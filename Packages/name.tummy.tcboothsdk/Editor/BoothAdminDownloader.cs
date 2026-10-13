using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class BoothAdminDownloader : EditorWindow
{
    private string serverUrl = "https://upload.tummy.name";
    private string adminUsername = "";
    private string adminPassword = "";

    private const string PrefKey_AdminUser = "BoothSDK_AdminUser";
    private const string PrefKey_AdminPass = "BoothSDK_AdminPass";
    private const string DownloadTempPath = "Temp/BoothDownloads";
    private const string BoothAssetRoot = "Assets/Booth";
    private const string TrackingFilePath = "ProjectSettings/BoothSDK_DownloadHistory.json";
    // Downloaded but not yet imported. Moved into the history only once Unity reports the import as completed,
    // so a failed import is downloaded again on the next sync.
    private const string PendingImportsPath = "Temp/BoothSDK_PendingImports.json";

    [InitializeOnLoadMethod]
    private static void RegisterImportCallbacks()
    {
        AssetDatabase.importPackageCompleted += OnImportPackageCompleted;
        AssetDatabase.importPackageFailed += OnImportPackageFailed;
        AssetDatabase.importPackageCancelled += OnImportPackageCancelled;
    }

    [MenuItem("Booth SDK/Admin/Admin Downloader")]
    public static void ShowWindow()
    {
        GetWindow<BoothAdminDownloader>("Booth Admin Downloader");
    }

    private void OnEnable()
    {
        adminUsername = EditorPrefs.GetString(PrefKey_AdminUser, "");
        adminPassword = EditorPrefs.GetString(PrefKey_AdminPass, "");
    }

    private void OnGUI()
    {
        GUILayout.Label("Admin Configuration", EditorStyles.boldLabel);
        
        serverUrl = EditorGUILayout.TextField("Server URL", serverUrl);
        
        EditorGUI.BeginChangeCheck();
        adminUsername = EditorGUILayout.TextField("Admin Username", adminUsername);
        adminPassword = EditorGUILayout.PasswordField("Admin Password", adminPassword);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetString(PrefKey_AdminUser, adminUsername);
            EditorPrefs.SetString(PrefKey_AdminPass, adminPassword);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space();

        // --- NEW: SMART AUTOMATION ---
        GUILayout.Label("Smart Automation", EditorStyles.boldLabel);
        GUIStyle wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11, fontStyle = FontStyle.Italic };
        GUILayout.Label("Automatically fetches the booth list, skips unchanged files, deletes old folders for updated booths, and imports the new packages.", wrapStyle);
        
        if (GUILayout.Button("Smart Sync & Import All", GUILayout.Height(40)))
        {
            _ = SmartSyncAndImportAsync();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space();

        // --- MANUAL CONTROLS ---
        GUILayout.Label("Manual Overrides", EditorStyles.boldLabel);
        
        GUILayout.Label("Step 1: Clean Up");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Delete Assets (Project)", GUILayout.Height(25)))
        {
            DeleteExistingBooths();
        }
        if (GUILayout.Button("Delete Objects (Scene)", GUILayout.Height(25)))
        {
            ClearBoothsFromScene();
        }
        GUILayout.EndHorizontal();

        EditorGUILayout.Space();

        GUILayout.Label("Step 2: Retrieve Data");
        if (GUILayout.Button("Force Download All Booth Packages", GUILayout.Height(25)))
        {
            _ = DownloadAllBoothsAsync();
        }

        EditorGUILayout.Space();

        GUILayout.Label("Step 3: Asset Import");
        if (GUILayout.Button("Import Downloaded Packages", GUILayout.Height(25)))
        {
            ImportDownloadedPackages();
        }

        EditorGUILayout.Space();

        GUILayout.Label("Step 4: Scene Setup");
        if (GUILayout.Button("Spawn Prefabs into Scene", GUILayout.Height(25)))
        {
            SpawnPrefabsToScene();
        }
    }

    // ==========================================
    // NEW: SMART SYNC LOGIC (BATCHED)
    // ==========================================
    private async Task SmartSyncAndImportAsync()
    {
        if (string.IsNullOrEmpty(adminUsername) || string.IsNullOrEmpty(adminPassword))
        {
            EditorUtility.DisplayDialog("Error", "Admin credentials required.", "OK");
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("Smart Sync", "Fetching booth list from server...", 0.1f);

            // 1. Fetch Booth List
            string apiUrl = $"{serverUrl}/api/booths";
            BoothListWrapper wrapper = null;

            using (UnityWebRequest req = UnityWebRequest.Get(apiUrl))
            {
                req.SetRequestHeader("x-username", adminUsername);
                req.SetRequestHeader("x-password", adminPassword);

                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Delay(50);

                if (req.result != UnityWebRequest.Result.Success)
                    throw new System.Exception($"Failed to get booth list: {req.error}");

                string wrappedJson = "{ \"booths\": " + req.downloadHandler.text + " }";
                wrapper = JsonUtility.FromJson<BoothListWrapper>(wrappedJson);
            }

            if (wrapper == null || wrapper.booths == null || wrapper.booths.Count == 0)
            {
                Debug.Log("[Booth Admin] No booths found on the server.");
                EditorUtility.ClearProgressBar();
                return;
            }

            // 2. Load History & Clean Temp Folder
            DownloadHistory history = LoadHistory();
            CleanTempDownloadsFolder();

            int downloadedCount = 0;

            // PHASE 1: BATCH CHECK & DOWNLOAD
            for (int i = 0; i < wrapper.booths.Count; i++)
            {
                BoothEntry booth = wrapper.booths[i];
                float progress = 0.2f + (0.5f * ((float)i / wrapper.booths.Count));

                if (!TryGetSafeNames(booth, out string safeCreator, out string safeFilename)) continue;
                HistoryRecord existingRecord = history.records.FirstOrDefault(r => r.creator == safeCreator);

                // Check: Has the file changed?
                if (existingRecord != null && existingRecord.lastFilename == safeFilename)
                {
                    Debug.Log($"[Smart Sync] Skipping {safeCreator} (Already up to date)");
                    continue;
                }

                // Download the new package
                EditorUtility.DisplayProgressBar("Smart Sync", $"Downloading update for {booth.creator}...", progress);
                using (UnityWebRequest dlReq = UnityWebRequest.Get(booth.downloadUrl))
                {
                    dlReq.SetRequestHeader("x-username", adminUsername);
                    dlReq.SetRequestHeader("x-password", adminPassword);

                    var dlOp = dlReq.SendWebRequest();
                    while (!dlOp.isDone) await Task.Delay(50);

                    if (dlReq.result == UnityWebRequest.Result.Success)
                    {
                        string savePath = Path.Combine(DownloadTempPath, safeFilename);
                        File.WriteAllBytes(savePath, dlReq.downloadHandler.data);
                        downloadedCount++;

                        // History is updated once the import completes (see OnImportPackageCompleted)
                        AddPendingImport(safeCreator, safeFilename);
                    }
                    else
                    {
                        Debug.LogError($"[Smart Sync] Failed to download {safeFilename}: {dlReq.error}");
                    }
                }
            }

            if (downloadedCount > 0)
            {
                // PHASE 2: REVIEW. Before deleting anything, so a skipped booth keeps its current version.
                EditorUtility.ClearProgressBar();
                List<(string creator, string packagePath)> approved = ReviewDownloadedPackages();

                // PHASE 3: BATCH DELETE (only booths that are about to be replaced)
                EditorUtility.DisplayProgressBar("Smart Sync", "Cleaning up outdated folders...", 0.75f);
                foreach (var (creator, _) in approved)
                {
                    string userFolder = $"{BoothAssetRoot}/{creator}";
                    if (AssetDatabase.IsValidFolder(userFolder))
                    {
                        Debug.Log($"[Smart Sync] Deleting outdated folder: {userFolder}");
                        AssetDatabase.DeleteAsset(userFolder);
                    }
                }

                EditorUtility.DisplayProgressBar("Smart Sync", "Refreshing Asset Database...", 0.85f);
                AssetDatabase.Refresh();

                // PHASE 4: BATCH IMPORT
                EditorUtility.DisplayProgressBar("Smart Sync", "Importing new packages...", 0.95f);
                ImportPackages(approved.Select(a => a.packagePath).ToList());

                Debug.Log($"[Smart Sync] Complete! Downloaded {downloadedCount} updates, queued {approved.Count} for import.");
            }
            else
            {
                Debug.Log("[Smart Sync] Complete! All booths are already up to date.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Smart Sync] Error: {ex.Message}");
            EditorUtility.DisplayDialog("Sync Error", ex.Message, "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }
    private void CleanTempDownloadsFolder()
    {
        if (Directory.Exists(DownloadTempPath))
        {
            DirectoryInfo di = new DirectoryInfo(DownloadTempPath);
            foreach (FileInfo file in di.GetFiles())
            {
                file.Delete();
            }
        }
        else
        {
            Directory.CreateDirectory(DownloadTempPath);
        }

        // The packages these pointed to were just deleted
        if (File.Exists(PendingImportsPath)) File.Delete(PendingImportsPath);
    }

    // The server only creates [a-zA-Z0-9_-] creator folders and plain .unitypackage names.
    // Anything else (e.g. "..\" or an absolute path) is rejected so it can't write outside the intended folders.
    private static bool TryGetSafeNames(BoothEntry booth, out string safeCreator, out string safeFilename)
    {
        safeCreator = booth.creator ?? "";
        safeFilename = Path.GetFileName(booth.filename ?? "");

        bool creatorOk = Regex.IsMatch(safeCreator, @"^[a-zA-Z0-9_-]+$");
        bool filenameOk = safeFilename == booth.filename &&
                          safeFilename.EndsWith(".unitypackage", System.StringComparison.OrdinalIgnoreCase) &&
                          safeFilename.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        if (!creatorOk || !filenameOk)
        {
            Debug.LogError($"[Booth Admin] Skipping booth with unexpected creator/filename from server: '{booth.creator}' / '{booth.filename}'");
            return false;
        }
        return true;
    }

    private static DownloadHistory LoadHistory()
    {
        return LoadRecords(TrackingFilePath);
    }

    private static void SaveHistory(DownloadHistory history)
    {
        SaveRecords(TrackingFilePath, history);
    }

    private static DownloadHistory LoadRecords(string filePath)
    {
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            return JsonUtility.FromJson<DownloadHistory>(json) ?? new DownloadHistory();
        }
        return new DownloadHistory();
    }

    private static void SaveRecords(string filePath, DownloadHistory records)
    {
        string json = JsonUtility.ToJson(records, true);
        File.WriteAllText(filePath, json);
    }

    private static void SetRecord(DownloadHistory records, string creator, string filename)
    {
        HistoryRecord existingRecord = records.records.FirstOrDefault(r => r.creator == creator);
        if (existingRecord != null) existingRecord.lastFilename = filename;
        else records.records.Add(new HistoryRecord { creator = creator, lastFilename = filename });
    }

    private static void AddPendingImport(string creator, string filename)
    {
        DownloadHistory pending = LoadRecords(PendingImportsPath);
        SetRecord(pending, creator, filename);
        SaveRecords(PendingImportsPath, pending);
    }

    // Removes and returns the pending record for an imported package, or null if it isn't one of ours
    private static HistoryRecord TakePendingImport(string packageName)
    {
        if (!File.Exists(PendingImportsPath)) return null;

        DownloadHistory pending = LoadRecords(PendingImportsPath);
        string name = Path.GetFileNameWithoutExtension(packageName);
        HistoryRecord record = pending.records.FirstOrDefault(r => Path.GetFileNameWithoutExtension(r.lastFilename) == name);
        if (record != null)
        {
            pending.records.Remove(record);
            SaveRecords(PendingImportsPath, pending);
        }
        return record;
    }

    private static void OnImportPackageCompleted(string packageName)
    {
        HistoryRecord record = TakePendingImport(packageName);
        if (record == null) return;

        DownloadHistory history = LoadHistory();
        SetRecord(history, record.creator, record.lastFilename);
        SaveHistory(history);
        Debug.Log($"[Booth Admin] Imported {record.lastFilename}, marked as up to date.");
    }

    private static void OnImportPackageFailed(string packageName, string errorMessage)
    {
        HistoryRecord record = TakePendingImport(packageName);
        if (record == null) return;
        Debug.LogError($"[Booth Admin] Import of {record.lastFilename} failed: {errorMessage}. It will be downloaded again on the next sync.");
    }

    private static void OnImportPackageCancelled(string packageName)
    {
        HistoryRecord record = TakePendingImport(packageName);
        if (record == null) return;
        Debug.LogWarning($"[Booth Admin] Import of {record.lastFilename} was cancelled. It will be downloaded again on the next sync.");
    }
    // ==========================================


    // ==========================================
    // STEP 1A: DELETE EXISTING BOOTHS (PROJECT)
    // ==========================================
    private void DeleteExistingBooths()
    {
        if (AssetDatabase.IsValidFolder(BoothAssetRoot))
        {
            string[] subfolders = AssetDatabase.GetSubFolders(BoothAssetRoot);
            if (!EditorUtility.DisplayDialog("Delete All Booth Assets?",
                    $"This permanently deletes all {subfolders.Length} folders under {BoothAssetRoot} and clears the download history.\n\nThis cannot be undone.",
                    "Delete", "Cancel"))
            {
                return;
            }

            Debug.Log("[Booth Admin] Starting deletion of existing booth assets...");
            foreach (string folder in subfolders)
            {
                Debug.Log($"[Booth Admin] Deleting folder: {folder}");
                AssetDatabase.DeleteAsset(folder);
            }
            AssetDatabase.Refresh();
            
            // Wipe history since we manually deleted everything
            if (File.Exists(TrackingFilePath)) File.Delete(TrackingFilePath);
            
            Debug.Log("[Booth Admin] Asset deletion complete. Project refreshed.");
        }
        else
        {
            Debug.LogWarning($"[Booth Admin] Folder {BoothAssetRoot} does not exist yet. Nothing to delete.");
        }
    }

    // ==========================================
    // STEP 1B: CLEAR SCENE HIERARCHY
    // ==========================================
    private void ClearBoothsFromScene()
    {
        BoothDescriptor[] existingInScene = FindObjectsOfType<BoothDescriptor>();
        if (existingInScene.Length == 0)
        {
            Debug.Log("[Booth Admin] No booths found in the active scene.");
            return;
        }

        if (!EditorUtility.DisplayDialog("Delete Booths From Scene?",
                $"This removes all {existingInScene.Length} booths from the open scene.\n\nYou can undo this with Ctrl+Z.",
                "Delete", "Cancel"))
        {
            return;
        }

        int count = 0;
        foreach (var desc in existingInScene)
        {
            if (desc == null) continue; // Already destroyed together with a parent booth
            Undo.DestroyObjectImmediate(desc.gameObject);
            count++;
        }
        Undo.SetCurrentGroupName("Delete Booths From Scene");

        Debug.Log($"[Booth Admin] Cleared {count} booths from the scene.");
    }

    // ==========================================
    // STEP 2: DOWNLOAD ALL BOOTHS
    // ==========================================
    private async Task DownloadAllBoothsAsync()
    {
        if (string.IsNullOrEmpty(adminUsername) || string.IsNullOrEmpty(adminPassword))
        {
            EditorUtility.DisplayDialog("Error", "Admin credentials required.", "OK");
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("Admin Downloader", "Fetching booth list...", 0.1f);
            
            string apiUrl = $"{serverUrl}/api/booths";
            Debug.Log($"[Booth Admin] Requesting list from: {apiUrl}");

            using (UnityWebRequest req = UnityWebRequest.Get(apiUrl))
            {
                req.SetRequestHeader("x-username", adminUsername);
                req.SetRequestHeader("x-password", adminPassword);

                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Delay(50);

                if (req.result != UnityWebRequest.Result.Success)
                {
                    throw new System.Exception($"Failed to get booth list: {req.error}\n(Check your admin credentials)");
                }

                string jsonResponse = req.downloadHandler.text;
                string wrappedJson = "{ \"booths\": " + jsonResponse + " }";
                
                BoothListWrapper wrapper = JsonUtility.FromJson<BoothListWrapper>(wrappedJson);
                
                if (wrapper == null || wrapper.booths == null || wrapper.booths.Count == 0)
                {
                    Debug.Log("[Booth Admin] No booths found on the server.");
                    EditorUtility.ClearProgressBar();
                    return;
                }

                Debug.Log($"[Booth Admin] Found {wrapper.booths.Count} booths to download.");

                CleanTempDownloadsFolder();

                for (int i = 0; i < wrapper.booths.Count; i++)
                {
                    BoothEntry booth = wrapper.booths[i];
                    float progress = 0.2f + (0.8f * ((float)i / wrapper.booths.Count));

                    if (!TryGetSafeNames(booth, out string safeCreator, out string safeFilename)) continue;

                    EditorUtility.DisplayProgressBar("Admin Downloader", $"Downloading {safeFilename}...", progress);
                    Debug.Log($"[Booth Admin] Downloading: {safeFilename}");

                    using (UnityWebRequest dlReq = UnityWebRequest.Get(booth.downloadUrl))
                    {
                        dlReq.SetRequestHeader("x-username", adminUsername);
                        dlReq.SetRequestHeader("x-password", adminPassword);

                        var dlOp = dlReq.SendWebRequest();
                        while (!dlOp.isDone) await Task.Delay(50);

                        if (dlReq.result == UnityWebRequest.Result.Success)
                        {
                            string savePath = Path.Combine(DownloadTempPath, safeFilename);
                            File.WriteAllBytes(savePath, dlReq.downloadHandler.data);
                            Debug.Log($"[Booth Admin] Saved to: {savePath}");

                            // History is updated once the import completes (see OnImportPackageCompleted)
                            AddPendingImport(safeCreator, safeFilename);
                        }
                        else
                        {
                            Debug.LogError($"[Booth Admin] Failed to download {safeFilename}: {dlReq.error}");
                        }
                    }
                }

                Debug.Log("[Booth Admin] All downloads finished.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Booth Admin] Error during download: {ex.Message}");
            EditorUtility.DisplayDialog("Download Error", ex.Message, "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ==========================================
    // STEP 3: IMPORT PACKAGES
    // ==========================================
    private void ImportDownloadedPackages()
    {
        if (!Directory.Exists(DownloadTempPath))
        {
            Debug.LogWarning("[Booth Admin] Download folder not found. Run Step 2 first.");
            return;
        }

        if (Directory.GetFiles(DownloadTempPath, "*.unitypackage").Length == 0)
        {
            Debug.LogWarning("[Booth Admin] No .unitypackage files found in the Temp directory.");
            return;
        }

        ImportPackages(ReviewDownloadedPackages().Select(a => a.packagePath).ToList());
    }

    // Inspects every downloaded package (see BoothPackageInspector) and asks about anything that needs review.
    // Blocked and skipped packages are removed from the download folder; the approved ones are returned.
    private List<(string creator, string packagePath)> ReviewDownloadedPackages()
    {
        List<(string creator, string packagePath)> approved = new List<(string, string)>();
        DownloadHistory pending = LoadRecords(PendingImportsPath);

        foreach (string packagePath in Directory.GetFiles(DownloadTempPath, "*.unitypackage"))
        {
            string filename = Path.GetFileName(packagePath);
            HistoryRecord record = pending.records.FirstOrDefault(r => r.lastFilename == filename);
            if (record == null)
            {
                Debug.LogError($"[Booth Admin] Don't know which creator {filename} belongs to. Download it again.");
                DiscardPackage(packagePath);
                continue;
            }

            EditorUtility.DisplayProgressBar("Booth Review", $"Inspecting {filename}...", 0.5f);
            BoothPackageInspector.Report report = BoothPackageInspector.Inspect(packagePath, record.creator);
            EditorUtility.ClearProgressBar();

            if (report.errors.Count > 0)
            {
                Debug.LogError($"[Booth Admin] {filename} is BLOCKED and will not be imported:\n- " + string.Join("\n- ", report.errors));
                EditorUtility.DisplayDialog($"Booth Blocked: {record.creator}",
                    $"{filename} will not be imported:\n\n- " + string.Join("\n- ", report.errors.Take(10)) +
                    (report.errors.Count > 10 ? $"\n...and {report.errors.Count - 10} more (see the Console)." : ""), "OK");
                DiscardPackage(packagePath);
                continue;
            }

            if (!report.NeedsReview)
            {
                Debug.Log($"[Booth Admin] {filename}: {report.scripts.Count} scripts, nothing flagged.");
                approved.Add((record.creator, packagePath));
                continue;
            }

            if (AskToImport(filename, record.creator, report))
            {
                approved.Add((record.creator, packagePath));
            }
            else
            {
                Debug.LogWarning($"[Booth Admin] Skipped {filename}. It will be offered again on the next sync.");
                DiscardPackage(packagePath);
            }
        }
        return approved;
    }

    private static bool AskToImport(string filename, string creator, BoothPackageInspector.Report report)
    {
        List<string> lines = new List<string>();
        lines.AddRange(report.flags.Select(f => $"- {f.path.Replace($"Assets/Booth/{creator}/", "")}: {string.Join(", ", f.reasons)}"));
        lines.AddRange(report.guidCollisions.Select(c => $"- ID collision: {c}"));

        Debug.LogWarning($"[Booth Admin] {filename} needs review:\n" + string.Join("\n", lines));

        string message = $"{filename}\n\n" + string.Join("\n", lines.Take(15)) +
                         (lines.Count > 15 ? $"\n...and {lines.Count - 15} more (see the Console)." : "") +
                         (report.guidCollisions.Count > 0 ? "\n\nID collisions: Unity will update the existing asset instead of adding a new one." : "");

        while (true)
        {
            int choice = report.flaggedScriptContents.Count > 0
                ? EditorUtility.DisplayDialogComplex($"Review Booth: {creator}", message, "Import", "Skip", "Show Flagged Scripts")
                : (EditorUtility.DisplayDialog($"Review Booth: {creator}", message, "Import", "Skip") ? 0 : 1);

            if (choice == 0) return true;
            if (choice == 1) return false;

            // Written outside Assets as .txt so Unity doesn't compile them
            string reviewFolder = BoothPackageInspector.WriteFlaggedScriptsForReview(report, creator);
            EditorUtility.RevealInFinder(reviewFolder);
        }
    }

    private static void DiscardPackage(string packagePath)
    {
        TakePendingImport(Path.GetFileName(packagePath));
        File.Delete(packagePath);
    }

    private void ImportPackages(List<string> packages)
    {
        if (packages.Count == 0)
        {
            Debug.Log("[Booth Admin] Nothing to import.");
            return;
        }

        Debug.Log($"[Booth Admin] Starting import of {packages.Count} packages...");

        foreach (string pkg in packages)
        {
            Debug.Log($"[Booth Admin] Queuing import for: {Path.GetFileName(pkg)}");
            AssetDatabase.ImportPackage(pkg, false); 
        }

        Debug.Log("[Booth Admin] Import commands issued. Unity is processing them in the background.");
    }

    // ==========================================
    // STEP 4: SPAWN PREFABS
    // ==========================================
    private void SpawnPrefabsToScene()
    {
        Debug.Log("[Booth Admin] Scanning for Booth descriptors in prefabs...");

        if (!AssetDatabase.IsValidFolder(BoothAssetRoot))
        {
            Debug.LogError("[Booth Admin] Assets/Booth folder does not exist.");
            return;
        }

        BoothDescriptor[] existingInScene = FindObjectsOfType<BoothDescriptor>();
        List<string> existingIdentifiers = new List<string>();
        foreach (var desc in existingInScene)
        {
            existingIdentifiers.Add($"{desc.creatorName}_{desc.boothName}");
        }

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { BoothAssetRoot });
        int spawnedCount = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (prefabAsset != null)
            {
                BoothDescriptor descriptor = prefabAsset.GetComponentInChildren<BoothDescriptor>(true);
                
                if (descriptor != null)
                {
                    string identifier = $"{descriptor.creatorName}_{descriptor.boothName}";
                    
                    if (existingIdentifiers.Contains(identifier))
                    {
                        Debug.Log($"[Booth Admin] Skipped: '{identifier}' is already in the scene.");
                    }
                    else
                    {
                        Debug.Log($"[Booth Admin] Spawning: '{identifier}' from {path}");
                        
                        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                        
                        existingIdentifiers.Add(identifier); 
                        spawnedCount++;
                    }
                }
            }
        }

        Debug.Log($"[Booth Admin] Spawn complete. Added {spawnedCount} new booths to the scene.");
    }

    // ==========================================
    // JSON DATA STRUCTURES
    // ==========================================
    [System.Serializable]
    private class BoothListWrapper
    {
        public List<BoothEntry> booths;
    }

    [System.Serializable]
    private class BoothEntry
    {
        public string creator;
        public string filename;
        public string downloadUrl;
    }

    [System.Serializable]
    private class DownloadHistory
    {
        public List<HistoryRecord> records = new List<HistoryRecord>();
    }

    [System.Serializable]
    private class HistoryRecord
    {
        public string creator;
        public string lastFilename;
    }
}