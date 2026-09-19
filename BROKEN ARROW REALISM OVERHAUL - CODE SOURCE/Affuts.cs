// RealismOverhaul - per-unit data written on the game's own unit copies (values of 2026-09-17), both sides alike.
//  The game clones weapons, ammunition, sensors and mobility into every loaded unit (see CopiesUnites.cs). A table row is shared by many
//  units with different mounts or roles, so these rules are written on the copies owned by the concerned units only:
//   1) Manual aim (reel/Affuts.csv + reel/AffutsPortees.csv): per (unit, weapon[, turret]) mount class OEIL (by eye on an open mount:
//      ground and helicopter range 600 m for 7.62/12.7/14.5 mm and 30/40 mm grenade launchers) or LUNETTE (simple scope or stable
//      tripod: ground 7.62 800 / 12.7 1500 / 14.5 2000, helicopter 800 / 1000 / 1200, grenade launchers unchanged). Camera mounts and
//      doubtful vehicles are not listed (unchanged). A range is only ever shortened: GroundRange = min(copy, cap), LowAltRange likewise.
//   2) Sensors per unit (reel/CapteursParUnite.csv): helicopter OpticsGround (Apache/Mi-28NM 8000 ... heavy transports 1800) and the
//      Spetsnaz GRU AA team air optics 5000. A sensor object shared with other units (or the table row itself) is never written: the
//      concerned unit gets its own clone in its list/option (journaled, the original object is put back at restore).
//   3) Heavy-weapon teams (reel/MobiliteEquipes.csv): road, cross-country and water speed x0.8 of the unit's mobility row, only on
//      mobility copies owned by those teams (a shared copy is logged and left alone, never cloned, never another row repurposed).
//   4) Helicopter flares (reel/LeurresParUnite.csv): DecoyQuantity (salvos), DecoySupplyCost (1000 / stock) and DecoyCooldown (gap between
//      two salvos: 1 s for every listed helicopter unless the file gives another value - the player's choice of 2026-09-18, a helicopter
//      must be able to answer a missile every second; well under the 2.9 s burn, so a flare is always burning during an attack, and the
//      stock burns about 2.5 times faster than with the old 2.5 s, which is accepted and never made up for by a bigger stock) on the decoy
//      ability copies owned by each listed helicopter. Planes keep their own gap (3 s, B-52 4 s). One ability row serves helicopters of
//      different classes (row 86: Mi-35M, MH-47G, Mi-8AMTSh, AH-64E), so the rows keep a common base value and each copy gets its unit's
//      value. A table row or a copy shared by units wanting different values is never written (it keeps the row value). Log: [LEURRES].
//  Stage 0 [VISEE] measurement inside the CopiesUnites walk: every ammunition copy pointer -> (unit, turret, weapon, ammo) keys, distinct
//  copies per ammo id, copies reached from several keys or units, copies that are the table row. Stage 1 in the same walk, guarded per
//  copy: written right after the Id sync in every walk path (first pass, FullUnitClone, LoadUnit, GetLoadedTurret, arsenal card) so the
//  sync never undoes it; skipped when the copy is the table row or is reached by owners wanting different values; a copy found shared
//  later gets its previous values back. Every write is journaled (Realism), so un-applying the real stats restores everything.
//  No engine hook here. Crash guard (SessionsInterrompues/VersionSecurite): two unfinished battles in a row -> measurement only for one
//  battle. Logs: [VISEE], [CAPTEURS], [EQUIPES].
//  For the anti-air module: TryGetCap(unit, ammo, vsHeli) reads an immutable (unit, ammo) cap table built from the CSV and the
//  database loadouts (pairs whose ammo is fired by mounts of different classes, or by a turret-specific mount, return false), and
//  CopiesProuvees tells whether stage 0 found per-unit unshared copies for the listed mounts. PairCapState gives PrecisionHeli one
//  helicopter range per (unit, ammo) pair for its helicopter-only copies (-1 when the pair is fired from mounts of different classes);
//  those copies (Id 20000+source) are never tracked here, and TryGetCap answers for them with their source row.
//  For the automatic flare salvos: TryDecoyStock(unit) gives the stock and the gap the flare file wrote for a listed helicopter, so
//  LeurresAuto uses the mod's own numbers instead of inventing any.
//  PrecisionHeli has no call site of its own in Mod.cs: its preferences, frame, battle end and quit go through this module.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using DbSource = Il2CppBrokenArrow.DataBase.DataBaseSourceData;
using AmmoRow = Il2CppBrokenArrow.DataBase.Models.Ammunitions;
using SensorRow = Il2CppBrokenArrow.DataBase.Models.Sensors;
using AbilityRow = Il2CppBrokenArrow.DataBase.Models.Abilities;
using MobilityRow = Il2CppBrokenArrow.DataBase.Models.Mobility;
using OptionRow = Il2CppBrokenArrow.DataBase.Models.Options;
using UnitRow = Il2CppBrokenArrow.DataBase.Models.Units;
using WeaponRow = Il2CppBrokenArrow.DataBase.Models.Weapons;
using SensorList = Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.DataBase.Models.Sensors>;

namespace RealismOverhaul
{
    static class Affuts
    {
        const string GuardVersion = "1.1";
        internal const byte KindAmmo = 0, KindSensor = 1, KindMobility = 2, KindDecoy = 3;
        const int Kinds = 4;
        const byte ClassNone = 0, ClassEye = 1, ClassScope = 2;
        const double MapCap = 9000;                                   // same rule as RealStats: whole-map values are never scaled
        internal const float HeliDecoyCooldown = 1f;                  // seconds between two flare salvos of a listed helicopter (default)
        const int MaxErrors = 50, MaxExamples = 6, MaxListed = 12;
        static readonly string[] ClassNames = { "CAMERA/non listé", "OEIL", "LUNETTE" };
        static readonly string[] KindLogs = { "[VISEE] ", "[CAPTEURS] ", "[EQUIPES] ", "[LEURRES] " };
        static readonly string[][] Fields =
        {
            new[] { "GroundRange", "LowAltRange", null },
            new[] { "OpticsGround", "OpticsLowAltitude", "OpticsHighAltitude" },
            new[] { "MaxSpeedRoad", "MaxCrossCountrySpeed", "MaxSpeedWater" },
            new[] { "DecoyQuantity", "DecoySupplyCost", "DecoyCooldown" },      // DecoyQuantity is an int property
        };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// A wanted value: V (NaN = unchanged); X = multiplier of the table row value (mobility), otherwise absolute (ammunition: upper cap).
        internal struct Want
        {
            internal float V;
            internal bool X;
            internal bool Set => !float.IsNaN(V);
            internal static readonly Want None = new Want { V = float.NaN };
            internal bool Same(Want o) => Set ? o.Set && V == o.V && X == o.X : !o.Set;
            public override string ToString() => !Set ? "-" : X ? "x" + V.ToString("0.###", Inv) : V.ToString("0.#", Inv);
        }

        /// One game object (ammunition, sensor or mobility copy) and every owner key that reaches it.
        internal sealed class Slot
        {
            internal byte Kind;
            internal int RowId;                        // Id when first seen: another Id at the same address is a new object
            internal bool Table;                       // the database table row itself: never written
            internal int Owners, FirstUnit;
            internal long FirstKey;
            internal bool MultiKey, MultiUnit, Mixed, Wants, Written, Counted;
            internal byte Class;                       // ammunition: listed class of an owner (reports)
            internal readonly Want[] W = { Want.None, Want.None, Want.None };
            internal readonly bool[] HasP = new bool[3];
            internal readonly float[] P = new float[3], V = new float[3];   // values before the module's write, values written
            internal object Owner;                     // first listed owner (Mount, SensorWant, MobilityWant)
        }

        /// The owner map a walk records into: the plan's map (first pass over the loaded units, kept alive by the loader cache) or a map
        /// local to one copy made later (its objects are not kept, so their addresses are never trusted beyond this walk).
        internal sealed class Walk
        {
            internal Dictionary<IntPtr, Slot> Local;   // null = first pass (plan map)
            internal readonly int[] Written = new int[Kinds], Skipped = new int[Kinds], Unwritten = new int[Kinds];
            internal int Cloned;
        }

        sealed class Mount { internal int Unit, Weapon, Turret; internal byte Class; internal string Label; internal bool Seen, UnknownAmmo; internal int Written, Skipped; }
        sealed class SensorWant { internal int Unit, Sensor; internal readonly Want[] W = new Want[3]; internal string Label; internal bool Seen; internal int Written, Cloned, Skipped; }
        sealed class MobilityWant { internal int Unit; internal readonly Want[] W = new Want[3]; internal string Label; internal bool Seen; internal int Written, Skipped; }
        sealed class DecoyWant
        {
            internal int Unit, Quantity;
            internal float Cooldown;
            internal readonly Want[] W = { Want.None, Want.None, Want.None };
            internal string Label, Class;
            internal bool Seen;
            internal int Written, Skipped;
            internal readonly HashSet<IntPtr> Objects = new();                   // first pass only (report)
            internal readonly SortedSet<int> Rows = new();
        }

        sealed class State
        {
            internal DbSource Src;                     // held reference: table rows stay alive, their addresses cannot be reused
            internal float Scale = 1f;
            internal bool Refused;
            internal readonly HashSet<IntPtr> TableRows = new();
            internal readonly Dictionary<int, MobilityRow> MobilityRows = new();
            internal readonly Dictionary<(int unit, int weapon), List<Mount>> Mounts = new();
            internal readonly List<Mount> MountList = new();
            internal readonly Dictionary<(byte cls, int ammo), (float g, float h)> Caps = new();
            internal readonly Dictionary<(int unit, int sensor), SensorWant> Sensors = new();
            internal readonly Dictionary<int, MobilityWant> Mobility = new();
            internal readonly Dictionary<int, DecoyWant> Decoys = new();
            internal int DecoyUnitsAbsent;
            internal readonly Dictionary<IntPtr, Slot> Map = new();
            // (list or option, slot) already given a clone in this plan: the undo closures hold those objects, so an address is never reused
            internal readonly HashSet<(IntPtr owner, int slot)> ClonedAt = new();
            internal readonly int[] WrittenByClass = new int[3], SkipMixedByClass = new int[3], SkipTableByClass = new int[3];
            internal readonly int[] Examples = new int[Kinds];
            internal int OptionsOff, LotsOff, BadRows, CapPairs, CapAmbiguous, MinRangeKept;
            internal volatile int WrittenTotal;                  // read by Frame on the main thread (the map itself is never enumerated there)
        }

        static MelonPreferences_Entry<bool> _enabled;
        static MelonPreferences_Entry<int> _unclean;
        static MelonPreferences_Entry<string> _guardVersion;
        static volatile State _state;
        static volatile Dictionary<long, (float g, float h)> _capTable;   // (unit << 32 | ammo) -> caps in game metres (NaN = none)
        static volatile HashSet<long> _capAmbiguous;                      // (unit << 32 | ammo) fired from mounts of different classes
        // flare stock and gap of each listed helicopter, read by LeurresAuto; filled by LoadDecoys and kept after EndPlan (data, not a plan)
        static readonly Dictionary<int, (int Qty, float Gap)> _decoyByUnit = new();
        static volatile bool _copiesProven;
        static bool _dead, _sessionArmed, _battleLogged, _refusedLogged;
        static int _errors, _badLogs, _unwriteLogs, _precisionFrameErrors;
        static float _next;
        static int _pendingNotify = -1;                  // TxtKey of a notice to show on the next main-thread frame (-1 = none)
        static long _nextHookLog;
        static readonly int[] _hookWritten = new int[Kinds], _hookSkipped = new int[Kinds], _hookUnwritten = new int[Kinds];
        static int _hookCloned, _hookWalks, _hookOffMain;
        static volatile int _mainThread = -1;
        static string _lastHookLine;

        static void Log(string s) => Mod.Log.Msg("[VISEE] " + s);
        static void LogK(byte kind, string s) => Mod.Log.Msg(KindLogs[kind] + s);

        // ------------------------------------------------------------ module interface

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Affuts");
            _enabled = c.CreateEntry("DonneesParUnite", true, description: Build.Desc("Portées selon l'affût (tir à l'oeil ou à la lunette), optiques des hélicoptères et des équipes sol-air, équipes d'armes lourdes plus lentes, stock de leurres de chaque hélicoptère : écrit sur les copies propres à chaque unité (toujours actif)"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            _mainThread = Environment.CurrentManagedThreadId;
            try { PrecisionHeli.CreatePrefs(); }
            catch (Exception e) { Mod.Log.Warning("[PRECISION] anti-hélico : réglages non créés (" + e.Message + ")"); }
        }

        /// True when stage 0 found per-unit unshared ammunition copies for the listed mounts (recomputed at every application) and the
        /// module writes them (not in measurement-only mode after two unfinished battles, not switched off after errors).
        internal static bool CopiesProuvees
        {
            get { var st = _state; return _copiesProven && !_dead && st != null && !st.Refused; }
        }

        /// Manual-aim cap of this unit type for this ammunition, in game metres. False when the pair is not listed, when the unit fires
        /// this ammunition from mounts of different classes (or from a turret-specific mount), or when the class leaves it unchanged.
        /// Lock-free: reads an immutable table replaced as a whole.
        internal static bool TryGetCap(int unitId, int ammoId, bool vsHeli, out float cap)
        {
            cap = 0f;
            var t = _capTable;
            ammoId = PrecisionHeli.SourceOf(ammoId);                     // a helicopter-only copy has the caps of its source row
            if (t == null || !t.TryGetValue(((long)unitId << 32) | (uint)ammoId, out var c)) return false;
            float v = vsHeli ? c.h : c.g;
            if (float.IsNaN(v)) return false;
            cap = v;
            return true;
        }

        /// Helicopter manual-aim state of a (unit, ammo) pair for PrecisionHeli: 1 = one cap for every mount of the pair (heliCap set),
        /// 0 = no listed mount or no helicopter cap, -1 = fired from mounts of different classes or from a turret-specific mount (the unit
        /// holds one ammunition box per ammunition id, so the copies of that pair could not share one range). Lock-free.
        internal static int PairCapState(int unitId, int ammoId, out float heliCap)
        {
            heliCap = 0f;
            long key = ((long)unitId << 32) | (uint)ammoId;
            var amb = _capAmbiguous;
            if (amb != null && amb.Contains(key)) return -1;
            var t = _capTable;
            if (t == null || !t.TryGetValue(key, out var c) || float.IsNaN(c.h)) return 0;
            heliCap = c.h;
            return 1;
        }

        static int _wait;

        /// Every frame inside a campaign mission: crash guard and the once-per-battle summary (1 s throttle).
        internal static void Frame()
        {
            if (_enabled == null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            _next = now + 1f;
            _mainThread = Environment.CurrentManagedThreadId;
            try { PrecisionHeli.Frame(now); }
            catch (Exception e) { if (_precisionFrameErrors++ < 3) Mod.Log.Warning("[PRECISION] anti-hélico : erreur (" + e.GetBaseException().Message + ")"); }
            { int n = System.Threading.Interlocked.Exchange(ref _pendingNotify, -1); if (n >= 0) Mod.Notify((TxtKey)n); }
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            var st = _state;
            if (st == null || _sessionArmed) return;
            int written = st.WrittenTotal + _hookCloned;
            if (written == 0 && !st.Refused) return;
            _sessionArmed = true;
            if (!st.Refused) { _unclean.Value = _unclean.Value + 1; MelonPreferences.Save(); }
            if (!_battleLogged)
            {
                _battleLogged = true;
                Log($"début de bataille : {(st.Refused ? "mesure seulement (sécurité)" : "valeurs par unité en place")} ; copies faites par le jeu depuis l'application : {_hookWalks} contrôle(s) (hors fil principal {_hookOffMain}), " +
                    $"munitions raccourcies {_hookWritten[KindAmmo]}, capteurs écrits {_hookWritten[KindSensor]} (copies propres {_hookCloned}), mobilités ralenties {_hookWritten[KindMobility]}, " +
                    $"leurres d'hélicoptère écrits {_hookWritten[KindDecoy]}, sautées (partagées) {Sum(_hookSkipped)}, remises {Sum(_hookUnwritten)} ; copies par unité prouvées={_copiesProven}");
            }
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _battleLogged = false;
            try { PrecisionHeli.ResetSession(); } catch (Exception e) { Mod.Log.Warning("[PRECISION] anti-hélico : erreur (" + e.Message + ")"); }
        }

        internal static void OnBattleEnd()
        {
            EndBattle("fin de bataille");
            try { PrecisionHeli.OnBattleEnd(); } catch (Exception e) { Mod.Log.Warning("[PRECISION] anti-hélico : erreur (" + e.Message + ")"); }
        }

        internal static void OnQuit()
        {
            EndBattle("fermeture du jeu");
            try { PrecisionHeli.OnQuit(); } catch { }
        }

        static void EndBattle(string why)
        {
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean != null && _unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
            Log($"bataille terminée normalement ({why}) : sécurité remise à zéro");
        }

        // ------------------------------------------------------------ plan (called by CopiesUnites under its lock)

        /// Loads the tables for a new application of the real stats. Null when the module is off or failed (the Id sync still runs).
        internal static Walk BeginPlan(DbSource src)
        {
            _state = null; _capTable = null; _capAmbiguous = null; _copiesProven = false;
            Array.Clear(_hookWritten, 0, Kinds); Array.Clear(_hookSkipped, 0, Kinds); Array.Clear(_hookUnwritten, 0, Kinds);
            _hookCloned = 0; _hookWalks = 0; _hookOffMain = 0; _lastHookLine = null;
            if (_dead || src == null) return null;
            if (_enabled != null && !_enabled.Value) { Log("données par unité coupées dans les réglages"); return null; }
            try
            {
                var st = new State { Src = src };
                float sc = Realism.EchellePortees != null ? Realism.EchellePortees.Value : 1f;
                st.Scale = sc > 1f ? sc : 1f;
                st.Refused = _unclean != null && _unclean.Value >= 2;
                foreach (var r in Props.Rows(src.Ammunitions.GetAll())) if (r != null) st.TableRows.Add(r.Pointer);
                foreach (var r in Props.Rows(src.Sensors.GetAll())) if (r != null) st.TableRows.Add(r.Pointer);
                foreach (var r in Props.Rows(src.Mobility.GetAll())) if (r != null) { st.TableRows.Add(r.Pointer); st.MobilityRows[r.Id] = r; }
                foreach (var r in Props.Rows(src.Abilities.GetAll())) if (r != null) st.TableRows.Add(r.Pointer);
                var off = OptionsOff();
                LoadMounts(st, src, off);
                LoadCaps(st);
                LoadSensors(st, src, off);
                LoadMobility(st, src, off);
                try { LoadDecoys(st, src, off); }
                catch (Exception e) { st.Decoys.Clear(); _decoyByUnit.Clear(); Mod.Log.Warning("[LEURRES] stock par hélicoptère illisible, valeurs des lignes gardées : " + e.Message); }
                _capTable = BuildCapTable(st, src);
                _state = st;
                Log($"tables : {st.MountList.Count} affût(s) listé(s) (OEIL {st.MountList.Count(m => m.Class == ClassEye)}, LUNETTE {st.MountList.Count(m => m.Class == ClassScope)}), " +
                    $"{st.Caps.Count} plafond(s) munition, {st.Sensors.Count} optique(s) par unité, {st.Mobility.Count} équipe(s) lourde(s), {st.Decoys.Count} stock(s) de leurres d'hélicoptère, options coupées {st.OptionsOff}, lignes de lot non appliquées {st.LotsOff}, lignes refusées {st.BadRows}, échelle {st.Scale}" +
                    $" ; plafonds pour la DCA : {st.CapPairs} paire(s) unité+munition, {st.CapAmbiguous} ambiguë(s) refusée(s)");
                if (st.Refused)
                {
                    Mod.Log.Warning("[VISEE] valeurs par unité NON écrites (mesure seulement) : les deux dernières batailles ne se sont pas terminées normalement");
                    if (!_refusedLogged) { _refusedLogged = true; _pendingNotify = (int)TxtKey.N_AFFUTS_OFF; }
                }
                return new Walk();
            }
            catch (Exception e)
            {
                _state = null; _capTable = null; _capAmbiguous = null;
                Mod.Log.Warning("[VISEE] tables par unité illisibles, rien n'est écrit : " + e);
                return null;
            }
        }

        /// A walk over one copy made later (FullUnitClone, LoadUnit, GetLoadedTurret, arsenal card). Null when no plan is active.
        internal static Walk BeginWalk() => _state != null && !_dead ? new Walk { Local = new Dictionary<IntPtr, Slot>() } : null;

        internal static void EndWalk(Walk w, string what)
        {
            if (w == null || w.Local == null) return;
            _hookWalks++;
            if (Environment.CurrentManagedThreadId != _mainThread) _hookOffMain++;
            for (int k = 0; k < Kinds; k++) { _hookWritten[k] += w.Written[k]; _hookSkipped[k] += w.Skipped[k]; _hookUnwritten[k] += w.Unwritten[k]; }
            _hookCloned += w.Cloned;
            if (Sum(w.Written) + w.Cloned + Sum(w.Skipped) + Sum(w.Unwritten) == 0) return;
            long now = Environment.TickCount64;                               // the loader can run off the main thread: no Unity call here
            if (now < _nextHookLog) return;
            _nextHookLog = now + 60000;
            string line = $"copies faites par le jeu ({what}) depuis l'application : {_hookWalks} contrôle(s) (hors fil principal {_hookOffMain}) ; munitions raccourcies {_hookWritten[0]}, sautées {_hookSkipped[0]}, remises {_hookUnwritten[0]} ; " +
                          $"capteurs écrits {_hookWritten[1]} (copies propres {_hookCloned}), sautés {_hookSkipped[1]} ; mobilités ralenties {_hookWritten[2]}, sautées {_hookSkipped[2]} ; " +
                          $"leurres d'hélicoptère écrits {_hookWritten[KindDecoy]}, sautés {_hookSkipped[KindDecoy]}, remis {_hookUnwritten[KindDecoy]}";
            if (line == _lastHookLine) return;
            _lastHookLine = line;
            Log(line);
        }

        internal static void EndPlan()
        {
            _state = null;
            _capTable = null;
            _capAmbiguous = null;
            _copiesProven = false;
        }

        // ------------------------------------------------------------ walk callbacks (CopiesUnites, under its lock)

        /// Records an ammunition copy reached from (unit, turret, weapon). Returns its slot (null = nothing to do).
        internal static Slot NoteAmmo(Walk w, AmmoRow a, int unitId, int turretId, int weaponId)
        {
            var st = _state;
            if (st == null || w == null || a == null || _dead) return null;
            try
            {
                int id = a.Id;
                if (PrecisionHeli.IsCopy(id)) return null;                  // helicopter-only copies: their range is set by PrecisionHeli
                var slot = Find(w, st, a.Pointer, id, KindAmmo);
                var m = Resolve(st, unitId, weaponId, turretId);
                Want g = Want.None, h = Want.None;
                if (m != null)
                {
                    m.Seen = true;
                    if (st.Caps.TryGetValue((m.Class, id), out var cap))
                    {
                        if (!float.IsNaN(cap.g)) g = new Want { V = cap.g };
                        if (!float.IsNaN(cap.h)) h = new Want { V = cap.h };
                    }
                    else m.UnknownAmmo = true;
                }
                long key = ((long)unitId << 40) ^ ((long)(turretId & 0xFFFFF) << 20) ^ (uint)(weaponId & 0xFFFFF);
                Merge(slot, unitId, key, g, h, Want.None, m?.Class ?? ClassNone, m);
                return slot;
            }
            catch (Exception e) { Fail(e); return null; }
        }

        internal static Slot NoteSensor(Walk w, SensorRow s, int unitId)
        {
            var st = _state;
            if (st == null || w == null || s == null || _dead) return null;
            try
            {
                int id = s.Id;
                var slot = Find(w, st, s.Pointer, id, KindSensor);
                st.Sensors.TryGetValue((unitId, id), out var want);
                if (want != null) want.Seen = true;
                Merge(slot, unitId, unitId, want?.W[0] ?? Want.None, want?.W[1] ?? Want.None, want?.W[2] ?? Want.None, ClassNone, want);
                return slot;
            }
            catch (Exception e) { Fail(e); return null; }
        }

        internal static Slot NoteMobility(Walk w, MobilityRow m, int unitId)
        {
            var st = _state;
            if (st == null || w == null || m == null || _dead) return null;
            try
            {
                var slot = Find(w, st, m.Pointer, m.Id, KindMobility);
                st.Mobility.TryGetValue(unitId, out var want);
                if (want != null) want.Seen = true;
                Merge(slot, unitId, unitId, want?.W[0] ?? Want.None, want?.W[1] ?? Want.None, want?.W[2] ?? Want.None, ClassNone, want);
                return slot;
            }
            catch (Exception e) { Fail(e); return null; }
        }

        /// Records a decoy ability copy reached from a unit (its own abilities or one of its options). Abilities without decoys are
        /// ignored. Every owner is recorded, listed or not, so a copy shared with another unit is never written.
        internal static Slot NoteDecoy(Walk w, AbilityRow a, int unitId)
        {
            var st = _state;
            if (st == null || w == null || a == null || _dead) return null;
            try
            {
                if (!a.IsDecoy) return null;
                int id = a.Id;
                var slot = Find(w, st, a.Pointer, id, KindDecoy);
                st.Decoys.TryGetValue(unitId, out var want);
                if (want != null)
                {
                    want.Seen = true;
                    want.Rows.Add(id);
                    if (w.Local == null) want.Objects.Add(a.Pointer);
                }
                Merge(slot, unitId, unitId, want?.W[0] ?? Want.None, want?.W[1] ?? Want.None, want?.W[2] ?? Want.None, ClassNone, want);
                return slot;
            }
            catch (Exception e) { Fail(e); return null; }
        }

        /// The value the Id sync must write on this copy: the table value adjusted by this module's rule for the copy.
        internal static object Adjust(Walk w, Slot s, object obj, string prop, object want)
        {
            var st = _state;
            if (st == null || w == null || s == null || !Active(st, s)) return want;
            float f;
            bool isInt = false;
            if (want is float wf) f = wf;
            else if (want is int wi) { f = wi; isInt = true; }
            else return want;
            try
            {
                int i = Array.IndexOf(Fields[s.Kind], prop);
                if (i < 0 || !s.W[i].Set) return want;
                float target = Target(st, s, i, f, f, obj);
                if (float.IsNaN(target) || Math.Abs(target - f) < 0.01f) return want;
                if (!s.HasP[i]) { s.HasP[i] = true; s.P[i] = f; }
                s.V[i] = target;
                if (!s.Written) Example(st, s, obj, prop, f, target);
                MarkWritten(w, st, s, obj);
                return isInt ? (object)(int)Math.Round(target) : target;
            }
            catch (Exception e) { Fail(e); return want; }
        }

        /// A float or int property this module can write; reads its value as float.
        static bool NumProp(object obj, string field, out System.Reflection.PropertyInfo p, out bool isInt, out float cur)
        {
            p = null; isInt = false; cur = 0f;
            if (field == null) return false;
            p = Props.Get(obj.GetType(), field);
            if (p == null || !p.CanWrite) return false;
            if (p.PropertyType == typeof(float)) { cur = (float)p.GetValue(obj); return true; }
            if (p.PropertyType == typeof(int)) { isInt = true; cur = (int)p.GetValue(obj); return true; }
            return false;
        }

        static object Boxed(bool isInt, float v) => isInt ? (object)(int)Math.Round(v) : v;

        static int Sum(int[] a)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++) n += a[i];
            return n;
        }

        /// Writes this module's values on the copy (journaled) when the copy is owned only by owners wanting the same values.
        /// A copy found shared after a write gets its previous values back. Returns the number of values written.
        internal static int Enforce(Walk w, Slot s, Il2CppObjectBase obj)
        {
            var st = _state;
            if (st == null || w == null || s == null || obj == null || _dead) return 0;
            try
            {
                if (!s.Wants) return 0;
                if (!Active(st, s))
                {
                    int n0 = s.Written && (s.Mixed || s.Table) ? Unwrite(w, s, obj) : 0;
                    // sensors: the owner gets its own clone instead (a failed clone is counted there)
                    if (!st.Refused && !s.Counted && s.Kind != KindSensor) { s.Counted = true; CountSkip(w, st, s); }
                    return n0;
                }
                int n = 0;
                var fields = Fields[s.Kind];
                for (int i = 0; i < 3; i++)
                {
                    if (fields[i] == null || !s.W[i].Set) continue;
                    if (!NumProp(obj, fields[i], out var p, out bool isInt, out float cur)) continue;
                    float target = Target(st, s, i, cur, float.NaN, obj);
                    if (float.IsNaN(target) || Math.Abs(target - cur) < 0.01f) continue;
                    if (!s.HasP[i]) { s.HasP[i] = true; s.P[i] = cur; }
                    s.V[i] = target;
                    Realism.SetValueForOverride(obj, p, Boxed(isInt, target));
                    n++;
                    if (!s.Written) Example(st, s, obj, fields[i], cur, target);
                }
                if (n > 0) MarkWritten(w, st, s, obj);
                return n;
            }
            catch (Exception e) { Fail(e); return 0; }
        }

        /// True when this unit wants its own values on a sensor object it cannot write (table row, or shared with other owners).
        internal static bool NeedsOwnSensor(Slot s, int unitId)
        {
            var st = _state;
            return st != null && s != null && !_dead && !st.Refused && s.Kind == KindSensor && (s.Table || s.Mixed) && st.Sensors.ContainsKey((unitId, s.RowId));
        }

        /// Replaces entry i of a unit's sensor list with a clone owned by that unit (journaled). Null = left as it is.
        internal static SensorRow OwnSensorInList(Walk w, SensorList list, int i, SensorRow orig, int unitId)
        {
            try
            {
                var st = _state;
                if (st == null || !st.ClonedAt.Add((list.Pointer, i))) { CloneFailed(w, unitId, orig); return null; }   // never twice (two owners fighting over one list)
                var clone = CloneSensor(orig);
                if (clone == null) { CloneFailed(w, unitId, orig); return null; }
                int idx = i;
                var l = list;
                Realism.JournalUndo(l.Pointer, "capteur-par-unite:" + idx, () =>
                {
                    try { if (idx < l.Count && l[idx] != null && l[idx].Pointer == clone.Pointer) l[idx] = orig; } catch { }
                });
                list[i] = clone;
                Cloned(w, unitId, orig);
                return clone;
            }
            catch (Exception e) { Fail(e); CloneFailed(w, unitId, orig); return null; }
        }

        /// Replaces an option's main or extra sensor with a clone owned by the option's unit (journaled). Null = left as it is.
        internal static SensorRow OwnSensorInOption(Walk w, OptionRow o, bool main, SensorRow orig, int unitId)
        {
            try
            {
                var st = _state;
                if (st == null || !st.ClonedAt.Add((o.Pointer, main ? -1 : -2))) { CloneFailed(w, unitId, orig); return null; }
                var clone = CloneSensor(orig);
                if (clone == null) { CloneFailed(w, unitId, orig); return null; }
                var opt = o;
                Realism.JournalUndo(opt.Pointer, main ? "capteur-option:principal" : "capteur-option:extra", () =>
                {
                    try
                    {
                        var cur = main ? opt.MainSensor : opt.ExtraSensor;
                        if (cur != null && cur.Pointer == clone.Pointer) { if (main) opt.MainSensor = orig; else opt.ExtraSensor = orig; }
                    }
                    catch { }
                });
                if (main) o.MainSensor = clone; else o.ExtraSensor = clone;
                Cloned(w, unitId, orig);
                return clone;
            }
            catch (Exception e) { Fail(e); CloneFailed(w, unitId, orig); return null; }
        }

        /// End of the first pass over the loaded units: stage 0 measurement, stage 1 results, proof flag.
        internal static void EndFirstPass(Walk w, int units)
        {
            var st = _state;
            if (st == null || w == null) return;
            try
            {
                ReportAmmo(st, w, units);
                ReportSensors(st, w);
                ReportMobility(st, w);
                ReportDecoys(st, w);
            }
            catch (Exception e) { Mod.Log.Warning("[VISEE] bilan impossible : " + e.Message); }
        }

        // ------------------------------------------------------------ internals

        static bool Active(State st, Slot s) => !st.Refused && !_dead && s.Wants && !s.Table && !s.Mixed;

        static Slot Find(Walk w, State st, IntPtr ptr, int rowId, byte kind)
        {
            if (st.Map.TryGetValue(ptr, out var s))
            {
                if (s.Kind == kind && s.RowId == rowId) return s;
                st.Map.Remove(ptr);                                          // the old object was freed: a new one lives at this address
            }
            else if (w.Local != null && w.Local.TryGetValue(ptr, out s))
            {
                if (s.Kind == kind && s.RowId == rowId) return s;
            }
            s = new Slot { Kind = kind, RowId = rowId, Table = st.TableRows.Contains(ptr) };
            if (w.Local == null) st.Map[ptr] = s; else w.Local[ptr] = s;
            return s;
        }

        static void Merge(Slot s, int unit, long key, Want a, Want b, Want c, byte cls, object owner)
        {
            bool wants = a.Set || b.Set || c.Set;
            if (s.Owners == 0)
            {
                s.Owners = 1; s.FirstUnit = unit; s.FirstKey = key;
                s.W[0] = a; s.W[1] = b; s.W[2] = c;
                s.Wants = wants; s.Class = cls; s.Owner = owner;
                return;
            }
            if (key != s.FirstKey)
            {
                if (!s.MultiKey) { s.MultiKey = true; s.Owners = 2; }
                if (unit != s.FirstUnit) s.MultiUnit = true;
            }
            if (!s.W[0].Same(a) || !s.W[1].Same(b) || !s.W[2].Same(c)) s.Mixed = true;
            if (wants) s.Wants = true;
            if (cls != ClassNone && s.Class == ClassNone) s.Class = cls;
            if (owner != null && s.Owner == null) s.Owner = owner;
        }

        /// Value to write for field i. Ammunition: the cap when the current value is above it (NaN = keep); sensors: the absolute value;
        /// mobility: multiplier of the database row value (or absolute).
        static float Target(State st, Slot s, int i, float cur, float tableValue, object obj)
        {
            var want = s.W[i];
            switch (s.Kind)
            {
                case KindAmmo:
                {
                    if (cur <= want.V + 0.5f) return float.NaN;
                    if (i == 0 && obj is AmmoRow a)
                    {
                        // a minimum range at or above the new maximum would leave the weapon unable to fire at all
                        float minR = a.MinimalRange;
                        if (minR > 0f && minR >= 0.9f * want.V) { st.MinRangeKept++; return float.NaN; }
                    }
                    return want.V;
                }
                case KindMobility:
                {
                    if (!want.X) return want.V;
                    float basis = tableValue;
                    if (float.IsNaN(basis))
                    {
                        if (!st.MobilityRows.TryGetValue(s.RowId, out var row) || row == null) return float.NaN;
                        var p = Props.Get(row.GetType(), Fields[KindMobility][i]);
                        if (p == null) return float.NaN;
                        basis = (float)p.GetValue(row);
                    }
                    return basis > 0f ? basis * want.V : float.NaN;
                }
                default: return want.V;
            }
        }

        static void MarkWritten(Walk w, State st, Slot s, object obj)
        {
            if (s.Written) return;
            s.Written = true;
            w.Written[s.Kind]++;
            st.WrittenTotal++;
            if (s.Kind == KindAmmo) st.WrittenByClass[s.Class]++;
            switch (s.Owner)
            {
                case Mount m: m.Written++; break;
                case SensorWant sw: sw.Written++; break;
                case MobilityWant mw: mw.Written++; break;
                case DecoyWant dw: dw.Written++; break;
            }
        }

        static void CountSkip(Walk w, State st, Slot s)
        {
            w.Skipped[s.Kind]++;
            if (s.Kind == KindAmmo) { if (s.Table) st.SkipTableByClass[s.Class]++; else st.SkipMixedByClass[s.Class]++; }
            switch (s.Owner)
            {
                case Mount m: m.Skipped++; break;
                case SensorWant sw: sw.Skipped++; break;
                case MobilityWant mw: mw.Skipped++; break;
                case DecoyWant dw: dw.Skipped++; break;
            }
        }

        static int Unwrite(Walk w, Slot s, Il2CppObjectBase obj)
        {
            int n = 0;
            var fields = Fields[s.Kind];
            for (int i = 0; i < 3; i++)
            {
                if (!s.HasP[i] || fields[i] == null) continue;
                if (!NumProp(obj, fields[i], out var p, out bool isInt, out float cur)) continue;
                if (Math.Abs(cur - s.V[i]) > 0.5f) continue;                  // not this module's value any more
                Realism.SetValueForOverride(obj, p, Boxed(isInt, s.P[i]));
                n++;
            }
            s.Written = false;
            Array.Clear(s.HasP, 0, 3);
            w.Unwritten[s.Kind]++;
            if (n > 0 && _unwriteLogs++ < 5)
                LogK(s.Kind, $"objet {s.RowId} partagé avec une autre unité découvert après écriture : {n} valeur(s) remise(s) comme avant");
            return n;
        }

        static SensorRow CloneSensor(SensorRow orig)
        {
            if (orig == null) return null;
            var o = orig.Clone();
            return o?.TryCast<SensorRow>();
        }

        static void CloneFailed(Walk w, int unitId, SensorRow orig)
        {
            try
            {
                w.Skipped[KindSensor]++;
                var st = _state;
                if (st != null && orig != null && st.Sensors.TryGetValue((unitId, orig.Id), out var sw)) sw.Skipped++;
            }
            catch { }
        }

        static void Cloned(Walk w, int unitId, SensorRow orig)
        {
            w.Cloned++;
            var st = _state;
            if (st != null && st.Sensors.TryGetValue((unitId, orig.Id), out var sw)) sw.Cloned++;
        }

        static void Example(State st, Slot s, object obj, string field, float before, float after)
        {
            if (st.Examples[s.Kind] >= MaxExamples) return;
            st.Examples[s.Kind]++;
            string who = s.Owner switch
            {
                Mount m => $"{m.Label} ({ClassNames[m.Class]})",
                SensorWant sw => sw.Label,
                MobilityWant mw => mw.Label,
                DecoyWant dw => dw.Label,
                _ => "unité " + s.FirstUnit,
            };
            LogK(s.Kind, $"exemple : {who}, objet {obj.GetType().Name} {s.RowId} {field} {before:0.##} -> {after:0.##}");
        }

        static Mount Resolve(State st, int unit, int weapon, int turret)
        {
            if (!st.Mounts.TryGetValue((unit, weapon), out var list)) return null;
            Mount any = null;
            foreach (var m in list)
            {
                if (m.Turret != 0 && m.Turret == turret) return m;
                if (m.Turret == 0) any = m;
            }
            return any;
        }

        static void Fail(Exception e)
        {
            if (_errors < 3) Mod.Log.Warning("[VISEE] erreur dans les données par unité : " + e.Message);
            if (++_errors > MaxErrors && !_dead)
            {
                _dead = true;
                Mod.Log.Warning("[VISEE] trop d'erreurs : données par unité coupées jusqu'au redémarrage du jeu (les valeurs déjà écrites restent journalisées)");
            }
        }

        // ------------------------------------------------------------ data files

        static HashSet<string> OptionsOff() =>
            new((Realism.OptionsInactives?.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        // a lot is all or nothing: its per-unit lines follow the verdict of the lot (refused, withdrawn, suspended, other database or scale)
        static bool LotOff(string opt) { try { return RealStats.LotInactif(opt); } catch { return false; } }

        static List<Dictionary<string, string>> ReadCsv(string file)
        {
            var rows = new List<Dictionary<string, string>>();
            string text;
            using (var s = typeof(Affuts).Assembly.GetManifestResourceStream("reel." + file))
            {
                if (s == null) { Mod.Log.Warning($"[VISEE] {file} absent du mod"); return rows; }
                using var r = new StreamReader(s, Encoding.UTF8);
                text = r.ReadToEnd();
            }
            string[] header = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                var cells = line.Split(';');
                if (header == null) { header = cells.Select(c => c.Trim()).ToArray(); continue; }
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < header.Length; i++) d[header[i]] = i < cells.Length ? cells[i].Trim() : "";
                rows.Add(d);
            }
            return rows;
        }

        static string Cell(Dictionary<string, string> r, string name) => r.TryGetValue(name, out var v) ? v ?? "" : "";

        static bool Int(string s, out int v) => int.TryParse(s, NumberStyles.Integer, Inv, out v);

        /// "" = unchanged, "600" = value, "x0.8" = multiplier. Range and optics values below the map edge follow the range scale.
        static bool ParseWant(string cell, bool range, float scale, out Want w)
        {
            w = Want.None;
            if (string.IsNullOrEmpty(cell)) return true;
            bool x = cell[0] == 'x' || cell[0] == 'X';
            if (!double.TryParse((x ? cell.Substring(1) : cell).Replace(',', '.'), NumberStyles.Float, Inv, out var v) || v < 0) return false;
            if (!x && range && scale > 1f && v > 0 && v < MapCap) v /= scale;
            w = new Want { V = (float)v, X = x };
            return true;
        }

        static void Bad(State st, string file, string msg)
        {
            st.BadRows++;
            if (_badLogs++ < 15) Mod.Log.Warning($"[VISEE] {file} : {msg} : ligne ignorée");
        }

        static bool SameName(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        static string UnitName(DbSource src, int id)
        {
            try { return src.Units.TryGetById(id, out UnitRow u) && u != null ? u.Name : null; } catch { return null; }
        }

        static void LoadMounts(State st, DbSource src, HashSet<string> off)
        {
            const string F = "Affuts.csv";
            foreach (var r in ReadCsv(F))
            {
                string opt = Cell(r, "Option");
                if (opt.Length > 0 && off.Contains(opt)) { st.OptionsOff++; continue; }
                if (opt.Length > 0 && LotOff(opt)) { st.LotsOff++; continue; }
                if (!Int(Cell(r, "UnitId"), out int unit) || !Int(Cell(r, "WeaponId"), out int weapon)) { Bad(st, F, "identifiant illisible"); continue; }
                string ts = Cell(r, "TurretId");
                int turret = 0;
                if (ts.Length > 0 && !Int(ts, out turret)) { Bad(st, F, $"tourelle '{ts}' illisible"); continue; }
                string c = Cell(r, "Classe").ToUpperInvariant();
                byte cls = c == "OEIL" ? ClassEye : c == "LUNETTE" ? ClassScope : ClassNone;
                if (cls == ClassNone) { Bad(st, F, $"classe '{c}' inconnue (U{unit} W{weapon})"); continue; }
                // a renamed row means the game changed its ids: never cap the wrong mount
                string un = UnitName(src, unit), wn = null;
                try { wn = src.Weapons.TryGetById(weapon, out WeaponRow wr) && wr != null ? wr.Name : null; } catch { }
                if (un == null || !SameName(un, Cell(r, "UnitName"))) { Bad(st, F, $"unité {unit} s'appelle '{un}' et non '{Cell(r, "UnitName")}'"); continue; }
                if (wn == null || !SameName(wn, Cell(r, "WeaponName"))) { Bad(st, F, $"arme {weapon} s'appelle '{wn}' et non '{Cell(r, "WeaponName")}'"); continue; }
                if (!st.Mounts.TryGetValue((unit, weapon), out var list)) st.Mounts[(unit, weapon)] = list = new List<Mount>();
                if (list.Any(m => m.Turret == turret)) { Bad(st, F, $"affût U{unit} W{weapon} T{turret} en double"); continue; }
                var mount = new Mount { Unit = unit, Weapon = weapon, Turret = turret, Class = cls, Label = $"U{unit} {un.Trim()} / W{weapon} {wn.Trim()}" + (turret != 0 ? $" / T{turret}" : "") };
                list.Add(mount);
                st.MountList.Add(mount);
            }
        }

        static void LoadCaps(State st)
        {
            const string F = "AffutsPortees.csv";
            foreach (var r in ReadCsv(F))
            {
                string c = Cell(r, "Classe").ToUpperInvariant();
                byte cls = c == "OEIL" ? ClassEye : c == "LUNETTE" ? ClassScope : ClassNone;
                if (cls == ClassNone) { Bad(st, F, $"classe '{c}' inconnue"); continue; }
                if (!ParseWant(Cell(r, "GroundRange"), true, st.Scale, out var g) || !ParseWant(Cell(r, "LowAltRange"), true, st.Scale, out var h) || g.X || h.X)
                { Bad(st, F, $"portée illisible ({c} {Cell(r, "Calibre")})"); continue; }
                float gv = g.V, hv = h.V;
                if (!float.IsNaN(gv) && !float.IsNaN(hv) && hv > gv) hv = gv;       // never reach helicopters farther than ground targets
                foreach (var tok in Cell(r, "Munitions").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Int(tok, out int ammo)) { Bad(st, F, $"munition '{tok}' illisible"); continue; }
                    st.Caps[(cls, ammo)] = (gv, hv);
                }
            }
        }

        static void LoadSensors(State st, DbSource src, HashSet<string> off)
        {
            const string F = "CapteursParUnite.csv";
            foreach (var r in ReadCsv(F))
            {
                string opt = Cell(r, "Option");
                if (opt.Length > 0 && off.Contains(opt)) { st.OptionsOff++; continue; }
                if (opt.Length > 0 && LotOff(opt)) { st.LotsOff++; continue; }
                if (!Int(Cell(r, "UnitId"), out int unit) || !Int(Cell(r, "SensorId"), out int sensor)) { Bad(st, F, "identifiant illisible"); continue; }
                string un = UnitName(src, unit);
                if (un == null || !SameName(un, Cell(r, "Name"))) { Bad(st, F, $"unité {unit} s'appelle '{un}' et non '{Cell(r, "Name")}'"); continue; }
                var sw = new SensorWant { Unit = unit, Sensor = sensor, Label = $"U{unit} {un.Trim()} capteur {sensor}" };
                bool ok = true;
                for (int i = 0; i < 3; i++) { ok &= ParseWant(Cell(r, Fields[KindSensor][i]), true, st.Scale, out sw.W[i]); ok &= !sw.W[i].X; }
                if (!ok || !(sw.W[0].Set || sw.W[1].Set || sw.W[2].Set)) { Bad(st, F, $"valeurs illisibles ({sw.Label})"); continue; }
                if (st.Sensors.ContainsKey((unit, sensor))) { Bad(st, F, $"{sw.Label} en double"); continue; }
                st.Sensors[(unit, sensor)] = sw;
            }
        }

        static void LoadMobility(State st, DbSource src, HashSet<string> off)
        {
            const string F = "MobiliteEquipes.csv";
            foreach (var r in ReadCsv(F))
            {
                string opt = Cell(r, "Option");
                if (opt.Length > 0 && off.Contains(opt)) { st.OptionsOff++; continue; }
                if (opt.Length > 0 && LotOff(opt)) { st.LotsOff++; continue; }
                if (!Int(Cell(r, "UnitId"), out int unit)) { Bad(st, F, "identifiant illisible"); continue; }
                string un = UnitName(src, unit);
                if (un == null || !SameName(un, Cell(r, "Name"))) { Bad(st, F, $"unité {unit} s'appelle '{un}' et non '{Cell(r, "Name")}'"); continue; }
                var mw = new MobilityWant { Unit = unit, Label = $"U{unit} {un.Trim()}" };
                bool ok = true;
                for (int i = 0; i < 3; i++) ok &= ParseWant(Cell(r, Fields[KindMobility][i]), false, 1f, out mw.W[i]);
                if (!ok || !(mw.W[0].Set || mw.W[1].Set || mw.W[2].Set)) { Bad(st, F, $"vitesses illisibles ({mw.Label})"); continue; }
                if (st.Mobility.ContainsKey(unit)) { Bad(st, F, $"{mw.Label} en double"); continue; }
                st.Mobility[unit] = mw;
            }
        }

        static void LoadDecoys(State st, DbSource src, HashSet<string> off)
        {
            const string F = "LeurresParUnite.csv";
            _decoyByUnit.Clear();
            foreach (var r in ReadCsv(F))
            {
                string opt = Cell(r, "Option");
                if (opt.Length > 0 && off.Contains(opt)) { st.OptionsOff++; continue; }
                if (opt.Length > 0 && LotOff(opt)) { st.LotsOff++; continue; }
                if (!Int(Cell(r, "UnitId"), out int unit)) { Bad(st, F, "identifiant illisible"); continue; }
                string un = UnitName(src, unit);
                if (un == null) { st.DecoyUnitsAbsent++; continue; }                 // not in this database (older data source without the DLC)
                if (!SameName(un, Cell(r, "Name"))) { Bad(st, F, $"unité {unit} s'appelle '{un}' et non '{Cell(r, "Name")}'"); continue; }
                var dw = new DecoyWant { Unit = unit, Class = Cell(r, "Classe"), Label = $"U{unit} {un.Trim()}" };
                if (!Int(Cell(r, "DecoyQuantity"), out int q) || q <= 0 || q > 500) { Bad(st, F, $"stock de leurres illisible ({dw.Label})"); continue; }
                if (!ParseWant(Cell(r, "DecoySupplyCost"), false, 1f, out var cost) || cost.X) { Bad(st, F, $"coût par salve illisible ({dw.Label})"); continue; }
                // gap between two salvos: 1 s unless the file gives another value
                var cooldown = new Want { V = HeliDecoyCooldown };
                string cd = Cell(r, "DecoyCooldown");
                if (cd.Length > 0 && (!ParseWant(cd, false, 1f, out cooldown) || cooldown.X || !(cooldown.V > 0f) || cooldown.V > 30f))
                { Bad(st, F, $"intervalle entre salves illisible ({dw.Label})"); continue; }
                dw.Quantity = q;
                dw.Cooldown = cooldown.V;
                dw.W[0] = new Want { V = q };
                dw.W[1] = cost;
                dw.W[2] = cooldown;
                if (st.Decoys.ContainsKey(unit)) { Bad(st, F, $"{dw.Label} en double"); continue; }
                st.Decoys[unit] = dw;
                _decoyByUnit[unit] = (q, cooldown.V);                               // read by LeurresAuto (stock and gap of an aircraft)
            }
            if (st.Decoys.Count > 0) LogDecoyRows(st, src);
        }

        /// Flare stock and gap between two salvos of a listed helicopter, as the file gives them (LeurresAuto). False for a unit the file
        /// does not list (planes, unlisted helicopters). Kept after EndPlan: it is data, not a plan. Main thread.
        internal static bool TryDecoyStock(int unitId, out int qty, out float gap)
        {
            if (_decoyByUnit.TryGetValue(unitId, out var v)) { qty = v.Qty; gap = v.Gap; return true; }
            qty = 0; gap = 0f;
            return false;
        }

        /// Which decoy ability rows each listed helicopter uses in the database (own abilities and options), and which rows are shared
        /// by helicopters wanting different stocks or by units that are not listed (planes): those rows keep their base value.
        static void LogDecoyRows(State st, DbSource src)
        {
            var decoyRows = new Dictionary<int, string>();
            foreach (var a in Props.Rows(src.Abilities.GetAll())) if (a != null && a.IsDecoy) decoyRows[a.Id] = (a.Name ?? "").Trim();
            var users = new Dictionary<int, SortedSet<int>>();
            void Add(int ability, int unit)
            {
                if (ability <= 0 || !decoyRows.ContainsKey(ability)) return;
                if (!users.TryGetValue(ability, out var set)) users[ability] = set = new SortedSet<int>();
                set.Add(unit);
            }
            foreach (var ua in Props.Rows(src.UnitAbilities.GetAll())) if (ua != null) Add(ua.AbilityId, ua.UnitId);
            var modUnit = new Dictionary<int, int>();
            foreach (var m in Props.Rows(src.Modifications.GetAll())) if (m != null) modUnit[m.Id] = m.UnitId;
            foreach (var o in Props.Rows(src.Options.GetAll()))
            {
                if (o == null || !modUnit.TryGetValue(o.ModificationId, out int u)) continue;
                Add(o.Ability1Id, u); Add(o.Ability2Id, u); Add(o.Ability3Id, u);
            }
            var sb = new StringBuilder();
            int mixed = 0, foreign = 0;
            foreach (var kv in users.OrderBy(k => k.Key))
            {
                var listed = kv.Value.Where(u => st.Decoys.ContainsKey(u)).ToList();
                if (listed.Count == 0) continue;                                        // plane rows: not concerned
                var others = kv.Value.Where(u => !st.Decoys.ContainsKey(u)).ToList();
                bool diff = listed.Select(u => st.Decoys[u].Quantity).Distinct().Count() > 1;
                if (diff) mixed++;
                if (others.Count > 0) foreign++;
                sb.Append($" [{kv.Key} {decoyRows[kv.Key]} :{(diff ? " stocks différents," : "")} {string.Join(", ", listed.Select(u => $"{st.Decoys[u].Label} {st.Decoys[u].Quantity}"))}");
                if (others.Count > 0) sb.Append(" ; non listées : " + string.Join(", ", others.Select(u => $"U{u} {(UnitName(src, u) ?? "?").Trim()}")));
                sb.Append(']');
            }
            LogK(KindDecoy, $"lignes de leurres des hélicoptères (base de données) : {mixed} ligne(s) servant à des stocks différents (valeur écrite sur la copie de chaque hélicoptère), " +
                            $"{foreign} ligne(s) aussi portée(s) par des unités non listées ;{sb}");
            var none = st.Decoys.Values.Where(d => !users.Values.Any(s => s.Contains(d.Unit))).Select(d => d.Label).ToList();
            if (none.Count > 0) LogK(KindDecoy, $"hélicoptère(s) listé(s) sans capacité de leurres dans la base : {none.Count}{List(none)}");
        }

        /// (unit, ammo) -> caps, from the listed mounts and the database loadouts. Ambiguous: the unit fires this ammunition from weapons of
        /// different classes (a non-listed weapon counts as camera), or from a mount listed for one turret only.
        static Dictionary<long, (float g, float h)> BuildCapTable(State st, DbSource src)
        {
            var listedUnits = new HashSet<int>(st.MountList.Select(m => m.Unit));
            var weaponsByPair = new Dictionary<(int unit, int ammo), HashSet<int>>();
            foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
            {
                if (wa == null || !listedUnits.Contains(wa.UnitId)) continue;
                var k = (wa.UnitId, wa.AmmunitionId);
                if (!weaponsByPair.TryGetValue(k, out var set)) weaponsByPair[k] = set = new HashSet<int>();
                set.Add(wa.WeaponId);
            }
            var table = new Dictionary<long, (float g, float h)>();
            var ambiguousPairs = new HashSet<long>();
            foreach (var kv in weaponsByPair)
            {
                int cls = -1;
                bool ambiguous = false;
                foreach (var weapon in kv.Value)
                {
                    int c = ClassNone;
                    if (st.Mounts.TryGetValue((kv.Key.unit, weapon), out var list))
                    {
                        if (list.Any(m => m.Turret != 0)) { ambiguous = true; break; }
                        c = list[0].Class;
                    }
                    if (cls < 0) cls = c; else if (cls != c) { ambiguous = true; break; }
                }
                if (ambiguous) { st.CapAmbiguous++; ambiguousPairs.Add(((long)kv.Key.unit << 32) | (uint)kv.Key.ammo); continue; }
                if (cls <= ClassNone || !st.Caps.TryGetValue(((byte)cls, kv.Key.ammo), out var cap)) continue;
                if (float.IsNaN(cap.g) && float.IsNaN(cap.h)) continue;
                table[((long)kv.Key.unit << 32) | (uint)kv.Key.ammo] = cap;
                st.CapPairs++;
            }
            _capAmbiguous = ambiguousPairs;
            return table;
        }

        // ------------------------------------------------------------ reports (first pass)

        static void ReportAmmo(State st, Walk w, int units)
        {
            var ammo = st.Map.Values.Where(s => s.Kind == KindAmmo).ToList();
            var byId = ammo.GroupBy(s => s.RowId).Select(g => (id: g.Key, n: g.Count())).OrderByDescending(t => t.n).ToList();
            int multiKey = ammo.Count(s => s.MultiKey), multiUnit = ammo.Count(s => s.MultiUnit), table = ammo.Count(s => s.Table), mixed = ammo.Count(s => s.Mixed);
            string top = byId.Count > 0 ? $"moyenne {(double)ammo.Count / byId.Count:0.#}, max {byId[0].n} (munition {byId[0].id})" : "aucune";
            Log($"étape 0 (mesure) : {units} unité(s) chargée(s), {ammo.Count} objet(s) munition vus pour {byId.Count} munition(s) ; copies par munition : {top} ; " +
                $"atteints par plusieurs affûts {multiKey} (entre unités différentes {multiUnit}) ; ligne de la table elle-même {table} ; valeurs voulues différentes {mixed}");

            var tracked = new HashSet<int>(st.Caps.Keys.Select(k => k.ammo));
            var sb = new StringBuilder();
            foreach (var id in tracked.OrderBy(i => i))
            {
                var l = ammo.Where(s => s.RowId == id).ToList();
                if (l.Count == 0) continue;
                sb.Append($" [{id} : {l.Count} copie(s), entre unités {l.Count(s => s.MultiUnit)}, table {l.Count(s => s.Table)}, visées {l.Count(s => s.Wants)}]");
            }
            if (sb.Length > 0) Log("munitions des affûts :" + sb);

            var wanted = ammo.Where(s => s.Wants).ToList();
            int exclusive = wanted.Count(s => !s.Table && !s.MultiUnit);
            _copiesProven = wanted.Count >= 20 && exclusive * 100 >= wanted.Count * 90;
            Log($"copies propres à chaque unité : {(_copiesProven ? "PROUVÉ" : "NON prouvé")} ({exclusive} copie(s) visée(s) sans partage entre unités ni ligne de table sur {wanted.Count})");

            var never = st.MountList.Where(m => !m.Seen).ToList();
            var unknown = st.MountList.Where(m => m.UnknownAmmo).ToList();
            var skippedAll = st.MountList.Where(m => m.Seen && m.Written == 0 && m.Skipped > 0).ToList();
            string state = st.Refused ? "mesure seulement (sécurité)" : "écrit";
            Log($"étape 1 ({state}) : OEIL {st.WrittenByClass[ClassEye]} copie(s) raccourcie(s), sautées {st.SkipMixedByClass[ClassEye] + st.SkipTableByClass[ClassEye]} (partagées {st.SkipMixedByClass[ClassEye]}, table {st.SkipTableByClass[ClassEye]}) ; " +
                $"LUNETTE {st.WrittenByClass[ClassScope]} copie(s) raccourcie(s), sautées {st.SkipMixedByClass[ClassScope] + st.SkipTableByClass[ClassScope]} (partagées {st.SkipMixedByClass[ClassScope]}, table {st.SkipTableByClass[ClassScope]}) ; " +
                $"valeurs remises (partage découvert) {w.Unwritten[KindAmmo]} ; portée mini trop haute (plafond non posé) {st.MinRangeKept}");
            Log($"affûts listés : {st.MountList.Count}, vus {st.MountList.Count - never.Count}, jamais vus {never.Count}{List(never.Select(m => m.Label))} ; " +
                $"toutes copies sautées {skippedAll.Count}{List(skippedAll.Select(m => m.Label))} ; munition sans plafond pour sa classe {unknown.Count}{List(unknown.Select(m => m.Label))}");
        }

        static void ReportSensors(State st, Walk w)
        {
            if (st.Sensors.Count == 0) return;
            var all = st.Sensors.Values.ToList();
            var never = all.Where(s => !s.Seen).ToList();
            var skipped = all.Where(s => s.Seen && s.Written == 0).ToList();
            var slots = st.Map.Values.Where(s => s.Kind == KindSensor).ToList();
            LogK(KindSensor, $"optiques par unité ({(st.Refused ? "mesure seulement" : "écrit")}) : {all.Count} visée(s) ; objets capteur vus {slots.Count} (table {slots.Count(s => s.Table)}, entre unités {slots.Count(s => s.MultiUnit)}) ; " +
                $"copies écrites {w.Written[KindSensor]}, copies propres créées {w.Cloned} (objet partagé ou ligne de table), remises {w.Unwritten[KindSensor]} ; " +
                $"jamais vues {never.Count}{List(never.Select(s => s.Label))} ; vues sans écriture {skipped.Count}{List(skipped.Select(s => s.Label))}");
        }

        static void ReportMobility(State st, Walk w)
        {
            if (st.Mobility.Count == 0) return;
            var all = st.Mobility.Values.ToList();
            var never = all.Where(m => !m.Seen).ToList();
            var skipped = all.Where(m => m.Seen && m.Written == 0).ToList();
            var slots = st.Map.Values.Where(s => s.Kind == KindMobility && s.Wants).ToList();
            string basis = "";
            if (st.MobilityRows.TryGetValue(11, out var inf) && inf != null)
                basis = $" ; ligne infanterie 11 : route {inf.MaxSpeedRoad:0.#}, tout-terrain {inf.MaxCrossCountrySpeed:0.#}, eau {inf.MaxSpeedWater:0.#} km/h";
            LogK(KindMobility, $"équipes lourdes ({(st.Refused ? "mesure seulement" : "écrit")}) : {all.Count} unité(s) ; copies de mobilité visées {slots.Count} : ralenties {w.Written[KindMobility]}, " +
                $"laissées car partagées avec d'autres unités {slots.Count(s => s.Mixed && !s.Table)}, ligne de la table elle-même {slots.Count(s => s.Table)}, remises {w.Unwritten[KindMobility]} ; " +
                $"jamais vues {never.Count}{List(never.Select(m => m.Label))} ; sans copie ralentie {skipped.Count}{List(skipped.Select(m => m.Label))}{basis}");
        }

        static void ReportDecoys(State st, Walk w)
        {
            if (st.Decoys.Count == 0) return;
            var all = st.Decoys.Values.ToList();
            var slots = st.Map.Values.Where(s => s.Kind == KindDecoy).ToList();
            var never = all.Where(d => !d.Seen).ToList();
            var blocked = new List<string>();
            foreach (var d in all)
            {
                int bad = 0;
                foreach (var ptr in d.Objects) if (st.Map.TryGetValue(ptr, out var s) && s.Kind == KindDecoy && (s.Table || s.Mixed)) bad++;
                if (bad > 0) blocked.Add($"{d.Label} {bad}/{d.Objects.Count}");
            }
            LogK(KindDecoy, $"stock de leurres par hélicoptère ({(st.Refused ? "mesure seulement" : "écrit")}) : {all.Count} hélicoptère(s) listé(s), absent(s) de cette base {st.DecoyUnitsAbsent} ; " +
                            $"objets capacité de leurres vus {slots.Count} (ligne de la table elle-même {slots.Count(s => s.Table)}, entre unités {slots.Count(s => s.MultiUnit)}, valeurs voulues différentes {slots.Count(s => s.Mixed)}) ; " +
                            $"copies écrites {w.Written[KindDecoy]}, remises {w.Unwritten[KindDecoy]} ; jamais vus {never.Count}{List(never.Select(d => d.Label))} ; " +
                            $"copies laissées à la valeur de la ligne (table ou partagée) {blocked.Count}{List(blocked)}");
            var byClass = all.Where(d => d.Seen).GroupBy(d => $"{d.Class} {d.Quantity} salves, une toutes les {d.Cooldown.ToString("0.##", Inv)} s")
                             .Select(g => $"{g.Key} : " + string.Join(", ", g.Select(d => $"{d.Label} ({string.Join("/", d.Rows)})")));
            LogK(KindDecoy, "capacités de leurres atteintes par classe : " + string.Join(" | ", byClass));
        }

        static string List(IEnumerable<string> items)
        {
            var l = items.ToList();
            if (l.Count == 0) return "";
            return " (" + string.Join(", ", l.Take(MaxListed)) + (l.Count > MaxListed ? $", +{l.Count - MaxListed}" : "") + ")";
        }
    }
}
