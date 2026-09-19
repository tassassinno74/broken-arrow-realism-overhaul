// Real-life stats from the "Contreparties réalistes" tables (14/09/2026).
// The data ships inside the DLL (reel\*.csv embedded resources): nothing to install by hand. Values are written through
// Realism.SetValueForOverride, so they are journaled and restored with the rest of the campaign realism.
// v0.23.0 data (17/09/2026): calibre damage and minimum armour (OPTION_DEGATS_CALIBRE, Blindages.csv), building cover pierced
// per ammunition (OPTION_COUVERT_REALISTE), MANPADS minimum ranges and RPG range against flying helicopters (OPTION_VOL_BAS_REALISTE),
// infantry foot speed and sprint (OPTION_VITESSE_INFANTERIE_REELLE). A ">=" cell only raises a value, so a minimum never lowers the game's value.
// v0.24.0 data (real distances at scale 1): the option keys of LotOrder are lots. Before any write every lot is checked as a whole on the
// merged state of the tables (game values, base lines, then the option lines in file order, lots refused before it left out): ids and
// names, columns, bounds, no duplicate cell, minimum ranges below 90 % of the final ranges, per-unit sensor pairs (CapteursParUnite.csv),
// and for the ballistics lot the reach of every direct-fire row under real gravity. A lot failing a check gets no write at all. A lot
// failing while it is written (refused cell, minimum range adjusted, read-back mismatch) is put back value by value, with the lots
// validated on top of it. Every write of a lot is journaled under its option key (Realism.Lot), so a lot is taken out of the journal as a
// whole (Realism.RestoreLot). The ballistics lot writes the real gravity together with its muzzle velocities (all or nothing).
// After the writes, the low-altitude range of the anti-helicopter, low-flight and manual-aim option rows is kept at or below the ground
// range. Lots only apply to the "Resources default" database at range scale 1. Log: [ECHELLE].
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.DataBase.Models;
using Il2CppBrokenArrow.Shared.Ecs;
using TrajectoryKind = Il2CppBrokenArrow.DataBase.Enums.TrajectoryType;
using GameCfg = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;

namespace RealismOverhaul
{
    static class RealStats
    {
        // playable map edge: a value at or above it means "whole map" and is never shortened by the range scale
        const double MapCap = 9000;
        static readonly string[] Files = { "Munitions.csv", "Armes.csv", "Mobilite.csv", "Unites.csv", "Avions.csv", "Capteurs.csv", "Blindages.csv", "Capacites.csv", "Chargements.csv", "Disponibilite.csv", "Explosions.csv" };
        static readonly HashSet<string> RangeFields = new(StringComparer.OrdinalIgnoreCase)
            { "GroundRange", "LowAltRange", "HighAltRange", "MinimalRange", "MaxSeekerDistance", "NonSeadProjectileRangeOverride", "SeadProjectileRangeOverride",
              "OpticsGround", "OpticsLowAltitude", "OpticsHighAltitude", "LaserMaxRange" };
        static Dictionary<int, int[]> _avail;

        sealed class Counts
        {
            // Kept: ">=" cells whose current value was already high enough (nothing written)
            // LotLines: lines of a lot that is not applied (refused, suspended, other database), counted apart from the options switched off
            public int Cells, Loads, Rejected, UnknownRows, NameMismatch, OptionsOff, Logged, MinFixed, MinLogged, Removed, Kept, LotLines;
            public readonly Dictionary<string, int> Options = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<int> Ammo = new();
        }

        // ------------------------------------------------------------ lots (real distances at scale 1)
        internal const string LotPortees = "OPTION_PORTEES_REELLES", LotVisee = "OPTION_VISEE_OEIL", LotLaser = "OPTION_LASER_PORTEE_REELLE",
            LotCapteurs = "OPTION_CAPTEURS_SOL_REELS", LotVueAir = "OPTION_VUE_AIR_REELLE", LotCapteurDedie = "OPTION_CAPTEUR_DEDIE",
            LotRadar = "OPTION_RADAR_COURTE_PORTEE", LotMinimales = "OPTION_PORTEES_MINI_REELLES", LotFumee = "OPTION_FUMEE_REELLE",
            LotBalistique = "OPTION_BALISTIQUE_REELLE";
        const string DefaultSource = "Resources default";
        internal const float RealGravity = 9.81f;
        const int MaxUnreachableUnderLotB = 3;          // direct-fire rows allowed without a firing solution under real gravity
        const int ClampWarnAbove = 5;                   // low-altitude clamps expected with the range lot accepted: 0

        // validation order: a lot is checked on top of the lots accepted before it; DependsOn: withdrawn with that lot
        static readonly (string Key, string Tag, string DependsOn)[] LotOrder =
        {
            (LotPortees, "A", null), (LotVisee, "A'", null), (LotLaser, "L", null), (LotCapteurs, "C", null), (LotVueAir, "C2", null),
            (LotCapteurDedie, "Q4", null), (LotRadar, "Q4-radar", null), (LotMinimales, "M", LotPortees), (LotFumee, "F", null), (LotBalistique, "B", null),
        };
        static readonly HashSet<string> UnitSensorLots = new(StringComparer.OrdinalIgnoreCase) { LotCapteurs, LotCapteurDedie, LotRadar };
        // option rows whose low-altitude range must never exceed the ground range (anti-helicopter caps, low flight, manual aim)
        static readonly HashSet<string> LowAltOptions = new(StringComparer.OrdinalIgnoreCase) { "OPTION_TIR_ANTIHELICO_COURT", "OPTION_VOL_BAS_REALISTE", LotVisee };
        // columns a lot line may carry, with their bounds (min exclusive unless MinIn)
        static readonly Dictionary<string, (double Min, double Max, bool MinIn)> Bounds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GroundRange"] = (0, MapCap, false), ["LowAltRange"] = (0, MapCap, false), ["HighAltRange"] = (0, MapCap, false),
            ["MinimalRange"] = (0, MapCap, false), ["MaxSeekerDistance"] = (0, MapCap, false),
            ["SeadProjectileRangeOverride"] = (0, MapCap, false), ["NonSeadProjectileRangeOverride"] = (0, MapCap, false),
            ["OpticsGround"] = (0, MapCap, false), ["OpticsLowAltitude"] = (0, MapCap, false), ["OpticsHighAltitude"] = (0, MapCap, false),
            ["MuzzleVelocity"] = (50, 2000, true), ["MaxSpeed"] = (50, 2700, true), ["Acceleration"] = (1, 3000, true), ["BurnTime"] = (0.1, 60, true),
            ["LaserMaxRange"] = (0, MapCap, true), ["SmokeRadius"] = (1, 300, true),
        };

        const string StOk = "ok", StRefused = "refusé", StWithdrawn = "retiré", StOff = "coupé", StSuspended = "suspendu",
                     StOtherDb = "hors base", StOtherScale = "échelle différente de 1", StAbsent = "absent", StNotApplied = "non appliqué";

        sealed class Rec { public object Row; public PropertyInfo P; public object Before, After; public int Seq; }

        sealed class LotState
        {
            public string Key, Tag, DependsOn, Status = StNotApplied, Reason;
            public int Lines, UnitLines, Values;
            public bool Gravity;
            public readonly List<Rec> Undo = new();
        }

        static readonly Dictionary<string, LotState> _lots = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<(IntPtr, string), int> _lastSeq = new();
        static readonly Dictionary<string, Plan> _plans = new(StringComparer.OrdinalIgnoreCase);
        static DataBaseSourceData _lotSrc;              // held reference: the database the lot state belongs to
        static HashSet<string> _offWrite;
        static int _seq;
        static string _lastScopeLog;

        static RealStats()
        {
            foreach (var (key, tag, dep) in LotOrder) _lots[key] = new LotState { Key = key, Tag = tag, DependsOn = dep };
        }

        /// Extra lot keys to leave out at the next application (the combat-health watchdog fills it after a trip).
        internal static Func<IEnumerable<string>> LotsSuspendusSource = null;

        /// The lot keys of this version, in validation order.
        internal static IEnumerable<string> LotsIntroduits => LotOrder.Select(l => l.Key);

        /// True when this lot was validated and written on the database of the last application and is still in place: a lot with table
        /// values still holds journal entries under its key (none after Realism.Restore or Realism.RestoreLot); a lot of per-unit lines only
        /// needs the realism applied (or being applied).
        internal static bool LotAccepte(string key)
        {
            if (key == null || _lotSrc == null || !_lots.TryGetValue(key.Trim(), out var l) || l.Status != StOk) return false;
            try { return l.Values > 0 ? Realism.LotWrites(l.Key) > 0 : Realism.IsApplied || Realism.JournalCount > 0; }
            catch { return false; }
        }

        /// True when the lines of this option must not be applied: a lot not accepted on the last application (refused, withdrawn,
        /// switched off, suspended, other database, not applied yet). Any other option name: false.
        internal static bool LotInactif(string option) => option != null && _lots.ContainsKey(option.Trim()) && !LotAccepte(option);

        /// One line per lot for the logs: "A=ok(... valeurs) ... B=refusé(raison)".
        internal static string ResumeLots => LotSummary();

        /// Puts back every value this lot wrote (and the lots validated on top of it) on the table rows; returns the number of values put back.
        /// A value written later by another line keeps that line's value. The lot state, the dependent lots and the low-altitude and minimum
        /// range rules follow (use this rather than Realism.RestoreLot alone for a data lot).
        internal static int RestoreLot(string key, string why)
        {
            try
            {
                if (key == null || !_lots.TryGetValue(key.Trim(), out var lot) || lot.Status != StOk) return 0;
                int n = WithdrawLot(lot, why ?? "retrait demandé", _offWrite);
                ClampLowAlt();
                return n;
            }
            catch (Exception e)
            {
                Mod.Log.Warning("[ECHELLE] retrait du lot " + key + " impossible : " + e.Message);
                return 0;
            }
        }

        /// The realism was restored: the lot state no longer describes the database (every lot back to "not applied", nothing held).
        internal static void Oublier()
        {
            foreach (var l in _lots.Values) { l.Status = StNotApplied; l.Reason = null; l.Gravity = false; l.Undo.Clear(); }
            _lotSrc = null;
            _offWrite = null;
            _plans.Clear();
            _lastSeq.Clear();
        }

        static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_realisme", "reel");

        static string Embedded(string file)
        {
            using var s = typeof(RealStats).Assembly.GetManifestResourceStream("reel." + file);
            if (s == null) return null;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// Copies the embedded files to UserData so they can be read. The mod itself always uses its embedded copy.
        internal static void ExportReference()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                int n = 0;
                foreach (var f in Files)
                {
                    var text = Embedded(f);
                    if (text == null) continue;
                    var path = Path.Combine(Dir, f);
                    if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) continue;
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    n++;
                }
                if (n > 0) Mod.Log.Msg($"[VRAIES STATS] {n} fichier(s) de référence mis à jour dans {Dir}");
            }
            catch (Exception e) { Mod.Log.Warning("[VRAIES STATS] fichiers de référence non écrits : " + e.Message); }
        }

        static List<string[]> Read(string file, out string[] header)
        {
            header = null;
            var rows = new List<string[]>();
            var text = Embedded(file);
            if (text == null) return rows;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                var cells = line.Split(';');
                if (header == null) header = cells.Select(c => c.Trim()).ToArray(); else rows.Add(cells);
            }
            return rows;
        }

        static HashSet<string> OptionsOff() =>
            new((Realism.OptionsInactives?.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        internal static string Apply(DataBaseSourceData src, double scale) => Apply(src, scale, null);

        /// sourceId: DataBaseService.CurrentSourceId of src (read from the running service when null).
        internal static string Apply(DataBaseSourceData src, double scale, string sourceId)
        {
            var c = new Counts();
            var off = OptionsOff();
            _plans.Clear();
            _lastSeq.Clear();
            try { PrepareLots(src, scale, sourceId, off); }
            catch (Exception e)
            {
                foreach (var l in _lots.Values)
                    if (l.Status == null || l.Status == StOk) { l.Status = StRefused; l.Reason = "contrôle impossible"; }
                Mod.Log.Warning("[ECHELLE] contrôle des lots impossible, aucun lot appliqué : " + e.Message);
            }
            var offWrite = new HashSet<string>(off, StringComparer.OrdinalIgnoreCase);
            foreach (var l in _lots.Values) if (l.Status != StOk) offWrite.Add(l.Key);
            _offWrite = offWrite;

            // one table failing (game update, renamed column...) must not cancel the others nor the rest of the realism;
            // a lot whose lines were in a failing table is put back as a whole
            void Safe(string what, Action a)
            {
                try { a(); }
                catch (Exception e)
                {
                    Mod.Log.Warning($"[VRAIES STATS] {what} ignoré : {e.Message}");
                    if (_plans.TryGetValue(what, out var pl))
                        foreach (var l in _lots.Values.ToList())
                            if (l.Status == StOk && pl.Lines.Any(x => x.Opt.Equals(l.Key, StringComparison.OrdinalIgnoreCase)))
                                WithdrawLot(l, what + " interrompu : " + e.Message, offWrite);
                }
            }
            Safe("Munitions.csv", () => Table("Munitions.csv", Props.Rows(src.Ammunitions.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c, c.Ammo));
            // real gravity goes with the real muzzle velocities of the ballistics lot, right after them
            Safe("pesanteur", ApplyGravity);
            // explosion radii come after the munitions table, so they win over its values (including its option rows)
            Safe("Explosions.csv", () => Table("Explosions.csv", Props.Rows(src.Ammunitions.GetAll()), r => r.Id, r => r.Name, 1, offWrite, c));
            Safe("Armes.csv", () => Table("Armes.csv", Props.Rows(src.Weapons.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Mobilite.csv", () => Table("Mobilite.csv", Props.Rows(src.Mobility.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Unites.csv", () => Table("Unites.csv", Props.Rows(src.Units.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Avions.csv", () => Table("Avions.csv", Props.Rows(src.PlaneFlyPresets.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Capteurs.csv", () => Table("Capteurs.csv", Props.Rows(src.Sensors.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Blindages.csv", () => Table("Blindages.csv", Props.Rows(src.Armors.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Capacites.csv", () => Table("Capacites.csv", Props.Rows(src.Abilities.GetAll()), r => r.Id, r => r.Name, scale, offWrite, c));
            Safe("Chargements.csv", () => Loads(Props.Rows(src.WeaponAmmunitions.GetAll()), off, c));
            Safe("relecture", ReadBackLots);
            int clamp = 0;
            Safe("portées basse altitude", () => clamp = ClampLowAlt());
            Safe("contrôle", () => CheckKinematics(src, c.Ammo));
            string reach = "non contrôlée";
            Safe("atteinte", () => reach = CheckReach(src));
            if (c.Options.Count > 0) Mod.Log.Msg("[VRAIES STATS] options appliquées : " + string.Join(", ", c.Options.Select(kv => $"{kv.Key}={kv.Value}")));
            if (off.Count > 0) Mod.Log.Msg("[VRAIES STATS] options désactivées par les préférences : " + string.Join(", ", off));
            string lots = LotSummary();
            Mod.Log.Msg($"[ECHELLE] lots : {lots} ; clamp L≤G={clamp} ; atteinte : {reach}");
            return $"valeurs={c.Cells} chargements={c.Loads} refusées={c.Rejected} lignes inconnues={c.UnknownRows} noms différents={c.NameMismatch} options désactivées={c.OptionsOff} portées mini ajustées={c.MinFixed} déjà au niveau={c.Kept} échelle={scale}" +
                   $" ; échelle réelle : {lots}" + (c.LotLines > 0 ? $" (lignes de lots non appliquées={c.LotLines})" : "");
        }

        // ------------------------------------------------------------ parsed tables

        sealed class Line
        {
            public int Id;
            public string Opt = "", IdText = "?", NameCell = "", RowName;
            public object Row;                     // null: row unknown or renamed
            public bool Renamed;
            public string[] Cells;
        }

        sealed class Plan
        {
            public string File;
            public string[] Header;
            public PropertyInfo[] Props;
            public int IOpt = -1, IId = -1, IName = -1;
            public readonly List<string> MissingColumns = new();
            public readonly List<Line> Lines = new();
        }

        static Plan PlanFor<T>(string file, List<T> rows, Func<T, int> id, Func<T, string> name)
        {
            if (_plans.TryGetValue(file, out var cached)) return cached;
            var lines = Read(file, out var header);
            if (header == null) return null;
            var pl = new Plan { File = file, Header = header, Props = new PropertyInfo[header.Length] };
            pl.IOpt = Array.FindIndex(header, h => h.Equals("Option", StringComparison.OrdinalIgnoreCase));
            pl.IId = Array.FindIndex(header, h => h.Equals("Id", StringComparison.OrdinalIgnoreCase));
            pl.IName = Array.FindIndex(header, h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
            _plans[file] = pl;
            if (pl.IId < 0) return pl;
            for (int i = 0; i < header.Length; i++)
            {
                if (i == pl.IOpt || i == pl.IId || i == pl.IName) continue;
                if (header[i].Length == 0 || header[i].StartsWith("__")) continue;   // padding column or trailing separator
                var p = Props.Get(typeof(T), header[i]);
                if (p == null || !p.CanWrite) { pl.MissingColumns.Add(header[i]); continue; }
                pl.Props[i] = p;
            }
            var byId = new Dictionary<int, T>();
            foreach (var r in rows) byId[id(r)] = r;
            foreach (var cells in lines)
            {
                var ln = new Line { Cells = cells };
                ln.Opt = pl.IOpt >= 0 && pl.IOpt < cells.Length ? cells[pl.IOpt].Trim() : "";
                if (pl.IId < cells.Length) ln.IdText = cells[pl.IId].Trim();
                if (pl.IName >= 0 && pl.IName < cells.Length) ln.NameCell = cells[pl.IName].Trim();
                if (pl.IId < cells.Length && int.TryParse(ln.IdText, out ln.Id) && byId.TryGetValue(ln.Id, out var row))
                {
                    // a renamed row means the game changed its ids: never write a value on the wrong object
                    if (ln.NameCell.Length > 0 && !string.Equals(ln.NameCell, (name(row) ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        ln.Renamed = true;
                        ln.RowName = name(row);
                    }
                    else ln.Row = row;
                }
                pl.Lines.Add(ln);
            }
            return pl;
        }

        static void Table<T>(string file, List<T> rows, Func<T, int> id, Func<T, string> name, double scale, HashSet<string> off, Counts c, HashSet<int> touched = null)
        {
            var pl = PlanFor(file, rows, id, name);
            if (pl == null) return;
            if (pl.IId < 0) { Mod.Log.Warning($"[VRAIES STATS] {file} : colonne Id absente"); return; }
            foreach (var col in pl.MissingColumns) Mod.Log.Warning($"[VRAIES STATS] {file} : colonne '{col}' absente de cette version du jeu, ignorée");
            int before = c.Cells, keptBefore = c.Kept;
            // rows whose minimum range already went through the range scale (read from the data or divided once): never divided twice
            var minDone = new HashSet<int>();
            // base values first, then the accepted options, so an option always overrides the base value of the same row
            for (int pass = 0; pass < 2; pass++)
            foreach (var ln in pl.Lines)
            {
                string opt = ln.Opt;
                if ((opt.Length > 0) != (pass == 1)) continue;
                if (opt.Length > 0 && off.Contains(opt))
                {
                    if (_lots.TryGetValue(opt, out var skipped) && skipped.Status != StOff) c.LotLines++; else c.OptionsOff++;
                    continue;
                }
                if (ln.Row == null)
                {
                    if (ln.Renamed)
                    {
                        c.NameMismatch++;
                        if (c.Logged++ < 15) Mod.Log.Warning($"[VRAIES STATS] {file} : Id {ln.Id} s'appelle '{ln.RowName}' et non '{ln.NameCell}' : ligne ignorée");
                    }
                    else
                    {
                        c.UnknownRows++;
                        if (c.Logged++ < 15) Mod.Log.Warning($"[VRAIES STATS] {file} : ligne {ln.IdText} introuvable dans cette base, ignorée");
                    }
                    continue;
                }
                var lot = opt.Length > 0 && _lots.TryGetValue(opt, out var l0) && l0.Status == StOk ? l0 : null;
                object row = ln.Row;
                int rid = ln.Id;
                var cells = ln.Cells;
                string fail = null;
                bool rangeWritten = false, minInCsv = false;
                // every write of a lot line is journaled under that lot (Realism.RestoreLot takes the lot out as a whole); other lines keep the current scope
                using (Realism.Lot(lot != null ? lot.Key : Realism.CurrentLot))
                {
                    for (int i = 0; i < Math.Min(cells.Length, pl.Header.Length); i++)
                    {
                        var p = pl.Props[i];
                        if (p == null) continue;
                        var cell = cells[i].Trim();
                        if (cell.Length == 0) continue;
                        if (p.Name == "MinimalRange") minInCsv = true;
                        object was = lot != null ? SafeGet(p, row) : null;
                        int res = Write(row, p, cell, scale);
                        if (res > 0)
                        {
                            c.Cells++;
                            touched?.Add(rid);
                            if (opt.Length > 0) c.Options[opt] = c.Options.GetValueOrDefault(opt) + 1;
                            if (p.Name == "GroundRange" || p.Name == "LowAltRange" || p.Name == "HighAltRange") rangeWritten = true;
                            Note(row, p, was, lot);
                        }
                        else if (res == 0) c.Kept++;
                        else
                        {
                            c.Rejected++;
                            if (c.Logged++ < 15) Mod.Log.Warning($"[VRAIES STATS] {file} : Id {rid} {p.Name} = '{cell}' refusé");
                            if (lot != null) fail ??= $"{file} : Id {rid} {p.Name} = '{cell}' refusé par le jeu";
                        }
                    }
                    // a new range or a new minimum range must never leave the minimum at or above the ranges (the weapon could not fire)
                    if (rangeWritten || minInCsv)
                    {
                        var pm = Props.Get(row.GetType(), "MinimalRange");
                        object was = lot != null && pm != null ? SafeGet(pm, row) : null;
                        if (FixMinimalRange(row, scale, minInCsv || minDone.Contains(rid)))
                        {
                            c.MinFixed++;
                            // said by name, never only counted: this rule can halve a minimum range the data really states
                            // (a mortar whose minimum is close to its own maximum), and that changes where the gun may stand
                            if (c.MinLogged++ < 40)
                            {
                                var pn = Props.Get(row.GetType(), "Name");
                                string mnm = pn == null ? "?" : Props.Csv(SafeGet(pn, row));
                                Mod.Log.Msg($"[VRAIES STATS] portée minimale ajustée : {file} Id {rid} ({mnm}) {Props.Csv(was)} -> {Props.Csv(SafeGet(pm, row))} m (elle était trop proche de la portée de l'arme)");
                            }
                            Note(row, pm, was, lot);
                            if (lot != null) fail ??= $"{file} : portée minimale de la munition {rid} ajustée (trop proche de sa portée)";
                        }
                        minDone.Add(rid);
                    }
                }
                if (fail != null) WithdrawLot(lot, fail, off);
            }
            Mod.Log.Msg($"[VRAIES STATS] {file} : {c.Cells - before} valeur(s) appliquée(s)" +
                        (c.Kept > keptBefore ? $", {c.Kept - keptBefore} déjà au niveau demandé (inchangée(s))" : ""));
        }

        static object SafeGet(PropertyInfo p, object row)
        {
            try { return p.GetValue(row); } catch { return null; }
        }

        /// Records a write: the last writer of each (object, property), and the undo entry when the line belongs to a lot.
        static void Note(object row, PropertyInfo p, object was, LotState lot)
        {
            if (row is not Il2CppObjectBase o || p == null) return;
            int seq = ++_seq;
            _lastSeq[(o.Pointer, p.Name)] = seq;
            if (lot == null) return;
            lot.Values++;
            lot.Undo.Add(new Rec { Row = row, P = p, Before = was, After = SafeGet(p, row), Seq = seq });
        }

        static void Loads<T>(List<T> rows, HashSet<string> off, Counts c)
        {
            var lines = Read("Chargements.csv", out var header);
            if (header == null) return;
            var hd = header;
            int Col(string n) => Array.FindIndex(hd, h => h.Equals(n, StringComparison.OrdinalIgnoreCase));
            int iOpt = Col("Option"), iU = Col("UnitId"), iW = Col("WeaponId"), iA = Col("AmmunitionId"), iQ = Col("Quantity");
            var pq = Props.Get(typeof(T), "Quantity");
            if (iU < 0 || iW < 0 || iA < 0 || iQ < 0 || pq == null || !pq.CanWrite) { Mod.Log.Warning("[VRAIES STATS] Chargements.csv : format ou colonne Quantity introuvable"); return; }
            // rows grouped by unit; the database holds a few duplicate (unit, weapon, ammo) rows: every match is written
            var byUnit = new Dictionary<int, List<(int wId, int aId, T row)>>();
            foreach (var r in rows)
            {
                var u = Props.Num(r, "UnitId");
                var w = Props.Num(r, "WeaponId");
                var a = Props.Num(r, "AmmunitionId");
                if (!u.HasValue || !w.HasValue || !a.HasValue) continue;
                if (!byUnit.TryGetValue((int)u.Value, out var l)) byUnit[(int)u.Value] = l = new List<(int, int, T)>();
                l.Add(((int)w.Value, (int)a.Value, r));
            }
            int last = Math.Max(iQ, Math.Max(iU, Math.Max(iW, iA)));
            int before = c.Loads;
            for (int pass = 0; pass < 2; pass++)
            foreach (var cells in lines)
            {
                if (cells.Length <= last) { if (pass == 0) c.UnknownRows++; continue; }
                string opt = iOpt >= 0 ? cells[iOpt].Trim() : "";
                if ((opt.Length > 0) != (pass == 1)) continue;
                if (opt.Length > 0 && off.Contains(opt)) { c.OptionsOff++; continue; }
                string wCell = cells[iW].Trim(), aCell = cells[iA].Trim(), q = cells[iQ].Trim();
                bool anyW = wCell == "*", anyA = aCell == "*";
                int wid = 0, aid = 0;
                if (!int.TryParse(cells[iU].Trim(), out var uid) || (!anyW && !int.TryParse(wCell, out wid)) || (!anyA && !int.TryParse(aCell, out aid))) { c.Rejected++; continue; }
                int hits = 0;
                if (byUnit.TryGetValue(uid, out var unitRows))
                    foreach (var (wId, aId, row) in unitRows)
                    {
                        if ((!anyW && wId != wid) || (!anyA && aId != aid)) continue;
                        hits++;
                        int res = Write(row, pq, q, 1);
                        if (res > 0)
                        {
                            c.Loads++;
                            if (q == "0") c.Removed++;
                            if (opt.Length > 0) c.Options[opt] = c.Options.GetValueOrDefault(opt) + 1;
                        }
                        else if (res == 0) c.Kept++;
                        else c.Rejected++;
                    }
                if (hits == 0)
                {
                    c.UnknownRows++;
                    if (c.Logged++ < 15) Mod.Log.Warning($"[VRAIES STATS] Chargements.csv : unité {uid} arme {wCell} munition {aCell} introuvable dans cette base");
                }
            }
            Mod.Log.Msg($"[VRAIES STATS] Chargements.csv : {c.Loads - before} quantité(s) appliquée(s), dont {c.Removed} arme(s) retirée(s)");
        }

        /// A shorter range must never leave the minimum range at or above it (the weapon could not fire at all).
        /// With a range scale, a minimum range the data does not set follows the scale too.
        static bool FixMinimalRange(object row, double scale, bool minInCsv)
        {
            var pm = Props.Get(row.GetType(), "MinimalRange");
            var cur = Props.Num(row, "MinimalRange");
            if (pm == null || !pm.CanWrite || cur == null || cur.Value <= 0) return false;
            double mx = Math.Max(Props.Num(row, "GroundRange") ?? 0, Math.Max(Props.Num(row, "LowAltRange") ?? 0, Props.Num(row, "HighAltRange") ?? 0));
            double mn = scale > 1 && !minInCsv ? cur.Value / scale : cur.Value;
            if (mx > 0 && mn >= 0.9 * mx) mn = 0.5 * mx;
            if (Math.Abs(mn - cur.Value) < 1e-6) return false;
            object boxed = pm.PropertyType == typeof(int) ? (int)Math.Round(mn) : pm.PropertyType == typeof(double) ? mn : (object)(float)mn;
            Realism.SetValueForOverride(row, pm, boxed);
            return true;
        }

        static bool Num(string s, out double v) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        /// Cell syntax: 1200 | 0.7 | True/False | enum names ("Aircraft, Helicopter") or value | x2 (multiplies the current value)
        /// | >=160 (at least: raises a lower value, never lowers a value that is already higher).
        /// Returns 1 when the value was written, 0 when an "at least" cell found the value already high enough (nothing written), -1 when refused.
        static int Write(object row, PropertyInfo p, string cell, double scale)
        {
            try
            {
                var t = p.PropertyType;
                object boxed;
                if (t == typeof(bool))
                {
                    if (!bool.TryParse(cell, out var b)) return -1;
                    boxed = b;
                }
                else if (t.IsEnum)
                {
                    boxed = Enum.Parse(t, cell, true);
                }
                else
                {
                    var cur = NeedsCurrent(cell) ? Props.Num(row, p.Name) : null;
                    if (!Eval(cell, cur, p.Name, scale, out double v, out bool kept)) return -1;
                    if (kept) return 0;
                    if (t == typeof(float)) boxed = (float)v;
                    else if (t == typeof(int)) boxed = (int)Math.Round(v);
                    else if (t == typeof(double)) boxed = v;
                    else return -1;
                }
                Realism.SetValueForOverride(row, p, boxed);
                return 1;
            }
            catch { return -1; }
        }

        /// True when the cell needs the current value (">=" or "x" cells): the game value is only read then.
        static bool NeedsCurrent(string cell) => cell.Length > 0 && (cell[0] == '>' || cell[0] == 'x' || cell[0] == 'X');

        /// Numeric cell value against the current value (same rules as Write). kept: an "at least" cell already satisfied.
        static bool Eval(string cell, double? cur, string prop, double scale, out double v, out bool kept)
        {
            kept = false;
            v = 0;
            if (string.IsNullOrEmpty(cell)) return false;
            bool atLeast = cell.StartsWith(">=", StringComparison.Ordinal);
            if (atLeast)
            {
                if (!Num(cell.Substring(2), out v)) return false;
            }
            else if ((cell[0] == 'x' || cell[0] == 'X') && Num(cell.Substring(1), out var m))
            {
                if (cur == null) return false;
                v = cur.Value * m;
            }
            else if (!Num(cell, out v)) return false;
            if (scale > 1 && RangeFields.Contains(prop) && v > 0 && v < MapCap) v /= scale;
            if (atLeast)
            {
                // the rule is max(current, minimum): a game update that already raised the value keeps its own value
                if (cur == null) return false;
                if (cur.Value >= v - 1e-6) kept = true;
            }
            return true;
        }

        // ------------------------------------------------------------ lot validation (before any write)

        static void PrepareLots(DataBaseSourceData src, double scale, string sourceId, HashSet<string> off)
        {
            _lotSrc = src;
            foreach (var l in _lots.Values) { l.Status = null; l.Reason = null; l.Lines = l.UnitLines = l.Values = 0; l.Gravity = false; l.Undo.Clear(); }
            var suspended = Suspended();
            sourceId ??= SourceIdOf(src);
            string scope = !string.Equals((sourceId ?? "").Trim(), DefaultSource, StringComparison.OrdinalIgnoreCase) ? StOtherDb
                         : Math.Abs(scale - 1) > 1e-6 ? StOtherScale : null;
            foreach (var l in _lots.Values)
                l.Status = off.Contains(l.Key) ? StOff : suspended.Contains(l.Key) ? StSuspended : scope;
            if (scope != null)
            {
                string msg = scope == StOtherDb
                    ? $"[ECHELLE] base '{sourceId ?? "inconnue"}' : lots de l'échelle réelle non appliqués (seule la base Resources default est prise en charge)"
                    : $"[ECHELLE] échelle des portées {scale.ToString("0.##", CultureInfo.InvariantCulture)} : lots de l'échelle réelle non appliqués (échelle 1 seulement)";
                if (msg != _lastScopeLog) { _lastScopeLog = msg; Mod.Log.Msg(msg); }
                return;
            }
            var mun = PlanFor("Munitions.csv", Props.Rows(src.Ammunitions.GetAll()), r => r.Id, r => r.Name);
            var sen = PlanFor("Capteurs.csv", Props.Rows(src.Sensors.GetAll()), r => r.Id, r => r.Name);
            var abi = PlanFor("Capacites.csv", Props.Rows(src.Abilities.GetAll()), r => r.Id, r => r.Name);
            UnitSensorData perUnit = null;
            var live = new Dictionary<(int, string), double?>();      // game values read once for all the lot simulations (no write before the end)
            foreach (var (key, _, _) in LotOrder)
            {
                var lot = _lots[key];
                if (lot.Status != null) continue;
                string why = CheckLotLines(mun, lot) ?? CheckLotLines(sen, lot) ?? CheckLotLines(abi, lot);
                if (why == null && UnitSensorLots.Contains(key))
                {
                    perUnit ??= new UnitSensorData(src);
                    why = perUnit.Check(lot);
                }
                if (why == null && lot.Lines == 0 && lot.UnitLines == 0) { lot.Status = StAbsent; continue; }
                if (why == null && lot.DependsOn != null && _lots.TryGetValue(lot.DependsOn, out var dep) && dep.Status != StOk)
                    why = $"dépend du lot {dep.Tag} ({dep.Status})";
                if (why == null && HasLines(mun, key)) why = SimulateAmmo(mun, lot, off, scale, src, live);
                if (why == null && key == LotBalistique) why = GravityCheck();
                if (why == null) { lot.Status = StOk; continue; }
                lot.Status = StRefused;
                lot.Reason = why;
                Mod.Log.Warning($"[ECHELLE] lot {key} ({lot.Tag}) refusé : {why} ({lot.Lines + lot.UnitLines} ligne(s), aucune valeur écrite)");
            }
        }

        static bool HasLines(Plan pl, string key) => pl != null && pl.Lines.Any(l => l.Opt.Equals(key, StringComparison.OrdinalIgnoreCase));

        static HashSet<string> Suspended()
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var extra = LotsSuspendusSource?.Invoke();
                if (extra != null) foreach (var k in extra) if (!string.IsNullOrWhiteSpace(k)) s.Add(k.Trim());
            }
            catch { }
            return s;
        }

        static string SourceIdOf(DataBaseSourceData src)
        {
            try
            {
                var db = DataBaseService._instance;
                if (db != null && src != null && db.RawAccess != null && db.RawAccess.Pointer == src.Pointer) return db.CurrentSourceId;
            }
            catch { }
            return null;
        }

        /// V1-V3 on the lines of one lot in one table: rows exist with their names, columns exist, numbers within bounds, no duplicate cell.
        static string CheckLotLines(Plan pl, LotState lot)
        {
            if (pl == null) return null;
            var seen = new HashSet<(int, string)>();
            foreach (var ln in pl.Lines)
            {
                if (!ln.Opt.Equals(lot.Key, StringComparison.OrdinalIgnoreCase)) continue;
                lot.Lines++;
                if (ln.Row == null)
                    return ln.Renamed ? $"{pl.File} : Id {ln.Id} s'appelle '{ln.RowName}' et non '{ln.NameCell}'" : $"{pl.File} : ligne {ln.IdText} introuvable dans cette base";
                int values = 0;
                for (int i = 0; i < ln.Cells.Length; i++)
                {
                    if (i == pl.IOpt || i == pl.IId || i == pl.IName) continue;
                    var cell = ln.Cells[i].Trim();
                    if (cell.Length == 0) continue;
                    if (i >= pl.Header.Length) return $"{pl.File} : Id {ln.Id} : valeur hors des colonnes";
                    var p = pl.Props[i];
                    if (p == null) return $"{pl.File} : colonne '{pl.Header[i]}' absente de cette version du jeu";
                    if (!Bounds.TryGetValue(p.Name, out var b)) return $"{pl.File} : colonne '{p.Name}' non prévue pour un lot";
                    if (!Num(cell, out var v) || double.IsNaN(v) || double.IsInfinity(v)) return $"{pl.File} : Id {ln.Id} {p.Name} = '{cell}' illisible";
                    if (v > b.Max || v < b.Min || (!b.MinIn && v <= b.Min)) return $"{pl.File} : Id {ln.Id} {p.Name} = {cell} hors limites";
                    if (!seen.Add((ln.Id, p.Name))) return $"{pl.File} : Id {ln.Id} {p.Name} en double dans le lot";
                    values++;
                }
                if (values == 0) return $"{pl.File} : Id {ln.Id} sans valeur";
            }
            return null;
        }

        /// V4 on the merged ammunition state (game values, base lines, accepted options and this lot, in file order, with the minimum range
        /// rule replayed per line): no minimum range of a row written by this lot may reach 90 % of its range. Ballistics lot: the direct-fire
        /// rows must keep a firing solution under real gravity.
        static string SimulateAmmo(Plan pl, LotState lot, HashSet<string> off, double scale, DataBaseSourceData src, Dictionary<(int, string), double?> live)
        {
            var shadow = new Dictionary<(int, string), double>();
            double? Get(Line ln, string prop)
            {
                if (shadow.TryGetValue((ln.Id, prop), out var v)) return v;
                if (!live.TryGetValue((ln.Id, prop), out var g)) live[(ln.Id, prop)] = g = Props.Num(ln.Row, prop);
                return g;
            }
            var lotRows = new HashSet<int>();
            var minDone = new HashSet<int>();
            for (int pass = 0; pass < 2; pass++)
            foreach (var ln in pl.Lines)
            {
                string opt = ln.Opt;
                if ((opt.Length > 0) != (pass == 1) || ln.Row == null) continue;
                bool mine = false;
                if (opt.Length > 0)
                {
                    if (off.Contains(opt)) continue;
                    if (_lots.TryGetValue(opt, out var other))
                    {
                        mine = other == lot;
                        if (!mine && other.Status != StOk) continue;
                    }
                }
                bool rangeWritten = false, minInCsv = false;
                for (int i = 0; i < Math.Min(ln.Cells.Length, pl.Header.Length); i++)
                {
                    var p = pl.Props[i];
                    if (p == null) continue;
                    var cell = ln.Cells[i].Trim();
                    if (cell.Length == 0) continue;
                    var t = p.PropertyType;
                    if (t != typeof(float) && t != typeof(int) && t != typeof(double)) continue;
                    if (p.Name == "MinimalRange") minInCsv = true;
                    if (!Eval(cell, NeedsCurrent(cell) ? Get(ln, p.Name) : null, p.Name, scale, out double v, out bool kept) || kept) continue;
                    shadow[(ln.Id, p.Name)] = t == typeof(int) ? Math.Round(v) : t == typeof(float) ? (float)v : v;
                    if (mine) lotRows.Add(ln.Id);
                    if (p.Name == "GroundRange" || p.Name == "LowAltRange" || p.Name == "HighAltRange") rangeWritten = true;
                }
                if (!rangeWritten && !minInCsv) continue;
                double cur = Get(ln, "MinimalRange") ?? 0;
                if (cur > 0)
                {
                    double mx = Math.Max(Get(ln, "GroundRange") ?? 0, Math.Max(Get(ln, "LowAltRange") ?? 0, Get(ln, "HighAltRange") ?? 0));
                    double mn = scale > 1 && !(minInCsv || minDone.Contains(ln.Id)) ? cur / scale : cur;
                    if (mx > 0 && mn >= 0.9 * mx)
                    {
                        if (lotRows.Contains(ln.Id))
                            return $"portée minimale de la munition {ln.Id} ({mn.ToString("0", CultureInfo.InvariantCulture)} m) à 90 % ou plus de sa portée ({mx.ToString("0", CultureInfo.InvariantCulture)} m)";
                        mn = 0.5 * mx;
                    }
                    shadow[(ln.Id, "MinimalRange")] = mn;
                }
                minDone.Add(ln.Id);
            }
            if (lot.Key != LotBalistique) return null;
            int bad = 0;
            var ids = new List<int>();
            foreach (var a in Props.Rows(src.Ammunitions.GetAll()))
            {
                if (a == null || a.TrajectoryType != TrajectoryKind.DirectShot) continue;
                double S(string prop, double game) => shadow.TryGetValue((a.Id, prop), out var v) ? v : game;
                double speed = S("MuzzleVelocity", a.MuzzleVelocity);
                double range = Math.Max(S("GroundRange", a.GroundRange), Math.Max(S("LowAltRange", a.LowAltRange), S("HighAltRange", a.HighAltRange)));
                if (speed <= 0 || range <= 0) continue;
                if (RealGravity * range / (speed * speed) > 1)
                {
                    bad++;
                    if (ids.Count < 8) ids.Add(a.Id);
                }
            }
            return bad > MaxUnreachableUnderLotB
                ? $"{bad} munition(s) en tir direct sans solution de tir sous la pesanteur réelle ({string.Join(", ", ids)})"
                : null;
        }

        static string GravityCheck()
        {
            try
            {
                var bs = GameCfg.Instance?.BattleSystemSettings;
                if (bs == null) return "réglages de balistique du jeu introuvables (pesanteur)";
                var p = Props.Get(bs.GetType(), "G");
                if (p == null || !p.CanWrite) return "pesanteur non modifiable dans cette version du jeu";
                float g = bs.G;
                if (float.IsNaN(g) || g <= 0f) return $"pesanteur illisible ({g})";
                return null;
            }
            catch (Exception e) { return "pesanteur illisible : " + e.Message; }
        }

        /// Ballistics lot: the real gravity, journaled like the rest and recorded in the lot (put back with its muzzle velocities).
        static void ApplyGravity()
        {
            if (!_lots.TryGetValue(LotBalistique, out var lot) || lot.Status != StOk) return;
            try
            {
                var bs = GameCfg.Instance?.BattleSystemSettings;
                if (bs == null) throw new InvalidOperationException("réglages de balistique introuvables");
                var p = Props.Get(bs.GetType(), "G");
                if (p == null || !p.CanWrite) throw new InvalidOperationException("pesanteur non modifiable");
                float before = bs.G;
                if (Math.Abs(before - RealGravity) > 0.005f)
                {
                    using (Realism.Lot(LotBalistique)) Realism.SetValueForOverride(bs, p, RealGravity);
                    Note(bs, p, before, lot);
                }
                float after = bs.G;
                if (Math.Abs(after - RealGravity) > 0.005f) throw new InvalidOperationException($"relue à {after.ToString("0.##", CultureInfo.InvariantCulture)}");
                lot.Gravity = true;
                Mod.Log.Msg($"[ECHELLE] pesanteur : {before.ToString("0.##", CultureInfo.InvariantCulture)} -> {after.ToString("0.##", CultureInfo.InvariantCulture)} m/s² (lot {LotBalistique}, avec les vitesses réelles)");
            }
            catch (Exception e) { WithdrawLot(lot, "pesanteur non écrite : " + e.Message, _offWrite); }
        }

        /// Puts a lot back (lots validated on top of it first). Returns the number of values put back.
        static int WithdrawLot(LotState lot, string reason, HashSet<string> off)
        {
            if (lot == null || lot.Status != StOk) return 0;
            int n = 0;
            foreach (var dep in _lots.Values.ToList())
                if (dep.Status == StOk && string.Equals(dep.DependsOn, lot.Key, StringComparison.OrdinalIgnoreCase))
                    n += WithdrawLot(dep, $"le lot {lot.Tag} dont il dépend est retiré", off);
            int back = UndoLot(lot, reason);
            n += back;
            lot.Status = StWithdrawn;
            lot.Reason = reason;
            lot.Gravity = false;
            off?.Add(lot.Key);
            Mod.Log.Warning($"[ECHELLE] lot {lot.Key} ({lot.Tag}) retiré : {reason} ({back} valeur(s) remise(s))");
            return n;
        }

        /// The journal takes the lot out (every value its lines wrote goes back to the value the lot found; a value a later line replaced
        /// keeps that line's value), then the minimum range rule runs again on the ammunition rows it touched.
        static int UndoLot(LotState lot, string reason)
        {
            var ammo = new List<object>();
            foreach (var r in lot.Undo) if (r.Row is Ammunitions && !ammo.Contains(r.Row)) ammo.Add(r.Row);
            int n = 0;
            try { n = Realism.RestoreLot(lot.Key, reason); }
            catch (Exception e) { Mod.Log.Warning($"[ECHELLE] lot {lot.Key} : retrait du journal impossible : {e.Message}"); }
            lot.Undo.Clear();
            // ranges put back: the minimum range rule again, so every weapon can still fire
            foreach (var row in ammo) { try { FixMinimalRange(row, 1, true); } catch { } }
            return n;
        }

        /// Read-back of up to 10 values per accepted lot (values no later line overwrote); a mismatch puts the lot back.
        static void ReadBackLots()
        {
            foreach (var (key, _, _) in LotOrder)
            {
                var lot = _lots[key];
                if (lot.Status != StOk || lot.Undo.Count == 0) continue;
                string why = null;
                int step = Math.Max(1, lot.Undo.Count / 10), done = 0;
                for (int i = 0; i < lot.Undo.Count && done < 10 && why == null; i += step)
                {
                    var r = lot.Undo[i];
                    if (r.Row is not Il2CppObjectBase o || !_lastSeq.TryGetValue((o.Pointer, r.P.Name), out var last) || last != r.Seq) continue;
                    done++;
                    object now;
                    try { now = r.P.GetValue(r.Row); }
                    catch (Exception e) { why = "relecture impossible : " + e.Message; break; }
                    if (!Equals(now, r.After)) why = $"relecture : {r.Row.GetType().Name} {IdOf(r.Row)} {r.P.Name} = {now} au lieu de {r.After}";
                }
                if (why != null) WithdrawLot(lot, why, _offWrite);
            }
        }

        static string IdOf(object row)
        {
            try { return Convert.ToString(Props.Get(row.GetType(), "Id")?.GetValue(row), CultureInfo.InvariantCulture) ?? "?"; } catch { return "?"; }
        }

        /// Low-altitude range of the anti-helicopter, low-flight and manual-aim option rows kept at or below the ground range
        /// (a ground range lot left out or put back must never leave a longer range against helicopters). Returns the rows lowered.
        static int ClampLowAlt()
        {
            if (!_plans.TryGetValue("Munitions.csv", out var pl) || pl == null) return 0;
            int n = 0;
            var ids = new List<int>();
            var done = new HashSet<int>();
            foreach (var ln in pl.Lines)
            {
                if (ln.Row == null || !LowAltOptions.Contains(ln.Opt) || (_offWrite != null && _offWrite.Contains(ln.Opt)) || !done.Add(ln.Id)) continue;
                var g = Props.Num(ln.Row, "GroundRange");
                var l = Props.Num(ln.Row, "LowAltRange");
                if (g == null || l == null || g.Value <= 0 || l.Value <= g.Value + 1e-3) continue;
                var p = Props.Get(ln.Row.GetType(), "LowAltRange");
                if (p == null || !p.CanWrite) continue;
                object boxed = p.PropertyType == typeof(int) ? (int)Math.Round(g.Value) : p.PropertyType == typeof(double) ? g.Value : (object)(float)g.Value;
                Realism.SetValueForOverride(ln.Row, p, boxed);
                n++;
                if (ids.Count < 12) ids.Add(ln.Id);
            }
            if (n > 0)
            {
                string line = $"[ECHELLE] portée contre hélicoptères ramenée à la portée au sol : {n} munition(s) ({string.Join(", ", ids)})";
                if (n > ClampWarnAbove && LotAccepte(LotPortees)) Mod.Log.Warning(line + " : plus que prévu avec le lot des portées réelles");
                else Mod.Log.Msg(line);
            }
            return n;
        }

        /// Per-unit sensor lines (CapteursParUnite.csv, written by the per-unit copies module): unit and name, (unit, sensor) pair used by
        /// the unit or one of its options, optics within bounds, no duplicate key in the file.
        sealed class UnitSensorData
        {
            readonly Dictionary<int, string> _names = new();
            readonly HashSet<(int, int)> _pairs = new();
            readonly Dictionary<(int, int), int> _keys = new();
            readonly List<string[]> _lines;
            readonly int _iOpt, _iU, _iS, _iN;
            readonly int[] _iV;

            internal UnitSensorData(DataBaseSourceData src)
            {
                foreach (var u in Props.Rows(src.Units.GetAll())) if (u != null) _names[u.Id] = u.Name ?? "";
                foreach (var su in Props.Rows(src.SensorUnits.GetAll())) if (su != null) _pairs.Add((su.UnitId, su.SensorId));
                var modUnit = new Dictionary<int, int>();
                foreach (var m in Props.Rows(src.Modifications.GetAll())) if (m != null) modUnit[m.Id] = m.UnitId;
                foreach (var o in Props.Rows(src.Options.GetAll()))
                {
                    if (o == null || !modUnit.TryGetValue(o.ModificationId, out var uid)) continue;
                    if (o.MainSensorId > 0) _pairs.Add((uid, o.MainSensorId));
                    if (o.ExtraSensorId > 0) _pairs.Add((uid, o.ExtraSensorId));
                }
                _lines = Read("CapteursParUnite.csv", out var h);
                h ??= Array.Empty<string>();
                int Col(string n) => Array.FindIndex(h, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                _iOpt = Col("Option"); _iU = Col("UnitId"); _iS = Col("SensorId"); _iN = Col("Name");
                _iV = new[] { Col("OpticsGround"), Col("OpticsLowAltitude"), Col("OpticsHighAltitude") };
                foreach (var cells in _lines)
                    if (TryKey(cells, out var k)) _keys[k] = _keys.GetValueOrDefault(k) + 1;
            }

            bool TryKey(string[] cells, out (int, int) key)
            {
                key = default;
                if (_iU < 0 || _iS < 0 || _iU >= cells.Length || _iS >= cells.Length) return false;
                if (!int.TryParse(cells[_iU].Trim(), out var u) || !int.TryParse(cells[_iS].Trim(), out var s)) return false;
                key = (u, s);
                return true;
            }

            internal string Check(LotState lot)
            {
                const string F = "CapteursParUnite.csv";
                if (_iOpt < 0 || _iU < 0 || _iS < 0 || _iV.Any(i => i < 0)) return $"{F} : colonnes introuvables";
                foreach (var cells in _lines)
                {
                    if (_iOpt >= cells.Length || !cells[_iOpt].Trim().Equals(lot.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    lot.UnitLines++;
                    if (!TryKey(cells, out var key)) return $"{F} : identifiant illisible";
                    if (!_names.TryGetValue(key.Item1, out var name)) return $"{F} : unité {key.Item1} introuvable dans cette base";
                    string want = _iN >= 0 && _iN < cells.Length ? cells[_iN].Trim() : "";
                    if (!string.Equals(want, name.Trim(), StringComparison.OrdinalIgnoreCase)) return $"{F} : unité {key.Item1} s'appelle '{name.Trim()}' et non '{want}'";
                    if (!_pairs.Contains(key)) return $"{F} : l'unité {key.Item1} n'utilise pas le capteur {key.Item2}";
                    if (_keys.GetValueOrDefault(key) > 1) return $"{F} : unité {key.Item1} capteur {key.Item2} en double";
                    int values = 0;
                    foreach (int i in _iV)
                    {
                        var cell = i < cells.Length ? cells[i].Trim() : "";
                        if (cell.Length == 0) continue;
                        if (!Num(cell, out var v) || double.IsNaN(v) || v <= 0 || v > MapCap) return $"{F} : unité {key.Item1} capteur {key.Item2} valeur '{cell}' hors limites";
                        values++;
                    }
                    if (values == 0) return $"{F} : unité {key.Item1} capteur {key.Item2} sans valeur";
                }
                return null;
            }
        }

        static string LotSummary()
        {
            var sb = new StringBuilder();
            foreach (var (key, tag, _) in LotOrder)
            {
                var l = _lots[key];
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(tag).Append('=');
                if (l.Status == StOk)
                {
                    sb.Append("ok(").Append(l.Values).Append(" valeurs");
                    if (l.UnitLines > 0) sb.Append(", ").Append(l.UnitLines).Append(" lignes par unité");
                    if (l.Gravity) sb.Append(", pesanteur ").Append(RealGravity.ToString("0.##", CultureInfo.InvariantCulture));
                    sb.Append(')');
                }
                else if (l.Status == StRefused || l.Status == StWithdrawn)
                {
                    var r = l.Reason ?? "";
                    if (r.Length > 90) r = r.Substring(0, 90) + "…";
                    sb.Append(l.Status).Append('(').Append(r).Append(')');
                }
                else sb.Append(l.Status ?? StNotApplied);
            }
            sb.Append(" E=non inclus (mesure préalable)");
            return sb.ToString();
        }

        /// Warning only: direct-fire rows without a firing solution at the current gravity, and rows whose low-arc angle is above the
        /// highest elevation of every weapon firing them.
        static string CheckReach(DataBaseSourceData src)
        {
            double g = 70;
            try { var bs = GameCfg.Instance?.BattleSystemSettings; if (bs != null && bs.G > 0f) g = bs.G; } catch { }
            var maxAngle = new Dictionary<int, int>();
            foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
            {
                if (wa == null || !src.Weapons.TryGetById(wa.WeaponId, out var w) || w == null || w.UpperVerticalAngles <= 0) continue;
                maxAngle[wa.AmmunitionId] = Math.Max(maxAngle.GetValueOrDefault(wa.AmmunitionId), w.UpperVerticalAngles);
            }
            int none = 0, angle = 0;
            var noneIds = new List<int>();
            var angleIds = new List<int>();
            foreach (var a in Props.Rows(src.Ammunitions.GetAll()))
            {
                if (a == null || a.TrajectoryType != TrajectoryKind.DirectShot) continue;
                double v = a.MuzzleVelocity, r = Math.Max(a.GroundRange, Math.Max(a.LowAltRange, a.HighAltRange));
                if (v <= 0 || r <= 0) continue;
                double k = g * r / (v * v);
                if (k > 1)
                {
                    none++;
                    if (noneIds.Count < 8) noneIds.Add(a.Id);
                    continue;
                }
                double deg = 0.5 * Math.Asin(k) * 180 / Math.PI;
                if (maxAngle.TryGetValue(a.Id, out var up) && deg > up)
                {
                    angle++;
                    if (angleIds.Count < 8) angleIds.Add(a.Id);
                }
            }
            return $"pesanteur {g.ToString("0.##", CultureInfo.InvariantCulture)}, sans solution {none}" + (noneIds.Count > 0 ? $" ({string.Join(", ", noneIds)})" : "") +
                   $", angle {angle}" + (angleIds.Count > 0 ? $" ({string.Join(", ", angleIds)})" : "");
        }

        /// Logs missiles whose new range looks out of reach for the engine (burn time x speed, seeker distance): what to test first.
        static void CheckKinematics(DataBaseSourceData src, HashSet<int> ids)
        {
            int slow = 0, blind = 0;
            var names = new List<string>();
            foreach (var id in ids)
            {
                if (!src.Ammunitions.TryGetById(id, out var a) || a == null) continue;
                double range = Math.Max(a.GroundRange, Math.Max(a.LowAltRange, a.HighAltRange));
                if (range <= 0) continue;
                if (a.MaxSpeed > 0 && a.BurnTime > 0 && a.BurnTime * a.MaxSpeed < range * 0.9)
                {
                    slow++;
                    if (names.Count < 8) names.Add($"{a.Name} ({range:0} m, vol {a.BurnTime * a.MaxSpeed:0} m)");
                }
                int seeker = Convert.ToInt32(a.Seeker);
                if ((seeker == 100 || seeker == 200) && a.MaxSeekerDistance > 0 && a.MaxSeekerDistance < range * 0.9) blind++;
            }
            Mod.Log.Msg($"[VRAIES STATS] contrôle : {ids.Count} munitions modifiées, {slow} missile(s) au vol peut-être trop court, {blind} autodirecteur(s) plus court(s) que la portée" +
                        (names.Count > 0 ? " : " + string.Join(", ", names) : ""));
        }

        /// Caps the number of units per card (per veterancy level) to the realistic limits of Disponibilite.csv. Never raises a value.
        internal static bool LimitAvailability(SpecializationAvailabilities a)
        {
            // called while the CAMPAGNE divisions are built: an error here must never cancel the divisions
            try { return LimitAvailabilityCore(a); }
            catch (Exception e)
            {
                if (!_availError) Mod.Log.Warning("[VRAIES STATS] limites d'exemplaires ignorées : " + e.Message);
                _availError = true;
                return false;
            }
        }
        static bool _availError;

        /// Largest number of units per card allowed for this unit (used to trim cards of decks saved before the limits).
        internal static bool MaxPerCard(int unitId, out int max)
        {
            max = 0;
            try
            {
                if (!Realism.RealModeOn) return false;
                if (_avail == null) LimitAvailabilityCore(null);
                if (_avail == null || !_avail.TryGetValue(unitId, out var lim)) return false;
                max = lim.Max();
                return max > 0;
            }
            catch { return false; }
        }
        internal static readonly HashSet<int> ClampLogged = new();

        static bool LimitAvailabilityCore(SpecializationAvailabilities a)
        {
            if (!Realism.RealModeOn) return false;
            if (_avail == null)
            {
                _avail = new Dictionary<int, int[]>();
                var lines = Read("Disponibilite.csv", out _);
                foreach (var cells in lines)
                {
                    if (cells.Length < 6 || !int.TryParse(cells[0].Trim(), out var uid)) continue;
                    var xp = new int[4];
                    bool ok = true;
                    for (int i = 0; i < 4; i++) ok &= int.TryParse(cells[2 + i].Trim(), out xp[i]);
                    if (ok) _avail[uid] = xp;
                }
                Mod.Log.Msg($"[VRAIES STATS] Disponibilite.csv : {_avail.Count} limite(s) d'exemplaires chargée(s)");
            }
            if (a == null) return false;
            if (!_avail.TryGetValue(a.UnitId, out var lim)) return false;
            a.MaxAvailabilityXp0 = Math.Min(a.MaxAvailabilityXp0, lim[0]);
            a.MaxAvailabilityXp1 = Math.Min(a.MaxAvailabilityXp1, lim[1]);
            a.MaxAvailabilityXp2 = Math.Min(a.MaxAvailabilityXp2, lim[2]);
            a.MaxAvailabilityXp3 = Math.Min(a.MaxAvailabilityXp3, lim[3]);
            return true;
        }
    }
}
