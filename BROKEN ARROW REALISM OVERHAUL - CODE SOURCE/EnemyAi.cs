// RealismOverhaul v0.18 - IA ennemie en campagne (équipe des bots), appelée depuis Mod.OnUpdate dans `if (Campaign.InCampaign)`.
//  F1 EnemyAi   : posture de mission verrouillée, garnison des bâtiments, rayon de réaction (sans toucher ceux du script), retour en position,
//                 porte réseau : un seul PC donne des ordres aux bots (hôte), sinon rien (fail closed).
//  F2 Ops       : contre-attaques locales sur les groupes du joueur que l'IA a DÉTECTÉS, avec rapport de force, réserve gardée, laisse, repli, reprise bornée.
//  F3 Artillery : salve courte de préparation / tir d'arrêt par les pièces conventionnelles des bots (jamais Iskander / LAM) + réglage du preset (TunePreset, appelé par Assistants.AiBoost).
//  F4           : diagnostic d'argent des bots uniquement (aucun spawn ni argent ajouté).
//  v0.23.0      : units driven by the mission script (Missions.ScriptReason: script orders, active script moves, script spawns, groups named
//                 by script order nodes) are never garrisoned, given a reaction radius, sent on a counter-attack, sent home, fired as
//                 artillery or cancelled. A unit taken by the script leaves its op without any cancel; our own radius is dropped only when
//                 the script order does not carry its own. Every order of this file is wrapped in Missions.OwnOrderBegin/End so the
//                 script-order hooks never mistake it for a script order. Fail closed: while Missions.ProtectionNotReady() is not null
//                 (hooks refused / disarmed / incomplete, move system unknown, script not parsed, group membership not fully read) no
//                 garrison, radius, counter-attack or artillery order is given, and an ending op neither cancels nor sends its units home.
// Rien n'est écrit dans la base ici ; les réglages AiConfig passent par Realism.SetJournaled ; tout l'état de partie est remis à zéro par ResetSession.
using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.AI;
using Il2CppBrokenArrow.Client.Ecs.AI.Systems;
using Il2CppBrokenArrow.Client.Ecs.Campaign;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.Utils;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.Shared.Ecs;
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;
using Il2CppNetworkCommon.Enums.Common;
using V3 = UnityEngine.Vector3;
using TeamSide = Il2CppNetworkCommon.Enums.TeamSide;
using DifficultyLevel = Il2CppBrokenArrow.Shared.Ecs.Enums.DifficultyLevel;
using AggressiveRadiusSystem = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.AggressiveRadiusSystem;
using AggressiveRadiusComponent = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.AggressiveRadiusComponent;
using NetworkScenarioStorage = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using IlIntSet = Il2CppSystem.Collections.Generic.HashSet<int>;
using IlIntList = Il2CppSystem.Collections.Generic.List<int>;

namespace RealismOverhaul
{
    /// F1 : posture et défense des unités garées des bots + porte réseau partagée par F2 / F3 (et la boucle SetAIState d'Assistants.AiBoost).
    static class EnemyAi
    {
        internal static MelonPreferences_Entry<bool> Enabled, Garrison, Reaction, Counter, Arty;
        internal static MelonPreferences_Entry<string> Posture, PerMission, Orders;
        internal static MelonPreferences_Entry<float> Aggr;
        internal enum Stance { Unknown, Defense, Attack, Meeting }
        internal sealed class Doc { public float React, Frac, Sup, OpCd, Withdraw, ArtyCd; public int MaxOp, MaxOps, Batteries; }
        internal sealed class Track { public LuaUnit U; public V3 Home, LastPos; public int Role, OpId; public float IdleSince = -1, Cooldown, NextGarrison, LastFight = -999; public bool Radius, RadiusChecked, Garrisoned, Returning; public string Script; }

        // doctrine par difficulté : React, Frac, Sup, OpCd, Withdraw, ArtyCd, MaxOp, MaxOps, Batteries
        static readonly Doc DocEasy = new() { React = 500, Frac = .25f, Sup = 2f, OpCd = 120, Withdraw = .3f, ArtyCd = 120, MaxOp = 3, MaxOps = 1, Batteries = 0 };
        static readonly Doc DocMedium = new() { React = 700, Frac = .35f, Sup = 1.5f, OpCd = 90, Withdraw = .4f, ArtyCd = 90, MaxOp = 5, MaxOps = 2, Batteries = 1 };
        static readonly Doc DocHard = new() { React = 900, Frac = .5f, Sup = 1.2f, OpCd = 60, Withdraw = .5f, ArtyCd = 60, MaxOp = 8, MaxOps = 3, Batteries = 2 };

        static readonly Dictionary<int, Track> _t = new();
        static readonly Dictionary<int, bool> _bot = new();
        static readonly Dictionary<int, float> _botRetry = new();       // owner uid -> next TryGetPlayer attempt (unknown owners)
        static readonly Dictionary<long, float> _reserved = new();       // building shoot position -> reserved until (walk time only; HasUnitsInside covers it afterwards)
        static readonly Dictionary<long, bool> _objStart = new();
        static readonly List<V3> _loaded = new();
        static readonly HashSet<int> _ourRadius = new();                // uids carrying OUR radius (a dropped and re-created track must not mistake it for a script radius)
        static readonly HashSet<string> _logged = new();
        // units the mod itself added on the enemy side (Renforts.cs): their guard position is the place they arrived at, and they are
        // left alone until `quiet` so the module's own approach order is not cancelled by a counter-attack started the same minute.
        // Kept outside _t because TrackUnits rebuilds _t from the live list at every pass.
        static readonly Dictionary<int, (V3 home, float quiet)> _renfort = new();
        internal static Stance Current;
        internal static bool CanCommand;
        internal static LuaMap Map;
        internal static LuaAI Ai;
        static bool _locked, _announced, _coop, _radiusBroken, _cargoFilterOk = true;
        static float _next, _start = -1, _nextStance, _nextHost, _nextDiag, _nextMoney;
        static int _lastCand, _lastKept, _moveLogs;
        static int _protStarts, _protEnds, _protLogs, _protNow, _protRadius, _protOps;   // script units: protections started / ended, lines, now, radii handled, op releases
        static bool _wasCommanding, _lastFiltered, _protPaused;         // _protPaused: last tick had no trustworthy script-unit protection
        static int _resumeLogs;
        static int _localUid = -1;
        static string _hostStatus;

        static void Log(string s) => Mod.Log.Msg("[IA-ENNEMIE] " + s);
        /// One line per key per session (per-unit errors would otherwise repeat every 4 s).
        internal static void LogOnce(string key, string s) { if (_logged.Count < 500 && _logged.Add(key)) Log(s); }
        /// Movement lines (reaction / return / radius removed) are capped per battle: an out-and-back fight would repeat them.
        static void MoveLog(string s) { if (_moveLogs++ < 40) Log(s); }

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_IA");
            Enabled = c.CreateEntry("IAEnnemieActive", true, description: Build.Desc("Campagne : l'IA ennemie défend ses positions, contre-attaque localement ce qu'elle a détecté et prépare à l'artillerie"));
            Posture = c.CreateEntry("Posture", "auto", description: Build.Desc("auto / defense / attaque / rencontre"));
            PerMission = c.CreateEntry("PosturesParMission", "", description: Build.Desc("Posture forcée par mission, ex: US_M02=defense;RU_C01=attaque"));
            Orders = c.CreateEntry("OrdresIA", "auto", description: Build.Desc("auto = seul l'hôte donne les ordres aux bots ; oui = toujours (uniquement sur le PC hôte) ; non = jamais (à mettre sur le PC client si les deux journaux disent ACTIFS)"));
            Garrison = c.CreateEntry("GarnisonBatiments", true, description: Build.Desc("En défense, l'infanterie ennemie garée entre dans un bâtiment proche"));
            Reaction = c.CreateEntry("RayonReaction", true, description: Build.Desc("Les blindés ennemis garés sans rayon du script engagent ce qu'ils voient près d'eux puis reviennent en position"));
            Counter = c.CreateEntry("ContreAttaques", true, description: Build.Desc("Contre-attaques locales sur les groupes détectés, seulement avec la supériorité locale"));
            Arty = c.CreateEntry("PreparationArtillerie", true, description: Build.Desc("Salve courte de l'artillerie ennemie avant une contre-attaque (ou tir d'arrêt)"));
            Aggr = c.CreateEntry("Agressivite", 1f, description: Build.Desc("Multiplie la part de la réserve engagée (plafonds inchangés)"));
        }

        internal static void ResetSession()
        {
            _t.Clear(); _bot.Clear(); _botRetry.Clear(); _reserved.Clear(); _objStart.Clear(); _loaded.Clear(); _ourRadius.Clear(); _logged.Clear(); _renfort.Clear();
            Current = Stance.Unknown; CanCommand = false;
            _locked = _announced = _coop = _radiusBroken = false; _cargoFilterOk = true;
            _start = -1; _next = _nextStance = _nextHost = _nextDiag = _nextMoney = 0; _lastCand = _lastKept = _moveLogs = 0; _hostStatus = null; _wasCommanding = false; _lastFiltered = false; _localUid = -1;
            _protPaused = false; _resumeLogs = 0;
            _protStarts = _protEnds = _protLogs = _protNow = _protRadius = _protOps = 0;
            Map = null; Ai = null;
            Ops.Reset(); Artillery.Reset();
        }

        internal static Doc DocFor(DifficultyLevel d) => d == DifficultyLevel.Easy ? DocEasy : d == DifficultyLevel.Hard ? DocHard : DocMedium;

        /// Allocation-free gate: the closures of a tick live in Tick.
        internal static void Frame()
        {
            float now = UnityEngine.Time.time; if (now < _next) return;
            if (!Planif.Take(ref _wait)) return;                 // one heavy module job per frame (Planif.cs); the 4 s period is unchanged
            _next = now + 4f;
            Tick(now);
        }

        static int _wait;

        static void Tick(float now)
        {
            var gc = GameController._instance; var cp = gc?._GameSession_k__BackingField?.CurrentPlayer; if (gc == null || cp == null) return;
            Campaign.NoteSession();                                          // a restarted battle is never driven with the previous battle's tracks
            try { _localUid = cp.UID; } catch { _localUid = -1; }
            int playerTeam = (int)cp.TeamSide; if (playerTeam != 0 && playerTeam != 1) return;       // camp du joueur inconnu : rien (fail closed)
            int botTeam = 1 - playerTeam;
            if (_start < 0) _start = now; if (now - _start < 20f) return;
            if (now >= _nextHost) { _nextHost = now + 60f; Guard.Run("IA.Hote", () => HostCheck(playerTeam)); }
            bool commanding = CanCommand && Enabled.Value;
            if (_wasCommanding && !commanding) Guard.Run("IA.Coupure", DropOrders);   // orders switched off mid-battle: our radii and ops must not stay on the bots
            _wasCommanding = commanding;
            if (!Enabled.Value) return;
            var doc = DocFor(Mod.Svc<CampaignService>()?.Difficulty ?? DifficultyLevel.Medium);
            Map ??= new LuaMap(); Ai ??= new LuaAI();
            if (now >= _nextStance) { _nextStance = now + 60f; Guard.Run("IA.Posture", () => UpdateStance(gc, playerTeam, botTeam, now)); }
            if (!CanCommand || now - _start < 60f) return;
            if (now >= _nextMoney) { _nextMoney = now + 300f; Guard.Run("IA.Argent", () => MoneyDiag(now)); }
            // fail closed: until Missions can tell which units the mission script drives, no new order and no cancel at op end
            string notReady = Missions.ProtectionNotReady(); bool safe = notReady == null;
            if (!safe) { _protPaused = true; LogOnce("prot:" + notReady, $"unités du script pas encore sûres ({notReady}) : aucun nouvel ordre de l'IA ennemie, fin d'opération sans annuler les ordres"); }
            else if (_protPaused) { _protPaused = false; if (_resumeLogs++ < 10) Log("unités du script identifiées : l'IA ennemie donne de nouveau des ordres"); }
            bool tracked = false; Guard.Run("IA.Suivi", () => { TrackUnits(gc, botTeam, doc, now, safe); tracked = true; });
            if (!tracked) return;          // suivi raté : ne jamais donner d'ordre aux unités du tour précédent (peut-être mortes)
            List<Ops.Group> groups = null; Guard.Run("IA.Groupes", () => groups = Ops.HonestGroups(gc, playerTeam, now)); groups ??= new();
            Guard.Run("IA.Contact", () => { foreach (var g in groups) MarkFight(g.Pos, doc.React, now); });
            Guard.Run("IA.Ops", () => Ops.Update(gc, groups, doc, now, safe));
            if (safe)
            {
                Guard.Run("IA.Defense", () => Defend(doc, now));
                Guard.Run("IA.Contre", () => Ops.TryLaunch(gc, groups, playerTeam, botTeam, doc, now));     // Artillery.Support is only reached from here
            }
            if (now >= _nextDiag)
            {
                _nextDiag = now + 30f;
                string kept = _lastFiltered ? $"{_lastKept}/{_lastCand} visibles (ni cachées ni embarquées)" : $"{_lastCand}, sans filtre de visibilité";
                Log($"diag : {groups.Count} groupe(s) joueur détecté(s) sur {Ops.LastRaw} vu(s) par le jeu (perdus : aucun de mes véhicules à moins de 2 km {Ops.LostFar}, aucune de tes unités visible à moins de 150 m {Ops.LostUnseen}) ({Ops.SpotLabel}), posture {Current}, {_t.Count} suivie(s) ({kept}), {Ops.Count} op(s), {_moveLogs} mouvement(s) ; unités du script protégées {_protNow} (protections {_protStarts}, fins {_protEnds}, rayons rendus {_protRadius}, retirées d'une op {_protOps}){(safe ? "" : $" ; EN PAUSE : {notReady}")}");
            }
        }

        static void DropOrders()
        {
            int n = 0;
            foreach (var kv in _t.ToList()) { var t = kv.Value; if (t.Radius) { ReleaseRadius(kv.Key, t, "ordres IA coupés"); n++; } t.OpId = 0; t.Returning = false; }
            Ops.Reset();
            Log($"ordres IA coupés : {n} rayon(s) retiré(s), opérations oubliées");
        }

        // ------------------------------------------------------------ réseau : exactement un PC pilote les bots
        static void HostCheck(int playerTeam)
        {
            CanCommand = false;                                             // fail closed tant que la vérification n'est pas allée au bout
            bool net = false, sh = false, h = false, slave = false, flagsOk = true;
            try { net = NetworkScenarioStorage.IsNetwork; sh = NetworkScenarioStorage.IsScenarioHost; h = NetworkScenarioStorage.IsHost; slave = NetworkScenarioStorage.IsScenarioSlave; }
            catch (Exception e) { flagsOk = false; LogOnce("drapeaux", "drapeaux réseau illisibles (ordres coupés sauf OrdresIA=oui) : " + e.Message); }
            // solo / co-op from the network state only: in a campaign the AI players are not flagged IsBot, so a "human" count is wrong (it read 8 in solo)
            string netState = "?"; try { netState = NetStatus.Status.ToString(); } catch { }
            if (!flagsOk || net || slave || sh || netState == "Loading" || netState == "Deploy" || netState == "Game") _coop = true;   // unreadable or online: latched for the battle (fail closed)
            var mode = (Orders.Value ?? "auto").Trim().ToLowerInvariant();
            // 'oui' is a solo escape hatch only: in co-op a client (slave) or unreadable flags never drive the bots
            bool autoOk = flagsOk && !slave && (!_coop || sh || h);
            bool ok = mode == "non" ? false : mode == "oui" ? (!_coop || autoOk) : autoOk;          // 'oui' never lets a co-op client drive the bots
            if (mode == "oui" && _coop && !ok) LogOnce("oui-coop", "OrdresIA=oui ignoré : ce PC est client en coop");
            string status = $"réseau={net} état={netState} hôteScénario={sh} hôte={h} esclave={slave} drapeaux={(flagsOk ? "lus" : "illisibles")} coop={_coop} pref={mode} -> ordres IA {(ok ? "ACTIFS" : "désactivés")}";
            if (status != _hostStatus) { _hostStatus = status; Log(status); }
            CanCommand = ok;
        }

        static bool Known(int uid) => _bot.ContainsKey(uid);

        /// Own cache from GameSessionContext.TryGetPlayer (Campaign.IsBotByUid is never filled). Unknown owner = false = never touched.
        internal static bool IsBot(int uid)
        {
            // solo battle: every owner other than the local player is the AI (campaign AI players are not flagged IsBot)
            if (CanCommand && _localUid >= 0) return uid != _localUid;          // callers only pass owners of enemy-team units (also right for the co-op host)
            if (_bot.TryGetValue(uid, out var b)) return b;
            float now = UnityEngine.Time.time;
            if (_botRetry.TryGetValue(uid, out var next) && now < next) return false;
            try { var ctx = Campaign.Ctx(); if (ctx != null && ctx.TryGetPlayer(uid, out var p) && p != null) return _bot[uid] = p.IsBot; } catch { }
            _botRetry[uid] = now + 30f;
            return false;
        }

        internal static bool IsInf(int r) => r >= 30 && r <= 36;
        static bool GarrisonRole(int r) => r == 30 || r == 31 || r == 35 || r == 36;       // pas reco / snipers / MANPADS
        internal static bool IsArmour(int r) => r == 10 || r == 11 || r == 12;
        internal static bool IsAssault(int r) => IsArmour(r) || r == 13 || r == 30 || r == 31 || r == 36;
        internal static float D2(V3 a, V3 b) { a.y = 0; b.y = 0; return V3.Distance(a, b); }
        internal static IEnumerable<Track> Tracks => _t.Values;
        internal static bool TryTrack(int uid, out Track t) => _t.TryGetValue(uid, out t);
        internal static bool Parked(Track t, float now) => t.Script == null && t.OpId == 0 && !t.Returning && t.IdleSince >= 0 && now - t.IdleSince >= 30f && now >= t.Cooldown;
        internal static long Key(V3 v) => ((long)UnityEngine.Mathf.RoundToInt(v.x) << 32) ^ (uint)UnityEngine.Mathf.RoundToInt(v.z);
        internal static void MarkFight(V3 p, float react, float now) { foreach (var t in _t.Values) if (D2(t.Home, p) < react) t.LastFight = now; }

        // ------------------------------------------------------------ units added by the mod itself (Renforts.cs)
        /// A unit the mod added on the enemy side: it is driven by this file exactly like any other free enemy unit (the mission
        /// script never named it, so Missions.ScriptReason says null for it), with its guard position at the place it arrived and a
        /// quiet time during which no counter-attack takes it. Nothing else here knows it is ours.
        internal static void NoteRenfort(int uid, V3 home, float quiet)
        {
            if (uid <= 0 || _renfort.Count >= 200) return;
            _renfort[uid] = (home, quiet);
            if (_t.TryGetValue(uid, out var t)) { t.Home = home; if (quiet > t.Cooldown) t.Cooldown = quiet; }
        }
        internal static void ForgetRenfort(int uid) => _renfort.Remove(uid);
        internal static int RenfortCount => _renfort.Count;

        static void MoneyDiag(float now)
        {
            var ps = new LuaStorage(null, null).GetAllPlayers();
            for (int i = 0; i < (ps?.Length ?? 0); i++)
            {
                try { var p = ps[i]; if (p != null && p.IsBot) Log($"argent bot uid={p.UID} équipe={p.TeamID} : {p.GetMoney()} (t={now - _start:0}s)"); }
                catch { }
            }
        }

        // ------------------------------------------------------------ suivi des unités des bots
        /// CountUnits on an explicit UID set, with the field combination Visibility proved on the player's own units (the old neutral defaults until then); null if the bus / delegate is missing.
        internal static IlIntList CountUnits(GameController gc, IlIntSet set, bool onlySpotted, bool withCargo, bool withHidden)
            => Visibility.CountUnits(gc, set, onlySpotted, withCargo, withHidden);

        /// UIDs of the set that are neither hidden nor embarked; null = no filter (every candidate kept, the diag then says so).
        static HashSet<int> VisibleNotCargo(GameController gc, IlIntSet set, int n)
        {
            if (!_cargoFilterOk || n == 0) return null;
            try
            {
                var l = CountUnits(gc, set, false, false, false);
                var r = new HashSet<int>(); for (int i = 0; i < (l?.Count ?? 0); i++) r.Add(l[i]);
                if (r.Count > 0) return r;
                // liste vide : tout est vraiment caché / embarqué (réserves cachées du script) OU l'appel filtre tout -> contrôle sans exclusion
                var all = l == null ? null : CountUnits(gc, set, false, true, true);
                if (all != null && all.Count > 0) { LogOnce("tous-caches", $"les {n} unités des bots sont cachées ou embarquées ({all.Count} sans filtre) : aucun ordre pour l'instant"); return r; }
                // the CountUnits field sweep (Visibility) is still running: no filter for this tick, no final verdict yet
                if (l != null && !Visibility.CountUnitsProven && !Visibility.CountUnitsGaveUp) { LogOnce("cu-attente", $"filtre cachés/embarqués : CountUnits pas encore confirmé (0 sur {n}) : sans filtre pour l'instant"); return null; }
                _cargoFilterOk = false; Log($"filtre cachés/embarqués inutilisable (0 sur {n}{(l == null ? ", bus absent" : ", même sans filtre")}) : sans filtre de visibilité");
                return null;
            }
            catch (Exception e) { _cargoFilterOk = false; Log("filtre cachés/embarqués : " + e.Message); return null; }
        }

        static void TrackUnits(GameController gc, int botTeam, Doc doc, float now, bool safe)
        {
            var arr = Map.GetUnits(V3.zero, 1_000_000f, botTeam, -1);
            var cand = new Dictionary<int, LuaUnit>(); var set = new IlIntSet();
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                var u = arr[i]; if (u == null) continue;
                try
                {
                    if (!u.IsAlive()) continue;
                    int r = u.UnitRole; if (!(IsArmour(r) || r == 13 || IsInf(r)) || !IsBot(u.GetOwnerPlayerUID())) continue;
                    int id = u.UID; cand[id] = u; set.Add(id);
                }
                catch { }
            }
            var ok = VisibleNotCargo(gc, set, cand.Count); var seen = new HashSet<int>(); _loaded.Clear();
            int prot = 0;
            foreach (var kv in cand)
            {
                if (ok != null && !ok.Contains(kv.Key)) continue;
                try
                {
                    var u = kv.Value; var p = u.GetPosition();
                    if (!_t.TryGetValue(kv.Key, out var t))
                    {
                        bool ours = _ourRadius.Contains(kv.Key);
                        _t[kv.Key] = t = new Track { Home = p, LastPos = p, Role = u.UnitRole, Radius = ours, RadiusChecked = ours };
                        // a unit the mod added itself: it guards the place it arrived at, and it is left alone until its quiet time
                        if (_renfort.TryGetValue(kv.Key, out var rf)) { t.Home = rf.home; if (rf.quiet > t.Cooldown) t.Cooldown = rf.quiet; }
                    }
                    t.U = u; seen.Add(kv.Key);
                    if (ok == null && IsArmour(t.Role) && u.GetCargoUnitsCount() > 0) _loaded.Add(p);
                    bool still = u.IsIdle() && (p - t.LastPos).sqrMagnitude < 100f;
                    if (!still) t.IdleSince = -1; else if (t.IdleSince < 0) t.IdleSince = now;
                    // mission script units: never touched while the script drives them; back under our control with a fresh home afterwards
                    string why = Missions.ScriptReason(kv.Key, now);
                    if (why != null)
                    {
                        if (t.Script == null) Protect(kv.Key, t, why);
                        t.Script = why; t.LastPos = p; prot++;
                        continue;
                    }
                    if (t.Script != null)
                    {
                        _protEnds++;
                        if (_protLogs++ < 60) Log($"unité du script rendue à l'IA ennemie : {u.Name} (uid {kv.Key}), protection terminée ({t.Script}), nouvelle position de garde ici");
                        t.Script = null; t.Home = p; t.Garrisoned = false; t.RadiusChecked = t.Radius; t.IdleSince = -1; t.Cooldown = now + 60f;
                    }
                    t.LastPos = p; float d = D2(p, t.Home);
                    if (t.Returning && (d < 40f || (still && now - t.IdleSince > 10f))) t.Returning = false;
                    if (t.OpId != 0 || t.Returning) continue;
                    bool chase = t.Radius && now - t.LastFight < 60f && d < doc.React * .6f;
                    // while script units are not identifiable, never write a zero radius: it could overwrite the radius of an unseen script order
                    if (safe && t.Radius && !still && d > doc.React * .6f) RemoveRadius(kv.Key, t, "déplacement du script");
                    if (still && now - t.IdleSince > 20f && d > 150f && !chase && (safe || !t.Radius)) { if (t.Radius) RemoveRadius(kv.Key, t, "nouvelle position"); t.Home = p; t.Garrisoned = false; }
                }
                catch (Exception e) { LogOnce("suivi" + kv.Key, $"suivi uid {kv.Key} : {e.Message}"); }
            }
            foreach (var k in _t.Keys.Where(k => !seen.Contains(k)).ToList()) _t.Remove(k);
            _lastCand = cand.Count; _lastKept = seen.Count; _lastFiltered = ok != null; _protNow = prot;
        }

        /// A tracked unit was just taken by the mission script: it leaves its op (no cancel, no return order) and gets our radius back off.
        static void Protect(int uid, Track t, string why)
        {
            _protStarts++;
            string name = "?"; try { name = t.U.Name; } catch { }
            if (_protLogs++ < 60) Log($"unité du script protégée : {name} (uid {uid}) : {why}{(t.OpId != 0 ? $", retirée de l'op #{t.OpId} sans annuler ses ordres" : "")}{(t.Radius ? ", rayon de réaction de l'IA rendu" : "")}");
            if (t.Radius) { ReleaseRadius(uid, t, "unité prise par le script"); _protRadius++; }
            if (t.OpId != 0) { Ops.Release(uid, t.OpId); _protOps++; }
            t.OpId = 0; t.Returning = false;
        }

        /// Our radius off a unit. When the script's own order carries a radius, the script already replaced ours: we only forget it.
        static void ReleaseRadius(int uid, Track t, string why)
        {
            if (Missions.OrderRadius(uid) == 1)
            {
                t.Radius = false; t.RadiusChecked = false; _ourRadius.Remove(uid);
                MoveLog($"rayon de l'IA oublié : {t.U?.Name} (uid {uid}) {why}, l'ordre du script porte son propre rayon");
                return;
            }
            RemoveRadius(uid, t, why);
        }

        static void RemoveRadius(int uid, Track t, string why)
        {
            Missions.OwnOrderBegin();
            try { var z = new AggressiveRadiusData { Grounds = 0, Helicopters = 0, Planes = 0 }; UtilsClass.ApplyAggressiveRadius(ref z, t.U.Entity); MoveLog($"rayon retiré : {t.U.Name} (uid {uid}) {why}"); }
            catch (Exception e) { LogOnce("rayon" + uid, "retrait rayon : " + e.Message); }
            finally { Missions.OwnOrderEnd(); }
            t.Radius = false; t.RadiusChecked = false; _ourRadius.Remove(uid);
        }

        // ------------------------------------------------------------ posture de mission (verrouillée)
        static void UpdateStance(GameController gc, int playerTeam, int botTeam, float now)
        {
            var forced = ParseMission(PerMission.Value, Campaign.MissionUid); if (forced == Stance.Unknown) forced = ParseOne(Posture.Value);
            int mine = 0, theirs = 0, neutral = 0; var objs = Map.GetObjectives(false);
            for (int i = 0; i < (objs?.Length ?? 0); i++)
            {
                try
                {
                    var o = objs[i]; if (o == null || !o.IsVisible()) continue;
                    string raw = OwnerRaw(o);
                    int side = SideOf(gc, raw); var pos = o.Position; long key = Key(pos);
                    if (side == botTeam) theirs++; else if (side == playerTeam) mine++; else neutral++;
                    if (!_objStart.TryGetValue(key, out var startBot)) { _objStart[key] = side == botTeam; Log($"objectif {pos} propriétaire brut='{raw}' -> camp {side}"); }
                    else if (startBot && side == playerTeam) Ops.AddRetake(pos, now);
                    else if (side == botTeam) Ops.ClearRetake(pos);
                }
                catch (Exception e) { LogOnce("objectif" + i, $"objectif {i} : {e.Message}"); }
            }
            var s = forced;
            if (s == Stance.Unknown)
            {
                if (_locked) return;
                if (theirs + mine == 0 && now - _start < 240f) { if (Current == Stance.Unknown) Current = Stance.Meeting; return; }
                s = theirs > mine ? Stance.Defense : mine > theirs ? Stance.Attack : Stance.Meeting; _locked = true;
            }
            if (s == Current && _announced) return;
            Current = s; _announced = true;
            Log($"posture : {s} (ennemis {theirs}, joueur {mine}, neutres {neutral}) mission={Campaign.MissionUid} difficulté={Mod.Svc<CampaignService>()?.Difficulty} {(forced != Stance.Unknown ? "(forcée)" : "(verrouillée)")}");
            Mod.Notify(s == Stance.Defense ? TxtKey.N_AI_DEFENSE : s == Stance.Attack ? TxtKey.N_AI_ATTACK : TxtKey.N_AI_MEETING);
        }

        internal static int SideOf(GameController gc, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return -1; if (raw == "Alpha") return 0; if (raw == "Bravo") return 1;
            if (!int.TryParse(raw, out int id)) return -1; if (id == 0 || id == 1) return id;
            try { var teams = gc._GameSession_k__BackingField.GetTeams(); if (teams != null && teams.TryGetValue(id, out var td) && td != null) return (int)td.TeamSide; } catch { }
            return -1;
        }

        /// GetZoneOwnerTeam returns a boxed Lua value: numbers must be unboxed (ToString of the box gave garbage such as '-1071284736').
        internal static string OwnerRaw(LuaObjective o)
        {
            Il2CppSystem.Object v = null;
            try { v = o.GetZoneOwnerTeam(); } catch { return null; }
            if (v == null) return null;
            string type = "?"; try { type = v.GetIl2CppType().FullName; } catch { }
            string res;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                res = type switch
                {
                    "System.Int32" => v.Unbox<int>().ToString(inv),
                    "System.Int64" => v.Unbox<long>().ToString(inv),
                    "System.Int16" => v.Unbox<short>().ToString(inv),
                    "System.Byte" => v.Unbox<byte>().ToString(inv),
                    "System.Double" => Math.Round(v.Unbox<double>()).ToString(inv),
                    "System.Single" => Math.Round(v.Unbox<float>()).ToString(inv),
                    _ => v.ToString()
                };
            }
            catch { res = null; }
            LogOnce("owner-type:" + type, $"propriétaire d'objectif : type {type} -> '{res}'");
            return res;
        }

        static Stance ParseOne(string v) { v = (v ?? "").Trim().ToLowerInvariant(); return v.StartsWith("def") || v.StartsWith("déf") ? Stance.Defense : v.StartsWith("att") ? Stance.Attack : v.StartsWith("ren") ? Stance.Meeting : Stance.Unknown; }

        static Stance ParseMission(string table, string mission)
        {
            if (string.IsNullOrEmpty(table) || mission == null) return Stance.Unknown;
            foreach (var part in table.Split(';')) { var kv = part.Split('='); if (kv.Length == 2 && string.Equals(kv[0].Trim(), mission, StringComparison.OrdinalIgnoreCase)) return ParseOne(kv[1]); }
            return Stance.Unknown;
        }

        // ------------------------------------------------------------ défense : garnison, rayon de réaction, retour en position
        static void Defend(Doc doc, float now)
        {
            if (Current != Stance.Defense && Current != Stance.Meeting) return;
            int orders = 0, searches = 0;
            foreach (var kv in _t)
            {
                var t = kv.Value;
                if (orders >= 4) break; if (!Parked(t, now)) continue;
                try
                {
                    if (Garrison.Value && GarrisonRole(t.Role) && !t.Garrisoned && now >= t.NextGarrison)
                    {
                        if (searches >= 4) continue;                                   // building queries are capped too, not only orders
                        t.NextGarrison = now + 180f + (kv.Key & 31);                  // spread the retries of squads parked together
                        if (_loaded.Any(p => D2(p, t.LastPos) < 8f)) continue;
                        searches++;
                        var b = FreeBuilding(t.LastPos, 60f, now, out float d, out bool inside);
                        if (inside) { t.Garrisoned = true; continue; }
                        if (b == null) continue;
                        t.Garrisoned = true; orders++;
                        Missions.OwnOrderBegin(); try { t.U.Embark(b); } finally { Missions.OwnOrderEnd(); }
                        Log($"garnison : {t.U.Name} (uid {kv.Key}) -> bâtiment à {d:0} m (capacité {b.GetHouseCapacity()})");
                    }
                    else if (Reaction.Value && !_radiusBroken && IsArmour(t.Role) && !t.Radius && !t.RadiusChecked)
                    {
                        t.RadiusChecked = true; bool has;
                        try { has = t.U.Entity.Has<AggressiveRadiusComponent>(); }
                        catch (Exception e) { _radiusBroken = true; Log("lecture du rayon agressif impossible, fonction coupée : " + e.Message); continue; }
                        if (has) { MoveLog($"réaction : {t.U.Name} (uid {kv.Key}) a déjà un rayon du script, inchangé"); continue; }
                        int max = AggressiveRadiusSystem.MAX_AGRESSIVE_RADIUS;
                        int rad = (int)UnityEngine.Mathf.Clamp(doc.React * .35f, 150f, 350f); if (max > 0) rad = Math.Min(rad, max);
                        var data = new AggressiveRadiusData { Grounds = rad, Helicopters = 0, Planes = 0 };
                        orders++;
                        Missions.OwnOrderBegin(); try { UtilsClass.ApplyAggressiveRadius(ref data, t.U.Entity); } finally { Missions.OwnOrderEnd(); }
                        t.Radius = true; _ourRadius.Add(kv.Key);
                        MoveLog($"réaction : {t.U.Name} (uid {kv.Key}) rayon {rad} m (max jeu {max}) HasAprès={SafeHas(t)}");
                    }
                    else if (t.Radius && now - t.LastFight < 60f)
                    {
                        float d = D2(t.LastPos, t.Home); if (d <= 150f || d >= doc.React * .6f) continue;
                        t.Cooldown = now + 60f; orders++;                    // avant l'ordre : un MoveTo qui lève n'est pas retenté toutes les 4 s
                        Missions.OwnOrderBegin(); try { t.U.MoveTo(t.Home, 30, false); } finally { Missions.OwnOrderEnd(); }
                        t.Returning = true; t.IdleSince = -1;
                        MoveLog($"retour en position : {t.U.Name} (uid {kv.Key}) à {d:0} m");
                    }
                }
                catch (Exception e) { LogOnce("defense" + kv.Key, $"défense uid {kv.Key} : {e.Message}"); }
            }
        }

        static string SafeHas(Track t) { try { return t.U.Entity.Has<AggressiveRadiusComponent>().ToString(); } catch { return "?"; } }

        static LuaBuilding FreeBuilding(V3 p, float radius, float now, out float best, out bool inside)
        {
            best = float.MaxValue; inside = false; LuaBuilding res = null; var arr = Map.GetBuildingsInRange(p, radius);
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                var b = arr[i]; if (b == null || b.IsDestroyed()) continue; var sp = b.GetShootPosition(); float d = D2(p, sp);
                if (d < 12f && b.HasUnitsInside()) { inside = true; return null; }
                if (b.GetHouseCapacity() <= 0 || b.HasUnitsInside() || (_reserved.TryGetValue(Key(sp), out var until) && now < until)) continue;
                if (d < best) { best = d; res = b; }
            }
            if (res != null) _reserved[Key(res.GetShootPosition())] = now + 120f;   // only while the squad walks there: an emptied building becomes usable again
            return res;
        }
    }

    /// F2 : contre-attaques locales sur les groupes du joueur que l'IA a détectés.
    static class Ops
    {
        internal sealed class Group { public V3 Pos; public int Cost; }
        sealed class Op { public int Id, StartCount, Released; public V3 Target; public readonly List<int> Members = new(); public float Start, LastSeen; public bool Retake; }
        sealed class Retake { public V3 Pos; public int Tries; public float Next; }
        static readonly List<Op> _ops = new();
        static readonly List<(V3 pos, float until)> _cd = new();
        static readonly List<Retake> _rt = new();
        static int _seq;
        static bool _dictOk = true;
        static float _nextCheck;
        static string _spotSource;                                      // visibility source used by the last spotted check (null = none usable)
        internal static int Count => _ops.Count;
        /// Honest label for the diag: which visibility source filtered the player groups, or none.
        internal static int LastRaw, LostFar, LostUnseen;   // diag of the group filter chain (see HonestGroups)
        internal static string SpotLabel => _spotSource != null ? $"repérés : source {_spotSource}" : "sans filtre de visibilité, proximité seulement";
        static void Log(string s) => Mod.Log.Msg("[IA-ENNEMIE] " + s);

        internal static void Reset() { _ops.Clear(); _cd.Clear(); _rt.Clear(); _refLog.Clear(); _dictOk = true; _nextCheck = 0; _spotSource = null; }

        static readonly Dictionary<long, float> _refLog = new();
        /// At most one refusal line per 300 m cell per 5 minutes (a standing contact is re-evaluated every OpCd).
        static bool SayRefusal(V3 target, float now)
        {
            long cell = ((long)UnityEngine.Mathf.FloorToInt(target.x / 300f) << 32) ^ (uint)UnityEngine.Mathf.FloorToInt(target.z / 300f);
            if (_refLog.TryGetValue(cell, out var nx) && now < nx) return false;
            _refLog[cell] = now + 300f;
            return true;
        }
        /// A member taken by the mission script leaves the op: no cancel, no return order, not counted as a loss.
        internal static void Release(int uid, int opId)
        {
            foreach (var op in _ops)
            {
                if (op.Id != opId || !op.Members.Remove(uid)) continue;
                op.StartCount = Math.Max(1, op.StartCount - 1); op.Released++;
                Log($"op #{op.Id} : uid {uid} pris par le script, retiré de l'opération sans annuler ses ordres");
                return;
            }
        }

        internal static void AddRetake(V3 p, float now) { if (_rt.Any(r => EnemyAi.D2(r.Pos, p) < 50f)) return; _rt.Add(new Retake { Pos = p, Next = now + 90f }); Log($"objectif perdu en {p} : reprise envisagée"); }
        internal static void ClearRetake(V3 p) => _rt.RemoveAll(r => EnemyAi.D2(r.Pos, p) < 50f);

        internal static List<Group> HonestGroups(GameController gc, int playerTeam, float now)
        {
            var raw = new List<Group>();
            if (_dictOk)
            {
                try
                {
                    var dict = AiTargetDetectionSystem._groups;
                    if (dict != null && dict.TryGetValue((TeamSide)playerTeam, out var list) && list != null)
                        for (int i = 0; i < list.Count; i++) { var g = list[i]; if (g != null) raw.Add(new Group { Pos = g.Center, Cost = g.GroupCost }); }
                }
                catch (Exception e) { _dictOk = false; raw.Clear(); Log("groupes (statique) illisibles, repli LuaAI : " + e.Message); }
            }
            if (now >= _nextCheck)
            {
                _nextCheck = now + 30f; int lua = EnemyAi.Ai.GetDetectedGroupPositions(playerTeam, 0)?.Length ?? 0;
                if (_dictOk && lua != raw.Count) { Log($"incohérence groupes statique={raw.Count} LuaAI={lua} : repli LuaAI"); _dictOk = false; }
            }
            if (!_dictOk) { raw.Clear(); var arr = EnemyAi.Ai.GetDetectedGroupPositions(playerTeam, 0); for (int i = 0; i < (arr?.Length ?? 0); i++) raw.Add(new Group { Pos = arr[i], Cost = -1 }); }
            // 2026-09-19: the player killed dozens of enemies while this returned 0 groups every single time. The line only
            // reported the final count, so nothing said whether the GAME never detected him or whether these two filters threw
            // everything away. Count each step, so the next log names the culprit instead of leaving us to guess.
            LastRaw = raw.Count; LostFar = LostUnseen = 0;
            var res = new List<Group>();
            foreach (var g in raw)
            {
                if (!EnemyAi.Tracks.Any(t => EnemyAi.D2(t.LastPos, g.Pos) < 2000f)) { LostFar++; continue; }
                float sc = SpottedCost(gc, playerTeam, g.Pos, 150f, out int n);
                if (sc >= 0) { if (n == 0) { LostUnseen++; continue; } }
                else if (!PlayerNear(playerTeam, g.Pos, 300f)) { LostUnseen++; continue; }     // sans filtre de visibilité : au moins une unité du joueur réellement là (protège d'une lecture inversée de _groups)
                res.Add(g);
            }
            return res;
        }

        static bool PlayerNear(int playerTeam, V3 c, float r)
        {
            try { var a = EnemyAi.Map.GetUnits(c, r, playerTeam, -1); for (int i = 0; i < (a?.Length ?? 0); i++) { try { if (a[i] != null && a[i].IsAlive()) return true; } catch { } } }
            catch { }
            return false;
        }

        /// Cost of the player units near c that the bots' team has really spotted (shared Visibility helper, bots' point of view);
        /// -1 when no visibility source is validated (callers then fall back to proximity, and the diag says "sans filtre de visibilité").
        static float SpottedCost(GameController gc, int playerTeam, V3 c, float r, out int n)
        {
            n = 0;
            VisResult vis;
            try { vis = Visibility.VisibleEnemies(gc, 1 - playerTeam, playerTeam); }
            catch { _spotSource = null; return -1; }
            _spotSource = vis.Usable ? vis.Source : null;
            if (!vis.Usable) return -1;
            float cost = 0;
            try
            {
                var near = EnemyAi.Map.GetUnits(c, r, playerTeam, -1);
                for (int i = 0; i < (near?.Length ?? 0); i++)
                {
                    try
                    {
                        var u = near[i];
                        if (u == null || !u.IsAlive() || !vis.Uids.Contains(u.UID)) continue;
                        n++;
                        try { cost += u.Cost; } catch { }
                    }
                    catch { }
                }
            }
            catch { return -1; }
            return cost;
        }

        internal static void TryLaunch(GameController gc, List<Group> groups, int playerTeam, int botTeam, EnemyAi.Doc doc, float now)
        {
            if (!EnemyAi.Counter.Value) return;
            int maxOps = EnemyAi.Current == EnemyAi.Stance.Attack ? 1 : doc.MaxOps;
            _cd.RemoveAll(s => s.until < now);
            var targets = groups.Select(g => (pos: g.Pos, cost: g.Cost, rt: (Retake)null)).ToList();
            foreach (var r in _rt) if (r.Tries < 2 && now >= r.Next) targets.Add((r.Pos, -1, r));
            foreach (var (target, gcost, rt) in targets)
            {
                if (_ops.Count >= maxOps) break;
                if (_cd.Any(s => EnemyAi.D2(s.pos, target) < 300f)) continue;
                if (_ops.Any(o => EnemyAi.D2(o.Target, target) < 300f)) continue;     // déjà une op sur ce contact (OpCd < durée max d'une op) : pas de renfort en chaîne
                if (rt == null) EnemyAi.MarkFight(target, doc.React, now);
                var cands = EnemyAi.Tracks.Where(t => EnemyAi.Parked(t, now) && EnemyAi.IsAssault(t.Role) && EnemyAi.D2(t.Home, target) <= doc.React)
                    .Where(t => { try { return t.U.GetHealPercentage() >= 60 && t.U.GetAmmoPercentage(true, false) >= 40 && t.U.GetCargoUnitsCount() == 0; } catch { return false; } })
                    .OrderBy(t => EnemyAi.D2(t.LastPos, target)).ToList();
                if (EnemyAi.Current != EnemyAi.Stance.Attack) { int maxG = cands.Count(t => t.Garrisoned) / 2, gi = 0; cands = cands.Where(t => !t.Garrisoned || gi++ < maxG).ToList(); }
                if (cands.Count < 3) continue;
                int take = Math.Min(Math.Min(rt != null ? 2 : doc.MaxOp, cands.Count - 1), Math.Max(2, (int)Math.Round(cands.Count * doc.Frac * EnemyAi.Aggr.Value)));
                var picked = cands.Take(take).ToList();
                float mine = 0; foreach (var t in picked) { try { mine += t.U.Cost; } catch { } }
                float theirs = gcost > 0 ? gcost : SpottedCost(gc, playerTeam, target, rt != null ? 250f : 150f, out _);
                _cd.Add((target, now + doc.OpCd)); if (rt != null) { rt.Tries++; rt.Next = now + 300f; }
                if (rt == null && theirs <= 0) { if (SayRefusal(target, now)) Log($"groupe {target} de valeur inconnue : pas d'assaut"); continue; }
                if (theirs > 0 && mine < theirs * doc.Sup)
                {
                    if (SayRefusal(target, now)) Log($"contre-attaque refusée vers {target} : rapport {mine:0}/{theirs:0} < {doc.Sup}");
                    if (rt == null) Artillery.Support(gc, botTeam, target, doc, now, "tir d'arrêt", 1);
                    continue;
                }
                Launch(gc, picked, target, botTeam, doc, now, rt != null, mine, theirs);
            }
        }

        static void Launch(GameController gc, List<EnemyAi.Track> picked, V3 target, int botTeam, EnemyAi.Doc doc, float now, bool retake, float mine, float theirs)
        {
            var op = new Op { Id = ++_seq, Target = target, Start = now, LastSeen = now, Retake = retake };
            var rally = V3.zero; foreach (var t in picked) rally += t.LastPos; rally /= picked.Count;
            var axis = target - rally; axis.y = 0; float dist = Math.Max(1f, axis.magnitude); var dir = axis / dist; var perp = new V3(-dir.z, 0, dir.x);
            float off = UnityEngine.Mathf.Clamp(dist * .4f, 150f, 350f); var bp = target - dir * (dist * .35f); var left = bp + perp * off; var right = bp - perp * off;
            bool useLeft = Cover(left) >= Cover(right); var flank = useLeft ? left : right; int bof = picked.Count >= 4 ? picked.Count / 3 : 0;
            for (int i = 0; i < picked.Count; i++)
            {
                var t = picked[i];
                if (t.Script != null) continue;                                      // taken by the mission script since the pick: never ordered
                Missions.OwnOrderBegin();
                try
                {
                    if (retake || i < bof) t.U.MoveTo(retake ? target : target - dir * Math.Min(dist * .5f, 300f), 40, true);
                    else { if (!EnemyAi.IsInf(t.Role)) t.U.MoveToFast(flank, 50, false); else t.U.MoveTo(flank, 50, false); t.U.MoveTo(target, 60, true); }
                    t.OpId = op.Id; t.Garrisoned = false; op.Members.Add(t.U.UID);
                }
                catch (Exception e) { Log($"ordre refusé uid {t.U?.UID} : {e.Message}"); }
                finally { Missions.OwnOrderEnd(); }
            }
            if (op.Members.Count < 2) { foreach (var id in op.Members) if (EnemyAi.TryTrack(id, out var t)) { t.OpId = 0; t.Cooldown = now + 120f; } return; }
            op.StartCount = op.Members.Count; _ops.Add(op); EnemyAi.MarkFight(target, doc.React, now);
            string names = string.Join(", ", picked.Select(t => { try { return t.U.Name; } catch { return "?"; } }));
            Log($"{(retake ? "reprise" : "contre-attaque")} #{op.Id} : {op.StartCount} unités ({names}) vers {target}, flanc {(useLeft ? "gauche" : "droit")}, appui {bof}, rapport {mine:0}/{theirs:0}, posture {EnemyAi.Current}");
            if (!retake) Artillery.Support(gc, botTeam, target, doc, now, $"préparation op #{op.Id}", doc.Batteries);
        }

        static int Cover(V3 p) { try { return EnemyAi.Map.GetBuildingsInRange(p, 150f)?.Length ?? 0; } catch { return 0; } }

        /// safe = false: Missions cannot identify script units yet, so an ending op neither cancels nor sends its units home.
        internal static void Update(GameController gc, List<Group> groups, EnemyAi.Doc doc, float now, bool safe)
        {
            if (_ops.Count == 0) return;
            var cmd = gc._GetEcsEventBus_k__BackingField?.Commands;
            for (int k = _ops.Count - 1; k >= 0; k--)
            {
                var op = _ops[k];
                // double safety: a member protected by the mission script is released here too (normally done by EnemyAi.Protect)
                foreach (var id in op.Members.Where(id => EnemyAi.TryTrack(id, out var pt) && pt.Script != null).ToList())
                {
                    op.Members.Remove(id); op.StartCount = Math.Max(1, op.StartCount - 1); op.Released++;
                    if (EnemyAi.TryTrack(id, out var rt)) { rt.OpId = 0; rt.Returning = false; }
                    Log($"op #{op.Id} : uid {id} pris par le script, retiré de l'opération sans annuler ses ordres");
                }
                var alive = op.Members.Where(id => EnemyAi.TryTrack(id, out _)).ToList();
                var near = groups.Where(g => EnemyAi.D2(g.Pos, op.Target) < 300f).OrderBy(g => EnemyAi.D2(g.Pos, op.Target)).FirstOrDefault();
                if (near != null) { op.LastSeen = now; op.Target = near.Pos; }
                float losses = 1f - alive.Count / (float)op.StartCount;
                bool leash = alive.Any(id => EnemyAi.TryTrack(id, out var t) && EnemyAi.D2(t.LastPos, t.Home) > doc.React * 1.4f);
                bool allIdle = alive.Count > 0 && alive.All(id => EnemyAi.TryTrack(id, out var t) && t.IdleSince >= 0);
                string end = (alive.Count == 0 && op.Released > 0 && op.Members.Count == 0) ? "unités prises par le script"
                    : losses >= doc.Withdraw ? "pertes" : (!op.Retake && now - op.LastSeen > 45f) ? "cible perdue" : now - op.Start > 180f ? "délai" : leash ? "trop loin" : (allIdle && now - op.Start > 40f) ? "terminée" : null;
                if (end == null) continue;
                bool hold = op.Retake && end == "terminée";
                if (alive.Count > 0 && !hold && safe)
                {
                    Missions.OwnOrderBegin();
                    try { cmd?.CancelUnitCommands?.Invoke(Cheats.ToIl2Cpp(alive), true); } catch (Exception e) { Log("annulation : " + e.Message); }
                    finally { Missions.OwnOrderEnd(); }
                }
                foreach (var id in alive)
                {
                    if (!EnemyAi.TryTrack(id, out var t)) continue;
                    if (!safe) { if (hold) t.Home = t.LastPos; t.OpId = 0; t.Cooldown = now + 120f; t.IdleSince = -1; continue; }   // script units unknown: never cancel or send home
                    Missions.OwnOrderBegin();
                    try
                    {
                        if (hold) t.Home = t.LastPos;
                        else { if (end == "pertes" && !EnemyAi.IsInf(t.Role)) t.U.MoveToFast(t.Home, 30, false); else t.U.MoveTo(t.Home, 30, false); t.Returning = true; }
                    }
                    catch (Exception e) { Log($"retour uid {id} : {e.Message}"); }
                    finally { Missions.OwnOrderEnd(); t.OpId = 0; t.Cooldown = now + 120f; t.IdleSince = -1; }
                }
                _ops.RemoveAt(k); Log($"op #{op.Id} fin ({end}) : {alive.Count}/{op.StartCount} survivants{(!safe && alive.Count > 0 ? ", laissés sur place sans annuler leurs ordres (unités du script pas encore sûres)" : "")}");
            }
        }
    }

    /// F3 : salve courte de préparation / tir d'arrêt par l'artillerie conventionnelle des bots (hôte uniquement) + réglage du preset d'artillerie.
    static class Artillery
    {
        static readonly Dictionary<int, float> _cd = new();
        static float _next, _nextNote;
        // preset : AiBoost repasse à chaque mission / redémarrage SANS restauration du journal -> repartir de la valeur d'origine, jamais de la valeur déjà modifiée
        static IntPtr _presetPtr; static int _presetOrig = -1, _presetWritten = -1;      // volontairement PAS remis à zéro par Reset (vit jusqu'à la restauration du journal)
        internal static void Reset() { _cd.Clear(); _next = _nextNote = 0; _scanAt = -1; _arty.Clear(); }
        // bot artillery scanned once per tick (several refused targets in one tick must not each read the whole map)
        static float _scanAt = -1;
        static readonly List<(LuaUnit u, V3 p, float range, float min)> _arty = new();
        static void Note(string s, float now) { if (now < _nextNote) return; _nextNote = now + 30f; Mod.Log.Msg("[IA-ENNEMIE] " + s); }

        /// v0.18 F3 : léger réglage du délai du preset d'artillerie de l'IA selon la difficulté (journalisé, restauré en fin de campagne). Appelé par le bloc unique d'Assistants.AiBoost.
        internal static int TunePreset(AiConfig ai)
        {
            if (ai == null) return 0;
            int n = 0;
            try
            {
                var diff = Mod.Svc<CampaignService>()?.Difficulty ?? DifficultyLevel.Medium;
                var p0 = ai.TargetingDefaultPreset; int cur = p0.CooldownTime;
                int o = ai.Pointer == _presetPtr && _presetOrig >= 0 && cur == _presetWritten ? _presetOrig : cur;
                Mod.Log.Msg($"[ASSIST] IA preset : délai {cur} (origine {o}) coûtMin {p0.MinGroupCost} durée {p0.PrefferedDuration} listeNoire {p0.TargetsBlacklist?.Length ?? 0} ; groupes {ai.GroupCreationCost}/{ai.GroupDisbandCost}/{ai.GroupingMaxDistance} ; cbVie {ai.CBTargetsLifeTime} ; intervalle {AiTargetDetectionSystem.GROUPS_UPDATE_INTERVAL} ; ordreMax {AiArtillerySystem.MAX_ORDER_EXECUTION_TIME} ; rayonMax {AggressiveRadiusSystem.MAX_AGRESSIVE_RADIUS} ; difficulté {diff} (surcharges par rôle possibles)");
                int nv = diff == DifficultyLevel.Easy ? (int)(o * 1.25f) : diff == DifficultyLevel.Hard ? Math.Max(Math.Min(o, 30), (int)(o * .75f)) : o;
                nv = Math.Min(nv, ushort.MaxValue);
                if (nv != cur)
                {
                    var p1 = ai.TargetingDefaultPreset; p1.CooldownTime = (ushort)nv;
                    n += Realism.SetJournaled(ai, "TargetingDefaultPreset", p1);
                    int back = ai.TargetingDefaultPreset.CooldownTime;
                    _presetPtr = ai.Pointer; _presetOrig = o; _presetWritten = back;
                    Mod.Log.Msg($"[ASSIST] IA preset délai {cur} -> {back} (origine {o})");
                }
                if (ai.CBTargetsLifeTime < 30) n += Realism.SetJournaled(ai, "CBTargetsLifeTime", (ushort)30);
            }
            catch (Exception e) { Mod.Log.Error("[ASSIST] preset IA : " + e.Message); }
            return n;
        }

        internal static void Support(GameController gc, int botTeam, V3 target, EnemyAi.Doc doc, float now, string why, int max)
        {
            if (!EnemyAi.Arty.Value || !EnemyAi.CanCommand || doc.Batteries <= 0 || max <= 0 || now < _next) return;
            var cmd = gc._GetEcsEventBus_k__BackingField?.Commands; if (cmd == null) return;
            var own = EnemyAi.Map.GetUnits(target, 200f, botTeam, -1);
            for (int i = 0; i < (own?.Length ?? 0); i++)
            {
                bool friend = false; try { friend = own[i] != null && own[i].IsAlive(); } catch { }
                if (friend) { Note($"{why} annulé : unités amies à moins de 200 m", now); return; }
            }
            if (now != _scanAt)
            {
                _scanAt = now; _arty.Clear();
                var all = EnemyAi.Map.GetUnits(V3.zero, 1_000_000f, botTeam, -1);
                for (int i = 0; i < (all?.Length ?? 0); i++)
                {
                    var u = all[i]; if (u == null) continue;
                    try
                    {
                        if (!u.IsAlive()) continue;
                        int r = u.UnitRole; if (r != 130 && r != 131 && r != 133) continue;          // MLRS / mortier / canon, jamais LAM (132)
                        if (!EnemyAi.IsBot(u.GetOwnerPlayerUID()) || !u.IsIdle() || u.GetAmmoPercentage(true, false) < 30) continue;
                        if (Missions.ScriptReason(u.UID, now) != null) continue;                     // a piece driven by the mission script never fires for us
                        float range = Assistants.RangeOf(u, out float min, out bool lr);
                        if (lr || Assistants.CarriesMissiles(u)) continue;                           // as before: never a launcher that can carry missiles (Iskander, HIMARS / M270 ATACMS)
                        _arty.Add((u, u.GetPosition(), range, min));
                    }
                    catch { }
                }
            }
            var pieces = new List<(LuaUnit u, float d)>();
            foreach (var a in _arty)
            {
                try
                {
                    if (_cd.TryGetValue(a.u.UID, out var c) && now < c) continue;
                    float d = EnemyAi.D2(a.p, target); if (d > a.range || d < Math.Max(80f, a.min)) continue;
                    pieces.Add((a.u, d));
                }
                catch { }
            }
            int fired = 0, cap = Math.Min(max, doc.Batteries);
            foreach (var p in pieces.OrderBy(p => p.d))
            {
                if (fired >= cap) break;
                int uid = 0;
                try
                {
                    uid = p.u.UID; _cd[uid] = now + doc.ArtyCd; fired++;               // cooldown posé avant l'ordre : jamais re-tenté en boucle
                    Missions.OwnOrderBegin(); try { Assistants.Fire(cmd, p.u, target, 40f); } finally { Missions.OwnOrderEnd(); }
                    Mod.Log.Msg($"[IA-ENNEMIE] {why} : {p.u.Name} (uid {uid}) salve courte sur {target} à {p.d:0} m");
                }
                catch (Exception e) { Mod.Log.Msg($"[IA-ENNEMIE] {why} : tir refusé uid {uid} : {e.Message}"); }
            }
            if (fired > 0) _next = now + 20f; else Note($"{why} : aucune pièce disponible ({pieces.Count} candidate(s))", now);
        }
    }
}
