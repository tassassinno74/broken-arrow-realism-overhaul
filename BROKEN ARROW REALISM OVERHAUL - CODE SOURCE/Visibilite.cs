// RealismOverhaul — which enemy units a team has really spotted (fog of war), shared by Assistants (FAV, repli, embuscade, ravitaillement) and EnemyAi.
// Several game sources are tried in order, each self-tested per battle on real units:
//   (a) FogOfWarHelper.IsHiddenForTeam   (b) FogOfWarVisibleMarker / InvisibleComponent tags   (c) GameplayBus.CountUnits   (d) AI detection groups.
// A source is trusted only after it has shown the team's own units as visible AND at least one enemy as hidden.
// An empty list from an unproven source means "unknown" (usable = false), never "nothing spotted".
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;
using V3 = UnityEngine.Vector3;
using TeamSide = Il2CppNetworkCommon.Enums.TeamSide;
using FowHelper = Il2CppBrokenArrow.Client.Ecs.FogOfWar.FogOfWarHelper;
using FowVisibleMarker = Il2CppBrokenArrow.Client.Ecs.FogOfWar.Components.FogOfWarVisibleMarker;
using FowInvisible = Il2CppBrokenArrow.Client.Ecs.FogOfWar.Components.InvisibleComponent;
using AiTargetDetect = Il2CppBrokenArrow.Client.Ecs.AI.Systems.AiTargetDetectionSystem;
using IlIntSet = Il2CppSystem.Collections.Generic.HashSet<int>;
using IlIntList = Il2CppSystem.Collections.Generic.List<int>;
using IlTeamSet = Il2CppSystem.Collections.Generic.HashSet<Il2CppNetworkCommon.Enums.TeamSide>;

namespace RealismOverhaul
{
    /// One visibility answer (cached 1 s and shared: callers must never modify it).
    internal sealed class VisResult
    {
        internal readonly List<(V3 pos, int role, int uid)> Units = new();   // role -1 = aircraft / helicopter (artillery cannot hit it)
        internal readonly HashSet<int> Uids = new();
        internal string Source = "aucune";
        internal bool Usable;
        internal int Enemies;                                                  // alive enemy units on the map (diagnostics only, never a target list)
    }

    static class Visibility
    {
        const int SRC_HIDDEN = 0, SRC_MARKER = 1, SRC_NOT_INVISIBLE = 2, SRC_COUNT = 3, SRC_GROUPS = 4, SRC_N = 5;
        const int MaxSweeps = 5;
        static readonly string[] SrcNames = { "IsHiddenForTeam", "marqueur visible", "sans InvisibleComponent", "CountUnits", "groupes IA" };

        sealed class Src
        {
            public bool Dead, Rejected, Validated, SawHidden;
            public float OwnBadSince = -1f;
            public int Visible = -1, OwnHidden, OwnChecked;
        }

        sealed class TeamState
        {
            public readonly Src[] S = { new Src(), new Src(), new Src(), new Src(), new Src() };
            public IlTeamSet Observed;
            public int Chosen = -2, GroupKey = -1, Summaries, GroupLogs, ChoiceLogs;
            public float FirstEnemies = -1f, NextSummary;
            public bool Noticed, OwnersLogged;
            public string LastGroups;
            public VisResult Cache;
            public float CacheUntil;
            public int CacheEnemy = -1;
        }

        /// Field values of CountUnitsData tried by the sweep (the defaults the mod used returned 0 units, even without the spotted filter).
        sealed class Combo
        {
            public bool AllTypes, AllCats, AroundAll, ZeroSentinels, NullGroup;
            public override string ToString() =>
                $"types={(AllTypes ? "tous" : "0")} catégories={(AllCats ? "toutes" : "0")} zone={(AroundAll ? "1e6 m" : "0")} sentinelles={(ZeroSentinels ? "0" : "-1")} groupe={(NullGroup ? "null" : "vide")}";
        }

        static readonly Dictionary<int, TeamState> _teams = new();
        static readonly Dictionary<string, float> _warnNext = new();
        static readonly Dictionary<string, (PropertyInfo prop, object all)> _allBits = new();
        static readonly Combo DefaultCombo = new();
        static LuaMap _map;
        static Combo _combo;
        static int _sweeps;
        static float _nextSweep;

        /// A CountUnits field combination returned the player's own units (EnemyAi's hidden / embarked filter can trust it).
        internal static bool CountUnitsProven => _combo != null;
        /// Every sweep of this battle failed: CountUnits will not work.
        internal static bool CountUnitsGaveUp => _combo == null && _sweeps >= MaxSweeps;

        static void Log(string s) => Mod.Log.Msg("[VISIBILITE] " + s);

        static bool WarnDue(string key)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_warnNext.TryGetValue(key, out var t) && now < t) return false;
            _warnNext[key] = now + 30f;
            return true;
        }

        static void Warn(string key, string msg) { if (WarnDue(key)) Log(msg); }

        /// Per-battle reset (called by Assistants.ResetSession).
        internal static void ResetSession()
        {
            _teams.Clear(); _warnNext.Clear();
            _map = null; _combo = null; _sweeps = 0; _nextSweep = 0f;
        }

        /// Enemy units (team enemyTeam) that team myTeam has really spotted. Cached 1 s per team.
        /// notify: one on-screen notice per battle when no source could be validated after 60 s with enemies on the map.
        internal static VisResult VisibleEnemies(GameController gc, int myTeam, int enemyTeam, bool notify = false)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_teams.TryGetValue(myTeam, out var ts)) _teams[myTeam] = ts = new TeamState();
            if (ts.Cache == null || now >= ts.CacheUntil || ts.CacheEnemy != enemyTeam)
            {
                var res = new VisResult();
                try { if (gc != null) Evaluate(gc, ts, myTeam, enemyTeam, now, res); }
                catch (Exception e)
                {
                    res.Units.Clear(); res.Uids.Clear(); res.Usable = false; res.Source = "erreur";
                    Warn("eval" + myTeam, $"camp {myTeam} : lecture de la visibilité impossible : {e.Message}");
                }
                ts.Cache = res; ts.CacheUntil = now + 1f; ts.CacheEnemy = enemyTeam;
            }
            if (notify && !ts.Noticed && !ts.Cache.Usable && ts.FirstEnemies >= 0f && now - ts.FirstEnemies >= 60f)
            {
                ts.Noticed = true;
                Log($"camp {myTeam} : aucune source de visibilité utilisable après 60 s ({Counts(ts, ts.Cache.Enemies)})");
                Mod.Notify(TxtKey.N_FAV_NO_VISIBILITY);
            }
            return ts.Cache;
        }

        /// CountUnits on an explicit UID set with the combination proven on the player's own units (the old defaults until one is found); null if the bus is missing.
        internal static IlIntList CountUnits(GameController gc, IlIntSet set, bool onlySpotted, bool withCargo, bool withHidden)
        {
            var gp = gc?._GetEcsEventBus_k__BackingField?.Gameplay;
            if (gp?.CountUnits == null) return null;
            try { var cp = gc._GameSession_k__BackingField?.CurrentPlayer; if (cp != null) Sweep(gc, cp.UID, UnityEngine.Time.realtimeSinceStartup); }
            catch (Exception e) { Warn("sweep", "CountUnits : essai impossible : " + e.Message); }
            var d = NewData(set, _combo ?? DefaultCombo, onlySpotted, withCargo, withHidden);
            return gp.CountUnits.Invoke(ref d);
        }

        // ------------------------------------------------------------ evaluation of every source
        static void Evaluate(GameController gc, TeamState ts, int myTeam, int enemyTeam, float now, VisResult res)
        {
            _map ??= new LuaMap();
            var enemies = AliveUnits(enemyTeam, int.MaxValue);
            var own = AliveUnits(myTeam, 40);
            res.Enemies = enemies.Count;
            if (enemies.Count > 0 && ts.FirstEnemies < 0f) ts.FirstEnemies = now;
            if (!ts.OwnersLogged && enemies.Count > 0) { ts.OwnersLogged = true; LogOwners(enemies, myTeam, enemyTeam); }

            var vis = new bool[SRC_N][];
            if (ts.Observed == null) { ts.Observed = new IlTeamSet(); ts.Observed.Add((TeamSide)myTeam); }
            var observed = ts.Observed;
            var side = (TeamSide)myTeam;
            vis[SRC_HIDDEN] = UnitSource(ts, SRC_HIDDEN, myTeam, own, enemies, now, u => FowHelper.IsHiddenForTeam(u.Entity, side, observed));
            vis[SRC_MARKER] = UnitSource(ts, SRC_MARKER, myTeam, own, enemies, now, u => !u.Entity.Has<FowVisibleMarker>());
            vis[SRC_NOT_INVISIBLE] = UnitSource(ts, SRC_NOT_INVISIBLE, myTeam, own, enemies, now, u => u.Entity.Has<FowInvisible>());
            vis[SRC_COUNT] = CountSource(gc, ts, myTeam, enemies, now);
            vis[SRC_GROUPS] = GroupSource(ts, myTeam, enemyTeam, enemies, now);

            int chosen = -1;
            for (int k = 0; k < SRC_N; k++)
            {
                var s = ts.S[k];
                if (s.Validated && !s.Rejected && !s.Dead && s.OwnBadSince < 0f && vis[k] != null) { chosen = k; break; }
            }
            if (chosen != ts.Chosen)
            {
                if (ts.ChoiceLogs++ < 20)
                    Log($"camp {myTeam} : source de visibilité {(chosen >= 0 ? $"retenue '{SrcNames[chosen]}'" : "AUCUNE confirmée pour l'instant")} ; {Counts(ts, enemies.Count)}");
                ts.Chosen = chosen;
            }
            if (enemies.Count > 0 && ts.Summaries < 5 && now >= ts.NextSummary)
            {
                ts.Summaries++; ts.NextSummary = now + 120f;
                Log($"camp {myTeam} : source {(chosen >= 0 ? SrcNames[chosen] : "aucune")} ; {Counts(ts, enemies.Count)}");
            }

            if (chosen < 0) { res.Usable = false; res.Source = "aucune"; return; }
            var v = vis[chosen];
            for (int i = 0; i < enemies.Count && i < v.Length; i++)
            {
                if (!v[i]) continue;
                var e = enemies[i];
                try
                {
                    int uid = e.UID;
                    res.Units.Add((e.GetPosition(), Assistants.IsAirUnit(e) ? -1 : e.UnitRole, uid));
                    res.Uids.Add(uid);
                }
                catch { }
            }
            res.Usable = true; res.Source = SrcNames[chosen];
        }

        static List<LuaUnit> AliveUnits(int team, int max)
        {
            var res = new List<LuaUnit>();
            var arr = _map.GetUnits(V3.zero, 1_000_000f, team, -1);
            for (int i = 0; i < (arr?.Length ?? 0) && res.Count < max; i++)
            {
                var u = arr[i];
                try { if (u != null && u.IsAlive()) res.Add(u); } catch { }
            }
            return res;
        }

        /// (a) and (b): a per-unit test. isHidden throwing on every unit switches the source off for the battle (a generic Has<T> may have no AOT instance).
        static bool[] UnitSource(TeamState ts, int k, int myTeam, List<LuaUnit> own, List<LuaUnit> enemies, float now, Func<LuaUnit, bool> isHidden)
        {
            var s = ts.S[k];
            if (s.Dead || s.Rejected) return null;
            int tries = 0, errors = 0, ownHidden = 0, ownChecked = 0, enemiesChecked = 0, visible = 0;
            string firstError = null;
            foreach (var o in own)
            {
                tries++;
                try { if (isHidden(o)) ownHidden++; ownChecked++; }
                catch (Exception e) { errors++; firstError ??= e.Message; }
            }
            var vis = new bool[enemies.Count];
            for (int i = 0; i < enemies.Count; i++)
            {
                tries++;
                try { if (!isHidden(enemies[i])) { vis[i] = true; visible++; } enemiesChecked++; }
                catch (Exception e) { errors++; firstError ??= e.Message; }
            }
            if (tries > 0 && errors == tries)
            {
                s.Dead = true;
                Log($"camp {myTeam} : source '{SrcNames[k]}' coupée pour la bataille ({firstError})");
                return null;
            }
            Judge(ts, k, myTeam, ownChecked, ownHidden, enemiesChecked, visible, now);
            return vis;
        }

        /// Self-test shared by every source: own units must not read as hidden, and at least one enemy must have read as hidden once.
        static void Judge(TeamState ts, int k, int myTeam, int ownChecked, int ownHidden, int enemiesChecked, int visible, float now)
        {
            var s = ts.S[k];
            s.Visible = visible; s.OwnChecked = ownChecked; s.OwnHidden = ownHidden;
            // most of the team's own units "hidden" from the team itself: the source reads something else (passengers may legitimately read hidden)
            bool ownBad = ownChecked > 0 && ownHidden * 4 > ownChecked * 3;
            if (ownBad)
            {
                if (s.OwnBadSince < 0f) s.OwnBadSince = now;
                else if (now - s.OwnBadSince >= 20f)
                {
                    s.Rejected = true; s.Validated = false;
                    Log($"camp {myTeam} : source '{SrcNames[k]}' rejetée pour la bataille : {ownHidden}/{ownChecked} unités de ce camp vues comme cachées depuis 20 s");
                    return;
                }
            }
            else s.OwnBadSince = -1f;
            if (enemiesChecked > 0 && visible < enemiesChecked) s.SawHidden = true;
            if (!s.Validated && s.SawHidden && ownChecked > 0 && !ownBad)
            {
                s.Validated = true;
                Log($"camp {myTeam} : source '{SrcNames[k]}' confirmée (ennemis {enemiesChecked}, visibles {visible}, unités du camp cachées {ownHidden}/{ownChecked})");
            }
        }

        // ------------------------------------------------------------ (c) CountUnits
        static bool[] CountSource(GameController gc, TeamState ts, int myTeam, List<LuaUnit> enemies, float now)
        {
            var s = ts.S[SRC_COUNT];
            if (s.Dead || s.Rejected) return null;
            try { var cp = gc._GameSession_k__BackingField?.CurrentPlayer; if (cp != null) Sweep(gc, cp.UID, now); }
            catch (Exception e) { Warn("sweep", "CountUnits : essai impossible : " + e.Message); }
            if (_combo == null) return null;
            var gp = gc._GetEcsEventBus_k__BackingField?.Gameplay;
            if (gp?.CountUnits == null) return null;
            var vis = new bool[enemies.Count];
            if (enemies.Count == 0) { Judge(ts, SRC_COUNT, myTeam, 1, 0, 0, 0, now); return vis; }
            try
            {
                var set = new IlIntSet();
                var index = new Dictionary<int, int>();
                for (int i = 0; i < enemies.Count; i++) { try { int uid = enemies[i].UID; set.Add(uid); index[uid] = i; } catch { } }
                var dS = NewData(set, _combo, true, false, false);
                var spotted = gp.CountUnits.Invoke(ref dS);
                if (spotted == null) return null;
                int visible = 0;
                // copied before the next call: the game may reuse its result buffer
                for (int i = 0; i < spotted.Count; i++)
                    if (index.TryGetValue(spotted[i], out int ix) && !vis[ix]) { vis[ix] = true; visible++; }
                var dP = NewData(set, _combo, false, false, false);
                var present = gp.CountUnits.Invoke(ref dP);
                int presentCount = present?.Count ?? 0;
                // own units: the combination itself was proven on the player's units; "hidden" = on the map, not embarked, but not spotted
                Judge(ts, SRC_COUNT, myTeam, 1, 0, Math.Max(presentCount, visible), visible, now);
                return vis;
            }
            catch (Exception e)
            {
                s.Dead = true;
                Log($"camp {myTeam} : source 'CountUnits' coupée pour la bataille ({e.Message})");
                return null;
            }
        }

        /// Finds a field combination for which CountUnits (spotted filter off) returns the local player's own units. Retried every 30 s, 5 times per battle.
        static void Sweep(GameController gc, int local, float now)
        {
            if (_combo != null || _sweeps >= MaxSweeps || now < _nextSweep) return;
            var gp = gc._GetEcsEventBus_k__BackingField?.Gameplay;
            if (gp?.CountUnits == null) return;
            _map ??= new LuaMap();
            var arr = _map.GetUnits(V3.zero, 1_000_000f, -1, local);
            var set = new IlIntSet();
            int n = 0;
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                var u = arr[i];
                try { if (u != null && u.IsAlive() && u.GetOwnerPlayerUID() == local) { set.Add(u.UID); n++; } } catch { }
            }
            if (n == 0) { _nextSweep = now + 5f; return; }          // nothing to test on yet (units not deployed): not counted as a sweep
            _sweeps++; _nextSweep = now + 30f;
            string firstError = null;
            // mask 0 = the old defaults; combinations with a null group come last (bit 16)
            for (int mask = 0; mask < 32; mask++)
            {
                var c = new Combo { AllTypes = (mask & 1) != 0, AllCats = (mask & 2) != 0, AroundAll = (mask & 4) != 0, ZeroSentinels = (mask & 8) != 0, NullGroup = (mask & 16) != 0 };
                int got;
                try { var d = NewData(set, c, false, true, true); var l = gp.CountUnits.Invoke(ref d); got = l?.Count ?? -1; }
                catch (Exception e) { got = -2; firstError ??= e.Message; }
                if (got > 0)
                {
                    _combo = c;
                    Log($"CountUnits : combinaison confirmée sur tes unités ({got}/{n} renvoyées, essai {mask + 1}/32) : {c}");
                    return;
                }
            }
            Log($"CountUnits : aucune des 32 combinaisons ne renvoie tes {n} unités (tentative {_sweeps}/{MaxSweeps}{(firstError != null ? ", erreur : " + firstError : "")})");
        }

        static CountUnitsData NewData(IlIntSet set, Combo c, bool onlySpotted, bool withCargo, bool withHidden)
        {
            var d = new CountUnitsData();
            d.UnitUIDs = set;
            d.WithGroup = c.NullGroup ? null : "";
            int none = c.ZeroSentinels ? 0 : -1;
            d.InsideTriggerUID = none; d.AroundTagUID = none; d.WithPlayerUID = none; d.WithTeamUID = none;
            d.AroundPos = V3.zero;
            d.AroundPosRadius = c.AroundAll ? 1_000_000f : 0f;
            if (c.AllTypes) SetAllBits(d, "UnitTypeFilter");
            if (c.AllCats) SetAllBits(d, "CategoryFilter");
            d.OnlySpottedUnits = onlySpotted; d.IncludeCargo = withCargo; d.IncludeHidden = withHidden;
            return d;
        }

        /// Sets an enum filter of CountUnitsData to the OR of every defined value, through reflection (the enum values are not in the dumps).
        static void SetAllBits(CountUnitsData d, string name)
        {
            if (!_allBits.TryGetValue(name, out var e))
            {
                PropertyInfo p = null; object all = null; string info;
                try
                {
                    p = typeof(CountUnitsData).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.PropertyType.IsEnum)
                    {
                        long bits = 0;
                        foreach (var v in Enum.GetValues(p.PropertyType)) bits |= Convert.ToInt64(v);
                        all = Enum.ToObject(p.PropertyType, bits);
                        info = $"{bits} ({string.Join("/", Enum.GetNames(p.PropertyType))})";
                    }
                    else info = "propriété introuvable";
                }
                catch (Exception ex) { all = null; info = "erreur " + ex.Message; }
                _allBits[name] = e = (p, all);
                Log($"CountUnits : {name} 'tous' = {info}");
            }
            if (e.prop != null && e.all != null) e.prop.SetValue(d, e.all);
        }

        // ------------------------------------------------------------ (d) AI detection groups, mapped to real units (never a guessed role)
        static bool[] GroupSource(TeamState ts, int myTeam, int enemyTeam, List<LuaUnit> enemies, float now)
        {
            var s = ts.S[SRC_GROUPS];
            if (s.Dead || s.Rejected || myTeam < 0 || myTeam > 1 || enemyTeam < 0 || enemyTeam > 1) return null;
            try
            {
                var dict = AiTargetDetect._groups;
                if (dict == null) return null;
                var enemyIndex = new Dictionary<int, int>();
                for (int i = 0; i < enemies.Count; i++) { try { enemyIndex[enemies[i].Entity.EntityId] = i; } catch { } }
                var friendIds = new HashSet<int>();
                foreach (var f in AliveUnits(myTeam, int.MaxValue)) { try { friendIds.Add(f.Entity.EntityId); } catch { } }
                var vis = new bool[2][];
                var raw = new int[2]; var foes = new int[2]; var friends = new int[2];
                for (int key = 0; key < 2; key++)
                {
                    vis[key] = new bool[enemies.Count];
                    if (!dict.TryGetValue((TeamSide)key, out var list) || list == null) continue;
                    for (int g = 0; g < list.Count; g++)
                    {
                        var grp = list[g];
                        if (grp == null) continue;
                        raw[key]++;
                        var ents = grp.Entities;
                        if (ents == null) continue;
                        foreach (var kv in ents)
                        {
                            int id = kv.Key.EntityId;
                            if (enemyIndex.TryGetValue(id, out int ix)) { if (!vis[key][ix]) { vis[key][ix] = true; foes[key]++; } }
                            else if (friendIds.Contains(id)) friends[key]++;
                        }
                    }
                }
                string rawText = $"clé 0 : {raw[0]} groupe(s), {foes[0]} ennemi(s), {friends[0]} ami(s) ; clé 1 : {raw[1]} groupe(s), {foes[1]} ennemi(s), {friends[1]} ami(s)";
                if (rawText != ts.LastGroups && ts.GroupLogs < 20 && WarnDue("groups" + myTeam))
                {
                    ts.LastGroups = rawText; ts.GroupLogs++;
                    Log($"camp {myTeam} : groupes IA bruts {rawText}");
                }
                if (ts.GroupKey < 0)
                {
                    if (foes[enemyTeam] > 0 && friends[enemyTeam] == 0) ts.GroupKey = enemyTeam;
                    else if (foes[myTeam] > 0 && friends[myTeam] == 0) ts.GroupKey = myTeam;
                    if (ts.GroupKey < 0) return null;                 // mapping not proven yet
                    Log($"camp {myTeam} : les groupes IA des ennemis sont sous la clé {ts.GroupKey}");
                }
                int k = ts.GroupKey;
                Judge(ts, SRC_GROUPS, myTeam, 1, friends[k] > 0 ? 1 : 0, enemies.Count, foes[k], now);
                return vis[k];
            }
            catch (Exception e)
            {
                s.Dead = true;
                Log($"camp {myTeam} : source 'groupes IA' coupée pour la bataille ({e.Message})");
                return null;
            }
        }

        // ------------------------------------------------------------ logs
        static string Counts(TeamState ts, int enemies)
        {
            string One(int k)
            {
                var s = ts.S[k];
                if (s.Dead) return "coupée";
                if (s.Rejected) return "rejetée";
                if (s.Visible < 0) return "non lue";
                string own = k <= SRC_NOT_INVISIBLE ? $" (camp caché {s.OwnHidden}/{s.OwnChecked})" : "";
                return $"{s.Visible}{own}{(s.Validated ? "" : " en test")}";
            }
            return $"ennemis {enemies}, IsHiddenForTeam visibles {One(SRC_HIDDEN)}, marqueur visibles {One(SRC_MARKER)}, sans Invisible {One(SRC_NOT_INVISIBLE)}, CountUnits repérés {One(SRC_COUNT)}, groupes IA {One(SRC_GROUPS)}";
        }

        /// Confirms once per battle that GetUnits(team) really returns that team (co-op and allied-AI missions).
        static void LogOwners(List<LuaUnit> enemies, int myTeam, int enemyTeam)
        {
            var byTeam = new SortedDictionary<int, int>();
            foreach (var e in enemies)
            {
                int t = int.MinValue;
                try { t = e.GetOwnerTeamID(); } catch { }
                byTeam[t] = (byTeam.TryGetValue(t, out var c) ? c : 0) + 1;
            }
            Log($"camp {myTeam} : GetUnits(camp {enemyTeam}) = {enemies.Count} unités vivantes, GetOwnerTeamID : {string.Join(", ", byTeam.Select(kv => $"{(kv.Key == int.MinValue ? "?" : kv.Key.ToString())} x{kv.Value}"))}");
        }
    }
}
