// ============================================================================
// SaveCleanerTests.cs — Standalone Regression Suite for BlueSkySaveCleaner
// ============================================================================
// Run anytime with: dotnet run --project Tests/BlueSkySaveCleaner.Tests.csproj -c Release
// Add new test cases whenever you add new custom items, recipes, or
// save-format cleanups in BlueSkySaveCleaner.cs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlueSkyMod;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class SaveCleanerTests
{
    private static int _passed;
    private static int _failed;

    public static int Main()
    {
        Run("DerivedMixesAndPriceResetChain", TestDerivedMixesAndPriceResetChain);
        Run("ContractsAndDealerGuidCleanup", TestContractsAndDealerGuidCleanup);
        Run("LegacyStandaloneCustomerAndMessages", TestLegacyStandaloneCustomerAndMessages);
        Run("StationsInventoriesCartelDeliveriesAndBackup", TestStationsInventoriesCartelDeliveriesAndBackup);
        Run("PreservesOtherModsVanillaCyclesAndUnrelatedChats", TestPreservesOtherModsVanillaCyclesAndUnrelatedChats);
        Run("SlotFiltersLegacyContractsAndSpecialCustomers", TestSlotFiltersLegacyContractsAndSpecialCustomers);

        Console.WriteLine($"SaveCleanerTests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  PASS: {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.Error.WriteLine($"  FAIL: {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "BlueSkyCleanerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteJson(string path, JObject obj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, obj.ToString(Formatting.Indented));
    }

    private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path));

    private static void TestDerivedMixesAndPriceResetChain()
    {
        string saveDir = CreateTempDir();
        try
        {
            WriteJson(Path.Combine(saveDir, "Products.json"), new JObject
            {
                ["DataType"] = "ProductManagerData",
                ["CreatedWeed"] = new JArray
                {
                    new JObject { ["ID"] = "superkush", ["Properties"] = new JArray("energizing") }
                },
                ["CreatedMeth"] = new JArray
                {
                    new JObject { ["ID"] = "bluecuke", ["Properties"] = new JArray("bluesky_purity", "energizing") },
                    new JObject { ["ID"] = "bluebanana", ["Properties"] = new JArray("bluesky_purity", "energizing", "gingeritis") },

                    new JObject { ["ID"] = "hybrid_x", ["Properties"] = new JArray("bluesky_purity", "gingeritis") },
                    new JObject { ["ID"] = "hybrid_y", ["Properties"] = new JArray("bluesky_purity", "gingeritis", "caloriedense") }
                },
                ["DiscoveredProducts"] = new JArray("meth_bluesky", "bluecuke", "bluebanana", "hybrid_x", "hybrid_y", "superkush"),
                ["ListedProducts"] = new JArray("meth_bluesky", "bluecuke", "superkush"),
                ["FavouritedProducts"] = new JArray("meth_bluesky", "hybrid_x"),
                ["ProductPrices"] = new JArray
                {
                    new JObject { ["String"] = "meth_bluesky", ["Int"] = 450 },
                    new JObject { ["String"] = "bluecuke", ["Int"] = 534 },
                    new JObject { ["String"] = "hybrid_x", ["Int"] = 600 },
                    new JObject { ["String"] = "hybrid_y", ["Int"] = 700 },
                    new JObject { ["String"] = "superkush", ["Int"] = 80 }
                },
                ["MixRecipes"] = new JArray
                {
                    new JObject { ["Product"] = "ogkush", ["Mixer"] = "cuke", ["Output"] = "superkush" },
                    new JObject { ["Product"] = "meth_bluesky", ["Mixer"] = "cuke", ["Output"] = "bluecuke" },
                    new JObject { ["Product"] = "bluecuke", ["Mixer"] = "banana", ["Output"] = "bluebanana" },
                    new JObject { ["Product"] = "meth_bluesky", ["Mixer"] = "banana", ["Output"] = "hybrid_x" },
                    new JObject { ["Product"] = "meth", ["Mixer"] = "banana", ["Output"] = "hybrid_x" },
                    new JObject { ["Product"] = "hybrid_x", ["Mixer"] = "donut", ["Output"] = "hybrid_y" }
                },
                ["ActiveMixOperation"] = new JObject { ["ProductID"] = "bluecuke", ["IngredientID"] = "cuke" },
                ["IsMixComplete"] = true
            });

            string legacyMixPath = Path.Combine(saveDir, "Products", "CreatedProducts", "bluecuke.json");
            WriteJson(legacyMixPath, new JObject
            {
                ["DataType"] = "MethProductData",
                ["ID"] = "bluecuke",
                ["Properties"] = new JArray("bluesky_purity", "energizing")
            });

            var warnings = new List<string>();
            var stats = BlueSkySaveCleaner.ScrubSaveFolder(saveDir, warnings.Add);

            Assert(warnings.Count == 0, "Unexpected warnings: " + string.Join("; ", warnings));
            Assert(!File.Exists(legacyMixPath), "Legacy derived product file was not deleted.");

            var prods = ReadJson(Path.Combine(saveDir, "Products.json"));
            var methIds = ((JArray)prods["CreatedMeth"]).Select(x => x["ID"].Value<string>()).ToList();
            Assert(!methIds.Contains("bluecuke") && !methIds.Contains("bluebanana"), "Derived Blue Sky mixes were not removed.");
            Assert(methIds.Contains("hybrid_x") && methIds.Contains("hybrid_y"), "Hybrid parent or downstream child mix was wrongly deleted.");

            foreach (var obj in (JArray)prods["CreatedMeth"])
            {
                var props = ((JArray)obj["Properties"]).Select(p => p.Value<string>()).ToList();
                Assert(!props.Contains("bluesky_purity"), $"bluesky_purity left on {obj["ID"]}");
            }

            var priceKeys = ((JArray)prods["ProductPrices"]).Select(p => p["String"].Value<string>()).ToList();
            Assert(priceKeys.SequenceEqual(new[] { "superkush" }), "Unexpected ProductPrices left: " + string.Join(",", priceKeys));

            var recipeOutputs = ((JArray)prods["MixRecipes"]).Select(r => $"{r["Product"]}+{r["Mixer"]}->{r["Output"]}").ToList();
            Assert(recipeOutputs.Contains("ogkush+cuke->superkush"), "Vanilla recipe removed.");
            Assert(recipeOutputs.Contains("meth+banana->hybrid_x"), "Non-mod hybrid recipe removed.");
            Assert(recipeOutputs.Contains("hybrid_x+donut->hybrid_y"), "Downstream child hybrid recipe removed.");
            Assert(!recipeOutputs.Any(s => s.Contains("meth_bluesky") || s.Contains("bluecuke")), "Mod recipe remained.");

            Assert(prods["ActiveMixOperation"]["ProductID"].Value<string>() == "", "ActiveMixOperation not cleared.");
            Assert(prods["IsMixComplete"].Value<bool>() == false, "IsMixComplete not reset.");
            Assert(Directory.GetFiles(saveDir, "*.bluesky.tmp", SearchOption.AllDirectories).Length == 0, "Leftover .tmp file found.");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }
    }

    private static void TestContractsAndDealerGuidCleanup()
    {
        string saveDir = CreateTempDir();
        try
        {
            const string blueContractGuid = "11111111-2222-3333-4444-555555555555";
            const string vanillaContractGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

            WriteJson(Path.Combine(saveDir, "Quests.json"), new JObject
            {
                ["DataType"] = "QuestManagerData",
                ["Contracts"] = new JArray
                {
                    new JObject
                    {
                        ["GUID"] = blueContractGuid,
                        ["ProductList"] = new JObject
                        {
                            ["entries"] = new JArray
                            {
                                new JObject { ["ProductID"] = "meth_bluesky", ["Quantity"] = 3 }
                            }
                        }
                    },
                    new JObject
                    {
                        ["GUID"] = vanillaContractGuid,
                        ["ProductList"] = new JObject
                        {
                            ["entries"] = new JArray
                            {
                                new JObject { ["ProductID"] = "ogkush", ["Quantity"] = 2 }
                            }
                        }
                    }
                }
            });

            var dealerBase = new JObject
            {
                ["DataType"] = "DealerData",
                ["ActiveContractGUIDs"] = new JArray(blueContractGuid, vanillaContractGuid)
            };
            var customerData = new JObject
            {
                ["DataType"] = "CustomerData",
                ["IsContractOffered"] = true,
                ["OfferedContract"] = new JObject
                {
                    ["Payment"] = 900.0,
                    ["DeliveryLocationGUID"] = "loc-1",
                    ["Products"] = new JObject
                    {
                        ["entries"] = new JArray { new JObject { ["ProductID"] = "meth_bluesky", ["Quantity"] = 2 } }
                    }
                }
            };
            var msgConv = new JObject
            {
                ["DataType"] = "MSGConversationData",
                ["ActiveResponses"] = new JArray { new JObject { ["Text"] = "Accept" } }
            };

            WriteJson(Path.Combine(saveDir, "NPCs.json"), new JObject
            {
                ["DataType"] = "NPCCollectionData",
                ["NPCs"] = new JArray
                {
                    new JObject
                    {
                        ["BaseData"] = dealerBase.ToString(Formatting.None),
                        ["AdditionalDatas"] = new JArray()
                    },
                    new JObject
                    {
                        ["BaseData"] = "{}",
                        ["AdditionalDatas"] = new JArray
                        {
                            new JObject { ["Name"] = "CustomerData", ["Contents"] = customerData.ToString(Formatting.None) },
                            new JObject { ["Name"] = "MessageConversation", ["Contents"] = msgConv.ToString(Formatting.None) }
                        }
                    }
                }
            });

            BlueSkySaveCleaner.ScrubSaveFolder(saveDir);

            var quests = ReadJson(Path.Combine(saveDir, "Quests.json"));
            var remainingContracts = (JArray)quests["Contracts"];
            Assert(remainingContracts.Count == 1 && remainingContracts[0]["GUID"].Value<string>() == vanillaContractGuid, "Blue Sky contract not removed cleanly.");

            var npcs = ReadJson(Path.Combine(saveDir, "NPCs.json"));
            var scrubbedDealer = JObject.Parse(npcs["NPCs"][0]["BaseData"].Value<string>());
            var dealerGuids = ((JArray)scrubbedDealer["ActiveContractGUIDs"]).Select(g => g.Value<string>()).ToList();
            Assert(dealerGuids.SequenceEqual(new[] { vanillaContractGuid }), "Dealer ActiveContractGUIDs still contains deleted contract GUID.");

            var scrubbedCustomer = JObject.Parse(npcs["NPCs"][1]["AdditionalDatas"][0]["Contents"].Value<string>());
            Assert(scrubbedCustomer["IsContractOffered"].Value<bool>() == false, "Customer offered contract not cleared.");
            var scrubbedMsg = JObject.Parse(npcs["NPCs"][1]["AdditionalDatas"][1]["Contents"].Value<string>());
            Assert(((JArray)scrubbedMsg["ActiveResponses"]).Count == 0, "Customer MessageConversation ActiveResponses not cleared.");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }
    }

    private static void TestLegacyStandaloneCustomerAndMessages()
    {
        string saveDir = CreateTempDir();
        try
        {
            string npcDir = Path.Combine(saveDir, "NPCs", "Kyle");
            WriteJson(Path.Combine(npcDir, "CustomerData.json"), new JObject
            {
                ["DataType"] = "CustomerData",
                ["IsContractOffered"] = true,
                ["OfferedContract"] = new JObject
                {
                    ["Payment"] = 450.0,
                    ["DeliveryLocationGUID"] = "loc-2",
                    ["Products"] = new JObject
                    {
                        ["entries"] = new JArray { new JObject { ["ProductID"] = "meth_bluesky", ["Quantity"] = 1 } }
                    }
                }
            });
            WriteJson(Path.Combine(npcDir, "MessageConversation.json"), new JObject
            {
                ["DataType"] = "MSGConversationData",
                ["ActiveResponses"] = new JArray { new JObject { ["Text"] = "Accept Deal" } }
            });

            BlueSkySaveCleaner.ScrubSaveFolder(saveDir);

            var cust = ReadJson(Path.Combine(npcDir, "CustomerData.json"));
            var msg = ReadJson(Path.Combine(npcDir, "MessageConversation.json"));
            Assert(cust["IsContractOffered"].Value<bool>() == false, "Legacy CustomerData offer not cleared.");
            Assert(((JArray)msg["ActiveResponses"]).Count == 0, "Legacy sibling MessageConversation.json ActiveResponses not cleared.");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }
    }

    private static void TestStationsInventoriesCartelDeliveriesAndBackup()
    {
        string saveDir = CreateTempDir();
        string backupRoot = CreateTempDir();
        try
        {
            var blueItemJson = new JObject { ["DataType"] = "MethData", ["GameVersion"] = "0.4.7f12", ["ID"] = "meth_bluesky", ["Quantity"] = 5 }.ToString(Formatting.None);
            var vanillaItemJson = new JObject { ["DataType"] = "ItemData", ["GameVersion"] = "0.4.7f12", ["ID"] = "acid", ["Quantity"] = 10 }.ToString(Formatting.None);

            WriteJson(Path.Combine(saveDir, "ChemStation.json"), new JObject
            {
                ["DataType"] = "ChemistryStationData",
                ["CurrentRecipeID"] = "1xliquid_meth_blue",
                ["LiquidLevel"] = 0.75,
                ["CurrentTime"] = 120,
                ["OutputContents"] = new JObject { ["Items"] = new JArray(blueItemJson, vanillaItemJson) }
            });
            WriteJson(Path.Combine(saveDir, "ChemConfig.json"), new JObject
            {
                ["DataType"] = "ChemistryStationConfigurationData",
                ["Recipe"] = new JObject { ["RecipeID"] = "1xliquid_meth_blue" }
            });
            WriteJson(Path.Combine(saveDir, "Oven.json"), new JObject
            {
                ["DataType"] = "LabOvenData",
                ["CurrentIngredientID"] = "liquid_meth_blue",
                ["CurrentIngredientQuantity"] = 1,
                ["CurrentProductID"] = "meth_bluesky",
                ["CurrentCookProgress"] = 50
            });
            WriteJson(Path.Combine(saveDir, "Cartel.json"), new JObject
            {
                ["DataType"] = "CartelData",
                ["ActiveCartelDeal"] = new JObject
                {
                    ["RequestedProductID"] = "meth_bluesky",
                    ["RequestedProductQuantity"] = 20,
                    ["PaymentAmount"] = 9000,
                    ["Status"] = 1
                }
            });

            string moddedFile = Path.Combine(saveDir, "Modded", "OtherMod.json");
            WriteJson(moddedFile, new JObject { ["Note"] = "meth_bluesky" });

            string backupPath = BlueSkySaveCleaner.CreateSaveBackup(saveDir, backupRoot);
            Assert(File.Exists(Path.Combine(backupPath, "ChemStation.json")), "Backup missing ChemStation.json.");

            BlueSkySaveCleaner.ScrubSaveFolder(saveDir);

            var chem = ReadJson(Path.Combine(saveDir, "ChemStation.json"));
            Assert(chem["CurrentRecipeID"].Value<string>() == "", "ChemStation recipe not cleared.");
            var slot0 = JObject.Parse(chem["OutputContents"]["Items"][0].Value<string>());
            var slot1 = JObject.Parse(chem["OutputContents"]["Items"][1].Value<string>());
            Assert(slot0["ID"].Value<string>() == "" && slot0["Quantity"].Value<int>() == 0, "Mod item slot not emptied.");
            Assert(slot1["ID"].Value<string>() == "acid" && slot1["Quantity"].Value<int>() == 10, "Vanilla item slot touched.");

            var cfg = ReadJson(Path.Combine(saveDir, "ChemConfig.json"));
            Assert(cfg["Recipe"]["RecipeID"].Value<string>() == "", "ChemStation config recipe not cleared.");

            var oven = ReadJson(Path.Combine(saveDir, "Oven.json"));
            Assert(oven["CurrentIngredientID"].Value<string>() == "" && oven["CurrentProductID"].Value<string>() == "", "LabOven op not cleared.");

            var cartel = ReadJson(Path.Combine(saveDir, "Cartel.json"));
            Assert(cartel["ActiveCartelDeal"]["RequestedProductID"].Value<string>() == "", "Cartel deal not cleared.");

            Assert(ReadJson(moddedFile)["Note"].Value<string>() == "meth_bluesky", "Modded/ directory should not be modified.");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
        }
    }

    private static void TestPreservesOtherModsVanillaCyclesAndUnrelatedChats()
    {
        string saveDir = CreateTempDir();
        try
        {
            WriteJson(Path.Combine(saveDir, "Products.json"), new JObject
            {
                ["DataType"] = "ProductManagerData",
                ["CreatedWeed"] = new JArray
                {

                    new JObject { ["ID"] = "other_mod_cycle_a", ["Properties"] = new JArray("other_mod_effect") },
                    new JObject { ["ID"] = "other_mod_cycle_b", ["Properties"] = new JArray("other_mod_effect", "energizing") },
                    new JObject { ["ID"] = "other_mod_standalone", ["Properties"] = new JArray("other_mod_effect") }
                },
                ["CreatedMeth"] = new JArray
                {
                    new JObject { ["ID"] = "bluecuke", ["Properties"] = new JArray("bluesky_purity", "energizing") }
                },
                ["DiscoveredProducts"] = new JArray("meth_bluesky", "bluecuke", "other_mod_cycle_a", "other_mod_cycle_b", "other_mod_standalone", "ogkush"),
                ["ListedProducts"] = new JArray("meth_bluesky", "other_mod_cycle_a", "ogkush"),
                ["ProductPrices"] = new JArray
                {
                    new JObject { ["String"] = "meth_bluesky", ["Int"] = 450 },
                    new JObject { ["String"] = "other_mod_cycle_a", ["Int"] = 120 },
                    new JObject { ["String"] = "other_mod_cycle_b", ["Int"] = 140 },
                    new JObject { ["String"] = "ogkush", ["Int"] = 45 }
                },
                ["MixRecipes"] = new JArray
                {
                    new JObject { ["Product"] = "other_mod_cycle_b", ["Mixer"] = "banana", ["Output"] = "other_mod_cycle_a" },
                    new JObject { ["Product"] = "other_mod_cycle_a", ["Mixer"] = "cuke", ["Output"] = "other_mod_cycle_b" },
                    new JObject { ["Product"] = "meth_bluesky", ["Mixer"] = "cuke", ["Output"] = "bluecuke" }
                }
            });

            var staleCustomer = new JObject
            {
                ["DataType"] = "CustomerData",
                ["IsContractOffered"] = false,
                ["OfferedContract"] = new JObject
                {
                    ["Products"] = new JObject
                    {
                        ["entries"] = new JArray { new JObject { ["ProductID"] = "meth_bluesky", ["Quantity"] = 1 } }
                    }
                }
            };
            var unrelatedMsg = new JObject
            {
                ["DataType"] = "MSGConversationData",
                ["ActiveResponses"] = new JArray { new JObject { ["Text"] = "Ask about supplier" } }
            };
            WriteJson(Path.Combine(saveDir, "NPCs.json"), new JObject
            {
                ["DataType"] = "NPCCollectionData",
                ["NPCs"] = new JArray
                {
                    new JObject
                    {
                        ["BaseData"] = "{}",
                        ["AdditionalDatas"] = new JArray
                        {
                            new JObject { ["Name"] = "CustomerData", ["Contents"] = staleCustomer.ToString(Formatting.None) },
                            new JObject { ["Name"] = "MessageConversation", ["Contents"] = unrelatedMsg.ToString(Formatting.None) }
                        }
                    }
                }
            });

            string otherModPath = Path.Combine(saveDir, "OtherModCustomFolder", "state.json");
            WriteJson(otherModPath, new JObject
            {
                ["CustomKey"] = "other_mod_cycle_a",
                ["Items"] = new JArray(new JObject { ["ID"] = "other_mod_item", ["Quantity"] = 7 }.ToString(Formatting.None))
            });

            BlueSkySaveCleaner.ScrubSaveFolder(saveDir);

            var prods = ReadJson(Path.Combine(saveDir, "Products.json"));
            var weedIds = ((JArray)prods["CreatedWeed"]).Select(x => x["ID"].Value<string>()).ToList();
            Assert(weedIds.Contains("other_mod_cycle_a") && weedIds.Contains("other_mod_cycle_b") && weedIds.Contains("other_mod_standalone"),
                "Unrelated cyclic or other-mod created products were wrongly removed!");

            var priceKeys = ((JArray)prods["ProductPrices"]).Select(p => p["String"].Value<string>()).ToList();
            Assert(priceKeys.Contains("other_mod_cycle_a") && priceKeys.Contains("other_mod_cycle_b") && priceKeys.Contains("ogkush"),
                "Unrelated ProductPrices were wrongly removed!");

            var npcs = ReadJson(Path.Combine(saveDir, "NPCs.json"));
            var msgAfter = JObject.Parse(npcs["NPCs"][0]["AdditionalDatas"][1]["Contents"].Value<string>());
            Assert(((JArray)msgAfter["ActiveResponses"]).Count == 1,
                "Unrelated MessageConversation ActiveResponses was cleared for an already-inactive customer offer!");

            var otherModAfter = ReadJson(otherModPath);
            var otherSlot = JObject.Parse(otherModAfter["Items"][0].Value<string>());
            Assert(otherSlot["ID"].Value<string>() == "other_mod_item" && otherSlot["Quantity"].Value<int>() == 7,
                "Other mod JSON file was wrongly modified!");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }
    }

    private static void TestSlotFiltersLegacyContractsAndSpecialCustomers()
    {
        string saveDir = CreateTempDir();
        try
        {
            WriteJson(Path.Combine(saveDir, "Rack.json"), new JObject
            {
                ["DataType"] = "PlaceableStorageData",
                ["Contents"] = new JObject
                {
                    ["Items"] = new JArray(),
                    ["SlotFilters"] = new JArray
                    {
                        new JObject { ["Type"] = 1, ["ItemIDs"] = new JArray("meth_bluesky"), ["AllowedQualities"] = new JArray(3, 4) },
                        new JObject { ["Type"] = 1, ["ItemIDs"] = new JArray("meth_bluesky", "ogkush"), ["AllowedQualities"] = new JArray() },
                        new JObject { ["Type"] = 2, ["ItemIDs"] = new JArray("pseudo_pristine"), ["AllowedQualities"] = new JArray() },
                        new JObject { ["Type"] = 1, ["ItemIDs"] = new JArray("other_mod_item"), ["AllowedQualities"] = new JArray() }
                    }
                }
            });

            string pureContractGuid = "a1b2c3d4-0000-0000-0000-000000000001";
            string pureContractPath = Path.Combine(saveDir, "Quests", "Contracts", pureContractGuid + ".json");
            WriteJson(pureContractPath, new JObject
            {
                ["DataType"] = "ContractData",
                ["GUID"] = pureContractGuid,
                ["ProductList"] = new JObject
                {
                    ["entries"] = new JArray
                    {
                        new JObject { ["ProductID"] = "meth_bluesky", ["Quantity"] = 3 }
                    }
                }
            });

            string mixedContractGuid = "a1b2c3d4-0000-0000-0000-000000000002";
            string mixedContractPath = Path.Combine(saveDir, "Quests", "Contracts", mixedContractGuid + ".json");
            WriteJson(mixedContractPath, new JObject
            {
                ["DataType"] = "ContractData",
                ["GUID"] = mixedContractGuid,
                ["ProductList"] = new JObject
                {
                    ["entries"] = new JArray
                    {
                        new JObject { ["ProductID"] = "meth_bluesky", ["Quantity"] = 2 },
                        new JObject { ["ProductID"] = "ogkush", ["Quantity"] = 4 }
                    }
                }
            });

            WriteJson(Path.Combine(saveDir, "NPCs", "Dealer_Benji", "NPC.json"), new JObject
            {
                ["DataType"] = "DealerData",
                ["ActiveContractGUIDs"] = new JArray(pureContractGuid, mixedContractGuid)
            });

            WriteJson(Path.Combine(saveDir, "SpecialCustomers.json"), new JObject
            {
                ["DataType"] = "SpecialCustomerSaveData",
                ["GroupId"] = "cartel_vip",
                ["DesiredEffects"] = new JArray("bluesky_purity", "energizing", "other_mod_effect"),
                ["GroupData"] = new JArray
                {
                    new JObject
                    {
                        ["GroupId"] = "cartel_vip",
                        ["GroupInventory"] = new JObject
                        {
                            ["Items"] = new JArray(
                                new JObject { ["DataType"] = "MethData", ["ID"] = "meth_bluesky", ["Quantity"] = 5 }.ToString(Formatting.None),
                                new JObject { ["DataType"] = "WeedData", ["ID"] = "ogkush", ["Quantity"] = 2 }.ToString(Formatting.None)
                            )
                        }
                    }
                }
            });

            BlueSkySaveCleaner.ScrubSaveFolder(saveDir);

            var rack = ReadJson(Path.Combine(saveDir, "Rack.json"));
            var filters = (JArray)rack["Contents"]["SlotFilters"];
            Assert(filters[0]["Type"].Value<int>() == 0 && ((JArray)filters[0]["ItemIDs"]).Count == 0 && ((JArray)filters[0]["AllowedQualities"]).Count == 2,
                "Emptied whitelist SlotFilter should reset Type to 0 while preserving AllowedQualities.");
            Assert(filters[1]["Type"].Value<int>() == 1 && ((JArray)filters[1]["ItemIDs"]).Count == 1 && filters[1]["ItemIDs"][0].Value<string>() == "ogkush",
                "Mixed whitelist SlotFilter should stay Type 1 with vanilla IDs preserved.");
            Assert(filters[2]["Type"].Value<int>() == 0 && ((JArray)filters[2]["ItemIDs"]).Count == 0,
                "Emptied blacklist SlotFilter should reset Type to 0.");
            Assert(filters[3]["Type"].Value<int>() == 1 && ((JArray)filters[3]["ItemIDs"]).Count == 1 && filters[3]["ItemIDs"][0].Value<string>() == "other_mod_item",
                "Other-mod SlotFilter should not be modified.");

            Assert(!File.Exists(pureContractPath), "Legacy standalone pure-BlueSky ContractData file should be deleted.");
            Assert(File.Exists(mixedContractPath), "Legacy standalone mixed ContractData file should be preserved.");
            var mixedAfter = ReadJson(mixedContractPath);
            var mixedEntries = (JArray)mixedAfter["ProductList"]["entries"];
            Assert(mixedEntries.Count == 1 && mixedEntries[0]["ProductID"].Value<string>() == "ogkush",
                "Legacy standalone mixed ContractData should keep vanilla entries.");

            var dealer = ReadJson(Path.Combine(saveDir, "NPCs", "Dealer_Benji", "NPC.json"));
            var activeGuids = ((JArray)dealer["ActiveContractGUIDs"]).Select(t => t.Value<string>()).ToList();
            Assert(!activeGuids.Contains(pureContractGuid) && activeGuids.Contains(mixedContractGuid),
                "Dealer ActiveContractGUIDs should drop deleted legacy contract GUID and keep mixed contract GUID.");

            var special = ReadJson(Path.Combine(saveDir, "SpecialCustomers.json"));
            var effects = ((JArray)special["DesiredEffects"]).Select(t => t.Value<string>()).ToList();
            Assert(!effects.Contains("bluesky_purity") && effects.Contains("energizing") && effects.Contains("other_mod_effect"),
                "SpecialCustomerSaveData DesiredEffects should strip only bluesky_purity.");
            var vipItems = (JArray)special["GroupData"][0]["GroupInventory"]["Items"];
            Assert(JObject.Parse(vipItems[0].Value<string>())["ID"].Value<string>() == "",
                "SpecialCustomer GroupInventory mod slot not emptied.");
            Assert(JObject.Parse(vipItems[1].Value<string>())["ID"].Value<string>() == "ogkush",
                "SpecialCustomer GroupInventory vanilla slot touched.");
        }
        finally
        {
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }
    }
}

