// RealismOverhaul - hits on helicopters and who fired them: MEASUREMENT ONLY (no behaviour change, design A2 of 2026-09-16).
//  The player sees his helicopters (and the enemy's) lost without knowing who shot them, and the shooter is never revealed to the
//  helicopter. Before any "the hit helicopter spots its attacker" rule, one battle must show:
//   - that damage to a helicopter goes through an impact (ShellHitSystem depth shared by LeurresMesure + CalculateHitDamage),
//     cross-checked with the helicopters' health polled every 0.5 s (hits that do not come from shells);
//   - which shooter source is reliable: the damage statistics id (CollectStatistic), the shooter entity of the shot vector
//     (GetArmorSideFromShotVector), or the nearest enemy ground unit within 9000 m (preferring one whose loadout has that ammo);
//   - whether the two fog-of-war override points fire at all, on which thread and with which values (installed only for a 20 s
//     window 3 minutes into the battle; DISABLED in 0.22.7: an unpatch leaves a pass-through detour on IL2CPP);
//   - whether the matched shooter carries FogOfWarVisibleMarker 0.5, 2, 5 and 15 s after the hit.
//  v0.23.0, flares v2 proof (flares_v2_design.md §8): helicopters AND planes are tracked; an impact whose damage the flare gate set to 0
//  (LeurresMesure.GatedFor) becomes a "gated impact" event, and the health poll counts a health drop right after a gated impact with no
//  other impact, and a drop with no impact at all while a decoyed infrared missile is in flight. LeurresMesure reads these counts at
//  battle end (TakeProof): they only move its fly-off stages and log alerts, the infrared decision and the damage gate act from the
//  first battle. The minimum distance between the two sides (ContactMeters) feeds its combat watchdog.
//  v0.23.0, [PRECISION] gun accuracy against helicopters, measurement only (heli_realism_gun-accuracy.json step 0):
//   - once per battle: dispersion reference range, ranges, dispersion radii and proximity fuse of reference rows, the engine's own
//     accuracy estimate (GetTargetAccuracy) against a few helicopter and ground unit rows, and the hit box (GetUnitBox) of a few units
//     (direct calls on the main thread, each call kind behind a crash guard);
//   - in battle: rounds fired toward airborne helicopters per ammunition and 250 m band, from the game's own ammunition counts
//     (GameplayBus.GetUnitsAmmo per unit and ammunition row, no hook), against the hits on helicopters seen here, every 60 s and at the
//     end of the battle. A row that can target helicopters and no ground target (the helicopter-only per-unit copies of gun rows,
//     Id 20000 + source, looked up in the database every 60 s) counts every round it fires toward the nearest airborne enemy
//     helicopter, with no ground ambiguity; the source row of a unit carrying such a copy counts as fire toward the ground. Dual-target
//     rows keep the old rule (a round counts toward a helicopter only when no enemy ground unit is within the row's ground range) and
//     report their doubtful rounds per band.
//  Hooks only queue plain values read from immutable snapshots rebuilt on the main thread; everything else runs on the main thread.
//  Both sides alike, solo campaign only, lazy Harmony patches with their own id, crash guard, error kill-switch.
//  Logs: [REPERE-TIREUR] and [PRECISION].
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Data;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using ShellHit = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellHitSystem;
using HitDet = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.HitDetectionSystem;
using FowUnit = Il2CppBrokenArrow.Client.Ecs.FogOfWar.FogOfWarUnit;
using FowVisibleMarker = Il2CppBrokenArrow.Client.Ecs.FogOfWar.Components.FogOfWarVisibleMarker;
using Aircraft = Il2CppBrokenArrow.Client.Ecs.Planes.AircraftHelper;
using AltLevel = Il2CppBrokenArrow.Shared.Ecs.Enums.GlobalAltitude;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using AmmoTarget = Il2CppBrokenArrow.DataBase.Enums.TargetType;
using SeekerType = Il2CppBrokenArrow.DataBase.Enums.SeekerType;
using Trajectory = Il2CppBrokenArrow.DataBase.Enums.TrajectoryType;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using EcsEntity = Il2CppDefaultEcs.Entity;
using EcsEventBusGetUnitsAmmo = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.EcsEventBus.GameplayBus.GetUnitsAmmoDel;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class AntiHeliTouches
    {
        const string GuardVersion = "0.24.0";
        const int MaxErrors = 50;
        const float SuspectRange = 9000f;                           // fallback suspect: nearest enemy ground unit within this distance
        const float FogStart = 180f, FogLength = 20f;               // fog-of-war sampling window (seconds after the battle start)
        const int QueueSize = 1024;                                 // power of two
        const int EvDamage = 1, EvStat = 2, EvArmor = 3, EvGated = 4, EvPlaneDamage = 5;
        const int NSamples = 8, MaxChecks = 16;
        const int LineHit = 0, LineHp = 1, LineVis = 2, LineGone = 3, LineGate = 4;
        static readonly int[] LineBudget = { 12, 12, 12, 20, 12 };  // detailed lines per 30 s, the rest is only counted
        static readonly float[] VisDelays = { 0.5f, 2f, 5f, 15f };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion, _precCall, _precRefused;
        static HarmonyLib.Harmony _harmony, _fowHarmony;
        static bool _patchTried, _refused, _everOnline, _sessionArmed, _depthWarned, _fowBroken, _helisLogged;
        static float _nextSnap, _nextReport, _battleStart, _fowUntil, _nextContact;
        static int _fowState;                                       // 0 waiting, 1 sampling, 2 done for this battle
        const bool FogSamplingEnabled = false;                      // see Frame: kept for a later version that installs the hooks once
        static bool _depthSeen;                                     // the impact depth was ready at some point in this battle
        static LuaMap _map;

        // ---------------------------------------------------------------- hook side (immutable snapshots, plain counters)
        static volatile bool _armed;
        static volatile bool _fowOff = true;
        static volatile int _mainThread;
        static volatile Dictionary<int, int> _heliSide = new();    // helicopter EntityId -> side, replaced as a whole
        static volatile Dictionary<int, int> _airSide = new();     // helicopter and plane EntityId -> side, replaced as a whole

        /// Minimum horizontal distance in metres between a unit of side 0 and a unit of side 1 (planes excluded), refreshed every 5 s;
        /// -1 when not measured (module off or no battle), 1e9 when a side has no unit. Any thread.
        internal static volatile float ContactMeters = -1f;

        struct Ev { public long Seq, Hit; public int Kind, Victim, A, Thread; public float F; }
        static readonly Ev[] _ev = new Ev[QueueSize];
        static long _evHead;                                        // last sequence number claimed by a hook

        static long _dmgInHit, _dmgHeli, _dmgOffMain, _statCalls, _statHeli, _statOffMain, _armorInHit, _armorHeli, _armorOffMain, _errors, _gatedEvents;
        static long _visCalls, _visOffMain, _visHeliChecker, _visHeliCheckerSum, _visOther, _visOtherSum, _visHeliTarget, _fowErrors;
        static long _optCalls, _optOffMain, _optHeli, _optHeliSum, _optHeliAmmoSum, _optOther, _optOtherSum;
        static long _vsN, _vtN, _osN, _ogN;
        static readonly int[] _vsChecker = new int[NSamples], _vsTarget = new int[NSamples], _vtChecker = new int[NSamples], _vtTarget = new int[NSamples];
        static readonly float[] _vsValue = new float[NSamples], _vtValue = new float[NSamples];
        static readonly int[] _osEid = new int[NSamples], _ogEid = new int[NSamples];
        static readonly float[] _osOptics = new float[NSamples], _osAmmo = new float[NSamples], _ogOptics = new float[NSamples], _ogAmmo = new float[NSamples];

        // ---------------------------------------------------------------- main thread
        sealed class UnitInfo { public int Uid, Eid, Side, Type, UnitId, Player; public V3 Pos; public LuaUnit U; }

        sealed class HitCase
        {
            public long Hit; public int VictimEid, Ammo, StatId, ArmorEid, Thread;
            public float Dmg, StatDmg;
            public bool HasDamage, HasStat, HasArmor;
        }

        sealed class VisCheck
        {
            public int ShooterUid, ShooterSide, VictimUid, Ammo, Step;
            public string Source, ShooterName, VictimName;
            public LuaUnit U;
            public float T0, Dist;
            public readonly string[] Res = new string[4];
        }

        static long _evRead, _evLost;
        static Dictionary<int, UnitInfo> _units = new();                                  // uid -> alive unit (last snapshot)
        static Dictionary<int, int> _uidByEid = new();                                    // EntityId -> uid
        static Dictionary<int, int> _playerSide = new();                                  // owner player UID -> side
        static List<UnitInfo>[] _ground = { new(), new() };                              // alive ground units (infantry, vehicle, ship) per side
        static readonly Dictionary<int, (int unitId, int type, int player)> _meta = new();
        static readonly Dictionary<int, int> _typeByUnitId = new();
        static readonly Dictionary<int, string> _names = new(), _ammoNames = new();
        static readonly Dictionary<int, (int hp, int side)> _heliHp = new();             // heli uid -> last health %
        static readonly Dictionary<int, (int hp, int side)> _planeHp = new();            // plane uid -> last health %
        static readonly Dictionary<int, int> _hitsSincePoll = new(), _hitsTotal = new(), _ammoHits = new();
        static readonly Dictionary<int, int> _planeHitsSincePoll = new(), _gatedSincePoll = new();
        static readonly List<VisCheck> _checks = new();
        static readonly HashSet<int> _checkedShooters = new();
        static Dictionary<int, HashSet<int>> _ammoByUnit;
        static IntPtr _ammoSrc;

        static long _heliHits, _victimUnknown, _hitsWithStat, _statEnemyUnit, _statSameUnit, _statEnemyPlayer, _statSamePlayer, _statEntity, _statUnknown, _statNoHit;
        static long _hitsWithArmor, _armorEnemyGround, _armorEnemyOther, _armorSame, _armorVictim, _armorUnknown;
        static long _suspectFound, _suspectWithAmmo, _agreeStatN, _agreeStat, _agreeArmorN, _agreeArmor, _checksSkipped, _snapErrors;
        static long _hpDrops, _hpDropsNoHit, _heliDead, _heliGone;
        static long _gatedTotal, _afterGateTotal, _noHitWindowTotal, _planeDrops, _planeDropsNoHit;
        static readonly long[] _distBand = new long[5];                                   // <500 m, 500-1000, 1-2 km, 2-3 km, >3 km
        static readonly long[] _visYes = new long[4], _visNo = new long[4], _visDeadAt = new long[4];
        static readonly int[] _linesLeft = new int[5];
        static readonly long[] _linesSuppressed = new long[5];
        static string _lastReport;

        // flares proof counters: not cleared at battle end, only by TakeProof (the two modules may end their battle in either order)
        static long _pGated, _pAfterGate, _pNoHitWindow, _pNoHit, _pDrops;
        static bool _pRunning;

        /// Flare proof counts since the previous TakeProof (LeurresMesure, main thread).
        internal struct AirProof
        {
            public bool Running;                                    // hits and health of aircraft were really followed
            public long GatedImpacts, DropsAfterGate, DropsNoHitInDecoyWindow, DropsNoHit, Drops;
        }

        static void Log(string s) => Mod.Log.Msg("[REPERE-TIREUR] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_AntiHeliTouches");
            _enabled = c.CreateEntry("MesureTouchesHelicos", true, description: Build.Desc("Mesure (ne change rien au jeu) des tirs qui touchent les hélicos et les avions, de leur tireur et de la précision des armes contre les hélicos ; contrôle des leurres"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _precCall = c.CreateEntry("PrecisionAppelEnCours", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _precRefused = c.CreateEntry("PrecisionAppelsRefuses", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; _precRefused.Value = ""; _precCall.Value = ""; }
            if (!string.IsNullOrEmpty(_precCall.Value))
            {
                // the game stopped during this direct call last time: never try it again in this version
                _precRefused.Value = string.IsNullOrEmpty(_precRefused.Value) ? _precCall.Value : _precRefused.Value + "," + _precCall.Value;
                _precCall.Value = "";
            }
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false;
            _meta.Clear(); _names.Clear();
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        /// Aircraft (helicopter or plane) side of an EntityId, -1 when it is not an aircraft of the last snapshot. Any thread, no allocation.
        internal static int AircraftSide(int entityId)
        {
            var a = _airSide;
            return a != null && a.TryGetValue(entityId, out int s) ? s : -1;
        }

        /// Flare proof counts since the previous call; pending hook events are drained first. Main thread.
        internal static AirProof TakeProof()
        {
            if (_sessionArmed) { try { Drain(UnityEngine.Time.realtimeSinceStartup); } catch { } }
            var p = new AirProof { Running = _pRunning, GatedImpacts = _pGated, DropsAfterGate = _pAfterGate, DropsNoHitInDecoyWindow = _pNoHitWindow, DropsNoHit = _pNoHit, Drops = _pDrops };
            _pGated = _pAfterGate = _pNoHitWindow = _pNoHit = _pDrops = 0;
            _pRunning = _sessionArmed && _armed && LeurresMesure.HitDepthReady;
            return p;
        }

        static void EndBattle(string why)
        {
            _armed = false;
            CloseFog(why);
            _heliSide = new Dictionary<int, int>();
            _airSide = new Dictionary<int, int>();
            ContactMeters = -1f;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
            try { Drain(UnityEngine.Time.realtimeSinceStartup); } catch { }
            FlushChecks();
            Report(true);
            try { PrecisionReport(true); } catch (Exception e) { LogPrec("bilan illisible (" + e.GetBaseException().Message + ")"); }
            Log($"fin de mesure ({why})");
            ClearBattle();
        }

        static int _wait;

        /// Every frame in campaign: drains the hook queue and runs due visibility checks; snapshot and health poll every 0.5 s.
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _refused) { if (_armed) _armed = false; ContactMeters = -1f; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= _nextSnap)
            {
                // one heavy module job per frame (Planif.cs): the 0.5 s snapshot keeps its period, it just avoids the frames of the others
                if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
                _nextSnap = now + 0.5f;
                if (!SlowTick(now)) return;
            }
            if (!_sessionArmed) return;
            Drain(now);
            RunChecks(now);
        }

        /// Returns false when the battle is not (or no longer) measured.
        static bool SlowTick(float now)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_sessionArmed) EndBattle("plus de partie"); return false; }
            if (!Solo()) { if (_armed) { _armed = false; Log("partie en ligne : mesure coupée"); } CloseFog("partie en ligne"); ContactMeters = -1f; return false; }

            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2)
                {
                    _refused = true;
                    ContactMeters = -1f;
                    Mod.Log.Warning("[REPERE-TIREUR] mesure désactivée : les deux dernières parties mesurées ne se sont pas terminées normalement");
                    return false;
                }
                _sessionArmed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();
                _battleStart = now;
                _nextReport = now + 30f;
                _precNextReport = now + 60f;
                _nextContact = now;
                _fowState = 0;
                Array.Copy(LineBudget, _linesLeft, LineBudget.Length);
                int side = -1;
                try { side = (int)cp.TeamSide; } catch { }
                Log($"mesure prête (aucun changement de jeu) ; camp du joueur {side} ; le marqueur visible est lu tel que le jeu le pose (a priori ce que voit le joueur)");
            }

            // drain first: hits of the last 0.5 s are counted against the aircraft health read just after
            Drain(now);
            Snapshot();
            if (now >= _nextContact) { _nextContact = now + 5f; try { ContactMeters = ComputeContact(); } catch { ContactMeters = -1f; } }
            if (!_patchTried) { _patchTried = true; TryPatchAll(); }
            if (_refused) return false;
            _armed = Interlocked.Read(ref _errors) <= MaxErrors;
            if (LeurresMesure.HitDepthReady) { _depthSeen = true; if (_armed) _pRunning = true; }
            if (!_depthWarned && now - _battleStart > 10f && !LeurresMesure.HitDepthReady)
            {
                _depthWarned = true;
                Log("profondeur d'impact indisponible (mesure des leurres coupée ou non installée) : les impacts sur hélicos ne seront pas vus, seule la santé est suivie");
            }
            // fog-of-war sampling window disabled in this version: on IL2CPP an unpatch leaves a pass-through detour for the whole session
            if (FogSamplingEnabled) FogWindow(now);
            if (!_precOff)
            {
                try { PrecisionTick(now); }
                catch (Exception e)
                {
                    if (++_precErrors <= 3) LogPrec("erreur (" + e.GetBaseException().Message + ")");
                    if (_precErrors > 20) { _precOff = true; LogPrec("mesure de précision coupée pour cette bataille (trop d'erreurs)"); }
                }
            }
            if (now >= _nextReport) { _nextReport = now + 30f; Report(false); Array.Copy(LineBudget, _linesLeft, LineBudget.Length); }
            if (now >= _precNextReport) { _precNextReport = now + 60f; try { PrecisionReport(false); } catch { } }
            return true;
        }

        static bool Solo()
        {
            try
            {
                string st = NetStatus.Status.ToString();
                if (NetScen.IsNetwork || NetScen.IsScenarioSlave || NetScen.IsScenarioHost || st == "Loading" || st == "Deploy" || st == "Game") _everOnline = true;
            }
            catch { return false; }
            return !_everOnline;
        }

        // ---------------------------------------------------------------- patches
        static void TryPatchAll()
        {
            _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.AntiHeliTouches");
            int ok = 0;
            ok += Patch(_harmony, "dégâts sur hélico", AccessTools.Method(typeof(BSH), "CalculateHitDamage"), nameof(DamagePostfix));
            ok += Patch(_harmony, "statistique de dégâts", AccessTools.Method(typeof(ShellHit), "CollectStatistic"), nameof(StatPostfix));
            // 0.22.8: GetArmorSideFromShotVector (Vector3 by value, enum return) is not hooked any more: it decides the armour side of every hit
            if (ok == 0) Log("aucun point de mesure des impacts installable dans cette version du jeu : seule la santé des hélicos est suivie");
            else Log($"{ok}/2 point(s) de mesure des impacts installé(s) (aucun changement de comportement)");
        }

        static int Patch(HarmonyLib.Harmony h, string label, MethodInfo target, string postfix)
        {
            try
            {
                if (target == null) { Log($"{label} : méthode introuvable"); return 0; }
                h.Patch(target, postfix: new HarmonyMethod(typeof(AntiHeliTouches).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
                return 1;
            }
            catch (Exception e) { Log($"{label} : non installé ({e.GetBaseException().Message})"); return 0; }
        }

        // ---------------------------------------------------------------- hooks (any thread, no Unity call, no allocation, no logging)
        static void Push(int kind, long hit, int victim, int a, float f)
        {
            long n = Interlocked.Increment(ref _evHead);
            int slot = (int)((n - 1) & (QueueSize - 1));
            _ev[slot].Seq = 0;                                                   // slot being written
            _ev[slot].Hit = hit; _ev[slot].Kind = kind; _ev[slot].Victim = victim; _ev[slot].A = a; _ev[slot].F = f;
            _ev[slot].Thread = Environment.CurrentManagedThreadId;
            _ev[slot].Seq = n;                                                   // written last: the slot is complete
        }

        /// static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo, Single penetration, Boolean forceTopArmorAttack, ArmorSides armorSide)
        /// Runs after LeurresMesure's gate (Priority.First): a gated call on an aircraft becomes a gated impact event.
        static void DamagePostfix(EcsEntity target, Ammo ammoInfo, float __result)
        {
            if (Campaign.MissionInerte || !_armed || LeurresMesure.HitDepth <= 0) return;
            try
            {
                Interlocked.Increment(ref _dmgInHit);
                var air = _airSide;
                int eid = target.EntityId;
                if (air == null || !air.ContainsKey(eid)) return;
                if (LeurresMesure.GatedFor(eid))
                {
                    Interlocked.Increment(ref _gatedEvents);
                    Push(EvGated, LeurresMesure.HitSerial, eid, ammoInfo != null ? ammoInfo.Id : 0, LeurresMesure.GatedValue);
                    return;
                }
                if (!(__result > 0f)) return;
                var helis = _heliSide;
                if (helis == null || !helis.ContainsKey(eid)) { Push(EvPlaneDamage, LeurresMesure.HitSerial, eid, 0, __result); return; }
                Interlocked.Increment(ref _dmgHeli);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _dmgOffMain);
                int id = 0;
                if (ammoInfo != null) id = ammoInfo.Id;
                Push(EvDamage, LeurresMesure.HitSerial, eid, id, __result);
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        /// static Void CollectStatistic(Int32 damageDealerId, Entity& targetEntity, Single damage)
        static void StatPostfix(int damageDealerId, ref EcsEntity targetEntity, float damage)
        {
            if (Campaign.MissionInerte || !_armed) return;
            try
            {
                Interlocked.Increment(ref _statCalls);
                var helis = _heliSide;
                int eid = targetEntity.EntityId;
                if (helis == null || !helis.ContainsKey(eid)) return;
                Interlocked.Increment(ref _statHeli);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _statOffMain);
                Push(EvStat, LeurresMesure.HitDepth > 0 ? LeurresMesure.HitSerial : 0, eid, damageDealerId, damage);
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        /// static ArmorSides GetArmorSideFromShotVector(Entity& shooter, Entity& target, Vector3 globalImpactVector)
        static void ArmorPostfix(ref EcsEntity shooter, ref EcsEntity target)
        {
            if (Campaign.MissionInerte || !_armed || LeurresMesure.HitDepth <= 0) return;
            try
            {
                Interlocked.Increment(ref _armorInHit);
                var helis = _heliSide;
                int eid = target.EntityId;
                if (helis == null || !helis.ContainsKey(eid)) return;
                Interlocked.Increment(ref _armorHeli);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _armorOffMain);
                Push(EvArmor, LeurresMesure.HitSerial, eid, shooter.EntityId, 0f);
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        /// FogOfWarUnit :: Single CalculateVisionRangeForTarget(Entity& checkerEntity, Entity& target). Sampling window only.
        static void VisionPostfix(ref EcsEntity checkerEntity, ref EcsEntity target, float __result)
        {
            if (Campaign.MissionInerte || _fowOff) return;
            try
            {
                Interlocked.Increment(ref _visCalls);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _visOffMain);
                var helis = _heliSide;
                if (helis == null) return;
                long v = Meters(__result);
                int c = checkerEntity.EntityId, t = target.EntityId;
                if (helis.ContainsKey(c))
                {
                    Interlocked.Increment(ref _visHeliChecker);
                    Interlocked.Add(ref _visHeliCheckerSum, v);
                    long k = Interlocked.Increment(ref _vsN) - 1;
                    if (k < NSamples) { _vsChecker[k] = c; _vsTarget[k] = t; _vsValue[k] = __result; }
                }
                else
                {
                    Interlocked.Increment(ref _visOther);
                    Interlocked.Add(ref _visOtherSum, v);
                }
                if (helis.ContainsKey(t))
                {
                    Interlocked.Increment(ref _visHeliTarget);
                    long k = Interlocked.Increment(ref _vtN) - 1;
                    if (k < NSamples) { _vtChecker[k] = c; _vtTarget[k] = t; _vtValue[k] = __result; }
                }
            }
            catch { if (Interlocked.Increment(ref _fowErrors) > MaxErrors) _fowOff = true; }
        }

        /// FogOfWarUnit :: Void GetMaxOpticsCheckDistance(Entity& checker, Single& maxOpticsRange, Single& maxAmmoRange). Sampling window only.
        static void OpticsPostfix(ref EcsEntity checker, ref float maxOpticsRange, ref float maxAmmoRange)
        {
            if (Campaign.MissionInerte || _fowOff) return;
            try
            {
                Interlocked.Increment(ref _optCalls);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _optOffMain);
                var helis = _heliSide;
                if (helis == null) return;
                int c = checker.EntityId;
                if (helis.ContainsKey(c))
                {
                    Interlocked.Increment(ref _optHeli);
                    Interlocked.Add(ref _optHeliSum, Meters(maxOpticsRange));
                    Interlocked.Add(ref _optHeliAmmoSum, Meters(maxAmmoRange));
                    long k = Interlocked.Increment(ref _osN) - 1;
                    if (k < NSamples) { _osEid[k] = c; _osOptics[k] = maxOpticsRange; _osAmmo[k] = maxAmmoRange; }
                }
                else
                {
                    Interlocked.Increment(ref _optOther);
                    Interlocked.Add(ref _optOtherSum, Meters(maxOpticsRange));
                    long k = Interlocked.Increment(ref _ogN) - 1;
                    if (k < NSamples) { _ogEid[k] = c; _ogOptics[k] = maxOpticsRange; _ogAmmo[k] = maxAmmoRange; }
                }
            }
            catch { if (Interlocked.Increment(ref _fowErrors) > MaxErrors) _fowOff = true; }
        }

        static long Meters(float v) => v > 1e7f ? 10_000_000L : v > 0f ? (long)v : 0L;

        // ---------------------------------------------------------------- snapshot (main thread, every 0.5 s)
        static void Snapshot()
        {
            _map ??= new LuaMap();
            var units = new Dictionary<int, UnitInfo>();
            var byEid = new Dictionary<int, int>();
            var players = new Dictionary<int, int>();
            var heliSide = new Dictionary<int, int>();
            var airSide = new Dictionary<int, int>();
            var ground = new[] { new List<UnitInfo>(), new List<UnitInfo>() };
            var seenHelis = new HashSet<int>();
            var seenPlanes = new HashSet<int>();
            for (int side = 0; side < 2; side++)
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LuaUnit> arr = null;
                try { arr = _map.GetUnits(V3.zero, 1_000_000f, side, -1); } catch { _snapErrors++; }
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = arr[i];
                        if (u == null) continue;
                        int uid = u.UID;
                        if (!u.IsAlive())
                        {
                            if (_heliHp.TryGetValue(uid, out var last)) { seenHelis.Add(uid); HeliLost(uid, last, true); }
                            continue;
                        }
                        var m = Meta(u, uid);
                        var info = new UnitInfo { Uid = uid, Eid = u.Entity.EntityId, Side = side, Type = m.type, UnitId = m.unitId, Player = m.player, U = u, Pos = u.GetPosition() };
                        units[uid] = info;
                        byEid[info.Eid] = uid;
                        if (m.player != int.MinValue) players[m.player] = side;
                        if ((m.type & 8) != 0)
                        {
                            heliSide[info.Eid] = side;
                            airSide[info.Eid] = side;
                            seenHelis.Add(uid);
                            if (!_names.ContainsKey(uid)) { try { _names[uid] = u.Name ?? "?"; } catch { } }   // still named once gone
                            PollHealth(info);
                        }
                        else if ((m.type & 16) != 0)
                        {
                            airSide[info.Eid] = side;
                            seenPlanes.Add(uid);
                            if (!_names.ContainsKey(uid)) { try { _names[uid] = u.Name ?? "?"; } catch { } }
                            PollPlane(info);
                        }
                        else if (IsGround(m.type)) ground[side].Add(info);
                    }
                    catch { _snapErrors++; }
                }
            }
            foreach (var uid in _heliHp.Keys.ToList())
                if (!seenHelis.Contains(uid)) HeliLost(uid, _heliHp[uid], false);
            foreach (var uid in _planeHp.Keys.ToList())
                if (!seenPlanes.Contains(uid)) { _planeHp.Remove(uid); _planeHitsSincePoll.Remove(uid); _gatedSincePoll.Remove(uid); }
            _units = units; _uidByEid = byEid; _playerSide = players; _ground = ground;
            _heliSide = heliSide;
            _airSide = airSide;
            if (!_helisLogged && airSide.Count > 0)
            {
                _helisLogged = true;
                int s0 = heliSide.Values.Count(s => s == 0), p0 = airSide.Count - heliSide.Count;
                Log($"appareils sur la carte : hélicos camp 0 {s0}, camp 1 {heliSide.Count - s0} ; avions {p0}");
            }
        }

        static bool IsGround(int type) => type != 0 && (type & (8 | 16)) == 0;         // infantry 2, vehicle 4, ship 32

        /// Minimum horizontal distance between the two sides (helicopters and ground units, planes excluded).
        static float ComputeContact()
        {
            var a = new List<V3>();
            var b = new List<V3>();
            foreach (var u in _units.Values)
            {
                if (u.Type == 0 || (u.Type & 16) != 0) continue;
                if (u.Side == 0) a.Add(u.Pos); else if (u.Side == 1) b.Add(u.Pos);
            }
            if (a.Count == 0 || b.Count == 0) return 1e9f;
            double best = double.MaxValue;
            foreach (var pa in a)
                foreach (var pb in b)
                {
                    double dx = pa.x - pb.x, dz = pa.z - pb.z;
                    double sq = dx * dx + dz * dz;
                    if (sq < best) best = sq;
                }
            return (float)Math.Sqrt(best);
        }

        static (int unitId, int type, int player) Meta(LuaUnit u, int uid)
        {
            if (_meta.TryGetValue(uid, out var m)) return m;
            int unitId = 0, player = int.MinValue;
            try { unitId = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            try { player = u.GetOwnerPlayerUID(); } catch { }
            m = (unitId, TypeOfUnitId(unitId), player);
            if (unitId > 0 && _meta.Count < 20000) _meta[uid] = m;                      // not cached while the spawn data is not readable yet
            return m;
        }

        static int TypeOfUnitId(int id)
        {
            if (id <= 0) return 0;
            if (_typeByUnitId.TryGetValue(id, out int t)) return t;
            UnitsRow row = null;
            try { var src = DataBaseService._instance?.RawAccess; if (src != null) src.Units.TryGetById(id, out row); } catch { row = null; }
            if (row == null) return 0;
            try { t = (int)row.Type; } catch { return 0; }
            _typeByUnitId[id] = t;
            return t;
        }

        static string NameOf(int uid)
        {
            if (_names.TryGetValue(uid, out var n)) return n;
            n = "?";
            try { if (_units.TryGetValue(uid, out var info)) n = info.U?.Name ?? "?"; } catch { n = "?"; }
            if (n != "?" && _names.Count < 5000) _names[uid] = n;
            return n;
        }

        static string AmmoName(int id)
        {
            if (_ammoNames.TryGetValue(id, out var n)) return n;
            n = "";
            try { var src = DataBaseService._instance?.RawAccess; if (src != null && src.Ammunitions.TryGetById(id, out var row) && row != null) n = row.Name ?? ""; } catch { n = ""; }
            _ammoNames[id] = n;
            return n;
        }

        /// The unit type's database loadout contains this ammo Id (index built once per database).
        static bool HasAmmo(int unitId, int ammoId)
        {
            if (unitId <= 0 || ammoId <= 0) return false;
            // a helicopter-only per-unit copy is carried by the units whose database loadout has its source row
            if (ammoId > CopyIdBase && _sourceOfCopy.TryGetValue(ammoId, out int source)) ammoId = source;
            try
            {
                var src = DataBaseService._instance?.RawAccess;
                if (src == null) return false;
                if (_ammoByUnit == null || _ammoSrc != src.Pointer)
                {
                    _ammoSrc = src.Pointer;
                    _ammoByUnit = new Dictionary<int, HashSet<int>>();
                    foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
                    {
                        if (wa == null) continue;
                        if (!_ammoByUnit.TryGetValue(wa.UnitId, out var set)) _ammoByUnit[wa.UnitId] = set = new HashSet<int>();
                        set.Add(wa.AmmunitionId);
                    }
                }
                return _ammoByUnit.TryGetValue(unitId, out var s) && s.Contains(ammoId);
            }
            catch { _ammoByUnit ??= new Dictionary<int, HashSet<int>>(); return false; }
        }

        // ---------------------------------------------------------------- health cross-check
        static void PollHealth(UnitInfo h)
        {
            int hp;
            try { hp = h.U.GetHealPercentage(); } catch { return; }
            int hits = _hitsSincePoll.TryGetValue(h.Uid, out var n) ? n : 0;
            int gated = _gatedSincePoll.TryGetValue(h.Uid, out var g) ? g : 0;
            if (_heliHp.TryGetValue(h.Uid, out var prev) && hp < prev.hp)
            {
                _hpDrops++;
                if (hits == 0) _hpDropsNoHit++;
                NoteAirDrop(h, "hélico", prev.hp, hp, hits, gated);
                Line(LineHp, $"santé de l'hélico {NameOf(h.Uid)} (uid {h.Uid}, camp {h.Side}) : {prev.hp} -> {hp} % ; impacts vus par le mod depuis le relevé précédent : {hits}" +
                    (gated > 0 ? $", impacts neutralisés (missile leurré) : {gated}" : ""));
            }
            _heliHp[h.Uid] = (hp, h.Side);
            _hitsSincePoll[h.Uid] = 0;
            _gatedSincePoll[h.Uid] = 0;
        }

        static void PollPlane(UnitInfo p)
        {
            int hp;
            try { hp = p.U.GetHealPercentage(); } catch { return; }
            int hits = _planeHitsSincePoll.TryGetValue(p.Uid, out var n) ? n : 0;
            int gated = _gatedSincePoll.TryGetValue(p.Uid, out var g) ? g : 0;
            if (_planeHp.TryGetValue(p.Uid, out var prev) && hp < prev.hp)
            {
                _planeDrops++;
                if (hits == 0) _planeDropsNoHit++;
                NoteAirDrop(p, "avion", prev.hp, hp, hits, gated);
            }
            _planeHp[p.Uid] = (hp, p.Side);
            _planeHitsSincePoll[p.Uid] = 0;
            _gatedSincePoll[p.Uid] = 0;
        }

        /// Flare proof: a health drop right after a gated impact with no other impact, or with no impact at all while a decoyed missile flies.
        static void NoteAirDrop(UnitInfo a, string kind, int from, int to, int hits, int gated)
        {
            _pDrops++;
            if (hits > 0) return;
            _pNoHit++;
            if (gated > 0)
            {
                _pAfterGate++;
                _afterGateTotal++;
                Line(LineGate, $"ALERTE leurres : santé de l'{kind} {NameOf(a.Uid)} (uid {a.Uid}, camp {a.Side}) {from} -> {to} % juste après {gated} impact(s) neutralisé(s), sans autre impact vu");
            }
            else if (LeurresMesure.DecoyWindowActive)
            {
                _pNoHitWindow++;
                _noHitWindowTotal++;
                Line(LineGate, $"santé de l'{kind} {NameOf(a.Uid)} (uid {a.Uid}, camp {a.Side}) {from} -> {to} % sans impact vu, pendant qu'un missile infrarouge face aux leurres était en vol");
            }
        }

        static void HeliLost(int uid, (int hp, int side) last, bool dead)
        {
            if (!_heliHp.Remove(uid)) return;
            if (dead) _heliDead++; else _heliGone++;
            int total = _hitsTotal.TryGetValue(uid, out var n) ? n : 0;
            Line(LineGone, $"hélico {NameOf(uid)} (uid {uid}, camp {last.side}) {(dead ? "détruit" : "n'est plus sur la carte (parti, embarqué ou détruit sans trace)")} ; dernière santé lue {last.hp} % ; impacts vus par le mod sur lui : {total}");
            _hitsSincePoll.Remove(uid);
            _gatedSincePoll.Remove(uid);
        }

        // ---------------------------------------------------------------- queue drain and hit analysis (main thread)
        static void Drain(float now)
        {
            long head = Interlocked.Read(ref _evHead);
            if (head == _evRead) return;
            if (head - _evRead > QueueSize) { _evLost += head - _evRead - QueueSize; _evRead = head - QueueSize; }
            var cases = new Dictionary<(long, int), HitCase>();
            var order = new List<HitCase>();
            while (_evRead < head)
            {
                long n = _evRead + 1;
                int slot = (int)((n - 1) & (QueueSize - 1));
                long s1 = _ev[slot].Seq;
                if (s1 < n)
                {
                    if (head - n < 64) break;                                    // still being written by a hook: next frame
                    _evLost++; _evRead = n; continue;
                }
                var e = _ev[slot];
                if (s1 > n || _ev[slot].Seq != n) { _evLost++; _evRead = n; continue; }   // overwritten meanwhile
                _evRead = n;
                if (e.Kind == EvGated) { NoteGated(e); continue; }
                if (e.Kind == EvPlaneDamage)
                {
                    if (_uidByEid.TryGetValue(e.Victim, out int pu)) _planeHitsSincePoll[pu] = (_planeHitsSincePoll.TryGetValue(pu, out var ph) ? ph : 0) + 1;
                    continue;
                }
                Collect(e, cases, order);
            }
            foreach (var c in order) Analyse(c, now);
        }

        static void NoteGated(Ev e)
        {
            _pGated++;
            _gatedTotal++;
            if (!_uidByEid.TryGetValue(e.Victim, out int uid)) { Line(LineGate, $"impact neutralisé (missile leurré) sur l'entité {e.Victim} : munition {AmmoName(e.A)} ({e.A}), dégâts annulés {e.F.ToString("0.##", Inv)}"); return; }
            _gatedSincePoll[uid] = (_gatedSincePoll.TryGetValue(uid, out var g) ? g : 0) + 1;
            Line(LineGate, $"impact neutralisé (missile leurré) sur {NameOf(uid)} (uid {uid}) : munition {AmmoName(e.A)} ({e.A}), dégâts annulés {e.F.ToString("0.##", Inv)}");
        }

        static void Collect(Ev e, Dictionary<(long, int), HitCase> cases, List<HitCase> order)
        {
            HitCase c = null;
            if (e.Kind == EvStat && e.Hit == 0)
            {
                // statistics outside an impact: attach to the last hit on the same helicopter in this batch
                for (int i = order.Count - 1; i >= 0; i--) if (order[i].VictimEid == e.Victim && !order[i].HasStat) { c = order[i]; break; }
            }
            if (c == null && !cases.TryGetValue((e.Hit, e.Victim), out c))
            {
                c = new HitCase { Hit = e.Hit, VictimEid = e.Victim, Thread = e.Thread };
                if (e.Hit != 0) cases[(e.Hit, e.Victim)] = c;
                order.Add(c);
            }
            switch (e.Kind)
            {
                case EvDamage: c.HasDamage = true; c.Ammo = e.A; c.Dmg += e.F; break;
                case EvStat: if (!c.HasStat) { c.HasStat = true; c.StatId = e.A; c.StatDmg = e.F; } break;
                case EvArmor: if (!c.HasArmor) { c.HasArmor = true; c.ArmorEid = e.A; } break;
            }
        }

        static void Analyse(HitCase c, float now)
        {
            if (!c.HasDamage)
            {
                if (c.HasStat) { _statNoHit++; if (_heliSide.TryGetValue(c.VictimEid, out int s)) ClassifyStat(c.StatId, s, out _); }
                return;
            }
            _heliHits++;
            if (!_uidByEid.TryGetValue(c.VictimEid, out int vUid) || !_units.TryGetValue(vUid, out var victim)) { _victimUnknown++; return; }
            int vSide = victim.Side;
            V3 vPos = victim.Pos;
            try { vPos = victim.U.GetPosition(); } catch { }
            _hitsSincePoll[vUid] = (_hitsSincePoll.TryGetValue(vUid, out var hs) ? hs : 0) + 1;
            _hitsTotal[vUid] = (_hitsTotal.TryGetValue(vUid, out var ht) ? ht : 0) + 1;
            if (_ammoHits.Count < 500 || _ammoHits.ContainsKey(c.Ammo)) _ammoHits[c.Ammo] = (_ammoHits.TryGetValue(c.Ammo, out var ah) ? ah : 0) + 1;
            try { PrecisionHeli.NoteHit(c.Ammo, c.VictimEid); } catch { }   // helicopter-only copy rows: hit counted by class, band and aspect

            // source 1: damage statistics id
            UnitInfo statUnit = null;
            string statTxt = "absente";
            if (c.HasStat) { _hitsWithStat++; statTxt = ClassifyStat(c.StatId, vSide, out statUnit); }

            // source 2: shooter entity of the shot vector
            UnitInfo armorUnit = null;
            string armorTxt = "absent";
            if (c.HasArmor)
            {
                _hitsWithArmor++;
                if (_uidByEid.TryGetValue(c.ArmorEid, out int au) && _units.TryGetValue(au, out var ai))
                {
                    if (ai.Uid == vUid) { _armorVictim++; armorTxt = "l'hélico lui-même"; }
                    else if (ai.Side == vSide) { _armorSame++; armorTxt = $"unité du même camp {NameOf(ai.Uid)} (uid {ai.Uid})"; }
                    else if (IsGround(ai.Type)) { _armorEnemyGround++; armorUnit = ai; armorTxt = $"unité au sol ennemie {NameOf(ai.Uid)} (uid {ai.Uid})"; }
                    else { _armorEnemyOther++; armorTxt = $"appareil ennemi {NameOf(ai.Uid)} (uid {ai.Uid})"; }
                }
                else { _armorUnknown++; armorTxt = $"entité {c.ArmorEid} inconnue (obus ?)"; }
            }

            // source 3: nearest enemy ground unit, preferring one whose loadout has that ammo
            UnitInfo nearest = null, withAmmo = null;
            float dNearest = float.MaxValue, dAmmo = float.MaxValue;
            foreach (var g in _ground[1 - vSide])
            {
                float d = V3.Distance(g.Pos, vPos);
                if (d > SuspectRange) continue;
                if (d < dNearest) { nearest = g; dNearest = d; }
                if (d < dAmmo && HasAmmo(g.UnitId, c.Ammo)) { withAmmo = g; dAmmo = d; }
            }
            var suspect = withAmmo ?? nearest;
            if (suspect != null) _suspectFound++;
            if (withAmmo != null) _suspectWithAmmo++;
            if (statUnit != null && suspect != null) { _agreeStatN++; if (statUnit.Uid == suspect.Uid) _agreeStat++; }
            if (armorUnit != null && suspect != null) { _agreeArmorN++; if (armorUnit.Uid == suspect.Uid) _agreeArmor++; }

            var shooter = statUnit ?? armorUnit ?? suspect;
            string source = statUnit != null ? "statistique" : armorUnit != null ? "vecteur de tir" : suspect != null ? (withAmmo != null ? "suspect avec cette munition" : "suspect le plus proche") : null;
            float dist = -1f;
            if (shooter != null)
            {
                dist = V3.Distance(shooter.Pos, vPos);
                _distBand[dist < 500f ? 0 : dist < 1000f ? 1 : dist < 2000f ? 2 : dist < 3000f ? 3 : 4]++;
                StartCheck(shooter, source, victim, c.Ammo, dist, now);
                if (IsGround(shooter.Type) && HasAmmo(shooter.UnitId, c.Ammo)) PrecisionHit(shooter, c.Ammo, dist, now);
            }

            string suspectTxt = suspect == null ? $"aucun à {SuspectRange:0} m" : $"{NameOf(suspect.Uid)} (uid {suspect.Uid}) à {V3.Distance(suspect.Pos, vPos):0} m{(withAmmo != null ? ", cette munition dans son chargement" : ", sans cette munition dans son chargement")}";
            Line(LineHit, $"impact sur l'hélico {NameOf(vUid)} (uid {vUid}, camp {vSide}) : munition {AmmoName(c.Ammo)} ({c.Ammo}), dégâts {c.Dmg:0.##}" +
                $" ; statistique : {statTxt} ; vecteur de tir : {armorTxt} ; suspect : {suspectTxt}" +
                (shooter != null ? $" ; tireur retenu {NameOf(shooter.Uid)} (uid {shooter.Uid}, {source}, {dist:0} m)" : " ; aucun tireur retenu") +
                (c.Thread != _mainThread ? " ; hors fil principal" : ""));
        }

        /// Classifies a CollectStatistic id against the victim's side (unit UID, owner player UID or EntityId; several may match).
        static string ClassifyStat(int id, int vSide, out UnitInfo enemyUnit)
        {
            enemyUnit = null;
            var parts = new List<string>();
            if (_units.TryGetValue(id, out var su))
            {
                if (su.Side != vSide) { _statEnemyUnit++; enemyUnit = su; parts.Add($"uid d'une unité ennemie {NameOf(su.Uid)}"); }
                else { _statSameUnit++; parts.Add($"uid d'une unité du même camp {NameOf(su.Uid)}"); }
            }
            if (_playerSide.TryGetValue(id, out int ps))
            {
                if (ps != vSide) { _statEnemyPlayer++; parts.Add("uid d'un joueur ennemi"); }
                else { _statSamePlayer++; parts.Add("uid d'un joueur du même camp"); }
            }
            if (_uidByEid.ContainsKey(id)) { _statEntity++; parts.Add("id d'entité d'une unité"); }
            if (parts.Count == 0) { _statUnknown++; return $"id {id} inconnu"; }
            return $"id {id} = " + string.Join(" / ", parts);
        }

        // ---------------------------------------------------------------- shooter visibility after a hit
        static void StartCheck(UnitInfo shooter, string source, UnitInfo victim, int ammo, float dist, float now)
        {
            if (_checks.Count >= MaxChecks || _checkedShooters.Contains(shooter.Uid)) { _checksSkipped++; return; }
            _checkedShooters.Add(shooter.Uid);
            _checks.Add(new VisCheck
            {
                ShooterUid = shooter.Uid, ShooterSide = shooter.Side, VictimUid = victim.Uid, Ammo = ammo, Source = source,
                ShooterName = NameOf(shooter.Uid), VictimName = NameOf(victim.Uid), U = shooter.U, T0 = now, Dist = dist
            });
        }

        static void RunChecks(float now)
        {
            for (int i = _checks.Count - 1; i >= 0; i--)
            {
                var c = _checks[i];
                while (c.Step < 4 && now >= c.T0 + VisDelays[c.Step]) Probe(c);
                if (c.Step < 4) continue;
                _checks.RemoveAt(i);
                _checkedShooters.Remove(c.ShooterUid);
                LogCheck(c, "");
            }
        }

        static void Probe(VisCheck c)
        {
            string r;
            try
            {
                if (c.U == null || !c.U.IsAlive()) { r = "mort"; _visDeadAt[c.Step]++; }
                else if (c.U.Entity.Has<FowVisibleMarker>()) { r = "oui"; _visYes[c.Step]++; }
                else { r = "non"; _visNo[c.Step]++; }
            }
            catch { r = "?"; }
            c.Res[c.Step] = r;
            c.Step++;
        }

        static void LogCheck(VisCheck c, string suffix)
        {
            var sb = new StringBuilder($"tireur {c.ShooterName} (uid {c.ShooterUid}, camp {c.ShooterSide}, {c.Source}) de l'hélico {c.VictimName} (uid {c.VictimUid}) à {c.Dist:0} m, munition {c.Ammo} : marqueur visible");
            for (int k = 0; k < 4; k++) sb.Append($" +{VisDelays[k].ToString("0.#", Inv)} s {c.Res[k] ?? "non lu"}{(k < 3 ? "," : "")}");
            Line(LineVis, sb + suffix);
        }

        /// End of battle: pending checks are written with what was read so far.
        static void FlushChecks()
        {
            foreach (var c in _checks) LogCheck(c, " (bataille terminée avant la fin)");
            _checks.Clear(); _checkedShooters.Clear();
        }

        static void Line(int kind, string msg)
        {
            if (_linesLeft[kind] <= 0) { _linesSuppressed[kind]++; return; }
            _linesLeft[kind]--;
            Log(msg);
        }

        // ---------------------------------------------------------------- fog-of-war sampling window
        static void FogWindow(float now)
        {
            if (_fowState == 0)
            {
                if (_fowBroken || now - _battleStart < FogStart) return;
                _fowState = 1;
                ResetFogCounters();
                _fowHarmony ??= new HarmonyLib.Harmony("RealismOverhaul.AntiHeliTouches.Brouillard");
                _fowOff = false;
                int ok = Patch(_fowHarmony, "brouillard : portée de vue vers une cible", AccessTools.Method(typeof(FowUnit), "CalculateVisionRangeForTarget"), nameof(VisionPostfix))
                       + Patch(_fowHarmony, "brouillard : distance de vue maximale", AccessTools.Method(typeof(FowUnit), "GetMaxOpticsCheckDistance"), nameof(OpticsPostfix));
                if (ok == 0)
                {
                    _fowOff = true; _fowState = 2;
                    Log("brouillard : aucun point d'observation installable, pas d'échantillon");
                    return;
                }
                _fowUntil = now + FogLength;
                Log($"brouillard : {ok}/2 point(s) d'observation installé(s) pour {FogLength:0} s");
            }
            else if (_fowState == 1 && now >= _fowUntil) CloseFog("fin de la fenêtre de 20 s");
        }

        static void CloseFog(string why)
        {
            if (_fowState != 1) return;
            _fowOff = true;
            _fowState = 2;
            try { _fowHarmony?.UnpatchSelf(); }
            catch (Exception e)
            {
                _fowBroken = true;                                               // postfixes stay installed but return at once
                Log($"brouillard : retrait des points d'observation impossible ({e.GetBaseException().Message}), ils restent muets jusqu'à la fermeture du jeu");
            }
            try { ReportFog(why); } catch (Exception e) { Log("brouillard : bilan illisible (" + e.Message + ")"); }
        }

        static void ResetFogCounters()
        {
            Interlocked.Exchange(ref _visCalls, 0); Interlocked.Exchange(ref _visOffMain, 0); Interlocked.Exchange(ref _visHeliChecker, 0); Interlocked.Exchange(ref _visHeliCheckerSum, 0);
            Interlocked.Exchange(ref _visOther, 0); Interlocked.Exchange(ref _visOtherSum, 0); Interlocked.Exchange(ref _visHeliTarget, 0); Interlocked.Exchange(ref _fowErrors, 0);
            Interlocked.Exchange(ref _optCalls, 0); Interlocked.Exchange(ref _optOffMain, 0); Interlocked.Exchange(ref _optHeli, 0); Interlocked.Exchange(ref _optHeliSum, 0);
            Interlocked.Exchange(ref _optHeliAmmoSum, 0); Interlocked.Exchange(ref _optOther, 0); Interlocked.Exchange(ref _optOtherSum, 0);
            Interlocked.Exchange(ref _vsN, 0); Interlocked.Exchange(ref _vtN, 0); Interlocked.Exchange(ref _osN, 0); Interlocked.Exchange(ref _ogN, 0);
        }

        static string Who(int eid)
        {
            if (!_uidByEid.TryGetValue(eid, out int uid)) return $"entité {eid}";
            return $"{NameOf(uid)} (uid {uid})";
        }

        static string Dist(int eidA, int eidB)
        {
            if (_uidByEid.TryGetValue(eidA, out int a) && _uidByEid.TryGetValue(eidB, out int b) && _units.TryGetValue(a, out var ua) && _units.TryGetValue(b, out var ub))
                return $" à {V3.Distance(ua.Pos, ub.Pos):0} m";
            return "";
        }

        static void ReportFog(string why)
        {
            long vis = Interlocked.Read(ref _visCalls), visHeli = Interlocked.Read(ref _visHeliChecker), visOther = Interlocked.Read(ref _visOther);
            var sb = new StringBuilder($"brouillard ({why}) : portée de vue vers une cible : {vis} appels (hors fil principal {Interlocked.Read(ref _visOffMain)})");
            sb.Append($", hélico observateur {visHeli} (moyenne {(visHeli > 0 ? Interlocked.Read(ref _visHeliCheckerSum) / visHeli : 0)} m)");
            sb.Append($", autres observateurs {visOther} (moyenne {(visOther > 0 ? Interlocked.Read(ref _visOtherSum) / visOther : 0)} m)");
            sb.Append($", hélico observé {Interlocked.Read(ref _visHeliTarget)}");
            int ns = (int)Math.Min(Interlocked.Read(ref _vsN), NSamples);
            for (int k = 0; k < ns; k++) sb.Append($" ; {Who(_vsChecker[k])} voit {Who(_vsTarget[k])}{Dist(_vsChecker[k], _vsTarget[k])} jusqu'à {_vsValue[k]:0} m");
            int nt = (int)Math.Min(Interlocked.Read(ref _vtN), NSamples);
            for (int k = 0; k < nt; k++) sb.Append($" ; {Who(_vtChecker[k])} voit l'hélico {Who(_vtTarget[k])}{Dist(_vtChecker[k], _vtTarget[k])} jusqu'à {_vtValue[k]:0} m");
            Log(sb.ToString());

            long opt = Interlocked.Read(ref _optCalls), optHeli = Interlocked.Read(ref _optHeli), optOther = Interlocked.Read(ref _optOther);
            var o = new StringBuilder($"brouillard ({why}) : distance de vue maximale : {opt} appels (hors fil principal {Interlocked.Read(ref _optOffMain)})");
            o.Append($", hélico {optHeli} (optique moyenne {(optHeli > 0 ? Interlocked.Read(ref _optHeliSum) / optHeli : 0)} m, arme moyenne {(optHeli > 0 ? Interlocked.Read(ref _optHeliAmmoSum) / optHeli : 0)} m)");
            o.Append($", autres {optOther} (optique moyenne {(optOther > 0 ? Interlocked.Read(ref _optOtherSum) / optOther : 0)} m)");
            int no = (int)Math.Min(Interlocked.Read(ref _osN), NSamples);
            for (int k = 0; k < no; k++) o.Append($" ; {Who(_osEid[k])} optique {_osOptics[k]:0} m, arme {_osAmmo[k]:0} m");
            int ng = (int)Math.Min(Interlocked.Read(ref _ogN), NSamples);
            for (int k = 0; k < ng; k++) o.Append($" ; {Who(_ogEid[k])} optique {_ogOptics[k]:0} m, arme {_ogAmmo[k]:0} m");
            o.Append($" ; erreurs {Interlocked.Read(ref _fowErrors)}");
            Log(o.ToString());
        }

        // ---------------------------------------------------------------- report (every 30 s, main thread)
        static string Pct(long a, long n) => n == 0 ? "-" : $"{a}/{n}";

        static void Report(bool final)
        {
            long heliHitsDepth = Interlocked.Read(ref _dmgHeli), statCalls = Interlocked.Read(ref _statCalls), statHeli = Interlocked.Read(ref _statHeli);
            if (!final && _heliHits + heliHitsDepth + statHeli + _hpDrops + _heliDead + _heliGone + _gatedTotal + _planeDrops == 0 && _heliHp.Count + _planeHp.Count == 0) return;
            int h0 = _heliHp.Values.Count(v => v.side == 0);
            var sb = new StringBuilder();
            sb.Append($"{(final ? "bilan" : "relevé")} : hélicos suivis camp 0 {h0}, camp 1 {_heliHp.Count - h0} ; impacts sur hélicos {_heliHits} (vus dans le calcul des dégâts {heliHitsDepth}, hors fil principal {Interlocked.Read(ref _dmgOffMain)}, hélico non retrouvé {_victimUnknown})");
            sb.Append($" ; calculs de dégâts pendant un impact {Interlocked.Read(ref _dmgInHit)}");
            if (_ammoHits.Count > 0)
                sb.Append(" ; munitions : " + string.Join(", ", _ammoHits.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{AmmoName(kv.Key)} ({kv.Key}) x{kv.Value}")));
            sb.Append($" ; baisses de santé vues {_hpDrops} (sans impact vu {_hpDropsNoHit}), hélicos détruits {_heliDead}, disparus de la carte {_heliGone}");
            sb.Append($" ; avions suivis {_planeHp.Count}, baisses de santé d'avion {_planeDrops} (sans impact vu {_planeDropsNoHit})");
            sb.Append($" ; leurres : impacts neutralisés sur appareil {_gatedTotal} (événements {Interlocked.Read(ref _gatedEvents)}), baisses de santé juste après un impact neutralisé {_afterGateTotal}, sans impact pendant un missile face aux leurres {_noHitWindowTotal}");
            if (!_depthSeen) sb.Append(" ; profondeur d'impact indisponible");
            string l1 = sb.ToString();

            var s2 = new StringBuilder("sources du tireur : ");
            s2.Append($"statistique {statCalls} appels (sur hélico {statHeli}, hors fil principal {Interlocked.Read(ref _statOffMain)}, avec un impact {_hitsWithStat}, sans impact vu {_statNoHit})");
            s2.Append($" -> unité ennemie {_statEnemyUnit}, unité du même camp {_statSameUnit}, joueur ennemi {_statEnemyPlayer}, joueur du même camp {_statSamePlayer}, id d'entité {_statEntity}, inconnu {_statUnknown}");
            s2.Append($" ; vecteur de tir {Interlocked.Read(ref _armorInHit)} appels pendant un impact (sur hélico {Interlocked.Read(ref _armorHeli)}, hors fil principal {Interlocked.Read(ref _armorOffMain)}, avec un impact {_hitsWithArmor})");
            s2.Append($" -> unité au sol ennemie {_armorEnemyGround}, appareil ennemi {_armorEnemyOther}, même camp {_armorSame}, l'hélico lui-même {_armorVictim}, inconnu {_armorUnknown}");
            s2.Append($" ; suspect à {SuspectRange:0} m trouvé {Pct(_suspectFound, _heliHits)} (avec cette munition {_suspectWithAmmo})");
            s2.Append($" ; accord statistique/suspect {Pct(_agreeStat, _agreeStatN)}, vecteur/suspect {Pct(_agreeArmor, _agreeArmorN)}");
            s2.Append($" ; distance du tireur retenu : <500 m {_distBand[0]}, 500-1000 m {_distBand[1]}, 1-2 km {_distBand[2]}, 2-3 km {_distBand[3]}, >3 km {_distBand[4]}");
            string l2 = s2.ToString();

            var s3 = new StringBuilder("marqueur visible sur le tireur retenu :");
            for (int k = 0; k < 4; k++) s3.Append($" +{VisDelays[k].ToString("0.#", Inv)} s oui {_visYes[k]} / non {_visNo[k]} / mort {_visDeadAt[k]}{(k < 3 ? " ;" : "")}");
            s3.Append($" ; vérifications en cours {_checks.Count}, non lancées {_checksSkipped}");
            s3.Append($" ; lignes non écrites : impacts {_linesSuppressed[LineHit]}, santé {_linesSuppressed[LineHp]}, visibilité {_linesSuppressed[LineVis]}, pertes {_linesSuppressed[LineGone]}, leurres {_linesSuppressed[LineGate]}");
            s3.Append($" ; distance entre les camps {(ContactMeters < 0f ? "inconnue" : ContactMeters >= 1e8f ? "un camp sans unité" : ContactMeters.ToString("0", Inv) + " m")}");
            s3.Append($" ; événements perdus {_evLost}, erreurs de lecture {_snapErrors}, erreurs {Interlocked.Read(ref _errors)}");
            string l3 = s3.ToString();

            string all = l1 + l2 + l3;
            if (!final && all == _lastReport) return;
            _lastReport = all;
            Log(l1); Log(l2); Log(l3);
        }

        static void ClearBattle()
        {
            Interlocked.Exchange(ref _dmgInHit, 0); Interlocked.Exchange(ref _dmgHeli, 0); Interlocked.Exchange(ref _dmgOffMain, 0);
            Interlocked.Exchange(ref _statCalls, 0); Interlocked.Exchange(ref _statHeli, 0); Interlocked.Exchange(ref _statOffMain, 0);
            Interlocked.Exchange(ref _armorInHit, 0); Interlocked.Exchange(ref _armorHeli, 0); Interlocked.Exchange(ref _armorOffMain, 0); Interlocked.Exchange(ref _errors, 0);
            Interlocked.Exchange(ref _gatedEvents, 0);
            _evRead = Interlocked.Read(ref _evHead);
            _evLost = 0;
            _heliHits = _victimUnknown = _hitsWithStat = _statEnemyUnit = _statSameUnit = _statEnemyPlayer = _statSamePlayer = _statEntity = _statUnknown = _statNoHit = 0;
            _hitsWithArmor = _armorEnemyGround = _armorEnemyOther = _armorSame = _armorVictim = _armorUnknown = 0;
            _suspectFound = _suspectWithAmmo = _agreeStatN = _agreeStat = _agreeArmorN = _agreeArmor = _checksSkipped = _snapErrors = 0;
            _hpDrops = _hpDropsNoHit = _heliDead = _heliGone = 0;
            _gatedTotal = _afterGateTotal = _noHitWindowTotal = _planeDrops = _planeDropsNoHit = 0;
            _depthSeen = false;
            Array.Clear(_distBand, 0, _distBand.Length);
            Array.Clear(_visYes, 0, 4); Array.Clear(_visNo, 0, 4); Array.Clear(_visDeadAt, 0, 4);
            Array.Clear(_linesSuppressed, 0, _linesSuppressed.Length);
            _units = new Dictionary<int, UnitInfo>(); _uidByEid = new Dictionary<int, int>(); _playerSide = new Dictionary<int, int>();
            _ground = new[] { new List<UnitInfo>(), new List<UnitInfo>() };
            _heliHp.Clear(); _planeHp.Clear(); _hitsSincePoll.Clear(); _hitsTotal.Clear(); _ammoHits.Clear(); _planeHitsSincePoll.Clear(); _gatedSincePoll.Clear();
            _checks.Clear(); _checkedShooters.Clear();
            _lastReport = null;
            _helisLogged = false; _depthWarned = false;
            _map = null;
            ClearPrecision();
        }

        // ================================================================ [PRECISION] gun accuracy against helicopters (measurement only)
        const int NBands = 13;                                      // 250 m bands up to 3000 m, then beyond
        const int MaxReadsPerTick = 60, MaxFilterChecks = 8;
        const float ReadGap = 3.5f, PureWindow = 5f;
        // helicopter-only per-unit copies of a gun row (Id = CopyIdBase + source Id): every round they fire goes to a helicopter
        const int CopyIdBase = 20000, CopyMissReads = 3;
        const float CopyProbeEvery = 60f;
        const long GroundTargetBits = (long)AmmoTarget.Ground | (long)AmmoTarget.Infantry | (long)AmmoTarget.Vehicle | (long)AmmoTarget.Ship;
        static readonly int[] PrecisionRefIds = { 19, 70, 163, 204, 176, 267, 1, 6 };
        static readonly int[] PrecisionUnitRows = { 263, 46, 270, 145, 214, 47 };     // Mi-35M, Ka-52, AH-64E, Mi-8AMTSh, BMP-2, Humvee (reference ids, used if present)
        static readonly float[] PrecisionDistances = { 100f, 300f, 500f, 800f, 1000f, 1500f, 2000f, 2500f, 3000f };

        sealed class GunRow
        {
            public int Id, Source;                                  // Source: gun row this helicopter-only copy was made from (0 otherwise)
            public string Name, Hud; public float Low, Ground; public bool HeliOnly; public AmmoFilterData ByName, ByHud;
        }
        sealed class PrecAgg { public readonly long[] Shots = new long[NBands], Hits = new long[NBands], HitsAll = new long[NBands], AmbBand = new long[NBands]; public long Ambiguous, TowardGround; }

        static MelonPreferences_Entry<string> PrecCallPref => _precCall;
        static bool _precOff, _precStaticDone, _altBroken;
        static int _precErrors, _readRound, _filterMode;             // filter mode: 0 unknown, 1 ammunition name, 2 HUD name, -1 not per row
        static float _precNextTick, _precNextReport;
        static long _precReads, _precReadErrors, _precInfinite, _precGapShots, _precNoFilter;
        static Dictionary<int, GunRow> _guns;
        static Dictionary<int, int[]> _gunsByUnit, _allAmmoByUnit;
        static Dictionary<int, HashSet<int>> _gunsByUnitBase, _allAmmoByUnitBase;   // database loadouts without the copies
        static Dictionary<int, int> _quantityByUnit;
        static Dictionary<int, int> _copyOfSource = new(), _sourceOfCopy = new();   // helicopter-only copies found in the database
        static IntPtr _gunsSrc;
        static float _copyNextProbe;
        static bool _copyFilterChecked;
        static int _copyFilterTries;
        static readonly Dictionary<long, int> _copyMiss = new();       // (unit uid << 32 | copy Id) -> unreadable reads (the unit has no copy)
        static readonly HashSet<long> _copySeen = new();               // (unit uid << 32 | copy Id) read at least once: the unit really carries it
        static readonly Dictionary<long, (int count, float t)> _lastAmmo = new();
        static readonly Dictionary<int, float> _pureUntil = new();
        static readonly Dictionary<int, PrecAgg> _prec = new();
        static readonly HashSet<int> _filterChecked = new();
        static readonly HashSet<string> _precOnce = new();
        static readonly Dictionary<int, bool> _airborne = new();
        static Il2CppSystem.Collections.Generic.List<int> _il2One;
        static Il2CppSystem.Collections.Generic.IReadOnlyList<int> _il2OneRo;
        static AmmoFilterData _anyFilter;
        static string _lastPrecReport;

        static void LogPrec(string s) => Mod.Log.Msg("[PRECISION] " + s);
        static void LogPrecOnce(string key, string s) { if (_precOnce.Add(key)) LogPrec(s); }

        static void ClearPrecision()
        {
            _precOff = false; _precStaticDone = false; _precErrors = 0; _readRound = 0;
            _precNextTick = 0f;
            _precReads = _precReadErrors = _precInfinite = _precGapShots = _precNoFilter = 0;
            _lastAmmo.Clear(); _pureUntil.Clear(); _prec.Clear(); _precOnce.Clear(); _airborne.Clear();
            _copyMiss.Clear(); _copySeen.Clear(); _copyFilterChecked = false; _copyFilterTries = 0; _copyNextProbe = 0f;
            _lastPrecReport = null;
        }

        static int Band(float d) => d < 0f ? NBands - 1 : Math.Min(NBands - 1, (int)(d / 250f));
        static float Dist3(V3 a, V3 b) { double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z; return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
        static string BandName(int b) => b >= NBands - 1 ? "plus de 3000 m" : $"{b * 250}-{(b + 1) * 250} m";

        static PrecAgg Agg(int ammo)
        {
            if (!_prec.TryGetValue(ammo, out var a)) _prec[ammo] = a = new PrecAgg();
            return a;
        }

        /// Unguided direct-fire rows able to target helicopters (the rows the anti-helicopter dispersion work is about), and per unit type
        /// its gun rows, all its ammunition rows and its total loadout quantity. Once per database; the helicopter-only copies of the gun
        /// rows (Id CopyIdBase + source) are looked up again every 60 s, since they may be created after the first build.
        static bool EnsureGuns(float now)
        {
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return false;
            if (_guns != null && src.Pointer == _gunsSrc)
            {
                if (now >= _copyNextProbe) { _copyNextProbe = now + CopyProbeEvery; ProbeCopies(src); }
                return true;
            }
            _gunsSrc = src.Pointer;
            var guns = new Dictionary<int, GunRow>();
            foreach (var a in Props.Rows(src.Ammunitions.GetAll()))
            {
                try
                {
                    if (a == null || a.Id > CopyIdBase) continue;                      // copies are found by ProbeCopies only
                    long tt = (long)a.TargetType;
                    if ((tt & (long)AmmoTarget.Helicopter) == 0) continue;
                    if (a.Seeker != SeekerType.None || a.TrajectoryType != Trajectory.DirectShot) continue;
                    float low = a.LowAltRange;
                    if (!(low > 0f)) continue;
                    guns[a.Id] = new GunRow { Id = a.Id, Name = a.Name ?? "", Hud = a.HUDName ?? "", Low = low, Ground = a.GroundRange, HeliOnly = (tt & GroundTargetBits) == 0 };
                }
                catch { }
            }
            var gunsByUnit = new Dictionary<int, HashSet<int>>();
            var allByUnit = new Dictionary<int, HashSet<int>>();
            var qty = new Dictionary<int, int>();
            foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
            {
                if (wa == null) continue;
                if (!allByUnit.TryGetValue(wa.UnitId, out var all)) allByUnit[wa.UnitId] = all = new HashSet<int>();
                all.Add(wa.AmmunitionId);
                qty[wa.UnitId] = (qty.TryGetValue(wa.UnitId, out int q) ? q : 0) + wa.Quantity;
                if (!guns.ContainsKey(wa.AmmunitionId)) continue;
                if (!gunsByUnit.TryGetValue(wa.UnitId, out var set)) gunsByUnit[wa.UnitId] = set = new HashSet<int>();
                set.Add(wa.AmmunitionId);
            }
            _guns = guns;
            _gunsByUnitBase = gunsByUnit;
            _allAmmoByUnitBase = allByUnit;
            _quantityByUnit = qty;
            _copyOfSource = new Dictionary<int, int>(); _sourceOfCopy = new Dictionary<int, int>();
            _copyMiss.Clear(); _copySeen.Clear(); _copyFilterChecked = false; _copyFilterTries = 0;
            BuildUnitGuns();
            LogPrec($"munitions sans guidage à tir direct capables de viser un hélico : {guns.Count} ; types d'unités qui en portent : {_gunsByUnit.Count}");
            _copyNextProbe = now + CopyProbeEvery;
            ProbeCopies(src);
            return true;
        }

        /// Helicopter-only copies (Id CopyIdBase + source) of the dual-target gun rows present in the database: unguided direct fire,
        /// Helicopter bit and no ground bit. Rebuilds the per-unit lists only when the set changed. Main thread.
        static void ProbeCopies(Il2CppBrokenArrow.DataBase.DataBaseSourceData src)
        {
            var found = new Dictionary<int, GunRow>();
            foreach (var g in _guns.Values)
            {
                if (g.Source != 0 || g.HeliOnly || g.Id <= 0 || g.Id >= CopyIdBase) continue;
                try
                {
                    if (!src.Ammunitions.TryGetById(CopyIdBase + g.Id, out var c) || c == null) continue;
                    long tt = (long)c.TargetType;
                    if ((tt & (long)AmmoTarget.Helicopter) == 0 || (tt & GroundTargetBits) != 0) continue;
                    if (c.Seeker != SeekerType.None || c.TrajectoryType != Trajectory.DirectShot) continue;
                    float low = c.LowAltRange;
                    if (!(low > 0f)) continue;
                    found[c.Id] = new GunRow { Id = c.Id, Source = g.Id, Name = c.Name ?? "", Hud = c.HUDName ?? "", Low = low, Ground = c.GroundRange, HeliOnly = true };
                }
                catch { }
            }
            bool same = found.Count == _sourceOfCopy.Count;
            if (same) foreach (var kv in found) if (!_sourceOfCopy.TryGetValue(kv.Key, out int s) || s != kv.Value.Source) { same = false; break; }
            if (same) return;
            foreach (int old in _sourceOfCopy.Keys) _guns.Remove(old);
            var copyOf = new Dictionary<int, int>(); var sourceOf = new Dictionary<int, int>();
            foreach (var kv in found) { _guns[kv.Key] = kv.Value; copyOf[kv.Value.Source] = kv.Key; sourceOf[kv.Key] = kv.Value.Source; }
            _copyOfSource = copyOf; _sourceOfCopy = sourceOf;
            _copyMiss.Clear(); _copySeen.Clear(); _copyFilterChecked = false; _copyFilterTries = 0;
            BuildUnitGuns();
            LogPrec($"copies anti-hélico des munitions trouvées dans la base : {found.Count}" +
                    (found.Count > 0 ? $" ({string.Join(", ", found.Values.Take(20).Select(r => $"{r.Id} de {r.Source}"))}{(found.Count > 20 ? ", ..." : "")}) ; chaque tir d'une copie est compté vers l'hélico ennemi en vol le plus proche" : ""));
        }

        /// Per unit type: its gun rows and all its ammunition rows, plus the helicopter-only copy of every gun row it carries (a unit of that
        /// type may still lack the copy: its unreadable count is then skipped after a few reads).
        static void BuildUnitGuns()
        {
            var guns = new Dictionary<int, int[]>(_gunsByUnitBase.Count);
            var all = new Dictionary<int, int[]>(_allAmmoByUnitBase.Count);
            foreach (var kv in _allAmmoByUnitBase)
            {
                _gunsByUnitBase.TryGetValue(kv.Key, out var gset);
                List<int> extra = null;
                if (gset != null && _copyOfSource.Count > 0)
                    foreach (int id in gset) if (_copyOfSource.TryGetValue(id, out int copy)) (extra ??= new List<int>()).Add(copy);
                if (gset != null) guns[kv.Key] = extra == null ? gset.ToArray() : gset.Concat(extra).ToArray();
                all[kv.Key] = extra == null ? kv.Value.ToArray() : kv.Value.Concat(extra).ToArray();
            }
            _gunsByUnit = guns;
            _allAmmoByUnit = all;
        }

        static AmmoFilterData AnyFilter()
        {
            if (_anyFilter != null) return _anyFilter;
            _anyFilter = new AmmoFilterData();
            _anyFilter.Type = NodeAmmoType.Any;
            _anyFilter.SpecificAmmoNameFilter = "";
            return _anyFilter;
        }

        static AmmoFilterData NewFilter(string name)
        {
            var f = new AmmoFilterData();
            f.Type = NodeAmmoType.Any;
            f.SpecificAmmoNameFilter = name ?? "";
            return f;
        }

        static AmmoFilterData RowFilter(GunRow r)
        {
            if (_filterMode == 1) return r.ByName ??= NewFilter(r.Name);
            if (_filterMode == 2) return r.ByHud ??= NewFilter(r.Hud);
            return null;
        }

        /// Ammunition count of one unit for a filter through the game's script bus (the same read MunitionsAir uses). -1 when unreadable.
        static int ReadCount(EcsEventBusGetUnitsAmmo del, int uid, AmmoFilterData filter, out bool infinite)
        {
            infinite = false;
            try
            {
                _il2One ??= new Il2CppSystem.Collections.Generic.List<int>();
                _il2OneRo ??= new Il2CppSystem.Collections.Generic.IReadOnlyList<int>(_il2One.Pointer);
                _il2One.Clear();
                _il2One.Add(uid);
                var d = del.Invoke(_il2OneRo, filter);
                _precReads++;
                infinite = d.InfiniteAmmo;
                return d.AmmoCount;
            }
            catch { _precReadErrors++; return -1; }
        }

        static bool Airborne(UnitInfo h)
        {
            if (_airborne.TryGetValue(h.Uid, out bool v)) return v;
            v = true;
            if (!_altBroken)
            {
                try
                {
                    AltLevel lvl = default;
                    Aircraft.GetCurrentAltitude(h.U.Entity, out lvl);
                    v = lvl != AltLevel.Ground;
                }
                catch (Exception e) { _altBroken = true; LogPrecOnce("alt", "classe d'altitude du jeu illisible (" + e.GetBaseException().Message + ") : tous les hélicos comptés en vol"); }
            }
            _airborne[h.Uid] = v;
            return v;
        }

        /// Every second in battle: static measurement once, then ammunition counts of ground units near an airborne enemy helicopter.
        static void PrecisionTick(float now)
        {
            if (!_precStaticDone && now - _battleStart >= 15f && (_heliSide.Count > 0 || now - _battleStart >= 300f)) PrecisionStatic();
            if (now < _precNextTick) return;
            _precNextTick = now + 1f;
            if (!EnsureGuns(now)) return;
            _airborne.Clear();
            var air = new[] { new List<UnitInfo>(), new List<UnitInfo>() };
            foreach (var u in _units.Values)
                if ((u.Type & 8) != 0 && u.Side >= 0 && u.Side <= 1 && Airborne(u)) air[u.Side].Add(u);
            if (air[0].Count + air[1].Count == 0) return;
            var gc = GameController._instance;
            EcsEventBusGetUnitsAmmo del = null;
            try { del = gc?._GetEcsEventBus_k__BackingField?.Gameplay?.GetUnitsAmmo; } catch { del = null; }
            if (del == null) { LogPrecOnce("bus", "lecture des munitions indisponible (bus du jeu absent) : tirs vers les hélicos non comptés"); return; }

            var cands = new List<(UnitInfo g, float dH, int[] guns)>();
            for (int side = 0; side < 2; side++)
            {
                var enemyAir = air[1 - side];
                if (enemyAir.Count == 0) continue;
                foreach (var g in _ground[side])
                {
                    if (!_gunsByUnit.TryGetValue(g.UnitId, out var guns)) continue;
                    float maxLow = 0f;
                    foreach (int id in guns) if (_guns.TryGetValue(id, out var r) && r.Low > maxLow) maxLow = r.Low;
                    float dH = float.MaxValue;
                    foreach (var h in enemyAir) { float d = Dist3(g.Pos, h.Pos); if (d < dH) dH = d; }
                    if (dH <= maxLow + 500f) cands.Add((g, dH, guns));
                }
            }
            if (cands.Count == 0) return;

            int reads = 0;
            if (_filterMode == 0 && _filterChecked.Count < MaxFilterChecks)
                foreach (var c in cands)
                {
                    if (_filterChecked.Contains(c.g.UnitId)) continue;
                    if (!_allAmmoByUnit.TryGetValue(c.g.UnitId, out var all) || all.Length < 2) continue;
                    _filterChecked.Add(c.g.UnitId);
                    reads += FilterCheck(del, c.g, all, true);
                    break;
                }
            // once per battle (and after the copies change): the name filter on a unit carrying a helicopter-only copy, logged only, so the
            // next battle shows whether the copy and its source row are counted apart
            if (!_copyFilterChecked && _copyFilterTries < MaxFilterChecks && _sourceOfCopy.Count > 0 && _filterMode > 0)
                foreach (var c in cands)
                {
                    if (!_allAmmoByUnit.TryGetValue(c.g.UnitId, out var all)) continue;
                    bool hasCopy = false;
                    foreach (int id in c.guns) if (_sourceOfCopy.ContainsKey(id) && _copySeen.Contains(CopyKey(c.g.Uid, id))) { hasCopy = true; break; }
                    if (!hasCopy) continue;
                    _copyFilterChecked = true;
                    _copyFilterTries++;
                    reads += FilterCheck(del, c.g, all, false);
                    break;
                }

            int start = _readRound % cands.Count, done = 0;
            for (int k = 0; k < cands.Count && reads < MaxReadsPerTick; k++)
            {
                var c = cands[(start + k) % cands.Count];
                done++;
                bool single = _allAmmoByUnit.TryGetValue(c.g.UnitId, out var all) && all.Length == 1;
                foreach (int gunId in c.guns)
                {
                    if (reads >= MaxReadsPerTick) break;
                    if (!_guns.TryGetValue(gunId, out var row)) continue;
                    long key = CopyKey(c.g.Uid, gunId);
                    if (row.Source != 0 && _copyMiss.TryGetValue(key, out int miss) && miss >= CopyMissReads) continue;   // this unit has no copy
                    var filter = single ? AnyFilter() : RowFilter(row);
                    if (filter == null) { _precNoFilter++; continue; }
                    int count = ReadCount(del, c.g.Uid, filter, out bool infinite);
                    reads++;
                    if (infinite) { _precInfinite++; continue; }
                    if (count < 0)
                    {
                        if (row.Source != 0 && !_copySeen.Contains(key) && _copyMiss.Count < 20000) _copyMiss[key] = (_copyMiss.TryGetValue(key, out int m) ? m : 0) + 1;
                        continue;
                    }
                    if (row.Source != 0 && _copySeen.Count < 20000) { _copySeen.Add(key); _copyMiss.Remove(key); }
                    if (_lastAmmo.TryGetValue(key, out var last) && count < last.count)
                    {
                        int shots = last.count - count;
                        if (now - last.t > ReadGap) _precGapShots += shots;
                        else
                        {
                            var agg = Agg(gunId);
                            int band = Band(c.dH);
                            if (row.HeliOnly)
                            {
                                // helicopter-only row (a copy or a vanilla one): every round went to a helicopter, no ground ambiguity;
                                // no airborne enemy helicopter within its range means a landed target or a late position
                                if (c.dH > row.Low + 100f) agg.Ambiguous += shots;
                                else { agg.Shots[band] += shots; _pureUntil[c.g.Uid] = now + PureWindow; }
                            }
                            else if (c.dH > row.Low + 100f) agg.TowardGround += shots;
                            else if (_copyOfSource.TryGetValue(gunId, out int copyId) && _copySeen.Contains(CopyKey(c.g.Uid, copyId)))
                                agg.TowardGround += shots;                               // this unit fires at helicopters through its copy only
                            else
                            {
                                float dG = float.MaxValue;
                                foreach (var e in _ground[1 - c.g.Side]) { float d = Dist3(c.g.Pos, e.Pos); if (d < dG) dG = d; }
                                if (dG > row.Ground + 100f) { agg.Shots[band] += shots; _pureUntil[c.g.Uid] = now + PureWindow; }
                                else { agg.Ambiguous += shots; agg.AmbBand[band] += shots; }
                            }
                        }
                    }
                    _lastAmmo[key] = (count, now);
                }
            }
            _readRound += done;
            if (_lastAmmo.Count > 20000) _lastAmmo.Clear();
        }

        static long CopyKey(int uid, int ammoId) => ((long)uid << 32) | (uint)ammoId;

        /// Whether the name filter of the ammunition bus reads one row at a time: the sum of the per-row counts must equal the total.
        /// setMode false: logged only (check on a unit carrying a helicopter-only copy), the reading mode in use is kept.
        static int FilterCheck(EcsEventBusGetUnitsAmmo del, UnitInfo u, int[] all, bool setMode)
        {
            int reads = 0;
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return 0;
            int total = ReadCount(del, u.Uid, AnyFilter(), out bool inf); reads++;
            if (total <= 0 || inf) { if (setMode) _filterChecked.Remove(u.UnitId); else _copyFilterChecked = false; return reads; }
            int mode = 0;
            string detail = "";
            foreach (int hud in new[] { 0, 1 })
            {
                long sum = 0; int equalTotal = 0, nonZero = 0;
                var parts = new List<string>();
                foreach (int id in all)
                {
                    string name = "";
                    try { if (src.Ammunitions.TryGetById(id, out var row) && row != null) name = (hud == 1 ? row.HUDName : row.Name) ?? ""; } catch { }
                    int c = string.IsNullOrEmpty(name) ? -1 : ReadCount(del, u.Uid, NewFilter(name), out _);
                    reads++;
                    if (c > 0) { sum += c; nonZero++; }
                    if (c == total) equalTotal++;
                    parts.Add($"{id} {c}");
                }
                detail += $" ; par {(hud == 1 ? "nom HUD" : "nom")} : somme {sum} ({string.Join(", ", parts)})";
                if (nonZero == 0) continue;                                      // this name does not match: try the HUD name
                if (sum == total && equalTotal <= 1) mode = hud == 1 ? 2 : 1;
                else if (equalTotal >= 2) mode = -1;
                break;
            }
            int q = _quantityByUnit != null && _quantityByUnit.TryGetValue(u.UnitId, out int qq) ? qq : -1;
            LogPrec($"filtre des munitions du jeu{(setMode ? "" : " (unité avec copie anti-hélico, contrôle seul)")}, {NameOf(u.Uid)} (type {u.UnitId}) : total {total}, quantité de la fiche {q}{detail} -> " +
                (mode == 1 ? "lecture par nom de munition" : mode == 2 ? "lecture par nom HUD" : mode == -1 ? "le filtre ne sépare pas les munitions" : "non concluant"));
            if (setMode && mode != 0) _filterMode = mode;
            return reads;
        }

        /// Hit on a helicopter by a ground shooter carrying that ammunition (from Analyse).
        static void PrecisionHit(UnitInfo shooter, int ammo, float dist, float now)
        {
            if (_guns == null || !_guns.ContainsKey(ammo) || dist < 0f) return;
            var agg = Agg(ammo);
            int band = Band(dist);
            agg.HitsAll[band]++;
            if (_pureUntil.TryGetValue(shooter.Uid, out float until) && now <= until) agg.Hits[band]++;
        }

        static void PrecisionReport(bool final)
        {
            if (_prec.Count == 0 && !final) return;
            var head = $"{(final ? "bilan" : "relevé")} : lectures de munitions {_precReads} (erreurs {_precReadErrors}, munitions infinies {_precInfinite}, sans filtre sûr {_precNoFilter}), tirs entre deux lectures trop espacées {_precGapShots}, " +
                       $"copies anti-hélico dans la base {_sourceOfCopy.Count} (portées par des unités lues {_copySeen.Count}, absentes {_copyMiss.Count(kv => kv.Value >= CopyMissReads)}), " +
                       $"filtre {(_filterMode == 1 ? "par nom" : _filterMode == 2 ? "par nom HUD" : _filterMode == -1 ? "inutilisable (seules les unités à une munition comptent)" : "pas encore vérifié (seules les unités à une munition comptent)")}, " +
                       $"altitude {(_altBroken ? "illisible" : "lue")} ; touches comptées seulement pour un tireur dont les tirs vers un hélico viennent d'être vus (5 s)";
            var lines = new List<string>();
            foreach (var kv in _prec.OrderByDescending(kv => kv.Value.Shots.Sum() + kv.Value.HitsAll.Sum()).Take(final ? 40 : 12))
            {
                var a = kv.Value;
                long shots = a.Shots.Sum(), hits = a.Hits.Sum(), hitsAll = a.HitsAll.Sum();
                if (shots + hitsAll + a.Ambiguous == 0) continue;
                bool copy = _sourceOfCopy.TryGetValue(kv.Key, out int source);
                var sb = new StringBuilder($"{AmmoName(kv.Key)} ({kv.Key}{(copy ? $", copie anti-hélico de {source}" : "")}) : tirs vers un hélico en vol {shots} " +
                    $"({(copy ? "hors portée d'un hélico en vol" : "douteux, ennemi au sol à portée")} {a.Ambiguous} ; vers le sol {a.TowardGround}), touches {hits} (tous tireurs {hitsAll}), taux {(shots > 0 ? (100.0 * hits / shots).ToString("0.##", Inv) + " %" : "-")}");
                for (int b = 0; b < NBands; b++)
                {
                    if (a.Shots[b] + a.HitsAll[b] + a.AmbBand[b] == 0) continue;
                    sb.Append($" ; {BandName(b)} {a.Hits[b]}/{a.Shots[b]}{(a.Shots[b] > 0 ? " (" + (100.0 * a.Hits[b] / a.Shots[b]).ToString("0.##", Inv) + " %)" : "")}{(a.HitsAll[b] != a.Hits[b] ? $" [touches tous tireurs {a.HitsAll[b]}]" : "")}{(a.AmbBand[b] > 0 ? $" [douteux {a.AmbBand[b]}]" : "")}");
                }
                lines.Add(sb.ToString());
            }
            string all = head + string.Join("|", lines);
            if (!final && all == _lastPrecReport) return;
            _lastPrecReport = all;
            LogPrec(head);
            foreach (var l in lines) LogPrec(l);
        }

        // ---------------------------------------------------------------- [PRECISION] once per battle: rows, engine estimate, hit boxes
        static bool BeginCall(string kind)
        {
            foreach (var r in (_precRefused.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (r == kind) { LogPrecOnce("refus-" + kind, $"appel direct {kind} refusé : la tentative précédente ne s'est jamais terminée (jeu arrêté pendant l'appel)"); return false; }
            PrecCallPref.Value = kind;
            MelonPreferences.Save();
            return true;
        }

        static void EndCall()
        {
            PrecCallPref.Value = "";
            MelonPreferences.Save();
        }

        static void PrecisionStatic()
        {
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return;
            _precStaticDone = true;
            var rows = new List<Ammo>();
            foreach (int id in PrecisionRefIds)
            {
                Ammo row = null;
                try { if (!src.Ammunitions.TryGetById(id, out row)) row = null; } catch { row = null; }
                if (row == null) { LogPrec($"ligne de munition {id} absente"); continue; }
                rows.Add(row);
                string line;
                try
                {
                    float f0 = row._dispersionReferenceRange;
                    float r = row.GetDispersionReferenceRange();
                    float f1 = row._dispersionReferenceRange;
                    line = $"{row.Name} ({id}) : distance de référence de la dispersion champ {F(f0)} m, GetDispersionReferenceRange {F(r)} m, champ relu {F(f1)} m ; " +
                           $"portées sol {F(row.GroundRange)} / basse altitude {F(row.LowAltRange)} / haute altitude {F(row.HighAltRange)} m ; " +
                           $"dispersion horizontale {F(row.DispersionHorizontalRadius)} verticale {F(row.DispersionVerticalRadius)} minimale {F(row.DispersionMinimal)} ; " +
                           $"fusée de proximité {F(row.RadioFuseDistance)} m ; vitesse initiale {F(row.MuzzleVelocity)} m/s ; cibles {row.TargetType}";
                }
                catch (Exception e) { line = $"ligne {id} illisible ({e.GetBaseException().Message})"; }
                LogPrec(line);
            }

            // unit rows: live helicopters first (up to 3 types), then the reference rows
            var unitRows = new List<UnitsRow>();
            var seen = new HashSet<int>();
            foreach (var u in _units.Values)
            {
                if ((u.Type & 8) == 0 || u.UnitId <= 0 || seen.Contains(u.UnitId) || seen.Count >= 3) continue;
                try { if (src.Units.TryGetById(u.UnitId, out UnitsRow ur) && ur != null) { unitRows.Add(ur); seen.Add(u.UnitId); } } catch { }
            }
            foreach (int id in PrecisionUnitRows)
            {
                if (seen.Contains(id)) continue;
                try { if (src.Units.TryGetById(id, out UnitsRow ur) && ur != null) { unitRows.Add(ur); seen.Add(id); } } catch { }
            }

            if (rows.Count > 0 && unitRows.Count > 0 && BeginCall("GetTargetAccuracy"))
            {
                try
                {
                    foreach (var row in rows)
                    {
                        var sb = new StringBuilder($"estimation du jeu (GetTargetAccuracy) pour {row.Name} ({row.Id}) :");
                        foreach (var ur in unitRows)
                        {
                            sb.Append($" | {ur.Name} ({ur.Id}, {F(ur.Length)} x {F(ur.Width)} x {F(ur.Height)} m)");
                            foreach (float d in PrecisionDistances)
                            {
                                float v = float.NaN;
                                try { v = BSH.GetTargetAccuracy(row, ur, d); } catch { }
                                sb.Append($" {d:0} m {(float.IsNaN(v) ? "?" : v.ToString("0.###", Inv))}");
                            }
                        }
                        LogPrec(sb.ToString());
                    }
                }
                finally { EndCall(); }
            }

            // hit boxes: up to 3 live helicopters and one live vehicle, for the 12.7 mm row 19 and the Hellfire row 164
            var boxUnits = new List<UnitInfo>();
            foreach (var u in _units.Values) if ((u.Type & 8) != 0 && boxUnits.Count < 3) boxUnits.Add(u);
            foreach (var u in _units.Values) if ((u.Type & 4) != 0 && (u.Type & (8 | 16)) == 0) { boxUnits.Add(u); break; }
            Ammo r19 = null, r164 = null;
            try { src.Ammunitions.TryGetById(19, out r19); src.Ammunitions.TryGetById(164, out r164); } catch { }
            if (boxUnits.Count > 0 && (r19 != null || r164 != null) && BeginCall("GetUnitBox"))
            {
                try
                {
                    var parts = new List<string>();
                    foreach (var u in boxUnits)
                    {
                        string card = "?";
                        try { if (src.Units.TryGetById(u.UnitId, out UnitsRow ur) && ur != null) card = $"{F(ur.Length)} x {F(ur.Width)} x {F(ur.Height)} m"; } catch { }
                        var sb = new StringBuilder($"{NameOf(u.Uid)} (type {u.UnitId}, fiche {card})");
                        foreach (var (row, label) in new[] { (r19, "munition 19"), (r164, "munition 164") })
                        {
                            if (row == null) continue;
                            try
                            {
                                var v = Il2Cpp.ClassExtensions.ToUnityVector(HitDet.GetUnitBox(u.U.Entity, row));
                                sb.Append($" {label} ({F(v.x)}, {F(v.y)}, {F(v.z)})");
                            }
                            catch (Exception e) { sb.Append($" {label} illisible ({e.GetBaseException().Message})"); }
                        }
                        parts.Add(sb.ToString());
                    }
                    LogPrec("boîte de touche du jeu (GetUnitBox) : " + string.Join(" | ", parts));
                }
                finally { EndCall(); }
            }
        }

        static string F(float v) => float.IsNaN(v) ? "?" : v.ToString("0.##", Inv);
    }
}
