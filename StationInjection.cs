// ============================================================================
// StationInjection.cs — Chemistry Station UI, Recipe Replication & Lab Oven Visuals
// ============================================================================
// Handles:
//   - Injecting the 'Blue Meth' recipe into ChemistryStationInterface (UI + gamepad/keyboard UIPanel)
//   - Multiplayer RPC safety nets (ChemistryStation, LabOven, ConfigurationReplicator) so late-joining
//     clients resolve custom recipes/items before replicated cook operations execute
//   - Blue Sky crystal shard & particle recoloring during Lab Oven shatter
// ============================================================================

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.UI.Stations;
using Il2CppSystem.Collections.Generic;

namespace BlueSkyMod
{
    public partial class Main
    {
        private static readonly System.Collections.Generic.Dictionary<int, ParticleSystem.MinMaxGradient> _ovenDefaultShatterColors =
            new System.Collections.Generic.Dictionary<int, ParticleSystem.MinMaxGradient>();

        private void PatchChemistryStationInterface()
        {
            int hooks = 0;
            if (TryPatch(typeof(ChemistryStationInterface), "Awake", Type.EmptyTypes, new HarmonyMethod(typeof(Main), nameof(OnStationAwakePrefix)), null))
                hooks++;
            if (TryPatch(typeof(ChemistryStationInterface), "Open", null, new HarmonyMethod(typeof(Main), nameof(OnStationOpen)), null))
                hooks++;
            LogMsg($"Applied {hooks} ChemistryStationInterface hooks.");
        }

        // Hooks FishNet RpcLogic methods (with direct fallback) so custom items/recipes are guaranteed
        // registered before replicated station operations run on multiplayer clients.
        private void PatchCookOperationSafetyNet()
        {
            int chemPatched   = PatchRpcLogicMethods(typeof(ChemistryStation), "RpcLogic___SetCookOperation", "SetCookOperation", new HarmonyMethod(typeof(Main), nameof(CookOperationPrefix)));
            int ovenPatched   = PatchRpcLogicMethods(typeof(LabOven), "RpcLogic___SetCookOperation", "SetCookOperation", new HarmonyMethod(typeof(Main), nameof(OvenCookOperationPrefix)));
            int recipePatched = PatchRpcLogicMethods(typeof(ConfigurationReplicator), "RpcLogic___ReceiveRecipeField", "ReceiveRecipeField", new HarmonyMethod(typeof(Main), nameof(ReceiveRecipeFieldPrefix)));

            if (chemPatched == 0)
                LogWarn("ChemistryStation.SetCookOperation not found — cook-operation safety net not applied.");
            else
                LogMsg($"Applied {chemPatched} ChemistryStation cook-operation safety-net hook(s).");

            if (ovenPatched == 0)
                LogWarn("LabOven.SetCookOperation not found — lab oven cook-operation safety net not applied.");
            else
                LogMsg($"Applied {ovenPatched} LabOven cook-operation safety-net hook(s).");

            if (recipePatched == 0)
                LogWarn("ConfigurationReplicator.ReceiveRecipeField not found — recipe config replication safety net not applied.");
            else
                LogMsg($"Applied {recipePatched} ConfigurationReplicator recipe safety-net hook(s).");
        }

        private int PatchRpcLogicMethods(Type targetType, string rpcLogicPrefix, string fallbackName, HarmonyMethod prefix)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var methods = targetType.GetMethods(flags);
            var rpcLogic = new System.Collections.Generic.List<MethodInfo>();
            var fallback = new System.Collections.Generic.List<MethodInfo>();

            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name.StartsWith(rpcLogicPrefix, StringComparison.Ordinal))
                    rpcLogic.Add(m);
                else if (m.Name == fallbackName)
                    fallback.Add(m);
            }

            var targets = rpcLogic.Count > 0 ? rpcLogic : fallback;
            int patched = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                try
                {
                    HarmonyInstance.Patch(targets[i], prefix: prefix);
                    patched++;
                }
                catch (Exception ex)
                {
                    LogWarn($"Failed to patch {targetType.Name}.{targets[i].Name}: {ex.Message}");
                }
            }
            return patched;
        }

        private static void OnStationAwakePrefix(ChemistryStationInterface __instance)
        {
            if (_suppressInjectionThisLoad || __instance == null) return;
            try
            {
                if (_labRecipe == null && CoreSystemsReady())
                {
                    EnsureCoreDefinitionsCreated(false);
                    if (_labRecipe == null)
                        CreateAndRegisterRecipes();
                }

                var recipes = __instance.Recipes;
                if (_labRecipe != null && recipes != null && !ContainsRecipe(recipes, _labRecipe.name))
                    recipes.Add(_labRecipe);
            }
            catch (Exception ex)
            {
                LogWarn($"Station awake recipe inject failed: {ex.Message}");
            }
        }

        private static void OnStationOpen(ChemistryStationInterface __instance)
        {
            if (_suppressInjectionThisLoad || __instance == null || _labRecipe == null) return;
            try
            {
                InjectRecipeIntoCanvas(__instance);
            }
            catch (Exception ex)
            {
                LogWarn($"Station open inject failed: {ex.Message}");
            }
        }

        private static bool TryInjectSingletonStationCanvas()
        {
            if (_labRecipe == null) return false;
            try
            {
                var singletonCanvas = Singleton<ChemistryStationInterface>.Instance;
                if (singletonCanvas != null && singletonCanvas.gameObject.scene.IsValid())
                {
                    InjectRecipeIntoCanvas(singletonCanvas);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogWarnOnce("canvas-singleton", $"ChemistryStationInterface singleton lookup failed: {ex.Message}");
            }
            return false;
        }

        private static void EnsureLabRecipeResolvable()
        {
            if (_labRecipe == null)
            {
                if (!CoreSystemsReady()) return;
                EnsureCoreDefinitionsCreated(false);
                if (_labRecipe == null)
                    CreateAndRegisterRecipes();
                if (_labRecipe == null) return;
            }

            var canvas = Singleton<ChemistryStationInterface>.Instance;
            if (canvas == null) return;
            var recipes = canvas.Recipes;
            if (recipes != null && !ContainsRecipe(recipes, _labRecipe.name))
                InjectRecipeIntoCanvas(canvas);
        }

        private static void CookOperationPrefix()
        {
            if (_suppressInjectionThisLoad) return;
            try { EnsureLabRecipeResolvable(); }
            catch (Exception ex) { LogWarnOnce("cookop-prefix", $"Cook-operation safety net failed: {ex.Message}"); }
        }

        private static void ReceiveRecipeFieldPrefix()
        {
            if (_suppressInjectionThisLoad) return;
            try { EnsureLabRecipeResolvable(); }
            catch (Exception ex) { LogWarnOnce("recipe-repl-prefix", $"Recipe config replication safety net failed: {ex.Message}"); }
        }

        private static void OvenCookOperationPrefix()
        {
            if (_suppressInjectionThisLoad || _definitionsReady) return;
            try
            {
                if (CoreSystemsReady())
                    EnsureCoreDefinitionsCreated(false);
            }
            catch (Exception ex)
            {
                LogWarnOnce("oven-cookop-prefix", $"Lab oven cook-operation safety net failed: {ex.Message}");
            }
        }

        public static class StationRecipeField_Load_Patch
        {
            public static void Prefix(StationRecipeField __instance, StationRecipeFieldData __0)
            {
                if (_suppressInjectionThisLoad || __instance == null || __0 == null) return;
                try
                {
                    string recipeId = __0.RecipeID;
                    if (!string.Equals(recipeId, BlueSkyIds.LabRecipeId, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(recipeId, BlueSkyIds.LabRecipe, StringComparison.OrdinalIgnoreCase))
                        return;

                    EnsureLabRecipeResolvable();
                    if (_labRecipe != null && __instance.Options != null && !ContainsRecipe(__instance.Options, _labRecipe.name))
                        __instance.Options.Add(_labRecipe);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("recipe-field-load", $"Resolving Blue Meth station config recipe failed: {ex.Message}");
                }
            }
        }

        private static void InjectRecipeIntoCanvas(ChemistryStationInterface canvas)
        {
            var recipes = canvas?.Recipes;
            if (recipes != null && !ContainsRecipe(recipes, _labRecipe.name))
                recipes.Add(_labRecipe);

            var entries = canvas?.recipeEntries;
            if (entries != null)
            {
                var existingEntry = FindEntryByRecipeName(entries, _labRecipe.name);
                if (existingEntry != null)
                {
                    RegisterRecipeEntrySelectable(canvas, entries, existingEntry);
                    return;
                }
            }
            else if (canvas.RecipeContainer != null)
            {
                var childEntries = canvas.RecipeContainer.GetComponentsInChildren<StationRecipeEntry>(true);
                if (childEntries != null)
                {
                    for (int i = 0; i < childEntries.Length; i++)
                    {
                        var e = childEntries[i];
                        if (e?.Recipe != null && e.Recipe.name == _labRecipe.name)
                        {
                            RegisterRecipeEntrySelectable(canvas, null, e);
                            return;
                        }
                    }
                }
            }

            var prefab    = canvas.RecipeEntryPrefab;
            var container = canvas.RecipeContainer;
            if (prefab == null || container == null) return;

            var newEntry = UnityEngine.Object.Instantiate(prefab, container);
            if (newEntry == null) return;

            newEntry.AssignRecipe(_labRecipe);
            entries?.Add(newEntry);
            RegisterRecipeEntrySelectable(canvas, entries, newEntry);

            var button = newEntry.Button ?? newEntry.GetComponentInChildren<Button>(true);
            if (button != null && _setSelectedRecipeMethod != null)
            {
                var capturedCanvas = canvas;
                var capturedEntry  = newEntry;
                button.onClick.AddListener(
                    (UnityAction)(() =>
                    {
                        try
                        {
                            if (capturedEntry != null && capturedEntry.IsValid)
                                _setSelectedRecipeMethod.Invoke(capturedCanvas, new object[] { capturedEntry });
                        }
                        catch (Exception ex)
                        {
                            LogWarnOnce("recipe-click", $"Blue Meth click failed: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }));
            }

            LogMsg("Injected 'Blue Meth' recipe entry into Chemistry Station UI.");
        }

        private static void RegisterRecipeEntrySelectable(ChemistryStationInterface canvas, List<StationRecipeEntry> entries, StationRecipeEntry entry)
        {
            if (entry == null) return;
            try
            {
                var selectable = FindComponentInChildren<UISelectable>(entry, true);
                if (selectable == null || selectable.ParentPanel != null) return;

                UIPanel panel = null;
                if (entries != null)
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var sibling = entries[i];
                        if (sibling == null || sibling == entry) continue;
                        var siblingSel = FindComponentInChildren<UISelectable>(sibling, true);
                        if (siblingSel != null && siblingSel.ParentPanel != null)
                        {
                            panel = siblingSel.ParentPanel;
                            break;
                        }
                    }
                }

                if (panel == null && canvas?.RecipeContainer != null)
                    panel = FindComponentInParent<UIPanel>(canvas.RecipeContainer, true);

                if (panel != null)
                    panel.AddSelectable(selectable);
            }
            catch (Exception ex)
            {
                LogWarnOnce("recipe-panel", $"Registering Blue Meth recipe UI panel failed: {ex.Message}");
            }
        }

        private static bool ContainsRecipe(List<StationRecipe> list, string recipeName)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].name == recipeName) return true;
            return false;
        }

        private static StationRecipeEntry FindEntryByRecipeName(List<StationRecipeEntry> entries, string recipeName)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e?.Recipe != null && e.Recipe.name == recipeName) return e;
            }
            return null;
        }

        private static bool IsBlueSkyOvenOperation(LabOven oven)
        {
            var op = oven?.CurrentOperation;
            if (op == null) return false;
            return op.ProductID == BlueSkyIds.BlueSky || op.IngredientID == BlueSkyIds.LiquidBlue;
        }

        // Lab Oven shatter particles & crystal shard material overrides for Blue Sky cooks.
        public static class LabOven_Shatter_Patch
        {
            public static void Prefix(LabOven __instance, out bool __state)
            {
                __state = false;
                if (_suppressInjectionThisLoad) return;
                try
                {
                    if (__instance == null) return;
                    bool isBlueSky = IsBlueSkyOvenOperation(__instance);
                    __state = isBlueSky;

                    var ps = __instance.ShatterParticles;
                    if (ps != null)
                    {
                        var main = ps.main;
                        int id = __instance.GetInstanceID();
                        if (isBlueSky)
                        {
                            if (!_ovenDefaultShatterColors.ContainsKey(id))
                                _ovenDefaultShatterColors[id] = main.startColor;
                            main.startColor = new ParticleSystem.MinMaxGradient((Color)CyanCrystalColor);
                        }
                        else if (_ovenDefaultShatterColors.TryGetValue(id, out ParticleSystem.MinMaxGradient origGradient))
                        {
                            main.startColor = origGradient;
                            _ovenDefaultShatterColors.Remove(id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("oven-shatter-prefix", $"Lab oven shatter prefix failed: {ex.Message}");
                }
            }

            public static void Postfix(LabOven __instance, bool __state)
            {
                if (!__state || __instance == null) return;
                try
                {
                    var shards = __instance.shards;
                    if (shards == null) return;

                    for (int i = 0; i < shards.Count; i++)
                        ApplyToAllRenderers(shards[i], ApplyBlueSkyMaterialAction, false);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("oven-shatter-postfix", $"Lab oven shatter visual patch failed: {ex.Message}");
                }
            }
        }

        public static class LabOven_Destroy_Patch
        {
            public static void Postfix(LabOven __instance)
            {
                if (__instance != null)
                    _ovenDefaultShatterColors.Remove(__instance.GetInstanceID());
            }
        }
    }
}

