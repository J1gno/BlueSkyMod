// ============================================================================
// UninstallWorkflow.cs — Safe In-Game & Pre-Load Uninstall Orchestration
// ============================================================================
// Coordinates two ways for players to cleanly remove the mod from a save:
//   1. In-game console command `bluesky_uninstall` (host/SP only): saves progress, blocks subsequent
//      saves, backs up the save to UserData/BlueSkyMod_Backups, scrubs via BlueSkySaveCleaner, and
//      exits to the Main Menu.
//   2. `UninstallOnNextLoad = true` in UserData/MelonPreferences.cfg: backs up and scrubs the save
//      in `LoadManager.StartGame` before loading, then loads cleanly without injecting mod items.
// ============================================================================

using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.Events;

using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Product;
using S1Console = Il2CppScheduleOne.Console;

namespace BlueSkyMod
{
    public partial class Main
    {
        // Backup output directory created under Schedule I/UserData/.
        private static string GetBackupRoot()
        {
            return Path.Combine(MelonEnvironment.UserDataDirectory, "BlueSkyMod_Backups");
        }

        private static string NormalizeSavePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        private static bool IsProtectedGamePath(string savePath)
        {
            if (string.IsNullOrEmpty(savePath)) return false;
            string normSave = NormalizeSavePath(savePath);
            string normStreaming = NormalizeSavePath(Application.streamingAssetsPath);
            if (!string.IsNullOrEmpty(normStreaming) &&
                (string.Equals(normSave, normStreaming, StringComparison.OrdinalIgnoreCase) ||
                 normSave.StartsWith(normStreaming + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return true;

            string normData = NormalizeSavePath(Application.dataPath);
            if (!string.IsNullOrEmpty(normData) &&
                (string.Equals(normSave, normData, StringComparison.OrdinalIgnoreCase) ||
                 normSave.StartsWith(normData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        public static class SaveManager_Save_Patch
        {
            public static bool Prefix()
            {
                if (!_blockGameSavesForUninstall)
                    return true;
                LogWarnOnce("uninstall-save-blocked", "Blocked a game save while Blue Sky uninstall cleanup is active so cleaned files are not overwritten.");
                return false;
            }
        }

        public static class LoadManager_StartGame_Patch
        {
            public static void Prefix(SaveInfo __0)
            {
                bool backupCreated = false;
                string backupPath = null;
                try
                {
                    ResetLoadState();
                    _suppressInjectionThisLoad = false;
                    _blockGameSavesForUninstall = false;

                    string savePath = __0?.SavePath;
                    string normPath = NormalizeSavePath(savePath);

                    if (!string.IsNullOrEmpty(normPath) && _cleanedSavePaths.Contains(normPath))
                    {
                        _suppressInjectionThisLoad = true;
                        RemoveCustomItemsFromRuntimeRegistries();
                        LogMsg($"This save was cleaned for uninstall earlier in this session — keeping Blue Sky Mod disabled so you can verify it cleanly before deleting {ModFileName}.");
                        return;
                    }

                    if (_prefUninstallOnNextLoad == null || !_prefUninstallOnNextLoad.Value)
                        return;

                    if (string.IsNullOrEmpty(savePath) || !Directory.Exists(savePath))
                    {
                        LogWarn("UninstallOnNextLoad is enabled, but the save folder path was not found.");
                        return;
                    }

                    if (IsProtectedGamePath(savePath))
                    {
                        LogWarn("UninstallOnNextLoad skipped: target path is inside game installation data (tutorial/default save).");
                        return;
                    }

                    backupPath = BlueSkySaveCleaner.CreateSaveBackup(savePath, GetBackupRoot());
                    backupCreated = true;

                    var stats = BlueSkySaveCleaner.ScrubSaveFolder(savePath, LogWarn);

                    _cleanedSavePaths.Add(normPath);
                    _suppressInjectionThisLoad = true;
                    RemoveCustomItemsFromRuntimeRegistries();
                    _prefUninstallOnNextLoad.Value = false;
                    _prefsCategory?.SaveToFile(false);

                    LogMsg($"Blue Sky Mod Uninstall Mode complete! Backup: '{backupPath}'. {stats.FormatSummary()}");
                    LogMsg($"Loading save without Blue Sky Mod. Verify everything looks clean, exit the game, and remove {ModFileName}.");
                }
                catch (Exception ex)
                {
                    if (backupCreated)
                    {
                        _suppressInjectionThisLoad = true;
                        RemoveCustomItemsFromRuntimeRegistries();
                        LogError($"UninstallOnNextLoad failed after creating backup '{backupPath}' (injection suppressed for safety — restore from backup if needed): {ex}");
                    }
                    else
                    {
                        LogError($"UninstallOnNextLoad failed before modifying the save: {ex}");
                    }
                }
            }
        }

        public static class LoadManager_LoadAsClient_Patch
        {
            public static void Prefix()
            {
                ResetLoadState();
                _suppressInjectionThisLoad = false;
                _blockGameSavesForUninstall = false;
            }
        }

        internal static void RequestActiveSaveUninstall()
        {
            if (_uninstallRunning)
            {
                LogBoth("Blue Sky uninstall is already in progress.");
                return;
            }

            var lm = Singleton<LoadManager>.Instance;
            if (lm == null || !lm.IsGameLoaded)
            {
                LogBoth("Load into a save first, or set UninstallOnNextLoad = true in UserData/MelonPreferences.cfg before loading.");
                return;
            }

            var pm = NetworkSingleton<ProductManager>.Instance;
            if (pm != null && pm.IsClientInitialized && !pm.IsServerInitialized)
            {
                LogBoth($"Only the host can run bluesky_uninstall. Ask the host to run it, then remove {ModFileName} on all players.");
                return;
            }

            string saveFolder = lm.ActiveSaveInfo?.SavePath;
            if (string.IsNullOrEmpty(saveFolder))
                saveFolder = lm.LoadedGameFolderPath;

            if (string.IsNullOrEmpty(saveFolder) || !Directory.Exists(saveFolder))
            {
                LogBoth("Could not locate the active save folder on disk.");
                return;
            }

            if (IsProtectedGamePath(saveFolder))
            {
                LogBoth("Cannot run bluesky_uninstall during the tutorial or on built-in game files. Load a normal save first.");
                return;
            }

            _uninstallRunning = true;
            _uninstallToken = MelonCoroutines.Start(UninstallActiveSaveRoutine(saveFolder));
        }

        private static IEnumerator UninstallActiveSaveRoutine(string saveFolder)
        {
            bool scrubCompleted = false;
            try
            {
                LogBoth("Blue Sky uninstall started: saving latest progress first...");

                var sm = Singleton<SaveManager>.Instance;
                if (sm != null)
                {
                    if (sm.IsSaving)
                    {
                        float waitExisting = 0f;
                        while (sm.IsSaving && waitExisting < 20f)
                        {
                            waitExisting += Time.unscaledDeltaTime;
                            yield return null;
                        }
                        if (sm.IsSaving)
                        {
                            LogBoth("Blue Sky uninstall aborted: an existing save is still in progress after 20 seconds. Nothing was changed.");
                            yield break;
                        }
                    }

                    bool hadSaveError = ReadSaveManagerError();
                    bool saveCompleted = false;
                    bool subscribed = false;
                    UnityAction onSaveComplete = (UnityAction)(() => saveCompleted = true);
                    bool saveCallFailed = false;

                    try
                    {
                        if (sm.onSaveComplete != null)
                        {
                            sm.onSaveComplete.AddListener(onSaveComplete);
                            subscribed = true;
                        }

                        try { sm.Save(); }
                        catch (Exception ex)
                        {
                            saveCallFailed = true;
                            LogError($"Blue Sky uninstall aborted — pre-uninstall Save() failed: {ex}");
                            LogToGameConsole($"Blue Sky uninstall aborted: save failed ({ex.Message}). Nothing was changed.");
                        }

                        if (saveCallFailed)
                            yield break;

                        bool saveStarted = sm.IsSaving;
                        float waitStart = 0f;
                        while (!saveStarted && !saveCompleted && waitStart < 1.0f)
                        {
                            waitStart += Time.unscaledDeltaTime;
                            yield return null;
                            if (sm.IsSaving) saveStarted = true;
                        }

                        if (!saveStarted && !saveCompleted)
                        {
                            LogBoth("Blue Sky uninstall aborted: the game did not start saving. Try again when saving is available. Nothing was changed.");
                            yield break;
                        }

                        float waitSave = 0f;
                        while (sm.IsSaving && !saveCompleted && waitSave < 20f)
                        {
                            waitSave += Time.unscaledDeltaTime;
                            yield return null;
                        }

                        if (sm.IsSaving && !saveCompleted)
                        {
                            LogBoth("Blue Sky uninstall aborted: the game is still saving after 20 seconds. Nothing was changed. Wait for the save to finish, then run bluesky_uninstall again.");
                            yield break;
                        }

                        if (!hadSaveError && ReadSaveManagerError())
                        {
                            LogBoth("Blue Sky uninstall aborted: the game reported a save error while saving. Nothing was changed.");
                            yield break;
                        }
                    }
                    finally
                    {
                        if (subscribed && sm != null && sm.onSaveComplete != null)
                            sm.onSaveComplete.RemoveListener(onSaveComplete);
                    }
                }

                _blockGameSavesForUninstall = true;

                yield return new WaitForSecondsRealtime(0.25f);

                string backupRoot = GetBackupRoot();
                var backupTask = Task.Run(() => BlueSkySaveCleaner.CreateSaveBackup(saveFolder, backupRoot));
                while (!backupTask.IsCompleted)
                    yield return null;

                string backupPath;
                if (backupTask.IsFaulted)
                {
                    Exception ex = backupTask.Exception?.GetBaseException() ?? backupTask.Exception;
                    LogError($"Blue Sky uninstall aborted — failed to back up save folder: {ex}");
                    LogToGameConsole($"Blue Sky uninstall aborted: backup failed ({ex?.Message}).");
                    yield break;
                }
                backupPath = backupTask.Result;

                var scrubWarnings = new System.Collections.Generic.List<string>();
                var scrubTask = Task.Run(() => BlueSkySaveCleaner.ScrubSaveFolder(saveFolder, scrubWarnings.Add));
                while (!scrubTask.IsCompleted)
                    yield return null;

                for (int i = 0; i < scrubWarnings.Count; i++)
                    LogWarn(scrubWarnings[i]);

                if (scrubTask.IsFaulted)
                {
                    Exception ex = scrubTask.Exception?.GetBaseException() ?? scrubTask.Exception;
                    LogError($"Blue Sky uninstall failed during scrub (backup is safe at '{backupPath}'): {ex}");
                    LogToGameConsole($"Blue Sky uninstall failed: {ex?.Message}. Backup saved at: {backupPath}");
                    yield break;
                }

                var stats = scrubTask.Result;
                scrubCompleted = true;

                CancelRoutines();
                _suppressInjectionThisLoad = true;
                _cleanedSavePaths.Add(NormalizeSavePath(saveFolder));
                ClearLocalPlayerBlueSkyEffects();
                RemoveCustomItemsFromRuntimeRegistries();

                LogBoth($"Blue Sky uninstall complete! {stats.FormatSummary()}");
                LogBoth($"Backup saved to: {backupPath}");
                LogBoth($"Returning to Main Menu without re-saving. Close the game and delete {ModFileName} (on host and all clients).");

                yield return new WaitForSecondsRealtime(0.5f);

                TryExitToMenuWithPopup();
            }
            finally
            {
                if (!scrubCompleted)
                    _blockGameSavesForUninstall = false;
                _uninstallRunning = false;
                _uninstallToken = null;
            }
        }

        private static bool ReadSaveManagerError()
        {
            try { return SaveManager.SaveError; }
            catch { return false; }
        }

        private static void TryExitToMenuWithPopup()
        {
            var lm = Singleton<LoadManager>.Instance;
            if (lm == null) return;

            try
            {
                var exitMethod = AccessTools.Method(typeof(LoadManager), "ExitToMenu");
                if (exitMethod == null) return;

                var prms = exitMethod.GetParameters();
                var callArgs = new object[prms.Length];
                for (int i = 0; i < prms.Length; i++)
                {
                    Type pt = prms[i].ParameterType;
                    if (pt.Name == "Data" && pt.DeclaringType?.Name == "MainMenuPopup")
                    {
                        var ctor = pt.GetConstructor(new[] { typeof(string), typeof(string), typeof(bool) });
                        if (ctor != null)
                        {
                            string popupBody =
                                "Save cleaned & backed up!\n" +
                                $"Close game & remove {ModFileName}.\n" +
                                "Backup: UserData\\BlueSkyMod_Backups";
                            callArgs[i] = ctor.Invoke(new object[] { "Blue Sky Mod Cleaned", popupBody, false });
                            continue;
                        }
                    }
                    callArgs[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                exitMethod.Invoke(lm, callArgs);
            }
            catch (Exception ex)
            {
                LogWarn($"Could not automatically return to menu ({ex.Message}) — please close the game WITHOUT saving.");
            }
        }

        private static void LogBoth(string message)
        {
            LogMsg(message);
            LogToGameConsole(message);
        }

        private static void LogToGameConsole(string message)
        {
            try
            {
                S1Console.Log((Il2CppSystem.Object)(Il2CppSystem.String)("[Blue Sky Mod] " + message), null);
            }
            catch { }
        }
    }
}

