using System.Diagnostics;

namespace HistoricRosterPlugin;

/// <summary>One installed car in a race and the drivers in it that don't have a livery of their own.</summary>
/// <param name="CarName">The installed car's display name (from ui_car.json).</param>
/// <param name="Without">Entries using this car with no livery of their own (they show someone else's).</param>
public record LiveryNeed(string Model, string CarName, int? Year, List<RosterEntry> Without, int Drivers);

/// <summary>Which drivers of a race have their own livery, and getting the missing ones.</summary>
public static class Liveries
{
    /// <summary>Generated liveries live in folders starting with this, so they can be told apart and removed.</summary>
    public const string GeneratedPrefix = "hr_";

    /// <summary>Per installed car: the race's drivers in it without a livery of their own.</summary>
    public static List<LiveryNeed> Needs(IEnumerable<(ResolvedCar Car, string Model)> cars, ContentLibrary library, int? year)
    {
        var list = new List<LiveryNeed>();
        foreach (var g in cars.GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase))
        {
            var info = library.Cars.FirstOrDefault(c => c.Folder.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            var without = g.Select(x => x.Car.Entry).Where(e => library.OwnSkin(g.Key, e) == null).ToList();
            if (without.Count > 0) list.Add(new LiveryNeed(g.Key, info.Name, info.Year ?? year, without, g.Count()));
        }
        return list;
    }

    /// <summary>Where to look for a livery pack for this car (download sites, searched by car and season).</summary>
    public static IReadOnlyList<DownloadSource> Sources(ContentResolver resolver, LiveryNeed need, Roster roster)
    {
        // "Dodge RAM 1500 Stockcar 2026 (RealSpec)" -> "Dodge RAM 1500 Stockcar"; then the season and "skins".
        var name = System.Text.RegularExpressions.Regex.Replace(need.CarName, @"\([^)]*\)|\[[^\]]*\]|(19|20)\d\d", " ").Trim();
        var season = roster.Year ?? need.Year;
        return resolver.SearchSources($"{name} {season} skins".Replace("  ", " "));
    }

    /// <summary>
    /// Opens the livery search for each car in the browser, then watches the Downloads folder: every archive you
    /// download there is installed (liveries into the car they belong to; bare livery folders into <paramref name="needs"/>'s
    /// car when there is just one). Stops when every driver has a livery, or after <paramref name="watchMinutes"/>.
    /// </summary>
    public static async Task<int> FindAsync(List<LiveryNeed> needs, Roster roster, ContentResolver resolver, string content,
        string downloads, int watchMinutes, IProgress<string> log, CancellationToken ct, Func<int> stillWithout)
    {
        foreach (var need in needs)
        {
            var source = Sources(resolver, need, roster).First();
            log.Report($"Opening a livery search for the {need.CarName} ({need.Without.Count} driver(s) without their own livery): {source.Name}");
            Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true });
        }
        var target = needs.Count == 1 ? needs[0].Model : null;
        log.Report($"Download the livery packs in your browser. Watching {downloads} for {watchMinutes} min; they are installed as they arrive.");

        var start = DateTime.Now.AddSeconds(-5);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, long>();
        var installed = 0;
        var until = DateTime.Now.AddMinutes(watchMinutes);
        while (DateTime.Now < until)
        {
            await Task.Delay(3000, ct);
            if (!Directory.Exists(downloads)) continue;
            foreach (var file in Directory.EnumerateFiles(downloads))
            {
                if (done.Contains(file) || !ArchiveInstaller.ArchiveExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                var info = new FileInfo(file);
                if (info.LastWriteTime < start) continue;
                if (!sizes.TryGetValue(file, out var last) || last != info.Length) { sizes[file] = info.Length; continue; }
                done.Add(file);
                log.Report($"Installing {info.Name} ...");
                var r = ArchiveInstaller.Install(file, new[] { content }, overwrite: false, liveryCar: target);
                ContentGetter.Report(r, log);
                installed += r.Skins?.Count ?? 0;
                var left = stillWithout();
                log.Report(left == 0 ? "Every driver has a livery of their own now." : $"{left} driver(s) still without their own livery.");
                if (left == 0) return installed;
            }
        }
        log.Report("Stopped watching the Downloads folder.");
        return installed;
    }

    /// <summary>Removes every generated livery (folders starting with hr_) of a car. Returns how many.</summary>
    public static int RemoveGenerated(string content, string model)
    {
        var skins = Path.Combine(content, "cars", model, "skins");
        if (!Directory.Exists(skins)) return 0;
        var n = 0;
        foreach (var dir in Directory.EnumerateDirectories(skins, GeneratedPrefix + "*"))
        {
            Directory.Delete(dir, recursive: true);
            n++;
        }
        if (n > 0) ContentLibrary.Invalidate();
        return n;
    }
}
