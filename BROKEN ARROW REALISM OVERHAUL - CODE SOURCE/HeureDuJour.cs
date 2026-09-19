// RealismOverhaul - time of day of a campaign mission (morning, day, evening, night). The picture only: the engine's spotting has no
//  light input at all (FogOfWarComponent / FilterTask / FogOfWarConfig know distance, terrain, smoke, forest, stealth and weapon flash,
//  never the hour), and SensorType.InfraRed / NightVision are dead names nothing stores, so night changes nothing to spotting, accuracy,
//  the AI or the difficulty. Nothing here is promised beyond the look.
//  How: the game already owns one preset set per scene (EnvironmentService.DayTimePresets) and one public call to apply one of them,
//  ApplyDayTimePreset(sceneName, presetKey) - the very call the game makes for itself at every scenario load and the call a mission script
//  makes through its dtpch node. The mod asks the game to do what it already knows how to do; sky, exposure, fog, forest / water / wind
//  ambiences and the muzzle, impact and explosion lights follow on their own (LightLimitedPool listens to DayTimePresetChangedEvent).
//  - One lazy postfix on EnvironmentService.Init, with its own Harmony id, installed only once the player has actually chosen an hour,
//    never removed (volatile _armed flag instead). Applied ONCE, during the loading screen, never in battle: a second apply in battle
//    would load an asset again and fight the mission's own intent.
//  - Preset keys are ENUMERATED from the map itself (ActiveSceneDayTimePresets.Data) and never written by hand: the studio's spelling is
//    inconsistent ("Day (Default)", "Day_Rainy (default)", "Evening  (Default) (Default)", "Night"). A key is sorted by its own text:
//    "night" -> night, "morning" -> morning, "evening" -> evening, otherwise "day" -> day; anything named Halloween is left out. Inside a
//    basket the IsDefault key wins, otherwise the first in ordinal order (the same choice always gives the same ambience). When the map
//    has no key for the chosen hour NOTHING is touched: the mission keeps the hour the studio wrote for it, and the row says so. An hour
//    the player did not ask for is never substituted for the one he did.
//  - Never fights a mission script. A mission whose saved node list holds a dtpch node (NodeDayTimeChanger) is never touched, and a battle
//    where the current preset changes without us makes the mod step aside for good on that mission (a change seen in the first seconds of
//    the battle is the end of the loading, not a script: the mod steps aside for that battle only, and never brands the mission). The node
//    list is read once per mission, in small budgeted chunks, from the battle the player is in: so the FIRST battle of a mission only reads
//    it, and the choice applies from the next launch of that mission on ("in doubt, do nothing"). The answer is kept per mission id in the
//    preferences and cleared when the game version changes.
//  - Fully inert: mod off, choice on "comme la mission", outside a solo campaign battle, online, or Campaign.MissionInerte (US_M01).
//    Error counter with kill-switch, crash guard (SessionsInterrompues), nothing per frame but a timer read, one apply per battle.
//  MEASUREMENT [HEURE] (stage 0 of the design): at each mission load with an hour chosen, one line gives the scene, the mission's own key,
//   every key the map owns with IsDefault / DayTimeVfxParameter / light-flash config, the basket each one was put in, and the player's own
//   graphics settings read (never written): light sources, volumetric fog, VFX dynamic shadows, lens flares.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using EnvService = Il2CppBrokenArrow.BrokenArrow.Client.Ecs.MapEnvironment.EnvironmentService;
using PresetSet = Il2CppBrokenArrow.BrokenArrow.Client.Ecs.MapEnvironment.SceneDayTimePreset;
using PresetData = Il2CppBrokenArrow.BrokenArrow.Client.Ecs.MapEnvironment.SceneDayTimePresetData;
using NodeSaveData = Il2CppBrokenArrow.ScriptEngine.Data.NodeSaveData;
using SettingsService = Il2CppBrokenArrow.Client.Ecs.GameSettings.Services.SettingsService;
using SettingType = Il2CppBrokenArrow.Client.Ecs.GameSettings.Enums.SettingType;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace RealismOverhaul
{
    static class HeureDuJour
    {
        const string GuardVersion = "1.1";
        const int MaxErrors = 20, MaxUnclean = 3, MaxPresets = 64;
        const int ScanChunk = 300;                                   // saved nodes read per pass, same cap as the script dump of Missions.cs
        const double ScanBudgetMs = 0.25;                            // and the same budget: a pass stops after a quarter of a millisecond
        const float ScanGiveUp = 120f;                               // seconds without a readable node list before giving up on this battle
        const float WatchGrace = 5f;                                 // a light change before this is the end of the loading, not a script
        const string NodeDayTime = "dtpch";                          // NodeDayTimeChanger, the only node that can change the preset

        // hours, in their own order: the baskets keys are sorted into
        internal const int HMorning = 0, HDay = 1, HEvening = 2, HNight = 3, HCount = 4;
        static readonly string[] NomsFr = { "matin", "jour", "soir", "nuit" };

        /// Preference values, in the order the row cycles through them (index 0 = the mod touches nothing).
        internal static readonly (string V, TxtKey K)[] Valeurs =
        {
            ("mission", TxtKey.HD_V_MISSION), ("matin", TxtKey.HD_V_MORNING), ("jour", TxtKey.HD_V_DAY),
            ("soir", TxtKey.HD_V_EVENING), ("nuit", TxtKey.HD_V_NIGHT),
        };

        internal const string PrefCat = "RealismOverhaul_Heure";
        internal const string PrefName = "HeureDeLaMission";

        static MelonPreferences_Entry<string> _choix, _lues, _pilotees, _versionJeu, _guardVersion;
        static MelonPreferences_Entry<int> _unclean;

        // ------------------------------------------------------------ state
        static HarmonyLib.Harmony _harmony;
        static volatile bool _armed;                                 // disarm flag: the postfix reads it first and returns at once
        static bool _patchTried, _patchOk, _refused, _dead, _sessionArmed, _pauseDite;
        static int _errors, _wait;

        static EnvService _env;                                      // held: a ScriptableObject that lives on between battles
        static string _cleAppliquee, _missionChargee;
        static bool _surveille, _everOnline, _netLogged;
        /// Set by Fini during the loading screen and cleared on the first live frame of the battle: the new GameSessionContext of that very
        /// battle appears AFTER the postfix has run, so Campaign.ResetSession must not throw away what was just installed for it.
        static bool _pose;
        static IntPtr _gcPtr;
        static float _next, _nextWatch, _battleSeen;
        static TxtMsg _notice;

        // node scan of the current battle
        static Il2CppReferenceArray<NodeSaveData> _nodes;            // held while the scan runs
        static int _scanIndex, _scanDtpch, _scanStage;               // stage 0 = waiting for the list, 1 = walking it, 2 = done, 3 = given up
        static readonly System.Diagnostics.Stopwatch _scanSw = new();

        // what the Mod tab shows
        const int EtNext = 0, EtLearn = 1, EtScript = 2, EtDone = 3, EtNoHour = 4, EtFail = 5, EtOff = 6;
        static int _etatCode = EtNext, _etatA = -1, _etatVer;

        static readonly List<string> _cles = new();                  // keys of the current map (read from the game, never rebuilt)
        static readonly List<string>[] _paniers = { new(), new(), new(), new() };

        static void Log(string s) => Mod.Log.Msg("[HEURE] " + s);

        // ============================================================ preferences and lifecycle
        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory(PrefCat);
            _choix = c.CreateEntry(PrefName, "mission", description: Build.Desc(
                "Heure des missions de campagne : mission | matin | jour | soir | nuit. « mission » = le mod ne touche à rien. C'est l'image seulement : la nuit ne change ni le repérage, ni la précision, ni la difficulté.",
                "Heure des missions de campagne (mission | matin | jour | soir | nuit)."));
            _lues = c.CreateEntry("MissionsLues", "", description: Build.Desc("Réglage automatique, ne pas modifier"));
            _pilotees = c.CreateEntry("MissionsPilotees", "", description: Build.Desc("Réglage automatique, ne pas modifier"));
            _versionJeu = c.CreateEntry("VersionJeu", "", description: Build.Desc("Réglage automatique, ne pas modifier"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            // a game update can rewrite the mission scripts: what was read about them is forgotten
            string game = "?"; try { game = UnityEngine.Application.version; } catch { }
            if (_versionJeu.Value != game)
            {
                _versionJeu.Value = game;
                if (_lues.Value.Length > 0 || _pilotees.Value.Length > 0) { _lues.Value = ""; _pilotees.Value = ""; Log("nouvelle version du jeu : ce qui avait été lu des missions est oublié"); }
            }
        }

        /// New mission (Campaign.ResetSession): everything of the previous battle goes, the per-mission knowledge stays.
        /// The battle's own GameSessionContext appears a few frames AFTER our postfix has set the hour up for it, and Campaign.NoteSession
        /// calls this then: while the loading latch is up, what was installed for the battle now starting is kept.
        internal static void ResetSession()
        {
            if (!_pose) EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false;
        }

        /// Battle-end screen (Campaign.PollBattleEnd), like the other modules: the battle went to its end, the crash guard is cleared.
        internal static void OnBattleEnd() => EndBattle("fin de bataille");

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        static void EndBattle(string why)
        {
            _surveille = false; _pose = false;
            _env = null; _set = null; _cleAppliquee = null; _missionChargee = null;
            _cles.Clear();
            for (int i = 0; i < HCount; i++) _paniers[i].Clear();
            _nodes = null; _scanStage = 0; _scanIndex = 0; _scanDtpch = 0;
            _gcPtr = IntPtr.Zero; _battleSeen = 0f; _notice = default;
            if (_etatCode != EtOff) Etat(EtNext);                     // the row goes back to "will be applied at the next mission load"
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean.Value != 0) { _unclean.Value = 0; Save(); }
            Log("fin de bataille (" + why + ")");
        }

        static void Save()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Mod.Log.Warning("[HEURE] enregistrement des réglages impossible : " + e.GetBaseException().Message); }
        }

        // ============================================================ error counter and kill-switch
        internal static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3) Mod.Log.Warning("[HEURE] erreur : " + e.GetBaseException().Message);
            if (_errors < MaxErrors || _dead) return;
            _dead = true;
            _armed = false;
            _surveille = false;
            Mod.Log.Warning($"[HEURE] {MaxErrors} erreurs : heure de la mission coupée jusqu'au redémarrage du jeu (le reste du mod fonctionne)");
            Etat(EtOff);
            _notice = TxtKey.N_HEURE_OFF;
        }

        static void Etat(int code, int a = -1)
        {
            _etatCode = code; _etatA = a; _etatVer++;
        }

        // ============================================================ the lazy hook (own Harmony id, never removed)
        /// Every 2 s, in the menus as well: the hook is installed the first time the player asks for an hour, and nothing else.
        internal static void SlowTick()
        {
            if (_choix == null || _refused || _dead) { _armed = false; return; }
            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive) { _armed = false; return; }
            int want = Voulu();
            if (want < 0)
            {
                // the player takes the hour back: the battle armed for him is closed here, so the crash guard never counts it
                if (_pose || _sessionArmed) EndBattle("réglé sur « comme la mission »");
                if (_armed) { _armed = false; Log("réglé sur « comme la mission » : le mod ne touche plus à l'heure"); }
                return;
            }
            // fully inert on a mission played without the mod (US_M01): not even a detour is posed there. It goes on at the first
            // normal mission, and nothing is lost: the choice only applies from the next mission load on anyway.
            if (Campaign.MissionInerte && !_patchOk) { _armed = false; return; }
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            if (!_patchOk) { _armed = false; return; }
            if (_unclean.Value >= MaxUnclean)
            {
                // said once per game start, armed or not: after a restart the row must not keep promising an hour that will never come
                _armed = false;
                if (!_pauseDite)
                {
                    _pauseDite = true;
                    Mod.Log.Warning($"[HEURE] {_unclean.Value} partie(s) non terminée(s) normalement après un changement d'heure : heure de la mission en pause par sécurité");
                    Etat(EtOff);
                    _notice = TxtKey.N_HEURE_OFF_SAFETY;
                }
                return;
            }
            if (!_armed) { _armed = true; Log($"heure demandée : {NomsFr[want]} (appliquée au chargement de la prochaine mission)"); }
        }

        static void TryPatch()
        {
            try
            {
                var m = AccessTools.Method(typeof(EnvService), "Init");
                if (m == null)
                {
                    _refused = true;
                    Log("l'heure du jour du jeu (EnvironmentService.Init) est introuvable dans cette version du jeu : heure de la mission impossible");
                    return;
                }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.HeureDuJour");
                _harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(HeureDuJour), nameof(InitPostfix))));
                _patchOk = true;
                Log("point(s) d'accroche installé(s) : heure du jour (chargement de mission)");
            }
            catch (Exception e)
            {
                _refused = true;
                Log("heure de la mission impossible dans cette version du jeu : " + e.GetBaseException().Message);
            }
        }

        static void InitPostfix(EnvService __instance, string dayTimePreset)
        {
            if (!_armed) return;                                     // volatile flag: nothing runs while the feature is off
            // the mod can be switched off from its tab between two 2 s ticks: read again here, the hook is never removed
            if (Identite.Blocked || Mod.AntiCheatActive || Mod.Actif == null || !Mod.Actif.Value) return;
            try { AuChargement(__instance, dayTimePreset); }
            catch (Exception e) { Fail(e); }
        }

        // ============================================================ the only moment the hour is changed: the loading screen
        static void AuChargement(EnvService env, string origine)
        {
            // a previous loading screen that never reached its battle (load aborted): that session is closed properly here, so an aborted
            // load can never be counted by the crash guard as a game that ended badly
            if (_pose) EndBattle("chargement abandonné");
            _surveille = false; _pose = false;
            _env = null; _cleAppliquee = null;
            _everOnline = false; _netLogged = false;
            int want = Voulu();
            if (env == null || want < 0) return;

            // the campaign state is asked for directly: a mission played without the mod (US_M01) stops us here, and the mission id must be
            // the one now loading, never the previous one. A failure here is counted and written, never swallowed: the hour is not touched
            // on a campaign state only half known.
            try { Campaign.Refresh(); }
            catch (Exception e) { Fail(e); return; }
            if (!Campaign.InCampaign || Campaign.MissionInerte) return;
            if (!Solo()) { Log("partie en ligne : l'heure de la mission n'est pas touchée"); return; }
            if (_unclean.Value >= MaxUnclean) { _armed = false; Etat(EtOff); return; }      // crash guard (the 2 s tick says it on screen)

            string scene = null;
            try { scene = SceneManager.GetActiveScene().name; } catch { }
            if (string.IsNullOrEmpty(scene)) { Log("carte de la mission illisible : l'heure n'est pas touchée"); Etat(EtFail); return; }

            // the map's own preset set, filled by the ApplyDayTimePreset that Init made just before us
            PresetSet set = null;
            try { set = env.ActiveSceneDayTimePresets; } catch { }
            if (!Lire(set)) { Log($"carte « {scene} » : aucune ambiance lisible, l'heure n'est pas touchée"); Etat(EtFail); return; }

            Ranger();
            Mesure(scene, origine);                                  // stage 0: read only, one line

            string uid = Campaign.MissionUid;
            if (string.IsNullOrEmpty(uid)) { Log("mission inconnue : l'heure n'est pas touchée"); Etat(EtFail); return; }
            _missionChargee = uid;
            if (Dans(_pilotees.Value, uid))
            {
                Log($"mission {uid} : son script règle la lumière lui-même, le mod n'y touche pas");
                Etat(EtScript);
                return;
            }
            if (!Dans(_lues.Value, uid))
            {
                // "in doubt, do nothing": the mission's node list is read during this battle, the hour follows from the next launch on
                Log($"mission {uid} : son script n'a pas encore été lu, l'heure sera changée à partir du prochain lancement de cette mission");
                Etat(EtLearn);
                _notice = TxtKey.N_HEURE_LEARN;
                return;
            }

            // the scene must be one the service knows, otherwise its own lookup would fall back on its default
            int known = -1;
            try { known = env.DayTimePresets != null && env.DayTimePresets.ContainsKey(scene) ? 1 : 0; } catch { known = -1; }
            if (known == 0) { Log($"carte « {scene} » inconnue du jeu : l'heure n'est pas touchée"); Etat(EtFail); return; }

            // the wanted hour and nothing else: a map without it keeps the hour the studio wrote for the mission
            string cle = Meilleure(want);
            if (cle == null) { Log($"carte « {scene} » : pas de {NomsFr[want]} sur cette carte, le mod garde l'heure d'origine de la mission"); Etat(EtNoHour, want); return; }

            string courante = null;
            try { courante = env.CurrentDayTimePreset?.Key; } catch { }
            if (string.Equals(cle, courante, StringComparison.Ordinal))
            {
                Log($"mission {uid} : la carte est déjà en {NomsFr[want]} (« {cle} »), rien à changer");
                Fini(want, cle, env);
                return;
            }

            try { env.ApplyDayTimePreset(scene, cle); }
            catch (Exception e) { Log($"ambiance « {cle} » refusée par le jeu : {e.GetBaseException().Message}"); Etat(EtFail); return; }

            // the game took it or it did not: read back before watching anything, so a refused key can never be blamed on a script
            string apres = null;
            try { apres = env.CurrentDayTimePreset?.Key; } catch { }
            if (!string.Equals(cle, apres, StringComparison.Ordinal))
            {
                Log($"mission {uid} : le jeu n'a pas pris l'ambiance « {cle} » (il est resté sur « {apres ?? "?"} ») : le mod n'insiste pas");
                Etat(EtFail);
                return;
            }
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;                     // crash guard: cleared at the end of the battle
            Save();
            Log($"mission {uid}, carte « {scene} » : {NomsFr[want]} appliqué (« {origine ?? "?"} » -> « {cle} »)");
            Fini(want, cle, env);

            // a mission written for the night keeps its night dialogue and cutscenes: said once, never refused
            if (want != HNight && Panier(origine) == HNight) _notice = TxtKey.N_HEURE_NIGHT;
            // and the night is the one hour that costs on the graphics card: the player's own settings say whether it will be expensive
            else if (want == HNight && NuitLourde()) _notice = TxtKey.N_HEURE_NIGHT_COST;
        }

        /// Arms the watchdog on the key really in place and tells the Mod tab what happened.
        /// _gcPtr is deliberately left empty: the battle's own GameController is adopted on the first live frame. Seeding it here, from
        /// inside the loading screen, would hold the controller of the battle before this one and disarm the watchdog at once.
        static void Fini(int want, string cle, EnvService env)
        {
            _env = env; _cleAppliquee = cle; _surveille = true; _nextWatch = 0f; _pose = true;
            Etat(EtDone, want);
        }

        /// True when the player's own graphics settings make a night expensive (read only, never written).
        static bool NuitLourde()
        {
            SettingsService s = null;
            try { s = Mod.Svc<SettingsService>(); } catch { }
            if (s == null) return false;
            return ReglageInt(s, SettingType.VolumetricFog) > 0 || ReglageInt(s, SettingType.VfxDynamicShadows) > 0;
        }

        // ============================================================ reading the map's own presets
        /// Keys of the map, taken from the game's own dictionary. False when the list cannot be walked.
        /// The walk goes through reflection on the wrapper the interop layer built: the exact shape of an il2cpp generic dictionary
        /// is not something to assume at build time, and this runs once per mission load, never in a hot path. Two ways are tried,
        /// the key collection then the dictionary itself; the strings added are always the game's own, never rebuilt.
        static bool Lire(PresetSet set)
        {
            _cles.Clear();
            _set = set;
            if (set == null) return false;
            object data = null;
            try { data = set.Data; } catch { }
            if (data == null) return false;
            if (!Parcourir(data, "Keys", null) && !Parcourir(data, null, "Key"))
                Mod.Log.Warning("[HEURE] ambiances de la carte illisibles : la liste des ambiances n'a pas pu être parcourue");
            if (_cles.Count > 1) _cles.Sort(StringComparer.Ordinal);     // same order every time: the same choice gives the same ambience
            return _cles.Count > 0;
        }

        /// Walks owner.<collection> (or owner itself) with its own GetEnumerator and keeps each item, or each item's <field>, as a key.
        static bool Parcourir(object owner, string collection, string field)
        {
            try
            {
                object src = owner;
                if (collection != null)
                {
                    var pi = owner.GetType().GetProperty(collection);
                    src = pi?.GetValue(owner);
                }
                var get = src?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
                object it = get?.Invoke(src, null);
                if (it == null) return false;
                var t = it.GetType();
                var move = t.GetMethod("MoveNext", Type.EmptyTypes);
                var cur = t.GetProperty("Current");
                if (move == null || cur == null) return false;
                int guard = 0;
                while (_cles.Count < MaxPresets && ++guard <= MaxPresets * 4 && move.Invoke(it, null) is bool ok && ok)
                {
                    object item = cur.GetValue(it);
                    if (item == null) continue;
                    string k = field == null ? item as string : item.GetType().GetProperty(field)?.GetValue(item) as string;
                    if (!string.IsNullOrEmpty(k)) _cles.Add(k);
                }
                return _cles.Count > 0;
            }
            catch { return false; }
        }

        /// Sorts the map's keys into the four baskets (their spelling is the studio's, never ours).
        static void Ranger()
        {
            for (int i = 0; i < HCount; i++) _paniers[i].Clear();
            foreach (string k in _cles)
            {
                int h = Panier(k);
                if (h >= 0) _paniers[h].Add(k);
            }
        }

        /// Hour a key belongs to, or -1. Halloween ambiences are not a normal night and are left out.
        static int Panier(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            if (Has(key, "haloween") || Has(key, "halloween")) return -1;
            if (Has(key, "night")) return HNight;
            if (Has(key, "morning")) return HMorning;
            if (Has(key, "evening")) return HEvening;
            if (Has(key, "day")) return HDay;
            return -1;
        }

        static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// Inside a basket: the key the studio marked as the map's default, otherwise the first one (the list is already ordinal-sorted).
        /// null when the map owns no key for that hour, and then nothing at all is applied.
        static string Meilleure(int hour)
        {
            var l = _paniers[hour];
            if (l.Count == 0) return null;
            foreach (string k in l) if (EstDefaut(k)) return k;
            return l[0];
        }

        static PresetSet _set;                                       // held: the preset set of the map being loaded

        static PresetData Donnee(string key)
        {
            try { return _set?.Data != null && _set.Data.ContainsKey(key) ? _set.Data[key] : null; }
            catch { return null; }
        }

        static bool EstDefaut(string key)
        {
            var d = Donnee(key);
            try { return d != null && d.IsDefault; } catch { return false; }
        }

        // ============================================================ stage 0: what this map really offers (read only, one line)
        static void Mesure(string scene, string origine)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("carte « ").Append(scene).Append(" » : heure d'origine « ").Append(origine ?? "?").Append(" » ; ambiances de la carte :");
                for (int i = 0; i < _cles.Count; i++)
                {
                    string k = _cles[i];
                    var d = Donnee(k);
                    sb.Append(i == 0 ? " " : ", ").Append('«').Append(' ').Append(k).Append(" » -> ");
                    int h = Panier(k);
                    sb.Append(h < 0 ? "non utilisée" : NomsFr[h]);
                    if (d == null) { sb.Append(" (détail illisible)"); continue; }
                    string vfx = "?"; bool def = false, flash = false;
                    try { vfx = d.DayTimeVfxParameter.ToString(); } catch { }
                    try { def = d.IsDefault; } catch { }
                    // through reflection: the property's own type lives in Unity.Addressables, an assembly the mod does not reference and
                    // will not start referencing for one detail of one log line (this runs once per map key, at a mission load)
                    try { var pi = d.GetType().GetProperty("LightFlashesConfigLink"); flash = pi != null && pi.GetValue(d) != null; } catch { }
                    sb.Append(" (vfx=").Append(vfx).Append(def ? ", défaut" : "").Append(flash ? ", lumières de tir" : "").Append(')');
                    if (h == HNight && !string.Equals(vfx, "Night", StringComparison.Ordinal)) sb.Append(" [donnée du jeu : nuit sans vfx de nuit]");
                }
                sb.Append(" ; réglages graphiques du joueur (lus, jamais modifiés) :");
                SettingsService s = null;
                try { s = Mod.Svc<SettingsService>(); } catch { }
                sb.Append(" sources lumineuses=").Append(Reglage(s, SettingType.LightSourcesCount))
                  .Append(", brouillard volumétrique=").Append(Reglage(s, SettingType.VolumetricFog))
                  .Append(", ombres VFX=").Append(Reglage(s, SettingType.VfxDynamicShadows))
                  .Append(", halos=").Append(Reglage(s, SettingType.VfxLensFlare));
                Log(sb.ToString());
            }
            catch (Exception e) { Mod.Log.Warning("[HEURE] relevé des ambiances impossible : " + e.GetBaseException().Message); }
        }

        static string Reglage(SettingsService s, SettingType t)
        {
            if (s == null) return "?";
            try { return s.GetSettingValue(t, false).ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        static int ReglageInt(SettingsService s, SettingType t)
        {
            try { return s.GetSettingValue(t, false); }
            catch { return -1; }
        }

        // ============================================================ per battle: the watchdog and the one reading of the mission script
        internal static void Frame()
        {
            if (_choix == null) return;
            if (Voulu() < 0 && !_notice.Set) return;                 // "comme la mission": not a single read
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _scanStage >= 2 ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + 0.5f;

            GameController gc = null;
            bool live = false;
            try { gc = GameController._instance; live = gc != null && gc._GameSession_k__BackingField?.CurrentPlayer != null; }
            catch { live = false; }
            // before the battle is really there, what the postfix installed during the loading screen is kept untouched; once the battle
            // has been seen and is gone, the battle is over: everything of it goes and the crash guard is cleared
            if (!live)
            {
                if (_battleSeen > 0f) EndBattle("plus de partie");
                return;
            }

            IntPtr p = gc.Pointer;
            if (_gcPtr == IntPtr.Zero) _gcPtr = p;
            else if (_gcPtr != p) { _gcPtr = p; _surveille = false; _nodes = null; _scanStage = 0; _scanIndex = 0; _scanDtpch = 0; _battleSeen = 0f; }
            if (_battleSeen <= 0f) { _battleSeen = now; _pose = false; }   // the battle is there: the loading latch goes down

            if (_notice.Set) { var m = _notice; _notice = default; Mod.Notify(m); }
            if (_dead) return;
            if (_surveille && now >= _nextWatch) { _nextWatch = now + 1f; Surveille(now); }
            Scan(gc, now);
        }

        /// Once per second while our hour is in place: a mission that changes the light itself wins, and the mod never reapplies.
        /// A change seen in the first seconds of the battle is the end of the loading sequence, not a script: the mod steps aside for
        /// this battle but does not brand the mission, so one odd load can never take the choice away from the player for good.
        static void Surveille(float now)
        {
            string k = null;
            try { k = _env?.CurrentDayTimePreset?.Key; }
            catch (Exception e) { _surveille = false; Fail(e); return; }
            if (k == null || string.Equals(k, _cleAppliquee, StringComparison.Ordinal)) return;
            _surveille = false;
            bool script = now - _battleSeen >= WatchGrace;
            Log($"la lumière a changé sans le mod (« {_cleAppliquee} » -> « {k} »)" +
                (script ? " : la mission la règle elle-même, le mod s'efface pour cette bataille et ne touchera plus l'heure de cette mission"
                        : $" moins de {WatchGrace:0} s après le début de la bataille (fin du chargement) : le mod s'efface pour cette bataille seulement"));
            if (!script) return;
            Marquer(_missionChargee, true);
            Etat(EtScript);
            _notice = TxtKey.N_HEURE_SCRIPT;
        }

        /// Reads the mission's saved node list once, in small chunks, only to know whether it holds a dtpch node.
        static void Scan(GameController gc, float now)
        {
            if (_scanStage >= 2) return;
            string uid = Campaign.MissionUid;
            if (string.IsNullOrEmpty(uid)) return;
            if (Dans(_lues.Value, uid) || Dans(_pilotees.Value, uid)) { _scanStage = 2; return; }
            try
            {
                if (_scanStage == 0)
                {
                    var sc = gc.GetScenarioController;
                    var nc = sc != null && sc.Loaded ? sc.GetNodeController : null;
                    bool ready = false;
                    try { ready = nc != null && nc.IsDataLoaded; } catch { ready = false; }
                    if (!ready || nc.RawLoadData == null)
                    {
                        if (now - _battleSeen < ScanGiveUp) return;
                        _scanStage = 3;
                        Log($"mission {uid} : script illisible dans cette bataille, l'heure reste celle de la mission (nouvel essai à la prochaine bataille)");
                        return;
                    }
                    _nodes = nc.RawLoadData.Nodes;
                    if (_nodes == null || _nodes.Length == 0)
                    {
                        if (now - _battleSeen < ScanGiveUp) return;
                        _scanStage = 3;
                        Log($"mission {uid} : aucune liste de nœuds lisible, l'heure reste celle de la mission");
                        return;
                    }
                    _scanIndex = 0; _scanDtpch = 0; _scanStage = 1;
                    return;
                }

                // one element fetch and one marshalled string per node: the pass is capped AND stops on the stopwatch, like the script
                // dump of Missions.cs, so a mission of a few thousand nodes never costs a visible frame
                int end = Math.Min(_nodes.Length, _scanIndex + ScanChunk);
                _scanSw.Restart();
                for (; _scanIndex < end; _scanIndex++)
                {
                    if ((_scanIndex & 15) == 15 && _scanSw.Elapsed.TotalMilliseconds >= ScanBudgetMs) break;
                    try
                    {
                        var nd = _nodes[_scanIndex];
                        if (nd == null) continue;
                        if (MemeNom(nd.NodeName, NodeDayTime)) _scanDtpch++;
                    }
                    catch { }                                        // one unreadable node is skipped, the reading goes on
                }
                if (_scanIndex < _nodes.Length) return;

                int total = _nodes.Length, found = _scanDtpch;
                _nodes = null; _scanStage = 2;
                Marquer(uid, found > 0);
                if (found > 0) Log($"mission {uid} : son script règle la lumière lui-même ({found} nœud(s) « {NodeDayTime} » sur {total}) : le mod n'y touchera pas");
                else Log($"mission {uid} : script lu ({total} nœuds, aucun changement d'heure) : l'heure choisie s'appliquera au prochain lancement de cette mission");
            }
            catch (Exception e)
            {
                _nodes = null; _scanStage = 3;
                Fail(e);
            }
        }

        /// Node name without case, spaces, underscores or dashes and without a leading "node" (the saved name format is not proven).
        static bool MemeNom(string name, string key)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int i = 0, n = name.Length;
            // a leading "node" is skipped only when something follows it
            if (n > 4 && (name[0] == 'n' || name[0] == 'N') && (name[1] == 'o' || name[1] == 'O')
                      && (name[2] == 'd' || name[2] == 'D') && (name[3] == 'e' || name[3] == 'E')) i = 4;
            int j = 0;
            for (; i < n; i++)
            {
                char c = name[i];
                if (c == ' ' || c == '_' || c == '-') continue;
                if (j >= key.Length) return false;
                if (char.ToLowerInvariant(c) != key[j]) return false;
                j++;
            }
            return j == key.Length;
        }

        // ============================================================ what the mod knows about each mission (kept in the preferences)
        static bool Dans(string list, string uid)
        {
            if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(uid)) return false;
            int from = 0;
            while (from < list.Length)
            {
                int end = list.IndexOf(',', from);
                if (end < 0) end = list.Length;
                if (end - from == uid.Length && string.Compare(list, from, uid, 0, uid.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
                from = end + 1;
            }
            return false;
        }

        static void Marquer(string uid, bool pilotee)
        {
            if (string.IsNullOrEmpty(uid)) return;
            var e = pilotee ? _pilotees : _lues;
            if (Dans(e.Value, uid)) return;
            e.Value = e.Value.Length == 0 ? uid : e.Value + "," + uid;
            Save();
        }

        // ============================================================ small helpers
        /// Hour the player asked for (0..3), or -1 for "comme la mission" (the general switch).
        static int Voulu()
        {
            string v = _choix?.Value;
            if (string.IsNullOrEmpty(v)) return -1;
            for (int i = 1; i < Valeurs.Length; i++)
                if (string.Equals(Valeurs[i].V, v, StringComparison.OrdinalIgnoreCase)) return i - 1;
            return -1;
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

        // ============================================================ texts of the Mod tab row
        /// Changes whenever the row's value or the mod's answer changes: the tab rebuilds its description then, and never per frame.
        internal static int EtatVersion => _etatVer * 8 + (Voulu() + 1);

        internal static string RowDesc(Lang l)
        {
            string body = Txt.Get(l, TxtKey.HD_DESC);
            int want = Voulu();
            if (want < 0) return body + "\n\n" + Txt.Get(l, TxtKey.HD_ST_OFF);
            string etat;
            switch (_etatCode)
            {
                case EtLearn: etat = Txt.Get(l, TxtKey.HD_ST_LEARN); break;
                case EtScript: etat = Txt.Get(l, TxtKey.HD_ST_SCRIPT); break;
                case EtDone: etat = Txt.Format(l, TxtKey.HD_ST_DONE, Nom(_etatA)); break;
                case EtNoHour: etat = Txt.Format(l, TxtKey.HD_ST_NOHOUR, Nom(_etatA)); break;
                case EtFail: etat = Txt.Get(l, TxtKey.HD_ST_FAIL); break;
                case EtOff: etat = Txt.Get(l, TxtKey.HD_ST_SAFETY); break;
                default: etat = Txt.Format(l, TxtKey.HD_ST_NEXT, Nom(want)); break;
            }
            return body + "\n\n" + etat;
        }

        static TxtKey Nom(int hour) =>
            hour == HMorning ? TxtKey.HD_V_MORNING : hour == HDay ? TxtKey.HD_V_DAY :
            hour == HEvening ? TxtKey.HD_V_EVENING : hour == HNight ? TxtKey.HD_V_NIGHT : TxtKey.HD_V_MISSION;
    }
}
