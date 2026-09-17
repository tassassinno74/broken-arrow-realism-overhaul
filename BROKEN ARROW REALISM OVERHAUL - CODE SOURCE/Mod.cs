// Broken Arrow Realism Overhaul
//  0. Startup (Identite.cs): credits and links; Easy Anti-Cheat loaded = message and the game closes; old CampagneDeckLibre.dll
//     in Mods = the mod stays inert for the session. PUBLIC builds write a compact log (ModLog.cs) and short preference texts.
//  1. Adds four divisions to the unit database: "CAMPAGNE RUSSIE 1" / "CAMPAGNE RUSSIE 2" (901 / 903) and "CAMPAGNE USA 1" /
//     "CAMPAGNE USA 2" (902 / 904). Each holds every unit of its side from the base game and the owned DLCs (Dlc.cs) and half of
//     the category points, so custom decks can be built in the hangar.
//  2. In the official campaign, merges every player deck holding BOTH CAMPAGNE divisions of the mission's side into the
//     mission deck (the deck name does not matter; idempotent: same unit+transport folds into one card), keeping free slots so
//     mission scripts can still add their own units. Cards of DLCs the player does not own never reach the mission deck.
//  3. Campaign-only realism v2 (all sides): ammunitions are classified into families (SAM, MANPADS,
//     AA gun, helicopter ATGM, tank shell, artillery...) and each family gets its own range / damage /
//     penetration / speed / dispersion multipliers; directional armour by vehicle role; AA sensors;
//     helicopter and plane speeds. Every change is journaled and restored when the campaign ends or
//     the database reloads, so skirmish / multiplayer never see modified values.
//  4. Campaign-only cheats for the local player only (toggles OFF at every mission start, one-shots on demand), hotkeys or Settings > Mod.
//  5. Exports database stats (CSV) and realism diagnostics so the numbers can be tuned by hand.
//  6. Keeps CAMPAGNE decks out of skirmish / multiplayer (deck lists, lobby, favourites).
//  7. Campaign missions listed in Campaign.MissionsSansMod (the US tutorial US_M01) are played without the mod: no deck merge, no
//     current database, no real stats, no module, no cheat, every installed hook returns at once; the mod is back at the next mission.
//  8. Every text shown in the game (notices, Mod tab, order buttons, division description) follows the game's UI language through
//     Txt.cs (English, French, Russian, German, Simplified Chinese; English for the others). Logs stay French.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using MelonLoader.Utils;

using Il2CppBrokenArrow.Client.Ecs.Campaign;
using Il2CppBrokenArrow.Client.Ecs.Campaign.Data;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.Client.Ecs.Configs.Management;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.Decks;
using Il2CppBrokenArrow.Client.Ecs.Decks.Models;
using Il2CppBrokenArrow.Client.Ecs.Decks_v2;
using Il2CppBrokenArrow.Client.Ecs.AI;
using Il2CppBrokenArrow.Client.Ecs.AI.Systems;
using Il2CppBrokenArrow.Client.Ecs.FogOfWar.Systems;
using Il2CppBrokenArrow.Client.Ecs.GNetwork.Services.Lobby;
using Il2CppBrokenArrow.Client.Ecs.GNetwork.Services.Room;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.BattleEnd;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Network.TeamPanel;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Profile;
using Il2CppBrokenArrow.Client.Ecs.UI.Orders;
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;
using Il2CppInterop.Runtime;
using Il2CppBrokenArrow.Client.Ecs.Utils;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.DataBase.Models;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Data;
using Il2CppBrokenArrow.Shared.Ecs;
using Il2CppBrokenArrow.Shared.Ecs.Services;
using Il2CppNetworkCommon.Enums.Common;
using Il2CppList = Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.DataBase.Models.SpecializationAvailabilities>;
using Il2CppTransportList = Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.DataBase.Models.TransportAvailabilities>;
using Il2CppModList = Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.Client.Ecs.Decks.Models.DeckModData>;

[assembly: MelonInfo(typeof(RealismOverhaul.Mod), "Broken Arrow Realism Overhaul", "0.24.0", "adrien74200", "https://discord.gg/hNUBQhXW8Z")]
[assembly: MelonGame("SteelBalalaikaStudio", "BrokenArrow")]
// patches are applied one class at a time (ApplyPatches): after a game update a renamed method only disables its own patch
[assembly: HarmonyDontPatchAll]

namespace RealismOverhaul
{
    public class Mod : MelonMod
    {
        internal static ModLog Log;

        internal static MelonPreferences_Entry<bool> Actif;
        internal static MelonPreferences_Entry<int> PointsParCategorie;
        internal static MelonPreferences_Entry<int> EmplacementsParCategorie;
        internal static MelonPreferences_Entry<bool> InclureUnitesDLC;
        internal static MelonPreferences_Entry<bool> ExporterStats;

        static float _nextCheck, _nextDeckRefresh, _nextLang;

        public override void OnInitializeMelon()
        {
            Log = new ModLog(LoggerInstance);
            Identite.LogCredits(LoggerInstance);
            // startup checks before any preference or patch: anti-cheat loaded (the game closes) or old mod file present (inert session)
            if (Identite.RunStartupChecks(LoggerInstance)) { AntiCheatActive = Identite.AntiCheatDetected; return; }
            var cat = MelonPreferences.CreateCategory("RealismOverhaul");
            Actif = cat.CreateEntry("Actif", true, description: Build.Desc("false = le mod ne fait plus rien"));
            PointsParCategorie = cat.CreateEntry("PointsParCategorie", 10000, description: Build.Desc("Points par catégorie des divisions CAMPAGNE 1 + 2 réunies (moitié chacune)", "Points par catégorie des divisions CAMPAGNE 1 + 2 réunies."));
            EmplacementsParCategorie = cat.CreateEntry("EmplacementsParCategorie", 7, description: Build.Desc("Nombre de cartes par catégorie des divisions CAMPAGNE (7 = maximum du créateur de decks)", "Cartes par catégorie des divisions CAMPAGNE (7 au maximum)."));
            InclureUnitesDLC = cat.CreateEntry("InclureUnitesDLC", true, description: Build.Desc("true = les divisions CAMPAGNE contiennent aussi les unités des DLC que tu possèdes"));
            ExporterStats = cat.CreateEntry("ExporterStats", true, description: Build.Desc("Exporte les caractéristiques des unités en CSV (une seule fois par version de base)"));
            CurrentDb.Enabled = cat.CreateEntry("BaseActuelleEnCampagne", true, description: Build.Desc("true = les missions de campagne utilisent la base d'unités actuelle du jeu (base d'unités à jour) au lieu de l'ancienne base figée de la mission"));
            Realism.CreatePrefs();
            SanteCombat.CreatePrefs();  // before any database apply: its suspended lots are read by Realism.OffKeys
            Cheats.CreatePrefs();
            Assistants.CreatePrefs();
            EnemyAi.CreatePrefs();
            Demolition.CreatePrefs();
            Spawns.CreatePrefs();   // before ApplyPatches: the spawn patches read their prefs in Prepare()
            Missions.CreatePrefs();
            Obstacles.CreatePrefs();
            S400Mode.CreatePrefs();
            Reperage.CreatePrefs();
            LeurresMesure.CreatePrefs();
            AntiHeliPortee.CreatePrefs();
            AntiHeliTouches.CreatePrefs();
            VueAA.CreatePrefs();
            AltCercles.CreatePrefs();   // before ApplyPatches: the Alt tool patches read its stage
            Affuts.CreatePrefs();
            CalibreMesure.CreatePrefs();
            Couvert.CreatePrefs();
            Dlc.CreatePrefs();
            ApplyForcedPrefs();     // after every CreatePrefs, before ApplyPatches (some patches read their prefs in Prepare())
            ApplyPatches();
            if (Realism.RealModeOn) RealStats.ExportReference();
            string game = "?"; try { game = UnityEngine.Application.version; } catch { }
            Log.Msg($"v{Info.Version} chargé (jeu {game}). Actif={Actif.Value} réalisme={Realism.Enabled.Value} ({(Realism.RealModeOn ? "vraies stats, échelle " + Realism.EchellePortees.Value : Realism.Preset.Value)}) touches triche={Cheats.Enabled.Value}");
        }

        /// True when Identite found Easy Anti-Cheat loaded at startup (the game is being closed and nothing of the mod runs).
        internal static bool AntiCheatActive;

        /// Settings forced at every start (the Settings > Mod tab only shows the cheats). Only these entries: never the automatic
        /// safety ones (SessionsInterrompues, VersionSecurite, ReglagesV021), UnitesS400 or OptionsInactives.
        static readonly (string Cat, string Name, object Value)[] ForcedPrefs =
        {
            ("RealismOverhaul", "Actif", true),
            ("RealismOverhaul", "InclureUnitesDLC", true),
            ("RealismOverhaul", "BaseActuelleEnCampagne", true),
            ("RealismOverhaul_Realisme", "Realisme", true),
            ("RealismOverhaul_Realisme", "VraiesStats", true),
            ("RealismOverhaul_Realisme", "VraiesStatsPartout", true),
            ("RealismOverhaul_Realisme", "EchellePortees", 1f),
            ("RealismOverhaul_Triche", "TouchesTriche", true),
            ("RealismOverhaul_Assistants", "Assistants", true),
            ("RealismOverhaul_Assistants", "FumigeneAutoParDefaut", true),
            ("RealismOverhaul_Assistants", "RepliAutoParDefaut", true),
            ("RealismOverhaul_Assistants", "DebarquementSiToucheParDefaut", true),
            ("RealismOverhaul_Assistants", "IAAmelioree", true),
            ("RealismOverhaul_Assistants", "EmbuscadeActive", false),
            ("RealismOverhaul_Assistants", "RavitaillementAutoParDefaut", false),
            ("RealismOverhaul_IA", "IAEnnemieActive", true),
            ("RealismOverhaul_S400", "S400MissilesParDefaut", true),
            ("RealismOverhaul_Batiments", "BatimentsChargesLourdes", true),
            ("RealismOverhaul_Spawns", "ModeRenforts", "auto"),          // player bubble: enemy reinforcements never appear near his units
            ("RealismOverhaul_Spawns", "SpawnsSurs", true),
            ("RealismOverhaul_Spawns", "MissionsExclues", ""),           // the mod never switches a feature off for one mission
            ("RealismOverhaul_Obstacles", "MesuresObstacles", true),
            ("RealismOverhaul_AntiHeliPortee", "PorteeAntiHelico", true),
            ("RealismOverhaul_Reperage", "DefenseAerienneRealiste", true),
            // v0.23.0
            ("RealismOverhaul_Realisme", "ProtectionBatimentsV4", 1.5f),  // infantry in buildings takes LESS damage (V3 = 0.5 doubled it)
            ("RealismOverhaul_Missions", "MissionsSures", true),
            ("RealismOverhaul_Leurres", "MesureLeurres", true),           // CalibreMesure and Couvert also read its impact depth
            ("RealismOverhaul_AntiHeliTouches", "MesureTouchesHelicos", true),
            ("RealismOverhaul_VueAA", "MesureVueAA", true),
            ("RealismOverhaul_Affuts", "DonneesParUnite", true),
            ("RealismOverhaul_CalibreMesure", "MesureCalibre", true),
            ("RealismOverhaul_CalibreMesure", "RegleDemiBlindage", true),
            ("RealismOverhaul_Couvert", "CouvertForetVegetation", true),
            // v0.24.0
            ("RealismOverhaul_Realisme", "FurtiviteVehicules", 1.0f),     // vehicles are spotted at the sight distance shown on the cards
        };

        /// Writes the forced values that differ (one log line per changed entry); saves the file only when something changed.
        static void ApplyForcedPrefs()
        {
            int changed = 0;
            foreach (var f in ForcedPrefs)
            {
                try
                {
                    var e = MelonPreferences.GetEntry(f.Cat, f.Name);
                    if (e == null) { Log.Warning($"[REGLAGES] {f.Cat}/{f.Name} introuvable : ignoré"); continue; }
                    object cur = e.BoxedValue;
                    if (SamePref(cur, f.Value)) continue;
                    if (cur != null && cur.GetType() != f.Value.GetType()) { Log.Warning($"[REGLAGES] {f.Cat}/{f.Name} : type {cur.GetType().Name} inattendu, ignoré"); continue; }
                    e.BoxedValue = f.Value;
                    changed++;
                    Log.Msg($"[REGLAGES] {f.Cat}/{f.Name} = {PrefText(f.Value)} forcé (avant : {PrefText(cur)})");
                }
                catch (Exception ex) { Log.Warning($"[REGLAGES] {f.Cat}/{f.Name} non forcé : {ex.GetBaseException().Message}"); }
            }
            if (changed == 0) return;
            try { MelonPreferences.Save(); }
            catch (Exception ex) { Log.Warning("[REGLAGES] enregistrement des réglages impossible : " + ex.GetBaseException().Message); }
        }

        static bool SamePref(object cur, object want)
        {
            if (cur is float a && want is float b) return Math.Abs(a - b) < 0.0001f;
            return object.Equals(cur, want);
        }

        static string PrefText(object v) => v == null ? "(vide)" : v is string s ? "\"" + s + "\"" : Convert.ToString(v, CultureInfo.InvariantCulture);

        internal static readonly List<string> PatchFailures = new();

        /// Each Harmony patch class on its own: a method renamed by a game update disables only that patch, never the others.
        void ApplyPatches()
        {
            Type[] types;
            try { types = typeof(Mod).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); Log.Warning("[PATCH] certains types du mod ne se chargent plus (mise à jour du jeu ?)"); }
            int ok = 0;
            foreach (var t in types)
            {
                bool isPatch;
                try { isPatch = t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0; } catch { isPatch = false; }
                if (!isPatch) continue;
                try { HarmonyInstance.CreateClassProcessor(t).Patch(); ok++; }
                catch (Exception e) { PatchFailures.Add(t.Name); Log.Error($"[PATCH] {t.Name} non appliqué (mise à jour du jeu ?) : {e.GetBaseException().Message}"); }
            }
            Log.Msg($"[PATCH] {ok} correctif(s) appliqué(s)" + (PatchFailures.Count > 0 ? $", {PatchFailures.Count} désactivé(s) : {string.Join(", ", PatchFailures)}" : ""));
        }

        static bool _modTabDead;
        // separate method: if the ModTab type itself fails to load, the exception is caught here once instead of every frame
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void RunModTab() => ModTab.Frame();


        public override void OnUpdate()
        {
            if (Identite.Blocked) return;                                  // anti-cheat or old mod file: nothing runs this session
            Planif.BeginFrame();                                           // one heavy module job per frame (Planif.cs)
            // the game's UI language (every 2 s, main thread), before the Mod tab so it relabels in the same frame
            if (!AntiCheatActive)
            {
                float lt = UnityEngine.Time.realtimeSinceStartup;
                if (lt >= _nextLang) { _nextLang = lt + 2f; Guard.Run("Langue", Txt.Poll); }
            }
            // the Options "Mod" tab must stay reachable even when the mod is switched off from it
            if (!_modTabDead && !AntiCheatActive) { try { RunModTab(); } catch (Exception e) { _modTabDead = true; Log.Error("[ONGLET] onglet Mod indisponible : " + e.GetBaseException().Message); } }
            // the damage-reduction hooks must never stay armed once the mod is off or the campaign is left (FastTick only runs inside it)
            if (Cheats.ToughArmed && (!Actif.Value || !Campaign.InCampaign || Campaign.MissionInerte)) Guard.Run("TricheDesarme", Cheats.Disarm);
            if (!Actif.Value || AntiCheatActive) return;

            // Hotkeys and the order-panel assistants run every frame (self-throttled), but only inside a campaign mission,
            // and never in a mission played without the mod (Campaign.MissionInerte).
            bool mission = Campaign.InCampaign && !Campaign.MissionInerte;
            if (mission)
            {
                Perf.Run("Hotkeys", Cheats.ReadHotkeys);
                Perf.Run("TricheRapide", Cheats.FastTick);
                Perf.Run("Assistants", Assistants.Frame);
                // battle-end latch (F22): the modules closed by the battle-end signal never start a new session on the end screen
                if (!Campaign.BattleOver) Perf.Run("Missions", Missions.Frame);          // before EnemyAi and Demolition: both fail closed without its protections
                Perf.Run("IA-Ennemie", EnemyAi.Frame);
                Perf.Run("Demolition", Demolition.Frame);
                Perf.Run("Spawns", Spawns.Frame);
                Perf.Run("Obstacles", Obstacles.Frame);
                Perf.Run("S400", S400Mode.Frame);
                Perf.Run("Reperage", Reperage.Frame);
                Perf.Run("VueAA", VueAA.Frame);
                Perf.Run("Leurres.Principal", LeurresMesure.FramePrincipal);
                Perf.Run("LeurresMesure", LeurresMesure.Frame);
                Perf.Run("AntiHeliPortee", AntiHeliPortee.Frame);
                Perf.Run("AntiHeliTouches", AntiHeliTouches.Frame);
                Perf.Run("CalibreMesure", CalibreMesure.Frame);                            // own end-screen latch (CalibreMesure._endLatch)
                if (!Campaign.BattleOver) Perf.Run("Couvert", Couvert.Frame);
                if (!Campaign.BattleOver) Perf.Run("Affuts", Affuts.Frame);
                Perf.Run("SanteCombat", SanteCombat.Frame);                                // own pause and end-screen handling
                Perf.Run("FinBataille", Campaign.PollBattleEnd);
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            // the 2 s block waits for a frame with no other heavy job, and keeps its own closures out of the per-frame path
            if (now >= _nextCheck && Planif.Take(ref _waitSlow)) { _nextCheck = now + 2f; _slowNow = now; Perf.Run("LentTick", _aSlowTick); }
            Perf.EndFrame(now, mission);
        }

        static int _waitSlow;
        static float _slowNow;
        static readonly Action _aSlowTick = () => SlowTick(_slowNow);

        /// Every 2 s: divisions, campaign state, safety restore and, outside a mission, the deck cache.
        static void SlowTick(float now)
        {
            bool inertBefore = Campaign.MissionInerte;
            Guard.Run("EnsureInjected", InjectCurrent);

            // one guard per step: an error in one step must not skip the others (safety restore above all)
            bool inCampaign = false;
            Guard.Run("Campaign", () => inCampaign = Campaign.Refresh());
            if (inCampaign && Campaign.MissionInerte)
            {
                // mission played without the mod: only the safety restore and the one on-screen notice
                Guard.Run("MissionSansMod", Campaign.InertTick);
            }
            else if (inCampaign)
            {
                Guard.Run("CurrentDb", CurrentDb.Poll);
                Guard.Run("Session", Campaign.NoteSession);
                Guard.Run("LateMerge", Campaign.TryLateMerge);
                Guard.Run("Announce", Campaign.Announce);
                Guard.Run("CheatsTick", Cheats.Tick);
                Guard.Run("SafetyPoll", () => Realism.SafetyPoll(true));
            }
            else
            {
                // just out of a mission without the mod: the menu database gets its divisions before the deck cache reads the decks
                if (inertBefore) Guard.Run("EnsureInjected", InjectCurrent);
                Guard.Run("SafetyPoll", () => Realism.SafetyPoll(false));
                Guard.Run("Routes", CurrentDb.LogRoutes);
                // deck files are read between battles only: no disk reading on the main thread while a battle is running
                if (now >= _nextDeckRefresh && Campaign.Ctx()?.CurrentPlayer == null)
                {
                    _nextDeckRefresh = now + 10f;
                    Guard.Run("DeckCache", DeckCache.Refresh);
                }
            }
        }

        /// Divisions CAMPAGNE on the loaded database (every 2 s), never in a mission played without the mod.
        static void InjectCurrent()
        {
            if (Campaign.MissionInerte) return;
            var db = DataBaseService._instance;
            if (db != null && db.IsLoaded) Divisions.EnsureInjected(db);
        }

        public override void OnApplicationQuit()
        {
            if (Identite.Blocked) return;                                  // nothing was created or patched this session
            Guard.Run("S400.Quit", S400Mode.OnQuit);
            Guard.Run("Reperage.Quit", Reperage.OnQuit);
            Guard.Run("LeurresMesure.Quit", LeurresMesure.OnQuit);
            Guard.Run("AntiHeliPortee.Quit", AntiHeliPortee.OnQuit);
            Guard.Run("AntiHeliTouches.Quit", AntiHeliTouches.OnQuit);
            Guard.Run("VueAA.Quit", VueAA.OnQuit);
            Guard.Run("Spawns.Quit", Spawns.OnQuit);
            Guard.Run("Missions.Quit", Missions.OnQuit);
            Guard.Run("AltCercles.Quit", AltCercles.OnQuit);
            Guard.Run("CalibreMesure.Quit", CalibreMesure.OnQuit);
            Guard.Run("Couvert.Quit", Couvert.OnQuit);
            Guard.Run("Affuts.Quit", Affuts.OnQuit);         // before Realism.Restore
            Guard.Run("SanteCombat.Quit", SanteCombat.OnQuit);
            Guard.Run("Perf.Quit", Perf.EndBattle);          // the last battle of the session keeps its [PERF] line
            try { Realism.Restore("fermeture du jeu"); } catch { }
        }

        internal static T Svc<T>() where T : Il2CppObjectBase
        {
            try { return Session.GetService<T>(); } catch { return null; }
        }

        internal static readonly UnitCategoryType[] Categories =
        {
            UnitCategoryType.Recon, UnitCategoryType.Infantry, UnitCategoryType.Vehicles, UnitCategoryType.Support,
            UnitCategoryType.Logistic, UnitCategoryType.Helicopters, UnitCategoryType.Aircrafts
        };

        internal static string DeckStr(IDeckDataModel d)
        {
            if (d == null) return "<null>";
            try
            {
                var sb = new StringBuilder();
                sb.Append($"'{d.Name}' country={d.CountryID} specs={d.Spec1ID}/{d.Spec2ID} points={Deck.GetTotalPoints(d)} cartes:");
                foreach (var cat in Categories)
                {
                    var arr = Deck.GetCategorySlots(d, cat);
                    int used = 0, total = arr?.Length ?? 0;
                    for (int i = 0; i < total; i++) if (arr[i] != null && arr[i].UnitID > 0) used++;
                    sb.Append($" {cat}={used}/{total}");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "<deck read error: " + e.Message + ">"; }
        }

        /// Shows a message on the game screen (mission alert) in the game's language, and logs it in French.
        internal static void Notify(TxtMsg message)
        {
            string fr = message.Fr;
            NotifyPair(fr, Txt.Current == Lang.FR ? fr : message.Local);
        }

        /// Notice built by the caller in French (log) and in the game's language (screen); shown == null shows the French text.
        internal static void NotifyPair(string fr, string shown)
        {
            Log.Msg("[ECRAN] " + fr);
            try { new LuaStorage(null, null).GameAlert(shown ?? fr); }
            catch (Exception e) { Log.Warning("Alerte à l'écran impossible: " + e.Message); }
        }
    }

    /// Runs mod code so that an exception can never reach the game, with rate-limited logging.
    static class Guard
    {
        static readonly Dictionary<string, (string msg, float next)> _last = new();

        internal static void Run(string what, Action act)
        {
            try { act(); }
            catch (Exception e)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                _last.TryGetValue(what, out var l);
                if (e.Message != l.msg || now >= l.next)
                {
                    _last[what] = (e.Message, now + 10f);
                    Mod.Log.Error($"{what}: {e}");
                }
            }
        }
    }

    // ---------------------------------------------------------------- reflection helpers for DB rows
    static class Props
    {
        static readonly Dictionary<(Type, string), PropertyInfo> _cache = new();
        static readonly Dictionary<Type, PropertyInfo[]> _exportable = new();

        internal static PropertyInfo Get(Type t, string name)
        {
            if (!_cache.TryGetValue((t, name), out var p))
                _cache[(t, name)] = p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            return p;
        }

        internal static double? Num(object row, string name)
        {
            var p = row == null ? null : Get(row.GetType(), name);
            if (p == null) return null;
            return p.GetValue(row) switch { float f => f, double d => d, int i => i, _ => null };
        }

        internal static PropertyInfo[] Exportable(Type t)
        {
            if (_exportable.TryGetValue(t, out var arr)) return arr;
            arr = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.Name.StartsWith("_") &&
                            (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)))
                .ToArray();
            _exportable[t] = arr;
            return arr;
        }

        internal static List<T> Rows<T>(Il2CppSystem.Collections.Generic.IEnumerable<T> src)
        {
            var l = new Il2CppSystem.Collections.Generic.List<T>(src);
            var res = new List<T>(l.Count);
            for (int i = 0; i < l.Count; i++) res.Add(l[i]);
            return res;
        }

        internal static string Csv(object v) =>
            (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace(";", ",").Replace("\n", " ").Replace("\r", " ");
    }

    // ---------------------------------------------------------------- CSV export of the database
    static class StatsExport
    {
        static readonly HashSet<string> _done = new();

        internal static string SafeName(string source) =>
            string.Concat((source ?? "inconnu").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        internal static void Run(DataBaseService db)
        {
            if (!Mod.ExporterStats.Value) return;
            var source = db.CurrentSourceId ?? "inconnu";
            if (!_done.Add(source)) return;
            var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_stats", SafeName(source));
            if (Directory.Exists(dir)) return;
            Directory.CreateDirectory(dir);

            var src = db.RawAccess;
            int n = 0;
            n += Write(dir, "Units", Props.Rows(src.Units.GetAll()));
            n += Write(dir, "Ammunitions", Props.Rows(src.Ammunitions.GetAll()));
            n += Write(dir, "Weapons", Props.Rows(src.Weapons.GetAll()));
            n += Write(dir, "WeaponAmmunitions", Props.Rows(src.WeaponAmmunitions.GetAll()));
            n += Write(dir, "TurretWeapons", Props.Rows(src.TurretWeapons.GetAll()));
            n += Write(dir, "Armors", Props.Rows(src.Armors.GetAll()));
            n += Write(dir, "UnitArmors", Props.Rows(src.UnitArmors.GetAll()));
            n += Write(dir, "Mobility", Props.Rows(src.Mobility.GetAll()));
            n += Write(dir, "UnitPropulsions", Props.Rows(src.UnitPropulsions.GetAll()));
            n += Write(dir, "Sensors", Props.Rows(src.Sensors.GetAll()));
            n += Write(dir, "SensorUnits", Props.Rows(src.SensorUnits.GetAll()));
            n += Write(dir, "PlaneFlyPresets", Props.Rows(src.PlaneFlyPresets.GetAll()));
            n += Write(dir, "Abilities", Props.Rows(src.Abilities.GetAll()));
            n += Write(dir, "Options", Props.Rows(src.Options.GetAll()));
            n += Write(dir, "Modifications", Props.Rows(src.Modifications.GetAll()));
            n += Write(dir, "SquadMembers", Props.Rows(src.SquadMembers.GetAll()));
            Mod.Log.Msg($"Stats exportées ({n} lignes) dans {dir}");
        }

        static int Write<T>(string dir, string name, List<T> rows)
        {
            try
            {
                var props = Props.Exportable(typeof(T));
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(";", props.Select(p => p.Name)));
                foreach (var r in rows)
                    sb.AppendLine(string.Join(";", props.Select(p =>
                    {
                        try { return Props.Csv(p.GetValue(r)); } catch { return ""; }
                    })));
                File.WriteAllText(Path.Combine(dir, name + ".csv"), sb.ToString(), new UTF8Encoding(true));
                return rows.Count;
            }
            catch (Exception e) { Mod.Log.Error($"Export {name}: {e.Message}"); return 0; }
        }
    }

    // ---------------------------------------------------------------- réalisme v2 (campagne, tout le monde, réversible)
    enum Family
    {
        SMOKE, SEAD, BALLISTIC, CRUISE, BOMB_GUIDED, BOMB_DUMB, A2A_RADAR, A2A_IR, MANPADS, AA_LR, AA_SR, AA_GUN,
        HELI_ATGM, A2G_MISSILE, ATGM_TOP, ATGM, ROCKET_AT, ROCKET_POD, PLANE_GUN, ARTY, MORTAR, MLRS,
        KE_SHELL, HE_SHELL, AUTOCANNON, AUTOCANNON_HE, AGL, HMG, SMALL_ARMS, OTHER
    }

    /// Multipliers of one family: ground / low-alt / high-alt range, damage, penetration, projectile speed, dispersion, aim time.
    sealed class Mult
    {
        public double G = 1, L = 1, H = 1, Dmg = 1, Pen = 1, Speed = 1, Disp = 1, Aim = 1;
        public Mult() { }
        public Mult(double g, double l, double h, double dmg, double pen, double speed, double disp, double aim)
        { G = g; L = l; H = h; Dmg = dmg; Pen = pen; Speed = speed; Disp = disp; Aim = aim; }
        public Mult Clone() => (Mult)MemberwiseClone();
        public double MaxRange => Math.Max(G, Math.Max(L, H));
        public bool IsIdentity => G == 1 && L == 1 && H == 1 && Dmg == 1 && Pen == 1 && Speed == 1 && Disp == 1 && Aim == 1;
    }

    static class Realism
    {
        internal static MelonPreferences_Entry<bool> Enabled;
        internal static MelonPreferences_Entry<string> Preset;
        static MelonPreferences_Entry<float> _plafond, _echelleReel;

        // game enum values (read from the interop metadata)
        const int TRAJ_DIRECT = 10, TRAJ_ARTY = 20, TRAJ_MORTAR = 30, TRAJ_MLRS = 40, TRAJ_CRUISE = 200, TRAJ_BALLISTIC = 300,
                  TRAJ_LOWDRAG = 400, TRAJ_HIGHDRAG = 410, TRAJ_LASERFREEFALL = 610;
        const int SEEK_NONE = 0, SEEK_FAF = 10, SEEK_TERMINAL = 50, SEEK_SEMI = 100, SEEK_SEAD = 200;
        const int ARMOR_KIN = 1, ARMOR_HEAT = 2;
        const int UT_INFANTRY = 2, UT_VEHICLE = 4, UT_HELICOPTER = 8, UT_AIRCRAFT = 16;
        const int ROLE_IFV = 10, ROLE_TANK = 11, ROLE_APC = 12, ROLE_LSV = 13, ROLE_LRSAM = 15, ROLE_SRSAM = 16,
                  ROLE_RECONINF = 32, ROLE_SNIPERS = 33, ROLE_AAINF = 34, ROLE_SPECFORCES = 36, ROLE_RECONHELI = 70, ROLE_MULTIHELI = 71,
                  ROLE_ATTACKHELI = 73, ROLE_DRONE = 100, ROLE_PLANE_MIN = 160, ROLE_PLANE_MAX = 164;

        // journal of every value written, so everything can be put back (see "journaled writes" below for the lot layers)
        static DataBaseSourceData _appliedSrc;   // held reference: keeps the native object alive (no address reuse)
        internal static string AppliedSource;
        internal static string AppliedSummary;

        internal static bool IsApplied => _appliedSrc != null;
        internal static bool IsAppliedTo(DataBaseService db) => _appliedSrc != null && db?.RawAccess != null && db.RawAccess.Pointer == _appliedSrc.Pointer;

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Realisme");
            Enabled = c.CreateEntry("Realisme", true, description: Build.Desc("Réalisme en campagne : s'applique à tout le monde pendant les missions de campagne, jamais ailleurs. Pris en compte au chargement de la mission suivante."));
            Preset = c.CreateEntry("Preset", "realiste", description: Build.Desc("realiste | modere | perso (perso = uniquement les fichiers CSV de UserData\\RealismOverhaul_realisme)"));
            _plafond = c.CreateEntry("PlafondPortee", 6000f, description: Build.Desc("Ancien réalisme par familles seulement : portée maximale en mètres (sans effet avec les vraies stats). 0 = pas de plafond"));
            _echelleReel = c.CreateEntry("EchelleReel", 4f, description: Build.Desc("Ancien réalisme par familles seulement : dans les fichiers CSV, une valeur 'r8000' est divisée par ce facteur (sans effet avec les vraies stats : 1 m réel = 1 m en jeu)"));
            _artyAuto = c.CreateEntry("ArtillerieTirAuto", true, description: Build.Desc("Fait apparaître le bouton vanilla 'tir automatique' sur l'artillerie, les mortiers et les lance-roquettes (panneau d'ordres)"));
            _furtivite = c.CreateEntry("FurtiviteReco", 0.7f, description: Build.Desc("Multiplicateur de la valeur Stealth de la reco, des snipers et des forces spéciales (plus petit = plus dur à repérer)"));
            // V4 (v0.23.0): the old V3 entry (0.5) doubled the damage taken inside buildings; a new name so the saved 0.5 is never reused
            _batiments = c.CreateEntry("ProtectionBatimentsV4", 1.5f, description: Build.Desc("Protection de l'infanterie dans les bâtiments (1 = jeu de base ; 1.5 = dégâts reçus à l'intérieur divisés par 1,5 : plancher 0,18 -> 0,12 et 0,02 -> 0,0133 par soldat)"));
            _couvertBat = c.CreateEntry("CouvertBatiments", 2.0f, description: Build.Desc("Multiplicateur du couvert des bâtiments : l'infanterie à l'intérieur n'est repérée que si elle tire ou si on est très près"));
            _furtiviteVeh = c.CreateEntry("FurtiviteVehicules", 1.0f, description: Build.Desc("Multiplicateur de la valeur Stealth des véhicules (1 = valeur du jeu : un véhicule est vu à la distance de vue de sa fiche ; plus petit = plus dur à repérer caché, sauf s'il tire)"));
            _couvert = c.CreateEntry("CouvertForetVegetation", 1.5f, description: Build.Desc("Multiplicateur du couvert des forêts et de la végétation (masque mieux les unités qui ne tirent pas)"));
            _flash = c.CreateEntry("SignatureTirsLourds", 2.0f, description: Build.Desc("Multiplicateur de la signature de tir des armes lourdes (canons, artillerie, lance-roquettes, missiles) : elles se dévoilent en tirant ; armes légères inchangées"));
            _hauteurBat = c.CreateEntry("HauteurObservationBatiments", 2.0f, description: Build.Desc("Multiplicateur de la hauteur d'observation depuis les bâtiments (10 m dans le jeu) : on voit plus loin par-dessus les obstacles bas, la ligne de vue reste bloquée par les autres bâtiments"));
            VraiesStats = c.CreateEntry("VraiesStats", true, description: Build.Desc("true = vraies statistiques par unité (vitesses, portées, munitions, rechargements) et contreparties réalistes ; les multiplicateurs par famille ne sont plus utilisés. false = ancien réalisme par familles"));
            EchellePortees = c.CreateEntry("EchellePortees", 1f, description: Build.Desc("1 = vraies portées (1 m réel = 1 m en jeu). 2 = les portées des vraies stats divisées par deux (celles qui couvrent déjà toute la carte restent à 9 km ; les munitions sans vraies stats gardent leur portée du jeu)"));
            OptionsInactives = c.CreateEntry("OptionsInactives", "OPTION_S400_ANTIMISSILE,OPTION_PANTSIR_ANTIMISSILE",
                description: Build.Desc("Options des vraies stats désactivées (séparées par des virgules). Retirer OPTION_S400_ANTIMISSILE pour que le S-400 (et le S-350) ne tire plus que sur les missiles ; OPTION_PANTSIR_ANTIMISSILE pareil pour le Pantsir (et le DT-30AA)"));
            VraiesStatsPartout = c.CreateEntry("VraiesStatsPartout", true, description: Build.Desc("true = les vraies stats s'appliquent partout (hangar, fiches d'unités, escarmouche, parties sans anti-triche) et pas seulement en mission de campagne. Sous Easy Anti-Cheat, le mod ferme le jeu"));
        }

        internal static MelonPreferences_Entry<bool> VraiesStats;
        internal static MelonPreferences_Entry<float> EchellePortees;
        internal static MelonPreferences_Entry<string> OptionsInactives;
        internal static bool RealModeOn => Enabled != null && Enabled.Value && VraiesStats != null && VraiesStats.Value;

        /// The stored preset is a config word: on screen it goes through the text table like every other notice.
        internal static TxtKey PresetKey
        {
            get
            {
                switch ((Preset?.Value ?? "").Trim().ToLowerInvariant())
                {
                    case "modere": return TxtKey.RL_PRESET_MODERATE;
                    case "perso": return TxtKey.RL_PRESET_CUSTOM;
                    default: return TxtKey.RL_PRESET_REAL;      // same fallback as Apply: anything else is the realistic preset
                }
            }
        }
        internal static MelonPreferences_Entry<bool> VraiesStatsPartout;
        internal static bool EverywhereOn => RealModeOn && VraiesStatsPartout != null && VraiesStatsPartout.Value;
        static MelonPreferences_Entry<float> _hauteurBat;

        static MelonPreferences_Entry<bool> _artyAuto;
        static MelonPreferences_Entry<float> _furtivite, _batiments, _furtiviteVeh, _couvert, _flash, _couvertBat;
        const int ROLE_MLRS = 130, ROLE_MORTAR = 131, ROLE_LAM = 132, ROLE_ARTY = 133;

        static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_realisme");

        // ------------------------------------------------------------ presets
        static Dictionary<Family, Mult> BuildPreset(bool realistic)
        {
            var d = new Dictionary<Family, Mult>();
            void R(Family f, double g, double l, double h, double dmg, double pen, double speed, double disp, double aim,
                   double mg, double ml, double mh, double mdmg, double mpen, double mspeed, double mdisp, double maim)
                => d[f] = realistic ? new Mult(g, l, h, dmg, pen, speed, disp, aim) : new Mult(mg, ml, mh, mdmg, mpen, mspeed, mdisp, maim);
            //             ---------- réaliste ----------             ---------- modéré ----------
            // Real ratios: Tor/Pantsir 12-20 km, Buk/S-300 40-150 km, Stinger/Igla 5 km vs Hellfire/Vikhr 8-10 km.
            R(Family.AA_LR,       1, 2.5, 3.0, 1.25, 1, 1.5, 1, 0.7,     1, 1.75, 2.0, 1.0, 1, 1.25, 1, 0.85);
            R(Family.AA_SR,       1, 3.0, 3.0, 1.5, 1, 1.5, 1, 0.6,      1, 2.0, 2.0, 1.2, 1, 1.25, 1, 0.8);
            R(Family.MANPADS,     1, 2.0, 2.0, 1.75, 1, 1.5, 1, 0.7,     1, 1.5, 1.5, 1.25, 1, 1.25, 1, 0.85);
            R(Family.A2A_RADAR,   1, 1.5, 2.0, 1.5, 1, 1.5, 1, 0.8,      1, 1.25, 1.5, 1.25, 1, 1.25, 1, 0.9);
            R(Family.A2A_IR,      1, 1.5, 1.5, 1.5, 1, 1.5, 1, 0.8,      1, 1.25, 1.25, 1.25, 1, 1.25, 1, 0.9);
            R(Family.AA_GUN,      1.5, 2.0, 1.5, 1.5, 1, 1, 0.7, 0.7,    1.25, 1.5, 1.25, 1.25, 1, 1, 0.85, 0.85);
            R(Family.AUTOCANNON,  1.4, 1.2, 1, 1, 1, 1, 0.8, 0.9,        1.2, 1.1, 1, 1, 1, 1, 0.9, 1);
            // HE autocannon / machine guns / grenade launchers: infantry in the open must not last (Terminator, BTR, Bradley HE).
            R(Family.AUTOCANNON_HE, 1.4, 1.2, 1, 2.2, 1, 1, 0.8, 0.9,    1.2, 1.1, 1, 1.6, 1, 1, 0.9, 1);
            R(Family.HMG,         1.3, 1.1, 1, 1.8, 1, 1, 0.9, 1,        1.15, 1, 1, 1.4, 1, 1, 1, 1);
            R(Family.SMALL_ARMS,  1.2, 1, 1, 1.4, 1, 1, 1, 1,            1.1, 1, 1, 1.2, 1, 1, 1, 1);
            R(Family.AGL,         1.2, 1, 1, 2.0, 1, 1, 0.9, 1,          1.1, 1, 1, 1.5, 1, 1, 1, 1);
            R(Family.KE_SHELL,    1.6, 1, 1, 1.5, 1.3, 1.2, 0.5, 0.8,    1.3, 1, 1, 1.25, 1.15, 1.1, 0.7, 0.9);
            R(Family.HE_SHELL,    1.3, 1, 1, 1.2, 1, 1, 0.8, 0.9,        1.15, 1, 1, 1.1, 1, 1, 0.9, 1);
            // Calibrated on the exported stats: Abrams/T-90M = 17 HP, HEAT armour front 1300-1400 / sides 250-500 / rear 100-200.
            // Kornet 12 dmg x1.7 = 20 with pen 1000 x1.5 = 1500 : rear/side hit = one kill, front hit ~50 % = two kills.
            R(Family.ATGM,        1.8, 1.5, 1, 1.7, 1.5, 1.5, 1, 0.9,    1.4, 1.25, 1, 1.3, 1.25, 1.25, 1, 1);
            R(Family.ATGM_TOP,    1.8, 1.5, 1, 1.7, 1.3, 1.5, 1, 0.9,    1.4, 1.25, 1, 1.3, 1.15, 1.25, 1, 1);
            R(Family.HELI_ATGM,   1.6, 1.5, 1, 1.7, 1.5, 1.5, 1, 0.9,    1.3, 1.25, 1, 1.3, 1.25, 1.25, 1, 1);
            R(Family.A2G_MISSILE, 1.8, 1, 1, 1.3, 1.3, 1.3, 1, 0.9,      1.4, 1, 1, 1.15, 1.15, 1.15, 1, 1);
            // RPG-7 PG-7VR 9 dmg x1.9 = 17 : one kill from the rear of a tank, hopeless from the front (pen 650 x1.3 vs 1400).
            R(Family.ROCKET_AT,   1.3, 1, 1, 1.9, 1.3, 1, 0.8, 1,        1.15, 1, 1, 1.4, 1.15, 1, 0.9, 1);
            R(Family.ROCKET_POD,  1.6, 1, 1, 1.2, 1, 1, 0.8, 1,          1.3, 1, 1, 1.1, 1, 1, 0.9, 1);
            R(Family.PLANE_GUN,   1.3, 1, 1, 1.2, 1, 1, 0.9, 1,          1.15, 1, 1, 1.1, 1, 1, 1, 1);
            R(Family.ARTY,        1.5, 1, 1, 1.3, 1, 1, 0.7, 0.8,        1.25, 1, 1, 1.15, 1, 1, 0.85, 0.9);
            R(Family.MORTAR,      1.3, 1, 1, 1.2, 1, 1, 0.8, 0.8,        1.15, 1, 1, 1.1, 1, 1, 0.9, 0.9);
            R(Family.MLRS,        1.6, 1, 1, 1.3, 1, 1.3, 0.8, 0.8,      1.3, 1, 1, 1.15, 1, 1.15, 0.9, 0.9);
            R(Family.SEAD,        1.5, 1.5, 1.5, 1.2, 1, 1.5, 1, 0.9,    1.25, 1.25, 1.25, 1.0, 1, 1.25, 1, 1);
            R(Family.CRUISE,      1, 1, 1, 1.5, 1, 1.2, 0.7, 1,          1, 1, 1, 1.25, 1, 1.1, 0.85, 1);
            R(Family.BALLISTIC,   1, 1, 1, 1.5, 1, 1.5, 0.5, 1,          1, 1, 1, 1.25, 1, 1.25, 0.7, 1);
            R(Family.BOMB_GUIDED, 1.5, 1, 1, 1.5, 1, 1, 0.5, 1,          1.25, 1, 1, 1.25, 1, 1, 0.7, 1);
            R(Family.BOMB_DUMB,   1, 1, 1, 1.5, 1, 1, 1, 1,              1, 1, 1, 1.25, 1, 1, 1, 1);
            d[Family.SMOKE] = new Mult();
            d[Family.OTHER] = new Mult();
            return d;
        }

        /// Sensors multipliers (ground, low, high) per role group, armour (heatFront, sides, rear, top) per role group, speeds.
        sealed class TablePreset
        {
            public double[] SensAA, SensRecon, SensPlane, SensOther;
            public double TankHeatFront, TankSides, TankRear, TankTop, IfvSides, IfvRear, IfvTop, HeliSpeed, PlaneSpeed;
        }

        // Optics rows are shared by quality (Exceptional 1200, Very good 1000, Good 875, Medium+ 700, Medium 600 raw metres);
        // a row used by several roles follows its majority role group; recon / snipers / attack helicopters (x2.0) keep their own rows.
        // Tank front HEAT armour is left at x1.0 so an ATGM front hit stays around 50 % damage (two hits to kill).
        static TablePreset BuildTables(bool realistic) => realistic
            ? new TablePreset { SensAA = new[] { 1.0, 3.0, 3.0 }, SensRecon = new[] { 2.0, 1.6, 1.6 }, SensPlane = new[] { 1.5, 1.5, 1.5 }, SensOther = new[] { 1.0, 1.5, 1.5 },
                                TankHeatFront = 1.0, TankSides = 0.6, TankRear = 0.3, TankTop = 0.3, IfvSides = 0.7, IfvRear = 0.5, IfvTop = 0.5, HeliSpeed = 1.2, PlaneSpeed = 1.2 }
            : new TablePreset { SensAA = new[] { 1.0, 2.0, 2.0 }, SensRecon = new[] { 1.5, 1.3, 1.3 }, SensPlane = new[] { 1.25, 1.25, 1.25 }, SensOther = new[] { 1.0, 1.25, 1.25 },
                                TankHeatFront = 1.0, TankSides = 0.75, TankRear = 0.45, TankTop = 0.45, IfvSides = 0.8, IfvRear = 0.6, IfvTop = 0.6, HeliSpeed = 1.1, PlaneSpeed = 1.1 };

        // ------------------------------------------------------------ classification
        sealed class AmmoCtx
        {
            public HashSet<int> WeaponTypes = new();
            public HashSet<int> Roles = new();
            public int UnitTypes;          // OR of UnitType flags
            public bool OnlyAircraft = true;
            public int Units;
        }

        static bool IsMissile(Ammunitions a)
        {
            int t = (int)a.TrajectoryType;
            return t == 100 || t == 110 || t == 120 || t == 600 || (int)a.Seeker != SEEK_NONE;
        }
        static bool IsBomb(Ammunitions a) { int t = (int)a.TrajectoryType; return t == TRAJ_LOWDRAG || t == TRAJ_HIGHDRAG || t == TRAJ_LASERFREEFALL; }

        static Family Classify(Ammunitions a, AmmoCtx c)
        {
            var wt = c.WeaponTypes;
            bool air = a.LowAltRange > 0 || a.HighAltRange > 0;
            bool ground = a.GroundRange > 0;
            int seeker = (int)a.Seeker, traj = (int)a.TrajectoryType, armor = (int)a.ArmorTargeted;
            bool guided = seeker != SEEK_NONE || a.LaserGuided || traj != TRAJ_DIRECT;
            bool inf = (c.UnitTypes & UT_INFANTRY) != 0, heli = (c.UnitTypes & UT_HELICOPTER) != 0, plane = (c.UnitTypes & UT_AIRCRAFT) != 0;
            bool onlyAir = c.Units > 0 && c.OnlyAircraft;

            if (a.GenerateSmoke) return Family.SMOKE;
            if (seeker == SEEK_SEAD || wt.Contains(84)) return Family.SEAD;
            if (traj == TRAJ_BALLISTIC || wt.Contains(83)) return Family.BALLISTIC;
            if (traj == TRAJ_CRUISE || wt.Contains(82)) return Family.CRUISE;
            if (wt.Contains(162) || traj == TRAJ_LASERFREEFALL || (IsBomb(a) && (a.LaserGuided || seeker != SEEK_NONE))) return Family.BOMB_GUIDED;
            if (wt.Contains(160) || wt.Contains(161) || traj == TRAJ_LOWDRAG || traj == TRAJ_HIGHDRAG) return Family.BOMB_DUMB;
            // A missile also fired by SAM vehicles or MANPADS infantry (Stinger, Igla) belongs to those families, not to the air-to-air ones.
            bool groundAA = wt.Contains(85) || wt.Contains(86);
            if (!groundAA && (wt.Contains(87) || (onlyAir && !ground && air && (seeker == SEEK_SEMI || seeker == SEEK_TERMINAL)))) return Family.A2A_RADAR;
            if (!groundAA && (wt.Contains(88) || (onlyAir && !ground && air && seeker == SEEK_FAF))) return Family.A2A_IR;
            if (wt.Contains(85) || (!ground && air && IsMissile(a) && inf)) return Family.MANPADS;
            if (wt.Contains(86) && c.Roles.Contains(ROLE_LRSAM)) return Family.AA_LR;
            if (wt.Contains(86) || (!ground && air && IsMissile(a))) return Family.AA_SR;
            if (wt.Contains(115) || (!ground && air && traj == TRAJ_DIRECT)) return Family.AA_GUN;
            // indirect fire before the ATGM test: every artillery round is "guided" (trajectory != direct) and HEAT
            if (wt.Contains(111) || traj == TRAJ_ARTY) return Family.ARTY;
            if (wt.Contains(112) || traj == TRAJ_MORTAR) return Family.MORTAR;
            if (wt.Contains(113) || traj == TRAJ_MLRS) return Family.MLRS;
            if ((wt.Contains(80) || wt.Contains(81) || wt.Contains(89)) && heli && ground) return Family.HELI_ATGM;
            if (wt.Contains(89) || (plane && ground && IsMissile(a))) return Family.A2G_MISSILE;
            bool atgm = wt.Contains(80) || wt.Contains(81) || (armor == ARMOR_HEAT && guided && ground);
            if (atgm && (a.TopArmorAttack || a.IsTopArmorArmorAttack)) return Family.ATGM_TOP;
            if (atgm) return Family.ATGM;
            if (wt.Contains(62) || wt.Contains(63) || (armor == ARMOR_HEAT && !guided && traj == TRAJ_DIRECT && inf)) return Family.ROCKET_AT;
            if (wt.Contains(140)) return Family.ROCKET_POD;
            if (wt.Contains(116) || wt.Contains(141)) return Family.PLANE_GUN;
            if (wt.Contains(110) && armor == ARMOR_KIN) return Family.KE_SHELL;
            if (wt.Contains(110)) return Family.HE_SHELL;
            if (wt.Contains(114)) return armor == ARMOR_KIN ? Family.AUTOCANNON : Family.AUTOCANNON_HE;
            if (wt.Contains(60) || wt.Contains(61) || wt.Contains(64)) return Family.AGL;
            if (wt.Contains(28) || wt.Contains(29) || wt.Contains(4)) return Family.HMG;
            if (((int)a.UiOptions & 1) != 0 || wt.Overlaps(new[] { 1, 2, 3, 5, 6, 25, 26, 27, 30, 40 })) return Family.SMALL_ARMS;
            return Family.OTHER;
        }

        static bool IsHeavy(Family f) => f == Family.ARTY || f == Family.MLRS || f == Family.MORTAR || f == Family.KE_SHELL || f == Family.HE_SHELL
            || f == Family.ATGM || f == Family.ATGM_TOP || f == Family.HELI_ATGM || f == Family.ROCKET_AT || f == Family.ROCKET_POD
            || f == Family.AA_LR || f == Family.AA_SR || f == Family.BALLISTIC || f == Family.CRUISE;

        static bool IsAA(Family f) => f == Family.AA_LR || f == Family.AA_SR || f == Family.MANPADS || f == Family.AA_GUN || f == Family.A2A_RADAR || f == Family.A2A_IR;

        // ------------------------------------------------------------ journaled writes
        // Every write is journaled with the lot (real-stats option key) of the scope open on this thread: Realism.Lot(key). A property
        // written under several lots keeps one entry per lot, linked in write order: the first entry holds the game's own value, each later
        // one the value its lot found. RestoreLot(key) takes one lot's entries out without touching the others; Restore takes everything out.
        sealed class JEntry
        {
            internal object Row;
            internal PropertyInfo P;
            internal IntPtr Ptr;
            internal object Orig, Written;       // value found before this lot's first write / last value written under this lot
            internal string Lot;                 // null = no lot
            internal bool Layer, Undone;         // Layer: not the first entry of this property (Orig is not the game's value)
            internal JEntry Prev, Next;          // same object and property, write order
        }

        static readonly object _jlock = new();
        static readonly List<JEntry> _journal = new();
        static readonly Dictionary<(IntPtr, string), JEntry> _last = new();                // last entry of each (object, property)
        static int _baseCount;
        static readonly List<(Action undo, string lot, (IntPtr, string) key)> _undo = new();
        static readonly HashSet<(IntPtr, string)> _undoKeys = new();
        static readonly Dictionary<string, int> _lotCount = new(StringComparer.OrdinalIgnoreCase);         // journal entries per lot
        [ThreadStatic] static string _currentLot;

        static void CountLot(string lot) { if (lot != null) _lotCount[lot] = (_lotCount.TryGetValue(lot, out int n) ? n : 0) + 1; }

        /// Number of values journaled (one per object and property, whatever the number of lots).
        internal static int JournalCount { get { lock (_jlock) return _baseCount; } }

        /// Lot of the writes made on this thread (null = none).
        internal static string CurrentLot => _currentLot;

        /// Scope of a lot: every journaled write made on this thread until Dispose belongs to that option key.
        /// Usage: using (Realism.Lot("OPTION_BALISTIQUE_REELLE")) { ... }. Scopes nest; null or blank = no lot.
        internal static LotScope Lot(string key) => new LotScope(string.IsNullOrWhiteSpace(key) ? null : key.Trim());

        internal readonly struct LotScope : IDisposable
        {
            readonly string _prev;
            internal LotScope(string lot) { _prev = _currentLot; _currentLot = lot; }
            public void Dispose() => _currentLot = _prev;
        }

        static void Set(object row, PropertyInfo p, object value)
        {
            var ptr = ((Il2CppObjectBase)row).Pointer;
            lock (_jlock)
            {
                string lot = _currentLot;
                var key = (ptr, p.Name);
                if (!_last.TryGetValue(key, out var e))
                {
                    e = new JEntry { Row = row, P = p, Ptr = ptr, Orig = p.GetValue(row), Lot = lot };
                    _last[key] = e; _journal.Add(e); _baseCount++; CountLot(lot);
                }
                else if (!string.Equals(e.Lot, lot, StringComparison.OrdinalIgnoreCase))
                {
                    var layer = new JEntry { Row = row, P = p, Ptr = ptr, Orig = p.GetValue(row), Lot = lot, Layer = true, Prev = e };
                    e.Next = layer; _last[key] = layer; _journal.Add(layer); e = layer; CountLot(lot);
                }
                p.SetValue(row, value);
                e.Written = value;
            }
        }

        /// Copy of the journaled writes (row, property, game's original value), one per object and property: the per-unit copies follow them.
        internal static List<(object row, PropertyInfo p, object orig)> JournalSnapshot()
        {
            lock (_jlock)
            {
                var l = new List<(object row, PropertyInfo p, object orig)>(_baseCount);
                foreach (var e in _journal) if (!e.Layer && !e.Undone) l.Add((e.Row, e.P, e.Orig));
                return l;
            }
        }

        /// Lot of the current value of a journaled property (null = not journaled or no lot).
        internal static string LotOf(object row, PropertyInfo p)
        {
            if (row is not Il2CppObjectBase o || p == null) return null;
            lock (_jlock) return _last.TryGetValue((o.Pointer, p.Name), out var e) ? e.Lot : null;
        }

        /// Journaled change that is not a property (a dictionary entry): undone at restore, recorded once per owner and key, under the
        /// lot of the current scope.
        internal static void JournalUndo(IntPtr owner, string key, Action undo)
        {
            lock (_jlock)
            {
                var k = (owner, key);
                if (_undoKeys.Add(k)) { _undo.Add((undo, _currentLot, k)); CountLot(_currentLot); }
            }
        }

        /// Number of journaled changes currently held by this lot (property entries and non-property changes).
        internal static int LotWrites(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return 0;
            lock (_jlock) return _lotCount.TryGetValue(key.Trim(), out int n) ? n : 0;
        }

        /// Takes out the changes journaled under one lot: non-property changes, then property writes in reverse order, each put back to the
        /// value the lot found. A value a later write (another lot or no lot) has since replaced is left as it is; that later entry then
        /// restores to the value this lot found. Runtime safe (plain property writes). Returns the number of values put back.
        internal static int RestoreLot(string key, string reason)
        {
            if (string.IsNullOrWhiteSpace(key)) return 0;
            key = key.Trim();
            int ok = 0, ko = 0, kept = 0;
            lock (_jlock)
            {
                for (int i = _undo.Count - 1; i >= 0; i--)
                {
                    var u = _undo[i];
                    if (!string.Equals(u.lot, key, StringComparison.OrdinalIgnoreCase)) continue;
                    try { u.undo(); ok++; } catch { ko++; }
                    _undoKeys.Remove(u.key);
                    _undo.RemoveAt(i);
                }
                bool any = false;
                for (int i = _journal.Count - 1; i >= 0; i--)
                {
                    var e = _journal[i];
                    if (e.Undone || !string.Equals(e.Lot, key, StringComparison.OrdinalIgnoreCase)) continue;
                    any = true;
                    e.Undone = true;
                    var k = (e.Ptr, e.P.Name);
                    if (e.Next != null)
                    {
                        e.Next.Orig = e.Orig;
                        e.Next.Prev = e.Prev;
                        if (e.Prev != null) e.Prev.Next = e.Next; else e.Next.Layer = false;
                        kept++;
                        continue;
                    }
                    try { e.P.SetValue(e.Row, e.Orig); ok++; } catch { ko++; }
                    if (e.Prev != null) { e.Prev.Next = null; _last[k] = e.Prev; }
                    else { _last.Remove(k); _baseCount--; }
                }
                if (any) _journal.RemoveAll(x => x.Undone);
                _lotCount.Remove(key);
            }
            if (ok + ko + kept > 0)
                Mod.Log.Msg($"[ECHELLE] valeurs du moteur du lot {key} retirées ({reason}) : {ok} remise(s)" +
                            (kept > 0 ? $", {kept} déjà remplacée(s) par une écriture plus récente" : "") + (ko > 0 ? $", {ko} échec(s)" : ""));
            return ok;
        }

        // ------------------------------------------------------------ real-scale lots (v0.24.0): engine part
        // RealStats validates and writes the data lots, the ballistics lot's muzzle velocities and its gravity, and withdraws a lot as a whole
        // (RealStats.LotAccepte / RestoreLot / ResumeLots). This class writes the engine values that go with an accepted lot, under that lot.
        internal const string LotBallistics = "OPTION_BALISTIQUE_REELLE";
        internal const string LotArtilleryEscape = "OPTION_FUITE_ARTILLERIE_REELLE";

        /// Option keys switched off: the OptionsInactives list plus the lots the combat watchdog suspended (this version, or the battle
        /// still running).
        internal static HashSet<string> OffKeys()
        {
            var s = new HashSet<string>((OptionsInactives?.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            try { foreach (var k in SanteCombat.SuspendedLots()) s.Add(k); } catch { }
            return s;
        }

        static int Scale(object row, string name, double mult, double? capRaw = null)
        {
            if (row == null || Math.Abs(mult - 1.0) < 1e-6) return 0;
            var p = Props.Get(row.GetType(), name);
            if (p == null || !p.CanWrite) return 0;
            switch (p.GetValue(row))
            {
                case float f when f > 0:
                {
                    double v = f * mult;
                    if (capRaw.HasValue && capRaw.Value > 0 && v > capRaw.Value) v = Math.Max(f, capRaw.Value);
                    Set(row, p, (float)v); return 1;
                }
                case double d when d > 0: Set(row, p, d * mult); return 1;
                case int i when i > 0: Set(row, p, Math.Max(1, (int)Math.Round(i * mult))); return 1;
            }
            return 0;
        }

        internal static void Restore(string reason)
        {
            lock (_jlock) { if (_appliedSrc == null && _journal.Count == 0 && _undo.Count == 0) return; }
            var copies = UnitCopies.TakePlan();                                  // no new copy is touched from here on
            int ok = 0, ko = 0;
            lock (_jlock)
            {
                for (int i = _undo.Count - 1; i >= 0; i--)
                {
                    try { _undo[i].undo(); ok++; } catch { ko++; }
                }
                _undo.Clear();
                _undoKeys.Clear();
                // reverse write order: a later lot layer goes back to the value it found, then the first entry to the game's value
                for (int i = _journal.Count - 1; i >= 0; i--)
                {
                    var e = _journal[i];
                    if (e.Undone) continue;
                    try { e.P.SetValue(e.Row, e.Orig); ok++; } catch { ko++; }
                }
                _journal.Clear();
                _last.Clear();
                _baseCount = 0;
                _lotCount.Clear();
            }
            UnitCopies.ReverseSync(copies);
            _appliedSrc = null;
            AppliedSource = null;
            AppliedSummary = null;
            Assistants.ReArmAi();
            Mod.Log.Msg($"[REALISME] annulé ({reason}) : {ok} valeurs restaurées" + (ko > 0 ? $", {ko} échecs" : ""));
        }

        /// Called every 2 s: inside a campaign nothing to do; outside, any leftover realism is undone.
        internal static void SafetyPoll(bool inCampaign)
        {
            if (!inCampaign) SanteCombat.OutOfCampaign();                        // a lot cut in the last battle comes back at the next apply
            if (!IsApplied)
            {
                if (!inCampaign && EverywhereOn) ApplyEverywhere();
                return;
            }
            if (!inCampaign)
            {
                if (!EverywhereOn) { Restore("hors campagne"); return; }
                var cur = DataBaseService._instance;
                if (cur == null || cur.RawAccess == null || cur.RawAccess.Pointer != _appliedSrc.Pointer) { Restore("la base du jeu a été remplacée"); ApplyEverywhere(); }
                return;
            }
            var db = DataBaseService._instance;
            if (db == null || db.RawAccess == null || db.RawAccess.Pointer != _appliedSrc.Pointer)
                Restore("la base du jeu a été remplacée");
        }

        static DataBaseSourceData _everywhereFailed;   // held reference: a database that failed is not retried every 2 s

        /// Real stats everywhere: (re)applies them to the database in use outside a campaign mission (menu, hangar, skirmish).
        static void ApplyEverywhere()
        {
            var db = DataBaseService._instance;
            if (db == null || !db.IsLoaded || db.RawAccess == null || IsAppliedTo(db)) return;
            if (_everywhereFailed != null && _everywhereFailed.Pointer == db.RawAccess.Pointer) return;
            Log($"[VRAIES STATS] application hors mission de campagne (source={db.CurrentSourceId})");
            Apply(db);
            if (!IsAppliedTo(db)) _everywhereFailed = db.RawAccess;
        }

        internal static bool ShouldApply(DataBaseService db, int loadsThisMission)
        {
            if (!Enabled.Value || db == null || db.RawAccess == null) return false;
            if (IsAppliedTo(db)) return false;
            bool camp = Campaign.Refresh();
            if (camp && Campaign.MissionInerte) return false;          // mission played without the mod: its database stays as the game loaded it
            if (EverywhereOn && (!camp || Campaign.MissionUid == null)) { Log($"[VRAIES STATS] application hors mission de campagne (source={db.CurrentSourceId})"); return true; }
            if (!camp || Campaign.MissionUid == null) { Log("[REALISME] base ignorée (pas de mission de campagne en cours)"); return false; }
            if (Campaign.MissionDbSource != null)
            {
                // the mod chose the mission database itself: only that one may be modified
                bool ok = db.RawAccess.Pointer == Campaign.MissionDbSource.Pointer;
                if (!ok) Log($"[REALISME] base ignorée (ce n'est pas la base de la mission : source={db.CurrentSourceId})");
                return ok;
            }
            // the mod is about to switch this mission to the current database: wait for it instead of applying then restoring
            if (Mod.Actif.Value && CurrentDb.Enabled.Value && !CurrentDb.IsDefault(db) && (CurrentDb.FailedSrc == null || CurrentDb.FailedSrc.Pointer != db.RawAccess.Pointer))
            {
                Log($"[REALISME] en attente de la base actuelle (source={db.CurrentSourceId})");
                return false;
            }
            bool isDefault = string.Equals(db.CurrentSourceId, "Resources default", StringComparison.OrdinalIgnoreCase);
            if (isDefault && loadsThisMission != 1)
            {
                Log($"[REALISME] base de menu ignorée (source={db.CurrentSourceId}, chargement n°{loadsThisMission} de la mission)");
                return false;
            }
            return true;
        }

        static void Log(string s) => Mod.Log.Msg(s);

        // ------------------------------------------------------------ apply
        internal static void Apply(DataBaseService db)
        {
            if (IsApplied) Restore("nouvelle application");
            var src = db.RawAccess;
            var preset = (Preset.Value ?? "realiste").Trim().ToLowerInvariant();
            bool perso = preset == "perso";
            bool realistic = preset != "modere";
            bool reel = RealModeOn;
            var mults = perso || reel ? BuildPreset(true).ToDictionary(k => k.Key, k => new Mult()) : BuildPreset(realistic);
            var tables = perso || reel ? new TablePreset { SensAA = new[] { 1.0, 1.0, 1.0 }, SensRecon = new[] { 1.0, 1.0, 1.0 }, SensPlane = new[] { 1.0, 1.0, 1.0 }, SensOther = new[] { 1.0, 1.0, 1.0 },
                                                   TankHeatFront = 1, TankSides = 1, TankRear = 1, TankTop = 1, IfvSides = 1, IfvRear = 1, IfvTop = 1, HeliSpeed = 1, PlaneSpeed = 1 }
                                : BuildTables(realistic);
            double cap = reel ? 0 : _plafond.Value;

            try
            {
                Directory.CreateDirectory(Dir);
                WriteReferenceFiles();
                if (!reel) Overrides.ApplyMultiplierFile(Path.Combine(Dir, "Multiplicateurs.csv"), mults);
                else Log("[REALISME] mode vraies stats : multiplicateurs par famille et plafond de portée ignorés");

                // ---- joins: ammo -> weapon types / unit types / roles ; weapon -> ammo ids ; sensor/armor/mobility -> roles/types
                var ctx = new Dictionary<int, AmmoCtx>();
                var weaponAmmo = new Dictionary<int, List<int>>();
                foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
                {
                    if (!ctx.TryGetValue(wa.AmmunitionId, out var c)) ctx[wa.AmmunitionId] = c = new AmmoCtx();
                    if (src.Weapons.TryGetById(wa.WeaponId, out var w) && w != null) c.WeaponTypes.Add((int)w.Type);
                    if (src.Units.TryGetById(wa.UnitId, out var u) && u != null)
                    {
                        int ut = (int)u.Type;
                        c.UnitTypes |= ut;
                        c.Roles.Add((int)u.Role);
                        c.Units++;
                        if ((ut & UT_AIRCRAFT) == 0) c.OnlyAircraft = false;
                    }
                    if (!weaponAmmo.TryGetValue(wa.WeaponId, out var l)) weaponAmmo[wa.WeaponId] = l = new List<int>();
                    l.Add(wa.AmmunitionId);
                }
                var unitRole = new Dictionary<int, int>();
                var unitType = new Dictionary<int, int>();
                foreach (var u in Props.Rows(src.Units.GetAll())) { unitRole[u.Id] = (int)u.Role; unitType[u.Id] = (int)u.Type; }

                // ---- ammunitions
                var families = new Dictionary<int, Family>();
                var counts = new Dictionary<Family, int>();
                var diag = new StringBuilder();
                diag.AppendLine("Id;Name;HUDName;Family;WeaponTypes;UnitTypes;Roles;GroundRange;LowAltRange;HighAltRange;MinimalRange;Damage;PenetrationAtMinRange;PenetrationAtGroundRange;MuzzleVelocity;MaxSpeed;BurnTime;MaxSeekerDistance;TargetType;ArmorTargeted;TrajectoryType;Seeker;LaserGuided;TopArmorAttack");
                var ammoRows = Props.Rows(src.Ammunitions.GetAll());
                foreach (var a in ammoRows)
                {
                    ctx.TryGetValue(a.Id, out var c);
                    c ??= new AmmoCtx();
                    var fam = Overrides.ForcedFamily(a.Id) ?? Classify(a, c);
                    families[a.Id] = fam;
                    counts[fam] = counts.GetValueOrDefault(fam) + 1;
                    diag.AppendLine(string.Join(";", new object[] { a.Id, Props.Csv(a.Name), Props.Csv(a.HUDName), fam, string.Join("|", c.WeaponTypes.OrderBy(x => x)), c.UnitTypes,
                        string.Join("|", c.Roles.OrderBy(x => x)), a.GroundRange, a.LowAltRange, a.HighAltRange, a.MinimalRange, a.Damage, a.PenetrationAtMinRange, a.PenetrationAtGroundRange,
                        a.MuzzleVelocity, a.MaxSpeed, a.BurnTime, a.MaxSeekerDistance, (int)a.TargetType, (int)a.ArmorTargeted, (int)a.TrajectoryType, (int)a.Seeker, a.LaserGuided, a.TopArmorAttack }
                        .Select(Props.Csv)));
                }
                File.WriteAllText(Path.Combine(Dir, "Familles_" + StatsExport.SafeName(db.CurrentSourceId) + ".csv"), diag.ToString(), new UTF8Encoding(true));

                int nAmmo = 0;
                foreach (var a in ammoRows)
                {
                    var m = mults[families[a.Id]];
                    if (m.IsIdentity)
                    {
                        // real stats: heavy warheads still pierce most of the building cover (the data can still set its own value)
                        var hf = families[a.Id];
                        if (reel && (hf == Family.BALLISTIC || hf == Family.CRUISE || hf == Family.BOMB_DUMB || hf == Family.BOMB_GUIDED) && a.IgnoreCover < 0.7f)
                            SetJournaled(a, "IgnoreCover", 0.7f);
                        continue;
                    }
                    nAmmo++;
                    Scale(a, "GroundRange", m.G, cap);
                    Scale(a, "LowAltRange", m.L, cap);
                    Scale(a, "HighAltRange", m.H, cap);
                    Scale(a, "_dispersionReferenceRange", m.G);
                    if (IsAA(families[a.Id]))
                    {
                        Scale(a, "NonSeadProjectileRangeOverride", m.L, cap);
                        Scale(a, "SeadProjectileRangeOverride", m.L, cap);
                    }
                    if (m.MaxRange > 1)
                    {
                        Scale(a, "MaxSeekerDistance", m.MaxRange, cap);
                        Scale(a, "BurnTime", m.MaxRange);
                    }
                    Scale(a, "Damage", m.Dmg);
                    Scale(a, "PenetrationAtMinRange", m.Pen);
                    Scale(a, "PenetrationAtGroundRange", m.Pen);
                    Scale(a, "MuzzleVelocity", m.Speed);
                    Scale(a, "MaxSpeed", m.Speed);
                    Scale(a, "Acceleration", m.Speed);
                    Scale(a, "DispersionHorizontalRadius", m.Disp);
                    Scale(a, "DispersionVerticalRadius", m.Disp);
                    Scale(a, "DispersionMinimal", m.Disp);
                    Scale(a, "AimTimeMinOverride", m.Aim);
                    Scale(a, "AimTimeMaxOverride", m.Aim);
                    // Heavy warheads (Iskander, cruise missiles, bombs) are not stopped by a building: they pierce 70% of building cover.
                    families.TryGetValue(a.Id, out var fam);
                    if ((fam == Family.BALLISTIC || fam == Family.CRUISE || fam == Family.BOMB_DUMB || fam == Family.BOMB_GUIDED) && a.IgnoreCover < 0.7f)
                        SetJournaled(a, "IgnoreCover", 0.7f);
                }

                // ---- weapons: aim time follows the fastest-aiming family of their ammunitions
                int nWeap = 0;
                foreach (var w in Props.Rows(src.Weapons.GetAll()))
                {
                    double aim = 1;
                    if (weaponAmmo.TryGetValue(w.Id, out var ids))
                        foreach (var id in ids) if (families.TryGetValue(id, out var f)) aim = Math.Min(aim, mults[f].Aim);
                    if (aim < 1) { nWeap += Scale(w, "AimTimeMin", aim); Scale(w, "AimTimeMax", aim); }
                    if (_flash.Value > 0 && ids != null && ids.Any(id => families.TryGetValue(id, out var f) && IsHeavy(f))) Scale(w, "FlashPerShot", _flash.Value);
                }

                // ---- sensors by role: a sensor row shared by several roles follows the role group that uses it most
                // (the largest multiplier would give line infantry and tanks the recon optics of a few special forces on the same row)
                var votes = new Dictionary<int, Dictionary<double[], int>>();
                foreach (var su in Props.Rows(src.SensorUnits.GetAll()))
                {
                    if (!unitRole.TryGetValue(su.UnitId, out var role)) continue;
                    double[] g = role == ROLE_LRSAM || role == ROLE_SRSAM || role == ROLE_AAINF ? tables.SensAA
                              : role == ROLE_ATTACKHELI || role == ROLE_RECONHELI || role == ROLE_MULTIHELI || role == ROLE_DRONE || role == ROLE_RECONINF || role == ROLE_SNIPERS || role == ROLE_SPECFORCES ? tables.SensRecon
                              : role >= ROLE_PLANE_MIN && role <= ROLE_PLANE_MAX ? tables.SensPlane : tables.SensOther;
                    if (!votes.TryGetValue(su.SensorId, out var v)) votes[su.SensorId] = v = new Dictionary<double[], int>();
                    v[g] = v.GetValueOrDefault(g) + 1;
                }
                var sensMult = votes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);
                int nSens = 0;
                foreach (var s in Props.Rows(src.Sensors.GetAll()))
                {
                    var g = sensMult.TryGetValue(s.Id, out var v) ? v : tables.SensOther;
                    nSens += Scale(s, "OpticsGround", g[0]);
                    Scale(s, "OpticsLowAltitude", g[1]);
                    Scale(s, "OpticsHighAltitude", g[2]);
                }

                // ---- armour by role: tank / light vehicle / others ; shared armour takes the mildest reduction
                var armMult = new Dictionary<int, double[]>(); // heatFront, sides, rear, top
                foreach (var ua in Props.Rows(src.UnitArmors.GetAll()))
                {
                    if (!unitRole.TryGetValue(ua.UnitId, out var role)) continue;
                    double[] g = role == ROLE_TANK ? new[] { tables.TankHeatFront, tables.TankSides, tables.TankRear, tables.TankTop }
                              : role == ROLE_IFV || role == ROLE_APC || role == ROLE_LSV ? new[] { 1.0, tables.IfvSides, tables.IfvRear, tables.IfvTop }
                              : new[] { 1.0, 1.0, 1.0, 1.0 };
                    if (!armMult.TryGetValue(ua.ArmorId, out var cur)) armMult[ua.ArmorId] = g;
                    else for (int i = 0; i < 4; i++) cur[i] = Math.Max(cur[i], g[i]);
                }
                int nArm = 0;
                foreach (var kv in armMult)
                {
                    if (!src.Armors.TryGetById(kv.Key, out var r) || r == null) continue;
                    var g = kv.Value;
                    int changed = Scale(r, "HeatArmorFront", g[0]);
                    changed += Scale(r, "KinArmorSides", g[1]) + Scale(r, "HeatArmorSides", g[1]);
                    changed += Scale(r, "KinArmorRear", g[2]) + Scale(r, "HeatArmorRear", g[2]);
                    changed += Scale(r, "KinArmorTop", g[3]) + Scale(r, "HeatArmorTop", g[3]);
                    if (changed > 0) nArm++;
                }

                // ---- speeds: helicopters (mobility rows used by helicopter units) and plane fly presets
                var heliMob = new HashSet<int>();
                foreach (var up in Props.Rows(src.UnitPropulsions.GetAll()))
                    if (unitType.TryGetValue(up.UnitId, out var t) && (t & UT_HELICOPTER) != 0) heliMob.Add(up.MobilityId);
                int nMob = 0;
                foreach (var id in heliMob)
                    if (src.Mobility.TryGetById(id, out var mrow) && mrow != null) { nMob += Scale(mrow, "MaxSpeedRoad", tables.HeliSpeed); Scale(mrow, "MaxCrossCountrySpeed", tables.HeliSpeed); }
                int nPlane = 0;
                foreach (var p in Props.Rows(src.PlaneFlyPresets.GetAll()))
                {
                    nPlane += Scale(p, "MaxSpeed", tables.PlaneSpeed);
                    Scale(p, "CornerSpeed", tables.PlaneSpeed); Scale(p, "AfterburnSpeed", tables.PlaneSpeed); Scale(p, "AfterburnCornerSpeed", tables.PlaneSpeed);
                }

                // ---- real-life stats (before the hand-made override files, which keep the last word)
                string reelSummary = null;
                if (reel)
                {
                    // real-scale lots, in this order: validation, writes, read-back and [ECHELLE] summary of every lot (RealStats: all or
                    // nothing per lot, the ballistics lot with its gravity), then the engine values that go with the accepted lots
                    reelSummary = RealStats.Apply(src, EchellePortees.Value, db.CurrentSourceId);
                    ApplyEngineLots(db);
                    Log($"[VRAIES STATS] {reelSummary} ; {JournalCount} valeurs mémorisées pour la restauration");
                    // unit cards show true metres instead of the vanilla x2 (every loaded InfocardConfig: the arsenal card holds its own reference)
                    FixCardMultipliers(null, "base appliquée");
                    // the engine scales the bomb release offset with the explosion radius: bigger real radii must not throw bombs far from the aimed spot
                    try
                    {
                        var pc = GameConfig.Instance?.PlanesConfig;
                        bool explosionsOff = (OptionsInactives.Value ?? "").IndexOf("OPTION_EXPLOSIONS_REELLES", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (pc != null && !explosionsOff)
                        {
                            var before = pc.BombDropOffsetAmmoAoeMult;
                            SetJournaled(pc, "BombDropOffsetAmmoAoeMult", before * 0.58f);
                            Log($"[VRAIES STATS] décalage de largage des bombes : {before} -> {pc.BombDropOffsetAmmoAoeMult}");
                        }
                    }
                    catch (Exception e) { Log("[VRAIES STATS] décalage de largage des bombes inchangé : " + e.Message); }
                }

                // ---- exact per-row overrides (applied last, absolute values win)
                int nOv = 0;
                nOv += Overrides.ApplyRows(Path.Combine(Dir, "Munitions_override.csv"), Props.Rows(src.Ammunitions.GetAll()), r => r.Id, r => r.Name, cap);
                nOv += Overrides.ApplyRows(Path.Combine(Dir, "Blindages_override.csv"), Props.Rows(src.Armors.GetAll()), r => r.Id, r => r.Name, null);
                nOv += Overrides.ApplyRows(Path.Combine(Dir, "Capteurs_override.csv"), Props.Rows(src.Sensors.GetAll()), r => r.Id, r => r.Name, null);
                nOv += Overrides.ApplyRows(Path.Combine(Dir, "Armes_override.csv"), Props.Rows(src.Weapons.GetAll()), r => r.Id, r => r.Name, null);
                nOv += Overrides.ApplyRows(Path.Combine(Dir, "Mobilite_override.csv"), Props.Rows(src.Mobility.GetAll()), r => r.Id, r => r.Name, null);

                // ---- infantry stealth: recon / snipers / special forces are harder to spot
                int nStealth = 0;
                if (_furtivite.Value > 0)
                    foreach (var u in Props.Rows(src.Units.GetAll()))
                    {
                        int role = (int)u.Role;
                        if (role == ROLE_RECONINF || role == ROLE_SNIPERS || role == ROLE_SPECFORCES) nStealth += Scale(u, "Stealth", _furtivite.Value);
                        else if (_furtiviteVeh.Value > 0 && (((int)u.Type & UT_VEHICLE) != 0)) nStealth += Scale(u, "Stealth", _furtiviteVeh.Value);
                    }
                if (_couvert.Value > 0)
                {
                    var tts = GameConfig.Instance?.FogOfWarConfig?.TerrainTypeSettings;
                    for (int i = 0; i < (tts?.Length ?? 0); i++)
                    {
                        var t = tts[i];
                        int tt = t == null ? 0 : (int)t.TerrainType;
                        if (tt == 1 || tt == 2) nStealth += Scale(t, "Cover", _couvert.Value);   // Vegetation, Forest
                        else if (tt == 3 && _couvertBat.Value > 0) nStealth += Scale(t, "Cover", _couvertBat.Value);   // Buildings
                    }
                }

                // ---- vanilla "auto fire" order button for artillery: the game only shows it when an ability carries this flag
                int nAuto = 0;
                if (_artyAuto.Value)
                {
                    var artyUnits = new HashSet<int>();
                    foreach (var kv in unitRole) if (kv.Value == ROLE_ARTY || kv.Value == ROLE_MLRS || kv.Value == ROLE_MORTAR || kv.Value == ROLE_LAM) artyUnits.Add(kv.Key);
                    var abilityIds = new HashSet<int>();
                    foreach (var ua in Props.Rows(src.UnitAbilities.GetAll())) if (artyUnits.Contains(ua.UnitId)) abilityIds.Add(ua.AbilityId);
                    var modUnit = new Dictionary<int, int>();
                    foreach (var m in Props.Rows(src.Modifications.GetAll())) modUnit[m.Id] = m.UnitId;
                    foreach (var o in Props.Rows(src.Options.GetAll()))
                    {
                        if (!modUnit.TryGetValue(o.ModificationId, out var uid) || !artyUnits.Contains(uid)) continue;
                        foreach (var name in new[] { "Ability1Id", "Ability2Id", "Ability3Id" })
                        {
                            var aid = Props.Num(o, name);
                            if (aid.HasValue && aid.Value > 0) abilityIds.Add((int)aid.Value);
                        }
                    }
                    // abilities also used by non-artillery units (smoke, empty ability, APS...) are left untouched
                    var shared = new HashSet<int>();
                    foreach (var ua in Props.Rows(src.UnitAbilities.GetAll())) if (!artyUnits.Contains(ua.UnitId)) shared.Add(ua.AbilityId);
                    foreach (var o in Props.Rows(src.Options.GetAll()))
                    {
                        if (modUnit.TryGetValue(o.ModificationId, out var ou) && artyUnits.Contains(ou)) continue;
                        foreach (var name in new[] { "Ability1Id", "Ability2Id", "Ability3Id" })
                        {
                            var aid = Props.Num(o, name);
                            if (aid.HasValue && aid.Value > 0) shared.Add((int)aid.Value);
                        }
                    }
                    abilityIds.ExceptWith(shared);
                    foreach (var id in abilityIds)
                    {
                        if (!src.Abilities.TryGetById(id, out var ab) || ab == null || ab.IsArtilleryAutoFire) continue;
                        var p = Props.Get(ab.GetType(), "IsArtilleryAutoFire");
                        if (p != null) { Set(ab, p, true); nAuto++; }
                    }
                    Log($"[REALISME] tir automatique d'artillerie : {artyUnits.Count} unités, {nAuto} capacités marquées");
                }

                // ---- infantry inside buildings takes less damage (GameConfig is shared: journaled and restored like everything else).
                // The config values are the share of the damage a garrisoned squad still takes (floor + perSoldier x soldiers, vanilla
                // 0.18 / 0.02), so a protection P divides them: P = 1.5 gives 0.12 / 0.0133 (6 men: 0.30 -> 0.20 of the damage).
                int nBuild = 0;
                if (_batiments.Value > 0 && Math.Abs(_batiments.Value - 1) > 1e-6)
                {
                    var bc = GameConfig.Instance?.BuildingsConfig;
                    if (bc != null)
                    {
                        Log($"[REALISME] bâtiments avant : floor={bc.DamageModifierFloor} parSoldat={bc.DamageModifierPerSoldier}");
                        nBuild += Scale(bc, "DamageModifierFloor", 1.0 / _batiments.Value);
                        nBuild += Scale(bc, "DamageModifierPerSoldier", 1.0 / _batiments.Value);
                        Log($"[REALISME] bâtiments après : floor={bc.DamageModifierFloor} parSoldat={bc.DamageModifierPerSoldier} ({nBuild} valeurs, protection x{_batiments.Value.ToString("0.##", CultureInfo.InvariantCulture)} : dégâts reçus à l'intérieur plus faibles)");
                    }
                }

                // ---- infantry walks slower in forest (17/09/2026: 3 km/h from 4 km/h cross-country), with the real foot speeds only
                int nForest = 0;
                if (reel && !OptionOff("OPTION_VITESSE_INFANTERIE_REELLE"))
                {
                    try
                    {
                        var gcf = GameConfig.Instance;
                        if (gcf != null)
                        {
                            float before = gcf.InfantryForestSpeedMultiplier;
                            if (Math.Abs(before - InfantryForestSpeed) > 1e-4f)
                            {
                                nForest = SetJournaled(gcf, "InfantryForestSpeedMultiplier", InfantryForestSpeed);
                                Log($"[VRAIES STATS] vitesse de l'infanterie en forêt : multiplicateur {before} -> {gcf.InfantryForestSpeedMultiplier} (forêt globale {gcf.GlobalForestSpeedMultiplier}, non modifiée)");
                            }
                        }
                    }
                    catch (Exception e) { Log("[VRAIES STATS] vitesse de l'infanterie en forêt inchangée : " + e.Message); }
                }

                // ---- observation height from buildings (fog-of-war terrain settings; journaled like the rest)
                int nTerrain = 0;
                if (_hauteurBat.Value > 0 && Math.Abs(_hauteurBat.Value - 1) > 1e-6)
                {
                    var ts = GameConfig.Instance?.FogOfWarConfig?.TerrainTypeSettings;
                    for (int i = 0; i < (ts?.Length ?? 0); i++)
                    {
                        var t = ts[i];
                        if (t != null && (int)t.TerrainType == 3) nTerrain += Scale(t, "Height", _hauteurBat.Value);   // TerrainType.Buildings
                    }
                    if (nTerrain > 0) Log($"[REALISME] hauteur d'observation des bâtiments x{_hauteurBat.Value}");
                }

                _appliedSrc = src;
                AppliedSource = db.CurrentSourceId;
                // the game keeps its own copy of every row per loaded unit: carry the values applied above to those copies
                UnitCopies.AfterApply(db, src);
                AppliedSummary = (reel ? $"VRAIES STATS [{reelSummary}] " : "") + $"preset={preset} munitions={nAmmo} armes={nWeap} capteurs={nSens} blindages={nArm} hélicos={nMob} avions={nPlane} furtivité={nStealth} tirAuto={nAuto} bâtiments={nBuild} forêtInfanterie={nForest} cellules override={nOv} (OTHER={counts.GetValueOrDefault(Family.OTHER)})";
                Log($"[REALISME] appliqué à la base '{db.CurrentSourceId}' : {AppliedSummary} ; {JournalCount} valeurs journalisées");
                Log("[REALISME] familles : " + string.Join(", ", counts.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
            }
            catch (Exception e)
            {
                Mod.Log.Error("[REALISME] échec, annulation : " + e);
                Restore("erreur pendant l'application");
            }
        }

        internal static void SetValueForOverride(object row, PropertyInfo p, object v) => Set(row, p, v);

        /// Forest foot speed multiplier of the real stats: 0.75 x 4 km/h cross-country = 3 km/h (17/09/2026).
        const float InfantryForestSpeed = 1f;            // game value: foot speed in forest is left as the game sets it

        /// True when this real-stats option name is switched off (OptionsInactives, or a lot suspended by the combat watchdog).
        static bool OptionOff(string name) => OffKeys().Contains(name);

        const float GameGravity = 70f, RealGravity = 9.81f;
        const float EscapeRadiusReal = 400f, EscapeMeanLimit = 300f;

        /// Engine values of the real-scale lots, journaled under their lot (Realism.RestoreLot puts them back; the combat watchdog does it
        /// together with RealStats.RestoreLot for the ballistics lot).
        ///  - OPTION_BALISTIQUE_REELLE, once RealStats accepted it (muzzle velocities and G 70 -> 9.81 written): the missile gravity before
        ///    motor ignition follows G only when it holds the same absolute value as the game's G (a multiplier is left alone). Bomb gravity
        ///    multipliers stay as the game sets them; BattleSystemConstants is never written.
        ///  - OPTION_FUITE_ARTILLERIE_REELLE: AiConfig.AfterFireEscapeRadius -> 400 m (real batteries move 300-500 m after firing), only once
        ///    the measured mean move of AI batteries after firing is known and below 300 m.
        /// Only on the game's own database ("Resources default"), like the data lots.
        static void ApplyEngineLots(DataBaseService db)
        {
            var inv = CultureInfo.InvariantCulture;
            bool defaultDb = string.Equals(db?.CurrentSourceId, "Resources default", StringComparison.OrdinalIgnoreCase);
            if (!defaultDb) return;

            if (RealStats.LotAccepte(LotBallistics)) WriteMissileGravity();

            if (!OptionOff(LotArtilleryEscape))
            {
                float mean = -1f; int samples = 0; bool known = false;
                try { known = SanteCombat.ArtilleryEscapeMeasure(out mean, out samples); } catch { }
                if (!known) Log($"[ECHELLE] fuite de l'artillerie IA après tir : rayon du jeu gardé, en attente de mesure ({samples} déplacement(s) mesuré(s))");
                else if (mean >= EscapeMeanLimit) Log($"[ECHELLE] fuite de l'artillerie IA après tir : rayon du jeu gardé (déplacement moyen mesuré {mean.ToString("0", inv)} m sur {samples})");
                else WriteEscapeRadius(mean, samples);
            }
        }

        static void WriteMissileGravity()
        {
            var inv = CultureInfo.InvariantCulture;
            try
            {
                var bs = GameConfig.Instance?.BattleSystemSettings;
                if (bs == null) return;
                float g = bs.G, m0 = bs.MISSILE_GRAVITY_ACCELERATION_BEFORE_MOTOR_ACTIVATION;
                if (Math.Abs(g - RealGravity) > 0.01f)
                {
                    Mod.Log.Warning($"[ECHELLE] lot de balistique accepté mais pesanteur à {g.ToString(inv)} : gravité des missiles avant allumage laissée à {m0.ToString(inv)}");
                    return;
                }
                if (Math.Abs(m0 - GameGravity) > 0.5f)
                {
                    Log($"[ECHELLE] gravité des missiles avant allumage laissée à {m0.ToString(inv)} (pas la pesanteur absolue du jeu : multiplicateur ou réglage à part)");
                    return;
                }
                using (Lot(LotBallistics)) SetJournaled(bs, "MISSILE_GRAVITY_ACCELERATION_BEFORE_MOTOR_ACTIVATION", RealGravity);
                float m1 = bs.MISSILE_GRAVITY_ACCELERATION_BEFORE_MOTOR_ACTIVATION;
                if (Math.Abs(m1 - RealGravity) > 1e-3f)
                {
                    RestoreLot(LotBallistics, "gravité des missiles relue à " + m1.ToString(inv));
                    return;
                }
                Log($"[ECHELLE] gravité des missiles avant allumage : {m0.ToString(inv)} -> {m1.ToString(inv)} (lot {LotBallistics}) ; bombes inchangées " +
                    $"(gravité x{bs.BOMBS_GRAVITY_MULTIPLYER.ToString(inv)}, freinées x{bs.HIGH_DRAG_BOMBS_GRAVITY_MULTIPLYER.ToString(inv)})");
            }
            catch (Exception e)
            {
                RestoreLot(LotBallistics, "gravité des missiles non écrite");
                Mod.Log.Warning("[ECHELLE] gravité des missiles avant allumage non écrite : " + e.GetBaseException().Message);
            }
        }

        static void WriteEscapeRadius(float mean, int samples)
        {
            var inv = CultureInfo.InvariantCulture;
            try
            {
                var ai = GameConfig.Instance?.AiConfig;
                if (ai == null) return;
                float before = ai.AfterFireEscapeRadius;
                if (before >= EscapeRadiusReal) { Log($"[ECHELLE] fuite de l'artillerie IA après tir : rayon du jeu déjà {before.ToString(inv)} m, gardé"); return; }
                using (Lot(LotArtilleryEscape)) SetJournaled(ai, "AfterFireEscapeRadius", EscapeRadiusReal);
                Log($"[ECHELLE] fuite de l'artillerie IA après tir : rayon {before.ToString(inv)} -> {ai.AfterFireEscapeRadius.ToString(inv)} m (lot {LotArtilleryEscape}, déplacement moyen mesuré {mean.ToString("0", inv)} m sur {samples})");
            }
            catch (Exception e)
            {
                RestoreLot(LotArtilleryEscape, "rayon non écrit");
                Mod.Log.Warning("[ECHELLE] fuite de l'artillerie IA : rayon non écrit : " + e.GetBaseException().Message);
            }
        }

        /// Journaled write of a game config value by property name (restored with everything else at campaign end). Returns 1 if written.
        /// Real metres per raw game metre in real-stats mode (1, or the EchellePortees option): what every displayed distance is multiplied by.
        internal static float DisplayScale => Math.Max(1f, EchellePortees != null ? EchellePortees.Value : 1f);

        /// Real-stats mode: unit cards show true metres (vanilla multiplies raw metres by 2 on the card).
        /// extra == null: GameConfig's InfocardConfig and every loaded InfocardConfig asset; otherwise only that one (a card's own reference).
        internal static void FixCardMultipliers(Il2CppBrokenArrow.Client.Ecs.UI.Infocard.InfocardConfig extra, string where)
        {
            try
            {
                var list = new List<Il2CppBrokenArrow.Client.Ecs.UI.Infocard.InfocardConfig>();
                if (extra != null) list.Add(extra);
                else
                {
                    var main = GameConfig.Instance?.InfocardConfig;
                    if (main != null) list.Add(main);
                    foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppBrokenArrow.Client.Ecs.UI.Infocard.InfocardConfig>()))
                    {
                        var c = o?.TryCast<Il2CppBrokenArrow.Client.Ecs.UI.Infocard.InfocardConfig>();
                        if (c != null) list.Add(c);
                    }
                }
                var seen = new HashSet<IntPtr>();
                float target = DisplayScale;
                foreach (var ic in list)
                {
                    if (!seen.Add(ic.Pointer) || Math.Abs(ic.EffectiveRangeMultiplier - target) <= 1e-6) continue;
                    var before = ic.EffectiveRangeMultiplier;
                    SetJournaled(ic, "EffectiveRangeMultiplier", target);
                    Log($"[VRAIES STATS] fiche d'unité ({where}, '{ic.name}') : multiplicateur de portée {before} -> {ic.EffectiveRangeMultiplier}");
                }
            }
            catch (Exception e) { Log("[VRAIES STATS] portées affichées sur la fiche inchangées : " + e.Message); }
        }

        internal static int SetJournaled(object obj, string name, object value)
        {
            var p = Props.Get(obj.GetType(), name);
            if (p == null || !p.CanWrite) { Mod.Log.Warning($"[REALISME] propriété inconnue {name}"); return 0; }
            object v = value;
            if (p.PropertyType == typeof(float) && value is not float) v = Convert.ToSingle(value);
            Set(obj, p, v);
            return 1;
        }
        internal static double RealScale => RealModeOn ? 1 : _echelleReel.Value <= 0 ? 1 : _echelleReel.Value;

        static void WriteReferenceFiles()
        {
            foreach (var (name, realistic) in new[] { ("Multiplicateurs_realiste.csv", true), ("Multiplicateurs_modere.csv", false) })
            {
                var path = Path.Combine(Dir, name);
                var sb = new StringBuilder();
                sb.AppendLine("# Référence du preset (générée par le mod, non lue). Pour modifier : copier des lignes dans Multiplicateurs.csv (cellule vide = valeur du preset).");
                sb.AppendLine("Family;GroundRange;LowAltRange;HighAltRange;Damage;Penetration;Speed;Dispersion;AimTime");
                foreach (var kv in BuildPreset(realistic).OrderBy(k => k.Key.ToString()))
                    sb.AppendLine(string.Join(";", new object[] { kv.Key, kv.Value.G, kv.Value.L, kv.Value.H, kv.Value.Dmg, kv.Value.Pen, kv.Value.Speed, kv.Value.Disp, kv.Value.Aim }.Select(Props.Csv)));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            void Template(string name, string header, string comment)
            {
                var path = Path.Combine(Dir, name);
                if (File.Exists(path)) return;
                File.WriteAllText(path, "# " + comment + "\n# Syntaxe d'une cellule : 1200 = valeur absolue ; x1.5 = multiplie ; +300 = ajoute ; r8000 = mètres réels divisés par EchelleReel. Cellule vide = inchangé. Lignes # ignorées.\n" + header + "\n", new UTF8Encoding(true));
            }
            Template("Multiplicateurs.csv", "Family;GroundRange;LowAltRange;HighAltRange;Damage;Penetration;Speed;Dispersion;AimTime", "Multiplicateurs par famille qui remplacent ceux du preset (noms de familles dans Familles_*.csv).");
            Template("Familles_override.csv", "Id;Family", "Force la famille d'une munition (Id = colonne Id de Familles_*.csv).");
            Template("Munitions_override.csv", "Id;Name;GroundRange;LowAltRange;HighAltRange;Damage;PenetrationAtMinRange;PenetrationAtGroundRange;MaxSpeed;MuzzleVelocity", "Valeurs exactes par munition, appliquées en dernier (toute colonne de Ammunitions.csv est acceptée).");
            Template("Blindages_override.csv", "Id;Name;MaxHealthPoints;KinArmorFront;KinArmorSides;KinArmorRear;KinArmorTop;HeatArmorFront;HeatArmorSides;HeatArmorRear;HeatArmorTop", "Valeurs exactes par blindage (Id = Armors.csv).");
            Template("Capteurs_override.csv", "Id;Name;OpticsGround;OpticsLowAltitude;OpticsHighAltitude", "Valeurs exactes par capteur (Id = Sensors.csv).");
            Template("Armes_override.csv", "Id;Name;AimTimeMin;AimTimeMax;MagazineSize;MagazineReloadTimeMin;MagazineReloadTimeMax", "Valeurs exactes par arme (Id = Weapons.csv).");
            Template("Mobilite_override.csv", "Id;Name;MaxSpeedRoad;MaxCrossCountrySpeed;MaxSpeedWater", "Valeurs exactes par mobilité (Id = Mobility.csv).");
        }
    }

    /// CSV override files: multipliers per family, forced families, exact per-row values.
    static class Overrides
    {
        static Dictionary<int, Family> _forced;

        static List<string[]> ReadCsv(string path, out string[] header)
        {
            header = null;
            var rows = new List<string[]>();
            if (!File.Exists(path)) return rows;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var cells = line.Split(';').Select(c => c.Trim()).ToArray();
                if (header == null) header = cells; else rows.Add(cells);
            }
            return rows;
        }

        static bool TryNumber(string s, out double v)
        {
            s = s.Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        internal static Family? ForcedFamily(int ammoId)
        {
            if (_forced == null)
            {
                _forced = new Dictionary<int, Family>();
                var path = Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_realisme", "Familles_override.csv");
                foreach (var r in ReadCsv(path, out _))
                {
                    if (r.Length < 2 || !int.TryParse(r[0], out var id)) continue;
                    if (Enum.TryParse<Family>(r[1], true, out var f)) _forced[id] = f;
                    else Mod.Log.Warning($"[REALISME] Familles_override.csv : famille inconnue '{r[1]}' (Id {id})");
                }
            }
            return _forced.TryGetValue(ammoId, out var fam) ? fam : null;
        }

        internal static void ApplyMultiplierFile(string path, Dictionary<Family, Mult> mults)
        {
            var rows = ReadCsv(path, out var header);
            if (header == null || rows.Count == 0) return;
            int n = 0;
            foreach (var r in rows)
            {
                if (r.Length == 0 || !Enum.TryParse<Family>(r[0], true, out var fam)) { Mod.Log.Warning($"[REALISME] Multiplicateurs.csv : famille inconnue '{(r.Length > 0 ? r[0] : "")}'"); continue; }
                var m = mults[fam];
                for (int i = 1; i < Math.Min(r.Length, header.Length); i++)
                {
                    if (r[i].Length == 0 || !TryNumber(r[i], out var v)) continue;
                    switch (header[i].ToLowerInvariant())
                    {
                        case "groundrange": m.G = v; break;
                        case "lowaltrange": m.L = v; break;
                        case "highaltrange": m.H = v; break;
                        case "damage": m.Dmg = v; break;
                        case "penetration": m.Pen = v; break;
                        case "speed": m.Speed = v; break;
                        case "dispersion": m.Disp = v; break;
                        case "aimtime": m.Aim = v; break;
                        default: continue;
                    }
                    n++;
                }
            }
            if (n > 0) Mod.Log.Msg($"[REALISME] Multiplicateurs.csv : {n} cellule(s) appliquée(s)");
        }

        /// Applies exact values. Cell syntax: 1200 | x1.5 | +300 | r8000 (real metres / EchelleReel). Returns number of cells applied.
        internal static int ApplyRows<T>(string path, List<T> rows, Func<T, int> id, Func<T, string> name, double? cap)
        {
            var lines = ReadCsv(path, out var header);
            if (header == null || lines.Count == 0) return 0;
            var byId = new Dictionary<int, T>();
            foreach (var r in rows) byId[id(r)] = r;
            var props = new PropertyInfo[header.Length];
            for (int i = 0; i < header.Length; i++)
            {
                var h = header[i];
                if (h.Equals("Id", StringComparison.OrdinalIgnoreCase) || h.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
                props[i] = Props.Get(typeof(T), h);
                if (props[i] == null || !props[i].CanWrite) { Mod.Log.Warning($"[REALISME] {Path.GetFileName(path)} : colonne inconnue '{h}' ignorée"); props[i] = null; }
            }
            int n = 0, rejected = 0;
            foreach (var cells in lines)
            {
                if (cells.Length == 0 || !int.TryParse(cells[0], out var rid)) { rejected++; continue; }
                if (!byId.TryGetValue(rid, out var row)) { Mod.Log.Warning($"[REALISME] {Path.GetFileName(path)} : Id {rid} inconnu"); rejected++; continue; }
                for (int i = 1; i < Math.Min(cells.Length, header.Length); i++)
                {
                    var cell = cells[i];
                    if (cell.Length == 0) continue;
                    if (header[i].Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(cell, name(row), StringComparison.OrdinalIgnoreCase)) Mod.Log.Warning($"[REALISME] {Path.GetFileName(path)} : Id {rid} s'appelle '{name(row)}' et non '{cell}'");
                        continue;
                    }
                    var p = props[i];
                    if (p == null) continue;
                    double? curN = Props.Num(row, p.Name);
                    if (curN == null) continue;
                    double cur = curN.Value, v;
                    char c0 = char.ToLowerInvariant(cell[0]);
                    if (c0 == 'x' && TryNumber(cell.Substring(1), out var m)) v = cur * m;
                    else if (c0 == '+' && TryNumber(cell.Substring(1), out var add)) v = cur + add;
                    else if (c0 == 'r' && TryNumber(cell.Substring(1), out var real)) v = real / Realism.RealScale;
                    else if (TryNumber(cell, out var abs)) v = abs;
                    else { rejected++; continue; }
                    if (cap.HasValue && cap.Value > 0 && p.Name.EndsWith("Range") && v > cap.Value) v = Math.Max(cur, cap.Value);
                    object boxed = p.PropertyType == typeof(float) ? (float)v : p.PropertyType == typeof(int) ? (object)(int)Math.Round(v) : p.PropertyType == typeof(double) ? v : null;
                    if (boxed == null) { rejected++; continue; }
                    Realism.SetValueForOverride(row, p, boxed);
                    n++;
                }
            }
            Mod.Log.Msg($"[REALISME] {Path.GetFileName(path)} : {n} cellule(s) appliquée(s), {rejected} rejetée(s)");
            return n;
        }
    }

    // ---------------------------------------------------------------- triche (campagne, joueur local, sur demande)
    static class Cheats
    {
        internal static MelonPreferences_Entry<bool> Enabled;
        internal static MelonPreferences_Entry<int> UnitsPerCard;
        static MelonPreferences_Entry<int> _full, _moneyPerPress;
        static MelonPreferences_Entry<float> _toughFactor;

        /// Cheat kinds: 0 ammo, 1 fuel, 2 units per card, 3 tough units (toggles); 4 money, 5 heal and repair, 6 card cooldowns (one-shots).
        internal const int KindCount = 7;

        /// Hotkey preference of each kind (index = kind). The F4..F7 defaults are temporary: change them here only.
        static readonly (string Pref, string Key, string Desc)[] KeyTable =
        {
            ("ToucheMunitions", "F8", "Munitions et fumigènes illimités (le Trophy/APS n'est pas concerné). Nom de touche Unity : F1..F12, Home, PageUp, Numpad0, LeftCtrl... Vérifier qu'elle n'est pas déjà utilisée dans Options > Commandes"),
            ("ToucheCarburant", "F9", "Carburant illimité (même règle de nom de touche)"),
            ("ToucheUnites", "F10", "Unités par carte (même règle de nom de touche)"),
            ("ToucheResistance", "F4", "Unités résistantes : tes unités reçoivent moins de dégâts (même règle de nom de touche)"),
            ("ToucheArgent", "F5", "Argent en plus pour toi seul, à chaque appui (même règle de nom de touche)"),
            ("ToucheSoin", "F6", "Soin et réparation de toutes tes unités, à chaque appui (même règle de nom de touche)"),
            ("ToucheCartes", "F7", "Cartes rechargées : délais de redéploiement et de rachat de tes cartes remis à zéro (même règle de nom de touche)"),
        };
        static readonly MelonPreferences_Entry<string>[] _keys = new MelonPreferences_Entry<string>[KindCount];
        static readonly string[] _resolved = new string[KindCount];   // keys in effect this mission after the check against the game's bindings (null = not checked yet)
        static int _airWarn;

        internal static bool AmmoOn, FuelOn, UnitsOn, ToughOn;

        /// EntityId of the local player's alive units, read by the Resistance damage hooks (maybe off the main thread): always replaced as a whole, never mutated.
        internal static volatile int[] LocalEntityIds = Array.Empty<int>();
        /// ToughOn && Allowed, refreshed every 0.5 s on the main thread by FastTick.
        internal static volatile bool ToughArmed;
        static float _nextFast, _toughWarnNext, _healLaterAt;
        static List<LuaUnit> _healLater;

        static readonly HashSet<int> _ammoDone = new(), _fuelDone = new();
        static readonly Dictionary<(UnitCategoryType, int, int), (int count, int tcount)> _originalCounts = new();
        static readonly Dictionary<string, PropertyInfo> _keyProps = new();
        static readonly HashSet<string> _badKeys = new();
        // key controls of the live keyboard: name lookup and property read once per keyboard, so a frame only reads wasPressedThisFrame
        static readonly Dictionary<string, UnityEngine.InputSystem.Controls.KeyControl> _keyControls = new();
        static UnityEngine.InputSystem.Keyboard _kb;
        static IntPtr _kbPtr;
        static LuaMap _map;
        static int _ticks;
        static bool _wasInMission, _fuelScaleLogged, _captureWasOpen;

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Triche");
            Enabled = c.CreateEntry("TouchesTriche", true, description: Build.Desc("Autorise les touches de triche en campagne (toutes désactivées au début de chaque mission et à chaque redémarrage)"));
            UnitsPerCard = c.CreateEntry("UnitesParCarte", 50, description: Build.Desc("Nombre d'unités par carte quand la triche 'unités' est activée", "Unités par carte avec la triche des unités."));
            _full = c.CreateEntry("NiveauPlein", 100, description: Build.Desc("Niveau (%) écrit dans les unités quand la triche munitions/carburant est activée ou désactivée", "Niveau (%) écrit par les triches munitions et carburant."));
            _moneyPerPress = c.CreateEntry("ArgentParAppui", 5000, description: Build.Desc("Argent : points ajoutés pour toi seul à chaque appui", "Argent ajouté à chaque appui de la triche argent."));
            for (int k = 0; k < KindCount; k++)
                _keys[k] = c.CreateEntry(KeyTable[k].Pref, KeyTable[k].Key, description: Build.Desc(KeyTable[k].Desc, "Touche de la triche « " + LabelOf(k) + " » (F1..F12, Home, PageUp, Numpad0...)."));
            _toughFactor = c.CreateEntry("FacteurDegatsResistance", 0.25f, description: Build.Desc("Unités résistantes : dégâts reçus par tes unités multipliés par ce nombre (0.05 à 1 ; 0.25 = 4 fois moins de dégâts)", "Dégâts reçus avec la triche résistance (0.05 à 1)."));
        }

        internal static int MoneyPerPress
        {
            get { int v = 5000; try { v = _moneyPerPress?.Value ?? 5000; } catch { } return Math.Clamp(v, 1, 1_000_000); }
        }

        internal static float ToughFactor
        {
            get { float v = 0.25f; try { v = _toughFactor?.Value ?? 0.25f; } catch { } return float.IsNaN(v) ? 0.25f : Math.Clamp(v, 0.05f, 1f); }
        }

        static string FactorText => ToughFactor.ToString("0.##", CultureInfo.InvariantCulture);
        static int PerCard { get { try { return UnitsPerCard?.Value ?? 50; } catch { return 50; } } }

        /// Every cheat key with its label, in French (logs). A key left empty in the preferences is not listed.
        internal static string KeysHint() => KeysHint(Lang.FR);

        /// Every cheat key with its label in a language (on-screen hint).
        internal static string KeysHint(Lang l)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < KindCount; k++)
            {
                string key = KeyOf(k);
                if (key.Length == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(key).Append(" = ").Append(LabelOf(k, l));
            }
            return sb.ToString();
        }

        /// On-screen list of the cheat keys (French in the log, the game's language on screen).
        internal static void NotifyKeys()
        {
            var cur = Txt.Current;
            string fr = Txt.Format(Lang.FR, TxtKey.N_CHEAT_KEYS, KeysHint(Lang.FR));
            Mod.NotifyPair(fr, cur == Lang.FR ? fr : Txt.Format(cur, TxtKey.N_CHEAT_KEYS, KeysHint(cur)));
        }

        internal static void Reset()
        {
            bool was = AmmoOn || FuelOn || UnitsOn || ToughOn;
            AmmoOn = FuelOn = UnitsOn = ToughOn = false;
            Disarm();
            _ammoDone.Clear();
            _fuelDone.Clear();
            _originalCounts.Clear();
            _map = null;
            _ticks = 0;
            _nextFast = 0f;
            _healLater = null;
            try { Resistance.ResetSession(); } catch (Exception e) { Mod.Log.Warning("[TRICHE] résistance (remise à zéro) : " + e.Message); }
            try { AirAmmo.ResetSession(); } catch (Exception e) { Mod.Log.Warning("[TRICHE] avions (remise à zéro) : " + e.Message); }
            if (was) Mod.Log.Msg("[TRICHE] remise à zéro (nouvelle partie)");
        }

        /// Disarms the damage hooks at once (toggle off, mission left, mod switched off). ToughOn itself is not changed.
        internal static void Disarm()
        {
            ToughArmed = false;                      // disarmed first, then the snapshot is emptied
            LocalEntityIds = Array.Empty<int>();
        }

        /// Reads the live keyboard once per frame; a new keyboard (device changed or reconnected) drops the resolved controls.
        static bool RefreshKeyboard()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            IntPtr p = kb == null ? IntPtr.Zero : kb.Pointer;
            if (p != _kbPtr) { _kbPtr = p; _kb = kb; _keyControls.Clear(); }
            return _kb != null;
        }

        static bool Pressed(string key)
        {
            if (_kb == null || string.IsNullOrWhiteSpace(key)) return false;
            key = key.Trim();
            if (!_keyControls.TryGetValue(key, out var kc))
            {
                var p = KeyProp(key);
                kc = null;
                if (p != null) { try { kc = p.GetValue(_kb) as UnityEngine.InputSystem.Controls.KeyControl; } catch { kc = null; } }
                _keyControls[key] = kc;
            }
            return kc != null && kc.wasPressedThisFrame;
        }

        static PropertyInfo KeyProp(string key)
        {
            if (_keyProps.TryGetValue(key, out var p)) return p;
            p = null;
            if (Enum.TryParse<UnityEngine.InputSystem.Key>(key, true, out var k) && k != UnityEngine.InputSystem.Key.None)
                p = _kb.GetType().GetProperty(k + "Key", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null && _badKeys.Add(key)) Mod.Log.Warning($"Touche '{key}' inconnue - utiliser un nom de touche Unity (F8, Home, Numpad0, LeftCtrl, PageUp...)");
            _keyProps[key] = p;
            return p;
        }

        static bool InMission(out GameController gc, out int local)
        {
            gc = GameController._instance;
            local = -1;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (cp == null) return false;
            local = cp.UID;
            return true;
        }

        internal static void ReadHotkeys()
        {
            if (!Enabled.Value) return;
            bool inMission = InMission(out _, out _);
            if (!inMission) { _wasInMission = false; return; }
            if (!_wasInMission) { _wasInMission = true; Campaign.NoteSession(); ResolveKeys(); }
            bool capture = GameKeys.CaptureScreenOpen();
            if (capture) { _captureWasOpen = true; return; }               // the game's key-capture screen is open: keys belong to it
            if (_captureWasOpen)
            {
                // the player may have rebound a game key during the battle: check the cheat keys again (message only if one changed)
                _captureWasOpen = false;
                string before = KeysHint();
                ResolveKeys();
                string after = KeysHint();
                if (after != before) NotifyKeys();
            }

            // every key goes through Activate (SetAmmo / SetFuel / SetUnits / SetTough or a one-shot), so the Allowed check always applies
            if (!RefreshKeyboard()) return;
            for (int kind = 0; kind < KindCount; kind++)
            {
                if (!Pressed(KeyOf(kind))) continue;
                bool needsAllowed = !IsToggle(kind) || !IsOn(kind);   // switching a toggle OFF is always accepted
                if (needsAllowed && !Allowed(out var why)) { Mod.Notify(new TxtMsg(TxtKey.N_CHEAT_UNAVAILABLE, why)); continue; }
                bool ok = Activate(kind);
                Mod.Log.Msg($"[TRICHE] touche {KeyOf(kind)} : {LabelOf(kind)} -> " + (IsToggle(kind) ? (IsOn(kind) ? "ON" : "OFF") : (ok ? "fait" : "échec")));
            }
        }

        /// UI-agnostic cheat API (Settings > Mod tab). Cheats only in a SOLO campaign mission; switching one off is always accepted.
        /// reason: a CH_WHY_* text key (only meaningful when false is returned).
        internal static bool Allowed(out TxtKey reason)
        {
            reason = TxtKey.CH_WHY_UNREADABLE;
            try
            {
                if (!Mod.Actif.Value) { reason = TxtKey.CH_WHY_MOD_OFF; return false; }
                if (Campaign.MissionInerte) { reason = TxtKey.CH_WHY_TUTO; return false; }
                if (Mod.AntiCheatActive) { reason = TxtKey.CH_WHY_ANTICHEAT; return false; }
                if (!Campaign.InCampaign) { reason = TxtKey.CH_WHY_NOT_CAMPAIGN; return false; }
                if (GameController._instance?._GameSession_k__BackingField?.CurrentPlayer == null) { reason = TxtKey.CH_WHY_NO_MISSION; return false; }
                bool online = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage.IsNetwork || Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage.IsHost
                           || Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage.IsScenarioHost || Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage.IsScenarioSlave;
                string st = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus.Status.ToString();
                if (online || st != "NotConnected") { reason = TxtKey.CH_WHY_ONLINE; return false; }
                return true;
            }
            catch { reason = TxtKey.CH_WHY_UNREADABLE; return false; }
        }

        internal static bool SetAmmo(bool on)
        {
            if (on == AmmoOn) return true;
            if (on && !Allowed(out _)) return false;
            if (!InMission(out var gc, out var local)) { AmmoOn = on; _ammoDone.Clear(); return true; }
            try { Campaign.NoteSession(); } catch { }
            AmmoOn = on;
            if (on) { _ammoDone.Clear(); Mod.Notify(TxtKey.N_AMMO_ON); }
            else
            {
                // also the units that got the flag and left the player's control since (given away by the mission)
                SafeSet(gc, AllMyUnits(local).Union(_ammoDone).Distinct().ToList(), ammo: true, fuel: false, infinite: false);
                _ammoDone.Clear();
                Mod.Notify(TxtKey.N_AMMO_OFF);
            }
            Tick();
            return true;
        }

        internal static bool SetFuel(bool on)
        {
            if (on == FuelOn) return true;
            if (on && !Allowed(out _)) return false;
            if (!InMission(out var gc, out var local)) { FuelOn = on; _fuelDone.Clear(); return true; }
            try { Campaign.NoteSession(); } catch { }
            FuelOn = on;
            if (on) { _fuelDone.Clear(); Mod.Notify(TxtKey.N_FUEL_ON); }
            else
            {
                SafeSet(gc, AllMyUnits(local).Union(_fuelDone).Distinct().ToList(), ammo: false, fuel: true, infinite: false);
                _fuelDone.Clear();
                Mod.Notify(TxtKey.N_FUEL_OFF);
            }
            Tick();
            return true;
        }

        internal static bool SetUnits(bool on)
        {
            if (on == UnitsOn) return true;
            if (on && !Allowed(out _)) return false;
            if (!InMission(out _, out var local)) { UnitsOn = on; return true; }
            try { Campaign.NoteSession(); } catch { }
            UnitsOn = on;
            bool ok = false;
            try { ok = ApplyUnitsToLiveDeck(local); } catch (Exception e) { Mod.Log.Error("[TRICHE] unités : " + e.Message); }
            if (ok) { Mod.Notify(new TxtMsg(UnitsOn ? TxtKey.N_UNITS_ON : TxtKey.N_UNITS_OFF, UnitsPerCard.Value)); return true; }
            UnitsOn = !on;
            Mod.Notify(TxtKey.N_UNITS_NOT_READY);
            return false;
        }

        /// Tough units: damage received by the local player's units x ToughFactor (Harmony hooks in Resistance.cs, armed by FastTick).
        internal static bool SetTough(bool on)
        {
            if (on == ToughOn) return true;
            if (!on)
            {
                ToughOn = false;
                Disarm();
                Mod.Log.Msg("[TRICHE] résistance OFF");
                Mod.Notify(TxtKey.N_TOUGH_OFF);
                return true;
            }
            if (!Allowed(out _)) return false;
            try { Campaign.NoteSession(); } catch { }
            bool hooked = false;
            try { hooked = Resistance.EnsurePatched(); } catch (Exception e) { Mod.Log.Error("[TRICHE] résistance : " + e); }
            if (!hooked) { Mod.Log.Warning("[TRICHE] résistance refusée : aucun correctif de dégâts actif"); return false; }   // EnsurePatched already told the player why
            ToughOn = true;
            RefreshTough();
            _nextFast = UnityEngine.Time.realtimeSinceStartup + 0.5f;
            Mod.Log.Msg($"[TRICHE] résistance ON : facteur {FactorText}, {LocalEntityIds.Length} unité(s) suivie(s), armé={ToughArmed}");
            Mod.Notify(new TxtMsg(TxtKey.N_TOUGH_ON, FactorText));
            return true;
        }

        /// One-shot: MoneyPerPress points for the local player only (game economy bus first, Lua player as fallback). Never touches the mission cap (SetMaxMoney is global).
        internal static bool AddMoney()
        {
            if (!Allowed(out var why)) { Mod.Notify(new TxtMsg(TxtKey.N_CHEAT_UNAVAILABLE, why)); return false; }
            if (!InMission(out var gc, out var local)) { Mod.Notify(TxtKey.N_MONEY_NO_MISSION); return false; }
            try { Campaign.NoteSession(); } catch { }
            int amount = MoneyPerPress;
            var p = FindLuaPlayer(local);

            // path A: the game's own economy entry point (same bus as ammo / fuel), keyed by the local player UID
            bool invoked = false;
            try
            {
                var add = gc._GetEcsEventBus_k__BackingField?.Gameplay?.AddMoney;
                if (add == null) Mod.Log.Warning("[TRICHE] argent : AddMoney absent du bus, essai via le joueur Lua");
                else
                {
                    float busBefore = BusMoney(gc, local), luaBefore = LuaMoney(p);
                    add.Invoke(local, (float)amount);
                    invoked = true;
                    float busAfter = BusMoney(gc, local), luaAfter = LuaMoney(p);
                    Mod.Log.Msg($"[TRICHE] argent (bus) : joueur {local} demandé +{amount} ; bus avant={MoneyText(busBefore)} après={MoneyText(busAfter)} ; joueur Lua avant={MoneyText(luaBefore)} après={MoneyText(luaAfter)}");
                    float gained = MaxKnown(Diff(busBefore, busAfter), Diff(luaBefore, luaAfter));
                    float total = float.IsNaN(busAfter) ? luaAfter : busAfter;
                    if (float.IsNaN(gained)) { Mod.Notify(new TxtMsg(TxtKey.N_MONEY_REQ_UNREADABLE, amount)); return true; }
                    if (gained >= amount - Math.Max(1f, amount * 0.01f)) { Mod.Notify(new TxtMsg(TxtKey.N_MONEY_ADDED, amount, MoneyText(total))); return true; }
                    if (gained > 0.5f) { Mod.Notify(new TxtMsg(TxtKey.N_MONEY_CAPPED, MoneyText(gained), MoneyText(total))); return true; }
                    Mod.Log.Warning("[TRICHE] argent : aucun effet par le bus, essai via le joueur Lua");
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warning("[TRICHE] argent (bus) : " + e.Message);
                if (invoked) { Mod.Notify(new TxtMsg(TxtKey.N_MONEY_REQ_UNVERIFIED, amount)); return true; }   // never added twice
            }

            // path B: the Lua player entry with the same UID
            if (p == null)
            {
                Mod.Log.Warning($"[TRICHE] argent : joueur {local} introuvable dans la liste des joueurs Lua");
                Mod.Notify(TxtKey.N_MONEY_NO_PLAYER);
                return false;
            }
            try
            {
                int b = p.GetMoney();
                p.AddMoney(amount);
                int a = p.GetMoney();
                Mod.Log.Msg($"[TRICHE] argent (joueur Lua) : joueur {local} demandé +{amount} ; avant={b} après={a}");
                if (a - b >= amount - Math.Max(1, amount / 100)) { Mod.Notify(new TxtMsg(TxtKey.N_MONEY_ADDED, amount, a)); return true; }
                if (a > b) { Mod.Notify(new TxtMsg(TxtKey.N_MONEY_CAPPED, a - b, a)); return true; }
                Mod.Notify(new TxtMsg(TxtKey.N_MONEY_NO_EFFECT, a));
                return false;
            }
            catch (Exception e) { Mod.Log.Error("[TRICHE] argent (joueur Lua) : " + e); Mod.Notify(TxtKey.N_MONEY_FAILED); return false; }
        }

        static LuaPlayer FindLuaPlayer(int local)
        {
            try
            {
                var all = new LuaStorage(null, null).GetAllPlayers();
                for (int i = 0; i < (all?.Length ?? 0); i++)
                {
                    var p = all[i];
                    if (p != null && p.UID == local) return p;
                }
            }
            catch (Exception e) { Mod.Log.Warning("[TRICHE] argent : liste des joueurs illisible : " + e.Message); }
            return null;
        }

        static float BusMoney(GameController gc, int local)
        {
            try
            {
                var get = gc?._GetEcsEventBus_k__BackingField?.Gameplay?.GetMoney;
                return get != null ? get.Invoke(local) : float.NaN;
            }
            catch (Exception e) { Mod.Log.Warning("[TRICHE] argent : lecture par le bus impossible : " + e.Message); return float.NaN; }
        }

        static float LuaMoney(LuaPlayer p)
        {
            try { return p != null ? p.GetMoney() : float.NaN; }
            catch (Exception e) { Mod.Log.Warning("[TRICHE] argent : lecture via le joueur Lua impossible : " + e.Message); return float.NaN; }
        }

        static float Diff(float before, float after) => float.IsNaN(before) || float.IsNaN(after) ? float.NaN : after - before;
        static float MaxKnown(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Max(a, b);
        static string MoneyText(float v) => float.IsNaN(v) ? "?" : Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

        /// One-shot (experimental): full health and repair of every alive unit of the local player, through the mission-editor "set health" event.
        internal static bool HealAll()
        {
            if (!Allowed(out var why)) { Mod.Notify(new TxtMsg(TxtKey.N_CHEAT_UNAVAILABLE, why)); return false; }
            if (!InMission(out var gc, out var local)) { Mod.Notify(TxtKey.N_HEAL_NO_MISSION); return false; }
            try { Campaign.NoteSession(); } catch { }
            List<LuaUnit> units;
            try { units = MyLuaUnits(local); }
            catch (Exception e) { Mod.Log.Error("[TRICHE] soin : liste des unités : " + e); Mod.Notify(TxtKey.N_HEAL_LIST_UNREADABLE); return false; }
            // the health node also writes the Immortal flag (sent false: F6 never makes a unit immortal). The unit's current flag is not readable
            // from the Lua bridge, so a unit a mission made immortal would lose it: rare, logged below.
            var mortal = new List<int>();
            var immortal = new List<int>();
            int unreadable = 0;
            foreach (var u in units)
            {
                try { mortal.Add(u.UID); } catch { unreadable++; }
            }
            int count = mortal.Count + immortal.Count;
            if (count == 0) { Mod.Notify(unreadable > 0 ? TxtKey.N_HEAL_STATE_UNREADABLE : TxtKey.N_HEAL_NO_UNITS); return false; }
            Mod.Log.Msg($"[TRICHE] soin (expérimental) : {count} unité(s) du joueur {local} ({immortal.Count} immortelle(s) gardée(s) immortelle(s), {unreadable} illisible(s) ignorée(s)), santé moyenne avant = {AverageHealth(units)}");
            try
            {
                var repair = gc._GetEcsEventBus_k__BackingField?.Gameplay?.DamageRepairUnit;
                if (repair == null)
                {
                    Mod.Log.Warning("[TRICHE] soin : DamageRepairUnit absent du bus");
                    Mod.Notify(TxtKey.N_HEAL_UNAVAILABLE);
                    return false;
                }
                foreach (var (list, flag) in new[] { (mortal, false), (immortal, true) })
                {
                    if (list.Count == 0) continue;
                    var data = new Il2CppBrokenArrow.Shared.Ecs.MissionEditor.NodeHealthUnitData();
                    data.UnitUids = ToIl2Cpp(list);
                    data.SetHealth = 100f;
                    data.AddHealth = 0f;
                    data.Immortal = flag;
                    // negative crit values repair critical damage (0 leaves it unchanged)
                    data.OpticsCrit = -10;
                    data.AimingCrit = -10;
                    data.ReloadCrit = -10;
                    data.MobilityCrit = -10;
                    repair.Invoke(data);
                }
            }
            catch (Exception e) { Mod.Log.Error("[TRICHE] soin : " + e); Mod.Notify(TxtKey.N_HEAL_FAILED); return false; }
            Mod.Log.Msg($"[TRICHE] soin (expérimental) : envoyé SetHealth=100 AddHealth=0 crits=-10 ; santé moyenne juste après = {AverageHealth(units)} (peut ne changer qu'à l'image suivante, nouvelle mesure dans 1 s)");
            _healLater = units;
            _healLaterAt = UnityEngine.Time.realtimeSinceStartup + 1f;
            Mod.Notify(new TxtMsg(TxtKey.N_HEAL_DONE, count));
            return true;
        }

        static string AverageHealth(List<LuaUnit> units)
        {
            long sum = 0;
            int n = 0;
            foreach (var u in units) { try { sum += u.GetHealPercentage(); n++; } catch { } }
            return n == 0 ? "?" : (sum / (double)n).ToString("0.#", CultureInfo.InvariantCulture) + " % (" + n + " unité(s) lue(s))";
        }

        /// One-shot: redeploy / repurchase cooldowns of the local player's cards back to zero (RefundDelayService, per player id).
        internal static bool ResetCardCooldowns()
        {
            if (!Allowed(out var why)) { Mod.Notify(new TxtMsg(TxtKey.N_CHEAT_UNAVAILABLE, why)); return false; }
            if (!InMission(out _, out var local)) { Mod.Notify(TxtKey.N_CARDS_NO_MISSION); return false; }
            try { Campaign.NoteSession(); } catch { }
            var svc = Mod.Svc<Il2CppBrokenArrow.Client.Ecs.Economy.RefundDelayService>();
            if (svc == null)
            {
                Mod.Log.Warning("[TRICHE] cartes : RefundDelayService introuvable dans la session");
                Mod.Notify(TxtKey.N_CARDS_UNAVAILABLE);
                return false;
            }
            string before = RefundText(svc, local);
            try { svc.ResetRefundTime(local); }
            catch (Exception e) { Mod.Log.Error("[TRICHE] cartes : " + e); Mod.Notify(TxtKey.N_CARDS_FAILED); return false; }
            Mod.Log.Msg($"[TRICHE] cartes : ResetRefundTime({local}) ; attentes avant : {before} ; après : {RefundText(svc, local)}");
            Mod.Notify(TxtKey.N_CARDS_DONE);
            return true;
        }

        /// Diagnostic only: the local player's refund timers (count, then the first CurrentTime/RepurchaseDelay pairs).
        static string RefundText(Il2CppBrokenArrow.Client.Ecs.Economy.RefundDelayService svc, int local)
        {
            try
            {
                var list = svc._refundDataList;
                if (list == null) return "liste absente";
                var sb = new StringBuilder();
                int mine = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    if (d == null || d.OwnerPlayerID != local) continue;
                    if (++mine <= 3) sb.Append(' ').Append(d.CurrentTime.ToString("0.#", CultureInfo.InvariantCulture)).Append('/').Append(d.RepurchaseDelay.ToString("0.#", CultureInfo.InvariantCulture));
                }
                return $"{mine} à toi sur {list.Count}" + (mine > 0 ? " (temps/délai :" + sb + ")" : "");
            }
            catch (Exception e) { return "illisible (" + e.Message + ")"; }
        }

        // ---- kind API shared by the hotkeys and the Settings > Mod tab
        internal static bool IsToggle(int kind) => kind <= 3;

        internal static bool IsOn(int kind)
        {
            switch (kind)
            {
                case 0: return AmmoOn;
                case 1: return FuelOn;
                case 2: return UnitsOn;
                case 3: return ToughOn;
                default: return false;
            }
        }

        /// Toggles a toggle cheat (SetX(!IsOn)) or fires a one-shot. True when applied.
        internal static bool Activate(int kind)
        {
            try
            {
                switch (kind)
                {
                    case 0: return SetAmmo(!AmmoOn);
                    case 1: return SetFuel(!FuelOn);
                    case 2: return SetUnits(!UnitsOn);
                    case 3: return SetTough(!ToughOn);
                    case 4: return AddMoney();
                    case 5: return HealAll();
                    case 6: return ResetCardCooldowns();
                }
            }
            catch (Exception e) { Mod.Log.Error($"[TRICHE] {LabelOf(kind)} : {e}"); }
            return false;
        }

        /// Current key name from the preferences ("" = no key).
        internal static string KeyOf(int kind)
        {
            if (kind < 0 || kind >= KindCount) return "";
            var r = _resolved[kind];
            return !string.IsNullOrEmpty(r) ? r : PrefKey(kind);
        }

        /// Key written in the preferences ("" = no key), before the check against the game's own bindings.
        static string PrefKey(int kind)
        {
            if (kind < 0 || kind >= KindCount) return "";
            try { var e = _keys[kind]; if (e != null) return (e.Value ?? "").Trim(); } catch { }
            return KeyTable[kind].Key;
        }

        /// At each mission start: every cheat key is checked against the keys the game uses (GameKeys) and replaced by a free one when needed.
        static void ResolveKeys()
        {
            try
            {
                GameKeys.Rebuild();
                GameKeys.LogMapOnce();
                var assigned = new List<string>();
                for (int k = 0; k < KindCount; k++)
                {
                    string wanted = PrefKey(k);
                    string key = string.IsNullOrEmpty(wanted) || !GameKeys.Ready ? wanted : GameKeys.Resolve(wanted, LabelOf(k), assigned);
                    _resolved[k] = key;
                    if (!string.IsNullOrEmpty(key)) assigned.Add(key);
                }
                Mod.Log.Msg("[TOUCHES] touches de triche retenues : " + KeysHint());
            }
            catch (Exception e) { Mod.Log.Warning("[TOUCHES] vérification des touches impossible : " + e.Message); }
        }

        /// Cheat label in French (logs, key check, preference texts).
        internal static string LabelOf(int kind) => LabelOf(kind, Lang.FR);

        internal static string LabelOf(int kind, Lang l)
        {
            switch (kind)
            {
                case 0: return Txt.Get(l, TxtKey.CH_LABEL_AMMO);
                case 1: return Txt.Get(l, TxtKey.CH_LABEL_FUEL);
                case 2: return Txt.Format(l, TxtKey.CH_LABEL_UNITS, PerCard);
                case 3: return Txt.Get(l, TxtKey.CH_LABEL_TOUGH);
                case 4: return Txt.Format(l, TxtKey.CH_LABEL_MONEY, MoneyPerPress);
                case 5: return Txt.Get(l, TxtKey.CH_LABEL_HEAL);
                case 6: return Txt.Get(l, TxtKey.CH_LABEL_CARDS);
                default: return "?";
            }
        }

        internal static string DescOf(int kind, Lang l)
        {
            switch (kind)
            {
                case 0: return Txt.Get(l, TxtKey.CH_DESC_AMMO);
                case 1: return Txt.Get(l, TxtKey.CH_DESC_FUEL);
                case 2: return Txt.Format(l, TxtKey.CH_DESC_UNITS, PerCard);
                case 3: return Txt.Format(l, TxtKey.CH_DESC_TOUGH, Math.Round(ToughFactor * 100f).ToString("0", CultureInfo.InvariantCulture));
                case 4: return Txt.Format(l, TxtKey.CH_DESC_MONEY, MoneyPerPress);
                case 5: return Txt.Get(l, TxtKey.CH_DESC_HEAL);
                case 6: return Txt.Get(l, TxtKey.CH_DESC_CARDS);
                default: return "";
            }
        }

        /// Every frame in campaign (Mod.OnUpdate): every 0.5 s refreshes the tough-units snapshot, then the Resistance counters.
        internal static void FastTick()
        {
            // aircraft ammo has its own timers (0.5 s refill, 2 s list, 10 s diagnostic) and only acts while AmmoOn && Allowed
            try { AirAmmo.Frame(AmmoOn); } catch (Exception e) { if (_airWarn++ < 3) Mod.Log.Warning("[TRICHE] avions : " + e.Message); }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextFast) return;
            _nextFast = now + 0.5f;
            RefreshTough();
            if (_healLater != null && now >= _healLaterAt)
            {
                var units = _healLater;
                _healLater = null;
                Mod.Log.Msg($"[TRICHE] soin (expérimental) : santé moyenne 1 s après = {AverageHealth(units)}");
            }
            try { Resistance.ReportIfDue(); } catch (Exception e) { Mod.Log.Warning("[TRICHE] résistance (compteurs) : " + e.Message); }
        }

        /// Armed only while ToughOn && Allowed; the EntityId snapshot is published before arming.
        static void RefreshTough()
        {
            if (!ToughOn || !Allowed(out _) || !Resistance.Usable) { Disarm(); return; }
            if (!InMission(out _, out var local)) { Disarm(); return; }
            var ids = new List<int>();
            try
            {
                foreach (var u in MyLuaUnits(local))
                {
                    try { ids.Add(u.Entity.EntityId); } catch { }
                }
            }
            catch (Exception e)
            {
                Disarm();
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now >= _toughWarnNext) { _toughWarnNext = now + 30f; Mod.Log.Warning("[TRICHE] résistance : liste de tes unités illisible : " + e.Message); }
                return;
            }
            LocalEntityIds = ids.ToArray();
            ToughArmed = true;
        }

        /// Alive units owned by the local player (the owner check is what enforces locality).
        static List<LuaUnit> MyLuaUnits(int local)
        {
            var res = new List<LuaUnit>();
            if (local < 0) return res;
            _map ??= new LuaMap();
            var units = _map.GetUnits(UnityEngine.Vector3.zero, 1_000_000f, -1, local);
            for (int i = 0; i < (units?.Length ?? 0); i++)
            {
                var u = units[i];
                if (u != null && u.IsAlive() && u.GetOwnerPlayerUID() == local) res.Add(u);
            }
            return res;
        }

        internal static List<int> AllMyUnits(int local)
        {
            var res = new List<int>();
            foreach (var u in MyLuaUnits(local)) res.Add(u.UID);
            return res;
        }

        internal static Il2CppSystem.Collections.Generic.IReadOnlyList<int> ToIl2Cpp(List<int> uids)
        {
            var list = new Il2CppSystem.Collections.Generic.List<int>();
            foreach (var u in uids) list.Add(u);
            return new Il2CppSystem.Collections.Generic.IReadOnlyList<int>(list.Pointer);
        }

        /// Writes ammo / fuel level + infinity flag; on failure the cheat is switched off and the player is told.
        static void SafeSet(GameController gc, List<int> uids, bool ammo, bool fuel, bool infinite)
        {
            if (uids.Count == 0) return;
            var bus = gc?._GetEcsEventBus_k__BackingField?.Gameplay;
            if (bus == null) return;
            var ro = ToIl2Cpp(uids);
            float full = Math.Max(1, _full.Value);
            if (ammo)
            {
                try
                {
                    var filter = new AmmoFilterData();
                    filter.Type = NodeAmmoType.Any;
                    filter.SpecificAmmoNameFilter = "";
                    bus.SetUnitsAmmo?.Invoke(ro, full, filter, infinite);
                }
                catch (Exception e) { AmmoOn = false; Mod.Log.Error("[TRICHE] munitions : " + e); Mod.Notify(TxtKey.N_AMMO_UNAVAILABLE); }
            }
            if (fuel)
            {
                try
                {
                    if (!_fuelScaleLogged)
                    {
                        _fuelScaleLogged = true;
                        try { var t = bus.GetUnitsFuel?.Invoke(ro); if (t != null) Mod.Log.Msg($"[TRICHE] niveau de carburant lu avant écriture : {t.Item1} (infini={t.Item2})"); } catch { }
                    }
                    bus.SetUnitsFuel?.Invoke(ro, full, infinite);
                }
                catch (Exception e) { FuelOn = false; Mod.Log.Error("[TRICHE] carburant : " + e); Mod.Notify(TxtKey.N_FUEL_UNAVAILABLE); }
            }
        }

        /// Every 2 s: gives infinite ammo / fuel to units that appeared since the last pass (and re-applies every 10 s).
        internal static void Tick()
        {
            if ((!AmmoOn && !FuelOn) || !Allowed(out _)) return;
            if (!InMission(out var gc, out var local)) return;
            List<int> mine;
            try { mine = AllMyUnits(local); }
            catch (Exception e)
            {
                AmmoOn = FuelOn = false;
                Mod.Log.Error("[TRICHE] liste des unités : " + e);
                Mod.Notify(TxtKey.N_AMMOFUEL_UNAVAILABLE);
                return;
            }
            // a unit that left the player's list (dead, back to base, given away by the mission) loses the infinite flag, then is forgotten
            var mineSet = new HashSet<int>(mine);
            var goneAmmo = _ammoDone.Where(id => !mineSet.Contains(id)).ToList();
            if (goneAmmo.Count > 0) { SafeSet(gc, goneAmmo, ammo: true, fuel: false, infinite: false); _ammoDone.ExceptWith(goneAmmo); }
            var goneFuel = _fuelDone.Where(id => !mineSet.Contains(id)).ToList();
            if (goneFuel.Count > 0) { SafeSet(gc, goneFuel, ammo: false, fuel: true, infinite: false); _fuelDone.ExceptWith(goneFuel); }
            bool reapplyAll = ++_ticks % 5 == 0;                               // every 10 s
            if (AmmoOn) SafeSet(gc, mine.Where(u => _ammoDone.Add(u) || reapplyAll).ToList(), ammo: true, fuel: false, infinite: true);
            if (FuelOn) SafeSet(gc, mine.Where(u => _fuelDone.Add(u) || reapplyAll).ToList(), ammo: false, fuel: true, infinite: true);
        }

        internal static void ForgetOriginals() => _originalCounts.Clear();

        /// ON: every card gets at least UnitsPerCard units (original counts remembered). OFF: cards never exceed their original count.
        internal static void ApplyUnitsPerCard(IDeckDataModel deck)
        {
            int n = UnitsPerCard.Value;
            if (deck == null || n <= 0) return;
            foreach (var cat in Mod.Categories)
            {
                var arr = Deck.GetCategorySlots(deck, cat);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    var s = arr[i];
                    if (s == null || s.UnitID <= 0) continue;
                    var key = (cat, s.UnitID, s.TransportID);
                    if (UnitsOn)
                    {
                        if (!_originalCounts.ContainsKey(key)) _originalCounts[key] = (s.Count, s.TransportCount);
                        if (s.Count < n) s.Count = n;
                        if (s.TransportID > 0 && s.TransportCount < n) s.TransportCount = n;
                    }
                    else if (_originalCounts.TryGetValue(key, out var orig))
                    {
                        s.Count = Math.Min(s.Count, orig.count);
                        s.TransportCount = Math.Min(s.TransportCount, orig.tcount);
                    }
                }
            }
            if (!UnitsOn) _originalCounts.Clear();
        }

        static bool ApplyUnitsToLiveDeck(int local)
        {
            if (local < 0 || !SharedPlayerDeck.ContainsDeck(local)) return false;
            if (!SharedPlayerDeck.TryGetDeck(local, out IDeckDataModel cur) || cur == null) return false;
            bool pendingMerge = Campaign.UserDecks.Count > 0 && !Campaign.Merged.Contains(local);
            var clone = pendingMerge ? Campaign.BuildMerged(cur, out _) : Deck.Clone(cur);
            var cloneI = new IDeckDataModel(clone.Pointer);
            ApplyUnitsPerCard(cloneI);
            Campaign.CommitDeck(local, clone);
            Mod.Log.Msg($"[TRICHE] unités par carte {(UnitsOn ? "ON" : "OFF")}: {Mod.DeckStr(cloneI)}");
            return true;
        }
    }

    // ---------------------------------------------------------------- base de données actuelle en campagne (unités DLC disponibles)
    static class CurrentDb
    {
        internal static MelonPreferences_Entry<bool> Enabled;
        const string Default = "Resources default";
        static bool _routesLogged;

        internal static bool IsDefault(DataBaseService db) => db != null && string.Equals(db.CurrentSourceId, Default, StringComparison.OrdinalIgnoreCase);

        /// Called by the ApplyForScenario postfix and by the 2-s safety poll. True when the mission now runs on the default database.
        internal static DataBaseSourceData FailedSrc;   // a source whose forced reload failed is never retried

        static void Pin(DataBaseService db)
        {
            Campaign.MissionDbSource = db.RawAccess;   // held reference
            Divisions.SafeInject(db);
            if (Realism.ShouldApply(db, Campaign.DbLoadsThisMission)) Realism.Apply(db);
        }

        /// Called by the ApplyForScenario postfix and by the 2-s safety poll. True when the mission now runs on the default database.
        internal static bool ForceIfNeeded(DataBaseService db, string why)
        {
            if (!Mod.Actif.Value || !Enabled.Value || db?.RawAccess == null || !Campaign.Refresh() || Campaign.MissionUid == null) return false;
            if (Campaign.MissionInerte) return false;                   // mission played without the mod: it keeps the database it loaded
            if (IsDefault(db))
            {
                if (Campaign.MissionDbSource == null || Campaign.MissionDbSource.Pointer != db.RawAccess.Pointer) Pin(db);
                return true;
            }
            var attempted = db.RawAccess;
            if (FailedSrc != null && FailedSrc.Pointer == attempted.Pointer) return false;
            Mod.Log.Msg($"[BASE] la mission a chargé la base '{db.CurrentSourceId}' -> passage à la base actuelle ({why})");
            Realism.Restore("rechargement de la base (campagne)");
            try { DataBaseService.ReloadByDefault(); }
            catch (Exception e) { FailedSrc = attempted; Mod.Log.Error($"[BASE] ReloadByDefault ({why}) : {e}"); KeepMissionDb(); return false; }
            db = DataBaseService._instance;
            Mod.Log.Msg($"[BASE] base actuelle forcée ({why}) -> source='{db?.CurrentSourceId}' chargée={db?.IsLoaded}");
            if (db == null || !db.IsLoaded || !IsDefault(db)) { FailedSrc = attempted; Mod.Log.Error("[BASE] échec, base de mission conservée"); KeepMissionDb(); return false; }
            try { db.ReloadDepended(); } catch (Exception e) { Mod.Log.Warning("[BASE] ReloadDepended : " + e.Message); }
            Pin(db);
            return true;
        }

        /// The forced reload failed: the mission keeps the database it has, and still gets realism.
        static void KeepMissionDb()
        {
            var cur = DataBaseService._instance;
            if (cur != null && cur.IsLoaded && cur.RawAccess != null) Pin(cur);
        }

        /// Safety net from OnUpdate (every 2 s in campaign): the postfix may not fire on this IL2CPP build.
        internal static void Poll()
        {
            if (!Enabled.Value || Campaign.MissionUid == null || Campaign.MissionInerte) return;
            var db = DataBaseService._instance;
            if (db == null || !db.IsLoaded || db.RawAccess == null) return;
            if (Campaign.MissionDbSource != null && db.RawAccess.Pointer == Campaign.MissionDbSource.Pointer) return;
            if (!IsDefault(db)) ForceIfNeeded(db, "secours, patch non vu");
            else if (Campaign.Ctx()?.CurrentPlayer != null) ForceIfNeeded(db, "base déjà actuelle");   // a live battle only: never pin the menu database
        }

        /// Logs the game's database routing table once (which profile/version each route selects).
        internal static void LogRoutes()
        {
            if (_routesLogged) return;
            try
            {
                if (!ConfigResolver.IsReady) return;
                _routesLogged = true;
                var sel = ConfigResolver._contextController?.TryCast<ConfigProfileSelector>();
                var routing = sel?._routing;
                if (routing == null) { Mod.Log.Msg("[BASE] table de routage indisponible"); return; }
                var def = routing._defaultProfile;
                Mod.Log.Msg($"[BASE] profil par défaut '{def?._id}' base={(def?._dataBase != null ? def._dataBase.name : "aucune")}");
                var routes = routing._routes;
                for (int i = 0; i < (routes?.Length ?? 0); i++)
                {
                    var r = routes[i]; var p = r?._profile;
                    Mod.Log.Msg($"[BASE] route prio={r?._priority} mode={r?._gameMode} version='{r?._gameVersion}' campagne='{r?._campaignUid}' mission='{r?._missionUid}' scénario='{r?._scenarioName}' -> profil '{p?._id}' base={(p?._dataBase != null ? p._dataBase.name : "aucune")}");
                }
            }
            catch (Exception e) { _routesLogged = true; Mod.Log.Msg("[BASE] table de routage illisible: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(ProfileDataBaseResolver), nameof(ProfileDataBaseResolver.ApplyForScenario))]
    static class Patch_ApplyForScenario
    {
        static void Postfix(ProfileDataBaseResolver __instance, string scenarioFolder) => Guard.Run("BaseActuelle", () =>
        {
            var db = __instance._dataBaseService ?? DataBaseService._instance;
            Mod.Log.Msg($"[BASE] ApplyForScenario('{scenarioFolder}') source='{db?.CurrentSourceId}' chargée={db?.IsLoaded} campagne={Campaign.Refresh()} mission={Campaign.MissionUid}");
            // mission played without the mod (id checked at mission start, scenario folder checked here too): the mission keeps its own database
            if (Campaign.NoteScenario(scenarioFolder))
            {
                Mod.Log.Msg($"[BASE] mission sans mod : la mission garde sa propre base '{db?.CurrentSourceId}'");
                return;
            }
            // only the active campaign mission's scenario (never a skirmish / co-op scenario resolved while the campaign flag is still set)
            var scen = Mod.Svc<CampaignService>()?.ActiveMission?.Scenario;
            string Norm(string s) => (s ?? "").Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(scen) || string.IsNullOrEmpty(scenarioFolder) || Norm(scenarioFolder).IndexOf(Norm(scen), StringComparison.OrdinalIgnoreCase) < 0)
            {
                Mod.Log.Msg($"[BASE] scénario '{scenarioFolder}' différent de la mission '{scen}' : ignoré");
                return;
            }
            CurrentDb.ForceIfNeeded(db, "patch ApplyForScenario");
        });
    }

    // ---------------------------------------------------------------- divisions CAMPAGNE
    static class Divisions
    {
        internal const int RussiaId = 1, UsaId = 2;
        // Two identical divisions per side, because deck creation asks for two specializations: a deck is merged into a campaign mission
        // only when it holds both divisions of the mission's side. 901/902 keep their ids so decks saved with them still load.
        internal const int SpecRussia = 901, SpecUsa = 902, SpecRussia2 = 903, SpecUsa2 = 904;
        const int AvailIdBase = 9_000_000, TransportIdBase = 9_500_000;
        const double UnknownDlcWaitSeconds = 60;
        // description under the division names, in the game's language (Txt DIV_DESC); the names stay the same in every language
        static readonly Dictionary<IntPtr, int> _descEpoch = new();   // source address -> Txt.Epoch its descriptions were written in
        static int _nextAvailId = AvailIdBase, _nextTransportId = TransportIdBase;
        static DataBaseSourceData _failedSrc;   // held reference: a DB whose injection failed is never retried

        /// Country, id, template division (country and content tag), division whose icon is shown, name.
        /// Icons: 1 = the template's own icon (VDV / USMC), 2 = a tank brigade icon (Guard Tank / Armored), so 1 and 2 look different.
        static readonly (int Country, int Id, int Template, int IconFrom, string Label)[] Defs =
        {
            (RussiaId, SpecRussia,  7, 7, "CAMPAGNE RUSSIE 1"),
            (RussiaId, SpecRussia2, 7, 8, "CAMPAGNE RUSSIE 2"),
            (UsaId,    SpecUsa,     3, 3, "CAMPAGNE USA 1"),
            (UsaId,    SpecUsa2,    3, 4, "CAMPAGNE USA 2"),
        };

        // DLC ownership used for the divisions of each source (key: source address), to rebuild them when the ownership changes
        static readonly Dictionary<IntPtr, string> _injectedSig = new();
        /// DLC content the divisions are built from: unknown ownership, no DLC owned and InclureUnitesDLC off all give base game only.
        static string BuildKey => Mod.InclureUnitesDLC.Value && Dlc.Known ? Dlc.Signature : "aucun";
        static long _nextOwnershipCheck;
        static bool _waitLogged, _giveUpLogged, _deferLogged;

        internal static bool IsOurSpec(int id) => id == SpecRussia || id == SpecUsa || id == SpecRussia2 || id == SpecUsa2;
        internal static bool IsOurDeck(IDeckDataModel d) => d != null && (IsOurSpec(d.Spec1ID) || IsOurSpec(d.Spec2ID));
        internal static bool IsOurDeck(DeckDataModel d) => d != null && (IsOurSpec(d.Spec1ID) || IsOurSpec(d.Spec2ID));

        /// Deck-builder points of one division per category: half of PointsParCategorie, so 1 + 2 together give PointsParCategorie.
        internal static int PointsPerDivision => Math.Max(1, Mod.PointsParCategorie.Value / 2);

        internal static string Label(int specId)
        {
            foreach (var d in Defs) if (d.Id == specId) return d.Label;
            return "division " + specId;
        }

        internal static string PairLabel(int countryId) => countryId == UsaId ? "CAMPAGNE USA 1 + 2" : "CAMPAGNE RUSSIE 1 + 2";

        /// Why a deck of this country is not merged into a mission of this country (null = merged): it must hold both CAMPAGNE
        /// divisions of the side, in any order.
        internal static string MissingFor(int countryId, int spec1, int spec2)
        {
            int a = countryId == UsaId ? SpecUsa : SpecRussia, b = countryId == UsaId ? SpecUsa2 : SpecRussia2;
            bool hasA = spec1 == a || spec2 == a, hasB = spec1 == b || spec2 == b;
            if (hasA && hasB) return null;
            if (!hasA && !hasB) return "aucune division " + PairLabel(countryId);
            return "il manque " + Label(hasA ? b : a);
        }

        /// True when this database went through the division injection (a CAMPAGNE division exists in it).
        internal static bool Ready(DataBaseService db)
        {
            var src = db?.RawAccess;
            return src != null && HasAny(src);
        }

        static bool HasAny(DataBaseSourceData src)
        {
            var rows = src.Specializations._rows;
            foreach (var d in Defs) if (rows.ContainsKey(d.Id)) return true;
            return false;
        }

        internal static void SafeInject(DataBaseService db)
        {
            try { if (db != null && db.RawAccess != null && Mod.Actif.Value) EnsureInjected(db); }
            catch (Exception e) { Mod.Log.Error("SafeInject: " + e.Message); }
        }

        internal static void EnsureInjected(DataBaseService db)
        {
            var src = db.RawAccess;
            if (src == null) return;
            Guard.Run("DLC", Dlc.Tick);   // self-throttled (10 s), never in a battle or in a mission played without the mod
            if (_failedSrc != null && src.Pointer == _failedSrc.Pointer) return;
            // A source that already holds a CAMPAGNE division has been through the mod, whatever its address; one that lacks them is freshly loaded.
            if (HasAny(src)) { RefreshTexts(src); CheckOwnershipChange(db); return; }
            // mission played without the mod: its database gets no division (injected again once the campaign mode is left or the next mission starts)
            if (Campaign.Refresh() && Campaign.MissionInerte)
            {
                Mod.Log.Msg($"[CAMPAGNE] mission sans mod : divisions CAMPAGNE non ajoutées à la base '{db.CurrentSourceId}'");
                return;
            }
            // Menu only: wait (60 s at most) for a readable DLC list, so an owner's deck is never read against divisions built without his
            // DLC units. Mission database loads never wait: the campaign merge filters the deck on its own.
            if (!Campaign.InCampaign && !Dlc.Known)
            {
                if (Dlc.SecondsUnknown < UnknownDlcWaitSeconds)
                {
                    if (!_waitLogged) { _waitLogged = true; Mod.Log.Msg("[DLC] en attente de la liste des DLC avant d'ajouter les divisions CAMPAGNE"); }
                    return;
                }
                if (!_giveUpLogged) { _giveUpLogged = true; Mod.Log.Warning("[DLC] liste des DLC toujours illisible après 60 s : divisions CAMPAGNE sans unités DLC"); }
            }

            Campaign.DbLoadsThisMission++;
            Mod.Log.Msg($"Injection des divisions CAMPAGNE (source={db.CurrentSourceId}, chargement n°{Campaign.DbLoadsThisMission} de la mission, DLC {Dlc.Signature})");
            try { StatsExport.Run(db); } catch (Exception e) { Mod.Log.Error("StatsExport: " + e.Message); }

            try
            {
                int epoch = Txt.Epoch;                                         // language of the descriptions written below
                CleanPreviousRows(src);
                InjectAll(db);
                _injectedSig[src.Pointer] = BuildKey;
                _descEpoch[src.Pointer] = epoch;
            }
            catch (Exception e)
            {
                _failedSrc = src;
                Mod.Log.Error($"Injection échouée sur la base '{db.CurrentSourceId}' (pas de nouvel essai) : {e}");
                return;
            }

            if (Realism.ShouldApply(db, Campaign.DbLoadsThisMission)) Realism.Apply(db);
        }

        /// Every 2 s on a database that has the divisions: their description follows the game's language (one write per language change).
        static void RefreshTexts(DataBaseSourceData src)
        {
            int epoch = Txt.Epoch;
            bool known = _descEpoch.TryGetValue(src.Pointer, out int was);
            if (known && was == epoch) return;
            string text = Txt.T(TxtKey.DIV_DESC);
            var rows = src.Specializations._rows;
            int n = 0;
            foreach (var d in Defs)
            {
                if (!rows.ContainsKey(d.Id)) continue;
                var spec = rows[d.Id];
                if (spec == null) continue;
                spec.UIDescription = text;
                n++;
            }
            _descEpoch[src.Pointer] = epoch;
            if (known) Mod.Log.Msg($"[LANGUE] description des divisions CAMPAGNE réécrite en {Txt.Current} ({n} division(s))");
        }

        /// The four divisions and the country point totals (the source must be clean: CleanPreviousRows first).
        static void InjectAll(DataBaseService db)
        {
            var allSpecs = new Il2CppSystem.Collections.Generic.List<Specializations>(db.GetAllSpecializations());
            foreach (var d in Defs) Inject(db, allSpecs, d.Country, d.Id, d.Template, d.IconFrom, d.Label);

            int total = Mod.PointsParCategorie.Value * 7;
            foreach (var cid in new[] { RussiaId, UsaId })
            {
                var c = db.GetCountryById(cid);
                if (c != null && c.MaxPoints < total) c.MaxPoints = total;
            }
        }

        /// Every 10 s on a database that already has the divisions: rebuilds them when the DLC ownership changed (new DLC owned) or when
        /// they hold a unit or transport the player may not use (DLC lost or unknown). Never in a battle, never while the deck builder is open.
        static void CheckOwnershipChange(DataBaseService db)
        {
            long now = Environment.TickCount64;
            if (now < _nextOwnershipCheck) return;
            _nextOwnershipCheck = now + 10_000;
            if (Campaign.MissionInerte || Dlc.BattleLive()) return;
            var src = db.RawAccess;
            if (src == null) return;

            string key = BuildKey, reason = null;
            if (_injectedSig.TryGetValue(src.Pointer, out var sig))
            {
                if (sig != key) reason = $"DLC {sig} -> {key}";
            }
            else
            {
                // divisions this session did not build: one full check, then the key alone
                int n = CountNotAllowed(src);
                if (n > 0) reason = $"{n} unité(s) ou transport(s) de DLC non possédés dans les divisions";
                else _injectedSig[src.Pointer] = key;
            }
            if (reason == null) return;
            if (DeckBuilderOpen())
            {
                if (!_deferLogged) { _deferLogged = true; Mod.Log.Msg($"[DLC] reconstruction des divisions CAMPAGNE en attente : créateur de decks ouvert ({reason})"); }
                return;
            }
            _deferLogged = false;
            Rebuild(db, reason);
        }

        static int CountNotAllowed(DataBaseSourceData src)
        {
            int n = 0;
            var rows = src.Specializations._rows;
            foreach (var d in Defs)
            {
                if (!rows.ContainsKey(d.Id)) continue;
                var av = rows[d.Id]?.Availabilities;
                for (int k = 0; k < (av?.Count ?? 0); k++)
                {
                    var a = av[k];
                    if (a?.Unit == null) continue;
                    if (!Dlc.Allowed(a.Unit)) n++;
                    var tl = a.Transport;
                    for (int t = 0; t < (tl?.Count ?? 0); t++)
                        if (tl[t]?.Unit != null && !Dlc.Allowed(tl[t].Unit)) n++;
                }
            }
            return n;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool ReadDeckBuilderActive() => Il2CppBrokenArrow.Client.Ecs.UI.Menu.Arsenal.ArmyBuilder.DeckOverview.Active;

        static bool _builderFlagDead;
        static bool DeckBuilderOpen()
        {
            if (_builderFlagDead) return false;
            try { return ReadDeckBuilderActive(); }
            catch (Exception e) { _builderFlagDead = true; Mod.Log.Warning("[DLC] état du créateur de decks illisible, reconstruction sans attente : " + e.GetBaseException().Message); return false; }
        }

        /// Removes and rebuilds the four divisions on the same source (no stats export, no realism pass: both already ran on it).
        static void Rebuild(DataBaseService db, string reason)
        {
            var src = db.RawAccess;
            try
            {
                int epoch = Txt.Epoch;                                         // language of the descriptions written below
                CleanPreviousRows(src);
                InjectAll(db);
                _injectedSig[src.Pointer] = BuildKey;
                _descEpoch[src.Pointer] = epoch;
            }
            catch (Exception e)
            {
                _failedSrc = src;
                Mod.Log.Error($"[DLC] reconstruction des divisions CAMPAGNE échouée sur la base '{db.CurrentSourceId}' (pas de nouvel essai) : {e}");
                return;
            }
            DeckCache.Clear();
            Mod.Log.Msg($"[DLC] divisions CAMPAGNE reconstruites ({reason})");
        }

        /// Removes rows left by a previous (partial) injection so the tables never grow, and the division ids it put on the units.
        static void CleanPreviousRows(DataBaseSourceData src)
        {
            foreach (var a in Props.Rows(src.SpecializationAvailabilities.GetAll())) if (a.Id >= AvailIdBase) src.SpecializationAvailabilities._rows.Remove(a.Id);
            foreach (var t in Props.Rows(src.TransportAvailabilities.GetAll())) if (t.Id >= TransportIdBase) src.TransportAvailabilities._rows.Remove(t.Id);
            foreach (var d in Defs) src.Specializations._rows.Remove(d.Id);
            foreach (var u in Props.Rows(src.Units.GetAll()))
            {
                var ids = u?.SpecializationIds;
                if (ids == null) continue;
                foreach (var d in Defs) ids.Remove(d.Id);
            }
            _nextAvailId = AvailIdBase;
            _nextTransportId = TransportIdBase;
        }

        static void Inject(DataBaseService db, Il2CppSystem.Collections.Generic.List<Specializations> allSpecs,
                           int countryId, int newId, int templateSpecId, int iconSpecId, string label)
        {
            var src = db.RawAccess;
            Specializations template = null, iconSrc = null;
            var best = new Dictionary<int, SpecializationAvailabilities>();
            var transports = new Dictionary<int, Dictionary<int, TransportAvailabilities>>();
            var skipped = new Dictionary<int, string>();       // unit id -> content tag of a DLC the player may not use
            var skippedTransports = new HashSet<int>();
            void Skip(Units u) { if (!skipped.ContainsKey(u.Id)) skipped[u.Id] = Dlc.MembershipName(u); }

            // every division of the country, DLC divisions included: the DLC filter is per unit and per transport
            for (int i = 0; i < allSpecs.Count; i++)
            {
                var s = allSpecs[i];
                if (s.CountryId != countryId || IsOurSpec(s.Id)) continue;
                if (s.Id == templateSpecId) template = s;
                if (s.Id == iconSpecId) iconSrc = s;
                var av = s.Availabilities;
                if (av == null) continue;
                for (int k = 0; k < av.Count; k++)
                {
                    var a = av[k];
                    if (a == null || a.Unit == null) continue;
                    if (!Dlc.Allowed(a.Unit)) { Skip(a.Unit); continue; }
                    if (!best.TryGetValue(a.UnitId, out var cur) || a.MaxAvailabilityXp0 > cur.MaxAvailabilityXp0) best[a.UnitId] = a;
                    if (!transports.TryGetValue(a.UnitId, out var tset)) transports[a.UnitId] = tset = new Dictionary<int, TransportAvailabilities>();
                    var tl = a.Transport;
                    if (tl == null) continue;
                    for (int t = 0; t < tl.Count; t++)
                    {
                        if (tl[t] == null || tl[t].Unit == null || tset.ContainsKey(tl[t].UnitId)) continue;
                        // a base game unit never inherits a transport of a DLC the player may not use
                        if (!Dlc.Allowed(tl[t].Unit)) { skippedTransports.Add(tl[t].UnitId); continue; }
                        tset[tl[t].UnitId] = tl[t];
                    }
                }
            }
            if (template == null) { Mod.Log.Error($"{label}: division modèle {templateSpecId} introuvable"); return; }
            if (iconSrc == null || string.IsNullOrEmpty(iconSrc.Icon))
            {
                Mod.Log.Warning($"{label}: icône de la division {iconSpecId} introuvable, icône de la division modèle {templateSpecId}");
                iconSrc = template;
            }

            int pts = PointsPerDivision, slots = Mod.EmplacementsParCategorie.Value;
            var spec = new Specializations();
            spec.Id = newId;
            spec.CountryId = countryId;
            spec.Country = template.Country;
            spec.Name = label;
            spec.UIName = label;
            spec.UIDescription = Txt.T(TxtKey.DIV_DESC);
            spec.Icon = iconSrc.Icon;
            spec.Illustration = template.Illustration;
            spec.ContentMembership = template.ContentMembership;
            spec.ShowInHangar = true;
            spec.ReconSlots = spec.InfantrySlots = spec.CombatSlots = spec.SupportSlots = slots;
            spec.HelicoptersSlots = spec.AirSlots = slots;
            spec.ReconPoints = spec.InfantryPoints = spec.CombatPoints = spec.SupportPoints = pts;
            spec.HelicoptersPoints = spec.AirPoints = pts;
            // No playable logistics units exist: an open Logistic tab makes the hangar throw on an empty list.
            spec.LogisticsSlots = 0;
            spec.LogisticsPoints = 0;

            var list = new Il2CppList();
            int nbTransports = 0, limited = 0;
            foreach (var kv in best)
            {
                var o = kv.Value;
                var a = new SpecializationAvailabilities();
                a.Id = _nextAvailId++;
                a.SpecializationId = newId;
                a.UnitId = o.UnitId;
                a.Unit = o.Unit;
                a.Name = o.Name;
                a.Specialization = spec;
                a.MaxAvailabilityXp0 = o.MaxAvailabilityXp0;
                a.MaxAvailabilityXp1 = o.MaxAvailabilityXp1;
                a.MaxAvailabilityXp2 = o.MaxAvailabilityXp2;
                a.MaxAvailabilityXp3 = o.MaxAvailabilityXp3;
                if (RealStats.LimitAvailability(a)) limited++;
                var tl = new Il2CppTransportList();
                foreach (var t0 in transports[kv.Key].Values)
                {
                    var t = new TransportAvailabilities();
                    t.Id = _nextTransportId++;
                    t.SpecializationAvailabilityId = a.Id;
                    t.UnitId = t0.UnitId;
                    t.Unit = t0.Unit;
                    t.Name = t0.Name;
                    t.SpecializationAvailability = a;
                    tl.Add(t);
                    src.TransportAvailabilities._rows[t.Id] = t;
                    nbTransports++;
                }
                a.Transport = tl;
                list.Add(a);
                src.SpecializationAvailabilities._rows[a.Id] = a;
                o.Unit.SpecializationIds?.Add(newId);
            }
            // Units of the country that no division offers (e.g. Bradley, only sold as a transport): add them too.
            int extra = 0;
            foreach (var u in Props.Rows(src.Units.GetAll()))
            {
                if (u == null || u.CountryId != countryId || !u.DisplayInArmory || u.IsUnitModification || best.ContainsKey(u.Id)) continue;
                if (!Dlc.Allowed(u)) { Skip(u); continue; }   // never re-adds a unit filtered out above
                var a = new SpecializationAvailabilities();
                a.Id = _nextAvailId++;
                a.SpecializationId = newId;
                a.UnitId = u.Id;
                a.Unit = u;
                a.Name = u.Name;
                a.Specialization = spec;
                a.MaxAvailabilityXp0 = 4; a.MaxAvailabilityXp1 = 3; a.MaxAvailabilityXp2 = 2; a.MaxAvailabilityXp3 = 1;
                if (RealStats.LimitAvailability(a)) limited++;
                a.Transport = new Il2CppTransportList();
                list.Add(a);
                src.SpecializationAvailabilities._rows[a.Id] = a;
                u.SpecializationIds?.Add(newId);
                extra++;
            }
            spec.Availabilities = list;
            src.Specializations._rows[newId] = spec;

            string dlc = "";
            if (skipped.Count > 0) dlc += $", {skipped.Count} unités de DLC non possédés retirées ({string.Join(", ", skipped.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal))})";
            if (skippedTransports.Count > 0) dlc += $", {skippedTransports.Count} transport(s) de DLC non possédés retirés";
            Mod.Log.Msg($"  {label} (id {newId}) : {list.Count} unités (dont {extra} hors divisions), {nbTransports} options de transport, {pts} pts et {slots} cartes par catégorie" +
                        (limited > 0 ? $", {limited} unités limitées par les vraies stats" : "") + dlc);
        }
    }

    [HarmonyPatch(typeof(DataBaseService), nameof(DataBaseService.LoadCompiled))]
    static class Patch_DbLoadCompiled
    {
        static void Prefix() => Guard.Run("DbLoad.Prefix", () => Realism.Restore("rechargement de la base"));
        static void Postfix(DataBaseService __instance) => Divisions.SafeInject(__instance);
    }

    [HarmonyPatch(typeof(DataBaseService), nameof(DataBaseService.LoadByJson))]
    static class Patch_DbLoadByJson
    {
        static void Prefix() => Guard.Run("DbLoad.Prefix", () => Realism.Restore("rechargement de la base"));
        static void Postfix(DataBaseService __instance) => Divisions.SafeInject(__instance);
    }

    [HarmonyPatch(typeof(DataBaseService), nameof(DataBaseService.LoadScenario))]
    static class Patch_DbLoadScenario
    {
        static void Prefix() => Guard.Run("DbLoad.Prefix", () => Realism.Restore("rechargement de la base"));
        static void Postfix(DataBaseService __instance) => Divisions.SafeInject(__instance);
    }

    [HarmonyPatch(typeof(Deck), nameof(Deck.GetMaxCategoryPointsOrSlots))]
    static class Patch_MaxPointsOrSlots
    {
        static void Postfix(Il2CppReferenceArray<Specializations> specs, UnitCategoryType categoryType, bool slots, ref int __result)
        {
            try
            {
                if (!Mod.Actif.Value || specs == null || categoryType == UnitCategoryType.Logistic) return;
                // points: half of PointsParCategorie per CAMPAGNE division (1 + 2 together = PointsParCategorie); slots: the builder maximum
                int ours = 0;
                for (int i = 0; i < specs.Length; i++)
                    if (specs[i] != null && Divisions.IsOurSpec(specs[i].Id)) ours++;
                if (ours == 0) return;
                int want = slots ? Mod.EmplacementsParCategorie.Value : Divisions.PointsPerDivision * ours;
                if (__result < want) __result = want;
            }
            catch (Exception e) { Mod.Log.Error("MaxPointsOrSlots: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- protection hors campagne
    [HarmonyPatch(typeof(DeckService), nameof(DeckService.GetAvailableDecksForScenario))]
    static class Patch_HideFromScenarios
    {
        static void Postfix(ref Il2CppReferenceArray<IDeckDataModel> __result)
        {
            try
            {
                if (__result == null) return;
                var keep = new List<IDeckDataModel>();
                for (int i = 0; i < __result.Length; i++) if (!Divisions.IsOurDeck(__result[i])) keep.Add(__result[i]);
                if (keep.Count == __result.Length) return;
                var arr = new Il2CppReferenceArray<IDeckDataModel>(keep.Count);
                for (int i = 0; i < keep.Count; i++) arr[i] = keep[i];
                __result = arr;
                Mod.Log.Msg("Decks CAMPAGNE masqués de la sélection (réservés à la campagne).");
            }
            catch (Exception e) { Mod.Log.Error("HideFromScenarios: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(TeamPanelPlayerInfo), nameof(TeamPanelPlayerInfo.ChangeDeck))]
    static class Patch_BlockNetworkDeck
    {
        static bool Prefix(DeckDataModel deckDataModel) => NetGuard.Allow(deckDataModel, "sélection de deck en salon");
    }

    [HarmonyPatch(typeof(NetworkRoomService), nameof(NetworkRoomService.ChangeClientDeck))]
    static class Patch_BlockRoomDeck
    {
        static bool Prefix(DeckDataModel deckData) => NetGuard.Allow(deckData, "envoi du deck au salon");
    }

    [HarmonyPatch(typeof(NetworkLobbyService), nameof(NetworkLobbyService.ChangeClientDeck))]
    static class Patch_BlockLobbyDeck
    {
        static bool Prefix(DeckDataModel deckData) => NetGuard.Allow(deckData, "envoi du deck au lobby");
    }

    [HarmonyPatch(typeof(DeckService), nameof(DeckService.GetFavoriteDeckByCountryId))]
    static class Patch_FavoriteDeck
    {
        static void Postfix(DeckService __instance, int countryId, ref DeckDataModel __result) => NetGuard.Substitute(__instance, countryId, ref __result, "deck favori");
    }

    [HarmonyPatch(typeof(DeckService), nameof(DeckService.GetDefaultDeckByCountryId))]
    static class Patch_DefaultDeck
    {
        static void Postfix(DeckService __instance, int countryId, ref DeckDataModel __result) => NetGuard.Substitute(__instance, countryId, ref __result, "deck par défaut");
    }

    static class NetGuard
    {
        internal static bool Allow(DeckDataModel deck, string where)
        {
            try
            {
                if (deck != null && Divisions.IsOurDeck(deck)) { Mod.Log.Warning($"Deck CAMPAGNE refusé ({where})."); return false; }
            }
            catch (Exception e) { Mod.Log.Error("NetGuard: " + e.Message); }
            return true;
        }

        /// A CAMPAGNE deck auto-picked as favourite/default is replaced by another deck of the same country.
        internal static void Substitute(DeckService ds, int countryId, ref DeckDataModel result, string where)
        {
            try
            {
                if (result == null || !Divisions.IsOurDeck(result)) return;
                DeckDataModel best = null;
                var all = ds.AllValidDecks;
                for (int i = 0; i < (all?.Length ?? 0); i++)
                {
                    var d = all[i];
                    if (d == null || d.CountryID != countryId || Divisions.IsOurDeck(d)) continue;
                    if (best == null || (d.IsFavorite && !best.IsFavorite)) best = d;
                }
                Mod.Log.Warning($"Deck CAMPAGNE '{result.Name}' écarté ({where}) -> " + (best == null ? "aucun" : $"'{best.Name}'"));
                result = best;
            }
            catch (Exception e) { Mod.Log.Error("NetGuard.Substitute: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- decks du joueur
    static class DeckCache
    {
        /// One .dek file as last read. Only decks holding a CAMPAGNE division keep a copy of their model.
        sealed class Entry
        {
            internal DateTime Mtime;
            internal int Country, Spec1, Spec2;
            internal DeckDataModel Deck;
        }

        static readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
        static bool _waitLogged;

        static string DecksDir => Path.Combine(UnityEngine.Application.persistentDataPath, "Decks");

        /// Forgets every deck read so far (the CAMPAGNE divisions were rebuilt): the next Refresh reads the files again.
        internal static void Clear() => _cache.Clear();

        /// Reads every deck file of the Decks folder once per file date (the deck name does not matter, its divisions do).
        /// Nothing is read before the CAMPAGNE divisions exist in the loaded database: a deck read against a missing division could be
        /// trimmed by the game's loader.
        internal static void Refresh()
        {
            if (!Directory.Exists(DecksDir)) { _cache.Clear(); return; }
            var files = Directory.GetFiles(DecksDir, "*.dek");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) names.Add(Path.GetFileNameWithoutExtension(f));
            foreach (var gone in _cache.Keys.Where(k => !names.Contains(k)).ToList()) _cache.Remove(gone);

            var db = DataBaseService._instance;
            if (db == null || !db.IsLoaded || !Divisions.Ready(db))
            {
                if (!_waitLogged) { _waitLogged = true; Mod.Log.Msg("[CAMPAGNE] lecture des decks en attente des divisions CAMPAGNE"); }
                return;
            }
            _waitLogged = false;
            DeckService ds = null;
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                try { Load(name, f, db, ref ds); }
                catch (Exception e) { Mod.Log.Error($"DeckCache '{name}': {e.Message}"); }
            }
        }

        static void Load(string name, string path, DataBaseService db, ref DeckService ds)
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(name, out var old) && old.Mtime == mtime) return;
            ds ??= Mod.Svc<DeckService>();
            if (ds == null) return;                                   // service not up yet: read on a later refresh
            var e = new Entry { Mtime = mtime };
            _cache[name] = e;                                         // an unreadable file is not read again until it changes
            if (!ds.TryLoadDeckModel(name, out DeckDataModel model) || model == null)
            {
                Mod.Log.Msg($"[CAMPAGNE] deck '{name}' illisible : ignoré");
                return;
            }
            e.Country = model.CountryID;
            e.Spec1 = model.Spec1ID;
            e.Spec2 = model.Spec2ID;
            if (!Divisions.IsOurSpec(e.Spec1) && !Divisions.IsOurSpec(e.Spec2)) return;   // an ordinary deck: never used by the campaign merge

            e.Deck = Deck.Clone(model);
            var deckI = new IDeckDataModel(e.Deck.Pointer);
            string why = Divisions.MissingFor(e.Country, e.Spec1, e.Spec2);
            if (why == null) Mod.Log.Msg($"Deck '{name}' prêt pour la campagne ({Divisions.PairLabel(e.Country)}) : {Mod.DeckStr(deckI)}");
            else Mod.Log.Msg($"[CAMPAGNE] deck '{name}' non utilisé en campagne : {why}");
            int n = CountNotAllowed(deckI, db);
            if (n > 0) Mod.Log.Msg($"[DLC] deck '{name}' : {n} carte(s) d'un DLC non possédé, ignorée(s) en mission");
        }

        /// Cards (units and transports) of the deck that the campaign merge will leave out for DLC ownership. Read only.
        static int CountNotAllowed(IDeckDataModel deck, DataBaseService db)
        {
            int n = 0;
            foreach (var cat in Mod.Categories)
            {
                var arr = Deck.GetCategorySlots(deck, cat);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    var s = arr[i];
                    if (s == null || s.UnitID <= 0) continue;
                    if (!Allowed(db, s.UnitID)) n++;
                    else if (s.TransportID > 0 && !Allowed(db, s.TransportID)) n++;
                }
            }
            return n;
        }

        static bool Allowed(DataBaseService db, int unitId)
        {
            Units u = null;
            try { u = db.GetUnitById(unitId, false); } catch { }
            return u == null || Dlc.Allowed(u);          // a unit unknown to the database is reported by the merge itself
        }

        /// Decks merged into a mission of this country: those holding both CAMPAGNE divisions of the side, in any order, whatever
        /// their name. The side's decks that miss a division are listed in `ignored` with the reason.
        internal static List<DeckDataModel> ForMission(int countryId, List<string> ignored)
        {
            try { Refresh(); } catch (Exception e) { Mod.Log.Error("DeckCache: " + e.Message); }
            var res = new List<DeckDataModel>();
            foreach (var kv in _cache.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var e = kv.Value;
                if (e.Deck == null || e.Country != countryId) continue;
                string why = Divisions.MissingFor(countryId, e.Spec1, e.Spec2);
                if (why == null) res.Add(e.Deck);
                else ignored?.Add($"deck '{kv.Key}' ignoré : {why}");
            }
            return res;
        }
    }

    // ---------------------------------------------------------------- campagne
    static class Campaign
    {
        internal static bool InCampaign;
        internal static string MissionUid;
        internal static int MissionCountryId;
        internal static int DbLoadsThisMission;
        internal static DataBaseSourceData MissionDbSource;   // the database the mod forced for this mission (held reference)
        internal static readonly List<DeckDataModel> UserDecks = new();
        internal static readonly Dictionary<int, bool> IsBotByUid = new();
        internal static int LocalUid = -1;
        internal static readonly HashSet<int> Merged = new();
        internal static DeckDataModel LastMerged;        // held reference: the address can't be reused while we hold it
        internal static bool Reentrant;
        static GameSessionContext _heldCtx;               // held reference to the current play session
        static string _lastStatus;
        static bool _announced;

        // ------------------------------------------------------------ missions played without the mod (player rule 2026-09-17)
        /// Campaign missions where the whole mod stays inert: the first US mission is a tutorial whose scripted steps are impossible with it.
        /// Its folder is the id with a space ("US_M01" -> SharedMissions\"US M01 Tuto").
        static readonly string[] MissionsSansMod = { "US_M01" };

        /// True while the current campaign mission is listed in MissionsSansMod. Set at mission start (id or scenario folder) or by the
        /// ApplyForScenario postfix (scenario folder); cleared when another mission starts or the campaign mode ends. Volatile: every hook
        /// reads it first, on any thread, and returns at once while it is set.
        internal static volatile bool MissionInerte;
        static bool _inertLogged, _inertNoticeShown, _releasePending;
        static GameController _inertGc;                   // held: the battle of the mission without the mod (its address is never reused)

        /// True when the mission id or a folder of the scenario path matches MissionsSansMod (case-insensitive).
        static bool IsMissionSansMod(string uid, string scenario)
        {
            foreach (var id in MissionsSansMod)
            {
                if (!string.IsNullOrWhiteSpace(uid) && string.Equals(uid.Trim(), id, StringComparison.OrdinalIgnoreCase)) return true;
                if (ScenarioMatches(scenario, id)) return true;
            }
            return false;
        }

        /// A path segment named after the id ("US M01", or "US M01 " followed by the title; '_' counts as a space). "US M01A" never matches.
        static bool ScenarioMatches(string path, string id)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(id)) return false;
            string name = id.Replace('_', ' ').Trim();
            foreach (var raw in path.Replace('\\', '/').Split('/'))
            {
                string seg = raw.Replace('_', ' ').Trim();
                if (seg.Equals(name, StringComparison.OrdinalIgnoreCase) || seg.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// ApplyForScenario postfix: a scenario folder of a mission without the mod switches the mod off for the current campaign mission.
        /// Returns MissionInerte.
        internal static bool NoteScenario(string scenarioFolder)
        {
            if (!InCampaign) return false;
            bool match = IsMissionSansMod(null, scenarioFolder);
            if (_releasePending && !match) ReleasePending("le scénario d'une autre mission se charge");
            if (!MissionInerte && match) { EnterInert(); ResetSession(); }
            return MissionInerte;
        }

        /// The mod goes inert for this mission: no player deck to merge, realism and the AI statics put back, one log line.
        static void EnterInert()
        {
            MissionInerte = true;
            UserDecks.Clear();
            if (!_inertLogged)
            {
                _inertLogged = true;
                Mod.Log.Msg($"[CAMPAGNE] mission {MissionUid} (tuto) : mod inactif sur cette mission");
            }
            try { Realism.Restore("mission sans mod"); } catch (Exception e) { Mod.Log.Warning("[CAMPAGNE] mission sans mod : restauration des stats impossible : " + e.Message); }
            try { Assistants.RestoreStatics(); } catch { }
        }

        /// True while the battle seen during the mission without the mod is still the live one (same GameController, a player in it).
        static bool InertBattleAlive()
        {
            try
            {
                var gc = GameController._instance;
                return _inertGc != null && gc != null && gc.Pointer == _inertGc.Pointer && gc._GameSession_k__BackingField?.CurrentPlayer != null;
            }
            catch { return false; }
        }

        /// The next mission was chosen while the battle without the mod was still on screen: now that it is over, that mission starts normally.
        static void ReleasePending(string why)
        {
            _releasePending = false;
            _inertGc = null;
            Mod.Log.Msg($"[CAMPAGNE] {why} : le mod reprend pour la mission {MissionUid}");
            MissionUid = null;                                 // Refresh starts the active mission again (new id, the mod active)
            Refresh();
        }

        /// Every 2 s in a mission without the mod: anything applied since is put back, and the player sees one notice once the battle exists.
        internal static void InertTick()
        {
            if (!MissionInerte) return;
            if (Realism.IsApplied) Realism.Restore("mission sans mod");
            if (Cheats.ToughArmed) Cheats.Disarm();
            if (_releasePending) { if (!InertBattleAlive()) ReleasePending("la bataille du tuto est terminée"); return; }
            try { var gc = GameController._instance; if (gc != null && gc._GameSession_k__BackingField?.CurrentPlayer != null) _inertGc = gc; } catch { }
            if (_inertNoticeShown) return;
            if (Ctx()?.CurrentPlayer == null) return;
            _inertNoticeShown = true;
            Mod.Notify(TxtKey.N_TUTO_NO_MOD);
        }

        /// Asks the campaign service directly (its SetCampaignMode call is not interceptable).
        static long _svcMissingSince = -1;

        internal static bool Refresh()
        {
            var camp = Mod.Svc<CampaignService>();
            if (camp == null)
            {
                // fail closed: a campaign service missing for more than 10 s means the campaign is over
                if (!InCampaign) return false;
                long t = Environment.TickCount64;
                if (_svcMissingSince < 0) _svcMissingSince = t;
                if (t - _svcMissingSince < 10000) return true;
            }
            else _svcMissingSince = -1;
            if (camp == null || !camp.IsCampaignMode)
            {
                if (InCampaign)
                {
                    InCampaign = false;
                    _svcMissingSince = -1;
                    UserDecks.Clear();
                    MissionUid = null;
                    MissionDbSource = null;
                    _releasePending = false;
                    _inertGc = null;
                    if (MissionInerte) { MissionInerte = false; Mod.Log.Msg("[CAMPAGNE] fin de la mission sans mod : le mod est de nouveau actif"); }
                    Realism.Restore("fin de campagne");
                    Assistants.RestoreStatics();
                    ResetSession();
                    _heldCtx = null;
                    Mod.Log.Msg("[CAMPAGNE] fin du mode campagne");
                }
                return false;
            }
            var uid = camp.ActiveMissionUid;
            if (!InCampaign || uid != MissionUid) Start(camp.ActiveMission, uid);
            return true;
        }

        static void Start(MissionData mission, string uid)
        {
            InCampaign = true;
            MissionUid = uid;
            DbLoadsThisMission = 0;
            MissionDbSource = null;
            CurrentDb.FailedSrc = null;
            // missions without the mod: decided first, so every hook stops before the per-session reset below
            string scenario = null;
            try { scenario = mission?.Scenario; } catch { }
            bool wasInert = MissionInerte;
            bool inert = IsMissionSansMod(uid, scenario);
            // another mission chosen while the battle without the mod is still on screen: the mod waits for that battle to end (InertTick)
            bool pending = !inert && wasInert && InertBattleAlive();
            _releasePending = pending;
            if (!pending) _inertGc = null;
            _inertLogged = pending;
            _inertNoticeShown = pending;
            MissionInerte = inert || pending;
            ResetSession();
            UserDecks.Clear();
            if (pending) { Mod.Log.Msg($"[CAMPAGNE] mission {uid} choisie pendant la bataille du tuto : le mod reste inactif jusqu'à la fin de cette bataille"); return; }
            if (MissionInerte) { EnterInert(); return; }
            if (wasInert) Mod.Log.Msg($"[CAMPAGNE] mission {uid} : le mod est de nouveau actif");

            var countryName = mission?.Country.ToString();
            MissionCountryId = countryName == "USA" ? Divisions.UsaId : Divisions.RussiaId;

            // DLC ownership read again for this mission (and written to the log once per mission)
            ResetDlcMission();
            Guard.Run("DLC.Mission", () => Dlc.Refresh(true, alwaysLog: true));

            // player decks: the ones holding both CAMPAGNE divisions of the mission's side, whatever their name
            var ignored = new List<string>();
            UserDecks.AddRange(DeckCache.ForMission(MissionCountryId, ignored));
            string pair = Divisions.PairLabel(MissionCountryId);
            Mod.Log.Msg($"[CAMPAGNE] mission {uid} ({countryName}) -> {UserDecks.Count} deck(s) avec {pair} : {string.Join(", ", UserDecks.Select(d => "'" + d.Name + "'"))}");
            foreach (var line in ignored) Mod.Log.Msg("[CAMPAGNE] " + line);
        }

        // ------------------------------------------------------------ DLC gate of the campaign merge
        /// Cards (units or transports) of DLCs the player may not use that the last merge left out; shown once per mission.
        internal static int DlcSkipped;
        static bool _dlcNoticeShown, _dlcReadThisMission;
        static readonly HashSet<int> _missionCardsChecked = new();

        static void ResetDlcMission()
        {
            DlcSkipped = 0;
            _dlcNoticeShown = false;
            _dlcReadThisMission = false;
            _missionCardsChecked.Clear();
        }

        /// One notice on screen, once the player exists, when the merge left cards out.
        static void DlcNotice()
        {
            if (_dlcNoticeShown || DlcSkipped <= 0) return;
            if (Ctx()?.CurrentPlayer == null) return;
            _dlcNoticeShown = true;
            Mod.Notify(new TxtMsg(Dlc.Known ? TxtKey.N_DLC_REMOVED : TxtKey.N_DLC_REMOVED_UNKNOWN, DlcSkipped));
        }

        /// False when the database knows this unit and it belongs to a DLC the player may not use (name filled for the log).
        /// A unit unknown to the database passes here: SanitizedClone drops it with its own reason.
        static bool UnitAllowed(DataBaseService db, int unitId, out string name)
        {
            name = null;
            Units u = null;
            try { u = db?.GetUnitById(unitId, false); } catch { }
            if (u == null || Dlc.Allowed(u)) return true;
            try { name = $"{unitId} {u.Name} ({Dlc.MembershipName(u)})"; } catch { name = unitId.ToString(CultureInfo.InvariantCulture); }
            return false;
        }

        /// Cards the mission itself provides are left as they are; a DLC one is only written to the log (once per mission).
        static void NoteMissionCard(DataBaseService db, DeckSlotModel s)
        {
            foreach (int id in new[] { s.UnitID, s.TransportID })
            {
                if (id <= 0 || !_missionCardsChecked.Add(id)) continue;
                Units u = null;
                try { u = db?.GetUnitById(id, false); } catch { }
                if (u == null || Dlc.MembershipOf(u) == -1) continue;
                Mod.Log.Msg($"[DLC] carte fournie par la mission elle-même : {id} {u.Name} ({Dlc.MembershipName(u)}) (laissée)");
            }
        }

        internal static void ResetSession()
        {
            IsBotByUid.Clear();
            Merged.Clear();
            LocalUid = -1;
            LastMerged = null;
            _lastStatus = null;
            _announced = false;
            Cheats.Reset();
            Assistants.ResetSession();
            EnemyAi.ResetSession();
            Demolition.ResetSession();
            Spawns.ResetSession();
            Obstacles.ResetSession();
            S400Mode.ResetSession();
            Reperage.ResetSession();
            LeurresMesure.ResetSession();
            AntiHeliPortee.ResetSession();
            AntiHeliTouches.ResetSession();
            VueAA.ResetSession();
            // v0.23.0 modules: each one on its own guard, so one failure never skips the next reset
            Guard.Run("Missions.Reset", Missions.ResetSession);
            Guard.Run("AltCercles.Reset", AltCercles.ResetSession);
            Guard.Run("CalibreMesure.Reset", CalibreMesure.ResetSession);
            Guard.Run("Couvert.Reset", Couvert.ResetSession);
            Guard.Run("Affuts.Reset", Affuts.ResetSession);
            Guard.Run("Perf.Reset", Perf.ResetSession);                   // counters start with this battle (and a battle closed by no end screen gets its line here)
            _battleEndSent = false;
            BattleOver = false;
            _endPlayed = 0f;
            _lastEndPoll = 0f;
            _nextEndPoll = 0f;
        }

        // ------------------------------------------------------------ battle end (poll of the end screen's static flag, no hook)
        static bool _battleEndSent;
        static float _endPlayed, _lastEndPoll, _nextEndPoll;

        /// Set when the battle-end signal was sent; cleared when the end screen hides, the game session goes away or a new session starts
        /// (ResetSession). Never cleared by OnRestart: the restart click comes while the end screen and the old session are still there.
        /// While it is set, the modules closed by OnBattleEnd do not start a new battle on the end screen (Mod.OnUpdate skips the frames of
        /// Missions, Couvert and Affuts; CalibreMesure has its own latch).
        internal static bool BattleOver;

        /// Every frame in a campaign mission (0.5 s throttle). When the battle end screen shows after at least 2 minutes of play with the
        /// screen hidden, the modules that close their measurements and automatic stages at battle end do it now, once per battle
        /// (a flag already up at the start of the battle never counts). If this never fires, the next ResetSession or the modules' own
        /// "no more game" check still ends their battle; a false signal only splits one battle in two measurement periods.
        internal static void PollBattleEnd()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextEndPoll) return;
            float dt = _lastEndPoll > 0f ? Math.Min(1f, now - _lastEndPoll) : 0f;
            _lastEndPoll = now;
            _nextEndPoll = now + 0.5f;
            if (_battleEndSent)
            {
                if (BattleOver)
                {
                    NoteSession();                                                  // a restarted battle (new session) clears the latch at once
                    if (!BattleOver) return;
                    bool still = false;
                    try { still = BattleEndScreen.Active && Ctx()?.CurrentPlayer != null; } catch { }
                    if (!still)
                    {
                        BattleOver = false;                                         // end screen closed or game gone: the next battle may start
                        Mod.Log.Msg("[CAMPAGNE] écran de fin de bataille fermé : les mesures peuvent reprendre");
                    }
                }
                return;
            }
            bool shown;
            try { shown = BattleEndScreen.Active; }
            catch { _battleEndSent = true; return; }                    // member gone after a game update: never again this battle (no latch)
            if (!shown)
            {
                bool player = false;
                try { player = Ctx()?.CurrentPlayer != null; } catch { }
                if (player) _endPlayed += dt;
                return;
            }
            if (_endPlayed < 120f) return;
            _battleEndSent = true;
            BattleOver = true;
            Mod.Log.Msg($"[CAMPAGNE] écran de fin de bataille affiché (après {_endPlayed.ToString("0", CultureInfo.InvariantCulture)} s de partie) : bilans de fin de bataille");
            Guard.Run("Missions.FinBataille", Missions.OnBattleEnd);
            Guard.Run("CalibreMesure.FinBataille", CalibreMesure.OnBattleEnd);
            Guard.Run("Couvert.FinBataille", Couvert.OnBattleEnd);
            Guard.Run("Affuts.FinBataille", Affuts.OnBattleEnd);
            Guard.Run("Perf.FinBataille", Perf.EndBattle);               // the battle gets its [PERF] line now, not at the next one
        }

        internal static GameSessionContext Ctx()
        {
            try { return GameController._instance?._GameSession_k__BackingField; } catch { return null; }
        }

        /// A new GameSessionContext means a new battle (mission start or in-place restart): everything per-session is reset.
        internal static void NoteSession()
        {
            var ctx = Ctx();
            if (ctx == null) return;
            if (_heldCtx != null && _heldCtx.Pointer == ctx.Pointer) return;
            bool first = _heldCtx == null;
            _heldCtx = ctx;
            if (!first) Mod.Log.Msg("[CAMPAGNE] nouvelle partie détectée (redémarrage) : remise à zéro");
            ResetSession();
        }

        internal static void OnRestart()
        {
            Mod.Log.Msg("[CAMPAGNE] redémarrage de mission demandé");
            Cheats.Reset();
        }

        internal static bool IsHuman(int uid, out bool known)
        {
            var ctx = Ctx();
            if (ctx != null)
            {
                PlayerInfo p = null;
                try { ctx.TryGetPlayer(uid, out p); } catch { }
                if (p != null)
                {
                    known = true;
                    PlayerInfo cur = null;
                    try { cur = ctx.CurrentPlayer; } catch { }
                    return !p.IsBot && (cur == null || cur.UID == uid);
                }
            }
            known = IsBotByUid.TryGetValue(uid, out var bot);
            if (!known) return false;
            return !bot && (LocalUid < 0 || LocalUid == uid);
        }

        static bool ModsValid(Il2CppModList mods, DataBaseService db)
        {
            if (mods == null) return true;
            for (int i = 0; i < mods.Count; i++)
            {
                var m = mods[i];
                if (m == null) continue;
                try
                {
                    if (db.GetModificationById(m.ModID) == null || db.GetOptionById(m.OptionID) == null) return false;
                }
                catch { return false; }
            }
            return true;
        }

        /// Copies a slot of the player's deck, dropping what the mission's database version doesn't know and any unit or transport of a
        /// DLC the player may not use (those are listed by BuildMerged, which checks them first).
        static DeckSlotModel SanitizedClone(DeckSlotModel us, DataBaseService db, List<string> dropped)
        {
            Units unit = null;
            try { unit = db.GetUnitById(us.UnitID, false); } catch { }
            if (unit == null) { dropped.Add("unité " + us.UnitID); return null; }
            if (!Dlc.Allowed(unit)) return null;

            var s = us.Clone();
            s._loadedUnit = null;
            s._loadedTransportUnit = null;
            if (!ModsValid(s.ModList, db)) { s.ModList = DeckModData.CollectMods(unit); dropped.Add("options de " + us.UnitID); }

            if (s.TransportID > 0)
            {
                Units tr = null;
                try { tr = db.GetUnitById(s.TransportID, false); } catch { }
                if (tr == null || !Dlc.Allowed(tr))
                {
                    if (tr == null) dropped.Add("transport " + s.TransportID);
                    s.TransportID = 0;
                    s.TransportCount = 0;
                    s.TransportModList = new Il2CppModList();
                }
                else if (!ModsValid(s.TransportModList, db))
                {
                    s.TransportModList = DeckModData.CollectMods(tr);
                }
            }
            return s;
        }

        /// Merges the player's CAMPAGNE decks into a mission deck. Idempotent: a unit+transport already present keeps one card with the larger count.
        internal static DeckDataModel BuildMerged(IDeckDataModel baseDeck, out bool changed)
        {
            var db = DataBaseService._instance;
            // DLC ownership still unknown: one more reading, at most once per mission
            if (!Dlc.Known && !_dlcReadThisMission) { _dlcReadThisMission = true; Guard.Run("DLC.Fusion", () => Dlc.Refresh(true)); }
            var clone = Deck.Clone(baseDeck);
            var cloneI = new IDeckDataModel(clone.Pointer);
            var dropped = new List<string>();
            var dlcDropped = new List<string>();
            changed = false;

            foreach (var cat in Mod.Categories)
            {
                var slots = new List<DeckSlotModel>();
                var baseArr = Deck.GetCategorySlots(cloneI, cat);
                int baseLen = baseArr?.Length ?? 0;
                for (int i = 0; i < baseLen; i++)
                    if (baseArr[i] != null && baseArr[i].UnitID > 0) { slots.Add(baseArr[i]); NoteMissionCard(db, baseArr[i]); }
                int baseUsed = slots.Count;

                foreach (var ud in UserDecks)
                {
                    var userArr = Deck.GetCategorySlots(new IDeckDataModel(ud.Pointer), cat);
                    for (int i = 0; i < (userArr?.Length ?? 0); i++)
                    {
                        var us = userArr[i];
                        if (us == null || us.UnitID <= 0) continue;
                        // DLC gate: a unit of a DLC the player may not use never enters the mission deck, not even as a count raise;
                        // its transport alone is removed (the card keeps its unit, without transport)
                        if (!UnitAllowed(db, us.UnitID, out var unitName)) { dlcDropped.Add(unitName); continue; }
                        int trId = us.TransportID;
                        if (trId > 0 && !UnitAllowed(db, trId, out var trName)) { dlcDropped.Add("transport " + trName); trId = 0; }
                        var existing = slots.FirstOrDefault(x => x.UnitID == us.UnitID && x.TransportID == trId);
                        if (existing != null)
                        {
                            if (us.Count > existing.Count) { existing.Count = us.Count; changed = true; }
                            if (trId > 0 && us.TransportCount > existing.TransportCount) { existing.TransportCount = us.TransportCount; changed = true; }
                            continue;
                        }
                        var s = SanitizedClone(us, db, dropped);
                        if (s != null) { slots.Add(s); changed = true; }
                    }
                }
                // decks saved before the real-stats limits: a card above the accepted number of units is trimmed (the F10 cheat applied afterwards still raises it)
                if (Realism.RealModeOn)   // always trimmed first: the F10 cheat below records these counts as originals
                    foreach (var sl in slots)
                        if (RealStats.MaxPerCard(sl.UnitID, out int lim) && sl.Count > lim)
                        {
                            if (RealStats.ClampLogged.Add(sl.UnitID)) Mod.Log.Msg($"[VRAIES STATS] carte {sl.UnitID} : {sl.Count} exemplaires ramenés à {lim} (limite réaliste)");
                            sl.Count = lim; changed = true;
                        }
                if (slots.Count == baseUsed) continue;

                // Leave free slots so mission scripts can still add their own reinforcements.
                int len = Math.Min(64, slots.Count + Math.Max(baseLen, 7));
                var arr = new Il2CppReferenceArray<DeckSlotModel>(len);
                for (int i = 0; i < len; i++)
                {
                    var s = i < slots.Count ? slots[i] : new DeckSlotModel();
                    s.Category = cat;
                    s.SlotIndex = i;
                    arr[i] = s;
                }
                Deck.ApplyResize(cloneI, cat, arr);
                var check = Deck.GetCategorySlots(cloneI, cat);
                if (check == null || check.Length != len) clone.GetSlots[cat] = arr;
            }
            if (Cheats.UnitsOn) { Cheats.ApplyUnitsPerCard(cloneI); changed = true; }
            if (dropped.Count > 0) Mod.Log.Warning("[CAMPAGNE] ignoré (absent de la base de la mission): " + string.Join(", ", dropped));
            var dlcList = dlcDropped.Distinct().ToList();
            DlcSkipped = dlcList.Count;
            if (dlcList.Count > 0)
                Mod.Log.Warning($"[DLC] deck CAMPAGNE : {dlcList.Count} carte(s) ignorée(s), DLC non possédé{(Dlc.Known ? "" : " (DLC non vérifiables)")} : {string.Join(", ", dlcList)}");
            return clone;
        }

        static bool HasWork() => UserDecks.Count > 0 || (Cheats.UnitsOn);
        internal static bool ShouldMerge() => HasWork();

        /// Hands a deck built by the mod to the game (our own SetDeck prefix is skipped).
        internal static void CommitDeck(int uid, DeckDataModel deck)
        {
            LastMerged = deck;
            Merged.Add(uid);
            Reentrant = true;
            try { SharedPlayerDeck.SetDeck(uid, new IDeckDataModel(deck.Pointer), out IDeckDataModel _); }
            finally { Reentrant = false; }
        }

        internal static void TryLateMerge()
        {
            if (!Mod.Actif.Value || MissionInerte) return;
            DlcNotice();
            var ctx = Ctx();
            int local = LocalUid;
            if (local < 0 && ctx != null)
            {
                try { var cp = ctx.CurrentPlayer; if (cp != null) local = cp.UID; } catch { }
            }

            if (!Merged.Contains(local))
            {
                bool hasDeck = local >= 0 && SharedPlayerDeck.ContainsDeck(local);
                string status = $"[ETAT] mission={MissionUid} decksJoueur={UserDecks.Count} partie={(ctx != null)} joueurLocal={local} deckEnPartie={hasDeck}";
                if (status != _lastStatus) { _lastStatus = status; Mod.Log.Msg(status + (HasWork() ? "" : " (rien à fusionner)")); }
            }

            if (local < 0 || Merged.Contains(local) || !HasWork()) return;
            if (!IsHuman(local, out var known) || !known) return;
            if (!SharedPlayerDeck.ContainsDeck(local)) return;
            if (!SharedPlayerDeck.TryGetDeck(local, out IDeckDataModel cur) || cur == null) return;

            Cheats.ForgetOriginals();
            var merged = BuildMerged(cur, out bool changed);
            if (!changed) { Merged.Add(local); Mod.Log.Msg("[CAMPAGNE] deck déjà complet, rien à fusionner"); return; }
            CommitDeck(local, merged);
            Mod.Log.Msg($"[CAMPAGNE] deck fusionné (tardif) pour uid {local}: {Mod.DeckStr(new IDeckDataModel(merged.Pointer))}");
        }

        /// Once per battle, when the player exists: tells the player what is active and logs calibration values.
        internal static void Announce()
        {
            if (_announced) return;
            var cp = Ctx()?.CurrentPlayer;
            if (cp == null) return;
            _announced = true;

            var db = DataBaseService._instance;
            if (Realism.Enabled.Value)
                Mod.Notify(Realism.IsAppliedTo(db) ? (Realism.RealModeOn ? new TxtMsg(TxtKey.N_REAL_STATS_ON) : new TxtMsg(TxtKey.N_REALISM_ON, Realism.PresetKey)) : new TxtMsg(TxtKey.N_REALISM_NOT_APPLIED));
            Mod.Notify(Cheats.Enabled.Value ? TxtKey.N_CHEAT_MENU_KEYS : TxtKey.N_CHEAT_MENU);
            if (Cheats.Enabled.Value) Cheats.NotifyKeys();
            if (Mod.PatchFailures.Count > 0) Mod.Notify(new TxtMsg(TxtKey.N_PATCH_FAILURES, Mod.PatchFailures.Count));

            try
            {
                var gc = GameController._instance;
                var ms = gc?._MapSettings_k__BackingField;
                var cfg = GameConfig.Instance;
                Mod.Log.Msg($"[CALIBRAGE] carte={(ms == null ? "?" : ms.MapSize.ToString())} ficheX={(cfg?.InfocardConfig?.EffectiveRangeMultiplier.ToString() ?? "?")} " +
                            $"altHaute={(cfg?.PlanesConfig?.HighAltitude.ToString() ?? "?")} altBasse={(cfg?.PlanesConfig?.LowAltitude.ToString() ?? "?")} heloLow={(cfg?.HelicopterLowMultiplier.ToString() ?? "?")}");
                Mod.Log.Msg($"[CALIBRAGE] économie: revenu/min={cfg?.DefaultIncomePerMin} entretien={cfg?.UpkeepMultiplier} couvertSnap={cfg?.CoverSnapDistance} forêtVitesseInf={cfg?.InfantryForestSpeedMultiplier}");
                var bc = cfg?.BuildingsConfig;
                if (bc != null) Mod.Log.Msg($"[CALIBRAGE] bâtiments: floor={bc.DamageModifierFloor} parSoldat={bc.DamageModifierPerSoldier} seuil={bc.BuildingDamageThreshold}");
                LogInfantryCalibration(cfg);
                var fow = cfg?.FogOfWarConfig;
                if (fow != null)
                {
                    Mod.Log.Msg($"[CALIBRAGE] brouillard: minDetect={fow.MinimumDetectionDistance} spotted={fow.SpottedPenalty} spottedBât={fow.SpottedPenaltyBuildings} sneakMin={fow.SneakMinAntivisible} forêtTirMax={fow.MaxShootableForestDistance} antiVisibleMax={fow.MaxAntiVisible}");
                    var ts = fow.TerrainTypeSettings;
                    for (int i = 0; i < (ts?.Length ?? 0); i++)
                    {
                        var t = ts[i];
                        if (t != null) Mod.Log.Msg($"[CALIBRAGE] terrain {t.TerrainType}: opacité={t.Opacity} couvert={t.Cover} hauteur={t.Height} mult={t.Multiplier}");
                    }
                }
                var ai = cfg?.AiConfig;
                if (ai != null)
                {
                    var tp = ai.TargetingDefaultPreset;
                    Mod.Log.Msg($"[CALIBRAGE] IA: botActif={AiConfig.IsBotAIEnabledInScenario} contreBatterie={ai.ChanceFireCounterBattery} fuiteAprèsTir={ai.AfterFireEscapeChance}/{ai.AfterFireEscapeRadius} cbVie={ai.CBTargetsLifeTime} regroupement={ai.GroupingMaxDistance} cooldownCible={tp.CooldownTime} coûtMinGroupe={tp.MinGroupCost}");
                }
                Mod.Log.Msg($"[CALIBRAGE] fumée auto des bots désactivée={SmokeAbilitySystem.GLOBAL_BOT_AUTO_SMOKE_DISABLED} autoRotation désactivée={AutoRotateSystem.GLOBAL_AUTO_ROTATE_DISABLED}");
                // flare trigger settings (never logged before): read before any change to the automatic release is proposed
                try
                {
                    var bs = cfg?.BattleSystemSettings;
                    if (bs != null)
                        Mod.Log.Msg($"[CALIBRAGE] leurres : délai auto hélicos={bs.AUTOFLARES_HELICOPTERS_DELAY} avions={bs.AUTOFLARES_PLANES_DELAY} ; leurres max comptés par missile au tir={bs.MAX_FLARES_COUNT_MISSILE_INITIAL} au nouveau tirage={bs.MAX_FLARES_COUNT_MISSILE_REROLL}");
                }
                catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] leurres : réglages illisibles : " + e.Message); }
                LogScaleCalibration(cfg);
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] indisponible: " + e.Message); }
        }

        /// Real-scale settings (v0.24.0), READ ONLY, once per battle: ballistics config and constants, plane, supply, loading, infantry
        /// and laser distances, the card's smallest sensor range. Each group has its own guard. The solver probe runs later in the battle
        /// (SanteCombat), once the ballistics helper holds its settings.
        static void LogScaleCalibration(GameConfig cfg)
        {
            var inv = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("0.#####", inv);
            try
            {
                var bs = cfg?.BattleSystemSettings;
                if (bs != null)
                {
                    Mod.Log.Msg($"[CALIBRAGE] balistique : G={F(bs.G)} bombesG x{F(bs.BOMBS_GRAVITY_MULTIPLYER)} bombesFreinéesG x{F(bs.HIGH_DRAG_BOMBS_GRAVITY_MULTIPLYER)} freinageBombes={F(bs.HIGH_DRAG_BOMBS_DECELERATION_PROPORTION)} " +
                                $"gravitéMissileAvantAllumage={F(bs.MISSILE_GRAVITY_ACCELERATION_BEFORE_MOTOR_ACTIVATION)} imprécisionViséeMax={F(bs.MAX_WEAPON_AIMING_IMPRECISION)} seuilContrôleVisée={F(bs.MAX_WEAPON_AIMING_CHECK_TRESHOLD)}");
                    Mod.Log.Msg($"[CALIBRAGE] trajectoires : artillerie distRéf {F(bs.MIN_ARTILLERY_HEIGHT_REFERENCE_DISTANCE)}-{F(bs.MAX_ARTILLERY_HEIGHT_REFERENCE_DISTANCE)} apogée {F(bs.MIN_ARTILLERY_SHOT_APOGEE)}-{F(bs.MAX_ARTILLERY_SHOT_APOGEE)} distApogéeMax={F(bs.MAX_ARTILLERY_SHOT_APOGEE_DISTANCE)} ; " +
                                $"mortier distRéf {F(bs.MIN_MORTAR_HEIGHT_REFERENCE_DISTANCE)}-{F(bs.MAX_MORTAR_HEIGHT_REFERENCE_DISTANCE)} apogée {F(bs.MIN_MORTAR_SHOT_APOGEE)}-{F(bs.MAX_MORTAR_SHOT_APOGEE)} ; missiles balistiques apoapside={F(bs.BALLISTIC_MISSILES_APOAPSIS)}");
                    Mod.Log.Msg($"[CALIBRAGE] missiles : décélération={F(bs.MISSILE_DECELERATION_PROPORTION)} vitesseDeMort={F(bs.MISSILE_DEATH_SPEED_PROPORTION)} ratéMin={F(bs.MINIMAL_MISSILE_MISS_DISTANCE)} ratéMaxPlafond={F(bs.MAX_MISSILE_MISS_DISTANCE_CAP)} " +
                                $"dégagementRaté={F(bs.MISSILE_MISS_ADDITIONAL_CLEARENCE)} tailleAvionObusNonGuidés={F(bs.ADDITIONAL_PLANE_SIZE_FOR_UNGUIDED_SHELLS)}");
                }
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] balistique : réglages illisibles : " + e.Message); }
            try
            {
                Mod.Log.Msg($"[CALIBRAGE] constantes balistiques : gravité par défaut x{F(Il2CppBrokenArrow.Shared.Ecs.BattleSystem.BattleSystemConstants.DEFAULT_GRAVITY_MULT)} " +
                            $"tir direct avion x{F(Il2CppBrokenArrow.Shared.Ecs.BattleSystem.BattleSystemConstants.PLANE_DIRECT_SHOT_GRAVITY_MULT)} " +
                            $"seuil de calcul statique={F(Il2CppBrokenArrow.Client.Ecs.BattleSystem.Ballistics.BallisticsCalculationsHelper.STATIC_CALCULATIONS_SPEED_THESHOLD)}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] constantes balistiques illisibles : " + e.Message); }
            try
            {
                var pc = cfg?.PlanesConfig;
                if (pc != null)
                    Mod.Log.Msg($"[CALIBRAGE] avions : mitraillage {F(pc.StrafeMinLength)}-{F(pc.StrafeMaxLength)} rechercheMitraillage={F(pc.StrafeSearchRadius)} décalageArme={F(pc.StrafeWeaponPointOffset)} " +
                                $"largage={F(pc.BombDropOffset)} largageFreinées={F(pc.BombDropOffsetHighDrag)} largageAoE x{F(pc.BombDropOffsetAmmoAoeMult)} avionContreAvion arrêt={F(pc.PlaneVsPlaneStopDistance)} fuite={F(pc.PlaneVsPlaneEscapeDistance)} " +
                                $"dégagementPrécision={pc.PrecisionFlyAwayExtraDistance}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] avions : réglages illisibles : " + e.Message); }
            try
            {
                var sc = cfg?.SupplyConfig;
                var ic = cfg?.InfantryConfig;
                Mod.Log.Msg($"[CALIBRAGE] distances : ravitaillement rayon={(sc == null ? "?" : F(sc.ResupplyRadius))} vue={(sc == null ? "?" : F(sc.ViewDistance))} ; " +
                            $"embarquement={cfg?.TransportLoadDistance} animé={cfg?.TransportLoadDistanceWithAnimation} ; " +
                            $"infanterie écartSoldats={(ic == null ? "?" : F(ic.DistanceBetweenSoldiers))} sprintDès={(ic == null ? "?" : F(ic.DistanceForSprint))} arrêtSoldat={(ic == null ? "?" : F(ic.DistanceForSoldierStop))}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] distances : réglages illisibles : " + e.Message); }
            try
            {
                Mod.Log.Msg($"[CALIBRAGE] laser : dégagementSol={F(Il2CppBrokenArrow.Client.Ecs.BattleSystem.Guidance.Systems.LaserDesignatorSystem.GROUND_CLEARANCE)} " +
                            $"dégagementDistance={F(Il2CppBrokenArrow.Client.Ecs.BattleSystem.Guidance.Systems.LaserDesignatorSystem.DISTANCE_CLEARANCE)} " +
                            $"diagonale={F(Il2CppBrokenArrow.Client.Ecs.BattleSystem.Guidance.Systems.LaserDesignatorSystem.DIAGONAL_LENGTH)} ; " +
                            $"fiche : portée de capteur minimale affichée={(cfg?.InfocardConfig == null ? "?" : F(cfg.InfocardConfig.MinimalSensorRange))} multiplicateur={(cfg?.InfocardConfig == null ? "?" : F(cfg.InfocardConfig.EffectiveRangeMultiplier))}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] laser et fiche : réglages illisibles : " + e.Message); }
        }

        /// Infantry speed, sprint, loading and damage-resist settings (v0.23.0), READ ONLY: the meaning of the sprint speed fields is
        /// unknown, so nothing here is ever written. Each group has its own guard, so one missing member never hides the others.
        static void LogInfantryCalibration(GameConfig cfg)
        {
            var inv = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("0.#####", inv);
            try { Mod.Log.Msg($"[CALIBRAGE] vitesses : km/h vers m/s (Mobility.KM_PER_HOUR_TO_MS)={F(Mobility.KM_PER_HOUR_TO_MS)}"); }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] vitesses : conversion km/h illisible : " + e.Message); }
            if (cfg == null) return;
            try
            {
                Mod.Log.Msg($"[CALIBRAGE] infanterie (jeu) : sprint={F(cfg.SprintSpeed)} forêtGlobal={F(cfg.GlobalForestSpeedMultiplier)} forêtInfanterie={F(cfg.InfantryForestSpeedMultiplier)} " +
                            $"sprintSortieBâtiment={F(cfg.HouseUnloadSprintMultiplier)} délaiSortieBâtiment={cfg.HouseUnloadDelay} distanceEmbarquement={cfg.TransportLoadDistance} " +
                            $"tempsMaxEmbarquementParUnité={F(cfg.MaxTimeForLoadOneUnit)} destructionBâtiment%={cfg.OnBuildingDestroyPrecentage}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] infanterie (jeu) : réglages illisibles : " + e.Message); }
            try
            {
                var ic = cfg.InfantryConfig;
                if (ic != null)
                    Mod.Log.Msg($"[CALIBRAGE] infanterie (soldats) : course={F(ic.RunSpeed)} sprint={F(ic.SprintSpeed)} courseAccroupi={F(ic.KneelRunSpeed)} courseCouché={F(ic.ProneRunSpeed)} " +
                                $"sprintCouché={F(ic.ProneSprintSpeed)} distancePourSprint={F(ic.DistanceForSprint)} tailleEscouadeBase={ic.BaseSquadSize}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] infanterie (soldats) : réglages illisibles : " + e.Message); }
            try
            {
                var bc = cfg.BuildingsConfig;
                if (bc != null)
                    Mod.Log.Msg($"[CALIBRAGE] bâtiments (dégâts) : plancher={F(bc.DamageModifierFloor)} parSoldat={F(bc.DamageModifierPerSoldier)} réductionCollision={F(bc.HitColliderReduction)}");
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] bâtiments (dégâts) : réglages illisibles : " + e.Message); }
            try
            {
                var buff = cfg.BuffConfig;
                if (buff == null) return;
                Mod.Log.Msg($"[CALIBRAGE] stress : seuil de dégâts sans résistance={F(buff.StressDamageModThreshold)} mobilité touchée (critique)={F(buff.MinorMobilityMultiplier)}");
                var sm = buff.StressModifiers;
                if (sm == null) { Mod.Log.Msg("[CALIBRAGE] stress : table des niveaux absente"); return; }
                var parts = new List<string>();
                foreach (var lvl in new[] { Il2CppBrokenArrow.Client.Ecs.BattleSystem.StressLevel.Calm, Il2CppBrokenArrow.Client.Ecs.BattleSystem.StressLevel.Shocked, Il2CppBrokenArrow.Client.Ecs.BattleSystem.StressLevel.Panicked })
                {
                    if (!sm.ContainsKey(lvl)) { parts.Add($"{lvl} absent"); continue; }
                    var m = sm[lvl];
                    parts.Add(m == null ? $"{lvl} vide" : $"{lvl} : résistance infanterie plancher={F(m.InfantryDmgModFloor)} parSoldat={F(m.InfantryDmgModPerSoldier)} vitesse={F(m.MovementSpeedMultiplier)} dispersionInf={F(m.DispersionInfMultiplier)}");
                }
                Mod.Log.Msg($"[CALIBRAGE] stress ({sm.Count} niveau(x)) : " + string.Join(" ; ", parts));
            }
            catch (Exception e) { Mod.Log.Msg("[CALIBRAGE] stress : réglages illisibles : " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(GameSessionContext), nameof(GameSessionContext.AddPlayer))]
    static class Patch_AddPlayer
    {
        static void Postfix(PlayerInfo player)
        {
            try
            {
                if (!Campaign.Refresh()) return;
                Campaign.NoteSession();
                if (player == null) return;
                Campaign.IsBotByUid[player.UID] = player.IsBot;
                Mod.Log.Msg($"[JOUEUR] uid={player.UID} '{player.PlayerName}' bot={player.IsBot} équipe={player.TeamName}");
            }
            catch (Exception e) { Mod.Log.Error("AddPlayer: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(GameSessionContext), nameof(GameSessionContext.SetCurrentPlayer))]
    static class Patch_SetCurrentPlayer
    {
        static void Postfix(int id)
        {
            try
            {
                if (!Campaign.Refresh()) return;
                Campaign.NoteSession();
                Campaign.LocalUid = id;
                Mod.Log.Msg($"[JOUEUR] joueur local uid={id}");
            }
            catch (Exception e) { Mod.Log.Error("SetCurrentPlayer: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(SharedPlayerDeck), nameof(SharedPlayerDeck.SetDeck))]
    static class Patch_SetDeck
    {
        static void Prefix(int playerUID, ref IDeckDataModel inputDeck)
        {
            try
            {
                if (Campaign.Reentrant || !Campaign.Refresh()) return;
                Campaign.NoteSession();
                bool human = Campaign.IsHuman(playerUID, out var known);
                Mod.Log.Msg($"[DECK] SetDeck uid={playerUID} connu={known} humain={human} deck={Mod.DeckStr(inputDeck)}");
                if (!Mod.Actif.Value || Campaign.MissionInerte || !human || inputDeck == null || !Campaign.ShouldMerge()) return;
                if (Campaign.LastMerged != null && inputDeck.Pointer == Campaign.LastMerged.Pointer) return;
                Cheats.ForgetOriginals();   // a fresh deck for the local human only: BuildMerged records new originals if F10 is ON

                var merged = Campaign.BuildMerged(inputDeck, out bool changed);
                if (!changed) { Campaign.Merged.Add(playerUID); return; }
                inputDeck = new IDeckDataModel(merged.Pointer);
                Campaign.LastMerged = merged;
                Campaign.Merged.Add(playerUID);
                Mod.Log.Msg($"[CAMPAGNE] deck fusionné pour uid {playerUID}: {Mod.DeckStr(inputDeck)}");
            }
            catch (Exception e) { Mod.Log.Error("SetDeck: " + e); }
        }
    }

    [HarmonyPatch(typeof(EscapeMenu), nameof(EscapeMenu.OnRestartMissionClick))]
    static class Patch_RestartFromMenu
    {
        static void Postfix() => Guard.Run("Restart", Campaign.OnRestart);
    }

    [HarmonyPatch(typeof(BattleEndScreen), nameof(BattleEndScreen.OnRestartClick))]
    static class Patch_RestartFromEnd
    {
        static void Postfix() => Guard.Run("Restart", Campaign.OnRestart);
    }
}
