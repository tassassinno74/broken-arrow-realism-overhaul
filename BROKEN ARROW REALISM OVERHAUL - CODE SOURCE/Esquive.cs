// RealismOverhaul - Esquive (v1.0) : les hélicoptères pilotés par l'IA (les DEUX camps) descendent en vol bas dès qu'on leur tire
//  dessus, et remontent à l'altitude qu'ils avaient dès que le ciel est calme autour d'eux. Campagne solo seulement, jamais en ligne,
//  jamais dans la mission US_M01, jamais sur l'écran de fin.
//  Pourquoi : le jeu n'a AUCUN comportement d'altitude pour l'IA (aucun système de Client.Ecs.AI ne parle d'altitude). Sur les 4 414
//  relevés de la sonde du 17/09, 2 983 hélicos volaient entre 40 et 60 m et aucun n'est jamais passé sous 25 m. Le moteur ne connaît
//  que deux altitudes : basse 8 m, haute 40 m.
//   D1 Descente  : un missile est TIRÉ sur l'hélico (postfix sur le départ des missiles du jeu, qui donne tireur et cible) -> il
//                  descend avant de prendre le coup ; ou il prend 2 impacts en 4 s, ou 1 seul impact d'une munition anti-aérienne
//                  (la DCA et les canons ne passent pas par le départ des missiles : eux ne se voient qu'à l'impact).
//   D2 Remontée  : au moins 12 s en bas, puis 25 s sans un tir ET plus aucun tireur anti-aérien à 1 200 m ; OU le tireur qui l'a fait
//                  descendre est détruit ou repassé hors de sa propre portée ; OU 45 s en bas au maximum, quoi qu'il arrive.
//   D3 Le script d'abord : le script de mission commande lui-même l'altitude de ses hélicos (21 fois rien que dans Blackout). Missions
//                  écoute maintenant ce canal ; AUCUN hélico tenu par le script (trajet en cours, ordre récent, apparition, groupe cité
//                  ou commandé : Missions.ScriptReason) n'est jamais touché, une unité que le script vient de commander est protégée
//                  60 s de plus, et un ordre d'altitude du script fait lâcher l'hélico au mod sans le moindre ordre en retour.
//                  C'est volontairement très strict pour une première version : sur une mission très scriptée le module ne touchera
//                  presque personne. Le relevé compte chaque refus (« hélico tenu par le script de mission ») : de vraies parties
//                  diront s'il y a lieu d'assouplir. Une mission qui marche passe avant une descente de plus.
//   D4 Pas d'invisibilité : un hélico descendu PAR LE MOD ne profite jamais de la règle « missile infrarouge impossible contre un
//                  hélico en vol bas » des règles anti-hélico. Le vol bas reste un abri de terrain, jamais une immunité offerte.
//  Mesure puis action, dans la MÊME bataille : le module commence en mesure seule (il écrit ce qu'il AURAIT fait) et ne se permet
//  d'agir qu'une fois le canal d'altitude du script vraiment écouté et les unités du script identifiées. Le tout premier hélico
//  descendu de la bataille est un ESSAI : aucun autre ne descend tant que cet hélico n'a pas prouvé, 15 s plus tard, qu'il avance
//  encore et qu'il n'a pas perdu un trajet du script. S'il est resté coincé ou s'il a perdu un trajet, il remonte aussitôt et plus
//  rien ne descend de la bataille : c'est la seule façon de vérifier, sans lancer le jeu, que l'ordre d'altitude du moteur n'annule
//  pas les ordres en cours. Le même contrôle tourne ensuite sur chaque hélico descendu (1 trajet perdu = tout est coupé).
//  Sécurités : compteur d'erreurs avec coupure, garde anti-plantage (marqueur de bataille non terminée dans les réglages : une
//  bataille interrompue en action = mesure seule à la suivante, deux = plus rien pour cette version), tout est rendu quand on s'arrête
//  (jamais un hélico laissé en bas), au plus 1 ordre par hélico toutes les 8 s, 40 par hélico et par bataille, 4 par seconde en tout.
//  Le module ne donne QUE des ordres d'altitude : jamais un déplacement, jamais une cible, jamais une annulation.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using AltSys = Il2CppBrokenArrow.Client.Ecs.Commands.Systems.AltitudeChangeCommandSystem;
using FlyMode = Il2CppBrokenArrow.Client.Ecs.Navigation.HelicopterFlyMode;
using CrashComp = Il2CppBrokenArrow.Client.Ecs.Navigation.Components.HelicopterCrashFlyComponent;
using Spawner = Il2CppBrokenArrow.Client.Ecs.BattleSystem.ProjectileSpawnerHelper;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using DbService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Esquive
    {
        const string GuardVersion = "1.1";

        // ---- réglages (secondes et mètres), tels que proposés au joueur
        const float Periode = 0.25f;                 // rythme du module
        const float FenetreTouches = 4f;             // 2 impacts dans cette fenêtre = une attaque
        const float FenetreDemande = 2f;             // une menace vue il y a plus longtemps que ça ne fait plus descendre
        const float MiniBas = 12f;                   // temps minimum en vol bas (anti yo-yo)
        const float CalmeSecondes = 25f;             // calme exigé avant de remonter
        const float CalmeTireur = 8f;                // calme exigé pour remonter parce que le tireur est détruit ou hors de portée
        const float RayonMenace = 1200f;             // plus aucun tireur anti-aérien ennemi dans ce rayon
        const float MaxBas = 45f;                    // durée maximum en vol bas d'affilée
        const float DelaiEntreOrdres = 8f;           // délai minimum entre deux ordres sur le même hélico
        const float ProtectionScript = 60f;          // après un ordre d'altitude du script, on ne touche plus l'unité
        const float DebutCalme = 20f;                // rien pendant les premières secondes d'une bataille
        const float PresDestination = 400f;          // jamais si près de la destination d'un trajet du script
        const float HauteurMini = 12f;               // jamais sous cette hauteur (pose, débarquement, cargo)
        const float VerifBlocage = 15f;              // surveillance "hélico coincé" après une descente
        const float DistanceMini = 30f;              // distance minimum parcourue pendant cette surveillance
        const float PorteeParDefaut = 2500f;         // portée supposée d'un tireur sans missile infrarouge lisible (canon, DCA)
        const float MargePortee = 1.1f;              // marge avant de dire qu'un tireur est hors de portée
        const int MaxOrdresParHelico = 40, MaxOrdresParSeconde = 4, MaxErreurs = 20;
        const int MaxCanaris = 5;                    // essais non concluants avant de rester en mesure pour la bataille
        const int MaxTrajetsPerdus = 1;              // un seul hélico ayant perdu un trajet du script suffit à tout couper
        const int MaxHelis = 400, MaxLignes = 80, MaxNoms = 2000;
        const float ReportEvery = 30f, AttenteEvery = 30f;
        const int Ring = 256;                        // puissance de deux : départs de missiles gardés pour le fil principal
        const int ModeInconnu = 0, ModeHaut = 1, ModeBas = 2;
        const int FiltreAvions = 1;                  // AltitudeChangeUnitFilter : All 0, PlanesOnly 1, HelicoptersOnly 2

        /// Un hélicoptère suivi par le module (fil principal).
        sealed class Heli
        {
            public LuaUnit U;
            public int Uid, Eid, Side = -1, UnitId;
            public V3 Pos, PosDescente;
            public float Hauteur;
            public bool EnVol, HauteurConnue, PosOk;  // PosOk : la position de ce tour a vraiment été lue (sinon Pos est celle d'avant)
            public int Normal;                       // altitude à retrouver : ModeHaut / ModeBas / ModeInconnu
            public bool NormalDuScript;              // le mode à retrouver vient d'un ordre du script (prioritaire)
            public bool Bas, Canari;
            public float BasDepuis, DernierOrdre, DerniereMenace, ScriptAlt = -999f, Demande, ProchaineVerif, ProchaineRemontee;
            public bool TrajetDescente; public float ResteDescente;  // trajet du script au moment de la descente, et distance qui restait
            public int Touches; public float PremiereTouche;
            public int TireurUid = -1, TireurSide = -1;
            public int Ordres;
            public string Cause, Interdit;
        }

        static MelonPreferences_Entry<bool> _enabled, _joueur;
        static MelonPreferences_Entry<int> _unclean, _uncleanHook;
        static MelonPreferences_Entry<string> _guardVersion;
        static HarmonyLib.Harmony _harmony;

        static readonly Dictionary<int, Heli> _helis = new();
        static readonly Dictionary<int, Heli> _parEid = new();
        static readonly Dictionary<int, bool> _altOk = new();        // fiche d'unité : altitude réglable
        static readonly Dictionary<int, string> _noms = new();
        static readonly Dictionary<string, int> _refus = new(StringComparer.Ordinal);
        static readonly HashSet<int> _vus = new();
        static readonly List<int> _morts = new();
        // identifiants d'entité des hélicos que LE MOD tient en vol bas : les règles anti-hélico les lisent pour ne pas leur offrir
        // l'immunité du vol bas contre les missiles infrarouges (remplacée d'un bloc, donc lisible de n'importe quel fil sans verrou)
        static volatile HashSet<int> _basEids = new();

        static bool _battle, _sessionArmed, _refused, _agit, _mesureForcee, _patched, _hookRefuse;
        static bool _everOnline, _netLogged, _crashLisible = true, _notifie, _marqueurEcrit;
        static string _stopBataille;                                 // non nul : plus aucune descente de cette bataille (et pourquoi)
        static int _localUid = -1;
        static float _next, _debut, _nextReport, _nextAttente, _secondeDebut;
        static int _wait, _lignes, _ordresCeTick, _ordresSeconde, _basCount, _crashErreurs;
        static int _canariUid = -1, _canaris; static bool _canariFait;

        // compteurs de la bataille
        static int _menaces, _touches, _descentes, _remontees, _ordres, _erreurs, _interdits, _voudrait;
        static int _renduScript, _basPerdus, _scriptAlt, _scriptAltInconnu, _scriptAltAvant, _enVol, _trajetsPerdus;
        static int _remonteePlafond, _remonteeCalme, _remonteeTireur, _remonteesRefusees, _verifsReportees;
        static long _tirs, _tirsSurHelico, _tirsDuSol, _tirsAutres, _hookErreurs, _hookHorsFil, _tirsPerdus;
        static long _tirsDepuisHelico;               // missiles launched BY a tracked helicopter (read by the anti-helicopter range rules)

        // côté crochet : que des entiers
        static volatile bool _hookOn;
        static volatile int _mainThread;
        static readonly long[] _ring = new long[Ring];
        static readonly int[] _ringSeq = new int[Ring];   // numéro de la case, écrit APRÈS la donnée : le fil principal ne lit qu'une case complète
        static int _ringWrite, _ringRead;

        static void Log(string s) => Mod.Log.Msg("[ESQUIVE] " + s);
        static void Ligne(string s) { _lignes++; Log(s); }

        /// À tester AVANT de composer le texte d'une ligne : passé le plafond, rien n'est construit pour rien.
        static bool Lignes()
        {
            if (_lignes < MaxLignes) return true;
            if (_lignes == MaxLignes) { _lignes++; Log($"plus de {MaxLignes} lignes de vol bas : la suite est seulement comptée dans le relevé"); }
            return false;
        }

        /// Vrai tant qu'au moins un hélico est descendu par le mod (Mod.cs le lit pour tout rendre si le mod est coupé en pleine bataille).
        internal static bool ARendre => _basCount > 0;

        /// Vrai quand l'observation des départs de missiles tourne vraiment : sans elle, personne ne peut dire si les hélicoptères
        /// tirent encore, et le chien de garde de la portée des hélicos bas n'a rien à surveiller.
        internal static bool CompteurTirsHelicoPret => _hookOn;

        /// Nombre de missiles partis D'UN hélicoptère suivi depuis le début de la bataille (fil principal, vidage de l'anneau).
        internal static long TirsDepuisHelico => Interlocked.Read(ref _tirsDepuisHelico);

        /// Vrai quand c'est LE MOD qui tient cet hélico en vol bas. Lu par les règles anti-hélico dans un chemin chaud (n'importe quel
        /// fil) : aucune allocation, aucun appel au jeu. Un vol bas décidé par le mod ne doit jamais rendre l'hélico intouchable.
        internal static bool DescenduParLeMod(int eid)
        {
            var s = _basEids;
            return s.Count > 0 && s.Contains(eid);
        }

        /// Publie la liste des hélicos que le mod tient en bas. N'alloue rien tant qu'aucun hélico n'est descendu (le cas ordinaire).
        static void PublierBas()
        {
            if (_basCount <= 0) { if (_basEids.Count > 0) _basEids = new HashSet<int>(); return; }
            var s = new HashSet<int>();
            foreach (var kv in _helis) { var h = kv.Value; if (h.Bas && h.Eid != 0) s.Add(h.Eid); }
            _basEids = s;
        }

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Esquive");
            _enabled = c.CreateEntry("EsquiveHelicos", true, description: Build.Desc(
                "Les hélicoptères de l'IA (ennemis et alliés) descendent en vol bas quand un missile anti-aérien est tiré sur eux ou quand la DCA les touche, et remontent quand le calme revient. Les hélicos commandés par le script de mission ne sont jamais touchés.",
                "Hélicoptères de l'IA : vol bas sous le feu anti-aérien"));
            _joueur = c.CreateEntry("EsquiveJoueur", false, description: Build.Desc(
                "Faire descendre aussi TES propres hélicoptères (non par défaut : le mod ne déplace jamais tes unités tout seul, et il ne voit pas les ordres d'altitude que tu donnes toi-même)",
                "Vol bas aussi pour tes hélicoptères"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _uncleanHook = c.CreateEntry("ObservationTirsInterrompue", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; _uncleanHook.Value = 0; }   // une nouvelle version remesure
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        /// Le mod vient d'être coupé depuis son onglet en pleine bataille : on rend son altitude à tout le monde tout de suite.
        internal static void Disarm()
        {
            int n = Rendre("mod coupé");
            if (n > 0) Log($"mod coupé : {n} hélico(s) remis à leur altitude d'avant");
        }

        /// Fin de bataille : on n'appelle plus le jeu (les unités de la bataille précédente n'existent peut-être plus), on remet tout à zéro.
        static void EndBattle(string why)
        {
            _hookOn = false;
            if (!_battle && !_sessionArmed) { Oublier(); return; }
            if (_sessionArmed)
            {
                _sessionArmed = false;
                try { Report(true); } catch { }
                try
                {
                    bool save = _unclean.Value != 0 || _uncleanHook.Value != 0;
                    _unclean.Value = 0; _uncleanHook.Value = 0;
                    if (save) MelonPreferences.Save();
                }
                catch { }
                Log($"fin de bataille ({why})");
            }
            _battle = false;
            Oublier();
        }

        static void Oublier()
        {
            _helis.Clear(); _parEid.Clear(); _noms.Clear(); _refus.Clear(); _vus.Clear(); _morts.Clear();
            _agit = _mesureForcee = _marqueurEcrit = false;
            _stopBataille = null;
            _localUid = -1;
            _debut = _next = _nextReport = _nextAttente = _secondeDebut = 0f;
            _lignes = _ordresCeTick = _ordresSeconde = _basCount = 0;
            // _crashErreurs et _crashLisible restent pour toute la session de jeu (lecture de composant impossible = inutile de réessayer)
            _canariUid = -1; _canaris = 0; _canariFait = false;
            _menaces = _touches = _descentes = _remontees = _ordres = _erreurs = _interdits = _voudrait = 0;
            _renduScript = _basPerdus = _scriptAlt = _scriptAltInconnu = _scriptAltAvant = _enVol = _trajetsPerdus = 0;
            _remonteePlafond = _remonteeCalme = _remonteeTireur = _remonteesRefusees = _verifsReportees = 0;
            Interlocked.Exchange(ref _tirs, 0); Interlocked.Exchange(ref _tirsSurHelico, 0);
            Interlocked.Exchange(ref _tirsDuSol, 0); Interlocked.Exchange(ref _tirsAutres, 0);
            Interlocked.Exchange(ref _hookErreurs, 0); Interlocked.Exchange(ref _hookHorsFil, 0);
            Interlocked.Exchange(ref _tirsPerdus, 0); Interlocked.Exchange(ref _tirsDepuisHelico, 0);
            _ringRead = Volatile.Read(ref _ringWrite);
            if (_basEids.Count > 0) _basEids = new HashSet<int>();
        }

        // ---------------------------------------------------------------- boucle principale

        /// Appelée à chaque image en campagne ; travaille toutes les 0,25 s.
        internal static void Frame()
        {
            if (Campaign.MissionInerte) { if (_battle) EndBattle("mission sans le mod"); return; }
            if (_enabled == null || !_enabled.Value)
            {
                if (_basCount > 0) { int n = Rendre("option coupée"); Log($"option coupée : {n} hélico(s) remis à leur altitude d'avant"); }
                if (_battle) EndBattle("option coupée");
                return;
            }
            if (_refused) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            // un seul gros travail de module par image (Planif.cs) : le battement attend une image libre, l'armement de la bataille
            // peut attendre plus longtemps. Rien n'exige une image précise : la vue des hélicos reste bonne pendant 1 s.
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _next = now + Periode;
            try { Tick(now); }
            catch (Exception e) { Erreur("boucle du vol bas", e); }     // le compteur d'erreurs du module doit voir même celles-là
        }

        static void Tick(float now)
        {
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_battle || _sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_battle || _sessionArmed) { Rendre("partie en ligne"); EndBattle("partie en ligne"); } return; }
            try { _localUid = cp.UID; } catch { _localUid = -1; }
            if (_localUid < 0) _localUid = Campaign.LocalUid;

            if (!_battle)
            {
                _battle = true; _debut = now; _nextReport = now + ReportEvery; _nextAttente = now + AttenteEvery;
                _sessionArmed = true;
                if (_unclean.Value >= 2)
                {
                    _refused = true; _sessionArmed = false; _battle = false;
                    Mod.Log.Warning("[ESQUIVE] vol bas des hélicos désactivé : les deux dernières batailles où il agissait ne se sont pas terminées normalement");
                    Notifier();
                    return;
                }
                _mesureForcee = _unclean.Value >= 1;
                if (_uncleanHook.Value >= 2 && !_hookRefuse)
                {
                    _hookRefuse = true;
                    Log("observation du départ des missiles coupée par sécurité (les deux dernières batailles où elle était en place ne se sont pas terminées normalement) : les hélicos ne descendront que sur les impacts");
                }
                // le détour du départ des missiles est déjà en place depuis une bataille précédente : son marqueur repart pour celle-ci
                else if (_patched) { try { _uncleanHook.Value = _uncleanHook.Value + 1; MelonPreferences.Save(); } catch { } }
                Log($"vol bas des hélicos de l'IA : {(_mesureForcee ? "MESURE SEULE pour cette bataille (la bataille précédente ne s'est pas terminée normalement)" : "mesure d'abord, puis action dès que le canal d'altitude du script est écouté")}" +
                    $" ; descente sur tir de missile ou 2 impacts en {FenetreTouches:0} s, remontée après {MiniBas:0} s en bas et {CalmeSecondes:0} s de calme, {MaxBas:0} s en bas au maximum" +
                    $" ; tes propres hélicos {((_joueur != null && _joueur.Value) ? "concernés (option cochée)" : "jamais touchés")}");
            }

            if (!AntiHeliPortee.VueFraiche(now))
            {
                // les règles anti-hélico sont en pause (option décochée en pleine bataille, ou sécurité) : sans leur liste le module ne
                // peut plus rien suivre. On rend TOUT DE SUITE son altitude à chaque hélico descendu, sinon il resterait en bas pour la
                // bataille entière.
                if (_basCount > 0)
                {
                    int rendus = Rendre("liste des hélicos indisponible");
                    Log($"liste des hélicos indisponible (règles anti-hélico en pause) : {rendus} hélico(s) remis à leur altitude d'avant");
                }
                Refus("liste des hélicos indisponible (règles anti-hélico en pause)");
                return;
            }
            Sync(now);
            if (!_patched && !_hookRefuse && _helis.Count > 0) Patcher();
            _hookOn = _patched && !_hookRefuse && Interlocked.Read(ref _hookErreurs) <= MaxErreurs;
            DrainRing();
            Promotion(now);
            Decide(now);
            PublierBas();
            if (now >= _nextReport) { _nextReport = now + ReportEvery; try { Report(false); } catch { } }
        }

        static bool Solo()
        {
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _everOnline = true;   // retenu pour la bataille
                if (!_netLogged) { _netLogged = true; Log($"réseau : état={st} -> {(_everOnline ? "en ligne" : "solo")}"); }
            }
            catch { return false; }
            return !_everOnline;
        }

        /// Reprend la liste d'hélicos d'AntiHeliPortee (aucun balayage d'unités en plus) et oublie les morts.
        static void Sync(float now)
        {
            _vus.Clear(); _parEid.Clear(); _enVol = 0;
            int n = AntiHeliPortee.HeliCount;
            for (int i = 0; i < n; i++)
            {
                if (!AntiHeliPortee.HeliAt(i, out int uid, out int eid, out int side, out int unitId, out bool posOk, out bool enVol, out bool hk, out float hauteur, out V3 pos, out var u)) continue;
                if (u == null || uid <= 0 || side < 0 || side > 1) continue;
                if (!_helis.TryGetValue(uid, out var h))
                {
                    if (_helis.Count >= MaxHelis) continue;
                    h = new Heli { Uid = uid };
                    _helis[uid] = h;
                }
                h.U = u; h.Eid = eid; h.Side = side; h.UnitId = unitId;
                _vus.Add(uid);                                       // vivant même si sa position n'a pas pu être lue : on garde son état
                _parEid[eid] = h;
                if (!posOk) { h.EnVol = false; h.HauteurConnue = false; h.PosOk = false; continue; }   // position illisible : plus de descente, la remontée reste possible
                h.EnVol = enVol; h.HauteurConnue = hk; h.Hauteur = hauteur; h.Pos = pos; h.PosOk = true;
                if (enVol) _enVol++;
            }
            if (_helis.Count <= _vus.Count) return;
            _morts.Clear();
            foreach (var kv in _helis) if (!_vus.Contains(kv.Key)) _morts.Add(kv.Key);
            for (int i = 0; i < _morts.Count; i++)
            {
                if (!_helis.TryGetValue(_morts[i], out var h)) continue;
                if (h.Bas) { h.Bas = false; if (_basCount > 0) _basCount--; _basPerdus++; }
                if (h.Canari && _canariUid == h.Uid) Canari(h, 0, "l'hélico de l'essai a été détruit avant la vérification");
                _helis.Remove(_morts[i]);
            }
        }

        /// Mesure -> action, dans la même bataille : dès que le canal d'altitude du script est écouté et que les unités du script sont sûres.
        static void Promotion(float now)
        {
            if (_agit || _mesureForcee || _refused || _stopBataille != null) return;
            if (now - _debut < DebutCalme) return;
            string pas = !Missions.AltitudeHookReady ? "les ordres d'altitude du script ne sont pas encore écoutés" : Missions.ProtectionNotReady();
            if (pas != null)
            {
                if (now >= _nextAttente) { _nextAttente = now + AttenteEvery; Log("mesure seule pour le moment : " + pas); }
                return;
            }
            _agit = true;
            MarqueurBataille();
            Log($"les descentes sont permises à partir de maintenant : le détour du canal d'altitude du script est en place (on ne saura qu'il sert vraiment que si la mission s'en sert)" +
                $" et les unités du script sont identifiées ; aucun hélico tenu par le script ne sera touché" +
                $" ; le tout premier hélico descendu sert d'essai, aucun autre ne descendra avant sa vérification ({VerifBlocage:0} s)");
        }

        /// Marqueur de bataille non terminée, écrit sur le disque AVANT le premier ordre au jeu.
        static void MarqueurBataille()
        {
            if (_marqueurEcrit) return;
            _marqueurEcrit = true;
            try { _unclean.Value = _unclean.Value + 1; MelonPreferences.Save(); } catch { }
        }

        static void Decide(float now)
        {
            _ordresCeTick = 0;
            foreach (var kv in _helis)
            {
                var h = kv.Value;
                try
                {
                    bool vivant = false;
                    try { vivant = h.U.IsAlive(); } catch { vivant = false; }
                    if (!vivant) continue;
                    if (h.Bas) EnBas(h, now);
                    else if (h.Demande > 0f)
                    {
                        if (now - h.Demande > FenetreDemande) { h.Demande = 0f; continue; }
                        Descendre(h, now);
                    }
                }
                catch (Exception e) { Erreur("règles de vol bas", e); }
            }
        }

        // ---------------------------------------------------------------- descente

        static void Descendre(Heli h, float now)
        {
            string non = Empeche(h, now);
            if (non != null) { Refus(non); return; }
            // altitude à retrouver : celle que le script a demandée en dernier, sinon celle que sa hauteur mesurée indique
            int normal = h.NormalDuScript && h.Normal != ModeInconnu ? h.Normal
                       : h.HauteurConnue && h.Hauteur < AntiHeliPortee.SeuilVolBas ? ModeBas : ModeHaut;
            if (normal == ModeBas) { h.Demande = 0f; Refus("déjà en vol bas"); return; }
            _voudrait++;
            if (!_agit)
            {
                h.Demande = 0f;
                if (Lignes()) Ligne($"mesure : {Nom(h)} (uid {h.Uid}, camp {h.Side}) serait descendu en vol bas ({h.Cause}), il est à {h.Hauteur:0} m du sol");
                return;
            }
            bool essai = !_canariFait;
            h.Demande = 0f;
            if (!Appeler(h, ModeBas, now)) return;
            h.Normal = normal;
            h.Bas = true; _basCount++; _descentes++;
            h.BasDepuis = now; h.PosDescente = h.Pos; h.ProchaineVerif = now + VerifBlocage;
            h.TrajetDescente = Missions.ScriptRouteFar(h.Uid, out h.ResteDescente);   // pour vérifier ensuite que l'ordre d'altitude n'a rien annulé
            if (essai) { _canariUid = h.Uid; h.Canari = true; }
            if (Lignes()) Ligne($"{Nom(h)} (uid {h.Uid}, camp {h.Side}) descend en vol bas ({h.Cause}), il était à {h.Hauteur:0} m du sol" +
                (essai ? $" ; c'est l'ESSAI : aucun autre hélico ne descendra avant sa vérification dans {VerifBlocage:0} s" : ""));
        }

        /// Pourquoi cet hélico ne doit pas descendre (texte pour le relevé), ou null quand tout est en règle.
        static string Empeche(Heli h, float now)
        {
            if (_stopBataille != null) return _stopBataille;
            if (h.Interdit != null) return h.Interdit;
            if (now - _debut < DebutCalme) return "tout début de bataille";
            if (!h.EnVol) return "hélico posé";
            if (!h.HauteurConnue) return "hauteur du sol illisible";
            if (h.Hauteur < HauteurMini) return "déjà très bas (pose, débarquement ou cargo)";
            if (_localUid < 0) return "joueur local inconnu";
            int owner;
            try { owner = h.U.GetOwnerPlayerUID(); } catch { return "propriétaire de l'hélico illisible"; }
            if (owner == _localUid && !(_joueur != null && _joueur.Value)) return "hélico à toi (option non cochée)";
            if (!AltitudeReglable(h.UnitId)) return "la fiche de l'unité ne permet pas de changer d'altitude";
            if (Ecrase(h)) return "hélico en train de s'écraser";
            if (now - h.ScriptAlt < ProtectionScript) return "altitude commandée par le script de mission";
            // le script de mission passe TOUJOURS avant : trajet en cours, ordre récent, apparition, groupe cité ou commandé.
            // C'est le même garde-fou que l'IA ennemie du mod (EnemyAi) et c'est ce que promet le texte du réglage.
            try { if (Missions.ScriptReason(h.Uid, UnityEngine.Time.time) != null) return "hélico tenu par le script de mission"; }
            catch { return "état du script de mission illisible"; }
            if (Missions.ScriptRouteFar(h.Uid, out float d) && d < PresDestination) return "trop près de la destination de son trajet du script";
            if (!Missions.AltitudeHookReady) return "les ordres d'altitude du script ne sont pas écoutés";
            string pret = Missions.ProtectionNotReady();
            if (pret != null) return "unités du script pas encore sûres";
            if (now - h.DernierOrdre < DelaiEntreOrdres) return "ordre trop récent sur cet hélico";
            if (h.Ordres >= MaxOrdresParHelico) { h.Interdit ??= "trop d'ordres sur cet hélico dans cette bataille"; _interdits++; return h.Interdit; }
            if (!_agit) return null;                                   // en mesure, les limites de débit n'ont pas de sens
            if (_canariUid >= 0 && _canariUid != h.Uid) return "essai en cours sur un autre hélico";
            if (!_canariFait && _canaris >= MaxCanaris) return "aucun essai concluant dans cette bataille";
            if (_ordresCeTick >= 1) return "un autre hélico vient de recevoir un ordre";
            if (TropVite(now)) return "trop d'ordres à la seconde";
            return null;
        }

        // ---------------------------------------------------------------- hélico en vol bas : surveillance et remontée

        static void EnBas(Heli h, float now)
        {
            // le script reprend la main : on lâche l'hélico sans lui donner le moindre ordre. Ce n'est pas seulement l'altitude :
            // un trajet, un ordre récent ou un groupe commandé comptent autant. Sans ce contrôle, la remontée partait jusqu'à 45 s
            // après que le script avait repris l'appareil en main, et l'ordre d'altitude pouvait effacer l'ordre en cours.
            string rendu = null;
            if (now - h.ScriptAlt < ProtectionScript) rendu = "le script commande son altitude";
            else
            {
                try { if (Missions.ScriptReason(h.Uid, UnityEngine.Time.time) != null) rendu = "le script a repris cet hélico en main"; }
                catch { rendu = "état du script de mission illisible"; }
            }
            if (rendu != null)
            {
                h.Bas = false; if (_basCount > 0) _basCount--;
                _renduScript++;
                if (h.Canari && _canariUid == h.Uid) Canari(h, 0, "le script a repris la main sur l'hélico de l'essai");
                if (Lignes()) Ligne($"{Nom(h)} (uid {h.Uid}) : {rendu}, le mod le lâche sans rien changer");
                return;
            }

            // chien de garde : l'hélico descendu a-t-il perdu son trajet du script, ou cessé d'avancer ?
            if (h.ProchaineVerif > 0f && now >= h.ProchaineVerif)
            {
                // position pas relue ce tour-ci : la distance parcourue vaudrait 0 par construction et accuserait un hélico en bonne santé
                if (!h.PosOk) { h.ProchaineVerif = now + VerifBlocage; _verifsReportees++; return; }
                h.ProchaineVerif = 0f;
                float bouge = Plat(h.Pos, h.PosDescente);
                bool trajet = Missions.ScriptRouteFar(h.Uid, out _);
                // le pire des cas : l'ordre d'altitude aurait annulé le trajet que le script lui avait donné. Un trajet qui se termine
                // normalement (arrivée) disparaît aussi de la liste : il n'y a perte que s'il restait plus de chemin qu'il n'en a fait.
                bool perdu = h.TrajetDescente && !trajet && h.ResteDescente > bouge + PresDestination;
                if (perdu || (bouge < DistanceMini && trajet))
                {
                    bool essai = h.Canari && _canariUid == h.Uid;
                    h.Canari = false; if (essai) _canariUid = -1;
                    string mal = perdu
                        ? $"il a PERDU le trajet du script qu'il suivait ({h.ResteDescente:0} m restaient à parcourir au moment de la descente)"
                        : $"il n'a parcouru que {bouge:0} m en {VerifBlocage:0} s alors qu'il suit un trajet du script";
                    if (h.Interdit == null) { h.Interdit = perdu ? "chien de garde : trajet du script perdu après une descente" : "chien de garde : hélico resté coincé après une descente"; _interdits++; }
                    Mod.Log.Warning($"[ESQUIVE] chien de garde : {Nom(h)} (uid {h.Uid}) : {mal} : il remonte et le mod ne le touche plus de la bataille");
                    Remonter(h, now, perdu ? "chien de garde : trajet du script perdu" : "chien de garde : il n'avance plus");
                    if (perdu) _trajetsPerdus++;
                    if (essai) Arreter("l'essai a mal tourné : " + mal);
                    else if (perdu && _trajetsPerdus >= MaxTrajetsPerdus) Arreter($"{_trajetsPerdus} hélicos ont perdu leur trajet du script après une descente");
                    return;
                }
                if (h.Canari && _canariUid == h.Uid)
                {
                    if (h.TrajetDescente) Canari(h, 1, $"l'hélico de l'essai a gardé son trajet du script et parcouru {bouge:0} m");
                    else if (bouge >= DistanceMini) Canari(h, 1, $"l'hélico de l'essai a parcouru {bouge:0} m en vol bas");
                    else Canari(h, 0, $"l'hélico de l'essai n'a bougé que de {bouge:0} m et n'avait aucun trajet du script : on ne peut rien conclure");
                }
            }

            float bas = now - h.BasDepuis;
            string pourquoi = null; int genre = 0;
            if (bas >= MaxBas) { pourquoi = $"durée maximum atteinte ({MaxBas:0} s en vol bas)"; genre = 1; }
            else if (bas >= MiniBas)
            {
                if (now - h.DerniereMenace >= CalmeTireur && TireurParti(h, out string txt)) { pourquoi = txt; genre = 3; }
                else if (now - h.DerniereMenace >= CalmeSecondes && !AntiHeliPortee.ThreatNear(h.Pos, h.Side, RayonMenace, out _))
                { pourquoi = $"plus un tir depuis {now - h.DerniereMenace:0} s et plus aucun tireur anti-aérien à {RayonMenace:0} m"; genre = 2; }
            }
            if (pourquoi == null) return;
            if (genre == 1) _remonteePlafond++; else if (genre == 2) _remonteeCalme++; else if (genre == 3) _remonteeTireur++;
            Remonter(h, now, pourquoi);
        }

        /// Le tireur qui l'a fait descendre est détruit ou repassé hors de sa propre portée (sinon un hélico à 8 m devient intouchable).
        static bool TireurParti(Heli h, out string pourquoi)
        {
            pourquoi = null;
            if (h.TireurUid <= 0 || h.TireurSide < 0) return false;
            if (!AntiHeliPortee.ShooterState(h.TireurUid, h.TireurSide, out bool vivant, out V3 pos, out float portee))
            { pourquoi = "le tireur qui l'avait visé n'est plus là"; return true; }
            if (!vivant) { pourquoi = "le tireur qui l'avait visé est détruit"; return true; }
            float p = portee > 0f ? portee : PorteeParDefaut;
            float d = Plat(pos, h.Pos);
            if (d <= p * MargePortee) return false;
            pourquoi = $"le tireur qui l'avait visé est à {d:0} m, au-delà de sa portée ({p:0} m)";
            return true;
        }

        static void Remonter(Heli h, float now, string pourquoi)
        {
            // une remontée que le jeu vient de refuser n'est pas réessayée tout de suite (l'hélico est resté en bas, il est encore suivi)
            if (h.ProchaineRemontee > 0f && now < h.ProchaineRemontee) return;
            float bas = now - h.BasDepuis, bouge = Plat(h.Pos, h.PosDescente);
            int mode = h.Normal == ModeBas ? ModeBas : ModeHaut;
            bool essai = h.Canari && _canariUid == h.Uid;
            // l'ordre part AVANT qu'on efface son état : si le jeu le refuse, l'hélico est toujours en bas et le mod doit le savoir
            bool ok = Appeler(h, mode, now);
            if (!ok)
            {
                h.ProchaineRemontee = now + DelaiEntreOrdres;
                _remonteesRefusees++;
                if (Lignes()) Ligne($"{Nom(h)} (uid {h.Uid}) : remontée refusée par le jeu ({pourquoi}), il reste en vol bas et le mod réessaiera");
                return;
            }
            h.Bas = false; h.Canari = false; h.ProchaineVerif = 0f; h.ProchaineRemontee = 0f;
            if (_basCount > 0) _basCount--;
            _remontees++;
            if (Lignes()) Ligne($"{Nom(h)} (uid {h.Uid}, camp {h.Side}) remonte en {(mode == ModeBas ? "vol bas" : "vol haut")}{(h.NormalDuScript ? " (altitude demandée par le script)" : "")} après {bas:0} s en vol bas ({pourquoi})");
            // l'hélico de l'essai remonte avant sa vérification : on juge sur ce qu'il a parcouru, jamais de coupure ici
            if (essai) Canari(h, bouge >= DistanceMini ? 1 : 0, bouge >= DistanceMini
                ? $"l'hélico de l'essai a parcouru {bouge:0} m avant de remonter"
                : $"l'hélico de l'essai remonte après seulement {bouge:0} m");
        }

        // ---------------------------------------------------------------- l'ordre au jeu

        /// Le seul appel du module au jeu : l'altitude de CET hélico, sans son et sans rien d'autre.
        static bool Appeler(Heli h, int mode, float now)
        {
            try
            {
                var e = h.U.Entity;
                Missions.OwnOrderBegin();
                try { AltSys.ChangeAltitude(ref e, new Il2CppSystem.Nullable<FlyMode>(mode == ModeBas ? FlyMode.LowAltitude : FlyMode.HighAltitude), false); }
                finally { Missions.OwnOrderEnd(); }
            }
            catch (Exception ex)
            {
                if (h.Interdit == null) { h.Interdit = "le jeu a refusé le changement d'altitude"; _interdits++; }
                Erreur("changement d'altitude", ex);
                return false;
            }
            h.DernierOrdre = now; h.Ordres++; _ordres++; _ordresCeTick++;
            NoterSeconde(now);
            return true;
        }

        static bool TropVite(float now) => now - _secondeDebut < 1f && _ordresSeconde >= MaxOrdresParSeconde;

        static void NoterSeconde(float now)
        {
            if (now - _secondeDebut >= 1f) { _secondeDebut = now; _ordresSeconde = 0; }
            _ordresSeconde++;
        }

        /// Remet à leur altitude d'avant tous les hélicos encore descendus (bataille en cours seulement). Rend le nombre d'hélicos rendus.
        static int Rendre(string pourquoi)
        {
            int n = 0;
            float now = 0f;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { }
            foreach (var kv in _helis)
            {
                var h = kv.Value;
                if (!h.Bas) continue;
                h.Bas = false; h.Canari = false; h.ProchaineVerif = 0f; h.ProchaineRemontee = 0f;
                if (_basCount > 0) _basCount--;
                bool vivant = false;
                try { vivant = h.U != null && h.U.IsAlive(); } catch { vivant = false; }
                if (!vivant) continue;
                if (Appeler(h, h.Normal == ModeBas ? ModeBas : ModeHaut, now)) n++;
            }
            _canariUid = -1;
            if (n > 0) _remontees += n;
            PublierBas();                                            // plus personne n'est tenu en bas par le mod
            return n;
        }

        // ---------------------------------------------------------------- essai du premier hélico (canari)

        /// Verdict de l'essai : 1 concluant (les autres hélicos peuvent descendre), 0 sans conclusion (un autre hélico servira d'essai).
        /// Un essai qui tourne mal ne passe pas par ici : il coupe tout de suite le vol bas de la bataille (Arreter).
        static void Canari(Heli h, int verdict, string pourquoi)
        {
            h.Canari = false;
            _canariUid = -1;
            if (verdict > 0)
            {
                _canariFait = true;
                Log($"essai concluant ({pourquoi}) : les autres hélicos de l'IA peuvent descendre à leur tour");
                return;
            }
            _canaris++;
            if (_canaris >= MaxCanaris)
            {
                _stopBataille = $"aucun essai concluant après {MaxCanaris} tentatives";
                Log($"{pourquoi} : {MaxCanaris} essais sans conclusion, plus aucune descente pour cette bataille");
                return;
            }
            Log($"essai sans conclusion ({pourquoi}) : le prochain hélico menacé servira d'essai ({_canaris}/{MaxCanaris})");
        }

        // ---------------------------------------------------------------- menaces

        /// Appelé par AntiHeliTouches (fil principal) à chaque impact sur un hélico. tireurUid/tireurSide = -1 quand aucun tireur n'a été retenu.
        internal static void NoteHit(int heliUid, int heliSide, int ammoId, int tireurUid, int tireurSide)
        {
            if (Campaign.MissionInerte || !_battle) return;
            try
            {
                if (!_helis.TryGetValue(heliUid, out var h)) return;
                float now = UnityEngine.Time.realtimeSinceStartup;
                _touches++;
                bool aa = AntiHeliPortee.MunitionAntiAerienne(ammoId);
                if (!aa)
                {
                    if (now - h.PremiereTouche > FenetreTouches) { h.PremiereTouche = now; h.Touches = 1; h.DerniereMenace = now; return; }
                    h.Touches++;
                    if (h.Touches < 2) { h.DerniereMenace = now; return; }
                }
                Menace(h, now, tireurUid, tireurSide, aa ? "touché par une munition anti-aérienne" : $"deux impacts en moins de {FenetreTouches:0} s");
            }
            catch (Exception e) { Erreur("impact sur un hélico", e); }
        }

        static void Menace(Heli h, float now, int tireurUid, int tireurSide, string cause)
        {
            h.DerniereMenace = now;
            h.Demande = now;
            h.Cause = cause;
            if (tireurUid > 0 && (tireurSide == 0 || tireurSide == 1)) { h.TireurUid = tireurUid; h.TireurSide = tireurSide; }
            _menaces++;
        }

        /// Appelé par Missions (fil principal) quand le script de mission commande une altitude : son intention passe toujours avant la nôtre.
        internal static void NoteScriptAltitude(int[] uids, int single, List<int> membres, bool helicosEnHaut, bool bascule, int filtre, bool lisible)
        {
            if (Campaign.MissionInerte) return;
            // l'ordre est arrivé avant le premier battement du module (début de bataille) : rien à marquer, mais on le compte
            if (!_battle) { _scriptAltAvant++; return; }
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                _scriptAlt++;
                if (uids != null) for (int i = 0; i < uids.Length; i++) Marquer(uids[i], now, helicosEnHaut, bascule, filtre, lisible);
                Marquer(single, now, helicosEnHaut, bascule, filtre, lisible);
                if (membres != null) for (int i = 0; i < membres.Count; i++) Marquer(membres[i], now, helicosEnHaut, bascule, filtre, lisible);
            }
            catch (Exception e) { Erreur("ordre d'altitude du script", e); }
        }

        static void Marquer(int uid, float now, bool haut, bool bascule, int filtre, bool lisible)
        {
            if (uid <= 0) return;
            // ordre d'altitude qui ne vise que les AVIONS : il ne touche aucun hélico, donc le mod n'a rien à en déduire ni à lâcher
            if (filtre == FiltreAvions) return;
            if (!_helis.TryGetValue(uid, out var h)) { _scriptAltInconnu++; return; }
            h.ScriptAlt = now;
            if (bascule || !lisible)
            {
                // le script inverse l'altitude (ou son ordre n'a pas pu être lu en entier) : impossible de savoir dans quel mode il le laisse
                if (h.Interdit == null) { h.Interdit = bascule ? "le script bascule son altitude : mode d'origine inconnu" : "ordre d'altitude du script illisible : mode d'origine inconnu"; _interdits++; }
                if (h.Bas) { h.Bas = false; if (_basCount > 0) _basCount--; _renduScript++; }
                if (h.Canari && _canariUid == h.Uid) Canari(h, 0, "le script a repris l'altitude de l'hélico de l'essai");
                return;
            }
            h.Normal = haut ? ModeHaut : ModeBas;
            h.NormalDuScript = true;
        }

        // ---------------------------------------------------------------- départ des missiles (le seul crochet du module)

        static void Patcher()
        {
            // garde anti-plantage propre au détour : deux batailles de suite mal terminées avec lui en place et on ne l'installe plus
            if (_uncleanHook.Value >= 2)
            {
                _hookRefuse = true;
                Log("observation du départ des missiles abandonnée par sécurité (les deux dernières batailles où elle était en place ne se sont pas terminées normalement) : les hélicos ne descendront que sur les impacts");
                return;
            }
            try
            {
                var m = AccessTools.Method(typeof(Spawner), "SpawnMissile");
                if (m == null)
                {
                    _hookRefuse = true;
                    Log("départ des missiles introuvable dans cette version du jeu : les hélicos ne descendront que sur les impacts");
                    return;
                }
                try { _uncleanHook.Value = _uncleanHook.Value + 1; MelonPreferences.Save(); } catch { }   // sur le disque AVANT la pose du détour
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Esquive");
                _harmony.Patch(m, postfix: new HarmonyMethod(typeof(Esquive).GetMethod(nameof(MissilePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
                Log("départ des missiles observé (tireur et cible seulement, aucun changement de comportement)");
            }
            catch (Exception e)
            {
                _hookRefuse = true;
                Log("observation du départ des missiles non installée (" + e.GetBaseException().Message + ") : les hélicos ne descendront que sur les impacts");
            }
        }

        /// static Entity SpawnMissile(ShellSpawnData data, Entity& shooterEntity, Entity& targetEntity)
        /// Chemin chaud, n'importe quel fil : deux identifiants rangés dans un tableau fixe, rien d'autre (aucune allocation, aucun appel
        /// au jeu, aucune écriture dans le journal). Le détour reste en place à vie : en cas d'erreurs il devient un simple passe-plat.
        static void MissilePostfix(ref EcsEntity shooterEntity, ref EcsEntity targetEntity)
        {
            if (Campaign.MissionInerte || !_hookOn) return;
            try
            {
                Interlocked.Increment(ref _tirs);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _hookHorsFil);
                int n = Interlocked.Increment(ref _ringWrite);
                int w = n & (Ring - 1);
                _ringSeq[w] = 0;                                        // case en cours d'écriture
                _ring[w] = ((long)shooterEntity.EntityId << 32) | (uint)targetEntity.EntityId;
                Volatile.Write(ref _ringSeq[w], n);                     // écrit en dernier : la case est complète
            }
            catch { if (Interlocked.Increment(ref _hookErreurs) > MaxErreurs) _hookOn = false; }
        }

        /// Fil principal : les départs de missiles gardés deviennent des menaces sur nos hélicos.
        static void DrainRing()
        {
            int end = Volatile.Read(ref _ringWrite);
            int start = end - _ringRead > Ring ? end - Ring : _ringRead;
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int k = start + 1; k <= end; k++)
            {
                try
                {
                    int w = k & (Ring - 1);
                    if (Volatile.Read(ref _ringSeq[w]) != k) { Interlocked.Increment(ref _tirsPerdus); continue; }   // case pas encore complète, ou déjà réécrite
                    long v = _ring[w];
                    int tireurEid = (int)(v >> 32), cibleEid = (int)(v & 0xFFFFFFFFL);
                    // un missile parti D'UN hélico suivi : c'est le seul compteur honnête de « les hélicos tirent encore », et c'est lui
                    // que le chien de garde de la portée des hélicos bas (AntiHeliPortee) attend avant de se permettre de plafonner
                    if (_parEid.ContainsKey(tireurEid)) Interlocked.Increment(ref _tirsDepuisHelico);
                    if (!_parEid.TryGetValue(cibleEid, out var h)) continue;
                    Interlocked.Increment(ref _tirsSurHelico);
                    if (!AntiHeliPortee.GroundByEid(tireurEid, out int tireurUid, out int tireurSide, out _) || tireurSide == h.Side)
                    { Interlocked.Increment(ref _tirsAutres); continue; }
                    Interlocked.Increment(ref _tirsDuSol);
                    Menace(h, now, tireurUid, tireurSide, "un missile anti-aérien vient d'être tiré sur lui");
                }
                catch (Exception e) { Erreur("départ de missile", e); }
            }
            _ringRead = end;
        }

        // ---------------------------------------------------------------- petits services

        static bool AltitudeReglable(int unitId)
        {
            if (unitId <= 0) return false;
            if (_altOk.TryGetValue(unitId, out bool v)) return v;
            try
            {
                var src = DbService._instance?.RawAccess;
                if (src == null) return false;
                UnitsRow row = null;
                if (!src.Units.TryGetById(unitId, out row) || row == null) return false;
                var m = row.CurrentMobility;
                if (m == null) return false;
                v = m.IsChangeAltitude;
            }
            catch { return false; }
            if (_altOk.Count < 4000) _altOk[unitId] = v;
            return v;
        }

        /// Hélico en train de s'écraser : on ne lui donne évidemment aucun ordre (seul Has<T> est employé, jamais Get<T>).
        static bool Ecrase(Heli h)
        {
            if (!_crashLisible) return false;
            try { return h.U.Entity.Has<CrashComp>(); }
            catch (Exception e)
            {
                if (++_crashErreurs < 5) return true;               // lecture ratée sur cet hélico : on le laisse tranquille cette fois
                _crashLisible = false;
                Log("état d'écrasement des hélicos illisible (" + e.GetBaseException().Message + ") : ce contrôle est abandonné");
                return false;
            }
        }

        static float Plat(V3 a, V3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        static string Nom(Heli h)
        {
            if (_noms.TryGetValue(h.Uid, out string n)) return n;
            n = "hélico";
            try { n = h.U.Name ?? "hélico"; } catch { }
            if (_noms.Count < MaxNoms) _noms[h.Uid] = n;
            return n;
        }

        static void Refus(string pourquoi)
        {
            if (_refus.Count >= 40 && !_refus.ContainsKey(pourquoi)) return;
            _refus[pourquoi] = (_refus.TryGetValue(pourquoi, out int n) ? n : 0) + 1;
        }

        static void Erreur(string quoi, Exception e)
        {
            _erreurs++;
            if (_erreurs <= 3) Log($"{quoi} : erreur ({e.GetBaseException().Message})");
            if (_erreurs > MaxErreurs && _stopBataille == null) Arreter($"trop d'erreurs ({_erreurs})");
        }

        /// Coupure du vol bas pour le reste de la bataille : tout le monde retrouve son altitude d'avant.
        static void Arreter(string pourquoi)
        {
            _stopBataille = pourquoi;
            _hookOn = false;
            int n = Rendre(pourquoi);
            Mod.Log.Warning($"[ESQUIVE] vol bas coupé pour cette bataille ({pourquoi}) ; {n} hélico(s) remis à leur altitude d'avant");
            Notifier();
        }

        static void Notifier()
        {
            if (_notifie) return;
            _notifie = true;
            try { Mod.Notify(TxtKey.N_ESQUIVE_OFF); } catch { }
        }

        // ---------------------------------------------------------------- relevé

        static void Report(bool final)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            string etat = _refused ? "désactivé par sécurité"
                : _stopBataille != null ? "coupé pour cette bataille (" + _stopBataille + ")"
                : _agit ? (_canariFait ? "ACTIF" : _canariUid >= 0 ? "essai en cours sur un hélico" : "actif, en attente d'un premier hélico à descendre")
                : _mesureForcee ? "mesure seule (bataille précédente interrompue)" : "mesure seule (en attente du script)";
            Log($"{(final ? "bilan" : "relevé")} t={now - _debut:0}s : {etat} ; hélicos suivis {_helis.Count} (en vol {_enVol}, descendus maintenant {_basCount})" +
                $" ; départs de missiles vus {Interlocked.Read(ref _tirs)} (sur un de nos hélicos {Interlocked.Read(ref _tirsSurHelico)}, tirés du sol d'en face {Interlocked.Read(ref _tirsDuSol)}, tirés PAR un hélico {Interlocked.Read(ref _tirsDepuisHelico)}, autres {Interlocked.Read(ref _tirsAutres)}, hors fil principal {Interlocked.Read(ref _hookHorsFil)}, cases incomplètes {Interlocked.Read(ref _tirsPerdus)}, erreurs {Interlocked.Read(ref _hookErreurs)})" +
                $" ; impacts anti-aériens vus {_touches}, menaces retenues {_menaces}" +
                $" ; descentes {(_agit ? _descentes.ToString() : "0 (mesure)")} sur {_voudrait} voulue(s), remontées {_remontees} (plafond {_remonteePlafond}, calme {_remonteeCalme}, tireur parti {_remonteeTireur}, refusées par le jeu {_remonteesRefusees})" +
                $" ; ordres envoyés au jeu {_ordres}, hélicos écartés {_interdits}, lâchés au script {_renduScript}, trajets du script perdus après une descente {_trajetsPerdus}, détruits en vol bas {_basPerdus}, vérifications reportées faute de position {_verifsReportees}" +
                $" ; ordres d'altitude du script vus {_scriptAlt} (sur des unités inconnues {_scriptAltInconnu}, arrivés avant le démarrage du module {_scriptAltAvant}) ; erreurs {_erreurs}");
            if (_refus.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _refus) { if (sb.Length > 0) sb.Append(" ; "); sb.Append(kv.Key).Append(" x").Append(kv.Value); }
                Log($"{(final ? "bilan" : "relevé")} des refus : {sb}");
            }
            if (!final) _refus.Clear();
        }
    }
}
