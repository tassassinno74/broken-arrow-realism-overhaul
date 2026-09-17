// RealismOverhaul - Spawns (v0.22.5 "bulle du joueur")
//  Solo campaign: an enemy script reinforcement must not appear near the player's own units (bubble RayonJoueur around his live units,
//  a 60 s trail, sticky until everything is 500 m further for 60 s). Reinforcements far from him are never touched (they may come from far).
//  ModeRenforts = auto: a call inside the bubble is swapped to another enemy tag where the script really landed units of the same class
//  in this battle (landing within 30 m), outside the bubble + 500 m, on the map / play zone, never next to a zone he owns.
//  Until one swap has been verified in the battle, one batch at a time; circuit breaker and per-group respawn breaker.
//  ModeRenforts = observation: every decision is written to the log, nothing is changed. off: only the zone state is followed.
//  Calls without a tag (spawn point or position only) are never changed: they are logged as unprotected.
//  Log-only hooks: move complex / unload / simple move orders, revealed reserves, multi-unit waves, exact spawn place, landings.
//  Never deletes, delays or skips a call; never touches allied calls, Quantity, UnitInfo, UnitGroup, CargoUnitGroup, Assign, SpawnPointUID or the owner.
//  v0.23.0: the trigger test no longer calls TriggerDataExt.IsPointInsideTrigger (NullReferenceException in every mission, so the trigger
//  rule was silently off). Own geometry from TriggerData (type, position, scale, rotation); the half/full-size convention is unknown, so a
//  new place must be inside the same triggers under BOTH readings. Triggers that cannot be read limit a swap to 1000 m. Every unit a script
//  spawn node lands is reported to Missions.NoteSpawn (script unit protection). MissionsExclues is ignored: no feature is ever switched
//  off for one mission (player rule 2026-09-16).
//  v0.24.0: ground optics now reach 2 to 8 km, so a place 2500 m away can be in plain sight. The search runs twice: first only places
//  outside the ground sight of every live unit of his, then, if no tag of the battle qualifies, the distance rule alone as before.
//  The second pass is the old behaviour unchanged, so no swap that was legal becomes impossible; the battle summary counts both.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.Spawn;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.MissionEditor.Data;
using Il2CppBrokenArrow.MissionEditor.Data.Meta;
using Il2CppBrokenArrow.MissionEditor.Data.SpawnPoint;
using Il2CppBrokenArrow.MissionEditor.Data.Trigger;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.MissionEditor.Storage;
using Il2CppBrokenArrow.ScriptEngine.Nodes.Spawn;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using MapSvc = Il2CppBrokenArrow.Client.Ecs.Navigation.MapService;
using MoveComplexSys = Il2CppBrokenArrow.MissionEditor.Systems.MoveComplexSystem;
using GroupNameSys = Il2CppBrokenArrow.MissionEditor.Systems.UnitGroupAndNameSystem;
using MoveComplexCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.MoveComplexData;
using MoveSimpleCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.MoveSimpleData;
using UnloadCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.UnloadCommandData;
using PlayZoneCtl = Il2CppBrokenArrow.Client.Ecs.Controllers.PlayableZone;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Spawns
    {
        internal static MelonPreferences_Entry<bool> Enabled;
        internal static MelonPreferences_Entry<string> Mode, ExMissions;
        internal static MelonPreferences_Entry<float> Radius, StartDelay, MaxShift, PlayerMin, PlayerRadius;
        static MelonPreferences_Entry<bool> _migrated, _migrated22;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;

        const string GuardVersion = "0.24.0";
        const float TrigUnknownShift = 1000f;
        const float MergeDist = 60f, LossDebounce = 8f, BatchWindow = 12f, MatchDist = 250f, VerifyDist = 60f, VerifyWindow = 45f,
                    FreeLogDist = 1500f, VisibleFor = 10f, RespawnWindow = 90f, SpawnPointMin = 1500f,
                    VerifySpeed = 20f, VerifyMax = 200f, ExitMargin = 500f, StickyHold = 60f, TrailEvery = 10f, TrailKeep = 60f,
                    LandDist = 30f, TagBusy = 20f, PlayerZoneMin = 1200f, MaxBatchSpan = 30f;
        const int CapCalls = 200, CapResults = 120, CapOrders = 80, CapReserves = 60, CapFree = 80, CapExact = 120, CapZones = 120,
                  CapLanded = 150, QueueMax = 4000, DrainMax = 600;

        // hook event kinds (index into _hk)
        internal const int EvSingle = 0, EvWave = 1, EvMoveComplex = 2, EvUnload = 3, EvRunMove = 4, EvGroupVisible = 5, EvUnitVisible = 6, EvSpawned = 7, EvError = 8, EvLanded = 9;

        /// Primitives copied inside a hook; processed later in Frame on the main thread.
        internal sealed class Ev
        {
            public int Kind, I1, I2, I3, Tries; public string S1, S2; public bool B1, B2; public V3 V1, V2; public IntPtr P; public long Tick;
        }

        sealed class Zone
        {
            public int Id; public readonly List<int> Uids = new();
            public V3 Pos; public float Radius = 300f, LastSeen = -100f, HeldSince = -1f, LossSince = -1f;
            public int Owner = -3; public bool EverNotPlayer, Held;
            public string LuaRaw, BusRaw;
            public int PollUid; public string PollLua, PollBus;
        }
        sealed class Req
        {
            public int Id, Owner, Side, TagUid, UnitId, Units, SeenN, ExactN, LandedN, PointUid; public string Group = "", Cls = "?", Place = "";
            public bool Enemy, TagOk, Deck, Moved, Air; public float T, NearD = -1f;
            public int SentTag; public V3 SentPos;               // tag (and its position) this call was really sent to by Apply
            public V3 TargetPos, TagPos, Expect, Origin; public Batch B; public UnitsRow Row;
        }
        sealed class Batch
        {
            public int Id, Side, TagUid, Expected, Seen, Extra; public string Group, Key, Kind = "simple";
            public bool Closed, Decided, Relocated, Verified, VerifyDone, TagOk, Overflow, Impossible, WaitFlight;
            public float T0, TLast, TReloc = -1f, FirstSeen = -1f; public V3 TagPos, Sum;
            public readonly List<Req> Reqs = new();
            public string Decision; public Cand Target;
        }
        /// Rank: 2 = landing seen by the spawn hooks, 1 = landing seen by polling. Only tags where units really landed (within LandDist) become candidates.
        sealed class Cand { public int Tag, Rank; public V3 Pos; public float Shift, PlayerD; public bool Reused; public bool Exact => Rank == 2; }
        /// Landed = units of this class really appeared within LandDist of the tag in this battle (hook or polling).
        sealed class Proof { public V3 Pos; public float T; public int N; public bool Exact, Polled, Landed; }
        sealed class Pt { public int Uid; public string Name; public V3 Pos; public int Team = -1; }
        /// Trigger shape read from the scenario data (never through the engine's IsPointInsideTrigger). Q = normalised rotation.
        sealed class TrigGeo { public int Uid; public bool Circle; public V3 C; public float Sx, Sz, Qx, Qy, Qz, Qw = 1f; }
        sealed class Reveal { public float Due; public string Group; public int Uid; }

        static readonly object _sync = new();
        static readonly List<Zone> _zones = new();
        static readonly Dictionary<int, Pt> _pts = new();
        static readonly List<(int uid, V3 pos)> _tags = new();
        static readonly List<TriggerData> _triggers = new();
        static readonly List<TrigGeo> _trigGeo = new();
        static int _trigBad;
        static bool _trigListBad;
        static readonly List<Batch> _batches = new();
        static readonly Dictionary<IntPtr, List<Req>> _byPtr = new();   // one SpawnNodeData may serve several calls: kept in call order
        static readonly Dictionary<(int side, int tag, string cls), Proof> _proven = new();
        static readonly Dictionary<string, float> _groupLast = new();
        static readonly Dictionary<string, List<float>> _relocGroups = new();
        static readonly HashSet<string> _blockedGroups = new();
        static readonly Dictionary<string, int> _reasons = new();
        static readonly HashSet<int> _known = new(), _rawSeen = new();
        static readonly Dictionary<int, int> _typeUid = new();
        static readonly Dictionary<int, float> _vueUid = new();         // uid -> ground sight, read once per unit
        static readonly Dictionary<int, UnitsRow> _rows = new();
        static readonly List<Reveal> _reveals = new();
        static readonly HashSet<string> _once = new();
        static readonly ConcurrentQueue<Ev> _q = new();
        static readonly int[] _hk = new int[10];
        static int _qCount, _qDropped;

        // player bubble: his own live units (ground + helicopters), a trail of past positions, sticky places
        static readonly List<(V3 pos, float vue)> _mine = new();         // his live units: place + ground sight of that unit
        static readonly List<(float t, V3 p)> _trail = new();
        static readonly Dictionary<string, (V3 pos, float last)> _sticky = new();
        static readonly Dictionary<int, float> _tagUsed = new();        // tag -> last time a batch spawned (or was sent) there
        static readonly Dictionary<string, int> _lastTarget = new();    // batch key -> tag its last swapped batch was sent to
        static readonly Dictionary<string, float> _lastSwap = new();    // batch key -> time of its last swap (respawn breaker window)
        static readonly List<Ev> _landPending = new();
        static bool _mineOk, _everVerified, _landPatched, _landRefused, _sessionArmed;
        static float _mineT = -100f, _nextTrail;
        static int _near, _nearNoTag, _nearEarly, _impossible, _landed, _landLogs, _landMissed, _mineCount;
        static int _horsVue, _repli;                                    // places chosen outside his units' sight / searches that fell back to the distance rule
        static HarmonyLib.Harmony _harmony;
        internal static PlayZoneCtl PlayZone;                            // captured by Patch_PlayZoneSet / Patch_PlayZoneReset

        static Il2CppReferenceArray<LuaUnit> _enemyArr, _playerArr;
        static ScenarioDataHandler _scen;
        static LuaMap _map;
        static bool? _solo;
        static volatile bool _active;
        static volatile int _modeCache = 1;
        static bool _coop, _baseline, _pointsLogged, _tagsLogged, _teamsLogged, _trigTested, _trigReadable,
                    _tripped;
        static string _tripWhy;
        static Batch _inFlight;
        static int _playerSide = -1, _enemySide = -1, _seq, _batchSeq, _modeLogged = -1, _byPtrCount;
        static int _callLogs, _resLogs, _orderLogs, _revealLogs, _freeLogs, _exactLogs, _zoneLogs, _droppedLogs;
        static int _enemyCalls, _allied, _moved, _would, _kept, _resSeen, _resNone, _free, _freeNear, _revealsDone, _hides,
                   _exactMatched, _exactNoReq, _exactAmbig, _spAtTarget, _spAtTag, _spZero, _ordersSeen, _batchOverflow;
        static float _start = -1f, _next, _nextPts, _nextSum;

        static void Log(string s) => Mod.Log.Msg("[SPAWN] " + s);
        static void LogOnce(string key, string s) { if (_once.Count < 300 && _once.Add(key)) Log(s); }
        static bool Cap(ref int n, int max) { if (n < max) { n++; return true; } _droppedLogs++; return false; }

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Spawns");
            Enabled = c.CreateEntry("SpawnsSurs", true, description: Build.Desc("Campagne solo : les renforts ennemis n'apparaissent plus près de tes unités"));
            Mode = c.CreateEntry("ModeRenforts", "auto", description: Build.Desc("auto = un renfort qui apparaîtrait près de tes unités part d'un autre endroit ennemi déjà utilisé dans la bataille ; observation = rien n'est déplacé, le journal dit ce qui aurait été fait ; off = rien"));
            PlayerRadius = c.CreateEntry("RayonJoueur", 2000f, description: Build.Desc("Distance (mètres) autour de tes unités dans laquelle un renfort ennemi ne doit pas apparaître"));
            Radius = c.CreateEntry("RayonProtection", 1200f, description: Build.Desc("Ancien réglage (zones tenues), gardé pour le journal seulement"));
            StartDelay = c.CreateEntry("DelaiDebutMission", 120f, description: Build.Desc("Secondes au début de chaque mission sans aucun changement"));
            MaxShift = c.CreateEntry("DecalageMax", 4000f, description: Build.Desc("Distance maximum entre l'apparition d'origine et l'endroit de remplacement"));
            PlayerMin = c.CreateEntry("DistanceJoueurMin", 2500f, description: Build.Desc("Distance minimum entre l'endroit de remplacement et tes unités"));
            ExMissions = c.CreateEntry("MissionsExclues", "", description: Build.Desc("Ignoré depuis 0.23.0 : le mod ne coupe jamais une fonction pour une mission"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            _migrated = c.CreateEntry("ReglagesV021", false, is_hidden: true);
            if (!_migrated.Value)
            {
                if (Math.Abs(Radius.Value - 1000f) < 0.5f) Radius.Value = 1200f;   // old v0.20 default -> new default
                _migrated.Value = true;
            }
            _migrated22 = c.CreateEntry("ReglagesV0225", false, is_hidden: true);
            if (!_migrated22.Value)
            {
                // old v0.21 defaults -> player bubble defaults
                if (Math.Abs(MaxShift.Value - 3000f) < 0.5f) MaxShift.Value = 4000f;
                if (Math.Abs(PlayerMin.Value - 800f) < 0.5f) PlayerMin.Value = 2500f;
                _migrated22.Value = true;
            }
        }

        internal static void OnQuit() => EndGuard();

        /// Clean end of a battle for the landing hook's crash guard.
        static void EndGuard()
        {
            if (!_sessionArmed) return;
            _sessionArmed = false;
            try { if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); } } catch { }
        }

        internal static void ResetSession()
        {
            lock (_sync)
            {
                if (_enemyCalls + _allied + _free > 0) Guard.Run("Spawns.Bilan", () => Summary(UnityEngine.Time.time, "fin de bataille"));
                _zones.Clear(); _pts.Clear(); _tags.Clear(); _triggers.Clear(); _trigGeo.Clear(); _trigBad = 0; _trigListBad = false; _batches.Clear(); _byPtr.Clear(); _proven.Clear();
                _groupLast.Clear(); _relocGroups.Clear(); _blockedGroups.Clear(); _reasons.Clear(); _known.Clear(); _rawSeen.Clear();
                _typeUid.Clear(); _vueUid.Clear(); _rows.Clear(); _reveals.Clear(); _once.Clear();
                _mine.Clear(); _trail.Clear(); _sticky.Clear(); _tagUsed.Clear(); _lastTarget.Clear(); _lastSwap.Clear(); _landPending.Clear();
                _mineOk = _everVerified = false; _mineT = -100f; _nextTrail = 0f; _busPosBad = false; _busBadN = 0;
                _near = _nearNoTag = _nearEarly = _impossible = _landed = _landLogs = _landMissed = _mineCount = 0;
                _horsVue = _repli = 0;
                EndGuard();
                while (_q.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _qCount, 0); Interlocked.Exchange(ref _qDropped, 0);
                for (int i = 0; i < _hk.Length; i++) Interlocked.Exchange(ref _hk[i], 0);
                _enemyArr = _playerArr = null; _scen = null; _map = null; _solo = null; _active = false;
                _coop = _baseline = _pointsLogged = _tagsLogged = _teamsLogged = _trigTested = _trigReadable = false;
                _tripped = false; _tripWhy = null; _inFlight = null;
                _playerSide = _enemySide = -1; _seq = _batchSeq = _byPtrCount = 0; _modeLogged = -1;
                _callLogs = _resLogs = _orderLogs = _revealLogs = _freeLogs = _exactLogs = _zoneLogs = _droppedLogs = 0;
                _enemyCalls = _allied = _moved = _would = _kept = _resSeen = _resNone = _free = _freeNear = _revealsDone = _hides = 0;
                _exactMatched = _exactNoReq = _exactAmbig = _spAtTarget = _spAtTag = _spZero = _ordersSeen = _batchOverflow = 0;
                _start = -1f; _next = _nextPts = _nextSum = 0f;
            }
        }

        internal static bool Active => !Campaign.MissionInerte && _active && Mod.Actif.Value && !Mod.AntiCheatActive && Campaign.InCampaign && (Enabled?.Value ?? false);
        /// Log-only hooks: cheap check, no preference string parsing.
        internal static bool Listening => !Campaign.MissionInerte && _active && _modeCache != 0 && Campaign.InCampaign;   // never in a mission without the mod
        /// Prepare() of every patch class: installed whenever SpawnsSurs is on at game launch, whatever the mode,
        /// so switching ModeRenforts from off in the settings panel works without a restart (Listening / OnSingle filter the mode at run time).
        internal static bool HooksWanted => Enabled?.Value ?? false;

        static int ModeVal()
        {
            string v = (Mode?.Value ?? "observation").Trim().ToLowerInvariant();
            return v == "off" || v == "non" ? 0 : v == "auto" ? 2 : 1;
        }
        static string ModeName(int m) => m == 0 ? "off" : m == 2 ? "auto" : "observation";

        static int _wait;

        internal static void Frame()
        {
            if (Enabled == null) return;
            float now = UnityEngine.Time.time;
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _active ? Planif.WaitNormal : Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            _next = now + 2f;
            lock (_sync) Poll(now);
        }

        static void Poll(float now)
        {
            if (!Enabled.Value) { _active = false; Discard(); return; }
            var gc = GameController._instance; var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { _active = false; Discard(); return; }
            Campaign.NoteSession();
            int side = (int)cp.TeamSide;
            if (side != 0 && side != 1) { _active = false; Discard(); return; }
            _playerSide = side; _enemySide = 1 - side;
            if (_start < 0) { _start = now; _nextSum = now + 300f; }
            Guard.Run("Spawns.Solo", SoloCheck);
            _active = _solo == true;
            if (!_active) { Discard(); return; }
            int m = ModeVal();
            if (m != _modeLogged)
            {
                if (_modeLogged == 0) _baseline = false;   // units that appeared while off are not "apparitions sans appel"
                _modeLogged = m;
                Log($"mode renforts = {ModeName(m)}" + (m == 2 ? $" (un renfort ennemi qui apparaîtrait à moins de {PlayerRadius.Value:0} m de tes unités part d'un autre endroit ennemi déjà utilisé dans la bataille)" : m == 1 ? " (rien n'est modifié, les décisions sont seulement écrites)" : " (seul l'état des zones est suivi)"));
            }
            _modeCache = m;
            _map ??= new LuaMap();
            if (now >= _nextPts) { _nextPts = now + 60f; Guard.Run("Spawns.Points", () => Points(gc)); Guard.Run("Spawns.Tags", ReadTags); }
            Guard.Run("Spawns.Unites", ReadUnits);
            Guard.Run("Spawns.Bulle", () => { ReadMine(gc, now); UpdateBubble(now); });
            if (now - _start >= 20f) Guard.Run("Spawns.Zones", () => Zones(gc, now));
            if (m == 0) { Discard(); return; }
            Guard.Run("Spawns.CrochetAtterrissage", LandingHook);
            // landings first (matched by group, exact place): a swap verified by the hook is settled before polling judges it
            Guard.Run("Spawns.Atterrissages", () => RetryLandings(now));
            Guard.Run("Spawns.Crochets", () => Drain(now));
            Guard.Run("Spawns.Observation", () => Observe(now));
            Guard.Run("Spawns.Reserves", () => Reveals(gc, now));
            if (now >= _nextSum) { _nextSum = now + 300f; Guard.Run("Spawns.Bilan", () => Summary(now, "5 min")); }
        }

        /// Same network test as EnemyAi.HostCheck, latched for the battle: anything online keeps the feature inactive.
        static void SoloCheck()
        {
            bool net = false, sh = false, slave = false, flagsOk = true; string st = "?";
            try { net = NetScen.IsNetwork; sh = NetScen.IsScenarioHost; slave = NetScen.IsScenarioSlave; } catch { flagsOk = false; }
            try { st = NetStatus.Status.ToString(); } catch { }
            if (!flagsOk || net || slave || sh || st == "Loading" || st == "Deploy" || st == "Game") _coop = true;
            bool solo = !_coop;
            if (_solo != solo) Log($"réseau={st} scénarioRéseau={net} esclave={slave} hôteScénario={sh} -> {(solo ? "solo : renforts suivis" : "en ligne : inactif")}");
            _solo = solo;
        }

        // ------------------------------------------------------------ scenario: spawn points (player side = axis source), triggers, authored tags
        static void Points(GameController gc)
        {
            _scen = gc.GetScenarioData;
            var sorted = _scen?._sortedObjects;
            if (sorted == null) return;
            var bus = gc._GetEcsEventBus_k__BackingField?.Gameplay;
            _triggers.Clear();
            bool trigList = false;
            try
            {
                if (sorted.TryGetValue(MEObjectTypeEnum.Trigger, out var tl) && tl != null)
                    for (int i = 0; i < tl.Count; i++) { var td = tl[i]?.TryCast<TriggerData>(); if (td != null) _triggers.Add(td); }
                trigList = true;
                _trigListBad = false;
            }
            catch (Exception e) { _trigListBad = true; LogOnce("trig-list", "liste des déclencheurs illisible : " + e.Message + " (échange limité à " + TrigUnknownShift.ToString("0", CultureInfo.InvariantCulture) + " m)"); }
            if (trigList && !_trigTested) TriggerGeometry();
            if (!sorted.TryGetValue(MEObjectTypeEnum.SpawnPoint, out var list) || list == null) { LogOnce("no-pts", "aucun point d'apparition dans ce scénario"); list = null; }
            int n = 0, mine = 0, theirs = 0;
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                try
                {
                    var sp = list[i]?.TryCast<SpawnPointData>();
                    if (sp == null) continue;
                    int uid = sp.UID;
                    if (!_pts.TryGetValue(uid, out var p)) _pts[uid] = p = new Pt { Uid = uid };
                    p.Name = sp.DisplayedName;
                    try { p.Pos = Il2Cpp.ClassExtensions.ToUnityVector(sp.GetActualPos); } catch { }
                    try { if (bus != null && bus.GetObjectPosition != null) { var bp = bus.GetObjectPosition.Invoke(uid, (int)MEObjectTypeEnum.SpawnPoint); if (bp != V3.zero) p.Pos = bp; } } catch { }
                    try { var td = sp.Team.LinkedData?.TryCast<TeamData>(); p.Team = td != null ? (int)td.TeamSide : -1; } catch { p.Team = -1; }
                    n++; if (p.Team == _playerSide) mine++; else if (p.Team == _enemySide) theirs++;
                }
                catch (Exception e) { LogOnce("pt" + i, "point illisible : " + e.Message); }
            }
            if (_pointsLogged) return;
            _pointsLogged = true;
            Log($"prêt : points d'apparition={n} (camp joueur {mine}, camp ennemi {theirs}) déclencheurs={(trigList ? _triggers.Count.ToString() : "illisibles")} test déclencheurs={TrigState()} mission={Campaign.MissionUid}");
            foreach (var p in _pts.Values.Take(20)) Log($"point {p.Uid} '{p.Name}' camp={p.Team} ({p.Pos.x:0},{p.Pos.z:0})");
        }

        static void ReadTags()
        {
            if (_scen == null || _map == null) return;
            var arr = _map.GetTags(V3.zero, 1_000_000f, -1);
            _tags.Clear();
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                try
                {
                    var t = arr[i]; if (t == null) continue;
                    int uid = t.DataUID;
                    if (uid > 0 && ScenarioDataHandlerExt.TryGetTagPos(_scen, uid, out var p)) _tags.Add((uid, p));
                }
                catch { }
            }
            if (!_tagsLogged) { _tagsLogged = true; Log($"tags du scénario lus : {_tags.Count}"); }
        }

        static void ReadUnits()
        {
            _enemyArr = null; _playerArr = null;
            try { _enemyArr = _map.GetUnits(V3.zero, 1_000_000f, _enemySide, -1); } catch (Exception e) { LogOnce("units-e", "unités ennemies illisibles : " + e.Message); }
            try { _playerArr = _map.GetUnits(V3.zero, 1_000_000f, _playerSide, -1); } catch (Exception e) { LogOnce("units-p", "unités du camp joueur illisibles : " + e.Message); }
        }

        /// Units.Type of a live unit (cached per uid); 0 = unknown, counted as ground (conservative for distances).
        static int TypeOf(LuaUnit u)
        {
            int uid = u.UID;
            if (_typeUid.TryGetValue(uid, out int t)) return t;
            UnitsRow row = null;
            try { row = Row(u.SpawnData?.Unit?.UnitID ?? 0); } catch { }
            if (row == null) return 0;
            try { t = (int)row.Type; } catch { t = 0; }
            if (_typeUid.Count < 5000) _typeUid[uid] = t;
            return t;
        }

        /// Ground sight of a live unit (Units.Sensors, largest OpticsGround); 0 = unreadable, the sight rule then ignores that unit.
        /// A per-unit exception written on an Options clone is not visible here, so a unit may be credited with the shared row value:
        /// that under-estimates the sight, which only makes the preference weaker, never less safe.
        static float VueSol(LuaUnit u)
        {
            int uid = u.UID;
            if (_vueUid.TryGetValue(uid, out float v)) return v;
            v = 0f;
            try
            {
                var row = Row(u.SpawnData?.Unit?.UnitID ?? 0);
                var list = row?.Sensors;
                if (list != null) for (int i = 0; i < list.Count; i++) { var s = list[i]; if (s != null) v = Math.Max(v, s.OpticsGround); }
            }
            catch { v = 0f; }
            if (_vueUid.Count < 5000) _vueUid[uid] = v;
            return v;
        }

        static UnitsRow Row(int id)
        {
            if (id <= 0) return null;
            if (_rows.TryGetValue(id, out var cached)) return cached;
            UnitsRow row = null;
            try { var src = DataBaseService._instance?.RawAccess; if (src != null) src.Units.TryGetById(id, out row); } catch { row = null; }
            if (row != null) _rows[id] = row;
            return row;
        }

        /// Class used for "proven" tags: Units.Type (wheeled/tracked is not exposed in a simple way).
        static string ClassOf(UnitsRow row)
        {
            if (row == null) return "?";
            try { int t = (int)row.Type; return t == 0 ? "?" : "type" + t.ToString(CultureInfo.InvariantCulture); } catch { return "?"; }
        }

        // ------------------------------------------------------------ zones: raw Lua + raw bus, merged by position, held since first player poll
        static void Zones(GameController gc, float now)
        {
            var objs = _map.GetObjectives(false);
            if (objs == null) return;
            var busDel = gc._GetEcsEventBus_k__BackingField?.Gameplay?.GetCapturedObjectiveZoneTeam;
            foreach (var z in _zones) z.PollUid = 0;
            for (int i = 0; i < objs.Length; i++)
            {
                try
                {
                    var o = objs[i];
                    if (o == null || !o.IsVisible()) continue;
                    int uid = o.Object.DataUID;
                    V3 pos = o.Position;
                    string lua = EnemyAi.OwnerRaw(o);
                    string bus = null;
                    try { if (busDel != null) bus = busDel.Invoke(uid).ToString(CultureInfo.InvariantCulture); }
                    catch (Exception e) { LogOnce("bus-zone", "lecture bus des zones impossible : " + e.Message); }
                    var z = ZoneFor(uid, pos, now);
                    // the most recent object of a merged zone wins (twins such as 27/102)
                    if (z.PollUid == 0 || z.Uids.IndexOf(uid) >= z.Uids.IndexOf(z.PollUid))
                    {
                        z.PollUid = uid; z.PollLua = lua; z.PollBus = bus; z.Pos = pos;
                        try { var s = o.Scale; z.Radius = UnityEngine.Mathf.Clamp(Math.Max(Math.Abs(s.x), Math.Abs(s.z)), 150f, 600f); } catch { }
                    }
                    RememberRaw(lua); RememberRaw(bus);
                }
                catch (Exception e) { LogOnce("zone" + i, "zone illisible : " + e.Message); }
            }
            foreach (var z in _zones) { if (z.PollUid == 0) continue; z.LastSeen = now; UpdateZone(gc, z, now); }
            if (!_teamsLogged && _zones.Count > 0) { _teamsLogged = true; LogTeams(gc, "début"); }
        }

        static void RememberRaw(string raw)
        {
            if (raw != null && _rawSeen.Count < 64 && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) _rawSeen.Add(v);
        }

        static Zone ZoneFor(int uid, V3 pos, float now)
        {
            foreach (var z in _zones) if (z.Uids.Contains(uid)) return z;
            Zone best = null; float bd = MergeDist;
            foreach (var z in _zones) { float d = EnemyAi.D2(z.Pos, pos); if (d < bd) { bd = d; best = z; } }
            if (best != null)
            {
                best.Uids.Add(uid);
                if (Cap(ref _zoneLogs, CapZones)) Log($"objectif {uid} fusionné avec la zone {best.Id} (à {bd:0} m) : historique conservé ({(best.Held ? $"tenue depuis t={best.HeldSince - _start:0}s" : SideName(best.Owner))})");
                return best;
            }
            var nz = new Zone { Id = uid, Pos = pos };
            nz.Uids.Add(uid); _zones.Add(nz);
            return nz;
        }

        static void UpdateZone(GameController gc, Zone z, float now)
        {
            int ls = EnemyAi.SideOf(gc, z.PollLua), bs = EnemyAi.SideOf(gc, z.PollBus);
            // player = lua is player OR (raw lua is exactly -1, neutral, AND bus is player); enemy = lua is enemy; anything else unknown
            bool luaNeutral = z.PollLua == "-1";
            int owner = ls == _playerSide || (luaNeutral && bs == _playerSide) ? _playerSide : ls == _enemySide ? _enemySide : -1;
            if (ls != _playerSide && ls != _enemySide && !luaNeutral)
                LogOnce("lua-inconnu" + z.Id, $"zone {z.Id} : propriétaire Lua illisible ou inconnu (brut '{z.PollLua ?? "null"}', bus '{z.PollBus ?? "null"}'->{bs}) : camp inconnu, le bus n'est pas utilisé");
            bool rawChanged = z.PollLua != z.LuaRaw || z.PollBus != z.BusRaw;
            z.LuaRaw = z.PollLua; z.BusRaw = z.PollBus;
            string raw = $"objet {z.PollUid} lua brut='{z.PollLua ?? "null"}'->{ls} bus brut='{z.PollBus ?? "null"}'->{bs}";
            if (z.Owner == -3)
            {
                z.Owner = owner; z.EverNotPlayer = owner != _playerSide;
                if (Cap(ref _zoneLogs, CapZones)) Log($"zone {z.Id} ({z.Pos.x:0},{z.Pos.z:0}) rayon {z.Radius:0} m départ : {SideName(owner)} ({raw}){(owner == _playerSide ? " : déjà au joueur, jamais protégée" : "")}");
                return;
            }
            int before = z.Owner; z.Owner = owner;
            if (owner == _playerSide)
            {
                z.LossSince = -1f;
                if (!z.Held && z.EverNotPlayer)
                {
                    z.Held = true; z.HeldSince = now;
                    if (Cap(ref _zoneLogs, CapZones)) Log($"zone {z.Id} tenue depuis t={now - _start:0}s ({raw}, avant : {SideName(before)})");
                }
                else if ((before != owner || rawChanged) && Cap(ref _zoneLogs, CapZones)) Log($"zone {z.Id} : {SideName(owner)} ({raw})");
                return;
            }
            z.EverNotPlayer = true;
            if (z.Held)
            {
                if (z.LossSince < 0) { z.LossSince = now; if (Cap(ref _zoneLogs, CapZones)) Log($"zone {z.Id} : lecture {SideName(owner)} ({raw}), perte confirmée si ça dure {LossDebounce:0} s"); }
                else if (now - z.LossSince >= LossDebounce)
                {
                    z.Held = false; z.HeldSince = -1f; z.LossSince = -1f;
                    if (Cap(ref _zoneLogs, CapZones)) Log($"zone {z.Id} perdue ({raw})");
                }
            }
            else if ((before != owner || rawChanged) && Cap(ref _zoneLogs, CapZones)) Log($"zone {z.Id} : {SideName(before)} -> {SideName(owner)} ({raw})");
        }

        static void LogTeams(GameController gc, string when)
        {
            var sb = new StringBuilder();
            try
            {
                var teams = gc._GameSession_k__BackingField?.GetTeams();
                var ids = new SortedSet<int>(Enumerable.Range(-1, 18)); foreach (int v in _rawSeen) ids.Add(v);
                foreach (int id in ids)
                {
                    try { if (teams != null && teams.TryGetValue(id, out var td) && td != null) sb.Append($" {id}->{td.TeamSide}({(int)td.TeamSide})"); } catch { }
                }
            }
            catch (Exception e) { sb.Append(" illisible : " + e.Message); }
            string mineTeam = "?";
            try { var td = gc._GameSession_k__BackingField?.CurrentPlayer?.TeamData; if (td != null) mineTeam = td.UID.ToString(CultureInfo.InvariantCulture); } catch { }
            Log($"table des équipes ({when}) uid->camp :{(sb.Length == 0 ? " vide" : sb.ToString())} ; joueur camp {_playerSide} équipe uid {mineTeam} ; valeurs brutes vues : {string.Join(",", _rawSeen.OrderBy(v => v))}");
        }

        static string SideName(int s) => s == _playerSide ? "joueur" : s == _enemySide ? "ennemi" : s == -3 ? "?" : "inconnu";
        static bool Vis(Zone z, float now) => now - z.LastSeen < VisibleFor;

        /// mode 1 = player-owned zones, 2 = held zones.
        static Zone Nearest(V3 p, float now, int mode, out float dist)
        {
            Zone best = null; dist = float.MaxValue;
            foreach (var z in _zones)
            {
                if (!Vis(z, now)) continue;
                if (mode == 1 && z.Owner != _playerSide) continue;
                if (mode == 2 && !z.Held) continue;
                float d = EnemyAi.D2(p, z.Pos); if (d < dist) { dist = d; best = z; }
            }
            return best;
        }

        // ------------------------------------------------------------ player bubble
        static float MinMine(V3 p) { float m = float.MaxValue; foreach (var q in _mine) m = Math.Min(m, EnemyAi.D2(p, q.pos)); return m; }

        /// True when a live unit of his would have this place inside its own ground sight.
        static bool VuParMoi(V3 p) { foreach (var m in _mine) if (m.vue > 0f && EnemyAi.D2(p, m.pos) <= m.vue) return true; return false; }
        static float MinTrail(V3 p) { float m = float.MaxValue; foreach (var q in _trail) m = Math.Min(m, EnemyAi.D2(p, q.p)); return m; }

        /// His own live units (owner = the local player), ground and helicopters; planes pass through and never count.
        static void ReadMine(GameController gc, float now)
        {
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            var arr = _playerArr;                                   // read by ReadUnits just before (whole player team)
            if (cp == null || arr == null) { _mineOk = false; return; }
            int local = cp.UID;
            _mine.Clear();
            for (int i = 0; i < arr.Length; i++)
            {
                try
                {
                    var u = arr[i];
                    if (u == null || !u.IsAlive() || u.GetOwnerPlayerUID() != local) continue;
                    if ((TypeOf(u) & 16) != 0) continue;
                    _mine.Add((u.GetPosition(), VueSol(u)));
                }
                catch { }
            }
            _mineOk = true; _mineT = now;
            if (_mine.Count != _mineCount && (_mineCount == 0 || _mine.Count == 0)) Log($"bulle du joueur : {_mine.Count} unité(s) à toi suivie(s) (rayon {PlayerRadius.Value:0} m)");
            _mineCount = _mine.Count;
        }

        /// Trail snapshot every TrailEvery s (kept TrailKeep s); a sticky place is released once everything stayed beyond radius + margin for StickyHold s.
        static void UpdateBubble(float now)
        {
            if (!_mineOk) return;
            if (now >= _nextTrail)
            {
                _nextTrail = now + TrailEvery;
                foreach (var p in _mine) { if (_trail.Count >= 3000) break; _trail.Add((now, p.pos)); }
            }
            _trail.RemoveAll(x => now - x.t > TrailKeep);
            if (_sticky.Count == 0) return;
            float exit = PlayerRadius.Value + ExitMargin;
            foreach (var key in _sticky.Keys.ToList())
            {
                var s = _sticky[key];
                if (MinMine(s.pos) < exit || MinTrail(s.pos) < exit) _sticky[key] = (s.pos, now);
                else if (now - s.last > StickyHold) _sticky.Remove(key);
            }
        }

        /// NEAR as soon as one of his units (or a trail position) is inside the radius; stays NEAR until UpdateBubble releases it.
        static bool IsNear(string key, V3 pos, float now, out float d)
        {
            d = Math.Min(MinMine(pos), MinTrail(pos));
            if (d < PlayerRadius.Value)
            {
                if (key.Length > 0 && (_sticky.ContainsKey(key) || _sticky.Count < 500)) _sticky[key] = (pos, now);
                return true;
            }
            return key.Length > 0 && _sticky.ContainsKey(key);
        }

        static string PlaceKey(Req r)
        {
            if (r.TagOk) return "t" + r.TagUid.ToString(CultureInfo.InvariantCulture);
            if (r.TargetPos != V3.zero) return "v" + Math.Round(r.TargetPos.x / 100f).ToString(CultureInfo.InvariantCulture) + "/" + Math.Round(r.TargetPos.z / 100f).ToString(CultureInfo.InvariantCulture);
            if (r.PointUid > 0) return "p" + r.PointUid.ToString(CultureInfo.InvariantCulture);
            return "";
        }

        // ------------------------------------------------------------ SpawnUnit prefix: batch, decide, (auto only) swap to a proven tag
        internal static void OnSingle(SpawnNodeData d)
        {
            float now = UnityEngine.Time.time;
            lock (_sync)
            {
                int mode = ModeVal();
                if (mode == 0 || _start < 0) return;
                Interlocked.Increment(ref _hk[EvSingle]);
                if (_scen == null) { try { _scen = GameController._instance?.GetScenarioData; } catch { } }
                var r = new Req { Id = ++_seq, T = now, Owner = d.OwnerPlayerUID, Group = d.UnitGroup ?? "", TagUid = d.TargetTagUID, TargetPos = d.TargetPos, Deck = d.Assign, Units = Math.Max(1, d.Quantity) };
                try { r.PointUid = d.SpawnPointUID; } catch { }
                try { r.UnitId = d.UnitInfo?.Unit?.UnitID ?? 0; } catch { }
                r.Row = Row(r.UnitId); r.Cls = ClassOf(r.Row);
                try { r.Air = r.Row != null && ((int)r.Row.Type & (8 | 16)) != 0; } catch { }
                r.Side = SideOfOwner(r.Owner);
                r.Enemy = (r.Side == 0 || r.Side == 1) && r.Side != _playerSide;
                try { r.TagOk = r.TagUid > 0 && _scen != null && ScenarioDataHandlerExt.TryGetTagPos(_scen, r.TagUid, out r.TagPos); } catch { r.TagOk = false; }
                // place judged: tag (it wins over the spawn point), else TargetPos, else the spawn point
                r.Expect = r.Origin = r.TagOk ? (r.TargetPos != V3.zero ? r.TargetPos : r.TagPos)
                                    : r.TargetPos != V3.zero ? r.TargetPos
                                    : r.PointUid > 0 && _pts.TryGetValue(r.PointUid, out var spt) ? spt.Pos : V3.zero;
                r.Place = PlaceKey(r);
                try
                {
                    if (d.Pointer != IntPtr.Zero && _byPtrCount < 2000)
                    {
                        if (!_byPtr.TryGetValue(d.Pointer, out var pl)) _byPtr[d.Pointer] = pl = new List<Req>();
                        pl.Add(r); _byPtrCount++;
                    }
                }
                catch { }
                if (!r.Enemy) { _allied++; return; }      // allied or unknown owner: never touched, not observed
                _enemyCalls++;
                var b = BatchFor(r, now, "simple", out bool isNew, out float interval);
                string verdict = Decide(d, r, b, now, mode);
                int usedTag = r.Moved ? r.SentTag : r.TagOk ? r.TagUid : 0;
                if (usedTag > 0 && (_tagUsed.ContainsKey(usedTag) || _tagUsed.Count < 2000)) _tagUsed[usedTag] = now;
                if (!Cap(ref _callLogs, CapCalls)) return;
                string near = r.NearD >= 0f && r.NearD < float.MaxValue ? $" ta plus proche unité à {r.NearD:0} m" : "";
                string lot = $" lot #{b.Id}" + (isNew ? (interval >= 0 ? $" (groupe rappelé après {interval:0} s)" : " (1er appel du groupe)") : " (suite)");
                Log($"appel #{r.Id} t={now - _start:0}s camp={r.Side} unité={r.UnitId} {r.Cls}{(r.Air ? " aérien" : "")} qté={r.Units} groupe='{r.Group}' tag={r.TagUid}{(r.TagOk ? $"({r.TagPos.x:0},{r.TagPos.z:0})" : "(?)")} point={r.PointUid} pos=({r.TargetPos.x:0},{r.TargetPos.z:0}){lot}{near} : {verdict}");
            }
        }

        static string GroupKey(int side, string group) => side + "|" + (group ?? "");
        /// A named group is blocked as a whole; calls without a group name share one group key, so only their place is blocked.
        static string BlockKey(int side, string group, string batchKey) => string.IsNullOrEmpty(group) ? batchKey : GroupKey(side, group);
        static bool Blocked(Req r, Batch b) => _blockedGroups.Contains(BlockKey(r.Side, r.Group, b.Key));

        static Batch BatchFor(Req r, float now, string kind, out bool isNew, out float interval)
        {
            string gk = GroupKey(r.Side, r.Group), key = gk + "|" + r.Place;
            interval = -1f; isNew = false;
            for (int i = _batches.Count - 1; i >= 0; i--)
            {
                var x = _batches[i];
                // a batch lives at most MaxBatchSpan: a group called again and again at the same place opens new batches (counted by the respawn breaker, decided again)
                if (!x.Closed && x.Key == key && now - x.TLast <= BatchWindow && now - x.T0 <= MaxBatchSpan) { x.Reqs.Add(r); x.TLast = now; x.Expected += r.Units; r.B = x; return x; }
            }
            isNew = true;
            var b = new Batch { Id = ++_batchSeq, Side = r.Side, TagUid = r.TagUid, TagPos = r.TagPos, TagOk = r.TagOk, Group = r.Group, Key = key, Kind = kind, T0 = now, TLast = now, Expected = r.Units };
            b.Reqs.Add(r); r.B = b;
            if (_groupLast.TryGetValue(gk, out float last)) interval = now - last;
            _groupLast[gk] = now;
            // respawn loop: the same group called again at the same place after a swap (other places of a wave are not counted);
            // only that group stops being swapped, the rest of the battle keeps its protection
            if (_relocGroups.TryGetValue(key, out var times) && _lastSwap.TryGetValue(key, out float swapAt) && now - swapAt <= RespawnWindow)
            {
                times.Add(now); times.RemoveAll(t => now - t > RespawnWindow);
                if (times.Count > 2 && _blockedGroups.Add(BlockKey(r.Side, r.Group, key)))
                    Mod.Log.Warning($"[SPAWN] groupe '{r.Group}' rappelé {times.Count} fois au même endroit en {RespawnWindow:0} s après un échange : {(string.IsNullOrEmpty(r.Group) ? "cet endroit" : "ce groupe")} n'est plus échangé (boucle de réapparition du script possible), les autres restent protégés");
            }
            if (_batches.Count >= 300)
            {
                int old = _batches.FindIndex(x => x.Closed && (!x.Relocated || x.VerifyDone));
                if (old >= 0) _batches.RemoveAt(old);
            }
            if (_batches.Count < 300) _batches.Add(b);
            else { b.Closed = b.Overflow = true; _batchOverflow++; _droppedLogs++; }   // never followed: Decide refuses any move for it
            return b;
        }

        static string Kept(string key, string detail)
        {
            _kept++; Count(key);
            return "gardé (" + key + (string.IsNullOrEmpty(detail) ? "" : " : " + detail) + ")";
        }
        static void Count(string key) { _reasons.TryGetValue(key, out int n); _reasons[key] = n + 1; }

        static string Gate(Req r, float now, out string detail)
        {
            detail = null;
            // player rule 2026-09-16: no feature is ever switched off for one mission, so MissionsExclues is only reported
            if (InList(Campaign.MissionUid, ExMissions.Value)) LogOnce("exclue", $"MissionsExclues contient {Campaign.MissionUid} : ignoré, le mod ne coupe jamais une fonction pour une mission");
            if (r.Deck) return "ajouté au deck";
            if (r.Cls == "?") return "type inconnu";
            if (r.Origin == V3.zero) { detail = $"point {r.PointUid}"; return "lieu inconnu"; }
            if (!_mineOk || now - _mineT > 10f) return "tes unités illisibles";
            bool near = IsNear(r.Place, r.Origin, now, out float dist);
            r.NearD = dist;
            if (!near) return "libre";
            _near++;
            if (now - _start < StartDelay.Value) { _nearEarly++; return "près de toi, début de mission"; }
            if (!r.TagOk) { _nearNoTag++; detail = $"point {r.PointUid}, seul un tag peut être échangé"; return "PRÈS DE TOI sans tag (non protégé)"; }
            return null;
        }

        static string Decide(SpawnNodeData d, Req r, Batch b, float now, int mode)
        {
            if (b.Overflow) return Kept("trop de lots", "lot non suivi, aucun déplacement");
            // every call of a swapped batch follows it, so a batch never ends up split between two places
            if (b.Decided && b.Relocated && b.Target != null)
            {
                string stop = _tripped ? "disjoncteur coupé" : Blocked(r, b) ? "groupe bloqué" : r.Deck ? "ajouté au deck" : !r.TagOk ? "sans tag" : null;
                if (stop != null) return Kept("lot déplacé interrompu", stop);
                // the new place is checked again: he may have moved toward it since the batch was swapped
                if (!StillSafe(b.Target.Pos))
                {
                    IsNear(r.Place, r.Origin, now, out float dNow);
                    r.NearD = dNow;
                    // out of sight first, then the distance rule alone if no tag of the battle stays outside his optics
                    var again = Pick(r, now, out string whyAgain, b.Target.Tag, true);
                    if (again == null) { _repli++; again = Pick(r, now, out whyAgain, b.Target.Tag); } else _horsVue++;
                    if (again != null)
                    {
                        Log($"lot #{b.Id} : le tag {b.Target.Tag} est maintenant près de tes unités, la suite du lot part du tag {again.Tag}");
                        b.Target = again;
                        _lastTarget[b.Key] = again.Tag;
                    }
                    else
                    {
                        // no better place: keep the current target as long as it is still farther from him than the original place
                        float dOld = Math.Min(MinMine(b.Target.Pos), MinTrail(b.Target.Pos));
                        if (dOld <= dNow) return Kept("lot déplacé interrompu", $"le nouvel endroit ({dOld:0} m) n'est plus plus loin de toi que l'endroit d'origine ({dNow:0} m) et aucun autre endroit sûr : " + whyAgain);
                        LogOnce($"garde:{b.Id}", $"lot #{b.Id} : le tag {b.Target.Tag} est à {dOld:0} m de tes unités (moins que voulu) mais reste plus loin que l'origine ({dNow:0} m) : la suite du lot y part quand même");
                    }
                }
                if (!Apply(d, r, b)) return Kept("écriture impossible", null);
                Count("déplacé"); return "DÉPLACÉ (même lot) vers " + Describe(b.Target);
            }
            string key = Gate(r, now, out string detail);
            if (key != null) return Kept(key, detail);
            if (b.Decided && b.Target == null && b.Impossible) { b.Decided = false; b.Decision = null; }      // proofs may have appeared since: judge again
            if (b.Decided && !b.Relocated && b.WaitFlight && (_everVerified || _inFlight == null || _inFlight.Verified || _inFlight.VerifyDone))
            { b.Decided = false; b.Target = null; b.WaitFlight = false; b.Decision = null; }                    // the batch it waited for is settled: judge again
            if (b.Decided)
            {
                if (b.Target == null) return Kept("lot déjà jugé", b.Decision);
                _would++; Count("aurait déplacé");
                return $"OBSERVATION : aurait déplacé (même lot) vers {Describe(b.Target)} (non appliqué : {b.Decision})";
            }
            b.Decided = true; b.Impossible = false; b.WaitFlight = false;
            // out of sight first, then the distance rule alone if no tag of the battle stays outside his optics
            var cand = Pick(r, now, out string why, SameKeyTarget(b.Key), true);
            if (cand == null) { _repli++; cand = Pick(r, now, out why, SameKeyTarget(b.Key)); } else _horsVue++;
            if (cand == null) { _impossible++; b.Impossible = true; b.Decision = "aucun endroit sûr : " + why; return Kept("IMPOSSIBLE : aucun endroit sûr loin de toi", why); }
            b.Target = cand;
            string desc = $"{Describe(cand)}, ta plus proche unité {r.NearD:0} m -> {cand.PlayerD:0} m";
            bool waitFlight = !_everVerified && _inFlight != null && _inFlight != b && !_inFlight.Verified && !_inFlight.VerifyDone;
            string block = mode != 2 ? "mode observation"
                : _tripped ? "disjoncteur coupé : " + _tripWhy
                : waitFlight ? $"lot #{_inFlight.Id} pas encore vérifié (tant qu'aucun échange n'est vérifié dans la bataille, un lot à la fois)"
                : Blocked(r, b) ? "groupe bloqué (réapparitions trop rapides)"
                : null;
            if (block != null) { b.Decision = block; b.WaitFlight = mode == 2 && !_tripped && waitFlight; _would++; Count("aurait déplacé"); return $"OBSERVATION : aurait déplacé vers {desc} (non appliqué : {block})"; }
            if (!Apply(d, r, b)) { b.Decision = "écriture impossible"; return Kept("écriture impossible", null); }
            b.Relocated = true; b.TReloc = now; _inFlight = b;
            _lastTarget[b.Key] = cand.Tag;
            // created once per group and place: BatchFor appends every later new batch there, so "more than 2 in 90 s" can really be seen
            // the swapped batch itself is the first entry (BatchFor already added it when it opened within RespawnWindow of an earlier swap)
            if (!_relocGroups.TryGetValue(b.Key, out var tl)) _relocGroups[b.Key] = tl = new List<float>();
            if (!tl.Contains(b.T0)) tl.Add(b.T0);
            _lastSwap[b.Key] = now;
            Count("déplacé");
            return "DÉPLACÉ vers " + desc;
        }

        /// A swap target is still outside the bubble + margin (current and past positions of his units).
        static bool StillSafe(V3 pos)
        {
            float R = PlayerRadius.Value, minPlayer = Math.Max(PlayerMin.Value, R + ExitMargin);
            return MinMine(pos) >= minPlayer && MinTrail(pos) >= R + ExitMargin;
        }

        /// The tag the previous batch of the same group and place was sent to (so a repeated call keeps going to the same place), 0 = none.
        static int SameKeyTarget(string key) => _lastTarget.TryGetValue(key, out int t) ? t : 0;

        static void MarkVerified(Batch b, string how)
        {
            if (b.VerifyDone) return;
            b.Verified = b.VerifyDone = true;
            if (!_everVerified) { _everVerified = true; Log($"premier échange vérifié dans cette bataille (lot #{b.Id}) : les lots suivants ne s'attendent plus"); }
            Log($"lot #{b.Id} vérifié : {how}");
        }

        static string Describe(Cand c) => $"tag {c.Tag} ({(c.Exact ? "atterrissage vu par le crochet" : "atterrissage vu par sondage")}{(c.Reused ? ", déjà utilisé il y a moins de 20 s : aucun autre endroit sûr" : "")}) ({c.Pos.x:0},{c.Pos.z:0}) décalage {c.Shift:0} m";

        /// Tag swap only. TargetPos is written only when the call had one (tag position + the call's own formation offset);
        /// a call with TargetPos = zero keeps it at zero so the game still spawns in tag mode.
        static bool Apply(SpawnNodeData d, Req r, Batch b)
        {
            var c = b.Target;
            bool writePos = r.TargetPos != V3.zero;
            V3 np = writePos ? c.Pos + (r.TargetPos - r.TagPos) : c.Pos;
            try { d.TargetTagUID = c.Tag; if (writePos) d.TargetPos = np; }
            catch (Exception e)
            {
                try { d.TargetTagUID = r.TagUid; if (writePos) d.TargetPos = r.TargetPos; } catch { }
                Trip("écriture de l'appel impossible : " + e.Message);
                return false;
            }
            r.Moved = true; r.Expect = np; r.SentTag = c.Tag; r.SentPos = c.Pos; _moved++;
            return true;
        }

        /// Only enemy tags where units of the same class really landed in this battle (the script's own places, never "the middle of nowhere"),
        /// outside the bubble + margin, away from zones and spawn points he owns, on the map and inside the play zone. Smallest shift first.
        /// horsVue: first pass, a place his units could see with their own ground optics is refused as well (the caller falls back to the
        /// distance rule alone when no tag passes, so nothing that is legal without this pass ever becomes impossible).
        static Cand Pick(Req r, float now, out string why, int busyExempt = 0, bool horsVue = false)
        {
            float R = PlayerRadius.Value, minPlayer = Math.Max(PlayerMin.Value, R + ExitMargin), maxShift = MaxShift.Value;
            int nSame = 0, fLanded = 0, fShift = 0, fBubble = 0, fBusy = 0, fZone = 0, fSpawn = 0, fPlay = 0, fMap = 0, fTrig = 0, fVue = 0;
            Cand best = null;
            bool pinned = false;
            Cand busyBest = null;
            float busyUsed = 0f;
            bool trigOn = _trigTested && _trigGeo.Count > 0;
            bool trigPartial = _trigListBad || (_trigTested && _trigBad > 0);            // some trigger shapes unknown: short swaps only
            string originTrig = trigOn ? TrigKey(r.Origin) : null;
            bool pzOk = PlayBounds(out var pz);
            foreach (var kv in _proven)
            {
                if (kv.Key.side != _enemySide || kv.Key.cls != r.Cls || kv.Key.tag == r.TagUid) continue;
                nSame++;
                var pr = kv.Value; int tag = kv.Key.tag; V3 pos = pr.Pos;
                if (!pr.Landed) { fLanded++; continue; }
                float shift = EnemyAi.D2(pos, r.Origin);
                if (shift > maxShift) { fShift++; continue; }
                float dm = MinMine(pos), dt = MinTrail(pos);
                // outside the bubble + margin for current AND past positions, and always clearly further from him than the original place
                bool farther = r.NearD < 0f || r.NearD == float.MaxValue || Math.Min(dm, dt) >= r.NearD + ExitMargin;
                if (dm < minPlayer || dt < R + ExitMargin || !farther || _sticky.ContainsKey("t" + tag.ToString(CultureInfo.InvariantCulture))) { fBubble++; continue; }
                if (horsVue && VuParMoi(pos)) { fVue++; continue; }
                float mz = float.MaxValue;
                foreach (var z in _zones) if (z.Held || (Vis(z, now) && z.Owner == _playerSide)) mz = Math.Min(mz, EnemyAi.D2(pos, z.Pos));
                if (mz < PlayerZoneMin) { fZone++; continue; }
                bool sp = false;
                foreach (var pt in _pts.Values) if (pt.Team == _playerSide && pt.Pos != V3.zero && EnemyAi.D2(pos, pt.Pos) < SpawnPointMin) { sp = true; break; }
                if (sp) { fSpawn++; continue; }
                if (pzOk && !InBounds(pz, pos, r.Air ? -50f : 0f)) { fPlay++; continue; }
                if (OnMap(pos) != 1) { fMap++; continue; }
                if (trigOn && (originTrig == null || TrigKey(pos) != originTrig)) { fTrig++; continue; }
                if (trigPartial && shift > TrigUnknownShift) { fTrig++; continue; }
                float pd = Math.Min(dm, dt);
                // a tag used less than TagBusy s ago is only a fallback (the least recently used one), so a wave never piles onto one spot when another exists
                if (tag != busyExempt && _tagUsed.TryGetValue(tag, out float used) && now - used < TagBusy)
                {
                    fBusy++;
                    if (busyBest == null || used < busyUsed) { busyBest = new Cand { Tag = tag, Pos = pos, Rank = pr.Exact ? 2 : 1, Shift = shift, PlayerD = pd, Reused = true }; busyUsed = used; }
                    continue;
                }
                // the place the same group and place was already sent to wins when it is still valid (the group keeps arriving from one place)
                if (tag == busyExempt) { best = new Cand { Tag = tag, Pos = pos, Rank = pr.Exact ? 2 : 1, Shift = shift, PlayerD = pd }; pinned = true; continue; }
                if (!pinned && (best == null || shift < best.Shift - 1f || (Math.Abs(shift - best.Shift) <= 1f && pd > best.PlayerD)))
                    best = new Cand { Tag = tag, Pos = pos, Rank = pr.Exact ? 2 : 1, Shift = shift, PlayerD = pd };
            }
            if (best == null && busyBest != null) best = busyBest;
            why = best != null ? null : $"tags ennemis même classe {nSame} ; refusés : pas d'atterrissage prouvé {fLanded}, trop loin (> {maxShift:0} m) {fShift}, près de tes unités {fBubble}, dans la vue de tes unités {fVue}, déjà utilisé il y a moins de {TagBusy:0} s {fBusy}, près d'une zone à toi {fZone}, près d'un de tes points d'apparition {fSpawn}, hors zone jouable {fPlay}, hors carte {fMap}, déclencheurs différents {fTrig}";
            return best;
        }

        /// Current play zone (script-restricted area) when readable.
        static bool PlayBounds(out UnityEngine.Bounds b)
        {
            b = default;
            try
            {
                PlayZoneCtl pz = null;
                try { pz = Il2CppBrokenArrow.Client.Ecs.BattleSystem.TargetSearchHelper._mpCheckZone; } catch { }
                pz ??= PlayZone;
                if (pz == null) { LogOnce("pz-null", "zone jouable pas encore connue : seule la carte est contrôlée"); return false; }
                b = pz.PlayZoneBounds;
                if (b.size.x < 100f || b.size.z < 100f) { LogOnce("pz-empty", "zone jouable vide : seule la carte est contrôlée"); return false; }
                LogOnce("pz-ok", $"zone jouable lue : x {b.min.x:0}..{b.max.x:0}, z {b.min.z:0}..{b.max.z:0}");
                return true;
            }
            catch (Exception e) { LogOnce("pz-ex", "zone jouable illisible : " + e.Message); return false; }
        }

        static bool InBounds(UnityEngine.Bounds b, V3 p, float margin) =>
            p.x >= b.min.x + margin && p.x <= b.max.x - margin && p.z >= b.min.z + margin && p.z <= b.max.z - margin;

        /// Reads every trigger's shape once per battle from the scenario data. The engine's IsPointInsideTrigger threw a
        /// NullReferenceException in every mission (probably a scene object absent in battle), so it is never called.
        static void TriggerGeometry()
        {
            _trigGeo.Clear(); _trigBad = 0;
            int circles = 0, boxes = 0, lines = 0;
            foreach (var td in _triggers)
            {
                try
                {
                    var g = new TrigGeo { Uid = td.UID, Circle = td.TriggerType == TriggerTypeEnum.Circle };
                    g.C = Il2Cpp.ClassExtensions.ToUnityVector(td.GetActualPos);
                    var s = Il2Cpp.ClassExtensions.ToUnityVector(td.Scale);
                    g.Sx = Math.Abs(s.x); g.Sz = Math.Abs(s.z);
                    var q = Il2Cpp.ClassExtensions.ToUnityQuaternion(td.Rotate);
                    float len = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                    if (len > 1e-4f) { g.Qx = q.x / len; g.Qy = q.y / len; g.Qz = q.z / len; g.Qw = q.w / len; }
                    if (g.Sx < 0.01f && g.Sz < 0.01f) { _trigBad++; LogOnce("trig-zero" + g.Uid, $"déclencheur {g.Uid} '{td.DisplayedName}' sans taille lisible : ignoré (échange limité à {TrigUnknownShift:0} m)"); continue; }
                    _trigGeo.Add(g);
                    if (g.Circle) circles++; else boxes++;
                    if (lines++ < 6) Log($"déclencheur {g.Uid} '{td.DisplayedName}' {(g.Circle ? "cercle" : "boîte")} centre ({g.C.x:0},{g.C.z:0}) taille ({g.Sx:0},{g.Sz:0}) rotation ({g.Qx:0.00},{g.Qy:0.00},{g.Qz:0.00},{g.Qw:0.00})");
                }
                catch (Exception e) { _trigBad++; LogOnce("trig-geo", "forme d'un déclencheur illisible : " + e.Message + $" (échange limité à {TrigUnknownShift:0} m)"); }
            }
            _trigTested = true;
            _trigReadable = _trigBad == 0;
            if (_triggers.Count > 0)
                Log($"déclencheurs : forme lue pour {_trigGeo.Count}/{_triggers.Count} (cercles {circles}, boîtes {boxes}, illisibles {_trigBad}) ; la taille peut être une demi-taille ou une taille pleine : un nouvel endroit n'est accepté que s'il est dans les mêmes déclencheurs pour les deux lectures");
        }

        static string TrigState() => !_trigTested ? (_trigListBad ? "liste illisible" : "pas fait") : _triggers.Count == 0 ? "aucun déclencheur" : _trigReadable ? $"géométrie ({_trigGeo.Count})" : $"géométrie partielle ({_trigGeo.Count}/{_triggers.Count})";

        /// Point inside a trigger with its size read as k times the scale (k = 0.5 half size, k = 1 full size). Pure math, never throws.
        static bool Inside(TrigGeo g, V3 p, float k)
        {
            float vx = p.x - g.C.x, vy = p.y - g.C.y, vz = p.z - g.C.z;
            if (g.Circle) { float r = Math.Max(g.Sx, g.Sz) * k; return vx * vx + vz * vz <= r * r; }
            // rotate v by the inverse rotation (conjugate u = -q.xyz): v' = v + w t + u x t, t = 2 u x v
            float ux = -g.Qx, uy = -g.Qy, uz = -g.Qz, w = g.Qw;
            float tx = 2f * (uy * vz - uz * vy), ty = 2f * (uz * vx - ux * vz), tz = 2f * (ux * vy - uy * vx);
            float lx = vx + w * tx + (uy * tz - uz * ty);
            float lz = vz + w * tz + (ux * ty - uy * tx);
            return Math.Abs(lx) <= g.Sx * k && Math.Abs(lz) <= g.Sz * k;
        }

        /// Triggers containing p under both size readings, e.g. "0d,4p," (d = inside even at half size, p = only at full size).
        /// Two places with the same key are inside the same triggers whichever reading is right.
        static string TrigKey(V3 p)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _trigGeo.Count; i++)
            {
                var g = _trigGeo[i];
                if (!Inside(g, p, 1f)) continue;
                sb.Append(i).Append(Inside(g, p, 0.5f) ? 'd' : 'p').Append(',');
            }
            return sb.ToString();
        }

        /// 1 = on the map, 0 = off the map, -1 = map unreadable (fail closed). Candidates are real landing places, so no edge margin.
        static int OnMap(V3 p)
        {
            try
            {
                var m = MapSvc._map;
                if (m == null) { LogOnce("map-null", "données de carte absentes : aucune cible acceptée"); return -1; }
                return m.PointIsOnMap(p) ? 1 : 0;
            }
            catch (Exception e) { LogOnce("map-ex", "test de la carte impossible : " + e.Message); return -1; }
        }

        static void Trip(string why)
        {
            if (_tripped) { LogOnce("trip:" + why, "(disjoncteur déjà coupé) " + why); return; }
            _tripped = true; _tripWhy = why;
            Mod.Log.Warning("[SPAWN] disjoncteur : " + why + " -> plus aucun déplacement de renfort pendant cette bataille (observation seulement)");
        }

        // ------------------------------------------------------------ observation: new enemy units matched to calls
        static void Observe(float now)
        {
            var arr = _enemyArr;
            if (arr == null) return;
            if (!_baseline)
            {
                for (int i = 0; i < arr.Length; i++) { try { if (arr[i] != null) _known.Add(arr[i].UID); } catch { } }
                _baseline = true;
                return;
            }
            for (int i = 0; i < arr.Length; i++)
            {
                try
                {
                    var u = arr[i];
                    if (u == null || !u.IsAlive() || !_known.Add(u.UID)) continue;
                    Match(u, u.GetPosition(), now);
                }
                catch { }
            }
            for (int k = _batches.Count - 1; k >= 0; k--)
            {
                var b = _batches[k];
                if (b.Relocated && !b.VerifyDone && b.TReloc >= 0f && now - b.TReloc > VerifyWindow)
                {
                    b.VerifyDone = true;
                    Trip($"lot #{b.Id} groupe '{b.Group}' : aucune unité vue au nouvel endroit en {VerifyWindow:0} s ({b.Seen} vue(s) à moins de {MatchDist:0} m, aucune apparition exacte vérifiée)");
                }
                if (!b.Closed)
                {
                    // never before BatchWindow since the last call: a later call of the group at the same place must still join (and follow) this batch
                    bool done = (b.FirstSeen >= 0 && b.Seen >= b.Expected && now - b.FirstSeen > 4f && now - b.TLast > BatchWindow) || now - b.TLast > 60f;
                    if (done) { b.Closed = true; WriteResult(b, now); }
                }
                if (b.Closed && now - b.TLast > 120f && (!b.Relocated || b.VerifyDone)) _batches.RemoveAt(k);
            }
            if (_byPtr.Count > 0)
            {
                List<IntPtr> empty = null;
                foreach (var kv in _byPtr)
                {
                    _byPtrCount -= kv.Value.RemoveAll(q => now - q.T > 120f);
                    if (kv.Value.Count == 0) (empty ??= new List<IntPtr>()).Add(kv.Key);
                }
                if (empty != null) foreach (var p in empty) _byPtr.Remove(p);
                if (_byPtrCount < 0) _byPtrCount = 0;
            }
        }

        static void Match(LuaUnit u, V3 p, float now)
        {
            // 1) a unit at the old place of a relocated, not yet verified batch trips the breaker, but only when it can be one of the swapped units:
            //    same unit id as the swapped call, and no other call (kept, or from another group) expects units at that spot
            int unitId = 0;
            try { unitId = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            foreach (var b in _batches)
            {
                if (!b.Relocated || b.VerifyDone || b.Side != _enemySide) continue;
                foreach (var r in b.Reqs)
                {
                    if (!r.Moved || EnemyAi.D2(p, r.Origin) > VerifyDist || EnemyAi.D2(p, r.Expect) <= MatchDist) continue;
                    if (unitId <= 0 || r.UnitId <= 0 || unitId != r.UnitId || OtherExpects(p, now)) continue;
                    b.VerifyDone = true;
                    Trip($"lot #{b.Id} groupe '{b.Group}' : {u.Name} (uid {u.UID}, même unité que l'appel échangé #{r.Id}) vu à l'ancien endroit ({p.x:0},{p.z:0})");
                    break;
                }
            }
            // 2) normal matching (counters, results)
            Req best = null; float bd = MatchDist;
            Batch near = null; float nd = MatchDist;
            foreach (var b in _batches)
            {
                if (b.Closed || b.Side != _enemySide) continue;
                foreach (var r in b.Reqs)
                {
                    float d = EnemyAi.D2(p, r.Expect);
                    if (d < nd) { nd = d; near = b; }
                    if (r.SeenN < r.Units && d < bd) { bd = d; best = r; }
                }
            }
            if (best != null)
            {
                best.SeenN++; Saw(best.B, p, now);
                // the poll runs every 2 s and a vehicle may already be driving: tolerance grows with time, capped, and never near the old place
                float tol = Math.Min(VerifyMax, VerifyDist + VerifySpeed * Math.Max(0f, now - best.T));
                if (best.Moved && !best.B.VerifyDone && bd <= tol && EnemyAi.D2(p, best.Origin) > MatchDist && best.B.TReloc >= 0f && now - best.B.TReloc <= VerifyWindow)
                    MarkVerified(best.B, $"(sondage) {u.Name} (uid {u.UID}) vu à {bd:0} m du nouvel endroit (tolérance {tol:0} m) après {now - best.T:0} s");
                return;
            }
            if (near != null) { near.Extra++; Saw(near, p, now); return; }
            _free++;
            var z = Nearest(p, now, 1, out float zd);
            if (z != null && zd <= FreeLogDist)
            {
                _freeNear++;
                if (Cap(ref _freeLogs, CapFree)) Log($"apparition sans appel : {u.Name} (uid {u.UID}) à ({p.x:0},{p.z:0}), à {zd:0} m de la zone joueur {z.Id}{(z.Held ? " (tenue)" : "")}");
            }
        }

        /// A recent enemy call that was not swapped expects units within MatchDist of p (so a unit there proves nothing about a swap).
        static bool OtherExpects(V3 p, float now)
        {
            foreach (var b in _batches)
            {
                if (b.Side != _enemySide || now - b.TLast > 60f) continue;
                foreach (var r in b.Reqs) if (!r.Moved && r.Expect != V3.zero && EnemyAi.D2(p, r.Expect) <= MatchDist) return true;
            }
            return false;
        }

        static void Saw(Batch b, V3 p, float now)
        {
            b.Seen++; b.Sum += p;
            if (b.FirstSeen < 0) b.FirstSeen = now;
        }

        static void WriteResult(Batch b, float now)
        {
            if (b.Seen == 0)
            {
                _resNone++;
                if (Cap(ref _resLogs, CapResults)) Log($"résultat lot #{b.Id} {b.Kind} groupe='{b.Group}' tag={b.TagUid} : rien vu près des positions attendues en {now - b.T0:0} s ({b.Expected} attendue(s))");
                return;
            }
            _resSeen++;
            V3 c = b.Sum / b.Seen;
            // the place each call was really sent to (a batch can be split: calls kept before a swap, or a new target after a re-pick)
            var sent = new Dictionary<int, V3>();
            foreach (var r in b.Reqs)
            {
                if (r.Moved && r.SentTag > 0) sent[r.SentTag] = r.SentPos;
                else if (r.TagOk && r.TagUid > 0) sent[r.TagUid] = r.TagPos;
            }
            bool split = sent.Count > 1;
            bool moved = !split && b.Relocated && b.Reqs.Any(r => r.Moved && r.SentTag > 0);
            int ptag = sent.Count == 1 ? sent.Keys.First() : 0;
            bool tagKnown = sent.Count == 1;
            V3 ppos = sent.Count == 1 ? sent.Values.First() : V3.zero;
            string proof = split ? $" -> lot partagé entre {sent.Count} endroits (tags {string.Join(", ", sent.Keys)}) : pas de preuve par sondage" : "";
            if (ptag > 0 && tagKnown)
            {
                // polling proof: only classes of requests that really got a unit (never the "extra" units);
                // a landing counts only when the units' centre is within LandDist of the tag (not when they already drove away)
                var classes = new HashSet<string>();
                foreach (var r in b.Reqs) if (r.SeenN > 0 && r.Cls != "?") classes.Add(r.Cls);
                float dc = EnemyAi.D2(c, ppos);
                bool landed = dc <= LandDist && (!b.Relocated || b.Verified);
                foreach (var cls in classes)
                {
                    var key = (b.Side, ptag, cls);
                    if (!_proven.TryGetValue(key, out var pr)) { _proven[key] = pr = new Proof(); pr.Pos = ppos; }
                    if (!pr.Exact) pr.Pos = ppos;
                    pr.Polled = true; pr.T = now; pr.N += b.Seen;
                    if (landed) pr.Landed = true;
                }
                if (classes.Count > 0) proof = $" -> tag {ptag} {(landed ? "prouvé (atterrissage vu par sondage)" : "vu mais pas d'atterrissage prouvé")} pour le camp {b.Side} ({string.Join(", ", classes)})";
            }
            string where;
            if (tagKnown) where = $"à {EnemyAi.D2(c, ppos):0} m du tag";
            else
            {
                V3 se = V3.zero; int ne = 0;
                foreach (var r in b.Reqs) if (r.Expect != V3.zero) { se += r.Expect; ne++; }
                where = ne > 0 ? $"à {EnemyAi.D2(c, se / ne):0} m de la position attendue (tag ?)" : "tag ?";
            }
            if (Cap(ref _resLogs, CapResults))
                Log($"résultat lot #{b.Id} {b.Kind} groupe='{b.Group}' : {b.Seen}/{b.Expected} unité(s) vue(s){(b.Extra > 0 ? $" (dont {b.Extra} en plus)" : "")} après {b.FirstSeen - b.T0:0.0} s, centre ({c.x:0},{c.z:0}) {where}{(b.Relocated ? (b.Verified ? " (déplacement vérifié)" : " (déplacement NON vérifié)") : "")}{proof}");
        }

        // ------------------------------------------------------------ log-only hook events (queued, processed here on the main thread)
        internal static void Push(Ev e)
        {
            Interlocked.Increment(ref _hk[e.Kind]);
            if (Interlocked.Increment(ref _qCount) > QueueMax) { Interlocked.Decrement(ref _qCount); Interlocked.Increment(ref _qDropped); return; }
            _q.Enqueue(e);
        }

        internal static void HookError(string what, Exception ex)
        {
            try { Push(new Ev { Kind = EvError, S1 = what, S2 = ex?.Message }); } catch { }
        }

        static void Discard() { while (_q.TryDequeue(out _)) Interlocked.Decrement(ref _qCount); }

        static void Drain(float now)
        {
            // every event uses the drain time (game time, at most 2 s late): mixing real ticks with Time.time was wrong after a pause or at x2 speed
            for (int n = 0; n < DrainMax && _q.TryDequeue(out var e); n++)
            {
                Interlocked.Decrement(ref _qCount);
                try
                {
                    switch (e.Kind)
                    {
                        case EvMoveComplex: OrderLog("ordre complexe", e, now); break;
                        case EvUnload: OrderLog("débarquement", e, now); break;
                        case EvRunMove: OrderLog("déplacement simple", e, now); break;
                        case EvGroupVisible: if (e.B1) AddReveal(e.S1, 0, now); else _hides++; break;
                        case EvUnitVisible: if (e.B1) AddReveal(null, e.I1, now); else _hides++; break;
                        case EvWave: Wave(e, now); break;
                        case EvSpawned: Exact(e, now); break;
                        case EvLanded: Landed(e, now); break;
                        case EvError: LogOnce("hook:" + e.S1, $"crochet {e.S1} en erreur : {e.S2}"); break;
                    }
                }
                catch (Exception ex) { LogOnce("ev" + e.Kind, $"traitement du crochet {e.Kind} : {ex.Message}"); }
            }
        }

        static bool TagPos(int tag, out V3 pos)
        {
            pos = V3.zero;
            if (tag <= 0 || _scen == null) return false;
            try { return ScenarioDataHandlerExt.TryGetTagPos(_scen, tag, out pos); } catch { return false; }
        }

        static void OrderLog(string what, Ev e, float now)
        {
            _ordersSeen++;
            V3 dest = e.V1; string src = "vecteur";
            if (TagPos(e.I2, out var tp)) { dest = tp; src = "tag " + e.I2; }
            var z = Nearest(dest, now, 1, out float zd);
            string recent = "";
            if (!string.IsNullOrEmpty(e.S1) && _groupLast.TryGetValue(GroupKey(_enemySide, e.S1), out float gt))
                recent = z != null && z.Held && gt >= z.HeldSince ? ", groupe ennemi appelé APRÈS la capture" : $", groupe ennemi appelé à t={gt - _start:0}s";
            if ((z == null || zd > FreeLogDist) && recent.Length == 0) return;       // far from the player's zones and not a known enemy reinforcement
            string zone = z == null ? "aucune zone joueur" : $"zone joueur {z.Id} à {zd:0} m{(z.Held ? $" (tenue depuis t={z.HeldSince - _start:0}s)" : "")}";
            if (Cap(ref _orderLogs, CapOrders))
                Log($"{what} t≈{now - _start:0}s groupe='{e.S1}' unité={e.I1} dest=({dest.x:0},{dest.z:0}) ({src}){e.S2} -> {zone}{recent} (observation seulement)");
        }

        static void AddReveal(string group, int uid, float now)
        {
            if (_reveals.Count < 300) _reveals.Add(new Reveal { Due = now + 2f, Group = group, Uid = uid });
        }

        static void Reveals(GameController gc, float now)
        {
            if (_reveals.Count == 0) return;
            var bus = gc._GetEcsEventBus_k__BackingField?.Gameplay;
            for (int k = _reveals.Count - 1; k >= 0; k--)
            {
                var rv = _reveals[k];
                if (now < rv.Due) continue;
                _reveals.RemoveAt(k);
                try
                {
                    V3 p = V3.zero; string who;
                    if (rv.Group != null)
                    {
                        who = $"groupe '{rv.Group}'";
                        try { var f = bus?.GetUnitGroupsPosition; if (f != null) p = f.Invoke(rv.Group, false); }
                        catch (Exception e) { LogOnce("grp-pos", "position de groupe illisible : " + e.Message); }
                    }
                    else
                    {
                        who = $"unité uid {rv.Uid}";
                        if (!FindUnit(_enemyArr, rv.Uid, "ennemie", ref p, ref who)) FindUnit(_playerArr, rv.Uid, "du camp joueur", ref p, ref who);
                    }
                    _revealsDone++;
                    if (!Cap(ref _revealLogs, CapReserves)) continue;
                    if (p == V3.zero) { Log($"réserve révélée : {who}, position inconnue"); continue; }
                    var z = Nearest(p, now, 1, out float zd);
                    Log($"réserve révélée : {who} à ({p.x:0},{p.z:0}){(z != null ? $", à {zd:0} m de la zone joueur {z.Id}{(z.Held ? " (tenue)" : "")}" : "")} (jamais bloquée ni déplacée)");
                }
                catch (Exception e) { LogOnce("reveal", "réserve révélée illisible : " + e.Message); }
            }
        }

        static bool FindUnit(Il2CppReferenceArray<LuaUnit> arr, int uid, string camp, ref V3 p, ref string who)
        {
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                try
                {
                    var u = arr[i];
                    if (u == null || u.UID != uid) continue;
                    p = u.GetPosition(); who = $"{u.Name} (uid {uid}, {camp})";
                    return true;
                }
                catch { }
            }
            return false;
        }

        static void Wave(Ev e, float now)
        {
            int side = SideOfOwner(e.I2);
            bool ok = TagPos(e.I1, out var tagPos);
            V3 exp = ok ? tagPos : e.V1;
            Zone z = null; float zd = 0f;
            if (exp != V3.zero) z = Nearest(exp, now, 1, out zd);
            string zone = z != null ? $" -> zone joueur {z.Id} à {zd:0} m{(z.Held ? " (tenue)" : "")}" : "";
            if (Cap(ref _callLogs, CapCalls))
                Log($"vague multiple t≈{now - _start:0}s camp={side} groupe='{e.S1}' unités={e.I3} tag={e.I1}{(ok ? $"({tagPos.x:0},{tagPos.z:0})" : "")} pos=({e.V1.x:0},{e.V1.z:0}){zone} (journal seulement : chaque unité de la vague passe par l'appel simple, jugé là)");
        }

        static bool GroupOk(string spawnGroups, string callGroup)
        {
            string g = spawnGroups ?? "", c = callGroup ?? "";
            if (g == c) return true;
            if (c.Length == 0) return false;
            foreach (var part in g.Split(',', ';', '|')) if (part.Trim() == c) return true;
            return false;
        }

        /// Exact spawn (InvokeUnitSpawned). A pointer can serve several calls: the request used must have spare capacity, the same group,
        /// the same unit id (when both are known) and a spawn place within MatchDist of its expected or original place; otherwise "ambiguë".
        static void Exact(Ev e, float now)
        {
            if (e.P == IntPtr.Zero || !_byPtr.TryGetValue(e.P, out var list) || list.Count == 0) { if (e.I2 == _enemySide) _exactNoReq++; return; }
            bool noPos = e.V1 == V3.zero;
            Req r = null; float rd = float.MaxValue;
            foreach (var q in list)
            {
                if (q.ExactN >= q.Units || !GroupOk(e.S1, q.Group)) continue;
                if (e.I3 > 0 && q.UnitId > 0 && e.I3 != q.UnitId) continue;
                float d = noPos ? 0f : Math.Min(EnemyAi.D2(e.V1, q.Expect), EnemyAi.D2(e.V1, q.Origin));
                if (!noPos && d > MatchDist) continue;
                if (d < rd) { rd = d; r = q; }       // strict '<': the oldest request wins a tie (list is in call order)
            }
            if (r == null)
            {
                _exactAmbig++;
                if (e.I2 == _enemySide && Cap(ref _exactLogs, CapExact))
                    Log($"apparition exacte ambiguë : uid={e.I1} unité={e.I3} groupe='{e.S1}' lieu=({e.V1.x:0},{e.V1.z:0}) ; {list.Count} appel(s) sur ce pointeur ({string.Join(" ", list.Take(4).Select(q => $"#{q.Id} unité={q.UnitId} groupe='{q.Group}' {q.ExactN}/{q.Units}"))}) : ni vérification ni preuve");
                return;
            }
            r.ExactN++; _exactMatched++;
            bool moved = r.Moved && r.SentTag > 0;
            V3 tagRef = moved ? r.SentPos : r.TagPos;
            float dT = EnemyAi.D2(e.V1, r.Expect);
            float dTag = moved || r.TagOk ? EnemyAi.D2(e.V1, tagRef) : -1f;
            if (noPos) _spZero++;
            else { if (dT <= 5f) _spAtTarget++; if (dTag >= 0f && dTag <= 5f) _spAtTag++; }
            if (r.Enemy && Cap(ref _exactLogs, CapExact))
                Log($"apparition exacte appel #{r.Id} uid={e.I1} groupe='{e.S1}' lieu=({e.V1.x:0},{e.V1.z:0}) demandé=({r.Expect.x:0},{r.Expect.z:0}) à {dT:0} m, tag {(moved ? r.SentTag : r.TagUid)} à {(dTag < 0f ? "?" : dTag.ToString("0", CultureInfo.InvariantCulture))} m, ordre initial={(e.B1 ? $"({e.V2.x:0},{e.V2.z:0})" : "aucun")}{(e.B2 ? " (unité d'éditeur)" : "")}");
            if (noPos) return;
            if (moved && !r.B.VerifyDone)
            {
                if (dT <= VerifyDist) MarkVerified(r.B, $"apparu à {dT:0} m du nouvel endroit");
                else if (EnemyAi.D2(e.V1, r.Origin) <= VerifyDist) { r.B.VerifyDone = true; Trip($"lot #{r.B.Id} groupe '{r.Group}' : apparu à l'ancien endroit malgré le changement de tag"); }
            }
            // exact proof, per unit class: enemy call, tag known, unit really placed at the tag (or at the requested place next to it)
            int ptag = moved ? r.SentTag : r.TagUid;
            if (r.Enemy && e.I2 != _playerSide && ptag > 0 && dTag >= 0f && r.Cls != "?" && (dTag <= VerifyDist || dT <= VerifyDist))
            {
                var key = (r.Side, ptag, r.Cls);
                if (!_proven.TryGetValue(key, out var pr)) _proven[key] = pr = new Proof();
                bool first = !pr.Exact;
                pr.Exact = true; pr.Pos = tagRef; pr.T = now; pr.N++;
                if (dTag <= LandDist || dT <= LandDist) pr.Landed = true;
                if (first) LogOnce($"preuve:{r.Side}:{ptag}:{r.Cls}", $"tag {ptag} prouvé (apparition exacte) pour le camp {r.Side} classe {r.Cls} : appel #{r.Id} apparu à {dTag:0} m du tag");
            }
        }

        // ------------------------------------------------------------ landings (NodeSpawnUnit / NodeSpawnMultiUnits.OnSpawned): where each unit really appeared
        /// Lazy patch with its own Harmony id, first battle only; the crash guard refuses it after two battles that did not end normally.
        static void LandingHook()
        {
            if (_landRefused || _unclean == null) return;
            if (!_sessionArmed)
            {
                if (!_landPatched && _unclean.Value >= 2)
                {
                    _landRefused = true;
                    Mod.Log.Warning("[SPAWN] crochet d'atterrissage coupé par sécurité (les deux dernières parties ne se sont pas terminées normalement) : preuve par sondage seulement");
                    return;
                }
                _sessionArmed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();
            }
            if (_landPatched) return;
            try
            {
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Renforts");
                int n = 0;
                var m1 = AccessTools.Method(typeof(NodeSpawnUnit), nameof(NodeSpawnUnit.OnSpawned));
                if (m1 != null) { _harmony.Patch(m1, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patch_Landed), nameof(Patch_Landed.PostSingle)))); n++; }
                var m2 = AccessTools.Method(typeof(NodeSpawnMultiUnits), nameof(NodeSpawnMultiUnits.OnSpawned));
                if (m2 != null) { _harmony.Patch(m2, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patch_Landed), nameof(Patch_Landed.PostMulti)))); n++; }
                _landPatched = true;
                Log($"crochet d'atterrissage installé ({n}/2) : chaque unité apparue par le script est comparée au lieu demandé");
            }
            catch (Exception e)
            {
                _landRefused = true;
                Mod.Log.Warning("[SPAWN] crochet d'atterrissage impossible : " + e.GetBaseException().Message + " (preuve par sondage seulement)");
            }
        }

        static volatile bool _busPosBad;
        static int _busBadN;

        /// Position of a unit at the moment it lands (hook side); zero when unreadable or already judged unreliable in this game.
        internal static V3 PosNow(int uid)
        {
            if (_busPosBad) return V3.zero;
            try
            {
                var f = GameController._instance?._GetEcsEventBus_k__BackingField?.Gameplay?.GetObjectPosition;
                return f != null ? f.Invoke(uid, (int)MEObjectTypeEnum.Unit) : V3.zero;
            }
            catch { _busPosBad = true; return V3.zero; }
        }

        static void RetryLandings(float now)
        {
            if (_landPending.Count == 0) return;
            var list = _landPending.ToList();
            _landPending.Clear();
            foreach (var e in list) Landed(e, now);
        }

        static bool FindLua(Il2CppReferenceArray<LuaUnit> arr, int uid, out LuaUnit found)
        {
            found = null;
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                try { var u = arr[i]; if (u != null && u.UID == uid) { found = u; return true; } } catch { }
            }
            return false;
        }

        /// One unit landed. Matched to the open enemy call of the same group whose requested place (or old place, if swapped) is nearest;
        /// tolerance grows with the time since the landing (the unit may already drive). Proves the tag, verifies or trips a swap.
        static void Landed(Ev e, float now)
        {
            if (e.Tries == 0) Missions.NoteSpawn(e.I1, e.S1);                    // script-spawned unit: protected from the mod's enemy AI for a while
            bool enemy = FindLua(_enemyArr, e.I1, out var u);
            if (!enemy && !FindLua(_playerArr, e.I1, out u))
            {
                if (e.Tries++ < 3 && _landPending.Count < 400) _landPending.Add(e); else _landMissed++;
                return;
            }
            if (!enemy) return;
            V3 p; try { p = u.GetPosition(); } catch { return; }
            float age = Math.Max(0f, (Environment.TickCount64 - e.Tick) / 1000f);
            string src = "sondage";
            if (e.V1 != V3.zero && !_busPosBad)
            {
                // the position read inside the hook is exact at landing; checked against the live position on ground units only
                // (aircraft move too fast), with the game speed taken into account; three inconsistent readings stop its use for the battle
                bool air = (TypeOf(u) & (8 | 16)) != 0;
                float gap = EnemyAi.D2(e.V1, p), speed = 1f;
                try { speed = Math.Max(1f, UnityEngine.Time.timeScale); } catch { }
                if (!air && gap > 150f + 40f * age * speed)
                {
                    if (++_busBadN >= 3) { _busPosBad = true; Log($"position lue au moment de l'apparition incohérente {_busBadN} fois (dernière : uid {e.I1}, écart {gap:0} m après {age:0.0} s) : plus utilisée dans cette bataille"); }
                }
                else { p = e.V1; age = 0f; src = "crochet"; }
            }
            float tol = Math.Min(80f, LandDist + 15f * age);
            Req best = null; float bd = float.MaxValue; bool atOld = false;
            foreach (var b in _batches)
            {
                if (b.Side != _enemySide || now - b.TLast > 60f) continue;
                foreach (var r in b.Reqs)
                {
                    if (r.LandedN >= r.Units || !GroupOk(e.S1, r.Group) || r.Expect == V3.zero) continue;
                    float d = EnemyAi.D2(p, r.Expect);
                    if (d < bd) { bd = d; best = r; atOld = false; }
                    if (r.Moved) { float d0 = EnemyAi.D2(p, r.Origin); if (d0 < bd) { bd = d0; best = r; atOld = true; } }
                }
            }
            if (best == null || bd > tol)
            {
                if (Cap(ref _landLogs, CapLanded)) Log($"atterrissage uid={e.I1} {u.Name} groupe='{e.S1}' à ({p.x:0},{p.z:0}) : aucun appel à moins de {tol:0} m{(best != null ? $" (le plus proche #{best.Id} à {bd:0} m)" : "")}");
                return;
            }
            var bb = best.B;
            if (atOld && OtherExpects(p, now))
            {
                // a call of the same group that was not swapped expects units here: nothing proves the swap was ignored
                if (Cap(ref _landLogs, CapLanded)) Log($"atterrissage uid={e.I1} {u.Name} groupe='{e.S1}' à l'ancien endroit du lot #{bb?.Id}, mais un appel non échangé attend aussi des unités ici : ni preuve ni disjoncteur");
                return;
            }
            best.LandedN++; _landed++;
            if (best.Moved && bb != null && !bb.VerifyDone)
            {
                if (!atOld) MarkVerified(bb, $"(atterrissage) {u.Name} uid {e.I1} apparu à {bd:0} m du nouvel endroit");
                else { bb.VerifyDone = true; Trip($"lot #{bb.Id} groupe '{best.Group}' : {u.Name} uid {e.I1} apparu à l'ancien endroit malgré l'échange"); }
            }
            if (Cap(ref _landLogs, CapLanded)) Log($"atterrissage uid={e.I1} {u.Name} appel #{best.Id} à {bd:0} m du lieu {(atOld ? "D'ORIGINE" : "demandé")} (point {best.PointUid}, position {src}, après {age:0.0} s)");
            if (atOld) return;
            bool swapped = best.Moved && best.SentTag > 0;
            int ptag = swapped ? best.SentTag : best.TagUid;
            if (ptag <= 0 || (!swapped && !best.TagOk) || best.Cls == "?") return;
            V3 tpos = swapped ? best.SentPos : best.TagPos;
            float dTag = EnemyAi.D2(p, tpos);
            if (dTag > tol && bd > tol) return;
            var key = (best.Side, ptag, best.Cls);
            if (!_proven.TryGetValue(key, out var pr)) _proven[key] = pr = new Proof();
            bool first = !pr.Landed;
            pr.Exact = pr.Landed = true; pr.Pos = tpos; pr.T = now; pr.N++;
            if (first) LogOnce($"atterri:{best.Side}:{ptag}:{best.Cls}", $"tag {ptag} prouvé (atterrissage) pour le camp {best.Side} classe {best.Cls} : {u.Name} à {dTag:0} m du tag");
        }

        // ------------------------------------------------------------ helpers and summary
        static int SideOfOwner(int uid)
        {
            try { var ctx = Campaign.Ctx(); if (ctx != null && ctx.TryGetPlayer(uid, out var p) && p != null) return (int)p.TeamSide; } catch { }
            return -1;
        }

        static bool InList(string value, string list)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(list)) return false;
            foreach (var part in list.Split(';')) if (string.Equals(part.Trim(), value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Summary(float now, string label)
        {
            int held = _zones.Count(z => z.Held);
            int provenTags = _proven.Keys.Where(k => k.side == _enemySide).Select(k => k.tag).Distinct().Count();
            int exactTags = _proven.Where(kv => kv.Key.side == _enemySide && kv.Value.Exact).Select(kv => kv.Key.tag).Distinct().Count();
            int landedTags = _proven.Where(kv => kv.Key.side == _enemySide && kv.Value.Landed).Select(kv => kv.Key.tag).Distinct().Count();
            string reasons = _reasons.Count == 0 ? "aucune" : string.Join(", ", _reasons.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value));
            string flight = _inFlight == null ? "aucun" : $"#{_inFlight.Id} {(_inFlight.Verified ? "vérifié" : _inFlight.VerifyDone ? "échec" : "en cours")}";
            Log($"bilan ({label}) t={(_start < 0 ? 0 : now - _start):0}s mode={ModeName(_modeCache)} : appels ennemis={_enemyCalls} autres={_allied} lots={_batchSeq} déplacés={_moved} aurait déplacé={_would} gardés={_kept} ; raisons : {reasons}");
            Log($"bilan bulle : rayon {PlayerRadius.Value:0} m, tes unités suivies={_mine.Count} ({(_mineOk ? "lisibles" : "ILLISIBLES")}), positions passées={_trail.Count}, endroits collants={_sticky.Count} ; appels près de toi={_near} dont jugés échangeables={Math.Max(0, _near - _nearNoTag - _nearEarly)}, appels sans endroit sûr (gardés)={_impossible}, sans tag (non protégés)={_nearNoTag}, début de mission={_nearEarly}" +
                $" ; renforts déplacés hors de vue de tes unités={_horsVue}, repli sur la règle de distance ({PlayerMin.Value:0} m) faute de tag hors de vue={_repli}");
            Log($"bilan portes : disjoncteur={(_tripped ? "COUPÉ (" + _tripWhy + ")" : "armé")} échange vérifié dans la bataille={(_everVerified ? "oui" : "non")} lot en vérification={flight} groupes bloqués={_blockedGroups.Count} test déclencheurs={TrigState()} tags scénario={_tags.Count} tags ennemis vus={provenTags} (atterrissage prouvé {landedTags}, par crochet {exactTags}) zones tenues={held}/{_zones.Count} lots non suivis (trop de lots)={_batchOverflow}");
            Log($"bilan atterrissages : crochet={(_landPatched ? "installé" : _landRefused ? "refusé" : "pas installé")} liés à un appel={_landed} introuvables={_landMissed} en attente={_landPending.Count} ; jamais protégés par cette règle : unités débarquées par les ordres du script, réserves révélées, appels sans tag");
            Log($"bilan observation : résultats vus={_resSeen} rien vu={_resNone} apparitions sans appel={_free} (dont {_freeNear} à moins de {FreeLogDist:0} m d'une zone joueur) ordres près des zones={_ordersSeen} réserves révélées={_revealsDone} cachées={_hides} ; apparitions exactes liées à un appel={_exactMatched} (lieu=demandé {_spAtTarget}, lieu=tag {_spAtTag}, lieu nul {_spZero}) ambiguës={_exactAmbig} ennemies sans appel={_exactNoReq}");
            Log($"bilan crochets : simple={_hk[EvSingle]} vague={_hk[EvWave]} ordre complexe={_hk[EvMoveComplex]} débarquement={_hk[EvUnload]} déplacement simple={_hk[EvRunMove]} groupe visible={_hk[EvGroupVisible]} unité visible={_hk[EvUnitVisible]} apparition exacte={_hk[EvSpawned]} atterrissage={_hk[EvLanded]} erreurs={_hk[EvError]} file perdue={_qDropped} lignes non écrites (plafonds)={_droppedLogs}");
        }
    }

    // ---------------------------------------------------------------- Harmony patches (one class each; the original always runs)
    /// The only hook that may change something (ModeRenforts=auto): TargetTagUID + TargetPos of an enemy ground tag spawn.
    [HarmonyPatch(typeof(SpawnService), nameof(SpawnService.SpawnUnit), new Type[] { typeof(SpawnNodeData) })]
    static class Patch_SpawnSingle
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(SpawnNodeData data)
        {
            if (data != null && Spawns.Active) Guard.Run("Spawns.Simple", () => Spawns.OnSingle(data));
        }
    }

    /// Multi-unit wave node (replaces the UniTask SpawnMultipleUnits hook), log only.
    [HarmonyPatch(typeof(NodeSpawnMultiUnits), nameof(NodeSpawnMultiUnits.OnActivated), new Type[] { })]
    static class Patch_SpawnWaveNode
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(NodeSpawnMultiUnits __instance)
        {
            if (__instance == null || !Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvWave, I2 = -1 };
                try { e.S1 = __instance.UnitGroup; } catch { }
                try { e.V1 = __instance.TargetPosition; } catch { }
                try { e.I1 = __instance.TargetTag?.DataUID ?? 0; } catch { }
                try { e.I2 = __instance.PlayerOwner?.DataUID ?? -1; } catch { }
                try { e.I3 = __instance.UnitInfo?.TotalUnitsCount ?? 0; } catch { }
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("vague multiple", ex); }
        }
    }

    [HarmonyPatch(typeof(MoveComplexSys), nameof(MoveComplexSys.OnMoveComplex), new Type[] { typeof(MoveComplexCmd) })]
    static class Patch_OrderMoveComplex
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(MoveComplexCmd data)
        {
            if (data == null || !Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvMoveComplex, S1 = data.GroupName, I1 = data.UnitUID, I2 = data.TargetTagUID, V1 = data.TargetVector };
                try { e.S2 = $" transport={data.TransportBehavior} trajet={data.DuringRide} arrivée={data.AtDestination} rayon={data.ErrorRadius:0} id={data.ID}"; } catch { }
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("ordre complexe", ex); }
        }
    }

    [HarmonyPatch(typeof(GroupNameSys), nameof(GroupNameSys.UnloadCommand), new Type[] { typeof(UnloadCmd) })]
    static class Patch_OrderUnload
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(UnloadCmd data)
        {
            if (data == null || !Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvUnload, S1 = data.Groups, I1 = data.UnitID, I2 = data.TargetPosTag, V1 = data.TargetPosition };
                try { e.S2 = $" file={data.Queue}"; } catch { }
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("débarquement", ex); }
        }
    }

    [HarmonyPatch(typeof(GroupNameSys), nameof(GroupNameSys.RunMove), new Type[] { typeof(MoveSimpleCmd) })]
    static class Patch_OrderRunMove
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(MoveSimpleCmd data)
        {
            if (data == null || !Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvRunMove, S1 = data.GroupName, I1 = data.UnitUID, I2 = data.TargetTagUID, V1 = data.TargetVector };
                try { e.S2 = $" file={data.Queue}"; } catch { }
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("déplacement simple", ex); }
        }
    }

    [HarmonyPatch(typeof(GroupNameSys), nameof(GroupNameSys.SetVisibleUnitGroupCommands), new Type[] { typeof(bool), typeof(string) })]
    static class Patch_RevealGroup
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(bool isVisible, string groups)
        {
            if (!Spawns.Listening) return;
            try { Spawns.Push(new Spawns.Ev { Kind = Spawns.EvGroupVisible, B1 = isVisible, S1 = groups }); }
            catch (Exception ex) { Spawns.HookError("groupe visible", ex); }
        }
    }

    [HarmonyPatch(typeof(GroupNameSys), nameof(GroupNameSys.SetUnitVisible), new Type[] { typeof(bool), typeof(int) })]
    static class Patch_RevealUnit
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Prefix(bool isVisible, int objectUID)
        {
            if (!Spawns.Listening) return;
            try { Spawns.Push(new Spawns.Ev { Kind = Spawns.EvUnitVisible, B1 = isVisible, I1 = objectUID }); }
            catch (Exception ex) { Spawns.HookError("unité visible", ex); }
        }
    }

    /// Landing postfixes, patched lazily by Spawns.LandingHook (never at launch). Only the unit uid and the node's group are copied.
    static class Patch_Landed
    {
        internal static void PostSingle(NodeSpawnUnit __instance, int uid)
        {
            if (!Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvLanded, I1 = uid, Tick = Environment.TickCount64 };
                try { e.S1 = __instance?.UnitGroup; } catch { }
                e.V1 = Spawns.PosNow(uid);
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("atterrissage", ex); }
        }

        internal static void PostMulti(NodeSpawnMultiUnits __instance, int uid)
        {
            if (!Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvLanded, I1 = uid, Tick = Environment.TickCount64 };
                try { e.S1 = __instance?.UnitGroup; } catch { }
                e.V1 = Spawns.PosNow(uid);
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("atterrissage vague", ex); }
        }
    }

    /// Keeps the play zone controller: the area a mission script restricts the battle to (new reinforcement places must stay inside).
    [HarmonyPatch(typeof(PlayZoneCtl), nameof(PlayZoneCtl.SetPlayZone))]
    static class Patch_PlayZoneSet
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Postfix(PlayZoneCtl __instance)
        {
            try { if (__instance != null) Spawns.PlayZone = __instance; } catch { }
        }
    }

    [HarmonyPatch(typeof(PlayZoneCtl), nameof(PlayZoneCtl.ResetToFull))]
    static class Patch_PlayZoneReset
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Postfix(PlayZoneCtl __instance)
        {
            try { if (__instance != null) Spawns.PlayZone = __instance; } catch { }
        }
    }

    /// Optional diagnostics: where a unit really appeared (SpawnerPosition) vs our TargetPos / tag; RequestPosition is only the initial order.
    [HarmonyPatch(typeof(SpawnService), nameof(SpawnService.InvokeUnitSpawned))]
    static class Patch_SpawnExact
    {
        static bool Prepare() => Spawns.HooksWanted;
        static void Postfix(SpawnData spawnData)
        {
            if (spawnData == null || !Spawns.Listening) return;
            try
            {
                var e = new Spawns.Ev { Kind = Spawns.EvSpawned, I2 = -1 };
                try { e.V1 = spawnData.SpawnerPosition; } catch { }
                try { e.I1 = spawnData.UID; } catch { }
                try { e.I3 = spawnData.UnitIDToSpawn; } catch { }
                try { e.S1 = spawnData.Groups; } catch { }
                try { e.B2 = spawnData.IsMEUnit; } catch { }
                try { var nd = spawnData.SpawnNodeData; e.P = nd != null ? nd.Pointer : IntPtr.Zero; } catch { }
                try { var rq = spawnData.RequestPosition; if (rq != null && rq.HasValue) { e.B1 = true; e.V2 = rq.Value; } } catch { }
                try { var o = spawnData.OwnerInfo; if (o != null) e.I2 = (int)o.TeamSide; } catch { }
                Spawns.Push(e);
            }
            catch (Exception ex) { Spawns.HookError("apparition exacte", ex); }
        }
    }
}
