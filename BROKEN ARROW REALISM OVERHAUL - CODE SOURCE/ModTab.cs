// ModTab: a native-looking "Mod" tab in the game's Options window (main menu and Escape menu).
//  - Detection: Harmony postfix on SettingsScreen.OnInitialize, poll of the static TabButton._lastMainSelectedButton,
//    last resort FindFirstObjectByType only while Options is open (or its state is unknown and nothing else ever worked, with back-off).
//  - Tab button: clone of the first vanilla TabButton, added as LAST sibling only (vanilla tab indexes stay unchanged),
//    never registered in TabsNavigation (no InitTab / OnTabSelected). Its _tabPrefab is an empty inactive object of ours.
//  - Panel: clone of the whole vanilla scroll view (content emptied while inactive), rows cloned from the screen's own prefabs
//    with every game script removed before they ever wake up. Vanilla content is hidden with CanvasGroups, never deactivated,
//    so vanilla OnActiveTab can keep toggling its own objects. The vanilla tabs are never Deselect()ed (visual only).
//  - Clicks: the mod reads the mouse itself (press then release over the same widget; a click on a cloned native widget has
//    never been observed in this game); vanilla callbacks (TabButton.OnSelected, Button.onClick) are a secondary path that is
//    ignored once the mouse path has worked. A click is refused when a game popup or a game button covers the widget.
//  - Rows: the player's own choices and the cheats. "Mission" holds the choice rows ("Heure de la mission", HeureDuJour.cs, "Durée des
//    cratères", Decor.cs, and "Durée des épaves", Epaves.cs, which is built but withheld while it only measures):
//    a Pref row cycles through named values of a MelonPreferences entry, with its label, its value names
//    and its description in the game's language, the description coming from the module and rebuilt only when the module's state or the
//    chosen value changed.
//    Then "Triche" (one row per Cheats kind 0..Cheats.KindCount-1; label, description and key come from the UI-agnostic Cheats API:
//    toggles and one-shots, immediate actions, never part of the game's Apply/Reset).
//    Every mod FEATURE is always on and has no row: only the cheats are switches, a choice row is a player's choice, not a switch.
//    Below the cheats, an "À propos" block: one info row (name, version, author) and two link rows (Steam, Discord) opened with
//    Application.OpenURL.
//  - Texts: every label, state word and description comes from Txt.cs in the game's language and is rewritten when that language
//    changes (Txt.Epoch, checked at each refresh); the log keeps the French names.
//  - Safety: own error counter; a missing game member (game update) or 20 errors in 60 s switch the tab off for the session.
//  Nothing is ever written to the game's SettingsService or PlayerPrefs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Settings;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Settings.Controls;
using Il2CppBrokenArrow.Client.Ecs.UI.Menu.Profile.TabNavigation;
using SettingsService = Il2CppBrokenArrow.Client.Ecs.GameSettings.Services.SettingsService;
using SettingCategory = Il2CppBrokenArrow.Client.Ecs.GameSettings.Enums.SettingCategory;
using UiPopupDialogue = Il2CppBrokenArrow.Client.Ecs.UI.BaseElements.Popup.UiPopupDialogue;
using GO = UnityEngine.GameObject;
using RT = UnityEngine.RectTransform;
using TMP = Il2CppTMPro.TMP_Text;
using UButton = UnityEngine.UI.Button;
using USlider = UnityEngine.UI.Slider;
using UToggle = UnityEngine.UI.Toggle;
using UScrollbar = UnityEngine.UI.Scrollbar;
using USelectable = UnityEngine.UI.Selectable;
using UScrollRect = UnityEngine.UI.ScrollRect;
using CanvasGroup = UnityEngine.CanvasGroup;
using EventSystem = UnityEngine.EventSystems.EventSystem;
using EventTrigger = UnityEngine.EventSystems.EventTrigger;
using PointerEventData = UnityEngine.EventSystems.PointerEventData;
using RaycastResult = UnityEngine.EventSystems.RaycastResult;

namespace RealismOverhaul
{
    /// Stores the settings screen as soon as the game initialises it (cheap, no UI work here).
    [HarmonyPatch(typeof(SettingsScreen), nameof(SettingsScreen.OnInitialize))]
    static class Patch_ModTabInit
    {
        /// Harmony calls this before patching the class, i.e. from Mod.ApplyPatches during OnInitializeMelon: the only startup-time
        /// entry point ModTab.cs owns. The wreck-duration and crater-duration preferences are created here so they exist long before the
        /// Options window is built (their rows need them) and so a value saved in a previous session is read back. Never returns false
        /// and never throws: a failure here would disable the tab's own postfix, and one module failing must not take the other with it.
        static bool Prepare()
        {
            try { Epaves.CreatePrefs(); }
            catch (Exception e) { try { Mod.Log.Warning("[EPAVES] réglage non créé : " + e.GetBaseException().Message); } catch { } }
            try { Decor.CreatePrefs(); }
            catch (Exception e) { try { Mod.Log.Warning("[DECOR] réglage non créé : " + e.GetBaseException().Message); } catch { } }
            return true;
        }

        static void Postfix(SettingsScreen __instance)
        {
            try { ModTab.Note(__instance, "postfix OnInitialize"); } catch { }
        }
    }

    static class ModTab
    {
        static void Log(string s) => Mod.Log.Msg("[ONGLET] " + s);

        // ------------------------------------------------------------ safety latch (own counter, Guard cannot report back)
        static bool _disabled;
        static int _errCount;
        static float _errWindow;

        // ------------------------------------------------------------ screen tracking
        static SettingsScreen _pending;
        static string _pendingPath;
        static float _pendingAt;
        static int _pendingFrame;

        static SettingsScreen _screen;
        static IntPtr _screenPtr;
        static bool _wasActive, _proven, _findLogged, _dumped, _scriptsLogged, _triggersLogged, _vanillaClickLogged;
        static readonly HashSet<IntPtr> _failed = new();
        static readonly List<SettingsScreen> _failedHeld = new();       // held references: a failed screen's address is never reused
        static readonly Dictionary<IntPtr, (int n, float at)> _builds = new();
        static readonly HashSet<string> _pathsLogged = new();
        static readonly HashSet<string> _blockLogged = new();
        static float _now, _nextPoll, _nextFind, _nextLive, _nextStatic, _nextWatch;
        static float _findDelay = 2f;

        // ------------------------------------------------------------ objects built for the current screen
        static TabsNavigation _nav;
        static GO _container;                                           // parent of the vanilla tab buttons (_tabsContainer or found)
        static GO _holder, _tabGo, _scrollGo, _descGo, _emptyPage;
        static TabButton _tab;
        static RT _tabRt;
        static IntPtr _tabPtr;
        static Il2CppSystem.Action<TabButton> _onSel;
        static int _ourIdx = -1;
        static readonly List<TabButton> _vanillaTabs = new();
        static readonly List<RT> _vanillaRts = new();
        static UScrollRect _scroll, _vanillaScroll;
        static RT _content, _viewport;
        static TMP _descTitle, _descBody;
        static Il2CppSystem.Action<int> _tabEvent;
        static TabButton _prevTab, _lastVanillaStatic;
        static UnityEngine.Canvas _canvas;
        static bool _shown;
        static float _tabLastClick;
        static Row _hover;
        static TabButton _vanillaClicked;
        static float _vanillaClickedAt;
        static Row _cheatTitle;
        static bool _valueModeLogged;

        // ------------------------------------------------------------ mouse state (press then release over the same widget)
        static bool _mouseProven, _pressTab;
        static int _pressVanilla = -1;
        static Row _pressRow;
        static PointerEventData _ped;
        static IntPtr _pedEs;
        static Il2CppSystem.Collections.Generic.List<RaycastResult> _hits;

        sealed class Hidden { public CanvasGroup Cg; public bool Added; public float Alpha; public bool Blocks, Inter; }
        static readonly List<Hidden> _hidden = new();
        static readonly List<CanvasGroup> _toDestroy = new();          // CanvasGroups the mod added, removed once Mod is left

        enum Kind { Title, Pref, Cheat, Info, Link }

        sealed class Row
        {
            public Kind K;
            public string Label, Desc, Cat, Name;
            public string LogName;                              // French name for the log (Title, Info and Link rows; cheat rows compute theirs)
            public TxtKey TitleKey;                             // Title and Pref rows: text key of the label
            public TxtKey DescKey;                              // Link rows: text key of the description
            public string Url;                                  // Link rows only
            public List<(object V, string T)> Choices;
            public (string V, TxtKey K)[] ChoiceKeys;           // Pref rows: text key of each value (relabelled with the game's language)
            public Func<Lang, string> DescFn;                   // Pref rows whose description follows what the mod answered
            public Func<int> DescVer;                           // that description is rebuilt only when this number changes
            public int DescEpoch = int.MinValue;
            public int Cheat;                                   // Cheats kind (0..Cheats.KindCount-1); 0..3 toggles, 4..6 one-shots
            public GO Go, DisabledBg;
            public RT Rt;
            public TMP NameText, ValueText;
            public readonly List<UButton> Buttons = new();
            public CanvasGroup Dim;
            public bool Fallback;                               // built from the title prefab: "label : value" in one text
            public bool Inline;                                 // item row without its own value control: the value is written after the label
            public float LastClick, LastMouse;
        }
        static readonly List<Row> _rows = new();

        // cached delegates: no allocation per frame
        static readonly Action _aWatch = Watch, _aDetect = Detect, _aBuild = TryBuild, _aMouse = PollMouse, _aLeave = CheckLeave,
                               _aRefresh = RefreshAll, _aFix = FixVanillaHighlight, _aStatic = TrackStatic, _aCleanup = Cleanup;
        // the two choice modules that run from this frame: cached so Perf.Run costs no allocation per frame
        static readonly Action _aEpaves = Epaves.Tick, _aDecor = Decor.Tick;

        // ============================================================ entry point (Mod.OnUpdate, before the Actif check)
        internal static void Frame()
        {
            if (Mod.AntiCheatActive || Identite.Blocked) return;
            // the wreck duration and the crater duration are the player's choices, not pieces of the tab: they keep working (and keep
            // giving the game its own values back) even after 20 errors have switched the tab itself off. Each throttles itself to one
            // pass every 2 s and neither ever throws.
            // Timed like every other module: they were the only two mod calls of a frame that never reached the [PERF] line, so a
            // regression in one of them was invisible in the very line the author reads to decide whether the mod costs anything.
            Perf.Run("Epaves", _aEpaves);
            Perf.Run("Decor", _aDecor);
            if (_disabled) return;
            try { FrameBody(); }
            catch (Exception e) { Fail("ModTab.Image", e); }
        }

        static void FrameBody()
        {
            _now = UnityEngine.Time.realtimeSinceStartup;
            if (!_shown && _toDestroy.Count > 0) Safe("ModTab.Nettoyage", _aCleanup);

            // while the options screen is closed (a whole battle), its two state reads are enough every 0.25 s: reopening is seen by the
            // same poll as the tab detection. While it is open, the watch stays on every frame (closing must be seen at once).
            if (!ReferenceEquals(_screen, null) && (_wasActive || _now >= _nextWatch)) { if (!_wasActive) _nextWatch = _now + 0.25f; Safe("ModTab.Surveillance", _aWatch); }
            if (_pending is null && (ReferenceEquals(_screen, null) || !_wasActive)) Safe("ModTab.Detection", _aDetect);
            if (!(_pending is null)) Safe("ModTab.Construction", _aBuild);

            if (!ReferenceEquals(_screen, null) && _wasActive && _tabGo != null)
            {
                Safe("ModTab.Souris", _aMouse);
                if (_shown) Safe("ModTab.Sortie", _aLeave);
                if (_shown && _now >= _nextLive) { _nextLive = _now + 0.5f; Safe("ModTab.Valeurs", _aRefresh); }
                if (!(_vanillaClicked is null)) Safe("ModTab.OngletJeu", _aFix);
                if (!_shown && _now >= _nextStatic) { _nextStatic = _now + 0.25f; Safe("ModTab.Statique", _aStatic); }
            }
        }

        static void Safe(string what, Action a)
        {
            if (_disabled) return;
            try { a(); }
            catch (Exception e) { Fail(what, e); }
        }

        static bool IsFatal(Exception e)
        {
            var b = e.GetBaseException();
            return e is MissingMemberException || e is TypeLoadException || e is TypeInitializationException
                || b is MissingMemberException || b is TypeLoadException;
        }

        static void Fail(string what, Exception e)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _errWindow > 60f) { _errWindow = now; _errCount = 0; }
            _errCount++;
            bool fatal = IsFatal(e);
            if (_errCount <= 3 || fatal) Mod.Log.Error($"[ONGLET] {what}: {e}");
            if (fatal) Disable("membre du jeu introuvable, mise à jour du jeu ?");
            else if (_errCount >= 20) Disable("20 erreurs en moins d'une minute");
        }

        static void Disable(string why)
        {
            if (_disabled) return;
            _disabled = true;
            Mod.Log.Error("[ONGLET] onglet Mod coupé jusqu'au prochain lancement du jeu (" + why + ") ; les options du jeu marchent normalement");
            try { if (!ReferenceEquals(_screen, null)) Forget("coupure de l'onglet", _screen != null); } catch { }
            _pending = null;
        }

        // ============================================================ detection
        /// Called by the postfix and by the polls: remembers a screen to build on 2 frames later.
        internal static void Note(SettingsScreen s, string path)
        {
            if (_disabled || s == null) return;
            IntPtr p = s.Pointer;
            if (_failed.Contains(p)) return;
            _findDelay = 2f;
            if (!ReferenceEquals(_screen, null) && _screenPtr == p && _tabGo != null) return;
            if (!(_pending is null) && _pending.Pointer == p) return;
            _pending = s;
            _pendingPath = path;
            _pendingAt = UnityEngine.Time.realtimeSinceStartup;
            _pendingFrame = UnityEngine.Time.frameCount;
        }

        static void Detect()
        {
            if (_now < _nextPoll) return;
            _nextPoll = _now + 0.25f;

            // b) the static "last selected main tab" (set by every vanilla tab bar)
            var last = TabButton._lastMainSelectedButton;
            if (last != null && !IsOurTab(last) && last.gameObject.activeInHierarchy)
            {
                var s = last.GetComponentInParent<SettingsScreen>();
                if (s != null && (ReferenceEquals(_screen, null) || s.Pointer != _screenPtr || _tabGo == null))
                {
                    Note(s, "TabButton._lastMainSelectedButton");
                    return;
                }
            }

            // c) last resort, only while Options is open (every 2 s), or while its state is unknown and nothing else ever worked
            //    (back-off 2, 4, 8 ... 60 s: no whole-scene search every 2 s during battles)
            if (_now < _nextFind) return;
            bool? opened = null;
            try { var svc = Mod.Svc<SettingsService>(); if (svc != null) opened = svc.UiSettingsOpened; } catch { }
            if (opened == false || (opened == null && _proven)) { _nextFind = _now + 2f; return; }
            if (opened == null && !_findLogged) { _findLogged = true; Log("service des réglages introuvable : recherche de secours de plus en plus espacée (2 s à 60 s) tant qu'aucune autre détection n'a marché"); }
            var o = UnityEngine.Object.FindFirstObjectByType(Il2CppType.Of<SettingsScreen>());
            var found = o != null ? o.TryCast<SettingsScreen>() : null;
            if (found != null && found.gameObject.activeInHierarchy && (ReferenceEquals(_screen, null) || found.Pointer != _screenPtr || _tabGo == null))
                Note(found, opened == true ? "recherche (options ouvertes)" : "recherche (état inconnu)");
            if (opened == true) _nextFind = _now + 2f;
            else { _nextFind = _now + _findDelay; _findDelay = Math.Min(60f, _findDelay * 2f); }
        }

        static void TryBuild()
        {
            var s = _pending;
            if (s == null) { _pending = null; return; }                          // destroyed before we could build
            if (!s.gameObject.activeInHierarchy)
            {
                if (_now - _pendingAt > 5f) _pending = null;                     // opened later: the polls will find it again
                return;
            }
            var nav = s._tabsNavigation;
            bool ready = nav != null && (nav._defaultTabWasSet || _now - _pendingAt >= 0.3f);
            if (!ready || UnityEngine.Time.frameCount - _pendingFrame < 2) return;

            string path = _pendingPath;
            _pending = null;
            IntPtr p = s.Pointer;
            if (_failed.Contains(p)) return;
            if (!ReferenceEquals(_screen, null)) Forget(_screenPtr == p ? "reconstruction" : "nouvel écran d'options", _screen != null);

            // only rebuild loops count: a rebuild less than 5 s after the previous one on the same screen (reset when Options reopens)
            _builds.TryGetValue(p, out var b);
            int n = b.n > 0 && _now - b.at < 5f ? b.n + 1 : 1;
            _builds[p] = (n, _now);
            if (n > 3) { Latch(s, "reconstructions en boucle sur le même écran"); return; }

            _proven = true;
            if (_pathsLogged.Add(path)) Log("écran des options détecté via " + path);
            _screen = s;
            _screenPtr = p;
            _wasActive = true;
            try
            {
                Build(s);
                Log($"onglet Mod ajouté (détection : {path}, {_rows.Count(r => r.K == Kind.Pref || r.K == Kind.Cheat)} réglage(s))");
            }
            catch (Exception e)
            {
                Mod.Log.Error("[ONGLET] l'onglet Mod n'a pas pu être ajouté ; les options du jeu marchent normalement : " + e);
                Forget("échec de construction", true);
                Latch(s, "échec de construction");
                if (IsFatal(e)) Disable("membre du jeu introuvable pendant la construction, mise à jour du jeu ?");
            }
        }

        static void Latch(SettingsScreen s, string why)
        {
            if (s == null) return;
            if (_failed.Add(s.Pointer)) { _failedHeld.Add(s); Log("écran abandonné (" + why + ")"); }
            if (_failedHeld.Count > 20) _failedHeld.RemoveAt(0);
        }

        // ============================================================ screen life
        static void Watch()
        {
            if (_screen == null) { Forget("écran des options détruit", false); return; }
            bool active = _screen.gameObject.activeInHierarchy;
            if (!active && _wasActive)
            {
                if (_shown) HideMod("options fermées", true);
                ClearStaticIfOurs();
                _pressTab = false; _pressVanilla = -1; _pressRow = null; _vanillaClicked = null;
                Log("options fermées");
            }
            else if (active && !_wasActive)
            {
                Log("options rouvertes");
                _builds.Remove(_screenPtr);
                _lastVanillaStatic = null;
                if (_tabGo != null)
                {
                    ReadVanillaTabs();                                          // the game may have recreated its tab buttons
                    CheckTabDelegate("réouverture");
                }
            }
            _wasActive = active;
            if (active && _tabGo == null) Forget("onglet Mod disparu", true);    // rebuilt by the polls
        }

        /// Drops every reference; when the screen is still alive our objects are destroyed and the vanilla state restored.
        static void Forget(string reason, bool screenAlive)
        {
            try { if (_shown) HideMod(reason, true); } catch { }
            _shown = false;
            ClearStaticIfOurs();
            try { if (_nav != null && _tabEvent != null) _nav.remove_ActiveTabEvent(_tabEvent); } catch { }
            if (screenAlive)
            {
                foreach (var g in new[] { _tabGo, _scrollGo, _descGo, _holder })
                    try { if (g != null) { g.SetActive(false); g.name = "RealismOverhaul_ancien"; UnityEngine.Object.Destroy(g); } } catch { }
                foreach (var cg in _toDestroy) try { if (cg != null) UnityEngine.Object.Destroy(cg); } catch { }
            }
            else _builds.Remove(_screenPtr);                                  // destroyed screen: its address may be reused by a new one
            _toDestroy.Clear();
            _rows.Clear();
            _hidden.Clear();
            _vanillaTabs.Clear();
            _vanillaRts.Clear();
            _nav = null; _container = null; _holder = null; _tabGo = null; _scrollGo = null; _descGo = null; _emptyPage = null; _tab = null; _tabRt = null; _tabPtr = IntPtr.Zero; _onSel = null;
            _scroll = null; _vanillaScroll = null; _content = null; _viewport = null; _descTitle = null; _descBody = null;
            _tabEvent = null; _prevTab = null; _lastVanillaStatic = null; _canvas = null; _hover = null; _vanillaClicked = null; _cheatTitle = null; _ourIdx = -1;
            _pressTab = false; _pressVanilla = -1; _pressRow = null;
            _screen = null; _screenPtr = IntPtr.Zero; _wasActive = false;
            Log("références libérées (" + reason + ")");
        }

        static bool IsOurTab(TabButton t) => !ReferenceEquals(t, null) && _tabPtr != IntPtr.Zero && t.Pointer == _tabPtr;

        /// The static is shared with the end-of-battle scores/results tabs and the profile: never leave it on our clone.
        static void ClearStaticIfOurs()
        {
            try
            {
                var last = TabButton._lastMainSelectedButton;
                if (!IsOurTab(last)) return;
                TabButton._lastMainSelectedButton = _prevTab != null ? _prevTab : null;
            }
            catch { }
        }

        /// While Mod is not shown: remembers the vanilla tab the game selected last (used when the game's click on our clone came first).
        static void TrackStatic()
        {
            var last = TabButton._lastMainSelectedButton;
            if (last == null || IsOurTab(last)) return;
            IntPtr p = last.Pointer;
            foreach (var tb in _vanillaTabs) if (!ReferenceEquals(tb, null) && tb.Pointer == p) { _lastVanillaStatic = tb; return; }
        }

        static void ReadVanillaTabs()
        {
            _vanillaTabs.Clear();
            _vanillaRts.Clear();
            var container = _container;
            if (container == null) return;
            foreach (var tb in container.GetComponentsInChildren<TabButton>(true))
                if (tb != null && !tb._isSubTab && !IsOurTab(tb) && !tb.gameObject.name.StartsWith("RealismOverhaul_", StringComparison.Ordinal))
                {
                    _vanillaTabs.Add(tb);
                    _vanillaRts.Add(tb.transform.TryCast<RT>());
                }
        }

        /// The settings prefab leaves TabsNavigation._tabsContainer empty (seen in 1.2.0.3): then the parent holding the most main tab buttons.
        static GO FindContainer(SettingsScreen s)
        {
            GO direct = null;
            try { direct = _nav._tabsContainer; } catch { }
            if (direct != null) return direct;
            var found = FindTabParent(_nav.GetComponentsInChildren<TabButton>(true)) ?? FindTabParent(s.GetComponentsInChildren<TabButton>(true));
            if (found != null) Log($"_tabsContainer vide : onglets du jeu trouvés sous '{found.name}'");
            return found;
        }

        static GO FindTabParent(IEnumerable<TabButton> tabs)
        {
            var counts = new Dictionary<IntPtr, (UnityEngine.Transform t, int n)>();
            foreach (var tb in tabs)
            {
                if (tb == null || tb._isSubTab || tb.gameObject.name.StartsWith("RealismOverhaul_", StringComparison.Ordinal)) continue;
                var p = tb.transform.parent;
                if (p == null) continue;
                counts.TryGetValue(p.Pointer, out var c);
                counts[p.Pointer] = (p, c.n + 1);
            }
            return counts.Count == 0 ? null : counts.Values.OrderByDescending(c => c.n).First().t.gameObject;
        }

        /// TabsNavigation could register our clone (Start / InitTab) and replace OnSelected: put ours back and say so.
        static void CheckTabDelegate(string when)
        {
            if (_tab == null || _onSel == null) return;
            var cur = _tab.OnSelected;
            if (cur != null && cur.Pointer == _onSel.Pointer) return;
            Log($"rappel de l'onglet Mod remplacé par le jeu ({when}, {(cur == null ? "vide" : "autre délégué")}) : remis");
            _tab.OnSelected = _onSel;
        }

        // ============================================================ build
        static void Build(SettingsScreen s)
        {
            _nav = s._tabsNavigation;
            if (_nav == null) throw new InvalidOperationException("_tabsNavigation introuvable");
            var container = _container = FindContainer(s);
            if (container == null) throw new InvalidOperationException("aucun conteneur d'onglets (ni _tabsContainer ni TabButton sous l'écran)");
            var vScroll = s._contentScroll;
            if (vScroll == null) throw new InvalidOperationException("_contentScroll introuvable");

            RemoveLeftovers(s);

            ReadVanillaTabs();
            if (_vanillaTabs.Count == 0) throw new InvalidOperationException("aucun TabButton dans _tabsContainer");
            var tpl = _vanillaTabs[0];

            if (!_dumped) { _dumped = true; Guard.Run("ModTab.Diagnostic", () => Diagnose(s, container, tpl, vScroll)); }
            else Log($"onglets du jeu : {string.Join(", ", _vanillaTabs.Select(t => $"'{t.gameObject.name}' rang={t.transform.GetSiblingIndex()}"))} ; _activeTabIdx={s._activeTabIdx}");

            _holder = new GO("RealismOverhaul_Hold");
            _holder.AddComponent(Il2CppType.Of<RT>());
            _holder.transform.SetParent(s.transform, false);
            _holder.SetActive(false);                                          // clones made under it never wake up half-configured

            BuildTab(tpl);
            BuildScroll(s, vScroll);
            BuildDescription(s);
            BuildRows(s);

            _tabEvent = (Il2CppSystem.Action<int>)new Action<int>(OnVanillaTab);
            _nav.add_ActiveTabEvent(_tabEvent);
        }

        /// Our own objects from an earlier build on this same screen (one tab only per screen instance).
        static void RemoveLeftovers(SettingsScreen s)
        {
            int n = 0;
            foreach (var t in s.GetComponentsInChildren<UnityEngine.Transform>(true))
            {
                if (t == null || !t.gameObject.name.StartsWith("RealismOverhaul_", StringComparison.Ordinal)) continue;
                try
                {
                    var tb = t.GetComponent<TabButton>();
                    if (tb != null && !ReferenceEquals(TabButton._lastMainSelectedButton, null) && TabButton._lastMainSelectedButton.Pointer == tb.Pointer)
                        TabButton._lastMainSelectedButton = null;
                    t.gameObject.SetActive(false);
                    t.gameObject.name = "RealismOverhaul_ancien";
                    UnityEngine.Object.Destroy(t.gameObject);
                    n++;
                }
                catch { }
            }
            if (n > 0) Log($"{n} ancien(s) objet(s) du mod retiré(s) de cet écran");
        }

        static void OnTabSelectedByGame(TabButton _) => Safe("ModTab.RappelOnglet", () => ClickTab("rappel du jeu"));

        static void BuildTab(TabButton tpl)
        {
            var go = UnityEngine.Object.Instantiate<GO>(tpl.gameObject, _holder.transform, false);
            go.name = "RealismOverhaul_TabMod";
            var tab = go.GetComponent<TabButton>();
            if (tab == null) throw new InvalidOperationException("le clone d'onglet n'a pas de TabButton");
            Strip(go, tab.Pointer);                                            // TextTarget & co removed: our label stays
            tab._isDefaultTab = false;
            // never null: the game's Select/Deselect on our clone (static swap) may use it without a null check;
            // an empty object under the inactive holder can be switched on and off without showing anything
            _emptyPage = new GO("RealismOverhaul_PageVide");
            _emptyPage.transform.SetParent(_holder.transform, false);
            _emptyPage.SetActive(false);
            tab._tabPrefab = _emptyPage;
            _onSel = (Il2CppSystem.Action<TabButton>)new Action<TabButton>(OnTabSelectedByGame);
            tab.OnSelected = _onSel;
            foreach (var t in go.GetComponentsInChildren<TMP>(true)) if (t != null) t.text = Txt.T(TxtKey.TAB_MOD);
            WireButtons(go, null, () => Safe("ModTab.RappelBouton", () => ClickTab("bouton")));
            _tab = tab;
            _tabPtr = tab.Pointer;
            _tabGo = go;
            _tabRt = go.transform.TryCast<RT>();
            SetTabVisual(tab, false);
            go.transform.SetParent(tpl.transform.parent, false);
            go.transform.SetAsLastSibling();                                   // LAST only: vanilla indexes (tab -> category) unchanged
            _ourIdx = _vanillaTabs.Count;
            _canvas = go.GetComponentInParent<UnityEngine.Canvas>();
        }

        static void BuildScroll(SettingsScreen s, UScrollRect vScroll)
        {
            _vanillaScroll = vScroll;
            var src = vScroll.gameObject;
            var go = UnityEngine.Object.Instantiate<GO>(src, _holder.transform, false);
            go.name = "RealismOverhaul_ModScroll";
            var sr = go.GetComponent<UScrollRect>();
            if (sr == null || sr.content == null) throw new InvalidOperationException("le clone de la zone de défilement n'a pas de contenu");
            var content = sr.content;
            for (int i = content.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject);
            // vanilla objects that may live inside the scroll view: never duplicated
            foreach (var other in new[] { s._subTabsContent, s._propertyDescriptionContainer, s._resetSettingsButton != null ? s._resetSettingsButton.gameObject : null,
                                          s._applyButton != null ? s._applyButton.gameObject : null, s._backButton != null ? s._backButton.gameObject : null })
            {
                string rel = RelPath(src.transform, other != null ? other.transform : null);
                if (rel == null) continue;
                var dup = go.transform.Find(rel);
                if (dup != null) { UnityEngine.Object.DestroyImmediate(dup.gameObject); Log("copie retirée de la zone de défilement : " + rel); }
            }
            Strip(go, IntPtr.Zero);
            sr.onValueChanged = new UScrollRect.ScrollRectEvent();             // vanilla OnScrollValueChanged never sees our rows
            sr.scrollSensitivity = 0f;                                          // wheel handled by the mod: one path only
            WireButtons(go, null, null);                                        // also sliders, toggles, scrollbars, EventTriggers
            go.SetActive(false);                                                // BEFORE leaving the inactive holder
            go.transform.SetParent(src.transform.parent, false);
            go.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);
            IgnoreLayout(go);                                                   // keeps the vanilla rect even if the parent has a layout group
            _scrollGo = go;
            _scroll = sr;
            _content = content;
            _viewport = sr.viewport != null ? sr.viewport : go.transform.Cast<RT>();
            if (content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>() == null)
            {
                var vlg = content.gameObject.AddComponent(Il2CppType.Of<UnityEngine.UI.VerticalLayoutGroup>()).Cast<UnityEngine.UI.VerticalLayoutGroup>();
                vlg.childControlHeight = false; vlg.childForceExpandHeight = false;
                vlg.childControlWidth = true; vlg.childForceExpandWidth = true;
                vlg.spacing = 4f;
                if (content.GetComponent<UnityEngine.UI.ContentSizeFitter>() == null)
                {
                    var fit = content.gameObject.AddComponent(Il2CppType.Of<UnityEngine.UI.ContentSizeFitter>()).Cast<UnityEngine.UI.ContentSizeFitter>();
                    fit.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                }
                Log("le contenu n'avait pas de mise en page verticale : ajoutée");
            }
        }

        static void BuildDescription(SettingsScreen s)
        {
            var container = s._propertyDescriptionContainer;
            var prefab = s._propertyDescriptionPrefab;
            if (container == null || prefab == null) { Log("pas de panneau de description (conteneur ou modèle absent)"); return; }
            var go = UnityEngine.Object.Instantiate<GO>(prefab, _holder.transform, false);
            go.name = "RealismOverhaul_ModDesc";
            var d = go.GetComponent<SettingsElementDescription>() ?? go.GetComponentInChildren<SettingsElementDescription>(true);
            TMP title = null, body = null;
            if (d != null)
            {
                title = d._title; body = d._description;
                try { if (d._imagesBlock != null) d._imagesBlock.SetActive(false); } catch { }
            }
            if (title == null || body == null)
            {
                var texts = go.GetComponentsInChildren<TMP>(true).Where(t => t != null).ToList();
                title ??= texts.FirstOrDefault();
                body ??= texts.Skip(1).FirstOrDefault();
            }
            Strip(go, IntPtr.Zero);
            WireButtons(go, null, null);
            go.SetActive(false);
            go.transform.SetParent(container.transform, false);
            go.transform.SetAsLastSibling();
            // same rect as the vanilla description when there is one, outside any layout group
            UnityEngine.Transform first = null;
            for (int i = 0; i < container.transform.childCount; i++)
            {
                var c = container.transform.GetChild(i);
                if (c != null && c.Pointer != go.transform.Pointer && !c.gameObject.name.StartsWith("RealismOverhaul_", StringComparison.Ordinal)) { first = c; break; }
            }
            var rt = go.transform.TryCast<RT>();
            var frt = first != null ? first.TryCast<RT>() : null;
            if (rt != null)
            {
                if (frt != null) { rt.anchorMin = frt.anchorMin; rt.anchorMax = frt.anchorMax; rt.pivot = frt.pivot; rt.anchoredPosition = frt.anchoredPosition; rt.sizeDelta = frt.sizeDelta; }
                else { rt.anchorMin = UnityEngine.Vector2.zero; rt.anchorMax = UnityEngine.Vector2.one; rt.pivot = new UnityEngine.Vector2(0.5f, 0.5f); rt.anchoredPosition = UnityEngine.Vector2.zero; rt.sizeDelta = UnityEngine.Vector2.zero; }
            }
            IgnoreLayout(go);
            _descGo = go;
            _descTitle = title;
            _descBody = body;
        }

        static void BuildRows(SettingsScreen s)
        {
            _rows.Clear();
            var defs = Defs();
            var titlePrefab = s._settingSubCategoryTitlePrefab;
            var itemPrefab = s._settingItemPrefab;
            if (itemPrefab == null && titlePrefab == null) throw new InvalidOperationException("aucun modèle de ligne (_settingItemPrefab / _settingSubCategoryTitlePrefab)");
            int fallback = 0;
            foreach (var r in defs)
            {
                if (r.K == Kind.Title) MakeTitle(r, titlePrefab ?? itemPrefab);
                else if (!(itemPrefab != null && MakeItem(r, itemPrefab))) { MakeFallback(r, titlePrefab ?? itemPrefab); fallback++; }
                r.Go.transform.SetParent(_content, false);                     // content is under the inactive clone: nothing wakes yet
                r.Rt = r.Go.transform.TryCast<RT>();
                _rows.Add(r);
                if (r.K == Kind.Cheat && _cheatTitle == null) _cheatTitle = _rows.LastOrDefault(x => x.K == Kind.Title);
            }
            if (fallback > 0) Log($"{fallback} ligne(s) construite(s) en mode simple (texte « réglage : valeur » cliquable)");
        }

        static void MakeTitle(Row r, GO prefab)
        {
            var go = UnityEngine.Object.Instantiate<GO>(prefab, _holder.transform, false);
            go.name = "RealismOverhaul_Titre";
            HideControls(go);
            Strip(go, IntPtr.Zero);
            WireButtons(go, null, null);
            var texts = go.GetComponentsInChildren<TMP>(true).Where(t => t != null).ToList();
            if (texts.Count == 0) throw new InvalidOperationException("le modèle de titre n'a pas de texte");
            r.NameText = texts[0];
            for (int i = 1; i < texts.Count; i++) texts[i].text = "";
            r.NameText.text = r.Label;
            r.Go = go;
        }

        /// Row cloned from the vanilla setting row: its dropdown visuals show the value, a click cycles through the values.
        static bool MakeItem(Row r, GO prefab)
        {
            GO go = null;
            try
            {
                go = UnityEngine.Object.Instantiate<GO>(prefab, _holder.transform, false);
                go.name = "RealismOverhaul_Ligne";
                var el = go.GetComponent<SettingsElement>() ?? go.GetComponentInChildren<SettingsElement>(true);
                if (el == null) throw new InvalidOperationException("pas de SettingsElement");
                TMP name = el._name;
                GO dd = el._dropdownControl, sl = el._sliderControl, hk = el._hotkeyControl, dis = el._disabledBg;
                if (name == null) throw new InvalidOperationException("pas de texte de nom");
                // the value controls of this build are separate prefabs created by Init, not children of the row:
                // anything outside the clone is never touched (it is a game asset) and the value is written in the name text instead
                var root = go.transform;
                bool Own(GO g) { try { return g != null && g.transform.IsChildOf(root); } catch { return false; } }
                var dc = Own(dd) ? (dd.GetComponent<DropdownControl>() ?? dd.GetComponentInChildren<DropdownControl>(true)) : null;
                TMP val = dc != null ? dc._currentValue : null;
                GO menu = dc != null ? dc._dropdownMenu : null;
                bool inline = val == null || !val.transform.IsChildOf(root);
                if (Own(sl)) sl.SetActive(false);
                if (Own(hk)) hk.SetActive(false);
                if (!inline) { dd.SetActive(true); if (Own(menu)) menu.SetActive(false); }
                if (Own(dis)) dis.SetActive(false); else dis = null;
                if (!_valueModeLogged)
                {
                    _valueModeLogged = true;
                    Log(inline ? "état des triches écrit dans le nom de chaque ligne (la liste déroulante du jeu n'est pas dans le modèle de ligne)" : "état des triches écrit dans la liste déroulante clonée");
                }
                Strip(go, IntPtr.Zero);                                        // read first, then every game script goes (no Init, no OnDestroy with null services)
                WireButtons(go, r, () => Safe("ModTab.RappelLigne", () => ClickRow(r, "bouton", false)));
                name.richText = true;
                name.text = r.Label;
                r.NameText = name; r.ValueText = inline ? null : val; r.Inline = inline; r.DisabledBg = dis; r.Go = go;
                return true;
            }
            catch (Exception e)
            {
                if (go != null) { try { go.name = "RealismOverhaul_ancien"; UnityEngine.Object.DestroyImmediate(go); } catch { } }
                if (IsFatal(e)) throw;
                Log($"ligne '{LogLabel(r)}' : modèle vanilla inutilisable ({e.Message}), mode simple");
                return false;
            }
        }

        /// Simplest proven widget: a cloned title whose text reads "label : value" and cycles on click.
        static void MakeFallback(Row r, GO prefab)
        {
            var go = UnityEngine.Object.Instantiate<GO>(prefab, _holder.transform, false);
            go.name = "RealismOverhaul_LigneSimple";
            HideControls(go);
            Strip(go, IntPtr.Zero);
            WireButtons(go, r, () => Safe("ModTab.RappelLigne", () => ClickRow(r, "bouton", false)));
            var texts = go.GetComponentsInChildren<TMP>(true).Where(t => t != null).ToList();
            if (texts.Count == 0) throw new InvalidOperationException("le modèle de secours n'a pas de texte");
            r.NameText = texts[0];
            for (int i = 1; i < texts.Count; i++) texts[i].text = "";
            r.Fallback = true;
            r.Go = go;
            if (r.Buttons.Count == 0) { try { r.Dim = go.AddComponent(Il2CppType.Of<CanvasGroup>()).Cast<CanvasGroup>(); } catch { } }
        }

        /// When the item prefab serves as a title / fallback, its value controls must not show.
        static void HideControls(GO go)
        {
            var el = go.GetComponent<SettingsElement>() ?? go.GetComponentInChildren<SettingsElement>(true);
            if (el == null) return;
            foreach (var g in new[] { el._sliderControl, el._dropdownControl, el._hotkeyControl, el._disabledBg }) if (g != null) g.SetActive(false);
        }

        // ============================================================ clone hygiene
        static string TypeName(UnityEngine.Component c) { try { return c.GetIl2CppType().FullName ?? ""; } catch { return ""; } }
        static bool IsGameScript(string n) => n.StartsWith("BrokenArrow", StringComparison.Ordinal) || n.Contains(".Localization.");

        /// Removes every game script from a clone that has never been active (no Awake/OnDestroy ever runs on it).
        static void Strip(GO root, IntPtr keep)
        {
            for (int pass = 0; pass < 3; pass++)                               // several passes: a script another one depends on goes last
            {
                var list = root.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true).Where(m => m != null && m.Pointer != keep && IsGameScript(TypeName(m))).ToList();
                if (list.Count == 0) break;
                if (!_scriptsLogged && pass == 0) { _scriptsLogged = true; Log("scripts du jeu retirés d'un clone : " + string.Join(", ", list.Select(m => TypeName(m)).Distinct())); }
                foreach (var m in list) try { UnityEngine.Object.DestroyImmediate(m); } catch { }
            }
            foreach (var m in root.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true))
                if (m != null && m.Pointer != keep && IsGameScript(TypeName(m)))
                {
                    try { m.enabled = false; } catch { }
                    Log($"script {TypeName(m)} impossible à retirer : désactivé");
                }
        }

        /// Replaces every inherited UI event (no vanilla handler can run: buttons, sliders, toggles, scrollbars),
        /// removes EventTriggers, and adds our click when given. Only called on clones that are still inactive.
        static void WireButtons(GO go, Row r, Action click)
        {
            foreach (var b in go.GetComponentsInChildren<UButton>(true))
            {
                if (b == null) continue;
                b.onClick = new UButton.ButtonClickedEvent();
                if (click != null) b.onClick.AddListener((UnityEngine.Events.UnityAction)click);
                r?.Buttons.Add(b);
            }
            foreach (var sl in go.GetComponentsInChildren<USlider>(true)) if (sl != null) sl.onValueChanged = new USlider.SliderEvent();
            foreach (var tg in go.GetComponentsInChildren<UToggle>(true)) if (tg != null) tg.onValueChanged = new UToggle.ToggleEvent();
            // a ScrollRect adds its own scrollbar listener in OnEnable, after this: scrolling keeps working
            foreach (var sb in go.GetComponentsInChildren<UScrollbar>(true)) if (sb != null) sb.onValueChanged = new UScrollbar.ScrollEvent();
            int n = 0;
            foreach (var et in go.GetComponentsInChildren<EventTrigger>(true))
                if (et != null) { try { UnityEngine.Object.DestroyImmediate(et); n++; } catch { } }
            if (n > 0 && !_triggersLogged) { _triggersLogged = true; Log($"{n} EventTrigger du jeu retiré(s) d'un clone"); }
        }

        static void IgnoreLayout(GO go)
        {
            try
            {
                var le = go.GetComponent<UnityEngine.UI.LayoutElement>();
                if (le == null) le = go.AddComponent(Il2CppType.Of<UnityEngine.UI.LayoutElement>()).Cast<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;
            }
            catch { }
        }

        static string RelPath(UnityEngine.Transform root, UnityEngine.Transform t)
        {
            if (root == null || t == null || t.Pointer == root.Pointer || !t.IsChildOf(root)) return null;
            var parts = new List<string>();
            for (var c = t; c != null && c.Pointer != root.Pointer; c = c.parent) parts.Add(c.gameObject.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string PathOf(UnityEngine.Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null && parts.Count < 6; c = c.parent) parts.Add(c.gameObject.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ============================================================ show / hide
        static void ShowMod()
        {
            if (_shown || _scrollGo == null || ReferenceEquals(_screen, null)) return;
            ReadVanillaTabs();
            var last = TabButton._lastMainSelectedButton;
            if (last != null && !IsOurTab(last)) _prevTab = last;
            else if (_lastVanillaStatic != null) _prevTab = _lastVanillaStatic;   // the game's click on our clone already moved the static
            foreach (var tb in _vanillaTabs) SetTabVisual(tb, false);             // visual only: the game's Deselect() is never called
            CheckTabDelegate("affichage");
            SetTabVisual(_tab, true);
            TabButton._lastMainSelectedButton = _tab;

            _hidden.Clear();
            Hide(_vanillaScroll != null ? _vanillaScroll.gameObject : null);
            Hide(_screen._subTabsContent);
            Hide(_screen._resetSettingsButton != null ? _screen._resetSettingsButton.gameObject : null);
            var dc = _screen._propertyDescriptionContainer;
            if (dc != null)
                for (int i = 0; i < dc.transform.childCount; i++)
                {
                    var c = dc.transform.GetChild(i);
                    if (c != null && (_descGo == null || c.Pointer != _descGo.transform.Pointer)) Hide(c.gameObject);
                }

            _shown = true;
            _hover = null;
            _pressRow = null; _pressVanilla = -1;
            _scrollGo.SetActive(true);
            if (_descGo != null) _descGo.SetActive(true);
            RefreshAll();
            try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_content); } catch { }
            try { _scroll.StopMovement(); _scroll.verticalNormalizedPosition = 1f; } catch { }
            ShowDesc(null);
            Log($"onglet Mod affiché (_activeTabIdx={_screen._activeTabIdx}, onglet précédent '{(_prevTab != null ? _prevTab.gameObject.name : "?")}')");
        }

        /// restorePrev: the screen closes while Mod is shown, so the previously active vanilla tab gets its highlight back.
        static void HideMod(string reason, bool restorePrev)
        {
            if (!_shown) return;
            _shown = false;
            _hover = null;
            _pressRow = null;
            try { if (_scrollGo != null) _scrollGo.SetActive(false); } catch { }
            try { if (_descGo != null) _descGo.SetActive(false); } catch { }
            for (int i = _hidden.Count - 1; i >= 0; i--)
            {
                var h = _hidden[i];
                try
                {
                    if (h.Cg == null) continue;
                    if (h.Added)
                    {
                        // ours: neutral now (a default CanvasGroup changes nothing), removed on the next frame
                        h.Cg.alpha = 1f; h.Cg.blocksRaycasts = true; h.Cg.interactable = true;
                        _toDestroy.Add(h.Cg);
                    }
                    else
                    {
                        // the game's: only the values the mod changed, and only if the game did not change them meanwhile
                        if (h.Cg.alpha == 0f) h.Cg.alpha = h.Alpha;
                        if (!h.Cg.blocksRaycasts) h.Cg.blocksRaycasts = h.Blocks;
                        if (!h.Cg.interactable) h.Cg.interactable = h.Inter;
                    }
                }
                catch { }
            }
            _hidden.Clear();
            SetTabVisual(_tab, false);
            if (restorePrev && _prevTab != null)
            {
                SetTabVisual(_prevTab, true);
                if (IsOurTab(TabButton._lastMainSelectedButton)) TabButton._lastMainSelectedButton = _prevTab;
            }
            Log("onglet Mod quitté (" + reason + ")");
        }

        static void Hide(GO go)
        {
            if (go == null) return;
            var cg = go.GetComponent<CanvasGroup>();
            bool added = false;
            if (cg != null)
            {
                IntPtr p = cg.Pointer;
                int k = _toDestroy.FindIndex(c => !ReferenceEquals(c, null) && c.Pointer == p);
                if (k >= 0) { _toDestroy.RemoveAt(k); added = true; }         // ours from the previous visit, not destroyed yet
            }
            else
            {
                cg = go.AddComponent(Il2CppType.Of<CanvasGroup>()).Cast<CanvasGroup>();
                added = true;
            }
            _hidden.Add(new Hidden { Cg = cg, Added = added, Alpha = cg.alpha, Blocks = cg.blocksRaycasts, Inter = cg.interactable });
            cg.alpha = 0f; cg.blocksRaycasts = false; cg.interactable = false;
        }

        /// Start of a frame, Mod not shown: the CanvasGroups the mod added are removed at once (never inside a game callback).
        static void Cleanup()
        {
            foreach (var cg in _toDestroy) try { if (cg != null) UnityEngine.Object.DestroyImmediate(cg); } catch { }
            _toDestroy.Clear();
        }

        static void SetTabVisual(TabButton tb, bool on)
        {
            if (tb == null) return;
            try { tb.ChangeSelectedColor(on); } catch { }
            try { if (tb._active != null) tb._active.SetActive(on); } catch { }
            try { if (tb._nonActive != null) tb._nonActive.SetActive(!on); } catch { }
        }

        /// Leaving the Mod page: the vanilla tab bar selected another tab (same tab again may raise no ActiveTabEvent).
        static void CheckLeave()
        {
            var last = TabButton._lastMainSelectedButton;
            if (last != null && !IsOurTab(last)) HideMod($"onglet du jeu '{last.gameObject.name}' sélectionné", false);
        }

        static void OnVanillaTab(int idx) => Safe("ModTab.EvenementOnglet", () =>
        {
            var last = TabButton._lastMainSelectedButton;
            string who = last != null ? $"'{last.gameObject.name}' rang={last.transform.GetSiblingIndex()}" : "?";
            Log($"ActiveTabEvent index={idx} _activeTabIdx={(_screen != null ? _screen._activeTabIdx : -1)} onglet={who}");
            if (idx == _ourIdx && IsOurTab(last))
            {
                // the game registered our clone and treated it as its own tab: its TabButton is switched off, the mouse read by the mod stays
                Log("index de l'onglet Mod reçu du jeu : composant TabButton du clone désactivé (la souris lue par le mod reste)");
                try { if (_tab != null) ((UnityEngine.Behaviour)_tab).enabled = false; } catch { }   // UiMonoBehaviour hides Behaviour.enabled with its own property
                return;
            }
            if (last != null && !IsOurTab(last)) TrackStatic();
            if (_shown) HideMod("onglet du jeu n°" + idx, false);
        });

        /// A vanilla tab clicked while Mod was shown: if the game did not re-highlight it after the click, the mod does.
        static void FixVanillaHighlight()
        {
            if (_now - _vanillaClickedAt < 0.6f) return;
            _vanillaClicked = null;
            var last = TabButton._lastMainSelectedButton;
            if (last != null && !IsOurTab(last)) return;
            // the click never reached the game: the content on screen is still the previous vanilla tab's
            var tb = _prevTab;
            if (tb == null) return;
            SetTabVisual(tb, true);
            TabButton._lastMainSelectedButton = tb;
            Log($"surbrillance rendue à l'onglet du jeu '{tb.gameObject.name}' (le clic n'est pas passé par le jeu)");
        }

        // ============================================================ mouse (read by the mod)
        static UnityEngine.Camera Cam()
        {
            if (_canvas == null && _tabGo != null) _canvas = _tabGo.GetComponentInParent<UnityEngine.Canvas>();
            return _canvas != null && _canvas.renderMode != UnityEngine.RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
        }

        static bool Over(RT rt, UnityEngine.Vector2 pos, UnityEngine.Camera cam) =>
            rt != null && UnityEngine.RectTransformUtility.RectangleContainsScreenPoint(rt, pos, cam);

        static void PollMouse()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;
            var left = mouse.leftButton;
            bool press = left.wasPressedThisFrame, release = left.wasReleasedThisFrame;
            if (!_shown && !press && !release) return;                          // nothing to do: no read, no allocation
            var pos = mouse.position.ReadValue();
            var cam = Cam();

            // hover and wheel (Mod page only)
            bool inView = false;
            Row over = null;
            if (_shown)
            {
                inView = Over(_viewport, pos, cam);
                if (inView)
                    foreach (var r in _rows)
                        if (r.K != Kind.Title && Over(r.Rt, pos, cam)) { over = r; break; }
                if (!ReferenceEquals(over, _hover)) { _hover = over; ShowDesc(over); }
                if (inView)
                {
                    float d = mouse.scroll.ReadValue().y;
                    if (Math.Abs(d) > 0.01f && !Blocked(pos, _scrollGo, "molette")) Scroll(d);
                }
            }

            if (press)
            {
                _pressTab = false; _pressVanilla = -1; _pressRow = null;
                if (_tabGo.activeInHierarchy && Over(_tabRt, pos, cam)) _pressTab = !Blocked(pos, _tabGo, "onglet Mod");
                else if (_shown)
                {
                    for (int i = 0; i < _vanillaTabs.Count && i < _vanillaRts.Count; i++)
                        if (_vanillaTabs[i] != null && Over(_vanillaRts[i], pos, cam))
                        {
                            if (!Blocked(pos, _vanillaTabs[i].gameObject, "onglet du jeu")) _pressVanilla = i;
                            break;
                        }
                    if (_pressVanilla < 0 && over != null && !Blocked(pos, _scrollGo, "ligne")) _pressRow = over;
                }
            }

            if (!release) return;
            bool pTab = _pressTab; int pVan = _pressVanilla; Row pRow = _pressRow;
            _pressTab = false; _pressVanilla = -1; _pressRow = null;

            if (pTab)
            {
                if (Over(_tabRt, pos, cam)) ClickTab("souris", true);
                return;
            }
            if (!_shown) return;
            if (pVan >= 0 && pVan < _vanillaTabs.Count && pVan < _vanillaRts.Count)
            {
                var tb = _vanillaTabs[pVan];
                if (tb != null && Over(_vanillaRts[pVan], pos, cam))
                {
                    Log($"clic sur l'onglet du jeu '{tb.gameObject.name}' (souris)");
                    HideMod($"clic sur '{tb.gameObject.name}'", false);
                    _vanillaClicked = tb;
                    _vanillaClickedAt = _now;                                   // delay counted from the release (the game's click)
                }
                return;
            }
            if (pRow != null && ReferenceEquals(over, pRow)) ClickRow(pRow, "souris", true);
        }

        /// True when a game popup or a game button covers the point (a click there belongs to the game, not to the mod).
        /// Only positive evidence blocks: an unknown passive object on top is accepted (logged once), because the game's own
        /// event system never delivered clicks to cloned widgets and the reason is unknown.
        static bool Blocked(UnityEngine.Vector2 pos, GO mine, string what)
        {
            var es = EventSystem.current;
            if (es == null)
            {
                string why = PopupOpen();
                if (why != null) { LogBlock($"{what} : {why}"); return true; }
                return false;
            }
            if (_ped == null || _pedEs != es.Pointer) { _ped = new PointerEventData(es); _pedEs = es.Pointer; }
            _ped.position = pos;
            _hits ??= new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            _hits.Clear();
            es.RaycastAll(_ped, _hits);
            if (_hits.Count == 0) return false;
            var top = _hits[0].gameObject;
            if (top == null) return false;
            var t = top.transform;
            if (mine != null && t.IsChildOf(mine.transform)) return false;
            if (_tabGo != null && t.IsChildOf(_tabGo.transform)) return false;
            if (_scrollGo != null && t.IsChildOf(_scrollGo.transform)) return false;
            string path = PathOf(t);
            if (top.GetComponentInParent<UiPopupDialogue>() != null || top.GetComponentInParent<ColorPickerPopup>() != null)
            { LogBlock($"{what} : fenêtre du jeu au-dessus ({path})"); return true; }
            var popups = !ReferenceEquals(_screen, null) && _screen != null ? _screen._internalPopupsContainer : null;
            if (popups != null && t.IsChildOf(popups)) { LogBlock($"{what} : fenêtre des options au-dessus ({path})"); return true; }
            var sel = top.GetComponentInParent<USelectable>();
            if (sel != null && sel.IsInteractable()) { LogBlock($"{what} : bouton du jeu au-dessus ({path})"); return true; }
            LogBlock($"{what} : objet du jeu au-dessus, clic accepté ({path})");
            return false;
        }

        /// Fallback without an EventSystem: the settings screen's own popups.
        static string PopupOpen()
        {
            var s = _screen;
            if (ReferenceEquals(s, null) || s == null) return null;
            try { var p = s._unsavedChangesPopup; if (p != null && p.gameObject.activeInHierarchy) return "fenêtre « modifications non enregistrées » ouverte"; } catch { }
            try { var p = s._hotkeysPopup; if (p != null && p.gameObject.activeInHierarchy) return "fenêtre des touches ouverte"; } catch { }
            try { var p = s._colorPickerPopup; if (p != null && p.gameObject.activeInHierarchy) return "sélecteur de couleur ouvert"; } catch { }
            return null;
        }

        static void LogBlock(string s)
        {
            if (_blockLogged.Count < 40 && _blockLogged.Add(s)) Log(s);
        }

        static void Scroll(float delta)
        {
            if (_scroll == null || _content == null || _viewport == null) return;
            float range = _content.rect.height - _viewport.rect.height;
            if (range <= 1f) return;
            _scroll.StopMovement();
            _scroll.verticalNormalizedPosition = UnityEngine.Mathf.Clamp01(_scroll.verticalNormalizedPosition + Math.Sign(delta) * 90f / range);
        }

        // ============================================================ clicks
        static void ClickTab(string via, bool fromMouse = false)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _tabLastClick < 0.3f) return;                            // one click, several delivery paths
            _tabLastClick = now;
            if (fromMouse) _mouseProven = true;
            Log($"clic sur l'onglet Mod ({via})");
            if (!_shown) Safe("ModTab.Afficher", ShowMod);
        }

        static void ClickRow(Row r, string via, bool fromMouse)
        {
            if (!_shown) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            // one path per click: once the mouse read by the mod has worked, the game's callbacks are only logged
            if (!fromMouse && (_mouseProven || now - r.LastMouse < 1.5f))
            {
                if (!_vanillaClickLogged) { _vanillaClickLogged = true; Log($"clic transmis aussi par le jeu sur '{LogLabel(r)}' : ignoré (la souris lue par le mod suffit)"); }
                return;
            }
            if (now - r.LastClick < 0.3f) return;
            r.LastClick = now;
            if (fromMouse) { r.LastMouse = now; _mouseProven = true; }
            Log($"clic sur '{LogLabel(r)}' ({via})");
            Safe("ModTab.Clic", () =>
            {
                if (r.K == Kind.Pref) CyclePref(r);
                else if (r.K == Kind.Cheat) ToggleCheat(r);
                else if (r.K == Kind.Link) OpenLink(r);
                RefreshAll();
                ShowDesc(r);
            });
        }

        static void CyclePref(Row r)
        {
            var e = MelonPreferences.GetEntry(r.Cat, r.Name);
            if (e == null) { Log($"réglage {r.Cat}/{r.Name} introuvable"); return; }
            object cur = e.BoxedValue;
            int idx = IndexOf(r, cur);
            var next = r.Choices[(idx + 1) % r.Choices.Count];
            object v = ConvertTo(next.V, cur);
            e.BoxedValue = v;
            MelonPreferences.Save();
            Log($"réglage {r.Cat}/{r.Name} : {Fmt(cur)} -> {Fmt(e.BoxedValue)} (enregistré)");
        }

        /// Cheats API (Mod.cs): Allowed(out reason) fails closed; Activate(kind) toggles a toggle (OFF always accepted) or fires
        /// a one-shot, and returns true when applied. A one-shot is never "on", so it always goes through the gate here.
        static void ToggleCheat(Row r)
        {
            if (!Cheats.IsOn(r.Cheat) && !Cheats.Allowed(out var why)) { Log("triche refusée : " + Txt.Fr(why)); return; }
            bool ok = Cheats.Activate(r.Cheat);
            if (Cheats.IsToggle(r.Cheat))
                Log($"triche '{LogLabel(r)}' -> {(CheatOn(r.Cheat) ? "ACTIVÉE" : "désactivée")}{(ok ? "" : " (refusée ou pas prête)")}");
            else
                Log($"triche '{LogLabel(r)}' -> {(ok ? "faite" : "refusée ou pas prête")}");
        }

        static bool CheatOn(int k) => Cheats.IsOn(k);

        static float _lastLinkOpen = -10f;

        /// Opens the row's web link in the default browser (at most once every 1.5 s, whatever path delivered the click).
        static void OpenLink(Row r)
        {
            if (string.IsNullOrEmpty(r.Url)) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastLinkOpen < 1.5f) return;
            _lastLinkOpen = now;
            UnityEngine.Application.OpenURL(r.Url);
            Log($"lien '{LogLabel(r)}' ouvert : {r.Url}");
        }

        /// Row label and description in a language, rebuilt from the Cheats API (the key name or an amount can change in the prefs).
        static string CheatLabel(int k, Lang l)
        {
            try { string key = Cheats.KeyOf(k); return key.Length == 0 ? Cheats.LabelOf(k, l) : $"{Cheats.LabelOf(k, l)} ({key})"; }
            catch { return Txt.Format(l, TxtKey.MT_CHEAT_FALLBACK, k); }
        }

        static string CheatDesc(int k, Lang l)
        {
            try
            {
                string key = Cheats.KeyOf(k);
                return key.Length == 0 ? Txt.Format(l, TxtKey.MT_CHEAT_DESC_NOKEY, Cheats.DescOf(k, l)) : Txt.Format(l, TxtKey.MT_CHEAT_DESC_KEY, Cheats.DescOf(k, l), key);
            }
            catch { return ""; }
        }

        /// French name of a row for the log, whatever the language shown.
        static string LogLabel(Row r)
        {
            if (r == null) return "?";
            return r.K == Kind.Cheat ? CheatLabel(r.Cheat, Lang.FR) : (r.LogName ?? r.Label);
        }

        // ============================================================ texts in the game's language
        static int _textEpoch = -1;

        /// The game's language changed (Txt.Epoch): tab name, section titles, about and link rows take the new texts.
        /// Cheat rows follow on their own in RefreshAll (label compared every refresh).
        static void Relabel()
        {
            try { if (_tabGo != null) foreach (var t in _tabGo.GetComponentsInChildren<TMP>(true)) if (t != null) t.text = Txt.T(TxtKey.TAB_MOD); } catch { }
            foreach (var r in _rows)
            {
                try
                {
                    if (r.K == Kind.Title)
                    {
                        r.Label = Txt.T(r.TitleKey);
                        if (r.NameText != null) r.NameText.text = r.Label;
                    }
                    else if (r.K == Kind.Pref) PrefTexts(r);
                    else if (r.K == Kind.Info) InfoTexts(r);
                    else if (r.K == Kind.Link) r.Desc = LinkDesc(r);
                }
                catch { }
            }
        }

        /// Choice row: label and value names in the game's language; its description is rebuilt at the next refresh.
        static void PrefTexts(Row r)
        {
            var lang = Txt.Current;
            r.Label = Txt.Get(lang, r.TitleKey);
            if (!r.Fallback && !r.Inline && r.NameText != null) { try { r.NameText.text = r.Label; } catch { } }
            if (r.ChoiceKeys != null && r.Choices != null)
                for (int i = 0; i < r.Choices.Count && i < r.ChoiceKeys.Length; i++) r.Choices[i] = (r.Choices[i].V, Txt.Get(lang, r.ChoiceKeys[i].K));
            r.DescEpoch = int.MinValue;
        }

        static void InfoTexts(Row r)
        {
            string name = Identite.ModName + " " + Identite.Version;
            r.Label = Txt.TF(TxtKey.MT_ABOUT_LABEL, name, Identite.Author);
            r.Desc = Txt.TF(TxtKey.MT_ABOUT_DESC, name, Identite.Author, Identite.SteamUrl, Identite.DiscordUrl);
        }

        static string LinkDesc(Row r) =>
            (r.DescKey == TxtKey.MT_LINK_STEAM_DESC ? Txt.TF(TxtKey.MT_LINK_STEAM_DESC, Identite.Author) : Txt.T(r.DescKey)) + "\n\n" + r.Url;

        // ============================================================ values
        enum St { On, Off, Click, Unavailable, Open, Text }

        static void RefreshAll()
        {
            if (_rows.Count == 0) return;
            int epoch = Txt.Epoch;
            if (_textEpoch != epoch) { _textEpoch = epoch; Relabel(); }
            bool allowed = false;
            try { allowed = Cheats.Allowed(out _); } catch { allowed = false; }
            if (_cheatTitle?.NameText != null) _cheatTitle.NameText.text = Txt.T(allowed ? TxtKey.MT_TITLE_CHEATS : TxtKey.MT_TITLE_CHEATS_SOLO);
            foreach (var r in _rows)
            {
                if (r.K == Kind.Title) continue;
                if (r.K == Kind.Info) { SetInfo(r); continue; }
                if (r.K == Kind.Link) { SetRow(r, St.Open, null, false); continue; }
                St st = St.Text; string text = null; bool grey = false;
                if (r.K == Kind.Pref)
                {
                    object cur = null;
                    try { cur = MelonPreferences.GetEntry(r.Cat, r.Name)?.BoxedValue; } catch { }
                    int i = IndexOf(r, cur);
                    text = i >= 0 ? r.Choices[i].T : Fmt(cur);
                    // description built by the module: rebuilt only when its own state or the chosen value changed
                    if (r.DescFn != null)
                    {
                        int v = 0;
                        try { v = r.DescVer != null ? r.DescVer() : 0; } catch { v = 0; }
                        if (v != r.DescEpoch) { r.DescEpoch = v; try { r.Desc = r.DescFn(Txt.Current); } catch { } }
                    }
                }
                else
                {
                    // a key name, an amount or the game's language changed while this screen existed: label and description follow
                    var lang = Txt.Current;
                    string label = CheatLabel(r.Cheat, lang);
                    if (!string.Equals(label, r.Label, StringComparison.Ordinal))
                    {
                        r.Label = label;
                        if (!r.Fallback && r.NameText != null) { try { r.NameText.text = r.Label; } catch { } }
                    }
                    r.Desc = CheatDesc(r.Cheat, lang);
                    bool on = CheatOn(r.Cheat);
                    if (Cheats.IsToggle(r.Cheat)) st = on ? St.On : allowed ? St.Off : St.Unavailable;
                    else st = allowed ? St.Click : St.Unavailable;
                    grey = !allowed && !on;
                }
                SetRow(r, st, text, grey);
            }
            if (_hover != null) ShowDesc(_hover);
        }

        static string Word(St st)
        {
            switch (st)
            {
                case St.On: return Txt.T(TxtKey.ST_ON);
                case St.Off: return Txt.T(TxtKey.ST_OFF);
                case St.Click: return Txt.T(TxtKey.ST_CLICK);
                case St.Unavailable: return Txt.T(TxtKey.ST_UNAVAILABLE);
                case St.Open: return Txt.T(TxtKey.ST_OPEN);
                default: return "";
            }
        }

        /// text: the value shown for St.Text (preference rows); the state word of the game's language otherwise.
        static void SetRow(Row r, St st, string text, bool grey)
        {
            try
            {
                string value = st == St.Text ? (text ?? "") : Word(st);
                if (r.Fallback || r.Inline) { if (r.NameText != null) { r.NameText.richText = true; r.NameText.text = $"{Colored(st, value)}   {r.Label}"; } }
                else if (r.ValueText != null) r.ValueText.text = value;
                if (r.DisabledBg != null) r.DisabledBg.SetActive(grey);
                foreach (var b in r.Buttons) if (b != null) b.interactable = !grey;
                if (r.DisabledBg == null)
                {
                    if (r.Dim == null && grey) r.Dim = r.Go.AddComponent(Il2CppType.Of<CanvasGroup>()).Cast<CanvasGroup>();
                    if (r.Dim != null) r.Dim.alpha = grey ? 0.45f : 1f;
                }
            }
            catch { }
        }

        /// Info row: the label alone, never a state word; an empty value box when the row kept the dropdown visuals.
        static void SetInfo(Row r)
        {
            try
            {
                if (r.NameText != null) { r.NameText.richText = true; r.NameText.text = r.Label; }
                if (!r.Fallback && !r.Inline && r.ValueText != null) r.ValueText.text = "";
                if (r.DisabledBg != null) r.DisabledBg.SetActive(false);
                if (r.Dim != null) r.Dim.alpha = 1f;
            }
            catch { }
        }

        /// State word in color, in square brackets, shown before the label so a long label can never hide it.
        static string Colored(St st, string value)
        {
            string c = st == St.On ? "#5FD068" : st == St.Off ? "#E0685A" : st == St.Click || st == St.Open ? "#7FB8E6" : "#8C8C8C";
            return $"<b><color={c}>[{value}]</color></b>";
        }

        static void ShowDesc(Row r)
        {
            if (_descGo == null) return;
            string title, body;
            if (r == null)
            {
                title = Txt.T(TxtKey.MT_DEFAULT_TITLE);
                body = Txt.T(TxtKey.MT_DEFAULT_BODY);
            }
            else
            {
                title = r.Label;
                body = r.Desc ?? "";
                if (r.K == Kind.Cheat)
                {
                    string why; bool ok = false;
                    try { ok = Cheats.Allowed(out var reason); why = Txt.T(reason); }
                    catch (Exception e) { why = Txt.TF(TxtKey.MT_STATE_UNREADABLE, e.Message); }
                    if (!ok) body += "\n\n" + Txt.TF(TxtKey.MT_UNAVAILABLE_HERE, why);
                }
            }
            try { if (_descTitle != null) _descTitle.text = title; } catch { }
            try { if (_descBody != null) _descBody.text = body; } catch { }
        }

        static int IndexOf(Row r, object cur)
        {
            if (cur == null || r.Choices == null) return -1;
            for (int i = 0; i < r.Choices.Count; i++) if (Same(r.Choices[i].V, cur)) return i;
            return -1;
        }

        static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (a is string || b is string || a.GetType().IsEnum || b.GetType().IsEnum)
                return string.Equals(a.ToString().Trim(), b.ToString().Trim(), StringComparison.OrdinalIgnoreCase);
            if (a is bool || b is bool) return a.Equals(b);
            try { return Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-4; }
            catch { return a.Equals(b); }
        }

        static object ConvertTo(object v, object like)
        {
            if (like == null) return v;
            var t = like.GetType();
            if (t.IsEnum) return Enum.Parse(t, v.ToString(), true);
            return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }

        static string Fmt(object v) => v == null ? "?" : v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString();

        // ============================================================ row table (texts in the game's language, Txt.cs)
        // Only the cheats are switches; a choice row is a player's choice, not a feature switch. Every mod feature is always on and
        // has no row. Order: the player's choices ("Mission"), then "Triche", then the "À propos" block.
        static List<Row> Defs()
        {
            var l = new List<Row>();
            var lang = Txt.Current;
            void Title(TxtKey key) => l.Add(new Row { K = Kind.Title, TitleKey = key, Label = Txt.T(key), LogName = Txt.Fr(key) });
            void Cheat(int kind) => l.Add(new Row { K = Kind.Cheat, Cheat = kind, Label = CheatLabel(kind, lang), Desc = CheatDesc(kind, lang) });
            void Link(string label, string url, TxtKey desc)
            {
                var r = new Row { K = Kind.Link, Label = label, LogName = label, Url = url, DescKey = desc };
                r.Desc = LinkDesc(r);
                l.Add(r);
            }
            // A row that cycles through named values of a MelonPreferences entry; description and version come from the module that owns it.
            void Choice(string cat, string name, TxtKey label, (string V, TxtKey K)[] values, Func<Lang, string> desc, Func<int> ver)
            {
                bool exists = false;
                try { exists = MelonPreferences.GetEntry(cat, name) != null; } catch { }
                if (!exists || values == null || values.Length == 0) { Log($"réglage {cat}/{name} introuvable : ligne non ajoutée"); return; }
                var r = new Row
                {
                    K = Kind.Pref, Cat = cat, Name = name, TitleKey = label, Label = Txt.Get(lang, label), LogName = Txt.Fr(label),
                    ChoiceKeys = values, DescFn = desc, DescVer = ver, Choices = new List<(object V, string T)>(values.Length),
                };
                foreach (var v in values) r.Choices.Add((v.V, Txt.Get(lang, v.K)));
                try { r.Desc = desc(lang); r.DescEpoch = ver(); } catch { r.Desc = ""; }
                l.Add(r);
            }

            // the player's own choices come first: they are not cheats, and only the cheats are switches
            Title(TxtKey.MT_TITLE_MISSION);
            Choice(HeureDuJour.PrefCat, HeureDuJour.PrefName, TxtKey.HD_LABEL, HeureDuJour.Valeurs, HeureDuJour.RowDesc, () => HeureDuJour.EtatVersion);
            // Wreck duration row withheld from this build (2026-09-18): the module measures only and changes nothing,
            // so a row here would promise the player something that does not happen yet. Restore this single line
            // once a test session has timed a real body and a real hull and Epaves.cs writes again.
            //             Choice(Epaves.PrefCat, Epaves.PrefName, TxtKey.EP_LABEL, Epaves.Valeurs, Epaves.RowDesc, () => Epaves.EtatVersion);
            // Crater duration (2026-09-19): this one DOES write, and writes exactly ONE value - DecalLimitGroupPreset.LifeTime, on every
            // readable group and not only on the craters, which the row says in all five languages. No hook, journaled under its own lot.
            // It deliberately raises NO count - neither MaxCount nor the runtime ObjectPoolGroup._maxTotalObjects - so the number of
            // marks stays inside the budget the studio chose; what it does cost is more marks alive at once inside that budget, and the
            // row states that cost instead of hiding it. It stops at 30 minutes, because a mark is born with the duration then in force
            // and a duration longer than a battle would mean nothing can expire during it. The wrecks, the fire and the buildings the
            // same request asked for do not exist as values in this build; Decor.cs says so in the log instead of shipping a figure that
            // changes nothing. The row says "written", never "applied": that the engine reads this preset when a mark is born is a
            // deduction from the class shapes, and one timed battle is what would settle it.
            Choice(Decor.PrefCat, Decor.PrefName, TxtKey.DC_LABEL, Decor.Valeurs, Decor.RowDesc, () => Decor.EtatVersion);

            Title(TxtKey.MT_TITLE_CHEATS);
            for (int k = 0; k < Cheats.KindCount; k++) Cheat(k);

            Title(TxtKey.MT_TITLE_ABOUT);
            var info = new Row { K = Kind.Info, LogName = Identite.ModName + " " + Identite.Version };
            InfoTexts(info);
            l.Add(info);
            Link("Steam", Identite.SteamUrl, TxtKey.MT_LINK_STEAM_DESC);
            Link("Discord", Identite.DiscordUrl, TxtKey.MT_LINK_DISCORD_DESC);
            _textEpoch = Txt.Epoch;

            // a section title with no row under it is dropped
            for (int i = l.Count - 1; i >= 0; i--)
                if (l[i].K == Kind.Title && (i == l.Count - 1 || l[i + 1].K == Kind.Title)) l.RemoveAt(i);
            return l;
        }

        // ============================================================ diagnostics (once per game launch)
        static void Diagnose(SettingsScreen s, GO container, TabButton tpl, UScrollRect vScroll)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"diagnostic : _activeTabIdx={s._activeTabIdx} _defaultTabWasSet={_nav._defaultTabWasSet} _defaultStartedIndexTab={_nav._defaultStartedIndexTab} _instantiateTabs={_nav._instantiateTabs}");
            foreach (var tb in _vanillaTabs)
            {
                string page = "?";
                try { var tp = tb._tabPrefab; page = tp == null ? "aucune" : $"'{tp.name}' actif={tp.activeSelf}"; } catch { }
                sb.AppendLine($"  onglet '{tb.gameObject.name}' rang={tb.transform.GetSiblingIndex()} parent='{tb.transform.parent?.gameObject.name}' défaut={tb._isDefaultTab} actif={tb.gameObject.activeInHierarchy} _tabPrefab={page}");
            }
            try { sb.AppendLine("  EventSystem : " + (EventSystem.current != null ? "présent" : "absent")); } catch { }
            try { sb.AppendLine("  SettingCategory : " + string.Join(", ", Enum.GetValues(typeof(SettingCategory)).Cast<object>().Select(v => $"{v}={Convert.ToInt32(v)}"))); } catch { }
            try { var svc = Mod.Svc<SettingsService>(); sb.AppendLine($"  SettingsService via Session : {(svc != null ? "trouvé, UiSettingsOpened=" + svc.UiSettingsOpened : "introuvable")}"); } catch (Exception e) { sb.AppendLine("  SettingsService : " + e.Message); }
            Tree(sb, "_tabsContainer", container.transform, 3);
            Tree(sb, "onglet modèle", tpl.transform, 3);
            Tree(sb, "_contentScroll", vScroll.transform, 3);
            Tree(sb, "_settingItemPrefab", s._settingItemPrefab?.transform, 3);
            Tree(sb, "_settingSubCategoryTitlePrefab", s._settingSubCategoryTitlePrefab?.transform, 3);
            Tree(sb, "_propertyDescriptionContainer", s._propertyDescriptionContainer?.transform, 2);
            Tree(sb, "_propertyDescriptionPrefab", s._propertyDescriptionPrefab?.transform, 2);
            Tree(sb, "_subTabsContent", s._subTabsContent?.transform, 1);
            Tree(sb, "_internalPopupsContainer", s._internalPopupsContainer, 1);
            Log(sb.ToString().TrimEnd());
        }

        static void Tree(StringBuilder sb, string label, UnityEngine.Transform t, int depth)
        {
            if (t == null) { sb.AppendLine($"  [{label}] absent"); return; }
            sb.AppendLine($"  [{label}]");
            int lines = 0;
            void Walk(UnityEngine.Transform n, int d)
            {
                if (n == null || lines > 80) return;
                lines++;
                string comps = "";
                try { comps = string.Join(",", n.GetComponents<UnityEngine.Component>().Where(c => c != null).Select(c => { var f = TypeName(c); int k = f.LastIndexOf('.'); return k >= 0 ? f.Substring(k + 1) : f; })); } catch { }
                string txt = "";
                try { var tm = n.GetComponent<TMP>(); if (tm != null) txt = $" texte=\"{tm.text}\""; } catch { }
                sb.AppendLine($"  {new string(' ', 2 * (depth - d + 1))}{n.gameObject.name} (actif={n.gameObject.activeSelf}) [{comps}]{txt}");
                if (d <= 0) return;
                for (int i = 0; i < n.childCount; i++) Walk(n.GetChild(i), d - 1);
            }
            Walk(t, depth);
        }
    }
}
