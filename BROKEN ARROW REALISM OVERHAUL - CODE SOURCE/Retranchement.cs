// RealismOverhaul - Retranchement (v1.3): an infantry squad that stays put digs in, both sides alike, solo campaign only.
//  The author's timings, kept exactly as he set them (he rejected anything faster as arcade):
//   5 minutes  = a scrape            (a little protection)
//   15 minutes = a fighting position (serious)
//   30 minutes = a prepared position (hard to dislodge by anything but direct fire or assault)
//  Everything is lost the moment the squad moves: its centre leaving a 10 m circle, or embarking, clears the hole.
//  SUPPLY makes it better and faster: inside a supply circle the squad digs 1.5x faster and, once it has spent
//  10 minutes inside that circle at the prepared level, it earns OVERHEAD COVER - a fifth level the others can never
//  reach, and the only one that protects against artillery. What makes a good position is not the shovel, it is the
//  sandbags and the timber the truck brings. The circle used is the game's own supply radius (SupplyConfig.ResupplyRadius,
//  the same number the game draws on screen at exactly twice its value), so what the player sees is what digs.
//  BUILDING AND FOREST STACK on top, they do not replace it: the engine applies its building formula, Couvert.cs applies
//  the vegetation/forest factor, and the entrenchment factor multiplies the result. Infantry dug in for half an hour
//  inside a building really is a strongpoint. Inside a building the entrenchment bonus is halved (the men cannot dig
//  through a concrete floor, and the engine already protects them there): that is also the safety valve which keeps a
//  garrison killable.
//  NO HOOK OF ITS OWN, and nothing written to the game's config, database or saves: there is nothing to restore when the
//  module stops. The factor is applied inside the postfix Couvert.cs already owns (BattleSystemHelpers.CalculateHitDamage,
//  Priority.High), which costs one flat-array read per damage call; the AI scoring calls go through the same postfix, so
//  the AI sees that a dug-in squad is harder to hurt.
//  Main thread every 0.5 s (Planif slot, shared DegatsScan): one dig counter per squad, supply crates of both sides.
//  SAFETY: fully inert when Campaign.MissionInerte (US_M01), outside a solo campaign battle and online; error counter with
//  kill-switch; two unclean sessions in a row switch it off; and a squad the mission script COMMANDS OR WATCHES (a death
//  detector, a counter, a trigger on its group) never goes past the fighting position - when that protection cannot be read
//  at all, no squad on the map goes past it either. Entrenchment can therefore never make a mission impossible to finish.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Retranchement
    {
        const string GuardVersion = "1.1";
        const int MaxErrors = 50;

        internal const byte LvlNone = 0, LvlScrape = 1, LvlFighting = 2, LvlPrepared = 3, LvlOverhead = 4, LvlCount = 5;
        const byte GrpOther = 0, GrpFlat = 1, GrpDirect = 2, GrpIndirect = 3, GrpCount = 4;

        // digging, in seconds of game time (the campaign clock: a pause stops the shovel, a speed-up speeds it)
        const float T1 = 300f, T2 = 900f, T3 = 1800f;     // 5 / 15 / 30 minutes, his figures
        const float TSupply = 600f;                        // 10 minutes actually spent inside a supply circle -> overhead cover
        const float SupplyFactor = 1.5f;                   // a squad in a supply circle digs half as fast again
        const float BuildingShare = 0.5f;                  // inside a building the bonus counts half (see header)
        const float FallbackRadius = 150f;                 // supply radius used only when SupplyConfig cannot be read
        const float StaleAfter = 5f;                       // a squad not seen for this long loses its hole
        const float MaxStep = 2f;                          // dt clamp: a loading hitch must not dig a trench
        const float MinIndirectAoe = 12f;                  // fallback only (no family file): blast radius of a mortar round and up
        const float SupplyEvery = 2f;                      // the crates are looked up this often, not every tick (they do not walk)
        const int MaxSquads = 20000, MaxSupplyErrors = 20;

        static readonly string[] LvlNames = { "à découvert", "trou de tirailleur", "poste de combat", "position préparée", "position couverte" };
        static readonly string[] GrpNames = { "sans effet", "balles et grenades", "tir direct explosif", "artillerie et mortiers" };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // damage taken, index = level * GrpCount + group. Read on the hook side: never rebuilt, never resized.
        static readonly float[] MultOpen =
        {
        //  other  flat   direct indirect
            1f,    1f,    1f,    1f,       // 0 nothing
            1f,    0.80f, 0.90f, 0.75f,    // 1 scrape
            1f,    0.60f, 0.80f, 0.55f,    // 2 fighting position
            1f,    0.45f, 0.70f, 0.40f,    // 3 prepared position
            1f,    0.40f, 0.65f, 0.22f,    // 4 overhead cover (supply only): the artillery column is the point of it
        };
        static readonly float[] MultBuilding = Halved(MultOpen);

        // artillery families of the realism pass; without that file the fallback in Indirect() is used instead
        static readonly HashSet<string> IndirectFamilies = new() { "ARTY", "MORTAR", "MLRS" };

        // ---------------------------------------------------------------- published to the hook side (Couvert's postfix)
        internal static volatile bool Armed;
        internal static volatile byte[] Groups = Array.Empty<byte>();

        // ---------------------------------------------------------------- main-thread state
        sealed class Dug
        {
            public float X, Z;          // anchor of the hole
            public float Dig;           // effective digging seconds
            public float Sup;           // seconds actually spent inside a supply circle
            public float Seen;          // game time of the last tick that saw this squad
            public byte Level;
            public bool Supplied, Capped;
        }
        static readonly Dictionary<int, Dug> _dug = new();
        static readonly List<int> _gone = new();
        static readonly HashSet<int> _loaded = new();       // squads Couvert saw embarked since the last tick
        static readonly HashSet<int> _supSeen = new();      // supply uids already taken in this tick

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<float> _move;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;

        static LuaMap _map;
        static DegatsMunitions _table;
        static float[] _supX = new float[64], _supZ = new float[64];
        static byte[] _supSide = new byte[64];
        static int _supN, _supDropped;
        static float _radius = FallbackRadius;
        static bool _radiusRead, _radiusWarned;
        static bool _refused, _sessionArmed, _everOnline, _netLogged, _off, _offLogged, _famLogged, _hookWarned;
        static bool _saidScrape, _saidCover, _supBroken, _blindLogged;
        static float _nextTick, _nextReport, _lastGame, _nextSup, _errClear;
        static int _wait, _errors, _supErrors, _sideMismatch, _localUid = int.MinValue;
        static long _reductions;
        static readonly int[] _levelCount = new int[LvlCount];
        static readonly int[] _grpCount = new int[GrpCount];
        static int _suppliedNow, _cappedNow, _lostByMove, _lostByBoard, _reanchored;
        static string _lastReport;

        static void Log(string s) => Mod.Log.Msg("[RETRANCHEMENT] " + s);

        static float[] Halved(float[] src)
        {
            var d = new float[src.Length];
            for (int i = 0; i < src.Length; i++) d[i] = 1f - (1f - src[i]) * BuildingShare;
            return d;
        }

        // ---------------------------------------------------------------- hook side (no allocation, no logging, no Unity call)

        /// Damage factor of a dug-in squad. level comes from Couvert's snapshot, ammoId from its ammunition cache.
        /// One bounds-checked array read; returns 1 (vanilla damage) for anything it does not know.
        internal static float MultOf(byte level, int ammoId, bool inBuilding)
        {
            if (!Armed || level == 0 || level >= LvlCount) return 1f;
            var g = Groups;
            int grp = (uint)ammoId < (uint)g.Length ? g[ammoId] : GrpOther;
            if ((uint)grp >= GrpCount) grp = GrpOther;
            return (inBuilding ? MultBuilding : MultOpen)[level * GrpCount + grp];
        }

        // ---------------------------------------------------------------- main-thread API (Couvert)

        /// Entrenchment level of this squad, 0 when it has none. Main thread, called once per squad per snapshot.
        internal static byte LevelOf(int uid)
        {
            if (!Armed || uid <= 0) return LvlNone;
            return _dug.TryGetValue(uid, out var d) ? d.Level : LvlNone;
        }

        /// Couvert saw this squad embarked: it is not in its hole any more, so the hole is lost (his rule).
        internal static void NoteLoaded(int uid)
        {
            if (uid > 0 && _loaded.Count < MaxSquads) _loaded.Add(uid);
        }

        /// True while the module wants Couvert's postfix installed, even when forest cover itself is switched off.
        internal static bool Wanted => _enabled != null && _enabled.Value && !_refused;

        // ---------------------------------------------------------------- lifecycle

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Retranchement");
            _enabled = c.CreateEntry("Retranchement", true, description: Build.Desc(
                "L'infanterie qui ne bouge pas se retranche : 5 min = trou de tirailleur, 15 min = poste de combat, 30 min = position préparée. " +
                "Dans un cercle de ravitaillement elle creuse plus vite et finit par obtenir une position couverte, la seule qui protège de l'artillerie. " +
                "Tout est perdu dès que l'escouade bouge. Les deux camps.",
                "L'infanterie qui ne bouge pas se retranche et encaisse mieux ; tout est perdu dès qu'elle bouge."));
            _move = c.CreateEntry("RayonImmobilite", 10f, description: Build.Desc(
                "Distance (mètres) que le centre de l'escouade peut parcourir sans perdre son trou (minimum 3, maximum 40)"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
        }

        /// Per-battle reset (Campaign.ResetSession: mission start, restart, campaign end).
        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        /// Called by the integrator when the battle-end screen appears.
        internal static void OnBattleEnd() => EndBattle("fin de bataille");

        static void EndBattle(string why)
        {
            Armed = false;
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean != null && _unclean.Value != 0) { _unclean.Value = 0; try { MelonPreferences.Save(); } catch { } }
            try { Report(true); } catch (Exception e) { Log("bilan illisible : " + e.GetBaseException().Message); }
            ClearBattle();
            Log($"fin de bataille ({why})");
        }

        static void ClearBattle()
        {
            _dug.Clear(); _loaded.Clear(); _gone.Clear(); _supSeen.Clear();
            _table = null; Groups = Array.Empty<byte>(); _map = null;
            _supN = 0; _supDropped = 0; _nextSup = 0f;
            _radius = FallbackRadius; _radiusRead = false; _radiusWarned = false; _famLogged = false; _hookWarned = false;
            _saidScrape = false; _saidCover = false; _supBroken = false; _blindLogged = false;
            _errors = 0; _supErrors = 0; _sideMismatch = 0; _errClear = 0f; _reanchored = 0;
            Interlocked.Exchange(ref _reductions, 0); _lostByMove = 0; _lostByBoard = 0; _suppliedNow = 0; _cappedNow = 0;
            Array.Clear(_levelCount, 0, LvlCount); Array.Clear(_grpCount, 0, GrpCount);
            _localUid = int.MinValue; _lastGame = 0f; _lastReport = null;
        }

        // ---------------------------------------------------------------- frame

        /// Called every frame while in a campaign mission. The work (and its closures) lives in Tick.
        static float _tickReal;
        static readonly Action _aTick = () => Tick(_tickReal);

        internal static void Frame()
        {
            if (_enabled == null || _move == null || !_enabled.Value || _refused)
            {
                if (Armed) { Armed = false; _dug.Clear(); Log("désactivé : tous les retranchements en cours sont perdus"); }
                _loaded.Clear();                                    // Couvert keeps filling it even when we are off: it must not grow for the whole game
                return;
            }
            if (Campaign.MissionInerte) { if (Armed) { Armed = false; _dug.Clear(); } _loaded.Clear(); return; }
            float real = UnityEngine.Time.realtimeSinceStartup;
            if (real < _nextTick) return;
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;
            _nextTick = real + 0.5f;
            _tickReal = real;
            Guard.Run("Retranchement.Tick", _aTick);
        }

        static void Tick(float real)
        {
            var gc = GameController._instance;
            var session = gc?._GameSession_k__BackingField;
            if (session?.CurrentPlayer == null) { _loaded.Clear(); if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (Armed) { Armed = false; _dug.Clear(); Log("partie en ligne : retranchement coupé"); } _loaded.Clear(); return; }
            if (!_sessionArmed && !StartBattle(real)) return;

            if (_off || _errors >= MaxErrors)
            {
                _off = true; Armed = false; _dug.Clear(); _loaded.Clear();
                if (!_offLogged)
                {
                    _offLogged = true;
                    Mod.Log.Warning($"[RETRANCHEMENT] {MaxErrors} erreurs : retranchement coupé jusqu'au redémarrage du jeu (dégâts normaux)");
                    Mod.Notify(TxtKey.N_ENTRENCH_OFF_ERRORS);
                }
                return;
            }

            // the factor is applied by Couvert's postfix: without it the levels would be a lie in the log, and every line below
            // (radius, supply scan, squad loop) would be paid for nothing. Tested first, before any work.
            if (!Couvert.HookReady)
            {
                Armed = false; _loaded.Clear();
                if (!_hookWarned) { _hookWarned = true; Log("le correctif des dégâts n'est pas en place : le retranchement attend, rien n'est creusé"); }
                return;
            }
            if (_hookWarned) { _hookWarned = false; Log("correctif des dégâts en place : le retranchement protège de nouveau"); }

            var table = DegatsMunitions.ForBattle();
            if (table == null) { Armed = false; return; }
            if (!ReferenceEquals(_table, table)) { _table = table; Groups = BuildGroups(table); LogTable(table); }

            float game = UnityEngine.Time.time;
            float dt = _lastGame <= 0f ? 0f : game - _lastGame;
            _lastGame = game;
            if (dt < 0f) dt = 0f; else if (dt > MaxStep) dt = MaxStep;      // pause, loading, restart: no free digging

            ReadRadius();
            ScanSupply(real);
            try { var cp = session.CurrentPlayer; if (cp != null) _localUid = cp.UID; } catch { }

            // when the script protection is not readable, NOBODY is known to be commanded by the mission: everyone is capped at the
            // fighting position (fail closed), exactly as EnemyAi and Renforts fall silent in that case.
            string blind = Missions.ProtectionNotReady();
            if ((blind != null) != _blindLogged)
            {
                _blindLogged = blind != null;
                Log(blind != null
                    ? "unités du script pas encore sûres (" + blind + ") : aucune escouade ne dépasse le poste de combat tant que c'est le cas"
                    : "unités du script de nouveau sûres : les escouades peuvent atteindre la position préparée");
            }

            var scan = DegatsScan.Get(real);
            Array.Clear(_levelCount, 0, LvlCount);
            _suppliedNow = 0; _cappedNow = 0;
            int errBefore = _errors;
            float move = Math.Clamp(_move.Value, 3f, 40f), move2 = move * move;
            var all = scan.All;
            for (int k = 0; k < all.Count; k++)
            {
                var u = all[k];
                if (!u.Infantry) continue;
                int uid = u.Uid;
                if (uid <= 0) continue;
                try { Step(u, uid, game, dt, move2, blind != null); }
                catch { _errors++; }
            }
            _loaded.Clear();
            // the counter must not add up a whole battle: a clean minute forgets what failed before (a passing glitch never
            // switches the module off, repeated failures still reach MaxErrors in about a minute)
            if (_errors != errBefore) _errClear = real + 60f;
            else if (real >= _errClear) _errors = 0;

            // squads that left the scan (dead, gone) lose their hole after a short grace period
            if (_dug.Count > 0)
            {
                _gone.Clear();
                foreach (var kv in _dug) if (game - kv.Value.Seen > StaleAfter) _gone.Add(kv.Key);
                for (int i = 0; i < _gone.Count; i++) _dug.Remove(_gone[i]);
                _gone.Clear();
            }

            Armed = true;                                            // Couvert.HookReady was checked at the top of the tick
            if (real >= _nextReport) { _nextReport = real + 60f; Report(false); }
        }

        /// One squad: movement test, digging, level. Main thread, no allocation.
        static void Step(DegatsUnit u, int uid, float game, float dt, float move2, bool scriptBlind)
        {
            if (!_dug.TryGetValue(uid, out var d))
            {
                _levelCount[LvlNone]++;
                if (_dug.Count >= MaxSquads) return;
                _dug[uid] = new Dug { X = u.Pos.x, Z = u.Pos.z, Seen = game };
                return;
            }
            float dx = u.Pos.x - d.X, dz = u.Pos.z - d.Z, d2 = dx * dx + dz * dz;
            bool boarded = _loaded.Contains(uid);
            if (boarded || d2 > move2)
            {
                // A squad that drifted a little without being sent anywhere (looking for cover, a building falling on it, its own
                // shuffle) has not moved position: the hole follows it and the clock keeps running. Only a real move - or embarking -
                // loses everything. The engine's own "no order in progress" answer is read here only, a handful of squads per tick.
                bool settled = false;
                if (!boarded && d2 < move2 * 9f)
                {
                    try { settled = u.U.IsIdle(); } catch { settled = false; }
                }
                if (settled) { d.X = u.Pos.x; d.Z = u.Pos.z; _reanchored++; }
                else
                {
                    // "everything is lost the moment the squad moves"
                    if (d.Dig > 0f) { if (boarded) _lostByBoard++; else _lostByMove++; }
                    d.X = u.Pos.x; d.Z = u.Pos.z; d.Dig = 0f; d.Sup = 0f; d.Level = LvlNone; d.Capped = false;
                    d.Supplied = false; d.Seen = game;
                    _levelCount[LvlNone]++;
                    return;
                }
            }
            d.Seen = game;
            bool sup = InSupply(u.Pos.x, u.Pos.z, u.Side);
            d.Supplied = sup;
            d.Dig += dt * (sup ? SupplyFactor : 1f);
            // the ten minutes that earn overhead cover are spent AT the prepared level, not before it: what the sandbags and the
            // timber need is a finished position. Counting them from the first second would have given overhead cover at 20 minutes
            // and made the prepared position unreachable for any supplied squad (the header promises half an hour).
            if (sup) { _suppliedNow++; if (d.Dig >= T3) d.Sup += dt; }

            byte lvl = d.Dig >= T3 ? (d.Sup >= TSupply ? LvlOverhead : LvlPrepared)
                     : d.Dig >= T2 ? LvlFighting
                     : d.Dig >= T1 ? LvlScrape : LvlNone;

            // a squad the mission script commands, or one whose group the script watches (a death detector, a counter, a trigger),
            // never goes past the fighting position: the mod must never make a mission impossible to finish. When the protection
            // itself is not readable, nobody goes past it either (fail closed).
            d.Capped = false;
            if (lvl > LvlFighting && (scriptBlind || Missions.ScriptReason(uid, game) != null || Missions.WatchedMember(uid)))
            { lvl = LvlFighting; d.Capped = true; _cappedNow++; }

            if (lvl != d.Level)
            {
                d.Level = lvl;
                if (_localUid != int.MinValue && u.Owner == _localUid) Announce(lvl);   // both are int.MinValue when unreadable
            }
            _levelCount[lvl]++;
        }

        static void Announce(byte lvl)
        {
            if (lvl == LvlScrape && !_saidScrape) { _saidScrape = true; Mod.Notify(TxtKey.N_ENTRENCH_READY); }
            else if (lvl == LvlOverhead && !_saidCover) { _saidCover = true; Mod.Notify(TxtKey.N_ENTRENCH_COVER); }
        }

        static bool InSupply(float x, float z, int side)
        {
            float r2 = _radius * _radius;
            for (int i = 0; i < _supN; i++)
            {
                if (_supSide[i] != side) continue;
                float dx = _supX[i] - x, dz = _supZ[i] - z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- battle start, supply, ammunition groups

        static bool StartBattle(float real)
        {
            if (_unclean.Value >= 2)
            {
                _refused = true; Armed = false;
                Mod.Log.Warning("[RETRANCHEMENT] désactivé : les deux dernières parties ne se sont pas terminées normalement");
                return false;
            }
            _sessionArmed = true;
            _unclean.Value = _unclean.Value + 1;
            try { MelonPreferences.Save(); } catch { }
            ClearBattle();
            _nextReport = real + 60f;
            Log($"prêt : infanterie des deux camps ; trou de tirailleur {T1 / 60f:0} min, poste de combat {T2 / 60f:0} min, " +
                $"position préparée {T3 / 60f:0} min ; dans un cercle de ravitaillement on creuse x{F2(SupplyFactor)} plus vite et " +
                $"{TSupply / 60f:0} min de plus passées dedans UNE FOIS la position préparée atteinte ouvrent la position couverte " +
                $"(soit une demi-heure sur place en tout) ; tout est perdu si le centre de l'escouade " +
                $"s'écarte de plus de {Math.Clamp(_move.Value, 3f, 40f):0} m ou embarque ; en bâtiment le bonus compte pour moitié");
            return true;
        }

        static void ReadRadius()
        {
            try
            {
                var sc = GameCfg.Instance?.SupplyConfig;
                if (sc == null) { WarnRadius("configuration du ravitaillement absente"); return; }
                float r = sc.ResupplyRadius;
                if (!(r > 1f) || r > 5000f) { WarnRadius($"rayon de ravitaillement invalide ({F2(r)})"); return; }
                if (!_radiusRead || Math.Abs(r - _radius) > 0.5f)
                    Log($"rayon de ravitaillement du jeu : {F2(r)} m (le cercle affiché fait exactement le double)");
                _radius = r; _radiusRead = true;
            }
            catch (Exception e) { WarnRadius(e.GetBaseException().Message); }
        }

        static void WarnRadius(string why)
        {
            if (_radiusWarned) return;
            _radiusWarned = true;
            _radius = FallbackRadius;
            Log($"rayon de ravitaillement illisible ({why}) : {F2(FallbackRadius)} m utilisés par défaut");
        }

        /// Crates and depots of both sides, every SupplyEvery seconds (a crate does not walk: the table stays valid between two passes).
        /// The side stored is the side the search was ASKED for, because that is the very convention the squads come from: the shared
        /// scan lists them with GetUnits(..., side, -1) (CalibreMesure.cs). GetOwnerTeamID may speak another numbering (scenario team
        /// ids), so it is only used to count disagreements in the log, never to decide who digs faster. Each uid is kept once.
        /// A search the game refuses does not count against the entrenchment: it only means "no supply circle" (digging goes on).
        static void ScanSupply(float real)
        {
            if (_supBroken) { _supN = 0; return; }
            if (real < _nextSup) return;                              // twice a second was a full-map search for a 1.5x factor
            _nextSup = real + SupplyEvery;
            _supN = 0; _supDropped = 0;
            _supSeen.Clear();
            _map ??= new LuaMap();
            for (int side = 0; side < 2; side++)
            {
                Il2CppReferenceArray<LuaSupply> arr = null;
                try { arr = _map.GetSupplyPoints(V3.zero, 1_000_000f, side, -1); }
                catch { SupplyFailed(); continue; }
                int len = arr == null ? 0 : arr.Length;
                for (int i = 0; i < len; i++)
                {
                    try
                    {
                        var s = arr[i];
                        if (s == null || !s.IsAlive() || s.GetSupplyAmount() <= 0) continue;
                        if (!_supSeen.Add(s.UID)) continue;
                        try { int owner = s.GetOwnerTeamID(); if ((owner == 0 || owner == 1) && owner != side) _sideMismatch++; } catch { }
                        if (_supN >= _supX.Length)
                        {
                            if (_supX.Length >= 4096) { _supDropped++; continue; }
                            Array.Resize(ref _supX, _supX.Length * 2);
                            Array.Resize(ref _supZ, _supZ.Length * 2);
                            Array.Resize(ref _supSide, _supSide.Length * 2);
                        }
                        var p = s.Position;
                        _supX[_supN] = p.x; _supZ[_supN] = p.z; _supSide[_supN] = (byte)side; _supN++;
                    }
                    catch { SupplyFailed(); }
                }
            }
        }

        /// The supply search failed. It has its own counter: an unreadable crate list must never switch the digging off.
        static void SupplyFailed()
        {
            if (++_supErrors < MaxSupplyErrors) return;
            _supBroken = true; _supN = 0;
            Log($"ravitaillement illisible ({MaxSupplyErrors} erreurs) : le retranchement continue sans cercle de ravitaillement (ni creusement plus rapide, ni position couverte)");
        }

        /// Ammunition Id -> entrenchment group, once per battle, from the table CalibreMesure already builds.
        static byte[] BuildGroups(DegatsMunitions t)
        {
            int n = t.Cover.Length;
            var g = new byte[n];
            Array.Clear(_grpCount, 0, GrpCount);
            for (int id = 1; id < n; id++)
            {
                byte cov = t.Cover[id];
                byte grp;
                if (cov >= DegatsMunitions.CoverBall && cov <= DegatsMunitions.CoverGrenade) grp = GrpFlat;
                else if (cov >= DegatsMunitions.CoverCannonHe && cov <= DegatsMunitions.CoverRocket) grp = GrpDirect;
                else if (Indirect(t, id)) grp = GrpIndirect;
                else grp = GrpOther;
                g[id] = grp;
                _grpCount[grp]++;
            }
            return g;
        }

        /// Artillery, mortars and MLRS: the fire a hole in the ground really protects from. Missiles, bombs, plane guns,
        /// armour-piercing shells and thermobaric rounds are deliberately left out - a trench does not stop those.
        static bool Indirect(DegatsMunitions t, int id)
        {
            // the family file answers for almost every round: its answer is read FIRST, so the name is only put in upper case
            // (one string per round) on the fallback path
            bool known = t.Families.TryGetValue(id, out var fam) && fam != null;
            if (known && !IndirectFamilies.Contains(fam)) return false;
            if (!known && t.FamilyFile) return false;                        // families are known and this round is not artillery
            if (!known && !(t.Armor[id] == DegatsMunitions.ArmorIgnore && t.Aoe[id] >= MinIndirectAoe)) return false;
            string name = t.Names.TryGetValue(id, out var nm) ? nm : null;    // a trench does not stop a thermobaric round
            return name == null || !DegatsMunitions.IsThermobaric(name.ToUpperInvariant());
        }

        static void LogTable(DegatsMunitions t)
        {
            if (_famLogged) return;
            _famLogged = true;
            var sb = new StringBuilder("munitions classées pour le retranchement :");
            for (int g = 0; g < GrpCount; g++) sb.Append($" {GrpNames[g]} {_grpCount[g]} ;");
            sb.Append($" familles du réalisme {(t.FamilyFile ? "lues" : $"absentes (repli : dégâts non perforants et souffle >= {F2(MinIndirectAoe)} m)")}");
            sb.Append(" ; facteurs :");
            for (byte l = 1; l < LvlCount; l++)
                sb.Append($" [{LvlNames[l]} : balles x{F2(MultOpen[l * GrpCount + GrpFlat])}, tir direct x{F2(MultOpen[l * GrpCount + GrpDirect])}, " +
                          $"artillerie x{F2(MultOpen[l * GrpCount + GrpIndirect])} ; en bâtiment {F2(MultBuilding[l * GrpCount + GrpIndirect])} contre l'artillerie]");
            Log(sb.ToString());
        }

        // ---------------------------------------------------------------- reports

        static string F2(double v) => v.ToString("0.##", Inv);

        /// Hook side (Couvert's postfix), any thread: counts one damage call the entrenchment actually reduced.
        internal static void NoteReduction() => Interlocked.Increment(ref _reductions);

        static void Report(bool final)
        {
            var sb = new StringBuilder(final ? "bilan :" : "relevé :");
            int squads = 0;
            for (int l = 0; l < LvlCount; l++) squads += _levelCount[l];
            sb.Append($" escouades d'infanterie {squads} (");
            for (int l = 0; l < LvlCount; l++) sb.Append($"{(l > 0 ? ", " : "")}{LvlNames[l]} {_levelCount[l]}");
            sb.Append($") ; dans un cercle de ravitaillement {_suppliedNow} ; caisses et dépôts suivis {_supN}" +
                      $"{(_supDropped > 0 ? $" (+{_supDropped} ignorés)" : "")} (rayon {F2(_radius)} m{(_radiusRead ? "" : ", par défaut")})");
            sb.Append($" ; bridées par le script de la mission {_cappedNow}{(_blindLogged ? " (toutes : script pas encore sûr)" : "")}" +
                      $" ; trous perdus : déplacement {_lostByMove}, embarquement {_lostByBoard} ; trous suivis sur place {_reanchored}");
            sb.Append($" ; dégâts réduits par le retranchement {Interlocked.Read(ref _reductions)} ; erreurs {_errors}" +
                      $"{(_supErrors > 0 ? $", ravitaillement {_supErrors}{(_supBroken ? " (abandonné)" : "")}" : "")}" +
                      $"{(_sideMismatch > 0 ? $" ; caisses dont le camp déclaré diffère du filtre {_sideMismatch}" : "")}");
            string all = sb.ToString();
            if (!final && (all == _lastReport || squads == 0)) return;
            _lastReport = all;
            Log(all);
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
    }
}
