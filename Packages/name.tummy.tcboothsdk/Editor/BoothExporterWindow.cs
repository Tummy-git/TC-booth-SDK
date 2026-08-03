using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

public class BoothExporterWindow : EditorWindow
{
    private string serverUsername = "";
    private string serverPassword = "";
    private bool cleanMaterials = true;
    
    private const string ServerBaseUrl = "https://upload.tummy.name";
    private const string ServerEndpoint = ServerBaseUrl + "/upload-booth"; 
    private const string LimitsEndpoint = ServerBaseUrl + "/api/limits"; 
    
    private const string BackupFolderName = "BoothExports";
    private const string ROOT_FOLDER = "Booth"; 

    private const string PrefKey_User = "BoothSDK_Username";
    private const string PrefKey_Pass = "BoothSDK_Password";
    private const string PrefKey_CleanMat = "BoothSDK_CleanMaterials";

    private Regex SHADER_INCLUDE_REGEX = new Regex(@"^\s*#\s*include\s*""(.*)""$");

    [MenuItem("Booth SDK/Open Booth Exporter")]
    public static void ShowWindow()
    {
        GetWindow<BoothExporterWindow>("Booth Exporter");
    }

    private void OnEnable()
    {
        serverUsername = EditorPrefs.GetString(PrefKey_User, "");
        serverPassword = EditorPrefs.GetString(PrefKey_Pass, "");
        cleanMaterials = EditorPrefs.GetBool(PrefKey_CleanMat, true);
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

        // --- Optimization UI ---
        GUILayout.Label("Optimization", EditorStyles.boldLabel);
        
        EditorGUI.BeginChangeCheck();
        cleanMaterials = EditorGUILayout.ToggleLeft(" Clean unused ghost properties from materials (Recommended)", cleanMaterials);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(PrefKey_CleanMat, cleanMaterials);
        }
        
        GUIStyle wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11, fontStyle = FontStyle.Italic };
        GUILayout.Label("Automatically removes hidden textures left behind when changing shaders. You probably want this enabled to prevent your file size from bloating with unused assets!", wrapStyle);
        
        EditorGUILayout.Space();
        // ----------------------------

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

            if (cleanMaterials)
            {
                EditorUtility.DisplayProgressBar("Booth Exporter", "Cleaning Material Properties...", 0.25f);
                CleanGhostPropertiesInDependencies(prefabPath, userRootFolder);
            }

            string dependencyError;
            HashSet<string> dependencies = GetDependencies(prefabPath, out dependencyError);
            if (dependencies == null)
            {
                EditorUtility.DisplayDialog("Dependency Error", dependencyError, "OK");
                return;
            }

            if (!ValidateFolderStructure(dependencies, userRootFolder, out string folderError))
            {
                EditorUtility.DisplayDialog("Folder Structure Error", folderError, "OK");
                return;
            }

            string vpmManifestSrc = "Packages/vpm-manifest.json";
            if (File.Exists(vpmManifestSrc))
            {
                File.Copy(vpmManifestSrc, manifestDestPath, true);
                AssetDatabase.ImportAsset(manifestDestPath, ImportAssetOptions.ForceUpdate);
            }

            if (!Directory.Exists(exportFolderPath)) Directory.CreateDirectory(exportFolderPath);
            string packageFilePath = Path.Combine(exportFolderPath, "booth_package.unitypackage");

            EditorUtility.DisplayProgressBar("Booth Exporter", "Resolving dependencies...", 0.4f);
            
            List<string> shaderDebugList = new List<string>(); 
            foreach (string dep in dependencies)
            {
                if (dep.EndsWith(".shader") || dep.EndsWith(".cginc") || dep.EndsWith(".hlsl"))
                {
                    shaderDebugList.Add(dep);
                }
            }
            if (shaderDebugList.Count > 0)
            {
                Debug.Log($"[Booth SDK] Bundling {shaderDebugList.Count} custom shader files:\n" + string.Join("\n", shaderDebugList));
            }

            if (File.Exists(manifestDestPath))
            {
                dependencies.Add(manifestDestPath);
            }

            EditorUtility.DisplayProgressBar("Booth Exporter", "Packaging filtered assets...", 0.5f);
            AssetDatabase.ExportPackage(dependencies.ToArray(), packageFilePath, ExportPackageOptions.Default);

            CreateLocalBackup(packageFilePath, descriptor.boothName);
            
            EditorUtility.DisplayProgressBar("Booth Exporter", "Checking server limits...", 0.7f);
            byte[] packageData = await File.ReadAllBytesAsync(packageFilePath);

            bool isSizeValid = await CheckServerLimitsAsync(packageData.Length);
            if (!isSizeValid) return;

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
            if (File.Exists(manifestDestPath))
            {
                AssetDatabase.DeleteAsset(manifestDestPath);
            }
            
            EditorUtility.ClearProgressBar();
            if (Directory.Exists(exportFolderPath)) Directory.Delete(exportFolderPath, true);
            AssetDatabase.Refresh();
        }
    }

    private HashSet<string> GetDependencies(string prefabPath, out string errorMessage)
    {
        errorMessage = "";
        string[] rawDependencies = AssetDatabase.GetDependencies(prefabPath, true);
        HashSet<string> dependencies = new HashSet<string>(rawDependencies);

        foreach (string dep in rawDependencies)
        {
            if (dep.EndsWith(".shader"))
            {
                if (!GetShaderDependencies(dep, dependencies, out errorMessage)) return null;
            }
        }

        dependencies.RemoveWhere(dep => ShouldSkipDependency(dep));
        return dependencies;
    }

    private bool GetShaderDependencies(string dep, HashSet<string> dependencies, out string errorMessage)
    {
        errorMessage = "";

        foreach (string line in File.ReadLines(dep))
        {
            Match m = SHADER_INCLUDE_REGEX.Match(line);
            if (m.Success)
            {
                string includeName = m.Groups[1].Value;
                includeName = includeName.TrimStart('/');
                string assetPath = Path.GetRelativePath(".", Path.Join(Path.GetDirectoryName(dep), includeName));

                if (!File.Exists(assetPath))
                {
                    assetPath = Path.GetRelativePath(".", includeName);
                    if (!File.Exists(assetPath))
                    {
                        if (!File.Exists(Path.Join(EditorApplication.applicationContentsPath, "CGIncludes", includeName)))
                        {
                            errorMessage = $"Cannot find dependency '{includeName}' included from '{dep}'";
                            return false;
                        }
                        else
                        {
                            continue;
                        }
                    }
                }

                assetPath = assetPath.Replace("\\", "/");

                if (!ShouldSkipDependency(assetPath))
                {
                    if (dependencies.Add(assetPath))
                    {
                        if (!GetShaderDependencies(assetPath, dependencies, out errorMessage)) return false;
                    }
                }
            }
        }
        return true;
    }

    private async Task<bool> CheckServerLimitsAsync(long localSizeBytes)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(LimitsEndpoint))
        {
            var operation = req.SendWebRequest();
            while (!operation.isDone) await Task.Delay(50);

            if (req.result == UnityWebRequest.Result.Success)
            {
                ServerLimits limits = JsonUtility.FromJson<ServerLimits>(req.downloadHandler.text);
                
                if (localSizeBytes > limits.maxSizeBytes)
                {
                    float localSizeMb = localSizeBytes / (1024f * 1024f);
                    long overageBytes = localSizeBytes - limits.maxSizeBytes;
                    
                    string overageText;
                    if (overageBytes >= 1048576) overageText = $"{overageBytes / 1048576f:F2} MB";
                    else if (overageBytes >= 1024) overageText = $"{overageBytes / 1024f:F1} KB";
                    else overageText = $"{overageBytes} Bytes";
                    
                    EditorUtility.DisplayDialog("Upload Failed", 
                        $"File could not be uploaded. The server accepts unitypackages up to {limits.maxSizeMb} MB.\n\n" +
                        $"The unitypackage you just created is {localSizeMb:F2} MB.\n" +
                        $"You are over the limit by exactly {overageText}.\n\n" +
                        $"Please try to lower the resolution of your source files and try again.", "OK");
                        
                    return false;
                }
            }
            else
            {
                Debug.LogWarning($"[Booth SDK] Could not fetch server limits for pre-check: {req.error}. Attempting upload anyway.");
            }
        }
        return true;
    }

    [System.Serializable]
    private class ServerLimits
    {
        public int maxSizeMb;
        public long maxSizeBytes;
    }

    private bool ShouldSkipDependency(string dep)
    {
        if (!dep.StartsWith("Assets/")) return true;

        string[] skipPrefixes = new string[] 
        {
            "Assets/TextMesh Pro/",
            "Assets/SerializedUdonPrograms/",
            "Assets/Mochie/",
            "Assets/Bakery/",
            "Assets/BakeryLightmaps"
        };

        foreach (string prefix in skipPrefixes)
        {
            if (dep.StartsWith(prefix)) return true;
        }

        if (dep.Contains("BoothDescriptor.cs")) return true;
        if (dep.EndsWith("_VPM.json")) return true; 

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
    
    private bool ValidateFolderStructure(HashSet<string> dependencies, string requiredRootPath, out string errorMessage)
    {
        errorMessage = "";
        foreach (string dep in dependencies)
        {
            if (!dep.StartsWith(requiredRootPath))
            {
                errorMessage = $"Asset Violation: '{dep}' is located outside your designated root folder.\n\n" +
                               $"All booth assets must be placed strictly inside:\n{requiredRootPath}/\n\n" +
                               $"Please move the asset, update your scene object, and try again.";
                return false;
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

        string packagePath = "Packages/name.tummy.tcboothsdk/Runtime/BoothLimits/ConBoothArea.prefab";
        GameObject prefabAsset = null;

        AssetDatabase.ImportAsset(packagePath, ImportAssetOptions.ForceUpdate);
        prefabAsset = AssetDatabase.LoadMainAssetAtPath(packagePath) as GameObject;

        if (prefabAsset == null)
        {
            string[] guids = AssetDatabase.FindAssets("ConBoothArea t:Prefab");
            foreach (string g in guids)
            {
                string foundPath = AssetDatabase.GUIDToAssetPath(g);
                if (foundPath.EndsWith(".prefab"))
                {
                    AssetDatabase.ImportAsset(foundPath, ImportAssetOptions.ForceUpdate);
                    prefabAsset = AssetDatabase.LoadMainAssetAtPath(foundPath) as GameObject;
                    if (prefabAsset != null) break;
                }
            }
        }

        if (prefabAsset == null)
        {
            EditorUtility.DisplayDialog("Not Found", "Could not find the 'ConBoothArea' prefab.\n\nPlease ensure the required VPM package is installed or the prefab is in your project.", "OK");
            return;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
        instance.name = "ConBoothArea"; 
        Selection.activeGameObject = instance;
        if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
        Debug.Log("[Booth SDK] Reference area spawned successfully.");
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

    private void CleanGhostPropertiesInDependencies(string prefabPath, string allowedRootFolder)
    {
        string[] rawDependencies = AssetDatabase.GetDependencies(prefabPath, true);
        int cleanedCount = 0;

        foreach (string dep in rawDependencies)
        {
            if (dep.EndsWith(".mat") && dep.StartsWith(allowedRootFolder))
            {
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(dep);
                if (mat != null)
                {
                    SerializedObject so = new SerializedObject(mat);
                    so.Update();

                    bool changed = false;
                    changed |= RemoveUnusedProperties(so, "m_SavedProperties.m_TexEnvs", mat);
                    changed |= RemoveUnusedProperties(so, "m_SavedProperties.m_Ints", mat);
                    changed |= RemoveUnusedProperties(so, "m_SavedProperties.m_Floats", mat);
                    changed |= RemoveUnusedProperties(so, "m_SavedProperties.m_Colors", mat);

                    if (changed)
                    {
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(mat);
                        cleanedCount++;
                    }
                }
            }
        }

        if (cleanedCount > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"[Booth SDK] Automatically cleaned hidden ghost properties from {cleanedCount} materials.");
        }
    }

    private bool RemoveUnusedProperties(SerializedObject so, string propertyPath, Material mat)
    {
        SerializedProperty propArray = so.FindProperty(propertyPath);
        if (propArray == null || !propArray.isArray) return false;

        bool changed = false;
        
        for (int i = propArray.arraySize - 1; i >= 0; i--)
        {
            SerializedProperty prop = propArray.GetArrayElementAtIndex(i);
            SerializedProperty nameProp = prop.FindPropertyRelative("first");
            
            if (nameProp != null)
            {
                if (!mat.HasProperty(nameProp.stringValue))
                {
                    propArray.DeleteArrayElementAtIndex(i);
                    changed = true;
                }
            }
        }
        return changed;
    }
}