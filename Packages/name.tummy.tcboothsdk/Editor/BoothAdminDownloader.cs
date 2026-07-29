using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

public class BoothAdminDownloader : EditorWindow
{
    private string serverUrl = "https://upload.tummy.name";
    private string adminUsername = "";
    private string adminPassword = "";

    private const string PrefKey_AdminUser = "BoothSDK_AdminUser";
    private const string PrefKey_AdminPass = "BoothSDK_AdminPass";
    private const string DownloadTempPath = "Temp/BoothDownloads";
    private const string BoothAssetRoot = "Assets/Booth";

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

        // --- STEP 1: CLEAN UP ---
        GUILayout.Label("Step 1: Clean Up", EditorStyles.boldLabel);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Delete Assets (Project)", GUILayout.Height(30)))
        {
            DeleteExistingBooths();
        }
        if (GUILayout.Button("Delete Objects (Scene)", GUILayout.Height(30)))
        {
            ClearBoothsFromScene();
        }
        GUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // --- STEP 2: DOWNLOAD ---
        GUILayout.Label("Step 2: Retrieve Data", EditorStyles.boldLabel);
        if (GUILayout.Button("Download All Booth Packages", GUILayout.Height(30)))
        {
            _ = DownloadAllBoothsAsync();
        }

        EditorGUILayout.Space();

        // --- STEP 3: IMPORT ---
        GUILayout.Label("Step 3: Asset Import", EditorStyles.boldLabel);
        if (GUILayout.Button("Import Downloaded Packages", GUILayout.Height(30)))
        {
            ImportDownloadedPackages();
        }

        EditorGUILayout.Space();

        // --- STEP 4: SPAWN ---
        GUILayout.Label("Step 4: Scene Setup", EditorStyles.boldLabel);
        if (GUILayout.Button("Spawn Prefabs into Scene", GUILayout.Height(30)))
        {
            SpawnPrefabsToScene();
        }
    }

    // ==========================================
    // STEP 1A: DELETE EXISTING BOOTHS (PROJECT)
    // ==========================================
    private void DeleteExistingBooths()
    {
        Debug.Log("[Booth Admin] Starting deletion of existing booth assets...");

        if (AssetDatabase.IsValidFolder(BoothAssetRoot))
        {
            // Find all subfolders inside Assets/Booth and delete them
            string[] subfolders = AssetDatabase.GetSubFolders(BoothAssetRoot);
            foreach (string folder in subfolders)
            {
                Debug.Log($"[Booth Admin] Deleting folder: {folder}");
                AssetDatabase.DeleteAsset(folder);
            }
            AssetDatabase.Refresh();
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
            // DestroyImmediate must be used in Editor scripts instead of Destroy
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
            
            // 1. Fetch the JSON list
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

                // Unity's JsonUtility cannot parse raw arrays natively.
                // We wrap the JSON array in an object string to parse it cleanly.
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

                // 2. Prepare Temp folder
                if (!Directory.Exists(DownloadTempPath)) Directory.CreateDirectory(DownloadTempPath);

                // 3. Download each file
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
                        }
                        else
                        {
                            Debug.LogError($"[Booth Admin] Failed to download {booth.filename}: {dlReq.error}");
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
            // interactive: false tells Unity not to show the popup window asking what to import
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

        // 1. Find all BoothDescriptors already currently active in the scene
        BoothDescriptor[] existingInScene = FindObjectsOfType<BoothDescriptor>();
        List<string> existingIdentifiers = new List<string>();
        foreach (var desc in existingInScene)
        {
            // Create a unique hash/string to identify the booth (Creator + BoothName)
            existingIdentifiers.Add($"{desc.creatorName}_{desc.boothName}");
        }

        // 2. Search for all prefabs inside Assets/Booth
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
                        
                        existingIdentifiers.Add(identifier); // Prevent duplicates in the same pass
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
}