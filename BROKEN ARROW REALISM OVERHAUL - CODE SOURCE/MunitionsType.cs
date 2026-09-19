// MunitionsType: "les munitions comptées par type", and the "first shot less accurate" request (19/09/2026).
// This module WRITES NOTHING into the game. It measures, it reports, and it refuses the second feature. Here is why.
//
// ---------------------------------------------------------------------------------------------------------------------------
// 1. AMMUNITION BY TYPE - the game already does it, and the mod already feeds it. Nothing to build.
//
//    Checked against the dumps and against the game's own exported tables before a line was written here:
//    - WeaponAmmunitions holds ONE ROW PER (UnitId, WeaponId, AmmunitionId), each with its own Int32 Quantity (set_Quantity exists).
//      2357 rows in 'Resources default'. Example read from the game's own export: unit 245 "M1A2 Abrams", weapon 342 "MainGun 120
//      M256", ammunition 256 "Round 120 M256 AP" Quantity=16 and ammunition 253 "Round 120 M256 HEAT" Quantity=24. Sixteen
//      armour-piercing and twenty-four high-explosive, in two separate rows. That is the feature, already in the data.
//    - LoadUnits.LoadAmmunitions(Weapons, Int32 unitId) copies those rows onto the per-unit clone into
//      Weapons.WeaponAmmunitions (Dictionary`2<Int64,Int32>), value = the row's Quantity. CopiesUnites.SyncCounts already writes the
//      mod's quantities into that dictionary and already logs which id the key is; the live log of the installed build confirms the
//      whole path runs ("copies faites par le jeu (fiche arsenal) : 862 contrôle(s) ... 0 quantité(s) recopiée(s)", i.e. the copies
//      already matched the values written on the table rows).
//    - At spawn UnitBuilder.SetupWeaponComponents builds an AmmunitionBoxComponent whose AmmunitionBox is a
//      Dictionary`2<Int32, AmmunitionContainer> KEYED BY SHELL ID, filled through AmmunitionBoxComponent.Add(Ammunitions,
//      Int32 addMaxQuantity, Int32 addAmount) and AmmunitionContainer.AddInitialWeaponAmmo(Int32 maxAmount, Int32 amount).
//      Every shell type gets its OWN container, with its own AmmoQuantity, MaxAmmoQuantity, AmmoReserved, ReduceAmmo, AddAmmo,
//      OverrideMaxAmmo and AmmunitionInfo. BattleSystemHelpers.GetUnitAmunitionBox(unit, shellID) returns one of them by shell id,
//      and ReserveAmmoForWeapon(weapon, priorityAmmoID, ammoBox) reserves from the one the engagement needs.
//      So a tank does NOT carry one undifferentiated pile: it carries one pile per shell, and the engine empties them separately.
//    - And the separation bites where it matters: the 45 main-gun armour-piercing rows have ArmorTargeted=Kinetic and
//      TargetType = Vehicle|Ship ONLY, while their HE/HEAT stablemates carry Ground|Infantry too. (Kinetic alone does NOT mean
//      "tank dart": of the 145 Kinetic rows of this build, 99 are machine-gun and autocannon rounds carrying
//      Ground|Infantry|Vehicle|Helicopter|Ship. That is why the split below reads TargetType and not only ArmorTargeted.)
//      Penetration read from the game's own table, T-90M: AP 450, HE 125. When the AP pile is empty the tank is out of
//      anti-tank rounds and has to shoot the 125-penetration shell at the tank, exactly as the author described. Nothing to add.
//    - "Quantity is the maxAmmo passed at spawn" was recorded as plausible, not proven, in review/extra_ideas.md. It is now proven
//      as far as the dumps can prove it (row Quantity -> Weapons.WeaponAmmunitions value -> AmmunitionBox per shell) and the note
//      itself is out of date on the other point: the mod DOES write Quantity today, through reel/Chargements.csv and
//      RealStats.Loads, 315 quantities per database load in the installed build's log. This module does not write a second time.
//
//    WHAT THIS MODULE ADDS: an audit, because the measurement turned up a real defect in the loadout table that IS ours.
//    reel/Chargements.csv cuts Russian main-gun ammunition and leaves the NATO main guns alone:
//      T-72B / T-72B3 / T-90 / T-90M : 45 rounds in the game -> 22 (the autoloader carousel alone)
//      T-80BV / T-80U                : 45 -> 28 (the same, T-80 carousel)
//      M1A1 / M1A2 / Leopard 2A7/A8  : 40 and 42 -> untouched
//    Counted over the whole file: 75 Russian main-gun rows changed (0 raised, 59 lowered) against 3 NATO main-gun rows.
//    The machine guns go the other way but only for part of the Russian fleet: the Abrams' M240 goes 1140 -> 10400 and its M2
//    200 -> 900 (the real M1 figures), while the T-90's PKT stays at 400 and its Kord at 60 (real: about 1250 and 300).
//    The author's rule is "les deux camps". So this module measures what the player actually gets, per side, and says so in the
//    log. It does not correct it: reel/Chargements.csv and RealStats.cs are not this file's to edit.
//
// ---------------------------------------------------------------------------------------------------------------------------
// 2. FIRST SHOT LESS ACCURATE - REFUSED, cleanly, and the measurement that refuses it is logged.
//
//    - The game has NO notion of a first shot, of a ranging shot, or of a target that has just changed. A full symbol search over
//      the dumps finds no FirstShot, Ranging, Zeroing, TargetChanged, PreviousTarget, LastTarget, SameTarget or ShotsOnTarget
//      anywhere in the game. What it does have is an aiming delay before the first shot of an engagement: Weapons.AimTimeMin /
//      AimTimeMax, drawn per engagement by WeaponComponent.GenerateRandomAimTime. Measured on this build: 39 of the 40 MainGun
//      weapons are 1.5 s - 2.5 s, the last one 2 s - 3 s. The game already makes the crew lay the gun; it does it in TIME, not in
//      accuracy, and that part is already there.
//    - Accuracy of a shell is dispersion, and dispersion lives on the AMMUNITION ROW, shared by every unit of every side firing
//      that shell: Ammunitions.DispersionHorizontalRadius / DispersionVerticalRadius / DispersionMinimal. Measured: every 120 mm
//      and 125 mm tank round in the database has exactly H=1.8, V=1.5, min=0. One row, one value, no per-shot and no per-crew slot.
//    - The two functions that actually apply dispersion to a shot are
//        ShootingSystem.AddBallisticDeviation(Vector3 initialVector, Single maxVertical, Single maxHorizontal, Single effectiveRange,
//                                             RandomComponent& randomComponent)
//        ShootingSystem.AddTrajectoryDeviation(Vector3& vectorToTarget, TrajectoryStruct& trajectory, Single maxVertical,
//                                             Single maxHorizontal, Single effectiveRange, RandomComponent& randomComponent)
//      Neither of them receives the weapon, the shooter or the target. They are geometry plus the random draw. Even hooked, the mod
//      could not tell a first shot from a tenth; it could only blur EVERY shot by the same amount, which is a different feature and
//      a worse one. And both are forbidden here anyway: RandomComponent holds a FastList`1<Single> and TrajectoryStruct holds an
//      Il2CppStructArray`1<Single>, so both are non-blittable IL2CPP structs taken BY REFERENCE - the exact hook shape that cost a
//      player his campaign on 18/09/2026. ShootingSystem.ShotCycleAndReload takes AmmunitionBoxComponent& for the same reason.
//    - The only per-weapon state that could stand in for "this engagement just started" is on the ECS component itself
//      (WeaponComponent.ShotsMade, WeaponComponent.TimeWithoutTarget). Reading it means Get<T> on an ECS component, which this
//      codebase forbids outright. So there is no honest way in, and a fake first-shot penalty is worse than none.
//    The module logs that measurement once, in French, and changes nothing. The ammunition work above is the valuable half and it
//    turns out to be already done by the game.
//
// ---------------------------------------------------------------------------------------------------------------------------
// HOW IT BEHAVES
//  - NO Harmony patch, NO detour, NO hook of any kind, and NOT ONE WRITE into the game. Nothing to journal, nothing to restore,
//    nothing to switch back to vanilla: with this file removed the game behaves identically.
//  - Runs once per database source id, right after the real stats are applied, so it measures the state the player really gets.
//  - Own error counter: 20 errors and the module stops for the rest of the session. Every step is wrapped on its own, so one
//    missing column can never take down the summary or anything else.
//  - Reference loadouts below are published open figures (army manuals and manufacturer data), NOT measured in the game. They are
//    used to say how far the shipped value is from the real one, never to write anything.
//
// WIRING (this file cannot do it itself, it owns no other file): call MunitionsType.Mesure(src, db.CurrentSourceId) from Mod.cs
// right after the RealStats.Apply line, and append the returned string to the [REALISME] summary. Without that call the module is
// inert and costs nothing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.DataBase.Models;
using AmmoTarget = Il2CppBrokenArrow.DataBase.Enums.TargetType;
using ArmorKind = Il2CppBrokenArrow.DataBase.Enums.ArmorType;
using WeaponKind = Il2CppBrokenArrow.DataBase.Enums.WeaponType;

namespace RealismOverhaul
{
    static class MunitionsType
    {
        const string Tag = "[MUNITIONS-TYPE]";
        const int MaxErreurs = 20;
        const int EchantillonParCamp = 6;         // sample lines per side, so neither side gets more attention than the other
        const double EcartCampAlerte = 0.15;      // a 15-point gap between the two sides' loadout ratios is reported as one-sided

        static readonly HashSet<string> _faits = new(StringComparer.OrdinalIgnoreCase);
        static int _erreurs;
        static bool _off;

        /// Real main-gun rounds carried, published open figures. Order matters: the most specific name fragment must come first.
        /// Detail says how the real figure splits, so the log can tell a carousel figure from a full stowage figure.
        sealed class Reel
        {
            public string Nom;       // unit name fragment, matched without case
            public int Obus;         // rounds carried in total
            public int Pret;         // rounds ready to fire without the crew restowing (carousel or ready rack), 0 when not relevant
            public string Detail;
        }

        static readonly Reel[] References =
        {
            // Russia: the carousel is the ready rack, the rest is stowed in the hull and has to be restowed by hand.
            new Reel { Nom = "T-90M",     Obus = 40, Pret = 22, Detail = "22 au carrousel + 18 en caisse" },
            new Reel { Nom = "T-90A",     Obus = 43, Pret = 22, Detail = "22 au carrousel + 21 en caisse" },
            new Reel { Nom = "T-90",      Obus = 43, Pret = 22, Detail = "22 au carrousel + 21 en caisse" },
            new Reel { Nom = "T-80",      Obus = 45, Pret = 28, Detail = "28 au carrousel + 17 en caisse" },
            new Reel { Nom = "T-72",      Obus = 45, Pret = 22, Detail = "22 au carrousel + 23 en caisse" },
            new Reel { Nom = "T-64",      Obus = 37, Pret = 28, Detail = "28 au carrousel + 9 en caisse" },
            new Reel { Nom = "T-14",      Obus = 45, Pret = 32, Detail = "32 au chargeur automatique + 13 en caisse" },
            // NATO: the bustle is the ready rack behind the blast doors, the rest is hull stowage.
            new Reel { Nom = "M1A2",      Obus = 42, Pret = 34, Detail = "34 en soute de tourelle + 8 en caisse" },
            new Reel { Nom = "M1A1",      Obus = 40, Pret = 34, Detail = "34 en soute de tourelle + 6 en caisse" },
            new Reel { Nom = "M60",       Obus = 63, Pret = 0,  Detail = "63 obus, pas de soute séparée" },
            new Reel { Nom = "Leopard 2", Obus = 42, Pret = 15, Detail = "15 en soute de tourelle + 27 en caisse" },
            new Reel { Nom = "Challenger",Obus = 50, Pret = 0,  Detail = "50 projectiles, munition en deux parties" },
            new Reel { Nom = "Leclerc",   Obus = 40, Pret = 22, Detail = "22 au chargeur automatique + 18 en caisse" },
            // No shorter fragment than these: checked against every unit name of this build, each fragment matches only the tank
            // family it names. A two-character fragment (a "K2" for the Black Panther) would also catch "2K22M Tunguska-M", so the
            // tanks that are not in the game are simply not listed. A unit no fragment matches is left out of the audit, never guessed.
        };

        sealed class Paire                        // one (unit, main gun) pair of the database
        {
            public Units U;
            public Weapons W;
            public readonly List<(Ammunitions A, int Q)> Munitions = new();
            public int Total, Perforant, AntiChar, Explosif;
            public Reel Ref;
        }

        // The three buckets, and where the line between them comes from.
        //  - "perforant": ArmorTargeted = Kinetic. The tank's kinetic dart.
        //  - "anti-char": any other round the GAME itself forbids against foot troops and open ground, i.e. whose
        //    TargetType carries neither Ground nor Infantry. Read on this build's own export: the T-90M's gun-launched
        //    Invar missile (row 245, TargetType "Vehicle, Ship") lands here, where an else-branch on Kinetic would have
        //    called it high explosive.
        //  - "explosif": everything else.
        // This is the game's own field, not a judgement. It is deliberately NOT a penetration threshold: on this build
        // the M1A2's HEAT shell (600 mm at ground range) and the T-90M's HEAT shell both carry Ground and Infantry, so
        // the game means them to be fired at anything - which is why each round's own penetration is printed beside its
        // label, and nobody has to take the word for it.

        /// True when the round may be fired at foot troops or at open ground, as the game's own TargetType says.
        static bool SolOuInfanterie(Ammunitions a)
        {
            long tt = (long)a.TargetType;
            return (tt & ((long)AmmoTarget.Ground | (long)AmmoTarget.Infantry)) != 0L;
        }

        /// The bucket of one round: 0 perforant, 1 anti-char, 2 explosif.
        static int Genre(Ammunitions a)
        {
            if (a.ArmorTargeted == ArmorKind.Kinetic) return 0;
            return SolOuInfanterie(a) ? 2 : 1;
        }

        static string GenreNom(int g) => g == 0 ? "perforant" : g == 1 ? "anti-char" : "explosif";

        /// Measures the ammunition actually loaded in this database and reports it. Writes nothing. Returns a one-line summary,
        /// or an empty string when it did nothing (already measured, module stopped, table missing).
        internal static string Mesure(DataBaseSourceData src, string sourceId)
        {
            if (_off || src == null || Mod.Log == null) return "";
            string cle = string.IsNullOrEmpty(sourceId) ? "inconnu" : sourceId;
            if (!_faits.Add(cle)) return "";

            var res = new StringBuilder();
            try
            {
                var units = Index(Props.Rows(src.Units.GetAll()), u => u.Id);
                var armes = Index(Props.Rows(src.Weapons.GetAll()), w => w.Id);
                var muns = Index(Props.Rows(src.Ammunitions.GetAll()), a => a.Id);
                var lignes = Props.Rows(src.WeaponAmmunitions.GetAll());
                if (units.Count == 0 || armes.Count == 0 || muns.Count == 0 || lignes.Count == 0)
                {
                    Mod.Log.Warning($"{Tag} tables de munitions illisibles dans '{cle}' : rien mesuré, rien changé");
                    return "";
                }

                var pays = Pays(src);
                var toutes = new Dictionary<(int, int), Paire>();
                int lignesLues = 0, pairesMulti = 0, pairesMixtes = 0, qMin = int.MaxValue, qMax = 0;

                foreach (var l in lignes)
                {
                    if (l == null) continue;
                    lignesLues++;
                    int q = l.Quantity;
                    if (q < qMin) qMin = q;
                    if (q > qMax) qMax = q;
                    if (!units.TryGetValue(l.UnitId, out var u) || !armes.TryGetValue(l.WeaponId, out var w)) continue;
                    if (!muns.TryGetValue(l.AmmunitionId, out var a)) continue;
                    var k = (l.UnitId, l.WeaponId);
                    if (!toutes.TryGetValue(k, out var p)) toutes[k] = p = new Paire { U = u, W = w };
                    p.Munitions.Add((a, q));
                    p.Total += q;
                    int g = Genre(a);
                    if (g == 0) p.Perforant += q; else if (g == 1) p.AntiChar += q; else p.Explosif += q;
                }
                foreach (var p in toutes.Values)
                {
                    if (p.Munitions.Count >= 2) pairesMulti++;
                    if (p.Munitions.Any(m => m.A.ArmorTargeted == ArmorKind.Kinetic) &&
                        p.Munitions.Any(m => m.A.ArmorTargeted != ArmorKind.Kinetic)) pairesMixtes++;
                }

                // ---- what the game itself owns: the proof line
                Etape("mécanisme", () => Mod.Log.Msg(
                    $"{Tag} le jeu compte déjà par type, base '{cle}' : {lignesLues} ligne(s) (unité, arme, munition) chacune avec sa propre quantité, " +
                    $"{toutes.Count} couple(s) unité+arme, dont {pairesMulti} qui portent au moins deux types de munition et {pairesMixtes} qui portent " +
                    $"à la fois du perforant et de l'explosif ; quantités de {(qMin == int.MaxValue ? 0 : qMin)} à {qMax} ; " +
                    "à la mise en jeu chaque type reçoit sa propre réserve (AmmunitionBox, une réserve par obus) : " +
                    "quand le perforant est épuisé l'unité n'a plus de perforant, rien n'est changé ici"));

                // ---- main guns of the units the reference table knows, per side.
                // One unit can carry several MainGun rows (gun variants, and the M60's 165 mm demolition gun beside the
                // 105 mm). Comparing each of them to the same reference counts one tank several times and matches a
                // 30-round demolition gun against a 63-round reference. Only the best-loaded main gun of each unit is
                // kept: that is the tank's own gun, and every unit then weighs exactly one in the per-side average.
                var parUnite = new Dictionary<int, Paire>();
                int variantes = 0;
                foreach (var p in toutes.Values)
                {
                    if (p.W.Type != WeaponKind.MainGun) continue;
                    if (p.U.IsUnitModification) continue;
                    p.Ref = Reference(Nom(p.U));
                    if (p.Ref == null) continue;
                    if (parUnite.TryGetValue(p.U.Id, out var deja))
                    {
                        variantes++;
                        if (p.Total <= deja.Total) continue;
                    }
                    parUnite[p.U.Id] = p;
                }
                var canons = new List<Paire>(parUnite.Values);

                Etape("échantillon", () => Echantillon(canons, pays));
                Etape("bilan par camp", () => res.Append(Camps(canons, pays)));
                Etape("premier coup", () => PremierCoup(armes, toutes));

                string bilan = $"{toutes.Count} couple(s) unité+arme, {pairesMixtes} avec perforant et explosif séparés, {canons.Count} canon(s) principal(aux) comparé(s) au réel" +
                               (variantes > 0 ? $" ({variantes} autre(s) canon(s) de la même unité écarté(s) : un char ne compte qu'une fois)" : "");
                Mod.Log.Msg($"{Tag} relevé terminé : {bilan} ; aucune valeur écrite dans le jeu");
                return res.Length > 0 ? bilan + " ; " + res.ToString() : bilan;
            }
            catch (Exception e)
            {
                Faute("relevé", e);
                return "";
            }
        }

        // ------------------------------------------------------------ report

        /// One line per unit: its main gun, its real reference, and the split the player actually gets. Balanced between the sides.
        static void Echantillon(List<Paire> canons, Dictionary<int, string> pays)
        {
            var parCamp = new Dictionary<int, int>();
            foreach (var p in canons.OrderBy(x => x.U.CountryId).ThenBy(x => x.U.Id).ThenBy(x => x.W.Id))
            {
                int c = p.U.CountryId;
                if (!parCamp.TryGetValue(c, out int n)) n = 0;
                if (n >= EchantillonParCamp) continue;
                parCamp[c] = n + 1;

                var detail = new StringBuilder();
                foreach (var (a, q) in p.Munitions.OrderBy(m => Genre(m.A)).ThenBy(m => m.A.Id))
                {
                    if (detail.Length > 0) detail.Append(", ");
                    // the round's own penetration at ground range comes with the label: a HEAT shell called "explosif"
                    // because the game lets it be fired at infantry still shows what it does to armour
                    float pen = 0f;
                    try { pen = a.PenetrationAtGroundRange; } catch { pen = 0f; }
                    detail.Append($"{Nom(a)} ({GenreNom(Genre(a))}{(pen > 0f ? ", " + F(pen) + " mm" : "")}) x{q}");
                }
                string ecart = p.Ref.Obus > 0
                    ? $" ; réel {p.Ref.Obus} obus ({p.Ref.Detail}), écart {Pourcent((double)p.Total / p.Ref.Obus - 1.0)}"
                    : "";
                // a total equal to the ready rack means the loadout table kept only what the crew can fire without restowing
                if (p.Ref.Pret > 0 && p.Total == p.Ref.Pret && p.Total < p.Ref.Obus)
                    ecart += " (la dotation embarquée ne vaut que la réserve prête à tirer, le reste du chargement réel manque)";
                Mod.Log.Msg($"{Tag} {Camp(pays, c)} {Nom(p.U)} / {Nom(p.W)} : {p.Total} obus, dont perforant {p.Perforant}, " +
                            $"anti-char {p.AntiChar} et explosif {p.Explosif} [{detail}]{ecart}");
            }
        }

        /// Per-side comparison against the real loadouts, and the one-sided warning when one camp is cut and the other is not.
        static string Camps(List<Paire> canons, Dictionary<int, string> pays)
        {
            var somme = new Dictionary<int, (double Ratio, int N, int Sous)>();
            foreach (var p in canons)
            {
                if (p.Ref == null || p.Ref.Obus <= 0) continue;
                double r = (double)p.Total / p.Ref.Obus;
                somme.TryGetValue(p.U.CountryId, out var s);
                somme[p.U.CountryId] = (s.Ratio + r, s.N + 1, s.Sous + (r < 0.85 ? 1 : 0));
            }
            if (somme.Count == 0) return "";

            var moyennes = new List<(int Pays, double Moy, int N, int Sous)>();
            var texte = new StringBuilder();
            foreach (var kv in somme.OrderBy(k => k.Key))
            {
                double moy = kv.Value.Ratio / kv.Value.N;
                moyennes.Add((kv.Key, moy, kv.Value.N, kv.Value.Sous));
                if (texte.Length > 0) texte.Append(" ; ");
                texte.Append($"{Camp(pays, kv.Key)} : {kv.Value.N} canon(s), dotation moyenne {Pourcent(moy - 1.0)} par rapport au réel, " +
                             $"{kv.Value.Sous} nettement en dessous");
            }
            Mod.Log.Msg($"{Tag} dotation des canons principaux face aux chargements réels -> {texte}");

            if (moyennes.Count >= 2)
            {
                var haut = moyennes.OrderByDescending(m => m.Moy).First();
                var bas = moyennes.OrderBy(m => m.Moy).First();
                if (haut.Moy - bas.Moy >= EcartCampAlerte)
                    Mod.Log.Warning($"{Tag} déséquilibre entre les camps : {Camp(pays, bas.Pays)} est à {Pourcent(bas.Moy - 1.0)} du réel " +
                                    $"et {Camp(pays, haut.Pays)} à {Pourcent(haut.Moy - 1.0)}. La règle de l'auteur est que tout s'applique aux deux camps. " +
                                    "Rien n'est corrigé ici : les quantités viennent de reel/Chargements.csv, ce fichier ne les écrit pas");
            }
            return texte.ToString();
        }

        /// The first-shot measurement, logged once. States what the game owns and why the mod does not touch it. Writes nothing.
        static void PremierCoup(Dictionary<int, Weapons> armes, Dictionary<(int, int), Paire> toutes)
        {
            var visees = new Dictionary<string, int>();
            int canons = 0;
            foreach (var w in armes.Values)
            {
                if (w == null || w.Type != WeaponKind.MainGun) continue;
                canons++;
                string k = $"{F(w.AimTimeMin)}-{F(w.AimTimeMax)} s";
                visees[k] = visees.TryGetValue(k, out int n) ? n + 1 : 1;
            }

            // counted on DISTINCT ammunition rows: the same shell is loaded on dozens of units, and counting it once per
            // unit would inflate the figure threefold while the point being made is that there is ONE shared row
            var dispersions = new Dictionary<string, int>();
            var vus = new HashSet<int>();
            foreach (var p in toutes.Values)
            {
                if (p.W.Type != WeaponKind.MainGun) continue;
                foreach (var (a, _) in p.Munitions)
                {
                    if (!vus.Add(a.Id)) continue;
                    string k = $"H{F(a.DispersionHorizontalRadius)}/V{F(a.DispersionVerticalRadius)}";
                    dispersions[k] = dispersions.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            }

            string visee = string.Join(", ", visees.OrderByDescending(k => k.Value).Take(3).Select(k => $"{k.Key} sur {k.Value} arme(s)"));
            string disp = string.Join(", ", dispersions.OrderByDescending(k => k.Value).Take(3).Select(k => $"{k.Key} sur {k.Value} munition(s)"));

            Mod.Log.Msg($"{Tag} premier coup, mesure : {canons} canon(s) principal(aux), temps de visée avant le premier tir {visee} ; " +
                        $"dispersion des obus de char {disp} ({dispersions.Count} valeur(s) différente(s) en tout)");
            Mod.Log.Msg($"{Tag} premier coup, décision : RIEN N'EST CHANGÉ. Le jeu fait déjà attendre l'équipage avant son premier tir " +
                        "(temps de visée tiré au hasard à chaque nouvel engagement), mais il ne garde aucune trace d'un premier coup ni " +
                        "d'un changement de cible, et la dispersion est portée par la ligne de munition, la même pour tous les tireurs des " +
                        "deux camps. Les deux fonctions qui appliquent la dispersion à un tir ne reçoivent ni le tireur ni la cible : " +
                        "les toucher rendrait tous les tirs moins précis, pas seulement le premier. Une fausse règle serait pire que rien");
        }

        // ------------------------------------------------------------ small helpers

        static Dictionary<int, T> Index<T>(List<T> rows, Func<T, int> id) where T : class
        {
            var d = new Dictionary<int, T>(rows.Count);
            foreach (var r in rows) if (r != null) d[id(r)] = r;
            return d;
        }

        /// Country names when the table can be read, so the log says "Russie" and not "pays 1". Never fatal.
        static Dictionary<int, string> Pays(DataBaseSourceData src)
        {
            var d = new Dictionary<int, string>();
            try
            {
                foreach (var c in Props.Rows(src.Countries.GetAll()))
                {
                    if (c == null) continue;
                    string n = c.UIName;
                    if (string.IsNullOrWhiteSpace(n)) n = c.Name;
                    if (!string.IsNullOrWhiteSpace(n)) d[c.Id] = n;
                }
            }
            catch (Exception e) { Faute("noms des pays", e); }
            return d;
        }

        static string Camp(Dictionary<int, string> pays, int id) =>
            pays != null && pays.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "pays " + id.ToString(CultureInfo.InvariantCulture);

        static Reel Reference(string nom)
        {
            if (string.IsNullOrEmpty(nom)) return null;
            foreach (var r in References)
                if (nom.IndexOf(r.Nom, StringComparison.OrdinalIgnoreCase) >= 0) return r;
            return null;
        }

        static string Nom(Units u) => Premier(u?.Name, u?.HUDName, "unité");
        static string Nom(Weapons w) => Premier(w?.Name, w?.HUDName, "arme");
        static string Nom(Ammunitions a) => Premier(a?.HUDName, a?.Name, "munition");

        static string Premier(string a, string b, string defaut) =>
            !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : defaut;

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        static string Pourcent(double v) => (v >= 0 ? "+" : "") + (v * 100.0).ToString("0", CultureInfo.InvariantCulture) + " %";

        /// One step failing must never take down the others nor the rest of the mod.
        static void Etape(string quoi, Action a)
        {
            if (_off) return;
            try { a(); }
            catch (Exception e) { Faute(quoi, e); }
        }

        static void Faute(string quoi, Exception e)
        {
            if (_off) return;
            if (++_erreurs >= MaxErreurs)
            {
                _off = true;
                Mod.Log.Warning($"{Tag} trop d'erreurs ({_erreurs}) : relevé arrêté pour la session. Aucune valeur n'avait été écrite dans le jeu");
                return;
            }
            Mod.Log.Warning($"{Tag} {quoi} : {e.Message}");
        }
    }
}
