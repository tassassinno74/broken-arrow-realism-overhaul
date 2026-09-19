// RealismOverhaul - ProtectionMission: the script-driven units a campaign mission needs alive, mission by mission.
//  Why this exists: the game's own script has no continuation when the engineers' extraction Mi-8 of RU_C01 dies. Its death
//  watcher (#19110) has zero outgoing links, the chain is held by two blocking orders (#6756, #6772), so subgraph #6731 never
//  exits and the mission is stuck for good. Verified in the decrypted graph: #19110 has 0 outgoing links and its only unit input
//  is "6751.Units -> forUnits". Four other spawns are on the same page: the two Mi-8s that deliver the engineers, the engineer
//  squads themselves, and the two evacuation flights. Six more missions carry the same shape and are listed below.
//
//  What it does NOT do, and why (decision note "missions_protection_design", sections 2.2, 2.3 and 3, binding):
//   - it never writes NodeHealthUnitData.Immortal. That flag is write-only from the mod's bridge, five missions toggle it at
//     runtime in both directions, and it would swallow the script's own killUnit3 / dmgUnit calls;
//   - it protects nothing at all in the twelve missions absent from the table, and nothing owned by the player in any mission;
//   - it leaves every unit whose death the script needs fully killable. In RU_C01 that is Power Plant, Garrison, Station and
//     the wave groups; in RU_C04 it is the allied Batalin BMD, which is why RU_C04 is not in the table at all.
//
//  Mechanism: the one Resistance.cs already proves. A postfix on BattleSystemHelpers.CalculateHitDamage and a prefix on
//  CloseQuartersCombatSystem.DeductHitPoints scale the damage aimed at a protected entity. The script's killUnit3 / dmgUnit /
//  refund2 never pass through CalculateHitDamage, so the mission keeps every tool it has to remove these units itself.
//  Both hooks may run on worker threads: plain field reads, Interlocked counters, no Unity call, no allocation, no logging.
//  Repeated errors switch the hooks off and the damage goes back to vanilla. Nothing is ever unpatched.
//
//  Identification, and why there are two paths. Several of these units have no group name at all (RU_C01 #6751/#8843/#8844 and
//  the US_M05N LHA-6 all carry an empty group), so a name rule misses them. Every rule is therefore keyed on (mission uid, owner
//  player uid, DB unit id, group / cargo / tag / loadout option) - never on a group name alone, and never on an owner alone.
//  RU_C04 is the proof that the mission uid has to come first: its allied BMD-2K Batalin (c444) is owner c3 with an empty group,
//  exactly like three units this table protects in other missions, and protecting it would make RU_C04 unwinnable.
//   1. The main path is the map sweep (Sweep below), on the main thread, every 5 s. It is the SAME LuaMap.GetUnits pass the
//      liveness prune already made, so it costs nothing new, and it does not care when a unit appeared - a mission-editor unit
//      placed before the battle starts is found on the first pass exactly like one a script node spawns at minute forty.
//      It recognises a unit by its mission-editor uid (ScanUids), or by an (owner, DB unit id) pair proved unique across the
//      whole decrypted mission, narrowed further by a loadout option, a cargo unit or an engine group check.
//      That uid is the very number the mission file writes as "cNNN". Proof, from the player's own log of the RU_C01 battle of
//      2026-09-18 at 23:19:31: "[TRICHE] avions : première recharge de Mi-35M (uid 27, entité 991, type 8)" - that uid comes
//      from LuaUnit.UID on a unit of this same sweep (MunitionsAir.cs), and RU_C01's objects.json has c27 = "Mi-35M", owner c2,
//      the human player. Same numbering, live, on this build.
//   2. The fast path is the spawn hook on SpawnService.InvokeUnitSpawned, which gives the ECS entity and the spawning node's own
//      fields the moment a unit appears. It is kept because when it fires it is instant - but it must never be relied on alone:
//      the same log proves it did not fire once in a whole RU_C01 battle ("[SPAWN] bilan crochets : ... apparition exacte=0",
//      while its two neighbours on other methods counted 59 and 67). The method is private and static and IL2CPP most likely
//      inlines it away. Before the sweep existed, that made this whole module inert, and it is why the evacuation Mi-8 of
//      2026-09-18 was not protected.
//  A rule with no exact sweep key is NOT invented: it stays on the spawn hook alone, the arm line says so, and the end-of-battle
//  line reports it as never seen. Guessing a wider key would protect units the mission needs killable, which is the one thing
//  this module must never do.
//
//  Fail closed: a mission absent from the table, an unreadable mission uid, an owner that is the local player, or any doubt
//  about a match means this module does nothing at all. Log lines are French; four prefixes, [PROTECTION] and the three under
//  Tag below, because the PUBLIC build keeps one line per prefix every five minutes and the two lines a test is read from must
//  never be the ones it drops.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.Spawn;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Nodes.Spawn;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using CQC = Il2CppBrokenArrow.Client.Ecs.Infantry.Systems.CloseQuartersCombatSystem;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NodeCtl = Il2CppBrokenArrow.ScriptEngine.Loader.NodeController;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class ProtectionMission
    {
        // ------------------------------------------------------------ the one behaviour constant
        /// Damage a protected unit actually receives, as a factor of what the game computed.
        ///  0f  = shipped behaviour: the author asked for these transports to survive anything ("même s'il se prend 12 roquettes").
        ///  1f  = the conservative mode the decision note prefers (section 2.3): the unit stays mortal and only the mod's own
        ///        added anti-helicopter damage is what it no longer suffers. Change THIS constant and nothing else to switch.
        ///  Note for whoever flips it: AntiHeliPortee's own postfix runs at Priority.Last and MULTIPLIES the result by 1.5 or 2
        ///  for a low helicopter, after this one. So 1f is not literally vanilla; ~0.5f-0.67f is what cancels that multiplier.
        ///  0f is stable whatever the order, because every other postfix in the chain multiplies or only reads.
        ///  One value for every tier on purpose: one behaviour, one switch, one thing to explain in the patch notes.
        const float DamageFactor = 0f;

        // ------------------------------------------------------------ the tier switch
        /// The single place a tier is turned on or off.
        ///   0 = the module protects nothing anywhere, in every mission, as if it were not installed;
        ///   1 = RU_C01 only - exactly the five rules that shipped on 18/09;
        ///   2 = RU_C01 plus US_M07, US_M05N, RU_N03, RU_N02 and US_M03B. US_M03C and US_M06N are audited and written down in
        ///       the table but sit at tier 3: each one says in its own comment what has to be decided before it can ship.
        /// Lowering it puts the module straight back to the behaviour of the tier under it without touching a single rule.
        /// Rules carried at tier 3 are written down and verified but deliberately NOT shipped; see their comments before
        /// raising this, because each one has an open question that a table entry alone does not answer.
        const int TierMax = 2;

        // ================================================================ the table

        /// One rule = one set of units a mission needs alive. Every field left at 0 / null is simply not checked; a field that
        /// is set MUST match, so a rule can only ever get narrower, never wider. Group and Cargo are compared as exact tokens
        /// of a comma / semicolon / pipe separated list, never as substrings.
        sealed class Rule
        {
            internal byte Id;            // 1-based index inside its mission, used by the per-rule tally
            internal string Name;        // French label for the log
            internal int Tier;           // 0 = this rule ships with its mission; anything else overrides it (see TierMax)
            internal int Owner;          // PlayerOwner uid the spawn must carry (never the local player: checked at runtime)
            internal int DbId;           // DB unit id (spawn: SpawnData.UnitIDToSpawn ; sweep: LuaUnit.SpawnData.Unit.UnitID), 0 = not checked
            internal string Group;       // exact token of SpawnData.Groups or of the spawning node's UnitGroup
            internal string Cargo;       // exact token of the spawning node's CargoUnitGroup
            internal int Tag;            // spawning node's TargetTagUID, 0 = not checked
            internal int Expected;       // units this rule should recognise over a whole battle
            internal bool Progressive;   // spawned in slices, or respawned: Expected is a floor, not a count
            internal string Why;         // the evidence, printed with every unit taken under protection

            // --- the sweep key (Sweep below). A rule with neither ScanUids nor ScanKey is simply never taken by the sweep:
            // it then depends on the spawn hook alone, the arm line names it, and nothing is guessed in its place.
            internal int[] ScanUids;     // mission-editor uids ("cNNN") of PRE-PLACED units: the exact key, nothing else needed
            internal bool ScanKey;       // true only when Owner + the fields below were checked unique over the whole decrypted
                                         // mission; the sweep refuses a ScanKey rule that narrows nothing beyond its owner
            internal int ScanOption;     // loadout option id the unit must carry, 0 = not checked (RU_C01 #6751 against #6954)
            internal int ScanCargo;      // DB unit id the unit must carry as cargo, 0 = not checked (RU_C01 #8843/#8844)
            internal string ScanGroup;   // group the engine itself must confirm (EntitiesHelper.UnitHasGroup), null = not checked
        }

        /// One mission of the table. Tier is what TierMax compares against. Keeps is the French half-sentence that says out
        /// loud, in the arm line, what this mission deliberately leaves killable - the half the author has to be able to check.
        sealed class MissionDef
        {
            internal string Uid;
            internal int Tier;
            internal Rule[] Rules;
            internal string Keeps;
            internal string Consequence;  // one warning line, written once, the first time this mission protects anything
        }

        // --- RU_C01 constants, kept as they were: Classify below is the tier 1 matcher and must not change behaviour.
        const string Mission = "RU_C01";           // Campaign.MissionUid, compared trimmed and case-insensitively
        const int OwnerAllied = 3;                 // "Allied VDV" (c3). The player is uid 2 ("Player VDV") and is never protected.
        const int TagStation01 = 148;              // c148 Station_01: where #6751 puts the engineers' extraction Mi-8
        const int UnitMi8 = 145, UnitMi26 = 261, UnitIngener = 117;
        const string GroupIngener = "Ingener";     // exact token: "Ingener_Cover" (#9382/#9389) must NOT match
        const string GroupMi26Evac = "mi26 evac";
        const string GroupMi8Evac = "Mi8 Evac";
        const string PhaseSix = "Start phase 6";   // mission storage variable; #19097.on_update -> #18382.deact frees the engineers

        /// #6751 carries loadout option 264; the four fire-team extraction Mi-8s (#6886, #6918, #6954, #6990) carry 267.
        /// This is the only field that separates #6751 from #6954, which shares its owner, unit id, tag and empty group.
        /// Re-checked node by node in the decrypted mission: over ALL of c3's spawns, option 264 on a Mi-8 exists once, at #6751,
        /// and RU_C01 has no pre-placed c3 Mi-8 at all (c29/c30/c33/c34 are Mi-35M, c35/c36 are Ka-52). So (c3, 145, 264) names
        /// one helicopter and nothing else - which is what lets the sweep find THE unit whose death blocks the mission.
        /// Option 266, by contrast, is just "the troop-carrying loadout" and sits on fourteen other c3 Mi-8s: it is never a key.
        const int OptExtraction = 264, OptFireTeam = 267;

        const byte RExtraction = 1, RDelivery = 2, RIngener = 3, RMi26Evac = 4, RMi8Evac = 5;

        /// The table. Every line below was re-checked against the decrypted mission (nodes.json link pins and objects.json
        /// owners), not against the pre-made reports, which draw condition pins with the same arrow as flow pins.
        static readonly MissionDef[] Table =
        {
            // ---------------------------------------------------------------- RU_C01 Ignalina (tier 1, the mission that failed)
            // Matched by Classify, not by the generic matcher: the fire-team Mi-8s share owner, unit id, tag and empty group
            // with #6751 and are told apart by the loadout option alone. The rules are listed here for the tally and the log.
            new MissionDef
            {
                Uid = Mission, Tier = 1,
                Keeps = "centrale, garnison, gare et toutes les vagues ennemies restent destructibles",
                // Sweep keys: RU_C01's five sets are all SCRIPT spawns (spawnUnit2 nodes), so none of them has a mission-editor
                // uid - there is no "cNNN" to look for, and a uid list would be an invention. Each key below was instead read
                // out of the node it comes from and checked against every other c3 spawn of the mission:
                //   #6751  Mi-8 145, options 262/264/269   -> 264 is unique to it (see OptExtraction above)
                //   #8843/#8844  Mi-8 145 carrying unit 117 -> 117 is carried by these two nodes only, in the whole of c3
                //   Ingener squads  unit 117 owner c3        -> spawned by #8843/#8844 only; #9382/#9389 carry 116, not 117
                //   mi26 evac / Mi8 Evac                     -> named groups, confirmed live by the engine itself
                Rules = new[]
                {
                    new Rule { Id = RExtraction, Name = "Mi-8 d'extraction des ingénieurs (#6751)", Owner = OwnerAllied, Expected = 1,
                               DbId = UnitMi8, ScanKey = true, ScanOption = OptExtraction,
                               Why = "surveillant de mort #19110 sans aucune sortie" },
                    new Rule { Id = RDelivery,   Name = "Mi-8 de dépose des ingénieurs (#8843/#8844)", Owner = OwnerAllied, Expected = 2,
                               DbId = UnitMi8, ScanKey = true, ScanCargo = UnitIngener,
                               Why = "#8839 sans sortie si les deux tombent" },
                    new Rule { Id = RIngener,    Name = "escouades d'ingénieurs 'Ingener'", Owner = OwnerAllied, Expected = 2,
                               DbId = UnitIngener, ScanKey = true,
                               Why = "#18382 -> défaite immédiate à la première escouade perdue" },
                    new Rule { Id = RMi26Evac,   Name = "évacuation 'mi26 evac'", Owner = OwnerAllied, Expected = 2,
                               DbId = UnitMi26, ScanKey = true, ScanGroup = GroupMi26Evac,
                               Why = "#15385 -> une seule perte annule l'évacuation" },
                    new Rule { Id = RMi8Evac,    Name = "évacuation 'Mi8 Evac'", Owner = OwnerAllied, Expected = 5,
                               ScanKey = true, ScanGroup = GroupMi8Evac,
                               Why = "#15369 -> deux pertes annulent l'évacuation" },
                },
            },

            // ---------------------------------------------------------------- US_M07 Dam (tier 2, the highest value of the audit)
            // #17429 (GLT) and #17430 (GLT 1) are onUnitDead3 groupDead=True startup=True with NO deact input, both feeding
            // #17431 once -> 17434.playerLose (verified: #17434 has that single in-link). They can lose the mission from the
            // first second to the last. GLT 1 ends on a scripted on-foot exfiltration, #3381 moveSimple bl=True -> #3383
            // refund2, the same shape as RU_C01. Nothing in the mission needs either group dead: the only other nodes citing
            // them are objVisible2, setWay and triggerEnter.
            // Key: owner c3 "Miller's troops" owns exactly nine pre-placed units, seven LMTV (dbId 392) and exactly two Green
            // Light Team (dbId 473, c37 and c1048); its six spawn nodes carry dbIds 270 and 272 only. So (c3, 473) is these two
            // and nothing else. The allied Williams (c5) and Bennett (c4) groups the note warns about keep respawning, because
            // the owner filter excludes them structurally - they are not c3.
            new MissionDef
            {
                Uid = "US_M07", Tier = 2,
                Keeps = "Williams (c5) et Bennett (c4) continuent de réapparaître, les Tochka et toutes les troupes russes restent destructibles",
                Rules = new[]
                {
                    // Sweep key: the two uids themselves. Re-checked in objects.json - c37 "Green Light Team" group GLT and c1048
                    // "Green Light Team" group GLT 1, both player c3, both dbId 473; no other object of the mission is dbId 473
                    // and no c3 spawn node produces one (they carry 270 and 272). Pre-placed, so the uid is the exact key.
                    new Rule { Id = 1, Name = "Green Light Team 'GLT' et 'GLT 1' (troupes de Miller)", Owner = 3, DbId = 473, Expected = 2,
                               ScanUids = new[] { 37, 1048 },
                               Why = "#17429/#17430 -> #17431 -> 17434.playerLose, sans entrée de désactivation" },
                },
            },

            // ---------------------------------------------------------------- US_M05N Klaipeda (tier 2)
            // #81 onUnitDead3 targetUnit=c307, startup=True, no deact -> #22 dialog3 -> #847 endMission2.playerLose.
            // #87 triggerEnter unitF=c307 (zone c72) completes the task and unlocks the Crossroads. It starts at hp=75 and must
            // arrive: the textbook escort-that-must-arrive. Its group is empty, so only (owner, dbId) can name it.
            // Key: owner c3 "Marines" owns exactly ONE unit in the whole mission, c307 dbId 489, and has no spawn node at all.
            // The faction trap is handled by the owner filter: the nineteen Lithuanians sit on c4, on the player's own team, and
            // the static enemy units sit on c8 - neither is c3.
            // Consequence below is real and was accepted by the author in advance; it is logged as a warning so it survives the
            // PUBLIC log filter and he can still say no after reading his own test log.
            new MissionDef
            {
                Uid = "US_M05N", Tier = 2,
                Keeps = "DefLoop_A1/A2/B1/B2 et toutes les boucles de Bravo (c8) restent destructibles, sinon les carrefours ne seraient jamais pris",
                Consequence = "US_M05N : le LHA-6 endommagé (c307) est protégé. Conséquence assumée, à lire avant de valider : sa perte est "
                            + "la SEULE condition de défaite de cette mission (#81 -> #847), donc la mission ne peut plus être perdue et la "
                            + "médaille de coque est acquise d'office. Si ce n'est pas ce que tu veux, il suffit de retirer US_M05N du tableau.",
                Rules = new[]
                {
                    // Sweep key: the uid. Re-checked - c307 "LHA-6 Damaged", player c3, dbId 489, hp 75, empty group, and it is
                    // the only object of dbId 489 in the whole mission; c3 has no spawn node at all.
                    new Rule { Id = 1, Name = "LHA-6 endommagé (c307, Marines)", Owner = 3, DbId = 489, Expected = 1,
                               ScanUids = new[] { 307 },
                               Why = "#81 -> #847 playerLose ; arrive à hp=75 et doit atteindre sa zone (#87)" },
                },
            },

            // ---------------------------------------------------------------- RU_N03 Parnu (tier 2)
            // The IL-76s are the safest and highest-value protection of the whole audit: #3133/#3134/#3135 onUnitDead3 on c25,
            // c284 and c368, startup=True, no deact, -> #3136 or_x4 -> #3140 once -> #3146 playerLose. Those three nodes are the
            // ONLY nodes in the mission that mention these three units: no order, no kill, nothing needs them dead. They are
            // stationary at hp=200, and Parnu contains zero split_unit and zero nCmprPlr, so there is no friendly-fire gate: an
            // enemy kill loses the mission outright.
            // Key: owner c3 "RU Ally Forces" carries dbId 361 exactly three times, all three pre-placed IL-76MD (landed).
            // COLONEL HEL is a script spawn (#3419 spawnMultiUnits, owner c5, UnitGroup=COLONEL HEL, dbId 261) carrying the VIP
            // group COLONEL. #3424 moveComplex bl=True duringRide=StayLoaded -> #3613 dmgUnit group=COLONEL imrtl=True, which is
            // the script granting the VIP immortality only after landing, and #3434 refund2 gr=COLONEL HEL removing the
            // airframe. Shot down in flight, the cargo dies and #3606 -> #3136.d -> playerLose. Only two nodes cite COLONEL HEL,
            // the spawn and the refund, so nothing needs the helicopter dead. We protect the airframe and never the COLONEL
            // cargo: the script handles the VIP itself with imrtl, and section 2.3 says never to race it.
            // Not protected here: the LCACs (c189/c190/c191, owner c8) whose death IS the victory condition and which the script
            // deliberately un-protects at #4650, and RESERVES UAV / UAV BALT, which respawn.
            new MissionDef
            {
                Uid = "RU_N03", Tier = 2,
                Keeps = "les LCAC de l'USMC (c8) restent destructibles - leur destruction est la victoire - et les drones RESERVES UAV / UAV BALT continuent de réapparaître",
                Rules = new[]
                {
                    // Sweep key: the three uids. Re-checked - c25, c284 and c368 are the three "IL-76MD (landed)", player c3,
                    // dbId 361, hp 200, group IL-76, and no spawn node of the mission produces a 361. Pre-placed, so the uids.
                    new Rule { Id = 1, Name = "IL-76MD posés (c25, c284, c368)", Owner = 3, DbId = 361, Expected = 3,
                               ScanUids = new[] { 25, 284, 368 },
                               Why = "#3133/#3134/#3135 -> #3136 -> #3146 playerLose, aucun tir ami possible dans cette mission" },
                    // Sweep key: (c5, 261). A script spawn, so there is no uid to look for; instead c5 was walked node by node -
                    // it owns no pre-placed unit and seven spawn nodes, of which only #3419 carries dbId 261. The COLONEL cargo
                    // of that same node is dbId 480, so the pair names the airframe and never the VIP, which is what §2.3 asks.
                    new Rule { Id = 2, Name = "hélicoptère du Colonel 'COLONEL HEL' en vol", Owner = 5, DbId = 261, Group = "COLONEL HEL", Expected = 1,
                               ScanKey = true,
                               Why = "#3419 -> #3424 ordre bloquant chargé ; abattu en vol -> #3606 -> #3146 playerLose" },
                },
            },

            // ---------------------------------------------------------------- RU_N02 Tallin (tier 2)
            // The Ka-29 c32 carries the PDSS naval infantry under a blocking ride: #1738 moveComplex bl=True duringRide=
            // StayLoaded -> #3377 unloadCom Blocking=True -> #1740 refund2, with #4 changeAltAirCom putting it at a scripted low
            // altitude - the RU_C01 shape exactly. If it falls loaded, the PDSS die with it and #446 onUnitDead3 groups2=PDSS
            // -> ... -> #475 playerLose. Only four nodes in the mission mention c32 and NONE of them is a death watcher, so
            // nothing needs it dead.
            // THE TRAP, and why Group is mandatory on this rule: owner c3 also spawns TALLIN HELP HELI at #2067 with dbIds
            // 273/273 - two more Ka-29s, same owner, same DB unit id, and that group is a respawning help group the mission
            // needs killable. (c3, 273) alone would protect them. The pre-placed helicopter carries group "START HELI", which is
            // cited by no node at all (the script drives c32 by unit reference), so it is a safe discriminator and the
            // respawning ones cannot match it.
            // Window: the note asks for protection to stop at the unload (#3377). This module has no unload signal, so
            // protection instead lasts until the script's own #1740 refund2 removes the unit, which the sweep notices. The extra
            // window is an empty helicopter flying home invulnerable; since no node watches c32's death, it changes nothing.
            // NOT taken: RU Crew 15 (c328). The table proposes it, the graph refuses it - see the block after the table.
            new MissionDef
            {
                Uid = "RU_N02", Tier = 2,
                Keeps = "COLUMN 1, les groupes d'aide NARVA/TALLIN et BATALIN HELP continuent tous de réapparaître, et l'équipage RU Crew 15 reste mortel",
                Rules = new[]
                {
                    // Sweep key: the uid c32, and that is the whole point here. "START HELI" is not written in a single node of
                    // this mission - it exists only as the "group" field of the pre-placed object c32 in objects.json - so the
                    // spawn-hook branch of this rule needs SpawnData.Groups to carry an editor group, which nothing proves. The
                    // sweep does not need it: c32 is pre-placed, it is the only c3 object of dbId 273, and the uid names it
                    // exactly, while the two respawning Ka-29 of "TALLIN HELP HELI" (#2067, same owner, same dbId 273) simply
                    // are not c32 and stay killable.
                    new Rule { Id = 1, Name = "Ka-29 de la PDSS (c32, 'START HELI')", Owner = 3, DbId = 273, Group = "START HELI", Expected = 1,
                               ScanUids = new[] { 32 },
                               Why = "#1738 ordre bloquant chargé -> #3377 -> #1740 ; sa chute tue la PDSS -> #446 -> #475 playerLose" },
                },
            },

            // ---------------------------------------------------------------- US_M03B Airbase (tier 2)
            // heist is the MH-47 that recovers the Su-57: #693 spawnUnit2 owner c3, UnitGroup=heist, dbId 274 - the only dbId
            // 274 anywhere in c3's roster, pre-placed or spawned. Its two death watchers are both penalties: #1209 (maxEv=1,
            // startup=False) writes "Su57 destroyed" which only ever closes gates, skips dialogue and refunds the AC-130, and
            // #1176 (maxEv=-1) writes "HeistKilled", read once by #1802 to close gate #1789, and sends a second helicopter via
            // the flipflop #1178 -> #693. Verified that no waitCond2 / and3 / andx4 waits on either state to advance: every
            // reader of both states closes something or skips a line. So protecting heist keeps the success path alive instead
            // of blocking anything. Progressive because #1176 can spawn it a second time and the hook re-arms on that spawn.
            new MissionDef
            {
                Uid = "US_M03B", Tier = 2,
                Keeps = "finalAttack, les SAM, les décollages et toutes les garnisons de c5/c6 restent destructibles, sinon les zones ne seraient jamais prises",
                Rules = new[]
                {
                    // Sweep key: (c3, 274). A script spawn, so no uid; dbId 274 was searched across the whole mission and appears
                    // exactly once, at #693, owner c3 - c3's nine pre-placed units are 420/305/65/342 and its three other spawn
                    // nodes carry 293 and 301. The pair is exact, and it re-arms by itself on the #1176 -> #693 second spawn.
                    new Rule { Id = 1, Name = "MH-47 de récupération 'heist'", Owner = 3, DbId = 274, Group = "heist", Expected = 1, Progressive = true,
                               ScanKey = true,
                               Why = "#1209 -> tâche 'Récupérer le Su-57' en échec ; tous les lecteurs de l'état ne font que fermer des portes" },

                    // Tier 3, written down but deliberately not shipped. SU57 (owner c190 "Neutral units", dbId 451) is the one
                    // tier 2 candidate the script destroys ON PURPOSE: #774 killUnit3 dis=False, next to #761 killUnit3 dis=True
                    // which recovers it. Section 2.2's own hard constraint is "never protect a unit the script kills itself".
                    // Section 2.3 says killUnit3 bypasses the damage hook and both paths would keep working - true, but the
                    // Su-57 is also the object of a timed search (#2782 timer) whose outcome #585 testCond2 reads, and its loss
                    // costs a task, never the mission. Poor trade, unusual surface (a third faction), so it stays off.
                    new Rule { Id = 2, Tier = 3, Name = "Su-57 (non livré : le script le détruit lui-même)", Owner = 190, DbId = 451, Group = "SU57", Expected = 1,
                               Why = "#1191 -> 'Su57 destroyed' ; #774 killUnit3 dis=False le détruit volontairement" },
                },
            },

            // ---------------------------------------------------------------- US_M03C Frontiers (tier 3: verified, NOT shipped)
            // Column is the escort the mission is named after: #3024 triggerEnter is one of only two victory paths and #2678
            // onUnitDead3 only fails the task "Protect the column" (-> #2683 updateTask3 Fail, plus dialogue and one gate).
            // #2691 countUnits4 grf=Column leads to #2690 getUnitHp -> #2692 equal -> a warning line and a pulse, so the counter
            // is an alarm, not a gate. Nothing in the script needs a Column vehicle dead, and nothing here can block.
            // Key: owner c218 "Column" is a player object that owns NOTHING but this column - zero pre-placed units and twelve
            // spawnUnit2 nodes, all of them UnitGroup=Column. So the owner alone is already exact; the group token is kept as a
            // second lock. The key is not the problem.
            // WHY IT DOES NOT SHIP, and this is the whole reason: those twelve nodes carry Quantity 4+4+3+4+4+3+4+4+3+4+4+4 = 45
            // vehicles, counted one by one in the decrypted mission. At DamageFactor 0 that is forty-five allied armoured
            // vehicles the player would watch clear the map without ever losing one. It is not a softlock and it is not unsafe -
            // it is a change of the mission's difficulty far bigger than anything else in this table, and nobody asked for it.
            // Tier 3 holds it until the author says yes; Consequence below is the text he would read the first time it fired.
            // To ship it: change Tier to 2 here. Nothing else has to move.
            new MissionDef
            {
                Uid = "US_M03C", Tier = 3,
                Keeps = "FinalAttack (c217), les défenses statiques c5 et les 21 boucles de contre-attaque restent destructibles",
                Consequence = "US_M03C : la colonne blindée 'Column' est protégée. Conséquence assumée, à lire avant de valider : cette "
                            + "colonne compte 45 véhicules (12 apparitions de 3 ou 4 véhicules), et ils deviennent tous indestructibles "
                            + "jusqu'à la fin de la mission. Rien ne se bloque, mais la mission devient nettement plus facile. "
                            + "Si ce n'est pas ce que tu veux, il suffit de laisser US_M03C au palier 3.",
                Rules = new[]
                {
                    new Rule { Id = 1, Name = "colonne blindée 'Column' (non livré : 45 véhicules alliés deviendraient indestructibles)",
                               Owner = 218, Group = "Column", Expected = 1, Progressive = true, ScanKey = true, ScanGroup = "Column",
                               Why = "#3024 = une des deux voies de victoire ; #2678 ne fait qu'échouer la tâche" },
                },
            },

            // ---------------------------------------------------------------- US_M06N River (tier 3: verified, NOT shipped)
            // The note lists AllyConvoy as "protect, optional" and says in the same sentence that its loss is not fatal, because
            // #5855 reaches the same ending. Three findings say leave it alone, and all three are in the mission file:
            //  1. no benefit: nothing is lost when it dies, so the rule buys nothing;
            //  2. owner c9 "Attack Base" also spawns CounterAttack 1 and CounterAttack 2 at twelve nodes, and those feed an and3
            //     (#4926/#4928) that needs BOTH - protecting them is a hard softlock. The dbId sets happen to be disjoint
            //     (convoy 54/78/84 against counter-attack 9/120/202/214/215/365/398/404) so the key below is exact, but the
            //     margin is one typo wide for zero gain;
            //  3. it would not even work: c9 sits on BRAVO team, and the sweep only enumerates the player's own team, so a
            //     protected convoy vehicle would never be found by it and would be dropped 15 seconds after any hook added it.
            // Raising TierMax to 3 would enable this without fixing point 3. Do not, until the sweep is taught to enumerate the
            // owner's own player uid instead of the player's team.
            new MissionDef
            {
                Uid = "US_M06N", Tier = 3,
                Keeps = "les convois ConvoyTrain/ConvoyRetreat doivent absolument rester destructibles : un convoi qui s'échappe fait perdre la mission (#5928)",
                Rules = new[]
                {
                    new Rule { Id = 1, Name = "colonne de secours 'AllyConvoy' (non livré : sans effet et mal placée)", Owner = 9, Group = "AllyConvoy", Expected = 24,
                               Why = "sa perte n'est pas fatale (#5855 mène à la même fin)" },
                },
            },
        };

        // ---------------------------------------------------------------- deliberately absent from the table, with the reason
        //  RU_N02 "RU Crew 15" (c328). The note lists it as a tier 2 PROTECT. The graph says no: #1697 assignUnitToDeck2
        //    player=c2 targetUnit=c328 hands that very unit to the HUMAN player (and #2930 later to c3). Correction C6 of the
        //    note is explicit - a player-owned unit is never auto-protected, that is what Resistance.cs is for, under the
        //    player's own toggle. Its death costs a secondary task only (#1687 -> #3290 or2). Dropped.
        //  RU_C01 fire-team Mi-8s (#6886/#6918/#6954/#6990). Tier 2 in the note, harmless either way (#6868 always exits).
        //    Classify still rejects them on loadout option 267, and the sweep never sees them either, because the extraction
        //    rule asks for option 264 and refuses when the loadout cannot be read instead of widening. Tier 1 stays tier 1.
        //  RU_C01 delivery Mi-8s of the COVER squads (#9382/#9389). They carry unit 116, not 117, so the cargo key of the
        //    delivery rule cannot reach them, and their option 266 is shared by fourteen other Mi-8s and is never a key.
        //  US_M03C's armoured column. In the table, keyed exactly, and deliberately held at tier 3: forty-five allied vehicles
        //    at once is a change of difficulty the author has not agreed to. Its own comment says how to turn it on.
        //  The eleven missions with no PROTECT entry at all - RU_C02, RU_C03, RU_C04, RU_N01, RU_N04, US_M01, US_M02, US_M03A,
        //    US_M04, US_M05S, US_M06S - are simply not in the table. The module never arms, never patches, never logs, never
        //    sweeps there: Find returns null, Tick leaves at once, and the sweep is only ever called with a mission in hand.
        //    RU_C04 is the one that matters: its allied BMD-2K Batalin (c444, owner c3, empty group, placed imrtl=true and
        //    deliberately cleared by #7396) MUST die for #12194 FINAL WAVE to spawn, and #12194 has exactly one in-link.
        //    Re-checked after the sweep was added: no rule of this module is keyed on an owner or a group alone, every key is
        //    read out of _active's own rules, and _active can only ever be a mission the table names - so nothing the sweep
        //    does can reach c444. RU_C04 keeps protecting nothing, which is what keeps it winnable.

        const int MaxErrors = 50;
        const float SummaryEvery = 30f;            // damage summary cadence, as the note asks (never log inside the hook)
        const float PruneEvery = 5f;               // map sweep: identifies what the rules name, drops what the battle no longer has
        const long PruneGraceMs = 15000;           // a unit younger than this is never dropped: the Lua map can lag the ECS entity
        const float TickEvery = 0.5f;
        const int ScanErrorMax = 40;               // the sweep gives up for the session rather than fighting an unreadable map

        /// Four log prefixes, not one, and the reason is the PUBLIC build the author plays and shares. Its filter (ModLog.cs)
        /// keeps ONE line per bracketed prefix every five minutes, so three install lines written back to back left only the
        /// first in his log of 2026-09-18 and nothing else of this module was ever visible. The two lines he has to be able to
        /// read after a test - which tier armed, and what was recognised - now sit under their own prefix and are written once
        /// per battle each, so no window can swallow them. Warnings are never filtered and keep the plain prefix.
        const string Tag = "[PROTECTION]";
        const string TagArm = "[PROTECTION ARME]";
        const string TagUnit = "[PROTECTION UNITE]";
        const string TagBilan = "[PROTECTION BILAN]";
        /// Heartbeat: Frame is only reached while the mod is on, inside a campaign mission. If it stops being called - the player
        /// switched the mod off from its tab, the module gave up, the battle is gone - the protection lapses on its own and the
        /// damage goes straight back to vanilla, without anything having to reach in and disarm it. Environment.TickCount64 is a
        /// plain OS read, safe on a worker thread, and it is what AntiHeliPortee already uses on its own hot path.
        const long BeatMaxAgeMs = 3000;

        // ------------------------------------------------------------ hook side: plain values only, read on worker threads
        static volatile bool _armed;               // true only inside a table mission, mod on, patches in place
        static volatile bool _disabled;            // error kill-switch: back to vanilla damage until the game restarts
        static volatile int[] _ids = Array.Empty<int>();   // protected EntityIds, republished whole (never mutated in place)
        static volatile MissionDef _active;        // the mission the hooks are arming for, null when there is none
        static long _shotCalls, _shotHits, _meleeCalls, _meleeHits, _errors, _rawMilli;
        static long _beat;                         // last main-thread tick, in Environment.TickCount64 (see BeatMaxAgeMs)

        /// Hook side, any thread: armed, no error kill-switch, mod still ticking, mission not one played without the mod.
        static bool Live() => _armed && !_disabled && _active != null && !Campaign.MissionInerte
                              && Environment.TickCount64 - Interlocked.Read(ref _beat) <= BeatMaxAgeMs;

        // ------------------------------------------------------------ main thread
        sealed class Entry { internal int Eid; internal int Uid; internal int DbId; internal Rule R; internal long At; }
        struct Note { internal int Uid, Eid, DbId, Tag; internal Rule R; internal bool Me; internal string Groups, Why; }

        static readonly object _sync = new();
        static readonly List<Entry> _list = new();
        static readonly ConcurrentQueue<Note> _notes = new();
        static int[] _seen = Array.Empty<int>();
        static HarmonyLib.Harmony _harmony;
        static bool _triedPatch, _okSpawn, _okShots, _okMelee, _disabledLogged, _armLogged, _endLogged, _frameDead;
        static bool _wasArmed, _refusalLogged, _consequenceLogged;
        static bool _ingenerReleased, _phaseBaselineTaken;
        static string _phaseBaseline;
        static float _nextTick, _nextSummary, _nextPrune;
        static long _lastShotHits, _lastMeleeHits, _lastRawMilli;
        static int _localUid = -1, _playerSide = -1, _pruned, _refused;
        static IntPtr _battle;
        static LuaMap _map;
        static GameController _gc;                 // the battle read by the current Tick: the sweep needs it for the group helper
        static volatile int _mainThread = -1;      // named by Frame alone; a watchdog call on any other thread stands down
        static int _scanTaken, _scanErrors;        // units the sweep took under protection, and how badly it is failing
        static bool _scanBroken, _scanBrokenLogged, _quitLogged;
        static bool _groupBroken;                  // the engine's group helper is unreadable: rules that need a group stop matching

        // ================================================================ hot paths

        /// Any thread: static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo,
        /// Single penetration, Boolean forceTopArmorAttack, ArmorSides armorSide). Scales the damage aimed at a protected unit.
        static void DamagePostfix(EcsEntity target, ref float __result)
        {
            if (!Live()) return;
            try
            {
                Interlocked.Increment(ref _shotCalls);
                if (!(__result > 0f)) return;
                var ids = _ids;
                if (ids == null || ids.Length == 0) return;
                int id = target.EntityId;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] != id) continue;
                    Interlocked.Add(ref _rawMilli, (long)(__result * 1000f));
                    __result *= DamageFactor;
                    Interlocked.Increment(ref _shotHits);
                    return;
                }
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) _disabled = true;   // repeated errors: back to vanilla damage
            }
        }

        /// Any thread: instance Void DeductHitPoints(Entity& target, Entity& shooter, Single incomingDamage, Single stressDamage).
        /// Entity is a blittable struct (Int16 Version, Int16 WorldId, Int32 EntityId), so taking it by reference is safe here.
        /// Stress damage is left alone: a protected transport may still be suppressed, it just does not lose hit points.
        static void MeleePrefix(ref EcsEntity target, ref float incomingDamage)
        {
            if (!Live()) return;
            try
            {
                Interlocked.Increment(ref _meleeCalls);
                if (!(incomingDamage > 0f)) return;
                var ids = _ids;
                if (ids == null || ids.Length == 0) return;
                int id = target.EntityId;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] != id) continue;
                    Interlocked.Add(ref _rawMilli, (long)(incomingDamage * 1000f));
                    incomingDamage *= DamageFactor;
                    Interlocked.Increment(ref _meleeHits);
                    return;
                }
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) _disabled = true;
            }
        }

        // ================================================================ spawn hook

        /// static Void InvokeUnitSpawned(Entity& newUnit, SpawnData spawnData). Not a hot path (a handful of calls per battle),
        /// but it may not be the main thread: property reads and a lock only, no Unity call and no logging. What is worth a log
        /// line is queued in _notes and written by Frame. newUnit is the ECS entity the damage hooks match on - the unit uid
        /// alone would be useless to them.
        static void SpawnPostfix(ref EcsEntity newUnit, SpawnData spawnData)
        {
            if (spawnData == null || !Live()) return;
            try
            {
                var def = _active;
                if (def == null) return;
                int eid = newUnit.EntityId;
                if (eid <= 0) return;

                int owner = -1;
                try { var o = spawnData.OwnerInfo; if (o != null) owner = o.UID; } catch { }
                if (!OwnerWanted(def, owner)) return;                   // only owners this mission's rules name, never the rest
                if (_localUid >= 0 && owner == _localUid) { Interlocked.Increment(ref _refused); return; }

                int dbId = 0; try { dbId = spawnData.UnitIDToSpawn; } catch { }
                string groups = null; try { groups = spawnData.Groups; } catch { }
                bool me = false; try { me = spawnData.IsMEUnit; } catch { }

                SpawnNodeData node = null; try { node = spawnData.SpawnNodeData; } catch { }
                int tag = 0; string nodeGroup = null, nodeCargo = null;
                if (node != null)
                {
                    try { tag = node.TargetTagUID; } catch { }
                    try { nodeGroup = node.UnitGroup; } catch { }
                    try { nodeCargo = node.CargoUnitGroup; } catch { }
                }

                Rule rule; string why;
                if (IsRuC01(def))                      // RU_C01 keeps its own matcher, unchanged
                {
                    byte id = Classify(spawnData, dbId, groups, tag, nodeGroup, nodeCargo, out why);
                    rule = id == 0 ? null : RuleOf(def, id);
                }
                else rule = Match(def, owner, dbId, groups, tag, nodeGroup, nodeCargo, out why);
                if (rule == null) return;

                int uid = 0; try { uid = spawnData.UID; } catch { }
                lock (_sync)
                {
                    for (int i = 0; i < _list.Count; i++) if (_list[i].Eid == eid) return;   // same entity twice: keep one entry
                    // Environment.TickCount64, not Unity time: the hook may not be on the main thread. Used by Prune only.
                    _list.Add(new Entry { Eid = eid, Uid = uid, DbId = dbId, R = rule, At = Environment.TickCount64 });
                    Publish();
                }
                if (_notes.Count < 64)
                    _notes.Enqueue(new Note { Uid = uid, Eid = eid, DbId = dbId, Tag = tag, R = rule, Me = me, Groups = groups, Why = why });
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) _disabled = true;
            }
        }

        /// True when at least one SHIPPED rule of this mission names that owner. Everything else leaves at once, which is what
        /// keeps a mission's untouched factions - Williams and Bennett in US_M07, the Lithuanians in US_M05N - out of the set.
        /// The tier filter matters here and not only in the reports: without it US_M03B's held-back Su-57 rule would let owner
        /// c190 through and then protect it.
        static bool OwnerWanted(MissionDef def, int owner)
        {
            if (owner < 0) return false;
            var rules = def.Rules;
            for (int i = 0; i < rules.Length; i++) if (rules[i].Owner == owner && RuleTier(def, rules[i]) <= TierMax) return true;
            return false;
        }

        static Rule RuleOf(MissionDef def, byte id)
        {
            var rules = def.Rules;
            for (int i = 0; i < rules.Length; i++) if (rules[i].Id == id) return rules[i];
            return null;
        }

        /// The generic matcher, used by every mission except RU_C01. A rule matches only when every field it sets matches;
        /// a rule that sets nothing beyond the owner would match that owner's whole army, which is why every rule in the table
        /// above sets at least a DB unit id or a group, and why the two that set only one of them were proved unique first.
        static Rule Match(MissionDef def, int owner, int dbId, string groups, int tag, string nodeGroup, string nodeCargo, out string why)
        {
            why = null;
            var rules = def.Rules;
            for (int i = 0; i < rules.Length; i++)
            {
                var r = rules[i];
                if (RuleTier(def, r) > TierMax) continue;       // a rule held back above TierMax must never match, only be read
                if (r.Owner != owner) continue;
                // dbId 0 means the spawn did not say: a rule that names a unit id then refuses rather than guesses (fail closed)
                if (r.DbId != 0 && dbId != r.DbId) continue;
                if (r.Group != null && !HasToken(groups, r.Group) && !HasToken(nodeGroup, r.Group)) continue;
                if (r.Cargo != null && !HasToken(nodeCargo, r.Cargo)) continue;
                if (r.Tag != 0 && tag != r.Tag) continue;
                why = Because(r);
                return r;
            }
            return null;
        }

        /// What the log prints as the key that matched, so a line in the player's log can be checked against the table by eye.
        static string Because(Rule r)
        {
            var sb = new System.Text.StringBuilder("propriétaire c").Append(r.Owner);
            if (r.DbId != 0) sb.Append(", unitéDB ").Append(r.DbId);
            if (r.Group != null) sb.Append(", groupe '").Append(r.Group).Append('\'');
            if (r.Cargo != null) sb.Append(", cargaison '").Append(r.Cargo).Append('\'');
            if (r.Tag != 0) sb.Append(", tag ").Append(r.Tag);
            return sb.ToString();
        }

        /// RU_C01's own table, unchanged. Returns 0 for everything the mission does not need alive - which is everything except
        /// five cases. The group rules come first because a group name, when there is one, is the most specific thing the spawn
        /// carries.
        static byte Classify(SpawnData sd, int dbId, string groups, int tag, string nodeGroup, string nodeCargo, out string why)
        {
            why = null;
            // The evacuation groups are named, so the name is the key. The unit id only has to not contradict it: 0 means the
            // spawn did not say, and a helicopter this group never contains means the mission file changed under us - refuse.
            if (HasToken(groups, GroupMi26Evac) || HasToken(nodeGroup, GroupMi26Evac))
            {
                if (dbId != 0 && dbId != UnitMi26) return 0;
                why = "groupe '" + GroupMi26Evac + "'"; return RMi26Evac;
            }

            if (HasToken(groups, GroupMi8Evac) || HasToken(nodeGroup, GroupMi8Evac))
            {
                if (dbId != 0 && dbId != UnitMi8 && dbId != UnitMi26) return 0;
                why = "groupe '" + GroupMi8Evac + "'"; return RMi8Evac;
            }

            // exact token only: 'Ingener_Cover' (#9382/#9389) is a different group and is never protected
            if (HasToken(groups, GroupIngener)) { why = "groupe '" + GroupIngener + "'"; return RIngener; }

            if (Same(nodeCargo, GroupIngener))
            {
                if (dbId == UnitIngener) { why = "cargaison '" + GroupIngener + "' (escouade)"; return RIngener; }
                if (dbId == UnitMi8) { why = "noeud de dépose, cargaison '" + GroupIngener + "', tag " + tag; return RDelivery; }
                return 0;
            }

            // #6751: the engineers' extraction Mi-8. Owner, unit id, tag and empty group are shared with #6954 (a fire-team
            // extraction Mi-8, tier 2 in the note), so the loadout option is what separates them: 264 here, 267 on the four
            // fire-team ones.
            if (dbId == UnitMi8 && tag == TagStation01 && Blank(nodeGroup) && Blank(nodeCargo))
            {
                int opt = ReadOption(sd);
                if (opt == OptFireTeam) return 0;                                     // #6886/#6918/#6954/#6990: left alone
                if (opt == OptExtraction) { why = "tag Station_01 + option " + OptExtraction; return RExtraction; }
                // Loadout unreadable: protect anyway rather than leave THE softlock unfixed. The only unit this can add is a
                // fire-team Mi-8, which the audit certifies as harmless to protect. Said out loud in the log, never silently.
                why = "tag Station_01, option illisible -> élargi (peut inclure un Mi-8 d'extraction d'équipe, sans danger)";
                return RExtraction;
            }
            return 0;
        }

        /// The spawn's loadout options (ICollection<Int32>); returns OptExtraction or OptFireTeam when one of them is there,
        /// 0 when the list cannot be read. Read the way Missions.ReadIds reads an ENGINE collection: TryCast to the concrete
        /// types first. Wrapping a foreign pointer in IReadOnlyList<int> only works when the object really implements that
        /// interface, which nothing guarantees here; a failed read must land on the audited "option illisible" branch.
        static int ReadOption(SpawnData sd)
        {
            try
            {
                var coll = sd.OptionIds;
                if (coll == null) return 0;
                try
                {
                    var l = coll.TryCast<Il2CppSystem.Collections.Generic.List<int>>();
                    if (l != null)
                    {
                        int n = l.Count;
                        for (int i = 0; i < n && i < 32; i++) { int o = l[i]; if (o == OptExtraction || o == OptFireTeam) return o; }
                        return 0;
                    }
                }
                catch { }
                try
                {
                    var a = coll.TryCast<Il2CppStructArray<int>>();
                    if (a != null)
                    {
                        int n = a.Length;
                        for (int i = 0; i < n && i < 32; i++) { int o = a[i]; if (o == OptExtraction || o == OptFireTeam) return o; }
                        return 0;
                    }
                }
                catch { }
            }
            catch { }
            return 0;
        }

        static bool Blank(string s) => string.IsNullOrWhiteSpace(s);
        static bool Same(string a, string b) => a != null && string.Equals(a.Trim(), b, StringComparison.Ordinal);

        /// Exact membership in a comma / semicolon / pipe separated group list, the same splitting Spawns uses.
        static bool HasToken(string list, string token)
        {
            if (string.IsNullOrEmpty(list)) return false;
            if (string.Equals(list.Trim(), token, StringComparison.Ordinal)) return true;
            foreach (var part in list.Split(',', ';', '|')) if (string.Equals(part.Trim(), token, StringComparison.Ordinal)) return true;
            return false;
        }

        /// Republishes the id array whole. Callers hold _sync. The hook only ever reads the published reference.
        static void Publish()
        {
            var a = new int[_list.Count];
            for (int i = 0; i < _list.Count; i++) a[i] = _list[i].Eid;
            _ids = a;
        }

        /// The mission's shipped definition, or null. Two independent reasons to return null, and both mean "do nothing at all":
        /// the uid is not in the table, or its tier is above TierMax. A mission whose every rule sits above TierMax is treated
        /// as absent, so the module never arms - and never logs - for a mission it would protect nothing in.
        static MissionDef Find(string uid)
        {
            if (TierMax <= 0 || string.IsNullOrWhiteSpace(uid)) return null;
            string u = uid.Trim();
            for (int i = 0; i < Table.Length; i++)
            {
                var d = Table[i];
                if (d.Tier > TierMax) continue;
                if (!string.Equals(u, d.Uid, StringComparison.OrdinalIgnoreCase)) continue;
                return Shipped(d) > 0 ? d : null;
            }
            return null;
        }

        static int Shipped(MissionDef d)
        {
            int n = 0;
            for (int i = 0; i < d.Rules.Length; i++) if (RuleTier(d, d.Rules[i]) <= TierMax) n++;
            return n;
        }

        /// A rule's own tier: its own value when it sets one, its mission's otherwise. Only US_M03B uses this today, shipping at
        /// tier 2 with its MH-47 rule while holding the Su-57 rule back at tier 3.
        static int RuleTier(MissionDef d, Rule r) => r.Tier > 0 ? r.Tier : d.Tier;

        /// RU_C01 is the one mission with its own matcher (Classify) and its own release rule (the engineers at phase 6).
        static bool IsRuC01(MissionDef d) => d != null && string.Equals(d.Uid, Mission, StringComparison.Ordinal);

        // ================================================================ frame (main thread)

        /// Called every frame from Spawns.Frame, which Mod.OnUpdate only reaches inside a campaign mission with the mod on.
        /// Self-throttled, never throws: one failure puts the module to sleep for the rest of the session rather than spam.
        internal static void Frame()
        {
            if (_frameDead) return;
            // Frame owns the value, the way AntiHeliPortee's does: Mod.OnUpdate reaches it and nothing else, so this IS the
            // Unity main thread by construction. ChienDeGarde only ever compares against it, and never sets it - a watchdog
            // wired to a script-engine hook could otherwise name the wrong thread and lock the real one out for good.
            _mainThread = Environment.CurrentManagedThreadId;
            try
            {
                // The same gate Mod.OnUpdate puts in front of Spawns.Frame, repeated here because the watchdog way in does not
                // go through it: switching the mod off from its tab must put the damage back to vanilla whoever is calling.
                // It sits before the heartbeat on purpose - a stale heartbeat is what makes a hook already in flight stand down.
                if (Identite.Blocked || Mod.AntiCheatActive || Mod.Actif == null || !Mod.Actif.Value) { if (_armed) Disarm(); return; }
                // the heartbeat then, and every frame: it is what keeps the hooks alive, so it must not sit behind the throttle
                Interlocked.Exchange(ref _beat, Environment.TickCount64);
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now < _nextTick) return;
                _nextTick = now + TickEvery;
                Tick(now);
            }
            catch (Exception e)
            {
                _frameDead = true;
                Disarm();
                try { Mod.Log.Warning(Tag + " suivi arrêté pour cette session : " + e.GetBaseException().Message); } catch { }
            }
        }

        /// A second way in, for whoever wires it (the script engine's own per-node call is the path BilanMission proved alive on
        /// this build). It exists because this module already went a whole battle doing nothing, and one driver is one single
        /// point of failure. It does exactly what Frame does - the throttle inside Frame means a second caller costs one clock
        /// read - but ONLY on the thread Frame has already named, and it never names one itself: a watchdog hook that ran
        /// somewhere else would otherwise lock the real main thread out and kill the module instead of saving it.
        internal static void ChienDeGarde()
        {
            if (_frameDead || _mainThread < 0) return;
            if (Environment.CurrentManagedThreadId != _mainThread) return;
            Frame();
        }

        static void Tick(float now)
        {
            // ---- which battle are we in?
            IntPtr battle = IntPtr.Zero;
            int localUid = -1, side = -1;
            GameController held = null;
            try
            {
                var gc = GameController._instance;
                var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
                if (gc != null && cp != null) { battle = gc.Pointer; localUid = cp.UID; side = (int)cp.TeamSide; held = gc; }
            }
            catch { }
            if (battle != _battle) { EndOfBattle(); _battle = battle; }
            _localUid = localUid;
            _playerSide = side;
            _gc = held;                                // the sweep asks it for the engine's group helper; never kept past a battle

            // ---- should this mission be protected at all? Anything unexpected means: nothing at all.
            string uid = Campaign.MissionUid;
            MissionDef def = Campaign.InCampaign && !Campaign.MissionInerte && battle != IntPtr.Zero ? Find(uid) : null;
            if (def != null && localUid >= 0 && OwnerWanted(def, localUid))
            {
                // a protected owner turned out to be the human player: refuse the whole mission rather than shield his units
                if (!_refusalLogged)
                {
                    _refusalLogged = true;
                    Mod.Log.Warning($"{Tag} REFUS mission {uid} : le joueur local est c{localUid}, un propriétaire du tableau, aucune unité protégée");
                }
                def = null;
            }
            // Nothing to protect here any more. A battle this module armed for gets its end-of-battle line written NOW, because
            // this branch is reached by ways that never change the GameController pointer - leaving the campaign, a mission
            // turning inert - and the report would otherwise be lost for good. EndOfBattle disarms through ResetSession.
            if (def == null) { if (_armed || _wasArmed) EndOfBattle(); else { _active = null; _gc = null; } return; }

            // The mission changed while the battle pointer did not (a reused GameController address is all it takes). Close the
            // old mission properly and start clean: carrying one mission's rules into another is the RU_C04 accident itself.
            if (_armed && !ReferenceEquals(_active, def)) EndOfBattle();

            // The end-of-battle signal already closed this session: Spawns.Frame (our caller) has no BattleOver guard,
            // so without this the module would re-arm on the end screen, log a second "armé" line and a second report -
            // the second one lying, because the units are no longer readable there.
            if (Campaign.BattleOver) { _active = null; _gc = null; return; }

            if (!_armed)
            {
                if (!EnsurePatched()) return;
                _active = def;
                if (_seen.Length != def.Rules.Length + 1) _seen = new int[def.Rules.Length + 1];
                _armed = true;
                _wasArmed = true;
                if (!_armLogged)
                {
                    _armLogged = true;
                    int rules = 0, units = 0, scanned = 0;
                    var owners = new List<int>();
                    var hookOnly = new System.Text.StringBuilder();
                    for (int i = 0; i < def.Rules.Length; i++)
                    {
                        var r = def.Rules[i];
                        if (RuleTier(def, r) > TierMax) continue;
                        rules++; units += r.Expected;
                        if (Scannable(r)) scanned++;
                        else { if (hookOnly.Length > 0) hookOnly.Append(", "); hookOnly.Append(r.Name); }
                        if (!owners.Contains(r.Owner)) owners.Add(r.Owner);
                    }
                    var who = new System.Text.StringBuilder();
                    for (int i = 0; i < owners.Count; i++) { if (i > 0) who.Append('/'); who.Append('c').Append(owners[i]); }
                    // Its own prefix, and it is the first thing to look for after a test: it says which tier armed, how many of
                    // its rules the map sweep can find on its own, and which ones still depend on the spawn hook alone.
                    Mod.Log.Msg($"{TagArm} mission {uid} reconnue : palier {def.Tier} armé, {rules} règle(s), " +
                        $"{units} unité(s) du scénario à protéger (propriétaire(s) {who}), " +
                        $"{scanned}/{rules} règle(s) reconnues par le balayage de la carte" +
                        (hookOnly.Length > 0 ? $" (dépendent encore du seul crochet d'apparition : {hookOnly})" : "") +
                        $", dégâts reçus x {DamageFactor.ToString("0.##", CultureInfo.InvariantCulture)} ; {def.Keeps}");
                }
            }

            DrainNotes();
            if (IsRuC01(def) && !_ingenerReleased) CheckPhaseSix();
            if (now >= _nextPrune) { _nextPrune = now + PruneEvery; Sweep(def); }
            if (_nextSummary <= 0f) _nextSummary = now + SummaryEvery;
            else if (now >= _nextSummary) { _nextSummary = now + SummaryEvery; Summary(false); }
            if (_disabled && !_disabledLogged)
            {
                _disabledLogged = true;
                Mod.Log.Warning($"{Tag} {MaxErrors} erreurs dans les correctifs : protection coupée, dégâts normaux jusqu'au redémarrage du jeu");
            }
        }

        /// One line per unit taken under protection, written on the main thread from what the spawn hook or the sweep queued.
        /// A mission that carries a Consequence says it here, once, the first time it actually protects something - as a
        /// warning, because a warning is the only level the PUBLIC log filter never drops, and this is the line the author has
        /// to be able to read in his own test log before he accepts the trade.
        static void DrainNotes()
        {
            while (_notes.TryDequeue(out var n))
            {
                var def = _active;
                if (n.R != null && n.R.Id < _seen.Length) _seen[n.R.Id]++;
                Mod.Log.Msg($"{TagUnit} protégé uid={n.Uid} entité={n.Eid} unitéDB={n.DbId} groupe='{n.Groups ?? ""}' tag={n.Tag} " +
                    $"editeur={(n.Me ? "oui" : "non")} règle={(n.R != null ? n.R.Name : "?")} ({n.Why})");
                if (def != null && def.Consequence != null && !_consequenceLogged)
                {
                    _consequenceLogged = true;
                    Mod.Log.Warning(Tag + " " + def.Consequence);
                }
            }
        }

        /// RU_C01 only. The engineers stop being protected the moment the script stops caring: #19097 ("Start phase 6")
        /// deactivates their death watcher #18382. Two independent signals, either is enough:
        ///   - the mission storage variable "Start phase 6" changes (that is exactly what #19097 reacts to);
        ///   - the extraction Mi-8 of #6751 has spawned, because #19099 (same variable) is what starts its subgraph #6731.
        static void CheckPhaseSix()
        {
            bool fired = _seen.Length > RExtraction && _seen[RExtraction] > 0;
            if (!fired)
            {
                string v = ReadPhase();
                if (!_phaseBaselineTaken) { _phaseBaselineTaken = true; _phaseBaseline = v; }
                else if (!string.Equals(v, _phaseBaseline, StringComparison.Ordinal)) fired = true;
            }
            if (!fired) return;
            bool viaSpawn = _seen.Length > RExtraction && _seen[RExtraction] > 0;
            _ingenerReleased = true;
            int freed = 0;
            lock (_sync)
            {
                for (int i = _list.Count - 1; i >= 0; i--) if (_list[i].R != null && _list[i].R.Id == RIngener) { _list.RemoveAt(i); freed++; }
                Publish();
            }
            Mod.Log.Msg($"{Tag} relâche '{GroupIngener}' : {freed} escouade(s) rendue(s) mortelle(s) ('{PhaseSix}' atteint" +
                (viaSpawn ? ", vu par l'apparition du Mi-8 d'extraction" : ", vu dans la mémoire de mission") + ")");
        }

        static string ReadPhase()
        {
            try
            {
                var st = NodeCtl.MissionStorage;
                if (st == null) return null;
                var v = st.GetMissionStorage(PhaseSix);
                return v == null ? null : v.ToString();
            }
            catch { return null; }
        }

        // ================================================================ the map sweep (main thread, every PruneEvery seconds)

        /// One pass over the battle, doing the module's two main-thread jobs on the same LuaMap.GetUnits array:
        ///  1. IDENTIFY. This is the path that actually protects anything. It does not care when a unit appeared, so a
        ///     mission-editor unit placed before the battle even started is found on the very first pass - which the spawn hook,
        ///     firing only on a spawn that on this build never reaches it, could never do. Keys come from the table and were each
        ///     checked exact against the decrypted mission; a rule with no key is simply skipped.
        ///  2. PRUNE. Drops an entity the script removed (refund2 / killUnit3) or that simply died, before DefaultEcs hands its
        ///     id to a new unit.
        /// Only the player's own side is enumerated, which is also why every owner shipped in the table sits on the player's
        /// team - an owner on the other side would never be identified and would be dropped 15 seconds after any hook added it.
        /// That is checked once per mission in the table's comments, and it is the third reason US_M06N is not shipped.
        /// Never throws. Repeated failures stop the sweep for the session rather than fight an unreadable map every 5 s.
        static void Sweep(MissionDef def)
        {
            if (_scanBroken || def == null) return;
            if (_playerSide != 0 && _playerSide != 1) return;                 // side unknown: change nothing at all

            Il2CppReferenceArray<LuaUnit> arr = null;
            try { _map ??= new LuaMap(); arr = _map.GetUnits(V3.zero, 1_000_000f, _playerSide, -1); }
            catch (Exception e) { ScanFailed("liste des unités", e); return; }  // unreadable: keep what we have rather than drop it
            if (arr == null) return;

            var live = new HashSet<int>();
            int n = 0;
            try { n = arr.Length; } catch (Exception e) { ScanFailed("taille de la liste", e); return; }
            for (int i = 0; i < n; i++)
            {
                LuaUnit u;
                int eid;
                try
                {
                    u = arr[i];
                    if (u == null || !u.IsAlive()) continue;
                    eid = u.Entity.EntityId;
                }
                catch { _scanErrors++; continue; }
                live.Add(eid);
                try { Identify(def, u, eid); }
                catch (Exception e) { ScanFailed("reconnaissance d'une unité", e); if (_scanBroken) break; }
            }
            if (live.Count == 0) return;                                       // nothing readable this pass: change nothing

            int dropped = 0;
            long old = Environment.TickCount64 - PruneGraceMs;
            lock (_sync)
            {
                for (int i = _list.Count - 1; i >= 0; i--)
                {
                    if (live.Contains(_list[i].Eid)) continue;
                    if (_list[i].At > old) continue;      // just spawned: the Lua map may not know it yet, never drop it on that
                    _list.RemoveAt(i); dropped++;
                }
                if (dropped > 0) Publish();
            }
            if (dropped > 0) { _pruned += dropped; Mod.Log.Msg($"{Tag} {dropped} unité(s) protégée(s) ont quitté la bataille : protection retirée (identifiant réutilisable)"); }
        }

        /// One live unit against this mission's shipped rules. Reads nothing it does not need: the owner decides almost
        /// everything, and the unit id, the loadout, the cargo and the group are only read for a unit an owner already let in.
        /// Adds nothing the hooks have already taken (same entity), and never takes a unit of the human player.
        static void Identify(MissionDef def, LuaUnit u, int eid)
        {
            if (eid <= 0) return;
            lock (_sync) { for (int i = 0; i < _list.Count; i++) if (_list[i].Eid == eid) return; }

            int owner = int.MinValue;
            try { owner = u.GetOwnerPlayerUID(); } catch { return; }
            if (!OwnerWanted(def, owner)) return;
            if (_localUid >= 0 && owner == _localUid) return;            // the human player's own units are never auto-protected

            int uid = 0; try { uid = u.UID; } catch { return; }
            int dbId = 0; try { dbId = u.SpawnData?.Unit?.UnitID ?? 0; } catch { dbId = 0; }

            var rules = def.Rules;
            for (int i = 0; i < rules.Length; i++)
            {
                var r = rules[i];
                if (RuleTier(def, r) > TierMax || !Scannable(r)) continue;
                if (r.Owner != owner) continue;
                // RU_C01 lets its engineers go at phase 6 (#19097 deactivates their watcher). The sweep must not put them back.
                if (IsRuC01(def) && _ingenerReleased && r.Id == RIngener) continue;
                if (r.ScanUids != null)
                {
                    if (Array.IndexOf(r.ScanUids, uid) < 0) continue;
                    // the unit id stays a second lock on a uid key: a mission file that changed under us stops matching
                    if (r.DbId != 0 && dbId != 0 && dbId != r.DbId) continue;
                }
                else
                {
                    if (r.DbId != 0 && dbId != r.DbId) continue;              // dbId 0 = unreadable: refuse rather than guess
                    if (r.ScanOption != 0 && !HasOption(u, r.ScanOption)) continue;
                    if (r.ScanCargo != 0 && !HasCargo(u, r.ScanCargo)) continue;
                    if (r.ScanGroup != null && !HasGroupLive(u, r.ScanGroup)) continue;
                }

                lock (_sync)
                {
                    for (int k = 0; k < _list.Count; k++) if (_list[k].Eid == eid) return;
                    _list.Add(new Entry { Eid = eid, Uid = uid, DbId = dbId, R = r, At = Environment.TickCount64 });
                    Publish();
                }
                _scanTaken++;
                if (_notes.Count < 64)
                    _notes.Enqueue(new Note { Uid = uid, Eid = eid, DbId = dbId, Tag = 0, R = r, Me = r.ScanUids != null,
                                              Groups = r.ScanGroup, Why = BecauseScan(r) });
                return;
            }
        }

        /// What the sweep actually compared, printed with every unit it takes - the fields of the sweep key and no others, so a
        /// line in the player's log can be checked against the table by eye without guessing which path found the unit.
        static string BecauseScan(Rule r)
        {
            var sb = new System.Text.StringBuilder("balayage de la carte : propriétaire c").Append(r.Owner);
            if (r.ScanUids != null)
            {
                sb.Append(", uid d'éditeur ");
                for (int i = 0; i < r.ScanUids.Length; i++) { if (i > 0) sb.Append('/'); sb.Append('c').Append(r.ScanUids[i]); }
            }
            if (r.DbId != 0) sb.Append(", unitéDB ").Append(r.DbId);
            if (r.ScanOption != 0) sb.Append(", option ").Append(r.ScanOption);
            if (r.ScanCargo != 0) sb.Append(", cargaison unitéDB ").Append(r.ScanCargo);
            if (r.ScanGroup != null) sb.Append(", groupe '").Append(r.ScanGroup).Append('\'');
            return sb.ToString();
        }

        /// True when the table gives this rule a key the sweep can use. A ScanKey rule that narrows nothing beyond its owner is
        /// refused here and not in the table, so a future edit that drops a field cannot silently widen a rule to a whole army.
        static bool Scannable(Rule r)
        {
            if (r == null) return false;
            if (r.ScanUids != null && r.ScanUids.Length > 0) return true;
            return r.ScanKey && (r.DbId != 0 || r.ScanGroup != null || r.ScanCargo != 0);
        }

        /// A loadout option the unit carries. Read through the engine's own ModDataExt.GetOptions, with a plain walk of the mod
        /// items as a fallback; unreadable means "no", so the rule refuses instead of widening. This is what separates RU_C01's
        /// extraction Mi-8 (#6751, option 264) from the fire-team ones (#6954 and its three siblings, option 267).
        static bool HasOption(LuaUnit u, int option)
        {
            var items = ItemsOf(u);
            if (items == null) return false;
            try
            {
                var opts = Il2CppBrokenArrow.Shared.Ecs.MissionEditor.ModDataExt.GetOptions(items);
                if (opts != null)
                {
                    int n = opts.Length;
                    for (int i = 0; i < n && i < 64; i++) if (opts[i] == option) return true;
                    return false;
                }
            }
            catch { }
            try
            {
                int n = items.Count;
                for (int i = 0; i < n && i < 64; i++) if (items[i].OptionID == option) return true;
            }
            catch { }
            return false;
        }

        /// A DB unit id the unit carries as cargo. RU_C01's two delivery Mi-8s (#8843/#8844) are the only c3 helicopters in the
        /// whole mission that carry an engineer squad (unit 117), and that is the only thing that tells them apart from the
        /// fourteen other troop-carrying Mi-8s, which share their loadout option exactly. Unreadable or empty means "no".
        static bool HasCargo(LuaUnit u, int dbId)
        {
            // two places carry the same list on this build; whichever answers first is enough, neither is required
            try { if (SlotsHave(u.SpawnData?.Cargo, dbId)) return true; } catch { }
            try { if (SlotsHave(u.SpawnData?.Unit?.Cargo, dbId)) return true; } catch { }
            return false;
        }

        static bool SlotsHave(Il2CppBrokenArrow.MissionEditor.Inspector.CargoData cargo, int dbId)
        {
            if (cargo == null) return false;
            try
            {
                var slots = cargo.Slots;
                if (slots == null) return false;
                int n = slots.Count;
                for (int i = 0; i < n && i < 32; i++)
                {
                    var s = slots[i];
                    if (s != null && s.UnitID == dbId) return true;
                }
            }
            catch { }
            return false;
        }

        /// The unit's loadout items, or null. One place, wrapped once, so the two readers below stay short.
        static Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.Shared.Ecs.MissionEditor.ModDataItem> ItemsOf(LuaUnit u)
        {
            try { return u.SpawnData?.Unit?.Items; } catch { return null; }
        }

        /// Group membership straight from the engine (EntitiesHelper.UnitHasGroup), the same call Missions.cs makes for every
        /// unit it protects from the mod's own AI. Not the spawn node's text: this is what the unit belongs to right now.
        /// Unreadable means "no", and twenty failures stop asking for the rest of the session.
        static bool HasGroupLive(LuaUnit u, string group)
        {
            if (_groupBroken || group == null) return false;
            Il2CppBrokenArrow.Client.Ecs.Utils.EntitiesHelper helper = null;
            try { helper = _gc?.GetEntitiesHelper; } catch { helper = null; }
            if (helper == null) return false;
            try { var e = u.Entity; return helper.UnitHasGroup(ref e, group); }
            catch (Exception ex)
            {
                if (++_scanErrors >= 20 && !_groupBroken)
                {
                    _groupBroken = true;
                    try { Mod.Log.Warning($"{Tag} appartenance aux groupes illisible ({ex.GetBaseException().Message}) : les règles qui demandent un groupe ne reconnaissent plus rien"); } catch { }
                }
                return false;
            }
        }

        /// The sweep's own kill-switch: it stops, says so once, and leaves whatever is already protected exactly as it is.
        static void ScanFailed(string what, Exception e)
        {
            if (++_scanErrors < ScanErrorMax) return;
            _scanBroken = true;
            if (_scanBrokenLogged) return;
            _scanBrokenLogged = true;
            try { Mod.Log.Warning($"{Tag} balayage de la carte arrêté pour cette session ({what} : {e.GetBaseException().Message}) : aucune nouvelle unité ne sera reconnue"); } catch { }
        }

        // ================================================================ patches

        /// Installs the three patches once. Never throws; a method the game renamed disables only its own patch.
        /// The spawn hook is NOT required any more, and that is the whole lesson of 2026-09-18: it installed perfectly and then
        /// never fired once ("apparition exacte=0" in the player's own log), so the module refused nothing and protected nothing.
        /// Identification now comes from the map sweep, which needs no patch at all; the spawn hook is a bonus when it fires.
        /// The damage patches, on the other hand, ARE the protection: without at least one of them there is nothing to do.
        static bool EnsurePatched()
        {
            if (_triedPatch) return (_okShots || _okMelee) && !_disabled;
            _triedPatch = true;
            _okSpawn = TryPatch("apparitions", typeof(SpawnService), "InvokeUnitSpawned", nameof(SpawnPostfix), false);
            _okShots = TryPatch("tirs", typeof(BSH), "CalculateHitDamage", nameof(DamagePostfix), false);
            _okMelee = TryPatch("corps à corps", typeof(CQC), "DeductHitPoints", nameof(MeleePrefix), true);
            if (!_okSpawn)
                Mod.Log.Warning(Tag + " crochet d'apparition absent : la reconnaissance passe entièrement par le balayage de la carte");
            if (!_okShots && !_okMelee)
            {
                Mod.Log.Warning(Tag + " REFUS : aucun correctif de dégâts installé, protection abandonnée pour cette partie");
                return false;
            }
            return true;
        }

        static bool TryPatch(string label, Type type, string methodName, string patchName, bool isPrefix)
        {
            try
            {
                var target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    Mod.Log.Warning($"{Tag} correctif {label} non installé ({methodName} introuvable dans cette version du jeu)");
                    return false;
                }
                var mine = typeof(ProtectionMission).GetMethod(patchName, BindingFlags.NonPublic | BindingFlags.Static);
                if (mine == null)
                {
                    Mod.Log.Warning($"{Tag} correctif {label} non installé (méthode {patchName} absente du mod)");
                    return false;
                }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.ProtectionMission");
                // Priority.Last for the damage postfix: every other postfix of the chain multiplies or only reads, so a zero
                // written here survives whatever runs after it (AntiHeliPortee is Priority.Last too and multiplies).
                var hm = new HarmonyMethod(mine) { priority = Priority.Last };
                if (isPrefix) _harmony.Patch(target, prefix: hm);
                else _harmony.Patch(target, postfix: hm);
                Mod.Log.Msg($"{Tag} correctif {label} installé");
                return true;
            }
            catch (Exception e)
            {
                try { Mod.Log.Warning($"{Tag} correctif {label} non installé : {e.GetBaseException().Message}"); } catch { }
                return false;
            }
        }

        // ================================================================ session

        /// The battle-end signal (Campaign.PollBattleEnd). Writes the report while the battle is still there to be described.
        internal static void OnBattleEnd() { try { EndOfBattle(); } catch { } }

        /// The game is closing. This is how the author tests: he plays a mission and quits from inside it, so without this the
        /// one line he was told to read ("attendu mais jamais vu ... 0/N") was never written at all - Frame stops being called
        /// and nothing else reached the report. Writing it twice is harmless: EndOfBattle latches on _endLogged.
        internal static void OnQuit()
        {
            try
            {
                if (_wasArmed && !_endLogged && !_quitLogged)
                {
                    _quitLogged = true;
                    Mod.Log.Msg($"{Tag} fermeture du jeu pendant la mission : bilan écrit maintenant");
                }
                EndOfBattle();
            }
            catch { }
        }

        /// Back to vanilla at once: the flag the hooks read is cleared before the list, so no hook can see a stale set.
        static void Disarm()
        {
            _armed = false;
            Interlocked.Exchange(ref _beat, 0);                 // any hook still in flight sees a stale heartbeat and stands down
            lock (_sync) { _list.Clear(); _ids = Array.Empty<int>(); }
        }

        /// End of a battle (or of the mission): the summary the next test will be read from, then everything back to zero.
        /// The installed patches and the error kill-switch stay as they are; nothing is ever unpatched.
        static void EndOfBattle()
        {
            var def = _active;
            // only a battle this module actually armed for gets a report: every other mission must stay completely silent
            if (_battle == IntPtr.Zero || !_wasArmed || _endLogged || def == null) { ResetSession(); return; }
            _endLogged = true;
            try
            {
                DrainNotes();
                Summary(true);
                // what was recognised, rule by rule, then what was expected and never turned up
                var recap = new System.Text.StringBuilder();
                for (int i = 0; i < def.Rules.Length; i++)
                {
                    var r = def.Rules[i];
                    if (RuleTier(def, r) > TierMax) continue;
                    if (recap.Length > 0) recap.Append(" ; ");
                    // a progressive rule prints "1 mini": its slices depend on the branch the player took, so a count is meaningless
                    recap.Append(r.Name).Append(' ').Append(Count(r)).Append('/')
                         .Append(r.Progressive ? "1 mini" : r.Expected.ToString(CultureInfo.InvariantCulture));
                }
                // its own prefix: one line per battle, so the PUBLIC filter can never swallow the line the test is read from
                Mod.Log.Msg($"{TagBilan} mission {def.Uid} palier {def.Tier}, reconnu cette bataille : " + recap +
                    $" ({_scanTaken} par le balayage de la carte" + (_scanBroken ? ", balayage arrêté" : "") + ")");
                for (int i = 0; i < def.Rules.Length; i++)
                {
                    var r = def.Rules[i];
                    if (RuleTier(def, r) > TierMax) continue;
                    int got = Count(r);
                    // a progressive rule only has to see one unit: its slices depend on the branch the player took
                    if (r.Progressive ? got > 0 : got >= r.Expected) continue;
                    Mod.Log.Warning($"{Tag} attendu mais jamais vu : {r.Name} — {got}/{(r.Progressive ? 1 : r.Expected)} unité(s) reconnue(s) ({r.Why})" +
                        (Scannable(r) ? "" : " [cette règle n'a pas de clé de balayage : elle dépend du seul crochet d'apparition]"));
                }
                if (_refused > 0) Mod.Log.Msg($"{Tag} {_refused} apparition(s) refusée(s) (propriétaire = joueur local)");
                if (IsRuC01(def) && !_ingenerReleased && Count(RuleOf(def, RIngener)) > 0)
                    Mod.Log.Msg($"{Tag} '{GroupIngener}' jamais relâché : '{PhaseSix}' n'a pas été vu pendant cette bataille");
            }
            catch (Exception e) { try { Mod.Log.Warning(Tag + " bilan impossible : " + e.GetBaseException().Message); } catch { } }
            ResetSession();
        }

        static int Count(Rule r) => r != null && r.Id < _seen.Length ? _seen[r.Id] : 0;

        /// Everything per-battle back to zero. The installed patches, the error kill-switch and the sweep's own kill-switch are
        /// deliberately NOT reset: they belong to the session, not to the battle, and nothing is ever unpatched.
        internal static void ResetSession()
        {
            Disarm();
            _active = null;
            _notes.Clear();
            Array.Clear(_seen, 0, _seen.Length);
            Interlocked.Exchange(ref _shotCalls, 0); Interlocked.Exchange(ref _shotHits, 0);
            Interlocked.Exchange(ref _meleeCalls, 0); Interlocked.Exchange(ref _meleeHits, 0);
            Interlocked.Exchange(ref _rawMilli, 0);
            _lastShotHits = _lastMeleeHits = _lastRawMilli = 0;
            _armLogged = _endLogged = _wasArmed = _refusalLogged = _consequenceLogged = false;
            _ingenerReleased = _phaseBaselineTaken = false;
            _phaseBaseline = null;
            _pruned = _refused = _scanTaken = 0;
            _nextSummary = _nextPrune = 0f;
            _map = null;
            _gc = null;
            _quitLogged = false;
        }

        /// The "how many damage events were nullified" line. Counters are read with Interlocked, never inside a hook.
        /// The call counts are there on purpose: they are what tells a silent battle ("the hooks never ran") apart from a calm
        /// one ("the hooks ran, nothing ever shot at a protected unit").
        static void Summary(bool final)
        {
            long calls = Interlocked.Read(ref _shotCalls), mcalls = Interlocked.Read(ref _meleeCalls);
            long shots = Interlocked.Read(ref _shotHits), melee = Interlocked.Read(ref _meleeHits);
            long raw = Interlocked.Read(ref _rawMilli), errors = Interlocked.Read(ref _errors);
            // the periodic line only when something moved; the end-of-battle one always, so a silent battle is still on record
            if (!final && shots == _lastShotHits && melee == _lastMeleeHits && raw == _lastRawMilli) return;
            _lastShotHits = shots; _lastMeleeHits = melee; _lastRawMilli = raw;
            int protectedNow; lock (_sync) protectedNow = _list.Count;
            string brut = (raw / 1000f).ToString("0.#", CultureInfo.InvariantCulture);
            string what = DamageFactor <= 0f ? "annulé(s)" : "réduit(s) x " + DamageFactor.ToString("0.##", CultureInfo.InvariantCulture);
            Mod.Log.Msg($"{Tag} {(final ? "bilan de fin de bataille" : "dégâts évités")} : {shots} tir(s) et {melee} coup(s) au corps à corps {what} " +
                $"({brut} de dégâts bruts évités) sur {calls} calcul(s) de dégâts et {mcalls} coup(s) vus, " +
                $"{protectedNow} unité(s) encore protégée(s), {_pruned} retirée(s)" + (errors > 0 ? $", {errors} erreur(s)" : ""));
        }
    }
}
