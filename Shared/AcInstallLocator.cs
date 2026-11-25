using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>
/// Finds the Assetto Corsa install (and its content folder, where car and track mods live) wherever it is:
/// a running copy of the game, the Steam library Steam itself lists AC in, Windows' uninstall entry, every
/// other Steam library, and finally a scan of every fixed drive.
/// </summary>
public static class AcInstallLocator
{
    public const string SteamAppId = "244210";

    /// <summary>An AC install that was found, and how.</summary>
    public record Candidate(string Root, string Source);

    private static IReadOnlyList<Candidate>? _cache;
    private static bool _cacheScanned;

    /// <summary>Returns the AC root folder (the one containing acs.exe and content/), or null.</summary>
    /// <param name="scanDrives">False skips the slow drive scan (e.g. on a server with no game install).</param>
    public static string? FindAcRoot(bool scanDrives = true) => FindAll(scanDrives: scanDrives).FirstOrDefault()?.Root;

    /// <inheritdoc cref="FindAcRoot"/>
    public static string? FindContentFolder(bool scanDrives = true)
    {
        var root = FindAcRoot(scanDrives);
        return root == null ? null : Path.Combine(root, "content");
    }

    /// <summary>True for a folder with acs.exe and content\cars (a real, playable install).</summary>
    public static bool IsAcRoot(string? root) =>
        !string.IsNullOrEmpty(root)
        && File.Exists(Path.Combine(root, "acs.exe"))
        && Directory.Exists(Path.Combine(root, "content", "cars"));

    /// <summary>
    /// Every AC install found, best first: the running game, then the install Steam has registered,
    /// then Windows' uninstall entry, other Steam libraries, and a drive scan (only when nothing else was found
    /// and <paramref name="scanDrives"/> is true).
    /// </summary>
    public static IReadOnlyList<Candidate> FindAll(bool refresh = false, bool scanDrives = true)
    {
        // A cached result without the scan doesn't answer a caller that wants the scan, unless it already found something.
        if (_cache != null && !refresh && (_cacheScanned || !scanDrives || _cache.Count > 0)) return _cache;
        var found = new List<Candidate>();
        void Add(string? root, string source)
        {
            if (root == null) return;
            try { root = Path.GetFullPath(root.Trim().TrimEnd('\\', '/')); } catch { return; }
            if (IsAcRoot(root) && !found.Any(c => c.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                found.Add(new Candidate(root, source));
        }

        foreach (var root in RunningGame()) Add(root, "running game");
        var libraries = SteamLibraryInfo();
        foreach (var lib in libraries.Where(l => l.HasAc)) Add(ManifestRoot(lib.Path), "Steam library");
        Add(UninstallEntry(), "Windows uninstall entry");
        foreach (var lib in libraries.Where(l => !l.HasAc)) Add(ManifestRoot(lib.Path), "Steam library (not registered)");
        if (found.Count == 0 && scanDrives)
        {
            // Several copies on disk: the one whose cars were changed most recently is the one in use.
            var scanned = ScanDrives(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3))
                .OrderByDescending(r => SafeWriteTime(Path.Combine(r, "content", "cars")));
            foreach (var root in scanned) Add(root, "drive scan");
        }

        _cacheScanned = scanDrives;
        return _cache = found;
    }

    /// <summary>Steam library folders from the registry + libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> SteamLibraries() => SteamLibraryInfo().Select(l => l.Path).ToList();

    // ------------------------------------------------------------------ sources

    private static IEnumerable<string> RunningGame()
    {
        foreach (var name in new[] { "acs", "acs_x86", "AssettoCorsa" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in processes)
            {
                string? dir = null;
                try { dir = Path.GetDirectoryName(p.MainModule?.FileName); }
                catch { /* no access to that process */ }
                finally { p.Dispose(); }
                if (dir != null) yield return dir;
            }
        }
    }

    /// <summary>A library's AC folder, using the install folder name from Steam's app manifest when there is one.</summary>
    private static string ManifestRoot(string library)
    {
        var installDir = "assettocorsa";
        var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
        try
        {
            if (File.Exists(manifest)
                && Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"") is { Success: true } m)
                installDir = m.Groups[1].Value;
        }
        catch { /* unreadable manifest: use the default name */ }
        return Path.Combine(library, "steamapps", "common", installDir);
    }

    private static string? UninstallEntry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        foreach (var key in new[]
                 {
                     $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SteamAppId}",
                     $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SteamAppId}",
                 })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue("InstallLocation") is string path && path.Length > 0) return path;
            }
            catch { /* no access: try the next one */ }
        }
        return null;
    }

    private record LibraryInfo(string Path, bool HasAc);

    /// <summary>Steam libraries; HasAc = Steam's libraryfolders.vdf lists AC (app 244210) in that library.</summary>
    private static List<LibraryInfo> SteamLibraryInfo()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, key, value) in new[]
                     {
                         (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                         (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                         (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                     })
            {
                try
                {
                    using var k = hive.OpenSubKey(key);
                    if (k?.GetValue(value) is string path && Directory.Exists(path)) steamRoots.Add(Path.GetFullPath(path));
                }
                catch
                {
                    // No access to that key: try the next one.
                }
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steamRoots.Add(Path.Combine(home, ".steam", "steam"));
            steamRoots.Add(Path.Combine(home, ".local", "share", "Steam"));
        }

        var libraries = new List<LibraryInfo>();
        foreach (var steam in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string text;
                try { text = File.ReadAllText(vdf); }
                catch { text = ""; }
                // Each library block starts with its "path"; its "apps" list runs until the next "path".
                var paths = Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"");
                for (var i = 0; i < paths.Count; i++)
                {
                    var end = i + 1 < paths.Count ? paths[i + 1].Index : text.Length;
                    var block = text[paths[i].Index..end];
                    libraries.Add(new LibraryInfo(paths[i].Groups[1].Value.Replace(@"\\", @"\"), block.Contains($"\"{SteamAppId}\"")));
                }
            }
            libraries.Add(new LibraryInfo(steam, false));
        }

        // Keep the first entry per folder (a library listed with AC wins over the same folder listed without).
        return libraries
            .Where(l => Directory.Exists(l.Path))
            .Select(l => l with { Path = Path.GetFullPath(l.Path) })
            .GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(l => l.HasAc).First())
            .ToList();
    }

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "$WinREAgent",
        "node_modules", ".git", "AppData", "Config.Msi", "PerfLogs", "WindowsApps", "WinSxS",
    };

    /// <summary>Folder names that usually lead to a game install; these are searched first.</summary>
    private static readonly Regex LikelyFolder = new(@"steam|common|game|assetto|corsa|^ac|racing|sim|program files",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Search of every fixed drive (a few folders deep) for acs.exe next to content\cars. Folders with game-like
    /// names (Steam, steamapps, common, Games, Assetto Corsa, ...) are searched first, so a typical install is found
    /// in a second or two even on large drives.
    /// </summary>
    private static DateTime SafeWriteTime(string dir)
    {
        try { return Directory.GetLastWriteTimeUtc(dir); }
        catch { return DateTime.MinValue; }
    }

    /// <param name="limit">Give up after this long.</param>
    /// <param name="afterFirst">Once an install is found, keep looking for other copies only this much longer.</param>
    private static IEnumerable<string> ScanDrives(TimeSpan limit, TimeSpan afterFirst)
    {
        const int maxDepth = 6;
        var clock = Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
        };
        // Priority: depth, minus a bonus for every game-like folder name on the way down (lower = searched sooner).
        var queue = new PriorityQueue<(string Dir, int Depth, int Score), int>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            queue.Enqueue((d.RootDirectory.FullName, 0, 0), 0);
        var stopAt = limit;
        while (queue.Count > 0 && clock.Elapsed < stopAt)
        {
            var (dir, depth, score) = queue.Dequeue();
            if (IsAcRoot(dir))
            {
                if (stopAt == limit) stopAt = clock.Elapsed + afterFirst < limit ? clock.Elapsed + afterFirst : limit;
                yield return dir;
                continue; // don't search inside an install
            }
            if (depth >= maxDepth) continue;
            List<string> children;
            try { children = Directory.EnumerateDirectories(dir, "*", options).ToList(); }
            catch { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SkipFolders.Contains(name)) continue;
                var childScore = score + (LikelyFolder.IsMatch(name) ? 3 : 0);
                queue.Enqueue((child, depth + 1, childScore), (depth + 1) * 2 - childScore);
            }
        }
    }
}
