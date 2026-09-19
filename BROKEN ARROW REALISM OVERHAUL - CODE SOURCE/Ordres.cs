// RealismOverhaul — order-panel options chosen by the player (WARNO-like), for the local player's selected units only.
//  Stances (on/off per unit):  Riposte seulement (RIP), Défendre la position (DEF), Débarquer au contact (DEBC).
//  One-click actions:          Aller se ravitailler (RAVX), Occuper le bâtiment proche (GAR), Repli vers zone amie (RZ),
//                              Dernier contact (DPC), which only reads and tells.
//  Nothing happens to a unit the player did not switch on or click. Artillery and aircraft never receive these orders
//  (DPC gives no order to anything: it is a report).
//
// DERNIER CONTACT (DPC) — what it is, and what it is NOT.
//  What the player asked for was a marker left on the map where an enemy was last seen, fading with time. The mod has no way
//  to draw anything on the map: everything it shows is a retouch of a game object that already exists (a cloned order button,
//  a sprite swapped on the supply ring, a number rewritten in the Alt tool). The engine has no "last seen" ghost to borrow
//  either: WasDetectedOnce is a bare boolean with no position (alldump.txt:38113), the minimap rebuilds its buffer every
//  frame, and the fire-mission feedback is built by two static methods that take a FireMissionInfo BY REFERENCE
//  (alldump.txt:42368/42371) — the exact signature shape this mod never touches. So there is NO marker here.
//  What is here is the half the mod really can do honestly: it remembers WHERE the enemies the player's own side really saw
//  were standing the last time they were visible, and one click tells him. The position is frozen at that moment and never
//  moves again, so nothing the player did not legitimately see is ever shown. A unit killed while visible leaves no contact
//  (the map read used for that can only REMOVE a contact, never create one), and a contact is forgotten after DpcDuree.
//  WHAT A DPC PASS REALLY COSTS. It is NOT a free read of a warm cache. In a normal battle nothing else forces the 1 s
//  visibility cache — repli only reads it when a vehicle is hit, ambush and logistics are off by default — so each pass is a
//  cold Visibility.Evaluate: three whole-map LuaMap.GetUnits, three per-unit sources over own + enemy units, two
//  CountUnits.Invoke and about fifteen managed allocations, of the order of 200 IL2CPP calls every 2 s. That is why the pass
//  does not run at all while the two Assistants.cs lines below are missing and the button therefore cannot exist.
//  It also has one side effect on a file this one does not own: forcing the visibility from t=0 sets TeamState.FirstEnemies
//  at the start of the battle instead of at the first read, so the FAV notice "pas de visibilité" and the self-test of
//  Visibilite.Judge() settle about a minute earlier than without DPC. The message stays true; only its timing moves.
//  WHY A CONTACT IS NOT FROZEN ON THE FIRST MISSED PASS. Visibilite.cs picks one of five sources and can reject it in the
//  middle of a battle, and the sources disagree by a lot (the player's own log: the same map read 22, 16 or 3 enemies
//  depending on the source). So the pass forgets everything when the source changes, and a unit must be missing from the
//  spotted list DpcManquesMin passes in a row before its position is frozen — and the moment kept is the moment it was
//  really last seen, not the moment the mod concluded it was gone.
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
        static readonly List<int> _selDPC = new();
        static readonly string[] IconsRIP = { "Order_HoldFireOn", "Order_HoldFire" };
        static readonly string[] IconsDEF = { "Order_Attack", "FM_Targeting_Creep" };
        static readonly string[] IconsDEBC = { "Order_Unload" };
        static readonly string[] IconsRAVX = { "Ability_SupplyRearm", "Ability_Supply" };
        static readonly string[] IconsGAR = { "Ability_Sneak", "Order_Unload" };
        static readonly string[] IconsRZ = { "Order_Reverse", "Order_BackToBase" };
        // both names are in the sprite inventory the mod logged in battle ("icônes connues", Assistants.Diagnose) and neither is
        // the first choice of another button, so the two orders never end up wearing the same icon
        static readonly string[] IconsDPC = { "Ability_LaserDesignation", "FM_Targeting_Line" };

        // No Opt member exists for this order in Assistants.cs and that file is not ours to edit, so the flag is declared here.
        // A [Flags] enum carries unnamed values perfectly well; 1024 is far above every value that file uses (256 = RZ).
        const Opt OptDPC = (Opt)1024;

        sealed class RipState { public bool Held; public int Hp; public float LastHit = -1000f; }
        sealed class DefState { public V3 Anchor; public float Radius; public float OrderAt = -1000f; public bool Chasing; }
        sealed class DebcState { public int Hp; public bool Armed = true; public float LastContact = -1000f; public int Pax = -1; }
        sealed class ActJob { public float T0; public string What; public V3? PendingDest; }

        /// One enemy the player's side really saw and does not see any more. Pos is the position of the LAST pass where it was
        /// visible: it is written once and never touched again, so the mod never follows a unit the player cannot see.
        /// Lost is the moment of that same pass - when it was last SEEN, not when the mod confirmed the loss a few passes later.
        sealed class Contact { public V3 Pos; public float Lost; public string Name; }

        static readonly Dictionary<int, RipState> _rip = new();
        static readonly Dictionary<int, DefState> _def = new();
        static readonly Dictionary<int, DebcState> _debc = new();
        static readonly Dictionary<(int uid, Opt opt), ActJob> _actJobs = new();
        static float _nextOrd;

        // ---- dernier contact (DPC)
        const float DpcPeriode = 2f;          // one visibility read every 2 s: the answer is cached 1 s, and a contact is at most this stale
        const float DpcDuree = 120f;          // a contact older than this is forgotten (that is what "fading with time" means here)
        const int DpcMax = 40;                // hard cap on what is remembered: no list can grow without a bound
        const int DpcMaxErreurs = 20;         // kill-switch: past this the tracking stops for the battle and the order says nothing
        const int DpcManquesMin = 3;          // passes in a row a unit must be missing before its position is frozen (about 6 s)
        static readonly Dictionary<int, (V3 pos, int role, float vu)> _dpcVu = new();  // enemy UID -> where it stood, and when, on the last pass where it WAS visible
        static readonly Dictionary<int, int> _dpcManques = new();               // enemy UID -> passes in a row it has been missing from the spotted list
        static readonly Dictionary<int, Contact> _dpcPerdus = new();            // enemy UID -> where and when the contact was lost
        static readonly List<int> _dpcTmp = new();                              // scratch, reused: the pass allocates nothing
        static readonly HashSet<int> _dpcVivants = new();
        static readonly Dictionary<int, string> _dpcNoms = new();
        static readonly List<string> _dpcLignes = new();
        static float _nextDpc, _dpcNextLog;
        static int _dpcErreurs;
        static string _dpcSource;             // the visibility source the current contact list was built with
        static bool _dpcArme = true, _dpcVuOk, _dpcTexteDit;

        // Whether the button can exist at all in this build: TitleKey/DescKey live in Assistants.cs, which this file may not
        // edit, and without their two arms the button is never created. The answer cannot change during a session, so it is
        // read once and kept: 0 not asked yet, 1 yes, 2 no.
        static int _dpcBouton;

        /// True when Assistants.cs really answers the DPC texts, i.e. when the button can be created and the tracking is worth
        /// its cost. While it is false the whole DPC pass is skipped and the order costs the game strictly nothing.
        static bool DpcBoutonPossible()
        {
            if (_dpcBouton == 0)
            {
                bool ok;
                try { ok = TitleKey(OptDPC) == TxtKey.OR_DPC_TITLE && DescKey(OptDPC) == TxtKey.OR_DPC_DESC; }
                catch { ok = false; }
                _dpcBouton = ok ? 1 : 2;
            }
            return _dpcBouton == 1;
        }

        static bool IsAction(Opt o) => (o & (Opt.RAVX | Opt.GAR | Opt.RZ | OptDPC)) != 0;
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
            // A button's name and hover description are read through TitleKey(Opt) / DescKey(Opt), and those two switches live in
            // Assistants.cs, which this file may not edit. Their default branch answers OR_RZ_TITLE / OR_RZ_DESC, so an unknown
            // option would show the player the name and the explanation of "Repli vers zone amie" — a wrong text on a real button.
            // Until these two lines are added there, the button is simply not created: nothing wrong is ever shown.
            // WHERE THEY GO: both switches are switch EXPRESSIONS whose LAST arm is the discard `_ => TxtKey.OR_RZ_*,`. An arm
            // written after the discard is unreachable and the build fails (CS8510). So each line goes BEFORE that discard,
            // right after the `Opt.GAR => ...` arm of its own switch:
            //     in TitleKey (Assistants.cs), just after «Opt.GAR => TxtKey.OR_GAR_TITLE,» :  OptDPC => TxtKey.OR_DPC_TITLE,
            //     in DescKey  (Assistants.cs), just after «Opt.GAR => TxtKey.OR_GAR_DESC,»  :  OptDPC => TxtKey.OR_DPC_DESC,
            if (DpcBoutonPossible())
                _btns.Add(Make(src, "RealismOverhaul_DPC", OptDPC, _selDPC, IconsDPC));
            else if (!_dpcTexteDit)
            {
                _dpcTexteDit = true;
                Mod.Log.Warning("[ASSIST] ordre « dernier contact » : bouton non créé, son nom et sa description ne sont pas branchés. " +
                                "Dans Assistants.cs, ajouter « OptDPC => TxtKey.OR_DPC_TITLE, » dans TitleKey et " +
                                "« OptDPC => TxtKey.OR_DPC_DESC, » dans DescKey, chaque ligne JUSTE APRÈS la ligne « Opt.GAR => ... » " +
                                "et AVANT la dernière ligne « _ => ... » (une ligne écrite après celle-ci empêche la compilation)");
            }
        }

        static void OrdersClearSel()
        {
            _selRIP.Clear(); _selDEF.Clear(); _selDEBC.Clear(); _selRAVX.Clear(); _selGAR.Clear(); _selRZ.Clear();
            _selDPC.Clear();
        }

        /// Adds one selected local unit to the lists of the options it can use.
        static void OrdersSelect(LuaUnit u)
        {
            int r = u.UnitRole;
            // "Dernier contact" gives no order: it is a report, so the artillery keeps it too (it is the piece that needs it most).
            // It is read BEFORE the artillery exit below, which is left exactly as it was for the six other options.
            if ((r >= 10 && r <= 16) || IsInfantryRole(r) || IsHeliRole(r) || IsArtillery(r)) _selDPC.Add(u.UID);
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
            int enemyTeam = myTeam == 0 ? 1 : 0;
            // the lost contacts are followed even when no stance is armed: a contact can only be noticed as it is lost
            TrackContacts(gc, enemyTeam);
            if (_rip.Count + _def.Count + _debc.Count + _actJobs.Count == 0) return;
            var cmds = gc._GetEcsEventBus_k__BackingField?.Commands;
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
            // the report has its own sentence (contacts, not units) and gives no order at all: it leaves before the common path
            if (s.Opt == OptDPC) { ReportLastContacts(gc, s, units); return; }
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

        // ------------------------------------------------------------ DPC : dernier contact (lecture seule, aucun ordre donné)

        /// Follows the enemies the player's own side really sees and freezes the position of the ones that leave that list.
        /// Read only: nothing of the game is written and no order is given. One pass every DpcPeriode seconds, on the main
        /// thread, inside the guarded pass of the stances; its own error counter switches it off for the battle rather than
        /// ever spoiling that pass, and the rest of the orders keep working.
        static void TrackContacts(GameController gc, int enemyTeam)
        {
            // Nothing at all while the button cannot exist: a visibility pass every 2 s for a feature no player can reach would be
            // a cost paid for nothing, and a log line claiming a result nobody can see.
            if (!DpcBoutonPossible()) return;
            // GAME time (frozen while the game is paused), like the artillery jobs: a long pause must not age a contact,
            // and no pass is needed while nothing on the map can move.
            float now = GameNow;
            if (!_dpcArme || now < _nextDpc) return;
            _nextDpc = now + DpcPeriode;
            if (Campaign.MissionInerte) return;
            try
            {
                var vis = SpottedInfo(gc, enemyTeam);                            // cached 1 s by Visibilite.cs, shared with FAV, repli and embuscade
                // the flag follows the LATEST pass: a source validated early and rejected later must turn the report back to
                // "la vue du champ de bataille n'est pas lisible", not leave it saying "aucun contact perdu de vue"
                _dpcVuOk = vis != null && vis.Usable;
                if (!_dpcVuOk) return;                                           // "not readable" is never "lost from sight"
                // Visibilite.cs can change source in the middle of a battle, and two sources do not see the same enemies. Every
                // UID that the old source saw and the new one does not would be frozen as a lost contact in one pass, while the
                // player is looking straight at those units. So a change of source throws the whole list away and starts over.
                if (!string.Equals(vis.Source, _dpcSource, StringComparison.Ordinal))
                {
                    if (_dpcSource != null)
                        Log($"dernier contact : la source de visibilité est passée de « {_dpcSource} » à « {vis.Source} », " +
                            $"les {_dpcPerdus.Count} contact(s) en mémoire sont oubliés (deux sources ne voient pas les mêmes ennemis)");
                    _dpcSource = vis.Source;
                    _dpcVu.Clear(); _dpcPerdus.Clear(); _dpcManques.Clear();
                    return;
                }
                // 1. who was visible on the last pass and is not any more. One missed pass is not a loss: the spotted set
                // flickers between two passes, so a unit has to be missing DpcManquesMin times in a row.
                _dpcTmp.Clear();
                foreach (var kv in _dpcVu)
                {
                    if (vis.Uids.Contains(kv.Key)) continue;
                    int m = _dpcManques.TryGetValue(kv.Key, out int n) ? n + 1 : 1;
                    if (m >= DpcManquesMin) { _dpcTmp.Add(kv.Key); _dpcManques.Remove(kv.Key); }
                    else _dpcManques[kv.Key] = m;
                }
                if (_dpcTmp.Count > 0) NoteLostContacts(enemyTeam, now);
                // 2. the ones still visible: position and moment refreshed, their miss counter cleared, and a contact kept on
                // them has no reason to exist any more
                for (int i = 0; i < vis.Units.Count; i++)
                {
                    var v = vis.Units[i];
                    _dpcVu[v.uid] = (v.pos, v.role, now);
                    if (_dpcManques.Count > 0) _dpcManques.Remove(v.uid);
                    if (_dpcPerdus.Count > 0) _dpcPerdus.Remove(v.uid);
                }
                // 3. the contacts fade: past DpcDuree they are forgotten
                if (_dpcPerdus.Count == 0) return;
                _dpcTmp.Clear();
                foreach (var kv in _dpcPerdus) if (now - kv.Value.Lost > DpcDuree) _dpcTmp.Add(kv.Key);
                for (int i = 0; i < _dpcTmp.Count; i++) _dpcPerdus.Remove(_dpcTmp[i]);
            }
            catch (Exception e)
            {
                if (++_dpcErreurs >= DpcMaxErreurs)
                {
                    _dpcArme = false;
                    _dpcVu.Clear(); _dpcPerdus.Clear(); _dpcManques.Clear();
                    Mod.Log.Warning($"[ASSIST] dernier contact : {_dpcErreurs} erreurs, suivi arrêté pour cette bataille " +
                                    $"(les autres ordres fonctionnent) : {e.Message}");
                }
                else Warn("dpc", "dernier contact : " + e.Message);
            }
        }

        /// The UIDs left in _dpcTmp have just left the spotted list. One read of the enemy units says which of them are still
        /// alive: a unit destroyed under the player's eyes must leave NO contact, or the report would say "it went somewhere"
        /// about something he killed himself. That read can only REMOVE a contact, never create one, so it never shows the
        /// player more than he legitimately saw. When it cannot be done, no contact at all is kept from this pass.
        static void NoteLostContacts(int enemyTeam, float now)
        {
            _dpcVivants.Clear(); _dpcNoms.Clear();
            try
            {
                _map ??= new LuaMap();
                var arr = _map.GetUnits(V3.zero, 1_000_000f, enemyTeam, -1);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    var e = arr[i];
                    try
                    {
                        if (e == null || !e.IsAlive()) continue;
                        int uid = e.UID;
                        _dpcVivants.Add(uid);
                        if (_dpcTmp.Contains(uid)) _dpcNoms[uid] = e.Name;        // the name of a unit the player had in sight: he read it himself
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Warn("dpc-liste", "dernier contact : unités ennemies illisibles, aucun contact retenu cette passe : " + ex.Message);
                for (int i = 0; i < _dpcTmp.Count; i++) _dpcVu.Remove(_dpcTmp[i]);
                return;
            }
            int perdus = 0, morts = 0;
            for (int i = 0; i < _dpcTmp.Count; i++)
            {
                int uid = _dpcTmp[i];
                if (!_dpcVu.TryGetValue(uid, out var v)) continue;
                _dpcVu.Remove(uid);
                if (!_dpcVivants.Contains(uid)) { morts++; continue; }
                if (v.role < 0) continue;                                    // aircraft and helicopters (role -1 in Visibilite.cs): they
                                                                             // leave the sight every few seconds by flying, and a point on
                                                                             // the ground would say nothing about where they went
                if (_dpcPerdus.Count >= DpcMax) continue;
                _dpcNoms.TryGetValue(uid, out string nom);
                // the moment kept is the one where the unit was really last SEEN, not the one where the mod concluded it was
                // gone: the confirmation takes DpcManquesMin passes, and "vu il y a 0 s" would be false by that much
                _dpcPerdus[uid] = new Contact { Pos = v.pos, Lost = v.vu, Name = nom };
                perdus++;
            }
            if (perdus > 0 && now >= _dpcNextLog)
            {
                _dpcNextLog = now + 15f;
                Log($"dernier contact : {perdus} ennemi(s) perdu(s) de vue{(morts > 0 ? $", {morts} détruit(s) (aucun contact gardé)" : "")}" +
                    $" ; {_dpcPerdus.Count} contact(s) en mémoire");
            }
        }

        // A contact is NOT dropped when its unit dies out of sight. The player never saw that death: dropping it would make the
        // count fall between two clicks and tell him that an enemy he cannot see is dead — information he never observed, and
        // the one thing a last-known-position report must not do. A real marker stays until the intel ages out, and that is
        // what DpcDuree is for. A unit destroyed WHILE visible is a different case and leaves no contact at all: that check is
        // in NoteLostContacts, and it is legitimate because the player watched it die.

        /// Click on "Dernier contact": how many enemies the player's side has lost from sight, and where the nearest of them
        /// stood the last time it was really seen. Nothing is ordered, nothing is drawn, nothing is followed.
        static void ReportLastContacts(GameController gc, Btn s, List<LuaUnit> units)
        {
            float now = GameNow;                                             // same clock as the contacts themselves (see TrackContacts)
            TxtKey title = TitleKey(OptDPC);
            TxtMsg msg;
            bool blind = !_dpcArme || !_dpcVuOk;
            if (blind) msg = new TxtMsg(TxtKey.N_OR_ACTION_NONE, title, TxtKey.OR_FAIL_NO_VIS);
            else
            {
                if (_dpcPerdus.Count == 0) msg = new TxtMsg(TxtKey.N_OR_ACTION_NONE, title, TxtKey.OR_FAIL_NO_LOST);
                else
                {
                    // the distance given is the one to the NEAREST selected unit: what the player would have to cover to go and look
                    float best = float.MaxValue, age = 0f;
                    _dpcLignes.Clear();
                    foreach (var kv in _dpcPerdus)
                    {
                        var c = kv.Value;
                        float d = float.MaxValue;
                        for (int i = 0; i < units.Count; i++)
                            try { float x = Flat(units[i].GetPosition(), c.Pos); if (x < d) d = x; } catch { }
                        if (d >= float.MaxValue) continue;                   // no readable position on the selection: nothing to say about it
                        if (d < best) { best = d; age = now - c.Lost; }
                        if (_dpcLignes.Count < 12) _dpcLignes.Add($"{c.Name ?? "unité inconnue"} à {d:0} m, vu il y a {now - c.Lost:0} s");
                    }
                    if (best >= float.MaxValue) msg = new TxtMsg(TxtKey.N_OR_ACTION_NONE, title, TxtKey.OR_FAIL_REFUSED);
                    else
                    {
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        msg = new TxtMsg(TxtKey.N_OR_LOST_REPORT, title, _dpcPerdus.Count, best.ToString("0", inv), age.ToString("0", inv));
                        Log($"dernier contact : {_dpcPerdus.Count} contact(s), distance à la sélection ({units.Count} unité(s)) : {string.Join(" ; ", _dpcLignes)}");
                    }
                }
            }
            string fr = msg.Fr;
            var lang = Txt.Current;
            Mod.NotifyPair(fr, lang == Lang.FR ? fr : msg.In(lang));
            if (blind || _dpcPerdus.Count == 0) Log("dernier contact : " + fr);
            UpdateUi(gc);
            if (_hovered == s) ShowHint(s);
        }

        static void OrdersReset()
        {
            OrdersClearSel();
            _rip.Clear(); _def.Clear(); _debc.Clear(); _actJobs.Clear();
            _dpcVu.Clear(); _dpcPerdus.Clear(); _dpcTmp.Clear(); _dpcVivants.Clear(); _dpcNoms.Clear(); _dpcLignes.Clear();
            _dpcManques.Clear();
            _nextDpc = 0f; _dpcNextLog = 0f; _dpcErreurs = 0; _dpcArme = true; _dpcVuOk = false; _dpcSource = null;
            // _dpcBouton is NOT reset: whether Assistants.cs answers the DPC texts is a property of the build, not of the battle
            _nextOrd = 0f;
        }
    }
}
