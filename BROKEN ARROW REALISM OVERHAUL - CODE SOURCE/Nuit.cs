// RealismOverhaul - what the night costs the one who fires ("les départs de coups qui te trahissent la nuit").
//
//  WHAT THE GAME REALLY OWNS. Every line below was checked in the dumps before a line of code was written here.
//
//  1. HEADLIGHTS DO NOT EXIST. This is a refusal, not a choice, and the module says so once per game start.
//     - The whole light system of the game knows exactly two kinds of light: BaLight+Type = { MuzzleFlash, ImpactFlash }
//       (alldump.txt:55995-55999). LightLimitedPool spawns three things and three only: SpawnMuzzleLight(Vector3&, Int32 weaponId,
//       Transform), SpawnImpactLight(Vector3, GameObject), SpawnAmmoFlashLight(GameObject, Transform) (alldump.txt:22855).
//     - LightFlashesConfig holds MuzzleFlashes / ImpactFlashes / AmmoFlashes and nothing else (alldump.txt:40526).
//     - A full search of both dumps for headlight, head_light, lamp, lantern, spotlight, searchlight, beam and phare over
//       85 517 member lines returns: UILine._lampSprite (a sprite of the mission editor), SensorType.Beam (a dead enum name),
//       WeaponType.LightMachineGun, the graphics settings LightSourcesCount / PixelLightCount, Unity's own HDAdditionalLightData,
//       and ProjectileSpawnerHelper.LIGHT_FLASH_POINT (the name of the transform a muzzle light is hung on). Not one vehicle light.
//     - The Units model of the database has no light field of any kind.
//     A vehicle therefore has no headlights, no switchable light state, and nothing to read. "A lit vehicle is easier to spot"
//     cannot be built here, at all, and no honest imitation of it exists. Nothing is faked in its place.
//
//  2. SOUND. The author refused detection by sound outright. Nothing of the kind is built.
//
//  3. NIGHT VISION AND THERMALS DO NOT EXIST EITHER. The list SensorType (Visual, InfraRed, TV, NightVision, RadarA/G/U, Laser, Beam)
//     exists at allmembers.txt:20574-20583, and those ten lines are the ONLY ten lines of the game that mention it: no field, no
//     parameter, no return value is of that type. A Sensors row holds Id, Name, IsDefault, OpticsGround, OpticsLowAltitude,
//     OpticsHighAltitude, ModelFileName - there is not even a box to put "this unit has thermals" in. So no unit can see better or
//     worse at night, and a uniform night penalty on everyone would be both false and the best way to stall a mission trigger that
//     waits for "the enemy has been spotted". Refused.
//
//  4. THE HOUR IS NOT AN INPUT OF SPOTTING. EnvironmentService.DayTimePresetChangedEvent has exactly ONE subscriber in the whole
//     game, LightLimitedPool::.ctor (cross-reference cache daynight\xref_dn.txt). No FogOfWar, combat, AI or spawn type ever reads
//     the time of day. The engine cannot make night change spotting by itself.
//
//  WHAT IS LEFT, AND IT IS THE THING THE AUTHOR ASKED FOR. The engine already owns "firing gives you away":
//     FogOfWarComponent.WeaponFlash (Single, per unit) accumulates Weapons.FlashPerShot at each shot and is driven by
//     WeaponFlashSystem(World, FogOfWarConfig), which is steered by exactly two writable global floats,
//     FogOfWarConfig.MaxFlashValue and FogOfWarConfig.FlashReduction (allmembers.txt:54809-54812).
//     The game's own tooltips fix the direction: "Maximum unit visibility cap applied after unit stealth, cover and flash were taken
//     into account" (MaxAntiVisible) puts flash in the same product as stealth and cover, and "This weapon doesn't degrade your
//     stealth value as fast as other weapons when shooting" proves firing pushes that product UP, i.e. gets you spotted from further.
//     So: MaxFlashValue is the ceiling of the tell-tale. FlashReduction steers how fast it fades - but in WHICH direction is exactly
//     what is not proven, and that is why it is never written (see below).
//     The mod can read which hour the battle is being fought at (EnvironmentService.CurrentDayTimePreset.DayTimeVfxParameter, the
//     game's own Day/Night flag, the very one the light pool uses) and raise that ceiling, for that battle only, when it is night.
//     That is "firing at night gives your position away further than firing by day", it is one global config shared by
//     both sides so it is exactly symmetric, and it costs nothing per frame.
//
//  THE COUNTERPART THE AUTHOR ASKED FOR ("moving dark should be worth something") IS THE SAME COIN. Only a unit that FIRES pays.
//  A unit that moves and keeps quiet is exactly as hard to see as before - which is what makes the choice to hold fire worth
//  something at night. That is the only form of it this engine allows: there is no speed, movement, engine or silence field anywhere
//  in the FogOfWar namespace, so "moving reveals you" stays impossible and is not pretended here.
//
//  MEASURE BEFORE ACTING, AND THIS RELEASE ONLY MEASURES. MaxFlashValue and FlashReduction have NEVER been read in a live log,
//  and the module must not decide on its own to act on numbers nobody has ever seen. So in this version:
//   - EVERY night battle is a measurement battle. The module reads the game's own numbers, writes them in the log in French with
//     the exact values it WOULD write, and changes NOTHING. It never moves itself from measuring to active: the author reads the
//     numbers in his log first, and a later build arms the writes with those numbers checked.
//   - It checks the scale against the game's own overall cap MaxAntiVisible, which the tooltip proves caps the same product. A
//     failed check is said in the log and holds for THIS GAME SESSION only, never written into the settings file: a build that
//     corrects the reading must work at once, and the author does not edit files.
//
//  WHAT WOULD BE WRITTEN, once armed, and its bounds:
//   - the ceiling of the tell-tale is raised by half at most (x1.5), and only when the engine's own overall cap MaxAntiVisible
//     leaves room for the whole of that rise. It is never clamped silently: a cap that would swallow the rise is a refusal, said
//     in the log, because writing a value the engine's own cap absorbs while announcing an effect is exactly the failure this
//     module exists to avoid;
//   - FlashReduction is NOT written, in this version or in the armed one, until its direction is proven. Nothing in the dumps says
//     whether the engine SUBTRACTS it from the tell-tale every second or MULTIPLIES the tell-tale by it. WeaponFlashSystem.Update
//     (Single elapsedTime, ReadOnlySpan<Entity>) with a SimpleTimer _everySecond proves a per-second pass and nothing else. If it
//     is a retention factor in 0..1, halving it would make the tell-tale vanish FASTER at night - the exact opposite of the
//     feature, with a log line claiming the opposite of what happened. The number is measured and printed instead. The one range
//     that would settle it is FlashReduction > 1: a multiplicative retention factor cannot be above 1, so a value above 1 proves
//     subtraction. The log now carries the number, and the decision belongs to whoever reads it, not to this file;
//   - both sides read the same config object, so the change is exactly symmetric;
//   - a unit that does not fire is untouched.
//
//  HOW IT BEHAVES: no Harmony patch, no detour, no ECS component read, nothing on any hot path. One property read and, once armed,
//  one property write every 2 s, from the tab's frame, through the journaled write helper under its own lot (NUIT_SIGNATURE_TIR), so
//  stopping the mod puts the game's own numbers back. Solo only, inert in a mission played without the mod (US_M01), inert when the
//  mod is off. Crash guard like HeureDuJour and Epaves, error counter with kill-switch at 20.
//
//  NO PLAYER-VISIBLE STRING IS ADDED: every text here is a log line in French. A Mod tab row would need TxtKey entries in Txt.cs,
//  which this file does not own.
using System;
using System.Globalization;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using EnvService = Il2CppBrokenArrow.BrokenArrow.Client.Ecs.MapEnvironment.EnvironmentService;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    static class Nuit
    {
        const string GuardVersion = "1.0";
        const int MaxErrors = 20, MaxUnclean = 3, MaxWrites = 40;
        const float TickEvery = 2f;                                  // the hour is read at most every 2 s, in the menus as well

        /// The tell-tale of a shot at night reaches half again as far as by day - and never further than the engine's own cap.
        const float FacteurPlafond = 1.5f;
        /// What "twice as long to fade" would be, IF the direction of FlashReduction were ever proven. It is not, so nothing uses
        /// this to write: it only appears in the log line that names the value the mod is deliberately not touching.
        const float FacteurDuree = 2f;

        // Plausibility windows. Outside them the field is not what this module thinks it is, and NOTHING is written.
        const float AntiVisibleMin = 0.5f, AntiVisibleMax = 10f;     // MaxAntiVisible is a multiplier on the observer's optics (1.5 expected)
        // FlashReduction is never written (its direction is unproven), but a value far outside this window would mean the module is
        // not reading the field it thinks it is - and then its reading of the neighbouring MaxFlashValue is suspect too.
        const float ReductionMin = 0.0001f, ReductionMax = 100f;

        internal const string PrefCat = "RealismOverhaul_Nuit";
        /// Own journal lot: RestoreLot takes these two writes out without touching a single value of the real stats.
        internal const string LotNuit = "NUIT_SIGNATURE_TIR";

        // stages, kept per game version: 0 = measuring (nothing is written), 1 = active, -1 = refused for this build of the game
        const int EtMesure = 0, EtActif = 1, EtRefus = -1;

        static MelonPreferences_Entry<int> _etape, _unclean;
        static MelonPreferences_Entry<string> _versionJeu, _guardVersion;

        // ------------------------------------------------------------ state
        static bool _prefsOk, _dead, _pauseDite, _armed, _refusDits;
        /// A failed scale check holds for THIS GAME SESSION only and is never written to the settings file: a build that corrects
        /// the reading must work the moment it is installed, and the author does not edit files by hand.
        static bool _refusSession;
        static int _errors, _writes, _illisible;                     // ticks in a row where the hour of the battle could not be read
        static float _next;

        static FogOfWarConfig _cfg;                                  // held: a ScriptableObject that lives on between battles
        static IntPtr _cfgPtr;
        static float _vanPlafond = -1f, _vanReduction = -1f, _vanAnti = -1f;   // the game's own values, read before anything of ours
        static bool _lu, _luRate, _ecrit;                            // values read / the failure was said once / something of ours is in place

        static GameSessionContext _ctx;                              // held: the battle we are in (its address is never reused)
        static IntPtr _ctxPtr;
        static bool _biland, _nuitDite, _mesureIci;                  // the battle had its line / night was answered / this battle IS the measurement one
        static bool _everOnline, _netLogged;

        static void Log(string s) => Mod.Log.Msg("[NUIT] " + s);

        static string Nb(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // ============================================================ preferences
        /// Called from Patch_ModTabInit.Prepare, i.e. during Mod.ApplyPatches, next to Epaves.CreatePrefs().
        internal static void CreatePrefs()
        {
            if (_prefsOk) return;
            _prefsOk = true;
            var c = MelonPreferences.CreateCategory(PrefCat);
            _etape = c.CreateEntry("Etape", EtMesure, description: Build.Desc(
                "Sécurité automatique, ne pas modifier. 0 = le mod mesure seulement et n'écrit rien, 1 = la signature de tir de nuit "
                + "est appliquée. Le mod ne passe jamais de 0 à 1 tout seul : c'est une version suivante du mod qui l'activera, "
                + "une fois les chiffres du journal vérifiés."));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _versionJeu = c.CreateEntry("VersionJeu", "", description: Build.Desc("Réglage automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; _etape.Value = EtMesure; }
            // a game update can change the fog-of-war numbers: the measurement starts again from zero
            string game = "?";
            try { game = UnityEngine.Application.version; } catch { }
            if (_versionJeu.Value != game) { _versionJeu.Value = game; _etape.Value = EtMesure; _unclean.Value = 0; }
        }

        static void Save()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Mod.Log.Warning("[NUIT] enregistrement des réglages impossible : " + e.GetBaseException().Message); }
        }

        /// The stage is READ here and never written by the module itself. It stays at 0 (measure only) unless a later build of the
        /// mod, with the measured numbers checked by a human, ships it at 1. Nothing here can promote itself to writing.
        static int EtapeCourante => _etape == null ? EtMesure : _etape.Value;

        // ============================================================ error counter and kill-switch
        static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3) Mod.Log.Warning("[NUIT] erreur : " + e.GetBaseException().Message);
            if (_errors < MaxErrors || _dead) return;
            _dead = true;
            Rendre("20 erreurs");
            Desarmer();
            Mod.Log.Warning($"[NUIT] {MaxErrors} erreurs : signature de tir de nuit coupée jusqu'au redémarrage du jeu "
                            + "(le reste du mod fonctionne)");
        }

        // ============================================================ the only entry point (ModTab.Frame, every frame, self-throttled)
        /// Never throws: the tab's frame is wrapped by a guard that kills the whole tab for the session on the first exception.
        internal static void Tick()
        {
            try
            {
                if (!_prefsOk || _dead) return;
                float now;
                try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
                if (now < _next) return;
                _next = now + TickEvery;
                Body();
            }
            catch (Exception e) { try { Fail(e); } catch { } }
        }

        /// Optional, and safe to leave unwired: the journal is taken out by Realism.Restore at quit anyway, and the 2 s tick puts the
        /// game's own numbers back as soon as the battle is over. Wired next to the other OnBattleEnd calls it simply makes it immediate.
        internal static void OnBattleEnd() { try { Rendre("fin de bataille"); Desarmer(); } catch { } }

        static void Body()
        {
            // the battle we are in: its change opens a new per-battle line and closes the crash guard of the previous one
            SuivreBataille();

            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive || Identite.Blocked) { Rendre("mod coupé"); Desarmer(); return; }
            // a mission played without the mod (US_M01): Realism.Restore has normally taken our writes out already, and asking the lot
            // again costs nothing when it holds nothing
            if (Campaign.MissionInerte) { Rendre("mission jouée sans le mod"); Desarmer(); return; }

            DireLesRefus();

            // the game's own values are read (never written) before anything else, so the log carries the measurement even for a player
            // who never fights a single night battle
            var cfg = Config();
            if (cfg == null) return;

            if (!Solo()) { Rendre("partie en réseau"); Desarmer(); return; }

            if (_unclean != null && _unclean.Value >= MaxUnclean)
            {
                Rendre("parties non terminées normalement");
                if (!_pauseDite)
                {
                    _pauseDite = true;
                    Mod.Log.Warning($"[NUIT] {_unclean.Value} partie(s) non terminée(s) normalement : signature de tir de nuit en pause "
                                    + "par sécurité (le reste du mod fonctionne)");
                }
                return;
            }

            // outside a battle (menus, loading): the game gets its own numbers back and keeps them
            if (_ctx == null) { Rendre("hors bataille"); Desarmer(); return; }

            string cle = null, vfx = null;
            bool nuit = EstNuit(out cle, out vfx);

            // The hour is not readable yet (the first ticks of a battle, a scene still loading): nothing is said and nothing is written.
            // Saying "day" here would lock the battle's one line on a wrong answer, and a mission whose script turns the light to night
            // in the middle of the battle would then be written without its measurement ever having been logged.
            // A battle where it stays unreadable says so once, rather than going quiet without a reason.
            if (vfx == null)
            {
                if (++_illisible == 30 && !_biland)
                {
                    _biland = true;
                    // the mod cannot know whether the ambience service is missing or simply not registered on this path: the wording
                    // names what the mod did, not a cause it has not established
                    Log("heure de cette bataille illisible par ce chemin (service d'ambiance non trouvé par le mod) : rien n'est "
                        + "changé, la signature de tir garde les valeurs du jeu");
                }
                return;
            }
            _illisible = 0;

            if (!nuit)
            {
                BilanJour(cle, vfx);
                Rendre("bataille de jour");
                Desarmer();
                return;
            }

            if (!BilanNuit(cle, vfx)) return;                          // refused, or this battle is the measurement battle: read only
            Ecrire(cfg);
        }

        // ============================================================ is this battle fought at night?
        /// The game's own Day/Night flag of the ambience in force, the very one LightLimitedPool uses to decide whether a muzzle flash
        /// lights the ground. Read through the game's service locator, never through a hook. Dusk reads Night too, and that is right:
        /// a muzzle flash tells at dusk as well. Unreadable = not night, and nothing is written.
        static bool EstNuit(out string cle, out string vfx)
        {
            cle = null; vfx = null;
            try
            {
                var env = Mod.Svc<EnvService>();
                var cur = env?.CurrentDayTimePreset;
                if (cur == null) return false;
                try { cle = cur.Key; } catch { }
                try { vfx = cur.DayTimeVfxParameter.ToString(); } catch { }
            }
            catch (Exception e) { Fail(e); return false; }
            return string.Equals(vfx, "Night", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================ the game's config object
        /// The FogOfWarConfig of the moment, with the game's own values read the first time each object is seen: a scene change can hand
        /// us another one, and the journal entry of the old one dies with it (RestoreLot swallows it).
        static FogOfWarConfig Config()
        {
            FogOfWarConfig cfg;
            try { cfg = GameConfig.Instance?.FogOfWarConfig; }
            catch (Exception e) { Fail(e); return null; }
            if (cfg == null) return null;
            IntPtr p;
            try { p = ((Il2CppObjectBase)cfg).Pointer; } catch { return null; }
            if (p == _cfgPtr)
            {
                var held = _cfg ?? cfg;
                if (!_lu) Lire(held);                                  // a read that failed once is tried again, never given up on
                return held;
            }
            // the held reference: its address can't be reused while we hold it
            _cfg = cfg; _cfgPtr = p; _ecrit = false; _lu = false; _luRate = false;
            _vanPlafond = _vanReduction = _vanAnti = -1f;
            Lire(cfg);
            return cfg;
        }

        /// Reads the game's own numbers off one config object, once. Said in the log the first time it works, and the first time it fails.
        static void Lire(FogOfWarConfig cfg)
        {
            try
            {
                _vanPlafond = cfg.MaxFlashValue;
                _vanReduction = cfg.FlashReduction;
                _vanAnti = cfg.MaxAntiVisible;
                _lu = true;
            }
            catch (Exception e)
            {
                _vanPlafond = _vanReduction = _vanAnti = -1f;
                if (!_luRate)
                {
                    _luRate = true;
                    Log("réglages du brouillard de guerre illisibles : " + e.GetBaseException().Message + " ; rien ne sera changé");
                }
                return;
            }
            string mini = "?", spotted = "?";
            try { mini = Nb(cfg.MinimumDetectionDistance); } catch { }
            try { spotted = Nb(cfg.SpottedPenalty); } catch { }
            // THE measurement the whole feature waited for: these two numbers had never been read in a live log.
            Log($"réglages du jeu lus (jamais devinés, jamais encore relevés jusqu'ici) : plafond de la signature de tir "
                + $"(MaxFlashValue) = {Nb(_vanPlafond)} ; effacement de la signature par seconde (FlashReduction) = {Nb(_vanReduction)} ; "
                + $"plafond général de visibilité (MaxAntiVisible) = {Nb(_vanAnti)} ; distance minimale de détection = {mini} m ; "
                + $"pénalité de repérage (SpottedPenalty) = {spotted}");
        }

        // ============================================================ the scale check
        /// The game's own tooltip says MaxAntiVisible caps the product of stealth, cover AND flash. So the ceiling of the firing
        /// tell-tale must live on that same scale: above zero, and not above the overall cap. If it does not, this module's reading of
        /// the two fields is wrong, and it refuses for this build of the game rather than write a number it does not understand.
        static bool Echelle(out string pourquoi)
        {
            pourquoi = null;
            if (!_lu) { pourquoi = "valeurs illisibles"; return false; }
            if (_vanAnti < AntiVisibleMin || _vanAnti > AntiVisibleMax)
            {
                pourquoi = $"le plafond général de visibilité vaut {Nb(_vanAnti)}, ce n'est pas un multiplicateur de portée de vue "
                         + $"(attendu entre {Nb(AntiVisibleMin)} et {Nb(AntiVisibleMax)})";
                return false;
            }
            if (_vanPlafond <= 0f)
            {
                pourquoi = $"le plafond de la signature de tir vaut {Nb(_vanPlafond)} : dans cette version du jeu, tirer ne trahit rien "
                         + "du tout, et le relever reviendrait à inventer une règle que le studio a éteinte";
                return false;
            }
            if (_vanPlafond > _vanAnti + 0.0001f)
            {
                pourquoi = $"le plafond de la signature de tir ({Nb(_vanPlafond)}) est au-dessus du plafond général de visibilité "
                         + $"({Nb(_vanAnti)}) : les deux ne sont pas sur la même échelle, donc la lecture du mod est fausse";
                return false;
            }
            if (_vanReduction < ReductionMin || _vanReduction > ReductionMax)
            {
                pourquoi = $"l'effacement de la signature vaut {Nb(_vanReduction)}, ce n'est pas un effacement par seconde "
                         + $"(attendu entre {Nb(ReductionMin)} et {Nb(ReductionMax)})";
                return false;
            }
            // The rise must FIT UNDER the engine's own overall cap, whole. Clamping it to MaxAntiVisible instead would let the mod
            // write a ceiling the cap swallows for exactly the units that fire most, while the log announced an effect. A cap with
            // no room left is a refusal, said out loud, not a value quietly cut down to nothing.
            if (_vanPlafond * FacteurPlafond > _vanAnti + 0.0001f)
            {
                pourquoi = $"le plafond général de visibilité du jeu ({Nb(_vanAnti)}) ne laisse pas la place de relever le plafond "
                         + $"de la signature de tir ({Nb(_vanPlafond)} x{Nb(FacteurPlafond)} = {Nb(_vanPlafond * FacteurPlafond)}) : "
                         + "ce qui serait écrit serait avalé par ce plafond général et ne changerait rien pour les unités qui tirent "
                         + "le plus";
                return false;
            }
            return true;
        }

        /// What would be written at night, once the scale check has passed. The check above has already proved there is room for the
        /// whole rise under the engine's own overall cap, so nothing is clamped here.
        /// FlashReduction is deliberately NOT part of this: its direction is unproven (see the header), so it is measured and printed,
        /// never written. The value this would use is returned only to be shown in the log.
        static void Cible(out float plafond, out float reductionSiJamaisProuvee)
        {
            plafond = _vanPlafond * FacteurPlafond;
            reductionSiJamaisProuvee = _vanReduction / FacteurDuree;
        }

        // ============================================================ the one write (only in an armed build: see the header)
        static void Ecrire(FogOfWarConfig cfg)
        {
            if (!_lu) return;
            Cible(out float plafond, out _);

            float livePlafond;
            try { livePlafond = cfg.MaxFlashValue; }
            catch (Exception e) { Fail(e); return; }

            bool besoin = Math.Abs(livePlafond - plafond) > 0.0001f;
            if (besoin)
            {
                if (_writes >= MaxWrites)
                {
                    // the game writes its own values back at every pass: we stop fighting it for THIS battle instead of writing for ever.
                    // The stage is left alone: a battle where the game took its numbers back is not a reason to refuse the next one.
                    Log($"la signature de tir est reprise par le jeu ({MaxWrites} écritures dans la même bataille) : le mod n'y touche "
                        + "plus jusqu'à la fin de cette bataille");
                    Rendre("valeurs reprises par le jeu");
                    _mesureIci = true;
                    return;
                }
                int n;
                try
                {
                    // one value only: FlashReduction is not written while its direction is unproven (see the header)
                    using (Realism.Lot(LotNuit)) n = Realism.SetJournaled(cfg, "MaxFlashValue", plafond);
                }
                catch (Exception e) { Fail(e); return; }
                if (n < 1)
                {
                    Log("plafond de la signature de tir introuvable dans cette version du jeu : rien n'est changé");
                    Rendre("valeur introuvable");
                    _refusSession = true;
                    return;
                }
                _writes++;
                // set INSIDE the branch that really wrote: otherwise Rendre() would announce values given back that were never taken
                _ecrit = true;
            }
            Armer();
        }

        /// Puts the game's own numbers back and forgets that anything of ours was written. The lot itself is asked what it holds, never
        /// our own flag alone: a write that half succeeded is taken out too.
        static void Rendre(string why)
        {
            bool tenu = false;
            try { tenu = Realism.LotWrites(LotNuit) > 0; } catch { }
            if (!_ecrit && !tenu) return;
            _ecrit = false;
            try
            {
                int n = Realism.RestoreLot(LotNuit, "signature de tir de nuit : " + why);
                Log($"signature de tir rendue au jeu ({why}) : {n} valeur(s) remise(s)");
            }
            catch (Exception e) { Mod.Log.Warning("[NUIT] valeurs non rendues : " + e.GetBaseException().Message); }
        }

        // ============================================================ crash guard (same rule as HeureDuJour and Epaves)
        static void Armer()
        {
            if (_armed || _unclean == null) return;
            _armed = true;
            _unclean.Value = _unclean.Value + 1;
            Save();
        }

        static void Desarmer()
        {
            if (!_armed) return;
            _armed = false;
            if (_unclean != null && _unclean.Value != 0) { _unclean.Value = 0; Save(); }
        }

        // ============================================================ battle tracking (read-only, no hook)
        static void SuivreBataille()
        {
            GameSessionContext ctx = null;
            try { ctx = Campaign.Ctx(); } catch { }
            IntPtr p = IntPtr.Zero;
            if (ctx != null) { try { p = ((Il2CppObjectBase)ctx).Pointer; } catch { p = IntPtr.Zero; } }
            if (p == _ctxPtr) return;
            _ctx = ctx; _ctxPtr = p;
            _biland = false; _nuitDite = false; _mesureIci = false;
            _writes = 0; _illisible = 0;
            _everOnline = false; _netLogged = false;
            Desarmer();                       // back to a menu or into another battle: the previous one ended without a crash
        }

        /// Solo latch of the battle: anything that looks like a network session makes the module inert, and so does an unreadable state.
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

        // ============================================================ the refusals, said once per game start
        /// Said plainly, because a player who was promised headlights and gets nothing deserves the reason, not silence.
        static void DireLesRefus()
        {
            if (_refusDits) return;
            _refusDits = true;
            Log("les phares n'existent pas dans ce jeu : le système de lumière ne connaît que deux sortes de lumière "
                + "(départ de coup et impact), la fiche d'une unité n'a aucun champ de feu ou de phare, et aucun symbole de phare, "
                + "de lampe ou de projecteur n'existe nulle part. Un véhicule qui roule tous feux allumés ne peut donc pas être rendu "
                + "plus repérable : ce n'est pas un choix du mod, c'est absent du jeu.");
            Log("repérage au son : non fait, refusé par l'auteur.");
            Log("vision nocturne et thermique : la liste des types de capteurs contient bien « infrarouge » et « vision nocturne », "
                + "mais rien dans le jeu ne s'en sert - un capteur n'a qu'un nom et trois portées. Aucune unité ne voit mieux ni moins "
                + "bien la nuit, et une pénalité identique pour tout le monde serait fausse et risquerait de bloquer une mission.");
            Log("bouger ne révèle rien : le calcul de repérage n'a aucun champ de vitesse, de déplacement ni de moteur. Ce qui rend "
                + "l'avance silencieuse payante ici, c'est que seul celui qui TIRE paie la nuit.");
        }

        static string Ambiance(string cle, string vfx) => $"ambiance de la carte « {cle ?? "?"} » (lumière du jeu : {vfx ?? "illisible"})";

        // ============================================================ the one line per battle
        /// Daylight: one line, once, and the module is done with this battle.
        static void BilanJour(string cle, string vfx)
        {
            if (_biland || _ctx == null) return;                       // one line per battle, never a line from a menu
            _biland = true;
            Log($"bataille de jour : {Ambiance(cle, vfx)} — la signature de tir garde les valeurs du jeu, rien n'est changé");
        }

        /// Night: said the first time night is seen in this battle, whatever the daylight line already said (a mission script can turn
        /// the light to night in the middle of a battle). Returns true when writing is allowed from now on in this battle.
        static bool BilanNuit(string cle, string vfx)
        {
            if (EtapeCourante == EtRefus || _refusSession) return false;   // refused: nothing is written until the game is restarted
            if (_nuitDite) return !_mesureIci;                         // already decided for this battle
            // values not read yet on this config object: nothing is said and nothing is decided, the next pass will try again. A pass
            // that failed to read must never turn into a permanent refusal.
            if (!_lu) return false;
            _nuitDite = true; _biland = true;
            string amb = Ambiance(cle, vfx);

            if (!Echelle(out string pourquoi))
            {
                _refusSession = true;
                // the wording carries "impossible dans cette version du jeu" on purpose: the PUBLIC log filter keeps that kind of line
                Log($"bataille de nuit : {amb} — signature de tir de nuit impossible dans cette version du jeu : {pourquoi} ; "
                    + "rien n'est changé, le jeu garde son comportement d'origine ; la mesure sera refaite au prochain démarrage du jeu");
                return false;
            }

            Cible(out float plafond, out float reduction);
            string effet = $"plafond de la signature {Nb(_vanPlafond)} -> {Nb(plafond)}";
            // the map whose DEFAULT ambience is "Evening" reads Night too: the player who chose no hour must be told that this
            // battle is being counted as a night one, or the day/night difference becomes invisible to him
            string soir = cle != null && cle.IndexOf("Night", StringComparison.OrdinalIgnoreCase) < 0
                          && cle.IndexOf("Nuit", StringComparison.OrdinalIgnoreCase) < 0
                ? $" (cette carte est en « {cle} » : le mod la compte comme une nuit, un départ de coup se voit aussi au crépuscule)"
                : "";

            if (EtapeCourante == EtMesure)
            {
                // MEASURE ONLY, and the module never promotes itself: whether the engine re-reads this value in the middle of a
                // battle has not been measured either, so a human reads the numbers below before anything is written
                _mesureIci = true;
                Log($"bataille de nuit : {amb}{soir} — MESURE SEULE, RIEN N'EST ÉCRIT dans le jeu. Ce qui serait écrit : {effet}. "
                    + $"L'effacement de la signature vaut {Nb(_vanReduction)} par seconde : le sens de ce réglage n'est pas prouvé "
                    + $"(soustraction ou multiplication), le mod n'y touche pas — la valeur « deux fois plus long » serait "
                    + $"{Nb(reduction)}. Envoyer ce journal pour que ces chiffres soient activés dans une version suivante.");
                return false;
            }

            Log($"bataille de nuit : {amb}{soir} — un départ de coup trahit plus loin : {effet} (au plus une fois et demie plus loin, "
                + $"et seulement parce que le plafond général du jeu {Nb(_vanAnti)} laisse la place). L'effacement de la signature "
                + $"({Nb(_vanReduction)} par seconde) n'est pas touché : son sens n'est pas prouvé. Les deux camps lisent exactement "
                + "le même réglage. Seul celui qui TIRE paie : une unité qui avance sans tirer est aussi difficile à voir qu'avant. "
                + "Valeur écrite ; le mod ne peut pas vérifier que le moteur la relit en cours de bataille.");
            return true;
        }
    }
}
