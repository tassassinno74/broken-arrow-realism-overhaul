// SanteCombat: combat-health watchdog of the real-scale lots (v0.24.0), the same for both sides, solo campaign only.
//  - Impacts are counted by an observer prefix on ShellHitSystem.InternalUpdate (counter only), installed once a real-scale lot is applied.
//    Every 5 s, live units of the two sides (planes left out) are checked for contact (within 3 km of each other). Every 60 s: impacts per
//    minute, contact, silence, and the longest frame of each of the last two 30 s windows.
//  - Watchdog clock: game time, stopped while the game is paused or the battle end screen is up. A stop signal contradicted three checks in
//    a row (impacts or movement while it is up) is ignored for the rest of the battle, so a stuck signal never freezes the watchdog.
//  - Trip: fire had started in this battle, then no impact for 180 s of watchdog time while the sides were in contact at 70 % or more of
//    the checks since the last impact.
//  - Real ballistics (OPTION_BALISTIQUE_REELLE, runtime safe): taken out at once for this battle (gravity, rows and per-unit copies),
//    logged and shown on screen; it comes back at the next database apply of another battle. Fire resuming within 120 s of that is a
//    proof against it (hidden preference, this mod version; impacts in the first 30 s are rounds already in flight and do not count),
//    unless another module cut its own rules in the same minute; after two proofs it stays off for this version. Without the impact
//    counter, real ballistics is taken out for the battle.
//  - Data lots (every other lot RealStats accepted: ranges, manual aim, sensors, minimum ranges, laser, smoke): never taken out in the
//    middle of a battle. A trip after which fire
//    stays silent 120 s more, with the sides in contact and units moving, is one alert (at most one per battle); after two alerts in this
//    version, the data lots applied in that battle are suspended (hidden preference LotsSuspendus, read at the next database apply).
//  - Once per battle, read only: whether the ballistics helper holds GameConfig's battle settings and their gravity (real ballistics is
//    taken out if the engine kept another gravity), and a solver probe (CalculateBallisticsAngle on four distance/speed cases).
//  - Cheats are never touched. No other engine hook; every other read is a plain getter on the main thread.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using ShellHit = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellHitSystem;
using Ballistics = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Ballistics.BallisticsCalculationsHelper;
using BallisticData = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Ballistics.BallisticDataStruct;
using BSConst = Il2CppBrokenArrow.Shared.Ecs.BattleSystem.BattleSystemConstants;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using Spawner = Il2CppBrokenArrow.Client.Ecs.BattleSystem.ProjectileSpawnerHelper;
using Settings = Il2CppBrokenArrow.Client.Ecs.Configs.BattleSystemSettings;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using EndScreen = Il2CppBrokenArrow.Client.Ecs.UI.Menu.BattleEnd.BattleEndScreen;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class SanteCombat
    {
        const float TickEvery = 1f, WatchEvery = 5f, ReportEvery = 60f, FrameWindow = 30f;
        const float Silence = 180f, ResumeWindow = 120f, Grace = 30f, OtherCutWindow = 60f, MaxStep = 10f;
        const float ContactRange = 3000f, ContactShare = 0.7f;
        const int MinChecks = 30, HoldStrikes = 3, ProofsToStop = 2, AlertsToSuspend = 2, MaxWatchErrors = 5;
        const int HoldEnd = 1, HoldPause = 2, HoldScale = 4;
        const int RolePlaneMin = 160, RolePlaneMax = 164;
        const int EscapeMinSamples = 10;
        const float EscapeMaxMove = 5000f;
        const int SettingsTries = 24;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // hidden preferences (reset when the mod version changes, except the artillery measurement: it describes the game)
        static MelonPreferences_Entry<string> _version, _suspended;
        static MelonPreferences_Entry<int> _proofs, _alerts, _escapeSamples;
        static MelonPreferences_Entry<float> _escapeMean;
        static bool _escapeDirty;

        // impact counter (any thread)
        static HarmonyLib.Harmony _harmony;
        static bool _hook, _hookFailed;
        static long _impacts;

        // battle
        static IntPtr _ctx, _cutCtx;                   // session of the battle / session where real ballistics was taken out
        static bool _battle, _online, _cutHere, _endLogged;
        static float _nextTick, _nextWatch, _nextReport, _nextSettings;
        static int _watchErrors, _settingsTries;
        static bool _settingsDone;
        static LuaMap _map;
        static readonly List<V3>[] _pos = { new(), new() };
        static readonly Dictionary<int, bool> _planeByUid = new();
        static double _sig; static int _sigCount = -1;
        static int _scanErrors;
        static bool _lastContact;

        // watchdog clock and state
        static float _clock, _prevTime = -1f, _lastImpactAt, _tripAt, _otherCutAt = float.NegativeInfinity;
        static long _lastImp = -1, _impPrevTick, _impAtTrip, _impAtGrace;
        static int _checks, _contactChecks, _postChecks, _postContact, _postMoving;
        static bool _fired, _tripped, _tripB, _quietUntilImpact, _alertThisBattle, _silenceNoted;
        static List<string> _tripData = new();
        static int _holdPrev, _holdBad;
        static readonly int[] _holdStrike = new int[3];
        static bool _endHiddenSeen;
        static string _otherCutName;
        static FieldInfo _antiHeliTripped;
        static bool _antiHeliLooked, _antiHeliPrev;

        // report and frames
        static float _frameMax, _frameWin0, _frameWin1, _nextWindow, _lastReportAt, _lastFrameAt;
        static long _lastReportImp;

        static void Log(string s) => Mod.Log.Msg("[SANTE] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_SanteCombat");
            _version = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _proofs = c.CreateEntry("PreuvesBalistique", 0, description: Build.Desc("Sécurité automatique (batailles où les tirs ont repris juste après le retrait de la balistique réelle), ne pas modifier"));
            _alerts = c.CreateEntry("AlertesLotsDonnees", 0, description: Build.Desc("Sécurité automatique (batailles restées sans aucun tir au contact avec les vraies portées), ne pas modifier"));
            _suspended = c.CreateEntry("LotsSuspendus", "", description: Build.Desc("Sécurité automatique (lots des vraies portées suspendus pour cette version), ne pas modifier"));
            _escapeMean = c.CreateEntry("FuiteArtillerieMoyenne", -1f, description: Build.Desc("Mesure automatique (déplacement moyen de l'artillerie IA après un tir, en mètres), ne pas modifier"));
            _escapeSamples = c.CreateEntry("FuiteArtillerieMesures", 0, description: Build.Desc("Mesure automatique (nombre de déplacements mesurés), ne pas modifier"));
            if (_version.Value != Identite.Version)
            {
                _version.Value = Identite.Version;
                _proofs.Value = 0;
                _alerts.Value = 0;
                _suspended.Value = "";
            }
            RealStats.LotsSuspendusSource = SuspendedLots;                           // read by the lot validation at every database apply
        }

        // ---------------------------------------------------------------- contract with Realism and the other modules

        /// Lots that must not be applied: suspended for this version, real ballistics after two proofs, and real ballistics while the
        /// battle where it was taken out still runs.
        internal static List<string> SuspendedLots()
        {
            var res = new List<string>();
            if (_suspended != null)
                foreach (var k in (_suspended.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) res.Add(k.Trim());
            if (_proofs != null && _proofs.Value >= ProofsToStop) res.Add(Realism.LotBallistics);
            else if (CutInRunningBattle()) res.Add(Realism.LotBallistics);
            return res;
        }

        /// Why a lot is off because of this watchdog (null when it is not).
        internal static string SuspendReason(string key)
        {
            if (key == null) return null;
            if (key.Equals(Realism.LotBallistics, StringComparison.OrdinalIgnoreCase))
            {
                if (_proofs != null && _proofs.Value >= ProofsToStop) return $"sécurité des tirs, version {Identite.Version}";
                if (CutInRunningBattle()) return "retirée dans la bataille en cours";
            }
            if (_suspended != null && (_suspended.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(s => s.Trim().Equals(key, StringComparison.OrdinalIgnoreCase)))
                return $"suspendu par la sécurité des tirs, version {Identite.Version}";
            return null;
        }

        static bool CutInRunningBattle()
        {
            if (_cutCtx == IntPtr.Zero) return false;
            var ctx = Campaign.Ctx();
            return ctx != null && ctx.Pointer == _cutCtx;
        }

        /// Another module cut its own rules (e.g. the anti-helicopter watchdog): a fire resumption in the same minute proves nothing
        /// against real ballistics.
        internal static void NoteOtherCut(string module)
        {
            _otherCutAt = _clock;
            _otherCutName = module;
        }

        /// Impacts counted in this battle (0 while the counter is not installed), for the other watchdogs.
        internal static long Impacts => Interlocked.Read(ref _impacts);
        internal static bool ImpactCounterInstalled => _hook;
        internal static bool InContact => _lastContact;

        /// One AI battery move after firing, in metres (measurement of the artillery escape radius, fed by the battle probes).
        internal static void NoteArtilleryEscape(float metres)
        {
            if (_escapeMean == null || float.IsNaN(metres) || metres <= 0f || metres > EscapeMaxMove) return;
            int n = Math.Max(0, _escapeSamples.Value);
            float mean = _escapeMean.Value < 0f || n == 0 ? metres : (_escapeMean.Value * n + metres) / (n + 1);
            _escapeSamples.Value = n + 1;
            _escapeMean.Value = mean;
            _escapeDirty = true;
        }

        /// Measured mean move of AI batteries after firing; true once enough moves were measured.
        internal static bool ArtilleryEscapeMeasure(out float mean, out int samples)
        {
            samples = _escapeSamples?.Value ?? 0;
            mean = _escapeMean?.Value ?? -1f;
            return samples >= EscapeMinSamples && mean >= 0f;
        }

        // ---------------------------------------------------------------- frame

        static int _wait;

        /// Every frame inside a campaign mission played with the mod; works once per second (the frame time is read every frame).
        internal static void Frame()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            float dt = _lastFrameAt > 0f ? now - _lastFrameAt : 0f;                         // real time between two frames
            _lastFrameAt = now;
            if (dt > _frameMax) _frameMax = dt;
            if (now >= _nextWindow)
            {
                _frameWin0 = _frameWin1; _frameWin1 = _frameMax; _frameMax = 0f;
                _nextWindow = now + FrameWindow;
            }
            if (now < _nextTick) return;
            if (!Planif.Take(ref _wait, _battle ? Planif.WaitNormal : Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            _nextTick = now + TickEvery;

            var ctx = Campaign.Ctx();
            bool player = false;
            try { player = ctx != null && ctx.CurrentPlayer != null; } catch { }
            if (!player) { if (_battle) EndBattle("plus de partie"); _cutCtx = IntPtr.Zero; return; }
            if (!_battle || ctx.Pointer != _ctx)
            {
                if (_battle) EndBattle("nouvelle partie");
                if (_cutCtx != ctx.Pointer) _cutCtx = IntPtr.Zero;
                StartBattle(ctx.Pointer, now);
            }
            if (!Solo()) return;

            if (!_settingsDone && now >= _nextSettings && Realism.RealModeOn)
            {
                _nextSettings = now + WatchEvery;
                try { CheckEngineSettings(); }
                catch (Exception e) { _settingsDone = true; Mod.Log.Msg("[CALIBRAGE] réglages du calculateur balistique illisibles : " + e.GetBaseException().Message); }
            }
            if (now >= _nextWatch)
            {
                _nextWatch = now + WatchEvery;
                if (!_hook && !_hookFailed && Guarded()) InstallHook();
                if (_hook && _watchErrors < MaxWatchErrors)
                {
                    try { Watch(); }
                    catch (Exception e)
                    {
                        if (++_watchErrors >= MaxWatchErrors)
                        {
                            Mod.Log.Warning("[SANTE] chien de garde : erreurs répétées (" + e.GetBaseException().Message + ") : surveillance arrêtée pour cette bataille, balistique réelle retirée par sécurité");
                            if (GuardBallistics()) CutBallistics("surveillance des tirs arrêtée");
                        }
                    }
                }
            }
            if (now >= _nextReport) { _nextReport = now + ReportEvery; try { Report(now); } catch { } }
        }

        internal static void OnQuit()
        {
            EndBattle("fermeture du jeu");
            SaveMeasure();
        }

        /// Called every 2 s outside a campaign mission: the battle is over and a lot cut in it may come back at the next apply.
        internal static void OutOfCampaign()
        {
            if (_battle) EndBattle("hors campagne");
            _cutCtx = IntPtr.Zero;
            _lastFrameAt = 0f;
        }

        static void StartBattle(IntPtr ctx, float now)
        {
            _battle = true; _ctx = ctx; _online = false; _endLogged = false;
            Interlocked.Exchange(ref _impacts, 0);
            _cutHere = _cutCtx != IntPtr.Zero && _cutCtx == ctx;
            _nextWatch = now + WatchEvery; _nextReport = now + ReportEvery; _nextSettings = now + WatchEvery;
            _watchErrors = 0; _settingsTries = 0; _settingsDone = false;
            _pos[0].Clear(); _pos[1].Clear(); _planeByUid.Clear(); _sig = 0; _sigCount = -1; _scanErrors = 0; _lastContact = false;
            _clock = 0f; _prevTime = -1f; _lastImpactAt = 0f; _tripAt = 0f; _otherCutAt = float.NegativeInfinity; _otherCutName = null;
            _lastImp = -1; _impPrevTick = 0; _impAtTrip = 0; _impAtGrace = 0;
            _checks = _contactChecks = _postChecks = _postContact = _postMoving = 0;
            _fired = _tripped = _tripB = _quietUntilImpact = _alertThisBattle = _silenceNoted = false;
            _tripData = new List<string>();
            _holdPrev = _holdBad = 0; Array.Clear(_holdStrike, 0, _holdStrike.Length); _endHiddenSeen = false;
            _antiHeliPrev = false;
            _frameMax = _frameWin0 = _frameWin1 = 0f; _nextWindow = now + FrameWindow;
            _lastReportAt = now; _lastReportImp = 0;
        }

        static void EndBattle(string why)
        {
            if (!_battle) return;
            _battle = false;
            if (_hook)
            {
                long imp = Interlocked.Read(ref _impacts);
                Log($"fin de bataille ({why}) : {imp} impacts" + (_cutHere ? ", balistique réelle retirée pendant la bataille" : "") + (_alertThisBattle ? ", alerte des lots de données" : "") +
                    (_tripped ? " ; bataille finie pendant la vérification après silence (rien n'est enregistré)" : ""));
            }
            _tripped = false;
            SaveMeasure();
        }

        static void SaveMeasure()
        {
            if (!_escapeDirty) return;
            _escapeDirty = false;
            try { MelonPreferences.Save(); } catch { }
        }

        static bool Solo()
        {
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _online = true;
            }
            catch { return false; }
            return !_online;
        }

        static bool GuardBallistics() => !_cutHere && Realism.IsApplied && RealStats.LotAccepte(Realism.LotBallistics);
        static bool Guarded() => Realism.RealModeOn && (GuardBallistics() || AcceptedDataLots().Count > 0);

        /// Data lots of this version accepted on the applied database (every lot but real ballistics).
        static List<string> AcceptedDataLots()
        {
            var res = new List<string>();
            if (!Realism.IsApplied) return res;
            foreach (var k in RealStats.LotsIntroduits)
                if (!k.Equals(Realism.LotBallistics, StringComparison.OrdinalIgnoreCase) && RealStats.LotAccepte(k)) res.Add(k);
            return res;
        }

        static string Tags(IEnumerable<string> keys) =>
            string.Join(" ", keys.Select(k => k.StartsWith("OPTION_", StringComparison.OrdinalIgnoreCase) ? k.Substring(7) : k));

        // ---------------------------------------------------------------- impact counter

        static void InstallHook()
        {
            try
            {
                var hit = AccessTools.Method(typeof(ShellHit), "InternalUpdate");
                if (hit == null) throw new MissingMethodException("ShellHitSystem.InternalUpdate");
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.SanteCombat");
                _harmony.Patch(hit, prefix: new HarmonyMethod(typeof(SanteCombat).GetMethod(nameof(ImpactPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                _hook = true;
                Log("compteur d'impacts installé : chien de garde des vraies portées actif");
            }
            catch (Exception e)
            {
                _hookFailed = true;
                Mod.Log.Warning("[SANTE] compteur d'impacts non installé (" + e.GetBaseException().Message + ") : SANS compteur d'impacts, la balistique réelle est retirée à chaque bataille");
                if (GuardBallistics()) CutBallistics("pas de compteur d'impacts");
            }
        }

        /// ShellHitSystem.InternalUpdate(Entity& shellEntity): one impact processed (any thread). Counter only.
        static void ImpactPrefix() { if (!Campaign.MissionInerte) Interlocked.Increment(ref _impacts); }

        // ---------------------------------------------------------------- watchdog

        /// Every 5 s (main thread): contact, clock, trip, then the verdict after a trip.
        static void Watch()
        {
            long imp = Interlocked.Read(ref _impacts);
            bool contact = ScanContact(out bool moving);
            _lastContact = contact;
            PollAntiHeliCut();

            float t = UnityEngine.Time.time;
            float step = _prevTime >= 0f ? t - _prevTime : 0f;
            _prevTime = t;
            if (!(step > 0f)) step = 0f; else if (step > MaxStep) step = MaxStep;

            int hold = HoldSignals();
            int both = hold & _holdPrev;
            bool live = imp != _impPrevTick || moving;
            int bad = 0;
            for (int i = 0, bit = 1; i < _holdStrike.Length; i++, bit <<= 1)
            {
                _holdStrike[i] = (both & bit) != 0 && live ? _holdStrike[i] + 1 : 0;
                if (_holdStrike[i] >= HoldStrikes) bad |= bit;
            }
            if (bad != 0)
            {
                _holdBad |= bad; hold &= ~bad;
                Log($"chien de garde : le jeu continue (tirs ou mouvements) à {HoldStrikes} contrôles de suite alors qu'il semble {((bad & HoldEnd) != 0 ? "en fin de bataille" : "en pause")} : ce signal est ignoré pour cette bataille");
            }
            _holdPrev = hold; _impPrevTick = imp;
            if (hold != 0)
            {
                if ((hold & HoldEnd) != 0 && _tripped && !_endLogged)
                {
                    _endLogged = true;
                    Log("chien de garde : écran de fin de bataille pendant la vérification après silence : rien n'est enregistré");
                }
                return;                                                                  // the watchdog clock does not run
            }
            _clock += step;
            float g = _clock;

            if (_lastImp < 0) { _lastImp = imp; _lastImpactAt = g; }
            if (imp != _lastImp)
            {
                _lastImp = imp; _lastImpactAt = g; _fired = true;
                _checks = 0; _contactChecks = 0; _quietUntilImpact = false;
            }
            _checks++;
            if (contact) _contactChecks++;

            if (_tripped) { AfterTrip(g, imp, contact, moving); return; }
            if (!_fired || _quietUntilImpact) return;
            if (g - _lastImpactAt < Silence || _checks < MinChecks || _contactChecks < ContactShare * _checks) return;

            bool b = GuardBallistics();
            var data = AcceptedDataLots();
            if (!b && (data.Count == 0 || _alertThisBattle))
            {
                _quietUntilImpact = true;
                if (!_silenceNoted) { _silenceNoted = true; Log($"chien de garde : aucun impact depuis {g - _lastImpactAt:0} s au contact, rien à retirer (balistique réelle {(_cutHere ? "déjà retirée" : "absente")})"); }
                return;
            }
            Trip(g, imp, b, data);
        }

        static void Trip(float g, long imp, bool b, List<string> data)
        {
            float silence = g - _lastImpactAt;
            _tripped = true; _tripAt = g; _impAtTrip = imp; _impAtGrace = imp; _tripB = b; _tripData = data;
            _postChecks = _postContact = _postMoving = 0; _endLogged = false;
            if (b)
            {
                int n = CutBallistics(null);
                Mod.Log.Warning($"[SANTE] chien de garde : aucun impact depuis {silence.ToString("0", Inv)} s alors que les deux camps sont au contact : balistique réelle retirée pour cette bataille " +
                                $"({n} valeur(s) remise(s) : gravité, vitesses, copies des unités)");
                Mod.Notify(TxtKey.N_BALLISTICS_CUT);
            }
            if (data.Count > 0)
                Log($"chien de garde : silence de {silence.ToString("0", Inv)} s au contact avec les lots de données {Tags(data)} : jamais retirés en pleine bataille, " +
                    $"une alerte seulement si le silence dure encore {ResumeWindow.ToString("0", Inv)} s");
        }

        /// Takes real ballistics out for the running battle: the lot's rows and gravity (RealStats), the engine values written with it
        /// (Realism), then the per-unit copies of every loaded unit take the table values again through the copies module's own walk
        /// (per-unit rules kept). why == null: the caller logs. Returns the values put back on the rows and the engine settings.
        static int CutBallistics(string why)
        {
            string reason = why ?? "sécurité des tirs";
            _cutHere = true;
            _cutCtx = _ctx;
            int n = 0, units = 0;
            try { n += RealStats.RestoreLot(Realism.LotBallistics, reason); }
            catch (Exception e) { Mod.Log.Warning("[SANTE] retrait des vitesses réelles impossible : " + e.GetBaseException().Message); }
            try { n += Realism.RestoreLot(Realism.LotBallistics, reason); }
            catch (Exception e) { Mod.Log.Warning("[SANTE] retrait de la gravité des missiles impossible : " + e.GetBaseException().Message); }
            try { units = SyncLoadedCopies(); }
            catch (Exception e) { Mod.Log.Warning("[SANTE] copies des unités non remises : " + e.GetBaseException().Message); }
            float g = float.NaN;
            try { g = GameCfg.Instance?.BattleSystemSettings?.G ?? float.NaN; } catch { }
            Log($"balistique réelle retirée : {n} valeur(s) remise(s), copies de {units} unité(s) chargée(s) resynchronisées, pesanteur {g.ToString(Inv)}");
            if (why != null)
            {
                Mod.Log.Warning($"[SANTE] balistique réelle retirée pour cette bataille ({why})");
                Mod.Notify(TxtKey.N_BALLISTICS_CUT);
            }
            return n;
        }

        /// Every loaded unit goes through the copies module's walk again, so its copies take the current table values (journaled).
        static int SyncLoadedCopies()
        {
            var loader = Il2CppBrokenArrow.Shared.Ecs.DataBaseService._instance?.UnitsLoader;
            var loaded = loader?._loadedUnits;
            if (loaded == null) return 0;
            var units = new List<Il2CppBrokenArrow.DataBase.Models.Units>();
            foreach (var kv in loaded) if (kv.Value != null) units.Add(kv.Value);
            foreach (var u in units) UnitCopies.SyncShown(u, "balistique réelle retirée");
            return units.Count;
        }

        static void AfterTrip(float g, long imp, bool contact, bool moving)
        {
            float since = g - _tripAt;
            _postChecks++;
            if (contact) _postContact++;
            if (moving) _postMoving++;
            if (since < Grace) { _impAtGrace = imp; return; }                           // rounds already in flight land during the grace
            if (imp > _impAtGrace)
            {
                _tripped = false;
                if (_tripB && since <= ResumeWindow) Proof(since);
                else if (_tripB) Log($"chien de garde : les tirs ont repris {since.ToString("0", Inv)} s après le retrait de la balistique réelle, trop tard pour l'accuser (rien n'est enregistré)");
                else Log($"chien de garde : les tirs ont repris {since.ToString("0", Inv)} s après le silence : les lots de données ne sont pas mis en cause");
                return;
            }
            if (since < ResumeWindow) return;
            _tripped = false;
            _quietUntilImpact = true;
            if (_tripB) Log($"chien de garde : toujours aucun tir {since.ToString("0", Inv)} s après le retrait de la balistique réelle : le silence ne venait pas d'elle (retirée jusqu'à la fin de la bataille, rien n'est enregistré)");
            if (_tripData.Count == 0 || _alertThisBattle) return;
            bool inContact = _postContact >= ContactShare * _postChecks, running = _postMoving >= ContactShare * _postChecks;
            if (!inContact || !running)
            {
                Log($"chien de garde : silence prolongé sans conclusion pour les lots de données (contact {_postContact}/{_postChecks}, unités en mouvement {_postMoving}/{_postChecks}) : rien n'est enregistré");
                return;
            }
            Alert(g - _lastImpactAt);
        }

        /// Fire resumed soon after real ballistics was taken out: one proof against it (this version).
        static void Proof(float since)
        {
            if (Math.Abs(_otherCutAt - _tripAt) <= OtherCutWindow)
            {
                Log($"chien de garde : les tirs ont repris {since.ToString("0", Inv)} s après le retrait de la balistique réelle, mais {_otherCutName ?? "un autre module"} a coupé ses règles dans la même minute : rien n'est enregistré");
                return;
            }
            _proofs.Value = _proofs.Value + 1;
            try { MelonPreferences.Save(); } catch { }
            bool stop = _proofs.Value >= ProofsToStop;
            Mod.Log.Warning($"[SANTE] chien de garde : les tirs ont repris {since.ToString("0", Inv)} s après le retrait de la balistique réelle : preuve enregistrée ({_proofs.Value}/{ProofsToStop})" +
                            (stop ? $", balistique réelle coupée pour la version {Identite.Version}" : ", elle revient à la prochaine bataille"));
            if (stop) Mod.Notify(TxtKey.N_BALLISTICS_OFF_VERSION);
        }

        /// Fire stayed silent after the trip with the data lots on: one alert (this version); the second one suspends them.
        static void Alert(float silence)
        {
            _alertThisBattle = true;
            _alerts.Value = _alerts.Value + 1;
            bool suspend = _alerts.Value >= AlertsToSuspend;
            if (suspend)
            {
                var set = new List<string>((_suspended.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                foreach (var k in _tripData) if (!set.Any(s => s.Equals(k, StringComparison.OrdinalIgnoreCase))) set.Add(k);
                _suspended.Value = string.Join(",", set);
            }
            try { MelonPreferences.Save(); } catch { }
            Mod.Log.Warning($"[SANTE] chien de garde : aucun tir pendant {silence.ToString("0", Inv)} s au contact, unités en mouvement, avec les lots de données {Tags(_tripData)} : alerte {_alerts.Value}/{AlertsToSuspend}" +
                            (suspend ? $", lots suspendus à partir de la prochaine mission (version {Identite.Version})" : " (rien n'est retiré en pleine bataille)"));
            Mod.Notify(suspend ? TxtKey.N_RANGES_SUSPENDED : TxtKey.N_RANGES_WATCHED);
        }

        /// Signals that stop the watchdog clock, minus those proven wrong in this battle: the battle end screen (Campaign.BattleOver, or
        /// the screen's flag once it was seen down in this battle), the session's pause flag and a session speed of 0.
        static int HoldSignals()
        {
            int hold = 0;
            if ((_holdBad & HoldEnd) == 0)
            {
                bool end = Campaign.BattleOver;
                if (!end)
                {
                    try
                    {
                        bool active = EndScreen.Active;
                        if (!active) _endHiddenSeen = true;
                        else end = _endHiddenSeen;
                    }
                    catch { }
                }
                if (end) hold |= HoldEnd;
            }
            if ((_holdBad & (HoldPause | HoldScale)) != (HoldPause | HoldScale))
            {
                try
                {
                    var session = Campaign.Ctx();
                    if (session != null)
                    {
                        if ((_holdBad & HoldPause) == 0 && session.IsPaused) hold |= HoldPause;
                        if ((_holdBad & HoldScale) == 0 && session.TimeScale <= 0f) hold |= HoldScale;
                    }
                }
                catch { }
            }
            return hold;
        }

        /// Contact: a live unit of each side (planes left out) within 3 km of each other. moving: the sum of positions changed since the
        /// previous scan (a frozen or paused battle does not move).
        static bool ScanContact(out bool moving)
        {
            _map ??= new LuaMap();
            double sig = 0; int cnt = 0;
            for (int side = 0; side < 2; side++)
            {
                var list = _pos[side];
                list.Clear();
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LuaUnit> arr = null;
                try { arr = _map.GetUnits(V3.zero, 1_000_000f, side, -1); } catch { _scanErrors++; }
                int n = arr?.Length ?? 0;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var u = arr[i];
                        if (u == null || !u.IsAlive()) continue;
                        int uid = u.UID;
                        if (!_planeByUid.TryGetValue(uid, out bool plane))
                        {
                            int role = u.UnitRole;
                            plane = role >= RolePlaneMin && role <= RolePlaneMax;
                            if (_planeByUid.Count < 20000) _planeByUid[uid] = plane;
                        }
                        if (plane) continue;
                        var p = u.GetPosition();
                        list.Add(p);
                        sig += p.x + 3.0 * p.z;
                        cnt++;
                    }
                    catch { _scanErrors++; }
                }
            }
            moving = _sigCount >= 0 && (cnt != _sigCount || Math.Abs(sig - _sig) > 0.5);
            _sig = sig; _sigCount = cnt;
            float r2 = ContactRange * ContactRange;
            var a = _pos[0]; var b = _pos[1];
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                {
                    float dx = a[i].x - b[j].x, dz = a[i].z - b[j].z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            return false;
        }

        /// The anti-helicopter watchdog cutting its rules (rising edge of its trip flag) counts as another module's cut, until it reports
        /// its cuts itself through NoteOtherCut.
        static void PollAntiHeliCut()
        {
            if (!_antiHeliLooked)
            {
                _antiHeliLooked = true;
                try
                {
                    var f = typeof(AntiHeliPortee).GetField("_wdTripped", BindingFlags.NonPublic | BindingFlags.Static);
                    _antiHeliTripped = f != null && f.FieldType == typeof(bool) ? f : null;
                }
                catch { _antiHeliTripped = null; }
            }
            if (_antiHeliTripped == null) return;
            bool cut;
            try { cut = (bool)_antiHeliTripped.GetValue(null); } catch { return; }
            if (cut && !_antiHeliPrev) NoteOtherCut("le tir anti-hélico");
            _antiHeliPrev = cut;
        }

        // ---------------------------------------------------------------- engine settings (read only)

        /// Once per battle, when the ballistics helper holds its settings: the settings objects the engine uses and their gravity, then
        /// the solver probe. Real ballistics is taken out when an engine settings object kept another gravity.
        static void CheckEngineSettings()
        {
            Settings helper = null;
            try { helper = Ballistics._battleSettings; } catch { }
            if (helper == null)
            {
                if (++_settingsTries >= SettingsTries)
                {
                    _settingsDone = true;
                    Mod.Log.Msg("[CALIBRAGE] calculateur balistique jamais initialisé dans les 2 premières minutes : sonde non faite");
                }
                return;
            }
            _settingsDone = true;
            Settings main = null, spawner = null, helpers = null;
            try { main = GameCfg.Instance?.BattleSystemSettings; } catch { }
            try { spawner = Spawner._battleSettings; } catch { }
            try { helpers = BSH._battleSettings; } catch { }
            string Same(Settings s) => s == null ? "non initialisés" : main != null && s.Pointer == main.Pointer ? "oui" : "non (G=" + s.G.ToString(Inv) + ")";
            Mod.Log.Msg($"[CALIBRAGE] calculateur balistique : réglages de GameConfig {Same(helper)} ; tirs {Same(spawner)} ; aides de combat {Same(helpers)} ; G={helper.G.ToString(Inv)}");

            if (GuardBallistics())
            {
                var wrong = new List<string>();
                foreach (var (name, s) in new[] { ("calculateur", helper), ("tirs", spawner), ("aides", helpers) })
                    if (s != null && Math.Abs(s.G - 9.81f) > 0.01f) wrong.Add($"{name} G={s.G.ToString(Inv)}");
                if (wrong.Count > 0) CutBallistics("le moteur garde une autre gravité : " + string.Join(", ", wrong));
            }
            Probe(helper);
        }

        /// CalculateBallisticsAngle on four horizontal cases with the default gravity multiplier: can it shoot, fire angle tangent, time.
        static void Probe(Settings helper)
        {
            float mult;
            try { mult = BSConst.DEFAULT_GRAVITY_MULT; } catch { mult = 1f; }
            var parts = new List<string>();
            foreach (var (d, v) in new[] { (1700f, 200f), (8000f, 450f), (5000f, 550f), (3000f, 1700f) })
            {
                try
                {
                    var data = new BallisticData(new V3(d, 0f, 0f), v, mult);
                    Ballistics.CalculateBallisticsAngle(ref data);
                    parts.Add($"{d.ToString("0", Inv)} m à {v.ToString("0", Inv)} m/s : tir {(data.CanShoot ? "oui" : "non")} tan={data.FireAngleTan.ToString("0.####", Inv)} temps={data.TimeToImpact.ToString("0.##", Inv)} s");
                }
                catch (Exception e)
                {
                    parts.Add($"{d.ToString("0", Inv)} m à {v.ToString("0", Inv)} m/s : erreur ({e.GetBaseException().Message})");
                    break;
                }
            }
            Mod.Log.Msg($"[CALIBRAGE] solveur balistique (G={helper.G.ToString(Inv)}, gravité x{mult.ToString(Inv)}) : " + string.Join(" ; ", parts));
        }

        // ---------------------------------------------------------------- report

        static void Report(float now)
        {
            string frames = $"plus longue image {(_frameWin0 * 1000f).ToString("0", Inv)} puis {(_frameWin1 * 1000f).ToString("0", Inv)} ms (fenêtres de 30 s)";
            if (!_hook)
            {
                Log($"relevé : {frames} ; " + (_hookFailed ? "compteur d'impacts absent" : "aucun lot des vraies portées à surveiller"));
                return;
            }
            long imp = Interlocked.Read(ref _impacts);
            float span = now - _lastReportAt;
            long delta = imp - _lastReportImp;
            _lastReportImp = imp; _lastReportAt = now;
            long perMin = span > 1f ? (long)Math.Round(delta * 60.0 / span) : 0;
            var guarded = new List<string>();
            if (GuardBallistics()) guarded.Add(Realism.LotBallistics);
            guarded.AddRange(AcceptedDataLots());
            Log($"relevé : {perMin} impacts/min (total {imp}), contact {(_lastContact ? "oui" : "non")}, silence {(_fired ? (_clock - _lastImpactAt).ToString("0", Inv) + " s" : "-")} ; {frames} ; " +
                $"lots surveillés : {(guarded.Count == 0 ? "aucun" : Tags(guarded))}" + (_cutHere ? " ; balistique réelle retirée" : "") + (_tripped ? " ; vérification après silence en cours" : "") +
                (_scanErrors > 0 ? $" ; {_scanErrors} lecture(s) d'unité en erreur" : ""));
        }
    }
}
