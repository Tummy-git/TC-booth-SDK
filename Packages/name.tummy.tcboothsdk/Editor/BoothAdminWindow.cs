using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class BoothAdminWindow : EditorWindow
{
    private List<string> masterPackages = new List<string>();
    private Dictionary<string, List<string>> missingPackagesPerBooth = new Dictionary<string, List<string>>();
    private Vector2 scrollPos;

    [MenuItem("Booth SDK/Admin/Check VPM Dependencies")]
    public static void ShowWindow()
    {
        GetWindow<BoothAdminWindow>("Booth Dependency Checker");
    }

    private void OnEnable()
    {
        RefreshData();
    }

    private void OnGUI()
    {
        GUILayout.Label("Booth Dependency Scanner", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Scans imported booths for missing VPM packages and provides safe install commands.", MessageType.Info);

        if (GUILayout.Button("Rescan Imported Booths", GUILayout.Height(30)))
        {
            RefreshData();
        }

        EditorGUILayout.Space();

        if (missingPackagesPerBooth.Count == 0)
        {
            GUILayout.Label("✅ All imported booths have their required VPM packages installed.", EditorStyles.boldLabel);
            return;
        }

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        foreach (var kvp in missingPackagesPerBooth)
        {
            string boothName = kvp.Key;
            List<string> missingPkgs = kvp.Value;

            if (missingPkgs.Count == 0) continue;

            EditorGUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"⚠️ Missing packages in: {boothName}", EditorStyles.boldLabel);
            
            foreach (string pkg in missingPkgs)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"- {pkg}");
                
                if (GUILayout.Button("Copy Install Command", GUILayout.Width(180)))
                {
                    string command = $"vrc-get install {pkg}";
                    GUIUtility.systemCopyBuffer = command;
                    Debug.Log($"[Booth Admin] Copied to clipboard: {command}");
                    EditorUtility.DisplayDialog("Command Copied", $"Open your command prompt or terminal in your Master Project folder and paste:\n\n{command}", "OK");
                }
                GUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        EditorGUILayout.EndScrollView();
    }

    private void RefreshData()
    {
        masterPackages.Clear();
        missingPackagesPerBooth.Clear();

        // 1. Read Master Project Manifest
        string masterManifestPath = "Packages/vpm-manifest.json";
        if (File.Exists(masterManifestPath))
        {
            masterPackages = ExtractPackagesFromManifest(File.ReadAllText(masterManifestPath));
        }

        // 2. Find all imported Booth Manifests
        string[] guids = AssetDatabase.FindAssets("*_VPM t:TextAsset");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string jsonContent = File.ReadAllText(path);
            
            List<string> boothPackages = ExtractPackagesFromManifest(jsonContent);
            List<string> missing = new List<string>();

            foreach (string pkg in boothPackages)
            {
                // Ignore base VRChat packages as they are guaranteed to exist or conflict
                if (pkg.StartsWith("com.vrchat.")) continue;

                if (!masterPackages.Contains(pkg))
                {
                    missing.Add(pkg);
                }
            }

            if (missing.Count > 0)
            {
                string boothName = Path.GetFileNameWithoutExtension(path).Replace("_VPM", "");
                missingPackagesPerBooth[boothName] = missing;
            }
        }
    }

    // A lightweight string parser to extract packages without needing heavy JSON libraries
    private List<string> ExtractPackagesFromManifest(string jsonContent)
    {
        List<string> packages = new List<string>();
        bool insideDependencies = false;

        string[] lines = jsonContent.Split('\n');
        foreach (string line in lines)
        {
            if (line.Contains("\"dependencies\": {")) 
            { 
                insideDependencies = true; 
                continue; 
            }
            
            if (insideDependencies && line.Contains("}")) 
            { 
                break; 
            }

            if (insideDependencies)
            {
                // Matches "com.creator.package": "^1.0.0"
                Match match = Regex.Match(line, @"\""(?<pkg>[\w\.\-]+)\""\s*:");
                if (match.Success)
                {
                    packages.Add(match.Groups["pkg"].Value);
                }
            }
        }
        return packages;
    }
}