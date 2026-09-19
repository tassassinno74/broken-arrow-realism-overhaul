// RealismOverhaul - how long the battlefield keeps its marks ("Durée des cratères", a choice row of the Mod tab, "Mission" block).
//  A Nexus moderator asked for four things: wrecks kept 60+ minutes, craters kept with them, felled trees kept for ever, and a staged
//  burn (8-10 min of heavy smoke, then 10 more, then light smoke) for wrecks and for buildings. Checked one by one against the dumps,
//  exactly one of the four is a value this game lets a mod write, one looks like it is already true, and two do not exist at all.
//  This module writes the one, measures the one that looks already true, and says the other two out loud instead of shipping a figure
//  that changes nothing.
//
//  1) CRATERS - REACHABLE, and the only thing written here. EXACTLY ONE value is written: DecalLimitGroupPreset.LifeTime.
//     Every shell impact goes through ExplosionEventSystem._decalService -> DecalService.AddDecal(position, ammoId, radius). The decal
//     picked by GetDecalData is resolved to a DecalLimitGroup by its own DecalData.DecalLimitGroup string, and that group holds four
//     DecalLimitGroupPreset (High / Medium / Low / Potato), each one { Int32 MaxCount, Int32 LifeTime }. DecalService is a
//     ScriptableObject of Il2CppBrokenArrow.dll (already referenced) and LifeTime is an ordinary interop property with a setter:
//     NO HOOK of any kind, one property write per preset, journaled under this module's own lot so stopping the mod puts the game's
//     own numbers back. These are ASSET values, so they live for the whole process, not for one battle - which is why the journal
//     matters and why the write is done in the menus too, before a battle is started.
//
//     WHAT IS **NOT** WRITTEN, and this is the whole safety of the row. MaxCount is never touched, and neither is the cap the engine
//     really draws against: ObjectPoolGroup._maxTotalObjects, held in DecalService._objectPoolGroups and built once per battle in
//     DecalService.Initialize(). The count budget therefore stays exactly the studio's. Said the careful way, because it is the way
//     the row says it to the player: this module raises no count anywhere, so whatever bounds the number of live marks in a vanilla
//     battle is still the only thing bounding it here. It is NOT claimed that the number on screen cannot rise - a live decal is a
//     pooled URP DecalProjector and a longer life means more of them alive at once, up to that budget. What the row promises is the
//     one thing the code can back: the budget is the studio's own, untouched. The honest price, in all five languages: the game SITS
//     AT its own maximum much more of the time instead of staying well under it, and at that maximum it does exactly what a battle
//     without the mod already does.
//
//     WHAT IS NOT KNOWN, and is claimed nowhere. The dumps give the shape of these classes, not the bodies of Processing /
//     LiveTimeProcessing / Spawn. Two things follow, and both are said out loud rather than papered over.
//     (a) That the engine reads this preset when a mark is born is a DEDUCTION from the class shapes (DecalLimitGroupPreset holds
//     { MaxCount, LifeTime }, LiveDecal holds its own Single LifeTime, GetDecalLimitGroup resolves one from the other): it is the
//     reason the row's state line says "written", never "applied", and the reason the write is logged with that reserve attached.
//     One timed battle turns it into a fact; nothing here pretends it already is one.
//     (b) What the engine does once its own MaxCount is reached - drop the oldest mark, or refuse the new one - is NOT known. Nothing
//     in this module, in its log or in its five translations says that the oldest go first or that new ones always appear. If the
//     engine refuses, a long battle stops marking the ground earlier than vanilla does, and the module watches for exactly that with
//     the one direct signal the dumps do give: ObjectPoolGroup._currentTotalObjects >= _maxTotalObjects, READ ONLY. That counter is the
//     pool's own peak demand and nothing in the class brings it back down during a battle, so "full" is read here as "has reached its
//     ceiling", and the log says it in those words. The first pool to fill up is logged as a measurement and nothing is done, because a
//     small pool can reach its own cap in a perfectly ordinary battle. The one action waits for a fact and not for an invented
//     threshold: EVERY capped pool full, three passes in a row, i.e. the engine has nowhere left to put a mark whatever it does at its
//     ceilings - and the action itself is the safe direction, the game's own duration handed back. It is then given back for the rest
//     of that battle, once, so the ground marks again as the long marks run out. Where those pools cannot be read the module says so
//     and changes nothing else: with the count budget untouched there is no frame-rate emergency to guard against, only marks that may
//     stop appearing - and the battle's closing line carries the peak against the allowed total either way, which is the measurement
//     the next decision needs.
//
//     WHY 60 MINUTES AND "THE WHOLE BATTLE" ARE NOT OFFERED (withdrawn 2026-09-19). A LiveDecal is born with its own Single LifeTime
//     (dump: LiveDecal { Int32 Id, GameObject Prefab, Int32 GroupRuntimeId, GameObject Decal, Single LifeTime, ... }), so a mark
//     already on the ground keeps the duration that was in force when it was made. A duration longer than a battle therefore means
//     nothing on the ground can expire during that battle - the frozen battlefield the research told us to avoid - and no later
//     correction can undo it. The row stops at 30 minutes until one timed battle has said which quality preset is live, what the
//     studio's real budget is and what the engine does at MaxCount. Those two values come back when that battle has been played.
//
//  2) TREES - PROBABLY ALREADY TRUE, and nothing is written. VegetationDestructionBackend (Assembly-CSharp, deliberately NOT
//     referenced here) does not leave a felled tree lying as a GameObject: CompletePhysical / CompleteWithoutPhysics bake a Matrix4x4
//     into a GPU-instanced OutcomeBuffer whose API is Add / Flush / Grow / Dispose. The class DOES have removal machinery -
//     QueueStandingRecordRemoval, FlushStandingRecordRemovals and a _zeroMatrixScratch of null matrices - but it works on the STANDING
//     tree records, i.e. it hides the tree that was there before it fell; nothing in the dump shows an outcome, once flushed, being
//     taken back. So a felled tree looks like it stays down for the whole battle, and that is as far as a dump can go: it is written
//     here as something to confirm with a stopwatch, not as a fact. MinimumPhysicalLifetime / MaximumPhysicalLifetime only decide how
//     long a tree stays a live rigidbody before it is frozen into that buffer, i.e. how long it flops; that is what the moderator saw
//     change. MaxActivePhysicalTrees is not an evict-oldest cap either: TrySpawnPhysical returns a Boolean and SpawnOrComplete falls
//     back to CompleteWithoutPhysics, so past the cap trees still fall, they just snap flat instead of animating.
//     Nothing is written. The module reads the config and logs it, in French, once per object, and warns if the older Unity-Terrain
//     path (TreeDestructionManager, which DOES recycle trees on a timer) is present on the map, because on such a map the above would
//     be wrong. The line that says felled trees stay down is written ONLY after that per-map check has come back empty.
//
//  3) WRECKS KEPT 60 MINUTES - NOT REACHABLE, said plainly once per session. No wreck timer exists: the model is not left on the
//     ground, RemoveUnitSystem.SecondPass -> DestroyUnit -> DisposeViewObject -> ResourcesManager.ReturnPooledInstance returns it to a
//     REUSE pool that later spawns. Holding wrecks would starve that pool. Every method of that path takes a non-blittable IL2CPP
//     struct by reference and is forbidden here - that family of hooks cost a player his campaign on 18/09/2026.
//
//  4) STAGED BURNING OF WRECKS AND BUILDINGS - NOT REACHABLE either. A death's fire and smoke are VfxHandlers held by the unit's own
//     SpawnVFX behaviour and are explicitly torn down by BOTH cleanup systems (UnitDeathSystem.RemoveSfxAndVfx, RemoveUnitSystem.
//     RemoveSfxAndVfx), so they cannot outlive a wreck that cannot itself be kept; EffectsLimiterPool is a limited pool sized from the
//     quality settings (Resize / ResizeBySettings / IsFull), so a handler held for ten minutes is a handler no later explosion can use.
//     A destroyed building does not burn: BuildingService.InitRemain swaps the mesh for a permanent ruin and BuildingFalling throws
//     one-shot dust, there is no building fire in the build to lengthen. SmokeLifeTimeSystem / SmokeComponent are the tactical smoke
//     screen and its fog-of-war cover: touching them would move concealment and combat balance, and they are left alone on purpose.
//
//  How the crater row behaves:
//  - NO hook at all. One pass every 2 s from the tab's own frame, in the menus as well, so a battle started afterwards already has it.
//  - MEASURED BEFORE WRITTEN: the first pass on a DecalService logs every limit group, its four presets' own MaxCount and LifeTime,
//    the DecalData -> group mapping and the service's own density limits, before a single value is written. Then, once a battle is
//    running, the runtime pools' own caps are logged next to those presets - which is what finally says WHICH quality preset the
//    engine is really using. On "comme le jeu" both lines are still written, so a battle played with the row untouched is the
//    measurement battle.
//  - Plausibility gate per preset: the game's own LifeTime must read between 5 s and 3600 s and its MaxCount between 1 and 100000, or
//    that preset is left alone. No plausible preset at all = a clean refusal and the game keeps its behaviour.
//  - Only ever raises. A group the studio already keeps longer than the player asked keeps its own value.
//  - ONE value is written, LifeTime, and it is written on EVERY readable group, not only on the one that holds the craters: which
//    group that is cannot be known from the dumps, so the row says in five languages that all of the game's ground marks are
//    concerned and does not pretend to aim at the craters alone.
//  - MaxCount and the runtime pool caps are read, logged and never written. That is why no ceiling of the mod's own is announced to
//    the player: the only ceiling in play is the game's, and the row says so rather than claiming one.
//  - ONE safety, and it waits for a fact, not a threshold: every capped pool full three passes in a row -> the chosen duration is
//    given back to the game for the rest of that battle, once. A mark already on the ground keeps the duration it was born with, so
//    the count only falls as those run out; the log says exactly that instead of promising a drain.
//  - Two lines per battle: what was written, then the peak marks held at once, the marks made, and the pools' own peak against what
//    they allow - the figures the next decision is made on.
//  - Solo only (a network battle is left alone), inert in a mission played without the mod, inert when the mod is off.
//  - Crash guard like HeureDuJour and Epaves, error counter with kill-switch back to vanilla after 20 errors.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using DecalService = Il2CppBrokenArrow.Client.Ecs.Render.Decals.DecalService;
using DecalGroup = Il2CppBrokenArrow.Client.Ecs.Render.Decals.DecalLimitGroup;
using DecalPreset = Il2CppBrokenArrow.Client.Ecs.Render.Decals.DecalLimitGroupPreset;
using DecalData = Il2CppBrokenArrow.Client.Ecs.Render.Decals.DecalData;
using DecalModel = Il2CppBrokenArrow.Client.Ecs.Render.Decals.DecalServiceModel;
using DecalArray = Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppBrokenArrow.Client.Ecs.Render.Decals.DecalData>;
using PoolGroup = Il2CppBrokenArrow.Client.Ecs.Render.ObjectPoolGroup;
using PoolArray = Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppBrokenArrow.Client.Ecs.Render.ObjectPoolGroup>;
using VegeConfig = Il2CppBrokenArrow.Client.Vegetation.VegetationDestructionConfig;
using GameSessionContext = Il2CppBrokenArrow.Client.Ecs.Configs.GameSessionContext;
using NetStatus = Il2CppBrokenArrow.Client.Ecs.GNetwork.NetworkStatus;
using NetScen = Il2CppBrokenArrow.ScriptEngine.Network.NetworkScenarioStorage;

namespace RealismOverhaul
{
    static class Decor
    {
        const string GuardVersion = "1.1";
        const int MaxErrors = 20, MaxUnclean = 3, MaxWrites = 40;
        const float TickEvery = 2f;                                  // the choice is read at most every 2 s, in the menus as well
        const float ChercheEvery = 15f;                              // a Resources scan at most every 15 s, and only while nothing is held
        const int ChercheMax = 60;                                   // and never more than this many scans in one game launch
        /// The legacy-tree scan is a Resources walk too, so it waits for the opening rush of a battle to be over before spending one.
        const float LegacyApres = 30f;

        /// The game's own LifeTime must look like a duration in seconds, or the field is something else and that preset is left alone.
        const int VieMin = 5, VieMax = 3600;
        /// Same gate on the count: a cap outside this window is not a decal budget. The count is READ ONLY - see the header.
        const int CountMin = 1, CountMax = 100000;
        /// Hard ceiling on what is ever written as a duration, whatever the player picked. It is deliberately SHORTER than a battle:
        /// a LiveDecal is born with its own LifeTime, so a duration longer than a battle is a duration nothing can ever expire under.
        const int PlafondSecondes = 1800;
        /// Full pools have to be seen this many passes in a row (about six seconds) before the duration is given back: one peak is not
        /// a saturated battle.
        const int SatPasses = 3;

        internal const string PrefCat = "RealismOverhaul_Decor";
        internal const string PrefName = "DureeDesCrateres";
        /// Own journal lot: RestoreLot takes these writes out without touching a single value of the real stats.
        internal const string LotDecor = "DECOR_CRATERES";

        /// Preference values, in the order the row cycles through them (index 0 = the mod touches nothing). "60min" and "bataille" were
        /// withdrawn on 2026-09-19: see the header. A preference left on one of them by an earlier build is migrated in CreatePrefs.
        internal static readonly (string V, TxtKey K)[] Valeurs =
        {
            ("vanille", TxtKey.DC_V_VANILLE), ("5min", TxtKey.DC_V_5), ("15min", TxtKey.DC_V_15), ("30min", TxtKey.DC_V_30),
        };
        static readonly int[] Secondes = { 0, 300, 900, 1800 };
        /// Values an older build could have saved, brought back to the longest one the row still offers.
        static readonly string[] Retirees = { "60min", "bataille" };

        static MelonPreferences_Entry<string> _choix, _guardVersion;
        static MelonPreferences_Entry<int> _unclean;

        /// One quality preset of one limit group, with the studio's own two numbers read the first time the object was seen.
        sealed class Slot
        {
            internal DecalPreset P;
            internal string Groupe, Qualite;
            internal int Vie0, Count0;                                // the game's own values, read before anything of ours was written
            internal bool Ecrivable;                                  // both numbers passed the plausibility gate
        }

        /// One runtime pool of the decal service, held for the battle and never written: its cap is the studio's frame budget, and the
        /// whole point of this module is that the mod does not raise it.
        sealed class PoolSlot
        {
            internal PoolGroup G;
            internal int Max0;
        }

        // ------------------------------------------------------------ state
        static bool _prefsOk, _dead, _refused, _pauseDite, _armed, _refusDit;
        static string _refusPourquoi;                                 // why the module gave up on this game, for the one line
        static int _errors, _writes, _cherches;
        static float _next, _nextCherche;

        static DecalService _svc;                                     // held: a ScriptableObject that lives on between battles
        static IntPtr _svcPtr;
        static bool _releveFait, _attenteDite;                        // the measurement of this service is done / "not loaded yet" said once
        static readonly List<Slot> _slots = new();
        static int _plafondBas, _plafondHaut;                         // the GAME's own count budgets, lowest and highest preset: measurement only
        static int _ecrit = -1;                                       // what we last wrote (-1 = nothing of ours is in place)
        static int _ditEcrit = -1;                                    // duration whose write detail has already been logged
        static int _dernCible = -1;                                   // duration the write pass last aimed at (a change resets the write counter)
        static int _presetsLeves;                                     // presets the last pass actually raised, for the battle's own line

        static readonly List<PoolSlot> _pools = new();                // read-only, rebuilt once per battle
        static bool _poolsLus, _poolsDits, _pleinDit;
        static int _satPasses;
        static bool _rendu;                                           // this battle already had its duration given back (pool full): once
        static int _poolCourantMax, _poolMaxTotal;                    // the peak the pools really held, and what they allowed

        static VegeConfig _vege;                                      // held the same way, read-only, never written
        static IntPtr _vegePtr;
        static bool _vegeDit, _legacyDit;

        static GameSessionContext _ctx;                               // held: the battle we are in (its address is never reused)
        static IntPtr _ctxPtr;
        static float _ctxDepuis;                                      // when this battle appeared, so the scans stay out of its opening rush
        static bool _biland;                                          // the battle already had its opening line
        static bool _everOnline, _netLogged;
        static int _tenuMax, _faitesDebut = -1, _faitesFin = -1;      // the battle's own measurement

        // what the Mod tab row shows
        const int EtNext = 0, EtDone = 1, EtAlready = 2, EtNoGame = 3, EtPlein = 4, EtOff = 5, EtReseau = 6;
        static int _etatCode = EtNext, _etatVer;

        static void Log(string s) => Mod.Log.Msg("[DECOR] " + s);

        static string Nb(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // ============================================================ preferences
        /// Called from Patch_ModTabInit.Prepare, i.e. during Mod.ApplyPatches: the entry exists long before the Options window is built,
        /// so the row is always there and a value saved in a previous session is read back.
        internal static void CreatePrefs()
        {
            if (_prefsOk) return;
            _prefsOk = true;
            var c = MelonPreferences.CreateCategory(PrefCat);
            _choix = c.CreateEntry(PrefName, "vanille", description: Build.Desc(
                "Durée des traces que le jeu laisse au sol : vanille | 5min | 15min | 30min. "
                + "« vanille » = le mod ne touche à rien. Le mod n'écrit QUE la durée, et il l'écrit sur tous les groupes de traces "
                + "que le jeu expose, pas seulement sur les cratères : quel groupe contient les cratères ne se lit pas dans le jeu, "
                + "c'est le relevé écrit dans le journal en début de bataille qui le dira. Aucun plafond de nombre n'est touché : le "
                + "budget de traces reste celui du jeu, et le jeu y reste simplement plus souvent ; à son plafond, il fait ce qu'il "
                + "fait déjà sans le mod. Ce qu'il y fait exactement n'est pas lisible dans cette version, donc si TOUTES ses réserves "
                + "de traces se remplissent, le mod lui rend la durée d'origine pour la bataille en cours. "
                + "Les épaves, le feu et la fumée ne sont pas concernés : cette version du jeu n'a aucun réglage pour eux.",
                "Durée des traces au sol : cratères et impacts (vanille | 5min | 15min | 30min)."));
            _unclean = c.CreateEntry("SessionsInterrompues", 0, description: Build.Desc("Sécurité automatique, ne pas modifier"));
            _guardVersion = c.CreateEntry("VersionSecurite", "", description: Build.Desc("Sécurité automatique, ne pas modifier"));
            if (_guardVersion.Value != GuardVersion) { _guardVersion.Value = GuardVersion; _unclean.Value = 0; }
            Migrer();
        }

        /// A value an earlier build offered and this one does not: brought back once to the longest value still on the row, with the
        /// reason, rather than leaving the row showing a name it no longer has.
        static void Migrer()
        {
            try
            {
                var v = _choix?.Value;
                if (string.IsNullOrEmpty(v)) return;
                for (int i = 0; i < Retirees.Length; i++)
                {
                    if (!string.Equals(v, Retirees[i], StringComparison.OrdinalIgnoreCase)) continue;
                    _choix.Value = "30min";
                    Save();
                    Mod.Log.Warning($"[DECOR] durée des cratères : « {v} » n'est plus proposée. Une trace au sol naît avec la durée "
                                    + "en vigueur à cet instant et la garde : une durée plus longue qu'une bataille veut dire que rien "
                                    + "ne peut plus s'effacer pendant cette bataille. Le réglage est ramené à 30 minutes ; ces valeurs "
                                    + "reviendront quand une bataille chronométrée aura mesuré ce que le jeu fait à son propre plafond");
                    return;
                }
            }
            catch { }
        }

        static void Save()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Mod.Log.Warning("[DECOR] enregistrement des réglages impossible : " + e.GetBaseException().Message); }
        }

        /// Index of the chosen value in Valeurs, or -1 for "vanille" / unreadable.
        static int Voulu()
        {
            var e = _choix;
            if (e == null) return -1;
            string v;
            try { v = e.Value; } catch { return -1; }
            if (string.IsNullOrEmpty(v)) return -1;
            for (int i = 1; i < Valeurs.Length; i++)
                if (string.Equals(v, Valeurs[i].V, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static string NomFr(int i) => Txt.Fr(i > 0 && i < Valeurs.Length ? Valeurs[i].K : TxtKey.DC_V_VANILLE);

        // ============================================================ error counter and kill-switch
        static void Fail(Exception e)
        {
            _errors++;
            if (_errors <= 3) Mod.Log.Warning("[DECOR] erreur : " + e.GetBaseException().Message);
            if (_errors < MaxErrors || _dead) return;
            _dead = true;
            Rendre("20 erreurs");
            Desarmer();
            Mod.Log.Warning($"[DECOR] {MaxErrors} erreurs : durée des cratères coupée jusqu'au redémarrage du jeu (le reste du mod fonctionne)");
            Etat(EtOff);
        }

        static void Etat(int code)
        {
            if (_etatCode == code) return;
            _etatCode = code; _etatVer++;
        }

        // ============================================================ the only entry point (ModTab.Frame, every frame, self-throttled)
        /// Never throws: the tab's frame is wrapped by a guard that kills the whole tab for the session on the first exception.
        internal static void Tick()
        {
            try
            {
                if (!_prefsOk || _dead) return;
                float now;
                try { now = UnityEngine.Time.realtimeSinceStartup; } catch { return; }
                if (now < _next) return;
                _next = now + TickEvery;
                Body(now);
            }
            catch (Exception e) { try { Fail(e); } catch { } }
        }

        static void Body(float now)
        {
            // the battle we are in: its change closes the previous one's measurement line and opens a new one
            SuivreBataille(now);

            if (Mod.Actif == null || !Mod.Actif.Value || Mod.AntiCheatActive || Identite.Blocked) { Rendre("mod coupé"); Desarmer(); return; }
            // a mission played without the mod (US_M01): our own lot is taken out here and not left to Realism.Restore, which
            // Campaign.InertTick only calls while the real stats are applied - a player who switched them off would otherwise keep our
            // durations for the one mission that must run without the mod.
            if (Campaign.MissionInerte) { Rendre("mission sans mod"); Desarmer(); return; }

            // the game's own values are read (never written) before anything else, so the log carries the measurement even for a player
            // who leaves the row on "comme le jeu": that reading is what the first test compares a stopwatch to.
            var svc = Service(now);
            // the marks alive right now and the pools' own saturation are read on EVERY pass, including a battle played on "comme le
            // jeu": that battle is the measurement battle, and its figures are what say whether a longer duration can be afforded.
            if (svc != null) Surveiller(svc);
            Vegetation(now);
            DireCeQuiEstImpossible();

            int want = Voulu();
            if (want < 0)
            {
                if (_ecrit > 0) Rendre("réglé sur « comme le jeu »");
                Desarmer();
                Etat(EtNext);
                Bilan(-1, 0, null);
                return;
            }
            if (_unclean != null && _unclean.Value >= MaxUnclean)
            {
                // said once per game start: after a restart the row must not keep promising a duration that will never be applied
                Rendre("parties non terminées normalement");
                if (!_pauseDite)
                {
                    _pauseDite = true;
                    Mod.Log.Warning($"[DECOR] {_unclean.Value} partie(s) non terminée(s) normalement après un changement de durée : "
                                    + "durée des cratères en pause par sécurité (le reste du mod fonctionne)");
                }
                Etat(EtOff);
                return;
            }
            // online: the marks are left to the game. The PUBLIC build blocks multiplayer, the personal one does not. The row says so
            // in its own state rather than going on promising a write that will not happen here.
            if (!Solo()) { Rendre("partie en réseau"); Desarmer(); Etat(EtReseau); return; }

            if (svc == null) { Etat(EtNext); return; }
            if (_refused) { Etat(EtNoGame); Bilan(want, 0, _refusPourquoi ?? "cette version du jeu ne le permet pas"); return; }
            // this battle filled the game's own pools and got its duration back: nothing is written again until the next battle, so the
            // ground goes on marking at the game's own rate instead of being pushed back into the same wall every two seconds.
            if (_rendu) { Desarmer(); Etat(EtPlein); return; }
            if (_slots.Count == 0) { Etat(EtNext); return; }          // nothing readable this pass: nothing written, nothing said

            int cible = Math.Min(Secondes[want], PlafondSecondes);
            // the write counter guards against a game that puts its own numbers back; a player cycling the row is not that, and neither
            // is a session spent in the menus, so the counter starts again on every new target.
            if (cible != _dernCible) { _dernCible = cible; _writes = 0; }

            if (!Ecrire(cible))
            {
                // nothing of ours would raise anything any more: a game that already keeps its marks longer than what is asked keeps its
                // own numbers, and whatever we had written is given back rather than left standing above them.
                string pourquoi = _refused ? _refusPourquoi : "le jeu garde déjà les traces plus longtemps";
                if (!_refused) Rendre(pourquoi);
                Desarmer();
                Etat(_refused ? EtNoGame : EtAlready);
                Bilan(want, 0, pourquoi);
                return;
            }
            _ecrit = cible;
            if (_ctx != null) Armer();                                // a battle is being fought with our values in place
            Etat(EtDone);
            Bilan(want, cible, null);
        }

        // ============================================================ the game's decal service
        /// The DecalService of the moment, with the studio's own numbers read and logged the first time each object is seen. The game's
        /// own container is asked FIRST on every pass: a service locked in from a Resources scan in the menus could be an asset nobody
        /// runs, and writing to that one would be a row that says "applied" while nothing changes in the battle.
        static DecalService Service(float now)
        {
            var svc = Mod.Svc<DecalService>() ?? _svc ?? Chercher<DecalService>(now);
            if (svc == null) return null;
            IntPtr p;
            try { p = ((Il2CppObjectBase)svc).Pointer; }
            catch { _svc = null; _svcPtr = IntPtr.Zero; return null; }
            // a wrapper with no object behind it is not a service: it would read as "the same one" for ever, since _svcPtr starts at zero
            if (p == IntPtr.Zero) { _svc = null; return null; }
            if (p != _svcPtr)
            {
                // another service object (a scene change, or the real one showing up after a scanned one): our writes on the old one are
                // given back here rather than simply forgotten, and everything measured about it dies with it.
                Rendre("un autre service de traces");
                _svc = svc; _svcPtr = p; _ecrit = -1; _ditEcrit = -1; _dernCible = -1;
                _refused = false; _refusPourquoi = null;
                _slots.Clear(); _plafondBas = 0; _plafondHaut = 0; _releveFait = false; _attenteDite = false;
                _pools.Clear(); _poolsLus = false; _poolsDits = false; _pleinDit = false;
            }
            // the measurement is retried every pass until the model is loaded, but it never says so more than once
            if (!_releveFait) Relever(svc);
            return svc;
        }

        /// Reads the whole decal budget of this game build and writes it to the log, in French, before anything of ours is written.
        /// Every group name is authored by the studio, so which one holds the craters cannot be known from the dumps: the line is what
        /// tells the author, after one battle, which of them is the one to keep an eye on.
        static void Relever(DecalService svc)
        {
            DecalModel model = null;
            try { model = svc.Model; } catch { }
            if (model == null)
            {
                // nothing is allocated on a pass that finds nothing: this one is retried every 2 s until the map is loaded
                if (_attenteDite) return;
                _attenteDite = true;
                Log("réglages des traces au sol pas encore chargés : rien n'est lu et rien n'est écrit pour l'instant");
                return;
            }
            _releveFait = true;
            _slots.Clear();
            _plafondBas = 0; _plafondHaut = 0;
            var vus = new HashSet<IntPtr>();
            var sb = new StringBuilder();
            sb.Append("réglages du jeu lus (rien n'est encore écrit) : ");
            try
            {
                // the groups as the studio authored them, plus the ones the engine itself resolves for its own decal data: both sets are
                // taken so a build that copies its groups at load cannot leave us writing an object nobody reads
                var arr = model.DecalLimitGroups;
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++) Ajouter(arr[i], vus, sb);
                foreach (var dd in DonneesDecals(model))
                {
                    DecalGroup g = null;
                    try { g = svc.GetDecalLimitGroup(dd); } catch { }
                    Ajouter(g, vus, sb);
                }
                sb.Append("; correspondance trace -> groupe : ");
                int n = 0;
                foreach (var dd in DonneesDecals(model))
                {
                    if (n++ >= 24) { sb.Append("..."); break; }
                    string nom = "?", grp = "?";
                    try { nom = dd.Name; } catch { }
                    try { grp = dd.DecalLimitGroup; } catch { }
                    sb.Append('\'').Append(nom).Append("'->").Append(grp).Append(' ');
                }
                try { sb.Append("; par image ").Append(svc._maxDecalInFrame); } catch { }
                try { sb.Append(", rayon minimal ").Append(Nb(svc._minimalAoeRadius)).Append(" m"); } catch { }
                try { sb.Append(", fusion de proximité ").Append(Nb(svc._decalFoundRadius)).Append(" m"); } catch { }
            }
            catch (Exception e) { Fail(e); }

            if (_slots.Count == 0)
            {
                _refused = true;
                _refusPourquoi = "aucun groupe de traces lisible";
                // the wording carries "impossible dans cette version du jeu" on purpose: the PUBLIC log filter keeps warnings whatever happens
                Log("durée des cratères impossible dans cette version du jeu : aucun groupe de traces au sol n'a pu être lu ; "
                    + "rien n'est changé, le jeu garde son comportement d'origine");
                return;
            }
            int ecrivables = 0;
            foreach (var s in _slots) if (s.Ecrivable) ecrivables++;
            if (ecrivables == 0)
            {
                _refused = true;
                _refusPourquoi = "les durées du jeu ne sont pas des durées";
                Log($"durée des cratères impossible dans cette version du jeu : les {_slots.Count} réglages de traces lus ne ressemblent "
                    + $"pas à des durées (attendu entre {VieMin} et {VieMax} s) ; rien n'est changé, le jeu garde son comportement d'origine");
                Log(sb.ToString());
                return;
            }
            // MEASUREMENT, not a promise: these two figures are the game's own, and the mod raises neither of them. They are given as a
            // RANGE on purpose - the engine honours exactly one of the four quality presets at a time, and which one is not in the dumps.
            sb.Append("; nombre de traces que LE JEU s'autorise à garder en même temps, tous groupes additionnés : ")
              .Append(_plafondBas).Append(" sur son préréglage le plus bas, ").Append(_plafondHaut)
              .Append(" sur le plus haut — un seul de ces quatre préréglages sert à la fois, celui de la qualité graphique en cours, et "
                      + "la ligne des réserves d'objets dira lequel ; le mod ne relève aucun de ces plafonds, il n'allonge que la durée");
            Log(sb.ToString());
        }

        /// The decal data the model owns: the three default sizes plus every per-ammunition override.
        static IEnumerable<DecalData> DonneesDecals(DecalModel model)
        {
            DecalData a = null, b = null, c = null;
            try { a = model.DefaultSmall; } catch { }
            try { b = model.DefaultMedium; } catch { }
            try { c = model.DefaultLarge; } catch { }
            if (a != null) yield return a;
            if (b != null) yield return b;
            if (c != null) yield return c;
            DecalArray over = null;
            try { over = model.OverrideDecal; } catch { }
            if (over == null) yield break;
            for (int i = 0; i < over.Length; i++)
            {
                DecalData d = null;
                try { d = over[i]; } catch { }
                if (d != null) yield return d;
            }
        }

        /// Records one group's four quality presets, each one with the studio's own two numbers, and adds that group's smallest and
        /// largest plausible count to the two measured totals. A preset seen twice (the model's array and the engine's own lookup give
        /// the same objects) is counted once. Only the LifeTime of these presets is ever written; MaxCount is read and reported.
        static void Ajouter(DecalGroup g, HashSet<IntPtr> vus, StringBuilder sb)
        {
            if (g == null) return;
            string nom = "?";
            try { nom = g.Name; } catch { }
            IntPtr gp;
            try { gp = ((Il2CppObjectBase)g).Pointer; } catch { return; }
            if (!vus.Add(gp)) return;
            sb.Append("groupe '").Append(nom).Append("' [");
            int bas = 0, haut = 0;
            for (int q = 0; q < 4; q++)
            {
                DecalPreset p = null;
                string qual = q == 0 ? "haute" : q == 1 ? "moyenne" : q == 2 ? "basse" : "minimale";
                try { p = q == 0 ? g.High : q == 1 ? g.Medium : q == 2 ? g.Low : g.Potato; }
                catch { }
                if (p == null) { sb.Append(qual).Append("=absente "); continue; }
                int vie, cnt;
                try { vie = p.LifeTime; cnt = p.MaxCount; }
                catch { sb.Append(qual).Append("=illisible "); continue; }
                bool ok = vie >= VieMin && vie <= VieMax && cnt >= CountMin && cnt <= CountMax;
                _slots.Add(new Slot { P = p, Groupe = nom, Qualite = qual, Vie0 = vie, Count0 = cnt, Ecrivable = ok });
                sb.Append(qual).Append('=').Append(vie).Append("s/").Append(cnt).Append(ok ? " " : " (hors gabarit) ");
                if (!ok) continue;
                if (bas == 0 || cnt < bas) bas = cnt;
                if (cnt > haut) haut = cnt;
            }
            sb.Append("] ");
            // which of the four the engine is really running is not in the dumps; the pool line logged once per battle is what settles it
            _plafondBas += bas;
            _plafondHaut += haut;
        }

        // ============================================================ the write itself
        /// Writes the chosen duration on every plausible preset. ONE property, LifeTime. MaxCount and the runtime pool caps are never
        /// touched - see the header: that is what keeps the number of decal projectors on screen inside the studio's own budget.
        /// Returns false when nothing needed writing (the game is already longer) or when the module gave up.
        static bool Ecrire(int cible)
        {
            if (_writes >= MaxWrites)
            {
                // the game puts its own numbers back at every pass: we stop fighting it instead of writing for ever. The counter counts
                // write PASSES made while a battle is running, not presets and not menu edits - counting either would trip on a player
                // who simply clicked through the row.
                _refused = true;
                _refusPourquoi = "les valeurs sont reprises par le jeu";
                Log($"les durées des traces au sol sont reprises par le jeu ({MaxWrites} passes d'écriture dans la même bataille) : "
                    + "le mod n'y touche plus ici, le jeu garde son comportement d'origine");
                Rendre("valeurs reprises par le jeu");
                return false;
            }
            bool quelqueChose = false, ecrit = false;
            int leves = 0;
            StringBuilder sb = null;
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                if (!s.Ecrivable) continue;
                int vieVoulue = Math.Max(s.Vie0, cible);              // only ever raises: a group the studio keeps longer keeps its value
                if (vieVoulue <= s.Vie0) continue;
                quelqueChose = true;
                leves++;
                int vie;
                try { vie = s.P.LifeTime; }
                catch (Exception e) { Fail(e); continue; }
                if (vie == vieVoulue) continue;
                try
                {
                    using (Realism.Lot(LotDecor)) Realism.SetJournaled(s.P, "LifeTime", vieVoulue);
                    ecrit = true;
                    if (_ditEcrit != cible)
                    {
                        sb ??= new StringBuilder();
                        sb.Append('\'').Append(s.Groupe).Append("'/").Append(s.Qualite).Append(' ')
                          .Append(s.Vie0).Append("s -> ").Append(vieVoulue).Append("s (plafond ").Append(s.Count0)
                          .Append(" inchangé) ; ");
                    }
                }
                catch (Exception e) { Fail(e); }
            }
            _presetsLeves = leves;
            if (ecrit && _ctx != null) _writes++;
            if (sb != null)
            {
                // said once per chosen duration, so the author reads exactly which group took which value and can tell the craters apart.
                // The reserve travels with the line: the write itself is certain, the effect on a mark is a deduction from the shape of
                // the game's classes (a preset holds a duration, a live mark holds its own), never something read in the game's code.
                _ditEcrit = cible;
                Log("écrit (durée seulement, valeur du jeu -> valeur du mod) : " + sb.ToString().TrimEnd(' ', ';')
                    + " — écriture faite et relue ; que le jeu prenne bien cette durée quand une trace apparaît reste à confirmer par "
                    + "une bataille chronométrée, c'est pourquoi la ligne de l'onglet dit « écrit » et non « appliqué »");
            }
            return quelqueChose;
        }

        /// Puts the game's own numbers back and forgets that anything of ours was written.
        static void Rendre(string why)
        {
            if (_ecrit <= 0) return;
            _ecrit = -1; _ditEcrit = -1; _dernCible = -1;
            try
            {
                int n = Realism.RestoreLot(LotDecor, "durée des cratères : " + why);
                Log($"durées des traces au sol rendues au jeu ({why}) : {n} valeur(s) remise(s)");
            }
            catch (Exception e) { Mod.Log.Warning("[DECOR] valeurs non rendues : " + e.GetBaseException().Message); }
        }

        // ============================================================ measurement, and the one safety this module has
        /// Reads how many marks are alive, how many have been made, and how full the game's own object pools are. NOTHING here is
        /// written. Two different things come out of it:
        ///  - MEASUREMENT, always, even on "comme le jeu": the pools' own caps (which is what finally says which quality preset the
        ///    engine runs), the first time any of them fills up, and the peak against the allowed total at the end of the battle.
        ///  - THE ONE ACTION, and only on a condition that needs no invented threshold: the WHOLE readable budget saturated
        ///    (every pool at or over its own cap) for three passes in a row. That is the only state in which the engine has nowhere
        ///    left to put a new mark whatever it does at its ceiling. The chosen duration is then given back for the rest of the
        ///    battle, once, so the ground goes on marking at the game's own rate as the long marks expire. Marks already down keep the
        ///    duration they were born with - the mod cannot erase one - so the count only falls as they run out, and the log says so.
        ///    What _currentTotalObjects really counts is said with it, because it decides what the reading means: it is the number of
        ///    objects the game has HAD to create for that pool, i.e. its peak demand, and nothing in the class brings it back down
        ///    while a battle runs. So "full" here means "this pool has reached its ceiling at least once", not "it is busy right now",
        ///    and the action that follows is the safe direction anyway - the game gets its own duration back, nothing else changes.
        static void Surveiller(DecalService svc)
        {
            int tenu = -1, faites = -1;
            try { var d = svc._timeLiveDecals; if (d != null) tenu = d.Count; } catch { }
            try { faites = svc._lastDecalId; } catch { }
            if (faites >= 0) { if (_faitesDebut < 0) _faitesDebut = faites; _faitesFin = faites; }
            if (tenu > _tenuMax) _tenuMax = tenu;

            Pools(svc);
            int lus = 0, avecPlafond = 0, pleins = 0, courant = 0, permis = 0;
            for (int i = 0; i < _pools.Count; i++)
            {
                int cur, max;
                try { cur = _pools[i].G._currentTotalObjects; max = _pools[i].G._maxTotalObjects; }
                catch { continue; }
                lus++;
                if (max <= 0) continue;                               // a pool with no cap says nothing about saturation either way
                avecPlafond++;
                courant += cur;
                permis += max;
                if (cur >= max) pleins++;
            }
            if (courant > _poolCourantMax) _poolCourantMax = courant;
            if (permis > _poolMaxTotal) _poolMaxTotal = permis;

            if (lus > 0 && !_poolsDits && _ctx != null)
            {
                // the measurement the dumps cannot give: these caps are built by Initialize() from the preset of the quality the player
                // is actually running, so comparing them with the four preset figures above says WHICH preset is live.
                _poolsDits = true;
                var sb = new StringBuilder();
                sb.Append("réserves d'objets du jeu pour les traces (lues, jamais écrites) : ").Append(lus).Append(" réserve(s), plafonds ");
                for (int i = 0; i < _pools.Count && i < 16; i++) sb.Append(_pools[i].Max0).Append(' ');
                sb.Append("— à comparer avec les plafonds par qualité ci-dessus : celui qui correspond est le préréglage que le jeu "
                          + "utilise vraiment sur cette machine");
                Log(sb.ToString());
            }
            if (pleins > 0 && !_pleinDit && _ctx != null)
            {
                // measurement, not an action: a small pool can sit at its own cap in a perfectly normal battle. What the author needs
                // to know is WHEN it happens and with how many marks on the ground.
                _pleinDit = true;
                Log($"première réserve pleine de la bataille : {pleins} sur {avecPlafond} ({courant} objets créés pour {permis} permis, "
                    + $"{tenu} traces vivantes) ; ce compte est celui des objets que le jeu a dû créer et il ne redescend pas, donc "
                    + "« pleine » veut dire « a atteint son plafond au moins une fois » ; rien n'est fait, c'est une mesure");
            }

            // The only action, and the condition is a fact, not a threshold: while one capped pool still has room the engine has
            // somewhere to put a new mark, whatever it does at its ceilings. Only when none of them has is the ground really full.
            if (_ecrit <= 0 || _rendu || avecPlafond == 0 || pleins < avecPlafond) { _satPasses = 0; return; }
            if (++_satPasses < SatPasses) return;
            _rendu = true;
            Log($"toutes les réserves de traces du jeu ont atteint leur plafond ({pleins} sur {avecPlafond} ; {courant} objets créés "
                + $"pour {permis} permis ; {tenu} traces vivantes) : la durée choisie est rendue au jeu pour le reste de cette "
                + "bataille. Les traces déjà posées gardent la durée qu'elles avaient à leur naissance : le mod ne peut en effacer "
                + "aucune, le nombre tenu ne baissera qu'à leur expiration. Ce que le jeu fait exactement à son plafond n'est pas "
                + "lisible dans cette version ; cette ligne est la mesure qui le dira");
            Rendre("les réserves de traces du jeu sont pleines");
            Desarmer();
            Etat(EtPlein);
        }

        /// The runtime pools the decal service really draws from, taken once per battle. They are built in DecalService.Initialize()
        /// from the presets, so before the first battle there is nothing to take. Nothing is ever WRITTEN here: leaving their cap alone
        /// is what bounds the frame cost of this row by a figure the studio chose.
        static void Pools(DecalService svc)
        {
            if (_poolsLus) return;
            PoolArray arr = null;
            try { arr = svc._objectPoolGroups; } catch { return; }
            if (arr == null || arr.Length == 0) return;
            _poolsLus = true;
            for (int i = 0; i < arr.Length && i < 64; i++)
            {
                PoolGroup g = null;
                try { g = arr[i]; } catch { }
                if (g == null) continue;
                int max;
                try { max = g._maxTotalObjects; } catch { continue; }
                _pools.Add(new PoolSlot { G = g, Max0 = max });
            }
        }

        // ============================================================ trees (read-only, nothing is ever written)
        /// Logs the studio's own vegetation numbers once per config object. What the dumps DO settle is stated; what they only suggest
        /// is stated as something to confirm, and the sentence that says a felled tree stays down is written by Legacy(), after the map
        /// has been looked at - not here, and never from a menu.
        static void Vegetation(float now)
        {
            Legacy(now);                                              // per map, so it is checked again at every battle
            var cfg = _vege;
            if (cfg == null)
            {
                cfg = Chercher<VegeConfig>(now);
                if (cfg == null) return;
            }
            IntPtr p;
            try { p = ((Il2CppObjectBase)cfg).Pointer; } catch { _vege = null; return; }
            if (p == IntPtr.Zero) { _vege = null; return; }
            if (p != _vegePtr) { _vege = cfg; _vegePtr = p; _vegeDit = false; }   // another config object: it gets its own line
            if (_vegeDit) return;
            _vegeDit = true;
            try
            {
                var c = _vege;
                Log("arbres : réglages du jeu lus (RIEN N'EST ÉCRIT) — destruction "
                    + (c.Enabled ? "active" : "inactive")
                    + $", arbres physiques à la fois {c.MaxActivePhysicalTrees}, par explosion {c.MaxPhysicalTreesPerExplosion}"
                    + $", distance de physique {Nb(c.PhysicalSpawnDistance)} m"
                    + $", durée physique {Nb(c.MinimumPhysicalLifetime)} à {Nb(c.MaximumPhysicalLifetime)} s"
                    + $", vitesse d'immobilisation {Nb(c.SettleVelocity)} / {Nb(c.SettleAngularVelocity)}"
                    + $", tampon initial {c.InitialOutcomeBufferCapacity}"
                    + " — ce que le code montre clairement : ces deux durées ne règlent que le temps pendant lequel l'arbre bascule "
                    + "encore comme un corps physique, avant d'être figé dans le tampon d'affichage ; elles ne règlent pas sa présence "
                    + "au sol. Les allonger allonge donc la chute, pas la durée pendant laquelle l'arbre reste couché. Ce qui reste à "
                    + "confirmer en bataille chronométrée : combien de temps un arbre abattu reste ensuite au sol. Le mod n'écrit rien "
                    + "de tout ceci");
            }
            catch (Exception e) { Fail(e); }
        }

        /// The one unknown of the tree question, checked once per battle because it is a scene object, not an asset: the older
        /// Unity-Terrain path (TreeDestructionManager, TreePool._lifeTimeSecond, ReturnToPoolWithDelay) DOES recycle its trees on a
        /// timer and is still in this build. Nothing about felled trees is said before this check has run, and it runs on the same scan
        /// budget as everything else here, a good half-minute into the battle, so it never lands in the opening rush.
        static void Legacy(float now)
        {
            if (_legacyDit || _ctx == null) return;                   // a battle only: in a menu there is no map to look at
            if (now < _ctxDepuis + LegacyApres) return;               // the opening seconds of a battle already carry every spike there is
            if (now < _nextCherche || _cherches >= ChercheMax) return;// same budget as Chercher<T>: one Resources walk at a time, rarely
            _nextCherche = now + ChercheEvery;
            _cherches++;
            _legacyDit = true;
            int n = LegacyArbres();
            if (n > 0)
                Mod.Log.Warning($"[DECOR] arbres : {n} gestionnaire(s) d'arbres hérité(s) (TreeDestructionManager) sur cette carte ; "
                                + "celui-là recycle bien les arbres sur une minuterie, donc sur une carte pareille les arbres abattus ne "
                                + "restent PAS — à vérifier en jeu avant de promettre quoi que ce soit au joueur");
            else if (n == 0)
                Log("arbres : aucun gestionnaire d'arbres hérité sur cette carte, c'est bien le chemin moderne qui est utilisé. "
                    + "Sur ce chemin, un arbre abattu est figé dans un tampon d'affichage ; le seul retrait qu'on voie dans le code "
                    + "agit sur l'arbre DEBOUT (il l'efface avec des matrices nulles), et rien ne montre qu'un arbre déjà tombé soit "
                    + "repris. Il a donc l'air de rester au sol toute la bataille — à confirmer par une bataille chronométrée avant "
                    + "d'en faire une promesse. Le mod n'écrit rien pour les arbres, dans un cas comme dans l'autre");
            else
                Log("arbres : présence d'un gestionnaire d'arbres hérité non vérifiable dans cette version du jeu ; "
                    + "c'est la seule inconnue qui reste sur les arbres");
        }

        /// Number of legacy Unity-Terrain tree managers in the scene, or -1 when the type cannot be reached. Il2Cpp.TreeDestructionManager
        /// lives in the Assembly-CSharp interop assembly, which this project deliberately does NOT reference (the vegetation config this
        /// module READS lives in Il2CppBrokenArrow.dll and needs nothing else). MelonLoader loads that assembly anyway, so the type is
        /// found by plain managed reflection at runtime and only COUNTED: its own TreeLifeTimeSecond is left unread rather than reached
        /// through IL2CPP reflection that a stripped build could break, and nothing of the trees is ever written.
        static int LegacyArbres()
        {
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n = null;
                    try { n = a.GetName().Name; } catch { }
                    if (!string.Equals(n, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) continue;
                    try { t = a.GetType("Il2Cpp.TreeDestructionManager", false); } catch { }
                    if (t != null) break;
                }
                if (t == null) return -1;
                var it = Il2CppType.From(t, false);
                if (it == null) return -1;
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(it);
                return arr == null ? -1 : arr.Length;
            }
            catch { return -1; }
        }

        // ============================================================ what this game build simply does not allow
        /// Said once per game launch, in French, so the author can copy it as it stands: these are the three things the moderator asked
        /// for that no value of this build can give, with the reason for each. A clean refusal is an answer; a figure that changes
        /// nothing is not. Nothing is said here about the trees: that answer depends on the map and belongs to Legacy().
        static void DireCeQuiEstImpossible()
        {
            if (_refusDit) return;
            _refusDit = true;
            Log("ce que cette version du jeu ne permet pas, et il vaut mieux le dire que de faire semblant :");
            Log("  1) garder une épave (véhicule, char, avion, hélicoptère) 60 minutes ou indéfiniment : le jeu n'a aucune minuterie "
                + "d'épave, et le modèle n'est pas laissé au sol — il est rendu à une réserve d'objets qui sert ensuite à faire "
                + "apparaître d'autres unités ; le retenir affamerait cette réserve, et tout le chemin qui le range est interdit au mod");
            Log("  2) faire brûler une épave 8 à 10 minutes puis fumer 10 de plus : le feu et la fumée appartiennent à la vue de "
                + "l'unité, et les deux systèmes de nettoyage les éteignent explicitement avec elle ; en plus les effets viennent d'une "
                + "réserve limitée, dimensionnée par les réglages de qualité, donc un feu tenu dix minutes est un feu qu'aucune autre "
                + "explosion ne pourra plus afficher");
            Log("  3) faire brûler un bâtiment 15 à 20 minutes : un bâtiment détruit ne brûle pas dans ce jeu — son modèle est remplacé "
                + "par une ruine définitive, avec une bouffée de poussière et des gravats qui tombent ; il n'y a aucun feu de bâtiment "
                + "à rallonger");
        }

        // ============================================================ a scan of the loaded objects, kept rare on purpose
        /// Resources.FindObjectsOfTypeAll walks every loaded object, so it is called at most once every 15 s, only while the object is
        /// not held, at most once per pass, and never more than ChercheMax times in one game launch. Legacy() spends from the same budget.
        static T Chercher<T>(float now) where T : Il2CppObjectBase
        {
            if (now < _nextCherche || _cherches >= ChercheMax) return null;
            _nextCherche = now + ChercheEvery;
            _cherches++;
            try
            {
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
                if (arr == null) return null;
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i]?.TryCast<T>();
                    if (c != null) return c;
                }
            }
            catch (Exception e) { Fail(e); }
            return null;
        }

        // ============================================================ crash guard (same rule as HeureDuJour and Epaves)
        /// A battle fought with our values in place is counted; reaching a menu or another battle clears it. Only a game that died
        /// mid-battle leaves the count standing.
        static void Armer()
        {
            if (_armed || _unclean == null) return;
            _armed = true;
            _unclean.Value = _unclean.Value + 1;
            Save();
        }

        static void Desarmer()
        {
            if (!_armed) return;
            _armed = false;
            if (_unclean != null && _unclean.Value != 0) { _unclean.Value = 0; Save(); }
        }

        // ============================================================ battle tracking (read-only, no hook)
        static void SuivreBataille(float now)
        {
            GameSessionContext ctx = null;
            try { ctx = Campaign.Ctx(); } catch { }
            IntPtr p = IntPtr.Zero;
            if (ctx != null) { try { p = ((Il2CppObjectBase)ctx).Pointer; } catch { p = IntPtr.Zero; } }
            if (p == _ctxPtr) return;
            if (_ctxPtr != IntPtr.Zero) Cloture();                    // the battle that was running has just ended: its measurement line
            _ctx = ctx; _ctxPtr = p; _ctxDepuis = now;
            _biland = false;
            _writes = 0; _dernCible = -1;
            _tenuMax = 0; _faitesDebut = -1; _faitesFin = -1;
            _pools.Clear(); _poolsLus = false; _poolsDits = false; _pleinDit = false;   // the pools are built per battle: the old ones say nothing about this one
            _satPasses = 0; _rendu = false; _poolCourantMax = 0; _poolMaxTotal = 0;
            _legacyDit = false;                                       // the legacy tree manager is a scene object: one look per map
            _everOnline = false; _netLogged = false;
            Desarmer();                       // back to a menu or into another battle: the previous one ended without a crash
        }

        /// Solo latch of the battle: anything that looks like a network session makes the module inert, and so does an unreadable state.
        static bool Solo()
        {
            try
            {
                bool net = NetScen.IsNetwork, slave = NetScen.IsScenarioSlave, host = NetScen.IsScenarioHost;
                string st = NetStatus.Status.ToString();
                if (net || slave || host || st == "Loading" || st == "Deploy" || st == "Game") _everOnline = true;
                if (!_netLogged) { _netLogged = true; Log($"réseau : état={st} -> {(_everOnline ? "en ligne" : "solo")}"); }
            }
            catch { return false; }
            return !_everOnline;
        }

        // ============================================================ the two lines per battle
        /// Opening line: what was chosen, what it really changed, and what was refused and why.
        static void Bilan(int want, int cible, string refus)
        {
            if (_biland || _ctx == null) return;                      // one line per battle, never a line from a menu
            _biland = true;
            string tete = "durée choisie : " + NomFr(want);
            if (want < 0) { Log(tete + " — le mod ne touche à rien, les traces au sol gardent la durée du jeu"); return; }
            if (refus != null)
            {
                Log(tete + " — NON APPLIQUÉE (" + refus + ") : le jeu garde son comportement d'origine");
                return;
            }
            Log(tete + $" — durée portée à {cible} s sur {_presetsLeves} réglage(s) allongé(s), sur {_slots.Count} lus"
                + " ; aucun plafond de nombre n'est touché : le budget de traces reste celui du jeu, et le jeu y restera simplement "
                + "plus souvent — à son plafond, il fera ce qu'il fait déjà sans le mod"
                + " ; si TOUTES ses réserves de traces se remplissent (quand elles sont lisibles), la durée lui est rendue pour le "
                + "reste de la bataille"
                + " ; épaves, feu et fumée : inchangés, cette version du jeu n'a rien à régler pour eux");
        }

        /// Closing line of a battle: what was really held, what was really made, and what the game's own pools really did. This is the
        /// figure the next battle is read against - nothing here is a guess.
        static void Cloture()
        {
            if (!_biland) return;                                     // a battle that never got its opening line has nothing to close
            int faites = _faitesDebut >= 0 && _faitesFin >= _faitesDebut ? _faitesFin - _faitesDebut : -1;
            Log($"fin de bataille — traces au sol tenues en même temps au maximum : {_tenuMax}"
                + (faites >= 0 ? $" ; traces posées pendant la bataille : {faites}" : " ; traces posées : non lisibles")
                + (_poolMaxTotal > 0
                    ? $" ; réserves du jeu : {_poolCourantMax} objets créés au plus pour {_poolMaxTotal} permis"
                    : " ; réserves du jeu : non lisibles dans cette version")
                + (_rendu ? " ; toutes les réserves ont atteint leur plafond : la durée a été rendue au jeu en cours de bataille"
                          : _pleinDit ? " ; une réserve au moins a atteint son plafond, mais pas toutes : rien n'a été rendu"
                                      : " ; aucune réserve n'a atteint son plafond")
                + (_ecrit > 0 ? $" ; durée en place à la fin : {_ecrit} s" : " ; rien n'était écrit à la fin"));
        }

        // ============================================================ texts of the Mod tab row
        /// Changes whenever the row's value or the module's answer changes: the tab rebuilds its description then, and never per frame.
        internal static int EtatVersion => _etatVer * 8 + (Voulu() + 1);

        internal static string RowDesc(Lang l)
        {
            string body = Txt.Get(l, TxtKey.DC_DESC);
            int want = Voulu();
            if (want < 0) return body + "\n\n" + Txt.Get(l, TxtKey.DC_ST_OFF);
            string etat;
            switch (_etatCode)
            {
                case EtDone: etat = Txt.Format(l, TxtKey.DC_ST_DONE, Valeurs[want].K); break;
                case EtAlready: etat = Txt.Get(l, TxtKey.DC_ST_ALREADY); break;
                case EtNoGame: etat = Txt.Get(l, TxtKey.DC_ST_NOGAME); break;
                case EtPlein: etat = Txt.Get(l, TxtKey.DC_ST_PLEIN); break;
                case EtReseau: etat = Txt.Get(l, TxtKey.DC_ST_RESEAU); break;
                case EtOff: etat = Txt.Get(l, TxtKey.DC_ST_SAFETY); break;
                default: etat = Txt.Format(l, TxtKey.DC_ST_NEXT, Valeurs[want].K); break;
            }
            return body + "\n\n" + etat;
        }
    }
}
