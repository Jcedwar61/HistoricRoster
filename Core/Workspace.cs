using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>A real car or track a roster needs, with where to get it. Folder null = no AC folder known yet.</summary>
/// <param name="Sources">Every place to get it (main link first); null = only <paramref name="Url"/>.</param>
public record WantedItem(string Kind, string? Folder, string? Layout, string Label, string Url, IReadOnlyList<DownloadSource>? Sources = null)
{
    public bool IsKunos => Url.Contains("store.steampowered.com");

    /// <summary>The sources other than the main link.</summary>
    public IReadOnlyList<DownloadSource> OtherSources => Sources?.Where(s => s.Url != Url).ToList() ?? new List<DownloadSource>();

    public bool IsInstalled(string content) => Folder != null && (Kind == "track"
        ? Directory.Exists(Layout == null ? Path.Combine(content, "tracks", Folder) : Path.Combine(content, "tracks", Folder, Layout))
        : Directory.Exists(Path.Combine(content, "cars", Folder)));
}

/// <summary>
/// Where everything lives: the Assetto Corsa install and the roster folder (database, catalogs, roster files).
/// </summary>
public class Workspace
{
    public string AcRoot { get; }
    public string ContentFolder { get; }
    public string RostersFolder { get; }
    public string DatabasePath => Path.Combine(RostersFolder, RosterDatabase.DefaultFileName);

    public Workspace(string contentFolder, string rostersFolder, string? bundledRosters = null)
    {
        ContentFolder = Path.GetFullPath(contentFolder);
        AcRoot = Path.GetDirectoryName(ContentFolder)!;
        RostersFolder = Path.GetFullPath(rostersFolder);
        Directory.CreateDirectory(RostersFolder);

        // First run: copy the bundled catalog/database next to the program, never overwriting.
        if (bundledRosters != null && Directory.Exists(bundledRosters)
            && !string.Equals(Path.GetFullPath(bundledRosters), RostersFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(bundledRosters, "*.json"))
            {
                var target = Path.Combine(RostersFolder, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
    }

    /// <summary>Finds Assetto Corsa (or uses the given content folder).</summary>
    public static Workspace Open(string? contentFolder, string rostersFolder, string? bundledRosters = null)
    {
        var content = contentFolder ?? AcInstallLocator.FindContentFolder();
        if (content == null || !Directory.Exists(Path.Combine(content, "cars")))
            throw new InvalidOperationException("Assetto Corsa not found. Set its content folder (…\\assettocorsa\\content).");
        // Tracks and cars in another AC install on this PC count as installed (linked in, nothing copied).
        var linked = TrackLinks.LinkFromOtherInstalls(content);
        var linkedCars = TrackLinks.LinkCarsFromOtherInstalls(content);
        return new Workspace(content, rostersFolder, bundledRosters) { LinkedTracks = linked, LinkedCars = linkedCars };
    }

    /// <summary>Track folders linked in from another Assetto Corsa install when this workspace was opened.</summary>
    public IReadOnlyList<string> LinkedTracks { get; private init; } = Array.Empty<string>();

    /// <summary>Car folders linked in from another Assetto Corsa install when this workspace was opened.</summary>
    public IReadOnlyList<string> LinkedCars { get; private init; } = Array.Empty<string>();

    public ContentResolver CreateResolver() => new(new ResolverOptions { ContentFolder = ContentFolder, CatalogFolder = RostersFolder });

    public ContentLibrary Library => ContentLibrary.Load(ContentFolder);

    /// <summary>Documents\Assetto Corsa\cfg\race.ini (HISTORICROSTER_RACE_INI overrides it, for testing).</summary>
    public string RaceIniPath => Environment.GetEnvironmentVariable("HISTORICROSTER_RACE_INI") is { Length: > 0 } test
        ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assetto Corsa", "cfg", "race.ini");

    public string ChampsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assetto Corsa", "champs");

    /// <summary>All rosters: single roster files win over database entries with the same id.</summary>
    public List<Roster> LoadRosters()
    {
        var result = new List<Roster>();
        var ids = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(RostersFolder, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('_') || name.StartsWith("content_catalog") || name == RosterDatabase.DefaultFileName) continue;
            Roster? r = null;
            try { r = JsonSerializer.Deserialize<Roster>(File.ReadAllText(file), RosterJson.Options); } catch { /* not a roster */ }
            if (r == null || r.Entries.Count == 0) continue;
            r.Id = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(r.Name)) r.Name = r.Id;
            ids.Add(r.Id);
            result.Add(r);
        }
        if (File.Exists(DatabasePath))
            result.AddRange(RosterDatabase.Load(DatabasePath).Rosters.Where(r => !ids.Contains(r.Id)));
        return result;
    }

    /// <summary>What a roster still needs on this PC: the real track and the real cars that aren't installed.</summary>
    public static List<WantedItem> MissingItems(RosterContent c)
    {
        var items = new List<WantedItem>();
        if (c.Track is { Installed: false } t) items.Add(new WantedItem("track", t.Folder, t.Layout, t.Label, t.DownloadUrl, t.Sources));
        foreach (var car in c.Cars.Where(x => !x.Installed).GroupBy(x => x.Model ?? "?" + x.Label).Select(g => g.First()))
            items.Add(new WantedItem("car", car.Model, null, car.Label, car.DownloadUrl, car.Sources));
        return items;
    }
}
