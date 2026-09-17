// CopiesUnites: the game copies weapons, ammunition, mobility, armour, sensors and abilities into every loaded unit
// (LoadUnits clones the table rows: Weapons.CloneWithAmmunitions, FullUnitClone...). Real stats written on the table rows
// must reach those copies too, or the arsenal card, the decks and the units spawned in battle keep the game's values.
//  - Plan: every table row property the realism journaled -> the same property on each copy with the same type and Id.
//  - Per-unit ammunition counts (Weapons.WeaponAmmunitions) follow the WeaponAmmunitions rows once their key is recognised
//    and the copy still holds the game's value (anything else is logged and left alone).
//  - Existing copies are updated right after the stats are applied; copies the game makes later (FullUnitClone, LoadUnit,
//    GetLoadedTurret) and the unit shown on the arsenal card are updated when they appear. Every write is journaled (restored),
//    and at restore the copies made in between are put back to the table values.
//  - v0.23.0: the walk also reaches the objects held by the unit's options (Modifications -> Options: turrets, sensors, mobility,
//    armour, abilities) and Units.Mobility/Armor, and passes the turret id down to the weapons. Per-unit data (Affuts.cs: manual aim
//    caps, per-unit sensors, heavy-weapon team speeds) is decided per copy: a survey pass records every owner key of every ammunition,
//    sensor and mobility object (before the "already seen" checks, so shared objects are known), then the sync pass writes the Id sync
//    value adjusted by that rule and enforces the rule right after it, in every walk path.
//  - Decoy abilities (the unit's own abilities and those of its options) are walked per unit too, survey included, so the helicopter
//    flare stock of Affuts.cs is decided per copy with the same sharing guards.
//  - v0.24.0: helicopter-only gun ammunition copies (PrecisionHeli.cs). The survey pass runs when Affuts or PrecisionHeli has a plan and
//    records the units reaching each weapon holding a listed row; in the sync pass PrecisionHeli.OnWeapon runs after the ammunition caps and
//    SyncCounts, so the real-stats quantity is written before 25 % of it moves to the copy. SyncCounts leaves alone a quantity whose copy
//    key exists (the split already used the real value). The restore walk removes any leftover copy from the loaded units.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.DataBase.Models;
using Il2CppBrokenArrow.Shared.Ecs;

namespace RealismOverhaul
{
    [HarmonyPatch(typeof(LoadUnits), nameof(LoadUnits.FullUnitClone))]
    static class Patch_CopiesCloneUnite
    {
        static void Postfix(LoadUnits __instance, Units __result) => UnitCopies.OnNewUnit(__instance, __result, "copie d'unité");
    }

    [HarmonyPatch(typeof(LoadUnits), nameof(LoadUnits.LoadUnit))]
    static class Patch_CopiesChargeUnite
    {
        static void Postfix(LoadUnits __instance, Units __result) => UnitCopies.OnNewUnit(__instance, __result, "chargement d'unité");
    }

    [HarmonyPatch(typeof(LoadUnits), nameof(LoadUnits.GetLoadedTurret))]
    static class Patch_CopiesTourelle
    {
        static void Postfix(LoadUnits __instance, Turrets __result, int unitId) => UnitCopies.OnNewTurret(__instance, __result, unitId);
    }

    static class UnitCopies
    {
        sealed class Entry { public object Row; public IntPtr RowPtr; public readonly List<PropertyInfo> Props = new(); }
        sealed class Load { public WeaponAmmunitions Row; public int RowId, AmmoId, Orig; }

        sealed class Plan
        {
            public IntPtr Src;
            public LoadUnits Loader;                                                     // held: its copies are put back at restore
            public readonly Dictionary<Type, Dictionary<int, Entry>> ByType = new();
            public readonly Dictionary<(int unit, int weapon), List<Load>> Loads = new();
        }

        sealed class Ctx
        {
            public Plan P;
            public bool Journal;
            public bool Survey;                                                          // record owner keys only (no write)
            public Affuts.Walk W;                                                        // per-unit data of this walk (null = none)
            public PrecisionHeli.Walk PW;                                                // helicopter-only copies of this walk (null = none)
            public readonly HashSet<IntPtr> Seen = new();
            public readonly HashSet<(IntPtr obj, int unit, int tag)> Keys = new();       // (object, unit, turret or kind) already walked
            public int Objects, Writes, Counts, Leftovers;
        }

        const int TagSensor = -2, TagOption = -3, TagTurret = -4, TagMobility = -5, TagAbility = -6;

        static readonly object _lock = new();
        static Plan _plan;
        static int _hookCalls, _hookWrites, _hookCounts, _errors, _detailLogs, _keyLogs, _mismatchLogs;
        static long _nextHookLog;
        static string _mode;

        static void Log(string s) => Mod.Log.Msg("[VRAIES STATS] " + s);

        // ------------------------------------------------------------ plan and first pass
        internal static void AfterApply(DataBaseService db, DataBaseSourceData src)
        {
            try
            {
                lock (_lock)
                {
                    var p = new Plan { Src = src.Pointer, Loader = db.UnitsLoader };
                    foreach (var (row, prop, orig) in Realism.JournalSnapshot())
                    {
                        if (row is not Il2CppObjectBase o || prop == null) continue;
                        var t = row.GetType();
                        if (t.Namespace != "Il2CppBrokenArrow.DataBase.Models") continue;
                        if (row is WeaponAmmunitions wa)
                        {
                            if (prop.Name != "Quantity") continue;
                            var key = (wa.UnitId, wa.WeaponId);
                            if (!p.Loads.TryGetValue(key, out var l)) p.Loads[key] = l = new List<Load>();
                            l.Add(new Load { Row = wa, RowId = wa.Id, AmmoId = wa.AmmunitionId, Orig = Convert.ToInt32(orig) });
                            continue;
                        }
                        var idp = Props.Get(t, "Id");
                        if (idp == null) continue;
                        int id = Convert.ToInt32(idp.GetValue(row));
                        if (!p.ByType.TryGetValue(t, out var byId)) p.ByType[t] = byId = new Dictionary<int, Entry>();
                        if (!byId.TryGetValue(id, out var e)) byId[id] = e = new Entry { Row = row, RowPtr = o.Pointer };
                        else if (e.RowPtr != o.Pointer) continue;                              // one object per id: the first journaled (the table row)
                        if (!e.Props.Contains(prop)) e.Props.Add(prop);
                    }
                    _plan = p;
                    var units = LoadedUnits(p.Loader);
                    Affuts.Walk w = null;
                    try { w = Affuts.BeginPlan(src); } catch (Exception e) { Mod.Log.Warning("[VISEE] données par unité non préparées : " + e.Message); }
                    PrecisionHeli.Walk pw = null;
                    try { pw = PrecisionHeli.BeginPlan(db, src); } catch (Exception e) { Mod.Log.Warning("[PRECISION] anti-hélico : lignes non préparées (" + e.Message + ")"); }
                    if (w != null || pw != null)
                    {
                        // survey first: every owner of every object is known before the first write
                        var s = new Ctx { P = p, Journal = true, Survey = true, W = w, PW = pw };
                        foreach (var u in units) SyncUnit(u, s);
                    }
                    var x = new Ctx { P = p, Journal = true, W = w, PW = pw };
                    foreach (var u in units) SyncUnit(u, x);
                    Log($"copies des unités : {units.Count} unité(s) chargée(s), {x.Objects} objet(s) vus, {x.Writes} valeur(s) et {x.Counts} quantité(s) recopiée(s) " +
                        $"(suivis : {string.Join(", ", p.ByType.Select(k => $"{k.Key.Name}={k.Value.Count}"))}, chargements={p.Loads.Count})");
                    if (w != null) Affuts.EndFirstPass(w, units.Count);
                    if (pw != null) PrecisionHeli.EndFirstPass(pw, units.Count);
                }
            }
            catch (Exception e) { Mod.Log.Warning("[VRAIES STATS] copies des unités non mises à jour : " + e); }
        }

        internal static object TakePlan()
        {
            lock (_lock) { var p = _plan; _plan = null; Affuts.EndPlan(); PrecisionHeli.EndPlan(); return p; }
        }

        /// After the journal was restored: copies the game made while the stats were applied get the table values back.
        internal static void ReverseSync(object plan)
        {
            if (plan is not Plan p) return;
            try
            {
                lock (_lock)
                {
                    var x = new Ctx { P = p, Journal = false };
                    foreach (var u in LoadedUnits(p.Loader)) SyncUnit(u, x);
                    if (x.Writes + x.Counts > 0) Log($"copies des unités remises aux valeurs du jeu : {x.Writes} valeur(s), {x.Counts} quantité(s)");
                    try { PrecisionHeli.AfterRestore(x.Leftovers); } catch (Exception e) { Mod.Log.Warning("[PRECISION] anti-hélico : contrôle après restauration impossible (" + e.Message + ")"); }
                }
            }
            catch (Exception e) { Mod.Log.Warning("[VRAIES STATS] copies des unités non remises : " + e.Message); }
        }

        // ------------------------------------------------------------ copies made later
        internal static void OnNewUnit(LoadUnits loader, Units u, string what)
        {
            if (Campaign.MissionInerte) return;                                          // mission without the mod: copies left as the game made them
            var p = _plan;
            if (p == null || u == null || loader == null) return;
            try
            {
                var src = loader._source;
                if (src == null || src.Pointer != p.Src) return;
                lock (_lock) { if (_plan != p) return; Walk(p, what, x => SyncUnit(u, x)); }
            }
            catch (Exception e) { Fail(e); }
        }

        internal static void OnNewTurret(LoadUnits loader, Turrets t, int unitId)
        {
            if (Campaign.MissionInerte) return;
            var p = _plan;
            if (p == null || t == null || loader == null) return;
            try
            {
                var src = loader._source;
                if (src == null || src.Pointer != p.Src) return;
                lock (_lock) { if (_plan != p) return; Walk(p, "tourelle", x => SyncTurret(t, unitId, x)); }
            }
            catch (Exception e) { Fail(e); }
        }

        /// The unit an arsenal card is about to show (it may be a deck copy made before the stats were applied).
        internal static void SyncShown(Units u, string what)
        {
            if (Campaign.MissionInerte) return;
            var p = _plan;
            if (p == null || u == null || !Realism.IsApplied) return;
            try { lock (_lock) { if (_plan != p) return; Walk(p, what, x => SyncUnit(u, x)); } }
            catch (Exception e) { Fail(e); }
        }

        /// One copy made later: survey of its owner keys (per-unit data only), then the journaled sync. Under _lock.
        static void Walk(Plan p, string what, Action<Ctx> body)
        {
            Affuts.Walk w = null;
            try { w = Affuts.BeginWalk(); } catch { w = null; }
            PrecisionHeli.Walk pw = null;
            try { pw = PrecisionHeli.BeginWalk(); } catch { pw = null; }
            if (w != null || pw != null) body(new Ctx { P = p, Journal = true, Survey = true, W = w, PW = pw });
            var x = new Ctx { P = p, Journal = true, W = w, PW = pw };
            body(x);
            Note(x, what);
            if (w != null) { try { Affuts.EndWalk(w, what); } catch { } }
            if (pw != null) { try { PrecisionHeli.EndWalk(pw); } catch { } }
        }

        static void Note(Ctx x, string what)
        {
            _hookCalls++;
            _hookWrites += x.Writes;
            _hookCounts += x.Counts;
            long now = Environment.TickCount64;                                   // the loader can run off the main thread: no Unity call here
            if ((x.Writes + x.Counts) > 0 && now >= _nextHookLog)
            {
                _nextHookLog = now + 60000;
                Log($"copies faites par le jeu ({what}) : {_hookCalls} contrôle(s), {_hookWrites} valeur(s) et {_hookCounts} quantité(s) recopiée(s) depuis l'application");
            }
        }

        static void Fail(Exception e)
        {
            if (_errors++ < 3) Mod.Log.Warning("[VRAIES STATS] copie d'unité non mise à jour : " + e.Message);
        }

        // ------------------------------------------------------------ graph walk
        static List<Units> LoadedUnits(LoadUnits loader)
        {
            var res = new List<Units>();
            var d = loader != null ? loader._loadedUnits : null;
            if (d == null) return res;
            foreach (var kv in d) { var u = kv.Value; if (u != null) res.Add(u); }
            return res;
        }

        static void Each<T>(Il2CppSystem.Collections.Generic.List<T> l, Action<T> f) where T : Il2CppObjectBase
        {
            if (l == null) return;
            for (int i = 0; i < l.Count; i++) { var v = l[i]; if (v != null) f(v); }
        }

        static bool First(Il2CppObjectBase o, Ctx x)
        {
            if (o == null || !x.Seen.Add(o.Pointer)) return false;
            x.Objects++;
            return true;
        }

        /// Plain objects of the sync (no per-unit data): copied once per walk, skipped by the survey.
        static void CopyOnce(Il2CppObjectBase o, int id, Ctx x)
        {
            if (x.Survey || o == null || !First(o, x)) return;
            Copy(o, id, x, null);
        }

        static void SyncUnit(Units u, Ctx x)
        {
            if (u == null || !First(u, x)) return;
            int uid = u.Id;
            if (!x.Survey) Copy(u, uid, x, null);
            SyncMobility(u.CurrentMobility, uid, x);
            SyncMobility(u.Mobility, uid, x);
            Each(u.Mobilities, m => SyncMobility(m, uid, x));
            if (!x.Survey)
            {
                var ca = u.CurrentArmor; if (ca != null) CopyOnce(ca, ca.Id, x);
                var ar = u.Armor; if (ar != null) CopyOnce(ar, ar.Id, x);
                Each(u.Armors, a => CopyOnce(a, a.Id, x));
            }
            SyncAbility(u.DefaultAbilities, uid, x);
            SyncAbility(u.ActiveAbilities, uid, x);
            Each(u.Abilities, a => SyncAbility(a, uid, x));
            SyncSensors(u.Sensors, uid, x);
            Each(u.Turrets, t => SyncTurret(t, uid, x));
            Each(u.SquadMembers, s => { SyncWeapon(s.PrimaryWeapon, uid, 0, x); SyncWeapon(s.SpecialWeapon, uid, 0, x); });
            Each(u.Modifications, md => Each(md.Options, o => SyncOption(o, uid, x)));
            Each(u.CurrentOptions, o => SyncOption(o, uid, x));
            SyncUnit(u.BaseUnit, x);
            SyncUnit(u.ReplaceUnit, x);
        }

        static void SyncOption(Options o, int uid, Ctx x)
        {
            if (o == null || !x.Keys.Add((o.Pointer, uid, TagOption))) return;
            if (!x.Survey) First(o, x);
            SyncOptionSensor(o, true, uid, x);
            SyncOptionSensor(o, false, uid, x);
            SyncMobility(o.Mobility, uid, x);
            if (!x.Survey)
            {
                var ar = o.Armor; if (ar != null) CopyOnce(ar, ar.Id, x);
            }
            SyncAbility(o.Ability1, uid, x);
            SyncAbility(o.Ability2, uid, x);
            SyncAbility(o.Ability3, uid, x);
            var turrets = new[]
            {
                o.Turret0, o.Turret1, o.Turret2, o.Turret3, o.Turret4, o.Turret5, o.Turret6, o.Turret7, o.Turret8, o.Turret9, o.Turret10,
                o.Turret11, o.Turret12, o.Turret13, o.Turret14, o.Turret15, o.Turret16, o.Turret17, o.Turret18, o.Turret19, o.Turret20,
            };
            foreach (var t in turrets) SyncTurret(t, uid, x);
        }

        /// An ability reached from a unit (own or option). Decoy abilities record their owner for the per-unit flare stock; the Id sync
        /// runs once per object and writes the value adjusted for this copy, then the rule is enforced.
        static void SyncAbility(Abilities a, int uid, Ctx x)
        {
            if (a == null || !x.Keys.Add((a.Pointer, uid, TagAbility))) return;
            var slot = x.W != null ? Affuts.NoteDecoy(x.W, a, uid) : null;
            if (x.Survey) return;
            if (First(a, x)) Copy(a, a.Id, x, slot);
            if (slot != null) x.Writes += Affuts.Enforce(x.W, slot, a);
        }

        static void SyncMobility(Mobility m, int uid, Ctx x)
        {
            if (m == null || !x.Keys.Add((m.Pointer, uid, TagMobility))) return;
            var slot = x.W != null ? Affuts.NoteMobility(x.W, m, uid) : null;
            if (x.Survey) return;
            if (First(m, x))
            {
                Copy(m, m.Id, x, slot);
                var fp = m.FlyPreset;
                if (First(fp, x)) Copy(fp, fp.Id, x, null);
            }
            if (slot != null) x.Writes += Affuts.Enforce(x.W, slot, m);
        }

        static void SyncSensors(Il2CppSystem.Collections.Generic.List<Sensors> list, int uid, Ctx x)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || !x.Keys.Add((s.Pointer, uid, TagSensor))) continue;
                Affuts.Slot slot = null;
                if (x.W != null)
                {
                    slot = Affuts.NoteSensor(x.W, s, uid);
                    if (!x.Survey && Affuts.NeedsOwnSensor(slot, uid))
                    {
                        // shared object or the table row: this unit gets its own clone in its list (journaled)
                        var own = Affuts.OwnSensorInList(x.W, list, i, s, uid);
                        if (own != null) { s = own; x.Keys.Add((s.Pointer, uid, TagSensor)); slot = Affuts.NoteSensor(x.W, s, uid); }
                    }
                }
                if (x.Survey) continue;
                if (First(s, x)) Copy(s, s.Id, x, slot);
                if (slot != null) x.Writes += Affuts.Enforce(x.W, slot, s);
            }
        }

        static void SyncOptionSensor(Options o, bool main, int uid, Ctx x)
        {
            var s = main ? o.MainSensor : o.ExtraSensor;
            if (s == null || !x.Keys.Add((s.Pointer, uid, TagSensor))) return;
            Affuts.Slot slot = null;
            if (x.W != null)
            {
                slot = Affuts.NoteSensor(x.W, s, uid);
                if (!x.Survey && Affuts.NeedsOwnSensor(slot, uid))
                {
                    var own = Affuts.OwnSensorInOption(x.W, o, main, s, uid);
                    if (own != null) { s = own; x.Keys.Add((s.Pointer, uid, TagSensor)); slot = Affuts.NoteSensor(x.W, s, uid); }
                }
            }
            if (x.Survey) return;
            if (First(s, x)) Copy(s, s.Id, x, slot);
            if (slot != null) x.Writes += Affuts.Enforce(x.W, slot, s);
        }

        static void SyncTurret(Turrets t, int unitId, Ctx x)
        {
            if (t == null || !x.Keys.Add((t.Pointer, unitId, TagTurret))) return;
            if (!x.Survey) First(t, x);
            int tid = t.Id;
            Each(t.Weapons, w => SyncWeapon(w, unitId, tid, x));
            Each(t.ChildTurrets, c => SyncTurret(c, unitId, x));
        }

        /// A weapon reached from (unit, turret; 0 = squad member). Its ammunition owners are recorded for every such key, even when the
        /// weapon object was already copied in this walk (shared objects must be known); the Id sync itself runs once per object.
        static void SyncWeapon(Weapons w, int unitId, int turretId, Ctx x)
        {
            if (w == null || !x.Keys.Add((w.Pointer, unitId, turretId))) return;
            if (x.Survey) { if (x.PW != null) PrecisionHeli.NoteWeapon(x.PW, w, unitId); }
            else if (!x.Journal) x.Leftovers += PrecisionHeli.Leftover(w);                  // restore walk: no helicopter-only copy may stay
            bool first = !x.Survey && First(w, x);
            if (first) Copy(w, w.Id, x, null);
            int wid = w.Id;
            var ammo = w.Ammunitions;
            if (ammo != null)
                for (int i = 0; i < ammo.Count; i++)
                {
                    var a = ammo[i];
                    if (a == null) continue;
                    var slot = x.W != null ? Affuts.NoteAmmo(x.W, a, unitId, turretId, wid) : null;
                    if (x.Survey) continue;
                    if (First(a, x)) Copy(a, a.Id, x, slot);
                    if (slot != null) x.Writes += Affuts.Enforce(x.W, slot, a);
                }
            if (first) SyncCounts(w, unitId, x);
            // after the caps and the counts: helicopter-only copies of this weapon (idempotent, every key reaching it)
            if (!x.Survey && x.Journal && x.PW != null) x.Writes += PrecisionHeli.OnWeapon(x.PW, w, unitId);
        }

        static void Copy(object obj, int id, Ctx x, Affuts.Slot slot)
        {
            if (obj == null) return;
            if (!x.P.ByType.TryGetValue(obj.GetType(), out var byId) || !byId.TryGetValue(id, out var e)) return;
            if (((Il2CppObjectBase)obj).Pointer == e.RowPtr) return;                           // the table row itself
            foreach (var p in e.Props)
            {
                object want = p.GetValue(e.Row), have = p.GetValue(obj);
                if (slot != null && x.Journal && x.W != null) want = Affuts.Adjust(x.W, slot, obj, p.Name, want);   // per-unit rule of this copy
                if (Equals(want, have)) continue;
                if (x.Journal) Realism.SetValueForOverride(obj, p, want); else p.SetValue(obj, want);
                x.Writes++;
                if (x.Journal && _detailLogs < 8) { _detailLogs++; Log($"copie : {obj.GetType().Name} {id} {p.Name} {have} -> {want}"); }
            }
        }

        static void SyncCounts(Weapons w, int unitId, Ctx x)
        {
            if (!x.P.Loads.TryGetValue((unitId, w.Id), out var rows)) return;
            var dict = w.WeaponAmmunitions;
            if (dict == null) return;
            var keys = new List<long>();
            foreach (var kv in dict) keys.Add(kv.Key);
            if (keys.Count == 0) return;
            string mode = KeyMode(keys, rows, unitId, w.Id);
            if (mode == null) return;
            foreach (var r in rows)
            {
                long k = mode == "munition" ? r.AmmoId : r.RowId;
                if (!dict.ContainsKey(k)) continue;
                if (mode == "munition" && dict.ContainsKey(k + PrecisionHeli.IdOffset)) continue;     // split into a helicopter-only copy: done
                int have = dict[k];
                int want = r.Row.Quantity;
                if (have == want) continue;
                if (x.Journal && have != r.Orig)
                {
                    // not the game's plain row value (summed duplicates, another format): never guess
                    if (_mismatchLogs++ < 5) Log($"quantité non recopiée : unité {unitId} arme {w.Id} munition {r.AmmoId} copie={have}, jeu={r.Orig}, vraie={want}");
                    continue;
                }
                long key = k;
                int back = have;
                if (x.Journal) Realism.JournalUndo(dict.Pointer, "quantité:" + key, () => { try { dict[key] = back; } catch { } });
                dict[k] = want;
                x.Counts++;
            }
        }

        static string KeyMode(List<long> keys, List<Load> rows, int unitId, int weaponId)
        {
            var ammo = new HashSet<long>(rows.Select(r => (long)r.AmmoId));
            var ids = new HashSet<long>(rows.Select(r => (long)r.RowId));
            // an ammunition id can equal a row id by chance (seen: 11): the format matching more keys wins, the known format breaks ties
            int nAmmo = keys.Count(ammo.Contains), nIds = keys.Count(ids.Contains);
            string m = nAmmo > nIds ? "munition" : nIds > nAmmo ? "ligne" : nAmmo > 0 ? _mode : null;
            if (m != null && _mode == null) { _mode = m; Log($"quantités par unité : la clé est l'id de {m} (unité {unitId}, arme {weaponId})"); }
            if (m == null && _keyLogs++ < 3)
                Log($"quantités par unité : clé non reconnue (unité {unitId}, arme {weaponId}, clés {string.Join("/", keys.Take(6))}, munitions {string.Join("/", ammo.Take(6))}, lignes {string.Join("/", ids.Take(6))}) : quantités laissées");
            return m;
        }
    }
}
