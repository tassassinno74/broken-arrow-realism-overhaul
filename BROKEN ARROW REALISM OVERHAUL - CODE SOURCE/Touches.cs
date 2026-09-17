// Game key map for the cheat hotkeys (no Harmony patch, read-only).
// Builds the table of keys the game already uses and picks free keys for the mod's cheats:
//  - every static Hotkey of HotkeySettings (configurable, debug, SelectAll/SubSelect) and HotkeySettings.SelectionGroups;
//  - SettingsService._hotkeys (the player's own rebinds from Options > Commandes);
//  - GameConfig.Instance.CameraConfig.PreventCameraZoomKey;
//  - a fixed set of system keys (Escape, Enter, Tab, Space, Backquote, digits, modifiers, numpad, OEM1-5, PrintScreen, F12).
// KeyMapper.IsReservedKey and ClickByHotKey components are logged for information only and never block a key.
// Every method must be called from the Unity main thread (IL2CPP statics, services, Resources).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using Key = UnityEngine.InputSystem.Key;
using Hotkey = Il2CppBrokenArrow.Client.Ecs.Input.Hotkey;
using HotkeySettings = Il2CppBrokenArrow.Client.Ecs.Input.HotkeySettings;
using KeyMapper = Il2CppBrokenArrow.Client.Ecs.Input.KeyMapper;
using SettingsService = Il2CppBrokenArrow.Client.Ecs.GameSettings.Services.SettingsService;
using GameConfig = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using ClickByHotKey = Il2CppBrokenArrow.MissionEditor.UI.ClickByHotKey;

namespace RealismOverhaul
{
    static class GameKeys
    {
        const string Reserved = "touche réservée";

        /// Replacement pool for a cheat key that is taken, in order of preference.
        internal static readonly string[] Candidates =
        {
            "F5", "F6", "F7", "F8", "F9", "F10", "Insert", "Delete", "Home", "End", "PageDown", "ScrollLock", "Pause"
        };

        // Keys never given to a cheat. Names are parsed at runtime so a missing enum member can never break the build.
        static readonly string[] FixedNames =
        {
            "Escape", "Enter", "Tab", "Space", "Backquote",
            "Digit0", "Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9",
            "LeftShift", "RightShift", "LeftAlt", "RightAlt", "AltGr", "LeftCtrl", "RightCtrl",
            "LeftMeta", "RightMeta", "LeftWindows", "RightWindows", "LeftApple", "RightApple", "LeftCommand", "RightCommand",
            "OEM1", "OEM2", "OEM3", "OEM4", "OEM5", "PrintScreen", "F12", "NumLock"
        };

        static Dictionary<Key, List<string>> _taken;                 // key -> names of what holds it (null until the first Rebuild)
        static bool _mapLogged;
        static string _lastSummary;
        static readonly HashSet<string> _once = new();
        static readonly Dictionary<string, long> _warnNext = new();
        static SettingsService _svc;
        static long _svcNext;

        /// True after a Rebuild that could read at least one game binding source (HotkeySettings or Options).
        internal static bool Ready { get; private set; }

        // ------------------------------------------------------------ table

        /// Rebuilds the taken-key table from every game source. A failing source is logged once and skipped.
        internal static void Rebuild()
        {
            try
            {
                var map = new Dictionary<Key, List<string>>();
                int fromStatic = -1, fromGroups = -1, fromOptions = -1, fromCamera = -1;

                // a) every public static Hotkey property of HotkeySettings
                try
                {
                    int n = 0;
                    foreach (var p in typeof(HotkeySettings).GetProperties(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (p.PropertyType != typeof(Hotkey) || p.GetIndexParameters().Length != 0) continue;
                        Hotkey h;
                        try { h = p.GetValue(null) as Hotkey; }
                        catch (Exception pe) { WarnOnce("prop:" + p.Name, $"[TOUCHES] raccourci du jeu {p.Name} illisible : {Msg(pe)}"); continue; }
                        if (h != null) n += AddHotkey(map, h, p.Name);
                    }
                    fromStatic = n;
                }
                catch (Exception e1) { WarnOnce("src:static", "[TOUCHES] raccourcis du jeu (HotkeySettings) illisibles, ignorés : " + Msg(e1)); }

                // a') selection groups (not saved in the options)
                try
                {
                    int n = 0;
                    var groups = HotkeySettings.SelectionGroups;
                    if (groups != null)
                        for (int i = 0; i < groups.Length; i++)
                        {
                            var g = groups[i];
                            if (g != null) n += AddHotkey(map, g, "SelectionGroups[" + i + "]");
                        }
                    fromGroups = n;
                }
                catch (Exception e2) { WarnOnce("src:groups", "[TOUCHES] groupes de sélection du jeu illisibles, ignorés : " + Msg(e2)); }

                // b) the player's bindings from Options > Commandes (authoritative for rebinds)
                try
                {
                    var dict = Mod.Svc<SettingsService>()?._hotkeys;
                    if (dict != null)
                    {
                        int n = 0;
                        foreach (var kv in dict)
                        {
                            string name = kv.Key.ToString();
                            var v = kv.Value;
                            n += Add(map, v.Primary, name);
                            n += Add(map, v.Secondary, name);
                        }
                        fromOptions = n;
                    }
                }
                catch (Exception e3) { WarnOnce("src:options", "[TOUCHES] touches des options du jeu (SettingsService) illisibles, ignorées : " + Msg(e3)); }

                // c) camera zoom lock key
                try
                {
                    var cam = GameConfig.Instance?.CameraConfig;
                    if (cam != null) fromCamera = Add(map, cam.PreventCameraZoomKey, "CameraConfig.PreventCameraZoomKey");
                }
                catch (Exception e4) { WarnOnce("src:camera", "[TOUCHES] touche de blocage du zoom caméra illisible, ignorée : " + Msg(e4)); }

                // d) fixed system keys + every numpad key
                try
                {
                    foreach (var fixedName in FixedNames)
                        if (TryKey(fixedName, out var fk)) Add(map, fk, Reserved);
                    foreach (var enumName in Enum.GetNames(typeof(Key)))
                        if (enumName.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && TryKey(enumName, out var nk)) Add(map, nk, Reserved);
                }
                catch (Exception e5) { WarnOnce("src:fixed", "[TOUCHES] liste des touches réservées incomplète : " + Msg(e5)); }

                bool ok = fromStatic > 0 || fromOptions > 0;
                if (!ok && Ready && _taken != null)
                {
                    // a previous full read exists: keep it rather than fall back to the reserved keys only
                    Warn("keepold", "[TOUCHES] relecture des touches du jeu incomplète : l'ancienne table est gardée");
                    return;
                }
                _taken = map;
                Ready = ok;

                string summary = $"[TOUCHES] table des touches du jeu : {map.Count} touche(s) prise(s) (raccourcis {Src(fromStatic)}, groupes {Src(fromGroups)}, options {Src(fromOptions)}, caméra {Src(fromCamera)})";
                if (summary != _lastSummary)
                {
                    _lastSummary = summary;
                    if (Ready) Mod.Log.Msg(summary);
                    else Warn("notready", summary + " : lecture incomplète, seules les touches réservées sont connues");
                }
            }
            catch (Exception e)
            {
                if (_taken == null) _taken = new Dictionary<Key, List<string>>();
                Warn("rebuild", "[TOUCHES] construction de la table des touches impossible : " + Msg(e));
            }
        }

        static int Add(Dictionary<Key, List<string>> map, Key k, string holder)
        {
            if (k == Key.None) return 0;
            if (!map.TryGetValue(k, out var list))
            {
                list = new List<string>();
                map[k] = list;
            }
            if (!list.Contains(holder)) list.Add(holder);
            return 1;
        }

        static int AddHotkey(Dictionary<Key, List<string>> map, Hotkey h, string holder)
        {
            int n = 0;
            try { n += Add(map, h.PrimaryKeyCode, holder); }
            catch (Exception e) { WarnOnce("hk1:" + holder, $"[TOUCHES] touche principale de {holder} illisible : {Msg(e)}"); }
            try { n += Add(map, h.SecondaryKeyCode, holder); }
            catch (Exception e) { WarnOnce("hk2:" + holder, $"[TOUCHES] touche secondaire de {holder} illisible : {Msg(e)}"); }
            return n;
        }

        // ------------------------------------------------------------ queries

        /// Parses a Unity key name (F8, Home, PageDown...). Numbers, flag lists and Key.None are refused.
        internal static bool TryKey(string name, out Key k)
        {
            k = Key.None;
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            if (!char.IsLetter(name[0]) || name.IndexOf(',') >= 0) return false;
            return Enum.TryParse<Key>(name, true, out k) && k != Key.None && Enum.IsDefined(typeof(Key), k);
        }

        /// Name(s) of what holds this key in the game, or null when the key is free (or not a valid key name).
        internal static string HolderOf(string keyName)
        {
            try
            {
                if (!TryKey(keyName, out var k)) return null;
                if (_taken == null) Rebuild();
                return HolderOfKey(k);
            }
            catch { return null; }
        }

        static string HolderOfKey(Key k)
        {
            var map = _taken;
            if (map == null || !map.TryGetValue(k, out var list) || list.Count == 0) return null;
            return string.Join(", ", list);
        }

        /// Returns the key name a cheat should use. alreadyAssigned = keys already given to the OTHER cheats.
        /// wanted is kept when it is a valid key, free in the game and not given to another cheat; otherwise the first
        /// free candidate is returned. When no candidate is free, wanted is kept and a warning is logged.
        internal static string Resolve(string wanted, string cheatLabel, ICollection<string> alreadyAssigned)
        {
            string w = (wanted ?? "").Trim();
            try
            {
                if (_taken == null || !Ready) Rebuild();

                var assigned = new HashSet<Key>();
                if (alreadyAssigned != null)
                    foreach (var a in alreadyAssigned)
                        if (TryKey(a, out var ak)) assigned.Add(ak);

                string reason = null;
                if (!TryKey(w, out var k)) reason = $"touche '{w}' inconnue";
                else
                {
                    string holder = HolderOfKey(k);
                    if (holder != null) reason = $"{w} déjà utilisée par le jeu ({holder})";
                    else if (assigned.Contains(k)) reason = $"{w} déjà prise par une autre triche";
                }
                if (reason == null) return w;

                foreach (var name in Candidates)
                {
                    if (!TryKey(name, out var c)) continue;
                    if (HolderOfKey(c) != null || assigned.Contains(c)) continue;
                    LogOnce($"[TOUCHES] {cheatLabel} : {reason} -> {name}", warning: true);
                    return name;
                }

                LogOnce($"[TOUCHES] {cheatLabel} : {reason}, et aucune touche libre parmi {string.Join(", ", Candidates)} : {w} est gardée", warning: true);
                return w;
            }
            catch (Exception e)
            {
                Warn("resolve:" + cheatLabel, $"[TOUCHES] {cheatLabel} : vérification de la touche {w} impossible, touche gardée : {Msg(e)}");
                return w;
            }
        }

        /// True while the game's settings screen (where keys are captured) is open. False on any error.
        internal static bool CaptureScreenOpen()
        {
            try
            {
                long now = Environment.TickCount64;
                if (now >= _svcNext)
                {
                    _svcNext = now + 2000;                                     // service lookup at most every 2 s
                    _svc = Mod.Svc<SettingsService>();
                }
                return _svc != null && _svc.UiSettingsOpened;
            }
            catch (Exception e)
            {
                _svc = null;
                Warn("capture", "[TOUCHES] état de l'écran des options illisible : " + Msg(e));
                return false;
            }
        }

        // ------------------------------------------------------------ diagnostics

        /// Logs once per game launch the full game key map, KeyMapper.IsReservedKey for the candidates (F5..F10 first)
        /// and the active ClickByHotKey buttons. Retried later while the table cannot be read.
        internal static void LogMapOnce()
        {
            if (_mapLogged) return;
            try
            {
                if (!Ready) Rebuild();
                var map = _taken;
                if (!Ready || map == null)
                {
                    Warn("map", "[TOUCHES] carte des touches du jeu pas encore lisible : nouvel essai plus tard");
                    return;
                }
                _mapLogged = true;

                var reservedOnly = new List<string>();
                var sb = new StringBuilder();
                sb.Append("[TOUCHES] carte des touches du jeu (").Append(map.Count).Append(" touches) :");
                foreach (var kv in map.OrderBy(x => x.Key.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    if (kv.Value.Count == 1 && kv.Value[0] == Reserved) { reservedOnly.Add(kv.Key.ToString()); continue; }
                    sb.Append("\n    ").Append(kv.Key.ToString()).Append(" (").Append((int)kv.Key).Append(") -> ").Append(string.Join(", ", kv.Value));
                }
                Mod.Log.Msg(sb.ToString());
                Mod.Log.Msg("[TOUCHES] touches réservées d'office : " + string.Join(", ", reservedOnly));

                var rs = new List<string>();
                foreach (var name in Candidates)
                {
                    if (!TryKey(name, out var ck)) { rs.Add(name + "=?"); continue; }
                    string r;
                    try { r = KeyMapper.IsReservedKey(ck) ? "oui" : "non"; }
                    catch (Exception re) { r = "erreur (" + re.Message + ")"; }
                    rs.Add(name + "=" + r);
                }
                Mod.Log.Msg("[TOUCHES] KeyMapper.IsReservedKey (pour info, ne bloque rien) : " + string.Join(", ", rs));

                try
                {
                    var found = new List<string>();
                    int total = 0;
                    foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<ClickByHotKey>()))
                    {
                        var cb = o?.TryCast<ClickByHotKey>();
                        if (cb == null || !cb.IsActive) continue;
                        total++;
                        if (found.Count >= 30) continue;
                        string s = cb._key.ToString();
                        if (cb._useAdditionalKey) s += " (en plus : " + cb._additionalKey.ToString() + ")";
                        found.Add(cb.name + "=" + s);
                    }
                    Mod.Log.Msg("[TOUCHES] boutons à raccourci actifs (ClickByHotKey, pour info) : "
                        + (total == 0 ? "aucun" : string.Join(", ", found) + (total > found.Count ? $" ... ({total} au total)" : "")));
                }
                catch (Exception ce) { WarnOnce("src:click", "[TOUCHES] boutons à raccourci (ClickByHotKey) illisibles, pour info seulement : " + Msg(ce)); }
            }
            catch (Exception e)
            {
                Warn("maplog", "[TOUCHES] affichage de la carte des touches impossible : " + Msg(e));
            }
        }

        // ------------------------------------------------------------ logging helpers

        static string Src(int n) => n < 0 ? "non lus" : n.ToString();

        static string Msg(Exception e) => (e is TargetInvocationException && e.InnerException != null ? e.InnerException : e).Message;

        static void LogOnce(string msg, bool warning)
        {
            if (_once.Count > 500) _once.Clear();
            if (!_once.Add(msg)) return;
            if (warning) Mod.Log.Warning(msg); else Mod.Log.Msg(msg);
        }

        static void WarnOnce(string key, string msg)
        {
            if (_once.Count > 500) _once.Clear();
            if (_once.Add("warn:" + key)) Mod.Log.Warning(msg);
        }

        /// Rate-limited warning: at most one per key every 60 s.
        static void Warn(string key, string msg)
        {
            long now = Environment.TickCount64;
            if (_warnNext.TryGetValue(key, out var next) && now < next) return;
            _warnNext[key] = now + 60000;
            Mod.Log.Warning(msg);
        }
    }
}
