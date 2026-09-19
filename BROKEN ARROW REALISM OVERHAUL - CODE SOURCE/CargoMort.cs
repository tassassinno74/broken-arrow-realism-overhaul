// RealismOverhaul - CargoMort: the squad burns with its carrier.
//
//  WHAT THE AUTHOR ASKED, in his own words and more than once: "il faut absolument que quand un véhicule est détruit et qu'il y a une
//  unité dedans, elle meure avec". His rule, precisely: an ATGM into a light carrier kills the squad inside; a heavily armoured
//  transport may leave a few survivors at very low health. It is NOT an option - it is the behaviour. So there is no Mod tab row here,
//  no preference to switch it on, nothing to choose. It applies, or it says in the log why it did not.
//
//  THE GAME ALREADY HAS THE MECHANISM, unused at realistic values. GameConfig carries nine values for it, and this module is the first
//  thing in the mod that writes any of them:
//      Int32  OnTransportDeathCargoForcedStressPercentage
//      Int32  OnTransportDeathCargoKillFloor          Single OnTransportDeathCargoKillMultiplier
//      Int32  OnTransportDeathCargoKillHLFloor        Single OnTransportDeathCargoKillHLMultiplier
//      Int32  OnTransportDeathCargoKillHHFloor        Single OnTransportDeathCargoKillHHMultiplier
//      Int32  OnTransportDeathCargoKillAirFloor       Single OnTransportDeathCargoKillAirMultiplier
//
//  WHAT IS KNOWN, and exactly how well it is known. The dumps carry signatures only, never the code of a method, so the place where
//  the engine READS these nine values could not be found and is not claimed anywhere in this file or in its log lines:
//   1. Everything points at the family firing on transport DEATH and nowhere else. UnloadingComponent has two constructors:
//        .ctor(Nullable<Entity> unloadTarget, Nullable<Entity> loadToEntity, Nullable<Vector3> postLoadTarget,
//              Nullable<Vector3> pointOverride, Single forcedStressProportion)   <- a soldier getting out on purpose
//        .ctor(Single lastDamageProportion, Single forcedStressProportion)       <- a soldier whose transport just died
//      The second one carries LastDamageProportion, and nothing else in the game carries that field. A separate config value,
//      OnTransportUnloadCargoForcedStressPercentage, exists for the ordinary dismount. Two different constructors with two different
//      inputs, and a name that says "OnTransportDeath": that is the whole of the evidence. It is a strong reading, it is NOT a proof,
//      and the log says so in those words rather than promising the author two bounds nobody has verified.
//   2. LastDamageProportion is a proportion (Single) and it is the only per-event number the death path has. The killing blow's share
//      of the transport is therefore what the game weighs the cargo's fate against - which is the author's rule already expressed in
//      the engine: an ATGM that one-shots a light carrier is a proportion at or above 1; the same ATGM into a heavy hull with three
//      times the health points is a much smaller one. The light/heavy split falls out of MaxHealthPoints by itself, so no table of
//      armour classes is written here and none is needed: whatever the real-stats pass puts into Blindages_override.csv
//      (MaxHealthPoints, KinArmorFront/Sides/Rear/Top, HeatArmor*) the split follows it, including future edits.
//   3. "Floor + Multiplier x something" is this studio's shape, not a reading of the word. BuildingsConfig does exactly that for a
//      garrisoned squad (DamageModifierFloor + DamageModifierPerSoldier x soldiers, vanilla 0.18 / 0.02, written by Mod.cs). A bigger
//      multiplier therefore means more of the cargo dies. That direction is the only thing this module relies on.
//
//  WHAT IS ONLY INFERRED, and what the module does about it:
//   a. HL / HH / Air. Those letters appear NOWHERE else in the whole game. The game does have exactly the matching categories -
//      HelicopterFlyMode { LowAltitude, HighAltitude } and GlobalAltitude { Ground, Low, High }, planes being the "Air" transports -
//      so HL = helicopter low, HH = helicopter high, Air = plane, and the pair with no suffix = ground transport. It fits; it is not
//      proved. Acting on it is safe anyway: the four pairs are each scaled from their OWN value, so even if two labels were swapped
//      the change would still read "a transport that dies takes more of its cargo with it", which is the rule.
//   b. The unit of the four Floors. An Int32 beside a Single multiplier is either a percentage or a number of men, and the dumps
//      settle neither. This is the one place where being wrong is a real regression: a Floor of 60 read as a percentage means "most
//      of the squad dies"; read as a count it means "every squad dies, in every transport, heavy hulls included" - exactly the
//      survivors the author wants kept. So the four Floors are READ, LOGGED, AND NEVER WRITTEN. Same for
//      OnTransportDeathCargoForcedStressPercentage: it is stress, not a kill, and the author's "survivors at very low health" is
//      carried by the game's own LastDamageProportion, which is not a config value and is not touched.
//   c. The scale of the multipliers. If the result is a percentage the shipped values are tens; if it is a proportion they are
//      fractions. The module never reads the scale, because it multiplies the game's OWN value, so the DIRECTION of the change is
//      right either way. The SIZE of it is not: with a Floor of 60 that this file deliberately does not touch, a big multiplier can
//      take a heavy hull losing 40 % of its health from "most of the squad dies" to "the whole squad dies" and wipe out exactly the
//      survivors the author asked to keep. So the step is deliberately small, and the same one everywhere:
//          x2 on the game's own value, and nothing else (author's decision of 19/09/2026, after review).
//      x3 and x4 were in the first version of this file. They were cut back because the nine values had never appeared in one single
//      log - the line that carried them, AntiHeliPortee's, was dropped by the PUBLIC filter - and a factor whose effect depends on an
//      unread Floor is a guess. Since this build the measurement line below is written in EVERY log, PUBLIC included, so the next log
//      gives the real numbers and the factor can be set on them instead of on a reading. The same x2 applies to helicopters high and
//      planes: "destroyed in flight kills everyone whatever the size of the hit" would need the Floor, the Floor is not written here,
//      and a bigger multiplier does not say that sentence - it only says "more", louder.
//   d. The ceiling. 100 is a ceiling on the percentage reading only: on the proportion reading a vanilla 0.9 doubled gives 1.8, outside
//      the legal [0, 1] of a proportion, and what the engine does with a kill proportion of 1.8 is unknown. So the ceiling is deduced
//      from the game's own value: at most 1.5 it is treated as a proportion and bounded at 1, above that as a percentage and bounded
//      at 100. Reading the scale the wrong way here can only ever write LESS than the other reading would, never more.
//
//  BOUNDS, which are the whole safety of the file:
//   - NEVER lowered. A game that already wipes its cargo keeps its own value.
//   - NEVER written above the ceiling deduced from the game's own value (1 on the proportion reading, 100 on the percentage one).
//   - A multiplier the game left at zero is LEFT at zero: zero has no scale, so scaling it says nothing and replacing it would be a
//     guess. A multiplier already at or above the ceiling is left alone too - the game already does what the author asked.
//   - Anything outside [0, 1000] means the field is not what this file thinks it is: NOTHING is written and the log says so.
//   - All or nothing on the read-back: the four values are written, then read back, and if a single one did not take, the whole lot
//     goes back to the game. Half a rule is worse than none.
//   - The base is always the game's own value and never ours: the first value ever seen for a GameConfig object is kept, and the base
//     can never exceed it, so a restore that silently failed can never compound into x4.
//
//  WHERE IT APPLIES, the same three gates as the mod's two other GameConfig writers (Epaves.cs, Meteo.cs), and for the same reasons:
//   - Campaign mission only (Campaign.InCampaign): nothing is written and nothing is said in the main menu, the hangar or a skirmish.
//   - Never in a mission played without the mod (Campaign.MissionInerte).
//   - Solo only. The PUBLIC build blocks multiplayer outright, the personal one does not, and a campaign-tuned simulation value has no
//     business in one client of an online battle. The network state is read exactly as Epaves.Solo() reads it, latched for the battle.
//  Any of the three turning against the module while the values are in place gives them back at once - that is what the frame beat is
//  for, below.
//
//  HOW IT RUNS. No ECS component read anywhere; the per-frame path is a clock read and four float comparisons at most.
//   - The measurement and the write ride the three database-load methods Mod.cs already patches (DataBaseService.LoadCompiled /
//     LoadScenario / LoadByJson). Their PREFIX is Realism.Restore, so by the time this postfix runs the journal is empty and the
//     game's own values are back in place - which is what makes reading the vanilla value at every mission correct instead of
//     compounding. No new hook surface: those three are already detoured.
//   - Tick() is called every frame from ModTab.Frame, which Mod.OnUpdate runs BEFORE its own Mod.Actif check, precisely so a
//     GameConfig writer can hand the game its values back the moment the mod is switched off from its tab in the middle of a battle
//     (the pattern Epaves.cs already follows, ModTab.cs:174-177). Self-throttled to one beat every half second. It is also the path
//     that writes when the database load came too early - before the campaign service knew which mission was starting.
//   - ChienDeGarde() runs the same beat from NodeDelay.OnActivated, already patched by DelaisMission.cs and parameterless, so the
//     module keeps working even if nothing wires Tick(). Self-throttled to one check every two seconds. It puts the values back if
//     the game took them over (Realism.Apply runs in the same postfix chain as the write, and postfix order between two patch classes
//     is not guaranteed).
//   - The per-battle line rides NodeEndMission.OnMissionEnd, already patched by DelaisMission.cs, with a postfix taking no parameter.
//   - Every value goes in through Realism.SetJournaled under this file's own lot, so Realism.Restore (mod switched off, campaign left,
//     mission without the mod, database reloaded, game closed) gives the game its values back without touching anything else.
//   - ONE hook of its own, and it changes nothing: a counter on UnitUnloadingSystem.UnloadSoldier(Entity, Vector3, Boolean). It is
//     unavoidable - the author asked to see the rule working in his log and the game offers no other signal. Its signature is safe and
//     that is checked, not hoped: static, three parameters, ALL BY VALUE, all blittable (Il2CppDefaultEcs.Entity is
//     { Int16 Version, Int16 WorldId, Int32 EntityId }, UnityEngine.Vector3 is three floats, Boolean is a byte). The proof it works on
//     this build is in the author's own log: AntiHeliPortee's GetAmmoShootingDistance postfix takes two Entity BY VALUE and ran
//     6 812 967 times in one battle with zero errors. The body is two Interlocked increments in a try/catch that cannot rethrow; it
//     allocates nothing and reads one volatile field.
//   - NEVER AGAIN, and this file goes nowhere near it: UnitUnloadingSystem.InternalUnload takes "UnloadingComponent& unloadParams", a
//     non-blittable IL2CPP struct BY REFERENCE. Harmony's wrapper has to marshal it whatever the postfix declares, it threw on every
//     single unload, and on 2026-09-18 it cost a player his campaign in RU_C01. It is permanently disabled in AntiHeliPortee.cs with
//     that comment, and nothing here touches it.
//   - Crash guard on that one hook, the same rule AntiHeliPortee uses for the unload system: the counter is armed on disk BEFORE a
//     battle counts anything and cleared when a battle ends normally. Two battles that ended badly with it in place and it is never
//     installed again on this machine. The rest of the module - which is the actual behaviour - does not depend on it in any way.
//   - Error counter with kill-switch: past MaxErreurs the module gives the game its values back and stays quiet for the session.
//     Nothing is ever unpatched.
//
//  WHAT THIS FILE CANNOT DO. It cannot say how many men died inside and how many walked out: that needs the squad's strength before
//  and after, i.e. an ECS component read on the death path, which is forbidden here. So the battle line reports what can be counted
//  honestly - soldiers the game took out of a dying transport, against ordinary dismounts - and says out loud that it does not know
//  which of the first group died afterwards. No figure is invented.
//
//  Log lines are French and start with [CARGO]. Nothing here is shown on screen: a player-visible string would need Txt.cs in five
//  languages, which this file does not own.
//  THE MEASUREMENT LINE IS ALWAYS WRITTEN, PUBLIC BUILD INCLUDED, and it is the whole point of this build: the nine values of the game
//  have never been seen once, and everything above is waiting on them. "[CARGO]" is not in the PUBLIC filter's KeepStarts and this file
//  does not own ModLog.cs, so the line carries "point(s) d'accroche installé(s)" instead - a text of PublicLog.KeepParts, which is kept
//  whatever the prefix. Epaves.cs uses the same idiom for the same reason. Dire() must never stop ending with that clause.
using System;
using System.Globalization;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using DbService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using NodeDelay = Il2CppBrokenArrow.ScriptEngine.Nodes.Timing.NodeDelay;
using NodeEndMission = Il2CppBrokenArrow.ScriptEngine.Nodes.Gameplay.NodeEndMission;
using UnloadSys = Il2CppBrokenArrow.Client.Ecs.Transports.Systems.UnitUnloadingSystem;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    // ---------------------------------------------------------------- hooks
    // The three database loads: their prefix is Realism.Restore, so this postfix always sees the game's own values back in place.
    // Method names are given as strings on purpose: a game update that renames one disables only that patch, with a [PATCH] line.

    [HarmonyPatch(typeof(DbService), "LoadCompiled")]
    static class Patch_CargoBaseCompilee { static void Postfix() => CargoMort.SurBaseChargee(); }

    [HarmonyPatch(typeof(DbService), "LoadScenario")]
    static class Patch_CargoBaseScenario { static void Postfix() => CargoMort.SurBaseChargee(); }

    [HarmonyPatch(typeof(DbService), "LoadByJson")]
    static class Patch_CargoBaseJson { static void Postfix() => CargoMort.SurBaseChargee(); }

    /// A delay node of the mission script starts counting: the module's only heartbeat, self-throttled to one check every two seconds.
    /// Parameterless method, nothing passed by reference.
    [HarmonyPatch(typeof(NodeDelay), "OnActivated")]
    static class Patch_CargoChienDeGarde { static void Postfix() => CargoMort.ChienDeGarde(); }

    /// The mission script ends the mission: the one moment where the battle's line can be said. Postfix takes no parameter at all,
    /// so it cannot break on a renamed argument.
    [HarmonyPatch(typeof(NodeEndMission), "OnMissionEnd")]
    static class Patch_CargoFinMission { static void Postfix() => CargoMort.SurFinDeMission(); }

    /// The only hook of this file, and it changes nothing: one soldier leaves a transport. Static, three parameters, all by value,
    /// all blittable; only the last is declared here. Prepare() refuses the install when the crash guard says so, and a refusal
    /// leaves the game's method completely untouched.
    [HarmonyPatch(typeof(UnloadSys), "UnloadSoldier")]
    static class Patch_CargoSortieSoldat
    {
        // WITHDRAWN 2026-09-19 after a player report: infantry could not leave vehicles OR buildings in campaign, and the
        // parachute drop crashed the game outright. This hook is a pure counter - it changes nothing in the game - but it sits
        // on the exact code path the player describes, and the mod has already broken a campaign once with an unload hook.
        // A counter is not worth that risk. Returning false here leaves the game method completely untouched.
        static bool Prepare() => false;
        static void Postfix(bool onDeathUnload) => CargoMort.SurSoldatSorti(onDeathUnload);
    }

    static class CargoMort
    {
        // ------------------------------------------------------------ the rule, and every number that may ever change
        /// x2 on the game's OWN multiplier, the same everywhere: ground, helicopter low, helicopter high, plane. Author's decision of
        /// 19/09/2026, after review. It is a deliberately small step, because the four Floors that go with these multipliers have
        /// never been read in a log and are not written here: a factor whose effect depends on an unread Floor can destroy the very
        /// survivors the author asked to keep. The measurement line below is now written in every log, PUBLIC build included, and the
        /// factor is meant to be set on those real numbers in the next build.
        const float Facteur = 2f;
        /// The ceiling on the percentage reading: "a hit worth the transport's full health kills the whole squad".
        const float PlafondPourcent = 100f;
        /// The ceiling on the proportion reading: a proportion above 1 has no meaning and the engine's answer to it is unknown.
        const float PlafondProportion = 1f;
        /// Which of the two ceilings applies is deduced from the game's own value, never assumed: a multiplier at or below this can
        /// only be a proportion, above it can only be a percentage. Reading it the wrong way can only write less, never more.
        const float SeuilEchelle = 1.5f;
        /// A multiplier at zero has no scale: scaling it says nothing and replacing it would be a guess. It is left at zero.
        const float MultZero = 0.0001f;
        /// Outside [0, this] the field is not what this file thinks it is: NOTHING is written at all.
        const float MultMaxLu = 1000f;

        const int MaxErreurs = 20;
        /// Past this many writes since the last database load the game is clearly taking the values back at every pass: the module
        /// gives up rather than fight it for ever. Counted per database load, like Epaves counts its own per battle.
        const int MaxEcritures = 30;
        /// Watchdog lines are worth reading the first few times and noise afterwards.
        const int MaxLignesChien = 3;
        /// Two battles that ended badly with the counter in place and it is never installed again.
        const int MaxInterrompues = 2;
        /// The watchdog only looks at four floats, and only this often.
        const long PeriodeChienMs = 2000L;
        /// The frame beat does the same work; twice a second is enough for "the mod was just switched off" to be seen at once.
        const long PeriodeTickMs = 500L;
        const string GuardVersion = "1.0";

        /// Own journal lot: Realism.RestoreLot takes these four writes out without touching a single value of the real stats.
        const string LotCargo = "CARGO_MORT_TRANSPORT";
        const string P = "[CARGO] ";

        internal const string PrefCat = "RealismOverhaul_CargoMort";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------ state
        static bool _mort, _refuse, _ecrit;
        /// A full measurement pass has run since the last database load (so the nine values have been said once for this mission).
        static bool _passeFaite;
        /// Network state, latched for the battle and reset at every database load, exactly as Epaves.cs does it.
        static bool _enLigne, _reseauDit, _solo;
        static int _erreurs, _ecritures, _lignesChien;
        static long _prochainChien, _prochainTick, _prochainReseau;

        static IntPtr _cfgPtr;                       // the GameConfig object we measured (its address cannot be reused while the game holds it)
        static bool _vuPremier;                      // the first values ever seen for that object are recorded
        static float _p0Sol, _p0Hl, _p0Hh, _p0Air;   // first multipliers ever seen: no base may exceed them, so nothing compounds
        static float _cSol, _cHl, _cHh, _cAir;       // what the module last put in place (what the watchdog compares against)

        // the nine values of the pass, as read before anything was written
        static int _floorSol, _floorHl, _floorHh, _floorAir, _choc;
        static float _multSol, _multHl, _multHh, _multAir;

        // ------------------------------------------------------------ counters of the battle (the hook may run off the main thread)
        static long _sortiesMort, _sortiesNormales, _erreursCompteur;
        static bool _bilanDit = true;                // no battle started yet: nothing to say
        static bool _compteurArme, _compteurPose, _compteurRefuse;

        static MelonPreferences_Entry<int> _interrompues;
        static MelonPreferences_Entry<string> _guardVersion;
        static bool _prefsOk;

        static void Log(string s) => Mod.Log.Msg(P + s);

        static string Nb(float v) => v.ToString("0.###", Inv);

        // ============================================================ error counter and kill-switch
        static void Echec(Exception e)
        {
            _erreurs++;
            if (_erreurs <= 3) Mod.Log.Warning(P + "erreur : " + e.GetBaseException().Message);
            if (_erreurs < MaxErreurs || _mort) return;
            _mort = true;
            Rendre(MaxErreurs + " erreurs");
            Mod.Log.Warning(P + MaxErreurs + " erreurs : le chargement d'un transport détruit meurt de nouveau comme dans le jeu "
                            + "d'origine jusqu'au redémarrage (le reste du mod fonctionne)");
        }

        // ============================================================ preferences (crash guard of the one hook)
        /// Entry point for Mod.cs, beside the other modules' CreatePrefs: the module creates its own preferences the first time
        /// anything needs them, so calling it early or not at all changes nothing. Never throws.
        internal static void CreatePrefs()
        {
            try { CreerPrefs(); }
            catch (Exception e) { try { Mod.Log.Warning(P + "réglages non créés : " + e.GetBaseException().Message); } catch { } }
        }

        /// Called from Patch_CargoSortieSoldat.Prepare, i.e. during Mod.ApplyPatches, well after MelonLoader loaded the preferences.
        static void CreerPrefs()
        {
            if (_prefsOk) return;
            _prefsOk = true;
            var c = MelonPreferences.CreateCategory(PrefCat);
            _interrompues = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc(
                "Sécurité automatique, ne pas modifier : batailles qui ne se sont pas terminées normalement avec le comptage des "
                + "sorties de transport détruit en place.", Build.AutoText));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier", Build.AutoText));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _interrompues.Value = 0; }
        }

        static void Sauver()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Mod.Log.Warning(P + "enregistrement des réglages impossible : " + e.GetBaseException().Message); }
        }

        /// Harmony asks whether the counter may be installed. Never throws: a throw here would disable the class.
        internal static bool AccepterCompteur()
        {
            try
            {
                CreerPrefs();
                if (_interrompues != null && _interrompues.Value >= MaxInterrompues)
                {
                    _compteurRefuse = true;
                    Log($"comptage des sorties de transport détruit non installé (les {MaxInterrompues} dernières batailles où il était "
                        + "en place ne se sont pas terminées normalement) : la règle s'applique quand même, seul le comptage manque");
                    return false;
                }
                _compteurPose = true;
                return true;
            }
            catch (Exception e)
            {
                _compteurRefuse = true;
                try { Mod.Log.Warning(P + "comptage non installé (" + e.GetBaseException().Message + ")"); } catch { }
                return false;
            }
        }

        /// A battle is about to be counted: the mark goes to disk BEFORE anything counts, so a game that dies mid-battle leaves it.
        static void ArmerCompteur()
        {
            if (_compteurArme || !_compteurPose || _interrompues == null) return;
            _compteurArme = true;
            try { _interrompues.Value = _interrompues.Value + 1; } catch { }
            Sauver();
        }

        /// The battle ended normally: the mark is cleared.
        static void DesarmerCompteur()
        {
            if (!_compteurArme) return;
            _compteurArme = false;
            if (_interrompues != null && _interrompues.Value != 0) { _interrompues.Value = 0; Sauver(); }
        }

        // ============================================================ the one hook (counter only, nothing else)
        /// UnitUnloadingSystem.UnloadSoldier(Entity, Vector3, Boolean): one soldier leaves a transport. onDeathUnload is the game's own
        /// name for "his transport just died". Field read and Interlocked only, no allocation, cannot rethrow, changes nothing.
        internal static void SurSoldatSorti(bool onDeathUnload)
        {
            try
            {
                if (Campaign.MissionInerte) return;                      // mission played without the mod: nothing is counted
                if (onDeathUnload) Interlocked.Increment(ref _sortiesMort);
                else Interlocked.Increment(ref _sortiesNormales);
            }
            catch { Interlocked.Increment(ref _erreursCompteur); }
        }

        // ============================================================ entry points
        /// A database has just been loaded: Realism.Restore ran as the prefix of this very method, so the game's own values are back in
        /// place and the previous battle, if there was one, is over.
        internal static void SurBaseChargee()
        {
            if (_mort) return;
            try
            {
                Bilan("nouvelle base chargée", false);
                // reaching another database load means the process survived the previous battle, whatever ended it (mission over,
                // quit to the menu, restart): the counter's crash guard only exists for a game that DIED with the hook in place.
                DesarmerCompteur();
                Interlocked.Exchange(ref _sortiesMort, 0L);
                Interlocked.Exchange(ref _sortiesNormales, 0L);
                _bilanDit = false;
                _ecrit = false;                                          // whatever we had written was taken out by the prefix
                _passeFaite = false;                                     // the nine values are measured and said again for this mission
                _ecritures = 0;                                          // the "the game takes them back" budget is per database load
                _enLigne = false; _reseauDit = false;                    // the network state is read again for this battle
                _cSol = _cHl = _cHh = _cAir = 0f;
                Appliquer(true);
            }
            catch (Exception e) { try { Echec(e); } catch { } }
        }

        /// Every frame, from ModTab.Frame - which Mod.OnUpdate runs BEFORE its own Mod.Actif check. This is the only path that gives
        /// the game its four multipliers back the moment the mod is switched off from its tab in the middle of a battle. Self-throttled
        /// to one beat every half second; never throws, whatever happens inside.
        internal static void Tick()
        {
            if (_mort) return;
            try
            {
                long now = Environment.TickCount64;
                if (now < _prochainTick) return;
                _prochainTick = now + PeriodeTickMs;
                Battement();
            }
            catch (Exception e) { try { Echec(e); } catch { } }
        }

        /// A delay node of the mission script woke up: the same beat, on its own throttle, so the module keeps working in a build where
        /// nothing wires Tick(). Four float comparisons at most, once every two seconds.
        internal static void ChienDeGarde()
        {
            if (_mort) return;
            try
            {
                long now = Environment.TickCount64;
                if (now < _prochainChien) return;
                _prochainChien = now + PeriodeChienMs;
                Battement();
            }
            catch (Exception e) { try { Echec(e); } catch { } }
        }

        /// The beat itself, shared by the frame path and the mission-script path.
        ///  - anything that turns against the module gives the values back AT ONCE, which is the whole reason this runs per frame;
        ///  - a mission whose database load came too early (the campaign service did not know the mission yet) is written here instead;
        ///  - and once the values are in place, four floats are compared to see whether the game took them back.
        static void Battement()
        {
            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive || Identite.Blocked) { Rendre("mod coupé"); return; }
            if (!Campaign.InCampaign) { Rendre("hors mission de campagne"); return; }
            if (Campaign.MissionInerte) { Rendre("mission sans mod"); return; }
            if (!Solo()) { Rendre("partie en réseau"); return; }
            if (_refuse) return;                                         // already said once why this game build is left alone

            // the crash guard of the counter belongs to the HOOK, not to the write: the hook counts in every battle, written or not
            if (_compteurPose && !_compteurArme) ArmerCompteur();

            if (!_passeFaite) { Appliquer(true); return; }
            if (!_ecrit) return;                                         // nothing of ours is in place: nothing to watch

            GameCfg cfg;
            try { cfg = GameCfg.Instance; }
            catch (Exception e) { Echec(e); return; }
            if (cfg == null) return;
            if (Math.Abs(cfg.OnTransportDeathCargoKillMultiplier - _cSol) <= 1e-3f
                && Math.Abs(cfg.OnTransportDeathCargoKillHLMultiplier - _cHl) <= 1e-3f
                && Math.Abs(cfg.OnTransportDeathCargoKillHHMultiplier - _cHh) <= 1e-3f
                && Math.Abs(cfg.OnTransportDeathCargoKillAirMultiplier - _cAir) <= 1e-3f) return;
            _ecrit = false;                                              // the game took them back: they are written again below
            Appliquer(false);
        }

        /// Solo or online, read exactly as Epaves.Solo() reads it and latched for the battle: a state seen online once is online until
        /// the next database load. A read that throws counts as online, so the module writes nothing rather than write blind.
        /// The beat runs twice a second and the state's name is a string: the answer is kept for two seconds so the beat allocates
        /// nothing of its own, which is the rate Epaves reads it at anyway.
        static bool Solo()
        {
            long now = Environment.TickCount64;
            if (_reseauDit && now < _prochainReseau) return _solo;
            _prochainReseau = now + PeriodeChienMs;
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _enLigne = true;
                if (!_reseauDit) { _reseauDit = true; Log($"réseau : état={st} -> {(_enLigne ? "en ligne" : "solo")}"); }
            }
            catch { _solo = false; return false; }
            _solo = !_enLigne;
            return _solo;
        }

        /// The mission script ends the mission.
        internal static void SurFinDeMission()
        {
            if (_mort) return;
            try
            {
                DesarmerCompteur();                                      // the battle went to its end: the crash guard is cleared
                Bilan("fin de mission", true);
                if (Mod.Actif != null && !Mod.Actif.Value) Rendre("mod coupé");
            }
            catch (Exception e) { try { Echec(e); } catch { } }
        }

        // ============================================================ the write
        /// dire == true: the full measurement line is written, whatever happened. dire == false (watchdog): only a short line, and only
        /// when something was actually put back, capped at MaxLignesChien for the session.
        static void Appliquer(bool dire)
        {
            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive || Identite.Blocked) { Rendre("mod coupé"); return; }
            // campaign mission only, like the mod's two other GameConfig writers (Epaves.cs:207, Meteo.cs:332): not a value and not a
            // line in the main menu, the hangar or a skirmish. A database load that runs before the campaign service knows which
            // mission is starting simply leaves the pass undone, and the beat above does it as soon as the mission is known.
            if (!Campaign.InCampaign) { Rendre("hors mission de campagne"); return; }
            // a mission played without the mod (US_M01): Realism.Restore already took everything out and nothing goes back in
            if (Campaign.MissionInerte) { Rendre("mission sans mod"); return; }
            // online: a campaign-tuned simulation value has no business in one client of a network battle. The PUBLIC build blocks
            // multiplayer outright; the personal one does not, which is exactly why this gate is here.
            if (!Solo()) { Rendre("partie en réseau"); return; }
            if (_refuse) return;                                          // already said once why this game build is left alone

            GameCfg cfg;
            try { cfg = GameCfg.Instance; }
            catch (Exception e) { Echec(e); return; }
            if (cfg == null) return;                                      // not ready yet: the next database load tries again

            IntPtr ptr;
            try { ptr = ((Il2CppObjectBase)cfg).Pointer; } catch { return; }
            if (ptr != _cfgPtr) { _cfgPtr = ptr; _vuPremier = false; }    // another config object: everything is measured again

            // ---- MEASURE FIRST: the nine values of the game, read before a single one is written.
            if (!Lire(cfg)) return;

            // The first values ever seen for this object are the only base ever used, so a restore that failed silently can never
            // compound into x4. On the normal path (this method's own prefix is Realism.Restore) live == first == the game's own.
            if (!_vuPremier)
            {
                _vuPremier = true;
                _p0Sol = _multSol; _p0Hl = _multHl; _p0Hh = _multHh; _p0Air = _multAir;
            }
            float bSol = Math.Min(_multSol, _p0Sol), bHl = Math.Min(_multHl, _p0Hl);
            float bHh = Math.Min(_multHh, _p0Hh), bAir = Math.Min(_multAir, _p0Air);

            // ---- plausibility gate: outside this the field is not what this file thinks it is and NOTHING is written
            if (!Plausible(bSol) || !Plausible(bHl) || !Plausible(bHh) || !Plausible(bAir))
            {
                _refuse = true;
                Log("mort du chargement impossible dans cette version du jeu : multiplicateurs lus "
                    + $"au sol {Nb(bSol)}, hélico bas {Nb(bHl)}, hélico haut {Nb(bHh)}, avion {Nb(bAir)} "
                    + $"(attendu entre 0 et {Nb(MultMaxLu)}) ; rien n'est changé, le jeu garde son comportement d'origine");
                return;
            }

            float cSol = Cible(bSol), cHl = Cible(bHl), cHh = Cible(bHh), cAir = Cible(bAir);
            bool aEcrire = cSol > bSol || cHl > bHl || cHh > bHh || cAir > bAir;

            string refus = null;
            int n = 0;
            if (!aEcrire)
                refus = bSol <= MultZero && bHl <= MultZero && bHh <= MultZero && bAir <= MultZero
                    ? "les quatre multiplicateurs du jeu sont à zéro : un zéro n'a pas d'échelle, en inventer une serait deviner, "
                      + "c'est la seule chose que ce fichier refuse de faire"
                    : "le jeu tue déjà le chargement autant que cette règle le demande";
            else if (_ecritures >= MaxEcritures)
            {
                _refuse = true;
                refus = "les valeurs sont reprises par le jeu à chaque passe";
                Log($"les multiplicateurs sont repris par le jeu ({MaxEcritures} écritures depuis le chargement de la base) : le mod n'y touche plus");
            }
            else
            {
                try
                {
                    using (Realism.Lot(LotCargo))
                    {
                        if (cSol > bSol) n += Realism.SetJournaled(cfg, "OnTransportDeathCargoKillMultiplier", cSol);
                        if (cHl > bHl) n += Realism.SetJournaled(cfg, "OnTransportDeathCargoKillHLMultiplier", cHl);
                        if (cHh > bHh) n += Realism.SetJournaled(cfg, "OnTransportDeathCargoKillHHMultiplier", cHh);
                        if (cAir > bAir) n += Realism.SetJournaled(cfg, "OnTransportDeathCargoKillAirMultiplier", cAir);
                    }
                    _ecritures++;
                    // all or nothing on the read-back: if a single one did not take, the whole lot goes back to the game
                    if (!Relire(cfg, cSol, cHl, cHh, cAir))
                    {
                        refus = "une valeur n'a pas été retenue par le jeu";
                        Realism.RestoreLot(LotCargo, "mort du chargement : " + refus);
                        n = 0;
                    }
                    else
                    {
                        _ecrit = true;
                        _cSol = cSol; _cHl = cHl; _cHh = cHh; _cAir = cAir;
                    }
                }
                catch (Exception e)
                {
                    refus = "écriture refusée (" + e.GetBaseException().Message + ")";
                    try { Realism.RestoreLot(LotCargo, "mort du chargement : " + refus); } catch { }
                    n = 0;
                    Echec(e);
                }
            }

            // the pass is done for this database load, whatever it concluded: the nine values have been said once for this mission
            _passeFaite = true;

            if (dire) Dire(bSol, bHl, bHh, bAir, cSol, cHl, cHh, cAir, n, refus);
            else if (_lignesChien < MaxLignesChien && (n > 0 || refus != null))
            {
                _lignesChien++;
                Log("chien de garde : les multiplicateurs de la mort du transport avaient été repris par le jeu — "
                    + (n > 0
                        ? $"remis en place ({n} valeur(s)) : au sol {Nb(cSol)}, hélico bas {Nb(cHl)}, hélico haut {Nb(cHh)}, avion {Nb(cAir)}"
                        : "NON remis (" + refus + ")"));
            }
        }

        static bool Plausible(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0f && v <= MultMaxLu;

        /// The ceiling that applies to ONE value, deduced from that value itself. The scale of these multipliers is nowhere in the
        /// dumps: a value at or below SeuilEchelle can only be a proportion (bounded at 1, since a kill proportion above 1 means
        /// nothing and the engine's answer to it is unknown), above it can only be a percentage (bounded at 100). Reading the scale
        /// the wrong way with this rule can only write LESS than the other reading would, never more.
        static float Plafond(float vanille) => vanille <= SeuilEchelle ? PlafondProportion : PlafondPourcent;

        /// Raise only, never above the ceiling, and never touch a multiplier the game left at zero or already past the ceiling.
        static float Cible(float vanille)
        {
            if (vanille <= MultZero) return vanille;         // no scale to work from: left exactly as the game set it
            float plafond = Plafond(vanille);
            if (vanille >= plafond) return vanille;          // the game already wipes the cargo for a full-health hit
            return Math.Min(plafond, vanille * Facteur);
        }

        /// The nine values, read in one place. False means this game build does not carry them and nothing will ever be written.
        static bool Lire(GameCfg cfg)
        {
            try
            {
                _floorSol = cfg.OnTransportDeathCargoKillFloor;
                _multSol = cfg.OnTransportDeathCargoKillMultiplier;
                _floorHl = cfg.OnTransportDeathCargoKillHLFloor;
                _multHl = cfg.OnTransportDeathCargoKillHLMultiplier;
                _floorHh = cfg.OnTransportDeathCargoKillHHFloor;
                _multHh = cfg.OnTransportDeathCargoKillHHMultiplier;
                _floorAir = cfg.OnTransportDeathCargoKillAirFloor;
                _multAir = cfg.OnTransportDeathCargoKillAirMultiplier;
                _choc = cfg.OnTransportDeathCargoForcedStressPercentage;
                return true;
            }
            catch (Exception e)
            {
                _refuse = true;
                Log("mort du chargement introuvable dans cette version du jeu (GameConfig.OnTransportDeathCargo*) : "
                    + e.GetBaseException().Message + " ; rien n'est changé");
                return false;
            }
        }

        static bool Relire(GameCfg cfg, float sol, float hl, float hh, float air)
        {
            try
            {
                return Math.Abs(cfg.OnTransportDeathCargoKillMultiplier - sol) <= 1e-3f
                    && Math.Abs(cfg.OnTransportDeathCargoKillHLMultiplier - hl) <= 1e-3f
                    && Math.Abs(cfg.OnTransportDeathCargoKillHHMultiplier - hh) <= 1e-3f
                    && Math.Abs(cfg.OnTransportDeathCargoKillAirMultiplier - air) <= 1e-3f;
            }
            catch { return false; }
        }

        /// Gives the game its own values back and forgets that anything of ours was written.
        static void Rendre(string pourquoi)
        {
            if (!_ecrit) return;
            _ecrit = false;
            _cSol = _cHl = _cHh = _cAir = 0f;
            try
            {
                int n = Realism.RestoreLot(LotCargo, "mort du chargement : " + pourquoi);
                Log($"multiplicateurs rendus au jeu ({pourquoi}) : {n} valeur(s) remise(s)");
            }
            catch (Exception e) { Mod.Log.Warning(P + "valeurs non rendues : " + e.GetBaseException().Message); }
        }

        // ============================================================ the lines
        /// ONE line per database load, carrying the nine values of the game AND what was written. One line on purpose, and it is
        /// ALWAYS written, PUBLIC build included: that filter keeps only one line per module prefix every five minutes, "[CARGO]" is
        /// not in its KeepStarts, and this file does not own ModLog.cs - but it does keep any line containing one of its KeepParts
        /// texts, and "point(s) d'accroche installé(s)" is one of them. The clause at the end of this line is therefore load-bearing:
        /// it is true, and it is what carries the nine values of the game into every log. Do not remove it, and do not split this
        /// line in two - the second half would be the half that gets dropped.
        static void Dire(float bSol, float bHl, float bHh, float bAir,
                         float cSol, float cHl, float cHh, float cAir, int n, string refus)
        {
            string lu = $"valeurs du jeu lues avant toute écriture : au sol plancher {_floorSol} multiplicateur {Nb(bSol)} ; "
                      + $"hélico bas plancher {_floorHl} multiplicateur {Nb(bHl)} ; "
                      + $"hélico haut plancher {_floorHh} multiplicateur {Nb(bHh)} ; "
                      + $"avion plancher {_floorAir} multiplicateur {Nb(bAir)} ; "
                      + $"choc imposé aux survivants {_choc} %";

            string ecrit;
            if (n > 0)
                ecrit = $"écrit ({n} valeur(s), lot {LotCargo}, rendu au jeu dès que le mod s'arrête) : "
                      + $"au sol {Nb(bSol)} -> {Nb(cSol)} ; hélico bas {Nb(bHl)} -> {Nb(cHl)} ; "
                      + $"hélico haut {Nb(bHh)} -> {Nb(cHh)} ; avion {Nb(bAir)} -> {Nb(cAir)} "
                      + $"(x{Nb(Facteur)} partout, sur les valeurs du jeu elles-mêmes ; jamais abaissé, un multiplicateur laissé à 0 par "
                      + $"le jeu reste à 0 ; plafond déduit de la valeur du jeu elle-même, {Nb(PlafondProportion)} si elle ne dépasse pas "
                      + $"{Nb(SeuilEchelle)} — c'est alors une proportion — et {Nb(PlafondPourcent)} au-dessus, où c'est un pourcentage)";
            else ecrit = "RIEN N'EST ÉCRIT (" + (refus ?? "rien à écrire") + ")";

            Log("mort du transport, sort du chargement : " + lu + " — " + ecrit
                + " ; les quatre planchers et le choc ne sont PAS touchés : leur unité (pourcentage ou nombre d'hommes) n'a pas pu être "
                + "établie dans les données du jeu, et un plancher mal réglé tuerait les escouades des transports lourdement blindés qui "
                + "devaient survivre"
                + " ; d'après leur nom et d'après les deux constructeurs de UnloadingComponent, ces valeurs ne servent qu'à la mort du "
                + "transport, et le jeu a un réglage séparé pour le débarquement normal "
                + "(OnTransportUnloadCargoForcedStressPercentage) ; l'endroit où le jeu les lit n'a pas pu être retrouvé dans les données "
                + "disponibles, c'est une lecture solide et non une preuve"
                + " ; point(s) d'accroche installé(s) : "
                + (_compteurPose ? "comptage des sorties de transport détruit" : _compteurRefuse ? "aucun (comptage refusé par sa sécurité)" : "aucun"));
        }

        /// The game's own multipliers, as they were the first time this module ever saw them for the live GameConfig - i.e. before any
        /// write of its own. AntiHeliPortee prints them beside the live ones so its unload line can never pass this module's values off
        /// as the game's. False when nothing has been measured yet, and then nothing of ours has been written either.
        internal static bool OrigineLue(out float sol, out float hl, out float hh, out float air)
        {
            sol = _p0Sol; hl = _p0Hl; hh = _p0Hh; air = _p0Air;
            return _vuPremier;
        }

        /// True while this module's own multipliers are in place in the live GameConfig.
        internal static bool MultiplicateursEcrits => _ecrit;

        /// One line per battle: what could be counted honestly, and what the game does not let this file know.
        /// force == false (a new database load): a battle that counted nothing says nothing, so a mission load does not spend the line.
        static void Bilan(string quand, bool force)
        {
            if (_bilanDit) return;
            long morts = Interlocked.Read(ref _sortiesMort), normales = Interlocked.Read(ref _sortiesNormales);
            if (!force && morts == 0L && normales == 0L) { _bilanDit = true; return; }
            _bilanDit = true;
            if (!_compteurPose)
            {
                Log($"bilan de la bataille ({quand}) : rien compté, le comptage des sorties n'est pas en place"
                    + (_ecrit ? " ; les multiplicateurs, eux, étaient bien en place" : " ; les multiplicateurs n'étaient pas en place"));
                return;
            }
            if (morts == 0L && normales == 0L) { Log($"bilan de la bataille ({quand}) : aucune sortie de transport observée"); return; }
            long err = Interlocked.Read(ref _erreursCompteur);
            Log($"bilan de la bataille ({quand}) : {morts} soldat(s) sorti(s) d'un transport détruit, {normales} débarquement(s) normal(aux)"
                + (_ecrit ? " ; multiplicateurs du mod en place" : " ; multiplicateurs du jeu d'origine")
                + " ; le jeu ne dit pas lesquels de ces soldats sont morts ensuite et aucun chiffre n'est inventé ici : ce qui se compare "
                + "d'une bataille à l'autre, c'est ce nombre-là avant et après le changement des multiplicateurs"
                + (err > 0L ? $" ; erreurs de comptage {err}" : ""));
        }
    }
}
