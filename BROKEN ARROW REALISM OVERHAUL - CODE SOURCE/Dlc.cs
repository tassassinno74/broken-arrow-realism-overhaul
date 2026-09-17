// DLC ownership of the local player, read with the game's own rule.
//  - The DLC list is GameConfig.Instance.UserItemsConfig.DLCContainers (Id, Name, ContentMembership).
//  - A container is owned when its user trigger "dlc_ownership_<Id>" is above 0 (NetworkUserService.GetTriggerFromRemoteOrLocal)
//    and the game helper UserItemsConfig.TryGetNotOwnedDLC does not list it. The store list GameMarketService.OwnedInternalDLC is
//    only written to the log.
//  - Fail closed: while the list cannot be read, only base game content (Vanilla) counts as owned.
//  - Nothing here edits a deck file or a data row; the CAMPAGNE divisions (Divisions) and the campaign deck merge (Campaign.BuildMerged)
//    ask Allowed() for each unit and each transport.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using MelonLoader;
using Il2CppBrokenArrow.DataBase.Models;

namespace RealismOverhaul
{
    static class Dlc
    {
        const int Vanilla = -1;               // Il2CppNetworkCommon.Enums.ContentMembership.Vanilla
        const long TickMs = 10_000;

        /// True once the DLC list and every ownership trigger were read (false = only base game content is allowed).
        internal static bool Known { get; private set; }

        /// Owned DLC memberships, sorted ("DLC2,DLC3"), "aucun" when none, "?" while unknown.
        internal static string Signature { get; private set; } = "?";

        static HashSet<int> _owned = new() { Vanilla };
        static bool _everRefreshed;
        static long _lastRefresh, _unknownSince = -1, _start = -1;
        static string _lastLine, _lastSig;
        static readonly HashSet<string> _reported = new();

        /// Seconds spent without a readable DLC list (0 once it is known).
        internal static double SecondsUnknown
        {
            get
            {
                if (Known) return 0;
                long now = Environment.TickCount64;
                if (_unknownSince < 0) _unknownSince = now;
                return (now - _unknownSince) / 1000.0;
            }
        }

        // ------------------------------------------------------------ queries
        internal static bool Owns(int membership) => membership == Vanilla || _owned.Contains(membership);

        internal static bool Owns(string membership)
        {
            if (string.IsNullOrWhiteSpace(membership)) return false;
            if (string.Equals(membership.Trim(), "Vanilla", StringComparison.OrdinalIgnoreCase)) return true;
            return Enum.TryParse(membership.Trim(), true, out Il2CppNetworkCommon.Enums.ContentMembership m) && Owns((int)m);
        }

        /// A unit may enter a CAMPAGNE division or a campaign deck: base game content, or a DLC the player owns (with InclureUnitesDLC).
        internal static bool Allowed(Units u)
        {
            int m = MembershipOf(u);
            if (m == Vanilla) return true;
            return Mod.InclureUnitesDLC.Value && _owned.Contains(m);
        }

        /// The unit's content tag as a number; an unreadable unit counts as not base game content (fail closed).
        internal static int MembershipOf(Units u)
        {
            if (u == null) return int.MinValue;
            try { return (int)u.ContentMembership; }
            catch (Exception e) { Report("Units.ContentMembership", e); return int.MinValue; }
        }

        internal static string MembershipName(Units u) => Label(MembershipOf(u));

        static string Label(int membership) =>
            membership == int.MinValue ? "?" : ((Il2CppNetworkCommon.Enums.ContentMembership)membership).ToString();

        // ------------------------------------------------------------ refresh
        /// Every 10 s from the division check (Divisions.EnsureInjected), outside a battle (except the very first reading) and never in a
        /// mission played without the mod.
        internal static void Tick()
        {
            if (Campaign.MissionInerte) return;
            if (_everRefreshed && BattleLive()) return;
            Refresh(false);
        }

        /// True while a battle is running (a game session with a current player).
        internal static bool BattleLive()
        {
            try { return Campaign.Ctx()?.CurrentPlayer != null; } catch { return false; }
        }

        /// Reads the ownership again (force = ignore the 10-s throttle). alwaysLog writes the state line even when nothing changed.
        internal static void Refresh(bool force, bool alwaysLog = false)
        {
            long now = Environment.TickCount64;
            if (_start < 0) _start = now;
            if (!force && _everRefreshed && now - _lastRefresh < TickMs) return;
            _lastRefresh = now;
            _everRefreshed = true;
            CreatePrefs();
            Snapshot s;
            try { s = ReadGame(); }
            catch (Exception e) { Report("liste des DLC", e); s = new Snapshot { Known = false, Reason = "API du jeu illisible" }; }
#if !PUBLIC
            try { Simulate(s, now - _start); }
            catch (Exception e) { Report("simulation", e); }
#endif
            Publish(s, now, alwaysLog);
        }

        sealed class Container
        {
            internal int Id, Membership;
            internal string Name;
            internal uint Trigger;
        }

        sealed class Snapshot
        {
            internal bool Known;
            internal string Reason, Market = "?", StoreInfo = "?", HelperNote;
            internal readonly List<Container> Containers = new();
            internal readonly HashSet<int> Owned = new() { Vanilla };
        }

        /// Everything that names a game member lives in NoInlining methods: a member removed by a game update fails here, inside the
        /// caller's try/catch, and only turns the ownership into "unknown".
        [MethodImpl(MethodImplOptions.NoInlining)]
        static Snapshot ReadGame()
        {
            var s = new Snapshot();
            var conts = ReadContainers();
            if (conts == null || conts.Count == 0) { s.Known = false; s.Reason = "liste des DLC absente"; return s; }

            int market = SafeMarket();
            s.Market = market < 0 ? "?" : ((Il2CppNetworkCommon.Enums.GameMarketType)(byte)market).ToString();
            HashSet<int> notOwned = null;
            if (market > 0 && market != 255)
            {
                try { notOwned = ReadNotOwned((byte)market); }
                catch (Exception e) { Report("UserItemsConfig.TryGetNotOwnedDLC", e); notOwned = null; s.HelperNote = "aide du jeu illisible, déclencheurs seuls"; }
            }
            else s.HelperNote = "boutique inconnue, déclencheurs seuls";
            try { s.StoreInfo = ReadStoreInfo(); } catch (Exception e) { Report("GameMarketService.OwnedInternalDLC", e); s.StoreInfo = "?"; }

            s.Known = true;
            foreach (var c in conts)
            {
                try { c.Trigger = ReadTrigger(c.Id); }
                catch (Exception e)
                {
                    Report("NetworkUserService.GetTriggerFromRemoteOrLocal", e);
                    s.Known = false;
                    s.Reason = "déclencheur dlc_ownership_" + c.Id.ToString(CultureInfo.InvariantCulture) + " illisible";
                    break;
                }
                s.Containers.Add(c);
                // a membership is owned when any owned container carries it
                if (c.Trigger > 0 && (notOwned == null || !notOwned.Contains(c.Id))) s.Owned.Add(c.Membership);
            }
            if (!s.Known) { s.Owned.Clear(); s.Owned.Add(Vanilla); }
            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<Container> ReadContainers()
        {
            var arr = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig.Instance?.UserItemsConfig?.DLCContainers;
            if (arr == null) return null;
            var res = new List<Container>(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                if (c == null) continue;
                res.Add(new Container { Id = c.Id, Name = c.Name ?? "", Membership = (int)c.ContentMembership });
            }
            return res;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static uint ReadTrigger(int containerId) =>
            Il2CppBrokenArrow.Client.Ecs.GNetwork.Authorization.User.NetworkUserService.GetTriggerFromRemoteOrLocal("dlc_ownership_" + containerId.ToString(CultureInfo.InvariantCulture));

        /// Store type as a number (-1 = unreadable): the static market first, the market service second.
        static int SafeMarket()
        {
            int m = -1;
            try { m = ReadStaticMarket(); } catch (Exception e) { Report("GameMarket.Type", e); }
            if (m > 0 && m != 255) return m;
            try { int svc = ReadServiceMarket(); if (svc >= 0) return svc; } catch (Exception e) { Report("GameMarketService.MarketType", e); }
            return m;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ReadStaticMarket() => (int)Il2CppBrokenArrow.Client.Ecs.GNetwork.Authorization.GameMarket.Type;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int ReadServiceMarket()
        {
            var gms = Mod.Svc<Il2CppBrokenArrow.Client.Ecs.GNetwork.Authorization.GameMarketService>();
            return gms == null ? -1 : (int)gms.MarketType;
        }

        /// Container ids the game helper reports as not owned (its return value is not used, only the list).
        [MethodImpl(MethodImplOptions.NoInlining)]
        static HashSet<int> ReadNotOwned(byte market)
        {
            var res = new HashSet<int>();
            Il2CppBrokenArrow.Client.Ecs.Configs.UserItems.UserItemsConfig.TryGetNotOwnedDLC(
                (Il2CppNetworkCommon.Enums.GameMarketType)market,
                out Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppBrokenArrow.Client.Ecs.Configs.UserItems.DLCContainer> no);
            for (int i = 0; i < (no?.Length ?? 0); i++) if (no[i] != null) res.Add(no[i].Id);
            return res;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ReadStoreInfo()
        {
            var gms = Mod.Svc<Il2CppBrokenArrow.Client.Ecs.GNetwork.Authorization.GameMarketService>();
            var arr = gms?.OwnedInternalDLC;
            if (arr == null) return "?";
            var ids = new List<string>();
            for (int i = 0; i < arr.Length; i++) ids.Add(arr[i].ToString(CultureInfo.InvariantCulture));
            return ids.Count == 0 ? "aucun" : string.Join(",", ids);
        }

        static void Publish(Snapshot s, long now, bool alwaysLog)
        {
            _owned = new HashSet<int>(s.Known ? s.Owned : new HashSet<int> { Vanilla });
            _owned.Add(Vanilla);
            Known = s.Known;
            if (Known) _unknownSince = -1;
            else if (_unknownSince < 0) _unknownSince = now;

            var ownedLabels = _owned.Where(m => m != Vanilla).Select(Label).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Signature = !Known ? "?" : ownedLabels.Count == 0 ? "aucun" : string.Join(",", ownedLabels);

            string line;
            if (!Known) line = $"[DLC] possédés : inconnu ({s.Reason}) -> unités DLC retirées par sécurité";
            else
            {
                string Named(Container c) => $"{Label(c.Membership)} « {c.Name} »";
                var own = s.Containers.Where(c => _owned.Contains(c.Membership)).Select(Named).Distinct().ToList();
                var not = s.Containers.Where(c => !_owned.Contains(c.Membership)).Select(Named).Distinct().ToList();
                string trig = string.Join(" ", s.Containers.Select(c => $"{c.Id.ToString(CultureInfo.InvariantCulture)}={c.Trigger.ToString(CultureInfo.InvariantCulture)}"));
                line = $"[DLC] possédés : {(own.Count == 0 ? "aucun" : string.Join(", ", own))} | non possédés : {(not.Count == 0 ? "aucun" : string.Join(", ", not))}" +
                       $" | marché={s.Market} | déclencheurs {trig} | {s.Market} (info) : {s.StoreInfo}" + (s.HelperNote != null ? $" | {s.HelperNote}" : "");
            }
            if (alwaysLog || line != _lastLine) Mod.Log.Msg(line);
            _lastLine = line;
            if (_lastSig != null && _lastSig != Signature) Mod.Log.Msg($"[DLC] changement : {_lastSig} -> {Signature}");
            _lastSig = Signature;
        }

        /// One log line per failing member (and message): a missing member after a game update says so plainly.
        static void Report(string member, Exception e)
        {
            var b = e.GetBaseException();
            bool missing = b is MissingMemberException || b is TypeLoadException || b is TypeInitializationException;
            if (!_reported.Add(member + "|" + b.GetType().Name + "|" + b.Message)) return;
            if (missing) Mod.Log.Warning($"[DLC] API du jeu introuvable (mise à jour ?) : {member} ({b.Message})");
            else Mod.Log.Warning($"[DLC] lecture impossible ({member}) : {b.Message}");
        }

        /// Creates the test pref of the personal build once (does nothing in the public build). Safe to call from OnInitializeMelon;
        /// Refresh also calls it. Not forced, no row in the Mod tab.
        internal static void CreatePrefs()
        {
#if !PUBLIC
            CreateTestPref();
#endif
        }

#if !PUBLIC
        // ------------------------------------------------------------ test pref (personal build only)
        static MelonPreferences_Entry<string> _simulate;
        static string _lastSimLine;

        static void CreateTestPref()
        {
            if (_simulate != null) return;
            try
            {
                var cat = MelonPreferences.CreateCategory("RealismOverhaul");
                _simulate = cat.CreateEntry("SimulerSansDLC", "",
                    description: "Test uniquement : DLC traités comme non possédés. Vide = rien. DLC3, DLC2, DLC2,DLC3, TOUS ; INCONNU = liste illisible ; " +
                                 "DLC3@90 = absent pendant 90 s puis possédé ; DLC3/90 = possédé pendant 90 s puis absent");
            }
            catch (Exception e) { Report("SimulerSansDLC", e); }
        }

        /// Applies SimulerSansDLC to the snapshot read from the game (seconds counted from the first ownership read).
        static void Simulate(Snapshot s, long elapsedMs)
        {
            string v = (_simulate?.Value ?? "").Trim();
            if (v.Length == 0) { if (_lastSimLine != null) { _lastSimLine = null; Mod.Log.Msg("[DLC] SIMULATION arrêtée"); } return; }
            var removed = new List<string>();
            bool unknown = false;
            double seconds = elapsedMs / 1000.0;
            foreach (var raw in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tok = raw.Trim().ToUpperInvariant();
                bool active = true;
                int at = tok.IndexOf('@'), slash = tok.IndexOf('/');
                if (at > 0 && int.TryParse(tok.Substring(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int until))
                { tok = tok.Substring(0, at); active = seconds < until; }          // missing first, owned after
                else if (slash > 0 && int.TryParse(tok.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int from))
                { tok = tok.Substring(0, slash); active = seconds >= from; }       // owned first, missing after
                if (!active) continue;
                if (tok == "INCONNU") { unknown = true; continue; }
                foreach (int m in s.Owned.ToList())
                {
                    if (m == Vanilla) continue;
                    string lbl = Label(m).ToUpperInvariant();
                    if (tok == "TOUS" || lbl == tok || (tok.StartsWith("DLC", StringComparison.Ordinal) && lbl.StartsWith(tok, StringComparison.Ordinal)))
                    {
                        s.Owned.Remove(m);
                        removed.Add(Label(m));
                    }
                }
            }
            if (unknown && s.Known) { s.Known = false; s.Reason = "simulation"; s.Owned.Clear(); s.Owned.Add(Vanilla); }
            string line = $"[DLC] SIMULATION (SimulerSansDLC={v}) : " + (unknown ? "liste illisible" : removed.Count == 0 ? "aucun DLC retiré pour l'instant" : "retirés " + string.Join(",", removed.Distinct()));
            if (line != _lastSimLine) { _lastSimLine = line; Mod.Log.Warning(line); }
        }
#endif
    }
}
