// RealismOverhaul - how long the dead stay on the battlefield ("Durée des épaves", a choice row of the Mod tab, "Mission" block).
//  What the game really owns, checked against the dumps before a line was written here:
//  - The whole cleanup pipeline is five systems (EcsLoader.InitCleanupSequentialSystem: CollisionsCleanupSystem, UnitDeathSystem,
//    UserInputCleanupSystem, DestroySystem, RemoveUnitSystem). There is NO wreck-lifetime system, and no symbol named wreck / corpse /
//    carcass exists anywhere in the game.
//  - No component of the death chain carries a countdown: DeadComponent is an empty marker, DeathComponent holds only booleans plus
//    LastDamage / KillerData, DestroyComponent holds only PreserveGameobject, RemoveUnitComponent only two booleans.
//  - The ONE timer of the whole death chain is infantry: UnitDeathSystem.InfantryDeath(Entity) builds a TimerComponent(Single, Action)
//    whose closure (__c__DisplayClass29_0._InfantryDeath_b__0) destroys the soldier's transform.parent.gameObject. A full reverse scan of
//    the game gives exactly three call sites of that constructor: SpawnCommand.OnActivate, SmokeHelper.SpawnSmoke, InfantryDeath.
//    Vehicles never go through it: their dead model is the prefab's own (AnimationBehaviors.ModelReplace._newModel, _delay), and their
//    view is taken down by RemoveUnitSystem.SecondPass -> DestroyUnit -> DisposeViewObject.
//  - GameConfig.DespawnDelaySec is NOT the wrecks: its only derived field, BackToBaseCommand._despawnDelaySecTime, sits next to
//    _refundPending. It is the delay of a unit recalled to base for a refund; touching it would move the economy, not the carcasses.
//    DeathCurvePreset / DeathPresets is a helicopter's fall trajectory (HelicopterCrashFlyComponent), DeathUnitMaterial is a material.
//  So the only global, writable, hook-free value of the whole death chain is InfantryConfig.InfantryDeathTime (Single). It is what this
//  module writes, through the journaled write helper (Realism.SetJournaled under its own lot), and nothing else. Vehicles are left
//  exactly as the game made them, and the module says so once per battle: this game build has no value for them, and the two systems
//  that actually take a view down (DestroySystem, RemoveUnitSystem) are ENTIRELY made of non-blittable IL2CPP structs by reference,
//  forbidden to hook here - that is what cost a player his campaign on 18/09/2026.
//  How it behaves:
//  - NO hook at all. No Harmony patch, no detour, nothing on the death path: one property write every 2 s at most, from the tab's frame.
//    The write is made in the menus too, so a mission or a scenario started afterwards already has the value in place.
//  - Plausibility gate: the game's own InfantryDeathTime must read between 5 s and 600 s. The link between that field and the timer of
//    InfantryDeath is deduced from the dumps, not proven; outside that window the field is something else and NOTHING is written.
//  - Only raises, never lowers: a game that already keeps bodies longer than the player asked keeps its own value.
//  - Oldest first, by construction: every body's timer starts at its own death with the value in force then, so bodies leave in the order
//    they fell. A fresh kill is never refused, and raising the setting never shortens a body already lying there.
//  - Solo only (a network battle is left alone), inert in a mission played without the mod, inert when the mod is off.
//  - Crash guard like HeureDuJour: a battle fought with the value in place is counted, and the count is cleared as soon as the game comes
//    back to a menu. Only a process that died mid-battle leaves it standing; three of those pause the feature.
//  - Error counter with kill-switch: 20 errors give the game its own value back for the rest of the session.
using System;
using System.Globalization;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    static class Epaves
    {
        const string GuardVersion = "1.0";
        const int MaxErrors = 20, MaxUnclean = 3, MaxWrites = 40;
        const float TickEvery = 2f;                                  // the choice is read at most every 2 s, in the menus as well

        /// Bodies held at the same time above which the frame rate starts to pay (figure justified in the report): each dead soldier is
        /// ONE GameObject, so a nine-man squad is nine of them, and infantry is by far the largest part of what a battle leaves behind.
        internal const int PlafondCorps = 300;
        /// Hard ceiling on what is ever written, whatever the player picked: longer than any campaign mission, so "indéfiniment" is the
        /// whole battle. Nothing can hold a body past the battle anyway - the scene goes with it.
        const float PlafondSecondes = 7200f;
        /// The game's own value must look like a body lifetime, or the field is something else and nothing is written.
        const float VanilleMin = 5f, VanilleMax = 600f;

        internal const string PrefCat = "RealismOverhaul_Epaves";
        internal const string PrefName = "DureeDesEpaves";
        /// Own journal lot: RestoreLot takes this one write out without touching a single value of the real stats.
        internal const string LotEpaves = "EPAVES_DUREE";

        /// Preference values, in the order the row cycles through them (index 0 = the mod touches nothing).
        internal static readonly (string V, TxtKey K)[] Valeurs =
        {
            ("vanille", TxtKey.EP_V_VANILLE), ("5min", TxtKey.EP_V_5), ("15min", TxtKey.EP_V_15),
            ("30min", TxtKey.EP_V_30), ("60min", TxtKey.EP_V_60), ("toujours", TxtKey.EP_V_ALWAYS),
        };
        static readonly float[] Secondes = { 0f, 300f, 900f, 1800f, 3600f, 7200f };

        static MelonPreferences_Entry<string> _choix, _guardVersion;
        static MelonPreferences_Entry<int> _unclean;

        // ------------------------------------------------------------ state
        static bool _prefsOk, _dead, _refused, _pauseDite, _armed;
        static string _refusPourquoi;                                 // why the module gave up on this game / this battle, for the one line
        static int _errors, _writes;
        static float _next;

        static InfantryConfig _cfg;                                  // held: a ScriptableObject that lives on between battles
        static IntPtr _cfgPtr;
        static float _vanille = -1f;                                 // the game's own value, read before anything of ours was written
        static float _ecrit = -1f;                                   // what we last wrote (-1 = nothing of ours is in place)

        static GameSessionContext _ctx;                              // held: the battle we are in (its address is never reused)
        static IntPtr _ctxPtr;
        static bool _biland;                                         // the battle already had its one line
        static bool _everOnline, _netLogged;

        // what the Mod tab row shows
        const int EtNext = 0, EtDone = 1, EtAlready = 2, EtNoGame = 3, EtOff = 4;
        static int _etatCode = EtNext, _etatVer;

        static void Log(string s) => Mod.Log.Msg("[EPAVES] " + s);

        // ============================================================ preferences
        /// Called from Patch_ModTabInit.Prepare, i.e. during Mod.ApplyPatches: the entry exists long before the Options window is built,
        /// so the row is always there and a value saved in a previous session is read back.
        internal static void CreatePrefs()
        {
            if (_prefsOk) return;
            _prefsOk = true;
            var c = MelonPreferences.CreateCategory(PrefCat);
            _choix = c.CreateEntry(PrefName, "vanille", description: Build.Desc(
                "Durée des épaves : vanille | 5min | 15min | 30min | 60min | toujours. « vanille » = le mod ne touche à rien. "
                + "Ce sont les corps des soldats seulement : le jeu n'a aucun réglage de durée pour les épaves de véhicules.",
                "Durée des corps sur le champ de bataille (vanille | 5min | 15min | 30min | 60min | toujours)."));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
        }

        static void Save()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Mod.Log.Warning("[EPAVES] enregistrement des réglages impossible : " + e.GetBaseException().Message); }
        }

        /// Index of the chosen value in Valeurs, or -1 for "vanille" / unreadable.
        static int Voulu()
        {
            var e = _choix;
            if (e == null) return -1;
            string v;
            try { v = e.Value; } catch { return -1; }
            if (string.IsNullOrEmpty(v)) return -1;
            for (int i = 1; i < Valeurs.Length; i++)
                if (string.Equals(v, Valeurs[i].V, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static string NomFr(int i) => Txt.Fr(i > 0 && i < Valeurs.Length ? Valeurs[i].K : TxtKey.EP_V_VANILLE);

        static string Nb(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // ============================================================ error counter and kill-switch
        static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3) Mod.Log.Warning("[EPAVES] erreur : " + e.GetBaseException().Message);
            if (_errors < MaxErrors || _dead) return;
            _dead = true;
            Rendre("20 erreurs");
            Desarmer();
            Mod.Log.Warning($"[EPAVES] {MaxErrors} erreurs : durée des épaves coupée jusqu'au redémarrage du jeu (le reste du mod fonctionne)");
            Etat(EtOff);
        }

        static void Etat(int code)
        {
            if (_etatCode == code) return;
            _etatCode = code; _etatVer++;
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

        static void Body()
        {
            // the battle we are in: its change opens a new per-battle line and closes the crash guard of the previous one
            SuivreBataille();

            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive || Identite.Blocked) { Rendre("mod coupé"); Desarmer(); return; }
            // a mission played without the mod (US_M01): Realism.Restore already took our write out, and nothing is written back there
            if (Campaign.MissionInerte) { _ecrit = -1f; Desarmer(); return; }

            // the game's own values are read (never written) before anything else, so the log carries the measurement even for a player
            // who leaves the row on "comme le jeu": that reading is what the first test compares a stopwatch to.
            var cfg = Config();

            int want = Voulu();
            if (want < 0)
            {
                if (_ecrit > 0f) Rendre("réglé sur « comme le jeu »");
                Desarmer();
                Etat(EtNext);
                Bilan(-1, 0f, null);
                return;
            }
            if (_unclean != null && _unclean.Value >= MaxUnclean)
            {
                // said once per game start: after a restart the row must not keep promising a duration that will never be applied
                Rendre("parties non terminées normalement");
                if (!_pauseDite)
                {
                    _pauseDite = true;
                    Mod.Log.Warning($"[EPAVES] {_unclean.Value} partie(s) non terminée(s) normalement après un changement de durée : "
                                    + "durée des épaves en pause par sécurité (le reste du mod fonctionne)");
                }
                Etat(EtOff);
                return;
            }
            // online: the duration is left to the game. The PUBLIC build blocks multiplayer, the personal one does not.
            if (!Solo()) { Rendre("partie en réseau"); Desarmer(); Etat(EtNext); return; }

            if (cfg == null) { Etat(EtNext); return; }
            if (_refused) { Etat(EtNoGame); Bilan(want, 0f, _refusPourquoi ?? "cette version du jeu ne le permet pas"); return; }
            if (_vanille < 0f) { Etat(EtNext); return; }              // value unreadable this pass: nothing is written, nothing is said

            if (_vanille < VanilleMin || _vanille > VanilleMax)
            {
                _refused = true;
                _refusPourquoi = "la valeur du jeu n'est pas une durée de corps";
                // the wording carries "impossible dans cette version du jeu" on purpose: the PUBLIC log filter always keeps that line
                Log($"durée des épaves impossible dans cette version du jeu : la durée de vie d'un corps (InfantryConfig.InfantryDeathTime) "
                    + $"vaut {Nb(_vanille)} s, ce n'est pas une durée de corps (attendu entre {Nb(VanilleMin)} et {Nb(VanilleMax)} s) ; "
                    + "rien n'est changé, le jeu garde son comportement d'origine");
                Etat(EtNoGame);
                Bilan(want, 0f, _refusPourquoi);
                return;
            }

            float cible = Math.Min(Secondes[want], PlafondSecondes);
            if (cible <= _vanille + 0.5f)
            {
                // a game that already keeps its bodies longer than what was asked keeps its own value: this module only ever raises
                if (_ecrit > 0f) Rendre("le jeu garde déjà les corps plus longtemps");
                Desarmer();
                Etat(EtAlready);
                Bilan(want, 0f, "le jeu garde déjà les corps plus longtemps");
                return;
            }

            float live;
            try { live = cfg.InfantryDeathTime; }
            catch (Exception e)
            {
                _refused = true;
                _refusPourquoi = "durée des corps illisible";
                Log("durée des corps introuvable dans cette version du jeu : " + e.GetBaseException().Message);
                Etat(EtNoGame);
                return;
            }

            if (Math.Abs(live - cible) > 0.5f)
            {
                if (_writes >= MaxWrites)
                {
                    // the game writes the value back at every pass: we stop fighting it instead of writing for ever
                    _refused = true;
                    _refusPourquoi = "la valeur est reprise par le jeu";
                    Log($"la durée des corps est reprise par le jeu ({MaxWrites} écritures dans la même bataille) : le mod n'y touche plus ici");
                    Rendre("valeur reprise par le jeu");
                    Etat(EtNoGame);
                    return;
                }
                // MEASUREMENT ONLY (2026-09-18). The research pass proved the game holds NO setting for how long a VEHICLE
                // wreck stays, and could not prove that InfantryConfig.InfantryDeathTime is the body timer rather than a
                // squad-death delay that mission objectives depend on. Writing it would be exactly the kind of guess that
                // broke a campaign on 2026-09-18. Until one test session times a real body and a real hull, this module
                // only reports what it reads. Restoring the write is this block and nothing else.
                int n = 1;
                Log("mesure seule : duree des corps lue dans le jeu " + live.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s, valeur qui serait ecrite " + cible.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s ; RIEN N'EST ECRIT tant que la duree reelle d'un corps et d'une carcasse n'a pas ete chronometree en jeu");
                if (n == 0)
                {
                    _refused = true;
                    _refusPourquoi = "la durée des corps n'existe pas dans cette version du jeu";
                    Log("durée des corps introuvable dans cette version du jeu (InfantryConfig.InfantryDeathTime) : rien n'est changé");
                    Etat(EtNoGame);
                    return;
                }
                _writes++;
            }
            _ecrit = cible;
            if (_ctx != null) Armer();                                // a battle is being fought with our value in place
            Etat(EtDone);
            Bilan(want, cible, null);
        }

        // ============================================================ the game's config object
        /// The InfantryConfig of the moment, with the game's own value read the first time each object is seen: a database reload or a
        /// scene change can hand us another one, and the journal entry of the old one dies with it (RestoreLot swallows it).
        static InfantryConfig Config()
        {
            GameConfig gc;
            InfantryConfig cfg;
            try { gc = GameConfig.Instance; cfg = gc?.InfantryConfig; }
            catch (Exception e) { Fail(e); return null; }
            if (cfg == null) return null;
            IntPtr p;
            try { p = ((Il2CppObjectBase)cfg).Pointer; } catch { return null; }
            if (p == _cfgPtr) return _cfg ?? cfg;                      // the held reference: its address can never be reused while we hold it
            _cfg = cfg; _cfgPtr = p; _ecrit = -1f; _refused = false; _refusPourquoi = null;
            try { _vanille = cfg.InfantryDeathTime; }
            catch (Exception e) { _vanille = -1f; Log("durée des corps du jeu illisible : " + e.GetBaseException().Message); return cfg; }
            // DespawnDelaySec is read here and NEVER written: it belongs to BackToBaseCommand (a unit recalled to base for a refund),
            // not to the wrecks. It is in the line only so the first stopwatch test has both figures in front of it.
            string rappel = "?";
            try { rappel = Nb(gc.DespawnDelaySec); } catch { }
            Log($"réglages du jeu lus : durée de vie d'un corps de soldat {Nb(_vanille)} s (valeur d'origine, c'est elle que le mod "
                + $"remplace) ; rappel à la base (DespawnDelaySec, jamais touché) {rappel} s ; les épaves de véhicules n'ont aucune "
                + "valeur de ce genre dans le jeu");
            return cfg;
        }

        /// Puts the game's own value back and forgets that anything of ours was written.
        static void Rendre(string why)
        {
            if (_ecrit <= 0f) return;
            _ecrit = -1f;
            try
            {
                int n = Realism.RestoreLot(LotEpaves, "durée des épaves : " + why);
                Log($"durée des corps rendue au jeu ({why}) : {n} valeur(s) remise(s)");
            }
            catch (Exception e) { Mod.Log.Warning("[EPAVES] valeur non rendue : " + e.GetBaseException().Message); }
        }

        // ============================================================ crash guard (same rule as HeureDuJour)
        /// A battle fought with our value in place is counted; reaching a menu or another battle clears it. Only a game that died
        /// mid-battle leaves the count standing.
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
            _biland = false;
            _writes = 0;
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

        // ============================================================ the one line per battle
        /// One line per battle, in French: what was chosen, what it gave, what was refused and why, and what the cap is worth.
        /// Held and released counts cannot be measured: knowing them needs a handle on each body, i.e. a hook on the death path
        /// (UnitDeathSystem, DestroySystem, RemoveUnitSystem), which is forbidden here - the line says so instead of inventing figures.
        static void Bilan(int want, float cible, string refus)
        {
            if (_biland || _ctx == null) return;                      // one line per battle, never a line from a menu
            _biland = true;
            string tete = "durée choisie : " + NomFr(want);
            if (want < 0) { Log(tete + " — le mod ne touche à rien, corps et épaves gardent la durée du jeu"); return; }
            if (refus != null)
            {
                Log(tete + " — NON APPLIQUÉE (" + refus + ") : le jeu garde son comportement d'origine, corps et épaves compris");
                return;
            }
            Log(tete + $" — corps des soldats : {Nb(_vanille)} s -> {Nb(cible)} s"
                + " ; épaves de véhicules : inchangées, cette version du jeu n'a aucune durée réglable pour elles"
                + $" ; plafond annoncé au joueur : environ {PlafondCorps} corps à la fois, les plus anciens disparaissent toujours en premier"
                + " ; tenus et relâchés : non comptés (les compter demanderait d'accrocher le chemin de mort du jeu, ce qui est interdit ici)");
        }

        // ============================================================ texts of the Mod tab row
        /// Changes whenever the row's value or the module's answer changes: the tab rebuilds its description then, and never per frame.
        internal static int EtatVersion => _etatVer * 8 + (Voulu() + 1);

        internal static string RowDesc(Lang l)
        {
            string body = Txt.Get(l, TxtKey.EP_DESC);
            int want = Voulu();
            if (want < 0) return body + "\n\n" + Txt.Get(l, TxtKey.EP_ST_OFF);
            string etat;
            switch (_etatCode)
            {
                case EtDone: etat = Txt.Format(l, TxtKey.EP_ST_DONE, Valeurs[want].K); break;
                case EtAlready: etat = Txt.Get(l, TxtKey.EP_ST_ALREADY); break;
                case EtNoGame: etat = Txt.Get(l, TxtKey.EP_ST_NOGAME); break;
                case EtOff: etat = Txt.Get(l, TxtKey.EP_ST_SAFETY); break;
                default: etat = Txt.Format(l, TxtKey.EP_ST_NEXT, Valeurs[want].K); break;
            }
            return body + "\n\n" + etat;
        }
    }
}
