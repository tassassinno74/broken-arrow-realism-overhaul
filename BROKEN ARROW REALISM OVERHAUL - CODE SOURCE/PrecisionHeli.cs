// RealismOverhaul - gun accuracy against helicopters (values of 2026-09-17), both sides alike, no engine hook.
//  With the real ranges the dispersion radii of the ammunition rows became several times tighter in angle, and nothing adds the aiming
//  error of a gunner leading a moving helicopter (the engine's own lead error is not measured), so by the engine's estimate machine guns
//  and cannons hit a flying helicopter with nearly every round inside their range limits. The tight spread is right against ground targets. A weapon already chooses among its ammunition rows by target type (AP/HE pairs, vanilla row 589 targets
//  helicopters only), so the helicopter part gets its own row:
//   - each gun row of reel/PrecisionAntiHelico.csv (7.62 mm class machine guns, 12.7/14.5 mm, 20-30 mm cannons, fire-control 25-57 mm
//     cannons) gets a template row Id 20000+source in the ammunition table: TargetType Helicopter only, range L = min(LowAltRange,
//     GroundRange), dispersion reference range L, DispersionMinimal 0, dispersion radii = spread angle x L (so the miss distance grows with
//     the range), every other field kept (damage, penetration, fuse, supply cost, effects);
//   - on the per-unit weapon copies of GROUND units only (CopiesUnites walk, every walk path), the weapon gets a copy of that template in
//     its ammunition list, 25 % of the weapon's rounds of that row move to it (Weapons.WeaponAmmunitions, keyed by ammunition id, the total
//     is unchanged), and the unit's own copy of the source row loses the Helicopter bit. Every copy of one (unit, source) pair gets the same
//     range: the template range capped by the Affuts manual-aim cap; a pair fired from mounts of different classes gets no copy (the unit
//     holds one ammunition box per ammunition id).
//  Dedicated anti-air guns, missiles, helicopter and plane guns are never listed, and a row with an Aircraft, Projectile, SEAD, Cruise or
//  Ballistic bit, a seeker or a non direct-fire trajectory is refused whatever the file says.
//  Safety:
//   - self-test at every application (main thread, never in an online session): Clone must copy the fields, the template Ids must be free,
//     and both the table and DataBaseService must return each template by Id; any failure removes every template and creates nothing
//     (the range limits of AntiHeliPortee stay the only anti-helicopter rule);
//   - a table row, a weapon, list, count dictionary or ammunition object reached by several units, or a count dictionary whose keys are
//     not the weapon's own ammunition ids, is never written (own owner survey before the first write, like Affuts);
//   - idempotent per object: a copy made by the game from a split unit is recognised by its count key or its list entry, a list entry
//     without its count key is removed (the unit could not be built with it);
//   - every change is journaled (templates, list entries, count keys, Helicopter bit) and undone at restore; the restore walk then removes
//     any leftover from the loaded units and the table and logs the count (0 expected);
//   - crash guard: two unfinished campaign battles in a row with copies in play -> the next battle without copies; a second time
//     without a normal battle in between -> off for this mod version. The template creation with the first pass, the deviation probe
//     and the direct estimate calls sit behind an in-progress marker (a game stopped inside one never runs it again in this version);
//   - battle checks from the game's own ammunition counts (HUD name filter, no hook): copies absent from every sampled carrier while
//     their source rows read fine, or carriers holding copies with enemy helicopters in flight within 600 m for a long time without a
//     single copy round, put the Helicopter bit back on the stripped rows and give the split rounds back to the source rows for the rest
//     of the battle (range limits only); a carrier whose copy count is empty within range for 30 s while its source row keeps rounds
//     gives that unit type its source rows back on its own (its copies stay); per battle only.
//  Measurement [PRECISION] (read-only): the engine's deviation sampler is probed once in the menu (radial shape, axes, scaling with the
//  distance and the reference range) and a shape factor per class keeps the planned engine hit rate at the calibration distance; in
//  battle, copy rounds fired per class, 250 m band and helicopter aspect (from the game's ammunition counts), hits on helicopters by copy
//  rows (reported by AntiHeliTouches), live copy values, the engine's accuracy estimate for the templates and the battle dispersion
//  settings. Logs start with "[PRECISION] anti-hélico".
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Data;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using WeaponRow = Il2CppBrokenArrow.DataBase.Models.Weapons;
using UnitRow = Il2CppBrokenArrow.DataBase.Models.Units;
using AmmoTarget = Il2CppBrokenArrow.DataBase.Enums.TargetType;
using SeekerType = Il2CppBrokenArrow.DataBase.Enums.SeekerType;
using Trajectory = Il2CppBrokenArrow.DataBase.Enums.TrajectoryType;
using DbSource = Il2CppBrokenArrow.DataBase.DataBaseSourceData;
using DbService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using Shooting = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShootingSystem;
using RandomComp = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.RandomComponent;
using GameConfig = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using Aircraft = Il2CppBrokenArrow.Client.Ecs.Planes.AircraftHelper;
using AltLevel = Il2CppBrokenArrow.Shared.Ecs.Enums.GlobalAltitude;
using GetAmmoDel = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.EcsEventBus.GameplayBus.GetUnitsAmmoDel;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using AmmoList = Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.DataBase.Models.Ammunitions>;
using CountDict = Il2CppSystem.Collections.Generic.Dictionary<long, int>;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class PrecisionHeli
    {
        internal const int IdOffset = 20000;
        const int IdSpan = 10000;
        const string GuardVersion = "1.1";
        const string OptionName = "OPTION_PRECISION_ANTIHELICO";
        const string CsvFile = "PrecisionAntiHelico.csv";
        const string Safety = "Sécurité automatique, ne pas modifier";
        const long HeliBit = 8, NoCopyBits = 16 | 128 | 256 | 512 | 1024;   // Aircraft, Projectile, SEAD, Cruise, Ballistic
        const int TypeHeli = 8, TypePlane = 16;
        const int MinQuantity = 8, MaxErrors = 50, Classes = 4, NBands = 13, Aspects = 4;
        const byte KindWeapon = 1, KindList = 2, KindDict = 3, KindAmmo = 4;
        const float RefWidth = 9.1f, RefHeight = 3.6f, HeadOnWidth = 2.7f;   // Mi-35M box: average aspect and head-on (Units row width)
        const float TickEvery = 2f, ReportEvery = 60f, SummaryEvery = 60f, MaxReadGap = 8f;
        const int MaxReadsPerTick = 8, MaxCreatedKept = 32, MaxStripped = 60000, ProbeSamples = 4096;   // per-row counts are also read by AntiHeliTouches
        const float ExposureNeeded = 600f, ExposureRange = 600f;             // carrier-seconds with an enemy helicopter in flight within 600 m
        const float IdleTickEvery = 6f;                                      // snapshot period while no helicopter is on the map
        const float DryNeeded = 30f;                                         // seconds with an empty copy count in range before the source rows come back
        static readonly string[] ClassCodes = { "MG7", "HMG", "CANON", "CANON_CT" };
        static readonly string[] ClassLabels = { "mitrailleuses 7,62 mm", "mitrailleuses 12,7-14,5 mm", "canons 20-30 mm", "canons à conduite de tir 25-57 mm" };
        static readonly string[] AspectLabels = { "de face ou de dos", "en biais", "de profil", "stationnaire" };
        // never given a copy, whatever the data file says: dedicated anti-air guns, helicopter guns, helicopter-only rows
        static readonly HashSet<int> Excluded = new() { 162, 189, 212, 213, 231, 60, 62, 262, 32, 350, 454, 201, 491, 65, 448, 563, 564, 78, 589 };
        static readonly int[] EstimateAmmo = { 19, 70, 204 };
        static readonly int[] EstimateUnits = { 263, 46, 270, 145 };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------ data
        sealed class Spec { internal int Src, Class; internal string Name, Option; internal float EH, EV, Share, Cal; internal bool KeepFuse; }

        sealed class Template
        {
            internal Spec Spec;
            internal int SourceId, CopyId;
            internal Ammo Source, Copy;                                   // table row and template row (held references)
            internal float L, High, K, SpreadH, SpreadV;                  // spreads in mrad after the shape factor
            internal string Hud, Name, SourceHud;
        }

        internal sealed class Own { internal byte Kind; internal int Unit; internal bool Multi; }

        sealed class Group { internal string Hud, SourceHud; internal int Class; internal float L; internal bool Ambiguous; internal int[] Sources; }

        sealed class State
        {
            internal DbSource Src;                                        // held reference
            internal readonly Dictionary<int, Template> BySource = new();
            internal readonly HashSet<int> Ground = new();
            internal readonly Dictionary<long, float> PairL = new();      // (unit << 32 | source) -> range of its copies
            internal readonly HashSet<IntPtr> TableAmmo = new(), TableWeapons = new();
            internal readonly Dictionary<IntPtr, Own> Map = new();        // first pass survey
            internal readonly Dictionary<int, Group[]> Groups = new();    // ground unit type -> copy groups by HUD name
            internal int Copies, Splits, Refreshed, Stripped, Errors;
            internal int SkipTable, SkipShared, SkipKeys, SkipFew, SkipNoCount, OrphansRemoved;
            internal int PairsAmbiguous, PairsMinRange, GroupsAmbiguous, RowsAbsent, RowsRenamed, RowsRefused, RowsNoRange, RowsOptionOff;
            internal bool InGetAll;
        }

        /// Owner survey of one walk: the plan map for the first pass over the loaded units, a local map for a copy made later.
        internal sealed class Walk
        {
            internal Dictionary<IntPtr, Own> Local;
            internal int Copies, Splits;
        }

        sealed class Created { internal Ammo Copy, Source; internal int Unit; internal float L; }

        /// One quantity split in place: the weapon's count dictionary and ammunition list, the source key and the copy key.
        sealed class SplitRef { internal CountDict Dict; internal AmmoList List; internal long Src, Copy; }

        sealed class Agg
        {
            internal readonly long[] Shots = new long[NBands], Hits = new long[NBands];
            internal readonly long[] AspectShots = new long[Aspects], AspectHits = new long[Aspects];
            internal long OutOfRange;
        }

        sealed class Heli { internal int Eid, Side; internal V3 Pos, Vel; internal bool Air; }
        sealed class Carrier { internal int Uid, Side, UnitId; internal V3 Pos; }
        sealed class Sample { internal int Uid, UnitId, C, S, T; internal string Hud; }

        // ------------------------------------------------------------ state
        static MelonPreferences_Entry<int> _unclean, _trips;
        static MelonPreferences_Entry<bool> _pause;
        static MelonPreferences_Entry<string> _refusal, _guardVersion, _callPending, _callRefused;
        static volatile State _state;
        static volatile HashSet<long> _pairs;                            // (unit << 32 | source) pairs with copies, replaced as a whole
        static volatile HashSet<int> _lastSources;                       // source ids of the last plan (restore check)
        static DbSource _lastSrc;                                        // held reference
        static volatile bool _dead, _battleRepair;
        static volatile int _mainThread = -1;
        static int _errorsTotal;
        static readonly ConcurrentQueue<string> _errorQueue = new();
        static string _lastSummary, _lastRefusal;
        static int _pendingNotify = -1;                  // TxtKey of a notice to show on the next main-thread frame (-1 = none)
        static float _nextSummary;
        static bool _pausedApply, _deadLogged;

        static readonly object _regLock = new();
        static readonly List<Created> _created = new();
        static readonly List<Ammo> _stripped = new();
        static readonly HashSet<IntPtr> _strippedSet = new();
        static readonly Dictionary<int, List<Ammo>> _strippedByUnit = new();      // stripped source rows of one ground unit type
        static readonly List<SplitRef> _splits = new();                           // quantity splits, merged back by the battle guard
        static readonly HashSet<(IntPtr, long)> _splitSet = new();

        // probe
        static bool _probeDone, _probeUsable, _axesSwapped;
        static float[] _px, _py;
        static string _probeLine;

        // battle
        static bool _armed, _battleStarted, _everOnline, _staticDone, _g3Done, _measureOff, _pauseConsumed;
        static float _battleStart, _nextTick, _nextReport, _firstCarrier = -1f;
        static int _g2State, _tickErrors, _readRound;                    // g2: 0 sampling, 1 proven, 2 not conclusive, -1 failed
        static long _reads, _readErrors, _gapShots, _ambiguousShots, _copyShotsTotal, _hitsTotal, _hitsUnplaced;
        static readonly Agg[] _agg = { new(), new(), new(), new() };
        static readonly float[] _exposure = new float[Classes];
        static readonly HashSet<int> _exposedCarriers = new(), _carrierPositive = new(), _sampledUids = new(), _sampledTypes = new();
        static readonly HashSet<long> _groupPositive = new();             // (uid, group) pairs whose own copy count was once read above zero
        static readonly List<Sample> _samples = new();
        static readonly Dictionary<long, (int count, float t)> _lastCount = new();
        static readonly Dictionary<int, float> _dryTime = new();          // ground unit type -> time with an empty copy count in range
        static readonly HashSet<int> _dryRepaired = new();                // unit types given their source rows back for this battle
        static long _dryCarriers;
        static readonly Dictionary<int, int> _unitIdByUid = new(), _typeByUnitId = new();
        static readonly Dictionary<int, (V3 pos, float t)> _heliPrev = new();
        static Dictionary<int, Heli> _heliByEid = new();
        static List<Carrier>[] _carriers = { new(), new() };
        static readonly Dictionary<string, AmmoFilterData> _filters = new();
        static AmmoFilterData _anyFilter;
        static LuaMap _map;
        static Il2CppSystem.Collections.Generic.List<int> _il2One;
        static Il2CppSystem.Collections.Generic.IReadOnlyList<int> _il2OneRo;
        static string _lastReport;

        static void Log(string s) => Mod.Log.Msg("[PRECISION] anti-hélico : " + s);
        static void Warn(string s) => Mod.Log.Warning("[PRECISION] anti-hélico : " + s);

        // ------------------------------------------------------------ public helpers (lock-free)

        /// True for the Id of a helicopter-only copy row (20001-29999).
        internal static bool IsCopy(long id) => id > IdOffset && id < IdOffset + IdSpan;

        /// Source row Id of a copy row, or the Id itself.
        internal static int SourceOf(int id) => IsCopy(id) ? id - IdOffset : id;

        /// True when this ground unit type gets helicopter-only copies of this source row in the current plan.
        internal static bool HasCopy(int unitId, int sourceId)
        {
            var p = _pairs;
            return p != null && p.Contains(PairKey(unitId, sourceId));
        }

        static long PairKey(int unit, int ammo) => ((long)unit << 32) | (uint)ammo;

        // ------------------------------------------------------------ module interface (called through Affuts)

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_PrecisionHeli");
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc(Safety));
            _trips = c.CreateEntry("Coupures", 0, description: Build.Desc(Safety));
            _pause = c.CreateEntry("PauseUneBataille", false, description: Build.Desc(Safety));
            _refusal = c.CreateEntry("Refus", "", description: Build.Desc(Safety));
            _callPending = c.CreateEntry("AppelEnCours", "", description: Build.Desc(Safety));
            _callRefused = c.CreateEntry("AppelsRefuses", "", description: Build.Desc(Safety));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc(Safety));
            bool save = false;
            if (_guardVersion.Value != GuardVersion)
            {
                _guardVersion.Value = GuardVersion;
                _unclean.Value = 0; _trips.Value = 0; _pause.Value = false; _refusal.Value = ""; _callPending.Value = ""; _callRefused.Value = "";
                save = true;
            }
            if (!string.IsNullOrEmpty(_callPending.Value))
            {
                // the game stopped during a direct call: never again in this version
                var refused = new HashSet<string>((_callRefused.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) { _callPending.Value };
                _callRefused.Value = string.Join(",", refused);
                _callPending.Value = "";
                save = true;
            }
            if (save) MelonPreferences.Save();
            _mainThread = Environment.CurrentManagedThreadId;
        }

        /// Called once per second by Affuts.Frame inside a campaign mission (main thread).
        internal static void Frame(float now)
        {
            if (_unclean == null) return;
            _mainThread = Environment.CurrentManagedThreadId;
            FlushErrors();
            { int n = System.Threading.Interlocked.Exchange(ref _pendingNotify, -1); if (n >= 0) Mod.Notify((TxtKey)n); }
            var st = _state;
            if (st != null && now >= _nextSummary) { _nextSummary = now + SummaryEvery; Summary(st, "depuis l'application"); }
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_armed) EndBattle("plus de partie", true); return; }
            if (!_battleStarted) BattleStart(now, st);
            if (st == null || _measureOff || !Solo()) return;
            if (!_armed && st.Copies > 0 && !_battleRepair)
            {
                _armed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();
            }
            if (now < _nextTick) return;
            _nextTick = now + (_heliByEid.Count == 0 ? IdleTickEvery : TickEvery);
            try { Tick(now, st, gc); }
            catch (Exception e)
            {
                if (++_tickErrors <= 3) Warn("mesure en bataille : erreur (" + e.GetBaseException().Message + ")");
                if (_tickErrors > 20) { _measureOff = true; Warn("mesure en bataille coupée pour cette bataille (trop d'erreurs) ; les lignes anti-heli restent en place"); }
            }
            if (now >= _nextReport) { _nextReport = now + ReportEvery; try { Report(false); } catch { } }
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission", true);
            ClearBattle();
        }

        internal static void OnBattleEnd()
        {
            try { if (_battleStarted) Report(true); } catch { }
            EndBattle("fin de bataille", true);
        }

        internal static void OnQuit()
        {
            try { if (_battleStarted) Report(true); } catch { }
            EndBattle("fermeture du jeu", true);
        }

        static void EndBattle(string why, bool normal)
        {
            if (!_armed || _unclean == null) return;
            _armed = false;
            _unclean.Value = 0;
            if (normal && _g2State != -1 && !_battleRepair) _trips.Value = 0;
            MelonPreferences.Save();
            Log($"bataille terminée normalement ({why}) : sécurité remise à zéro");
        }

        static void ClearBattle()
        {
            _battleStarted = false; _everOnline = false; _staticDone = false; _g3Done = false; _measureOff = false; _pauseConsumed = false;
            _battleRepair = false;
            _firstCarrier = -1f; _nextTick = 0f; _nextReport = 0f;
            _g2State = 0; _tickErrors = 0; _readRound = 0;
            _reads = _readErrors = _gapShots = _ambiguousShots = _copyShotsTotal = _hitsTotal = _hitsUnplaced = 0;
            _dryCarriers = 0;
            _dryTime.Clear(); _dryRepaired.Clear();
            for (int i = 0; i < Classes; i++) { _agg[i] = new Agg(); _exposure[i] = 0f; }
            _exposedCarriers.Clear(); _carrierPositive.Clear(); _groupPositive.Clear(); _sampledUids.Clear(); _sampledTypes.Clear(); _samples.Clear();
            _lastCount.Clear(); _unitIdByUid.Clear(); _heliPrev.Clear();
            _heliByEid = new Dictionary<int, Heli>();
            _carriers = new[] { new List<Carrier>(), new List<Carrier>() };
            _map = null;
            _lastReport = null;
        }

        static void BattleStart(float now, State st)
        {
            _battleStarted = true;
            _battleStart = now;
            _nextReport = now + ReportEvery;
            if (_pausedApply && !_pauseConsumed && _pause != null && _pause.Value)
            {
                _pauseConsumed = true;
                _pause.Value = false;
                MelonPreferences.Save();
                Log("bataille jouée sans lignes anti-heli (sécurité) : elles reviennent à la prochaine bataille");
            }
            if (st != null) Log($"début de bataille : lignes anti-heli actives ({st.BySource.Count} modèle(s), {st.Copies} copie(s) déjà posée(s))");
            else Log("début de bataille : pas de lignes anti-heli" + (_lastRefusal != null ? $" ({_lastRefusal})" : "") + " ; seules les portées limites s'appliquent");
        }

        // ------------------------------------------------------------ plan (CopiesUnites, under its lock)

        /// Loads the data, probes the deviation sampler once, creates and checks the template rows. Null = no copy in this application.
        internal static Walk BeginPlan(DbService db, DbSource src)
        {
            _state = null; _pairs = null; _battleRepair = false; _pausedApply = false;
            lock (_regLock) { _created.Clear(); _stripped.Clear(); _strippedSet.Clear(); _strippedByUnit.Clear(); _splits.Clear(); _splitSet.Clear(); }
            if (_unclean == null || src == null) return null;
            if (_dead) { _lastRefusal = "coupées après trop d'erreurs"; return null; }
            string why = RefuseReason(out bool safety);
            if (why != null)
            {
                _lastRefusal = why;
                if (safety) Warn("lignes non créées : " + why); else Log("lignes non créées : " + why);
                return null;
            }
            var st = new State { Src = src };
            var specs = LoadSpecs(st);
            if (specs.Count == 0) { _lastRefusal = $"{CsvFile} vide ou illisible"; Warn("lignes non créées : " + _lastRefusal); return null; }
            try { ProbeShape(specs); }
            catch (Exception e) { _probeUsable = false; _probeLine = "sonde de dispersion en erreur (" + e.GetBaseException().Message + ") ; facteur de forme 1"; }
            if (_probeLine != null) { Log(_probeLine); _probeLine = null; }

            foreach (var r in Props.Rows(src.Ammunitions.GetAll())) if (r != null) st.TableAmmo.Add(r.Pointer);
            foreach (var r in Props.Rows(src.Weapons.GetAll())) if (r != null) st.TableWeapons.Add(r.Pointer);
            foreach (var u in Props.Rows(src.Units.GetAll()))
            {
                if (u == null) continue;
                int type = (int)u.Type;
                if (type != 0 && (type & (TypeHeli | TypePlane)) == 0) st.Ground.Add(u.Id);
            }
            // a game stopped between here and the end of the first pass never retries in this version (a crash would repeat at every launch)
            if (!BeginCall("modeles"))
            {
                _lastRefusal = "refusées pour cette version du mod : le jeu s'était arrêté pendant la création des lignes";
                Warn("lignes non créées : " + _lastRefusal);
                return null;
            }
            if (!CreateTemplates(db, src, st, specs, out why))
            {
                EndCall();
                _lastRefusal = "auto-test refusé : " + why;
                Warn($"auto-test refusé, aucune ligne créée (portées limites seulement) : {why}");
                return null;
            }
            BuildPairs(st, src);
            BuildGroups(st, src);
            _lastSrc = src;
            _lastSources = new HashSet<int>(st.BySource.Keys);
            _pairs = new HashSet<long>(st.PairL.Keys);
            _lastRefusal = null;
            _state = st;
            var perClass = Enumerable.Range(0, Classes).Select(c => st.BySource.Values.Where(t => t.Spec.Class == c).ToList()).ToList();
            var classText = new List<string>();
            for (int c = 0; c < Classes; c++)
            {
                var l = perClass[c];
                if (l.Count == 0) continue;
                var t = l[0];
                classText.Add($"{ClassCodes[c]} {l.Count} ligne(s) {F(t.Spec.EH)}/{F(t.Spec.EV)} mrad x{t.K.ToString("0.##", Inv)} = {F(t.SpreadH)}/{F(t.SpreadV)}");
            }
            Log($"auto-test réussi : {st.BySource.Count} modèle(s) ajouté(s) à la table (ids {IdOffset}+source, lus par la table et le service de base de données ; visibles dans la liste complète de la table : {(st.InGetAll ? "oui" : "non")} ; " +
                $"cibles hélicoptère seul, sans bit avion, projectile ni missile : jamais classés défense antiaérienne ni missile infrarouge, vus comme canon anti-hélico sans guidage) ; " +
                $"{string.Join(", ", classText)} ; lignes absentes {st.RowsAbsent}, renommées {st.RowsRenamed}, refusées (bits, guidage, trajectoire, liste antiaérienne) {st.RowsRefused}, sans portée anti-hélico {st.RowsNoRange}, option coupée {st.RowsOptionOff} ; " +
                $"paires unité+munition au sol {st.PairL.Count} ({st.Groups.Count} type(s) d'unités), refusées : affûts de classes différentes {st.PairsAmbiguous}, portée mini trop haute {st.PairsMinRange} ; groupes au nom HUD ambigu (mesure seulement) {st.GroupsAmbiguous}");
            return new Walk();
        }

        /// A walk over one copy made later (FullUnitClone, LoadUnit, GetLoadedTurret, arsenal card). Null when no plan is active.
        internal static Walk BeginWalk() => _state != null && !_dead && SoloNow() ? new Walk { Local = new Dictionary<IntPtr, Own>() } : null;

        internal static void EndWalk(Walk pw) { }

        internal static void EndPlan()
        {
            _state = null;
            _pairs = null;
            ClearMarker("modeles");
        }

        static void ClearMarker(string kind)
        {
            try { if (_callPending != null && _callPending.Value == kind) EndCall(); } catch { }
        }

        internal static void EndFirstPass(Walk pw, int units)
        {
            ClearMarker("modeles");
            var st = _state;
            if (st == null || pw == null) return;
            try
            {
                int multi = st.Map.Values.Count(o => o.Multi);
                int multiDict = st.Map.Values.Count(o => o.Kind == KindDict && o.Multi), dicts = st.Map.Values.Count(o => o.Kind == KindDict);
                Log($"premier passage : {units} unité(s) chargée(s), objets suivis {st.Map.Count} (partagés entre unités {multi} ; dictionnaires de quantités {dicts}, partagés {multiDict}) ; " +
                    $"copies posées {pw.Copies}, quantités partagées {pw.Splits}");
                Summary(st, "premier passage", true);
            }
            catch (Exception e) { Warn("bilan du premier passage impossible : " + e.Message); }
        }

        static string RefuseReason(out bool safety)
        {
            safety = false;
            if (Campaign.MissionInerte) return "mission jouée sans le mod";
            if (!Realism.RealModeOn) return "vraies stats coupées";
            if (OptionOff(OptionName)) return $"option {OptionName} désactivée (OptionsInactives)";
            safety = true;
            if (_unclean.Value >= 2)
            {
                _unclean.Value = 0;
                _trips.Value = _trips.Value + 1;
                if (_trips.Value >= 2) _refusal.Value = "deux fois de suite deux batailles interrompues avec les lignes anti-heli";
                else _pause.Value = true;
                MelonPreferences.Save();
                if (_trips.Value < 2) _pendingNotify = (int)TxtKey.N_PRECISION_OFF_BATTLE;
            }
            if (!string.IsNullOrEmpty(_refusal.Value)) return "refusées pour cette version du mod : " + _refusal.Value;
            if (_pause.Value) { _pausedApply = true; return "coupées pour une bataille par sécurité"; }
            if (_mainThread >= 0 && Environment.CurrentManagedThreadId != _mainThread) return "application hors du fil principal";
            if (!SoloNow()) return "partie en ligne";
            return null;
        }

        static bool OptionOff(string name)
        {
            foreach (var s in (Realism.OptionsInactives?.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (s.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool SoloNow()
        {
            try
            {
                // stricter than the battle modules: a multiplayer lobby already counts as online (copies made there would reach its battle)
                string st = NetStatus.Status.ToString();
                return st == "NotConnected" && !(NetScen.IsNetwork || NetScen.IsScenarioSlave || NetScen.IsScenarioHost);
            }
            catch { return false; }
        }

        static bool Solo()
        {
            if (!SoloNow()) _everOnline = true;
            return !_everOnline;
        }

        // ------------------------------------------------------------ data file

        static List<Spec> LoadSpecs(State st)
        {
            var res = new List<Spec>();
            string text;
            using (var s = typeof(PrecisionHeli).Assembly.GetManifestResourceStream("reel." + CsvFile))
            {
                if (s == null) { Warn($"{CsvFile} absent du mod"); return res; }
                using var r = new StreamReader(s, Encoding.UTF8);
                text = r.ReadToEnd();
            }
            string[] header = null;
            var seen = new HashSet<int>();
            int bad = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                var cells = line.Split(';');
                if (header == null) { header = cells.Select(c => c.Trim()).ToArray(); continue; }
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < header.Length; i++) d[header[i]] = i < cells.Length ? cells[i].Trim() : "";
                string opt = Cell(d, "Option");
                if (opt.Length > 0 && OptionOff(opt)) { st.RowsOptionOff++; continue; }
                int cls = Array.IndexOf(ClassCodes, Cell(d, "Classe").ToUpperInvariant());
                if (!int.TryParse(Cell(d, "SourceId"), NumberStyles.Integer, Inv, out int src) || src <= 0 || src >= IdSpan || cls < 0
                    || !Num(Cell(d, "EcartH_mrad"), out float eh) || !Num(Cell(d, "EcartV_mrad"), out float ev) || !Num(Cell(d, "PartHelico"), out float share)
                    || !Num(Cell(d, "DistanceCalibrage"), out float cal)
                    || !(eh > 0f && eh <= 200f) || !(ev > 0f && ev <= 200f) || !(share >= 0.05f && share <= 0.5f) || !(cal >= 100f && cal <= 5000f)
                    || !seen.Add(src))
                {
                    if (bad++ < 10) Warn($"{CsvFile} : ligne illisible ou en double ignorée : {line}");
                    continue;
                }
                string fuse = Cell(d, "FuseeRadio").ToLowerInvariant();
                res.Add(new Spec { Src = src, Class = cls, Name = Cell(d, "Name"), Option = opt, EH = eh, EV = ev, Share = share, Cal = cal, KeepFuse = fuse != "0" });
            }
            return res;
        }

        static string Cell(Dictionary<string, string> r, string name) => r.TryGetValue(name, out var v) ? v ?? "" : "";

        static bool Num(string s, out float v)
        {
            v = 0f;
            if (!double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, Inv, out var d) || double.IsNaN(d)) return false;
            v = (float)d;
            return true;
        }

        static bool SameName(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// Name of a copy on the cards and the HUD: the source HUD name without its first "mm" (so the source name is never part of it) + " anti-heli".
        static string CopyHud(string hud)
        {
            string h = (hud ?? "").Trim();
            int i = h.IndexOf("mm", StringComparison.Ordinal);
            if (i >= 0) h = h.Remove(i, 2);
            while (h.Contains("  ")) h = h.Replace("  ", " ");
            h = h.Trim();
            return (h.Length > 0 ? h : "?") + " anti-heli";
        }

        static string CopyName(string name) => (name ?? "").Trim() + " AntiHelico";

        // ------------------------------------------------------------ self-test: template rows

        static bool CreateTemplates(DbService db, DbSource src, State st, List<Spec> specs, out string why)
        {
            why = null;
            var table = src.Ammunitions;
            var rows = table?._rows;
            if (rows == null) { why = "table des munitions illisible"; return false; }
            var added = new List<(int id, Ammo c)>();
            try
            {
                foreach (var sp in specs)
                {
                    Ammo row = null;
                    if (!table.TryGetById(sp.Src, out row) || row == null) { st.RowsAbsent++; continue; }
                    if (!SameName(row.Name, sp.Name)) { st.RowsRenamed++; Warn($"ligne {sp.Src} s'appelle '{row.Name}' et non '{sp.Name}' : ignorée"); continue; }
                    long tt = (long)row.TargetType;
                    if ((tt & HeliBit) == 0 || (tt & NoCopyBits) != 0 || row.Seeker != SeekerType.None || row.TrajectoryType != Trajectory.DirectShot || Excluded.Contains(sp.Src))
                    { st.RowsRefused++; continue; }
                    float l = Math.Min(row.LowAltRange, row.GroundRange);
                    if (!(l > 0f)) { st.RowsNoRange++; continue; }
                    int copyId = IdOffset + sp.Src;
                    if (rows.ContainsKey(copyId)) { why = $"identifiant {copyId} déjà pris dans la table"; Remove(rows, added); return false; }
                    var c = row.Clone()?.TryCast<Ammo>();
                    if (c == null || c.Pointer == row.Pointer) { why = $"copie de la ligne {sp.Src} impossible"; Remove(rows, added); return false; }
                    if (c.Id != row.Id || c.Damage != row.Damage || c.MuzzleVelocity != row.MuzzleVelocity || c.GroundRange != row.GroundRange || c.LowAltRange != row.LowAltRange
                        || c.HUDName != row.HUDName || c.TargetType != row.TargetType || c.PenetrationAtMinRange != row.PenetrationAtMinRange)
                    { why = $"la copie de la ligne {sp.Src} ne reprend pas ses valeurs"; Remove(rows, added); return false; }
                    float k = ShapeFactor(sp);
                    var t = new Template
                    {
                        Spec = sp, SourceId = sp.Src, CopyId = copyId, Source = row, L = l, High = row.HighAltRange, K = k,
                        SpreadH = sp.EH * k, SpreadV = sp.EV * k, Hud = CopyHud(row.HUDName), Name = CopyName(row.Name), SourceHud = row.HUDName ?? "",
                    };
                    c.Id = copyId;
                    c.Name = t.Name;
                    c.HUDName = t.Hud;
                    c.TargetType = AmmoTarget.Helicopter;
                    if (!sp.KeepFuse) c.RadioFuseDistance = 0f;
                    SetRanges(c, t, l);
                    t.Copy = c;
                    rows.Add(copyId, c);
                    added.Add((copyId, c));
                    var d = rows; int cid = copyId; var cc = c;
                    Realism.JournalUndo(d.Pointer, "precision-modele:" + cid, () =>
                    {
                        try { if (d.ContainsKey(cid) && d[cid] != null && d[cid].Pointer == cc.Pointer) d.Remove(cid); } catch { }
                    });
                    if (!table.TryGetById(copyId, out Ammo back) || back == null || back.Pointer != c.Pointer)
                    { why = $"la table ne rend pas la ligne {copyId}"; Remove(rows, added); return false; }
                    if (db != null && db.RawAccess != null && db.RawAccess.Pointer == src.Pointer)
                    {
                        var g = db.GetAmmunitionById(copyId);
                        if (g == null || g.Pointer != c.Pointer) { why = $"le service de base de données ne rend pas la ligne {copyId}"; Remove(rows, added); return false; }
                    }
                    st.BySource[sp.Src] = t;
                }
            }
            catch (Exception e)
            {
                why = "erreur (" + e.GetBaseException().Message + ")";
                Remove(rows, added);
                st.BySource.Clear();
                return false;
            }
            if (st.BySource.Count == 0) { why = "aucune ligne utilisable dans cette base"; return false; }
            try
            {
                foreach (var r in Props.Rows(table.GetAll())) if (r != null && IsCopy(r.Id)) { st.InGetAll = true; break; }
            }
            catch { }
            return true;
        }

        static void Remove(Il2CppSystem.Collections.Generic.Dictionary<int, Ammo> rows, List<(int id, Ammo c)> added)
        {
            foreach (var (id, c) in added)
            {
                try { if (rows.ContainsKey(id) && rows[id] != null && rows[id].Pointer == c.Pointer) rows.Remove(id); } catch { }
            }
            added.Clear();
        }

        static void SetRanges(Ammo c, Template t, float l)
        {
            c.GroundRange = l;
            c.LowAltRange = l;
            c.HighAltRange = t.High > 0f ? Math.Min(t.High, l) : 0f;
            c._dispersionReferenceRange = l;
            c.DispersionMinimal = 0f;
            c.DispersionHorizontalRadius = t.SpreadH * l / 1000f;
            c.DispersionVerticalRadius = t.SpreadV * l / 1000f;
        }

        /// (unit, source) pairs of ground units with the range of their copies. A pair fired from mounts of different manual-aim classes gets
        /// none; a minimum range at or above 90 % of the copy range would leave the copy unable to fire.
        static void BuildPairs(State st, DbSource src)
        {
            var pairs = new HashSet<long>();
            foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
            {
                if (wa == null || !st.BySource.ContainsKey(wa.AmmunitionId) || !st.Ground.Contains(wa.UnitId)) continue;
                pairs.Add(PairKey(wa.UnitId, wa.AmmunitionId));
            }
            foreach (long key in pairs)
            {
                int unit = (int)(key >> 32), ammo = (int)(uint)key;
                var t = st.BySource[ammo];
                float l = t.L;
                int cs;
                float cap;
                try { cs = Affuts.PairCapState(unit, ammo, out cap); } catch { cs = 0; cap = 0f; }
                if (cs < 0) { st.PairsAmbiguous++; continue; }
                if (cs > 0 && cap > 0f && cap < l) l = cap;
                float min = 0f;
                try { min = t.Source.MinimalRange; } catch { }
                if (min > 0f && min >= 0.9f * l) { st.PairsMinRange++; continue; }
                st.PairL[key] = l;
            }
        }

        /// Per ground unit type: its copies grouped by HUD name (the game's ammunition count filter reads by HUD name). A group is
        /// ambiguous for the measurement when its sources belong to different classes or another row of the unit has a HUD name that
        /// contains it or is contained in it.
        static void BuildGroups(State st, DbSource src)
        {
            var hudById = new Dictionary<int, string>();
            foreach (var a in Props.Rows(src.Ammunitions.GetAll())) if (a != null && !IsCopy(a.Id)) hudById[a.Id] = (a.HUDName ?? "").Trim().ToLowerInvariant();
            var ammoByUnit = new Dictionary<int, HashSet<int>>();
            foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
            {
                if (wa == null || !st.Ground.Contains(wa.UnitId)) continue;
                if (!ammoByUnit.TryGetValue(wa.UnitId, out var set)) ammoByUnit[wa.UnitId] = set = new HashSet<int>();
                set.Add(wa.AmmunitionId);
            }
            foreach (var byUnit in st.PairL.GroupBy(kv => (int)(kv.Key >> 32)))
            {
                int unit = byUnit.Key;
                var groups = new List<Group>();
                foreach (var byHud in byUnit.GroupBy(kv => st.BySource[(int)(uint)kv.Key].Hud))
                {
                    var srcs = byHud.Select(kv => (int)(uint)kv.Key).ToArray();
                    var classes = srcs.Select(s => st.BySource[s].Spec.Class).Distinct().ToList();
                    var srcHuds = srcs.Select(s => st.BySource[s].SourceHud).Distinct().ToList();
                    string h = byHud.Key.ToLowerInvariant();
                    bool amb = classes.Count != 1;
                    if (ammoByUnit.TryGetValue(unit, out var carried))
                        foreach (int id in carried)
                        {
                            if (!hudById.TryGetValue(id, out var other) || other.Length == 0) continue;
                            if (other.Contains(h) || h.Contains(other)) { amb = true; break; }
                        }
                    if (amb) st.GroupsAmbiguous++;
                    groups.Add(new Group
                    {
                        Hud = byHud.Key, Sources = srcs, Class = classes.Count == 1 ? classes[0] : -1, L = byHud.Max(kv => kv.Value), Ambiguous = amb,
                        SourceHud = srcHuds.Count == 1 ? srcHuds[0] : null,
                    });
                }
                st.Groups[unit] = groups.ToArray();
            }
        }

        // ------------------------------------------------------------ walk callbacks (CopiesUnites, under its lock, maybe off the main thread:
        // no Unity call and no logging here, counters only)

        /// Survey: records the units reaching a weapon holding a listed row (the weapon, its list, its count dictionary, the rows).
        internal static void NoteWeapon(Walk pw, WeaponRow w, int unitId)
        {
            var st = _state;
            if (st == null || pw == null || w == null || _dead) return;
            try
            {
                var list = w.Ammunitions;
                if (list == null) return;
                bool any = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a == null) continue;
                    int id = a.Id;
                    if (!st.BySource.ContainsKey(id) && !IsCopy(id)) continue;
                    any = true;
                    Mark(pw, st, a.Pointer, KindAmmo, unitId);
                }
                if (!any) return;
                Mark(pw, st, w.Pointer, KindWeapon, unitId);
                Mark(pw, st, list.Pointer, KindList, unitId);
                var d = w.WeaponAmmunitions;
                if (d != null) Mark(pw, st, d.Pointer, KindDict, unitId);
            }
            catch (Exception e) { Error(st, e); }
        }

        static void Mark(Walk pw, State st, IntPtr p, byte kind, int unit)
        {
            var map = pw.Local ?? st.Map;
            if (map.TryGetValue(p, out var o) && o.Kind == kind) { if (o.Unit != unit) o.Multi = true; return; }
            map[p] = new Own { Kind = kind, Unit = unit };
        }

        static bool Shared(Walk pw, State st, IntPtr p, byte kind, int unit)
        {
            if (pw.Local != null && pw.Local.TryGetValue(p, out var l) && l.Kind == kind && (l.Multi || l.Unit != unit)) return true;
            return st.Map.TryGetValue(p, out var o) && o.Kind == kind && (o.Multi || o.Unit != unit);
        }

        /// After the Id sync, the ammunition caps and the counts of a weapon reached from a ground unit: its helicopter-only copies.
        /// Returns the number of values written.
        internal static int OnWeapon(Walk pw, WeaponRow w, int unitId)
        {
            var st = _state;
            if (st == null || pw == null || w == null || _dead || _battleRepair || Campaign.MissionInerte) return 0;
            try
            {
                if (!st.Ground.Contains(unitId) || !st.Groups.ContainsKey(unitId)) return 0;
                var list = w.Ammunitions;
                if (list == null) return 0;
                int n = list.Count;
                if (n == 0) return 0;
                List<(Ammo a, Template t, float l)> sources = null;
                Dictionary<int, Ammo> copies = null;
                var ids = new HashSet<long>();
                for (int i = 0; i < n; i++)
                {
                    var a = list[i];
                    if (a == null) continue;
                    int id = a.Id;
                    ids.Add(id);
                    if (IsCopy(id)) { (copies ??= new Dictionary<int, Ammo>())[id] = a; continue; }
                    if (st.BySource.TryGetValue(id, out var t) && st.PairL.TryGetValue(PairKey(unitId, id), out float l))
                        (sources ??= new List<(Ammo, Template, float)>()).Add((a, t, l));
                }
                if (sources == null) return 0;
                if (st.TableWeapons.Contains(w.Pointer)) { st.SkipTable++; return 0; }
                var dict = w.WeaponAmmunitions;
                if (dict == null) { st.SkipNoCount++; return 0; }
                if (Shared(pw, st, w.Pointer, KindWeapon, unitId) || Shared(pw, st, list.Pointer, KindList, unitId) || Shared(pw, st, dict.Pointer, KindDict, unitId))
                { st.SkipShared++; return 0; }
                // the count dictionary must be keyed by this weapon's own ammunition ids (copy keys included)
                foreach (var kv in dict)
                    if (!ids.Contains(kv.Key) && !IsCopy(kv.Key)) { st.SkipKeys++; return 0; }
                int writes = 0;
                foreach (var (a, t, l) in sources)
                {
                    Ammo have = null;
                    copies?.TryGetValue(t.CopyId, out have);
                    writes += Split(pw, st, list, dict, a, t, l, unitId, have);
                }
                return writes;
            }
            catch (Exception e) { Error(st, e); return 0; }
        }

        static int Split(Walk pw, State st, AmmoList list, CountDict dict, Ammo a, Template t, float l, int unitId, Ammo copy)
        {
            if (st.TableAmmo.Contains(a.Pointer)) { st.SkipTable++; return 0; }
            if (Shared(pw, st, a.Pointer, KindAmmo, unitId)) { st.SkipShared++; return 0; }
            long srcKey = t.SourceId, copyKey = t.CopyId;
            bool hasCopyKey = dict.ContainsKey(copyKey), hasSrcKey = dict.ContainsKey(srcKey);
            int writes = 0, q = 0;
            bool splitNow = false;
            if (!hasCopyKey)
            {
                if (hasSrcKey) q = dict[srcKey];
                if (!hasSrcKey || q < MinQuantity)
                {
                    if (!hasSrcKey) st.SkipNoCount++; else st.SkipFew++;
                    if (copy != null)
                    {
                        // a list entry without its count: the unit could not be built with it
                        RemoveEntries(list, copyKey);
                        SetHeliBit(a);
                        st.OrphansRemoved++;
                        writes++;
                    }
                    return writes;
                }
                int qAir = (int)Math.Round(q * t.Spec.Share);
                if (qAir < 1) qAir = 1;
                if (qAir >= q) qAir = q - 1;
                RegisterCountUndo(dict, srcKey, copyKey);
                dict[copyKey] = qAir;
                dict[srcKey] = q - qAir;
                splitNow = true;
                writes += 2;
            }
            else RegisterCountUndo(dict, srcKey, copyKey);

            if (copy == null)
            {
                Ammo c = null;
                try
                {
                    c = t.Copy.Clone()?.TryCast<Ammo>();
                    if (c != null && (c.Pointer == t.Copy.Pointer || c.Id != t.CopyId)) c = null;
                    if (c != null) SetRanges(c, t, l);
                }
                catch { c = null; }
                if (c == null)
                {
                    if (splitNow) { try { dict.Remove(copyKey); dict[srcKey] = q; } catch { } }
                    st.Errors++;
                    return 0;
                }
                RegisterListUndo(list, copyKey);
                list.Add(c);
                writes++;
                st.Copies++; pw.Copies++;
                Remember(c, a, unitId, l);
            }
            else
            {
                RegisterListUndo(list, copyKey);
                if (Refresh(copy, t, l)) { writes++; st.Refreshed++; }
            }

            RememberSplit(dict, list, srcKey, copyKey);
            RegisterBitUndo(a);
            long tt = (long)a.TargetType;
            if ((tt & HeliBit) != 0)
            {
                a.TargetType = (AmmoTarget)(int)(tt & ~HeliBit);
                writes++;
                st.Stripped++;
            }
            RememberStripped(a, unitId);
            if (splitNow) { st.Splits++; pw.Splits++; }
            return writes;
        }

        /// A copy already in the list (made by the game from a split unit): its values must still be the ones of this pair.
        static bool Refresh(Ammo c, Template t, float l)
        {
            bool changed = false;
            if (c.TargetType != AmmoTarget.Helicopter) { c.TargetType = AmmoTarget.Helicopter; changed = true; }
            if (c.HUDName != t.Hud) { c.HUDName = t.Hud; changed = true; }
            if (c.Name != t.Name) { c.Name = t.Name; changed = true; }
            float dh = t.SpreadH * l / 1000f, dv = t.SpreadV * l / 1000f, high = t.High > 0f ? Math.Min(t.High, l) : 0f;
            if (Math.Abs(c.GroundRange - l) > 0.01f || Math.Abs(c.LowAltRange - l) > 0.01f || Math.Abs(c.HighAltRange - high) > 0.01f
                || Math.Abs(c._dispersionReferenceRange - l) > 0.01f || c.DispersionMinimal != 0f
                || Math.Abs(c.DispersionHorizontalRadius - dh) > 0.001f || Math.Abs(c.DispersionVerticalRadius - dv) > 0.001f)
            {
                SetRanges(c, t, l);
                changed = true;
            }
            return changed;
        }

        static void RegisterCountUndo(CountDict dict, long srcKey, long copyKey)
        {
            var d = dict;
            Realism.JournalUndo(d.Pointer, "precision-quantite:" + copyKey, () =>
            {
                try
                {
                    if (!d.ContainsKey(copyKey)) return;
                    int v = d[copyKey];
                    d.Remove(copyKey);
                    if (d.ContainsKey(srcKey)) d[srcKey] = d[srcKey] + v;
                }
                catch { }
            });
        }

        static void RegisterListUndo(AmmoList list, long copyKey)
        {
            var l = list;
            Realism.JournalUndo(l.Pointer, "precision-liste:" + copyKey, () => { try { RemoveEntries(l, copyKey); } catch { } });
        }

        static void RegisterBitUndo(Ammo a)
        {
            var obj = a;
            Realism.JournalUndo(obj.Pointer, "precision-cible", () => { try { SetHeliBit(obj); } catch { } });
        }

        static void RemoveEntries(AmmoList list, long id)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e != null && e.Id == id) list.RemoveAt(i);
            }
        }

        static bool SetHeliBit(Ammo a)
        {
            long tt = (long)a.TargetType;
            if ((tt & HeliBit) != 0) return false;
            a.TargetType = (AmmoTarget)(int)(tt | HeliBit);
            return true;
        }

        static void Remember(Ammo c, Ammo a, int unit, float l)
        {
            lock (_regLock)
            {
                if (_created.Count >= MaxCreatedKept) _created.RemoveAt(0);
                _created.Add(new Created { Copy = c, Source = a, Unit = unit, L = l });
            }
        }

        static void RememberStripped(Ammo a, int unitId)
        {
            lock (_regLock)
            {
                if (_stripped.Count < MaxStripped && _strippedSet.Add(a.Pointer))
                {
                    _stripped.Add(a);
                    if (!_strippedByUnit.TryGetValue(unitId, out var l)) _strippedByUnit[unitId] = l = new List<Ammo>();
                    l.Add(a);
                }
            }
        }

        /// Remembers a quantity split in place so the battle guard can put the rounds back on the source key.
        static void RememberSplit(CountDict dict, AmmoList list, long srcKey, long copyKey)
        {
            lock (_regLock)
                if (_splits.Count < MaxStripped && _splitSet.Add((dict.Pointer, copyKey)))
                    _splits.Add(new SplitRef { Dict = dict, List = list, Src = srcKey, Copy = copyKey });
        }

        static void Error(State st, Exception e)
        {
            if (st != null) st.Errors++;
            int n = ++_errorsTotal;
            if (n <= 3) _errorQueue.Enqueue(e.GetBaseException().Message);
            if (n > MaxErrors) _dead = true;
        }

        static void FlushErrors()
        {
            while (_errorQueue.TryDequeue(out var m)) Warn("erreur dans les copies par unité : " + m);
            if (_dead && !_deadLogged)
            {
                _deadLogged = true;
                Warn("trop d'erreurs : plus aucune nouvelle copie jusqu'au redémarrage du jeu (les changements déjà faits restent journalisés)");
            }
        }

        static void Summary(State st, string when, bool force = false)
        {
            string line = $"lignes anti-heli ({when}) : copies posées {st.Copies}, quantités partagées {st.Splits}, lignes d'origine sans cible hélico {st.Stripped}, copies remises à jour {st.Refreshed} ; " +
                          $"sautées : table {st.SkipTable}, objet partagé entre unités {st.SkipShared}, clés de quantité inconnues {st.SkipKeys}, moins de {MinQuantity} munitions {st.SkipFew}, sans quantité {st.SkipNoCount} ; " +
                          $"copies sans quantité retirées {st.OrphansRemoved} ; erreurs {st.Errors}";
            if (!force && line == _lastSummary) return;
            _lastSummary = line;
            Log(line);
        }

        // ------------------------------------------------------------ restore check (CopiesUnites.ReverseSync, journal already undone)

        /// Removes leftovers from a weapon of a loaded unit after the restore: copy entries, copy count keys (merged back), a missing
        /// Helicopter bit on a listed source row. Returns the number of fixes (0 expected).
        internal static int Leftover(WeaponRow w)
        {
            var srcs = _lastSources;
            if (srcs == null || srcs.Count == 0 || w == null) return 0;
            int n = 0;
            try
            {
                var list = w.Ammunitions;
                if (list != null)
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var a = list[i];
                        if (a == null) continue;
                        int id = a.Id;
                        if (IsCopy(id)) { list.RemoveAt(i); n++; continue; }
                        if (srcs.Contains(id) && SetHeliBit(a)) n++;
                    }
                var d = w.WeaponAmmunitions;
                if (d != null)
                {
                    List<long> keys = null;
                    foreach (var kv in d) if (IsCopy(kv.Key)) (keys ??= new List<long>()).Add(kv.Key);
                    if (keys != null)
                        foreach (long k in keys)
                        {
                            int v = d[k];
                            d.Remove(k);
                            long s = k - IdOffset;
                            if (d.ContainsKey(s)) d[s] = d[s] + v;
                            n++;
                        }
                }
            }
            catch { }
            return n;
        }

        internal static void AfterRestore(int leftovers)
        {
            var srcs = _lastSources;
            if (srcs == null || srcs.Count == 0) return;
            int rowsLeft = 0;
            try
            {
                var rows = _lastSrc?.Ammunitions?._rows;
                if (rows != null)
                {
                    var keys = new List<int>();
                    foreach (var kv in rows) if (IsCopy(kv.Key)) keys.Add(kv.Key);
                    foreach (int k in keys) { rows.Remove(k); rowsLeft++; }
                }
            }
            catch { }
            lock (_regLock) { _created.Clear(); _stripped.Clear(); _strippedSet.Clear(); _strippedByUnit.Clear(); _splits.Clear(); _splitSet.Clear(); }
            string line = $"restauration : restes retirés des unités chargées {leftovers}, lignes restées dans la table {rowsLeft} (0 attendu)";
            if (leftovers + rowsLeft > 0) Warn(line); else Log(line);
            _lastSources = null;
            _lastSrc = null;
        }

        // ------------------------------------------------------------ deviation probe (menu, once per game launch)

        static bool BeginCall(string kind)
        {
            foreach (var r in (_callRefused.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (r == kind) return false;
            _callPending.Value = kind;
            MelonPreferences.Save();
            return true;
        }

        static void EndCall()
        {
            _callPending.Value = "";
            MelonPreferences.Save();
        }

        static bool TryNext(RandomComp rc)
        {
            try { float f = rc.NextFloat(0f, 1f); return !float.IsNaN(f); } catch { return false; }
        }

        /// Samples ShootingSystem.AddBallisticDeviation (the direct-fire deviation) and CalculateDeviationPolar with a RandomComponent of
        /// its own: normalised horizontal and vertical deviations, radial shape, axes, scaling with the distance and the reference range.
        static void ProbeShape(List<Spec> specs)
        {
            if (_probeDone) return;
            _probeDone = true;
            _probeUsable = false;
            if (!BeginCall("dispersion")) { _probeLine = "sonde de dispersion refusée (le jeu s'était arrêté pendant la sonde) : facteur de forme 1"; return; }
            try
            {
                RandomComp rc;
                try { rc = new RandomComp(); }
                catch (Exception e) { _probeLine = "sonde de dispersion impossible : générateur aléatoire du jeu non créé (" + e.GetBaseException().Message + ") : facteur de forme 1"; return; }
                string init = "sans initialisation";
                if (!TryNext(rc))
                {
                    try { rc.Init(20260917, null); init = "initialisé"; }
                    catch (Exception e) { init = "initialisation impossible (" + e.GetBaseException().Message + ")"; }
                    if (!TryNext(rc)) { _probeLine = $"sonde de dispersion impossible : tirage aléatoire du jeu illisible ({init}) : facteur de forme 1"; return; }
                }
                const float E = 1000f, MH = 20f, MV = 5f;
                int n = ProbeSamples, bad = 0;
                var hx = new double[n]; var vy = new double[n];
                for (int i = 0; i < n; i++)
                {
                    var v = Shooting.AddBallisticDeviation(new V3(0f, 0f, E), MV, MH, E, ref rc);
                    if (float.IsNaN(v.x) || float.IsNaN(v.y) || !(v.z > 0f)) { bad++; continue; }
                    hx[i] = Math.Atan2(v.x, v.z);
                    vy[i] = Math.Atan2(v.y, v.z);
                }
                if (bad > n / 20) { _probeLine = $"sonde de dispersion inutilisable : {bad}/{n} tirages invalides : facteur de forme 1"; return; }
                double mh = MeanAbs(hx), mv = MeanAbs(vy);
                // same reference range, target at half of it (angular spread: same angle), and reference range doubled (half the angle)
                double mhHalf = 0, mhRef2 = 0;
                int m = n / 4;
                for (int i = 0; i < m; i++)
                {
                    var a = Shooting.AddBallisticDeviation(new V3(0f, 0f, E / 2f), MV, MH, E, ref rc);
                    if (a.z > 0f) mhHalf += Math.Abs(Math.Atan2(a.x, a.z)) + Math.Abs(Math.Atan2(a.y, a.z));
                    var b = Shooting.AddBallisticDeviation(new V3(0f, 0f, E), MV, MH, 2f * E, ref rc);
                    if (b.z > 0f) mhRef2 += Math.Abs(Math.Atan2(b.x, b.z)) + Math.Abs(Math.Atan2(b.y, b.z));
                }
                double baseSum = (mh + mv) * m;
                double rHalf = baseSum > 0 ? mhHalf / baseSum : double.NaN, rRef2 = baseSum > 0 ? mhRef2 / baseSum : double.NaN;
                // axes: the horizontal parameter is 4 times the vertical one
                double axis = mv > 0 ? mh / mv : double.NaN;
                string axes;
                if (axis >= 2.5 && axis <= 6.5) { axes = "conformes"; _axesSwapped = false; }
                else if (axis > 0 && axis <= 0.4 && axis >= 0.15) { axes = "INVERSÉS (le rayon horizontal part à la verticale)"; _axesSwapped = true; }
                else axes = "incertains";
                var px = new float[n]; var py = new float[n];
                double maxR = 0, underHalf = 0;
                var deciles = new int[10];
                for (int i = 0; i < n; i++)
                {
                    // normalised on the radius of each parameter at the reference range
                    double sx = (_axesSwapped ? vy[i] : hx[i]) * E / MH, sy = (_axesSwapped ? hx[i] : vy[i]) * E / MV;
                    px[i] = (float)sx; py[i] = (float)sy;
                    double r = Math.Sqrt(sx * sx + sy * sy);
                    if (r > maxR) maxR = r;
                    if (r <= 0.5) underHalf++;
                    deciles[Math.Min(9, (int)(r * 10))]++;
                }
                // the polar sampler itself
                double pMax = 0, pUnder = 0;
                int pn = n / 2, pBad = 0;
                var pr = new double[pn];
                for (int i = 0; i < pn; i++)
                {
                    try
                    {
                        var t = Shooting.CalculateDeviationPolar(ref rc);
                        pr[i] = Math.Sqrt((double)t.Item1 * t.Item1 + (double)t.Item2 * t.Item2);
                        if (pr[i] > pMax) pMax = pr[i];
                    }
                    catch { pBad++; if (pBad > 10) break; }
                }
                if (pMax > 0) for (int i = 0; i < pn; i++) if (pr[i] <= 0.5 * pMax) pUnder++;
                bool angular = rHalf >= 0.8 && rHalf <= 1.25 && rRef2 >= 0.4 && rRef2 <= 0.625;
                bool scaleOk = maxR >= 0.7 && maxR <= 1.6;
                _probeUsable = angular && scaleOk && axes != "incertains";
                if (_probeUsable) { _px = px; _py = py; }
                var sb = new StringBuilder();
                sb.Append($"sonde de dispersion du jeu ({init}, {n} tirages de AddBallisticDeviation) : axes {axes} (rapport {D(axis)} pour 4), rayon normalisé max {D(maxR)}, ");
                sb.Append($"part sous la moitié du rayon {D(underHalf / n)} (rayon uniforme 0,5 ; disque uniforme 0,25), déciles {string.Join("/", deciles)} ; ");
                sb.Append($"échelle : cible à mi-portée x{D(rHalf)} (1 si angulaire), portée de référence doublée x{D(rRef2)} (0,5 attendu) ; ");
                sb.Append($"CalculateDeviationPolar : rayon max {D(pMax)}, part sous la moitié {(pn > 0 ? D(pUnder / pn) : "?")}{(pBad > 0 ? $", erreurs {pBad}" : "")} ; ");
                sb.Append(_probeUsable ? "forme mesurée utilisée pour les facteurs de forme" : "forme NON utilisée (échelle ou axes inattendus) : facteur de forme 1");
                if (_probeUsable)
                {
                    var done = new HashSet<(float, float, float)>();
                    foreach (var sp in specs)
                    {
                        if (!done.Add((sp.EH, sp.EV, sp.Cal))) continue;
                        float k = ShapeFactor(sp);
                        double target = TriModel(sp.EH, sp.EV, sp.Cal, RefWidth);
                        sb.Append($" ; {ClassCodes[sp.Class]} {F(sp.EH)}/{F(sp.EV)} mrad à {sp.Cal:0} m : taux prévu {Pct(target)} -> facteur {k.ToString("0.##", Inv)} (de face {Pct(Empirical(sp.EH * k, sp.EV * k, sp.Cal, HeadOnWidth))})");
                    }
                }
                _probeLine = sb.ToString();
            }
            finally { EndCall(); }
        }

        static double MeanAbs(double[] a) { double s = 0; foreach (var v in a) s += Math.Abs(v); return a.Length > 0 ? s / a.Length : 0; }

        /// Per-axis triangular model (the shape the class values were fitted with): hit probability on a W x RefHeight box at distance d.
        static double TriModel(float eh, float ev, float d, float width)
        {
            static double Tri(double w, double r) => w / 2 >= r ? 1 : (w / r) * (1 - w / (4 * r));
            return Tri(width, eh * d / 1000.0) * Tri(RefHeight, ev * d / 1000.0);
        }

        /// Hit probability with the measured deviation samples.
        static double Empirical(float eh, float ev, float d, float width)
        {
            var px = _px; var py = _py;
            if (px == null || py == null) return TriModel(eh, ev, d, width);
            double rh = eh * d / 1000.0, rv = ev * d / 1000.0, hw = width / 2.0, hh = RefHeight / 2.0;
            int hit = 0;
            for (int i = 0; i < px.Length; i++) if (Math.Abs(px[i] * rh) <= hw && Math.Abs(py[i] * rv) <= hh) hit++;
            return (double)hit / px.Length;
        }

        /// Multiplier of the spreads that gives, with the measured deviation shape, the hit rate the class values give with the per-axis
        /// triangular shape at the calibration distance (average aspect). 1 when the probe is not usable; limited to 1/3..3.
        static float ShapeFactor(Spec sp)
        {
            if (!_probeUsable || _px == null) return 1f;
            double target = TriModel(sp.EH, sp.EV, sp.Cal, RefWidth);
            if (!(target > 0 && target < 1)) return 1f;
            double lo = Math.Log(1.0 / 3.0), hi = Math.Log(3.0);
            if (Empirical((float)(sp.EH * Math.Exp(lo)), (float)(sp.EV * Math.Exp(lo)), sp.Cal, RefWidth) < target) return (float)Math.Exp(lo);
            if (Empirical((float)(sp.EH * Math.Exp(hi)), (float)(sp.EV * Math.Exp(hi)), sp.Cal, RefWidth) > target) return (float)Math.Exp(hi);
            for (int i = 0; i < 30; i++)
            {
                double mid = (lo + hi) / 2, k = Math.Exp(mid);
                if (Empirical((float)(sp.EH * k), (float)(sp.EV * k), sp.Cal, RefWidth) > target) lo = mid; else hi = mid;
            }
            return (float)Math.Exp((lo + hi) / 2);
        }

        // ------------------------------------------------------------ battle measurement (main thread)

        static void Tick(float now, State st, GameController gc)
        {
            Snapshot(now, st);
            if (_carriers[0].Count + _carriers[1].Count > 0 && _firstCarrier < 0f) _firstCarrier = now;
            if (!_staticDone && ((_firstCarrier >= 0f && now - _firstCarrier >= 20f) || now - _battleStart >= 120f)) { _staticDone = true; Static(st); }
            GetAmmoDel del = null;
            try { del = gc._GetEcsEventBus_k__BackingField?.Gameplay?.GetUnitsAmmo; } catch { del = null; }
            if (del == null) return;
            int reads = 0;
            if (_g2State == 0 && _firstCarrier >= 0f && now - _firstCarrier >= 30f) reads += LoadoutProof(now, st, del);
            reads += CountShots(now, st, del, MaxReadsPerTick - reads);
            FireProof(now, st);
        }

        static void Snapshot(float now, State st)
        {
            _map ??= new LuaMap();
            var helis = new Dictionary<int, Heli>();
            var carriers = new[] { new List<Carrier>(), new List<Carrier>() };
            var src = st.Src;
            for (int side = 0; side < 2; side++)
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LuaUnit> arr = null;
                try { arr = _map.GetUnits(V3.zero, 1_000_000f, side, -1); } catch { arr = null; }
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = arr[i];
                        if (u == null || !u.IsAlive()) continue;
                        int uid = u.UID;
                        int unitId = UnitIdOf(u, uid);
                        if (unitId <= 0) continue;
                        int type = TypeOf(src, unitId);
                        if ((type & TypeHeli) != 0)
                        {
                            var pos = u.GetPosition();
                            var h = new Heli { Eid = u.Entity.EntityId, Side = side, Pos = pos };
                            if (_heliPrev.TryGetValue(uid, out var prev) && now - prev.t > 0.5f && now - prev.t < 10f)
                                h.Vel = (pos - prev.pos) / (now - prev.t);
                            _heliPrev[uid] = (pos, now);
                            h.Air = true;
                            try { AltLevel lvl = default; Aircraft.GetCurrentAltitude(u.Entity, out lvl); h.Air = lvl != AltLevel.Ground; } catch { }
                            helis[h.Eid] = h;
                        }
                        else if ((type & TypePlane) == 0 && type != 0 && st.Groups.ContainsKey(unitId))
                            carriers[side].Add(new Carrier { Uid = uid, Side = side, UnitId = unitId, Pos = u.GetPosition() });
                    }
                    catch { }
                }
            }
            _heliByEid = helis;
            _carriers = carriers;
            if (_heliPrev.Count > 2000) _heliPrev.Clear();
            if (_unitIdByUid.Count > 20000) _unitIdByUid.Clear();
        }

        static int UnitIdOf(LuaUnit u, int uid)
        {
            if (_unitIdByUid.TryGetValue(uid, out int id)) return id;
            try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { id = 0; }
            if (id > 0) _unitIdByUid[uid] = id;
            return id;
        }

        static int TypeOf(DbSource src, int unitId)
        {
            if (_typeByUnitId.TryGetValue(unitId, out int t)) return t;
            try { t = src.Units.TryGetById(unitId, out UnitRow row) && row != null ? (int)row.Type : 0; } catch { t = 0; }
            if (t != 0) _typeByUnitId[unitId] = t;
            return t;
        }

        static AmmoFilterData Filter(string hud)
        {
            if (hud == null) { if (_anyFilter == null) { _anyFilter = new AmmoFilterData(); _anyFilter.Type = NodeAmmoType.Any; _anyFilter.SpecificAmmoNameFilter = ""; } return _anyFilter; }
            if (_filters.TryGetValue(hud, out var f)) return f;
            f = new AmmoFilterData();
            f.Type = NodeAmmoType.Any;
            f.SpecificAmmoNameFilter = hud;
            _filters[hud] = f;
            return f;
        }

        static int Read(GetAmmoDel del, int uid, string hud)
        {
            try
            {
                _il2One ??= new Il2CppSystem.Collections.Generic.List<int>();
                _il2OneRo ??= new Il2CppSystem.Collections.Generic.IReadOnlyList<int>(_il2One.Pointer);
                _il2One.Clear();
                _il2One.Add(uid);
                var d = del.Invoke(_il2OneRo, Filter(hud));
                _reads++;
                return d.InfiniteAmmo ? -2 : d.AmmoCount;
            }
            catch { _readErrors++; return -1; }
        }

        static float Dist(V3 a, V3 b) { double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z; return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }

        static int Band(float d) => d < 0f ? NBands - 1 : Math.Min(NBands - 1, (int)(d / 250f));

        static string BandName(int b) => b >= NBands - 1 ? $"plus de {(NBands - 1) * 250} m" : $"{b * 250}-{(b + 1) * 250} m";

        /// 0 head-on or tail (within 30 degrees), 1 oblique, 2 broadside (60-90 degrees), 3 hovering or slow (below 5 m/s).
        static int Aspect(Heli h, V3 shooter)
        {
            double vx = h.Vel.x, vz = h.Vel.z, sp = Math.Sqrt(vx * vx + vz * vz);
            if (sp < 5.0) return 3;
            double dx = shooter.x - h.Pos.x, dz = shooter.z - h.Pos.z, dl = Math.Sqrt(dx * dx + dz * dz);
            if (dl < 1.0) return 3;
            double c = Math.Abs((vx * dx + vz * dz) / (sp * dl));
            return c >= 0.866 ? 0 : c <= 0.5 ? 2 : 1;
        }

        /// Copy rounds fired: decreases of the copy count of carriers near an airborne enemy helicopter, by class, band and aspect.
        static int CountShots(float now, State st, GetAmmoDel del, int budget)
        {
            if (budget <= 0) return 0;
            var cands = new List<(Carrier c, int gi, Group g, Heli h, float d)>();
            for (int side = 0; side < 2; side++)
            {
                var airList = new List<Heli>();
                foreach (var h in _heliByEid.Values) if (h.Side == 1 - side && h.Air) airList.Add(h);
                if (airList.Count == 0) continue;
                foreach (var c in _carriers[side])
                {
                    if (!st.Groups.TryGetValue(c.UnitId, out var groups)) continue;
                    Heli best = null;
                    float bd = float.MaxValue;
                    foreach (var h in airList) { float d = Dist(c.Pos, h.Pos); if (d < bd) { bd = d; best = h; } }
                    if (best == null) continue;
                    for (int gi = 0; gi < groups.Length; gi++)
                    {
                        var g = groups[gi];
                        if (bd > g.L + 300f) continue;
                        cands.Add((c, gi, g, best, bd));
                        if (bd <= Math.Min(0.8f * g.L, ExposureRange) && g.Class >= 0 && _carrierPositive.Contains(c.Uid)) { _exposure[g.Class] += TickEvery; _exposedCarriers.Add(c.Uid); }
                    }
                }
            }
            if (cands.Count == 0) return 0;
            int reads = 0, start = _readRound % cands.Count, done = 0;
            for (int k = 0; k < cands.Count && reads < budget; k++)
            {
                var (c, gi, g, h, d) = cands[(start + k) % cands.Count];
                done++;
                int count = Read(del, c.Uid, g.Hud);
                reads++;
                if (count < 0) continue;
                long key = ((long)c.Uid << 8) | (uint)gi;
                if (count > 0) { _carrierPositive.Add(c.Uid); _groupPositive.Add(key); }
                // an empty copy pool with the source row stripped leaves this carrier silent against helicopters: give that row its target back.
                // Only a group whose own count was read above zero can be judged empty: a group that never carried a copy always reads 0.
                if (count == 0 && d <= g.L && _groupPositive.Contains(key) && !_dryRepaired.Contains(c.UnitId))
                {
                    _dryCarriers++;
                    float gap = _lastCount.TryGetValue(key, out var prev) ? Math.Min(now - prev.t, MaxReadGap) : 0f;
                    _dryTime.TryGetValue(c.UnitId, out float dt);
                    dt += gap;
                    _dryTime[c.UnitId] = dt;
                    if (dt >= DryNeeded) RepairUnit(c.UnitId, g.Hud);
                }
                if (_lastCount.TryGetValue(key, out var last) && count < last.count)
                {
                    int shots = last.count - count;
                    if (now - last.t > MaxReadGap) _gapShots += shots;
                    else if (g.Class < 0 || g.Ambiguous) _ambiguousShots += shots;
                    else
                    {
                        var a = _agg[g.Class];
                        a.Shots[Band(d)] += shots;
                        a.AspectShots[Aspect(h, c.Pos)] += shots;
                        if (d > g.L + 150f) a.OutOfRange += shots;
                        _copyShotsTotal += shots;
                    }
                }
                _lastCount[key] = (count, now);
            }
            _readRound += done;
            if (_lastCount.Count > 20000) _lastCount.Clear();
            return reads;
        }

        /// Hit on a helicopter by a copy row (AntiHeliTouches, main thread): nearest enemy carrier of that copy, by class, band and aspect.
        internal static void NoteHit(int ammoId, int victimEntityId)
        {
            if (!IsCopy(ammoId)) return;
            var st = _state;
            if (st == null) return;
            try
            {
                int src = ammoId - IdOffset;
                if (!st.BySource.TryGetValue(src, out var t) || !_heliByEid.TryGetValue(victimEntityId, out var h) || h.Side < 0 || h.Side > 1) { _hitsUnplaced++; return; }
                Carrier best = null;
                float bd = float.MaxValue;
                foreach (var c in _carriers[1 - h.Side])
                {
                    if (!HasCopy(c.UnitId, src)) continue;
                    float d = Dist(c.Pos, h.Pos);
                    if (d < bd) { bd = d; best = c; }
                }
                if (best == null) { _hitsUnplaced++; return; }
                var a = _agg[t.Spec.Class];
                a.Hits[Band(bd)]++;
                a.AspectHits[Aspect(h, best.Pos)]++;
                _hitsTotal++;
            }
            catch { _hitsUnplaced++; }
        }

        /// Loadout proof, 30 s after the first carrier: copy count, source count and total of a few carriers of different types.
        static int LoadoutProof(float now, State st, GetAmmoDel del)
        {
            int reads = 0, sampled = 0;
            foreach (var list in _carriers)
                foreach (var c in list)
                {
                    if (sampled >= 2 || _sampledUids.Contains(c.Uid)) continue;
                    if (_sampledTypes.Contains(c.UnitId) && _sampledTypes.Count < 3) continue;
                    if (!st.Groups.TryGetValue(c.UnitId, out var groups) || groups.Length == 0) continue;
                    var g = groups[0];
                    _sampledUids.Add(c.Uid);
                    _sampledTypes.Add(c.UnitId);
                    sampled++;
                    var s = new Sample { Uid = c.Uid, UnitId = c.UnitId, Hud = g.Hud };
                    s.C = Read(del, c.Uid, g.Hud);
                    s.S = g.SourceHud != null ? Read(del, c.Uid, g.SourceHud) : -1;
                    s.T = Read(del, c.Uid, null);
                    reads += g.SourceHud != null ? 3 : 2;
                    _samples.Add(s);
                    if (s.C > 0) _carrierPositive.Add(c.Uid);
                }
            bool timeUp = now - _firstCarrier >= 240f;
            if (_samples.Count >= 6 || (timeUp && _samples.Count >= 3) || (timeUp && now - _firstCarrier >= 600f))
            {
                string detail = string.Join(", ", _samples.Select(x => $"U{x.UnitId} '{x.Hud}' {x.C}/{x.S}/{x.T}"));
                int positive = _samples.Count(x => x.C > 0);
                int validZero = _samples.Count(x => x.C == 0 && x.S > 0);
                int types = _samples.Where(x => x.C == 0 && x.S > 0).Select(x => x.UnitId).Distinct().Count();
                if (positive > 0)
                {
                    _g2State = 1;
                    Log($"preuve de chargement : lignes anti-heli présentes dans les munitions de {positive}/{_samples.Count} unité(s) (copie/origine/total : {detail})");
                    if (_trips.Value != 0) { _trips.Value = 0; MelonPreferences.Save(); }
                }
                else if (_samples.Count >= 4 && validZero == _samples.Count && types >= 2)
                {
                    // per battle only: the reads go through a name filter, so this is strong evidence but not a proof
                    _g2State = -1;
                    Repair($"lignes anti-heli absentes des munitions de {_samples.Count} unité(s) alors que leur ligne d'origine se lit (copie/origine/total : {detail})");
                    _pendingNotify = (int)TxtKey.N_PRECISION_NOT_LOADED;
                }
                else
                {
                    _g2State = 2;
                    Log($"preuve de chargement non concluante (copie/origine/total : {(detail.Length > 0 ? detail : "aucune unité lue")})");
                }
            }
            return reads;
        }

        /// Carriers holding copies, exposed to airborne enemy helicopters well inside the copy range for a long time, never fired one round.
        static void FireProof(float now, State st)
        {
            if (_g3Done || _battleRepair || now - _battleStart < 180f) return;
            float exposure = 0f;
            for (int i = 0; i < Classes; i++) exposure += _exposure[i];
            // dry carriers are handled per unit type by RepairUnit: only a type that could not be repaired justifies the global repair
            if (exposure < ExposureNeeded || _exposedCarriers.Count < 4 || (_copyShotsTotal > 0 && (_dryCarriers == 0 || _dryRepaired.Count > 0))) return;
            _g3Done = true;
            string why = _copyShotsTotal > 0
                ? $"munitions anti-heli épuisées sur {_dryCarriers} lecture(s) de porteur alors que les lignes d'origine en gardent, malgré {exposure:0} s unité x temps d'hélicos ennemis bien à portée ({_exposedCarriers.Count} unités qui les portent)"
                : $"aucun tir des lignes anti-heli malgré {exposure:0} s unité x temps d'hélicos ennemis bien à portée ({_exposedCarriers.Count} unités qui les portent)";
            Repair(why);
        }

        /// Copies of one unit type run dry while its source rows keep rounds: that type takes helicopters back (its copies stay, the range caps still apply).
        static void RepairUnit(int unitId, string hud)
        {
            _dryRepaired.Add(unitId);
            List<Ammo> items = null;
            lock (_regLock) if (_strippedByUnit.TryGetValue(unitId, out var l)) items = new List<Ammo>(l);
            int n = 0;
            if (items != null) foreach (var a in items) { try { if (SetHeliBit(a)) n++; } catch { } }
            Warn($"garde-fou : munitions anti-heli '{hud}' épuisées sur l'unité {unitId} alors que la ligne d'origine en garde : cible hélicoptère remise sur {n} ligne(s) d'origine de cette unité pour cette bataille");
        }

        /// Puts the Helicopter bit back on every stripped source row, gives the split rounds back to the source rows and stops new copies for the rest of the battle.
        static void Repair(string reason)
        {
            _battleRepair = true;
            int n = 0;
            List<Ammo> items;
            lock (_regLock) items = new List<Ammo>(_stripped);
            foreach (var a in items) { try { if (SetHeliBit(a)) n++; } catch { } }
            List<SplitRef> pairs;
            lock (_regLock) pairs = new List<SplitRef>(_splits);
            int m = 0;
            foreach (var s in pairs)
            {
                try
                {
                    RemoveEntries(s.List, s.Copy);              // list entry first: never a listed row without its count
                    if (!s.Dict.ContainsKey(s.Copy)) continue;
                    int v = s.Dict[s.Copy];
                    s.Dict.Remove(s.Copy);
                    if (s.Dict.ContainsKey(s.Src)) { s.Dict[s.Src] = s.Dict[s.Src] + v; m++; }
                }
                catch { }
            }
            Warn($"garde-fou : {reason} : cible hélicoptère remise sur {n} ligne(s) d'origine pour cette bataille (portées limites seulement), quantités rendues à {m} ligne(s)");
        }

        /// Once per battle: live copy values (dispersion reference cache included), the engine estimate for the templates, battle settings.
        static void Static(State st)
        {
            try
            {
                var bs = GameConfig.Instance?.BattleSystemSettings;
                if (bs != null)
                    Log($"réglages de tir du jeu : imprécision de visée max {F(bs.MAX_WEAPON_AIMING_IMPRECISION)}, seuil de contrôle de visée {F(bs.MAX_WEAPON_AIMING_CHECK_TRESHOLD)}, " +
                        $"dispersion de point mini {F(bs.MIN_POINT_DISPERSION)}, multiplicateur max {F(bs.MAX_POINT_DISPERSION_MULTIPLIER)}");
            }
            catch (Exception e) { Log("réglages de tir du jeu illisibles (" + e.GetBaseException().Message + ")"); }

            List<Created> items;
            lock (_regLock) items = _created.Skip(Math.Max(0, _created.Count - 6)).ToList();
            if (items.Count > 0)
            {
                var parts = new List<string>();
                foreach (var it in items)
                {
                    try
                    {
                        var c = it.Copy; var s = it.Source;
                        float f0 = c._dispersionReferenceRange, r = c.GetDispersionReferenceRange();
                        parts.Add($"U{it.Unit} copie {c.Id} '{c.HUDName}' : référence champ {F(f0)} / lue {F(r)} m, portées {F(c.GroundRange)}/{F(c.LowAltRange)}, dispersion {F(c.DispersionHorizontalRadius)}/{F(c.DispersionVerticalRadius)} (mini {F(c.DispersionMinimal)}), fusée {F(c.RadioFuseDistance)}, cibles {c.TargetType} ; " +
                                  $"origine {s.Id} : cibles {s.TargetType}, référence champ {F(s._dispersionReferenceRange)} m, portées {F(s.GroundRange)}/{F(s.LowAltRange)}, dispersion {F(s.DispersionHorizontalRadius)}/{F(s.DispersionVerticalRadius)}");
                    }
                    catch (Exception e) { parts.Add("copie illisible (" + e.GetBaseException().Message + ")"); }
                }
                Log("copies vues en bataille : " + string.Join(" | ", parts));
            }
            else Log("copies vues en bataille : aucune copie faite depuis l'application");

            var src = st.Src;
            var tpls = EstimateAmmo.Select(id => st.BySource.TryGetValue(id, out var t) ? t : null).Where(t => t != null).ToList();
            if (tpls.Count == 0 || !BeginCall("estimation")) return;
            try
            {
                var units = new List<UnitRow>();
                foreach (int id in EstimateUnits) { try { if (src.Units.TryGetById(id, out UnitRow u) && u != null) units.Add(u); } catch { } }
                foreach (var t in tpls)
                {
                    var sb = new StringBuilder($"estimation du jeu (GetTargetAccuracy) pour la ligne {t.CopyId} '{t.Hud}' ({F(t.SpreadH)}/{F(t.SpreadV)} mrad, portée {F(t.L)} m) :");
                    foreach (var u in units)
                    {
                        sb.Append($" | {u.Name} ({F(u.Width)} x {F(u.Height)} m)");
                        for (float d = 125f; d <= Math.Min(t.L + 125f, 2875f); d += 250f)
                        {
                            float v = float.NaN;
                            try { v = BSH.GetTargetAccuracy(t.Copy, u, d); } catch { }
                            sb.Append($" {d:0} m {(float.IsNaN(v) ? "?" : v.ToString("0.###", Inv))}");
                        }
                    }
                    Log(sb.ToString());
                }
            }
            finally { EndCall(); }
        }

        static void Report(bool final)
        {
            var st = _state;
            if (!final && _copyShotsTotal + _hitsTotal + _ambiguousShots == 0) return;
            var sb = new StringBuilder($"{(final ? "bilan" : "relevé")} : lectures {_reads} (erreurs {_readErrors}), tirs de copies comptés {_copyShotsTotal}, touches {_hitsTotal} (non placées {_hitsUnplaced}), " +
                                       $"tirs entre lectures trop espacées {_gapShots}, tirs non classés (noms ambigus) {_ambiguousShots}, " +
                                       $"porteurs à sec {_dryCarriers} (types rendus à la ligne d'origine {_dryRepaired.Count}), preuve de chargement {(_g2State == 1 ? "oui" : _g2State == -1 ? "ÉCHEC" : _g2State == 2 ? "non concluante" : "en cours")}" +
                                       $"{(_battleRepair ? ", garde-fou déclenché" : "")}");
            for (int c = 0; c < Classes; c++)
            {
                var a = _agg[c];
                long shots = a.Shots.Sum(), hits = a.Hits.Sum();
                if (shots + hits == 0) continue;
                sb.Append($" ; {ClassLabels[c]} : tirs {shots} touches {hits} ({(shots > 0 ? Pct((double)hits / shots) : "-")})");
                var bands = new List<string>();
                for (int b = 0; b < NBands; b++) if (a.Shots[b] + a.Hits[b] > 0) bands.Add($"{BandName(b)} {a.Hits[b]}/{a.Shots[b]}");
                sb.Append(" [" + string.Join(", ", bands) + "]");
                var asp = new List<string>();
                for (int k = 0; k < Aspects; k++) if (a.AspectShots[k] + a.AspectHits[k] > 0) asp.Add($"{AspectLabels[k]} {a.AspectHits[k]}/{a.AspectShots[k]}");
                if (asp.Count > 0) sb.Append(" [" + string.Join(", ", asp) + "]");
                if (a.OutOfRange > 0) sb.Append($" hors portée {a.OutOfRange}");
                if (final && st != null)
                {
                    var t = st.BySource.Values.FirstOrDefault(x => x.Spec.Class == c);
                    if (t != null)
                        sb.Append($" ; modèle à {t.Spec.Cal:0} m : de face {Pct(Empirical(t.SpreadH, t.SpreadV, t.Spec.Cal, HeadOnWidth))}, profil moyen {Pct(Empirical(t.SpreadH, t.SpreadV, t.Spec.Cal, RefWidth))}");
                }
            }
            string line = sb.ToString();
            if (!final && line == _lastReport) return;
            _lastReport = line;
            Log(line);
        }

        static string F(float v) => float.IsNaN(v) ? "?" : v.ToString("0.##", Inv);
        static string D(double v) => double.IsNaN(v) ? "?" : v.ToString("0.###", Inv);
        static string Pct(double p) => double.IsNaN(p) ? "?" : (100.0 * p).ToString("0.##", Inv) + " %";
    }
}
