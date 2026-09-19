// RealismOverhaul - Demolition (v0.18)
//  Campaign only: a heavy warhead (ballistic / cruise missile, FAB-500 and heavier bombs) that lands on or right
//  next to a building collapses it with the vanilla BuildingService.DestroyBuilding call (same call as the mission
//  editor's "Destroy Building" node): hit segment, up to 4 neighbours, small radius for the largest warheads.
//  Detection = polling BuildingSegmentComponent.LastDamage every 0.5 s (no Harmony patch).
//  Nothing is written to the DB / configs / statics: the only effect is the current battle's map.
//  Inert in online co-op (more than 1 human) unless DemolitionEnLigne=true; unknown player count = inert.
//  v0.23.0: never collapses a segment inside a place the mission may need (Missions.ProtectedAt: objective zones including hidden ones,
//  building-entry destinations of script orders, tags named by building script nodes, buildings holding script infantry). Checked when
//  the job is planned and again when it runs; while that protection is not built yet, nothing is destroyed. The game's own building
//  damage is untouched. Log: [BATIMENT] ... protégé.
// v1.3: a segment that HOLDS A SQUAD and has a live supply crate within the game's supply radius is spared as well (the crate is what
//  keeps the strongpoint standing: sandbags, props and timber off the truck). This is the honest half of the repair the author asked
//  for: the mod cannot raise a building's real health without Entity.Get<HealthComponent>(), which the project forbids, so it does not
//  pretend to - it only stops its OWN extra demolition. The game's own collapse at zero health still happens, which is why this can
//  never keep a mission from finishing: skipping an extra destruction only leaves the map closer to vanilla. The crate must belong to
//  the SIDE that holds the building, so the player's own truck never saves the block the enemy is holding. Checked once, at the last
//  moment before the irreversible call and only for a segment that really exists, so it costs two or three Lua searches per real
//  destruction and nothing per frame.
using System;
using System.Collections.Generic;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using BSvc = Il2CppBrokenArrow.Client.Ecs.Building.BuildingService;
using BSeg = Il2CppBrokenArrow.Client.Ecs.Building.BuildingSegmentComponent;
using Shooter = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.ShooterInfo;
using HitSys = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellHitSystem;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using EcsEntity = Il2CppDefaultEcs.Entity;
using UGameObject = UnityEngine.GameObject;

namespace RealismOverhaul
{
    static class Demolition
    {
        static MelonPreferences_Entry<bool> _on, _online, _supplyHold; static MelonPreferences_Entry<float> _heavy, _neigh;
        sealed class Seg { public int Id; public BSeg C; public UGameObject Obj; public float Last, X, Z; public bool Dead; }
        static readonly List<Seg> _segs = new(); static readonly Dictionary<int, Seg> _byId = new();
        static readonly HashSet<int> _done = new(); static readonly Queue<int> _jobs = new();
        static BSvc _bs; static GameSessionContext _ctx; static bool? _solo; static bool _active, _viaSession, _sessionRouteSeen, _notFoundLogged, _everOnline;
        static int _wait;                                          // frames this module has waited for the heavy-job slot (Planif.cs)
        static float _next, _nextScan, _nextCoop, _nextCalib, _nextSetup;
        static int _calibSkipped, _calibLines, _destroyed, _noRuin, _notFound, _skippedRuin, _heavyLogs, _heavyHidden;
        static int _protected, _protectedUnknown, _protectedLate, _protLogs;                  // segments spared by the mission protection (planned, protection not ready, at run time)
        const int MaxProtLogs = 20;
        static LuaMap _map; static bool _supplyBroken; static int _supplied, _supplyLogs;      // segments spared because a squad holds them with supply in range
        const int MaxSupplyLogs = 10;
        const float SegSearch = 6f;                  // metres around the segment's shoot position: enough to find that very segment
        const float HoldSearch = 25f;                // metres around that same position: the squad holding the segment, whatever side it is
        const float DefaultSupplyRadius = 150f;      // used only when SupplyConfig cannot be read
        static string _coopErr;
        static BSvc _failSvc; static GameSessionContext _failCtx; static int _failCount;   // held wrappers (no address reuse): Setup give-up
        const int MaxPerStep = 2;            // DestroyBuilding calls per drain step (Instantiate + Physics.SyncTransforms each)
        const float ScanStep = 0.5f, DrainStep = 0.17f;   // at most 3 steps x 2 = 6 destroys per 0.5 s, spread over frames
        const float MinHeavy = 25f;          // floor for SeuilLourd: a config typo must never let tank shells raze buildings
        const int MaxSetupTries = 3, MaxHeavyLogs = 4;
        static void Log(string s) => Mod.Log.Msg("[BATIMENT] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Batiments");
            _on = c.CreateEntry("BatimentsChargesLourdes", true, description: Build.Desc("Missiles balistiques/croisière et bombes lourdes (FAB-500 et plus) rasent le bâtiment touché (campagne, partie solo)"));
            _heavy = c.CreateEntry("SeuilLourd", 50f, description: Build.Desc("Dégâts bâtiment d'un impact à partir desquels le bâtiment est rasé (minimum 25, voir lignes [BATIMENT] du log)"));
            _neigh = c.CreateEntry("SeuilVoisins", 80f, description: Build.Desc("À partir de ces dégâts, les segments voisins (4 max) sont aussi rasés"));
            _online = c.CreateEntry("DemolitionEnLigne", false, description: Build.Desc("true = aussi en coop en ligne (RISQUE de désynchronisation)"));
            _supplyHold = c.CreateEntry("RavitaillementTientLeBatiment", true, description: Build.Desc(
                "Un bâtiment occupé par une escouade et situé dans un cercle de ravitaillement DE SON CAMP n'est jamais rasé par le mod (le jeu peut toujours l'effondrer normalement)",
                "Une caisse de ravitaillement du même camp empêche le mod de raser le bâtiment tenu par ces hommes."));
        }

        /// Per-battle reset (Campaign.ResetSession: mission start, restart, campaign end).
        internal static void ResetSession()
        {
            Clear();
            _nextSetup = 0; _nextCalib = 0; _calibSkipped = 0; _calibLines = 0;
            _protected = _protectedUnknown = _protectedLate = _protLogs = 0;
            _map = null; _supplyBroken = false; _supplied = 0; _supplyLogs = 0;
            _everOnline = false;   // co-op latch: kept across Clear() (ctx can be null for a moment around a reconnect)
            _failSvc = null; _failCtx = null; _failCount = 0;
        }

        /// Drops the held service/context and all per-battle state (also used on any service/context change).
        static void Clear()
        {
            _segs.Clear(); _byId.Clear(); _done.Clear(); _jobs.Clear();
            _bs = null; _ctx = null; _solo = null; _active = false; _viaSession = false; _notFoundLogged = false;
            _nextCoop = 0; _nextScan = 0; _coopErr = null;
            _destroyed = _noRuin = _notFound = _skippedRuin = 0;
        }

        static float Heavy() => Math.Max(_heavy.Value, MinHeavy);

        /// Called every frame while in campaign. Kept allocation-free: the work (and its closures) lives in Poll.
        internal static void Frame()
        {
            if (_on == null || _online == null || _heavy == null || _neigh == null) return;
            if (!_on.Value)
            {
                // switched off: nothing pending may survive, and switching back on takes a fresh baseline
                if (_active || _jobs.Count > 0) { _active = false; _jobs.Clear(); Log("désactivé (BatimentsChargesLourdes=false) : file vidée"); }
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _active ? Planif.WaitNormal : Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            _next = now + ScanStep;
            Poll(now);
        }

        static void Poll(float now)
        {
            // fail closed: live play session with a current player, and the SAME service/context as the ones we hold
            var ctx = Campaign.Ctx();
            if (ctx == null || ctx.CurrentPlayer == null) { if (_bs != null || _ctx != null) Clear(); return; }
            var cur = Resolve(out bool viaSession);
            if (cur == null) { if (_bs != null || _ctx != null) Clear(); return; }
            if (_bs == null || _bs.Pointer != cur.Pointer || _ctx == null || _ctx.Pointer != ctx.Pointer)
            {
                Clear();
                if (now < _nextSetup || !cur.IsInitialized) return;
                bool samePair = _failSvc != null && _failSvc.Pointer == cur.Pointer && _failCtx != null && _failCtx.Pointer == ctx.Pointer;
                if (samePair && _failCount >= MaxSetupTries) return;   // given up for this battle
                _bs = cur; _ctx = ctx; _viaSession = viaSession;
                int n = 0;
                Guard.Run("Demolition.Setup", () => n = Setup());
                if (n <= 0)
                {
                    _bs = null; _segs.Clear(); _byId.Clear(); _nextSetup = now + 5f;
                    if (!samePair) { _failSvc = cur; _failCtx = ctx; _failCount = 0; }
                    _failCount++;
                    if (_failCount == 1) Log("aucun segment de bâtiment utilisable, nouvel essai dans 5 s");
                    else if (_failCount == MaxSetupTries) Log($"préparation impossible après {MaxSetupTries} essais : démolition inactive pour cette partie");
                    return;
                }
                _failSvc = null; _failCtx = null; _failCount = 0;
            }

            if (now >= _nextCoop) { _nextCoop = now + 10f; Guard.Run("Demolition.Coop", () => CheckCoop(ctx)); }
            if (_solo != true && !(_solo == false && _online.Value))
            {
                // unknown or online -> inert; planned destructions are dropped (their ids stay done)
                _active = false;
                if (_jobs.Count > 0) { Log($"démolition suspendue : {_jobs.Count} destruction(s) en attente annulée(s)"); _jobs.Clear(); }
                return;
            }
            if (!_active)
            {
                // (re)activation: take the current LastDamage values as baseline, so hits from an inert period are never replayed
                _active = true; _nextScan = now + ScanStep;
                Guard.Run("Demolition.Scan", () => Scan(now, false));
                return;
            }

            if (now >= _nextScan) { _nextScan = now + ScanStep; Guard.Run("Demolition.Scan", () => Scan(now, true)); }
            int done = 0;
            for (; done < MaxPerStep && _jobs.Count > 0; done++) { int id = _jobs.Dequeue(); Guard.Run("Demolition.Destroy", () => DestroyOne(id)); }
            if (_jobs.Count > 0) _next = now + DrainStep;
            else if (done > 0)
                Log($"file vidée : {_destroyed} détruit(s) au total" + (_noRuin > 0 ? $", dont {_noRuin} sans ruine visible" : "") +
                    (_skippedRuin > 0 ? $", {_skippedRuin} déjà en ruine entre-temps" : "") + (_notFound > 0 ? $", {_notFound} introuvable(s)" : "") +
                    (_protected + _protectedUnknown + _protectedLate > 0 ? $" ; épargnés pour la mission : {_protected} (+{_protectedLate} au moment de raser, +{_protectedUnknown} protection pas prête)" : "") +
                    (_supplied > 0 ? $" ; épargnés parce qu'une escouade les tient avec du ravitaillement à portée : {_supplied}" : ""));
        }

        static BSvc Resolve(out bool viaSession)
        {
            viaSession = false;
            var s = Mod.Svc<BSvc>();
            if (s != null) { viaSession = true; _sessionRouteSeen = true; return s; }
            // ShellHitSystem.Dispose clears only its static _game (+0x08), never _buildingService (+0x38), and BuildingService.Dispose
            // leaves IsInitialized=true: once the Session route has worked, a null there means the battle is closed -> no stale fallback.
            if (_sessionRouteSeen) return null;
            try { return HitSys._buildingService; } catch { return null; }
        }

        static int Setup()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var dict = _bs.Buildings; if (dict == null) return 0;
            int bad = 0, noObj = 0;
            var en = dict.GetEnumerator();
            while (en.MoveNext())
            {
                try
                {
                    var kv = en.Current; var c = kv.Value; if (c == null) continue;
                    var go = c.SegmentObject; if (go == null) { noObj++; continue; }   // Unity null: no live building object
                    var p = c.ShootPosition;
                    var s = new Seg { Id = kv.Key, C = c, Obj = go, Last = c.LastDamage, X = p.x, Z = p.z };
                    _segs.Add(s); _byId[s.Id] = s;
                }
                catch { bad++; }
            }
            if (_segs.Count == 0) return 0;
            string vanilla = "?", stat = "?", pct = "?", net = "?";
            try { var cfg = GameConfig.Instance; vanilla = cfg?.BuildingsConfig?.BuildingDamageThreshold.ToString() ?? "?"; pct = cfg?.OnBuildingDestroyPrecentage.ToString() ?? "?"; } catch { }
            try { stat = HitSys._buildingDamageThreshold.ToString(); } catch { }
            try { net = NetStatus.Status.ToString(); } catch { }
            Log($"prêt : {_segs.Count} segments en {sw.Elapsed.TotalMilliseconds:0.0} ms" + (bad + noObj > 0 ? $" ({bad} illisibles, {noObj} sans objet)" : "") +
                $", via={(_viaSession ? "Session" : "ShellHitSystem")} seuilVanilla={vanilla} seuilStatique={stat} tuésOccupants%={pct} réseau={net} seuilLourd={Heavy()} seuilVoisins={_neigh.Value}");
            return _segs.Count;
        }

        /// One float read per segment. plan=false only refreshes the baseline. No lambda in the loop body (a capture there
        /// would allocate one closure per segment per scan).
        static void Scan(float now, bool plan)
        {
            var sw = plan ? null : System.Diagnostics.Stopwatch.StartNew();
            float heavy = Heavy();
            int bad = 0; string err = null;
            _heavyLogs = 0; _heavyHidden = 0;
            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i]; float ld;
                try { ld = s.C.LastDamage; }
                catch (Exception ex) { s.Dead = true; bad++; err ??= ex.Message; continue; }
                if (ld == s.Last) continue; s.Last = ld;
                if (!plan || _done.Contains(s.Id)) continue;
                if (ld >= heavy) PlanGuarded(s, ld);
                else if (ld >= MinHeavy) Calib(now, s.Id, ld, heavy);
            }
            if (_heavyHidden > 0) Log($"(+{_heavyHidden} autre(s) impact(s) lourd(s) non détaillé(s), file={_jobs.Count})");
            if (bad > 0)
            {
                // unreadable segments leave the scan for the rest of the battle (an exception per poll per segment would be costly)
                _segs.RemoveAll(x => x.Dead);
                foreach (var id in new List<int>(_byId.Keys)) if (_byId[id].Dead) _byId.Remove(id);
                Log($"{bad} segment(s) illisible(s) retiré(s) de la surveillance : {err}");
            }
            if (sw != null) Log($"surveillance active : {_segs.Count} segments, balayage en {sw.Elapsed.TotalMilliseconds:0.00} ms");
        }

        static void PlanGuarded(Seg s, float dmg) => Guard.Run("Demolition.Plan", () => Plan(s, dmg));

        // true when the segment is no longer the original building (vanilla InitRemain re-inits the same component with the ruin GameObject)
        static bool IsRuin(Seg s) { var go = s.C.SegmentObject; return go == null || go.Pointer != s.Obj.Pointer; }

        /// True when the mission needs this place (or its protection is not known yet): the segment is spared and counted.
        static bool Spared(Seg s, float dmg, string what)
        {
            int st = Missions.ProtectedAt(s.X, s.Z, out string why);
            if (st == 0) return false;
            if (st < 0) _protectedUnknown++; else _protected++;
            if (_protLogs++ < MaxProtLogs)
                Log($"impact lourd seg={s.Id} ({what}) dégâts={dmg:0} pos=({s.X:0},{s.Z:0}) : bâtiment protégé, non rasé ({why})");
            return true;
        }

        static void Plan(Seg s, float dmg)
        {
            _done.Add(s.Id);
            bool ruined = IsRuin(s); int self = 0, nb = 0, nr = 0, spared = 0;
            if (!ruined) { if (Spared(s, dmg, "touché")) spared++; else { _jobs.Enqueue(s.Id); self = 1; } }
            if (dmg >= _neigh.Value)
            {
                var ids = s.C.NeighborsIds; int len = ids == null ? 0 : ids.Length;
                for (int i = 0; i < len && nb < 4; i++)
                {
                    try
                    {
                        if (_byId.TryGetValue(ids[i], out var o) && !o.Dead && !_done.Contains(o.Id) && !IsRuin(o))
                        {
                            _done.Add(o.Id);
                            if (Spared(o, dmg, "voisin")) { spared++; continue; }
                            _jobs.Enqueue(o.Id); nb++;
                        }
                    }
                    catch { }
                }
            }
            float r = Math.Clamp((dmg - 80f) * 0.5f, 0f, 40f);
            if (r > 0f)
            {
                float r2 = r * r;
                for (int i = 0; i < _segs.Count; i++)
                {
                    var o = _segs[i]; float dx = o.X - s.X, dz = o.Z - s.Z;
                    if (dx * dx + dz * dz > r2 || o.Dead || _done.Contains(o.Id)) continue;
                    try { if (IsRuin(o)) continue; } catch { continue; }
                    _done.Add(o.Id);
                    if (Spared(o, dmg, "dans le rayon")) { spared++; continue; }
                    _jobs.Enqueue(o.Id); nr++;
                }
            }
            if (_heavyLogs++ < MaxHeavyLogs)
                Log($"impact lourd seg={s.Id} dégâts={dmg:0} pos=({s.X:0},{s.Z:0}) déjàRuine={ruined} -> {self} + {nb} voisins + {nr} dans {r:0} m (file={_jobs.Count}){(spared > 0 ? $", {spared} épargné(s) pour la mission" : "")}");
            else _heavyHidden++;
        }

        static void DestroyOne(int id)
        {
            if (_bs == null) return;
            // re-check at execution time: a job can wait several seconds and vanilla may have collapsed the segment meanwhile
            // (fresh ruin with full HP): a ruin is never destroyed again
            if (!_byId.TryGetValue(id, out var s) || s.Dead) return;
            if (IsRuin(s)) { _skippedRuin++; return; }
            // re-check: a zone may have appeared or a script unit entered the building while the job waited
            int st = Missions.ProtectedAt(s.X, s.Z, out string why);
            if (st != 0)
            {
                _protectedLate++;
                if (_protLogs++ < MaxProtLogs) Log($"seg={id} pos=({s.X:0},{s.Z:0}) : bâtiment protégé au moment de le raser, non rasé ({why})");
                return;
            }
            if (!_bs.TryGetBuildingEntityById(id, out EcsEntity e))
            {
                _notFound++;
                int left = -1; try { left = _bs.Buildings?.Count ?? -1; } catch { }
                if (left <= 0) { Log($"seg={id} introuvable, service des bâtiments vidé : {_jobs.Count} destruction(s) annulée(s)"); _jobs.Clear(); }
                else if (!_notFoundLogged) { _notFoundLogged = true; Log($"seg={id} introuvable (les suivants sont seulement comptés)"); }
                return;
            }
            // asked last, once the segment is known to exist: it is the only line here that searches the map
            if (HeldAndSupplied(s))
            {
                _supplied++;
                if (_supplyLogs++ < MaxSupplyLogs) Log($"seg={id} pos=({s.X:0},{s.Z:0}) : tenu par une escouade avec le ravitaillement de SON camp à portée, non rasé");
                return;
            }
            var sh = new Shooter();                 // zeroed boxed struct, like NodeDestroyBuilding.OnActivated
            _bs.DestroyBuilding(ref e, ref sh, -1);  // -1 = random ruin prefab (vanilla)
            _destroyed++;
            try { if (!IsRuin(s)) _noRuin++; } catch { }
        }

        /// True when a squad is inside this segment AND a live supply crate OF THAT SQUAD'S OWN SIDE stands within the game's supply
        /// radius: the mod then leaves the building alone. The side matters: without it the player's own supply truck parked behind
        /// his line would spare the block the enemy is holding in front of it - and with the mod's wider supply radius it would do so
        /// from three times as far. The garrison's side is read from the units standing on the segment and the crate is asked for with
        /// that same side, so both answers come from the same bridge and the same numbering.
        /// Fails OPEN (destroys) when the search is unreadable, so a broken Lua bridge can only bring back the module's normal
        /// behaviour instead of silently switching demolition off. Called at most a few times per second, never per frame.
        static bool HeldAndSupplied(Seg s)
        {
            if (_supplyHold == null || !_supplyHold.Value) return false;
            try
            {
                _map ??= new LuaMap();
                var pos = new UnityEngine.Vector3(s.X, s.C.ShootPosition.y, s.Z);
                var near = _map.GetBuildingsInRange(pos, SegSearch);
                int len = near == null ? 0 : near.Length;
                bool held = false;
                for (int i = 0; i < len && !held; i++)
                {
                    var b = near[i];
                    if (b == null) continue;
                    var seg = b.Segment;
                    if (seg == null || seg.Id != s.Id) continue;
                    held = b.HasUnitsInside();
                }
                if (!held) return false;
                int side = SideHolding(pos);
                if (side < 0) return false;                       // nobody identified around the segment: the building is razed as usual
                var depot = _map.GetNearestSupplyPoint(pos, SupplyRadius(), side, -1);
                return depot != null && depot.IsAlive() && depot.GetSupplyAmount() > 0;
            }
            catch (Exception e)
            {
                if (!_supplyBroken) { _supplyBroken = true; Log("occupation ou ravitaillement du bâtiment illisible (" + e.GetBaseException().Message + ") : la règle « le ravitaillement tient le bâtiment » ne s'applique pas"); }
                return false;
            }
        }

        /// Side of the units standing on this segment, or -1 when none is found. The two sides are asked in turn, exactly as the
        /// shared unit scan does (GetUnits(..., side, -1)), so the number returned means the same thing as the crate filter below.
        static int SideHolding(UnityEngine.Vector3 pos)
        {
            for (int side = 0; side < 2; side++)
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LuaUnit> arr = null;
                try { arr = _map.GetUnits(pos, HoldSearch, side, -1); } catch { return -1; }
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    try { var u = arr[i]; if (u != null && u.IsAlive()) return side; } catch { }
                }
            }
            return -1;
        }

        /// The game's own supply radius: the very number the circle on screen draws at exactly twice its value.
        static float SupplyRadius()
        {
            try
            {
                float r = GameConfig.Instance?.SupplyConfig?.ResupplyRadius ?? 0f;
                if (r > 1f && r <= 5000f) return r;
            }
            catch { }
            return DefaultSupplyRadius;
        }

        /// Solo / online from the network state only: in a campaign the AI players are not flagged IsBot, so counting humans is wrong (it read 8 in solo).
        static void CheckCoop(GameSessionContext ctx)
        {
            bool net, slave, sh; string state;
            try { net = NetScen.IsNetwork; slave = NetScen.IsScenarioSlave; sh = NetScen.IsScenarioHost; state = NetStatus.Status.ToString(); }
            catch (Exception ex)
            {
                if (_solo != null || _coopErr != ex.Message) Log("état réseau illisible, démolition suspendue: " + ex.Message);
                _solo = null; _coopErr = ex.Message; return;
            }
            _coopErr = null;
            if (net || slave || sh || state == "Loading" || state == "Deploy" || state == "Game") _everOnline = true;   // latched for the battle: a disconnect must not re-enable local destruction
            bool? solo = !_everOnline;
            if (solo != _solo)
                Log(solo == true ? $"partie solo (réseau={state}) : démolition active"
                  : $"partie en ligne (réseau={state} scénarioRéseau={net} esclave={slave} hôteScénario={sh}) : démolition " + (_online.Value ? "ACTIVE (risque de désynchro)" : "désactivée"));
            _solo = solo;
        }

        static void Calib(float now, int id, float ld, float heavy)
        {
            if (now < _nextCalib) { _calibSkipped++; return; }
            _nextCalib = now + (_calibLines < 20 ? 5f : 120f); _calibLines++;   // 20 detailed lines, then one summary every 2 minutes
            Log($"impact moyen seg={id} dégâts={ld:0.0} (< seuil {heavy})" + (_calibSkipped > 0 ? $" (+{_calibSkipped} autres)" : ""));
            _calibSkipped = 0;
        }
    }
}
