// RealismOverhaul - Obstacles, stage 0 (v0.20.x): MEASUREMENT ONLY, nothing in the game changes.
//  Player request: shells, rockets and missiles must explode on the trees and buildings they meet.
//  Before any gameplay code, this stage logs how the engine resolves impacts: the terrain types the hit detection
//  ignores, its check heights, the map pixel size, forest/building pixel heights and the number of live projectiles.
//  No constructor patch (Il2CppInterop cannot detour IL2CPP constructors in this game):
//   - 'statique' part: static members and GameConfig, logged once per game session at the first battle frame (no system instance needed);
//     the map is read 5 s into each battle from the per-battle sources first (GameController.MapMetaData, then the systems,
//     the static MapService/TargetSearchHelper caches last), retried while not initialised, and sampled only once per map object;
//   - 'instance' part: once per battle, the HitDetectionSystem / ShellLifetimeSystem instances are found by walking
//     GameController._instance._ecsLoader's system groups with the raw IL2CPP API (read-only pointer reads).
//  The ShellLifetimeSystem.Dispose postfix is only a "battle over" signal. EcsLoader._subGraph/_roadGraph are cached for later stages.
//  v0.23.0: the system walk also runs when the diagnostic preference is off, because the flare module (LeurresMesure) reads the live
//  ShellLifetimeSystem through ShellLifeSystem; only the log-only parts (constants, map sample, hit system values, projectile counts)
//  follow the preference.
//  Log lines start with [OBSTACLES].
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MelonLoader;
using HitDet = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.HitDetectionSystem;
using ShellLife = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellLifetimeSystem;
using MapMeta = Il2CppBrokenArrow.Client.Ecs.Navigation.MapMetaData;
using MapSvc = Il2CppBrokenArrow.Client.Ecs.Navigation.MapService;
using NavConst = Il2CppBrokenArrow.Client.Ecs.Navigation.NavigationConstants;
using NavGraph = Il2CppBrokenArrow.Core.Navigation.NavigationGraph;
using TargetHelper = Il2CppBrokenArrow.Client.Ecs.BattleSystem.TargetSearchHelper;
using Terrain = Il2CppBrokenArrow.Shared.Ecs.Enums.TerrainType;
using GameCtl = Il2CppBrokenArrow.Client.Ecs.Controllers.GameController;
using EcsLoad = Il2CppBrokenArrow.Client.Ecs.Controllers.EcsLoader;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using UVector3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Obstacles
    {
        internal static MelonPreferences_Entry<bool> Diagnostic;

        /// Navigation graphs of the current battle (EcsLoader._subGraph / _roadGraph), cached read-only for later stages; null outside a battle.
        internal static NavGraph SubGraph, RoadGraph;

        static HitDet _hit; static ShellLife _life;

        /// ShellLifetimeSystem of the battle in progress (found by the system walk), or null. Main thread only. It is dropped when the
        /// GameController or the EcsLoader changes, or when its Dispose is seen; callers must still catch errors near a battle end.
        internal static ShellLife ShellLifeSystem
        {
            get
            {
                var life = _life;
                if (life == null || _gcPtr == IntPtr.Zero) return null;
                try
                {
                    var gc = GameCtl._instance;
                    if (gc == null || gc.Pointer != _gcPtr) return null;
                    return life.Pointer == _staleLifePtr ? null : life;
                }
                catch { return null; }
            }
        }
        static IntPtr _gcPtr, _loaderPtr, _staleLifePtr;
        static long _disposedLifePtr;          // written by the Dispose postfix (any thread), read by Frame
        static long _handledDisposePtr;
        static float _next, _nextCount, _battleSeen, _nextWalk;
        static bool _staticLogged, _mapLogged, _hitLogged, _walkPending, _graphLogged;
        static bool _staticDone;               // session level: constants and GameConfig logged once (never reset by ForgetBattle)
        static IntPtr _sampledMapPtr; static int _sampledMaxX, _sampledMaxY;   // session level: last map object sampled
        static int _mapTries, _walkTries;
        static int _peak, _samples, _errors; static long _sum; static double _msMax;
        const int MaxErrors = 20, MaxMapTries = 120, MaxWalkTries = 24;

        // system walk
        const int MaxDepth = 6, MaxNodes = 400, MaxArray = 256;
        static readonly int ArrayFirst = 4 * IntPtr.Size;   // Il2CppArray: klass, monitor, bounds, max_length, then the elements
        static int _nodes;
        static IntPtr _foundHit, _foundLife;
        static string _hitPath, _lifePath;
        static readonly HashSet<string> _leafSet = new();
        static readonly List<string> _leafNames = new();
        static readonly string[] LoaderFields =
        {
            "_battleSystem", "_cleanupSystem", "_postUpdateSystem", "_initSystem", "_codeLodSystem", "_audioCommandSystem",
            "_animationSystem", "_cameraSystem", "_postCameraSystem", "_inputSystem", "_commandsSystem", "_moveSystem",
            "_planeSystem", "_selectionSystem", "_renderSystem", "_uiSystem", "_networkSystem", "_aiSystem", "_runtimeValidatorSystem"
        };

        static void Log(string s) => Mod.Log.Msg("[OBSTACLES] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Obstacles");
            Diagnostic = c.CreateEntry("MesuresObstacles", true, description: Build.Desc("Étape 0 : note dans le journal comment le jeu gère les impacts (arbres, bâtiments, projectiles en vol). Ne change rien au jeu."));
        }

        /// Per-mission statistics only; the battle itself is followed through the GameController / EcsLoader pointers.
        internal static void ResetSession()
        {
            _nextCount = 0; _peak = 0; _samples = 0; _sum = 0; _msMax = 0;
        }

        /// Dispose postfix: only remembers which ShellLifetimeSystem was released; Frame does the rest on the main thread.
        internal static void OnLifeDispose(ShellLife s)
        {
            if (s == null) return;
            Interlocked.Exchange(ref _disposedLifePtr, s.Pointer.ToInt64());
        }

        /// Called every frame in campaign; does its work at most once per second. The system walk always runs (the flare module needs
        /// the live ShellLifetimeSystem); the log-only measurements follow the diagnostic preference.
        internal static void Frame()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            if (!Planif.Take(ref _wait)) return;                 // one heavy module job per frame (Planif.cs); the period is unchanged
            _next = now + 1f;
            Tick(now, Diagnostic != null && Diagnostic.Value);
        }

        static int _wait;

        /// Allocation-free gate above: the closures of the tick live here.
        static void Tick(float now, bool diag)
        {
            var gc = GameCtl._instance;
            IntPtr gcPtr = gc == null ? IntPtr.Zero : gc.Pointer;
            if (gcPtr != _gcPtr) { ForgetBattle(); _gcPtr = gcPtr; _battleSeen = now; }
            if (gcPtr == IntPtr.Zero || _errors > MaxErrors) return;

            long disposed = Interlocked.Read(ref _disposedLifePtr);
            if (disposed != _handledDisposePtr) { _handledDisposePtr = disposed; OnDisposeSeen(new IntPtr(disposed), now); }

            if (diag && !_staticLogged) { _staticLogged = true; if (!_staticDone) Safe("statique", LogStatic); }
            if (now - _battleSeen < 5f) return;   // map and systems: only once the battle had time to initialise
            if (diag && !_mapLogged && _mapTries < MaxMapTries) { _mapTries++; Safe("carte", () => TryLogMap(gc)); }
            Safe("instance", () => InstanceStage(gc, now, diag));
        }

        static void LogSummary()
        {
            if (_samples > 0)
                Log($"bilan de la bataille : pic de {_peak} projectiles en vol, moyenne {(double)_sum / _samples:0.0} sur {_samples} relevés");
        }

        /// New or finished battle: drop every reference and per-battle flag (the next battle is measured from scratch).
        static void ForgetBattle()
        {
            LogSummary();   // a release and a controller change in the same tick would otherwise lose the summary
            _hit = null; _life = null; SubGraph = null; RoadGraph = null;
            _gcPtr = IntPtr.Zero; _loaderPtr = IntPtr.Zero; _staleLifePtr = IntPtr.Zero;
            _handledDisposePtr = Interlocked.Read(ref _disposedLifePtr);   // a release seen before this battle is not about it
            _staticLogged = false; _mapLogged = false; _hitLogged = false; _walkPending = false; _graphLogged = false;
            _mapTries = 0; _walkTries = 0; _nextWalk = 0; _nextCount = 0; _errors = 0;
            _peak = 0; _samples = 0; _sum = 0; _msMax = 0;
        }

        static void OnDisposeSeen(IntPtr p, float now)
        {
            if (p == IntPtr.Zero) return;
            if (_life != null && _life.Pointer != p) return;   // another world's system, not ours
            LogSummary();
            _staleLifePtr = p; _hit = null; _life = null; _hitLogged = false;
            _peak = 0; _samples = 0; _sum = 0; _msMax = 0;
            _walkPending = _loaderPtr != IntPtr.Zero; _walkTries = 0; _nextWalk = now + 5f;
            Log("fin de bataille détectée (système des projectiles libéré) : systèmes oubliés");
        }

        static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                _errors++;
                if (_errors <= 3 || _errors == MaxErrors + 1)
                    Mod.Log.Warning($"[OBSTACLES] mesure '{what}' impossible : {e.GetBaseException().Message}" + (_errors > MaxErrors ? " (mesures arrêtées pour cette bataille)" : ""));
            }
        }

        /// Reads a property (or field) by reflection; obj may be null for static members. Never throws.
        static object Val(object obj, Type t, string name)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            try
            {
                var p = t.GetProperty(name, all);
                if (p != null)
                {
                    var getter = p.GetGetMethod(true);
                    if (getter == null) return "?";
                    if (!getter.IsStatic && obj == null) return "instance";
                    return getter.Invoke(getter.IsStatic ? null : obj, null) ?? "null";
                }
                var f = t.GetField(name, all);
                if (f == null) return "?";
                if (!f.IsStatic && obj == null) return "instance";
                return f.GetValue(f.IsStatic ? null : obj) ?? "null";
            }
            catch (Exception e) { return "erreur:" + e.GetBaseException().GetType().Name; }
        }

        static string Num(object v) => v is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToString(v);
        static string Present(object v) => v is string s ? s : "présent";

        // ------------------------------------------------------------------ statique (no system instance)

        static void LogStatic()
        {
            var t = typeof(HitDet);
            Log($"[statique] détection des impacts : réglages de combat {Present(Val(null, t, "_battleSettings"))}, réduction des collisions bâtiments = {Num(Val(null, t, "_buildingsHitDetectionColliderReduction"))}");
            Log($"[statique] constantes : recherche d'arbres toutes les {Num(Val(null, t, "TREE_SEARCH_DELAY"))} s, contrôle trajectoire/bâtiments {Num(Val(null, t, "TRAJECTORY_BUILDING_CHECK_DISTANE"))} m, " +
                $"marge hauteur carte {Num(Val(null, t, "MAP_MAX_HEIGHT_SAFETY_MARGIN"))}, marge hauteur bâtiments {Num(Val(null, t, "BUILDING_MAX_HEIGHT_SAFETY_MARGIN"))}, " +
                $"multiplicateur distance par image {Num(Val(null, t, "FRAME_DISTANCE_MULTIPLIER"))}, recherche minimale {Num(Val(null, t, "MINIMAL_SEARCH"))}, " +
                $"préfiltre missiles {Num(Val(null, t, "MISSILE_EXTRA_PRE_FILTER_MULTIPLIER"))}, préfiltre bâtiments {Num(Val(null, t, "BUILDINGS_EXTRA_PRE_FILTER_MULTIPLIER"))}, " +
                $"préfiltre unités {Num(Val(null, t, "UNITS_PREFILTER_HITBOX_MULTIPLIER"))}");

            var cfg = GameCfg.Instance;
            if (cfg == null) { Log("[statique] configuration du jeu introuvable"); return; }
            var bc = cfg.BuildingsConfig;
            Log(bc == null ? "[statique] configuration des bâtiments introuvable"
                : $"[statique] bâtiments : réduction des collisions {Num(bc.HitColliderReduction)}, seuil de dégâts {bc.BuildingDamageThreshold}, plancher de dégâts {Num(bc.DamageModifierFloor)}, dégâts par soldat {Num(bc.DamageModifierPerSoldier)}");
            var bs = cfg.BattleSystemSettings;
            Log(bs == null ? "[statique] réglages de combat introuvables"
                : $"[statique] combat : G {Num(bs.G)}, dispersion min {Num(bs.MIN_POINT_DISPERSION)}, multiplicateur dispersion max {Num(bs.MAX_POINT_DISPERSION_MULTIPLIER)}, " +
                  $"angle toit des bâtiments {Num(bs.BUILDINGS_TOP_ARMOR_AIMING_TRESHOLD_ANGLE)}, marges des bombes {Num(bs.BOMB_HORIZONTAL_MARGIN)}/{Num(bs.BOMB_UPPER_MARGIN)}, " +
                  $"début contrôle de vue missile lofté {Num(bs.MISSILE_LOFTING_LOS_CHECK_START_TRAJECTORY_PROPORTION)}, hauteur missile de croisière {Num(bs.CRUISE_MISSILE_TARGET_HEIGHT)}, " +
                  $"taille avion en plus (obus non guidés) {Num(bs.ADDITIONAL_PLANE_SIZE_FOR_UNGUIDED_SHELLS)}");
            _staticDone = true;   // complete: not repeated in later battles (a missing GameConfig is retried next battle)
        }

        static void TryLogMap(GameCtl gc)
        {
            // per-battle sources first; the static MapService/TargetSearchHelper caches can still hold the previous battle's map
            string src = null; MapMeta m = null;
            if (gc != null) { m = Val(gc, typeof(GameCtl), "MapMetaData") as MapMeta; if (m != null) src = "GameController.MapMetaData"; }
            if (m == null && _life != null) { m = Val(_life, typeof(ShellLife), "_mapMeta") as MapMeta; if (m != null) src = "ShellLifetimeSystem._mapMeta"; }
            if (m == null && _hit != null) { m = Val(_hit, typeof(HitDet), "_map") as MapMeta; if (m != null) src = "HitDetectionSystem._map"; }
            if (m == null) { m = Val(null, typeof(MapSvc), "_map") as MapMeta; if (m != null) src = "MapService._map"; }
            if (m == null) { m = Val(null, typeof(TargetHelper), "_map") as MapMeta; if (m != null) src = "TargetSearchHelper._map"; }
            if (m == null)
            {
                if (_mapTries >= MaxMapTries) Log("carte introuvable (GameController, systèmes, MapService, TargetSearchHelper)");
                return;
            }

            // not initialised yet (InitAsync still running): try again next second, within MaxMapTries
            var mt = typeof(MapMeta);
            object ox = Val(m, mt, "_maxX"), oy = Val(m, mt, "_maxY"), px = Val(m, mt, "_pixels");
            int maxX = ox is int ix ? ix : 0, maxY = oy is int iy ? iy : 0;
            bool pixels = px != null && !(px is string);   // Val gives a string ("null", "?", "erreur:...") when unreadable
            if (maxX <= 0 || maxY <= 0 || !pixels)
            {
                if (_mapTries >= MaxMapTries) Log($"carte (source {src}) jamais prête : maxX={maxX}, maxY={maxY}, pixels {(pixels ? "présents" : "absents")}");
                return;
            }

            _mapLogged = true;   // set before the sampling: a failing sample is never repeated
            IntPtr mp = m.Pointer;
            if (mp == _sampledMapPtr && maxX == _sampledMaxX && maxY == _sampledMaxY)
            {
                Log($"[statique] carte (source {src}) : même carte que la bataille précédente, échantillonnage non refait");
                return;
            }
            _sampledMapPtr = mp; _sampledMaxX = maxX; _sampledMaxY = maxY;
            LogMap(m, src);
        }

        static void LogMap(MapMeta m, string src)
        {
            var mt = typeof(MapMeta);
            var ms = m.MapSettings;
            Log($"[statique] carte (source {src}) : taille {(ms == null ? "?" : ms.MapSize.ToString())}, texture {(ms == null ? "?" : ms.TextureSize.ToString())}, hauteur max réglage {(ms == null ? "?" : Num(ms.MaxHeight))}, " +
                $"hauteur min/max {Num(Val(m, mt, "MinMapHeight"))}/{Num(Val(m, mt, "MaxMapHeight"))}, delta {Val(m, mt, "_delta")}, delta hauteurs {Val(m, mt, "_heightSampleDelta")}, " +
                $"stride {Val(m, mt, "_stride")}, maxX {Val(m, mt, "_maxX")}, maxY {Val(m, mt, "_maxY")}, hauteur de forêt (navigation) {Num(Val(null, typeof(NavConst), "FOREST_HEIGHT"))}");
            Log("types de terrain : " + string.Join(", ", Enum.GetValues(typeof(Terrain)).Cast<object>().Select(v => $"{v}={Convert.ToInt32(v)}")));
            SamplePixels(m);
        }

        /// Samples the map on a coarse grid: terrain type, pixel height and the engine's own terrain height queries,
        /// to learn whether a forest/building pixel height already includes the canopy/roof.
        static void SamplePixels(MapMeta m)
        {
            var mt = typeof(MapMeta);
            object ox = Val(m, mt, "_maxX"), oy = Val(m, mt, "_maxY");
            int maxX = ox is int ix ? ix : 0, maxY = oy is int iy ? iy : 0;
            var convert = mt.GetMethods().FirstOrDefault(x => x.Name == "ConvertPosition" && x.GetParameters().Length == 3);
            var atHeight = mt.GetMethods().FirstOrDefault(x => x.Name == "GetTerrainAtHeight" && x.GetParameters().Length == 3);
            var interp = mt.GetMethods().FirstOrDefault(x => x.Name == "GetInterpolatedTerrainHeight" && x.GetParameters().Length == 7);
            if (maxX <= 0 || maxY <= 0 || convert == null || atHeight == null) { Log($"échantillonnage impossible (maxX={maxX}, maxY={maxY}, méthodes {convert != null}/{atHeight != null}/{interp != null})"); return; }
            // TerrainType is a numbered enum (Vegetation=1, Forest=2, Buildings=3...) with bridge flags on top: compare values, not bits
            int forestVal = Convert.ToInt32(Enum.Parse(typeof(Terrain), "Forest")), buildVal = Convert.ToInt32(Enum.Parse(typeof(Terrain), "Buildings")), vegVal = Convert.ToInt32(Enum.Parse(typeof(Terrain), "Vegetation"));
            var bf = Val(m, mt, "BRIDGE_FLAG_FILTER");
            int bridgeMask = bf is Enum ? Convert.ToInt32(bf) : 0;
            int step = Math.Max(1, Math.Max(maxX, maxY) / 60);   // about 60x60 points: keeps the main-thread stall short
            int nForest = 0, nBuild = 0, nVeg = 0, nTotal = 0, shownF = 0, shownB = 0, shownP = 0;
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            for (int y = step / 2; y < maxY; y += step)
                for (int x = step / 2; x < maxX; x += step)
                {
                    var pos = (UVector3)convert.Invoke(m, new object[] { x, y, 0f });
                    var args = new object[] { pos, Enum.ToObject(typeof(Terrain), 0), 0f };
                    atHeight.Invoke(m, args);
                    int tt = Convert.ToInt32(args[1]);
                    int baseT = bridgeMask != 0 ? (tt & ~bridgeMask) : tt;
                    float th = Convert.ToSingle(args[2]);
                    nTotal++;
                    bool f = baseT == forestVal, b = baseT == buildVal, v = baseT == vegVal;
                    if (f) nForest++;
                    if (b) nBuild++;
                    if (v) nVeg++;
                    bool show = (f && shownF < 6) || (b && shownB < 6) || (!f && !b && !v && shownP < 3);
                    if (!show) continue;
                    if (f) shownF++; else if (b) shownB++; else shownP++;
                    string ground = "?", withBuild = "?";
                    if (interp != null)
                    {
                        try
                        {
                            var a1 = new object[] { pos, args[1], th, false, false, true, 1000f };
                            ground = Num(interp.Invoke(m, a1));
                            var a2 = new object[] { pos, args[1], th, true, false, true, 1000f };
                            withBuild = Num(interp.Invoke(m, a2));
                        }
                        catch (Exception e) { ground = withBuild = "erreur:" + e.GetBaseException().GetType().Name; }
                    }
                    sb.Append($"\n    ({x},{y}) monde=({pos.x:0},{pos.z:0}) terrain={args[1]} (brut {tt}, base {baseT}) hauteur pixel={Num(th)} sol interpolé={ground} avec bâtiments={withBuild}");
                }
            Log($"échantillon de {nTotal} points (pas {step} pixels, {sw.Elapsed.TotalMilliseconds:0} ms, filtre des ponts {bf} = {bridgeMask}) : forêt {nForest}, végétation {nVeg}, bâtiments {nBuild}" + sb);
        }

        // ------------------------------------------------------------------ instance (systems found by walking the EcsLoader)

        static void InstanceStage(GameCtl gc, float now, bool diag)
        {
            EcsLoad loader = gc._ecsLoader;
            IntPtr lp = loader == null ? IntPtr.Zero : loader.Pointer;
            if (lp != _loaderPtr)
            {
                _loaderPtr = lp; _hit = null; _life = null; _hitLogged = false; _staleLifePtr = IntPtr.Zero;
                SubGraph = null; RoadGraph = null; _graphLogged = false;
                _walkPending = lp != IntPtr.Zero; _walkTries = 0; _nextWalk = now;
            }
            if (lp == IntPtr.Zero) return;

            if (SubGraph == null)
            {
                SubGraph = loader._subGraph; RoadGraph = loader._roadGraph;
                if (SubGraph != null && !_graphLogged)
                {
                    _graphLogged = true;
                    Log($"graphe de navigation mémorisé (graphe des routes {(RoadGraph != null ? "présent" : "absent")})");
                }
            }

            if (_walkPending && now >= _nextWalk) WalkSystems(lp, now);
            if (!diag) return;                    // measurement logs only below
            if (_hit != null && !_hitLogged) { _hitLogged = true; Safe("détection", LogHitSystem); }
            if ((_life != null || _hit != null) && now >= _nextCount) { _nextCount = now + 10f; Safe("projectiles", CountProjectiles); }
        }

        static void WalkSystems(IntPtr loaderPtr, float now)
        {
            _walkTries++;
            _nodes = 0; _foundHit = IntPtr.Zero; _foundLife = IntPtr.Zero; _hitPath = null; _lifePath = null;
            _leafSet.Clear(); _leafNames.Clear();
            var sw = Stopwatch.StartNew();
            IntPtr lk = IL2CPP.il2cpp_object_get_class(loaderPtr);
            if (lk == IntPtr.Zero) { _walkPending = false; Log("recherche des systèmes impossible (classe du chargeur illisible)"); return; }

            var top = new StringBuilder();
            bool battleReady = false;
            foreach (var name in LoaderFields)
            {
                IntPtr sys = ReadRefField(loaderPtr, lk, name, out string why);
                if (name == "_battleSystem") battleReady = sys != IntPtr.Zero;
                top.Append(name).Append('=').Append(sys == IntPtr.Zero ? (why ?? "null") : ClassName(IL2CPP.il2cpp_object_get_class(sys))).Append(' ');
                if (sys != IntPtr.Zero) Walk(sys, name, 0);
                if (_foundHit != IntPtr.Zero && _foundLife != IntPtr.Zero) break;
            }
            sw.Stop();

            if (_foundHit == IntPtr.Zero && _foundLife == IntPtr.Zero)
            {
                if (!battleReady && _walkTries < MaxWalkTries) { _nextWalk = now + 5f; return; }   // battle still loading
                _walkPending = false;
                Log($"systèmes introuvables ({_nodes} nœuds visités, {sw.Elapsed.TotalMilliseconds:0.0} ms) : champs = {top}; classes rencontrées = {string.Join(", ", _leafNames.Take(60))}");
                return;
            }
            if (_foundLife != IntPtr.Zero && _foundLife == _staleLifePtr)
            {
                // the loader still holds the systems of the finished battle: wait for the next one
                if (_walkTries == 1) Log("systèmes trouvés mais la bataille est terminée : attente de la bataille suivante");
                if (_walkTries >= MaxWalkTries) { _walkPending = false; Log("systèmes toujours ceux de la bataille terminée : recherche abandonnée jusqu'à la bataille suivante"); }
                else _nextWalk = now + 15f;
                return;
            }

            _walkPending = false;
            if (_foundHit != IntPtr.Zero) _hit = new HitDet(_foundHit);
            if (_foundLife != IntPtr.Zero) _life = new ShellLife(_foundLife);
            Log($"systèmes trouvés : chemin={_hitPath ?? "introuvable"} classe=HitDetectionSystem ; chemin={_lifePath ?? "introuvable"} classe=ShellLifetimeSystem " +
                $"({_nodes} nœuds visités, {sw.Elapsed.TotalMilliseconds:0.0} ms)");
            if (_foundHit == IntPtr.Zero || _foundLife == IntPtr.Zero)
                Log($"champs visités : {top}; classes rencontrées = {string.Join(", ", _leafNames.Take(60))}");
        }

        /// Recursive walk of DefaultEcs groups: SequentialSystem / ProfilerSequentialSystem (_systems), ParallelSystem (_mainSystem + _systems).
        /// Anything else is a leaf. Only reference fields and arrays whose class name ends with [] are ever dereferenced.
        static void Walk(IntPtr obj, string path, int depth)
        {
            if (obj == IntPtr.Zero || depth > MaxDepth || _nodes >= MaxNodes) return;
            if (_foundHit != IntPtr.Zero && _foundLife != IntPtr.Zero) return;
            _nodes++;
            IntPtr k = IL2CPP.il2cpp_object_get_class(obj);
            if (k == IntPtr.Zero) return;
            string name = ClassName(k);
            string here = path + "(" + name + ")";
            if (name == "HitDetectionSystem") { if (_foundHit == IntPtr.Zero) { _foundHit = obj; _hitPath = here; } return; }
            if (name == "ShellLifetimeSystem") { if (_foundLife == IntPtr.Zero) { _foundLife = obj; _lifePath = here; } return; }

            bool par = name.StartsWith("ParallelSystem", StringComparison.Ordinal);
            bool seq = !par && (name.StartsWith("SequentialSystem", StringComparison.Ordinal) || name.StartsWith("ProfilerSequentialSystem", StringComparison.Ordinal));
            if (!par && !seq)
            {
                if (_leafNames.Count < 200 && _leafSet.Add(name)) _leafNames.Add(name);
                return;
            }

            if (par) Walk(ReadRefField(obj, k, "_mainSystem", out _), here + "/_mainSystem", depth + 1);

            IntPtr arr = ReadRefField(obj, k, "_systems", out _);
            if (arr == IntPtr.Zero) return;
            IntPtr ak = IL2CPP.il2cpp_object_get_class(arr);
            if (ak == IntPtr.Zero || !ClassName(ak).EndsWith("[]", StringComparison.Ordinal)) return;
            long len = Convert.ToInt64(IL2CPP.il2cpp_array_length(arr));
            if (len <= 0 || len > MaxArray) return;
            for (int i = 0; i < (int)len; i++)
            {
                if (_nodes >= MaxNodes) return;
                IntPtr el = Marshal.ReadIntPtr(arr, ArrayFirst + i * IntPtr.Size);
                if (el != IntPtr.Zero) Walk(el, here + "/" + i, depth + 1);
            }
        }

        /// Reads an instance reference field (own class or a parent) as a raw object pointer; Zero when absent, static or not a reference.
        static IntPtr ReadRefField(IntPtr obj, IntPtr klass, string name, out string why)
        {
            why = null;
            if (obj == IntPtr.Zero || klass == IntPtr.Zero) { why = "null"; return IntPtr.Zero; }
            IntPtr field = IntPtr.Zero;
            int guard = 0;
            for (IntPtr c = klass; c != IntPtr.Zero && field == IntPtr.Zero && guard < 16; c = IL2CPP.il2cpp_class_get_parent(c), guard++)
                field = IL2CPP.il2cpp_class_get_field_from_name(c, name);
            if (field == IntPtr.Zero) { why = "champ absent"; return IntPtr.Zero; }
            if ((Convert.ToInt64(IL2CPP.il2cpp_field_get_flags(field)) & 0x10) != 0) { why = "statique"; return IntPtr.Zero; }   // FIELD_ATTRIBUTE_STATIC
            IntPtr type = IL2CPP.il2cpp_field_get_type(field);
            if (type == IntPtr.Zero) { why = "type illisible"; return IntPtr.Zero; }
            int te = Convert.ToInt32(IL2CPP.il2cpp_type_get_type(type));
            // Il2CppTypeEnum: STRING 0x0e, CLASS 0x12, ARRAY 0x14, GENERICINST 0x15 (may be a struct), OBJECT 0x1c, SZARRAY 0x1d
            bool reference = te == 0x0e || te == 0x12 || te == 0x14 || te == 0x1c || te == 0x1d;
            if (te == 0x15)
            {
                IntPtr fc = IL2CPP.il2cpp_class_from_type(type);
                reference = fc != IntPtr.Zero && !Convert.ToBoolean(IL2CPP.il2cpp_class_is_valuetype(fc));
            }
            if (!reference) { why = "type " + te; return IntPtr.Zero; }
            long off = Convert.ToInt64(IL2CPP.il2cpp_field_get_offset(field));
            if (off < IntPtr.Size * 2 || off > 65536) { why = "décalage " + off; return IntPtr.Zero; }
            return Marshal.ReadIntPtr(obj, (int)off);
        }

        static string ClassName(IntPtr klass) => klass == IntPtr.Zero ? "?" : Str(IL2CPP.il2cpp_class_get_name(klass));
        static string Str(IntPtr p) => p == IntPtr.Zero ? "?" : (Marshal.PtrToStringAnsi(p) ?? "?");
        static string Str(string s) => s ?? "?";

        static void LogHitSystem()
        {
            var h = _hit; var t = typeof(HitDet);
            if (h == null) return;
            var filter = Val(h, t, "_terrainFlagExcludeFilter");
            string filterInt = filter is Enum ? Convert.ToInt32(filter).ToString() : "?";
            Log($"[instance] détection des impacts : terrains ignorés = {filter} ({filterInt}), hauteur de contrôle carte = {Num(Val(h, t, "_mapCheckHeight"))}, " +
                $"hauteur de contrôle bâtiments = {Num(Val(h, t, "_buildingCheckHeight"))}, arbre des unités {Present(Val(h, t, "_unitOcTree"))}, carte {Present(Val(h, t, "_map"))}, " +
                $"réglages de combat {Present(Val(h, t, "_battleSettings"))}");
        }

        static void CountProjectiles()
        {
            string src = "ShellLifetimeSystem";
            object set = _life != null ? Val(_life, typeof(ShellLife), "_set") : null;
            if ((set == null || set is string) && _hit != null) { set = Val(_hit, typeof(HitDet), "_set"); src = "HitDetectionSystem"; }
            if (set == null || set is string) return;
            var sw = Stopwatch.StartNew();
            int n = -1;
            var cp = set.GetType().GetProperty("Count");
            if (cp != null) n = Convert.ToInt32(cp.GetValue(set));
            sw.Stop();
            if (n < 0) { Log("nombre de projectiles illisible"); _errors = MaxErrors + 1; return; }
            _samples++; _sum += n; if (n > _peak) _peak = n;
            _msMax = Math.Max(_msMax, sw.Elapsed.TotalMilliseconds);
            if (_samples == 1 || _samples % 6 == 0)
                Log($"[instance] projectiles en vol ({src}) : {n} maintenant, pic {_peak}, moyenne {(double)_sum / _samples:0.0} sur {_samples} relevés (lecture {_msMax:0.000} ms max)");
        }
    }

    [HarmonyPatch(typeof(ShellLife), nameof(ShellLife.Dispose))]
    static class Patch_ObstaclesShellLifetimeDispose
    {
        static bool Prepare() => Obstacles.Diagnostic == null || Obstacles.Diagnostic.Value;
        static void Postfix(ShellLife __instance) => Guard.Run("Obstacles.ShellDispose", () => Obstacles.OnLifeDispose(__instance));
    }
}
