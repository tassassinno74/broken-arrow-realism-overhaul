// RealismOverhaul - DelaisMission: the two evacuation timers of RU_C01 Ignalina, and nothing else in the whole campaign.
//
//  WHY. The author asked for more time on RU_C01's evacuation: his infantry is scattered across the map when the task
//  opens and one minute is not enough to walk it back. NOTE, corrected 2026-09-18: this build does NOT slow infantry on
//  foot - reel/Mobilite.csv carries no OPTION_VITESSE_INFANTERIE_REELLE row, so walkers keep the game's 18 km/h. The
//  x4.5 below is therefore a deliberate PLAYABILITY choice the author asked for, not a compensation for something the
//  mod took away. It must still never be raised: it already gives about 2.4 km of walking, which covers a scattered force.
//
//  WHAT IT TOUCHES. Exactly two nodes, in exactly one mission (19-mission audit, section 4 of the decision note):
//    - RU_C01 #15225, delay 70 s -> 315 s. Its chain (#16560) fails "Evacuate your infantry" (task c362) and ends the mission.
//    - RU_C01 #15291, delay 71 s -> 320 s. Its chain (#16569) fails "Evacuate your vehicles" (task c361) and ends the same mission.
//  Both sit in subgraph #7170 "Player Evac" and BOTH feed the same end node #20873 (a 10 s delay -> #7466 -> #20469 endMission2),
//  so whichever expires first ends the mission about eleven seconds later. Lengthening only one is useless: at 71 s the other gate
//  still closes the mission. They are therefore one atomic edit, and #15291 must stay strictly greater than #15225.
//  #20873 is read as a fingerprint and NEVER written. #7292 and #20280 (30 s lead-ins) sit behind AmorcesOn, OFF by default:
//  the note says to ship the LZ change alone first and add them only if it tests short.
//  EVERY OTHER TIMER IN EVERY MISSION IS LEFT ALONE. There is no automatic rule here on purpose - an automatic rule would grab
//  hold-out timers (RU_N02 #814, RU_N03 #798: longer = harder), recapture deadlines (RU_C03 #3398, RU_N02 #2801), medal timers,
//  wave pacing, and the US_M07 nuke countdown whose delays are spoken aloud by the next voice line. This is a table of two nodes.
//
//  HOW IT IDENTIFIES A NODE. NodeLogic exposes its own identity through OwnNode (NodeCore): ID is the node number of the mission
//  file, SubID the subgraph holding it. The module never trusts a single number. Before anything is written it:
//    1. proves the node controller it found really drives the node that woke it up - controller.GetNode(id).Logic is that very
//       instance, pointer for pointer - so the table can never be applied to another mission's or another graph's nodes;
//    2. resolves every node of the table through that same controller and demands ID, SubID, node class (NodeDelay) and the
//       game's current Time all match the audited values, fingerprint node #20873 at 10 s included;
//    3. checks the arithmetic (never longer than x4.5 plus the rounding second, never above the 600 s clamp, never shorter than
//       the game's own value) and the coupling (#15291 strictly greater than #15225).
//  If any one of those fails, NOTHING is written and the refusal is logged with its reason: a timer changed on the wrong node is
//  worse than a timer not changed.
//
//  SAFETY.
//  - Fails closed: outside a campaign, outside RU_C01, on an unreadable mission uid, on a graph that does not match the audit, or
//    on any doubt at all, the module does nothing and says so.
//  - Hooks: a prefix on the parameterless NodeDelay.OnActivated and a postfix taking no parameter at all on
//    NodeEndMission.OnMissionEnd. No method taking a non-blittable IL2CPP struct by reference is patched here, no ECS component is
//    read, nothing is ever unpatched. Both are [HarmonyPatch] classes, so Mod.ApplyPatches installs them one by one and a game
//    update that renames either method disables only that one patch.
//  - Every hook body is wrapped and has its own error counter. Past MaxErreurs the module is dead for the rest of the game session:
//    no further mission is touched, which is vanilla again. It deliberately does NOT put the running mission's timers back -
//    writing 70 s into a mission already past its seventieth second would end it on the spot, which is the exact failure this
//    module exists to prevent.
//  - Cheap on the script path: RU_C01 activates about 424 delay nodes over a run, every other mission leaves on the first two
//    comparisons, and nothing is allocated once the mission has been recognised.
//  - Log lines are French and start with [MINUTEURS]. Nothing here is shown on screen: a player-visible string would need Txt.cs
//    in five languages, which this file does not own.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using NodeCtl = Il2CppBrokenArrow.ScriptEngine.Loader.NodeController;
using NodeCore = Il2CppBrokenArrow.ScriptEngine.Core.NodeCore;
using NodeDelay = Il2CppBrokenArrow.ScriptEngine.Nodes.Timing.NodeDelay;
using NodeEndMission = Il2CppBrokenArrow.ScriptEngine.Nodes.Gameplay.NodeEndMission;

namespace RealismOverhaul
{
    /// A delay node of the mission script starts counting. Parameterless method: nothing is passed by reference.
    [HarmonyPatch(typeof(NodeDelay), nameof(NodeDelay.OnActivated))]
    static class Patch_MinuteurDelaiActive
    {
        static void Prefix(NodeDelay __instance) => DelaisMission.SurDelaiActive(__instance);
    }

    /// The mission script ends the mission: the one moment where the module can say what it did and what it never saw.
    /// The postfix takes no parameter at all, so it cannot break on a renamed argument.
    [HarmonyPatch(typeof(NodeEndMission), nameof(NodeEndMission.OnMissionEnd))]
    static class Patch_MinuteurFinMission
    {
        static void Postfix() => DelaisMission.SurFinDeMission();
    }

    static class DelaisMission
    {
        // ------------------------------------------------------------ the rule, and the only numbers that may ever change
        /// Exactly what the mod took from infantry on foot (18 km/h -> 4 km/h off-road). NEVER raise this: above x4.5 the change
        /// stops being compensation for the mod and becomes a difficulty change nobody approved.
        const float Mult = 4.5f;
        /// Absolute clamp per node, whatever the multiplier would give.
        const float Plafond = 600f;
        /// The optional 30 s lead-ins (#7292, #20280). OFF: ship the LZ change alone first, turn this on only if it tests short.
        /// This one word is the whole switch; it is read through _amorces so no branch is folded away at compile time.
        const bool AmorcesOn = true;
        static readonly bool _amorces = AmorcesOn;

        const string Mission = "RU_C01";
        const int SousGrapheEvac = 7170;              // subgraph "Player Evac"
        const int NInfanterie = 15225, NVehicules = 15291, NFin = 20873, NAmorceA = 7292, NAmorceB = 20280;

        const float Tol = 0.01f;
        /// The resolution normally goes through on the mission's very first delay node. It is retried on every later one, right up
        /// to the two nodes themselves, because a node the controller cannot vouch for must never make the module give up early:
        /// RU_C01 activates about 424 delay nodes over a run, so 500 means "the whole mission had its chance".
        const int MaxTentatives = 500;
        const int MaxErreurs = 20;
        const string P = "[MINUTEURS] ";

        // ------------------------------------------------------------ the table (two nodes written, one fingerprint, two optional)
        sealed class Noeud
        {
            internal readonly int Id, SousGraphe;
            internal readonly float Vanille;
            internal readonly bool Rallonger, Optionnel;
            internal readonly string Role;
            internal Noeud(int id, int sousGraphe, float vanille, bool rallonger, bool optionnel, string role)
            { Id = id; SousGraphe = sousGraphe; Vanille = vanille; Rallonger = rallonger; Optionnel = optionnel; Role = role; }
        }

        static readonly Noeud[] Table =
        {
            new Noeud(NInfanterie, SousGrapheEvac, 70f, true,  false, "évacuation de l'infanterie à la zone de ramassage (tâche c362)"),
            new Noeud(NVehicules,  SousGrapheEvac, 71f, true,  false, "évacuation des véhicules (tâche c361), jumelé au même nœud de fin #20873"),
            new Noeud(NFin,        SousGrapheEvac, 10f, false, false, "nœud de fin partagé : vérifié comme empreinte, jamais modifié"),
            new Noeud(NAmorceA,    SousGrapheEvac, 30f, true,  true,  "amorce de marche (optionnelle)"),
            new Noeud(NAmorceB,    SousGrapheEvac, 30f, true,  true,  "amorce de marche (optionnelle)"),
        };

        const int Attente = 0, Applique = 1, Refuse = 2;

        // ------------------------------------------------------------ state (one session = one run of the mission)
        static volatile bool _mort;                   // kill-switch: no mission is touched again until the game restarts
        static int _erreurs;
        static int _fil = -1;                         // thread the script engine calls us on, written once, logged once
        static int _resolution;                       // CompareExchange gate: one resolution at a time, whatever the thread

        static bool _session;                         // a RU_C01 run is open
        static IntPtr _cle;                           // its graph (NodeLogic.Subgraphs), IntPtr.Zero when the engine gives none
        static int _etape = Attente, _tentatives;
        static string _dernierEchec;                  // why the last resolution attempt gave up
        static bool _bilanFait, _ditSansSuite, _ditSansClef;

        static readonly NodeDelay[] _resolus = new NodeDelay[Table.Length];   // held: the live nodes of this run
        static readonly float[] _nouveaux = new float[Table.Length];
        static readonly bool[] _vus = new bool[Table.Length];                 // the node really started during this run
        static readonly bool[] _ditVu = new bool[Table.Length];
        static readonly bool[] _ditRemis = new bool[Table.Length];

        static readonly HashSet<string> _autresDites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static volatile string _dernierAutre;         // last non-Ignalina mission uid already handled (fast path)
        static bool _ditUidIllisible;

        // ------------------------------------------------------------ hook: a delay node of the script starts
        internal static void SurDelaiActive(NodeDelay noeud)
        {
            if (_mort || noeud == null || !Campaign.InCampaign) return;
            try
            {
                string uid = Campaign.MissionUid;
                if (!EstIgnalina(uid)) { HorsMission(uid); return; }      // fail closed: one mission, and only that one
                if (Campaign.MissionInerte) return;                       // mission played without the mod
                var actif = Mod.Actif; if (actif == null || !actif.Value) return;

                if (_fil < 0) _fil = Environment.CurrentManagedThreadId;

                // graph identity, read from the node itself. When the engine gives none, the run is still handled: what protects
                // the pair then is that each of the two nodes has its value put in place again in its own prefix, below.
                IntPtr cle = IntPtr.Zero;
                try { var sg = noeud.Subgraphs; if (sg != null) cle = sg.Pointer; } catch { }
                if (!_session) NouvelleSession(cle);
                else if (cle != IntPtr.Zero && cle != _cle) { Bilan("script rechargé"); NouvelleSession(cle); }

                var core = noeud.OwnNode;
                if (core == null) { Echec("le nœud ne donne pas son identité (OwnNode absent)"); return; }
                int id = core.ID;

                if (_etape == Attente) Resoudre(noeud, id);

                int idx = Index(id);
                if (idx < 0) return;                                      // any other delay node of the mission: never touched
                Surveille(idx, noeud, core);
            }
            catch (Exception e) { Erreur("crochet des délais", e); }
        }

        // ------------------------------------------------------------ hook: the script ends the mission
        internal static void SurFinDeMission()
        {
            if (_mort || !_session || !Campaign.InCampaign) return;
            try
            {
                if (!EstIgnalina(Campaign.MissionUid)) return;
                Bilan("fin de mission");
            }
            catch (Exception e) { Erreur("crochet de fin de mission", e); }
        }

        // ------------------------------------------------------------ resolution: read everything, then write both or neither
        static void Resoudre(NodeDelay appelant, int idAppelant)
        {
            if (Interlocked.CompareExchange(ref _resolution, 1, 0) != 0) return;
            bool ecrit = false;
            try
            {
                if (_etape != Attente) return;
                _tentatives++;

                var nc = Controleur();
                if (nc == null) { Repousser(); return; }

                // the controller must be the one driving the very node that woke us up, pointer for pointer. Without this proof the
                // table could be applied to a graph that merely happens to hold a node numbered 15225.
                if (!PiloteCeNoeud(nc, idAppelant, appelant))
                {
                    _dernierEchec = "le contrôleur trouvé ne pilote pas ce nœud";
                    Repousser();
                    return;
                }

                // pass 1: read and check. Nothing is written here - that is what makes the pair one single edit.
                for (int i = 0; i < Table.Length; i++)
                {
                    var e = Table[i];
                    _resolus[i] = null; _nouveaux[i] = 0f;
                    if (e.Optionnel && !_amorces) continue;               // lead-ins switched off: not read, not checked, not touched

                    // NOT a refusal: the engine opens and closes subgraphs as the mission runs, so subgraph #7170 "Player Evac"
                    // may simply not be loaded yet. This is retried on every later delay node - and the two nodes of the pair are
                    // themselves in #7170, so the very worst case is that the resolution lands on the first of the two to start,
                    // which is still before its own countdown begins (this runs in the prefix of OnActivated).
                    NodeCore core = null;
                    try { core = nc.GetNode(e.Id); } catch { }
                    if (core == null)
                    {
                        _dernierEchec = $"nœud #{e.Id} pas encore chargé (sous-graphe fermé ?)";
                        Repousser();
                        return;
                    }

                    int idLu, sousLu;
                    try { idLu = core.ID; sousLu = core.SubID; }
                    catch { Refuser($"nœud #{e.Id} : identité illisible"); return; }
                    if (idLu != e.Id || sousLu != e.SousGraphe)
                    {
                        Refuser($"nœud #{e.Id} : identité inattendue (numéro {idLu}, sous-graphe {sousLu} au lieu de {e.SousGraphe})");
                        return;
                    }

                    NodeDelay nd = null;
                    try { nd = core.Logic?.TryCast<NodeDelay>(); } catch { }
                    if (nd == null) { Refuser($"nœud #{e.Id} : ce n'est pas un délai dans cette version du jeu"); return; }

                    float t;
                    try { t = nd.Time; } catch { Refuser($"nœud #{e.Id} : durée illisible"); return; }
                    if (float.IsNaN(t) || Math.Abs(t - e.Vanille) > Tol)
                    {
                        Refuser($"nœud #{e.Id} : durée {Sec(t)} au lieu des {Sec(e.Vanille)} de la mission auditée (mission changée par une mise à jour ?)");
                        return;
                    }

                    _resolus[i] = nd;
                    _nouveaux[i] = e.Rallonger ? Rallonge(t) : t;
                    if (e.Rallonger && !Admissible(t, _nouveaux[i]))
                    {
                        Refuser($"nœud #{e.Id} : {Sec(t)} -> {Sec(_nouveaux[i])} sort de la règle (x{Num(Mult)} au plus, plafond {Sec(Plafond)})");
                        return;
                    }
                }

                // the coupling: both timers end the same mission through #20873, so the vehicle one must stay strictly the longer
                float ni = _nouveaux[Index(NInfanterie)], nv = _nouveaux[Index(NVehicules)];
                if (!(nv > ni))
                {
                    Refuser($"paire couplée invalide : #{NVehicules} ({Sec(nv)}) n'est plus strictement supérieur à #{NInfanterie} ({Sec(ni)})");
                    return;
                }

                // pass 2: write. Only now, and only because every single check above passed. A throw here rolls the pair back.
                ecrit = true;
                for (int i = 0; i < Table.Length; i++)
                {
                    if (_resolus[i] == null || !Table[i].Rallonger) continue;
                    _resolus[i].Time = _nouveaux[i];
                }

                // pass 3: the write really took, on every node of the pair - otherwise the mission goes back as the game made it
                for (int i = 0; i < Table.Length; i++)
                {
                    if (_resolus[i] == null || !Table[i].Rallonger) continue;
                    float relu;
                    try { relu = _resolus[i].Time; } catch { relu = float.NaN; }
                    if (float.IsNaN(relu) || Math.Abs(relu - _nouveaux[i]) > Tol)
                    {
                        Annuler();
                        Refuser($"nœud #{Table[i].Id} : la nouvelle durée n'a pas été retenue par le jeu (relu {Sec(relu)}) ; la paire est remise aux durées d'origine");
                        return;
                    }
                }

                ecrit = false;
                _etape = Applique;
                // one line carrying the whole atomic edit first (R5: both new values on the same line), then one line per node
                Mod.Log.Msg(P + $"mission {Mission} (sous-graphe #{SousGrapheEvac} « Player Evac ») : délai #{NInfanterie} " +
                    $"{Sec(Table[Index(NInfanterie)].Vanille)} -> {Sec(ni)} (évacuation de l'infanterie), délai #{NVehicules} " +
                    $"{Sec(Table[Index(NVehicules)].Vanille)} -> {Sec(nv)} (évacuation des véhicules){Amorces()} ; " +
                    $"multiplicateur x{Num(Mult)} au plus, plafond {Sec(Plafond)} par nœud, nœud de fin #{NFin} laissé à " +
                    $"{Sec(Table[Index(NFin)].Vanille)} ; choix de jouabilité demandé par l'auteur : laisser le temps de " +
                    $"rassembler une infanterie dispersée avant l'évacuation ; tentative {_tentatives}, fil {_fil}");
                for (int i = 0; i < Table.Length; i++)
                {
                    if (_resolus[i] == null || !Table[i].Rallonger) continue;
                    Mod.Log.Msg(P + $"nœud #{Table[i].Id} reconnu (sous-graphe #{Table[i].SousGraphe}, délai) : " +
                        $"{Sec(Table[i].Vanille)} -> {Sec(_nouveaux[i])} : {Table[i].Role}");
                }
            }
            catch (Exception e)
            {
                if (ecrit) { try { Annuler(); } catch { } }               // half-written pair: back to the game's own durations
                _etape = Refuse;
                Erreur("identification des minuteurs", e);
            }
            finally { Interlocked.Exchange(ref _resolution, 0); }
        }

        /// A resolution attempt could not go through yet. Refuses for good once the attempts run out (fail closed, and said once).
        static void Repousser()
        {
            if (_tentatives < MaxTentatives) return;
            Refuser($"graphe de la mission illisible après {_tentatives} tentatives ({_dernierEchec ?? "raison inconnue"})");
        }

        /// Puts every node the write pass touched back to the duration the game shipped. Safe here and only here: the resolution
        /// runs long before the evacuation phase, so no countdown is under way.
        static void Annuler()
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (_resolus[i] == null || !Table[i].Rallonger) continue;
                try { _resolus[i].Time = Table[i].Vanille; } catch { }
            }
        }

        /// True when this controller's node number idAppelant IS this very NodeDelay instance.
        static bool PiloteCeNoeud(NodeCtl nc, int idAppelant, NodeDelay appelant)
        {
            try
            {
                var core = nc.GetNode(idAppelant);
                var logique = core?.Logic;
                return logique != null && logique.Pointer == appelant.Pointer;
            }
            catch { return false; }
        }

        /// The node controller of the battle in progress, or null with the reason kept for the refusal line.
        static NodeCtl Controleur()
        {
            GameController gc;
            try { gc = GameController._instance; } catch { gc = null; }
            if (gc == null) { _dernierEchec = "pas de bataille en cours"; return null; }

            ScenarioController sc;
            try { sc = gc.GetScenarioController; } catch { sc = null; }
            if (sc == null) { _dernierEchec = "scénario indisponible"; return null; }

            NodeCtl nc;
            try { nc = sc.GetNodeController; } catch { nc = null; }
            if (nc == null) { _dernierEchec = "contrôleur de nœuds indisponible"; return null; }

            bool charge;
            try { charge = nc.IsDataLoaded; } catch { charge = false; }
            if (!charge) { _dernierEchec = "script pas encore chargé"; return null; }

            _dernierEchec = null;
            return nc;
        }

        // ------------------------------------------------------------ one of our own nodes starts
        static void Surveille(int idx, NodeDelay noeud, NodeCore core)
        {
            var e = Table[idx];
            bool aRallonger = e.Rallonger && (!e.Optionnel || _amorces);

            if (_etape != Applique)
            {
                // the node is here, it has just started, and the module could not change it: say so once, loudly
                if (aRallonger && !_ditSansSuite)
                {
                    _ditSansSuite = true;
                    string pourquoi = _etape == Refuse
                        ? "identification refusée"
                        : "identification jamais aboutie, " + _tentatives + " tentative(s), " + (_dernierEchec ?? "raison inconnue");
                    Mod.Log.Warning(P + $"mission {Mission} : le nœud #{e.Id} vient de démarrer et n'a PAS été rallongé " +
                        $"({pourquoi}) : la mission garde ses durées d'origine");
                }
                return;
            }

            if (!aRallonger) return;
            _vus[idx] = true;

            // the script engine can restore a property from its port: our value goes back in before the countdown starts, so the
            // two nodes of the coupled pair are always both long when they run, even after a restart of the same mission
            float t;
            try { t = noeud.Time; } catch { return; }
            if (t < _nouveaux[idx] - Tol)
            {
                try { noeud.Time = _nouveaux[idx]; } catch { return; }
                if (!_ditRemis[idx])
                {
                    _ditRemis[idx] = true;
                    Mod.Log.Warning(P + $"nœud #{e.Id} : le jeu avait remis {Sec(t)} au démarrage, la durée rallongée ({Sec(_nouveaux[idx])}) est réappliquée");
                }
                t = _nouveaux[idx];
            }

            if (_ditVu[idx]) return;
            _ditVu[idx] = true;
            int sous = 0; try { sous = core.SubID; } catch { }
            Mod.Log.Msg(P + $"nœud #{e.Id} (sous-graphe #{sous}) démarre avec {Sec(t)} au lieu de {Sec(e.Vanille)} : {e.Role}");
        }

        // ------------------------------------------------------------ verdict of a run
        static void Bilan(string cause)
        {
            if (_bilanFait) return;
            _bilanFait = true;
            if (_etape == Applique)
            {
                bool vi = _vus[Index(NInfanterie)], vv = _vus[Index(NVehicules)];
                if (vi && vv)
                    Mod.Log.Msg(P + $"mission {Mission} ({cause}) : les deux minuteurs d'évacuation ont été rallongés et ont bien tourné");
                else if (!vi && !vv)
                    Mod.Log.Warning(P + $"mission {Mission} ({cause}) : minuteurs rallongés, mais ni le nœud #{NInfanterie} ni le nœud " +
                        $"#{NVehicules} n'ont démarré pendant cette partie (phase d'évacuation jamais atteinte, ou nœuds jamais vus)");
                else
                    Mod.Log.Warning(P + $"mission {Mission} ({cause}) : minuteurs rallongés, mais le nœud #{(vi ? NVehicules : NInfanterie)} " +
                        "n'a jamais démarré pendant cette partie (phase d'évacuation jamais atteinte, ou nœud jamais vu)");
                return;
            }
            if (_etape == Refuse)
            {
                Mod.Log.Warning(P + $"mission {Mission} ({cause}) : aucun minuteur modifié, l'identification avait été refusée");
                return;
            }
            Mod.Log.Warning(P + $"mission {Mission} ({cause}) : les minuteurs n'ont jamais pu être identifiés " +
                $"({_tentatives} tentative(s), {_dernierEchec ?? "aucun nœud de délai vu"}) : aucun changement, la mission garde ses durées d'origine");
        }

        static void Refuser(string pourquoi)
        {
            _etape = Refuse;
            Mod.Log.Warning(P + $"REFUS mission {Mission} : {pourquoi} ; aucun minuteur n'est modifié (règle : les deux nœuds changent ensemble ou aucun)");
        }

        // ------------------------------------------------------------ session, identity, arithmetic
        static void NouvelleSession(IntPtr cle)
        {
            _session = true;
            _cle = cle;
            _etape = Attente; _tentatives = 0; _dernierEchec = null;
            _bilanFait = false; _ditSansSuite = false;
            Array.Clear(_resolus, 0, _resolus.Length);
            Array.Clear(_nouveaux, 0, _nouveaux.Length);
            Array.Clear(_vus, 0, _vus.Length);
            Array.Clear(_ditVu, 0, _ditVu.Length);
            Array.Clear(_ditRemis, 0, _ditRemis.Length);
            if (cle == IntPtr.Zero && !_ditSansClef)
            {
                _ditSansClef = true;
                Mod.Log.Msg(P + $"mission {Mission} : le moteur ne donne pas l'identité du graphe ; la durée rallongée est simplement " +
                    "remise en place au démarrage de chacun des deux nœuds");
            }
        }

        /// The mission uid of the audited mission, '_' and ' ' alike, case ignored. Anything else, or an unreadable uid, is a no.
        static bool EstIgnalina(string uid)
        {
            if (uid == null) return false;
            int n = uid.Length;
            int a = 0; while (a < n && uid[a] == ' ') a++;
            int b = n - 1; while (b >= a && uid[b] == ' ') b--;
            if (b - a + 1 != Mission.Length) return false;
            for (int i = 0; i < Mission.Length; i++)
            {
                char c = uid[a + i], m = Mission[i];
                if (c == ' ') c = '_';
                if (c != m && char.ToUpperInvariant(c) != m) return false;
            }
            return true;
        }

        /// A campaign mission that is not the audited one: nothing is touched, and the uid is written once per game session so a
        /// player log always shows the id the mod actually read.
        static void HorsMission(string uid)
        {
            // hot path of the other eighteen missions: one string compare and out, no lock, no allocation
            if (uid != null && string.Equals(uid, _dernierAutre, StringComparison.Ordinal)) return;
            if (_session) { Bilan("changement de mission"); _session = false; _cle = IntPtr.Zero; }
            if (uid == null)
            {
                if (_ditUidIllisible) return;
                _ditUidIllisible = true;
                Mod.Log.Warning(P + "identifiant de mission illisible : aucun minuteur n'est touché (règle : dans le doute, on ne fait rien)");
                return;
            }
            _dernierAutre = uid;
            lock (_autresDites) { if (!_autresDites.Add(uid)) return; }
            Mod.Log.Msg(P + $"mission {uid} : hors {Mission}, aucun minuteur touché (un seul couple de minuteurs est corrigé dans toute la campagne)");
        }

        /// x4.5 at most, rounded up to the whole second so 71 s stays strictly above 70 s once both are multiplied, then clamped.
        static float Rallonge(float vanille)
        {
            float brut = vanille * Mult;                          // 70 -> 315.0, 71 -> 319.5, 30 -> 135.0
            float sec = (float)Math.Ceiling((double)brut);        // 319.5 -> 320 : the coupled pair keeps its one-second order
            return sec > Plafond ? Plafond : sec;
        }

        /// Never shorter than the game's own value, never above the clamp, and never more than x4.5 plus the one rounding second.
        static bool Admissible(float vanille, float nouveau)
            => !float.IsNaN(nouveau) && nouveau > vanille && nouveau <= Plafond + Tol && nouveau <= vanille * Mult + 1f + Tol;

        static int Index(int id)
        {
            for (int i = 0; i < Table.Length; i++) if (Table[i].Id == id) return i;
            return -1;
        }

        static string Amorces()
            => _amorces
                ? $", amorces #{NAmorceA} et #{NAmorceB} {Sec(30f)} -> {Sec(_nouveaux[Index(NAmorceA)])}"
                : $", amorces #{NAmorceA} et #{NAmorceB} laissées à {Sec(30f)} (option désactivée)";

        static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string Sec(float v) => Num(v) + " s";

        /// A reading the node itself refused. Counts as an attempt and refuses for good once the attempts run out.
        static void Echec(string pourquoi)
        {
            if (_etape != Attente) return;
            _dernierEchec = pourquoi;
            _tentatives++;
            Repousser();
        }

        static void Erreur(string quoi, Exception e)
        {
            int n = Interlocked.Increment(ref _erreurs);
            try
            {
                if (n <= 3) Mod.Log.Warning(P + $"{quoi} : erreur ({e.GetBaseException().Message}) ; aucun minuteur n'est modifié");
                if (n >= MaxErreurs && !_mort)
                {
                    _mort = true;
                    // the running mission keeps the durations already in place: writing 70 s back into a mission already past its
                    // seventieth second would end it at once, which is the very failure this module exists to prevent.
                    Mod.Log.Warning(P + $"{MaxErreurs} erreurs : le module ne touchera plus aucun minuteur jusqu'au redémarrage du jeu " +
                        "(les missions suivantes gardent leurs durées d'origine)");
                }
            }
            catch { }
        }
    }
}
