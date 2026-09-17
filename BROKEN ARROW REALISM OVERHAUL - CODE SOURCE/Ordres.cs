// RealismOverhaul — order-panel options chosen by the player (WARNO-like), for the local player's selected units only.
//  Stances (on/off per unit):  Riposte seulement (RIP), Défendre la position (DEF), Débarquer au contact (DEBC).
//  One-click actions:          Aller se ravitailler (RAVX), Occuper le bâtiment proche (GAR), Repli vers zone amie (RZ).
//  Nothing happens to a unit the player did not switch on or click. Artillery and aircraft never receive these orders.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.UI.Orders;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static partial class Assistants
    {
        static readonly List<int> _selRIP = new(), _selDEF = new(), _selDEBC = new(), _selRAVX = new(), _selGAR = new(), _selRZ = new();
        static readonly string[] IconsRIP = { "Order_HoldFireOn", "Order_HoldFire" };
        static readonly string[] IconsDEF = { "Order_Attack", "FM_Targeting_Creep" };
        static readonly string[] IconsDEBC = { "Order_Unload" };
        static readonly string[] IconsRAVX = { "Ability_SupplyRearm", "Ability_Supply" };
        static readonly string[] IconsGAR = { "Ability_Sneak", "Order_Unload" };
        static readonly string[] IconsRZ = { "Order_Reverse", "Order_BackToBase" };

        sealed class RipState { public bool Held; public int Hp; public float LastHit = -1000f; }
        sealed class DefState { public V3 Anchor; public float Radius; public float OrderAt = -1000f; public bool Chasing; }
        sealed class DebcState { public int Hp; public bool Armed = true; public float LastContact = -1000f; public int Pax = -1; }
        sealed class ActJob { public float T0; public string What; public V3? PendingDest; }

        static readonly Dictionary<int, RipState> _rip = new();
        static readonly Dictionary<int, DefState> _def = new();
        static readonly Dictionary<int, DebcState> _debc = new();
        static readonly Dictionary<(int uid, Opt opt), ActJob> _actJobs = new();
        static float _nextOrd;

        static bool IsAction(Opt o) => (o & (Opt.RAVX | Opt.GAR | Opt.RZ)) != 0;
        static bool ActionBusy(Opt o, int uid) => _actJobs.ContainsKey((uid, o));
        static bool IsInfantryRole(int r) => r >= 30 && r <= 36;
        static bool IsHeliRole(int r) => r >= 70 && r <= 73;
        static V3 FlatDir(V3 v) { v.y = 0f; return v.sqrMagnitude > 0.01f ? v.normalized : V3.zero; }
        static long BuildingKey(V3 p) => ((long)Math.Round(p.x) << 32) ^ (long)Math.Round(p.z);

        /// Buttons of the chosen order options (called from BuildUi). Names and descriptions: Txt.cs (TitleKey / DescKey).
        static void MakeOrderButtons(UiOrderButton src)
        {
            _btns.Add(Make(src, "RealismOverhaul_RIP", Opt.RIP, _selRIP, IconsRIP));
            _btns.Add(Make(src, "RealismOverhaul_DEF", Opt.DEF, _selDEF, IconsDEF));
            _btns.Add(Make(src, "RealismOverhaul_DEBC", Opt.DEBC, _selDEBC, IconsDEBC));
            _btns.Add(Make(src, "RealismOverhaul_RAVX", Opt.RAVX, _selRAVX, IconsRAVX));
            _btns.Add(Make(src, "RealismOverhaul_GAR", Opt.GAR, _selGAR, IconsGAR));
            _btns.Add(Make(src, "RealismOverhaul_RZ", Opt.RZ, _selRZ, IconsRZ));
        }

        static void OrdersClearSel()
        {
            _selRIP.Clear(); _selDEF.Clear(); _selDEBC.Clear(); _selRAVX.Clear(); _selGAR.Clear(); _selRZ.Clear();
        }

        /// Adds one selected local unit to the lists of the options it can use.
        static void OrdersSelect(LuaUnit u)
        {
            int r = u.UnitRole;
            if (IsArtillery(r)) return;
            bool s400 = IsS400(u);
            bool direct = ((r >= 10 && r <= 13) || IsInfantryRole(r)) && r != ROLE_AAINF && !s400;
            if (direct) { _selRIP.Add(u.UID); _selDEF.Add(u.UID); }
            if (IsGroundTransportRole(r) && IsTransport(u)) _selDEBC.Add(u.UID);
            if (IsGroundCombat(r) || r == 14) _selRAVX.Add(u.UID);
            if (IsInfantryRole(r)) _selGAR.Add(u.UID);
            if ((r >= 10 && r <= 16) || IsInfantryRole(r) || IsHeliRole(r)) _selRZ.Add(u.UID);
        }

        /// A stance was switched on or off on these units (Toggle).
        static void OrdersOnToggle(Opt opt, List<int> uids, bool on)
        {
            var gc = GameController._instance;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (opt == Opt.RIP)
            {
                var hf = gc?._GetEcsEventBus_k__BackingField?.Commands?.HoldFireCommand;
                if (on)
                {
                    var hold = new List<int>();
                    foreach (var uid in uids)
                    {
                        if (!_mineByUid.TryGetValue(uid, out var u)) continue;
                        int hp = 100; try { hp = u.GetHealPercentage(); } catch { }
                        _rip[uid] = new RipState { Held = true, Hp = hp };
                        hold.Add(uid);
                    }
                    SendHold(hf, hold, true);
                }
                else
                {
                    var free = uids.Where(id => _rip.TryGetValue(id, out var st) && st.Held).ToList();
                    foreach (var uid in uids) _rip.Remove(uid);
                    SendHold(hf, free, false);
                }
            }
            else if (opt == Opt.DEF)
            {
                foreach (var uid in uids)
                {
                    if (!on) { _def.Remove(uid); continue; }
                    if (!_mineByUid.TryGetValue(uid, out var u)) continue;
                    try
                    {
                        float radius = Math.Clamp(DirectRange(u).ground, 200f, 600f);
                        _def[uid] = new DefState { Anchor = u.GetPosition(), Radius = radius };
                        Log($"DEF {u.Name} (uid {uid}) : position défendue, rayon {radius:0} m");
                    }
                    catch (Exception e) { Warn("def-on", "DEF : " + e.Message); }
                }
            }
            else if (opt == Opt.DEBC)
            {
                foreach (var uid in uids)
                {
                    if (!on) { _debc.Remove(uid); continue; }
                    if (!_mineByUid.TryGetValue(uid, out var u)) continue;
                    int hp = 100; try { hp = u.GetHealPercentage(); } catch { }
                    _debc[uid] = new DebcState { Hp = hp };
                }
            }
        }

        /// Every 0.5 s in campaign: runs the stances and follows the one-click actions.
        static void OrdersFrame(GameController gc, int local, int myTeam, float now)
        {
            if (_rip.Count + _def.Count + _debc.Count + _actJobs.Count == 0) return;
            var cmds = gc._GetEcsEventBus_k__BackingField?.Commands;
            int enemyTeam = myTeam == 0 ? 1 : 0;
            List<(V3 pos, int role)> spotted = null;

            // ---- Riposte seulement
            List<int> hold = null, free = null;
            foreach (var kv in _rip.ToList())
            {
                int uid = kv.Key; var st = kv.Value;
                if (!Has(uid, Opt.RIP)) { _rip.Remove(uid); continue; }
                if (!_mineByUid.TryGetValue(uid, out var u)) continue;
                try
                {
                    int hp = u.GetHealPercentage();
                    bool hit = hp < st.Hp;
                    st.Hp = hp;
                    if (hit) st.LastHit = now;
                    if (st.Held && hit)
                    {
                        st.Held = false; (free ??= new()).Add(uid);
                        Log($"RIP {u.Name} (uid {uid}) : touché -> riposte");
                    }
                    else if (!st.Held && now - st.LastHit > 20f)
                    {
                        spotted ??= SpottedCached(gc, enemyTeam);
                        var pos = u.GetPosition();
                        float rng = DirectRange(u).ground;
                        bool contact = spotted.Any(s => s.role >= 0 && Flat(s.pos, pos) <= rng);
                        if (!contact)
                        {
                            st.Held = true; (hold ??= new()).Add(uid);
                            Log($"RIP {u.Name} (uid {uid}) : plus de contact depuis 20 s -> tir retenu");
                        }
                    }
                }
                catch (Exception e) { Warn("rip" + uid, $"RIP uid {uid} : {e.Message}"); }
            }
            var hf = cmds?.HoldFireCommand;
            SendHold(hf, hold, true);
            SendHold(hf, free, false);

            // ---- Défendre la position
            foreach (var kv in _def.ToList())
            {
                int uid = kv.Key; var st = kv.Value;
                if (!Has(uid, Opt.DEF)) { _def.Remove(uid); continue; }
                if (!_mineByUid.TryGetValue(uid, out var u)) continue;
                try
                {
                    var pos = u.GetPosition();
                    bool idle = u.IsIdle();
                    float fromAnchor = Flat(pos, st.Anchor);
                    // sent far away by the player (not by a pursuit of the mod): the stance follows the player's choice and switches off
                    if (fromAnchor > st.Radius * 3f && now - st.OrderAt > 30f)
                    {
                        _def.Remove(uid);
                        if (_byUid.TryGetValue(uid, out var v)) _byUid[uid] = v & ~Opt.DEF;
                        Log($"DEF {u.Name} (uid {uid}) : envoyée à {fromAnchor:0} m de sa position par un ordre -> option coupée");
                        continue;
                    }
                    spotted ??= SpottedCached(gc, enemyTeam);
                    V3? target = null; float best = float.MaxValue;
                    foreach (var s in spotted)
                    {
                        if (s.role < 0) continue;
                        float d = Flat(s.pos, st.Anchor);
                        if (d <= st.Radius && d < best) { best = d; target = s.pos; }
                    }
                    float weapon = Math.Max(150f, DirectRange(u).ground);
                    if (target != null)
                    {
                        float toTarget = Flat(pos, target.Value);
                        if (toTarget > weapon * 0.9f && idle && now - st.OrderAt > 8f)
                        {
                            var dest = pos + FlatDir(target.Value - pos) * Math.Max(0f, toTarget - weapon * 0.8f);
                            if (Flat(dest, st.Anchor) > st.Radius * 1.5f) dest = st.Anchor + FlatDir(dest - st.Anchor) * st.Radius * 1.5f;   // never beyond the leash
                            u.MoveTo(dest, 10, true);
                            st.OrderAt = now; st.Chasing = true;
                            Log($"DEF {u.Name} (uid {uid}) : ennemi repéré à {toTarget:0} m dans la zone -> attaque en avançant de {Flat(pos, dest):0} m");
                        }
                    }
                    else if (st.Chasing && idle && fromAnchor > 40f && now - st.OrderAt > 8f)
                    {
                        u.MoveTo(st.Anchor, 10, false);
                        st.OrderAt = now; st.Chasing = false;
                        Log($"DEF {u.Name} (uid {uid}) : plus d'ennemi dans la zone -> retour à la position ({fromAnchor:0} m)");
                    }
                }
                catch (Exception e) { Warn("def" + uid, $"DEF uid {uid} : {e.Message}"); }
            }

            // ---- Débarquer au contact
            foreach (var kv in _debc.ToList())
            {
                int uid = kv.Key; var st = kv.Value;
                if (!Has(uid, Opt.DEBC)) { _debc.Remove(uid); continue; }
                if (!_mineByUid.TryGetValue(uid, out var u)) continue;
                try
                {
                    int pax = u.GetCargoUnitsCount();
                    int hp = u.GetHealPercentage();
                    bool hit = hp < st.Hp;
                    st.Hp = hp;
                    var pos = u.GetPosition();
                    spotted ??= SpottedCached(gc, enemyTeam);
                    float near = float.MaxValue;
                    foreach (var s in spotted) if (s.role >= 0) { float d = Flat(pos, s.pos); if (d < near) near = d; }
                    bool contact = hit || near <= 500f;
                    // passengers picked up while already in contact (evacuation): no immediate unload, re-armed after 30 s without contact
                    if (st.Pax == 0 && pax > 0 && contact && st.Armed) { st.Armed = false; Log($"DEBC {u.Name} (uid {uid}) : passagers embarqués au contact -> pas de débarquement immédiat"); }
                    st.Pax = pax;
                    if (contact) st.LastContact = now;
                    if (!st.Armed)
                    {
                        if (pax > 0 && now - st.LastContact > 30f) { st.Armed = true; Log($"DEBC {u.Name} (uid {uid}) : réarmé"); }
                        continue;
                    }
                    if (!contact || pax <= 0) continue;
                    int kg = 0; try { kg = u.GetSupplyCargo(); } catch { }
                    if (kg > 0) continue;                                            // a supply truck keeps its cargo
                    st.Armed = false;
                    CancelOne(cmds, uid, "debc");
                    bool ok;
                    try { u.Unload(null, null); ok = true; }
                    catch (Exception e) { Log("DEBC Unload(Lua) refusé, bus : " + e.Message); ok = cmds != null && UnloadViaBus(cmds, uid, pos); }
                    Log($"DEBC {u.Name} (uid {uid}) : {(hit ? "touché" : $"ennemi repéré à {near:0} m")} -> débarquement de {pax} passager(s){(ok ? "" : " ÉCHEC")}");
                }
                catch (Exception e) { Warn("debc" + uid, $"DEBC uid {uid} : {e.Message}"); }
            }

            // ---- follow the one-click actions
            foreach (var kv in _actJobs.ToList())
            {
                var (uid, opt) = kv.Key; var j = kv.Value;
                if (!_mineByUid.TryGetValue(uid, out var u))
                {
                    if (now - j.T0 > 5f) _actJobs.Remove(kv.Key);                 // garrisoned or embarked units leave the list: job done
                    continue;
                }
                try
                {
                    if (j.PendingDest.HasValue)
                    {
                        if (_revJobs.ContainsKey(uid)) continue;                      // still reversing
                        var dest = j.PendingDest.Value; j.PendingDest = null; j.T0 = now;
                        try { u.MoveToFast(dest, 20, false); } catch { u.MoveTo(dest, 20, false); }
                        Log($"RZ {u.Name} (uid {uid}) : marche arrière finie -> repli vers {j.What}");
                        continue;
                    }
                    if ((u.IsIdle() && now - j.T0 > 5f) || now - j.T0 > 240f)
                    {
                        _actJobs.Remove(kv.Key);
                        Log($"{TitleFr(opt)} : {u.Name} (uid {uid}) terminé");
                    }
                }
                catch { _actJobs.Remove(kv.Key); }
            }
        }

        /// Click on a one-click action button.
        static void RunAction(Btn s)
        {
            if (!Enabled.Value) return;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) return;
            int local = cp.UID, myTeam = (int)cp.TeamSide, enemyTeam = myTeam == 0 ? 1 : 0;
            float now = UnityEngine.Time.realtimeSinceStartup;
            bool fresh = s.SnapAt >= 0f && now - s.SnapAt < 0.5f;
            var units = new List<LuaUnit>();
            foreach (var uid in (fresh ? s.Snap : s.Sel).ToList())
                try { if (_mineByUid.TryGetValue(uid, out var u) && u != null && u.IsAlive() && u.GetOwnerPlayerUID() == local) units.Add(u); } catch { }
            if (units.Count == 0) return;
            TxtMsg fail;
            int done = s.Opt == Opt.RAVX ? GoResupply(myTeam, units, now, out fail)
                     : s.Opt == Opt.GAR ? GoGarrison(units, now, out fail)
                     : GoFallBack(gc, myTeam, enemyTeam, units, now, out fail);
            TxtKey title = TitleKey(s.Opt);
            int total = units.Count;
            var failMsg = fail;
            string Text(Lang l) => done > 0
                ? (failMsg.Set && done < total ? Txt.Format(l, TxtKey.N_OR_ACTION_PARTIAL, title, done, total - done, failMsg.In(l)) : Txt.Format(l, TxtKey.N_OR_ACTION_DONE, title, done))
                : Txt.Format(l, TxtKey.N_OR_ACTION_NONE, title, failMsg.Set ? failMsg.In(l) : Txt.Get(l, TxtKey.OR_NOTHING));
            var lang = Txt.Current;
            string fr = Text(Lang.FR);
            Mod.NotifyPair(fr, lang == Lang.FR ? fr : Text(lang));
            UpdateUi(gc);
            if (_hovered == s) ShowHint(s);
        }

        /// A running one-click order (repli, ravitaillement) or an armed "débarquer au contact" with passengers owns the unit's reaction to hits.
        static bool OrderOwnsUnit(int uid, out string owner)
        {
            owner = null;
            if (_actJobs.ContainsKey((uid, Opt.RZ))) owner = "repli vers zone amie";
            else if (_actJobs.ContainsKey((uid, Opt.RAVX))) owner = "ravitaillement";
            else if (_debc.TryGetValue(uid, out var st) && st.Armed && st.Pax > 0) owner = "débarquer au contact";
            return owner != null;
        }

        static void AddJob(int uid, Opt opt, float now, string what, V3? pending = null) =>
            _actJobs[(uid, opt)] = new ActJob { T0 = now, What = what, PendingDest = pending };

        static int GoResupply(int myTeam, List<LuaUnit> units, float now, out TxtMsg fail)
        {
            fail = default; int n = 0;
            _map ??= new LuaMap();
            float range = _ravRange.Value;
            foreach (var u in units)
            {
                try
                {
                    var pos = u.GetPosition();
                    var depot = _map.GetNearestSupplyPoint(pos, range, myTeam, -1);
                    if (depot != null && depot.IsAlive() && depot.GetSupplyAmount() > 0)
                    {
                        CancelOne(GameController._instance?._GetEcsEventBus_k__BackingField?.Commands, u.UID, "ravx");   // a click replaces the current order
                        u.ResupplyAtDepot(depot);
                        AddJob(u.UID, Opt.RAVX, now, "dépôt");
                        Log($"RAVX {u.Name} (uid {u.UID}) : vers le dépôt uid {depot.UID} à {Flat(pos, depot.Position):0} m (stock {depot.GetSupplyAmount()} kg)");
                        n++; continue;
                    }
                    LuaUnit truck = null; float bd = range;
                    foreach (var t in _mineByUid.Values)
                    {
                        try { if (t.UID == u.UID || t.GetSupplyCargo() <= 0) continue; float d = Flat(pos, t.GetPosition()); if (d < bd) { bd = d; truck = t; } } catch { }
                    }
                    if (truck == null) { fail = new TxtMsg(TxtKey.OR_FAIL_NO_SUPPLY, range.ToString("0", System.Globalization.CultureInfo.InvariantCulture)); Log($"RAVX {u.Name} (uid {u.UID}) : {fail.Fr}"); continue; }
                    CancelOne(GameController._instance?._GetEcsEventBus_k__BackingField?.Commands, u.UID, "ravx");
                    u.MoveTo(truck.GetPosition(), 30, false);
                    AddJob(u.UID, Opt.RAVX, now, "camion");
                    Log($"RAVX {u.Name} (uid {u.UID}) : vers le camion {truck.Name} (uid {truck.UID}) à {bd:0} m");
                    n++;
                }
                catch (Exception e) { fail = TxtKey.OR_FAIL_REFUSED; Warn("ravx", $"RAVX : {e.Message}"); }
            }
            return n;
        }

        static int GoGarrison(List<LuaUnit> units, float now, out TxtMsg fail)
        {
            fail = default; int n = 0;
            _map ??= new LuaMap();
            var taken = new HashSet<long>();
            foreach (var u in units)
            {
                try
                {
                    var pos = u.GetPosition();
                    var arr = _map.GetBuildingsInRange(pos, 300f);
                    LuaBuilding best = null; float bd = float.MaxValue;
                    for (int i = 0; i < (arr?.Length ?? 0); i++)
                    {
                        var b = arr[i];
                        if (b == null || b.IsDestroyed() || b.GetHouseCapacity() <= 0 || b.HasUnitsInside()) continue;
                        var sp = b.GetShootPosition();
                        if (taken.Contains(BuildingKey(sp))) continue;
                        float d = Flat(pos, sp);
                        if (d < bd) { bd = d; best = b; }
                    }
                    if (best == null) { fail = TxtKey.OR_FAIL_NO_BUILDING; Log($"GAR {u.Name} (uid {u.UID}) : {fail.Fr}"); continue; }
                    taken.Add(BuildingKey(best.GetShootPosition()));
                    u.Embark(best);
                    AddJob(u.UID, Opt.GAR, now, "bâtiment");
                    Log($"GAR {u.Name} (uid {u.UID}) : vers le bâtiment à {bd:0} m (capacité {best.GetHouseCapacity()})");
                    n++;
                }
                catch (Exception e) { fail = TxtKey.OR_FAIL_REFUSED; Warn("gar", $"GAR : {e.Message}"); }
            }
            return n;
        }

        static int GoFallBack(GameController gc, int myTeam, int enemyTeam, List<LuaUnit> units, float now, out TxtMsg fail)
        {
            fail = default; int n = 0;
            _map ??= new LuaMap();
            var spotted = SpottedCached(gc, enemyTeam);
            var zones = new List<V3>();
            try
            {
                var objs = _map.GetObjectives(false);
                for (int i = 0; i < (objs?.Length ?? 0); i++)
                {
                    var o = objs[i];
                    if (o == null) continue;
                    if (EnemyAi.SideOf(gc, EnemyAi.OwnerRaw(o)) == myTeam) zones.Add(o.Position);   // unknown owner = not friendly
                }
            }
            catch (Exception e) { Warn("rz-zones", "RZ : zones illisibles : " + e.Message); }
            foreach (var u in units)
            {
                try
                {
                    var pos = u.GetPosition();
                    V3? enemy = null; float ne = float.MaxValue;
                    foreach (var s in spotted) { if (s.role < 0) continue; float d = Flat(pos, s.pos); if (d < ne) { ne = d; enemy = s.pos; } }
                    V3 away = enemy.HasValue ? FlatDir(pos - enemy.Value) : V3.zero;
                    V3? dest = null; float bz = float.MaxValue;
                    foreach (var z in zones)
                    {
                        float d = Flat(pos, z);
                        if (d < 60f) continue;                                                                   // already there
                        if (enemy.HasValue && ne < 2000f && V3.Dot(FlatDir(z - pos), away) < -0.3f) continue;    // toward the enemy
                        if (d < bz) { bz = d; dest = z; }
                    }
                    string where = dest.HasValue ? $"la zone amie à {bz:0} m" : "400 m à l'opposé de l'ennemi";
                    if (!dest.HasValue)
                    {
                        if (!enemy.HasValue) { fail = TxtKey.OR_FAIL_NO_ZONE; Log($"RZ {u.Name} (uid {u.UID}) : {fail.Fr}"); continue; }
                        dest = pos + away * 400f;
                    }
                    int r = u.UnitRole;
                    if (r >= 10 && r <= 13 && enemy.HasValue && ne < 600f)
                    {
                        string why;
                        bool reversed = false;
                        try { reversed = TryReverse(u, pos, pos + away * 80f, now, out why); } catch (Exception e) { why = e.Message; }
                        if (reversed)
                        {
                            AddJob(u.UID, Opt.RZ, now, where, dest.Value);
                            Log($"RZ {u.Name} (uid {u.UID}) : ennemi à {ne:0} m -> marche arrière 80 m puis repli vers {where}");
                            n++; continue;
                        }
                        Log($"RZ {u.Name} (uid {u.UID}) : marche arrière impossible ({why}) -> repli direct");
                    }
                    CancelOne(gc._GetEcsEventBus_k__BackingField?.Commands, u.UID, "rz");                          // a click replaces the current order (Lua moves queue behind it)
                    try { u.MoveToFast(dest.Value, 20, false); } catch { u.MoveTo(dest.Value, 20, false); }
                    AddJob(u.UID, Opt.RZ, now, where);
                    Log($"RZ {u.Name} (uid {u.UID}) : repli vers {where}");
                    n++;
                }
                catch (Exception e) { fail = TxtKey.OR_FAIL_REFUSED; Warn("rz", $"RZ : {e.Message}"); }
            }
            return n;
        }

        static void OrdersReset()
        {
            OrdersClearSel();
            _rip.Clear(); _def.Clear(); _debc.Clear(); _actJobs.Clear();
            _nextOrd = 0f;
        }
    }
}
