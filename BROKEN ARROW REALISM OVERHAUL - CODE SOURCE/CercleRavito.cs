// CercleRavito: the white supply circle drawn on the ground keeps its size, its place and its colour, and only its LINE is thinned.
//  WHY THE PREVIOUS APPROACH IS GONE. Until 18/09 this module built its own ring TEXTURE and handed it to the two Images through
//  Image.overrideSprite. The measurement line of the player's battle of 18/09 killed that idea outright:
//      image inactive : sprite '(aucun)' (texture ?, zone ?), type Simple, shader 'UI/RingShader_Gradient'
//  There is no sprite at all on either Image. The circle is not a stretched picture: it is drawn by a shader, 'UI/RingShader_Gradient',
//  over a RectTransform whose size is the circle's diameter (SupplyRadius::Init writes sizeDelta = (2r, 2r), proved at disassembly,
//  review\ravitaillement_design.md §1.1). An overrideSprite does nothing to a shader-drawn ring, which is why nothing changed that
//  night. The whole texture-generation code is deleted, not left dormant: the fix now lives in the MATERIAL.
//  WHAT IS DONE INSTEAD. The shader's own parameters are read at runtime and written once per game session into the log, in French:
//  name, type and current value, for both Images. That listing is the only thing that can tell us what the ring's width is really
//  called, so it is written whatever happens next - even when the module can do nothing else at all. Then, if one of those parameters
//  plausibly holds the width of the line, it is scaled down so the drawn line gets thinner while the circle's OUTER edge stays exactly
//  where it is. Nothing else is touched: not the size, not the position, not the colour, not the inactive/active pair.
//  The outer edge is what makes this safe. Three shapes are accepted, in that order:
//    1. an inner/outer radius PAIR (this shader is a GRADIENT ring, so its width may well be expressed that way): only the INNER
//       radius is raised towards the outer one, so the outer edge is provably untouched - the best case by far;
//    2. a lone thickness/width/border/stroke parameter: it is multiplied by the wanted fraction, which can only pull the line in;
//    3. as a last resort a falloff/feather/softness parameter, with a tighter floor, because on a gradient ring the visible band can
//       be the falloff itself.
//  Nothing that looks like a plain radius, a colour, a texture, a system value of the UI, or a distance to the camera is ever
//  written, and the two ends of a pair have to name the same shape ('_InnerRadius' with '_OuterRadius', never with some unrelated
//  maximum). If no parameter is plausible the module says so in one clear line and switches itself off for the session: a wrong
//  guess must degrade to vanilla.
//  NEVER A SHARED MATERIAL. Writing the game's own material would change every ring in the game and outlive the battle. The module
//  copies it instead (new Material(source)) and hands the copy to the Image through the per-instance slot Image.material; the game's
//  material object is only ever READ. One copy is made per source material and reused by every circle of the session (they all want
//  the same line), marked HideAndDontSave so no scene load unloads it: at most MaxMaterials copies in a whole game session, nothing
//  built per battle, nothing built per frame, no leak. Every Image written is journaled with the material it had, so switching the
//  setting off, changing the width, or the error kill-switch puts the game's own material straight back on every live circle.
//  A circle whose source material no longer holds the value it held when the copy was made means the game sets that parameter itself,
//  per circle: the module stops there rather than fight it.
//  HOOK: SupplyRadius.ToggleVisibility(Boolean) - a blittable bool by value, the only safe signature of that class, and it fired
//  correctly on 18/09. Init takes a Nullable<Single> and OnSetUIVisible a SwitchUIData by reference: both are listed as forbidden in
//  review\spec_ravito.md and are never touched (a generic value type handed to the Il2CppInterop trampoline is exactly the trap that
//  broke a campaign on 18/09). ToggleVisibility is called by the 7 places that show or hide a circle (scratchpad\xref_supply.txt), so
//  both a dropped crate and a truck's ability circle come through it, always after Init has sized the rect. It is called again every
//  time a circle is shown, so each circle is settled once and then skipped on a pointer test alone: no allocation, no game call.
//  LOG PREFIXES. Three one-shot lines carry their own prefix on purpose: in the PUBLIC build ModLog keeps only one line per prefix
//  every 5 minutes, and on 18/09 that is why only the measurement line came back. '[CERCLE RAVITO]' keeps the measurement, the
//  waiting line and the battle summary; '[CERCLE RAVITO NUANCEUR]' carries the once-per-session parameter listing; '[CERCLE RAVITO
//  TRAIT]' carries what was retained and written. Each is the first line of its own prefix, so all three always reach the log.
//  Preferences: RealismOverhaul_CercleRavito (created the first time a supply circle appears). The three entries of the texture era -
//  TraitFinCercleRavito, LargeurTraitCercleRavito, BordExterieurCercleRavito - no longer exist and are ignored if still in the file.
//  Preference texts are French like every other module's: this module does not own Txt.cs, so nothing here is shown on screen.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using MelonLoader;
using SupplyRadius = Il2CppBrokenArrow.Client.Ecs.UI.SupplyRadius;
using GameConfig = Il2CppBrokenArrow.Client.Ecs.Configs.GameConfig;
using UImage = UnityEngine.UI.Image;
using UMat = UnityEngine.Material;
using UShader = UnityEngine.Shader;

namespace RealismOverhaul
{
    /// ToggleVisibility takes one Boolean by value: blittable, nothing of it is marshalled by reference, and the circle is always
    /// sized by Init before it is ever shown. No other method of SupplyRadius may be patched (see the file header).
    [HarmonyPatch(typeof(SupplyRadius), nameof(SupplyRadius.ToggleVisibility), new Type[] { typeof(bool) })]
    static class Patch_CercleRavitoVisible
    {
        static void Postfix(SupplyRadius __instance)
        {
            try { CercleRavito.OnShown(__instance); } catch (Exception e) { CercleRavito.Fail(e); }
        }
    }

    static class CercleRavito
    {
        const int MaxErrors = 10;            // errors before the module gives the game's circle back for the session
        const int MaxSeen = 64;              // circles remembered as settled; past that the oldest is tested again, which changes nothing
        const int MaxMaterials = 6;          // source materials served in a whole game session
        const int MaxMade = 24;              // copies built in a whole game session, width changes included: past that, something is wrong
        const int MaxJournal = 256;          // Images whose original material is remembered at the same time
        const int MaxProps = 64;             // parameters written into the listing line
        const float DefaultFine = 0.35f;     // the resupply radius went from 50 m to 150 m, so the drawn line tripled: a third gives it back
        const float MinFine = 0.10f, MaxFine = 1f;
        const float FalloffFloor = 0.35f;    // a falloff is the last resort: it is never cut further than this, or the ring could vanish
        const float KeepAtLeast = 0.05f;     // a written value never falls under this fraction of the game's own
        const string OurName = "RealismOverhaul_CercleRavito";
        const string Tag = "[CERCLE RAVITO]";
        const string TagShader = "[CERCLE RAVITO NUANCEUR]";
        const string TagLine = "[CERCLE RAVITO TRAIT]";

        // shape of the parameter retained, for the log and for the write
        const int ShapePair = 0, ShapeThick = 1, ShapeFall = 2;

        static MelonPreferences_Entry<bool> _on;
        static MelonPreferences_Entry<float> _fine;
        static bool _prefsTried, _off, _measured, _listed, _battleKnown, _waitLogged, _fullLogged;
        static int _errors, _changed, _leftAlone, _skipLogs, _made;
        static float _fineUsed = -1f;        // fraction the copies in _mats were built with: a change rebuilds them
        static IntPtr _battle;

        // circles already settled this battle, by pointer: a circle shown every frame costs a few integer tests and nothing else
        static readonly IntPtr[] _seen = new IntPtr[MaxSeen];
        static int _seenAt;

        // ---------------------------------------------------------------- the copies and the journal

        /// One copy of one of the game's materials, with what was written in it. The game's own material is only ever read.
        sealed class MatSlot
        {
            internal IntPtr SrcPtr;          // the game's material this copy was made from
            internal IntPtr ShaderPtr;       // and its shader: an address freed and handed to another material is caught here
            internal UMat Inst;              // our copy, never an asset of the game
            internal int Shape;
            internal string Name;            // name of the parameter written, for the log
            internal int Id;                 // its name id
            internal float Was, Now;         // its value in the game's material and in our copy
            internal int GuardId = -1;       // outer radius of a pair: never written, only checked
            internal float GuardWas;
        }

        /// One Image written, with the material it had before. Putting that material back is all it takes to give the game its circle back.
        sealed class JEntry
        {
            internal UImage Img;
            internal UMat Orig;
        }

        static readonly List<MatSlot> _mats = new();
        static readonly List<JEntry> _journal = new();

        /// One parameter of the shader, read once.
        struct PropInfo
        {
            internal string Name;
            internal int Id, Type;
            internal float Num;
            internal bool HasNum, HasRange;
            internal float Lo, Hi;
            internal string Text;            // what the listing line shows for this parameter
        }

        // ---------------------------------------------------------------- settings (created the first time a circle appears)

        static void EnsurePrefs()
        {
            if (_prefsTried) return;
            _prefsTried = true;
            try
            {
                var c = MelonPreferences.CreateCategory("RealismOverhaul_CercleRavito");
                _on = c.CreateEntry("AffinerTraitCercleRavito", true, description: Build.Desc(
                    "Affine le trait blanc du cercle de ravitaillement dessiné au sol. Le cercle n'est pas une image mais un dessin fait par " +
                    "un nuanceur, et son trait grossit avec le rayon : depuis que le rayon est passé de 50 m à 150 m le trait est trois fois " +
                    "plus épais. Le mod fait une COPIE du matériau du jeu, n'écrit que dans sa copie, et n'y change que la largeur du trait : " +
                    "la taille du cercle, sa position et sa couleur blanche ne bougent pas, et le bord extérieur du cercle reste exactement au " +
                    "vrai rayon de ravitaillement. Si le mod ne trouve aucun réglage de largeur dans le nuanceur, il ne change rien du tout et " +
                    "l'écrit dans le journal. false = le trait du jeu, tel quel",
                    "Trait du cercle de ravitaillement affiné sans changer la taille du cercle."));
                _fine = c.CreateEntry("FinesseTraitCercleRavito", DefaultFine, description: Build.Desc(
                    "Largeur voulue pour le trait du cercle de ravitaillement, en fraction de la largeur du jeu. 1 = le trait du jeu, " +
                    "0.35 = un trait un peu moins de trois fois plus fin (valeur par défaut : le rayon ayant triplé, cela redonne à peu près " +
                    "le trait d'avant), 0.5 = deux fois plus fin. Minimum 0.1, maximum 1. Seule la largeur du trait change : le cercle garde " +
                    "sa taille, et son bord extérieur reste sur le vrai rayon",
                    "Largeur du trait du cercle de ravitaillement, en fraction de celle du jeu (0.35 par défaut)."));
                // written to MelonPreferences.cfg right away, or the player has nothing to edit until another module happens to save
                try { MelonPreferences.Save(); } catch { }
            }
            catch (Exception e) { Disable("réglages du trait impossibles à créer : " + e.GetBaseException().Message); }
        }

        static bool WantOn()
        {
            try { return _on == null || _on.Value; } catch { return false; }
        }

        static float WantedFine()
        {
            float v = DefaultFine;
            try { if (_fine != null) v = _fine.Value; } catch { }
            if (float.IsNaN(v) || v < MinFine) return MinFine;
            return v > MaxFine ? MaxFine : v;
        }

        // ---------------------------------------------------------------- battle bookkeeping (this module has no frame of its own)

        /// A new game session means a new battle: the measurement line is written again and the counters restart. A session that
        /// cannot be read yet leaves the current battle as it is.
        static void NoteBattle()
        {
            IntPtr p = IntPtr.Zero;
            try { var ctx = Campaign.Ctx(); if (ctx != null) p = ctx.Pointer; } catch { }
            if (p == IntPtr.Zero || (_battleKnown && p == _battle)) return;
            if (_battleKnown) Report();
            _battleKnown = true;
            _battle = p;
            _measured = false;
            _changed = _leftAlone = 0;
            ForgetAll();                                        // the circles of the battle that just ended are gone
            Purge();                                            // and so are their Images: nothing of that battle is held on to
        }

        /// Summary of the battle that just ended, written at the first circle of the next one.
        static void Report()
        {
            if (_changed == 0 && _leftAlone == 0) return;
            try
            {
                Mod.Log.Msg($"{Tag} bilan de la bataille : {_changed} cercle(s) au trait affiné, {_leftAlone} laissé(s) comme le jeu les dessine" +
                            (_errors > 0 ? $", {_errors} erreur(s)" : ""));
            }
            catch { }
        }

        /// Circles already settled, by pointer. A pointer freed by the game and handed to another circle would at worst leave that
        /// one with the game's own ring: never a wrong size, never a broken circle.
        static bool Seen(IntPtr p)
        {
            for (int i = 0; i < MaxSeen; i++) if (_seen[i] == p) return true;
            return false;
        }

        static void Remember(IntPtr p)
        {
            _seen[_seenAt] = p;
            _seenAt++;
            if (_seenAt >= MaxSeen) _seenAt = 0;
        }

        static void ForgetAll()
        {
            for (int i = 0; i < MaxSeen; i++) _seen[i] = IntPtr.Zero;
            _seenAt = 0;
        }

        // ---------------------------------------------------------------- the hook

        /// SupplyRadius.ToggleVisibility postfix: once per circle, both Images are given our copy of the game's material, the one
        /// whose line is thinner. Everything before the write only reads.
        internal static void OnShown(SupplyRadius sr)
        {
            if (_off || sr == null) return;
            if (Identite.Blocked || Mod.AntiCheatActive || Mod.Actif == null || !Mod.Actif.Value) return;
            IntPtr id;
            try { id = sr.Pointer; } catch { return; }
            if (id == IntPtr.Zero || Seen(id)) return;           // settled already: a circle shown every frame stops here
            EnsurePrefs();
            if (_off || Campaign.MissionInerte) return;
            NoteBattle();

            var rect = sr._radiusRectTransform;
            var dim = sr._nonactiveImage;
            var lit = sr._activeImage;
            if (rect == null || dim == null || lit == null) { Disable("le cadre ou les deux images du cercle sont introuvables"); return; }

            float sizeX = 0f, sizeY = 0f, rectW = 0f, scale = 1f;
            try
            {
                var sd = rect.sizeDelta; sizeX = sd.x; sizeY = sd.y;
                var rc = rect.rect; rectW = rc.width;
                var ls = rect.lossyScale; scale = ls.x;
            }
            catch (Exception e)
            {
                // the listing is written even here: it is the only thing that can tell us the real name of the width
                ListOnce(dim, lit);
                Disable("taille du cercle illisible : " + e.GetBaseException().Message);
                return;
            }

            // measured first, before a single value is written and whatever the settings say: this is the line the next test log is read on
            if (!_measured)
            {
                _measured = true;
                try
                {
                    float radius = GameRadius(sr);
                    Mod.Log.Msg($"{Tag} cercle mesuré avant toute retouche : cadre {F(sizeX)} x {F(sizeY)} " +
                                $"(rect {F(rectW)}, échelle {F(scale)}), rayon de ravitaillement du jeu {F(radius)} m " +
                                $"(cadre attendu {F(radius * 2f)} pour une caisse posée ; pour un camion, c'est le double du rayon de sa capacité) ; " +
                                $"image inactive : {Describe(dim)} ; image active : {Describe(lit)}");
                }
                catch (Exception e) { Fail(e); }
            }

            // the parameters of the shader, once per game session, whatever the settings say and whatever can be done afterwards:
            // this listing is the only thing that can tell us what the width of the line is really called.
            ListOnce(dim, lit);

            // nothing is written unless the player wants it: the setting off puts the game's material back on every circle we hold
            if (!WantOn())
            {
                RestoreAll("le réglage AffinerTraitCercleRavito est sur false");
                WaitLine();
                Remember(id);
                return;
            }

            // a width changed in the settings between two circles: the copies were built for the old one and are dropped, live circles first
            float fine = WantedFine();
            if (_fineUsed >= 0f && Math.Abs(fine - _fineUsed) > 0.0001f)
            {
                RestoreAll($"largeur de trait changée ({F(_fineUsed)} -> {F(fine)})");
                _mats.Clear();                                   // the copies already handed out are not destroyed: an Image we no longer
                ForgetAll();                                     // hold must never end up pointing at a destroyed material. Every circle
            }                                                    // is settled again at its next show, with the new width
            _fineUsed = fine;

            // the width asked for IS the game's own: nothing to thin, and the circles already thinned go back to the game's material
            if (fine >= 0.999f)
            {
                RestoreAll("FinesseTraitCercleRavito est sur 1, c'est-à-dire la largeur du jeu");
                Remember(id);
                return;
            }

            if (IsOurs(dim) && IsOurs(lit)) { Remember(id); return; }   // this circle already carries the thin line

            // Both Images are settled together, and only once everything they need is in hand: a circle is never left with a thin line
            // in one state and the game's line in the other. Anything refused leaves this circle exactly as the game drew it, and the
            // circle is marked as settled either way so a refusal is never repeated at every show.
            var src1 = MatOf(dim, "inactive");
            var src2 = MatOf(lit, "active");
            if (src1 == null || src2 == null) { Remember(id); return; }
            var slot1 = Plan(src1, fine);
            if (slot1 == null) { Remember(id); return; }          // Plan counts, logs and switches off by itself
            var slot2 = Plan(src2, fine);
            if (slot2 == null) { Remember(id); return; }
            if (!Room(2)) { Remember(id); return; }
            // journaled before either is written: giving the game its two circles back is then two writes and nothing else
            if (!Hold(dim, src1) || !Hold(lit, src2)) { Remember(id); return; }
            try { dim.material = slot1.Inst; lit.material = slot2.Inst; }
            catch (Exception e) { Fail(e); Remember(id); return; }
            _changed++;
            Remember(id);
        }

        /// One line per game session while the thin line is switched off: the player has to be able to see that the module is there,
        /// what it measured, and how to try it.
        static void WaitLine()
        {
            if (_waitLogged) return;
            _waitLogged = true;
            try
            {
                Mod.Log.Msg($"{Tag} trait affiné en attente : le cercle du jeu n'a pas été touché. Pour l'essayer, mettre " +
                            "AffinerTraitCercleRavito = true dans la catégorie RealismOverhaul_CercleRavito des réglages du mod, " +
                            "et régler la largeur voulue avec FinesseTraitCercleRavito (0.35 = trait presque trois fois plus fin, " +
                            "1 = le trait du jeu). La taille du cercle ne change pas.");
            }
            catch { }
        }

        /// The global resupply radius, in metres, for the measurement line only: it is what a dropped crate is sized on, and a truck's
        /// ability circle has its own radius instead. Nothing decides on this value. 0 = unreadable.
        static float GameRadius(SupplyRadius sr)
        {
            try
            {
                var sc = sr._gameConfig?.SupplyConfig;
                if (sc != null && sc.ResupplyRadius > 0f) return sc.ResupplyRadius;
            }
            catch { }
            try
            {
                var sc = GameConfig.Instance?.SupplyConfig;
                if (sc != null && sc.ResupplyRadius > 0f) return sc.ResupplyRadius;
            }
            catch { }
            return 0f;
        }

        // ---------------------------------------------------------------- the once-per-session parameter listing

        /// Every parameter of the shader that draws the circle, with its type and its current value, in one line, once per game
        /// session. Written even when the module can do nothing else: it is what tells us the real name of the width.
        static void ListOnce(UImage dim, UImage lit)
        {
            if (_listed) return;
            _listed = true;
            try
            {
                UMat m1 = null, m2 = null;
                try { m1 = dim.material; } catch { }
                try { m2 = lit.material; } catch { }
                string s1 = Params(m1);
                bool same = false;
                try { same = m1 != null && m2 != null && m1.Pointer == m2.Pointer; } catch { }
                string s2 = same ? "même matériau que l'image inactive" : Params(m2);
                Mod.Log.Msg($"{TagShader} paramètres du nuanceur qui dessine le cercle, relevés une seule fois par session " +
                            $"— image inactive : {s1} ; image active : {s2}");
            }
            catch (Exception e)
            {
                try { Mod.Log.Warning($"{TagShader} paramètres du nuanceur illisibles : {e.GetBaseException().Message}"); } catch { }
                Fail(e);
            }
        }

        /// Text of one material: its name, its shader, and every parameter the shader declares with its type and its current value.
        static string Params(UMat m)
        {
            if (m == null) return "aucun matériau";
            var list = new List<PropInfo>();
            string shader = Scan(m, list);
            if (shader == null) return $"matériau '{NameOf(m)}', nuanceur illisible";
            var sb = new StringBuilder();
            sb.Append("matériau '").Append(NameOf(m)).Append("', nuanceur '").Append(shader).Append("', ")
              .Append(list.Count).Append(" paramètre(s) : ");
            if (list.Count == 0) sb.Append("(aucun)");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(list[i].Text);
            }
            return sb.ToString();
        }

        /// Reads every parameter the shader of that material declares. Returns the shader's name, or null when it cannot be read.
        /// Only reads: Shader.GetPropertyCount / GetPropertyName / GetPropertyType / GetPropertyNameId and Material.Get*.
        static string Scan(UMat m, List<PropInfo> into)
        {
            UShader sh = null;
            string shaderName = null;
            int n = 0;
            try
            {
                sh = m.shader;
                if (sh == null) return null;
                shaderName = sh.name;
                n = sh.GetPropertyCount();
            }
            catch { return null; }
            if (n < 0) return shaderName;
            if (n > MaxProps) n = MaxProps;
            for (int i = 0; i < n; i++)
            {
                var p = new PropInfo { Id = -1, Type = -1 };
                try
                {
                    p.Name = sh.GetPropertyName(i);
                    p.Id = sh.GetPropertyNameId(i);
                    p.Type = (int)sh.GetPropertyType(i);
                }
                catch { continue; }
                if (p.Name == null || p.Id < 0) continue;
                string text = Value(m, sh, i, ref p);
                p.Text = text;
                into.Add(p);
            }
            return shaderName;
        }

        /// "nom = type valeur" for the listing line, and the number itself when the parameter holds one.
        static string Value(UMat m, UShader sh, int index, ref PropInfo p)
        {
            string kind = TypeName(p.Type);
            string val = "?";
            try
            {
                bool has = m.HasProperty(p.Id);
                if (!has) val = "(absent du matériau)";
                else if (p.Type == 2 || p.Type == 3 || p.Type == 5)      // nombre, nombre borné, entier
                {
                    float v = m.GetFloat(p.Id);
                    p.Num = v;
                    p.HasNum = !float.IsNaN(v) && !float.IsInfinity(v);
                    val = F6(v);
                    if (p.Type == 3)
                    {
                        try
                        {
                            var lim = sh.GetPropertyRangeLimits(index);
                            p.Lo = lim.x; p.Hi = lim.y;
                            p.HasRange = p.Hi > p.Lo;
                            if (p.HasRange) val += $" [{F6(p.Lo)} à {F6(p.Hi)}]";
                        }
                        catch { }
                    }
                }
                else if (p.Type == 0)                                     // couleur
                {
                    var c = m.GetColor(p.Id);
                    val = $"({F6(c.r)}, {F6(c.g)}, {F6(c.b)}, {F6(c.a)})";
                }
                else if (p.Type == 1)                                     // vecteur
                {
                    var v = m.GetVector(p.Id);
                    val = $"({F6(v.x)}, {F6(v.y)}, {F6(v.z)}, {F6(v.w)})";
                }
                else if (p.Type == 4)                                     // texture
                {
                    string tn = "(aucune)";
                    try { var t = m.GetTexture(p.Id); if (t != null) tn = t.name; } catch { }
                    val = "'" + tn + "'";
                }
            }
            catch (Exception e) { val = "illisible (" + e.GetBaseException().Message + ")"; }
            return p.Name + " = " + kind + " " + val;
        }

        /// UnityEngine.Rendering.ShaderPropertyType, in French. The numbers are Unity's own and are printed with the word so an
        /// unknown one still reaches the log intact.
        static string TypeName(int t) => t switch
        {
            0 => "couleur",
            1 => "vecteur",
            2 => "nombre",
            3 => "nombre borné",
            4 => "texture",
            5 => "entier",
            _ => "type " + t,
        };

        // ---------------------------------------------------------------- choosing the parameter that holds the width

        /// Letters and digits of a parameter name, lowercased: '_Inner Radius' and '_innerRadius' are then the same text.
        static string Key(string n)
        {
            if (n == null) return "";
            var sb = new StringBuilder(n.Length);
            for (int i = 0; i < n.Length; i++)
            {
                char ch = n[i];
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        static bool Any(string k, string[] words)
        {
            for (int i = 0; i < words.Length; i++) if (k.Contains(words[i])) return true;
            return false;
        }

        /// Never written, whatever else the name says: colours, textures, everything the UI owns, and - just as important - everything
        /// that speaks of a distance to the camera, a depth or a level of detail. A ring that faded out at range because the mod cut a
        /// '_FadeDistance' would look exactly like a bug, so those words are banned before any matching is even tried.
        static readonly string[] Banned =
        {
            "color", "colour", "tint", "alpha", "opacity", "texture", "tex", "map", "stencil", "mask", "clip", "blend",
            "cull", "zwrite", "ztest", "zclip", "speed", "time", "angle", "rotation", "rotate", "segment", "count",
            "dash", "seed", "noise", "glow", "intensity", "emission", "shadow", "enable", "toggle", "keyword", "fill", "offset",
            "dist", "depth", "lod", "cam", "view", "range", "fog", "scroll", "pulse", "anim", "wave",
        };

        /// Words that make a name a size rather than a look: an inner or outer radius has to carry one of them, and the two ends of a
        /// pair have to carry the SAME one ('_InnerRadius' with '_OuterRadius', never '_InnerRadius' with some unrelated maximum).
        /// 'rad' on its own is deliberately absent: it also sits inside 'gradient', and this shader is a GRADIENT ring, so it would
        /// turn '_InnerGradient' into an inner radius. 'weight' is absent from the widths for the same kind of reason.
        static readonly string[] SizeWords = { "radius", "edge", "size", "circle", "ring", "cutoff", "threshold" };
        static readonly string[] ThickWords = { "thickness", "thick", "width", "border", "stroke", "outline" };
        /// Edge softness only. A bare 'edge' is NOT here on purpose: '_Edge' alone can be the radius itself, and scaling it would
        /// shrink the drawn circle. '_EdgeWidth' and '_EdgeSoftness' are caught by their other word.
        static readonly string[] FallWords = { "falloff", "feather", "softness", "soft", "smooth", "antialias", "blur" };

        static bool Usable(in PropInfo p) => (p.Type == 2 || p.Type == 3) && p.HasNum && !Any(Key(p.Name), Banned);

        static bool IsInner(in PropInfo p)
        {
            string k = Key(p.Name);
            if (k.Contains("outer") || k.Contains("max")) return false;
            return (k.Contains("inner") || k.Contains("hole") || k.Contains("min")) && Any(k, SizeWords);
        }

        static bool IsOuter(in PropInfo p)
        {
            string k = Key(p.Name);
            if (k.Contains("inner") || k.Contains("hole") || k.Contains("min")) return false;
            return (k.Contains("outer") || k.Contains("max")) && Any(k, SizeWords);
        }

        /// The two ends of a pair say the same thing about the same shape: '_InnerRadius' and '_OuterRadius' both say 'radius'.
        /// Without that, a lone maximum of something else could be taken for the outer edge and the computed width would be nonsense.
        static bool SamePair(in PropInfo a, in PropInfo b)
        {
            string ka = Key(a.Name), kb = Key(b.Name);
            for (int i = 0; i < SizeWords.Length; i++)
                if (ka.Contains(SizeWords[i]) && kb.Contains(SizeWords[i])) return true;
            return false;
        }

        static bool IsThick(in PropInfo p) => Any(Key(p.Name), ThickWords);

        static bool IsFall(in PropInfo p) => Any(Key(p.Name), FallWords);

        /// The more a name says "this is the stroke", the higher it scores: '_Thickness' beats '_BorderWidth' beats '_Outline'.
        static int ThickScore(string k)
        {
            if (k.Contains("thickness") || k.Contains("thick")) return 6;
            if (k.Contains("linewidth") || k.Contains("ringwidth") || k.Contains("strokewidth")) return 5;
            if (k.Contains("width")) return 4;
            if (k.Contains("border") || k.Contains("stroke")) return 3;
            if (k.Contains("outline")) return 2;
            return 1;
        }

        // ---------------------------------------------------------------- the write

        /// The material that Image is drawn with, or null when there is nothing to work from (the circle is then left as the game draws it).
        static UMat MatOf(UImage img, string what)
        {
            UMat src;
            try { src = img.material; }
            catch (Exception e) { Fail(e); return null; }
            if (src == null)
            {
                _leftAlone++;
                if (_skipLogs++ < 3)
                {
                    try { Mod.Log.Msg($"{Tag} cercle laissé comme le jeu le dessine : l'image {what} n'a aucun matériau"); } catch { }
                }
                return null;
            }
            return src;
        }

        /// The copy that serves that source material, built the first time it is seen. Returns null when nothing may be written.
        static MatSlot Plan(UMat src, float fine)
        {
            IntPtr sp = IntPtr.Zero, hp = IntPtr.Zero;
            UShader sh = null;
            try
            {
                sp = src.Pointer;
                sh = src.shader;
                if (sh == null) { _leftAlone++; return null; }
                hp = sh.Pointer;
            }
            catch (Exception e) { Fail(e); return null; }

            for (int i = 0; i < _mats.Count; i++)
            {
                var s = _mats[i];
                if (s.SrcPtr != sp || s.ShaderPtr != hp) continue;
                bool dead;
                try { dead = s.Inst == null; } catch { dead = true; }
                if (dead) { _mats.RemoveAt(i); break; }          // our copy is gone: one is built again below
                // the game's own material must still hold the value it held when the copy was made. If it does not, the game sets
                // that parameter itself for each circle and our single copy would fight it: the module stops rather than guess.
                if (!Same(src, s.Id, s.Was) || (s.GuardId >= 0 && !Same(src, s.GuardId, s.GuardWas)))
                {
                    Disable($"le jeu règle lui-même « {s.Name} » pour chaque cercle : le trait est laissé comme le jeu le dessine");
                    return null;
                }
                return s;
            }

            if (_mats.Count >= MaxMaterials) { Disable($"plus de {MaxMaterials} matériaux différents pour un même cercle"); return null; }
            if (_made >= MaxMade) { Disable($"{MaxMade} copies de matériau dans une seule session"); return null; }

            var list = new List<PropInfo>();
            if (Scan(src, list) == null) { Disable("le nuanceur du cercle est illisible"); return null; }

            int shape = -1, idx = -1, guard = -1;
            int bestScore = 0;
            for (int i = 0; i < list.Count; i++)                 // 1. an inner / outer radius pair: the outer edge is then provably untouched
            {
                if (!Usable(list[i]) || !IsInner(list[i]) || !(list[i].Num > 0f)) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (j == i || !Usable(list[j]) || !IsOuter(list[j])) continue;
                    if (!(list[j].Num > list[i].Num)) continue;
                    if (!SamePair(list[i], list[j])) continue;   // the two ends have to describe the same shape
                    if (list[j].Num > list[i].Num * 20f) continue;  // a ring whose inner radius is twenty times smaller is not a ring
                    shape = ShapePair; idx = i; guard = j;
                    break;
                }
                if (shape >= 0) break;
            }
            if (shape < 0)                                       // 2. a lone thickness: multiplying it can only pull the line in
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (!Usable(list[i]) || !IsThick(list[i]) || !(list[i].Num > 0f)) continue;
                    int sc = ThickScore(Key(list[i].Name));
                    if (sc <= bestScore) continue;
                    bestScore = sc; shape = ShapeThick; idx = i;
                }
            }
            if (shape < 0)                                       // 3. last resort: on a gradient ring the visible band can be the falloff
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (!Usable(list[i]) || !IsFall(list[i]) || !(list[i].Num > 0f)) continue;
                    shape = ShapeFall; idx = i;
                    break;
                }
            }

            if (shape < 0)
            {
                Disable("aucun paramètre du nuanceur ne ressemble à une largeur de trait (voir la ligne " + TagShader +
                        " du journal) : le cercle reste exactement celui du jeu");
                return null;
            }

            var target = list[idx];
            float was = target.Num, now;
            if (shape == ShapePair)
            {
                float outer = list[guard].Num;
                now = outer - (outer - was) * fine;              // the outer edge is never written: the drawn radius cannot move
                float ceil = outer - Math.Abs(outer) * 0.001f;
                if (now > ceil) now = ceil;
                if (now < was) now = was;                        // the line is only ever made thinner
            }
            else
            {
                float f = shape == ShapeFall && fine < FalloffFloor ? FalloffFloor : fine;
                now = was * f;
                float floor = was * KeepAtLeast;
                if (now < floor) now = floor;
            }
            if (target.HasRange)
            {
                if (now < target.Lo) now = target.Lo;
                if (now > target.Hi) now = target.Hi;
            }
            if (float.IsNaN(now) || float.IsInfinity(now)) { Disable($"valeur illisible pour « {target.Name} »"); return null; }
            if (Math.Abs(now - was) <= Math.Abs(was) * 0.0005f)
            {
                Disable($"« {target.Name} » ne peut pas être affiné à cette valeur ({F6(was)}) : le cercle reste celui du jeu");
                return null;
            }

            UMat inst = null;
            try
            {
                inst = new UMat(src);                            // OUR copy: the game's material is never written
                if (inst == null) { Disable("copie du matériau du cercle impossible"); return null; }
                inst.name = OurName;
                inst.hideFlags = UnityEngine.HideFlags.HideAndDontSave;   // kept for the whole session, never saved, never unloaded with a scene
                inst.SetFloat(target.Id, now);
            }
            catch (Exception e) { Disable("copie du matériau du cercle impossible : " + e.GetBaseException().Message); return null; }
            _made++;

            var slot = new MatSlot
            {
                SrcPtr = sp,
                ShaderPtr = hp,
                Inst = inst,
                Shape = shape,
                Name = target.Name,
                Id = target.Id,
                Was = was,
                Now = now,
                GuardId = shape == ShapePair ? list[guard].Id : -1,
                GuardWas = shape == ShapePair ? list[guard].Num : 0f,
            };
            _mats.Add(slot);

            try
            {
                string how = shape switch
                {
                    ShapePair => $"rayon intérieur « {target.Name} » remonté de {F6(was)} à {F6(now)} vers le rayon extérieur " +
                                 $"« {list[guard].Name} » = {F6(list[guard].Num)}, qui n'est PAS touché : le bord extérieur du cercle ne bouge pas",
                    ShapeThick => $"épaisseur « {target.Name} » ramenée de {F6(was)} à {F6(now)}",
                    _ => $"dégradé de bord « {target.Name} » ramené de {F6(was)} à {F6(now)} (dernier recours : aucun paramètre d'épaisseur " +
                         "dans ce nuanceur)",
                };
                Mod.Log.Msg($"{TagLine} trait affiné à {F(_fineUsed)} de sa largeur : {how}. Écrit dans une COPIE du matériau du jeu " +
                            $"(le matériau du jeu n'est pas touché), copie n° {_made} de la session. La taille, la position et la " +
                            "couleur du cercle ne changent pas.");
            }
            catch { }
            return slot;
        }

        /// True when the game's material still holds that value: read only, no allocation.
        static bool Same(UMat m, int id, float v)
        {
            try
            {
                if (!m.HasProperty(id)) return false;
                float cur = m.GetFloat(id);
                return Math.Abs(cur - v) <= Math.Max(0.00001f, Math.Abs(v) * 0.01f);
            }
            catch { return false; }
        }

        static bool IsOurs(UImage img)
        {
            UMat m;
            try { m = img.material; } catch { return false; }
            if (m == null) return false;
            IntPtr p;
            try { p = m.Pointer; } catch { return false; }
            for (int i = 0; i < _mats.Count; i++)
            {
                var s = _mats[i];
                if (s.Inst == null) continue;
                IntPtr q;
                try { q = s.Inst.Pointer; } catch { continue; }
                if (q == p) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- the journal: stopping puts the game's own material back

        /// Room for that many more circles in the journal, the circles the game has already destroyed dropped first. False = the
        /// module writes nothing more for now: an Image whose own material is not remembered could never be given it back.
        static bool Room(int n)
        {
            if (_journal.Count + n <= MaxJournal) return true;
            Purge();
            if (_journal.Count + n <= MaxJournal) return true;
            _leftAlone++;
            if (!_fullLogged)
            {
                _fullLogged = true;
                try
                {
                    Mod.Log.Msg($"{Tag} {MaxJournal} cercles suivis en même temps : les suivants sont laissés comme le jeu " +
                                "les dessine, pour qu'aucun ne reste sans retour au matériau du jeu");
                }
                catch { }
            }
            return false;
        }

        /// Remembers the material that Image had. Room() has already made sure there is a place for it.
        static bool Hold(UImage img, UMat orig)
        {
            if (_journal.Count >= MaxJournal) return false;
            _journal.Add(new JEntry { Img = img, Orig = orig });
            return true;
        }

        /// Drops the circles the game has already destroyed: nothing of a battle that is over is held on to.
        static void Purge()
        {
            for (int i = _journal.Count - 1; i >= 0; i--)
            {
                bool dead;
                try { dead = _journal[i].Img == null; } catch { dead = true; }
                if (dead) _journal.RemoveAt(i);
            }
        }

        /// Gives the game's own material back to every circle still alive. The copies themselves are never destroyed: an Image the
        /// module no longer holds must never be left pointing at a destroyed material.
        static void RestoreAll(string why)
        {
            if (_journal.Count == 0) return;
            int n = 0;
            for (int i = 0; i < _journal.Count; i++)
            {
                var e = _journal[i];
                try
                {
                    if (e.Img == null) continue;
                    e.Img.material = e.Orig;
                    n++;
                }
                catch { }
            }
            _journal.Clear();
            if (n > 0)
            {
                try { Mod.Log.Msg($"{Tag} matériau du jeu remis sur {n} cercle(s) : {why}"); } catch { }
            }
        }

        // ---------------------------------------------------------------- reading and reporting

        static string Describe(UImage img)
        {
            try
            {
                string sprite = "(aucun)", texture = "?", zone = "?";
                var s = img.sprite;
                if (s != null)
                {
                    sprite = NameOf(s);
                    try { var t = s.texture; if (t != null) texture = t.width + "x" + t.height + " px"; } catch { }
                    try { var r = s.rect; zone = F(r.width) + "x" + F(r.height) + " px"; } catch { }
                }
                string material = "(aucun)", shader = "?";
                try
                {
                    var m = img.material;
                    if (m != null)
                    {
                        material = NameOf(m);
                        var sh = m.shader;
                        if (sh != null) shader = sh.name;
                    }
                }
                catch { }
                return $"sprite '{sprite}' (texture {texture}, zone {zone}), type {img.type}, matériau '{material}', shader '{shader}'";
            }
            catch (Exception e) { return "illisible (" + e.GetBaseException().Message + ")"; }
        }

        static string NameOf(UnityEngine.Object o)
        {
            try { return o != null ? o.name : "(aucun)"; } catch { return "?"; }
        }

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// Shader values are often small fractions: the listing has to keep enough digits to be read back.
        static string F6(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        internal static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3)
            {
                try { Mod.Log.Warning($"{Tag} erreur : " + e.GetBaseException().Message); } catch { }
            }
            if (_errors >= MaxErrors) Disable($"{_errors} erreurs");
        }

        /// Back to the game's own circle for the rest of the game session: a wrong guess must never leave a broken circle.
        /// Every circle the module still holds gets the game's material back on the spot.
        static void Disable(string why)
        {
            if (_off) return;
            _off = true;
            int held = _journal.Count;
            try { RestoreAll("affinage du trait coupé"); } catch { }
            try
            {
                Mod.Log.Warning($"{Tag} affinage du trait coupé jusqu'au redémarrage du jeu : {why}. Les prochains cercles seront " +
                                "ceux du jeu" + (held > 0
                                    ? $", et le matériau du jeu a été remis sur les {held} cercle(s) encore affichés."
                                    : ", et rien n'a été changé jusqu'ici."));
            }
            catch { }
        }
    }
}
