// ============================================================================
// ItemDefinitions.cs — Custom Items, Pricing, '99.1% Pure' Effect, Recipes & Shops
// ============================================================================
// Primary customization file for gameplay balance:
//   - Pricing, addictiveness & NPC speed constants (top of class)
//   - Pure Ephedrine item stats, stack size, quality & unlock rank (`CreatePristinePseudo`)
//   - Liquid Meth (Blue) & Lab Oven yield (`CreateLiquidBlue`)
//   - Blue Sky Meth base product (`CreateBlueSky`)
//   - '99.1% Pure' custom effect metadata (`GetOrCreateBlueSkyEffect`)
//   - Player & NPC visual/movement/post-processing effects (`ApplyBlueSkyToNativePlayer/Npc`)
//   - Chemistry Station recipe ingredients & cook time (`CreateAndRegisterRecipes`)
//   - Supplier / Shop listing injection (`TryInjectPristinePseudoIntoShops`)
// ============================================================================

using System;
using System.Collections;
using UnityEngine;

using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Effects;
using Il2CppScheduleOne.Effects.MixMaps;
using Il2CppScheduleOne.FX;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Levelling;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Tools;
using Il2CppScheduleOne.UI.Shop;
using Il2CppScheduleOne.UI.Stations;
using Il2CppScheduleOne.VoiceOver;
using Il2CppSystem.Collections.Generic;
using S1Avatar = Il2CppScheduleOne.AvatarFramework.Avatar;
using S1Npc = Il2CppScheduleOne.NPCs.NPC;

namespace BlueSkyMod
{
    public partial class Main
    {
        // Gameplay balance constants for Blue Sky Meth & derived mixes.
        private const float BlueSkyBasePrice          = 450f;
        private const float BlueSkyMixBasePrice       = 340f;
        private const float BlueSkyBaseAddictiveness  = 1.0f;
        private const float BlueSkyNpcSpeedMultiplier = 1.85f;
        private const float BlueSkyNpcMaxSpeedMult    = 2.5f;

        // Tracks NPCs currently under Blue Sky effects so sequential consumption cleans up cleanly.
        private static readonly System.Collections.Generic.HashSet<int> _blueSkyAffectedNpcIds =
            new System.Collections.Generic.HashSet<int>();

        private static int _effectRegisteredPropUtilId;

        public static class Registry_Awake_Patch
        {
            public static void Postfix()
            {
                if (_suppressInjectionThisLoad || _definitionsReady) return;
                try
                {
                    if (!CoreSystemsReady()) return;
                    EnsureCoreDefinitionsCreated(false);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("registry-awake-init", $"Early Registry definition setup failed: {ex.Message}");
                }
            }
        }

        public static class ProductManager_Awake_Patch
        {
            public static void Postfix(ProductManager __instance)
            {
                if (_suppressInjectionThisLoad || _definitionsReady || __instance == null || __instance.DefaultMeth == null) return;
                try
                {
                    if (Singleton<Registry>.Instance == null || Registry.GetItem(BlueSkyIds.VanillaPseudo) == null) return;
                    EnsureCoreDefinitionsCreated(false);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("pm-awake-init", $"Early ProductManager definition setup failed: {ex.Message}");
                }
            }
        }

        public static class ProductManager_OnStartClient_Patch
        {
            public static void Prefix(ProductManager __instance)
            {
                if (_suppressInjectionThisLoad || _definitionsReady || __instance == null || __instance.DefaultMeth == null) return;
                try
                {
                    if (Singleton<Registry>.Instance == null || Registry.GetItem(BlueSkyIds.VanillaPseudo) == null) return;
                    EnsureCoreDefinitionsCreated(false);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("pm-startclient-init", $"Client ProductManager definition setup failed: {ex.Message}");
                }
            }
        }

        // Vanilla CheckDiscovery only unlocks hardcoded "meth"/"cocaine"/"shroom" and gates "meth" on
        // MethDiscovered (any Methamphetamine in DiscoveredProducts). Discover Blue Sky explicitly when
        // produced/held without unlocking vanilla meth, and keep vanilla meth discoverable afterwards.
        public static class ProductManager_CheckDiscovery_Patch
        {
            public static bool Prefix(ItemInstance __0)
            {
                if (_suppressInjectionThisLoad || __0 == null) return true;
                try
                {
                    if (!NetworkSingleton<ProductManager>.InstanceExists) return true;
                    var pm = NetworkSingleton<ProductManager>.Instance;
                    if (pm == null) return true;

                    string itemId = GetItemInstanceId(__0);
                    if (string.IsNullOrEmpty(itemId)) return true;

                    if (string.Equals(itemId, BlueSkyIds.BlueSky, StringComparison.OrdinalIgnoreCase))
                    {
                        if (BlueSky == null)
                            EnsureCoreDefinitionsCreated(false);
                        if (BlueSky != null && (ProductManager.DiscoveredProducts == null || !ProductManager.DiscoveredProducts.Contains(BlueSky)))
                        {
                            AddToProductLists(pm, BlueSky);
                            DiscoverBlueSky(pm);
                        }
                        return false;
                    }

                    if (pm.DefaultMeth != null &&
                        string.Equals(itemId, pm.DefaultMeth.ID, StringComparison.OrdinalIgnoreCase) &&
                        ProductManager.DiscoveredProducts != null &&
                        !ProductManager.DiscoveredProducts.Contains(pm.DefaultMeth))
                    {
                        pm.SetMethDiscovered();
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("check-discovery", $"Blue Sky product discovery hook failed: {ex.Message}");
                }
                return true;
            }
        }

        public static class PropertyUtility_Awake_Patch
        {
            public static void Postfix(PropertyUtility __instance)
            {
                if (_suppressInjectionThisLoad || __instance == null) return;
                EnsureBlueSkyEffectInPropertyUtility(GetOrCreateBlueSkyEffect(), __instance);
            }
        }

        public static class PropertyUtility_GetProperties_Patch
        {
            public static void Prefix(PropertyUtility __instance, List<string> __0)
            {
                if (_suppressInjectionThisLoad || __instance == null || __0 == null) return;
                try
                {
                    for (int i = 0; i < __0.Count; i++)
                    {
                        if (string.Equals(__0[i], BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase))
                        {
                            EnsureBlueSkyEffectInPropertyUtility(GetOrCreateBlueSkyEffect(), __instance);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("prop-util-get", $"Ensuring '99.1% Pure' in PropertyUtility failed: {ex.Message}");
                }
            }
        }

        // Ensures custom mixes created from Blue Sky inherit BlueSkyMixBasePrice & addictiveness
        // before Schedule I's ProductDefinition.Initialize computes MarketValue.
        public static class ProductDefinition_Initialize_Patch
        {
            public static void Prefix(ProductDefinition __instance, List<Effect> __0)
            {
                if (_suppressInjectionThisLoad || __instance == null || __0 == null) return;
                if (__instance.ID == BlueSkyIds.BlueSky || !TryCast(__instance, out MethDefinition _)) return;

                try
                {
                    for (int i = 0; i < __0.Count; i++)
                    {
                        var e = __0[i];
                        if (e != null && string.Equals(e.ID, BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase))
                        {
                            __instance.BasePrice         = BlueSkyMixBasePrice;
                            __instance.BaseAddictiveness = BlueSky != null ? BlueSky.BaseAddictiveness : BlueSkyBaseAddictiveness;
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("mix-base-price", $"Applying Blue Sky base value to mix failed: {ex.Message}");
                }
            }
        }

        public static class MethDefinition_ApplyAppearanceSettings_Patch
        {
            public static void Postfix(MethDefinition __instance)
            {
                if (_suppressInjectionThisLoad || __instance == null || _pendingAvatarEquips.Count == 0) return;
                try
                {
                    if (!string.IsNullOrEmpty(__instance.ID) && __instance.CrystalMaterial != null)
                        FlushPendingAvatarEquips(__instance);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("pending-mix-visual", $"Re-applying replicated Blue Sky mix visuals failed: {ex.Message}");
                }
            }
        }

        private static void EnsureCoreDefinitionsCreated(bool logMissingIcons = true)
        {
            var pm = NetworkSingleton<ProductManager>.Instance;
            if (pm == null || pm.DefaultMeth == null || Singleton<Registry>.Instance == null) return;

            if (BlueSky == null)
                CreateBlueSky(pm);
            else
            {
                EnsureBlueSkyEffectAttached(pm);
                RegisterItemInRegistry(BlueSky);
                AddToProductLists(pm, BlueSky);
            }

            if (PristinePseudo == null) CreatePristinePseudo();
            else RegisterItemInRegistry(PristinePseudo);

            if (LiquidBlue == null)
            {
                if (BlueSky != null)
                    CreateLiquidBlue();
            }
            else
            {
                if (BlueSky != null && _customLiquidPrefab != null)
                {
                    var cookable = _customLiquidPrefab.GetComponentInChildren<CookableModule>(true);
                    if (cookable != null && cookable.Product == null)
                        cookable.Product = BlueSky;
                }
                RegisterItemInRegistry(LiquidBlue);
            }

            RefreshCustomIcons(pm);
            _definitionsReady = BlueSky != null && PristinePseudo != null && LiquidBlue != null;

            if (BlueSky != null && _pendingAvatarEquips.Count > 0)
                FlushPendingAvatarEquips(BlueSky);

            if (_definitionsReady)
            {
                try
                {
                    CreateAndRegisterRecipes();
                    TryInjectSingletonStationCanvas();
                }
                catch (Exception ex)
                {
                    LogWarn($"Early recipe registration failed (will retry after load): {ex.Message}");
                }
            }

            if (IconManagerReady())
                InjectPackagedIcons(Singleton<ProductIconManager>.Instance);
            else if (logMissingIcons)
                LogWarnOnce("icons-early", "ProductIconManager not available at pre-load; packaged icons will be injected after load (slots may show white until moved).");
        }

        private static Transform GetPrefabHolder()
        {
            if (_prefabHolder == null)
            {
                _prefabHolder = new GameObject("BlueSkyMod_PrefabHolder");
                _prefabHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_prefabHolder);
            }
            return _prefabHolder.transform;
        }

        // Pure Ephedrine precursor definition (name, price, stack limit, quality, unlock rank).
        // NOTE: If you change IDs, update `BlueSkyIds` in BlueSkySaveCleaner.cs so uninstall stays in sync.
        private static void CreatePristinePseudo()
        {
            var rawPseudo = Registry.GetItem(BlueSkyIds.VanillaPseudo);
            if (rawPseudo == null) return;

            if (!TryCast(rawPseudo, out QualityItemDefinition basePseudo))
            {
                LogError("Failed to cast 'pseudo' to QualityItemDefinition.");
                return;
            }

            PristinePseudo = UnityEngine.Object.Instantiate<QualityItemDefinition>(basePseudo);
            UnityEngine.Object.DontDestroyOnLoad(PristinePseudo);
            PristinePseudo.name              = "Pure Ephedrine";
            PristinePseudo.Name              = "Pure Ephedrine";
            PristinePseudo.ID                = BlueSkyIds.PristinePseudo;
            PristinePseudo.Description       = "Heavenly quality precursor. Yields Blue Sky Meth.";
            PristinePseudo.BasePurchasePrice = 150f;
            PristinePseudo.StackLimit        = 50;
            PristinePseudo.DefaultQuality    = EQuality.Heavenly;
            PristinePseudo.Icon              = TintIcon(basePseudo.Icon, PureEphedrinePillColor, true);
            PristinePseudo.AvailableInDemo   = true;
            // Standard build inherits vanilla Pseudo's unlock rank; InstantUnlock clears rank gating.
#if INSTANT_UNLOCK
            PristinePseudo.RequiresLevelToPurchase = false;
            PristinePseudo.RequiredRank            = default;
#endif

            if (basePseudo.StationItem != null)
            {
                _customPseudoStationPrefab = UnityEngine.Object.Instantiate(basePseudo.StationItem.gameObject, GetPrefabHolder());
                _customPseudoStationPrefab.name = "StationItem_PureEphedrine";
                _customPseudoStationPrefab.SetActive(true);
                ApplyToAllRenderers(_customPseudoStationPrefab, ReplacePillMaterialsAction);

                var particles = _customPseudoStationPrefab.GetComponentsInChildren<ParticleSystem>(true);
                if (particles != null)
                {
                    for (int i = 0; i < particles.Length; i++)
                    {
                        if (particles[i] == null) continue;
                        var main = particles[i].main;
                        main.startColor = new ParticleSystem.MinMaxGradient((Color)PureEphedrinePillColor);
                    }
                }

                PristinePseudo.StationItem = _customPseudoStationPrefab.GetComponent<StationItem>();
            }

            RegisterItemInRegistry(PristinePseudo);
#if INSTANT_UNLOCK
            LogMsg("Registered Pure Ephedrine (Heavenly, Instant Unlock).");
#else
            LogMsg("Registered Pure Ephedrine (Heavenly).");
#endif
        }

        // Liquid Meth (Blue) intermediate item & Lab Oven cook yield (`cookable.ProductQuantity`).
        private static void CreateLiquidBlue()
        {
            var originalDef = Registry.GetItem(BlueSkyIds.VanillaLiquidMeth);
            if (originalDef == null) return;

            if (TryCast(originalDef, out LiquidMethDefinition origLiquid))
            {
                var clonedLiquid = UnityEngine.Object.Instantiate<LiquidMethDefinition>(origLiquid);
                clonedLiquid.StaticLiquidColor   = BlueLiquidColor;
                clonedLiquid.LiquidVolumeColor   = BlueLiquidColor;
                clonedLiquid.PourParticlesColor  = BlueLiquidColor;
                clonedLiquid.CookableLiquidColor = BlueLiquidColor;
                clonedLiquid.CookableSolidColor  = CyanCrystalColor;
                LiquidBlue = clonedLiquid;
            }
            else
            {
                LiquidBlue = UnityEngine.Object.Instantiate<ItemDefinition>(originalDef);
            }

            UnityEngine.Object.DontDestroyOnLoad(LiquidBlue);
            LiquidBlue.name            = "Liquid Meth (Blue)";
            LiquidBlue.Name            = "Liquid Meth (Blue)";
            LiquidBlue.ID              = BlueSkyIds.LiquidBlue;
            LiquidBlue.AvailableInDemo = true;

            ItemDefinition babyBlue = Registry.GetItem(BlueSkyIds.VanillaBabyBlue);
            LiquidBlue.Icon = (babyBlue != null && babyBlue.Icon != null)
                ? babyBlue.Icon
                : TintIcon(originalDef.Icon, BlueLiquidColor);

            if (TryCast(originalDef, out StorableItemDefinition origStorable) &&
                TryCast(LiquidBlue,  out StorableItemDefinition blueStorable)  &&
                origStorable.StationItem != null)
            {
                _customLiquidPrefab = UnityEngine.Object.Instantiate(origStorable.StationItem.gameObject, GetPrefabHolder());
                _customLiquidPrefab.name = "StationItem_LiquidBlue";
                _customLiquidPrefab.SetActive(true);

                var cookable = _customLiquidPrefab.GetComponentInChildren<CookableModule>(true);
                if (cookable != null)
                {
                    cookable.Product         = BlueSky;
                    cookable.ProductQuantity = 10;
                    cookable.LiquidColor     = BlueLiquidColor;
                    cookable.SolidColor      = CyanCrystalColor;
                }

                var pourable = _customLiquidPrefab.GetComponentInChildren<PourableModule>(true);
                if (pourable != null)
                {
                    pourable.LiquidColor        = BlueLiquidColor;
                    pourable.PourParticlesColor = BlueLiquidColor;
                }

                blueStorable.StationItem = _customLiquidPrefab.GetComponent<StationItem>();
            }

            RegisterItemInRegistry(LiquidBlue);
            LogMsg("Registered Liquid Meth (Blue).");
        }

        // Blue Sky Meth final product definition (display name, crystal colors, material glow).
        private static void CreateBlueSky(ProductManager pm)
        {
            BlueSky = UnityEngine.Object.Instantiate<MethDefinition>(pm.DefaultMeth);
            UnityEngine.Object.DontDestroyOnLoad(BlueSky);
            BlueSky.name              = "Blue Sky";
            BlueSky.Name              = "Blue Sky";
            BlueSky.ID                = BlueSkyIds.BlueSky;
            BlueSky.BasePrice         = BlueSkyBasePrice;
            BlueSky.MarketValue       = BlueSkyBasePrice;
            BlueSky.BaseAddictiveness = BlueSkyBaseAddictiveness;
            BlueSky.Icon              = TintIcon(pm.DefaultMeth.Icon, CyanCrystalColor);
            BlueSky.AvailableInDemo   = true;

            var newSettings = new MethAppearanceSettings(CyanCrystalColor, BlueSkySecondaryColor);
            BlueSky.TintColor = newSettings.MainColor;
            try
            {
                BlueSky.AppearanceSettings = newSettings;
            }
            catch (Exception ex)
            {
                LogWarnOnce("appearance-settings", $"Setting Blue Sky appearance failed: {ex.Message}");
            }

            if (BlueSky.CrystalMaterial != null)
            {
                Color emission = Color.Lerp(Color.black, newSettings.MainColor, 0.08f);
                BlueSky.CrystalMaterial = CreateTintedMaterial(BlueSky.CrystalMaterial, "BlueSky_Mat", newSettings.MainColor, emission);
            }

            BlueSky.Properties = new List<Effect>();
            EnsureBlueSkyEffectAttached(pm);

            RegisterItemInRegistry(BlueSky);
            AddToProductLists(pm, BlueSky);

            LogMsg("Registered Blue Sky Meth.");
        }

        // Custom '99.1% Pure' drug effect metadata (name, description, tier, pricing bonus).
        // Uses `Refreshing` as a no-op vanilla Effect base class so custom runtime behavior lives in our patches.
        private static Effect GetOrCreateBlueSkyEffect()
        {
            if (_blueSkyEffect != null) return _blueSkyEffect;

            try
            {
                var effect = ScriptableObject.CreateInstance<Refreshing>();
                UnityEngine.Object.DontDestroyOnLoad(effect);
                effect.name                         = BlueSkyIds.BlueSkyEffect;
                effect.ID                           = BlueSkyIds.BlueSkyEffect;
                effect.Name                         = "99.1% Pure";
                effect.Description                  = "Walter's signature 99.1% chemically pure formula. Triggers an intense euphoric rush, hyper-focus, blazing speed, and glowing blue eyes.";
                effect.Tier                         = 5;
                effect.Addictiveness                = 1.0f;
                effect.ProductColor                 = CyanCrystalColor;
                effect.LabelColor                   = CyanCrystalColor;
                effect.ValueChange                  = 0;
                effect.ValueMultiplier              = 1.0f;
                effect.AddBaseValueMultiple         = 0.35f;
                effect.MixDirection                 = Vector2.zero;
                effect.MixMagnitude                 = 0f;
                effect.ImplementedPriorMixingRework = true;

                _blueSkyEffect = effect;
                LogMsg("Registered custom effect '99.1% Pure' (bluesky_purity).");
            }
            catch (Exception ex)
            {
                LogWarnOnce("bluesky-effect-create", $"Creating custom effect '99.1% Pure' failed: {ex.Message}");
            }

            return _blueSkyEffect;
        }

        private static void EnsureBlueSkyEffectInPropertyUtility(Effect effect, PropertyUtility pu = null)
        {
            if (effect == null) return;
            try
            {
                if (pu == null) pu = Singleton<PropertyUtility>.Instance;
                if (pu == null) return;

                int puId = pu.GetInstanceID();
                var dict = pu.PropertiesDict;
                if (puId == _effectRegisteredPropUtilId && dict != null && dict.ContainsKey(effect.ID))
                    return;

                if (dict != null && !dict.ContainsKey(effect.ID))
                    dict[effect.ID] = effect;

                if (pu.AllProperties != null)
                {
                    bool found = false;
                    for (int i = 0; i < pu.AllProperties.Count; i++)
                    {
                        var e = pu.AllProperties[i];
                        if (e != null && string.Equals(e.ID, effect.ID, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        pu.AllProperties.Add(effect);
                }

                _effectRegisteredPropUtilId = puId;
            }
            catch { }
        }

        private static void EnsureBlueSkyEffectInMethMixMap(Effect effect, ProductManager pm = null)
        {
            if (effect == null) return;
            try
            {
                if (pm == null) pm = NetworkSingleton<ProductManager>.Instance;
                var map = pm?.GetMixerMap(EDrugType.Methamphetamine);
                var effects = map?.Effects;
                if (effects == null) return;

                for (int i = 0; i < effects.Count; i++)
                {
                    var entry = effects[i];
                    if (entry?.Property != null &&
                        string.Equals(entry.Property.ID, effect.ID, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                effects.Add(new MixerMapEffect
                {
                    Property = effect,
                    Position = new Vector2(999f, 999f),
                    Radius   = 0.001f
                });
            }
            catch (Exception ex)
            {
                LogWarnOnce("bluesky-mixmap", $"Registering '99.1% Pure' in MethMixMap failed: {ex.Message}");
            }
        }

        private static void EnsureBlueSkyEffectAttached(ProductManager pm = null)
        {
            if (BlueSky == null) return;
            var effect = GetOrCreateBlueSkyEffect();
            if (effect == null) return;

            EnsureBlueSkyEffectInPropertyUtility(effect);
            EnsureBlueSkyEffectInMethMixMap(effect, pm);

            var props = BlueSky.Properties;
            if (props == null)
            {
                props = new List<Effect>();
                BlueSky.Properties = props;
            }

            for (int i = props.Count - 1; i >= 0; i--)
            {
                var existing = props[i];
                if (existing == null) continue;
                if (existing == effect) return;
                if (string.Equals(existing.ID, BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase))
                    props.RemoveAt(i);
            }
            props.Add(effect);
        }

        private static void RemoveCustomItemsFromRuntimeRegistries()
        {
            try
            {
                var registry = Singleton<Registry>.Instance;
                if (registry != null)
                {
                    if (BlueSky != null && Registry.GetItem(BlueSky.ID) != null)
                        registry.RemoveFromRegistry(BlueSky);
                    if (PristinePseudo != null && Registry.GetItem(PristinePseudo.ID) != null)
                        registry.RemoveFromRegistry(PristinePseudo);
                    if (LiquidBlue != null && Registry.GetItem(LiquidBlue.ID) != null)
                        registry.RemoveFromRegistry(LiquidBlue);
                }
            }
            catch { }

            try
            {
                if (BlueSky != null)
                {
                    ProductManager.DiscoveredProducts?.Remove(BlueSky);
                    ProductManager.ListedProducts?.Remove(BlueSky);
                    ProductManager.FavouritedProducts?.Remove(BlueSky);
                }

                var pm = NetworkSingleton<ProductManager>.Instance;
                if (pm != null)
                {
                    if (BlueSky != null)
                    {
                        pm.AllProducts?.Remove(BlueSky);
                        pm.ProductNames?.Remove(BlueSky.Name);
                        var prices = pm.ProductPrices;
                        if (prices != null && prices.ContainsKey(BlueSky))
                            prices.Remove(BlueSky);
                    }

                    var mixEffects = pm.GetMixerMap(EDrugType.Methamphetamine)?.Effects;
                    if (mixEffects != null)
                    {
                        for (int i = mixEffects.Count - 1; i >= 0; i--)
                        {
                            var entry = mixEffects[i];
                            if (entry?.Property != null &&
                                string.Equals(entry.Property.ID, BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase))
                                mixEffects.RemoveAt(i);
                        }
                    }
                }
            }
            catch { }

            try
            {
                var pu = Singleton<PropertyUtility>.Instance;
                if (pu == null) return;

                if (pu.AllProperties != null)
                {
                    for (int i = pu.AllProperties.Count - 1; i >= 0; i--)
                    {
                        var e = pu.AllProperties[i];
                        if (e != null && string.Equals(e.ID, BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase))
                            pu.AllProperties.RemoveAt(i);
                    }
                }

                var dict = pu.PropertiesDict;
                if (dict != null && dict.ContainsKey(BlueSkyIds.BlueSkyEffect))
                    dict.Remove(BlueSkyIds.BlueSkyEffect);
                _effectRegisteredPropUtilId = 0;
            }
            catch { }
        }

        private static bool ProductHasBlueSkyEffect(ProductItemInstance product)
        {
            return DefinitionHasBlueSkyEffect(product?.Definition);
        }

        private static bool DefinitionHasBlueSkyEffect(ItemDefinition def)
        {
            if (def == null) return false;
            if (def.ID == BlueSkyIds.BlueSky) return true;
            if (!TryCast(def, out ProductDefinition productDef)) return false;

            var props = productDef.Properties;
            if (props == null) return false;
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p != null && string.Equals(p.ID, BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GetItemInstanceId(ItemInstance item)
        {
            if (item == null) return null;
            return !string.IsNullOrEmpty(item.ID) ? item.ID : item.Definition?.ID;
        }

        private static void DiscoverBlueSky(ProductManager pm)
        {
            if (pm == null || BlueSky == null) return;
            var discovered = ProductManager.DiscoveredProducts;
            if (discovered != null && discovered.Contains(BlueSky)) return;

            if (pm.IsServerInitialized || !pm.IsClientInitialized)
                pm.SetProductDiscovered(null, BlueSky.ID, false);
            else
                pm.DiscoverProduct(BlueSky.ID);
        }

        // Backfills Products App discovery on existing v1.9.1 saves that already hold Blue Sky or a derived mix.
        private static void TryDiscoverExistingBlueSky(ProductManager pm)
        {
            if (_suppressInjectionThisLoad || pm == null || BlueSky == null) return;
            if (!pm.IsServerInitialized && pm.IsClientInitialized) return;

            try
            {
                var discovered = ProductManager.DiscoveredProducts;
                if (discovered != null && discovered.Contains(BlueSky)) return;

                bool found = false;
                if (discovered != null)
                {
                    for (int i = 0; i < discovered.Count; i++)
                    {
                        if (DefinitionHasBlueSkyEffect(discovered[i]))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    var hotbar = PlayerSingleton<PlayerInventory>.Instance?.hotbarSlots;
                    if (hotbar != null)
                    {
                        for (int i = 0; i < hotbar.Count; i++)
                        {
                            if (string.Equals(GetItemInstanceId(hotbar[i]?.ItemInstance), BlueSkyIds.BlueSky, StringComparison.OrdinalIgnoreCase))
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }

                if (!found)
                {
                    var storages = FindAllLoadedObjects<StorageEntity>();
                    for (int i = 0; i < storages.Length; i++)
                    {
                        var s = storages[i];
                        if (s != null && s.gameObject.scene.IsValid() && SlotsContainItem(s.ItemSlots, BlueSkyIds.BlueSky))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    var dealers = FindAllLoadedObjects<Dealer>();
                    for (int i = 0; i < dealers.Length; i++)
                    {
                        var d = dealers[i];
                        if (d != null && d.gameObject.scene.IsValid() && SlotsContainItem(d.ItemSlots, BlueSkyIds.BlueSky))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                {
                    AddToProductLists(pm, BlueSky);
                    DiscoverBlueSky(pm);
                    LogMsg("Discovered Blue Sky Meth from existing save data.");
                }
            }
            catch (Exception ex)
            {
                LogWarnOnce("discover-existing", $"Checking existing save for Blue Sky discovery failed: {ex.Message}");
            }
        }

        private static bool SlotsContainItem(List<ItemSlot> slots, string itemId)
        {
            if (slots == null) return false;
            for (int i = 0; i < slots.Count; i++)
            {
                if (string.Equals(GetItemInstanceId(slots[i]?.ItemInstance), itemId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static class Player_ApplyEffects_Patch
        {
            public static void Postfix(ProductItemInstance __instance, Player __0)
            {
                if (_suppressInjectionThisLoad || __0 == null) return;
                try
                {
                    if (!ProductHasBlueSkyEffect(__instance)) return;
                    ApplyBlueSkyToNativePlayer(__0);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("bluesky-player-apply", $"Applying '99.1% Pure' effect to player failed: {ex.Message}");
                }
            }
        }

        public static class Player_ClearEffects_Patch
        {
            public static void Postfix(ProductItemInstance __instance, Player __0)
            {
                if (_suppressInjectionThisLoad || __0 == null) return;
                try
                {
                    if (!ProductHasBlueSkyEffect(__instance)) return;
                    ClearBlueSkyFromNativePlayer(__0);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("bluesky-player-clear", $"Clearing '99.1% Pure' effect from player failed: {ex.Message}");
                }
            }
        }

        public static class Npc_ApplyEffects_Patch
        {
            public static void Prefix(ProductItemInstance __instance, S1Npc __0)
            {
                if (_suppressInjectionThisLoad || __0 == null || _blueSkyAffectedNpcIds.Count == 0) return;
                try
                {
                    int npcId = __0.GetInstanceID();
                    if (_blueSkyAffectedNpcIds.Contains(npcId))
                        ClearBlueSkyFromNativeNpc(__0);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("bluesky-npc-switch-clear", $"Clearing previous '99.1% Pure' effect on product switch failed: {ex.Message}");
                }
            }

            public static void Postfix(ProductItemInstance __instance, S1Npc __0)
            {
                if (_suppressInjectionThisLoad || __0 == null) return;
                try
                {
                    if (!ProductHasBlueSkyEffect(__instance)) return;
                    ApplyBlueSkyToNativeNpc(__0);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("bluesky-npc-apply", $"Applying '99.1% Pure' effect to NPC failed: {ex.Message}");
                }
            }
        }

        public static class Npc_ClearEffects_Patch
        {
            public static void Postfix(ProductItemInstance __instance, S1Npc __0)
            {
                if (_suppressInjectionThisLoad || __0 == null) return;
                try
                {
                    int npcId = __0.GetInstanceID();
                    if (!ProductHasBlueSkyEffect(__instance) && !_blueSkyAffectedNpcIds.Contains(npcId))
                        return;
                    ClearBlueSkyFromNativeNpc(__0);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("bluesky-npc-clear", $"Clearing '99.1% Pure' effect from NPC failed: {ex.Message}");
                }
            }
        }

        private static void ApplyAvatarBlueSkyVisuals(S1Avatar avatar)
        {
            if (avatar == null) return;

            if (avatar.Effects != null)
                avatar.Effects.OverrideEyeColor(CyanCrystalColor, 1.2f, true);

            if (avatar.EmotionManager != null)
                avatar.EmotionManager.AddEmotionOverride("Meth", BlueSkyIds.BlueSkyEffect, 0f, 1);
        }

        private static void ClearAvatarBlueSkyVisuals(S1Avatar avatar)
        {
            if (avatar == null) return;

            if (avatar.Effects != null)
                avatar.Effects.ResetEyeColor(true);

            if (avatar.EmotionManager != null)
                avatar.EmotionManager.RemoveEmotionOverride(BlueSkyIds.BlueSkyEffect);
        }

        // Player consumption effects (move speed multiplier, stamina refill, FoV, shake, post-processing).
        // IMPORTANT (MP): Always guard camera/movement/post-processing behind `if (target != Player.Local) return;`
        // so remote players consuming Blue Sky only update their avatar visuals on other clients.
        private static void ApplyBlueSkyToNativePlayer(Player target)
        {
            if (target == null) return;

            ApplyAvatarBlueSkyVisuals(target.Avatar);

            if (target != Player.Local) return;

            var movement = PlayerSingleton<PlayerMovement>.Instance;
            if (movement != null)
            {
                movement.MoveSpeedMultiplierStack?.Remove(BlueSkyIds.BlueSkyEffect);
                movement.MoveSpeedMultiplierStack?.Add(new FloatStack.StackEntry(BlueSkyIds.BlueSkyEffect, 1.35f, FloatStack.EStackMode.Multiplicative, 0));
                movement.SetStamina(PlayerMovement.StaminaReserveMax, true);
            }

            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam != null)
            {
                cam.FoVChangeSmoother?.AddOverride(10f, 1, BlueSkyIds.BlueSkyEffect);
                cam.StartCameraShake(0.28f, 0.75f, true);
            }

            var pp = Singleton<PostProcessingManager>.Instance;
            if (pp != null)
            {
                pp.SaturationController?.AddOverride(30f, 1, BlueSkyIds.BlueSkyEffect);
                pp.ChromaticAberrationController?.AddOverride(0.22f, 1, BlueSkyIds.BlueSkyEffect);
                pp.BloomController?.AddOverride(1.35f, 1, BlueSkyIds.BlueSkyEffect);
            }
        }

        private static void ClearBlueSkyFromNativePlayer(Player target)
        {
            if (target == null) return;

            ClearAvatarBlueSkyVisuals(target.Avatar);

            if (target == Player.Local)
                ClearLocalPlayerBlueSkyEffects();
        }

        private static void ClearLocalPlayerBlueSkyEffects()
        {
            try
            {
                var movement = PlayerSingleton<PlayerMovement>.Instance;
                movement?.MoveSpeedMultiplierStack?.Remove(BlueSkyIds.BlueSkyEffect);

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam?.FoVChangeSmoother?.RemoveOverride(BlueSkyIds.BlueSkyEffect);

                var pp = Singleton<PostProcessingManager>.Instance;
                if (pp != null)
                {
                    pp.SaturationController?.RemoveOverride(BlueSkyIds.BlueSkyEffect);
                    pp.ChromaticAberrationController?.RemoveOverride(BlueSkyIds.BlueSkyEffect);
                    pp.BloomController?.RemoveOverride(BlueSkyIds.BlueSkyEffect);
                }
            }
            catch { }
        }

        // NPC consumption effects (glowing blue eyes, emotion, voice line, walk speed boost).
        // IMPORTANT (MP): Only adjust NPCSpeedController on host/SP (`IsServerInitialized || !IsClientInitialized`);
        // clients receive NPC movement from the server. On clear, reset to 1f matching vanilla drug effects.
        private static void ApplyBlueSkyToNativeNpc(S1Npc npc)
        {
            if (npc == null) return;
            int npcId = npc.GetInstanceID();
            _blueSkyAffectedNpcIds.Add(npcId);
            ApplyAvatarBlueSkyVisuals(npc.Avatar);

            if (npc.IsServerInitialized || !npc.IsClientInitialized)
            {
                var sc = npc.Movement?.SpeedController;
                if (sc != null)
                {
                    float baseMult = Mathf.Max(0.1f, sc._speedMultiplier);
                    float applied = Mathf.Min(baseMult * BlueSkyNpcSpeedMultiplier, BlueSkyNpcMaxSpeedMult);
                    sc.SetSpeedMultiplier(applied);
                }
            }

            if (npc.Avatar == null || !npc.Avatar.IsCulled)
                npc.PlayVO(EVOLineType.Surprised, false);
        }

        private static void ClearBlueSkyFromNativeNpc(S1Npc npc)
        {
            if (npc == null) return;
            int npcId = npc.GetInstanceID();
            _blueSkyAffectedNpcIds.Remove(npcId);
            ClearAvatarBlueSkyVisuals(npc.Avatar);

            if (npc.IsServerInitialized || !npc.IsClientInitialized)
            {
                var sc = npc.Movement?.SpeedController;
                if (sc != null)
                    sc.SetSpeedMultiplier(1f);
            }
        }

        private static void AddToProductLists(ProductManager pm, MethDefinition product)
        {
            if (pm == null || product == null) return;

            if (pm.AllProducts != null && !pm.AllProducts.Contains(product))
                pm.AllProducts.Add(product);
            if (pm.ProductNames != null && !pm.ProductNames.Contains(product.Name))
                pm.ProductNames.Add(product.Name);

            var prices = pm.ProductPrices;
            if (prices != null && !prices.ContainsKey(product))
                prices.Add(product, product.MarketValue);
        }

        private static void RegisterItemInRegistry(ItemDefinition item)
        {
            var registry = Singleton<Registry>.Instance;
            if (registry == null || item == null) return;

            if (Registry.GetItem(item.ID) != null)
                return;

            registry.AddToRegistry(item);

            if (Registry.GetItem(item.ID) == null)
                LogWarn($"Registry.AddToRegistry did not register '{item.ID}'.");
        }

        // Chemistry Station recipe for Blue Meth (title, cook duration, ingredients, output quantity).
        // NOTE: If you change the output item or quantity, update `BlueSkyIds.LabRecipeId` in BlueSkySaveCleaner.cs.
        private static void CreateAndRegisterRecipes()
        {
            if (_labRecipe != null) return;

            var acid       = Registry.GetItem(BlueSkyIds.VanillaAcid);
            var phosphorus = Registry.GetItem(BlueSkyIds.VanillaPhosphorus);
            StationRecipe vanillaLiquidMethRecipe = FindVanillaStationRecipe(BlueSkyIds.VanillaLiquidMeth);

            if (PristinePseudo == null || LiquidBlue == null || BlueSky == null)
                LogWarn("One or more custom definitions failed to create — dependent recipes will be skipped.");

            if (acid != null && phosphorus != null && PristinePseudo != null && LiquidBlue != null)
            {
                _labRecipe = vanillaLiquidMethRecipe != null
                    ? UnityEngine.Object.Instantiate<StationRecipe>(vanillaLiquidMethRecipe)
                    : ScriptableObject.CreateInstance<StationRecipe>();

                UnityEngine.Object.DontDestroyOnLoad(_labRecipe);
                _labRecipe.name             = BlueSkyIds.LabRecipe;
                _labRecipe.RecipeTitle      = "Blue Meth";
                _labRecipe.CookTime_Mins    = 300;
                _labRecipe.Unlocked         = true;
                _labRecipe.FinalLiquidColor = BlueLiquidColor;

                var labIngredients = new List<StationRecipe.IngredientQuantity>();
                labIngredients.Add(CreateIngredient(PristinePseudo, 1));
                labIngredients.Add(CreateIngredient(acid,           1));
                labIngredients.Add(CreateIngredient(phosphorus,     1));
                _labRecipe.Ingredients = labIngredients;
                _labRecipe.Product = new StationRecipe.ItemQuantity { Item = LiquidBlue, Quantity = 1 };
            }
        }

        private static StationRecipe.IngredientQuantity CreateIngredient(ItemDefinition item, int quantity)
        {
            var items = new List<ItemDefinition>();
            items.Add(item);
            return new StationRecipe.IngredientQuantity { Items = items, Quantity = quantity };
        }

        private static StationRecipe FindVanillaStationRecipe(string productItemId)
        {
            try
            {
                var canvasRecipes = Singleton<ChemistryStationInterface>.Instance?.Recipes;
                if (canvasRecipes != null)
                {
                    for (int i = 0; i < canvasRecipes.Count; i++)
                    {
                        var r = canvasRecipes[i];
                        if (r != null && r.Product?.Item?.ID == productItemId)
                            return r;
                    }
                }
            }
            catch { }

            var allRecipes = FindAllLoadedObjects<StationRecipe>();
            for (int i = 0; i < allRecipes.Length; i++)
            {
                var r = allRecipes[i];
                if (r != null && r.Product?.Item?.ID == productItemId)
                    return r;
            }
            return null;
        }

        private IEnumerator InjectShopListingsRoutine()
        {
            try
            {
                bool injected = false;
                float waited  = 0f;

                while (waited < 30f)
                {
                    injected = TryInjectPristinePseudoIntoShops();
                    if (injected) break;
                    waited += 0.5f;
                    yield return _shopPollWait;
                }

                if (!injected)
                    LogWarn("No shop selling 'pseudo' was found; Pure Ephedrine was not added to a shop.");
            }
            finally
            {
                _shopToken = null;
            }
        }

        // Shop / Supplier injection rules (which shop sells Pure Ephedrine, stock limits, delivery flag).
        // Adds Pure Ephedrine to every ShopInterface that sells vanilla Pseudo (e.g. Shirley Watts).
        private static bool TryInjectPristinePseudoIntoShops()
        {
            if (PristinePseudo == null) return false;

            bool injectedAny = false;
            try
            {
                var allShops = ShopInterface.AllShops;
                if (allShops == null) return false;

                for (int i = 0; i < allShops.Count; i++)
                {
                    var s1Shop = allShops[i];
                    if (s1Shop == null) continue;

                    bool sellsVanillaPseudo = s1Shop.GetListing(BlueSkyIds.VanillaPseudo) != null;
                    var listing = s1Shop.GetListing(BlueSkyIds.PristinePseudo);

                    if (listing == null && sellsVanillaPseudo && s1Shop.Listings != null)
                    {
                        listing = new ShopListing
                        {
                            Item           = PristinePseudo,
                            name           = PristinePseudo.Name,
                            LimitedStock   = false,
                            DefaultStock   = -1,
                            CanBeDelivered = false
                        };
                        s1Shop.Listings.Add(listing);
                        listing.Initialize(s1Shop);
                        s1Shop.CreateListingUI(listing);
                        if (PristinePseudo.RequiresLevelToPurchase)
                        {
                            var levelManager = NetworkSingleton<LevelManager>.Instance;
                            if (levelManager != null)
                                levelManager.AddUnlockable(new Unlockable(PristinePseudo.RequiredRank, PristinePseudo.Name, PristinePseudo.Icon));
                        }
                        LogMsg($"Added Pure Ephedrine to shop '{s1Shop.ShopName}'.");
                    }

                    if (listing == null) continue;

                    listing.CanBeDelivered = false;
                    s1Shop.RefreshUnlockStatus();
                    if (s1Shop.IsOpen)
                        s1Shop.RefreshShownItems();
                    injectedAny = true;
                }
            }
            catch (Exception ex)
            {
                LogWarnOnce("shop-listing-sync", $"Refreshing shop listing state failed: {ex.Message}");
            }

            return injectedAny;
        }
    }
}

