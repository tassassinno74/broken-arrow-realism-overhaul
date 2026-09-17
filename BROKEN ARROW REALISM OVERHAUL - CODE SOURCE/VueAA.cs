// RealismOverhaul - anti-air line of sight towards helicopters (design R3, v0.23.0), identical for both sides.
//  Player request 2026-09-16: MANPADS and short-range infrared SAMs may fire at a helicopter only when they see it themselves (trees,
//  buildings, relief and a wall next to the shooter block it; a shooter high in a building sees farther). This module only computes the
//  verdicts; AntiHeliPortee applies them to INFANTRY teams holding infrared MANPADS-class missiles (range 0 on a masked pair) once the map
//  self-test, the copy check and the building calibration passed in this battle (ReadyForGating); vehicle pairs are measured only.
//  v0.22.8 measured 97-99 % of pairs masked, buildings first: the model is corrected here. v0.23.0 measured 98-100 % masked at 1-5 km
//  on RU_C01, forest first: audited against that log, this is the geometry of the rule (25 m trees over 26.5 % of the map, helicopters
//  32-40 m above the ground, eye 1.8 m: the line stays under the treetops for the first 59-74 % of its length), not a model error.
//  Map: GameController.MapMetaData._pixels, self-tested on 20 points against the engine (ConvertPosition + GetTerrainAtHeight, the path
//  proven in Obstacles.cs), copied into managed arrays a few lines per frame, then calibrated once: each sampled building pixel is compared
//  with the nearest ground around it (up to 5000 pixels, rings of 15 m). Median 3 m or more: roofs are already in the height map (building
//  top = pixel height); below 1 m: flat footprints (top = pixel height + the engine's own building observation height, without the mod's
//  multiplier); in between: ambiguous map, verdicts are measured but never used for the rule.
//  Line (values): eye of infantry 1.8 m / vehicle 3 m above the ground under it (and above its real position when that is
//  higher), helicopter position + 1 m; every map pixel the line crosses is visited (grid walk; 48 m blocks are skipped when the line
//  passes above their highest obstacle). Masked when the line passes below a building top, below the ground, or crosses more than 45 m of
//  forest below ground + 25 m. The shooter's own pixel and the helicopter's pixel are ignored: a team next to a wall is masked only when
//  the wall is between. Infantry inside a building (next to building pixels and 2.5 m above the ground, or standing on a building pixel
//  of an occupied building whose shoot position is within 8 m) looks from its building top + 1.8 m and ignores its own building pixels,
//  so it sees farther. A team standing in the street next to an occupied building keeps a ground-level eye: that building stays an
//  obstacle.
//  Pairs with helicopters from 5 m up are evaluated, so AntiHeliPortee can let an infantry team engage a low helicopter within 1 km
//  only on a clear verdict.
//  Pairs: shooters holding an infrared MANPADS-class missile (AntiHeliPortee snapshot) and enemy helicopters at least 5 m above the
//  ground within that missile's range + 150 m (9 km at most); when that snapshot is missing, the Reperage anti-air scan at 6 km (measurement).
//  Infantry pairs are queued first (they drive the rule; vehicle pairs are the first dropped at the pair cap). The line starts at the
//  position the cached eye was computed at (at most 3 m from the unit), so the ignored shooter pixel, its ground and its building match.
//  Every 0.5 s, 2 ms per frame at most; verdicts are published as immutable sets valid 3 s. Every 60 s, 300 building pixels are compared
//  with the engine's map (destroyed buildings) and the map is copied again when it changed. No hook, solo campaign only, crash guard,
//  error kill-switch. Logs: [VUE-AA].
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using MapMeta = Il2CppBrokenArrow.Client.Ecs.Navigation.MapMetaData;
using MapPixel = Il2CppBrokenArrow.Client.Ecs.Navigation.MapPixel;
using NavConst = Il2CppBrokenArrow.Client.Ecs.Navigation.NavigationConstants;
using Terrain = Il2CppBrokenArrow.Shared.Ecs.Enums.TerrainType;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class VueAA
    {
        const string GuardVersion = "0.24.0";
        const float CycleSeconds = 0.5f, FallbackRange = 6000f, MaxRangeCap = 9000f, RangeMargin = 150f, ValidMs = 3000f;
        const float EyeInfantry = 1.8f, EyeVehicle = 3f, HeliAim = 1f, MinAirborne = 5f;
        const float TreeHeight = 25f, ForestMax = 45f;
        const float ElevatedGap = 2.5f, GarrisonRadius = 20f, EyeMove = 3f, EyeMaxAge = 10f, EyeMaxLift = 15f;
        const float GarrisonOwnRadius = 8f;                         // an occupied building holds this team only when its shoot position is this close
        const float RoofMin = 3f, FlatMax = 1f, DefaultBuildingHeight = 8f, RecheckEvery = 60f;
        const double BudgetMs = 2.0;
        const int TestPoints = 20, TestPass = 18, MaxTestTries = 3, MaxMapTries = 240, MaxErrors = 50, MaxPairs = 6000, Bands = 10;
        const int RingMax = 5, OwnRadiusPx = 9, OwnMaxPx = 400, BlockShift = 4, CalSamples = 5000, RecheckSamples = 300, MinCalSamples = 50;
        const int MaxGarrisonCallsPerCycle = 20, FullCauseEvery = 10;
        const int Clear = 0, CauseForest = 1, CauseRelief = 2, CauseBuilding = 3;
        static readonly string[] CauseNames = { "dégagée", "forêt", "relief", "bâtiment" };
        static readonly float[] OffsetBins = { 0.5f, 1f, 2f, 3f, 5f, 8f, 12f, 20f };
        static readonly float[] GapBins = { -1f, 1f, 2.5f, 6f };

        /// Pairs whose line of sight was masked in the last completed cycle. Key: ((long)shooterEntityId << 32) | (uint)targetEntityId.
        /// Immutable: replaced as a whole, never modified after publication (read by the AntiHeliPortee hook, maybe off the main thread).
        internal static volatile HashSet<long> Masked = new();
        /// Every pair evaluated in the last completed cycle (masked or not), same key and same rules as Masked.
        internal static volatile HashSet<long> Evaluated = new();
        static long _validUntil;                                    // Environment.TickCount64 limit of the published sets
        /// The published sets belong to a cycle finished less than 3 s ago.
        internal static bool MaskedFresh => Environment.TickCount64 <= Interlocked.Read(ref _validUntil);
        /// Map self-test, copy and building calibration passed in this battle: the verdicts may drive the rule.
        internal static volatile bool ReadyForGating;
        /// Why the verdicts may not drive the rule (French, for the log).
        internal static volatile string GateReason = "carte pas encore lue";
        /// The verdicts will not drive the rule again in this battle (map self-test or copy failed, map never ready, ambiguous buildings,
        /// error kill-switch, crash guard or calculation switched off). False while the map is only being prepared.
        internal static volatile bool GateFailed;

        internal static long Key(int shooterEntityId, int targetEntityId) => ((long)shooterEntityId << 32) | (uint)targetEntityId;

        enum Stage { Waiting, Copying, Calibrating, Ready, Failed }
        enum BuildMode { Unknown, Roof, Flat, Ambiguous }

        /// Eye of one shooter, cached while it moves less than 3 m (at most 10 s).
        sealed class Eye
        {
            public V3 Pos;
            public float Time, Y, Gap, Top;
            public int K0;
            public bool Infantry, OnBuilding, NearBuilding, Inside, Garrison;
            public HashSet<int> Own;                                // pixels of its own building (inside only)
        }

        struct Pair
        {
            public long Key;
            public float Ex, Ey, Ez, Tx, Ty, Tz, Dist;
            public int K0, Kt;
            public bool Infantry;
            public Eye Eye;
        }

        struct AirTarget { public int Eid, Side, Kt; public V3 Pos; }

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        static bool _refused, _everOnline, _sessionArmed, _constantsLogged, _onlineLogged, _garrisonBroken, _recopy;
        static float _nextCheck, _nextReport, _nextTest, _battleSeen, _nextCycle, _nextRecheck;
        static int _errors;
        static IntPtr _gcPtr, _mapPtr;
        static Stage _stage;
        static BuildMode _mode;
        static string _testResult = "en attente", _calibResult = "pas encore faite";
        static int _mapTries, _testTries;
        static LuaMap _map;

        // map copy (main thread only)
        static Il2CppStructArray<MapPixel> _pix;
        static int _w, _h, _stride, _copyLine, _copyFrames, _bridgeMask, _bw, _bh;
        static bool _orderXFirst;                                   // true: index = x * stride + y; false: index = y * stride + x
        static float _ox, _oz, _invDx, _invDz, _roundX, _roundZ, _minGround, _maxGround;
        static float[] _height;                                     // [y * _w + x] pixel height as the engine stores it
        static byte[] _terrain;                                     // [y * _w + x] base terrain type (bridge flags removed)
        static byte _forestVal, _buildingVal, _waterVal, _lowWaterVal;
        static long _forestPixels, _buildingPixels;
        static double _copyMs;
        static readonly int[] _tpX = new int[TestPoints], _tpY = new int[TestPoints];
        static readonly float[] _tpH = new float[TestPoints];
        static readonly byte[] _tpT = new byte[TestPoints];
        static readonly Stopwatch _sw = new();

        // building calibration and obstacle blocks (main thread only)
        static int _calLine, _calStep, _calCounter, _calSampleCount, _calNoGround, _recheckStep, _recheckCounter, _recheckCount;
        static float[] _calOffsets, _blockNb, _blockB, _blockMax;
        static int[] _recheckIdx;
        static float _buildingAdd, _roofOffset, _median, _flatHeight, _maxObstacle, _fowBuildingHeight, _fowMultiplier;
        static readonly long[] _offsetHist = new long[9];
        static double _calMs;

        // cycle state (main thread only)
        static readonly List<Pair> _pairs = new();
        static readonly List<AirTarget> _air = new();
        static readonly Dictionary<int, Eye> _eyes = new();
        static int _pairIdx, _garrisonCallsThisCycle;
        static bool _cycleRunning;
        static float _cycleStart;
        static HashSet<long> _cycleMasked, _cycleEval;

        // statistics (main thread only)
        static long _cycles, _evaluated, _skippedPairs, _clearWithForest, _ownScans, _losCycles, _fallbackCycles, _evalInfantry, _maskedInfantry;
        static readonly long[] _byCause = new long[4];
        static readonly long[] _bandEval = new long[Bands], _bandMasked = new long[Bands];
        static long _fullN, _fullForest, _fullBuilding, _fullBuildingForest, _fullRelief, _fullReliefOther;
        static double _marchMaxMs, _frameMaxMs, _cycleMaxS;
        static long _heliSamples, _heliLanded;
        static double _heliHeightSum, _heliHeightMin = double.MaxValue, _heliHeightMax;
        static int _lastShooters, _lastHelisAir;
        static readonly long[] _gapBuilding = new long[5], _gapOther = new long[5];
        static long _eyeComputed, _eyeNear, _eyeInside, _eyeElevated, _eyeGarrison, _eyeOffMap, _garrisonCalls, _garrisonDeferred;
        static long _eyeNearNotOn;                                  // infantry next to a building pixel, not on one and not raised: ground-level eye
        static long _recheckRuns, _recheckChanged, _recopies;
        static string _lastReport;
        static long _lastReportedEvaluated;
        static Stage _lastReportedStage;
        static int _lastReportedErrors;

        static void Log(string s) => Mod.Log.Msg("[VUE-AA] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_VueAA");
            _enabled = c.CreateEntry("MesureVueAA", true, description: Build.Desc("Vue propre des défenses anti-aériennes vers les hélicoptères (arbres, relief, bâtiments) : calcul automatique utilisé par la règle des missiles infrarouges ; toujours actif"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _onlineLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        static void EndBattle(string why)
        {
            if (_sessionArmed)
            {
                _sessionArmed = false;
                if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
                Report(true);                                       // before ForgetMap: the final line keeps the self-test result and errors
                Log($"fin de calcul ({why})");
            }
            ForgetMap();
            _gcPtr = IntPtr.Zero;
            ResetStats();
            SetGate(false, "pas de bataille");
        }

        /// Drops the map copy and the current cycle; the published sets become empty and stale.
        static void ForgetMap()
        {
            PublishEmpty(false);
            _stage = Stage.Waiting;
            _mode = BuildMode.Unknown;
            _pix = null; _height = null; _terrain = null; _calOffsets = null; _blockNb = null; _blockB = null; _blockMax = null; _recheckIdx = null;
            _mapPtr = IntPtr.Zero;
            _copyLine = 0; _copyFrames = 0; _copyMs = 0; _forestPixels = 0; _buildingPixels = 0;
            _mapTries = 0; _testTries = 0; _nextTest = 0; _errors = 0; _recopy = false; _garrisonBroken = false;
            _cycleRunning = false; _pairs.Clear(); _air.Clear(); _eyes.Clear(); _pairIdx = 0; _cycleMasked = null; _cycleEval = null;
            _recheckCount = 0; _calSampleCount = 0;
            _testResult = "en attente"; _calibResult = "pas encore faite";
        }

        static void PublishEmpty(bool fresh)
        {
            if (Masked.Count != 0) Masked = new HashSet<long>();
            if (Evaluated.Count != 0) Evaluated = new HashSet<long>();
            Interlocked.Exchange(ref _validUntil, fresh ? Environment.TickCount64 + (long)ValidMs : 0);
        }

        static void SetGate(bool ready, string reason, bool failed = false)
        {
            ReadyForGating = ready;
            GateReason = reason;
            GateFailed = failed && !ready;
        }

        static void ResetStats()
        {
            _cycles = 0; _evaluated = 0; _skippedPairs = 0; _clearWithForest = 0; _ownScans = 0; _losCycles = 0; _fallbackCycles = 0; _evalInfantry = 0; _maskedInfantry = 0;
            Array.Clear(_byCause, 0, _byCause.Length); Array.Clear(_bandEval, 0, Bands); Array.Clear(_bandMasked, 0, Bands);
            _fullN = _fullForest = _fullBuilding = _fullBuildingForest = _fullRelief = _fullReliefOther = 0;
            _marchMaxMs = 0; _frameMaxMs = 0; _cycleMaxS = 0;
            _heliSamples = 0; _heliLanded = 0;
            _heliHeightSum = 0; _heliHeightMin = double.MaxValue; _heliHeightMax = 0;
            Array.Clear(_gapBuilding, 0, _gapBuilding.Length); Array.Clear(_gapOther, 0, _gapOther.Length); Array.Clear(_offsetHist, 0, _offsetHist.Length);
            _eyeComputed = _eyeNear = _eyeInside = _eyeElevated = _eyeGarrison = _eyeOffMap = _garrisonCalls = _garrisonDeferred = 0;
            _eyeNearNotOn = 0;
            _recheckRuns = _recheckChanged = _recopies = 0;
            _lastShooters = 0; _lastHelisAir = 0; _lastReport = null; _lastReportedEvaluated = 0; _lastReportedErrors = 0;
        }

        static int _wait;

        /// Every frame in a campaign mission: scheduling every 0.5 s; map copy, calibration or line walking every frame within 2 ms.
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _refused)
            {
                if (ReadyForGating || !GateFailed) SetGate(false, _refused ? "calcul de la vue coupé par sécurité" : "calcul de la vue désactivé", true);
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            // the 0.5 s scheduling waits for a frame with no other heavy module job (Planif.cs); the map copy and the line walking
            // below keep their own per-frame budget and are never delayed
            if (now >= _nextCheck && Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm))
            {
                _nextCheck = now + 0.5f;
                var gc = GameController._instance;
                if (gc == null || gc._GameSession_k__BackingField?.CurrentPlayer == null) { if (_sessionArmed || _stage != Stage.Waiting) EndBattle("plus de partie"); return; }
                if (!Solo())
                {
                    if (!_onlineLogged) { _onlineLogged = true; Log("partie en ligne : calcul coupé"); }
                    if (_stage != Stage.Waiting) ForgetMap();
                    SetGate(false, "partie en ligne", true);
                    return;
                }
                IntPtr gp = gc.Pointer;
                if (gp != _gcPtr) { ForgetMap(); _gcPtr = gp; _battleSeen = now; }
                if (_errors <= MaxErrors)
                {
                    try
                    {
                        MapStage(gc, now);
                        if (_refused) return;
                        if (_stage == Stage.Ready && !_cycleRunning && now >= _nextRecheck) { _nextRecheck = now + RecheckEvery; RecheckBuildings(gc); }
                        if (_stage == Stage.Ready && !_cycleRunning && now >= _nextCycle) StartCycle(now);
                    }
                    catch (Exception e) { Error("préparation", e); }
                }
                UpdateGate();
                if (now >= _nextReport) { _nextReport = now + 30f; Report(false); }
            }
            if (_errors > MaxErrors) return;
            try
            {
                if (_stage == Stage.Copying) CopyStep(now);
                else if (_stage == Stage.Calibrating) CalibrateStep(now);
                else if (_cycleRunning) RunCycle(now);
            }
            catch (Exception e) { Error(_stage == Stage.Copying ? "copie de la carte" : _stage == Stage.Calibrating ? "calibrage des bâtiments" : "calcul des vues", e); }
        }

        static void UpdateGate()
        {
            if (_errors > MaxErrors) SetGate(false, "trop d'erreurs dans le calcul des vues", true);
            else if (_stage == Stage.Failed) SetGate(false, "carte : " + _testResult, true);
            else if (!_sessionArmed) SetGate(false, "carte pas encore lue");
            else if (_stage == Stage.Waiting || (!_recopy && (_stage == Stage.Copying || _stage == Stage.Calibrating))) SetGate(false, "carte en préparation");
            else if (_mode == BuildMode.Ambiguous) SetGate(false, $"hauteur des bâtiments ambiguë sur cette carte (médiane {_median:0.0} m, entre {FlatMax:0} et {RoofMin:0} m)", true);
            else if (_mode == BuildMode.Unknown) SetGate(false, "bâtiments pas encore calibrés");
            else SetGate(true, "carte prête");
        }

        static void Error(string what, Exception e)
        {
            _errors++;
            if (_errors <= 3 || _errors == MaxErrors + 1)
                Mod.Log.Warning($"[VUE-AA] {what} impossible : {e.GetBaseException().Message}" + (_errors > MaxErrors ? " (calcul arrêté pour cette bataille)" : ""));
            if (_errors > MaxErrors) { _cycleRunning = false; _stage = Stage.Failed; PublishEmpty(false); _testResult = "arrêtée après trop d'erreurs"; SetGate(false, "trop d'erreurs dans le calcul des vues", true); }
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

        // ---------------------------------------------------------------- map: self-test, copy, calibration

        static void MapStage(GameController gc, float now)
        {
            if (_stage != Stage.Waiting)
            {
                var cur = gc.MapMetaData;
                IntPtr cp = cur == null ? IntPtr.Zero : cur.Pointer;
                if (_mapPtr != IntPtr.Zero && cp != _mapPtr) { Log("la carte a changé : nouveau calcul"); ForgetMap(); _battleSeen = now; }
                return;
            }
            if (now - _battleSeen < 3f || now < _nextTest) return;   // let the battle initialise its map
            var m = gc.MapMetaData;
            int maxX = 0, maxY = 0, stride = 0;
            Il2CppStructArray<MapPixel> pix = null;
            if (m != null) { maxX = m._maxX; maxY = m._maxY; stride = m._stride; pix = m._pixels; }
            if (m == null || maxX <= 0 || maxY <= 0 || stride <= 0 || pix == null)
            {
                if (++_mapTries == MaxMapTries)
                {
                    _stage = Stage.Failed;
                    _testResult = "carte jamais prête";
                    Log($"carte jamais prête (carte {(m != null ? "présente" : "absente")}, maxX {maxX}, maxY {maxY}, stride {stride}, pixels {(pix != null ? "présents" : "absents")}) : aucune paire masquée publiée");
                }
                return;
            }
            if (!ArmGuard()) return;
            LogConstantsOnce();
            SelfTest(m, pix, maxX, maxY, stride, now);
        }

        /// Crash guard: counted once per battle before the first native read of the map.
        static bool ArmGuard()
        {
            if (_sessionArmed) return true;
            if (_unclean.Value >= 2)
            {
                _refused = true;
                PublishEmpty(false);
                SetGate(false, "calcul de la vue coupé par sécurité", true);
                Mod.Log.Warning("[VUE-AA] calcul désactivé : les deux dernières parties calculées ne se sont pas terminées normalement (la règle de vue propre de l'infanterie reste en mesure seule)");
                return false;
            }
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;
            MelonPreferences.Save();
            return true;
        }

        static void LogConstantsOnce()
        {
            if (_constantsLogged) return;
            _constantsLogged = true;
            var sb = new StringBuilder("réglages du jeu :");
            try { sb.Append($" hauteur de forêt (navigation) {NavConst.FOREST_HEIGHT:0.##} m"); } catch (Exception e) { sb.Append(" hauteur de forêt illisible (" + e.GetBaseException().Message + ")"); }
            try
            {
                var fow = GameCfg.Instance?.FogOfWarConfig;
                sb.Append(fow == null ? ", réglages du brouillard illisibles"
                    : $", forêt traversable par un tir {fow.MaxShootableForestDistance:0.##} m, laser en forêt {fow.LaserForestMaxRange:0.##} m (dans sa propre forêt {fow.LaserOwnForestRange:0.##} m)");
            }
            catch (Exception e) { sb.Append(", réglages du brouillard illisibles (" + e.GetBaseException().Message + ")"); }
            sb.Append($" ; règle de vue : arbres {TreeHeight:0} m, forêt opaque au-delà de {ForestMax:0} m traversés, sommet des bâtiments calibré sur la carte, " +
                      $"œil infanterie +{EyeInfantry:0.0} m / véhicule +{EyeVehicle:0} m, dans un bâtiment : sommet du bâtiment +{EyeInfantry:0.0} m, hélico +{HeliAim:0} m, en vol dès {MinAirborne:0} m, " +
                      $"portée du missile +{RangeMargin:0} m (au plus {MaxRangeCap / 1000f:0} km), chaque pixel traversé ; case du tireur et de l'hélico ignorées ; " +
                      "appliquée aux équipes d'infanterie seulement, véhicules mesurés sans blocage");
            Log(sb.ToString());
        }

        static bool SamePixel(MapPixel p, byte terrain, float height)
        {
            byte keep = (byte)~_bridgeMask;
            return Math.Abs(p.Height - height) < 0.05f && (byte)((byte)p.TerrainData & keep) == (byte)(terrain & keep);
        }

        /// 20 random pixels: the engine's ConvertPosition + GetTerrainAtHeight against both index orders of _pixels,
        /// and the engine's WorldToPixelX/Y against floor or rounding of the managed conversion.
        static void SelfTest(MapMeta m, Il2CppStructArray<MapPixel> pix, int maxX, int maxY, int stride, float now)
        {
            _testTries++;
            _nextTest = now + 10f;
            int w = maxX + 1, h = maxY + 1;
            long len = pix.Length;
            try { _bridgeMask = (byte)MapMeta.BRIDGE_FLAG_FILTER; } catch { _bridgeMask = 240; }
            _forestVal = (byte)Terrain.Forest; _buildingVal = (byte)Terrain.Buildings; _waterVal = (byte)Terrain.Water; _lowWaterVal = (byte)Terrain.LowWater;

            string why = null;
            V3 p0 = m.ConvertPosition(0, 0, 0f), p1 = m.ConvertPosition(maxX, maxY, 0f);
            float dx = (p1.x - p0.x) / maxX, dz = (p1.z - p0.z) / maxY;
            if (!(dx > 0.01f) || !(dz > 0.01f)) why = $"pas des pixels illisible ({dx}, {dz})";
            if ((long)w * h > len) why = $"tableau trop petit ({len} pixels pour {w}x{h})";

            int okA = 0, okB = 0, convFloor = 0, convRound = 0, convLinear = 0;
            var samples = new StringBuilder();
            if (why == null)
            {
                var rnd = new Random(7919 * _testTries + w * 31 + h);
                for (int k = 0; k < TestPoints; k++)
                {
                    int x = rnd.Next(1, maxX), y = rnd.Next(1, maxY);
                    V3 pos = m.ConvertPosition(x, y, 0f);
                    V3 q = pos;
                    m.GetTerrainAtHeight(ref q, out Terrain tt, out float th);
                    byte tb = (byte)tt;
                    _tpX[k] = x; _tpY[k] = y; _tpH[k] = th; _tpT[k] = (byte)(tb & (byte)~_bridgeMask);

                    long ia = (long)x * stride + y, ib = (long)y * stride + x;
                    bool a = y < stride && ia < len && SamePixel(pix[(int)ia], tb, th);
                    bool b = x < stride && ib < len && SamePixel(pix[(int)ib], tb, th);
                    if (a) okA++;
                    if (b) okB++;

                    if (Math.Abs(pos.x - (p0.x + x * dx)) < 0.05f && Math.Abs(pos.z - (p0.z + y * dz)) < 0.05f) convLinear++;
                    int ex3 = m.WorldToPixelX(pos.x + 0.3f * dx), ey3 = m.WorldToPixelY(pos.z + 0.3f * dz);
                    int ex7 = m.WorldToPixelX(pos.x + 0.7f * dx), ey7 = m.WorldToPixelY(pos.z + 0.7f * dz);
                    if (ex3 == x && ey3 == y && ex7 == x && ey7 == y) convFloor++;
                    if (ex3 == x && ey3 == y && ex7 == x + 1 && ey7 == y + 1) convRound++;
                    if (k < 3) samples.Append($" ({x},{y}) monde ({pos.x:0.#},{pos.z:0.#}) moteur {tt}/{th:0.##} m");
                }
                bool orderA = okA >= TestPass, orderB = okB >= TestPass;
                if (orderA && orderB) why = "les deux ordres des pixels concordent (carte trop uniforme pour décider)";
                else if (!orderA && !orderB) why = "aucun ordre des pixels ne concorde avec le moteur";
                else if (convLinear < TestPass) why = "positions du moteur non alignées sur la grille";
                else if (orderA && h > stride) why = "ordre x*stride+y incompatible avec la hauteur de la carte";
                else if (orderB && w > stride) why = "ordre y*stride+x incompatible avec la largeur de la carte";
                else if ((orderA ? (long)(w - 1) * stride + (h - 1) : (long)(h - 1) * stride + (w - 1)) >= len) why = "tableau des pixels trop court pour cet ordre";
                if (why == null) _orderXFirst = orderA;
            }

            string detail = $"ordre x*stride+y {okA}/{TestPoints}, ordre y*stride+x {okB}/{TestPoints}, grille {convLinear}/{TestPoints}, " +
                            $"conversion plancher {convFloor}/{TestPoints} arrondi {convRound}/{TestPoints}, {w}x{h} pixels, stride {stride}, pixel {dx:0.###}x{dz:0.###} m";
            if (why != null)
            {
                PublishEmpty(false);
                if (_testTries >= MaxTestTries)
                {
                    _stage = Stage.Failed;
                    _testResult = "échec (" + why + ")";
                    Log($"auto-test de la carte échoué ({_testTries}/{MaxTestTries}) : {why} ; {detail} ;{samples} : calcul coupé pour cette bataille, aucune paire masquée publiée");
                }
                else Log($"auto-test de la carte échoué (essai {_testTries}/{MaxTestTries}, nouvel essai dans 10 s) : {why} ; {detail} ;{samples}");
                return;
            }

            _w = w; _h = h; _stride = stride; _pix = pix; _mapPtr = m.Pointer;
            _ox = p0.x; _oz = p0.z; _invDx = 1f / dx; _invDz = 1f / dz;
            // rounding only moves a sample by one pixel (3 m): an unrecognised engine conversion is logged, not fatal (floor is used)
            _roundX = _roundZ = convRound >= TestPass && convFloor < TestPass ? 0.5f : 0f;
            string conv = convFloor >= TestPass ? "plancher" : convRound >= TestPass ? "arrondi" : "non reconnue (plancher utilisé)";
            int n = w * h;
            if (_height == null || _height.Length != n) { _height = new float[n]; _terrain = new byte[n]; }
            _copyLine = 0; _copyFrames = 0; _copyMs = 0; _forestPixels = 0; _buildingPixels = 0;
            _minGround = float.MaxValue; _maxGround = float.MinValue;
            _recopy = false;
            _stage = Stage.Copying;
            _testResult = $"réussi ({(_orderXFirst ? "x*stride+y" : "y*stride+x")}, {(_orderXFirst ? okA : okB)}/{TestPoints})";
            Log($"auto-test de la carte réussi : ordre {(_orderXFirst ? "x*stride+y" : "y*stride+x")}, conversion {conv}, origine ({_ox:0.#},{_oz:0.#}) ; {detail} ;{samples} ; copie en cours");
        }

        /// Copies whole pixel lines until the 2 ms budget is spent; the source array stays alive through _pix.
        static void CopyStep(float now)
        {
            var pix = _pix;
            if (pix == null || _height == null) { _stage = Stage.Failed; _testResult = "copie interrompue"; PublishEmpty(false); return; }
            _sw.Restart();
            var span = pix.AsSpan();
            int outer = _orderXFirst ? _w : _h, inner = _orderXFirst ? _h : _w;
            byte keep = (byte)~_bridgeMask, forestVal = _forestVal, buildingVal = _buildingVal;
            float minG = _minGround, maxG = _maxGround;
            long forest = _forestPixels, build = _buildingPixels;
            float[] hgt = _height; byte[] ter = _terrain;
            int w = _w, stride = _stride;
            bool xFirst = _orderXFirst;
            while (_copyLine < outer)
            {
                int line = _copyLine++;
                int src = line * stride;
                // destination step: one row down (x fixed) when the source is column-major, one pixel right otherwise
                int d = xFirst ? line : line * w, dStep = xFirst ? w : 1;
                for (int i = 0; i < inner; i++, d += dStep)
                {
                    var p = span[src + i];
                    float g = p.Height;
                    byte t = (byte)((byte)p.TerrainData & keep);
                    hgt[d] = g; ter[d] = t;
                    if (g < minG) minG = g;
                    if (g > maxG) maxG = g;
                    if (t == forestVal) forest++;
                    else if (t == buildingVal) build++;
                }
                if (_sw.Elapsed.TotalMilliseconds >= BudgetMs) break;
            }
            _minGround = minG; _maxGround = maxG; _forestPixels = forest; _buildingPixels = build;
            double ms = _sw.Elapsed.TotalMilliseconds;
            _copyMs += ms; _copyFrames++;
            if (ms > _frameMaxMs) _frameMaxMs = ms;
            if (_copyLine < outer) return;

            int ok = 0;
            for (int k = 0; k < TestPoints; k++)
            {
                int idx = _tpY[k] * _w + _tpX[k];
                if (Math.Abs(_height[idx] - _tpH[k]) < 0.05f && _terrain[idx] == _tpT[k]) ok++;
            }
            _pix = null;                                            // the managed copy is all the walk needs
            long n = (long)_w * _h;
            if (ok < TestPass)
            {
                _stage = Stage.Failed;
                _testResult = $"échec de la copie ({ok}/{TestPoints})";
                PublishEmpty(false);
                Log($"copie de la carte incohérente : {ok}/{TestPoints} points identiques au moteur ; calcul coupé pour cette bataille, aucune paire masquée publiée");
                return;
            }
            Log($"carte {(_recopy ? "recopiée" : "copiée")} : {_w}x{_h} pixels en {_copyMs:0} ms sur {_copyFrames} images, forêt {100.0 * forest / n:0.#} %, bâtiments {100.0 * build / n:0.##} %, " +
                $"sol {_minGround:0.#} à {_maxGround:0.#} m, vérification {ok}/{TestPoints} points ; calibrage des bâtiments en cours");
            InitCalibration();
            _stage = Stage.Calibrating;
        }

        static void InitCalibration()
        {
            _bw = (_w + (1 << BlockShift) - 1) >> BlockShift;
            _bh = (_h + (1 << BlockShift) - 1) >> BlockShift;
            int nb = _bw * _bh;
            if (_blockNb == null || _blockNb.Length != nb) { _blockNb = new float[nb]; _blockB = new float[nb]; _blockMax = new float[nb]; }
            Array.Fill(_blockNb, float.MinValue);
            Array.Fill(_blockB, float.MinValue);
            _calLine = 0; _calMs = 0;
            if (_recopy) return;
            _calStep = (int)Math.Max(1, _buildingPixels / CalSamples);
            _recheckStep = (int)Math.Max(1, _buildingPixels / RecheckSamples);
            _calCounter = 0; _calSampleCount = 0; _calNoGround = 0; _recheckCounter = 0; _recheckCount = 0;
            _calOffsets ??= new float[CalSamples];
            _recheckIdx ??= new int[RecheckSamples];
            Array.Clear(_offsetHist, 0, _offsetHist.Length);
        }

        /// One pass over the copy, a few rows per frame: building height samples (first copy only) and the highest obstacle per 48 m block.
        static void CalibrateStep(float now)
        {
            _sw.Restart();
            float[] hgt = _height; byte[] ter = _terrain;
            int w = _w, h = _h, bw = _bw;
            byte bv = _buildingVal, fv = _forestVal;
            bool recopy = _recopy;
            while (_calLine < h)
            {
                int y = _calLine++;
                int row = y * w, brow = (y >> BlockShift) * bw;
                for (int x = 0; x < w; x++)
                {
                    int k = row + x;
                    byte t = ter[k];
                    float hh = hgt[k];
                    int b = brow + (x >> BlockShift);
                    if (t == bv)
                    {
                        if (hh > _blockB[b]) _blockB[b] = hh;
                        if (recopy) continue;
                        if (++_calCounter >= _calStep && _calSampleCount < CalSamples)
                        {
                            _calCounter = 0;
                            if (RingGround(x, y, out float g))
                            {
                                float off = hh - g;
                                _calOffsets[_calSampleCount++] = off;
                                int bin = 0;
                                while (bin < OffsetBins.Length && off >= OffsetBins[bin]) bin++;
                                _offsetHist[bin]++;
                            }
                            else _calNoGround++;
                        }
                        if (++_recheckCounter >= _recheckStep && _recheckCount < RecheckSamples) { _recheckCounter = 0; _recheckIdx[_recheckCount++] = k; }
                    }
                    else
                    {
                        float ob = t == fv ? hh + TreeHeight : hh;
                        if (ob > _blockNb[b]) _blockNb[b] = ob;
                    }
                }
                if (_sw.Elapsed.TotalMilliseconds >= BudgetMs) break;
            }
            double ms = _sw.Elapsed.TotalMilliseconds;
            _calMs += ms;
            if (ms > _frameMaxMs) _frameMaxMs = ms;
            if (_calLine < h) return;

            if (!recopy) DecideMode();
            float maxOb = float.MinValue;
            for (int i = 0; i < _blockMax.Length; i++)
            {
                float v = _blockNb[i];
                if (_blockB[i] > float.MinValue) v = Math.Max(v, _blockB[i] + _buildingAdd);
                _blockMax[i] = v;
                if (v > maxOb) maxOb = v;
            }
            _maxObstacle = maxOb;
            _eyes.Clear();
            _stage = Stage.Ready;
            _nextCycle = 0;
            _nextRecheck = now + RecheckEvery;
            if (recopy) { _recopy = false; _recopies++; Log($"obstacles recalculés après la nouvelle copie ({_calMs:0} ms), obstacle le plus haut {_maxObstacle:0.#} m"); }
            else
            {
                var hist = new StringBuilder();
                for (int b = 0; b < _offsetHist.Length; b++)
                {
                    string label = b == 0 ? $"<{OffsetBins[0]:0.#}" : b == OffsetBins.Length ? $"{OffsetBins[b - 1]:0.#}+" : $"{OffsetBins[b - 1]:0.#}-{OffsetBins[b]:0.#}";
                    hist.Append(b == 0 ? "" : ", ").Append($"{label} m {_offsetHist[b]}");
                }
                Log($"bâtiments : {_calSampleCount} pixels mesurés (sans sol autour {_calNoGround}), hauteur au-dessus du sol voisin médiane {_median:0.0} m [{hist}] ; " +
                    $"hauteur d'observation des bâtiments du jeu {_fowBuildingHeight:0.#} m (multiplicateur du mod x{_fowMultiplier:0.##}) ; décision : {_calibResult} ; " +
                    $"obstacle le plus haut {_maxObstacle:0.#} m ; {_calMs:0} ms ; {_recheckCount} pixels de bâtiments contrôlés toutes les {RecheckEvery:0} s");
            }
        }

        /// Roofs in the height map, flat footprints or ambiguous, from the median height of building pixels above the ground around them.
        static void DecideMode()
        {
            int n = _calSampleCount;
            _median = 0f;
            if (n > 0)
            {
                var tmp = new float[n];
                Array.Copy(_calOffsets, tmp, n);
                Array.Sort(tmp);
                _median = n % 2 == 1 ? tmp[n / 2] : (tmp[n / 2 - 1] + tmp[n / 2]) / 2f;
            }
            _flatHeight = EngineBuildingHeight();
            if (n < MinCalSamples)
            {
                _mode = BuildMode.Roof; _buildingAdd = 0f; _roofOffset = Math.Max(0f, _median);
                _calibResult = $"peu de bâtiments mesurés ({n}) : sommet = hauteur du pixel";
            }
            else if (_median >= RoofMin)
            {
                _mode = BuildMode.Roof; _buildingAdd = 0f; _roofOffset = _median;
                _calibResult = "toits déjà dans la carte : sommet = hauteur du pixel";
            }
            else if (_median < FlatMax)
            {
                _mode = BuildMode.Flat; _buildingAdd = _flatHeight; _roofOffset = 0f;
                _calibResult = $"empreintes plates : sommet = sol + {_flatHeight:0.#} m (hauteur des bâtiments du jeu)";
            }
            else
            {
                _mode = BuildMode.Ambiguous; _buildingAdd = 0f; _roofOffset = _median;
                _calibResult = "ambiguë : vues calculées avec le sommet = hauteur du pixel, jamais utilisées par la règle sur cette carte";
            }
        }

        /// The engine's building observation height without the mod's HauteurObservationBatiments multiplier (4 to 20 m, 8 m if unreadable).
        static float EngineBuildingHeight()
        {
            _fowBuildingHeight = float.NaN; _fowMultiplier = 1f;
            try
            {
                var ts = GameCfg.Instance?.FogOfWarConfig?.TerrainTypeSettings;
                for (int i = 0; i < (ts?.Length ?? 0); i++)
                {
                    var t = ts[i];
                    if (t != null && (byte)t.TerrainType == _buildingVal) { _fowBuildingHeight = t.Height; break; }
                }
                var e = MelonPreferences.GetEntry("RealismOverhaul_Realisme", "HauteurObservationBatiments");
                if (Realism.IsApplied && e?.BoxedValue is float mult && mult > 0f) _fowMultiplier = mult;
            }
            catch { }
            if (float.IsNaN(_fowBuildingHeight) || !(_fowBuildingHeight > 0f)) return DefaultBuildingHeight;
            return Math.Clamp(_fowBuildingHeight / _fowMultiplier, 4f, 20f);
        }

        /// Lowest non-building, non-water pixel height on the nearest ring (1 to 5 pixels) around a pixel.
        static bool RingGround(int x, int y, out float ground)
        {
            ground = 0f;
            for (int r = 1; r <= RingMax; r++)
            {
                float best = float.MaxValue;
                int x0 = x - r, x1 = x + r, y0 = y - r, y1 = y + r;
                for (int xx = x0; xx <= x1; xx++) { RingCheck(xx, y0, ref best); RingCheck(xx, y1, ref best); }
                for (int yy = y0 + 1; yy < y1; yy++) { RingCheck(x0, yy, ref best); RingCheck(x1, yy, ref best); }
                if (best < float.MaxValue) { ground = best; return true; }
            }
            return false;
        }

        static void RingCheck(int x, int y, ref float best)
        {
            if ((uint)x >= (uint)_w || (uint)y >= (uint)_h) return;
            int k = y * _w + x;
            byte t = _terrain[k];
            if (t == _buildingVal || t == _waterVal || t == _lowWaterVal) return;
            float g = _height[k];
            if (g < best) best = g;
        }

        /// Ground height under a pixel: the pixel itself, or the ground around it on a building pixel.
        static float GroundAt(int k, int x, int y)
        {
            if (_terrain[k] != _buildingVal) return _height[k];
            return RingGround(x, y, out float g) ? g : _height[k] - _roofOffset;
        }

        static int PixelX(float wx) => (int)MathF.Floor((wx - _ox) * _invDx + _roundX);
        static int PixelY(float wz) => (int)MathF.Floor((wz - _oz) * _invDz + _roundZ);

        /// Every 60 s: 300 building pixels of the copy against the engine's map; a change (destroyed building) starts a new copy.
        static void RecheckBuildings(GameController gc)
        {
            if (_recheckCount == 0 || _height == null) return;
            var m = gc.MapMetaData;
            if (m == null || m.Pointer != _mapPtr) return;
            var pix = m._pixels;
            if (pix == null) return;
            long len = pix.Length;
            byte keep = (byte)~_bridgeMask;
            int changed = 0;
            for (int i = 0; i < _recheckCount; i++)
            {
                int k = _recheckIdx[i];
                int x = k % _w, y = k / _w;
                long idx = _orderXFirst ? (long)x * _stride + y : (long)y * _stride + x;
                if (idx >= len) continue;
                var p = pix[(int)idx];
                if (Math.Abs(p.Height - _height[k]) > 0.05f || (byte)((byte)p.TerrainData & keep) != _terrain[k]) changed++;
            }
            _recheckRuns++;
            if (changed == 0) return;
            _recheckChanged += changed;
            Log($"bâtiments modifiés dans la carte du jeu ({changed}/{_recheckCount} pixels contrôlés) : nouvelle copie de la carte");
            _pix = pix; _recopy = true;
            _copyLine = 0; _copyFrames = 0; _copyMs = 0; _forestPixels = 0; _buildingPixels = 0;
            _minGround = float.MaxValue; _maxGround = float.MinValue;
            _stage = Stage.Copying;
        }

        // ---------------------------------------------------------------- eyes

        /// Eye of a shooter (cached while it moves less than 3 m, 10 s at most). Null when it stands off the map.
        static Eye EyeOf(int eid, V3 pos, bool infantry, float now)
        {
            if (_eyes.TryGetValue(eid, out var cached) && now - cached.Time < EyeMaxAge && cached.Infantry == infantry)
            {
                float mx = pos.x - cached.Pos.x, my = pos.y - cached.Pos.y, mz = pos.z - cached.Pos.z;
                if (mx * mx + my * my + mz * mz < EyeMove * EyeMove) return cached;
            }
            int px = PixelX(pos.x), py = PixelY(pos.z);
            if ((uint)px >= (uint)_w || (uint)py >= (uint)_h) { _eyeOffMap++; return null; }
            var e = new Eye { Pos = pos, Time = now, Infantry = infantry };
            int k0 = py * _w + px;
            e.K0 = k0;
            byte bv = _buildingVal;
            e.OnBuilding = _terrain[k0] == bv;
            bool near = false;
            for (int dy = -1; dy <= 1 && !near; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if ((uint)nx < (uint)_w && (uint)ny < (uint)_h && _terrain[ny * _w + nx] == bv) { near = true; break; }
                }
            e.NearBuilding = near;
            float g = GroundAt(k0, px, py);
            e.Gap = pos.y - g;
            var gh = e.OnBuilding ? _gapBuilding : _gapOther;
            int gb = 0;
            while (gb < GapBins.Length && e.Gap >= GapBins[gb]) gb++;
            gh[gb]++;
            _eyeComputed++;
            bool elevated = e.Gap >= ElevatedGap;
            if (elevated) _eyeElevated++;
            if (infantry && near)
            {
                _eyeNear++;
                // only a team standing on a building pixel can be inside it: next to a wall in the street it keeps a ground-level eye
                if (!elevated && !e.OnBuilding) _eyeNearNotOn++;
                if (!elevated && e.OnBuilding && !_garrisonBroken)
                {
                    if (_garrisonCallsThisCycle < MaxGarrisonCallsPerCycle) { _garrisonCallsThisCycle++; e.Garrison = GarrisonNear(pos); if (e.Garrison) _eyeGarrison++; }
                    else { _garrisonDeferred++; e.Time = now - EyeMaxAge + 1f; }                 // not checked yet: computed again next cycle
                }
                e.Inside = elevated || e.Garrison;
            }
            if (e.Inside)
            {
                _eyeInside++;
                e.Own = OwnBuilding(px, py, out float topPixel);
                e.Top = e.Own.Count > 0 ? topPixel + _buildingAdd : g;
                e.Y = Math.Max(e.Top, pos.y) + EyeInfantry;
            }
            else
            {
                float lift = Math.Clamp(pos.y - g, 0f, EyeMaxLift);
                e.Y = g + lift + (infantry ? EyeInfantry : EyeVehicle);
            }
            _eyes[eid] = e;
            return e;
        }

        /// A garrisoned (not destroyed) building among those within 20 m whose shoot position is within 8 m of the team (the building that
        /// holds it, not a neighbour). LuaMap/LuaBuilding calls proven in EnemyAi.cs.
        static bool GarrisonNear(V3 pos)
        {
            try
            {
                _map ??= new LuaMap();
                _garrisonCalls++;
                var arr = _map.GetBuildingsInRange(pos, GarrisonRadius);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    var b = arr[i];
                    if (b == null || b.IsDestroyed() || !b.HasUnitsInside()) continue;
                    var sp = b.GetShootPosition();
                    float dx = sp.x - pos.x, dz = sp.z - pos.z;
                    if (dx * dx + dz * dz <= GarrisonOwnRadius * GarrisonOwnRadius) return true;
                }
            }
            catch (Exception e)
            {
                _garrisonBroken = true;
                Log("bâtiments occupés illisibles (" + e.GetBaseException().Message + ") : seule la hauteur de l'unité dit si elle est dans un bâtiment");
            }
            return false;
        }

        /// Building pixels connected (8 neighbours) to the shooter's 3x3 cell, within 27 m, 400 pixels at most. Top = highest pixel height.
        static HashSet<int> OwnBuilding(int px, int py, out float top)
        {
            var own = new HashSet<int>();
            var queue = new Queue<int>();
            top = float.MinValue;
            byte bv = _buildingVal;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if ((uint)nx >= (uint)_w || (uint)ny >= (uint)_h) continue;
                    int k = ny * _w + nx;
                    if (_terrain[k] == bv && own.Add(k)) { queue.Enqueue(k); if (_height[k] > top) top = _height[k]; }
                }
            while (queue.Count > 0 && own.Count < OwnMaxPx)
            {
                int k = queue.Dequeue();
                int x = k % _w, y = k / _w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)_w || (uint)ny >= (uint)_h || Math.Abs(nx - px) > OwnRadiusPx || Math.Abs(ny - py) > OwnRadiusPx) continue;
                        int nk = ny * _w + nx;
                        if (_terrain[nk] != bv || own.Count >= OwnMaxPx || !own.Add(nk)) continue;
                        queue.Enqueue(nk);
                        if (_height[nk] > top) top = _height[nk];
                    }
            }
            return own;
        }

        // ---------------------------------------------------------------- line-of-sight cycles

        static void StartCycle(float now)
        {
            _nextCycle = now + CycleSeconds;
            _pairs.Clear();
            _air.Clear();
            _pairIdx = 0;
            _garrisonCallsThisCycle = 0;
            var los = AntiHeliPortee.Los;
            bool fromLos = los != null && now - los.Time <= 1.5f;
            AaSnapshot aa = null;
            if (!fromLos)
            {
                aa = Reperage.Aa;
                if (aa == null || now - aa.Time > 1.5f) { aa = Reperage.ScanForVueAA(now); _ownScans++; }   // Reperage not publishing: same scan, done here
                if (aa == null) { PublishEmpty(true); _cycles++; return; }
                _fallbackCycles++;
            }
            else _losCycles++;

            int nh = fromLos ? los.Helis.Length : aa.Helis.Length;
            for (int i = 0; i < nh; i++)
            {
                int eid, side; V3 pos;
                if (fromLos) { var hu = los.Helis[i]; eid = hu.EntityId; side = hu.Side; pos = hu.Pos; }
                else { var hu = aa.Helis[i]; eid = hu.EntityId; side = hu.Side; pos = hu.Pos; }
                int px = PixelX(pos.x), py = PixelY(pos.z);
                if ((uint)px >= (uint)_w || (uint)py >= (uint)_h) continue;
                int kt = py * _w + px;
                double height = pos.y - GroundAt(kt, px, py);
                _heliSamples++;
                if (height < MinAirborne) { _heliLanded++; continue; }
                _heliHeightSum += height;
                if (height < _heliHeightMin) _heliHeightMin = height;
                if (height > _heliHeightMax) _heliHeightMax = height;
                _air.Add(new AirTarget { Eid = eid, Side = side, Pos = pos, Kt = kt });
            }
            _lastHelisAir = _air.Count;
            int ns = fromLos ? los.Shooters.Length : aa.Shooters.Length;
            _lastShooters = ns;

            int skipped = 0;
            if (_air.Count > 0)
            {
                // pass 0: infantry (their verdicts drive the rule), pass 1: vehicles (measurement only, first dropped at the pair cap)
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < ns; i++)
                    {
                        int eid, side; V3 pos; bool infantry; float range;
                        if (fromLos) { var s = los.Shooters[i]; eid = s.EntityId; side = s.Side; pos = s.Pos; infantry = s.Infantry; range = Math.Min(s.Range + RangeMargin, MaxRangeCap); }
                        else { var s = aa.Shooters[i]; eid = s.EntityId; side = s.Side; pos = s.Pos; infantry = s.Infantry; range = FallbackRange; }
                        if (infantry != (pass == 0)) continue;
                        Eye eye = null;
                        foreach (var hu in _air)
                        {
                            if (hu.Side == side) continue;                              // enemy helicopters only
                            float ddx = hu.Pos.x - pos.x, ddz = hu.Pos.z - pos.z;
                            if (ddx * ddx + ddz * ddz > range * range) continue;
                            if (_pairs.Count >= MaxPairs) { skipped++; continue; }
                            eye ??= EyeOf(eid, pos, infantry, now);
                            if (eye == null) break;
                            // the line starts where the cached eye was computed (at most 3 m away): its pixel K0, ground and own building
                            // belong to that point, so the ignored start pixel is really the one the line leaves from
                            float ex = eye.Pos.x, ez = eye.Pos.z, mdx = hu.Pos.x - ex, mdz = hu.Pos.z - ez;
                            _pairs.Add(new Pair
                            {
                                Key = Key(eid, hu.Eid), Ex = ex, Ey = eye.Y, Ez = ez, Tx = hu.Pos.x, Ty = hu.Pos.y + HeliAim, Tz = hu.Pos.z,
                                Dist = MathF.Sqrt(mdx * mdx + mdz * mdz), K0 = eye.K0, Kt = hu.Kt, Infantry = infantry, Eye = eye
                            });
                        }
                    }
            }
            _skippedPairs += skipped;
            if (_pairs.Count == 0) { PublishEmpty(true); _cycles++; return; }
            _cycleMasked = new HashSet<long>(_pairs.Count);
            _cycleEval = new HashSet<long>(_pairs.Count);
            _cycleStart = now;
            _cycleRunning = true;
        }

        static void RunCycle(float now)
        {
            _sw.Restart();
            var masked = _cycleMasked; var eval = _cycleEval;
            while (_pairIdx < _pairs.Count)
            {
                var p = _pairs[_pairIdx++];
                bool full = _evaluated % FullCauseEvery == 0;
                double t0 = _sw.Elapsed.TotalMilliseconds;
                int cause = March(in p, full, out float forestRun, out int mask);
                double t1 = _sw.Elapsed.TotalMilliseconds;
                if (t1 - t0 > _marchMaxMs) _marchMaxMs = t1 - t0;
                eval.Add(p.Key);
                _evaluated++;
                _byCause[cause]++;
                int band = Math.Min(Bands - 1, (int)(p.Dist / 1000f));
                _bandEval[band]++;
                if (p.Infantry) { _evalInfantry++; if (cause != Clear) _maskedInfantry++; }
                if (cause != Clear) { masked.Add(p.Key); _bandMasked[band]++; }
                else if (forestRun > 0f) _clearWithForest++;
                if (full) FullStats(mask);
                if (t1 >= BudgetMs) break;
            }
            double ms = _sw.Elapsed.TotalMilliseconds;
            if (ms > _frameMaxMs) _frameMaxMs = ms;
            if (_pairIdx < _pairs.Count) return;
            Masked = masked;                                        // one assignment each: readers never see a set being filled
            Evaluated = eval;
            Interlocked.Exchange(ref _validUntil, Environment.TickCount64 + (long)ValidMs);
            _cycleMasked = null; _cycleEval = null;
            _cycleRunning = false;
            _cycles++;
            double s = now - _cycleStart;
            if (s > _cycleMaxS) _cycleMaxS = s;
        }

        static void FullStats(int mask)
        {
            _fullN++;
            if (mask == 0) return;
            if ((mask & (1 << CauseRelief)) != 0) { if (mask == 1 << CauseRelief) _fullRelief++; else _fullReliefOther++; }
            else if (mask == 1 << CauseForest) _fullForest++;
            else if (mask == 1 << CauseBuilding) _fullBuilding++;
            else _fullBuildingForest++;
        }

        /// Walks every pixel the line crosses from the eye to the helicopter on the managed copy (pure managed code). The lowest height of
        /// the line inside each pixel is compared with the obstacle; 48 m blocks whose highest obstacle is below the line are skipped.
        /// full = false: returns at the first cause; full = true: walks to the end and sets every cause bit in mask (statistics).
        static int March(in Pair p, bool full, out float forestRun, out int mask)
        {
            forestRun = 0f; mask = 0;
            if (p.Dist < 0.5f) return Clear;
            const double Inf = double.PositiveInfinity;
            double gx0 = (p.Ex - _ox) * _invDx + _roundX, gz0 = (p.Ez - _oz) * _invDz + _roundZ;
            double gx1 = (p.Tx - _ox) * _invDx + _roundX, gz1 = (p.Tz - _oz) * _invDz + _roundZ;
            double sx = gx1 - gx0, sz = gz1 - gz0;
            double ey = p.Ey, dy = p.Ty - p.Ey, dist = p.Dist;
            int w = _w, h = _h, bw = _bw, bs = 1 << BlockShift;
            float[] hgt = _height; byte[] ter = _terrain; float[] bmax = _blockMax;
            float add = _buildingAdd;
            byte fv = _forestVal, bv = _buildingVal;
            HashSet<int> own = p.Eye?.Own;
            int stepX = sx > 0 ? 1 : -1, stepZ = sz > 0 ? 1 : -1;
            bool xMoves = Math.Abs(sx) > 1e-9, zMoves = Math.Abs(sz) > 1e-9;
            double invSx = xMoves ? 1.0 / sx : 0.0, invSz = zMoves ? 1.0 / sz : 0.0;
            double tDeltaX = xMoves ? Math.Abs(invSx) : Inf, tDeltaZ = zMoves ? Math.Abs(invSz) : Inf;
            double t = 0.0;
            int px = (int)Math.Floor(gx0), pz = (int)Math.Floor(gz0);
            double tNextX = xMoves ? ((stepX > 0 ? px + 1 : px) - gx0) * invSx : Inf;
            double tNextZ = zMoves ? ((stepZ > 0 ? pz + 1 : pz) - gz0) * invSz : Inf;
            int lastBlock = -1, first = Clear;
            bool forestDone = false;
            int guard = 4 * (w + h) + 64;
            while (guard-- > 0)
            {
                if ((uint)px >= (uint)w || (uint)pz >= (uint)h) break;             // off the map: nothing more to test
                double tB = Math.Min(Math.Min(tNextX, tNextZ), 1.0);
                int bi = (pz >> BlockShift) * bw + (px >> BlockShift);
                if (bi != lastBlock)
                {
                    lastBlock = bi;
                    int bx0 = (px >> BlockShift) << BlockShift, bz0 = (pz >> BlockShift) << BlockShift;
                    double tbx = xMoves ? ((stepX > 0 ? bx0 + bs : bx0) - gx0) * invSx : Inf;
                    double tbz = zMoves ? ((stepZ > 0 ? bz0 + bs : bz0) - gz0) * invSz : Inf;
                    double tExit = Math.Min(Math.Min(tbx, tbz), 1.0);
                    double yLowBlock = dy >= 0 ? ey + dy * t : ey + dy * tExit;
                    if (yLowBlock > bmax[bi])
                    {
                        if (tExit >= 1.0) break;                                    // above every obstacle up to the helicopter
                        t = tExit;
                        double te = t + 1e-9;
                        px = (int)Math.Floor(gx0 + sx * te); pz = (int)Math.Floor(gz0 + sz * te);
                        tNextX = xMoves ? ((stepX > 0 ? px + 1 : px) - gx0) * invSx : Inf;
                        tNextZ = zMoves ? ((stepZ > 0 ? pz + 1 : pz) - gz0) * invSz : Inf;
                        continue;
                    }
                }
                int k = pz * w + px;
                if (k != p.K0 && k != p.Kt)
                {
                    byte tt = ter[k];
                    if (!(tt == bv && own != null && own.Contains(k)))
                    {
                        double yLow = dy >= 0 ? ey + dy * t : ey + dy * tB;
                        float g = hgt[k];
                        int cause = Clear;
                        if (tt == bv) { if (yLow < g + add) cause = CauseBuilding; }
                        else if (yLow < g) cause = CauseRelief;
                        else if (tt == fv && !forestDone && yLow < g + TreeHeight)
                        {
                            forestRun += (float)((tB - t) * dist);
                            if (forestRun > ForestMax) { cause = CauseForest; forestDone = true; }
                        }
                        if (cause != Clear)
                        {
                            if (!full) return cause;
                            if (first == Clear) first = cause;
                            mask |= 1 << cause;
                        }
                    }
                }
                if (tB >= 1.0) break;
                if (tNextX < tNextZ) { px += stepX; t = tNextX; tNextX += tDeltaX; }
                else { pz += stepZ; t = tNextZ; tNextZ += tDeltaZ; }
            }
            return first;
        }

        // ---------------------------------------------------------------- report

        static void Report(bool final)
        {
            if (!final && _stage == Stage.Waiting && _cycles == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_eyes.Count > 0)
            {
                List<int> old = null;
                foreach (var kv in _eyes) if (now - kv.Value.Time > 30f) (old ??= new List<int>()).Add(kv.Key);
                if (old != null) foreach (int k in old) _eyes.Remove(k);
            }
            // nothing new since the last line (no pair evaluated, same stage and errors): stay quiet until the final summary
            if (!final && _lastReport != null && _evaluated == _lastReportedEvaluated && _stage == _lastReportedStage && _errors == _lastReportedErrors) return;
            _lastReportedEvaluated = _evaluated; _lastReportedStage = _stage; _lastReportedErrors = _errors;
            var sb = new StringBuilder();
            sb.Append($"{(final ? "bilan" : "relevé")} : carte {_testResult}, bâtiments {_calibResult}, cycles {_cycles} (unités de la règle anti-hélico {_losCycles}, relevé anti-aérien de secours {_fallbackCycles}");
            if (_ownScans > 0) sb.Append($", dont {_ownScans} relevés faits ici");
            long masked = _byCause[CauseForest] + _byCause[CauseRelief] + _byCause[CauseBuilding];
            sb.Append($"), paires évaluées {_evaluated} dont vue masquée {masked}{(_evaluated > 0 ? $" ({100.0 * masked / _evaluated:0} %)" : "")} (première cause : ");
            for (int c = CauseForest; c <= CauseBuilding; c++) sb.Append($"{(c > CauseForest ? ", " : "")}{CauseNames[c]} {_byCause[c]}");
            sb.Append($"), vue {CauseNames[Clear]} mais avec un peu de forêt {_clearWithForest}, maintenant {Masked.Count} masquées sur {Evaluated.Count}");
            sb.Append($" ; infanterie (règle appliquée) {_evalInfantry} paires dont masquées {_maskedInfantry}{(_evalInfantry > 0 ? $" ({100.0 * _maskedInfantry / _evalInfantry:0} %)" : "")}, " +
                      $"véhicules (mesure seule, jamais bloqués) {_evaluated - _evalInfantry} dont masquées {masked - _maskedInfantry}");
            if (_evaluated > 0)
            {
                sb.Append(" ; par distance (évaluées/masquées) :");
                for (int b = 0; b < Bands; b++)
                    if (_bandEval[b] > 0) sb.Append($" {b}-{(b == Bands - 1 ? "+" : (b + 1).ToString())} km {_bandEval[b]}/{_bandMasked[b]}");
            }
            if (_fullN > 0)
                sb.Append($" ; toutes les causes sur 1 paire sur {FullCauseEvery} ({_fullN}) : forêt seule {_fullForest}, bâtiment seul {_fullBuilding}, bâtiment et forêt {_fullBuildingForest}, relief seul {_fullRelief}, relief et autre {_fullReliefOther}");
            sb.Append($" ; tireurs {_lastShooters}, hélicos en vol ennemis possibles {_lastHelisAir}");
            long air = _heliSamples - _heliLanded;
            if (air > 0) sb.Append($", hauteur des hélicos en vol min {_heliHeightMin:0} / moy {_heliHeightSum / air:0} / max {_heliHeightMax:0} m ({_heliLanded} relevés posés sur {_heliSamples})");
            else if (_heliSamples > 0) sb.Append($", hélicos tous posés ({_heliSamples} relevés)");
            if (_eyeComputed > 0)
            {
                sb.Append($" ; yeux calculés {_eyeComputed} (à côté d'un bâtiment {_eyeNear} dont dans la rue au niveau du sol {_eyeNearNotOn}, dans un bâtiment {_eyeInside} dont surélevés {_eyeElevated} et garnison trouvée {_eyeGarrison}, appels bâtiments {_garrisonCalls}, reportés {_garrisonDeferred}, hors carte {_eyeOffMap})");
                sb.Append($" ; écart position - sol (signé) sur un bâtiment [{Gaps(_gapBuilding)}] ailleurs [{Gaps(_gapOther)}]");
            }
            if (_recheckRuns > 0) sb.Append($" ; contrôles des bâtiments {_recheckRuns}, pixels changés {_recheckChanged}, recopies {_recopies}");
            sb.Append($" ; marche la plus longue {_marchMaxMs:0.000} ms, image la plus longue {_frameMaxMs:0.00} ms, cycle le plus long {_cycleMaxS:0.00} s");
            if (_skippedPairs > 0) sb.Append($", paires ignorées (plus de {MaxPairs}) {_skippedPairs}");
            sb.Append($" ; utilisable par la règle de l'infanterie : {(ReadyForGating ? "oui" : "non (" + GateReason + ")")}, erreurs {_errors}");
            string s = sb.ToString();
            if (!final && s == _lastReport) return;
            _lastReport = s;
            Log(s);
        }

        static string Gaps(long[] hist)
        {
            return $"<-1 m {hist[0]}, -1 à 1 m {hist[1]}, 1 à 2,5 m {hist[2]}, 2,5 à 6 m {hist[3]}, >6 m {hist[4]}";
        }
    }
}
