// RealismOverhaul - "Suppression" : what being shot at does to the way a crew shoots back.
//  The game already owns a full suppression model; this module does NOT invent one. It measures what the game's own three stress
//  levels do, then raises the two bad levels to a floor when the game leaves them too gentle. Nothing else.
//
//  WHAT THE GAME OWNS, checked in the dumps before a line was written here:
//  - 'BuffConfig :: StressModifiersDictionary get_StressModifiers()', a 'UnitySerializedDictionary`2<StressLevel,StressModifiers>'
//    with StressLevel = { Calm, Shocked, Panicked } (byte enum). 'StressModifiers : Object' is a REFERENCE object, so the three
//    entries are edited IN PLACE: the dictionary itself is never replaced, and a system that cached the dictionary still points at
//    the very objects we wrote into.
//  - Each StressModifiers carries 17 writable Single properties: MissileAccuracyMultiplier, AimTimeMultiplier,
//    TimeBetweenBurstsMultiplier, ReloadTimeMultiplier, DispersionMultiplier, the four Inf variants, the five Plane variants,
//    InfantryDmgModFloor, InfantryDmgModPerSoldier, MovementSpeedMultiplier.
//  - 'BuffDebuffSystem :: Void SetStressModifiers(Entity unitEntity)' copies them into BuffComponent (AimTimeModifier,
//    TimeBetweenBurstsStressModifier, MagazineReloadTimeStressModifier, DispersionModifier, MissileAccuracyMultiplier,
//    MaxSpeedModifier). Stress also reaches guided weapons through
//    'CalculateMissileHitChance(Ammunitions, Single targetECM, Single targetDecoyEffect, Int32 targetActiveDecoys, Single stressMultiplier)'.
//
//  WHAT IS FORBIDDEN HERE, and why this module hooks almost nothing:
//  - 'StressComponent' holds 'List`1<Single> PendingDamages', a managed reference field, so the component is NON-BLITTABLE. Every
//    method that takes it by reference is therefore out of reach: StressSystem.StressRecovery(StressComponent&, Entity&),
//    StressSystem.ApplyStressDamage(StressComponent&), StressSystem.SetStressLevel(StressComponent&, Entity&) and
//    BattleSystemHelpers.CalculateStressDamageFromFixedValue(StressComponent&, Single). Patching one of those is exactly the mistake
//    that cost a player his campaign on 18/09/2026 (see the permanently disabled hook in AntiHeliPortee.cs). None of them is touched.
//  - No ECS component is read: no Get<T>, nowhere.
//  The feature itself needs NO hook at all - it is property writes on a config object. The single Harmony patch in this file is a
//  measurement postfix whose only parameter is Harmony's own __instance, the system object, a REFERENCE type: no game parameter is
//  declared and nothing is marshalled by reference (see the "mesure" section). It can be switched off on its own.
//
//  WHAT WAS ALREADY MEASURED IN THE GAME (Latest.log of the build installed on 18/09/2026), and what it decided:
//  - "précision des missiles selon le stress (MissileAccuracyMultiplier / MissileAccuracyPlaneMult) :
//     Shocked=0.85/0.85 Panicked=0.55/0.6 Calm=1/1". The game's missile values are ALREADY a real gradient - a panicked crew loses
//    45 % of its guided hit chance. So MissileAccuracy* is left exactly as the game made it: it does not need us.
//  - "[CALIBRE-MESURE] ... G4 stress pendant un impact : 689" - stress damage really is computed, 689 times in one battle. The stress
//    machinery runs; it is not dead code.
//  - "[ATGM] ... stress moyen 1.00 (min 1.00, max 1.00)" over 8373 hit-chance calculations of the 18/09/2026 battle - not one shooter
//    sampled there was above Calm. That is the honest limit of what is proven today: stress is COMPUTED, but it was never observed to
//    actually reach Shocked or Panicked on a firing unit, and IF THAT HOLDS, everything this module writes below changes nothing at all.
//    So the module does not merely count the calls. At each SetStressModifiers it reads the system's own '_missileAccuracyMultiplier'
//    scratch value, which the game fills with the level's own figure (measured: Calm=1, Shocked=0.85, Panicked=0.55), and splits the count
//    into calm / shocked / panicked. A battle that ends with zero shocked and zero panicked is reported as exactly that, in plain French,
//    and never as a success.
//  The other 15 properties of the three levels have never been logged by anything. They are logged here, in full, BEFORE a single
//  value is written, so the author can read them in his next log and correct the floors if they turn out to be wrong.
//
//  WHAT IS WRITTEN, and what is deliberately refused:
//  - Written (Shocked and Panicked only): AimTime, TimeBetweenBursts, ReloadTime, Dispersion - each in its normal AND its Inf
//    variant - plus MovementSpeedMultiplier. A suppressed crew aims slower, fires shorter bursts further apart, reloads clumsily,
//    shoots wider and moves less boldly. Both sides alike: BuffConfig is global, there is no per-player copy of it.
//  - REFUSED, with reasons, because a fake version is worse than none:
//      * Calm: it is the neutral reference. Touching it would move the whole game, not the suppression.
//      * MissileAccuracyMultiplier / MissileAccuracyPlaneMult: already meaningful, measured above.
//      * every Plane variant: an aircraft crosses the fight in seconds, and the campaign air missions are scripted. Vanilla already
//        has a gradient there. Not our business.
//      * InfantryDmgModFloor / InfantryDmgModPerSoldier: by the BuildingsConfig analogue (floor + perSoldier x soldiers) these look
//        like a damage SHARE, but whether it is damage dealt by a stressed squad or damage taken by one is NOT established. A value
//        written in the wrong direction would make suppression heal people. Logged, never written.
//      * StressRecoveryMultiplier: 'StressSystem :: Single get__stressRecoveryMultiplayer()' is filled by
//        'StressSystem :: .ctor(World, BuffConfig)', so it is baked when the battle world is built. We cannot prove we write before
//        that. Logged, never written.
//      * ShockedLevelMultiplier / PanickedLevelMultiplier (and the Infantry pair), Units.MaxStress, STRESS_TICK, Ammunitions
//        StressDamage / StressAOERadius: those decide HOW FAST stress rises, not what it does. Players already report panic
//        triggering too quickly, so making it rise faster is the opposite of the fix. Logged, never written.
//  - Only raises, never softens: a value the game already makes harsher than our floor keeps the game's own value. So a future game
//    update that ships a real suppression model simply switches this module off by itself.
//  - THE SIGN IS NEVER ASSUMED. Nothing in a name proves that "AimTimeMultiplier" multiplies a TIME rather than a SPEED, and a name read
//    backwards would make suppressed troops shoot BETTER. Two independent references decide, in this order:
//      1. the game's own Calm -> stressed step on that very property. It moves the way we expect -> confirmed, the floor applies; it moves
//         the other way -> the name does not mean what it says in this build, the property is skipped and the log names it;
//      2. when the game ships the level FLAT (step of zero, which is precisely the case this module exists for) the step proves nothing.
//         The critical-hit multipliers of the same BuffConfig then decide: MinorAimTimeMultiplier / MajorAimTimeMultiplier,
//         Minor/MajorDispersionMultiplier, Minor/MajorLoadingMultiplier, MinorMobilityMultiplier. A critical hit unambiguously makes a
//         unit WORSE, so the direction of those values fixes the game's own convention for the words Aim, Dispersion, Loading and Mobility
//         beyond argument. The witness must agree, or nothing is written on that property.
//      The burst gap has no critical-hit sibling in BuffConfig. It is a TIME, like the aim time and the loading time, and those two do have
//      one: only when both of them say that a longer time is the worse outcome is the convention of the object settled, and only then does
//      the burst gap follow it. Otherwise it is left alone. NOTHING is ever written on the strength of a name alone.
//    If the Calm entry itself is missing from the table, nothing at all is written (see Appliquer).
//  - Plausibility gate per property: the game's value must read as a finite multiplier inside a sane window, or that ONE property is
//    skipped (the field is something else in this build). One odd field never stops the others.
//  - Hard ceilings, justified in "Paliers" below: nothing above x2.5 on a penalty, nothing below x0.70 on movement.
//  - Every write goes through Realism.SetJournaled under the lot LotSuppression, so stopping the mod hands the game its values back.
//  - Solo only (a network battle is left alone: stress is computed on one PC and sent with SendUnitGetStressDamage, so two players
//    with different settings would disagree), inert in a mission played without the mod, inert when the mod is off.
//  - Error counter with kill-switch: 20 errors give the game its own values back for the rest of the session.
//
//  Player-visible strings would need Txt.cs, which this module does not own: there is NO Mod tab row here. The settings live in the
//  preferences file (RealismOverhaul_Suppression) and everything else goes to the log.
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using StressLvl = Il2CppBrokenArrow.Client.Ecs.BattleSystem.StressLevel;
using BuffSys = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.BuffDebuffSystem;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    static class Suppression
    {
        const string GuardVersion = "1.0";
        const int MaxErrors = 20;
        const float TickEvery = 2f;                                  // the gate is re-checked at most every 2 s, in the menus too
        const float ReportEvery = 60f;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal const string PrefCat = "RealismOverhaul_Suppression";
        /// Own journal lot: RestoreLot takes these writes out without touching a single value of the real stats.
        internal const string LotSuppression = "SUPPRESSION_STRESS";

        // ============================================================ the floors, and why they are these numbers
        /// A penalty multiplier is never written above this. Beyond x2.5 a squad under fire stops being a squad: it is realistic and
        /// miserable, which the author's standing rule calls a failure. It is also the point where scripted missions with a timer
        /// start to fail, because the attack that was meant to succeed no longer can.
        const float PlafondPenalite = 2.5f;
        /// A movement multiplier is never written below this. Real suppressed troops still move - in short rushes, badly - and a
        /// campaign objective that must be reached before a trigger fires needs them to arrive at all.
        const float PlancherVitesse = 0.70f;
        /// The game's own value must look like a multiplier or the property means something else here and is skipped.
        const float VanilleMin = 0.01f, VanilleMaxPenalite = 20f, VanilleMaxVitesse = 1.5f;

        /// One stress level's floors.
        ///  Aim / Burst / Reload / Disp are FLOORS on penalty multipliers (higher = worse for the shooter): written only when the game
        ///  is gentler. Move is a CEILING on a speed multiplier (lower = slower): written only when the game is faster.
        ///
        ///  Why these figures. Suppression trials (UK Dstl and US ARL small-arms work) put the drop in effective return fire from
        ///  incoming fire at roughly 50-90 %. The arithmetic below is spelled out because the first version of this comment understated
        ///  it by half: dispersion counts as an AREA against an area target, so a dispersion multiplier k divides the density of hits by
        ///  k squared, not by k. Only Shocked is at the mild end of the band; Panicked is at the harsh end, and is kept there on purpose
        ///  because it is the level a squad is meant to break at, not the level it fights at.
        ///   - Shocked  : bursts x1.25 leave 1/1.25 = 0.80 of the rate of fire, dispersion x1.35 leaves 1/1.35^2 = 0.55 of the density;
        ///                together about 0.44, i.e. roughly 55 % less effective fire, aim time on top. Clearly hampered, clearly fighting.
        ///   - Panicked : bursts x1.60 leave 0.63 of the rate, dispersion x1.90 leaves 1/1.90^2 = 0.28 of the density; together about
        ///                0.17, i.e. roughly 80 % less effective fire - the harsh end of the 50-90 % band, not the middle. On guided
        ///                weapons the game's own 0.55 missile accuracy already stacks on top of that, which is why we add nothing
        ///                there. A panicked squad is close to useless offensively but still defends itself - it is not a casualty.
        ///   - Reload   : x1.10 / x1.25 only. Fumbling a magazine is real, but reload time is a long flat block of dead time and
        ///                doubling it reads as a bug to a player rather than as pressure.
        ///   - Move     : x0.92 / x0.80. Kept mild on purpose, see PlancherVitesse.
        readonly struct Palier
        {
            internal readonly StressLvl Niveau;
            internal readonly string Nom;
            internal readonly float Aim, Burst, Reload, Disp, Move;
            internal Palier(StressLvl niveau, string nom, float aim, float burst, float reload, float disp, float move)
            { Niveau = niveau; Nom = nom; Aim = aim; Burst = burst; Reload = reload; Disp = disp; Move = move; }
        }

        static readonly Palier[] Paliers =
        {
            new Palier(StressLvl.Shocked,  "choqué", 1.25f, 1.25f, 1.10f, 1.35f, 0.92f),
            new Palier(StressLvl.Panicked, "paniqué", 1.60f, 1.60f, 1.25f, 1.90f, 0.80f),
        };

        /// Every property read for the record, in the order they are logged. Everything is measured; only the ones listed in
        /// Appliquer are ever written.
        static readonly string[] ChampsMesures =
        {
            "MissileAccuracyMultiplier", "AimTimeMultiplier", "TimeBetweenBurstsMultiplier", "ReloadTimeMultiplier", "DispersionMultiplier",
            "AimTimeInfMultiplier", "TimeBetweenBurstsInfMultiplier", "ReloadTimeInfMultiplier", "DispersionInfMultiplier",
            "MissileAccuracyPlaneMult", "AimTimePlaneMult", "TimeBetweenBurstsPlaneMult", "ReloadTimePlaneMult", "DispersionPlaneMult",
            "InfantryDmgModFloor", "InfantryDmgModPerSoldier", "MovementSpeedMultiplier",
        };

        /// BuffConfig scalars that decide how fast stress rises. Read-only here, see the header.
        static readonly string[] ChampsSeuils =
        {
            "ShockedLevelMultiplier", "PanickedLevelMultiplier", "ShockedLevelMultiplierInfantry", "PanickedLevelMultiplierInfantry",
            "StressRecoveryMultiplier", "StressDamageModThreshold",
        };

        /// The critical-hit multipliers of the SAME BuffConfig, and the whole reason this module can write anything on a level the game
        /// ships flat. A critical hit always makes a unit worse, so the direction of these values is the game's own answer to "which way
        /// does the word Aim, Dispersion, Loading or Mobility get worse in this build". Read-only, logged before anything is written.
        static readonly string[] ChampsTemoins =
        {
            "MinorAimTimeMultiplier", "MajorAimTimeMultiplier", "MinorDispersionMultiplier", "MajorDispersionMultiplier",
            "MinorLoadingMultiplier", "MajorLoadingMultiplier", "MinorMobilityMultiplier", "MinorMobilityMultiplierRotation",
        };

        // ============================================================ state (main thread)
        static MelonPreferences_Entry<bool> _actif, _prefMesure;
        static MelonPreferences_Entry<string> _guardVersion;
        static bool _prefsOk, _dead, _deadLogged;
        static int _errors;
        static float _next, _nextReport;

        static BuffConfig _cfg;                                      // held: a ScriptableObject that lives on between battles
        static IntPtr _cfgPtr;
        static bool _mesureFaite;                                    // this BuffConfig object already had its full measurement line
        static bool _ecrit;                                          // our floors are in place on this BuffConfig
        static bool _refusDit;                                       // the "nothing to write" line was said once for this object
        static int _nEcrits;
        static int _reecrits;                                        // times a global restore took our floors out and we put them back
        const int MaxDitsReecriture = 5;                             // that line is said at most this many times per session, then counted only

        // ============================================================ measurement hook (counters and stress level)
        static HarmonyLib.Harmony _harmony;
        static bool _hookTried, _hookOk;
        static long _applies, _hookErrors;
        static long _calme, _choc, _panique, _sondeIllisible;        // the level each call pushed, read from the system's own scratch value
        static long _lastApplies;
        static float _battleStart;
        /// Past this many calls the probe stops reading and only counts: the split is already settled long before, and this method can be
        /// called once per unit per frame for a whole session. Never unpatched, just idle.
        const long MaxSondes = 2000000L;

        static void Log(string s) => Mod.Log.Msg("[SUPPRESSION] " + s);
        static string Nb(float v) => v.ToString("0.###", Inv);

        // ============================================================ preferences
        internal static void CreatePrefs()
        {
            if (_prefsOk) return;
            _prefsOk = true;
            try
            {
                var c = MelonPreferences.CreateCategory(PrefCat);
                _actif = c.CreateEntry("SuppressionSousLeFeu", true, description: Build.Desc(
                    "Une unité choquée ou paniquée vise plus lentement, tire par rafales plus espacées, recharge plus mal, disperse "
                    + "davantage ses tirs et avance moins vite. Les deux camps sont touchés de la même façon. Le mod ne fait que "
                    + "relever les valeurs du jeu quand elles sont trop douces : il n'adoucit jamais rien.",
                    "L'infanterie sous le feu vise moins bien et avance moins vite (les deux camps)."));
                _prefMesure = c.CreateEntry("MesureDuStress", true, description: Build.Desc(
                    "Compte dans le journal combien de fois le jeu applique les effets du stress à une unité pendant une bataille. "
                    + "Ne change rien au jeu, sert seulement à vérifier que le stress monte vraiment.",
                    "Relevé du stress dans le journal (ne change rien)."));
                _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
                if (_guardVersion.Value != GuardVersion) _guardVersion.Value = GuardVersion;
            }
            catch (Exception e) { Mod.Log.Warning("[SUPPRESSION] réglages non créés : " + e.GetBaseException().Message); }
        }

        static bool Voulu
        {
            get { try { return _actif == null || _actif.Value; } catch { return false; } }
        }

        static bool MesureVoulue
        {
            get { try { return _prefMesure == null || _prefMesure.Value; } catch { return false; } }
        }

        // ============================================================ the gate
        /// Solo latch: anything that looks like a network session makes the module inert, and so does an unreadable state.
        static bool Solo()
        {
            try { return !NetScen.IsNetwork && !NetScen.IsScenarioSlave && !NetScen.IsScenarioHost; }
            catch { return false; }
        }

        /// The context the whole module is supposed to be absent from: a network battle, a mission played without the mod, or a module
        /// that has given up. It does NOT include the player's own setting, so switching the suppression off still leaves the read-only
        /// measurement and its counter available - they are a separate setting (MesureDuStress) and must stay one.
        static bool ContexteOk()
        {
            if (_dead) return false;
            try { if (Campaign.MissionInerte) return false; } catch { return false; }
            return Solo();
        }

        static void Fail(Exception e, string what)
        {
            if (++_errors >= MaxErrors) _dead = true;
            try { Mod.Log.Warning($"[SUPPRESSION] {what} : {e.GetBaseException().Message}"); } catch { }
        }

        // ============================================================ entry points
        /// Main thread. Called from the realism pass and from the frame tick: safe to call as often as wanted, it does its work once
        /// per BuffConfig object and then only re-checks the gate. Never throws.
        internal static void Apply()
        {
            try
            {
                if (_dead)
                {
                    if (!_deadLogged)
                    {
                        _deadLogged = true;
                        Log($"{MaxErrors} erreurs : le jeu garde ses propres valeurs de stress jusqu'au redémarrage");
                        Rendre("trop d'erreurs");
                    }
                    return;
                }

                var cfg = Config();
                if (cfg == null) return;

                if (!ContexteOk()) { Rendre("bataille en réseau ou mission sans le mod"); return; }
                Mesurer(cfg);                                        // read-only, once per BuffConfig, before a single value of ours
                if (!Voulu) { Rendre("réglage désactivé"); return; }

                if (_ecrit)
                {
                    // _ecrit alone is not proof that our floors are still on the object. Realism.Restore puts every journalled value back
                    // and empties the journal, and it runs on every database load, on the campaign reload and from SafetyPoll - while this
                    // BuffConfig is a ScriptableObject whose pointer does not change, so Config() sees nothing. Without this check the
                    // floors would be stripped at the first mission load of the session and the log would keep saying they were applied.
                    int tenus;
                    try { tenus = Realism.LotWrites(LotSuppression); } catch { tenus = _nEcrits; }
                    if (_nEcrits <= 0 || tenus > 0) return;          // nothing of ours to hold, or still held
                    _ecrit = false; _nEcrits = 0; _refusDit = false;
                    _reecrits++;
                    if (_reecrits <= MaxDitsReecriture)
                        Log("les effets du stress ont été rendus au jeu par une remise à zéro générale (rechargement de la base) : ils sont réécrits");
                }

                Appliquer(cfg);
            }
            catch (Exception e) { Fail(e, "application impossible"); }
        }

        /// Main thread, from the mod's frame loop. Keeps the gate fresh (the player can switch the setting off mid-session, and a
        /// database reload can hand us another BuffConfig), then reports the measurement counters. Never throws.
        internal static void Frame()
        {
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now >= _next)
                {
                    _next = now + TickEvery;
                    if (ContexteOk()) EnsureHook();                  // no detour at all in a network battle or in a mission without the mod
                    Apply();
                }
                if (_nextReport <= 0f) { _nextReport = now + ReportEvery; return; }
                if (now < _nextReport) return;
                _nextReport = now + ReportEvery;
                Report(false);
            }
            catch (Exception e) { Fail(e, "relevé impossible"); }
        }

        /// Main thread, at each mission start: last summary of the battle that ended, then the counters go back to zero. The installed
        /// patch and the error kill-switch stay as they are, and so do the values written into BuffConfig.
        internal static void ResetSession()
        {
            try { Report(true); } catch { }
            Interlocked.Exchange(ref _applies, 0);
            Interlocked.Exchange(ref _calme, 0);
            Interlocked.Exchange(ref _choc, 0);
            Interlocked.Exchange(ref _panique, 0);
            Interlocked.Exchange(ref _sondeIllisible, 0);
            _lastApplies = 0;
            _nextReport = 0f;
            try { _battleStart = UnityEngine.Time.realtimeSinceStartup; } catch { _battleStart = 0f; }
        }

        internal static void OnBattleEnd()
        {
            try { Report(true); } catch { }
        }

        /// The mod is stopping or the player switched everything off: the game gets its own values back.
        internal static void OnQuit()
        {
            try { Report(true); } catch { }
            Rendre("arrêt du mod");
        }

        // ============================================================ the game's config object
        /// The BuffConfig of the moment, measured in full the first time each object is seen. A database reload or a scene change can
        /// hand us another one, and the journal entries of the old one die with it.
        static BuffConfig Config()
        {
            BuffConfig cfg;
            try { cfg = GameConfig.Instance?.BuffConfig; }
            catch (Exception e) { Fail(e, "BuffConfig illisible"); return null; }
            if (cfg == null) return null;

            IntPtr p;
            try { p = ((Il2CppObjectBase)cfg).Pointer; } catch { return null; }
            if (p == _cfgPtr) return _cfg ?? cfg;

            // a new config object: everything we believed about the old one is void. The measurement is NOT taken here: it belongs behind
            // the network/inert gate, so Apply() calls it once that gate is open and _mesureFaite keeps it to one pass per object.
            _cfg = cfg; _cfgPtr = p;
            _mesureFaite = false; _ecrit = false; _refusDit = false; _nEcrits = 0;
            return cfg;
        }

        // ============================================================ measurement (before anything is written)
        /// Logs the game's own values, in full, for the three stress levels plus the six BuffConfig scalars that decide how fast
        /// stress rises. Runs once per BuffConfig object, before a single value of ours is written. Read-only.
        static void Mesurer(BuffConfig cfg)
        {
            if (_mesureFaite) return;
            _mesureFaite = true;
            try
            {
                var sb = new System.Text.StringBuilder("valeurs du jeu AVANT toute écriture — seuils :");
                foreach (var n in ChampsSeuils)
                    sb.Append(TryRead(cfg, n, out float sv) ? $" {n}={Nb(sv)}" : $" {n}=?");
                Log(sb.ToString());
            }
            catch (Exception e) { Fail(e, "seuils du stress illisibles"); }

            try
            {
                var sb = new System.Text.StringBuilder("valeurs du jeu AVANT toute écriture — témoins des coups critiques (jamais écrits ; "
                                                     + "ils servent à savoir dans quel sens le jeu rend une unité PIRE) :");
                foreach (var n in ChampsTemoins)
                    sb.Append(TryRead(cfg, n, out float tv) ? $" {n}={Nb(tv)}" : $" {n}=?");
                Log(sb.ToString());
            }
            catch (Exception e) { Fail(e, "témoins des coups critiques illisibles"); }

            try
            {
                var sm = cfg.StressModifiers;
                if (sm == null) { Log("valeurs du jeu AVANT toute écriture : la table des effets du stress est absente de cette version du jeu, rien ne sera écrit"); return; }

                int n = 0;
                foreach (var kv in sm)
                {
                    n++;
                    var m = kv.Value;
                    var sb = new System.Text.StringBuilder($"valeurs du jeu AVANT toute écriture — niveau {kv.Key} :");
                    if (m == null) { sb.Append(" (entrée vide)"); Log(sb.ToString()); continue; }
                    foreach (var f in ChampsMesures)
                        sb.Append(TryRead(m, f, out float v) ? $" {f}={Nb(v)}" : $" {f}=?");
                    Log(sb.ToString());
                }
                if (n == 0) Log("valeurs du jeu AVANT toute écriture : la table des effets du stress est vide, rien ne sera écrit");
            }
            catch (Exception e) { Fail(e, "effets du stress illisibles"); }
        }

        /// Reads one Single property by name. False when the property is missing, is not a Single, or holds something that is not a
        /// finite number: that one field is then left alone.
        static bool TryRead(object o, string name, out float v)
        {
            v = 0f;
            try
            {
                var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead || p.PropertyType != typeof(float)) return false;
                v = (float)p.GetValue(o);
                return !float.IsNaN(v) && !float.IsInfinity(v);
            }
            catch { return false; }
        }

        /// One critical-hit witness: the Minor value when it is a usable multiplier that really steps away from 1, otherwise the Major
        /// one. Returns 0 when neither says anything, and 0 is what makes Ecrire refuse to write on a flat level. A witness is never
        /// written, never clamped and never used to pick a figure: only its direction is read.
        static float Temoin(BuffConfig cfg, string minor, string major)
        {
            if (TryRead(cfg, minor, out float v) && v >= VanilleMin && v <= VanilleMaxPenalite && Math.Abs(v - 1f) > 1e-4f) return v;
            if (major != null && TryRead(cfg, major, out float w) && w >= VanilleMin && w <= VanilleMaxPenalite && Math.Abs(w - 1f) > 1e-4f) return w;
            return 0f;
        }

        // ============================================================ writing the floors
        static void Appliquer(BuffConfig cfg)
        {
            StressModifiersDictionary sm;
            try { sm = cfg.StressModifiers; }
            catch (Exception e) { Fail(e, "table des effets du stress illisible"); return; }
            if (sm == null) return;

            int ecrits = 0, deja = 0, sautes = 0, contre = 0, sansTemoin = 0;
            var detail = new System.Text.StringBuilder();

            // the critical-hit witnesses of this same object, read once. 0 = no usable witness for that word.
            float tAim = Temoin(cfg, "MinorAimTimeMultiplier", "MajorAimTimeMultiplier");
            float tDisp = Temoin(cfg, "MinorDispersionMultiplier", "MajorDispersionMultiplier");
            float tLoad = Temoin(cfg, "MinorLoadingMultiplier", "MajorLoadingMultiplier");
            float tMove = Temoin(cfg, "MinorMobilityMultiplier", "MinorMobilityMultiplierRotation");
            // the burst gap has no sibling of its own. It is a TIME, like the aim time and the loading time: only when both of those say
            // that a longer time is the worse outcome is the convention of this object settled, and the weaker of the two is then used.
            float tBurst = tAim > 1f + 1e-4f && tLoad > 1f + 1e-4f ? Math.Min(tAim, tLoad) : 0f;

            // The game's own Calm entry is the reference used to CHECK THE SIGN of every property before writing it (see Sens).
            StressModifiers calme = null;
            try { foreach (var kv in sm) if (kv.Key == StressLvl.Calm) { calme = kv.Value; break; } }
            catch (Exception e) { Fail(e, "niveau calme illisible"); return; }
            if (calme == null)
            {
                // without the reference we cannot prove which way "worse" goes, and guessing is how a mod makes suppressed troops
                // shoot BETTER. Clean refusal.
                if (!_refusDit)
                {
                    _refusDit = true;
                    Log("suppression : le niveau calme est absent de la table, impossible de vérifier le sens des réglages — rien n'est écrit");
                }
                _ecrit = true; _nEcrits = 0;
                return;
            }

            try
            {
                foreach (var kv in sm)
                {
                    var m = kv.Value;
                    if (m == null) continue;

                    int iPalier = -1;
                    for (int i = 0; i < Paliers.Length; i++) if (Paliers[i].Niveau == kv.Key) { iPalier = i; break; }
                    if (iPalier < 0) continue;                        // Calm, or a level this build added: never touched
                    var pal = Paliers[iPalier];

                    int n = 0, d = 0, s = 0, c = 0, t = 0;
                    // penalties: floors, higher = worse for the shooter
                    Ecrire(m, calme, "AimTimeMultiplier", pal.Aim, false, tAim, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "AimTimeInfMultiplier", pal.Aim, false, tAim, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "TimeBetweenBurstsMultiplier", pal.Burst, false, tBurst, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "TimeBetweenBurstsInfMultiplier", pal.Burst, false, tBurst, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "ReloadTimeMultiplier", pal.Reload, false, tLoad, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "ReloadTimeInfMultiplier", pal.Reload, false, tLoad, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "DispersionMultiplier", pal.Disp, false, tDisp, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    Ecrire(m, calme, "DispersionInfMultiplier", pal.Disp, false, tDisp, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);
                    // movement: a ceiling, lower = slower
                    Ecrire(m, calme, "MovementSpeedMultiplier", pal.Move, true, tMove, ref n, ref d, ref s, ref c, ref t, detail, pal.Nom);

                    ecrits += n; deja += d; sautes += s; contre += c; sansTemoin += t;
                }
            }
            catch (Exception e) { Fail(e, "effets du stress non écrits"); return; }

            _nEcrits = ecrits;
            _ecrit = true;

            string reserves = (sautes > 0 ? $", {sautes} valeur(s) écartée(s) (hors des bornes plausibles)" : "")
                + (contre > 0 ? $", {contre} valeur(s) écartée(s) parce que le jeu les fait varier dans l'AUTRE sens que prévu "
                              + "(le réglage ne veut pas dire ce qu'on croyait : à signaler, rien n'a été écrit dessus)" : "")
                + (sansTemoin > 0 ? $", {sansTemoin} valeur(s) écartée(s) faute de preuve du sens : le jeu laisse ce niveau identique au "
                              + "calme et aucun multiplicateur de coup critique ne dit dans quel sens il empire — rien n'est jamais écrit "
                              + "sur la seule foi d'un nom" : "");

            if (ecrits > 0)
            {
                Log($"suppression appliquée : {ecrits} valeur(s) durcie(s){detail} — le jeu gardait déjà {deja} valeur(s) plus dures que les nôtres"
                    + reserves
                    + " ; les deux camps sont touchés de la même façon (BuffConfig est global) ; rien n'est touché au niveau calme, "
                    + "ni sur les missiles, ni sur les avions, ni sur la vitesse de montée du stress"
                    + " ; attention en relisant ce journal : la ligne « [CALIBRAGE] stress » écrite plus bas relit ces mêmes objets APRÈS "
                    + "cette écriture, donc sa vitesse et sa dispersionInf sont les valeurs du mod, pas celles du jeu — les valeurs du jeu "
                    + "sont dans les lignes « valeurs du jeu AVANT toute écriture » ci-dessus");
            }
            else if (!_refusDit)
            {
                _refusDit = true;
                Log($"suppression : rien à écrire, le jeu est déjà au moins aussi dur que nos planchers sur les {deja} valeur(s) concernée(s)"
                    + reserves);
            }
        }

        /// Writes one property of one stressed level, with the sign checked first.
        ///  vitesse = false : the value is a PENALTY multiplier (aim time, burst gap, reload, dispersion). Higher = worse for the
        ///                    shooter, so 'cible' is a floor and we only ever raise.
        ///  vitesse = true  : the value is a SPEED multiplier. Lower = slower, so 'cible' is a ceiling and we only ever lower.
        ///
        /// THE SIGN IS NOT ASSUMED. Before writing, the level's value is compared with the Calm entry's value for the same property.
        /// The game's own Calm -> stressed step tells us which way it makes things worse:
        ///   - it moves the way we expect  -> the reading is confirmed, the floor/ceiling is applied;
        ///   - it moves the OTHER way      -> the property does not mean what its name suggests in this build (a multiplier on aim
        ///                                    SPEED rather than on aim TIME would read exactly like this). Writing our value would
        ///                                    make a suppressed crew shoot BETTER, so the property is skipped and counted in
        ///                                    'contre', which the log names explicitly;
        ///   - it does not move at all     -> the game left this level flat, which is precisely the case this module exists for, and the
        ///                                    step proves nothing at all. The 'temoin' then decides: it is the critical-hit multiplier of
        ///                                    the same BuffConfig for the same word, and a critical hit always makes a unit WORSE, so its
        ///                                    own direction is the game's answer. No witness, or a witness pointing the other way, and
        ///                                    nothing is written. A name alone is never evidence here.
        static void Ecrire(StressModifiers m, StressModifiers calme, string name, float cible, bool vitesse, float temoin,
                           ref int n, ref int d, ref int s, ref int c, ref int t, System.Text.StringBuilder detail, string niveau)
        {
            try
            {
                float max = vitesse ? VanilleMaxVitesse : VanilleMaxPenalite;
                if (!TryRead(m, name, out float v) || v < VanilleMin || v > max) { s++; return; }

                // sign check, first reference: the game's own step from calm to this level, on this very property
                bool prouve = false;
                if (TryRead(calme, name, out float vc) && vc >= VanilleMin && vc <= max && Math.Abs(v - vc) > 1e-4f)
                {
                    bool attendu = vitesse ? v < vc : v > vc;         // the game already makes this level worse the way we expect
                    if (!attendu) { c++; return; }
                    prouve = true;
                }
                if (!prouve)
                {
                    // the level is flat, or the calm value is unreadable: second reference, the critical-hit multiplier of the same object
                    if (temoin <= 0f) { t++; return; }
                    bool conforme = vitesse ? temoin < 1f - 1e-4f : temoin > 1f + 1e-4f;
                    if (!conforme) { c++; return; }
                }

                cible = vitesse ? (cible < PlancherVitesse ? PlancherVitesse : cible)
                                : (cible > PlafondPenalite ? PlafondPenalite : cible);

                // the game is already at least as hard as we are: it keeps its own value
                if (vitesse ? v <= cible + 1e-4f : v >= cible - 1e-4f) { d++; return; }

                using (Realism.Lot(LotSuppression))
                {
                    if (Realism.SetJournaled(m, name, cible) <= 0) { s++; return; }
                }
                if (!TryRead(m, name, out float apres) || Math.Abs(apres - cible) > 1e-3f) { s++; return; }
                n++;
                detail.Append($" ; {niveau} {name} {Nb(v)} -> {Nb(apres)}");
            }
            catch { s++; }
        }

        /// Hands the game its own values back and forgets that anything of ours was written.
        static void Rendre(string why)
        {
            if (!_ecrit) return;
            _ecrit = false;
            // nothing of ours was in place (the game was already harder than our floors): _refusDit stays set, so the "rien à
            // écrire" line is not said again every time the gate opens and closes.
            if (_nEcrits <= 0) return;
            _nEcrits = 0; _refusDit = false;
            try
            {
                int n = Realism.RestoreLot(LotSuppression, "suppression : " + why);
                if (n > 0) Log($"effets du stress rendus au jeu ({why}) : {n} valeur(s) remise(s)");
            }
            catch (Exception e) { try { Mod.Log.Warning("[SUPPRESSION] valeurs non rendues : " + e.GetBaseException().Message); } catch { } }
        }

        // ============================================================ mesure : does stress actually rise?
        /// One postfix, on 'BuffDebuffSystem :: Void SetStressModifiers(Entity unitEntity)'. Entity is passed BY VALUE and is a
        /// blittable struct (WorldId + EntityId), so this is not the forbidden pattern; and the postfix declares NO parameter at
        /// all, so nothing is marshalled either way. It increments one counter and does nothing else: it may run on a worker thread,
        /// so no Unity call, no allocation, no logging inside.
        ///
        /// What it proves: the game calls this when it pushes a unit's stress modifiers into its BuffComponent. A count that stays at
        /// zero for a whole battle means the stress levels never move and the values written above can change nothing.
        ///
        /// WHICH LEVEL was applied is read from the system itself, not from any component. 'BuffDebuffSystem :: Single
        /// get__missileAccuracyMultiplier()' is a plain property on a REFERENCE type (alldump: "P Single _missileAccuracyMultiplier"),
        /// and SetStressModifiers fills it with the level's own MissileAccuracyMultiplier - measured in the game as Calm=1, Shocked=0.85,
        /// Panicked=0.55. This module never writes that property (MissileAccuracy* is on the refused list), so it stays the game's own
        /// gradient and is a clean three-way probe. Its one weakness is stated here rather than hidden: the system also has
        /// SetValuesToOne(), so a call that sets nothing leaves 1 behind and is counted CALM. The probe can therefore under-report stress,
        /// never over-report it - which is the right way round for a module whose whole question is "did anything actually happen".
        /// Anything outside a plausible multiplier is counted apart as unreadable and never as panic.
        static void EnsureHook()
        {
            if (_hookTried || !MesureVoulue) return;
            _hookTried = true;
            try
            {
                var target = AccessTools.Method(typeof(BuffSys), "SetStressModifiers");
                if (target == null) { Log("relevé du stress : SetStressModifiers introuvable dans cette version du jeu, comptage indisponible (rien d'autre n'est affecté)"); return; }
                var mine = typeof(Suppression).GetMethod(nameof(Post), BindingFlags.NonPublic | BindingFlags.Static);
                if (mine == null) return;
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Suppression");
                _harmony.Patch(target, postfix: new HarmonyMethod(mine));
                _hookOk = true;
                Log("relevé du stress installé (comptage seul, aucun changement de comportement)");
            }
            catch (Exception e)
            {
                try { Mod.Log.Warning("[SUPPRESSION] relevé du stress non installé : " + e.GetBaseException().Message); } catch { }
            }
        }

        /// Hot path, any thread. Counters only: one increment, one property read on the instance Harmony already holds, no allocation,
        /// no Unity call, no logging. '__instance' is the system object itself, a reference type - nothing is marshalled by reference.
        static void Post(BuffSys __instance)
        {
            long n = Interlocked.Increment(ref _applies);
            if (n > MaxSondes) return;                               // the split is long settled: from here on we only count
            try
            {
                float a = __instance._missileAccuracyMultiplier;     // 1 = calme, 0.85 = choqué, 0.55 = paniqué (mesuré dans le jeu)
                if (!(a > 0.01f) || a > 1.5f) Interlocked.Increment(ref _sondeIllisible);   // NaN included: not the value we measured
                else if (a > 0.95f) Interlocked.Increment(ref _calme);
                else if (a > 0.70f) Interlocked.Increment(ref _choc);
                else Interlocked.Increment(ref _panique);
            }
            catch { Interlocked.Increment(ref _hookErrors); }
        }

        // ============================================================ report
        static void Report(bool final)
        {
            long applies = Interlocked.Read(ref _applies);
            if (!final && applies == _lastApplies) return;
            if (final && applies == 0 && _lastApplies == 0 && !_ecrit) return;
            _lastApplies = applies;

            string duree = "";
            try
            {
                float t = UnityEngine.Time.realtimeSinceStartup - _battleStart;
                if (_battleStart > 0f && t > 1f)
                    duree = $" en {t.ToString("0", Inv)} s, soit {(applies / t).ToString("0.##", Inv)} par seconde";
            }
            catch { }

            long calme = Interlocked.Read(ref _calme), choc = Interlocked.Read(ref _choc);
            long panique = Interlocked.Read(ref _panique), flou = Interlocked.Read(ref _sondeIllisible);

            // the ways the count can mean nothing are all said apart, so a zero is never read as "stress never rose" when in fact nobody
            // was counting, and a count above zero is never read as a success when every single one of them was at the calm level
            string verdict;
            if (!_hookOk && !MesureVoulue) verdict = "relevé du stress désactivé dans les réglages : le mod ne dit pas ici si le stress est monté";
            else if (!_hookOk) verdict = "comptage impossible dans cette version du jeu : le mod ne dit pas ici si le stress est monté";
            else if (applies == 0) verdict = "le stress n'a JAMAIS été appliqué à une unité pendant cette bataille : les effets réglés par le mod n'ont rien changé ici, à signaler";
            else if (choc + panique > 0)
                verdict = $"des unités ont vraiment été choquées ({choc}) ou paniquées ({panique}) : les effets réglés par le mod ont servi";
            else if (calme > 0)
                verdict = "le jeu a bien appliqué les effets du stress, mais TOUJOURS au niveau calme : aucune unité n'a été choquée ni "
                        + "paniquée, donc les valeurs réglées par le mod n'ont rien changé du tout dans cette bataille. C'est aussi ce que "
                        + "montrait le journal du 18/09 (8373 calculs de chance de toucher, stress 1.00 du début à la fin). À signaler";
            else
                verdict = "niveau de stress illisible dans cette version du jeu : le mod ne peut pas dire si le stress est monté, il ne "
                        + "sait que le nombre d'applications";

            Log($"{(final ? "bilan" : "relevé")} : effets du stress appliqués à une unité {applies} fois{duree} "
                + $"(calme {calme}, choqué {choc}, paniqué {panique}"
                + (flou > 0 ? $", illisible {flou}" : "") + ") ; "
                + $"valeurs durcies par le mod {_nEcrits}"
                + (_reecrits > 0 ? $" (réécrites {_reecrits} fois après une remise à zéro générale)" : "")
                + $" ; {verdict}"
                + (Interlocked.Read(ref _hookErrors) > 0 ? $" ; {Interlocked.Read(ref _hookErrors)} erreur(s) de comptage" : "")
                + (_errors > 0 ? $" ; {_errors} erreur(s)" : ""));
        }
    }
}
