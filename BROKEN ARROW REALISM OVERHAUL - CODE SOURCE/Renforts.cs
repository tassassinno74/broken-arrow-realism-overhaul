// RealismOverhaul - Renforts : the attacks the mod adds itself in a solo campaign battle ("un deuxième adversaire").
//  What it does: a few times in a battle it adds 1 to 3 ENEMY ground vehicles, far from the player, at a place where the mission
//  script itself landed units of the same class in this same battle, copying one of the mission's own units (never a unit chosen
//  from the database), with no group name and no deck. The mission's own units, waves and money are never touched.
//  Why an enemy unit is safe and a player unit would not be: a unit owned by an enemy AI player, with an empty UnitGroup and
//  CargoUnitGroup and Assign = false, is not seen by the mission's counters and death detectors. That is only true when the
//  mission's script says so, so every mission is audited first (§ audit below) and the function stays off until it is green.
//  The one thing that has never been proven: this mod has never created a single unit. So the FIRST added unit of every battle is
//  a canary - exactly one light vehicle, at the farthest valid place. The module then checks that it exists, that it moves, that it
//  carries none of the script's group names and that the mission keeps progressing, and only then allows a second shot. Anything
//  wrong stops the module for the battle and writes why. A preference counts the attempts a game never came back from, so two
//  interrupted games in a row switch the spawning off by itself. Nothing is ever REMOVED from a battle: when a unit turns out to be
//  one of the mission's own (it carries a group of the script), the module stops and leaves it exactly where it is, because deleting
//  a unit the mission is waiting for would block the battle for good. For the same reason it stays silent altogether while the
//  landing hook that tells it which units the script itself landed is not in place.
//  Difficulty changes the MANNER, never the quantity: Easy = a careful approach, Medium = a determined one, Hard = a flank along
//  the edge of the play zone. The target is always one of the mission's OWN objective zones: the module never looks at where the
//  player's units are to choose what to attack (it only uses that distance to stay far away, which is a safety rule).
//  Points: after a confirmed arrival the player gets a few deployment points back, and only inside a window the mission script
//  itself opened (he has money) and never in a mission whose script reads money. A mission that counts his units at the end pays
//  once and says so on screen.
//  Audit: read only, from the mission script the mod already writes in UserData\RealismOverhaul_missions\<mission>.txt. A mission
//  without that file (its first play) is never touched. The file is parsed on a background task, the link ids it holds are resolved
//  to camps on the main thread, and the result is written next to it as <mission>_renforts.txt so the reason is always readable.
//  The audit covers the ZONE CAPTURES too (ozCaptured): that door filters on a team, never on a group, so an empty group name hides
//  nothing from it - a mission that watches a capture on the enemy side simply gets no added attack.
//  Hooks: one lazy postfix on NodeDialog.OnActivated() and one on NodeDialog.OnDialogEnd(), both instance methods with NO parameter
//  (so no struct is ever passed by reference), read only, with this module's own Harmony id, never removed. They only tell the
//  module to keep quiet around a cinematic.
//  Everything is inert when Campaign.MissionInerte is true (US_M01), outside a solo campaign battle, or online (Cheats.Allowed).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Il2CppBrokenArrow.Client.Ecs.Campaign;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.Spawn;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Nodes.Spawn;
using DifficultyLevel = Il2CppBrokenArrow.Shared.Ecs.Enums.DifficultyLevel;
using NodeDialog = Il2CppBrokenArrow.ScriptEngine.Nodes.UI.NodeDialog;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class Renforts
    {
        const string GuardVersion = "1.1";

        // ---- timing (game seconds of a battle) ----
        const float FirstShotAt = 480f;          // never before 8 minutes: he must have something deployed
        const float BetweenShots = 600f;         // 10 minutes at least between two shots (the script sends something every ~140 s)
        const float CanaryHold = 300f;           // after the canary, nothing else until it is fully judged
        const float QuietAfterScriptSpawn = 120f;
        const float QuietAfterTask = 60f;
        const float QuietAfterDialog = 60f;
        const long DialogMax = 90_000L;          // a blocking dialogue that never reports its end stops holding the module after 90 s
        const float QuietAfterMoney = 120f;
        const float RetreatAfter = 720f;         // a shot that never fought goes home after 12 minutes
        const float PayAfter = 20f;              // points only 20 s after the arrival is confirmed
        const float RetryAfter = 30f;            // a search that qualified nothing is not redone before this (it costs a place scan)
        const int LightTries = 3;                // refusals before the canary accepts the cheapest vehicle of the battle, whatever its price
        const int ScriptSpawnBurst = 8;          // >= this many script units in the last 180 s: the module keeps quiet
        const float BurstWindow = 180f;

        // ---- distances ----
        const float ZoneMin = 1500f;             // never within this of a zone he holds
        const float SpawnPointMin = 2500f;       // never within this of one of his deployment points
        const float TargetMinAway = 800f;        // the objective zone aimed at is at least this far from the arrival place

        // ---- caps ----
        const int MaxPointsPerMission = 100;
        const float ShareUnits = 0.15f;          // added units <= 15 % of what the script itself spawned on the enemy side
        const float ShareValue = 0.10f;          // added points <= 10 % of the value the script itself spawned
        const float BrakeSkip = 0.25f, BrakeStop = 0.40f;

        internal static MelonPreferences_Entry<string> Mode;
        internal static MelonPreferences_Entry<bool> Points;
        internal static MelonPreferences_Entry<float> Distance;
        static MelonPreferences_Entry<int> _essais;
        static MelonPreferences_Entry<string> _guardVersion;

        // ---------------------------------------------------------------- mission audit (read only)

        sealed class Gate
        {
            internal string Node = "?", Types = "";
            internal int Id, Player, Team;
            internal bool Ground;                 // its type filter could match a ground vehicle
        }

        sealed class Verdict
        {
            internal string Mission = "?";
            internal bool Parsed, NoCreated, NoGetMoney, NoRefundWatch, CountedEvac, HeliWatched;
            internal int Nodes, Links, OpenGates;
            internal readonly List<Gate> Gates = new();
            internal readonly List<(int player, int amount)> Money = new();
            internal readonly List<string> Groups = new();
            internal string Error;
            // filled on the main thread once the link ids are resolved
            internal bool Resolved, Allowed, PayAllowed, PayOnce;
            internal int PayCap;
            internal string Why = "pas encore lu", PayWhy = "pas encore lu";
        }

        static readonly string[] GroundWords = { "vehicle", "vehicules", "véhicule", "infantry", "infanterie", "recon", "support", "logistic", "ground", "tank", "armor", "armour" };
        static readonly string[] AirSeaWords = { "helicopter", "helicoptere", "hélicoptère", "plane", "aircraft", "avion", "ship", "boat", "navire" };

        /// A type / category filter that could match a ground vehicle. Empty, "None" or any word we do not know counts as "yes" (fail closed).
        static bool GroundMatch(string types)
        {
            if (string.IsNullOrWhiteSpace(types)) return true;
            string t = types.ToLowerInvariant();
            if (t == "none" || t == "all" || t == "any") return true;
            foreach (var w in GroundWords) if (t.Contains(w)) return true;
            bool air = false;
            foreach (var w in AirSeaWords) if (t.Contains(w)) { air = true; break; }
            return !air;                                       // only air / sea words: a ground vehicle can never match it
        }
        static bool HeliMatch(string types)
        {
            if (string.IsNullOrWhiteSpace(types)) return false;
            string t = types.ToLowerInvariant();
            return t.Contains("helicopter") || t.Contains("helicoptere") || t.Contains("hélicoptère");
        }

        /// "c123" -> 123, "c0" and anything else -> 0 (no filter).
        static int Link(string v)
        {
            if (string.IsNullOrEmpty(v) || v.Length < 2 || (v[0] != 'c' && v[0] != 'C')) return 0;
            return int.TryParse(v.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;
        }
        static bool Empty(string v) => string.IsNullOrWhiteSpace(v);

        static readonly string[] GroupKeys = { "grf", "gr", "fg", "fg2", "tg", "ug", "groups2", "forgroups", "forgroup2" };
        static bool IsGroupKey(string k)
        {
            if (k.Contains("group")) return true;
            foreach (var g in GroupKeys) if (k == g) return true;
            return false;
        }

        /// Reads the mission script the mod wrote. Background task only: no game call, no Unity call, nothing but the text.
        static Verdict Parse(string path, string mission)
        {
            var v = new Verdict { Mission = mission };
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { v.Error = e.Message; return v; }
            v.NoCreated = v.NoGetMoney = v.NoRefundWatch = true;

            var names = new Dictionary<int, string>(4096);
            var cands = new List<(int id, string node, string player, string team, string group, string types, string target)>();
            var counters = new List<(int id, int player, bool openGroup)>();
            var groups = new HashSet<string>(StringComparer.Ordinal);
            var props = new Dictionary<string, string>(16, StringComparer.Ordinal);
            var wired = new HashSet<int>();                               // node ids whose forUnits port a link fills
            var usTo = new List<(int from, int to)>();                    // countUnits4.us -> node
            int id = 0; string node = null; bool inNodes = false, inLinks = false, sawNodes = false;
            var lines = text.Split('\n');
            text = null;

            void Close()
            {
                if (node == null) return;
                v.Nodes++;
                names[id] = node;
                foreach (var kv in props)
                    if (IsGroupKey(kv.Key) && !Empty(kv.Value) && kv.Value[0] != '{' && kv.Value[0] != '[')
                        foreach (var part in kv.Value.Split(',', ';', '|'))
                        { var g = part.Trim(); if (g.Length > 0 && groups.Count < 600) groups.Add(g); }
                switch (node)
                {
                    case "onUnitCreated2": v.NoCreated = false; break;
                    case "getMoney2":
                    case "getUpkeepedValue": v.NoGetMoney = false; break;
                    case "onUnitRefunded":
                        v.NoRefundWatch = false;
                        Cand("onUnitRefunded", "plr", "team", "groups2", "type", "targetUnit");
                        break;
                    case "countUnits4":
                        Cand("countUnits4", "plf2", "tef2", "grf", "tyf3", null);
                        counters.Add((id, Link(Get("plf2")), Empty(Get("grf"))));
                        break;
                    case "onUnitDead3":
                        Cand("onUnitDead3", "plr", "team", "groups2", Empty(Get("type")) || Get("type") == "None" ? "cat" : "type", "targetUnit");
                        break;
                    case "triggerEnter":
                    case "triggerLeave":
                        Cand(node, "playerF", "teamF", "gr", "types", "unitF");
                        break;
                    // the capture of an objective zone: it filters on a TEAM, never on a group or on a named unit, so nothing the
                    // module does to its own units can hide them from it. An added vehicle that reaches a zone before the player
                    // would make the mission's own capture branch fire (RU_C01: task c302 set to Fail for good). A mission holding
                    // one on the enemy side is simply refused.
                    case "ozCaptured":
                        Cand("ozCaptured", "", "byteam", "", null, null);
                        break;
                    case "setMoney2":
                        if (int.TryParse(Get("amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                            v.Money.Add((Link(Get("tp2")), amount));
                        break;
                }
                props.Clear(); node = null;
            }
            string Get(string k) => props.TryGetValue(k, out var s) ? s : "";
            void Cand(string kind, string kp, string kt, string kg, string kty, string ktarget)
            {
                if (!Empty(Get(kg))) return;                                   // a group filter: our unit has no group, it cannot match
                if (ktarget != null && Link(Get(ktarget)) > 0) return;         // it watches one named unit
                cands.Add((id, kind, Get(kp), Get(kt), Get(kg), kty == null ? "" : Get(kty), ktarget));
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0) continue;
                if (l[l.Length - 1] == '\r') l = l.Substring(0, l.Length - 1);
                if (l.StartsWith("== ", StringComparison.Ordinal))
                {
                    Close();
                    inNodes = l.StartsWith("== NOEUDS", StringComparison.Ordinal);
                    inLinks = l.StartsWith("== LIENS", StringComparison.Ordinal);
                    if (inNodes) sawNodes = true;
                    continue;
                }
                if (inNodes)
                {
                    if (l.Length > 1 && l[0] == '#')
                    {
                        Close();
                        int sp = l.IndexOf(' ');
                        if (sp <= 1 || !int.TryParse(l.Substring(1, sp - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) { id = 0; continue; }
                        string rest = l.Substring(sp + 1);
                        if (rest.StartsWith("[", StringComparison.Ordinal)) { int e = rest.IndexOf("] ", StringComparison.Ordinal); rest = e > 0 ? rest.Substring(e + 2) : rest; }
                        node = rest.Trim();
                        continue;
                    }
                    if (node == null || !l.StartsWith("    ", StringComparison.Ordinal)) continue;
                    int eq = l.IndexOf(" = ", StringComparison.Ordinal);
                    if (eq <= 4) continue;
                    string key = l.Substring(4, eq - 4).Trim().ToLowerInvariant();
                    if (props.Count < 32 && !props.ContainsKey(key)) props[key] = l.Substring(eq + 3).Trim();
                    continue;
                }
                if (!inLinks) continue;
                // "6751.Units -> 19110.forUnits [sous-graphe 6751]"
                int arrow = l.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow <= 0) continue;
                v.Links++;
                string left = l.Substring(0, arrow), right = l.Substring(arrow + 4);
                int b = right.IndexOf(" [", StringComparison.Ordinal); if (b > 0) right = right.Substring(0, b);
                int ld = left.IndexOf('.'), rd = right.IndexOf('.');
                if (ld <= 0 || rd <= 0) continue;
                if (!int.TryParse(left.Substring(0, ld), NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)) continue;
                if (!int.TryParse(right.Substring(0, rd), NumberStyles.Integer, CultureInfo.InvariantCulture, out int to)) continue;
                string fromConn = left.Substring(ld + 1), toConn = right.Substring(rd + 1);
                if (string.Equals(toConn, "forUnits", StringComparison.OrdinalIgnoreCase)) wired.Add(to);
                if (string.Equals(fromConn, "us", StringComparison.OrdinalIgnoreCase) && usTo.Count < 4000) usTo.Add((from, to));
            }
            Close();

            // gates: an open door is one with no group filter, no named unit and no wired forUnits port
            foreach (var c in cands)
            {
                if (wired.Contains(c.id)) continue;
                var g = new Gate { Id = c.id, Node = c.node, Player = Link(c.player), Team = Link(c.team), Types = c.types ?? "" };
                g.Ground = GroundMatch(g.Types);
                if (HeliMatch(g.Types)) v.HeliWatched = true;
                if (v.Gates.Count < 400) v.Gates.Add(g);
                v.OpenGates++;
            }
            // "this mission counts your units at the end": an open counter on a player, whose us output reaches a unit_accumulation
            // or an assignGroup3. That is the Blackout pattern (#7188.us -> #19188 unit_accumulation -> assignGroup3 Player_Veh).
            foreach (var c in counters)
            {
                if (!c.openGroup || c.player <= 0) continue;
                foreach (var (from, to) in usTo)
                {
                    if (from != c.id || !names.TryGetValue(to, out string n)) continue;
                    if (n == "unit_accumulation" || n == "assignGroup3") { v.CountedEvac = true; break; }
                }
                if (v.CountedEvac) break;
            }
            foreach (var g in groups) v.Groups.Add(g);
            v.Parsed = v.Nodes > 0;
            if (!v.Parsed && !sawNodes) v.Error = "le script a été écrit dans l'autre format (fichier .bascr déchiffré), que ce juge ne sait pas lire";
            return v;
        }

        // ---------------------------------------------------------------- per-battle state

        sealed class Shot
        {
            internal int Uid, UnitId, Cost, Role, Wave; internal string Name = "?";
            internal V3 Home, Target, Last;
            internal float SpawnT, SeenT = -1f, MoveT = -1f, LastMoveCheck;
            internal bool Confirmed, Moved, Paid, Fought, Retreated, Canary;
            internal int Health = -1, Ammo = -1;
        }

        static readonly List<Shot> _shots = new();
        static readonly List<Spawns.Model> _models = new();
        static readonly List<(int uid, V3 pos, int role)> _units = new();
        static readonly HashSet<int> _before = new();
        static readonly List<(float t, int n)> _mineTrail = new();
        static readonly List<(float t, int n)> _scriptTrail = new();      // script units spawned on the enemy side, sampled over time
        static readonly Dictionary<int, int> _tasks = new();
        static readonly HashSet<string> _once = new();

        static Verdict _v;
        static Task<Verdict> _parse;
        static string _reportPath;
        static bool _reportWritten, _dumpMissing, _armed, _stopped, _noticeShown, _evacNoticeShown, _guardArmed;
        static string _stopWhy;
        static float _start = -1f, _next, _nextSlow, _nextShot, _nextTry, _canaryUntil = -1f;
        static float _lastTaskT = -1f, _lastMoneyT = -1f;
        static float _money = float.NaN, _zeroSince = -1f;
        static bool _moneyOpen, _moneyClosed, _payDead, _canaryPassed;
        static bool _canarySeen, _canaryMoved;                            // the canary verdict, kept when its shot leaves the list (destroyed, forgotten)
        static int _localUid = -1, _shotsDone, _unitsAdded, _valueAdded, _pointsPaid, _payments, _taskChanges, _brakeLevel;
        static int _canaryCalls, _canaryTasks;                            // script activity counted when the canary left, for the before / after test
        static int _wave, _waveN, _announcedWave = -1; static V3 _wavePos;
        static int _wait, _lost, _noLightTries;
        static string _lastRefuse; static float _nextRefuseLog;
        static DifficultyLevel _diff = DifficultyLevel.Medium;

        // dialog hook (its own Harmony id, lazy, never removed, read only, no parameter on either target)
        static HarmonyLib.Harmony _harmony;
        static bool _dlgTried, _dlgOk;
        static long _dlgTick, _dlgEndTick;

        static void Log(string s) => Mod.Log.Msg("[RENFORTS] " + s);
        static void LogOnce(string key, string s) { if (_once.Count < 200 && _once.Add(key)) Log(s); }
        static string M(float v) => v.ToString("0", CultureInfo.InvariantCulture);

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Renforts");
            Mode = c.CreateEntry("RenfortsEnnemis", "off", description: Build.Desc(
                "Campagne solo : un deuxième adversaire. off = rien ; observation = rien n'est ajouté, le journal écrit ce qui aurait été envoyé et pourquoi ; auto = le mod ajoute quelques attaques ennemies, toujours loin de tes unités",
                "Campagne solo : attaques ennemies ajoutées (off / observation / auto)"));
            Points = c.CreateEntry("RenfortsPoints", true, description: Build.Desc(
                "Après une attaque ajoutée, tu reçois quelques points de déploiement, seulement pendant une phase où la mission t'en a déjà donné"));
            Distance = c.CreateEntry("RenfortsDistanceMin", 3500f, description: Build.Desc(
                "Distance minimum (mètres) entre l'apparition ajoutée et tes unités, à toutes les difficultés"));
            _essais = c.CreateEntry("EssaisInterrompus", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _essais.Value = 0; }
        }

        /// off = 0, observation = 1, auto = 2.
        static int ModeVal()
        {
            string v = (Mode?.Value ?? "off").Trim().ToLowerInvariant();
            return v == "auto" ? 2 : v == "observation" ? 1 : 0;
        }

        internal static void ResetSession()
        {
            if (_start >= 0f && (_shotsDone > 0 || _armed)) Guard.Run("Renforts.Bilan", () => Summary("fin de bataille"));
            ClearGuard();
            _shots.Clear(); _models.Clear(); _units.Clear(); _before.Clear(); _mineTrail.Clear(); _scriptTrail.Clear(); _tasks.Clear(); _once.Clear();
            _v = null; _parse = null; _reportPath = null;
            _reportWritten = _dumpMissing = _armed = _stopped = _noticeShown = _evacNoticeShown = false;
            _stopWhy = null;
            _start = -1f; _next = _nextSlow = _nextShot = _nextTry = 0f; _canaryUntil = -1f; _canaryPassed = _canarySeen = _canaryMoved = false;
            _lastTaskT = _lastMoneyT = -1f;
            _money = float.NaN; _zeroSince = -1f; _moneyOpen = _moneyClosed = _payDead = false;
            _localUid = -1; _shotsDone = _unitsAdded = _valueAdded = _pointsPaid = _payments = _taskChanges = _brakeLevel = 0;
            _canaryCalls = _canaryTasks = 0;
            _wave = _waveN = 0; _announcedWave = -1; _wavePos = V3.zero;
            _wait = _lost = _noLightTries = 0; _lastRefuse = null; _nextRefuseLog = 0f;
            _diff = DifficultyLevel.Medium;
            _dlgTick = _dlgEndTick = 0;
        }

        internal static void OnQuit() => ClearGuard();
        internal static void OnBattleEnd() { ClearGuard(); Guard.Run("Renforts.Bilan", () => Summary("écran de fin")); }

        /// The spawning crash guard: armed just before the first SpawnUnit call of a battle, cleared as soon as one added unit was
        /// really seen alive, and cleared again at a clean end of battle. Two games that never came back switch the spawning off.
        static void ArmGuard()
        {
            if (_guardArmed || _essais == null) return;
            _guardArmed = true;
            try { _essais.Value = _essais.Value + 1; MelonPreferences.Save(); } catch { }
        }
        static void ClearGuard()
        {
            if (!_guardArmed || _essais == null) return;
            _guardArmed = false;
            try { if (_essais.Value != 0) { _essais.Value = 0; MelonPreferences.Save(); } } catch { }
        }

        // ---------------------------------------------------------------- frame

        internal static void Frame()
        {
            if (Mode == null) return;
            float now = UnityEngine.Time.time;
            if (now < _next) return;                                                           // read BEFORE the mode: ModeVal builds a
            int mode = ModeVal();                                                              // string, and this runs on every frame
            if (mode == 0) { _next = now + 2f; return; }
            if (!Planif.Take(ref _wait, _armed ? Planif.WaitNormal : Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            _next = now + 2f;
            Tick(now, mode);
        }

        static void Tick(float now, int mode)
        {
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) return;
            Campaign.NoteSession();
            if (!Cheats.Allowed(out _)) return;                            // mod on, campaign mission, not US_M01, offline, no anti-cheat
            try { _localUid = cp.UID; } catch { return; }
            if (_localUid < 0) return;
            if (_start < 0f)
            {
                _start = now;
                try { _diff = Mod.Svc<CampaignService>()?.Difficulty ?? DifficultyLevel.Medium; } catch { }
                StartAudit();
            }
            Guard.Run("Renforts.Audit", () => AuditStep(gc, now, mode));
            if (_v == null || !_v.Resolved) return;
            _armed = true;
            // installed as soon as the mission is judged, not at the first shot: a cinematic that started before would be missed
            if (mode == 2 && _v.Allowed) Guard.Run("Renforts.CrochetDialogue", EnsureDialogHook);
            Guard.Run("Renforts.Mesures", () => Measure(gc, now));
            Guard.Run("Renforts.Suivi", () => Follow(gc, now));
            Guard.Run("Renforts.Canari", () => JudgeCanary(now));
            if (now >= _nextSlow) { _nextSlow = now + 300f; Guard.Run("Renforts.Releve", () => Summary("5 min")); }
            if (_stopped || !_v.Allowed) return;
            Guard.Run("Renforts.Coup", () => Consider(gc, now, mode));
        }

        // ---------------------------------------------------------------- audit

        static void StartAudit()
        {
            string mission = Campaign.MissionUid;
            if (string.IsNullOrWhiteSpace(mission)) { _dumpMissing = true; return; }
            string safe = Safe(mission);
            string dir;
            try { dir = Path.Combine(MelonEnvironment.UserDataDirectory, "RealismOverhaul_missions"); }
            catch (Exception e) { _dumpMissing = true; Log("dossier des missions illisible : " + e.Message); return; }
            string path = Path.Combine(dir, safe + ".txt");
            _reportPath = Path.Combine(dir, safe + "_renforts.txt");
            bool exists = false;
            try { exists = File.Exists(path); } catch { }
            if (!exists)
            {
                // first play of this mission: the script is being written right now, so nothing is added this battle (by design)
                _dumpMissing = true;
                Log($"mission {mission} : le script n'a pas encore été écrit par le mod (première partie) : aucun renfort ajouté cette bataille, la mission sera jugée à la prochaine");
                return;
            }
            Log($"mission {mission} : lecture du script écrit par le mod ({path})");
            _parse = Task.Run(() => { try { return Parse(path, mission); } catch (Exception e) { return new Verdict { Mission = mission, Error = e.Message }; } });
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder(s.Length);
            var bad = Path.GetInvalidFileNameChars();
            foreach (char ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
            return sb.ToString();
        }

        static void AuditStep(GameController gc, float now, int mode)
        {
            if (_v != null && _v.Resolved) return;
            if (_dumpMissing)
            {
                if (_v != null) return;
                _v = new Verdict { Mission = Campaign.MissionUid ?? "?", Why = "première partie de cette mission : le script n'a pas encore été lu par le mod", PayWhy = "script pas encore lu", Resolved = true };
                NoticeOnce(mode, TxtKey.RF_MEASURE);
                Log($"mission {_v.Mission} : {_v.Why} -> renforts INTERDITS cette bataille");
                return;
            }
            if (_v == null)
            {
                if (_parse == null || !_parse.IsCompleted) return;
                _v = _parse.Result ?? new Verdict();
                _parse = null;
            }
            Resolve(gc);                                   // judged again at the next tick while the camps are not known yet
            if (!_v.Resolved) return;
            WriteReport();
            Log($"audit {_v.Mission} : {_v.Nodes} nœuds, {_v.Links} liens, portes ouvertes {_v.OpenGates} (retenues {_v.Gates.Count}), groupes cités {_v.Groups.Count} -> {(_v.Allowed ? "renforts POSSIBLES" : "renforts INTERDITS")} : {_v.Why}");
            Log($"audit {_v.Mission} argent : {(_v.PayAllowed ? $"points possibles, plafond {_v.PayCap} pour toute la mission{(_v.PayOnce ? ", un seul versement (cette mission compte tes unités à la fin)" : "")}" : "aucun point : " + _v.PayWhy)}");
            if (mode == 2 && !_v.Allowed) NoticeOnce(mode, TxtKey.RF_NOT_HERE);
            else if (mode == 1) NoticeOnce(mode, TxtKey.RF_MEASURE);
        }

        static void Resolve(GameController gc)
        {
            var v = _v;
            v.Resolved = true;
            v.Allowed = v.PayAllowed = v.PayOnce = false; v.PayCap = 0;
            if (!v.Parsed) { v.Why = v.Error != null ? "script illisible : " + v.Error : "script vide ou tronqué"; v.PayWhy = v.Why; return; }
            int enemy = Spawns.EnemySide;
            if (enemy < 0 || Spawns.PlayerSide < 0) { v.Resolved = false; return; }   // camps not known yet: judged again at the next tick
            if (!v.NoCreated) { v.Why = "la mission réagit à la création des unités (onUnitCreated2)"; v.PayWhy = v.Why; return; }
            int unresolved = 0, watching = 0, open = 0;
            Gate worst = null;
            foreach (var g in v.Gates)
            {
                if (!g.Ground) continue;                                         // its type filter cannot match a ground vehicle
                int ps = g.Player > 0 ? PlayerSideOf(g.Player) : -2;
                int ts = g.Team > 0 ? TeamSideOf(gc, g.Team) : -2;
                if (ps == -2 && ts == -2) { open++; worst ??= g; continue; }      // no player and no team filter: it would count anything
                if (ps == -1 || ts == -1) { unresolved++; worst ??= g; continue; }
                if (ps == enemy || ts == enemy) { watching++; worst ??= g; }
            }
            if (open + unresolved + watching > 0)
            {
                v.Why = $"{open + unresolved + watching} porte(s) du script pourraient compter une unité ajoutée (sans filtre {open}, camp ennemi {watching}, filtre illisible {unresolved})"
                      + (worst != null ? $", par exemple le nœud #{worst.Id} {worst.Node}" : "");
                v.PayWhy = "renforts interdits dans cette mission";
                return;
            }
            v.Allowed = true;
            v.Why = $"aucune porte du script ne peut compter une unité ennemie ajoutée ({v.Gates.Count} porte(s) ouverte(s) examinée(s), toutes sur ton camp ou sur un type que nos véhicules ne peuvent pas être)";
            // points
            if (!Points.Value) { v.PayWhy = "les points sont coupés dans les réglages"; return; }
            if (!v.NoGetMoney) { v.PayWhy = "le script de cette mission LIT l'argent (getMoney2 / getUpkeepedValue) : on ne peut pas prouver qu'on ne casse rien"; return; }
            int best = 0;
            foreach (var (p, amount) in v.Money) if (p == _localUid && amount > best) best = amount;
            if (best <= 0) { v.PayWhy = "le script de cette mission ne te donne jamais d'argent : porte fermée"; return; }
            v.PayCap = Math.Min(MaxPointsPerMission, (int)Math.Round(best * 0.10f));
            v.PayOnce = v.CountedEvac;
            if (v.PayOnce) v.PayCap = Math.Min(v.PayCap, 45);
            if (v.PayCap <= 0) { v.PayWhy = "le plafond calculé sur l'argent du script est nul"; return; }
            v.PayAllowed = true;
            v.PayWhy = null;
        }

        static int PlayerSideOf(int uid)
        {
            try { var ctx = Campaign.Ctx(); if (ctx != null && ctx.TryGetPlayer(uid, out var p) && p != null) return (int)p.TeamSide; }
            catch { }
            return -1;
        }
        /// Camp of a TEAM the script names, from the battle's own team table. Never through EnemyAi.SideOf: that one answers a zone
        /// owner, where 0 and 1 already ARE the two camps, and would read the link id "c1" of a script field as "camp 1".
        /// An id the table does not hold stays unresolved (-1), which makes Resolve count the gate as risky and refuse the mission.
        static int TeamSideOf(GameController gc, int uid)
        {
            try
            {
                var teams = gc._GameSession_k__BackingField.GetTeams();
                if (teams != null && teams.TryGetValue(uid, out var td) && td != null)
                {
                    int s = (int)td.TeamSide;
                    if (s == 0 || s == 1) return s;
                }
            }
            catch { }
            return -1;
        }

        static void WriteReport()
        {
            if (_reportWritten || _reportPath == null || _v == null) return;
            _reportWritten = true;
            var v = _v;
            var sb = new StringBuilder(2048);
            sb.Append("# Broken Arrow Realism Overhaul ").Append(GuardVersion).Append(" - renforts ajoutés : jugement de la mission ").Append(v.Mission).Append(" (lecture seule)\n");
            sb.Append("date=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("nœuds=").Append(v.Nodes).Append(" liens=").Append(v.Links).Append(" portes ouvertes=").Append(v.OpenGates).Append(" groupes cités=").Append(v.Groups.Count).Append('\n');
            sb.Append("renforts=").Append(v.Allowed ? "POSSIBLES" : "INTERDITS").Append(" : ").Append(v.Why ?? "").Append('\n');
            sb.Append("points=").Append(v.PayAllowed ? "possibles, plafond " + v.PayCap.ToString(CultureInfo.InvariantCulture) + (v.PayOnce ? " (un seul versement)" : "") : "non : " + (v.PayWhy ?? "")).Append('\n');
            sb.Append("hélicoptères=INTERDITS (cette version n'envoie que des véhicules terrestres)").Append(v.HeliWatched ? " ; de plus la mission surveille les hélicoptères ennemis sur toute la carte" : "").Append('\n');
            sb.Append("avions=INTERDITS (non prouvé : il faudrait une apparition aérienne ennemie prouvée dans la bataille et un point d'apparition ennemi acceptant les avions)\n");
            sb.Append("remboursement par le mod=jamais (aucune unité n'est retirée de la bataille ; la mission ").Append(v.NoRefundWatch ? "ne surveille pas" : "surveille").Append(" les remboursements)").Append('\n');
            sb.Append("la mission compte tes unités à la fin=").Append(v.CountedEvac ? "OUI" : "non").Append('\n');
            sb.Append("\n== PORTES OUVERTES EXAMINEES\n");
            foreach (var g in v.Gates)
                sb.Append('#').Append(g.Id).Append(' ').Append(g.Node).Append(" joueur=c").Append(g.Player).Append(" équipe=c").Append(g.Team)
                  .Append(" types='").Append(g.Types).Append("' peut viser un véhicule terrestre=").Append(g.Ground ? "oui" : "non").Append('\n');
            sb.Append("\n== ARGENT FIXE PAR LE SCRIPT\n");
            foreach (var (p, amount) in v.Money) sb.Append("joueur c").Append(p).Append(" = ").Append(amount).Append(p == _localUid ? "   <- toi" : "").Append('\n');
            sb.Append("\n== GROUPES CITES (coupe-circuit : une unité ajoutée n'en porte aucun)\n");
            foreach (var g in v.Groups) sb.Append(g).Append('\n');
            string text = sb.ToString(), path = _reportPath;
            Task.Run(() => { try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { } });
        }

        // ---------------------------------------------------------------- measurements (money window, tasks, brake)

        static void Measure(GameController gc, float now)
        {
            // what the script itself sent over the last 3 minutes: the module keeps quiet while the mission is already busy
            _scriptTrail.Add((now, Spawns.ScriptEnemyUnits));
            for (int i = _scriptTrail.Count - 1; i >= 0; i--) if (now - _scriptTrail[i].t > BurstWindow + 10f) _scriptTrail.RemoveAt(i);

            // his live unit count, kept over 5 minutes: the automatic brake
            int mine = Spawns.MineCount;
            _mineTrail.Add((now, mine));
            for (int i = _mineTrail.Count - 1; i >= 0; i--) if (now - _mineTrail[i].t > 300f) _mineTrail.RemoveAt(i);
            int peak = 0;
            foreach (var (_, cnt) in _mineTrail) if (cnt > peak) peak = cnt;   // 'cnt': 'n' is already the task count later in this method
            int level = 0;
            if (peak >= 6)
            {
                float lost = 1f - mine / (float)peak;
                if (lost >= BrakeStop) level = 2; else if (lost >= BrakeSkip) level = 1;
            }
            if (level != _brakeLevel)
            {
                _brakeLevel = level;
                Log($"frein automatique : tu as {mine} unité(s) pour un maximum de {peak} sur 5 min -> {(level == 2 ? "renforts suspendus tant que tes pertes restent au-dessus de 40 % sur 5 min" : level == 1 ? "coup sauté, budget restant divisé par deux" : "aucun frein")}");
            }

            // the mission's own economy window: it opens when the script has given him money and closes when it takes it all back
            var f = gc._GetEcsEventBus_k__BackingField?.Gameplay?.GetMoney;
            if (f != null)
            {
                float cur = float.NaN;
                try { cur = f.Invoke(_localUid); } catch { }
                if (!float.IsNaN(cur))
                {
                    // Only a RISE is a script money move (setMoney2, a deck change). A fall is the player buying something, and the
                    // normal rhythm of a campaign push would otherwise have kept the module quiet from one purchase to the next.
                    if (!float.IsNaN(_money) && cur - _money >= 150f) _lastMoneyT = now;
                    _money = cur;
                    if (cur > 0f)
                    {
                        _zeroSince = -1f;
                        if (_moneyClosed)                                          // he has points again: the window re-opens
                        {
                            _moneyClosed = false; _moneyOpen = true;
                            Log($"la mission t'a redonné des points ({M(cur)} à t={M(now - _start)}s) : les renforts ajoutés redeviennent possibles");
                        }
                        else if (!_moneyOpen) { _moneyOpen = true; Log($"la mission t'a ouvert un budget ({M(cur)} points à t={M(now - _start)}s) : les renforts ajoutés deviennent possibles"); }
                    }
                    else if (_moneyOpen)
                    {
                        if (_zeroSince < 0f) _zeroSince = now;
                        else if (now - _zeroSince >= 60f && !_moneyClosed)
                        {
                            // a PAUSE, never a latch: a player who spent everything on his first assault must not lose the whole
                            // feature for the rest of the mission
                            _moneyClosed = true; _moneyOpen = false;
                            Log($"tu n'as plus aucun point depuis 60 s à t={M(now - _start)}s : renforts ajoutés et points suspendus tant que tu es à zéro");
                        }
                    }
                }
            }

            // the mission's own tasks: a step that changes is a sign the script is alive, and a quiet window for us
            Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.MissionEditor.Data.Meta.TaskData> list = null;
            try { list = gc._taskPanel?._taskDataList; } catch { }
            if (list == null) return;
            int n = Math.Min(list.Count, 200);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var t = list[i]; if (t == null) continue;
                    int uid = t.UID, st = (int)t.Status;
                    if (_tasks.TryGetValue(uid, out int old)) { if (old != st) { _lastTaskT = now; _taskChanges++; } }
                    else if (_tasks.Count >= 200) continue;
                    _tasks[uid] = st;
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- the added units: confirm, drive, pay, retreat

        static void Follow(GameController gc, float now)
        {
            if (_shots.Count == 0) return;
            Spawns.RenfortEnemyUnits(_units);
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                if (!s.Confirmed)
                {
                    if (!Confirm(s, now) && now - s.SpawnT > 30f)
                    {
                        _shots.RemoveAt(i);
                        if (s.Canary) Stop("le canari n'est jamais apparu près du tag demandé en 30 s : le mod ne sait pas créer d'unité dans cette version du jeu");
                        else if (++_lost >= 2) Stop($"{_lost} unités ajoutées ne sont jamais apparues : plus rien n'est ajouté de cette bataille");
                        else Mod.Log.Warning("[RENFORTS] une unité ajoutée n'est pas apparue en 30 s (peut-être pas de place au tag) : elle est oubliée, le module continue");
                    }
                    continue;
                }
                V3 p = V3.zero; bool alive = false;
                foreach (var (uid, pos, _) in _units) if (uid == s.Uid) { p = pos; alive = true; break; }
                if (!alive)
                {
                    if (s.Canary) { _canarySeen = s.Confirmed; _canaryMoved = s.Moved; }   // its verdict is kept, never guessed later
                    if (s.Canary && !s.Moved && now - s.SeenT < 120f) Stop("le canari a disparu avant d'avoir bougé : rien n'est ajouté de cette bataille");
                    else Log($"renfort uid {s.Uid} ({s.Name}) détruit ou parti à t={M(now - _start)}s");
                    EnemyAi.ForgetRenfort(s.Uid);
                    _shots.RemoveAt(i);
                    continue;
                }
                if (!s.Moved && now - s.LastMoveCheck >= 10f)
                {
                    s.LastMoveCheck = now;
                    if (EnemyAi.D2(p, s.Home) >= 50f)
                    {
                        s.Moved = true; s.MoveT = now;
                        Log($"renfort uid {s.Uid} ({s.Name}) a bougé de {M(EnemyAi.D2(p, s.Home))} m : il obéit aux ordres");
                    }
                    else if (now - s.SeenT > 120f && s.Canary)
                    {
                        Stop($"le canari (uid {s.Uid}) n'a pas bougé de son point d'arrivée en 2 minutes : les unités ajoutées ne sont pas commandables");
                    }
                }
                s.Last = p;
                // it fought: health or ammunition went down since it arrived
                if (!s.Fought)
                {
                    var u = Spawns.RenfortEnemyLua(s.Uid);
                    if (u != null)
                    {
                        int h = -1, a = -1;
                        try { h = u.GetHealPercentage(); a = u.GetAmmoPercentage(true, false); } catch { }
                        if (s.Health < 0) { s.Health = h; s.Ammo = a; }
                        else if ((h >= 0 && h < s.Health - 2) || (a >= 0 && a < s.Ammo - 5)) { s.Fought = true; Log($"renfort uid {s.Uid} ({s.Name}) est au contact à t={M(now - _start)}s"); }
                    }
                }
                if (!s.Paid) Pay(gc, s, now);
                if (!s.Retreated && !s.Fought && now - s.SeenT > RetreatAfter) Retreat(s, now);
            }
        }

        /// The added unit is a new alive enemy unit that was not there before the call, that stands near the place we asked for, that
        /// is of the very model we asked for, and that the mission script does not drive. The last two tests matter: the script may
        /// land one of its own waves at the same tag in the same seconds, and a unit of the script must never be taken for ours.
        static bool Confirm(Shot s, float now)
        {
            foreach (var (uid, pos, role) in _units)
            {
                if (_before.Contains(uid) || EnemyAi.D2(pos, s.Home) > 150f) continue;
                var u = Spawns.RenfortEnemyLua(uid);
                if (u == null) continue;
                int unitId = 0;
                try { unitId = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
                if (unitId != s.UnitId) continue;                       // another model: not the one we asked for
                if (Missions.ScriptReason(uid, now) != null)
                {
                    LogOnce("script-at-tag:" + uid, $"une unité du script est arrivée au même tag (uid {uid}) : elle n'est jamais prise pour un renfort du mod, ni commandée");
                    continue;
                }
                s.Uid = uid; s.Confirmed = true; s.SeenT = now; s.Last = pos; s.LastMoveCheck = now; s.Role = role;
                try { s.Name = u.Name ?? s.Name; } catch { }
                Log($"renfort uid {uid} ({s.Name}) confirmé vivant à {M(EnemyAi.D2(pos, s.Home))} m du tag demandé, {M(Spawns.RenfortPlayerDistance(pos))} m de tes unités, à t={M(now - _start)}s");
                ClearGuard();                                           // the game came back from a spawn: the crash guard is clean again
                _before.Add(uid);                                       // judged, whatever the verdict: never judged twice
                if (!GroupCheck(u, uid)) return true;                   // GroupCheck already stopped the module
                EnemyAi.NoteRenfort(uid, s.Home, now + 120f);
                Order(s, now);
                Announce(s.Wave);
                return true;
            }
            return false;
        }

        /// Kill switch: an added unit must carry NO group name of the mission script. One match and the module stops for the battle.
        /// The unit is LEFT WHERE IT IS: carrying a group of the script is far more likely to mean the module took one of the
        /// mission's own units for its own than that it really created that unit, and removing a unit the mission is waiting for
        /// would block the battle for good. Stopping costs nothing; a wrong refund cannot be undone.
        static bool GroupCheck(LuaUnit u, int uid)
        {
            if (u == null || _v == null || _v.Groups.Count == 0) return true;
            Il2CppBrokenArrow.Client.Ecs.Utils.EntitiesHelper helper = null;
            try { helper = GameController._instance?.GetEntitiesHelper; } catch { }
            if (helper == null) { LogOnce("helper", "appartenance aux groupes illisible : le coupe-circuit des groupes ne peut pas être vérifié"); return true; }
            int n = Math.Min(_v.Groups.Count, 400);
            for (int i = 0; i < n; i++)
            {
                string g = _v.Groups[i];
                try { var e = u.Entity; if (!helper.UnitHasGroup(ref e, g)) continue; }
                catch { continue; }
                Stop($"l'unité ajoutée uid {uid} porte le groupe '{g}' du script : coupe-circuit, plus rien n'est ajouté de cette bataille ; l'unité est laissée exactement où elle est (c'est probablement une unité de la mission, elle ne doit surtout pas disparaître)");
                EnemyAi.ForgetRenfort(uid);                              // never driven by the mod again either
                return false;
            }
            return true;
        }

        /// The approach order. The target is one of the mission's OWN objective zones: the module never reads where his units are
        /// to choose it. Difficulty only changes the manner - a careful approach, a determined one, or a flank along the play zone edge.
        static void Order(Shot s, float now)
        {
            var u = Spawns.RenfortEnemyLua(s.Uid);
            if (u == null) return;
            V3 target = s.Target;
            float dist = EnemyAi.D2(s.Home, target);
            if (dist < 10f) return;
            V3 dir = target - s.Home; dir.y = 0f; dir /= Math.Max(1f, dir.magnitude);
            V3 perp = new V3(-dir.z, 0f, dir.x);
            float off = _diff == DifficultyLevel.Hard ? Math.Min(900f, dist * 0.35f) : _diff == DifficultyLevel.Easy ? 0f : 250f;
            V3 mid = s.Home + dir * (dist * 0.6f);
            if (off > 0f && Spawns.RenfortBounds(out var b))
            {
                // toward the nearer edge of the play zone, then clamped inside it: a flank that stays where the mission is played
                V3 a = mid + perp * off, c = mid - perp * off;
                float da = Math.Min(Math.Min(a.x - b.min.x, b.max.x - a.x), Math.Min(a.z - b.min.z, b.max.z - a.z));
                float dc = Math.Min(Math.Min(c.x - b.min.x, b.max.x - c.x), Math.Min(c.z - b.min.z, b.max.z - c.z));
                mid = da < dc ? a : c;
                mid.x = UnityEngine.Mathf.Clamp(mid.x, b.min.x + 300f, b.max.x - 300f);
                mid.z = UnityEngine.Mathf.Clamp(mid.z, b.min.z + 300f, b.max.z - 300f);
            }
            else if (off > 0f) mid += perp * off;
            Missions.OwnOrderBegin();
            try
            {
                // every difficulty ends on the objective zone, or the announced attack would never arrive: Easy only goes there
                // slowly and straight, Medium plainly, Hard along the edge of the play zone
                if (_diff == DifficultyLevel.Easy) { u.MoveTo(mid, 50, false); u.MoveTo(target, 60, true); }
                else if (_diff == DifficultyLevel.Medium) { u.MoveTo(mid, 60, false); u.MoveTo(target, 90, true); }
                else { u.MoveToFast(mid, 60, false); u.MoveTo(target, 90, true); }
            }
            catch (Exception e) { Log($"ordre d'approche refusé pour uid {s.Uid} : {e.Message}"); }
            finally { Missions.OwnOrderEnd(); }
            Log($"renfort uid {s.Uid} : approche {(_diff == DifficultyLevel.Hard ? "par le flanc" : _diff == DifficultyLevel.Easy ? "prudente" : "décidée")} vers l'objectif ({M(target.x)},{M(target.z)}) à {M(dist)} m, point de passage ({M(mid.x)},{M(mid.z)})");
        }

        static void Retreat(Shot s, float now)
        {
            s.Retreated = true;
            var u = Spawns.RenfortEnemyLua(s.Uid);
            if (u == null) return;
            Missions.OwnOrderBegin();
            try { u.MoveTo(s.Home, 40, false); }
            catch (Exception e) { Log($"repli refusé pour uid {s.Uid} : {e.Message}"); }
            finally { Missions.OwnOrderEnd(); }
            Log($"renfort uid {s.Uid} ({s.Name}) n'a jamais été au contact en {M(RetreatAfter)} s : repli vers son point d'arrivée");
        }

        // The module has no refund path at all any more: nothing it adds is ever removed from the battle. Leaving a wrongly
        // identified unit alive costs a stopped module; removing one of the mission's own units would cost the battle.

        // ---------------------------------------------------------------- the few points given back

        static void Pay(GameController gc, Shot s, float now)
        {
            var v = _v;
            // never after a stop: if the module had to switch itself off, the last thing to do is touch his money.
            // Never before the canary is fully judged either: the mission's economy is touched only once the module has proved
            // that what it adds really is its own and that the script kept running.
            if (v == null || _stopped || !_canaryPassed || !v.PayAllowed || _payDead || !_moneyOpen || _moneyClosed) return;
            if (!s.Confirmed || now - s.SeenT < PayAfter) return;
            if (v.PayOnce && _payments >= 1) { s.Paid = true; return; }
            if (_pointsPaid >= v.PayCap) { s.Paid = true; return; }
            // never next to a script money move: a payment inside a setMoney2 / changePlayerDeck window would be wiped or would
            // arrive while the script is changing his deck. A mission that moves units between decks gets a wider window.
            float quiet = v.CountedEvac ? 120f : QuietAfterMoney;
            if (_lastMoneyT >= 0f && now - _lastMoneyT < quiet) return;
            int amount = (int)(Math.Round(s.Cost * 0.25f / 5.0) * 5);
            amount = Math.Max(10, Math.Min(50, amount));
            amount = Math.Min(amount, v.PayCap - _pointsPaid);
            if (amount <= 0) { s.Paid = true; return; }
            var add = gc._GetEcsEventBus_k__BackingField?.Gameplay?.AddMoney;
            var get = gc._GetEcsEventBus_k__BackingField?.Gameplay?.GetMoney;
            if (add == null) { _payDead = true; Log("points impossibles : l'entrée d'économie du jeu est absente"); return; }
            float before = float.NaN, after = float.NaN;
            try { if (get != null) before = get.Invoke(_localUid); } catch { }
            try { add.Invoke(_localUid, (float)amount); } catch (Exception e) { _payDead = true; Log("points refusés : " + e.Message); return; }
            try { if (get != null) after = get.Invoke(_localUid); } catch { }
            s.Paid = true;
            if (!float.IsNaN(before) && !float.IsNaN(after) && after - before < 0.5f)
            {
                _payDead = true;
                Log($"points versés sans effet (avant {M(before)}, après {M(after)}) : plafond de la mission atteint, plus aucun versement de cette bataille");
                return;
            }
            _pointsPaid += amount; _payments++;
            _money = float.IsNaN(after) ? _money : after;
            _lastMoneyT = now;                                            // our own move must not be read back as a script money jump
            Log($"+{amount} points pour toi après le renfort uid {s.Uid} (total versé {_pointsPaid}/{v.PayCap} pour la mission ; argent avant {M(before)} après {M(after)})");
            Mod.Notify(new TxtMsg(TxtKey.RF_POINTS, amount));
            if (v.CountedEvac && !_evacNoticeShown) { _evacNoticeShown = true; Mod.Notify(TxtKey.RF_EVAC_WARNING); }
        }

        // ---------------------------------------------------------------- deciding a shot

        static void Consider(GameController gc, float now, int mode)
        {
            string why = Refuse(now, mode);
            if (why != null) { SayRefusal(why, now); return; }
            if (now < _nextTry) return;                                          // a search that found nothing is not worth redoing every 2 s

            bool canary = _shotsDone == 0;
            Spawns.RenfortModels(_models);
            if (_models.Count == 0) { _nextTry = now + RetryAfter; LogOnce("no-model", "pas de renfort : la mission n'a encore fait apparaître aucun véhicule ennemi que le mod puisse copier"); return; }
            Spawns.Model model = null;
            // the canary is one light vehicle: the cheapest the mission fields. After LightTries refusals in a row, a mission whose
            // script only lands IFVs and MBTs would never get its canary and the whole function would stay dead, so the cheapest
            // vehicle of the battle is accepted whatever its price (one unit, farthest valid tag, every other door unchanged).
            bool cheapOnly = canary && _noLightTries < LightTries;
            for (int i = 0; i < _models.Count; i++)
            {
                var m = _models[i];
                if (cheapOnly && m.Cost > Spawns.CanaryMaxCost) continue;
                if (!EnemyAi.IsBot(m.Owner) || m.Owner <= 0) continue;
                model = m; break;                                               // cheapest first (RenfortModels sorts by cost)
            }
            if (model == null)
            {
                _nextTry = now + RetryAfter;
                if (cheapOnly) _noLightTries++;
                LogOnce("no-light" + (cheapOnly ? "" : "-any"), canary ? "pas de canari : aucun véhicule léger ennemi copiable pour l'instant" : "pas de renfort : aucun modèle ennemi copiable pour l'instant");
                return;
            }
            if (canary && model.Cost > Spawns.CanaryMaxCost)
                LogOnce("canary-heavy", $"canari : aucun véhicule ennemi à {Spawns.CanaryMaxCost} points ou moins dans cette mission, le moins cher qu'elle emploie est pris ({model.Name}, {model.Cost} points)");

            int want = canary ? 1 : _brakeLevel == 1 ? 1 : _diff == DifficultyLevel.Easy ? 2 : 3;
            want = Math.Min(want, Budget(model.Cost));
            if (want <= 0) { _nextTry = now + RetryAfter; LogOnce("budget", $"pas de renfort : plafond atteint ({_unitsAdded} unité(s) et {_valueAdded} points ajoutés pour {Spawns.ScriptEnemyUnits} unités et {Spawns.ScriptEnemyValue} points du script)"); return; }

            if (!Spawns.RenfortPick(model.Cls, Math.Max(2500f, Distance.Value), ZoneMin, SpawnPointMin, out int tag, out V3 pos, out float playerD, out string pickWhy))
            { _nextTry = now + RetryAfter; LogOnce("no-place", "pas de renfort : aucun endroit sûr (" + pickWhy + ")"); return; }

            if (!Spawns.RenfortZoneTarget(pos, TargetMinAway, out V3 target, out int zoneId))
            { _nextTry = now + RetryAfter; LogOnce("no-zone", "pas de renfort : aucune zone objectif de la mission à viser depuis cet endroit (le mod ne vise jamais tes unités)"); return; }

            if (mode == 1)
            {
                // nothing is created: the module only writes what it would have done, then goes on as if that shot had worked.
                // The canary is NOT declared passed here: nothing was created, so nothing was proven - a battle switched from
                // observation to auto must still start with a real canary.
                _nextShot = now + BetweenShots;
                _shotsDone++; _unitsAdded += want; _valueAdded += want * model.Cost;
                Log($"OBSERVATION : aurait envoyé {want} x {model.Name} ({model.Cost} pts pièce) du tag {tag} ({M(pos.x)},{M(pos.z)}), à {M(playerD)} m de tes unités, vers la zone objectif {zoneId} ({M(target.x)},{M(target.z)}) ; difficulté {_diff}, canari={canary} ; rien n'a été créé");
                return;
            }

            Send(model, tag, pos, target, zoneId, want, canary, playerD, now);
        }

        /// How many units are still allowed by the two shares of what the script itself sent.
        static int Budget(int cost)
        {
            int unitsCap = (int)(Spawns.ScriptEnemyUnits * ShareUnits);
            int valueCap = (int)(Spawns.ScriptEnemyValue * ShareValue);
            if (_brakeLevel == 1) { unitsCap /= 2; valueCap /= 2; }
            int byUnits = unitsCap - _unitsAdded;
            int byValue = cost > 0 ? (valueCap - _valueAdded) / cost : 0;
            return Math.Max(0, Math.Min(byUnits, byValue));
        }

        static void Send(Spawns.Model model, int tag, V3 pos, V3 target, int zoneId, int want, bool canary, float playerD, float now)
        {
            _before.Clear();
            Spawns.RenfortEnemyUnits(_units);
            foreach (var (uid, _, _) in _units) _before.Add(uid);
            Spawns.RenfortUseTag(tag);                                   // the tag is busy now: no second wave there, and no script swap either
            _wave++;
            ArmGuard();
            int made = 0;
            for (int i = 0; i < want; i++)
            {
                SpawnNodeData d = null;
                try
                {
                    d = new SpawnNodeData
                    {
                        UnitInfo = model.Info,               // the mission's own unit, never one chosen from the database
                        TargetTagUID = tag,
                        TargetPos = V3.zero,                 // tag mode: the game places the unit itself, exactly as the script's own calls do
                        SpawnPointUID = 0,
                        OwnerPlayerUID = model.Owner,        // an enemy AI player really seen spawning units in this battle
                        UnitGroup = "",                      // never a group name: that is what keeps the unit invisible to the script
                        CargoUnitGroup = "",
                        Quantity = 1,
                        Assign = false,                      // never a deck: it would touch the economy and the upkeep
                        RotationY = 0f
                    };
                    // Spawned is left null on purpose. The delegate of a script node points at NodeSpawnUnit.OnSpawned, whose spawn
                    // counter the mission waits for: giving it one more spawn would freeze the script.
                }
                catch (Exception e) { Stop("impossible de préparer une apparition : " + e.Message); return; }
                // the very entry point the mission script's own spawn nodes end up in; the re-entrance counter keeps our own prefix
                // (Spawns.OnSingle) from judging this call as if the script had made it
                Spawns.OwnSpawnBegin();
                try { SpawnService.SpawnUnit(d); made++; }
                catch (Exception e) { Stop("apparition refusée par le jeu : " + e.Message); return; }
                finally { Spawns.OwnSpawnEnd(); }
                _shots.Add(new Shot { Home = pos, Target = target, SpawnT = now, Cost = model.Cost, UnitId = model.UnitId, Name = model.Name, Canary = canary, Wave = _wave });
                if (canary) break;
            }
            if (made == 0) return;
            _shotsDone++; _unitsAdded += made; _valueAdded += made * model.Cost;
            _nextShot = now + BetweenShots;
            _waveN = made; _wavePos = pos;
            if (canary) { _canaryUntil = now + CanaryHold; _canaryCalls = Spawns.ScriptEnemyCalls; _canaryTasks = _taskChanges; }
            Log($"{(canary ? "CANARI" : "renfort")} envoyé : {made} x {model.Name} ({model.Cost} pts pièce, propriétaire {model.Owner}) au tag {tag} ({M(pos.x)},{M(pos.z)}), à {M(playerD)} m de tes unités, vers la zone objectif {zoneId} ; coup {_shotsDone}, total ajouté {_unitsAdded} unité(s) / {_valueAdded} points pour {Spawns.ScriptEnemyUnits} / {Spawns.ScriptEnemyValue} du script ; difficulté {_diff}");
        }

        /// Never a silent attack: the player is told the moment the first vehicle of the wave is really there, with the compass point
        /// it comes from and how far it is from his own units. The points, when they come, are a second notice of their own: nothing
        /// is promised on screen before it has really been given.
        static void Announce(int wave)
        {
            if (_announcedWave == wave) return;
            _announcedWave = wave;
            float km = Spawns.RenfortPlayerDistance(_wavePos) / 1000f;
            Mod.Notify(new TxtMsg(TxtKey.RF_ATTACK, _waveN, Compass(_wavePos), km.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        /// Compass point of a place on the map, taken from the middle of the play zone (mission data only, never his units).
        /// +z is read as north, which is the usual Unity convention; a wrong word here changes nothing but the wording.
        static TxtKey Compass(V3 pos)
        {
            V3 c = V3.zero;
            if (Spawns.RenfortBounds(out var b)) c = b.center;
            float a = (float)(Math.Atan2(pos.x - c.x, pos.z - c.z) * 180.0 / Math.PI);
            if (a < 0f) a += 360f;
            switch ((int)Math.Round(a / 45f) % 8)
            {
                case 1: return TxtKey.RF_DIR_NE;
                case 2: return TxtKey.RF_DIR_E;
                case 3: return TxtKey.RF_DIR_SE;
                case 4: return TxtKey.RF_DIR_S;
                case 5: return TxtKey.RF_DIR_SW;
                case 6: return TxtKey.RF_DIR_W;
                case 7: return TxtKey.RF_DIR_NW;
                default: return TxtKey.RF_DIR_N;
            }
        }

        // ---------------------------------------------------------------- the doors, all closed by default

        /// Why no shot right now, or null. Every door of the design, in the order that costs the least to check.
        static string Refuse(float now, int mode)
        {
            if (_stopped) return "arrêté pour cette bataille : " + _stopWhy;
            var v = _v;
            if (v == null || !v.Resolved) return "mission pas encore jugée";
            if (!v.Allowed) return v.Why;
            if (_essais != null && _essais.Value >= 2) return "deux parties de suite ne sont pas revenues d'une apparition ajoutée : apparitions coupées par sécurité";
            // the module lives on what Spawns measures: the proven enemy tags, the player bubble and the script's own spawn counters
            if (!Spawns.RenfortsReady) return "la surveillance des apparitions n'est pas prête (réglage SpawnsSurs coupé, camps inconnus ou tes unités illisibles)";
            // without the landing hook nothing tells Missions which units the script itself landed, so a unit of the script arriving
            // at our tag could be taken for ours: the only protection against adopting a mission unit would be blind
            if (!Spawns.LandingHookOk) return "le crochet des atterrissages du script n'est pas en place : une unité de la mission pourrait être prise pour un renfort du mod";
            string notReady = Missions.ProtectionNotReady();
            if (notReady != null) return "unités du script pas encore sûres (" + notReady + ")";
            if (!EnemyAi.CanCommand) return "les ordres de l'IA ennemie ne sont pas actifs sur ce PC";
            if (!(EnemyAi.Enabled?.Value ?? false)) return "l'IA ennemie du mod est coupée : une unité ajoutée n'aurait personne pour la commander";
            if (now - _start < FirstShotAt) return $"début de bataille ({M(FirstShotAt - (now - _start))} s à attendre)";
            if (!_moneyOpen) return "la mission ne t'a pas encore donné de points : le mod se tait tant que sa phase économique n'est pas ouverte";
            if (_moneyClosed) return "tu n'as plus un seul point : rien n'est ajouté tant que la mission ne t'en redonne pas";
            if (_brakeLevel >= 2) return "frein automatique : tu as perdu trop d'unités";
            if (Spawns.ScriptEnemyCalls < 3) return "le script n'a pas encore envoyé trois arrivées ennemies";
            if (now < _nextShot) return $"moins de {M(BetweenShots)} s depuis le dernier coup ({M(_nextShot - now)} s à attendre)";
            if (mode == 2 && _shotsDone > 0 && !_canaryPassed) return "le premier renfort de la bataille (le canari) n'est pas encore entièrement jugé";
            float ls = Spawns.LastScriptSpawnT;
            if (ls >= 0f && now - ls < QuietAfterScriptSpawn) return "le script vient de faire apparaître quelque chose";
            if (BurstCount(now) >= ScriptSpawnBurst) return "le script envoie déjà beaucoup de monde en ce moment";
            if (_lastTaskT >= 0f && now - _lastTaskT < QuietAfterTask) return "une étape de la mission vient de changer";
            if (_lastMoneyT >= 0f && now - _lastMoneyT < QuietAfterMoney) return "l'argent ou le deck vient de changer";
            if (DialogQuiet(now)) return "une cinématique du script est en cours ou vient de finir";
            if (_shots.Count >= 6) return "les renforts précédents ne sont pas encore réglés";
            return null;
        }

        /// The first added unit of the battle is judged once, CanaryHold seconds after it left: it must have existed, moved, carried
        /// none of the script's group names (checked at once, on arrival) and the mission must have kept moving exactly as it did
        /// before. Anything else stops the module for the battle and says so in the log. Nothing else is added until this is done.
        static void JudgeCanary(float now)
        {
            if (_canaryPassed || _stopped || _canaryUntil < 0f || now < _canaryUntil) return;
            bool seen = _canarySeen, moved = _canaryMoved;                                    // the verdict kept when it left the list
            foreach (var s in _shots) if (s.Canary) { seen = s.Confirmed; moved = s.Moved; break; }
            if (!seen) { Stop("le canari n'a jamais été vu vivant : le mod ne sait pas créer d'unité dans cette version du jeu"); return; }
            if (!moved) { Stop("le canari n'a jamais bougé de son point d'arrivée : les unités ajoutées ne sont pas commandables"); return; }
            int calls = Spawns.ScriptEnemyCalls - _canaryCalls, tasks = _taskChanges - _canaryTasks;
            if (calls <= 0 && tasks <= 0)
            {
                Stop($"depuis le canari ({M(CanaryHold)} s), le script de la mission n'a plus fait apparaître ni changé aucune étape : on ne peut pas prouver qu'il n'est pas bloqué");
                return;
            }
            _canaryPassed = true;
            Log($"canari validé : l'unité est apparue, elle a bougé, elle ne porte aucun groupe du script, et depuis son arrivée le script a envoyé {calls} arrivée(s) et changé {tasks} étape(s). Les renforts suivants sont autorisés.");
        }

        /// Units the mission script itself put on the enemy side in the last BurstWindow seconds.
        static int BurstCount(float now)
        {
            int oldest = -1;
            foreach (var (t, n) in _scriptTrail) if (now - t <= BurstWindow && (oldest < 0 || n < oldest)) oldest = n;
            return oldest < 0 ? 0 : Math.Max(0, Spawns.ScriptEnemyUnits - oldest);
        }

        /// True while a blocking cinematic of the script is running, or in the minute that follows any dialogue.
        /// Two stamps, no counter: OnDialogEnd fires for dialogues that never incremented anything (non-blocking ones), and a
        /// blocking node closed by a skip signal or a Dispose never fires it at all. A counter drifted both ways - it could both
        /// let a vehicle appear in the middle of a cinematic and silence the module for the rest of the battle.
        /// A blocking dialogue that never reports its end stops counting after DialogMax.
        static bool DialogQuiet(float now)
        {
            if (!_dlgOk) return false;                                    // no hook: no cinematic window (said once in the summary)
            long tick = Environment.TickCount64;
            long start = Volatile.Read(ref _dlgTick), end = Volatile.Read(ref _dlgEndTick);
            if (start > 0 && start > end && tick - start < DialogMax) return true;
            long last = Math.Max(start, end);
            return last > 0 && (tick - last) < (long)(QuietAfterDialog * 1000f);
        }

        /// The reason nothing is sent, written at most once a minute and only when it changed (it holds a countdown).
        static void SayRefusal(string why, float now)
        {
            int cut = why.IndexOf(" (", StringComparison.Ordinal);
            string key = cut > 0 ? why.Substring(0, cut) : why;
            if (key == _lastRefuse && now < _nextRefuseLog) return;
            _lastRefuse = key; _nextRefuseLog = now + 60f;
            Log("pas de renfort pour l'instant : " + why);
        }

        static void Stop(string why)
        {
            if (_stopped) return;
            _stopped = true; _stopWhy = why;
            ClearGuard();
            Mod.Log.Warning("[RENFORTS] arrêt pour cette bataille : " + why);
            Mod.Notify(TxtKey.RF_STOPPED);
        }

        static void NoticeOnce(int mode, TxtKey key)
        {
            if (_noticeShown) return;
            _noticeShown = true;
            if (_essais != null && _essais.Value >= 2) { Mod.Notify(TxtKey.RF_GUARD_OFF); return; }
            if (mode == 2) Mod.Notify(key);
        }

        // ---------------------------------------------------------------- dialog hook (lazy, own id, never removed)

        static void EnsureDialogHook()
        {
            if (_dlgTried) return;
            _dlgTried = true;
            try
            {
                // both targets are instance methods with NO parameter: no struct is ever passed by reference to them
                var act = AccessTools.Method(typeof(NodeDialog), nameof(NodeDialog.OnActivated));
                var end = AccessTools.Method(typeof(NodeDialog), nameof(NodeDialog.OnDialogEnd));
                var mine1 = typeof(Renforts).GetMethod(nameof(PostDialogStart), BindingFlags.NonPublic | BindingFlags.Static);
                var mine2 = typeof(Renforts).GetMethod(nameof(PostDialogEnd), BindingFlags.NonPublic | BindingFlags.Static);
                if (act == null || end == null || mine1 == null || mine2 == null) { Log("crochet des cinématiques non installé (méthode absente de cette version du jeu) : les renforts ne surveillent pas les dialogues"); return; }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.RenfortsLibres");
                _harmony.Patch(act, postfix: new HarmonyMethod(mine1));
                _harmony.Patch(end, postfix: new HarmonyMethod(mine2));
                _dlgOk = true;
                Log("crochet des cinématiques installé : aucun renfort ajouté pendant un dialogue bloquant du script ni dans la minute qui suit");
            }
            catch (Exception e) { try { Mod.Log.Warning("[RENFORTS] crochet des cinématiques non installé : " + e.GetBaseException().Message); } catch { } }
        }

        /// Hook, read only: a blocking dialogue of the script started. No allocation, no Unity call, no logging.
        static void PostDialogStart(NodeDialog __instance)
        {
            try
            {
                if (__instance == null || !__instance.Blocking) return;
                Volatile.Write(ref _dlgTick, Environment.TickCount64);
            }
            catch { }
        }
        /// Hook, read only: a dialogue of the script ended.
        static void PostDialogEnd()
        {
            try { Volatile.Write(ref _dlgEndTick, Environment.TickCount64); }
            catch { }
        }

        // ---------------------------------------------------------------- summary

        static void Summary(string label)
        {
            float now = UnityEngine.Time.time;
            var v = _v;
            Log($"relevé ({label}) t={M(_start < 0f ? 0f : now - _start)}s mode={(Mode?.Value ?? "off")} mission={Campaign.MissionUid} difficulté={_diff} : "
              + $"coups={_shotsDone} unités ajoutées={_unitsAdded} pour {_valueAdded} points ; en vol={_shots.Count} suivies par l'IA ennemie={EnemyAi.RenfortCount} ; "
              + $"points versés={_pointsPaid} en {_payments} versement(s){(v != null ? $" (plafond {v.PayCap})" : "")}{(_payDead ? " [plus aucun versement]" : "")}");
            Log($"relevé portes : {(v == null ? "mission pas encore jugée" : v.Allowed ? "mission autorisée : " + v.Why : "mission INTERDITE : " + v.Why)} ; "
              + $"argent : {(v != null && v.PayAllowed ? "autorisé" : "non : " + (v?.PayWhy ?? "?"))} ; fenêtre économique={(_moneyClosed ? "suspendue (tu es à zéro point)" : _moneyOpen ? "ouverte" : "pas encore ouverte")} argent lu={(float.IsNaN(_money) ? "?" : M(_money))} ; "
              + $"frein automatique={_brakeLevel} ; étapes changées={_taskChanges} ; cinématiques surveillées={(_dlgOk ? "oui" : "non")} ; arrêt={(_stopped ? _stopWhy : "non")} ; essais interrompus mémorisés={(_essais?.Value ?? 0)}");
            Log("relevé apparitions : " + Spawns.RenfortState());
        }
    }
}
