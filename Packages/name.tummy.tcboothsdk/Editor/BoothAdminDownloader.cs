using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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
            List<string> foldersToDelete = new List<string>();

            // PHASE 1: BATCH CHECK & DOWNLOAD
            for (int i = 0; i < wrapper.booths.Count; i++)
            {
                BoothEntry booth = wrapper.booths[i];
                float progress = 0.2f + (0.5f * ((float)i / wrapper.booths.Count));
                
                string safeCreator = string.Join("_", booth.creator.Split(Path.GetInvalidFileNameChars()));
                HistoryRecord existingRecord = history.records.FirstOrDefault(r => r.creator == safeCreator);

                // Check: Has the file changed?
                if (existingRecord != null && existingRecord.lastFilename == booth.filename)
                {
                    Debug.Log($"[Smart Sync] Skipping {booth.creator} (Already up to date)");
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
                        string savePath = Path.Combine(DownloadTempPath, booth.filename);
                        File.WriteAllBytes(savePath, dlReq.downloadHandler.data);
                        downloadedCount++;

                        // Only mark the folder for deletion if the download ACTUALLY succeeded
                        string userFolder = $"{BoothAssetRoot}/{safeCreator}";
                        if (AssetDatabase.IsValidFolder(userFolder))
                        {
                            foldersToDelete.Add(userFolder);
                        }

                        // Update History
                        if (existingRecord != null) existingRecord.lastFilename = booth.filename;
                        else history.records.Add(new HistoryRecord { creator = safeCreator, lastFilename = booth.filename });
                    }
                    else
                    {
                        Debug.LogError($"[Smart Sync] Failed to download {booth.filename}: {dlReq.error}");
                    }
                }
            }

            // Save history right after downloads finish
            SaveHistory(history);

            if (downloadedCount > 0)
            {
                // PHASE 2: BATCH DELETE
                EditorUtility.DisplayProgressBar("Smart Sync", "Cleaning up outdated folders...", 0.75f);
                foreach (string folder in foldersToDelete)
                {
                    Debug.Log($"[Smart Sync] Deleting outdated folder: {folder}");
                    AssetDatabase.DeleteAsset(folder);
                }

                EditorUtility.DisplayProgressBar("Smart Sync", "Refreshing Asset Database...", 0.85f);
                AssetDatabase.Refresh(); 
                
                // PHASE 3: BATCH IMPORT
                EditorUtility.DisplayProgressBar("Smart Sync", "Importing new packages...", 0.95f);
                ImportDownloadedPackages();
                
                Debug.Log($"[Smart Sync] Complete! Downloaded and queued {downloadedCount} new updates for import.");
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
    }

    private DownloadHistory LoadHistory()
    {
        if (File.Exists(TrackingFilePath))
        {
            string json = File.ReadAllText(TrackingFilePath);
            return JsonUtility.FromJson<DownloadHistory>(json) ?? new DownloadHistory();
        }
        return new DownloadHistory();
    }

    private void SaveHistory(DownloadHistory history)
    {
        string json = JsonUtility.ToJson(history, true);
        File.WriteAllText(TrackingFilePath, json);
    }
    // ==========================================


    // ==========================================
    // STEP 1A: DELETE EXISTING BOOTHS (PROJECT)
    // ==========================================
    private void DeleteExistingBooths()
    {
        Debug.Log("[Booth Admin] Starting deletion of existing booth assets...");

        if (AssetDatabase.IsValidFolder(BoothAssetRoot))
        {
            string[] subfolders = AssetDatabase.GetSubFolders(BoothAssetRoot);
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

        int count = 0;
        foreach (var desc in existingInScene)
        {
            DestroyImmediate(desc.gameObject);
            count++;
        }
        
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
                DownloadHistory history = LoadHistory();

                for (int i = 0; i < wrapper.booths.Count; i++)
                {
                    BoothEntry booth = wrapper.booths[i];
                    float progress = 0.2f + (0.8f * ((float)i / wrapper.booths.Count));
                    
                    EditorUtility.DisplayProgressBar("Admin Downloader", $"Downloading {booth.filename}...", progress);
                    Debug.Log($"[Booth Admin] Downloading: {booth.filename}");

                    using (UnityWebRequest dlReq = UnityWebRequest.Get(booth.downloadUrl))
                    {
                        dlReq.SetRequestHeader("x-username", adminUsername);
                        dlReq.SetRequestHeader("x-password", adminPassword);

                        var dlOp = dlReq.SendWebRequest();
                        while (!dlOp.isDone) await Task.Delay(50);

                        if (dlReq.result == UnityWebRequest.Result.Success)
                        {
                            string savePath = Path.Combine(DownloadTempPath, booth.filename);
                            File.WriteAllBytes(savePath, dlReq.downloadHandler.data);
                            Debug.Log($"[Booth Admin] Saved to: {savePath}");
                            
                            // Update history even on manual force download
                            string safeCreator = string.Join("_", booth.creator.Split(Path.GetInvalidFileNameChars()));
                            HistoryRecord existingRecord = history.records.FirstOrDefault(r => r.creator == safeCreator);
                            if (existingRecord != null) existingRecord.lastFilename = booth.filename;
                            else history.records.Add(new HistoryRecord { creator = safeCreator, lastFilename = booth.filename });
                        }
                        else
                        {
                            Debug.LogError($"[Booth Admin] Failed to download {booth.filename}: {dlReq.error}");
                        }
                    }
                }
                
                SaveHistory(history);
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

        string[] packages = Directory.GetFiles(DownloadTempPath, "*.unitypackage");
        if (packages.Length == 0)
        {
            Debug.LogWarning("[Booth Admin] No .unitypackage files found in the Temp directory.");
            return;
        }

        Debug.Log($"[Booth Admin] Starting import of {packages.Length} packages...");

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