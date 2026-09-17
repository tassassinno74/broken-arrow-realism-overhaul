// RealismOverhaul - S-400 "missiles only" mode (v0.20.3): the player's S-400 ignore aircraft, helicopters and drones unless the
//  order-panel button "Anti-aérien" is switched on for that launcher (Assistants.cs, Opt.AA). Earlier design notes:
//  Player request: his S-400 fire only at missiles by default; a button in the order panel turns anti-air on for the
//  selected launcher; the enemy AI and the Pantsir (which always does both) stay untouched.
//  The only engine hook that can do this per unit is the per-candidate target filter TargetSearchHelper.TryAddTargetToCheckList.
//  This step only watches it: patched lazily in a solo campaign battle where the local player owns an S-400 (never at launch,
//  never in skirmish or online), the prefix reads a few ints and returns. A crash guard stops patching after two battles in a
//  row that did not end normally. Log lines start with [S400].
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Search = Il2CppBrokenArrow.Client.Ecs.BattleSystem.TargetSearchHelper;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using EcsEntity = Il2CppDefaultEcs.Entity;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;
using V3 = UnityEngine.Vector3;

namespace RealismOverhaul
{
    static class S400Mode
    {
        const string GuardVersion = "0.24.0";
        const int Ring = 256;                     // power of two: sampled calls kept for the main thread

        static MelonPreferences_Entry<bool> _observe;
        static MelonPreferences_Entry<string> _unitIds, _guardVersion;
        static MelonPreferences_Entry<int> _unclean;
        static HarmonyLib.Harmony _harmony;
        static readonly HashSet<int> _typeIds = new();
        static bool _patched, _patchRefused, _everOnline, _sessionArmed, _netLogged;
        static volatile bool _armed;
        static int[] _flagged = Array.Empty<int>();          // EntityIds of the local player's S-400 launchers, replaced atomically
        static float _next, _nextReport;
        static LuaMap _map;
        static int _mainThread;

        // hook side: plain ints only
        static long _calls, _flaggedCalls, _priorityCalls, _hookErrors, _skipped;
        static volatile bool _filterOn;
        static MelonPreferences_Entry<bool> _filter;
        static int _owned;
        static int _ringWrite, _ringRead;
        static readonly EcsEntity[] _ringTarget = new EcsEntity[Ring];
        static readonly int[] _ringMask = new int[Ring], _ringWeapon = new int[Ring], _ringThread = new int[Ring];
        static readonly bool[] _ringPriority = new bool[Ring];

        // main-thread summaries
        static readonly Dictionary<int, HashSet<int>> _masksByWeapon = new();
        static readonly Dictionary<string, int> _targetKinds = new();
        static readonly HashSet<int> _threads = new();

        static void Log(string s) => Mod.Log.Msg("[S400] " + s);

        internal static bool IsTracked(int unitTypeId) => _typeIds.Contains(unitTypeId);
        /// True when the missiles-only filter can really apply (the button is only shown then).
        internal static bool FilterActive => _observe != null && _observe.Value && _filter != null && _filter.Value && !_patchRefused && !_everOnline && Interlocked.Read(ref _hookErrors) <= 50;

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_S400");
            _observe = c.CreateEntry("ObservationCiblageS400", true, description: Build.Desc("Étape 1 : observe comment tes S-400 choisissent leurs cibles (lignes [S400] du journal) pour préparer le mode « missiles seulement ». Ne change rien au jeu."));
            _unitIds = c.CreateEntry("UnitesS400", "227", description: Build.Desc("Identifiants des unités concernées (227 = S-400)"));
            _filter = c.CreateEntry("S400MissilesParDefaut", true, description: Build.Desc("true = tes S-400 ne visent que les missiles tant que le bouton « Anti-aérien » n'est pas activé sur eux ; false = ils tirent sur tout (comme le jeu)"));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _mainThread = Environment.CurrentManagedThreadId;
            foreach (var s in (_unitIds.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(s, out var id)) _typeIds.Add(id);
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }   // a new mod version re-arms the guard
        }

        internal static void ResetSession()
        {
            EndBattle("nouvelle mission");
            _everOnline = false; _netLogged = false;
        }

        internal static void OnQuit() => EndBattle("fermeture du jeu");

        /// Normal end of a battle: the crash guard is cleared and the hook goes idle (the detour stays but returns at once).
        static void EndBattle(string why)
        {
            _armed = false;
            _flagged = Array.Empty<int>();
            _typeByUid.Clear();                                    // unit ids belong to the battle that spawned them
            if (!_sessionArmed) return;
            _sessionArmed = false;
            if (_unclean.Value != 0) { _unclean.Value = 0; MelonPreferences.Save(); }
            Report(true);
            Log($"observation terminée ({why})");
        }

        static int _wait;

        /// Every frame in campaign, work once per second.
        internal static void Frame()
        {
            if (_observe == null || !_observe.Value || _patchRefused) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _next) return;
            if (!Planif.Take(ref _wait, _sessionArmed ? Planif.WaitNormal : Planif.WaitArm)) return;   // one heavy module job per frame (Planif.cs)
            _next = now + 1f;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) { if (_sessionArmed) EndBattle("plus de partie"); return; }
            if (!Solo()) { if (_armed) { _armed = false; _flagged = Array.Empty<int>(); Log("partie en ligne : observation coupée"); } return; }
            if (_hookErrors <= 50) _filterOn = _filter.Value;
            if (!Assistants.AaControlReady)
            {
                // no anti-air button (assistants off, interface failed or not built yet): S-400 keep the vanilla behaviour
                if (_armed) Log("bouton anti-aérien indisponible : S-400 en mode normal (tir sur tout)");
                _armed = false; _flagged = Array.Empty<int>(); Drain(); return;
            }
            RefreshFlagged(cp.UID);
            if (_flagged.Length == 0) { _armed = false; Drain(); return; }
            if (!_sessionArmed)
            {
                if (_unclean.Value >= 2)
                {
                    _patchRefused = true;
                    Mod.Log.Warning("[S400] observation désactivée : les deux dernières parties observées ne se sont pas terminées normalement");
                    Mod.Notify(TxtKey.N_S400_OFF);
                    return;
                }
                _sessionArmed = true;
                _unclean.Value = _unclean.Value + 1;
                MelonPreferences.Save();            // on disk before the detour is installed or used
                Log($"S-400 : {_owned} à toi, {_flagged.Length} en mode missiles seulement (filtre {(_filterOn ? "actif" : "désactivé")})");
            }
            if (!_patched && !TryPatch()) return;
            _armed = true;
            Drain();
            if (now >= _nextReport) { _nextReport = now + 30f; Report(false); }
        }

        static bool Solo()
        {
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _everOnline = true;   // latched for the battle
                if (!_netLogged) { _netLogged = true; Log($"réseau : état={st} scénarioRéseau={net} esclave={slave} hôte={host} -> {(_everOnline ? "en ligne" : "solo")}"); }
            }
            catch { return false; }
            return !_everOnline;
        }

        // unit type of a spawned unit, by uid: it never changes while the unit lives, so the SpawnData chain is read once instead of every second
        static readonly Dictionary<int, int> _typeByUid = new();

        static void RefreshFlagged(int local)
        {
            _map ??= new LuaMap();
            var units = _map.GetUnits(V3.zero, 1_000_000f, -1, local);
            var ids = new List<int>();
            int owned = 0;
            for (int i = 0; i < (units?.Length ?? 0); i++)
            {
                try
                {
                    var u = units[i];
                    if (u == null || !u.IsAlive() || u.GetOwnerPlayerUID() != local) continue;
                    int uid = u.UID;
                    if (!_typeByUid.TryGetValue(uid, out int typeId))
                    {
                        typeId = u.SpawnData?.Unit?.UnitID ?? 0;
                        if (typeId > 0 && _typeByUid.Count < 20000) _typeByUid[uid] = typeId;
                    }
                    if (!_typeIds.Contains(typeId)) continue;
                    owned++;
                    if (!Assistants.AntiAirOn(u.UID)) ids.Add(u.Entity.EntityId);   // the order-panel button switches anti-air on for this launcher
                }
                catch { }
            }
            _flagged = ids.ToArray();
            _owned = owned;
        }

        static bool TryPatch()
        {
            if (!Reperage.TargetFilterAllowed)
            {
                // 0.22.8: same engine hook as Reperage, which stopped every unit from firing in the 0.22.7 test battle
                _patchRefused = true;
                Log("filtre de ciblage NON installé (il empêchait les unités de tirer) : mode missiles seulement du S-400 en pause");
                return false;
            }
            try
            {
                var target = AccessTools.Method(typeof(Search), "TryAddTargetToCheckList");
                if (target == null) { _patchRefused = true; Log("filtre de ciblage introuvable dans cette version du jeu"); return false; }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.S400");
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(S400Mode).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
                Log("filtre de ciblage observé (aucun changement de comportement)");
                return true;
            }
            catch (Exception e)
            {
                _patchRefused = true;
                Mod.Log.Warning("[S400] observation impossible : " + e.GetBaseException().Message);
                return false;
            }
        }

        /// Hot path (every weapon, every candidate): for the local player's S-400 in "missiles only" mode, an aircraft,
        /// helicopter or drone candidate is not added to the target list. Priority candidates (the player's own attack
        /// order) always pass, projectiles always pass, any error lets the original run: the fallback is vanilla behaviour.
        static bool Prefix(ref EcsEntity unitEntity, ref EcsEntity weaponEntity, ref EcsEntity checkTargetEntity, bool isTargetPriority, int weaponTargetMask)
        {
            if (Campaign.MissionInerte || !_armed) return true;                       // mission without the mod: the original runs untouched
            try
            {
                Interlocked.Increment(ref _calls);
                var ids = _flagged;
                int uid = unitEntity.EntityId;
                bool mine = false;
                for (int i = 0; i < ids.Length; i++) if (ids[i] == uid) { mine = true; break; }
                if (!mine) return true;
                long n = Interlocked.Increment(ref _flaggedCalls);
                if (isTargetPriority) Interlocked.Increment(ref _priorityCalls);
                if ((n & 3) == 0)                                  // one sample in four for the log
                {
                    int w = Interlocked.Increment(ref _ringWrite) & (Ring - 1);
                    _ringTarget[w] = checkTargetEntity;
                    _ringMask[w] = weaponTargetMask;
                    _ringWeapon[w] = weaponEntity.EntityId;
                    _ringPriority[w] = isTargetPriority;
                    _ringThread[w] = Environment.CurrentManagedThreadId;
                }
                if (!_filterOn || isTargetPriority) return true;
                int ut = Convert.ToInt32(BSH.GetUnitType(checkTargetEntity));
                if ((ut & (8 | 16)) == 0) return true;             // not a helicopter (8) nor an aircraft (16)
                if (BSH.IsTargetProjectile(ref checkTargetEntity)) return true;
                Interlocked.Increment(ref _skipped);
                return false;
            }
            catch
            {
                if (Interlocked.Increment(ref _hookErrors) > 50) _filterOn = false;   // repeated errors: back to vanilla
                return true;
            }
        }

        /// Main thread: turns the sampled calls into readable facts (mask per weapon, kind of candidate).
        static void Drain()
        {
            int end = Volatile.Read(ref _ringWrite);
            int start = end - _ringRead > Ring ? end - Ring : _ringRead;
            for (int k = start + 1; k <= end; k++)
            {
                int i = k & (Ring - 1);
                try
                {
                    if (!_masksByWeapon.TryGetValue(_ringWeapon[i], out var set)) _masksByWeapon[_ringWeapon[i]] = set = new HashSet<int>();
                    set.Add(_ringMask[i]);
                    _threads.Add(_ringThread[i]);
                    var t = _ringTarget[i];
                    string kind;
                    try
                    {
                        int ut = Convert.ToInt32(BSH.GetUnitType(t));
                        bool proj = BSH.IsTargetProjectile(ref t);
                        kind = $"type {ut}{(proj ? " projectile" : "")}{(_ringPriority[i] ? " prioritaire" : "")}";
                    }
                    catch (Exception e) { kind = "illisible (" + e.GetBaseException().GetType().Name + ")"; }
                    _targetKinds[kind] = _targetKinds.GetValueOrDefault(kind) + 1;
                }
                catch { }
            }
            _ringRead = end;
        }

        static void Report(bool final)
        {
            Drain();
            long calls = Interlocked.Read(ref _calls);
            if (calls == 0 && _masksByWeapon.Count == 0) return;
            Log($"{(final ? "bilan" : "relevé")} : appels du filtre {calls} (dont tes S-400 {Interlocked.Read(ref _flaggedCalls)}, prioritaires {Interlocked.Read(ref _priorityCalls)}), " +
                $"avions/hélicos écartés {Interlocked.Read(ref _skipped)}, erreurs {Interlocked.Read(ref _hookErrors)}, fils {string.Join("/", _threads)} (principal {_mainThread})");
            if (_masksByWeapon.Count > 0) Log("masques de cibles : " + string.Join(" ; ", _masksByWeapon.Select(kv => $"arme {kv.Key} = {string.Join("/", kv.Value.OrderBy(x => x))}")));
            if (_targetKinds.Count > 0) Log("cibles examinées : " + string.Join(", ", _targetKinds.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} x{kv.Value}")));
            if (!final) return;
            Interlocked.Exchange(ref _calls, 0); Interlocked.Exchange(ref _flaggedCalls, 0); Interlocked.Exchange(ref _priorityCalls, 0); Interlocked.Exchange(ref _hookErrors, 0); Interlocked.Exchange(ref _skipped, 0);
            _masksByWeapon.Clear(); _targetKinds.Clear(); _threads.Clear();
        }
    }
}
