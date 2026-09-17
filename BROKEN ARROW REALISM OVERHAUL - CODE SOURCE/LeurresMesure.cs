// RealismOverhaul - flares v2 "toujours trompé" (player's choice, 2026-09-16) and guided-missile measurement. Both sides alike, solo
//  campaign only, no owner filter. When an INFRARED guided missile rolls its hit chance (SeekerSystem.CalculateMissileDivertion) while
//  its target has active decoys, the missile is decoyed: forced chance 0, it stays decoyed for its whole flight (a later roll can never
//  turn it back into a hit), it deals no damage to anything (damage gate inside its impact) and, once proven, it drops its target and
//  flies off (SeekerSystem.DropCurrentTarget called from the main loop, never from a hook). With no active decoy the game's own chance
//  applies, drawn by the mod's own random generator (seeded per battle, main thread). Radar, laser, command and wire-guided missiles
//  ignore flares: on a roll against active decoys their chance becomes the game's chance with 0 decoys (one call back into
//  CalculateMissileHitChance, used only once rule R5 has proven it).
//  Infrared ids (flares_v2_design.md §4.1, seeker checked in the vanilla table): R-60 165/399, R-27T/ET 309/310, R-73 158/734,
//  Stinger F/J/vehicle/UAV 462/474/18/403, Igla-N 106, Grom 598, Strela-10 9M333 181, AIM-9M heli 398, Igla-S 127, Piorun 597,
//  RIM-116 560, Verba 97, Mistral 3 601, AIM-9X ground 465, AIM-9 plane 59 (all FireAndForget) and IRIS-T SLM 618 (TerminalGuidance
//  in the table, imaging infrared in reality).
//  The infrared decision and the damage gate are on from the first battle: stage 1 is the floor of the stored stage (an older stored 0
//  is raised to 1 once, with one notice). Above that floor the stage only governs the fly-off; it changes only at battle end, from
//  logged proof rules, stored in hidden preferences (LeurresEtape, LeurresPreuves), with a notice in simple French, and takes effect
//  from the next battle:
//   1 decision + damage gate; 2 fly-off trial on the first 5 decoyed missiles of a battle; 3 fly-off of every decoyed missile.
//  A failed control (health drop right after a gated impact, another ammunition inside a gated impact, too few impacts linked to their
//  missile, hook errors) steps the fly-off back to 1 and restarts its proof; it never turns the decision off. Stage 0 is only the
//  behaviour while a safety cut-off holds (watchdog trip, session switch-off, partial install, end screen, mod switched off): classic
//  flares. The measurement (missile identity, chance calls per roll, the 0-decoy base call, the engine formula grid, missile settings,
//  the 0.90 decoy anomaly, one line per missile) runs at every stage.
//  Safety: hooks installed lazily in a solo battle with their own Harmony id and never unpatched (a volatile armed flag instead); hooks
//  act on the main thread only (an off-main call is counted and left vanilla); error kill-switch; crash guard (a battle that never
//  ended takes the fly-off away at the next battle and restarts its proof, two in a row refuse the whole module for this version, and
//  an interrupted fly-off trial is not retried in this version); combat watchdog on a running game clock (real seconds times the session game speed) that
//  stops while the game is paused or frozen (a pause signal is ignored for the battle once impacts land or units move while it holds)
//  or while the end screen is up (flare effects, base call and fly-off stay off meanwhile). No impact for 3 minutes while enemies are
//  within 1 km, when the module acted in that window or in the 90 s before it, or the battle has been silent since it began: flare
//  effects, base call and fly-off off for THIS BATTLE only, nothing written. Only one thing reaches beyond the battle, and never beyond
//  the game session: fire coming back within 30 s of the switch-off in three different battles of the same session (the
//  anti-helicopter watchdog's own switch-off can also bring fire back and is not readable from here, hence three and not two) keeps
//  flare effects, base call and fly-off off until the game is closed. No watchdog verdict writes a version-wide stop or erases a proof.
//  Never Get<T>, RandomComponent, GenerateMissVectorOnTarget, SetNewTargetToMissile or any
//  target-search hook. The [ATGM] measurement of ground-attack missiles is unchanged. Logs: [LEURRES] and [ATGM].
//  The impact depth (ShellHitSystem.InternalUpdate) is shared with AntiHeliTouches through HitDepth / HitSerial / GatedFor.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using ShellHit = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellHitSystem;
using ShellLife = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellLifetimeSystem;
using HitDet = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.HitDetectionSystem;
using Seeker = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Guidance.Systems.SeekerSystem;
using MissilesHelper = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Missiles.MissilesHelper;
using BsConst = Il2CppBrokenArrow.Shared.Ecs.BattleSystem.BattleSystemConstants;
using RemoveReason = Il2CppBrokenArrow.Client.Ecs.BattleSystem.TargetRemoveReason;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class LeurresMesure
    {
        const string GuardVersion = "0.24.0";
        const int MaxErrors = 50;
        const int TableSize = 128, RollRecs = 4;
        const int StageMeasure = 0, StageGate = 1, StageTrial = 2, StageFull = 3;
        const byte StNone = 0, StHit = 1, StFlare = 2, StOther = 3;
        const byte DecMeasure = 0, DecHit = 1, DecFlare = 2, DecOther = 3, DecLocked = 4, DecKept = 5;
        const int MaxMissileLines = 40, TrialDrops = 5, MaxDropErrors = 5, MaxScanErrors = 50;
        const long AliveSeenMs = 1500, AliveUnseenMs = 3000, MaxAgeMs = 90000, DecoyWindowMs = 3000, DropLeaveMs = 20000, MinBattleMs = 120000;
        const float WatchEvery = 5f, WatchContact = 1000f;
        const int WatchSamples = 36;                                    // 3 minutes of 5 s samples
        const long WatchStartImpacts = 20;                              // fewer impacts than this before the window = silent since the battle began
        const float WatchLookback = 90f;                                // running seconds before the window in which a module action still blames a silence (rounds already in flight, artillery included, land first)
        const int FrozenSamples = 12;                                   // same distance between the sides over the last minute of the window = positions maybe frozen
        const float JudgeResume = 30f;                                  // fire back within this many running seconds after the switch-off = a "fast resume" battle
        const long ResumeImpacts = 3;                                   // more impacts than a silent window allows
        const int SessionResumes = 3;                                   // fast-resume battles in one game session before the session switch-off
        const float TickMax = 2f;                                       // longest real-time step credited to one watchdog tick (then times the game speed)
        const float MaxSpeed = 16f;                                     // highest session TimeScale trusted as a game speed
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- missile classes
        static readonly int[] IrIds = { 165, 399, 309, 310, 158, 734, 462, 474, 18, 403, 106, 598, 181, 398, 127, 597, 560, 97, 601, 465, 59, 618 };
        const int NIr = 22;
        static int IrIndex(int id) => id switch
        {
            165 => 0, 399 => 1, 309 => 2, 310 => 3, 158 => 4, 734 => 5, 462 => 6, 474 => 7, 18 => 8, 403 => 9, 106 => 10,
            598 => 11, 181 => 12, 398 => 13, 127 => 14, 597 => 15, 560 => 16, 97 => 17, 601 => 18, 465 => 19, 59 => 20, 618 => 21, _ => -1
        };
        // radar, command and laser rows added to the engine formula grid (Pantsir, Tor M1, Tunguska, RBS-70, AMRAAM ground, Hellfire SHORAD)
        static readonly int[] GridOtherIds = { 20, 28, 232, 573, 464, 166 };

        // ground-attack guided missiles followed one by one ([ATGM]: Hellfire, JAGM, Vikhr, Ataka, Kokon, TOW, LMUR, Kornet-M, Spike LR, Hermes, Kh-25ML...)
        static readonly int[] TrackedIds = { 5, 13, 52, 80, 83, 123, 131, 164, 270, 449, 456, 549, 553, 555, 733 };
        const int NTracked = 15;

        // ---------------------------------------------------------------- preferences and session state
        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean, _stagePref;
        static MelonPreferences_Entry<string> _guardVersion, _proofPref, _stopPref;
        static MelonPreferences_Entry<bool> _dropPending;                     // a fly-off call ran in the current battle (saved before the first call)
        static HarmonyLib.Harmony _harmony;
        static bool _patchTried, _refused, _refusedNotified, _everOnline, _sessionArmed, _settingsLogged, _hitPatched;
        static bool _hooksFull, _simpleHooks;
        static bool _announce;                                          // stored stage raised to the floor at start-up: one notice at the first battle where the decision acts
        // switched off until the game is closed (never saved): three fast-resume battles in this session, or DisarmEffects
        static bool _effectsOffSession, _baseOffSession, _dropsOffSession;
        static int _fastResumes;                                        // battles of this game session whose fire came back within 30 s of the switch-off
        static int _hooksInstalled;
        static float _next, _nextReport, _nextWatch, _battleStartReal;

        static volatile int _mainThread;
        static volatile bool _armed;
        static volatile int _stage;                                     // stage of the battle in progress (read at battle start)
        static volatile bool _effectsOff;                               // watchdog, partial install or kill-switch: stage 0 behaviour
        static volatile bool _endScreenOff;                             // Campaign.BattleOver seen on the last watchdog tick: effects and base call off while it holds
        static volatile int _frame;                                     // main-loop frame counter (FramePrincipal), stamps hook depths
        static volatile bool _baseProven;                               // rule R5 proven: radar/laser/command missiles get the 0-decoy chance
        static volatile bool _baseOff;                                  // watchdog trip, session switch-off or partial install: no re-entrant 0-decoy call either
        // the whole mod switched off from its tab mid-battle: Frame no longer runs, so the effects read the switch themselves (plain field read)
        static bool ModOn => Mod.Actif == null || Mod.Actif.Value;
        static int EffStage => _effectsOff || _endScreenOff || !ModOn ? StageMeasure : _stage;

        // ---------------------------------------------------------------- hook side, per thread
        [ThreadStatic] static int _hitDepth, _depthFrame;
        [ThreadStatic] static long _hitSerialCur;
        [ThreadStatic] static int _linkSlot1, _gateSlot1;               // table slot + 1 of the shell being processed (0 = none)
        [ThreadStatic] static long _countedSerial, _gatedSerial;
        [ThreadStatic] static int _gatedTarget;
        [ThreadStatic] static float _gatedValue;
        [ThreadStatic] static int _rollDepth, _rollFrameT, _rollChance;
        [ThreadStatic] static bool _rollForce, _rollDecided, _rollFlares, _inBase;
        [ThreadStatic] static float _rollForced;
        [ThreadStatic] static EcsEntity _rollEntity;
        static long _hitSerial;

        // per-thread cache ammo pointer -> Id (a few entries, cleared when the generation changes at each new battle)
        [ThreadStatic] static IntPtr _cp0, _cp1, _cp2, _cp3;
        [ThreadStatic] static int _ci0, _ci1, _ci2, _ci3, _cNext, _cGen;
        static volatile int _cacheGen = 1;

        // counters that any thread may touch
        static long _hitUpdates, _hitTotal, _hitOffMain, _dmgInHit, _dmgOutside, _dmgOffMain, _divert, _divertReroll, _divertOffMain;
        static long _chanceOffMain, _rollOffMain, _staleDepth, _staleRoll, _errors;
        static readonly long[] _chanceCalls = new long[2];                                            // [0] outside a roll, [1] inside a roll
        static readonly long[] _decoyN = new long[8], _decoyChance = new long[8], _decoyEffect = new long[8];   // [roll * 4 + active flares 0/1/2/3+] (sums x1000)
        static readonly long[] _bN = new long[NTracked * 2], _bSum = new long[NTracked * 2], _bMin = new long[NTracked * 2], _bMax = new long[NTracked * 2];
        static readonly long[] _bEcm = new long[NTracked * 2], _bStress = new long[NTracked * 2], _bDecoy = new long[NTracked * 2];
        static readonly long[] _cN = new long[NTracked * 2], _cSum = new long[NTracked * 2], _cMin = new long[NTracked * 2];
        static readonly int[] _otherId = new int[16];
        static readonly long[] _otherN = new long[16];
        static long _otherOverflow;

        // ---------------------------------------------------------------- missile table (main thread only: hooks use it only on the main thread)
        struct Missile
        {
            public bool Used, SeenInScan, IdentityCounted, Dropped, DropFailed, AirHitAfterDrop;
            public byte State;
            public int Eid, Ver, Ammo, Irx, Rolls, FlareRolls, Impacts, GatedImpacts, GatedCalls, UngatedCalls, DivertCalls, HitSide, RollFrame, SeenFrame;
            public long FirstMs, LastSeenMs, LastDivertMs, DecideMs, DropMs;
            public float LastChance, GatedDamage, UngatedDamage;
            public EcsEntity E;
        }
        struct RollRec { public long Ms; public int N; public float Effect, Chance, Base, U; public byte Decision; public bool Force; }
        static readonly Missile[] _t = new Missile[TableSize];
        static readonly RollRec[] _rr = new RollRec[TableSize * RollRecs];
        static int _used;
        static long _lastFlareGoneMs = long.MinValue / 2;

        /// Per-battle counters written on the main thread (replaced as a whole at each battle start).
        sealed class Bat
        {
            public long IrRolls, IrFlareRolls, Hit, Flare, Other, Locked, HitToFlare, TableFull, AmmoMismatch, NonIrBase, NonIrBaseSkipped;
            public long BaseCalls, BaseErr, BaseBelow, ZeroEq, ZeroNe, RatioN;
            public double RatioSum;
            public long H0, H1, H2, H1F, H2F;
            public long EligImpacts, Linked, ScanTotal, ScanSeen, ScanErrors;
            public long GateImpacts, GateCalls, GateLeak, GatedAir0, GatedAir1, GatedOther;
            public double GatedDmg;
            public long Drops, DropErr, DropSkippedDead, DroppedFinal, DroppedLeftOk, AirHitAfterDrop;
            public long Missiles, LeftNoImpact, ForcedHit, ForcedHitImpact, MissileLines, MissileLinesSuppressed, DivertCallsTracked;
            public double FlightSeconds;
            public long Effect65, Effect90, EffectOther;
            public float FirstEffect90 = -1f, LastEffect90 = -1f;
            public readonly long[] AmmoMissiles = new long[NIr], AmmoDamaged = new long[NIr], AmmoLastChance = new long[NIr];
        }
        static Bat _b = new Bat();

        static readonly bool[] _fuse0 = new bool[NIr];
        static bool _rowsChecked, _gridDone, _aircraftLogged, _tripped, _dropsOff, _scanAvail, _scanWarned, _scanOff;
        static IntPtr _gridSrc;
        static int _lastUnityFrame = -1;
        static long _battleStartMs;
        static System.Random _rng = new System.Random(1);
        static LuaMap _map;

        // combat watchdog
        static readonly bool[] _wContact = new bool[WatchSamples];
        static readonly long[] _wHits = new long[WatchSamples];
        static readonly long[] _wAct = new long[WatchSamples];          // running total of what the module changed, per sample
        static readonly float[] _wRun = new float[WatchSamples];        // running game clock of each sample
        static readonly float[] _wDist = new float[WatchSamples];       // distance between the sides at each sample
        static int _wIdx = -1, _wCount;
        static bool _contactWarned, _lullLogged;
        static long _wActLast;                                          // last action total seen, and the running clock of its last change
        static float _wActRun = float.NegativeInfinity;
        // running game clock: Time.time keeps running while this game is paused (timeScale stays 1), so pauses are read from the game
        static float _runClock, _frozenSec, _lastTickT = -1f, _lastGameTime, _speed = 1f;
        static long _lastTickImp;
        static bool _gtSeen, _gtLive, _unityPrev, _sessPrev, _gtPrev, _sessSeenRun;
        static float _unityClaimDist = -1f, _sessClaimDist = -1f, _gtClaimDist = -1f;   // distance between the sides when that pause claim started
        static bool _unityDistrust, _sessDistrust, _gtDistrust;         // per battle: that pause signal claimed a pause while the combat went on
        // verdict after a trip (per battle, nothing written)
        static bool _judged, _tripBlamed, _tripFrozen;
        static float _tripRun;
        static long _resumeImp;                                         // impacts counted on running ticks since the trip

        static string _lastReport, _lastDecisions, _lastAtgm;
        static readonly Dictionary<int, string> _ammoNames = new();
        static readonly Dictionary<string, float> _warnNext = new();

        static void Log(string s) => Mod.Log.Msg("[LEURRES] " + s);
        static void LogAtgm(string s) => Mod.Log.Msg("[ATGM] " + s);

        // ---------------------------------------------------------------- public interface
        /// Depth of ShellHitSystem.InternalUpdate on the calling thread (0 = not inside an impact). Any thread, no allocation.
        internal static int HitDepth => _hitDepth > 0 && _depthFrame == _frame ? _hitDepth : 0;
        /// Serial number of the impact being processed on the calling thread (meaningful while HitDepth > 0).
        internal static long HitSerial => _hitSerialCur;
        /// The impact hooks are installed and armed, so HitDepth can become > 0.
        internal static bool HitDepthReady => _armed && _hitPatched;
        /// True inside a CalculateHitDamage call on this target that the flare gate has just set to 0 (called by later postfixes of the
        /// same call, same thread). Any thread, no allocation.
        internal static bool GatedFor(int targetEntityId) => _armed && _gatedSerial != 0 && _gatedSerial == _hitSerialCur && _gatedTarget == targetEntityId && HitDepth > 0;
        /// Damage value the gate removed in that call (before the resistance cheat).
        internal static float GatedValue => _gatedValue;
        /// Every impact seen since the battle started (combat watchdogs). Any thread.
        internal static long ImpactsThisBattle => Interlocked.Read(ref _hitTotal);
        /// Current flare stage of the battle in progress, after safety cut-offs: 1 infrared decision + gate (the floor), 2 fly-off trial,
        /// 3 full; 0 only while a safety cut-off holds (classic flares).
        internal static int CurrentStage => EffStage;
        /// Main thread: an infrared missile rolled against active decoys is in flight, or left less than 3 s ago.
        internal static bool DecoyWindowActive
        {
            get
            {
                if (!_sessionArmed) return false;
                if (Environment.TickCount64 - _lastFlareGoneMs <= DecoyWindowMs) return true;
                if (_used == 0) return false;
                for (int i = 0; i < TableSize; i++) if (_t[i].Used && (_t[i].State == StFlare || _t[i].FlareRolls > 0)) return true;
                return false;
            }
        }
        /// Main thread: switches the flare effects, the base call and the fly-off off until the game is closed (a shared combat watchdog may
        /// call it). Nothing is written.
        internal static void DisarmEffects(string why)
        {
            if (_effectsOffSession) return;
            OffForSession();
            Log($"effets des leurres, chance sans leurre et décrochage coupés jusqu'à la fermeture du jeu ({why}) ; la mesure continue");
        }

        /// Session switch-off (never saved): this battle at once, and every later battle of this game session through ArmBattle.
        static void OffForSession()
        {
            _effectsOffSession = true; _baseOffSession = true; _dropsOffSession = true;
            _effectsOff = true; _baseOff = true; _dropsOff = true;
        }

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Leurres");
            _enabled = c.CreateEntry("MesureLeurres", true, description: Build.Desc("Leurres réalistes (toujours actif) : un missile infrarouge face à des leurres actifs rate toujours et ne fait aucun dégât ; mesure des missiles guidés"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _stagePref = c.CreateEntry("LeurresEtape", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _proofPref = c.CreateEntry("LeurresPreuves", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            // version-wide fallback to the 0.22.8 observer hooks when not empty; no watchdog verdict ever writes it
            _stopPref = c.CreateEntry("LeurresArret", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _dropPending = c.CreateEntry("LeurresDecrochageEnCours", false, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion)
            {
                _guardVersion.Value = GuardVersion;
                _unclean.Value = 0;
                _dropPending.Value = false;
                _stopPref.Value = "";                                   // a new version gets a new chance; the proven stage is kept
                var pv = LoadProofs(); if (pv.Remove("volcrash")) SaveProofs(pv);   // and a new fly-off trial is allowed
            }
            if (_stagePref.Value < StageGate || _stagePref.Value > StageFull)
            {
                // the decision and the damage gate never wait for a measurement battle: stage 1 is the floor (older measurement counters dropped)
                _stagePref.Value = StageGate;
                var pv = LoadProofs(); ResetKeys(pv, "s0"); ResetKeys(pv, "s1"); ResetKeys(pv, "s2"); SaveProofs(pv);
                _announce = true;
            }
            _simpleHooks = !string.IsNullOrEmpty(_stopPref.Value);
            _mainThread = Environment.CurrentManagedThreadId;
            ResetBuckets();
        }

        internal static void ResetSession() { EndBattle("nouvelle mission"); _everOnline = false; }
        internal static void OnQuit() => EndBattle("fermeture du jeu");

        // ---------------------------------------------------------------- main loop
        /// Every frame in a campaign mission (Mod.OnUpdate, and Frame as a fallback; runs once per Unity frame): frame counter, projectile
        /// list scan for the tracked missiles, fly-off calls, table expiry. Main thread only.
        internal static void FramePrincipal()
        {
            int fc;
            try { fc = UnityEngine.Time.frameCount; } catch { return; }
            if (fc == _lastUnityFrame) return;
            _lastUnityFrame = fc;
            _frame = _frame + 1;
            if (!_sessionArmed || !_armed || _used == 0) return;
            long ms = Environment.TickCount64;
            if (!_scanOff) Scan(ms);
            if (EffStage >= StageTrial && !_dropsOff) Drops(ms);
            Expire(ms);
        }

        static int _wait;

        /// Every frame in a campaign mission; works every second. Allocation-free gate: the closures of the tick live in Tick.
        internal static void Frame()
        {
            try { FramePrincipal(); } catch (Exception e) { Warn("principal", "boucle principale : erreur (" + e.GetBaseException().Message + ")"); }
            if (_enabled == null || !_enabled.Value || _refused) { _armed = false; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            // one heavy module job per frame (Planif.cs); the battle-start arming, its preference write and its hook installs may wait longer
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + 1f;
            Tick(now);
        }

        static void Tick(float now)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            if (gc?._GameSession_k__BackingField?.CurrentPlayer == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_armed) { _armed = false; Log("partie en ligne : leurres réalistes et mesure coupés"); } return; }
            if (!_settingsLogged) { _settingsLogged = true; Safe("réglages", LogSettings); }
            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2) { Refuse(); return; }
                ArmBattle(now);
            }
            if (!_patchTried) { _patchTried = true; TryPatchAll(); }
            _armed = !_refused && Interlocked.Read(ref _errors) <= MaxErrors;
            if (!_armed) return;
            if (_announce && _hooksFull && EffStage >= StageGate)
            {
                _announce = false;
                Mod.Notify(TxtKey.N_FLARES_ACTIVE);
            }
            if (!_rowsChecked) Safe("lignes", CheckRows);
            if (!_gridDone) Safe("grille", FormulaGrid);
            if (!_aircraftLogged && (now - _battleStartReal >= 20f || _b.Effect90 > 0)) Safe("appareils", () => LogAircraft(now));
            Safe("chien de garde", () => Watchdog(now));
            if (now >= _nextReport) { _nextReport = now + 30f; Safe("relevé", () => Report(false)); }
        }

        static void Refuse()
        {
            _refused = true;
            _armed = false;
            Mod.Log.Warning("[LEURRES] leurres réalistes et mesure désactivés pour cette version : les deux dernières parties ne se sont pas terminées normalement ; leurres classiques du jeu");
            if (_stagePref.Value != StageGate)
            {
                Log($"étape {_stagePref.Value} ramenée à 1 par sécurité (plus de décrochage), preuves effacées");
                _stagePref.Value = StageGate;
                _proofPref.Value = "";
                MelonPreferences.Save();
            }
            if (!_refusedNotified) { _refusedNotified = true; Mod.Notify(TxtKey.N_FLARES_OFF_SAFETY); }
        }

        static void ArmBattle(float now)
        {
            _sessionArmed = true;
            // The previous battle never ended (game stopped). The fly-off call is unproven: it is taken away and its proof restarts before
            // arming again. The decision and the damage gate (plain result writes) stay; a second interrupted battle in a row refuses the module.
            if (_unclean.Value >= 1)
            {
                int from = Math.Clamp(_stagePref.Value, StageGate, StageFull);
                var pr = LoadProofs();
                ResetKeys(pr, "s0"); ResetKeys(pr, "s1"); ResetKeys(pr, "s2");
                if (_dropPending.Value) pr["volcrash"] = 1;                   // a fly-off call ran in the interrupted battle: no new trial in this version
                SaveProofs(pr);
                _stagePref.Value = StageGate;
                if (from >= StageTrial)
                {
                    Mod.Log.Warning($"[LEURRES] la dernière partie ne s'est pas terminée normalement : étape {from} ramenée à 1 par sécurité (plus de décrochage) ; les missiles infrarouges leurrés ratent toujours sans dégâts");
                    Mod.Notify(TxtKey.N_FLARES_DROP_OFF_INTERRUPTED);
                }
                else Mod.Log.Warning("[LEURRES] la dernière partie ne s'est pas terminée normalement : missiles infrarouges leurrés toujours ratés sans dégâts, preuves du décrochage recommencées ; une deuxième partie interrompue de suite coupera les leurres réalistes");
            }
            _dropPending.Value = false;
            _unclean.Value = _unclean.Value + 1;
            MelonPreferences.Save();
            _cacheGen++;                                                       // ammo rows of the previous battle may be gone
            _b = new Bat();
            ClearTable();
            _battleStartMs = Environment.TickCount64;
            _battleStartReal = now;
            _nextReport = now + 30f;
            _rng = new System.Random(unchecked(Environment.TickCount * 31 + 7));
            // a watchdog trip of the previous battle ends here; only the session flags (never saved) carry over
            _tripped = false; _dropsOff = _dropsOffSession; _scanAvail = false; _scanWarned = false; _scanOff = false;
            _rowsChecked = false; _aircraftLogged = false; _contactWarned = false; _lullLogged = false;
            _wIdx = -1; _wCount = 0;
            _wActLast = 0; _wActRun = float.NegativeInfinity;
            _runClock = 0f; _frozenSec = 0f; _nextWatch = 0f; _lastTickT = -1f; _lastGameTime = 0f; _lastTickImp = 0; _speed = 1f;
            _gtSeen = false; _gtLive = false; _unityPrev = false; _sessPrev = false; _gtPrev = false; _sessSeenRun = false;
            _unityClaimDist = -1f; _sessClaimDist = -1f; _gtClaimDist = -1f;
            _unityDistrust = false; _sessDistrust = false; _gtDistrust = false;
            _judged = false; _resumeImp = 0;
            _endScreenOff = Campaign.BattleOver;
            _baseOff = _baseOffSession || (_patchTried && !_hooksFull);
            _lastFlareGoneMs = long.MinValue / 2;
            Interlocked.Exchange(ref _hitTotal, 0);
            _effectsOff = _effectsOffSession || (_patchTried && !_hooksFull);
            // stage 1 is the floor: the infrared decision and the damage gate act from the first battle
            _stage = _simpleHooks || _effectsOffSession ? StageMeasure : Math.Clamp(_stagePref.Value, StageGate, StageFull);
            var p = LoadProofs();
            _baseProven = !_simpleHooks && P(p, "base") == 1;
            try { AntiHeliTouches.TakeProof(); } catch { }                      // leftovers from before this battle
            string ir = EffStage >= StageGate
                ? "décision infrarouge ACTIVE dès cette bataille : un missile infrarouge qui trouve des leurres actifs rate toujours et ne fait aucun dégât"
                : $"décision infrarouge coupée pour cette bataille ({OffWhy()}) : leurres classiques du jeu";
            string drop = EffStage < StageGate ? "non" : _dropsOff ? "coupé" : _stage == StageTrial ? "essai sur les 5 premiers missiles leurrés" : _stage >= StageFull ? "tous les missiles leurrés" : "pas encore (preuves en cours)";
            Log($"bataille : {ir} ; décrochage des missiles leurrés : {drop} ; étape {StageName(EffStage)}" +
                (_simpleHooks ? $" ; crochets simples de sécurité ({_stopPref.Value})" : "") +
                (!_simpleHooks && (_effectsOffSession || _baseOffSession || _dropsOffSession) ? " ; effets des leurres, calcul de chance sans leurre et décrochage coupés jusqu'à la fermeture du jeu (chien de garde)" : "") +
                $" ; missiles radar, laser et à commande : chance sans leurre {(_baseOff ? "non calculée (jeu inchangé)" : _baseProven ? "appliquée face aux leurres" : "pas encore prouvée (jeu inchangé)")} ; mêmes règles pour les deux camps");
        }

        /// Why the decision is off for the battle being armed (log text).
        static string OffWhy() =>
            _simpleHooks ? "crochets simples de sécurité"
            : _effectsOffSession ? "coupée par le chien de garde jusqu'à la fermeture du jeu"
            : _patchTried && !_hooksFull ? "points d'accroche incomplets"
            : !ModOn ? "mod désactivé"
            : _endScreenOff ? "écran de fin de bataille encore affiché, elle revient dès qu'il disparaît"
            : "sécurité";

        static void EndBattle(string why)
        {
            _armed = false;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            long ms = Environment.TickCount64;
            try { FinalizeAll(ms); } catch (Exception e) { Log("fin des missiles suivis illisible : " + e.GetBaseException().Message); }
            bool normal = !_tripped && ms - _battleStartMs >= MinBattleMs;
            long errors = Interlocked.Read(ref _errors);
            long offMain = Interlocked.Read(ref _hitOffMain) + Interlocked.Read(ref _divertOffMain) + Interlocked.Read(ref _rollOffMain) + Interlocked.Read(ref _dmgOffMain);
            try { Report(true); } catch (Exception e) { Log("bilan illisible : " + e.GetBaseException().Message); }
            if (_tripped && !_judged)
                Log("chien de garde : bataille finie avant le verdict sur la coupure : rien n'est compté");
            AntiHeliTouches.AirProof air = default;
            try { air = AntiHeliTouches.TakeProof(); } catch { }
            try { EvaluateStage(why, normal, errors, offMain, air); }
            catch (Exception e) { Log("règles de preuve illisibles : " + e.GetBaseException().Message); }
            if (_unclean.Value != 0) _unclean.Value = 0;
            _dropPending.Value = false;
            MelonPreferences.Save();
            ClearTable();
            Log($"fin de bataille ({why})");
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

        // ---------------------------------------------------------------- patches
        static void TryPatchAll()
        {
            _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.LeurresMesure");
            bool full = !_simpleHooks;
            int ok = 0;
            int hit = Patch("impacts", AccessTools.Method(typeof(ShellHit), "InternalUpdate"), full ? nameof(HitPrefix) : nameof(HitPrefixSimple), nameof(HitPostfix), false);
            ok += hit;
            _hitPatched = hit == 1;
            // Priority.First: the gate writes 0 before Resistance scales the result and before AntiHeliTouches reads it
            ok += Patch("dégâts", AccessTools.Method(typeof(BSH), "CalculateHitDamage"), null, full ? nameof(DamagePostfix) : nameof(DamagePostfixSimple), full);
            ok += Patch("tirages de missile", AccessTools.Method(typeof(Seeker), "CalculateMissileDivertion"), full ? nameof(DivertPrefix) : nameof(DivertPrefixSimple), nameof(DivertPostfix), false);
            // overload 1 is the one the engine calls (the detailed 8-value overload never fired in the 0.22.4 log)
            ok += Patch("chance de toucher (munition)", AccessTools.Method(typeof(BSH), "CalculateMissileHitChance",
                new[] { typeof(Ammo), typeof(float), typeof(float), typeof(int), typeof(float) }), null, full ? nameof(ChancePostfix) : nameof(ChancePostfixSimple), false);
            _hooksInstalled = ok;
            _hooksFull = full && ok == 4;
            if (ok == 0) { _refused = true; Log("aucun point d'accroche installable dans cette version du jeu : leurres classiques"); return; }
            if (!_hooksFull) { _effectsOff = true; _baseOff = true; _stage = StageMeasure; }   // no watchdog without the full hooks: nothing may act
            Log($"{ok}/4 point(s) d'accroche installé(s), forme {(full ? "0.23.0 (missile et projectile lus)" : "simple 0.22.8 (sécurité)")}" +
                (_hooksFull ? $" ; étape {StageName(EffStage)}" : " ; effets des leurres coupés (installation incomplète ou sécurité)"));
        }

        static int Patch(string label, MethodInfo target, string prefix, string postfix, bool postfixFirst)
        {
            try
            {
                if (target == null) { Log($"{label} : méthode introuvable"); return 0; }
                var t = typeof(LeurresMesure);
                var pre = prefix == null ? null : new HarmonyMethod(t.GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static));
                var post = postfix == null ? null : new HarmonyMethod(t.GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static));
                if (post != null && postfixFirst) post.priority = Priority.First;
                _harmony.Patch(target, prefix: pre, postfix: post);
                return 1;
            }
            catch (Exception e) { Log($"{label} : non installé ({e.GetBaseException().Message})"); return 0; }
        }

        // ---------------------------------------------------------------- hooks, 0.23.0 shapes (no Unity call, no logging, no allocation)
        /// ShellHitSystem :: Void InternalUpdate(Entity& shellEntity). Depth and serial; on the main thread, links the shell to a tracked
        /// missile and arms the damage gate for a decoyed one (or a missed one with a contact fuse only).
        static void HitPrefix(ref EcsEntity shellEntity)
        {
            if (Campaign.MissionInerte || !_armed) return;                            // mission without the mod: no depth, no gate
            if (_hitDepth > 0 && _depthFrame != _frame) { _hitDepth = 0; Interlocked.Increment(ref _staleDepth); }
            _hitDepth++;
            _depthFrame = _frame;
            _hitSerialCur = Interlocked.Increment(ref _hitSerial);
            Interlocked.Increment(ref _hitUpdates);
            Interlocked.Increment(ref _hitTotal);
            if (Environment.CurrentManagedThreadId != _mainThread) { Interlocked.Increment(ref _hitOffMain); return; }
            if (_hitDepth != 1) return;
            _linkSlot1 = 0; _gateSlot1 = 0;
            if (_used == 0) return;
            try
            {
                int s = Find(shellEntity.EntityId, shellEntity.Version);
                if (s < 0) return;
                _linkSlot1 = s + 1;
                _t[s].Impacts++;
                if (EffStage < StageGate) return;
                byte st = _t[s].State;
                if (st == StFlare || (st == StOther && _fuse0[_t[s].Irx]))
                {
                    _gateSlot1 = s + 1;
                    _t[s].GatedImpacts++;
                    _b.GateImpacts++;
                }
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        static void HitPostfix()
        {
            if (_hitDepth <= 0) return;
            _hitDepth--;
            if (_hitDepth == 0) { _linkSlot1 = 0; _gateSlot1 = 0; }
        }

        /// static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo, Single penetration, Boolean forceTopArmorAttack,
        /// ArmorSides armorSide), Priority.First. Inside the impact of a gated missile, the damage of that missile's ammunition becomes 0.
        static void DamagePostfix(EcsEntity target, Ammo ammoInfo, ref float __result)
        {
            if (Campaign.MissionInerte || !_armed) return;                            // mission without the mod: vanilla damage
            if (_hitDepth <= 0 || _depthFrame != _frame) { Interlocked.Increment(ref _dmgOutside); return; }
            Interlocked.Increment(ref _dmgInHit);
            _gatedSerial = 0;                                                    // GatedFor only describes the call in progress
            if (Environment.CurrentManagedThreadId != _mainThread) { Interlocked.Increment(ref _dmgOffMain); return; }
            if (ammoInfo == null) return;
            try
            {
                int id = AmmoId(ammoInfo);
                int irx = IrIndex(id);
                int slot = _linkSlot1 - 1;
                if (irx >= 0 && _hitSerialCur != _countedSerial)
                {
                    _countedSerial = _hitSerialCur;
                    _b.EligImpacts++;
                    if (slot >= 0 && _t[slot].Used && _t[slot].Ammo == id) _b.Linked++;
                }
                if (slot < 0 || !_t[slot].Used) return;
                ref Missile m = ref _t[slot];
                bool sameAmmo = m.Ammo == id;
                float dmg = __result;
                int side = AntiHeliTouches.AircraftSide(target.EntityId);
                if (sameAmmo && side >= 0) m.HitSide = side;
                if (sameAmmo && m.Dropped && side >= 0 && dmg > 0f && !m.AirHitAfterDrop) { m.AirHitAfterDrop = true; _b.AirHitAfterDrop++; }
                if (_gateSlot1 == slot + 1 && EffStage >= StageGate)
                {
                    if (sameAmmo)
                    {
                        __result = 0f;
                        m.GatedCalls++; m.GatedDamage += dmg;
                        _b.GateCalls++; _b.GatedDmg += dmg;
                        if (side == 0) _b.GatedAir0++; else if (side == 1) _b.GatedAir1++; else _b.GatedOther++;
                        _gatedTarget = target.EntityId; _gatedValue = dmg; _gatedSerial = _hitSerialCur;
                    }
                    else if (dmg > 0f) _b.GateLeak++;                            // another ammunition inside a gated impact: not ours to zero
                }
                else if (sameAmmo) { m.UngatedCalls++; m.UngatedDamage += dmg; }
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        /// SeekerSystem :: Void CalculateMissileDivertion(Entity& missile, Boolean forceReroll). Remembers the missile of the outer call.
        static void DivertPrefix(ref EcsEntity missile, bool forceReroll)
        {
            if (Campaign.MissionInerte || !_armed) return;
            if (_rollDepth > 0 && _rollFrameT != _frame) { _rollDepth = 0; Interlocked.Increment(ref _staleRoll); }
            _rollDepth++;
            _rollFrameT = _frame;
            if (_rollDepth != 1) return;
            _rollChance = 0; _rollDecided = false; _rollFlares = false; _rollForce = forceReroll;
            try
            {
                _rollEntity = missile;
                if (_used == 0 || Environment.CurrentManagedThreadId != _mainThread) return;
                int s = Find(missile.EntityId, missile.Version);
                if (s < 0) return;
                _t[s].LastDivertMs = Environment.TickCount64;
                _t[s].DivertCalls++;
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        static void DivertPostfix(bool forceReroll)
        {
            if (_rollDepth > 0) _rollDepth--;
            if (Campaign.MissionInerte || !_armed) return;                            // the depth above is always unwound first
            Interlocked.Increment(ref _divert);
            if (forceReroll) Interlocked.Increment(ref _divertReroll);
            if (Environment.CurrentManagedThreadId != _mainThread) { Interlocked.Increment(ref _divertOffMain); return; }
            if (_rollDepth != 0 || !_hooksFull) return;
            int c = _rollChance;
            _rollChance = 0;
            if (c == 0) _b.H0++;
            else if (c == 1) { _b.H1++; if (_rollFlares) _b.H1F++; }
            else { _b.H2++; if (_rollFlares) _b.H2F++; }
        }

        /// static Single CalculateMissileHitChance(Ammunitions ammoInfo, Single targetECM, Single targetDecoyEffect, Int32 targetActiveDecoys,
        /// Single stressMultiplier). Outside a roll (AI scoring): observed only. Inside a roll, on the main thread: the flare decision.
        static void ChancePostfix(Ammo ammoInfo, float targetECM, float targetDecoyEffect, int targetActiveDecoys, float stressMultiplier, ref float __result)
        {
            if (Campaign.MissionInerte || _inBase || !_armed) return;                // mission without the mod: vanilla chance
            try
            {
                bool inRoll = _rollDepth > 0 && _rollFrameT == _frame;
                Observe(ammoInfo, targetECM, targetDecoyEffect, targetActiveDecoys, stressMultiplier, __result, inRoll ? 1 : 0);
                if (!inRoll || ammoInfo == null) return;
                if (Environment.CurrentManagedThreadId != _mainThread) { Interlocked.Increment(ref _rollOffMain); return; }
                _rollChance++;
                if (targetActiveDecoys > 0) _rollFlares = true;
                // a second call in the same roll gets the same answer, unless decoys show up in it while an infrared missile is still on a hit
                if (_rollChance > 1 && _rollDecided && (targetActiveDecoys <= 0 || _rollForced <= 0f || IrIndex(AmmoId(ammoInfo)) < 0)) { __result = _rollForced; return; }

                long ms = Environment.TickCount64;
                int id = AmmoId(ammoInfo);
                int irx = IrIndex(id);
                int n = targetActiveDecoys;
                float game = __result;
                bool effects = EffStage >= StageGate;

                // 0-decoy base: measured until R5 is proven, then used for radar / laser / command missiles only
                float bse = float.NaN;
                bool wantBase = !_baseOff && !_endScreenOff && (n > 0 ? (!_baseProven || (effects && irx < 0)) : (!_baseProven && _b.ZeroEq + _b.ZeroNe < 50));
                if (wantBase) bse = BaseChance(ammoInfo, targetECM, targetDecoyEffect, stressMultiplier, game, n);

                if (irx < 0)
                {
                    if (n <= 0 || !effects) return;
                    if (_baseProven && !float.IsNaN(bse))
                    {
                        __result = bse < 0f ? 0f : bse > 1f ? 1f : bse;
                        _rollDecided = true; _rollForced = __result;
                        _b.NonIrBase++;
                    }
                    else _b.NonIrBaseSkipped++;
                    return;
                }

                _b.IrRolls++;
                if (n > 0)
                {
                    _b.IrFlareRolls++;
                    float sec = (ms - _battleStartMs) / 1000f;
                    if (Math.Abs(targetDecoyEffect - 0.9f) < 0.005f) { _b.Effect90++; if (_b.FirstEffect90 < 0f) _b.FirstEffect90 = sec; _b.LastEffect90 = sec; }
                    else if (Math.Abs(targetDecoyEffect - 0.65f) < 0.005f) _b.Effect65++;
                    else _b.EffectOther++;
                }
                int s = FindOrCreate(id, irx, ms);
                if (s < 0)
                {
                    _b.TableFull++;
                    if (effects && n > 0) { __result = 0f; _rollDecided = true; _rollForced = 0f; _b.Flare++; }
                    return;
                }
                ref Missile m = ref _t[s];
                m.Rolls++;
                if (n > 0) m.FlareRolls++;
                m.LastChance = game;
                byte decision = DecMeasure;
                float u = -1f;
                if (effects)
                {
                    if (m.State == StFlare) { decision = DecLocked; _b.Locked++; __result = 0f; }
                    else if (n > 0)
                    {
                        if (m.State == StHit) _b.HitToFlare++;
                        m.State = StFlare; m.DecideMs = ms;
                        decision = DecFlare; _b.Flare++; __result = 0f;
                    }
                    else if (m.State == StNone || _rollForce)
                    {
                        u = (float)_rng.NextDouble();
                        if (u < game) { m.State = StHit; decision = DecHit; _b.Hit++; __result = 1f; }
                        else { m.State = StOther; decision = DecOther; _b.Other++; __result = 0f; }
                    }
                    else
                    {
                        decision = DecKept;
                        __result = m.State == StHit ? 1f : 0f;
                        if (m.State == StOther) _b.Locked++;
                    }
                    _rollDecided = true; _rollForced = __result;
                }
                int k = m.Rolls - 1;
                if (k < RollRecs)
                {
                    ref RollRec r = ref _rr[s * RollRecs + k];
                    r.Ms = ms - m.FirstMs; r.N = n; r.Effect = targetDecoyEffect; r.Chance = game; r.Base = bse; r.U = u; r.Decision = decision; r.Force = _rollForce;
                }
            }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        /// The game's chance for the same call with 0 active decoys (re-entrant call, this postfix stays passive through _inBase).
        static float BaseChance(Ammo ammoInfo, float ecm, float effect, float stress, float game, int n)
        {
            float v;
            _inBase = true;
            try { v = BSH.CalculateMissileHitChance(ammoInfo, ecm, effect, 0, stress); }
            catch { _b.BaseErr++; return float.NaN; }
            finally { _inBase = false; }
            if (n > 0)
            {
                _b.BaseCalls++;
                if (v < game - 0.001f) _b.BaseBelow++;
                if (v > 0.0001f) { _b.RatioSum += game / v; _b.RatioN++; }
            }
            else if (Math.Abs(v - game) <= 0.001f) _b.ZeroEq++;
            else _b.ZeroNe++;
            return v;
        }

        // ---------------------------------------------------------------- hooks, 0.22.8 shapes (safety fallback: observation only)
        static void HitPrefixSimple()
        {
            if (Campaign.MissionInerte || !_armed) return;
            if (_hitDepth > 0 && _depthFrame != _frame) { _hitDepth = 0; Interlocked.Increment(ref _staleDepth); }
            _hitDepth++;
            _depthFrame = _frame;
            _hitSerialCur = Interlocked.Increment(ref _hitSerial);
            Interlocked.Increment(ref _hitUpdates);
            Interlocked.Increment(ref _hitTotal);
            if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _hitOffMain);
        }

        static void DamagePostfixSimple()
        {
            if (Campaign.MissionInerte || !_armed) return;
            if (_hitDepth > 0 && _depthFrame == _frame) Interlocked.Increment(ref _dmgInHit); else Interlocked.Increment(ref _dmgOutside);
        }

        static void DivertPrefixSimple()
        {
            if (Campaign.MissionInerte || !_armed) return;
            if (_rollDepth > 0 && _rollFrameT != _frame) { _rollDepth = 0; Interlocked.Increment(ref _staleRoll); }
            _rollDepth++;
            _rollFrameT = _frame;
        }

        static void ChancePostfixSimple(Ammo ammoInfo, float targetECM, float targetDecoyEffect, int targetActiveDecoys, float stressMultiplier, float __result)
        {
            if (Campaign.MissionInerte || _inBase || !_armed) return;
            try { Observe(ammoInfo, targetECM, targetDecoyEffect, targetActiveDecoys, stressMultiplier, __result, _rollDepth > 0 && _rollFrameT == _frame ? 1 : 0); }
            catch { if (Interlocked.Increment(ref _errors) > MaxErrors) _armed = false; }
        }

        // ---------------------------------------------------------------- hot-path helpers (no allocation)
        /// Chance statistics (flare buckets, tracked ground-attack missiles, other ammo in rolls). Counters only.
        static void Observe(Ammo ammoInfo, float targetECM, float targetDecoyEffect, int targetActiveDecoys, float stressMultiplier, float result, int roll)
        {
            Interlocked.Increment(ref _chanceCalls[roll]);
            if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _chanceOffMain);
            long r = (long)(result * 1000f);
            int d = roll * 4 + Math.Clamp(targetActiveDecoys, 0, 3);
            Interlocked.Increment(ref _decoyN[d]);
            Interlocked.Add(ref _decoyChance[d], r);
            Interlocked.Add(ref _decoyEffect[d], (long)(targetDecoyEffect * 1000f));
            if (ammoInfo == null) return;
            int id = AmmoId(ammoInfo);
            int k = Bucket(id);
            if (k < 0) { if (roll == 1) NoteOther(id); return; }
            int i = k * 2 + roll;
            Interlocked.Increment(ref _bN[i]);
            Interlocked.Add(ref _bSum[i], r);
            LowerTo(ref _bMin[i], r);
            RaiseTo(ref _bMax[i], r);
            Interlocked.Add(ref _bEcm[i], (long)(targetECM * 1000f));
            Interlocked.Add(ref _bStress[i], (long)(stressMultiplier * 1000f));
            if (targetActiveDecoys > 0) Interlocked.Increment(ref _bDecoy[i]);
            if (targetECM >= 0.999f && targetActiveDecoys <= 0 && stressMultiplier >= 0.999f)
            {
                Interlocked.Increment(ref _cN[i]);
                Interlocked.Add(ref _cSum[i], r);
                LowerTo(ref _cMin[i], r);
            }
        }

        /// Ammo Id through a tiny per-thread cache keyed by the native row pointer (one interop call on a miss).
        static int AmmoId(Ammo a)
        {
            int gen = _cacheGen;
            if (_cGen != gen) { _cGen = gen; _cp0 = _cp1 = _cp2 = _cp3 = IntPtr.Zero; }
            IntPtr p = a.Pointer;
            if (p == _cp0) return _ci0;
            if (p == _cp1) return _ci1;
            if (p == _cp2) return _ci2;
            if (p == _cp3) return _ci3;
            int id = a.Id;
            switch (_cNext++ & 3)
            {
                case 0: _cp0 = p; _ci0 = id; break;
                case 1: _cp1 = p; _ci1 = id; break;
                case 2: _cp2 = p; _ci2 = id; break;
                default: _cp3 = p; _ci3 = id; break;
            }
            return id;
        }

        static int Bucket(int id) => id switch
        {
            5 => 0, 13 => 1, 52 => 2, 80 => 3, 83 => 4, 123 => 5, 131 => 6, 164 => 7, 270 => 8,
            449 => 9, 456 => 10, 549 => 11, 553 => 12, 555 => 13, 733 => 14, _ => -1
        };

        static void NoteOther(int id)
        {
            if (id == 0) return;
            for (int j = 0; j < _otherId.Length; j++)
            {
                int cur = _otherId[j];
                if (cur == 0) cur = Interlocked.CompareExchange(ref _otherId[j], id, 0);
                if (cur == 0 || cur == id) { Interlocked.Increment(ref _otherN[j]); return; }
            }
            Interlocked.Increment(ref _otherOverflow);
        }

        static void LowerTo(ref long slot, long v)
        {
            long cur;
            while (v < (cur = Interlocked.Read(ref slot)))
                if (Interlocked.CompareExchange(ref slot, v, cur) == cur) return;
        }

        static void RaiseTo(ref long slot, long v)
        {
            long cur;
            while (v > (cur = Interlocked.Read(ref slot)))
                if (Interlocked.CompareExchange(ref slot, v, cur) == cur) return;
        }

        static int Find(int eid, int ver)
        {
            if (_used == 0) return -1;
            for (int i = 0; i < TableSize; i++)
                if (_t[i].Used && _t[i].Eid == eid && _t[i].Ver == ver) return i;
            return -1;
        }

        /// Entry of the missile of the current roll, created at its first roll. -1 when the table is full.
        static int FindOrCreate(int ammoId, int irx, long ms)
        {
            EcsEntity e = _rollEntity;
            int eid = e.EntityId, ver = e.Version;
            int s = Find(eid, ver);
            if (s >= 0)
            {
                if (_t[s].Ammo != ammoId) _b.AmmoMismatch++;
                return s;
            }
            for (int i = 0; i < TableSize; i++)
            {
                if (_t[i].Used) continue;
                _t[i] = new Missile
                {
                    Used = true, Eid = eid, Ver = ver, Ammo = ammoId, Irx = irx, E = e, FirstMs = ms, LastDivertMs = ms,
                    HitSide = -1, RollFrame = _frame, SeenFrame = -1
                };
                for (int k = 0; k < RollRecs; k++) _rr[i * RollRecs + k] = default;
                _used++;
                return i;
            }
            return -1;
        }

        // ---------------------------------------------------------------- main loop helpers (main thread)
        /// Projectile list of the battle: presence of every tracked missile (identity rule R3b and liveness).
        static void Scan(long ms)
        {
            ShellLife life;
            try { life = Obstacles.ShellLifeSystem; } catch { life = null; }
            if (life == null)
            {
                ScanUnavailable();
                if (!_scanWarned && ms - _battleStartMs > 30000) { _scanWarned = true; Log("liste des projectiles en vol indisponible (système non trouvé) : la vie des missiles suivis se lit à leurs appels de guidage"); }
                return;
            }
            EcsEntity[] arr;
            try
            {
                var set = life._set;
                if (set == null) { ScanUnavailable(); return; }
                arr = set.GetEntities().ToArray();
            }
            catch (Exception e)
            {
                ScanUnavailable();
                _b.ScanErrors++;
                if (_b.ScanErrors == 1) Log("lecture des projectiles en vol impossible : " + e.GetBaseException().Message);
                if (_b.ScanErrors >= MaxScanErrors) { _scanOff = true; Log("lecture des projectiles en vol coupée pour cette bataille (trop d'erreurs)"); }
                return;
            }
            _scanAvail = true;
            int frame = _frame;
            for (int i = 0; i < TableSize; i++)
            {
                if (!_t[i].Used) continue;
                ref Missile m = ref _t[i];
                bool found = false;
                for (int j = 0; j < arr.Length; j++)
                    if (arr[j].EntityId == m.Eid && arr[j].Version == m.Ver) { found = true; break; }
                if (found)
                {
                    m.LastSeenMs = ms;
                    m.SeenFrame = frame;
                    if (!m.SeenInScan)
                    {
                        m.SeenInScan = true;
                        if (!m.IdentityCounted) { m.IdentityCounted = true; _b.ScanTotal++; if (frame - m.RollFrame <= 2) _b.ScanSeen++; }
                    }
                }
                else if (!m.IdentityCounted && frame - m.RollFrame > 2) { m.IdentityCounted = true; _b.ScanTotal++; }
            }
        }

        /// No projectile list this frame: missiles rolled meanwhile are left out of the identity rule (neither seen nor missed).
        static void ScanUnavailable()
        {
            _scanAvail = false;
            for (int i = 0; i < TableSize; i++)
                if (_t[i].Used && !_t[i].IdentityCounted) _t[i].IdentityCounted = true;
        }

        /// Fly-off: one DropCurrentTarget per decoyed missile still in flight (stage 2: first 5 of the battle; stage 3: all).
        static void Drops(long ms)
        {
            int frame = _frame;
            for (int i = 0; i < TableSize; i++)
            {
                if (!_t[i].Used) continue;
                ref Missile m = ref _t[i];
                if (m.State != StFlare || m.Dropped || m.DropFailed) continue;
                if (EffStage == StageTrial && _b.Drops >= TrialDrops) return;
                bool live = _scanAvail ? m.SeenFrame == frame : ms - m.LastDivertMs <= 100;
                if (!live) continue;
                EcsEntity e = m.E;
                try
                {
                    if (!e.IsAlive) { m.DropFailed = true; _b.DropSkippedDead++; continue; }
                    if (!_dropPending.Value) { _dropPending.Value = true; MelonPreferences.Save(); }   // once per battle, main thread
                    Seeker.DropCurrentTarget(ref e, false, false, RemoveReason.MissileBeyondSeekerAngles);
                    m.Dropped = true;
                    m.DropMs = ms;
                    _b.Drops++;
                }
                catch (Exception ex)
                {
                    m.DropFailed = true;
                    _b.DropErr++;
                    if (_b.DropErr == 1) Log("décrochage d'un missile leurré impossible : " + ex.GetBaseException().Message);
                    if (_b.DropErr >= MaxDropErrors) { _dropsOff = true; Log("décrochage coupé pour cette bataille (5 erreurs) ; les missiles leurrés restent sans dégâts"); return; }
                }
            }
        }

        static long LastAlive(ref Missile m) => Math.Max(m.FirstMs, Math.Max(m.SeenInScan ? m.LastSeenMs : 0, m.LastDivertMs));

        /// A missile leaves the table when neither the projectile list nor a guidance call has shown it for a while.
        static void Expire(long ms)
        {
            for (int i = 0; i < TableSize; i++)
            {
                if (!_t[i].Used) continue;
                ref Missile m = ref _t[i];
                long quiet = ms - LastAlive(ref m);
                bool gone = m.SeenInScan && _scanAvail ? quiet >= AliveSeenMs : quiet >= AliveUnseenMs;
                if (gone || ms - m.FirstMs >= MaxAgeMs) Finalize(i, ms);
            }
        }

        static void FinalizeAll(long ms)
        {
            for (int i = 0; i < TableSize; i++) if (_t[i].Used) Finalize(i, ms);
        }

        static void ClearTable()
        {
            for (int i = 0; i < TableSize; i++) _t[i].Used = false;
            _used = 0;
        }

        static void Finalize(int i, long ms)
        {
            ref Missile m = ref _t[i];
            if (!m.Used) return;
            var b = _b;
            long end = LastAlive(ref m);
            b.Missiles++;
            b.FlightSeconds += (end - m.FirstMs) / 1000.0;
            b.DivertCallsTracked += m.DivertCalls;
            if (m.Impacts == 0) b.LeftNoImpact++;
            if (m.Irx >= 0 && m.Irx < NIr)
            {
                b.AmmoMissiles[m.Irx]++;
                b.AmmoLastChance[m.Irx] += (long)(m.LastChance * 1000f);
                if (m.UngatedDamage > 0f) b.AmmoDamaged[m.Irx]++;
            }
            if (EffStage >= StageGate && m.State == StHit) { b.ForcedHit++; if (m.Impacts > 0) b.ForcedHitImpact++; }
            if (m.Dropped)
            {
                b.DroppedFinal++;
                if (!m.AirHitAfterDrop && end - m.DropMs <= DropLeaveMs) b.DroppedLeftOk++;
            }
            if (m.State == StFlare || m.FlareRolls > 0) _lastFlareGoneMs = ms;
            if (b.MissileLines < MaxMissileLines) { b.MissileLines++; Log(MissileLine(i, end)); }
            else b.MissileLinesSuppressed++;
            m.Used = false;
            _used--;
        }

        static string MissileLine(int i, long end)
        {
            ref Missile m = ref _t[i];
            var sb = new StringBuilder($"missile {m.Eid} {AmmoName(m.Ammo)} ({m.Ammo}) :");
            int nrec = Math.Min(m.Rolls, RollRecs);
            for (int k = 0; k < nrec; k++)
            {
                ref RollRec r = ref _rr[i * RollRecs + k];
                sb.Append(k == 0 ? " " : " ; ").Append($"+{Sec(r.Ms)}s {r.N} leurre{(r.N > 1 ? "s" : "")}");
                if (r.N > 0) sb.Append($" (effet jeu {F2(r.Effect)})");
                sb.Append($" chance jeu {F2(r.Chance)}");
                if (!float.IsNaN(r.Base)) sb.Append($" base {F2(r.Base)}");
                if (r.U >= 0f) sb.Append($" tirage {F2(r.U)}");
                if (r.Force) sb.Append(" (nouveau tirage forcé)");
                sb.Append(" -> ").Append(DecisionName(r.Decision));
            }
            if (m.Rolls > RollRecs) sb.Append($" ; +{m.Rolls - RollRecs} autre(s) tirage(s)");
            if (m.Dropped) sb.Append($" ; décroché +{Sec(m.DropMs - m.FirstMs)}s");
            else if (m.DropFailed) sb.Append(" ; décrochage impossible");
            sb.Append($" ; fin +{Sec(end - m.FirstMs)}s, impacts {m.Impacts} (neutralisés {m.GatedImpacts}), dégâts annulés {F2(m.GatedDamage)} ({m.GatedCalls} calcul(s)), dégâts faits {F2(m.UngatedDamage)}");
            if (m.HitSide >= 0) sb.Append($", appareil du camp {m.HitSide} atteint");
            if (m.AirHitAfterDrop) sb.Append(", appareil atteint après le décrochage");
            sb.Append($", appels de guidage {m.DivertCalls}{(m.SeenInScan ? "" : ", jamais vu dans la liste des projectiles")}");
            return sb.ToString();
        }

        static string DecisionName(byte d) => d switch
        {
            DecHit => "touché", DecFlare => "leurré", DecOther => "raté", DecLocked => "reste leurré", DecKept => "état gardé", _ => "mesure"
        };

        static string Sec(long ms) => (ms / 1000.0).ToString("0.0", Inv);
        static string F2(float v) => float.IsNaN(v) ? "?" : v.ToString("0.00", Inv);
        static string F3(long sum, long n) => n == 0 ? "-" : (sum / (double)n / 1000.0).ToString("0.00", Inv);
        static string Milli(long v) => (v / 1000.0).ToString("0.00", Inv);
        static string Pct(long a, long n) => n <= 0 ? "-" : (100.0 * a / n).ToString("0.#", Inv) + " %";

        // ---------------------------------------------------------------- combat watchdog
        /// Once per second (Frame): advances the running game clock, then every 5 s of that clock checks impacts against "enemies within
        /// 1 km" over the last 3 minutes. The clock stops while the game is paused or frozen and while the end screen is up, so those spans
        /// never count as silence, and a window never spans the end screen. A silent window trips only when the silence can be blamed on
        /// the module: it acted in that window or in the 90 s before it (rounds already in flight land first), or the battle has been silent
        /// since it began (the v0.22.7 pattern). A trip switches flare effects, base call and fly-off off for this battle only; Judge then
        /// only notes whether fire came back fast (three such battles in this game session: off until the game is closed). Any other lull is
        /// logged once.
        static void Watchdog(float now)
        {
            if (!_hooksFull || _effectsOffSession) return;                   // nothing left to switch off
            long imp = Interlocked.Read(ref _hitTotal);
            float contact = AntiHeliTouches.ContactMeters;
            bool running = Tick(imp, contact, out bool endScreen, out long landed);
            var b = _b;
            // everything the module does besides observing: forced decisions, base chance applied, gate, drops, stage-0 re-entrant base calls
            long act = b.Flare + b.Hit + b.Other + b.Locked + b.NonIrBase + b.GateCalls + b.Drops + b.BaseCalls + b.ZeroEq + b.ZeroNe + b.BaseErr;
            if (act != _wActLast) { _wActLast = act; _wActRun = _runClock; }
            if (endScreen)
            {
                _wCount = 0;                                                 // a window never spans the end screen
                if (_tripped && !_judged) { _judged = true; Log("chien de garde : écran de fin de bataille affiché avant le verdict sur la coupure : rien n'est compté"); }
            }
            if (!running) return;
            if (_tripped) { _resumeImp += landed; Judge(); return; }        // impacts of paused ticks never count as fire coming back
            if (_runClock < _nextWatch) return;
            _nextWatch = _runClock + WatchEvery;
            if (contact < 0f)
            {
                if (!_contactWarned && now - _battleStartReal > 60f) { _contactWarned = true; Log("chien de garde : distance entre les camps inconnue (mesure des hélicos coupée), surveillance en pause"); }
                return;
            }
            _wIdx = (_wIdx + 1) % WatchSamples;
            _wContact[_wIdx] = contact <= WatchContact;
            _wHits[_wIdx] = imp;
            _wAct[_wIdx] = act;
            _wRun[_wIdx] = _runClock;
            _wDist[_wIdx] = contact;
            if (_wCount < WatchSamples) _wCount++;
            if (_wCount < WatchSamples) return;
            for (int i = 0; i < WatchSamples; i++) if (!_wContact[i]) return;
            int first = (_wIdx + 1) % WatchSamples;
            long oldest = _wHits[first];
            if (imp - oldest > 2) return;
            long acted = act - _wAct[first];
            float from = _wRun[first] - WatchLookback;
            bool recent = _wActRun >= from;                                  // the module acted in the window or in the 90 s before it
            bool fromStart = oldest < WatchStartImpacts;
            if (acted == 0 && !recent && !fromStart)
            {
                // the same hooks already carried combat in this battle and the module changed nothing in these 3 minutes nor just before: an ordinary lull
                if (!_lullLogged) { _lullLogged = true; Log($"chien de garde : 3 minutes de jeu sans impact avec les deux camps à moins de {WatchContact.ToString("0", Inv)} m, mais les leurres n'ont rien changé pendant ce temps ni dans les 90 secondes d'avant et le combat passait avant : simple accalmie, rien n'est coupé"); }
                return;
            }
            // the same distance over the last minute: positions may be frozen (a pause the game did not report), so fire coming back proves nothing
            bool frozen = true;
            for (int k = 1; k < FrozenSamples; k++) if (_wDist[(_wIdx - k + WatchSamples) % WatchSamples] != contact) { frozen = false; break; }
            Trip(acted != 0 || recent, fromStart, frozen);
        }

        /// Advances the running game clock by the real time since the last tick (Time.time, at most 2 s) times the session game speed
        /// (GameSessionContext.TimeScale when readable and 0 < TimeScale <= 16, otherwise 1), unless the game is paused, frozen or on the end
        /// screen. Time.time keeps running while this game is paused (timeScale stays 1), so the pause is read from Unity's time scale, the
        /// session's own pause flag and speed (trusted once seen unpaused in this battle) and the game clock GameController.GameTime (trusted
        /// once seen advancing). A signal that claims a pause on two ticks in a row is ignored for the rest of the battle as soon as an impact
        /// lands or the distance between the sides changes while it holds (Claim). The end screen is not tested that way, since a battle may
        /// go on behind it: the flare effects and the base call stay off while Campaign.BattleOver is set (_endScreenOff), so a stale flag
        /// that stops the clock can never leave them running unwatched. Main thread, once per second.
        static bool Tick(long imp, float contact, out bool endScreen, out long landed)
        {
            endScreen = Campaign.BattleOver;
            _endScreenOff = endScreen;
            float t;
            try { t = UnityEngine.Time.time; } catch { t = _lastTickT; }
            float dt = _lastTickT < 0f ? 0f : Math.Clamp(t - _lastTickT, 0f, TickMax);
            _lastTickT = t;
            landed = imp - _lastTickImp;
            _lastTickImp = imp;

            bool unity = false, sess = false, gtFrozen = false;
            float speed = 1f;
            try { unity = UnityEngine.Time.timeScale <= 0f; } catch { }
            var gc = GameController._instance;
            if (gc != null)
            {
                try
                {
                    var s = gc._GameSession_k__BackingField;
                    if (s != null)
                    {
                        float ts = s.TimeScale;
                        sess = s.IsPaused || ts <= 0f;
                        if (!sess) _sessSeenRun = true;
                        if (ts > 0f && ts <= MaxSpeed) speed = ts;
                    }
                }
                catch { sess = false; speed = 1f; }
                try
                {
                    float g = gc.GameTime;
                    if (_gtSeen && g > _lastGameTime) _gtLive = true;
                    gtFrozen = _gtSeen && _gtLive && g == _lastGameTime;
                    _lastGameTime = g;
                    _gtSeen = true;
                }
                catch { gtFrozen = false; }
            }
            _speed = speed;

            // non-short-circuit: every signal keeps its own two-tick state
            bool paused = Claim(unity, landed, contact, ref _unityPrev, ref _unityClaimDist, ref _unityDistrust, "l'échelle de temps de Unity indique une pause")
                        | Claim(sess && _sessSeenRun, landed, contact, ref _sessPrev, ref _sessClaimDist, ref _sessDistrust, "la partie indique une pause")
                        | Claim(gtFrozen, landed, contact, ref _gtPrev, ref _gtClaimDist, ref _gtDistrust, "l'horloge de la partie est arrêtée");
            if (paused || endScreen) { _frozenSec += dt; return false; }
            _runClock += dt * speed;
            return true;
        }

        /// One pause signal on this tick: true while it claims a pause and is still trusted. From the second tick in a row of a claim, any
        /// impact landing, or a change in the distance between the sides since the claim started (both values known), shows the combat goes
        /// on: the signal is ignored for the rest of the battle, with one log line. No allocation unless it logs.
        static bool Claim(bool claims, long landed, float contact, ref bool prev, ref float claimDist, ref bool distrust, string what)
        {
            if (!claims) { prev = false; return false; }
            if (!prev) { prev = true; claimDist = contact; }
            else if (!distrust)
            {
                if (landed > 0 || (contact >= 0f && claimDist >= 0f && contact != claimDist))
                {
                    distrust = true;
                    Log($"chien de garde : {what} alors que le combat continue (tirs ou unités qui bougent) : indication ignorée pour cette bataille");
                }
                else if (claimDist < 0f) claimDist = contact;
            }
            return !distrust;
        }

        /// Silent combat blamed on the module: flare effects (forced decisions, gate), fly-off and the re-entrant base call off at once, for
        /// this battle only (ArmBattle puts them back unless the session flags are set). Nothing is written.
        static void Trip(bool blamed, bool fromStart, bool frozen)
        {
            _tripped = true;
            _effectsOff = true;
            _baseOff = true;
            _dropsOff = true;
            _judged = false; _resumeImp = 0;
            _tripRun = _runClock; _tripBlamed = blamed; _tripFrozen = frozen;
            Mod.Log.Warning($"[LEURRES] chien de garde : aucun impact depuis 3 minutes de jeu alors que les deux camps sont à moins de {WatchContact.ToString("0", Inv)} m (" +
                (blamed ? "les leurres ont agi pendant ce temps ou juste avant" : "bataille silencieuse depuis le début") +
                (_frozenSec > 0f ? $", {_frozenSec.ToString("0", Inv)} s de pause ou d'écran de fin non comptées" : "") +
                (_speed != 1f ? $", vitesse du jeu x{_speed.ToString("0.##", Inv)}" : "") +
                (frozen ? ", distance entre les camps inchangée depuis 1 minute (pause possible)" : "") +
                ") ; effets des leurres, calcul de chance sans leurre et décrochage coupés pour cette bataille seulement ; rien n'est enregistré");
            Mod.Notify(TxtKey.N_FLARES_OFF_BATTLE);
        }

        /// Main thread, every running tick after a trip until the verdict. 3 impacts or more on running ticks within 30 s of running time
        /// after the switch-off, when the module had acted and positions were not frozen before the trip, marks this battle as a fast resume.
        /// One such battle proves nothing (a lull may end, or another module's own switch-off may free the shots it was holding at the same
        /// moment): only the third one in the same game session switches everything off until the game is closed (FastResume). Pauses and
        /// the end screen stop this clock; the end screen before the verdict counts nothing (Watchdog). Nothing is written.
        static void Judge()
        {
            if (_judged) return;
            float since = _runClock - _tripRun;
            if (_resumeImp >= ResumeImpacts)
            {
                _judged = true;
                string s = since.ToString("0", Inv);
                if (!_tripBlamed) Log($"chien de garde : les tirs ont repris {s} s après la coupure, mais les leurres n'avaient rien fait : accalmie, rien n'est compté");
                else if (_tripFrozen) Log($"chien de garde : les tirs ont repris {s} s après la coupure, mais les unités ne bougeaient plus avant (pause probable) : rien n'est compté");
                else FastResume(s);
                return;
            }
            if (since <= JudgeResume) return;
            _judged = true;
            Log("chien de garde : aucun tir n'a repris dans les 30 secondes de jeu après la coupure : accalmie ou blocage durable ; leurres coupés pour cette bataille seulement, rien n'est compté");
        }

        /// A fast-resume battle, counted for this game session only (never saved). The third one keeps the flare effects, the base call and
        /// the fly-off off until the game is closed.
        static void FastResume(string since)
        {
            _fastResumes++;
            if (_fastResumes < SessionResumes)
            {
                Log($"chien de garde : les tirs ont repris {since} s après la coupure des leurres : bataille notée ({_fastResumes}/{SessionResumes} dans cette session de jeu, rien n'est enregistré) ; " +
                    "une fois seule ne prouve rien (fin d'accalmie, ou autre sécurité du mod qui libère les tirs au même moment) ; leurres coupés pour cette bataille seulement");
                return;
            }
            OffForSession();
            Mod.Log.Warning($"[LEURRES] chien de garde : les tirs ont repris {since} s après la coupure des leurres, pour la {SessionResumes}e bataille de cette session de jeu : " +
                "effets des leurres, calcul de chance sans leurre et décrochage coupés jusqu'à la fermeture du jeu (rien n'est enregistré : tout revient au prochain lancement)");
            Mod.Notify(TxtKey.N_FLARES_OFF_SESSION);
        }

        // ---------------------------------------------------------------- proof rules and stage switch (battle end)
        static string StageName(int s) => s switch
        {
            StageMeasure => "0 (sécurité : leurres classiques du jeu, mesure seulement)",
            StageGate => "1 (missile infrarouge face aux leurres : toujours raté, sans dégâts ; pas encore de décrochage)",
            StageTrial => "2 (missile infrarouge face aux leurres : toujours raté, sans dégâts ; essai du décrochage sur 5 missiles)",
            _ => "3 (missile infrarouge face aux leurres : toujours raté, sans dégâts, et décroche)"
        };

        static Dictionary<string, long> LoadProofs()
        {
            var d = new Dictionary<string, long>();
            foreach (var part in (_proofPref?.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && long.TryParse(part.Substring(eq + 1), NumberStyles.Integer, Inv, out long v)) d[part.Substring(0, eq)] = v;
            }
            return d;
        }

        static void SaveProofs(Dictionary<string, long> d)
        {
            var keys = new List<string>(d.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var k in keys) { if (sb.Length > 0) sb.Append(';'); sb.Append(k).Append('=').Append(d[k].ToString(Inv)); }
            _proofPref.Value = sb.ToString();
        }

        static long P(Dictionary<string, long> d, string k) => d.TryGetValue(k, out var v) ? v : 0;
        static void Add(Dictionary<string, long> d, string k, long v) => d[k] = P(d, k) + v;
        static void ResetKeys(Dictionary<string, long> d, string prefix) { foreach (var k in new List<string>(d.Keys)) if (k.StartsWith(prefix, StringComparison.Ordinal)) d.Remove(k); }
        static string OkKo(bool ok) => ok ? "réussie" : "pas encore";

        static void EvaluateStage(string why, bool normal, long errors, long offMain, AntiHeliTouches.AirProof air)
        {
            // stage 1 is the floor: the proof rules below only move the fly-off, never the infrared decision or the damage gate
            int stage = Math.Clamp(_stagePref.Value, StageGate, StageFull);
            var p = LoadProofs();
            var b = _b;
            var sb = new StringBuilder($"preuves (étape {stage}, {why}, bataille {(normal ? "terminée normalement" : _tripped ? "coupée par le chien de garde" : "trop courte ou interrompue")}) :");
            if (_simpleHooks || !_hooksFull || _effectsOffSession)
            {
                Log(sb + $" crochets {(_simpleHooks ? "simples de sécurité" : _effectsOffSession ? "coupés par le chien de garde jusqu'à la fermeture du jeu" : $"incomplets ({_hooksInstalled}/4)")} : rien n'est compté ; étape suivante : {StageName(stage)}");
                return;
            }

            // R1 (every battle): hooks, errors, calls off the main thread
            bool r1 = _hooksInstalled == 4 && errors == 0 && offMain == 0;
            sb.Append($" R1 crochets {_hooksInstalled}/4, erreurs {errors}, hors fil principal {offMain} : {(r1 ? "réussie" : "échouée")} ;");

            // R5 (independent): the 0-decoy base call
            bool r5Fail = b.BaseErr > 0 || b.BaseBelow > 0 || b.ZeroNe > 0;
            if (r5Fail) { p["base"] = 0; p["r5n"] = 0; p["r5z"] = 0; }
            else if (normal && r1) { Add(p, "r5n", b.BaseCalls); Add(p, "r5z", b.ZeroEq); }
            if (!r5Fail && P(p, "r5n") >= 10 && P(p, "r5z") >= 10) p["base"] = 1;
            sb.Append($" R5 chance sans leurre {(P(p, "base") == 1 ? "prouvée" : r5Fail ? "échouée (compteurs remis à zéro)" : "pas encore")} ({P(p, "r5n")}/10 appels avec leurres, {P(p, "r5z")}/10 égalités sans leurre ; cette bataille : erreurs {b.BaseErr}, sous la chance du jeu {b.BaseBelow}, écarts {b.ZeroNe}" +
                (b.RatioN > 0 ? $", rapport chance jeu/base moyen {(b.RatioSum / b.RatioN).ToString("0.00", Inv)}" : "") + ") ;");

            // R3b (independent): tracked missiles seen in the projectile list within 2 frames
            if (b.ScanTotal >= 20 && b.ScanSeen * 100 < b.ScanTotal * 98) { p["scan"] = 0; p["r3t"] = 0; p["r3s"] = 0; }
            else if (normal && r1) { Add(p, "r3t", b.ScanTotal); Add(p, "r3s", b.ScanSeen); }
            if (P(p, "r3t") >= 50 && P(p, "r3s") * 100 < P(p, "r3t") * 98) { p["scan"] = 0; p["r3t"] = 0; p["r3s"] = 0; }
            if (P(p, "r3t") >= 20 && P(p, "r3s") * 100 >= P(p, "r3t") * 98) p["scan"] = 1;
            sb.Append($" R3b missiles vus dans les projectiles {P(p, "r3s")}/{P(p, "r3t")} (98 % sur 20 au moins) : {(P(p, "scan") == 1 ? "prouvée" : "pas encore")} ;");

            int next = stage;
            string reason = null;
            bool linkedLow = b.EligImpacts >= 10 && b.Linked * 100 < b.EligImpacts * 90;
            sb.Append($" contrôle : tirages infrarouges {b.IrRolls} (avec leurres {b.IrFlareRolls}), leurrés {b.Flare}, tirages avec 2 calculs de chance ou plus {b.H2}, baisses de santé juste après un impact neutralisé {air.DropsAfterGate}, fuites dans un impact neutralisé {b.GateLeak}, impacts reliés {b.Linked}/{b.EligImpacts}, contrôle de santé {(air.Running ? "actif" : "indisponible")} ;");
            if (!r1 || air.DropsAfterGate > 0 || b.GateLeak > 0 || linkedLow)
            {
                // the decision and the gate stay on: only the fly-off steps back, and its proof starts again
                next = StageGate;
                ResetKeys(p, "s1"); ResetKeys(p, "s2");
                reason = (!r1 ? "R1 échouée" : air.DropsAfterGate > 0 ? "ALERTE santé d'un appareil en baisse juste après un impact neutralisé" : b.GateLeak > 0 ? "dégâts d'une autre munition dans un impact neutralisé" : "moins de 90 % des impacts reliés à leur missile") +
                    " : décrochage retiré ou remis à plus tard, preuves du décrochage recommencées ; les missiles infrarouges leurrés ratent toujours sans dégâts";
            }
            else if (!air.Running) reason = "contrôle de santé des appareils indisponible : rien n'est compté";
            else if (stage == StageGate)
            {
                if (air.DropsNoHitInDecoyWindow > 0) { ResetKeys(p, "s1"); reason = $"{air.DropsNoHitInDecoyWindow} baisse(s) de santé sans impact pendant un leurrage : compteurs de l'étape 1 remis à zéro"; }
                else if (normal) { Add(p, "s1f", b.Flare); Add(p, "s1h", b.ForcedHit); Add(p, "s1hi", b.ForcedHitImpact); Add(p, "s1g", b.GateImpacts); }
                bool a = P(p, "s1f") >= 10;
                bool c = P(p, "s1h") >= 10 && P(p, "s1hi") * 2 >= P(p, "s1h");
                bool sc = P(p, "scan") == 1;
                bool noCrash = P(p, "volcrash") == 0;                      // a fly-off trial interrupted by a game stop is never retried in this version
                sb.Append($" leurrés {P(p, "s1f")}/10 : {OkKo(a)} ; touchés forcés suivis d'un impact {P(p, "s1hi")}/{P(p, "s1h")} (10 au moins, 50 %) : {OkKo(c)} ; impacts neutralisés {P(p, "s1g")} ; baisses de santé sans impact pendant un leurrage {air.DropsNoHitInDecoyWindow} (0 exigé) ; identité dans les projectiles {(sc ? "prouvée" : "pas encore")} ;");
                sb.Append($" décrochage {(noCrash ? "autorisé" : "bloqué (partie interrompue pendant l'essai)")} ;");
                if (a && c && sc && noCrash && air.DropsNoHitInDecoyWindow == 0) { next = StageTrial; reason = "leurres et neutralisation des dégâts prouvés"; }
            }
            else if (stage == StageTrial)
            {
                if (b.DropErr > 0) { next = StageGate; reason = $"{b.DropErr} erreur(s) de décrochage"; }
                else
                {
                    if (normal) { Add(p, "s2d", b.DroppedFinal); Add(p, "s2ok", b.DroppedLeftOk); }
                    sb.Append($" décrochés {P(p, "s2d")}/{TrialDrops}, partis en moins de 20 s sans toucher un appareil {P(p, "s2ok")} (80 % exigés) ;");
                    if (P(p, "s2d") >= TrialDrops)
                    {
                        bool good = P(p, "s2ok") * 10 >= P(p, "s2d") * 8;
                        next = good ? StageFull : StageGate;
                        reason = good ? "décrochage prouvé" : "trop de missiles décrochés restent en vol ou touchent un appareil";
                    }
                }
            }
            else
            {
                sb.Append($" décrochés {b.DroppedFinal}, partis en moins de 20 s sans toucher un appareil {b.DroppedLeftOk}, erreurs {b.DropErr} ;");
                if (b.DropErr >= MaxDropErrors || (b.DroppedFinal >= 5 && b.DroppedLeftOk * 2 < b.DroppedFinal))
                {
                    next = StageGate;
                    reason = b.DropErr >= MaxDropErrors ? "erreurs de décrochage répétées" : "décrochage moins fiable qu'à l'essai";
                }
            }

            if (next != stage)
            {
                ResetKeys(p, "s0"); ResetKeys(p, "s1"); ResetKeys(p, "s2");
                _stagePref.Value = next;
                Mod.Notify(next switch
                {
                    StageTrial => TxtKey.N_FLARES_TRIAL_NEXT,
                    StageFull => TxtKey.N_FLARES_DROP_CONFIRMED,
                    _ => TxtKey.N_FLARES_DROP_OFF
                });
            }
            else if (_stagePref.Value != stage) _stagePref.Value = stage;          // a stored value outside the ladder
            SaveProofs(p);
            _baseProven = P(p, "base") == 1;
            Log(sb + (reason != null ? $" {reason} ;" : "") + $" étape suivante : {StageName(next)}");
        }

        // ---------------------------------------------------------------- battle-start checks and measurements (main thread)
        /// Infrared rows: present, seeker, proximity fuse (a fuse of 0 m also gates the plain misses of that missile).
        static void CheckRows()
        {
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return;
            _rowsChecked = true;
            var missing = new List<int>();
            var seekers = new Dictionary<string, List<int>>();
            var fuse0 = new List<int>();
            for (int k = 0; k < NIr; k++)
            {
                int id = IrIds[k];
                _fuse0[k] = false;
                Ammo row = null;
                try { if (!src.Ammunitions.TryGetById(id, out row)) row = null; } catch { row = null; }
                if (row == null) { missing.Add(id); continue; }
                string sk = "?";
                try { sk = row.Seeker.ToString(); } catch { }
                if (!seekers.TryGetValue(sk, out var l)) seekers[sk] = l = new List<int>();
                l.Add(id);
                try { _fuse0[k] = !(row.RadioFuseDistance > 0f); } catch { _fuse0[k] = false; }
                if (_fuse0[k]) fuse0.Add(id);
            }
            var sb = new StringBuilder($"missiles infrarouges : {NIr - missing.Count}/{NIr} lignes trouvées ; autodirecteurs :");
            foreach (var kv in seekers) sb.Append($" {kv.Key} {kv.Value.Count} ({string.Join(", ", kv.Value)})");
            sb.Append(fuse0.Count > 0 ? $" ; fusée de proximité à 0 m (un raté ne fait pas de dégâts) : {string.Join(", ", fuse0)}" : " ; aucune fusée de proximité à 0 m");
            if (missing.Count > 0) sb.Append($" ; absentes : {string.Join(", ", missing)}");
            Log(sb.ToString());
        }

        /// Engine formula grid, once per database: the chance of each infrared row plus a few radar/command rows with decoy effects
        /// 0.65 / 0.9 / 1 and 0 to 3 active decoys, and CalculateCountermeasuresEffect. Direct calls on the main thread, never counted.
        static void FormulaGrid()
        {
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return;
            _gridDone = true;
            if (src.Pointer == _gridSrc) return;
            _gridSrc = src.Pointer;
            float[] effects = { 0.65f, 0.9f, 1f };
            var ids = new List<int>(IrIds);
            ids.AddRange(GridOtherIds);
            int fails = 0;
            foreach (int id in ids)
            {
                Ammo row = null;
                try { if (!src.Ammunitions.TryGetById(id, out row)) row = null; } catch { row = null; }
                if (row == null) continue;
                string sk = "?";
                try { sk = row.Seeker.ToString(); } catch { }
                var sb = new StringBuilder($"grille du jeu : {AmmoName(id)} ({id}, {sk}, {(IrIndex(id) >= 0 ? "infrarouge" : "radar/laser/commande")}) chance pour 0/1/2/3 leurres :");
                foreach (float e in effects)
                {
                    sb.Append($" effet {F2(e)} =");
                    for (int k = 0; k <= 3; k++) { float v = RawChance(row, 1f, e, k, 1f); if (float.IsNaN(v)) fails++; sb.Append(k == 0 ? " " : "/").Append(F2(v)); }
                    sb.Append(" ;");
                }
                sb.Append($" ECM 0.8 et stress 0.85, effet 0.65, 1 leurre = {F2(RawChance(row, 0.8f, 0.65f, 1, 0.85f))}");
                Log(sb.ToString());
                if (fails > 20) { Log("grille du jeu : appels en échec, arrêt"); return; }
            }
            float[] resist = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            foreach (float e in new[] { 0.65f, 0.9f })
            {
                var sb = new StringBuilder($"grille du jeu : effet des leurres (CalculateCountermeasuresEffect) multiplicateur {F2(e)}, résistance du missile r, 0/1/2/3 leurres :");
                foreach (float r in resist)
                {
                    sb.Append($" r {F2(r)} =");
                    for (int k = 0; k <= 3; k++)
                    {
                        float v = float.NaN;
                        try { v = BSH.CalculateCountermeasuresEffect(e, r, k); } catch { }
                        sb.Append(k == 0 ? " " : "/").Append(F2(v));
                    }
                    sb.Append(" ;");
                }
                Log(sb.ToString());
            }
        }

        static float RawChance(Ammo row, float ecm, float effect, int n, float stress)
        {
            _inBase = true;
            try { return BSH.CalculateMissileHitChance(row, ecm, effect, n, stress); }
            catch { return float.NaN; }
            finally { _inBase = false; }
        }

        /// Aircraft on the map (both sides) with the decoy values of their abilities (database table rows), once per battle: explains the
        /// decoy effect 0.90 seen next to the table value 0.65.
        static void LogAircraft(float now)
        {
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return;
            _map ??= new LuaMap();
            var groups = new Dictionary<(int unitId, int side), (string name, int count)>();
            for (int side = 0; side < 2; side++)
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LuaUnit> arr = null;
                try { arr = _map.GetUnits(V3.zero, 1_000_000f, side, -1); } catch { }
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = arr[i];
                        if (u == null || !u.IsAlive()) continue;
                        int unitId = u.SpawnData?.Unit?.UnitID ?? 0;
                        if (unitId <= 0 || !src.Units.TryGetById(unitId, out UnitsRow row) || row == null) continue;
                        if (((int)row.Type & (8 | 16)) == 0) continue;
                        var key = (unitId, side);
                        groups[key] = groups.TryGetValue(key, out var g) ? (g.name, g.count + 1) : (u.Name ?? row.Name ?? "?", 1);
                    }
                    catch { }
                }
            }
            if (groups.Count == 0) { if (now - _battleStartReal >= 120f) _aircraftLogged = true; return; }
            _aircraftLogged = true;
            var abil = new Dictionary<int, List<int>>();
            foreach (var ua in Props.Rows(src.UnitAbilities.GetAll()))
            {
                if (ua == null) continue;
                if (!abil.TryGetValue(ua.UnitId, out var l)) abil[ua.UnitId] = l = new List<int>();
                l.Add(ua.AbilityId);
            }
            var parts = new List<string>();
            foreach (var kv in groups)
            {
                if (parts.Count >= 30) { parts.Add($"et {groups.Count - 30} autre(s)"); break; }
                var sb = new StringBuilder($"{kv.Value.name} (camp {kv.Key.side}, x{kv.Value.count}) :");
                bool any = false;
                if (abil.TryGetValue(kv.Key.unitId, out var list))
                    foreach (int aid in list)
                    {
                        try
                        {
                            if (!src.Abilities.TryGetById(aid, out var a) || a == null || a.DecoyQuantity <= 0) continue;
                            any = true;
                            sb.Append($" capacité {aid} {a.DecoyQuantity} leurres effet {F2(a.DecoyAccuracyMultiplier)} durée {F2(a.DecoyDuration)} s recharge {F2(a.DecoyCooldown)} s");
                        }
                        catch { }
                    }
                if (!any) sb.Append(" aucune capacité de leurres dans la table");
                parts.Add(sb.ToString());
            }
            string eff = _b.Effect90 > 0 ? $" ; effet 0.90 vu {_b.Effect90} fois (de +{_b.FirstEffect90:0} s à +{_b.LastEffect90:0} s), 0.65 {_b.Effect65} fois" : "";
            Log("appareils sur la carte et leurs leurres (table) : " + string.Join(" | ", parts) + eff);
        }

        static void LogSettings()
        {
            var sb = new StringBuilder("réglages du jeu :");
            BattleSystemSettings bs = null;
            try
            {
                bs = GameConfig.Instance?.BattleSystemSettings;
                if (bs != null)
                {
                    foreach (var n in new[] { "FORCE_MISSILE_MISS_DAMAGE", "MINIMAL_MISSILE_MISS_DISTANCE", "RADIUFUSE_MISS_DISTANCE_PROPORTION_MIN", "RADIUFUSE_MISS_DISTANCE_PROPORTION_MAX",
                                              "MISSILE_MISS_ADDITIONAL_CLEARENCE", "MAX_MISSILE_MISS_DISTANCE_PROPORTION", "MAX_MISSILE_MISS_DISTANCE_CAP", "MAX_FLARES_COUNT_MISSILE_INITIAL", "MAX_FLARES_COUNT_MISSILE_REROLL",
                                              "MISSILE_NO_TARGET_SUPPORT_TIME", "MISSILE_NO_TARGET_SUPPORT_TIME_TG", "ACTIVE_APS_IGNORES_ONE_MISSILE",
                                              "SAM_MISSILE_EXPLOSION_CHANGE_ON_TARGET_LOSS", "MISSILE_DEAD_TRAJECTORY_MINIMAL_TIME", "MISSILE_DEAD_TRAJECTORY_REROLL_TIME_MIN",
                                              "MISSILE_DEAD_TRAJECTORY_REROLL_TIME_MAX", "MISSILE_DEATH_SPEED_PROPORTION", "RADIOFUSE_RADNOM_TARGET_DISTANCE_TRESHOLD_MULTIPLIER",
                                              "AUTOFLARES_HELICOPTERS_DELAY", "AUTOFLARES_PLANES_DELAY" })
                    {
                        try { sb.Append($" {n}={bs.GetType().GetProperty(n)?.GetValue(bs)}"); } catch { sb.Append($" {n}=?"); }
                    }
                }
            }
            catch (Exception e) { sb.Append(" BattleSystemSettings illisible (" + e.Message + ")"); }
            try { sb.Append($" RADIOFUSE_PREFILTER_MULTIPLIER={HitDet.RADIOFUSE_PREFILTER_MULTIPLIER} RADIOFUSE_EXPLSOION_THRESHOLD={HitDet.RADIOFUSE_EXPLSOION_THRESHOLD}"); } catch { }
            try { sb.Append($" MISSILE_MISS_RADIOFUSE_TRIGGER_CHANCE={BsConst.MISSILE_MISS_RADIOFUSE_TRIGGER_CHANCE}"); } catch { }
            try { sb.Append($" NO_TARGET_MIN_DELETE_TIME={ShellLife.NO_TARGET_MIN_DELETE_TIME} NET_MISSILES_FREEZE_CHECKTIME={ShellLife.NET_MISSILES_FREEZE_CHECKTIME}"); } catch { }
            try { sb.Append($" TARGET_CHECK_PERIOD={Seeker.TARGET_CHECK_PERIOD}"); } catch { }
            try
            {
                sb.Append($" REROLL_HORIZONTAL_CHANGE={MissilesHelper.REROLL_HORIZONTAL_CHANGE_MIN}..{MissilesHelper.REROLL_HORIZONTAL_CHANGE_MAX}");
                sb.Append($" REROLL_VERTICAL_CHANGE={MissilesHelper.REROLL_VERTICAL_CHANGE_MIN}..{MissilesHelper.REROLL_VERTICAL_CHANGE_MAX}");
            }
            catch { }
            Log(sb.ToString());

            // smoke, kill threshold and stress: values in use now (the mod may already have changed the player's smoke delay)
            var a = new StringBuilder("réglages des missiles et fumigènes (valeurs en cours) :");
            try
            {
                if (bs != null)
                {
                    a.Append($" AUTOSMOKE_DELAY_MIN={bs.AUTOSMOKE_DELAY_MIN:0.###} AUTOSMOKE_DELAY_MAX={bs.AUTOSMOKE_DELAY_MAX:0.###}");
                    a.Append($" AUTOSMOKE_AI_DELAY_MIN={bs.AUTOSMOKE_AI_DELAY_MIN:0.###} AUTOSMOKE_AI_DELAY_MAX={bs.AUTOSMOKE_AI_DELAY_MAX:0.###}");
                    a.Append($" AUTOSMOKE_TRIGGER_BY_MISSILES={bs.AUTOSMOKE_TRIGGER_BY_MISSILES} AUTOSMOKE_AI_TRIGGER_BY_MISSILES={bs.AUTOSMOKE_AI_TRIGGER_BY_MISSILES}");
                    a.Append($" AUTOSMOKE_AI_CHANCE facile/moyen/difficile={bs.AUTOSMOKE_AI_DIFFICULTY_CHANCE_EASY:0.##}/{bs.AUTOSMOKE_AI_DIFFICULTY_CHANCE_MEDIUM:0.##}/{bs.AUTOSMOKE_AI_DIFFICULTY_CHANCE_HARD:0.##}");
                    a.Append($" MINIMAL_CHANCE_TO_KILL={bs.MINIMAL_CHANCE_TO_KILL:0.###}");
                }
                else a.Append(" BattleSystemSettings absent");
            }
            catch (Exception e) { a.Append(" lecture impossible (" + e.Message + ")"); }
            try
            {
                var sm = GameConfig.Instance?.BuffConfig?.StressModifiers;
                if (sm == null) a.Append(" ; stress : absent");
                else
                {
                    a.Append(" ; précision des missiles selon le stress (MissileAccuracyMultiplier / MissileAccuracyPlaneMult) :");
                    foreach (var kv in sm)
                    {
                        var m = kv.Value;
                        a.Append(m == null ? $" {kv.Key}=?" : $" {kv.Key}={m.MissileAccuracyMultiplier:0.###}/{m.MissileAccuracyPlaneMult:0.###}");
                    }
                }
            }
            catch (Exception e) { a.Append(" ; stress illisible (" + e.Message + ")"); }
            LogAtgm(a.ToString());
        }

        static string AmmoName(int id)
        {
            if (_ammoNames.TryGetValue(id, out var n)) return n;
            n = "";
            try { var src = DataBaseService._instance?.RawAccess; if (src != null && src.Ammunitions.TryGetById(id, out var row) && row != null) n = row.Name ?? ""; } catch { n = ""; }
            _ammoNames[id] = n;
            return n;
        }

        // ---------------------------------------------------------------- reports (main thread)
        static void Report(bool final)
        {
            long inRoll = Interlocked.Read(ref _chanceCalls[1]), outRoll = Interlocked.Read(ref _chanceCalls[0]);
            var sb = new StringBuilder();
            sb.Append($"{(final ? "bilan" : "relevé")} : impacts traités {Interlocked.Read(ref _hitUpdates)} (hors fil principal {Interlocked.Read(ref _hitOffMain)}), ");
            sb.Append($"calculs de dégâts pendant un impact {Interlocked.Read(ref _dmgInHit)} / en dehors {Interlocked.Read(ref _dmgOutside)}, ");
            sb.Append($"tirages de missile {Interlocked.Read(ref _divert)} (nouveaux tirages {Interlocked.Read(ref _divertReroll)}, hors fil principal {Interlocked.Read(ref _divertOffMain)}), ");
            sb.Append($"chances calculées pendant un tirage {inRoll} / hors tirage {outRoll} (hors fil principal {Interlocked.Read(ref _chanceOffMain)})");
            string[] lbl = { "0 leurre", "1 leurre", "2 leurres", "3+ leurres" };
            for (int d = 0; d < 8; d++)
            {
                long n = Interlocked.Read(ref _decoyN[d]);
                if (n == 0) continue;
                sb.Append($" ; {(d >= 4 ? "tirage" : "hors tirage")} {lbl[d % 4]} : {n} calculs, chance moyenne {F3(Interlocked.Read(ref _decoyChance[d]), n)}, effet leurre {F3(Interlocked.Read(ref _decoyEffect[d]), n)}");
            }
            sb.Append($", profondeurs périmées impacts/tirages {Interlocked.Read(ref _staleDepth)}/{Interlocked.Read(ref _staleRoll)}, erreurs {Interlocked.Read(ref _errors)}");
            string s = sb.ToString();
            string dec = _hooksFull ? DecisionsLine(final) : "";
            string atgm = AtgmLines();
            bool changed = s != _lastReport || dec != _lastDecisions || atgm != _lastAtgm;
            if (final || changed)
            {
                _lastReport = s; _lastDecisions = dec; _lastAtgm = atgm;
                bool any = Interlocked.Read(ref _hitUpdates) + Interlocked.Read(ref _divert) + inRoll + outRoll + Interlocked.Read(ref _dmgOutside) > 0;
                if (any || final)
                {
                    Log(s);
                    if (dec.Length > 0) Log(dec);
                    if (atgm.Length > 0) foreach (var line in atgm.Split('\n')) LogAtgm(line);
                }
            }
            if (!final) return;
            Interlocked.Exchange(ref _hitUpdates, 0); Interlocked.Exchange(ref _hitOffMain, 0); Interlocked.Exchange(ref _dmgInHit, 0); Interlocked.Exchange(ref _dmgOutside, 0);
            Interlocked.Exchange(ref _dmgOffMain, 0); Interlocked.Exchange(ref _rollOffMain, 0); Interlocked.Exchange(ref _staleDepth, 0); Interlocked.Exchange(ref _staleRoll, 0);
            Interlocked.Exchange(ref _divert, 0); Interlocked.Exchange(ref _divertReroll, 0); Interlocked.Exchange(ref _divertOffMain, 0);
            Interlocked.Exchange(ref _chanceCalls[0], 0); Interlocked.Exchange(ref _chanceCalls[1], 0); Interlocked.Exchange(ref _chanceOffMain, 0); Interlocked.Exchange(ref _errors, 0);
            for (int d = 0; d < 8; d++) { Interlocked.Exchange(ref _decoyN[d], 0); Interlocked.Exchange(ref _decoyChance[d], 0); Interlocked.Exchange(ref _decoyEffect[d], 0); }
            ResetBuckets();
            _lastReport = null; _lastDecisions = null; _lastAtgm = null;
        }

        /// [LEURRES] decisions line: missiles, rolls, decisions, gate, identity, base call, drops. Empty when nothing happened.
        static string DecisionsLine(bool final)
        {
            var b = _b;
            if (!final && b.IrRolls + b.NonIrBase + b.NonIrBaseSkipped + b.EligImpacts + b.H1 + b.H2 == 0) return "";
            var sb = new StringBuilder();
            sb.Append($"étape {StageName(EffStage)} : missiles infrarouges suivis {b.Missiles + _used} (en vol {_used}), tirages {b.IrRolls} (avec leurres {b.IrFlareRolls}), ");
            sb.Append($"touchés {b.Hit}, leurrés {b.Flare} (dont touchés devenus leurrés {b.HitToFlare}), ratés {b.Other}, retours à touché bloqués {b.Locked}");
            if (b.TableFull + b.AmmoMismatch > 0) sb.Append($", table pleine {b.TableFull}, munition différente sur un même missile {b.AmmoMismatch}");
            sb.Append($" ; radar/laser/commande face aux leurres : chance sans leurre appliquée {b.NonIrBase}, non appliquée {b.NonIrBaseSkipped}");
            sb.Append($" ; dégâts annulés : {b.GateCalls} calcul(s) dans {b.GateImpacts} impact(s) (appareils camp 0 {b.GatedAir0}, camp 1 {b.GatedAir1}, autres {b.GatedOther}), total avant triche {b.GatedDmg.ToString("0.##", Inv)}, fuites {b.GateLeak}");
            sb.Append($" ; impacts de missiles infrarouges reliés {b.Linked}/{b.EligImpacts} ; vus dans les projectiles {b.ScanSeen}/{b.ScanTotal}{(_scanAvail ? "" : " (liste indisponible)")}");
            sb.Append($" ; calculs de chance par tirage 0/1/2+ : {b.H0}/{b.H1}/{b.H2} (avec leurres 1 : {b.H1F}, 2+ : {b.H2F})");
            sb.Append($" ; chance sans leurre : {b.BaseCalls} appel(s) avec leurres (erreurs {b.BaseErr}, sous le jeu {b.BaseBelow}), égalités sans leurre {b.ZeroEq}/{b.ZeroEq + b.ZeroNe}");
            if (b.RatioN > 0) sb.Append($", rapport jeu/base {(b.RatioSum / b.RatioN).ToString("0.00", Inv)}");
            sb.Append($" ; décrochages {b.Drops} (erreurs {b.DropErr}, missiles déjà morts {b.DropSkippedDead}, partis en 20 s sans toucher un appareil {b.DroppedLeftOk}/{b.DroppedFinal}, appareil touché après décrochage {b.AirHitAfterDrop})");
            sb.Append($" ; missiles finis sans impact {b.LeftNoImpact}/{b.Missiles}, vol moyen {(b.Missiles > 0 ? (b.FlightSeconds / b.Missiles).ToString("0.0", Inv) : "-")} s, appels de guidage par seconde {(b.FlightSeconds > 0.1 ? (b.DivertCallsTracked / b.FlightSeconds).ToString("0", Inv) : "-")}");
            sb.Append($" ; effet des leurres vu 0.65 {b.Effect65}, 0.90 {b.Effect90}, autre {b.EffectOther}");
            sb.Append($" ; tirages hors fil principal {Interlocked.Read(ref _rollOffMain)}, dégâts hors fil principal {Interlocked.Read(ref _dmgOffMain)}");
            if (b.MissileLinesSuppressed > 0) sb.Append($" ; lignes de missile non écrites {b.MissileLinesSuppressed}");
            if (final)
            {
                var per = new List<string>();
                for (int k = 0; k < NIr; k++)
                    if (b.AmmoMissiles[k] > 0)
                        per.Add($"{AmmoName(IrIds[k])} ({IrIds[k]}) {b.AmmoMissiles[k]} missile(s), somme des chances du dernier tirage {Milli(b.AmmoLastChance[k])}, missiles ayant fait des dégâts {b.AmmoDamaged[k]}");
                if (per.Count > 0) sb.Append(" ; par munition : ").Append(string.Join(" | ", per));
            }
            return sb.ToString();
        }

        /// [ATGM] lines: one per tracked ground-attack missile seen, then other missiles rolled. Empty when nothing happened.
        static string AtgmLines()
        {
            var lines = new List<string>();
            var inv = Inv;
            for (int k = 0; k < NTracked; k++)
            {
                long nOut = Interlocked.Read(ref _bN[k * 2]), nIn = Interlocked.Read(ref _bN[k * 2 + 1]);
                if (nOut + nIn == 0) continue;
                int id = TrackedIds[k];
                var l = new StringBuilder($"{AmmoName(id)} ({id}) :");
                for (int roll = 1; roll >= 0; roll--)
                {
                    int i = k * 2 + roll;
                    long n = Interlocked.Read(ref _bN[i]);
                    l.Append(roll == 1 ? " pendant un tirage " : " | hors tirage ");
                    if (n == 0) { l.Append("aucun calcul"); continue; }
                    long cn = Interlocked.Read(ref _cN[i]);
                    l.Append($"{n} calculs, chance moyenne {F3(Interlocked.Read(ref _bSum[i]), n)} (min {Milli(Interlocked.Read(ref _bMin[i]))}, max {Milli(Interlocked.Read(ref _bMax[i]))}), ");
                    l.Append($"ECM moyen {F3(Interlocked.Read(ref _bEcm[i]), n)}, stress moyen {F3(Interlocked.Read(ref _bStress[i]), n)}, avec leurres actifs {Interlocked.Read(ref _bDecoy[i])} ; ");
                    l.Append(cn == 0 ? "cas propre (sans ECM, leurre ni stress) : aucun" : $"cas propre {cn} calculs, moyenne {F3(Interlocked.Read(ref _cSum[i]), cn)}, min {Milli(Interlocked.Read(ref _cMin[i]))}");
                }
                lines.Add(l.ToString());
            }
            var o = new StringBuilder();
            for (int j = 0; j < _otherId.Length; j++)
            {
                int id = _otherId[j];
                long n = Interlocked.Read(ref _otherN[j]);
                if (id == 0 || n == 0) continue;
                o.Append(o.Length == 0 ? "autres munitions tirées pendant un tirage :" : ",");
                o.Append($" {AmmoName(id)} ({id}) x{n.ToString(inv)}");
            }
            long over = Interlocked.Read(ref _otherOverflow);
            if (over > 0) o.Append($" ; plus {over} calcul(s) d'autres munitions");
            if (o.Length > 0) lines.Add(o.ToString());
            return string.Join("\n", lines);
        }

        static void ResetBuckets()
        {
            for (int i = 0; i < NTracked * 2; i++)
            {
                Interlocked.Exchange(ref _bN[i], 0); Interlocked.Exchange(ref _bSum[i], 0);
                Interlocked.Exchange(ref _bMin[i], long.MaxValue); Interlocked.Exchange(ref _bMax[i], long.MinValue);
                Interlocked.Exchange(ref _bEcm[i], 0); Interlocked.Exchange(ref _bStress[i], 0); Interlocked.Exchange(ref _bDecoy[i], 0);
                Interlocked.Exchange(ref _cN[i], 0); Interlocked.Exchange(ref _cSum[i], 0); Interlocked.Exchange(ref _cMin[i], long.MaxValue);
            }
            for (int j = 0; j < _otherId.Length; j++) { Interlocked.Exchange(ref _otherId[j], 0); Interlocked.Exchange(ref _otherN[j], 0); }
            Interlocked.Exchange(ref _otherOverflow, 0);
        }
    }
}
