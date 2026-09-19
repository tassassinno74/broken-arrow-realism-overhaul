// RealismOverhaul - R7 [VUE-SOL] own line of sight between GROUND units (design of 2026-09-19), identical for both sides.
//  Player request 2026-09-19: "les unités voient de loin à travers 10 forêts, cela va pas". Ground units shoot each other across the
//  map as if it were flat and empty. VueAA already answers "masked or clear" for a shooter and a target, with its own copy of the map
//  (25 m trees over the forest pixels, building tops calibrated against the ground around them, terrain height); until today only
//  HELICOPTER targets were ever asked. This module asks the same question for a ground target and, when the answer is "masked", zeroes
//  the range of a direct-fire gun round through the hook AntiHeliPortee already owns. Nothing else is invented: the map, the eye cache
//  and the line walk are VueAA's, unchanged. No hook of its own, no unpatch, ever.
//
//  WHAT IT COSTS, and why the shape below. Measured in the battle of 2026-09-19 (Latest.log, 03:27:45 -> 04:09:14, about 2489 s):
//  GetAmmoShootingDistance was called 2 100 973 times, 408 822 of them on a helicopter, so 1 692 151 calls are ground-to-ground -
//  11.3 per frame at the measured 59.9 images per second, and not one of them off the main thread. Eleven extra hash lookups an image
//  is nothing; the danger is on the PRODUCER side, where the neighbouring module showed two spikes of 33.85 ms and 60.70 ms in that
//  same battle. So every step that builds a verdict is capped three ways (per cycle, per image and on the clock), is resumable across
//  images, and gives the image back to the anti-air pass whenever that one already worked in it. A line walk costs 1.25 us (VueAA
//  measured 159 188 walks for 0.2 s of line time), and the longest single walk of that battle was 1.130 ms.
//
//  THE LADDER, cheapest first, every step fail-open (no answer = no cut):
//   0. Actif, one volatile bool. While the module is off this is the whole cost.
//   1. both entity ids in AntiHeliPortee's ground set (tested by the hook, which already holds it).
//   2. the eligible-shooter set, published here, empty unless the pass is producing, 60 shooters a side at most.
//   3. the ammunition table CanonsSol: direct fire, no seeker, a ground target bit.
//   4. freshness of the published verdicts (1.5 s).
//   5. the target is not being hit right now (a unit taking fire is visibly in the open).
//   6. the verdict itself.
//
//  WHAT CAN NEVER BE CUT. One enum test does most of the work: requiring TrajectoryType == DirectShot leaves out artillery, howitzers,
//  mortars, MLRS and bombs - fire that needs no line of sight at all, and where getting this wrong would silence every gun on the map -
//  and EVERY missile (PursuitMissile, LeadMissile, LaserMissile, Cruise, Ballistic...), so ATGMs, MANPADS and SAMs keep their ranges
//  and no guided round in flight can be touched. Seeker == None leaves out anything that guides itself. The four exclusion sets
//  AntiHeliPortee already builds take out dedicated anti-air rows, infrared missiles, RPGs, rifles and marksman rifles. A round that
//  carries no ground target bit at all (smoke, illumination) is out by construction. Then: nothing under the floor, nothing at a
//  target that is being hit right now, no ship at either end, no aircraft, and never a pair without a fresh verdict.
//
//  THE FLOOR, and why a pair is queued well above it. The promise is 800 m for a vehicle and 1000 m for an infantry team: below that,
//  nothing may ever be cut. 800 m is above every infantry small arm of the real-stats tables (rifles 400 m, marksman rifles 500 m,
//  7.62 mm machine guns 800 m) and far above every RPG (200 m or 300 m); 1000 m is above the 12.7 mm group. It is also where the model
//  is honest: map pixels are 3 x 3 m and the shooter's own pixel and the target's own pixel are ignored by design, so at 300 m that is
//  2 pixels out of 100 (a hedge right in front is exactly what the model does not see) and at 800 m 2 out of 267.
//  A verdict, though, is never fresh: the positions come from AntiHeliPortee.RefreshPositions, refreshed at most once a second; the
//  cycle that uses them may take up to 3 s of images before it publishes; and the published verdict then lives ValidMs longer. About
//  5.5 s in the worst case, during which two vehicles driving at each other at 100 km/h each close 320 m. So the pair is not queued at
//  the promised floor but at the floor PLUS that 320 m - 1120 m for a vehicle, 1320 m for an infantry team - and the promise becomes
//  true instead of nearly true: a verdict that is as stale as the design allows still cannot cut a shot at 800 m.
//
//  THE CACHE. Terrain is static, so an answer is kept per 12 m cell (4 map pixels) and per half-metre of eye height, for 20 s, 40 000
//  entries, cleared whole on overflow and whenever VueAA copies the map again (a destroyed building). One cached answer in 50 is
//  recomputed exactly and compared, and the share of disagreements is printed: above 5 % over 2000 controls the CACHE is switched off
//  (exact walks only, the rule keeps working) and the rule may not cut until it is back under.
//
//  STAGING. Stage 0 measures and cuts NOTHING; the log says so in as many words, and lists what it must show before stage 1. Stage 1
//  cuts for vehicles only, stage 2 adds infantry shooters. The stage is a hidden preference and a battle that ends abnormally drops it
//  back by one. Nothing advances on its own.
//
//  THE CEILING, and why it is not just a sentence in the log. The neighbouring pass, aiming at HELICOPTERS 27 m up, already measures
//  92 % of its pairs masked on the map he played on 2026-09-19 (Latest.log: 159 188 pairs, 146 654 masked; 97 % for its vehicle pairs;
//  88 % even between 0 and 1 km), and the forest is 137 013 of those maskings. A line that starts 3 m up and ends 3 m up is lower than
//  any of them, so it will be masked at least as often. On such a map, setting the stage to 1 with nothing but a printed criterion
//  would make every tank on the map blind past the floor in every direction - exactly what he did NOT ask for. So the share is a gate,
//  not a remark: while more than MaxMasqueProche of the VEHICLE pairs just above the floor come back masked, the rule stays in
//  measurement whatever the stage says, and a French line says why. It also has to have measured at least MinProcheControles of them
//  before it may cut at all, so a cold battle starts in measurement rather than starting blind.
//
//  SYMMETRY. One list, one cycle, one published set for both sides; the hook gets two entity ids and cannot tell whose they are.
//  Per-side caps, never a shared one - and that includes the LINE budget, not only the lists. The pairs are built side 0 first, so a
//  single shared budget of lines would always be spent on side 0's pairs and side 1's would answer "clear" for want of one: side 0
//  would be cut harder than side 1, always in the same direction, and the two per-side columns of the report - the ones whose whole
//  job is to prove the symmetry - would show it as terrain. Each side therefore gets half the lines of a cycle and cannot touch the
//  other half. NO script filter: script-held units are overwhelmingly on the AI side, so filtering them out
//  would BE the asymmetry (R6 measured 56 of 118 ground units script-held at all times). R7 only ever REMOVES fire from a gun beyond
//  800 m, and a scripted column closing to under 800 m shoots exactly as before, so it can neither stall an assault nor block a stage.
//  The script figure is read for MEASUREMENT and printed per side, so if a mission ever does stall the number is already in the log.
//
//  SAFETY. Its own error counter and kill-switch; the same map gate as R4 and R6 (VueAA.ReadyForGating); off with the combat
//  watchdog, the proof stage and the impact counter of AntiHeliPortee, and off unless the DAMAGE hook is really installed (without it
//  this module never learns that a unit is being hit, so it can neither spare one that is visibly in the open nor count the rounds its
//  own watchdog judges on); no allocation, no Unity call and no logging on the hook side; and its own watchdog, which needs FIVE
//  things at once before it disarms R7 alone for the battle - the rule really acted, the engine really kept answering, not one
//  direct-fire gun round landed on a ground unit, NOTHING AT ALL landed anywhere in the battle, and the two sides were in contact
//  throughout. That fourth one is new and it is the one that matters: the rounds this module can see are a narrow slice (no
//  artillery, no mortar, no rocket, no missile, no rifle, nothing air-to-ground), so without it an infantry fight, an artillery
//  preparation or an air-support phase would look exactly like a rule that had silenced every gun on the map.
//  Judging on silence alone is the mistake LeurresAuto.cs:673-706 already paid for.
//  Its cuts feed a counter of their own in AntiHeliPortee (_changedSol), never _changed: _changed is the combat watchdog's evidence
//  that the HELICOPTER rules acted, it is what lets it trip on a silence and what lets a re-test write a proof that blocks them for
//  two battles, and a ground rule cutting freely would manufacture that evidence on their behalf.
//  Logs: [VUE-SOL], in French. This module is driven from VueAA.Frame (last, and only in an image the anti-air pass did not work in),
//  so its time is counted inside the "VueAA" line of the performance report; it prints its own worst image here.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using MelonLoader;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class VueSol
    {
        const string GuardVersion = "1.0";

        // ---- producer
        const float CycleSeconds = 1.0f;            // tanks are not helicopters: half the rate of the anti-air pass
        const float ValidMs = 1500f;                // a ground verdict lives 1.5 s (VueAA's 3 s would let a 20 m/s unit walk out of it)
        const float FloorVehicle = 800f;            // the PROMISE: under this, a vehicle is never cut, however stale the verdict
        const float FloorInfantry = 1000f;          // above the 12.7 mm group: past 1 km an infantry team is an ATGM or an HMG, never a rifleman
        // worst staleness of a verdict: 1 s of position age + up to 3 s of cycle + ValidMs of publication = 5.5 s, during which two
        // vehicles driving at each other at 100 km/h each close 5.5 x 56 = 308 m. Rounded up, this is the distance between the floor
        // that is promised and the distance at which a pair is actually queued.
        const float ClosureMargin = 320f;
        const float QueueVehicle = FloorVehicle + ClosureMargin;        // 1120 m
        const float QueueInfantry = FloorInfantry + ClosureMargin;      // 1320 m
        const float MaxRange = 4000f;               // nothing in the direct-fire gun tables reaches further
        const float RangeMargin = 150f;
        const float NearBand = 2000f;               // pairs closer than this are queued first, so the cap drops the far ones
        const int MaxShootersPerSide = 60;          // per SIDE, never a shared list: a shared one fills with side 0 first on a big map
        const int MaxTargetsPerSide = 200;          // idem: without it the pair loop is proportional to the whole ground population
        const int MaxPairsPerSide = 1200, MaxPairsTotal = 2400;
        const int MaxWalksPerCycle = 800;           // 800 x 1.25 us = 1.0 ms a second = 0.017 ms an image
        const int MaxWalksPerSideCycle = MaxWalksPerCycle / 2;          // half each, so side 0's pairs can never eat side 1's lines
        const int MaxWalksPerFrame = 200;           // second, independent cap: one image can never hold more than about 0.25 ms of line work
        const double BudgetMs = 0.8;                // and a third one, on the clock, like the anti-air pass

        // ---- the ceiling: how masked the VEHICLE pairs just above the floor may be before the rule is allowed to cut at all
        const double MaxMasqueProche = 0.80;
        const double PlafondMarge = 0.05;           // hysteresis: 75 % to start cutting, 80 % to go on cutting
        const long MinProcheControles = 500;

        // ---- verdict cache
        const long CacheLifeMs = 20000;
        const int MaxCacheEntries = 40000;
        const int CheckEvery = 50;                  // one cached answer in 50 is recomputed exactly and compared
        const double MaxDisagree = 0.05;
        const long MinChecks = 2000;

        // ---- hook side
        const int MaxErrors = 50;
        const int HitRing = 1024;                   // power of two
        const float HitLife = 10f;                  // a unit that took a round stays out of the rule for this long

        // ---- watchdog (watchdog clock: it does not advance on the end screen, in pause or at time scale 0)
        const float WatchWindow = 300f;
        const int WatchMinCuts = 300;               // below this the rule is not what stopped anything
        const int WatchMinUncut = 200;              // positive proof the engine kept answering: without it the window restarts
        // safety line: warning only, nothing is switched off (the forest may genuinely be blocking)
        const float SafetyWindow = 180f, SafetyMaskedShare = 0.8f;
        const int SafetyMaxWarnings = 3;

        const int Bands = 5;                        // 0-1, 1-2, 2-3, 3-4, 4+ km
        static readonly HashSet<int> VideInt = new();
        static readonly HashSet<long> VideLong = new();

        // ---------------------------------------------------------------- hook side: immutable sets, plain counters

        /// The module is running (measuring or acting). One volatile read is the whole cost of the rule while it is off.
        internal static volatile bool Actif;
        /// The rule may really zero a range. False for the whole of stage 0.
        static volatile bool _coupe;
        static volatile HashSet<int> _tireursSol = VideInt;      // eligible shooters, both sides, one list
        static volatile HashSet<int> _camp0 = VideInt;           // of those, the ones of side 0: the symmetry counters, nothing else
        static volatile HashSet<int> _canonsSol = VideInt;       // direct-fire gun rounds that can hit a ground unit
        static volatile HashSet<long> _masqueSol = VideLong;     // published verdicts: ((long)shooter << 32) | (uint)target
        static volatile HashSet<int> _touchesRecentes = VideInt; // targets that took a round in the last 10 s
        static long _validUntilSol;

        static long _appels, _tireurAuSol, _cibleAuSol, _solSol;        // stage 0: does targetEntity.EntityId really match the ground set
        static long _askEligible, _askCanon, _askComplet, _perime, _skipTouche;
        static long _askSide0, _askSide1, _masqueVu, _masqueSide0, _masqueSide1;
        static long _auraitCoupe, _cutSol, _cutSide0, _cutSide1;
        static long _touchesSol, _touchesCanon;                         // hits landed on a ground unit, and of those the direct-fire gun rounds
        static long _err, _anneauPerdus;

        static readonly long[] _anneau = new long[HitRing];
        static readonly int[] _anneauSeq = new int[HitRing];
        static int _anneauEcrit, _anneauLu;

        /// Ranges this rule really set to 0 so far in this battle (0 for the whole of stage 0). Read by the anti-helicopter report.
        internal static long Coupes => Interlocked.Read(ref _cutSol);

        /// Hot path, maybe off the main thread. Counts the ground-to-ground calls and says whether this one is worth reading the
        /// ammunition for. No allocation, no Unity call, no logging: Interlocked and immutable set reads only.
        internal static bool Candidat(int shooter, int target, bool shooterGround, bool targetGround)
        {
            try
            {
                Interlocked.Increment(ref _appels);
                if (shooterGround) Interlocked.Increment(ref _tireurAuSol);
                if (!targetGround) return false;
                Interlocked.Increment(ref _cibleAuSol);
                if (!shooterGround) return false;
                Interlocked.Increment(ref _solSol);
                return _tireursSol.Contains(shooter);
            }
            catch { if (Interlocked.Increment(ref _err) > MaxErrors) ArretCrochet(); return false; }
        }

        /// Hot path, maybe off the main thread. The rest of the ladder. True when the range was really set to 0 (the caller then feeds
        /// the combat watchdog, exactly as it does for R1, R2 and R6). Returns false for every call of stage 0.
        internal static bool Coupe(int shooter, int target, int ammoId, ref float result)
        {
            try
            {
                Interlocked.Increment(ref _askEligible);
                if (!_canonsSol.Contains(ammoId)) return false;         // direct fire, no seeker, ground bit: never artillery, never a missile
                Interlocked.Increment(ref _askCanon);
                bool zero = _camp0.Contains(shooter);
                if (zero) Interlocked.Increment(ref _askSide0); else Interlocked.Increment(ref _askSide1);
                if (Environment.TickCount64 > Interlocked.Read(ref _validUntilSol)) { Interlocked.Increment(ref _perime); return false; }
                // BOTH ends. A unit taking fire is visibly in the open, and so is the one it is exchanging fire with. Sparing only the
                // target would spare the aggressor and leave the unit being killed unable to answer: a missile post or a howitzer -
                // neither of which this rule can ever touch - would hit a tank through a wood at 1200 m, the tank would go into this
                // set, and its OWN gun would stay at range 0 while it died. A unit that has just been hit may shoot back.
                var touchees = _touchesRecentes;
                if (touchees.Contains(target) || touchees.Contains(shooter)) { Interlocked.Increment(ref _skipTouche); return false; }
                Interlocked.Increment(ref _askComplet);
                if (!_masqueSol.Contains(((long)shooter << 32) | (uint)target)) return false;
                Interlocked.Increment(ref _masqueVu);
                if (zero) Interlocked.Increment(ref _masqueSide0); else Interlocked.Increment(ref _masqueSide1);
                if (!_coupe) { Interlocked.Increment(ref _auraitCoupe); return false; }   // stage 0: the log says what it WOULD have cut
                if (!(result > 0f)) return false;
                result = 0f;
                Interlocked.Increment(ref _cutSol);
                if (zero) Interlocked.Increment(ref _cutSide0); else Interlocked.Increment(ref _cutSide1);
                return true;
            }
            catch { if (Interlocked.Increment(ref _err) > MaxErrors) ArretCrochet(); return false; }
        }

        /// Hot path, any thread (the damage hook of AntiHeliPortee). A unit taking a round right now is visibly in the open: its entity
        /// id goes into a fixed ring the main thread drains. The direct-fire gun rounds landing on the ground are counted straight away,
        /// because they are the denominator of the watchdog and must not depend on the ring. No allocation, no Unity call, no logging.
        internal static void NoteTouche(int eid, int ammoId, bool auSol)
        {
            try
            {
                if (!Actif || !auSol) return;
                Interlocked.Increment(ref _touchesSol);
                bool canon = _canonsSol.Contains(ammoId);
                if (canon) Interlocked.Increment(ref _touchesCanon);
                int n = Interlocked.Increment(ref _anneauEcrit);
                int w = n & (HitRing - 1);
                _anneauSeq[w] = 0;                                      // slot being written
                _anneau[w] = ((long)eid << 1) | (canon ? 1L : 0L);
                Volatile.Write(ref _anneauSeq[w], n);                   // written last: the slot is complete
            }
            catch { if (Interlocked.Increment(ref _err) > MaxErrors) ArretCrochet(); }
        }

        // ---------------------------------------------------------------- main thread

        sealed class Tireur
        {
            public int Eid, Uid, UnitId, Camp, Cellule, Hauteur;
            public bool Infanterie;
            public float Plancher, Portee;
            public V3 Pos;
            public VueAA.Eye Oeil;
            public int Etat;                                            // 0 = eye not computed yet, 1 = ready, 2 = unusable
        }

        sealed class Cible
        {
            public int Eid, Camp, Kt, Cellule, Hauteur;
            public bool Infanterie;
            public V3 Pos;
            public float Visee;
            public int Etat;                                            // 0 = aim point not computed yet, 1 = ready, 2 = off the map
        }

        struct Paire { public long Cle; public int T, C, Camp, Bande; public float Dist; public bool Inf; }

        static MelonPreferences_Entry<bool> _enabled, _cacheOn;
        static MelonPreferences_Entry<int> _etapePref, _unclean;
        static MelonPreferences_Entry<string> _guardVersion;

        static bool _sessionArmed, _arret, _entete, _cycleEnCours, _cacheActif = true;
        static int _etape, _versionCarte = -1, _campJoueur = -1;
        static string _raisonArret;
        static float _prochainCycle, _prochainReport, _prochainDrain, _cycleDebut;

        static readonly List<Tireur> _tireurs = new();
        static readonly List<Cible> _cibles = new();
        static readonly List<int>[] _ciblesDuCamp = { new(), new() };   // indexes into _cibles, per side: the pair loop only walks the other one
        static readonly List<Paire> _paires = new();
        static readonly Dictionary<int, int> _campParEid = new();
        static readonly Dictionary<int, float> _touchees = new();       // eid -> realtime of its last hit
        static readonly Dictionary<long, long> _cache = new();          // key -> (TickCount64 << 1) | verdict
        static HashSet<long> _brut = new(), _brutPrecedent = new();
        static int _idx, _marchesCycle;
        static readonly int[] _marchesCamp = new int[2];                // half the cycle's lines each: one side can never spend the other's
        static readonly Stopwatch _sw = new();

        // statistics (main thread only)
        static long _cycles, _cyclesAbandonnes, _pairesEnFile, _pairesEvaluees, _pairesMasquees, _pairesSansMarche, _pairesHorsCarte;
        static long _marches, _marchesCache, _cacheHits, _cacheManques, _cacheVides, _cacheControles, _cacheEcarts;
        static long _clairAvecForet, _naviresEcartes, _hysteresis;
        static long _yeuxSol, _viseesSol;                               // eyes and aim points built: native work, budgeted like a line
        static readonly long[] _parCause = new long[4];
        static readonly long[] _bandeEval = new long[Bands], _bandeMasq = new long[Bands];
        // The band just above the queue floor, split by the kind of SHOOTER. Stage 1 will only ever concern vehicles, so the vehicle
        // column is the one the ceiling judges and the one the stage-1 criteria are read on; stage 0 queues infantry too, and mixing
        // the two would have the entry figures describe a population stage 1 does not have.
        static long _procheEvalVeh, _procheMasqVeh, _procheEvalInf, _procheMasqInf;
        static long _evalVeh, _masqVeh, _evalInf, _masqInf;
        static readonly long[] _tireursCamp = new long[2], _tireursCumul = new long[2], _pairesCamp = new long[2], _pairesPlafond = new long[2];
        static readonly long[] _ciblesPlafond = new long[2];             // ground units dropped from the target list by the per-side cap
        static readonly long[] _evalCamp = new long[2], _masqCamp = new long[2], _scriptCamp = new long[2];
        static readonly long[] _touchesCamp = new long[2];
        static double _frameMaxMs, _marcheMaxMs, _cycleMaxS;
        static int _pairesMax, _cacheMax;
        static string _dernierRapport;

        // watchdog and safety line
        static float _wdBase = -1f, _sureteBase = -1f;
        static long _wdCuts, _wdComplets, _wdHits, _wdImpacts, _sureteHits;
        static bool _wdCoupe;
        static int _sureteAlertes;
        static int _plafondEtat;                                        // 0 nothing said yet, 1 the ceiling is holding the cut, 2 it is not

        static void Log(string s) => Mod.Log.Msg("[VUE-SOL] " + s);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_VueSol");
            _enabled = c.CreateEntry("MesureVueSol", true, description: Build.Desc("Vue propre des unités au sol entre elles (forêts, bâtiments, relief) : calcul et mesure ; ce qui est réellement coupé dépend de l'étape ci-dessous"));
            _etapePref = c.CreateEntry("VueSolEtape", 0, description: Build.Desc("0 : mesure seule, rien n'est coupé (par défaut) ; 1 : les véhicules au-delà de 800 m ; 2 : les véhicules et l'infanterie au-delà de 1000 m"));
            _cacheOn = c.CreateEntry("CacheVueSol", true, description: Build.Desc("Réutiliser une vue déjà calculée pour une case de 12 m pendant 20 s ; coupée automatiquement si le contrôle montre trop d'écarts"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
        }

        /// Called by VueAA when it closes a battle (new mission, game closed, no game left, online game): the single place this module
        /// has to follow, so its session can never outlive the anti-air pass's.
        internal static void FinBataille(string why)
        {
            if (!_sessionArmed && !Actif && _cache.Count == 0 && _paires.Count == 0 && _touchees.Count == 0) return;
            if (_sessionArmed)
            {
                _sessionArmed = false;
                try { Rapport(true); } catch { }
                if (_unclean.Value != 0) { _unclean.Value = 0; try { MelonPreferences.Save(); } catch { } }
                Log($"fin de calcul ({why})");
            }
            Oublier();
        }

        static void Oublier()
        {
            Actif = false; _coupe = false;
            _tireursSol = VideInt; _camp0 = VideInt; _canonsSol = VideInt; _masqueSol = VideLong; _touchesRecentes = VideInt;
            Interlocked.Exchange(ref _validUntilSol, 0);
            _tireurs.Clear(); _cibles.Clear(); _paires.Clear(); _ciblesDuCamp[0].Clear(); _ciblesDuCamp[1].Clear();
            _campParEid.Clear(); _touchees.Clear(); _cache.Clear();
            // the two working sets are recycled, never republished: only the set handed to the hook is ever a new object
            _brut.Clear(); _brutPrecedent.Clear();
            _idx = 0; _marchesCycle = 0; Array.Clear(_marchesCamp, 0, 2); _cycleEnCours = false;
            _anneauLu = Volatile.Read(ref _anneauEcrit);
            _versionCarte = -1; _arret = false; _arretDemande = false; _raisonArret = null; _cacheActif = true; _entete = false; _campJoueur = -1;
            _prochainCycle = 0f; _prochainReport = 0f; _prochainDrain = 0f;
            RemiseAZero();
        }

        static void RemiseAZero()
        {
            Interlocked.Exchange(ref _appels, 0); Interlocked.Exchange(ref _tireurAuSol, 0); Interlocked.Exchange(ref _cibleAuSol, 0);
            Interlocked.Exchange(ref _solSol, 0); Interlocked.Exchange(ref _askEligible, 0); Interlocked.Exchange(ref _askCanon, 0);
            Interlocked.Exchange(ref _askComplet, 0); Interlocked.Exchange(ref _perime, 0); Interlocked.Exchange(ref _skipTouche, 0);
            Interlocked.Exchange(ref _askSide0, 0); Interlocked.Exchange(ref _askSide1, 0); Interlocked.Exchange(ref _masqueVu, 0);
            Interlocked.Exchange(ref _masqueSide0, 0); Interlocked.Exchange(ref _masqueSide1, 0); Interlocked.Exchange(ref _auraitCoupe, 0);
            Interlocked.Exchange(ref _cutSol, 0); Interlocked.Exchange(ref _cutSide0, 0); Interlocked.Exchange(ref _cutSide1, 0);
            Interlocked.Exchange(ref _touchesSol, 0); Interlocked.Exchange(ref _touchesCanon, 0); Interlocked.Exchange(ref _err, 0);
            Interlocked.Exchange(ref _anneauPerdus, 0);
            _cycles = _cyclesAbandonnes = _pairesEnFile = _pairesEvaluees = _pairesMasquees = _pairesSansMarche = _pairesHorsCarte = 0;
            _marches = _marchesCache = _cacheHits = _cacheManques = _cacheVides = _cacheControles = _cacheEcarts = 0;
            _clairAvecForet = _naviresEcartes = _hysteresis = 0;
            _yeuxSol = _viseesSol = 0;
            Array.Clear(_parCause, 0, _parCause.Length);
            Array.Clear(_bandeEval, 0, Bands); Array.Clear(_bandeMasq, 0, Bands);
            _procheEvalVeh = _procheMasqVeh = _procheEvalInf = _procheMasqInf = 0;
            _evalVeh = _masqVeh = _evalInf = _masqInf = 0;
            Array.Clear(_tireursCamp, 0, 2); Array.Clear(_tireursCumul, 0, 2); Array.Clear(_pairesCamp, 0, 2); Array.Clear(_pairesPlafond, 0, 2);
            Array.Clear(_ciblesPlafond, 0, 2);
            Array.Clear(_evalCamp, 0, 2); Array.Clear(_masqCamp, 0, 2); Array.Clear(_scriptCamp, 0, 2);
            Array.Clear(_touchesCamp, 0, 2);
            _frameMaxMs = _marcheMaxMs = _cycleMaxS = 0; _pairesMax = 0; _cacheMax = 0;
            _dernierRapport = null;
            _wdBase = -1f; _wdCuts = _wdComplets = _wdHits = _wdImpacts = 0; _wdCoupe = false;
            _sureteBase = -1f; _sureteHits = 0; _sureteAlertes = 0; _plafondEtat = 0;
        }

        /// Called last by VueAA.Frame, with travailAA = true when the anti-air pass already copied, calibrated or walked in this image.
        /// The two line passes then never spend their budget in the same one.
        internal static void Image(float now, bool travailAA)
        {
            if (_enabled == null) return;
            try
            {
                if (!_enabled.Value) { FinBataille("calcul désactivé"); return; }
                if (!VueAA.CarteSolPrete)
                {
                    // no map yet, or the anti-air pass gave up on this battle: nothing is published, nothing can be cut
                    if (Actif) { Actif = false; _coupe = false; _masqueSol = VideLong; _tireursSol = VideInt; Interlocked.Exchange(ref _validUntilSol, 0); }
                    return;
                }
                // ONE clock for the whole image, started here and read once at the end. The pair build and the line pass run in the
                // SAME image (the build arms the cycle and the pass takes it straight away), so two clocks would let the image spend
                // the build AND the full budget, and would then print the larger of the two halves as "the longest image" instead of
                // their sum - a measuring build failing its own entry bar on a figure that is too small. The budget of the line pass
                // is now what is left of the image, which is what the header says it is. Arming comes first and is left out: it happens
                // once in a battle and writes the preferences to disk, and a disk write is not what an image of this module costs.
                if (!_sessionArmed) Armer();
                _sw.Restart();
                if (_arretDemande && !_arret) Arret("trop d'erreurs dans le crochet");   // the hook asked for it; the main thread finishes the job
                if (_arret) { Etat(); Rythme(now); Chrono(); return; }

                if (_versionCarte != VueAA.VersionCarte)
                {
                    // VueAA copied the map again (a building came down): every kept verdict describes a map that no longer exists
                    if (_versionCarte >= 0) Log($"la carte a été recopiée : les {_cache.Count} vues gardées en mémoire sont oubliées");
                    _versionCarte = VueAA.VersionCarte;
                    _cache.Clear();
                    _brutPrecedent.Clear();
                    // a map too large for the 19 bits of a cell number would give a wrong answer, which is worse than a slow one:
                    // every line is then calculated in full
                    if (_cacheActif && !VueAA.CelluleSolFiable)
                    {
                        _cacheActif = false;
                        Log("carte trop grande pour réutiliser les vues par case : chaque vue sera calculée en entier");
                    }
                }

                if (now >= _prochainDrain) { _prochainDrain = now + 0.5f; ViderAnneau(now); }
                // a cycle that never gets an image (the anti-air pass working in every one of them, a long copy, a frozen game) must
                // not hold the module for ever: after 3 s it is dropped, the published set becomes empty and nothing can be cut
                if (_cycleEnCours && now - _cycleDebut > 3f)
                {
                    _cycleEnCours = false; _cyclesAbandonnes++;
                    _brut.Clear();
                    PublierVide();
                }
                if (!_cycleEnCours && now >= _prochainCycle) DebutCycle(now);
                if (_cycleEnCours && !travailAA) TourDeCycle(now);
                Etat();
                Rythme(now);
                Chrono();
            }
            catch (Exception e) { Erreur("calcul de la vue au sol", e); }
        }

        /// The whole image, report included: what the module really cost, not the largest of its pieces.
        static void Chrono()
        {
            double ms = _sw.Elapsed.TotalMilliseconds;
            if (ms > _frameMaxMs) _frameMaxMs = ms;
        }

        static void Rythme(float now)
        {
            if (now < _prochainReport) return;
            _prochainReport = now + 30f;
            try { Rapport(false); } catch (Exception e) { Erreur("relevé", e); }
        }

        static void Armer()
        {
            // crash guard: a battle that ends abnormally drops the stage back by one. Nothing is ever written into the game, so there
            // is never anything to restore; only the stage steps back.
            int want = Math.Clamp(_etapePref.Value, 0, 2);
            if (_unclean.Value >= 1 && want > 0)
            {
                want--;
                _etapePref.Value = want;
                Mod.Log.Warning($"[VUE-SOL] la bataille précédente ne s'est pas terminée normalement : l'étape redescend à {want} ({(want == 0 ? "mesure seule, rien n'est coupé" : "véhicules seulement")})");
            }
            _etape = want;
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;
            try { MelonPreferences.Save(); } catch { }
            _cacheActif = _cacheOn.Value;
        }

        static void Erreur(string quoi, Exception e)
        {
            long n = Interlocked.Increment(ref _err);
            if (n <= 3 || n == MaxErrors + 1)
                Mod.Log.Warning($"[VUE-SOL] {quoi} impossible : {e.GetBaseException().Message}" + (n > MaxErrors ? " (calcul arrêté pour cette bataille)" : ""));
            if (n > MaxErrors) Arret("trop d'erreurs dans le calcul");
        }

        /// Kill-switch: the game goes straight back to its own ranges. Nothing was written into it, so there is nothing to restore,
        /// and nothing is ever unpatched.
        static void Arret(string pourquoi)
        {
            if (_arret) return;
            _arret = true; _raisonArret = pourquoi;
            ArretCrochet();
            _cycleEnCours = false;
        }

        /// The same kill-switch as seen from the hook, which may run off the main thread: volatile flags and the two sets that were
        /// allocated once at load only. No allocation, no Unity call, no logging. The main thread finishes the job on its next turn.
        static void ArretCrochet()
        {
            _arretDemande = true;
            Actif = false; _coupe = false;
            _masqueSol = VideLong; _tireursSol = VideInt; _camp0 = VideInt; _touchesRecentes = VideInt;
            Interlocked.Exchange(ref _validUntilSol, 0);
        }

        static volatile bool _arretDemande;

        /// The rule's state, read from the neighbours: the map gate, the combat watchdog of AntiHeliPortee, its proof stage and its
        /// impact counter. The module always MEASURES (that is the whole point of stage 0); only the cut follows.
        static void Etat()
        {
            Actif = !_arret && _sessionArmed;
            bool cacheSain = !_cacheActif || _cacheControles < MinChecks || (double)_cacheEcarts / _cacheControles < MaxDisagree;
            // the ceiling. Measured on the VEHICLE pairs just above the floor, because that is the population stage 1 acts on and the
            // one where being wrong is felt in the first minute. Not enough of them measured yet = no cut: a battle starts measuring.
            bool assez = _procheEvalVeh >= MinProcheControles;
            double partProche = _procheEvalVeh > 0 ? (double)_procheMasqVeh / _procheEvalVeh : 1.0;
            // hysteresis, the same idea as on a verdict: it takes a clearly lower share to start cutting than to keep cutting, so the
            // rule cannot switch itself on and off around the limit while the share drifts across it
            double limite = _plafondEtat == 2 ? MaxMasqueProche : MaxMasqueProche - PlafondMarge;
            bool vuePlausible = assez && partProche <= limite;
            _coupe = Actif && !_wdCoupe && _etape >= 1 && VueAA.ReadyForGating && !AntiHeliPortee.ReglesBloquees
                     && AntiHeliPortee.CrochetDegats && _canonsSol.Count > 0 && cacheSain && vuePlausible;
            // said once each way, and only from stage 1 on: at stage 0 nothing is cut whatever the share is
            if (_etape >= 1 && assez)
            {
                int veut = vuePlausible ? 2 : 1;
                if (veut != _plafondEtat)
                {
                    _plafondEtat = veut;
                    if (veut == 1)
                        Mod.Log.Warning($"[VUE-SOL] {100.0 * partProche:0} % des vues entre véhicules juste au-dessus du plancher " +
                                        $"({QueueVehicle:0}-{NearBand:0} m) sont masquées, sur {_procheEvalVeh} mesures : c'est au-dessus de la limite de " +
                                        $"{100.0 * limite:0} %. Cette carte est trop couverte pour le modèle actuel, la règle reste en mesure seule " +
                                        "et ne coupe aucune portée (mieux vaut ne rien couper que rendre vos véhicules aveugles).");
                    else
                        Log($"{100.0 * partProche:0} % des vues entre véhicules juste au-dessus du plancher sont masquées sur {_procheEvalVeh} mesures, " +
                            $"sous la limite de {100.0 * limite:0} % : la règle peut couper");
                }
            }
        }

        // ---------------------------------------------------------------- the recently hit set (a target being hit is in the open)

        static void ViderAnneau(float now)
        {
            int write = Volatile.Read(ref _anneauEcrit);
            if (write != _anneauLu)
            {
                int first = _anneauLu + 1;
                if (write - first >= HitRing)
                {
                    Interlocked.Add(ref _anneauPerdus, write - first - HitRing + 1);
                    first = write - HitRing + 1;
                }
                for (int n = first; n <= write; n++)
                {
                    int w = n & (HitRing - 1);
                    if (Volatile.Read(ref _anneauSeq[w]) != n) continue;    // slot overwritten while it was read: dropped, counted above
                    long v = _anneau[w];
                    int eid = (int)(v >> 1);
                    _touchees[eid] = now;
                    if ((v & 1L) != 0 && _campParEid.TryGetValue(eid, out int camp) && (uint)camp < 2u) _touchesCamp[camp]++;
                }
                _anneauLu = write;
            }
            if (_touchees.Count == 0) { if (_touchesRecentes.Count != 0) _touchesRecentes = VideInt; return; }
            var vivants = new HashSet<int>();
            List<int> vieux = null;
            foreach (var kv in _touchees)
            {
                if (now - kv.Value <= HitLife) vivants.Add(kv.Key);
                else (vieux ??= new List<int>()).Add(kv.Key);
            }
            if (vieux != null) foreach (int k in vieux) _touchees.Remove(k);
            _touchesRecentes = vivants;                                     // one assignment: the hook never sees a set being filled
        }

        // ---------------------------------------------------------------- the pair build (main thread, once a second)

        static void DebutCycle(float now)
        {
            _prochainCycle = now + CycleSeconds;
            _tireurs.Clear(); _cibles.Clear(); _paires.Clear(); _ciblesDuCamp[0].Clear(); _ciblesDuCamp[1].Clear();
            _marchesCycle = 0; Array.Clear(_marchesCamp, 0, 2); _idx = 0;
            try { _campJoueur = Spawns.PlayerSide; } catch { _campJoueur = -1; }
            _canonsSol = AntiHeliPortee.CanonsSol ?? VideInt;
            ChienDeGarde();
            LigneDeSurete();
            if (_canonsSol.Count == 0) { PublierVide(); return; }

            // stage 0 queues infantry too: the first log then describes exactly what stage 2 would do, while cutting nothing at all
            bool avecInfanterie = _etape != 1;
            var elig = new HashSet<int>();
            var camp0 = new HashSet<int>();
            _campParEid.Clear();

            for (int camp = 0; camp < 2; camp++)
            {
                int pris = 0;
                int n = AntiHeliPortee.SolCount(camp);
                for (int i = 0; i < n; i++)
                {
                    if (!AntiHeliPortee.SolAt(camp, i, now, out int eid, out int uid, out int unitId, out int type, out bool posOk, out V3 pos)) continue;
                    if (!posOk) continue;
                    _campParEid[eid] = camp;
                    // ships are out at BOTH ends: VueAA has exactly two eye heights, 1.8 m and 3 m, and a naval gun sits ten metres up,
                    // so it would be wrongly told it cannot see. The number this costs is printed, so the choice can be revisited.
                    if (AntiHeliPortee.EstNavire(type)) { _naviresEcartes++; continue; }
                    bool inf = AntiHeliPortee.EstInfanterie(type);
                    // targets are capped per side too, exactly as the shooters are. This is the one part of the module with no budget
                    // of its own: it runs whole, in one image, and the pair loop below walks the shooter x target matrix TWICE, so
                    // without a cap its cost follows the ground population - 120 x 300 x 2 distance tests and 600 objects in a single
                    // image on a loaded mission. Per side, so both sides keep the same ceiling. It only limits the TARGET list: a
                    // unit past the cap can still be an eligible shooter, and the number dropped is printed.
                    if (_ciblesDuCamp[camp].Count < MaxTargetsPerSide)
                    {
                        _ciblesDuCamp[camp].Add(_cibles.Count);
                        _cibles.Add(new Cible { Eid = eid, Camp = camp, Pos = pos, Infanterie = inf });
                    }
                    else _ciblesPlafond[camp]++;
                    if (unitId <= 0) continue;
                    if (inf && !avecInfanterie) continue;
                    float portee = AntiHeliPortee.PorteeSolDe(unitId);
                    // the QUEUE floor, the promised floor plus the closing margin: a verdict as stale as the design allows still
                    // cannot act below the promised floor, because the pair was never queued that close
                    float plancher = inf ? QueueInfantry : QueueVehicle;
                    if (!(portee > plancher)) continue;                     // a gun that cannot reach past the floor has nothing to gate
                    if (pris >= MaxShootersPerSide) continue;               // per side, never a shared cap
                    pris++;
                    _tireursCumul[camp]++;                                  // cumulative, for the per-side ratio; _tireursCamp is this cycle's
                    elig.Add(eid);
                    if (camp == 0) camp0.Add(eid);
                    _tireurs.Add(new Tireur
                    {
                        Eid = eid, Uid = uid, UnitId = unitId, Camp = camp, Infanterie = inf, Pos = pos,
                        Plancher = plancher, Portee = Math.Min(portee + RangeMargin, MaxRange)
                    });
                }
                _tireursCamp[camp] = pris;
            }
            // one assignment each: the hook never sees a set being filled. The side set goes FIRST, so a shooter that becomes eligible
            // between the two is already classified when the hook first sees it (it only ever changes which counter goes up).
            _camp0 = camp0;
            _tireursSol = elig;

            // nearest band first, in two passes, so the per-side cap drops the far pairs: they are the least likely to be a real
            // engagement. Only the OTHER side's targets are walked: at 60 shooters and 90 units that halves the loop for nothing.
            var comptes = new int[2];
            const float near2 = NearBand * NearBand;
            for (int passe = 0; passe < 2; passe++)
                for (int ti = 0; ti < _tireurs.Count; ti++)
                {
                    var t = _tireurs[ti];
                    float min2 = t.Plancher * t.Plancher, max2 = t.Portee * t.Portee;
                    var enFace = _ciblesDuCamp[1 - t.Camp];
                    for (int ii = 0; ii < enFace.Count; ii++)
                    {
                        int ci = enFace[ii];
                        var c = _cibles[ci];
                        float dx = c.Pos.x - t.Pos.x, dz = c.Pos.z - t.Pos.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < min2 || d2 > max2) continue;
                        bool proche = d2 <= near2;
                        if (passe == 0 ? !proche : proche) continue;
                        if (comptes[t.Camp] >= MaxPairsPerSide || _paires.Count >= MaxPairsTotal) { _pairesPlafond[t.Camp]++; continue; }
                        comptes[t.Camp]++;
                        float d = MathF.Sqrt(d2);
                        _paires.Add(new Paire
                        {
                            Cle = ((long)t.Eid << 32) | (uint)c.Eid, T = ti, C = ci, Camp = t.Camp, Inf = t.Infanterie,
                            Bande = Math.Min(Bands - 1, (int)(d / 1000f)), Dist = d
                        });
                    }
                }
            _pairesCamp[0] += comptes[0]; _pairesCamp[1] += comptes[1];
            _pairesEnFile += _paires.Count;
            if (_paires.Count > _pairesMax) _pairesMax = _paires.Count;
            if (_paires.Count == 0) { PublierVide(); return; }
            _brut.Clear();                                              // recycled: only the set handed to the hook is ever a new object
            VueAA.NouveauCycleSol();
            _cycleEnCours = true;
            _cycleDebut = now;
        }

        /// Nothing to evaluate this cycle: the published set becomes empty (so nothing can be cut) without allocating anything.
        static void PublierVide()
        {
            if (_masqueSol.Count != 0) _masqueSol = VideLong;
            _brutPrecedent.Clear();
            Interlocked.Exchange(ref _validUntilSol, Environment.TickCount64 + (long)ValidMs);
            _cycles++;
        }

        // ---------------------------------------------------------------- the line pass (main thread, budgeted three ways)

        static void TourDeCycle(float now)
        {
            int marchesImage = 0;
            while (_idx < _paires.Count)
            {
                var p = _paires[_idx++];
                int marchesAvant = marchesImage;
                bool masque = Evaluer(in p, now, ref marchesImage);
                _pairesEvaluees++;
                _evalCamp[p.Camp]++;
                _bandeEval[p.Bande]++;
                bool proche = p.Dist < NearBand;
                if (p.Inf) { _evalInf++; if (proche) _procheEvalInf++; }
                else { _evalVeh++; if (proche) _procheEvalVeh++; }
                if (masque)
                {
                    _brut.Add(p.Cle);
                    _pairesMasquees++;
                    _masqCamp[p.Camp]++;
                    _bandeMasq[p.Bande]++;
                    if (p.Inf) { _masqInf++; if (proche) _procheMasqInf++; }
                    else { _masqVeh++; if (proche) _procheMasqVeh++; }
                }
                if (marchesImage >= MaxWalksPerFrame) break;
                // the clock is only worth reading when it can have moved. A pair served by the cache is one dictionary lookup, about
                // 20 ns; Stopwatch.Elapsed is a counter read plus a TimeSpan plus a division, more than that on its own - and the
                // cache-served pair is the COMMON case, so reading the clock every time made the budget run out about twice as fast
                // as the work deserved and spread every cycle over twice as many images.
                if ((marchesImage != marchesAvant || (_idx & 31) == 0) && _sw.Elapsed.TotalMilliseconds >= BudgetMs) break;
            }
            if (_idx < _paires.Count) return;

            // hysteresis: a pair enters the published set only after two cycles in a row of the same masked verdict, and leaves it on
            // the first clear one. A verdict flickering at the edge of a wood then cannot chop a burst, and a target stepping into the
            // open is engaged again within one cycle.
            var publie = new HashSet<long>(_brut.Count);
            foreach (long k in _brut) { if (_brutPrecedent.Contains(k)) publie.Add(k); else _hysteresis++; }
            _masqueSol = publie;                                            // one assignment: the hook never sees a set being filled
            Interlocked.Exchange(ref _validUntilSol, Environment.TickCount64 + (long)ValidMs);
            // the two working sets swap and the older one is emptied: the hook reads neither of them, so neither has to be new. Only
            // "publie" above is ever a fresh object, because that one is handed over by reference without a lock.
            var tmp = _brutPrecedent; _brutPrecedent = _brut; _brut = tmp; _brut.Clear();
            _cycleEnCours = false;
            _cycles++;
            double s = now - _cycleDebut;
            if (s > _cycleMaxS) _cycleMaxS = s;
            if (_cache.Count > _cacheMax) _cacheMax = _cache.Count;
        }

        /// One pair. Fail-open at every step: off the map, out of budget or any doubt answers "clear" and nothing is ever cut.
        static bool Evaluer(in Paire p, float now, ref int marchesImage)
        {
            var t = _tireurs[p.T];
            if (t.Etat == 0)
            {
                // Building an INFANTRY eye is not just a map read: for a team on a building pixel it asks the game for the buildings
                // around it (a native call that allocates) and then walks up to 400 pixels of the one it stands in. That work is
                // charged as a line on all three budgets - otherwise an image could carry its whole budget of lines PLUS that on top,
                // and it is at stage 0 that the risk is highest, since that is the stage which queues infantry shooters.
                // A VEHICLE eye cannot take either path (the building branch is behind "infantry && near"): it is nine terrain reads
                // and some arithmetic, a tenth of a line, so it is counted but not charged - charging it, and charging the aim points
                // too, would have spent most of a cycle's budget on bookkeeping and left the lines themselves unwalked.
                _yeuxSol++;
                if (t.Infanterie) { marchesImage++; _marchesCycle++; _marchesCamp[p.Camp]++; }
                t.Oeil = VueAA.OeilSol(t.Eid, t.Pos, t.Infanterie, now, out int cell, out int haut);
                if (t.Oeil == null) t.Etat = 2;
                else { t.Etat = 1; t.Cellule = cell; t.Hauteur = haut; }
            }
            if (t.Etat != 1) { _pairesHorsCarte++; return false; }

            var c = _cibles[p.C];
            if (c.Etat == 0)
            {
                _viseesSol++;                                               // five array reads and a clamp: nothing native, nothing to cap
                if (VueAA.CibleSol(c.Pos, c.Infanterie, out float visee, out int kt, out int cell2, out int haut2))
                { c.Etat = 1; c.Visee = visee; c.Kt = kt; c.Cellule = cell2; c.Hauteur = haut2; }
                else c.Etat = 2;
            }
            if (c.Etat != 1) { _pairesHorsCarte++; return false; }

            // 19 bits of shooter cell, 19 of target cell, 12 of eye height and 12 of aim height, all quantised: a verdict computed for
            // a 12 m cell and a half-metre of height answers for the next unit that stands there. Terrain does not move.
            long key = ((long)t.Cellule << 43) | ((long)c.Cellule << 24) | ((long)t.Hauteur << 12) | (uint)c.Hauteur;
            long nowTick = Environment.TickCount64;
            bool aCache = false, verdictCache = false;
            if (_cacheActif)
            {
                if (_cache.TryGetValue(key, out long v))
                {
                    if ((v >> 1) + CacheLifeMs >= nowTick) { aCache = true; verdictCache = (v & 1L) != 0; _cacheHits++; }
                    else _cacheVides++;
                }
                else _cacheManques++;
            }

            bool controle = aCache && (_cacheHits % CheckEvery) == 0;       // one answer in 50 is recomputed exactly and compared
            if (aCache && !controle) { _marchesCache++; return verdictCache; }

            // Out of line budget: the pair answers "clear" and is computed in one of the next cycles. No cut.
            // The per-SIDE half matters as much as the total. The pairs are built side 0 first, so a single shared budget is always
            // spent on side 0's pairs: the moment it bites - a cold cache, a building coming down, a big battle - side 0 would be
            // judged and side 1 would answer "clear" for want of a line, cut harder in one direction, every time. And the two
            // per-side columns of the report, whose whole job is to prove the symmetry, would show it as a difference in terrain.
            if (_marchesCycle >= MaxWalksPerCycle || _marchesCamp[p.Camp] >= MaxWalksPerSideCycle)
            {
                if (aCache) { _marchesCache++; return verdictCache; }
                _pairesSansMarche++;
                return false;
            }

            double t0 = _sw.Elapsed.TotalMilliseconds;
            int cause = VueAA.MarcheSol(t.Oeil, c.Pos, c.Visee, c.Kt, p.Dist, out float foret);
            double dt = _sw.Elapsed.TotalMilliseconds - t0;
            if (dt > _marcheMaxMs) _marcheMaxMs = dt;
            _marches++; _marchesCycle++; _marchesCamp[p.Camp]++; marchesImage++;
            bool masque = cause != 0;
            if ((uint)cause < 4u) _parCause[cause]++;
            if (!masque && foret > 0f) _clairAvecForet++;
            if (controle)
            {
                _cacheControles++;
                if (masque != verdictCache) _cacheEcarts++;
                if (_cacheControles >= MinChecks && (double)_cacheEcarts / _cacheControles >= MaxDisagree)
                {
                    _cacheActif = false;
                    _cache.Clear();
                    Mod.Log.Warning($"[VUE-SOL] la réutilisation des vues par case de 12 m donne {100.0 * _cacheEcarts / _cacheControles:0.0} % de réponses différentes du calcul exact " +
                                    $"sur {_cacheControles} contrôles : elle est coupée pour cette bataille, chaque vue est calculée en entier");
                    return masque;
                }
            }
            if (_cacheActif)
            {
                if (_cache.Count >= MaxCacheEntries) _cache.Clear();        // no LRU, no allocation per entry: one cold cycle at most
                _cache[key] = (nowTick << 1) | (masque ? 1L : 0L);
            }
            return masque;
        }

        // ---------------------------------------------------------------- watchdog and safety line (once a cycle, main thread)

        /// FIVE conditions, all five needed. Judging on silence alone is the mistake LeurresAuto already paid for: an ordinary lull
        /// fires nothing at all, so it proves nothing, and the window has to restart instead of tripping.
        /// The denominator this module can see is narrow - only the CanonsSol rounds that reach the damage hook on a unit of the
        /// ground scan. It does not see artillery, mortars, rockets, missiles, rifles, air-to-ground, or anything landing on a
        /// helicopter. Five minutes of an infantry fight, of an artillery preparation or of air support would therefore look, to that
        /// denominator alone, exactly like a rule that had silenced every gun on the map. So the window also restarts on the GLOBAL
        /// impact counter: nothing is judged unless nothing at all landed anywhere, which is the only silence that means anything.
        static void ChienDeGarde()
        {
            if (_wdCoupe || !_coupe) { _wdBase = -1f; return; }
            float clock = AntiHeliPortee.HorlogeChienDeGarde;
            long cuts = Interlocked.Read(ref _cutSol), complets = Interlocked.Read(ref _askComplet), hits = Interlocked.Read(ref _touchesCanon);
            long impacts = AntiHeliPortee.Impacts;
            // 3 (a gun round landed on a ground unit), 4 (anything at all landed anywhere) and 5 (the sides are in contact) restart it
            if (_wdBase < 0f || !AntiHeliPortee.CampsAuContact || hits > _wdHits || impacts > _wdImpacts)
            { _wdBase = clock; _wdCuts = cuts; _wdComplets = complets; _wdHits = hits; _wdImpacts = impacts; return; }
            if (clock - _wdBase < WatchWindow) return;
            long fenCuts = cuts - _wdCuts, fenNonCoupes = (complets - _wdComplets) - fenCuts;
            // 1 (the rule really acted) and 2 (the engine really kept answering) must both hold, or nothing is proved
            if (fenCuts < WatchMinCuts || fenNonCoupes < WatchMinUncut)
            { _wdBase = clock; _wdCuts = cuts; _wdComplets = complets; _wdHits = hits; _wdImpacts = impacts; return; }
            _wdCoupe = true;
            _coupe = false;
            Mod.Log.Warning($"[VUE-SOL] chien de garde : {WatchWindow:0} secondes de jeu sans le moindre impact dans la bataille et sans qu'un seul obus de tir direct " +
                            $"soit vu arriver par le mod sur une unité au sol, alors que les deux camps étaient au contact, que {fenNonCoupes} demandes de portée " +
                            $"sont passées sans être coupées pendant ce temps et que la règle en a coupé {fenCuts} : la vue propre au sol est coupée jusqu'à la fin " +
                            "de la bataille, les portées redeviennent celles du jeu. Rien n'est enregistré.");
        }

        /// Warning only, nothing is switched off: the forest may genuinely be blocking. Three a battle at most.
        static void LigneDeSurete()
        {
            if (!_coupe || _sureteAlertes >= SafetyMaxWarnings) return;
            int camp = _campJoueur;
            if (camp < 0 || camp > 1) { _sureteBase = -1f; return; }
            float clock = AntiHeliPortee.HorlogeChienDeGarde;
            long hits = _touchesCamp[1 - camp];                             // rounds landed on the OTHER side: the player's own shots
            long eval = _evalCamp[camp], masq = _masqCamp[camp];
            bool beaucoupMasque = eval > 0 && (double)masq / eval >= SafetyMaskedShare;
            if (_sureteBase < 0f || hits > _sureteHits || !beaucoupMasque) { _sureteBase = clock; _sureteHits = hits; return; }
            if (clock - _sureteBase < SafetyWindow) return;
            _sureteBase = clock; _sureteHits = hits;
            _sureteAlertes++;
            Mod.Log.Warning($"[VUE-SOL] sécurité : aucun obus de tir direct de votre camp n'a été vu arriver par le mod depuis {SafetyWindow:0} s alors que " +
                            $"{100.0 * masq / Math.Max(1L, eval):0} % de vos paires évaluées sont masquées : la règle de vue propre au sol reste active, rien n'est coupé " +
                            "en plus (la forêt, les bâtiments ou le relief peuvent vraiment empêcher de voir)");
        }

        // ---------------------------------------------------------------- report

        static void MajScript()
        {
            // MEASUREMENT only, and deliberately NOT a filter (see the header): the figure is here so that if a mission ever stalls it
            // is already in the log.
            _scriptCamp[0] = 0; _scriptCamp[1] = 0;
            float gameNow;
            try { gameNow = UnityEngine.Time.time; } catch { return; }
            foreach (var t in _tireurs)
            {
                try { if ((uint)t.Camp < 2u && Missions.ScriptReason(t.Uid, gameNow) != null) _scriptCamp[t.Camp]++; }
                catch { return; }
            }
        }

        static void Rapport(bool final)
        {
            if (!final && _cycles == 0 && Interlocked.Read(ref _appels) == 0) return;
            MajScript();
            long appels = Interlocked.Read(ref _appels), solSol = Interlocked.Read(ref _solSol);
            long cibleSol = Interlocked.Read(ref _cibleAuSol), tireurSol = Interlocked.Read(ref _tireurAuSol);
            long elig = Interlocked.Read(ref _askEligible), canon = Interlocked.Read(ref _askCanon), complets = Interlocked.Read(ref _askComplet);
            long vus = Interlocked.Read(ref _masqueVu), aurait = Interlocked.Read(ref _auraitCoupe), coupes = Interlocked.Read(ref _cutSol);
            long[] askCamp = { Interlocked.Read(ref _askSide0), Interlocked.Read(ref _askSide1) };
            long[] masqueCamp = { Interlocked.Read(ref _masqueSide0), Interlocked.Read(ref _masqueSide1) };
            long[] coupeCamp = { Interlocked.Read(ref _cutSide0), Interlocked.Read(ref _cutSide1) };

            var sb = new StringBuilder();
            sb.Append(final ? "bilan : " : "relevé : ");
            // careful with this sentence: it speaks for THIS rule only. Other rules of the mod (the manual-aim cap on ground targets,
            // for one) do shorten ground ranges in the same battle, and saying "the ground ranges are the game's own" would be false.
            sb.Append(_etape == 0
                ? "ÉTAPE 0, MESURE SEULE : rien n'est coupé PAR CETTE RÈGLE, aucune portée au sol n'est raccourcie par la vue propre au sol"
                : _etape == 1
                    ? $"étape 1 : seuls les véhicules au-delà de {FloorVehicle:0} m peuvent être coupés (paires prises à partir de {QueueVehicle:0} m)"
                    : $"étape 2 : véhicules au-delà de {FloorVehicle:0} m et infanterie au-delà de {FloorInfantry:0} m (paires prises à partir de {QueueVehicle:0} et {QueueInfantry:0} m)");
            if (_arret) sb.Append($" ; ARRÊTÉE ({_raisonArret})");
            else if (_wdCoupe) sb.Append(" ; COUPÉE par son chien de garde pour cette bataille");
            else if (_etape >= 1 && !_coupe) sb.Append(" ; en mesure seule pour l'instant (" + RaisonPasDeCoupe() + ")");

            sb.Append($" ; appels du calcul de portée vus {appels} (cible au sol {cibleSol}, tireur au sol {tireurSol}, les deux {solSol}" +
                      $"{(appels > 0 ? $" = {100.0 * solSol / appels:0} %" : "")}), dont tireur éligible {elig}, obus à tir direct {canon}, " +
                      $"arrivés jusqu'au verdict {complets} ; vue masquée trouvée {vus} " +
                      $"({(_coupe ? "" : $"auraient été coupés {aurait}, ")}coupés {coupes}) ; " +
                      $"verdicts périmés {Interlocked.Read(ref _perime)}, tireur ou cible touché à l'instant {Interlocked.Read(ref _skipTouche)}");

            sb.Append($" ; cycles {_cycles} (abandonnés faute d'image {_cyclesAbandonnes}), paires en file {_pairesEnFile} (au plus {_pairesMax} dans un cycle, plafond {MaxPairsTotal}), " +
                      $"évaluées {_pairesEvaluees} dont masquées {_pairesMasquees}{(_pairesEvaluees > 0 ? $" ({100.0 * _pairesMasquees / _pairesEvaluees:0} %)" : "")}, " +
                      $"retenues seulement au 2e cycle de suite {_hysteresis}, sans calcul faute de budget {_pairesSansMarche}, hors carte {_pairesHorsCarte}, " +
                      $"navires écartés {_naviresEcartes}");
            sb.Append($" ; première cause : forêt {_parCause[1]}, relief {_parCause[2]}, bâtiment {_parCause[3]} ; vue dégagée mais avec un peu de forêt {_clairAvecForet}");

            sb.Append(" ; par distance (évaluées/masquées) :");
            for (int b = 0; b < Bands; b++)
                if (_bandeEval[b] > 0) sb.Append($" {b}-{(b == Bands - 1 ? "+" : (b + 1).ToString())} km {_bandeEval[b]}/{_bandeMasq[b]}");
            sb.Append($" ; par tireur : véhicules {_evalVeh} évaluées dont {_masqVeh} masquées{(_evalVeh > 0 ? $" ({100.0 * _masqVeh / _evalVeh:0} %)" : "")}, " +
                      $"infanterie {_evalInf} dont {_masqInf}{(_evalInf > 0 ? $" ({100.0 * _masqInf / _evalInf:0} %)" : "")}");
            // the decisive figure, and the one the ceiling in Etat() judges on: VEHICLE pairs just above the floor
            sb.Append($" ; JUSTE AU-DESSUS DU PLANCHER ({QueueVehicle:0}-{NearBand:0} m), véhicules : {_procheEvalVeh} évaluées, {_procheMasqVeh} masquées " +
                      $"{(_procheEvalVeh > 0 ? $"({100.0 * _procheMasqVeh / _procheEvalVeh:0} %)" : "(pas encore de mesure)")} " +
                      $"— limite pour COMMENCER à couper : {100.0 * (MaxMasqueProche - PlafondMarge):0} % sur au moins {MinProcheControles} mesures " +
                      $"(et {100.0 * MaxMasqueProche:0} % pour continuer une fois commencé)");
            if (_procheEvalInf > 0)
                sb.Append($" ; les mêmes pour l'infanterie (pour information, l'étape 1 ne les concerne pas) : {_procheEvalInf} évaluées, " +
                          $"{_procheMasqInf} masquées ({100.0 * _procheMasqInf / _procheEvalInf:0} %)");

            sb.Append($" ; lignes réellement calculées {_marches}, yeux construits {_yeuxSol} (ceux de l'infanterie comptent comme une ligne dans le budget), " +
                      $"points visés construits {_viseesSol} ; budget {MaxWalksPerCycle} lignes par cycle dont {MaxWalksPerSideCycle} par camp et {MaxWalksPerFrame} par image ; " +
                      $"réponses réutilisées {_marchesCache} ; réutilisation des vues " +
                      $"{(!_cacheOn.Value ? "coupée par les réglages" : _cacheActif ? "active" : "coupée automatiquement")} " +
                      $"({_cache.Count} en mémoire, au plus {_cacheMax} sur {MaxCacheEntries}, trouvées {_cacheHits}, absentes {_cacheManques}, trop vieilles {_cacheVides}), " +
                      $"contrôles {_cacheControles} dont écarts {_cacheEcarts}{(_cacheControles > 0 ? $" ({100.0 * _cacheEcarts / _cacheControles:0.0} %)" : "")}");

            // the symmetry proof: the two columns are meant to be read side by side
            sb.Append($" ; par camp (camp du joueur : {((uint)_campJoueur < 2u ? _campJoueur.ToString() : "inconnu")}) :");
            for (int s = 0; s < 2; s++)
                sb.Append($" camp {s} : tireurs éligibles {_tireursCamp[s]} au dernier cycle, {_tireursCumul[s]} en tout (tenus par le script {_scriptCamp[s]}), " +
                          $"cibles écartées par le plafond {_ciblesPlafond[s]}, paires en file {_pairesCamp[s]} " +
                          $"(écartées par le plafond {_pairesPlafond[s]}), évaluées {_evalCamp[s]}, masquées {_masqCamp[s]}, " +
                          $"demandes de portée {askCamp[s]}, vues masquées au tir {masqueCamp[s]}, coupés {coupeCamp[s]}, " +
                          $"obus à tir direct arrivés sur lui {_touchesCamp[s]} ;");
            sb.Append(Ecart(masqueCamp, coupeCamp));

            sb.Append($" ; touches au sol vues {Interlocked.Read(ref _touchesSol)} (dont obus à tir direct {Interlocked.Read(ref _touchesCanon)}), " +
                      $"unités touchées récemment {_touchesRecentes.Count}, touches perdues (anneau plein) {Interlocked.Read(ref _anneauPerdus)}");
            sb.Append($" ; image la plus longue {_frameMaxMs:0.00} ms, ligne la plus longue {_marcheMaxMs:0.000} ms, cycle le plus long {_cycleMaxS:0.00} s " +
                      "(ce temps est compté dans la ligne VueAA du relevé des performances)");
            sb.Append($" ; munitions à tir direct retenues {_canonsSol.Count}, erreurs {Interlocked.Read(ref _err)}, alertes de sécurité {_sureteAlertes}");
            if (_etape == 0)
                sb.Append($" ; RIEN N'EST COUPÉ À CETTE ÉTAPE PAR LA VUE PROPRE AU SOL. Pour passer à l'étape 1, ce relevé doit montrer : " +
                          "« les deux » au-dessus de 60 %, image la plus longue sous 1 ms (c'est bien l'image entière qui est mesurée, " +
                          "construction des paires et relevé compris), cycle le plus long sous 1 s, écarts de réutilisation sous 5 % sur au moins " +
                          $"2000 contrôles, 0 erreur, les deux colonnes par camp du même ordre, et surtout la part masquée JUSTE AU-DESSUS DU PLANCHER " +
                          $"CHEZ LES VÉHICULES (la seule population de l'étape 1) en dessous de {100.0 * (MaxMasqueProche - PlafondMarge):0} % sur au moins {MinProcheControles} mesures. " +
                          "Cette dernière n'est pas qu'une consigne : tant qu'elle n'est pas tenue, la règle refuse de couper même à l'étape 1");
            string s2 = sb.ToString();
            if (!final && s2 == _dernierRapport) return;
            _dernierRapport = s2;
            if (!_entete)
            {
                _entete = true;
                Log($"règle de vue propre au sol : plancher garanti {FloorVehicle:0} m pour les véhicules et {FloorInfantry:0} m pour l'infanterie — aucune paire n'est même " +
                    $"mise en file en dessous de {QueueVehicle:0} et {QueueInfantry:0} m, pour que même un verdict aussi vieux que le permet le calcul ({ClosureMargin:0} m de rapprochement) " +
                    $"ne puisse jamais couper sous le plancher annoncé ; portée maximale {MaxRange:0} m, " +
                    $"cycle {CycleSeconds:0.#} s, verdict valable {ValidMs / 1000f:0.#} s, au plus {MaxWalksPerCycle} lignes par cycle dont {MaxWalksPerSideCycle} PAR CAMP et {MaxWalksPerFrame} par image, " +
                    $"budget {BudgetMs:0.#} ms par image, au plus {MaxShootersPerSide} tireurs, {MaxTargetsPerSide} cibles et {MaxPairsPerSide} paires PAR CAMP ; " +
                    "jamais l'artillerie, les mortiers, les lance-roquettes multiples, les bombes ni le moindre missile (une seule vérification : tir direct, " +
                    "sans autodirecteur, capable de toucher une unité au sol) ; jamais un fusil, un fusil de précision, une roquette antichar ni une munition " +
                    "anti-aérienne dédiée ; jamais un navire ; jamais une unité en train d'être touchée, ni celle qui tire sur elle (une unité qui vient " +
                    "d'encaisser peut riposter) ; identique pour les deux camps ; aucun crochet propre, la règle se greffe sur celui des portées anti-hélico, " +
                    "et ses coupures sont comptées à part, jamais dans le compteur du chien de garde anti-hélico");
            }
            Log(s2);
        }

        static string RaisonPasDeCoupe()
        {
            if (_wdCoupe) return "coupée par son chien de garde";
            if (!VueAA.ReadyForGating) return "carte pas encore utilisable : " + VueAA.GateReason;
            if (AntiHeliPortee.ReglesBloquees) return "les règles anti-hélico sont bloquées (chien de garde, preuve enregistrée ou compteur d'impacts absent)";
            if (!AntiHeliPortee.CrochetDegats) return "le calcul des dégâts n'est pas accroché : la règle ne saurait pas qu'une unité est en train d'être touchée";
            if (_canonsSol.Count == 0) return "aucune munition à tir direct retenue";
            if (_cacheActif && _cacheControles >= MinChecks && (double)_cacheEcarts / _cacheControles >= MaxDisagree) return "la réutilisation des vues donne trop d'écarts";
            if (_procheEvalVeh < MinProcheControles)
                return $"pas encore assez de vues entre véhicules juste au-dessus du plancher ({_procheEvalVeh}/{MinProcheControles}) pour savoir si la règle serait jouable";
            double limite = _plafondEtat == 2 ? MaxMasqueProche : MaxMasqueProche - PlafondMarge;
            if ((double)_procheMasqVeh / _procheEvalVeh > limite)
                return $"{100.0 * _procheMasqVeh / _procheEvalVeh:0} % des vues entre véhicules juste au-dessus du plancher sont masquées, au-dessus de la limite de {100.0 * limite:0} % : carte trop couverte pour le modèle actuel";
            return "en attente";
        }

        /// Cuts (or, while measuring, masked views seen by the hook) per eligible shooter AND per cycle, side 0 against side 1. This
        /// figure is the whole point of the line above: above x2 and sustained, a cap is biting one side, or a budget is being spent
        /// on one side before the other, or the two lists are not built the same way.
        static string Ecart(long[] masqueCamp, long[] coupeCamp)
        {
            // both halves have to cover the same span of the battle. The numerators run from the first cycle; the denominator must
            // therefore be the CUMULATIVE count of eligible shooters, not the last cycle's, which is reassigned every second: a side
            // losing its shooters in the last cycle would otherwise show a ratio that explodes, and raise a false alarm - or hide a
            // real one.
            double a = _tireursCumul[0] > 0 ? (double)Math.Max(coupeCamp[0], masqueCamp[0]) / _tireursCumul[0] : 0;
            double b = _tireursCumul[1] > 0 ? (double)Math.Max(coupeCamp[1], masqueCamp[1]) / _tireursCumul[1] : 0;
            if (a <= 0 && b <= 0) return " écart entre les camps : rien à comparer pour l'instant";
            double hi = Math.Max(a, b), lo = Math.Min(a, b);
            string txt = $" écart entre les camps : par tireur éligible et par cycle {a:0.00} contre {b:0.00}";
            if (lo <= 0) return txt + " (un seul camp concerné)";
            double x = hi / lo;
            txt += $" (x{x:0.00})";
            if (x >= 2.0 && _tireursCumul[0] > 0 && _tireursCumul[1] > 0)
                txt += " — AU-DESSUS DE x2 : à vérifier (les deux camps peuvent vraiment être dans des terrains différents, le mod ne devine rien)";
            return txt;
        }
    }
}
