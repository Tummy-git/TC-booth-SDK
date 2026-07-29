using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

public class BoothExporterWindow : EditorWindow
{
    private string serverUsername = "";
    private string serverPassword = "";
    private const string ServerEndpoint = "https://upload.tummy.name/upload-booth"; 
    private const string BackupFolderName = "BoothExports";
    
    // Unified root folder name
    private const string ROOT_FOLDER = "Booth"; 

    private const string PrefKey_User = "BoothSDK_Username";
    private const string PrefKey_Pass = "BoothSDK_Password";

    [MenuItem("Booth SDK/Open Booth Exporter")]
    public static void ShowWindow()
    {
        GetWindow<BoothExporterWindow>("Booth Exporter");
    }

    private void OnEnable()
    {
        serverUsername = EditorPrefs.GetString(PrefKey_User, "");
        serverPassword = EditorPrefs.GetString(PrefKey_Pass, "");
    }

    private void OnGUI()
    {
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Read Terms & Conditions", GUILayout.Width(180), GUILayout.Height(25)))
        {
            ShowTermsAndConditions();
        }
        if (GUILayout.Button("Open SDK Readme", GUILayout.Height(25)))
        {
            ShowReadme();
        }
        GUILayout.EndHorizontal();
        
        EditorGUILayout.Space();

        // --- CREATOR TOOLS SECTION ---
        GUILayout.Label("Creator Tools", EditorStyles.boldLabel);
        if (GUILayout.Button("Spawn Booth Reference Area", GUILayout.Height(30)))
        {
            SpawnReferencePrefab();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space();

        GUILayout.Label("Server Authentication", EditorStyles.boldLabel);
        
        EditorGUI.BeginChangeCheck();
        serverUsername = EditorGUILayout.TextField("Username", serverUsername);
        serverPassword = EditorGUILayout.PasswordField("Password", serverPassword);
        
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetString(PrefKey_User, serverUsername);
            EditorPrefs.SetString(PrefKey_Pass, serverPassword);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        EditorGUILayout.Space();

        if (GUILayout.Button("Build and Export Booth", GUILayout.Height(40)))
        {
            _ = ExportBoothAsync();
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Open Local Backup Folder", GUILayout.Height(25)))
        {
            string path = Path.GetFullPath(BackupFolderName);
            if (Directory.Exists(path)) EditorUtility.RevealInFinder(path);
            else EditorUtility.DisplayDialog("Folder Not Found", "No backups exist yet. Export a booth first.", "OK");
        }
    }

    private void ShowTermsAndConditions()
    {
        string terms = "By uploading your virtual booth, you agree to the following:\n\n" +
                       "1. You hold the rights or licenses for all assets included.\n" +
                       "2. You are responsible for the performance and compatibility of your booth.\n" +
                       "3. The organizers reserve the right to remove any booth that violates community guidelines or performs poorly.\n\n" +
                       "Please review the full documentation provided by the admin team.";
                       
        EditorUtility.DisplayDialog("Terms & Conditions", terms, "I Agree", "Close");
    }

    private void ShowReadme()
    {
        string readme = "BOOTH SDK GUIDE\n\n" +
                        "1. FOLDER STRUCTURE:\n" +
                        $"All your assets must be placed inside 'Assets/{ROOT_FOLDER}/[YourUsername]/'.\n" +
                        "If you place files outside this folder, the SDK will reject the export.\n\n" +
                        "2. LOGIC & SHADERS:\n" +
                        "Custom scripts and shaders are permitted. You are responsible for their stability. Any booth causing crashes or performance issues will be removed.\n" +
                        "Easiest is to use shaders in VPM packages. Make sure to send a link to the VPM my way so I can add it.\n" +
                        "If you use Poiyomi shaders. Locking in is the way to do it.\n\n" +
                        "3. EXPORT PROCESS:\n" +
                        "- Ensure your booth is an active GameObject with a 'BoothDescriptor' component. Fill in your VRC user name and the name of the booth.\n" +
                        "- Make sure the ConBoothArea prefab isn't part of your booth. It's just there for reference.\n" +
                        "- Enter your credentials, verify your assets are in the correct folder, and click 'Build and Export'.";

        EditorUtility.DisplayDialog("SDK Readme", readme, "Understood");
    }

    private async Task ExportBoothAsync()
    {
        if (string.IsNullOrEmpty(serverUsername) || string.IsNullOrEmpty(serverPassword))
        {
            EditorUtility.DisplayDialog("Error", "Username and Password are required.", "OK");
            return;
        }

        BoothDescriptor descriptor = FindActiveBooth();
        if (descriptor == null)
        {
            EditorUtility.DisplayDialog("Error", "No active BoothDescriptor found in the scene.", "OK");
            return;
        }

        if (!ValidateComponents(descriptor.gameObject, out string componentError))
        {
            EditorUtility.DisplayDialog("Component Violation", componentError, "OK");
            return;
        }

        string safeUsername = string.Join("_", serverUsername.Split(Path.GetInvalidFileNameChars()));
        string userRootFolder = $"Assets/{ROOT_FOLDER}/{safeUsername}";
        
        string rawPrefabName = $"{descriptor.creatorName} - {descriptor.boothName}";
        string safePrefabName = string.Join("_", rawPrefabName.Split(Path.GetInvalidFileNameChars()));
        string prefabPath = $"{userRootFolder}/{safePrefabName}.prefab";
        
        string exportFolderPath = "Temp/BoothPackages";
        
        // VPM Manifest Path
        string manifestDestPath = $"{userRootFolder}/{safePrefabName}_VPM.json";

        try
        {
            EditorUtility.DisplayProgressBar("Booth Exporter", "Generating Prefab...", 0.2f);

            if (!AssetDatabase.IsValidFolder(userRootFolder))
            {
                if (!AssetDatabase.IsValidFolder($"Assets/{ROOT_FOLDER}")) 
                    AssetDatabase.CreateFolder("Assets", ROOT_FOLDER);
                
                AssetDatabase.CreateFolder($"Assets/{ROOT_FOLDER}", safeUsername);
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(descriptor.gameObject, prefabPath, InteractionMode.AutomatedAction);
            if (prefab == null) throw new System.Exception("Failed to generate booth Prefab.");

            if (!ValidateFolderStructure(prefabPath, userRootFolder, out string folderError))
            {
                EditorUtility.DisplayDialog("Folder Structure Error", folderError, "OK");
                return;
            }

            // --- INJECT VPM MANIFEST ---
            string vpmManifestSrc = "Packages/vpm-manifest.json";
            if (File.Exists(vpmManifestSrc))
            {
                File.Copy(vpmManifestSrc, manifestDestPath, true);
                AssetDatabase.ImportAsset(manifestDestPath, ImportAssetOptions.ForceUpdate);
            }

            if (!Directory.Exists(exportFolderPath)) Directory.CreateDirectory(exportFolderPath);
            string packageFilePath = Path.Combine(exportFolderPath, "booth_package.unitypackage");

            EditorUtility.DisplayProgressBar("Booth Exporter", "Resolving dependencies...", 0.4f);
            
            string[] rawDependencies = AssetDatabase.GetDependencies(prefabPath, true);
            List<string> filteredDependencies = new List<string>();

            foreach (string dep in rawDependencies)
            {
                if (ShouldSkipDependency(dep)) continue;
                if (dep.StartsWith("Assets/")) filteredDependencies.Add(dep);
            }

            // Force the injected manifest into the package
            if (File.Exists(manifestDestPath) && !filteredDependencies.Contains(manifestDestPath))
            {
                filteredDependencies.Add(manifestDestPath);
            }

            EditorUtility.DisplayProgressBar("Booth Exporter", "Packaging filtered assets...", 0.5f);
            AssetDatabase.ExportPackage(filteredDependencies.ToArray(), packageFilePath, ExportPackageOptions.Default);

            CreateLocalBackup(packageFilePath, descriptor.boothName);
            
            EditorUtility.DisplayProgressBar("Booth Exporter", "Loading package into memory...", 0.7f);
            byte[] packageData = await File.ReadAllBytesAsync(packageFilePath);

            await UploadToServer(packageData, descriptor);
            Debug.Log("[Booth SDK] Export and upload completed successfully.");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Booth SDK] Export/Upload failed: {ex.Message}");
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Process Halted", ex.Message, "OK");
        }
        finally
        {
            // Clean up the temporary VPM file so the user's project stays clean
            if (File.Exists(manifestDestPath))
            {
                AssetDatabase.DeleteAsset(manifestDestPath);
            }
            
            EditorUtility.ClearProgressBar();
            if (Directory.Exists(exportFolderPath)) Directory.Delete(exportFolderPath, true);
            AssetDatabase.Refresh();
        }
    }

    private bool ShouldSkipDependency(string dep)
    {
        string[] skipPrefixes = new string[] 
        {
            "Packages/",
            "Resources/",
            "Assets/TextMesh Pro/",
            "Assets/SerializedUdonPrograms/"
        };

        foreach (string prefix in skipPrefixes)
        {
            if (dep.StartsWith(prefix)) return true;
        }

        if (dep.Contains("BoothDescriptor.cs")) return true;
        if (dep.EndsWith("_VPM.json")) return true; // Handled manually

        return false;
    }

    private bool ValidateComponents(GameObject rootObject, out string errorMessage)
    {
        errorMessage = "";
        string[] blacklist = new string[] { "VRC.SDK3.Components.VRCSceneDescriptor" };
        Component[] components = rootObject.GetComponentsInChildren<Component>(true);

        foreach (Component c in components)
        {
            if (c == null) continue;
            string componentType = c.GetType().FullName;
            foreach (string forbidden in blacklist)
            {
                if (componentType.Contains(forbidden))
                {
                    errorMessage = $"Component Violation: '{c.GetType().Name}' is not allowed on your booth.\n\n" +
                                   $"This component (found on '{c.gameObject.name}') is forbidden because it " +
                                   $"would conflict with the Master Project settings.";
                    return false;
                }
            }
        }
        return true;
    }
    
    private bool ValidateFolderStructure(string prefabPath, string requiredRootPath, out string errorMessage)
    {
        errorMessage = "";
        string[] rawDependencies = AssetDatabase.GetDependencies(prefabPath, true);

        foreach (string dep in rawDependencies)
        {
            if (ShouldSkipDependency(dep)) continue; 

            if (dep.StartsWith("Assets/"))
            {
                if (!dep.StartsWith(requiredRootPath))
                {
                    errorMessage = $"Asset Violation: '{dep}' is located outside your designated root folder.\n\n" +
                                   $"All booth assets must be placed strictly inside:\n{requiredRootPath}/\n\n" +
                                   $"Please move the asset, update your scene object, and try again.";
                    return false;
                }
            }
        }
        return true;
    }

    private void CreateLocalBackup(string sourceFilePath, string rawBoothName)
    {
        if (!Directory.Exists(BackupFolderName)) Directory.CreateDirectory(BackupFolderName);
        DirectoryInfo dirInfo = new DirectoryInfo(BackupFolderName);
        foreach (FileInfo file in dirInfo.GetFiles()) file.Delete();

        string safeBoothName = string.Join("_", rawBoothName.Split(Path.GetInvalidFileNameChars()));
        string dateString = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
        string backupFilePath = Path.Combine(BackupFolderName, $"{safeBoothName}_{dateString}.unitypackage");

        File.Copy(sourceFilePath, backupFilePath, true);
    }

    private BoothDescriptor FindActiveBooth()
    {
        BoothDescriptor[] descriptors = FindObjectsOfType<BoothDescriptor>();
        foreach (var desc in descriptors)
        {
            if (desc.gameObject.activeInHierarchy) return desc;
        }
        return null;
    }
    
    private void SpawnReferencePrefab()
    {
        if (GameObject.Find("ConBoothArea") != null || GameObject.Find("ConBoothArea(Clone)") != null)
        {
            EditorUtility.DisplayDialog("Already Exists", "The reference area is already present in your scene.", "OK");
            return;
        }

        string[] guids = AssetDatabase.FindAssets("ConBoothArea t:Prefab");
        
        if (guids.Length == 0)
        {
            EditorUtility.DisplayDialog("Not Found", "Could not find the 'ConBoothArea' prefab.\n\nPlease ensure the required VPM package is installed or the prefab is in your project.", "OK");
            return;
        }

        string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);

        if (prefabAsset != null)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            instance.name = "ConBoothArea"; 
            Selection.activeGameObject = instance;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            Debug.Log("[Booth SDK] Reference area spawned successfully.");
        }
    }
    
    private async Task UploadToServer(byte[] packageData, BoothDescriptor descriptor)
    {
        WWWForm form = new WWWForm();
        form.AddField("username", serverUsername);
        form.AddField("password", serverPassword);
        form.AddField("boothName", descriptor.boothName);
        form.AddField("creatorName", descriptor.creatorName);
        form.AddBinaryData("boothFile", packageData, "booth_package.unitypackage", "application/octet-stream");

        using (UnityWebRequest request = UnityWebRequest.Post(ServerEndpoint, form))
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                float progress = request.uploadProgress;
                bool canceled = EditorUtility.DisplayCancelableProgressBar("Booth Exporter", $"Uploading... {(progress * 100):F1}%", progress);
                if (canceled) { request.Abort(); throw new System.Exception("Upload canceled."); }
                await Task.Delay(100); 
            }
            EditorUtility.ClearProgressBar();
            if (request.result == UnityWebRequest.Result.Success) EditorUtility.DisplayDialog("Upload Complete", "Upload successful.", "OK");
            else throw new System.Exception($"Server rejected the upload: {request.error}");
        }
    }
}