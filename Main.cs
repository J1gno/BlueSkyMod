// ============================================================================
// Main.cs — Mod Entry Point, Lifecycle & Harmony Patch Registration
// ============================================================================
// This mod is split across partial classes of BlueSkyMod.Main:
//   - Main.cs               : Mod metadata, lifecycle hooks, Harmony patch wiring, helpers
//   - ItemDefinitions.cs    : Custom items, prices, recipes, '99.1% Pure' effect, shop injection
//   - StationInjection.cs   : Chemistry Station UI/recipe hooks, Lab Oven shatter & MP safety nets
//   - VisualsAndAvatars.cs  : Color palette, icon tinting, stored/held/multiplayer avatar visuals
//   - UninstallWorkflow.cs  : In-game `bluesky_uninstall` command & `UninstallOnNextLoad` flow
//   - BlueSkySaveCleaner.cs : Item/recipe ID registry (`BlueSkyIds`) & standalone JSON save scrubber
// ============================================================================

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using S1API.Console;
using S1API.Lifecycle;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Effects;
using Il2CppScheduleOne.Equipping;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI.Stations;
using Il2CppSystem.Collections.Generic;
using S1Avatar = Il2CppScheduleOne.AvatarFramework.Avatar;
using S1Npc = Il2CppScheduleOne.NPCs.NPC;

// Mod identity shown in MelonLoader logs & Nexus Mods metadata.
[assembly: MelonInfo(typeof(BlueSkyMod.Main), "Blue Sky Mod", BlueSkyMod.Main.ModVersion, "J1gno")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonAdditionalDependencies("S1API")]
[assembly: System.Reflection.AssemblyMetadata("NexusModId", "3235")]

namespace BlueSkyMod
{
    public partial class Main : MelonMod
    {
        // Bump ModVersion when publishing a new release.
        public const string ModVersion = "1.9.1";
        // Build variant is controlled by the `INSTANT_UNLOCK` define in BlueSkyMod_InstantUnlock.csproj.
#if INSTANT_UNLOCK
        private const string BuildVariant = "InstantUnlock";
        private const string ModFileName = "BlueSkyMod_InstantUnlock.dll";
#else
        private const string BuildVariant = "Standard";
        private const string ModFileName = "BlueSkyMod.dll";
#endif

        // Maximum time PostLoadInjectionRoutine waits for Registry/ProductManager/ProductIconManager.
        private const float ReadyTimeoutSeconds = 120f;

        private static MelonPreferences_Category _prefsCategory;
        private static MelonPreferences_Entry<bool> _prefUninstallOnNextLoad;
        private static bool _suppressInjectionThisLoad;
        private static bool _blockGameSavesForUninstall;
        private static bool _uninstallRunning;
        private static object _uninstallToken;
        private static readonly System.Collections.Generic.HashSet<string> _cleanedSavePaths =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Main Instance { get; private set; }

        public static QualityItemDefinition PristinePseudo { get; private set; }
        public static ItemDefinition LiquidBlue { get; private set; }
        public static MethDefinition BlueSky { get; private set; }

        private static Effect _blueSkyEffect;
        private static GameObject _prefabHolder;
        private static GameObject _customPseudoStationPrefab;
        private static GameObject _customLiquidPrefab;
        private static StationRecipe _labRecipe;

        private static object _postLoadToken;
        private static object _shopToken;

        private static readonly WaitForSeconds _shopPollWait = new WaitForSeconds(0.5f);

        private static MethodInfo _setSelectedRecipeMethod;
        private static bool _definitionsReady;
        private static int _iconsInjectedForManagerId;

        private static readonly System.Collections.Generic.HashSet<string> _warnedKeys =
            new System.Collections.Generic.HashSet<string>();

        private static void LogMsg(string message)
        {
            if (Instance != null) Instance.LoggerInstance.Msg(message); else MelonLogger.Msg(message);
        }

        private static void LogWarn(string message)
        {
            if (Instance != null) Instance.LoggerInstance.Warning(message); else MelonLogger.Warning(message);
        }

        private static void LogWarnOnce(string key, string message)
        {
            if (_warnedKeys.Add(key)) LogWarn(message);
        }

        private static void LogError(string message)
        {
            if (Instance != null) Instance.LoggerInstance.Error(message); else MelonLogger.Error(message);
        }

        public override void OnInitializeMelon()
        {
            Instance = this;

            // Add or customize MelonPreferences entries (stored in UserData/MelonPreferences.cfg).
            _prefsCategory = MelonPreferences.CreateCategory("BlueSkyMod", "Blue Sky Mod");
            _prefUninstallOnNextLoad = _prefsCategory.CreateEntry(
                "UninstallOnNextLoad",
                false,
                "Uninstall On Next Load",
                "Set to true before loading a save to back up and scrub all Blue Sky Mod data from that save so the mod can be safely removed. Resets to false automatically.");

            CacheReflectionMetadata();

            // Harmony patch registration.
            // If a Schedule I update renames a method or changes parameter types, update the matching TryPatch call below.
            PatchChemistryStationInterface();

            // Early definition & effect registration (ensures items exist before save load / MP client replication).
            TryPatch(typeof(Registry), "Awake", Type.EmptyTypes, null, new HarmonyMethod(typeof(Registry_Awake_Patch), "Postfix"));
            TryPatch(typeof(ProductManager), "Awake", Type.EmptyTypes, null, new HarmonyMethod(typeof(ProductManager_Awake_Patch), "Postfix"));
            TryPatch(typeof(ProductManager), "OnStartClient", Type.EmptyTypes, new HarmonyMethod(typeof(ProductManager_OnStartClient_Patch), "Prefix"), null);
            TryPatch(typeof(ProductManager), "CheckDiscovery", new[] { typeof(ItemInstance) }, new HarmonyMethod(typeof(ProductManager_CheckDiscovery_Patch), "Prefix"), null);
            TryPatch(typeof(ProductIconManager), "Awake", Type.EmptyTypes, null, new HarmonyMethod(typeof(ProductIconManager_Awake_Patch), "Postfix"));
            TryPatch(typeof(PropertyUtility), "Awake", Type.EmptyTypes, null, new HarmonyMethod(typeof(PropertyUtility_Awake_Patch), "Postfix"));
            TryPatch(typeof(PropertyUtility), "GetProperties", new[] { typeof(List<string>) }, new HarmonyMethod(typeof(PropertyUtility_GetProperties_Patch), "Prefix"), null);

            // Mix pricing & late-replicated mix visual flushing.
            TryPatch(typeof(ProductDefinition), "Initialize", new[] { typeof(List<Effect>), typeof(List<EDrugType>) },
                new HarmonyMethod(typeof(ProductDefinition_Initialize_Patch), "Prefix"),
                null);
            TryPatch(typeof(MethDefinition), "ApplyAppearanceSettings", Type.EmptyTypes,
                null,
                new HarmonyMethod(typeof(MethDefinition_ApplyAppearanceSettings_Patch), "Postfix"));

            // Station recipes, world/stored/held visuals, multiplayer third-person equippables, and lab oven shatter visuals.
            TryPatch(typeof(StationRecipeField), "Load", new[] { typeof(StationRecipeFieldData) }, new HarmonyMethod(typeof(StationRecipeField_Load_Patch), "Prefix"), null);
            TryPatch(typeof(LiquidMeth_StationItem), "Initialize", null, null, new HarmonyMethod(typeof(LiquidVisual_Initialize_Patch), "Postfix"));
            TryPatch(typeof(StoredItem), "InitializeStoredItem", new[] { typeof(StorableItemInstance), typeof(StorageGrid), typeof(Vector2), typeof(float) }, null, new HarmonyMethod(typeof(StoredItem_Initialize_Patch), "Postfix"));
            TryPatch(typeof(Equippable_Viewmodel), "Equip", new[] { typeof(ItemInstance) }, null, new HarmonyMethod(typeof(Equippable_Equip_Patch), "Postfix"));
            TryPatch(typeof(Equippable_Viewmodel), "PlayUnequipAnimation", Type.EmptyTypes, null, new HarmonyMethod(typeof(Equippable_PlayUnequipAnimation_Patch), "Postfix"));
            TryPatch(typeof(Equippable_Viewmodel), "PlayEquipAnimation", Type.EmptyTypes, new HarmonyMethod(typeof(Equippable_Viewmodel_PlayEquipAnimation_Patch), "Prefix"), null);
            TryPatch(typeof(ProductConsumeAnimation), "StopConsume", Type.EmptyTypes, null, new HarmonyMethod(typeof(ProductConsumeAnimation_StopConsume_Patch), "Postfix"));
            TryPatch(typeof(S1Avatar), "SetEquippable", new[] { typeof(string) }, new HarmonyMethod(typeof(Avatar_SetEquippable_Patch), "Prefix"), null);
            TryPatch(typeof(LabOven), "Shatter", new[] { typeof(int), typeof(GameObject) },
                new HarmonyMethod(typeof(LabOven_Shatter_Patch), "Prefix"),
                new HarmonyMethod(typeof(LabOven_Shatter_Patch), "Postfix"));
            TryPatch(typeof(LabOven), "Destroy", Type.EmptyTypes, null, new HarmonyMethod(typeof(LabOven_Destroy_Patch), "Postfix"));

            // Save/load lifecycle hooks for safe uninstall & save blocking.
            TryPatch(typeof(LoadManager), "StartGame", new[] { typeof(SaveInfo), typeof(bool), typeof(bool) }, new HarmonyMethod(typeof(LoadManager_StartGame_Patch), "Prefix"), null);
            TryPatch(typeof(LoadManager), "LoadAsClient", new[] { typeof(string) }, new HarmonyMethod(typeof(LoadManager_LoadAsClient_Patch), "Prefix"), null);
            TryPatch(typeof(SaveManager), "Save", new[] { typeof(string) }, new HarmonyMethod(typeof(SaveManager_Save_Patch), "Prefix"), null);

            // Custom '99.1% Pure' player & NPC consumption effects.
            TryPatch(typeof(ProductItemInstance), "ApplyEffectsToPlayer", new[] { typeof(Player) }, null, new HarmonyMethod(typeof(Player_ApplyEffects_Patch), "Postfix"));
            TryPatch(typeof(ProductItemInstance), "ClearEffectsFromPlayer", new[] { typeof(Player) }, null, new HarmonyMethod(typeof(Player_ClearEffects_Patch), "Postfix"));
            TryPatch(typeof(ProductItemInstance), "ApplyEffectsToNPC", new[] { typeof(S1Npc) },
                new HarmonyMethod(typeof(Npc_ApplyEffects_Patch), "Prefix"),
                new HarmonyMethod(typeof(Npc_ApplyEffects_Patch), "Postfix"));
            TryPatch(typeof(ProductItemInstance), "ClearEffectsFromNPC", new[] { typeof(S1Npc) }, null, new HarmonyMethod(typeof(Npc_ClearEffects_Patch), "Postfix"));

            PatchCookOperationSafetyNet();

            // S1API fires both OnPreLoad and OnLoadComplete for Singleplayer, Host, and Multiplayer Clients.
            GameLifecycle.OnPreLoad      += OnPreLoad;
            GameLifecycle.OnLoadComplete += OnLoadComplete;

            LogMsg($"Blue Sky Mod v{ModVersion} Initialized.");
        }

        public override void OnDeinitializeMelon()
        {
            GameLifecycle.OnPreLoad      -= OnPreLoad;
            GameLifecycle.OnLoadComplete -= OnLoadComplete;
            CancelRoutines();
            if (_uninstallToken != null) { MelonCoroutines.Stop(_uninstallToken); _uninstallToken = null; }
            _uninstallRunning = false;
            _blockGameSavesForUninstall = false;
            try { HarmonyInstance?.UnpatchSelf(); }
            catch { }
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _suppressInjectionThisLoad = false;
                _blockGameSavesForUninstall = false;
                ResetLoadState();
            }
        }

        private static void CancelRoutines()
        {
            if (_postLoadToken != null) { MelonCoroutines.Stop(_postLoadToken); _postLoadToken = null; }
            if (_shopToken     != null) { MelonCoroutines.Stop(_shopToken);     _shopToken     = null; }
        }

        // Clears per-session runtime caches when returning to Menu or starting a new save/MP join.
        // Add any new per-session static collections here to prevent stale references across loads.
        private static void ResetLoadState()
        {
            CancelRoutines();
            ClearLocalPlayerBlueSkyEffects();
            _definitionsReady                = false;
            _iconsInjectedForManagerId       = 0;
            _effectRegisteredPropUtilId      = 0;
            _customFallbackAvatarEquipActive = false;
            _cachedFallbackAvatarPath        = null;
            _ovenDefaultShatterColors.Clear();
            _blueSkyAffectedNpcIds.Clear();
            _pendingAvatarEquips.Clear();
            _warnedKeys.Clear();
        }

        private static void CacheReflectionMetadata()
        {
            Type canvasType = typeof(ChemistryStationInterface);
            _setSelectedRecipeMethod = AccessTools.Method(canvasType, "SetSelectedRecipe", new[] { typeof(StationRecipeEntry) });
            if (_setSelectedRecipeMethod == null)
                LogWarn("ChemistryStationInterface.SetSelectedRecipe not found — late click binding will not work.");
        }

        private bool TryPatch(Type target, string methodName, Type[] paramTypes, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                var original = paramTypes != null
                    ? AccessTools.Method(target, methodName, paramTypes)
                    : AccessTools.Method(target, methodName);
                if (original == null)
                {
                    LogWarn($"{target.Name}.{methodName} not found — patch skipped.");
                    return false;
                }
                HarmonyInstance.Patch(original, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception ex)
            {
                LogWarn($"Failed to patch {target.Name}.{methodName}: {ex.Message}");
                return false;
            }
        }

        private void OnPreLoad()
        {
            CancelRoutines();
            if (_suppressInjectionThisLoad)
            {
                RemoveCustomItemsFromRuntimeRegistries();
                LogMsg("Uninstall mode active — skipping Blue Sky Mod pre-load injection for this save.");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                EnsureCoreDefinitionsCreated(true);
            }
            catch (Exception ex)
            {
                LogError($"Pre-load registration failed (will retry after load): {ex}");
            }
            sw.Stop();
            LogMsg($"Pre-load registration took {sw.ElapsedMilliseconds} ms (ready: {_definitionsReady}).");
        }

        private void OnLoadComplete()
        {
            CancelRoutines();
            if (_suppressInjectionThisLoad)
            {
                RemoveCustomItemsFromRuntimeRegistries();
                LogMsg($"Uninstall mode active — save loaded without Blue Sky Mod. Verify your save, exit the game, and remove {ModFileName}.");
                return;
            }
            _postLoadToken = MelonCoroutines.Start(PostLoadInjectionRoutine());
        }

        private IEnumerator PostLoadInjectionRoutine()
        {
            try
            {
                float elapsed = 0f;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (!CoreSystemsReady())
                {
                    elapsed += Time.unscaledDeltaTime;
                    if (elapsed > ReadyTimeoutSeconds)
                    {
                        LogWarn("Timed out waiting for ProductManager/Registry — Blue Sky injection skipped.");
                        yield break;
                    }
                    yield return null;
                }

                EnsureCoreDefinitionsCreated(false);
                TryDiscoverExistingBlueSky(NetworkSingleton<ProductManager>.Instance);

                elapsed = 0f;
                while (!IconManagerReady())
                {
                    elapsed += Time.unscaledDeltaTime;
                    if (elapsed > ReadyTimeoutSeconds)
                    {
                        LogWarn("Timed out waiting for ProductIconManager — icons skipped.");
                        break;
                    }
                    yield return null;
                }

                if (IconManagerReady())
                    InjectPackagedIcons(Singleton<ProductIconManager>.Instance);

                if (_labRecipe == null)
                    CreateAndRegisterRecipes();

                bool canvasReady = TryInjectSingletonStationCanvas();
                if (!canvasReady)
                    LogWarn("Chemistry Station canvas not ready after load — Blue Meth will be added when a station opens.");

                if (_shopToken == null && !TryInjectPristinePseudoIntoShops())
                    _shopToken = MelonCoroutines.Start(InjectShopListingsRoutine());

                sw.Stop();
                LogMsg($"Blue Sky v{ModVersion} ({BuildVariant}) Injection Complete ({sw.ElapsedMilliseconds} ms of post-load work, station canvas ready: {canvasReady}).");
            }
            finally
            {
                _postLoadToken = null;
            }
        }

        private static bool CoreSystemsReady()
        {
            try
            {
                var pm = NetworkSingleton<ProductManager>.Instance;
                return pm != null
                    && pm.DefaultMeth != null
                    && Singleton<Registry>.Instance != null
                    && Registry.GetItem(BlueSkyIds.VanillaPseudo) != null;
            }
            catch (Exception ex)
            {
                LogWarnOnce("core-ready", $"Core systems not ready yet ({ex.GetType().Name}: {ex.Message}); still waiting.");
                return false;
            }
        }

        private static bool IconManagerReady()
        {
            try { return Singleton<ProductIconManager>.Instance != null; }
            catch (Exception ex)
            {
                LogWarnOnce("icon-ready", $"ProductIconManager not ready yet ({ex.GetType().Name}: {ex.Message}); still waiting.");
                return false;
            }
        }

        private static T FindComponentInChildren<T>(Component root, bool includeInactive = true) where T : Component
        {
            if (root == null) return null;
            var comp = root.GetComponentInChildren(Il2CppType.Of<T>(), includeInactive);
            return comp != null ? comp.TryCast<T>() : null;
        }

        private static T FindComponentInParent<T>(Component root, bool includeInactive = true) where T : Component
        {
            if (root == null) return null;
            var comp = root.GetComponentInParent(Il2CppType.Of<T>(), includeInactive);
            return comp != null ? comp.TryCast<T>() : null;
        }

        private static T[] FindAllLoadedObjects<T>() where T : UnityEngine.Object
        {
            var raw = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
            if (raw == null || raw.Length == 0) return Array.Empty<T>();
            var list = new System.Collections.Generic.List<T>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (TryCast(raw[i], out T cast))
                    list.Add(cast);
            }
            return list.ToArray();
        }

        private static bool TryCast<T>(object obj, out T result) where T : Il2CppObjectBase
        {
            if (obj is T direct) { result = direct; return true; }
            if (obj is Il2CppObjectBase il2cppObj)
            {
                result = il2cppObj.TryCast<T>();
                return result != null;
            }
            result = null;
            return false;
        }
    }

    // In-game developer console command registered automatically via S1API.
    public sealed class BlueSkyUninstallCommand : BaseConsoleCommand
    {
        public override string CommandWord => "bluesky_uninstall";

        public override string CommandDescription => "Saves, backs up, and scrubs all Blue Sky Mod data from the current save, then returns to the menu for safe mod removal.";

        public override string ExampleUsage => "bluesky_uninstall";

        public override void ExecuteCommand(System.Collections.Generic.List<string> args)
        {
            Main.RequestActiveSaveUninstall();
        }
    }
}

