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
//  v1.0: this file also holds Discretion (its own header below), which switches the game's dormant "sneak" ability back on by
//  data. The two share nothing but this file, the RealismOverhaul_Reperage preference category and Reperage's online check.
//  v1.0: and MesureVue (its own header below), which reads the game's own detection clock and writes nothing at all. It is the
//  answer to "spotting should take time": the engine has exactly one clock, the measurement prints it, and the refusal to
//  lengthen it is argued in the log in French. It touches no state Reperage, VueAA or AntiHeliPortee read.
using System;
using System.Collections.Generic;
using System.Globalization;
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
using DbSource = Il2CppBrokenArrow.DataBase.DataBaseSourceData;
using AbilityRow = Il2CppBrokenArrow.DataBase.Models.Abilities;
using SneakSystem = Il2CppBrokenArrow.Client.Ecs.FogOfWar.Systems.SneakAbilitySystem;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;

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
        const string GuardVersion = "1.1";
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
            Discretion.CreatePrefs();                                  // same preference category, own entries
            MesureVue.CreatePrefs();                                   // same category, two automatic safety entries, no player switch
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false; _visLogged0 = _visLogged1 = false;
            _delayByName.Clear();
            _infoByUid.Clear();
            Discretion.ResetSession();
            MesureVue.ResetSession();
        }

        /// Online check of this module, shared with Discretion: false as soon as one network sign has been seen in this session.
        internal static bool IsSolo() => Solo();

        internal static void OnQuit()
        {
            EndBattle("fermeture du jeu");
            Discretion.OnQuit();
            MesureVue.OnQuit();
        }

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
            Discretion.Frame();                                        // own timer, own guards, own Planif ticket (this module's Perf.Run slot)
            MesureVue.Frame();                                         // same: read-only, at most two working frames in a whole battle
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

    // ---------------------------------------------------------------------------------------------------------------------
    /// RealismOverhaul - the game's own "sneak" ability, switched back on by data (v1.0), both sides alike, solo campaign only.
    ///  The game ships the whole chain and turns it off with one flag: the order button (UI.Orders.OrderButtonType.AbilitySneak),
    ///  the hotkey (Input.HotKeySettingType.AbilitySneak and the SneakButtonPressed trigger), the icon on the unit label
    ///  (LabelFacade.SetSneakIconState, LabelDataComponent.IsSneakOn), the infocard sprite, texts already translated in every
    ///  game language (ui_infocard_ability_sneak and its hints), the system (FogOfWar.Systems.SneakAbilitySystem, with the static
    ///  SetSneakState) and the component (SneakAbilityComponent: CoverBonus, VehicleSpeedMultiplier, IsEnabled) all exist, while
    ///  Abilities.IsSneakAbility is False on every ability line of the database. The mod already made exactly this move for the
    ///  vanilla artillery "auto fire" button (Realism.Apply, IsArtilleryAutoFire) and that button does show up in game, so this is
    ///  the same data write on the same table, not a new mechanism.
    ///  What the player gets, in the game's own wording: while the unit is not spotted it keeps a constant cover bonus whatever
    ///  the ground under it, at the cost of movement speed. It is a button the player presses, never an automatic effect.
    ///  Why it is not a cheat, and none of it comes from the mod: the engine never lets a sneaking unit go below
    ///  FogOfWarConfig.SneakMinAntivisible (0.2 = a fifth of the observer's optics), everything inside MinimumDetectionDistance
    ///  (45 m) is seen whatever happens, firing still builds the weapon flash, the cover bonus is lost the moment the unit is
    ///  spotted, and the flag is written on shared ability lines, so the AI recon gets exactly the same on its own units.
    ///  Not proven, and left at the game's own values because of it: SneakCoverBonus is kept at 2 (the value the game ships on all
    ///  78 ability lines) because the engine's wording is "constant cover bonus", so a unit sneaking inside a wood may end up with
    ///  2 instead of the forest's own cover, and a higher number would change nothing anyway for the units targeted here (their
    ///  stealth already lands them on the SneakMinAntivisible floor). The speed cost only has a field for vehicles
    ///  (SneakVehicleSpeedMultiplier); nothing says a squad on foot slows down at all. Both are for the battle log to answer.
    ///  Mission scripts: only a flag on an ability line the unit already carries is switched. No ability line is ever added or
    ///  cloned and Ability1Id/Ability2Id/Ability3Id are never touched (RU_C01 alone holds 214 setAbilityState nodes that address
    ///  an ability by its slot on the unit). Ability lines also carried by units outside the target roles are left alone, exactly
    ///  like the artillery block does.
    ///  Measurement stage (hidden preference EtapeDiscretion, model AltCercles.cs): 0 = the flag is written and the module only
    ///  reports; 1 = proven, a SetSneakState call was seen, so the button, the key and the system all answer; -1 = refused, the
    ///  game no longer ships the pieces or two database loads in a row found no ability line of its own to flag. Back to 0 at
    ///  every new game version. Never -1 because the player did not press the button: that proves nothing and is said so in the
    ///  log. No Harmony patch ever forces the button; the only hook is a read-only postfix on the static SetSneakState, installed
    ///  lazily in battle with its own Harmony id, never removed (volatile disarm flag), which counts calls and nothing else.
    ///  Inert on US_M01 and outside a solo campaign battle, crash guard (two sessions that did not end cleanly), error
    ///  kill-switch. Every write goes through Realism.SetValueForOverride: journaled, carried to the per-unit copies by
    ///  UnitCopies.AfterApply and put back with the rest of the realism. Logs: [DISCRETION].
    static class Discretion
    {
        const string GuardVersion = "1.1";
        const int RoleReconInf = 32, RoleSnipers = 33, RoleSpecForces = 36;
        const int TypeInfantry = 2;                                  // UnitType.Infantry bit
        const long MaxErrors = 20;
        static readonly string[] AbilitySlots = { "Ability1Id", "Ability2Id", "Ability3Id" };

        static MelonPreferences_Entry<bool> _on;
        static MelonPreferences_Entry<float> _cover, _speed;
        static MelonPreferences_Entry<int> _stage, _emptyLoads, _unclean;
        static MelonPreferences_Entry<string> _stageGame, _guardVersion;
        static HarmonyLib.Harmony _harmony;
        static readonly HashSet<string> _said = new(StringComparer.Ordinal);

        // hook side: read only, no allocation, no logging
        static volatile bool _armed;
        static long _calls, _switchedOn, _errors;

        static bool _patched, _refused, _plain, _sessionArmed;
        // last database application: ability lines that carry the flag, lines this pass had to write, lines left alone
        // because other units carry them too, and units in the target roles
        static int _marked, _written, _sharedLeft, _targets;
        // ability lines of these units that already carry another function (laser, sprint, smoke, artillery auto fire): never touched
        static int _busyLeft;
        static float _next;
        static int _wait;

        static void Log(string s) => Mod.Log.Msg("[DISCRETION] " + s);

        static void Say(string key, string s)
        {
            if (_said.Add(key)) Log(s);
        }

        static bool Active => !Campaign.MissionInerte && Campaign.InCampaign && Realism.RealModeOn && Realism.IsApplied;

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Reperage");
            _on = c.CreateEntry("Discretion", true, description: Build.Desc(
                "Rallume la capacité « discrétion » du jeu sur la reconnaissance, les tireurs d'élite et les forces spéciales : un bouton dans le panneau d'ordres (et sa touche) qui garde un bon camouflage tant que l'unité n'est pas repérée, au prix de la vitesse. Aucune invisibilité : sous 45 m tout est vu, le jeu garde toujours au moins un cinquième de la portée d'observation, et tirer trahit toujours",
                "Bouton « discrétion » du jeu sur la reconnaissance, les tireurs d'élite et les forces spéciales."));
            _cover = c.CreateEntry("DiscretionCouvert", 2.0f, description: Build.Desc(
                "Couvert gardé en discrétion quel que soit le terrain (2 = valeur du jeu, un peu moins que le couvert d'un bois)"));
            _speed = c.CreateEntry("DiscretionVitesse", 0.4f, description: Build.Desc(
                "Part de la vitesse gardée par un véhicule en discrétion (0.4 = 40 % de sa vitesse). Pour l'instant sans effet : seules les unités à pied reçoivent le bouton, la reconnaissance montée est prévue pour plus tard"));
            _stage = c.CreateEntry("EtapeDiscretion", 0, description: Build.Desc("Sécurité automatique (discrétion : 0 mesure, 1 actif, -1 refusé), ne pas modifier"));
            _stageGame = c.CreateEntry("VersionJeuDiscretion", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _emptyLoads = c.CreateEntry("DiscretionSansCapacite", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _unclean = c.CreateEntry("SessionsInterrompuesDiscretion", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecuriteDiscretion", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            string game = "?";
            try { game = UnityEngine.Application.version; } catch { }
            if (_stageGame.Value != game)
            {
                if (_stage.Value != 0) Log($"nouvelle version du jeu ({game}) : discrétion de nouveau en mesure");
                _stageGame.Value = game;
                _stage.Value = 0;
                _emptyLoads.Value = 0;
            }
        }

        // ---------------------------------------------------------------- database: the only write of this module

        /// Called once by Realism.Apply, on the database it just wrote, before UnitCopies.AfterApply carries the values to the
        /// per-unit copies. Returns the number of ability lines flagged.
        internal static int Apply(DbSource src)
        {
            _marked = _written = _sharedLeft = _targets = 0;
            if (src == null || _on == null || _refused) return 0;
            try
            {
                if (!_on.Value) { Say("off", "coupée dans les réglages"); return 0; }
                if (Campaign.MissionInerte) return 0;                          // mission played without the mod (US_M01)
                // the button is a SOLO CAMPAIGN feature, exactly as the header of this class promises. The real stats otherwise apply
                // outside a campaign too (VraiesStatsPartout), and there a button the other clients' database does not have would let
                // the player switch a state nobody else applies.
                if (!Campaign.InCampaign) { Say("horscampagne", "hors mission de campagne : le bouton n'est pas écrit dans la base"); return 0; }
                if (!Reperage.IsSolo()) { Say("enligne", "partie en ligne : le bouton n'est pas écrit dans la base"); return 0; }
                if (_stage.Value < 0) { Say("stage", "refusée par la mesure (étape -1) : le drapeau n'est plus écrit"); return 0; }
                if (_unclean.Value >= 2) { Say("crash", "coupée : les deux dernières parties ne se sont pas terminées normalement"); return 0; }

                var pFlag = Props.Get(typeof(AbilityRow), "IsSneakAbility");
                if (pFlag == null || !pFlag.CanWrite) { Refuse("la colonne IsSneakAbility est introuvable dans cette version du jeu"); return 0; }
                if (AccessTools.Method(typeof(SneakSystem), "SetSneakState") == null) { Refuse("SneakAbilitySystem.SetSneakState est introuvable dans cette version du jeu"); return 0; }
                var pCover = Props.Get(typeof(AbilityRow), "SneakCoverBonus");
                var pSpeed = Props.Get(typeof(AbilityRow), "SneakVehicleSpeedMultiplier");

                // units that get the ability: recon squads, snipers and special forces on foot (recon vehicles only after a test)
                var targets = new HashSet<int>();
                foreach (var u in Props.Rows(src.Units.GetAll()))
                {
                    int role = (int)u.Role;
                    if ((role == RoleReconInf || role == RoleSnipers || role == RoleSpecForces) && ((int)u.Type & TypeInfantry) != 0) targets.Add(u.Id);
                }
                _targets = targets.Count;
                if (targets.Count == 0) { Say("units", "aucune unité de reconnaissance, de tireurs d'élite ou de forces spéciales dans cette base : rien écrit"); return 0; }

                // ability lines those units carry, by their own abilities and by the abilities of their options
                var modUnit = new Dictionary<int, int>();
                foreach (var m in Props.Rows(src.Modifications.GetAll())) modUnit[m.Id] = m.UnitId;
                // ONE pass per table, not two: each row decides on the spot whether it feeds "carried by these units" or "carried by
                // somebody else too" (the artillery block of Mod.cs walks the same three tables twice, this one does not).
                var ids = new HashSet<int>();
                var shared = new HashSet<int>();
                foreach (var ua in Props.Rows(src.UnitAbilities.GetAll()))
                {
                    if (targets.Contains(ua.UnitId)) ids.Add(ua.AbilityId);
                    else shared.Add(ua.AbilityId);
                }
                foreach (var o in Props.Rows(src.Options.GetAll()))
                {
                    bool mine = modUnit.TryGetValue(o.ModificationId, out var uid) && targets.Contains(uid);
                    foreach (var slot in AbilitySlots)
                    {
                        var aid = Props.Num(o, slot);
                        if (!aid.HasValue || aid.Value <= 0) continue;
                        if (mine) ids.Add((int)aid.Value); else shared.Add((int)aid.Value);
                    }
                }
                int own = ids.Count;
                // lines also carried by other units (smoke, sprint, empty ability...) are left untouched, like the artillery block
                ids.ExceptWith(shared);
                _sharedLeft = own - ids.Count;

                // A row of this table is a PACKAGE, not an empty slot: this database holds "Sprint Smoke Laser", "Sprint Laser",
                // "Laser Drone 1500"... and those are exactly the packages a recon squad or a special-forces team would carry alone.
                // Writing the sneak flag on one of them could take away the laser designation (which the artillery reads as its
                // "tir désigné au laser") or the sprint. So only a row that does nothing else is ever flagged, and both lists of
                // names go into the log: one battle settles it instead of a guess.
                var busy = new List<string>();
                var keep = new List<int>();
                var kept = new List<string>();
                foreach (var id in ids)
                {
                    if (!src.Abilities.TryGetById(id, out var ab) || ab == null) continue;
                    if (Occupee(ab, out string quoi)) { if (busy.Count < 12) busy.Add($"{NomLigne(ab, id)} ({quoi})"); continue; }
                    keep.Add(id);
                    if (kept.Count < 12) kept.Add(NomLigne(ab, id));
                }
                ids = new HashSet<int>(keep);
                _busyLeft = own - _sharedLeft - keep.Count;
                _marked = keep.Count;
                if (_busyLeft > 0)
                    Say("occupees", $"{_busyLeft} capacité(s) propre(s) à ces unités font déjà autre chose et ne sont PAS touchées : {string.Join(", ", busy)}");
                if (kept.Count > 0) Say("gardees", $"capacité(s) marquée(s) « discrétion » : {string.Join(", ", kept)}");

                // nothing of its own to flag: that is the only thing that can close the file, and only when it happens twice in
                // a row. A line already flagged by an earlier application of the realism is counted as marked, not as missing.
                if (_marked == 0)
                {
                    // rows left alone because they already do something else are a DIFFERENT answer from "this game version has no
                    // own ability": they must not close the file for good, they must be read by the author in the log above.
                    if (_busyLeft > 0)
                    {
                        Log($"rien écrit : les {_busyLeft} capacité(s) propre(s) à ces unités font toutes déjà autre chose (laser, sprint, fumigènes) et le mod ne les remplace pas ({_targets} unités visées, {_sharedLeft} partagée(s) avec d'autres unités)");
                        return 0;
                    }
                    int n = _emptyLoads.Value + 1;
                    _emptyLoads.Value = n;
                    Log($"aucune ligne de capacité propre à ces unités ({_targets} unités visées, {_sharedLeft} capacité(s) partagée(s) avec d'autres unités, laissée(s) telle(s) quelle(s)) : rien écrit ({n} chargement(s) de base de suite)");
                    if (n >= 2 && _stage.Value == 0) { _stage.Value = -1; Log("discrétion refusée : cette version du jeu ne donne aucune capacité propre à la reconnaissance, aux tireurs d'élite ni aux forces spéciales"); }
                    MelonPreferences.Save();
                    return 0;
                }
                if (_emptyLoads.Value != 0) { _emptyLoads.Value = 0; MelonPreferences.Save(); }

                // bounded like the speed just below: a typing slip ("20" for "2.0") must not go into the database
                float cover = _cover.Value >= 1f && _cover.Value <= 4f ? _cover.Value : 2f;
                float speed = _speed.Value >= 0.1f && _speed.Value <= 1f ? _speed.Value : 0.4f;
                foreach (var id in ids)
                {
                    if (!src.Abilities.TryGetById(id, out var ab) || ab == null || ab.IsSneakAbility) continue;
                    Realism.SetValueForOverride(ab, pFlag, true);
                    if (pCover != null && pCover.CanWrite) Realism.SetValueForOverride(ab, pCover, cover);
                    if (pSpeed != null && pSpeed.CanWrite) Realism.SetValueForOverride(ab, pSpeed, speed);
                    _written++;
                }

                Log($"capacité « discrétion » rallumée : {_marked} ligne(s) de capacité pour {_targets} unités (reconnaissance, tireurs d'élite, forces spéciales)" +
                    (_written == _marked ? "" : $", dont {_written} écrite(s) à ce chargement") +
                    $" ; {_sharedLeft} capacité(s) partagée(s) avec d'autres unités et {_busyLeft} qui font déjà autre chose, laissée(s) telle(s) quelle(s) ; couvert gardé {cover.ToString("0.##", CultureInfo.InvariantCulture)}, " +
                    $"vitesse des véhicules {(speed * 100f).ToString("0", CultureInfo.InvariantCulture)} % ; {EngineFloors()} ; étape {_stage.Value}");
                return _written;
            }
            catch (Exception e) { Fail(e); return 0; }
        }

        /// True when this ability row already does something else. The precedent invoked for this whole block (IsArtilleryAutoFire)
        /// made a button APPEAR on a row that did nothing; nothing says a row that already works keeps its old job once flagged, so
        /// those rows are simply left alone. Any read error answers "busy": the doubt always protects the row.
        static bool Occupee(AbilityRow ab, out string quoi)
        {
            quoi = null;
            try
            {
                if (ab.IsLaserDesignator || ab.LaserMaxRange > 0f) quoi = "désignation laser";
                else if (ab.IsArtilleryAutoFire) quoi = "tir automatique d'artillerie";
                else if (ab.SmokeAmmunitionQuantity > 0) quoi = "fumigènes";
                else if (ab.SprintDuration > 0f) quoi = "sprint";
            }
            catch { quoi = "capacité illisible"; }
            return quoi != null;
        }

        /// Name of an ability row for the log (the id alone tells the author nothing).
        static string NomLigne(AbilityRow ab, int id)
        {
            try { var n = ab.Name; if (!string.IsNullOrEmpty(n)) return $"{n} (id {id})"; } catch { }
            return $"id {id}";
        }

        /// The engine's own floors, read from the game and printed as they are: they are what makes this not a cheat.
        static string EngineFloors()
        {
            try
            {
                var f = GameCfg.Instance?.FogOfWarConfig;
                if (f == null) return "planchers du moteur illisibles";
                return $"planchers du moteur inchangés : sous {f.MinimumDetectionDistance.ToString("0", CultureInfo.InvariantCulture)} m tout est vu, " +
                       $"et une unité en discrétion reste vue à au moins {(f.SneakMinAntivisible * 100f).ToString("0", CultureInfo.InvariantCulture)} % de la portée d'observation";
            }
            catch { return "planchers du moteur illisibles"; }
        }

        // ---------------------------------------------------------------- battle: measurement only

        /// Called at the top of Reperage.Frame, inside its Perf.Run slot but with its OWN Planif ticket: on the frames where this one
        /// takes the free slot, Reperage's own scan waits for the next one. Own timer, works every 2 s at most.
        internal static void Frame()
        {
            if (_stage == null || _refused) return;
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + 2f;
            try
            {
                if (!Active) { _armed = false; return; }
                if (!Reperage.IsSolo())
                {
                    if (_armed) { _armed = false; Log("partie en ligne : mesure de la discrétion coupée"); }
                    return;
                }
                if (!_sessionArmed)
                {
                    if (_unclean.Value >= 2)
                    {
                        _refused = true;
                        _armed = false;
                        Mod.Log.Warning("[DISCRETION] mesure coupée : les deux dernières parties ne se sont pas terminées normalement");
                        return;
                    }
                    _sessionArmed = true;
                    _unclean.Value = _unclean.Value + 1;
                    MelonPreferences.Save();
                }
                if (!_patched && !TryPatch()) return;
                _armed = Interlocked.Read(ref _errors) <= MaxErrors;
            }
            catch (Exception e) { Fail(e); }
        }

        static bool TryPatch()
        {
            try
            {
                var target = AccessTools.Method(typeof(SneakSystem), "SetSneakState");
                if (target == null) { Refuse("SneakAbilitySystem.SetSneakState est introuvable dans cette version du jeu"); return false; }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Discretion");
                // the name of the argument is PROBED first, never tried and caught: a postfix Harmony refuses is already recorded on
                // the method, so a second Patch call would build the same wrapper and throw again — and the whole measurement would
                // be lost for the session instead of falling back. Same way Reperage.TryPatch checks "weaponPosition".
                bool hasState = false;
                try
                {
                    foreach (var pi in target.GetParameters())
                        if (pi.Name == "state" && pi.ParameterType == typeof(bool)) { hasState = true; break; }
                }
                catch { hasState = false; }
                _plain = !hasState;
                var post = typeof(Discretion).GetMethod(hasState ? nameof(PostfixState) : nameof(PostfixPlain), BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(post));
                _patched = true;
                Log($"mesure installée : postfix en lecture seule sur SetSneakState{(_plain ? " (sens du passage inconnu)" : "")}, " +
                    $"{_marked} capacité(s) marquée(s) au chargement, étape {_stage.Value}. Aucun crochet ne force le bouton.");
                return true;
            }
            catch (Exception e)
            {
                _refused = true;
                _armed = false;
                Mod.Log.Warning("[DISCRETION] mesure non installée (" + e.GetBaseException().Message + ")");
                return false;
            }
        }

        // hook: counters only, no allocation, no logging, any thread
        static void PostfixState(bool state)
        {
            if (!_armed) return;
            Interlocked.Increment(ref _calls);
            if (state) Interlocked.Increment(ref _switchedOn);
        }

        static void PostfixPlain()
        {
            if (!_armed) return;
            Interlocked.Increment(ref _calls);
        }

        // ---------------------------------------------------------------- end of battle: verdict

        internal static void ResetSession() => EndBattle("nouvelle mission");

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        static void EndBattle(string why)
        {
            _armed = false;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            try
            {
                if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
                long calls = Interlocked.Read(ref _calls), on = Interlocked.Read(ref _switchedOn);
                int before = _stage.Value;
                if (calls > 0 && before == 0) { _stage.Value = 1; MelonPreferences.Save(); }
                Log($"bilan ({why}) : {_marked} capacité(s) marquée(s) au chargement, {calls} passage(s) en discrétion observé(s)" +
                    (_plain ? "" : $" (dont {on} mise(s) en discrétion)") +
                    $", erreurs {Interlocked.Read(ref _errors)} ; étape {before}" + (_stage.Value != before ? $" -> {_stage.Value}" : "") +
                    (calls == 0 && _marked > 0 ? " ; personne n'a appuyé sur le bouton : cela ne prouve rien, la mesure continue" : ""));
                if (_stage.Value == 1 && before != 1) Log("le bouton « discrétion », sa touche et le système du jeu répondent : capacité confirmée");
            }
            catch (Exception e) { Fail(e); }
            Interlocked.Exchange(ref _calls, 0);
            Interlocked.Exchange(ref _switchedOn, 0);
        }

        // ---------------------------------------------------------------- safety

        /// The game no longer ships what this needs: the flag is never written again until the next game version.
        static void Refuse(string why)
        {
            _refused = true;
            _armed = false;
            try
            {
                if (_stage != null && _stage.Value != -1) { _stage.Value = -1; MelonPreferences.Save(); }
            }
            catch { }
            Mod.Log.Warning("[DISCRETION] discrétion refusée : " + why);
        }

        static void Fail(Exception e)
        {
            long n = Interlocked.Increment(ref _errors);
            _armed = false;
            if (n <= 3) Mod.Log.Warning("[DISCRETION] discrétion : erreur (" + e.GetBaseException().Message + ")");
            if (n >= MaxErrors && !_refused)
            {
                _refused = true;
                Mod.Log.Warning($"[DISCRETION] discrétion coupée après {n} erreurs (le reste du mod fonctionne)");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    /// RealismOverhaul - how long the game itself takes to notice a unit (v1.0). MEASUREMENT ONLY, and that is the whole point:
    ///  this module installs no Harmony patch, writes nothing into the game, keeps no state anybody else reads, and answers in
    ///  the battle log, in French, the question the author asked - "a unit should not be instantly and perfectly known the
    ///  moment it enters vision range".
    ///
    ///  WHAT WAS ESTABLISHED FIRST, in the dumps of this exact build, before any design (each point is a fact, not an opinion):
    ///   - Detection is BINARY, never graded. FogOfWarComponent is DistanceGround / DistanceLowAlt / DistanceHighAlt,
    ///     AntiVisible, WeaponFlash, SpottedPenalty, VisibleTo (a set of entities), WasDetectedOnce, PotentialSee, Shootable,
    ///     ExternalChecked, the ground points and two dirty flags. A unit is in VisibleTo or it is not. There is no acquisition
    ///     progress, no confidence, no identification level, and NO FIELD OF TIME AT ALL. The module checks that again at run
    ///     time on the player's own build and prints the count, so this does not rest on my reading of a dump.
    ///   - FogOfWarConfig holds no delay either, and nothing per side, per player or per difficulty: FILTER_STEP_LENGTH,
    ///     TerrainTypeSettings, MinimumDetectionDistance, MaxShootableForestDistance, LaserForestMaxRange, LaserOwnForestRange,
    ///     FlashReduction, MaxFlashValue, MaxAntiVisible, SpottedPenalty, SpottedPenaltyBuildings, SneakMinAntivisible. So a
    ///     detection delay cannot be put in by data, the way the real optics, the stealth values and the cover ratings were.
    ///   - Detection IS per observer, and that part is rich: one FogOfWarUnit per unit, each with its own _owner, its own
    ///     FogOfWarFilter, its own raycast buffers, and its own scan clock (_scanPeriod / _lastScan for the general pass,
    ///     _scanPeriodGround / _lastScanGround for the ground points), read by IsUnitScanTick(). There is even a per pair
    ///     function, CalculateVisionRangeForTarget(Entity& checker, Entity& target).
    ///
    ///  SO THE ENGINE OWNS EXACTLY ONE CLOCK, and it is neither in the database nor in the config: the scan period is a
    ///  constructor argument kept in a private field of every FogOfWarUnit. That field can be written at run time - the interop
    ///  assembly of this build exposes its setter. THIS MODULE DELIBERATELY DOES NOT WRITE IT, and says so in the log:
    ///   (a) the same tick gates the whole re-evaluation, so lengthening the period delays LOSING a contact exactly as much as
    ///       gaining one. The complaint this feature exists to answer is the opposite one ("the spot sticks"), so a longer
    ///       period would make the very thing the author dislikes worse, not better;
    ///   (b) to be felt as "spotting takes two or three seconds" the period would have to go to four to six seconds, and then
    ///       the whole fog of war advances in jerks - aircraft at 250 m/s move more than a kilometre between two ticks - with
    ///       the mod's own air defence rules (Reperage's spotting delay, VueAA, AntiHeliPortee) reading that same fog. That is
    ///       realistic and miserable, which the author's standing rule calls a failure;
    ///   (c) the AI's scripted waves run on mission timers and aggressive radii, not on what it has spotted, so slowing the fog
    ///       down for both sides in the same way still costs the player far more than the AI. "Never in the AI's favour" is
    ///       easier to keep by changing nothing.
    ///  What the mod can honestly do instead, and already does elsewhere, is make a unit spotted CLOSER rather than later: real
    ///  optics per unit, stealth per unit, cover, the weapon flash of firing, and the sneak button of Discretion just above.
    ///
    ///  COST: two working frames in a whole battle (one around 8 s, one around 150 s), at most 150 units sampled per fog team,
    ///  inside Reperage's Perf.Run slot and behind its own Planif ticket. Nothing per frame, nothing on a hot path.
    ///  SAFETY: everything through plain System.Reflection on names, so a build that renamed one of these types logs a clean
    ///  "unreadable" line instead of failing to start; every step in try/catch; own error counter and kill-switch; and the
    ///  project's crash guard (two battles left in the middle with the walk in place and it is never walked again), because
    ///  enumerating an engine collection is the one thing here a try/catch cannot protect. Inert on US_M01, outside a solo
    ///  campaign battle and when the realism is not applied. Logs: [REPERAGE-VUE].
    static class MesureVue
    {
        const string GuardVersion = "1.0";
        const int MaxUnits = 150;                                   // units sampled per fog team: enough for a stable picture, invisible in Perf
        const int MaxWalk = 4000;                                   // hard stop on the enumeration, whatever the collection does
        const int MaxTries = 8;                                     // attempts to find the fog system before giving up for this battle
        const long MaxErrors = 10;
        const float FirstAt = 8f, SecondAt = 150f;                  // seconds of battle before the first and the second reading

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;

        static bool _refused, _sessionArmed, _first, _done, _fieldsSaid;
        static float _next, _battleStart = -1f, _lastGeneral = -1f;
        static int _wait, _tries, _passes;
        static long _errors;

        // reflection handles, resolved once per session
        static Type _tSystem, _tWorker, _tUnit;
        static PropertyInfo _pInstance, _pWorkers, _pSam, _pTeamUnits, _pEnemyUnits, _pPeriod, _pPeriodGround;
        static bool _resolved, _resolveFailed;

        static void Log(string s) => Mod.Log.Msg("[REPERAGE-VUE] " + s);

        /// Same activity guard as the other modules of this wave (AltCercles model): solo campaign, mod applied, US_M01 inert.
        static bool Active => !Campaign.MissionInerte && Campaign.InCampaign && Realism.RealModeOn && Realism.IsApplied;

        internal static void CreatePrefs()
        {
            // No player switch on purpose: this reads and never writes, so there is nothing for the player to turn off. The two
            // entries below are the automatic crash guard, exactly like the ones Reperage and Discretion keep.
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Reperage");
            _unclean = c.CreateEntry("SessionsInterrompuesVue", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecuriteVue", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
        }

        internal static void ResetSession() => EndBattle("nouvelle mission");

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        /// Called at the top of Reperage.Frame, with its own timer and its own Planif ticket. Does real work twice per battle.
        internal static void Frame()
        {
            if (_refused || _unclean == null || _done) return;
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + 2f;
            try
            {
                if (!Active) { _battleStart = -1f; return; }
                if (!Reperage.IsSolo()) return;                      // the fog of an online game is not ours to look at
                if (_battleStart < 0f) { _battleStart = now; return; }
                float age = now - _battleStart;
                if (!_first && age < FirstAt) return;
                if (_first && age < SecondAt) return;
                if (!_sessionArmed)
                {
                    if (_unclean.Value >= 2)
                    {
                        _refused = true;
                        Mod.Log.Warning("[REPERAGE-VUE] relevé abandonné par sécurité : les deux dernières batailles où il était en place ne se sont pas terminées normalement (rien n'était modifié, ce module ne fait que lire)");
                        return;
                    }
                    _sessionArmed = true;
                    _unclean.Value = _unclean.Value + 1;
                    MelonPreferences.Save();                         // the guard is on disk BEFORE the first walk, which happens at the next tick
                    return;
                }
                if (!Resolve())
                {
                    _done = true;
                    Log("le brouillard du jeu ne s'ouvre pas à la lecture dans cette version (types ou champs renommés) : aucun relevé, et rien n'est modifié");
                    return;
                }
                if (!Mesurer(!_first))
                {
                    if (++_tries >= MaxTries)
                    {
                        _done = true;
                        // "not in place yet" is only true of the FIRST reading. The second one happens late in the battle, where a
                        // failure means the fog is gone, not that it never came: naming the wrong cause would send the author looking
                        // for a bug that is not there.
                        Log(_first
                            ? $"le brouillard du jeu n'est plus lisible après {MaxTries} essais (bataille terminée ou brouillard démonté) : deuxième relevé abandonné, et rien n'est modifié"
                            : $"le système de brouillard du jeu n'était pas encore en place après {MaxTries} essais : aucun relevé pour cette bataille, et rien n'est modifié");
                    }
                    return;
                }
                _passes++;
                // the try counter belongs to ONE reading: left standing, the misses of the first reading would add themselves to
                // those of the second and blame "the fog was not in place yet" for a fog that was there and is now gone
                _tries = 0;
                if (_first) _done = true; else _first = true;
            }
            catch (Exception e) { Fail(e); }
        }

        // ---------------------------------------------------------------- reflection handles

        static bool Resolve()
        {
            if (_resolved) return true;
            if (_resolveFailed) return false;
            try
            {
                _tSystem = AccessTools.TypeByName("Il2CppBrokenArrow.Client.Ecs.FogOfWar.Systems.FogOfWarSystem");
                _tWorker = AccessTools.TypeByName("Il2CppBrokenArrow.Client.Ecs.FogOfWar.FogOfWarWorker");
                _tUnit = AccessTools.TypeByName("Il2CppBrokenArrow.Client.Ecs.FogOfWar.FogOfWarUnit");
                if (_tSystem != null)
                {
                    _pInstance = _tSystem.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    _pWorkers = _tSystem.GetProperty("_teamsFogWorkers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _pSam = _tSystem.GetProperty("ANTI_PROJECTILE_SAM_SEARCH_PERIOD_MULTIPLIER", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (_tWorker != null)
                {
                    _pTeamUnits = _tWorker.GetProperty("_teamUnits", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _pEnemyUnits = _tWorker.GetProperty("_enemyUnits", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_tUnit != null)
                {
                    _pPeriod = _tUnit.GetProperty("_scanPeriod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _pPeriodGround = _tUnit.GetProperty("_scanPeriodGround", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
            }
            catch (Exception e) { Fail(e); }
            // the three that the whole reading rests on; everything else degrades into "illisible" in the log
            if (_pInstance == null || _pWorkers == null || _pTeamUnits == null || _pPeriod == null) { _resolveFailed = true; return false; }
            _resolved = true;
            return true;
        }

        // ---------------------------------------------------------------- the reading itself (pure reads)

        /// One reading of the game's own detection clock. False = the fog system is not built yet, try again later.
        static bool Mesurer(bool premier)
        {
            object sys = _pInstance.GetValue(null);
            if (sys == null) return false;
            object arr = _pWorkers.GetValue(sys);
            int teams = arr == null ? -1 : Longueur(arr);
            if (arr == null || teams <= 0) return false;

            var lignes = new List<string>();
            var gTotal = new Stat();
            int unitsTotal = 0;
            for (int i = 0; i < teams; i++)
            {
                object worker = Travailleur(Element(arr, i));
                if (worker == null) { lignes.Add($"brouillard n°{i + 1} : illisible"); continue; }
                object teamSet = Lire(worker, _pTeamUnits), enemySet = Lire(worker, _pEnemyUnits);
                var gen = new Stat(); var sol = new Stat();
                int n = Parcourir(teamSet, gen, sol);
                unitsTotal += n;
                gTotal.Merge(gen);
                lignes.Add($"brouillard n°{i + 1} : {Compte(teamSet)} unité(s) qui observent, {Compte(enemySet)} cible(s) suivies ; " +
                           $"période d'examen générale {gen.Txt()} ; au sol {sol.Txt()}" + (n >= MaxUnits ? $" (relevé arrêté à {MaxUnits} unités)" : ""));
            }
            if (unitsTotal == 0) return false;

            if (premier)
            {
                string sam = "?";
                try { if (_pSam != null) sam = Convert.ToSingle(_pSam.GetValue(null)).ToString("0.###", Inv); } catch { }
                Log($"relevé du brouillard du jeu : {teams} brouillard(s) simulé(s), un par camp ; multiplicateur de période de recherche des SAM anti-missile {sam}");
                for (int i = 0; i < lignes.Count; i++) Log(lignes[i]);
                Verdict(gTotal);
                Leviers();
                if (!_fieldsSaid) { _fieldsSaid = true; Log("champs du brouillard d'une unité (FogOfWarComponent) : " + ChampsBrouillard()); }
                Log("aucune valeur n'a été écrite : ce relevé ne fait que lire le jeu");
            }
            else
            {
                float g = gTotal.Count > 0 ? gTotal.Moy : -1f;
                Log($"deuxième relevé ({SecondAt.ToString("0", Inv)} s de bataille) : {unitsTotal} unité(s) lue(s), période d'examen générale " +
                    (g < 0f ? "illisible" : g.ToString("0.###", Inv) + " s") +
                    // an observation, not a cause: the module reads no reinforcement and cannot know whether any arrived
                    (g >= 0f && _lastGeneral >= 0f ? (Math.Abs(g - _lastGeneral) < 0.0005f ? " (inchangée : aucune unité lue n'a une cadence différente du premier relevé)" : $" (elle était de {_lastGeneral.ToString("0.###", Inv)} s au premier relevé)") : ""));
            }
            if (gTotal.Count > 0) _lastGeneral = gTotal.Moy;
            return true;
        }

        /// The sentence the author reads. Every number in it comes from his own battle, none of it from me.
        static void Verdict(Stat gen)
        {
            if (gen.Count == 0) { Log("verdict : la période d'examen n'a pas pu être lue ; rien n'est modifié"); return; }
            // "au plus tard" is the WORST case, so it carries the MAXIMUM period, never the minimum. The periods are not uniform in
            // this build - the engine has its own ANTI_PROJECTILE_SAM_SEARCH_PERIOD_MULTIPLIER, and the reading counts distinct
            // values because several are expected - so the range is printed whenever the two ends differ.
            string pMin = gen.Min.ToString("0.###", Inv), pMax = gen.Max.ToString("0.###", Inv);
            string plage = Math.Abs(gen.Max - gen.Min) < 0.0005f ? pMax : pMin + " à " + pMax;
            Log($"verdict : le jeu réexamine ce que chaque unité voit toutes les {plage} s. Une unité qui entre dans le champ est donc " +
                $"repérée au plus tard {pMax} s après, et d'un seul coup : le brouillard du jeu n'a ni compteur de temps, ni " +
                "probabilité, ni identification par paliers. Le repérage est tout ou rien, et il l'est pour les deux camps.");
            // wording kept as the log filter of the PUBLIC build knows it ("non installé ("), so a shared build keeps the refusal
            // and its reasons instead of dropping them with the rest of the burst
            Log("délai de repérage non installé (c'est volontaire) : la seule horloge du moteur est cette période, celle-là même " +
                "qui décide aussi quand un contact est PERDU. L'allonger retarderait autant la perte de contact que le repérage, " +
                "donc aggraverait le « contact qui colle » au lieu de le corriger ; et pour qu'un délai se sente (2 à 3 s) il " +
                "faudrait une période de 4 à 6 s, où toute la vue du jeu avancerait par à-coups, avions et hélicoptères compris, " +
                "avec les règles anti-aériennes du mod qui lisent ce même brouillard par-dessus. Rien n'a été écrit.");
            Log("ce que le mod fait à la place, et qui se ressent pareil : être repéré plus PRÈS plutôt que plus tard — optiques " +
                "réelles par unité, furtivité, couvert, signature de tir, et le bouton « discrétion » rendu au joueur.");
        }

        /// The levers this build really ships, so the author knows what exists without taking my word for it. Nothing is hooked.
        static void Leviers()
        {
            string tick = "non", portee = "non", ecriture = "non";
            try { if (_tUnit != null && AccessTools.Method(_tUnit, "IsUnitScanTick") != null) tick = "oui"; } catch { }
            try { if (_tUnit != null && AccessTools.Method(_tUnit, "CalculateVisionRangeForTarget") != null) portee = "oui"; } catch { }
            try { if (_pPeriod != null && _pPeriod.CanWrite) ecriture = "oui"; } catch { }
            Log($"leviers présents dans cette version du jeu (aucun n'est détourné ni écrit par le mod) : IsUnitScanTick {tick}, " +
                $"CalculateVisionRangeForTarget {portee} (portée de vue calculée pour chaque paire observateur / cible), " +
                $"période d'examen modifiable {ecriture}.");
        }

        /// Counts the fields of the unit fog component and names the ones that could carry a time or a chance. On this build the
        /// answer is none, and printing it is how the author checks that for himself instead of trusting a dump.
        static string ChampsBrouillard()
        {
            try
            {
                var t = AccessTools.TypeByName("Il2CppBrokenArrow.Client.Ecs.FogOfWar.Components.FogOfWarComponent");
                if (t == null) return "type introuvable dans cette version";
                int nb = 0;
                var suspects = new List<string>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    // the generated class inherits ObjectClass / Pointer / WasCollected from Il2CppObjectBase: counting them would
                    // print 21 where the dump shows 18, and this line exists precisely so the count can be checked against the dump
                    if (p.DeclaringType != t) continue;
                    nb++;
                    string n = p.Name.ToLowerInvariant();
                    bool suspect = n.Contains("time") || n.Contains("delay") || n.Contains("progress") || n.Contains("duration")
                                || n.Contains("chance") || n.Contains("probab") || n.Contains("cooldown");
                    if (suspect && suspects.Count < 8) suspects.Add(p.Name);
                }
                return nb + " champ(s), dont " + (suspects.Count == 0
                    ? "aucun qui porte un temps ou une probabilité : le repérage du jeu n'a rien à retarder"
                    : suspects.Count + " à regarder de près (" + string.Join(", ", suspects) + ")");
            }
            catch (Exception e) { return "illisible (" + e.GetBaseException().Message + ")"; }
        }

        // ---------------------------------------------------------------- reflection helpers (no engine type is named at compile time)

        static int Longueur(object arr)
        {
            try
            {
                var t = arr.GetType();
                var p = t.GetProperty("Length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? t.GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return p == null ? -1 : Convert.ToInt32(p.GetValue(arr));
            }
            catch { return -1; }
        }

        static object Element(object arr, int i)
        {
            try
            {
                var m = arr.GetType().GetMethod("get_Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                                null, new[] { typeof(int) }, null);
                return m?.Invoke(arr, new object[] { i });
            }
            catch { return null; }
        }

        /// The array holds (TeamData, IFogOfWarWorker) pairs: take the second member, then make sure we hold the concrete worker.
        static object Travailleur(object tuple)
        {
            if (tuple == null) return null;
            object w = null;
            try
            {
                var t = tuple.GetType();
                var p = t.GetProperty("Item2", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) w = p.GetValue(tuple);
                if (w == null)
                {
                    var f = t.GetField("Item2", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) w = f.GetValue(tuple);
                }
            }
            catch { return null; }
            if (w == null) return null;
            if (_tWorker != null && _tWorker.IsInstanceOfType(w)) return w;
            // the interface wrapper: ask the interop runtime for the concrete one, still without naming it at compile time
            try
            {
                var tc = w.GetType().GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
                if (tc != null && tc.IsGenericMethodDefinition && _tWorker != null) return tc.MakeGenericMethod(_tWorker).Invoke(w, null);
            }
            catch { }
            return null;
        }

        static object Lire(object target, PropertyInfo p)
        {
            if (target == null || p == null) return null;
            try { return p.DeclaringType != null && p.DeclaringType.IsInstanceOfType(target) ? p.GetValue(target) : null; }
            catch { return null; }
        }

        static string Compte(object set)
        {
            if (set == null) return "?";
            try
            {
                var p = set.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return p == null ? "?" : Convert.ToInt32(p.GetValue(set)).ToString(Inv);
            }
            catch { return "?"; }
        }

        /// Walks the fog units of one team and collects their two scan periods. Main thread, twice per battle, hard bounded.
        static int Parcourir(object set, Stat gen, Stat sol)
        {
            if (set == null) return 0;
            object en = null;
            MethodInfo next = null; PropertyInfo cur = null;
            try
            {
                var st = set.GetType();
                var mGet = st.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                        null, Type.EmptyTypes, null);
                if (mGet == null) return 0;
                en = mGet.Invoke(set, null);
                if (en == null) return 0;
                var et = en.GetType();
                next = et.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                cur = et.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { return 0; }
            if (en == null || next == null || cur == null) return 0;
            int n = 0, guard = 0;
            while (n < MaxUnits && guard++ < MaxWalk)
            {
                object more;
                try { more = next.Invoke(en, null); } catch { break; }
                if (!(more is bool avance) || !avance) break;
                object u;
                try { u = cur.GetValue(en); } catch { break; }
                if (u == null) continue;
                n++;
                try { gen.Add(Convert.ToSingle(_pPeriod.GetValue(u))); } catch { }
                if (_pPeriodGround != null) { try { sol.Add(Convert.ToSingle(_pPeriodGround.GetValue(u))); } catch { } }
            }
            return n;
        }

        /// Min / mean / max and the distinct values of one period, kept without ever growing without bound.
        sealed class Stat
        {
            int _n;
            float _min = float.MaxValue, _max = float.MinValue, _sum;
            readonly List<float> _distinct = new();

            internal int Count => _n;
            internal float Min => _n == 0 ? -1f : _min;
            internal float Max => _n == 0 ? -1f : _max;
            internal float Moy => _n == 0 ? -1f : _sum / _n;

            /// Adds every sample of another reading, so the whole battle's mean is a real mean and not a mean of means.
            internal void Merge(Stat o)
            {
                if (o == null || o._n == 0) return;
                _n += o._n; _sum += o._sum;
                if (o._min < _min) _min = o._min;
                if (o._max > _max) _max = o._max;
                for (int i = 0; i < o._distinct.Count; i++) AddDistinct(o._distinct[i]);
            }

            void AddDistinct(float v)
            {
                if (_distinct.Count >= 8) return;
                for (int i = 0; i < _distinct.Count; i++) if (Math.Abs(_distinct[i] - v) < 0.0005f) return;
                _distinct.Add(v);
            }

            internal void Add(float v)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) return;
                _n++; _sum += v;
                if (v < _min) _min = v;
                if (v > _max) _max = v;
                AddDistinct(v);
            }

            internal string Txt() => _n == 0
                ? "illisible"
                : $"min {_min.ToString("0.###", Inv)} / moy {(_sum / _n).ToString("0.###", Inv)} / max {_max.ToString("0.###", Inv)} s sur {_n} unité(s), {(_distinct.Count >= 8 ? "8 valeurs ou plus" : _distinct.Count + " valeur(s) distincte(s)")}";
        }

        // ---------------------------------------------------------------- end of battle and safety

        static void EndBattle(string why)
        {
            bool armed = _sessionArmed;
            _sessionArmed = false;
            _first = false; _done = false; _battleStart = -1f; _tries = 0; _lastGeneral = -1f;
            if (!armed) { _passes = 0; return; }
            try
            {
                if (_unclean != null && _unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
                Log($"bilan ({why}) : {_passes} relevé(s) du brouillard du jeu, erreurs {Interlocked.Read(ref _errors)} ; aucune valeur du jeu n'a été modifiée par ce module");
            }
            catch { }
            _passes = 0;
        }

        static void Fail(Exception e)
        {
            long n = Interlocked.Increment(ref _errors);
            if (n <= 3) Mod.Log.Warning("[REPERAGE-VUE] relevé : erreur (" + e.GetBaseException().Message + ")");
            if (n >= MaxErrors && !_refused)
            {
                _refused = true;
                Mod.Log.Warning($"[REPERAGE-VUE] relevé coupé après {n} erreurs (rien n'était modifié, le reste du mod fonctionne)");
            }
        }
    }
}
