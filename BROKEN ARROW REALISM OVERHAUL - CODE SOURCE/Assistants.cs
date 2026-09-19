// RealismOverhaul — assistants in the vanilla order panel (campaign only, local player's units).
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppBrokenArrow.Client.Ecs.AI;
using Il2CppBrokenArrow.Client.Ecs.Configs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.FogOfWar.Systems;
using Il2CppBrokenArrow.Client.Ecs.UI.Orders;
using Il2CppBrokenArrow.Client.Ecs.Utils;
using Il2CppBrokenArrow.DataBase;
using Il2CppBrokenArrow.MissionEditor.LuaBridge;
using Il2CppBrokenArrow.ScriptEngine.Data;
using Il2CppBrokenArrow.Shared.Ecs;
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;
using Il2CppBrokenArrow.Shared.Ecs.Services;
using Il2CppNetworkCommon.Enums.Common;
using NetCmdService = Il2CppBrokenArrow.Client.Ecs.GNetwork.Services.NetworkCommandService;
using RevMoveCmd = Il2CppBrokenArrow.Client.Ecs.Commands.ReverseMoveCommand;
using ICmd = Il2CppBrokenArrow.Shared.Ecs.Commands.ICommand;
using MainCmdSys = Il2CppBrokenArrow.Client.Ecs.Commands.Systems.MainCommandSystem;
using HoldFireSys = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Systems.HoldFireSystem;
using V3 = UnityEngine.Vector3;
using RT = UnityEngine.RectTransform;
using UImage = UnityEngine.UI.Image;
using UiElementState = Il2CppBrokenArrow.Client.Ecs.UI.BaseElements.Utils.UiElementState;
using Hints = Il2CppBrokenArrow.Client.Ecs.UI.ButtonHints;
using TMP = Il2CppTMPro.TMP_Text;
using TeamSide = Il2CppNetworkCommon.Enums.TeamSide;
using AiComp = Il2CppBrokenArrow.Client.Ecs.AI.Components;
using AiTargetDetect = Il2CppBrokenArrow.Client.Ecs.AI.Systems.AiTargetDetectionSystem;
using AbilityRow = Il2CppBrokenArrow.DataBase.Models.Abilities;

namespace RealismOverhaul
{
    // ---------------------------------------------------------------- assistants (campagne, joueur local) : options dans le panneau d'ordres vanilla
    static partial class Assistants
    {
        internal static MelonPreferences_Entry<bool> Enabled;
        static MelonPreferences_Entry<bool> _smokeDefault, _longRange, _longRangeValuable, _aiBoost, _retDefault, _retReverse, _debDefault, _ravDefault, _ambDefault, _favSpread;
        static MelonPreferences_Entry<int> _cbCooldown, _cbInterval, _fawCooldown, _minAmmo, _retDistance, _retCooldown, _retReverseDistance, _ambRearm, _debMinDrop, _ravAmmo, _ravHp;
        static MelonPreferences_Entry<int> _favTargetCooldown, _favMaxPerTick, _favUnclean;
        static MelonPreferences_Entry<float> _cbFirstError, _ambFraction, _ravRange;
        static MelonPreferences_Entry<string> _favGuardVersion;
        static MelonPreferences_Entry<bool> _favMigre025;
        // "eyes on the target": where the centre of the sheaf lands depends on who of yours actually watches the area.
        // _rangeMargin belongs to the firing-error budget (fire safety) and works even when the observer rule is off.
        static MelonPreferences_Entry<bool> _eyeOn, _eyeBrake, _eyeSilence;
        static MelonPreferences_Entry<float> _eyeObs, _eyeSeen, _eyeBlind, _eyeZone, _rangeMargin;

        // Order-panel buttons exist for artillery only: CB counter-battery, FAW fire at will.
        // Retreat, dismount, ambush and resupply are global options (prefs, later the Mod options tab), not panel buttons.
        // Artillery is NEVER moved by the mod (it could break scripted missions): CB / FAV only fire from where the player left it.
        // AA: S-400 anti-air switched on (default = missiles only). RIP/DEF/DEBC: stances, RAVX/GAR/RZ: one-click actions (Ordres.cs)
        [Flags] enum Opt { None = 0, CB = 1, FAW = 2, AA = 4, RIP = 8, DEF = 16, DEBC = 32, RAVX = 64, GAR = 128, RZ = 256 }

        const int ROLE_MLRS = 130, ROLE_MORTAR = 131, ROLE_LAM = 132, ROLE_ARTY = 133, ROLE_LRSAM = 15, ROLE_SRSAM = 16, ROLE_AAINF = 34;
        // roles used by the observer rule only (same values as Mod.cs): scouts, snipers, special forces, scout helicopters, drones, planes
        const int ROLE_RECONINF = 32, ROLE_SNIPERS = 33, ROLE_SPECFORCES = 36, ROLE_RECONHELI = 70, ROLE_DRONE = 100, ROLE_PLANE_MIN = 160, ROLE_PLANE_MAX = 164;

        static readonly Dictionary<int, Opt> _byUid = new();                 // unit UID -> options
        static readonly Dictionary<int, float> _nextShot = new();           // unit UID -> time allowed to fire again
        // FAV target reservation: one entry per ENEMY (UID), never a circle on the ground (a 150 m circle used to lock out every other piece)
        static readonly Dictionary<int, float> _favTargetNext = new();      // enemy UID -> time before which no piece fires at it again
        // old reservation by area, kept only for the escape hatch FeuAVolonteRepartition = false
        static readonly List<(V3 pos, float until)> _recentTargets = new();
        static readonly HashSet<int> _smokeDone = new();
        static readonly HashSet<int> _aiArtyDone = new();
        static readonly Dictionary<int, LuaUnit> _mineByEntityId = new();
        static readonly Dictionary<int, LuaUnit> _mineByUid = new();
        static readonly Dictionary<int, float> _rangeByUnitId = new();     // DB unit id -> indirect-fire range (raw metres)
        static readonly List<int> _selectedArty = new();
        static readonly Dictionary<int, int> _lastHp = new();
        static readonly Dictionary<int, V3> _lastPos = new(), _moveDir = new();
        static readonly Dictionary<int, float> _nextRetreat = new();
        static readonly Dictionary<string, float> _warnNext = new();
        static ActionPanelRoot _root;
        static LuaMap _map;
        static LuaAI _ai;
        static float _nextUi, _nextUnits, _nextLogic, _nextAi, _nextRet, _nextAmb, _nextRav;
        static bool _uiFailed, _uiLogged, _aiDone, _smokeTriggersSet;
        static bool? _botAiOriginal, _botSmokeOriginal;

        internal static bool IsArtillery(int role) => role == ROLE_MLRS || role == ROLE_MORTAR || role == ROLE_LAM || role == ROLE_ARTY;
        static bool IsLongRange(int role) => role == ROLE_LAM;
        static bool IsHighValue(int role) => role == ROLE_LRSAM || role == ROLE_SRSAM || role == ROLE_AAINF || IsArtillery(role);
        static bool IsVehicleRole(int role) => role >= 10 && role <= 16;          // REP: never artillery (it stays where the player put it)
        static bool IsGroundCombat(int r) => (r >= 10 && r <= 13) || r == ROLE_LRSAM || r == ROLE_SRSAM || (r >= 30 && r <= 36);
        static bool IsAaRole(int r) => r == ROLE_LRSAM || r == ROLE_SRSAM || r == ROLE_AAINF;
        static bool IsGroundTransportRole(int r) => r == 10 || r == 12 || r == 13 || r == 14;
        static bool IsRavRole(int r) => IsGroundCombat(r);                          // RAV: never artillery either

        internal static void CreatePrefs()
        {
            var c = MelonPreferences.CreateCategory("RealismOverhaul_Assistants");
            Enabled = c.CreateEntry("Assistants", true, description: Build.Desc("Assistants de campagne pour tes unités : boutons CB / FAV de l'artillerie + repli, débarquement, embuscade et ravitaillement automatiques ; l'artillerie n'est jamais déplacée par le mod"));
            _smokeDefault = c.CreateEntry("FumigeneAutoParDefaut", true, description: Build.Desc("Active le fumigène défensif automatique (fonction vanilla) sur tes unités dès qu'elles apparaissent ; désactivable sur le bouton fumigène"));
            _longRange = c.CreateEntry("ArtillerieLonguePortee", true, description: Build.Desc("Les lanceurs qui n'ont que des missiles longue portée (Iskander, ATACMS...) participent au feu à volonté (les HIMARS / M270 avec roquettes tirent toujours leurs roquettes)"));
            _longRangeValuable = c.CreateEntry("ArtillerieLonguePorteeCiblesDeValeur", false, description: Build.Desc("Lanceurs à missiles seuls : true = feu à volonté seulement sur les cibles de valeur repérées (défense anti-aérienne, artillerie) ; false = sur toute cible au sol repérée à portée"));
            _cbCooldown = c.CreateEntry("ContreBatterieDelaiPiece", 20, description: Build.Desc("CB : secondes minimum entre deux salves d'une même pièce"));
            _cbInterval = c.CreateEntry("ContreBatterieIntervalle", 15, description: Build.Desc("CB : secondes minimum entre deux salves sur une même batterie ennemie (toutes pièces confondues)"));
            _cbFirstError = c.CreateEntry("ContreBatterieEcartInitial", 150f, description: Build.Desc("CB : écart de la première salve en mètres ; divisé par 2 à chaque salve sur la même batterie, jusqu'à 0 (pile dessus)"));
            _fawCooldown = c.CreateEntry("FeuAVolonteDelai", 35, description: Build.Desc("Secondes minimum entre deux salves d'une même pièce (x3 pour la longue portée, x2 sous 40 % de munitions, x3 sous 30 %)"));
            _favTargetCooldown = c.CreateEntry("FeuAVolonteDelaiCible", 45, description: Build.Desc("Secondes pendant lesquelles plus aucune pièce ne retire sur le même ennemi (la réserve porte sur l'ennemi, plus sur une zone)"));
            _favMaxPerTick = c.CreateEntry("FeuAVolonteSalvesParPasse", 4, description: Build.Desc("Nombre maximum de salves ordonnées dans la même passe (toutes les 2 s) ; 0 = autant que de pièces prêtes"));
            _favSpread = c.CreateEntry("FeuAVolonteRepartition", true, description: Build.Desc("Répartit les pièces sur les cibles repérées (une pièce par cible, la plus proche d'abord) ; false = ancien comportement, une seule pièce à la fois et réserve de 150 m autour du point visé"));
            _minAmmo = c.CreateEntry("MunitionsMinimum", 20, description: Build.Desc("Une pièce ne tire plus automatiquement sous ce pourcentage de munitions"));
            // ---- les yeux sur l'objectif : l'artillerie tire toujours, mais elle ne touche que si quelqu'un regarde la zone
            _eyeOn = c.CreateEntry("ArtillerieOeil", true, description: Build.Desc("L'artillerie tire toujours, même très loin, mais la salve ne tombe juste que si une de tes unités voit la zone : personne ne regarde = la salve tombe à côté et ne se corrige pas ; un éclaireur, un drone ou un désignateur laser sur la zone = elle se resserre salve après salve. Limite honnête : le mod compare des distances à plat, il ne sait pas si une crête, un bois ou de la fumée bouche la vue — il est donc toujours trop gentil, jamais trop sévère. Et l'écart est toujours rogné quand une de tes unités est près du point visé : tes obus ne partent jamais vers tes propres troupes"));
            _eyeObs = c.CreateEntry("ArtillerieOeilFacteurObservateur", 0.005f, description: Build.Desc("Œil : écart de la première salve quand un éclaireur, un drone ou un tireur d'élite voit la zone, en part de la distance de tir (0.005 = 20 m à 4 000 m) ; plafond 40 m, divisé par 2 à chaque salve"));
            _eyeSeen = c.CreateEntry("ArtillerieOeilFacteurVu", 0.015f, description: Build.Desc("Œil : écart de la première salve quand une unité ordinaire à toi voit la zone (0.015 = 60 m à 4 000 m) ; plafond 120 m, divisé par 2 à chaque salve. Mets 0 pour que la règle ne joue plus que sur la contre-batterie"));
            _eyeBlind = c.CreateEntry("ArtillerieOeilFacteurAveugle", 0.05f, description: Build.Desc("Œil : écart quand personne à toi ne voit la zone (0.05 = 200 m à 4 000 m) ; minimum 80 m, plafond 400 m, et il ne se resserre jamais — personne ne peut corriger le tir"));
            _eyeZone = c.CreateEntry("ArtillerieOeilRayonZone", 150f, description: Build.Desc("Œil : un ennemi repéré à moins de cette distance du point visé suffit à considérer que ton camp voit la zone (mètres)"));
            _eyeBrake = c.CreateEntry("ArtillerieOeilFrein", true, description: Build.Desc("Tir aveugle : la pièce tire quand même, mais deux fois moins vite, et le feu à volonté préfère une cible que quelqu'un observe (aucun tir n'est interdit)"));
            _eyeSilence = c.CreateEntry("ArtillerieOeilSilenceAveugle", false, description: Build.Desc("Contre-batterie : après 3 salves aveugles sur la même position sans que rien ne s'y fasse repérer, laisser ce point tranquille 3 minutes. C'est la seule règle qui empêche vraiment un tir : coupée par défaut"));
            _rangeMargin = c.CreateEntry("ArtillerieMargePortee", 50f, description: Build.Desc("Sécurité des tirs : marge gardée à l'intérieur de la portée de la pièce (mètres). L'écart de tir est rogné pour que le point visé reste toujours à portée, sinon le jeu aurait de quoi rapprocher la pièce — le mod ne déplace jamais ton artillerie"));
            _aiBoost = c.CreateEntry("IAAmelioree", true, description: Build.Desc("Campagne : l'IA ennemie utilise sa contre-batterie et ses fumigènes à 100 % (réglages internes du jeu)"));
            _retDefault = c.CreateEntry("RepliAutoParDefaut", true, description: Build.Desc("Repli auto : tes véhicules touchés (jamais l'artillerie) s'arrêtent et reculent loin de l'ennemi repéré"));
            _retDistance = c.CreateEntry("RepliDistance", 120, description: Build.Desc("Distance du repli en demi-tour, en mètres"));
            _retCooldown = c.CreateEntry("RepliDelai", 25, description: Build.Desc("Secondes minimum entre deux replis d'un même véhicule"));
            _retReverse = c.CreateEntry("RepliMarcheArriere", true, description: Build.Desc("Repli en marche arrière (blindage avant vers l'ennemi) ; false = demi-tour classique"));
            _retReverseDistance = c.CreateEntry("RepliDistanceMarcheArriere", 80, description: Build.Desc("Distance du repli en marche arrière (mètres ; la marche arrière est lente)"));
            _ambDefault = c.CreateEntry("EmbuscadeActive", false, description: Build.Desc("Embuscade : tes unités au sol retiennent leur tir jusqu'à ce qu'un ennemi repéré arrive à courte portée (ou qu'elles soient touchées)"));
            _ambFraction = c.CreateEntry("EmbuscadeFraction", 0.5f, description: Build.Desc("EMBU : ouverture du feu quand un ennemi repéré arrive à cette fraction de la portée (0.2 à 1)"));
            _ambRearm = c.CreateEntry("EmbuscadeRearmement", 30, description: Build.Desc("EMBU : secondes sans contact avant de reprendre le tir retenu"));
            _debDefault = c.CreateEntry("DebarquementSiToucheParDefaut", true, description: Build.Desc("Débarquement : l'infanterie descend quand son transport est vraiment touché"));
            _debMinDrop = c.CreateEntry("DebarquementSeuilCoup", 15, description: Build.Desc("DEB : perte de santé minimum d'un seul coup (en points de %) pour débarquer (ou santé <= 50 %)"));
            _ravDefault = c.CreateEntry("RavitaillementAutoParDefaut", false, description: Build.Desc("Ravitaillement auto : une unité au sol (jamais l'artillerie) à court de munitions ou abîmée va se ravitailler seule puis revient"));
            _ravAmmo = c.CreateEntry("RavitaillementSeuilMunitions", 25, description: Build.Desc("RAV : munitions (%) en dessous desquelles l'unité part se ravitailler"));
            _ravHp = c.CreateEntry("RavitaillementSeuilSante", 45, description: Build.Desc("RAV : santé (%) en dessous de laquelle l'unité part se réparer"));
            _ravRange = c.CreateEntry("RavitaillementDistanceMax", 2500f, description: Build.Desc("RAV : distance maximum du dépôt ou du camion (mètres)"));
            // safety: two battles in a row left unfinished with the new spreading -> the artillery goes back to the old behaviour (one piece at a time)
            _favUnclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _favGuardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_favGuardVersion.Value != GuardVersion) { _favGuardVersion.Value = GuardVersion; _favUnclean.Value = 0; }
            // the new default delay only replaces the old one, never a delay the player chose himself
            _favMigre025 = c.CreateEntry("ReglagesV025", false, is_hidden: true);
            if (!_favMigre025.Value)
            {
                if (_fawCooldown.Value == 50) _fawCooldown.Value = 35;      // old v0.24 default -> new default
                _favMigre025.Value = true;
            }
        }

        const string GuardVersion = "1.1";

        static void Log(string s) => Mod.Log.Msg("[ASSIST] " + s);

        /// Per-key rate-limited log for per-unit errors (a dead unit between two refreshes must not spam the console).
        static void Warn(string key, string msg)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_warnNext.TryGetValue(key, out var t) && now < t) return;
            _warnNext[key] = now + 30f;
            Log(msg);
        }

        /// Per-battle reset. AI statics are NOT restored here (only when the campaign ends), so a new mission's own values are not overwritten.
        internal static void ResetSession()
        {
            EndGuard();                                                      // the battle that ends here was a clean one
            DestroyButtons();
            _byUid.Clear(); _nextShot.Clear(); _recentTargets.Clear(); _favTargetNext.Clear(); _smokeDone.Clear(); _aiArtyDone.Clear(); _warnNext.Clear();
            _mineByEntityId.Clear(); _mineByUid.Clear(); _rangeByUnitId.Clear(); _minRangeByUnitId.Clear(); _longByUnitId.Clear(); _airById.Clear();
            _ammoByUnit = null; _ammoSrc = IntPtr.Zero;
            _selectedArty.Clear();
            _lastHp.Clear(); _lastPos.Clear(); _moveDir.Clear(); _nextRetreat.Clear();
            _ncs = null; _ncsLooked = false; _revJobs.Clear(); _revAfterUnload.Clear();
            _spotCache = null; _cbSpots.Clear();
            _ambHeld.Clear(); _ambLastContact.Clear(); _ambHp.Clear(); _dfRangeById.Clear();
            _debNext.Clear(); _transportByUid.Clear();
            _rav.Clear(); _ravNext.Clear();
            _root = null; _selCB.Clear(); _selFAW.Clear(); _selAA.Clear(); _hoverLogged = false; _postfixLogged = false;
            _map = null; _ai = null; _smokeTriggersSet = false;
            _uiFailed = false; _aiDone = false;   // _uiLogged kept: the UI diagnostic is logged once per game launch
            // artillery (FAV / CB): visibility self-tests, fire watchdog, per-type ranges, CB key, diagnostics
            Visibility.ResetSession();
            _fireJobs.Clear(); _blacklist.Clear(); _creeps.Clear(); _nextJobs = 0f;
            _favPieces.Clear(); _favTgtIdx.Clear(); _favUids.Clear(); _favPrune.Clear();
            _favErrors = 0; _favArmed = true; _favNextReport = 0f; _favFullLogged = false;
            _favUncleanAtStart = _favUnclean?.Value ?? 0;                    // read after EndGuard: 2 = the two previous battles were cut short
            if (_favUncleanAtStart >= 2) Log($"artillerie : {_favUncleanAtStart} batailles de suite interrompues, la répartition du feu à volonté reste coupée pour cette bataille (une seule pièce par passe)");
            _ammoTypeByUnitId.Clear(); _missileByUnitId.Clear();
            _porteeLogged.Clear(); _porteesOubliLogged = false;               // range logs: once per type and once for the forgetting, per battle
            // observer rule + firing-error budget: caches, per-target salvo counters and both kill-switches are per battle
            _eyes.Clear(); _typeByEyeUid.Clear(); _sightByUnitId.Clear(); _laserByUnitId.Clear(); _favSalvos.Clear();
            _friendPos.Clear(); _friendPosAt = -999f; _friendTeam = -1;
            _eyeErrors = 0; _eyeArmed = true; _eyeNotified = false;
            _keptOut = 0; _keptHard = true;
            _cbKey = -1; _cbNearEnemy[0].Clear(); _cbNearEnemy[1].Clear(); _cbNearOwn[0].Clear(); _cbNearOwn[1].Clear(); _nextCbDiag = 0f;
            _missingLogged.Clear(); _compLogged.Clear(); _compBroken = false;
            _selSig.Clear(); _selSigSet = false; _nextSelLog = 0f;
            OrdersReset();
        }

        internal static void ReArmAi() { _aiDone = false; _smokeTriggersSet = false; }

        /// Closing the game: the battle in progress is treated as finished normally (same as the start of the next mission).
        internal static void OnQuit() => EndGuard();

        /// Crash guard of the artillery spreading: the marker is written the first time a piece is given an automatic fire order
        /// in this battle, and wiped when the battle ends normally. Two battles in a row left unfinished -> the spreading stays off
        /// for the whole next battle and the artillery goes back to the old behaviour (one piece at a time).
        static bool _favArmedSession;
        static int _favUncleanAtStart;                                       // value read at the start of the battle: it never changes during it

        static void ArmGuard()
        {
            if (_favArmedSession || _favUnclean == null) return;
            _favArmedSession = true;
            try { _favUnclean.Value += 1; MelonPreferences.Save(); } catch { }
        }

        static void EndGuard()
        {
            if (!_favArmedSession || _favUnclean == null) return;
            _favArmedSession = false;
            try { if (_favUnclean.Value != 0) { _favUnclean.Value = 0; MelonPreferences.Save(); } } catch { }
        }

        /// The player asked for the spreading AND the guard has not tripped (read once per battle, never mid-battle).
        static bool FavSpreadOn => _favSpread != null && _favSpread.Value && _favUncleanAtStart < 2;

        // State of the frame being handled, read by the cached delegates below: same frame, main thread, no closure built per frame.
        static GameController _fGc;
        static int _fLocal, _fTeam;
        static float _fNow;
        static int _wUnits, _wLogic, _wAmb, _wRav, _wAi;

        static readonly Action _aUiOff = () => { DestroyButtons(); _root = null; };
        static readonly Action _aTirs = () => WatchFire(_fGc, GameNow);
        static readonly Action _aUnits = () => RefreshUnits(_fLocal);
        static readonly Action _aUi = UiTick;
        static readonly Action _aSmoke = () => SmokeDefault(_fGc);
        static readonly Action _aDiagArty = () => { if (Enabled.Value) LogArtyComponents(); };
        static readonly Action _aLogic = () => { if (Enabled.Value) Logic(_fGc, _fTeam); };
        static readonly Action _aRepli = () => { Retreat(_fGc, _fTeam, _fNow); WatchReverse(_fGc, _fNow); };
        static readonly Action _aAmb = () => Ambush(_fGc, _fTeam, _fNow);
        static readonly Action _aRav = () => Logistics(_fGc, _fTeam, _fNow);
        static readonly Action _aOrd = () => OrdersFrame(_fGc, _fLocal, _fTeam, _fNow);
        static readonly Action _aAi = () => AiBoost(_fGc, _fTeam);

        /// Called every frame while in campaign; each part throttles itself.
        internal static void Frame()
        {
            if (!Enabled.Value && _btns.Count > 0) Guard.Run("Assist.UiOff", _aUiOff);   // switched off during a mission: no button left behind
            if (!Enabled.Value && !_smokeDefault.Value && !_aiBoost.Value && _fireJobs.Count == 0) return;
            var gc = GameController._instance;
            var cp = gc?._GameSession_k__BackingField?.CurrentPlayer;
            if (gc == null || cp == null) return;
            int local = cp.UID, myTeam = (int)cp.TeamSide;
            float now = UnityEngine.Time.realtimeSinceStartup;
            _fGc = gc; _fLocal = local; _fTeam = myTeam; _fNow = now;

            // fire-order watchdog first: it must keep running even if the assistants are switched off right after an order
            if (_fireJobs.Count > 0 && now >= _nextJobs) { _nextJobs = now + 0.25f; Guard.Run("Assist.Tirs", _aTirs); }
            // the unit scan and the 2 s logic wait for a frame with no other heavy job (Planif.cs); their period is unchanged
            if (now >= _nextUnits && Planif.Take(ref _wUnits)) { _nextUnits = now + 1f; Guard.Run("Assist.Units", _aUnits); }
            if (Enabled.Value && now >= _nextUi)
            {
                _nextUi = now + 0.25f;
                Guard.Run("Assist.Ui", _aUi);
            }
            if (Enabled.Value && _root != null && _btns.Count > 0) Guard.Run("Assist.Cases", _checkSlots);
            if (Enabled.Value && _root != null && _btns.Count > 0) Guard.Run("Assist.Souris", _pollPointer);
            if (now >= _nextLogic && Planif.Take(ref _wLogic))
            {
                _nextLogic = now + 2f;
                Guard.Run("Assist.Smoke", _aSmoke);
                Guard.Run("Assist.DiagArty", _aDiagArty);
                Guard.Run("Assist.Logic", _aLogic);
            }
            if (Enabled.Value && now >= _nextRet) { _nextRet = now + 0.5f; Guard.Run("Assist.Repli", _aRepli); }
            if (Enabled.Value && now >= _nextAmb && Planif.Take(ref _wAmb)) { _nextAmb = now + 1f; Guard.Run("Assist.EMB", _aAmb); }
            if (Enabled.Value && now >= _nextRav && Planif.Take(ref _wRav)) { _nextRav = now + 3f; Guard.Run("Assist.RAV", _aRav); }
            if (Enabled.Value && now >= _nextOrd) { _nextOrd = now + 0.5f; Guard.Run("Assist.Ordres", _aOrd); }
            if (_aiBoost.Value && now >= _nextAi && Planif.Take(ref _wAi))
            {
                _nextAi = now + 5f;
                Guard.Run("Assist.IA", _aAi);
            }
        }

        /// The 0.25 s interface refresh (buttons, selection, state).
        static void UiTick()
        {
            var gc = _fGc;
            var root = gc?._OrdersRoot_k__BackingField;
            if (root == null) return;
            if (!_uiFailed && (_root == null || _root.Pointer != root.Pointer || _hold == null)) BuildUi(root, gc);
            RefreshSelection(gc);
            UpdateUi(gc);
        }

        // ------------------------------------------------------------ units & selection
        static void RefreshUnits(int local)
        {
            _map ??= new LuaMap();
            var units = _map.GetUnits(V3.zero, 1_000_000f, -1, local);
            _mineByEntityId.Clear();
            _mineByUid.Clear();
            _eyes.Clear();
            bool eyes = EyeOn;                                               // the sight snapshot is built in this pass, never in a separate one
            for (int i = 0; i < (units?.Length ?? 0); i++)
            {
                var u = units[i];
                try
                {
                    if (u == null || !u.IsAlive() || u.GetOwnerPlayerUID() != local) continue;
                    _mineByEntityId[u.Entity.EntityId] = u;
                    _mineByUid[u.UID] = u;
                    if (eyes) AddEye(u);
                }
                catch (Exception e) { Warn("units", "unité illisible : " + e.Message); }
            }
        }

        static void RefreshSelection(GameController gc)
        {
            _selectedArty.Clear(); _selCB.Clear(); _selFAW.Clear(); _selAA.Clear();
            OrdersClearSel();
            var set = gc._hudService?._selectedUnits;
            if (set != null && set.Count > 0)
            {
                var arr = set.GetEntities().ToArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (!_mineByEntityId.TryGetValue(arr[i].EntityId, out var u)) continue;
                    try
                    {
                        try { OrdersSelect(u); } catch { }
                        if (IsS400(u)) { if (S400Mode.FilterActive) _selAA.Add(u.UID); continue; }   // no button when the filter cannot work
                        if (!IsArtillery(u.UnitRole)) continue;
                        _selectedArty.Add(u.UID);
                        float range = RangeOf(u, out _, out bool lr);          // same rules as Logic(): a button never shows where it would do nothing
                        if (range <= 0) continue;
                        if (!lr) _selCB.Add(u.UID);                             // CB: every piece with artillery rounds (HIMARS / M270 included)
                        if (!lr || _longRange.Value) _selFAW.Add(u.UID);
                    }
                    catch { }
                }
            }
            LogSelection();
        }

        static readonly List<int> _selSig = new();
        static bool _selSigSet;
        static float _nextSelLog;

        /// Logs the selected artillery when it changes, at most once every 2 s (a change during the pause is logged at the next refresh).
        /// The comparison is done on the list itself: nothing is built while the selection does not change (up to 10 refreshes per second).
        static void LogSelection()
        {
            if (_selSigSet && SameUids(_selSig, _selectedArty)) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextSelLog) return;
            _nextSelLog = now + 2f;
            _selSig.Clear(); _selSig.AddRange(_selectedArty); _selSigSet = true;
            if (_selectedArty.Count == 0) { if (_selAA.Count == 0) Log("sélection : aucune pièce d'artillerie"); return; }
            Log("sélection : " + string.Join(" ; ", _selectedArty.Select(DescribeUid)));
        }

        static bool SameUids(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// uid / name / role / range / minimum / long range / current options of one of the player's units, for the logs.
        static string DescribeUid(int uid)
        {
            if (!_mineByUid.TryGetValue(uid, out var u) || u == null) return $"uid {uid} (pas dans tes unités)";
            try
            {
                float range = RangeOf(u, out float min, out bool lr);
                return $"{u.Name} uid {uid} rôle {u.UnitRole} portée {range:0} min {min:0} LR {(lr ? "oui" : "non")} FAV {(Has(uid, Opt.FAW) ? "on" : "off")} CB {(Has(uid, Opt.CB) ? "on" : "off")}";
            }
            catch (Exception e) { return $"uid {uid} (illisible : {e.Message})"; }
        }

        // ------------------------------------------------------------ UI : CB / FAV as native order buttons (artillery only)
        // Clones of the vanilla auto-fire button: real game icons (no crossed-out art, "off" = dimmed), no text (name in the hover hint),
        // placed in free cells of the vanilla order grid, or in a tidy row above it when the grid has no room.
        sealed class Btn
        {
            // name and hover description: TitleKey(Opt) / DescKey(Opt), read at display time
            public string Name; public Opt Opt; public List<int> Sel; public string[] Icons;
            public UiOrderButton B; public UnityEngine.GameObject Off, On; public UnityEngine.CanvasGroup WholeDim;
            public ActionPanelSlot Slot; public IntPtr Parent; public bool Visible, State; public float LastClick;
            public UnityEngine.Canvas Canvas;
            // selection and displayed state while the pointer rested on the button, before the press (a click that also changes the world selection keeps these)
            public readonly List<int> Snap = new(); public bool SnapState; public float SnapAt = -1f;
        }
        static readonly List<Btn> _btns = new();
        static readonly List<int> _selCB = new(), _selFAW = new(), _selAA = new();
        static readonly Dictionary<string, UnityEngine.Sprite> _sprites = new();
        static readonly OrderButtonType[] _allTypes = (OrderButtonType[])Enum.GetValues(typeof(OrderButtonType));
        static readonly string[] IconsCB = { "FM_Targeting_Point", "Order_PrecisionStrike", "Order_FireMission" };
        static readonly string[] IconsFAV = { "Ability_AutoFire_On", "FM_Ammo_Expl", "Order_ForceFire" };
        static readonly string[] IconsAA = { "Order_AltitudePlane_Up", "Ability_Flares" };   // never the radar icon: the S-400 already shows the vanilla radar button

        static bool IsS400(LuaUnit u) { try { return S400Mode.IsTracked(u.SpawnData?.Unit?.UnitID ?? 0); } catch { return false; } }
        internal static bool AntiAirOn(int uid) => Has(uid, Opt.AA);
        /// The S-400 filter may only run while the player can switch it off with the button.
        internal static bool AaControlReady => Enabled != null && Enabled.Value && !_uiFailed && _btns.Any(b => b.Opt == Opt.AA && b.B != null);
        static readonly Action _checkSlots = CheckSlots;
        static readonly Action _pollPointer = PollPointer;
        static UnityEngine.GameObject _hold;
        static Btn _hovered;
        static bool _rowMode, _placeLogged, _hoverLogged, _postfixLogged, _hintTitleLogged, _hintDescLogged;
        static int _lastActiveCount = -1, _evictions, _hintFitChecks;
        static float _evictWindow, _nextPostfix;

        static void BuildUi(ActionPanelRoot root, GameController gc)
        {
            DestroyButtons();
            _root = root;
            UiOrderButton src = null;
            foreach (var t in new[] { OrderButtonType.AbilityAutoFire, OrderButtonType.OrderHoldFire })
                if (root._items != null && root._items.TryGetValue(t, out var c) && c != null) { src = c; Log($"bouton modèle = {t} ('{c.gameObject.name}')"); break; }
            if (src == null) { _uiFailed = true; Mod.Log.Warning("[ASSIST] aucun bouton modèle trouvé dans le panneau d'ordres : assistants sans interface"); return; }
            foreach (var n in new[] { "RealismOverhaul_Assistants", "RealismOverhaul_Hold" })
                try { var old = root.transform.Find(n); if (old != null) UnityEngine.Object.Destroy(old.gameObject); } catch { }
            try
            {
                _hold = new UnityEngine.GameObject("RealismOverhaul_Hold");
                _hold.AddComponent(Il2CppType.Of<RT>());
                _hold.transform.SetParent(root.transform, false);
                _hold.SetActive(false);
                IndexSprites(root, gc);
                _btns.Add(Make(src, "RealismOverhaul_CB", Opt.CB, _selCB, IconsCB));
                _btns.Add(Make(src, "RealismOverhaul_FAV", Opt.FAW, _selFAW, IconsFAV));
                _btns.Add(Make(src, "RealismOverhaul_AA", Opt.AA, _selAA, IconsAA));
                MakeOrderButtons(src);
                Log("interface créée : boutons CB et FAV (artillerie), anti-aérien (S-400) et ordres (riposte, défendre, débarquer, ravitailler, bâtiment, repli)");
                if (!_uiLogged) { _uiLogged = true; Guard.Run("Assist.Diag", () => Diagnose(root, src)); }
            }
            catch (Exception e) { _uiFailed = true; DestroyButtons(); Mod.Log.Error("[ASSIST] création de l'interface impossible : " + e); }
        }

        /// Text keys of a button's name and hover description (Txt.cs; read when shown, so a language change in battle is followed).
        static TxtKey TitleKey(Opt o) => o switch
        {
            Opt.CB => TxtKey.OR_CB_TITLE, Opt.FAW => TxtKey.OR_FAV_TITLE, Opt.AA => TxtKey.OR_AA_TITLE, Opt.RIP => TxtKey.OR_RIP_TITLE,
            Opt.DEF => TxtKey.OR_DEF_TITLE, Opt.DEBC => TxtKey.OR_DEBC_TITLE, Opt.RAVX => TxtKey.OR_RAVX_TITLE, Opt.GAR => TxtKey.OR_GAR_TITLE,
            _ => TxtKey.OR_RZ_TITLE,
        };

        static TxtKey DescKey(Opt o) => o switch
        {
            Opt.CB => TxtKey.OR_CB_DESC, Opt.FAW => TxtKey.OR_FAV_DESC, Opt.AA => TxtKey.OR_AA_DESC, Opt.RIP => TxtKey.OR_RIP_DESC,
            Opt.DEF => TxtKey.OR_DEF_DESC, Opt.DEBC => TxtKey.OR_DEBC_DESC, Opt.RAVX => TxtKey.OR_RAVX_DESC, Opt.GAR => TxtKey.OR_GAR_DESC,
            _ => TxtKey.OR_RZ_DESC,
        };

        /// French button name, for the log.
        static string TitleFr(Opt o) => Txt.Fr(TitleKey(o));

        static Btn Make(UiOrderButton src, string name, Opt opt, List<int> sel, string[] icons)
        {
            var go = UnityEngine.Object.Instantiate<UnityEngine.GameObject>(src.gameObject, _hold.transform, false);
            go.name = name;
            var s = new Btn { Name = name, Opt = opt, Sel = sel, Icons = icons, B = go.GetComponent<UiOrderButton>() };
            if (s.B == null) throw new InvalidOperationException("le clone n'a pas de UiOrderButton");
            s.Parent = _hold.transform.Pointer;
            Wire(s);
            var off = go.transform.Find("Off"); var on = go.transform.Find("On");
            s.Off = off != null ? off.gameObject : null; s.On = on != null ? on.gameObject : null;
            Guard.Run("Assist.Icone", () => ApplyIcon(s));
            if (s.Off != null && s.On != null)
            {
                var cg = s.Off.AddComponent(Il2CppType.Of<UnityEngine.CanvasGroup>()).Cast<UnityEngine.CanvasGroup>();
                cg.alpha = 0.4f;                                                      // "off" = the same icon, dimmed (never the crossed-out art)
                foreach (var p in new[] { "On/OnIconHover", "On/OnIconClick", "Off/OffIconHover", "Off/OffIconClick" })
                { var t = go.transform.Find(p); if (t != null) t.gameObject.SetActive(false); }
            }
            else s.WholeDim = go.AddComponent(Il2CppType.Of<UnityEngine.CanvasGroup>()).Cast<UnityEngine.CanvasGroup>();
            try { var le = go.AddComponent(Il2CppType.Of<UnityEngine.UI.LayoutElement>()).Cast<UnityEngine.UI.LayoutElement>(); le.ignoreLayout = true; } catch { }
            try { var hit = go.AddComponent(Il2CppType.Of<UImage>()).Cast<UImage>(); hit.color = new UnityEngine.Color(1f, 1f, 1f, 0f); hit.raycastTarget = true; } catch { }   // whole cell hoverable / clickable
            SetState(s, false);
            return s;
        }

        static void Wire(Btn s)
        {
            // One click may arrive through several paths (ClickLogic, ClickEvent, Button.onClick): debounce so it toggles once.
            Action click = () => Click(s, "panneau");
            var h = (Il2CppSystem.Action<UnityEngine.GameObject, UnityEngine.EventSystems.PointerEventData.InputButton>)
                new Action<UnityEngine.GameObject, UnityEngine.EventSystems.PointerEventData.InputButton>((g, ib) => click());
            s.B.ClickLogic = h;
            try { s.B.ClickEvent = h; } catch (Exception e) { Log("ClickEvent: " + e.Message); }   // replaced, never combined: a vanilla auto-fire handler must not run too
            try { s.B._button?.onClick?.AddListener((UnityEngine.Events.UnityAction)click); } catch (Exception e) { Log("onClick: " + e.Message); }
            s.B.HoverEnterLogic = (Il2CppSystem.Action<UnityEngine.GameObject>)new Action<UnityEngine.GameObject>(g => Guard.Run("Assist.Hover", () =>
            {
                _hovered = s; ShowHint(s);
                if (!_hoverLogged) { _hoverLogged = true; Log($"survol reçu sur {s.Name} (initialisé={s.B.IsInitialized})"); }
            }));
            s.B.HoverExitLogic = (Il2CppSystem.Action<UnityEngine.GameObject>)new Action<UnityEngine.GameObject>(g => Guard.Run("Assist.HoverExit", () =>
            {
                if (_hovered == s) _hovered = null;
                _root?.HintsScript?.SetHintsState(false, false);
            }));
        }

        static void Click(Btn s, string via)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - s.LastClick < 0.3f) return;     // per button: merges the delivery paths of one click, never swallows a click on the other button
            s.LastClick = now;
            Log($"clic sur {s.Name} ({via})");
            Guard.Run("Assist.Click", () => { if (IsAction(s.Opt)) RunAction(s); else Toggle(s); });
        }

        /// The vanilla panel never delivers hover/click events to the clones (no hover nor click line in any game log):
        /// the mod reads the mouse itself and tests the buttons' rectangles every frame.
        static void PollPointer()
        {
            // nothing on screen and nothing hovered: no mouse read and no rectangle test this frame
            if (_hovered == null)
            {
                bool any = false;
                foreach (var s in _btns) if (s.Visible && s.B != null) { any = true; break; }
                if (!any) return;
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;
            var pos = mouse.position.ReadValue();
            Btn over = null;
            foreach (var s in _btns)
            {
                if (!s.Visible || s.B == null || !s.B.gameObject.activeInHierarchy) continue;
                if (s.Canvas == null) s.Canvas = s.B.GetComponentInParent<UnityEngine.Canvas>();
                UnityEngine.Camera cam = s.Canvas != null && s.Canvas.renderMode != UnityEngine.RenderMode.ScreenSpaceOverlay ? s.Canvas.worldCamera : null;
                if (UnityEngine.RectTransformUtility.RectangleContainsScreenPoint(s.B.transform.Cast<RT>(), pos, cam)) { over = s; break; }
            }
            if (over != _hovered)
            {
                if (over == null) { _hovered = null; _root?.HintsScript?.SetHintsState(false, false); }
                else
                {
                    _hovered = over;
                    ShowHint(over);
                    if (!_hoverLogged) { _hoverLogged = true; Log($"survol reçu sur {over.Name} (souris lue par le mod)"); }
                }
            }
            if (over != null && mouse.leftButton.wasPressedThisFrame) Click(over, "souris");
            // snapshot taken in the frames BEFORE the press while the pointer rests on a button: a click that also reaches the world
            // (and changes the selection in the same frame) still acts on the pieces the button was showing
            else if (over != null)
            {
                over.Snap.Clear(); over.Snap.AddRange(over.Sel); over.SnapState = over.State; over.SnapAt = UnityEngine.Time.realtimeSinceStartup;
            }
        }

        /// The game's hover box draws the description right under a one-line title: the name, the state and the count of
        /// units concerned all stay on the title line, and the description holds the explanation alone (short, two lines at most).
        static void ShowHint(Btn s)
        {
            var hs = _root?.HintsScript; if (hs == null) return;
            int n = s.Sel.Count(u => IsAction(s.Opt) ? ActionBusy(s.Opt, u) : Has(u, s.Opt));
            var lang = Txt.Current;
            string title = Txt.Format(lang, s.State ? TxtKey.OR_HINT_ON : TxtKey.OR_HINT_OFF, TitleKey(s.Opt), n, s.Sel.Count);
            string desc = Txt.Get(lang, DescKey(s.Opt));
            hs.SetTitle("", title);
            hs.SetDescription(desc);
            hs.SetHintsState(true, true);
            if (_hintFitChecks < 40) Guard.Run("Assist.Infobulle", () => CheckHintFits(hs, s, title, desc));
        }

        /// Safety check on the hover box, log only: measures the two texts the game has just laid out and warns once for the
        /// title above one line and once for the description above two. Nothing is moved, resized or cut: only read.
        static void CheckHintFits(Hints hs, Btn s, string title, string desc)
        {
            _hintFitChecks++;
            if (!_hintTitleLogged) _hintTitleLogged = TooTall(hs._titleText, title, 1, s.Name, "titre");
            if (!_hintDescLogged) _hintDescLogged = TooTall(hs._descriptionText, desc, 2, s.Name, "description");
            if (_hintTitleLogged && _hintDescLogged) _hintFitChecks = int.MaxValue;
        }

        /// True (and one log line) when a hint text needs more lines than the box gives it.
        static bool TooTall(TMP t, string text, int lines, string name, string what)
        {
            if (t == null) return false;
            float line = t.fontSize * 1.2f;
            if (!(line > 0f)) return false;
            float height = t.preferredHeight;
            if (!(height > line * (lines + 0.4f))) return false;                  // the allowed lines plus a margin: it fits
            Mod.Log.Warning($"[ASSIST] infobulle de {name} : {what} sur environ {(int)Math.Round(height / line)} ligne(s) au lieu de {lines} " +
                            $"({text.Length} caractères, langue {Txt.Current}) : risque de chevauchement, texte à raccourcir dans Txt.cs");
            return true;
        }

        static void Toggle(Btn s)
        {
            if (!Enabled.Value) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            // the pieces and the state the button was SHOWING just before the press (not a selection the same click may have changed)
            bool fresh = s.SnapAt >= 0f && now - s.SnapAt < 0.5f;
            var uids = new List<int>(fresh ? s.Snap : s.Sel);
            bool turnOff = fresh ? s.SnapState : s.State;                        // displayed "on" -> the click switches it off
            if (fresh && !uids.SequenceEqual(s.Sel))
                Log($"{s.Name} : sélection changée pendant le clic, appliqué aux unités affichées avant le clic ({string.Join(",", uids)}) et non à ({string.Join(",", s.Sel)})");
            if (uids.Count == 0) return;
            foreach (var uid in uids) { _byUid.TryGetValue(uid, out var v); _byUid[uid] = turnOff ? (v & ~s.Opt) : (v | s.Opt); }
            foreach (var uid in uids) Log($"{TitleFr(s.Opt)} {(turnOff ? "désactivé" : "ACTIVÉ")} : {DescribeUid(uid)}");
            OrdersOnToggle(s.Opt, uids, !turnOff);
            Mod.Notify(new TxtMsg(turnOff ? TxtKey.N_OR_TOGGLE_OFF : TxtKey.N_OR_TOGGLE_ON, TitleKey(s.Opt), uids.Count));
            UpdateUi(GameController._instance);
            if (_hovered == s) ShowHint(s);
        }

        static void IndexSprites(ActionPanelRoot root, GameController gc)
        {
            _sprites.Clear();
            void Add(UnityEngine.Component c)
            {
                if (c == null) return;
                try
                {
                    foreach (var im in c.GetComponentsInChildren<UImage>(true))
                    { var sp = im != null ? im.sprite : null; if (sp != null && !_sprites.ContainsKey(sp.name)) _sprites[sp.name] = sp; }
                }
                catch { }
            }
            foreach (var t in _allTypes) { try { if (root._items.TryGetValue(t, out var vb)) Add(vb); } catch { } }   // parked buttons included
            try { Add(gc._FireMissionUi_k__BackingField); } catch { }
            Add(root);
            if (!_sprites.ContainsKey(IconsCB[0]) || !_sprites.ContainsKey(IconsFAV[0]))
                try
                {
                    foreach (var sp in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Sprite>())
                        if (sp != null && !_sprites.ContainsKey(sp.name)) _sprites[sp.name] = sp;
                }
                catch { }
        }

        static void ApplyIcon(Btn s)
        {
            var imgs = s.B.GetComponentsInChildren<UImage>(true).Where(i => i != null).ToList();
            bool named = imgs.Any(i => i.gameObject.name.Contains("Icon"));
            var onArt = new Dictionary<string, UnityEngine.Sprite>();                 // Main/Hover/Click -> template On art (never crossed out)
            foreach (var im in imgs) if (im.gameObject.name.StartsWith("OnIcon") && im.sprite != null) onArt[im.gameObject.name.Substring(6)] = im.sprite;
            string pick = s.Icons.FirstOrDefault(n => _sprites.ContainsKey(n));
            foreach (var im in imgs)
            {
                var n = im.gameObject.name; int k = n.IndexOf("Icon", StringComparison.Ordinal);
                if (named && k < 0) continue;
                string suffix = k >= 0 ? n.Substring(k + 4) : "Main";
                UnityEngine.Sprite sp = null;
                if (pick != null) { if (suffix != "Main") _sprites.TryGetValue($"{pick}_{suffix}", out sp); if (sp == null) sp = _sprites[pick]; }
                else onArt.TryGetValue(suffix, out sp);
                if (sp != null) { im.sprite = sp; im.preserveAspect = true; }
            }
            Log($"{s.Name} : icône '{pick ?? "modèle (On)"}'");
        }

        static void SetState(Btn s, bool on)
        {
            s.State = on;
            if (s.Off != null && s.On != null)
            {
                if (s.Off.activeSelf == on) s.Off.SetActive(!on);
                if (s.On.activeSelf != on) s.On.SetActive(on);
            }
            if (s.WholeDim != null) s.WholeDim.alpha = on ? 1f : 0.4f;
        }

        static void UpdateUi(GameController gc)
        {
            if (_root == null || _btns.Count == 0) return;
            bool fm = false; try { var f = gc?._FireMissionUi_k__BackingField; fm = f != null && f.IsActive; } catch { }
            bool panel = _root.UiShowState && !_root.IsConfimationState && !fm;
            bool changed = false;
            foreach (var s in _btns)
            {
                bool v = panel && s.Sel.Count > 0;
                if (v != s.Visible) { s.Visible = v; changed = true; }
                bool on = s.Sel.Count > 0 && StateOf(s);                             // plain loops: nothing is allocated four times per second
                SetState(s, on);                                                     // always re-applied: a re-enabled clone may have its groups reset by vanilla code
            }
            if (changed) Place();
        }

        /// "On" state of a button: any piece busy for an action button, every piece switched on for a toggle.
        static bool StateOf(Btn s)
        {
            bool action = IsAction(s.Opt);
            for (int i = 0; i < s.Sel.Count; i++)
            {
                if (action) { if (ActionBusy(s.Opt, s.Sel[i])) return true; }
                else if (!Has(s.Sel[i], s.Opt)) return false;
            }
            return !action;
        }

        static void CheckSlots()
        {
            var act = _root?._activeSlots; if (act == null) return;
            bool dirty = act.Count != _lastActiveCount, evicted = false;
            // the eviction test is only needed when the slot count did not change (a dirty panel is placed again anyway), and it stops
            // at the first evicted button: this runs in every frame while the buttons are on screen
            if (!dirty)
                foreach (var s in _btns)
                {
                    if (!s.Visible || s.B == null) continue;
                    var p = s.B.transform.parent;
                    if ((s.Slot != null && act.ContainsValue(s.Slot)) || p == null || p.Pointer != s.Parent) { evicted = true; break; }
                }
            if (!dirty && !evicted) return;
            _lastActiveCount = act.Count;
            if (evicted && !dirty)                                                       // a slot reused because the order set changed is not an eviction
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now > _evictWindow) { _evictWindow = now + 10f; _evictions = 0; }
                if (++_evictions > 5 && !_rowMode) { _rowMode = true; Log("cases reprises en boucle par le jeu -> rangée au-dessus du panneau"); }
            }
            Place();
        }

        static V3 Center(UnityEngine.Component c)
        {
            var rt = c.transform.Cast<RT>(); var r = rt.rect;
            return _root.transform.InverseTransformPoint(rt.TransformPoint(new V3(r.center.x, r.center.y, 0f)));
        }

        static List<ActionPanelSlot> ReadingOrder()
        {
            var res = new List<ActionPanelSlot>(); var arr = _root._rawButtonSlots;
            for (int i = 0; i < (arr?.Length ?? 0); i++) if (arr[i] != null) res.Add(arr[i]);
            return res.OrderByDescending(sl => Math.Round(Center(sl).y)).ThenBy(sl => Center(sl).x).ToList();
        }

        static bool HasForeignButton(ActionPanelSlot sl)
        {
            foreach (var b in sl.GetComponentsInChildren<UiOrderButton>(false))
                if (b != null && !b.gameObject.name.StartsWith("RealismOverhaul_")) return true;
            return false;
        }

        static void Park(Btn s)
        {
            s.Slot = null;
            if (_hovered == s) { _hovered = null; try { var hs = _root != null ? _root.HintsScript : null; if (hs != null) hs.SetHintsState(false, false); } catch { } }   // a parked clone never gets its hover exit
            if (s.B == null || _hold == null || s.Parent == _hold.transform.Pointer) return;
            s.B.transform.SetParent(_hold.transform, false); s.Parent = _hold.transform.Pointer;
        }

        static void Place()
        {
            var vis = new List<Btn>();
            foreach (var s in _btns) { if (s.Visible) vis.Add(s); else Park(s); }
            if (vis.Count == 0) return;
            var order = ReadingOrder();
            UiOrderButton refBtn = null; ActionPanelSlot refSlot = null; var used = new HashSet<IntPtr>();
            foreach (var t in _allTypes)
                if (_root._activeSlots.TryGetValue(t, out var sl) && sl != null)
                {
                    used.Add(sl.Pointer);
                    if (refBtn == null && _root._items.TryGetValue(t, out var vb) && vb != null && vb.gameObject.activeInHierarchy) { refBtn = vb; refSlot = sl; }
                }
            if (_rowMode || refBtn == null || order.Count == 0) { PlaceRow(vis, order, refBtn); return; }
            var rp = refBtn.transform.parent;
            bool parented = rp != null && rp.Pointer == refSlot.transform.Pointer;
            bool Busy(ActionPanelSlot sl) => used.Contains(sl.Pointer) || HasForeignButton(sl);
            int last = -1; for (int i = 0; i < order.Count; i++) if (Busy(order[i])) last = i;
            var free = new List<ActionPanelSlot>();
            for (int k = 1; k <= order.Count && free.Count < vis.Count; k++)
            {
                var sl = order[(last + k) % order.Count];
                if (sl.gameObject.activeInHierarchy && !Busy(sl)) free.Add(sl);
            }
            if (free.Count < vis.Count) { PlaceRow(vis, order, refBtn); return; }        // never split between the grid and the row
            for (int i = 0; i < vis.Count; i++) PutInSlot(vis[i], free[i], refBtn, refSlot, parented);
            if (!_placeLogged)
            {
                _placeLogged = true;
                Log($"grille ({(parented ? "enfant de la case" : "position de la case")}) : {string.Join(", ", vis.Select((s, i) => $"{s.Name}->{free[i].name}"))}");
            }
        }

        static void PutInSlot(Btn s, ActionPanelSlot slot, UiOrderButton refBtn, ActionPanelSlot refSlot, bool parented)
        {
            var rt = s.B.transform.Cast<RT>(); var rr = refBtn.transform.Cast<RT>();
            rt.SetParent(parented ? slot.transform : rr.parent, false);
            rt.anchorMin = rr.anchorMin; rt.anchorMax = rr.anchorMax; rt.pivot = rr.pivot;
            rt.sizeDelta = rr.sizeDelta; rt.localScale = rr.localScale; rt.localRotation = rr.localRotation;
            if (parented) rt.anchoredPosition = rr.anchoredPosition;
            else rt.position = slot.transform.position + (rr.position - refSlot.transform.position);
            rt.SetAsLastSibling();
            s.Slot = slot; s.Parent = rt.parent.Pointer;
            if (!s.B.gameObject.activeSelf) s.B.gameObject.SetActive(true);
            MakeClickable(s.B);
            SetState(s, s.State);
        }

        static void PlaceRow(List<Btn> vis, List<ActionPanelSlot> order, UiOrderButton refBtn)
        {
            var root = _root.transform; var prt = root.Cast<RT>();
            float w = 40f, h = 40f;
            try { var r = (refBtn != null ? refBtn.transform : vis[0].B.transform).Cast<RT>().rect; if (r.width > 10f) { w = r.width; h = r.height; } } catch { }
            V3 a = V3.zero; float stepX = w + 4f, stepY = h + 4f; bool grid = false;
            if (order.Count >= 2)
            {
                a = Center(order[0]);
                foreach (var sl in order.Skip(1))
                {
                    var p = Center(sl);
                    if (Math.Abs(p.y - a.y) < 1f) { if (!grid) { stepX = p.x - a.x; grid = true; } }
                    else { stepY = a.y - p.y; break; }
                }
            }
            for (int i = 0; i < vis.Count; i++)
            {
                var s = vis[i]; var rt = s.B.transform.Cast<RT>();
                rt.SetParent(root, false);
                rt.anchorMin = new UnityEngine.Vector2(0.5f, 0.5f); rt.anchorMax = rt.anchorMin; rt.pivot = rt.anchorMin;
                rt.sizeDelta = new UnityEngine.Vector2(w, h); rt.localScale = V3.one;
                rt.localPosition = grid ? new V3(a.x + i * stepX, a.y + stepY, 0f)
                                        : new V3(prt.rect.xMin + w / 2 + i * (w + 4f), prt.rect.yMax + 4f + h / 2, 0f);
                s.Slot = null; s.Parent = rt.parent.Pointer;
                if (!s.B.gameObject.activeSelf) s.B.gameObject.SetActive(true);
                MakeClickable(s.B);
                SetState(s, s.State);
            }
            if (!_placeLogged) { _placeLogged = true; Log($"rangée au-dessus du panneau ({vis.Count} bouton(s), grille={grid})"); }
        }

        static void DestroyButtons()
        {
            foreach (var s in _btns) try { if (s.B != null) UnityEngine.Object.Destroy(s.B.gameObject); } catch { }
            _btns.Clear();
            try { if (_hold != null) UnityEngine.Object.Destroy(_hold); } catch { }
            if (_hovered != null) { try { var hs = _root != null ? _root.HintsScript : null; if (hs != null) hs.SetHintsState(false, false); } catch { } }
            _hold = null; _hovered = null; _sprites.Clear();
            _rowMode = false; _placeLogged = false; _lastActiveCount = -1; _evictions = 0;
        }

        /// Harmony postfix on ValidOrdersSystem.FilterOrdersData: immediate refresh on selection change (the 0.25 s poll stays as safety net).
        internal static void OnOrdersRefreshed()
        {
            var gc = GameController._instance;
            if (!Campaign.InCampaign || Campaign.MissionInerte || Mod.AntiCheatActive || !Enabled.Value || _root == null || _btns.Count == 0 || gc == null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextPostfix) { _nextUi = Math.Min(_nextUi, _nextPostfix); return; }   // at most 10 refreshes per second; a skipped one is picked up by Frame
            _nextPostfix = now + 0.1f;
            if (!_postfixLogged) { _postfixLogged = true; Log("FilterOrdersData : rafraîchissement immédiat actif"); }
            RefreshSelection(gc); UpdateUi(gc); CheckSlots();
        }

        static void Diagnose(ActionPanelRoot root, UiOrderButton tpl)
        {
            string N(UnityEngine.Object o) => o == null ? "null" : o.name;
            try { Log($"modèle : autoOn={N(tpl._abilityAutoUseOn)} autoOff={N(tpl._abilityAutoUseOff)} icônes={N(tpl._defaultIconSetObject)}"); } catch (Exception e) { Log("diag modèle : " + e.Message); }
            try
            {
                var es = tpl.uiElementStates;
                foreach (UiElementState st in Enum.GetValues(typeof(UiElementState)))
                    if (es != null && es.TryGetValue(st, out var l) && l != null && l.Data != null)
                        foreach (var d in l.Data)
                            if (d != null) Log($"  état {st} : objets={(d.Objects == null ? "" : string.Join("/", d.Objects.Select(o => N(o))))} actif={d.SetActive} couleur={(d.SetColor ? d.Color.ToString() : "-")}");
            }
            catch (Exception e) { Log("diag états : " + e.Message); }
            try { foreach (var im in tpl.GetComponentsInChildren<UImage>(true)) Log($"  image {im.gameObject.name} = {N(im.sprite)}"); } catch { }
            try
            {
                var slots = root._rawButtonSlots;
                Log($"panneau : cases={slots?.Length} attente={N(root._inactiveButtonsHolder)} fond={N(root._panelBackground)}");
                for (int i = 0; i < (slots?.Length ?? 0); i++)
                {
                    var sl = slots[i]; if (sl == null) continue; var c = Center(sl);
                    Log($"  case {i} '{sl.name}' parent={N(sl.transform.parent)} actif={sl.gameObject.activeSelf} centre=({c.x:0},{c.y:0}) liée={N(sl.BindedItem)} enfants={sl.transform.childCount}");
                }
            }
            catch (Exception e) { Log("diag cases : " + e.Message); }
            try { Log("icônes connues : " + string.Join(", ", _sprites.Keys.Where(k => k.StartsWith("Order_") || k.StartsWith("Ability_") || k.StartsWith("FM_") || k.StartsWith("AutoAbility")).OrderBy(k => k))); } catch { }
        }

        /// The template button is usually disabled (no unit has the vanilla auto-fire ability): force the clone interactable.
        static void MakeClickable(UiOrderButton b)
        {
            if (b == null) return;
            try { b.SetInteractable(true, true); } catch { }
            try { if (b._button != null) b._button.interactable = true; } catch { }
            try { b.SetBlockRaycast(true); } catch { }
        }

        static bool Has(int uid, Opt o) => _byUid.TryGetValue(uid, out var v) && (v & o) != 0;

        /// Enemy units spotted by my team, shared by every assistant for 1 s (empty while no visibility source is validated).
        static List<(V3 pos, int role)> _spotCache;
        static float _spotUntil;
        static int _spotTeam = -1;
        static List<(V3 pos, int role)> SpottedCached(GameController gc, int enemyTeam)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_spotCache != null && now < _spotUntil && _spotTeam == enemyTeam) return _spotCache;
            _spotCache = Spotted(gc, enemyTeam); _spotUntil = now + 1f; _spotTeam = enemyTeam;
            return _spotCache;
        }

        /// Full visibility answer for my team (list, source, usable), cached 1 s by Visibility. notify: on-screen notice once per battle if unusable.
        static VisResult SpottedInfo(GameController gc, int enemyTeam, bool notify = false) => Visibility.VisibleEnemies(gc, enemyTeam == 0 ? 1 : 0, enemyTeam, notify);

        static void CancelOne(EcsEventBus.CommandsBus cmd, int uid, string key)
        {
            try { cmd?.CancelUnitCommands?.Invoke(Cheats.ToIl2Cpp(new List<int> { uid }), true); } catch (Exception e) { Warn(key, "arrêt : " + e.Message); }
        }

        // ------------------------------------------------------------ REP : repli automatique des véhicules touchés (marche arrière)
        static void Retreat(GameController gc, int myTeam, float now)
        {
            var cmd = gc._GetEcsEventBus_k__BackingField?.Commands;
            int enemyTeam = myTeam == 0 ? 1 : 0;
            foreach (var kv in _mineByUid)
            {
                int uid = kv.Key; var u = kv.Value;
                try
                {
                    if (!IsVehicleRole(u.UnitRole) || !u.IsAlive()) continue;
                    var pos = u.GetPosition();
                    // during a retreat the movement is the retreat itself: keep the heading of the advance
                    bool retreating = _nextRetreat.TryGetValue(uid, out var nr) && now < nr;
                    if (!retreating && _lastPos.TryGetValue(uid, out var lp) && (pos - lp).sqrMagnitude > 9f) _moveDir[uid] = (pos - lp).normalized;
                    _lastPos[uid] = pos;
                    int hp = u.GetHealPercentage();
                    bool hit = _lastHp.TryGetValue(uid, out var last) && hp < last;
                    _lastHp[uid] = hp;
                    if (!hit) continue;
                    // an explicit order button owns the unit: the automatic retreat / dismount must not cancel it
                    if (OrderOwnsUnit(uid, out string owner)) { Warn("rep-owner" + uid, $"{u.Name} (uid {uid}) touché ({last}% -> {hp}%) : pas de repli automatique ({owner} en cours)"); continue; }

                    bool dismounted = TryDismount(cmd, u, uid, pos, last, hp, now, out bool stopped);
                    // DEB stopped the vehicle (even if the unload failed): the retreat must be given again, even during the REP cooldown
                    if (!_retDefault.Value || (retreating && !stopped)) continue;
                    _nextRetreat[uid] = now + _retCooldown.Value;

                    // no explicit smoke here: the vanilla auto smoke (triggered by hits) respects the player's per-unit switch
                    var spotted = SpottedCached(gc, enemyTeam);
                    V3? away = null;
                    float best = 3000f;
                    foreach (var s in spotted)
                    {
                        float d = V3.Distance(pos, s.pos);
                        if (d < best) { best = d; away = (pos - s.pos).normalized; }
                    }
                    if (away == null && _moveDir.TryGetValue(uid, out var md)) away = -md;
                    if (away != null) { var ad = away.Value; ad.y = 0; if (ad.sqrMagnitude > 1e-4f) _moveDir[uid] = -ad.normalized; }   // a later unseen hit keeps backing off the same way
                    // after a dismount the unload order must stay: no cancel, the reverse is given once the passengers are out (WatchReverse)
                    if (!dismounted) CancelOne(cmd, uid, "cancel");
                    if (away == null) { Log($"{u.Name} (uid {uid}) touché ({last}% -> {hp}%) : arrêt"); continue; }
                    var dir = away.Value; dir.y = 0; dir = dir.normalized;
                    if (dismounted)
                    {
                        _revAfterUnload[uid] = (dir, now + 20f);
                        Log($"{u.Name} (uid {uid}) touché ({last}% -> {hp}%) : débarquement, recul ensuite");
                        continue;
                    }
                    bool reversed = false; string how = null;
                    try { reversed = TryReverse(u, pos, pos + dir * _retReverseDistance.Value, now, out how); }
                    catch (Exception e) { how = "erreur " + e.Message; CancelOne(cmd, uid, "cancel"); }
                    if (!reversed) { try { u.MoveTo(pos + dir * _retDistance.Value, 15, false); } catch (Exception e) { Warn("recul", "recul: " + e.Message); } }
                    Log($"{u.Name} (uid {uid}) touché ({last}% -> {hp}%) : {(reversed ? $"MARCHE ARRIÈRE {_retReverseDistance.Value} m" : $"demi-tour {_retDistance.Value} m ({how})")}");
                }
                catch (Exception e) { Warn("repli", $"repli uid {uid}: {e.Message}"); }
            }
        }

        static NetCmdService _ncs;
        static bool _ncsLooked;
        sealed class RevJob { public RevMoveCmd Cmd; public V3 Start, Dest; public float T0; }
        static readonly Dictionary<int, RevJob> _revJobs = new();
        static readonly Dictionary<int, (V3 dir, float until)> _revAfterUnload = new();

        /// Same native path as a co-op partner's reverse order: SetRemoteCommand cancels, pre-activates, enqueues and activates synchronously.
        static bool TryReverse(LuaUnit u, V3 pos, V3 dest, float now, out string why)
        {
            why = null;
            if (!_retReverse.Value) { why = "option désactivée"; return false; }
            if (!_ncsLooked)
            {
                _ncsLooked = true;
                _ncs = Mod.Svc<NetCmdService>();
                float d = -1, a = -1;
                try { d = MainCmdSys.AUTO_REVERSE_DISTANCE; a = MainCmdSys.AUTO_REVERSE_BACK_ANGLE; } catch { }
                Log($"recul : NetworkCommandService {(_ncs != null ? "trouvé" : "ABSENT -> demi-tour MoveTo")} ; marche arrière auto vanilla <= {d} m, angle {a}");
            }
            if (_ncs == null) { why = "service absent"; return false; }
            var e = u.Entity;
            var cmd = new RevMoveCmd(dest, new Il2CppSystem.Nullable<float>());   // identical to MainCommandSystem.GetReverseMoveCommand
            if (!cmd.IsValid(e)) { why = "ordre refusé"; return false; }        // SetRemoteCommand skips IsValid; vanilla activation checks it
            _ncs.SetRemoteCommand(ref e, new ICmd(cmd.Pointer));
            if (!cmd.IsRemoteCommand) { why = "destination refusée (zone/terrain)"; return false; }   // the flag is set only after PreActivate succeeded
            cmd.IsRemoteCommand = false;   // same state as a clicked order (the network send already happened in Activate)
            _revJobs[u.UID] = new RevJob { Cmd = cmd, Start = pos, Dest = dest, T0 = now };   // held wrapper keeps the command alive
            return true;
        }

        static void WatchReverse(GameController gc, float now)
        {
            var cmdBus = gc._GetEcsEventBus_k__BackingField?.Commands;
            // transports that dismounted: reverse once the passengers are out (a reverse given earlier would clear the unload order)
            if (_revAfterUnload.Count > 0)
                foreach (var kv in _revAfterUnload.ToList())
                {
                    int uid = kv.Key; var (dir, until) = kv.Value;
                    try
                    {
                        if (!_mineByUid.TryGetValue(uid, out var u) || u == null || !u.IsAlive()) { _revAfterUnload.Remove(uid); continue; }
                        bool empty = u.GetCargoUnitsCount() == 0, idle = u.IsIdle(), expired = now >= until;
                        if (!(empty && idle) && !expired) continue;
                        _revAfterUnload.Remove(uid);
                        var p = u.GetPosition();
                        bool reversed = false; string how = null;
                        if (empty && idle)
                        {
                            try { reversed = TryReverse(u, p, p + dir * _retReverseDistance.Value, now, out how); }
                            catch (Exception e) { how = "erreur " + e.Message; CancelOne(cmdBus, uid, "cancel"); }
                        }
                        else how = "débarquement trop long";
                        if (!reversed) { try { u.MoveTo(p + dir * _retDistance.Value, 15, false); } catch (Exception e) { Warn("recul", "recul: " + e.Message); } }   // queued: never cuts an unload in progress
                        Log($"{u.Name} (uid {uid}) après débarquement : {(reversed ? $"MARCHE ARRIÈRE {_retReverseDistance.Value} m" : $"demi-tour {_retDistance.Value} m ({how})")}");
                    }
                    catch (Exception e) { _revAfterUnload.Remove(uid); Warn("recul3", "recul après débarquement : " + e.Message); }
                }

            if (_revJobs.Count == 0) return;
            List<int> done = null;
            foreach (var kv in _revJobs)
            {
                var j = kv.Value; string end = null; string name = "?";
                try
                {
                    bool alive = _mineByUid.TryGetValue(kv.Key, out var u) && u != null && u.IsAlive();
                    if (alive) name = u.Name;
                    float moved = alive ? V3.Distance(u.GetPosition(), j.Start) : 0f;
                    if (!alive) end = "unité perdue";
                    else if (j.Cmd.WasCompleted) end = $"recul terminé : {moved:0} m en {now - j.T0:0} s";
                    else if (j.Cmd.WasCanceled) end = "recul interrompu (nouvel ordre, mort ou script)";
                    else if (moved < 2f)
                    {
                        int mob = 0; try { mob = u.GetBadConditionsCount(true); } catch { }
                        if (mob >= 2 && now - j.T0 > 5f) end = "recul impossible (mobilité rouge) : pas de demi-tour";
                        else if (now - j.T0 > (mob == 1 ? 10f : 5f))
                        {
                            end = "recul sans effet -> demi-tour";
                            CancelOne(cmdBus, kv.Key, "recul2c");   // the stalled reverse is still the current command: MoveTo would wait behind it
                            try { u.MoveTo(j.Dest, 15, false); } catch (Exception ex) { Warn("recul2", "recul (secours): " + ex.Message); }
                        }
                    }
                    if (end == null && now - j.T0 > 60f) end = "suivi abandonné (60 s)";
                }
                catch (Exception e) { end = "suivi impossible : " + e.Message; }
                if (end == null) continue;
                Log($"{name} (uid {kv.Key}) {end}");
                (done ??= new List<int>()).Add(kv.Key);
            }
            if (done != null) foreach (var k in done) _revJobs.Remove(k);
        }

        // ------------------------------------------------------------ DEB : débarquement quand le transport est touché
        static readonly Dictionary<int, float> _debNext = new();
        static readonly Dictionary<int, bool> _transportByUid = new();

        static bool IsTransport(LuaUnit u)
        {
            int uid = u.UID;
            if (!_transportByUid.TryGetValue(uid, out var t)) { try { t = u.CanBeTransport() != -1; } catch { t = false; } _transportByUid[uid] = t; }
            return t;
        }

        /// true when the squad was ordered out; stopped = the vehicle's commands were cancelled (even if the unload then failed).
        static bool TryDismount(EcsEventBus.CommandsBus cmd, LuaUnit u, int uid, V3 pos, int last, int hp, float now, out bool stopped)
        {
            stopped = false;
            if (!_debDefault.Value || !IsGroundTransportRole(u.UnitRole)) return false;
            if (last - hp < _debMinDrop.Value && hp > 50) return false;          // a stray MG burst is not a reason to drop the squad in the open
            if (_debNext.TryGetValue(uid, out var dn) && now < dn) return false;
            if (!IsTransport(u)) return false;
            int pax = 0, kg = 0;
            try { pax = u.GetCargoUnitsCount(); kg = u.GetSupplyCargo(); } catch { }
            if (pax <= 0) return false;
            _debNext[uid] = now + 15f;
            if (kg > 0) { Log($"DEB {u.Name} (uid {uid}) : {kg} kg de ravitaillement à bord, pas de débarquement"); return false; }
            stopped = true;
            CancelOne(cmd, uid, "debstop");
            bool ok;
            try { u.Unload(null, null); ok = true; }
            catch (Exception e) { Log("DEB Unload(Lua) refusé, bus: " + e.Message); ok = UnloadViaBus(cmd, uid, pos); }
            Log($"DEB {u.Name} (uid {uid}) touché ({last}% -> {hp}%) : débarquement de {pax} passager(s){(ok ? "" : " ÉCHEC")}");
            return ok;
        }

        static bool UnloadViaBus(EcsEventBus.CommandsBus cmd, int uid, V3 pos)
        {
            try
            {
                var d = new UnloadCommandData();
                d.UnitsList = Cheats.ToIl2Cpp(new List<int> { uid });
                d.UnitID = 0; d.Groups = ""; d.TargetPosTag = 0; d.TargetPosition = pos; d.Blocking = false; d.Queue = false;
                d.UnloadedUnitsBuffer = new Il2CppSystem.Collections.Generic.List<int>();
                cmd.UnloadCommand.Invoke(d);
                return true;
            }
            catch (Exception e) { Log("DEB bus: " + e.Message); return false; }
        }

        // ------------------------------------------------------------ EMBU : embuscade (tir retenu jusqu'à courte portée)
        static readonly HashSet<int> _ambHeld = new();
        static readonly Dictionary<int, float> _ambLastContact = new();
        static readonly Dictionary<int, int> _ambHp = new();
        static readonly Dictionary<int, (float ground, float air)> _dfRangeById = new();

        /// Longest direct-fire / missile ground and low-altitude range of the unit type (all loadouts), cached per DB unit id.
        static (float ground, float air) DirectRange(LuaUnit u)
        {
            int id = 0;
            try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            if (id > 0 && _dfRangeById.TryGetValue(id, out var r)) return r;
            float g = 0, a = 0;
            var src = DataBaseService._instance?.RawAccess;
            if (id > 0 && src != null)
                foreach (var aid in AmmoIdsOf(id))
                {
                    if (!src.Ammunitions.TryGetById(aid, out var am) || am == null || am.GenerateSmoke) continue;
                    int tt = (int)am.TrajectoryType;
                    if (tt != 10 && tt != 100 && tt != 110 && tt != 120 && tt != 600) continue;
                    g = Math.Max(g, am.GroundRange); a = Math.Max(a, am.LowAltRange);
                }
            if (g <= 0) g = 500f;
            var res = (g, a);
            if (id > 0) _dfRangeById[id] = res;
            return res;
        }

        static void Ambush(GameController gc, int myTeam, float now)
        {
            if (_ambHeld.Count == 0 && !_ambDefault.Value) return;
            var hf = gc._GetEcsEventBus_k__BackingField?.Commands?.HoldFireCommand;
            int enemyTeam = myTeam == 0 ? 1 : 0;
            List<int> hold = null, free = null;
            List<(V3 pos, int role)> spotted = null;
            foreach (var uid in _ambHeld.ToList())
                if (!_ambDefault.Value || !_mineByUid.ContainsKey(uid))
                {
                    _ambHeld.Remove(uid);
                    if (_mineByUid.ContainsKey(uid)) { (free ??= new()).Add(uid); Log($"EMBU uid {uid} : embuscade désactivée -> feu libre"); }
                }
            float f = Math.Clamp(_ambFraction.Value, 0.2f, 1f);
            foreach (var kv in _mineByUid)
            {
                int uid = kv.Key; var u = kv.Value;
                if (!_ambDefault.Value) break;
                try
                {
                    if (!IsGroundCombat(u.UnitRole) || !u.IsAlive()) continue;
                    bool aa = IsAaRole(u.UnitRole);
                    var rng = DirectRange(u);
                    spotted ??= SpottedCached(gc, enemyTeam);
                    var pos = u.GetPosition();
                    float bestG = float.MaxValue, bestA = float.MaxValue;
                    foreach (var s in spotted) { float d = V3.Distance(pos, s.pos); if (s.role < 0) { if (d < bestA) bestA = d; } else if (d < bestG) bestG = d; }
                    int hp = u.GetHealPercentage();
                    bool hit = _ambHp.TryGetValue(uid, out var lh) && hp < lh;
                    _ambHp[uid] = hp;
                    bool airIn = rng.air > 0 && bestA <= rng.air * f;
                    // pure AA ignores ground targets, except point-blank ones (a MANPADS squad must defend itself)
                    bool groundIn = (!aa && bestG <= rng.ground * f) || bestG <= 120f;
                    bool trigger = hit || airIn || groundIn;
                    if (hit || (rng.air > 0 && bestA <= rng.air * 1.1f) || (!aa && bestG <= rng.ground * 1.1f) || bestG <= 150f) _ambLastContact[uid] = now;
                    if (_ambHeld.Contains(uid))
                    {
                        if (!trigger) continue;
                        _ambHeld.Remove(uid); (free ??= new()).Add(uid);
                        Log($"EMBU {u.Name} (uid {uid}) : {(hit ? "touché" : airIn ? $"aérien à {bestA:0} m" : $"ennemi à {bestG:0} m")} (sol {rng.ground:0} / air {rng.air:0}) -> FEU");
                    }
                    else if (!trigger && (!_ambLastContact.TryGetValue(uid, out var lc) || now - lc > _ambRearm.Value))
                    {
                        _ambHeld.Add(uid); (hold ??= new()).Add(uid);
                        Log($"EMBU {u.Name} (uid {uid}) : tir retenu (ouverture {(aa ? "" : $"sol {rng.ground * f:0} m ")}{(rng.air > 0 ? $"air {rng.air * f:0} m" : "")})");
                    }
                }
                catch (Exception e) { Warn("emb", $"EMBU uid {uid}: {e.Message}"); }
            }
            SendHold(hf, hold, true);
            SendHold(hf, free, false);
        }

        static void SendHold(EcsEventBus.CommandsBus.HoldFireCommandDel hf, List<int> l, bool on)
        {
            if (l == null || l.Count == 0) return;
            try
            {
                if (hf == null) throw new InvalidOperationException("HoldFireCommand absent");
                hf.Invoke(Cheats.ToIl2Cpp(l), on);
            }
            catch (Exception e)
            {
                Log($"EMBU HoldFireCommand refusé ({e.Message}) : repli HoldFireSystem");
                foreach (var uid in l)
                    try { if (_mineByUid.TryGetValue(uid, out var u)) { var ent = u.Entity; HoldFireSys.SetHoldFireStatus(ref ent, on, false); } }
                    catch (Exception e2) { Warn("embfb", $"EMBU repli uid {uid}: {e2.Message}"); }
            }
        }

        // ------------------------------------------------------------ RAV : ravitaillement / réparation automatique en retrait
        enum RavStage { Going, Back }
        sealed class RavState { public RavStage Stage; public V3 Home, Target; public float Since, MinDist, LastGain, LastProgress, Arrived = -1f; public int Ammo0, Hp0; public LuaUnit Truck; }
        static readonly Dictionary<int, RavState> _rav = new();
        static readonly Dictionary<int, float> _ravNext = new();
        static float _ravNotifyUntil;

        static void Logistics(GameController gc, int myTeam, float now)
        {
            if (_rav.Count == 0 && !_ravDefault.Value) return;
            int enemyTeam = myTeam == 0 ? 1 : 0;
            List<(V3 pos, int role)> spotted = null;
            foreach (var id in _rav.Keys.ToList()) if (!_mineByUid.ContainsKey(id) || !_ravDefault.Value) _rav.Remove(id);
            foreach (var kv in _mineByUid)
            {
                int uid = kv.Key; var u = kv.Value;
                if (!_ravDefault.Value) break;
                try
                {
                    if (!IsRavRole(u.UnitRole) || !u.IsAlive()) continue;
                    int ammo = u.GetAmmoPercentage(true, false), hp = u.GetHealPercentage();
                    var pos = u.GetPosition();
                    bool idle = u.IsIdle();
                    if (_rav.TryGetValue(uid, out var st)) { StepRav(gc, enemyTeam, u, uid, st, pos, idle, ammo, hp, now, ref spotted); continue; }
                    if (!idle || (_ravNext.TryGetValue(uid, out var nx) && now < nx)) continue;
                    bool needAmmo = ammo < _ravAmmo.Value, needHp = hp < _ravHp.Value;
                    if (!needHp) needHp = u.GetBadConditionsCount(true) >= 2;
                    if (!needAmmo && !needHp) continue;
                    spotted ??= SpottedCached(gc, enemyTeam);
                    if (spotted.Any(s => s.role >= 0 && (s.pos - pos).sqrMagnitude < 700f * 700f)) continue;
                    _ravNext[uid] = now + 90f;
                    _map ??= new LuaMap();
                    var depot = _map.GetNearestSupplyPoint(pos, _ravRange.Value, myTeam, -1);
                    if (depot != null && depot.IsAlive() && depot.GetSupplyAmount() > 0)
                    {
                        var dp = depot.Position; int kg = depot.GetSupplyAmount(), duid = depot.UID;
                        u.ResupplyAtDepot(depot);
                        _rav[uid] = new RavState { Stage = RavStage.Going, Home = pos, Target = dp, Since = now, MinDist = V3.Distance(pos, dp), Ammo0 = ammo, Hp0 = hp, LastGain = now, LastProgress = now };
                        Log($"RAV {u.Name} (uid {uid}) : munitions {ammo}% santé {hp}% -> dépôt uid {duid} à {V3.Distance(pos, dp):0} m (stock {kg} kg)");
                        continue;
                    }
                    LuaUnit truck = null; float bd = _ravRange.Value;
                    foreach (var t in _mineByUid.Values)
                    {
                        try { if (t.UID == uid || t.GetSupplyCargo() <= 0) continue; float d = V3.Distance(pos, t.GetPosition()); if (d < bd) { bd = d; truck = t; } } catch { }
                    }
                    if (truck == null)
                    {
                        _ravNext[uid] = now + 300f;
                        Warn("ravnone", $"RAV {u.Name} : besoin (munitions {ammo}%, santé {hp}%) mais aucun ravitaillement à moins de {_ravRange.Value:0} m");
                        continue;
                    }
                    var tp = truck.GetPosition();
                    u.MoveTo(tp, 30, false);
                    _rav[uid] = new RavState { Stage = RavStage.Going, Home = pos, Target = tp, Since = now, MinDist = bd, Ammo0 = ammo, Hp0 = hp, LastGain = now, LastProgress = now, Truck = truck };
                    Log($"RAV {u.Name} (uid {uid}) : munitions {ammo}% santé {hp}% -> camion {truck.Name} (uid {truck.UID}) à {bd:0} m");
                }
                catch (Exception e) { Warn("rav", $"RAV uid {uid}: {e.Message}"); }
            }
        }

        static void StepRav(GameController gc, int enemyTeam, LuaUnit u, int uid, RavState st, V3 pos, bool idle, int ammo, int hp, float now, ref List<(V3 pos, int role)> spotted)
        {
            if (st.Stage == RavStage.Back)
            {
                if (now - st.Since > 5f && (idle || now - st.Since > 180f)) { _rav.Remove(uid); Log($"RAV {u.Name} (uid {uid}) : retour terminé"); }
                return;
            }
            bool truckGone = false;
            if (st.Truck != null) { try { truckGone = !st.Truck.IsAlive() || (st.Truck.GetPosition() - st.Target).sqrMagnitude > 300f * 300f; } catch { truckGone = true; } }
            float d = V3.Distance(pos, st.Target);
            if (d < st.MinDist - 20f) st.LastProgress = now;
            if (d < st.MinDist) st.MinDist = d;
            if (ammo > st.Ammo0 || hp > st.Hp0) { st.LastGain = now; st.Ammo0 = Math.Max(st.Ammo0, ammo); st.Hp0 = Math.Max(st.Hp0, hp); }
            bool near = d < (st.Truck != null ? 300f : 400f);
            if (truckGone || (!idle && d > st.MinDist + 250f) || (idle && !near && now - st.Since > 10f))
            {
                _rav.Remove(uid);
                Log($"RAV {u.Name} (uid {uid}) : {(truckGone ? "camion parti" : "ordre du joueur")}, abandon");
                return;
            }
            spotted ??= SpottedCached(gc, enemyTeam);
            if (spotted.Any(s => s.role >= 0 && (s.pos - pos).sqrMagnitude < 400f * 400f))
            {
                _rav.Remove(uid);
                Log($"RAV {u.Name} (uid {uid}) : ennemi repéré à moins de 400 m, abandon (aucun ordre donné)");
                if (now >= _ravNotifyUntil) { _ravNotifyUntil = now + 20f; Mod.Notify(new TxtMsg(TxtKey.N_RAV_INTERRUPTED, u.Name)); }
                return;
            }
            bool gained = st.LastGain > st.Since;
            if (idle && near) { if (st.Arrived < 0) st.Arrived = now; } else st.Arrived = -1f;
            // stopped near the supply without gaining anything: a player order or a source that cannot help (no repair) -> leave it alone
            if (idle && near && !gained && now - st.Arrived > (st.Truck == null ? 10f : 40f))
            {
                _rav.Remove(uid); _ravNext[uid] = now + 900f;
                Log($"RAV {u.Name} (uid {uid}) : arrêt sans gain près du ravitaillement, abandon (aucun ordre)");
                return;
            }
            bool done = st.Truck == null ? (idle && near && gained && now - st.Arrived > 3f)
                                         : (idle && near && gained && ((ammo >= 90 && hp >= 90) || now - Math.Max(st.LastGain, st.Arrived) > 40f));
            if (!done)
            {
                if (now - Math.Max(st.LastProgress, st.LastGain) > 240f)
                {
                    _rav.Remove(uid); _ravNext[uid] = now + 300f;
                    Log($"RAV {u.Name} (uid {uid}) : plus de progrès depuis 4 min, suivi abandonné (aucun ordre)");
                }
                return;
            }
            u.MoveTo(st.Home, 20, false);
            Log($"RAV {u.Name} (uid {uid}) : ravitaillé (munitions {ammo}%, santé {hp}%) -> retour à {V3.Distance(pos, st.Home):0} m");
            st.Stage = RavStage.Back; st.Since = now;
            // a need the source could not fix must not start an endless shuttle
            if (ammo < _ravAmmo.Value || hp < _ravHp.Value || u.GetBadConditionsCount(true) >= 2) _ravNext[uid] = now + 900f;
        }

        // ------------------------------------------------------------ fumigène défensif automatique (fonction vanilla) et options par défaut
        const float SmokeDelayMin = 2f, SmokeDelayMax = 4f;

        static void SmokeDefault(GameController gc)
        {
            var gp = gc._GetEcsEventBus_k__BackingField?.Gameplay;
            if (gp == null) return;
            if (_smokeDefault.Value && !_smokeTriggersSet)
            {
                _smokeTriggersSet = true;
                var bs = GameConfig.Instance?.BattleSystemSettings;
                if (bs != null)
                {
                    // 2-4 s before the smoke goes off, same as the enemy AI (2026-09-16): a screen takes seconds to form, so smoke
                    // stays a real defence against a 10-20 s missile flight without making every helicopter missile miss
                    int n = Realism.SetJournaled(bs, "AUTOSMOKE_TRIGGER_BY_MISSILES", true) + Realism.SetJournaled(bs, "AUTOSMOKE_TRIGGER_BY_GUNS", true)
                          + Realism.SetJournaled(bs, "AUTOSMOKE_DELAY_MIN", SmokeDelayMin) + Realism.SetJournaled(bs, "AUTOSMOKE_DELAY_MAX", SmokeDelayMax);
                    Log($"fumigène auto : déclenché par missiles et tirs, après {SmokeDelayMin:0.#} à {SmokeDelayMax:0.#} s ({n} réglages)");
                }
            }
            var fresh = new List<int>();
            foreach (var kv in _mineByUid)
            {
                if (_smokeDone.Contains(kv.Key)) continue;
                try { if (!kv.Value.IsAlive()) continue; } catch { continue; }   // a unit dead since the last refresh is simply skipped
                _smokeDone.Add(kv.Key);
                fresh.Add(kv.Key);
            }
            if (fresh.Count == 0 || !_smokeDefault.Value) return;
            gp.UnitAbility?.Invoke(Cheats.ToIl2Cpp(fresh), 0, "", NodeAbilityEnum.Smoke, true, false);
            Log($"fumigène automatique activé sur {fresh.Count} nouvelle(s) unité(s)");
        }

        // ------------------------------------------------------------ données de la base : munitions par unité (index construit une fois par base)
        static Dictionary<int, List<int>> _ammoByUnit;
        static IntPtr _ammoSrc;
        static readonly List<int> _noAmmo = new();

        static List<int> AmmoIdsOf(int unitId)
        {
            var src = DataBaseService._instance?.RawAccess;
            if (src == null) return _noAmmo;
            if (_ammoByUnit == null || _ammoSrc != src.Pointer)
            {
                _ammoByUnit = new Dictionary<int, List<int>>();
                _ammoSrc = src.Pointer;
                foreach (var wa in Props.Rows(src.WeaponAmmunitions.GetAll()))
                {
                    if (!_ammoByUnit.TryGetValue(wa.UnitId, out var l)) _ammoByUnit[wa.UnitId] = l = new List<int>();
                    l.Add(wa.AmmunitionId);
                }
            }
            return _ammoByUnit.TryGetValue(unitId, out var r) ? r : _noAmmo;
        }

        // ------------------------------------------------------------ « les yeux sur l'objectif » : qui de tes unités regarde la zone visée
        // The artillery ALWAYS fires, however far the target is: the rule only moves the centre of the sheaf.
        //   somebody watches  -> the salvo is on the point and tightens salvo after salvo (somebody can correct the fire)
        //   nobody watches    -> the salvo lands wide and NEVER tightens (nobody can correct it)
        // The snapshot is filled inside RefreshUnits (1 Hz, Planif slot), which already walks every unit of the player: no extra pass.
        // Honest limit, to be told to the player: the sight is a flat distance against Sensors.OpticsGround. Relief, woods and smoke are
        // ignored, so the mod OVER-estimates what he sees: it is too kind, never too harsh.
        struct Eye
        {
            public V3 Pos;
            public float Vue;                                                // ground optics of the unit (m), 0 = unreadable
            public float Laser;                                              // laser designator range usable RIGHT NOW (m), 0 = none
            public int Role, Uid;
            public bool Desig;                                               // carries a designator (even one it cannot use while moving)
        }

        const int EYES_MAX = 512;                                            // snapshot ceiling: a battle never has more friendly units than that
        const float EYE_CAP_OBS = 40f, EYE_CAP_SEEN = 120f, EYE_FLOOR_BLIND = 80f, EYE_CAP_BLIND = 400f, EYE_ZERO = 15f;
        const float EYE_BLIND_PENALTY = 1500f;                               // sorting only: at equal range the pairing prefers a watched target
        const float EYE_FRIEND_SAFE = 250f;                                  // the aim point may never be pushed closer than this to one of MY OWN units
        const float EYE_SILENCE = 180f;                                      // CB: how long a point beaten for nothing is left alone
        const int EYE_BLIND_MAX = 3;                                         // CB: blind salvoes before that silence

        static readonly List<Eye> _eyes = new();
        static readonly Dictionary<int, int> _typeByEyeUid = new();          // unit UID -> DB unit id (spares 3 interop calls per unit per second)
        static readonly Dictionary<int, float> _sightByUnitId = new();       // DB unit id -> largest OpticsGround
        static readonly Dictionary<int, (float range, bool inMove)> _laserByUnitId = new();
        static readonly Dictionary<int, (int n, float last)> _favSalvos = new();   // enemy UID -> salvoes already landed on him
        static int _eyeErrors;
        static bool _eyeArmed = true, _eyeNotified;

        /// The player asked for the rule AND it has not been switched off by its own error counter.
        static bool EyeOn => Enabled != null && Enabled.Value && _eyeOn != null && _eyeOn.Value && _eyeArmed;

        /// Roles whose whole job is to look: scouts, snipers, special forces, scout helicopters and drones.
        /// Filtered by ROLE, never by the Units.Type bits, so drones (role 100) are not lost.
        static bool IsObserverRole(int r) => r == ROLE_RECONINF || r == ROLE_SNIPERS || r == ROLE_SPECFORCES || r == ROLE_RECONHELI || r == ROLE_DRONE;

        /// One line of the sight snapshot for a live unit of the player. Planes only pass over: they never count as an observer.
        static void AddEye(LuaUnit u)
        {
            int role;
            try { role = u.UnitRole; } catch { return; }
            if (role >= ROLE_PLANE_MIN && role <= ROLE_PLANE_MAX) return;
            if (_eyes.Count >= EYES_MAX) return;
            int uid = 0;
            try { uid = u.UID; } catch { return; }
            if (!_typeByEyeUid.TryGetValue(uid, out int id))
            {
                id = 0;
                try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
                if (_typeByEyeUid.Count < 5000) _typeByEyeUid[uid] = id;
            }
            float vue = SightOf(id);
            var las = LaserOf(id);
            if (vue <= 0f && las.range <= 0f) return;                        // sees nothing and designates nothing: useless in the snapshot
            float laser = 0f;
            if (las.range > 0f)
            {
                bool ok = las.inMove;
                if (!ok) { try { ok = u.IsIdle(); } catch { ok = false; } }  // a designator that cannot work on the move must be stopped
                if (ok) laser = las.range;
            }
            V3 pos;
            try { pos = u.GetPosition(); } catch { return; }
            _eyes.Add(new Eye { Pos = pos, Vue = vue, Laser = laser, Role = role, Uid = uid, Desig = las.range > 0f });
        }

        /// Ground sight of a unit type (largest Sensors.OpticsGround); 0 = unreadable, that unit then never gives sight of anything.
        static float SightOf(int id)
        {
            if (id <= 0) return 0f;
            if (_sightByUnitId.TryGetValue(id, out var v)) return v;
            v = 0f;
            try
            {
                var src = DataBaseService._instance?.RawAccess;
                if (src != null && src.Units.TryGetById(id, out var row) && row != null)
                {
                    var list = row.Sensors;
                    if (list != null) for (int i = 0; i < list.Count; i++) { var s = list[i]; if (s != null) v = Math.Max(v, s.OpticsGround); }
                }
            }
            catch { v = 0f; }
            _sightByUnitId[id] = v;
            return v;
        }

        /// Laser designator of a unit type: largest range, and whether it still works while the unit moves.
        static (float range, bool inMove) LaserOf(int id)
        {
            if (id <= 0) return (0f, false);
            if (_laserByUnitId.TryGetValue(id, out var v)) return v;
            float range = 0f; bool inMove = false;
            try
            {
                var src = DataBaseService._instance?.RawAccess;
                if (src != null && src.Units.TryGetById(id, out var row) && row != null)
                {
                    ReadLaser(row.DefaultAbilities, ref range, ref inMove);
                    var list = row.Abilities;
                    if (list != null) for (int i = 0; i < list.Count; i++) ReadLaser(list[i], ref range, ref inMove);
                }
            }
            catch { range = 0f; inMove = false; }
            v = (range, inMove);
            _laserByUnitId[id] = v;
            return v;
        }

        static void ReadLaser(AbilityRow a, ref float range, ref bool inMove)
        {
            if (a == null) return;
            try
            {
                if (!a.IsLaserDesignator) return;
                float r = a.LaserMaxRange;
                if (r <= 0f) return;
                if (r > range) range = r;
                if (a.LaserUsableInMove) inMove = true;
            }
            catch { }
        }

        /// Observation class of a point: 3 designated by laser, 2 an observer's eye on it, 1 simply inside a friendly unit's sight, 0 blind.
        /// byUid / byDist = the unit that gives the best class and its distance to the point, so the log can name it (0 = none).
        /// On ANY doubt (rule off, empty snapshot, read error) the answer is 1, never 0: the mod is kind when it does not know.
        /// selfUid = the enemy the point IS (feu à volonté): he is skipped in the "an enemy is spotted near the point" refinement,
        /// otherwise the target sits 0 m from itself and class 0 could never happen on that side.
        static int Oeil(V3 p, VisResult vis, out int byUid, out float byDist, int selfUid = 0)
        {
            byUid = 0; byDist = 0f;
            if (!EyeOn || _eyes.Count == 0) return 1;
            try
            {
                int best = 0; float bestD2 = float.MaxValue;
                // squared flat distances only: the square root is taken once, on the unit finally shown in the log
                for (int i = 0; i < _eyes.Count; i++)
                {
                    var e = _eyes[i];
                    float dx = e.Pos.x - p.x, dz = e.Pos.z - p.z, d2 = dx * dx + dz * dz;
                    if (e.Laser > 0f && d2 <= e.Laser * e.Laser)             // designated: nothing can be better
                    {
                        byUid = e.Uid; byDist = (float)Math.Sqrt(d2);
                        return 3;
                    }
                    if (e.Vue > 0f && d2 <= e.Vue * e.Vue)
                    {
                        int c = e.Desig || IsObserverRole(e.Role) ? 2 : 1;
                        // the best class wins; at equal class the nearest one is the one shown
                        if (c > best || (c == best && d2 < bestD2)) { best = c; bestD2 = d2; byUid = e.Uid; }
                    }
                }
                if (best > 0) byDist = (float)Math.Sqrt(bestD2);
                // free refinement: an enemy your side has really spotted next to the point means your side sees something there
                if (best == 0 && EnemyNear(vis, p, selfUid)) { best = 1; byUid = 0; }
                return best;
            }
            catch (Exception ex)
            {
                byUid = 0; byDist = 0f;
                if (++_eyeErrors >= 20)
                {
                    _eyeArmed = false;
                    Log("artillerie : trop d'erreurs dans la règle des observateurs, elle est coupée jusqu'à la fin de la bataille (l'artillerie tire comme avant)");
                }
                Warn("oeil", "artillerie : lecture des observateurs impossible : " + ex.Message);
                return 1;
            }
        }

        /// An enemy your side has really spotted, inside the zone around the aimed point (ground units only).
        static bool EnemyNear(VisResult vis, V3 p, int selfUid)
        {
            if (vis == null || !vis.Usable || vis.Units.Count == 0) return false;
            float r = _eyeZone != null ? Math.Max(0f, _eyeZone.Value) : 150f;
            if (r <= 0f) return false;
            float r2 = r * r;
            for (int i = 0; i < vis.Units.Count; i++)
            {
                var s = vis.Units[i];
                if (s.role < 0) continue;
                if (selfUid != 0 && s.uid == selfUid) continue;               // the target does not prove it is being watched
                if ((s.pos - p).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        /// Error radius asked for this salvo, in metres: how far from the point the centre of the sheaf may sit.
        /// Blind fire never tightens, whatever the number of salvoes: nobody is there to correct it.
        static float Ecart(int cls, float dist, int salvos)
        {
            if (cls >= 3) return 0f;
            float e;
            if (cls == 2) e = Math.Min(_eyeObs.Value * dist, EYE_CAP_OBS) * Half(salvos);
            else if (cls == 1) e = Math.Min(_eyeSeen.Value * dist, EYE_CAP_SEEN) * Half(salvos);
            else
            {
                e = _eyeBlind.Value * dist;
                if (e < EYE_FLOOR_BLIND) e = EYE_FLOOR_BLIND;
                if (e > EYE_CAP_BLIND) e = EYE_CAP_BLIND;
            }
            return e <= EYE_ZERO ? 0f : e;                                   // anything under 15 m is "pile dessus", as the CB already did
        }

        /// 1 / 2^n without Math.Pow (n salvoes already landed on the same point).
        static float Half(int n) => n <= 0 ? 1f : n >= 16 ? 0f : 1f / (1 << n);

        /// What the player reads in the log for each class.
        static string EyeName(int cls) => cls >= 3 ? "tir désigné au laser" : cls == 2 ? "observateur sur zone" : cls == 1 ? "zone tenue à vue" : "aucune unité à toi ne voit la zone";

        /// Which of his units gives sight of the point, for the log; empty when it is an enemy marker or nobody in particular.
        static string EyeWho(int uid, float dist)
        {
            if (uid == 0) return "";
            string name = "?";
            try { if (_mineByUid.TryGetValue(uid, out var u) && u != null) name = u.Name; } catch { }
            return $" — {name} à {dist:0} m";
        }

        static int SalvesSur(int enemyUid) => _favSalvos.TryGetValue(enemyUid, out var v) ? v.n : 0;

        static void BumpFavSalvo(int enemyUid, float now)
        {
            int n = _favSalvos.TryGetValue(enemyUid, out var v) ? v.n + 1 : 1;
            _favSalvos[enemyUid] = (n, now);
        }

        /// One on-screen notice per battle, the first time a salvo really leaves without anybody watching the area.
        static void NotifyBlindOnce()
        {
            if (_eyeNotified) return;
            _eyeNotified = true;
            try { Mod.Notify(TxtKey.N_ARTY_NO_EYES); } catch { }
        }

        // ------------------------------------------------------------ contre-batterie / feu à volonté
        static readonly Dictionary<int, float> _minRangeByUnitId = new();
        static readonly Dictionary<int, bool> _longByUnitId = new();

        static readonly Dictionary<int, AmmoTypeEnum> _ammoTypeByUnitId = new();
        static readonly Dictionary<int, bool> _missileByUnitId = new();
        // Unit types whose range line is already in the log for this battle. OublierPortees() does NOT clear it: the ranges are read again,
        // but the same line is not written once more at every zone change (the selection log still prints the current range and minimum).
        static readonly HashSet<int> _porteeLogged = new();
        static bool _porteesOubliLogged;                                     // the "forgotten ranges" line is written once per battle, not at every call

        /// Range of the unit's indirect-fire rounds (all loadouts are listed in the DB, whatever the unit carries).
        /// A piece with at least one artillery / mortar / MLRS round (trajectory 20/30/40) uses the shortest of those rounds and their minimum,
        /// so no order is given out of range (HIMARS / M270 included, even if they can also carry missiles).
        /// Only a launcher with missiles alone (trajectory 200/300), or role 132, counts as long range: it then uses its missile range.
        /// The ammo type sent with the order is Guided when every round used for that range is laser guided, else Basic.
        internal static float RangeOf(LuaUnit u, out float min, out bool longRange)
        {
            int id = 0;
            try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            int role = 0;
            try { role = u.UnitRole; } catch { }
            if (id > 0 && _rangeByUnitId.TryGetValue(id, out var r))
            {
                min = _minRangeByUnitId.GetValueOrDefault(id, 80f);
                longRange = _longByUnitId.GetValueOrDefault(id) || IsLongRange(role);
                return r;
            }
            float conv = 0, bal = 0, convMin = 80f, balMin = 80f;
            int nConv = 0, nConvGuided = 0, nMissile = 0, nMissileGuided = 0;
            var src = DataBaseService._instance?.RawAccess;
            if (id > 0 && src != null)
            {
                foreach (var aid in AmmoIdsOf(id))
                {
                    if (!src.Ammunitions.TryGetById(aid, out var a) || a == null || a.GenerateSmoke || a.GroundRange <= 0) continue;
                    int tt = (int)a.TrajectoryType;
                    bool guided = false;
                    try { guided = a.LaserGuided; } catch { }
                    if (tt == 200 || tt == 300)                                                           // cruise / ballistic missile
                    {
                        nMissile++; if (guided) nMissileGuided++;
                        bal = Math.Max(bal, a.GroundRange);
                        if (a.MinimalRange > balMin) balMin = a.MinimalRange;
                    }
                    else if (tt == 20 || tt == 30 || tt == 40)                                            // artillery / mortar / MLRS
                    {
                        nConv++; if (guided) nConvGuided++;
                        conv = conv <= 0 ? a.GroundRange : Math.Min(conv, a.GroundRange);
                        if (a.MinimalRange > convMin) convMin = a.MinimalRange;
                    }
                }
            }
            bool lr = conv <= 0 && bal > 0;
            float best = conv > 0 ? conv : bal;
            min = conv > 0 ? convMin : balMin;
            var ammo = conv > 0 ? (nConvGuided == nConv ? AmmoTypeEnum.Guided : AmmoTypeEnum.Basic)
                                : (nMissile > 0 && nMissileGuided == nMissile ? AmmoTypeEnum.Guided : AmmoTypeEnum.Basic);
            // no known indirect-fire round: range 0 = never an automatic fire order (a guessed range could send a target out of range and make the piece move)
            if (best <= 0) { best = 0f; min = 80f; lr = false; }
            if (id > 0)
            {
                _rangeByUnitId[id] = best; _minRangeByUnitId[id] = min; _longByUnitId[id] = lr;
                _ammoTypeByUnitId[id] = ammo; _missileByUnitId[id] = nMissile > 0;
                if (best > 0 && _porteeLogged.Add(id))
                {
                    string name = "?"; try { name = u.Name; } catch { }
                    Log($"portée {name} (type {id}, rôle {role}) : {best:0} m, minimum {min:0} m, {(lr || IsLongRange(role) ? "longue portée (missiles seuls)" : "artillerie")}, munition {ammo} ({nConv} obus/roquettes dont {nConvGuided} guidés, {nMissile} missiles dont {nMissileGuided} guidés)");
                }
            }
            longRange = lr || IsLongRange(role);
            return best;
        }

        /// Ammo type for an automatic order of this unit (see RangeOf).
        static AmmoTypeEnum AmmoTypeOf(LuaUnit u)
        {
            RangeOf(u, out _, out _);
            int id = 0;
            try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            return id > 0 && _ammoTypeByUnitId.TryGetValue(id, out var t) ? t : AmmoTypeEnum.Basic;
        }

        /// True when the unit type lists any cruise / ballistic missile. EnemyAi keeps such launchers out of its preparation fires, as it always did.
        internal static bool CarriesMissiles(LuaUnit u)
        {
            RangeOf(u, out _, out _);
            int id = 0;
            try { id = u.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            return id > 0 && _missileByUnitId.GetValueOrDefault(id);
        }

        /// Forget every weapon range cached per unit type, so the next evaluation reads the database again.
        /// PorteeMiniCarte lowers the ammunition MINIMAL ranges while the playable zone is small and gives the real values back when it
        /// grows: it calls this at every zone change. Without it the assistants would keep refusing targets that are back in range, and
        /// would order fire the game refuses once the real minimums are back. Only the ranges go: the per-battle counters, the error
        /// counters and the kill-switches are untouched, and a call outside a battle simply empties dictionaries that are already empty.
        internal static void OublierPortees()
        {
            try
            {
                // RangeOf() takes _rangeByUnitId as its cache key and writes the five entries in one go, so the five are dropped together.
                _rangeByUnitId.Clear(); _minRangeByUnitId.Clear(); _longByUnitId.Clear();
                _ammoTypeByUnitId.Clear(); _missileByUnitId.Clear();
                _dfRangeById.Clear();                                        // direct fire (EMBU / GAR / RZ): read from the same ammunition rows
                if (!_porteesOubliLogged)
                {
                    _porteesOubliLogged = true;
                    Log("portées oubliées : les distances de tir en cache seront relues dans la base (la zone de jeu a changé)");
                }
            }
            catch { }
        }

        static readonly Dictionary<int, bool> _airById = new();

        /// Helicopters, drones and planes: artillery can't hit them.
        internal static bool IsAirUnit(LuaUnit e)
        {
            int r = e.UnitRole;
            if ((r >= 70 && r <= 73) || r == 100 || (r >= 160 && r <= 164)) return true;
            int id = 0;
            try { id = e.SpawnData?.Unit?.UnitID ?? 0; } catch { }
            if (id <= 0) return false;
            if (_airById.TryGetValue(id, out var air)) return air;
            var src = DataBaseService._instance?.RawAccess;
            air = src != null && src.Units.TryGetById(id, out var row) && row != null && (((int)row.Type) & (8 | 16)) != 0;   // UnitType Helicopter | Aircraft
            _airById[id] = air;
            return air;
        }

        /// Enemy units currently spotted by my team (never unseen units), with their role (-1 = aircraft).
        /// Visibility validates its source per battle: while nothing is validated the list is empty (no retreat direction, no FAV target).
        static List<(V3 pos, int role)> Spotted(GameController gc, int enemyTeam)
        {
            var res = new List<(V3, int)>();
            var v = SpottedInfo(gc, enemyTeam);
            if (!v.Usable) return res;
            foreach (var s in v.Units) res.Add((s.pos, s.role));
            return res;
        }

        // Hold / BlindSalvos / SilentUntil: the graduated brakes on blind counter-battery fire (see "les yeux sur l'objectif").
        sealed class CbSpot { public V3 Pos; public int Salvos, BlindSalvos; public float LastSalvo = -999f, LastSeen, Hold, SilentUntil; }
        static readonly List<CbSpot> _cbSpots = new();

        static int _cbKey = -1;                                                           // key of GetDetectedCBTargets holding ENEMY fire spots (-1 = not proven yet)
        static readonly HashSet<long>[] _cbNearEnemy = { new(), new() }, _cbNearOwn = { new(), new() };
        static float _nextCbDiag;

        static long Cell(V3 p) => ((long)UnityEngine.Mathf.FloorToInt(p.x / 100f) << 32) ^ (uint)UnityEngine.Mathf.FloorToInt(p.z / 100f);

        static List<V3> TeamPositions(int team)
        {
            var res = new List<V3>();
            _map ??= new LuaMap();
            var arr = _map.GetUnits(V3.zero, 1_000_000f, team, -1);
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                var e = arr[i];
                try { if (e != null && e.IsAlive()) res.Add(e.GetPosition()); } catch { }
            }
            return res;
        }

        static float NearestDist(List<V3> pts, V3 p)
        {
            float best = 99999f;
            foreach (var q in pts) { float d = V3.Distance(p, q); if (d < best) best = d; }
            return best;
        }

        // Positions of every live unit of MY TEAM (scripted allies included), refreshed at most once a second and kept in the same
        // list: it is the only thing that tells the firing error how close it may push a salvo to my own troops.
        static readonly List<V3> _friendPos = new();
        static float _friendPosAt = -999f;
        static int _friendTeam = -1;

        static void RefreshFriendPos(int myTeam, float now)
        {
            if (myTeam < 0) return;
            // "now" is game time and starts again at the next battle: a negative age means a new battle, never a fresh snapshot
            float age = now - _friendPosAt;
            if (_friendTeam == myTeam && age >= 0f && age < 1f && _friendPos.Count > 0) return;
            _friendTeam = myTeam; _friendPosAt = now;
            _friendPos.Clear();
            try
            {
                _map ??= new LuaMap();
                var arr = _map.GetUnits(V3.zero, 1_000_000f, myTeam, -1);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    var e = arr[i];
                    try { if (e != null && e.IsAlive()) _friendPos.Add(e.GetPosition()); } catch { }
                }
            }
            catch (Exception e) { Warn("amis", "artillerie : positions de tes unités illisibles : " + e.Message); }
        }

        /// Enemy fire positions (known as soon as a shot is fired, even when the battery is not spotted), grouped per battery.
        /// Both team keys are read; the key whose spots sit on enemy units is kept once proven. Spots within 300 m of my side's units are always ignored.
        static void UpdateCbSpots(int myTeam, int enemyTeam, float now)
        {
            _ai ??= new LuaAI();
            var friends = TeamPositions(myTeam);
            var foes = TeamPositions(enemyTeam);                     // only to tell which key holds enemy fire spots: never a target
            var spots = new List<V3>[2];
            for (int k = 0; k < 2; k++)
            {
                spots[k] = new List<V3>();
                try
                {
                    var arr = _ai.GetDetectedCBTargets(k);
                    for (int i = 0; i < (arr?.Length ?? 0); i++) spots[k].Add(arr[i]);
                }
                catch (Exception e) { Warn("cbread" + k, $"CB : lecture des tirs détectés (clé {k}) impossible : {e.Message}"); }
                foreach (var p in spots[k])
                {
                    float dOwn = NearestDist(friends, p), dFoe = NearestDist(foes, p);
                    if (dOwn <= 300f) _cbNearOwn[k].Add(Cell(p));
                    else if (dFoe <= 300f) _cbNearEnemy[k].Add(Cell(p));
                }
            }
            if (_cbKey < 0)
            {
                for (int k = 0; k < 2; k++)
                {
                    if (_cbNearEnemy[k].Count < 2 || _cbNearEnemy[k].Count <= 2 * _cbNearEnemy[1 - k].Count) continue;
                    _cbKey = k;
                    Log($"CB : tirs ennemis lus sous la clé {k} ({_cbNearEnemy[k].Count} position(s) sur des unités ennemies, {_cbNearOwn[k].Count} près des tiennes ; autre clé {_cbNearEnemy[1 - k].Count} / {_cbNearOwn[1 - k].Count})");
                    break;
                }
            }
            DiagCb(spots, friends, foes, enemyTeam, now);
            for (int k = 0; k < 2; k++)
            {
                if (_cbKey >= 0 && k != _cbKey) continue;
                foreach (var p in spots[k])
                {
                    if (NearestDist(friends, p) < 300f) continue;                     // never near my side's units
                    if (_cbKey < 0 && NearestDist(foes, p) > 300f) continue;          // key not proven yet: only spots that sit on enemy units
                    CbSpot best = null; float bd = 200f;
                    foreach (var s in _cbSpots) { float d = V3.Distance(s.Pos, p); if (d < bd) { bd = d; best = s; } }
                    if (best == null) _cbSpots.Add(new CbSpot { Pos = p, LastSeen = now });
                    else { best.Pos = p; best.LastSeen = now; }
                }
            }
            // a battery that stopped firing (or moved far away) is forgotten: a new position starts again with a wide first salvo
            _cbSpots.RemoveAll(s => now - s.LastSeen > 60f || NearestDist(friends, s.Pos) < 300f);
        }

        /// Fire spots of both keys with the distance to the nearest unit of each side, only while an enemy artillery piece is busy (at most every 30 s).
        static void DiagCb(List<V3>[] spots, List<V3> friends, List<V3> foes, int enemyTeam, float now)
        {
            if (now < _nextCbDiag) return;
            string busy = null;
            _map ??= new LuaMap();
            var arr = _map.GetUnits(V3.zero, 1_000_000f, enemyTeam, -1);
            for (int i = 0; i < (arr?.Length ?? 0) && busy == null; i++)
            {
                var e = arr[i];
                try { if (e != null && e.IsAlive() && IsArtillery(e.UnitRole) && !e.IsIdle()) busy = $"{e.Name} uid {e.UID}"; } catch { }
            }
            if (busy == null) { _nextCbDiag = now + 5f; return; }
            _nextCbDiag = now + 30f;
            var sb = new System.Text.StringBuilder($"CB diag (artillerie ennemie active : {busy}) :");
            for (int k = 0; k < 2; k++)
            {
                sb.Append($" clé {k} = {spots[k].Count} tir(s)");
                foreach (var p in spots[k].Take(5)) sb.Append($" [{p.x:0},{p.z:0} ami à {NearestDist(friends, p):0} m, ennemi à {NearestDist(foes, p):0} m]");
                sb.Append(" ;");
            }
            sb.Append($" types {CbShotTypes()} ; clé retenue {(_cbKey < 0 ? "pas encore" : _cbKey.ToString())}");
            Log(sb.ToString());
        }

        static string CbShotTypes()
        {
            try
            {
                var dict = AiTargetDetect._cbTargets;
                if (dict == null) return "?";
                var parts = new List<string>();
                for (int k = 0; k < 2; k++)
                {
                    if (!dict.TryGetValue((TeamSide)k, out var l) || l == null) { parts.Add($"{k}:-"); continue; }
                    var t = new List<string>();
                    for (int i = 0; i < l.Count && i < 5; i++) { var s = l[i]; if (s != null) t.Add($"{s.ShotType}/{s.TimeSinceLastShot:0}s"); }
                    parts.Add($"{k}:{string.Join(",", t)}");
                }
                return string.Join(" ", parts);
            }
            catch (Exception e) { return "illisible (" + e.Message + ")"; }
        }

        // ------------------------------------------------------------ old reservation by area (150 m), used only when FeuAVolonteRepartition = false
        static bool RecentlyTargeted(V3 p)
        {
            for (int i = 0; i < _recentTargets.Count; i++) if ((_recentTargets[i].pos - p).sqrMagnitude < 150f * 150f) return true;
            return false;
        }

        static void ForgetRecent(V3 p)
        {
            for (int i = _recentTargets.Count - 1; i >= 0; i--)
                if ((_recentTargets[i].pos - p).sqrMagnitude < 1f) _recentTargets.RemoveAt(i);
        }

        // (unit, distance band) pairs that made a piece move after an automatic order.
        // The order is cancelled the moment the piece creeps (the mod NEVER lets the artillery travel), but the band is only
        // forbidden after a second creep or a big one, and for 180 s: one lay of 5 m no longer kills the piece for the whole battle.
        const float BAND = 250f, BAN = 180f;
        static readonly Dictionary<long, float> _blacklist = new();          // band key -> time the ban ends
        static readonly Dictionary<long, int> _creeps = new();               // band key -> number of creeps already seen
        static long BandKey(int uid, float dist) => ((long)uid << 20) + Math.Min(0xFFFFF, Math.Max(0, (int)(dist / BAND)));
        static bool Blacklisted(int uid, float dist, float now) => _blacklist.Count > 0 && _blacklist.TryGetValue(BandKey(uid, dist), out var until) && now < until;

        static readonly HashSet<int> _missingLogged = new();

        /// Why an armed piece did not fire: one line per piece every 30 s at most (Warn limiter).
        static void Why(int uid, string tag, LuaUnit u, string reason)
        {
            if (_warnNext.TryGetValue("why" + uid, out var until) && UnityEngine.Time.realtimeSinceStartup < until) return;
            string name = "?"; try { name = u.Name; } catch { }
            Warn("why" + uid, $"{tag} {name} uid {uid} : {reason}");
        }

        // ------------------------------------------------------------ feu à volonté : une pièce par cible (répartition en une seule passe)
        // Pass 1 walks the armed pieces, fires counter-battery at once and sets the pieces still free aside; pass 2 pairs those
        // pieces with the spotted enemies (nearest pair first, one piece per enemy) and sends every order in the same pass.
        // The buffers below are reused from one pass to the next: no allocation and no LINQ in the pairing.
        struct FavPiece
        {
            public int Uid, Ammo;
            public LuaUnit U;
            public V3 Pos;
            public float Range, Min, MinRaw;
            public bool Long, ValuableOnly;
            public string Tag, CbWhy;
        }

        const int FAV_MAX = 64;                                              // buffer size: at most 64 pieces and 64 targets handled per pass
        static readonly List<FavPiece> _favPieces = new();                   // pieces ready to fire this pass
        static readonly List<int> _favTgtIdx = new();                        // indexes into vis.Units of the targets free this pass
        static readonly List<int> _favUids = new();                          // copy of the armed UIDs (the player can click a button during the pass)
        static readonly List<int> _favPrune = new();                         // enemy UIDs whose reservation has expired
        static readonly int[] _favPick = new int[FAV_MAX];                   // piece -> index in _favTgtIdx of its target (-1 = none)
        static readonly int[] _favOrder = new int[FAV_MAX];                  // pieces in the order they were paired (nearest pair first)
        static readonly bool[] _favTgtUsed = new bool[FAV_MAX];
        static readonly byte[] _favTgtWhy = new byte[FAV_MAX];               // 1 not valuable, 2 out of range, 3 forbidden band, 4 reachable
        static readonly byte[] _favTgtEye = new byte[FAV_MAX];               // classe d'observation de chaque cible libre (0 aveugle .. 3 désignée)
        static readonly int[] _favTgtEyeUid = new int[FAV_MAX];              // unité à toi qui donne cette classe (0 = aucune en particulier)
        static readonly float[] _favTgtEyeDist = new float[FAV_MAX];         // sa distance à la cible
        static readonly float[] _favD = new float[FAV_MAX * FAV_MAX];        // distance pièce -> cible, calculée UNE fois par passe (-1 = paire impossible)
        static readonly System.Text.StringBuilder _favSb = new();

        // counters of the pass, written into the one report line at the end of Logic
        static int _cPieces, _cReady, _cNotIdle, _cCooldown, _cAmmo, _cRange, _cLongExcl, _cPending, _cDead;
        static int _cTargets, _cGround, _cTgtCooldown, _cUsable, _cOutOfRange, _cBlack, _cValuable, _cAssigned, _cRefused, _cTropPres;
        static float _favNextReport;
        static int _favErrors;
        static bool _favArmed = true, _favFullLogged;                        // _favArmed = false: too many errors, the spreading is off until the end of the battle

        static void FavResetCounters()
        {
            _cPieces = _cReady = _cNotIdle = _cCooldown = _cAmmo = _cRange = _cLongExcl = _cPending = _cDead = 0;
            _cTargets = _cGround = _cTgtCooldown = _cUsable = _cOutOfRange = _cBlack = _cValuable = _cAssigned = _cRefused = _cTropPres = 0;
        }

        /// Delay before this piece fires again: the magazine brakes it by itself as it empties (no separate ammunition budget).
        static float FavCooldown(int ammoPct, bool longRange)
        {
            float c = _fawCooldown.Value;
            if (ammoPct < 30) c *= 3f;
            else if (ammoPct < 40) c *= 2f;
            return longRange ? c * 3f : c;
        }

        /// Reservations older than 120 s are dropped (a dead enemy simply stops coming back in the spotted list).
        static void FavPrune(float now)
        {
            if (_favTargetNext.Count > 0)
            {
                _favPrune.Clear();
                foreach (var kv in _favTargetNext) if (now - kv.Value > 120f) _favPrune.Add(kv.Key);
                for (int i = 0; i < _favPrune.Count; i++) _favTargetNext.Remove(_favPrune[i]);
            }
            // salvo counters of the observer rule: an enemy nobody has shelled for 2 min starts again with a first, wider salvo
            if (_favSalvos.Count > 0)
            {
                _favPrune.Clear();
                foreach (var kv in _favSalvos) if (now - kv.Value.last > 120f) _favPrune.Add(kv.Key);
                for (int i = 0; i < _favPrune.Count; i++) _favSalvos.Remove(_favPrune[i]);
            }
            for (int i = _recentTargets.Count - 1; i >= 0; i--) if (_recentTargets[i].until < now) _recentTargets.RemoveAt(i);
        }

        static void Logic(GameController gc, int myTeam)
        {
            if (_byUid.Count == 0) return;
            if (GamePaused) return;                                          // no automatic fire order while the game is paused
            var cmd = gc._GetEcsEventBus_k__BackingField?.Commands;
            if (cmd == null) return;
            int enemyTeam = myTeam == 0 ? 1 : 0;
            float now = GameNow;
            FavResetCounters();
            FavPrune(now);
            RefreshFriendPos(myTeam, now);                                   // one map read a second, shared by the CB pass and the FAV pass
            bool cbUpdated = false;
            VisResult eyeVis = null;                                         // read once, and only if a counter-battery salvo really leaves
            _favPieces.Clear();

            // copy of the armed UIDs: the pass gives several orders and the player may switch a button during it
            _favUids.Clear();
            foreach (var kv in _byUid) if ((kv.Value & (Opt.CB | Opt.FAW)) != 0) _favUids.Add(kv.Key);

            // ---------------- passe 1 : contre-batterie tout de suite, pièces en feu à volonté mises de côté
            for (int i = 0; i < _favUids.Count; i++)
            {
                int uid = _favUids[i];
                if (!_byUid.TryGetValue(uid, out var opt) || (opt & (Opt.CB | Opt.FAW)) == 0) continue;
                bool fav = (opt & Opt.FAW) != 0;
                string tag = fav ? ((opt & Opt.CB) != 0 ? "FAV+CB" : "FAV") : "CB";
                if (fav) _cPieces++;
                if (!_mineByUid.TryGetValue(uid, out var u) || u == null)
                {
                    if (fav) _cDead++;
                    if (_missingLogged.Add(uid)) Log($"{tag} uid {uid} : pièce armée absente de tes unités (détruite, embarquée ou passée à un autre joueur)");
                    continue;
                }
                _missingLogged.Remove(uid);
                try
                {
                    if (!u.IsAlive()) { if (fav) _cDead++; continue; }
                    if (_fireJobs.TryGetValue(uid, out var job) && !job.Accepted && !job.Refused) { if (fav) _cPending++; Why(uid, tag, u, "ordre de tir en attente de confirmation"); continue; }
                    if (!u.IsIdle()) { if (fav) _cNotIdle++; Why(uid, tag, u, "pas inactive (ordre ou tir en cours)"); continue; }
                    if (_nextShot.TryGetValue(uid, out var next) && now < next) { if (fav) _cCooldown++; Why(uid, tag, u, $"délai {next - now:0} s avant la prochaine salve"); continue; }
                    int ammo = 100;
                    try { ammo = u.GetAmmoPercentage(true, false); } catch { }
                    if (ammo < _minAmmo.Value) { if (fav) _cAmmo++; Why(uid, tag, u, $"munitions {ammo}% sous le minimum {_minAmmo.Value}%"); continue; }
                    float range = RangeOf(u, out var minRange, out bool longRange);
                    if (range <= 0) { if (fav) _cRange++; Why(uid, tag, u, "portée 0 (aucun obus d'artillerie connu pour cette unité)"); continue; }
                    if (longRange && !_longRange.Value) { if (fav) _cLongExcl++; Why(uid, tag, u, "missiles longue portée exclus (option ArtillerieLonguePortee)"); continue; }
                    // targets are only taken inside [minimum range, range] of the CURRENT position: the fire order never needs the piece to move
                    var pos = u.GetPosition();
                    string cbWhy = null;

                    // CB: enemy fire positions, even unspotted; each salvo on the same battery is more precise than the last (every piece with artillery rounds)
                    if ((opt & Opt.CB) != 0)
                    {
                        if (longRange) cbWhy = "CB impossible avec des missiles seuls";
                        else
                        {
                            if (!cbUpdated) { cbUpdated = true; UpdateCbSpots(myTeam, enemyTeam, now); }
                            CbSpot spot = null; float sd = 0f; int cbIn = 0;
                            foreach (var s in _cbSpots)
                            {
                                float d = V3.Distance(pos, s.Pos);
                                if (d > range || d < Math.Max(80f, minRange) || Blacklisted(uid, d, now)) continue;
                                cbIn++;
                                if (now < s.SilentUntil) continue;                        // point beaten blind for nothing: left alone for a while
                                if (now - s.LastSalvo < _cbInterval.Value + s.Hold) continue;
                                // keep adjusting a battery already under fire before opening on a new one, then the nearest
                                if (spot == null || s.Salvos > spot.Salvos || (s.Salvos == spot.Salvos && d < sd)) { spot = s; sd = d; }
                            }
                            if (spot != null)
                            {
                                // CB is the textbook unobserved fire: the point comes from a detection of muzzle flashes, not from a sight.
                                // With the rule on, it stays wide as long as the player sends nobody to look, and recovers in two salvoes
                                // as soon as a drone flies over the battery.
                                int cls = 1, eyeUid = 0; float eyeDist = 0f, err;
                                if (EyeOn)
                                {
                                    if (eyeVis == null) eyeVis = SpottedInfo(gc, enemyTeam);   // cached 1 s by Visibility, read only when a salvo leaves
                                    cls = Oeil(spot.Pos, eyeVis, out eyeUid, out eyeDist);
                                    err = Ecart(cls, sd, spot.Salvos);
                                }
                                else
                                {
                                    err = _cbFirstError.Value * (float)Math.Pow(0.5, spot.Salvos);
                                    if (err < 15f) err = 0f;
                                }
                                if (FireOwn(cmd, u, uid, pos, spot.Pos, err, range, minRange, "CB", now, out float errUsed))
                                {
                                    // the battery is reserved now; the salvo only counts (tighter next salvo) once the game accepts the order
                                    if (_fireJobs.TryGetValue(uid, out var fj)) { fj.Spot = spot; fj.SpotPrevLast = spot.LastSalvo; fj.Blind = cls == 0; }
                                    spot.LastSalvo = now;
                                    // soft brake: a blind battery is shelled half as often, it is never forbidden
                                    // "deux fois moins vite" really means twice the interval: Hold is ADDED to it, so it must be a
                                    // whole interval, not half of one (the feu à volonté already does cool *= 2f).
                                    spot.Hold = cls == 0 && EyeOn && _eyeBrake.Value ? _cbInterval.Value : 0f;
                                    _nextShot[uid] = now + _cbCooldown.Value;
                                    ArmGuard();
                                    Log($"[contre-batterie] {u.Name} (uid {uid}) : salve {spot.Salvos + 1} ordonnée à {sd:0} m, écart {errUsed:0} m{(errUsed <= 0f ? " (pile dessus)" : "")}{(EyeOn ? $" (œil : {EyeName(cls)}{EyeWho(eyeUid, eyeDist)})" : "")}");
                                    if (cls == 0 && EyeOn) NotifyBlindOnce();
                                    continue;
                                }
                            }
                            cbWhy = $"CB : {_cbSpots.Count} batterie(s) ennemie(s) localisée(s) (clé {(_cbKey < 0 ? "non prouvée" : _cbKey.ToString())}), {cbIn} dans [{minRange:0},{range:0}] m";
                        }
                    }

                    // FAV: the piece is free, it goes to the pairing pass (it fires there, never here)
                    if (!fav) { if (cbWhy != null) Why(uid, tag, u, cbWhy); continue; }
                    if (_favPieces.Count >= FAV_MAX)
                    {
                        if (!_favFullLogged) { _favFullLogged = true; Log($"feu à volonté : plus de {FAV_MAX} pièces prêtes dans la même passe, les suivantes tireront à la passe d'après"); }
                        continue;
                    }
                    _cReady++;
                    _favPieces.Add(new FavPiece
                    {
                        Uid = uid, U = u, Pos = pos, Ammo = ammo,
                        Range = range, Min = Math.Max(80f, minRange), MinRaw = minRange,
                        Long = longRange, ValuableOnly = longRange && _longRangeValuable.Value,
                        Tag = tag, CbWhy = cbWhy,
                    });
                }
                catch (Exception e) { Warn("logic" + uid, $"artillerie uid {uid}: {e.Message}"); }
            }

            // ---------------- passe 2 : répartition des pièces prêtes sur les ennemis repérés
            VisResult vis = null;
            if (_favPieces.Count > 0 && _favArmed)
            {
                vis = SpottedInfo(gc, enemyTeam, true);
                try { FavAssign(cmd, vis, now); }
                catch (Exception e)
                {
                    if (++_favErrors >= 20)
                    {
                        _favArmed = false;
                        Log("feu à volonté : trop d'erreurs de répartition, la fonction est coupée jusqu'à la fin de la bataille (la contre-batterie continue)");
                    }
                    Warn("favrep", "feu à volonté : répartition impossible : " + e.Message);
                }
            }
            FavReport(vis, now);
        }

        /// One piece per enemy: the nearest suitable pair first, then the next, up to FeuAVolonteSalvesParPasse orders in the same pass.
        /// Distances use the same measure as FireOwn, so a paired target is always inside [minimum range, range] of the piece WHERE IT STANDS:
        /// the mod never has to move the player's artillery, and FireOwn still refuses anything outside that window.
        static void FavAssign(EcsEventBus.CommandsBus cmd, VisResult vis, float now)
        {
            int np = _favPieces.Count;
            _cTargets = vis.Units.Count;
            if (!vis.Usable)
            {
                for (int p = 0; p < np; p++)
                {
                    var fp = _favPieces[p];
                    Why(fp.Uid, fp.Tag, fp.U, $"{vis.Enemies} ennemis, visibilité inutilisable (source {vis.Source}) : aucun tir{(fp.CbWhy != null ? " | " + fp.CbWhy : "")}");
                }
                return;
            }

            bool spread = FavSpreadOn;

            // targets free this pass: on the ground, not reserved by another piece a moment ago
            _favTgtIdx.Clear();
            for (int t = 0; t < vis.Units.Count; t++)
            {
                var s = vis.Units[t];
                if (s.role < 0) continue;                                    // aircraft and helicopters: artillery cannot hit them
                _cGround++;
                if (_favTargetNext.TryGetValue(s.uid, out var until) && now < until) { _cTgtCooldown++; continue; }
                // la réserve de zone de 150 m vaut AUSSI avec la répartition : sans elle, plusieurs pièces pilonnent le même paquet
                if (RecentlyTargeted(s.pos)) { _cTgtCooldown++; continue; }
                if (_favTgtIdx.Count >= FAV_MAX) break;
                _favTgtIdx.Add(t);
            }
            int nt = _cUsable = _favTgtIdx.Count;

            // observation class of every free target, computed ONCE for the whole pass: it serves both the pairing and the firing error
            bool eye = EyeOn;
            for (int t = 0; t < nt; t++)
            {
                int cl = 1, who = 0; float far = 0f;
                if (eye) cl = Oeil(vis.Units[_favTgtIdx[t]].pos, vis, out who, out far, vis.Units[_favTgtIdx[t]].uid);
                _favTgtEye[t] = (byte)cl; _favTgtEyeUid[t] = who; _favTgtEyeDist[t] = far;
            }
            // sorting only: at equal range the pairing puts a watched target before a blind one. Nothing is ever forbidden.
            float blindPen = eye && _eyeBrake.Value ? EYE_BLIND_PENALTY : 0f;

            for (int p = 0; p < np; p++) _favPick[p] = -1;
            for (int t = 0; t < nt; t++) { _favTgtUsed[t] = false; _favTgtWhy[t] = 0; }

            // how many orders this pass: the option caps them, and the escape hatch goes back to one salvo at a time
            int maxAssign = spread ? (_favMaxPerTick.Value <= 0 ? np : Math.Min(np, _favMaxPerTick.Value)) : 1;
            if (maxAssign > nt) maxAssign = nt;
            int paired = 0;

            // une seule matrice pour toute la passe : ni les pièces ni les cibles ne bougent entre deux tours, donc chaque paire n'est
            // regardée (et sa racine carrée calculée) qu'une fois. Les tours ne font plus que relire cette matrice.
            for (int p = 0; p < np; p++)
            {
                var fp = _favPieces[p];
                int b = p * FAV_MAX;
                for (int t = 0; t < nt; t++)
                {
                    var s = vis.Units[_favTgtIdx[t]];
                    byte why;
                    float d = -1f;
                    if (fp.ValuableOnly && !IsHighValue(s.role)) why = 1;
                    else
                    {
                        d = V3.Distance(fp.Pos, s.pos);
                        if (d > fp.Range || d < fp.Min) { why = 2; d = -1f; }
                        else if (Blacklisted(fp.Uid, d, now)) { why = 3; d = -1f; }
                        else why = 4;
                    }
                    // la matrice porte la CLÉ DE TRI (distance + pénalité du tir aveugle), jamais la distance employée pour l'ordre :
                    // celle-ci est recalculée au moment du tir, donc la garde de portée reste faite sur la vraie distance.
                    _favD[b + t] = d < 0f ? -1f : d + (blindPen > 0f && _favTgtEye[t] == 0 ? blindPen : 0f);
                    if (why > _favTgtWhy[t]) _favTgtWhy[t] = why;     // la meilleure raison de chaque cible est gardée
                }
            }

            for (int round = 0; round < maxAssign; round++)
            {
                int bp = -1, bt = -1; float bd = float.MaxValue;
                for (int p = 0; p < np; p++)
                {
                    if (_favPick[p] >= 0) continue;
                    int b = p * FAV_MAX;
                    for (int t = 0; t < nt; t++)
                    {
                        if (_favTgtUsed[t]) continue;
                        float d = _favD[b + t];
                        if (d < 0f) continue;
                        if (d < bd) { bd = d; bp = p; bt = t; }
                    }
                }
                if (bp < 0) break;
                _favPick[bp] = bt; _favTgtUsed[bt] = true; _favOrder[paired++] = bp;
                // deux salves à moins de 150 m l'une de l'autre, c'est le même paquet d'ennemis : les cibles voisines sortent de la passe
                var pris = vis.Units[_favTgtIdx[bt]];
                for (int t2 = 0; t2 < nt; t2++)
                    if (!_favTgtUsed[t2] && (vis.Units[_favTgtIdx[t2]].pos - pris.pos).sqrMagnitude < 150f * 150f) { _favTgtUsed[t2] = true; _cTropPres++; }
            }

            for (int t = 0; t < nt; t++)
                switch (_favTgtWhy[t]) { case 1: _cValuable++; break; case 2: _cOutOfRange++; break; case 3: _cBlack++; break; }

            // ---------------- les ordres, la paire la plus proche d'abord (le tir le plus sûr part en premier)
            for (int k = 0; k < paired; k++)
            {
                int p = _favOrder[k];
                var fp = _favPieces[p];
                var s = vis.Units[_favTgtIdx[_favPick[p]]];
                float d = V3.Distance(fp.Pos, s.pos);
                // the FAV only fires at enemies your side has really spotted, so it is nearly always class >= 1: it does not become bad,
                // it just loses its divine accuracy on the FIRST salvo against something seen from very far away.
                int cls = _favTgtEye[_favPick[p]];
                int salvos = SalvesSur(s.uid);
                float err = eye ? Ecart(cls, d, salvos) : 0f;
                if (!FireOwn(cmd, fp.U, fp.Uid, fp.Pos, s.pos, err, fp.Range, fp.MinRaw, "FAV", now, out float errUsed)) { _cRefused++; continue; }
                float cool = FavCooldown(fp.Ammo, fp.Long);
                if (cls == 0 && blindPen > 0f) cool *= 2f;                   // soft brake: blind fire is half as fast, never forbidden
                _nextShot[fp.Uid] = now + cool;
                _favTargetNext[s.uid] = now + _favTargetCooldown.Value;      // the ENEMY is reserved, not a circle on the ground
                _recentTargets.Add((s.pos, now + 30f));                      // et la zone autour de lui, répartition ou pas
                if (_fireJobs.TryGetValue(fp.Uid, out var fj)) { fj.FavTargetUid = s.uid; fj.Blind = cls == 0; }   // released if the game refuses the order
                _cAssigned++;
                ArmGuard();
                string name = "?"; try { name = fp.U.Name; } catch { }
                Log($"[feu à volonté] {name} (uid {fp.Uid}) -> ennemi uid {s.uid} à {d:0} m (source {vis.Source}, {nt} cible(s) libre(s), munitions {fp.Ammo} %{(eye ? $", œil : {EyeName(cls)}{EyeWho(_favTgtEyeUid[_favPick[p]], _favTgtEyeDist[_favPick[p]])}, écart {errUsed:0} m, salve {salvos + 1}" : "")})");
                if (cls == 0 && eye) NotifyBlindOnce();
            }

            // one line per piece left without a target, with what the pass really saw
            for (int p = 0; p < np; p++)
            {
                if (_favPick[p] >= 0) continue;
                var fp = _favPieces[p];
                Why(fp.Uid, fp.Tag, fp.U, $"{vis.Enemies} ennemis, {vis.Units.Count} repérés (source {vis.Source}), {_cGround} au sol, {nt} libre(s), aucune dans [{fp.Min:0},{fp.Range:0}] m pour cette pièce" +
                    $"{(_cTgtCooldown > 0 ? $" ({_cTgtCooldown} déjà réservée(s) par une autre pièce)" : "")}{(fp.ValuableOnly ? " (missiles seuls : cibles de valeur uniquement)" : "")}{(fp.CbWhy != null ? " | " + fp.CbWhy : "")}");
            }
        }

        /// The one compact line that tells, in the shared log, what the artillery did this pass and why.
        /// Never named "relevé": that word sends a line back through the 5-minute limit of the PUBLIC log (ModLog.cs).
        static void FavReport(VisResult vis, float now)
        {
            bool salvo = _cAssigned > 0 || _cRefused > 0;
            if (!salvo && (_cPieces == 0 || now < _favNextReport)) return;
            _favNextReport = now + (_cReady > 0 ? 10f : 60f);
            var sb = _favSb;
            sb.Clear();
            sb.Append("artillerie : pièces en feu à volonté ").Append(_cPieces).Append(" (prêtes ").Append(_cReady)
              .Append(" ; écartées : pas inactive ").Append(_cNotIdle).Append(", délai ").Append(_cCooldown)
              .Append(", munitions ").Append(_cAmmo).Append(", portée 0 ").Append(_cRange)
              .Append(", longue portée exclue ").Append(_cLongExcl).Append(", ordre en attente ").Append(_cPending)
              .Append(", absente ").Append(_cDead).Append(')');
            if (!_favArmed) sb.Append(" ; répartition coupée (trop d'erreurs)");
            else if (!FavSpreadOn) sb.Append(" ; répartition désactivée (une seule pièce par passe)");
            if (vis == null) sb.Append(_cReady > 0 ? " ; cibles non cherchées cette passe" : " ; visibilité non consultée (aucune pièce prête)");
            else if (!vis.Usable) sb.Append(" ; visibilité inutilisable (source ").Append(vis.Source).Append(')');
            else
                sb.Append(" ; ennemis repérés ").Append(_cTargets).Append(" (source ").Append(vis.Source)
                  .Append(", au sol ").Append(_cGround).Append(", déjà réservés ").Append(_cTgtCooldown)
                  .Append(", libres ").Append(_cUsable).Append(", hors portée ").Append(_cOutOfRange)
                  .Append(", tranche interdite ").Append(_cBlack).Append(", pas de valeur ").Append(_cValuable)
                  .Append(", trop près d'une salve de la même passe ").Append(_cTropPres).Append(')');
            sb.Append(" ; salves ordonnées ").Append(_cAssigned).Append(" ; ordres refusés ").Append(_cRefused);
            if (_eyeOn == null || !_eyeOn.Value) sb.Append(" ; règle des observateurs coupée (réglage)");
            else if (!_eyeArmed) sb.Append(" ; règle des observateurs coupée (trop d'erreurs)");
            else sb.Append(" ; observateurs : ").Append(_eyes.Count).Append(" unité(s) à toi dans l'instantané de vue");
            Log(sb.ToString());
        }

        /// Point fire order with the ammo type chosen from the loadout (used by EnemyAi for the bots' artillery too; no watchdog there).
        internal static void Fire(EcsEventBus.CommandsBus cmd, LuaUnit u, V3 target, float errorRadius = 0f)
            => FireCore(cmd, u, target, errorRadius, AmmoTypeOf(u), out _, out _);

        /// Point fire order through the command bus (LuaUnit.FireMission as fallback). resolved = point the game kept (bus only).
        static bool FireCore(EcsEventBus.CommandsBus cmd, LuaUnit u, V3 target, float errorRadius, AmmoTypeEnum ammo, out V3 resolved, out bool viaBus)
        {
            resolved = V3.zero; viaBus = false;
            try
            {
                var d = new FireMissionPointData();
                d.TargetGroupName = "";
                d.ErrorRadius = errorRadius;
                d.Dispersion = 0f;
                d.TargetVector = target;
                d.TargetTagUid = 0;
                d.TargetUnitUid = 0;
                d.AmmoType = ammo;
                d.Duration = DurationEnum.Short;
                d.UnitsList = Cheats.ToIl2Cpp(new List<int> { u.UID });
                d.UnitUID = 0;
                d.GroupName = "";
                d.Blocking = false;
                d.Queue = false;
                // the mod is ordering, not the player: skip the crew acknowledgement for this one order (SilenceOrdres.cs)
                try { SilenceOrdres.Marquer(u.Entity.EntityId); } catch { }
                cmd.FireMissionPointTarget.Invoke(d, out resolved);
                viaBus = true;
                float fromPiece = -1f;
                try { fromPiece = V3.Distance(u.GetPosition(), resolved); } catch { }
                Log($"tir ordonné vers {target} ({ammo}) : point retenu {resolved} (à {V3.Distance(resolved, target):0} m de la cible, à {fromPiece:0} m de la pièce)");
                return true;
            }
            catch (Exception e)
            {
                Log("ordre de tir (bus) refusé, repli LuaUnit.FireMission : " + e.Message);
                try { u.FireMission(target); return true; }
                catch (Exception e2) { Mod.Log.Error("[ASSIST] tir impossible: " + e2.Message); return false; }
            }
        }

        // ------------------------------------------------------------ fire safety for the player's own pieces
        sealed class FireJob
        {
            public LuaUnit U; public string Name, Kind; public V3 P0, Target;
            public float T0, TOrder, Err, Range, Min, Dist; public AmmoTypeEnum Ammo; public bool Retried, Accepted, Refused;
            public CbSpot Spot; public float SpotPrevLast;                   // CB: battery reserved by this order (salvo counted only once accepted)
            public int FavTargetUid;                                        // FAV: enemy UID reserved by this order, 0 = none (released if refused)
            public bool Blind;                                               // nobody of yours watched the area when the order left
        }

        /// Game time for the artillery logic: follows pause and game speed (the real clock would let the safety windows expire during a pause).
        static float GameNow => UnityEngine.Time.time;
        static bool GamePaused { get { try { return UnityEngine.Time.timeScale <= 0f; } catch { return false; } } }
        static readonly Dictionary<int, FireJob> _fireJobs = new();
        static float _nextJobs;

        static float Flat(V3 a, V3 b) { a.y = 0f; b.y = 0f; return V3.Distance(a, b); }

        /// Automatic order for one of the player's own pieces: never beyond the computed range, then followed by WatchFire
        /// (acceptance within 2 s, one retry with the other ammo type, 15 s movement watchdog).
        ///
        /// FIRE SAFETY — the mod NEVER lets the player's artillery travel. The error radius is consumed when the order is built:
        /// it MOVES the aim point. An error that pushes that point past the piece's own range gives the engine a reason to close
        /// the range itself, that is, to drive the piece forward. Two layers stop that:
        ///   1. the error budget: the error is cut back so the aim point always stays inside [minimum range, range] of the piece
        ///      WHERE IT STANDS, with a margin. The error is cut back, the order is NEVER refused: a piece at the edge of its
        ///      range fires straight at the point instead of firing wide. No shot is ever lost to this rule.
        ///   2. the point the game really kept: outside that window the order is cancelled on the spot.
        /// Layer 3 is WatchFire, unchanged.
        /// errUsed = the error radius really sent, for the log line of the caller.
        static bool FireOwn(EcsEventBus.CommandsBus cmd, LuaUnit u, int uid, V3 pos, V3 target, float err, float range, float min, string kind, float now, out float errUsed)
        {
            errUsed = err;
            float dist = V3.Distance(pos, target);
            float low = Math.Max(80f, min);
            if (dist > range || dist < low)
            {
                Warn("hors" + uid, $"{kind} uid {uid} : cible à {dist:0} m hors de [{min:0}, {range:0}] m, aucun ordre");
                return false;
            }
            // ---- layer 0: FRIENDLY SAFETY. The error MOVES the aim point, so a wide blind salvo could sit on my own troops (or on
            // a scripted ally a "protect" stage needs alive). Blind fire may ask for up to 400 m and the counter-battery spot list
            // only keeps points 300 m away from my units; the "feu à volonté" side had no margin at all. The error is cut back to the
            // room really available, never the order: a piece next to friendly troops fires straight at the point instead of firing wide.
            if (err > 0f && _friendPos.Count > 0)
            {
                float safe = NearestDist(_friendPos, target) - EYE_FRIEND_SAFE;
                if (err > safe)
                {
                    float kept = safe > 1f ? safe : 0f;
                    Log($"{kind} uid {uid} : écart rogné de {err:0} à {kept:0} m — unité à toi à {(safe + EYE_FRIEND_SAFE):0} m du point visé");
                    err = kept;
                }
            }
            // ---- layer 1: the error budget (what the error may bite on either side of the target, inside the range window)
            if (err > 0f)
            {
                float margin = _rangeMargin != null ? Math.Max(0f, _rangeMargin.Value) : 50f;
                float room = Math.Min(range - margin - dist, dist - low - margin);
                if (err > room)
                {
                    float kept = room > 1f ? room : 0f;
                    Log($"{kind} uid {uid} : écart rogné de {err:0} à {kept:0} m pour rester dans la portée ({range:0} m) — la pièce ne bouge pas");
                    err = kept;
                }
            }
            errUsed = err;
            var ammo = AmmoTypeOf(u);
            if (!FireCore(cmd, u, target, err, ammo, out var resolved, out bool viaBus)) return false;
            string name = "?"; try { name = u.Name; } catch { }
            _fireJobs[uid] = new FireJob { U = u, Name = name, Kind = kind, P0 = pos, Target = target, T0 = now, TOrder = now, Err = err, Range = range, Min = min, Dist = dist, Ammo = ammo };
            // ---- layer 2: the point the game really kept
            if (viaBus && resolved.sqrMagnitude > 1f)
            {
                float rd = V3.Distance(pos, resolved);
                if (rd > range + 20f || rd < low - 20f)
                {
                    if (!_keptHard)
                    {
                        Log($"{kind} {name} (uid {uid}) : ATTENTION point retenu par le jeu à {rd:0} m, hors de [{min:0}, {range:0}] m (pièce surveillée 15 s)");
                        return true;
                    }
                    CancelOne(cmd, uid, "portee" + uid);
                    // the job is KEPT, marked as refused: no retry with the other ammo type, but WatchFire goes on watching the piece
                    // for 15 s, so a piece that would move anyway (a cancel the game ignores) is still caught and stopped
                    if (_fireJobs.TryGetValue(uid, out var cj)) { cj.Refused = true; cj.Err = 0f; }
                    // same graduated rule as the movement watchdog: watched the first time, band forbidden from the second one
                    long key = BandKey(uid, dist);
                    int n = _creeps.TryGetValue(key, out var c) ? c + 1 : 1;
                    _creeps[key] = n;
                    int band = (int)(dist / BAND);
                    if (n >= 2) _blacklist[key] = now + BAN;
                    _nextShot[uid] = now + Math.Max(10f, _cbCooldown.Value);          // never order-then-cancel in a loop
                    Log($"{kind} {name} (uid {uid}) : point retenu par le jeu à {rd:0} m, hors de [{min:0}, {range:0}] m : ordre annulé tout de suite, la pièce ne bouge pas" +
                        $"{(n >= 2 ? $" ; tranche {band * BAND:0}-{(band + 1) * BAND:0} m interdite pour cette pièce pendant {BAN:0} s" : " ; tranche surveillée (1re fois)")}");
                    if (++_keptOut >= KEPT_MAX)
                    {
                        _keptHard = false;
                        Log($"artillerie : {_keptOut} points retenus hors de portée dans cette bataille, l'annulation automatique est coupée jusqu'à la fin (le contrôle de portée avant l'ordre et le chien de garde de déplacement restent actifs)");
                    }
                    return false;
                }
            }
            return true;
        }

        const int KEPT_MAX = 8;                                              // kill-switch of layer 2: past this, only the warning is kept
        static int _keptOut;
        static bool _keptHard = true;

        /// Every 0.25 s while orders are followed (game time, frozen during a pause): accepted when the piece stops being idle within 2 s (else one
        /// retry with the other ammo type). Until the salvo is over (15 s at most), a move of more than 5 m TOWARD the target (the game closing
        /// the range for our order) cancels the piece's orders and forbids that distance band; any other move is the player's own order and only
        /// ends the watch. Switching FAV/CB off for the piece also ends the watch.
        static void WatchFire(GameController gc, float now)
        {
            if (GamePaused) return;
            var cmd = gc?._GetEcsEventBus_k__BackingField?.Commands;
            List<int> done = null;
            foreach (var kv in _fireJobs)
            {
                int uid = kv.Key; var j = kv.Value; bool end = false;
                try
                {
                    if (j.U == null || !j.U.IsAlive()) end = true;
                    else if (!_byUid.TryGetValue(uid, out var flags) || (flags & (Opt.CB | Opt.FAW)) == 0)
                    {
                        end = true;
                        Log($"{j.Kind} {j.Name} (uid {uid}) : tir automatique coupé pour cette pièce, surveillance arrêtée (aucun ordre annulé)");
                    }
                    else
                    {
                        var p = j.U.GetPosition();
                        float moved = Flat(p, j.P0);
                        if (moved > 5f)
                        {
                            float closed = Flat(j.P0, j.Target) - Flat(p, j.Target);
                            // a move the MISSION SCRIPT asked for is not the mod closing the range: cancelling it would stop the stage
                            // waiting for that arrival. The mod only ends its watch and says so. Unreadable script state = cancel, as before.
                            bool parScript = false;
                            try { parScript = Missions.ScriptReason(uid, now) != null; } catch { parScript = false; }
                            if (parScript)
                            {
                                Log($"{j.Kind} {j.Name} (uid {uid}) : déplacement de {moved:0} m demandé par le script de mission : surveillance arrêtée, rien annulé");
                            }
                            else if (closed >= 0.8f * moved)
                            {
                                // the piece is stopped at once, every time: the mod never lets the player's artillery travel
                                CancelOne(cmd, uid, "watch");
                                int band = (int)(j.Dist / BAND);
                                long key = BandKey(uid, j.Dist);
                                int n = _creeps.TryGetValue(key, out var c) ? c + 1 : 1;
                                _creeps[key] = n;
                                if (n >= 2 || closed >= 25f)
                                {
                                    _blacklist[key] = now + BAN;
                                    Log($"{j.Kind} {j.Name} (uid {uid}) s'est rapprochée de la cible de {closed:0} m après l'ordre automatique (cible à {j.Dist:0} m) : ordre annulé ; tranche {band * BAND:0}-{(band + 1) * BAND:0} m interdite pour cette pièce pendant {BAN:0} s ({n}e rapprochement)");
                                }
                                else
                                    Log($"{j.Kind} {j.Name} (uid {uid}) s'est rapprochée de la cible de {closed:0} m après l'ordre automatique (cible à {j.Dist:0} m) : ordre annulé ; tranche {band * BAND:0}-{(band + 1) * BAND:0} m surveillée (1er rapprochement, interdite au suivant)");
                            }
                            else Log($"{j.Kind} {j.Name} (uid {uid}) : déplacement de {moved:0} m qui ne vise pas la cible (ordre du joueur) : surveillance arrêtée, rien annulé");
                            end = true;
                        }
                        else if (j.Accepted && now - j.TOrder > 3f)
                        {
                            bool idleAgain = false;
                            try { idleAgain = j.U.IsIdle(); } catch { }
                            if (idleAgain) end = true;                                        // salvo over: later moves are never caused by this order
                        }
                        else if (!j.Accepted && !j.Refused)
                        {
                            bool idle = true;
                            try { idle = j.U.IsIdle(); } catch { }
                            if (!idle)
                            {
                                j.Accepted = true;
                                if (j.Spot != null)
                                {
                                    j.Spot.Salvos++;                                       // the next CB salvo on this battery is tighter only after a real one
                                    if (j.Blind) j.Spot.BlindSalvos++; else j.Spot.BlindSalvos = 0;
                                    // hard brake (off by default): emptying the magazine on a ghost battery nobody can see
                                    if (_eyeSilence != null && _eyeSilence.Value && j.Spot.BlindSalvos >= EYE_BLIND_MAX && now >= j.Spot.SilentUntil)
                                    {
                                        j.Spot.SilentUntil = now + EYE_SILENCE;
                                        j.Spot.BlindSalvos = 0;
                                        Log($"[contre-batterie] {j.Name} (uid {uid}) : {EYE_BLIND_MAX} salves aveugles sur la même position sans rien y repérer, ce point est laissé tranquille {EYE_SILENCE:0} s (envoie un œil pour reprendre)");
                                    }
                                }
                                // the observer rule tightens salvo after salvo only once a salvo has really left
                                if (j.FavTargetUid != 0) BumpFavSalvo(j.FavTargetUid, now);
                                Log($"{j.Kind} {j.Name} (uid {uid}) : ordre accepté ({j.Ammo}), pièce active après {now - j.TOrder:0.0} s");
                            }
                            else if (now - j.TOrder >= 2f)
                            {
                                if (!j.Retried && cmd != null)
                                {
                                    var other = j.Ammo == AmmoTypeEnum.Guided ? AmmoTypeEnum.Basic : AmmoTypeEnum.Guided;
                                    Log($"{j.Kind} {j.Name} (uid {uid}) : toujours inactive 2 s après l'ordre avec {j.Ammo} : nouvel essai avec {other}");
                                    j.Retried = true; j.Ammo = other; j.TOrder = now;
                                    if (!FireCore(cmd, j.U, j.Target, j.Err, other, out _, out _)) j.Refused = true;
                                }
                                else
                                {
                                    j.Refused = true;
                                    // release the reservations made when the order was sent
                                    if (j.Spot != null) j.Spot.LastSalvo = j.SpotPrevLast;
                                    _nextShot.Remove(uid);                     // la pièce n'a pas tiré un obus : elle ne reste pas écartée pour rien
                                    if (j.FavTargetUid != 0) { _favTargetNext.Remove(j.FavTargetUid); ForgetRecent(j.Target); }
                                    int ammoPct = -1;
                                    try { ammoPct = j.U.GetAmmoPercentage(true, false); } catch { }
                                    Log($"{j.Kind} {j.Name} (uid {uid}) : tir refusé, la pièce reste inactive{(j.Retried ? " avec les deux types de munition" : "")} (cible à {j.Dist:0} m dans [{j.Min:0}, {j.Range:0}] m, munitions {ammoPct}%)");
                                }
                            }
                        }
                        if (!end && now - j.T0 >= 15f) end = true;
                    }
                }
                catch (Exception e) { end = true; Warn("watch" + uid, $"suivi du tir uid {uid} impossible : {e.Message}"); }
                if (end) (done ??= new List<int>()).Add(uid);
            }
            if (done != null) foreach (var k in done) _fireJobs.Remove(k);
        }

        // ------------------------------------------------------------ vanilla auto-fire diagnostics (read only)
        static readonly HashSet<int> _compLogged = new();
        static bool _compBroken;

        /// Once per own artillery piece: does its entity carry the vanilla AutoFireAbilityComponent / AiArtilleryComponent?
        static void LogArtyComponents()
        {
            if (_compBroken || _compLogged.Count >= 60) return;
            foreach (var kv in _mineByUid)
            {
                var u = kv.Value;
                try { if (u == null || !IsArtillery(u.UnitRole) || !_compLogged.Add(kv.Key)) continue; } catch { continue; }
                string a, b;
                try { a = u.Entity.Has<AiComp.AutoFireAbilityComponent>() ? "oui" : "non"; } catch (Exception e) { a = "erreur (" + e.Message + ")"; _compBroken = true; }
                try { b = u.Entity.Has<AiComp.AiArtilleryComponent>() ? "oui" : "non"; } catch (Exception e) { b = "erreur (" + e.Message + ")"; _compBroken = true; }
                string name = "?"; try { name = u.Name; } catch { }
                Log($"artillerie {name} (uid {kv.Key}) : AutoFireAbilityComponent={a} AiArtilleryComponent={b}");
                if (_compBroken) { Log("lecture des composants d'artillerie impossible : diagnostic coupé pour la bataille"); return; }
                if (_compLogged.Count >= 60) return;
            }
        }

        // ------------------------------------------------------------ IA ennemie : ses propres réflexes à fond (réglages internes du jeu)
        static void AiBoost(GameController gc, int myTeam)
        {
            // host only (co-op): nothing, not even the statics, before EnemyAi.HostCheck allowed bot orders on this PC
            if (!EnemyAi.CanCommand) return;
            if (!_aiDone)
            {
                _aiDone = true;
                try
                {
                    _botAiOriginal ??= AiConfig.IsBotAIEnabledInScenario;
                    _botSmokeOriginal ??= SmokeAbilitySystem.GLOBAL_BOT_AUTO_SMOKE_DISABLED;
                    AiConfig.IsBotAIEnabledInScenario = true;
                    SmokeAbilitySystem.GLOBAL_BOT_AUTO_SMOKE_DISABLED = false;
                    var ai = GameConfig.Instance?.AiConfig;
                    var bs = GameConfig.Instance?.BattleSystemSettings;
                    int n = 0;
                    // ChanceFireCounterBattery is a PERCENT (vanilla 100), not a 0-1 ratio: writing 1f used to cut enemy
                    // counter-battery from 100 % down to 1 %, the exact opposite of what this line is for.
                    if (ai != null) { n += Realism.SetJournaled(ai, "ChanceFireCounterBattery", 100f); n += Artillery.TunePreset(ai); }   // F3 (EnemyAi.cs)
                    if (bs != null)
                    {
                        // The AI's chance to pop smoke when engaged is the ONE thing this game changes between difficulty levels
                        // (vanilla 0.25 / 0.5 / 0.75). Writing 1 everywhere, as this did, flattened the three levels into one. The
                        // levels are given back their gradient, raised so that even on Easy a crew reacts like a real crew.
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_DIFFICULTY_CHANCE_EASY", 0.5f);
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_DIFFICULTY_CHANCE_MEDIUM", 0.75f);
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_DIFFICULTY_CHANCE_HARD", 1f);
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_TRIGGER_BY_GUNS", true);
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_TRIGGER_BY_MISSILES", true);
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_DELAY_MIN", SmokeDelayMin);   // same delay as the player's units
                        n += Realism.SetJournaled(bs, "AUTOSMOKE_AI_DELAY_MAX", SmokeDelayMax);
                    }
                    Log($"IA ennemie : contre-batterie et fumigènes à 100 % ({n} réglages), botIA={AiConfig.IsBotAIEnabledInScenario}");
                }
                catch (Exception e) { Mod.Log.Error("[ASSIST] réglages IA: " + e); }
            }
            // enemy artillery: make sure the game's own artillery AI is switched on for it
            try
            {
                _map ??= new LuaMap();
                _ai ??= new LuaAI();
                int enemyTeam = myTeam == 0 ? 1 : 0;
                var enemies = _map.GetUnits(V3.zero, 1_000_000f, enemyTeam, -1);
                int n = 0;
                for (int i = 0; i < (enemies?.Length ?? 0); i++)
                {
                    var e = enemies[i];
                    try
                    {
                        if (e == null || !e.IsAlive() || !IsArtillery(e.UnitRole) || !_aiArtyDone.Add(e.UID)) continue;
                        _ai.SetAIState(e, true);
                        n++;
                    }
                    catch { }
                }
                if (n > 0) Log($"IA d'artillerie activée sur {n} pièce(s) ennemie(s)");
            }
            catch (Exception e) { Warn("aiarty", "[ASSIST] IA artillerie: " + e.Message); }
        }

        internal static void RestoreStatics()
        {
            try
            {
                if (_botAiOriginal.HasValue) { AiConfig.IsBotAIEnabledInScenario = _botAiOriginal.Value; _botAiOriginal = null; }
                if (_botSmokeOriginal.HasValue) { SmokeAbilitySystem.GLOBAL_BOT_AUTO_SMOKE_DISABLED = _botSmokeOriginal.Value; _botSmokeOriginal = null; }
            }
            catch { }
        }
    }

    // immediate refresh of the CB / FAV buttons when the vanilla panel recomputes its orders (applied one class at a time by Mod.ApplyPatches)
    [HarmonyPatch(typeof(ValidOrdersSystem), nameof(ValidOrdersSystem.FilterOrdersData))]
    static class Patch_AssistOrders
    {
        static void Postfix() => Guard.Run("Assist.Ordres", Assistants.OnOrdersRefreshed);
    }
}
