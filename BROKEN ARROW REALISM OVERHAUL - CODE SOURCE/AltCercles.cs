// AltCercles: the Alt line-of-sight tool (range circles of the selected unit) and the rangefinder show true distances.
//  - Each Alt distance label shows the radius of the circle it belongs to, in real metres (x EchellePortees when that option is used).
//  - The rangefinder text shows the measured distance the same way.
//  - Only the number of the game's own text is rewritten (its unit and decimals are kept), only when it disagrees, and it is logged.
//  - v0.23.0: a circle whose weapons are all shortened in battle by AntiHeliPortee (anti-helicopter card cap, manual aim cap against
//    helicopters, or manual aim cap against the ground while the per-unit copies are not proven) is drawn at the range really applied, so
//    the circle, its label and the battle agree. Ambiguous circles (another weapon of the unit keeps that range) are left as drawn.
//  - v0.23.0 (antihelico design, display item "low-altitude circles for machine guns"), staged because the tool's behaviour is unproven:
//    adding a weapon type to the tool's low/high altitude list (FOWToolConfig.LowHighAltRangeWeapons) might replace its ground circle.
//    Stage 0 (measurement): when a unit whose listed weapon (e.g. AA guns: M163 ground 1800 / low 1200) has a ground range distinct from
//    every other range of that unit is selected, the drawn circles tell whether the ground circle is kept. At battle end (new mission or
//    quit): ground circle dropped once -> stage -1 (never added); kept twice and never dropped -> stage 1. Stage 1: machine guns,
//    miniguns and autocannons are added to the list in campaign battles with the real stats (journaled, undone with the real stats), so
//    their anti-helicopter range gets its own circle; a dropped ground circle seen on an added type removes them at once, latches the
//    removal for the game session and sets stage -1 at battle end. Whenever adding is not allowed, leftover added types are removed from
//    the tool's config at the next SetWeaponRanges, so a later battle never keeps them.
//    Stage and game version are kept in hidden preferences; a new game version starts again at stage 0. Logs: [CERCLES ALT].
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.FogOfWar.VisualTools;
using Il2CppBrokenArrow.Client.Ecs.UI.TargetLine;
using TMP = Il2CppTMPro.TMP_Text;
using EcsEntity = Il2CppDefaultEcs.Entity;
using WeaponType = Il2CppBrokenArrow.DataBase.Enums.WeaponType;

namespace RealismOverhaul
{
    [HarmonyPatch(typeof(FOWToolScript), nameof(FOWToolScript.SetWeaponRanges))]
    static class Patch_AltCerclesPortees
    {
        static void Prefix(FOWToolScript __instance)
        {
            try { AltCercles.EnsureLowAltTypes(__instance); } catch (Exception e) { AltCercles.Fail(e); }
        }

        static void Postfix(FOWToolScript __instance, EcsEntity selectedUnit)
        {
            try { AltCercles.NoteUnit(selectedUnit); AltCercles.Align(__instance, "portées d'armes posées", true); } catch (Exception e) { AltCercles.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(FOWToolScript), nameof(FOWToolScript.SetActiveCircles))]
    static class Patch_AltCerclesActifs
    {
        static void Postfix(FOWToolScript __instance, bool setActive)
        {
            try
            {
                if (setActive) AltCercles.Align(__instance, "cercles affichés", false);
                else AltCercles.Report(__instance, "cercles masqués");
            }
            catch (Exception e) { AltCercles.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(Il2Cpp.FOWCanvasDistanceElement), nameof(Il2Cpp.FOWCanvasDistanceElement.SetData))]
    static class Patch_AltCerclesTexte
    {
        static void Postfix(Il2Cpp.FOWCanvasDistanceElement __instance, float distance)
        {
            try { AltCercles.Label(__instance, distance); } catch (Exception e) { AltCercles.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(RangefinderFacade), nameof(RangefinderFacade.SetDistance))]
    static class Patch_TelemetreDistance
    {
        static void Postfix(RangefinderFacade __instance, float dist)
        {
            try { AltCercles.Rangefinder(__instance, dist); } catch (Exception e) { AltCercles.Fail(e); }
        }
    }

    static class AltCercles
    {
        const float Tol = 5f;                                       // metres: two ranges closer than this draw the same circle
        const int MaxExpected = 3, KeptToEnable = 2, MaxProofLogs = 20;

        static int _reports, _errors, _fixLogs, _capLogs, _unitEntity = -1, _proofLogs;
        static string _last;
        static float _nextCardScan;
        static bool _relationLogged, _typesBroken;
        static IntPtr _rfText;
        static string _rfOut;
        static float _rfDist = -1f;

        // weapon types that fire at helicopters with a short anti-helicopter range (Munitions.csv OPTION_TIR_ANTIHELICO_COURT rows)
        static readonly WeaponType[] AntiHeliTypes = { WeaponType.LightMachineGun, WeaponType.MediumMachineGun, WeaponType.HeavyMachineGun, WeaponType.MiniGun, WeaponType.AutoCannon };
        static readonly HashSet<WeaponType> _addedTypes = new();

        static MelonPreferences_Entry<int> _stage;
        static MelonPreferences_Entry<string> _stageGame;
        static MelonPreferences_Entry<int> _keptTotal;
        static int _kept, _dropped, _droppedAdded;
        static readonly HashSet<string> _proofSeen = new();

        static bool Active => !Campaign.MissionInerte && Realism.RealModeOn && Realism.IsApplied;   // never in a mission without the mod (added types are removed)

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_AltCercles");
            _stage = c.CreateEntry("EtapeCerclesAntiHelico", 0, description: Build.Desc("Sécurité automatique (cercles anti-hélico des mitrailleuses dans l'outil Alt : 0 mesure, 1 actif, -1 refusé), ne pas modifier"));
            _stageGame = c.CreateEntry("VersionJeuCercles", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _keptTotal = c.CreateEntry("PreuvesCerclesGardes", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            string game = "?";
            try { game = UnityEngine.Application.version; } catch { }
            if (_stageGame.Value != game)
            {
                if (_stage.Value != 0) Mod.Log.Msg($"[CERCLES ALT] nouvelle version du jeu ({game}) : cercles anti-hélico des mitrailleuses de nouveau en mesure");
                _stageGame.Value = game; _stage.Value = 0; _keptTotal.Value = 0;
            }
        }

        internal static void ResetSession() => Decide("fin de bataille");

        internal static void OnQuit() => Decide("fermeture du jeu");

        /// Battle end: the proof samples of this battle switch the stage (hidden preference), then the counters restart.
        static void Decide(string why)
        {
            if (_stage == null) return;
            try
            {
                int before = _stage.Value;
                if (_kept + _dropped + _droppedAdded > 0)
                    Mod.Log.Msg($"[CERCLES ALT] preuve de l'outil Alt ({why}) : cercle au sol gardé {_kept} (total {_keptTotal.Value + (_dropped + _droppedAdded == 0 ? _kept : 0)}), disparu {_dropped} (dont types ajoutés {_droppedAdded}) ; étape {before}");
                if (before >= 0 && (_dropped > 0 || _droppedAdded > 0)) _stage.Value = -1;
                else if (before == 0 && _kept > 0) { _keptTotal.Value = _keptTotal.Value + _kept; if (_keptTotal.Value >= KeptToEnable) _stage.Value = 1; else MelonPreferences.Save(); }
                if (_stage.Value != before)
                {
                    Mod.Log.Msg(_stage.Value == 1
                        ? "[CERCLES ALT] l'outil Alt garde le cercle au sol des armes de sa liste basse altitude : cercles anti-hélico des mitrailleuses et canons activés dès la prochaine bataille"
                        : "[CERCLES ALT] l'outil Alt remplace le cercle au sol des armes de sa liste basse altitude : cercles anti-hélico des mitrailleuses refusés (la portée au sol reste affichée)");
                    MelonPreferences.Save();
                }
            }
            catch (Exception e) { Fail(e); }
            _kept = _dropped = _droppedAdded = 0;
            _proofSeen.Clear();
            _unitEntity = -1;
        }

        /// SetWeaponRanges prefix, stage 1 only: machine guns and autocannons get low-altitude circles (journaled, undone with the real stats).
        /// When adding is no longer allowed (stage -1 or 0, latched break, real stats off, outside a campaign), the types this mod added
        /// earlier are taken out of the tool's config before the game draws, without waiting for the real stats to be restored.
        internal static void EnsureLowAltTypes(FOWToolScript t)
        {
            if (t == null) return;
            bool allowed = !_typesBroken && _stage != null && _stage.Value == 1 && Active && Campaign.InCampaign;
            if (!allowed)
            {
                if (_addedTypes.Count > 0)
                {
                    var old = t._config;
                    if (old != null && old.LowHighAltRangeWeapons != null)
                    {
                        RemoveAdded(old);   // only the types the mod added: the game's own entries are never touched
                        Mod.Log.Msg("[CERCLES ALT] cercles basse altitude des mitrailleuses retirés de l'outil Alt (étape " + (_stage != null ? _stage.Value : 0) + ")");
                    }
                }
                return;
            }
            var cfg = t._config;
            var set = cfg != null ? cfg.LowHighAltRangeWeapons : null;
            if (set == null) return;
            List<string> added = null;
            try
            {
                foreach (var w in AntiHeliTypes)
                {
                    if (set.Contains(w)) continue;
                    set.Add(w);
                    _addedTypes.Add(w);
                    (added ??= new List<string>()).Add(w.ToString());
                }
            }
            catch (Exception e)
            {
                _typesBroken = true;
                Mod.Log.Warning("[CERCLES ALT] cercles basse altitude des mitrailleuses non ajoutés : " + e.GetBaseException().Message);
                return;
            }
            if (added == null) return;
            var keep = cfg;
            Realism.JournalUndo(cfg.Pointer, "AltCercles:LowHighAltRangeWeapons", () => RemoveAdded(keep));
            Mod.Log.Msg($"[CERCLES ALT] cercles basse altitude (portée anti-hélico) ajoutés pour : {string.Join(", ", added)} ({set.Count} types dans la liste de l'outil)");
        }

        static void RemoveAdded(FOWToolConfig cfg)
        {
            var s = cfg?.LowHighAltRangeWeapons;
            if (s != null) foreach (var w in _addedTypes) s.Remove(w);
            _addedTypes.Clear();
        }

        internal static void NoteUnit(EcsEntity unit)
        {
            try { _unitEntity = unit.EntityId; } catch { _unitEntity = -1; }
        }

        /// Stage proof, right after SetWeaponRanges and before any radius is changed: for each listed weapon whose ground range differs
        /// from every other range of the unit, is its ground circle drawn? Only units with at most 3 distinct ranges are judged.
        static void Proof(FOWToolScript t)
        {
            if (_stage == null || _stage.Value < 0 || _unitEntity < 0 || !Campaign.InCampaign) return;
            var cfg = t._config;
            var set = cfg != null ? cfg.LowHighAltRangeWeapons : null;
            var circles = t._circles;
            if (set == null || circles == null) return;
            var all = AntiHeliPortee.UnitWeaponRanges(_unitEntity);
            if (all == null || all.Count == 0) return;
            var weapons = new List<(WeaponType type, float g, float l, float h, bool manual)>();
            foreach (var w in all) if (!weapons.Contains(w)) weapons.Add(w);          // the same weapon row listed for several squad members
            var visible = new List<float>();
            for (int i = 0; i < circles.Count; i++)
            {
                var c = circles[i];
                if (c != null && c.IsVisible && c.Radius > 0f) visible.Add(c.Radius);
            }
            if (visible.Count == 0) return;
            bool manual = false;
            var expected = new List<float>();
            foreach (var w in weapons)
            {
                if (w.manual) manual = true;
                bool listed = set.Contains(w.type);
                AddDistinct(expected, w.g);
                if (listed) { AddDistinct(expected, w.l); AddDistinct(expected, w.h); }
            }
            if (manual || expected.Count > MaxExpected) return;
            for (int wi = 0; wi < weapons.Count; wi++)
            {
                var w = weapons[wi];
                if (!(w.g > 0f) || !set.Contains(w.type)) continue;
                // the ground range must come from this weapon alone: no other range of the unit (nor its own low/high) draws that circle
                bool uniqueG = !(Math.Abs(w.l - w.g) <= Tol) && !(Math.Abs(w.h - w.g) <= Tol);
                for (int oi = 0; oi < weapons.Count && uniqueG; oi++)
                {
                    if (oi == wi) continue;
                    var o = weapons[oi];
                    if (Math.Abs(o.g - w.g) <= Tol) uniqueG = false;
                    else if (set.Contains(o.type) && (Math.Abs(o.l - w.g) <= Tol || Math.Abs(o.h - w.g) <= Tol)) uniqueG = false;
                }
                if (!uniqueG) continue;
                bool drawn = visible.Exists(r => Math.Abs(r - w.g) <= Tol);
                bool othersDrawn = true;
                foreach (float v in expected) if (Math.Abs(v - w.g) > Tol && !visible.Exists(r => Math.Abs(r - v) <= Tol)) othersDrawn = false;
                bool onlyExpected = visible.TrueForAll(r => expected.Exists(v => Math.Abs(v - r) <= Tol));
                bool added = _addedTypes.Contains(w.type);
                string key = $"{w.type}|{w.g:0}|{w.l:0}|{w.h:0}|{(drawn ? 1 : 0)}";
                if (drawn) { if (!onlyExpected) continue; if (_proofSeen.Add(key)) _kept++; }
                else if (othersDrawn && onlyExpected && visible.Count == expected.Count - 1)
                {
                    if (_proofSeen.Add(key)) { if (added) _droppedAdded++; else _dropped++; }
                    if (added && _addedTypes.Count > 0)
                    {
                        // latch for the rest of the game session first, so the next SetWeaponRanges prefix cannot add the types back
                        // (the stage itself only becomes -1 in Decide at battle end)
                        _typesBroken = true;
                        RemoveAdded(cfg);
                        Mod.Log.Msg("[CERCLES ALT] le cercle au sol d'une arme ajoutée a disparu : cercles anti-hélico des mitrailleuses retirés tout de suite");
                    }
                }
                else continue;
                if (_proofLogs++ < MaxProofLogs)
                    Mod.Log.Msg($"[CERCLES ALT] preuve : arme {w.type} (sol {w.g:0} m, basse altitude {w.l:0} m, haute altitude {w.h:0} m) dans la liste basse altitude{(added ? " (ajoutée par le mod)" : "")} : " +
                                $"cercle au sol {(drawn ? "gardé" : "DISPARU")} ; cercles visibles {string.Join("/", visible.ConvertAll(r => r.ToString("0", CultureInfo.InvariantCulture)))}");
            }
        }

        static void AddDistinct(List<float> list, float v)
        {
            if (!(v > 0f)) return;
            foreach (float x in list) if (Math.Abs(x - v) <= Tol) return;
            list.Add(v);
        }

        /// Circles whose weapons are all shortened in battle are drawn at the applied range (campaign battle, real stats only).
        static void ApplyBattleCaps(FOWToolScript t)
        {
            if (_unitEntity < 0 || !Campaign.InCampaign) return;
            var circles = t._circles;
            if (circles == null) return;
            for (int i = 0; i < circles.Count; i++)
            {
                var c = circles[i];
                if (c == null) continue;
                float r = c.Radius;
                if (!AntiHeliPortee.TryShownRange(_unitEntity, r, out float applied, out string why)) continue;
                c.Radius = applied;
                if (_capLogs++ < 30) Mod.Log.Msg($"[CERCLES ALT] cercle {i + 1} : {r:0} m -> {applied:0} m (portée appliquée au combat{(why != null ? " : " + why : "")})");
            }
        }

        /// Battle UI can load its own InfocardConfig copy after the database was modified: rescan now and then.
        static void CardScan(string where)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextCardScan) return;
            _nextCardScan = now + 30f;
            Realism.FixCardMultipliers(null, where);
        }

        /// Circle i and label i are built together: label i must show circle i's radius.
        internal static void Align(FOWToolScript t, string when, bool rangesJustSet)
        {
            if (t == null) return;
            if (Active)
            {
                CardScan("outil Alt");
                if (rangesJustSet) { try { Proof(t); } catch (Exception e) { Fail(e); } }
                try { ApplyBattleCaps(t); } catch (Exception e) { Fail(e); }
                var circles = t._circles;
                var labels = t._distanceElements;
                int n = circles != null && labels != null && circles.Count == labels.Count ? circles.Count : 0;
                for (int i = 0; i < n; i++)
                {
                    var c = circles[i];
                    var l = labels[i];
                    if (c == null || l == null || !(c.Radius > 0f)) continue;
                    Fix(l._distanceText, c.Radius * Realism.DisplayScale, $"cercle Alt {i + 1}");
                }
            }
            Report(t, when);
        }

        internal static void Label(Il2Cpp.FOWCanvasDistanceElement e, float distance)
        {
            if (e == null || !Active) return;
            float radius = distance;
            var t = FOWToolScript.Instance;
            var labels = t != null ? t._distanceElements : null;
            var circles = t != null ? t._circles : null;
            if (labels != null && circles != null && labels.Count == circles.Count)
            {
                for (int i = 0; i < labels.Count; i++)
                {
                    var l = labels[i];
                    if (l == null || l.Pointer != e.Pointer) continue;
                    var c = circles[i];
                    if (c != null && c.Radius > 0f)
                    {
                        radius = c.Radius;
                        if (!_relationLogged) { _relationLogged = true; Mod.Log.Msg($"[CERCLES ALT] texte : distance reçue {distance:0} m, rayon du cercle {radius:0} m"); }
                    }
                    break;
                }
            }
            Fix(e._distanceText, radius * Realism.DisplayScale, "texte Alt");
        }

        internal static void Rangefinder(RangefinderFacade f, float dist)
        {
            if (f == null || !Active || !(dist > 0f)) return;
            var view = f._circleView;
            var text = view != null ? view.TextTopRange : null;
            if (text == null) return;
            if (text.Pointer == _rfText && Math.Abs(dist - _rfDist) < 0.5f && text.text == _rfOut) return;   // unchanged since the last frame
            CardScan("télémètre");
            Fix(text, dist * Realism.DisplayScale, "télémètre");
            _rfText = text.Pointer;
            _rfDist = dist;
            _rfOut = text.text;
        }

        static void Fix(TMP text, float metres, string what)
        {
            if (text == null) return;
            if (!DistanceText.Ensure(text, metres, out var before, out var after)) return;
            if (_fixLogs++ < 30) Mod.Log.Msg($"[CERCLES ALT] {what} : '{before}' -> '{after}' ({metres:0} m réels)");
        }

        internal static void Report(FOWToolScript t, string when)
        {
            if (t == null || _reports >= 80 || Campaign.MissionInerte) return;
            var sb = new StringBuilder();
            var list = t._circles;
            var labels = t._distanceElements;
            int n = list != null ? list.Count : -1;
            sb.Append($"[CERCLES ALT] {when} : {n} cercle(s)");
            for (int i = 0; i < n && i < 12; i++)
            {
                var c = list[i];
                if (c == null) { sb.Append(" [vide]"); continue; }
                bool on = false; try { on = c.gameObject.activeInHierarchy; } catch { }
                string label = "";
                try { if (labels != null && i < labels.Count && labels[i] != null) label = $" texte='{labels[i]._distanceText?.text}' arme='{labels[i]._weaponText?.text}' texte visible={labels[i].IsVisible}"; } catch { }
                sb.Append($" [{c.Radius:0} m visible={c.IsVisible} objet actif={on} alpha={c.Color.a:0.##}{label}]");
            }
            try { var p = t._points; if (p != null) sb.Append($" ; distance max de l'outil {p.MaxDistance:0} m"); } catch { }
            try
            {
                var m = t._defaultModel;
                if (m != null && m.Circles != null)
                {
                    sb.Append(" ; cercles par défaut");
                    for (int i = 0; i < m.Circles.Length; i++) sb.Append(i == 0 ? " " : "/").Append(m.Circles[i]);
                }
            }
            catch { }
            string s = sb.ToString();
            if (s == _last) return;
            _last = s;
            _reports++;
            Mod.Log.Msg(s);
        }

        internal static void Fail(Exception e)
        {
            if (_errors++ < 3) Mod.Log.Warning("[CERCLES ALT] lecture impossible : " + e.Message);
        }
    }

    /// Rewrites the single number of a distance text so it shows a given number of metres (keeps "km" or "m", decimals, separator).
    static class DistanceText
    {
        static readonly Regex Num = new Regex(@"\d(?:[\d   ]*\d)?(?:[.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly char[] Spaces = { ' ', ' ', ' ' };

        internal static bool Ensure(TMP t, float metres, out string before, out string after)
        {
            before = after = null;
            if (t == null || !(metres > 0f)) return false;
            string s = t.text;
            before = s;
            if (string.IsNullOrEmpty(s)) return false;

            Match m = null;
            int count = 0;
            for (var x = Num.Match(s); x.Success; x = x.NextMatch())
            {
                if (s.LastIndexOf('<', x.Index) > s.LastIndexOf('>', x.Index)) continue;   // inside a rich-text tag
                count++;
                if (m == null) m = x;
            }
            if (count != 1) return false;                                                // no number, or several: not a plain distance

            string tail = s.Substring(m.Index + m.Length).TrimStart(Spaces);
            while (tail.StartsWith("<", StringComparison.Ordinal))
            {
                int close = tail.IndexOf('>');
                if (close < 0) break;
                tail = tail.Substring(close + 1).TrimStart(Spaces);
            }
            // kilometre unit in the game's UI languages: Latin "km", Cyrillic "км", Chinese "公里" / "千米"
            bool kmUnit = tail.StartsWith("km", StringComparison.OrdinalIgnoreCase) || tail.StartsWith("км", StringComparison.OrdinalIgnoreCase)
                       || tail.StartsWith("公里", StringComparison.Ordinal) || tail.StartsWith("千米", StringComparison.Ordinal);

            string num = m.Value;
            int sepIdx = num.LastIndexOfAny(new[] { '.', ',' });
            int fraction = sepIdx >= 0 ? num.Length - sepIdx - 1 : 0;
            bool thousands = sepIdx >= 0 && fraction == 3 && !kmUnit;
            bool km = kmUnit || (sepIdx >= 0 && !thousands);                              // "1.2" without unit is kilometres
            char sep = sepIdx >= 0 && !thousands ? num[sepIdx] : '.';
            int decimals = sepIdx >= 0 && !thousands ? fraction : 0;

            double shown;
            if (km)
            {
                string clean = Strip(num).Replace(',', '.');
                if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out shown)) return false;
                shown *= 1000.0;
            }
            else
            {
                string digits = new string(Array.FindAll(num.ToCharArray(), char.IsDigit));
                if (!double.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out shown)) return false;
            }
            if (Math.Abs(shown - metres) <= Math.Max(5.0, metres * 0.015)) return false;

            string repl;
            if (km)
            {
                int dec = Math.Max(decimals, Math.Abs(metres % 1000f) < 0.5f ? 0 : 1);
                repl = (metres / 1000.0).ToString("F" + dec, CultureInfo.InvariantCulture);
                if (sep != '.') repl = repl.Replace('.', sep);
            }
            else repl = Math.Round(metres).ToString("0", CultureInfo.InvariantCulture);

            after = s.Substring(0, m.Index) + repl + s.Substring(m.Index + m.Length);
            t.text = after;
            return true;
        }

        static string Strip(string v)
        {
            var sb = new StringBuilder(v.Length);
            foreach (char ch in v) if (Array.IndexOf(Spaces, ch) < 0) sb.Append(ch);
            return sb.ToString();
        }
    }
}
