// PorteeMiniCarte: the realistic MINIMUM ranges of the artillery (lot OPTION_PORTEES_MINI_REELLES of Munitions.csv: 3000 m for the
//  152/155/203 mm howitzers, 2000 m for the 105/122 mm, 4500 m for the rocket artillery and the ballistic missiles, 5000 m for the Grad)
//  are right on a full map and wrong inside the playable zone a campaign mission opens phase by phase. Measured on the campaign
//  missions, the smallest active zones are 600 x 900 m (RU_C04 landing), 750 x 750 m and 800 x 800 m, 1400 x 1400 m (Tallinn phase 1),
//  1660 x 2700 m (US_M02 phase 1): with a 3000 m minimum a howitzer standing in one of them can fire nowhere at all and the game says
//  nothing on screen. The rule the author approved: the minimum range never exceeds a QUARTER of the diagonal of the zone the mission
//  is playing in right now, recomputed at every zone change. It only ever LOWERS a value, never raises one above the realistic value
//  the CSV wrote, and it does nothing at all outside a campaign mission or when the lot is not applied.
//  ROWS: the ammunition ids of that lot and no others, read from the same embedded reel\Munitions.csv the lot is written from, kept
//  only when the row really is indirect fire (artillery, mortar, rocket artillery, ballistic) and carries no air-defence target bit.
//  The lot also holds three anti-aircraft missiles (S-300V, Buk-M3) whose minimum range is a guidance distance, not a gun's dead zone:
//  those are left alone, and so is every ATGM (they are not in this lot).
//  WRITES: Realism.SetValueForOverride under this module's own journal lot, so everything is put back when the mod stops, and so the
//  real-stats pipeline can take its own lot out from under it without a fight (the journal layers the two lots per property). The
//  module never writes a value the lot did not already write.
//  COPIES: the game clones the ammunition rows into every loaded unit, so the table row alone does not reach the gun that fires. A
//  copy made AFTER a write already follows it (UnitCopies mirrors the table row for every property the journal holds, MinimalRange
//  included); the copies made BEFORE are walked here, from the loader's loaded units, and aligned on the table row, journaled the
//  same way.
//  GIVING BACK: the table rows come back through the journal (RestoreLot), but a copy the game cloned WHILE the ceiling was on was
//  born with the capped value and holds no journal entry of the game's value, so the journal alone would pin it at the capped value
//  for the rest of the session. So every give-back walks the copies once more, AFTER the lot is restored, and aligns them on the
//  table rows with a plain write - the same plain write UnitCopies makes on its own restore walk (Ctx.Journal == false). The log line
//  then says how many copies were really given back instead of claiming it.
//  WHOLE MAP: the ceiling only ever bites on a zone the mission really restricted. The play zone is compared with the controller's own
//  FullMapBounds: when it is the whole map (its diagonal within a few per cent of the map's), no ceiling is applied at all and the
//  realistic minimum ranges of the lot are in force, which is what this header and the setting promise. Without that test the biggest
//  map (9000 x 9000 m, diagonal 12728 m) would give a 3182 m ceiling and lower every rocket artillery and every ballistic missile of
//  the lot everywhere and for good, and a 4 x 4 km map would lower all the howitzers too: the exact opposite of the point.
//  DRIVEN BY THE ZONE PATCHES, WITH ONE RETRY: the two methods that change the zone are taken, exactly as Spawns.cs already takes them
//  for the reinforcement places: a postfix that reads the controller's own PlayZoneBounds property and takes no parameter of the
//  patched method. Bounds is a blittable struct passed by value and nothing is marshalled by reference, and the same pair of patches
//  has been running in this mod for versions. A zone change happens a handful of times per mission: nothing of this file is on a hot
//  path. The first zone of a mission can arrive BEFORE the realism is on the database (it is applied from the 2 s slow tick), and a
//  refused pass used to end there, silently, leaving phase 1 - the very case this module exists for - at the full 3000 m. So
//  Mod.OnUpdate calls Frame() every frame: it does nothing at all, and allocates nothing, unless a pass is waiting and 2 s have passed
//  since the last try, and it works from the last zone controller seen. Every refusal is logged once, so the module can be read from
//  the log instead of being guessed at. THE RETRY BELONGS TO ITS BATTLE: the zone patches are installed for every battle, so the zone
//  of the prologue without the mod, of a skirmish and of a multiplayer game reaches this module too, where Frame() is never called -
//  a retry armed there would survive the battle and be run with that dead controller on the first frame of the NEXT campaign mission,
//  which would read the PREVIOUS battle's bounds and put a ceiling from another map on this mission's rows. So no retry is armed at
//  all outside a campaign mission, and the campaign mission the retry was armed in is recorded with it and checked on every frame.
//  SAFETY: error counter with kill-switch (the realistic values are given back when it trips), every hook body and the tick in
//  try/catch, and a no-op whenever the realism is not applied to the database in use or the lot is not accepted.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.DataBase.Models;
using Il2CppBrokenArrow.Shared.Ecs;                  // DataBaseService lives here, not in Il2CppBrokenArrow.DataBase (CopiesUnites.cs does the same)
using PlayZoneCtl = Il2CppBrokenArrow.Client.Ecs.Controllers.PlayableZone;

namespace RealismOverhaul
{
    static class PorteeMiniCarte
    {
        const string Tag = "[PORTEE-MINI]";
        const string LotKey = "OPTION_PORTEE_MINI_CARTE";        // journal lot of this module alone
        const float Share = 0.25f;                                // a quarter of the diagonal: the rule the author approved
        const float WholeShare = 0.95f;                           // the play zone IS the whole map when its diagonal reaches this much of the map's
                                                                  //  (the widest campaign phase zone measured is barely 40 % of its map: no confusion possible,
                                                                  //   and the log prints the share so a map whose full zone is inset can be seen at once)
        const float NoCeiling = float.MaxValue;                   // no ceiling at all: every row keeps the realistic value of the lot
        const float MinSide = 100f;                               // below this the zone is not set yet (same test as Spawns.cs)
        const float Epsilon = 0.5f;                               // metres: below this two minimum ranges are the same value
        const int MaxErrors = 20;
        const int MaxExamples = 3;
        const long RetryMs = 2000;                                // retry delay of Frame(), the period of the slow tick the realism is applied from
        const int MaxTries = 120;                                 // about four minutes of retries: far more than the realism ever needs to be applied
        const int MaxWhy = 12;                                    // refusal reasons kept in the log-once set

        // Refusal reasons: constants, so a retry every 2 s builds no string at all and the log-once set only compares known values.
        const string WhySetting = "réglage coupé";
        const string WhyMod = "mod coupé";
        const string WhyMission = "pas de mission de campagne";
        const string WhyReal = "vraies stats coupées";
        const string WhyDb = "réalisme non appliqué à la base en cours";
        const string WhyLot = "lot " + RealStats.LotMinimales + " non appliqué";
        const string WhyZone = "zone jouable pas encore lisible";

        // indirect fire: artillery (20), mortar (30), rocket artillery (40), ballistic missile (300). The same numbers Assistants.cs
        // uses to tell an artillery piece from a direct-fire gun. A missile trajectory is never in this list.
        static readonly int[] Indirect = { 20, 30, 40, 300 };
        // TargetType bits of an air-defence round (aircraft, projectile, SEAD, cruise, ballistic): the bits AntiHeliPortee.cs also
        // refuses to cap. Such a row's minimum range is a guidance distance and is never touched here.
        const long AirDefenceBits = 16 | 128 | 256 | 512 | 1024;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class Row
        {
            public Ammunitions R;
            public int Id;
            public string Name;
            public float Real;            // realistic minimum range of the lot: the ceiling never goes above it
            public float Now;             // value the table row holds: written by the last pass, or read back on the give-back path
                                          // (0 = row left alone, copies too)
        }

        static MelonPreferences_Entry<bool> _enabled;
        static bool _prefsDone;

        static Row[] _rows;
        static readonly Dictionary<int, Row> _byId = new();
        static readonly HashSet<IntPtr> _seen = new();             // objects already walked in the current pass (reused, never grown twice)
        static DataBaseSourceData _src;                            // held reference: the database _rows belongs to (no address reuse)
        static PropertyInfo _pMin;

        static float _ceiling = float.NaN;                         // ceiling of the last pass (NaN = no pass since the last reset)
        static bool _whole;                                        // last pass ran on the whole map: no ceiling, realistic values in force
        static float _mapDiag;                                     // diagonal of the whole map, last readable value (0 = not readable yet);
                                                                   //  forgotten with the rest of the state, never carried to another mission
        static int _writes;                                        // journal entries this module held after the last pass
        static int _errors;
        static DataBaseSourceData _noRowsSrc;                      // held reference: database whose build found nothing, not read again (no address reuse)
        static bool _off, _propWarned, _mapWarned, _rowsLogged, _assistWarned;
        static bool _giveBack;                                     // copy walk: plain write back to the table value (give-back path, nothing journaled)

        // retry (Frame): a pass refused for a reason that can change on its own is tried again from the zone controller kept here
        static PlayZoneCtl _zone;                                  // held reference: the controller of the last zone change seen
        static bool _zoneLive;                                     // that zone was seen inside a campaign mission really being played
        static string _zoneUid;                                    // and in THAT mission (null = seen before the mission id was set)
        static string _what;                                       // what that change was, for the log
        static bool _pending;
        static long _nextTry;                                      // Environment.TickCount64 of the next try (no Unity call: the zone hook is not ours to place)
        static int _tries, _wait;
        static readonly HashSet<string> _why = new();              // refusal reasons already logged for the zone being worked on

        static void Log(string s) => Mod.Log.Msg(Tag + " " + s);

        // ------------------------------------------------------------ settings (created lazily, own category)
        static void EnsurePrefs()
        {
            if (_prefsDone) return;
            _prefsDone = true;
            var c = MelonPreferences.CreateCategory("RealismOverhaul_PorteeMiniCarte");
            _enabled = c.CreateEntry("PlafondSelonZoneJouable", true, description: Build.Desc(
                "true = en mission de campagne, quand la mission restreint la zone jouable, la portée minimale de l'artillerie ne dépasse jamais le quart de la " +
                "diagonale de cette zone, et elle est recalculée à chaque changement de zone. Une valeur n'est jamais augmentée au-dessus de la portée minimale " +
                "réelle : dès que la zone s'agrandit, les vraies valeurs reviennent, et quand la zone jouable est la carte entière aucune limite n'est appliquée. " +
                "Sans effet hors campagne, et sans effet si le lot OPTION_PORTEES_MINI_REELLES n'est pas appliqué. " +
                "false = les portées minimales réelles s'appliquent telles quelles, même dans une zone où l'artillerie ne peut plus tirer.",
                "Portée minimale de l'artillerie limitée au quart de la diagonale de la zone jouable."));
            // written to MelonPreferences.cfg right away, or the player has nothing to edit until another module happens to save (CercleRavito.cs)
            try { MelonPreferences.Save(); } catch { }
        }

        /// Read by the two patch classes: the hooks are only installed when the module may work.
        internal static bool HooksWanted
        {
            get
            {
                try { EnsurePrefs(); return _enabled == null || _enabled.Value; }
                catch { return false; }
            }
        }

        // ------------------------------------------------------------ zone change and retry (the two entry points)
        internal static void OnZone(PlayZoneCtl pz, string what)
        {
            if (_off || pz == null) return;
            _zone = pz;                                   // held: a refused pass is tried again from this controller
            // the battle this zone belongs to, recorded with it: Frame() drops the retry as soon as the present no longer matches, so a
            // controller of a battle that is over is never read again. Plain field reads on a static class of the mod: nothing to throw.
            bool live = Campaign.InCampaign && !Campaign.MissionInerte;
            _zoneLive = live;
            _zoneUid = live ? Campaign.MissionUid : null;
            _what = what;
            _tries = 0;
            // new zone: the reasons the previous one was refused for are stale. Outside a campaign mission the single refusal line stays
            // logged once instead of once per zone change: a skirmish or a multiplayer game changes its zone too, and says nothing here.
            if (live) _why.Clear();
            Run(pz, what);
        }

        /// Called every frame from Mod.OnUpdate (Perf.Run), inside a campaign mission only. Does nothing at all - no game call, no
        /// allocation - unless a pass is waiting and the retry delay has passed. A pass only waits for a reason that can change on its
        /// own: the realism is applied from the 2 s slow tick, a suspended lot can be resumed, the zone can become readable. The
        /// player's own switches are never waited on (the patches are not even installed when the setting is off).
        internal static void Frame()
        {
            if (_off || !_pending) return;
            long now = Environment.TickCount64;
            if (now < _nextTry) return;
            var pz = _zone;
            if (pz == null) { Rest(); return; }
            // the retry is only ever run in the battle its zone was seen in. Outside one, or in another campaign mission, the controller
            // held is that of a battle that is over and would hand back THAT battle's bounds: the retry is dropped instead, silently.
            // The case the retry exists for is kept: inside a campaign mission the zone can arrive before the mission id is set, and the
            // mission is then bound to the retry as soon as it is known.
            if (!_zoneLive || !Campaign.InCampaign || Campaign.MissionInerte) { Rest(); return; }
            string uid = Campaign.MissionUid;
            if (_zoneUid == null) _zoneUid = uid;
            else if (uid == null || !string.Equals(_zoneUid, uid, StringComparison.Ordinal)) { Rest(); return; }
            if (_tries >= MaxTries)
            {
                Log("plafond des portées minimales : abandon des nouveaux essais après " + MaxTries.ToString(Inv) + " tentatives, les portées minimales réelles restent en place");
                Rest();
                return;
            }
            if (!Planif.Take(ref _wait)) return;          // one heavy module job per frame (Planif.cs)
            _nextTry = now + RetryMs;
            _tries++;
            int errors = _errors;
            Run(pz, _what);
            // the retry itself threw (a zone controller of a mission being left, anything else): the controller is dropped instead of
            // being read again every 2 s, and a real zone change starts the module again. The error budget stays for the zone patches.
            if (_errors != errors) Rest();
        }

        /// One attempt, from a zone change or from a retry. Everything this module does runs inside this try/catch.
        static void Run(PlayZoneCtl pz, string what)
        {
            try
            {
                EnsurePrefs();
                if (_enabled != null && !_enabled.Value) { Refuse(WhySetting, false); return; }
                Pass(pz, what);
            }
            catch (Exception e) { Fail(e); }
        }

        /// A pass the state refused: the reason is logged once for the zone being worked on (without it the module could not be read
        /// from the log at all), the values are given back, and a retry is armed when the reason can change on its own.
        static void Refuse(string why, bool retry)
        {
            if (_why.Count < MaxWhy && _why.Add(why))
                Log("portées minimales laissées telles quelles : " + why + (retry ? " ; nouvel essai dans 2 s" : ""));
            if (retry) { _pending = true; _nextTry = Environment.TickCount64 + RetryMs; }
            else Rest();
            Forget(why);
        }

        /// Nothing more to try for now: the retry is dropped and the zone controller released.
        static void Rest()
        {
            _pending = false;
            _zone = null;
            _zoneLive = false;
            _zoneUid = null;
            _what = null;
            _tries = 0;
            _wait = 0;
        }

        static void Pass(PlayZoneCtl pz, string what)
        {
            if (Mod.Actif == null || !Mod.Actif.Value) { Refuse(WhyMod, false); return; }
            // a campaign mission really being played is the only place Frame() is called: a retry armed anywhere else (prologue without
            // the mod, skirmish, multiplayer) could never run there and would only survive the battle. Inside one, the zone can arrive
            // before the mission id is set, which is exactly what the retry is for.
            bool live = Campaign.InCampaign && !Campaign.MissionInerte;
            if (!live || Campaign.MissionUid == null) { Refuse(WhyMission, live); return; }
            if (!Realism.RealModeOn) { Refuse(WhyReal, false); return; }
            var db = DataBaseService._instance;
            // the realism is applied from the 2 s slow tick: the first zone of a mission can arrive before it, hence the retry
            if (db == null || db.RawAccess == null || !Realism.IsAppliedTo(db)) { Refuse(WhyDb, true); return; }
            // the lot the ceiling works on: not accepted (refusé, coupé, suspendu, retiré) means there is no realistic minimum to lower
            if (!RealStats.LotAccepte(RealStats.LotMinimales)) { Refuse(WhyLot, true); return; }

            // the zone is read before the row list is built: a zone not readable yet must not make every retry rebuild (and log) the list
            if (!Zone(pz, out float sx, out float sz, out float mapDiag)) { Refuse(WhyZone, true); return; }
            float diag = Diag(sx, sz);
            // the whole map, within a few per cent: no ceiling at all, the realistic minimum ranges of the lot are exactly what must be
            // in force there. The ceiling only ever bites on a zone the mission really restricted.
            bool whole = mapDiag > 0f && diag >= mapDiag * WholeShare;
            float ceiling = whole ? NoCeiling : (float)Math.Round(diag * Share);
            if (mapDiag <= 0f && !_mapWarned)
            {
                _mapWarned = true;
                Log("taille de la carte entière illisible : le plafond est calculé sur la seule zone jouable");
            }

            if (!Table(db)) { Rest(); return; }                                        // Table said why, once: nothing to retry

            // journal entries gone while the state says otherwise (the realism was applied again on the same database): the ceiling is
            // no longer on the rows, so it is written again instead of being believed
            if (Realism.LotWrites(LotKey) != _writes) _ceiling = float.NaN;

            if (!float.IsNaN(_ceiling) && whole == _whole && Math.Abs(ceiling - _ceiling) < 1f)
            {
                Rest(); _why.Clear();                                                  // same zone set again: nothing to write, nothing to say
                return;
            }

            int lowered = 0;
            var examples = new StringBuilder();
            for (int i = 0; i < _rows.Length; i++)
            {
                var r = _rows[i];
                float cur = Read(r.R);
                if (cur <= 0f) { r.Now = 0f; continue; }                                 // row without a minimum range: never given one here
                float want = r.Real <= ceiling ? r.Real : ceiling;                       // only ever lower: never above the realistic value
                if (Math.Abs(cur - want) >= Epsilon) Write(r.R, want);
                r.Now = want;
                if (want >= r.Real - Epsilon) continue;
                lowered++;
                if (lowered <= MaxExamples)
                    examples.Append(examples.Length > 0 ? ", " : "").Append(r.Name).Append(' ')
                            .Append(r.Real.ToString("0", Inv)).Append(" -> ").Append(want.ToString("0", Inv));
            }

            // the per-unit copies are aligned on the table rows in both directions: a copy the game cloned while the ceiling was lower
            // would otherwise keep that lower value once the zone opens up again
            int copies = Copies(db, false);
            // the fire assistants cache the ranges of every unit for the whole battle: while the ceiling is on they would keep refusing
            // targets under the realistic minimum, and once the real values are back they would keep ordering shots the engine refuses.
            // Guarded on its own and swallowed: another module's cache must never take this one down, nor spend its error budget.
            try { Assistants.OublierPortees(); }
            catch (Exception e)
            {
                if (!_assistWarned)
                {
                    _assistWarned = true;
                    Mod.Log.Warning(Tag + " portées en mémoire des assistants de tir non vidées : " + e.GetBaseException().Message);
                }
            }
            _ceiling = ceiling;
            _whole = whole;
            _writes = Realism.LotWrites(LotKey);
            Rest(); _why.Clear();                                                      // the pass went through: nothing waiting any more

            // the share of the map is printed on purpose: it is the number that says whether the zone was taken for a restricted one
            string share = mapDiag > 0f ? $", {(diag / mapDiag * 100f).ToString("0", Inv)} % de la carte" : ", carte de taille inconnue";
            string zone = whole
                ? $"zone jouable : la carte entière, {sx.ToString("0", Inv)} x {sz.ToString("0", Inv)} m ({what}) : aucun plafond, les portées minimales réelles s'appliquent"
                : $"zone jouable {sx.ToString("0", Inv)} x {sz.ToString("0", Inv)} m (diagonale {diag.ToString("0", Inv)} m{share}, {what}) : " +
                  $"plafond des portées minimales {ceiling.ToString("0", Inv)} m";
            if (lowered > 0)
                Log($"{zone} ; {lowered} munition(s) abaissée(s) sur {_rows.Length} ({examples})" +
                    (copies > 0 ? $" ; {copies} copie(s) d'unité alignée(s)" : ""));
            else
                Log($"{zone} ; aucune portée minimale abaissée" + (whole ? "" : ", la zone est assez grande pour les vraies valeurs") +
                    (copies > 0 ? $" ; {copies} copie(s) d'unité remise(s) aux vraies valeurs" : ""));
        }

        /// Size of the zone the mission is playing in, and the diagonal of the WHOLE MAP to compare it with (0 = the map was never
        /// readable). Falls back to the whole map when the play zone is not set yet, which is the whole map by definition.
        static bool Zone(PlayZoneCtl pz, out float sx, out float sz, out float mapDiag)
        {
            sx = sz = 0f;
            var fb = pz.FullMapBounds;
            var fs = fb.size;
            bool mapOk = fs.x >= MinSide && fs.z >= MinSide;
            if (mapOk) _mapDiag = Diag(fs.x, fs.z);              // kept: the map does not change during a mission, the play zone does
            mapDiag = _mapDiag;
            var b = pz.PlayZoneBounds;
            var s = b.size;
            if (s.x < MinSide || s.z < MinSide)
            {
                if (!mapOk) return false;                        // neither the zone nor the map readable: nothing is touched
                sx = fs.x;
                sz = fs.z;
                return true;                                     // no zone set: the whole map is being played
            }
            sx = s.x;
            sz = s.z;
            return true;
        }

        static float Diag(float x, float z) => (float)Math.Sqrt((double)x * x + (double)z * z);

        // ------------------------------------------------------------ the rows of the lot
        /// Builds the row list for this database, once per database. False when nothing can be done.
        static bool Table(DataBaseService db)
        {
            var src = db.RawAccess;
            if (_rows != null && _src != null && _src.Pointer == src.Pointer) return true;
            // held reference, never a bare address: a freed database whose address is reused again would otherwise be refused for good
            try { if (_noRowsSrc != null && _noRowsSrc.Pointer == src.Pointer) return false; } catch { _noRowsSrc = null; }

            Forget("base de données remplacée");
            if (_pMin == null)
            {
                _pMin = Props.Get(typeof(Ammunitions), "MinimalRange");
                if (_pMin == null || !_pMin.CanWrite || _pMin.PropertyType != typeof(float))
                {
                    if (!_propWarned) { _propWarned = true; Mod.Log.Warning(Tag + " portée minimale impossible dans cette version du jeu : plafond de carte non appliqué"); }
                    _off = true;
                    return false;
                }
            }

            var ids = new List<int>();
            LotIds(ids);
            if (ids.Count == 0)
            {
                _noRowsSrc = src;
                if (!_rowsLogged) { _rowsLogged = true; Log("aucune ligne " + RealStats.LotMinimales + " lue dans les données du mod : plafond de carte sans objet"); }
                return false;
            }

            var rows = new List<Row>(ids.Count);
            var dropped = new List<string>();
            _byId.Clear();
            foreach (int id in ids)
            {
                Ammunitions a = null;
                try { if (!src.Ammunitions.TryGetById(id, out a)) a = null; } catch { a = null; }
                if (a == null) continue;
                if (_byId.ContainsKey(id)) continue;
                string name;
                try { name = a.Name ?? id.ToString(Inv); } catch { name = id.ToString(Inv); }
                if (!IsIndirect(a))
                {
                    if (dropped.Count < 8) dropped.Add(name);
                    continue;
                }
                float real = Read(a);
                if (real <= 0f) continue;                                               // no minimum range to lower
                var r = new Row { R = a, Id = id, Name = name, Real = real };
                rows.Add(r);
                _byId[id] = r;
            }
            if (rows.Count == 0)
            {
                _noRowsSrc = src;
                _byId.Clear();
                if (!_rowsLogged) { _rowsLogged = true; Log("aucune munition de tir indirect retenue dans " + RealStats.LotMinimales + " : plafond de carte sans objet"); }
                return false;
            }
            _rows = rows.ToArray();
            _src = src;
            _noRowsSrc = null;
            _ceiling = float.NaN;
            _writes = 0;
            _rowsLogged = true;
            Log($"{_rows.Length} munition(s) du lot {RealStats.LotMinimales} retenues (artillerie, mortiers, lance-roquettes, missiles balistiques)" +
                (dropped.Count > 0 ? $" ; {dropped.Count} écartée(s), leur portée minimale est une distance de guidage et non une zone morte : {string.Join(", ", dropped)}" : ""));
            return true;
        }

        /// True when this row really is an indirect-fire round: one of the four indirect trajectories and no air-defence target bit.
        static bool IsIndirect(Ammunitions a)
        {
            try
            {
                int traj = (int)a.TrajectoryType;
                bool ok = false;
                for (int i = 0; i < Indirect.Length; i++) if (Indirect[i] == traj) { ok = true; break; }
                if (!ok) return false;
                return ((long)a.TargetType & AirDefenceBits) == 0;
            }
            catch { return false; }
        }

        /// Ammunition ids carrying the lot's option key, read from the same embedded file the lot itself is written from.
        static void LotIds(List<int> ids)
        {
            using var s = typeof(PorteeMiniCarte).Assembly.GetManifestResourceStream("reel.Munitions.csv");
            if (s == null) return;
            using var r = new StreamReader(s, Encoding.UTF8);
            int iOpt = -1, iId = -1;
            bool header = false;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                var cells = line.Split(';');
                if (!header)
                {
                    header = true;
                    for (int i = 0; i < cells.Length; i++)
                    {
                        var h = cells[i].Trim();
                        if (h.Equals("Option", StringComparison.OrdinalIgnoreCase)) iOpt = i;
                        else if (h.Equals("Id", StringComparison.OrdinalIgnoreCase)) iId = i;
                    }
                    if (iOpt < 0 || iId < 0) return;
                    continue;
                }
                if (iOpt >= cells.Length || iId >= cells.Length) continue;
                if (!cells[iOpt].Trim().Equals(RealStats.LotMinimales, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(cells[iId].Trim(), out int id)) ids.Add(id);
            }
        }

        // ------------------------------------------------------------ per-unit copies
        /// Aligns every ammunition copy of the loaded units on the value its table row holds (Row.Now). Returns the number of copies
        /// written. giveBack: plain writes instead of journaled ones, the give-back path of Forget.
        static int Copies(DataBaseService db, bool giveBack)
        {
            if (_rows == null || _src == null || db == null) return 0;
            var loader = db.UnitsLoader;
            if (loader == null) return 0;
            var loaded = loader._loadedUnits;
            if (loaded == null) return 0;
            // the loader must belong to the database the rows were taken from, or its copies are none of our business
            try { if (loader._source == null || loader._source.Pointer != _src.Pointer) return 0; }
            catch { return 0; }
            _giveBack = giveBack;
            _seen.Clear();
            int written = 0;
            try { foreach (var kv in loaded) written += Unit(kv.Value); }
            finally { _seen.Clear(); _giveBack = false; }
            return written;
        }

        static int Unit(Units u)
        {
            if (u == null || !_seen.Add(u.Pointer)) return 0;
            int n = 0;
            var turrets = u.Turrets;
            if (turrets != null) for (int i = 0; i < turrets.Count; i++) n += Turret(turrets[i]);
            var squad = u.SquadMembers;
            if (squad != null)
                for (int i = 0; i < squad.Count; i++)
                {
                    var s = squad[i];
                    if (s == null) continue;
                    n += Weapon(s.PrimaryWeapon);
                    n += Weapon(s.SpecialWeapon);
                }
            var mods = u.Modifications;
            if (mods != null)
                for (int i = 0; i < mods.Count; i++)
                {
                    var m = mods[i];
                    var opts = m == null ? null : m.Options;
                    if (opts != null) for (int j = 0; j < opts.Count; j++) n += Option(opts[j]);
                }
            var cur = u.CurrentOptions;
            if (cur != null) for (int i = 0; i < cur.Count; i++) n += Option(cur[i]);
            n += Unit(u.BaseUnit);
            n += Unit(u.ReplaceUnit);
            return n;
        }

        static int Option(Options o)
        {
            if (o == null || !_seen.Add(o.Pointer)) return 0;
            int n = 0;
            n += Turret(o.Turret0); n += Turret(o.Turret1); n += Turret(o.Turret2); n += Turret(o.Turret3);
            n += Turret(o.Turret4); n += Turret(o.Turret5); n += Turret(o.Turret6); n += Turret(o.Turret7);
            n += Turret(o.Turret8); n += Turret(o.Turret9); n += Turret(o.Turret10); n += Turret(o.Turret11);
            n += Turret(o.Turret12); n += Turret(o.Turret13); n += Turret(o.Turret14); n += Turret(o.Turret15);
            n += Turret(o.Turret16); n += Turret(o.Turret17); n += Turret(o.Turret18); n += Turret(o.Turret19);
            n += Turret(o.Turret20);
            return n;
        }

        static int Turret(Turrets t)
        {
            if (t == null || !_seen.Add(t.Pointer)) return 0;
            int n = 0;
            var weapons = t.Weapons;
            if (weapons != null) for (int i = 0; i < weapons.Count; i++) n += Weapon(weapons[i]);
            var children = t.ChildTurrets;
            if (children != null) for (int i = 0; i < children.Count; i++) n += Turret(children[i]);
            return n;
        }

        static int Weapon(Weapons w)
        {
            if (w == null || !_seen.Add(w.Pointer)) return 0;
            var ammo = w.Ammunitions;
            if (ammo == null) return 0;
            int n = 0;
            for (int i = 0; i < ammo.Count; i++)
            {
                var a = ammo[i];
                if (a == null || !_seen.Add(a.Pointer)) continue;
                // unknown row, row the pass left alone, or the table row itself
                if (!_byId.TryGetValue(a.Id, out var r) || r.Now <= 0f || a.Pointer == r.R.Pointer) continue;
                float cur = Read(a);
                if (cur <= 0f || Math.Abs(cur - r.Now) < Epsilon) continue;                      // never gives a minimum range to a copy that has none
                Put(a, r.Now);
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------ read, write, give back
        static float Read(Ammunitions a)
        {
            try { return a.MinimalRange; } catch { return 0f; }
        }

        /// Journaled write under this module's own lot: put back when the mod stops, and layered over the lot that wrote the
        /// realistic value, so the real-stats pipeline can take its own lot out without losing the value it found.
        static void Write(Ammunitions a, float value)
        {
            using (Realism.Lot(LotKey)) Realism.SetValueForOverride(a, _pMin, value);
        }

        /// Write of one copy: journaled during a pass, plain on the give-back path. Plain there on purpose, and it is the only place
        /// this module writes outside the journal: journaling a give-back would record the CAPPED value as the value to come back to
        /// and pin the copy at it for the rest of the session. It is the same plain write UnitCopies makes on its own restore walk
        /// (Ctx.Journal == false), and the mod's own restore realigns every copy on the table rows afterwards anyway.
        static void Put(Ammunitions a, float value)
        {
            if (!_giveBack) { Write(a, value); return; }
            try { _pMin.SetValue(a, value); } catch { }
        }

        /// Gives back every value this module wrote and forgets the state. The table rows come back through the journal; the per-unit
        /// copies are then aligned on them, because a copy the game cloned while the ceiling was on was born capped and holds no
        /// journal entry of the game's value: RestoreLot puts THAT copy back to the capped value (its entry's original value is the
        /// capped one) and the artillery already on the field would keep firing with a wrong minimum range for the rest of the
        /// session. Walked after RestoreLot, never before: before, the copies would be realigned on the still-capped table rows.
        static void Forget(string why)
        {
            // the map size is forgotten with the rest, and BEFORE the early return below: the state can be empty (nothing written yet)
            // while a map read on a PREVIOUS mission is still held, and deciding the whole-map test with it would take a genuinely
            // restricted phase zone of a small map for the whole map and give back the full 3000 m where the ceiling is needed most.
            // Nothing is lost inside a mission: Zone() reads FullMapBounds again on every pass and takes it back as soon as it is readable.
            _mapDiag = 0f;
            if (_rows == null && float.IsNaN(_ceiling) && _writes == 0) return;
            bool held = false, failed = false;
            try
            {
                held = Realism.LotWrites(LotKey) > 0;
                if (held) Realism.RestoreLot(LotKey, why);
            }
            catch (Exception e) { failed = true; Mod.Log.Warning(Tag + " portées minimales réelles non rendues : " + e.GetBaseException().Message); }
            if (held)
            {
                int copies = 0;
                if (_rows != null)
                {
                    try
                    {
                        for (int i = 0; i < _rows.Length; i++) _rows[i].Now = Read(_rows[i].R);    // the value the table row is back to
                        copies = Copies(DataBaseService._instance, true);
                    }
                    catch (Exception e) { Mod.Log.Warning(Tag + " copies d'unité non remises aux portées minimales réelles : " + e.GetBaseException().Message); }
                }
                Log((failed ? "portées minimales rendues en partie (" : "portées minimales réelles rendues (") + why + ")" +
                    (copies > 0 ? " : " + copies.ToString(Inv) + " copie(s) d'unité remise(s)" : ""));
            }
            _rows = null;
            _src = null;
            _byId.Clear();
            _seen.Clear();
            _ceiling = float.NaN;
            _whole = false;
            _writes = 0;
        }

        static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3) Mod.Log.Warning($"{Tag} erreur ({_errors}/{MaxErrors}) : {e.GetBaseException().Message}");
            if (_off || _errors < MaxErrors) return;
            _off = true;
            Mod.Log.Warning($"{Tag} {MaxErrors} erreurs : garde-fou, plafond des portées minimales coupé jusqu'au redémarrage du jeu (portées minimales réelles rendues)");
            try { Forget("garde-fou du plafond de carte"); } catch { }
        }
    }

    /// The play zone a mission script sets for the phase being played. Postfix without any parameter of the patched method: only the
    /// controller's own PlayZoneBounds property is read, so nothing of the method's signature is marshalled here. Spawns.cs already
    /// takes the same two methods the same way.
    [HarmonyPatch(typeof(PlayZoneCtl), nameof(PlayZoneCtl.SetPlayZone))]
    static class Patch_PorteeMiniZone
    {
        static bool Prepare() => PorteeMiniCarte.HooksWanted;
        static void Postfix(PlayZoneCtl __instance) => PorteeMiniCarte.OnZone(__instance, "nouvelle zone");
    }

    [HarmonyPatch(typeof(PlayZoneCtl), nameof(PlayZoneCtl.ResetToFull))]
    static class Patch_PorteeMiniZoneEntiere
    {
        static bool Prepare() => PorteeMiniCarte.HooksWanted;
        static void Postfix(PlayZoneCtl __instance) => PorteeMiniCarte.OnZone(__instance, "carte entière");
    }
}
