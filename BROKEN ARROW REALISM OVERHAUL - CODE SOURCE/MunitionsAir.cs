// RealismOverhaul - infinite ammo for the local player's planes and helicopters (part of the "munitions illimitées" cheat).
//  Cheats writes ammo + the infinity flag every 2 s for every own unit, but aircraft still ran dry (separate strafe / bomb
//  bookkeeping). This module adds an air fast path, driven every frame by Cheats (AirAmmo.Frame(AmmoOn)):
//   - every 2 s: list of the local player's own alive planes / helicopters (BattleSystemHelpers.GetUnitType mask & (8|16));
//   - every 0.5 s: for each of them, one single-uid GameplayBus.GetUnitsAmmo read + LuaUnit.GetAmmoPercentage; the units whose
//     read shows InfiniteAmmo=false, MainWeaponEmpty=true or a lower percentage than after the last refill get one batched
//     GameplayBus.SetUnitsAmmo(100 %, filter Any, infinity=true), followed by one read per written uid;
//   - every 10 s, and only when something changed: diagnostic lines (own units by type, per aircraft percentages and bus read).
//  Only units owned by CurrentPlayer.UID are ever read or written, nothing is moved, no Harmony patch (PlaneAutoEvacSystem and
//  BackToBaseCommand stay untouched). Main thread only. 20 errors in a battle switch it off until the next mission.
//  Log lines start with [TRICHE] avions.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Data;
using Il2CppBrokenArrow.Shared.Ecs;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class AirAmmo
    {
        const float WriteEvery = 0.5f, ListEvery = 2f, DiagEvery = 10f, WarnEvery = 10f;
        const int MaxErrors = 20;
        const int MaskGround = 4, MaskHeli = 8, MaskPlane = 16;   // UnitType bits (S400Mode / Assistants use 8 | 16 for air)
        const int MaxDiagLines = 12;
        const float Full = 100f;

        /// One own aircraft, kept between two list refreshes (the LuaUnit is re-checked alive + owned on every pass).
        sealed class AirState
        {
            public LuaUnit Unit;
            public int Uid, EntityId, Mask;
            public string Name = "?";
            public int Pct = -1, PctSmoke = -1;      // last GetAmmoPercentage(false,false) / (false,true)
            public int FullPct = 100;                // highest percentage seen after a refill (some loadouts never show 100)
            public bool HasRead;                     // last single-uid bus read succeeded
            public NodeGetAmmoData Read;             // last single-uid bus read
            public bool HasAfter;                    // read right after the last write succeeded
            public NodeGetAmmoData After;            // read right after the last write
            public int PctAfter = -1;
            public bool PendingFull;                 // a write happened: next pass records the new "full" percentage
            public bool FirstWriteLogged;
            public int Writes;
            public bool Seen;                        // still present at the last list refresh
        }

        static readonly Dictionary<int, AirState> _air = new();     // by uid
        static readonly Dictionary<string, float> _warnNext = new();
        static readonly List<int> _one = new(1), _toWrite = new();
        static LuaMap _map;
        static float _nextWrite, _nextList, _nextDiag;
        static int _local = -1, _errors;
        static bool _disabled, _wasOn, _typeHelperBroken, _readUnavailableLogged;
        static int _ownTotal, _ownGround, _ownHeli, _ownPlane, _ownOther;
        static int _writeCalls, _unitsWritten, _writeCallsSinceDiag, _unitsWrittenSinceDiag, _stillBadAfterWrite;
        static string _lastDiagSig;
        static readonly Dictionary<int, int> _maskByUnitId = new();      // DB fallback cache (UnitID -> UnitType mask)

        static void Info(string s) { try { Mod.Log.Msg("[TRICHE] avions : " + s); } catch { } }

        /// Per-key rate-limited warning; every call counts toward the per-battle error budget.
        static void Fail(string what, Exception e)
        {
            if (_disabled) return;
            _errors++;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_warnNext.TryGetValue(what, out var next) || now >= next)
            {
                _warnNext[what] = now + WarnEvery;
                try { Mod.Log.Warning($"[TRICHE] avions : {what} impossible ({_errors}/{MaxErrors}) : {e?.GetBaseException().Message}"); } catch { }
            }
            if (_errors >= MaxErrors)
            {
                _disabled = true;
                try { Mod.Log.Warning($"[TRICHE] avions : {MaxErrors} erreurs dans cette bataille, recharge des avions et hélicos coupée jusqu'à la prochaine mission"); } catch { }
            }
        }

        /// New battle (called from the per-session reset): everything per-battle is forgotten and the error budget re-armed.
        internal static void ResetSession()
        {
            try
            {
                if (_writeCalls > 0) Info($"bilan : {_writeCalls} recharge(s), {_unitsWritten} écriture(s) d'appareil, {_errors} erreur(s)");
            }
            catch { }
            _air.Clear();
            _warnNext.Clear();
            _one.Clear();
            _toWrite.Clear();
            _maskByUnitId.Clear();
            _map = null;
            _nextWrite = _nextList = _nextDiag = 0f;
            _local = -1;
            _errors = 0;
            _disabled = false;
            _wasOn = false;
            _typeHelperBroken = false;
            _readUnavailableLogged = false;
            _ownTotal = _ownGround = _ownHeli = _ownPlane = _ownOther = 0;
            _writeCalls = _unitsWritten = _writeCallsSinceDiag = _unitsWrittenSinceDiag = _stillBadAfterWrite = 0;
            _lastDiagSig = null;
        }

        /// Every frame (from Cheats). Does nothing unless the ammo cheat is on and cheats are allowed (solo campaign mission).
        internal static void Frame(bool ammoOn)
        {
            if (!ammoOn) { _wasOn = false; return; }
            if (_disabled) return;
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
            if (!_wasOn)
            {
                // switched on (or first frame of the battle with it on): fresh list, write pass and diagnostic right away
                _wasOn = true;
                _nextWrite = _nextList = _nextDiag = 0f;
                _lastDiagSig = null;
            }
            if (now < _nextWrite) return;
            _nextWrite = now + WriteEvery;

            if (!Cheats.Allowed(out _)) return;

            GameController gc;
            int local;
            try
            {
                gc = GameController._instance;
                var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
                if (cp == null) return;
                local = cp.UID;
            }
            catch (Exception e) { Fail("lecture de la partie", e); return; }

            if (local != _local)
            {
                _local = local;
                _air.Clear();
                _nextList = 0f;
                _lastDiagSig = null;
            }

            if (now >= _nextList)
            {
                _nextList = now + ListEvery;
                if (!RefreshList(local)) return;
            }
            if (_disabled) return;

            Refill(gc, local);
            if (_disabled) return;

            if (now >= _nextDiag)
            {
                _nextDiag = now + DiagEvery;
                Diagnostic();
            }
        }

        // ------------------------------------------------------------ own aircraft list (every 2 s)

        static bool RefreshList(int local)
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LuaUnit> units;
            try
            {
                _map ??= new LuaMap();
                units = _map.GetUnits(V3.zero, 1_000_000f, -1, local);
            }
            catch (Exception e) { Fail("liste des unités", e); return false; }

            int total = 0, ground = 0, heli = 0, plane = 0, other = 0;
            foreach (var st in _air.Values) st.Seen = false;
            int n = 0;
            try { n = units?.Length ?? 0; } catch (Exception e) { Fail("liste des unités", e); return false; }

            for (int i = 0; i < n; i++)
            {
                if (_disabled) return false;
                LuaUnit u;
                int uid, eid, mask;
                try
                {
                    u = units[i];
                    if (u == null || !u.IsAlive() || u.GetOwnerPlayerUID() != local) continue;
                    uid = u.UID;
                    eid = u.Entity.EntityId;
                }
                catch (Exception e) { Fail("lecture d'une unité", e); continue; }

                mask = TypeMask(u);
                total++;
                if (mask < 0) { other++; continue; }
                if ((mask & MaskGround) != 0) ground++;
                if ((mask & MaskHeli) != 0) heli++;
                if ((mask & MaskPlane) != 0) plane++;
                if ((mask & (MaskGround | MaskHeli | MaskPlane)) == 0) other++;
                if ((mask & (MaskHeli | MaskPlane)) == 0) continue;

                if (!_air.TryGetValue(uid, out var st) || st.EntityId != eid)
                {
                    // new aircraft, or the uid now points to another entity: start from a clean state
                    st = new AirState { Uid = uid, EntityId = eid };
                    try { st.Name = u.Name ?? "?"; } catch { st.Name = "?"; }
                    _air[uid] = st;
                }
                st.Unit = u;
                st.Mask = mask;
                st.Seen = true;
            }

            foreach (var gone in _air.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList()) _air.Remove(gone);
            _ownTotal = total; _ownGround = ground; _ownHeli = heli; _ownPlane = plane; _ownOther = other;
            return true;
        }

        /// UnitType bits of a unit: BattleSystemHelpers.GetUnitType on the entity, database row type as a fallback. -1 = unknown.
        static int TypeMask(LuaUnit u)
        {
            if (!_typeHelperBroken)
            {
                try { return Convert.ToInt32(BSH.GetUnitType(u.Entity)); }
                catch (Exception e)
                {
                    _typeHelperBroken = true;
                    try { Mod.Log.Warning("[TRICHE] avions : type d'unité illisible par le moteur, lecture dans la base de données à la place : " + e.GetBaseException().Message); } catch { }
                }
            }
            try
            {
                int id = u.SpawnData?.Unit?.UnitID ?? 0;
                if (id <= 0) return -1;
                if (_maskByUnitId.TryGetValue(id, out var cached)) return cached;
                var src = DataBaseService._instance?.RawAccess;
                if (src == null) return -1;
                int mask = src.Units.TryGetById(id, out var row) && row != null ? (int)row.Type : -1;
                _maskByUnitId[id] = mask;
                return mask;
            }
            catch (Exception e) { Fail("type d'unité", e); return -1; }
        }

        // ------------------------------------------------------------ refill pass (every 0.5 s)

        static void Refill(GameController gc, int local)
        {
            if (_air.Count == 0) return;
            _toWrite.Clear();

            foreach (var st in _air.Values)
            {
                if (_disabled) return;
                if (!st.Seen || st.Unit == null) continue;
                try
                {
                    // ownership and life re-checked on every pass: never touch a unit that is not the local player's
                    if (!st.Unit.IsAlive() || st.Unit.GetOwnerPlayerUID() != local) { st.Seen = false; continue; }
                    st.Pct = st.Unit.GetAmmoPercentage(false, false);
                    st.PctSmoke = st.Unit.GetAmmoPercentage(false, true);
                }
                catch (Exception e) { st.Seen = false; Fail("lecture des munitions d'un appareil", e); continue; }

                st.HasRead = TryRead(gc, st.Uid, out st.Read);

                if (st.PendingFull)
                {
                    // first pass after a write: the percentage now shown is what "full" means for this loadout
                    st.PendingFull = false;
                    st.FullPct = Math.Max(1, Math.Min(100, st.Pct));
                }
                else if (st.Pct > st.FullPct) st.FullPct = Math.Min(100, st.Pct);

                bool need = st.Writes == 0                                  // never refilled by this module yet
                            || st.Pct < st.FullPct                          // it has fired since the last refill
                            || (st.HasRead && (!st.Read.InfiniteAmmo || st.Read.MainWeaponEmpty));
                if (need) _toWrite.Add(st.Uid);
            }

            if (_toWrite.Count == 0 || _disabled) return;
            if (!Write(gc, _toWrite)) return;

            _writeCalls++;
            _writeCallsSinceDiag++;
            _unitsWritten += _toWrite.Count;
            _unitsWrittenSinceDiag += _toWrite.Count;

            foreach (int uid in _toWrite)
            {
                if (_disabled) return;
                if (!_air.TryGetValue(uid, out var st)) continue;
                st.Writes++;
                st.PendingFull = true;
                st.HasAfter = TryRead(gc, uid, out st.After);
                try { st.PctAfter = st.Unit.GetAmmoPercentage(false, false); }
                catch (Exception e) { st.PctAfter = -1; Fail("lecture après recharge", e); }
                if (st.HasAfter && (!st.After.InfiniteAmmo || st.After.MainWeaponEmpty)) _stillBadAfterWrite++;
                if (!st.FirstWriteLogged)
                {
                    st.FirstWriteLogged = true;
                    Info($"première recharge de {st.Name} (uid {st.Uid}, entité {st.EntityId}, type {st.Mask}) : " +
                         $"avant {st.Pct}% ({Describe(st.HasRead, st.Read)}) ; après {st.PctAfter}% ({Describe(st.HasAfter, st.After)})");
                }
            }
        }

        /// Single-uid GameplayBus.GetUnitsAmmo read (filter Any). False when the bus / delegate is missing or the call fails.
        static bool TryRead(GameController gc, int uid, out NodeGetAmmoData data)
        {
            data = default;
            try
            {
                var del = gc?._GetEcsEventBus_k__BackingField?.Gameplay?.GetUnitsAmmo;
                if (del == null)
                {
                    if (!_readUnavailableLogged) { _readUnavailableLogged = true; Info("lecture des munitions indisponible (bus du jeu absent) : recharge au pourcentage seulement"); }
                    return false;
                }
                _one.Clear();
                _one.Add(uid);
                data = del.Invoke(Cheats.ToIl2Cpp(_one), AnyFilter());
                return true;
            }
            catch (Exception e) { Fail("lecture des munitions (bus)", e); return false; }
        }

        /// One batched GameplayBus.SetUnitsAmmo(100 %, filter Any, infinity=true) on the given own aircraft.
        static bool Write(GameController gc, List<int> uids)
        {
            try
            {
                var del = gc?._GetEcsEventBus_k__BackingField?.Gameplay?.SetUnitsAmmo;
                if (del == null) { Fail("recharge", new InvalidOperationException("bus du jeu absent")); return false; }
                del.Invoke(Cheats.ToIl2Cpp(uids), Full, AnyFilter(), true);
                return true;
            }
            catch (Exception e) { Fail("recharge", e); return false; }
        }

        static AmmoFilterData AnyFilter()
        {
            var filter = new AmmoFilterData();
            filter.Type = NodeAmmoType.Any;
            filter.SpecificAmmoNameFilter = "";
            return filter;
        }

        static string Describe(bool ok, NodeGetAmmoData d) =>
            ok ? $"nombre {d.AmmoCount}, coût {d.AmmoCostPercents}%, infini={(d.InfiniteAmmo ? "oui" : "non")}, arme principale vide={(d.MainWeaponEmpty ? "oui" : "non")}"
               : "lecture jeu impossible";

        // ------------------------------------------------------------ diagnostic (every 10 s, only on change)

        static void Diagnostic()
        {
            try
            {
                var body = new StringBuilder();
                body.Append($"tes unités : {_ownTotal} (sol {_ownGround}, hélicos {_ownHeli}, avions {_ownPlane}, autres {_ownOther})");
                var list = _air.Values.Where(s => s.Seen).OrderBy(s => s.Uid).ToList();
                int shown = 0;
                foreach (var st in list)
                {
                    if (shown >= MaxDiagLines) break;
                    shown++;
                    body.Append($"\n  {st.Name} (uid {st.Uid}, entité {st.EntityId}, type {st.Mask}) : munitions {st.Pct}% (avec fumigènes {st.PctSmoke}%), " +
                                $"plein vu {st.FullPct}%, jeu : {Describe(st.HasRead, st.Read)}");
                    if (st.Writes > 0) body.Append($" ; après dernière recharge : {st.PctAfter}%, {Describe(st.HasAfter, st.After)}");
                }
                if (list.Count > shown) body.Append($"\n  ... et {list.Count - shown} autre(s) appareil(s)");

                string sig = body.ToString();
                if (sig == _lastDiagSig) { _writeCallsSinceDiag = _unitsWrittenSinceDiag = 0; return; }
                _lastDiagSig = sig;
                Info($"relevé : {_writeCallsSinceDiag} recharge(s) ({_unitsWrittenSinceDiag} écriture(s) d'appareil) depuis le dernier relevé, " +
                     $"{_stillBadAfterWrite} lecture(s) encore vide/non infinie juste après recharge, erreurs {_errors}/{MaxErrors} ; " + sig);
                _writeCallsSinceDiag = _unitsWrittenSinceDiag = 0;
            }
            catch (Exception e) { Fail("relevé", e); }
        }
    }
}
