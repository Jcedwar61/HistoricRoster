using System.Diagnostics;

namespace HistoricRosterPlugin;

/// <summary>
/// Tracks and cars installed in another Assetto Corsa install on this PC (a second Steam library, an old copy) count
/// as installed: each track or car folder the other install has and this one doesn't is linked into this install's
/// content\tracks or content\cars (an NTFS junction: nothing is copied, deleting the link leaves the other install as
/// it was). The program, Content Manager and the game then see it like any other track or car. A folder this install
/// already has is never touched.
/// </summary>
public static class TrackLinks
{
    /// <summary>Links the tracks other installs have and this one doesn't. Returns the folders linked now.</summary>
    public static IReadOnlyList<string> LinkFromOtherInstalls(string contentFolder) => Link(contentFolder, "tracks", IsTrack);

    /// <summary>Links the cars other installs have and this one doesn't. Returns the folders linked now.</summary>
    public static IReadOnlyList<string> LinkCarsFromOtherInstalls(string contentFolder) => Link(contentFolder, "cars", IsCar);

    private static readonly Dictionary<string, DateTime> LastCheck = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Links any cars and tracks added to another install since the last check. Called whenever the content library is
    /// read (program, RosterTool and the server plugin alike), at most every 30 seconds per content folder.
    /// </summary>
    public static void LinkNewContent(string contentFolder)
    {
        lock (LastCheck)
        {
            if (LastCheck.TryGetValue(contentFolder, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(30)) return;
            LastCheck[contentFolder] = DateTime.UtcNow;
        }
        Link(contentFolder, "cars", IsCar);
        Link(contentFolder, "tracks", IsTrack);
    }

    private static IReadOnlyList<string> Link(string contentFolder, string kind, Func<string, bool> isContent)
    {
        var linked = new List<string>();
        if (!OperatingSystem.IsWindows()) return linked;
        try
        {
            var mine = Path.Combine(Path.GetFullPath(contentFolder), kind);
            if (!Directory.Exists(mine)) return linked;
            var mainRoot = Path.GetDirectoryName(Path.GetFullPath(contentFolder).TrimEnd('\\', '/'))!;
            foreach (var other in AcInstallLocator.FindAll(scanDrives: false))
            {
                if (other.Root.Equals(mainRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var theirs = Path.Combine(other.Root, "content", kind);
                if (!Directory.Exists(theirs)) continue;
                foreach (var dir in Directory.EnumerateDirectories(theirs))
                {
                    var name = Path.GetFileName(dir);
                    var link = Path.Combine(mine, name);
                    if (Directory.Exists(link) || File.Exists(link)) continue; // this install has it (or a link already)
                    if (IsLink(dir) || !isContent(dir)) continue;             // never chain links between installs
                    if (Junction(link, dir)) linked.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // Never in the way of opening the program: the other install's content just isn't linked this time.
        }
        return linked;
    }

    private static bool IsTrack(string dir)
    {
        var ui = Path.Combine(dir, "ui");
        return File.Exists(Path.Combine(ui, "ui_track.json"))
               || (Directory.Exists(ui) && Directory.EnumerateFiles(ui, "ui_track.json", SearchOption.AllDirectories).Any());
    }

    /// <summary>A playable car: ui_car.json, physics (data.acd or data\) and a model.</summary>
    private static bool IsCar(string dir)
        => File.Exists(Path.Combine(dir, "ui", "ui_car.json"))
           && (File.Exists(Path.Combine(dir, "data.acd")) || Directory.Exists(Path.Combine(dir, "data")))
           && Directory.EnumerateFiles(dir, "*.kn5").Any();

    private static bool IsLink(string dir)
    {
        try { return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; }
    }

    // A directory junction needs no administrator rights (a symbolic link would) and works across local drives.
    private static bool Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        });
        p?.WaitForExit(10000);
        return Directory.Exists(link);
    }
}
