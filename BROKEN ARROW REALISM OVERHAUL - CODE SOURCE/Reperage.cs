// RealismOverhaul - realistic air defence reaction (values of 2026-09-16), both sides alike:
//  an aircraft or helicopter must have been spotted by the shooter's side for a while before an air-defence unit takes it as a
//  target: long-range SAM 8 s, Tor 7 s, short-range infrared SAM vehicles 6 s (Pantsir, NASAMS, SLAMRAAM, Tunguska, Osa, guns and
//  infantry MANPADS keep their own vanilla/real aim times). Missiles and other projectiles are never delayed (anti-missile
//  interception stays immediate); an attack order given on a target (priority target) always passes.
//  Same engine hook as the S-400 mode (TargetSearchHelper.TryAddTargetToCheckList), own lazy Harmony patch, solo campaign only,
//  crash guard, error kill-switch. When a side's visibility cannot be read, that side is not delayed (vanilla). Logs: [REPERAGE].
//  v0.22.7 (design A4, MEASUREMENT ONLY): the hook is also installed for anti-air observation, even when no delayed shooter exists
//  (before, Frame returned first and the detour never ran). Observed shooters, both sides: roles 15/16/34 and ground units whose
//  database loadout holds an Aircraft/Projectile ammunition (or ammo 491/201). For their helicopter candidates the prefix counts
//  role, 1 km distance band, thread, entity match and VueAA's line-of-sight verdict; the postfix (main thread only) compares the
//  engine's target list size before/after the call to count the candidates the engine really added. Nothing is refused because of
//  line of sight in this version. The unit scan is shared with VueAA (Aa snapshot). Observation logs: [VUE-AA].
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Search = Il2CppBrokenArrow.Client.Ecs.BattleSystem.TargetSearchHelper;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using AmmoTarget = Il2CppBrokenArrow.DataBase.Enums.TargetType;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    /// One unit of the anti-air snapshot (immutable value).
    internal readonly struct AaUnit
    {
        internal readonly int EntityId, Side, Role;                 // Role: 0 = role 15, 1 = role 16, 2 = role 34, 3 = other AA loadout, -1 = helicopter
        internal readonly V3 Pos;
        internal readonly bool Infantry;
        internal AaUnit(int entityId, int side, int role, V3 pos, bool infantry) { EntityId = entityId; Side = side; Role = role; Pos = pos; Infantry = infantry; }
    }

    /// Observed anti-air shooters and helicopters of both sides at one moment (built on the main thread, never modified afterwards).
    internal sealed class AaSnapshot
    {
        internal readonly AaUnit[] Shooters, Helis;
        internal readonly float Time;                                // realtimeSinceStartup of the scan
        internal AaSnapshot(AaUnit[] shooters, AaUnit[] helis, float time) { Shooters = shooters; Helis = helis; Time = time; }
    }

    static class Reperage
    {
        const string GuardVersion = "0.24.0";
        const long GraceMs = 1500;                                  // a target briefly lost keeps its tracking time
        const int BandN = 12, BandFar = 10, BandUnknown = 11;       // distance bands: 0..9 km, 10 km and more, helicopter position unknown
        const long StateObserved = 1L << 40, StateDelayed = 1L << 41;
        static readonly string[] RoleNames = { "rôle 15", "rôle 16", "rôle 34", "autres" };
        static readonly string[] VerdictNames = { "vue dégagée", "vue masquée", "non évaluée" };

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<float> _delayLong, _delayTor, _delayIr;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        static HarmonyLib.Harmony _harmony;
        static bool _patched, _refused, _everOnline, _sessionArmed, _netLogged, _visLogged0, _visLogged1;
        static float _next, _nextReport;
        static LuaMap _map;

        /// Latest anti-air snapshot (both sides), rebuilt every 0.5 s while the module runs; null outside a solo battle. Read by VueAA.
        internal static volatile AaSnapshot Aa;

        // hook side: immutable snapshots replaced as a whole
        static volatile bool _armed, _obsArmed;
        static volatile Dictionary<int, (int side, int delayMs)> _shooters = new();
        static volatile Dictionary<int, long> _seen0, _seen1;       // target EntityId -> tick since which it is continuously spotted by side 0 / 1 (null = unreadable)
        static long _calls, _delayed, _passed, _errors;
        static long _lastCalls, _lastDelayed, _lastPassed;

        // observation side (A4): immutable snapshots, counters only
        static volatile Dictionary<int, byte> _observers = new();   // shooter EntityId -> role index
        static volatile HashSet<int> _allUnits = new();             // every live LuaUnit entity, both sides
        static volatile Dictionary<int, V3> _heliPos = new();       // helicopter EntityId -> position at the last scan
        static volatile int _sizeOff = -1;                          // offset of List._size in the engine's target list
        static long _listPtr;                                       // native pointer of TargetSearchHelper._targetsList (0 = unreadable)
        static object _listHold;                                    // keeps the list wrapper (and so the native list) alive
        static volatile int _mainThread;
        static long _obsCalls, _obsCallsOffMain, _obsCallsKnown, _obsCand, _obsOffMain, _obsPriority, _obsTargetKnown, _obsDelayed, _obsNoCount, _obsErrors;
        static readonly long[] _obsRole = new long[4], _obsBand = new long[BandN], _obsVerdict = new long[3];
        static readonly long[] _added = new long[3 * BandN], _refusedByEngine = new long[3 * BandN];   // [verdict * BandN + band]
        static string _lastObsReport;
        static int _lastObservers, _lastHelis;
        static bool _listLogged, _listBadLogged;

        // main thread tracking
        static readonly Dictionary<int, long>[] _since = { new(), new() };
        static readonly Dictionary<int, long>[] _lastSeen = { new(), new() };
        static readonly Dictionary<string, int> _delayByName = new();
        static readonly Regex TorName = new Regex(@"\bTOR\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // unit classification caches (main thread)
        static readonly Dictionary<int, (int type, int role)> _infoByUid = new();
        static readonly Dictionary<int, int> _typeByUnitId = new();
        static readonly Dictionary<int, bool> _aaByUnitId = new(), _aaAmmo = new();
        static readonly HashSet<string> _aaNamesLogged = new();
        static Dictionary<int, List<int>> _ammoByUnit;
        static IntPtr _dbSrc;

        static void Log(string s) => Mod.Log.Msg("[REPERAGE] " + s);
        static void LogAa(string s) => Mod.Log.Msg("[VUE-AA] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Reperage");
            _enabled = c.CreateEntry("DefenseAerienneRealiste", true, description: Build.Desc("Défense aérienne réaliste : un avion ou un hélico doit être repéré un moment avant le premier tir (les missiles sont toujours interceptés tout de suite), pour les deux camps"));
            _delayLong = c.CreateEntry("DelaiLonguePortee", 8f, description: Build.Desc("Secondes de repérage avant le premier tir des défenses longue portée (Patriot, S-400, S-300, Buk, IRIS-T)"));
            _delayTor = c.CreateEntry("DelaiTor", 7f, description: Build.Desc("Secondes de repérage avant le premier tir du Tor"));
            _delayIr = c.CreateEntry("DelaiInfrarouge", 6f, description: Build.Desc("Secondes de repérage avant le premier tir des véhicules à missiles infrarouges (Avenger, M-SHORAD...)"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false; _visLogged0 = _visLogged1 = false;
            _delayByName.Clear();
            _infoByUid.Clear();
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        static void EndBattle(string why)
        {
            _armed = false;
            _obsArmed = false;
            _shooters = new Dictionary<int, (int, int)>();
            _seen0 = null; _seen1 = null;
            _observers = new Dictionary<int, byte>(); _allUnits = new HashSet<int>(); _heliPos = new Dictionary<int, V3>();
            Aa = null;
            Interlocked.Exchange(ref _listPtr, 0); _listHold = null;
            _since[0].Clear(); _since[1].Clear(); _lastSeen[0].Clear(); _lastSeen[1].Clear();
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
            Report(true);
            Log($"fin de bataille ({why})");
        }

        static int _wait;

        /// Every frame in campaign; works every 0.5 s.
        internal static void Frame()
        {
            if (_enabled == null || _refused) { if (_armed) _armed = false; if (_obsArmed) _obsArmed = false; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            // one heavy module job per frame (Planif.cs): the scan keeps its 0.5 s period, it just avoids the frames of the others
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + 0.5f;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo())
            {
                if (_armed || _obsArmed) { _armed = false; _obsArmed = false; Log("partie en ligne : délai de repérage et observation anti-aérienne coupés"); }
                Aa = null;
                return;
            }
            _mainThread = Environment.CurrentManagedThreadId;

            long tick = Environment.TickCount64;
            var scan = ScanUnits(_enabled.Value, now);                     // the delay pref only switches the delay; observation always runs
            Aa = scan.Aa;
            var shooters = scan.Shooters;
            var uidToEntity = scan.UidToEntity;
            if (shooters.Count == 0 && scan.Observers.Count == 0) { _armed = false; _obsArmed = false; return; }

            if (shooters.Count > 0)
            {
                // spotted aircraft and helicopters of the other side, per viewing side
                var seen = new Dictionary<int, long>[2];
                for (int side = 0; side < 2; side++)
                {
                    try
                    {
                        var vis = Visibility.VisibleEnemies(gc, side, 1 - side);
                        if (vis == null || !vis.Usable)
                        {
                            seen[side] = null;
                            bool logged = side == 0 ? _visLogged0 : _visLogged1;
                            if (!logged) { if (side == 0) _visLogged0 = true; else _visLogged1 = true; Log($"camp {side} : visibilité illisible, pas de délai pour ce camp (comme le jeu)"); }
                            continue;
                        }
                        var visible = new HashSet<int>();
                        foreach (var e in vis.Units)
                            if (e.role < 0 && uidToEntity[1 - side].TryGetValue(e.uid, out var eid)) visible.Add(eid);
                        var since = _since[side]; var last = _lastSeen[side];
                        foreach (var eid in visible) { if (!since.ContainsKey(eid)) since[eid] = tick; last[eid] = tick; }
                        foreach (var eid in new List<int>(since.Keys))
                            if (!visible.Contains(eid) && tick - last.GetValueOrDefault(eid) > GraceMs) { since.Remove(eid); last.Remove(eid); }
                        seen[side] = new Dictionary<int, long>(since);
                    }
                    catch (Exception e) { seen[side] = null; Warn("vis" + side, $"camp {side} : visibilité illisible ({e.Message})"); }
                }
                _seen0 = seen[0];
                _seen1 = seen[1];
            }
            _shooters = shooters;
            _observers = scan.Observers;
            _allUnits = scan.AllUnits;
            _heliPos = scan.HeliPos;
            _lastObservers = scan.Observers.Count; _lastHelis = scan.HeliPos.Count;

            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2)
                {
                    _refused = true;
                    _armed = false; _obsArmed = false;
                    Mod.Log.Warning("[REPERAGE] désactivé : les deux dernières parties ne se sont pas terminées normalement");
                    Mod.Notify(TxtKey.N_AA_REALISTIC_OFF);
                    return;
                }
                _sessionArmed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();
                Log($"défenses concernées : {shooters.Count} ; délais longue portée {_delayLong.Value:0.#} s, Tor {_delayTor.Value:0.#} s, infrarouge {_delayIr.Value:0.#} s" +
                    (_enabled.Value ? "" : " (délai coupé dans les réglages)") +
                    $" ; unités anti-aériennes observées : {scan.Observers.Count} (rôle 15 {scan.ByRole[0]}, rôle 16 {scan.ByRole[1]}, rôle 34 {scan.ByRole[2]}, autres {scan.ByRole[3]}), hélicoptères {scan.HeliPos.Count}");
            }
            // 0.22.8: the detour on TargetSearchHelper.TryAddTargetToCheckList is never installed. In the 0.22.7 test battle (the first one
            // where it was installed) no unit fired for 6 minutes, against 138 impacts in the first minute of the same mission without it.
            if (!TargetFilterAllowed)
            {
                if (!_filterOffLogged) { _filterOffLogged = true; Log("filtre de ciblage NON installé : il empêchait les unités de tirer (test 0.22.7) ; délai de repérage et observation anti-aérienne en pause"); }
                _armed = false; _obsArmed = false;
                return;
            }
            RefreshTargetsList();
            if (!_patched && !TryPatch()) return;
            _armed = Interlocked.Read(ref _errors) <= 50;
            _obsArmed = scan.Observers.Count > 0 && Interlocked.Read(ref _obsErrors) <= 50;
            if (now >= _nextReport) { _nextReport = now + 30f; Report(false); }
        }

        /// Scan for VueAA when this module does not publish (refused, not armed): same classification, no hook state touched. Main thread.
        internal static AaSnapshot ScanForVueAA(float now) => ScanUnits(false, now).Aa;

        sealed class ScanResult
        {
            internal readonly Dictionary<int, (int side, int delayMs)> Shooters = new();
            internal readonly Dictionary<int, int>[] UidToEntity = { new(), new() };
            internal readonly Dictionary<int, byte> Observers = new();
            internal readonly HashSet<int> AllUnits = new();
            internal readonly Dictionary<int, V3> HeliPos = new();
            internal readonly int[] ByRole = new int[4];
            internal AaSnapshot Aa;
        }

        static ScanResult ScanUnits(bool delays, float now)
        {
            var r = new ScanResult();
            var aaShooters = new List<AaUnit>();
            var helis = new List<AaUnit>();
            _map ??= new LuaMap();
            for (int side = 0; side < 2; side++)
            {
                var units = _map.GetUnits(V3.zero, 1_000_000f, side, -1);
                for (int i = 0; i < (units?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = units[i];
                        if (u == null || !u.IsAlive()) continue;
                        int eid = u.Entity.EntityId;
                        r.UidToEntity[side][u.UID] = eid;
                        if (delays) { int d = DelayMs(u); if (d > 0) r.Shooters[eid] = (side, d); }
                        r.AllUnits.Add(eid);
                        var info = InfoOf(u);
                        if ((info.type & 8) != 0)
                        {
                            var p = u.GetPosition();
                            r.HeliPos[eid] = p;
                            helis.Add(new AaUnit(eid, side, -1, p, false));
                        }
                        else if (info.role >= 0)
                        {
                            var p = u.GetPosition();
                            r.Observers[eid] = (byte)info.role;
                            r.ByRole[info.role]++;
                            aaShooters.Add(new AaUnit(eid, side, info.role, p, (info.type & 2) != 0));
                        }
                    }
                    catch { }
                }
            }
            r.Aa = new AaSnapshot(aaShooters.ToArray(), helis.ToArray(), now);
            return r;
        }

        /// Units.Type bits (2 infantry, 4 vehicle, 8 helicopter, 16 plane) and anti-air role index (-1 = not observed), cached per UID.
        static (int type, int role) InfoOf(LuaUnit u)
        {
            int uid = u.UID;
            if (_infoByUid.TryGetValue(uid, out var cached)) return cached;
            int role = u.UnitRole;
            int unitId = 0;
            try { unitId = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            int type = TypeOfUnitId(unitId);
            if (type == 0) { try { type = (int)BSH.GetUnitType(u.Entity); } catch { type = 0; } }
            int idx = -1;
            if ((type & (8 | 16)) == 0)
            {
                if (role == 15) idx = 0;
                else if (role == 16) idx = 1;
                else if (role == 34) idx = 2;
                else if ((type & (2 | 4)) != 0 && HasAaAmmo(unitId))
                {
                    idx = 3;
                    string name = "?";
                    try { name = u.Name ?? "?"; } catch { }
                    if (_aaNamesLogged.Count < 40 && _aaNamesLogged.Add(name))
                        LogAa($"{name} (rôle {role}) : munition anti-aérienne dans sa dotation, observé comme défense anti-aérienne");
                }
            }
            var res = (type, idx);
            if (_infoByUid.Count < 20000) _infoByUid[uid] = res;
            return res;
        }

        static void CheckDbSource(Il2CppBrokenArrow.DataBase.DataBaseSourceData src)
        {
            if (src.Pointer == _dbSrc) return;
            _dbSrc = src.Pointer;
            _typeByUnitId.Clear(); _aaByUnitId.Clear(); _aaAmmo.Clear(); _ammoByUnit = null; _infoByUid.Clear();
        }

        static int TypeOfUnitId(int unitId)
        {
            if (unitId <= 0) return 0;
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return 0;
            CheckDbSource(src);
            if (_typeByUnitId.TryGetValue(unitId, out int t)) return t;
            t = 0;
            try { if (src.Units.TryGetById(unitId, out UnitsRow row) && row != null) t = (int)row.Type; } catch { t = 0; }
            _typeByUnitId[unitId] = t;
            return t;
        }

        /// True when the unit's database loadout (WeaponAmmunitions join) holds an Aircraft/Projectile ammunition, or ammo 491 / 201.
        static bool HasAaAmmo(int unitId)
        {
            if (unitId <= 0) return false;
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return false;
            CheckDbSource(src);
            if (_aaByUnitId.TryGetValue(unitId, out bool aa)) return aa;
            if (_ammoByUnit == null)
            {
                _ammoByUnit = new Dictionary<int, List<int>>();
                foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
                {
                    if (wa == null) continue;
                    if (!_ammoByUnit.TryGetValue(wa.UnitId, out var l)) _ammoByUnit[wa.UnitId] = l = new List<int>();
                    l.Add(wa.AmmunitionId);
                }
            }
            aa = false;
            if (_ammoByUnit.TryGetValue(unitId, out var ids))
                foreach (var aid in ids)
                    if (IsAaAmmo(src, aid)) { aa = true; break; }
            _aaByUnitId[unitId] = aa;
            return aa;
        }

        static bool IsAaAmmo(Il2CppBrokenArrow.DataBase.DataBaseSourceData src, int ammoId)
        {
            if (ammoId == 491 || ammoId == 201) return true;
            if (_aaAmmo.TryGetValue(ammoId, out bool v)) return v;
            v = false;
            try
            {
                if (src.Ammunitions.TryGetById(ammoId, out var a) && a != null)
                    v = ((long)a.TargetType & ((long)AmmoTarget.Aircraft | (long)AmmoTarget.Projectile)) != 0;
            }
            catch { v = false; }
            _aaAmmo[ammoId] = v;
            return v;
        }

        static readonly Dictionary<string, float> _warnNext = new();
        static void Warn(string key, string msg)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_warnNext.TryGetValue(key, out var t) && now < t) return;
            _warnNext[key] = now + 60f;
            Log(msg);
        }

        /// Delay class of a unit (0 = no delay). Long-range SAM (role 15) and short-range SAM vehicles (role 16) only.
        static int DelayMs(LuaUnit u)
        {
            int r = u.UnitRole;
            if (r != 15 && r != 16) return 0;
            string name = u.Name ?? "";
            if (_delayByName.TryGetValue(name, out var cached)) return cached;
            string n = name.ToUpperInvariant();
            float s;
            if (n.Contains("PANTSIR") || n.Contains("NASAMS") || n.Contains("SLAMRAAM") || n.Contains("TUNGUSKA") || n.Contains("OSA")
                || n.Contains("SHILKA") || n.Contains("GEPARD") || n.Contains("C-RAM") || n.Contains("VULCAN") || n.Contains("PIVADS") || n.Contains("ZU-23"))
                s = 0f;                                                          // real aim times already in the data, or guns
            else if (TorName.IsMatch(n)) s = _delayTor.Value;
            else if (r == 15) s = _delayLong.Value;
            else s = _delayIr.Value;
            int ms = (int)Math.Round(Math.Clamp(s, 0f, 30f) * 1000f);
            _delayByName[name] = ms;
            if (ms > 0) Log($"{name} (rôle {r}) : {ms / 1000f:0.#} s de repérage avant le premier tir sur un appareil");
            return ms;
        }

        static bool Solo()
        {
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _everOnline = true;
                if (!_netLogged) { _netLogged = true; Log($"réseau : état={st} -> {(_everOnline ? "en ligne" : "solo")}"); }
            }
            catch { return false; }
            return !_everOnline;
        }

        internal const bool TargetFilterAllowed = false;           // see Frame: this engine hook broke target acquisition for every unit
        static bool _filterOffLogged;

        static bool TryPatch()
        {
            try
            {
                var target = AccessTools.Method(typeof(Search), "TryAddTargetToCheckList");
                if (target == null) { _refused = true; Log("filtre de ciblage introuvable dans cette version du jeu"); return false; }
                // the weapon position is only needed for distance bands: if the interop signature differs, observe without it
                bool posOk = false;
                foreach (var p in target.GetParameters())
                    if (p.Name == "weaponPosition" && p.ParameterType.IsByRef && p.ParameterType.GetElementType() == typeof(V3)) posOk = true;
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Reperage");
                _harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Reperage).GetMethod(posOk ? nameof(Prefix) : nameof(PrefixNoPosition), BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(Reperage).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
                Log($"filtre de ciblage installé : délai de repérage {(_enabled.Value ? "actif" : "coupé dans les réglages")}, observation anti-aérienne active (aucune cible refusée pour la vue)");
                if (!posOk) LogAa("position de l'arme absente de la signature du filtre : distance des candidats inconnue");
                return true;
            }
            catch (Exception e)
            {
                _refused = true;
                _armed = false; _obsArmed = false;
                Mod.Log.Warning("[REPERAGE] installation impossible : " + e.GetBaseException().Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- engine target list (read by the observation postfix)

        /// Main thread: holds TargetSearchHelper._targetsList and the offset of its _size field, checked once against Count.
        static void RefreshTargetsList()
        {
            try
            {
                var l = Search._targetsList;
                if (l == null) { Interlocked.Exchange(ref _listPtr, 0); _listHold = null; return; }
                IntPtr p = l.Pointer;
                if (_listHold != null && p.ToInt64() == Interlocked.Read(ref _listPtr)) return;
                int off = SizeOffset(p);
                int raw = off > 0 ? Marshal.ReadInt32(p, off) : -1, count = l.Count;
                if (off <= 0 || raw != count)
                {
                    Interlocked.Exchange(ref _listPtr, 0); _listHold = null;
                    if (!_listBadLogged) { _listBadLogged = true; LogAa($"liste des cibles du jeu illisible (décalage {off}, lu {raw}, compte {count}) : les candidats ajoutés ne seront pas comptés"); }
                    return;
                }
                _listHold = l;
                _sizeOff = off;
                Interlocked.Exchange(ref _listPtr, p.ToInt64());
                if (!_listLogged) { _listLogged = true; LogAa($"liste des cibles du jeu lue (taille au décalage {off}, {count} élément(s) maintenant)"); }
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _listPtr, 0); _listHold = null;
                Warn("liste", "liste des cibles du jeu illisible : " + e.GetBaseException().Message);
            }
        }

        static int SizeOffset(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return -1;
            IntPtr field = IntPtr.Zero;
            int guard = 0;
            for (IntPtr c = IL2CPP.il2cpp_object_get_class(obj); c != IntPtr.Zero && field == IntPtr.Zero && guard < 8; c = IL2CPP.il2cpp_class_get_parent(c), guard++)
                field = IL2CPP.il2cpp_class_get_field_from_name(c, "_size");
            if (field == IntPtr.Zero) return -1;
            long off = Convert.ToInt64(IL2CPP.il2cpp_field_get_offset(field));
            return off >= 2 * IntPtr.Size && off <= 256 ? (int)off : -1;
        }

        /// Hot path: current size of the engine's target list by a plain memory read (no interop call, no allocation); -1 = unreadable.
        static int TargetsCount()
        {
            long p = Interlocked.Read(ref _listPtr);
            int off = _sizeOff;
            if (p == 0 || off <= 0) return -1;
            return Marshal.ReadInt32(new IntPtr(p), off);
        }

        // ---------------------------------------------------------------- hooks

        /// Hot path (every weapon, every candidate, maybe worker threads): plain reads of immutable snapshots only.
        /// Observation first (never changes the result), then the delay exactly as before.
        static bool Prefix(ref EcsEntity unitEntity, ref EcsEntity weaponEntity, ref EcsEntity checkTargetEntity, bool isTargetPriority, ref V3 weaponPosition, int weaponTargetMask, out long __state)
        {
            if (Campaign.MissionInerte) { __state = 0; return true; }             // mission without the mod: the original runs untouched
            __state = _obsArmed ? Observe(ref unitEntity, ref checkTargetEntity, isTargetPriority, ref weaponPosition, true) : 0;
            return DelayCore(ref unitEntity, ref checkTargetEntity, isTargetPriority, ref __state);
        }

        /// Same hook when the interop signature exposes no usable weapon position (distance band "unknown").
        static bool PrefixNoPosition(ref EcsEntity unitEntity, ref EcsEntity weaponEntity, ref EcsEntity checkTargetEntity, bool isTargetPriority, int weaponTargetMask, out long __state)
        {
            if (Campaign.MissionInerte) { __state = 0; return true; }
            V3 none = default;
            __state = _obsArmed ? Observe(ref unitEntity, ref checkTargetEntity, isTargetPriority, ref none, false) : 0;
            return DelayCore(ref unitEntity, ref checkTargetEntity, isTargetPriority, ref __state);
        }

        /// The realistic reaction delay (unchanged rules). Marks the observation state when it refuses the candidate.
        static bool DelayCore(ref EcsEntity unitEntity, ref EcsEntity checkTargetEntity, bool isTargetPriority, ref long state)
        {
            if (!_armed) return true;
            try
            {
                Interlocked.Increment(ref _calls);
                if (isTargetPriority) return true;                               // the player's (or AI's) own attack order
                if (!_shooters.TryGetValue(unitEntity.EntityId, out var sh)) return true;
                int ut = Convert.ToInt32(BSH.GetUnitType(checkTargetEntity));
                if ((ut & (8 | 16)) == 0) return true;                           // not a helicopter (8) nor an aircraft (16)
                if (BSH.IsTargetProjectile(ref checkTargetEntity)) return true;  // missiles: intercepted at once
                var seen = sh.side == 0 ? _seen0 : _seen1;
                if (seen == null) return true;                                   // visibility unreadable for this side: vanilla
                if (seen.TryGetValue(checkTargetEntity.EntityId, out var since) && Environment.TickCount64 - since >= sh.delayMs)
                {
                    Interlocked.Increment(ref _passed);
                    return true;
                }
                Interlocked.Increment(ref _delayed);
                state |= StateDelayed;                                           // the observation postfix must not count this as the engine's choice
                return false;
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) > 50) _armed = false;     // repeated errors: back to vanilla
                return true;
            }
        }

        /// Counts a helicopter candidate of an observed anti-air shooter. Returns the postfix state (0 = nothing to compare).
        static long Observe(ref EcsEntity unitEntity, ref EcsEntity checkTargetEntity, bool isTargetPriority, ref V3 weaponPosition, bool hasPosition)
        {
            try
            {
                Interlocked.Increment(ref _obsCalls);
                bool main = Environment.CurrentManagedThreadId == _mainThread;
                if (!main) Interlocked.Increment(ref _obsCallsOffMain);
                int sid = unitEntity.EntityId;
                if (_allUnits.Contains(sid)) Interlocked.Increment(ref _obsCallsKnown);
                if (!_observers.TryGetValue(sid, out byte role)) return 0;
                int ut = (int)BSH.GetUnitType(checkTargetEntity);
                if ((ut & 8) == 0) return 0;                                     // helicopters only
                if (BSH.IsTargetProjectile(ref checkTargetEntity)) return 0;
                Interlocked.Increment(ref _obsCand);
                Interlocked.Increment(ref _obsRole[role & 3]);
                if (isTargetPriority) Interlocked.Increment(ref _obsPriority);
                int tid = checkTargetEntity.EntityId;
                int band = BandUnknown;
                if (_heliPos.TryGetValue(tid, out V3 hp))
                {
                    Interlocked.Increment(ref _obsTargetKnown);
                    if (hasPosition)
                    {
                        double dx = hp.x - weaponPosition.x, dy = hp.y - weaponPosition.y, dz = hp.z - weaponPosition.z;
                        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        band = d >= 10000.0 ? BandFar : (int)(d / 1000.0);
                    }
                }
                Interlocked.Increment(ref _obsBand[band]);
                long key = ((long)sid << 32) | (uint)tid;
                int verdict = VueAA.Masked.Contains(key) ? 1 : VueAA.Evaluated.Contains(key) ? 0 : 2;
                Interlocked.Increment(ref _obsVerdict[verdict]);
                if (!main) { Interlocked.Increment(ref _obsOffMain); return 0; }  // the list size is only compared on the main thread
                int before = TargetsCount();
                if (before < 0) { Interlocked.Increment(ref _obsNoCount); return 0; }
                return StateObserved | ((long)verdict << 36) | ((long)band << 32) | (uint)before;
            }
            catch
            {
                if (Interlocked.Increment(ref _obsErrors) > 50) _obsArmed = false;
                return 0;
            }
        }

        /// Main thread only (state is set only there): did the engine add the observed candidate to its target list?
        static void Postfix(long __state)
        {
            if ((__state & StateObserved) == 0) return;
            try
            {
                if ((__state & StateDelayed) != 0) { Interlocked.Increment(ref _obsDelayed); return; }
                int after = TargetsCount();
                if (after < 0) { Interlocked.Increment(ref _obsNoCount); return; }
                int band = (int)((__state >> 32) & 0xF), verdict = (int)((__state >> 36) & 0x3);
                if (band >= BandN || verdict > 2) return;
                int before = (int)(uint)__state;
                if (after > before) Interlocked.Increment(ref _added[verdict * BandN + band]);
                else Interlocked.Increment(ref _refusedByEngine[verdict * BandN + band]);
            }
            catch
            {
                if (Interlocked.Increment(ref _obsErrors) > 50) _obsArmed = false;
            }
        }

        // ---------------------------------------------------------------- reports

        static void Report(bool final)
        {
            ReportObservation(final);
            ReportDelay(final);
        }

        static void ReportDelay(bool final)
        {
            long calls = Interlocked.Read(ref _calls), delayed = Interlocked.Read(ref _delayed), passed = Interlocked.Read(ref _passed), errors = Interlocked.Read(ref _errors);
            if (calls == _lastCalls && delayed == _lastDelayed && passed == _lastPassed && !final) return;
            _lastCalls = calls; _lastDelayed = delayed; _lastPassed = passed;
            if (calls == 0 && !final) return;
            Log($"{(final ? "bilan" : "relevé")} : appels du filtre {calls}, cibles aériennes retenues (pas encore assez repérées) {delayed}, acceptées après le délai {passed}, erreurs {errors}" +
                $" ; appareils suivis camp 0 {_seen0?.Count.ToString() ?? "illisible"}, camp 1 {_seen1?.Count.ToString() ?? "illisible"}");
            if (!final) return;
            Interlocked.Exchange(ref _calls, 0); Interlocked.Exchange(ref _delayed, 0); Interlocked.Exchange(ref _passed, 0); Interlocked.Exchange(ref _errors, 0);
            _lastCalls = _lastDelayed = _lastPassed = 0;
        }

        static void ReportObservation(bool final)
        {
            long calls = Interlocked.Read(ref _obsCalls);
            if (calls == 0 && !final) return;
            long offMain = Interlocked.Read(ref _obsCallsOffMain), known = Interlocked.Read(ref _obsCallsKnown), cand = Interlocked.Read(ref _obsCand);
            var sb = new StringBuilder();
            sb.Append($"filtre du jeu {(final ? "bilan" : "relevé")} : appels {calls} (hors fil principal {offMain}, tireur reconnu parmi les unités {Pct(known, calls)}) ; ");
            sb.Append($"unités anti-aériennes observées {_lastObservers}, hélicoptères {_lastHelis} ; ");
            sb.Append($"candidats hélicoptères de ces unités {cand} (hors fil principal {Interlocked.Read(ref _obsOffMain)}, ordres d'attaque {Interlocked.Read(ref _obsPriority)}, hélico reconnu {Pct(Interlocked.Read(ref _obsTargetKnown), cand)})");
            if (cand > 0)
            {
                sb.Append(" ; par rôle :");
                for (int r = 0; r < 4; r++) sb.Append($" {RoleNames[r]} {Interlocked.Read(ref _obsRole[r])}");
                sb.Append(" ; verdict de VueAA :");
                for (int v = 0; v < 3; v++) sb.Append($" {VerdictNames[v]} {Interlocked.Read(ref _obsVerdict[v])}");
                sb.Append($" ; retenus par le délai {Interlocked.Read(ref _obsDelayed)}, taille de liste illisible {Interlocked.Read(ref _obsNoCount)}");
                sb.Append(" ; par distance (candidats, ajoutés par le jeu dont vue masquée, écartés par le jeu dont vue masquée) :");
                for (int b = 0; b < BandN; b++)
                {
                    long n = Interlocked.Read(ref _obsBand[b]);
                    if (n == 0) continue;
                    long add = 0, rej = 0;
                    for (int v = 0; v < 3; v++) { add += Interlocked.Read(ref _added[v * BandN + b]); rej += Interlocked.Read(ref _refusedByEngine[v * BandN + b]); }
                    string label = b == BandUnknown ? "position inconnue" : b == BandFar ? "10 km et plus" : $"{b}-{b + 1} km";
                    sb.Append($" [{label} : {n}, ajoutés {add} dont masqués {Interlocked.Read(ref _added[BandN + b])}, écartés {rej} dont masqués {Interlocked.Read(ref _refusedByEngine[BandN + b])}]");
                }
                long maskedAdded = 0;
                for (int b = 2; b <= 4; b++) maskedAdded += Interlocked.Read(ref _added[BandN + b]);
                sb.Append($" ; vue masquée mais hélico ajouté par le jeu entre 2 et 5 km : {maskedAdded}");
            }
            sb.Append($" ; erreurs {Interlocked.Read(ref _obsErrors)}");
            string s = sb.ToString();
            if (!final && s == _lastObsReport) return;
            _lastObsReport = s;
            LogAa(s);
            if (!final) return;
            Interlocked.Exchange(ref _obsCalls, 0); Interlocked.Exchange(ref _obsCallsOffMain, 0); Interlocked.Exchange(ref _obsCallsKnown, 0); Interlocked.Exchange(ref _obsCand, 0);
            Interlocked.Exchange(ref _obsOffMain, 0); Interlocked.Exchange(ref _obsPriority, 0); Interlocked.Exchange(ref _obsTargetKnown, 0); Interlocked.Exchange(ref _obsDelayed, 0);
            Interlocked.Exchange(ref _obsNoCount, 0); Interlocked.Exchange(ref _obsErrors, 0);
            for (int i = 0; i < _obsRole.Length; i++) Interlocked.Exchange(ref _obsRole[i], 0);
            for (int i = 0; i < _obsBand.Length; i++) Interlocked.Exchange(ref _obsBand[i], 0);
            for (int i = 0; i < _obsVerdict.Length; i++) Interlocked.Exchange(ref _obsVerdict[i], 0);
            for (int i = 0; i < _added.Length; i++) { Interlocked.Exchange(ref _added[i], 0); Interlocked.Exchange(ref _refusedByEngine[i], 0); }
            _lastObsReport = null;
        }

        static string Pct(long part, long total) => total <= 0 ? "-" : $"{100.0 * part / total:0.#} %";
    }
}
