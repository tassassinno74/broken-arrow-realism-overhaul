// ModLog: every line the mod writes to the MelonLoader console and Latest.log goes through Mod.Log.
//  - Personal build: every line is written, unchanged.
//  - PUBLIC build (dotnet build -p:Public=true): every warning and error is written, and so are the startup, installation,
//    safety, patch, settings, DLC, campaign and deck, on-screen notice, cheat, watchdog and convoy relaunch lines and the
//    real-stats summary. Any other line is written at most once per module prefix ("[ASSIST]", "[SPAWN]"...) every 5 minutes,
//    followed by the number of lines of that prefix left out since the previous one, so a shared log stays short but useful.
//    Nothing in the mod reads the log back: the filter never changes what the mod does.
// Build: build flavour and preference descriptions (full texts in the personal build, short neutral texts in the PUBLIC build).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MelonLoader;

namespace RealismOverhaul
{
    internal sealed class ModLog
    {
        readonly MelonLogger.Instance _out;

        internal ModLog(MelonLogger.Instance output) { _out = output; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Msg(string text)
        {
#if PUBLIC
            text = PublicLog.Filter(text);
            if (text == null) return;
#endif
            _out.Msg(text);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Warning(string text) => _out.Warning(text);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Error(string text) => _out.Error(text);
    }

    static class Build
    {
#if PUBLIC
        internal static bool IsPublic => true;
#else
        internal static bool IsPublic => false;
#endif

        internal const string AutoText = "Réglage automatique, ne pas modifier.";

        /// Preference description: the full text in the personal build; in the PUBLIC build the short text, or AutoText when none is given.
        internal static string Desc(string full, string shortText = null)
        {
#if PUBLIC
            return shortText ?? AutoText;
#else
            return full;
#endif
        }
    }

#if PUBLIC
    /// Line filter of the PUBLIC build (thread-safe: some hooks log outside the main thread).
    static class PublicLog
    {
        const long WindowMs = 5L * 60L * 1000L;
        const string OtherKey = "(divers)";

        /// Lines always written, by start.
        static readonly string[] KeepStarts =
        {
            "v" + Identite.Version + " ",                 // version line
            "[PATCH]", "[SECURITE]", "[INSTALLATION]", "[REGLAGES]", "[DLC]",
            "[CAMPAGNE]",                                  // mission start and end, decks used, mission played without the mod (US_M01)
            "Injection des divisions CAMPAGNE", "  CAMPAGNE ", "Deck '", "Decks CAMPAGNE ",
            "[ECRAN]",                                     // on-screen notices (always logged in French)
            "[LANGUE]",                                    // game language read and the language of the mod's texts
            "[PERF] bilan",                                // time the mod itself takes per frame (one compact line every 2 minutes)
            "[TRICHE] touche ", "[TRICHE] résistance ON", "[TRICHE] résistance OFF", "[TRICHE] unités par carte ",
            "[TRICHE] remise à zéro", "[TRICHE] argent", "[TRICHE] cartes", "[TRICHE] soin",
            "[MISSION] suivi de la mission", "[MISSION] fin de bataille", "[MISSION] bilan ", "[MISSION] relance ",
            "[REALISME] appliqué", "[REALISME] annulé",
        };

        /// Lines always written when they start with the first text and contain the second one.
        static readonly (string Start, string Part)[] KeepStartAndPart =
        {
            ("[VRAIES STATS]", "valeurs mémorisées pour la restauration"),  // real-stats summary
            ("[SPAWN]", " en erreur : "),                                   // hook exception (logged once)
            ("[SPAWN]", "traitement du crochet"),                           // drain exception
            ("[SPAWN]", "bilan crochets"),                                  // battle-end hook summary
        };

        /// Lines always written when they contain one of these texts: watchdog trips, module errors, hook install failures.
        static readonly string[] KeepParts =
        {
            "chien de garde", "garde-fou",
            " : erreur (",
            "méthode introuvable",
            "introuvable dans cette version du jeu",
            "impossible dans cette version du jeu",
            "non installé (",
            "SANS compteur d'impacts",
            "point(s) d'accroche installé(s)",
        };

        static readonly object _lock = new object();
        static readonly Dictionary<string, long> _next = new(StringComparer.Ordinal);
        static readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);

        /// The text to write (maybe with a count of left-out lines), or null to write nothing.
        internal static string Filter(string text)
        {
            if (text == null) return null;
            try
            {
                if (Keep(text)) return text;
                string key = Key(text);
                long now = Environment.TickCount64;
                lock (_lock)
                {
                    if (_next.TryGetValue(key, out long next) && now < next)
                    {
                        _skipped[key] = (_skipped.TryGetValue(key, out int s) ? s : 0) + 1;
                        return null;
                    }
                    _next[key] = now + WindowMs;
                    if (_skipped.TryGetValue(key, out int n) && n > 0)
                    {
                        _skipped[key] = 0;
                        return text + $" (+{n} autre(s) ligne(s) {key} non écrite(s))";
                    }
                }
                return text;
            }
            catch { return text; }
        }

        static bool Keep(string text)
        {
            foreach (var s in KeepStarts)
                if (text.StartsWith(s, StringComparison.Ordinal)) return true;
            foreach (var (start, part) in KeepStartAndPart)
                if (text.StartsWith(start, StringComparison.Ordinal) && text.IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            // periodic reports ("[MODULE] relevé ...") go through the 5-minute limit even when they quote the watchdog
            int end = text.Length > 1 && text[0] == '[' ? text.IndexOf("] ", 1, StringComparison.Ordinal) : -1;
            if (end > 0 && end <= 40 && text.Length >= end + 8 && string.CompareOrdinal(text, end + 2, "relevé", 0, 6) == 0) return false;
            foreach (var p in KeepParts)
                if (text.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// Module prefix of a line ("[ASSIST]"), or OtherKey for a line without one.
        static string Key(string text)
        {
            if (text.Length > 1 && text[0] == '[')
            {
                int end = text.IndexOf(']', 1);
                if (end > 0 && end <= 40) return text.Substring(0, end + 1);
            }
            return OtherKey;
        }
    }
#endif
}
