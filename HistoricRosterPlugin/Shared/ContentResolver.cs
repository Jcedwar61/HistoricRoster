using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>Settings for <see cref="ContentResolver"/>; built from the plugin config or the tool's arguments.</summary>
public record ResolverOptions
{
    /// <summary>AC "content" folder holding cars/ and tracks/.</summary>
    public required string ContentFolder { get; init; }
    /// <summary>Folder holding content_catalog.json (and content_catalog.*.json extras).</summary>
    public required string CatalogFolder { get; init; }
    public string CatalogFile { get; init; } = "content_catalog.json";
    public string DownloadSearchUrl { get; init; } = "https://www.overtake.gg/search/?q={query}&t=resource";
    /// <summary>Official content: Steam's list of every Assetto Corsa DLC.</summary>
    public string KunosContentUrl { get; init; } = DefaultKunosContentUrl;
    public string FallbackSkin { get; init; } = "";
    /// <summary>More places to search for a missing mod, name -> URL with {query}. Offered next to the main link.</summary>
    public IReadOnlyDictionary<string, string> ExtraSearchUrls { get; init; } = DefaultExtraSearchUrls;

    public const string DefaultKunosContentUrl = "https://store.steampowered.com/dlc/244210/";

    /// <summary>Checked Nov 2026: each returns search results for a plain GET link.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultExtraSearchUrls = new Dictionary<string, string>
    {
        ["acmods.net"] = "https://www.acmods.net/?s={query}",
        ["Google"] = "https://www.google.com/search?q={query}+assetto+corsa+mod+download",
    };
}

/// <summary>One place to get a missing car or track.</summary>
public record DownloadSource(string Name, string Url);

/// <param name="Model">The real car's AC folder, or null when no AC folder is known for it yet.</param>
/// <param name="Match">How the model was found: "entry" (roster names it), "catalog" (a catalog rule), or "none".</param>
/// <param name="DownloadUrl">Where to get the real car.</param>
public record ResolvedCar(RosterEntry Entry, string? Model, string? Skin, bool Installed, bool Kunos, string DownloadUrl,
    string Label, string Match)
{
    /// <summary>Every place to get it: the main link (<see cref="DownloadUrl"/>) first, then other sites.</summary>
    public IReadOnlyList<DownloadSource> Sources { get; init; } = Array.Empty<DownloadSource>();
}

/// <param name="Folder">The real circuit's AC folder, or null when none is known yet.</param>
public record ResolvedTrack(string? Folder, string? Layout, bool Installed, bool Kunos, string DownloadUrl, string Label, int? PitBoxes)
{
    /// <summary>Every place to get it: the main link first, then other sites.</summary>
    public IReadOnlyList<DownloadSource> Sources { get; init; } = Array.Empty<DownloadSource>();
}

public record RosterContent(Roster Roster, ResolvedTrack? Track, IReadOnlyList<ResolvedCar> Cars)
{
    /// <summary>Like <see cref="Missing"/>, with every download source of each item.</summary>
    public IReadOnlyList<(string Label, IReadOnlyList<DownloadSource> Sources)> MissingSources()
    {
        var list = new List<(string, IReadOnlyList<DownloadSource>)>();
        if (Track != null && !Track.Installed) list.Add((Track.Label, Track.Sources));
        foreach (var group in Cars.Where(c => !c.Installed).GroupBy(c => c.Model ?? "?" + c.Label))
            list.Add((group.First().Label, group.First().Sources));
        return list;
    }

    /// <summary>Real cars/track that aren't installed (or have no known AC folder), one line each, with download links.</summary>
    public IReadOnlyList<(string Label, string Url)> Missing()
    {
        var list = new List<(string, string)>();
        if (Track != null && !Track.Installed) list.Add((Track.Label, Track.DownloadUrl));
        foreach (var group in Cars.Where(c => !c.Installed).GroupBy(c => c.Model ?? "?" + c.Label))
        {
            var car = group.First();
            list.Add((car.Label, car.DownloadUrl));
        }
        return list;
    }

    /// <summary>Entries that can be driven: their real car is installed.</summary>
    public IEnumerable<ResolvedCar> Ready => Cars.Where(c => c.Installed);
}

/// <summary>
/// Works out the real AC car and track folders a roster needs, whether they are installed,
/// and where to download them. It never substitutes a different car or track: if the real one
/// isn't known or installed, it is reported as missing.
/// </summary>
public class ContentResolver
{
    private readonly ResolverOptions _options;

    public ContentResolver(ResolverOptions options)
    {
        _options = options;
    }

    public string ContentFolder => _options.ContentFolder;

    /// <summary>Installed cars/tracks index (used to pick the right livery of the real car).</summary>
    public ContentLibrary? Library => Directory.Exists(Path.Combine(_options.ContentFolder, "cars"))
        ? ContentLibrary.Load(_options.ContentFolder)
        : null;

    /// <summary>
    /// Loads every catalog file in the catalog folder: extra files (content_catalog.*.json, e.g. championship imports
    /// with exact per-driver cars) first, then the main catalog (hand-checked rules, incl. every official Kunos car),
    /// then the automatic scan (content_catalog.installed.json), whose year windows are guesses. Re-read on every
    /// call so admins can edit catalogs without restarting.
    /// </summary>
    public ContentCatalog LoadCatalog()
    {
        var merged = new ContentCatalog();
        if (!Directory.Exists(_options.CatalogFolder)) return merged;

        var main = Path.Combine(_options.CatalogFolder, _options.CatalogFile);
        var prefix = Path.GetFileNameWithoutExtension(_options.CatalogFile) + ".";
        bool IsScan(string p) => Path.GetFileName(p).Equals(prefix + "installed.json", StringComparison.OrdinalIgnoreCase);
        var extras = Directory.EnumerateFiles(_options.CatalogFolder, prefix + "*.json")
            .Where(p => !string.Equals(Path.GetFullPath(p), Path.GetFullPath(main), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in extras.Where(p => !IsScan(p)).Append(main).Concat(extras.Where(IsScan)))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var catalog = JsonSerializer.Deserialize<ContentCatalog>(File.ReadAllText(path), RosterJson.Options);
                if (catalog == null) continue;
                merged.Cars.AddRange(catalog.Cars);
                merged.Tracks.AddRange(catalog.Tracks);
                merged.Balance.AddRange(catalog.Balance);
            }
            catch (Exception ex)
            {
                RosterLog.Warn($"content catalog {path} is invalid: {ex.Message}");
            }
        }
        return merged;
    }

    /// <summary>Resolves a roster. Pass a catalog loaded once with <see cref="LoadCatalog"/> when resolving many rosters.</summary>
    public RosterContent Resolve(Roster roster, ContentCatalog? preloadedCatalog = null)
    {
        var catalog = preloadedCatalog ?? LoadCatalog();
        var library = Library;
        var track = roster.Track == null ? null : ResolveTrack(roster.Track, roster.Year, roster.Category, catalog);
        var cars = roster.Entries.Select(e => ResolveCar(e, roster, catalog, library)).ToList();
        return new RosterContent(roster, track, cars);
    }

    private ResolvedCar ResolveCar(RosterEntry entry, Roster roster, ContentCatalog catalog, ContentLibrary? library)
    {
        var rules = catalog.Cars.Where(r => r.Matches(roster.Category, roster.Year, entry) && r.MatchesTrack(roster.Track)).ToList();
        // The entry's own model wins; otherwise the first catalog rule naming an INSTALLED AC folder of this real car,
        // else the first rule naming any folder (reported as missing).
        var modelRule = Clean(entry.Model) == null
            ? rules.FirstOrDefault(r => Clean(r.Model) is { } m && Directory.Exists(Path.Combine(_options.ContentFolder, "cars", m)))
              ?? rules.FirstOrDefault(r => Clean(r.Model) != null)
            : null;
        var model = Clean(entry.Model) ?? Clean(modelRule?.Model);
        var match = Clean(entry.Model) != null ? "entry" : model != null ? "catalog" : "none";
        var installed = model != null && Directory.Exists(Path.Combine(_options.ContentFolder, "cars", model));

        // Livery of the real car: roster/catalog skin, else the installed skin with this car's number/driver/team.
        var skin = Clean(entry.Skin) ?? Clean(modelRule?.Skin)
                   ?? (installed ? library?.SkinFor(model!, entry) : null)
                   ?? Clean(_options.FallbackSkin);
        var kunos = modelRule?.Kunos == true || (model?.StartsWith("ks_", StringComparison.OrdinalIgnoreCase) ?? false);
        var label = entry.Car ?? Join(roster.Year?.ToString(), entry.Team, roster.Category) ?? entry.Driver;
        if (model != null) label += $" [{model}]";

        // Download link: exact page for this entry, a catalog page for this car, Steam for Kunos, else a search.
        var searchRule = rules.FirstOrDefault(r => r.Search != null);
        var search = searchRule != null
            ? Fill(searchRule.Search!, roster, entry)
            : entry.Car ?? Join(roster.Year?.ToString(), entry.Team, roster.Category) ?? model ?? entry.Driver;
        var url = Clean(entry.DownloadUrl)
                  ?? Clean(modelRule?.Url)
                  ?? (kunos ? _options.KunosContentUrl : null)
                  ?? Clean(rules.FirstOrDefault(r => Clean(r.Url) != null)?.Url)
                  ?? SearchUrl(search);

        return new ResolvedCar(entry, model, skin, installed, kunos, url, label, match) { Sources = Sources(url, kunos, search) };
    }

    /// <summary>The main link, then the overtake.gg search (when the main link is a page) and the other search sites.</summary>
    /// <summary>Search links on every download site for a free-text query (e.g. a livery pack).</summary>
    public IReadOnlyList<DownloadSource> SearchSources(string query) => Sources(SearchUrl(query), false, query);

    private IReadOnlyList<DownloadSource> Sources(string mainUrl, bool kunos, string query)
    {
        var list = new List<DownloadSource> { new(SourceName(mainUrl, kunos), mainUrl) };
        if (kunos) return list; // official content only comes from Steam
        var search = SearchUrl(query);
        if (search != mainUrl) list.Add(new DownloadSource(SourceName(search, false) + " search", search));
        foreach (var (name, template) in _options.ExtraSearchUrls)
        {
            if (!template.Contains("{query}")) continue;
            var url = template.Replace("{query}", Uri.EscapeDataString(query.Trim()));
            if (list.All(s => s.Url != url)) list.Add(new DownloadSource(name, url));
        }
        return list;
    }

    private static string SourceName(string url, bool kunos)
    {
        if (kunos) return "Steam (official DLC)";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Download";
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host;
    }

    private ResolvedTrack ResolveTrack(RosterTrack track, int? year, string? category, ContentCatalog catalog)
    {
        var rules = catalog.Tracks.Where(r => r.Matches(track, year, category)).ToList();
        bool TrackInstalled(TrackRule r) => Clean(r.Folder) is { } f
            && Directory.Exists(Clean(r.Layout) is { } l ? Path.Combine(_options.ContentFolder, "tracks", f, l) : Path.Combine(_options.ContentFolder, "tracks", f));
        // First rule naming an installed track, else the first naming any track (reported as missing).
        var folderRule = Clean(track.Folder) == null
            ? rules.FirstOrDefault(TrackInstalled) ?? rules.FirstOrDefault(r => Clean(r.Folder) != null)
            : null;
        // A link-only rule (no folder) still provides the download page for the real circuit.
        var rule = folderRule ?? rules.FirstOrDefault();
        var folder = Clean(track.Folder) ?? Clean(folderRule?.Folder);
        // Only take the rule's layout when the folder also came from the rule.
        var layout = Clean(track.Folder) != null ? Clean(track.Layout) : Clean(track.Layout) ?? Clean(folderRule?.Layout);
        var kunos = folderRule?.Kunos == true || (folder?.StartsWith("ks_", StringComparison.OrdinalIgnoreCase) ?? false);

        var installed = false;
        if (folder != null)
        {
            var trackDir = Path.Combine(_options.ContentFolder, "tracks", folder);
            installed = layout == null ? Directory.Exists(trackDir) : Directory.Exists(Path.Combine(trackDir, layout));
        }

        var label = track.Name + (folder != null ? $" [{folder}{(layout != null ? "/" + layout : "")}]" : "");
        var query = rule?.Search ?? track.Name;
        var url = Clean(track.DownloadUrl) ?? Clean(rule?.Url)
                  ?? (kunos ? _options.KunosContentUrl : SearchUrl(query));

        return new ResolvedTrack(folder, layout, installed, kunos, url, label, installed ? ReadPitBoxes(folder!, layout) : null)
            { Sources = Sources(url, kunos, query) };
    }

    /// <summary>Reads "pitboxes" from the track's ui_track.json, if present.</summary>
    private int? ReadPitBoxes(string folder, string? layout)
    {
        var ui = layout == null
            ? Path.Combine(_options.ContentFolder, "tracks", folder, "ui", "ui_track.json")
            : Path.Combine(_options.ContentFolder, "tracks", folder, "ui", layout, "ui_track.json");
        if (!File.Exists(ui)) return null;
        try
        {
            // AC ui files often contain raw tabs/newlines inside strings, which strict JSON rejects.
            var text = File.ReadAllText(ui).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ");
            using var doc = JsonDocument.Parse(text, RosterJson.Lenient);
            if (!doc.RootElement.TryGetProperty("pitboxes", out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s)) return s;
        }
        catch (Exception ex)
        {
            RosterLog.Info($"could not read pit boxes from {ui}: {ex.Message}");
        }
        return null;
    }

    public string SearchUrl(string query)
        => _options.DownloadSearchUrl.Replace("{query}", Uri.EscapeDataString(query.Trim()));

    private static string Fill(string template, Roster roster, RosterEntry entry) => template
        .Replace("{year}", roster.Year?.ToString() ?? "")
        .Replace("{team}", entry.Team ?? "")
        .Replace("{car}", entry.Car ?? "")
        .Replace("{category}", roster.Category ?? "")
        .Trim();

    private static string? Join(params string?[] parts)
    {
        var joined = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
        return joined.Length == 0 ? null : joined;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
