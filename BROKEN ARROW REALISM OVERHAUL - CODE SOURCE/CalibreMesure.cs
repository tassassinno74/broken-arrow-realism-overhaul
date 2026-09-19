// RealismOverhaul - calibre damage (v0.23.0, calibre_design.md §2 and §4), both sides alike, solo campaign only.
//  1) MEASUREMENT ONLY, logs [CALIBRE-MESURE]: proves how BattleSystemHelpers.CalculateHitDamage turns penetration and armour into
//     damage before any rule relies on it. Gates read after a battle:
//      G1 kinetic: on vehicles every hit with P/A < 0.5 gives exactly 0 and hits with 0.5 <= P/A < 1 follow
//         Damage x (0.1 + 1.8 (P/A - 0.5)) within 3 % (a floor at MINIMAL_DAMAGE_COEFFICIENT instead of 0 is recognised);
//      G2 HEAT: on vehicles result / base = r^k / (r^k + 1) within 3 % on at least 20 hits, k = HEAT_CURVE_COEFFICIENT;
//      G3 AI: the scoring calls outside impacts already return exact 0 against vehicles (1 call in 16 is sampled);
//      G4 suppression: CalculateStressDamage during impacts, health damage 0 against > 0;
//      G5 top attack: which ammunition and targets come with forceTopArmorAttack.
//     A postfix at Priority.First queues plain values of impact hits (LeurresMesure.HitDepth) on vehicles and infantry. The main
//     thread resolves each hit against every candidate armour row of the unit (loaded unit copy and table row) and cross-checks
//     the engine's own DamageFormulaKinetic / DamageFormulaHEAT / GetArmorBySideAndType. Nothing in the game is changed.
//  2) HALF-ARMOUR ZERO RULE, logs [DEMI-BLINDAGE] (switched on after a measurement): on ground vehicles a shaped-charge / explosive
//     round (a kinetic one too when G1 found a floor) whose result is below the half-armour ratio does 0. Artillery, mortars, MLRS,
//     ballistic and cruise missiles, bombs and blasts of 20 m or more (plane guns excepted) are exempt. Own Harmony id, installed
//     lazily only while the automatic stage is 1. The stage changes only at the end of a normal battle, from the gates cumulated in
//     hidden prefs; the combat-health watchdog (no hit for 3 min while units are in contact and cuts continue), the error
//     kill-switch, an interrupted session or a changed engine setting all fall back to stage 0 (rule off, measurement again).
//  Shared helpers for the damage modules of this version (also used by Couvert): DegatsAmmoCache, DegatsScan, DegatsMunitions.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using ArmorSides = Il2CppBrokenArrow.Client.Ecs.BattleSystem.ArmorSides;
using ArmorType = Il2CppBrokenArrow.DataBase.Enums.ArmorType;
using Traj = Il2CppBrokenArrow.DataBase.Enums.TrajectoryType;
using WType = Il2CppBrokenArrow.DataBase.Enums.WeaponType;
using UiOpt = Il2CppBrokenArrow.DataBase.Enums.UiOptions;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using BattleSettings = Il2CppBrokenArrow.Client.Ecs.Configs.BattleSystemSettings;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using ArmorsRow = Il2CppBrokenArrow.DataBase.Models.Armors;
using WeaponsRow = Il2CppBrokenArrow.DataBase.Models.Weapons;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using DbSource = Il2CppBrokenArrow.DataBase.DataBaseSourceData;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    // ==================================================================================================== shared helpers

    /// Ammunition Id through a tiny per-thread cache keyed by the native row pointer (one interop call on a miss, no allocation).
    static class DegatsAmmoCache
    {
        [ThreadStatic] static IntPtr _p0, _p1, _p2, _p3;
        [ThreadStatic] static int _i0, _i1, _i2, _i3, _next, _gen;
        static int _generation = 1;

        /// Main thread, at each battle start: rows of the previous battle may be gone and their addresses reused.
        internal static void NewBattle() => Interlocked.Increment(ref _generation);

        internal static int Id(Ammo a)
        {
            int g = Volatile.Read(ref _generation);
            if (_gen != g) { _gen = g; _p0 = _p1 = _p2 = _p3 = IntPtr.Zero; }
            IntPtr p = a.Pointer;
            if (p == IntPtr.Zero) return 0;
            if (p == _p0) return _i0;
            if (p == _p1) return _i1;
            if (p == _p2) return _i2;
            if (p == _p3) return _i3;
            int id = a.Id;
            switch (_next++ & 3)
            {
                case 0: _p0 = p; _i0 = id; break;
                case 1: _p1 = p; _i1 = id; break;
                case 2: _p2 = p; _i2 = id; break;
                default: _p3 = p; _i3 = id; break;
            }
            return id;
        }
    }

    /// One live unit of the damage modules' scan (main thread only).
    sealed class DegatsUnit
    {
        public LuaUnit U;
        public int Eid, Uid, UnitId, Type, Side, Owner;
        public V3 Pos;
        /// Units.Type bits: 2 infantry, 4 vehicle, 8 helicopter, 16 plane, 32 ship.
        public bool Vehicle => (Type & 4) != 0 && (Type & (2 | 8 | 16)) == 0;
        public bool Infantry => (Type & 2) != 0 && (Type & (8 | 16)) == 0;
        public bool Ground => Type != 0 && (Type & (8 | 16)) == 0;
    }

    /// Alive units of both sides, rebuilt at most every 0.5 s on the main thread and shared by CalibreMesure, DemiBlindage and Couvert.
    sealed class DegatsScan
    {
        internal readonly List<DegatsUnit> All = new();
        internal float Time;
        internal int Errors;

        static DegatsScan _last;
        static IntPtr _gcPtr;
        static GameController _gcHold;                                                       // held: no address reuse
        static LuaMap _map;
        static readonly Dictionary<int, (int unitId, int type, int owner)> _meta = new();
        static readonly Dictionary<int, int> _typeByUnitId = new();
        static readonly Dictionary<int, string> _names = new();

        /// Main thread. A new GameController (new battle) drops every cache.
        internal static DegatsScan Get(float now)
        {
            var gc = GameController._instance;
            IntPtr p = gc == null ? IntPtr.Zero : gc.Pointer;
            if (p != _gcPtr) { Forget(); _gcPtr = p; _gcHold = gc; }
            if (_last != null && now >= _last.Time && now - _last.Time < 0.49f) return _last;            // one scan per 0.5 s for all modules
            var s = new DegatsScan { Time = now };
            _map ??= new LuaMap();
            for (int side = 0; side < 2; side++)
            {
                Il2CppReferenceArray<LuaUnit> arr = null;
                try { arr = _map.GetUnits(V3.zero, 1_000_000f, side, -1); } catch { s.Errors++; }
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = arr[i];
                        if (u == null || !u.IsAlive()) continue;
                        int uid = u.UID;
                        var m = Meta(u, uid);
                        s.All.Add(new DegatsUnit { U = u, Eid = u.Entity.EntityId, Uid = uid, UnitId = m.unitId, Type = m.type, Side = side, Owner = m.owner, Pos = u.GetPosition() });
                    }
                    catch { s.Errors++; }
                }
            }
            _last = s;
            return s;
        }

        internal static void Forget()
        {
            _last = null; _map = null; _gcPtr = IntPtr.Zero; _gcHold = null;
            _meta.Clear(); _typeByUnitId.Clear(); _names.Clear();
        }

        static (int unitId, int type, int owner) Meta(LuaUnit u, int uid)
        {
            if (_meta.TryGetValue(uid, out var m)) return m;
            int unitId = 0, owner = int.MinValue, type = 0;
            try { unitId = u.SpawnData?.Unit?.UnitID ?? 0; } catch { unitId = 0; }
            try { owner = u.GetOwnerPlayerUID(); } catch { owner = int.MinValue; }
            if (unitId > 0 && !_typeByUnitId.TryGetValue(unitId, out type))
            {
                type = 0;
                try { var src = DataBaseService._instance?.RawAccess; if (src != null && src.Units.TryGetById(unitId, out UnitsRow row) && row != null) type = (int)row.Type; } catch { type = 0; }
                if (type != 0) _typeByUnitId[unitId] = type;
            }
            if (type == 0) { try { type = (int)BSH.GetUnitType(u.Entity); } catch { type = 0; } }
            m = (unitId, type, owner);
            if (unitId > 0 && type != 0 && _meta.Count < 20000) _meta[uid] = m;                // not cached while the spawn data is not readable yet
            return m;
        }

        internal static string NameOf(DegatsUnit u)
        {
            if (u == null) return "?";
            if (_names.TryGetValue(u.Uid, out var n)) return n;
            n = "?";
            try { n = u.U?.Name ?? "?"; } catch { n = "?"; }
            if (n != "?" && _names.Count < 5000) _names[u.Uid] = n;
            return n;
        }
    }

    /// Per-battle ammunition table indexed by ammo Id: armour type, cover class (Couvert), half-armour rule class, blast radius...
    sealed class DegatsMunitions
    {
        internal const byte ArmorIgnore = 0, ArmorKinetic = 1, ArmorHeat = 2;
        internal const byte CoverNone = 0, CoverBall = 1, CoverDmr = 2, CoverSniper = 3, CoverHeavy = 4, CoverGrenade = 5, CoverCannonHe = 6, CoverTankHe = 7, CoverRocket = 8, CoverCount = 9;
        internal const byte RuleExempt = 0, RuleHeat = 1, RuleKinetic = 2;

        internal byte[] Armor = Array.Empty<byte>(), Cover = Array.Empty<byte>(), Rule = Array.Empty<byte>();
        internal float[] Aoe = Array.Empty<float>(), IgnoreCover = Array.Empty<float>();
        internal readonly Dictionary<int, string> Names = new(), Families = new();
        internal readonly int[] CoverCounts = new int[CoverCount];
        internal int Rows, RuleHeatCount, RuleKineticCount, Exempt;
        internal bool FamilyFile;

        static DegatsMunitions _cur;
        static DbSource _curSrc;                                                             // held: no address reuse
        static IntPtr _curGc;
        static bool _curApplied;

        /// Main thread: built once per battle, rebuilt when the database or the applied real stats change.
        internal static DegatsMunitions ForBattle()
        {
            var db = DataBaseService._instance;
            var src = db?.RawAccess;
            if (src == null) return null;
            var gc = GameController._instance;
            IntPtr g = gc == null ? IntPtr.Zero : gc.Pointer;
            bool applied = Realism.IsApplied;
            if (_cur != null && _curSrc != null && _curSrc.Pointer == src.Pointer && _curGc == g && _curApplied == applied) return _cur;
            var t = Build(db, src);
            _cur = t; _curSrc = src; _curGc = g; _curApplied = applied;
            return t;
        }

        internal static void Forget() { _cur = null; _curSrc = null; _curGc = IntPtr.Zero; }

        static DegatsMunitions Build(DataBaseService db, DbSource src)
        {
            var t = new DegatsMunitions();
            var weaponType = new Dictionary<int, int>();
            var types = new Dictionary<int, HashSet<int>>();
            foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
            {
                if (wa == null) continue;
                int wid = wa.WeaponId;
                if (!weaponType.TryGetValue(wid, out int wt))
                {
                    wt = 0;
                    try { if (src.Weapons.TryGetById(wid, out WeaponsRow w) && w != null) wt = (int)w.Type; } catch { wt = 0; }
                    weaponType[wid] = wt;
                }
                int aid = wa.AmmunitionId;
                if (!types.TryGetValue(aid, out var set)) types[aid] = set = new HashSet<int>();
                if (wt != 0) set.Add(wt);
            }
            ReadFamilies(db, t);
            var rows = Props.Rows(src.Ammunitions.GetAll());
            int max = 0;
            foreach (var a in rows) if (a != null && a.Id > max) max = a.Id;
            int n = Math.Min(max + 1, 65536);
            t.Armor = new byte[n]; t.Cover = new byte[n]; t.Rule = new byte[n]; t.Aoe = new float[n]; t.IgnoreCover = new float[n];
            foreach (var a in rows)
            {
                if (a == null) continue;
                try
                {
                    int id = a.Id;
                    if (id <= 0 || id >= n) continue;
                    string name = a.Name ?? "";
                    int at = (int)a.ArmorTargeted, traj = (int)a.TrajectoryType;
                    byte armor = at == (int)ArmorType.Kinetic ? ArmorKinetic : at == (int)ArmorType.HEAT ? ArmorHeat : ArmorIgnore;
                    float aoe = a.HealthAOERadius;
                    bool small = ((int)a.UiOptions & (int)UiOpt.IsSmallArmsAmmo) != 0;
                    types.TryGetValue(id, out var wts);
                    t.Families.TryGetValue(id, out var fam);
                    t.Names[id] = name;
                    t.Armor[id] = armor;
                    t.Aoe[id] = aoe;
                    t.IgnoreCover[id] = a.IgnoreCover;
                    byte cover = CoverClassOf(name, fam, armor, traj, wts, small);
                    byte rule = RuleClassOf(fam, armor, traj, wts, aoe);
                    t.Cover[id] = cover; t.Rule[id] = rule;
                    t.CoverCounts[cover]++;
                    if (rule == RuleHeat) t.RuleHeatCount++; else if (rule == RuleKinetic) t.RuleKineticCount++; else t.Exempt++;
                    t.Rows++;
                }
                catch { }
            }
            return t;
        }

        /// Families written by the realism pass (UserData/RealismOverhaul_realisme/Familles_<source>.csv), Id -> family name.
        static void ReadFamilies(DataBaseService db, DegatsMunitions t)
        {
            try
            {
                string path = Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_realisme", "Familles_" + StatsExport.SafeName(db.CurrentSourceId) + ".csv");
                if (!File.Exists(path)) return;
                int iId = -1, iFam = -1;
                bool header = true;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim().TrimStart('﻿');
                    if (line.Length == 0) continue;
                    var cells = line.Split(';');
                    if (header)
                    {
                        header = false;
                        for (int i = 0; i < cells.Length; i++)
                        {
                            var c = cells[i].Trim().TrimStart('﻿');
                            if (c == "Id") iId = i; else if (c == "Family") iFam = i;
                        }
                        if (iId < 0 || iFam < 0) return;
                        continue;
                    }
                    if (cells.Length <= Math.Max(iId, iFam)) continue;
                    if (int.TryParse(cells[iId].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) t.Families[id] = cells[iFam].Trim();
                }
                t.FamilyFile = t.Families.Count > 0;
            }
            catch { t.Families.Clear(); t.FamilyFile = false; }
        }

        static readonly HashSet<string> NoCoverFamilies = new() { "SMOKE", "SEAD", "BALLISTIC", "CRUISE", "BOMB_GUIDED", "BOMB_DUMB", "A2A_RADAR", "A2A_IR", "MANPADS", "AA_LR", "AA_SR", "HELI_ATGM", "A2G_MISSILE", "ATGM_TOP", "ATGM", "ROCKET_POD", "ARTY", "MORTAR", "MLRS" };

        static bool Has(HashSet<int> wts, WType w) => wts != null && wts.Contains((int)w);

        internal static bool IsThermobaric(string upper) =>
            upper.Contains("THERMOBARIC") || upper.Contains("INCENDIARY") || upper.Contains("TBG") || upper.Contains("RSHG") || upper.Contains(" RMG")
            || upper.Contains("GM94") || upper.Contains("MROZ") || upper.Contains("FLASH") || upper.Contains("FAE") || upper.Contains(" RPO");

        /// Cover class of the infantry cover table (infantry_speed_cover.txt, corrected values).
        static byte CoverClassOf(string name, string family, byte armor, int traj, HashSet<int> wt, bool smallArms)
        {
            string n = name.ToUpperInvariant();
            if (family != null && NoCoverFamilies.Contains(family)) return CoverNone;                // realism families: indirect fire, missiles, bombs, rocket pods
            if (traj != 0 && traj != (int)Traj.DirectShot) return CoverNone;                  // artillery, mortars, MLRS, missiles, bombs
            if (IsThermobaric(n)) return CoverNone;
            if (Has(wt, WType.PlaneGun) || Has(wt, WType.RocketPod) || Has(wt, WType.BombDumb) || Has(wt, WType.BombDrag) || Has(wt, WType.BombSmart)
                || Has(wt, WType.Howitzer) || Has(wt, WType.Mortar) || Has(wt, WType.MLRS)
                || Has(wt, WType.ATGM) || Has(wt, WType.ATGMHE) || Has(wt, WType.CruiseMissile) || Has(wt, WType.BallisticMissile) || Has(wt, WType.AntiRadarMissile)
                || Has(wt, WType.MANPAD) || Has(wt, WType.SAM) || Has(wt, WType.A2AMissileRadar) || Has(wt, WType.A2AMissileIR) || Has(wt, WType.AGM))
                return CoverNone;
            bool heat = armor == ArmorHeat, kin = armor == ArmorKinetic;
            bool heavyCal = n.Contains("12.7") || n.Contains("14.5") || n.Contains("12,7") || n.Contains("14,5");
            if (Has(wt, WType.MainGun)) return heat ? CoverTankHe : CoverNone;
            if (Has(wt, WType.AutoCannon) || Has(wt, WType.AntiAirGun) || Has(wt, WType.GunPod)) return heat ? CoverCannonHe : heavyCal ? CoverHeavy : CoverNone;
            if (heat && (Has(wt, WType.RPGReloadable) || Has(wt, WType.RPGDisposable))) return CoverRocket;
            if (heat && (Has(wt, WType.GrenadeLauncher) || Has(wt, WType.AutoGrenadeLauncher))) return CoverGrenade;
            if (kin || smallArms)
            {
                if (heavyCal || Has(wt, WType.AntiMaterialRifle) || Has(wt, WType.HeavyMachineGun)) return CoverHeavy;
                if (Has(wt, WType.SniperRifle)) return CoverSniper;
                if (Has(wt, WType.MarksmanRifle)) return CoverDmr;
                if (smallArms || Has(wt, WType.Rifle) || Has(wt, WType.AssaultRifle) || Has(wt, WType.BattleRifle) || Has(wt, WType.SMG)
                    || Has(wt, WType.LightMachineGun) || Has(wt, WType.MediumMachineGun) || Has(wt, WType.MiniGun) || Has(wt, WType.Autorifle)
                    || Has(wt, WType.Shotgun) || Has(wt, WType.GrenadeLauncher))
                    return CoverBall;
            }
            return CoverNone;
        }

        /// Half-armour rule class (calibre_design.md §4): 1 HEAT-flagged, 2 kinetic, 0 exempt (indirect fire, bombs, big blasts).
        static byte RuleClassOf(string family, byte armor, int traj, HashSet<int> wt, float aoe)
        {
            if (armor != ArmorHeat && armor != ArmorKinetic) return RuleExempt;
            if (family == "ARTY" || family == "MORTAR" || family == "MLRS" || family == "BALLISTIC" || family == "CRUISE" || family == "BOMB_GUIDED" || family == "BOMB_DUMB") return RuleExempt;
            if (traj == (int)Traj.Artillery || traj == (int)Traj.Mortar || traj == (int)Traj.MLRS || traj == (int)Traj.CruiseMissile || traj == (int)Traj.BallisticMissile
                || traj == (int)Traj.LowDragBomb || traj == (int)Traj.HighDragBomb || traj == (int)Traj.LaserGuidedFreeFaling) return RuleExempt;
            if (Has(wt, WType.Howitzer) || Has(wt, WType.Mortar) || Has(wt, WType.MLRS) || Has(wt, WType.CruiseMissile) || Has(wt, WType.BallisticMissile)
                || Has(wt, WType.BombDumb) || Has(wt, WType.BombDrag) || Has(wt, WType.BombSmart)) return RuleExempt;
            bool planeGun = family == "PLANE_GUN" || Has(wt, WType.PlaneGun) || Has(wt, WType.GunPod);
            if (aoe >= 20f && !planeGun) return RuleExempt;
            return armor == ArmorHeat ? RuleHeat : RuleKinetic;
        }

        internal string NameOf(int id) => Names.TryGetValue(id, out var n) && n.Length > 0 ? n : "munition " + id;
    }

    // ==================================================================================================== measurement

    static class CalibreMesure
    {
        const string GuardVersion = "1.1";
        const int MaxErrors = 50;
        const int QueueSize = 2048;                                  // power of two
        const int DetailBudget = 12, DetailInfantryBudget = 3;       // detailed hit lines per 30 s
        internal const float FitTolerance = 0.03f, PassShare = 0.95f;
        internal const int MinGateHits = 20;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] FaceNames = { "avant", "flanc", "arrière", "toit" };

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        internal static MelonPreferences_Entry<bool> RuleEnabled;
        internal static MelonPreferences_Entry<int> RuleStage;
        internal static MelonPreferences_Entry<float> RuleHeat, RuleKin;
        internal static MelonPreferences_Entry<string> RuleProof, RuleVersion;

        static HarmonyLib.Harmony _harmony;
        static bool _patchTried, _damagePatched, _stressPatched, _refused, _everOnline, _sessionArmed, _netLogged, _depthWarned, _engineBroken, _engineArmorBroken;
        static bool _endLatch;                                       // battle closed at the end screen: no new measured battle while that screen shows
        static GameController _endLatchGc;                           // held (no address reuse): a new GameController also releases the latch
        static DegatsMunitions _lastTable;
        static float _nextTick, _nextDetailReset, _nextSummary, _battleStartReal;
        static int _detailLeft, _detailInfLeft;
        static long _detailSuppressed;

        // ---------------------------------------------------------------- hook side (immutable snapshots, plain counters)
        static volatile bool _armed;
        static volatile int _mainThread;

        sealed class Targets
        {
            internal readonly int[] Ids; internal readonly byte[] Kind;                      // Kind: 2 infantry, 4 vehicle
            internal Targets(int[] ids, byte[] kind) { Ids = ids; Kind = kind; }
        }
        static volatile Targets _targets = new Targets(Array.Empty<int>(), Array.Empty<byte>());
        static volatile byte[] _armorTable = Array.Empty<byte>();

        struct Ev { public long Seq, Hit; public int Eid, Ammo, Side; public float Pen, Base, Res; public byte Kind; public bool Top; }
        static readonly Ev[] _ev = new Ev[QueueSize];
        static long _evHead, _evRead, _evLost;
        [ThreadStatic] static int _sample;

        static long _inHitAll, _inHitUnits, _inHitOffMain, _outSampled, _outVehicle, _outZero, _outZeroKin, _outZeroHeat, _outLow, _outOffMain, _errors;
        static long _stressIn, _stressOut, _stressZero, _stressPos, _stressZeroRatio, _stressPosRatio, _stressZeroRatioN, _stressPosRatioN, _stressOffMain;

        /// Calls of CalculateHitDamage inside an impact since the battle started (any target). Read by the DemiBlindage watchdog.
        internal static long ImpactCalls => Interlocked.Read(ref _inHitAll);
        /// The measurement hooks are installed and armed in this battle.
        internal static bool Armed => _armed && _damagePatched;
        internal static bool SessionArmed => _sessionArmed;

        // ---------------------------------------------------------------- settings of this battle
        internal static float K = 2f, MinCoef;
        internal static bool KValid, SettingsRead;
        static float _penEff, _heEff, _topAngle;

        // ---------------------------------------------------------------- gates (main thread)
        internal sealed class Gates
        {
            public long KinBelow, KinBelowZero, KinBelowFloor, KinBelowOther, KinBand, KinBandFit, KinBandFitFloor, KinFull, KinFullFit, KinNoArmor, KinAmbiguous, KinAmbiguousFit, KinEdge;
            public long HeatN, HeatFit, HeatHalf, HeatHalfFit, HeatNoArmor, HeatAmbiguous, HeatAmbiguousFit;
            public readonly long[] HeatAoeN = new long[4], HeatAoeFit = new long[4];         // blast radius 0, (0,5), [5,20), 20+
            public long InfN, InfFit, IgnoreHits, UnknownUnit, UnknownAmmo, NoCandidate, VehicleHits, InfantryHits, IdentifiedByEngine;
            public long EngKinN, EngKinMatch, EngHeatN, EngHeatMatch, EngOtherFormula, EngArmorN, EngArmorAgree, EngInfN, EngInfMatch;
            public long TopVehicle, TopInfantry;
            public readonly Dictionary<int, long> TopByAmmo = new();
            public readonly Dictionary<string, long> TopByFamily = new();
        }
        static Gates _g = new Gates();

        internal enum GateState { Insufficient, Pass, Floor, Fail }

        /// Counts the stage switch needs (cumulated across battles in a hidden pref).
        internal sealed class GateTotals
        {
            public long KinBelow, KinBelowZero, KinBelowFloor, KinBand, KinBandFit, KinBandFitFloor, HeatN, HeatFit;
            public float K, Coef;

            internal void Add(GateTotals o)
            {
                KinBelow += o.KinBelow; KinBelowZero += o.KinBelowZero; KinBelowFloor += o.KinBelowFloor;
                KinBand += o.KinBand; KinBandFit += o.KinBandFit; KinBandFitFloor += o.KinBandFitFloor;
                HeatN += o.HeatN; HeatFit += o.HeatFit;
            }

            internal string Serialize() => string.Format(Inv, "k={0:R};c={1:R};kb={2};kbz={3};kbf={4};kd={5};kdf={6};kdff={7};hn={8};hf={9}",
                K, Coef, KinBelow, KinBelowZero, KinBelowFloor, KinBand, KinBandFit, KinBandFitFloor, HeatN, HeatFit);

            internal static GateTotals Parse(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                try
                {
                    var t = new GateTotals();
                    foreach (var part in s.Split(';'))
                    {
                        var kv = part.Split('=');
                        if (kv.Length != 2) return null;
                        string v = kv[1].Trim();
                        switch (kv[0].Trim())
                        {
                            case "k": t.K = float.Parse(v, Inv); break;
                            case "c": t.Coef = float.Parse(v, Inv); break;
                            case "kb": t.KinBelow = long.Parse(v, Inv); break;
                            case "kbz": t.KinBelowZero = long.Parse(v, Inv); break;
                            case "kbf": t.KinBelowFloor = long.Parse(v, Inv); break;
                            case "kd": t.KinBand = long.Parse(v, Inv); break;
                            case "kdf": t.KinBandFit = long.Parse(v, Inv); break;
                            case "kdff": t.KinBandFitFloor = long.Parse(v, Inv); break;
                            case "hn": t.HeatN = long.Parse(v, Inv); break;
                            case "hf": t.HeatFit = long.Parse(v, Inv); break;
                            default: return null;
                        }
                    }
                    return t;
                }
                catch { return null; }
            }
        }

        internal static GateState EvalG1(GateTotals t)
        {
            long n = t.KinBelow + t.KinBand;
            if (n < MinGateHits) return GateState.Insufficient;
            bool bandOk = t.KinBand == 0 || t.KinBandFit >= PassShare * t.KinBand;
            if (t.KinBelowZero == t.KinBelow && bandOk) return GateState.Pass;
            bool floorOk = t.Coef > 0f && t.KinBelow >= 5 && t.KinBelowFloor >= PassShare * t.KinBelow
                           && (t.KinBand == 0 || t.KinBandFitFloor >= PassShare * t.KinBand);
            return floorOk ? GateState.Floor : GateState.Fail;
        }

        internal static GateState EvalG2(GateTotals t)
        {
            if (t.HeatN < MinGateHits) return GateState.Insufficient;
            return t.HeatFit >= PassShare * t.HeatN ? GateState.Pass : GateState.Fail;
        }

        static void Log(string s) => Mod.Log.Msg("[CALIBRE-MESURE] " + s);

        // ---------------------------------------------------------------- lifecycle
        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_CalibreMesure");
            _enabled = c.CreateEntry("MesureCalibre", true, description: Build.Desc("Mesure (ne change rien au jeu) des dégâts selon la pénétration et le blindage, pour vérifier les dégâts par calibre"));
            RuleEnabled = c.CreateEntry("RegleDemiBlindage", true, description: Build.Desc("Règle du demi-blindage (automatique après une mesure réussie) : un tir qui perce moins de la moitié du blindage d'un véhicule ne fait aucun dégât"));
            RuleStage = c.CreateEntry("EtapeDemiBlindage", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            RuleHeat = c.CreateEntry("SeuilChargeCreuse", 0f, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            RuleKin = c.CreateEntry("SeuilCinetique", 0f, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            RuleProof = c.CreateEntry("PreuvesDemiBlindage", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            RuleVersion = c.CreateEntry("VersionPreuves", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            if (RuleVersion.Value != GuardVersion)
            {
                // a new mod version may change the data: the rule is measured again before it acts
                RuleVersion.Value = GuardVersion;
                RuleStage.Value = 0; RuleHeat.Value = 0f; RuleKin.Value = 0f; RuleProof.Value = "";
            }
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            _endLatch = false; _endLatchGc = null;
            EndBattle("nouvelle mission", true);
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu", false);

        /// Called by Campaign.PollBattleEnd when the battle-end screen appears. The latch keeps Frame from starting a new measured
        /// battle (and consuming the rule notice, raising SessionsInterrompues) while that screen still shows.
        internal static void OnBattleEnd()
        {
            EndBattle("fin de bataille", true);
            _endLatch = true;
            try { _endLatchGc = GameController._instance; } catch { _endLatchGc = null; }
        }

        static void EndBattle(string why, bool normal)
        {
            _armed = false;
            _targets = new Targets(Array.Empty<int>(), Array.Empty<byte>());
            if (!_sessionArmed) { DemiBlindage.Disarm(); return; }
            _sessionArmed = false;
            if (_unclean.Value != 0) _unclean.Value = 0;
            try { Drain(); } catch { }
            try { Summary(true); } catch (Exception e) { Log("bilan illisible : " + e.Message); }
            GateTotals battle = null;
            try { battle = GateSummary(why); } catch (Exception e) { Log("portes illisibles : " + e.Message); }
            try { DemiBlindage.BattleEnd(battle, normal, why, Interlocked.Read(ref _inHitUnits), Interlocked.Read(ref _errors)); }
            catch (Exception e) { Mod.Log.Warning("[DEMI-BLINDAGE] décision de fin de bataille impossible : " + e.GetBaseException().Message); DemiBlindage.Disarm(); }
            try { MelonPreferences.Save(); } catch { }
            Log($"fin de mesure ({why})");
        }

        static int _wait;

        /// Every frame in campaign: drains the hook queue; the rest every 0.5 s.
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _refused) { if (_armed) _armed = false; DemiBlindage.Disarm(); return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_sessionArmed) Drain();
            if (now < _nextTick) return;
            // one heavy module job per frame (Planif.cs): same 0.5 s period, just not in the same frame as the other modules
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _nextTick = now + 0.5f;
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            if (gc?._GameSession_k__BackingField?.CurrentPlayer == null) { _endLatch = false; _endLatchGc = null; if (_sessionArmed) EndBattle("plus de partie", true); return; }
            if (!Solo())
            {
                if (_armed) { _armed = false; Log("partie en ligne : mesure coupée"); }
                DemiBlindage.Disarm();
                return;
            }
            if (_endLatch)
            {
                bool shown;
                try { shown = Il2CppBrokenArrow.Client.Ecs.UI.Menu.BattleEnd.BattleEndScreen.Active; }   // static Boolean get_Active()
                catch { shown = true; }                                                                    // unreadable: stay closed (no measurement, no rule, normal game damage)
                if (shown && _endLatchGc != null && gc != null && _endLatchGc.Pointer != gc.Pointer) shown = false;   // new battle controller: a stale flag must not block it
                if (shown) { DemiBlindage.Disarm(); return; }
                _endLatch = false; _endLatchGc = null;
            }
            if (!_sessionArmed && !StartBattle(now)) return;

            var table = DegatsMunitions.ForBattle();
            if (table != null) { _armorTable = table.Armor; _lastTable = table; }
            var scan = DegatsScan.Get(now);
            PublishTargets(scan);
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            if (_refused) return;
            _armed = (_damagePatched || _stressPatched) && Interlocked.Read(ref _errors) < MaxErrors;
            if (!_depthWarned && now - _battleStartReal > 15f && !LeurresMesure.HitDepthReady)
            {
                _depthWarned = true;
                Log("profondeur d'impact indisponible (mesure des leurres coupée ou non installée) : les impacts ne sont pas vus, seules les portes G3 et G4 avancent ; la règle du demi-blindage reste coupée");
            }
            DemiBlindage.Tick(now, scan, table);
            if (now >= _nextDetailReset) { _nextDetailReset = now + 30f; _detailLeft = DetailBudget; _detailInfLeft = DetailInfantryBudget; }
            if (now >= _nextSummary) { _nextSummary = now + 60f; Summary(false); }
        }

        static bool StartBattle(float now)
        {
            if (_unclean.Value >= 2)
            {
                _refused = true;
                _armed = false;
                Mod.Log.Warning("[CALIBRE-MESURE] mesure désactivée : les deux dernières parties mesurées ne se sont pas terminées normalement");
                DemiBlindage.RevertAfterUncleanSession(true);
                return false;
            }
            if (_unclean.Value >= 1) DemiBlindage.RevertAfterUncleanSession(false);
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;
            MelonPreferences.Save();
            ClearBattle();
            _battleStartReal = now;
            _nextSummary = now + 60f;
            _nextDetailReset = now + 30f;
            _detailLeft = DetailBudget; _detailInfLeft = DetailInfantryBudget;
            DegatsAmmoCache.NewBattle();
            LogSettings();
            Log("mesure prête (aucun changement de jeu) : impacts sur véhicules et infanterie des deux camps, appels de l'IA échantillonnés 1 sur 16");
            DemiBlindage.BattleStart(now);
            return true;
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

        static readonly Dictionary<int, DegatsUnit> _known = new();

        /// Vehicles and infantry of both sides, sorted by EntityId, published as one immutable object.
        static void PublishTargets(DegatsScan scan)
        {
            var ids = new List<int>(scan.All.Count);
            var kinds = new List<byte>(scan.All.Count);
            foreach (var u in scan.All)
            {
                byte k = u.Vehicle ? (byte)4 : u.Infantry ? (byte)2 : (byte)0;
                if (k == 0) continue;
                ids.Add(u.Eid); kinds.Add(k);
                if (_known.Count < 20000 || _known.ContainsKey(u.Eid)) _known[u.Eid] = u;
            }
            var ia = ids.ToArray();
            var ka = kinds.ToArray();
            Array.Sort(ia, ka);
            _targets = new Targets(ia, ka);
        }

        static void TryPatch()
        {
            _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.CalibreMesure");
            _damagePatched = Patch("dégâts", AccessTools.Method(typeof(BSH), "CalculateHitDamage"), nameof(DamagePostfix), Priority.First);
            _stressPatched = Patch("stress", AccessTools.Method(typeof(BSH), "CalculateStressDamage"), nameof(StressPostfix), Priority.Normal);
            if (!_damagePatched && !_stressPatched) { _refused = true; Log("aucun point de mesure installable dans cette version du jeu"); return; }
            Log($"{(_damagePatched ? 1 : 0) + (_stressPatched ? 1 : 0)}/2 point(s) de mesure installé(s) (aucun changement de comportement)");
        }

        static bool Patch(string label, MethodInfo target, string postfix, int priority)
        {
            try
            {
                if (target == null) { Log($"{label} : méthode introuvable"); return false; }
                var hm = new HarmonyMethod(typeof(CalibreMesure).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)) { priority = priority };
                _harmony.Patch(target, postfix: hm);
                return true;
            }
            catch (Exception e) { Log($"{label} : non installé ({e.GetBaseException().Message})"); return false; }
        }

        // ---------------------------------------------------------------- hooks (any thread, no Unity call, no allocation, no logging)
        static void Push(long hit, int eid, int ammo, float pen, float baseDamage, float res, int side, byte kind, bool top)
        {
            long n = Interlocked.Increment(ref _evHead);
            int slot = (int)((n - 1) & (QueueSize - 1));
            _ev[slot].Seq = 0;                                                               // slot being written
            _ev[slot].Hit = hit; _ev[slot].Eid = eid; _ev[slot].Ammo = ammo; _ev[slot].Side = side;
            _ev[slot].Pen = pen; _ev[slot].Base = baseDamage; _ev[slot].Res = res; _ev[slot].Kind = kind; _ev[slot].Top = top;
            _ev[slot].Seq = n;                                                               // written last: the slot is complete
        }

        /// static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo, Single penetration,
        /// Boolean forceTopArmorAttack, ArmorSides armorSide). Priority.First: sees the engine's value before any mod change.
        static void DamagePostfix(EcsEntity target, float baseDamage, Ammo ammoInfo, float penetration, bool forceTopArmorAttack, ArmorSides armorSide, float __result)
        {
            if (Campaign.MissionInerte || !_armed) return;
            try
            {
                if (LeurresMesure.HitDepth > 0)
                {
                    Interlocked.Increment(ref _inHitAll);
                    var t = _targets;
                    int eid = target.EntityId;
                    int i = Array.BinarySearch(t.Ids, eid);
                    if (i < 0) return;
                    Interlocked.Increment(ref _inHitUnits);
                    if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _inHitOffMain);
                    int ammo = ammoInfo == null ? 0 : DegatsAmmoCache.Id(ammoInfo);
                    Push(LeurresMesure.HitSerial, eid, ammo, penetration, baseDamage, __result, (int)armorSide, t.Kind[i], forceTopArmorAttack);
                    return;
                }
                if ((++_sample & 15) != 0) return;                                           // outside impacts: 1 call in 16
                Interlocked.Increment(ref _outSampled);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _outOffMain);
                var v = _targets;
                int j = Array.BinarySearch(v.Ids, target.EntityId);
                if (j < 0 || v.Kind[j] != 4) return;
                Interlocked.Increment(ref _outVehicle);
                if (__result == 0f)
                {
                    Interlocked.Increment(ref _outZero);
                    var tbl = _armorTable;
                    int id = ammoInfo == null ? 0 : DegatsAmmoCache.Id(ammoInfo);
                    if ((uint)id < (uint)tbl.Length)
                    {
                        if (tbl[id] == DegatsMunitions.ArmorKinetic) Interlocked.Increment(ref _outZeroKin);
                        else if (tbl[id] == DegatsMunitions.ArmorHeat) Interlocked.Increment(ref _outZeroHeat);
                    }
                }
                else if (__result < 0.2f * baseDamage) Interlocked.Increment(ref _outLow);
            }
            catch { if (Interlocked.Increment(ref _errors) >= MaxErrors) _armed = false; }
        }

        /// static Single CalculateStressDamage(Single maxStress, Single healthDamage, Single stressDamage, Single targetMaxHeal)
        static void StressPostfix(float healthDamage, float stressDamage, float __result)
        {
            if (Campaign.MissionInerte || !_armed) return;
            try
            {
                if (LeurresMesure.HitDepth <= 0) { Interlocked.Increment(ref _stressOut); return; }
                Interlocked.Increment(ref _stressIn);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _stressOffMain);
                bool zero = !(healthDamage > 0f);
                if (zero) Interlocked.Increment(ref _stressZero); else Interlocked.Increment(ref _stressPos);
                if (stressDamage > 0f)
                {
                    float r = __result / stressDamage;
                    long milli = r > 1000f ? 1_000_000L : r > 0f ? (long)(r * 1000f) : 0L;
                    if (zero) { Interlocked.Add(ref _stressZeroRatio, milli); Interlocked.Increment(ref _stressZeroRatioN); }
                    else { Interlocked.Add(ref _stressPosRatio, milli); Interlocked.Increment(ref _stressPosRatioN); }
                }
            }
            catch { if (Interlocked.Increment(ref _errors) >= MaxErrors) _armed = false; }
        }

        // ---------------------------------------------------------------- resolution (main thread)
        static void Drain()
        {
            long head = Interlocked.Read(ref _evHead);
            if (head == _evRead) return;
            if (head - _evRead > QueueSize) { _evLost += head - _evRead - QueueSize; _evRead = head - QueueSize; }
            var table = _lastTable;
            int budget = 400;                                                                // resolution work per frame
            while (_evRead < head && budget-- > 0)
            {
                long n = _evRead + 1;
                int slot = (int)((n - 1) & (QueueSize - 1));
                long s1 = _ev[slot].Seq;
                if (s1 < n)
                {
                    if (head - n < 64) break;                                                // still being written by a hook: next frame
                    _evLost++; _evRead = n; continue;
                }
                var e = _ev[slot];
                if (s1 > n || _ev[slot].Seq != n) { _evLost++; _evRead = n; continue; }     // overwritten meanwhile
                _evRead = n;
                try { Resolve(e, table); } catch (Exception ex) { _g.UnknownUnit++; if (_detailLeft > 0) { _detailLeft--; Log("impact illisible : " + ex.GetBaseException().Message); } }
            }
        }

        sealed class ArmorCands
        {
            public readonly List<(string label, int[] v)> List = new();                      // v: ArmorValue, Kin F/S/R/T, Heat F/S/R/T
            public UnitsRow EngineRow;
        }
        static readonly Dictionary<int, ArmorCands> _cands = new();

        static ArmorCands Candidates(int unitId)
        {
            if (_cands.TryGetValue(unitId, out var c)) return c;
            c = new ArmorCands();
            if (unitId > 0)
            {
                var db = DataBaseService._instance;
                UnitsRow table = null, copy = null;
                try { var src = db?.RawAccess; if (src != null) src.Units.TryGetById(unitId, out table); } catch { table = null; }
                try { var d = db?.UnitsLoader?._loadedUnits; if (d != null) d.TryGetValue(unitId, out copy); } catch { copy = null; }
                AddUnit(c, "copie", copy);
                AddUnit(c, "table", table);
                c.EngineRow = copy ?? table;
            }
            if (_cands.Count < 4000) _cands[unitId] = c;
            return c;
        }

        static void AddUnit(ArmorCands c, string label, UnitsRow u)
        {
            if (u == null) return;
            try { AddArmor(c, label + " actuel", u.CurrentArmor); } catch { }
            try { AddArmor(c, label, u.Armor); } catch { }
            try
            {
                var l = u.Armors;
                for (int i = 0; i < (l?.Count ?? 0) && i < 8; i++) AddArmor(c, label + " liste", l[i]);
            }
            catch { }
        }

        static void AddArmor(ArmorCands c, string label, ArmorsRow a)
        {
            if (a == null) return;
            int[] v;
            try { v = new[] { a.ArmorValue, a.KinArmorFront, a.KinArmorSides, a.KinArmorRear, a.KinArmorTop, a.HeatArmorFront, a.HeatArmorSides, a.HeatArmorRear, a.HeatArmorTop }; }
            catch { return; }
            foreach (var e in c.List) if (e.v.SequenceEqual(v)) return;
            int id = 0;
            try { id = a.Id; } catch { }
            c.List.Add(($"{label} n°{id}", v));
        }

        static bool HasCurrentArmor(UnitsRow u)
        {
            try { return u.CurrentArmor != null; } catch { return false; }
        }

        static int FaceIndex(int side)
        {
            if (side == (int)ArmorSides.Front) return 0;
            if (side == (int)ArmorSides.Sides) return 1;
            if (side == (int)ArmorSides.Back) return 2;
            if (side == (int)ArmorSides.Top) return 3;
            return -1;
        }

        static readonly ArmorSides[] FaceEnums = { ArmorSides.Front, ArmorSides.Sides, ArmorSides.Back, ArmorSides.Top };

        /// Documented kinetic rule (pre-release guide): 0 below P/A 0.5, linear 0.1 -> 1 between 0.5 and 1, full damage above.
        internal static float KineticRule(float baseDamage, float pen, float armor)
        {
            if (!(armor > 0f)) return baseDamage;
            float r = pen / armor;
            if (r >= 1f) return baseDamage;
            if (r >= 0.5f) return baseDamage * (0.1f + 1.8f * (r - 0.5f));
            return 0f;
        }

        /// Documented explosive / shaped-charge curve: D x P^k / (P^k + A^k).
        internal static float Curve(float baseDamage, float pen, float armor, float k)
        {
            if (!(armor > 0f)) return baseDamage;
            if (!(pen > 0f)) return 0f;
            double pk = Math.Pow(pen, k), ak = Math.Pow(armor, k);
            return (float)(baseDamage * pk / (pk + ak));
        }

        static float RelErr(float res, float pred, float baseDamage)
        {
            float scale = Math.Max(Math.Abs(baseDamage), 1f);
            if (Math.Abs(pred) <= 1e-5f * scale) return Math.Abs(res) <= 1e-5f * scale ? 0f : 1f;
            return Math.Abs(res - pred) / Math.Abs(pred);
        }

        static bool IsZero(float res, float baseDamage) => Math.Abs(res) <= 1e-5f * Math.Max(Math.Abs(baseDamage), 1f);
        static bool Same(float a, float b, float baseDamage) => Math.Abs(a - b) <= 1e-3f * Math.Max(Math.Abs(baseDamage), 1f);

        static void Resolve(Ev e, DegatsMunitions table)
        {
            if (!_known.TryGetValue(e.Eid, out var u)) { _g.UnknownUnit++; return; }
            bool vehicle = e.Kind == 4;
            if (vehicle) _g.VehicleHits++; else _g.InfantryHits++;
            if (table == null || (uint)e.Ammo >= (uint)table.Armor.Length) { _g.UnknownAmmo++; return; }
            byte armorType = table.Armor[e.Ammo];
            if (e.Top)
            {
                if (vehicle) _g.TopVehicle++; else _g.TopInfantry++;
                if (_g.TopByAmmo.Count < 200 || _g.TopByAmmo.ContainsKey(e.Ammo)) _g.TopByAmmo[e.Ammo] = _g.TopByAmmo.GetValueOrDefault(e.Ammo) + 1;
                string fam = table.Families.TryGetValue(e.Ammo, out var f) ? f : "famille inconnue";
                _g.TopByFamily[fam] = _g.TopByFamily.GetValueOrDefault(fam) + 1;
            }
            if (armorType == DegatsMunitions.ArmorIgnore) { _g.IgnoreHits++; return; }
            var cands = Candidates(u.UnitId);
            if (cands.List.Count == 0) { _g.NoCandidate++; return; }
            int face = e.Top ? 3 : FaceIndex(e.Side);
            bool kin = armorType == DegatsMunitions.ArmorKinetic;

            if (!vehicle)
            {
                // infantry: documented curve against ArmorValue (information only, no gate)
                float a0 = cands.List[0].v[0];
                float pred = Curve(e.Base, e.Pen, a0, K);
                _g.InfN++;
                if (RelErr(e.Res, pred, e.Base) <= FitTolerance) _g.InfFit++;
                string eng = EngineCheckInfantry(e, a0);
                if (_detailInfLeft > 0 && _detailLeft > 0)
                {
                    _detailInfLeft--; _detailLeft--;
                    Log($"impact sur infanterie {DegatsScan.NameOf(u)} (uid {u.Uid}, camp {u.Side}, joueur {u.Owner}) : {table.NameOf(e.Ammo)} ({e.Ammo}, {(kin ? "cinétique" : "charge creuse/explosif")}), pénétration {F(e.Pen)}, dégâts de base {F(e.Base)}, résultat {F(e.Res)} (x{F3(Ratio(e.Res, e.Base))})" +
                        $" ; courbe sur ArmorValue {a0} ({cands.List[0].label}) {F(pred)} (écart {Pct(RelErr(e.Res, pred, e.Base))}){eng}{(e.Top ? " ; toit forcé" : "")}");
                }
                else _detailSuppressed++;
                return;
            }

            if (face < 0) { _g.NoCandidate++; return; }
            int col = (kin ? 1 : 5) + face;
            var distinct = new List<int>();
            foreach (var c in cands.List) if (!distinct.Contains(c.v[col])) distinct.Add(c.v[col]);
            float A = cands.List[0].v[col];
            bool ambiguous = distinct.Count > 1, identified = false;
            if (ambiguous && !_engineBroken)
            {
                // several armour options: the game's own formula names the armour when exactly one candidate gives the exact result
                // (independent of the documented rule tested below, so the gates stay unbiased)
                try
                {
                    int matches = 0, found = 0;
                    foreach (int a in distinct)
                    {
                        float ev = kin ? BSH.DamageFormulaKinetic(e.Base, e.Pen, a) : BSH.DamageFormulaHEAT(e.Base, e.Pen, a);
                        if (Same(ev, e.Res, e.Base)) { matches++; found = a; }
                    }
                    if (matches == 1) { A = found; ambiguous = false; identified = true; _g.IdentifiedByEngine++; }
                }
                catch (Exception ex)
                {
                    _engineBroken = true;
                    Log("formules du jeu non appelables (" + ex.GetBaseException().Message + ") : contrôle croisé arrêté pour la session");
                }
            }
            float predMain = kin ? KineticRule(e.Base, e.Pen, A) : Curve(e.Base, e.Pen, A, K);
            float errMain = RelErr(e.Res, predMain, e.Base);
            float bestErr = errMain; int bestA = (int)A;
            foreach (int a in distinct)
            {
                float p = kin ? KineticRule(e.Base, e.Pen, a) : Curve(e.Base, e.Pen, a, K);
                float er = RelErr(e.Res, p, e.Base);
                if (er < bestErr) { bestErr = er; bestA = a; }
            }
            float r = A > 0f ? e.Pen / A : float.PositiveInfinity;

            if (kin)
            {
                if (ambiguous) { _g.KinAmbiguous++; if (bestErr <= FitTolerance) _g.KinAmbiguousFit++; }
                else if (!(A > 0f)) _g.KinNoArmor++;
                else if (Math.Abs(r - 0.5f) < 0.002f || Math.Abs(r - 1f) < 0.002f) _g.KinEdge++;           // float rounding at the rule's limits
                else if (r < 0.5f)
                {
                    _g.KinBelow++;
                    if (IsZero(e.Res, e.Base)) _g.KinBelowZero++;
                    else if (MinCoef > 0f && Math.Abs(e.Res - MinCoef * e.Base) <= FitTolerance * MinCoef * Math.Abs(e.Base)) _g.KinBelowFloor++;
                    else _g.KinBelowOther++;
                }
                else if (r < 1f)
                {
                    _g.KinBand++;
                    if (errMain <= FitTolerance) _g.KinBandFit++;
                    if (RelErr(e.Res, Math.Max(predMain, MinCoef * e.Base), e.Base) <= FitTolerance) _g.KinBandFitFloor++;
                }
                else
                {
                    _g.KinFull++;
                    if (RelErr(e.Res, e.Base, e.Base) <= FitTolerance) _g.KinFullFit++;
                }
            }
            else
            {
                if (ambiguous) { _g.HeatAmbiguous++; if (bestErr <= FitTolerance) _g.HeatAmbiguousFit++; }
                else if (!(A > 0f)) _g.HeatNoArmor++;
                else
                {
                    _g.HeatN++;
                    bool fit = errMain <= FitTolerance;
                    if (fit) _g.HeatFit++;
                    if (e.Pen < 0.5f * A) { _g.HeatHalf++; if (fit) _g.HeatHalfFit++; }
                    float aoe = table.Aoe[e.Ammo];
                    int b = aoe <= 0f ? 0 : aoe < 5f ? 1 : aoe < 20f ? 2 : 3;
                    _g.HeatAoeN[b]++;
                    if (fit) _g.HeatAoeFit[b]++;
                }
            }

            string engine = EngineCheckVehicle(e, cands, kin, face, A, col);
            if (_detailLeft > 0)
            {
                _detailLeft--;
                var detail = new StringBuilder();
                detail.Append($"impact sur véhicule {DegatsScan.NameOf(u)} (uid {u.Uid}, camp {u.Side}, joueur {u.Owner}) : {table.NameOf(e.Ammo)} ({e.Ammo}, {(kin ? "cinétique" : "charge creuse/explosif")}, souffle {F(table.Aoe[e.Ammo])} m)");
                detail.Append($", face {FaceNames[face]}{(e.Top ? " (toit forcé)" : "")}, pénétration {F(e.Pen)}, dégâts de base {F(e.Base)}, résultat {F(e.Res)} (x{F3(Ratio(e.Res, e.Base))})");
                detail.Append($" ; blindage {(kin ? "cinétique" : "charge creuse")} {cands.List[0].label} = {A} (P/A {(float.IsInfinity(r) ? "-" : F(r))}) -> {(kin ? "règle cinétique" : "courbe")} {F(predMain)} (écart {Pct(errMain)})");
                if (ambiguous) detail.Append($" ; autres valeurs candidates {string.Join("/", distinct.Where(x => x != (int)A))}, meilleure {bestA} (écart {Pct(bestErr)}), tir hors portes");
                else if (identified) detail.Append($" ; blindage {A} identifié par la formule du jeu parmi {string.Join("/", distinct)}");
                detail.Append(engine);
                Log(detail.ToString());
            }
            else _detailSuppressed++;
        }

        /// Engine's own formulas on the same values (information): which formula and which armour the game really uses.
        static string EngineCheckVehicle(Ev e, ArmorCands cands, bool kin, int face, float A, int col)
        {
            if (_engineBroken) return "";
            try
            {
                float other = cands.List[0].v[(kin ? 5 : 1) + face];
                float ek = BSH.DamageFormulaKinetic(e.Base, e.Pen, kin ? A : other);
                float eh = BSH.DamageFormulaHEAT(e.Base, e.Pen, kin ? other : A);
                bool match;
                if (kin) { _g.EngKinN++; match = Same(ek, e.Res, e.Base); if (match) _g.EngKinMatch++; else if (Same(eh, e.Res, e.Base)) _g.EngOtherFormula++; }
                else { _g.EngHeatN++; match = Same(eh, e.Res, e.Base); if (match) _g.EngHeatMatch++; else if (Same(ek, e.Res, e.Base)) _g.EngOtherFormula++; }
                string armorTxt = "";
                if (cands.EngineRow != null && !_engineArmorBroken && HasCurrentArmor(cands.EngineRow))
                {
                    try
                    {
                        int ea = BSH.GetArmorBySideAndType(cands.EngineRow, kin ? ArmorType.Kinetic : ArmorType.HEAT, FaceEnums[face]);
                        _g.EngArmorN++;
                        if (ea == (int)A) _g.EngArmorAgree++;
                        armorTxt = $", blindage lu par le jeu sur la copie {ea}";
                    }
                    catch (Exception ex)
                    {
                        _engineArmorBroken = true;
                        Log("lecture du blindage par le jeu impossible (" + ex.GetBaseException().Message + ") : ce contrôle est arrêté pour la session");
                    }
                }
                return $" ; formules du jeu : cinétique {F(ek)}, charge creuse {F(eh)} -> {(match ? "identique" : "différent")}{armorTxt}";
            }
            catch (Exception ex)
            {
                _engineBroken = true;
                Log("formules du jeu non appelables (" + ex.GetBaseException().Message + ") : contrôle croisé arrêté pour la session");
                return "";
            }
        }

        static string EngineCheckInfantry(Ev e, float armorValue)
        {
            if (_engineBroken) return "";
            try
            {
                float eh = BSH.DamageFormulaHEAT(e.Base, e.Pen, armorValue);
                _g.EngInfN++;
                bool match = Same(eh, e.Res, e.Base);
                if (match) _g.EngInfMatch++;
                return $" ; formule charge creuse du jeu {F(eh)} -> {(match ? "identique" : "différent")}";
            }
            catch (Exception ex)
            {
                _engineBroken = true;
                Log("formules du jeu non appelables (" + ex.GetBaseException().Message + ") : contrôle croisé arrêté pour la session");
                return "";
            }
        }

        // ---------------------------------------------------------------- logs (main thread)
        static string F(float v) => v.ToString("0.##", Inv);
        static string F3(float v) => v.ToString("0.###", Inv);
        static string Pct(float err) => (err * 100f).ToString("0.#", Inv) + " %";
        static float Ratio(float a, float b) => Math.Abs(b) > 1e-6f ? a / b : 0f;
        static string Share(long part, long n) => n <= 0 ? "-" : $"{part}/{n}";
        static string Milli(long sum, long n) => n <= 0 ? "-" : (sum / (double)n / 1000.0).ToString("0.###", Inv);

        static void LogSettings()
        {
            SettingsRead = false; KValid = false; K = 2f; MinCoef = 0f;
            var sb = new StringBuilder("réglages de combat :");
            try
            {
                BattleSettings bs = GameCfg.Instance?.BattleSystemSettings;
                BattleSettings used = null;
                try { used = BSH._battleSettings; } catch { used = null; }
                var src = used ?? bs;
                if (src == null) sb.Append(" illisibles");
                else
                {
                    _penEff = src.ARMOR_PENETRATION_EFFECTIVENESS;
                    MinCoef = src.MINIMAL_DAMAGE_COEFFICIENT;
                    _heEff = src.HE_ARMOR_EFFECTIVENESS;
                    float k = src.HEAT_CURVE_COEFFICIENT;
                    _topAngle = src.BUILDINGS_TOP_ARMOR_AIMING_TRESHOLD_ANGLE;
                    KValid = k > 0f && k < 20f && !float.IsNaN(k);
                    K = KValid ? k : 2f;
                    SettingsRead = true;
                    sb.Append($" ARMOR_PENETRATION_EFFECTIVENESS={F3(_penEff)} MINIMAL_DAMAGE_COEFFICIENT={F3(MinCoef)} HE_ARMOR_EFFECTIVENESS={F3(_heEff)}");
                    sb.Append($" HEAT_CURVE_COEFFICIENT={F3(k)}{(KValid ? "" : " (illisible : 2 utilisé pour la mesure, règle impossible)")} BUILDINGS_TOP_ARMOR_AIMING_TRESHOLD_ANGLE={F3(_topAngle)}");
                    sb.Append(used != null && bs != null && used.Pointer != bs.Pointer ? " (réglages du calcul différents de GameConfig)" : used != null ? " (réglages du calcul)" : " (GameConfig)");
                }
            }
            catch (Exception e) { sb.Append(" lecture impossible (" + e.GetBaseException().Message + ")"); }
            Log(sb.ToString());
        }

        static string _lastSummary;

        static void Summary(bool final)
        {
            var g = _g;
            var sb = new StringBuilder();
            sb.Append($"{(final ? "bilan" : "relevé")} : impacts sur véhicules et infanterie {Interlocked.Read(ref _inHitUnits)} (calculs pendant un impact {Interlocked.Read(ref _inHitAll)}, hors fil principal {Interlocked.Read(ref _inHitOffMain)}), résolus véhicules {g.VehicleHits} / infanterie {g.InfantryHits}");
            sb.Append($", unité inconnue {g.UnknownUnit}, munition inconnue {g.UnknownAmmo}, sans blindage lisible {g.NoCandidate}, munitions sans blindage visé {g.IgnoreHits}, perdus {_evLost}");
            sb.Append($" ; G1 cinétique : P/A < 0,5 {g.KinBelow} (à zéro {g.KinBelowZero}, au plancher {g.KinBelowFloor}, autre {g.KinBelowOther}), entre 0,5 et 1 {g.KinBand} (conformes {g.KinBandFit}, avec plancher {g.KinBandFitFloor}), perce {g.KinFull} (dégâts pleins {g.KinFullFit}), à la limite exacte (non comptés) {g.KinEdge}, blindage nul {g.KinNoArmor}, blindages candidats différents {g.KinAmbiguous} (dont conformes au meilleur {g.KinAmbiguousFit}) ; blindage identifié par la formule du jeu parmi plusieurs options {g.IdentifiedByEngine}");
            sb.Append($" ; G2 charge creuse (k={F3(K)}) : {g.HeatN} (conformes {g.HeatFit}), sous la moitié du blindage {g.HeatHalf} (conformes {g.HeatHalfFit}), par souffle 0 m {Share(g.HeatAoeFit[0], g.HeatAoeN[0])}, <5 m {Share(g.HeatAoeFit[1], g.HeatAoeN[1])}, 5-20 m {Share(g.HeatAoeFit[2], g.HeatAoeN[2])}, 20 m+ {Share(g.HeatAoeFit[3], g.HeatAoeN[3])}, blindage nul {g.HeatNoArmor}, candidats différents {g.HeatAmbiguous} (conformes au meilleur {g.HeatAmbiguousFit})");
            sb.Append($" ; infanterie, courbe sur ArmorValue : {Share(g.InfFit, g.InfN)}");
            sb.Append($" ; G3 IA hors impact (1 sur 16) : appels {Interlocked.Read(ref _outSampled)} (hors fil principal {Interlocked.Read(ref _outOffMain)}), sur véhicule {Interlocked.Read(ref _outVehicle)}, résultat 0 {Interlocked.Read(ref _outZero)} (cinétique {Interlocked.Read(ref _outZeroKin)}, charge creuse {Interlocked.Read(ref _outZeroHeat)}), sous 20 % {Interlocked.Read(ref _outLow)}");
            sb.Append($" ; G4 stress pendant un impact : {Interlocked.Read(ref _stressIn)} (hors impact {Interlocked.Read(ref _stressOut)}, hors fil principal {Interlocked.Read(ref _stressOffMain)}), dégâts nuls {Interlocked.Read(ref _stressZero)} (stress x{Milli(Interlocked.Read(ref _stressZeroRatio), Interlocked.Read(ref _stressZeroRatioN))}), dégâts positifs {Interlocked.Read(ref _stressPos)} (stress x{Milli(Interlocked.Read(ref _stressPosRatio), Interlocked.Read(ref _stressPosRatioN))})");
            sb.Append($" ; G5 toit forcé : véhicules {g.TopVehicle}, infanterie {g.TopInfantry}");
            sb.Append($" ; formules du jeu : cinétique identiques {Share(g.EngKinMatch, g.EngKinN)}, charge creuse identiques {Share(g.EngHeatMatch, g.EngHeatN)}, l'autre formule correspond {g.EngOtherFormula}, infanterie {Share(g.EngInfMatch, g.EngInfN)}, blindage lu par le jeu identique {Share(g.EngArmorAgree, g.EngArmorN)}");
            sb.Append($" ; lignes détaillées non écrites {_detailSuppressed}, erreurs {Interlocked.Read(ref _errors)}");
            string s = sb.ToString();
            if (!final && s == _lastSummary) return;
            _lastSummary = s;
            bool any = Interlocked.Read(ref _inHitAll) + Interlocked.Read(ref _outSampled) + Interlocked.Read(ref _stressIn) > 0;
            if (any || final) Log(s);
        }

        /// Battle end: one line per gate with its verdict; returns this battle's counts for the stage switch.
        static GateTotals GateSummary(string why)
        {
            var g = _g;
            var t = new GateTotals
            {
                KinBelow = g.KinBelow, KinBelowZero = g.KinBelowZero, KinBelowFloor = g.KinBelowFloor,
                KinBand = g.KinBand, KinBandFit = g.KinBandFit, KinBandFitFloor = g.KinBandFitFloor,
                HeatN = g.HeatN, HeatFit = g.HeatFit, K = K, Coef = MinCoef
            };
            var g1 = EvalG1(t);
            var g2 = EvalG2(t);
            Log($"porte G1 (cinétique, cette bataille) : {Verdict(g1)} ; {t.KinBelow + t.KinBand} tirs utiles sur {MinGateHits} demandés, P/A < 0,5 : {t.KinBelow} dont exactement 0 {t.KinBelowZero} (plancher {F3(MinCoef)} : {t.KinBelowFloor}), entre 0,5 et 1 : {t.KinBand} dont conformes à 3 % {t.KinBandFit} (95 % demandés)");
            Log($"porte G2 (charge creuse, cette bataille) : {Verdict(g2)} ; {t.HeatN} tirs sur {MinGateHits} demandés, conformes à 3 % {t.HeatFit} (95 % demandés), k={F3(K)}{(KValid ? "" : " illisible")} -> seuil de la règle {F3(1f / (1f + (float)Math.Pow(2, K)))} x dégâts de base");
            long outVeh = Interlocked.Read(ref _outVehicle), outZero = Interlocked.Read(ref _outZero);
            Log($"porte G3 (IA) : {(outVeh == 0 ? "données insuffisantes" : outZero > 0 ? "réussie : le jeu renvoie déjà 0 contre des véhicules" : "échouée : aucun 0 vu contre des véhicules")} ; {outZero}/{outVeh} appels échantillonnés à 0 (cinétique {Interlocked.Read(ref _outZeroKin)}, charge creuse {Interlocked.Read(ref _outZeroHeat)})");
            long z = Interlocked.Read(ref _stressZeroRatioN), p = Interlocked.Read(ref _stressPosRatioN);
            string g4;
            if (z < 10 || p < 10) g4 = "données insuffisantes";
            else
            {
                double rz = Interlocked.Read(ref _stressZeroRatio) / (double)z / 1000.0, rp = Interlocked.Read(ref _stressPosRatio) / (double)p / 1000.0;
                g4 = rz >= 0.5 * rp ? $"le stress reste quand les dégâts sont nuls (x{rz.ToString("0.###", Inv)} contre x{rp.ToString("0.###", Inv)})"
                                    : $"le stress s'effondre quand les dégâts sont nuls (x{rz.ToString("0.###", Inv)} contre x{rp.ToString("0.###", Inv)}) : les mitrailleuses ne neutralisent plus les blindés, à signaler";
            }
            Log($"porte G4 (neutralisation) : {g4} ; appels pendant un impact dégâts nuls {Interlocked.Read(ref _stressZero)}, positifs {Interlocked.Read(ref _stressPos)}");
            var fam = string.Join(", ", g.TopByFamily.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} x{kv.Value}"));
            var table = _lastTable;
            var ammo = string.Join(", ", g.TopByAmmo.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{(table != null ? table.NameOf(kv.Key) : kv.Key.ToString(Inv))} ({kv.Key}) x{kv.Value}"));
            Log($"porte G5 (toit forcé) : {(g.TopVehicle + g.TopInfantry > 0 ? "sources relevées" : "aucun toit forcé vu")} ; véhicules {g.TopVehicle}, infanterie {g.TopInfantry} ; familles : {(fam.Length > 0 ? fam : "-")} ; munitions : {(ammo.Length > 0 ? ammo : "-")}");
            return t;
        }

        internal static string Verdict(GateState s) => s switch
        {
            GateState.Pass => "réussie",
            GateState.Floor => "plancher trouvé (règle cinétique à ajouter)",
            GateState.Fail => "échouée",
            _ => "données insuffisantes",
        };

        static void ClearBattle()
        {
            _g = new Gates();
            _known.Clear(); _cands.Clear();
            _evRead = Interlocked.Read(ref _evHead); _evLost = 0;
            Interlocked.Exchange(ref _inHitAll, 0); Interlocked.Exchange(ref _inHitUnits, 0); Interlocked.Exchange(ref _inHitOffMain, 0);
            Interlocked.Exchange(ref _outSampled, 0); Interlocked.Exchange(ref _outVehicle, 0); Interlocked.Exchange(ref _outZero, 0);
            Interlocked.Exchange(ref _outZeroKin, 0); Interlocked.Exchange(ref _outZeroHeat, 0); Interlocked.Exchange(ref _outLow, 0); Interlocked.Exchange(ref _outOffMain, 0);
            Interlocked.Exchange(ref _errors, 0);
            Interlocked.Exchange(ref _stressIn, 0); Interlocked.Exchange(ref _stressOut, 0); Interlocked.Exchange(ref _stressZero, 0); Interlocked.Exchange(ref _stressPos, 0);
            Interlocked.Exchange(ref _stressZeroRatio, 0); Interlocked.Exchange(ref _stressPosRatio, 0); Interlocked.Exchange(ref _stressZeroRatioN, 0); Interlocked.Exchange(ref _stressPosRatioN, 0);
            Interlocked.Exchange(ref _stressOffMain, 0);
            _detailSuppressed = 0; _lastSummary = null; _depthWarned = false;
            _lastTable = null;
        }
    }

    // ==================================================================================================== half-armour zero rule

    static class DemiBlindage
    {
        const int MaxErrors = 50;
        const float SilenceSeconds = 180f, ContactNeeded = 120f, ContactRange = 1000f;
        const float KinFloorMax = 0.1001f;                           // kinetic rule value at P/A 0.5 (0.1), plus float tolerance

        static HarmonyLib.Harmony _harmony;
        static bool _patchTried, _patched, _wanted, _depthLogged, _armedLogged, _tripped;
        static bool _announce;                                       // stage just switched to 1: tell the player when the next battle starts
        static bool _offLogged;
        static float _nextReport, _nextContact, _battleStartReal;

        // hook side
        static volatile bool _armed, _off;
        static volatile float _tHeat, _tKin, _tMax;
        static volatile byte[] _class = Array.Empty<byte>();
        static volatile int[] _vehicleIds = Array.Empty<int>();
        static volatile int _mainThread;
        static long _cutsIn, _cutsOut, _cutsOffMain, _candidates, _errors;
        static long _lastCutsIn = -1, _lastCutsOut = -1, _lastErrors = -1;

        // watchdog (main thread, game time)
        static long _wdHits;
        static float _wdLastHitGame, _wdContact, _wdLastCheckGame;
        static long _wdCutsAtHit;
        static bool _wdHad;
        static double _wdSig;                                        // position signature of the last contact check
        static int _wdCount = -1;
        static bool _wdMoving;                                       // units moved since the last contact check (simulation really running)

        // trip verdict: the rule is off for this battle; stage and proofs are wiped only if fire comes back within one minute of game time
        static float _tripGame;
        static long _tripHits;
        static bool _tripJudged, _tripConfirmed;

        static void Log(string s) => Mod.Log.Msg("[DEMI-BLINDAGE] " + s);
        static float GameNow { get { try { return UnityEngine.Time.time; } catch { return 0f; } } }
        static bool Paused { get { try { return UnityEngine.Time.timeScale <= 0f; } catch { return false; } } }
        static long Cuts => Interlocked.Read(ref _cutsIn) + Interlocked.Read(ref _cutsOut);

        internal static void Disarm() { _armed = false; }

        /// Previous measured battle did not end normally (crash or kill): the rule, if it was on, goes back to measurement.
        internal static void RevertAfterUncleanSession(bool refused)
        {
            _armed = false;
            var st = CalibreMesure.RuleStage;
            if (st == null || st.Value == 0) return;
            st.Value = 0;
            CalibreMesure.RuleProof.Value = "";
            try { MelonPreferences.Save(); } catch { }
            Mod.Log.Warning("[DEMI-BLINDAGE] la dernière partie ne s'est pas terminée normalement : règle remise à l'étape 0 (mesure)" + (refused ? " et mesure coupée par sécurité" : ""));
            Mod.Notify(TxtKey.N_HALF_OFF_INTERRUPTED);
        }

        internal static void BattleStart(float now)
        {
            _armed = false; _tripped = false; _depthLogged = false; _armedLogged = false;
            _battleStartReal = now;
            _nextReport = now + 60f; _nextContact = 0f;
            Interlocked.Exchange(ref _cutsIn, 0); Interlocked.Exchange(ref _cutsOut, 0); Interlocked.Exchange(ref _cutsOffMain, 0); Interlocked.Exchange(ref _candidates, 0);
            _lastCutsIn = _lastCutsOut = _lastErrors = -1;
            _wdHits = 0; _wdHad = false; _wdLastHitGame = GameNow; _wdContact = 0f; _wdLastCheckGame = GameNow; _wdCutsAtHit = 0;
            _wdCount = -1; _wdMoving = false; _wdSig = 0;
            _tripGame = 0f; _tripHits = 0; _tripJudged = _tripConfirmed = false;
            _vehicleIds = Array.Empty<int>();

            _wanted = false;
            var stage = CalibreMesure.RuleStage;
            if (stage == null || CalibreMesure.RuleEnabled == null) return;
            string why = null;
            float tHeat = CalibreMesure.RuleHeat.Value, tKin = CalibreMesure.RuleKin.Value;
            if (!CalibreMesure.RuleEnabled.Value) why = "règle coupée dans les réglages";
            else if (stage.Value != 1) why = "étape 0 : mesure en cours, la règle s'active seule après une bataille aux portes G1 et G2 réussies";
            else if (!Realism.RealModeOn) why = "vraies stats coupées";
            else if (OptionOff("OPTION_DEGATS_CALIBRE")) why = "option OPTION_DEGATS_CALIBRE inactive";
            else if (_off) why = "coupée après trop d'erreurs dans cette session";
            else if (!(tHeat > 0f && tHeat < 0.5f) || tKin < 0f || tKin > KinFloorMax + 0.0001f) why = "seuils enregistrés invalides";   // a kinetic threshold above the half-armour value is refused
            else if (!CalibreMesure.SettingsRead || !CalibreMesure.KValid) why = "réglages de combat illisibles";
            else
            {
                // the engine settings must still be those the proof was made with
                float expect = 1f / (1f + (float)Math.Pow(2, CalibreMesure.K));
                bool kinOk = tKin == 0f || Math.Abs(tKin - (CalibreMesure.MinCoef + 0.0001f)) <= 1e-4f;
                if (Math.Abs(expect - tHeat) > 1e-4f || !kinOk)
                {
                    stage.Value = 0; CalibreMesure.RuleHeat.Value = 0f; CalibreMesure.RuleKin.Value = 0f; CalibreMesure.RuleProof.Value = "";
                    try { MelonPreferences.Save(); } catch { }
                    why = "les réglages de combat du jeu ont changé depuis la mesure : retour à l'étape 0 (mesure)";
                    Mod.Notify(TxtKey.N_HALF_REMEASURE);
                }
            }
            if (why != null) { Log("bataille sans la règle : " + why); return; }
            _tHeat = tHeat; _tKin = tKin; _tMax = Math.Max(tHeat, tKin);
            _wanted = true;
            if (_announce) { _announce = false; Mod.Notify(TxtKey.N_HALF_ACTIVE); }
            Log($"bataille avec la règle (étape 1) : coupure sous {tHeat.ToString("0.####", CultureInfo.InvariantCulture)} x dégâts de base pour les charges creuses et explosifs" +
                (tKin > 0f ? $", sous {tKin.ToString("0.####", CultureInfo.InvariantCulture)} x pour les tirs cinétiques (plancher du jeu)" : ", tirs cinétiques laissés au jeu") +
                " ; elle s'active dès que les impacts sont visibles");
        }

        static bool OptionOff(string name)
        {
            try
            {
                var v = Realism.OptionsInactives?.Value ?? "";
                foreach (var p in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(p.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// Main thread, every 0.5 s during a measured battle.
        internal static void Tick(float now, DegatsScan scan, DegatsMunitions table)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            if (_patched && !_off && Interlocked.Read(ref _errors) >= MaxErrors) _off = true;
            if (_off && _patched && !_offLogged)
            {
                _offLogged = true;
                _armed = false;
                Mod.Log.Warning($"[DEMI-BLINDAGE] {MaxErrors} erreurs dans le correctif : règle coupée jusqu'au redémarrage du jeu (dégâts du jeu)");
                Mod.Notify(TxtKey.N_HALF_OFF_ERRORS);
            }
            JudgeTrip();
            if (!_wanted || _off || _tripped) { _armed = false; return; }
            if (!CalibreMesure.Armed || !LeurresMesure.HitDepthReady)
            {
                _armed = false;
                if (!_depthLogged && now - _battleStartReal > 15f) { _depthLogged = true; Log("impacts invisibles (mesure coupée) : règle en attente, aucun tir coupé"); }
                return;
            }
            if (table == null) { _armed = false; return; }
            if (!ReferenceEquals(_class, table.Rule)) _class = table.Rule;
            var ids = new List<int>(scan.All.Count);
            foreach (var u in scan.All) if (u.Vehicle) ids.Add(u.Eid);
            var arr = ids.ToArray();
            Array.Sort(arr);
            _vehicleIds = arr;                                                               // published before arming
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            if (!_patched || _off) { _armed = false; return; }
            _armed = true;
            if (!_armedLogged)
            {
                _armedLogged = true;
                Log($"règle active : {table.RuleHeatCount} munitions à charge creuse ou explosives concernées, {table.RuleKineticCount} cinétiques ({(_tKin > 0f ? "concernées" : "non concernées")}), {table.Exempt} exemptées (artillerie, mortiers, lance-roquettes, missiles balistiques et de croisière, bombes, souffle de 20 m et plus) ; véhicules suivis {arr.Length}");
            }
            Watchdog(now, scan);
            if (now >= _nextReport) { _nextReport = now + 60f; Report(false); }
        }

        /// Combat health: no hit on any unit for 3 minutes of game time after the battle had hits, while cuts continue and ground
        /// units of both sides stand within 1 km of each other for at least 2 of those minutes with the simulation really running
        /// (units moving) -> rule off for this battle, log and notice. Stage and proofs are wiped only if fire resumes right after (JudgeTrip).
        static void Watchdog(float now, DegatsScan scan)
        {
            float g = GameNow;
            long hits = CalibreMesure.ImpactCalls;
            if (hits != _wdHits)
            {
                _wdHits = hits; _wdHad = true; _wdLastHitGame = g; _wdCutsAtHit = Cuts; _wdContact = 0f; _wdLastCheckGame = g;
                _wdCount = -1; _wdMoving = false;
                return;
            }
            if (!_wdHad) return;
            if (Paused) { _wdLastCheckGame = g; return; }
            float silent = g - _wdLastHitGame;
            if (silent < 30f) { _wdLastCheckGame = g; return; }                              // contact is only counted inside a long silence
            if (now >= _nextContact)
            {
                _nextContact = now + 2f;
                float dt = Math.Max(0f, g - _wdLastCheckGame);
                _wdLastCheckGame = g;
                // frozen positions = game paused or frozen (Time.timeScale stays 1 in this game, Time.time keeps running)
                double sig = 0;
                var all = scan.All;
                for (int i = 0; i < all.Count; i++) sig += all[i].Pos.x + 3.0 * all[i].Pos.z;
                _wdMoving = _wdCount >= 0 && (all.Count != _wdCount || Math.Abs(sig - _wdSig) > 0.5);
                _wdSig = sig; _wdCount = all.Count;
                if (_wdMoving && Contact(scan)) _wdContact += dt;
            }
            if (silent < SilenceSeconds || Cuts <= _wdCutsAtHit || _wdContact < ContactNeeded || !_wdMoving) return;
            Trip(silent);
        }

        static bool Contact(DegatsScan scan)
        {
            var a = new List<V3>(); var b = new List<V3>();
            foreach (var u in scan.All) { if (!u.Ground) continue; if (u.Side == 0) a.Add(u.Pos); else b.Add(u.Pos); }
            float r2 = ContactRange * ContactRange;
            foreach (var p in a)
                foreach (var q in b)
                {
                    float dx = p.x - q.x, dz = p.z - q.z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            return false;
        }

        /// The rule goes off for this battle only; stage and proofs stay until JudgeTrip sees fire resume right after the switch-off.
        static void Trip(float silent)
        {
            _tripped = true;
            _armed = false;
            _tripGame = GameNow; _tripHits = CalibreMesure.ImpactCalls; _tripJudged = false; _tripConfirmed = false;
            Report(true);
            Mod.Log.Warning($"[DEMI-BLINDAGE] garde-fou : aucun tir n'a touché d'unité depuis {silent.ToString("0", CultureInfo.InvariantCulture)} s de jeu au contact ({Cuts - _wdCutsAtHit} coupures) : règle coupée pour cette bataille ; étape et preuves gardées sauf si les tirs reprennent dans la minute");
            Mod.Notify(TxtKey.N_HALF_OFF_BATTLE);
        }

        /// Main thread (Tick and BattleEnd). Fire back within one minute of game time after the trip means the rule was blocking it:
        /// stage 0 and proofs wiped. No fire within that minute means the silence was a pause, a frozen game or a lull: nothing wiped.
        /// A minute spent paused leaves the verdict open: the rule stays off for this battle and nothing is wiped (safe side).
        static void JudgeTrip()
        {
            if (!_tripped || _tripJudged || !(_tripGame > 0f)) return;
            float since = GameNow - _tripGame;
            if (CalibreMesure.ImpactCalls > _tripHits && since <= 60f)
            {
                _tripJudged = _tripConfirmed = true;
                if (CalibreMesure.RuleStage != null) CalibreMesure.RuleStage.Value = 0;
                if (CalibreMesure.RuleProof != null) CalibreMesure.RuleProof.Value = "";
                try { MelonPreferences.Save(); } catch { }
                Mod.Log.Warning("[DEMI-BLINDAGE] garde-fou : les tirs ont repris juste après la coupure, la règle bloquait les tirs : retour à l'étape 0 (mesure), preuves effacées");
            }
            else if (since > 60f)
            {
                _tripJudged = true;
                Log("garde-fou : aucun tir n'a repris dans la minute après la coupure : silence non dû à la règle (pause, jeu figé ou accalmie), étape et preuves gardées");
            }
        }

        static void TryPatch()
        {
            try
            {
                var target = AccessTools.Method(typeof(BSH), "CalculateHitDamage");
                if (target == null) { _off = true; Log("calcul des dégâts introuvable dans cette version du jeu : règle impossible"); return; }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.DemiBlindage");
                // VeryHigh: after the measurement (First, which must keep seeing the engine's value), before Resistance and Couvert
                var hm = new HarmonyMethod(typeof(DemiBlindage).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.VeryHigh };
                _harmony.Patch(target, postfix: hm);
                _patched = true;
                Log("correctif installé");
            }
            catch (Exception e)
            {
                _off = true;
                Mod.Log.Warning("[DEMI-BLINDAGE] installation impossible : " + e.GetBaseException().Message);
            }
        }

        /// Hot path, any thread: static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo, ...).
        /// Most calls stop at the float test without any IL2CPP read; no allocation, no Unity call, no logging.
        static void Postfix(EcsEntity target, float baseDamage, Ammo ammoInfo, ref float __result)
        {
            if (Campaign.MissionInerte || !_armed || _off) return;                    // mission without the mod: vanilla damage
            float r = __result;
            if (!(r > 0f) || !(r < _tMax * baseDamage)) return;
            try
            {
                Interlocked.Increment(ref _candidates);
                var cls = _class;
                int id = ammoInfo == null ? 0 : DegatsAmmoCache.Id(ammoInfo);
                if ((uint)id >= (uint)cls.Length) return;
                byte c = cls[id];
                if (c == DegatsMunitions.RuleHeat) { if (!(r < _tHeat * baseDamage)) return; }
                else if (c == DegatsMunitions.RuleKinetic) { float tk = _tKin; if (!(tk > 0f) || !(r <= tk * baseDamage)) return; }
                else return;
                if (Array.BinarySearch(_vehicleIds, target.EntityId) < 0) return;
                __result = 0f;
                if (LeurresMesure.HitDepth > 0) Interlocked.Increment(ref _cutsIn); else Interlocked.Increment(ref _cutsOut);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _cutsOffMain);
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) _off = true;             // repeated errors: vanilla damage
            }
        }

        static void Report(bool final)
        {
            long ci = Interlocked.Read(ref _cutsIn), co = Interlocked.Read(ref _cutsOut), er = Interlocked.Read(ref _errors);
            if (!final && ci == _lastCutsIn && co == _lastCutsOut && er == _lastErrors) return;
            _lastCutsIn = ci; _lastCutsOut = co; _lastErrors = er;
            Log($"{(final ? "bilan" : "relevé")} : tirs mis à 0 pendant un impact {ci}, dans les calculs de l'IA {co} (hors fil principal {Interlocked.Read(ref _cutsOffMain)}), candidats examinés {Interlocked.Read(ref _candidates)}, erreurs {er} ; véhicules suivis {_vehicleIds.Length}");
        }

        /// Battle end (main thread): cumulates this battle's gate counts and switches the stage from the logged proof rules.
        internal static void BattleEnd(CalibreMesure.GateTotals battle, bool normal, string why, long impactsOnUnits, long measureErrors)
        {
            bool wasArmed = _armed || _armedLogged;
            _armed = false;
            if (wasArmed) Report(true);
            JudgeTrip();                                                                     // a hit since the last tick may still confirm a trip
            var stage = CalibreMesure.RuleStage;
            if (stage == null || battle == null) return;
            var inv = CultureInfo.InvariantCulture;
            if (_tripped)
            {
                Log(_tripConfirmed ? "étape 0 après le garde-fou de cette bataille : preuves remises à zéro"
                                   : $"règle coupée par le garde-fou sans preuve qu'elle bloquait les tirs : bataille non comptée, étape {stage.Value} et preuves inchangées");
                return;
            }
            if (!normal) { Log($"fin anormale ({why}) : preuves et étape inchangées (étape {stage.Value})"); return; }
            if (!CalibreMesure.SettingsRead || !CalibreMesure.KValid) { Log("réglages de combat illisibles : étape inchangée, bataille non comptée"); return; }
            if (impactsOnUnits <= 0 || measureErrors >= MaxErrors)
            {
                Log($"bataille non comptée (impacts sur unités {impactsOnUnits}, erreurs de mesure {measureErrors}) : étape {stage.Value} inchangée");
                return;
            }

            var total = CalibreMesure.GateTotals.Parse(CalibreMesure.RuleProof.Value);
            if (total == null || Math.Abs(total.K - battle.K) > 1e-4f || Math.Abs(total.Coef - battle.Coef) > 1e-5f)
                total = new CalibreMesure.GateTotals { K = battle.K, Coef = battle.Coef };
            total.Add(battle);
            var g1 = CalibreMesure.EvalG1(total);
            var g2 = CalibreMesure.EvalG2(total);
            Log($"preuves cumulées : G1 {CalibreMesure.Verdict(g1)} (P/A < 0,5 : {total.KinBelow} dont 0 exact {total.KinBelowZero}, plancher {total.KinBelowFloor} ; 0,5 à 1 : {total.KinBand} dont conformes {total.KinBandFit}) ; G2 {CalibreMesure.Verdict(g2)} ({total.HeatN} tirs dont conformes {total.HeatFit}) ; impacts sur unités cette bataille {impactsOnUnits}");

            if (stage.Value != 1)
            {
                bool g1ok = g1 == CalibreMesure.GateState.Pass || g1 == CalibreMesure.GateState.Floor;
                if (g2 == CalibreMesure.GateState.Pass && g1ok && impactsOnUnits >= CalibreMesure.MinGateHits)
                {
                    float tHeat = 1f / (1f + (float)Math.Pow(2, battle.K));
                    // kinetic cut only when the floor is at or below the rule's own value at P/A 0.5 (0.1): above it, hits piercing
                    // half the armour or more also land on the floor and a ratio threshold cannot tell them apart
                    float tKin = g1 == CalibreMesure.GateState.Floor && battle.Coef <= KinFloorMax ? battle.Coef + 0.0001f : 0f;
                    stage.Value = 1;
                    CalibreMesure.RuleHeat.Value = tHeat;
                    CalibreMesure.RuleKin.Value = tKin;
                    _announce = true;
                    CalibreMesure.RuleProof.Value = "";
                    Log($"PORTES G1 ET G2 RÉUSSIES : étape 1, la règle s'active dès la prochaine bataille (seuil charge creuse {tHeat.ToString("0.####", inv)}, cinétique {(tKin > 0f ? tKin.ToString("0.####", inv) : "non")})");
                    if (g1 == CalibreMesure.GateState.Floor && battle.Coef > KinFloorMax)
                        Log($"plancher cinétique {battle.Coef.ToString("0.###", inv)} au-dessus de 0,1 : un tir qui perce plus de la moitié du blindage tomberait aussi au plancher, tirs cinétiques laissés au jeu");
                    Mod.Notify(TxtKey.N_HALF_MEASURED);
                }
                else if (g1 == CalibreMesure.GateState.Fail || g2 == CalibreMesure.GateState.Fail)
                {
                    CalibreMesure.RuleProof.Value = "";
                    Log("une porte échoue : la règle reste coupée (étape 0), preuves remises à zéro pour une nouvelle mesure" +
                        (g2 == CalibreMesure.GateState.Fail ? " ; G2 échouée : la version avec instantané du blindage serait nécessaire (pas disponible)" : ""));
                }
                else
                {
                    CalibreMesure.RuleProof.Value = total.Serialize();
                    Log("données encore insuffisantes : la règle reste coupée (étape 0), preuves gardées pour la prochaine bataille");
                }
                return;
            }

            // stage 1: a contradicting proof puts the rule back to measurement
            if (g1 == CalibreMesure.GateState.Fail || g2 == CalibreMesure.GateState.Fail)
            {
                stage.Value = 0;
                CalibreMesure.RuleHeat.Value = 0f; CalibreMesure.RuleKin.Value = 0f; CalibreMesure.RuleProof.Value = "";
                Log($"la mesure ne confirme plus les formules (G1 {CalibreMesure.Verdict(g1)}, G2 {CalibreMesure.Verdict(g2)}) : règle coupée, retour à l'étape 0");
                Mod.Notify(TxtKey.N_HALF_OFF_FORMULA);
                return;
            }
            if (g1 == CalibreMesure.GateState.Floor && battle.Coef <= KinFloorMax && CalibreMesure.RuleKin.Value == 0f)
            {
                CalibreMesure.RuleKin.Value = battle.Coef + 0.0001f;
                Log($"plancher cinétique confirmé : tirs cinétiques au plancher aussi coupés dès la prochaine bataille (seuil {CalibreMesure.RuleKin.Value.ToString("0.####", inv)})");
            }
            else if (g1 == CalibreMesure.GateState.Pass && CalibreMesure.RuleKin.Value > 0f)
            {
                CalibreMesure.RuleKin.Value = 0f;
                Log("plus de plancher cinétique : tirs cinétiques laissés au jeu dès la prochaine bataille");
            }
            else if (CalibreMesure.RuleKin.Value > KinFloorMax + 0.0001f)
            {
                // stored earlier above the half-armour value (BattleStart refuses it): kinetic hits go back to the game, the HEAT rule stays
                CalibreMesure.RuleKin.Value = 0f;
                Log("seuil cinétique enregistré au-dessus de 0,1 : tirs cinétiques laissés au jeu dès la prochaine bataille");
            }
            bool large = total.KinBelow + total.KinBand + total.HeatN > 5000;
            CalibreMesure.RuleProof.Value = large ? "" : total.Serialize();
            Log($"étape 1 conservée{(large ? " (preuves remises à zéro après 5000 tirs)" : "")}");
        }
    }
}
