// FicheArsenal: the arsenal unit card holds its own InfocardConfig reference. In real-stats mode its range
// multiplier must be 1 like the game's (true metres), otherwise every distance on the card is doubled.
// Prefixes on the two methods that write the card's numbers (simple signatures only).
using System;
using HarmonyLib;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Arsenal.InfoCard;
using Units = Il2CppBrokenArrow.DataBase.Models.Units;

namespace RealismOverhaul
{
    [HarmonyPatch(typeof(UnitInfoCard), nameof(UnitInfoCard.SetUnitWeaponList))]
    static class Patch_FicheArsenalArmes
    {
        static void Prefix(UnitInfoCard __instance, Units unit)
        {
            try { FicheArsenal.Fix(__instance, "armes"); UnitCopies.SyncShown(unit, "fiche arsenal"); } catch (Exception e) { FicheArsenal.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(UnitInfoCard), nameof(UnitInfoCard.SetUnitStatistic))]
    static class Patch_FicheArsenalStats
    {
        static void Prefix(UnitInfoCard __instance, Units unit)
        {
            try { FicheArsenal.Fix(__instance, "caractéristiques"); UnitCopies.SyncShown(unit, "fiche arsenal"); } catch (Exception e) { FicheArsenal.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(UnitInfoCard), nameof(UnitInfoCard.OnToggleCompactMode))]
    static class Patch_FicheArsenalCompact
    {
        static void Prefix(UnitInfoCard __instance)
        {
            try { FicheArsenal.Fix(__instance, "mode compact"); } catch (Exception e) { FicheArsenal.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(UnitInfoCard), nameof(UnitInfoCard.ShowBattleMode))]
    static class Patch_FicheArsenalBataille
    {
        static void Prefix(UnitInfoCard __instance)
        {
            try { FicheArsenal.Fix(__instance, "fiche en bataille"); } catch (Exception e) { FicheArsenal.Fail(e); }
        }
    }

    static class FicheArsenal
    {
        static bool _logged;
        static int _errors;
        static float _nextScan;

        internal static void Fix(UnitInfoCard card, string where)
        {
            if (Campaign.MissionInerte || card == null || !Realism.RealModeOn || !Realism.IsApplied) return;
            var own = card._infocardConfig;
            if (!_logged)
            {
                _logged = true;
                var main = GameConfig.Instance?.InfocardConfig;
                string ownTxt = own == null ? "aucun" : $"'{own.name}' x{own.EffectiveRangeMultiplier}";
                string mainTxt = main == null ? "aucun" : $"'{main.name}' x{main.EffectiveRangeMultiplier}";
                bool same = own != null && main != null && own.Pointer == main.Pointer;
                Mod.Log.Msg($"[VRAIES STATS] fiche arsenal ({where}) : réglage de la fiche {ownTxt}, réglage du jeu {mainTxt}, même objet={same}");
            }
            if (own != null) Realism.FixCardMultipliers(own, "fiche arsenal");
            // the sub-panels (weapon and ammo lines, compact mode) read the card's static copy
            Il2CppBrokenArrow.Client.Ecs.UI.Infocard.InfocardConfig shared = null;
            try { shared = UnitInfoCard.InfocardConfig; } catch { }
            if (shared != null) Realism.FixCardMultipliers(shared, "fiche arsenal, réglage partagé");
            // sub-panels (compact mode, ammo panels) may read another loaded copy: rescan now and then
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= _nextScan) { _nextScan = now + 10f; Realism.FixCardMultipliers(null, "fiche arsenal"); }
        }

        internal static void Fail(Exception e)
        {
            if (_errors++ < 3) Mod.Log.Warning("[VRAIES STATS] fiche arsenal : correction impossible : " + e.Message);
        }
    }
}
