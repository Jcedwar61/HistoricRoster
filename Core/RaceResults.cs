using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>Where the program keeps its own files: %APPDATA%\HistoricRoster.</summary>
public static class AppData
{
    /// <summary>Shared code (compiled into this assembly) caches the real race-day weather under %APPDATA%.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => RealConditions.CachePath ??= PathOf("conditions.json");

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HistoricRoster");
    public static string PathOf(params string[] parts) => Path.Combine(new[] { Folder }.Concat(parts).ToArray());

    public static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), RosterJson.Options) : null; }
        catch { return null; }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, RosterJson.Options));
    }
}

/// <summary>The race last sent to Assetto Corsa (race.ini or Content Manager), to compare with what AC reports afterwards.</summary>
public class LastRace
{
    public string RosterId { get; set; } = "";
    public string RosterName { get; set; } = "";
    public string TrackFolder { get; set; } = "";
    /// <summary>The real driver whose seat the player took.</summary>
    public string PlayerSeat { get; set; } = "";
    /// <summary>Your driver name in that seat (null in races written before names were set).</summary>
    public string? PlayerName { get; set; }
    public DateTime WrittenUtc { get; set; }
    /// <summary>race_out.json time already shown, so a result is reported once.</summary>
    public DateTime? HandledResultUtc { get; set; }
    public string? SeasonKey { get; set; }
    /// <summary>An AI-only race you watched: no row is "you" and it never counts for a season.</summary>
    public bool Watched { get; set; }

    public static string FilePath => AppData.PathOf("last_race.json");
    public static LastRace? Load() => AppData.Load<LastRace>(FilePath);
    public void Save() => AppData.Save(FilePath, this);

    public static LastRace Record(RaceSetup setup, string? seasonKey) => new()
    {
        RosterId = setup.Roster.Id,
        RosterName = setup.Roster.Name,
        TrackFolder = setup.Track.Folder ?? "",
        PlayerSeat = RaceIniWriter.UserCar(setup).Driver, // CAR_0 (in a watched race, the best real finisher)
        PlayerName = string.IsNullOrWhiteSpace(setup.PlayerName) ? null : setup.PlayerName.Trim(),
        WrittenUtc = DateTime.UtcNow,
        SeasonKey = setup.Watch ? null : seasonKey,
        Watched = setup.Watch,
    };
}

/// <summary>One line of the comparison: a real driver's real result and their result in your race.</summary>
public record ResultRow(string Driver, string? Team, int? RealPosition, int? RealGrid, int? YourRacePosition, bool IsPlayer)
{
    /// <summary>The car's class in a multi-class race (null otherwise).</summary>
    public string? Class { get; init; }
    /// <summary>Real finishing position within the class.</summary>
    public int? RealClassPosition { get; init; }
    /// <summary>Finishing position within the class in your race.</summary>
    public int? YourClassPosition { get; init; }

    /// <summary>Places gained (+) or lost (-) against the real result: within the class in a multi-class race.</summary>
    public int? Delta => Class != null
        ? (RealClassPosition != null && YourClassPosition != null ? RealClassPosition - YourClassPosition : null)
        : RealPosition != null && YourRacePosition != null ? RealPosition - YourRacePosition : null;
}

public record RaceComparison(LastRace Race, DateTime ResultUtc, IReadOnlyList<ResultRow> Rows)
{
    public ResultRow? Player => Rows.FirstOrDefault(r => r.IsPlayer);

    public bool IsMultiClass => Rows.Any(r => r.Class != null);

    /// <summary>Class winners of your race, fastest class first: (class, winner, real class winner).</summary>
    public List<(string Class, ResultRow? Winner, ResultRow? RealWinner)> ClassWinners
        => Rows.Where(r => r.Class != null).GroupBy(r => r.Class!.ToUpperInvariant().Replace(" ", ""))
            .OrderBy(g => g.Min(r => r.YourRacePosition ?? int.MaxValue))
            .Select(g => (g.First().Class!, g.FirstOrDefault(r => r.YourClassPosition == 1), g.FirstOrDefault(r => r.RealClassPosition == 1)))
            .ToList();

    /// <summary>Finishing order of your race (driver names; the player under their seat's name).</summary>
    public List<string> Order => Rows.Where(r => r.YourRacePosition != null).OrderBy(r => r.YourRacePosition).Select(r => r.Driver).ToList();

    public string Headline
    {
        get
        {
            if (Race.Watched)
            {
                var winner = Rows.FirstOrDefault(r => r.YourRacePosition == 1);
                var realWinner = Rows.FirstOrDefault(r => r.RealPosition == 1);
                if (winner != null && IsMultiClass)
                    return $"{Race.RosterName} (AI race): " + string.Join("; ", ClassWinners.Where(c => c.Winner != null).Select(c =>
                        $"{c.Class}: {c.Winner!.Driver} won" + (c.RealWinner == null ? "" : c.RealWinner.Driver == c.Winner.Driver ? " (as in the real race)" : $" (real: {c.RealWinner.Driver})"))) + ".";
                return winner == null
                    ? $"{Race.RosterName} (AI race): no finishing order in the result."
                    : $"{Race.RosterName} (AI race): {winner.Driver} won" +
                      (realWinner == null ? "." : realWinner.Driver == winner.Driver ? ", as in the real race." : $"; the real winner was {realWinner.Driver}.");
            }
            var p = Player;
            if (p?.YourRacePosition == null) return $"{Race.RosterName}: no finishing order for you in the result.";
            if (p.Class != null)
            {
                var realClass = p.RealClassPosition is { } rc ? $"{p.Class} P{rc}, P{p.RealPosition} overall" : "not classified";
                var classVerdict = p.Delta switch
                {
                    null => "",
                    > 0 => $" Beat the real class result by {p.Delta} place{(p.Delta == 1 ? "" : "s")}.",
                    0 => " Matched the real class result.",
                    _ => $" {-p.Delta} place{(p.Delta == -1 ? "" : "s")} behind the real class result.",
                };
                return $"You finished {p.Class} P{p.YourClassPosition} (P{p.YourRacePosition} overall) in {p.Driver}'s seat (real: {realClass}).{classVerdict}";
            }
            var real = p.RealPosition is { } rp ? $"P{rp}" : "not classified";
            var verdict = p.Delta switch
            {
                null => "",
                > 0 => $" Beat the real result by {p.Delta} place{(p.Delta == 1 ? "" : "s")}.",
                0 => " Matched the real result.",
                _ => $" {-p.Delta} place{(p.Delta == -1 ? "" : "s")} behind the real result.",
            };
            return $"You finished P{p.YourRacePosition} in {p.Driver}'s seat (real: {real}).{verdict}";
        }
    }
}

/// <summary>Reads Documents\Assetto Corsa\out\race_out.json, which AC writes when a session ends.</summary>
public static class RaceOutReader
{
    public static string FilePath => Environment.GetEnvironmentVariable("HISTORICROSTER_RACE_OUT") is { Length: > 0 } test
        ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assetto Corsa", "out", "race_out.json");

    /// <summary>
    /// The comparison for the last race, when race_out.json is newer than that race and belongs to it
    /// (same track, and most AI names are its real drivers). Null otherwise.
    /// </summary>
    public static RaceComparison? Compare(LastRace race, Roster roster, bool includeHandled = false)
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp < race.WrittenUtc) return null;
        if (!includeHandled && race.HandledResultUtc is { } h && Math.Abs((stamp - h).TotalSeconds) < 1) return null;

        List<string> names;
        List<int> order;
        string track;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), RosterJson.Lenient);
            var root = doc.RootElement;
            track = root.TryGetProperty("track", out var t) ? t.GetString() ?? "" : "";
            names = root.GetProperty("players").EnumerateArray()
                .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList();
            order = RaceOrder(root, names.Count);
        }
        catch
        {
            return null;
        }
        if (order.Count == 0 || names.Count < 2) return null;
        // AC writes "folder-layout" for a track with layouts ("ks_barcelona-layout_gp").
        if (race.TrackFolder.Length > 0 && track.Length > 0 && !track.Equals(race.TrackFolder, StringComparison.OrdinalIgnoreCase)
            && !track.StartsWith(race.TrackFolder + "-", StringComparison.OrdinalIgnoreCase)) return null;

        var byName = roster.Entries.GroupBy(e => e.Driver, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var known = names.Skip(1).Count(byName.ContainsKey);
        if (known * 2 < names.Count - 1) return null;

        var yourPos = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++) yourPos[order[i]] = i + 1;

        var rows = new List<ResultRow>();
        for (var car = 0; car < names.Count; car++)
        {
            // Car 0 is the seat you picked; in a watched race the AI drove it, so it isn't "you".
            var isPlayer = car == 0 && !race.Watched;
            var driver = car == 0 ? race.PlayerSeat : names[car];
            if (!isPlayer && !byName.ContainsKey(driver)) continue; // e.g. the Safety Car
            byName.TryGetValue(driver, out var e);
            rows.Add(new ResultRow(driver, e?.Team, e?.Position, e?.GridPosition, yourPos.TryGetValue(car, out var p) ? p : null, isPlayer));
        }
        rows = WithClasses(rows, byName);
        rows = rows.OrderBy(r => r.YourRacePosition ?? int.MaxValue).ThenBy(r => r.RealPosition ?? int.MaxValue).ToList();
        return new RaceComparison(race, stamp, rows);
    }

    /// <summary>In a multi-class race: each row's class, and its real and your-race positions within the class.</summary>
    private static List<ResultRow> WithClasses(List<ResultRow> rows, Dictionary<string, RosterEntry> byName)
    {
        var entries = rows.Select(r => byName.GetValueOrDefault(r.Driver)).ToList();
        if (!RaceClasses.IsMultiClass(entries.OfType<RosterEntry>())) return rows;
        // Rows without a roster entry (none in practice) get a stand-in entry so they still take part.
        var keyed = rows.Select((r, i) => (Row: r, Entry: entries[i] ?? new RosterEntry { Driver = r.Driver })).ToList();
        var real = RaceClasses.Positions(keyed.Select(k => k.Entry), e => e.Position);
        var yourPos = new Dictionary<RosterEntry, int?>(ReferenceEqualityComparer.Instance);
        foreach (var k in keyed) yourPos[k.Entry] = k.Row.YourRacePosition;
        var yours = RaceClasses.Positions(keyed.Select(k => k.Entry), e => yourPos[e]);
        return keyed.Select(k => k.Row with
        {
            Class = RaceClasses.Of(k.Entry),
            RealClassPosition = real.TryGetValue(k.Entry, out var rc) ? rc : null,
            YourClassPosition = yours.TryGetValue(k.Entry, out var yc) ? yc : null,
        }).ToList();
    }

    /// <summary>Car indexes in finishing order of the last race session: AC's raceResult, else laps done then total time.</summary>
    private static List<int> RaceOrder(JsonElement root, int cars)
    {
        if (!root.TryGetProperty("sessions", out var sessions)) return new();
        var race = sessions.EnumerateArray().LastOrDefault(s => s.TryGetProperty("type", out var ty) && ty.TryGetInt32(out var v) && v == 3);
        if (race.ValueKind != JsonValueKind.Object) return new();

        if (race.TryGetProperty("raceResult", out var rr) && rr.ValueKind == JsonValueKind.Array && rr.GetArrayLength() > 0)
            return rr.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).Where(i => i >= 0 && i < cars).ToList();

        if (!race.TryGetProperty("laps", out var laps)) return new();
        var done = new Dictionary<int, (int Laps, long Time)>();
        foreach (var lap in laps.EnumerateArray())
        {
            if (!lap.TryGetProperty("car", out var c) || !c.TryGetInt32(out var car)) continue;
            var time = lap.TryGetProperty("time", out var tt) && tt.TryGetInt64(out var ms) ? ms : 0;
            var cur = done.GetValueOrDefault(car);
            done[car] = (cur.Laps + 1, cur.Time + time);
        }
        return done.OrderByDescending(kv => kv.Value.Laps).ThenBy(kv => kv.Value.Time).Select(kv => kv.Key).ToList();
    }
}
