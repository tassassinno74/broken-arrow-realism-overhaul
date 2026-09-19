// Identite: the mod's identity (name, version, author, links) and the startup checks that run before anything else.
//  - Easy Anti-Cheat loaded in the game process: a message in five languages (console/log and a Windows message box),
//    then the game closes. The anti-cheat itself is never read, called or changed: only the module list of our own process is looked at.
//  - Old mod file Mods/CampagneDeckLibre.dll present: a message in five languages, the mod stays inert for this session
//    (the game keeps running, no file is deleted).
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;
using MelonLoader.Utils;

namespace RealismOverhaul
{
    static class Identite
    {
        internal const string ModName = "Broken Arrow Realism Overhaul";
        internal const string Version = "1.1";
        internal const string Author = "tassassinno";
        internal const string SteamUrl = "https://steamcommunity.com/profiles/76561199350352727/";
        internal const string DiscordUrl = "https://discord.gg/hNUBQhXW8Z";
        internal const string OldDllName = "CampagneDeckLibre.dll";

        /// True when an Easy Anti-Cheat module is loaded in this game process: the mod does nothing and the game is being closed.
        internal static bool AntiCheatDetected { get; private set; }
        /// True when Mods/CampagneDeckLibre.dll exists: the mod does nothing for this session.
        internal static bool OldVersionPresent { get; private set; }
        /// Either check failed: no preference, no patch, no frame work for this session.
        internal static bool Blocked => AntiCheatDetected || OldVersionPresent;

        static bool _ran;
        static volatile bool _quitStarted;

        // ------------------------------------------------------------ texts (EN, FR, RU, DE, ZH)
        static readonly string[] AntiCheatText =
        {
            "Broken Arrow Realism Overhaul can't run while the anti-cheat is active.\n" +
            "Close this window, then start the game from Steam and choose the launch option \"Anti-Cheat Disabled\" (not \"Broken Arrow\").\n" +
            "The game will now close.",

            "Broken Arrow Realism Overhaul ne peut pas fonctionner avec l'anti-triche.\n" +
            "Ferme cette fenêtre, puis lance le jeu depuis Steam et choisis l'option de lancement « Anti-Cheat Disabled » (et non « Broken Arrow »).\n" +
            "Le jeu va maintenant se fermer.",

            "Broken Arrow Realism Overhaul не работает при включённом античите.\n" +
            "Закройте это окно и запустите игру из Steam, выбрав вариант запуска «Anti-Cheat Disabled» (а не «Broken Arrow»).\n" +
            "Сейчас игра закроется.",

            "Broken Arrow Realism Overhaul funktioniert nicht, solange der Anti-Cheat aktiv ist.\n" +
            "Schließe dieses Fenster und starte das Spiel über Steam mit der Startoption „Anti-Cheat Disabled“ (nicht „Broken Arrow“).\n" +
            "Das Spiel wird jetzt beendet.",

            "Broken Arrow Realism Overhaul 无法在反作弊开启的情况下运行。\n" +
            "请关闭此窗口，然后在 Steam 中启动游戏时选择启动选项“Anti-Cheat Disabled”（不要选“Broken Arrow”）。\n" +
            "游戏即将关闭。",
        };

        static readonly string[] OldVersionText =
        {
            "An old version of the mod ({0}) is still in the Mods folder.\n" +
            "Delete that file, then restart the game.\n" +
            "Until then, Broken Arrow Realism Overhaul stays off.",

            "Une ancienne version du mod ({0}) est encore dans le dossier Mods.\n" +
            "Supprime ce fichier, puis relance le jeu.\n" +
            "En attendant, Broken Arrow Realism Overhaul reste désactivé.",

            "В папке Mods осталась старая версия мода ({0}).\n" +
            "Удалите этот файл и перезапустите игру.\n" +
            "До этого Broken Arrow Realism Overhaul работать не будет.",

            "Im Mods-Ordner liegt noch eine alte Version der Mod ({0}).\n" +
            "Lösche diese Datei und starte das Spiel neu.\n" +
            "Bis dahin bleibt Broken Arrow Realism Overhaul inaktiv.",

            "Mods 文件夹中还有旧版本的模组（{0}）。\n" +
            "请删除该文件，然后重新启动游戏。\n" +
            "在此之前，Broken Arrow Realism Overhaul 不会生效。",
        };

        // ------------------------------------------------------------ Windows message box (user32, no extra program)
        const uint MB_OK = 0x00000000;
        const uint MB_ICONWARNING = 0x00000030;
        const uint MB_SETFOREGROUND = 0x00010000;
        const uint MB_TOPMOST = 0x00040000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        // ============================================================ entry points (Mod.OnInitializeMelon)
        /// The two credit lines of the startup log.
        internal static void LogCredits(MelonLogger.Instance log)
        {
            log ??= new MelonLogger.Instance(ModName);
            log.Msg("Créateur : " + Author);
            log.Msg($"Steam : {SteamUrl} | Discord : {DiscordUrl}");
        }

        /// Runs the startup checks once, before any preference or patch. Returns Blocked: the caller must then do nothing else.
        internal static bool RunStartupChecks(MelonLogger.Instance log)
        {
            if (_ran) return Blocked;
            _ran = true;
            log ??= new MelonLogger.Instance(ModName);

            AntiCheatDetected = DetectAntiCheat();
            if (AntiCheatDetected)
            {
                try { StopForAntiCheat(log); }
                catch (Exception e) { try { log.Error("[SECURITE] fermeture du jeu : " + e.GetBaseException().Message); } catch { } StartKillTimer(log); }
                return true;
            }

            string oldPath = null;
            try { oldPath = FindOldVersion(); }
            catch (Exception e) { log.Warning("[INSTALLATION] vérification de l'ancienne version impossible : " + e.GetBaseException().Message); }
            if (oldPath != null)
            {
                OldVersionPresent = true;
                try
                {
                    // the file name is put in at run time, so it is written only once in the mod (OldDllName)
                    var texts = Array.ConvertAll(OldVersionText, t => t.Replace("{0}", OldDllName));
                    Banner(log, false, "OLD VERSION OF THE MOD FOUND / ANCIENNE VERSION DU MOD TROUVÉE", texts, oldPath);
                    ShowBox(Join(texts) + "\n\n" + oldPath);
                    log.Warning("[INSTALLATION] mod inactif pour cette session : supprime " + oldPath + " puis relance le jeu");
                }
                catch (Exception e) { try { log.Warning("[INSTALLATION] message impossible : " + e.GetBaseException().Message); } catch { } }
            }
            return Blocked;
        }

        // ============================================================ anti-cheat
        /// True when an Easy Anti-Cheat module is loaded in this game process (the online launch). Only our own process is inspected.
        static bool DetectAntiCheat()
        {
            try
            {
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                    if ((m.ModuleName ?? "").IndexOf("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        /// Message (log and blocking box), then a normal game exit; a forced exit only if the game has not started closing 3 s later.
        static void StopForAntiCheat(MelonLogger.Instance log)
        {
            Banner(log, true, "ANTI-CHEAT DETECTED / ANTI-TRICHE DÉTECTÉ", AntiCheatText, null);
            ShowBox(Join(AntiCheatText));
            log.Error("[SECURITE] anti-triche chargé : le mod ne fait rien et le jeu se ferme");
            try { MelonEvents.OnApplicationQuit.Subscribe(OnQuitStarted, 0, true); } catch { }
            try { UnityEngine.Application.Quit(); }
            catch (Exception e) { log.Warning("[SECURITE] fermeture normale impossible : " + e.GetBaseException().Message); }
            StartKillTimer(log);
        }

        static void OnQuitStarted() => _quitStarted = true;

        /// Background thread, never touches Unity: 3 s without the game closing, then the process ends.
        /// Once the normal exit has started it gets 15 s in all before the process is ended.
        static void StartKillTimer(MelonLogger.Instance log)
        {
            try
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        Thread.Sleep(3000);
                        if (_quitStarted) Thread.Sleep(12000);
                        try { log?.Error("[SECURITE] le jeu ne s'est pas fermé : arrêt forcé"); } catch { }
                        Process.GetCurrentProcess().Kill();
                    }
                    catch { }
                })
                { IsBackground = true, Name = "RealismOverhaul_Fermeture" };
                t.Start();
            }
            catch (Exception e) { try { log?.Error("[SECURITE] minuterie de fermeture impossible : " + e.GetBaseException().Message); } catch { } }
        }

        // ============================================================ old mod file
        /// Full path of Mods/CampagneDeckLibre.dll when it exists (and is not this very assembly), else null.
        static string FindOldVersion()
        {
            string dir = MelonEnvironment.ModsDirectory;
            if (string.IsNullOrEmpty(dir)) return null;
            string path = Path.Combine(dir, OldDllName);
            if (!File.Exists(path)) return null;
            string self = null;
            try { self = typeof(Identite).Assembly.Location; } catch { }
            if (!string.IsNullOrEmpty(self) && string.Equals(Path.GetFullPath(self), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return null;
            return path;
        }

        // ============================================================ output helpers
        static string Join(string[] blocks) => string.Join("\n\n", blocks);

        static void Banner(MelonLogger.Instance log, bool error, string title, string[] blocks, string extra)
        {
            const string rule = "==================================================================";
            void Line(string s) { if (error) log.Error(s); else log.Warning(s); }
            Line(rule);
            Line("  " + ModName + " : " + title);
            Line(rule);
            foreach (var b in blocks)
            {
                foreach (var l in b.Split('\n')) Line("  " + l);
                Line("  ------------------------------------------------------------");
            }
            if (!string.IsNullOrEmpty(extra)) Line("  " + extra);
            Line(rule);
        }

        static void ShowBox(string text)
        {
            try { MessageBoxW(IntPtr.Zero, text, ModName, MB_OK | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND); }
            catch { }
        }
    }
}
