using System.Text;
using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>
/// historic_rosters_db.json: many rosters in one file, built by "RosterTool build-db".
/// The plugin reads it next to the single-roster files; a roster file with the same id wins.
/// </summary>
public class RosterDatabase
{
    public const string DefaultFileName = "historic_rosters_db.json";

    public DateTime? GeneratedUtc { get; set; }
    public string? Notes { get; set; }
    public List<Roster> Rosters { get; set; } = new();

    public static RosterDatabase Load(string path)
    {
        if (!File.Exists(path)) return new RosterDatabase();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RosterDatabase>(stream, RosterJson.Options) ?? new RosterDatabase();
    }

    public void Save(string path)
    {
        GeneratedUtc = DateTime.UtcNow;
        Rosters = Rosters
            .OrderBy(r => r.Category ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Date ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, RosterJson.Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Adds or replaces a roster by id.</summary>
    public void Upsert(Roster roster)
    {
        var i = Rosters.FindIndex(r => r.Id == roster.Id);
        if (i >= 0) Rosters[i] = roster;
        else Rosters.Add(roster);
    }

    public bool Contains(string id) => Rosters.Any(r => r.Id == id);
}
