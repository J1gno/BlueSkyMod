// ============================================================================
// VisualsAndAvatars.cs — Color Palette, Icon Tinting & Multiplayer Held Visuals
// ============================================================================
// Primary customization file for art & visuals:
//   - Color palette (`BlueLiquidColor`, `CyanCrystalColor`, `PureEphedrinePillColor`, etc.)
//   - Runtime sprite tinting & packaged icon injection (`TintIcon`, `InjectPackagedIcons`)
//   - Station liquid & stored-item recoloring (`LiquidVisual_Initialize_Patch`, `StoredItem_Initialize_Patch`)
//   - First-person & third-person multiplayer held-item visuals (`Avatar_SetEquippable_Patch`)
//
// MULTIPLAYER DESIGN NOTE:
//   Held custom visuals ride on vanilla `Player.SendEquippable_Networked(assetPath)` by appending a
//   `#tag` suffix (e.g. `#bluesky_pkg:baggie|mixid`). `Avatar_SetEquippable_Patch` strips the tag before
//   loading the real prefab and then applies the matching custom materials/packaged crystal meshes on
//   every client. If a mix definition hasn't finished replicating yet, `_pendingAvatarEquips` queues it
//   and `FlushPendingAvatarEquips` upgrades the held model once `ApplyAppearanceSettings` runs.
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.Rendering;

using Il2CppInterop.Runtime;
using Il2CppScheduleOne;
using Il2CppScheduleOne.AvatarFramework.Equipping;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Equipping;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Product.Packaging;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Storage;
using Il2CppSystem.Collections.Generic;
using S1Avatar = Il2CppScheduleOne.AvatarFramework.Avatar;

namespace BlueSkyMod
{
    public partial class Main
    {
        // Master color palette used across icons, pills, liquids, crystals, eyes & particles.
        private static readonly Color32 BlueLiquidColor        = new Color32(58, 178, 242, 255);
        private static readonly Color32 CyanCrystalColor       = new Color32(135, 206, 235, 255);
        private static readonly Color32 BlueSkySecondaryColor  = new Color32(92, 168, 204, 255);
        private static readonly Color32 PureEphedrinePillColor = new Color32(72, 188, 246, 255);

        private static Material _bluePillMaterial;
        private static Material _blueLiquidMaterial;
        private static Material _blueCrystalFallbackMaterial;

        private static readonly Action<Renderer> ReplacePillMaterialsAction = ReplacePillMaterials;
        private static readonly Action<Renderer> ApplyBlueSkyMaterialAction = ApplyBlueSkyMaterial;

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> _tintCache =
            new System.Collections.Generic.Dictionary<string, Sprite>(StringComparer.Ordinal);

        // Tags appended to networked avatar equippable paths so all clients render the right custom visuals.
        private const string BlueSkyAvatarSuffix          = "#bluesky";
        private const string BlueSkyProductPrefix         = "#bluesky:";
        private const string BlueSkyPackagedAvatarSuffix  = "#bluesky_pkg";
        private const string BlueSkyPackagedPrefix        = "#bluesky_pkg:";
        private const string BlueSkyPackagedReplacePrefix = "#bluesky_pkg_replace:";
        private const string EphedrineAvatarSuffix        = "#ephedrine";
        private const string LiquidBlueAvatarSuffix       = "#liquidblue";
        private const string ClonedMethVisualsName        = "__BlueSkyMethVisuals";

        private static bool _customFallbackAvatarEquipActive;
        private static string _cachedFallbackAvatarPath;

        [ThreadStatic]
        private static bool _inBlueSkyAvatarEquip;

        private sealed class PendingAvatarEquip
        {
            public S1Avatar Avatar;
            public AvatarEquippable Equippable;
            public string ProductId;
            public bool Packaged;
            public string PackagingId;
            public bool ReplaceContainer;
        }

        private static readonly System.Collections.Generic.List<PendingAvatarEquip> _pendingAvatarEquips =
            new System.Collections.Generic.List<PendingAvatarEquip>();

        private static Material CreateTintedMaterial(Material source, string matName, Color color, Color? emissionColor = null)
        {
            if (source == null) return null;

            Material mat = new Material(source)
            {
                name      = matName,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     color);
            if (emissionColor.HasValue && mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", emissionColor.Value);
            mat.color = color;
            return mat;
        }

        private static Material GetOrCreateBlueLiquidMaterial(Material source)
        {
            if (_blueLiquidMaterial != null) return _blueLiquidMaterial;
            _blueLiquidMaterial = CreateTintedMaterial(source, "LiquidBlue_Mat", BlueLiquidColor);
            return _blueLiquidMaterial;
        }

        private static Material GetOrCreateBluePillMaterial(Material source)
        {
            if (_bluePillMaterial != null) return _bluePillMaterial;
            _bluePillMaterial = CreateTintedMaterial(source, "PureEphedrine_Pill_Mat", PureEphedrinePillColor);
            return _bluePillMaterial;
        }

        private static Material GetOrCreateBlueCrystalFallbackMaterial(Material source)
        {
            if (_blueCrystalFallbackMaterial != null) return _blueCrystalFallbackMaterial;
            _blueCrystalFallbackMaterial = CreateTintedMaterial(source, "BlueSky_Fallback_Mat", CyanCrystalColor, CyanCrystalColor);
            return _blueCrystalFallbackMaterial;
        }

        private static void RefreshCustomIcons(ProductManager pm)
        {
            try
            {
                if (BlueSky != null && BlueSky.Icon == null && pm?.DefaultMeth?.Icon != null)
                    BlueSky.Icon = TintIcon(pm.DefaultMeth.Icon, CyanCrystalColor);

                if (PristinePseudo != null && PristinePseudo.Icon == null)
                {
                    var basePseudo = Registry.GetItem(BlueSkyIds.VanillaPseudo);
                    if (basePseudo != null && basePseudo.Icon != null)
                        PristinePseudo.Icon = TintIcon(basePseudo.Icon, PureEphedrinePillColor, true);
                }

                if (LiquidBlue != null && LiquidBlue.Icon == null)
                {
                    var babyBlue = Registry.GetItem(BlueSkyIds.VanillaBabyBlue);
                    if (babyBlue != null && babyBlue.Icon != null)
                    {
                        LiquidBlue.Icon = babyBlue.Icon;
                    }
                    else
                    {
                        var original = Registry.GetItem(BlueSkyIds.VanillaLiquidMeth);
                        if (original != null && original.Icon != null)
                            LiquidBlue.Icon = TintIcon(original.Icon, BlueLiquidColor);
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarnOnce("icon-refresh", $"Refreshing custom item icons failed: {ex.Message}");
            }
        }

        private static int InjectPackagedIcons(ProductIconManager iconManager)
        {
            var pm = NetworkSingleton<ProductManager>.Instance;
            if (iconManager == null || pm?.DefaultMeth == null) return 0;

            int managerId = iconManager.GetInstanceID();
            if (managerId == _iconsInjectedForManagerId) return 0;

            List<ProductIconManager.ProductIcon> iconsList = iconManager.icons;
            if (iconsList == null)
            {
                LogWarnOnce("icons-list", "ProductIconManager icon list not found — packaged Blue Sky icons were not injected (game update changed it?).");
                return 0;
            }

            string sourceID   = pm.DefaultMeth.ID;
            var existingNames = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var sourceIcons   = new System.Collections.Generic.List<ProductIconManager.ProductIcon>();

            for (int i = 0; i < iconsList.Count; i++)
            {
                var pIcon = iconsList[i];
                if (pIcon == null) continue;
                if (!string.IsNullOrEmpty(pIcon.name))
                    existingNames.Add(pIcon.name);
                if (pIcon.ProductID == sourceID)
                    sourceIcons.Add(pIcon);
            }

            int added = 0;
            for (int i = 0; i < sourceIcons.Count; i++)
            {
                var sourceIcon   = sourceIcons[i];
                string entryName = BlueSkyIds.BlueSky + "_" + sourceIcon.PackagingID;
                if (existingNames.Contains(entryName)) continue;

                iconsList.Add(new ProductIconManager.ProductIcon
                {
                    name        = entryName,
                    ProductID   = BlueSkyIds.BlueSky,
                    PackagingID = sourceIcon.PackagingID,
                    Icon        = TintIcon(sourceIcon.Icon, CyanCrystalColor)
                });
                existingNames.Add(entryName);
                added++;
            }

            if (sourceIcons.Count > 0)
                _iconsInjectedForManagerId = managerId;

            if (added > 0)
                LogMsg($"Injected {added} packaged Blue Sky icon(s).");
            return added;
        }

        private static Sprite TintIcon(Sprite source, Color32 tint, bool redOnly = false)
        {
            if (source == null || source.texture == null) return null;

            Rect rect = source.rect;
            string sourceId = !string.IsNullOrEmpty(source.name)
                ? source.name
                : source.GetInstanceID().ToString();
            string key = $"{sourceId}|{(int)rect.x},{(int)rect.y},{(int)rect.width},{(int)rect.height}|{(redOnly ? 1 : 0)}|{tint.r},{tint.g},{tint.b}";

            if (_tintCache.TryGetValue(key, out Sprite cached) && cached != null)
                return cached;

            Texture2D srcTex = source.texture;
            Texture2D newTex = new Texture2D(srcTex.width, srcTex.height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };

            try
            {
                Color32[] pixels;
                if (srcTex.isReadable)
                {
                    pixels = srcTex.GetPixels32();
                }
                else
                {
                    RenderTexture rt   = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture prev = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(srcTex, rt);
                        RenderTexture.active = rt;
                        newTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    }
                    finally
                    {
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                    }

                    pixels = newTex.GetPixels32();
                }

                if (redOnly)
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Color32 px = pixels[i];
                        if (px.a <= 20) continue;

                        int maxGB = px.g > px.b ? px.g : px.b;
                        int redExcess = px.r - maxGB;
                        if (redExcess <= 10) continue;

                        float t = redExcess >= 32 ? 1f : (redExcess - 10) / 22f;
                        float chroma = redExcess / 255f;
                        float targetR = maxGB + chroma * tint.r;
                        float targetG = maxGB + chroma * tint.g;
                        float targetB = maxGB + chroma * tint.b;

                        byte r = (byte)Mathf.Clamp((int)(px.r + (targetR - px.r) * t + 0.5f), 0, 255);
                        byte g = (byte)Mathf.Clamp((int)(px.g + (targetG - px.g) * t + 0.5f), 0, 255);
                        byte b = (byte)Mathf.Clamp((int)(px.b + (targetB - px.b) * t + 0.5f), 0, 255);
                        pixels[i] = new Color32(r, g, b, px.a);
                    }
                }
                else
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Color32 px = pixels[i];
                        if (px.a > 20)
                        {
                            pixels[i] = new Color32(
                                (byte)((px.r * tint.r) / 255),
                                (byte)((px.g * tint.g) / 255),
                                (byte)((px.b * tint.b) / 255),
                                px.a);
                        }
                    }
                }

                newTex.SetPixels32(pixels);
                newTex.Apply(false, true);

                Vector2 normalizedPivot = new Vector2(
                    rect.width  > 0f ? source.pivot.x / rect.width  : 0.5f,
                    rect.height > 0f ? source.pivot.y / rect.height : 0.5f);

                Sprite sprite = Sprite.Create(newTex, rect, normalizedPivot, source.pixelsPerUnit);
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

                _tintCache[key] = sprite;
                return sprite;
            }
            catch
            {
                UnityEngine.Object.Destroy(newTex);
                throw;
            }
        }

        public static class ProductIconManager_Awake_Patch
        {
            public static void Postfix(ProductIconManager __instance)
            {
                if (_suppressInjectionThisLoad || __instance == null || BlueSky == null) return;
                try
                {
                    InjectPackagedIcons(__instance);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("icon-awake-init", $"Early ProductIconManager icon injection failed: {ex.Message}");
                }
            }
        }

        public static class LiquidVisual_Initialize_Patch
        {
            public static void Postfix(LiquidMeth_StationItem __instance, StorableItemDefinition itemDefinition)
            {
                if (_suppressInjectionThisLoad) return;
                try
                {
                    if (__instance == null || itemDefinition == null || itemDefinition.ID != BlueSkyIds.LiquidBlue) return;

                    if (!__instance.gameObject.activeSelf)
                        __instance.gameObject.SetActive(true);

                    if (__instance.Visuals != null)
                    {
                        if (__instance.Visuals.StaticLiquidMesh != null)
                        {
                            var shared = __instance.Visuals.StaticLiquidMesh.sharedMaterial;
                            var blueMat = GetOrCreateBlueLiquidMaterial(shared);
                            if (blueMat != null)
                                __instance.Visuals.StaticLiquidMesh.sharedMaterial = blueMat;
                        }
                        if (__instance.Visuals.LiquidContainer != null)
                            __instance.Visuals.LiquidContainer.SetLiquidColor(BlueLiquidColor, true, true);
                    }

                    var pourable = __instance.GetComponentInChildren<PourableModule>(true);
                    if (pourable != null)
                    {
                        pourable.LiquidColor        = BlueLiquidColor;
                        pourable.PourParticlesColor = BlueLiquidColor;
                    }

                    var cookable = __instance.GetComponentInChildren<CookableModule>(true);
                    if (cookable != null)
                    {
                        cookable.LiquidColor     = BlueLiquidColor;
                        cookable.SolidColor      = CyanCrystalColor;
                        cookable.Product         = BlueSky;
                        cookable.ProductQuantity = 10;
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("liquid-visual", $"Blue liquid station visual patch failed: {ex.Message}");
                }
            }
        }

        public static class StoredItem_Initialize_Patch
        {
            public static void Postfix(StoredItem __instance, StorableItemInstance __0)
            {
                if (_suppressInjectionThisLoad) return;
                try
                {
                    if (__instance == null) return;
                    var def = __0?.Definition;
                    if (def == null || def.ID != BlueSkyIds.PristinePseudo) return;
                    ApplyToAllRenderers(__instance.gameObject, ReplacePillMaterialsAction);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("ephedrine-stored", $"Pure Ephedrine stored visual patch failed: {ex.Message}");
                }
            }
        }

        private static bool IsBlueSkyMethProduct(ProductItemInstance product)
        {
            var def = product?.Definition;
            if (def == null) return false;
            if (def.ID == BlueSkyIds.BlueSky) return BlueSky != null;
            if (!TryCast(def, out MethDefinition _)) return false;
            return ProductHasBlueSkyEffect(product);
        }

        private static MethDefinition ResolveAvatarMethDefinition(string productId, out bool pendingReplication)
        {
            pendingReplication = false;
            if (!string.IsNullOrEmpty(productId) && productId != BlueSkyIds.BlueSky)
            {
                var raw = Registry.GetItem(productId);
                if (raw != null && TryCast(raw, out MethDefinition customMeth) && customMeth.CrystalMaterial != null)
                    return customMeth;
                pendingReplication = true;
            }

            if (BlueSky == null)
                pendingReplication = true;
            return BlueSky;
        }

        private static void SplitPackagingAndProduct(string payload, out string packagingId, out string productId)
        {
            int sep = payload != null ? payload.IndexOf('|') : -1;
            if (sep >= 0)
            {
                packagingId = payload.Substring(0, sep);
                productId   = payload.Substring(sep + 1);
            }
            else
            {
                packagingId = payload;
                productId   = null;
            }
        }

        private static bool TryGetBlueSkyAvatarEquippablePath(Equippable_Viewmodel vm, ItemInstance item, out string customPath, out bool isFallback)
        {
            customPath = null;
            isFallback = false;
            if (vm == null || item == null) return false;

            string itemId = item.Definition?.ID;
            if (string.IsNullOrEmpty(itemId)) return false;

            string assetPath = vm.AvatarEquippable != null ? vm.AvatarEquippable.AssetPath : null;
            if (string.IsNullOrEmpty(assetPath))
            {
                if (TryCast(item, out ProductItemInstance fallbackProduct) &&
                    fallbackProduct.AppliedPackaging != null &&
                    !string.IsNullOrEmpty(fallbackProduct.AppliedPackaging.ID) &&
                    IsBlueSkyMethProduct(fallbackProduct))
                {
                    string fallbackPath = GetFallbackAvatarEquippablePath();
                    if (!string.IsNullOrEmpty(fallbackPath))
                    {
                        string prodSuffix = itemId == BlueSkyIds.BlueSky ? string.Empty : "|" + itemId;
                        customPath = fallbackPath + BlueSkyPackagedReplacePrefix + fallbackProduct.AppliedPackaging.ID + prodSuffix;
                        isFallback = true;
                        return true;
                    }
                }
                return false;
            }

            string suffix = null;
            if (TryCast(item, out ProductItemInstance productItem) && IsBlueSkyMethProduct(productItem))
            {
                string prodSuffix = itemId == BlueSkyIds.BlueSky ? string.Empty : "|" + itemId;
                if (productItem.AppliedPackaging != null)
                {
                    string pkgId = productItem.AppliedPackaging.ID;
                    suffix = !string.IsNullOrEmpty(pkgId)
                        ? BlueSkyPackagedPrefix + pkgId + prodSuffix
                        : BlueSkyPackagedAvatarSuffix + prodSuffix;
                }
                else
                {
                    suffix = itemId == BlueSkyIds.BlueSky
                        ? BlueSkyAvatarSuffix
                        : BlueSkyProductPrefix + itemId;
                }
            }
            else if (itemId == BlueSkyIds.PristinePseudo)
            {
                suffix = EphedrineAvatarSuffix;
            }
            else if (itemId == BlueSkyIds.LiquidBlue)
            {
                suffix = LiquidBlueAvatarSuffix;
            }

            if (suffix == null) return false;

            customPath = assetPath + suffix;
            return true;
        }

        public static class Equippable_Equip_Patch
        {
            public static void Postfix(Equippable_Viewmodel __instance, ItemInstance __0)
            {
                if (_suppressInjectionThisLoad || __instance == null) return;
                try
                {
                    var def = __0?.Definition;
                    if (def != null && def.ID == BlueSkyIds.PristinePseudo)
                        ApplyToAllRenderers(__instance.gameObject, ReplacePillMaterialsAction);

                    if (__instance.AvatarEquippable == null &&
                        TryGetBlueSkyAvatarEquippablePath(__instance, __0, out string customPath, out bool isFallback) &&
                        isFallback)
                    {
                        var localPlayer = Player.Local;
                        if (localPlayer != null)
                        {
                            _customFallbackAvatarEquipActive = true;
                            localPlayer.SendEquippable_Networked(customPath);
                            return;
                        }
                    }

                    if (_customFallbackAvatarEquipActive)
                    {
                        _customFallbackAvatarEquipActive = false;
                        if (__instance.AvatarEquippable == null)
                        {
                            var localPlayer = Player.Local;
                            if (localPlayer != null)
                                localPlayer.SendEquippable_Networked(string.Empty);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("ephedrine-viewmodel", $"First-person equip visual patch failed: {ex.Message}");
                }
            }
        }

        public static class Equippable_PlayUnequipAnimation_Patch
        {
            public static void Postfix(Equippable_Viewmodel __instance)
            {
                if (!_customFallbackAvatarEquipActive)
                    return;

                try
                {
                    if (__instance == null || __instance.AvatarEquippable != null)
                        return;

                    _customFallbackAvatarEquipActive = false;
                    var localPlayer = Player.Local;
                    if (localPlayer != null)
                        localPlayer.SendEquippable_Networked(string.Empty);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("custom-unequip-send", $"Custom third-person unequip hook failed: {ex.Message}");
                }
            }
        }

        // Restores held third-person Blue Sky visuals after consuming one unit from a stack.
        // Vanilla ConsumeRoutine re-equips a new viewmodel before calling StopConsume() on the old one;
        // skipping when IsChildOf(vm.transform) avoids interfering with mid-consume unequip.
        public static class ProductConsumeAnimation_StopConsume_Patch
        {
            public static void Postfix(ProductConsumeAnimation __instance)
            {
                if (_suppressInjectionThisLoad || __instance == null) return;
                try
                {
                    var localPlayer = Player.Local;
                    if (localPlayer == null || !__instance.transform.IsChildOf(localPlayer.transform))
                        return;

                    var inv = PlayerSingleton<PlayerInventory>.Instance;
                    var currentEq = inv != null ? inv.Equippable : null;
                    if (currentEq == null || !TryCast(currentEq, out Equippable_Viewmodel vm))
                        return;
                    if (__instance.transform.IsChildOf(vm.transform))
                        return;

                    if (!TryGetBlueSkyAvatarEquippablePath(vm, vm.itemInstance, out string customPath, out bool isFallback))
                        return;

                    _customFallbackAvatarEquipActive = isFallback;
                    localPlayer.SendEquippable_Networked(customPath);
                }
                catch (Exception ex)
                {
                    LogWarnOnce("consume-stop-reequip", $"Restoring held Blue Sky visual after consumption failed: {ex.Message}");
                }
            }
        }

        private static string GetFallbackAvatarEquippablePath()
        {
            if (!string.IsNullOrEmpty(_cachedFallbackAvatarPath))
                return _cachedFallbackAvatarPath;

            var jarItem = Registry.GetItem("jar");
            if (jarItem != null && TryCast(jarItem, out PackagingDefinition jarPkg) &&
                jarPkg.Equippable_Filled != null &&
                TryCast(jarPkg.Equippable_Filled, out Equippable_Viewmodel jarVm) &&
                jarVm.AvatarEquippable != null &&
                !string.IsNullOrEmpty(jarVm.AvatarEquippable.AssetPath))
            {
                _cachedFallbackAvatarPath = jarVm.AvatarEquippable.AssetPath;
                return _cachedFallbackAvatarPath;
            }

            if (BlueSky != null && BlueSky.Equippable != null &&
                TryCast(BlueSky.Equippable, out Equippable_Viewmodel methVm) &&
                methVm.AvatarEquippable != null &&
                !string.IsNullOrEmpty(methVm.AvatarEquippable.AssetPath))
            {
                _cachedFallbackAvatarPath = methVm.AvatarEquippable.AssetPath;
                return _cachedFallbackAvatarPath;
            }

            return null;
        }

        public static class Equippable_Viewmodel_PlayEquipAnimation_Patch
        {
            public static bool Prefix(Equippable_Viewmodel __instance)
            {
                if (_suppressInjectionThisLoad || __instance == null || __instance.AvatarEquippable == null)
                    return true;
                try
                {
                    if (!TryGetBlueSkyAvatarEquippablePath(__instance, __instance.itemInstance, out string customPath, out bool isFallback) || isFallback)
                        return true;

                    var localPlayer = Player.Local;
                    if (localPlayer == null)
                        return true;

                    _customFallbackAvatarEquipActive = false;
                    localPlayer.SendEquippable_Networked(customPath);
                    return false;
                }
                catch (Exception ex)
                {
                    LogWarnOnce("custom-equip-send", $"Custom third-person equip hook failed: {ex.Message}");
                    return true;
                }
            }
        }

        public static class Avatar_SetEquippable_Patch
        {
            public static bool Prefix(S1Avatar __instance, string __0, ref AvatarEquippable __result)
            {
                if (_inBlueSkyAvatarEquip || __instance == null || string.IsNullOrEmpty(__0))
                    return true;

                int hashIdx = __0.IndexOf('#');
                if (hashIdx < 0)
                    return true;

                string realPath = __0.Substring(0, hashIdx);
                string tag      = __0.Substring(hashIdx);

                bool isBlueSky = false;
                bool packaged = false;
                bool replaceContainer = false;
                string packagingId = null;
                string productId = null;
                bool isEphedrine = false;
                bool isLiquidBlue = false;

                if (tag == BlueSkyAvatarSuffix)
                {
                    isBlueSky = true;
                }
                else if (tag.StartsWith(BlueSkyProductPrefix, StringComparison.Ordinal))
                {
                    isBlueSky = true;
                    productId = tag.Substring(BlueSkyProductPrefix.Length);
                }
                else if (tag == BlueSkyPackagedAvatarSuffix)
                {
                    isBlueSky = true;
                    packaged = true;
                }
                else if (tag.StartsWith(BlueSkyPackagedAvatarSuffix + "|", StringComparison.Ordinal))
                {
                    isBlueSky = true;
                    packaged = true;
                    productId = tag.Substring(BlueSkyPackagedAvatarSuffix.Length + 1);
                }
                else if (tag.StartsWith(BlueSkyPackagedPrefix, StringComparison.Ordinal))
                {
                    isBlueSky = true;
                    packaged = true;
                    SplitPackagingAndProduct(tag.Substring(BlueSkyPackagedPrefix.Length), out packagingId, out productId);
                }
                else if (tag.StartsWith(BlueSkyPackagedReplacePrefix, StringComparison.Ordinal))
                {
                    isBlueSky = true;
                    packaged = true;
                    replaceContainer = true;
                    SplitPackagingAndProduct(tag.Substring(BlueSkyPackagedReplacePrefix.Length), out packagingId, out productId);
                }
                else if (tag == EphedrineAvatarSuffix)
                {
                    isEphedrine = true;
                }
                else if (tag == LiquidBlueAvatarSuffix)
                {
                    isLiquidBlue = true;
                }
                else
                {
                    return true;
                }

                _inBlueSkyAvatarEquip = true;
                AvatarEquippable eq = null;
                try
                {
                    eq = __instance.SetEquippable(realPath);
                    if (!_suppressInjectionThisLoad && eq != null)
                    {
                        if (!_definitionsReady && CoreSystemsReady())
                            EnsureCoreDefinitionsCreated(false);

                        if (isBlueSky)
                        {
                            var methDef = ResolveAvatarMethDefinition(productId, out bool pendingReplication);
                            if (methDef != null)
                            {
                                ApplyBlueSkyToAvatarEquippable(eq, methDef, packaged, packagingId, replaceContainer);
                            }
                            else if (replaceContainer)
                            {
                                HideNonAlignmentChildren(eq);
                            }
                            else if (!packaged)
                            {
                                ApplyBlueSkyToAvatarEquippable(eq, null, false, null, false);
                            }

                            if (pendingReplication)
                                TrackPendingAvatarEquip(__instance, eq, productId, packaged, packagingId, replaceContainer);
                        }
                        else if (isEphedrine)
                        {
                            ApplyToAllRenderers(eq.gameObject, ReplacePillMaterialsAction);
                        }
                        else if (isLiquidBlue)
                        {
                            ApplyLiquidBlueToAvatarEquippable(eq);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarnOnce("custom-avatar-visual", $"Applying custom visuals to held avatar item failed: {ex.Message}");
                }
                finally
                {
                    _inBlueSkyAvatarEquip = false;
                }
                __result = eq;
                return false;
            }
        }

        private static void TrackPendingAvatarEquip(S1Avatar avatar, AvatarEquippable eq, string productId, bool packaged, string packagingId, bool replaceContainer)
        {
            for (int i = _pendingAvatarEquips.Count - 1; i >= 0; i--)
            {
                var p = _pendingAvatarEquips[i];
                if (p.Avatar == null || p.Equippable == null || p.Avatar == avatar)
                    _pendingAvatarEquips.RemoveAt(i);
            }

            _pendingAvatarEquips.Add(new PendingAvatarEquip
            {
                Avatar           = avatar,
                Equippable       = eq,
                ProductId        = productId,
                Packaged         = packaged,
                PackagingId      = packagingId,
                ReplaceContainer = replaceContainer
            });
        }

        private static void FlushPendingAvatarEquips(MethDefinition readyMeth)
        {
            if (readyMeth == null) return;

            for (int i = _pendingAvatarEquips.Count - 1; i >= 0; i--)
            {
                var p = _pendingAvatarEquips[i];
                if (p.Avatar == null || p.Equippable == null || p.Avatar.CurrentEquippable != p.Equippable)
                {
                    _pendingAvatarEquips.RemoveAt(i);
                    continue;
                }

                bool targetsPureBlueSky = string.IsNullOrEmpty(p.ProductId) ||
                    string.Equals(p.ProductId, BlueSkyIds.BlueSky, StringComparison.OrdinalIgnoreCase);

                if ((targetsPureBlueSky && readyMeth == BlueSky) ||
                    (!targetsPureBlueSky && string.Equals(p.ProductId, readyMeth.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    ApplyBlueSkyToAvatarEquippable(p.Equippable, readyMeth, p.Packaged, p.PackagingId, p.ReplaceContainer);
                    _pendingAvatarEquips.RemoveAt(i);
                }
                else if (readyMeth == BlueSky && !targetsPureBlueSky)
                {
                    var raw = Registry.GetItem(p.ProductId);
                    if (raw != null && TryCast(raw, out MethDefinition mixMeth) && mixMeth.CrystalMaterial != null)
                    {
                        ApplyBlueSkyToAvatarEquippable(p.Equippable, mixMeth, p.Packaged, p.PackagingId, p.ReplaceContainer);
                        _pendingAvatarEquips.RemoveAt(i);
                    }
                    else
                    {
                        ApplyBlueSkyToAvatarEquippable(p.Equippable, BlueSky, p.Packaged, p.PackagingId, p.ReplaceContainer);
                    }
                }
            }
        }

        private static void ApplyToAllRenderers(GameObject root, Action<Renderer> apply, bool includeSkinned = true)
        {
            if (root == null) return;

            var meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (meshRenderers != null)
            {
                for (int i = 0; i < meshRenderers.Length; i++)
                    apply(meshRenderers[i]);
            }

            if (!includeSkinned) return;

            var skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skinnedRenderers != null)
            {
                for (int i = 0; i < skinnedRenderers.Length; i++)
                    apply(skinnedRenderers[i]);
            }
        }

        private static bool IsPillMaterial(Material m)
        {
            if (m == null) return false;
            string n = m.name;
            if (string.IsNullOrEmpty(n)) return false;

            if (n.IndexOf("metal",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("pack",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("bottle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("foil",   StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return n.IndexOf("pill", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("red",  StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ReplacePillMaterials(Renderer r)
        {
            if (r == null) return;
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) return;

            Material[] updated = null;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m != null && m == _bluePillMaterial)
                {
                    if (updated != null) updated[i] = m;
                    continue;
                }
                if (IsPillMaterial(m))
                {
                    if (updated == null)
                    {
                        updated = new Material[mats.Length];
                        for (int j = 0; j < i; j++) updated[j] = mats[j];
                    }
                    updated[i] = GetOrCreateBluePillMaterial(m);
                }
                else if (updated != null)
                {
                    updated[i] = m;
                }
            }

            if (updated != null)
                r.sharedMaterials = updated;
        }

        private static void SetLayerRecursive(Transform root, int layer)
        {
            if (root == null) return;
            root.gameObject.layer = layer;
            int count = root.childCount;
            for (int i = 0; i < count; i++)
                SetLayerRecursive(root.GetChild(i), layer);
        }

        private static void StripColliders(GameObject root)
        {
            if (root == null) return;
            var colliders = root.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
            if (colliders == null) return;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (TryCast(colliders[i], out Collider col))
                {
                    col.enabled = false;
                    UnityEngine.Object.Destroy(col);
                }
            }
        }

        private static bool IsAlignmentTransform(Transform t, Transform alignmentPoint)
        {
            if (t == null) return false;
            if (alignmentPoint != null && t == alignmentPoint) return true;
            string n = t.name;
            return !string.IsNullOrEmpty(n) && n.IndexOf("alignment", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void HideNonAlignmentChildren(AvatarEquippable eq)
        {
            if (eq == null) return;
            Transform alignment = eq.AlignmentPoint;
            for (int i = 0; i < eq.transform.childCount; i++)
            {
                var child = eq.transform.GetChild(i);
                if (!IsAlignmentTransform(child, alignment) && child.name != ClonedMethVisualsName)
                    child.gameObject.SetActive(false);
            }
        }

        private static void AttachPackagedBlueSkyVisuals(AvatarEquippable eq, MethDefinition product, string packagingId, bool replaceContainer)
        {
            if (eq == null || product == null || string.IsNullOrEmpty(packagingId)) return;

            var rawPkg = Registry.GetItem(packagingId);
            if (rawPkg == null || !TryCast(rawPkg, out PackagingDefinition pkgDef) || pkgDef.Equippable_Filled == null)
                return;

            var srcMulti = FindComponentInChildren<MultiTypeVisualsSetter>(pkgDef.Equippable_Filled, true);
            var srcMeth = srcMulti != null ? srcMulti.MethVisuals : FindComponentInChildren<MethVisualsSetter>(pkgDef.Equippable_Filled, true);
            if (srcMeth == null) return;

            Transform alignment = eq.AlignmentPoint;
            Transform targetParent = eq.transform;

            if (replaceContainer)
            {
                HideNonAlignmentChildren(eq);
            }
            else
            {
                Transform matched = null;
                if (srcMulti != null && srcMulti.transform.parent != null && srcMulti.transform.parent != pkgDef.Equippable_Filled.transform)
                    matched = eq.transform.Find(srcMulti.transform.parent.name);

                if (matched == null)
                {
                    for (int i = 0; i < eq.transform.childCount; i++)
                    {
                        var child = eq.transform.GetChild(i);
                        if (!IsAlignmentTransform(child, alignment) && child.name != ClonedMethVisualsName)
                        {
                            matched = child;
                            break;
                        }
                    }
                }

                if (matched != null)
                    targetParent = matched;

                var flat = targetParent.Find("Flat");
                var volumetric = targetParent.Find("Volumetric");
                if (flat != null && volumetric != null)
                {
                    flat.gameObject.SetActive(false);
                    volumetric.gameObject.SetActive(true);
                }
            }

            Transform existingClone = targetParent.Find(ClonedMethVisualsName);
            GameObject clone;
            if (existingClone != null)
            {
                clone = existingClone.gameObject;
            }
            else
            {
                MeshRenderer refRenderer = eq.GetComponentInChildren<MeshRenderer>(false);
                if (refRenderer == null)
                    refRenderer = eq.GetComponentInChildren<MeshRenderer>(true);
                int targetLayer = refRenderer != null ? refRenderer.gameObject.layer : targetParent.gameObject.layer;
                ShadowCastingMode targetShadowMode = refRenderer != null ? refRenderer.shadowCastingMode : ShadowCastingMode.On;
                bool targetReceiveShadows = refRenderer == null || refRenderer.receiveShadows;

                clone = UnityEngine.Object.Instantiate(srcMeth.gameObject, targetParent);
                if (clone == null) return;
                clone.name = ClonedMethVisualsName;

                bool distinctMulti = srcMulti != null && srcMulti.transform != srcMeth.transform;
                if (replaceContainer)
                {
                    clone.transform.localPosition = new Vector3(0f, 0.05f, 0f);
                    clone.transform.localRotation = distinctMulti
                        ? srcMulti.transform.localRotation * srcMeth.transform.localRotation
                        : srcMeth.transform.localRotation;
                    clone.transform.localScale    = new Vector3(0.65f, 0.65f, 0.65f);
                }
                else if (distinctMulti)
                {
                    Transform mt = srcMulti.transform;
                    Transform ct = srcMeth.transform;
                    clone.transform.localPosition = mt.localPosition + mt.localRotation * Vector3.Scale(mt.localScale, ct.localPosition);
                    clone.transform.localRotation = mt.localRotation * ct.localRotation;
                    clone.transform.localScale    = Vector3.Scale(mt.localScale, ct.localScale);
                }
                else
                {
                    clone.transform.localPosition = srcMeth.transform.localPosition;
                    clone.transform.localRotation = srcMeth.transform.localRotation;
                    clone.transform.localScale    = srcMeth.transform.localScale;
                }

                StripColliders(clone);
                SetLayerRecursive(clone.transform, targetLayer);

                var cloneRenderers = clone.GetComponentsInChildren<MeshRenderer>(true);
                if (cloneRenderers != null)
                {
                    for (int i = 0; i < cloneRenderers.Length; i++)
                    {
                        var cr = cloneRenderers[i];
                        if (cr == null) continue;
                        cr.shadowCastingMode = targetShadowMode;
                        cr.receiveShadows    = targetReceiveShadows;
                    }
                }
            }

            clone.SetActive(true);

            var clonedMeth = FindComponentInChildren<MethVisualsSetter>(clone.transform, true);
            if (clonedMeth != null)
            {
                clonedMeth.ApplyVisuals(product);
            }
            else if (product == BlueSky || product.CrystalMaterial == null)
            {
                ApplyToAllRenderers(clone, ApplyBlueSkyMaterialAction, false);
            }
            else
            {
                ApplyToAllRenderers(clone, r => ApplyMaterialToRenderer(r, product.CrystalMaterial), false);
            }
        }

        private static void ApplyBlueSkyToAvatarEquippable(AvatarEquippable eq, MethDefinition product, bool packaged, string packagingId, bool replaceContainer)
        {
            if (eq == null) return;

            if (product != null)
            {
                if (!replaceContainer)
                {
                    var multi = FindComponentInChildren<MultiTypeVisualsSetter>(eq, true);
                    if (multi != null)
                    {
                        multi.ApplyVisuals(product);
                        return;
                    }

                    var methVis = FindComponentInChildren<MethVisualsSetter>(eq, true);
                    if (methVis != null)
                    {
                        methVis.ApplyVisuals(product);
                        return;
                    }
                }

                if (packaged)
                {
                    AttachPackagedBlueSkyVisuals(eq, product, packagingId, replaceContainer);
                    return;
                }
            }

            if (packaged) return;

            if (product == null || product == BlueSky || product.CrystalMaterial == null)
                ApplyToAllRenderers(eq.gameObject, ApplyBlueSkyMaterialAction);
            else
                ApplyToAllRenderers(eq.gameObject, r => ApplyMaterialToRenderer(r, product.CrystalMaterial));
        }

        private static void ApplyLiquidBlueToAvatarEquippable(AvatarEquippable eq)
        {
            if (eq == null) return;

            var visuals = FindComponentInChildren<LiquidMethVisuals>(eq, true);
            if (visuals != null)
            {
                if (LiquidBlue != null && TryCast(LiquidBlue, out LiquidMethDefinition liquidDef))
                {
                    visuals.Setup(liquidDef);
                    return;
                }

                if (visuals.StaticLiquidMesh != null)
                {
                    var blueMat = GetOrCreateBlueLiquidMaterial(visuals.StaticLiquidMesh.sharedMaterial);
                    if (blueMat != null)
                        visuals.StaticLiquidMesh.sharedMaterial = blueMat;
                }
                if (visuals.LiquidContainer != null)
                    visuals.LiquidContainer.SetLiquidColor(BlueLiquidColor, true, true);
                return;
            }

            var container = FindComponentInChildren<LiquidContainer>(eq, true);
            if (container != null)
            {
                container.SetLiquidColor(BlueLiquidColor, true, true);
                return;
            }

            var renderers = eq.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                var mat = r.sharedMaterial;
                string goName = r.gameObject.name ?? string.Empty;
                string matName = mat != null ? (mat.name ?? string.Empty) : string.Empty;
                if (matName.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    matName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (string.Equals(goName, "Liquid", StringComparison.OrdinalIgnoreCase) ||
                    matName.IndexOf("liquid", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var blueMat = GetOrCreateBlueLiquidMaterial(mat);
                    if (blueMat != null)
                        r.sharedMaterial = blueMat;
                }
            }
        }

        private static void ApplyMaterialToRenderer(Renderer r, Material targetMat)
        {
            if (r == null || targetMat == null) return;

            var mats = r.sharedMaterials;
            if (mats != null && mats.Length > 1)
            {
                var replacement = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                    replacement[i] = targetMat;
                r.sharedMaterials = replacement;
            }
            else
            {
                r.sharedMaterial = targetMat;
            }
        }

        private static void ApplyBlueSkyMaterial(Renderer r)
        {
            if (r == null) return;

            Material targetMat = (BlueSky != null && BlueSky.CrystalMaterial != null)
                ? BlueSky.CrystalMaterial
                : GetOrCreateBlueCrystalFallbackMaterial(r.sharedMaterial);
            ApplyMaterialToRenderer(r, targetMat);
        }
    }
}

