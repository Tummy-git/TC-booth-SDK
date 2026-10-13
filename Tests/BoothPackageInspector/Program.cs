// Tests for BoothPackageInspector.cs, run outside Unity:
//   dotnet run --project Tests/BoothPackageInspector
// The cases in inspector-cases.json are shared with the upload server's packageInspector.js tests.
// Exits with code 1 if any test fails.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace UnityEditor
{
    // Stand-in for Unity's AssetDatabase: the project is empty unless a test says otherwise
    public static class AssetDatabase
    {
        public static Dictionary<string, string> ExistingAssets = new Dictionary<string, string>();

        public static string GUIDToAssetPath(string guid) =>
            ExistingAssets.TryGetValue(guid, out string path) ? path : "";
    }
}

public static class Program
{
    private static int failures;

    public static int Main()
    {
        string casesPath = Path.Combine(AppContext.BaseDirectory, "inspector-cases.json");
        foreach (JObject testCase in JObject.Parse(File.ReadAllText(casesPath))["cases"])
        {
            Run((string)testCase["name"], () =>
            {
                string package = BuildPackage(testCase);
                CheckReport(BoothPackageInspector.Inspect(package, (string)testCase["creator"]), (JObject)testCase["expect"]);
            });
        }

        // SDK only: the server can't see the master project, so it doesn't check GUID collisions
        Run("a GUID already used outside the creator's folder is reported as a collision", () =>
        {
            JObject testCase = JObject.Parse(@"{ creator: 'bob', entries: [ { path: 'Assets/Booth/bob/Shared.mat', content: 'x' } ] }");
            UnityEditor.AssetDatabase.ExistingAssets[GuidFor("Assets/Booth/bob/Shared.mat")] = "Assets/Booth/alice/Shared.mat";
            try
            {
                var report = BoothPackageInspector.Inspect(BuildPackage(testCase), "bob");
                Assert(report.errors.Count == 0, $"expected no errors, got {string.Join(" | ", report.errors)}");
                Assert(report.guidCollisions.Count == 1 && report.guidCollisions[0].Contains("Assets/Booth/alice/Shared.mat"),
                    $"expected one collision with alice's asset, got {string.Join(" | ", report.guidCollisions)}");
            }
            finally
            {
                UnityEditor.AssetDatabase.ExistingAssets.Clear();
            }
        });

        Run("the creator's own previous assets are not collisions", () =>
        {
            JObject testCase = JObject.Parse(@"{ creator: 'bob', entries: [ { path: 'Assets/Booth/bob/Shared.mat', content: 'x' } ] }");
            UnityEditor.AssetDatabase.ExistingAssets[GuidFor("Assets/Booth/bob/Shared.mat")] = "Assets/Booth/bob/Old/Shared.mat";
            try
            {
                var report = BoothPackageInspector.Inspect(BuildPackage(testCase), "bob");
                Assert(report.guidCollisions.Count == 0, $"expected no collisions, got {string.Join(" | ", report.guidCollisions)}");
            }
            finally
            {
                UnityEditor.AssetDatabase.ExistingAssets.Clear();
            }
        });

        Console.WriteLine(failures == 0 ? "\nAll tests passed." : $"\n{failures} test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckReport(BoothPackageInspector.Report report, JObject expect)
    {
        var expectedErrors = expect["errors"].ToObject<List<string>>();
        Assert(report.errors.Count == expectedErrors.Count,
            $"expected {expectedErrors.Count} error(s), got {report.errors.Count}: {string.Join(" | ", report.errors)}");
        for (int i = 0; i < expectedErrors.Count; i++)
        {
            Assert(report.errors[i].Contains(expectedErrors[i]), $"error {i} should contain '{expectedErrors[i]}': {report.errors[i]}");
        }

        var expectedFlags = expect["flags"].ToObject<Dictionary<string, List<string>>>();
        var actualFlags = report.flags.ToDictionary(f => f.path, f => f.reasons);
        Assert(expectedFlags.Keys.OrderBy(k => k).SequenceEqual(actualFlags.Keys.OrderBy(k => k)),
            $"flagged files differ. Expected: {string.Join(", ", expectedFlags.Keys)}. Got: {string.Join(", ", actualFlags.Keys)}");
        foreach (var kvp in expectedFlags)
        {
            Assert(kvp.Value.SequenceEqual(actualFlags[kvp.Key]),
                $"reasons for {kvp.Key} differ. Expected: {string.Join(", ", kvp.Value)}. Got: {string.Join(", ", actualFlags[kvp.Key])}");
        }

        int expectedScripts = (int)expect["scripts"];
        Assert(report.scripts.Count == expectedScripts, $"expected {expectedScripts} script(s), got {report.scripts.Count}");
    }

    // Same stable GUIDs as the server tests: MD5 of the asset path
    private static string GuidFor(string assetPath) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(assetPath))).ToLowerInvariant();

    private static string BuildPackage(JObject testCase)
    {
        string file = Path.Combine(Path.GetTempPath(), $"booth-inspector-{Guid.NewGuid():N}.unitypackage");
        if (testCase["raw"] != null)
        {
            File.WriteAllText(file, (string)testCase["raw"]);
            return file;
        }

        string prefix = (string)testCase["prefix"] ?? "";
        using (FileStream stream = File.Create(file))
        using (GZipStream gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (TarWriter tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            foreach (JObject entry in testCase["entries"] ?? new JArray())
            {
                string assetPath = (string)entry["path"];
                string guid = GuidFor(assetPath);
                tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, $"{prefix}{guid}/"));
                WriteFile(tar, $"{prefix}{guid}/pathname", assetPath);
                WriteFile(tar, $"{prefix}{guid}/asset.meta", "fileFormatVersion: 2\n");
                if (entry["content"].Type != JTokenType.Null) WriteFile(tar, $"{prefix}{guid}/asset", (string)entry["content"]);
            }
            foreach (JObject raw in testCase["rawEntries"] ?? new JArray())
            {
                if ((string)raw["type"] == "symlink")
                {
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, prefix + (string)raw["name"]) { LinkName = (string)raw["linkName"] });
                }
                else
                {
                    WriteFile(tar, prefix + (string)raw["name"], (string)raw["content"]);
                }
            }
        }
        return file;
    }

    private static void WriteFile(TarWriter tar, string name, string content)
    {
        tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
        });
    }
}
