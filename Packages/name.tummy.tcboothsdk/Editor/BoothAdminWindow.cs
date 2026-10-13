using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public class BoothAdminWindow : EditorWindow
{
    private const string BoothAssetRoot = "Assets/Booth";

    private class PackageIssue
    {
        public string name;
        public string requiredVersion;
        public string installedVersion; // null = not installed
    }

    private Dictionary<string, List<PackageIssue>> issuesPerBooth = new Dictionary<string, List<PackageIssue>>();
    private List<string> unreadableManifests = new List<string>();
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

        foreach (string path in unreadableManifests)
        {
            EditorGUILayout.HelpBox($"Could not read {path}. See the Console for details.", MessageType.Error);
        }

        if (issuesPerBooth.Count == 0)
        {
            GUILayout.Label("✅ All imported booths have their required VPM packages installed.", EditorStyles.boldLabel);
            return;
        }

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        foreach (var kvp in issuesPerBooth)
        {
            string boothName = kvp.Key;
            List<PackageIssue> issues = kvp.Value;

            if (issues.Count == 0) continue;

            EditorGUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"⚠️ Package issues in: {boothName}", EditorStyles.boldLabel);

            foreach (PackageIssue issue in issues)
            {
                bool isMissing = issue.installedVersion == null;

                GUILayout.BeginHorizontal();
                GUILayout.Label(isMissing
                    ? $"- {issue.name} (missing, booth uses {issue.requiredVersion})"
                    : $"- {issue.name} (installed {issue.installedVersion}, booth uses {issue.requiredVersion})");

                if (GUILayout.Button(isMissing ? "Copy Install Command" : "Copy Upgrade Command", GUILayout.Width(180)))
                {
                    string command = isMissing ? $"vrc-get install {issue.name}" : $"vrc-get upgrade {issue.name}";
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
        issuesPerBooth.Clear();
        unreadableManifests.Clear();

        // 1. Read Master Project Manifest. "locked" lists everything actually installed, including
        // packages pulled in as dependencies of other packages.
        Dictionary<string, string> installed = new Dictionary<string, string>();
        string masterManifestPath = "Packages/vpm-manifest.json";
        if (File.Exists(masterManifestPath))
        {
            if (!TryReadManifest(masterManifestPath, out JObject master)) return;
            foreach (var pkg in ReadPackageSection(master, "dependencies")) installed[pkg.Key] = pkg.Value;
            foreach (var pkg in ReadPackageSection(master, "locked")) installed[pkg.Key] = pkg.Value;
        }

        // 2. Find all imported Booth Manifests
        if (!AssetDatabase.IsValidFolder(BoothAssetRoot)) return;

        string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { BoothAssetRoot });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith("_VPM.json")) continue;
            if (!TryReadManifest(path, out JObject boothManifest)) continue;

            List<PackageIssue> issues = new List<PackageIssue>();

            // Only the packages the creator installed directly; their own dependencies come along when installing them
            foreach (var pkg in ReadPackageSection(boothManifest, "dependencies"))
            {
                // Ignore base VRChat packages as they are guaranteed to exist or conflict
                if (pkg.Key.StartsWith("com.vrchat.")) continue;

                if (!installed.TryGetValue(pkg.Key, out string installedVersion))
                {
                    issues.Add(new PackageIssue { name = pkg.Key, requiredVersion = pkg.Value, installedVersion = null });
                }
                else if (IsOlderVersion(installedVersion, pkg.Value))
                {
                    issues.Add(new PackageIssue { name = pkg.Key, requiredVersion = pkg.Value, installedVersion = installedVersion });
                }
            }

            if (issues.Count > 0)
            {
                string boothName = Path.GetFileNameWithoutExtension(path).Replace("_VPM", "");
                issuesPerBooth[boothName] = issues;
            }
        }
    }

    private bool TryReadManifest(string path, out JObject manifest)
    {
        try
        {
            manifest = JObject.Parse(File.ReadAllText(path));
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Booth Admin] Could not parse VPM manifest '{path}': {ex.Message}");
            unreadableManifests.Add(path);
            manifest = null;
            return false;
        }
    }

    // Returns package name -> version for "dependencies" or "locked".
    // Entries look like "com.x": { "version": "1.0.0", ... }.
    private static Dictionary<string, string> ReadPackageSection(JObject manifest, string section)
    {
        Dictionary<string, string> packages = new Dictionary<string, string>();
        if (!(manifest[section] is JObject entries)) return packages;

        foreach (JProperty entry in entries.Properties())
        {
            string version = entry.Value.Type == JTokenType.Object
                ? (string)entry.Value["version"]
                : (string)entry.Value;
            packages[entry.Name] = version ?? "?";
        }
        return packages;
    }

    // Compares the numeric part only ("1.2.3-beta.1" -> 1.2.3). Unparseable versions are never reported as older.
    private static bool IsOlderVersion(string installed, string required)
    {
        if (TryParseVersion(installed, out System.Version installedVer) && TryParseVersion(required, out System.Version requiredVer))
        {
            return installedVer < requiredVer;
        }
        return false;
    }

    private static bool TryParseVersion(string versionStr, out System.Version version)
    {
        string clean = (versionStr ?? "").Split('-', '+')[0].Trim();
        if (!clean.Contains(".")) clean += ".0";
        return System.Version.TryParse(clean, out version);
    }
}