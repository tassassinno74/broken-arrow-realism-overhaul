// RealismOverhaul - weather of a campaign mission. WHAT THE GAME HAS, measured before anything was designed (2026-09-19):
//  - The word "weather" does not exist anywhere in Broken Arrow 1.2.0.3: zero hit in the type dump, the member dump and the ECS dump.
//    "fog" is only the FogOfWar detection system (FogOfWarUnit / FogOfWarConfig / FogOfWarComponent), "rain" is only the substring of
//    "terrain", "cloud" is only Steam cloud saves and the HDRP quality settings SettingType.VolumetricClouds / VolumetricFog. There is
//    no weather state, no weather service, no weather component, no wind but Effects.WindState (a Vector3 and a Single used by the
//    grass and water shaders) and SceneDayTimePresetData.WindEvent (an FMOD ambience name).
//  - Weather exists ONLY as a spelling inside the scene's day-time preset list, chosen per mission by the studio. The US campaign,
//    read from the missions' own head.json: M01 Ruda "Day (default)", M02 Central Village "Morning (Default)", M03A Baltiisk
//    "Day_Overcast (Default)", M03B Airbase "Day_Rainy (default)", M03C "Evening (Default)", M04 "Night", M05N Klaipeda
//    "Day overcast (Default)", M05S "Night (Default)", M06N "Day (default)", M06S "Evening", M07 "Evening (Default)". So three of
//    eleven missions already run a weather preset, and the studio's spelling is inconsistent ("Day_Overcast" against "Day overcast"):
//    a key is therefore read as text, never matched against a hand-written list.
//  - A SceneDayTimePresetData holds a PostProcessingPrefab (a rendering Volume), three FMOD ambience names and a Day/Night vfx flag.
//    It is a picture and a sound. It changes NO number of the simulation: the detection model reads distance, terrain, smoke, forest,
//    stealth and weapon flash (FogOfWarConfig, VisibleTerrainType, FilterTask) and has no input for weather, exactly as HeureDuJour.cs
//    proved it has none for the hour.
//  So this file is in the second world: the game's weather is decoration, and the simulation side is added here. THE PICTURE AND THE
//  EFFECT ARE TWO SEPARATE THINGS TIED TOGETHER BY THIS FILE. The rain the player sees is the studio's; the shorter sight is the mod's.
//
// WHAT IS CHANGED, and nothing else: Sensors.OpticsGround, the distance at which a unit sees a GROUND unit. Rain 0.75, overcast 0.90,
//  clear 1.00, both sides by construction (one database, one set of Sensors rows, no side in them). Real basis: in moderate rain the
//  meteorological range falls to 2-6 km and a thermal imager loses 20 to 40 % of its range, water droplets absorbing the 8-12 um band;
//  under heavy overcast the loss is the contrast only, about a tenth. A tank at 2000 m sees at 1500 m in the rain, at 1800 m under
//  overcast. Never below a floor (250 m, and never below FogOfWarConfig.MinimumDetectionDistance), never raised.
//  OpticsLowAltitude and OpticsHighAltitude are DELIBERATELY NOT TOUCHED: a Sensors row is one number per altitude band with no type
//  beside it, so optics and radar cannot be told apart in the data (SensorType exists as an enum but nothing on the row stores it).
//  Cutting them would blind radar SAMs in the rain, which is neither real nor playable, and would undo the anti-air balance of the
//  other modules. Said plainly in the log so the player is never left guessing.
//
// HOW, and why it is safe:
//  - One lazy postfix on EnvironmentService.ApplyDayTimePreset(String sceneName, String presetKey), own Harmony id, never removed.
//    Two strings, no struct, no reference parameter: nothing of the hard rule about non-blittable structs by reference applies here.
//    It is the call the game itself makes at every scenario load, and the call HeureDuJour.cs makes when the player has chosen an
//    hour, so whoever applies last, this file sees the key that really ends up in place. The two modules never fight: HeureDuJour
//    hooks Init and WRITES the preset, this one hooks ApplyDayTimePreset and only READS it.
//  - It runs during the loading screen, before the units of the battle are spawned, which is the only moment that works: a unit's
//    FogOfWarComponent takes its three distances in its constructor at spawn, and an ECS component is never read here (no Get<T>).
//  - Every value goes through the journal under its own lot, OPTION_METEO, and only that lot is taken back at the end of the battle.
//    OpticsGround is already written by the real stats under OPTION_CAPTEURS_SOL_REELS, so OPTION_METEO is a LAYER on top of it: the
//    weather factor is applied to whatever value the real stats left, and giving the lot back puts that value back, never the raw
//    value of the game. That is what the journal's layers are for, and it is why a weather that lasts one battle can never eat a
//    real stat. The Sensors table rows are already in the copies plan of CopiesUnites.cs, and a copy made later takes the table row's
//    CURRENT value, so the units spawned after this write follow on their own; the copies that already exist are written here as well.
//  - Stage 0 measures and writes nothing. Stage 1 applies. The move from 0 to 1 is the house rule of the other modules: it happens by
//    itself after ONE battle where the measurement proved the data path, never before. The gate is written below at Portes(), and every
//    refusal of that gate is logged: a gate that will never open must be readable in the log, never silent.
//  - Error counter with kill-switch back to vanilla, crash guard, no allocation per frame, one apply per loading screen.
//  - The setting ships on "mesure" for this first release. Rain and overcast shorten ground sight for BOTH camps, and a scripted
//    objective that waits for a spot is the one thing that can stall a campaign mission (Nuit.cs refuses a uniform spotting penalty in
//    writing for that exact reason). US_M03A Baltiisk, US_M03B Airbase and US_M05N Klaipeda must be played through with the effect on
//    before it is advertised; the player turns it on by putting the Mod tab row on "auto", and stage 1 then arms after one battle.
// WHAT THE WEATHER DOES NOT REACH, said here and in the log because it is visible in play: the units whose optics this mod writes per
//  unit (Affuts.cs, reel/CapteursParUnite.csv - attack helicopters, recon vehicles) get their own ABSOLUTE value rewritten onto their
//  sensor clone every time the game walks them, which happens after this file has written. Those units keep their clear-weather sight.
//  The journal keeps everything consistent (giving the lot back puts Affuts' value back), but the count is printed instead of hidden.
// MEASUREMENT [METEO] (stage 0): one line per mission load gives the map, the mission, the preset key the game ended on, what that key
//  was read as, the game's own OpticsGround values (rows, smallest, middle, largest), the detection floor, and the state of the unit
//  copies (how many Sensors objects the loaded units reach, how many are table rows, how many are copies, how many copies carry an
//  optic this mod wrote per unit, how many disagree with their table row for a reason nobody here can name). The first of those two
//  counts is normal and expected; only the second would say the data model is not what this file believes it is.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using EnvService = Il2CppBrokenArrow.BrokenArrow.Client.Ecs.MapEnvironment.EnvironmentService;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using DbSource = Il2CppBrokenArrow.DataBase.DataBaseSourceData;
using SensorRow = Il2CppBrokenArrow.DataBase.Models.Sensors;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using OptionRow = Il2CppBrokenArrow.DataBase.Models.Options;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace RealismOverhaul
{
    static class Meteo
    {
        const string GuardVersion = "1.0";
        const int MaxErrors = 20, MaxUnclean = 3;

        /// The lot every value of this file is written under, and the only one taken back at the end of a battle.
        internal const string LotMeteo = "OPTION_METEO";

        // ------------------------------------------------------------ what a preset key is read as
        internal const int WClair = 0, WCouvert = 1, WPluie = 2, WCount = 3;
        static readonly string[] NomsFr = { "temps clair", "temps couvert", "pluie" };

        /// Ground sight kept, per weather. Clear is 1 and nothing is ever written for it.
        ///  pluie 0.75: moderate rain, meteorological range 2-6 km, thermal range down 20-40 %.
        ///  couvert 0.90: low cloud, no precipitation; the loss is contrast and light, not the air itself.
        /// The author's rule, "realistic but not unplayable": these are the small end of the real range, on purpose.
        static readonly float[] FacteurSol = { 1.00f, 0.90f, 0.75f };

        const float PlancherSol = 250f;               // a unit never ends up blinder than this, whatever the weather
        const float ScanChaque = 0.5f;                // seconds between two frame passes (a timer read and nothing else)

        // ------------------------------------------------------------ preferences
        internal const string PrefCat = "RealismOverhaul_Meteo";
        internal const string PrefName = "Meteo";

        /// Preference values, in the order the row cycles through them.
        ///  "auto"   = measure on the first battle, then apply from the next mission load on.
        ///  "mesure" = never writes anything, only the measurement line. THE DEFAULT of this first release, on purpose: see the header.
        ///  "off"    = nothing at all, not even a detour.
        internal static readonly string[] Valeurs = { "auto", "mesure", "off" };

        /// What the setting starts on. "mesure" until the three rainy/overcast campaign missions have been played with the effect on:
        /// a uniform ground-sight cut is the one change that can stall a scripted objective waiting for a spot.
        const string ChoixDefaut = "mesure";

        static MelonPreferences_Entry<string> _choix, _versionJeu, _guardVersion;
        static MelonPreferences_Entry<int> _etape, _unclean;

        // ------------------------------------------------------------ state
        static HarmonyLib.Harmony _harmony;
        static volatile bool _armed;                  // disarm flag: the postfix reads it first and returns at once
        static bool _patchTried, _patchOk, _refused, _dead, _sessionArmed, _pauseDite;
        static bool _midDit, _onlineDit;              // one line each per battle / per session, so a refusal is said once and never repeated
        static int _errors, _wait, _porteLogs;

        static IntPtr _srcPtr;                        // database the values were written into: a reload makes our writes vanish with it
        static IntPtr _gcPtr;
        static float _next, _battleSeen;
        static bool _pose;                            // set by the postfix during the loading screen, cleared on the first live frame

        static int _temps = WClair;                   // weather of the mission now loading
        static string _cle, _carte, _mission;
        static bool _ecrit;                           // stage 1 wrote for this loading screen
        static int _nEcrits, _nCopies;                // sensors written: table rows, then copies already loaded
        static float _avantMoyen, _apresMoyen;

        // measurement of the last loading screen (kept for the Mod tab and the gate)
        // _mEcartMod = copies that disagree with their table row BECAUSE THIS MOD wrote an optic on them per unit (Affuts): expected.
        // _mEcart    = copies that disagree for a reason nobody here can name: that one, and only that one, would be a finding.
        static int _mRows, _mReach, _mTable, _mCopie, _mEcart = -1, _mEcartMod;
        static float _mMin, _mMed, _mMax, _mPlancher;
        static bool _mLue;

        // what the Mod tab shows
        const int EtMesure = 0, EtApplique = 1, EtClair = 2, EtOff = 3, EtFail = 4, EtAttente = 5;
        static int _etatCode = EtMesure, _etatA = -1, _etatVer;

        static bool _everOnline, _netLogged;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // reusable buffers: nothing is allocated on a frame path (these are filled during the loading screen only)
        static readonly List<SensorRow> _rows = new();          // the sensors table
        static readonly HashSet<IntPtr> _rowPtrs = new();        // the same rows by pointer, to tell a table row from a copy
        static readonly Dictionary<IntPtr, SensorRow> _vus = new();   // every sensors object the loaded units reach
        static readonly List<float> _tri = new();
        static readonly HashSet<IntPtr> _ecritsMod = new();       // sensors objects whose OpticsGround the mod itself has journaled

        static void Log(string s) => Mod.Log.Msg("[METEO] " + s);

        // ============================================================ preferences and lifecycle
        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory(PrefCat);
            _choix = c.CreateEntry(PrefName, ChoixDefaut, description: Build.Desc(
                "Météo des missions de campagne : auto | mesure | off. La pluie et le ciel couvert sont ceux que le studio a écrits dans la mission (le mod ne change jamais l'image) ; « auto » leur donne un effet : sous la pluie les unités voient le sol à 75 % de leur distance normale, par temps couvert à 90 %, des deux côtés pareil. La portée contre les avions et les hélicoptères n'est jamais touchée (la base de données ne distingue pas l'optique du radar). Le réglage arrive sur « mesure » : le mod relève seulement, il ne change rien. Mettez « auto » quand vous voulez l'effet ; il s'appliquera après une bataille. Vérifiez alors les missions sous la pluie ou par temps couvert (Baltiisk, la base aérienne, Klaipeda) : si un objectif attend que l'ennemi soit repéré, une vue plus courte peut le retarder.",
                "Météo des missions de campagne (auto | mesure | off)."));
            _etape = c.CreateEntry("Etape", 0, description: Build.Desc(
                "Réglage automatique, ne pas modifier. 0 = mesure en cours, 1 = l'effet de la météo est appliqué."));
            _versionJeu = c.CreateEntry("VersionJeu", "", description: Build.Desc("Réglage automatique, ne pas modifier"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; _etape.Value = 0; }
            // a game update can change the sensors table: what was proved about it is forgotten and measured again
            string game = "?";
            try { game = UnityEngine.Application.version; } catch { }
            if (_versionJeu.Value != game)
            {
                if (!string.IsNullOrEmpty(_versionJeu.Value) && _etape.Value != 0)
                    Log("le jeu a changé de version : la météo repasse en mesure, l'effet reviendra après une bataille");
                _versionJeu.Value = game;
                _etape.Value = 0;
            }
        }

        /// -1 = the mod touches nothing; 0 = measure only; 1 = measure and apply once proved.
        static int Voulu()
        {
            string v = _choix?.Value;
            if (string.IsNullOrWhiteSpace(v)) return 0;               // an empty setting falls back on the default, which writes nothing
            v = v.Trim().ToLowerInvariant();
            if (v == "off" || v == "non" || v == "false") return -1;
            if (v == "mesure" || v == "mesurer") return 0;
            return 1;
        }

        /// True when stage 1 is reached AND the player left the setting on "auto".
        static bool Applique => Voulu() == 1 && _etape != null && _etape.Value >= 1;

        internal static void ResetSession()
        {
            if (!_pose) EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false; _onlineDit = false;
        }

        /// The battle went to its end (Campaign.PollBattleEnd): this, and only this, opens the gate from stage 0 to stage 1.
        /// A battle the player abandoned, a crash or a quit proves nothing and is never counted.
        internal static void OnBattleEnd() { Portes(); EndBattle("fin de bataille"); }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        static void EndBattle(string why)
        {
            Rendre(why);
            _pose = false; _gcPtr = IntPtr.Zero; _battleSeen = 0f;
            _cle = null; _carte = null; _mission = null; _temps = WClair; _midDit = false;
            _rows.Clear(); _rowPtrs.Clear(); _vus.Clear(); _tri.Clear(); _ecritsMod.Clear();   // no stale native pointer is kept between battles
            _mLue = false; _mEcart = -1; _mEcartMod = 0;
            if (_etatCode != EtOff) Etat(EtMesure);
            if (!_sessionArmed) return;
            _sessionArmed = false;
            // the crash guard is cleared here, so the warning that goes with it must be allowed to speak again when it comes back
            if (_unclean.Value != 0) { _unclean.Value = 0; _pauseDite = false; Save(); }
            Log("fin de bataille (" + why + ")");
        }

        /// Takes this file's lot back out of the game. Never touches another lot's values.
        static void Rendre(string why)
        {
            if (!_ecrit) { _srcPtr = IntPtr.Zero; return; }
            _ecrit = false;
            IntPtr now = IntPtr.Zero;
            try { now = DataBaseService._instance?.RawAccess?.Pointer ?? IntPtr.Zero; } catch { }
            if (_srcPtr != IntPtr.Zero && now != _srcPtr)
            {
                // the database itself was replaced (the mod restores everything at each mission load): our values went with it
                _srcPtr = IntPtr.Zero; _nEcrits = 0; _nCopies = 0;
                return;
            }
            _srcPtr = IntPtr.Zero;
            try
            {
                int n = Realism.RestoreLot(LotMeteo, "météo : " + why);
                Log($"météo retirée ({why}) : {n} valeur(s) remise(s) à la valeur du jeu");
            }
            catch (Exception e) { Mod.Log.Warning("[METEO] valeurs non remises : " + e.GetBaseException().Message); }
            _nEcrits = 0; _nCopies = 0;
        }

        static void Save()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Mod.Log.Warning("[METEO] enregistrement des réglages impossible : " + e.GetBaseException().Message); }
        }

        // ============================================================ error counter and kill-switch
        internal static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3) Mod.Log.Warning("[METEO] erreur : " + e.GetBaseException().Message);
            if (_errors < MaxErrors || _dead) return;
            _dead = true;
            _armed = false;
            Mod.Log.Warning($"[METEO] {MaxErrors} erreurs : météo coupée jusqu'au redémarrage du jeu (le reste du mod fonctionne)");
            Rendre("trop d'erreurs");
            Etat(EtOff);
        }

        static void Etat(int code, int a = -1) { _etatCode = code; _etatA = a; _etatVer++; }

        // ============================================================ the lazy hook (own Harmony id, never removed)
        /// Every 2 s, menus included: the detour is installed the first time the feature is wanted, and nothing else.
        internal static void SlowTick()
        {
            if (_choix == null || _refused || _dead) { _armed = false; return; }
            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive) { _armed = false; return; }
            if (Voulu() < 0)
            {
                if (_pose || _sessionArmed || _ecrit) EndBattle("météo réglée sur « off »");
                if (_armed) { _armed = false; Log("réglée sur « off » : le mod ne touche plus à la météo"); }
                return;
            }
            if (Campaign.MissionInerte && !_patchOk) { _armed = false; return; }
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            if (!_patchOk) { _armed = false; return; }
            if (_unclean.Value >= MaxUnclean)
            {
                _armed = false;
                if (!_pauseDite)
                {
                    _pauseDite = true;
                    Mod.Log.Warning($"[METEO] {_unclean.Value} partie(s) non terminée(s) normalement après une météo appliquée : météo en pause par sécurité");
                    Etat(EtOff);
                }
                return;
            }
            if (!_armed)
            {
                _armed = true;
                Log(Applique
                    ? "météo demandée : effet appliqué au chargement de la prochaine mission"
                    : Voulu() == 0
                        ? "météo réglée sur « mesure » : le mod relève seulement et ne change rien ; mettez la ligne « Météo » de l'onglet du mod sur « auto » pour que la pluie et le temps couvert raccourcissent la vue au sol"
                        : "météo demandée : mesure seule pour cette bataille, l'effet s'activera ensuite tout seul");
            }
        }

        static void TryPatch()
        {
            try
            {
                var m = AccessTools.Method(typeof(EnvService), "ApplyDayTimePreset");
                if (m == null)
                {
                    _refused = true;
                    Log("l'ambiance de la carte (EnvironmentService.ApplyDayTimePreset) est introuvable dans cette version du jeu : météo impossible");
                    Etat(EtFail);
                    return;
                }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Meteo");
                _harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(Meteo), nameof(ApplyPostfix))));
                _patchOk = true;
                Log("point(s) d'accroche installé(s) : météo (ambiance de la mission)");
            }
            catch (Exception e)
            {
                _refused = true;
                Log("météo impossible dans cette version du jeu : " + e.GetBaseException().Message);
                Etat(EtFail);
            }
        }

        /// Void ApplyDayTimePreset(String sceneName, String presetKey): two strings, nothing else. Fires for the game's own apply at
        /// scenario load AND for HeureDuJour's, so the LAST key in place is always the one read here.
        static void ApplyPostfix(EnvService __instance, string sceneName, string presetKey)
        {
            if (!_armed) return;
            if (Identite.Blocked || Mod.AntiCheatActive || Mod.Actif == null || !Mod.Actif.Value) return;
            try { AuChargement(__instance, sceneName, presetKey); }
            catch (Exception e) { Fail(e); }
        }

        // ============================================================ the only moment the weather is decided: the loading screen
        static void AuChargement(EnvService env, string scene, string cle)
        {
            if (env == null) return;

            // ApplyDayTimePreset is not only the loading screen's call: a mission script can change the ambience in the middle of a
            // battle through its dtpch node (NodeDayTimeChanger), which is why HeureDuJour.cs refuses any mission carrying that node.
            // Everything below this line assumes the units of the battle are not spawned yet - a FogOfWarComponent takes its three
            // distances in its constructor - so in a live battle this module does nothing at all rather than half of something.
            if (_battleSeen > 0f)
            {
                if (!_midDit)
                {
                    _midDit = true;
                    Log("l'ambiance de la mission a changé pendant la bataille : la météo n'y touche pas, les unités déjà en jeu gardent la vue qu'elles avaient");
                }
                return;
            }

            // the campaign state is asked for directly, exactly as HeureDuJour does: a mission played without the mod stops us here,
            // and the mission id must be the one now loading, never the previous one
            try { Campaign.Refresh(); }
            catch (Exception e) { Fail(e); return; }
            if (!Campaign.InCampaign || Campaign.MissionInerte) return;      // hangar, arsenal, skirmish, US_M01: not a word
            // Solo() sets _netLogged itself on its first successful call, so the refusal needs a flag of its own to be said even once
            if (!Solo()) { if (!_onlineDit) { _onlineDit = true; Log("partie en ligne : la météo n'est pas touchée"); } return; }
            if (_unclean.Value >= MaxUnclean) { _armed = false; Etat(EtOff); return; }

            // the key the game really ended on, not the one we were handed: HeureDuJour may have applied another one a moment ago
            string reelle = null;
            try { reelle = env.CurrentDayTimePreset?.Key; } catch { }
            if (string.IsNullOrEmpty(reelle)) reelle = cle;
            if (string.IsNullOrEmpty(reelle)) { Log("ambiance de la mission illisible : la météo n'est pas touchée"); Etat(EtFail); return; }

            if (string.IsNullOrEmpty(scene)) { try { scene = SceneManager.GetActiveScene().name; } catch { } }

            string uid = Campaign.MissionUid;

            // a loading screen that never reached its battle (load abandoned), then another mission: the first one is closed properly
            // here, so an abandoned load can never leave values in the game nor be counted by the crash guard
            if (_pose && !string.Equals(uid, _mission, StringComparison.Ordinal)) EndBattle("chargement abandonné");

            // a second apply on the same loading screen (the game's, then HeureDuJour's): what was written for the first key is taken
            // back before the second is looked at, so two keys can never be layered on top of each other
            if (_ecrit && string.Equals(reelle, _cle, StringComparison.Ordinal)) return;         // same key again: nothing to redo
            if (_ecrit) Rendre("l'ambiance a changé pendant le chargement");

            _pose = true;
            _cle = reelle;
            _carte = scene;
            _mission = uid;
            _temps = Lire(reelle);

            if (!Mesure()) { Etat(EtFail); return; }                          // the line is written inside Mesure()

            if (_temps == WClair)
            {
                Log($"mission {_mission ?? "?"}, carte « {scene ?? "?"} » : ambiance « {reelle} » = temps clair, la météo ne change rien");
                Etat(EtClair);
                return;
            }

            if (!Applique)
            {
                Log($"mission {_mission ?? "?"} : {NomsFr[_temps]} relevée, mais la météo est encore en mesure : rien n'est changé pour cette bataille" +
                    (Voulu() == 0 ? " (réglage « mesure »)" : " (l'effet s'activera tout seul après cette bataille)"));
                Etat(EtAttente, _temps);
                return;
            }

            Appliquer();
        }

        /// Reads a preset key as a weather, by its own text and never by a hand-written list of keys: the studio writes "Day_Overcast"
        /// on one map and "Day overcast" on the next, and a mod map may write anything at all.
        static int Lire(string cle)
        {
            if (string.IsNullOrEmpty(cle)) return WClair;
            string s = cle.ToLowerInvariant();
            if (s.Contains("rain") || s.Contains("pluie") || s.Contains("storm") || s.Contains("thunder")) return WPluie;
            if (s.Contains("overcast") || s.Contains("fog") || s.Contains("mist") || s.Contains("couvert") || s.Contains("brume")) return WCouvert;
            return WClair;
        }

        // ============================================================ stage 0: the measurement, written before anything is changed
        /// Reads the game's own values and the state of the unit copies. Writes one line. Changes nothing. False = unreadable.
        static bool Mesure()
        {
            _mLue = false; _mEcart = -1; _mEcartMod = 0;
            var src = Source();
            if (src == null) { Log("base de données illisible : la météo n'est pas touchée"); return false; }

            if (!Table(src)) { Log("table des capteurs illisible : la météo n'est pas touchée"); return false; }

            var pg = Props.Get(typeof(SensorRow), "OpticsGround");
            if (pg == null) { Log("la portée de vue au sol (Sensors.OpticsGround) est absente de cette version du jeu : météo impossible"); _refused = true; return false; }

            // the game's own ground sight, read and nothing else
            _tri.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                float v = 0f;
                try { v = _rows[i].OpticsGround; } catch { continue; }
                if (v > 0f && !float.IsNaN(v) && !float.IsInfinity(v)) _tri.Add(v);
            }
            _mRows = _rows.Count;
            if (_tri.Count == 0) { Log("aucune portée de vue au sol lisible : la météo n'est pas touchée"); return false; }
            _tri.Sort();
            _mMin = _tri[0];
            _mMax = _tri[_tri.Count - 1];
            _mMed = _tri[_tri.Count / 2];

            _mPlancher = PlancherSol;
            try
            {
                float f = GameCfg.Instance.FogOfWarConfig.MinimumDetectionDistance;
                if (f > _mPlancher && f > 0f && !float.IsNaN(f)) _mPlancher = f;
            }
            catch { }

            // the gate: every Sensors object the loaded units reach, and whether the copies still say what their table row says
            Copies(pg);

            var sb = new StringBuilder(420);
            sb.Append("relevé : carte « ").Append(_carte ?? "?").Append(" », mission ").Append(_mission ?? "?")
              .Append(", ambiance « ").Append(_cle ?? "?").Append(" » lue comme ").Append(NomsFr[_temps])
              .Append(" ; vue au sol du jeu : ").Append(_mRows).Append(" capteur(s), la plus courte ").Append(_mMin.ToString("0", Inv))
              .Append(" m, au milieu ").Append(_mMed.ToString("0", Inv)).Append(" m, la plus longue ").Append(_mMax.ToString("0", Inv))
              .Append(" m, plancher de détection ").Append(_mPlancher.ToString("0", Inv)).Append(" m")
              .Append(" ; copies des unités : ").Append(_mReach).Append(" objet(s) capteur atteints (").Append(_mTable)
              .Append(" ligne(s) de table, ").Append(_mCopie).Append(" copie(s)) ; parmi les copies, ")
              .Append(_mEcartMod.ToString(Inv)).Append(" ont une optique propre écrite par le mod lui-même (normal) et ")
              .Append(_mEcart < 0 ? "un nombre non vérifié" : _mEcart.ToString(Inv))
              .Append(" ne disent pas la même chose que leur ligne sans raison connue");
            sb.Append(" ; portées contre les avions et les hélicoptères : jamais touchées (la base ne distingue pas l'optique du radar)");
            Log(sb.ToString());
            _mLue = true;
            return true;
        }

        static DbSource Source()
        {
            try { return DataBaseService._instance?.RawAccess; } catch { return null; }
        }

        /// Fills _rows with the sensors table. False = unreadable.
        static bool Table(DbSource src)
        {
            _rows.Clear();
            try
            {
                var all = Props.Rows(src.Sensors.GetAll());
                for (int i = 0; i < all.Count; i++) if (all[i] != null) _rows.Add(all[i]);
            }
            catch (Exception e) { Mod.Log.Warning("[METEO] table des capteurs illisible : " + e.GetBaseException().Message); return false; }
            return _rows.Count > 0;
        }

        /// Walks every Sensors object the loaded units reach (the unit's own list, its current options, and the options of its
        /// modifications, which is the same walk CopiesUnites.cs does) and fills _vus.
        /// A copy that disagrees with its table row is counted in one of two places, and telling them apart is the whole point:
        /// this mod writes optics PER UNIT onto exactly those copies (Affuts.cs and reel/CapteursParUnite.csv - a BTR-82's clone
        /// holds 3000 m where its row holds 2000 m), so a copy the journal already knows about is the mod's own work and proves
        /// nothing about the game. Only a copy nobody here wrote would say the data model is not what this file believes.
        static void Copies(PropertyInfo pg)
        {
            _vus.Clear();
            _mReach = 0; _mTable = 0; _mCopie = 0; _mEcart = -1; _mEcartMod = 0;

            // every sensors object whose ground sight the mod has already written, by pointer: the journal is the only honest source
            // for that, and a lot key is not enough (a write made outside a lot scope is journaled with a null lot).
            _ecritsMod.Clear();
            try
            {
                var snap = Realism.JournalSnapshot();
                for (int i = 0; i < snap.Count; i++)
                {
                    var je = snap[i];
                    if (je.p == null || pg == null || !string.Equals(je.p.Name, pg.Name, StringComparison.Ordinal)) continue;
                    if (je.row is Il2CppObjectBase ob) _ecritsMod.Add(ob.Pointer);
                }
            }
            catch (Exception e) { Mod.Log.Warning("[METEO] journal du mod illisible : " + e.GetBaseException().Message); }

            _rowPtrs.Clear();
            for (int i = 0; i < _rows.Count; i++) { var o = _rows[i] as Il2CppObjectBase; if (o != null) _rowPtrs.Add(o.Pointer); }
            var table = _rowPtrs;

            // table row id -> its ground sight, to compare a copy against its own row
            var parId = new Dictionary<int, float>(_rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                try { parId[_rows[i].Id] = _rows[i].OpticsGround; } catch { }
            }

            List<UnitsRow> units;
            try
            {
                units = new List<UnitsRow>();
                var loader = DataBaseService._instance?.UnitsLoader;
                var d = loader?._loadedUnits;
                if (d != null) foreach (var kv in d) { var u = kv.Value; if (u != null) units.Add(u); }
            }
            catch (Exception e) { Mod.Log.Warning("[METEO] unités chargées illisibles : " + e.GetBaseException().Message); return; }

            int ecart = 0;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                try
                {
                    Ajoute(u.Sensors, table, parId, ref ecart);
                    Options(u.CurrentOptions, table, parId, ref ecart);
                    var mods = u.Modifications;
                    if (mods != null) for (int k = 0; k < mods.Count; k++) { var m = mods[k]; if (m != null) Options(m.Options, table, parId, ref ecart); }
                }
                catch { }                                              // one unreadable unit never stops the walk
            }
            _mReach = _vus.Count;
            _mEcart = ecart;
        }

        static void Options(Il2CppSystem.Collections.Generic.List<OptionRow> list, HashSet<IntPtr> table, Dictionary<int, float> parId, ref int ecart)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o == null) continue;
                try { Un(o.MainSensor, table, parId, ref ecart); } catch { }
                try { Un(o.ExtraSensor, table, parId, ref ecart); } catch { }
            }
        }

        static void Ajoute(Il2CppSystem.Collections.Generic.List<SensorRow> list, HashSet<IntPtr> table, Dictionary<int, float> parId, ref int ecart)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) Un(list[i], table, parId, ref ecart);
        }

        static void Un(SensorRow s, HashSet<IntPtr> table, Dictionary<int, float> parId, ref int ecart)
        {
            if (s == null) return;
            var o = s as Il2CppObjectBase;
            if (o == null || _vus.ContainsKey(o.Pointer)) return;
            _vus[o.Pointer] = s;
            if (table.Contains(o.Pointer)) { _mTable++; return; }
            _mCopie++;
            try
            {
                if (parId.TryGetValue(s.Id, out float want) && Math.Abs(want - s.OpticsGround) > 0.5f)
                {
                    if (_ecritsMod.Contains(o.Pointer)) _mEcartMod++;      // the mod's own per-unit optic: expected, not a finding
                    else ecart++;
                }
            }
            catch { }
        }

        // ============================================================ stage 1: the values, written under this file's own lot
        static void Appliquer()
        {
            var src = Source();
            var pg = Props.Get(typeof(SensorRow), "OpticsGround");
            if (src == null || pg == null || _temps == WClair) return;
            float f = FacteurSol[_temps];
            if (!(f > 0f) || f >= 1f) return;

            // _ecrit and _srcPtr are set BEFORE the first write, never after the loop: Rendre() does nothing at all while _ecrit is
            // false, so a loop broken in the middle would otherwise leave half the sensors reduced under a lot the module believes it
            // never opened, and the end of the battle would not take them back. The cost of being early is one empty restore.
            _ecrit = true;
            _srcPtr = IntPtr.Zero;
            try { _srcPtr = src.Pointer; } catch { }

            int n = 0, nc = 0, plancher = 0;
            double avant = 0, apres = 0;
            try
            {
                using (Realism.Lot(LotMeteo))
                {
                    // the table rows first: a unit copy the game makes after this point takes the row's current value on its own
                    // (CopiesUnites.cs reads the row live), so the units spawned for this battle follow without anything else
                    for (int i = 0; i < _rows.Count; i++)
                        if (Ecris(_rows[i], pg, f, ref avant, ref apres, ref plancher)) n++;

                    // then the copies that already existed before this loading screen, which no later sync would reach. The same
                    // accumulators as the table rows: the average and the floor count in the log must cover what was really written.
                    foreach (var kv in _vus)
                    {
                        if (_rowPtrs.Contains(kv.Key)) continue;
                        if (Ecris(kv.Value, pg, f, ref avant, ref apres, ref plancher)) nc++;
                    }
                }
            }
            catch (Exception e) { Fail(e); Rendre("écriture interrompue"); return; }

            if (n == 0)
            {
                Rendre("aucune portée de vue écrite");
                Log("aucune portée de vue n'a pu être écrite : la météo ne change rien pour cette bataille");
                Etat(EtFail);
                return;
            }

            _nEcrits = n; _nCopies = nc;
            int tot = Math.Max(1, n + nc);
            _avantMoyen = (float)(avant / tot);
            _apresMoyen = (float)(apres / tot);
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;                        // crash guard: cleared at the end of the battle
            Save();
            Etat(EtApplique, _temps);

            // the cause, said plainly: a player who sees his tanks stop at 1500 m must read why, or he will think the mod broke his optics
            Log($"mission {_mission ?? "?"}, carte « {_carte ?? "?"} » : {NomsFr[_temps]} (ambiance « {_cle} ») — vue au sol des DEUX camps à {(f * 100f).ToString("0", Inv)} % " +
                $": en moyenne {_avantMoyen.ToString("0", Inv)} m -> {_apresMoyen.ToString("0", Inv)} m ; " +
                $"{n} capteur(s) de la table et {nc} copie(s) déjà chargée(s) écrits" +
                (plancher > 0 ? $", {plancher} tenu(s) au plancher de {_mPlancher.ToString("0", Inv)} m" : "") +
                // said plainly rather than hidden: these units get their own absolute optic rewritten by the mod after this write
                (_mEcartMod > 0
                    ? $" ; attention, {_mEcartMod} capteur(s) d'unités qui ont leur propre optique dans le mod (hélicoptères d'attaque, véhicules de reconnaissance) sont réécrits après la météo et gardent leur portée de temps clair"
                    : "") +
                " ; la portée contre les avions et les hélicoptères n'est pas touchée ; tout est remis à la fin de la bataille");
        }

        /// One journaled write, never upward, never below the floor. True = written.
        static bool Ecris(SensorRow s, PropertyInfo pg, float f, ref double avant, ref double apres, ref int plancher)
        {
            if (s == null) return false;
            float v;
            try { v = s.OpticsGround; } catch { return false; }
            if (!(v > 0f) || float.IsNaN(v) || float.IsInfinity(v)) return false;
            float w = v * f;
            if (w < _mPlancher) { w = Math.Min(v, _mPlancher); plancher++; }
            if (w >= v) return false;                                    // never raised
            w = (float)Math.Round(w);
            if (w >= v) return false;
            try { Realism.SetValueForOverride(s, pg, w); }
            catch { return false; }
            avant += v; apres += w;
            return true;
        }

        // ============================================================ the gate from stage 0 to stage 1
        /// The house rule of the other modules: the effect arms itself after ONE battle load where the measurement proved the data
        /// path, and never before. What must be true:
        ///  - the setting is on "auto" (never in "mesure");
        ///  - the sensors table was read whole, with sane ground sights;
        ///  - the loaded units reach their sensors (that, and nothing else, is the data path this gate is about);
        ///  - no error of this file so far.
        /// WHAT IS DELIBERATELY NOT ASKED, and why: this gate used to demand that not one loaded copy disagree with its table row.
        /// That condition can never hold on this build and would have closed the gate for ever, without a word: the mod's own real
        /// stats write optics per unit onto exactly those copies (the log says it out loud - "U1 BTR-82 capteur 23 ... OpticsGround
        /// 2000 -> 3000"), so the count is in the hundreds at every mission load. It was also asking for the wrong thing: Appliquer()
        /// writes EVERY copy reached in _vus from that copy's own value, so a copy that disagrees is covered exactly like one that
        /// agrees. The count stays as a measurement, split between the copies the mod wrote itself and the rest.
        /// Nothing about balance is proved here, and the log says so: only that writing the two places reaches every unit.
        /// Every refusal below is logged. A gate that will never open must be readable in the log, never silent.
        static void Portes()
        {
            if (_etape == null || _dead || _etape.Value >= 1) return;
            if (Voulu() < 0) return;                                   // "off": the player said no, there is nothing to explain
            if (Voulu() != 1)
            {
                Refus("le réglage est sur « mesure » ; mettez-le sur « auto » dans l'onglet du mod pour que l'effet s'active après une bataille");
                return;
            }
            if (!_mLue) { Refus("aucun relevé n'a pu être fait pendant cette bataille"); return; }
            if (_mRows <= 0) { Refus("la table des capteurs n'a pas été lue"); return; }
            if (_mReach <= 0) { Refus("les unités chargées n'ont atteint aucun objet capteur : le chemin des données n'est pas prouvé"); return; }
            if (_errors != 0) { Refus(_errors + " erreur(s) de la météo pendant cette bataille"); return; }

            _etape.Value = 1;
            Save();
            Log($"changement d'étape de la météo : preuve faite dans cette bataille ({_mRows} capteur(s) lus, {_mReach} objet(s) capteur atteints " +
                $"par les unités chargées, dont {_mCopie} copie(s) écrites une par une, aucune erreur) ; à partir du prochain chargement, " +
                "une mission sous la pluie réduit la vue au sol des deux camps à 75 %, une mission par temps couvert à 90 % ; " +
                "la portée contre les avions et les hélicoptères ne sera jamais touchée");
        }

        /// Why the weather stayed in measurement at the end of this battle. Bounded per game session so a refusal that repeats every
        /// battle is visible without filling the log.
        static void Refus(string why)
        {
            if (_porteLogs >= 8) return;
            _porteLogs++;
            Log("la météo reste en mesure : " + why);
        }

        // ============================================================ frame watchdog (a timer read and nothing else)
        internal static void Frame()
        {
            if (_choix == null || Voulu() < 0) return;
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _battleSeen > 0f ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + ScanChaque;

            GameController gc = null;
            bool live = false;
            try { gc = GameController._instance; live = gc != null && gc._GameSession_k__BackingField?.CurrentPlayer != null; }
            catch { live = false; }
            if (!live)
            {
                if (_battleSeen > 0f) EndBattle("plus de partie");
                return;
            }

            IntPtr p = gc.Pointer;
            if (_gcPtr == IntPtr.Zero) _gcPtr = p;
            else if (_gcPtr != p) { _gcPtr = p; _battleSeen = 0f; }
            if (_battleSeen <= 0f) { _battleSeen = now; _pose = false; }

            // the database is reloaded at every mission load: if it went, our journal went with it and the state must not pretend otherwise
            if (_ecrit && _srcPtr != IntPtr.Zero)
            {
                IntPtr cur = IntPtr.Zero;
                try { cur = DataBaseService._instance?.RawAccess?.Pointer ?? IntPtr.Zero; } catch { }
                if (cur != IntPtr.Zero && cur != _srcPtr)
                {
                    Log("la base de données du jeu a été remplacée : la météo de cette bataille est retombée d'elle-même");
                    _ecrit = false; _srcPtr = IntPtr.Zero; _nEcrits = 0; _nCopies = 0;
                    Etat(EtMesure);
                }
            }
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

        // ============================================================ what the Mod tab row needs (the row itself belongs to ModTab.cs)
        /// Changes whenever the row's value or the mod's answer changes: the tab rebuilds its description then, and never per frame.
        internal static int EtatVersion => _etatVer * 8 + (Voulu() + 1);

        /// The value the row shows, as one of Valeurs.
        internal static string Valeur
        {
            get
            {
                int v = Voulu();
                return v < 0 ? "off" : v == 0 ? "mesure" : "auto";
            }
        }

        internal static void Set(string v)
        {
            if (_choix == null || string.IsNullOrWhiteSpace(v)) return;
            _choix.Value = v.Trim().ToLowerInvariant();
            Save();
        }

        /// The weather of the battle now running, for the row and for any other module that wants it: -1 = none read yet.
        internal static int TempsCourant => _pose || _battleSeen > 0f ? _temps : -1;

        /// The ground-sight factor in force right now (1 = nothing is changed).
        internal static float FacteurCourant => _ecrit && _temps >= 0 && _temps < WCount ? FacteurSol[_temps] : 1f;

        /// One sentence for the LOG and as a fallback, in French only. IT IS NOT THE TEXT OF THE MOD TAB ROW: the row must read its
        /// five-language TxtKey.ME_* strings, which do not exist in Txt.cs yet. Wiring the row on this method would show French to
        /// English, German, Spanish and Russian players, so the row waits for the keys.
        internal static string EtatFr()
        {
            switch (_etatCode)
            {
                case EtOff: return "Coupée par sécurité pour cette partie.";
                case EtFail: return "Impossible à lire dans cette version du jeu : rien n'est changé.";
                case EtClair: return "Cette mission est par temps clair : la météo ne change rien.";
                case EtAttente:
                    return (_etatA >= 0 && _etatA < WCount ? "Mission sous " + NomsFr[_etatA] : "Météo relevée")
                         + (Voulu() == 0
                            ? " : réglage « mesure », le mod relève seulement et ne change rien. Mettez « auto » pour activer l'effet."
                            : " : mesure en cours, l'effet s'activera au prochain chargement.");
                case EtApplique:
                    return (_etatA >= 0 && _etatA < WCount ? "Mission sous " + NomsFr[_etatA] : "Météo appliquée")
                         + $" : vue au sol des deux camps à {(FacteurCourant * 100f).ToString("0", Inv)} %"
                         + $" ({_avantMoyen.ToString("0", Inv)} m -> {_apresMoyen.ToString("0", Inv)} m en moyenne,"
                         + $" {_nEcrits + _nCopies} capteur(s))."
                         + (_mEcartMod > 0
                            ? " Les unités qui ont leur propre optique dans le mod (hélicoptères d'attaque, véhicules de reconnaissance) gardent leur portée de temps clair."
                            : "")
                         + " La portée contre les avions et les hélicoptères n'est pas touchée.";
                default:
                    return Voulu() == 0
                        ? "Réglage « mesure » : le mod relève seulement, il ne change rien. Mettez « auto » pour activer l'effet."
                        : "Mesure en cours : la météo sera appliquée après une bataille.";
            }
        }
    }
}
