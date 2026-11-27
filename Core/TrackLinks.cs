using System.Diagnostics;

namespace HistoricRosterPlugin;

/// <summary>
/// Tracks installed in another Assetto Corsa install on this PC (a second Steam library, an old copy) count as
/// installed: each track folder the other install has and this one doesn't is linked into this install's
/// content\tracks (an NTFS junction: nothing is copied, deleting the link leaves the other install as it was). The
/// program, Content Manager and the game then see it like any other track. A folder this install already has is never
/// touched.
/// </summary>
public static class TrackLinks
{
    /// <summary>Links the tracks other installs have and this one doesn't. Returns the folders linked now.</summary>
    public static IReadOnlyList<string> LinkFromOtherInstalls(string contentFolder)
    {
        var linked = new List<string>();
        if (!OperatingSystem.IsWindows()) return linked;
        try
        {
            var mine = Path.Combine(Path.GetFullPath(contentFolder), "tracks");
            if (!Directory.Exists(mine)) return linked;
            var mainRoot = Path.GetDirectoryName(Path.GetFullPath(contentFolder).TrimEnd('\\', '/'))!;
            foreach (var other in AcInstallLocator.FindAll(scanDrives: false))
            {
                if (other.Root.Equals(mainRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var theirs = Path.Combine(other.Root, "content", "tracks");
                if (!Directory.Exists(theirs)) continue;
                foreach (var dir in Directory.EnumerateDirectories(theirs))
                {
                    var name = Path.GetFileName(dir);
                    var link = Path.Combine(mine, name);
                    if (Directory.Exists(link) || File.Exists(link)) continue; // this install has it (or a link already)
                    if (!IsTrack(dir)) continue;
                    if (Junction(link, dir)) linked.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // Never in the way of opening the program: the other install's tracks just aren't linked this time.
        }
        return linked;
    }

    private static bool IsTrack(string dir)
    {
        var ui = Path.Combine(dir, "ui");
        return File.Exists(Path.Combine(ui, "ui_track.json"))
               || (Directory.Exists(ui) && Directory.EnumerateFiles(ui, "ui_track.json", SearchOption.AllDirectories).Any());
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
