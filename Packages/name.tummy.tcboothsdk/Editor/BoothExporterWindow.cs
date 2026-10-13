using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
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
    private const int MaxLocalBackups = 5;
    private const string ROOT_FOLDER = "Booth";

    private const string PrefKey_User = "BoothSDK_Username";
    private const string PrefKey_Pass = "BoothSDK_Password";
    private const string PrefKey_CleanMat = "BoothSDK_CleanMaterials";

    private const string PackageJsonPath = "Packages/name.tummy.tcboothsdk/package.json";
    private const string GithubLatestReleaseUrl = "https://api.github.com/repos/Tummy-git/TC-booth-SDK/releases/latest";
    private const string GithubReleasesPage = "https://github.com/Tummy-git/TC-booth-SDK/releases/latest";

    private static bool updateCheckDone = false;
    private static bool updateAvailable = false;
    private static string localVersionStr = "Unknown";
    private static string remoteVersionStr = "Unknown";

    // Matches #include and #include_with_pragmas, with or without a trailing comment
    private Regex SHADER_INCLUDE_REGEX = new Regex(@"^\s*#\s*include(?:_with_pragmas)?\s*""([^""]+)""");

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

        if (!updateCheckDone)
        {
            _ = CheckForUpdatesAsync();
        }
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

        if (GUILayout.Button("Spawn / Update Name Plate", GUILayout.Height(30)))
        {
            BoothDescriptor desc = FindActiveBooth(out bool canceled);
            if (desc != null)
            {
                SpawnOrUpdateNamePlate(desc);
            }
            else if (!canceled)
            {
                EditorUtility.DisplayDialog("Error", "No active BoothDescriptor found in the scene to attach the plate to.", "OK");
            }
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

        if (GUILayout.Button("Build and Export Booth", GUILayout.Height(40)))
        {
            _ = ExportBoothAsync();
        }

        EditorGUILayout.Space();
        
        GUILayout.Label($"When you upload, the unitypackage is saved locally as well (the last {MaxLocalBackups} exports are kept). Click below to find the folder.", wrapStyle);
        
        EditorGUILayout.Space();

        if (GUILayout.Button("Open Local Backup Folder", GUILayout.Height(25)))
        {
            string path = Path.GetFullPath(BackupFolderName);
            if (Directory.Exists(path)) EditorUtility.RevealInFinder(path);
            else EditorUtility.DisplayDialog("Folder Not Found", "No backups exist yet. Export a booth first.", "OK");
        }

        // --- UPDATED: HelpBox Version Display ---
        EditorGUILayout.Space();
        
        if (!updateCheckDone)
        {
            GUILayout.Label("Checking version...", EditorStyles.centeredGreyMiniLabel);
        }
        else if (updateAvailable)
        {
            EditorGUILayout.HelpBox($"Your version is {localVersionStr}, there is a newer version {remoteVersionStr} available", MessageType.Warning);
        }
        else
        {
            GUIStyle boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            GUILayout.Label(localVersionStr, boldStyle);
        }
        // ----------------------------------------
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

        BoothDescriptor descriptor = FindActiveBooth(out bool canceled);
        if (canceled) return;
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

        if (!ValidateShaders(descriptor.gameObject, out string shaderError))
        {
            EditorUtility.DisplayDialog("Missing Shaders", shaderError, "OK");
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
            Transform existingPlate = descriptor.transform.Find("BoothNamePlate");
            if (existingPlate != null)
            {
                UpdateNamePlateText(existingPlate.gameObject, descriptor);
            }
            
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

            HashSet<string> dependencies = GetDependencies(prefabPath);

            string[] codeGuids = AssetDatabase.FindAssets("t:MonoScript t:AssemblyDefinitionAsset t:UdonSharpAssemblyDefinition", new[] { userRootFolder });
            foreach (string guid in codeGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                dependencies.Add(path);
            }
            
            if (!ValidateFolderStructure(dependencies, userRootFolder, out string folderError))
            {
                EditorUtility.DisplayDialog("Folder Structure Error", folderError, "OK");
                return;
            }

            // The server rejects these too; checking here saves the creator an upload
            List<string> blockedFiles = dependencies.Where(BoothPackageInspector.HasBlockedExtension).ToList();
            if (blockedFiles.Count > 0)
            {
                EditorUtility.DisplayDialog("Compiled Code Not Allowed",
                    "Booths can't contain compiled or executable code (DLLs, native plugins, .rsp compiler files and similar). Remove these and try again:\n\n" +
                    string.Join("\n", blockedFiles.Take(15)), "OK");
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

    private HashSet<string> GetDependencies(string prefabPath)
    {
        string[] rawDependencies = AssetDatabase.GetDependencies(prefabPath, true);
        HashSet<string> dependencies = new HashSet<string>(rawDependencies);

        foreach (string dep in rawDependencies)
        {
            if (dep.EndsWith(".shader") || dep.EndsWith(".orlshader") || dep.EndsWith(".cginc") || dep.EndsWith(".hlsl"))
            {
                GetShaderDependencies(dep, dependencies);
            }
        }

        List<string> skippedTmpAssets = dependencies.Where(dep => dep.StartsWith("Assets/TextMesh Pro/")).ToList();
        if (skippedTmpAssets.Count > 0)
        {
            Debug.LogWarning($"[Booth SDK] {skippedTmpAssets.Count} asset(s) in 'Assets/TextMesh Pro/' are NOT included in the upload. " +
                             "The master project uses the standard TMP Essentials. If you made custom fonts or font materials, move them into your booth folder:\n" +
                             string.Join("\n", skippedTmpAssets));
        }

        dependencies.RemoveWhere(dep => ShouldSkipDependency(dep));
        return dependencies;
    }

    private void GetShaderDependencies(string dep, HashSet<string> dependencies)
    {
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
                        // Shaders that actually fail to compile are blocked by ValidateShaders, so a
                        // missing include here is usually inside an #if branch that is never used.
                        if (!File.Exists(Path.Join(EditorApplication.applicationContentsPath, "CGIncludes", includeName)))
                        {
                            Debug.LogWarning($"[Booth SDK] Cannot find '{includeName}' included from '{dep}'. It will not be part of the upload.");
                        }
                        continue;
                    }
                }

                assetPath = assetPath.Replace("\\", "/");

                if (!ShouldSkipDependency(assetPath))
                {
                    if (dependencies.Add(assetPath))
                    {
                        GetShaderDependencies(assetPath, dependencies);
                    }
                }
            }
        }
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

                string localVersion = ReadLocalSdkVersion();
                if (!string.IsNullOrEmpty(limits.minSdkVersion) &&
                    TryParseSdkVersion(localVersion, out System.Version localVer) &&
                    TryParseSdkVersion(limits.minSdkVersion, out System.Version minVer) &&
                    localVer < minVer)
                {
                    EditorUtility.DisplayDialog("SDK Update Required",
                        $"Your Booth SDK is version {localVersion}, but the server requires version {limits.minSdkVersion} or newer.\n\n" +
                        "Please update the 'TummyCon booth SDK' package in VCC / ALCOM and try again.", "OK");
                    return false;
                }

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
        public string minSdkVersion;
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
    
    private bool ValidateShaders(GameObject rootObject, out string errorMessage)
    {
        errorMessage = "";
        List<string> brokenMaterials = new List<string>();

        foreach (Object dep in EditorUtility.CollectDependencies(new Object[] { rootObject }))
        {
            if (dep is Material mat && IsShaderBroken(mat.shader))
            {
                string shaderName = mat.shader != null ? mat.shader.name : "no shader";
                brokenMaterials.Add($"- '{mat.name}' ({AssetDatabase.GetAssetPath(mat)}), shader: {shaderName}");
            }
        }

        if (brokenMaterials.Count == 0) return true;

        errorMessage = "These materials use a shader that is missing or fails to compile (they show up pink):\n\n" +
                       string.Join("\n", brokenMaterials.Distinct().Take(15)) +
                       (brokenMaterials.Count > 15 ? $"\n...and {brokenMaterials.Count - 15} more (see the Console)." : "") +
                       "\n\nInstall the shader package, or assign a working shader, and try again.";
        Debug.LogError("[Booth SDK] Materials with missing or broken shaders:\n" + string.Join("\n", brokenMaterials.Distinct()));
        return false;
    }

    private static bool IsShaderBroken(Shader shader)
    {
        // A missing shader reference resolves to Hidden/InternalErrorShader
        return shader == null || shader.name == "Hidden/InternalErrorShader" || ShaderUtil.ShaderHasError(shader);
    }

    private bool ValidateFolderStructure(HashSet<string> dependencies, string requiredRootPath, out string errorMessage)
    {
        errorMessage = "";
        // Trailing slash so "Assets/Booth/bob" doesn't also match "Assets/Booth/bobby"
        string requiredPrefix = requiredRootPath.TrimEnd('/') + "/";
        foreach (string dep in dependencies)
        {
            if (!dep.StartsWith(requiredPrefix, System.StringComparison.Ordinal))
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

        // Keep the newest backups, leaving room for the one about to be written
        IEnumerable<string> oldBackups = Directory.GetFiles(BackupFolderName, "*.unitypackage")
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .Skip(MaxLocalBackups - 1);
        foreach (string oldBackup in oldBackups) File.Delete(oldBackup);

        string safeBoothName = string.Join("_", rawBoothName.Split(Path.GetInvalidFileNameChars()));
        string dateString = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string backupFilePath = Path.Combine(BackupFolderName, $"{safeBoothName}_{dateString}.unitypackage");

        File.Copy(sourceFilePath, backupFilePath, true);
    }

    // Returns the first active booth in hierarchy order. If there are several, the user is told
    // which one was picked and can cancel (canceled = true, returns null).
    private BoothDescriptor FindActiveBooth(out bool canceled)
    {
        canceled = false;
        List<BoothDescriptor> activeBooths = FindObjectsOfType<BoothDescriptor>()
            .Where(desc => desc.gameObject.activeInHierarchy)
            .ToList();

        if (activeBooths.Count == 0) return null;

        activeBooths.Sort((a, b) => CompareHierarchyPaths(GetHierarchyPath(a.transform), GetHierarchyPath(b.transform)));
        BoothDescriptor selected = activeBooths[0];

        if (activeBooths.Count > 1)
        {
            string others = string.Join("\n", activeBooths.Skip(1).Select(desc => $"- {desc.gameObject.name}"));
            bool proceed = EditorUtility.DisplayDialog("Multiple Active Booths",
                $"Found {activeBooths.Count} active booths in the scene. The first one in the hierarchy was selected:\n\n" +
                $"'{selected.gameObject.name}' ({selected.creatorName} - {selected.boothName})\n\n" +
                $"Ignored:\n{others}\n\n" +
                "To use a different booth, disable the others or move it to the top of the hierarchy.",
                "Continue", "Cancel");

            if (!proceed)
            {
                canceled = true;
                return null;
            }
        }

        return selected;
    }

    // Scene index followed by the sibling index of each transform from the root down
    private static List<int> GetHierarchyPath(Transform transform)
    {
        List<int> path = new List<int>();
        for (Transform current = transform; current != null; current = current.parent)
        {
            path.Insert(0, current.GetSiblingIndex());
        }

        int sceneIndex = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i) == transform.gameObject.scene) sceneIndex = i;
        }
        path.Insert(0, sceneIndex);
        return path;
    }

    private static int CompareHierarchyPaths(List<int> a, List<int> b)
    {
        for (int i = 0; i < Mathf.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Count.CompareTo(b.Count);
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
    
    private void SpawnOrUpdateNamePlate(BoothDescriptor descriptor)
    {
        Transform existingPlate = descriptor.transform.Find("BoothNamePlate");
        GameObject plateObj = null;

        if (existingPlate == null)
        {
            string packagePath = "Packages/name.tummy.tcboothsdk/Runtime/BoothNamePlate.prefab";
            GameObject prefabAsset = null;

            AssetDatabase.ImportAsset(packagePath, ImportAssetOptions.ForceUpdate);
            prefabAsset = AssetDatabase.LoadMainAssetAtPath(packagePath) as GameObject;

            if (prefabAsset == null)
            {
                string[] guids = AssetDatabase.FindAssets("BoothNamePlate t:Prefab");
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
                EditorUtility.DisplayDialog("Not Found", "Could not find the 'BoothNamePlate' prefab.\n\nPlease ensure it is included in your package.", "OK");
                return;
            }

            plateObj = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, descriptor.transform);
            plateObj.name = "BoothNamePlate"; 
            Selection.activeGameObject = plateObj;
            Debug.Log("[Booth SDK] Name plate spawned and attached to the booth.");
        }
        else
        {
            plateObj = existingPlate.gameObject;
        }

        UpdateNamePlateText(plateObj, descriptor);
        if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
    }

    private void UpdateNamePlateText(GameObject plateObj, BoothDescriptor descriptor)
    {
        try
        {
            if (PrefabUtility.IsAnyPrefabInstanceRoot(plateObj))
            {
                PrefabUtility.UnpackPrefabInstance(plateObj, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                Debug.Log("[Booth SDK] Unpacked Name Plate into standard GameObjects to ensure safe export.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Booth SDK] Note: Could not unpack Name Plate (it may already be unpacked). {ex.Message}");
        }

        Component[] allComponents = plateObj.GetComponentsInChildren<Component>(true);
        bool updated = false;

        foreach (Component comp in allComponents)
        {
            if (comp == null) continue;
            
            string typeName = comp.GetType().Name;
            if (typeName == "TextMeshProUGUI" || typeName == "TextMeshPro")
            {
                string objName = comp.gameObject.name;
                if (objName == "CreatorName" || objName == "BoothName")
                {
                    SerializedObject so = new SerializedObject(comp);
                    SerializedProperty textProp = so.FindProperty("m_text");
                    
                    if (textProp != null)
                    {
                        string newText = (objName == "CreatorName") ? descriptor.creatorName : descriptor.boothName;
                        if (textProp.stringValue != newText)
                        {
                            textProp.stringValue = newText;
                            so.ApplyModifiedProperties();
                            updated = true;
                        }
                    }
                }
            }
        }

        if (updated)
        {
            Debug.Log("[Booth SDK] Name plate text synchronized with BoothDescriptor.");
        }
    }
    
    private static string ReadLocalSdkVersion()
    {
        if (File.Exists(PackageJsonPath))
        {
            try
            {
                string json = File.ReadAllText(PackageJsonPath);
                PackageJsonData pkg = JsonUtility.FromJson<PackageJsonData>(json);
                if (pkg != null && !string.IsNullOrEmpty(pkg.version))
                {
                    return pkg.version;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Booth SDK] Could not parse local package.json: {e.Message}");
            }
        }
        return "Unknown";
    }

    // Accepts "v1.2.3" and "1" as well as "1.2.3"; System.Version needs at least two parts
    private static bool TryParseSdkVersion(string versionStr, out System.Version version)
    {
        string clean = (versionStr ?? "").Replace("v", "").Trim();
        if (!clean.Contains(".")) clean += ".0";
        return System.Version.TryParse(clean, out version);
    }

    private async Task CheckForUpdatesAsync()
    {
        localVersionStr = ReadLocalSdkVersion();

        using (UnityWebRequest req = UnityWebRequest.Get(GithubLatestReleaseUrl))
        {
            req.SetRequestHeader("User-Agent", "Unity-BoothExporter-SDK");
            
            var operation = req.SendWebRequest();
            while (!operation.isDone) await Task.Delay(50);

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    GithubReleaseData release = JsonUtility.FromJson<GithubReleaseData>(req.downloadHandler.text);
                    
                    remoteVersionStr = release.tag_name.Replace("v", "").Trim();

                    if (TryParseSdkVersion(localVersionStr, out System.Version localVer) &&
                        TryParseSdkVersion(remoteVersionStr, out System.Version remoteVer))
                    {
                        if (remoteVer > localVer)
                        {
                            updateAvailable = true;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Booth SDK] Error parsing GitHub response: {ex.Message}");
                }
            }
        }
        
        updateCheckDone = true;
        
        if (this != null) Repaint();
    }

    [System.Serializable]
    private class PackageJsonData
    {
        public string version;
    }

    [System.Serializable]
    private class GithubReleaseData
    {
        public string tag_name;
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
            // Lets the server reject outdated SDKs before receiving the file
            request.SetRequestHeader("x-sdk-version", ReadLocalSdkVersion());
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                float progress = request.uploadProgress;
                bool canceled = EditorUtility.DisplayCancelableProgressBar("Booth Exporter", $"Uploading... {(progress * 100):F1}%", progress);
                if (canceled) { request.Abort(); throw new System.Exception("Upload canceled."); }
                await Task.Delay(100); 
            }
            EditorUtility.ClearProgressBar();
            if (request.result == UnityWebRequest.Result.Success)
            {
                EditorUtility.DisplayDialog("Upload Complete", "Upload successful.", "OK");
            }
            else
            {
                // The server explains rejections (bad login, outdated SDK, too large) in the response body
                string serverMessage = request.downloadHandler?.text?.Trim();
                if (!string.IsNullOrEmpty(serverMessage) && serverMessage.Length > 500) serverMessage = serverMessage.Substring(0, 500) + "...";
                string details = string.IsNullOrEmpty(serverMessage) ? request.error : $"{serverMessage}\n\n({request.error})";
                throw new System.Exception($"Server rejected the upload: {details}");
            }
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
                // A broken shader reports HasProperty == false for everything, which would wipe the material
                if (mat != null && !IsShaderBroken(mat.shader))
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