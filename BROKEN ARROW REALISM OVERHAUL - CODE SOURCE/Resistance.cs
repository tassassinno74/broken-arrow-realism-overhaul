// RealismOverhaul - "Unités résistantes" cheat (kind 3): damage received by the local player's own units is multiplied by
//  Cheats.ToughFactor. Two runtime Harmony patches, installed lazily the first time the toggle is switched on (never at launch,
//  no [HarmonyPatch] attribute so ApplyPatches ignores this class):
//   - postfix on BattleSystemHelpers.CalculateHitDamage (shells, bullets, missiles): the returned damage is scaled;
//   - prefix on CloseQuartersCombatSystem.DeductHitPoints (infantry close combat): the incoming damage is scaled.
//  Both only act while Cheats.ToughArmed (toggle on + solo campaign) and only when the target's EntityId is in
//  Cheats.LocalEntityIds (alive units owned by CurrentPlayer.UID, refreshed on the main thread by Cheats.FastTick).
//  The hooks may run on worker threads: plain field reads, Interlocked counters, no Unity call, no allocation, no logging.
//  Repeated errors switch the hooks off (vanilla damage). Log lines start with [TRICHE] résistance.
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using BSH = Il2CppBrokenArrow.Client.Ecs.BattleSystem.BattleSystemHelpers;
using CQC = Il2CppBrokenArrow.Client.Ecs.Infantry.Systems.CloseQuartersCombatSystem;
using EcsEntity = Il2CppDefaultEcs.Entity;

namespace RealismOverhaul
{
    static class Resistance
    {
        const int MaxErrors = 50;
        const float ReportEvery = 60f;
        const float DefaultFactor = 0.25f;

        static HarmonyLib.Harmony _harmony;
        static bool _triedShots, _triedMelee, _okShots, _okMelee, _disabledLogged, _reportFailLogged;
        static float _nextReport;

        // hook side: plain values only
        static volatile bool _disabled;
        static volatile float _factor = DefaultFactor;       // copy of Cheats.ToughFactor, refreshed on the main thread
        static long _shotCalls, _shotHits, _meleeCalls, _meleeHits, _errors;

        // last values written to the log (main thread)
        static long _lastShotCalls, _lastShotHits, _lastMeleeCalls, _lastMeleeHits, _lastErrors;

        /// At least one damage hook is installed and the error kill-switch has not tripped.
        internal static bool Usable => (_okShots || _okMelee) && !_disabled;

        /// Main thread (Cheats.SetTough(true)): installs the two patches once each. Returns Usable. Never throws.
        internal static bool EnsurePatched()
        {
            try
            {
                RefreshFactor();
                if (!_triedShots)
                {
                    _triedShots = true;
                    _okShots = TryPatch("tirs", typeof(BSH), "CalculateHitDamage", nameof(Postfix), false);
                }
                if (!_triedMelee)
                {
                    _triedMelee = true;
                    _okMelee = TryPatch("corps à corps", typeof(CQC), "DeductHitPoints", nameof(Prefix), true);
                }
                if (_disabled) Mod.Notify(TxtKey.N_TOUGH_ERRORS);
                else if (!_okShots && !_okMelee) Mod.Notify(TxtKey.N_TOUGH_IMPOSSIBLE);
                return Usable;
            }
            catch (Exception e)
            {
                try { Mod.Log.Warning("[TRICHE] résistance : préparation impossible : " + e.GetBaseException().Message); } catch { }
                return false;
            }
        }

        static bool TryPatch(string label, Type type, string methodName, string patchName, bool isPrefix)
        {
            try
            {
                var target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    Mod.Log.Warning($"[TRICHE] résistance : correctif {label} non installé ({methodName} introuvable dans cette version du jeu)");
                    return false;
                }
                var mine = typeof(Resistance).GetMethod(patchName, BindingFlags.NonPublic | BindingFlags.Static);
                if (mine == null)
                {
                    Mod.Log.Warning($"[TRICHE] résistance : correctif {label} non installé (méthode {patchName} absente du mod)");
                    return false;
                }
                _harmony ??= new HarmonyLib.Harmony("RealismOverhaul.Resistance");
                if (isPrefix) _harmony.Patch(target, prefix: new HarmonyMethod(mine));
                else _harmony.Patch(target, postfix: new HarmonyMethod(mine));
                Mod.Log.Msg($"[TRICHE] résistance : correctif {label} installé");
                return true;
            }
            catch (Exception e)
            {
                try { Mod.Log.Warning($"[TRICHE] résistance : correctif {label} non installé : {e.GetBaseException().Message}"); } catch { }
                return false;
            }
        }

        static void RefreshFactor()
        {
            float f = Cheats.ToughFactor;
            if (float.IsNaN(f) || f < 0f) f = DefaultFactor;
            else if (f > 1f) f = 1f;
            _factor = f;
        }

        /// Hot path, any thread: static Single CalculateHitDamage(Entity target, Single baseDamage, Ammunitions ammoInfo,
        /// Single penetration, Boolean forceTopArmorAttack, ArmorSides armorSide). Scales the result for the local player's units.
        static void Postfix(EcsEntity target, ref float __result)
        {
            if (Campaign.MissionInerte || _disabled || !Cheats.ToughArmed) return;     // mission without the mod: vanilla damage
            try
            {
                Interlocked.Increment(ref _shotCalls);
                if (!(__result > 0f)) return;
                var ids = Cheats.LocalEntityIds;
                if (ids == null || ids.Length == 0) return;
                int id = target.EntityId;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] != id) continue;
                    __result *= _factor;
                    Interlocked.Increment(ref _shotHits);
                    return;
                }
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) _disabled = true;   // repeated errors: back to vanilla damage
            }
        }

        /// Hot path, any thread: instance Void DeductHitPoints(Entity& target, Entity& shooter, Single incomingDamage,
        /// Single stressDamage). Scales the hit points removed from the local player's units (stress is left unchanged).
        static void Prefix(ref EcsEntity target, ref float incomingDamage)
        {
            if (Campaign.MissionInerte || _disabled || !Cheats.ToughArmed) return;
            try
            {
                Interlocked.Increment(ref _meleeCalls);
                if (!(incomingDamage > 0f)) return;
                var ids = Cheats.LocalEntityIds;
                if (ids == null || ids.Length == 0) return;
                int id = target.EntityId;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] != id) continue;
                    incomingDamage *= _factor;
                    Interlocked.Increment(ref _meleeHits);
                    return;
                }
            }
            catch
            {
                if (Interlocked.Increment(ref _errors) >= MaxErrors) _disabled = true;   // repeated errors: back to vanilla damage
            }
        }

        /// Main thread, called from Cheats.FastTick: keeps the factor copy fresh, reports the kill-switch once and logs
        /// the counters every 60 s when something happened. Never throws.
        internal static void ReportIfDue()
        {
            try
            {
                RefreshFactor();
                if (_disabled && !_disabledLogged)
                {
                    _disabledLogged = true;
                    Mod.Log.Warning($"[TRICHE] résistance : {MaxErrors} erreurs dans les correctifs, dégâts normaux jusqu'au redémarrage du jeu");
                    Mod.Notify(TxtKey.N_TOUGH_ERRORS);
                }
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (_nextReport <= 0f) { _nextReport = now + ReportEvery; return; }
                if (now < _nextReport) return;
                _nextReport = now + ReportEvery;
                Report(false);
            }
            catch (Exception e)
            {
                if (_reportFailLogged) return;
                _reportFailLogged = true;
                try { Mod.Log.Warning("[TRICHE] résistance : relevé impossible : " + e.GetBaseException().Message); } catch { }
            }
        }

        /// Logs the counters when they changed since the last line.
        static void Report(bool final)
        {
            long shotCalls = Interlocked.Read(ref _shotCalls), shotHits = Interlocked.Read(ref _shotHits);
            long meleeCalls = Interlocked.Read(ref _meleeCalls), meleeHits = Interlocked.Read(ref _meleeHits);
            long errors = Interlocked.Read(ref _errors);
            if (shotCalls == _lastShotCalls && shotHits == _lastShotHits && meleeCalls == _lastMeleeCalls
                && meleeHits == _lastMeleeHits && errors == _lastErrors) return;
            _lastShotCalls = shotCalls; _lastShotHits = shotHits;
            _lastMeleeCalls = meleeCalls; _lastMeleeHits = meleeHits;
            _lastErrors = errors;
            string factor = _factor.ToString("0.##", CultureInfo.InvariantCulture);
            Mod.Log.Msg($"[TRICHE] résistance{(final ? " (bilan)" : "")} : {shotCalls} calculs de dégâts ({shotHits} sur tes unités, réduits x {factor}), " +
                $"{meleeCalls} coups au corps à corps ({meleeHits} réduits)" + (errors > 0 ? $", {errors} erreur(s)" : ""));
        }

        /// Main thread, called from Cheats.Reset at each mission start: last summary, then counters back to zero.
        /// The installed patches and the error kill-switch stay as they are.
        internal static void ResetSession()
        {
            try { Report(true); }
            catch { }
            Interlocked.Exchange(ref _shotCalls, 0);
            Interlocked.Exchange(ref _shotHits, 0);
            Interlocked.Exchange(ref _meleeCalls, 0);
            Interlocked.Exchange(ref _meleeHits, 0);
            Interlocked.Exchange(ref _errors, 0);
            _lastShotCalls = _lastShotHits = _lastMeleeCalls = _lastMeleeHits = _lastErrors = 0;
            _nextReport = 0f;
        }
    }
}
