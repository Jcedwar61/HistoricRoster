using System.Diagnostics;
using System.IO.Compression;

namespace HistoricRosterPlugin;

/// <summary>
/// Installs car and track mods from a downloaded archive into an AC content folder, like dropping the
/// archive on Content Manager: it finds every car folder (data.acd or data\ + ui\ui_car.json) and every
/// track folder (ui\ui_track.json + .kn5 files) inside the archive and copies them to content\cars or content\tracks.
/// Nothing from the archive is ever executed.
/// </summary>
public static class ArchiveInstaller
{
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

    /// <param name="Skins">Liveries installed, as "car/livery".</param>
    public record Result(List<string> Cars, List<string> Tracks, List<string> Skipped, string? Error, List<string>? Skins = null);

    /// <param name="liveryCar">
    /// The car folder that loose liveries in the archive belong to (a pack of bare livery folders, without the
    /// content\cars\&lt;car&gt;\skins path). Liveries inside a cars\&lt;car&gt;\skins path always go to that car.
    /// </param>
    public static Result Install(string archive, IReadOnlyList<string> contentFolders, bool overwrite, string? liveryCar = null)
    {
        var cars = new List<string>();
        var tracks = new List<string>();
        var skins = new List<string>();
        var skipped = new List<string>();
        var temp = Path.Combine(Path.GetTempPath(), "RosterTool-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var error = Extract(archive, temp);
            if (error != null) return new Result(cars, tracks, skipped, error, skins);

            foreach (var (kind, dir) in FindContent(temp))
            {
                var name = Path.GetFileName(dir);
                foreach (var content in contentFolders)
                {
                    var target = Path.Combine(content, kind, name);
                    if (Directory.Exists(target) && !overwrite)
                    {
                        // The car is already installed: still add the liveries the archive has that you don't.
                        if (kind == "cars") skins.AddRange(MergeSkins(Path.Combine(dir, "skins"), Path.Combine(target, "skins"), name));
                        skipped.Add($"{kind}/{name} (already in {content})");
                        continue;
                    }
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    CopyDirectory(dir, target);
                }
                (kind == "cars" ? cars : tracks).Add(name);
            }

            // Livery packs: skin folders of cars that aren't in the archive themselves.
            foreach (var (car, skinDir) in FindSkins(temp, liveryCar))
            {
                var installedSomewhere = false;
                foreach (var content in contentFolders)
                {
                    var carDir = Path.Combine(content, "cars", car);
                    if (!Directory.Exists(carDir)) continue;
                    installedSomewhere = true;
                    var target = Path.Combine(carDir, "skins", Path.GetFileName(skinDir));
                    if (Directory.Exists(target) && !overwrite) { skipped.Add($"livery {car}/{Path.GetFileName(skinDir)} (already installed)"); continue; }
                    CopyDirectory(skinDir, target);
                    skins.Add($"{car}/{Path.GetFileName(skinDir)}");
                }
                if (!installedSomewhere) skipped.Add($"livery {car}/{Path.GetFileName(skinDir)} (car {car} isn't installed)");
            }

            skins = skins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (skins.Count > 0) ContentLibrary.Invalidate();
            return new Result(cars, tracks, skipped,
                cars.Count + tracks.Count + skins.Count == 0 && skipped.Count == 0 ? "No car, track or livery found in the archive." : null, skins);
        }
        catch (Exception ex)
        {
            return new Result(cars, tracks, skipped, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { /* temp cleanup only */ }
        }
    }

    private static string? Extract(string archive, string target)
    {
        Directory.CreateDirectory(target);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // ExtractToDirectory refuses entries that would land outside the target folder.
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
            return null;
        }

        var sevenZip = FindSevenZip();
        if (sevenZip == null)
            return "Needs 7-Zip to open .7z/.rar files (https://www.7-zip.org). Or drag the archive onto Content Manager.";

        var psi = new ProcessStartInfo(sevenZip)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "x", archive, "-o" + target, "-y", "-bso0", "-bsp0" }) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10 * 60 * 1000);
        return p.ExitCode == 0 ? null : $"7-Zip failed: {p.StandardError.ReadToEnd().Trim()}";
    }

    private static string? FindSevenZip()
    {
        foreach (var path in new[]
                 {
                     Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\7-Zip\7z.exe"),
                     Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\7-Zip\7z.exe"),
                 })
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>Car and track folders anywhere in the extracted tree (not looking inside a found folder).</summary>
    public static IEnumerable<(string Kind, string Dir)> FindContent(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (IsCar(dir)) { yield return ("cars", dir); continue; }
            if (IsTrack(dir)) { yield return ("tracks", dir); continue; }
            foreach (var sub in Directory.EnumerateDirectories(dir)) stack.Push(sub);
        }
    }

    /// <summary>A livery folder: a skin's own files (ui_skin.json, preview.jpg, or textures) and no car/track inside.</summary>
    public static bool IsSkin(string dir)
        => !IsCar(dir) && !IsTrack(dir)
           && (File.Exists(Path.Combine(dir, "ui_skin.json")) || File.Exists(Path.Combine(dir, "preview.jpg"))
               || (Directory.EnumerateFiles(dir, "*.dds").Any() && !Directory.EnumerateDirectories(dir).Any()));

    /// <summary>
    /// Livery folders in an extracted archive whose car isn't in the archive: ...\cars\&lt;car&gt;\skins\&lt;livery&gt; (or
    /// ...\&lt;car&gt;\skins\&lt;livery&gt;), and, when <paramref name="liveryCar"/> is given, bare livery folders anywhere else.
    /// </summary>
    public static IEnumerable<(string Car, string SkinDir)> FindSkins(string root, string? liveryCar)
    {
        var cars = FindContent(root).Where(c => c.Kind == "cars").Select(c => Path.GetFullPath(c.Dir)).ToList();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(dir);
            if (cars.Any(c => full.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            if (!IsSkin(dir)) continue;
            var parent = Path.GetDirectoryName(dir)!;
            if (Path.GetFileName(parent).Equals("skins", StringComparison.OrdinalIgnoreCase))
                yield return (Path.GetFileName(Path.GetDirectoryName(parent)!), dir);
            else if (liveryCar != null && !IsSkin(parent))
                yield return (liveryCar, dir);
        }
    }

    /// <summary>Copies the liveries of an archive's car that the installed car doesn't have yet.</summary>
    private static List<string> MergeSkins(string fromSkins, string toSkins, string car)
    {
        var added = new List<string>();
        if (!Directory.Exists(fromSkins)) return added;
        foreach (var skin in Directory.EnumerateDirectories(fromSkins))
        {
            var target = Path.Combine(toSkins, Path.GetFileName(skin));
            if (Directory.Exists(target)) continue;
            CopyDirectory(skin, target);
            added.Add($"{car}/{Path.GetFileName(skin)}");
        }
        return added;
    }

    private static bool IsCar(string dir)
        => File.Exists(Path.Combine(dir, "ui", "ui_car.json"))
           && (File.Exists(Path.Combine(dir, "data.acd")) || Directory.Exists(Path.Combine(dir, "data")));

    private static bool IsTrack(string dir)
    {
        var ui = Path.Combine(dir, "ui");
        if (!Directory.Exists(ui)) return false;
        var hasUi = File.Exists(Path.Combine(ui, "ui_track.json"))
                    || Directory.EnumerateDirectories(ui).Any(d => File.Exists(Path.Combine(d, "ui_track.json")));
        var hasModel = Directory.EnumerateFiles(dir, "*.kn5").Any() || Directory.EnumerateFiles(dir, "models*.ini").Any();
        return hasUi && hasModel && !File.Exists(Path.Combine(ui, "ui_car.json"));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
