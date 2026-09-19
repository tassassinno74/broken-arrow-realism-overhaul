// ModLog: every line the mod writes to the MelonLoader console and Latest.log goes through Mod.Log.
//  - Personal build: every line is written, unchanged.
//  - PUBLIC build (dotnet build -p:Public=true): every warning and error is written, and so are the startup, installation,
//    safety, patch, settings, DLC, campaign and deck, on-screen notice, cheat, time-of-day, watchdog and convoy relaunch lines
//    and the real-stats summary, plus, for each module that changes a value of the game or measures one the author must read back,
//    the one or two lines a battle it owes the log - never its periodic reports. Any other line is written at most once per module
//    prefix ("[ASSIST]", "[SPAWN]"...) every 5 minutes,
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
            "[DECOR]", "[EPAVES]", "[CARGO]",             // measurements the author needs back from a shared build to set these values
            "[CAMPAGNE]",                                  // mission start and end, decks used, mission played without the mod (US_M01)
            "Injection des divisions CAMPAGNE", "  CAMPAGNE ", "Deck '", "Decks CAMPAGNE ",
            "[ECRAN]",                                     // on-screen notices (always logged in French)
            "[LANGUE]",                                    // game language read and the language of the mod's texts
            "[PERF] bilan",                                // time the mod itself takes per frame (one compact line every 2 minutes)
            "[TRICHE] touche ", "[TRICHE] résistance ON", "[TRICHE] résistance OFF", "[TRICHE] unités par carte ",
            "[TRICHE] remise à zéro", "[TRICHE] argent", "[TRICHE] cartes", "[TRICHE] soin",
            "[MISSION] suivi de la mission", "[MISSION] fin de bataille", "[MISSION] bilan ", "[MISSION] relance ",
            "[ESQUIVE] bilan",                             // vol bas des hélicos : le bilan de fin de bataille
            "[DEBARQUEMENT] bilan",                        // durée réelle des débarquements : le chiffre attendu pour trancher sur les transports
            "[DEBARQUEMENT] réglages",                     // les réglages de débarquement du jeu, une fois par bataille
            "[DEBARQUEMENT] ATTENTION",                    // un débarquement de plus de 60 s : la seule chose qui pourrait retarder une étape
            "[VOL-BAS] le vol bas coûte",                  // l'état des trois règles du vol bas, une fois par changement
            "[VUE-AA] règles de vue propre",               // l'état des deux règles de vue (infanterie, véhicules), une fois par changement
            "[DISCRETION] capacité",                       // capacité « discrétion » rallumée : ce qui a été marqué au chargement
            "[DISCRETION] bilan",                          // fin de bataille : ce que la mesure a vu
            "[HEURE]",                                     // heure de la mission : quelques lignes par bataille (relevé de la carte, heure appliquée, script qui pilote la lumière)
            "[REALISME] appliqué", "[REALISME] annulé",
            "[ASSIST] artillerie :",          // artillerie : une ligne compacte par passe (pièces prêtes, cibles, salves, refus)
            "[ASSIST] [feu à volonté]",       // une ligne par salve de feu à volonté (quelle pièce sur quel ennemi)
            "[ASSIST] [contre-batterie]",     // une ligne par salve de contre-batterie
            "[ASSIST] feu à volonté :",       // pannes du feu à volonté (répartition coupée, trop de pièces dans la même passe)
            // v0.24.0 modules. One rule for the whole block: a line kept here is written once per session or once per battle, never
            // on a timer. The periodic reports of the very same modules ("[PROTECTION] dégâts évités" every 30 s, "[CRITIQUES] relevé"
            // and "[SUPPRESSION] relevé" every 60 s) are deliberately left out, so they keep going through the 5-minute limit and a
            // shared log does not grow by a hundred lines a battle. Warnings are never filtered, so refusals and errors are already
            // safe and are not listed here.
            "[CARGO]",                          // mort du transport : les neuf valeurs du jeu lues AVANT toute écriture — les chiffres sur lesquels le facteur sera réglé — ce qui a été écrit, et le bilan de la bataille (au plus quatre lignes par bataille)
            "[PROTECTION ARME]",                // quelle mission a été reconnue et quel palier s'est armé : une ligne par bataille
            "[PROTECTION BILAN]",               // le rappel de fin de bataille, règle par règle
            "[PROTECTION] mission ",            // même chose quand ces deux lignes gardent le préfixe simple
            "[PROTECTION] correctif ",          // les correctifs de dégâts : posés ou non
            "[PROTECTION] bilan",               // fin de bataille : tirs et coups annulés, unités encore protégées (la ligne périodique « dégâts évités » reste, elle, limitée)
            "[SUPPRESSION] valeurs du jeu AVANT",   // les trois niveaux de stress tels que le jeu les donne, avant qu'une seule valeur soit écrite
            "[SUPPRESSION] suppression ",       // ce qui a été écrit sur les niveaux choqué et paniqué, ou pourquoi rien ne l'a été
            "[SUPPRESSION] bilan",              // fin de bataille : combien de fois le jeu a vraiment appliqué le stress à une unité
            "[SUPPRESSION] effets du stress rendus",         // les valeurs rendues au jeu : la preuve que couper le mod remet tout en place
            "[SUPPRESSION] les effets du stress ont été rendus",  // rendus par une remise à zéro générale, puis réécrits
            "[CRITIQUES] bilan",                // fin de bataille : la mesure des dégâts critiques
            "[CRITIQUES] verdict",              // ce que cette mesure conclut, en français
            "[NUIT]",                           // signature de tir de nuit : les refus une fois par session, une ou deux lignes par bataille
            "[METEO]",                          // météo de la bataille : la vue au sol écrite pour les DEUX camps, ou la raison pour laquelle rien n'a été touché
            "[REPERAGE-VUE]",                   // relevé du brouillard de guerre : deux passes par bataille, lecture seule, avec son verdict
            "[BATIMENTS]",                      // relevé des bâtiments de la carte : une seule ligne par bataille dans cette build, et rien n'est écrit dans le jeu
            "[BILAN]",                          // bilan de fin de mission : un seul appel à Msg, donc tout le bilan passe ou rien
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
            "changement d'étape des leurres automatiques",
            "écart rogné de",                  // artillerie : l'écart de tir a été réduit pour rester dans la portée de la pièce
            "point retenu par le jeu",         // artillerie : le point d'impact retenu sortait de la portée, ordre annulé tout de suite
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
