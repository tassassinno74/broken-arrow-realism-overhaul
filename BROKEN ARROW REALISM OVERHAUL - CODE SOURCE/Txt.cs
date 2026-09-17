// Txt: every text the mod shows in the game (on-screen notices, Mod tab, order buttons and their hints, cheat labels, division
// description) in English, French, Russian, German and Simplified Chinese.
//  - The language follows the game's own UI language (LocalizationService.CurrentLanguage), read every 2 s on the main thread by
//    Mod.OnUpdate. Game languages without a table here (Spanish, Polish, Ukrainian, Japanese...) get English; so does the time before
//    the first successful read.
//  - One key per text, one table row per key. French is the source: every other language must use exactly the same {n} placeholders,
//    otherwise English (then French) is used for that key. The table is checked once when the class loads; problems are logged at the
//    first poll.
//  - Logs never go through this table: they stay French. Mod.Notify logs the French text and shows the text in the game's language.
//  - T(), Get() and Fr() are plain array reads (no allocation, no lock), safe from any thread. Poll() calls a game service: main thread only.
//  - Names that stay the same in every language: CAMPAGNE RUSSIE 1/2, CAMPAGNE USA 1/2, "Mod", Steam, Discord, key names, weapon names.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace RealismOverhaul
{
    enum Lang : byte { EN, FR, RU, DE, ZH }

    /// Text keys, in table order. Count must stay last.
    enum TxtKey : ushort
    {
        // Mod tab
        TAB_MOD, MT_TITLE_CHEATS, MT_TITLE_CHEATS_SOLO, MT_TITLE_ABOUT,
        ST_ON, ST_OFF, ST_CLICK, ST_UNAVAILABLE, ST_OPEN,
        MT_CHEAT_FALLBACK, MT_CHEAT_DESC_NOKEY, MT_CHEAT_DESC_KEY, MT_DEFAULT_TITLE, MT_DEFAULT_BODY,
        MT_UNAVAILABLE_HERE, MT_STATE_UNREADABLE, MT_ABOUT_LABEL, MT_ABOUT_DESC, MT_LINK_STEAM_DESC, MT_LINK_DISCORD_DESC,
        // cheat labels, descriptions and refusal reasons
        CH_LABEL_AMMO, CH_LABEL_FUEL, CH_LABEL_UNITS, CH_LABEL_TOUGH, CH_LABEL_MONEY, CH_LABEL_HEAL, CH_LABEL_CARDS,
        CH_DESC_AMMO, CH_DESC_FUEL, CH_DESC_UNITS, CH_DESC_TOUGH, CH_DESC_MONEY, CH_DESC_HEAL, CH_DESC_CARDS,
        CH_WHY_MOD_OFF, CH_WHY_TUTO, CH_WHY_ANTICHEAT, CH_WHY_NOT_CAMPAIGN, CH_WHY_NO_MISSION, CH_WHY_ONLINE, CH_WHY_UNREADABLE,
        // cheat notices
        N_CHEAT_KEYS, N_CHEAT_UNAVAILABLE, N_CHEAT_MENU, N_CHEAT_MENU_KEYS,
        N_AMMO_ON, N_AMMO_OFF, N_FUEL_ON, N_FUEL_OFF, N_UNITS_ON, N_UNITS_OFF, N_UNITS_NOT_READY,
        N_TOUGH_ON, N_TOUGH_OFF, N_TOUGH_ERRORS, N_TOUGH_IMPOSSIBLE,
        N_MONEY_NO_MISSION, N_MONEY_REQ_UNREADABLE, N_MONEY_ADDED, N_MONEY_CAPPED, N_MONEY_REQ_UNVERIFIED, N_MONEY_NO_PLAYER,
        N_MONEY_NO_EFFECT, N_MONEY_FAILED,
        N_HEAL_NO_MISSION, N_HEAL_LIST_UNREADABLE, N_HEAL_STATE_UNREADABLE, N_HEAL_NO_UNITS, N_HEAL_UNAVAILABLE, N_HEAL_FAILED, N_HEAL_DONE,
        N_CARDS_NO_MISSION, N_CARDS_UNAVAILABLE, N_CARDS_FAILED, N_CARDS_DONE,
        N_AMMO_UNAVAILABLE, N_FUEL_UNAVAILABLE, N_AMMOFUEL_UNAVAILABLE,
        // campaign, DLC and division
        N_TUTO_NO_MOD, N_DLC_REMOVED, N_DLC_REMOVED_UNKNOWN, N_REAL_STATS_ON, N_REALISM_ON, N_REALISM_NOT_APPLIED,
        RL_PRESET_REAL, RL_PRESET_MODERATE, RL_PRESET_CUSTOM, N_PATCH_FAILURES, DIV_DESC,
        // order panel
        OR_CB_TITLE, OR_CB_DESC, OR_FAV_TITLE, OR_FAV_DESC, OR_AA_TITLE, OR_AA_DESC, OR_RIP_TITLE, OR_RIP_DESC, OR_DEF_TITLE, OR_DEF_DESC,
        OR_DEBC_TITLE, OR_DEBC_DESC, OR_RAVX_TITLE, OR_RAVX_DESC, OR_GAR_TITLE, OR_GAR_DESC, OR_RZ_TITLE, OR_RZ_DESC,
        OR_HINT_ON, OR_HINT_OFF, N_OR_TOGGLE_ON, N_OR_TOGGLE_OFF, N_RAV_INTERRUPTED,
        N_OR_ACTION_DONE, N_OR_ACTION_PARTIAL, N_OR_ACTION_NONE, OR_NOTHING,
        OR_FAIL_NO_SUPPLY, OR_FAIL_REFUSED, OR_FAIL_NO_BUILDING, OR_FAIL_NO_ZONE,
        // module safety notices
        N_AH_OFF_SAFETY, N_AH_OBSERVE_ONLY, N_AH_RULES_OFF, N_AH_RULES_BACK, N_AH_RULES_OFF_3MIN, N_AFFUTS_OFF,
        N_HALF_OFF_INTERRUPTED, N_HALF_REMEASURE, N_HALF_ACTIVE, N_HALF_OFF_ERRORS, N_HALF_OFF_BATTLE, N_HALF_MEASURED, N_HALF_OFF_FORMULA,
        N_COVER_OFF_ERRORS, N_COVER_OFF_SAFETY, N_AI_DEFENSE, N_AI_ATTACK, N_AI_MEETING,
        N_FLARES_ACTIVE, N_FLARES_OFF_SAFETY, N_FLARES_DROP_OFF_INTERRUPTED, N_FLARES_OFF_BATTLE, N_FLARES_OFF_SESSION,
        N_FLARES_TRIAL_NEXT, N_FLARES_DROP_CONFIRMED, N_FLARES_DROP_OFF,
        N_MISSIONS_PAUSE, MS_WHY_INTERRUPTED, MS_WHY_HOOK_ERRORS, MS_WHY_HOOKS_INCOMPLETE, MS_WHY_HOOKS_FAILED, MS_WHY_SCRIPT_UNREADABLE,
        N_AA_REALISTIC_OFF, N_S400_OFF, N_FAV_NO_VISIBILITY,
        N_BALLISTICS_CUT, N_BALLISTICS_OFF_VERSION, N_RANGES_SUSPENDED, N_RANGES_WATCHED,
        N_PRECISION_OFF_BATTLE, N_PRECISION_NOT_LOADED,
        Count
    }

    /// One argument of a text: plain text (numbers already formatted with the invariant culture) or a key read in the same language.
    readonly struct TxtArg
    {
        readonly string _s;
        readonly TxtKey _k;
        readonly bool _isKey;

        TxtArg(string s) { _s = s ?? ""; _k = default; _isKey = false; }
        TxtArg(TxtKey k) { _s = null; _k = k; _isKey = true; }

        public static implicit operator TxtArg(string s) => new TxtArg(s);
        public static implicit operator TxtArg(TxtKey k) => new TxtArg(k);
        public static implicit operator TxtArg(int v) => new TxtArg(v.ToString(CultureInfo.InvariantCulture));

        internal string In(Lang l) => _isKey ? Txt.Get(l, _k) : _s ?? "";
    }

    /// A text key with its arguments, formatted in any language on demand.
    readonly struct TxtMsg
    {
        internal readonly TxtKey Key;
        internal readonly TxtArg A0, A1, A2, A3;
        internal readonly bool Set;

        public TxtMsg(TxtKey key, TxtArg a0 = default, TxtArg a1 = default, TxtArg a2 = default, TxtArg a3 = default)
        {
            Key = key; A0 = a0; A1 = a1; A2 = a2; A3 = a3; Set = true;
        }

        public static implicit operator TxtMsg(TxtKey k) => new TxtMsg(k);

        internal string In(Lang l) => Txt.Format(l, Key, A0, A1, A2, A3);
        internal string Fr => In(Lang.FR);
        internal string Local => In(Txt.Current);
    }

    static class Txt
    {
        const int Langs = 5, MaxArgs = 4;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string[][] _t;
        static int[] _argc;
        static readonly List<string> _problems = new();
        static readonly int[] _translated = new int[Langs];
        static volatile int _lang = (int)Lang.EN;
        static int _epoch;
        static string _lastRaw;
        static bool _firstPoll = true, _unreadLogged;

        internal static Lang Current => (Lang)_lang;
        /// Changes each time the language changes: cached UI texts compare it to know when to rebuild.
        internal static int Epoch => Volatile.Read(ref _epoch);

        internal static string Get(Lang l, TxtKey k)
        {
            int i = (int)k;
            var t = _t;
            if (t == null || (uint)i >= (uint)TxtKey.Count || (uint)l >= Langs) return k.ToString();
            return t[(int)l][i];
        }

        internal static string T(TxtKey k) => Get(Current, k);
        internal static string Fr(TxtKey k) => Get(Lang.FR, k);

        /// The text of a key in a language with its arguments; the raw text when formatting is impossible.
        internal static string Format(Lang l, TxtKey k, TxtArg a0 = default, TxtArg a1 = default, TxtArg a2 = default, TxtArg a3 = default)
        {
            string f = Get(l, k);
            int i = (int)k;
            int argc = _argc != null && (uint)i < (uint)_argc.Length ? _argc[i] : 0;
            if (argc == 0) return f;
            try
            {
                switch (argc)
                {
                    case 1: return string.Format(Inv, f, a0.In(l));
                    case 2: return string.Format(Inv, f, a0.In(l), a1.In(l));
                    case 3: return string.Format(Inv, f, a0.In(l), a1.In(l), a2.In(l));
                    default: return string.Format(Inv, f, a0.In(l), a1.In(l), a2.In(l), a3.In(l));
                }
            }
            catch { return f; }
        }

        /// Format in the current language.
        internal static string TF(TxtKey k, TxtArg a0 = default, TxtArg a1 = default, TxtArg a2 = default, TxtArg a3 = default)
            => Format(Current, k, a0, a1, a2, a3);

        // ------------------------------------------------------------ language of the game
        /// Every 2 s on the main thread (Mod.OnUpdate): reads the game's UI language and switches the mod's texts when it changed.
        internal static void Poll()
        {
            if (_firstPoll)
            {
                _firstPoll = false;
                int n = (int)TxtKey.Count;
                Mod.Log.Msg($"[LANGUE] {n} textes : EN {_translated[(int)Lang.EN]}/{n}, RU {_translated[(int)Lang.RU]}/{n}, DE {_translated[(int)Lang.DE]}/{n}, ZH {_translated[(int)Lang.ZH]}/{n}, problèmes {_problems.Count}");
                foreach (var p in _problems) Mod.Log.Warning("[LANGUE] " + p);
            }
            string raw = null;
            try
            {
                var svc = Mod.Svc<Il2CppBrokenArrow.Shared.Ecs.Localization.LocalizationService>();
                if (svc != null) raw = svc.CurrentLanguage;
            }
            catch { raw = null; }
            if (string.IsNullOrWhiteSpace(raw))
            {
                // unreadable (loading, service not yet registered): the last language read stays
                if (_lastRaw == null && !_unreadLogged) { _unreadLogged = true; Mod.Log.Msg("[LANGUE] langue du jeu pas encore lisible : textes du mod en anglais en attendant"); }
                return;
            }
            if (string.Equals(raw, _lastRaw, StringComparison.Ordinal)) return;
            _lastRaw = raw;
            var l = Parse(raw, out bool known);
            if ((int)l != _lang)
            {
                _lang = (int)l;
                Interlocked.Increment(ref _epoch);
            }
            Mod.Log.Msg($"[LANGUE] langue du jeu « {raw} » -> textes du mod en {l}" + (known ? "" : " (langue sans traduction du mod : anglais)"));
        }

        static readonly char[] CodeCut = { '-', '_' };

        /// Game language code ("fre", "eng", "rus", "ger", "chi"...) to a table language; anything else is English.
        internal static Lang Parse(string raw, out bool known)
        {
            known = true;
            string s = (raw ?? "").Trim().ToLowerInvariant();
            int cut = s.IndexOfAny(CodeCut);
            if (cut > 0) s = s.Substring(0, cut);
            switch (s)
            {
                case "fre": case "fra": case "fr": case "french": case "français": case "francais": return Lang.FR;
                case "eng": case "en": case "english": return Lang.EN;
                case "rus": case "ru": case "russian": case "русский": return Lang.RU;
                case "ger": case "deu": case "de": case "german": case "deutsch": return Lang.DE;
                case "chi": case "zho": case "zh": case "chs": case "cht": case "cn": case "chinese": case "schinese": case "中文": case "简体中文": return Lang.ZH;
            }
            known = false;
            return Lang.EN;
        }

        // ------------------------------------------------------------ table check (once, when the class loads)
        static Txt()
        {
            int n = (int)TxtKey.Count;
            try { BuildTables(n); }
            catch (Exception e) { _problems.Add("table des textes illisible : " + e.Message); }
            try
            {
                // whatever happened above: every cell holds a text, so a lookup can never return null
                if (_t == null) { _t = new string[Langs][]; }
                for (int l = 0; l < Langs; l++)
                {
                    if (_t[l] == null) _t[l] = new string[n];
                    for (int k = 0; k < n; k++) _t[l][k] ??= (_t[(int)Lang.FR]?[k] ?? ((TxtKey)k).ToString());
                }
                _argc ??= new int[n];
            }
            catch { }
        }

        static void BuildTables(int n)
        {
            var t = new string[Langs][];
            for (int l = 0; l < Langs; l++) t[l] = new string[n];
            var argc = new int[n];
            foreach (var r in Rows)
            {
                int k = (int)r.Key;
                if ((uint)k >= (uint)n) { _problems.Add($"clé hors table : {r.Key}"); continue; }
                if (t[(int)Lang.FR][k] != null) { _problems.Add($"clé en double : {r.Key}"); continue; }
                string fr = string.IsNullOrEmpty(r.Fr) ? r.Key.ToString() : r.Fr;
                int mask = Mask(fr, out bool frOk);
                if (!frOk || !Formats(fr, mask)) _problems.Add($"{r.Key} : texte français mal formé");
                int max = -1;
                for (int b = 0; b < 31; b++) if ((mask & (1 << b)) != 0) max = b;
                if (max >= MaxArgs) _problems.Add($"{r.Key} : plus de {MaxArgs} arguments");
                argc[k] = max + 1;
                t[(int)Lang.FR][k] = fr;
                string en = Check(r.Key, "EN", r.En, mask);
                if (en != null) _translated[(int)Lang.EN]++;
                t[(int)Lang.EN][k] = en ?? fr;
                string ru = Check(r.Key, "RU", r.Ru, mask), de = Check(r.Key, "DE", r.De, mask), zh = Check(r.Key, "ZH", r.Zh, mask);
                if (ru != null) _translated[(int)Lang.RU]++;
                if (de != null) _translated[(int)Lang.DE]++;
                if (zh != null) _translated[(int)Lang.ZH]++;
                t[(int)Lang.RU][k] = ru ?? en ?? fr;
                t[(int)Lang.DE][k] = de ?? en ?? fr;
                t[(int)Lang.ZH][k] = zh ?? en ?? fr;
            }
            for (int k = 0; k < n; k++)
                if (t[(int)Lang.FR][k] == null) _problems.Add($"clé sans texte : {(TxtKey)k}");
            _argc = argc;
            _t = t;
        }

        /// The translation when it is usable (same placeholders as the French text, formats), else null.
        static string Check(TxtKey key, string lang, string s, int frMask)
        {
            if (string.IsNullOrEmpty(s)) { _problems.Add($"{key} : pas de texte {lang}"); return null; }
            int mask = Mask(s, out bool ok);
            if (!ok || mask != frMask || !Formats(s, mask)) { _problems.Add($"{key} : texte {lang} refusé (arguments différents du français)"); return null; }
            return s;
        }

        /// Bit n set for each {n} in the text; ok false on a brace that is not a simple {n}.
        static int Mask(string s, out bool ok)
        {
            ok = true;
            int mask = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '}') { ok = false; continue; }
                if (c != '{') continue;
                int j = i + 1, v = 0, digits = 0;
                while (j < s.Length && s[j] >= '0' && s[j] <= '9' && digits < 3) { v = v * 10 + (s[j] - '0'); j++; digits++; }
                if (digits == 0 || j >= s.Length || s[j] != '}' || v > 30) { ok = false; continue; }
                mask |= 1 << v;
                i = j;
            }
            return mask;
        }

        static bool Formats(string s, int mask)
        {
            if (mask == 0) return true;
            try { string.Format(Inv, s, "a", "b", "c", "d"); return true; }
            catch { return false; }
        }

        // ------------------------------------------------------------ table: key, French (source), English, Russian, German, Chinese
        static readonly (TxtKey Key, string Fr, string En, string Ru, string De, string Zh)[] Rows =
        {
            // ---- Mod tab
            (TxtKey.TAB_MOD, "Mod", "Mod", "Mod", "Mod", "Mod"),
            (TxtKey.MT_TITLE_CHEATS, "Triche", "Cheats", "Читы", "Cheats", "作弊"),
            (TxtKey.MT_TITLE_CHEATS_SOLO, "Triche : mission de campagne solo uniquement", "Cheats: solo campaign missions only", "Читы: только в одиночных миссиях кампании", "Cheats: nur in Einzelspieler-Kampagnenmissionen", "作弊：仅限单人战役任务"),
            (TxtKey.MT_TITLE_ABOUT, "À propos", "About", "О моде", "Über", "关于"),
            (TxtKey.ST_ON, "ACTIVÉ", "ON", "ВКЛ", "AN", "开启"),
            (TxtKey.ST_OFF, "DÉSACTIVÉ", "OFF", "ВЫКЛ", "AUS", "关闭"),
            (TxtKey.ST_CLICK, "CLIQUER", "CLICK", "НАЖАТЬ", "KLICKEN", "点击"),
            (TxtKey.ST_UNAVAILABLE, "INDISPONIBLE", "UNAVAILABLE", "НЕДОСТУПНО", "GESPERRT", "不可用"),
            (TxtKey.ST_OPEN, "OUVRIR", "OPEN", "ОТКРЫТЬ", "ÖFFNEN", "打开"),
            (TxtKey.MT_CHEAT_FALLBACK, "Triche {0}", "Cheat {0}", "Чит {0}", "Cheat {0}", "作弊 {0}"),
            (TxtKey.MT_CHEAT_DESC_NOKEY, "{0} Pas de touche : utilise cette ligne.", "{0} No key: use this row.", "{0} Клавиша не назначена: используйте эту строку.", "{0} Keine Taste: nutze diese Zeile.", "{0} 未设置按键：请点击这一行。"),
            (TxtKey.MT_CHEAT_DESC_KEY, "{0} Touche : {1}.", "{0} Key: {1}.", "{0} Клавиша: {1}.", "{0} Taste: {1}.", "{0} 按键：{1}。"),
            (TxtKey.MT_DEFAULT_TITLE, "Triche (toi seul)", "Cheats (you only)", "Читы (только для вас)", "Cheats (nur für dich)", "作弊（只对你生效）"),
            (TxtKey.MT_DEFAULT_BODY,
                "Les triches ne touchent que tes propres unités et ton argent, en mission de campagne solo. Les autres fonctions du mod sont activées d'office, sauf l'embuscade et le ravitaillement automatique (retirés) ; les renforts ennemis n'apparaissent plus près de tes unités (ils partent d'un autre endroit ennemi déjà utilisé, plus loin). Clique sur une ligne ou utilise sa touche.",
                "Cheats only affect your own units and your money, in solo campaign missions. Every other feature of the mod is always on, except ambush and automatic resupply (removed). Enemy reinforcements no longer appear near your units: they come in from another enemy spot already in use, further away. Click a row or press its key.",
                "Читы действуют только на ваши юниты и ваши деньги и только в одиночных миссиях кампании. Все остальные функции мода всегда включены, кроме засады и автоматического пополнения (они убраны). Вражеские подкрепления больше не появляются рядом с вашими юнитами: они выходят из другой, более далёкой точки, которую противник уже использовал. Нажмите на строку или используйте её клавишу.",
                "Cheats wirken nur auf deine eigenen Einheiten und dein Geld, in Einzelspieler-Kampagnenmissionen. Alle anderen Funktionen der Mod sind immer an, außer Hinterhalt und automatischer Versorgung (entfernt). Feindliche Verstärkungen erscheinen nicht mehr in der Nähe deiner Einheiten, sondern kommen von einem anderen, weiter entfernten Punkt, den der Gegner schon benutzt hat. Klicke auf eine Zeile oder drücke ihre Taste.",
                "作弊只影响你自己的单位和资金，并且只在单人战役任务中有效。模组的其他功能始终开启，但伏击和自动补给除外（已移除）。敌方增援不会再出现在你的单位附近，而是从敌人已经用过的另一个更远的地点出发。点击某一行或按对应的按键即可。"),
            (TxtKey.MT_UNAVAILABLE_HERE, "Indisponible ici : {0}.", "Not available here: {0}.", "Здесь недоступно: {0}.", "Hier nicht verfügbar: {0}.", "此处不可用：{0}。"),
            (TxtKey.MT_STATE_UNREADABLE, "état illisible ({0})", "state could not be read ({0})", "не удалось прочитать состояние ({0})", "Status nicht lesbar ({0})", "无法读取状态（{0}）"),
            (TxtKey.MT_ABOUT_LABEL, "{0} par {1}", "{0} by {1}", "{0}, автор {1}", "{0} von {1}", "{0} 作者 {1}"),
            (TxtKey.MT_ABOUT_DESC,
                "{0}, créé par {1}.\nMod pour la campagne solo.\n\nSteam : {2}\nDiscord : {3}",
                "{0}, made by {1}.\nA mod for the single-player campaign.\n\nSteam: {2}\nDiscord: {3}",
                "{0}, автор {1}.\nМод для одиночной кампании.\n\nSteam: {2}\nDiscord: {3}",
                "{0}, erstellt von {1}.\nEine Mod für die Einzelspielerkampagne.\n\nSteam: {2}\nDiscord: {3}",
                "{0}，作者 {1}。\n单人战役模组。\n\nSteam：{2}\nDiscord：{3}"),
            (TxtKey.MT_LINK_STEAM_DESC, "Ouvre le profil Steam d'{0} dans ton navigateur.", "Opens {0}'s Steam profile in your browser.", "Открывает профиль Steam автора ({0}) в браузере.", "Öffnet das Steam-Profil von {0} in deinem Browser.", "在浏览器中打开 {0} 的 Steam 个人资料。"),
            (TxtKey.MT_LINK_DISCORD_DESC, "Ouvre le Discord du mod : questions, problèmes, nouvelles versions.", "Opens the mod's Discord: questions, problems, new versions.", "Открывает Discord мода: вопросы, проблемы, новые версии.", "Öffnet den Discord der Mod: Fragen, Probleme, neue Versionen.", "打开模组的 Discord：提问、反馈问题、获取新版本。"),

            // ---- cheats
            (TxtKey.CH_LABEL_AMMO, "Munitions et fumigènes illimités", "Unlimited ammo and smoke", "Бесконечные боеприпасы и дым", "Unbegrenzte Munition und Rauch", "无限弹药和烟雾弹"),
            (TxtKey.CH_LABEL_FUEL, "Carburant illimité", "Unlimited fuel", "Бесконечное топливо", "Unbegrenzter Treibstoff", "无限燃料"),
            (TxtKey.CH_LABEL_UNITS, "{0} unités par carte", "{0} units per card", "Юнитов на карточку: {0}", "{0} Einheiten pro Karte", "每张卡片 {0} 个单位"),
            (TxtKey.CH_LABEL_TOUGH, "Unités résistantes", "Tough units", "Живучие юниты", "Robuste Einheiten", "坚韧单位"),
            (TxtKey.CH_LABEL_MONEY, "Argent +{0}", "Money +{0}", "Деньги +{0}", "Geld +{0}", "资金 +{0}"),
            (TxtKey.CH_LABEL_HEAL, "Soin et réparation", "Heal and repair", "Лечение и ремонт", "Heilen und reparieren", "治疗和维修"),
            (TxtKey.CH_LABEL_CARDS, "Cartes rechargées", "Card cooldowns reset", "Сброс задержки карточек", "Karten sofort bereit", "卡片冷却清零"),
            (TxtKey.CH_DESC_AMMO, "Tes unités gardent munitions et fumigènes au maximum ; coupé au début de chaque mission.",
                "Your units keep their ammo and smoke topped up. Switched off at the start of every mission.",
                "Боеприпасы и дымовые гранаты ваших юнитов всегда пополнены. Выключается в начале каждой миссии.",
                "Deine Einheiten haben immer volle Munition und vollen Rauch. Wird zu Beginn jeder Mission ausgeschaltet.",
                "你的单位弹药和烟雾弹始终保持满载；每个任务开始时自动关闭。"),
            (TxtKey.CH_DESC_FUEL, "Tes unités ne tombent jamais en panne de carburant ; coupé au début de chaque mission.",
                "Your units never run out of fuel. Switched off at the start of every mission.",
                "У ваших юнитов никогда не кончается топливо. Выключается в начале каждой миссии.",
                "Deinen Einheiten geht nie der Treibstoff aus. Wird zu Beginn jeder Mission ausgeschaltet.",
                "你的单位永远不会耗尽燃料；每个任务开始时自动关闭。"),
            (TxtKey.CH_DESC_UNITS, "Chaque carte de ton deck passe à au moins {0} unités, pour toi seul ; coupé au début de chaque mission.",
                "Every card in your deck gets at least {0} units, for you only. Switched off at the start of every mission.",
                "Каждая карточка вашей колоды получает не меньше {0} юнитов, только для вас. Выключается в начале каждой миссии.",
                "Jede Karte in deinem Deck bekommt mindestens {0} Einheiten, nur für dich. Wird zu Beginn jeder Mission ausgeschaltet.",
                "你卡组里的每张卡片至少有 {0} 个单位，只对你生效；每个任务开始时自动关闭。"),
            (TxtKey.CH_DESC_TOUGH, "Tes unités ne reçoivent que {0} % des dégâts, pour toi seul ; coupé au début de chaque mission.",
                "Your units take only {0}% of the damage, for you only. Switched off at the start of every mission.",
                "Ваши юниты получают только {0} % урона, только для вас. Выключается в начале каждой миссии.",
                "Deine Einheiten erleiden nur {0} % des Schadens, nur für dich. Wird zu Beginn jeder Mission ausgeschaltet.",
                "你的单位只受到 {0}% 的伤害，只对你生效；每个任务开始时自动关闭。"),
            (TxtKey.CH_DESC_MONEY, "Ajoute {0} points à ton argent, pour toi seul, à chaque appui.",
                "Adds {0} points to your money, for you only, each time you press it.",
                "При каждом нажатии добавляет {0} очков к вашим деньгам, только для вас.",
                "Gibt dir bei jedem Drücken {0} Punkte Geld dazu, nur für dich.",
                "每按一次，给你的资金增加 {0} 点，只对你生效。"),
            (TxtKey.CH_DESC_HEAL, "Remet toutes tes unités en pleine santé et les répare, à chaque appui (essai).",
                "Brings all your units back to full health and repairs them, each time you press it (experimental).",
                "При каждом нажатии полностью лечит и ремонтирует все ваши юниты (экспериментально).",
                "Bringt bei jedem Drücken alle deine Einheiten auf volle Gesundheit und repariert sie (experimentell).",
                "每按一次，把你的所有单位恢复满血并修好（试验功能）。"),
            (TxtKey.CH_DESC_CARDS, "Tes cartes peuvent être redéployées ou rachetées tout de suite, pour toi seul.",
                "Your cards can be redeployed or bought again right away, for you only.",
                "Ваши карточки можно сразу снова развернуть или купить, только для вас.",
                "Deine Karten können sofort wieder eingesetzt oder neu gekauft werden, nur für dich.",
                "你的卡片可以立即重新部署或再次购买，只对你生效。"),
            (TxtKey.CH_WHY_MOD_OFF, "le mod est désactivé", "the mod is switched off", "мод выключен", "die Mod ist ausgeschaltet", "模组已关闭"),
            (TxtKey.CH_WHY_TUTO, "mission tuto, le mod est désactivé pour cette mission", "tutorial mission, the mod is off for this mission", "обучающая миссия, мод выключен на эту миссию", "Tutorial-Mission, die Mod ist für diese Mission aus", "教学任务，本任务不启用模组"),
            (TxtKey.CH_WHY_ANTICHEAT, "anti-triche actif", "anti-cheat is running", "античит включён", "Anti-Cheat ist aktiv", "反作弊已开启"),
            (TxtKey.CH_WHY_NOT_CAMPAIGN, "seulement en mission de campagne", "campaign missions only", "только в миссиях кампании", "nur in Kampagnenmissionen", "仅限战役任务"),
            (TxtKey.CH_WHY_NO_MISSION, "aucune mission en cours", "no mission in progress", "нет текущей миссии", "keine laufende Mission", "当前没有进行中的任务"),
            (TxtKey.CH_WHY_ONLINE, "partie en ligne", "online game", "сетевая игра", "Online-Partie", "在线对局"),
            (TxtKey.CH_WHY_UNREADABLE, "état de la partie illisible", "game state could not be read", "не удалось прочитать состояние игры", "Spielstatus nicht lesbar", "无法读取对局状态"),

            // ---- cheat notices
            (TxtKey.N_CHEAT_KEYS, "Touches de triche : {0}", "Cheat keys: {0}", "Клавиши читов: {0}", "Cheat-Tasten: {0}", "作弊按键：{0}"),
            (TxtKey.N_CHEAT_UNAVAILABLE, "Triche indisponible : {0}", "Cheat not available: {0}", "Чит недоступен: {0}", "Cheat nicht verfügbar: {0}", "作弊不可用：{0}"),
            // the escape menu entry that opens the screen hosting the Mod tab: each language uses the game's own word for it
            (TxtKey.N_CHEAT_MENU, "Triche : Échap > Paramètres > Mod", "Cheats: Esc > Settings > Mod", "Читы: Esc > Настройки > Mod", "Cheats: Esc > Einstellungen > Mod", "作弊：Esc > 设置 > Mod"),
            (TxtKey.N_CHEAT_MENU_KEYS, "Triche : Échap > Paramètres > Mod (ou touches du clavier)", "Cheats: Esc > Settings > Mod (or hotkeys)", "Читы: Esc > Настройки > Mod (или горячие клавиши)", "Cheats: Esc > Einstellungen > Mod (oder Tastenkürzel)", "作弊：Esc > 设置 > Mod（或使用快捷键）"),
            (TxtKey.N_AMMO_ON, "Munitions illimitées : ACTIVÉES", "Unlimited ammo: ON", "Бесконечные боеприпасы: ВКЛ", "Unbegrenzte Munition: AN", "无限弹药：开启"),
            (TxtKey.N_AMMO_OFF, "Munitions illimitées : désactivées (unités laissées pleines)", "Unlimited ammo: off (units left fully stocked)", "Бесконечные боеприпасы: выкл (боекомплект юнитов остаётся полным)", "Unbegrenzte Munition: aus (Einheiten bleiben voll aufmunitioniert)", "无限弹药：关闭（单位保持满载）"),
            (TxtKey.N_FUEL_ON, "Carburant illimité : ACTIVÉ", "Unlimited fuel: ON", "Бесконечное топливо: ВКЛ", "Unbegrenzter Treibstoff: AN", "无限燃料：开启"),
            (TxtKey.N_FUEL_OFF, "Carburant illimité : désactivé (unités laissées pleines)", "Unlimited fuel: off (units left with full tanks)", "Бесконечное топливо: выкл (баки юнитов остаются полными)", "Unbegrenzter Treibstoff: aus (Einheiten bleiben vollgetankt)", "无限燃料：关闭（单位保持满油）"),
            (TxtKey.N_UNITS_ON, "{0} unités par carte : ACTIVÉ", "{0} units per card: ON", "Юнитов на карточку {0}: ВКЛ", "{0} Einheiten pro Karte: AN", "每张卡片 {0} 个单位：开启"),
            (TxtKey.N_UNITS_OFF, "{0} unités par carte : désactivé", "{0} units per card: off", "Юнитов на карточку {0}: выкл", "{0} Einheiten pro Karte: aus", "每张卡片 {0} 个单位：关闭"),
            (TxtKey.N_UNITS_NOT_READY, "Unités par carte : ton deck de mission n'est pas encore prêt, réessaie dans quelques secondes",
                "Units per card: your mission deck isn't ready yet, try again in a few seconds",
                "Юниты на карточку: колода миссии ещё не готова, попробуйте через несколько секунд",
                "Einheiten pro Karte: Dein Missionsdeck ist noch nicht bereit, versuch es in ein paar Sekunden noch mal",
                "每张卡片单位数：任务卡组还没准备好，请几秒后再试"),
            (TxtKey.N_TOUGH_ON, "Unités résistantes : ACTIVÉ (dégâts reçus x{0})", "Tough units: ON (damage taken x{0})", "Живучие юниты: ВКЛ (получаемый урон x{0})", "Robuste Einheiten: AN (erlittener Schaden x{0})", "坚韧单位：开启（受到伤害 x{0}）"),
            (TxtKey.N_TOUGH_OFF, "Unités résistantes : désactivé", "Tough units: off", "Живучие юниты: выкл", "Robuste Einheiten: aus", "坚韧单位：关闭"),
            (TxtKey.N_TOUGH_ERRORS, "Unités résistantes : coupé après trop d'erreurs (voir le journal)", "Tough units: switched off after too many errors (see the log)", "Живучие юниты: выключено из-за слишком большого числа ошибок (см. журнал)", "Robuste Einheiten: nach zu vielen Fehlern abgeschaltet (siehe Log)", "坚韧单位：错误过多，已关闭（详见日志）"),
            (TxtKey.N_TOUGH_IMPOSSIBLE, "Unités résistantes : impossible dans cette version du jeu", "Tough units: not possible in this version of the game", "Живучие юниты: невозможно в этой версии игры", "Robuste Einheiten: in dieser Spielversion nicht möglich", "坚韧单位：当前游戏版本无法使用"),
            (TxtKey.N_MONEY_NO_MISSION, "Argent : aucune mission en cours", "Money: no mission in progress", "Деньги: нет текущей миссии", "Geld: keine laufende Mission", "资金：当前没有进行中的任务"),
            (TxtKey.N_MONEY_REQ_UNREADABLE, "+{0} points demandés (total illisible)", "+{0} points requested (total could not be read)", "Запрошено +{0} очков (не удалось прочитать итог)", "+{0} Punkte angefordert (Gesamtstand nicht lesbar)", "已请求 +{0} 点（无法读取总额）"),
            (TxtKey.N_MONEY_ADDED, "+{0} points (total {1})", "+{0} points (total {1})", "+{0} очков (всего {1})", "+{0} Punkte (insgesamt {1})", "+{0} 点（总计 {1}）"),
            (TxtKey.N_MONEY_CAPPED, "+{0} points seulement : plafond d'argent de la mission atteint (total {1})", "Only +{0} points: the mission's money cap is reached (total {1})", "Только +{0} очков: достигнут лимит денег миссии (всего {1})", "Nur +{0} Punkte: Geldobergrenze der Mission erreicht (insgesamt {1})", "只增加了 {0} 点：已达到本任务的资金上限（总计 {1}）"),
            (TxtKey.N_MONEY_REQ_UNVERIFIED, "+{0} points demandés (vérification impossible)", "+{0} points requested (could not be checked)", "Запрошено +{0} очков (проверить не удалось)", "+{0} Punkte angefordert (Prüfung nicht möglich)", "已请求 +{0} 点（无法确认）"),
            (TxtKey.N_MONEY_NO_PLAYER, "Argent : impossible dans cette mission (joueur introuvable)", "Money: not possible in this mission (player not found)", "Деньги: невозможно в этой миссии (игрок не найден)", "Geld: in dieser Mission nicht möglich (Spieler nicht gefunden)", "资金：本任务中无法使用（找不到玩家）"),
            (TxtKey.N_MONEY_NO_EFFECT, "Argent : aucun effet (plafond atteint ou pas d'argent dans cette mission, total {0})", "Money: no effect (cap reached or no money in this mission, total {0})", "Деньги: без эффекта (лимит достигнут или в этой миссии нет денег, всего {0})", "Geld: keine Wirkung (Obergrenze erreicht oder kein Geld in dieser Mission, insgesamt {0})", "资金：没有效果（已达上限或本任务没有资金，总计 {0}）"),
            (TxtKey.N_MONEY_FAILED, "Argent : échec (voir la console)", "Money: failed (see the console)", "Деньги: не удалось (см. консоль)", "Geld: fehlgeschlagen (siehe Konsole)", "资金：失败（详见控制台）"),
            (TxtKey.N_HEAL_NO_MISSION, "Soin et réparation : aucune mission en cours", "Heal and repair: no mission in progress", "Лечение и ремонт: нет текущей миссии", "Heilen und reparieren: keine laufende Mission", "治疗和维修：当前没有进行中的任务"),
            (TxtKey.N_HEAL_LIST_UNREADABLE, "Soin et réparation : liste de tes unités illisible", "Heal and repair: your unit list could not be read", "Лечение и ремонт: не удалось прочитать список ваших юнитов", "Heilen und reparieren: Liste deiner Einheiten nicht lesbar", "治疗和维修：无法读取你的单位列表"),
            (TxtKey.N_HEAL_STATE_UNREADABLE, "Soin et réparation : état de tes unités illisible", "Heal and repair: your units' state could not be read", "Лечение и ремонт: не удалось прочитать состояние ваших юнитов", "Heilen und reparieren: Zustand deiner Einheiten nicht lesbar", "治疗和维修：无法读取你的单位状态"),
            (TxtKey.N_HEAL_NO_UNITS, "Soin et réparation : aucune unité à toi sur la carte", "Heal and repair: you have no units on the map", "Лечение и ремонт: на карте нет ваших юнитов", "Heilen und reparieren: Du hast keine Einheiten auf der Karte", "治疗和维修：地图上没有你的单位"),
            (TxtKey.N_HEAL_UNAVAILABLE, "Soin et réparation : indisponible dans cette mission", "Heal and repair: not available in this mission", "Лечение и ремонт: недоступно в этой миссии", "Heilen und reparieren: in dieser Mission nicht verfügbar", "治疗和维修：本任务中不可用"),
            (TxtKey.N_HEAL_FAILED, "Soin et réparation : échec (voir la console)", "Heal and repair: failed (see the console)", "Лечение и ремонт: не удалось (см. консоль)", "Heilen und reparieren: fehlgeschlagen (siehe Konsole)", "治疗和维修：失败（详见控制台）"),
            (TxtKey.N_HEAL_DONE, "Soin et réparation : {0} unités", "Heal and repair: {0} units", "Лечение и ремонт: юнитов {0}", "Heilen und reparieren: {0} Einheiten", "治疗和维修：{0} 个单位"),
            (TxtKey.N_CARDS_NO_MISSION, "Cartes rechargées : aucune mission en cours", "Card cooldowns reset: no mission in progress", "Сброс задержки карточек: нет текущей миссии", "Karten sofort bereit: keine laufende Mission", "卡片冷却清零：当前没有进行中的任务"),
            (TxtKey.N_CARDS_UNAVAILABLE, "Cartes rechargées : indisponible dans cette mission", "Card cooldowns reset: not available in this mission", "Сброс задержки карточек: недоступно в этой миссии", "Karten sofort bereit: in dieser Mission nicht verfügbar", "卡片冷却清零：本任务中不可用"),
            (TxtKey.N_CARDS_FAILED, "Cartes rechargées : échec (voir la console)", "Card cooldowns reset: failed (see the console)", "Сброс задержки карточек: не удалось (см. консоль)", "Karten sofort bereit: fehlgeschlagen (siehe Konsole)", "卡片冷却清零：失败（详见控制台）"),
            (TxtKey.N_CARDS_DONE, "Cartes rechargées : délais de tes cartes remis à zéro", "Card cooldowns reset: your card timers are back to zero", "Сброс задержки карточек: задержки ваших карточек обнулены", "Karten sofort bereit: Wartezeiten deiner Karten auf null gesetzt", "卡片冷却清零：你的卡片冷却时间已归零"),
            (TxtKey.N_AMMO_UNAVAILABLE, "Triche munitions indisponible dans cette mission", "Ammo cheat not available in this mission", "Чит на боеприпасы недоступен в этой миссии", "Munitions-Cheat in dieser Mission nicht verfügbar", "弹药作弊在本任务中不可用"),
            (TxtKey.N_FUEL_UNAVAILABLE, "Triche carburant indisponible dans cette mission", "Fuel cheat not available in this mission", "Чит на топливо недоступен в этой миссии", "Treibstoff-Cheat in dieser Mission nicht verfügbar", "燃料作弊在本任务中不可用"),
            (TxtKey.N_AMMOFUEL_UNAVAILABLE, "Triche munitions/carburant indisponible dans cette mission", "Ammo/fuel cheat not available in this mission", "Чит на боеприпасы и топливо недоступен в этой миссии", "Munitions-/Treibstoff-Cheat in dieser Mission nicht verfügbar", "弹药/燃料作弊在本任务中不可用"),

            // ---- campaign, DLC and division
            (TxtKey.N_TUTO_NO_MOD, "Mission tuto : le mod est désactivé pour cette mission, il revient à la suivante",
                "Tutorial mission: the mod is off for this mission and comes back at the next one",
                "Обучающая миссия: мод выключен на эту миссию и снова включится в следующей",
                "Tutorial-Mission: Die Mod ist für diese Mission aus und ab der nächsten wieder aktiv",
                "教学任务：本任务不启用模组，下一个任务会重新启用"),
            (TxtKey.N_DLC_REMOVED, "Unités de DLC non possédés retirées de ton deck CAMPAGNE : {0}", "Units from DLC you don't own removed from your CAMPAGNE deck: {0}", "Юниты из DLC, которых у вас нет, убраны из колоды CAMPAGNE: {0}", "Einheiten aus DLCs, die du nicht besitzt, aus deinem CAMPAGNE-Deck entfernt: {0}", "已从你的 CAMPAGNE 卡组中移除未拥有 DLC 的单位：{0}"),
            (TxtKey.N_DLC_REMOVED_UNKNOWN, "Unités de DLC non possédés retirées de ton deck CAMPAGNE : {0} (DLC non vérifiables)", "Units from DLC you don't own removed from your CAMPAGNE deck: {0} (DLC could not be checked)", "Юниты из DLC, которых у вас нет, убраны из колоды CAMPAGNE: {0} (DLC проверить не удалось)", "Einheiten aus DLCs, die du nicht besitzt, aus deinem CAMPAGNE-Deck entfernt: {0} (DLCs nicht überprüfbar)", "已从你的 CAMPAGNE 卡组中移除未拥有 DLC 的单位：{0}（无法核实 DLC）"),
            (TxtKey.N_REAL_STATS_ON, "Vraies stats actives sur cette mission", "Real stats active in this mission", "Реальные характеристики действуют в этой миссии", "Echte Werte in dieser Mission aktiv", "本任务已启用真实数据"),
            (TxtKey.N_REALISM_ON, "Réalisme actif ({0}) sur cette mission", "Realism active ({0}) in this mission", "Реализм включён ({0}) в этой миссии", "Realismus aktiv ({0}) in dieser Mission", "本任务已启用真实化（{0}）"),
            // the preset name inside that notice: the stored value is a config word, on screen it is read in the player's language
            (TxtKey.RL_PRESET_REAL, "realiste", "realistic", "реалистичный", "realistisch", "真实"),
            (TxtKey.RL_PRESET_MODERATE, "modere", "moderate", "умеренный", "moderat", "适中"),
            (TxtKey.RL_PRESET_CUSTOM, "perso", "custom", "свой", "eigen", "自定义"),
            (TxtKey.N_REALISM_NOT_APPLIED, "Réalisme activé mais non appliqué à cette mission (voir la console)", "Realism is on but was not applied to this mission (see the console)", "Реализм включён, но к этой миссии не применён (см. консоль)", "Realismus ist an, wurde auf diese Mission aber nicht angewendet (siehe Konsole)", "真实化已开启，但未应用到本任务（详见控制台）"),
            (TxtKey.N_PATCH_FAILURES, "Mod : {0} fonction(s) désactivée(s) depuis la mise à jour du jeu, le reste marche (détails dans la console)",
                "Mod: {0} feature(s) switched off since the game update, everything else works (details in the console)",
                "Мод: после обновления игры отключено функций: {0}, всё остальное работает (подробности в консоли)",
                "Mod: {0} Funktion(en) seit dem Spielupdate abgeschaltet, der Rest funktioniert (Details in der Konsole)",
                "模组：游戏更新后有 {0} 项功能已关闭，其余功能正常（详见控制台）"),
            (TxtKey.DIV_DESC, "Toutes les unités du camp. Réservé à la campagne.", "Every unit of the side. Campaign only.", "Все юниты стороны. Только для кампании.", "Alle Einheiten der Seite. Nur für die Kampagne.", "该阵营的全部单位。仅限战役使用。"),

            // ---- order panel (names chosen so they never repeat the game's own "Auto-fire" and "Return fire" buttons)
            // The hover box draws its description right under a one-line title: every OR_*_DESC stays short enough for two lines
            // (about 95 characters, half that in Chinese), otherwise the game's box overlaps its own title.
            (TxtKey.OR_CB_TITLE, "Contre-batterie", "Counter-battery", "Контрбатарейный огонь", "Konterbatterie", "反炮兵射击"),
            (TxtKey.OR_CB_DESC,
                "Toute artillerie ennemie qui tire est localisée, même non repérée : les salves se resserrent.",
                "Any enemy artillery that fires is located, even unspotted: the salvos close in on it.",
                "Любая стреляющая артиллерия врага засекается, даже необнаруженная: залпы всё точнее.",
                "Jede feuernde feindliche Artillerie wird geortet, auch unentdeckt: Salven werden genauer.",
                "敌方炮兵一开火就会被定位，即使未被发现：每轮齐射越来越准。"),
            (TxtKey.OR_FAV_TITLE, "Feu à volonté", "Fire at will", "Огонь по готовности", "Feuer frei", "自由射击"),
            (TxtKey.OR_FAV_DESC,
                "Tire seule sur les ennemis repérés à portée : une salve courte par cible, sans jamais bouger.",
                "Fires on its own at spotted enemies in range: one short salvo per target, never moving.",
                "Сама стреляет по обнаруженным целям в зоне: короткий залп на цель, не сходя с места.",
                "Feuert selbst auf aufgeklärte Ziele in Reichweite: kurze Salve pro Ziel, ohne sich zu bewegen.",
                "自动射击射程内已发现的敌人：每个目标一轮短齐射，火炮不移动。"),
            (TxtKey.OR_AA_TITLE, "Anti-aérien", "Anti-air", "ПВО", "Flugabwehr", "防空"),
            (TxtKey.OR_AA_DESC,
                "Désactivé : ce S-400 ne vise que les missiles. Activé : avions, hélicoptères et drones aussi.",
                "Off: this S-400 only fires at missiles. On: planes, helicopters and drones as well.",
                "Выкл: этот S-400 бьёт только по ракетам. Вкл: ещё самолёты, вертолёты и дроны.",
                "Aus: Diese S-400 feuert nur auf Raketen. An: auch Flugzeuge, Hubschrauber und Drohnen.",
                "关闭：该 S-400 只打导弹。开启：也打飞机、直升机和无人机。"),
            (TxtKey.OR_RIP_TITLE, "Riposte seulement", "Only fire back", "Только в ответ", "Nur zurückschießen", "仅在遭到攻击时反击"),
            (TxtKey.OR_RIP_DESC,
                "Les unités ne tirent plus d'elles-mêmes, seulement quand on les touche. Ni artillerie ni DCA.",
                "Units stop firing on their own and only shoot back when hit. Not artillery or air defence.",
                "Юниты не стреляют сами, только в ответ на попадание. Не для артиллерии и ПВО.",
                "Einheiten feuern nur noch zurück, wenn sie getroffen werden. Nicht Artillerie und Flugabwehr.",
                "单位不再主动开火，只在被击中时反击。不适用于炮兵和防空。"),
            (TxtKey.OR_DEF_TITLE, "Défendre la position", "Defend the position", "Оборонять позицию", "Stellung verteidigen", "防守阵地"),
            (TxtKey.OR_DEF_DESC,
                "Défendent l'endroit choisi : sortent au plus 600 m sur l'ennemi repéré, puis reviennent.",
                "They hold the chosen spot: move out 600 m at most onto spotted enemies, then come back.",
                "Обороняют выбранное место: выходят не дальше 600 м на замеченного врага и возвращаются.",
                "Halten den gewählten Punkt: höchstens 600 m zum aufgeklärten Gegner vor, dann zurück.",
                "防守所选位置：最远前出 600 米攻击已发现的敌人，然后返回。"),
            (TxtKey.OR_DEBC_TITLE, "Débarquer au contact", "Unload on contact", "Высадка при контакте", "Bei Feindkontakt absitzen", "接敌下车"),
            (TxtKey.OR_DEBC_DESC,
                "Le transport s'arrête et débarque l'infanterie à 500 m d'un ennemi repéré ou s'il est touché.",
                "The transport stops and unloads its infantry within 500 m of a spotted enemy, or when hit.",
                "Транспорт останавливается и высаживает пехоту в 500 м от врага или при попадании.",
                "Der Transporter hält und setzt die Infanterie 500 m vor dem Gegner oder bei Treffer ab.",
                "运输载具在距已发现敌人 500 米处或被击中时停车，让步兵下车。"),
            (TxtKey.OR_RAVX_TITLE, "Aller se ravitailler", "Go and resupply", "На пополнение", "Zur Versorgung fahren", "前往补给"),
            (TxtKey.OR_RAVX_DESC,
                "Un clic : chaque unité rejoint le ravitaillement ami le plus proche (2,5 km au plus).",
                "One click: each unit heads to the nearest friendly supply, 2.5 km at most.",
                "Один клик: каждый юнит едет к ближайшему своему снабжению (до 2,5 км).",
                "Ein Klick: Jede Einheit fährt zur nächsten eigenen Versorgung (max. 2,5 km).",
                "点一下：每个单位前往最近的友方补给（最远 2.5 公里）。"),
            // the title line also carries the state and the count, so the name itself stays short ("le plus proche" is in the description)
            (TxtKey.OR_GAR_TITLE, "Occuper un bâtiment", "Occupy a building", "Занять здание", "Gebäude besetzen", "占领建筑"),
            (TxtKey.OR_GAR_DESC,
                "Un clic : ton infanterie entre dans le bâtiment libre le plus proche (300 m au plus).",
                "One click: your infantry enters the nearest free, intact building (300 m at most).",
                "Один клик: пехота занимает ближайшее целое свободное здание (до 300 м).",
                "Ein Klick: Deine Infanterie besetzt das nächste freie, heile Gebäude (max. 300 m).",
                "点一下：步兵进入最近的完好空闲建筑（最远 300 米）。"),
            (TxtKey.OR_RZ_TITLE, "Repli vers zone amie", "Fall back to friendly zone", "Отход к своей зоне", "Rückzug in eigene Zone", "撤往友方区域"),
            (TxtKey.OR_RZ_DESC,
                "Un clic : repli vers la zone amie la plus proche, dos à l'ennemi. Jamais l'artillerie.",
                "One click: fall back to the nearest friendly zone, away from the enemy. Never artillery.",
                "Один клик: отход к ближайшей своей зоне, прочь от врага. Артиллерию не трогает.",
                "Ein Klick: Rückzug in die nächste eigene Zone, weg vom Gegner. Nie die Artillerie.",
                "点一下：撤往最近的友方区域，远离敌人。炮兵不受影响。"),
            // state and count on the title line; the hover box keeps its description for the explanation alone
            (TxtKey.OR_HINT_ON, "{0} : ACTIVÉ ({1}/{2})", "{0}: ON ({1}/{2})", "{0}: ВКЛ ({1}/{2})", "{0}: AN ({1}/{2})", "{0}：开启（{1}/{2}）"),
            (TxtKey.OR_HINT_OFF, "{0} : désactivé ({1}/{2})", "{0}: off ({1}/{2})", "{0}: выкл ({1}/{2})", "{0}: aus ({1}/{2})", "{0}：关闭（{1}/{2}）"),
            (TxtKey.N_OR_TOGGLE_ON, "{0} : ACTIVÉ sur {1} unité(s)", "{0}: ON for {1} unit(s)", "{0}: ВКЛ для юнитов: {1}", "{0}: AN für {1} Einheit(en)", "{0}：已对 {1} 个单位开启"),
            (TxtKey.N_OR_TOGGLE_OFF, "{0} : désactivé sur {1} unité(s)", "{0}: off for {1} unit(s)", "{0}: выкл для юнитов: {1}", "{0}: aus für {1} Einheit(en)", "{0}：已对 {1} 个单位关闭"),
            (TxtKey.N_RAV_INTERRUPTED, "Ravitaillement auto interrompu : {0}, ennemi proche", "Auto resupply interrupted: {0}, enemy nearby", "Автопополнение прервано: {0}, противник рядом", "Automatische Versorgung abgebrochen: {0}, Gegner in der Nähe", "自动补给中断：{0}，附近有敌人"),
            (TxtKey.N_OR_ACTION_DONE, "{0} : {1} unité(s)", "{0}: {1} unit(s)", "{0}: юнитов {1}", "{0}: {1} Einheit(en)", "{0}：{1} 个单位"),
            (TxtKey.N_OR_ACTION_PARTIAL, "{0} : {1} unité(s) ({2} sans solution : {3})", "{0}: {1} unit(s) ({2} could not go: {3})", "{0}: юнитов {1} ({2} без решения: {3})", "{0}: {1} Einheit(en) ({2} ohne Lösung: {3})", "{0}：{1} 个单位（{2} 个无法执行：{3}）"),
            (TxtKey.N_OR_ACTION_NONE, "{0} : {1}", "{0}: {1}", "{0}: {1}", "{0}: {1}", "{0}：{1}"),
            (TxtKey.OR_NOTHING, "rien à faire", "nothing to do", "нечего делать", "nichts zu tun", "无事可做"),
            (TxtKey.OR_FAIL_NO_SUPPLY, "aucun ravitaillement ami à moins de {0} m", "no friendly supply within {0} m", "нет своего снабжения ближе {0} м", "keine eigene Versorgung im Umkreis von {0} m", "{0} 米内没有友方补给"),
            (TxtKey.OR_FAIL_REFUSED, "ordre refusé", "order refused", "приказ отклонён", "Befehl abgelehnt", "命令被拒绝"),
            (TxtKey.OR_FAIL_NO_BUILDING, "aucun bâtiment libre à moins de 300 m", "no free building within 300 m", "нет свободного здания ближе 300 м", "kein freies Gebäude im Umkreis von 300 m", "300 米内没有空闲建筑"),
            (TxtKey.OR_FAIL_NO_ZONE, "aucune zone amie ni ennemi repéré", "no friendly zone and no spotted enemy", "нет ни своей зоны, ни обнаруженного противника", "keine eigene Zone und kein aufgeklärter Gegner", "没有友方区域，也没有已发现的敌人"),

            // ---- module safety notices
            (TxtKey.N_AH_OFF_SAFETY, "Tir anti-hélico : coupé par sécurité (le reste du mod fonctionne)", "Anti-helicopter fire: switched off for safety (the rest of the mod still works)", "Стрельба по вертолётам: отключено для безопасности (остальной мод работает)", "Beschuss von Hubschraubern: aus Sicherheitsgründen abgeschaltet (der Rest der Mod funktioniert)", "对直升机射击：出于安全已关闭（模组其余部分正常）"),
            (TxtKey.N_AH_OBSERVE_ONLY, "Tir anti-hélico : règles en observation seule (sécurité des tirs)", "Anti-helicopter fire: rules only observed, not applied (firing safety)", "Стрельба по вертолётам: правила только в режиме наблюдения (безопасность стрельбы)", "Beschuss von Hubschraubern: Regeln werden nur beobachtet (Feuersicherheit)", "对直升机射击：规则仅观察，不生效（射击安全）"),
            (TxtKey.N_AH_RULES_OFF, "Tir anti-hélico : règles coupées par sécurité (plus aucun tir)", "Anti-helicopter fire: rules switched off for safety (firing had stopped)", "Стрельба по вертолётам: правила отключены для безопасности (стрельба прекратилась)", "Beschuss von Hubschraubern: Regeln aus Sicherheitsgründen abgeschaltet (es wurde nicht mehr geschossen)", "对直升机射击：出于安全已关闭规则（射击已完全停止）"),
            (TxtKey.N_AH_RULES_BACK, "Tir anti-hélico : règles remises après vérification", "Anti-helicopter fire: rules back on after a check", "Стрельба по вертолётам: правила снова включены после проверки", "Beschuss von Hubschraubern: Regeln nach einer Prüfung wieder an", "对直升机射击：检查后已恢复规则"),
            (TxtKey.N_AH_RULES_OFF_3MIN, "Tir anti-hélico : règles coupées par sécurité (plus aucun tir depuis 3 min)", "Anti-helicopter fire: rules switched off for safety (no shots for 3 min)", "Стрельба по вертолётам: правила отключены для безопасности (3 мин без стрельбы)", "Beschuss von Hubschraubern: Regeln aus Sicherheitsgründen abgeschaltet (seit 3 Min. kein Schuss)", "对直升机射击：出于安全已关闭规则（3 分钟内没有任何射击）"),
            (TxtKey.N_AFFUTS_OFF, "Affûts, optiques et équipes lourdes : coupés par sécurité pour une bataille (le reste du mod fonctionne)",
                "Weapon mounts, optics and heavy weapon teams: switched off for one battle for safety (the rest of the mod still works)",
                "Установки оружия, оптика и тяжёлые расчёты: отключены на один бой для безопасности (остальной мод работает)",
                "Waffenlafetten, Optiken und schwere Trupps: für ein Gefecht aus Sicherheitsgründen abgeschaltet (der Rest der Mod funktioniert)",
                "武器架、光学瞄具和重武器小组：出于安全在一场战斗中关闭（模组其余部分正常）"),
            (TxtKey.N_HALF_OFF_INTERRUPTED, "Règle du demi-blindage coupée par sécurité (partie précédente interrompue). Le reste du mod fonctionne.",
                "Half-armour rule switched off for safety (the previous game was interrupted). The rest of the mod still works.",
                "Правило половины брони отключено для безопасности (предыдущая игра была прервана). Остальной мод работает.",
                "Halbpanzerungs-Regel aus Sicherheitsgründen abgeschaltet (die vorherige Partie wurde unterbrochen). Der Rest der Mod funktioniert.",
                "半装甲规则出于安全已关闭（上一局被中断）。模组其余部分正常。"),
            (TxtKey.N_HALF_REMEASURE, "Règle du demi-blindage remise en mesure : les réglages du jeu ont changé.",
                "Half-armour rule back to measuring: the game's settings have changed.",
                "Правило половины брони снова проходит замер: настройки игры изменились.",
                "Halbpanzerungs-Regel wird neu gemessen: Die Spieleinstellungen haben sich geändert.",
                "半装甲规则重新进入测量：游戏设置已更改。"),
            (TxtKey.N_HALF_ACTIVE, "Règle du demi-blindage active : un tir qui perce moins de la moitié du blindage d'un véhicule ne fait plus de dégâts (artillerie et bombes non concernées).",
                "Half-armour rule active: a shot that penetrates less than half of a vehicle's armour no longer does any damage (artillery and bombs are not affected).",
                "Правило половины брони действует: выстрел, пробивающий меньше половины брони техники, больше не наносит урона (артиллерии и бомб это не касается).",
                "Halbpanzerungs-Regel aktiv: Ein Treffer, der weniger als die Hälfte der Panzerung eines Fahrzeugs durchschlägt, richtet keinen Schaden mehr an (Artillerie und Bomben ausgenommen).",
                "半装甲规则已生效：穿深不到车辆装甲一半的射击不再造成伤害（炮兵和炸弹除外）。"),
            (TxtKey.N_HALF_OFF_ERRORS, "Règle du demi-blindage coupée après trop d'erreurs. Le reste du mod fonctionne.",
                "Half-armour rule switched off after too many errors. The rest of the mod still works.",
                "Правило половины брони отключено из-за слишком большого числа ошибок. Остальной мод работает.",
                "Halbpanzerungs-Regel nach zu vielen Fehlern abgeschaltet. Der Rest der Mod funktioniert.",
                "半装甲规则因错误过多已关闭。模组其余部分正常。"),
            (TxtKey.N_HALF_OFF_BATTLE, "Règle du demi-blindage coupée pour cette bataille par sécurité. Le reste du mod fonctionne.",
                "Half-armour rule switched off for this battle for safety. The rest of the mod still works.",
                "Правило половины брони отключено на этот бой для безопасности. Остальной мод работает.",
                "Halbpanzerungs-Regel für dieses Gefecht aus Sicherheitsgründen abgeschaltet. Der Rest der Mod funktioniert.",
                "半装甲规则出于安全在本场战斗中关闭。模组其余部分正常。"),
            (TxtKey.N_HALF_MEASURED, "Mesure réussie : dès la prochaine bataille, un tir qui perce moins de la moitié du blindage d'un véhicule ne fera plus de dégâts (artillerie et bombes non concernées).",
                "Measurement done: from the next battle on, a shot that penetrates less than half of a vehicle's armour will do no damage (artillery and bombs are not affected).",
                "Замер прошёл успешно: со следующего боя выстрел, пробивающий меньше половины брони техники, не будет наносить урона (артиллерии и бомб это не касается).",
                "Messung erfolgreich: Ab dem nächsten Gefecht richtet ein Treffer, der weniger als die Hälfte der Panzerung eines Fahrzeugs durchschlägt, keinen Schaden mehr an (Artillerie und Bomben ausgenommen).",
                "测量成功：从下一场战斗开始，穿深不到车辆装甲一半的射击将不再造成伤害（炮兵和炸弹除外）。"),
            (TxtKey.N_HALF_OFF_FORMULA, "Règle du demi-blindage coupée : la mesure ne confirme plus les formules du jeu. Le reste du mod fonctionne.",
                "Half-armour rule switched off: the measurement no longer matches the game's formulas. The rest of the mod still works.",
                "Правило половины брони отключено: замер больше не подтверждает формулы игры. Остальной мод работает.",
                "Halbpanzerungs-Regel abgeschaltet: Die Messung bestätigt die Formeln des Spiels nicht mehr. Der Rest der Mod funktioniert.",
                "半装甲规则已关闭：测量结果不再符合游戏公式。模组其余部分正常。"),
            (TxtKey.N_COVER_OFF_ERRORS, "Couvert de la forêt coupé après trop d'erreurs. Le reste du mod fonctionne.",
                "Forest cover switched off after too many errors. The rest of the mod still works.",
                "Укрытие в лесу отключено из-за слишком большого числа ошибок. Остальной мод работает.",
                "Walddeckung nach zu vielen Fehlern abgeschaltet. Der Rest der Mod funktioniert.",
                "森林掩护因错误过多已关闭。模组其余部分正常。"),
            (TxtKey.N_COVER_OFF_SAFETY, "Couvert de la forêt coupé par sécurité (le reste du mod fonctionne)", "Forest cover switched off for safety (the rest of the mod still works)", "Укрытие в лесу отключено для безопасности (остальной мод работает)", "Walddeckung aus Sicherheitsgründen abgeschaltet (der Rest der Mod funktioniert)", "森林掩护出于安全已关闭（模组其余部分正常）"),
            (TxtKey.N_AI_DEFENSE, "IA ennemie : défensive", "Enemy AI: defensive", "ИИ противника: оборона", "Gegner-KI: defensiv", "敌方 AI：防守"),
            (TxtKey.N_AI_ATTACK, "IA ennemie : offensive", "Enemy AI: offensive", "ИИ противника: наступление", "Gegner-KI: offensiv", "敌方 AI：进攻"),
            (TxtKey.N_AI_MEETING, "IA ennemie : rencontre", "Enemy AI: meeting engagement", "ИИ противника: встречный бой", "Gegner-KI: Begegnungsgefecht", "敌方 AI：遭遇战"),
            (TxtKey.N_FLARES_ACTIVE, "Leurres réalistes : un missile infrarouge (Stinger, Igla, Mistral...) qui trouve des leurres actifs rate toujours et ne fait aucun dégât",
                "Realistic flares: an infrared missile (Stinger, Igla, Mistral...) that meets burning flares always misses and does no damage",
                "Реалистичные тепловые ловушки: ракета с ИК-наведением (Stinger, Igla, Mistral...), встретившая активные ловушки, всегда промахивается и не наносит урона",
                "Realistische Flares: Eine Infrarotrakete (Stinger, Igla, Mistral...), die auf aktive Flares trifft, verfehlt immer und richtet keinen Schaden an",
                "真实热诱弹：遇到正在燃烧的热诱弹的红外导弹（Stinger、Igla、Mistral 等）一定会脱靶，不造成任何伤害"),
            (TxtKey.N_FLARES_OFF_SAFETY, "Leurres réalistes : coupés par sécurité, leurres classiques du jeu (le reste du mod fonctionne)",
                "Realistic flares: switched off for safety, the game's normal flares are back (the rest of the mod still works)",
                "Реалистичные тепловые ловушки: отключены для безопасности, работают обычные ловушки игры (остальной мод работает)",
                "Realistische Flares: aus Sicherheitsgründen abgeschaltet, es gelten die normalen Flares des Spiels (der Rest der Mod funktioniert)",
                "真实热诱弹：出于安全已关闭，改用游戏原版热诱弹（模组其余部分正常）"),
            (TxtKey.N_FLARES_DROP_OFF_INTERRUPTED, "Leurres : décrochage retiré par sécurité (partie précédente interrompue) ; les missiles infrarouges leurrés ratent toujours sans dégâts",
                "Flares: missiles no longer break away, for safety (the previous game was interrupted); decoyed infrared missiles still always miss with no damage",
                "Тепловые ловушки: увод ракет отключён для безопасности (предыдущая игра была прервана); обманутые ракеты с ИК-наведением по-прежнему всегда промахиваются без урона",
                "Flares: Abdrehen der Raketen aus Sicherheitsgründen entfernt (die vorherige Partie wurde unterbrochen); getäuschte Infrarotraketen verfehlen weiterhin immer ohne Schaden",
                "热诱弹：出于安全已取消导弹偏离（上一局被中断）；被诱骗的红外导弹仍然一定脱靶，不造成伤害"),
            (TxtKey.N_FLARES_OFF_BATTLE, "Leurres réalistes : aucun tir depuis 3 minutes au contact, coupés pour cette bataille par sécurité",
                "Realistic flares: no shots for 3 minutes in contact, switched off for this battle for safety",
                "Реалистичные тепловые ловушки: 3 минуты без стрельбы при контакте, отключены на этот бой для безопасности",
                "Realistische Flares: seit 3 Minuten kein Schuss bei Feindkontakt, für dieses Gefecht aus Sicherheitsgründen abgeschaltet",
                "真实热诱弹：交战中 3 分钟没有任何射击，出于安全在本场战斗中关闭"),
            (TxtKey.N_FLARES_OFF_SESSION, "Leurres réalistes : les tirs reprenaient après chaque coupure, leurres coupés jusqu'à la fermeture du jeu",
                "Realistic flares: firing came back after every switch-off, so flares stay off until you close the game",
                "Реалистичные тепловые ловушки: стрельба возобновлялась после каждого отключения, ловушки отключены до закрытия игры",
                "Realistische Flares: Nach jedem Abschalten wurde wieder geschossen, Flares bleiben bis zum Beenden des Spiels aus",
                "真实热诱弹：每次关闭后射击都会恢复，热诱弹将保持关闭直到退出游戏"),
            (TxtKey.N_FLARES_TRIAL_NEXT, "Leurres : à la prochaine bataille, essai du décrochage des missiles leurrés",
                "Flares: the next battle will try making decoyed missiles break away",
                "Тепловые ловушки: в следующем бою будет опробован увод обманутых ракет",
                "Flares: Im nächsten Gefecht wird das Abdrehen getäuschter Raketen getestet",
                "热诱弹：下一场战斗将试验让被诱骗的导弹偏离"),
            (TxtKey.N_FLARES_DROP_CONFIRMED, "Leurres : décrochage confirmé, les missiles leurrés partent loin de l'appareil",
                "Flares: break-away confirmed, decoyed missiles fly off away from the aircraft",
                "Тепловые ловушки: увод подтверждён, обманутые ракеты уходят в сторону от цели",
                "Flares: Abdrehen bestätigt, getäuschte Raketen fliegen vom Luftfahrzeug weg",
                "热诱弹：偏离已确认，被诱骗的导弹会飞离目标"),
            (TxtKey.N_FLARES_DROP_OFF, "Leurres : décrochage retiré par sécurité ; les missiles infrarouges leurrés ratent toujours sans dégâts",
                "Flares: missiles no longer break away, for safety; decoyed infrared missiles still always miss with no damage",
                "Тепловые ловушки: увод ракет отключён для безопасности; обманутые ракеты с ИК-наведением по-прежнему всегда промахиваются без урона",
                "Flares: Abdrehen der Raketen aus Sicherheitsgründen entfernt; getäuschte Infrarotraketen verfehlen weiterhin immer ohne Schaden",
                "热诱弹：出于安全已取消导弹偏离；被诱骗的红外导弹仍然一定脱靶，不造成伤害"),
            (TxtKey.N_MISSIONS_PAUSE, "Missions : {0} : IA ennemie du mod en pause (unités du script non identifiables)",
                "Missions: {0}: the mod's enemy AI is paused (script units can't be identified)",
                "Миссии: {0}: ИИ противника из мода на паузе (не удаётся определить скриптовые юниты)",
                "Missionen: {0}: Gegner-KI der Mod pausiert (Skript-Einheiten nicht erkennbar)",
                "任务：{0}：模组的敌方 AI 已暂停（无法识别脚本单位）"),
            (TxtKey.MS_WHY_INTERRUPTED, "sécurité après 2 parties interrompues", "safety measure after 2 interrupted games", "мера безопасности после 2 прерванных игр", "Sicherheitsmaßnahme nach 2 unterbrochenen Partien", "连续 2 局被中断后的安全措施"),
            (TxtKey.MS_WHY_HOOK_ERRORS, "crochets des ordres du script coupés après trop d'erreurs", "script order hooks switched off after too many errors", "перехват приказов скрипта отключён из-за слишком большого числа ошибок", "Abfangen der Skriptbefehle nach zu vielen Fehlern abgeschaltet", "脚本命令拦截因错误过多已关闭"),
            (TxtKey.MS_WHY_HOOKS_INCOMPLETE, "crochets des ordres du script incomplets", "script order hooks incomplete", "перехват приказов скрипта неполный", "Abfangen der Skriptbefehle unvollständig", "脚本命令拦截不完整"),
            (TxtKey.MS_WHY_HOOKS_FAILED, "installation des crochets impossible", "hooks could not be installed", "не удалось установить перехват", "Abfangen konnte nicht eingerichtet werden", "无法安装拦截"),
            (TxtKey.MS_WHY_SCRIPT_UNREADABLE, "script de la mission illisible", "mission script could not be read", "не удалось прочитать скрипт миссии", "Missionsskript nicht lesbar", "无法读取任务脚本"),
            (TxtKey.N_AA_REALISTIC_OFF, "Défense aérienne réaliste : coupée par sécurité (le reste du mod fonctionne)", "Realistic air defence: switched off for safety (the rest of the mod still works)", "Реалистичная ПВО: отключена для безопасности (остальной мод работает)", "Realistische Flugabwehr: aus Sicherheitsgründen abgeschaltet (der Rest der Mod funktioniert)", "真实防空：出于安全已关闭（模组其余部分正常）"),
            (TxtKey.N_S400_OFF, "S-400 : observation coupée par sécurité (le reste du mod fonctionne)", "S-400: monitoring switched off for safety (the rest of the mod still works)", "S-400: наблюдение отключено для безопасности (остальной мод работает)", "S-400: Überwachung aus Sicherheitsgründen abgeschaltet (der Rest der Mod funktioniert)", "S-400：出于安全已关闭监测（模组其余部分正常）"),
            (TxtKey.N_FAV_NO_VISIBILITY, "Feu à volonté : impossible de savoir quels ennemis sont repérés dans cette mission", "Fire at will: can't tell which enemies are spotted in this mission", "Огонь по готовности: в этой миссии невозможно узнать, какие противники обнаружены", "Feuer frei: In dieser Mission lässt sich nicht erkennen, welche Gegner aufgeklärt sind", "自由射击：本任务中无法判断哪些敌人已被发现"),
            (TxtKey.N_BALLISTICS_CUT, "Balistique réelle coupée par sécurité", "Real ballistics switched off for safety", "Реальная баллистика отключена для безопасности", "Echte Ballistik aus Sicherheitsgründen abgeschaltet", "真实弹道出于安全已关闭"),
            (TxtKey.N_BALLISTICS_OFF_VERSION, "Balistique réelle désactivée pour cette version (sécurité des tirs)", "Real ballistics switched off for this version (firing safety)", "Реальная баллистика отключена для этой версии (безопасность стрельбы)", "Echte Ballistik für diese Version abgeschaltet (Feuersicherheit)", "真实弹道在此版本中已关闭（射击安全）"),
            (TxtKey.N_RANGES_SUSPENDED, "Vraies portées suspendues à la prochaine mission (sécurité des tirs)", "Real ranges suspended from the next mission (firing safety)", "Реальные дальности приостановлены со следующей миссии (безопасность стрельбы)", "Echte Reichweiten ab der nächsten Mission ausgesetzt (Feuersicherheit)", "从下一个任务起暂停真实射程（射击安全）"),
            (TxtKey.N_RANGES_WATCHED, "Sécurité des tirs : aucun tir depuis 5 min, vraies portées surveillées", "Firing safety: no shots for 5 min, real ranges are being watched", "Безопасность стрельбы: 5 мин без стрельбы, реальные дальности под наблюдением", "Feuersicherheit: seit 5 Min. kein Schuss, echte Reichweiten werden überwacht", "射击安全：5 分钟内没有射击，正在监控真实射程"),
            (TxtKey.N_PRECISION_OFF_BATTLE, "Précision anti-hélico : coupée pour une bataille par sécurité (les parties précédentes ne se sont pas terminées normalement)",
                "Anti-helicopter accuracy: switched off for one battle for safety (the previous games did not end normally)",
                "Точность стрельбы по вертолётам: отключена на один бой для безопасности (предыдущие игры завершились некорректно)",
                "Treffgenauigkeit gegen Hubschrauber: für ein Gefecht aus Sicherheitsgründen abgeschaltet (die vorherigen Partien endeten nicht normal)",
                "对直升机射击精度：出于安全关闭一场战斗（之前的对局没有正常结束）"),
            (TxtKey.N_PRECISION_NOT_LOADED, "Précision anti-hélico : lignes non chargées, tir normal remis pour cette bataille",
                "Anti-helicopter accuracy: data not loaded, normal fire restored for this battle",
                "Точность стрельбы по вертолётам: данные не загружены, на этот бой возвращена обычная стрельба",
                "Treffgenauigkeit gegen Hubschrauber: Daten nicht geladen, normales Feuer für dieses Gefecht wiederhergestellt",
                "对直升机射击精度：数据未加载，本场战斗恢复普通射击"),
        };
    }
}
