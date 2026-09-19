// RealismOverhaul - automatic flare salvos (player's request, 2026-09-17): every aircraft of both sides (the player's, his allies', the
//  AI's, the enemy's) releases one flare salvo for every infrared missile that is aimed at it. Today the game only pops flares for the
//  first missile of an attack, so the second and the third one kill.
//  The module does NOT re-implement flares: it asks the engine to do its own job at the right moment, from the main loop.
//   trigger  CounterMeasuresSystem.FireCounterMeasures(ref Entity unit, bool force)  (static, works for any unit, owned or not)
//   guard    Entity.Has<CounterMeasuresComponent>()                                  (Has<T> only in a try/catch, never Get<T>)
//   pairing  SeekerSystem.CheckSingleTargetLosInit(ref Entity missileEntity, ref Entity target), a read-only prefix that only writes
//            (missile -> target) into LeurresMesure's missile table; fallback MissilesHelper.ScoreTargetOverkill (first two parameters only)
//   aircraft AntiHeliTouches' 0.5 s both-sides snapshot (EntityId -> LuaUnit, side, helicopter or plane, unit id)
//   infrared LeurresMesure's own missile table (it only holds infrared missiles, Irx >= 0)
//  One salvo per missile, never two: every threat read is marked as served whatever happens to it - with ONE exception, the aircraft still
//  reloading between two salvos. That threat is left PENDING instead of being written off, so the second missile of an attack gets its own
//  salvo as soon as the gap has run out; a threat whose missile is gone, whose target is stale or whose aircraft is dead is still written
//  off at once, which is what keeps that wait from spinning. A salvo is only ever asked from the main loop (Mod.OnUpdate), never from
//  inside a hook, exactly like LeurresMesure's fly-off.
//  The gap between two salvos is the one the DATABASE holds, never a shorter one: the engine refuses an early ask and that used to cost the
//  aircraft a whole salvo. The flare file gives it for a listed helicopter (1 s); for a plane, for an unlisted helicopter, and for every
//  aircraft when the mod's flare data is not in the database (OPTION_LEURRES_DEVIATION unticked, real stats off, lot suspended), the game's
//  own reload is taken instead. And when the engine still says no to an aircraft that has already fired, that "no" IS the real reload: the
//  gap is raised to what the refusal showed (never lowered) and the threat waits instead of being lost.
//  Stages, and the in-battle promotion the player asked for (2026-09-17: no measurement battle wasted):
//   0 measure only, nothing is fired. Every battle starts here. The module promotes ITSELF inside the same battle as soon as it has read
//     30 missile-to-target pairs on the main thread, resolved at least one threat to a real aircraft carrying CounterMeasuresComponent,
//     and made no error. One log line says so and the salvos start at once.
//   1 FireCounterMeasures(force: false): the engine keeps the stock, the gap and its own rules. Normal resting state.
//   2 FireCounterMeasures(force: true) with the mod's own stock accounting, and ONLY for an aircraft the flare file really gives a stock
//     to and that the engine has not already refused three times in a row (such an aircraft had its decoy ability switched off by the
//     mission script: it keeps normal salvos and is never forced). Entered only at the END of a battle whose numbers prove the engine
//     refused (fired/asks below 0.25 over at least 20 asks, the switched-off aircraft left out of the count), and left again as soon as
//     a battle shows errors or does not end normally.
//  The hidden preference LeurresAutoEtape holds the stage the module may reach: 0 forces a battle to measure only and never fire.
//  Aircraft held by the mission script (Blackout's helicopters, reinforcements, evacuations) are served like the others: a salvo issues no
//  order and changes no path, altitude, speed, target, health, fuel or weapon ammunition, so no mission script can see it. They are only
//  counted in the report. An ability the mission script switched off itself is never touched: if the engine refuses every salvo of such an
//  aircraft, the report says so, nothing is re-enabled.
//  Safety: hook installed lazily in a solo campaign battle with its own Harmony id and never unpatched (a volatile armed flag instead); the
//  prefix acts on the main thread only; error kill-switch (20); crash guard (two battles in a row that never ended refuse the module for
//  this version, and a battle interrupted while firing takes the forced salvos away); the module dies with LeurresMesure (CurrentStage 0
//  covers its watchdog trip, the end screen, the session switch-off and the mod being switched off) and has its own watchdog: 3 running
//  minutes without a single impact on the map, while the two sides are within 1 km, while the engine still resolves infrared missiles in
//  flight AND while the module is asking for salvos, SUSPEND the salvos. Impacts coming back lift the suspension; the second one of the
//  same battle is kept until the end of the battle. A quiet stretch resolves no missile at all, so it can never trip it. Nothing is written
//  into the game, so stopping restores everything by itself. Completely inert in a mission played without the mod (US_M01) and outside a
//  solo campaign battle. Logs: [LEURRES].
using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Seeker = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Guidance.Systems.SeekerSystem;
using MissilesHelper = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Missiles.MissilesHelper;
using CmSys = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.CounterMeasuresSystem;
using CmComp = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.CounterMeasuresComponent;
using EcsEntity = Il2CppDefaultEcs.Entity;
using LuaUnit = Il2CppBrokenArrow.MissionEditor.LuaBridge.LuaUnit;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    static class LeurresAuto
    {
        const string GuardVersion = "1.1";
        const int StageMesure = 0, StageNormal = 1, StageForce = 2;
        const int MaxErrors = 20;                                       // error budget of a game session
        const int MaxAir = 96;                                          // aircraft followed in a battle (above anything a campaign map holds)
        const int MaxAsksPerAircraft = 64;                              // ceiling per aircraft and per battle, FORCED salvos only (stage 1 is bounded by the engine's own stock)
        const int BufMax = 64;                                          // threats served in one frame
        const int ProofPairs = 30;                                      // missile-to-target pairs needed before the in-battle promotion
        const int DefaultStock = 30;                                    // stage 2 stock of an aircraft the flare file does not list
        const int ForceMinAsks = 20;                                    // asks needed before a battle may prove the engine refuses
        const float ForceRatio = 0.25f;                                 // below this share of salvos really fired, the engine refuses
        const float DefaultHeliGap = Affuts.HeliDecoyCooldown;          // 1 s between two salvos of the same helicopter (player's choice, 2026-09-18)
        // Reload the game's OWN database holds on a decoy ability (3 s for a helicopter as for a plane, 4 s for the B-52 row). Asking faster
        // than what the database really holds is refused by the engine, so this is the gap used whenever the mod's flare data is NOT what the
        // database holds: the player unticked OPTION_LEURRES_DEVIATION (then reel/Capacites.csv and reel/LeurresParUnite.csv are both skipped
        // and the game keeps its 3 s), the real stats are off, or the combat watchdog suspended that lot. It is also the gap of every plane,
        // which the mod leaves at the game's value (the plane's own 1.5 s AUTOFLARES_PLANES_DELAY is NOT a reload: it is only the floor of
        // the gap, through _gapPlane).
        const float GameDecoyCooldown = 3f;
        const float MaxLearnedGap = 6f;                                 // ceiling of the gap learned from the engine's own refusals
        const float LearnMargin = 0.25f;                                // added to a refused wait, so the next ask lands after the reload
        const string DecoyOption = "OPTION_LEURRES_DEVIATION";          // option that carries the mod's flare data (Capacites.csv, LeurresParUnite.csv)
        // Watchdog window. WatchAfter is the same 3 running minutes of complete silence as LeurresMesure's own watchdog: the shorter 60 s
        // window of v0.23.0 took a defensive standoff for a breakage in a real battle (RU_C01, 2026-09-18).
        const float WatchAfter = 180f, WatchContact = 1000f, TickMax = 2f;
        const float WatchLookback = 90f;                                // a salvo asked this long before the window still counts as "the module was acting"
        const long WatchRolls = 5;                                      // infrared missiles the engine really resolved during the window (a lull resolves none)
        const long WatchImpSlack = 2;                                   // impacts a window still counts as silent
        const long WatchBack = 5;                                       // impacts after a suspension that show the combat was simply quiet
        const int MaxTrips = 2;                                         // second suspension in the same battle: off until the end of the battle
        const long SlotReuseMs = 60000, ThreatSilenceMs = 60000, MinBattleMs = 120000;
        const float ReportEvery = 300f;                                 // one report every 5 minutes, plus one at battle end
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- preferences and session state
        static MelonPreferences_Entry<bool> _enabled, _firePending;
        static MelonPreferences_Entry<int> _unclean, _stagePref;
        static MelonPreferences_Entry<string> _guardVersion;
        static HarmonyLib.Harmony _harmony;
        static bool _patchTried, _refused, _refusedLogged, _everOnline, _sessionArmed, _settingsRead;
        static bool _battleOff;                                         // own watchdog: nothing is asked while it holds (it may be lifted, see Watchdog)
        static bool _offFinal;                                          // that suspension is the last one of this battle: never lifted again
        static int _trips;                                              // suspensions in this battle (report and battle-end rules)
        static bool _promoteLog, _silenceLogged, _firedThisBattle, _tableOff, _staleWarned, _snapWarned;
        static bool _decoyDataOn = true;                                // read once at battle start: the mod's flare data is in the database
        static int _stage, _target;                                     // stage of the battle in progress, stage it may be promoted to
        static int _hookKind;                                           // 1 = seeker target check, 2 = overkill scoring fallback
        static float _next, _nextReport, _lastTick = -1f, _runClock;
        static float _gapHeli = 0.5f, _gapPlane = 1.5f;                 // the game's own automatic-flare delays (floor of the mod's gap)
        static long _battleStartMs;
        // watchdog window: when it opened on the running game clock (-1 = the module has not asked for anything yet in this battle) and the
        // two LeurresMesure counters as they stood then, plus the running clock of the last salvo the module asked the engine for
        static float _winRun = -1f, _lastAskRun = float.NegativeInfinity;
        static long _winImp, _winRolls, _winFlareRolls;
        static float _offRun;                                           // running clock of the suspension in progress
        static long _offImp;                                            // impacts as they stood at that suspension
        static int _wait;

        static volatile bool _armed;
        static volatile int _mainThread;

        // counters of the battle in progress (the prefix runs on the main thread, the counters stay interlocked for the off-thread case)
        static long _pairs, _pairsOffMain, _pairsUnknown, _errors;      // _errors is the battle's own count (reset at every battle start)
        static long _errorsSession;                                     // the game session's count: it alone drives the kill-switch
        // _gapDefers = threats that had to wait for a reload (counted once each, not once per frame: the mod's own gap, or a refusal of the
        // engine on an aircraft that has already fired), _gapLate = those whose salvo really went out after that wait. A deferred threat is
        // never a skip: the two are told apart in the report, and the difference (threats that waited for nothing) is told as well.
        static long _asks, _fired, _refusedAsks, _mesures, _notAir, _stale, _dead, _noCm, _gapDefers, _gapLate, _emptySkips, _capSkips, _tableFull, _snapMissing;
        static int _proofAir, _airSeen, _airHeli, _airPlane, _airSide0, _airSide1, _airWithCm, _airScripted;
        static string _firstError;
        static string _lastReport;

        /// One aircraft of the battle in progress. Fixed-size table, no allocation in the salvo pass (the name is read once).
        struct Air
        {
            public int Eid, Uid, UnitId, Side;
            public bool Heli, HasCm, NoCm;
            public bool StockConnu;                                     // the flare file really gave this aircraft a stock (stage 2 needs it)
            public bool NoForce;                                        // the engine refuses every salvo of this aircraft: its decoy ability is switched off, never forced
            public byte Scripted;                                       // 0 not looked at yet, 1 held by the mission script, 2 free
            public float Gap;
            public int Left, Asks, Fired, Menaces, Mesures;
            public long LastAskMs, LastSeenMs;
            public long LastFiredMs;                                    // last ask the engine really fired: what a later refusal measures the reload against
            public bool GapLearned;                                     // the gap was raised from a refusal of the engine (report only)
            public string Name;
        }
        static readonly Air[] _air = new Air[MaxAir];
        static readonly int[] _bufT = new int[BufMax], _bufV = new int[BufMax], _bufS = new int[BufMax];

        static void Log(string s) => Mod.Log.Msg("[LEURRES] " + s);

        // ---------------------------------------------------------------- start-up
        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_LeurresAuto");
            _enabled = c.CreateEntry("LeurresAutomatiques", true, description: Build.Desc(
                "Leurres automatiques : chaque appareil (le tien, tes alliés, l'IA, les deux camps) envoie une salve de leurres pour chaque missile infrarouge qui le vise, tant qu'il lui reste du stock",
                "Leurres automatiques contre les missiles infrarouges (tous les appareils, les deux camps)."));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _stagePref = c.CreateEntry("LeurresAutoEtape", StageNormal, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _firePending = c.CreateEntry("LeurresAutoSalveEnCours", false, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion)
            {
                _guardVersion.Value = GuardVersion;
                _unclean.Value = 0;
                _firePending.Value = false;                             // a new version gets a new chance
            }
            if (_stagePref.Value < StageMesure || _stagePref.Value > StageForce) _stagePref.Value = StageNormal;
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false;                                        // a new mission may well be a solo campaign one again
        }
        internal static void OnQuit() => EndBattle("fermeture du jeu");

        // ---------------------------------------------------------------- main loop (Mod.OnUpdate, main thread)
        /// Every frame in a campaign mission: the salvo pass (it leaves at once on a frame with no new threat), then the module's own
        /// one-second job (arming, hook install, watchdog, report).
        internal static void Frame()
        {
            // the switch-off test comes first: the frame the player unchecks the option must not fire a last buffer of salvos
            if (_enabled == null || !_enabled.Value || _refused) { _armed = false; return; }
            Salves();
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
            if (now < _next) return;
            // one heavy module job per frame (Planif.cs); the battle arming, its preference write and the hook install may wait longer
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + 1f;
            try { Tick(now); }
            catch (Exception e) { Log("boucle des leurres automatiques : erreur (" + e.GetBaseException().Message + ")"); }
        }

        static void Tick(float now)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            if (gc?._GameSession_k__BackingField?.CurrentPlayer == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_armed) { _armed = false; Log("partie en ligne : leurres automatiques coupés"); } return; }
            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2) { Refuse(); return; }
                ArmBattle(now);
            }
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            _armed = !_refused && Interlocked.Read(ref _errorsSession) <= MaxErrors;
            if (!_armed) return;
            if (_promoteLog) { _promoteLog = false; LogPromotion(); }
            if (Interlocked.Read(ref _errors) > 0 && _firstError != null)
            {
                string m = _firstError; _firstError = null;
                Log($"leurres automatiques : erreur ({m})" + (Interlocked.Read(ref _errorsSession) > MaxErrors ? " ; coupés pour cette session (trop d'erreurs)" : ""));
            }
            if (!LeurresMesure.TableRunning) _tableOff = true;           // the flare measurement is off: nothing can say which missile aims at what
            if (!_silenceLogged && _hookKind > 0 && Interlocked.Read(ref _pairs) == 0 && Environment.TickCount64 - _battleStartMs > ThreatSilenceMs)
            {
                _silenceLogged = true;
                Log(_tableOff
                    ? "leurres réalistes coupés dans les réglages : les leurres automatiques ne peuvent pas savoir quel missile vise quel appareil, ils ne feront rien de cette bataille"
                    : "aucune cible de missile lue depuis le début de la bataille : les leurres automatiques n'ont encore rien à faire (le point d'accroche ne donne peut-être rien dans cette version du jeu)");
            }
            if (!_snapWarned && _snapMissing >= 50)
            {
                _snapWarned = true;
                Log("les appareils de la carte ne sont pas connus (la mesure des tirs sur les hélicoptères est coupée) : les leurres automatiques ne peuvent rien envoyer de cette bataille");
            }
            if (!_staleWarned && _stale >= 20 && _asks == 0)
            {
                _staleWarned = true;
                Log("les cibles des missiles ne correspondent à aucun appareil vivant de la carte : les leurres automatiques n'envoient rien, cibles périmées " + _stale);
            }
            bool running = Running(now);
            if (running) Watchdog();
            if (now >= _nextReport) { _nextReport = now + ReportEvery; Report(false); }
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

        static void Refuse()
        {
            _refused = true;
            _armed = false;
            if (_refusedLogged) return;
            _refusedLogged = true;
            Mod.Log.Warning("[LEURRES] leurres automatiques désactivés pour cette version : les deux dernières parties ne se sont pas terminées normalement ; le jeu garde ses leurres habituels");
        }

        // ---------------------------------------------------------------- battle start and end
        static void ArmBattle(float now)
        {
            _sessionArmed = true;
            if (_unclean.Value >= 1)
            {
                // the previous battle never ended (the game stopped). The forced salvos are the only unproven part: they are taken away.
                if (_stagePref.Value >= StageForce)
                {
                    _stagePref.Value = StageNormal;
                    Mod.Log.Warning("[LEURRES] la dernière partie ne s'est pas terminée normalement : les salves forcées des leurres automatiques sont retirées, les salves normales continuent ; une deuxième partie interrompue de suite les coupera pour cette version");
                }
                else Mod.Log.Warning("[LEURRES] la dernière partie ne s'est pas terminée normalement" + (_firePending.Value ? " alors que les leurres automatiques envoyaient des salves" : "") +
                                     " : ils continuent en salves normales ; une deuxième partie interrompue de suite les coupera pour cette version");
            }
            // the unfinished-battle marker is NOT written here: it is written the first time a salvo is really asked (see Salves), so a
            // battle in which the module only measured can never count as an interrupted one
            _firePending.Value = false;
            MelonPreferences.Save();

            for (int i = 0; i < MaxAir; i++) _air[i] = default;
            Interlocked.Exchange(ref _pairs, 0); Interlocked.Exchange(ref _pairsOffMain, 0); Interlocked.Exchange(ref _pairsUnknown, 0);
            Interlocked.Exchange(ref _errors, 0); _firstError = null;   // the battle starts with a clean slate (the session count keeps the kill-switch)
            _asks = _fired = _refusedAsks = _mesures = _notAir = _stale = _dead = _noCm = _gapDefers = _gapLate = _emptySkips = _capSkips = _tableFull = _snapMissing = 0;
            _proofAir = _airSeen = _airHeli = _airPlane = _airSide0 = _airSide1 = _airWithCm = _airScripted = 0;
            _battleOff = false; _offFinal = false; _trips = 0;
            _promoteLog = false; _silenceLogged = false; _firedThisBattle = false; _tableOff = false;
            _staleWarned = false; _snapWarned = false;
            _lastReport = null;
            _battleStartMs = Environment.TickCount64;
            _nextReport = now + ReportEvery;
            _runClock = 0f; _lastTick = -1f;
            _winRun = -1f; _winImp = 0; _winRolls = 0; _winFlareRolls = 0;
            _lastAskRun = float.NegativeInfinity; _offRun = 0f; _offImp = 0;
            _stage = StageMesure;
            _target = Math.Clamp(_stagePref.Value, StageMesure, StageForce);
            if (!_settingsRead) _settingsRead = ReadGameDelays();
            _decoyDataOn = DecoyDataWritten();                           // once per battle: the option read allocates, the salvo pass may not
            Log($"bataille : leurres automatiques en mesure pour commencer (rien n'est envoyé) ; ils passeront tout seuls aux salves dès qu'ils auront lu {ProofPairs} cibles de missile et un appareil porteur de leurres" +
                (_target == StageMesure ? " ; réglage sur mesure seulement : aucune salve ne sera envoyée de la bataille"
                 : _target >= StageForce ? " ; salves forcées (le jeu avait refusé les salves normales lors d'une bataille précédente)"
                 : " ; salves normales (le jeu garde le stock et la recharge)") +
                $" ; intervalle minimum {UnknownGap(true).ToString("0.#", Inv)} s pour un hélicoptère et {Math.Max(UnknownGap(false), _gapPlane).ToString("0.#", Inv)} s pour un avion" +
                (_decoyDataOn ? "" : " (les vraies stats des leurres ne sont pas appliquées : le jeu garde sa recharge, le mod ne demande donc pas plus vite qu'elle)") +
                " ; mêmes règles pour les deux camps");
        }

        static void EndBattle(string why)
        {
            _armed = false;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            // a battle the watchdog suspended proves nothing about the stage, even if the suspension was lifted afterwards
            bool normal = !_battleOff && _trips == 0 && Environment.TickCount64 - _battleStartMs >= MinBattleMs;
            try { Report(true); } catch (Exception e) { Log("bilan des leurres automatiques illisible : " + e.GetBaseException().Message); }
            try { EvaluateStage(normal); } catch (Exception e) { Log("règles d'étape des leurres automatiques illisibles : " + e.GetBaseException().Message); }
            if (_unclean != null && _unclean.Value != 0) _unclean.Value = 0;
            if (_firePending != null) _firePending.Value = false;
            MelonPreferences.Save();
            for (int i = 0; i < MaxAir; i++) _air[i] = default;
            Log($"fin de bataille ({why})");
        }

        /// Battle-end rules. Stage 2 is only ever entered when the engine really refused the normal salvos, and left again at the first
        /// battle with errors or with no clean end.
        static void EvaluateStage(bool normal)
        {
            long err = Interlocked.Read(ref _errors);
            // an aircraft whose decoy ability the mission script switched off refuses every salvo: it must NOT be what proves the engine
            // refuses, otherwise the mod would force flares on exactly the aircraft the script meant to leave without any.
            // Same rule for an aircraft whose stock the flare file does not give: the forced salvos skip it anyway (see "force" in Salves),
            // so its refusals - a reload the mod did not know, a stock the game keeps - may not be what sends the whole module into forced
            // salvos. Only the aircraft the forced salvos could really serve have a say.
            long asks = 0, fired = 0;
            for (int i = 0; i < MaxAir; i++)
            {
                if (_air[i].Eid == 0 || _air[i].NoForce || !_air[i].StockConnu) continue;
                asks += _air[i].Asks; fired += _air[i].Fired;
            }
            if (_stage == StageMesure)
            {
                string why =
                    _target == StageMesure ? "le réglage demande la mesure seulement"
                    : !_patchTried ? "la bataille a été trop courte pour poser le point d'accroche des cibles de missile"
                    : _hookKind == 0 ? "aucun point d'accroche des cibles de missile dans cette version du jeu"
                    : _tableOff ? "les leurres réalistes sont coupés dans les réglages : sans eux le mod ne sait pas quel missile vise quel appareil"
                    : err > 0 ? $"des erreurs sont arrivées ({err})"
                    : Interlocked.Read(ref _pairs) < ProofPairs ? $"trop peu de cibles de missile lues ({Interlocked.Read(ref _pairs)} sur {ProofPairs} : il faut des missiles infrarouges tirés sur des appareils)"
                    : _proofAir == 0 ? $"aucun missile infrarouge n'a visé un appareil porteur de leurres (cibles qui ne sont pas des appareils {_notAir}, cibles périmées {_stale}, appareils sans leurres {_noCm})"
                    : "la bataille s'est terminée avant la preuve";
                Log($"leurres automatiques : aucune salve envoyée de la bataille, {why}");
                return;
            }
            if (_stage >= StageForce)
            {
                if (err > 0 || !normal)
                {
                    _stagePref.Value = StageNormal;
                    Log($"changement d'étape des leurres automatiques : retour aux salves normales ({(err > 0 ? "erreurs " + err : "bataille trop courte ou coupée")})");
                }
                return;
            }
            if (err == 0 && normal && asks >= ForceMinAsks && fired < (long)(asks * ForceRatio) && _stagePref.Value < StageForce)
            {
                _stagePref.Value = StageForce;
                Log($"changement d'étape des leurres automatiques : le jeu a refusé {asks - fired} salve(s) sur {asks} (appareils dont le mod connaît le stock de leurres ; il garde sûrement les leurres automatiques coupés sur ces appareils) ; " +
                    "à la prochaine bataille les salves seront forcées, avec le stock de la base de données tenu par le mod");
            }
        }

        // ---------------------------------------------------------------- hook (own Harmony id, never unpatched)
        static void TryPatch()
        {
            try { _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.LeurresAuto"); }
            catch (Exception e) { _refused = true; Log("leurres automatiques : non installé (" + e.GetBaseException().Message + ")"); return; }
            var m = AccessTools.Method(typeof(Seeker), "CheckSingleTargetLosInit");
            if (m != null && Install(m, nameof(CiblePrefix))) { _hookKind = 1; Log($"leurres automatiques : point d'accroche des cibles de missile installé ; étape {StageName(_stage)}"); return; }
            var m2 = AccessTools.Method(typeof(MissilesHelper), "ScoreTargetOverkill");
            if (m2 != null && Install(m2, nameof(CiblePrefix2))) { _hookKind = 2; Log($"leurres automatiques : point d'accroche de secours des cibles de missile installé ; étape {StageName(_stage)}"); return; }
            _refused = true;
            _armed = false;
            Log("cible des missiles : méthode introuvable dans cette version du jeu : leurres automatiques inactifs");
        }

        static bool Install(MethodInfo target, string prefix)
        {
            try
            {
                var pre = new HarmonyMethod(typeof(LeurresAuto).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static));
                _harmony.Patch(target, prefix: pre);
                return true;
            }
            catch (Exception e) { Log($"cible des missiles ({target.Name}) : non installé ({e.GetBaseException().Message})"); return false; }
        }

        /// SeekerSystem :: Void CheckSingleTargetLosInit(Entity& missileEntity, Entity& target). Read-only: it declares no __result and no
        /// __instance, and only writes the pair into LeurresMesure's table. Main thread only, no allocation, no Unity call, no logging.
        static void CiblePrefix(ref EcsEntity missileEntity, ref EcsEntity target)
        {
            if (Campaign.MissionInerte || !_armed) return;
            if (Environment.CurrentManagedThreadId != _mainThread) { Interlocked.Increment(ref _pairsOffMain); return; }
            try
            {
                if (LeurresMesure.NoteMissileTarget(missileEntity.EntityId, missileEntity.Version, target.EntityId, target.Version, target))
                    Interlocked.Increment(ref _pairs);
                else Interlocked.Increment(ref _pairsUnknown);
            }
            catch (Exception e) { Err(e); }
        }

        /// MissilesHelper :: static Void ScoreTargetOverkill(Entity& missileEntity, Entity& targetEntity, SeekerComponent& seeker). Fallback
        /// when the seeker's target check is gone: the third parameter is simply not declared (same trick as LeurresMesure.DivertPostfix).
        static void CiblePrefix2(ref EcsEntity missileEntity, ref EcsEntity targetEntity)
        {
            if (Campaign.MissionInerte || !_armed) return;
            if (Environment.CurrentManagedThreadId != _mainThread) { Interlocked.Increment(ref _pairsOffMain); return; }
            try
            {
                if (LeurresMesure.NoteMissileTarget(missileEntity.EntityId, missileEntity.Version, targetEntity.EntityId, targetEntity.Version, targetEntity))
                    Interlocked.Increment(ref _pairs);
                else Interlocked.Increment(ref _pairsUnknown);
            }
            catch (Exception e) { Err(e); }
        }

        static void Err(Exception e)
        {
            if (_firstError == null) { try { _firstError = e.GetBaseException().Message; } catch { _firstError = "?"; } }
            Interlocked.Increment(ref _errors);
            if (Interlocked.Increment(ref _errorsSession) > MaxErrors) _armed = false;
        }

        // ---------------------------------------------------------------- the salvo pass (every frame, main thread, never inside a hook)
        /// One salvo per infrared missile aimed at an aircraft. Leaves at once on a frame with no new threat (one volatile int read).
        static void Salves()
        {
            if (Campaign.MissionInerte || !_sessionArmed || !_armed || _refused || _battleOff) return;
            if (LeurresMesure.PendingIrThreats == 0) return;
            if (Campaign.BattleOver || !ModOn) return;
            if (LeurresMesure.CurrentStage == 0) return;                // LeurresMesure's watchdog, end screen, session switch-off or mod off
            long ms = Environment.TickCount64;
            // the aircraft of the map come from AntiHeliTouches' 0.5 s snapshot; while it holds nothing, a threat is left for the next frame
            // instead of being written off as "not an aircraft" (otherwise a missile fired in the first half-second would never get a salvo)
            bool snapshot;
            try { snapshot = AntiHeliTouches.AircraftCount > 0; } catch { snapshot = false; }
            int n;
            try { n = LeurresMesure.CollectIrThreats(_bufT, _bufV, _bufS, BufMax, ms); }
            catch (Exception e) { Err(e); return; }
            for (int i = 0; i < n; i++)
            {
                int slot = _bufS[i];
                int eid = _bufT[i];
                LuaUnit u; int uid, side, unitId; bool heli;
                bool air;
                try { air = AntiHeliTouches.TryAircraft(eid, out u, out uid, out side, out heli, out unitId); }
                catch (Exception e) { LeurresMesure.MarkFlareAsked(slot); Err(e); continue; }
                if (!air)
                {
                    // the map's aircraft are not known yet: retry next frame. Once the warning is out they never will be (the hit
                    // measurement is off or refused), so the threat is written off instead of being re-scanned every frame for nothing.
                    if (!snapshot && !_snapWarned) { _snapMissing++; continue; }
                    if (!snapshot) _snapMissing++; else _notAir++;                       // ground unit, ship, or a shell the seeker may aim at
                    LeurresMesure.MarkFlareAsked(slot); continue;
                }
                int k = Slot(eid, uid, side, heli, unitId, u, ms);
                if (k < 0) { _tableFull++; LeurresMesure.MarkFlareAsked(slot); continue; }
                // a threat that already waited for a reload comes back frame after frame: it is one missile, counted once
                bool waited = LeurresMesure.WasFlareDeferred(slot);
                if (!waited) _air[k].Menaces++;
                if (_air[k].NoCm) { _noCm++; LeurresMesure.MarkFlareAsked(slot); continue; }
                // forced salvos only for an aircraft whose stock the flare file really gives and which the engine has not already refused
                bool force = _stage >= StageForce && _air[k].StockConnu && !_air[k].NoForce;
                // the per-aircraft ceiling only bounds the forced salvos: in stage 1 the engine's own stock and reload bound everything
                if (force && _air[k].Asks >= MaxAsksPerAircraft) { _capSkips++; LeurresMesure.MarkFlareAsked(slot); continue; }

                EcsEntity e2;
                try { e2 = u.Entity; } catch (Exception e) { LeurresMesure.MarkFlareAsked(slot); Err(e); continue; }
                if (e2.EntityId != eid || e2.Version != _bufV[i]) { _stale++; LeurresMesure.MarkFlareAsked(slot); continue; }
                bool alive;
                try { alive = e2.IsAlive; } catch (Exception e) { LeurresMesure.MarkFlareAsked(slot); Err(e); continue; }
                if (!alive) { _dead++; LeurresMesure.MarkFlareAsked(slot); continue; }
                if (!_air[k].HasCm)
                {
                    bool has;
                    try { has = e2.Has<CmComp>(); }
                    catch (Exception e) { _air[k].NoCm = true; LeurresMesure.MarkFlareAsked(slot); Err(e); continue; }
                    if (!has) { _air[k].NoCm = true; _noCm++; LeurresMesure.MarkFlareAsked(slot); continue; }
                    _air[k].HasCm = true;
                    _airWithCm++;
                    _proofAir++;
                }
                if (_stage == StageMesure) { _air[k].Mesures++; _mesures++; LeurresMesure.MarkFlareAsked(slot); continue; }
                // Still reloading. The threat is NOT written off: it stays pending and comes back on a later frame, once the gap has run
                // out, otherwise the second missile of an attack would be dropped for ever over a wait of one second. The wait cannot spin -
                // every test above (missile gone from the table, target stale, aircraft dead, no decoy component) still writes it off at
                // once, and a threat the seeker stops refreshing leaves the table on its own.
                if (_air[k].LastAskMs != 0 && ms - _air[k].LastAskMs < (long)(_air[k].Gap * 1000f))
                {
                    if (LeurresMesure.MarkFlareDeferred(slot)) _gapDefers++;
                    continue;
                }
                if (force && _air[k].Left <= 0) { _emptySkips++; LeurresMesure.MarkFlareAsked(slot); continue; }
                // crash guard: the battle is marked as "a salvo was asked in it" before the very first call, once, on the main thread
                if (!_firedThisBattle)
                {
                    _firedThisBattle = true;
                    try { _unclean.Value = _unclean.Value + 1; _firePending.Value = true; MelonPreferences.Save(); } catch { }
                }

                bool ok;
                try { ok = CmSys.FireCounterMeasures(ref e2, force); }
                catch (Exception e) { LeurresMesure.MarkFlareAsked(slot); Err(e); continue; }
                _asks++;
                _air[k].Asks++;
                _air[k].LastAskMs = ms;
                // watchdog: when the module last asked the engine for anything (a refused ask is an engine call all the same), and the
                // opening of its first window. Two writes of a static float, no allocation, no engine call.
                _lastAskRun = _runClock;
                if (_winRun < 0f) StartWindow();
                if (ok)
                {
                    // this threat would have been lost before: it had waited for a reload, and its salvo really went out (a refused ask is
                    // not a salvo, so it is not counted here, otherwise the report could show more threats served than threats that waited)
                    if (waited) _gapLate++;
                    _fired++;
                    _air[k].Fired++;
                    _air[k].LastFiredMs = ms;
                    if (force && _air[k].Left > 0) _air[k].Left--;
                }
                else
                {
                    _refusedAsks++;
                    // three asks, nothing fired: the mission script switched this aircraft's decoy ability off. It is never forced.
                    if (!_air[k].NoForce && _air[k].Asks >= 3 && _air[k].Fired == 0) _air[k].NoForce = true;
                    // This aircraft has already fired in this battle, so its decoy ability works, and this "no" arrives soon enough after its
                    // last salvo to BE its reload: the database holds a longer one than the mod assumed. The real reload is learned from the
                    // refusal (the gap is only ever made longer, never shorter) and the threat is kept PENDING instead of being lost, exactly
                    // as if the mod had known that reload and waited. Without this, an ask one frame too early costs the aircraft a salvo.
                    // A "no" that comes long after the last salvo is not a reload (empty stock in the engine, ability switched off since):
                    // nothing is learned from it and the threat is written off as before.
                    long since = ms - _air[k].LastFiredMs;
                    if (_air[k].Fired > 0 && _air[k].LastFiredMs != 0 && since <= (long)(MaxLearnedGap * 1000f))
                    {
                        float seen = since / 1000f + LearnMargin;
                        float step = _air[k].Gap * 2f;                   // at most double per refusal: one odd refusal cannot send the gap to the ceiling
                        if (seen > step) seen = step;
                        if (seen > MaxLearnedGap) seen = MaxLearnedGap;
                        if (seen > _air[k].Gap) { _air[k].Gap = seen; _air[k].GapLearned = true; }
                        if (LeurresMesure.MarkFlareDeferred(slot)) _gapDefers++;
                        continue;                                       // no MarkFlareAsked: the threat comes back once the gap has run out
                    }
                }
                if (_air[k].Scripted == 0) NoteScript(k);               // once per aircraft, counted only, never a reason to skip
                LeurresMesure.MarkFlareAsked(slot);
            }
            if (_stage == StageMesure && _target > StageMesure) TryPromote();
        }

        static bool ModOn => Mod.Actif == null || Mod.Actif.Value;

        /// In-battle promotion (player's choice, 2026-09-17): the module proves itself inside the battle instead of wasting a whole one.
        static void TryPromote()
        {
            if (Interlocked.Read(ref _pairs) < ProofPairs) return;
            if (_proofAir < 1) return;
            if (Interlocked.Read(ref _errors) != 0) return;
            _stage = _target;
            _promoteLog = true;                                          // the line is written by the one-second job, never from the salvo pass
        }

        static void LogPromotion() =>
            Log($"changement d'étape des leurres automatiques : preuve faite dans cette bataille ({Interlocked.Read(ref _pairs)} cibles de missile lues, {_airWithCm} appareil(s) porteur(s) de leurres, aucune erreur) ; " +
                $"à partir de maintenant chaque appareil des deux camps envoie une salve pour chaque missile infrarouge qui le vise ({(_stage >= StageForce ? "salves forcées, stock tenu par le mod" : "salves normales, stock et recharge tenus par le jeu")})");

        /// Table slot of an aircraft, created at its first threat. -1 when the table is full and nothing may be reused.
        static int Slot(int eid, int uid, int side, bool heli, int unitId, LuaUnit u, long ms)
        {
            int free = -1, old = -1;
            long oldest = long.MaxValue;
            for (int i = 0; i < MaxAir; i++)
            {
                // the EntityId alone is not an identity: the engine hands a dead aircraft's EntityId to a new one. A row whose mission
                // UID no longer matches is wiped, so the new aircraft is looked at from scratch (its decoy component included).
                if (_air[i].Eid == eid)
                {
                    if (_air[i].Uid == uid) { _air[i].LastSeenMs = ms; return i; }
                    _air[i] = default;
                }
                if (_air[i].Eid == 0) { if (free < 0) free = i; continue; }
                if (_air[i].LastSeenMs < oldest) { oldest = _air[i].LastSeenMs; old = i; }
            }
            int k = free >= 0 ? free : (old >= 0 && ms - oldest > SlotReuseMs ? old : -1);
            if (k < 0) return -1;
            _air[k] = default;
            _air[k].Eid = eid; _air[k].Uid = uid; _air[k].UnitId = unitId; _air[k].Side = side; _air[k].Heli = heli;
            _air[k].LastSeenMs = ms;
            int qty;
            float gap;
            // the flare file's stock and gap are only taken when that file really is in the database: otherwise the game keeps its own stock
            // and its own reload, and going by the file would both ask too fast and count a stock the game does not have
            try { if (Affuts.TryDecoyStock(unitId, out qty, out gap) && _decoyDataOn) _air[k].StockConnu = qty > 0; else { qty = 0; gap = UnknownGap(heli); } }
            catch { qty = 0; gap = UnknownGap(heli); }
            float floorGap = heli ? _gapHeli : _gapPlane;               // never shorter than the game's own automatic-flare delay
            if (gap < floorGap) gap = floorGap;
            if (gap < 0.5f) gap = 0.5f;
            _air[k].Gap = gap;
            _air[k].Left = qty > 0 ? qty : DefaultStock;
            try { _air[k].Name = u.Name ?? "?"; } catch { _air[k].Name = "?"; }
            _airSeen++;
            if (heli) _airHeli++; else _airPlane++;
            if (side == 0) _airSide0++; else if (side == 1) _airSide1++;
            return k;
        }

        /// Gap for an aircraft whose reload the flare file does not give: every plane, a helicopter the file does not list, and every
        /// aircraft when the mod's flare data is not in the database at all. The engine refuses an ask that comes before the reload the
        /// DATABASE holds, so the mod may never guess shorter than that: the helicopter's 1 s is only used when the mod's flare data really
        /// was written (that is what puts 1 s in the database); otherwise, and for planes, which the mod leaves at the game's 3 s (B-52 4 s),
        /// the game's own reload is used. An aircraft whose real reload turns out to be longer still corrects itself on the first refusal.
        static float UnknownGap(bool heli) => heli && _decoyDataOn ? DefaultHeliGap : GameDecoyCooldown;

        /// Whether the mission script holds this aircraft. Counted for the report only: a scripted helicopter gets its salvos like any
        /// other one (a salvo issues no order, so the script hooks of Missions.cs can never mistake it for one).
        static void NoteScript(int k)
        {
            _air[k].Scripted = 2;
            try
            {
                if (Missions.ScriptReason(_air[k].Uid, UnityEngine.Time.time) != null) { _air[k].Scripted = 1; _airScripted++; }
            }
            catch { }
        }

        // ---------------------------------------------------------------- watchdog (main thread, once per second)
        /// Running game clock of the battle: real seconds since the last tick (at most 2 s), only while the game is not paused and the end
        /// screen is not up. Time.time keeps running while this game is paused, so the pause is read from the play session.
        static bool Running(float now)
        {
            float dt = _lastTick < 0f ? 0f : Math.Clamp(now - _lastTick, 0f, TickMax);
            _lastTick = now;
            if (Campaign.BattleOver) return false;
            bool paused = false;
            try { paused = UnityEngine.Time.timeScale <= 0f; } catch { }
            var gc = GameController._instance;
            if (gc != null)
            {
                try
                {
                    var s = gc._GameSession_k__BackingField;
                    if (s != null && (s.IsPaused || s.TimeScale <= 0f)) paused = true;
                }
                catch { }
            }
            if (paused) return false;
            _runClock += dt;
            return true;
        }

        /// Opens (or restarts) the watchdog window at the running clock of now, with the LeurresMesure counters as they stand.
        static void StartWindow()
        {
            _winRun = _runClock;
            _winImp = LeurresMesure.ImpactsThisBattle;
            LeurresMesure.BattleRolls(out _winRolls, out _winFlareRolls);
        }

        /// Own watchdog. What it is afraid of: the module asks for flares and missiles then stop hitting anything at all. It may only ever
        /// act on that, so a window trips ONLY when all four of these hold over the last 3 running minutes (pauses and the end screen do not
        /// advance that clock):
        ///   - not one impact on the whole map (at most WatchImpSlack), while
        ///   - the two sides stand within WatchContact of each other, and
        ///   - the engine still really resolved WatchRolls infrared missiles IN FLIGHT during the window (LeurresMesure's roll counters,
        ///     which move before the module decides anything), and
        ///   - the module asked the engine for a salvo during the window or in the WatchLookback before it.
        /// The third test is what the v0.23.0 watchdog was missing, and it is what makes a lull impossible to mistake for a breakage: an
        /// ordinary lull - a defensive standoff, the player away from the keyboard, a pause the game did not report - fires no missile at
        /// all, so it rolls none, so the window restarts instead of tripping. A real breakage keeps rolling missiles and lands nothing.
        /// A trip only SUSPENDS the salvos: WatchBack impacts coming back lift it, since the combat was plainly not stopped. The second
        /// suspension of the same battle is kept until the end of the battle, so a build that really does stop every impact still ends up
        /// switched off. Nothing is ever written, so there is nothing to put back: the game simply keeps its own flares meanwhile.
        static void Watchdog()
        {
            if (_winRun < 0f) return;                                   // the module has not asked for anything yet in this battle
            long imp = LeurresMesure.ImpactsThisBattle;
            LeurresMesure.BattleRolls(out long rolls, out long flareRolls);
            if (_battleOff) { LiftMaybe(imp); return; }
            long impIn = imp - _winImp, rollsIn = rolls - _winRolls, flaresIn = flareRolls - _winFlareRolls;
            // neither counter can go backwards inside a battle: if one does, LeurresMesure has started a new battle of its own and the
            // window would compare two different battles. Start over rather than judge on it.
            if (impIn < 0 || rollsIn < 0 || flaresIn < 0) { StartWindow(); return; }
            float contact = AntiHeliTouches.ContactMeters;
            // fire is alive, or the sides are far apart, or the distance is unreadable (the helicopter measurement is off): start over
            if (impIn > WatchImpSlack || contact < 0f || contact > WatchContact) { StartWindow(); return; }
            if (_runClock - _winRun < WatchAfter) return;
            // the two proofs the v0.23.0 watchdog was missing. Either one missing means an ordinary lull, not a breakage.
            if (rollsIn < WatchRolls || _lastAskRun < _winRun - WatchLookback) { StartWindow(); return; }
            Trip(rollsIn, flaresIn, imp, contact);
        }

        /// Suspends the salvos and says exactly what was observed - nothing about what caused it, which the mod cannot know.
        static void Trip(long rollsIn, long flaresIn, long imp, float contact)
        {
            _battleOff = true;
            _trips++;
            _offRun = _runClock;
            _offImp = imp;
            _offFinal = _trips >= MaxTrips;
            Mod.Log.Warning($"[LEURRES] chien de garde : {WatchAfter.ToString("0", Inv)} secondes de jeu sans le moindre impact sur la carte, alors que les deux camps étaient à " +
                            $"{contact.ToString("0", Inv)} m l'un de l'autre, que le jeu a quand même calculé {rollsIn} missile(s) infrarouge(s) en vol pendant ce temps " +
                            $"(dont {flaresIn} face à des leurres actifs) et que les leurres automatiques ont demandé des salves pendant ce temps ou juste avant " +
                            $"({_asks} demandée(s) et {_fired} partie(s) depuis le début de la bataille) : " +
                            (_offFinal
                             ? $"{MaxTrips}e fois dans cette bataille, les leurres automatiques restent coupés jusqu'à la fin de la bataille"
                             : $"leurres automatiques mis en pause ; ils repartiront tout seuls dès que {WatchBack} impacts seront revenus") +
                            " ; rien n'est enregistré, le jeu garde ses leurres habituels");
        }

        /// Main thread, once a second while the salvos are suspended. Impacts coming back show the combat was not stopped, so the
        /// suspension is lifted and a fresh window opens. Nothing is concluded about the cause: if the same complete silence comes back,
        /// the next trip is the last one of the battle.
        static void LiftMaybe(long imp)
        {
            if (_offFinal) return;
            long back = imp - _offImp;
            if (back < 0) { _offImp = imp; return; }                    // LeurresMesure started a new battle: rebase, never judge on it
            if (back < WatchBack) return;
            _battleOff = false;
            StartWindow();
            Log($"chien de garde : {back} impact(s) depuis la mise en pause des leurres automatiques, {(_runClock - _offRun).ToString("0", Inv)} secondes de jeu plus tard : " +
                $"le combat n'est pas arrêté, les leurres automatiques repartent" +
                (_trips + 1 >= MaxTrips ? " ; si le même silence complet revient dans cette bataille, ils seront coupés jusqu'à la fin de la bataille" : ""));
        }

        // ---------------------------------------------------------------- reports
        /// Whether the mod's own flare data is really in the database for this battle. When the player unticks OPTION_LEURRES_DEVIATION, the
        /// ECM Heli rows of reel/Capacites.csv AND reel/LeurresParUnite.csv are both skipped, so the database keeps the game's own reload:
        /// the mod must then ask at the game's rate, not at its own. Same thing when the real stats are off or when the combat watchdog has
        /// suspended that lot. Allocates (the off list is built): called once per battle from ArmBattle, never from the salvo pass.
        static bool DecoyDataWritten()
        {
            try { return Realism.RealModeOn && !Realism.OffKeys().Contains(DecoyOption); }
            catch { return false; }                                      // unreadable: take the slower, always-accepted gap
        }

        /// The game's own automatic-flare delays, read once per game session (0.5 s helicopters / 1.5 s planes on this build). They are only
        /// the floor of the mod's gap: the mod never asks for salvos closer together than the game itself would.
        static bool ReadGameDelays()
        {
            try
            {
                var bs = GameConfig.Instance?.BattleSystemSettings;
                if (bs == null) return false;
                var t = bs.GetType();
                _gapHeli = Read(t, bs, "AUTOFLARES_HELICOPTERS_DELAY", _gapHeli);
                _gapPlane = Read(t, bs, "AUTOFLARES_PLANES_DELAY", _gapPlane);
                return true;
            }
            catch { return false; }
        }

        static float Read(Type t, object bs, string name, float def)
        {
            try
            {
                object v = t.GetProperty(name)?.GetValue(bs);
                if (v is float f && f > 0f && f <= 30f) return f;
            }
            catch { }
            return def;
        }

        static string StageName(int s) => s >= StageForce ? "salves forcées" : s >= StageNormal ? "salves normales" : "mesure";

        static void Report(bool final)
        {
            long asks = _asks, fired = _fired, menaces = 0;
            int refusedAir = 0, learnedAir = 0;
            for (int i = 0; i < MaxAir; i++)
            {
                if (_air[i].Eid == 0) continue;
                menaces += _air[i].Menaces;
                // an aircraft the game always refuses: its decoy ability is switched off (mission scripts do that on their own helicopters)
                if (_air[i].Asks >= 3 && _air[i].Fired == 0) refusedAir++;
                if (_air[i].GapLearned) learnedAir++;
            }
            // threats that waited for a reload and never got their salvo: the missile arrived first, the aircraft was lost, or the wait is
            // still running at the moment of this report. Never told as "none" any more: the wait can lose a threat and the player must see it.
            long gapLost = _gapDefers - _gapLate;
            if (gapLost < 0) gapLost = 0;
            var sb = new StringBuilder();
            sb.Append(final ? "bilan des leurres automatiques" : "relevé des leurres automatiques");
            string wd = _battleOff
                ? (_offFinal ? ", coupés jusqu'à la fin de la bataille par le chien de garde" : ", en pause par le chien de garde")
                : _trips > 0 ? $", remis en route après {_trips} mise(s) en pause du chien de garde" : "";
            sb.Append($" (étape {StageName(_stage)}{wd}) : ");
            sb.Append($"missiles infrarouges visant un appareil {menaces}, appareils visés {_airSeen} (camp 0 {_airSide0}, camp 1 {_airSide1} ; hélicoptères {_airHeli}, avions {_airPlane}) ; ");
            sb.Append($"salves demandées {asks}, parties {fired}, refusées par le jeu {_refusedAsks} (recharge, stock vide ou leurres coupés sur l'appareil) ; ");
            sb.Append($"menaces vues pendant la mesure {_mesures} ; appareils porteurs de leurres {_airWithCm}, menaces laissées de côté faute de leurres {_noCm}, ");
            sb.Append($"menaces mises en attente de la recharge {_gapDefers} (dont {_gapLate} servie(s) après l'attente, {gapLost} sans salve : missile arrivé au but, appareil perdu, ou attente encore en cours), stock vide {_emptySkips}, plafond par appareil {_capSkips}, table pleine {_tableFull} ; ");
            sb.Append($"missiles dont la cible n'est pas un appareil {_notAir}, cibles périmées {_stale}, appareils déjà détruits {_dead}, appareils de la carte pas encore connus {_snapMissing}, appareils tenus par le script de mission {_airScripted} ; ");
            sb.Append($"paires missile-cible lues {Interlocked.Read(ref _pairs)} (missiles hors table {Interlocked.Read(ref _pairsUnknown)}, hors fil principal {Interlocked.Read(ref _pairsOffMain)}), erreurs {Interlocked.Read(ref _errors)}");
            if (refusedAir > 0)
                sb.Append($" ; {refusedAir} appareil(s) refusent toutes leurs salves : leur capacité de leurres est coupée (souvent par le script de la mission), le mod n'y touche pas");
            if (learnedAir > 0)
                sb.Append($" ; {learnedAir} appareil(s) rechargent plus lentement que prévu : le mod a allongé leur intervalle entre deux salves d'après les refus du jeu, la menace attend au lieu d'être perdue");
            if (!_decoyDataOn)
                sb.Append($" ; vraies stats des leurres non appliquées (option {DecoyOption}) : le mod s'en tient à la recharge du jeu ({GameDecoyCooldown.ToString("0.#", Inv)} s)");

            string s = sb.ToString();
            if (!final && s == _lastReport) return;
            _lastReport = s;
            bool any = asks + _mesures + menaces + Interlocked.Read(ref _pairs) > 0;
            if (any) Log(s);                                            // a battle where nothing happened only gets the one-line verdict
            if (!final) return;
            for (int i = 0; i < MaxAir; i++)
            {
                if (_air[i].Eid == 0 || (_air[i].Asks <= 5 && _air[i].Mesures <= 5)) continue;
                Log($"U{_air[i].UnitId} {_air[i].Name} camp {_air[i].Side} ({(_air[i].Heli ? "hélicoptère" : "avion")}) : {_air[i].Menaces} missile(s) infrarouge(s) reçu(s), " +
                    $"{_air[i].Asks} demande(s), {_air[i].Fired} salve(s) partie(s), stock restant tenu par le mod {_air[i].Left}" +
                    (_air[i].Mesures > 0 ? $", {_air[i].Mesures} menace(s) vue(s) pendant la mesure" : "") +
                    (_air[i].GapLearned ? $" ; intervalle entre deux salves porté à {_air[i].Gap.ToString("0.#", Inv)} s d'après les refus du jeu" : "") +
                    (_air[i].Scripted == 1 ? " ; tenu par le script de la mission" : "") +
                    (_air[i].Asks >= 3 && _air[i].Fired == 0 ? " ; le jeu refuse toutes ses salves (capacité de leurres coupée)" : ""));
            }
        }
    }
}
