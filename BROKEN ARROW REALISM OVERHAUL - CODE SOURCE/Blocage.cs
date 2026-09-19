// RealismOverhaul - Blocage (v1.0): softlock watchdog of a campaign mission. Log only.
//  Why: on 2026-09-18 a player lost an hour and a half on a mission that had become impossible to finish (a scripted helicopter died
//  and the mission graph had no fallback wired behind it). Every number needed to see it was already measured by Missions, and nothing
//  said a word. This module reads those numbers and writes one French paragraph when the mission looks stuck.
//  It is incapable of changing a battle: no Harmony patch, no database write, no order, no ECS access, nothing on screen. It reads
//  static fields of Missions (same assembly, reflection, cached FieldInfo) plus two counters of SanteCombat, and calls Mod.Log.
//
//  THE RULE (all of it must hold, at the same moment, or nothing is written):
//   - the mission is tracked and trustworthy: Missions.ProtectionNotReady() says null (session armed, the five order hooks armed and
//     complete, no order lost, script graph read, groups read), the script's move system is known, and the frozen-time accounting of
//     Missions ran less than WaysFresh seconds ago. Any of these missing for one sample restarts the whole measurement (fail closed);
//   - at least one task is still active (a mission with nothing active is finishing, not stuck);
//   - the impact counter of SanteCombat is installed, otherwise "is combat raging" cannot be answered and we say nothing;
//   - no task changed state, no script order was issued and no script spawn landed, for Silence seconds of REAL RUNNING GAME TIME;
//   - combat over that same stretch stays under CombatMax impacts per running minute.
//
//  RUNNING GAME TIME is the whole point. Time.time keeps running while this game is paused (its timeScale stays 1, the simulation is
//  what stops), so a player who leaves the battle paused piles up wall-clock silence with nothing wrong. Missions already measures that:
//  _wayUncounted is the time its stall timers refuse to count (zero timeScale, or the position signature of every scanned unit unchanged,
//  or a gap where its own pass did not run). Every stretch below is wall time minus the _wayUncounted that accumulated inside it.
//  Proof from E:\...\MelonLoader\Logs\26-9-18_19-2-1.log (RU_C01, the 45-minute lull of that evening, t=1807s to t=4518s): the wall-clock
//  silence grew from 1073 s to 3785 s, while _wayUncounted grew from 537 s to 3228 s - the same 2711 s, to within the sampling error.
//  In running time the three stretches stayed flat the whole lull: tasks 623-631 s, script orders 57-66 s, script spawns 80-89 s. The lull
//  cannot fire the rule, whatever its wall-clock length, because it contains almost no running time at all: the task silence alone would
//  cross a 600 s threshold, but the rule needs the three together and the script-order silence never leaves 66 s - a margin of 9x.
//
//  THRESHOLDS, recomputed with this module's own arithmetic from the six campaign logs of 2026-09-17 and 2026-09-18 (34 "[MISSION] relevé"
//  samples, all RU_C01). For every sample the three stretches were rebuilt in running time - the wall-clock stretch minus the _wayUncounted
//  that piled up inside it, interpolated between samples. What decides is NOT the longest stretch of one signal: the three are required at
//  the same moment, so the number to beat is the SMALLEST of the three at a sample. Highest smallest-of-three ever measured: 212 s
//  (26-9-18_14-32-48.log, t=300 s). Inside the 45-minute lull of 2026-09-18 it sits between 57 s and 65 s and stays flat for the whole lull.
//  Taken one by one the longest were tasks 650 s, script orders 213 s, script spawns 212 s.
//  Silence = 600 s is 2.8x the worst sample ever measured and 9x the lull. The first version used 1200 s, which is 5.7x the worst
//  measurement - another way of saying that no block would ever have reached it. 600 s keeps the lull far out of range and leaves a real
//  block a threshold it can actually cross. Combat in those logs runs 10 to 600 impacts/min while fighting and 0 while nothing happens:
//  CombatMax = 60 keeps a real battle from being called a block. Combat alone separates nothing here - the lull was at 0 impacts/min too;
//  only running time separates them.
//
//  WHAT THIS MODULE CANNOT TELL APART, and why the end-of-battle line carries both clocks. Missions charges a 10 s window to frozen time
//  when timeScale is zero OR when the position signature of every tracked unit of both sides did not move. A paused battle and a battle
//  where nothing at all moves read exactly the same. So in a block where the player has given up and parked everything, running time stops
//  advancing and this watchdog stays quiet however long the block lasts. Nothing in the game's own state separates those two cases, so the
//  module does not pretend to: every stretch is reported at the wall clock as well as in running time, and the gap between the two IS the
//  evidence the author reads afterwards (650 s of running time against 3785 s at the wall clock is the shape of the 2026-09-18 evening).
//
//  Player-visible strings would have to go through Txt.cs in five languages. This module does not own Txt.cs, so it writes to the log
//  only and shows nothing on screen. The line carries the words "chien de garde", which the PUBLIC log filter always keeps, so a shared
//  build reports a block too (a new "[BLOCAGE]" prefix is not in ModLog's keep list and would otherwise be thinned to one line per 5 min).
//
//  What it writes: one French paragraph when the rule fires, repeated at most every Repeat seconds of running time and once more when the
//  battle ends; plus one line at the end of every battle with the longest silences really measured and whether the watchdog was ever armed.
//  That last line is how the thresholds above get checked against the missions no log has ever covered; it is a measurement, not an alarm.
//
//  DRIVEN BY Mod.OnUpdate, like every other module of the mod: one line, Perf.Run("Blocage", Blocage.Frame), in its mission block. Frame()
//  is internal, self-throttled and gates itself, so calling it twice in a frame costs one float compare. An earlier version subscribed
//  itself to MelonEvents.OnUpdate from a [ModuleInitializer]; that is gone. At module-initializer time the melon is not registered yet,
//  MelonLoader's Subscribe looks the owning melon up from the stack, and neither the success nor the failure of that lookup can be read
//  from the DLL: a subscriber the loader cannot own is either dropped silently - the module never runs and never says so - or kept with a
//  null owner inside MelonLoader's own dispatch, which is not a place this mod may risk throwing. The first sample of the first battle
//  writes one "surveillance des blocages active" line instead: if that line is absent from a log, the line in Mod.OnUpdate is absent too.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace RealismOverhaul
{
    static class Blocage
    {
        const float TickEvery = 5f;          // seconds between two samples (game seconds, Time.time like Missions)
        const float Silence = 600f;          // running game seconds with no task change, no script order and no script spawn
        const float Repeat = 300f;           // running game seconds before the same finding may be written again
        const float CombatMax = 60f;         // impacts per running minute above which the battle is raging and nothing is written
        const float WaysFresh = 60f;         // the frozen-time accounting of Missions must have run this recently
        const int MaxTasksNamed = 6;         // active tasks named in the line
        const int MaxNameLen = 40;
        const int MaxUnitsWalked = 5000;     // safety cap on the unit walk of a finding
        const int MaxErrors = 5;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- reflection on Missions (same assembly, read only)

        static FieldInfo _fStart, _fTaskT, _fCmdT, _fSpawnT, _fUnc, _fWayLastT, _fLastMoney;
        static FieldInfo _fStuckNow, _fWaysNow, _fCmdSeen, _fSpawnSeen;
        static FieldInfo _fTasks, _fUnits, _fMoveSys, _fArmed, _fSessionArmed;
        static bool _bound;

        // ---------------------------------------------------------------- state

        static bool _off;                    // kill-switch: this module says nothing more for the rest of the game run
        static int _errors;
        static float _next;                  // next sample (Time.time)
        static bool _saidAlive;              // the one "surveillance active" line of this game run (its absence is how a missing wiring shows)

        static float _seenStart = -1f;       // Missions._start of the battle being followed, -1 = no battle followed
        static float _closedStart = float.NaN;   // the battle already closed: Missions keeps its _start until the next mission loads
        static int _samples;                 // samples taken in this battle (a battle never really sampled gets no end-of-battle line)
        static string _mission;

        // last seen timestamp of each signal, and what the frozen-time counter and the impact counter read when it last changed
        static float _tAt = float.NaN, _tUnc, _cAt = float.NaN, _cUnc, _sAt = float.NaN, _sUnc;
        static long _tImp, _cImp, _sImp;

        static float _readyRun = -1f;        // running time at which the readiness became true and stayed true, -1 = not ready
        static bool _everReady;              // the watchdog was armed at least once in this battle
        static string _whyNot;               // last reason it was not (literal, kept for the end-of-battle line)
        static float _lastFireRun = -1f;
        static int _fires;
        static string _lastSay;              // last finding of this battle, written again once when the battle ends
        static float _maxTaskRun, _maxCmdRun, _maxSpawnRun;      // longest stretches of this battle in running time, for the end-of-battle line
        static float _maxTaskWall, _maxCmdWall, _maxSpawnWall;   // the same three at the wall clock: the gap with the line above is the evidence
        static float _lastUnc;               // frozen time read at the last sample: Missions wipes its own before Close() runs on two paths

        /// Wrapped like every other write of this module: in the PUBLIC build Msg goes through a filter that takes a lock and two
        /// dictionaries, and Frame() is called from a loop with no handler of ours above it.
        static void Log(string s) { try { var l = Mod.Log; if (l != null) l.Msg("[BLOCAGE] " + s); } catch { } }
        static string N(float v) => v.ToString("0", Inv);
        static string N(int v) => v.ToString(Inv);
        static string N(long v) => v.ToString(Inv);

        // ---------------------------------------------------------------- driver

        /// Every frame, from Mod.OnUpdate (main thread), after Missions.Frame so a sample reads the state Missions refreshed this frame.
        /// Cheap gate first, one sample every TickEvery seconds. Self-throttled, so being called twice in the same frame costs one float
        /// compare, and this module has no other driver: see the note at the top of the file about the module initializer that was here.
        internal static void Frame()
        {
            if (_off) return;
            try
            {
                if (!Campaign.InCampaign || Campaign.MissionInerte || Campaign.BattleOver
                    || Identite.Blocked || Mod.Actif == null || !Mod.Actif.Value)
                {
                    if (_seenStart >= 0f) Close();          // mission left, mod switched off or end screen up: close this battle once
                    return;
                }
                float now = UnityEngine.Time.time;
                if (now < _next) return;
                _next = now + TickEvery;
                Step(now);
            }
            catch (Exception e) { Fault(e); }
        }

        static void Fault(Exception e)
        {
            _errors++;
            if (_errors >= MaxErrors)
            {
                _off = true;
                Log("chien de garde : erreurs répétées (" + e.GetBaseException().Message + ") : surveillance des blocages arrêtée pour cette session de jeu ; rien d'autre n'est touché");
                return;
            }
            Log("chien de garde : erreur (" + e.GetBaseException().Message + ") : ce relevé est ignoré");
        }

        // ---------------------------------------------------------------- binding

        /// Resolves once every static field of Missions this module reads, and checks its type. Anything missing or of another type
        /// (a rename in a later version of the mod) switches the module off for good instead of guessing: a wrong field would mean a
        /// wrong alarm, and a false alarm every battle is exactly what would make this log unreadable.
        static bool Bind()
        {
            if (_bound) return !_off;
            _bound = true;
            try
            {
                Type m = typeof(Missions);
                _fStart = F(m, "_start", typeof(float));
                _fTaskT = F(m, "_lastTaskT", typeof(float));
                _fCmdT = F(m, "_lastCmdT", typeof(float));
                _fSpawnT = F(m, "_lastSpawnT", typeof(float));
                _fUnc = F(m, "_wayUncounted", typeof(float));
                _fWayLastT = F(m, "_wayLastT", typeof(float));
                _fLastMoney = F(m, "_lastMoney", typeof(float));
                _fStuckNow = F(m, "_stuckNow", typeof(int));
                _fWaysNow = F(m, "_waysNow", typeof(int));
                _fCmdSeen = F(m, "_cmdSeen", typeof(int));
                _fSpawnSeen = F(m, "_spawnNotes", typeof(int));
                _fArmed = F(m, "_armed", typeof(bool));
                _fSessionArmed = F(m, "_sessionArmed", typeof(bool));
                _fTasks = F(m, "_tasks", typeof(Dictionary<int, (int, string, int)>));      // no element names: typeof refuses them
                _fMoveSys = F(m, "_moveSys", null);
                _fUnits = F(m, "_units", null);
                if (_fUnits != null && !typeof(IDictionary).IsAssignableFrom(_fUnits.FieldType)) _fUnits = null;
            }
            catch { }
            if (_fStart == null || _fTaskT == null || _fCmdT == null || _fSpawnT == null || _fUnc == null || _fWayLastT == null
                || _fLastMoney == null || _fStuckNow == null || _fWaysNow == null || _fCmdSeen == null || _fSpawnSeen == null
                || _fArmed == null || _fSessionArmed == null || _fTasks == null || _fMoveSys == null || _fUnits == null)
            {
                _off = true;
                Log("chien de garde : les relevés de la mission ne sont pas lisibles dans cette version du mod : surveillance des blocages arrêtée (rien d'autre n'est touché)");
                return false;
            }
            return true;
        }

        static FieldInfo F(Type t, string name, Type want)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) return null;
            return want == null || f.FieldType == want ? f : null;
        }

        static float Flt(FieldInfo f) => (float)f.GetValue(null);
        static int Int(FieldInfo f) => (int)f.GetValue(null);
        static bool Bit(FieldInfo f) => (bool)f.GetValue(null);

        // ---------------------------------------------------------------- one sample

        static void Step(float now)
        {
            if (!Bind()) return;
            if (!_saidAlive)
            {
                // the only proof that the line in Mod.OnUpdate is really there: a log without it is a log where this module never ran
                _saidAlive = true;
                Log("chien de garde : surveillance des blocages active (un relevé toutes les " + N(TickEvery)
                    + " s, seuil " + N(Silence) + " s de jeu réel sans tâche, sans ordre et sans apparition du script)");
            }

            float start = Flt(_fStart);
            if (start < 0f) { if (_seenStart >= 0f) Close(); return; }       // Missions has no battle: nothing to watch
            if (_seenStart < 0f && start.Equals(_closedStart)) return;       // battle already closed: never open it again on its leftovers
            if (start != _seenStart) NewBattle(start);                       // a different battle (or the first one): start again
            if (!Bit(_fSessionArmed)) { Close(); return; }                   // Missions closed this battle (end, online, mod off)
            _samples++;

            float unc = Flt(_fUnc);
            if (unc < 0f) unc = 0f;
            _lastUnc = unc;                                                  // kept: Missions wipes _wayUncounted before Close() can read it
            float run = (now - start) - unc;                                 // running game time of this battle, pauses removed
            if (run < 0f) run = 0f;

            long imp = 0;
            bool impOk = false;
            try { impOk = SanteCombat.ImpactCounterInstalled; imp = SanteCombat.Impacts; } catch { impOk = false; }

            // remember, for each signal, when it last moved and what the two counters read at that moment
            Mark(Flt(_fTaskT), ref _tAt, ref _tUnc, ref _tImp, unc, imp);
            Mark(Flt(_fCmdT), ref _cAt, ref _cUnc, ref _cImp, unc, imp);
            Mark(Flt(_fSpawnT), ref _sAt, ref _sUnc, ref _sImp, unc, imp);

            float runTask = Since(_tAt, _tUnc, start, now, unc);
            float runCmd = Since(_cAt, _cUnc, start, now, unc);
            float runSpawn = Since(_sAt, _sUnc, start, now, unc);
            if (runTask > _maxTaskRun) _maxTaskRun = runTask;
            if (runCmd > _maxCmdRun) _maxCmdRun = runCmd;
            if (runSpawn > _maxSpawnRun) _maxSpawnRun = runSpawn;

            // the same three stretches at the wall clock. A battle where these grow while the ones above stay flat is a battle that spent
            // its time frozen: either paused, or standing still with nobody moving. The module cannot tell those apart, so it reports both.
            float wallTask = Wall(_tAt, start, now), wallCmd = Wall(_cAt, start, now), wallSpawn = Wall(_sAt, start, now);
            if (wallTask > _maxTaskWall) _maxTaskWall = wallTask;
            if (wallCmd > _maxCmdWall) _maxCmdWall = wallCmd;
            if (wallSpawn > _maxSpawnWall) _maxSpawnWall = wallSpawn;

            // readiness: everything below must have been true without a break for the whole stretch, or the stretch is not evidence.
            // Every reason is a literal, so keeping the last one costs nothing.
            string why;
            try
            {
                if (!impOk) why = "compteur d'impacts non installé";         // "is combat raging" cannot be answered: say nothing
                else if (!Bit(_fArmed)) why = "crochets des ordres du script désarmés";
                else
                {
                    why = Missions.ProtectionNotReady();
                    if (why == null && _fMoveSys.GetValue(null) == null) why = "système des trajets du script inconnu";
                    else if (why == null && now - Flt(_fWayLastT) > WaysFresh) why = "comptage du temps figé à l'arrêt";
                }
            }
            catch { why = "état de la mission illisible"; }
            if (why != null) { _readyRun = -1f; _whyNot = why; return; }      // without the move system and its pass, frozen time is not counted
            _whyNot = null;
            if (_readyRun < 0f) { _readyRun = run; _everReady = true; return; }
            if (run - _readyRun < Silence) return;

            if (runTask < Silence || runCmd < Silence || runSpawn < Silence) return;

            // combat over the stretch, measured from the most recent of the three signals (the one that opened the silence)
            float window = runTask; long impBase = _tImp;
            if (runCmd < window) { window = runCmd; impBase = _cImp; }
            if (runSpawn < window) { window = runSpawn; impBase = _sImp; }
            if (window < 1f) return;
            long hits = imp - impBase; if (hits < 0L) hits = 0L;
            float perMin = hits * 60f / window;
            if (perMin > CombatMax) return;                                   // a battle is raging: the player has something to do

            if (_lastFireRun >= 0f && run - _lastFireRun < Repeat) return;

            int active = 0, won = 0, failed = 0;
            string names = Tasks(ref active, ref won, ref failed);
            if (active <= 0) return;                                          // nothing active left: the mission is finishing, not stuck

            _lastFireRun = run;
            _fires++;
            Say(now, start, unc, runTask, runCmd, runSpawn, wallTask, wallCmd, wallSpawn, active, won, failed, names, perMin);
        }

        /// Snapshots the frozen-time counter and the impact counter the first time a signal is seen at a new timestamp. The snapshot is
        /// taken at the sample that noticed the change, so it can be up to TickEvery late. Frozen time only grows, so a late snapshot is a
        /// LARGER uncAt, less frozen time subtracted, and a measured stretch that is longer than the truth: the error is loud, not quiet.
        /// It is bounded by the frozen time of one tick per signal and per battle (5 s against a 600 s threshold).
        static void Mark(float t, ref float at, ref float unc, ref long imp, float uncNow, long impNow)
        {
            if (at.Equals(t)) return;
            at = t; unc = uncNow; imp = impNow;
        }

        /// Running game time since a signal last moved: wall time since it, minus the frozen time that piled up inside that stretch.
        /// A signal that never fired in this battle counts from the start of the battle.
        static float Since(float at, float uncAt, float start, float now, float unc)
        {
            float from = at < 0f ? start : at;
            float r = (now - from) - (unc - uncAt);
            return r < 0f ? 0f : r;
        }

        /// The same stretch at the wall clock, frozen time included. Nothing is ever decided on this figure; it is only reported.
        static float Wall(float at, float start, float now)
        {
            float r = now - (at < 0f ? start : at);
            return r < 0f ? 0f : r;
        }

        // ---------------------------------------------------------------- the finding

        static void Say(float now, float start, float unc, float runTask, float runCmd, float runSpawn,
                        float wallTask, float wallCmd, float wallSpawn,
                        int active, int won, int failed, string names, float perMin)
        {
            int way = 0, cmd = 0, spawn = 0, grpCmd = 0, cited = 0;
            bool unitsRead = Units(now, ref way, ref cmd, ref spawn, ref grpCmd, ref cited);
            int held = way + cmd + spawn + grpCmd + cited;
            int waysNow = Int(_fWaysNow), stuck = Int(_fStuckNow);
            float money = Flt(_fLastMoney);
            bool contact = false;
            try { contact = SanteCombat.InContact; } catch { }

            var sb = new StringBuilder(1024);
            sb.Append("la mission ").Append(_mission ?? "?").Append(" semble bloquée. Depuis ")
              .Append(N(runTask)).Append(" s de jeu réel (").Append(N(runTask / 60f)).Append(" min ; ").Append(N(wallTask))
              .Append(" s à l'horloge ; ").Append(N(now - start)).Append(" s au compteur de la bataille, dont ").Append(N(unc))
              .Append(" s de temps figé ou non suivi, retirés) : aucune tâche n'a changé d'état, le script n'a donné aucun ordre (dernier il y a ")
              .Append(N(runCmd)).Append(" s de jeu réel, ").Append(N(wallCmd)).Append(" s à l'horloge, ").Append(N(Int(_fCmdSeen)))
              .Append(" vus depuis le début) et n'a fait apparaître aucune unité (dernière il y a ")
              .Append(N(runSpawn)).Append(" s de jeu réel, ").Append(N(wallSpawn)).Append(" s à l'horloge, ")
              .Append(N(Int(_fSpawnSeen))).Append(" vues depuis le début).");
            sb.Append(" Tâches encore actives ").Append(N(active)).Append(" (réussies ").Append(N(won))
              .Append(", échouées ").Append(N(failed)).Append(") :").Append(names).Append('.');
            if (unitsRead)
                sb.Append(" Unités tenues par le script encore vivantes ").Append(N(held))
                  .Append(" : trajet ").Append(N(way)).Append(", ordre ").Append(N(cmd)).Append(", apparition ").Append(N(spawn))
                  .Append(", groupe commandé ").Append(N(grpCmd)).Append(", groupe cité ").Append(N(cited)).Append('.');
            else sb.Append(" Unités tenues par le script : illisibles au moment du relevé.");
            sb.Append(" Trajets du script actifs ").Append(N(waysNow)).Append(", dont bloqués ").Append(N(stuck)).Append('.');
            sb.Append(" Combat sur la période : ").Append(N(perMin)).Append(" impacts/min, contact entre les deux camps ")
              .Append(contact ? "oui" : "non").Append('.');
            sb.Append(" Argent du joueur ").Append(float.IsNaN(money) ? "?" : N(money)).Append('.');
            sb.Append(" Rien n'est modifié : cette ligne ne sert qu'au diagnostic. À regarder d'abord : le dernier ordre du script dans les lignes [MISSION], les unités que le script tient encore, et le script de la mission écrit dans UserData\\RealismOverhaul_missions.");
            if (_fires > 1) sb.Append(" (alerte ").Append(N(_fires)).Append(" de cette bataille)");
            _lastSay = sb.ToString();                                        // repeated once at the end of the battle
            Log("chien de garde : " + _lastSay);
        }

        /// Active tasks of the mission, named, and the counts of the three states. Reads the copy Missions already keeps.
        static string Tasks(ref int active, ref int won, ref int failed)
        {
            var sb = new StringBuilder(160);
            try
            {
                var tasks = (Dictionary<int, (int status, string name, int type)>)_fTasks.GetValue(null);
                if (tasks == null) return " (liste des tâches vide)";
                foreach (var kv in tasks)
                {
                    int st = kv.Value.status;
                    if (st == 1) { won++; continue; }
                    if (st == 2) { failed++; continue; }
                    if (st != 0) continue;                                   // 3 = hidden, anything else = unknown: not counted as active
                    active++;
                    if (active > MaxTasksNamed) continue;
                    string nm = kv.Value.name ?? "";
                    if (nm.Length > MaxNameLen) nm = nm.Substring(0, MaxNameLen);
                    sb.Append(' ').Append(N(kv.Key)).Append(" '").Append(nm).Append("' (")
                      .Append(kv.Value.type == 0 ? "principale" : "secondaire").Append(')');
                }
                if (active > MaxTasksNamed) sb.Append(" et ").Append(N(active - MaxTasksNamed)).Append(" autre(s)");
            }
            catch { return " (liste des tâches illisible)"; }
            return sb.Length == 0 ? " (aucune)" : sb.ToString();
        }

        /// Living units the mission script still holds, split the way the [MISSION] relevé splits them. Missions rebuilds its unit table
        /// every 5 s from the living units of both sides, so what is counted here is alive. Walked only when a finding is written.
        static bool Units(float now, ref int way, ref int cmd, ref int spawn, ref int grpCmd, ref int cited)
        {
            try
            {
                var units = (IDictionary)_fUnits.GetValue(null);
                if (units == null) return false;
                int walked = 0;
                foreach (object k in units.Keys)
                {
                    if (++walked > MaxUnitsWalked) break;
                    string r;
                    try { r = Missions.ScriptReason((int)k, now); } catch { continue; }
                    if (r == null) continue;
                    if (r.StartsWith("trajet", StringComparison.Ordinal)) way++;
                    else if (r == "ordre du script") cmd++;
                    else if (r.StartsWith("apparue", StringComparison.Ordinal)) spawn++;
                    else if (r.StartsWith("groupe commandé", StringComparison.Ordinal)) grpCmd++;
                    else cited++;
                }
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- battle boundaries

        static void NewBattle(float start)
        {
            if (_seenStart >= 0f) Close();
            Reset();
            _seenStart = start;
            try { _mission = Campaign.MissionUid; } catch { }
        }

        /// End of the battle: one line with what the longest silences really were, in running game time. It is the only line written when
        /// nothing was found, and it is what tells the author whether Silence is set too high or too low for the next mission.
        static void Close()
        {
            float start = _seenStart;
            _seenStart = -1f;
            if (start < 0f) return;
            _closedStart = start;
            if (_samples <= 0) { Reset(); return; }                          // a battle already over when the module woke up: no line
            try
            {
                float now = UnityEngine.Time.time;
                // _lastUnc, not the live field: Missions wipes _wayUncounted in ClearBattle, and two real paths (leaving the mission,
                // loading the next one) run that before this module closes the battle. A zero there would make the line claim the whole
                // wall clock as real play, which is exactly the figure the author calibrates the threshold on.
                float unc = _lastUnc, run = (now - start) - unc;
                if (run < 0f) run = 0f;
                var sb = new StringBuilder(384);
                sb.Append("chien de garde : bilan de la bataille ").Append(_mission ?? "?").Append(" (t=").Append(N(now - start))
                  .Append(" s, dont ").Append(N(unc)).Append(" s de temps figé ou non suivi ; ").Append(N(run))
                  .Append(" s de jeu réel) : ").Append(_fires == 0 ? "aucun blocage signalé" : N(_fires) + " alerte(s) de blocage")
                  .Append(". Plus longues périodes sans rien, en temps de jeu réel puis à l'horloge : tâches ").Append(N(_maxTaskRun))
                  .Append(" s (").Append(N(_maxTaskWall)).Append(" s), ordres du script ").Append(N(_maxCmdRun))
                  .Append(" s (").Append(N(_maxCmdWall)).Append(" s), apparitions ").Append(N(_maxSpawnRun))
                  .Append(" s (").Append(N(_maxSpawnWall)).Append(" s) ; seuil ").Append(N(Silence))
                  .Append(" s de jeu réel, pour les trois à la fois. Un écart entre les deux chiffres veut dire que la bataille a passé ce "
                        + "temps figée : en pause, ou immobile sans que personne bouge — le mod ne sait pas distinguer les deux. Surveillance armée : ")
                  .Append(_everReady ? "oui" : "non").Append(_whyNot != null ? " (dernière raison : " + _whyNot + ")" : "").Append('.');
                Log(sb.ToString());
                if (_lastSay != null) Log("chien de garde : rappel de la dernière alerte de cette bataille. " + _lastSay);
            }
            catch { }
            Reset();
        }

        static void Reset()
        {
            _mission = null;
            _lastSay = null;
            _fires = 0;
            _samples = 0;
            _readyRun = -1f;
            _everReady = false;
            _whyNot = null;
            _lastFireRun = -1f;
            _tAt = _cAt = _sAt = float.NaN;
            _tUnc = _cUnc = _sUnc = 0f;
            _tImp = _cImp = _sImp = 0L;
            _maxTaskRun = _maxCmdRun = _maxSpawnRun = 0f;
            _maxTaskWall = _maxCmdWall = _maxSpawnWall = 0f;
            _lastUnc = 0f;
        }
    }
}
