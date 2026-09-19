// RealismOverhaul - Batiments (v0.1): buildings of different resistance, "béton contre bois".
//  THIS VERSION MEASURES AND CHANGES NOTHING. Not one value of the game is written by this file, there is nothing to restore
//  when it stops, and no Harmony patch is installed. The reason is written out below, with everything that was checked in the
//  dumps first, so the next build starts from facts and not from a guess.
//
//  WHAT THE GAME REALLY OWNS (checked in alldump.txt / allmembers.txt before a line was written here):
//  - There is NO building table in the database. Il2CppBrokenArrow.DataBase.Models holds Units, Armors, Ammunitions, Weapons,
//    Sensors, Mobility... and nothing about houses. So a building has no row, no armour class and no material field to read.
//  - There is NO material or type enum anywhere: BuildingLabelType is Empty / OneSlot / TwoSlots, which is the label of the
//    squads standing inside, not the house.
//  - BuildingsConfig (ScriptableObject, global, one for the whole game) holds exactly five numbers that matter here:
//    BuildingDamageThreshold, DamageModifierFloor, DamageModifierPerSoldier, HitColliderReduction, BuildingImpactVfxSpawnChance.
//    The two the mod already writes through the realism pipeline are DamageModifierFloor and DamageModifierPerSoldier
//    (Mod.cs, "[REALISME] bâtiments ... bâtiments=2"): they are the share of the damage a GARRISONED SQUAD still takes, not the
//    house's own strength. This file does not touch them and must not: they are journalled under the realism lot.
//  - What IS readable per building, on the map, in battle:
//      BuildingSegmentComponent.SharedMeshHashID   Int32   - the model. Every copy of the same house shares it: a type key.
//      BuildingSegmentComponent.MaxFakeHealth / FakeHealth  Single - the segment's own health numbers.
//      BuildingSegmentComponent.ColliderBounds     Bounds  - footprint and height.
//      BuildingSegmentComponent.SegmentObject      GameObject - its name, and the MeshRenderer's material name.
//      BuildingInitializer.Durability              Int32   - set by the level designers on the scene object, per building.
//      BuildingInitializer.Capacity                UInt32  - how many squads fit inside.
//    So buildings are NOT all the same object with one global number: they carry a model id and a designer-set durability.
//    This is world 2 of the three the author asked about - per prefab / per instance, with no declared material - and the
//    classification can only be built from what is readable: name, model id, size, durability.
//  - SO "CONCRETE AGAINST WOOD" IS DEDUCED, NEVER DECLARED. The game says nowhere that a building is concrete or wood. The only
//    thing this file has is the name the artists gave the object and its material, plus the mesh id and the size. Every log line
//    says so in those words, gives the share of buildings the names actually classify, and never presents the difference as a
//    number the game holds. That is the author's decision of 19/09/2026 and it is the condition under which the feature was kept.
//  - WHY NOTHING IS WRITTEN YET: two things are unknown and only a real battle log can answer them.
//      (1) Do Durability and MaxFakeHealth actually DIFFER from one building to the next on a campaign map, or did the designers
//          leave the same number everywhere? If they are all equal, the game itself makes no difference today and the author
//          must be told that plainly instead of being sold a fake one.
//      (2) Which of the two is the one the engine really spends? "FakeHealth" is named fake for a reason, and Demolition.cs
//          already states that a building's real health sits in a HealthComponent on its entity - which this project forbids
//          reading (no Entity.Get<T>). Writing a number whose meaning is not proven is exactly how a campaign gets broken.
//    So this build reads both, counts how many distinct values the map holds, and prints the models. One log from one mission
//    settles the first question; the second one it cannot settle at all, and the verdict says which value it was measured on
//    instead of announcing a property of the engine.
//
//  ONE LINE, ON PURPOSE. The build the author runs is the PUBLIC one, whose log filter keeps one line per module prefix every five
//  minutes and drops the rest ("[BATIMENTS]" is not in its always-keep list and this file does not own ModLog.cs). A census spread
//  over forty lines would therefore reach him as its first line and nothing else. So everything that decides the feature - the
//  game's global numbers, the counts, the health and durability ranges, the verdict, the share the names classify and the biggest
//  models - travels in ONE line. The per-model catalogue stays as extra lines in the personal build only.
//
//  WHEN IT RUNS. BuildingService.InitMapBuildings() is a UniTask: IsInitialized can be true while the dictionary is still filling,
//  and a pass over 300 of 906 segments would produce exactly the distinct-value counts that decide the feature with nothing in the
//  log to say it was partial. So the size of the list is read first (one property, no walk) and the census waits for two equal
//  readings before it runs, retrying 5 s apart a bounded number of times, the way Demolition.Poll already does. A list that never
//  settles is measured on the last try anyway, with the line saying the reading may be partial; a map that gives nothing readable
//  is retried instead of freezing an empty verdict for the battle.
//
//  THE LEVER FOR THE NEXT BUILD, identified and checked against the hard rules (not installed here):
//    BuildingService.InitBuildingSegment(BuildingInitializer buildingInitializer) - instance method, ONE argument, a plain
//    MonoBehaviour reference passed BY VALUE. No IL2CPP struct, nothing by reference: a prefix on it is allowed here, unlike
//    every other building damage entry point. ShellHitSystem.DamageBuilding, DealUnitDamage and DealAOEDamage all take
//    HitDamageInfo BY REFERENCE, and HitDamageInfo is a non-blittable struct (Entity, Ammunitions, IPlayerInfo, Nullable<Single>):
//    they are forbidden, and that is what cost a player his campaign on 18/09/2026. A prefix on InitBuildingSegment can scale
//    buildingInitializer.Durability before the engine builds the segment, once per building at map load (about 900 calls, no
//    hot path), journalled so stopping the mod puts the designers' number back.
//    CONSEQUENCE TO ACCEPT: that lever is per BUILDING, not per SHELL. The author's "concrete stops 30 mm but not a 152 mm
//    shell" cannot be written per calibre, because the only place that knows both the shell and the house is DamageBuilding,
//    which is forbidden. A tougher house gets there on its own: a 30 mm burst never takes a concrete block's hit points down,
//    a 152 mm shell does in a few rounds, and Demolition.cs already razes anything under a heavy warhead whatever it is made of.
//
//  HOW THIS WOULD INTERACT WITH THE GARRISON RULES (read before writing this file):
//   - Couvert.cs postfixes BattleSystemHelpers.CalculateHitDamage and scales the damage taken by INFANTRY SQUADS in vegetation
//     and forest; a garrisoned squad and a squad on a building pixel are explicitly never scaled there. Retranchement.cs rides
//     on that same postfix and halves its own bonus inside a building.
//   - Nothing in this file is on that path. Changing a house's hit points does not change by one point the damage a squad
//     inside it takes: that stays BuildingsConfig floor + per soldier, times IgnoreCover, times Couvert, times Retranchement.
//     A garrison becomes neither invulnerable nor paper.
//   - The ONE real interaction, and it must be said out loud: GameConfig.OnBuildingDestroyPrecentage (60 on this build) kills
//     that share of the occupants when the house comes down. A shed made weaker collapses sooner, so the squad inside dies
//     sooner. That is the honest cost of the feature, and it is why the next build must never weaken a building the mission
//     needs (Missions.ProtectedAt, the same protection Demolition.cs already uses) and must keep a floor under the weakest class.
//
//  Cost: one pass over the map's building dictionary, once per battle, in the frame slot Planif.cs hands out (Demolition.cs
//  pays 2.7 ms for the same pass on a 906 segment map). That pass now also asks each segment's object for its BuildingInitializer,
//  because the designers set Durability per scene object and two copies of the same mesh can carry two different numbers: reading
//  one copy per model would report "a single durability" on a map holding three. The lookups are capped, and the time the whole
//  pass took is printed in the line so the cost is measured and not assumed. After that the module only checks every 2 s whether
//  the battle changed. No allocation per frame, no hook, no thread but the main one. Error counter with kill-switch for the session.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using BSvc = Il2CppBrokenArrow.Client.Ecs.Building.BuildingService;
using BSeg = Il2CppBrokenArrow.Client.Ecs.Building.BuildingSegmentComponent;
using BInit = Il2CppBrokenArrow.Client.Ecs.Init.BuildingInitializer;
using HitSys = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.ShellHitSystem;
using UGameObject = UnityEngine.GameObject;

namespace RealismOverhaul
{
    static class Batiments
    {
        const int MaxErrors = 20;
        const int MaxSegments = 8000;       // a map far larger than anything shipped: the pass can never run away
        const int MaxSamples = 250;         // models whose NAME and MATERIAL are read: one read each, not one per segment
        const int MaxDurabilite = 4000;     // designer durability: one component lookup per SEGMENT, capped so the pass cannot run away
        const int DetailLines = 30;         // models printed one per line, personal build only (see Census)
        const int DetailLigneUne = 5;       // models carried inside the single summary line, the biggest first
        const int MaxEssais = 6;            // tries before the map is declared unreadable for this battle (Demolition.cs does the same)
        const float Step = 2f;              // seconds between two checks once the census is done
        const float Retry = 5f;             // seconds before trying again while the map's building list is still filling
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // classes of the planned table, in the order the name is tested
        const int KBeton = 0, KBrique = 1, KBois = 2, KInconnu = 3, KAmbigu = 4, KNonEch = 5, KCount = 6;
        static readonly string[] KNames = { "béton armé", "brique ou maçonnerie", "bois ou tôle", "inconnu (aucun mot reconnu)",
                                            "ambigu (le nom cite deux familles)", "non échantillonné (au-delà du plafond de lecture)" };

        /// Resistance planned for each class, as a factor on the building's own durability. NOT APPLIED by this version:
        /// it is printed in the log so the author can judge the table on his own map before anything is written.
        ///  béton armé x1.80 - a reinforced concrete frame eats a 30 mm burst and a light shell and keeps standing; it goes down
        ///                     to a 152 mm shell, a heavy rocket or a guided bomb, which is what Demolition.cs already does.
        ///  brique / maçonnerie x1.00 - the reference, the game's own number: walls that crack, spall and finally fall in.
        ///  bois ou tôle x0.35 - a shed, a barn, a sheet-metal hangar: rifle and machine-gun fire go through it, an autocannon
        ///                     burst or one HE shell puts it on the ground.
        static readonly float[] KMult = { 1.80f, 1.00f, 0.35f, 1.00f, 1.00f, 1.00f };

        // keywords looked for in the segment object's name and in its material's name, lower case, accents both ways
        static readonly string[] WBeton = { "beton", "béton", "concrete", "panel", "khrush", "factory", "usine", "plant", "industr", "bunker", "silo", "tower", "tour", "church", "eglise", "église", "school", "ecole", "école", "hospital", "hopital", "hôpital", "apartment", "appart", "highrise", "office", "bloc", "block" };
        static readonly string[] WBrique = { "brick", "brique", "stone", "pierre", "masonry", "house", "maison", "cottage", "villa", "farm", "ferme" };
        static readonly string[] WBois = { "wood", "bois", "plank", "shed", "cabane", "hangar", "hut", "shack", "barn", "grange", "garage", "kiosk", "kiosque", "container", "tole", "tôle", "sheet", "metal", "fence", "trailer", "caravan" };

        static MelonPreferences_Entry<bool> _catalogue;
        static BSvc _bs; static GameSessionContext _ctx;
        static bool _done, _off, _noServiceLogged;
        static int _errors, _wait;
        static int _essais;                 // tries spent on this battle's map
        static int _dernierTotal = -1;      // size of the map's building list at the previous try: the pass waits for it to stop growing
        static float _next, _now;

        static void Log(string s) => Mod.Log.Msg("[BATIMENTS] " + s);
        static string F(double v, string fmt = "0.##") => v.ToString(fmt, Inv);

        internal static void CreatePrefs()
        {
            // same category as Demolition.cs (MelonPreferences returns the existing one), a name of its own
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Batiments");
            // the description says what the line really contains: the game declares no material anywhere, so nothing here may be
            // presented as "the resistance the game gives" - it is what the mod reads, plus a family guessed from the model's name.
            _catalogue = c.CreateEntry("CatalogueBatiments", true, description: Build.Desc(
                "Écrit une fois par bataille, dans le log, ce que le mod peut lire des bâtiments de la carte (santé affichée, durabilité posée par les concepteurs, modèle, taille) et la famille de matériau DÉDUITE du nom du modèle — le jeu ne déclare aucun matériau. Mesure seule : ne change rien dans la partie.",
                "Relevé des bâtiments de la carte dans le journal du mod (mesure seule)."));
        }

        /// Called every frame (Mod.OnUpdate or ModTab.Frame): does nothing but a clock read until a battle is loaded.
        internal static void Tick()
        {
            if (_off || _catalogue == null || !_catalogue.Value) return;
            if (Mod.AntiCheatActive || Identite.Blocked) return;
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
            if (now < _next) return;
            _next = now + Step;
            _now = now;
            Guard.Run("Batiments.Poll", Poll);
        }

        /// One census per battle - but only once the map's building list has stopped growing, and only if the pass read something.
        /// BuildingService.InitMapBuildings() is a UniTask: IsInitialized can be true while the dictionary is still filling, and a
        /// pass over 300 of 906 segments would produce exactly the distinct-value counts that decide the whole feature, with nothing
        /// in the log to say it was partial. So the list's size is read first and the census waits for two equal readings, then
        /// retries on an empty or unreadable map, the way Demolition.Poll already does (5 s apart, a bounded number of tries).
        static void Poll()
        {
            var ctx = Campaign.Ctx();
            var svc = Mod.Svc<BSvc>();
            if (ctx == null || svc == null)
            {
                // back in the menus, or the battle is not up yet: everything per-battle is dropped
                if (_bs != null || _ctx != null) { _bs = null; _ctx = null; _done = false; _essais = 0; _dernierTotal = -1; }
                return;
            }
            if (_bs == null || _bs.Pointer != svc.Pointer || _ctx == null || _ctx.Pointer != ctx.Pointer)
            {
                _bs = svc; _ctx = ctx; _done = false; _essais = 0; _dernierTotal = -1;
            }
            if (_done) return;
            bool ready;
            try { ready = svc.IsInitialized; } catch { ready = false; }
            if (!ready) return;

            // how many segments the service holds right now: one property read, no enumeration
            int total;
            try { var d = svc.Buildings; total = d == null ? -1 : d.Count; } catch { total = -1; }
            bool stable = total > 0 && total == _dernierTotal;
            _dernierTotal = total;
            if (!stable && _essais < MaxEssais - 1)
            {
                // still filling, or not readable at all: wait and look again instead of freezing a partial map into the verdict
                _essais++;
                _next = _now + Retry;
                return;
            }

            if (!Planif.Take(ref _wait, Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            // last try: the census runs even on a list that never settled, and the line says the reading may be partial
            if (Census(svc, total, stable)) { _done = true; return; }
            _essais++;
            _next = _now + Retry;
            if (_essais < MaxEssais) return;
            _done = true;
            Log($"aucun bâtiment lisible sur cette carte après {MaxEssais} essais : rien à relever pour cette bataille, "
                + "et rien n'est modifié dans le jeu");
        }

        // ---------------------------------------------------------------- one model of building, as the map shows it
        sealed class Cls
        {
            internal int Hash, N;
            internal int Klass = KNonEch;          // a model whose name was never read is counted apart: never as concrete, never as "unknown name"
            internal float HMin = float.MaxValue, HMax = float.MinValue, HSum;
            internal float SX, SY, SZ;
            internal BSeg Sample;                  // held for the length of the pass only
            internal int Durab = int.MinValue, Cap = -1;
            internal bool DurabVarie;              // two copies of the SAME model carry different designer durabilities
            internal string ObjName = "?", MatName = "?";
        }

        /// The whole census, and it says everything in ONE log line. That is not a style choice: the PUBLIC build - the build the
        /// author actually runs - keeps one line per module prefix every five minutes and drops the rest, so a census spread over
        /// forty lines would reach him as its first line and nothing else. Everything that decides the feature therefore travels in
        /// the first line: the game's own global numbers, the segment count, the health and durability ranges, the verdict, the share
        /// of buildings the NAMES actually classify, and the biggest models. The per-model catalogue stays as extra lines, personal
        /// build only, because in the PUBLIC build those lines would be dropped whatever this file does.
        /// Returns false when the map gave nothing readable, so the caller can try again instead of freezing an empty verdict.
        static bool Census(BSvc svc, int annonces, bool stable)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ---- 1) the game's own global numbers, before anything else and before anything is ever changed
            string thr = "?", floor = "?", perSoldier = "?", collider = "?", pct = "?", stat = "?", soldierMod = "?", map = "?";
            try
            {
                var cfg = GameConfig.Instance;
                var bc = cfg?.BuildingsConfig;
                if (bc != null)
                {
                    thr = bc.BuildingDamageThreshold.ToString(Inv);
                    floor = F(bc.DamageModifierFloor, "0.####");
                    perSoldier = F(bc.DamageModifierPerSoldier, "0.####");
                    collider = F(bc.HitColliderReduction, "0.####");
                }
                if (cfg != null) pct = cfg.OnBuildingDestroyPrecentage.ToString(Inv);
            }
            catch (Exception e) { Note(e); }
            try { stat = HitSys._buildingDamageThreshold.ToString(Inv); } catch { }
            try { soldierMod = F(HitSys.HOUSE_SOLDIER_DAMAGE_MODIFIER, "0.####"); } catch { }
            try { map = svc._mapName ?? "?"; } catch { }

            string jeu = $"valeurs du jeu avant toute intervention : seuil de dégâts d'un bâtiment={thr} (statique={stat}), " +
                         $"plancher de protection en garnison={floor}, par soldat={perSoldier} (ces deux-là sont écrits par [REALISME], ce module n'y touche pas), " +
                         $"réduction du collider={collider}, occupants tués à l'effondrement={pct} %, modificateur soldat en maison={soldierMod}";

            // ---- 2) one pass over the map's buildings.
            // The designer durability is read HERE, on every segment, and not once per model: BuildingInitializer.Durability is set by
            // the level designers on each scene object, so two copies of the same mesh can carry two different numbers, and a
            // per-model sample would report "one single durability" on a map that holds three. One component lookup per segment, on a
            // pass that already walks all of them, capped so it can never run away; the time it costs is in the line below.
            var byHash = new Dictionary<int, Cls>(256);
            int n = 0, bad = 0, noObj = 0;
            float hMin = float.MaxValue, hMax = float.MinValue; double hSum = 0;
            float cMin = float.MaxValue, cMax = float.MinValue;
            var distinctMax = new HashSet<float>();
            int durLus = 0, durOk = 0, modelesDurabVariable = 0;
            int dMin = int.MaxValue, dMax = int.MinValue;
            var distinctDur = new HashSet<int>();
            string firstErr = null;
            try
            {
                var dict = svc.Buildings;
                if (dict == null) return false;                    // the caller tries again: an empty verdict is never frozen in
                var en = dict.GetEnumerator();
                while (en.MoveNext() && n < MaxSegments)
                {
                    try
                    {
                        var c = en.Current.Value; if (c == null) continue;
                        var go = c.SegmentObject; if (go == null) { noObj++; continue; }
                        n++;
                        float mh = c.MaxFakeHealth, fh = c.FakeHealth;
                        if (mh < hMin) hMin = mh; if (mh > hMax) hMax = mh; hSum += mh;
                        if (fh < cMin) cMin = fh; if (fh > cMax) cMax = fh;
                        if (distinctMax.Count < 512) distinctMax.Add(mh);
                        int hash = c.SharedMeshHashID;
                        if (!byHash.TryGetValue(hash, out var cl)) byHash[hash] = cl = new Cls { Hash = hash, Sample = c };
                        cl.N++;
                        if (mh < cl.HMin) cl.HMin = mh; if (mh > cl.HMax) cl.HMax = mh; cl.HSum += mh;
                        if (cl.N == 1)
                        {
                            var sz = c.ColliderBounds.size;
                            cl.SX = sz.x; cl.SY = sz.y; cl.SZ = sz.z;
                        }
                        if (durLus < MaxDurabilite)
                        {
                            durLus++;
                            try
                            {
                                var init = go.GetComponent<BInit>();
                                if (init != null)
                                {
                                    int d = init.Durability;
                                    durOk++;
                                    if (d < dMin) dMin = d; if (d > dMax) dMax = d;
                                    if (distinctDur.Count < 512) distinctDur.Add(d);
                                    if (cl.Cap < 0) cl.Cap = (int)init.Capacity;
                                    if (cl.Durab == int.MinValue) cl.Durab = d;
                                    else if (cl.Durab != d && !cl.DurabVarie) { cl.DurabVarie = true; modelesDurabVariable++; }
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception e) { bad++; firstErr ??= e.GetBaseException().Message; }
                }
            }
            catch (Exception e) { Note(e); firstErr ??= e.GetBaseException().Message; }

            if (n == 0) return false;                              // nothing readable: the caller tries again, then gives up with a line

            // ---- 3) one sample per model for the NAMES: object name and material name. Sorted by number of copies first, so that a
            // map with more models than MaxSamples still reads the ones that actually cover the ground instead of whatever the
            // dictionary happened to hand out first. A model past that cap keeps the class "non échantillonné": it is counted apart
            // and left out of the reliability figure, because "its name was never read" is not the same thing as "its name says
            // nothing" and the author must not be told one for the other.
            var list = new List<Cls>(byHash.Values);
            list.Sort((a, b) => b.N.CompareTo(a.N));
            int samples = 0;
            foreach (var cl in list)
            {
                if (samples >= MaxSamples) { cl.Sample = null; continue; }   // nothing of the map is held past this pass
                samples++;
                try
                {
                    UGameObject go = cl.Sample?.SegmentObject;
                    if (go != null) cl.ObjName = go.name ?? "?";
                    try
                    {
                        var mr = cl.Sample.MeshRenderer;
                        var mat = mr == null ? null : mr.sharedMaterial;
                        if (mat != null) cl.MatName = mat.name ?? "?";
                    }
                    catch { }
                }
                catch (Exception e) { Note(e); }
                cl.Klass = Classify(cl.ObjName, cl.MatName);
                cl.Sample = null;                   // the pass is over: nothing of the map is held any more
            }

            // ---- 4) how far the NAMES actually carry. Models never sampled are counted apart and left out of the ratio.
            var perClass = new int[KCount];
            var segPerClass = new int[KCount];
            foreach (var c in list) { perClass[c.Klass]++; segPerClass[c.Klass] += c.N; }
            int juges = n - segPerClass[KNonEch];                                     // segments whose name was actually read
            int nommes = segPerClass[KBeton] + segPerClass[KBrique] + segPerClass[KBois];

            // ---- 5) ONE line. Everything that decides the feature is in it, because in the PUBLIC build it is the only one written.
            var sb = new StringBuilder(2048);
            sb.Append("matériaux des bâtiments (relevé seul, ce module n'écrit aucune valeur dans le jeu) : carte '").Append(map)
              .Append("' — ").Append(n).Append(" segment(s) lus sur ").Append(annonces).Append(" annoncés par le jeu, ")
              .Append(byHash.Count).Append(" modèle(s) distinct(s) (SharedMeshHashID)");
            if (!stable)
                sb.Append(" ; ATTENTION : la liste des bâtiments changeait encore au moment du relevé, les chiffres qui suivent peuvent "
                        + "ne porter que sur une partie de la carte");
            if (bad + noObj > 0)
            {
                sb.Append(" (").Append(bad).Append(" illisible(s), ").Append(noObj).Append(" sans objet");
                if (firstErr != null) sb.Append(" : ").Append(firstErr);
                sb.Append(')');
            }
            sb.Append(" ; ").Append(jeu);

            sb.Append(" ; santé AFFICHÉE du segment (MaxFakeHealth) de ").Append(F(hMin)).Append(" à ").Append(F(hMax))
              .Append(", moyenne ").Append(F(hSum / n)).Append(", ").Append(distinctMax.Count).Append(distinctMax.Count >= 512 ? "+" : "")
              .Append(" valeur(s) distincte(s) ; FakeHealth actuelle de ").Append(F(cMin)).Append(" à ").Append(F(cMax));

            if (durOk > 0)
                sb.Append(" ; durabilité posée par les concepteurs (BuildingInitializer.Durability), relevée sur ").Append(durOk)
                  .Append(" segment(s) sur ").Append(durLus).Append(" examinés : de ").Append(dMin.ToString(Inv)).Append(" à ")
                  .Append(dMax.ToString(Inv)).Append(", ").Append(distinctDur.Count).Append(distinctDur.Count >= 512 ? "+" : "")
                  .Append(" valeur(s) distincte(s), dont ").Append(modelesDurabVariable)
                  .Append(" modèle(s) dont deux exemplaires ne portent pas le même chiffre");
            else
                sb.Append(" ; durabilité posée par les concepteurs : illisible sur cette carte (l'objet d'initialisation n'est plus là "
                        + "après le chargement) ; il reste la santé affichée et le modèle");

            // the verdict names the value it was measured on, and claims nothing about the engine: this project already established
            // (Demolition.cs) that a building's real health sits in a HealthComponent, which is forbidden reading here.
            bool varieHealth = distinctMax.Count > 1, varieDur = distinctDur.Count > 1;
            sb.Append(" ; ce que cela dit : ");
            if (varieHealth || varieDur)
                sb.Append("d'un bâtiment à l'autre, ").Append(varieDur ? "la durabilité des concepteurs varie" : "la santé affichée varie")
                  .Append(varieHealth && varieDur ? " et la santé affichée aussi" : "")
                  .Append(" — une résistance par matériau aurait donc de quoi s'appuyer. Ce n'est PAS une preuve sur le moteur : la santé "
                        + "que le moteur dépense vraiment est dans un HealthComponent, illisible ici (voir Demolition.cs)");
            else
                sb.Append("sur cette carte, ni la santé affichée ni la durabilité relevée ne varient d'un bâtiment à l'autre, pour ")
                  .Append(byHash.Count).Append(" modèle(s) distinct(s). Cela ne dit pas que le moteur ne fait aucune différence : la "
                        + "santé qu'il dépense vraiment est dans un HealthComponent, illisible ici (voir Demolition.cs)");

            // the author's binding decision: the classification is DEDUCED from the artists' names, never declared by the game
            sb.Append(" ; ATTENTION, LE CLASSEMENT CI-DESSOUS EST DÉDUIT DU NOM DU MODÈLE ET DE SON MATÉRIAU, PAS D'UNE DONNÉE DU JEU : "
                    + "le jeu ne déclare nulle part qu'un bâtiment est en béton ou en bois, il n'y a ni table de bâtiments dans la base "
                    + "ni matériau ; béton contre bois est une supposition tirée des noms que les graphistes ont donnés aux objets");
            sb.Append(" ; bâtiments réellement reconnus : ").Append(F(100.0 * nommes / Math.Max(1, juges), "0"))
              .Append(" % des segments dont le nom a été lu (").Append(nommes).Append(" sur ").Append(juges).Append(")");
            for (int k = 0; k < KCount; k++)
                sb.Append(" ; ").Append(KNames[k]).Append(' ').Append(perClass[k]).Append(" modèle(s) / ").Append(segPerClass[k]).Append(" segment(s)");
            sb.Append(" ; un nom qui cite deux familles est compté « ambigu » et non forcé dans l'une des deux : cette part mesure autant "
                    + "la liste de mots de ce fichier que les noms de la carte");

            // the biggest models, inside the same line: the evidence has to travel with the figure it supports
            int enLigne = Math.Min(DetailLigneUne, list.Count);
            if (enLigne > 0)
            {
                sb.Append(" ; modèles les plus nombreux : ");
                for (int i = 0; i < enLigne; i++)
                {
                    var c = list[i];
                    if (i > 0) sb.Append(" | ");
                    sb.Append(c.N).Append("x '").Append(Short(c.ObjName, 28)).Append("' / '").Append(Short(c.MatName, 28))
                      .Append("' -> ").Append(KNames[c.Klass]).Append(", santé ").Append(F(c.HMin)).Append('-').Append(F(c.HMax));
                    if (c.Durab != int.MinValue) sb.Append(", durabilité ").Append(c.Durab.ToString(Inv)).Append(c.DurabVarie ? " (variable)" : "");
                }
            }

            sb.Append(" ; table prévue, NON APPLIQUÉE : béton armé x").Append(F(KMult[KBeton])).Append(", brique ou maçonnerie x")
              .Append(F(KMult[KBrique])).Append(", bois ou tôle x").Append(F(KMult[KBois]))
              .Append(" — elle se poserait sur la durabilité du bâtiment avant la construction du segment, jamais sur la protection des "
                    + "soldats en garnison, qui ne change pas d'un point");
            sb.Append(" ; aucune valeur n'a été écrite dans le jeu par ce module (").Append(F(sw.Elapsed.TotalMilliseconds, "0.0"))
              .Append(" ms de lecture)");
            Log(sb.ToString());

            // ---- 6) the per-model catalogue, personal build only. In the PUBLIC build these lines are dropped by the log filter
            // whatever this file does, so they are not even built there - everything that matters is already in the line above.
            if (!Build.IsPublic)
            {
                int shown = Math.Min(DetailLines, list.Count);
                for (int i = 0; i < shown; i++)
                {
                    var c = list[i];
                    Log($"  modèle {c.Hash} : {c.N} exemplaire(s), santé affichée {F(c.HMin)} à {F(c.HMax)} (moyenne {F(c.HSum / Math.Max(1, c.N))})" +
                        (c.Durab != int.MinValue ? $", durabilité {c.Durab.ToString(Inv)}{(c.DurabVarie ? " (variable d'un exemplaire à l'autre)" : "")}, capacité {c.Cap.ToString(Inv)}" : ", durabilité illisible") +
                        $", taille {F(c.SX)} x {F(c.SZ)} m au sol, {F(c.SY)} m de haut, objet '{Short(c.ObjName)}', matériau '{Short(c.MatName)}'" +
                        $" -> classe déduite du nom : {KNames[c.Klass]} (prévu x{F(KMult[c.Klass])})");
                }
            }
            return true;
        }

        /// Class of a building GUESSED from the two strings the map gives - the object's name and its material's name. The game
        /// declares nothing: there is no building table in the database and no material enum, so this is a reading of what the
        /// artists typed, and the log says exactly that. A name that cites two families is counted apart instead of being forced
        /// into one: a wrong class silently applied is worse than an honest "ambigu" in the log. That share is partly a property of
        /// the word lists themselves ("PanelHouse" hits concrete and masonry, "ConcreteShed" hits concrete and wood), which is why
        /// the line says so beside the figure instead of letting it pass for a measurement of the map.
        static int Classify(string objName, string matName)
        {
            string s = ((objName ?? "") + " " + (matName ?? "")).ToLowerInvariant();
            bool beton = Any(s, WBeton), bois = Any(s, WBois), brique = Any(s, WBrique);
            int hits = (beton ? 1 : 0) + (bois ? 1 : 0) + (brique ? 1 : 0);
            if (hits == 0) return KInconnu;
            if (hits > 1) return KAmbigu;
            return beton ? KBeton : bois ? KBois : KBrique;
        }

        static bool Any(string s, string[] words)
        {
            for (int i = 0; i < words.Length; i++) if (s.Contains(words[i], StringComparison.Ordinal)) return true;
            return false;
        }

        static string Short(string s, int max = 48)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        /// Error counter: the module gives up for the session rather than write the same failure every battle.
        static void Note(Exception e)
        {
            if (++_errors < MaxErrors) return;
            _off = true;
            Log($"{MaxErrors} erreurs de lecture : relevé des bâtiments arrêté pour cette session ({e.GetBaseException().Message})");
        }
    }
}
