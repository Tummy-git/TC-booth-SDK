using UnityEditor;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Inspects a .unitypackage without importing it.
//
// A .unitypackage is a gzipped tar with one folder per asset GUID:
//   <guid>/pathname    destination path inside the Unity project ("Assets/...")
//   <guid>/asset       file contents (missing for folders)
//   <guid>/asset.meta  Unity import settings
//
// Errors mean the package must not be imported. Flags and GUID collisions are shown for review.
// The upload server runs the same checks (packageInspector.js), so keep the rules in sync.
public static class BoothPackageInspector
{
    // Compiled or OS-executable code. A booth never needs these and they can't be reviewed.
    // .rsp files are compiler option files that can pull in DLLs or change how scripts compile.
    private static readonly string[] BlockedExtensions =
    {
        ".dll", ".so", ".dylib", ".bundle", ".framework", ".a", ".lib", ".aar", ".jar",
        ".exe", ".msi", ".com", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".sh", ".rsp"
    };

    // Hints for manual review, not proof of anything. UdonSharp code runs in VRChat's sandbox, but every
    // .cs file is also compiled and run by the Unity editor that imports the booth.
    private static readonly (Regex pattern, string reason)[] SuspiciousCodePatterns =
    {
        (new Regex(@"System\.Diagnostics|Process\.Start|ProcessStartInfo"), "starts processes (System.Diagnostics)"),
        (new Regex(@"System\.Net\b|HttpClient|WebClient|UnityWebRequest|TcpClient|Socket\b"), "network access"),
        (new Regex(@"System\.IO\b|\bFile\.|\bDirectory\.|FileStream|StreamWriter"), "file system access (System.IO)"),
        (new Regex(@"DllImport|System\.Runtime\.InteropServices|\bMarshal\.|\bextern\s"), "native code interop"),
        (new Regex(@"\bunsafe\b"), "unsafe code"),
        (new Regex(@"System\.Reflection|Assembly\.Load|Activator\.CreateInstance|Type\.GetType|AppDomain"), "reflection / dynamic code loading"),
        (new Regex(@"Microsoft\.Win32|\bRegistry\."), "Windows registry access"),
        (new Regex(@"\bEnvironment\."), "environment access (System.Environment)"),
        (new Regex(@"UnityEditor|InitializeOnLoad|DidReloadScripts|AssetPostprocessor|AssetModificationProcessor|\[MenuItem"), "runs in the Unity editor (UnityEditor)"),
        (new Regex(@"Convert\.FromBase64String|[A-Za-z0-9+/]{200,}={0,2}"), "encoded data (base64)"),
    };

    private const int MaxEntries = 50000;
    private const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024; // zip bomb guard
    private const int MaxPathnameBytes = 4096;
    private const int MaxScannedFileBytes = 2 * 1024 * 1024;

    private static readonly Regex EntryRegex = new Regex(@"^([0-9a-fA-F]{32})/(pathname|asset|asset\.meta|preview\.png)?$");

    public class Flag
    {
        public string path;
        public List<string> reasons;
    }

    public class Report
    {
        public List<string> errors = new List<string>();
        public List<Flag> flags = new List<Flag>();
        public List<string> guidCollisions = new List<string>();
        public List<string> scripts = new List<string>();
        // Contents of flagged scripts, so they can be written out for review
        public Dictionary<string, string> flaggedScriptContents = new Dictionary<string, string>();

        public bool NeedsReview => flags.Count > 0 || guidCollisions.Count > 0;
    }

    private class AssetInfo
    {
        public string pathname;
        public bool hasAsset;
    }

    public static bool HasBlockedExtension(string assetPath)
    {
        // Check every path segment: macOS plugins (.bundle/.framework) are folders with files inside
        return assetPath.Split('/').Any(segment => BlockedExtensions.Contains(Path.GetExtension(segment).ToLowerInvariant()));
    }

    /// <param name="packagePath">The .unitypackage on disk.</param>
    /// <param name="creator">Login name of the creator; everything must be inside Assets/Booth/&lt;creator&gt;/.</param>
    public static Report Inspect(string packagePath, string creator)
    {
        Report report = new Report();
        string creatorRoot = $"Assets/Booth/{creator}";

        Dictionary<string, AssetInfo> assets;
        try
        {
            assets = ReadPathnames(packagePath, report.errors);
        }
        catch (System.Exception ex)
        {
            report.errors.Add($"The file is not a valid .unitypackage ({ex.Message}).");
            return report;
        }

        // Nothing recognisable means the layout is one these checks don't understand, so don't let it through
        if (report.errors.Count == 0 && !assets.Values.Any(a => a.hasAsset))
        {
            report.errors.Add("The package contains no assets in the expected .unitypackage layout.");
        }

        HashSet<string> codeGuids = new HashSet<string>();
        foreach (var kvp in assets)
        {
            string guid = kvp.Key;
            AssetInfo asset = kvp.Value;

            string problem = ValidatePathname(asset.pathname, asset.hasAsset, creatorRoot);
            if (problem != null)
            {
                report.errors.Add($"'{asset.pathname ?? guid}' {problem}.");
                continue;
            }

            // Unity keeps an existing asset's location when a package reuses its GUID, so the import would
            // update that asset instead of creating a new one. Common with shared free asset packs.
            string existingPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(existingPath) && existingPath != asset.pathname &&
                !existingPath.StartsWith(creatorRoot + "/") && existingPath != creatorRoot && existingPath != "Assets/Booth")
            {
                report.guidCollisions.Add($"'{asset.pathname}' has the same ID as existing '{existingPath}'");
            }

            string ext = Path.GetExtension(asset.pathname).ToLowerInvariant();
            if (asset.hasAsset && (ext == ".cs" || ext == ".asmdef")) codeGuids.Add(guid);
        }

        if (report.errors.Count > 0 || codeGuids.Count == 0) return report;

        Dictionary<string, string> contents = ReadAssetContents(packagePath, codeGuids);
        foreach (string guid in codeGuids)
        {
            string pathname = assets[guid].pathname;
            contents.TryGetValue(guid, out string text);

            List<string> reasons = pathname.EndsWith(".asmdef") ? ReviewAssemblyDefinition(text ?? "") : ReviewScript(pathname, text);
            if (pathname.EndsWith(".cs")) report.scripts.Add(pathname);
            if (reasons.Count > 0)
            {
                report.flags.Add(new Flag { path = pathname, reasons = reasons });
                if (text != null) report.flaggedScriptContents[pathname] = text;
            }
        }

        return report;
    }

    /// <summary>
    /// Writes the flagged scripts to a folder outside Assets (so Unity doesn't compile them) and returns it.
    /// </summary>
    public static string WriteFlaggedScriptsForReview(Report report, string creator)
    {
        string reviewRoot = Path.GetFullPath(Path.Combine("Temp", "BoothReview", creator));
        if (Directory.Exists(reviewRoot)) Directory.Delete(reviewRoot, true);
        Directory.CreateDirectory(reviewRoot);

        foreach (var kvp in report.flaggedScriptContents)
        {
            // Paths were validated by Inspect (no "..", inside Assets/Booth/<creator>/)
            string target = Path.GetFullPath(Path.Combine(reviewRoot, kvp.Key));
            if (!target.StartsWith(reviewRoot)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target + ".txt", kvp.Value);
        }
        return reviewRoot;
    }

    private static string ValidatePathname(string pathname, bool hasAsset, string creatorRoot)
    {
        if (string.IsNullOrEmpty(pathname)) return "has no destination path";
        if (pathname.Contains("\\") || pathname.Contains(":") || pathname.StartsWith("/") ||
            pathname.Split('/').Any(segment => segment == ".." || segment == "."))
        {
            return "has an invalid destination path";
        }

        // Folder entries for the creator's own root (and Assets/Booth above it) are fine
        bool isOwnRootFolder = !hasAsset && (pathname == creatorRoot || pathname == "Assets/Booth");
        if (!pathname.StartsWith(creatorRoot + "/") && !isOwnRootFolder)
        {
            return $"is outside the creator's booth folder ({creatorRoot}/)";
        }

        if (HasBlockedExtension(pathname)) return "is compiled or executable code, which is not allowed";
        return null;
    }

    private static List<string> ReviewAssemblyDefinition(string text)
    {
        List<string> reasons = new List<string>();
        try
        {
            var asmdef = Newtonsoft.Json.Linq.JObject.Parse(text);
            var platforms = asmdef["includePlatforms"]?.ToObject<List<string>>() ?? new List<string>();
            var precompiled = asmdef["precompiledReferences"]?.ToObject<List<string>>() ?? new List<string>();
            if (platforms.Contains("Editor")) reasons.Add("editor-only assembly (runs in the Unity editor)");
            if ((bool?)asmdef["allowUnsafeCode"] == true) reasons.Add("allows unsafe code");
            if ((bool?)asmdef["overrideReferences"] == true || precompiled.Count > 0) reasons.Add("references precompiled DLLs");
        }
        catch
        {
            reasons.Add("could not be parsed");
        }
        return reasons;
    }

    private static List<string> ReviewScript(string pathname, string text)
    {
        List<string> reasons = new List<string>();
        if (pathname.Split('/').Contains("Editor")) reasons.Add("in an Editor folder (runs in the Unity editor)");
        if (text == null)
        {
            reasons.Add("too large to scan automatically");
            return reasons;
        }
        foreach (var (pattern, reason) in SuspiciousCodePatterns)
        {
            if (pattern.IsMatch(text)) reasons.Add(reason);
        }
        return reasons;
    }

    // Pass 1: every GUID's destination path, plus sanity checks on the archive itself
    private static Dictionary<string, AssetInfo> ReadPathnames(string packagePath, List<string> errors)
    {
        Dictionary<string, AssetInfo> assets = new Dictionary<string, AssetInfo>();
        int entryCount = 0;
        long unpackedBytes = 0;

        ReadTar(packagePath, (name, type, size, readContent) =>
        {
            entryCount++;
            unpackedBytes += size;
            if (entryCount > MaxEntries || unpackedBytes > MaxUnpackedBytes)
            {
                errors.Add("The package is too large or has too many files once unpacked.");
                return false;
            }

            if (type != '0' && type != '\0' && type != '5')
            {
                errors.Add($"Unexpected entry type '{type}' in the package: {name}");
                return true;
            }

            Match match = EntryRegex.Match(name);
            if (!match.Success || !match.Groups[2].Success) return true;

            string guid = match.Groups[1].Value.ToLowerInvariant();
            if (!assets.TryGetValue(guid, out AssetInfo asset)) assets[guid] = asset = new AssetInfo();

            if (match.Groups[2].Value == "asset") asset.hasAsset = true;
            if (match.Groups[2].Value == "pathname")
            {
                byte[] data = readContent(MaxPathnameBytes);
                // Unity writes the path on the first line; some versions add a second line
                if (data != null) asset.pathname = Encoding.UTF8.GetString(data).Split('\n')[0].TrimEnd('\r');
                else errors.Add($"Invalid path entry for asset {guid}.");
            }
            return true;
        });

        return assets;
    }

    // Pass 2: contents of the given GUIDs (null if too large to scan)
    private static Dictionary<string, string> ReadAssetContents(string packagePath, HashSet<string> guids)
    {
        Dictionary<string, string> contents = new Dictionary<string, string>();
        ReadTar(packagePath, (name, type, size, readContent) =>
        {
            Match match = EntryRegex.Match(name);
            if (!match.Success || match.Groups[2].Value != "asset") return true;

            string guid = match.Groups[1].Value.ToLowerInvariant();
            if (!guids.Contains(guid)) return true;

            byte[] data = readContent(MaxScannedFileBytes);
            contents[guid] = data != null ? Encoding.UTF8.GetString(data) : null;
            return true;
        });
        return contents;
    }

    // Minimal streaming tar reader. The callback gets (name, type, size, readContent) and returns false to stop.
    // readContent(maxBytes) returns the entry's data, or null if it is larger than maxBytes.
    private delegate bool TarEntryCallback(string name, char type, long size, System.Func<int, byte[]> readContent);

    private static void ReadTar(string packagePath, TarEntryCallback onEntry)
    {
        using (FileStream file = File.OpenRead(packagePath))
        using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            byte[] header = new byte[512];
            string overrideName = null;

            while (ReadFully(gzip, header, 512))
            {
                if (header.All(b => b == 0)) break; // End of archive

                string name = ReadString(header, 0, 100);
                long size = ParseSize(header, 124, 12);
                char type = (char)header[156];
                if (ReadString(header, 257, 5) == "ustar")
                {
                    string prefix = ReadString(header, 345, 155);
                    if (prefix.Length > 0) name = prefix + "/" + name;
                }

                bool consumed = false;
                byte[] content = null;
                byte[] ReadContent(int maxBytes)
                {
                    if (!consumed)
                    {
                        consumed = true;
                        content = size <= maxBytes ? ReadBytes(gzip, size) : null;
                        if (content == null) Skip(gzip, size);
                    }
                    return content;
                }

                // GNU long name and PAX headers describe the name of the next entry
                if (type == 'L')
                {
                    byte[] data = ReadContent(MaxPathnameBytes);
                    overrideName = data != null ? Encoding.UTF8.GetString(data).TrimEnd('\0') : null;
                }
                else if (type == 'x')
                {
                    byte[] data = ReadContent(MaxPathnameBytes * 4);
                    overrideName = data != null ? ParsePaxPath(Encoding.UTF8.GetString(data)) : null;
                }
                else if (type != 'g')
                {
                    if (overrideName != null) name = overrideName;
                    overrideName = null;
                    // Packages repacked with tools like macOS tar have "./" in front of every entry, which Unity accepts
                    while (name.StartsWith("./")) name = name.Substring(2);

                    bool keepGoing = onEntry(name, type, size, ReadContent);
                    if (!keepGoing) return;
                }

                if (!consumed) Skip(gzip, size);
                long padding = (512 - size % 512) % 512;
                Skip(gzip, padding);
            }
        }
    }

    private static string ParsePaxPath(string pax)
    {
        // Records look like "<length> path=<value>\n"
        foreach (string record in pax.Split('\n'))
        {
            int space = record.IndexOf(' ');
            if (space >= 0 && record.Substring(space + 1).StartsWith("path=")) return record.Substring(space + 6);
        }
        return null;
    }

    private static string ReadString(byte[] buffer, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && buffer[end] != 0) end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static long ParseSize(byte[] buffer, int offset, int length)
    {
        // Base-256 encoding for large files (high bit set), otherwise octal text
        if ((buffer[offset] & 0x80) != 0)
        {
            long value = buffer[offset] & 0x7F;
            for (int i = offset + 1; i < offset + length; i++) value = (value << 8) | buffer[i];
            return value;
        }
        string octal = ReadString(buffer, offset, length).Trim();
        return octal.Length == 0 ? 0 : System.Convert.ToInt64(octal, 8);
    }

    private static bool ReadFully(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("Unexpected end of archive");
            }
            read += n;
        }
        return true;
    }

    private static byte[] ReadBytes(Stream stream, long size)
    {
        byte[] data = new byte[size];
        if (size > 0 && !ReadFully(stream, data, (int)size)) throw new EndOfStreamException("Unexpected end of archive");
        return data;
    }

    private static void Skip(Stream stream, long count)
    {
        byte[] scratch = new byte[81920];
        while (count > 0)
        {
            int n = stream.Read(scratch, 0, (int)System.Math.Min(scratch.Length, count));
            if (n == 0) throw new EndOfStreamException("Unexpected end of archive");
            count -= n;
        }
    }
}
