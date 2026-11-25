namespace HistoricRosterPlugin;

/// <summary>
/// A real season you drive through: you take one driver's seat, join at any round, and the standings start from the
/// real results of the rounds before that. Every round you race replaces the real result with yours (the AI drivers
/// keep their names, so your race gives a whole classification).
/// </summary>
public class SeasonState
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The real driver whose seat you take all season.</summary>
    public string Driver { get; set; } = "";
    public string? Team { get; set; }
    /// <summary>First round you race; earlier rounds count with their real results.</summary>
    public string JoinedRosterId { get; set; } = "";
    /// <summary>Your races: roster id -> finishing order (driver names).</summary>
    public Dictionary<string, List<string>> Results { get; set; } = new();

    private static string FileOf(string key) => AppData.PathOf("seasons", RosterIds.Slugify(key) + ".json");
    public static SeasonState? Load(string key) => AppData.Load<SeasonState>(FileOf(key));
    public void Save() => AppData.Save(FileOf(Key), this);
    public void Delete() { if (File.Exists(FileOf(Key))) File.Delete(FileOf(Key)); }

    public static string ActiveFile => AppData.PathOf("seasons", "_active.txt");
    public static string? ActiveKey
    {
        get => File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() is { Length: > 0 } k ? k : null : null;
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveFile)!);
            if (value == null) { if (File.Exists(ActiveFile)) File.Delete(ActiveFile); }
            else File.WriteAllText(ActiveFile, value);
        }
    }
}

public record Standing(int Position, string Driver, string? Team, double Points, int Wins, bool IsPlayer);

public enum RoundState { Real, Raced, Next, Upcoming }

public record SeasonRound(int Number, Roster Roster, RoundState State, string? Winner, int? YourPosition);

public static class Seasons
{
    /// <summary>Season key of a race: series and year.</summary>
    public static string KeyOf(Roster r) => $"{r.Category}|{r.Series}|{r.Year}";

    public static string NameOf(Roster r) => $"{r.Year} {r.Series ?? r.Category}";

    /// <summary>The races of a season in date order (one per date and circuit).</summary>
    public static List<Roster> Rounds(IEnumerable<Roster> all, string key)
        => all.Where(r => KeyOf(r) == key && r.Entries.Count > 0)
            .GroupBy(r => $"{r.Date}|{r.Track?.Name}")
            .Select(g => g.OrderBy(r => r.Id.Length).First())
            .OrderBy(r => r.Date ?? "").ThenBy(r => r.Id)
            .ToList();

    /// <summary>Seasons with at least two races, newest first.</summary>
    public static List<(string Key, string Name, int Rounds)> List(IEnumerable<Roster> all)
        => all.Where(r => r.Year != null && r.Entries.Count > 0)
            .GroupBy(KeyOf)
            .Select(g => (g.Key, NameOf(g.First()), g.Select(r => $"{r.Date}|{r.Track?.Name}").Distinct().Count()))
            .Where(s => s.Item3 >= 2)
            .OrderByDescending(s => s.Item2)
            .ToList();

    /// <summary>Points for 1st, 2nd, ... in the season's own era (fastest-lap and stage points not counted).</summary>
    public static double[] PointsFor(string? category, int? year)
    {
        if (string.Equals(category, "F1", StringComparison.OrdinalIgnoreCase))
            return year switch
            {
                >= 2010 => new double[] { 25, 18, 15, 12, 10, 8, 6, 4, 2, 1 },
                >= 2003 => new double[] { 10, 8, 6, 5, 4, 3, 2, 1 },
                >= 1991 => new double[] { 10, 6, 4, 3, 2, 1 },
                _ => new double[] { 9, 6, 4, 3, 2, 1 },
            };
        if (string.Equals(category, "NASCAR", StringComparison.OrdinalIgnoreCase))
            return new double[] { 40 }.Concat(Enumerable.Range(0, 39).Select(i => 35.0 - i)).Where(p => p > 0).ToArray();
        return new double[] { 25, 18, 15, 12, 10, 8, 6, 4, 2, 1 };
    }

    public static string PointsLabel(string? category, int? year)
        => string.Join("-", PointsFor(category, year).Take(10).Select(p => p.ToString("0"))) + (PointsFor(category, year).Length > 10 ? "-..." : "");

    /// <summary>Real finishing order of a race (classified drivers).</summary>
    public static List<string> RealOrder(Roster r)
        => r.Entries.Where(e => e.Position is > 0).OrderBy(e => e.Position).Select(e => e.Driver).ToList();

    public static List<SeasonRound> RoundStates(List<Roster> rounds, SeasonState s)
    {
        var join = Math.Max(0, rounds.FindIndex(r => r.Id == s.JoinedRosterId));
        var list = new List<SeasonRound>();
        var nextGiven = false;
        for (var i = 0; i < rounds.Count; i++)
        {
            var r = rounds[i];
            RoundState state;
            List<string>? order = null;
            if (s.Results.TryGetValue(r.Id, out var mine)) { state = RoundState.Raced; order = mine; }
            else if (i < join) { state = RoundState.Real; order = RealOrder(r); }
            else if (!nextGiven) { state = RoundState.Next; nextGiven = true; }
            else state = RoundState.Upcoming;
            var pos = order?.IndexOf(s.Driver) is >= 0 and var p ? p + 1 : (int?)null;
            list.Add(new SeasonRound(i + 1, r, state, order?.FirstOrDefault(), pos));
        }
        return list;
    }

    public static List<Standing> Standings(List<Roster> rounds, SeasonState s)
    {
        if (rounds.Count == 0) return new();
        var pts = PointsFor(rounds[0].Category, rounds[0].Year);
        var points = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var wins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var teams = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var round in RoundStates(rounds, s))
        {
            foreach (var e in round.Roster.Entries) teams.TryAdd(e.Driver, e.Team);
            var order = round.State switch
            {
                RoundState.Raced => s.Results[round.Roster.Id],
                RoundState.Real => RealOrder(round.Roster),
                _ => null,
            };
            if (order == null) continue;
            for (var i = 0; i < order.Count; i++)
            {
                points[order[i]] = points.GetValueOrDefault(order[i]) + (i < pts.Length ? pts[i] : 0);
                if (i == 0) wins[order[i]] = wins.GetValueOrDefault(order[i]) + 1;
            }
        }
        return points.OrderByDescending(kv => kv.Value).ThenByDescending(kv => wins.GetValueOrDefault(kv.Key))
            .Select((kv, i) => new Standing(i + 1, kv.Key, teams.GetValueOrDefault(kv.Key), kv.Value, wins.GetValueOrDefault(kv.Key),
                kv.Key.Equals(s.Driver, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
