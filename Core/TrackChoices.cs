using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>
/// Tracks you picked yourself for a real circuit, kept in content_catalog.0-my-choices.json in the rosters folder.
/// Extra catalogs are read in name order before the main one and the first matching rule wins, so a choice made
/// here applies to every race at that circuit and beats every other rule.
/// </summary>
public static class TrackChoices
{
    public const string FileName = "content_catalog.0-my-choices.json";

    public static string PathIn(Workspace ws) => Path.Combine(ws.RostersFolder, FileName);

    /// <summary>What a rule matches on for this circuit: its source id, or its name when the id is a number-style id.</summary>
    public static string MatchKey(RosterTrack t)
        => !string.IsNullOrWhiteSpace(t.Key) && !t.Key.Any(char.IsDigit) ? t.Key! : t.Name;

    public static void Set(Workspace ws, RosterTrack track, string folder, string? layout)
    {
        var catalog = Load(ws);
        var key = MatchKey(track);
        catalog.Tracks.RemoveAll(r => r.Match.Contains(key, StringComparer.OrdinalIgnoreCase));
        catalog.Tracks.Insert(0, new TrackRule
        {
            Match = new List<string> { key },
            Folder = folder,
            Layout = string.IsNullOrWhiteSpace(layout) ? null : layout,
            Kunos = folder.StartsWith("ks_", StringComparison.OrdinalIgnoreCase),
            Note = $"Chosen in Historic Roster for {track.Name}",
        });
        AppData.Save(PathIn(ws), catalog);
    }

    public static bool Clear(Workspace ws, RosterTrack track)
    {
        var catalog = Load(ws);
        var removed = catalog.Tracks.RemoveAll(r => r.Match.Contains(MatchKey(track), StringComparer.OrdinalIgnoreCase)) > 0;
        if (removed) AppData.Save(PathIn(ws), catalog);
        return removed;
    }

    public static bool Has(Workspace ws, RosterTrack track)
        => Load(ws).Tracks.Any(r => r.Match.Contains(MatchKey(track), StringComparer.OrdinalIgnoreCase));

    private static ContentCatalog Load(Workspace ws)
    {
        try
        {
            var path = PathIn(ws);
            return File.Exists(path) ? JsonSerializer.Deserialize<ContentCatalog>(File.ReadAllText(path), RosterJson.Options) ?? new() : new();
        }
        catch { return new(); }
    }
}
