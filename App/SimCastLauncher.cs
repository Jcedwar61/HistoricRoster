using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HistoricRosterApp;

/// <summary>
/// Finds and starts SimCast (live TV commentary for sim races), so it's on air when Assetto Corsa starts. SimCast
/// reads the race context Historic Roster writes (HistoricRosterPlugin.SimCastContext).
/// </summary>
public static class SimCastLauncher
{
    /// <summary>SimCast.exe from its installer's uninstall entry, else its default install folder; null when not installed.</summary>
    public static string? Exe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SimCast");
            if (key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "SimCast.exe")))
                return Path.Combine(dir, "SimCast.exe");
        }
        catch { /* no access: try the default folder */ }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "SimCast", "SimCast.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// True while SimCast's window is open. SimCast runs as Python behind its SimCast.exe launcher, so it's found by
    /// its main window title ("SimCast 1.2" today), not by a process name. Any title starting with "SimCast" counts,
    /// so a new version's title still matches; an Explorer window on a "SimCast" folder doesn't.
    /// </summary>
    public static bool Running
    {
        get
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowTitle.StartsWith("SimCast", StringComparison.OrdinalIgnoreCase)
                        && !p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { /* exited or no access */ }
                finally { p.Dispose(); }
            }
            return false;
        }
    }

    /// <summary>
    /// SimCast is on this PC: installed (any version), or its settings folder is there (a copy run from source).
    /// </summary>
    public static bool Installed => Exe() != null || HistoricRosterPlugin.SimCastContext.SimCastInstalled;

    /// <summary>Starts SimCast unless it's already open. Returns what happened, for the log and status line.</summary>
    public static string StartIfNeeded()
    {
        if (Running) return "SimCast is already open.";
        var exe = Exe();
        if (exe == null) return "SimCast isn't installed.";
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        return "Starting SimCast.";
    }
}
