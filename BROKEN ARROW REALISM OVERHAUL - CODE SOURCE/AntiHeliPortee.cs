// RealismOverhaul - anti-helicopter rules on the engine's shooting range (values of 2026-09-16 and 2026-09-17),
//  identical for both sides. One lazy postfix on BattleSystemHelpers.GetAmmoShootingDistance (on the engine path: 38,337 calls in the 0.22.7
//  battle, all on the main thread) that only ever SHORTENS the returned range. Ground shooters (infantry, vehicles, ships) get every rule;
//  air shooters only get the Affuts manual-aim caps of listed helicopter door guns (R2); planes and every other air weapon are never touched.
//   R1 [ANTIHELICO] guns and rifles against a helicopter in flight (5 m or more above the ground): at most the ammunition card's own
//      LowAltRange (OPTION_TIR_ANTIHELICO_COURT rows: rifles 400 m, DMR 500, 7.62 mm MG 800, 12.7 mm 1000, 14.5 mm 1200, 23 mm 1500,
//      20-30 mm 2000, 35 mm 2500, 57 mm 3000). Air-defence ammunition (Aircraft, Projectile or missile bits) is never capped and RPG rows
//      are never capped below 300 m (reloadable) / 200 m (disposable), or their own ground range when shorter.
//   R2 [ANTIHELICO] manual aim: the per-unit cap given by Affuts (weapons aimed by eye 600 m, scopes...) for listed ground mounts and
//      helicopter door guns, against helicopters in flight and against ground targets (a helicopter below 5 m is a ground target). The
//      ground caps stay on even when the per-unit copies are proven: they change nothing on copies Affuts wrote and catch the ones it skipped.
//   R3 [VOL-BAS] infrared MANPADS-class missiles against a low helicopter (height = position minus terrain height; on a building pixel
//      the lowest non-building ground around it, as VueAA does; unreadable height = no rule):
//      - Stinger, Igla, Igla-S, Verba, Mistral, Grom, Piorun fired by INFANTRY: range 0 below 25 m, except against a helicopter in flight
//        within 1000 m (horizontal) of the team, where R4 decides: the team fires only with its own line of sight, and only while R4 is
//        enforced with a fresh verdict for that pair (otherwise range 0 as beyond 1000 m);
//      - the same missiles on vehicles (Avenger / M-SHORAD / Linebacker / LAV-AD / MML Stinger, Strela-10 9M333) and every other ground
//        shooter: range 0 below 10 m (seeker floor), at any distance.
//      RBS-70, Osa, Tunguska, Sosna and every radar or command SAM are not concerned.
//   R4 [VUE-AA] own line of sight, INFANTRY ONLY: a ground unit whose Type is infantry (no vehicle, ship or aircraft bit) holding one of
//      those missiles gets range 0 against a helicopter VueAA says it cannot see (trees, buildings, relief; a team high in a building
//      looks from its roof). Vehicle air defence (Avenger, Strela-10, M-SHORAD, Tor, Pantsir...) is never gated by R4, and missile
//      interception is never touched (only helicopters in flight are targets, and missiles that intercept missiles are not in the list).
//      Armed when VueAA's map self-test, copy check and building calibration passed in this battle; otherwise measurement only, with the
//      reason in the log. Safety line (warning only, nothing is switched off: the rule may rightly keep teams silent): when infantry teams
//      launched no missile during 10 minutes of watchdog time with enemy helicopters in flight within their range, while at least 90 % of
//      those pairs were masked. Launches are counted from the game's own missile counts of those teams (GameplayBus.GetUnitsAmmo, the
//      read AntiHeliTouches and MunitionsAir use), every 5 s, at most 24 reads.
//  The list of infrared missiles is checked against the live database: an ammunition is kept only if it can hit helicopters or aircraft,
//  has no ground bit and no Projectile/SEAD/Cruise/Ballistic bit (missile interception is never affected) and is fire-and-forget.
//  Safety: solo campaign only, own Harmony id, crash guard, immutable snapshots rebuilt on the main thread, one error kill-switch per rule,
//  and a combat watchdog: impacts are counted (observer prefix on ShellHitSystem.InternalUpdate); when they stop for 3 minutes of game
//  time while the two sides are in contact and the gun rules (R1, R2) keep shortening ranges, every rule is switched off for the battle,
//  logged and shown on screen. The intended cuts of R3 and R4 never feed that test: they only zero infrared missile ranges against
//  helicopters, so they cannot silence every weapon (they keep their own error kill-switches, the VueAA gate and the crash guard).
//  The watchdog clock stops while the game is paused or the battle end screen is up (Unity time keeps running there).
//  Fire resuming within 2 minutes of that switch-off only makes the rules suspect: once fire is flowing again (20 impacts per minute,
//  sides in contact) the rules are put back for a 90 s test, once per battle. The first 30 s are not judged (rounds already in flight
//  land); silence again after that (at most 2 late impacts, none in the last 30 s) while units move, the sides are in contact and the
//  gun rules keep cutting is proof against the rules: after two such battles (same mod version, hidden preference), the rules stay in
//  measurement mode. A test that meets the end screen or a frozen game proves nothing.
//  Measurement kept: direct engine queries every 5 s (rows 19, 70, 204), the Alt tool weapon types, and a read-only probe of helicopter
//  heights by deduced flight mode ([VOL-BAS]); NavigationConstants are only read, their setters are never called.
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
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using ShellHit = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellHitSystem;
using Aircraft = Il2CppBrokenArrow.Client.Ecs.Planes.AircraftHelper;
using AltLevel = Il2CppBrokenArrow.Shared.Ecs.Enums.GlobalAltitude;
using MapMeta = Il2CppBrokenArrow.Client.Ecs.Navigation.MapMetaData;
using NavConst = Il2CppBrokenArrow.Client.Ecs.Navigation.NavigationConstants;
using TerrainKind = Il2CppBrokenArrow.Shared.Ecs.Enums.TerrainType;
using FowTool = Il2CppBrokenArrow.Client.Ecs.FogOfWar.VisualTools.FOWToolScript;
using WeaponType = Il2CppBrokenArrow.DataBase.Enums.WeaponType;
using SeekerType = Il2CppBrokenArrow.DataBase.Enums.SeekerType;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using DbSource = Il2CppBrokenArrow.DataBase.DataBaseSourceData;
using DbService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using AmmoFilter = Il2CppBrokenArrow.ScriptEngine.Data.AmmoFilterData;
using NodeAmmoKind = Il2CppBrokenArrow.ScriptEngine.Data.NodeAmmoType;
using GetAmmoDel = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.EcsEventBus.GameplayBus.GetUnitsAmmoDel;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    /// One ground shooter holding an infrared MANPADS-class missile (immutable value, read by VueAA).
    internal readonly struct LosShooter
    {
        internal readonly int EntityId, Side;
        internal readonly V3 Pos;
        internal readonly bool Infantry;                             // unit Type infantry without vehicle, ship or aircraft bit (R4 applies)
        internal readonly float Range;                               // largest LowAltRange of its infrared missiles (metres)
        internal LosShooter(int entityId, int side, V3 pos, bool infantry, float range) { EntityId = entityId; Side = side; Pos = pos; Infantry = infantry; Range = range; }
    }

    /// One helicopter of either side (immutable value, read by VueAA).
    internal readonly struct LosHeli
    {
        internal readonly int EntityId, Side;
        internal readonly V3 Pos;
        internal LosHeli(int entityId, int side, V3 pos) { EntityId = entityId; Side = side; Pos = pos; }
    }

    /// Shooters and helicopters for VueAA's line-of-sight cycle (built on the main thread, never modified afterwards).
    internal sealed class LosSnapshot
    {
        internal readonly LosShooter[] Shooters;
        internal readonly LosHeli[] Helis;
        internal readonly float Time;                                // realtimeSinceStartup of the snapshot
        internal LosSnapshot(LosShooter[] shooters, LosHeli[] helis, float time) { Shooters = shooters; Helis = helis; Time = time; }
    }

    static class AntiHeliPortee
    {
        const string GuardVersion = "0.24.0";
        const string OptionName = "OPTION_TIR_ANTIHELICO_COURT";
        const int MaxErrors = 50;
        const float AirborneMin = 5f;                               // metres above the ground: below, a helicopter is a normal ground target
        const float LowFlightFloor = 25f;                           // R3, infantry: shoulder-launched infrared missiles cannot engage a helicopter below this height...
        const float LowFlightCloseRange = 1000f;                    // ...beyond this horizontal distance of the team (within it, R4 decides)
        const float VehicleLowFlightFloor = 10f;                    // R3, vehicles and every other ground shooter: seeker floor, any distance
        const float HeliEvery = 0.25f, ScanEvery = 1f, TablesEvery = 5f, ManualEvery = 10f, LosEvery = 0.5f, MeasureEvery = 5f, FowEvery = 5f;
        const float ReportEvery = 30f, WatchEvery = 5f, ProbeEvery = 1f, ProbeReportEvery = 60f;
        const long StaleMs = 3000;                                  // snapshots older than this are ignored by the hook (Frame not running any more)
        const int TypeInfantry = 2, TypeVehicle = 4, TypeHeli = 8, TypePlane = 16, TypeShip = 32;
        const long AirDefenceBits = 16 | 128 | 256 | 512 | 1024;  // Aircraft, Projectile, SEAD, Cruise, Ballistic: never capped
        const long MissileBits = 128 | 256 | 512 | 1024, GroundBits = 1 | 2 | 4 | 32, HeliAirBits = 8 | 16;
        const int RingSize = 512;                                   // power of two
        const int MaxHelisPerMeasure = 3, MaxMeasureLines = 90, MaxPatterns = 30, MaxAgg = 300;
        const float DefaultIrRange = 6500f;                         // AA infantry (role 34) whose loadout could not be read
        // R4 safety line (warning only): infantry teams silent for this long with enemy helicopters in range, most pairs masked
        const float SafetyWindow = 600f, SafetyMaskedShare = 0.9f;
        const int SafetyMaxReads = 24, SafetyMaxWarnings = 3, SafetyMaxReadErrors = 20;
        // combat watchdog (game time)
        const float WatchSilence = 180f, WatchResume = 120f, WatchRecentChange = 60f, ContactRange = 3000f, ContactShare = 0.8f;
        const int WatchMinChanges = 20, WatchMinChecks = 20, ProofBattles = 2;
        const float RetestLen = 90f;                                // re-test of suspect rules: put back this long while fire is flowing
        const float RetestGrace = 30f;                              // first part of the re-test not judged: rounds already in flight land, bursts end
        const float RetestQuiet = 30f;                              // last part of the re-test: no impact at all allowed there
        const int RetestMaxImpacts = 2;                             // late shells tolerated in the judged part (grace to the end)
        const int RetestMinImpacts = 20;                            // impacts in the last minute needed to start the re-test
        const float WatchMaxStep = 60f;                             // largest game-time step one watchdog tick may add to its clock
        const int HoldEnd = 1, HoldPause = 2, HoldScale = 4;        // signals that stop the watchdog clock: end screen, game pause flag, game speed 0
        const int HoldStrikes = 2;                                  // check pairs in a row where the game ran under a steady signal before that signal is ignored
        // helicopter height over a building pixel (the height map holds the roof): nearest ring of non-building ground, as VueAA does
        const int RoofRingMax = 5, RoofSampleBudget = 120;          // rings of 1 to 5 pixels; engine samples per refresh (all helicopters)
        const float RoofReuse = 1.5f, RoofReuseAge = 10f;           // same place (metres) and age (s) to reuse the last search of a helicopter
        const int WhyCap = 1, WhyManual = 2, WhyFloor = 3, WhyLos = 4;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // planned anti-helicopter ranges (upper bound of the LowAltRange written by the option rows); the cap is always the row's own value
        static readonly (float max, int[] ids)[] PlannedCaps =
        {
            (400f,  new[] { 16, 30, 51, 93, 94, 116, 120, 143, 341, 357, 358, 360, 361, 364, 366, 368, 373, 381, 382, 434, 469, 471, 510, 511, 568, 619, 620, 724, 904, 913, 43, 359, 363, 914 }),
            (500f,  new[] { 39, 40, 100, 101, 122, 129, 274, 275, 342, 343, 369, 370, 371, 372, 375, 376, 383, 384, 385, 387, 388, 389, 391, 393, 394, 526, 527, 579, 614, 732, 907, 912, 916 }),
            (800f,  new[] { 9, 27, 75, 76, 153, 155, 156, 362, 365, 386, 390, 472, 473, 480, 528, 621 }),
            (1000f, new[] { 19, 280, 488, 70 }),
            (1200f, new[] { 163 }),
            (1500f, new[] { 176 }),
            (2000f, new[] { 204, 205, 207, 210, 177, 196, 546, 26, 277, 53, 36, 567 }),
            (2500f, new[] { 570 }),
            (3000f, new[] { 267, 222, 507 }),
        };
        // dedicated anti-air rows: never capped, whatever a table edit may say (the TargetType bits exclude the others)
        static readonly HashSet<int> DedicatedAa = new() { 162, 189, 212, 213, 231, 60, 62, 262, 32, 350, 454, 201, 491 };
        // R3/R4 candidates (upper bound): infrared fire-and-forget MANPADS-class missiles; kept only if the live row passes the bit checks
        static readonly (int id, string label)[] IrCandidates =
        {
            (462, "Stinger F"), (474, "Stinger J"), (18, "Stinger des véhicules (Avenger, M-SHORAD, Linebacker, LAV-AD, MML)"),
            (106, "Igla"), (127, "Igla-S"), (97, "Verba"), (601, "Mistral 3"), (598, "Grom"), (597, "Piorun"), (181, "Strela-10 9M333"),
        };
        // minimum ranges from the data tables, checked and logged only
        static readonly (int id, float min, string label)[] MinRangeChecks =
        {
            (462, 200f, "Stinger F"), (474, 200f, "Stinger J"), (18, 200f, "Stinger véhicules"), (106, 500f, "Igla"), (127, 500f, "Igla-S"),
            (97, 500f, "Verba"), (601, 500f, "Mistral 3"), (598, 500f, "Grom"), (597, 400f, "Piorun"), (573, 250f, "RBS-70 véhicule"), (599, 250f, "RBS-70"),
        };
        // RPG ranges against a flying helicopter (data tables): caps never go below min(value, row GroundRange)
        static readonly (float floor, int[] ids)[] RpgFloors =
        {
            (300f, new[] { 104, 736, 730, 476, 31, 107, 906, 112, 905, 509, 594, 33, 344, 492, 42, 41, 595 }),
            (200f, new[] { 735, 109, 111, 115, 110, 86, 475, 592, 130, 128, 593, 524, 377, 108, 396, 103, 121, 392 }),
        };
        // rows queried directly by the measurement: M2 12.7x99, Kord 12.7x108, 2A42 30 mm HE
        static readonly (int id, string label)[] Probes = { (19, "M2 12,7"), (70, "Kord 12,7"), (204, "2A42 30 mm HE") };
        static readonly string[] ProbeNames = { "posé (moins de 5 m)", "stationnaire en vol", "vol bas", "vol haut" };
        static readonly float[] ProbeBins = { 5f, 10f, 15f, 20f, 30f, 40f, 60f, 100f };

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean, _proofConfirmed, _proofNoEffect;
        static MelonPreferences_Entry<string> _guardVersion;
        static HarmonyLib.Harmony _harmony;
        static bool _patched, _refused, _everOnline, _netLogged, _sessionArmed, _battle, _impactHook, _stopByProof, _proofNotified;
        static float _nextHeli, _nextScan, _nextTables, _nextManual, _nextLos, _nextMeasure, _nextFow, _nextReport, _nextWatch, _battleStart;
        static float _nextProbeReport, _nextGateLog;
        static LuaMap _map;

        /// Latest shooters/helicopters snapshot for VueAA (null outside a solo battle).
        internal static volatile LosSnapshot Los;

        // ---------------------------------------------------------------- hook side: immutable snapshots replaced as a whole, plain counters
        sealed class UnitSnap
        {
            internal readonly HashSet<int> Ground = new(), Air = new(), ManualShooters = new();
            internal readonly HashSet<int> IrInfantry = new();       // infantry units holding an infrared MANPADS-class missile: R4 applies
            internal readonly Dictionary<int, int> UnitOf = new();   // ground or helicopter EntityId -> database UnitID
        }

        sealed class Tables
        {
            internal readonly Dictionary<int, float> Caps = new();      // R1: ammunition Id -> anti-helicopter cap (row LowAltRange)
            internal readonly HashSet<int> NoCap = new();               // air-defence rows: never capped
            internal readonly HashSet<int> Ir = new();                  // R3/R4: infrared MANPADS-class ammunition
            internal readonly Dictionary<int, float> IrRange = new();   // Ir ammunition Id -> LowAltRange (VueAA pair range)
            internal readonly Dictionary<int, float> RpgFloor = new();  // RPG ammunition Id -> lowest cap allowed
            internal readonly Dictionary<int, float> MinRange = new();  // capped ammunition Id -> MinimalRange (caps never below)
            internal IntPtr Src;
            internal bool CapsOn;                                       // real stats applied and option active
        }

        sealed class ManualTables
        {
            internal readonly Dictionary<long, float> Heli = new(), Ground = new();   // (UnitID << 32 | ammunition Id) -> cap
            internal bool GroundOn;                                                     // ground caps applied here (always: only shortens, so written copies are unchanged)
        }

        static volatile bool _armed, _ruleCaps, _ruleFloor, _ruleLos, _ruleManual, _manualGroundLive;
        [ThreadStatic] static bool _bypass;                         // set around the module's own measurement calls: raw engine value
        static volatile int _mainThread;
        static long _validUntil;                                    // Environment.TickCount64 limit of the current snapshots
        static volatile HashSet<int> _helisAir = new();             // EntityIds of helicopters at least AirborneMin above the ground (or height unreadable)
        static volatile HashSet<int> _helisLow = new();             // EntityIds of helicopters whose height is known and below LowFlightFloor
        static volatile HashSet<int> _helisVeryLow = new();         // subset of _helisLow: height known and below VehicleLowFlightFloor
        static volatile HashSet<long> _lowClose = new();            // (infantry shooter EntityId << 32 | helicopter EntityId): enemy helicopter in flight
                                                                    // below LowFlightFloor within LowFlightCloseRange of that team (built every 0.5 s)
        static volatile UnitSnap _units = new();
        static volatile Tables _tables;
        static volatile ManualTables _manual;
        static long _calls, _offMain, _stale, _heliCalls, _fromGround, _fromAir, _fromOther, _changed, _impacts;
        static long _cutCaps, _cutManualHeli, _cutManualGround, _cutFloor, _cutLos, _losWould, _losVehicle, _irCalls;
        static long _closeAllowed, _closeNoVerdict;                 // R3 infantry: low helicopter close enough, left to R4 / close but no enforced fresh verdict
        static long _errCore, _errCaps, _errManual, _errFloor, _errLos;
        static readonly long[] _ring = new long[RingSize];          // samples: Id << 40 | original dm << 20 | returned dm
        static long _ringSeq;

        // ---------------------------------------------------------------- main thread
        sealed class Unit
        {
            public LuaUnit U;
            public int Uid, Eid, Side, Type, UnitId;
            public V3 Pos, PrevPos;
            public bool PosOk, HeightKnown, Airborne, PrevOk, IrInfantry;
            public float Height, IrRange, PrevTime, NextProbe;
            public V3 RoofPos;                                       // last ground-under-building search of this helicopter
            public float RoofGround = float.NaN, RoofTime;
        }
        sealed class Agg { public long N, Capped; public float MaxOrig, Cap, MaxFree; }
        sealed class ProbeAgg { public long N; public double Sum, Min = double.MaxValue, Max; }

        static readonly List<Unit> _heliList = new();
        static readonly List<Unit> _lowHelisTmp = new();            // PublishLos scratch list (main thread only)
        static List<Unit>[] _groundBySide = { new(), new() };
        static List<Unit> _gateShooters = new();
        static readonly float[] _posTime = new float[2];
        static readonly Dictionary<int, int> _typeByUid = new(), _typeByUnitId = new(), _unitIdByUid = new(), _roleByUid = new();
        static readonly Dictionary<int, float> _irRangeByUnitId = new();
        static readonly Dictionary<int, Agg> _agg = new();
        static readonly Dictionary<string, int> _patterns = new();
        static readonly Dictionary<int, (string key, float time)> _lastMeasure = new();
        static readonly HashSet<string> _once = new();
        static readonly Dictionary<string, float> _warnNext = new();
        static Dictionary<int, int[]> _ammoByUnitId;                // WeaponAmmunitions join, per database
        static IntPtr _joinSrc;
        static DbSource _tablesSrc;                                 // held reference: its address cannot be reused while held
        static string _capsLine, _lastSig, _irLine, _rpgLine, _minLine, _manualLine, _gateReason, _lastRuleSig, _lastProbe;
        static long _drained, _lostSamples;
        static int _landed, _unknownType, _scanErrors, _measureLines, _measureTurn, _probeFails, _patternTotal, _affutsErrors;
        static bool _terrainBroken, _altBroken, _typeHelperBroken, _fowDone, _optionOffLogged, _notAppliedLogged, _killLogged, _affutsBroken, _lastCopiesProven;
        // R4 gate and safety line (main thread)
        static bool _losGate, _losFailedLogged;
        static float _sfClockPrev = -1f, _sfExposure, _sfExposureTotal; // watchdog clock at the previous check / seconds with helicopters in range: since the last launch, in the battle
        static long _sfPairs, _sfMasked, _sfEvaluated;              // infantry/helicopter pair samples in range in the current window
        static long _sfLaunches, _sfReads, _sfReadErrors, _sfUnreadable;
        static int _sfWindowReadable, _sfWarnings, _sfRound;
        static bool _sfBroken;
        static readonly Dictionary<int, int> _sfLastCount = new();          // unit uid -> last missile count read
        static readonly Dictionary<int, AmmoFilter> _sfFilterByUnitId = new(); // unit type -> filter that reads its infrared missiles (null: none)
        static readonly List<Unit> _sfTeams = new();
        static Il2CppSystem.Collections.Generic.List<int> _sfOne;
        static Il2CppSystem.Collections.Generic.IReadOnlyList<int> _sfOneRo;
        // watchdog (game time)
        static long _wdImp = -1, _wdChg, _wdChgAtImpact, _wdImpAtTrip;
        static float _wdLastImpact, _wdLastChange, _wdTripTime;
        static int _wdChecks, _wdContact;
        static bool _wdFired, _wdTripped, _wdResolved, _wdConfirmed, _lastContact;
        static bool _wdSuspect, _wdRetest, _wdRetestDone;           // fire resumed fast after a trip / rules put back for the test / test done
        static float _wdRetestStart, _wdRetestBase, _wdQuietBase;   // test start / start of the judged part / start of the last quiet part (watchdog clock)
        static long _wdImpAtRetest, _wdImpAtQuiet, _wdChgAtRetest;  // impact counts at those two points (-1 = not taken yet)
        static int _wdRetestChecks, _wdRetestContact, _wdRetestMoving; // test checks / in contact / units moving (the battle really runs)
        // watchdog clock: Unity time only while the game runs (Time.timeScale stays 1 in this game and Time.time keeps running in a pause)
        static float _wdClock, _wdPrevTime = -1f;
        static long _wdImpPrevTick;
        static int _wdHoldPrev, _wdHoldBad;                         // hold signals up at the previous check and at every 0.25 s frame since / proven wrong in this battle (HoldEnd, HoldPause, HoldScale)
        static readonly int[] _wdHoldStrike = new int[3];           // per signal (bit 1, 2, 4): check pairs in a row where the game ran while the signal stayed up
        static bool _wdEndHiddenSeen, _wdMoving;
        static double _wdSig;
        static int _wdSigCount = -1;
        static readonly Queue<(float t, long imp)> _impHistory = new();
        // helicopter height over buildings (main thread)
        static long _roofFixed, _roofRaw;
        static int _roofBridgeMask = -1, _roofMaxX, _roofMaxY;
        static IntPtr _roofMap;
        static bool _roofGridOk, _roofBroken;
        // altitude probe
        static readonly ProbeAgg[] _probe = { new(), new(), new(), new() };
        static readonly long[] _probeHist = new long[9];
        static long _probeUnknown;
        static string _constLine;
        static float _probeMid = 24f;

        static void Log(string s) => Mod.Log.Msg("[ANTIHELICO] " + s);
        static void LogLow(string s) => Mod.Log.Msg("[VOL-BAS] " + s);
        static void LogLos(string s) => Mod.Log.Msg("[VUE-AA] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_AntiHeliPortee");
            _enabled = c.CreateEntry("PorteeAntiHelico", true, description: Build.Desc("Tir contre les hélicoptères (les deux camps) : portée anti-hélico des armes au sol, visée à l'œil, missiles infrarouges d'épaule impossibles sous 25 m au-delà de 1 km et sans vue propre, missiles infrarouges des véhicules impossibles sous 10 m ; toujours actif"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _proofConfirmed = c.CreateEntry("CoupuresConfirmees", 0, description: Build.Desc("Sécurité automatique (batailles où les règles remises à l'essai ont de nouveau arrêté tous les tirs), ne pas modifier"));
            _proofNoEffect = c.CreateEntry("CoupuresSansEffet", 0, description: Build.Desc("Sécurité automatique (coupures des règles sans reprise des tirs), ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; _proofConfirmed.Value = 0; _proofNoEffect.Value = 0; }
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        static void EndBattle(string why)
        {
            _armed = false;
            _ruleCaps = _ruleFloor = _ruleLos = _ruleManual = _manualGroundLive = false;
            Los = null;
            if (!_battle && !_sessionArmed) return;
            _battle = false;
            Interlocked.Exchange(ref _validUntil, 0);
            if (_sessionArmed)
            {
                _sessionArmed = false;
                try { Drain(); Report(true); ReportProbe(true); } catch { }
                try { ProofAtEnd(); } catch { }
                if (_unclean.Value != 0) _unclean.Value = 0;
                try { MelonPreferences.Save(); } catch { }
                Log($"fin de bataille ({why})");
            }
            _helisAir = new HashSet<int>(); _helisLow = new HashSet<int>(); _helisVeryLow = new HashSet<int>(); _lowClose = new HashSet<long>();
            _units = new UnitSnap();
            _tables = null; _manual = null;
            ForgetBattle();
        }

        /// Per-battle state back to zero (the installed patch stays; the hook is disarmed by the flags).
        static void ForgetBattle()
        {
            _heliList.Clear(); _lowHelisTmp.Clear(); _groundBySide = new[] { new List<Unit>(), new List<Unit>() }; _gateShooters = new List<Unit>();
            _posTime[0] = _posTime[1] = 0f;
            _typeByUid.Clear(); _typeByUnitId.Clear(); _unitIdByUid.Clear(); _roleByUid.Clear(); _irRangeByUnitId.Clear();
            _agg.Clear(); _patterns.Clear(); _lastMeasure.Clear(); _once.Clear(); _warnNext.Clear();
            _tablesSrc = null; _capsLine = null; _lastSig = null; _irLine = null; _rpgLine = null; _minLine = null; _manualLine = null;
            _gateReason = null; _lastRuleSig = null; _lastProbe = null;
            Interlocked.Exchange(ref _calls, 0); Interlocked.Exchange(ref _offMain, 0); Interlocked.Exchange(ref _stale, 0);
            Interlocked.Exchange(ref _heliCalls, 0); Interlocked.Exchange(ref _fromGround, 0); Interlocked.Exchange(ref _fromAir, 0);
            Interlocked.Exchange(ref _fromOther, 0); Interlocked.Exchange(ref _changed, 0); Interlocked.Exchange(ref _impacts, 0);
            Interlocked.Exchange(ref _cutCaps, 0); Interlocked.Exchange(ref _cutManualHeli, 0); Interlocked.Exchange(ref _cutManualGround, 0);
            Interlocked.Exchange(ref _cutFloor, 0); Interlocked.Exchange(ref _cutLos, 0); Interlocked.Exchange(ref _losWould, 0); Interlocked.Exchange(ref _losVehicle, 0); Interlocked.Exchange(ref _irCalls, 0);
            Interlocked.Exchange(ref _closeAllowed, 0); Interlocked.Exchange(ref _closeNoVerdict, 0);
            Interlocked.Exchange(ref _errCore, 0); Interlocked.Exchange(ref _errCaps, 0); Interlocked.Exchange(ref _errManual, 0);
            Interlocked.Exchange(ref _errFloor, 0); Interlocked.Exchange(ref _errLos, 0);
            Interlocked.Exchange(ref _ringSeq, 0);
            _drained = 0; _lostSamples = 0;
            _landed = _unknownType = _scanErrors = _measureLines = _measureTurn = _probeFails = _patternTotal = _affutsErrors = 0;
            _terrainBroken = _altBroken = _typeHelperBroken = _fowDone = _optionOffLogged = _notAppliedLogged = _killLogged = _affutsBroken = false;
            _lastCopiesProven = false;
            _losGate = false; _losFailedLogged = false;
            _sfClockPrev = -1f; _sfExposure = 0f; _sfPairs = _sfMasked = _sfEvaluated = 0;
            _sfLaunches = _sfReads = _sfReadErrors = _sfUnreadable = 0; _sfExposureTotal = 0f;
            _sfWindowReadable = _sfWarnings = _sfRound = 0; _sfBroken = false;
            _sfLastCount.Clear(); _sfFilterByUnitId.Clear(); _sfTeams.Clear();
            _wdImp = -1; _wdChg = 0; _wdChgAtImpact = 0; _wdImpAtTrip = 0; _wdLastImpact = _wdLastChange = _wdTripTime = 0f;
            _wdChecks = _wdContact = 0; _wdFired = _wdTripped = _wdResolved = _wdConfirmed = _lastContact = false; _impHistory.Clear();
            _wdSuspect = _wdRetest = _wdRetestDone = false; _wdRetestStart = _wdRetestBase = _wdQuietBase = 0f; _wdImpAtRetest = _wdImpAtQuiet = -1; _wdChgAtRetest = 0;
            _wdRetestChecks = _wdRetestContact = _wdRetestMoving = 0;
            _wdClock = 0f; _wdPrevTime = -1f; _wdImpPrevTick = 0; _wdHoldPrev = _wdHoldBad = 0; _wdEndHiddenSeen = _wdMoving = false; _wdSig = 0; _wdSigCount = -1;
            Array.Clear(_wdHoldStrike, 0, _wdHoldStrike.Length);
            _roofFixed = _roofRaw = 0; _roofBridgeMask = -1; _roofMaxX = _roofMaxY = 0; _roofMap = IntPtr.Zero; _roofGridOk = _roofBroken = false;
            foreach (var p in _probe) { p.N = 0; p.Sum = 0; p.Min = double.MaxValue; p.Max = 0; }
            Array.Clear(_probeHist, 0, _probeHist.Length); _probeUnknown = 0; _constLine = null; _probeMid = 24f;
            _nextScan = _nextTables = _nextManual = _nextLos = _nextMeasure = _nextFow = _nextReport = _nextWatch = _nextProbeReport = _nextGateLog = 0f;
        }

        static int _wait, _waitScan;

        /// Every frame in campaign; works every 0.25 s. Allocation-free gate: the closures of the tick live in Tick.
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _refused) { DisarmAll(); return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextHeli) return;
            // battle start only: the arming, the preference write and the hook install wait for a frame with no other module doing
            // the same (Planif.cs). Once armed the 0.25 s refresh never waits: the rules read fresh helicopter positions.
            if (!_sessionArmed && !Planif.Take(ref _wait, Planif.WaitArm)) return;
            _nextHeli = now + HeliEvery;
            Tick(now);
        }

        static void Tick(float now)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_battle || _sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_armed) { DisarmAll(); Log("partie en ligne : règles anti-hélico coupées"); } Los = null; return; }
            if (!_battle) { _battle = true; _battleStart = now; _nextProbeReport = now + ProbeReportEvery; LogConstantsOnce(); }

            // full unit scan: one heavy job per frame (Planif.cs). This gate is polled once per 0.25 s tick, not once per frame, so it
            // asks for the shortest wait: a refused scan slips one tick (0.25 s) instead of three.
            if (now >= _nextScan && Planif.Take(ref _waitScan, 1)) { _nextScan = now + ScanEvery; Safe("unités", Scan); }
            Safe("hélicos", () => RefreshHelis(gc, now));
            Interlocked.Exchange(ref _validUntil, Environment.TickCount64 + StaleMs);
            if (now >= _nextTables) { _nextTables = now + TablesEvery; Safe("tables", () => BuildTables(now)); }
            if (now >= _nextManual) { _nextManual = now + ManualEvery; Safe("visée à l'œil", BuildManual); }
            if (now >= _nextLos) { _nextLos = now + LosEvery; Safe("instantané de vue", () => PublishLos(now)); }

            bool needHook = _heliList.Count > 0 || (_manual != null && _manual.GroundOn && _manual.Ground.Count > 0);
            if (!needHook && !_patched) return;                    // nothing to hook yet
            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2)
                {
                    _refused = true;
                    DisarmAll();
                    Mod.Log.Warning("[ANTIHELICO] désactivé : les deux dernières parties ne se sont pas terminées normalement");
                    Mod.Notify(TxtKey.N_AH_OFF_SAFETY);
                    return;
                }
                _sessionArmed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();
                _nextReport = now + ReportEvery;
                _stopByProof = _proofConfirmed.Value >= ProofBattles;
                if (_stopByProof)
                {
                    Log($"règles en mesure seule : dans {_proofConfirmed.Value} batailles, les règles remises à l'essai par le chien de garde ont de nouveau arrêté tous les tirs (version {GuardVersion})");
                    if (!_proofNotified) { _proofNotified = true; Mod.Notify(TxtKey.N_AH_OBSERVE_ONLY); }
                }
            }
            if (!_patched && !TryPatch()) return;
            // hold signals must stay up at every frame between two watchdog checks: a short unpause between two paused checks is not a contradiction
            if (_wdHoldPrev != 0) _wdHoldPrev &= HoldSignals();
            if (now >= _nextWatch) { _nextWatch = now + WatchEvery; Safe("chien de garde", () => Watch(now)); Safe("vue AA", () => UpdateLosGate(now)); Safe("sécurité vue AA", () => LosSafety(now)); }
            UpdateRules();
            if (now >= _nextMeasure) { _nextMeasure = now + MeasureEvery; Safe("mesure", () => Measure(now)); }
            if (!_fowDone && now >= _nextFow) { _nextFow = now + FowEvery; LogFowTool(now); }
            if (now >= _nextReport) { _nextReport = now + ReportEvery; Safe("relevé", () => Report(false)); }
            if (now >= _nextProbeReport) { _nextProbeReport = now + ProbeReportEvery; Safe("sonde", () => ReportProbe(false)); }
        }

        static void DisarmAll()
        {
            if (_armed) _armed = false;
            _ruleCaps = _ruleFloor = _ruleLos = _ruleManual = _manualGroundLive = false;
        }

        static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Warn(what, $"{what} : erreur ({e.GetBaseException().Message})"); }
        }

        static void Warn(string key, string msg)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_warnNext.TryGetValue(key, out var t) && now < t) return;
            _warnNext[key] = now + 60f;
            Log(msg);
        }

        static void LogOnce(string key, string msg) { if (_once.Add(key)) Log(msg); }

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

        static bool TryPatch()
        {
            try
            {
                var target = AccessTools.Method(typeof(BSH), "GetAmmoShootingDistance", new[] { typeof(EcsEntity), typeof(EcsEntity), typeof(Ammo) });
                if (target == null) { _refused = true; Log("calcul de portée introuvable dans cette version du jeu : module coupé"); return false; }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.AntiHeliPortee");
                _harmony.Patch(target, postfix: new HarmonyMethod(typeof(AntiHeliPortee).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
            }
            catch (Exception e)
            {
                _refused = true;
                DisarmAll();
                Mod.Log.Warning("[ANTIHELICO] installation impossible : " + e.GetBaseException().Message);
                return false;
            }
            // the impact counter is what the combat watchdog watches: without it no rule may change a range
            try
            {
                var hit = AccessTools.Method(typeof(ShellHit), "InternalUpdate");
                if (hit != null)
                {
                    _harmony.Patch(hit, prefix: new HarmonyMethod(typeof(AntiHeliPortee).GetMethod(nameof(ImpactPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    _impactHook = true;
                }
            }
            catch (Exception e) { Log("compteur d'impacts non installé (" + e.GetBaseException().Message + ")"); }
            _armed = true;
            Log("calcul de portée surveillé (seulement raccourci, jamais allongé ; tireurs au sol, et pour les hélicos seulement la visée à l'œil des mitrailleuses de porte listées)" +
                (_impactHook ? " ; compteur d'impacts du chien de garde installé" : " ; SANS compteur d'impacts : toutes les règles restent en mesure seule"));
            return true;
        }

        // ---------------------------------------------------------------- hooks (plain reads of immutable snapshots, counters)

        /// ShellHitSystem.InternalUpdate(Entity& shellEntity): one impact processed (any thread). Counter only.
        static void ImpactPrefix() { if (!Campaign.MissionInerte) Interlocked.Increment(ref _impacts); }   // mission without the mod: nothing counted

        /// Hot path, maybe off the main thread: static Single GetAmmoShootingDistance(Entity shooterUnitEntity, Entity targetEntity,
        /// Ammunitions ammoInfo). Every rule for ground shooters; listed door guns only get their manual-aim caps; the range is only ever shortened.
        static void Postfix(EcsEntity shooterUnitEntity, EcsEntity targetEntity, Ammo ammoInfo, ref float __result)
        {
            if (Campaign.MissionInerte || !_armed || _bypass) return;                  // mission without the mod: vanilla range
            int shooter, target, id;
            bool heliAir, heliLow;
            UnitSnap units;
            try
            {
                Interlocked.Increment(ref _calls);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _offMain);
                if (!(__result > 0f) || ammoInfo == null) return;                      // nothing to shorten
                if (Environment.TickCount64 > Interlocked.Read(ref _validUntil)) { Interlocked.Increment(ref _stale); return; }
                target = targetEntity.EntityId;
                heliAir = _helisAir.Contains(target);
                heliLow = _helisLow.Contains(target);
                shooter = shooterUnitEntity.EntityId;
                units = _units;
                if (!heliAir)
                {
                    // R2 against ground targets; a helicopter with a known height below AirborneMin (in _helisLow but not in _helisAir)
                    // is a normal ground target and gets the same ground cap
                    if (_manualGroundLive && units.ManualShooters.Contains(shooter) && (heliLow || units.Ground.Contains(target)))
                        GroundManual(units, shooter, ammoInfo.Id, ref __result);
                    if (!heliLow) return;                                               // real ground target: done
                    // landed helicopter: continue so R3 (infrared missile against a low helicopter) still applies
                }
                Interlocked.Increment(ref _heliCalls);
                if (!units.Ground.Contains(shooter))
                {
                    if (!units.Air.Contains(shooter)) { Interlocked.Increment(ref _fromOther); return; }   // unknown shooters: vanilla
                    Interlocked.Increment(ref _fromAir);
                    if (heliAir && _ruleManual) AirManual(units, shooter, ammoInfo.Id, ref __result);    // listed door guns: manual-aim cap only, never R1/R3/R4
                    return;
                }
                Interlocked.Increment(ref _fromGround);
                id = ammoInfo.Id;
            }
            catch
            {
                if (Interlocked.Increment(ref _errCore) > MaxErrors) _armed = false;   // repeated errors: back to vanilla ranges
                return;
            }

            var t = _tables;
            if (t == null) return;
            float orig = __result, res = orig;
            int why = 0;
            bool ir = false;
            try { ir = t.Ir.Contains(id); } catch { }
            if (ir) Interlocked.Increment(ref _irCalls);

            // R3: infrared MANPADS-class missile against a low helicopter. Infantry: below 25 m, except a helicopter in flight within
            // 1000 m of the team while R4 is enforced with a fresh verdict for the pair (R4 then decides). Every other ground shooter:
            // below 10 m only. Any doubt (no verdict, stale data, error) keeps range 0.
            if (ir && heliLow && _ruleFloor)
            {
                bool cut = true;
                try
                {
                    if (units.IrInfantry.Contains(shooter))
                    {
                        long ck = ((long)shooter << 32) | (uint)target;
                        if (heliAir && _lowClose.Contains(ck))
                        {
                            if (_ruleLos && VueAA.MaskedFresh && VueAA.Evaluated.Contains(ck)) { cut = false; Interlocked.Increment(ref _closeAllowed); }
                            else Interlocked.Increment(ref _closeNoVerdict);
                        }
                    }
                    else cut = _helisVeryLow.Contains(target);
                }
                catch { cut = true; if (Interlocked.Increment(ref _errFloor) > MaxErrors) _ruleFloor = false; }
                if (cut) { res = 0f; why = WhyFloor; }
            }
            // R4: own line of sight of infantry teams (VueAA verdict of the last completed cycle); vehicles are only counted
            if (why == 0 && ir && heliAir)
            {
                try
                {
                    long key = ((long)shooter << 32) | (uint)target;
                    if (VueAA.MaskedFresh && VueAA.Masked.Contains(key))
                    {
                        if (!units.IrInfantry.Contains(shooter)) Interlocked.Increment(ref _losVehicle);
                        else if (_ruleLos) { res = 0f; why = WhyLos; }
                        else Interlocked.Increment(ref _losWould);
                    }
                }
                catch { if (Interlocked.Increment(ref _errLos) > MaxErrors) _ruleLos = false; }
            }
            // R1 + R2: caps against a helicopter in flight (never on air-defence rows)
            if (why == 0 && !ir && heliAir)
            {
                float cap = float.MaxValue;
                int w = 0;
                bool noCap = true;
                try { noCap = t.NoCap.Contains(id); } catch { }
                if (!noCap)
                {
                    if (_ruleCaps)
                    {
                        try { if (t.CapsOn && t.Caps.TryGetValue(id, out float c)) { cap = c; w = WhyCap; } }
                        catch { if (Interlocked.Increment(ref _errCaps) > MaxErrors) _ruleCaps = false; }
                    }
                    if (_ruleManual)
                    {
                        try
                        {
                            var m = _manual;
                            if (m != null && units.UnitOf.TryGetValue(shooter, out int uid) && m.Heli.TryGetValue(Key(uid, id), out float mc) && mc < cap) { cap = mc; w = WhyManual; }
                        }
                        catch { if (Interlocked.Increment(ref _errManual) > MaxErrors) _ruleManual = false; }
                    }
                    if (w != 0)
                    {
                        try
                        {
                            if (t.RpgFloor.TryGetValue(id, out float f) && cap < f) cap = f;
                            if (t.MinRange.TryGetValue(id, out float mr) && cap < mr) cap = mr;
                            if (res > cap) { res = cap; why = w; }
                        }
                        catch { if (Interlocked.Increment(ref _errCaps) > MaxErrors) _ruleCaps = false; }
                    }
                }
            }

            try
            {
                if (res < orig)
                {
                    __result = res;
                    // only the gun rules feed the combat watchdog: the infrared-only cuts (low flight, own line of sight) are intended
                    // and cannot silence every weapon
                    if (why == WhyCap || why == WhyManual) Interlocked.Increment(ref _changed);
                    switch (why)
                    {
                        case WhyCap: Interlocked.Increment(ref _cutCaps); break;
                        case WhyManual: Interlocked.Increment(ref _cutManualHeli); break;
                        case WhyFloor: Interlocked.Increment(ref _cutFloor); break;
                        case WhyLos: Interlocked.Increment(ref _cutLos); break;
                    }
                }
                if (heliAir)
                {
                    long seq = Interlocked.Increment(ref _ringSeq);
                    Interlocked.Exchange(ref _ring[(int)(seq & (RingSize - 1))], Pack(id, orig, res));
                }
            }
            catch { if (Interlocked.Increment(ref _errCore) > MaxErrors) _armed = false; }
        }

        /// R2 for an air shooter (listed helicopter door gun) against a helicopter in flight. Hot path.
        static void AirManual(UnitSnap units, int shooter, int id, ref float result)
        {
            try
            {
                var m = _manual; var t = _tables;
                if (m == null || t == null || t.NoCap.Contains(id) || t.Ir.Contains(id)) return;
                if (!units.UnitOf.TryGetValue(shooter, out int uid) || !m.Heli.TryGetValue(Key(uid, id), out float cap)) return;
                if (t.RpgFloor.TryGetValue(id, out float f) && cap < f) cap = f;
                if (t.MinRange.TryGetValue(id, out float mr) && cap < mr) cap = mr;
                if (result > cap)
                {
                    result = cap;
                    Interlocked.Increment(ref _changed);
                    Interlocked.Increment(ref _cutManualHeli);
                }
            }
            catch { if (Interlocked.Increment(ref _errManual) > MaxErrors) _ruleManual = false; }
        }

        /// R2 against a ground target (or a helicopter below AirborneMin). Hot path.
        static void GroundManual(UnitSnap units, int shooter, int id, ref float result)
        {
            try
            {
                var m = _manual; var t = _tables;
                if (m == null || t == null || !m.GroundOn || !_ruleManual) return;
                if (t.NoCap.Contains(id) || t.Ir.Contains(id)) return;
                if (!units.UnitOf.TryGetValue(shooter, out int uid) || !m.Ground.TryGetValue(Key(uid, id), out float cap)) return;
                if (t.MinRange.TryGetValue(id, out float mr) && cap < mr) cap = mr;
                if (result > cap)
                {
                    result = cap;
                    Interlocked.Increment(ref _changed);
                    Interlocked.Increment(ref _cutManualGround);
                }
            }
            catch { if (Interlocked.Increment(ref _errManual) > MaxErrors) _ruleManual = false; }
        }

        static long Key(int unitId, int ammoId) => ((long)unitId << 32) | (uint)ammoId;

        static long Pack(int id, float orig, float res)
        {
            const float MaxDm = (1 << 20) - 1;
            long a = Math.Clamp(id, 0, (1 << 23) - 1);
            long o = (long)(orig >= 0f ? Math.Min(orig * 10f, MaxDm) : 0f);
            long r = (long)(res >= 0f ? Math.Min(res * 10f, MaxDm) : 0f);
            return (a << 40) | (o << 20) | r;
        }

        // ---------------------------------------------------------------- main thread: rule states

        /// Rule flags from the tables, the kill-switches, the watchdog and the proof stage. Logs every change.
        static void UpdateRules()
        {
            var t = _tables; var m = _manual;
            bool block = _wdTripped || _stopByProof || !_impactHook;
            bool caps = !block && t != null && t.CapsOn && t.Caps.Count > 0 && Interlocked.Read(ref _errCaps) <= MaxErrors;
            bool floor = !block && t != null && t.Ir.Count > 0 && Interlocked.Read(ref _errFloor) <= MaxErrors;
            bool manual = !block && m != null && (m.Heli.Count + m.Ground.Count) > 0 && Interlocked.Read(ref _errManual) <= MaxErrors;
            bool los = !block && t != null && t.Ir.Count > 0 && _losGate && Interlocked.Read(ref _errLos) <= MaxErrors;
            _ruleCaps = caps; _ruleFloor = floor; _ruleManual = manual; _ruleLos = los;
            _manualGroundLive = manual && m.GroundOn && m.Ground.Count > 0;
            _armed = _patched && Interlocked.Read(ref _errCore) <= MaxErrors;

            string sig = $"{caps}|{floor}|{manual}|{_manualGroundLive}|{los}|{block}|{_armed}";
            if (sig == _lastRuleSig) return;
            _lastRuleSig = sig;
            string why = _wdTripped ? "chien de garde" : _stopByProof ? "preuve de coupure (batailles précédentes)" : !_impactHook ? "pas de compteur d'impacts" : null;
            Log($"règles : portée anti-hélico des fiches {State(caps, why ?? (t == null ? "base pas lue" : !t.CapsOn ? "vraies stats absentes ou option coupée" : Interlocked.Read(ref _errCaps) > MaxErrors ? "trop d'erreurs" : "aucune munition"))}" +
                $", visée à l'œil contre hélicos {State(manual, why ?? (m == null ? "Affuts pas encore lu" : Interlocked.Read(ref _errManual) > MaxErrors ? "trop d'erreurs" : "aucun plafond"))}" +
                $" et contre le sol {State(_manualGroundLive, why ?? (m == null ? "Affuts pas encore lu" : "aucun plafond"))}" +
                $" ; [VOL-BAS] missiles infrarouges sous {LowFlightFloor:0} m pour l'infanterie (sauf à moins de {LowFlightCloseRange:0} m avec vue propre vérifiée) et sous {VehicleLowFlightFloor:0} m pour les véhicules {State(floor, why ?? (t == null ? "base pas lue" : Interlocked.Read(ref _errFloor) > MaxErrors ? "trop d'erreurs" : "aucun missile retenu"))}" +
                $" ; [VUE-AA] vue propre de l'infanterie {State(los, why ?? (t == null ? "base pas lue" : t.Ir.Count == 0 ? "aucun missile retenu" : Interlocked.Read(ref _errLos) > MaxErrors ? "trop d'erreurs" : _gateReason ?? "en attente"))} (véhicules jamais bloqués par cette règle)");
            if (!_armed && _patched && !_killLogged) { _killLogged = true; Log("trop d'erreurs dans le calcul de portée : crochet désarmé pour cette bataille"); }
        }

        static string State(bool on, string reason) => on ? "ACTIVE" : $"en mesure seule ({reason})";

        // ---------------------------------------------------------------- main thread: snapshots

        /// Every second: live units of both sides split into helicopters, planes and ground units.
        static void Scan()
        {
            _map ??= new LuaMap();
            var snap = new UnitSnap();
            var bySide = new[] { new List<Unit>(), new List<Unit>() };
            var gate = new List<Unit>();
            var known = new Dictionary<int, Unit>();
            foreach (var h in _heliList) known[h.Uid] = h;
            var oldGround = new Dictionary<int, Unit>();
            foreach (var l in _groundBySide) foreach (var g in l) oldGround[g.Uid] = g;
            var helis = new List<Unit>();
            var manual = _manual;
            int unknown = 0;
            for (int side = 0; side < 2; side++)
            {
                var units = _map.GetUnits(V3.zero, 1_000_000f, side, -1);
                for (int i = 0; i < (units?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = units[i];
                        if (u == null || !u.IsAlive()) continue;
                        int uid = u.UID;
                        int t = TypeOf(u, uid);
                        if (t <= 0) { unknown++; continue; }
                        int eid = u.Entity.EntityId;
                        if ((t & (TypeHeli | TypePlane)) != 0)
                        {
                            snap.Air.Add(eid);
                            if ((t & TypeHeli) == 0) continue;
                            if (!known.TryGetValue(uid, out var h)) h = new Unit { Uid = uid };
                            h.U = u; h.Eid = eid; h.Side = side; h.Type = t;
                            h.UnitId = UnitIdOf(u, uid);
                            if (h.UnitId > 0)
                            {
                                // listed door guns: manual-aim caps from Affuts (against helicopters in AirManual, against the ground in GroundManual)
                                snap.UnitOf[eid] = h.UnitId;
                                if (manual != null && manual.GroundOn && HasGroundCap(manual, h.UnitId)) snap.ManualShooters.Add(eid);
                            }
                            helis.Add(h);
                        }
                        else if ((t & (TypeInfantry | TypeVehicle | TypeShip)) != 0)
                        {
                            snap.Ground.Add(eid);
                            if (!oldGround.TryGetValue(uid, out var g)) g = new Unit { Uid = uid };
                            g.U = u; g.Eid = eid; g.Side = side; g.Type = t;
                            g.UnitId = UnitIdOf(u, uid);
                            if (g.UnitId > 0)
                            {
                                snap.UnitOf[eid] = g.UnitId;
                                if (manual != null && manual.GroundOn && HasGroundCap(manual, g.UnitId)) snap.ManualShooters.Add(eid);
                            }
                            g.IrRange = IrRangeOf(g.UnitId, RoleOf(u, uid));
                            g.IrInfantry = g.IrRange > 0f && IsInfantry(t);
                            if (g.IrRange > 0f) gate.Add(g);
                            if (g.IrInfantry) snap.IrInfantry.Add(eid);
                            bySide[side].Add(g);
                        }
                    }
                    catch { _scanErrors++; }
                }
            }
            _heliList.Clear(); _heliList.AddRange(helis);
            _groundBySide = bySide;
            _gateShooters = gate;
            _unknownType = unknown;
            _units = snap;
        }

        /// Units.Type of an infantry unit: the infantry bit and no vehicle, ship or aircraft bit.
        static bool IsInfantry(int type) => (type & TypeInfantry) != 0 && (type & (TypeVehicle | TypeShip | TypeHeli | TypePlane)) == 0;

        static readonly Dictionary<int, bool> _groundCapByUnitId = new();
        static bool HasGroundCap(ManualTables m, int unitId)
        {
            if (_groundCapByUnitId.TryGetValue(unitId, out bool v)) return v;
            v = false;
            foreach (var k in m.Ground.Keys) if ((int)(k >> 32) == unitId) { v = true; break; }
            _groundCapByUnitId[unitId] = v;
            return v;
        }

        /// Units.Type bits of a unit (database row first, engine helper as a fallback), cached per uid. 0 = unknown.
        static int TypeOf(LuaUnit u, int uid)
        {
            if (_typeByUid.TryGetValue(uid, out int t)) return t;
            t = 0;
            try
            {
                int id = UnitIdOf(u, uid);
                if (id > 0 && !_typeByUnitId.TryGetValue(id, out t))
                {
                    t = 0;
                    var src = DbService._instance?.RawAccess;
                    if (src != null)
                    {
                        UnitsRow row = null;
                        if (src.Units.TryGetById(id, out row) && row != null) t = (int)row.Type;
                        _typeByUnitId[id] = t;
                    }
                }
            }
            catch { t = 0; }
            if (t <= 0 && !_typeHelperBroken)
            {
                try { t = Convert.ToInt32(BSH.GetUnitType(u.Entity)); }
                catch (Exception e) { _typeHelperBroken = true; t = 0; LogOnce("type", "type d'unité illisible par le moteur (" + e.GetBaseException().Message + ") : base de données seule"); }
            }
            if (t > 0 && _typeByUid.Count < 20000) _typeByUid[uid] = t;
            return t;
        }

        static int UnitIdOf(LuaUnit u, int uid)
        {
            if (_unitIdByUid.TryGetValue(uid, out int id)) return id;
            id = 0;
            try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { id = 0; }
            if (_unitIdByUid.Count < 20000) _unitIdByUid[uid] = id;
            return id;
        }

        static int RoleOf(LuaUnit u, int uid)
        {
            if (_roleByUid.TryGetValue(uid, out int r)) return r;
            r = -1;
            try { r = u.UnitRole; } catch { r = -1; }
            if (_roleByUid.Count < 20000) _roleByUid[uid] = r;
            return r;
        }

        /// Database ammunition Ids of a unit type (WeaponAmmunitions join, all options), cached per database. Main thread.
        static int[] AmmoOfUnit(DbSource src, int unitId)
        {
            if (src == null || unitId <= 0) return null;
            if (_ammoByUnitId == null || _joinSrc != src.Pointer)
            {
                var map = new Dictionary<int, List<int>>();
                var pairs = new Dictionary<int, List<(int w, int a)>>();
                foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
                {
                    if (wa == null) continue;
                    int u = wa.UnitId, w = wa.WeaponId, a = wa.AmmunitionId;
                    if (!map.TryGetValue(u, out var l)) map[u] = l = new List<int>();
                    if (!l.Contains(a)) l.Add(a);
                    if (!pairs.TryGetValue(u, out var p)) pairs[u] = p = new List<(int, int)>();
                    if (!p.Contains((w, a))) p.Add((w, a));
                }
                _ammoByUnitId = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
                _weaponAmmoByUnitId = pairs.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
                _joinSrc = src.Pointer;
                _irRangeByUnitId.Clear(); _groundCapByUnitId.Clear();
            }
            return _ammoByUnitId.TryGetValue(unitId, out var found) ? found : null;
        }

        static Dictionary<int, (int w, int a)[]> _weaponAmmoByUnitId;

        /// Main thread (Alt tool proof): weapon type and table ranges of every (weapon, ammunition) of a live ground unit's type.
        /// manual = the pair has a manual-aim cap (its per-unit copy may differ from the table row). Null when the unit is unknown.
        internal static List<(WeaponType type, float g, float l, float h, bool manual)> UnitWeaponRanges(int entityId)
        {
            var u = _units; var m = _manual;
            if (u == null || u.Air.Contains(entityId) || !u.UnitOf.TryGetValue(entityId, out int uid)) return null;   // ground units only (as before)
            var src = DbService._instance?.RawAccess;
            if (AmmoOfUnit(src, uid) == null || _weaponAmmoByUnitId == null || !_weaponAmmoByUnitId.TryGetValue(uid, out var pairs)) return null;
            var res = new List<(WeaponType, float, float, float, bool)>(pairs.Length);
            foreach (var (w, a) in pairs)
            {
                try
                {
                    if (!src.Weapons.TryGetById(w, out var wr) || wr == null) continue;
                    if (!src.Ammunitions.TryGetById(a, out var ar) || ar == null) continue;
                    bool manual = m != null && (m.Heli.ContainsKey(Key(uid, a)) || m.Ground.ContainsKey(Key(uid, a)));
                    res.Add((wr.Type, ar.GroundRange, ar.LowAltRange, ar.HighAltRange, manual));
                }
                catch { }
            }
            return res;
        }

        /// Largest range of the unit type's infrared MANPADS-class missiles (0 = none). AA infantry without a readable loadout: 6.5 km.
        static float IrRangeOf(int unitId, int role)
        {
            var t = _tables;
            if (t == null || t.Ir.Count == 0) return 0f;
            if (unitId > 0 && _irRangeByUnitId.TryGetValue(unitId, out float r)) return r;
            r = 0f;
            var src = DbService._instance?.RawAccess;
            var ammo = AmmoOfUnit(src, unitId);
            if (ammo != null)
            {
                foreach (int aid in ammo)
                    if (t.IrRange.TryGetValue(aid, out float lr) && lr > r) r = lr;
            }
            else if (role == 34) r = DefaultIrRange;
            if (unitId > 0) _irRangeByUnitId[unitId] = r;
            return r;
        }

        /// Every 0.25 s: position and height above the ground of the known helicopters; publishes the flying and low ones; feeds the probe.
        static void RefreshHelis(GameController gc, float now)
        {
            if (_heliList.Count == 0)
            {
                if (_helisAir.Count > 0) _helisAir = new HashSet<int>();
                if (_helisLow.Count > 0) _helisLow = new HashSet<int>();
                if (_helisVeryLow.Count > 0) _helisVeryLow = new HashSet<int>();
                _landed = 0;
                return;
            }
            MapMeta m = null;
            try { m = gc.MapMetaData; } catch { m = null; }
            var flying = new HashSet<int>();
            var low = new HashSet<int>();
            var veryLow = new HashSet<int>();
            int landed = 0, budget = RoofSampleBudget;
            foreach (var h in _heliList)
            {
                try
                {
                    if (!h.U.IsAlive()) { h.Airborne = false; continue; }
                    var p = h.U.GetPosition();
                    h.Pos = p; h.PosOk = true;
                    if (GroundHeight(m, h, p, now, ref budget, out float g))
                    {
                        h.Height = p.y - g; h.HeightKnown = true; h.Airborne = h.Height >= AirborneMin;
                        if (h.Height < LowFlightFloor) low.Add(h.Eid);
                        if (h.Height < VehicleLowFlightFloor) veryLow.Add(h.Eid);
                    }
                    else { h.HeightKnown = false; h.Airborne = true; }                   // height unreadable: counted as flying, no low-flight rule
                    if (h.Airborne) flying.Add(h.Eid); else landed++;
                    if (now >= h.NextProbe) { h.NextProbe = now + ProbeEvery; ProbeSample(h, now); }
                }
                catch { h.Airborne = false; h.PosOk = false; _scanErrors++; }
            }
            _landed = landed;
            _helisAir = flying;
            _helisLow = low;
            _helisVeryLow = veryLow;
        }

        static bool GroundHeight(MapMeta m, Unit h, V3 p, float now, ref int budget, out float ground)
        {
            ground = 0f;
            if (_terrainBroken) return false;
            if (m == null) { LogOnce("carte", "hauteur du sol illisible (carte absente) : hélicos comptés en vol, pas de règle de vol bas tant qu'elle manque"); return false; }
            TerrainKind tk;
            try
            {
                V3 q = p;
                m.GetTerrainAtHeight(ref q, out tk, out float th);
                if (float.IsNaN(th) || float.IsInfinity(th)) return false;
                ground = th;
            }
            catch (Exception e)
            {
                _terrainBroken = true;
                LogOnce("terrain", "hauteur du sol illisible (" + e.GetBaseException().Message + ") : hélicos comptés en vol, pas de règle de vol bas pour cette bataille");
                return false;
            }
            // building pixel: the height map holds the roof; use the lowest non-building ground around it, as VueAA does
            // (rule: height above the ground). Only ever lowers the ground; the roof is kept when nothing better is found.
            if (_roofBroken) return true;
            try
            {
                if (_roofBridgeMask < 0) { try { _roofBridgeMask = (int)MapMeta.BRIDGE_FLAG_FILTER & 0xFF; } catch { _roofBridgeMask = 240; } }
                if ((((int)tk & ~_roofBridgeMask) & 0xFF) != (int)TerrainKind.Buildings) return true;
                if (!float.IsNaN(h.RoofGround) && now - h.RoofTime < RoofReuseAge)
                {
                    float dx = p.x - h.RoofPos.x, dz = p.z - h.RoofPos.z;
                    if (dx * dx + dz * dz < RoofReuse * RoofReuse)
                    {
                        if (h.RoofGround < ground) { ground = h.RoofGround; _roofFixed++; } else _roofRaw++;
                        return true;
                    }
                }
                if (!RoofGridReady(m)) { _roofRaw++; return true; }
                if (RingGround(m, p, ref budget, out float g, out bool complete))
                {
                    h.RoofPos = p; h.RoofGround = g; h.RoofTime = now;
                    if (g < ground) { ground = g; _roofFixed++; } else _roofRaw++;
                }
                else
                {
                    if (complete) { h.RoofPos = p; h.RoofGround = float.PositiveInfinity; h.RoofTime = now; }   // no ground within 5 pixels: remembered, roof kept
                    _roofRaw++;                                                           // no ground found or sample budget spent: roof kept
                }
            }
            catch (Exception e)
            {
                _roofBroken = true;
                LogOnce("toit", "sol sous les hélicos au-dessus des bâtiments illisible (" + e.GetBaseException().Message + ") : hauteur mesurée depuis le toit pour cette bataille");
            }
            return true;
        }

        /// Map grid bounds for the ring search, read once per map. False when unreadable (logged once).
        static bool RoofGridReady(MapMeta m)
        {
            if (m.Pointer == _roofMap) return _roofGridOk;
            _roofMap = m.Pointer;
            _roofMaxX = m._maxX; _roofMaxY = m._maxY;
            _roofGridOk = _roofMaxX > 0 && _roofMaxY > 0;
            if (!_roofGridOk) LogOnce("grille", $"grille de la carte illisible (maxX {_roofMaxX}, maxY {_roofMaxY}) : hauteur des hélicos au-dessus des bâtiments mesurée depuis le toit");
            return _roofGridOk;
        }

        /// Lowest non-building, non-water ground on the nearest ring (1 to 5 pixels) around the pixel under p (same rule as VueAA.RingGround).
        /// Engine reads only; each sample costs one unit of the per-refresh budget. False when nothing was found (complete: every ring was
        /// read) or the budget ran out (complete false).
        static bool RingGround(MapMeta m, V3 p, ref int budget, out float ground, out bool complete)
        {
            ground = 0f; complete = false;
            int px = m.WorldToPixelX(p.x), py = m.WorldToPixelY(p.z);
            for (int r = 1; r <= RoofRingMax; r++)
            {
                if (budget < 8 * r) return false;                                         // a ring is read whole or not at all
                float best = float.MaxValue;
                for (int x = px - r; x <= px + r; x++) { RingSample(m, x, py - r, ref best, ref budget); RingSample(m, x, py + r, ref best, ref budget); }
                for (int y = py - r + 1; y < py + r; y++) { RingSample(m, px - r, y, ref best, ref budget); RingSample(m, px + r, y, ref best, ref budget); }
                if (best < float.MaxValue) { ground = best; return true; }
            }
            complete = true;
            return false;
        }

        static void RingSample(MapMeta m, int x, int y, ref float best, ref int budget)
        {
            if (x < 0 || y < 0 || x > _roofMaxX || y > _roofMaxY) return;
            budget--;
            V3 q = m.ConvertPosition(x, y, 0f);
            m.GetTerrainAtHeight(ref q, out TerrainKind tk, out float th);
            int t = ((int)tk & ~_roofBridgeMask) & 0xFF;
            if (t == (int)TerrainKind.Buildings || t == (int)TerrainKind.Water || t == (int)TerrainKind.LowWater) return;
            if (float.IsNaN(th) || float.IsInfinity(th)) return;
            if (th < best) best = th;
        }

        /// Every 0.5 s: shooters holding an infrared MANPADS-class missile and every helicopter, for VueAA.
        static void PublishLos(float now)
        {
            var t = _tables;
            if (t == null || t.Ir.Count == 0) { Los = null; if (_lowClose.Count > 0) _lowClose = new HashSet<long>(); return; }
            var shooters = new List<LosShooter>(_gateShooters.Count);
            // R3 close-range pairs: infantry teams and enemy helicopters in flight below LowFlightFloor within LowFlightCloseRange
            var lowHelis = _lowHelisTmp;
            lowHelis.Clear();
            foreach (var h in _heliList) if (h.PosOk && h.HeightKnown && h.Airborne && h.Height < LowFlightFloor) lowHelis.Add(h);
            var close = lowHelis.Count > 0 ? new HashSet<long>() : null;
            float close2 = LowFlightCloseRange * LowFlightCloseRange;
            foreach (var g in _gateShooters)
            {
                try
                {
                    var p = g.U.GetPosition();
                    bool infantry = IsInfantry(g.Type);
                    shooters.Add(new LosShooter(g.Eid, g.Side, p, infantry, g.IrRange));
                    if (close == null || !g.IrInfantry) continue;
                    foreach (var h in lowHelis)
                    {
                        if (h.Side == g.Side) continue;
                        float dx = h.Pos.x - p.x, dz = h.Pos.z - p.z;
                        if (dx * dx + dz * dz <= close2) close.Add(((long)g.Eid << 32) | (uint)h.Eid);
                    }
                }
                catch { }
            }
            lowHelis.Clear();
            if (close != null) _lowClose = close;                    // one assignment: the hook never sees a set being filled
            else if (_lowClose.Count > 0) _lowClose = new HashSet<long>();
            var helis = new List<LosHeli>(_heliList.Count);
            foreach (var h in _heliList) if (h.PosOk) helis.Add(new LosHeli(h.Eid, h.Side, h.Pos));
            Los = new LosSnapshot(shooters.ToArray(), helis.ToArray(), now);
        }

        /// Every 5 s (the database may be replaced or the real stats applied late): caps, air-defence rows, infrared missiles, RPG floors.
        static void BuildTables(float now)
        {
            try
            {
                var db = DbService._instance;
                var src = db != null && db.IsLoaded ? db.RawAccess : null;
                if (src == null) return;
                bool optionOff = OptionOff();
                bool real = Realism.RealModeOn && Realism.IsAppliedTo(db);                  // the option rows only exist in the real stats
                bool capsOn = real && !optionOff;
                var old = _tables;
                if (old != null && _tablesSrc != null && _tablesSrc.Pointer == src.Pointer && old.CapsOn == capsOn) return;
                if (optionOff && !_optionOffLogged) { _optionOffLogged = true; Log($"option {OptionName} désactivée (OptionsInactives) : aucune portée anti-hélico des fiches"); }
                if (!real && !_notAppliedLogged && now - _battleStart >= 30f) { _notAppliedLogged = true; Log("vraies stats non appliquées à la base de la mission : portée anti-hélico des fiches en mesure seule"); }

                var t = new Tables { Src = src.Pointer, CapsOn = capsOn };
                // air-defence rows (never capped) from the live bits
                foreach (var a in Props.Rows(src.Ammunitions.GetAll()))
                {
                    if (a == null) continue;
                    long tt;
                    try { tt = (long)a.TargetType; } catch { continue; }
                    if ((tt & AirDefenceBits) != 0) t.NoCap.Add(a.Id);
                }
                foreach (int id in DedicatedAa) t.NoCap.Add(id);

                // infrared MANPADS-class missiles, checked on the live rows
                var irOk = new List<string>(); var irBad = new List<string>();
                foreach (var (id, label) in IrCandidates)
                {
                    Ammo row = null;
                    try { if (!src.Ammunitions.TryGetById(id, out row)) row = null; } catch { row = null; }
                    if (row == null) { irBad.Add($"{label} ({id}) absente"); continue; }
                    long tt; SeekerType seeker; float low;
                    try { tt = (long)row.TargetType; seeker = row.Seeker; low = row.LowAltRange; } catch { irBad.Add($"{label} ({id}) illisible"); continue; }
                    if ((tt & HeliAirBits) == 0) { irBad.Add($"{label} ({id}) ne vise pas les aéronefs"); continue; }
                    if ((tt & MissileBits) != 0) { irBad.Add($"{label} ({id}) intercepte des missiles"); continue; }
                    if ((tt & GroundBits) != 0) { irBad.Add($"{label} ({id}) vise aussi le sol"); continue; }
                    if (seeker != SeekerType.FireAndForget) { irBad.Add($"{label} ({id}) guidage {seeker}"); continue; }
                    t.Ir.Add(id);
                    t.IrRange[id] = low > 0f ? Math.Min(low, 9000f) : DefaultIrRange;
                    irOk.Add($"{label} ({id}, {(low > 0f ? low.ToString("0", Inv) : "?")} m)");
                }
                string irLine = $"missiles infrarouges concernés (impossible sous {LowFlightFloor:0} m pour l'infanterie, sauf à moins de {LowFlightCloseRange:0} m avec vue propre vérifiée ; sous {VehicleLowFlightFloor:0} m pour les véhicules ; vue propre exigée pour l'infanterie seulement) :{(irOk.Count > 0 ? string.Join(", ", irOk) : "aucun")}" +
                                (irBad.Count > 0 ? $" ; écartés : {string.Join(", ", irBad)}" : "") + " ; jamais concernés : RBS-70, Osa, Tunguska, Sosna, Tor, Pantsir, Buk, S-300/350/400, Patriot, IRIS-T, NASAMS, C-RAM, canons radar";
                if (irLine != _irLine) { _irLine = irLine; LogLow(irLine); }

                // minimum ranges (data): logged only
                var mins = new List<string>();
                foreach (var (id, min, label) in MinRangeChecks)
                {
                    try
                    {
                        if (!src.Ammunitions.TryGetById(id, out var row) || row == null) continue;
                        float mr = row.MinimalRange;
                        mins.Add($"{label} {mr.ToString("0", Inv)}{(Math.Abs(mr - min) <= 1f ? "" : $" (prévu {min.ToString("0", Inv)})")}");
                    }
                    catch { }
                }
                string minLine = "portées minimales lues (données) : " + (mins.Count > 0 ? string.Join(", ", mins) : "aucune");
                if (minLine != _minLine) { _minLine = minLine; LogLow(minLine); }

                // RPG floors: never below min(table value, row GroundRange)
                int rpgAt = 0, rpgTotal = 0; var rpgLow = new List<string>();
                foreach (var (floor, ids) in RpgFloors)
                {
                    foreach (int id in ids)
                    {
                        rpgTotal++;
                        Ammo row = null;
                        try { if (!src.Ammunitions.TryGetById(id, out row)) row = null; } catch { row = null; }
                        if (row == null) continue;
                        float g = 0f, l = 0f;
                        try { g = row.GroundRange; l = row.LowAltRange; } catch { }
                        float f = g > 0f ? Math.Min(floor, g) : floor;
                        t.RpgFloor[id] = f;
                        if (l + 1f >= f) rpgAt++; else if (rpgLow.Count < 12) rpgLow.Add($"{id} {l.ToString("0", Inv)} m");
                    }
                }
                string rpgLine = $"RPG contre hélico en vol : {rpgAt}/{rpgTotal} lignes à la portée prévue (300 m réutilisables, 200 m jetables, ou leur portée au sol)" +
                                 (rpgLow.Count > 0 ? $" ; encore à la valeur du jeu (données à venir) : {string.Join(", ", rpgLow)}" : "") + " ; aucun plafond du mod ne descend sous ces valeurs";
                if (rpgLine != _rpgLine) { _rpgLine = rpgLine; Log(rpgLine); }

                // R1 caps from the rows' own LowAltRange
                var skipped = new List<string>();
                int total = 0;
                foreach (var (max, ids) in PlannedCaps)
                {
                    foreach (int id in ids)
                    {
                        total++;
                        if (t.NoCap.Contains(id) || t.Ir.Contains(id) || t.Caps.ContainsKey(id)) { if (t.NoCap.Contains(id)) skipped.Add($"{id} (anti-aérienne)"); continue; }
                        Ammo row = null;
                        try { if (!src.Ammunitions.TryGetById(id, out row)) row = null; } catch { row = null; }
                        if (row == null) { skipped.Add($"{id} (absente)"); continue; }
                        float low, min = 0f;
                        try { low = row.LowAltRange; min = row.MinimalRange; } catch { skipped.Add($"{id} (illisible)"); continue; }
                        if (min > 0f) t.MinRange[id] = min;
                        if (!(low > 0f)) { skipped.Add($"{id} (basse altitude 0)"); continue; }
                        if (low > max + 1f) { skipped.Add($"{id} (basse altitude {low.ToString("0", Inv)} > {max.ToString("0", Inv)})"); continue; }
                        if (t.RpgFloor.TryGetValue(id, out float f) && low < f) low = f;
                        t.Caps[id] = Math.Max(low, min);
                    }
                }
                _tables = t;
                _tablesSrc = src;
                _irRangeByUnitId.Clear();
                string line = $"portées anti-hélico ({OptionName}) : {t.Caps.Count}/{total} munitions plafonnées{(capsOn ? "" : " (en mesure seule)")}, munitions anti-aériennes jamais plafonnées {t.NoCap.Count}" +
                              (skipped.Count > 0 ? $" ; ignorées (portée du jeu gardée) : {string.Join(", ", skipped)}" : " ; aucune ignorée");
                if (line != _capsLine) { _capsLine = line; Log(line); }
            }
            catch (Exception e) { Warn("caps", "tables des portées anti-hélico illisibles : " + e.GetBaseException().Message); }
        }

        static bool OptionOff()
        {
            var v = Realism.OptionsInactives?.Value ?? "";
            foreach (var s in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (s.Equals(OptionName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// Every 10 s: per-unit manual-aim caps from Affuts for the unit types present (ground units and helicopters with listed door guns),
        /// against helicopters and against the ground. The ground caps are kept even when the per-unit copies are proven: the hook only
        /// shortens, so copies Affuts wrote are unchanged, and copies it skipped (table row, shared, cloned later) still get the cap.
        /// Air-defence rows and infrared missiles are never capped; RPG floors and minimum ranges hold.
        static void BuildManual()
        {
            var t = _tables;
            var src = DbService._instance?.RawAccess;
            if (t == null || src == null || _affutsBroken) return;
            bool proven;
            try { proven = Affuts.CopiesProuvees; }
            catch (Exception e) { AffutsFailed(e); return; }
            var m = new ManualTables { GroundOn = true };
            var unitIds = new HashSet<int>();
            foreach (var l in _groundBySide) foreach (var g in l) if (g.UnitId > 0) unitIds.Add(g.UnitId);
            foreach (var h in _heliList) if (h.UnitId > 0) unitIds.Add(h.UnitId);
            int types = 0;
            foreach (int uid in unitIds)
            {
                var ammo = AmmoOfUnit(src, uid);
                if (ammo == null) continue;
                bool any = false;
                foreach (int aid in ammo)
                {
                    if (t.NoCap.Contains(aid) || t.Ir.Contains(aid)) continue;
                    float floorH = t.RpgFloor.TryGetValue(aid, out float f) ? f : 0f;
                    float min = t.MinRange.TryGetValue(aid, out float mr) ? mr : RowMinRange(src, aid);
                    try
                    {
                        if (Affuts.TryGetCap(uid, aid, true, out float ch) && ch > 0f) { m.Heli[Key(uid, aid)] = Math.Max(ch, Math.Max(floorH, min)); any = true; }
                        // same guard as Affuts.Target: a minimum range at or above 90 % of the cap leaves the mount at its own range
                        if (Affuts.TryGetCap(uid, aid, false, out float cg) && cg > 0f && !(min > 0f && min >= 0.9f * cg)) { m.Ground[Key(uid, aid)] = Math.Max(cg, min); any = true; }
                    }
                    catch (Exception e) { AffutsFailed(e); if (_affutsBroken) return; }
                }
                if (any) types++;
            }
            _groundCapByUnitId.Clear();
            _manual = m;
            string line = $"visée à l'œil (Affuts) : {types} type(s) d'unités présents avec un plafond, {m.Heli.Count} contre hélicos, {m.Ground.Count} contre le sol ; copies par unité prouvées : {(proven ? "oui" : "non")}";
            if (line != _manualLine)
            {
                _manualLine = line;
                Log(line);
                if (m.Ground.Count > 0)
                    Log(proven
                        ? "copies par unité prouvées : les plafonds contre le sol restent surveillés au combat pour les copies que Affuts n'a pas pu écrire (sans effet sur les copies écrites)"
                        : "copies par unité non prouvées : les plafonds contre le sol sont appliqués au combat par ce module, les fiches peuvent encore montrer la longue portée (les cercles Alt sont corrigés)");
            }
            _lastCopiesProven = proven;
        }

        /// MinimalRange of an ammunition row (0 when unreadable). Main thread.
        static float RowMinRange(DbSource src, int aid)
        {
            try { return src.Ammunitions.TryGetById(aid, out var row) && row != null ? Math.Max(0f, row.MinimalRange) : 0f; }
            catch { return 0f; }
        }

        static void AffutsFailed(Exception e)
        {
            if (++_affutsErrors >= 5) { _affutsBroken = true; _manual = null; Log("Affuts illisible (" + e.GetBaseException().Message + ") : visée à l'œil en mesure seule pour cette bataille"); }
        }

        // ---------------------------------------------------------------- R4: line-of-sight gate and safety line

        /// Every 5 s: the infantry line-of-sight rule may act when VueAA's map self-test, copy check and building calibration passed in
        /// this battle; otherwise measurement only. Logs every change, a failed map check once (warning), and the waiting reason every 2 min.
        static void UpdateLosGate(float now)
        {
            bool before = _losGate;
            string reason;
            if (VueAA.ReadyForGating)
            {
                _losGate = true;
                reason = "carte vérifiée (auto-test, copie et calibrage des bâtiments réussis)";
            }
            else
            {
                _losGate = false;
                reason = "vue AA pas prête : " + VueAA.GateReason;
            }
            _gateReason = reason;
            if (before != _losGate)
            {
                LogLos(_losGate
                    ? $"règle de vue propre armée pour les équipes d'infanterie à missiles infrarouges : {reason} ; pas de tir sur un hélico caché par la forêt, un bâtiment ou le relief ; véhicules anti-aériens jamais bloqués par cette règle"
                    : $"règle de vue propre de l'infanterie en mesure seule : {reason} ; hélicos sous {LowFlightFloor:0} m hors d'atteinte des missiles d'épaule même à moins de {LowFlightCloseRange:0} m tant que la vue n'est pas vérifiée");
                _nextGateLog = now + 120f;
            }
            if (_losGate) _losFailedLogged = false;
            else if (VueAA.GateFailed && !_losFailedLogged)
            {
                _losFailedLogged = true;
                _nextGateLog = now + 120f;
                Mod.Log.Warning($"[VUE-AA] règle de vue propre de l'infanterie en mesure seule pour cette bataille : {reason}");
            }
            else if (now >= _nextGateLog && Interlocked.Read(ref _irCalls) > 0)
            {
                _nextGateLog = now + 120f;                                   // only once infrared missiles were really considered, every 2 min at most
                LogLos($"règle de vue propre de l'infanterie en mesure seule : {reason} ; tirs d'infanterie qui auraient été refusés {Interlocked.Read(ref _losWould)}");
            }
        }

        /// Every 5 s, on the watchdog clock (stopped while the game is paused or the end screen is up): launches of infantry teams holding
        /// infrared missiles, from the game's own missile counts, and the share of their pairs with enemy helicopters in flight within the
        /// missile range that VueAA masks. Ten minutes of such exposure without a single launch, while at least 90 % of those pairs were
        /// masked, writes a warning line. Nothing is switched off: forest, buildings or relief may really keep the teams silent.
        static void LosSafety(float now)
        {
            float clock = _wdClock;
            float dt = _sfClockPrev >= 0f ? clock - _sfClockPrev : 0f;
            _sfClockPrev = clock;
            if (!(dt > 0f)) dt = 0f; else if (dt > WatchMaxStep) dt = WatchMaxStep;

            _sfTeams.Clear();
            foreach (var g in _gateShooters) if (g.IrInfantry) _sfTeams.Add(g);
            if (_sfTeams.Count == 0) return;

            bool anyAir = false;
            long pairs = 0, masked = 0, evaluated = 0;
            bool fresh = VueAA.MaskedFresh;
            var maskedSet = VueAA.Masked; var evalSet = VueAA.Evaluated;
            foreach (var h in _heliList)
            {
                if (!h.Airborne || !h.PosOk) continue;
                anyAir = true;
                foreach (var g in _sfTeams)
                {
                    if (g.Side == h.Side || !g.PosOk) continue;
                    float dx = h.Pos.x - g.Pos.x, dz = h.Pos.z - g.Pos.z;
                    if (dx * dx + dz * dz > g.IrRange * g.IrRange) continue;
                    pairs++;
                    if (!fresh) continue;
                    long key = ((long)g.Eid << 32) | (uint)h.Eid;
                    if (evalSet.Contains(key)) evaluated++;
                    if (maskedSet.Contains(key)) masked++;
                }
            }
            if (!anyAir) return;                                             // no helicopter in flight: nothing to judge, no read

            ReadLaunches(out int launches, out int readable);
            if (readable > 0) _sfWindowReadable++;
            if (launches > 0)
            {
                _sfLaunches += launches;
                _sfExposure = 0f; _sfPairs = _sfMasked = _sfEvaluated = 0; _sfWindowReadable = 0;   // a launch ends the silent window
                return;
            }
            if (pairs == 0) return;
            _sfExposure += dt; _sfExposureTotal += dt;
            _sfPairs += pairs; _sfMasked += masked; _sfEvaluated += evaluated;
            if (_sfExposure < SafetyWindow) return;

            double share = (double)_sfMasked / _sfPairs;
            if (_sfWindowReadable > 0 && share >= SafetyMaskedShare && _sfWarnings < SafetyMaxWarnings)
            {
                _sfWarnings++;
                Mod.Log.Warning($"[VUE-AA] sécurité de la règle de vue : aucune équipe d'infanterie à missiles infrarouges n'a tiré pendant {_sfExposure / 60f:0} min de jeu " +
                                $"avec des hélicos ennemis en vol à portée, et {share * 100:0} % de ces paires étaient masquées ({_sfMasked}/{_sfPairs} relevés, dont {_sfEvaluated} évalués par la vue AA) ; " +
                                $"règle de vue propre {(_ruleLos ? "active" : "en mesure seule")}, rien n'est coupé (la forêt, les bâtiments ou le relief peuvent vraiment les empêcher de voir) ; " +
                                $"lancements d'infanterie comptés dans la bataille {_sfLaunches} ({_sfWarnings}/{SafetyMaxWarnings})");
            }
            _sfExposure = 0f; _sfPairs = _sfMasked = _sfEvaluated = 0; _sfWindowReadable = 0;   // next window
        }

        /// Missile count of every infantry team holding infrared missiles (round robin, at most 24 reads per check): a lower count than the
        /// previous read is a launch. readable = teams whose count could be read this time.
        static void ReadLaunches(out int launches, out int readable)
        {
            launches = 0; readable = 0;
            if (_sfBroken) return;
            GetAmmoDel del = null;
            try { del = GameController._instance?._GetEcsEventBus_k__BackingField?.Gameplay?.GetUnitsAmmo; } catch { del = null; }
            if (del == null)
            {
                if (_once.Add("bus des missiles")) LogLos("sécurité de la règle de vue : lecture des missiles indisponible (bus du jeu absent), lancements de l'infanterie non comptés tant qu'il manque");
                return;
            }
            int n = _sfTeams.Count, reads = 0, visited = 0;
            int start = (int)((uint)_sfRound % (uint)n);
            for (int k = 0; k < n && reads < SafetyMaxReads && !_sfBroken; k++)
            {
                var g = _sfTeams[(start + k) % n];
                visited++;
                int count;
                bool infinite;
                if (!_sfFilterByUnitId.TryGetValue(g.UnitId, out var filter))
                {
                    long errorsBefore = _sfReadErrors;
                    filter = FindMissileFilter(del, g, ref reads, out count, out infinite);
                    // a search spoiled by a read error is tried again later instead of marking the unit type unreadable
                    if (g.UnitId > 0 && _sfFilterByUnitId.Count < 500 && (filter != null || _sfReadErrors == errorsBefore)) _sfFilterByUnitId[g.UnitId] = filter;
                    if (filter == null) { _sfUnreadable++; continue; }
                }
                else
                {
                    if (filter == null) { _sfUnreadable++; continue; }
                    reads++;
                    count = ReadMissiles(del, g.Uid, filter, out infinite);
                }
                if (count < 0 || infinite) { _sfUnreadable++; _sfLastCount.Remove(g.Uid); continue; }
                readable++;
                if (_sfLastCount.TryGetValue(g.Uid, out int last) && count < last) launches += last - count;
                _sfLastCount[g.Uid] = count;
            }
            _sfRound += Math.Max(1, visited);
            if (_sfLastCount.Count > 2000) _sfLastCount.Clear();
        }

        /// First name filter (HUD name, then name) of the team's infrared missile rows that the game's ammunition read recognises
        /// (a name it does not know returns a negative count). Null when none does. Main thread, once per unit type per battle.
        static AmmoFilter FindMissileFilter(GetAmmoDel del, Unit g, ref int reads, out int count, out bool infinite)
        {
            count = int.MinValue; infinite = false;
            var t = _tables;
            var src = DbService._instance?.RawAccess;
            var ammo = t != null ? AmmoOfUnit(src, g.UnitId) : null;
            if (ammo == null) return null;
            foreach (int aid in ammo)
            {
                if (!t.Ir.Contains(aid)) continue;
                string hud = null, name = null;
                try { if (src.Ammunitions.TryGetById(aid, out var row) && row != null) { hud = row.HUDName; name = row.Name; } } catch { }
                for (int pass = 0; pass < 2 && !_sfBroken; pass++)
                {
                    string label = pass == 0 ? hud : name;
                    if (string.IsNullOrEmpty(label) || (pass == 1 && label == hud)) continue;
                    var filter = new AmmoFilter();
                    filter.Type = NodeAmmoKind.Any;
                    filter.SpecificAmmoNameFilter = label;
                    reads++;
                    int c = ReadMissiles(del, g.Uid, filter, out bool inf);
                    if (c >= 0)
                    {
                        count = c; infinite = inf;
                        if (_once.Add("missiles " + g.UnitId)) LogLos($"sécurité de la règle de vue : missiles de l'unité de type {g.UnitId} lus par {(pass == 0 ? "nom HUD" : "nom")} « {label} » (munition {aid}, {c} en réserve{(inf ? ", munitions infinies : lancements non comptés" : "")})");
                        return filter;
                    }
                }
            }
            if (_once.Add("missiles " + g.UnitId)) LogLos($"sécurité de la règle de vue : missiles infrarouges de l'unité de type {g.UnitId} illisibles par le jeu (aucun nom reconnu), lancements non comptés pour elle");
            return null;
        }

        /// Ammunition count of one unit for a name filter through the game's script bus (same read as AntiHeliTouches). int.MinValue when unreadable.
        static int ReadMissiles(GetAmmoDel del, int uid, AmmoFilter filter, out bool infinite)
        {
            infinite = false;
            try
            {
                _sfOne ??= new Il2CppSystem.Collections.Generic.List<int>();
                _sfOneRo ??= new Il2CppSystem.Collections.Generic.IReadOnlyList<int>(_sfOne.Pointer);
                _sfOne.Clear();
                _sfOne.Add(uid);
                var d = del.Invoke(_sfOneRo, filter);
                _sfReads++;
                infinite = d.InfiniteAmmo;
                return d.AmmoCount;
            }
            catch (Exception e)
            {
                if (++_sfReadErrors >= SafetyMaxReadErrors && !_sfBroken)
                {
                    _sfBroken = true;
                    LogLos("sécurité de la règle de vue : lecture des missiles en échec (" + e.GetBaseException().Message + "), lancements de l'infanterie non comptés dans cette bataille");
                }
                return int.MinValue;
            }
        }

        // ---------------------------------------------------------------- combat watchdog

        /// Every 5 s: impacts per minute, contact between the sides, silence while the rules shorten ranges -> switch-off. Every window runs
        /// on the watchdog clock (_wdClock), which does not advance while the game is paused or the battle end screen is up.
        static void Watch(float now)
        {
            bool unityPaused;
            try { unityPaused = UnityEngine.Time.timeScale <= 0f; } catch { unityPaused = false; }
            float t = UnityEngine.Time.time;
            float step = _wdPrevTime >= 0f ? t - _wdPrevTime : 0f;
            _wdPrevTime = t;
            if (!(step > 0f)) step = 0f; else if (step > WatchMaxStep) step = WatchMaxStep;
            long imp = Interlocked.Read(ref _impacts), chg = Interlocked.Read(ref _changed);
            bool contact = Contact(now);                                                    // refreshes the ground positions
            _lastContact = contact;
            // frozen positions = game paused, frozen or end screen (Time.timeScale stays 1 in this game, Time.time keeps running)
            double sig = 0; int cnt = 0;
            for (int s = 0; s < 2; s++) foreach (var u in _groundBySide[s]) if (u.PosOk) { sig += u.Pos.x + 3.0 * u.Pos.z; cnt++; }
            foreach (var h in _heliList) if (h.PosOk) { sig += h.Pos.x + 3.0 * h.Pos.z + 7.0 * h.Pos.y; cnt++; }
            _wdMoving = _wdSigCount >= 0 && (cnt != _wdSigCount || Math.Abs(sig - _wdSig) > 0.5);
            _wdSig = sig; _wdSigCount = cnt;

            // end screen and game pause stop the clock. A signal is only trusted while the battle really stands still: an impact or a
            // movement between two checks while the signal stayed up (at both checks and at every 0.25 s frame between, see Frame) is
            // a strike; HoldStrikes strikes in a row prove it wrong, and it is ignored for the rest of the battle (a wrong signal must
            // never freeze the watchdog while the rules might be stopping every weapon; a signal stuck up strikes on every pair).
            // Any other pair (nothing moved, or the signal went down) clears the strikes: one stray movement is not a proof.
            int hold = HoldSignals();
            int both = hold & _wdHoldPrev;
            bool live = imp != _wdImpPrevTick || _wdMoving;
            int bad = 0;
            for (int i = 0, bit = 1; i < _wdHoldStrike.Length; i++, bit <<= 1)
            {
                _wdHoldStrike[i] = (both & bit) != 0 && live ? _wdHoldStrike[i] + 1 : 0;
                if (_wdHoldStrike[i] >= HoldStrikes) bad |= bit;
            }
            if (bad != 0)
            {
                _wdHoldBad |= bad; hold &= ~bad;
                Log($"chien de garde : le jeu continue (tirs ou mouvements) à {HoldStrikes} contrôles de suite alors qu'il semble {((bad & HoldEnd) != 0 ? "en fin de bataille" : "en pause")} : ce signal est ignoré pour cette bataille");
            }
            _wdHoldPrev = hold; _wdImpPrevTick = imp;
            if (unityPaused || hold != 0)
            {
                if ((hold & HoldEnd) != 0 && _wdRetest)
                {
                    // the end screen freezes the battle: no verdict, the rules stay on, the silence count keeps the last real impact
                    _wdRetest = false; _wdRetestDone = true; _wdConfirmed = false; _wdSuspect = false;
                    Log("chien de garde : écran de fin de bataille pendant l'essai des règles remises : essai annulé, rien n'est enregistré");
                }
                return;                                                                     // the watchdog clock does not run
            }
            _wdClock += step;
            float g = _wdClock;
            _impHistory.Enqueue((g, imp));
            while (_impHistory.Count > 0 && g - _impHistory.Peek().t > 65f) _impHistory.Dequeue();
            if (_wdImp < 0) { _wdImp = imp; _wdLastImpact = g; _wdChg = chg; _wdChgAtImpact = chg; _wdLastChange = g; }
            if (imp != _wdImp)
            {
                _wdImp = imp; _wdLastImpact = g; _wdFired = true;
                _wdChgAtImpact = chg; _wdChecks = 0; _wdContact = 0;
            }
            if (chg != _wdChg) { _wdChg = chg; _wdLastChange = g; }
            _wdChecks++;
            if (contact) _wdContact++;

            if (_wdTripped)
            {
                if (!_wdResolved)
                {
                    if (imp > _wdImpAtTrip)
                    {
                        _wdResolved = true;
                        float delay = g - _wdTripTime;
                        // fast resume only makes the rules suspect: the rules may just have been holding some shots, as intended
                        _wdSuspect = delay <= WatchResume;
                        Log(!_wdSuspect
                            ? $"chien de garde : les tirs ont repris {delay:0} s après la coupure, trop tard pour accuser les règles"
                            : _wdRetestDone
                                ? $"chien de garde : les tirs ont repris {delay:0} s après la coupure des règles (essai déjà fait dans cette bataille : règles coupées jusqu'à la fin, rien n'est enregistré)"
                                : $"chien de garde : les tirs ont repris {delay:0} s après la coupure des règles : elles seront remises {RetestLen:0} s pour vérifier, dès que les tirs sont nourris");
                    }
                    else if (g - _wdTripTime > WatchResume * 3f)
                    {
                        _wdResolved = true;
                        Log($"chien de garde : toujours aucun tir {WatchResume * 3f:0} s après la coupure : le silence ne venait pas des règles (elles restent coupées pour cette bataille)");
                    }
                }
                // re-test once per battle, while fire is flowing again: rules that break firing silence every weapon when put back
                long last60 = _impHistory.Count > 0 ? imp - _impHistory.Peek().imp : 0;
                if (_wdSuspect && !_wdRetestDone && contact && last60 >= RetestMinImpacts)
                {
                    _wdTripped = false; _wdRetest = true; _wdRetestStart = g; _wdChgAtRetest = chg;
                    _wdImpAtRetest = _wdImpAtQuiet = -1; _wdRetestBase = _wdQuietBase = g;          // impact baselines taken later, after the grace
                    _wdRetestChecks = _wdRetestContact = _wdRetestMoving = 0;
                    Log($"chien de garde : les tirs ont repris ({last60} impacts en une minute), règles remises {RetestLen:0} s de jeu pour vérifier qu'elles ne coupent pas tous les tirs " +
                        $"(les {RetestGrace:0} premières secondes ne comptent pas : tirs déjà partis)");
                }
                return;
            }
            if (_wdRetest)
            {
                // the re-test window replaces the 180 s silence rule while it runs (checks under a pause or the end screen never get here)
                _wdRetestChecks++;
                if (contact) _wdRetestContact++;
                if (_wdMoving) _wdRetestMoving++;
                float elapsed = g - _wdRetestStart;
                // impact baselines on later checks, never on the check that put the rules back: rounds fired before the test
                // (shells, missiles, bursts under way) land during the grace; the last part must then stay completely quiet
                if (_wdImpAtRetest < 0) { if (elapsed >= RetestGrace) { _wdImpAtRetest = imp; _wdRetestBase = g; } return; }
                if (_wdImpAtQuiet < 0) { if (elapsed >= RetestLen - RetestQuiet) { _wdImpAtQuiet = imp; _wdQuietBase = g; } return; }
                if (elapsed < RetestLen) return;
                _wdRetest = false; _wdRetestDone = true;
                long cuts = chg - _wdChgAtRetest, late = imp - _wdImpAtRetest, lateQuiet = imp - _wdImpAtQuiet;
                float judged = g - _wdRetestBase, quiet = g - _wdQuietBase;
                bool silent = late <= RetestMaxImpacts && lateQuiet == 0;
                bool cut = cuts >= WatchMinChanges && g - _wdLastChange <= WatchRecentChange;
                bool inContact = _wdRetestContact >= ContactShare * _wdRetestChecks;
                bool running = _wdRetestMoving >= ContactShare * _wdRetestChecks;          // frozen positions: paused or frozen game, no proof
                if (silent && cut && inContact && running)
                {
                    _wdConfirmed = true; _wdTripped = true; _wdResolved = true; _wdTripTime = g; _wdImpAtTrip = imp;
                    DisarmAll();
                    _armed = _patched;                                                      // the hook keeps measuring
                    Mod.Log.Warning($"[ANTIHELICO] chien de garde : les règles remises ont de nouveau arrêté tous les tirs ({late} impact(s) en {judged:0} s après {RetestGrace:0} s laissées aux tirs déjà partis, " +
                                    $"aucun dans les {quiet:0} dernières secondes, {cuts} portées raccourcies, camps au contact, unités en mouvement) : règles coupées pour cette bataille, preuve enregistrée en fin de bataille");
                    Mod.Notify(TxtKey.N_AH_RULES_OFF);
                }
                else
                {
                    _wdConfirmed = false; _wdSuspect = false;
                    // the silence count is not restarted: Watch kept _wdLastImpact at the last real impact all through the test, so rules
                    // that do stop every weapon trip again 180 s after that impact, not 180 s after the end of the test
                    Log(!silent
                        ? $"chien de garde : les tirs continuent avec les règles remises ({late} impacts en {judged:0} s après {RetestGrace:0} s laissées aux tirs déjà partis, dont {lateQuiet} dans les {quiet:0} dernières secondes) : le silence d'avant ne venait pas d'elles, règles actives"
                        : !running
                            ? $"chien de garde : essai sans conclusion (presque aucun impact en {judged:0} s, mais les unités n'ont bougé qu'à {_wdRetestMoving}/{_wdRetestChecks} contrôles : jeu en pause ou figé) : règles actives, rien n'est enregistré"
                            : $"chien de garde : essai sans conclusion (presque aucun impact en {judged:0} s, mais {(cuts < WatchMinChanges ? $"seulement {cuts} portées raccourcies" : !cut ? "plus aucune portée raccourcie dans la dernière minute" : "camps pas assez au contact")}) : règles actives, rien n'est enregistré");
                    Mod.Notify(TxtKey.N_AH_RULES_BACK);
                }
                return;
            }
            bool rulesActive = _ruleCaps || _ruleManual;                                    // only the gun rules can be blamed for a general silence
            if (!rulesActive || !_wdFired) return;
            float silence = g - _wdLastImpact;
            long changesSince = chg - _wdChgAtImpact;
            if (silence < WatchSilence || changesSince < WatchMinChanges || g - _wdLastChange > WatchRecentChange) return;
            if (_wdChecks < WatchMinChecks || _wdContact < ContactShare * _wdChecks) return;

            _wdTripped = true; _wdTripTime = g; _wdImpAtTrip = imp;
            _wdResolved = false; _wdSuspect = false;                                        // a later trip (after a passed re-test) is judged again
            DisarmAll();
            _armed = _patched;                                                              // the hook keeps measuring
            Mod.Log.Warning($"[ANTIHELICO] chien de garde : aucun impact depuis {silence:0} s alors que les deux camps sont au contact et que les règles des armes à tir direct ont raccourci {changesSince} portées : " +
                            $"toutes les règles anti-hélico coupées {(_wdRetestDone ? "pour cette bataille" : "(remises à l'essai une fois si les tirs reprennent vite)")} (portée des fiches {Interlocked.Read(ref _cutCaps)}, visée à l'œil {Interlocked.Read(ref _cutManualHeli)}/{Interlocked.Read(ref _cutManualGround)}, vol bas {Interlocked.Read(ref _cutFloor)}, vue propre {Interlocked.Read(ref _cutLos)})");
            Mod.Notify(TxtKey.N_AH_RULES_OFF_3MIN);
        }

        /// Watchdog check (main thread, every 5 s): signals that stop the watchdog clock, minus those proven wrong in this battle.
        /// HoldEnd: the battle end screen (Campaign.BattleOver, or the screen's flag once it was seen down in this battle: a flag
        /// already up when the battle started never counts). HoldPause / HoldScale: the game session's own pause flag / speed 0.
        static int HoldSignals()
        {
            int hold = 0;
            if ((_wdHoldBad & HoldEnd) == 0)
            {
                bool end = Campaign.BattleOver;
                if (!end)
                {
                    try
                    {
                        bool active = Il2CppBrokenArrow.Client.Ecs.UI.Menu.BattleEnd.BattleEndScreen.Active;   // static Boolean get_Active()
                        if (!active) _wdEndHiddenSeen = true;
                        else end = _wdEndHiddenSeen;
                    }
                    catch { }
                }
                if (end) hold |= HoldEnd;
            }
            if ((_wdHoldBad & (HoldPause | HoldScale)) != (HoldPause | HoldScale))
            {
                try
                {
                    var session = GameController._instance?._GameSession_k__BackingField;
                    if (session != null)
                    {
                        if ((_wdHoldBad & HoldPause) == 0 && session.IsPaused) hold |= HoldPause;       // Boolean get_IsPaused()
                        if ((_wdHoldBad & HoldScale) == 0 && session.TimeScale <= 0f) hold |= HoldScale; // Single get_TimeScale()
                    }
                }
                catch { }
            }
            return hold;
        }

        /// Contact: a live unit of each side within 3 km of each other (ground units and helicopters, positions refreshed here).
        static bool Contact(float now)
        {
            for (int side = 0; side < 2; side++) RefreshPositions(side, now);
            var a = new List<V3>(); var b = new List<V3>();
            foreach (var g in _groundBySide[0]) if (g.PosOk) a.Add(g.Pos);
            foreach (var g in _groundBySide[1]) if (g.PosOk) b.Add(g.Pos);
            foreach (var h in _heliList) if (h.PosOk) (h.Side == 0 ? a : b).Add(h.Pos);
            float r2 = ContactRange * ContactRange;
            foreach (var p in a)
                foreach (var q in b)
                {
                    float dx = p.x - q.x, dz = p.z - q.z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            return false;
        }

        static void RefreshPositions(int side, float now)
        {
            if (now - _posTime[side] < 1f) return;
            _posTime[side] = now;
            foreach (var g in _groundBySide[side])
            {
                try { g.Pos = g.U.GetPosition(); g.PosOk = true; }
                catch { g.PosOk = false; }
            }
        }

        /// Battle end: only a failed re-test (rules put back, every weapon silent again) counts as proof against the rules (hidden preference, this version).
        static void ProofAtEnd()
        {
            if (_wdRetest) { Log("chien de garde : bataille finie pendant l'essai des règles remises (rien n'est enregistré)"); return; }
            if (!_wdTripped) return;
            // proof is only set by the re-test: the rules put back stopped every weapon again while the sides were in contact
            if (_wdConfirmed)
            {
                _proofConfirmed.Value = _proofConfirmed.Value + 1;
                Log($"chien de garde : coupure confirmée, les règles remises ont de nouveau arrêté tous les tirs ({_proofConfirmed.Value}/{ProofBattles})" +
                    (_proofConfirmed.Value >= ProofBattles ? " : les règles resteront en mesure seule dans les prochaines batailles de cette version" : ""));
                return;
            }
            if (_wdSuspect)
            {
                Log(_wdRetestDone
                    ? "chien de garde : coupure non vérifiée (un seul essai des règles par bataille, déjà fait) : rien n'est enregistré"
                    : "chien de garde : coupure non vérifiée (bataille finie avant l'essai des règles remises) : rien n'est enregistré");
                return;
            }
            float since = _wdClock - _wdTripTime;                              // watchdog clock: pauses and the end screen do not count
            if (!_wdResolved && since < WatchResume)
            {
                Log($"chien de garde : bataille finie {since:0} s après la coupure, trop tôt pour conclure (rien n'est enregistré)");
                return;
            }
            _proofNoEffect.Value = _proofNoEffect.Value + 1;
            Log($"chien de garde : coupure sans reprise rapide des tirs, les règles ne sont pas mises en cause (batailles de ce type : {_proofNoEffect.Value})");
        }

        // ---------------------------------------------------------------- display helper for the Alt tool (AltCercles, main thread)

        /// For a circle of radius <paramref name="radius"/> drawn for the unit <paramref name="entityId"/>: the range the rules really apply
        /// when every weapon of that unit drawn at that radius is shortened to the same value. False when nothing applies or it is ambiguous.
        internal static bool TryShownRange(int entityId, float radius, out float applied, out string why)
        {
            applied = radius; why = null;
            if (!(radius > 1f)) return false;
            var u = _units; var t = _tables; var m = _manual;
            if (u == null || t == null || !u.UnitOf.TryGetValue(entityId, out int uid)) return false;
            var src = DbService._instance?.RawAccess;
            var ammo = AmmoOfUnit(src, uid);
            if (ammo == null) return false;
            float found = -1f;
            bool uncapped = false, conflict = false;
            bool air = u.Air.Contains(entityId);                    // helicopter door guns: the hook applies the manual-aim caps only, never R1
            foreach (int aid in ammo)
            {
                Ammo row = null;
                try { if (!src.Ammunitions.TryGetById(aid, out row)) row = null; } catch { row = null; }
                if (row == null) continue;
                float g, l;
                try { g = row.GroundRange; l = row.LowAltRange; } catch { continue; }
                bool noCap = t.NoCap.Contains(aid) || t.Ir.Contains(aid);
                if (g > 0f && Math.Abs(g - radius) <= 1f)
                {
                    float v = g;
                    if (!noCap && _manualGroundLive && m != null && m.Ground.TryGetValue(Key(uid, aid), out float c) && c < v) { v = Math.Max(c, t.MinRange.TryGetValue(aid, out float mr) ? mr : 0f); why = "visée à l'œil"; }
                    Note(v);
                }
                if (l > 0f && Math.Abs(l - radius) <= 1f)
                {
                    float v = l;
                    if (!noCap)
                    {
                        if (!air && _ruleCaps && t.CapsOn && t.Caps.TryGetValue(aid, out float c1) && c1 < v) v = c1;
                        if (_ruleManual && m != null && m.Heli.TryGetValue(Key(uid, aid), out float c2) && c2 < v) { v = c2; why = "visée à l'œil contre hélico"; }
                        if (t.RpgFloor.TryGetValue(aid, out float f) && v < f) v = Math.Min(l, f);
                    }
                    Note(v);
                }
            }
            if (uncapped || conflict || found < 0f) return false;
            applied = found;
            return true;

            void Note(float v)
            {
                if (v >= radius - 1f) { uncapped = true; return; }
                if (found < 0f) found = v;
                else if (Math.Abs(found - v) > 1f) conflict = true;
            }
        }

        // ---------------------------------------------------------------- main thread: measurement

        /// Hook samples into per-ammunition statistics (the ring keeps the last RingSize samples).
        static void Drain()
        {
            long seq = Interlocked.Read(ref _ringSeq);
            if (seq <= _drained) { if (seq < _drained) _drained = seq; return; }
            long from = _drained + 1;
            if (seq - _drained > RingSize) { _lostSamples += seq - _drained - RingSize; from = seq - RingSize + 1; }
            for (long s = from; s <= seq; s++)
            {
                long v = Interlocked.Read(ref _ring[(int)(s & (RingSize - 1))]);
                int id = (int)(v >> 40);
                float orig = ((v >> 20) & 0xFFFFF) / 10f, res = (v & 0xFFFFF) / 10f;
                if (!_agg.TryGetValue(id, out var a))
                {
                    if (_agg.Count >= MaxAgg) continue;
                    _agg[id] = a = new Agg();
                }
                a.N++;
                if (res < orig) { a.Capped++; a.Cap = res; if (orig > a.MaxOrig) a.MaxOrig = orig; }
                else if (orig > a.MaxFree) a.MaxFree = orig;
            }
            _drained = seq;
        }

        /// Every 5 s: for up to 3 flying helicopters (in turn) and the nearest enemy ground unit, the engine's raw range for rows 19, 70
        /// and 204, the height above the ground and the engine's altitude class.
        static void Measure(float now)
        {
            var flying = new List<Unit>();
            foreach (var h in _heliList) if (h.Airborne && h.PosOk) flying.Add(h);
            if (flying.Count == 0) return;
            var src = DbService._instance?.RawAccess;
            var rows = new Ammo[Probes.Length];
            for (int i = 0; i < Probes.Length; i++)
            {
                try { if (src == null || !src.Ammunitions.TryGetById(Probes[i].id, out rows[i])) rows[i] = null; }
                catch { rows[i] = null; }
            }
            int n = Math.Min(MaxHelisPerMeasure, flying.Count);
            int start = (int)((uint)_measureTurn % (uint)flying.Count);
            _measureTurn += n;
            for (int k = 0; k < n; k++)
            {
                var h = flying[(start + k) % flying.Count];
                int enemy = 1 - h.Side;
                if (enemy < 0 || enemy > 1) continue;
                RefreshPositions(enemy, now);
                Unit best = null;
                double bestSq = double.MaxValue;
                foreach (var g in _groundBySide[enemy])
                {
                    if (!g.PosOk) continue;
                    double dx = g.Pos.x - h.Pos.x, dy = g.Pos.y - h.Pos.y, dz = g.Pos.z - h.Pos.z;
                    double sq = dx * dx + dy * dy + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = g; }
                }
                if (best == null) continue;

                string level = "?", altitude = "?";
                if (!_altBroken)
                {
                    try
                    {
                        AltLevel lvl = default;
                        float alt = Aircraft.GetCurrentAltitude(h.U.Entity, out lvl);
                        level = lvl.ToString();
                        altitude = alt.ToString("0", Inv);
                    }
                    catch (Exception e) { _altBroken = true; LogOnce("alt", "classe d'altitude du jeu illisible (" + e.GetBaseException().Message + ")"); }
                }

                var key = new StringBuilder(level);
                var detail = new StringBuilder();
                for (int i = 0; i < Probes.Length; i++)
                {
                    string r = "absente";
                    var row = rows[i];
                    if (row != null && _probeFails < 10)
                    {
                        float v = float.NaN;
                        _bypass = true;
                        try { v = BSH.GetAmmoShootingDistance(best.U.Entity, h.U.Entity, row); }
                        catch (Exception e) { _probeFails++; LogOnce("probe", "appel direct du calcul de portée impossible : " + e.GetBaseException().Message); }
                        finally { _bypass = false; }
                        r = float.IsNaN(v) ? "erreur" : v.ToString("0", Inv);
                        string g = "?", l = "?";
                        try { g = row.GroundRange.ToString("0", Inv); l = row.LowAltRange.ToString("0", Inv); } catch { }
                        detail.Append($" ; {Probes[i].label} ({Probes[i].id}) {r} m [fiche sol {g} / basse altitude {l}]");
                    }
                    else detail.Append($" ; {Probes[i].label} ({Probes[i].id}) {r}");
                    key.Append(' ').Append(Probes[i].id).Append('=').Append(r);
                }

                string k2 = key.ToString();
                _patternTotal++;
                if (_patterns.ContainsKey(k2) || _patterns.Count < MaxPatterns) _patterns[k2] = _patterns.GetValueOrDefault(k2) + 1;

                bool changed = !_lastMeasure.TryGetValue(h.Uid, out var last) || last.key != k2 || now - last.time >= 30f;
                if (!changed) continue;
                if (_measureLines >= MaxMeasureLines) { LogOnce("lines", "mesures directes : suite résumée dans les relevés"); continue; }
                _measureLines++;
                _lastMeasure[h.Uid] = (k2, now);
                string heliName = "?", shooterName = "?";
                try { heliName = h.U.Name ?? "?"; } catch { }
                try { shooterName = best.U.Name ?? "?"; } catch { }
                Log($"mesure : hélico {heliName} (uid {h.Uid}, camp {h.Side}) à {(h.HeightKnown ? h.Height.ToString("0", Inv) : "?")} m du sol, altitude du jeu {altitude} m (classe {level}) ; " +
                    $"unité au sol ennemie la plus proche {shooterName} (uid {best.Uid}) à {Math.Sqrt(bestSq).ToString("0", Inv)} m (valeurs brutes du jeu, sans les règles)" + detail);
            }
        }

        /// Once per battle: weapon types the Alt tool draws low/high altitude circles for.
        static void LogFowTool(float now)
        {
            try
            {
                var t = FowTool.Instance;
                var cfg = t != null ? t._config : null;
                if (cfg == null)
                {
                    if (now - _battleStart >= 120f) LogOnce("fow", "outil Alt : configuration pas encore disponible (nouvel essai toutes les 5 s)");
                    return;
                }
                _fowDone = true;
                var set = cfg.LowHighAltRangeWeapons;
                if (set == null) { Log("outil Alt : liste des armes à cercles basse/haute altitude absente"); return; }
                var names = new List<string>();
                foreach (WeaponType w in Enum.GetValues(typeof(WeaponType)))
                {
                    try { if (set.Contains(w)) names.Add(w.ToString()); }
                    catch (Exception e) { names.Add("illisible (" + e.GetBaseException().Message + ")"); break; }
                }
                Log($"outil Alt : {set.Count} type(s) d'armes avec cercles basse/haute altitude : {(names.Count > 0 ? string.Join(", ", names) : "aucun")}");
            }
            catch (Exception e) { _fowDone = true; Log("outil Alt : liste illisible (" + e.GetBaseException().Message + ")"); }
        }

        /// Once per battle: the engine's helicopter altitude constants, read only (their setters are never called).
        static void LogConstantsOnce()
        {
            float lowC = float.NaN, highC = float.NaN;
            string line;
            try
            {
                lowC = NavConst.HELICOPTER_PREFFERED_ALTITUDE_LOW;
                highC = NavConst.HELICOPTER_PREFFERED_ALTITUDE_HIGH;
                float hover = NavConst.HELICOPTERS_HOVER_ALTITUDE, cargo = NavConst.HELICOPTERS_CARGO_ALTITUDE;
                line = $"constantes d'altitude des hélicos lisibles (lecture seule, jamais modifiées) : basse {lowC.ToString("0.#", Inv)} m, haute {highC.ToString("0.#", Inv)} m, stationnaire {hover.ToString("0.#", Inv)} m, cargo {cargo.ToString("0.#", Inv)} m";
            }
            catch (Exception e) { line = "constantes d'altitude des hélicos illisibles (" + e.GetBaseException().Message + ") : bandes de la sonde par défaut"; }
            if (lowC > 0f && highC > lowC) _probeMid = (lowC + highC) / 2f;
            _constLine = line;
            LogLow(line + $" ; sonde : mode déduit de la hauteur mesurée (le mode réel n'est pas lisible sans lecture de composant), vol bas sous {_probeMid.ToString("0", Inv)} m, stationnaire sous 3 m/s");
        }

        /// One helicopter sample per second: height above the ground by deduced flight mode.
        static void ProbeSample(Unit h, float now)
        {
            if (!h.HeightKnown) { _probeUnknown++; h.PrevOk = false; return; }
            float speed = -1f;
            if (h.PrevOk && now > h.PrevTime)
            {
                float dx = h.Pos.x - h.PrevPos.x, dy = h.Pos.y - h.PrevPos.y, dz = h.Pos.z - h.PrevPos.z;
                speed = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / (now - h.PrevTime);
            }
            h.PrevPos = h.Pos; h.PrevTime = now; h.PrevOk = true;
            if (speed < 0f) return;
            float ht = h.Height;
            int mode = ht < AirborneMin ? 0 : speed < 3f ? 1 : ht < _probeMid ? 2 : 3;
            var a = _probe[mode];
            a.N++; a.Sum += ht;
            if (ht < a.Min) a.Min = ht;
            if (ht > a.Max) a.Max = ht;
            int bin = 0;
            while (bin < ProbeBins.Length && ht >= ProbeBins[bin]) bin++;
            _probeHist[bin]++;
        }

        static void ReportProbe(bool final)
        {
            long total = 0;
            foreach (var a in _probe) total += a.N;
            if (total == 0 && !final) return;
            var sb = new StringBuilder(final ? "bilan de la sonde d'altitude" : "sonde d'altitude");
            sb.Append($" ({total} relevés d'hélicos, hauteur illisible {_probeUnknown}) :");
            for (int i = 0; i < _probe.Length; i++)
            {
                var a = _probe[i];
                sb.Append(i == 0 ? " " : " ; ").Append(ProbeNames[i]).Append(' ');
                sb.Append(a.N == 0 ? "aucun" : $"{a.N} (min {a.Min.ToString("0", Inv)} / moy {(a.Sum / a.N).ToString("0", Inv)} / max {a.Max.ToString("0", Inv)} m)");
            }
            sb.Append(" ; répartition :");
            for (int b = 0; b < _probeHist.Length; b++)
            {
                string label = b == 0 ? $"<{ProbeBins[0]:0}" : b == ProbeBins.Length ? $"{ProbeBins[b - 1]:0}+" : $"{ProbeBins[b - 1]:0}-{ProbeBins[b]:0}";
                sb.Append($" {label} m {_probeHist[b]}");
            }
            sb.Append($" ; portées de missiles infrarouges mises à 0 contre un hélico en vol bas (sous {LowFlightFloor:0} m pour l'infanterie, {VehicleLowFlightFloor:0} m pour les véhicules) : {Interlocked.Read(ref _cutFloor)}" +
                      $", infanterie à moins de {LowFlightCloseRange:0} m laissée à la règle de vue {Interlocked.Read(ref _closeAllowed)} (sans verdict de vue utilisable {Interlocked.Read(ref _closeNoVerdict)})");
            string s = sb.ToString();
            if (!final && s == _lastProbe) return;
            _lastProbe = s;
            LogLow(s);
        }

        static void Report(bool final)
        {
            long calls = Interlocked.Read(ref _calls), off = Interlocked.Read(ref _offMain), stale = Interlocked.Read(ref _stale);
            long heli = Interlocked.Read(ref _heliCalls), fromGround = Interlocked.Read(ref _fromGround), fromAir = Interlocked.Read(ref _fromAir), fromOther = Interlocked.Read(ref _fromOther);
            // every shortened range: gun rules (watchdog counter) plus the infrared-only cuts, which the watchdog never counts
            long changed = Interlocked.Read(ref _changed) + Interlocked.Read(ref _cutFloor) + Interlocked.Read(ref _cutLos), imp = Interlocked.Read(ref _impacts);
            string sig = $"{calls}|{heli}|{changed}|{Interlocked.Read(ref _errCore)}|{_patternTotal}|{imp}";
            if (!final && (sig == _lastSig || (calls == 0 && _patternTotal == 0))) return;
            _lastSig = sig;
            Drain();

            long perMin = 0;
            if (_impHistory.Count >= 2)
            {
                var first = _impHistory.Peek();
                float span = _wdClock - first.t;
                if (span > 5f) perMin = (long)Math.Round((imp - first.imp) * 60.0 / span);
            }
            var sb = new StringBuilder();
            sb.Append(final ? "bilan" : "relevé");
            sb.Append($" : appels du calcul de portée {calls} (hors fil principal {off}, instantanés périmés {stale}), sur un hélico {heli} ");
            sb.Append($"(tireur au sol {fromGround}, aérien {fromAir}, inconnu {fromOther}), missiles infrarouges {Interlocked.Read(ref _irCalls)} ; portées raccourcies {changed} : ");
            sb.Append($"fiches {Interlocked.Read(ref _cutCaps)}, visée à l'œil contre hélico {Interlocked.Read(ref _cutManualHeli)} / contre le sol {Interlocked.Read(ref _cutManualGround)}, ");
            sb.Append($"vol bas {Interlocked.Read(ref _cutFloor)} (infanterie proche laissée à la règle de vue {Interlocked.Read(ref _closeAllowed)}, sans verdict utilisable {Interlocked.Read(ref _closeNoVerdict)}), " +
                      $"vue propre de l'infanterie {Interlocked.Read(ref _cutLos)} (auraient été refusés en mesure {Interlocked.Read(ref _losWould)}) ; comptées par le chien de garde (armes à tir direct) {Interlocked.Read(ref _changed)}");
            sb.Append($" ; règles fiches {OnOff(_ruleCaps)}, visée {OnOff(_ruleManual)}{(_manualGroundLive ? " (+sol)" : "")}, vol bas {OnOff(_ruleFloor)}, vue propre {OnOff(_ruleLos)}");
            sb.Append($" ; erreurs cœur {Interlocked.Read(ref _errCore)}, fiches {Interlocked.Read(ref _errCaps)}, visée {Interlocked.Read(ref _errManual)}, vol bas {Interlocked.Read(ref _errFloor)}, vue {Interlocked.Read(ref _errLos)}");
            sb.Append($" ; chien de garde : impacts {imp} ({perMin}/min), contact {(_lastContact ? "oui" : "non")}, silence {(_wdFired ? (_wdClock - _wdLastImpact).ToString("0", Inv) + " s" : "-")}{(_wdTripped ? ", RÈGLES COUPÉES" : "")}{(_wdRetest ? ", essai des règles remises en cours" : "")}{(_impactHook ? "" : ", compteur absent")}");
            sb.Append($" ; hélicos en vol {_helisAir.Count}, sous {LowFlightFloor:0} m {_helisLow.Count} (dont sous {VehicleLowFlightFloor:0} m {_helisVeryLow.Count}), paires infanterie-hélico bas à moins de {LowFlightCloseRange:0} m {_lowClose.Count}, posés {_landed}, unités au sol {_units.Ground.Count}, tireurs à missiles infrarouges {_gateShooters.Count} (dont infanterie {_units.IrInfantry.Count}), sans type {_unknownType}");
            if (_roofFixed + _roofRaw > 0) sb.Append($" ; hauteur des hélicos au-dessus d'un bâtiment : mesurée depuis le sol voisin {_roofFixed}, depuis la hauteur lue (pas de sol plus bas trouvé) {_roofRaw}");
            if (_scanErrors > 0) sb.Append($", lectures d'unités en échec {_scanErrors}");
            sb.Append($" ; vue propre : véhicules à vue masquée jamais bloqués {Interlocked.Read(ref _losVehicle)}, sécurité de l'infanterie : lancements comptés {_sfLaunches}, " +
                      $"sans tir depuis {_sfExposure:0} s avec hélicos à portée (paires masquées {_sfMasked}/{_sfPairs}), exposition totale {_sfExposureTotal:0} s, " +
                      $"lectures de missiles {_sfReads} (illisibles {_sfUnreadable}, erreurs {_sfReadErrors}{(_sfBroken ? ", lecture coupée" : "")}), alertes {_sfWarnings}");

            var cappedTop = _agg.Where(kv => kv.Value.Capped > 0).OrderByDescending(kv => kv.Value.Capped).Take(10)
                .Select(kv => $"{kv.Key} {kv.Value.MaxOrig.ToString("0", Inv)}->{kv.Value.Cap.ToString("0", Inv)} m x{kv.Value.Capped}").ToList();
            if (cappedTop.Count > 0) sb.Append(" ; raccourcies (munition portée->plafond) : ").Append(string.Join(", ", cappedTop));
            var t = _tables;
            var freeTop = _agg.Where(kv => kv.Value.N > kv.Value.Capped).OrderByDescending(kv => kv.Value.N - kv.Value.Capped).Take(8)
                .Select(kv => $"{kv.Key} {kv.Value.MaxFree.ToString("0", Inv)} m x{kv.Value.N - kv.Value.Capped}{(t != null && (t.Caps.ContainsKey(kv.Key) || t.Ir.Contains(kv.Key)) ? "" : " (hors table)")}").ToList();
            if (freeTop.Count > 0) sb.Append(" ; non raccourcies (munition portée max) : ").Append(string.Join(", ", freeTop));
            if (_lostSamples > 0) sb.Append($" ; échantillons non lus {_lostSamples}");
            if (_patterns.Count > 0)
                sb.Append($" ; mesures directes ({_patternTotal}) : ").Append(string.Join(", ", _patterns.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"[{kv.Key}] x{kv.Value}")));
            Log(sb.ToString());
        }

        static string OnOff(bool on) => on ? "oui" : "non";
    }
}
