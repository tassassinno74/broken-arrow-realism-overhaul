// RealismOverhaul - critical damage: damage that lands somewhere instead of only on a health bar. MEASUREMENT ONLY,
//  logs [CRITIQUES]. Nothing in the game is read through Get<T>, nothing is written, no journaled lot, nothing to restore.
//
//  WHY NOTHING IS WRITTEN. The engine already has this feature, end to end, and it is already raised by combat damage:
//   - the state is an ECS component, Client.Ecs.BattleSystem.Components.CriticalEffectComponent, holding
//     FastList<CriticalDamage> CriticalDamages and Dictionary<CriticalEffectTypes,int> CriticalEffectStatus, with
//     AddCriticalEffect(type, value) and SetCriticalEffectsZero();
//   - CriticalEffectTypes has exactly four members: Optics, Mobility, Targeting, Loading - the four sliders the mission
//     editor exposes as OpticsCrit / MobilityCrit / AimingCrit / ReloadCrit on its health node;
//   - one damage event becomes one CriticalDamage through
//     CriticalDamage(Single hitPointLossRate, Single ammunitionCriticMultiplier, ShooterInfo& shooter): the share of
//     health the hit removed, times the ammunition's own crit weight. That constructor is a combat constructor, not a
//     script one;
//   - CriticalUpdateComponent { bool DamageReceived; bool UnitRepaired; } is the trigger: damage raises it, a repair
//     clears it;
//   - CriticalEffectSystem (an EcsSetSystem<float> with its own Random, a BuffConfig, UNIT_CRIT_SLOTS_COUNT slots per
//     unit and IsYellowCrit) rolls the dice and fills the status; BuffConfig.CriticalBaseProbability is the base chance
//     and RedCritsActivationThreshold separates a yellow (minor) crit from a red (major) one;
//   - BuffDebuffSystem.SetCriticalModifiers then ApplyBuffModifiers turn the status into the effects the player feels,
//     named in BuffConfig: Minor/MajorOpticsMultiplier (blinded), MinorMobilityMultiplier and
//     MinorMobilityMultiplierRotation (tracks and turret drive), Minor/MajorAimTimeMultiplier and
//     Minor/MajorDispersionMultiplier (the gun no longer points straight), Minor/MajorLoadingMultiplier (slower reload);
//   - LabelSystem.UpdateCriticalEffects paints them on the unit label and NetworkUnitService.SendUnitGetCriticalEffect /
//     RemoteUnitGetCriticalEffect sync them between players.
//  And the weighting by what actually hit is already in the data. Measured on the game's own exported table
//  (UserData\RealismOverhaul_stats\Resources default\Ammunitions.csv, 630 rows): CriticMultiplier is 0 on 10 rows
//  (smoke, napalm, incendiary, flashbang - those can never crit), 0.33 on 7 (automatic grenade launchers) and 0.5 on 7
//  (thermobaric MLRS), 1 on 468 (rifle and machine-gun rounds, tank AP and HE), 2 on 24 (light AT rockets), 3 on 11
//  (57 mm HE), 5 on 44 (23 to 35 mm autocannon), 10 on 4, 98 on 19 (disposable HEAT rockets: RPG-18, RPG-27, Carl
//  Gustaf), 99 on 14 (cluster shells and bombs), 199 on 13 and 202 on 9 (heavy cluster bombs, cluster MLRS, Kinzhal).
//  A rifle round and an RPG on the same vehicle are already about 1 to 98 apart, and identical for both sides because
//  it is one shared ammunition table.
//  What is NOT in any file, and therefore not knowable without playing, is the runtime scale: the value of
//  CriticalBaseProbability, of RedCritsActivationThreshold, of UNIT_CRIT_SLOTS_COUNT and of the ten Minor/Major
//  multipliers. Writing a new probability into a scale nobody has read yet is how a bomb blast that today scratches a
//  company would tomorrow immobilise it permanently. So this version reads those numbers, proves in the player's own
//  battle how often a crit really lands and how hard it bites, and changes nothing.
//
//  WHAT THE NEXT VERSION WOULD TURN, once the log below gives the numbers. One knob only, BuffConfig's own values,
//  journaled under a lot of this module, identical for both sides, with these bounds:
//   - a rifle or machine-gun round on a vehicle must stay under 0.1 % per hit (a glancing hit never immobilises);
//   - a 23 to 35 mm autocannon burst on a light vehicle: about 2 to 5 % per burst;
//   - an RPG / AT4 / ATGM hit that hurts without killing: about 25 to 35 %, so a hit that lands is felt;
//   - never more than 2 crits at once on one unit, and MinorMobilityMultiplier never below 0.50, so a crippled unit
//     can always be driven out of the fight. The engine may already enforce the first half through
//     UNIT_CRIT_SLOTS_COUNT: the report says which value it holds, and the bound is only written if the engine's own
//     cap is looser.
//
//  HOW IT MEASURES.
//   - Rules, once per battle: GameConfig.Instance.BuffConfig read by reflection (a renamed field cannot break the
//     build), plus CriticalEffectSystem's static UNIT_CRIT_SLOTS_COUNT and AllCriticalEffects looked up by type name.
//   - Ammunition, once per battle: CriticMultiplier of every row, banded, with a few names per band.
//   - Hits: a postfix on BattleSystemHelpers.CalculateHitDamage at Priority.Last, declared "after" the two other
//     Priority.Last owners of that chain (ProtectionMission, which writes a zero for a protected mission unit, and
//     AntiHeliPortee, which multiplies for a helicopter in low flight), so the value read is the one the game really
//     applies and a hit the mission protection cancelled is not counted at all. Same proven path as Couvert.cs,
//     Retranchement.cs, AntiHeliPortee.cs and ProtectionMission.cs. It only pushes plain values into a ring: no
//     allocation, no Unity call, no logging, and it never writes __result.
//   - Bad conditions: the main thread, every 0.5 s, on slices of 120 units of the shared DegatsScan, calls the engine's
//     own reader LuaUnit.GetBadConditionsCount(false) and (true). It is a plain managed int call on the Lua facade,
//     never Get<T> on a component, and GetHealPercentage gives the share of health the hits removed in the same window.
//     WHAT THAT READER REALLY COUNTS, in the game's own words (Manual/Data/MissionEditor/3. Lua/07. LuaUnit.md, lines
//     145-149): "count of problematic conditions on unit. That includes types of crit, plus abilities that can run out
//     of charges, like APS, or flares". So a helicopter that burns its last flare volley - which this mod's own
//     LeurresAuto can order - raises the same counter as a crit, and the mod cannot tell them apart. Every number
//     below is therefore a MAXIMUM, and every French line says "mauvaise condition", never "critique".
//     The one clean signal is the second call: with mobilityCritLevelOnly = true the reader returns the mobility crit
//     LEVEL (1 yellow, 2 red), which no flare and no APS can raise. A rise of that level is certainly a crit, and it is
//     counted and printed on its own line.
//   - Attribution: a rise of the count is charged to the heaviest ammunition band that hit that unit since its own
//     previous poll. A rise on a unit that has never been hit at all is charged to "no shot seen" - that is the
//     discriminator that answers whether anything other than a mission script ever sets a crit.
//   - The two rates. A burst of 30 autocannon shells landing between two polls of the same unit is 30 hits but at most
//     one rise, so "per shot" collapses for automatic weapons and stays intact for single-shot ones. Both ends are
//     printed: "per shot" over the hits, "per burst" over the windows (one window = the hits seen on one unit between
//     two of its own polls). Infantry is left out of both denominators: the engine only puts crits on units that can
//     carry them, and infantry takes most of the hits (the mod's own [CALIBRE-MESURE] line: 153 vehicles / 352 infantry
//     out of 505 impacts), which would divide the rate by about three.
//  Reports every 60 s and at the end of the battle. Error kill-switch, crash guard, own Harmony id installed lazily in
//  battle, solo campaign only, silent in a mission played without the mod.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    static class Critiques
    {
        const string GuardVersion = "1.0";
        const int MaxErrors = 50;
        const int QueueSize = 4096;                                  // power of two
        const int Slice = 120;                                       // units polled per 0.5 s tick
        const int MaxSuivis = 20000;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ammunition bands, by the game's own CriticMultiplier
        const byte BandNone = 0, BandUnder = 1, BandOne = 2, BandLight = 3, BandMedium = 4, BandAt = 5, BandBomb = 6, NBand = 7;
        static readonly string[] BandNames =
        {
            "aucun critique possible (0)",
            "sous une balle (0,33 à 0,5)",
            "balles et obus ordinaires (1)",
            "roquettes légères et canons automatiques (2 à 5)",
            "gros calibres (5 à 20)",
            "roquettes antichars et missiles (20 à 120)",
            "bombes et sous-munitions lourdes (plus de 120)",
        };

        // target kinds, for the hit counts
        const byte KindUnknown = 0, KindInfantry = 1, KindVehicle = 2, KindHeli = 3, KindPlane = 4, KindOther = 5, NKind = 6;
        static readonly string[] KindNames = { "inconnues", "infanterie", "véhicules", "hélicoptères", "avions", "autres" };

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        static HarmonyLib.Harmony _harmony;
        static bool _patchTried, _damagePatched, _refused, _everOnline, _sessionArmed, _netLogged;
        static bool _pollBroken, _offLogged, _depthWarned, _rulesRead, _ammoRead;
        static float _nextTick, _nextReport, _battleStartReal;
        static int _wait, _cursor;

        // ---------------------------------------------------------------- hook side (immutable snapshots, plain counters)
        static volatile bool _armed, _off;
        static volatile int _mainThread;
        static volatile byte[] _band = Array.Empty<byte>();          // ammo Id -> band, published as a whole before arming

        struct Ev { public long Seq; public int Eid, Ammo; public float Dmg; public byte Band; }
        static readonly Ev[] _ev = new Ev[QueueSize];
        static long _evHead, _evRead, _evLost;
        static long _offMain, _hits, _errors;

        // ---------------------------------------------------------------- measurement (main thread only)
        /// One followed unit. Keyed by UID, which survives the rebuild of the shared scan; Eid is what the hook sees.
        sealed class Suivi
        {
            internal int Uid;
            internal string Nom = "?";
            internal byte Kind;
            internal int Bad = -1, Mob = -1;                          // last poll, -1 = never polled
            internal int PeakBad;
            /// Highest mobility crit LEVEL seen (1 yellow, 2 red), not a number of crits: the reader returns a level.
            internal int NiveauMobMax;
            internal float LastPoll, PeakAt = -1f, PeakHeld;
            internal double PorteTotal;                               // seconds spent carrying at least one crit
            internal bool Revenu;                                     // came back to zero after carrying at least one
            internal long TirsVus;                                    // hits seen on this unit since the start of the battle
            // window since this unit's own previous poll: what hit it, and how hard
            internal int FenTirs, FenAmmo; internal byte FenBande; internal float FenDegats;
            internal int SantePrec = -1;
        }

        static readonly Dictionary<int, Suivi> _suivis = new();
        static readonly Dictionary<int, byte> _kindOfEid = new();
        struct Pend { public int Tirs, Ammo; public byte Bande; public float Degats; }
        static readonly Dictionary<int, Pend> _pending = new();       // hits waiting for the next poll of their unit

        static readonly long[,] _hitsBand = new long[NBand, NKind];   // hits seen, per band and target kind
        static readonly double[,] _dmgBand = new double[NBand, NKind];
        static readonly long[] _monteesBand = new long[NBand];        // bad-condition rises charged to a band
        static readonly long[] _fenetresBand = new long[NBand];       // windows (bursts) charged to a band: the honest denominator
        static readonly long[] _mobBand = new long[NBand];            // of those rises, the ones where the mobility level rose too
        static readonly long[] _niveauxBand = new long[NBand];        // levels gained with them
        static readonly double[] _perteBand = new double[NBand];      // share of health removed in the winning window
        static readonly double[] _degatsBand = new double[NBand];     // damage of the winning window (the other half of the same ratio)
        static readonly long[] _perteN = new long[NBand];
        static readonly Dictionary<int, long> _coupables = new();     // ammo Id -> crit rises charged to it
        static long _monteesVues, _monteesAnciennes, _monteesSansTir, _descentes, _polls, _pollErr;
        static long _monteesMob;                                      // rises where the mobility level rose too: certainly crits
        static long _monteesInf;                                      // rises seen on infantry, left out of the two denominators
        static long _ecartsIgnores;                                   // polls too far apart to be a sweep: their carry time is lost
        static int _pleines;                                          // units seen at the engine's own slot count

        // rules read from the game, in the game's own words
        static float _probaBase, _optMin, _optMaj, _mobMin, _mobRot, _viseMin, _viseMaj, _dispMin, _dispMaj, _chargeMin, _chargeMaj;
        static int _seuilRouge, _slots = -1, _effets = -1;
        static bool _buffLu;
        static readonly int[] _bandRows = new int[NBand];
        static string _lastReport;

        static void Log(string s) => Mod.Log.Msg("[CRITIQUES] " + s);
        static string F(double v) => v.ToString("0.##", Inv);
        static string F3(double v) => v.ToString("0.###", Inv);
        static string Pct(double v) => (v * 100.0).ToString("0.###", Inv) + " %";
        static string N(long v) => v.ToString(Inv);

        // ---------------------------------------------------------------- lifecycle
        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Critiques");
            _enabled = c.CreateEntry("MesureCritiques", true, description: Build.Desc(
                "Mesure les dégâts critiques du jeu (optique, mobilité, visée, chargement) : lit les règles du moteur et compte les mauvaises conditions réellement subies. Ne change rien dans le jeu.",
                "Mesure seulement, ne change rien"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            _mainThread = Environment.CurrentManagedThreadId;
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        internal static void OnBattleEnd() => EndBattle("fin de bataille");

        static void EndBattle(string why)
        {
            _armed = false;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean.Value != 0) { _unclean.Value = 0; try { MelonPreferences.Save(); } catch { } }
            try
            {
                Drain();
                float now = UnityEngine.Time.realtimeSinceStartup;
                foreach (var s in _suivis.Values) CloseCarry(s, now);
                Report(true);
            }
            catch (Exception e) { Log("bilan illisible : " + e.GetBaseException().Message); }
            Log($"fin de bataille ({why})");
        }

        /// Every frame in campaign. Drains the hook queue; rules, ammunition table, unit poll and reports every 0.5 s.
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _refused) { if (_armed) _armed = false; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_sessionArmed) Drain();
            if (now < _nextTick) return;
            // one heavy module job per frame (Planif.cs): same 0.5 s period as the others, just not in the same frame
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _nextTick = now + 0.5f;
            _mainThread = Environment.CurrentManagedThreadId;

            var gc = GameController._instance;
            if (gc?._GameSession_k__BackingField?.CurrentPlayer == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_armed) { _armed = false; Log("partie en ligne : mesure coupée"); } return; }
            if (!_sessionArmed && !StartBattle(now)) return;

            if (!_rulesRead) { _rulesRead = true; ReadRules(); }
            if (!_ammoRead) ReadAmmo();
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            if (_refused) return;

            if (Interlocked.Read(ref _errors) >= MaxErrors) _off = true;
            if (_off)
            {
                _armed = false;
                if (!_offLogged)
                {
                    _offLogged = true;
                    Mod.Log.Warning($"[CRITIQUES] {MaxErrors} erreurs dans le correctif : mesure coupée jusqu'au redémarrage du jeu (le jeu n'était de toute façon pas modifié)");
                }
                return;
            }
            // never armed before the ammunition table is in hand: a hit that cannot be classed would be counted as an
            // ordinary round and would falsify the whole measurement
            _armed = _damagePatched && _band.Length > 0;

            var scan = DegatsScan.Get(now);
            Index(scan);
            Drain();                                                                 // hits of the last 0.5 s before the poll
            Poll(scan, now);

            if (!_depthWarned && now - _battleStartReal > 15f && !LeurresMesure.HitDepthReady)
            {
                _depthWarned = true;
                Log("profondeur d'impact indisponible (mesure des leurres coupée) : les mauvaises conditions sont comptées, mais aucun tir ne peut leur être attribué");
            }
            if (now >= _nextReport) { _nextReport = now + 60f; Report(false); }
        }

        static bool StartBattle(float now)
        {
            if (_unclean.Value >= 2)
            {
                _refused = true;
                _armed = false;
                Mod.Log.Warning("[CRITIQUES] désactivé : les deux dernières parties ne se sont pas terminées normalement");
                return false;
            }
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;
            MelonPreferences.Save();
            ClearBattle();
            _battleStartReal = now;
            _nextReport = now + 60f;
            DegatsAmmoCache.NewBattle();
            Log("prêt : mesure seule des dégâts critiques (optique, mobilité, visée, chargement), les deux camps, rien n'est changé dans le jeu");
            Log("règle de lecture : le compteur du jeu additionne les critiques ET les capacités épuisées (APS, leurres épuisés) ; " +
                "ce relevé est donc un maximum. Seul le niveau de mobilité (1 jaune, 2 rouge) ne peut venir que d'un critique : " +
                "c'est la ligne « dont ... sûrement des critiques de mobilité » qui répond vraiment à la question");
            return true;
        }

        static bool Solo()
        {
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _everOnline = true;
                if (!_netLogged) { _netLogged = true; Log($"réseau : état={st} -> {(_everOnline ? "en ligne" : "solo")}"); }
            }
            catch { return false; }
            return !_everOnline;
        }

        // ---------------------------------------------------------------- the game's own rules (measure before acting)
        /// Reads BuffConfig by reflection: a field renamed in a later patch of the game leaves the line as "absent"
        /// instead of breaking the module.
        static void ReadRules()
        {
            try
            {
                var buff = GameCfg.Instance?.BuffConfig;
                if (buff == null) { Log("règles des critiques : BuffConfig illisible, les réglages du jeu ne peuvent pas être relevés"); }
                else
                {
                    _buffLu = true;
                    _probaBase = (float)(Props.Num(buff, "CriticalBaseProbability") ?? 0.0);
                    _seuilRouge = (int)(Props.Num(buff, "RedCritsActivationThreshold") ?? 0.0);
                    _optMin = (float)(Props.Num(buff, "MinorOpticsMultiplier") ?? 1.0);
                    _optMaj = (float)(Props.Num(buff, "MajorOpticsMultiplier") ?? 1.0);
                    _mobMin = (float)(Props.Num(buff, "MinorMobilityMultiplier") ?? 1.0);
                    _mobRot = (float)(Props.Num(buff, "MinorMobilityMultiplierRotation") ?? 1.0);
                    _viseMin = (float)(Props.Num(buff, "MinorAimTimeMultiplier") ?? 1.0);
                    _viseMaj = (float)(Props.Num(buff, "MajorAimTimeMultiplier") ?? 1.0);
                    _dispMin = (float)(Props.Num(buff, "MinorDispersionMultiplier") ?? 1.0);
                    _dispMaj = (float)(Props.Num(buff, "MajorDispersionMultiplier") ?? 1.0);
                    _chargeMin = (float)(Props.Num(buff, "MinorLoadingMultiplier") ?? 1.0);
                    _chargeMaj = (float)(Props.Num(buff, "MajorLoadingMultiplier") ?? 1.0);
                }
            }
            catch (Exception e) { Log("règles des critiques illisibles : " + e.GetBaseException().Message); }

            try
            {
                var t = AccessTools.TypeByName("Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.CriticalEffectSystem");
                if (t != null)
                {
                    var p = AccessTools.Property(t, "UNIT_CRIT_SLOTS_COUNT");
                    if (p != null && p.GetValue(null) is int n) _slots = n;
                    var q = AccessTools.Property(t, "AllCriticalEffects");
                    var arr = q?.GetValue(null);
                    if (arr != null)
                    {
                        var len = arr.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                        if (len?.GetValue(arr) is int m) _effets = m;
                    }
                }
            }
            catch { _slots = -1; _effets = -1; }

            Log($"règles du jeu (lecture seule) : chance de base {(_buffLu ? F3(_probaBase) : "illisible")}, " +
                $"seuil du critique rouge {(_buffLu ? N(_seuilRouge) : "illisible")}, " +
                $"emplacements de critiques par unité {(_slots >= 0 ? N(_slots) : "illisible")}, " +
                $"types de critiques {(_effets >= 0 ? N(_effets) : "illisible")} (optique, mobilité, visée, chargement)");
            if (_buffLu)
                Log($"effets du jeu (lecture seule) : optique x{F3(_optMin)} jaune / x{F3(_optMaj)} rouge ; mobilité x{F3(_mobMin)} (rotation x{F3(_mobRot)}) ; " +
                    $"temps de visée x{F3(_viseMin)} / x{F3(_viseMaj)} ; dispersion x{F3(_dispMin)} / x{F3(_dispMaj)} ; chargement x{F3(_chargeMin)} / x{F3(_chargeMaj)}");
        }

        /// Reads the ammunition table once per battle: the game's own crit weight of every round, banded.
        static void ReadAmmo()
        {
            try
            {
                var src = DataBaseService._instance?.RawAccess;
                if (src == null) return;
                var rows = Props.Rows(src.Ammunitions.GetAll());
                int max = 0;
                foreach (var a in rows) if (a != null && a.Id > max) max = a.Id;
                int n = Math.Min(max + 1, 65536);
                var band = new byte[n];
                Array.Clear(_bandRows, 0, NBand);
                var exemples = new List<string>[NBand];
                for (int i = 0; i < NBand; i++) exemples[i] = new List<string>();
                int lus = 0, sansChamp = 0;
                foreach (var a in rows)
                {
                    if (a == null) continue;
                    int id = a.Id;
                    if ((uint)id >= (uint)n) continue;
                    var v = Props.Num(a, "CriticMultiplier");
                    if (!v.HasValue) { sansChamp++; continue; }
                    float m = (float)v.Value;
                    byte b = BandOf(m);
                    band[id] = b;
                    _bandRows[b]++; lus++;
                    if (exemples[b].Count < 3)
                    {
                        string nom = null;
                        try { nom = a.Name; } catch { nom = null; }
                        if (!string.IsNullOrEmpty(nom)) exemples[b].Add(nom + " (x" + F(m) + ")");
                    }
                }
                if (lus == 0)
                {
                    Log("munitions : le poids de critique du jeu (CriticMultiplier) est introuvable dans cette version, les tirs ne pourront pas être classés");
                    _ammoRead = true;
                    return;
                }
                _band = band;                                                                // published as a whole, before arming
                _ammoRead = true;
                var sb = new StringBuilder("munitions du jeu (lecture seule), poids de critique par bande :");
                for (int b = 0; b < NBand; b++)
                {
                    if (_bandRows[b] == 0) continue;
                    sb.Append(' ').Append(BandNames[b]).Append(" : ").Append(N(_bandRows[b])).Append(" munition(s)");
                    if (exemples[b].Count > 0) sb.Append(" [").Append(string.Join(", ", exemples[b])).Append(']');
                    sb.Append(" ;");
                }
                if (sansChamp > 0) sb.Append(' ').Append(N(sansChamp)).Append(" ligne(s) sans le champ");
                Log(sb.ToString());
            }
            catch (Exception e) { _ammoRead = true; Log("munitions illisibles : " + e.GetBaseException().Message); }
        }

        static byte BandOf(float m)
        {
            if (!(m > 0f)) return BandNone;
            if (m < 0.95f) return BandUnder;
            if (m <= 1.05f) return BandOne;
            if (m <= 5f) return BandLight;
            if (m <= 20f) return BandMedium;
            if (m <= 120f) return BandAt;
            return BandBomb;
        }

        // ---------------------------------------------------------------- hook (any thread, no Unity call, no allocation, no logging)
        static void TryPatch()
        {
            _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Critiques");
            var target = AccessTools.Method(typeof(BSH), "CalculateHitDamage");
            if (target == null)
            {
                _refused = true;
                Log("calcul des dégâts introuvable dans cette version du jeu : les tirs ne peuvent pas être comptés");
                return;
            }
            try
            {
                // Priority.Last, and explicitly after the two other Priority.Last owners of this chain: equal priorities
                // are otherwise ordered by registration, which is not stable between builds.
                var hm = new HarmonyMethod(typeof(Critiques).GetMethod(nameof(DamagePostfix), BindingFlags.NonPublic | BindingFlags.Static))
                {
                    priority = Priority.Last,
                    after = new[] { "RealismOverhaul.ProtectionMission", "RealismOverhaul.AntiHeliPortee" },
                };
                _harmony.Patch(target, postfix: hm);
                _damagePatched = true;
                // wording kept as the log filter of the PUBLIC build knows it ("point(s) d'accroche installé(s)"),
                // so a shared build also says whether the measurement is really running
                Log("1/1 point(s) d'accroche installé(s) (après les autres correctifs de dégâts : la valeur vue est celle que le jeu applique)");
            }
            catch (Exception e) { _refused = true; Log("point d'accroche non installé (" + e.GetBaseException().Message + ")"); }
        }

        static void Push(int eid, int ammo, float dmg, byte band)
        {
            long n = Interlocked.Increment(ref _evHead);
            int slot = (int)((n - 1) & (QueueSize - 1));
            _ev[slot].Seq = 0;                                                               // slot being written
            _ev[slot].Eid = eid; _ev[slot].Ammo = ammo; _ev[slot].Dmg = dmg; _ev[slot].Band = band;
            _ev[slot].Seq = n;                                                               // written last: the slot is complete
        }

        /// static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo, Single penetration,
        /// Boolean forceTopArmorAttack, ArmorSides armorSide). Priority.Last: __result is the damage the game applies,
        /// after the cover, entrenchment and half-armour postfixes. Reads only, never writes __result.
        static void DamagePostfix(EcsEntity target, Ammo ammoInfo, float __result)
        {
            if (Campaign.MissionInerte || !_armed) return;
            try
            {
                if (LeurresMesure.HitDepth <= 0) return;                                     // AI scoring call, not a real impact
                float r = __result;
                if (!(r > 0f)) return;                                                       // no damage applied (a zeroed or protected hit)
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _offMain);
                int ammo = ammoInfo == null ? 0 : DegatsAmmoCache.Id(ammoInfo);
                var tbl = _band;
                byte band = (uint)ammo < (uint)tbl.Length ? tbl[ammo] : BandOne;
                if (band >= NBand) band = BandOne;
                Interlocked.Increment(ref _hits);
                Push(target.EntityId, ammo, r, band);
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) { _off = true; _armed = false; }
            }
        }

        // ---------------------------------------------------------------- main thread
        static void Drain()
        {
            long head = Interlocked.Read(ref _evHead);
            if (head == _evRead) return;
            if (head - _evRead > QueueSize) { _evLost += head - _evRead - QueueSize; _evRead = head - QueueSize; }
            while (_evRead < head)
            {
                long n = _evRead + 1;
                int slot = (int)((n - 1) & (QueueSize - 1));
                long s1 = _ev[slot].Seq;
                if (s1 < n)
                {
                    if (head - n < 64) break;                                                // still being written by a hook: next frame
                    _evLost++; _evRead = n; continue;
                }
                var e = _ev[slot];
                if (s1 > n || _ev[slot].Seq != n) { _evLost++; _evRead = n; continue; }      // overwritten meanwhile
                _evRead = n;
                if (e.Band >= NBand) continue;
                OnHit(e);
            }
        }

        static void OnHit(Ev e)
        {
            byte kind = _kindOfEid.TryGetValue(e.Eid, out byte k) ? k : KindUnknown;
            _hitsBand[e.Band, kind]++;
            _dmgBand[e.Band, kind] += e.Dmg;
            if (_pending.Count >= MaxSuivis && !_pending.ContainsKey(e.Eid)) return;
            _pending.TryGetValue(e.Eid, out var p);
            p.Tirs++;
            p.Degats += e.Dmg;
            if (e.Band > p.Bande || p.Tirs == 1) { p.Bande = e.Band; p.Ammo = e.Ammo; }      // the heaviest thing that hit wins
            _pending[e.Eid] = p;
        }

        /// Keeps the Eid -> kind map fresh: the shared scan rebuilds its LuaUnit objects every 0.5 s.
        /// A pending window is consumed within one sweep, so what is left over belongs to units that have died: past a
        /// soft cap the whole table is dropped rather than walked, and the loss is counted with the other lost events.
        static void Index(DegatsScan scan)
        {
            foreach (var u in scan.All)
                if (_kindOfEid.Count < MaxSuivis || _kindOfEid.ContainsKey(u.Eid)) _kindOfEid[u.Eid] = KindOf(u);
            if (_pending.Count > 8192) { _evLost += _pending.Count; _pending.Clear(); }
        }

        static byte KindOf(DegatsUnit u)
        {
            if (u.Infantry) return KindInfantry;
            if (u.Vehicle) return KindVehicle;
            if ((u.Type & 8) != 0) return KindHeli;
            if ((u.Type & 16) != 0) return KindPlane;
            return u.Type == 0 ? KindUnknown : KindOther;
        }

        /// Polls a slice of the units with the engine's own reader. Two int calls plus the health percentage per unit;
        /// a full sweep takes ceil(units / 120) ticks, so about 1 to 2 s on a busy map.
        static void Poll(DegatsScan scan, float now)
        {
            if (_pollBroken) return;
            int total = scan.All.Count;
            if (total == 0) return;
            if (_cursor >= total) _cursor = 0;
            int done = 0;
            while (done < Slice && done < total)
            {
                if (_cursor >= total) _cursor = 0;
                var u = scan.All[_cursor++];
                done++;
                if (u?.U == null) continue;
                int bad, mob, sante;
                try
                {
                    bad = u.U.GetBadConditionsCount(false);
                    mob = u.U.GetBadConditionsCount(true);
                    sante = u.U.GetHealPercentage();
                }
                catch (Exception e)
                {
                    // the engine's own reader refusing a unit is not a reason to keep calling it: dropped after a few
                    _pollErr++;
                    if (_pollErr >= 5)
                    {
                        _pollBroken = true;
                        Log("lecture des mauvaises conditions du jeu impossible (" + e.GetBaseException().Message + ") : seuls les tirs restent comptés");
                    }
                    continue;
                }
                _polls++;
                Apply(u, bad, mob, sante, now);
            }
        }

        static void Apply(DegatsUnit u, int bad, int mob, int sante, float now)
        {
            if (!_suivis.TryGetValue(u.Uid, out var s))
            {
                if (_suivis.Count >= MaxSuivis) return;
                s = new Suivi { Uid = u.Uid, Kind = KindOf(u), LastPoll = now };
                try { s.Nom = DegatsScan.NameOf(u); } catch { s.Nom = "?"; }
                _suivis[u.Uid] = s;
            }

            // hits seen on this unit since its own previous poll
            if (_pending.TryGetValue(u.Eid, out var p))
            {
                _pending.Remove(u.Eid);
                s.FenTirs += p.Tirs; s.FenDegats += p.Degats;
                if (p.Bande > s.FenBande || s.FenTirs == p.Tirs) { s.FenBande = p.Bande; s.FenAmmo = p.Ammo; }
                s.TirsVus += p.Tirs;
            }

            // Time spent carrying at least one bad condition, counted between this unit's own polls. A gap longer than
            // a few sweeps means the unit had left the scan (embarked, dead, out of the map query): that time is not
            // counted, otherwise a unit destroyed with one would look as if it had carried it until the end.
            float gap = Ecart(s.LastPoll, now);
            if (s.Bad > 0) s.PorteTotal += gap;
            if (s.PeakAt >= 0f && s.Bad >= s.PeakBad && s.PeakBad > 0) s.PeakHeld += gap;
            s.LastPoll = now;

            if (s.Bad < 0)
            {
                // first poll of this unit: a bad condition already there cannot be charged to a shot of this battle
                Charge(s, bad, sante, true, false);
                s.PeakBad = bad; s.NiveauMobMax = mob;
                if (bad > 0) s.PeakAt = now;
            }
            else
            {
                // one window = the hits seen on this unit since its own previous poll. It is the denominator of the
                // "per burst" rate and it is counted whether or not anything rose, so that a 30-shell burst weighs 1
                // on both sides of the ratio. Infantry is left out, as in the per-shot denominator.
                if (s.FenTirs > 0 && s.Kind != KindInfantry) _fenetresBand[s.FenBande]++;
                if (bad > s.Bad) Charge(s, bad - s.Bad, sante, false, s.Mob >= 0 && mob > s.Mob);
                else if (bad < s.Bad)
                {
                    _descentes++;
                    if (bad == 0) s.Revenu = true;
                }
                if (bad > s.PeakBad) { s.PeakBad = bad; s.PeakAt = now; s.PeakHeld = 0f; }
                if (mob > s.NiveauMobMax) s.NiveauMobMax = mob;
                if (_slots > 0 && bad >= _slots && s.Bad < _slots) _pleines++;
            }
            s.Bad = bad; s.Mob = mob;
            s.SantePrec = sante;
            s.FenTirs = 0; s.FenBande = BandNone; s.FenDegats = 0f; s.FenAmmo = 0;           // the window is consumed by this poll
        }

        /// Charges a rise of the bad-condition count to what hit that unit in the window, and records the share of
        /// health the window removed: that is the engine's own hitPointLossRate input, measured instead of guessed.
        /// mobUp is true when the mobility crit LEVEL rose in the same poll: no flare and no APS can do that, so that
        /// rise is certainly a crit and it is counted apart.
        static void Charge(Suivi s, int levels, int sante, bool first, bool mobUp)
        {
            if (levels <= 0) return;
            if (first)
            {
                // the very first poll of a unit that already carries one: nothing can be attributed to a shot
                if (s.TirsVus == 0) _monteesSansTir += levels;
                else _monteesAnciennes += levels;
                return;
            }
            if (s.Kind == KindInfantry) _monteesInf++;
            if (mobUp) _monteesMob++;
            if (s.FenTirs > 0)
            {
                _monteesVues++;
                _monteesBand[s.FenBande]++;
                if (mobUp) _mobBand[s.FenBande]++;
                _niveauxBand[s.FenBande] += levels;
                _degatsBand[s.FenBande] += s.FenDegats;
                if (s.SantePrec >= 0 && sante >= 0 && s.SantePrec > sante)
                {
                    _perteBand[s.FenBande] += (s.SantePrec - sante) / 100.0;
                    _perteN[s.FenBande]++;
                }
                if (s.FenAmmo > 0 && (_coupables.Count < 4096 || _coupables.ContainsKey(s.FenAmmo)))
                    _coupables[s.FenAmmo] = (_coupables.TryGetValue(s.FenAmmo, out long c) ? c : 0L) + 1L;
            }
            else if (s.TirsVus > 0) _monteesAnciennes++;
            else _monteesSansTir++;
        }

        /// Seconds between two polls of the same unit, or 0 when the gap is too long to be a sweep: the unit had left
        /// the scan and nothing is known about what happened to it meanwhile.
        static float Ecart(float from, float now)
        {
            float d = now - from;
            if (d > 0f && d <= 5f) return d;
            // a dropped gap is counted: an average carry time of 0 s must be readable as "not measured" and never as
            // "bad conditions never last"
            if (d > 5f) _ecartsIgnores++;
            return 0f;
        }

        static void CloseCarry(Suivi s, float now)
        {
            // the same clamp as Ecart, but WITHOUT counting the rejection: the final close walks every unit ever followed, most of
            // which left the scan long ago, and counting those would swell "relevés trop espacés" with an artefact of the close
            float d = now - s.LastPoll;
            if (s.Bad > 0 && d > 0f && d <= 5f) s.PorteTotal += d;
            s.LastPoll = now;
        }

        // ---------------------------------------------------------------- report
        static void Report(bool end)
        {
            long hitsTot = 0;
            for (int b = 0; b < NBand; b++) for (int k = 0; k < NKind; k++) hitsTot += _hitsBand[b, k];

            var sb = new StringBuilder(640);
            sb.Append(end ? "bilan de la bataille : " : "relevé : ");
            sb.Append("unités suivies ").Append(N(_suivis.Count)).Append(", lectures ").Append(N(_polls));
            if (_pollBroken) sb.Append(" (lecture des mauvaises conditions coupée)");
            sb.Append(" ; tirs vus ").Append(N(hitsTot)).Append(" sur ").Append(N(Interlocked.Read(ref _hits))).Append(" comptés par le crochet")
              .Append(" (hors fil principal ").Append(N(Interlocked.Read(ref _offMain))).Append(')');

            sb.Append(" ; par bande :");
            bool any = false;
            for (int b = 0; b < NBand; b++)
            {
                long h = 0;
                double d = 0;
                for (int k = 0; k < NKind; k++) { h += _hitsBand[b, k]; d += _dmgBand[b, k]; }
                long hv = _hitsBand[b, KindVehicle] + _hitsBand[b, KindHeli];
                // only hits on units that can carry a crit: infantry takes most of the rounds and can carry none, so
                // leaving it in would divide the rate by about three
                long hCrit = h - _hitsBand[b, KindInfantry];
                if (h == 0 && _monteesBand[b] == 0) continue;
                any = true;
                sb.Append(' ').Append(BandNames[b]).Append(" : ").Append(N(h)).Append(" tir(s)");
                if (hv > 0) sb.Append(" (dont ").Append(N(hv)).Append(" sur véhicule ou hélico)");
                if (h > 0) sb.Append(", dégâts moyens ").Append(F(d / h));
                sb.Append(", mauvaises conditions ").Append(N(_monteesBand[b]));
                if (_mobBand[b] > 0) sb.Append(" (dont ").Append(N(_mobBand[b])).Append(" sûrement des critiques de mobilité)");
                if (_niveauxBand[b] > _monteesBand[b]) sb.Append(" (").Append(N(_niveauxBand[b])).Append(" niveau(x))");
                if (hCrit > 0) sb.Append(", soit ").Append(Pct(_monteesBand[b] / (double)hCrit)).Append(" par tir sur véhicule ou hélico");
                if (_fenetresBand[b] > 0) sb.Append(", ").Append(Pct(_monteesBand[b] / (double)_fenetresBand[b])).Append(" par rafale (")
                                            .Append(N(_fenetresBand[b])).Append(" rafale(s))");
                if (_monteesBand[b] > 0)
                    sb.Append(", fenêtre gagnante : ").Append(F(_degatsBand[b] / _monteesBand[b])).Append(" de dégâts")
                      .Append(_perteN[b] > 0 ? ", " + Pct(_perteBand[b] / _perteN[b]) + " de la santé" : "");
                sb.Append(" ;");
            }
            if (!any) sb.Append(" aucun tir encore vu ;");

            sb.Append(" origine des hausses : après un tir vu ").Append(N(_monteesVues))
              .Append(", après des tirs plus anciens ").Append(N(_monteesAnciennes))
              .Append(", sans aucun tir vu ").Append(N(_monteesSansTir))
              .Append(" ; dont sûrement des critiques de mobilité ").Append(N(_monteesMob))
              .Append(", sur de l'infanterie (hors des taux ci-dessus) ").Append(N(_monteesInf))
              .Append(" ; retours à la normale ").Append(N(_descentes));

            // the playability question: can bad conditions stack into a unit the player cannot withdraw?
            Suivi pire = null;
            int porteurs = 0;
            double porteTot = 0;
            foreach (var s in _suivis.Values)
            {
                if (s.PeakBad <= 0) continue;
                porteurs++;
                porteTot += s.PorteTotal;
                if (pire == null || s.PeakBad > pire.PeakBad || (s.PeakBad == pire.PeakBad && s.PorteTotal > pire.PorteTotal)) pire = s;
            }
            sb.Append(" ; unités ayant porté une mauvaise condition ").Append(N(porteurs));
            if (porteurs > 0) sb.Append(", durée moyenne ").Append(F(porteTot / porteurs)).Append(" s");
            if (_ecartsIgnores > 0) sb.Append(" (relevés trop espacés, durée non comptée : ").Append(N(_ecartsIgnores)).Append(')');
            if (_slots > 0) sb.Append(", arrivées au plafond du jeu (").Append(N(_slots)).Append(") ").Append(N(_pleines));
            if (pire != null)
                sb.Append(" ; la plus touchée : ").Append(pire.Nom).Append(" (uid ").Append(N(pire.Uid)).Append(", ").Append(KindNames[pire.Kind])
                  .Append(") jusqu'à ").Append(N(pire.PeakBad)).Append(" mauvaise(s) condition(s), niveau de mobilité au plus haut ")
                  .Append(N(pire.NiveauMobMax)).Append(" (1 jaune, 2 rouge), portées ")
                  .Append(F(pire.PorteTotal)).Append(" s dont ").Append(F(pire.PeakHeld)).Append(" s au maximum, ")
                  .Append(pire.Revenu ? "revenue à zéro" : "jamais revenue à zéro");

            sb.Append(" ; perdus ").Append(N(_evLost)).Append(", erreurs correctif ").Append(N(Interlocked.Read(ref _errors)))
              .Append(", erreurs lecture ").Append(N(_pollErr));

            string line = sb.ToString();
            if (!end && line == _lastReport) return;                                          // nothing moved since the last one
            _lastReport = line;
            Log(line);

            if (!end) return;
            Verdict(hitsTot);
        }

        /// The end-of-battle conclusion, in plain French: what the measurement proves, and the one number the next
        /// version needs before any value may be written.
        static void Verdict(long hitsTot)
        {
            if (hitsTot == 0 && _monteesVues == 0) { Log("verdict : aucun tir vu, rien à conclure sur les mauvaises conditions"); return; }

            var sb = new StringBuilder(560);
            sb.Append("verdict : ");
            if (_monteesVues >= 5)
                sb.Append(N(_monteesVues)).Append(" hausses de mauvaises conditions après un tir vu (critique, APS ou leurre : cette lecture ne les distingue pas)");
            else if (_monteesVues > 0)
                sb.Append("trop peu de hausses vues (").Append(N(_monteesVues)).Append(") pour conclure : rejouer une bataille");
            else if (hitsTot > 0)
                sb.Append("aucune hausse n'a suivi un tir sur ").Append(N(hitsTot)).Append(" tir(s) vu(s)");
            sb.Append(" ; dont ").Append(N(_monteesMob)).Append(" sûrement des critiques de mobilité (le niveau de mobilité est monté : ni un leurre ni un APS ne peut le faire)");
            if (_monteesSansTir > 0)
                sb.Append(" ; ").Append(N(_monteesSansTir)).Append(" hausse(s) sans aucun tir vu : celles-là viennent d'ailleurs (script de mission, capacité épuisée, ou unité arrivée déjà touchée)");
            else
                sb.Append(" ; aucune hausse sans tir vu ; ce zéro n'est pas une preuve : une mission qui n'en pose aucune par script le donne d'avance");

            // the two bands the next version has to calibrate on: the anti-tank rockets and the plain rounds.
            // Infantry is left out of both denominators, as in the per-band lines above.
            long hAt = 0, hOne = 0;
            for (int k = 0; k < NKind; k++)
            {
                if (k == KindInfantry) continue;
                hAt += _hitsBand[BandAt, k]; hOne += _hitsBand[BandOne, k];
            }
            if (hAt > 0)
                sb.Append(" ; roquettes antichars et missiles : ").Append(Pct(_monteesBand[BandAt] / (double)hAt))
                  .Append(" par tir sur véhicule ou hélico, sur ").Append(N(hAt)).Append(" tir(s)");
            if (_fenetresBand[BandAt] > 0)
                sb.Append(" (").Append(Pct(_monteesBand[BandAt] / (double)_fenetresBand[BandAt])).Append(" par rafale)");
            if (hOne > 0)
                sb.Append(" ; balles et obus ordinaires : ").Append(Pct(_monteesBand[BandOne] / (double)hOne))
                  .Append(" par tir sur véhicule ou hélico, sur ").Append(N(hOne)).Append(" tir(s)");
            if (_fenetresBand[BandOne] > 0)
                sb.Append(" (").Append(Pct(_monteesBand[BandOne] / (double)_fenetresBand[BandOne])).Append(" par rafale)");

            if (_buffLu)
                sb.Append(" ; chance de base du jeu ").Append(F3(_probaBase)).Append(" (c'est la seule valeur à régler ; elle n'a pas été touchée)");
            sb.Append(". Rien n'a été modifié dans le jeu par ce module.");
            Log(sb.ToString());

            // the rounds that actually caused the crits, by name: the shortest way to see whether the weighting holds up
            if (_coupables.Count > 0)
            {
                DegatsMunitions table = null;
                try { table = DegatsMunitions.ForBattle(); } catch { table = null; }
                var top = new List<KeyValuePair<int, long>>(_coupables);
                top.Sort((x, y) => y.Value.CompareTo(x.Value));
                var sb2 = new StringBuilder("munitions à l'origine des hausses (les plus fréquentes) :");
                for (int i = 0; i < top.Count && i < 8; i++)
                {
                    string nom = null;
                    if (table != null && table.Names.TryGetValue(top[i].Key, out var nn)) nom = nn;
                    sb2.Append(' ').Append(string.IsNullOrEmpty(nom) ? "munition " + N(top[i].Key) : nom)
                       .Append(" (").Append(N(top[i].Key)).Append(") x").Append(N(top[i].Value)).Append(" ;");
                }
                Log(sb2.ToString());
            }

            // what a next version would need, stated as a target and not as a change
            Log("objectif visé pour une version ultérieure, si la mesure ci-dessus le justifie : une balle sur un véhicule sous 0,1 % par tir, " +
                "une rafale de canon automatique sur un blindé léger 2 à 5 %, une roquette antichar qui blesse sans tuer 25 à 35 %, " +
                "jamais plus de 2 critiques en même temps sur une unité et la mobilité jamais sous la moitié, pour qu'une unité touchée puisse toujours être retirée du combat. " +
                "Les deux camps avec les mêmes chiffres, puisque c'est la même table de munitions.");
        }

        static void ClearBattle()
        {
            _suivis.Clear(); _kindOfEid.Clear(); _pending.Clear(); _coupables.Clear();
            Array.Clear(_hitsBand, 0, _hitsBand.Length);
            Array.Clear(_dmgBand, 0, _dmgBand.Length);
            Array.Clear(_monteesBand, 0, NBand);
            Array.Clear(_fenetresBand, 0, NBand);
            Array.Clear(_mobBand, 0, NBand);
            Array.Clear(_niveauxBand, 0, NBand);
            Array.Clear(_perteBand, 0, NBand);
            Array.Clear(_degatsBand, 0, NBand);
            Array.Clear(_perteN, 0, NBand);
            _monteesVues = _monteesAnciennes = _monteesSansTir = _descentes = _polls = _pollErr = 0;
            _monteesMob = _monteesInf = _ecartsIgnores = 0;
            _pleines = 0; _cursor = 0;
            _evRead = Interlocked.Read(ref _evHead); _evLost = 0;
            Interlocked.Exchange(ref _offMain, 0);
            Interlocked.Exchange(ref _hits, 0); Interlocked.Exchange(ref _errors, 0);
            _lastReport = null; _depthWarned = false; _pollBroken = false;
            _rulesRead = false; _ammoRead = false;
        }
    }
}
