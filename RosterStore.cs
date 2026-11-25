using System.Text;
using System.Text.Json;
using Serilog;

namespace HistoricRosterPlugin;

/// <summary>
/// Rosters come from three places in the roster folder: one JSON file per roster, the bulk database
/// historic_rosters_db.json built by RosterTool, and races captured on this server (captured/*.json, listed last).
/// A file wins over a database entry with the same id.
/// </summary>
public class RosterStore
{
    private const string ActiveFileName = "_active.json";
    private const string SeatsFileName = "_seats.json";
    public const string CapturedCategory = "Captured";

    private readonly object _lock = new();
    private readonly string _catalogPrefix;
    private readonly string _databasePath;
    private (DateTime Stamp, IReadOnlyList<Roster> Rosters)? _dbCache;

    public string Folder { get; }
    /// <summary>Races captured on this server, kept apart so they don't bury the real races.</summary>
    public string CapturedFolder { get; }

    public RosterStore(HistoricRosterConfiguration configuration)
    {
        _catalogPrefix = Path.GetFileNameWithoutExtension(configuration.CatalogFile);
        Folder = Path.GetFullPath(configuration.RosterFolder);
        CapturedFolder = Path.Combine(Folder, "captured");
        _databasePath = Path.Combine(Folder, configuration.DatabaseFile);
        Directory.CreateDirectory(Folder);
        SeedBundledFiles();
    }

    public static bool IsValidId(string id) => RosterIds.IsValid(id);

    /// <summary>Copies the catalog and sample rosters shipped next to the plugin DLL, never overwriting existing files.</summary>
    private void SeedBundledFiles()
    {
        var pluginDir = Path.GetDirectoryName(typeof(RosterStore).Assembly.Location);
        if (string.IsNullOrEmpty(pluginDir)) return;
        var bundled = Path.Combine(pluginDir, "rosters");
        if (!Directory.Exists(bundled) || Path.GetFullPath(bundled) == Folder) return;

        foreach (var file in Directory.EnumerateFiles(bundled, "*.json"))
        {
            var target = Path.Combine(Folder, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            Log.Information("HistoricRoster: added bundled {File} to {Folder}", Path.GetFileName(file), Folder);
        }
    }

    private bool IsRosterFile(string path)
    {
        var name = Path.GetFileName(path);
        return !name.StartsWith('_')
               && !name.StartsWith(_catalogPrefix, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(Path.GetFullPath(path), _databasePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All rosters: single files first, then database entries whose id isn't taken by a file.</summary>
    public IReadOnlyList<Roster> List()
    {
        var rosters = new List<Roster>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order())
        {
            if (!IsRosterFile(file)) continue;
            var roster = TryRead(file);
            if (roster != null) rosters.Add(roster);
        }
        var ids = rosters.Select(r => r.Id).ToHashSet();
        rosters.AddRange(Database().Where(r => !ids.Contains(r.Id)));
        ids.UnionWith(rosters.Select(r => r.Id));
        if (Directory.Exists(CapturedFolder))
        {
            // Newest capture first.
            foreach (var file in Directory.EnumerateFiles(CapturedFolder, "*.json").OrderDescending())
                if (TryRead(file) is { } captured && ids.Add(captured.Id)) rosters.Add(captured);
        }
        return rosters;
    }

    public static bool IsCaptured(Roster r) => string.Equals(r.Category, CapturedCategory, StringComparison.OrdinalIgnoreCase);

    public int DatabaseCount => Database().Count;

    public Roster? Get(string id)
    {
        id = id.Trim().ToLowerInvariant();
        if (!IsValidId(id)) return null;
        var path = Path.Combine(Folder, id + ".json");
        if (File.Exists(path) && IsRosterFile(path)) return TryRead(path);
        var fromDb = Database().FirstOrDefault(r => r.Id == id);
        if (fromDb != null) return fromDb;
        var captured = Path.Combine(CapturedFolder, id + ".json");
        return File.Exists(captured) ? TryRead(captured) : null;
    }

    /// <summary>Rosters whose name, id, track, series or year contain every word of the query.</summary>
    public IReadOnlyList<Roster> Find(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return List().Where(r =>
        {
            var text = $"{r.Id} {r.Name} {r.Category} {r.Series} {r.Year} {r.Track?.Name}";
            return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    /// <summary>The bulk database, cached until the file changes.</summary>
    private IReadOnlyList<Roster> Database()
    {
        if (!File.Exists(_databasePath)) return Array.Empty<Roster>();
        var stamp = File.GetLastWriteTimeUtc(_databasePath);
        lock (_lock)
        {
            if (_dbCache is { } c && c.Stamp == stamp) return c.Rosters;
            try
            {
                var db = RosterDatabase.Load(_databasePath);
                var rosters = db.Rosters.Where(r => IsValidId(r.Id)).ToList();
                _dbCache = (stamp, rosters);
                Log.Information("HistoricRoster: loaded {Count} rosters from {Path}", rosters.Count, _databasePath);
                return rosters;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "HistoricRoster: could not read roster database {Path}", _databasePath);
                _dbCache = (stamp, Array.Empty<Roster>());
                return Array.Empty<Roster>();
            }
        }
    }

    /// <summary>
    /// Saves a roster as its own file, picking a free id (suffix -2, -3, ...) if the id is taken.
    /// Captured races go to the captured/ subfolder.
    /// </summary>
    public Roster SaveNew(Roster roster, bool captured = false)
    {
        lock (_lock)
        {
            var folder = captured ? CapturedFolder : Folder;
            Directory.CreateDirectory(folder);
            var baseId = IsValidId(roster.Id) ? roster.Id : RosterIds.Slugify(roster.Id.Length > 0 ? roster.Id : roster.Name);
            var id = baseId;
            bool Taken(string x) => File.Exists(Path.Combine(Folder, x + ".json")) || File.Exists(Path.Combine(CapturedFolder, x + ".json"));
            for (var i = 2; Taken(id); i++)
                id = $"{baseId}-{i}";
            roster.Id = id;
            WriteAtomic(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(roster, RosterJson.Options));
            return roster;
        }
    }

    /// <summary>All seat bookings (every roster).</summary>
    public List<SeatBooking> GetSeats()
    {
        var path = Path.Combine(Folder, SeatsFileName);
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<SeatBooking>>(File.ReadAllText(path), RosterJson.Options) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HistoricRoster: could not read {Path}", path);
            return new();
        }
    }

    /// <summary>Changes the bookings under the store lock and saves them.</summary>
    public T UpdateSeats<T>(Func<List<SeatBooking>, T> change)
    {
        lock (_lock)
        {
            var seats = GetSeats();
            var result = change(seats);
            WriteAtomic(Path.Combine(Folder, SeatsFileName), JsonSerializer.Serialize(seats, RosterJson.Options));
            return result;
        }
    }

    public ActiveRoster? GetActive()
    {
        var path = Path.Combine(Folder, ActiveFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ActiveRoster>(File.ReadAllText(path), RosterJson.Options);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HistoricRoster: could not read {Path}", path);
            return null;
        }
    }

    public void SetActive(ActiveRoster active)
    {
        lock (_lock)
        {
            WriteAtomic(Path.Combine(Folder, ActiveFileName), JsonSerializer.Serialize(active, RosterJson.Options));
        }
    }

    private static Roster? TryRead(string path)
    {
        try
        {
            var roster = JsonSerializer.Deserialize<Roster>(File.ReadAllText(path), RosterJson.Options);
            if (roster == null) return null;
            // The file name is the source of truth for the id.
            roster.Id = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(roster.Name)) roster.Name = roster.Id;
            return roster;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HistoricRoster: skipping invalid roster file {Path}", path);
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
