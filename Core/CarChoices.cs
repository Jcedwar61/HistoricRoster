using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>
/// Cars you picked yourself ("I have this car"), kept with your track picks in content_catalog.0-my-choices.json.
/// That file is read before every other catalog and the first matching rule wins, so the installed car you pick is
/// used for that real car in that series and season, in every race of it, whatever the automatic matching thinks.
/// </summary>
public static class CarChoices
{
    /// <summary>What the rule matches on: the real car's name, or the team when the real car has no name.</summary>
    public static (string? Car, string? Team) MatchOf(RosterEntry e)
        => !string.IsNullOrWhiteSpace(e.Car) ? (e.Car!.Trim(), null) : (null, e.Team?.Trim());

    private static bool Same(CarRule r, Roster roster, RosterEntry e)
    {
        var (car, team) = MatchOf(e);
        return string.Equals(r.Category, roster.Category, StringComparison.OrdinalIgnoreCase)
               && r.YearFrom == roster.Year && r.YearTo == roster.Year
               && string.Equals(r.Car, car, StringComparison.OrdinalIgnoreCase)
               && string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase)
               && r.Driver == null;
    }

    /// <summary>Use the installed car folder <paramref name="model"/> for this real car (and its season).</summary>
    public static void Set(Workspace ws, Roster roster, RosterEntry entry, string model)
    {
        var catalog = Load(ws);
        var (car, team) = MatchOf(entry);
        catalog.Cars.RemoveAll(r => Same(r, roster, entry));
        catalog.Cars.Insert(0, new CarRule
        {
            Category = roster.Category,
            YearFrom = roster.Year,
            YearTo = roster.Year,
            Car = car,
            Team = team,
            Model = model,
            Kunos = model.StartsWith("ks_", StringComparison.OrdinalIgnoreCase),
            Note = $"Chosen in Historic Roster for the {roster.Year} {car ?? team} ({roster.Category})",
        });
        AppData.Save(TrackChoices.PathIn(ws), catalog);
    }

    public static bool Clear(Workspace ws, Roster roster, RosterEntry entry)
    {
        var catalog = Load(ws);
        var removed = catalog.Cars.RemoveAll(r => Same(r, roster, entry)) > 0;
        if (removed) AppData.Save(TrackChoices.PathIn(ws), catalog);
        return removed;
    }

    public static string? Get(Workspace ws, Roster roster, RosterEntry entry)
        => Load(ws).Cars.FirstOrDefault(r => Same(r, roster, entry))?.Model;

    private static ContentCatalog Load(Workspace ws)
    {
        try
        {
            var path = TrackChoices.PathIn(ws);
            return File.Exists(path) ? JsonSerializer.Deserialize<ContentCatalog>(File.ReadAllText(path), RosterJson.Options) ?? new() : new();
        }
        catch { return new(); }
    }
}
