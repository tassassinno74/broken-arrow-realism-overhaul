// RealismOverhaul - BilanMission: one end-of-battle report, written once, in the log.
//
//  WHY. The author asked for what a commander wants after a fight: what it cost, what it killed, how long it took, which tasks
//  held and which did not. The mod already counts a great deal, but every module reports its own machinery ([MISSION] hooks,
//  [PERF] frames, [LEURRES] rolls). None of them answers "how did the battle go". This file does that and nothing else.
//
//  WHAT IT WRITES INTO THE GAME: NOTHING. Not one value, not one field, no journaled lot, no preference. It reads the game and it
//  writes lines to the log. Switching the mod off, or this module dying on its error counter, changes no behaviour at all,
//  because it never changed any. That is why there is no restore path here and no kill-switch back to vanilla: it is vanilla.
//
//  WHAT IT COSTS WHILE THE BATTLE RUNS. No per-frame work at all: the module is not in Mod.OnUpdate (which this file does not own)
//  and asks for no frame slot. Between the start and the end of a battle it only runs three postfixes, all on methods the game
//  calls once per unit death:
//    - AchievementsStatistic.AddLostUnit(Int32)      -> one linear scan over at most 96 ints
//    - AchievementsStatistic.AddUnitKillCount(Int32) -> the same
//    - StatisticService.OnUnitDeath(Int32,Int32,Int32) -> the same over at most 32 ints
//  A campaign battle produces a few hundred of those, not a few hundred thousand. Nothing is allocated on any of those paths: every
//  table is a fixed array built once when the type loads and cleared at each battle start. Off the main thread the hooks do
//  Interlocked only (totals and the per-minute buckets) and never touch the detail tables, exactly like the other modules.
//  The report itself allocates (a StringBuilder and a few small lists) but it runs once, after the fighting, on the end screen.
//
//  SIGNATURES. Every method patched here takes blittable arguments only - Int32, Boolean, one enum by value - or no argument at
//  all. Not one takes a non-blittable IL2CPP struct by reference (see the permanently disabled hook in AntiHeliPortee.cs for what
//  that mistake costs). No ECS component is read, Get<T> is never called, nothing is ever unpatched. Each patch is its own
//  [HarmonyPatch] class, so Mod.ApplyPatches installs them one by one and a game update that renames one method disables that one
//  patch and logs it, leaving the rest of the report standing.
//
//  HOW IT KNOWS A BATTLE IS RUNNING, and why it is not the obvious hook. The obvious hook was GameSessionContext.SetCurrentPlayer,
//  the moment Mod.cs itself treats as "a battle has a local player" (Patch_SetCurrentPlayer). IT NEVER FIRES IN A CAMPAIGN ON THIS
//  BUILD: ten of the player's log files hold not one "[JOUEUR]" line, one of them a PERSONAL build with no filtering at all and a
//  whole RU_C01 battle in it, although Mod.cs writes that line from that postfix and "[PATCH] 45 correctif(s) appliqué(s)" carries
//  no "désactivé(s)" clause. So the module carries its own heartbeat instead, ChienDeGarde(), and the heartbeat is what opens the
//  battle, names the main thread and watches for the end screen. It rides NodeDelay.OnActivated - parameterless, already patched by
//  DelaisMission.cs and CargoMort.cs, and PROVED ALIVE on this build: "[MINUTEURS] ... tentative 1, fil 1" at 23:19:06 on
//  2026-09-18 was written from that postfix chain, on thread 1. Mod.cs may call ChienDeGarde() as well; it is throttled to one pass
//  every two seconds whoever calls it. Patch_BilanNouvelleBataille stays, as a second chance and nothing more.
//
//  WHEN IT FIRES. Four end-of-battle signals, first one wins, latched for the battle:
//    1. the heartbeat reading BattleEndScreen.Active   - the same read Mod.cs's Campaign.PollBattleEnd does, and the one that has
//       actually been seen working in a log ("[CAMPAGNE] écran de fin de bataille affiché", 2026-09-18 14:54);
//    2. StatisticService.OnFightEnd                    - the game's own end of fight, and the only place the winning team is handed
//       over; it is a service with network dependencies and there is NO proof it runs in a solo campaign, so nothing waits on it;
//    3. NodeEndMission.OnMissionEnd(Boolean win)       - the mission script ending the mission, and the only place the RESULT of a
//       campaign mission is handed over (DelaisMission.cs and CargoMort.cs use the same node);
//    4. OnBattleEnd(), when Mod.cs's Campaign.PollBattleEnd calls it.
//  The setter patch on BattleEndScreen.Active is kept but is NOT counted as a signal: it is the accessor of a trivial static
//  auto-property, a prime candidate for IL2CPP inlining, and no other module of this mod patches it - they all poll the property.
//  A battle shorter than 120 s never gets a report: that is the same floor Mod.cs uses to reject a stale end-screen flag, and it
//  is what protects the report from a script node that ends a sub-objective early.
//
//  HONESTY RULES, which are the whole point of the file.
//   - Every figure says where it comes from: "compté par le mod", "compteur du jeu", or "instantané de fin".
//   - A counter that never fired prints "non mesuré", never 0. A silence and a zero are not the same statement.
//   - The end-of-battle unit sweep is a snapshot of who is still standing. It is NOT a loss count and the line says so.
//   - Two things the game does not hand over, and which are therefore refused instead of guessed:
//       * the category of the ENEMY units destroyed - the game's kill counter is keyed by the killing unit, not the victim;
//       * total ammunition and supply CONSUMED over the battle - no running total is exposed in campaign. What is printed is the
//         state at the end (ammunition left, supply still carried), which is a different statement and is labelled as one.
//   - StatisticService.OnUnitDeath is read but its meaning is NOT assumed. reperage/pilote_abattu_design.md flagged it: "non prouvé
//     que victimId soit l'uid de l'unité, et que ce service tourne en campagne solo - a mesurer avant de s'en servir". So this
//     module measures it: it collects the distinct ids it saw and, at the end, asks the session whether each one is a player uid.
//     Only if every id resolves to a player does it print a per-side split, and even then it says the deduction is a deduction.
//     Otherwise it prints the raw totals and says the attribution is not possible. The "relevé brut" line exists for that check.
//
//  WHOSE LOSSES. AchievementsStatistic is the game's achievement bookkeeping: AddOnFieldUnit/RemoveOnFieldUnit track what the
//  player has on the field, so AddLostUnit/AddUnitKillCount are READ here as the local player's losses and the local player's
//  kills. IT IS A DEDUCTION AND NOTHING MORE - the dump shows _onField/_lost/_killedWith with no owner on them and no victim on a
//  kill, so a friendly-fire kill counts too - and the printed lines say so in as many words, not only this header. The losses are
//  cross-checked against PlayerInfo.DeadUnitsValue, which the game keeps separately; when the two disagree the report says so
//  rather than picking a winner.
//
//  ON SCREEN, LATER. Everything here goes to the log, in French. A screen panel needs Txt.cs in five languages and ModTab.cs, and
//  this file owns neither. The report is built as a list of finished lines (Lignes()), so turning it into a panel is a formatting
//  change: take the same list, drop the "[BILAN] " prefix, put one line per row. The strings that would then need translating are
//  listed at the end of this file, under "TEXTES A TRADUIRE".
//
//  Comments English, log lines French, no emoji, tag [BILAN].
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.BattleEnd;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using AchievementsStatistic = Il2CppBrokenArrow.Client.Ecs.Statistic.AchievementsStatistic;
using StatisticService = Il2CppBrokenArrow.Client.Ecs.Statistic.StatisticService;
using GameSessionContext = Il2CppBrokenArrow.Client.Ecs.Configs.GameSessionContext;
using PlayerInfo = Il2CppBrokenArrow.Client.Ecs.Utils.PlayerInfo;
using NodeDelay = Il2CppBrokenArrow.ScriptEngine.Nodes.Timing.NodeDelay;
using NodeEndMission = Il2CppBrokenArrow.ScriptEngine.Nodes.Gameplay.NodeEndMission;
using DataBaseService = Il2CppBrokenArrow.Shared.Ecs.DataBaseService;
using UnitsRow = Il2CppBrokenArrow.DataBase.Models.Units;
using TeamSide = Il2CppNetworkCommon.Enums.TeamSide;
using TaskData = Il2CppBrokenArrow.MissionEditor.Data.Meta.TaskData;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    // ---------------------------------------------------------------- hooks (one class each: a renamed method kills one, not the report)

    /// A delay node of the mission script starts counting. Parameterless method, nothing by reference, and the ONLY signal of this
    /// module proved alive on this build: DelaisMission.cs and CargoMort.cs already ride it, and "[MINUTEURS] ... tentative 1,
    /// fil 1" at 23:19:06 on 2026-09-18 was written from this postfix chain. Everything else here is a second chance.
    /// The body is throttled to one pass every two seconds, so a mission script full of delay nodes costs nothing.
    [HarmonyPatch(typeof(NodeDelay), nameof(NodeDelay.OnActivated))]
    static class Patch_BilanChienDeGarde
    {
        static void Postfix() => BilanMission.ChienDeGarde();
    }

    /// A battle gets its local player. This is the moment Mod.cs treats as the start of a battle (Patch_SetCurrentPlayer), and it
    /// is kept here as a SECOND CHANCE only: on this build it never fires in a campaign (see the file header), which is why the
    /// heartbeat above exists. One Int32, nothing by reference; __instance is Harmony's own injection, so no game parameter name
    /// is relied on for it. The session object identifies the battle, so a second call inside one battle never resets the counters
    /// (Campaign.NoteSession recognises a new battle exactly the same way).
    [HarmonyPatch(typeof(GameSessionContext), nameof(GameSessionContext.SetCurrentPlayer))]
    static class Patch_BilanNouvelleBataille
    {
        static void Postfix(GameSessionContext __instance, int id) => BilanMission.NouvelleBataille(__instance, id);
    }

    /// The game books a unit the player lost, for its achievements. One Int32 (a database unit id).
    [HarmonyPatch(typeof(AchievementsStatistic), nameof(AchievementsStatistic.AddLostUnit))]
    static class Patch_BilanPerte
    {
        static void Postfix(int unitID) => BilanMission.NotePerte(unitID);
    }

    /// The game books a kill for its achievements. One Int32 (the unit that killed, not the victim - hence the refusal to give
    /// the enemy losses by category).
    [HarmonyPatch(typeof(AchievementsStatistic), nameof(AchievementsStatistic.AddUnitKillCount))]
    static class Patch_BilanDestruction
    {
        static void Postfix(int unitID) => BilanMission.NoteDestruction(unitID);
    }

    /// The game's own death bookkeeping. Three Int32: the safest signature in the whole game, and the only one carrying the cost
    /// of what died. Its meaning is measured here, never assumed.
    [HarmonyPatch(typeof(StatisticService), nameof(StatisticService.OnUnitDeath))]
    static class Patch_BilanMortUnite
    {
        static void Postfix(int killerId, int victimId, int unitCost) => BilanMission.NoteMort(killerId, victimId, unitCost);
    }

    /// End-of-battle screen shown or hidden: one Boolean, and the postfix reads the static property itself rather than the
    /// argument, so it cannot break on a renamed parameter. NOT COUNTED AS A SIGNAL: this is the accessor of a trivial static
    /// auto-property (the backing field set__Active_k__BackingField is in the dump), so IL2CPP may well have inlined it and the
    /// detour may never be reached. No other module of this mod patches it - Mod.cs, AntiHeliPortee.cs and CalibreMesure.cs all
    /// poll the property instead, and so does ChienDeGarde() here. It is kept because it costs nothing and may be the fastest.
    [HarmonyPatch(typeof(BattleEndScreen), nameof(BattleEndScreen.Active), MethodType.Setter)]
    static class Patch_BilanEcranFin
    {
        static void Postfix() => BilanMission.SurEcranDeFin();
    }

    /// The game ends the fight and says which team won. One enum by value.
    [HarmonyPatch(typeof(StatisticService), nameof(StatisticService.OnFightEnd))]
    static class Patch_BilanFinDeCombat
    {
        static void Postfix(TeamSide winnerTeam) => BilanMission.SurFinDeCombat((int)winnerTeam);
    }

    /// The mission script ends the mission. The dump is explicit - "Void OnMissionEnd(Boolean win)", called by _OnInit_b__2_0 and
    /// _OnInit_b__2_1, the node's PlayerWin and PlayerLose pins - so it carries the ONE thing the game hands the mod about the
    /// result of a campaign mission, and throwing it away would leave the first line of the report saying "non mesuré" for ever.
    /// One Boolean by value: blittable, nothing by reference.
    [HarmonyPatch(typeof(NodeEndMission), nameof(NodeEndMission.OnMissionEnd))]
    static class Patch_BilanFinDeMission
    {
        static void Postfix(bool win) => BilanMission.SurFinDeMission(win);
    }

    // ---------------------------------------------------------------- the module

    static class BilanMission
    {
        const string GuardVersion = "1.0";
        const string P = "[BILAN] ";

        /// Past this many errors the module stops counting and stops reporting for the rest of the game session. It writes
        /// nothing into the game, so "dead" simply means "no report": there is nothing to put back.
        const int MaxErreurs = 30;

        /// Distinct unit types kept per battle for the losses and the kills. A campaign deck holds far fewer than this; anything
        /// beyond is counted in _perteHorsTable and said out loud instead of being dropped in silence.
        const int MaxTypes = 96;

        /// Distinct identifiers kept from OnUnitDeath. A campaign has a handful of players; needing more than 32 is itself the
        /// answer to "are these player uids?" (no) and the report says so.
        const int MaxIds = 32;

        /// Minutes of battle kept for "the most costly moment". 90 minutes covers every campaign mission; a longer battle keeps
        /// piling into the last bucket, which is stated in the line.
        const int MaxPhases = 90;

        /// A battle shorter than this never gets a report (same floor as Campaign.PollBattleEnd in Mod.cs).
        const float MinBataille = 120f;

        /// Milliseconds between two passes of the heartbeat, whoever calls it (the mission script's delay nodes, or Mod.cs).
        const long PeriodeChienMs = 2000;

        /// Tasks named in full in the report, worst first. Beyond that only the count is given.
        const int MaxTachesCitees = 4;
        /// Unit types named in the losses line.
        const int MaxTypesCites = 4;

        // ------------------------------------------------------------ state (every table fixed, built once, cleared per battle)
        static int _erreurs;
        static volatile bool _mort;                         // error kill-switch: no more counting, no more report
        static volatile int _filPrincipal = -1;             // thread the battle-start postfix runs on
        static volatile bool _bataille;                     // a battle is open
        static volatile bool _ecrit;                        // this battle already has its report

        static long _debutTick;                             // Environment.TickCount64 at battle start (no Unity call in the hooks)
        static IntPtr _session;                             // the GameSessionContext this battle belongs to
        static int _uidLocal = -1;
        static string _mission;

        static long _pertes, _destructions, _morts, _coutMorts;   // Interlocked totals, every thread
        static long _horsFil;                                     // calls seen outside the main thread (detail tables skipped)
        static int _vainqueur = -1;                               // TeamSide handed over by OnFightEnd, -1 = never said
        static int _resultatScript = -1;                          // what NodeEndMission.OnMissionEnd said: 1 won, 0 lost, -1 never said
        static long _prochainChien;                               // next heartbeat pass, Environment.TickCount64
        static bool _ditBattement;                                // the heartbeat has said once that it opened the battle

        static readonly int[] _perteId = new int[MaxTypes];
        static readonly int[] _perteN = new int[MaxTypes];
        static int _nPerteTypes, _perteHorsTable;

        static readonly int[] _destId = new int[MaxTypes];
        static readonly int[] _destN = new int[MaxTypes];
        static int _nDestTypes, _destHorsTable;

        static readonly int[] _id = new int[MaxIds];              // identifiers seen in OnUnitDeath
        static readonly int[] _idVictime = new int[MaxIds];
        static readonly int[] _idTueur = new int[MaxIds];
        static readonly long[] _idCout = new long[MaxIds];
        static int _nIds;
        static bool _idsDebordent;

        static readonly int[] _phasePertes = new int[MaxPhases];
        static readonly int[] _phaseCout = new int[MaxPhases];

        static void Log(string s) => Mod.Log.Msg(P + s);

        static string N(long v) => v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
        static string N(int v) => N((long)v);
        static string Pc(double v) => v.ToString("0", CultureInfo.InvariantCulture);

        /// "18 min 12 s", "42 s".
        static string Duree(double secondes)
        {
            if (secondes < 0) return "?";
            int s = (int)Math.Round(secondes);
            int m = s / 60;
            return m <= 0 ? s.ToString(CultureInfo.InvariantCulture) + " s"
                          : m.ToString(CultureInfo.InvariantCulture) + " min " + (s % 60).ToString(CultureInfo.InvariantCulture) + " s";
        }

        /// The seven deck categories, in the order of Mod.Categories.
        static readonly string[] CategorieFr = { "reconnaissance", "infanterie", "véhicules", "appui", "logistique", "hélicoptères", "avions" };

        // ------------------------------------------------------------ gates
        /// True while a campaign battle with the mod on may be observed. Every hook goes through this first.
        static bool Ouvert()
        {
            if (_mort || !Campaign.InCampaign || Campaign.MissionInerte) return false;
            if (Identite.Blocked || Mod.AntiCheatActive) return false;
            var actif = Mod.Actif;
            return actif != null && actif.Value;
        }

        static bool FilPrincipal => _filPrincipal >= 0 && Environment.CurrentManagedThreadId == _filPrincipal;

        // ------------------------------------------------------------ battle start
        /// The battle has a local player: the counting period starts here, and so does the clock the "most costly moment" uses.
        /// Called on the thread Mod.cs already does Unity work on from the same method (Patch_SetCurrentPlayer).
        internal static void NouvelleBataille(GameSessionContext session, int uidLocal)
        {
            if (_mort) return;
            try
            {
                // first writer wins: the heartbeat gets here long before this postfix would, and two different threads claiming to
                // be "the main one" would send the detail tables to whichever spoke last
                if (_filPrincipal < 0) _filPrincipal = Environment.CurrentManagedThreadId;
                if (!Campaign.InCampaign || Campaign.MissionInerte) { _bataille = false; return; }

                IntPtr ptr = IntPtr.Zero;
                try { if (session != null) ptr = session.Pointer; } catch { ptr = IntPtr.Zero; }
                string mission = null;
                try { mission = Campaign.MissionUid; } catch { }

                // the same battle calling SetCurrentPlayer again must not wipe what has been counted since it started
                if (_bataille && ptr != IntPtr.Zero && ptr == _session && string.Equals(mission, _mission, StringComparison.Ordinal))
                { _uidLocal = uidLocal; return; }

                Remettre();
                _session = ptr;
                _mission = mission;
                _uidLocal = uidLocal;
                Interlocked.Exchange(ref _debutTick, Environment.TickCount64);
                _bataille = true;
            }
            catch (Exception e) { Erreur("début de bataille", e); }
        }

        // ------------------------------------------------------------ the heartbeat, and the reason this module works at all
        /// Rides NodeDelay.OnActivated (and Mod.cs, if it calls it): the only path proved alive on this build. Self-throttled to
        /// one pass every two seconds whoever calls it, so a mission script full of delay nodes costs one clock read per node.
        /// It does three things and nothing else, and none of them writes anything into the game:
        ///  1. names the main thread, which the battle-start postfix used to do and never gets to do here;
        ///  2. opens the battle when the session has a local player - the same signal Campaign.PollBattleEnd counts its 120 s on;
        ///  3. reads BattleEndScreen.Active, exactly as Campaign.PollBattleEnd does, because the setter patch may be inlined away.
        internal static void ChienDeGarde()
        {
            if (_mort) return;
            try
            {
                long now = Environment.TickCount64;
                if (now < _prochainChien) return;
                _prochainChien = now + PeriodeChienMs;
                Battement();
            }
            catch (Exception e) { try { Erreur("battement", e); } catch { } }
        }

        static void Battement()
        {
            // the script engine calls us on the game's own thread (the player's log of 2026-09-18 says "fil 1" from this same
            // postfix chain). First writer wins, so a stray call from elsewhere can never move the counting thread afterwards.
            if (_filPrincipal < 0) _filPrincipal = Environment.CurrentManagedThreadId;
            if (!Ouvert()) return;

            var ctx = Campaign.Ctx();
            if (ctx == null) return;
            PlayerInfo cp = null;
            try { cp = ctx.CurrentPlayer; } catch { cp = null; }
            if (cp == null) return;                      // loading screen, menu, end of everything: no battle to open

            IntPtr ptr = IntPtr.Zero;
            try { ptr = ctx.Pointer; } catch { }
            if (ptr != IntPtr.Zero && (!_bataille || ptr != _session))
            {
                int uid = -1;
                try { uid = cp.UID; } catch { }
                NouvelleBataille(ctx, uid);              // it does the campaign checks and the "same battle again" check itself
                if (_bataille && !_ditBattement)
                {
                    _ditBattement = true;
                    // "chien de garde" is a text of PublicLog.KeepParts, so this line is written in the PUBLIC build too. It is the
                    // one line that proves the module is alive on this build, which is exactly what was missing. Same idiom as
                    // CargoMort.cs and Epaves.cs, and for the same reason: this file does not own ModLog.cs.
                    Log("chien de garde : début de bataille vu (mission " + (_mission ?? "?") + ") ; le bilan de fin de bataille est armé");
                }
            }

            if (!_bataille || _ecrit) return;
            bool fin;
            try { fin = BattleEndScreen.Active; }
            catch { return; }                            // member gone after a game update: the other signals still have their turn
            if (fin) Declencher("écran de fin (relevé périodique)");
        }

        // ------------------------------------------------------------ entry points Mod.cs may wire (all optional, all no-ops if not)
        /// Campaign.PollBattleEnd: the battle went to its end. The strongest signal there is, and the one actually seen in a log
        /// ("[CAMPAGNE] écran de fin de bataille affiché", 2026-09-18 14:54). Writes nothing, restores nothing: there is nothing to
        /// restore.
        internal static void OnBattleEnd() { if (!_mort) Declencher("fin de bataille (campagne)"); }

        /// A new game session (mission start, or an in-place restart): whatever was counted belongs to the battle that is gone.
        /// NO REPORT IS WRITTEN HERE, on purpose. By the time this runs the game has already moved to the next battle, so the
        /// report would read the new mission's tasks, units and objectives and print them under the old mission's name. A battle
        /// whose end nobody saw gets no report at all, which is the honest answer; a wrong one would be worse than none.
        internal static void ResetSession()
        {
            if (_mort) return;
            try { Remettre(); } catch (Exception e) { Erreur("remise à zéro", e); }
        }

        /// Clears every per-battle counter. No allocation: the tables are reused.
        static void Remettre()
        {
            _bataille = false;
            _ecrit = false;
            Interlocked.Exchange(ref _pertes, 0);
            Interlocked.Exchange(ref _destructions, 0);
            Interlocked.Exchange(ref _morts, 0);
            Interlocked.Exchange(ref _coutMorts, 0);
            Interlocked.Exchange(ref _horsFil, 0);
            _vainqueur = -1;
            _resultatScript = -1;
            _ditBattement = false;
            _nPerteTypes = 0; _perteHorsTable = 0;
            _nDestTypes = 0; _destHorsTable = 0;
            _nIds = 0; _idsDebordent = false;
            Array.Clear(_phasePertes, 0, _phasePertes.Length);
            Array.Clear(_phaseCout, 0, _phaseCout.Length);
            _uidLocal = -1;
            _mission = null;
            _session = IntPtr.Zero;
        }

        // ------------------------------------------------------------ counting hooks (no allocation, no Unity call)
        /// Index of the minute of the battle this call falls in, clamped. Environment.TickCount64 only: never a Unity call, so
        /// the hook is safe on any thread.
        static int Phase()
        {
            long debut = Interlocked.Read(ref _debutTick);
            if (debut <= 0) return 0;
            long ms = Environment.TickCount64 - debut;
            if (ms < 0) return 0;
            long m = ms / 60000L;
            return m >= MaxPhases ? MaxPhases - 1 : (int)m;
        }

        /// The game booked a unit the player lost (achievement bookkeeping).
        internal static void NotePerte(int unitId)
        {
            if (!_bataille || !Ouvert()) return;
            try
            {
                Interlocked.Increment(ref _pertes);
                Interlocked.Increment(ref _phasePertes[Phase()]);
                if (!FilPrincipal) { Interlocked.Increment(ref _horsFil); return; }   // off the main thread: totals only
                Ajouter(_perteId, _perteN, ref _nPerteTypes, ref _perteHorsTable, unitId);
            }
            catch (Exception e) { Erreur("compteur de pertes", e); }
        }

        /// The game booked a kill for its achievements. The id is the unit that killed, not the victim.
        internal static void NoteDestruction(int unitId)
        {
            if (!_bataille || !Ouvert()) return;
            try
            {
                Interlocked.Increment(ref _destructions);
                if (!FilPrincipal) { Interlocked.Increment(ref _horsFil); return; }
                Ajouter(_destId, _destN, ref _nDestTypes, ref _destHorsTable, unitId);
            }
            catch (Exception e) { Erreur("compteur de destructions", e); }
        }

        /// The game's own death bookkeeping. Nothing is deduced here: the raw identifiers are kept so the report can ask the
        /// session what they are.
        internal static void NoteMort(int killerId, int victimId, int cout)
        {
            if (!_bataille || !Ouvert()) return;
            try
            {
                Interlocked.Increment(ref _morts);
                if (cout > 0 && cout < 1_000_000)                     // a price, not a handle: anything absurd is counted as a death only
                {
                    Interlocked.Add(ref _coutMorts, cout);
                    Interlocked.Add(ref _phaseCout[Phase()], cout);
                }
                if (!FilPrincipal) { Interlocked.Increment(ref _horsFil); return; }
                int a = Slot(victimId); if (a >= 0) { _idVictime[a]++; if (cout > 0) _idCout[a] += cout; }
                int b = Slot(killerId); if (b >= 0) _idTueur[b]++;
            }
            catch (Exception e) { Erreur("compteur de morts du jeu", e); }
        }

        /// Linear scan over a fixed table. Main thread only, so no lock and no allocation.
        static void Ajouter(int[] ids, int[] n, ref int used, ref int horsTable, int id)
        {
            for (int i = 0; i < used; i++) if (ids[i] == id) { n[i]++; return; }
            if (used >= ids.Length) { horsTable++; return; }
            ids[used] = id; n[used] = 1; used++;
        }

        /// Slot of an identifier in the id table, or -1 when the table is full (which is itself the answer to "player uid?").
        static int Slot(int id)
        {
            for (int i = 0; i < _nIds; i++) if (_id[i] == id) return i;
            if (_nIds >= MaxIds) { _idsDebordent = true; return -1; }
            int k = _nIds++;
            _id[k] = id; _idVictime[k] = 0; _idTueur[k] = 0; _idCout[k] = 0;
            return k;
        }

        // ------------------------------------------------------------ the end-of-battle signals (the heartbeat above is a fourth)
        internal static void SurEcranDeFin()
        {
            if (_mort) return;
            bool actif;
            try { actif = BattleEndScreen.Active; } catch { return; }
            if (actif) Declencher("écran de fin de bataille");
        }

        internal static void SurFinDeCombat(int equipeGagnante)
        {
            if (_mort) return;
            _vainqueur = equipeGagnante;
            Declencher("fin de combat annoncée par le jeu");
        }

        /// The mission script ends the mission and says whether the player won. In a solo campaign this is the ONLY place the result
        /// is handed to the mod: StatisticService.OnFightEnd, which fills _vainqueur, is a service with network dependencies
        /// (NetworkRoomService, OnlineStatistic) and there is no proof it runs at all here. So the flag is kept even when the report
        /// is written by another signal a moment later.
        internal static void SurFinDeMission(bool gagne)
        {
            if (_mort) return;
            _resultatScript = gagne ? 1 : 0;
            Declencher("fin de mission du script");
        }

        static bool _ditSansDebut, _ditHorsFil;

        /// First signal wins. A battle too short is left alone: a later signal of the same battle still finds the latch open.
        static void Declencher(string source)
        {
            if (_ecrit || !Ouvert()) return;
            try
            {
                // the start of the battle was never seen: neither the heartbeat nor the battle-start postfix ever found a session
                // with a local player, so nothing was counted and nothing is reported - and the player is told once why, instead of
                // getting silence. "chien de garde" keeps the line in the PUBLIC build, because this is the line that would explain
                // a missing report.
                if (!_bataille)
                {
                    if (!_ditSansDebut) { _ditSansDebut = true; Log("chien de garde : le début de cette bataille n'a pas été vu (signal : " + source + ") : rien n'a été compté, donc aucun bilan n'est écrit"); }
                    return;
                }
                // the report reads the game, so it only runs on the main thread; the other signals still have their chance
                if (!FilPrincipal)
                {
                    if (!_ditHorsFil) { _ditHorsFil = true; Log("signal de fin « " + source + " » reçu hors du fil principal : bilan remis au signal suivant"); }
                    return;
                }
                double ecoule = Ecoule();
                if (ecoule < MinBataille) return;                // a battle of less than two minutes gets no report, on purpose
                _ecrit = true;
                Ecrire(source, ecoule);
            }
            catch (Exception e) { Erreur("bilan de fin de bataille", e); }
        }

        /// Seconds since the battle opened, by the real clock (pauses included). Said as such in the report.
        static double Ecoule()
        {
            long debut = Interlocked.Read(ref _debutTick);
            if (debut <= 0) return -1;
            return (Environment.TickCount64 - debut) / 1000.0;
        }

        // ------------------------------------------------------------ the report
        /// The finished lines of the last report, in order, without the "[BILAN] " prefix. Kept so a screen panel can show the
        /// same report later without recomputing anything: one line per row, nothing else to do.
        internal static string[] DernierBilan = Array.Empty<string>();

        static void Ecrire(string source, double ecoule)
        {
            var lignes = Lignes(source, ecoule);
            DernierBilan = lignes.ToArray();
            var sb = new StringBuilder(1024);
            for (int i = 0; i < lignes.Count; i++)
            {
                if (i > 0) sb.Append('\n').Append(P);
                sb.Append(lignes[i]);
            }
            // one single Mod.Log.Msg on purpose: the PUBLIC log filter (ModLog.cs) decides per call, so the whole report goes
            // through or none of it does. See "A FAIRE AILLEURS" at the end of this file.
            Log(sb.ToString());
        }

        /// Builds the report. Every step is guarded on its own: one unreadable thing never costs the rest of the report.
        static List<string> Lignes(string source, double ecoule)
        {
            var l = new List<string>(12);
            var gc = TryGc();
            string mission = _mission ?? SafeMission() ?? "inconnue";

            // ---- 1. header: mission, how long, who won
            double horloge = -1;
            try { if (gc != null) horloge = gc.GameTime; } catch { }
            var tete = new StringBuilder(160);
            // the clock starts when the mod first SAW the battle, not when the game started it: the heartbeat opens the battle on the
            // first delay node of the mission script, which is a second or two in, but a mission whose script starts late would show
            // a shorter duration than the real one. Said here rather than left to be guessed.
            tete.Append("bilan de la bataille — mission ").Append(mission)
                .Append(", durée ").Append(Duree(ecoule)).Append(" (horloge réelle depuis le moment où le mod a vu la bataille commencer, pauses comprises)");
            if (horloge >= 0)
            {
                tete.Append(" ; compteur GameTime du jeu ").Append(Duree(horloge))
                    .Append(" (valeur brute : le mod n'a pas vérifié si le jeu le remet à zéro à chaque bataille, à comparer avec la durée ci-dessus)");
            }
            tete.Append(" ; signal de fin : ").Append(source);
            l.Add(tete.ToString());

            int campLocal = CampLocal(gc);
            if (_vainqueur >= 0)
            {
                string verdict = campLocal < 0 ? "camp du joueur inconnu" :
                                 _vainqueur == campLocal ? "VICTOIRE" :
                                 _vainqueur == 0 || _vainqueur == 1 ? "DÉFAITE" : "ni victoire ni défaite";
                l.Add("résultat annoncé par le jeu : " + verdict + " (équipe gagnante " + _vainqueur.ToString(CultureInfo.InvariantCulture) + ")");
            }
            // the mission script's own end node, which is what a campaign mission really uses: kept as the second source rather than
            // printing "non mesuré" while the answer sits in the signature of a postfix already installed
            else if (_resultatScript >= 0)
                l.Add("résultat annoncé par le script de la mission : " + (_resultatScript == 1 ? "VICTOIRE" : "DÉFAITE")
                      + " (nœud de fin de mission ; le jeu lui-même n'a rien annoncé au mod pendant cette bataille)");
            else l.Add("résultat : le jeu ne l'a pas annoncé au mod pendant cette bataille (non mesuré) ; les tâches ci-dessous disent ce qui a tenu");

            // ---- 2. tasks and objectives
            l.Add(LigneTaches(gc));
            string obj = LigneObjectifs(gc, campLocal);
            if (obj != null) l.Add(obj);

            // ---- 3. what it cost
            l.AddRange(LignesPertes(gc));

            // ---- 4. the most costly moment
            string pire = LignePireMoment();
            if (pire != null) l.Add(pire);

            // ---- 5. what was left standing, ammunition and supply
            l.AddRange(LignesFin(gc, campLocal));

            // ---- 6. the raw reading, for the next log
            l.Add(LigneReleve(gc));
            return l;
        }

        static GameController TryGc() { try { return GameController._instance; } catch { return null; } }
        static string SafeMission() { try { return Campaign.MissionUid; } catch { return null; } }

        static int CampLocal(GameController gc)
        {
            try
            {
                var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
                if (cp != null) return (int)cp.TeamSide;
            }
            catch { }
            return -1;
        }

        // ---- tasks: the game's own panel, with the game's own already-translated names
        static string LigneTaches(GameController gc)
        {
            Il2CppSystem.Collections.Generic.List<TaskData> liste = null;
            try { liste = gc?._taskPanel?._taskDataList; } catch { }
            if (liste == null) return "tâches : la liste des tâches du jeu n'a pas pu être lue (non mesuré)";

            int actives = 0, reussies = 0, echouees = 0, cachees = 0, total = 0;
            var echecs = new List<string>(MaxTachesCitees);
            var ouvertes = new List<string>(MaxTachesCitees);
            int n = 0;
            try { n = Math.Min(liste.Count, 200); } catch { }
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var t = liste[i]; if (t == null) continue;
                    total++;
                    int st = (int)t.Status, ty = (int)t.Type;
                    string nom = t.DisplayedName ?? "";
                    string genre = ty == 0 ? "principale" : "secondaire";
                    if (st == 1) reussies++;
                    else if (st == 2) { echouees++; if (echecs.Count < MaxTachesCitees) echecs.Add("« " + nom + " » (" + genre + ")"); }
                    else if (st == 3) cachees++;
                    else { actives++; if (ouvertes.Count < MaxTachesCitees) ouvertes.Add("« " + nom + " » (" + genre + ")"); }
                }
                catch { }
            }
            if (total == 0) return "tâches : la mission n'en a affiché aucune";

            var sb = new StringBuilder(200);
            sb.Append("tâches (compteur du jeu, ").Append(total.ToString(CultureInfo.InvariantCulture)).Append(" au total) : ")
              .Append(reussies.ToString(CultureInfo.InvariantCulture)).Append(" réussie(s), ")
              .Append(echouees.ToString(CultureInfo.InvariantCulture)).Append(" échouée(s), ")
              .Append(actives.ToString(CultureInfo.InvariantCulture)).Append(" encore active(s)");
            if (cachees > 0) sb.Append(", ").Append(cachees.ToString(CultureInfo.InvariantCulture)).Append(" cachée(s)");
            if (echecs.Count > 0) sb.Append(" ; échouée(s) : ").Append(string.Join(", ", echecs));
            if (echouees > echecs.Count) sb.Append(" et ").Append((echouees - echecs.Count).ToString(CultureInfo.InvariantCulture)).Append(" autre(s)");
            if (echouees == 0 && ouvertes.Count > 0) sb.Append(" ; laissée(s) en plan : ").Append(string.Join(", ", ouvertes));
            return sb.ToString();
        }

        // ---- objectives: the same reading Missions.cs does, at the last moment of the battle
        static string LigneObjectifs(GameController gc, int campLocal)
        {
            if (gc == null) return null;
            int visibles = 0, aMoi = 0, aLui = 0, autres = 0;
            try
            {
                var map = new LuaMap();
                Il2CppReferenceArray<LuaObjective> objs = map.GetObjectives(false);
                int n = objs == null ? 0 : objs.Length;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var o = objs[i]; if (o == null || !o.IsVisible()) continue;
                        visibles++;
                        int cote = EnemyAi.SideOf(gc, EnemyAi.OwnerRaw(o));
                        if (campLocal >= 0 && cote == campLocal) aMoi++;
                        else if (campLocal >= 0 && cote == 1 - campLocal) aLui++;
                        else autres++;
                    }
                    catch { }
                }
            }
            catch { return null; }
            if (visibles == 0) return null;
            return "objectifs de la carte à la fin (instantané) : " + visibles.ToString(CultureInfo.InvariantCulture) + " visible(s), "
                 + aMoi.ToString(CultureInfo.InvariantCulture) + " à vous, " + aLui.ToString(CultureInfo.InvariantCulture) + " à l'ennemi"
                 + (autres > 0 ? ", " + autres.ToString(CultureInfo.InvariantCulture) + " à personne ou illisible(s)" : "");
        }

        // ---- losses and kills
        static List<string> LignesPertes(GameController gc)
        {
            var res = new List<string>(3);
            long pertes = Interlocked.Read(ref _pertes);
            long dest = Interlocked.Read(ref _destructions);

            if (pertes <= 0 && dest <= 0)
            {
                res.Add("pertes et destructions : le jeu n'a appelé aucun de ses deux compteurs pendant cette bataille — NON MESURÉ. "
                      + "Rien n'est déduit de ce silence : ce n'est pas « zéro perte ».");
                return res;
            }

            // --- my losses, by category and by type, from the database
            if (pertes <= 0)
                res.Add("vos pertes : le compteur de pertes du jeu n'a pas été appelé — non mesuré");
            else
            {
                var sb = new StringBuilder(260);
                // the header says this reading is a deduction; the printed line must say it too, or only the header is honest
                sb.Append("vos pertes : ").Append(N(pertes)).Append(" unité(s) (compteur de succès du jeu ; le mod n'a PAS prouvé que ce compteur ne suit que vos unités)");
                string cats = ParCategorie(_perteId, _perteN, _nPerteTypes, out long valeur, out string types);
                if (cats != null) sb.Append(" — ").Append(cats);
                if (valeur > 0) sb.Append(" ; valeur perdue environ ").Append(N(valeur)).Append(" points (prix de fiche, options non comptées)");
                if (types != null) sb.Append(" ; les plus nombreuses : ").Append(types);
                if (_perteHorsTable > 0) sb.Append(" ; ").Append(N(_perteHorsTable)).Append(" perte(s) au-delà des ").Append(N(MaxTypes)).Append(" types suivis (comptées, non détaillées)");
                long hors = Interlocked.Read(ref _horsFil);
                if (hors > 0) sb.Append(" ; ").Append(N(hors)).Append(" appel(s) de compteur hors fil principal : comptés dans les totaux, pas dans le détail");

                // cross-check: the game keeps its own value of what this player lost. The two are built from different things,
                // so when they part company the report says so instead of picking the one it likes.
                long jeu = ValeurPerdueJoueur(gc);
                if (valeur > 0 && jeu > 0)
                {
                    double ecart = Math.Abs(jeu - valeur) / (double)Math.Max(jeu, valeur);
                    sb.Append(" ; le compteur de valeur du jeu (DeadUnitsValue) dit ").Append(N(jeu)).Append(" points : ")
                      .Append(ecart <= 0.25 ? "les deux concordent" : "les deux ne concordent pas, la répartition par catégorie ci-dessus est donc à prendre avec prudence");
                }
                else if (jeu > 0) sb.Append(" ; le compteur de valeur du jeu (DeadUnitsValue) dit ").Append(N(jeu)).Append(" points");
                res.Add(sb.ToString());
            }

            // --- what I destroyed: the count is real, the category of the victim is not available
            if (dest <= 0)
                res.Add("vos destructions : le compteur de destructions du jeu n'a pas été appelé — non mesuré");
            else
            {
                var sb = new StringBuilder(260);
                sb.Append("vos destructions : ").Append(N(dest)).Append(" destruction(s) comptée(s) par le jeu (compteur de succès ; le mod n'a PAS prouvé que la victime était ennemie ni que le tueur était à vous)");
                string cats = ParCategorie(_destId, _destN, _nDestTypes, out _, out string types);
                if (cats != null) sb.Append(" — obtenues par : ").Append(cats);
                if (types != null) sb.Append(" ; vos meilleures unités : ").Append(types);
                if (_destHorsTable > 0) sb.Append(" ; ").Append(N(_destHorsTable)).Append(" destruction(s) au-delà des ").Append(N(MaxTypes)).Append(" types suivis (comptées, non détaillées)");
                sb.Append(" ; le jeu ne dit pas de quelle catégorie étaient les unités ennemies détruites : NON MESURÉ, et rien n'est deviné");
                res.Add(sb.ToString());
            }
            return res;
        }

        /// What the game itself says this player lost, in points. -1 when it cannot be read.
        static long ValeurPerdueJoueur(GameController gc)
        {
            try
            {
                var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
                if (cp != null) return cp.DeadUnitsValue;
            }
            catch { }
            return -1;
        }

        /// Breaks a (unit id -> count) table into the seven deck categories, using the database. Also gives the total price of the
        /// listed units and the most numerous types by name. Returns null when nothing could be resolved.
        static string ParCategorie(int[] ids, int[] n, int used, out long valeur, out string types)
        {
            valeur = 0; types = null;
            if (used <= 0) return null;
            DataBaseService db = null;
            try { db = DataBaseService._instance; } catch { }
            if (db == null) return null;

            var parCat = new int[CategorieFr.Length];
            int inconnus = 0, resolus = 0;
            var noms = new List<(string nom, int n)>(used);
            for (int i = 0; i < used; i++)
            {
                UnitsRow u = null;
                try { u = db.GetUnitById(ids[i], false); } catch { }
                if (u == null) { inconnus += n[i]; continue; }
                resolus += n[i];
                try { valeur += (long)u.Cost * n[i]; } catch { }
                int idx = -1;
                try { idx = Array.IndexOf(Mod.Categories, u.CategoryType); } catch { }
                if (idx >= 0 && idx < parCat.Length) parCat[idx] += n[i]; else inconnus += n[i];
                string nom = null;
                try { nom = u.HUDName; } catch { }
                if (string.IsNullOrWhiteSpace(nom)) { try { nom = u.Name; } catch { } }
                if (!string.IsNullOrWhiteSpace(nom)) noms.Add((nom, n[i]));
            }
            if (resolus == 0 && inconnus == 0) return null;

            var sb = new StringBuilder(120);
            for (int i = 0; i < parCat.Length; i++)
            {
                if (parCat[i] <= 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(CategorieFr[i]).Append(' ').Append(parCat[i].ToString(CultureInfo.InvariantCulture));
            }
            if (inconnus > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("type non retrouvé dans la base ").Append(inconnus.ToString(CultureInfo.InvariantCulture));
            }

            noms.Sort((a, b) => b.n.CompareTo(a.n));
            var t = new StringBuilder(80);
            for (int i = 0; i < noms.Count && i < MaxTypesCites; i++)
            {
                if (t.Length > 0) t.Append(", ");
                t.Append(noms[i].nom).Append(" x").Append(noms[i].n.ToString(CultureInfo.InvariantCulture));
            }
            if (t.Length > 0) types = t.ToString();
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// The worst minute of the battle. Real-clock minutes, said as such: the game clock is not readable from the hooks.
        static string LignePireMoment()
        {
            int best = -1, bestN = 0;
            for (int i = 0; i < MaxPhases; i++)
            {
                int v = Volatile.Read(ref _phasePertes[i]);
                if (v > bestN) { bestN = v; best = i; }
            }
            if (best < 0 || bestN <= 1) return null;                 // one loss in a minute is not "a costly moment"
            int cout = Volatile.Read(ref _phaseCout[best]);
            var sb = new StringBuilder(180);
            sb.Append("moment le plus coûteux : ").Append(bestN.ToString(CultureInfo.InvariantCulture)).Append(" perte(s) entre la ")
              .Append((best + 1).ToString(CultureInfo.InvariantCulture)).Append("e et la ").Append((best + 2).ToString(CultureInfo.InvariantCulture))
              .Append("e minute de bataille (minutes d'horloge réelle, pauses comprises)");
            if (cout > 0) sb.Append(" ; le jeu chiffre ce qui est mort dans cette minute à ").Append(N(cout)).Append(" points, tous camps confondus");
            if (best == MaxPhases - 1) sb.Append(" ; dernière tranche : elle rassemble tout ce qui dépasse ").Append(N(MaxPhases)).Append(" minutes");
            return sb.ToString();
        }

        // ---- the end-of-battle snapshot: who is left, with what ammunition and what supply
        static List<string> LignesFin(GameController gc, int campLocal)
        {
            var res = new List<string>(2);
            if (gc == null || campLocal < 0 || campLocal > 1)
            {
                res.Add("forces à la fin : le camp du joueur n'a pas pu être lu — non mesuré");
                res.Add(LigneRavitaillement(-1, -1, -1, -1));
                return res;
            }

            int nMoi = 0, nLui = 0;
            long valMoi = 0, valLui = 0;
            long sommeMun = 0; int nMun = 0;
            long cargo = 0; int nCargo = 0;
            try
            {
                var map = new LuaMap();
                for (int cote = 0; cote < 2; cote++)
                {
                    Il2CppReferenceArray<LuaUnit> arr = null;
                    try { arr = map.GetUnits(V3.zero, 1_000_000f, cote, -1); } catch { }
                    int n = arr == null ? 0 : arr.Length;
                    bool mien = cote == campLocal;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            var u = arr[i]; if (u == null || !u.IsAlive()) continue;
                            int cout = 0; try { cout = u.Cost; } catch { }
                            if (mien)
                            {
                                nMoi++; valMoi += cout;
                                int mun = -1; try { mun = u.GetAmmoPercentage(false, false); } catch { }
                                if (mun >= 0 && mun <= 100) { sommeMun += mun; nMun++; }
                                int sup = 0; try { sup = u.GetSupplyCargo(); } catch { }
                                if (sup > 0) { cargo += sup; nCargo++; }
                            }
                            else { nLui++; valLui += cout; }
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                res.Add("forces à la fin : le relevé des unités a échoué — non mesuré");
                res.Add(LigneRavitaillement(-1, -1, -1, -1));
                return res;
            }

            res.Add("forces encore en ligne à la fin (INSTANTANÉ de fin de bataille, ce n'est PAS un compte des pertes) : vous "
                  + N(nMoi) + " unité(s) pour " + N(valMoi) + " points, en face " + N(nLui) + " unité(s) pour " + N(valLui) + " points");
            res.Add(LigneRavitaillement(nMun > 0 ? (double)sommeMun / nMun : -1, nMun, cargo, nCargo));
            return res;
        }

        /// Ammunition and supply. The consumed totals are refused on purpose: the game keeps no running total the mod can read in
        /// campaign, and an estimate presented as a measurement is exactly what this project forbids.
        static string LigneRavitaillement(double moyenneMun, int nMun, long cargo, int nCargo)
        {
            var sb = new StringBuilder(280);
            sb.Append("munitions et ravitaillement : la CONSOMMATION totale de la bataille n'est pas tenue par le jeu — NON MESURÉE, et elle n'est pas estimée");
            if (nMun > 0) sb.Append(" ; à la fin il restait en moyenne ").Append(Pc(moyenneMun)).Append(" % de munitions sur ").Append(N(nMun)).Append(" de vos unités");
            if (nCargo > 0) sb.Append(" ; vos unités transportaient encore ").Append(N(cargo)).Append(" de ravitaillement (").Append(N(nCargo)).Append(" porteur(s))");
            if (nMun <= 0 && nCargo <= 0) sb.Append(" ; l'état de fin n'a pas pu être lu non plus");
            return sb.ToString();
        }

        // ---- the raw reading: everything the author needs to check the deductions above on his next log
        static string LigneReleve(GameController gc)
        {
            var sb = new StringBuilder(700);
            long morts = Interlocked.Read(ref _morts), cout = Interlocked.Read(ref _coutMorts);
            sb.Append("relevé brut (à vérifier sur le prochain log, rien n'en est déduit dans les lignes ci-dessus sauf là où c'est dit) : ")
              .Append("compteur de morts du jeu (StatisticService.OnUnitDeath) ").Append(N(morts)).Append(" appel(s)");
            if (morts > 0) sb.Append(", coût total annoncé ").Append(N(cout)).Append(" points");
            sb.Append(" ; compteur de succès : pertes ").Append(N(Interlocked.Read(ref _pertes)))
              .Append(", destructions ").Append(N(Interlocked.Read(ref _destructions)))
              .Append(" ; hors fil principal ").Append(N(Interlocked.Read(ref _horsFil)))
              .Append(" ; erreurs ").Append(N(_erreurs));

            // what the identifiers of OnUnitDeath actually are: asked, never assumed
            sb.Append(" ; ").Append(Identifiants(gc));

            // the game's own per-player figures, for the cross-check
            sb.Append(" ; ").Append(Joueurs(gc));
            sb.Append(" ; module ").Append(GuardVersion).Append(", aucune valeur écrite dans le jeu");
            return sb.ToString();
        }

        /// Resolves the identifiers seen in OnUnitDeath against the session. Only when every one of them is a player of this
        /// battle does the module print a per-side split, and it says the deduction is a deduction.
        static string Identifiants(GameController gc)
        {
            if (_nIds == 0) return "identifiants du compteur de morts : aucun vu";
            if (_idsDebordent) return "identifiants du compteur de morts : plus de " + N(MaxIds) + " distincts — ce ne sont donc PAS des uid de joueurs, la répartition par camp est impossible et n'est pas tentée";

            GameSessionContext ctx = null;
            try { ctx = Campaign.Ctx(); } catch { }
            var sb = new StringBuilder(320);
            sb.Append("identifiants du compteur de morts : ").Append(N(_nIds)).Append(" distinct(s) [");
            bool tousJoueurs = ctx != null;
            for (int i = 0; i < _nIds; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(_id[i].ToString(CultureInfo.InvariantCulture));
                string nom = null; int camp = -1;
                if (ctx != null)
                {
                    PlayerInfo p = null;
                    try { ctx.TryGetPlayer(_id[i], out p); } catch { p = null; }
                    if (p != null) { try { nom = p.PlayerName; } catch { } try { camp = (int)p.TeamSide; } catch { } }
                    else tousJoueurs = false;
                }
                sb.Append(nom != null ? " « " + nom + " » équipe " + camp.ToString(CultureInfo.InvariantCulture) : " (pas un joueur de cette partie)");
                sb.Append(" victime ").Append(_idVictime[i].ToString(CultureInfo.InvariantCulture))
                  .Append(", tueur ").Append(_idTueur[i].ToString(CultureInfo.InvariantCulture));
                if (_idCout[i] > 0) sb.Append(", ").Append(N(_idCout[i])).Append(" points perdus");
            }
            sb.Append(']');
            sb.Append(tousJoueurs
                ? " -> tous reconnus comme des joueurs de cette partie : le mod EN DÉDUIT que « victime » compte les unités perdues par ce joueur et « tueur » celles qu'il a détruites (déduction, pas une mesure : à confirmer sur une deuxième bataille)"
                : " -> tous ne sont pas des joueurs de cette partie : ce sont donc des identifiants d'unités ou de camps, la répartition par joueur n'est PAS faite");
            return sb.ToString();
        }

        /// The game's own per-player figures. Read once, printed as read, with the names the game uses.
        static string Joueurs(GameController gc)
        {
            GameSessionContext ctx = null;
            try { ctx = Campaign.Ctx(); } catch { }
            if (ctx == null) return "valeurs des joueurs : partie illisible";
            int annonces = -1;
            try { annonces = ctx.PlayersCount; } catch { }

            object bus = null;
            try { bus = gc?._GetEcsEventBus_k__BackingField?.Gameplay; } catch { }

            var sb = new StringBuilder(400);
            sb.Append("valeurs des joueurs (compteurs du jeu");
            if (annonces >= 0) sb.Append(", ").Append(N(annonces)).Append(" joueur(s) annoncé(s)");
            sb.Append(") :");
            int vus = 0;
            // the mod never gets AddPlayer for campaign bots (EnemyAi.cs:196), so the uids are probed instead of enumerated:
            // a campaign uses a handful of low uids, and TryGetPlayer simply says no for the rest.
            for (int uid = 0; uid <= 15 && vus < 8; uid++)
            {
                PlayerInfo p = null;
                try { ctx.TryGetPlayer(uid, out p); } catch { p = null; }
                if (p == null) continue;
                vus++;
                sb.Append(vus > 1 ? " | " : " ").Append("uid ").Append(uid.ToString(CultureInfo.InvariantCulture));
                try { sb.Append(" « ").Append(p.PlayerName ?? "?").Append(" »"); } catch { }
                try { sb.Append(p.IsBot ? " IA" : " humain"); } catch { }
                try { sb.Append(" équipe ").Append(((int)p.TeamSide).ToString(CultureInfo.InvariantCulture)); } catch { }
                if (uid == _uidLocal) sb.Append(" (VOUS)");
                try { sb.Append(" ; unités en ligne ").Append(N(p.BattlefieldValue)); } catch { }
                try { sb.Append(", unités perdues ").Append(N(p.DeadUnitsValue)); } catch { }
                if (bus != null)
                {
                    double? argent = Invoquer(bus, "GetMoney", uid);
                    if (argent.HasValue) sb.Append(", argent ").Append(Pc(argent.Value));
                }
                string stat = Statistique(p);
                if (stat != null) sb.Append(" ; ").Append(stat);
            }
            if (vus == 0) sb.Append(" aucun joueur lisible sur les uid 0 à 15");
            return sb.ToString();
        }

        /// One of the economy delegates of the gameplay bus (GetMoney, GetIncome, GetBattlefieldValue), read by reflection so a
        /// renamed delegate costs one clause and not the report.
        static double? Invoquer(object bus, string nom, int uid)
        {
            try
            {
                var prop = Props.Get(bus.GetType(), nom);
                var del = prop?.GetValue(bus);
                if (del == null) return null;
                var invoke = del.GetType().GetMethod("Invoke");
                if (invoke == null) return null;
                object v = invoke.Invoke(del, new object[] { uid });
                return v switch { float f => float.IsNaN(f) ? (double?)null : (double?)f, double d => (double?)d, int i => (double?)i, _ => (double?)null };
            }
            catch { return null; }
        }

        /// PlayerInfo.Statistic is a NetworkCommon row whose exact member list is not in the dumps this project holds, so it is
        /// read by name through the codebase's reflection helper: a member that does not exist simply does not appear, and a row
        /// the campaign never fills prints "tous à zéro" instead of a made-up figure.
        static readonly string[] StatChamps =
        {
            "Killed", "Losses", "LossesByFriendlyFire", "ObjectivesCaptured",
            "SupplyPointsConsumed", "TotalSupplyPointsConsumed", "SupplyAirdropped", "SupplyCaptured", "SupplyDestroyed",
            "DamageReceived", "TotalDamageReceived", "DamageFriendlyFireReceived",
        };
        static readonly string[] StatFr =
        {
            "tués", "pertes", "pertes par tir ami", "objectifs pris",
            "ravitaillement consommé", "ravitaillement consommé (total)", "ravitaillement parachuté", "ravitaillement capturé", "ravitaillement détruit",
            "dégâts reçus", "dégâts reçus (total)", "dégâts de tir ami reçus",
        };

        static string Statistique(PlayerInfo p)
        {
            // read through reflection, not p.Statistic: the row lives in Il2CppNetworkCommon and neither its shape nor its
            // presence is guaranteed by the dumps this project holds. A missing property costs one clause, never the build.
            object st = null;
            try { st = Props.Get(typeof(PlayerInfo), "Statistic")?.GetValue(p); } catch { }
            if (st == null) return null;
            var sb = new StringBuilder(160);
            int lus = 0, nonNuls = 0;
            for (int i = 0; i < StatChamps.Length; i++)
            {
                double? v = null;
                try { v = Props.Num(st, StatChamps[i]); } catch { }
                if (!v.HasValue) continue;
                lus++;
                if (Math.Abs(v.Value) < 0.5) continue;
                nonNuls++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(StatFr[i]).Append(' ').Append(Pc(v.Value));
            }
            if (lus == 0) return "compteur ClientStatistic : aucun des champs attendus n'a pu être lu en nombre dans cette version du jeu";
            if (nonNuls == 0) return "compteur ClientStatistic : " + N(lus) + " champ(s) lu(s), tous à zéro (le jeu ne le remplit pas en campagne — à confirmer)";
            return "compteur ClientStatistic : " + sb;
        }

        // ------------------------------------------------------------ errors
        static void Erreur(string quoi, Exception e)
        {
            int n = Interlocked.Increment(ref _erreurs);
            try
            {
                if (n <= 3) Mod.Log.Warning(P + quoi + " : erreur (" + e.GetBaseException().Message + ") ; le bilan n'est qu'une lecture, rien n'est modifié dans le jeu");
                if (n >= MaxErreurs && !_mort)
                {
                    _mort = true;
                    _bataille = false;
                    Mod.Log.Warning(P + N(MaxErreurs) + " erreurs : plus aucun bilan de fin de bataille jusqu'au redémarrage du jeu "
                        + "(ce module ne fait que lire et écrire des lignes : le jeu se comporte exactement pareil sans lui)");
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ TEXTES A TRADUIRE (Txt.cs, cinq langues) - fichier non possédé ici
        //  Pour un panneau à l'écran plus tard, il faudrait traduire exactement ces morceaux (les noms de tâches, les noms
        //  d'unités et les noms de joueurs viennent déjà traduits du jeu, il n'y a rien à faire pour eux) :
        //   BI_TITRE            "bilan de la bataille — mission {0}"
        //   BI_DUREE            "durée {0} (horloge réelle, pauses comprises)" / "horloge de partie du jeu {0}"
        //   BI_RESULTAT         "VICTOIRE" / "DÉFAITE" / "ni victoire ni défaite" / "résultat non annoncé par le jeu"
        //                       + "résultat annoncé par le script de la mission" (nœud de fin de mission)
        //   BI_TACHES           "tâches : {0} réussie(s), {1} échouée(s), {2} encore active(s)" + "échouée(s) :" + "laissée(s) en plan :"
        //   BI_OBJECTIFS        "objectifs de la carte à la fin : {0} visible(s), {1} à vous, {2} à l'ennemi"
        //   BI_PERTES           "vos pertes : {0} unité(s)" + "valeur perdue environ {0} points" + "les plus nombreuses :"
        //   BI_DESTRUCTIONS     "vos destructions : {0} destruction(s) comptée(s) par le jeu" (+ la réserve sur la victime et le tueur,
        //                       qui doit rester aussi nette dans les cinq langues : c'est une déduction, pas une donnée du jeu)
        //   BI_PIRE_MOMENT      "moment le plus coûteux : {0} perte(s) entre la {1}e et la {2}e minute"
        //   BI_FORCES_FIN       "forces encore en ligne à la fin : vous {0} unité(s) pour {1} points, en face {2} pour {3}"
        //   BI_RAVITAILLEMENT   "il restait en moyenne {0} % de munitions" + "vos unités transportaient encore {0} de ravitaillement"
        //   BI_NON_MESURE       "non mesuré" (la formule qui revient partout ; elle doit rester aussi nette dans les cinq langues)
        //   BI_INSTANTANE       "instantané de fin de bataille, ce n'est pas un compte des pertes"
        //  Les sept catégories (reconnaissance, infanterie, véhicules, appui, logistique, hélicoptères, avions) existent déjà
        //  côté jeu : reprendre les textes du jeu plutôt que d'en écrire de nouveaux.
        //
        // ------------------------------------------------------------ A FAIRE AILLEURS (fichiers non possédés ici)
        //  ModLog.cs, build PUBLIC : ajouter "[BILAN]" à PublicLog.KeepStarts, à côté de "[CAMPAGNE]". Le bilan part en UN SEUL appel
        //  à Mod.Log.Msg, donc le filtre décide une fois pour tout le bilan et il passe déjà au moins une fois par tranche de
        //  5 minutes ; mais deux batailles courtes enchaînées dans la même tranche feraient sauter le second bilan. Une ligne dans
        //  KeepStarts règle ça. En attendant, les deux lignes de diagnostic de ce fichier (« chien de garde : début de bataille vu »
        //  et « chien de garde : le début de cette bataille n'a pas été vu ») portent « chien de garde », qui est dans
        //  PublicLog.KeepParts : elles passent toujours, et elles suffisent à dire si le module est vivant.
    }
}
