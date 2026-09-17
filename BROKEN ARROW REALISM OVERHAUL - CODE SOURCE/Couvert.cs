// RealismOverhaul - infantry cover in vegetation and forest (v0.23.0, table of infantry_speed_cover.txt with its
//  corrections), both sides alike, solo campaign only. The engine protects infantry only inside buildings (BuildingsConfig floor +
//  per soldier, reduced by each ammunition's IgnoreCover); trees give no damage reduction. A postfix on
//  BattleSystemHelpers.CalculateHitDamage (Priority.High: after the calibre measurement and the half-armour rule, order-independent
//  with the Resistance cheat because both multiply) scales the damage an infantry squad takes when it stands in vegetation or forest:
//   rifles/MG ball 0.90/0.60, DMR 0.90/0.65, sniper 0.90/0.65, 12.7/14.5 mm 0.90/0.70, 40 mm grenades 1.0/0.80, autocannon HE
//   1.0/0.80, tank HE 1.0/0.85, HEAT/HE rockets 1.0/0.85; mortars, artillery, MLRS, thermobaric, missiles, bombs, plane guns and
//   everything else 1.0/1.0. Garrisoned squads and squads standing on building pixels are never scaled (buildings are handled by
//   the config and IgnoreCover data). Close combat (CloseQuartersCombatSystem.DeductHitPoints) is untouched. The AI scoring calls
//   get the same factor, so the AI sees that a squad in the woods is harder to hurt.
//  Main thread every 0.5 s: EntityId -> location class for every infantry squad (map terrain under the squad centre and 4 points
//  at 5 m, LoadedComponent for garrisons), published as one immutable snapshot; ammo Id -> cover class once per battle
//  (DegatsMunitions: weapon types, trajectory, ArmorTargeted, calibre and thermobaric names).
//  MEASUREMENT [COUVERT] (the game's CollectStatistic is never called, 0 calls in the 0.22.8 logs):
//   A) each impact hit on a squad is paired with the CalculateStressDamage call that follows it on the same thread in the same
//      impact (its healthDamage is taken as the final damage), grouped by location class, ammo class and, in garrison, by the
//      estimated number of soldiers against the assumed building formula floor + perSoldier x N and IgnoreCover, alone or
//      stacked with the open-ground ratio (the Calm resist);
//   B) squad health polled every 0.5 s: loss over a stay in one location class against the raw hits seen during it (stays with a
//      health drop and no hit seen are discarded: close combat, collapse, fire).
//  Mean ratios every 60 s. Error kill-switch, crash guard, own Harmony id installed lazily in battle.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using LoadedComponent = Il2CppBrokenArrow.Client.Ecs.Transports.Components.LoadedComponent;
using MapMeta = Il2CppBrokenArrow.Client.Ecs.Navigation.MapMetaData;
using Terrain = Il2CppBrokenArrow.Shared.Ecs.Enums.TerrainType;
using Ammo = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using ArmorsRow = Il2CppBrokenArrow.DataBase.Models.Armors;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Couvert
    {
        const string GuardVersion = "0.24.0";
        const int MaxErrors = 50;
        const int QueueSize = 4096;                                  // power of two
        internal const byte LocOpen = 0, LocVegetation = 1, LocForest = 2, LocGarrison = 3, LocOnBuilding = 4, LocLoaded = 5, LocUnknown = 6;
        const int NLoc = 7;
        const int Mixed = DegatsMunitions.CoverCount;                // health stays hit by several ammo classes
        const int NCls = DegatsMunitions.CoverCount + 1;
        const int NMen = 4;
        const float EdgeOffset = 5f;
        const byte EvHit = 1, EvPair = 2;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly string[] LocNames = { "terrain découvert", "végétation", "forêt", "en garnison", "sur un bâtiment hors garnison", "embarquée", "terrain illisible" };
        static readonly string[] ClsNames = { "sans effet", "balles de fusil et mitrailleuse", "DMR", "tireurs d'élite", "12,7 et 14,5 mm", "grenades 40 mm", "obus explosifs de canon", "obus explosifs de char", "roquettes", "mélange" };
        static readonly string[] MenNames = { "1 à 3 soldats", "4 à 6 soldats", "7 à 9 soldats", "10 soldats et plus" };

        // multipliers, index = DegatsMunitions cover class
        static readonly float[] VegetationMult = { 1f, 0.90f, 0.90f, 0.90f, 0.90f, 1f, 1f, 1f, 1f };
        static readonly float[] ForestMult = { 1f, 0.60f, 0.65f, 0.65f, 0.70f, 0.80f, 0.80f, 0.85f, 0.85f };

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        static HarmonyLib.Harmony _harmony;
        static bool _patchTried, _damagePatched, _stressPatched, _refused, _everOnline, _sessionArmed, _netLogged, _depthWarned;
        static bool _loadedBroken, _terrainBroken, _offLogged;
        static float _nextTick, _nextReport, _battleStartReal;
        static int _bridgeMask = -1;

        // ---------------------------------------------------------------- hook side (immutable snapshots, plain counters)
        static volatile bool _armed, _off;
        static volatile int _mainThread;

        sealed class Snap
        {
            internal readonly int[] Ids; internal readonly byte[] Loc, Men;
            internal Snap(int[] ids, byte[] loc, byte[] men) { Ids = ids; Loc = loc; Men = men; }
        }
        static volatile Snap _snap = new Snap(Array.Empty<int>(), Array.Empty<byte>(), Array.Empty<byte>());
        static volatile byte[] _cover = Array.Empty<byte>();

        struct Ev { public long Seq; public int Eid, Ammo; public byte Kind, Loc, Cls, Men; public float Raw, Mult, Final, MaxHeal; }
        static readonly Ev[] _ev = new Ev[QueueSize];
        static long _evHead, _evRead, _evLost;

        [ThreadStatic] static long _seq, _pendSeq, _pendSerial;
        [ThreadStatic] static int _pendEid, _pendAmmo;
        [ThreadStatic] static float _pendRaw, _pendMult;
        [ThreadStatic] static byte _pendLoc, _pendCls, _pendMen;
        [ThreadStatic] static bool _pendOn;

        static long _calls, _offMain, _reducedHit, _reducedAi, _hits, _pairs, _unpaired, _stressIn, _errors;

        // ---------------------------------------------------------------- measurement (main thread)
        sealed class Acc { public long N; public double Raw, MultRaw, Final, RatioSum; }
        static Acc[,] _a = NewAcc(), _b = NewAcc();
        static long[,] _gN = new long[NMen, NCls];
        static double[,] _gRaw = new double[NMen, NCls], _gFinal = new double[NMen, NCls], _gPred = new double[NMen, NCls];

        sealed class Stay
        {
            public byte Loc; public int UnitId, StartPct, LastPct, Men;
            public double RawNow, MultRawNow, RawAtPoll, MultRawAtPoll;
            public readonly double[] ClsRaw = new double[DegatsMunitions.CoverCount], ClsRawAtPoll = new double[DegatsMunitions.CoverCount];
            public bool Tainted;
        }
        static readonly Dictionary<int, Stay> _stays = new();
        static readonly Dictionary<int, int> _unitOfEid = new();
        static readonly Dictionary<int, (int members, float maxHp)> _unitHp = new();
        static readonly Dictionary<int, (float value, int count)> _learnedMaxHp = new();
        static readonly List<string> _hpExamples = new();
        static long _pairsChecked, _pairsConsistent, _staysClosed, _staysTainted, _staysNoHp, _dropsWithoutHit;
        static readonly int[] _locCount = new int[NLoc];
        static float _floor, _perSoldier;
        static bool _buildingRead;
        static DegatsMunitions _table;
        static string _lastReport;

        static Acc[,] NewAcc()
        {
            var a = new Acc[NLoc, NCls];
            for (int i = 0; i < NLoc; i++) for (int j = 0; j < NCls; j++) a[i, j] = new Acc();
            return a;
        }

        static void Log(string s) => Mod.Log.Msg("[COUVERT] " + s);

        // ---------------------------------------------------------------- lifecycle
        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Couvert");
            _enabled = c.CreateEntry("CouvertForetVegetation", true, description: Build.Desc("L'infanterie en végétation ou en forêt reçoit moins de dégâts des balles, grenades et obus directs (pas de l'artillerie ni des bombes), pour les deux camps"));
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

        /// Optional: the integrator may call it when the battle-end screen appears.
        internal static void OnBattleEnd() => EndBattle("fin de bataille");

        static void EndBattle(string why)
        {
            _armed = false;
            _snap = new Snap(Array.Empty<int>(), Array.Empty<byte>(), Array.Empty<byte>());
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean.Value != 0) { _unclean.Value = 0; try { MelonPreferences.Save(); } catch { } }
            try
            {
                Drain();
                foreach (var st in _stays.Values) Close(st);
                _stays.Clear();
                Report(true);
            }
            catch (Exception e) { Log("bilan illisible : " + e.GetBaseException().Message); }
            Log($"fin de bataille ({why})");
        }

        static int _wait;

        /// Every frame in campaign: drains the hook queue; snapshot, health poll and reports every 0.5 s.
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _refused) { if (_armed) _armed = false; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_sessionArmed) Drain();
            if (now < _nextTick) return;
            // one heavy module job per frame (Planif.cs): same 0.5 s period, just not in the same frame as the other modules
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _nextTick = now + 0.5f;
            _mainThread = Environment.CurrentManagedThreadId;
            var gc = GameController._instance;
            if (gc?._GameSession_k__BackingField?.CurrentPlayer == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_armed) { _armed = false; Log("partie en ligne : couvert coupé"); } return; }
            if (!_sessionArmed && !StartBattle(now)) return;

            var table = DegatsMunitions.ForBattle();
            if (table == null) { _armed = false; return; }
            if (!ReferenceEquals(_table, table)) { _table = table; _cover = table.Cover; LogTable(table); }
            var scan = DegatsScan.Get(now);
            Drain();                                                                         // hits of the last 0.5 s before the health poll
            ScanInfantry(gc, scan);
            if (!_patchTried) { _patchTried = true; TryPatch(); }
            if (_refused) return;
            if (Interlocked.Read(ref _errors) >= MaxErrors) _off = true;
            if (_off)
            {
                _armed = false;
                if (!_offLogged)
                {
                    _offLogged = true;
                    Mod.Log.Warning($"[COUVERT] {MaxErrors} erreurs dans le correctif : couvert de la forêt coupé jusqu'au redémarrage du jeu (dégâts normaux)");
                    Mod.Notify(TxtKey.N_COVER_OFF_ERRORS);
                }
                return;
            }
            _armed = _damagePatched;
            if (!_depthWarned && now - _battleStartReal > 15f && !LeurresMesure.HitDepthReady)
            {
                _depthWarned = true;
                Log("profondeur d'impact indisponible (mesure des leurres coupée) : le couvert agit, mais les mesures A et B ne voient pas les tirs");
            }
            if (now >= _nextReport) { _nextReport = now + 60f; Report(false); }
        }

        static bool StartBattle(float now)
        {
            if (_unclean.Value >= 2)
            {
                _refused = true;
                _armed = false;
                Mod.Log.Warning("[COUVERT] désactivé : les deux dernières parties ne se sont pas terminées normalement");
                Mod.Notify(TxtKey.N_COVER_OFF_SAFETY);
                return false;
            }
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;
            MelonPreferences.Save();
            ClearBattle();
            _battleStartReal = now;
            _nextReport = now + 60f;
            DegatsAmmoCache.NewBattle();
            ReadBuildings();
            Log($"prêt : infanterie des deux camps, forêt et végétation hors garnison ; bâtiments du jeu : plancher {F3(_floor)}, par soldat {F3(_perSoldier)}{(_buildingRead ? "" : " (illisibles)")}");
            return true;
        }

        static void ReadBuildings()
        {
            _buildingRead = false; _floor = 0f; _perSoldier = 0f;
            try
            {
                var bc = GameCfg.Instance?.BuildingsConfig;
                if (bc == null) return;
                _floor = bc.DamageModifierFloor;
                _perSoldier = bc.DamageModifierPerSoldier;
                _buildingRead = true;
            }
            catch { _buildingRead = false; }
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

        static void TryPatch()
        {
            _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Couvert");
            _damagePatched = Patch("dégâts", AccessTools.Method(typeof(BSH), "CalculateHitDamage"), nameof(DamagePostfix), Priority.High);
            _stressPatched = Patch("stress (mesure)", AccessTools.Method(typeof(BSH), "CalculateStressDamage"), nameof(StressPostfix), Priority.Normal);
            if (!_damagePatched) { _refused = true; Log("calcul des dégâts introuvable ou non modifiable : couvert impossible dans cette version du jeu"); return; }
            Log($"correctif installé{(_stressPatched ? "" : " (mesure A indisponible)")}");
        }

        static bool Patch(string label, MethodInfo target, string postfix, int priority)
        {
            try
            {
                if (target == null) { Log($"{label} : méthode introuvable"); return false; }
                var hm = new HarmonyMethod(typeof(Couvert).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)) { priority = priority };
                _harmony.Patch(target, postfix: hm);
                return true;
            }
            catch (Exception e) { Log($"{label} : non installé ({e.GetBaseException().Message})"); return false; }
        }

        static void LogTable(DegatsMunitions t)
        {
            var sb = new StringBuilder("table des munitions contre l'infanterie hors garnison (végétation / forêt) :");
            for (int c = 1; c < DegatsMunitions.CoverCount; c++)
                sb.Append($" {ClsNames[c]} x{F2(VegetationMult[c])}/x{F2(ForestMult[c])} ({t.CoverCounts[c]} munitions) ;");
            sb.Append($" sans effet (artillerie, mortiers, lance-roquettes, thermobariques, missiles, bombes, canons d'avion, obus perforants...) {t.CoverCounts[0]} munitions ; familles du réalisme {(t.FamilyFile ? "lues" : "absentes")}");
            Log(sb.ToString());
        }

        // ---------------------------------------------------------------- hooks (any thread, no Unity call, no allocation, no logging)
        static void Push(byte kind, int eid, int ammo, byte loc, byte cls, byte men, float raw, float mult, float final, float maxHeal)
        {
            long n = Interlocked.Increment(ref _evHead);
            int slot = (int)((n - 1) & (QueueSize - 1));
            _ev[slot].Seq = 0;                                                               // slot being written
            _ev[slot].Kind = kind; _ev[slot].Eid = eid; _ev[slot].Ammo = ammo; _ev[slot].Loc = loc; _ev[slot].Cls = cls; _ev[slot].Men = men;
            _ev[slot].Raw = raw; _ev[slot].Mult = mult; _ev[slot].Final = final; _ev[slot].MaxHeal = maxHeal;
            _ev[slot].Seq = n;                                                               // written last: the slot is complete
        }

        /// static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo, Single penetration,
        /// Boolean forceTopArmorAttack, ArmorSides armorSide). Scales the result for squads in vegetation or forest.
        static void DamagePostfix(EcsEntity target, Ammo ammoInfo, ref float __result)
        {
            if (Campaign.MissionInerte || !_armed) return;                            // mission without the mod: vanilla damage
            try
            {
                bool inHit = LeurresMesure.HitDepth > 0;
                if (inHit) _seq++;                                                           // any call in an impact breaks the pairing chain
                float r = __result;
                if (!(r > 0f)) return;
                var s = _snap;
                int eid = target.EntityId;
                int i = Array.BinarySearch(s.Ids, eid);
                if (i < 0) return;
                Interlocked.Increment(ref _calls);
                if (Environment.CurrentManagedThreadId != _mainThread) Interlocked.Increment(ref _offMain);
                byte loc = s.Loc[i];
                bool cover = loc == LocVegetation || loc == LocForest;
                if (!cover && !inHit) return;                                                // AI scoring outside the woods: nothing to do
                int ammo = ammoInfo == null ? 0 : DegatsAmmoCache.Id(ammoInfo);
                var tbl = _cover;
                byte cls = (uint)ammo < (uint)tbl.Length ? tbl[ammo] : DegatsMunitions.CoverNone;
                if (cls >= DegatsMunitions.CoverCount) cls = DegatsMunitions.CoverNone;
                float mult = loc == LocForest ? ForestMult[cls] : loc == LocVegetation ? VegetationMult[cls] : 1f;
                if (mult < 1f)
                {
                    __result = r * mult;
                    if (inHit) Interlocked.Increment(ref _reducedHit); else Interlocked.Increment(ref _reducedAi);
                }
                if (!inHit) return;
                Interlocked.Increment(ref _hits);
                byte men = s.Men[i];
                Push(EvHit, eid, ammo, loc, cls, men, r, mult, 0f, 0f);
                _pendSeq = _seq; _pendSerial = LeurresMesure.HitSerial; _pendEid = eid; _pendAmmo = ammo;
                _pendRaw = r; _pendMult = mult; _pendLoc = loc; _pendCls = cls; _pendMen = men; _pendOn = true;
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) { _off = true; _armed = false; }   // repeated errors: vanilla damage
            }
        }

        /// static Single CalculateStressDamage(Single maxStress, Single healthDamage, Single stressDamage, Single targetMaxHeal).
        /// Measurement only: pairs the infantry hit computed just before on this thread, in the same impact.
        static void StressPostfix(float healthDamage, float targetMaxHeal)
        {
            if (Campaign.MissionInerte || !_armed || LeurresMesure.HitDepth <= 0) return;
            try
            {
                Interlocked.Increment(ref _stressIn);
                if (!_pendOn) return;
                if (_pendSeq != _seq || _pendSerial != LeurresMesure.HitSerial) { _pendOn = false; Interlocked.Increment(ref _unpaired); return; }
                Push(EvPair, _pendEid, _pendAmmo, _pendLoc, _pendCls, _pendMen, _pendRaw, _pendMult, healthDamage, targetMaxHeal);
                _pendOn = false;
                Interlocked.Increment(ref _pairs);
            }
            catch { if (Interlocked.Increment(ref _errors) >= MaxErrors) { _off = true; _armed = false; } }
        }

        // ---------------------------------------------------------------- snapshot (main thread, every 0.5 s)
        static void ScanInfantry(GameController gc, DegatsScan scan)
        {
            MapMeta map = null;
            try { map = gc.MapMetaData; } catch { map = null; }
            if (_bridgeMask < 0) { try { _bridgeMask = (int)MapMeta.BRIDGE_FLAG_FILTER; } catch { _bridgeMask = 240; } }
            var ids = new List<int>();
            var locs = new List<byte>();
            var men = new List<byte>();
            var seen = new HashSet<int>();
            Array.Clear(_locCount, 0, NLoc);
            foreach (var u in scan.All)
            {
                if (!u.Infantry) continue;
                byte loc;
                try { loc = Classify(u, map); } catch { loc = LocUnknown; }
                int pct = -1;
                try { pct = u.U.GetHealPercentage(); } catch { pct = -1; }
                var hp = UnitHp(u.UnitId);
                int m = hp.members > 0 && pct >= 0 ? (int)Math.Ceiling(pct * hp.members / 100.0 - 0.01) : 0;
                m = Math.Clamp(m, 0, 255);
                ids.Add(u.Eid); locs.Add(loc); men.Add((byte)m);
                _locCount[loc]++;
                seen.Add(u.Eid);
                if (_unitOfEid.Count < 20000 || _unitOfEid.ContainsKey(u.Eid)) _unitOfEid[u.Eid] = u.UnitId;
                if (pct >= 0) Poll(u, loc, pct, m);
            }
            // squads that left the scan (dead, embarked...): their stay ends at the last poll
            if (_stays.Count > 0)
            {
                var gone = new List<int>();
                foreach (var kv in _stays) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                foreach (var eid in gone) { Close(_stays[eid]); _stays.Remove(eid); }
            }
            var ia = ids.ToArray();
            var order = new int[ia.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(ia, order);
            var la = new byte[ia.Length];
            var ma = new byte[ia.Length];
            for (int i = 0; i < order.Length; i++) { la[i] = locs[order[i]]; ma[i] = men[order[i]]; }
            _snap = new Snap(ia, la, ma);                                                    // published as a whole, before arming
        }

        /// Location class of a squad: garrison / building pixel / vegetation / forest / open, from the map under the squad centre and
        /// 4 points at 5 m (a squad on the edge of a wood counts as vegetation, not forest).
        static byte Classify(DegatsUnit u, MapMeta map)
        {
            bool loaded = false;
            if (!_loadedBroken)
            {
                try { loaded = u.U.Entity.Has<LoadedComponent>(); }
                catch (Exception e)
                {
                    _loadedBroken = true;
                    Log("lecture « escouade embarquée » impossible (" + e.GetBaseException().Message + ") : une escouade sur un bâtiment est comptée en garnison");
                }
            }
            if (map == null || _terrainBroken) return loaded ? LocLoaded : LocUnknown;
            int center = TerrainAt(map, u.Pos);
            if (center < 0) return loaded ? LocLoaded : LocUnknown;
            if (center == (int)Terrain.Buildings) return loaded || _loadedBroken ? LocGarrison : LocOnBuilding;
            if (!loaded && center != (int)Terrain.Forest && center != (int)Terrain.Vegetation) return LocOpen;   // neighbours cannot change it
            int forest = center == (int)Terrain.Forest ? 1 : 0, veg = center == (int)Terrain.Vegetation ? 1 : 0, bld = 0;
            for (int k = 0; k < 4; k++)
            {
                V3 q = u.Pos;
                if (k == 0) q.x += EdgeOffset; else if (k == 1) q.x -= EdgeOffset; else if (k == 2) q.z += EdgeOffset; else q.z -= EdgeOffset;
                int t = TerrainAt(map, q);
                if (t < 0) break;
                if (t == (int)Terrain.Forest) forest++;
                else if (t == (int)Terrain.Vegetation) veg++;
                else if (t == (int)Terrain.Buildings) bld++;
            }
            if (loaded) return bld > 0 ? LocGarrison : LocLoaded;
            if (bld >= 2) return LocOnBuilding;
            if (center == (int)Terrain.Forest && forest >= 3) return LocForest;
            if ((center == (int)Terrain.Forest || center == (int)Terrain.Vegetation) && forest + veg >= 3) return LocVegetation;
            return LocOpen;
        }

        static int TerrainAt(MapMeta map, V3 p)
        {
            if (_terrainBroken) return -1;
            try
            {
                V3 q = p;
                map.GetTerrainAtHeight(ref q, out Terrain tt, out float _);
                return (int)tt & ~_bridgeMask;
            }
            catch (Exception e)
            {
                _terrainBroken = true;
                Log("terrain de la carte illisible (" + e.GetBaseException().Message + ") : couvert inactif pour cette bataille");
                return -1;
            }
        }

        /// Squad size (DB squad members, loaded unit copy first) and estimated max health (members x armour health).
        static (int members, float maxHp) UnitHp(int unitId)
        {
            if (unitId <= 0) return (0, 0f);
            if (_unitHp.TryGetValue(unitId, out var v)) return v;
            int members = 0;
            float hp = 0f;
            try
            {
                var db = DataBaseService._instance;
                UnitsRow row = null;
                try { var d = db?.UnitsLoader?._loadedUnits; if (d != null) d.TryGetValue(unitId, out row); } catch { row = null; }
                if (row == null) { try { var src = db?.RawAccess; if (src != null) src.Units.TryGetById(unitId, out row); } catch { row = null; } }
                if (row != null)
                {
                    try { members = row.SquadMembers?.Count ?? 0; } catch { members = 0; }
                    ArmorsRow a = null;
                    try { a = row.CurrentArmor ?? row.Armor; } catch { a = null; }
                    if (a != null) hp = a.MaxHealthPoints * Math.Max(members, 1);
                }
            }
            catch { }
            v = (members, hp);
            if (_unitHp.Count < 5000) _unitHp[unitId] = v;
            return v;
        }

        static float MaxHp(int unitId)
        {
            if (_learnedMaxHp.TryGetValue(unitId, out var l) && l.count >= 3) return l.value;   // the game's own value, seen 3 times
            return UnitHp(unitId).maxHp;
        }

        // ---------------------------------------------------------------- measurement B: health stays
        static void Poll(DegatsUnit u, byte loc, int pct, int men)
        {
            if (!_stays.TryGetValue(u.Eid, out var st) || st.UnitId != u.UnitId || st.Loc != loc || pct > st.LastPct)
            {
                if (st != null) Close(st);
                st = new Stay { Loc = loc, UnitId = u.UnitId, StartPct = pct, LastPct = pct, Men = men };
                _stays[u.Eid] = st;
                return;
            }
            if (st.LastPct - pct >= 2 && st.RawNow - st.RawAtPoll <= 0)
            {
                st.Tainted = true;                                                           // health lost without any hit seen
                _dropsWithoutHit++;
            }
            st.LastPct = pct;
            st.RawAtPoll = st.RawNow;
            st.MultRawAtPoll = st.MultRawNow;
            Array.Copy(st.ClsRaw, st.ClsRawAtPoll, st.ClsRaw.Length);
        }

        static void Close(Stay st)
        {
            if (st == null || !(st.RawAtPoll > 0)) return;
            _staysClosed++;
            if (st.Tainted) { _staysTainted++; return; }
            float maxHp = MaxHp(st.UnitId);
            if (!(maxHp > 0f)) { _staysNoHp++; return; }
            double observed = (st.StartPct - st.LastPct) / 100.0 * maxHp;
            int dom = Mixed;
            double best = 0;
            for (int c = 0; c < st.ClsRawAtPoll.Length; c++) if (st.ClsRawAtPoll[c] > best) { best = st.ClsRawAtPoll[c]; dom = c; }
            if (best < 0.8 * st.RawAtPoll) dom = Mixed;
            Add(_b[st.Loc, dom], st.RawAtPoll, st.MultRawAtPoll, observed);
        }

        static void Add(Acc a, double raw, double multRaw, double final)
        {
            a.N++; a.Raw += raw; a.MultRaw += multRaw; a.Final += final;
            if (raw > 0) a.RatioSum += final / raw;
        }

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
                if (s1 > n || _ev[slot].Seq != n) { _evLost++; _evRead = n; continue; }     // overwritten meanwhile
                _evRead = n;
                if (e.Loc >= NLoc || e.Cls >= DegatsMunitions.CoverCount) continue;
                if (e.Kind == EvHit) OnHit(e); else if (e.Kind == EvPair) OnPair(e);
            }
        }

        static void OnHit(Ev e)
        {
            if (!_stays.TryGetValue(e.Eid, out var st) || st.Loc != e.Loc) return;
            st.RawNow += e.Raw;
            st.MultRawNow += e.Raw * e.Mult;
            st.ClsRaw[e.Cls] += e.Raw;
        }

        // ---------------------------------------------------------------- measurement A: calculation -> stress pairs
        static void OnPair(Ev e)
        {
            int unitId = _unitOfEid.TryGetValue(e.Eid, out var uid) ? uid : 0;
            if (unitId > 0 && e.MaxHeal > 0f)
            {
                float est = UnitHp(unitId).maxHp;
                if (est > 0f)
                {
                    _pairsChecked++;
                    if (Math.Abs(e.MaxHeal - est) <= 0.6f) _pairsConsistent++;
                    else if (_hpExamples.Count < 5) _hpExamples.Add($"unité {unitId} : santé max du jeu {F2(e.MaxHeal)}, estimée {F2(est)}");
                }
                if (_learnedMaxHp.TryGetValue(unitId, out var l) && Math.Abs(l.value - e.MaxHeal) < 0.01f) _learnedMaxHp[unitId] = (l.value, l.count + 1);
                else if (_learnedMaxHp.Count < 5000) _learnedMaxHp[unitId] = (e.MaxHeal, 1);
            }
            Add(_a[e.Loc, e.Cls], e.Raw, e.Raw * e.Mult, e.Final);
            if (e.Loc == LocGarrison)
            {
                int b = e.Men <= 3 ? 0 : e.Men <= 6 ? 1 : e.Men <= 9 ? 2 : 3;
                double baseMod = _floor + _perSoldier * Math.Max(1, (int)e.Men);
                double ic = _table != null && (uint)e.Ammo < (uint)_table.IgnoreCover.Length ? _table.IgnoreCover[e.Ammo] : 0.0;
                double pred = Math.Min(1.0, baseMod + (1.0 - baseMod) * Math.Clamp(ic, 0.0, 1.0));
                _gN[b, e.Cls]++;
                _gRaw[b, e.Cls] += e.Raw;
                _gFinal[b, e.Cls] += e.Final;
                _gPred[b, e.Cls] += pred * e.Raw;
            }
        }

        // ---------------------------------------------------------------- reports (main thread)
        static string F2(double v) => v.ToString("0.##", Inv);
        static string F3(double v) => v.ToString("0.###", Inv);
        static string R(double num, double den) => den > 0 ? "x" + (num / den).ToString("0.###", Inv) : "-";
        static string Share(long part, long n) => n <= 0 ? "-" : $"{part}/{n}";

        static void Report(bool final)
        {
            ReadBuildings();
            var lines = new List<string>();
            int squads = 0;
            for (int l = 0; l < NLoc; l++) squads += _locCount[l];
            var head = new StringBuilder();
            head.Append($"{(final ? "bilan" : "relevé")} : escouades {squads} (");
            for (int l = 0; l < NLoc; l++) head.Append($"{(l > 0 ? ", " : "")}{LocNames[l]} {_locCount[l]}");
            head.Append($") ; calculs de dégâts sur l'infanterie {Interlocked.Read(ref _calls)} (hors fil principal {Interlocked.Read(ref _offMain)}), réduits par le couvert pendant un impact {Interlocked.Read(ref _reducedHit)} / dans les calculs de l'IA {Interlocked.Read(ref _reducedAi)}");
            head.Append($" ; tirs sur l'infanterie pendant un impact {Interlocked.Read(ref _hits)}, paires calcul -> stress {Interlocked.Read(ref _pairs)} (non appariées {Interlocked.Read(ref _unpaired)}, calculs de stress pendant un impact {Interlocked.Read(ref _stressIn)}, santé max du jeu = estimée {Share(_pairsConsistent, _pairsChecked)})");
            head.Append($" ; séjours santé clos {_staysClosed} (écartés : baisse sans tir vu {_staysTainted}, santé max inconnue {_staysNoHp}), baisses de santé sans tir vu {_dropsWithoutHit} ; perdus {_evLost}, erreurs {Interlocked.Read(ref _errors)}");
            if (_hpExamples.Count > 0) head.Append(" ; écarts de santé max : " + string.Join(" ; ", _hpExamples));
            lines.Add(head.ToString());

            for (int l = 0; l < NLoc; l++)
            {
                Acc ta = Sum(_a, l), tb = Sum(_b, l);
                if (ta.N + tb.N == 0) continue;
                var sb = new StringBuilder();
                sb.Append($"{LocNames[l]} : A {ta.N} paires, final/brut {R(ta.Final, ta.Raw)}, final/(brut x couvert) {R(ta.Final, ta.MultRaw)}, moyenne des rapports {(ta.N > 0 ? "x" + (ta.RatioSum / ta.N).ToString("0.###", Inv) : "-")}");
                sb.Append($" ; B {tb.N} séjours, final/brut {R(tb.Final, tb.Raw)}, final/(brut x couvert) {R(tb.Final, tb.MultRaw)}");
                bool first = true;
                for (int c = 0; c < NCls; c++)
                {
                    var a = _a[l, c]; var b = _b[l, c];
                    if (a.N + b.N == 0) continue;
                    sb.Append(first ? " ; par munition :" : " ;");
                    first = false;
                    sb.Append($" {ClsNames[c]}{(l == LocVegetation || l == LocForest ? MultText(l, c) : "")} A {a.N} {R(a.Final, a.Raw)} / B {b.N} {R(b.Final, b.Raw)}");
                }
                lines.Add(sb.ToString());
            }

            long gTotal = 0;
            for (int m = 0; m < NMen; m++) for (int c = 0; c < NCls; c++) gTotal += _gN[m, c];
            if (gTotal > 0) lines.Add(GarrisonLine());

            string all = string.Join("\n", lines);
            bool any = Interlocked.Read(ref _calls) + _staysClosed > 0;
            if (!final && (all == _lastReport || !any)) return;
            _lastReport = all;
            foreach (var s in lines) Log(s);
        }

        static string MultText(int loc, int cls) =>
            cls >= DegatsMunitions.CoverCount ? "" : $" (x{F2(loc == LocForest ? ForestMult[cls] : VegetationMult[cls])})";

        static Acc Sum(Acc[,] m, int loc)
        {
            var t = new Acc();
            for (int c = 0; c < NCls; c++) { var a = m[loc, c]; t.N += a.N; t.Raw += a.Raw; t.MultRaw += a.MultRaw; t.Final += a.Final; t.RatioSum += a.RatioSum; }
            return t;
        }

        /// Garrison check: measured final/raw against the assumed building formula alone, and stacked with the open-ground ratio.
        static string GarrisonLine()
        {
            var open = Sum(_a, LocOpen);
            double openAll = open.N >= 5 && open.Raw > 0 ? open.Final / open.Raw : double.NaN;
            var sb = new StringBuilder($"garnison, formule supposée plancher {F3(_floor)} + {F3(_perSoldier)} x soldats puis IgnoreCover de la munition (soldats estimés d'après la santé)" +
                $" ; terrain découvert mesuré {(double.IsNaN(openAll) ? "-" : "x" + openAll.ToString("0.###", Inv))} :");
            for (int m = 0; m < NMen; m++)
            {
                long n = 0; double raw = 0, fin = 0, pred = 0, stack = 0; bool stackOk = true;
                for (int c = 0; c < NCls; c++)
                {
                    if (_gN[m, c] == 0) continue;
                    n += _gN[m, c]; raw += _gRaw[m, c]; fin += _gFinal[m, c]; pred += _gPred[m, c];
                    var oc = _a[LocOpen, c];
                    double or = oc.N >= 5 && oc.Raw > 0 ? oc.Final / oc.Raw : openAll;
                    if (double.IsNaN(or)) stackOk = false; else stack += _gPred[m, c] * or;
                }
                if (n == 0 || !(raw > 0)) continue;
                double meas = fin / raw, p = pred / raw, s = stackOk ? stack / raw : double.NaN;
                sb.Append($" [{MenNames[m]} : {n} paires, mesuré x{meas.ToString("0.###", Inv)}, formule seule x{p.ToString("0.###", Inv)}, formule x découvert {(double.IsNaN(s) ? "-" : "x" + s.ToString("0.###", Inv))} -> {Verdict(meas, p, s)}]");
            }
            return sb.ToString();
        }

        static string Verdict(double meas, double formula, double stacked)
        {
            double ef = formula > 0 ? Math.Abs(meas - formula) / formula : double.MaxValue;
            double es = !double.IsNaN(stacked) && stacked > 0 ? Math.Abs(meas - stacked) / stacked : double.MaxValue;
            if (Math.Min(ef, es) > 0.15) return $"ne correspond à aucune des deux (écarts {Pct(ef)} / {Pct(es)})";
            return ef <= es ? $"correspond à la formule seule, sans cumul avec la résistance en découvert (écart {Pct(ef)})"
                            : $"correspond à la formule cumulée avec la résistance en découvert (écart {Pct(es)})";
        }

        static string Pct(double e) => e >= double.MaxValue / 2 ? "-" : (e * 100).ToString("0.#", Inv) + " %";

        static void ClearBattle()
        {
            _a = NewAcc(); _b = NewAcc();
            _gN = new long[NMen, NCls]; _gRaw = new double[NMen, NCls]; _gFinal = new double[NMen, NCls]; _gPred = new double[NMen, NCls];
            _stays.Clear(); _unitOfEid.Clear(); _unitHp.Clear(); _learnedMaxHp.Clear(); _hpExamples.Clear();
            _pairsChecked = _pairsConsistent = _staysClosed = _staysTainted = _staysNoHp = _dropsWithoutHit = 0;
            Array.Clear(_locCount, 0, NLoc);
            _evRead = Interlocked.Read(ref _evHead); _evLost = 0;
            Interlocked.Exchange(ref _calls, 0); Interlocked.Exchange(ref _offMain, 0); Interlocked.Exchange(ref _reducedHit, 0); Interlocked.Exchange(ref _reducedAi, 0);
            Interlocked.Exchange(ref _hits, 0); Interlocked.Exchange(ref _pairs, 0); Interlocked.Exchange(ref _unpaired, 0); Interlocked.Exchange(ref _stressIn, 0);
            Interlocked.Exchange(ref _errors, 0);
            _table = null; _cover = Array.Empty<byte>();
            _lastReport = null; _depthWarned = false; _terrainBroken = false; _bridgeMask = -1;
        }
    }
}
