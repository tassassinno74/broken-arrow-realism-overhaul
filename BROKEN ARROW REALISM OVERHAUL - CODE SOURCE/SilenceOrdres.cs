// SilenceOrdres: the mod's own automatic orders must not make the player's crews answer "bien reçu".
//  Player report, 2026-09-19 04:00: "les véhicules font que parler, à chaque fois que l'artillerie prend une cible,
//  comme si je spammais la commande « va là »". Measured in the same log: the artillery assistant issued 53 fire
//  orders in nine minutes, and every one of them went through the game's ordinary command path, so the game played
//  the acknowledgement voice exactly as if the player had clicked. The rate is not the defect - the voice is. An
//  order the player never gave must never be acknowledged out loud.
//  How: Il2CppBrokenArrow.Client.Ecs.Audio.AudioCommandSystem.PlayCommand(Entity) takes its Entity BY VALUE and
//  Il2CppDefaultEcs.Entity is {Int16 Version, Int16 WorldId, Int32 EntityId}, all primitives - so this is a blittable
//  by-value target, never the forbidden by-reference struct pattern. A prefix returning false skips the voice for
//  that one call and nothing else; the worst possible failure of this module is a missing sound.
//  Scope: only entities the mod itself just ordered, within SilenceMs. Everything the player orders keeps its voice.
using System;
using HarmonyLib;
using AudioCmd = Il2CppBrokenArrow.Client.Ecs.Audio.AudioCommandSystem;
using EcsEntity = Il2CppDefaultEcs.Entity;

namespace RealismOverhaul
{
    [HarmonyPatch(typeof(AudioCmd), nameof(AudioCmd.PlayCommand))]
    static class Patch_SilenceOrdres
    {
        static bool Prefix(EcsEntity entity)
        {
            try { return !SilenceOrdres.Muet(entity.EntityId); }
            catch (Exception e) { SilenceOrdres.Fail(e); return true; }   // any doubt: the game keeps its voice
        }
    }

    /// Marks the units the mod has just ordered, so the acknowledgement voice of THAT order is skipped.
    static class SilenceOrdres
    {
        const int Slots = 64;                       // ring: far more than the handful of pieces ordered in one pass
        const long SilenceMs = 1500;                // the voice is played a frame or two after the command is posted
        const long MaxErrors = 20;

        static readonly int[] _eid = new int[Slots];
        static readonly long[] _ms = new long[Slots];
        static int _next;
        static long _errors, _mutes, _asked;
        static bool _off;

        /// Main thread, called just before the mod posts an order for this unit. Allocation-free.
        internal static void Marquer(int entityId)
        {
            if (_off || entityId == 0) return;
            int i = _next; _next = (i + 1) % Slots;
            _eid[i] = entityId;
            _ms[i] = Environment.TickCount64;
        }

        /// True when this unit was ordered by the mod less than SilenceMs ago: its acknowledgement is skipped.
        internal static bool Muet(int entityId)
        {
            if (_off || entityId == 0) return false;
            _asked++;
            long now = Environment.TickCount64;
            for (int i = 0; i < Slots; i++)
            {
                if (_eid[i] != entityId) continue;
                if (now - _ms[i] > SilenceMs) { _eid[i] = 0; continue; }   // stale: forget it and let the voice play
                _eid[i] = 0;                                              // one order, one silence
                _mutes++;
                return true;
            }
            return false;
        }

        internal static void Fail(Exception e)
        {
            if (++_errors <= MaxErrors) return;
            _off = true;
            Mod.Log.Warning("[SILENCE] accusés de réception : trop d'erreurs, le mod laisse le jeu parler normalement (" + e.GetBaseException().Message + ")");
        }

        /// One line per battle, in the report voice of the other modules.
        internal static void OnBattleEnd()
        {
            if (_asked == 0 && _mutes == 0) return;
            Mod.Log.Msg($"[SILENCE] accusés de réception : {_mutes} ordre(s) du mod rendus muets sur {_asked} annonce(s) vue(s)" +
                        (_off ? " ; module coupé par sécurité" : "") + " ; tes propres ordres parlent toujours");
            _asked = _mutes = 0;
        }

        internal static void ResetSession()
        {
            for (int i = 0; i < Slots; i++) { _eid[i] = 0; _ms[i] = 0; }
            _next = 0; _asked = _mutes = _errors = 0; _off = false;
        }
    }
}
