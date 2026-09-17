// RealismOverhaul - Missions (v0.23.0): mission script safety for every campaign mission (US and RU), solo only, both sides alike.
//  1. Script units: remembers which units the mission script drives. Sources: log-only hooks on the script command systems
//     (complex move, simple move, unload, attack, capture), the engine's own list of active complex moves
//     (MoveComplexSystem._activeWays), units spawned by script nodes (Spawns' landing hook calls NoteSpawn) and group names used by
//     order nodes of the mission graph. EnemyAi asks ScriptReason(uid) and never orders, garrisons, counter-attacks with or cancels
//     these units. When in doubt a unit is protected (long windows, whole battle for groups named by order nodes; cached group
//     membership is never dropped, cached "not a member" answers of cited groups are re-read every 60 s).
//     ScriptReason is only trusted once ProtectionNotReady() returns null (session armed, the 5 order hooks armed, move system known,
//     script graph parsed: a whole node list really read, one full group membership pass done); until then EnemyAi gives no order at all (fail closed).
//  2. Protected places for Demolition (ProtectedAt, main thread): objective zones (hidden ones too), building-entry destinations of script
//     orders, tags named by building-related script nodes, buildings holding script infantry.
//  3. Stuck convoy relaunch: an active script complex move whose units moved less than 50 m in 3 minutes, still far from its destination,
//     not hit for 90 s, no opposing unit within 300 m, engine stage readable and TargetMove, not engaged according to the engine, holding
//     none of the player's own units, may be sent again with the engine's own MoveComplexSystem.ForceMoveToTarget (same way object:
//     same units, destination, stage and callback); again after 5 minutes, at most 3 times per order. ForceMoveToTarget's effect is
//     unproven, so this starts in measurement mode (hidden pref RelanceConvoisEtape = 0: "relance possible" is only logged, then the
//     way is watched: resumed on its own = false positive, still stuck 5 more minutes = proven stall). At battle end, proven stalls with
//     no false positive switch the relaunch on for the next battles (stage 1); a stage-1 battle whose relaunches never moved a convoy
//     (moved nothing in 60 s of game time with no pause or frozen time in that window), or that hit the error kill-switch, switches it
//     back to measurement. Stall timers skip frozen time (pause, end screen, unobserved gaps).
//  4. Read-only diagnostics: the mission script graph (NodeController.RawLoadData, or the .bascr decrypted by EncryptedFileManager) is written
//     once per mission to UserData\RealismOverhaul_missions\<mission>.txt; task changes, script money / deck nodes and sharp money drops
//     are logged; one [MISSION] line every 60 s.
//  Never edits the script, never completes or skips a node, never changes money. Hooks are lazy (own Harmony id), log-only, crash guard,
//  error kill-switch; nothing here changes how units search targets or fire.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Campaign;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.Data;
using Il2CppBrokenArrow.MissionEditor.Data.Trigger;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.MissionEditor.Storage;
using MoveComplexSys = Il2CppBrokenArrow.MissionEditor.Systems.MoveComplexSystem;
using WayMoveData = Il2CppBrokenArrow.MissionEditor.Systems.WayMoveData;
using GroupNameSys = Il2CppBrokenArrow.MissionEditor.Systems.UnitGroupAndNameSystem;
using MoveComplexCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.MoveComplexData;
using MoveSimpleCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.MoveSimpleData;
using UnloadCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.UnloadCommandData;
using AttackCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.AttackUnitData;
using CaptureCmd = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.CaptureZoneData;
using RadiusData = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.AggressiveRadiusData;
using NodeSetMoney = Il2CppBrokenArrow.ScriptEngine.Nodes.Economy.NodeSetMoney;
using NodeChangeDeck = Il2CppBrokenArrow.ScriptEngine.Nodes.Decks.NodeChangePlayerDeck;
using ObjZoneType = Il2CppBrokenArrow.MissionEditor.Data.ObjectiveZone.ObjectiveZoneType;
using EncFiles = Il2CppBrokenArrow.Core.Security.EncryptedFileManager;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using IlIntList = Il2CppSystem.Collections.Generic.IReadOnlyList<int>;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Missions
    {
        const string GuardVersion = "0.24.0";
        const float Step = 0.5f, ScanEvery = 5f, WayEvery = 10f, AreaEvery = 10f, PollEvery = 5f, ReportEvery = 60f;
        const float CmdProtect = 900f, SpawnProtect = 600f, WayAfter = 900f;                 // protection windows (game seconds)
        const float StuckDist = 50f, StuckFirst = 180f, StuckAgain = 300f, NearDest = 200f, QuietFor = 90f, ContactDist = 300f;
        const float EffectWindow = 60f;                                                      // counted game seconds before a relaunch that moved nothing counts as a failure
        const int MaxRelaunch = 3, QueueMax = 2000, MaxIds = 64, MaxHookErrors = 50, OrderHookCount = 5;
        const int EconLogMax = 20, WayLogMax = 120, AssignLogMax = 20;                       // per-battle line caps (the relevé keeps the counts)
        const float CitedRecheckEvery = 60f;
        const long MaxDumpChars = 30_000_000;
        const int KComplex = 0, KSimple = 1, KUnload = 2, KAttack = 3, KCapture = 4, KSetMoney = 5, KDeck = 6;
        static readonly string[] KindNames = { "déplacement complexe", "déplacement simple", "débarquement", "attaque", "capture de zone", "argent fixé", "deck changé" };

        /// Primitives copied inside a hook; processed later on the main thread.
        internal sealed class Cmd { public int Kind, Uid, Tag, Radius = -1, Amount, Player = -1; public int[] Uids; public string Group, AtDest; public V3 Vec; }

        /// Places where Demolition must never collapse a building (built on the main thread, replaced as a whole).
        internal sealed class Areas
        {
            internal readonly float[] X, Z, R; internal readonly string[] Why;
            internal readonly int Zones, Entries, Tags, Occupied;
            internal Areas(List<(float x, float z, float r, string why)> l, int zones, int entries, int tags, int occupied)
            {
                int n = l.Count; X = new float[n]; Z = new float[n]; R = new float[n]; Why = new string[n];
                for (int i = 0; i < n; i++) { X[i] = l[i].x; Z[i] = l[i].z; R[i] = l[i].r; Why[i] = l[i].why; }
                Zones = zones; Entries = entries; Tags = tags; Occupied = occupied;
            }
        }

        sealed class UnitInfo { public int Uid, Eid, Owner, Side, Role, Heal; public V3 Pos; public bool Local; public LuaUnit U; }

        sealed class WayTrack
        {
            public WayMoveData W; public int Id, Side = -1, Tag, Relaunches, LastAlive = -1, LastHeal = -1, Alive, LastOffAlive = -1, LastOffHeal = -1;
            public V3 Anchor, Center, Target, RelaunchPos, WouldPos; public float AnchorT = -1f, LastHit = -999f, LastRelaunch = -999f, FirstSeen, Dist, NearestOpp, WouldAt = -1f;
            public bool StuckLogged, WaitLogged, Engaged, OwnRadius, GaveUpLogged, EffectLogged, WouldJudged; public float EffectAt, UncAt; public string Stage, AtDest;   // UncAt = _wayUncounted when the effect window (re)started
            public readonly HashSet<int> Uids = new();
        }

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        static MelonPreferences_Entry<int> _relanceEtape;              // hidden: 0 = convoy relaunch measured only, 1 = active (switched at battle end from logged proof)
        static HarmonyLib.Harmony _harmony;
        static bool _patched, _refused, _sessionArmed, _everOnline, _netLogged;
        static int _orderHooks;                                         // order hooks installed (game run); EnemyAi waits for all OrderHookCount
        static bool _notified;                                          // one on-screen notice per game run when EnemyAi must pause for lack of hooks
        static volatile bool _armed;
        static volatile int _mainThread, _own;
        static long _hookCalls, _hookOffMain, _hookErrors, _hookDropped;
        static int _qCount;
        static readonly ConcurrentQueue<Cmd> _q = new();
        static volatile MoveComplexSys _moveSys;

        // registry (main thread)
        static readonly Dictionary<int, UnitInfo> _units = new();
        static readonly Dictionary<int, int> _byEntity = new();
        static readonly List<UnitInfo> _aiUnits = new();
        static readonly Dictionary<int, float> _cmdUid = new(), _spawnUid = new(), _wayEnd = new(), _memberCmdAt = new();
        static readonly Dictionary<int, int> _uidRadius = new();
        static readonly HashSet<int> _wayUid = new(), _memberCited = new();
        static readonly Dictionary<string, float> _groupCmd = new(StringComparer.Ordinal);
        static readonly Dictionary<string, int> _groupRadius = new(StringComparer.Ordinal);
        static readonly HashSet<string> _groupDirty = new(StringComparer.Ordinal), _groupsCited = new(StringComparer.Ordinal);
        static readonly Dictionary<int, Dictionary<string, bool>> _memb = new();
        static readonly HashSet<int> _tagsCited = new();
        static readonly List<(V3 pos, int order)> _buildingDest = new();
        static readonly Dictionary<IntPtr, WayTrack> _ways = new();
        static readonly Dictionary<int, (int status, string name, int type)> _tasks = new();
        static readonly HashSet<string> _once = new();
        static readonly int[] _cmdByKind = new int[7];
        internal static volatile Areas Protection;                  // null = not built yet in this battle
        static LuaMap _map;
        static bool _groupBroken, _relaunchBroken, _tasksLogged, _areasLogged;
        static bool _scriptParsed, _groupsRead, _stageJudged;          // readiness for EnemyAi (battle); convoy relaunch stage judged at this battle's end
        static int _groupErrors, _relaunchErrors, _cmdSeen, _cmdNoTarget, _cmdLogs, _spawnNotes, _relaunches, _stuckNow, _waysNow, _wayLogs, _moneyResets, _scriptMoneySets, _scriptDeckChanges;
        static int _econLogs, _assignLogs, _citedAdded, _stageBattle = -1, _wouldCount, _selfResumed, _provenStalls, _relaunchEffects, _relaunchMoved;
        static int _recheckCursor = -1;                                 // cited-group re-read pass: next index in _aiUnits, -1 = no pass running
        static float _nextCitedRecheck;
        static int _localUid = -1, _playerSide = -1;
        static float _next, _nextScan, _nextWays, _nextAreas, _nextPoll, _nextReport, _start = -1f, _lastCmdT = -1f, _lastSpawnT = -1f, _lastTaskT = -1f, _lastSetMoneyT = -999f, _lastDeckT = -999f;
        static float _lastMoney = float.NaN;
        static string _lastRelaunch = "aucune";
        static bool _unitsRead;
        // frozen simulation detection for the stall timers (Ways pass): Time.time keeps running while the game is paused or frozen
        static double _waySig; static int _wayCount = -1; static float _wayLastT = -1f, _wayUncounted;

        // script dump (incremental, main thread): a step stops after 1 ms, and a step runs in every frame until the script is read
        const double DumpBudgetMs = 1.0;
        const int DumpNodeCap = 300, DumpLineCap = 3000;
        static readonly System.Diagnostics.Stopwatch _dumpSw = new();
        static readonly HashSet<string> _dumpedThisRun = new(StringComparer.OrdinalIgnoreCase);
        static int _dumpStage, _dumpIndex, _dumpNodes, _dumpLines;
        static ulong _dumpHash;
        static StringBuilder _dumpSb;
        static Il2CppBrokenArrow.ScriptEngine.Data.GraphSaveData _dumpRaw;
        static string _dumpSource, _dumpPath;
        static Task _dumpTask;
        static volatile string _dumpWriteError;
        static bool _dumpTruncated;
        static bool _graphRead;                                         // a whole node list was read (RawLoadData to its end, or a nodes JSON walked without error)

        static void Log(string s) => Mod.Log.Msg("[MISSION] " + s);
        static void LogOnce(string key, string s) { if (_once.Count < 400 && _once.Add(key)) Log(s); }
        /// Script way (convoy) lines: capped per battle, the relevé keeps counting what is not written.
        static void WayLog(string s)
        {
            if (_wayLogs++ < WayLogMax) Log(s);
            else if (_wayLogs == WayLogMax + 1) Log($"plus de {WayLogMax} lignes sur les trajets du script : la suite est seulement comptée dans le relevé");
        }
        static void NotifyPause(TxtKey why)
        {
            if (_notified) return;
            _notified = true;
            try { Mod.Notify(new TxtMsg(TxtKey.N_MISSIONS_PAUSE, why)); } catch { }
        }

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Missions");
            _enabled = c.CreateEntry("MissionsSures", true, description: Build.Desc("Toujours actif : protège les unités et les bâtiments du script de mission, relance les convois du script bloqués, écrit le script de chaque mission"), is_hidden: true);
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _relanceEtape = c.CreateEntry("RelanceConvoisEtape", 0, description: Build.Desc("Automatique, ne pas modifier"), is_hidden: true);
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; _relanceEtape.Value = 0; }   // a new version measures again
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            ClearBattle();
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        /// Optional: called by the integrator when the battle end screen shows (the next ResetSession clears the state anyway).
        internal static void OnBattleEnd() => EndBattle("fin de bataille");

        static void EndBattle(string why)
        {
            _armed = false;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            try { if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); } } catch { }
            try { Report(UnityEngine.Time.time, true); } catch { }
            try { JudgeRelaunch(); } catch (Exception e) { Log("relance des convois : bilan impossible : " + e.Message); }
            Log($"fin de bataille ({why})");
        }

        /// Once per battle, at its end: switches the convoy relaunch stage from logged proof only (hidden pref, saved at once).
        static void JudgeRelaunch()
        {
            if (_stageJudged || _stageBattle < 0 || _relanceEtape == null) return;
            _stageJudged = true;
            if (_stageBattle == 0)
            {
                if (_provenStalls >= 1 && _selfResumed == 0 && _relaunchErrors == 0)
                {
                    _relanceEtape.Value = 1; MelonPreferences.Save();
                    Log($"relance des convois bloqués activée pour les prochaines batailles (preuve : {_provenStalls} trajet(s) vraiment bloqué(s), 0 reprise seule, {_wouldCount} relance(s) possible(s))");
                }
                else Log($"relance des convois : reste en mesure (relances possibles {_wouldCount}, bloqués prouvés {_provenStalls}, reprises seules {_selfResumed})");
                return;
            }
            string back = _relaunchErrors > 0 ? $"{_relaunchErrors} échec(s) de l'appel au jeu"
                : _relaunchEffects > 0 && _relaunchMoved == 0 ? $"aucun des {_relaunchEffects} convoi(s) relancé(s) n'a bougé de {StuckDist:0} m en {EffectWindow:0} s de jeu" : null;
            if (back != null)
            {
                _relanceEtape.Value = 0; MelonPreferences.Save();
                Log($"relance des convois repassée en mesure pour les prochaines batailles ({back})");
            }
            else Log($"relance des convois : reste active (relances {_relaunches}, convois repartis {_relaunchMoved}/{_relaunchEffects} mesurés)");
        }

        static void ClearBattle()
        {
            while (_q.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _qCount, 0);
            _moveSys = null; Protection = null; _map = null;
            _units.Clear(); _byEntity.Clear(); _aiUnits.Clear(); _cmdUid.Clear(); _spawnUid.Clear(); _wayEnd.Clear(); _memberCmdAt.Clear();
            _uidRadius.Clear(); _wayUid.Clear(); _memberCited.Clear(); _groupCmd.Clear(); _groupRadius.Clear(); _groupDirty.Clear(); _groupsCited.Clear();
            _memb.Clear(); _tagsCited.Clear(); _buildingDest.Clear(); _ways.Clear(); _tasks.Clear(); _once.Clear();
            Array.Clear(_cmdByKind, 0, _cmdByKind.Length);
            _groupBroken = _relaunchBroken = _tasksLogged = _areasLogged = _unitsRead = false;
            _scriptParsed = _groupsRead = _stageJudged = false;
            _groupErrors = _relaunchErrors = _cmdSeen = _cmdNoTarget = _cmdLogs = _spawnNotes = _relaunches = _stuckNow = _waysNow = _wayLogs = _moneyResets = _scriptMoneySets = _scriptDeckChanges = 0;
            _econLogs = _assignLogs = _citedAdded = _wouldCount = _selfResumed = _provenStalls = _relaunchEffects = _relaunchMoved = 0;
            _stageBattle = -1; _recheckCursor = -1; _nextCitedRecheck = 0f;
            _localUid = -1; _playerSide = -1;
            _next = _nextScan = _nextWays = _nextAreas = _nextPoll = _nextReport = 0f;
            _start = -1f; _lastCmdT = _lastSpawnT = _lastTaskT = -1f; _lastSetMoneyT = _lastDeckT = -999f; _lastMoney = float.NaN;
            _lastRelaunch = "aucune";
            _waySig = 0; _wayCount = -1; _wayLastT = -1f; _wayUncounted = 0f;
            Interlocked.Exchange(ref _hookCalls, 0); Interlocked.Exchange(ref _hookOffMain, 0); Interlocked.Exchange(ref _hookErrors, 0); Interlocked.Exchange(ref _hookDropped, 0);
            _dumpStage = 0; _dumpIndex = 0; _dumpNodes = _dumpLines = 0; _dumpHash = 0; _dumpSb = null; _dumpRaw = null; _dumpSource = null; _dumpPath = null; _dumpTruncated = false; _graphRead = false;
        }

        // ---------------------------------------------------------------- public API (main thread)

        /// EnemyAi wraps its own orders with these, so the command hooks never mistake them for script orders.
        internal static void OwnOrderBegin() => _own++;
        internal static void OwnOrderEnd() { if (_own > 0) _own--; }

        /// Why the mission script controls this unit, or null. Main thread; now = UnityEngine.Time.time.
        internal static string ScriptReason(int uid, float now)
        {
            if (_wayUid.Contains(uid)) return "trajet du script en cours";
            if (_wayEnd.TryGetValue(uid, out float t) && now - t < WayAfter) return "trajet du script récent";
            if (_cmdUid.TryGetValue(uid, out t) && now - t < CmdProtect) return "ordre du script";
            if (_spawnUid.TryGetValue(uid, out t) && now - t < SpawnProtect) return "apparue par le script";
            if (_memberCmdAt.TryGetValue(uid, out t) && now - t < CmdProtect) return "groupe commandé par le script";
            if (_memberCited.Contains(uid)) return "groupe cité par un ordre du script";
            return null;
        }

        /// Null when ScriptReason can be trusted for EnemyAi orders; otherwise why not (EnemyAi then gives no order: fail closed). Main thread.
        internal static string ProtectionNotReady()
        {
            if (!_sessionArmed) return "suivi de la mission pas démarré";
            if (_refused) return "crochets des ordres du script refusés";
            if (!_armed || _orderHooks < OrderHookCount) return "crochets des ordres du script désarmés ou incomplets";
            if (_cmdByKind[KComplex] > 0 && _moveSys == null) return "système des trajets du script inconnu";
            if (!_scriptParsed) return _dumpStage >= 30 ? "script de la mission illisible" : "script de la mission pas encore lu";   // stage 30+ (or 99 after an error): reading is over
            if (_groupBroken) return "appartenance aux groupes illisible";
            if (!_groupsRead) return "groupes du script pas encore lus";
            return null;
        }

        /// Aggressive radius carried by the last script order seen for this unit: 1 = yes, 0 = no, -1 = unknown.
        internal static int OrderRadius(int uid)
        {
            if (_uidRadius.TryGetValue(uid, out int r)) return r;
            if (_memb.TryGetValue(uid, out var m))
                foreach (var kv in m) if (kv.Value && _groupRadius.TryGetValue(kv.Key, out int g) && g >= 0) return g;
            return -1;
        }

        /// Called by Spawns (main thread) when a script spawn node landed a unit.
        internal static void NoteSpawn(int uid, string group)
        {
            if (uid <= 0) return;
            float now = UnityEngine.Time.time;
            if (_spawnUid.Count < 20000) _spawnUid[uid] = now;
            _spawnNotes++; _lastSpawnT = now;
        }

        /// 1 = protected (why says which place), 0 = free, -1 = protection not known yet (callers must not destroy). Main thread.
        internal static int ProtectedAt(float x, float z, out string why)
        {
            why = null;
            var a = Protection;
            if (a == null) { why = "protection des missions pas encore prête"; return -1; }
            for (int i = 0; i < a.X.Length; i++)
            {
                float dx = a.X[i] - x, dz = a.Z[i] - z;
                if (dx * dx + dz * dz <= a.R[i] * a.R[i]) { why = a.Why[i]; return 1; }
            }
            return 0;
        }

        // ---------------------------------------------------------------- frame

        static int _wait;
        static GameController _dumpGc;
        static readonly Action _aDump = () => DumpStep(_dumpGc, UnityEngine.Time.time);

        /// Allocation-free gate: the closures of a tick live in Tick. Between two ticks the mission script is read a little in every
        /// frame (1 ms at most), instead of one long step every 0.5 s.
        internal static void Frame()
        {
            if (_enabled == null) return;
            float real = UnityEngine.Time.realtimeSinceStartup;
            if (real < _next) { DumpFrame(); return; }
            // the tick waits for a frame with no other heavy job (Planif.cs); the first arming of a battle may wait longer
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = real + Step;
            Tick();
        }

        /// Mission script reading, on the frames without a tick (main thread, same conditions as the tick).
        static void DumpFrame()
        {
            if (_dumpStage >= 99 || !_sessionArmed) return;
            var gc = GameController._instance;
            if (gc?._GameSession_k__BackingField?.CurrentPlayer == null) return;
            _dumpGc = gc;
            Guard.Run("Missions.Script", _aDump);
        }

        static void Tick()
        {
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_sessionArmed) EndBattle("plus de partie"); _armed = false; return; }
            if (!_enabled.Value) { _armed = false; return; }
            if (!Solo()) { if (_armed) Log("partie en ligne : missions non suivies"); _armed = false; return; }
            _mainThread = Environment.CurrentManagedThreadId;
            Campaign.NoteSession();
            float now = UnityEngine.Time.time;
            if (_start < 0) { _start = now; _nextReport = now + ReportEvery; }
            try { _localUid = cp.UID; _playerSide = (int)cp.TeamSide; } catch { }

            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2 && !_patched)
                {
                    _refused = true;
                    Mod.Log.Warning("[MISSION] crochets d'observation des ordres du script coupés par sécurité (les deux dernières parties ne se sont pas terminées normalement) : l'IA ennemie du mod reste en pause pendant cette session de jeu");
                    NotifyPause(TxtKey.MS_WHY_INTERRUPTED);
                }
                _sessionArmed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();
                if (_stageBattle < 0) _stageBattle = _relanceEtape.Value >= 1 ? 1 : 0;           // read once per battle: a switch at battle end applies to the next battle
                Log($"suivi de la mission {Campaign.MissionUid} : unités et bâtiments du script protégés, convois du script bloqués {(_stageBattle == 1 ? "relancés" : "mesurés (relance pas encore prouvée)")}, script écrit dans UserData\\RealismOverhaul_missions");
            }
            if (!_refused && !_patched) TryPatch();
            _armed = _patched && Interlocked.Read(ref _hookErrors) <= MaxHookErrors;
            if (_patched && !_armed && Interlocked.Read(ref _hookErrors) > MaxHookErrors) NotifyPause(TxtKey.MS_WHY_HOOK_ERRORS);

            Guard.Run("Missions.Ordres", () => Drain(now));
            if (now >= _nextScan) { _nextScan = now + ScanEvery; Guard.Run("Missions.Unites", () => Scan(gc, now)); }
            if (now >= _nextWays) { _nextWays = now + WayEvery; Guard.Run("Missions.Trajets", () => Ways(gc, now)); }
            if (now >= _nextAreas && _unitsRead) { _nextAreas = now + AreaEvery; Guard.Run("Missions.Zones", () => BuildAreas(gc, now)); }
            if (now >= _nextPoll) { _nextPoll = now + PollEvery; Guard.Run("Missions.Taches", () => PollTasks(gc, now)); Guard.Run("Missions.Argent", () => PollMoney(gc, now)); }
            Guard.Run("Missions.Script", () => DumpStep(gc, now));
            if (now >= _nextReport) { _nextReport = now + ReportEvery; Guard.Run("Missions.Releve", () => Report(now, false)); }
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

        // ---------------------------------------------------------------- hooks (log-only)

        static bool TryPatch()
        {
            try
            {
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Missions");
                int want = 0, n = 0, orders = 0;
                orders += PatchOne(typeof(MoveComplexSys), nameof(MoveComplexSys.OnMoveComplex), nameof(PreMoveComplex), nameof(PostMoveComplex), ref want);
                orders += PatchOne(typeof(GroupNameSys), nameof(GroupNameSys.RunMove), nameof(PreRunMove), null, ref want);
                orders += PatchOne(typeof(GroupNameSys), nameof(GroupNameSys.UnloadCommand), nameof(PreUnload), null, ref want);
                orders += PatchOne(typeof(GroupNameSys), nameof(GroupNameSys.RunAttackEnemy), nameof(PreAttack), null, ref want);
                orders += PatchOne(typeof(GroupNameSys), nameof(GroupNameSys.RunCapture), nameof(PreCapture), null, ref want);
                _orderHooks = orders; n += orders;
                n += PatchOne(typeof(NodeSetMoney), nameof(NodeSetMoney.OnActivated), null, nameof(PostSetMoney), ref want);
                n += PatchOne(typeof(NodeChangeDeck), nameof(NodeChangeDeck.OnActivated), null, nameof(PostDeck), ref want);
                _patched = true;
                Log($"crochets d'observation installés ({n}/{want}) : ordres du script, argent et deck fixés par le script ; rien n'est modifié");
                if (orders < OrderHookCount)
                {
                    Mod.Log.Warning($"[MISSION] crochets des ordres du script incomplets ({orders}/{OrderHookCount}) : l'IA ennemie du mod reste en pause pendant cette session de jeu");
                    NotifyPause(TxtKey.MS_WHY_HOOKS_INCOMPLETE);
                }
                return true;
            }
            catch (Exception e)
            {
                _refused = true; _armed = false;
                Mod.Log.Warning("[MISSION] installation des crochets impossible : " + e.GetBaseException().Message + " : l'IA ennemie du mod reste en pause pendant cette session de jeu");
                NotifyPause(TxtKey.MS_WHY_HOOKS_FAILED);
                return false;
            }
        }

        static int PatchOne(Type type, string method, string pre, string post, ref int want)
        {
            want++;
            try
            {
                var m = AccessTools.DeclaredMethod(type, method);                 // declared only: never a base NodeLogic method shared by every node
                if (m == null) { Log($"crochet {type.Name}.{method} introuvable dans cette version du jeu"); return 0; }
                var hpre = pre == null ? null : new HarmonyMethod(typeof(Missions).GetMethod(pre, BindingFlags.NonPublic | BindingFlags.Static));
                var hpost = post == null ? null : new HarmonyMethod(typeof(Missions).GetMethod(post, BindingFlags.NonPublic | BindingFlags.Static));
                _harmony.Patch(m, prefix: hpre, postfix: hpost);
                return 1;
            }
            catch (Exception e) { Log($"crochet {type.Name}.{method} impossible : {e.GetBaseException().Message}"); return 0; }
        }

        static void HookFail() { if (Interlocked.Increment(ref _hookErrors) > MaxHookErrors) _armed = false; }

        static bool HookEnter()
        {
            if (Campaign.MissionInerte || !_armed || _own > 0) return false;           // mission without the mod: nothing recorded
            Interlocked.Increment(ref _hookCalls);
            if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _hookOffMain);
            return true;
        }

        static void Push(Cmd c)
        {
            if (Interlocked.Increment(ref _qCount) > QueueMax) { Interlocked.Decrement(ref _qCount); Interlocked.Increment(ref _hookDropped); return; }
            _q.Enqueue(c);
        }

        static int RadiusFlag(RadiusData r) => r.Grounds > 0 || r.Helicopters > 0 || r.Planes > 0 ? 1 : 0;

        /// Copies at most MaxIds uids of an engine IReadOnlyList<int> (List<int>, int[] or any IReadOnlyCollection).
        static int[] ReadIds(IlIntList ro)
        {
            if (ro == null) return null;
            try
            {
                var l = ro.TryCast<Il2CppSystem.Collections.Generic.List<int>>();
                if (l != null)
                {
                    int n = Math.Min(l.Count, MaxIds); if (n <= 0) return null;
                    var r = new int[n]; for (int i = 0; i < n; i++) r[i] = l[i]; return r;
                }
            }
            catch { }
            try
            {
                var a = ro.TryCast<Il2CppStructArray<int>>();
                if (a != null)
                {
                    int n = Math.Min(a.Length, MaxIds); if (n <= 0) return null;
                    var r = new int[n]; for (int i = 0; i < n; i++) r[i] = a[i]; return r;
                }
            }
            catch { }
            int count = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<int>(ro.Pointer).Count;
            int m = Math.Min(count, MaxIds); if (m <= 0) return null;
            var res = new int[m]; for (int i = 0; i < m; i++) res[i] = ro[i];
            return res;
        }

        static void PreMoveComplex(MoveComplexCmd data)
        {
            if (data == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KComplex };
                try { c.Uids = ReadIds(data.UnitList); } catch { }
                try { c.Uid = data.UnitUID; c.Group = data.GroupName; } catch { }
                try { c.Radius = RadiusFlag(data.AggressiveRadius); } catch { }
                try { c.Tag = data.TargetTagUID; c.Vec = data.TargetVector; c.AtDest = data.AtDestination.ToString(); } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        /// Keeps the complex move system of this battle (its active ways drive the stuck convoy watchdog).
        static void PostMoveComplex(MoveComplexSys __instance)
        {
            if (Campaign.MissionInerte || !_armed || __instance == null) return;
            try { var cur = _moveSys; if (cur == null || cur.Pointer != __instance.Pointer) _moveSys = __instance; }
            catch { HookFail(); }
        }

        static void PreRunMove(MoveSimpleCmd data)
        {
            if (data == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KSimple };
                try { c.Uids = ReadIds(data.UnitList); } catch { }
                try { c.Uid = data.UnitUID; c.Group = data.GroupName; } catch { }
                try { c.Radius = RadiusFlag(data.AggressiveRadius); } catch { }
                try { c.Tag = data.TargetTagUID; c.Vec = data.TargetVector; c.AtDest = data.AtDestination.ToString(); } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        static void PreUnload(UnloadCmd data)
        {
            if (data == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KUnload };
                try { c.Uids = ReadIds(data.UnitsList); } catch { }
                try { c.Uid = data.UnitID; c.Group = data.Groups; } catch { }
                try { c.Radius = RadiusFlag(data.AggressiveRadius); } catch { }
                try { c.Tag = data.TargetPosTag; c.Vec = data.TargetPosition; } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        static void PreAttack(AttackCmd data)
        {
            if (data == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KAttack };
                try { c.Uids = ReadIds(data.UnitList); } catch { }
                try { c.Uid = data.UnitUID; c.Group = data.GroupName; } catch { }
                try { c.Radius = RadiusFlag(data.AggressiveRadius); } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        static void PreCapture(CaptureCmd data)
        {
            if (data == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KCapture };
                try { c.Uids = ReadIds(data.UnitList); } catch { }
                try { c.Uid = data.UnitUID; c.Group = data.GroupName; } catch { }
                try { c.Radius = RadiusFlag(data.AggressiveRadius); } catch { }
                try { c.Tag = data.ObjectiveZoneUID; } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        static void PostSetMoney(NodeSetMoney __instance)
        {
            if (__instance == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KSetMoney };
                try { c.Amount = __instance.Amount; } catch { }
                try { c.Player = __instance.TargetPlayer?.DataUID ?? -1; } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        static void PostDeck(NodeChangeDeck __instance)
        {
            if (__instance == null || !HookEnter()) return;
            try
            {
                var c = new Cmd { Kind = KDeck };
                try { c.Player = __instance.Player?.DataUID ?? -1; } catch { }
                Push(c);
            }
            catch { HookFail(); }
        }

        // ---------------------------------------------------------------- script orders (main thread)

        static void Drain(float now)
        {
            for (int n = 0; n < 500 && _q.TryDequeue(out var c); n++)
            {
                Interlocked.Decrement(ref _qCount);
                if (c.Kind == KSetMoney)
                {
                    _scriptMoneySets++; _lastSetMoneyT = now;
                    if (_econLogs++ < EconLogMax) Log($"le script fixe l'argent d'un joueur (lien joueur {c.Player}) à {c.Amount} à t={now - _start:0}s (aucun changement par le mod)");
                    else if (_econLogs == EconLogMax + 1) Log($"plus de {EconLogMax} lignes argent/deck du script : la suite est seulement comptée dans le relevé");
                    continue;
                }
                if (c.Kind == KDeck)
                {
                    _scriptDeckChanges++; _lastDeckT = now;
                    if (_econLogs++ < EconLogMax) Log($"le script change le deck d'un joueur (lien joueur {c.Player}) à t={now - _start:0}s");
                    else if (_econLogs == EconLogMax + 1) Log($"plus de {EconLogMax} lignes argent/deck du script : la suite est seulement comptée dans le relevé");
                    continue;
                }
                NoteCommand(c, now);
            }
        }

        static IEnumerable<string> SplitGroups(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (var part in s.Split(',', ';', '|'))
            {
                var g = part.Trim();
                if (g.Length > 0 && g.Length <= 64) yield return g;
            }
        }

        static void NoteCommand(Cmd c, float now)
        {
            _cmdSeen++; _lastCmdT = now;
            if (c.Kind >= 0 && c.Kind < _cmdByKind.Length) _cmdByKind[c.Kind]++;
            int targets = 0;
            if (c.Uids != null)
                foreach (int uid in c.Uids) { if (uid <= 0) continue; Mark(uid, c.Radius, now); targets++; }
            if (c.Uid > 0) { Mark(c.Uid, c.Radius, now); targets++; }
            string groups = null;
            foreach (var g in SplitGroups(c.Group))
            {
                if (_groupCmd.Count >= 500 && !_groupCmd.ContainsKey(g)) break;
                _groupCmd[g] = now; _groupRadius[g] = c.Radius; targets++;
                groups = groups == null ? g : groups + "," + g;
                GroupNow(g, now);
            }
            if (targets == 0) _cmdNoTarget++;
            V3 dest = c.Vec;
            string where = "";
            if (c.Kind != KAttack && c.Kind != KCapture)
            {
                if (c.Tag > 0 && TagPos(c.Tag, out var tp)) { dest = tp; where = $" dest=({tp.x:0},{tp.z:0}) tag {c.Tag}"; }
                else if (dest != V3.zero) where = $" dest=({dest.x:0},{dest.z:0})";
            }
            else if (c.Kind == KCapture) where = $" zone {c.Tag}";
            bool entry = c.AtDest != null && c.AtDest.IndexOf("Building", StringComparison.OrdinalIgnoreCase) >= 0;
            if (entry && dest != V3.zero && _buildingDest.Count < 400)
            {
                bool dup = false;
                foreach (var b in _buildingDest) if (EnemyAi.D2(b.pos, dest) < 20f) { dup = true; break; }
                if (!dup) _buildingDest.Add((dest, c.Tag));
            }
            if (_cmdLogs++ < 150)
                Log($"ordre du script ({KindNames[c.Kind]}) t={now - _start:0}s : unités [{Ids(c.Uids, c.Uid)}]{(groups != null ? $" groupe '{groups}'" : "")}{where}{(c.AtDest != null ? " arrivée=" + c.AtDest : "")} rayon={(c.Radius < 0 ? "?" : c.Radius == 1 ? "oui" : "non")}{(targets == 0 ? " (cible illisible)" : "")}");
        }

        static void Mark(int uid, int radius, float now)
        {
            if (_cmdUid.Count >= 20000 && !_cmdUid.ContainsKey(uid)) return;
            _cmdUid[uid] = now;
            if (radius >= 0) _uidRadius[uid] = radius;
        }

        static string Ids(int[] ids, int single)
        {
            var sb = new StringBuilder();
            int n = 0;
            if (ids != null) foreach (int u in ids) { if (n++ >= 10) { sb.Append(" ..."); break; } if (sb.Length > 0) sb.Append(' '); sb.Append(u); }
            if (single > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(single); }
            return sb.ToString();
        }

        static bool TagPos(int tag, out V3 pos)
        {
            pos = V3.zero;
            if (tag <= 0) return false;
            try { var scen = GameController._instance?.GetScenarioData; return scen != null && ScenarioDataHandlerExt.TryGetTagPos(scen, tag, out pos); }
            catch { return false; }
        }

        // ---------------------------------------------------------------- units and group membership

        static void Scan(GameController gc, float now)
        {
            _map ??= new LuaMap();
            _groupsRead = false;                                        // set again by Groups at the end of a successful scan (a failed scan leaves EnemyAi waiting)
            _units.Clear(); _byEntity.Clear(); _aiUnits.Clear();
            for (int side = 0; side < 2; side++)
            {
                var arr = _map.GetUnits(V3.zero, 1_000_000f, side, -1);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = arr[i];
                        if (u == null || !u.IsAlive()) continue;
                        var ui = new UnitInfo { U = u, Uid = u.UID, Side = side };
                        try { ui.Eid = u.Entity.EntityId; _byEntity[ui.Eid] = ui.Uid; } catch { ui.Eid = -1; }
                        try { ui.Owner = u.GetOwnerPlayerUID(); } catch { ui.Owner = -1; }
                        try { ui.Role = u.UnitRole; } catch { }
                        try { ui.Pos = u.GetPosition(); } catch { }
                        try { ui.Heal = u.GetHealPercentage(); } catch { ui.Heal = -1; }
                        ui.Local = ui.Owner == _localUid;
                        _units[ui.Uid] = ui;
                        if (!ui.Local) _aiUnits.Add(ui);
                    }
                    catch { }
                }
            }
            _unitsRead = true;
            // forget dead units
            if (_memb.Count > 0) { var dead = new List<int>(); foreach (var k in _memb.Keys) if (!_units.ContainsKey(k)) dead.Add(k); foreach (var k in dead) _memb.Remove(k); }
            Groups(gc, now);
        }

        /// A group order just arrived: its members (last unit scan) are protected at once, without waiting for the next scan.
        static void GroupNow(string g, float now)
        {
            // a group left dirty is re-read by the next scan; until then EnemyAi waits (its members may not all be known)
            if (_groupBroken || _aiUnits.Count == 0) { _groupDirty.Add(g); _groupsRead = false; return; }
            Il2CppBrokenArrow.Client.Ecs.Utils.EntitiesHelper helper = null;
            try { helper = GameController._instance?.GetEntitiesHelper; } catch { }
            if (helper == null) { _groupDirty.Add(g); _groupsRead = false; return; }
            int budget = 600;
            foreach (var ui in _aiUnits)
            {
                if (budget-- <= 0) { _groupDirty.Add(g); _groupsRead = false; break; }
                bool has;
                try { var e = ui.U.Entity; has = helper.UnitHasGroup(ref e, g); }
                catch (Exception ex)
                {
                    if (++_groupErrors >= 20) { _groupBroken = true; Log("appartenance aux groupes illisible (20 erreurs, dernière : " + ex.Message + ") : protection par ordres, trajets et apparitions seulement"); return; }
                    continue;
                }
                if (!_memb.TryGetValue(ui.Uid, out var m)) _memb[ui.Uid] = m = new Dictionary<string, bool>(StringComparer.Ordinal);
                m[g] = has;
                if (has && (!_memberCmdAt.TryGetValue(ui.Uid, out float old) || now > old)) _memberCmdAt[ui.Uid] = now;
            }
        }

        /// Group membership through the engine's own EntitiesHelper.UnitHasGroup, cached per unit; groups commanded again are re-read.
        /// A cached "member" answer is never dropped here (fail safe); cached "not a member" answers of cited groups are re-read every
        /// 60 s (the script can assign units to a group during the battle). _groupsRead is true only after a full pass over every pair.
        static void Groups(GameController gc, float now)
        {
            if (_groupBroken) { _groupsRead = false; return; }
            var active = new List<string>();
            foreach (var g in _groupsCited) active.Add(g);
            foreach (var kv in _groupCmd) if (now - kv.Value < CmdProtect && !_groupsCited.Contains(kv.Key)) active.Add(kv.Key);
            if (active.Count == 0 && _memberCmdAt.Count == 0 && _memberCited.Count == 0) { _groupsRead = _scriptParsed; return; }
            if (_groupDirty.Count > 0)
            {
                // only "not a member" answers are forgotten: a unit cached as member stays protected while it is re-read
                foreach (var m in _memb.Values) foreach (var g in _groupDirty) if (m.TryGetValue(g, out bool had) && !had) m.Remove(g);
                _groupDirty.Clear();
            }
            Il2CppBrokenArrow.Client.Ecs.Utils.EntitiesHelper helper = null;
            try { helper = gc.GetEntitiesHelper; } catch { }
            if (helper == null) { _groupsRead = false; LogOnce("helper", "appartenance aux groupes illisible (aide d'entités absente) : l'IA ennemie du mod attend"); return; }
            int budget = 3000;
            bool complete = true;
            // pass 1: pairs never read (new units, groups newly active or dirty)
            foreach (var ui in _aiUnits)
            {
                if (!_memb.TryGetValue(ui.Uid, out var m)) _memb[ui.Uid] = m = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var g in active)
                {
                    if (m.ContainsKey(g)) continue;
                    if (budget <= 0) { complete = false; break; }
                    budget--;
                    try { var e = ui.U.Entity; m[g] = helper.UnitHasGroup(ref e, g); }
                    catch (Exception ex)
                    {
                        m[g] = false;
                        if (++_groupErrors >= 20) { _groupBroken = true; _groupsRead = false; Log("appartenance aux groupes illisible (20 erreurs, dernière : " + ex.Message + ") : l'IA ennemie du mod reste en pause pour cette bataille"); return; }
                    }
                }
                if (!complete) break;
            }
            // pass 2: cited groups, cached "not a member" answers re-read every 60 s, spread over several scans with a cursor
            if (_recheckCursor < 0 && _groupsCited.Count > 0 && now >= _nextCitedRecheck) { _recheckCursor = 0; _nextCitedRecheck = now + CitedRecheckEvery; }
            if (_recheckCursor >= 0 && complete)
            {
                int idx = 0, resumeAt = -1;
                foreach (var ui in _aiUnits)
                {
                    int i = idx++;
                    if (i < _recheckCursor) continue;
                    if (budget <= 0) { resumeAt = i; break; }
                    if (!_memb.TryGetValue(ui.Uid, out var m)) continue;
                    foreach (var g in _groupsCited)
                    {
                        if (!m.TryGetValue(g, out bool had) || had) continue;
                        if (budget <= 0) { resumeAt = i; break; }
                        budget--;
                        bool has;
                        try { var e = ui.U.Entity; has = helper.UnitHasGroup(ref e, g); }
                        catch (Exception ex)
                        {
                            if (++_groupErrors >= 20) { _groupBroken = true; _groupsRead = false; Log("appartenance aux groupes illisible (20 erreurs, dernière : " + ex.Message + ") : l'IA ennemie du mod reste en pause pour cette bataille"); return; }
                            continue;
                        }
                        if (!has) continue;
                        m[g] = true; _citedAdded++;
                        if (_assignLogs++ < AssignLogMax) Log($"unité uid {ui.Uid} ajoutée par le script au groupe cité '{g}' : protégée de l'IA ennemie du mod");
                    }
                    if (resumeAt >= 0) break;
                }
                _recheckCursor = resumeAt;                                                   // -1 = full re-read pass finished
            }
            _groupsRead = _scriptParsed && complete;
            _memberCmdAt.Clear(); _memberCited.Clear();
            foreach (var kv in _memb)
                foreach (var gk in kv.Value)
                {
                    if (!gk.Value) continue;
                    if (_groupCmd.TryGetValue(gk.Key, out float t) && (!_memberCmdAt.TryGetValue(kv.Key, out float old) || t > old)) _memberCmdAt[kv.Key] = t;
                    if (_groupsCited.Contains(gk.Key)) _memberCited.Add(kv.Key);
                }
        }

        // ---------------------------------------------------------------- script complex moves: protection and stuck convoy relaunch

        static void Ways(GameController gc, float now)
        {
            var sys = _moveSys;
            _waysNow = 0; _stuckNow = 0;
            if (sys == null) return;
            try
            {
                var g = MoveComplexSys._game;
                if (g == null || g.Pointer != gc.Pointer) { _moveSys = null; LogOnce("way-stale", "système des trajets du script d'une autre partie : oublié"); return; }
            }
            catch { }
            Il2CppSystem.Collections.Generic.List<WayMoveData> list;
            try { list = sys._activeWays; }
            catch (Exception e) { _moveSys = null; LogOnce("way-list", "liste des trajets du script illisible : " + e.Message); return; }
            if (list == null) return;
            float frozenDt = UncountedSinceLastWays(now);
            var seen = new HashSet<IntPtr>();
            _wayUid.Clear();
            int count = Math.Min(list.Count, 400);
            for (int i = 0; i < count; i++)
            {
                WayMoveData w;
                try { w = list[i]; } catch { continue; }
                if (w == null) continue;
                IntPtr p = w.Pointer;
                seen.Add(p);
                if (!_ways.TryGetValue(p, out var wt))
                {
                    wt = new WayTrack { W = w, FirstSeen = now };
                    try { var bd = w.BaseData; if (bd != null) { wt.Id = bd.ID; wt.Tag = bd.TargetTagUID; wt.AtDest = bd.AtDestination.ToString(); wt.OwnRadius = RadiusFlag(bd.AggressiveRadius) == 1; } } catch { }
                    _ways[p] = wt;
                }
                else if (frozenDt > 0f) ShiftClock(wt, frozenDt);                                // only ways that existed before this pass; Track still runs (AddEntities refills _wayUid protection)
                _waysNow++;
                try { Track(sys, wt, now); } catch (Exception e) { LogOnce("way" + wt.Id, $"trajet du script {wt.Id} illisible : {e.Message}"); }
            }
            var ended = new List<IntPtr>();
            foreach (var kv in _ways) if (!seen.Contains(kv.Key)) ended.Add(kv.Key);
            foreach (var p in ended)
            {
                var wt = _ways[p];
                foreach (int uid in wt.Uids) if (_wayEnd.Count < 20000 || _wayEnd.ContainsKey(uid)) _wayEnd[uid] = now;
                if (wt.Relaunches > 0) WayLog($"trajet du script {wt.Id} terminé après {wt.Relaunches} relance(s) (unités {UidSample(wt)})");
                if (wt.WouldAt >= 0 && !wt.WouldJudged)
                {
                    // ended by the game or the script while still measured: the stall resolved without us, so it is no proof (fail closed)
                    _selfResumed++;
                    WayLog($"trajet du script {wt.Id} (camp {wt.Side}) terminé par le jeu {now - wt.WouldAt:0} s après 'relance possible', sans relance : compté comme reprise seule");
                }
                _ways.Remove(p);
            }
        }

        /// Time since the last Ways pass that must not count as game time for the stall timers. Time.time keeps running while the game
        /// is paused or frozen (timeScale stays 1 in this game, same proof as DemiBlindage), so: an unchanged position signature of every
        /// scanned unit (both sides) or a zero timeScale -> the whole interval is frozen; a gap in which this pass did not run at all
        /// (battle end screen latch Campaign.BattleOver skips Missions.Frame, online check, move system unknown) -> all but one normal
        /// interval is unobserved. Fail closed: a map that really stands still for 10 s only delays a stall proof or a relaunch.
        /// Every 10 s, no allocation (struct enumerator over the dictionary values).
        static float UncountedSinceLastWays(float now)
        {
            double sig = 0;
            foreach (var ui in _units.Values) sig += ui.Pos.x + 3.0 * ui.Pos.z;
            bool paused;
            try { paused = UnityEngine.Time.timeScale <= 0f; } catch { paused = false; }
            float gap = _wayLastT >= 0f ? Math.Max(0f, now - _wayLastT) : 0f;
            bool frozen = paused || (_wayCount >= 0 && _units.Count == _wayCount && Math.Abs(sig - _waySig) < 0.5);
            _waySig = sig; _wayCount = _units.Count; _wayLastT = now;
            float dt = frozen ? gap : gap > 2f * WayEvery ? gap - WayEvery : 0f;
            if (dt > 0f) _wayUncounted += dt;
            return dt;
        }

        /// Pushes every stall timer of a way forward by frozen time, so a pause never counts as immobility or quiet time.
        /// EffectAt is not shifted: the relaunch effect check restarts its own window when uncounted time came in (UncAt, see Track).
        static void ShiftClock(WayTrack wt, float dt)
        {
            if (wt.AnchorT >= 0f) wt.AnchorT += dt;                                             // -1 = no anchor yet (sentinel kept)
            if (wt.WouldAt >= 0f) wt.WouldAt += dt;                                             // -1 = not flagged (sentinel kept)
            wt.LastHit += dt;                                                                   // keeps 'now - LastHit' unchanged (also for the -999 default)
            if (wt.Relaunches > 0) wt.LastRelaunch += dt;
        }

        /// Registers the way's units (protection); sums position and health of the list when stats is set. Any unit of the player's
        /// own, in any list, makes the whole way the player's (side -2: never relaunched); otherwise the first AI unit gives the side.
        static void AddEntities(Il2CppSystem.Collections.Generic.List<Il2CppDefaultEcs.Entity> l, WayTrack wt, bool stats, ref V3 sum, ref int alive, ref int heal, ref int side)
        {
            if (l == null) return;
            int n = Math.Min(l.Count, 200);
            for (int i = 0; i < n; i++)
            {
                int eid;
                try { eid = l[i].EntityId; } catch { continue; }
                if (!_byEntity.TryGetValue(eid, out int uid)) continue;
                if (wt.Uids.Count < 200) wt.Uids.Add(uid);
                _wayUid.Add(uid);
                if (!_units.TryGetValue(uid, out var ui)) continue;
                if (ui.Local) side = -2;
                else if (side == -1) side = ui.Side;
                if (!stats) continue;
                sum += ui.Pos; alive++; heal += Math.Max(0, ui.Heal);
            }
        }

        static bool StageIsMove(WayTrack wt) => wt.Stage != null && wt.Stage.IndexOf("TargetMove", StringComparison.OrdinalIgnoreCase) >= 0;

        static void Track(MoveComplexSys sys, WayTrack wt, float now)
        {
            var w = wt.W;
            V3 sum = V3.zero, offSum = V3.zero; int alive = 0, heal = 0, offAlive = 0, offHeal = 0, side = -1;
            AddEntities(w.Units, wt, true, ref sum, ref alive, ref heal, ref side);
            AddEntities(w.InitialUnits, wt, false, ref sum, ref alive, ref heal, ref side);
            AddEntities(w.UnloadedUnits, wt, true, ref offSum, ref offAlive, ref offHeal, ref side);   // unloaded units: hits and contacts only, never the convoy position
            foreach (int uid in wt.Uids) if (!_uidRadius.ContainsKey(uid) && _uidRadius.Count < 20000) _uidRadius[uid] = wt.OwnRadius ? 1 : 0;   // the way's own order radius
            if (side >= 0) wt.Side = side;
            bool interrupted = false;
            try { interrupted = w.Interrupted; } catch { }
            try { wt.Target = w.TargetPos; } catch { }
            try { wt.Engaged = w.GroupIsEngaged; } catch { }
            try { var act = w.UpdateAction; wt.Stage = act?.Method?.Name; } catch { wt.Stage = null; }
            if (alive == 0 || interrupted || side == -2 || wt.Target == V3.zero) { wt.Alive = alive; return; }   // nothing to move, interrupted, the player's own units, or no readable destination
            var c = sum / alive;
            wt.Center = c; wt.Alive = alive;
            if (wt.LastAlive >= 0 && (alive < wt.LastAlive || heal < wt.LastHeal)) wt.LastHit = now;
            if (wt.LastOffAlive >= 0 && (offAlive < wt.LastOffAlive || offHeal < wt.LastOffHeal)) wt.LastHit = now;   // unloaded units hit, lost or re-embarked: not quiet
            wt.LastAlive = alive; wt.LastHeal = heal; wt.LastOffAlive = offAlive; wt.LastOffHeal = offHeal;
            wt.Dist = EnemyAi.D2(c, wt.Target);
            float opp = float.MaxValue;
            V3 oc = offAlive > 0 ? offSum / offAlive : c;
            foreach (var ui in _units.Values)
                if (ui.Side != wt.Side)
                {
                    float d = EnemyAi.D2(ui.Pos, c);
                    if (offAlive > 0) d = Math.Min(d, EnemyAi.D2(ui.Pos, oc));
                    if (d < opp) opp = d;
                }
            wt.NearestOpp = opp;
            bool quiet = now - wt.LastHit >= QuietFor && opp > ContactDist;
            bool stageOk = StageIsMove(wt);
            // relaunch effect: a move of StuckDist counts at once; "moved nothing" counts only after EffectWindow seconds with no uncounted
            // time (pause, frozen map, gap) since the window started, otherwise the window restarts (RelaunchPos kept). Never measured = no count.
            if (wt.Relaunches > 0 && !wt.EffectLogged)
            {
                float moved = EnemyAi.D2(c, wt.RelaunchPos);
                if (moved >= StuckDist || (now >= wt.EffectAt && _wayUncounted == wt.UncAt))
                {
                    wt.EffectLogged = true;
                    _relaunchEffects++; if (moved >= StuckDist) _relaunchMoved++;
                    WayLog($"trajet du script {wt.Id} : {now - wt.LastRelaunch:0} s de jeu après la relance #{wt.Relaunches}, convoi déplacé de {moved:0} m (encore {wt.Dist:0} m jusqu'à la destination, engagé selon le jeu {(wt.Engaged ? "oui" : "non")})");
                }
                else if (now >= wt.EffectAt)
                {
                    wt.EffectAt = now + EffectWindow; wt.UncAt = _wayUncounted;
                    LogOnce("effet-refait" + wt.Id, $"trajet du script {wt.Id} : mesure de la relance refaite ({EffectWindow:0} s) : pause ou jeu figé pendant la mesure");
                }
            }
            // measurement stage: a way flagged "relance possible" is judged once, resumed on its own (false positive) or proven stuck
            if (wt.WouldAt >= 0 && !wt.WouldJudged)
            {
                float since = now - wt.WouldAt, movedSelf = EnemyAi.D2(c, wt.WouldPos);
                if (movedSelf >= StuckDist)
                {
                    wt.WouldJudged = true; _selfResumed++;
                    WayLog($"trajet du script {wt.Id} (camp {wt.Side}) : a repris seul {since:0} s après 'relance possible' ({movedSelf:0} m parcourus sans relance) : fausse alerte comptée");
                }
                else if (since >= StuckAgain && stageOk && !wt.Engaged && quiet && wt.Dist > NearDest)
                {
                    wt.WouldJudged = true; _provenStalls++;
                    WayLog($"trajet du script {wt.Id} (camp {wt.Side}) : vraiment bloqué, toujours immobile {since:0} s après 'relance possible' (étape {wt.Stage}, engagé non, encore {wt.Dist:0} m jusqu'à la destination) : preuve comptée");
                }
            }
            if (wt.AnchorT < 0 || EnemyAi.D2(c, wt.Anchor) >= StuckDist)
            {
                if (wt.Relaunches > 0 && wt.AnchorT >= 0 && wt.StuckLogged)
                    WayLog($"trajet du script {wt.Id} (camp {wt.Side}) : le convoi a repris après la relance #{wt.Relaunches} ({EnemyAi.D2(c, wt.RelaunchPos):0} m parcourus, encore {wt.Dist:0} m jusqu'à la destination)");
                wt.Anchor = c; wt.AnchorT = now; wt.StuckLogged = false; wt.WaitLogged = false;
                return;
            }
            float still = now - wt.AnchorT;
            if (wt.Dist <= NearDest || still < StuckFirst) return;
            float delay = 0f;
            try { delay = w.Delay; } catch { }
            if (delay > still) return;                                                          // engine-internal timer (MoveComplexData has no delay); guard only
            _stuckNow++;
            if (!wt.StuckLogged && !wt.GaveUpLogged)
            {
                wt.StuckLogged = true;
                WayLog($"trajet du script {wt.Id} (camp {wt.Side}) bloqué : unités {UidSample(wt)} ({alive} vivante(s)) à ({c.x:0},{c.z:0}), moins de {StuckDist:0} m en {still:0} s, destination ({wt.Target.x:0},{wt.Target.z:0}){(wt.Tag > 0 ? " tag " + wt.Tag : "")} à {wt.Dist:0} m, étape {wt.Stage ?? "illisible"}, engagé selon le jeu {(wt.Engaged ? "oui" : "non")}, touché il y a {Math.Min(9999f, now - wt.LastHit):0} s, ennemi le plus proche {(opp == float.MaxValue ? "aucun" : opp.ToString("0", CultureInfo.InvariantCulture) + " m")}");
            }
            if (wt.Relaunches >= MaxRelaunch)
            {
                if (!wt.GaveUpLogged && now - wt.LastRelaunch >= StuckAgain) { wt.GaveUpLogged = true; WayLog($"trajet du script {wt.Id} (camp {wt.Side}) toujours bloqué après {MaxRelaunch} relances : plus de relance (unités {UidSample(wt)} à ({c.x:0},{c.z:0}), destination à {wt.Dist:0} m)"); }
                return;
            }
            if (_relaunchBroken) return;
            if (wt.Relaunches > 0 && now - wt.LastRelaunch < StuckAgain) return;
            bool measure = _stageBattle != 1;                                                   // stage 0 (or unknown): measurement only, no engine call
            // fail closed: stage readable and TargetMove, not engaged according to the engine, not hit recently, no opposing unit near
            if (!stageOk || wt.Engaged || !quiet)
            {
                if (!wt.WaitLogged)
                {
                    wt.WaitLogged = true;
                    string why = wt.Stage == null ? "étape illisible" : !stageOk ? "étape " + wt.Stage : wt.Engaged ? "engagé selon le jeu"
                        : now - wt.LastHit < QuietFor ? "convoi touché récemment" : "ennemi à moins de " + ContactDist.ToString("0", CultureInfo.InvariantCulture) + " m";
                    WayLog($"trajet du script {wt.Id} : relance différée ({why}){(measure ? " (mesure seulement)" : "")}");
                }
                return;
            }
            if (measure)
            {
                if (wt.WouldAt < 0)
                {
                    wt.WouldAt = now; wt.WouldPos = c; _wouldCount++;
                    WayLog($"trajet du script {wt.Id} (camp {wt.Side}) : relance possible (mesure seulement, aucun ordre envoyé) : étape {wt.Stage}, engagé non, immobile {still:0} s, destination à {wt.Dist:0} m");
                }
                return;
            }
            Relaunch(sys, wt, now, still);
        }

        static void Relaunch(MoveComplexSys sys, WayTrack wt, float now, float still)
        {
            wt.Relaunches++; wt.LastRelaunch = now; wt.RelaunchPos = wt.Center; wt.WaitLogged = false; wt.EffectLogged = false; wt.EffectAt = now + EffectWindow; wt.UncAt = _wayUncounted;
            string result;
            OwnOrderBegin();
            try { sys.ForceMoveToTarget(wt.W); result = "ordre renvoyé (ForceMoveToTarget)"; _relaunches++; }
            catch (Exception e)
            {
                result = "échec : " + e.GetBaseException().Message;
                if (++_relaunchErrors >= 3) { _relaunchBroken = true; result += " (relances coupées pour cette bataille)"; }
            }
            finally { OwnOrderEnd(); }
            _lastRelaunch = $"trajet {wt.Id} #{wt.Relaunches} à t={now - _start:0}s";
            WayLog($"relance #{wt.Relaunches}/{MaxRelaunch} du trajet du script {wt.Id} (camp {wt.Side}) : unités {UidSample(wt)} ({wt.Alive} vivante(s)) à ({wt.Center.x:0},{wt.Center.z:0}), immobiles depuis {still:0} s, destination ({wt.Target.x:0},{wt.Target.z:0}){(wt.Tag > 0 ? " tag " + wt.Tag : "")} à {wt.Dist:0} m, arrivée={wt.AtDest ?? "?"}, étape {wt.Stage ?? "illisible"}, engagé selon le jeu {(wt.Engaged ? "oui" : "non")} -> {result}");
        }

        static string UidSample(WayTrack wt)
        {
            var sb = new StringBuilder(); int n = 0;
            foreach (int u in wt.Uids) { if (n++ >= 8) { sb.Append(" ..."); break; } if (sb.Length > 0) sb.Append(' '); sb.Append(u); }
            return sb.Length == 0 ? "?" : sb.ToString();
        }

        // ---------------------------------------------------------------- protected places for Demolition

        static void BuildAreas(GameController gc, float now)
        {
            var l = new List<(float x, float z, float r, string why)>();
            int zones = 0, entries = 0, tags = 0, occupied = 0;
            try
            {
                var all = LuaMap.GetAllMapObjectives();
                for (int i = 0; i < (all?.Count ?? 0); i++)
                {
                    try
                    {
                        var d = all[i]; if (d == null) continue;
                        var p = Il2Cpp.ClassExtensions.ToUnityVector(d.GetActualPos);
                        var s = Il2Cpp.ClassExtensions.ToUnityVector(d.Scale);
                        float sx = Math.Abs(s.x), sz = Math.Abs(s.z);
                        float r = d.ObjectiveZoneType == ObjZoneType.Box ? (float)Math.Sqrt(sx * sx + sz * sz) : Math.Max(sx, sz);
                        r = Math.Clamp(r, 100f, 2000f) + 50f;
                        l.Add((p.x, p.z, r, $"zone objectif {d.UID}")); zones++;
                    }
                    catch { }
                }
            }
            catch (Exception e) { LogOnce("zones-all", "liste des zones objectifs illisible : " + e.Message); }
            try
            {
                _map ??= new LuaMap();
                var objs = _map.GetObjectives(true);
                for (int i = 0; i < (objs?.Length ?? 0); i++)
                {
                    try
                    {
                        var o = objs[i]; if (o == null) continue;
                        var p = o.Position; var s = o.Scale;
                        float r = Math.Clamp(Math.Max(Math.Abs(s.x), Math.Abs(s.z)), 100f, 2000f) + 50f;
                        int uid = -1; try { uid = o.Object.DataUID; } catch { }
                        l.Add((p.x, p.z, r, $"zone objectif {uid}")); zones++;
                    }
                    catch { }
                }
            }
            catch (Exception e) { LogOnce("zones-lua", "zones objectifs (Lua) illisibles : " + e.Message); }
            foreach (var b in _buildingDest) { l.Add((b.pos.x, b.pos.z, 80f, b.order > 0 ? $"entrée de bâtiment ordonnée par le script (tag {b.order})" : "entrée de bâtiment ordonnée par le script")); entries++; }
            foreach (int tag in _tagsCited) if (TagPos(tag, out var tp)) { l.Add((tp.x, tp.z, 60f, $"tag {tag} cité par le script")); tags++; }
            int calls = 0;
            foreach (var ui in _aiUnits)
            {
                if (calls >= 120) break;
                if (!EnemyAi.IsInf(ui.Role) || ScriptReason(ui.Uid, now) == null) continue;
                try
                {
                    calls++;
                    var bs = _map.GetBuildingsInRange(ui.Pos, 25f);
                    for (int i = 0; i < (bs?.Length ?? 0); i++)
                    {
                        var b = bs[i];
                        if (b == null || b.IsDestroyed() || !b.HasUnitsInside()) continue;
                        var sp = b.GetShootPosition();
                        l.Add((sp.x, sp.z, 15f, $"bâtiment occupé par une unité du script (uid {ui.Uid})")); occupied++;
                    }
                }
                catch { }
            }
            Protection = new Areas(l, zones, entries, tags, occupied);
            if (!_areasLogged && zones > 0) { _areasLogged = true; Log($"places protégées des démolitions : zones objectifs {zones}, entrées de bâtiment {entries}, tags du script {tags}, bâtiments occupés {occupied}"); }
        }

        // ---------------------------------------------------------------- tasks and money (read only)

        static void PollTasks(GameController gc, float now)
        {
            Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.MissionEditor.Data.Meta.TaskData> list;
            try { list = gc._taskPanel?._taskDataList; }
            catch (Exception e) { LogOnce("tasks", "liste des tâches illisible : " + e.Message); return; }
            if (list == null) return;
            int n = Math.Min(list.Count, 200);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var t = list[i]; if (t == null) continue;
                    int uid = t.UID, st = (int)t.Status, ty = (int)t.Type;
                    string name = t.DisplayedName ?? "";
                    if (_tasks.TryGetValue(uid, out var old))
                    {
                        if (old.status != st)
                        {
                            _lastTaskT = now;
                            Log($"tâche {uid} '{name}' ({(ty == 0 ? "principale" : "secondaire")}) : {StatusName(old.status)} -> {StatusName(st)} à t={now - _start:0}s");
                        }
                    }
                    else if (_tasksLogged) { _lastTaskT = now; Log($"nouvelle tâche {uid} '{name}' ({(ty == 0 ? "principale" : "secondaire")}) : {StatusName(st)} à t={now - _start:0}s"); }
                    _tasks[uid] = (st, name, ty);
                }
                catch { }
            }
            if (!_tasksLogged && _tasks.Count > 0)
            {
                _tasksLogged = true; _lastTaskT = now;
                var sb = new StringBuilder();
                foreach (var kv in _tasks) sb.Append($" [{kv.Key} '{kv.Value.name}' {StatusName(kv.Value.status)}]");
                Log($"tâches au départ ({_tasks.Count}) :{sb}");
            }
        }

        static string StatusName(int s) => s == 0 ? "active" : s == 1 ? "réussie" : s == 2 ? "échouée" : s == 3 ? "cachée" : s.ToString(CultureInfo.InvariantCulture);

        static void PollMoney(GameController gc, float now)
        {
            if (_localUid < 0) return;
            var f = gc._GetEcsEventBus_k__BackingField?.Gameplay?.GetMoney;
            if (f == null) return;
            float cur;
            try { cur = f.Invoke(_localUid); } catch { return; }
            if (float.IsNaN(cur)) return;
            float prev = _lastMoney;
            _lastMoney = cur;
            if (float.IsNaN(prev)) return;
            if (prev >= 1500f && cur < prev * 0.25f && prev - cur >= 1500f)
            {
                _moneyResets++;
                bool script = now - _lastSetMoneyT <= 15f, deck = now - _lastDeckT <= 15f;
                Log($"argent du joueur : chute brutale {prev:0} -> {cur:0} à t={now - _start:0}s ({(script ? "le script vient de fixer l'argent" : deck ? "le script vient de changer le deck : remise à zéro par le script probable" : "achat ou remise à zéro par le script")}) ; rien n'est modifié");
            }
        }

        // ---------------------------------------------------------------- mission script dump (read only, once per mission)

        static void DumpStep(GameController gc, float now)
        {
            if (_dumpStage >= 99) return;
            try { DumpStepCore(gc, now); }
            catch (Exception e) { _dumpStage = 99; _dumpSb = null; _dumpRaw = null; Log("lecture du script de la mission abandonnée : " + e.GetBaseException().Message + (_scriptParsed ? "" : " : groupes du script inconnus, l'IA ennemie du mod reste en pause pour cette bataille")); }
        }

        static void DumpStepCore(GameController gc, float now)
        {
            var sc = gc.GetScenarioController;
            var nc = sc?.GetNodeController;
            switch (_dumpStage)
            {
                case 0:
                {
                    bool ready = false;
                    try { ready = sc != null && sc.Loaded && nc != null && nc.IsDataLoaded; } catch { }
                    if (!ready && now - _start < 60f) return;
                    _dumpSb = new StringBuilder(1 << 20);
                    string mission = Campaign.MissionUid ?? "inconnue";
                    _dumpSb.Append("# Broken Arrow Realism Overhaul ").Append(GuardVersion).Append(" - script de la mission ").Append(mission).Append(" (lecture seule, rien n'est modifié)\n");
                    string game = "?"; try { game = UnityEngine.Application.version; } catch { }
                    string scen = "?"; try { scen = Mod.Svc<CampaignService>()?.ActiveMission?.Scenario ?? "?"; } catch { }
                    _dumpSb.Append("jeu=").Append(game).Append(" scénario='").Append(scen).Append("' date=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
                    try { _dumpRaw = nc?.RawLoadData; } catch { _dumpRaw = null; }
                    Il2CppReferenceArray<Il2CppBrokenArrow.ScriptEngine.Data.NodeSaveData> nodes = null;
                    try { nodes = _dumpRaw?.Nodes; } catch { }
                    if (nodes != null && nodes.Length > 0)
                    {
                        _dumpSource = "NodeController.RawLoadData";
                        _dumpSb.Append("source=").Append(_dumpSource).Append('\n').Append("\n== NOEUDS (").Append(nodes.Length).Append(")\n");
                        _dumpIndex = 0; _dumpStage = 1;
                    }
                    else { _dumpSource = "fichier .bascr déchiffré"; _dumpStage = 10; }
                    return;
                }
                case 1: DumpNodes(); return;
                case 2: DumpLines(); return;
                case 10: DumpFile(gc); _dumpStage = 20; return;
                case 20:
                    // only a node list actually read to its end (RawLoadData nodes, or a nodes JSON walked without error) makes the cited
                    // groups trustworthy; no source, failed decryption, non-JSON file or arrays gone on re-read -> EnemyAi stays paused (fail closed)
                    _scriptParsed = _graphRead && _dumpNodes > 0;
                    if (!_scriptParsed)
                    {
                        Log($"script de la mission illisible (source {_dumpSource ?? "aucune"}, {_dumpNodes} nœud(s) lu(s), liste incomplète ou vide) : groupes du script inconnus, l'IA ennemie du mod reste en pause pour cette bataille");
                        NotifyPause(TxtKey.MS_WHY_SCRIPT_UNREADABLE);
                    }
                    DumpRuntime(gc, nc); _dumpStage = 30; return;
                case 30: DumpWrite(); return;
                case 31:
                    if (_dumpTask == null || !_dumpTask.IsCompleted) return;
                    if (_dumpWriteError != null) Log($"écriture du script de la mission impossible : {_dumpWriteError}");
                    else Log($"script de la mission écrit : {_dumpPath} ({_dumpNodes} nœuds, {_dumpLines} liens, source {_dumpSource}, empreinte {_dumpHash:X16}{(_dumpTruncated ? ", tronqué" : "")})");
                    _dumpTask = null; _dumpSb = null; _dumpRaw = null; _dumpStage = 99;
                    return;
            }
        }

        /// Node name without case, spaces, underscores or a leading "Node" (the saved name format is not proven).
        static string NodeKey(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name) if (ch != ' ' && ch != '_' && ch != '-') sb.Append(char.ToLowerInvariant(ch));
            string n = sb.ToString();
            return n.StartsWith("node", StringComparison.Ordinal) ? n.Substring(4) : n;
        }

        /// Order nodes (their group fields name units the script will drive). Getters, events, counts and spawns are not orders.
        static bool IsOrderNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = NodeKey(name);
            if (n.StartsWith("get", StringComparison.Ordinal) || n.StartsWith("on", StringComparison.Ordinal) || n.StartsWith("is", StringComparison.Ordinal)) return false;
            if (n.Contains("count") || n.Contains("visible") || n.Contains("spawn") || n.Contains("dead") || n.Contains("condition") || n.Contains("compare") || n.Contains("trigger")) return false;
            foreach (var k in OrderWords) if (n.Contains(k)) return true;
            return false;
        }
        static readonly string[] OrderWords = { "move", "unload", "attack", "capture", "waypoint", "airstrike", "airdrop", "patrol", "enter", "embark", "retreat", "garrison", "holdfire", "firemission", "laser", "altitude", "aggressive", "refund", "command", "order", "follow", "defend", "escort" };
        static bool IsBuildingNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("building") || n.Contains("enter") || n.Contains("house") || n.Contains("garrison") || n.Contains("embark");
        }
        static readonly Regex FirstNumber = new Regex(@"-?\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// Groups named by order nodes (protected for the whole battle) and tags named by building nodes (protected from demolition).
        static void ParseProp(string nodeName, string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            string k = key.ToLowerInvariant();
            if (k.Contains("group") && IsOrderNode(nodeName) && value[0] != '{' && value[0] != '[')
                foreach (var g in SplitGroups(value)) if (_groupsCited.Count < 500) _groupsCited.Add(g);
            if (k.Contains("tag") && IsBuildingNode(nodeName))
            {
                var m = FirstNumber.Match(value);
                if (m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tag) && tag > 0 && _tagsCited.Count < 500) _tagsCited.Add(tag);
            }
        }

        static void HashAdd(string s, int id)
        {
            unchecked
            {
                ulong h = _dumpHash == 0 ? 14695981039346656037UL : _dumpHash;
                h = (h ^ (ulong)(uint)id) * 1099511628211UL;
                if (s != null) foreach (char ch in s) h = (h ^ ch) * 1099511628211UL;
                _dumpHash = h;
            }
        }

        static void DumpNodes()
        {
            var nodes = _dumpRaw?.Nodes;
            if (nodes == null) { _dumpStage = 20; return; }
            int end = Math.Min(nodes.Length, _dumpIndex + DumpNodeCap);
            var sb = _dumpSb;
            _dumpSw.Restart();
            for (; _dumpIndex < end; _dumpIndex++)
            {
                if ((_dumpIndex & 15) == 15 && _dumpSw.Elapsed.TotalMilliseconds >= DumpBudgetMs) break;
                try
                {
                    var nd = nodes[_dumpIndex]; if (nd == null) continue;
                    string name = nd.NodeName; int id = nd.NodeID, sub = nd.SubgraphID;
                    _dumpNodes++; HashAdd(name, id);
                    sb.Append('#').Append(id);
                    if (sub != 0) sb.Append(" [sous-graphe ").Append(sub).Append(']');
                    sb.Append(' ').Append(name).Append('\n');
                    var props = nd.PropData;
                    for (int j = 0; j < (props?.Length ?? 0); j++)
                    {
                        var pr = props[j]; if (pr == null) continue;
                        string key = pr.FieldKeyName, val = pr.Value;
                        ParseProp(name, key, val);
                        if (sb.Length < MaxDumpChars) sb.Append("    ").Append(key).Append(" = ").Append(val).Append('\n');
                        else _dumpTruncated = true;
                    }
                }
                catch (Exception e) { sb.Append("    (nœud illisible : ").Append(e.Message).Append(")\n"); }
            }
            if (_dumpIndex >= nodes.Length)
            {
                _graphRead = _dumpNodes > 0;                            // the node list was walked to its end (groups come from nodes only, not from links)
                int lines = 0; try { lines = _dumpRaw.Lines?.Length ?? 0; } catch { }
                sb.Append("\n== LIENS (").Append(lines).Append(")\n");
                _dumpIndex = 0; _dumpStage = 2;
            }
        }

        static void DumpLines()
        {
            Il2CppReferenceArray<Il2CppBrokenArrow.ScriptEngine.Data.LineSaveData> lines = null;
            try { lines = _dumpRaw?.Lines; } catch { }
            if (lines == null) { _dumpStage = 20; return; }
            int end = Math.Min(lines.Length, _dumpIndex + DumpLineCap);
            var sb = _dumpSb;
            _dumpSw.Restart();
            for (; _dumpIndex < end; _dumpIndex++)
            {
                if ((_dumpIndex & 63) == 63 && _dumpSw.Elapsed.TotalMilliseconds >= DumpBudgetMs) break;
                try
                {
                    var ln = lines[_dumpIndex]; if (ln == null) continue;
                    _dumpLines++;
                    if (sb.Length >= MaxDumpChars) { _dumpTruncated = true; continue; }
                    sb.Append(ln.OutputNodeID).Append('.').Append(ln.OutputConnectorKeyName).Append(" -> ").Append(ln.InputNodeID).Append('.').Append(ln.InputConnectorKeyName);
                    if (ln.SubgraphID != 0) sb.Append(" [sous-graphe ").Append(ln.SubgraphID).Append(']');
                    sb.Append('\n');
                }
                catch { }
            }
            if (_dumpIndex >= lines.Length)
            {
                try
                {
                    var subs = _dumpRaw.SubgraphNodeData;
                    sb.Append("\n== SOUS-GRAPHES (").Append(subs?.Length ?? 0).Append(")\n");
                    for (int i = 0; i < (subs?.Length ?? 0); i++) { var s = subs[i]; if (s != null) sb.Append("graphe ").Append(s.GraphNodeID).Append(" entrée ").Append(s.EnterNodeID).Append(" sortie ").Append(s.ExitNodeID).Append('\n'); }
                }
                catch { }
                _dumpStage = 20;
            }
        }

        /// Fallback when RawLoadData is empty: the .bascr zip of the mission, each entry decrypted by the game's EncryptedFileManager.
        static void DumpFile(GameController gc)
        {
            if (!OperatingSystem.IsWindows()) return;
            var sb = _dumpSb;
            string file = FindBascr(gc);
            if (file == null) { sb.Append("source=aucune (RawLoadData vide et fichier .bascr introuvable)\n"); Log("script de la mission : RawLoadData vide et fichier .bascr introuvable"); return; }
            sb.Append("source=").Append(file).Append('\n');
            try
            {
                using var zip = new ZipArchive(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), ZipArchiveMode.Read, false);
                foreach (var entry in zip.Entries)
                {
                    if (!entry.Name.EndsWith(".bam", StringComparison.OrdinalIgnoreCase) && !entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length > 64_000_000) { sb.Append("\n== ").Append(entry.FullName).Append(" (trop gros, ignoré)\n"); continue; }
                    byte[] bytes;
                    using (var ms = new MemoryStream()) { using (var st = entry.Open()) st.CopyTo(ms); bytes = ms.ToArray(); }
                    string text;
                    try
                    {
                        var il = new Il2CppStructArray<byte>(bytes);
                        text = EncFiles.IsEncrypted(il) ? EncFiles.ReadAllText(il) : Encoding.UTF8.GetString(bytes);
                    }
                    catch (Exception e) { sb.Append("\n== ").Append(entry.FullName).Append(" (déchiffrement impossible : ").Append(e.GetBaseException().Message).Append(")\n"); continue; }
                    if (entry.Name.StartsWith("nodes", StringComparison.OrdinalIgnoreCase)) ParseJsonGraph(text);
                    sb.Append("\n== ").Append(entry.FullName).Append(" (").Append(text?.Length ?? 0).Append(" caractères)\n");
                    if (text == null) continue;
                    if (sb.Length + text.Length < MaxDumpChars) sb.Append(text).Append('\n');
                    else _dumpTruncated = true;
                }
            }
            catch (Exception e) { sb.Append("(lecture du fichier impossible : ").Append(e.GetBaseException().Message).Append(")\n"); }
        }

        static string FindBascr(GameController gc)
        {
            // the campaign mission's own scenario folder first; the scene's default scenario only when it is that same scenario
            var dirs = new List<string>();
            string scen = null;
            try { scen = Mod.Svc<CampaignService>()?.ActiveMission?.Scenario; } catch { }
            if (string.IsNullOrEmpty(scen)) return null;
            try { dirs.Add(Path.Combine(UnityEngine.Application.streamingAssetsPath, "SharedMissions", scen)); } catch { }
            dirs.Add(scen);
            try
            {
                var f = gc.GetScenarioController?.DefaultScenario?.Folder;
                if (!string.IsNullOrEmpty(f) && f.Replace('\\', '/').TrimEnd('/').EndsWith(scen.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) dirs.Add(f);
            }
            catch { }
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    var files = Directory.GetFiles(d, "*.bascr");
                    if (files.Length > 0) return files[0];
                }
                catch { }
            }
            return null;
        }

        /// Walks any JSON object holding NodeName + PropData (the node save format) to read cited groups and tags.
        static void ParseJsonGraph(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int before = _dumpNodes;
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                Walk(doc.RootElement, 0);
                if (_dumpNodes > before) _graphRead = true;             // only a walk that ended without error and found nodes
            }
            catch (Exception e) { _dumpNodes = before; LogOnce("json", "graphe du script (fichier) illisible en JSON : " + e.Message); }   // a partial walk never counts as a read graph
        }

        static void Walk(JsonElement e, int depth)
        {
            if (depth > 12) return;
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x, depth + 1); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("NodeName", out var nn) && nn.ValueKind == JsonValueKind.String)
            {
                string name = nn.GetString();
                int id = e.TryGetProperty("NodeID", out var ni) && ni.ValueKind == JsonValueKind.Number ? ni.GetInt32() : 0;
                _dumpNodes++; HashAdd(name, id);
                if (e.TryGetProperty("PropData", out var pd) && pd.ValueKind == JsonValueKind.Array)
                    foreach (var p in pd.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Object) continue;
                        string key = p.TryGetProperty("FieldKeyName", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                        string val = p.TryGetProperty("Value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
                        ParseProp(name, key, val);
                    }
                return;
            }
            if (e.TryGetProperty("InputNodeID", out _) && e.TryGetProperty("OutputNodeID", out _)) { _dumpLines++; return; }
            foreach (var prop in e.EnumerateObject()) if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array) Walk(prop.Value, depth + 1);
        }

        static void DumpRuntime(GameController gc, Il2CppBrokenArrow.ScriptEngine.Loader.NodeController nc)
        {
            var sb = _dumpSb;
            sb.Append("\n== ZONES OBJECTIFS (en jeu)\n");
            try
            {
                var all = LuaMap.GetAllMapObjectives();
                for (int i = 0; i < (all?.Count ?? 0); i++)
                {
                    var d = all[i]; if (d == null) continue;
                    var p = Il2Cpp.ClassExtensions.ToUnityVector(d.GetActualPos); var s = Il2Cpp.ClassExtensions.ToUnityVector(d.Scale);
                    sb.Append(d.UID).Append(" '").Append(d.DisplayedName).Append("' ").Append(d.ObjectiveZoneType).Append(" pos=(").Append(p.x.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(p.z.ToString("0", CultureInfo.InvariantCulture))
                      .Append(") échelle=(").Append(s.x.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(s.z.ToString("0", CultureInfo.InvariantCulture)).Append(") visible=").Append(d.IsVisible).Append('\n');
                }
            }
            catch (Exception e) { sb.Append("(illisible : ").Append(e.Message).Append(")\n"); }
            sb.Append("\n== DECLENCHEURS\n");
            try
            {
                var sorted = gc.GetScenarioData?._sortedObjects;
                if (sorted != null && sorted.TryGetValue(MEObjectTypeEnum.Trigger, out var tl) && tl != null)
                    for (int i = 0; i < tl.Count; i++)
                    {
                        var td = tl[i]?.TryCast<TriggerData>(); if (td == null) continue;
                        var p = Il2Cpp.ClassExtensions.ToUnityVector(td.GetActualPos); var s = Il2Cpp.ClassExtensions.ToUnityVector(td.Scale);
                        sb.Append(td.UID).Append(" '").Append(td.DisplayedName).Append("' ").Append(td.TriggerType).Append(" pos=(").Append(p.x.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(p.z.ToString("0", CultureInfo.InvariantCulture))
                          .Append(") échelle=(").Append(s.x.ToString("0", CultureInfo.InvariantCulture)).Append(',').Append(s.z.ToString("0", CultureInfo.InvariantCulture)).Append(")\n");
                    }
            }
            catch (Exception e) { sb.Append("(illisible : ").Append(e.Message).Append(")\n"); }
            sb.Append("\n== TACHES\n");
            foreach (var kv in _tasks) sb.Append(kv.Key).Append(" '").Append(kv.Value.name).Append("' ").Append(kv.Value.type == 0 ? "principale" : "secondaire").Append(' ').Append(StatusName(kv.Value.status)).Append('\n');
            sb.Append("\n== VARIABLES DU SCRIPT\n");
            try
            {
                var dict = nc?.CustomVars?._customVariablesDict;
                if (dict != null)
                {
                    var en = dict.GetEnumerator();
                    while (en.MoveNext()) sb.Append(en.Current.Key).Append(" = ").Append(en.Current.Value).Append('\n');
                }
            }
            catch (Exception e) { sb.Append("(illisible : ").Append(e.Message).Append(")\n"); }
            sb.Append("\n== LU PAR LE MOD\n");
            sb.Append("groupes cités par des ordres du script (protégés de l'IA ennemie du mod) : ").Append(string.Join(", ", _groupsCited)).Append('\n');
            sb.Append("tags cités par des nœuds de bâtiment (protégés des démolitions) : ").Append(string.Join(", ", _tagsCited)).Append('\n');
            Log($"script lu ({_dumpSource}) : {_dumpNodes} nœuds, {_dumpLines} liens, groupes cités par des ordres {_groupsCited.Count}, tags de bâtiment cités {_tagsCited.Count}");
        }

        static void DumpWrite()
        {
            string mission = Campaign.MissionUid ?? "inconnue";
            string key = mission + "|" + _dumpHash.ToString("X16", CultureInfo.InvariantCulture);
            if (_dumpedThisRun.Contains(key)) { Log($"script de la mission {mission} déjà écrit pendant cette session de jeu (même empreinte) : pas de nouvelle écriture"); _dumpSb = null; _dumpRaw = null; _dumpStage = 99; return; }
            _dumpedThisRun.Add(key);
            var safe = new StringBuilder();
            foreach (char ch in mission) safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_missions");
            _dumpPath = Path.Combine(dir, safe + ".txt");
            string text = _dumpSb.ToString(), path = _dumpPath;
            _dumpWriteError = null;
            _dumpTask = Task.Run(() =>
            {
                try { Directory.CreateDirectory(dir); File.WriteAllText(path, text, new UTF8Encoding(false)); }
                catch (Exception e) { _dumpWriteError = e.Message; }
            });
            _dumpStage = 31;
        }

        // ---------------------------------------------------------------- report

        static void Report(float now, bool final)
        {
            if (_start < 0) return;
            int visible = 0, mine = 0, theirs = 0, other = 0;
            try
            {
                var gc = GameController._instance;
                _map ??= new LuaMap();
                var objs = gc == null ? null : _map.GetObjectives(false);
                for (int i = 0; i < (objs?.Length ?? 0); i++)
                {
                    try
                    {
                        var o = objs[i]; if (o == null || !o.IsVisible()) continue;
                        visible++;
                        int side = EnemyAi.SideOf(gc, EnemyAi.OwnerRaw(o));
                        if (side == _playerSide) mine++; else if (side == 1 - _playerSide) theirs++; else other++;
                    }
                    catch { }
                }
            }
            catch { }
            int active = 0, won = 0, failed = 0;
            foreach (var t in _tasks.Values) { if (t.status == 0) active++; else if (t.status == 1) won++; else if (t.status == 2) failed++; }
            int enemies = 0, pWay = 0, pCmd = 0, pSpawn = 0, pGroupCmd = 0, pCited = 0;
            foreach (var ui in _units.Values)
            {
                if (ui.Local) continue;
                if (ui.Side != _playerSide) enemies++;
                string r = ScriptReason(ui.Uid, now);
                if (r == null) continue;
                if (r.StartsWith("trajet", StringComparison.Ordinal)) pWay++;
                else if (r == "ordre du script") pCmd++;
                else if (r.StartsWith("apparue", StringComparison.Ordinal)) pSpawn++;
                else if (r.StartsWith("groupe commandé", StringComparison.Ordinal)) pGroupCmd++;
                else pCited++;
            }
            int prot = pWay + pCmd + pSpawn + pGroupCmd + pCited;
            string money = float.IsNaN(_lastMoney) ? "?" : _lastMoney.ToString("0", CultureInfo.InvariantCulture);
            string notReady = final ? null : ProtectionNotReady();
            string aiGate = final ? "" : notReady != null ? $" ; IA ennemie du mod en pause ({notReady})" : " ; IA ennemie du mod : unités du script identifiées, ordres permis";
            Log($"{(final ? "bilan" : "relevé")} t={now - _start:0}s mission={Campaign.MissionUid} : objectifs visibles {visible} (joueur {mine}, ennemi {theirs}, autres {other}) ; tâches actives {active}, réussies {won}, échouées {failed}, dernier changement {Ago(now, _lastTaskT)} ; ennemis vivants {enemies} ; unités du script protégées {prot} (trajet {pWay}, ordre {pCmd}, apparition {pSpawn}, groupe commandé {pGroupCmd}, groupe cité {pCited}) ; trajets du script actifs {_waysNow} (bloqués {_stuckNow}, temps figé ou non suivi ignoré {_wayUncounted:0} s) ; relances {_relaunches} (dernière : {_lastRelaunch}){(_stageBattle == 1 ? $", convois repartis {_relaunchMoved}/{_relaunchEffects} mesurés" : $", mesure : relances possibles {_wouldCount}, bloqués prouvés {_provenStalls}, reprises seules {_selfResumed}")}{(_wayLogs > WayLogMax ? $" ; lignes de trajets non écrites {_wayLogs - WayLogMax}" : "")}{(_econLogs > EconLogMax ? $" ; lignes argent/deck non écrites {_econLogs - EconLogMax}" : "")} ; ordres du script vus {_cmdSeen} (dernier {Ago(now, _lastCmdT)}, sans cible lisible {_cmdNoTarget}) ; apparitions du script {_spawnNotes} (dernière {Ago(now, _lastSpawnT)}) ; argent {money} (chutes brutales {_moneyResets}, argent fixé par le script {_scriptMoneySets}, decks changés {_scriptDeckChanges})");
            Log($"{(final ? "bilan" : "relevé")} crochets : {(_patched ? (_armed ? "armés" : "désarmés") : _refused ? "refusés" : "pas installés")}, appels {Interlocked.Read(ref _hookCalls)} (hors fil principal {Interlocked.Read(ref _hookOffMain)}), perdus {Interlocked.Read(ref _hookDropped)}, erreurs {Interlocked.Read(ref _hookErrors)} ; système des trajets {(_moveSys != null ? "connu" : "inconnu (aucun ordre complexe vu)")} ; groupes : cités {_groupsCited.Count}, commandés {_groupCmd.Count}, unités ajoutées aux groupes cités {_citedAdded}{(_groupBroken ? ", lecture coupée" : "")}{aiGate} ; places protégées des démolitions {Protection?.X.Length.ToString(CultureInfo.InvariantCulture) ?? "pas prêtes"} ; script {(_dumpStage >= 99 ? "lu" : "en lecture")} ({_dumpNodes} nœuds)");
        }

        static string Ago(float now, float t) => t < 0 ? "jamais" : $"il y a {now - t:0} s";
    }
}
