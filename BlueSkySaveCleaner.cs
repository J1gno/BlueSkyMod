// ============================================================================
// BlueSkySaveCleaner.cs — Mod ID Registry & Standalone JSON Save Scrubber
// ============================================================================
// Kept completely Unity/IL2CPP-free so it can be unit-tested directly under .NET 8
// via `Tests/BlueSkySaveCleaner.Tests.csproj` without launching Schedule I.
//
// IMPORTANT FOR FORKS / CUSTOM ITEMS:
//   If you add new custom items, effects, or station recipes, register their IDs in
//   `BlueSkyIds`, `ModTokens`, `IsBaseModItemId`, `IsModEffectId`, and `IsModRecipeId`
//   below, and add a matching test case in `Tests/SaveCleanerTests.cs`.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BlueSkyMod
{
    // Central registry of vanilla IDs referenced by the mod and custom IDs added by the mod.
    internal static class BlueSkyIds
    {
        // Vanilla Schedule I item IDs used as templates/ingredients.
        public const string VanillaPseudo     = "pseudo";
        public const string VanillaLiquidMeth = "liquidmeth";
        public const string VanillaBabyBlue   = "liquidbabyblue";
        public const string VanillaAcid       = "acid";
        public const string VanillaPhosphorus = "phosphorus";

        // Custom item & effect IDs introduced by this mod.
        public const string PristinePseudo = "pseudo_pristine";
        public const string LiquidBlue     = "liquid_meth_blue";
        public const string BlueSky        = "meth_bluesky";
        public const string BlueSkyEffect  = "bluesky_purity";

        // Custom Chemistry Station recipe name & serialized RecipeID (`{qty}x{outputItemId}`).
        public const string LabRecipe   = "Recipe_LiquidBlue";
        public const string LabRecipeId = "1xliquid_meth_blue";
    }

    internal static class BlueSkySaveCleaner
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private const string LegacyOvenRecipe   = "Recipe_Cook_BlueSky";
        private const string LegacyOvenRecipeId = "10xmeth_bluesky";

        // Fast pre-filter tokens — any save JSON not containing at least one token is skipped untouched.
        private static readonly string[] ModTokens =
        {
            BlueSkyIds.PristinePseudo,
            BlueSkyIds.LiquidBlue,
            BlueSkyIds.BlueSky,
            BlueSkyIds.BlueSkyEffect,
            BlueSkyIds.LabRecipeId,
            BlueSkyIds.LabRecipe,
            LegacyOvenRecipeId,
            LegacyOvenRecipe
        };

        private static readonly string[] CreatedProductListKeys =
        {
            "CreatedWeed",
            "CreatedMeth",
            "CreatedCocaine",
            "CreatedShrooms"
        };

        public sealed class ScrubStats
        {
            public int FilesScanned;
            public int FilesModified;
            public int ItemsRemoved;
            public int StationsCleared;
            public int ContractsCleared;
            public int ProductEntriesRemoved;
            public int FiltersCleaned;
            public int DeliveriesCleaned;

            public string FormatSummary()
            {
                return $"Modified {FilesModified}/{FilesScanned} file(s): {ItemsRemoved} item slot(s), {StationsCleared} station op/config(s), {ContractsCleared} contract(s), {ProductEntriesRemoved} product record(s), {FiltersCleaned} slot filter(s), {DeliveriesCleaned} delivery record(s) cleaned.";
            }
        }

        private sealed class ScrubContext
        {
            public readonly HashSet<string> DerivedMixIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<string> PriceResetIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<string> DeletedContractGuids =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public readonly List<string> ScanTokens = new List<string>(ModTokens);
        }

        public static string CreateSaveBackup(string saveFolder, string backupRoot)
        {
            string trimmed = saveFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string saveName = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(saveName)) saveName = "SaveGame";

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupDir = Path.Combine(backupRoot, $"{saveName}_{stamp}");
            int suffix = 1;
            while (Directory.Exists(backupDir))
            {
                backupDir = Path.Combine(backupRoot, $"{saveName}_{stamp}_{suffix}");
                suffix++;
            }

            CopyDirectoryRecursive(trimmed, backupDir);
            return backupDir;
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string target = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, target, true);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string name = Path.GetFileName(subDir);
                if (string.Equals(name, "BlueSkyMod_Backups", StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyDirectoryRecursive(subDir, Path.Combine(destDir, name));
            }
        }

        public static ScrubStats ScrubSaveFolder(string saveFolder, Action<string> logWarn = null)
        {
            var stats = new ScrubStats();
            string trimmedRoot = saveFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string moddedRoot = Path.Combine(trimmedRoot, "Modded") + Path.DirectorySeparatorChar;
            string[] files = Directory.GetFiles(saveFolder, "*.json", SearchOption.AllDirectories);

            Array.Sort(files, (a, b) =>
            {
                int pa = GetFilePassOrder(a, trimmedRoot);
                int pb = GetFilePassOrder(b, trimmedRoot);
                int cmp = pa.CompareTo(pb);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });

            ScrubContext ctx = BuildScrubContext(trimmedRoot, moddedRoot, files, logWarn);

            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (path.IndexOf("BlueSkyMod_Backups", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (path.StartsWith(moddedRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                stats.FilesScanned++;

                string raw;
                try { raw = File.ReadAllText(path, Utf8NoBom); }
                catch (Exception ex)
                {
                    logWarn?.Invoke($"Could not read '{path}': {ex.Message}");
                    continue;
                }

                if (!ContainsAnyModToken(raw, ctx))
                    continue;

                JObject root;
                try { root = JObject.Parse(raw); }
                catch (Exception ex)
                {
                    logWarn?.Invoke($"Skipping non-standard JSON '{path}': {ex.Message}");
                    continue;
                }

                string rootType = root["DataType"]?.Type == JTokenType.String ? root["DataType"].Value<string>() : null;
                string rootId = root["ID"]?.Type == JTokenType.String ? root["ID"].Value<string>() : null;
                if (rootType != null && rootType.EndsWith("ProductData", StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(rootId) && IsModItemId(rootId, ctx))
                {
                    try
                    {
                        File.Delete(path);
                        stats.ProductEntriesRemoved++;
                        stats.FilesModified++;
                    }
                    catch (Exception ex)
                    {
                        logWarn?.Invoke($"Could not remove derived product file '{path}': {ex.Message}");
                    }
                    continue;
                }

                if (rootType == "ContractData" &&
                    root["ProductList"] is JObject rootPl && rootPl["entries"] is JArray rootContractEntries)
                {
                    bool removedContractEntry = false;
                    for (int j = rootContractEntries.Count - 1; j >= 0; j--)
                    {
                        if (rootContractEntries[j] is JObject entry && IsModItemId(entry["ProductID"]?.Value<string>(), ctx))
                        {
                            rootContractEntries.RemoveAt(j);
                            removedContractEntry = true;
                        }
                    }

                    if (removedContractEntry)
                    {
                        stats.ContractsCleared++;
                        if (rootContractEntries.Count == 0)
                        {
                            string guid = root["GUID"]?.Type == JTokenType.String ? root["GUID"].Value<string>() : null;
                            if (!string.IsNullOrEmpty(guid) && ctx.DeletedContractGuids.Add(guid))
                                ctx.ScanTokens.Add(guid);
                            try
                            {
                                File.Delete(path);
                                stats.FilesModified++;
                            }
                            catch (Exception ex)
                            {
                                logWarn?.Invoke($"Could not remove legacy contract file '{path}': {ex.Message}");
                            }
                            continue;
                        }

                        ScrubObject(root, stats, ctx);
                        WriteJsonAtomically(path, root, raw);
                        stats.FilesModified++;
                        continue;
                    }
                }

                bool hadCustomerOffer = rootType == "CustomerData" &&
                    root["IsContractOffered"]?.Type == JTokenType.Boolean &&
                    root["IsContractOffered"].Value<bool>();

                bool changed = ScrubObject(root, stats, ctx);
                if (!changed) continue;

                WriteJsonAtomically(path, root, raw);
                stats.FilesModified++;

                if (hadCustomerOffer &&
                    root["IsContractOffered"]?.Type == JTokenType.Boolean &&
                    !root["IsContractOffered"].Value<bool>())
                {
                    TryClearSiblingMessageResponses(Path.GetDirectoryName(path), stats, logWarn);
                }
            }

            return stats;
        }

        private static int GetFilePassOrder(string path, string trimmedRoot)
        {
            string rel = path;
            if (!string.IsNullOrEmpty(trimmedRoot) &&
                path.Length > trimmedRoot.Length &&
                path.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                rel = path.Substring(trimmedRoot.Length);
            }
            if (rel.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rel.IndexOf("Contract", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            return 1;
        }

        private static void WriteJsonAtomically(string path, JObject root, string originalRaw)
        {
            Formatting formatting = originalRaw.IndexOf('\n') >= 0 ? Formatting.Indented : Formatting.None;
            string updated = root.ToString(formatting);
            string tmpPath = path + ".bluesky.tmp";
            try
            {
                File.WriteAllText(tmpPath, updated, Utf8NoBom);
                try
                {
                    File.Replace(tmpPath, path, null);
                }
                catch (Exception)
                {
                    File.Copy(tmpPath, path, true);
                }
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch { }
            }
        }

        private static void TryClearSiblingMessageResponses(string npcDir, ScrubStats stats, Action<string> logWarn)
        {
            if (string.IsNullOrEmpty(npcDir)) return;
            string[] candidates = { "MessageConversation.json", "Messages.json" };
            for (int i = 0; i < candidates.Length; i++)
            {
                string msgPath = Path.Combine(npcDir, candidates[i]);
                if (!File.Exists(msgPath)) continue;
                try
                {
                    string raw = File.ReadAllText(msgPath, Utf8NoBom);
                    var msgObj = JObject.Parse(raw);
                    if (msgObj["ActiveResponses"] is JArray responses && responses.Count > 0)
                    {
                        responses.Clear();
                        WriteJsonAtomically(msgPath, msgObj, raw);
                        stats.FilesModified++;
                    }
                }
                catch (Exception ex)
                {
                    logWarn?.Invoke($"Could not clear legacy message responses in '{msgPath}': {ex.Message}");
                }
            }
        }

        private static ScrubContext BuildScrubContext(string saveFolder, string moddedRoot, string[] files, Action<string> logWarn)
        {
            var ctx = new ScrubContext();
            var allCreatedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var withBlueSkyEffect = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recipesByOutput = new Dictionary<string, List<(string product, string mixer)>>(StringComparer.OrdinalIgnoreCase);

            string productsPath = Path.Combine(saveFolder, "Products.json");
            if (!File.Exists(productsPath))
                productsPath = Path.Combine(saveFolder, "Products", "Products.json");
            if (File.Exists(productsPath))
            {
                try
                {
                    var prodRoot = JObject.Parse(File.ReadAllText(productsPath, Utf8NoBom));
                    for (int c = 0; c < CreatedProductListKeys.Length; c++)
                    {
                        if (!(prodRoot[CreatedProductListKeys[c]] is JArray createdList)) continue;
                        for (int i = 0; i < createdList.Count; i++)
                        {
                            if (!(createdList[i] is JObject prodObj)) continue;
                            string id = prodObj["ID"]?.Value<string>();
                            if (string.IsNullOrEmpty(id)) continue;
                            allCreatedIds.Add(id);
                            if (ArrayContainsEffectId(prodObj["Properties"] as JArray, BlueSkyIds.BlueSkyEffect))
                                withBlueSkyEffect.Add(id);
                        }
                    }

                    if (prodRoot["MixRecipes"] is JArray recipes)
                    {
                        for (int i = 0; i < recipes.Count; i++)
                        {
                            if (!(recipes[i] is JObject r)) continue;
                            string prod = r["Product"]?.Value<string>();
                            string mixer = r["Mixer"]?.Value<string>();
                            string output = r["Output"]?.Value<string>();
                            if (string.IsNullOrEmpty(output)) continue;
                            if (!recipesByOutput.TryGetValue(output, out var list))
                            {
                                list = new List<(string, string)>();
                                recipesByOutput[output] = list;
                            }
                            list.Add((prod, mixer));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logWarn?.Invoke($"Could not pre-scan Products.json for Blue Sky mixes: {ex.Message}");
                }
            }

            string legacyCreatedDir = Path.Combine(saveFolder, "Products", "CreatedProducts") + Path.DirectorySeparatorChar;
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (path.IndexOf("BlueSkyMod_Backups", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (path.StartsWith(moddedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.StartsWith(legacyCreatedDir, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var obj = JObject.Parse(File.ReadAllText(path, Utf8NoBom));
                    string id = obj["ID"]?.Value<string>();
                    if (string.IsNullOrEmpty(id)) continue;
                    allCreatedIds.Add(id);
                    if (ArrayContainsEffectId(obj["Properties"] as JArray, BlueSkyIds.BlueSkyEffect))
                        withBlueSkyEffect.Add(id);
                }
                catch { }
            }

            var touchesBlueSky = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in withBlueSkyEffect)
            {
                if (!IsBaseModItemId(id))
                    touchesBlueSky.Add(id);
            }

            bool tainted = true;
            while (tainted)
            {
                tainted = false;
                foreach (var kv in recipesByOutput)
                {
                    string outputId = kv.Key;
                    if (!allCreatedIds.Contains(outputId) || IsBaseModItemId(outputId) || touchesBlueSky.Contains(outputId))
                        continue;

                    var list = kv.Value;
                    for (int i = 0; i < list.Count; i++)
                    {
                        string p = list[i].product;
                        string m = list[i].mixer;
                        if (IsBaseModItemId(p) || IsBaseModItemId(m) ||
                            (!string.IsNullOrEmpty(p) && touchesBlueSky.Contains(p)) ||
                            (!string.IsNullOrEmpty(m) && touchesBlueSky.Contains(m)))
                        {
                            touchesBlueSky.Add(outputId);
                            tainted = true;
                            break;
                        }
                    }
                }
            }

            var validNonModCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in allCreatedIds)
            {
                if (!IsBaseModItemId(id) && !touchesBlueSky.Contains(id))
                    validNonModCreated.Add(id);
            }

            bool IsValidWithoutMod(string id)
            {
                if (string.IsNullOrEmpty(id)) return true;
                if (IsBaseModItemId(id)) return false;
                if (!allCreatedIds.Contains(id)) return true;
                return validNonModCreated.Contains(id);
            }

            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (string outputId in touchesBlueSky)
                {
                    if (validNonModCreated.Contains(outputId)) continue;
                    if (!recipesByOutput.TryGetValue(outputId, out var list)) continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        if (IsValidWithoutMod(list[i].product) && IsValidWithoutMod(list[i].mixer))
                        {
                            validNonModCreated.Add(outputId);
                            grew = true;
                            break;
                        }
                    }
                }
            }

            foreach (string id in touchesBlueSky)
            {
                if (!validNonModCreated.Contains(id))
                    ctx.DerivedMixIds.Add(id);
                else if (withBlueSkyEffect.Contains(id))
                    ctx.PriceResetIds.Add(id);
            }

            foreach (string id in ctx.DerivedMixIds)
                ctx.ScanTokens.Add(id);
            foreach (string id in ctx.PriceResetIds)
                ctx.ScanTokens.Add(id);

            return ctx;
        }

        private static bool ArrayContainsEffectId(JArray propsArr, string effectId)
        {
            if (propsArr == null) return false;
            for (int i = 0; i < propsArr.Count; i++)
            {
                if (propsArr[i].Type == JTokenType.String &&
                    string.Equals(propsArr[i].Value<string>(), effectId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ContainsAnyModToken(string text, ScrubContext ctx)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var tokens = ctx.ScanTokens;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (text.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool HasStringElement(JArray arr)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i].Type == JTokenType.String)
                    return true;
            }
            return false;
        }

        // Update these four predicates whenever new custom items, effects, or recipes are added.
        private static bool IsBaseModItemId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return string.Equals(id, BlueSkyIds.PristinePseudo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, BlueSkyIds.LiquidBlue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, BlueSkyIds.BlueSky, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModItemId(string id, ScrubContext ctx)
        {
            if (IsBaseModItemId(id)) return true;
            return ctx != null && !string.IsNullOrEmpty(id) && ctx.DerivedMixIds.Contains(id);
        }

        private static bool IsModEffectId(string id)
        {
            return !string.IsNullOrEmpty(id) && string.Equals(id, BlueSkyIds.BlueSkyEffect, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModRecipeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return string.Equals(id, BlueSkyIds.LabRecipeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, BlueSkyIds.LabRecipe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, LegacyOvenRecipeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, LegacyOvenRecipe, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ScrubObject(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            if (obj == null) return false;
            bool changed = false;

            string dataType = obj["DataType"]?.Type == JTokenType.String ? obj["DataType"].Value<string>() : null;

            if (dataType == "ProductManagerData")
                changed |= ScrubProductManager(obj, stats, ctx);
            else if (dataType == "ChemistryStationData")
                changed |= ScrubChemistryStation(obj, stats);
            else if (dataType == "ChemistryStationConfigurationData")
                changed |= ScrubChemistryStationConfig(obj, stats);
            else if (dataType == "LabOvenData")
                changed |= ScrubLabOven(obj, stats, ctx);
            else if (dataType == "MixingStationData")
                changed |= ScrubMixingStation(obj, stats, ctx);
            else if (dataType == "CustomerData")
                changed |= ScrubCustomerData(obj, stats, ctx);
            else if (dataType == "QuestManagerData")
                changed |= ScrubQuestManager(obj, stats, ctx);
            else if (dataType == "DeliveriesData")
                changed |= ScrubDeliveries(obj, stats, ctx);
            else if (dataType == "NPCCollectionData" && obj["NPCs"] is JArray npcsArr)
                changed |= ScrubNpcCollection(npcsArr, stats, ctx);
            else if (dataType == "ShopData")
                changed |= ScrubShopData(obj, stats, ctx);
            else if (dataType == "CartelData")
                changed |= ScrubCartelData(obj, stats, ctx);
            else if (dataType == "SpecialCustomerSaveData")
                changed |= ScrubProductProperties(obj["DesiredEffects"] as JArray, stats);
            else if (dataType != null && dataType.EndsWith("ProductData", StringComparison.Ordinal))
                changed |= ScrubProductProperties(obj["Properties"] as JArray, stats);

            bool scrubbedItems = false;
            if (obj["Items"] is JArray itemsArr && HasStringElement(itemsArr))
            {
                scrubbedItems = true;
                changed |= ScrubItemSetStrings(itemsArr, stats, ctx);
            }

            if (obj["SlotFilters"] is JArray filtersArr)
                changed |= ScrubSlotFilters(filtersArr, stats, ctx);

            if (obj["FilterItemIDs"] is JArray routeFilterIds)
                changed |= RemoveModStringsFromArray(routeFilterIds, ref stats.FiltersCleaned, ctx);

            if (ctx.DeletedContractGuids.Count > 0 && obj["ActiveContractGUIDs"] is JArray activeContractGuids)
                changed |= RemoveStringsInSet(activeContractGuids, ctx.DeletedContractGuids, ref stats.ContractsCleared);

            if (obj["MoveItemData"] is JObject moveItem)
                changed |= ScrubMoveItemData(moveItem, stats, ctx);

            if (obj["deaddropItems"] is JArray deadDropItems &&
                RemoveModStringIntPairs(deadDropItems, ref stats.DeliveriesCleaned, ctx, false))
            {
                changed = true;
                if (deadDropItems.Count == 0 && obj["minsUntilDeadDropReady"] != null)
                    obj["minsUntilDeadDropReady"] = -1;
            }

            foreach (var prop in obj.Properties())
            {
                if (prop.Name == "Text" || (prop.Name == "NPCs" && dataType == "NPCCollectionData"))
                    continue;

                if (prop.Value is JObject childObj)
                {
                    changed |= ScrubObject(childObj, stats, ctx);
                }
                else if (prop.Value is JArray childArr)
                {
                    if (scrubbedItems && prop.Name == "Items")
                        continue;
                    changed |= ScrubArray(childArr, stats, ctx);
                }
                else if (prop.Value.Type == JTokenType.String)
                {
                    string str = prop.Value.Value<string>();
                    if (TryScrubEmbeddedJsonString(str, stats, ctx, out string updated))
                    {
                        prop.Value = updated;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool ScrubArray(JArray arr, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;
            for (int i = 0; i < arr.Count; i++)
            {
                var elem = arr[i];
                if (elem is JObject childObj)
                {
                    changed |= ScrubObject(childObj, stats, ctx);
                }
                else if (elem is JArray childArr)
                {
                    changed |= ScrubArray(childArr, stats, ctx);
                }
                else if (elem.Type == JTokenType.String)
                {
                    string str = elem.Value<string>();
                    if (TryScrubEmbeddedJsonString(str, stats, ctx, out string updated))
                    {
                        arr[i] = updated;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        private static bool TryScrubEmbeddedJsonString(string str, ScrubStats stats, ScrubContext ctx, out string updated)
        {
            return TryScrubEmbeddedJsonString(str, stats, ctx, out updated, out _);
        }

        private static bool TryScrubEmbeddedJsonString(string str, ScrubStats stats, ScrubContext ctx, out string updated, out JObject parsed)
        {
            updated = null;
            parsed = null;
            if (string.IsNullOrEmpty(str) || !ContainsAnyModToken(str, ctx))
                return false;

            string trimmed = str.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return false;

            JObject nested;
            try { nested = JObject.Parse(str); }
            catch { return false; }

            if (!ScrubObject(nested, stats, ctx))
                return false;

            Formatting fmt = str.IndexOf('\n') >= 0 ? Formatting.Indented : Formatting.None;
            updated = nested.ToString(fmt);
            parsed = nested;
            return true;
        }

        private static bool ScrubProductProperties(JArray propsArr, ScrubStats stats)
        {
            if (propsArr == null) return false;
            bool changed = false;
            for (int j = propsArr.Count - 1; j >= 0; j--)
            {
                if (propsArr[j].Type == JTokenType.String && IsModEffectId(propsArr[j].Value<string>()))
                {
                    propsArr.RemoveAt(j);
                    stats.ProductEntriesRemoved++;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool ScrubProductManager(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;

            for (int c = 0; c < CreatedProductListKeys.Length; c++)
            {
                if (!(obj[CreatedProductListKeys[c]] is JArray createdList)) continue;
                for (int i = createdList.Count - 1; i >= 0; i--)
                {
                    if (!(createdList[i] is JObject createdProd)) continue;
                    string prodId = createdProd["ID"]?.Value<string>();
                    if (IsModItemId(prodId, ctx))
                    {
                        createdList.RemoveAt(i);
                        stats.ProductEntriesRemoved++;
                        changed = true;
                    }
                    else if (ScrubProductProperties(createdProd["Properties"] as JArray, stats))
                    {
                        changed = true;
                        if (!string.IsNullOrEmpty(prodId))
                            ctx.PriceResetIds.Add(prodId);
                    }
                }
            }

            changed |= RemoveModStringsFromArray(obj["DiscoveredProducts"] as JArray, ref stats.ProductEntriesRemoved, ctx);
            changed |= RemoveModStringsFromArray(obj["ListedProducts"] as JArray, ref stats.ProductEntriesRemoved, ctx);
            changed |= RemoveModStringsFromArray(obj["FavouritedProducts"] as JArray, ref stats.ProductEntriesRemoved, ctx);

            changed |= RemoveModStringIntPairs(obj["ProductPrices"] as JArray, ref stats.ProductEntriesRemoved, ctx, true);

            if (obj["MixRecipes"] is JArray recipes)
            {
                for (int i = recipes.Count - 1; i >= 0; i--)
                {
                    if (recipes[i] is JObject r &&
                        (IsModItemId(r["Product"]?.Value<string>(), ctx) ||
                         IsModItemId(r["Mixer"]?.Value<string>(), ctx) ||
                         IsModItemId(r["Output"]?.Value<string>(), ctx)))
                    {
                        recipes.RemoveAt(i);
                        stats.ProductEntriesRemoved++;
                        changed = true;
                    }
                }
            }

            if (obj["ActiveMixOperation"] is JObject activeMix &&
                (IsModItemId(activeMix["ProductID"]?.Value<string>(), ctx) ||
                 IsModItemId(activeMix["IngredientID"]?.Value<string>(), ctx)))
            {
                activeMix["ProductID"] = "";
                activeMix["IngredientID"] = "";
                obj["IsMixComplete"] = false;
                stats.ProductEntriesRemoved++;
                changed = true;
            }

            if (obj["ContractReceipts"] is JArray receipts)
            {
                for (int i = receipts.Count - 1; i >= 0; i--)
                {
                    if (receipts[i] is JObject rec && rec["Items"] is JArray recItems &&
                        RemoveModStringIntPairs(recItems, ref stats.ProductEntriesRemoved, ctx, false))
                    {
                        changed = true;
                        if (recItems.Count == 0)
                            receipts.RemoveAt(i);
                    }
                }
            }

            return changed;
        }

        private static bool RemoveModStringIntPairs(JArray arr, ref int counter, ScrubContext ctx, bool includePriceResets)
        {
            if (arr == null) return false;
            bool changed = false;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JObject pair)
                {
                    string key = pair["String"]?.Value<string>();
                    if (IsModItemId(key, ctx) || (includePriceResets && ctx != null && !string.IsNullOrEmpty(key) && ctx.PriceResetIds.Contains(key)))
                    {
                        arr.RemoveAt(i);
                        counter++;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        private static bool RemoveModStringsFromArray(JArray arr, ref int counter, ScrubContext ctx)
        {
            if (arr == null) return false;
            bool changed = false;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i].Type == JTokenType.String && IsModItemId(arr[i].Value<string>(), ctx))
                {
                    arr.RemoveAt(i);
                    counter++;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool RemoveStringsInSet(JArray arr, HashSet<string> targetSet, ref int counter)
        {
            if (arr == null || targetSet.Count == 0) return false;
            bool changed = false;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i].Type == JTokenType.String && targetSet.Contains(arr[i].Value<string>()))
                {
                    arr.RemoveAt(i);
                    counter++;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool ScrubChemistryStation(JObject obj, ScrubStats stats)
        {
            string recipeId = obj["CurrentRecipeID"]?.Value<string>();
            if (!IsModRecipeId(recipeId)) return false;

            obj["CurrentRecipeID"] = "";
            obj["LiquidLevel"] = 0.0;
            obj["CurrentTime"] = 0;
            stats.StationsCleared++;
            return true;
        }

        private static bool ScrubChemistryStationConfig(JObject obj, ScrubStats stats)
        {
            if (obj["Recipe"] is JObject recipeField && IsModRecipeId(recipeField["RecipeID"]?.Value<string>()))
            {
                recipeField["RecipeID"] = "";
                stats.StationsCleared++;
                return true;
            }
            return false;
        }

        private static bool ScrubLabOven(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            string ingId = obj["CurrentIngredientID"]?.Value<string>();
            string prodId = obj["CurrentProductID"]?.Value<string>();
            if (!IsModItemId(ingId, ctx) && !IsModItemId(prodId, ctx)) return false;

            obj["CurrentIngredientID"] = "";
            obj["CurrentIngredientQuantity"] = 0;
            obj["CurrentProductID"] = "";
            obj["CurrentCookProgress"] = 0;
            stats.StationsCleared++;
            return true;
        }

        private static bool ScrubMixingStation(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            if (obj["CurrentMixOperation"] is JObject mixOp &&
                (IsModItemId(mixOp["ProductID"]?.Value<string>(), ctx) ||
                 IsModItemId(mixOp["IngredientID"]?.Value<string>(), ctx)))
            {
                mixOp["ProductID"] = "";
                mixOp["ProductQuality"] = 0;
                mixOp["IngredientID"] = "";
                mixOp["Quantity"] = 0;
                obj["CurrentMixTime"] = 0;
                stats.StationsCleared++;
                return true;
            }
            return false;
        }

        private static bool ScrubCustomerData(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            if (!(obj["OfferedContract"] is JObject offered)) return false;
            if (!(offered["Products"] is JObject prodList) || !(prodList["entries"] is JArray entries)) return false;

            bool removedAny = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] is JObject entry && IsModItemId(entry["ProductID"]?.Value<string>(), ctx))
                {
                    entries.RemoveAt(i);
                    removedAny = true;
                }
            }

            if (!removedAny) return false;

            if (entries.Count == 0)
            {
                obj["IsContractOffered"] = false;
                offered["Payment"] = 0.0;
                offered["DeliveryLocationGUID"] = "";
                if (offered["DeliveryWindow"] is JObject win)
                    win["IsEnabled"] = false;
                offered["Expires"] = false;
                offered["ExpiresAfter"] = 0;
                if (obj["OfferedContractTime"] is JObject ct)
                {
                    ct["elapsedDays"] = 0;
                    ct["time"] = 0;
                }
            }

            stats.ContractsCleared++;
            return true;
        }

        private static bool ScrubNpcCollection(JArray npcsArr, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;
            for (int i = 0; i < npcsArr.Count; i++)
            {
                if (!(npcsArr[i] is JObject npcObj)) continue;

                bool clearedCustomerContract = false;
                if (npcObj["AdditionalDatas"] is JArray addDatas)
                {
                    for (int j = 0; j < addDatas.Count; j++)
                    {
                        if (!(addDatas[j] is JObject entry)) continue;
                        string name = entry["Name"]?.Value<string>();
                        string contents = entry["Contents"]?.Value<string>();
                        if (string.IsNullOrEmpty(contents) || !ContainsAnyModToken(contents, ctx)) continue;

                        bool hadActiveOffer = false;
                        if (name == "CustomerData")
                        {
                            try
                            {
                                var beforeObj = JObject.Parse(contents);
                                hadActiveOffer = beforeObj["IsContractOffered"]?.Type == JTokenType.Boolean &&
                                                 beforeObj["IsContractOffered"].Value<bool>();
                            }
                            catch { }
                        }

                        if (TryScrubEmbeddedJsonString(contents, stats, ctx, out string updated, out JObject updatedObj))
                        {
                            entry["Contents"] = updated;
                            changed = true;
                            if (hadActiveOffer &&
                                updatedObj != null &&
                                updatedObj["IsContractOffered"]?.Type == JTokenType.Boolean &&
                                !updatedObj["IsContractOffered"].Value<bool>())
                            {
                                clearedCustomerContract = true;
                            }
                        }
                    }

                    if (clearedCustomerContract)
                    {
                        for (int j = 0; j < addDatas.Count; j++)
                        {
                            if (!(addDatas[j] is JObject entry)) continue;
                            if (entry["Name"]?.Value<string>() != "MessageConversation") continue;
                            string msgContents = entry["Contents"]?.Value<string>();
                            if (string.IsNullOrEmpty(msgContents)) continue;
                            try
                            {
                                var msgObj = JObject.Parse(msgContents);
                                if (msgObj["ActiveResponses"] is JArray responses && responses.Count > 0)
                                {
                                    responses.Clear();
                                    Formatting fmt = msgContents.IndexOf('\n') >= 0 ? Formatting.Indented : Formatting.None;
                                    entry["Contents"] = msgObj.ToString(fmt);
                                    changed = true;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (npcObj["BaseData"]?.Type == JTokenType.String)
                {
                    string baseData = npcObj["BaseData"].Value<string>();
                    if (TryScrubEmbeddedJsonString(baseData, stats, ctx, out string updated))
                    {
                        npcObj["BaseData"] = updated;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        private static bool ScrubQuestManager(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;
            changed |= ScrubContractsArray(obj["Contracts"] as JArray, stats, ctx);
            changed |= ScrubContractsArray(obj["Quests"] as JArray, stats, ctx);
            return changed;
        }

        private static bool ScrubContractsArray(JArray arr, ScrubStats stats, ScrubContext ctx)
        {
            if (arr == null) return false;
            bool changed = false;

            for (int i = arr.Count - 1; i >= 0; i--)
            {
                bool isString = arr[i].Type == JTokenType.String;
                JObject qObj = arr[i] as JObject;
                string rawStr = null;

                if (isString)
                {
                    rawStr = arr[i].Value<string>();
                    if (!ContainsAnyModToken(rawStr, ctx)) continue;
                    try { qObj = JObject.Parse(rawStr); }
                    catch { continue; }
                }

                if (qObj == null) continue;
                if (!(qObj["ProductList"] is JObject pl) || !(pl["entries"] is JArray entries)) continue;

                bool removedAny = false;
                for (int j = entries.Count - 1; j >= 0; j--)
                {
                    if (entries[j] is JObject entry && IsModItemId(entry["ProductID"]?.Value<string>(), ctx))
                    {
                        entries.RemoveAt(j);
                        removedAny = true;
                    }
                }

                if (!removedAny) continue;

                stats.ContractsCleared++;
                changed = true;

                if (entries.Count == 0)
                {
                    string guid = qObj["GUID"]?.Type == JTokenType.String ? qObj["GUID"].Value<string>() : null;
                    if (!string.IsNullOrEmpty(guid) && ctx.DeletedContractGuids.Add(guid))
                        ctx.ScanTokens.Add(guid);
                    arr.RemoveAt(i);
                }
                else if (isString)
                {
                    Formatting fmt = rawStr.IndexOf('\n') >= 0 ? Formatting.Indented : Formatting.None;
                    arr[i] = qObj.ToString(fmt);
                }
            }

            return changed;
        }

        private static bool ScrubDeliveries(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;
            changed |= ScrubDeliveryList(obj["ActiveDeliveries"] as JArray, stats, ctx);
            changed |= ScrubDeliveryList(obj["DeliveryHistory"] as JArray, stats, ctx);
            changed |= ScrubDeliveryList(obj["DisplayedDeliveryHistory"] as JArray, stats, ctx);
            return changed;
        }

        private static bool ScrubDeliveryList(JArray list, ScrubStats stats, ScrubContext ctx)
        {
            if (list == null) return false;
            bool changed = false;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is JObject del && del["Items"] is JArray delItems &&
                    RemoveModStringIntPairs(delItems, ref stats.DeliveriesCleaned, ctx, false))
                {
                    changed = true;
                    if (delItems.Count == 0)
                        list.RemoveAt(i);
                }
            }

            return changed;
        }

        private static bool ScrubItemSetStrings(JArray itemsArr, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;
            for (int i = 0; i < itemsArr.Count; i++)
            {
                if (itemsArr[i].Type != JTokenType.String) continue;
                string raw = itemsArr[i].Value<string>();
                if (!ContainsAnyModToken(raw, ctx)) continue;

                JObject itemObj;
                try { itemObj = JObject.Parse(raw); }
                catch { continue; }

                if (!IsModItemId(itemObj["ID"]?.Value<string>(), ctx)) continue;

                string gameVer = itemObj["GameVersion"]?.Value<string>() ?? "0.4.7f12";
                var emptySlot = new JObject
                {
                    ["DataType"] = "ItemData",
                    ["DataVersion"] = 0,
                    ["GameVersion"] = gameVer,
                    ["ID"] = "",
                    ["Quantity"] = 0
                };
                itemsArr[i] = emptySlot.ToString(Formatting.None);
                stats.ItemsRemoved++;
                changed = true;
            }
            return changed;
        }

        private static bool ScrubShopData(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            return RemoveModStringIntPairs(obj["ItemStockQuantities"] as JArray, ref stats.ProductEntriesRemoved, ctx, false);
        }

        private static bool ScrubCartelData(JObject obj, ScrubStats stats, ScrubContext ctx)
        {
            if (!(obj["ActiveCartelDeal"] is JObject deal)) return false;
            if (!IsModItemId(deal["RequestedProductID"]?.Value<string>(), ctx)) return false;

            deal["RequestedProductID"] = "";
            deal["RequestedProductQuantity"] = 0;
            deal["PaymentAmount"] = 0;
            deal["Status"] = 0;
            if (deal["DueTime"] is JObject due)
            {
                due["elapsedDays"] = 0;
                due["time"] = 0;
            }
            stats.ContractsCleared++;
            return true;
        }

        private static bool ScrubMoveItemData(JObject moveItem, ScrubStats stats, ScrubContext ctx)
        {
            string raw = moveItem["TemplateItemJSON"]?.Type == JTokenType.String
                ? moveItem["TemplateItemJSON"].Value<string>()
                : null;
            if (string.IsNullOrEmpty(raw) || !ContainsAnyModToken(raw, ctx)) return false;

            JObject itemObj;
            try { itemObj = JObject.Parse(raw); }
            catch { return false; }

            if (!IsModItemId(itemObj["ID"]?.Value<string>(), ctx)) return false;

            moveItem["TemplateItemJSON"] = "";
            moveItem["GrabbedItemQuantity"] = 0;
            if (moveItem["SourceGUID"] != null) moveItem["SourceGUID"] = "";
            if (moveItem["DestinationGUID"] != null) moveItem["DestinationGUID"] = "";
            stats.ItemsRemoved++;
            return true;
        }

        private static bool ScrubSlotFilters(JArray filtersArr, ScrubStats stats, ScrubContext ctx)
        {
            bool changed = false;
            for (int i = 0; i < filtersArr.Count; i++)
            {
                if (filtersArr[i] is JObject filter && filter["ItemIDs"] is JArray itemIds)
                {
                    if (RemoveModStringsFromArray(itemIds, ref stats.FiltersCleaned, ctx))
                    {
                        changed = true;
                        if (itemIds.Count == 0 &&
                            filter["Type"] != null &&
                            filter["Type"].Type == JTokenType.Integer &&
                            filter["Type"].Value<int>() != 0)
                        {
                            filter["Type"] = 0;
                        }
                    }
                }
            }
            return changed;
        }
    }
}

