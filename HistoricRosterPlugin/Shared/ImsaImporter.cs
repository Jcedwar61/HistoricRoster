using System.Globalization;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>An IMSA championship in IMSA's timing archive.</summary>
/// <param name="FolderPattern">Matches the championship's folder inside an event folder.</param>
public record ImsaSeries(string Key, string Name, string FolderPattern, Func<int, string> Category);

/// <summary>A race session found in IMSA's timing archive.</summary>
public record ImsaRaceRef(ImsaSeries Series, int Year, string Event, string Session, DateTime LocalStart, string Url, string SeriesUrl)
{
    /// <summary>The same championship's folder at the "Roar Before the 24", where Daytona's qualifying is held in some years.</summary>
    public string? RoarSeriesUrl { get; init; }
}

/// <summary>
/// IMSA results from IMSA's official timing archive (Al Kamel Systems, https://imsa.results.alkamelcloud.com):
/// every WeatherTech SportsCar Championship and Michelin Pilot Challenge race from 2016 on, all classes, the official
/// classification and the qualifying order (the starting grid itself is only published as a PDF).
/// </summary>
public class ImsaImporter
{
    public const string BaseUrl = "https://imsa.results.alkamelcloud.com/Results/";
    public const int FirstYear = 2016;

    public static readonly ImsaSeries WeatherTech = new("imsa", "IMSA WeatherTech SportsCar Championship",
        @"weathertech|sportscar championship", y => y >= 2023 ? "Hypercar" : "LMP1");
    public static readonly ImsaSeries PilotChallenge = new("imsa-mpc", "IMSA Michelin Pilot Challenge",
        @"pilot challenge|continental tire", _ => "GT4");
    public static readonly IReadOnlyList<ImsaSeries> AllSeries = new[] { WeatherTech, PilotChallenge };

    /// <summary>Real circuit name, catalog key and position for the archive's event folder names (some are race names).</summary>
    private static readonly (string Pattern, string Name, string Key, double Lat, double Lon)[] Circuits =
    {
        ("daytona", "Daytona International Speedway road course", "daytona road course", 29.185, -81.071),
        ("sebring", "Sebring International Raceway", "sebring", 27.454, -81.348),
        ("long beach", "Long Beach Street Circuit", "long beach", 33.765, -118.189),
        ("laguna", "WeatherTech Raceway Laguna Seca", "laguna seca", 36.584, -121.753),
        ("mid-?ohio", "Mid-Ohio Sports Car Course", "mid-ohio", 40.689, -82.636),
        ("belle isle", "Belle Isle Street Circuit", "belle isle", 42.340, -82.997),
        ("detroit", "Detroit Street Circuit", "detroit", 42.331, -83.040),
        ("watkins|\\bwgi\\b", "Watkins Glen International", "watkins glen", 42.337, -76.927),
        ("canadian tire|ctmp|mosport", "Canadian Tire Motorsport Park", "mosport", 44.053, -78.676),
        ("lime rock", "Lime Rock Park", "lime rock", 41.928, -73.383),
        ("road america", "Road America", "road america", 43.798, -87.990),
        ("virginia|\\bvir\\b", "Virginia International Raceway", "virginia international raceway", 36.568, -79.206),
        ("road atlanta|petit le mans", "Michelin Raceway Road Atlanta", "road atlanta", 34.146, -83.815),
        ("americas|cota", "Circuit of the Americas", "americas", 30.133, -97.641),
        ("indianapolis|on the bricks", "Indianapolis Motor Speedway road course", "indianapolis road course", 39.795, -86.235),
        ("charlotte", "Charlotte Motor Speedway Roval", "charlotte roval", 35.352, -80.683),
        ("barber", "Barber Motorsports Park", "barber", 33.532, -86.619),
        ("st\\.?- ?petersburg", "St. Petersburg Street Circuit", "st. petersburg", 27.766, -82.630),
        ("toronto", "Toronto Street Circuit", "toronto", 43.634, -79.415),
        ("mont-?tremblant", "Circuit Mont-Tremblant", "mont-tremblant", 46.191, -74.611),
        ("gilles villeneuve|montreal", "Circuit Gilles Villeneuve", "villeneuve", 45.500, -73.523),
        ("trois-?rivi", "Circuit Trois-Rivières", "trois-rivieres", 46.364, -72.570),
        ("miami", "Miami International Autodrome", "miami", 25.958, -80.239),
    };

    // ---------------------------------------------------------------- the archive's folders

    private static readonly Regex DirLink = new("href=\"([^\"?/][^\"]*)/\"", RegexOptions.IgnoreCase);
    private static readonly Regex CsvLink = new("href=\"([^\"?/][^\"]*\\.csv)\"", RegexOptions.IgnoreCase);

    private static async Task<List<(string Name, string Url)>> DirsAsync(string url, CancellationToken ct)
        => DirLink.Matches(await ImportHttp.GetTextAsync(url, ct) ?? "").Select(m => (Uri.UnescapeDataString(m.Groups[1].Value), url + m.Groups[1].Value + "/")).ToList();

    private static async Task<List<(string Name, string Url)>> CsvsAsync(string url, CancellationToken ct)
        => CsvLink.Matches(await ImportHttp.GetTextAsync(url, ct) ?? "").Select(m => (Uri.UnescapeDataString(m.Groups[1].Value), url + m.Groups[1].Value)).ToList();

    /// <summary>The season's race sessions of one championship, in date order.</summary>
    public async Task<List<ImsaRaceRef>> ListSeasonAsync(int year, ImsaSeries series, CancellationToken ct = default)
    {
        var season = (await DirsAsync(BaseUrl, ct)).FirstOrDefault(d => d.Name.EndsWith("_" + year));
        if (season.Url == null) return new();
        var races = new List<ImsaRaceRef>();
        string? roar = null;
        foreach (var ev in await DirsAsync(season.Url, ct))
        {
            ct.ThrowIfCancellationRequested();
            var isRoar = Regex.IsMatch(ev.Name, "roar", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(Regex.Replace(ev.Name, @"^\d+_", ""), @"\btest\b|pccna|^lst\b|^gt3 -", RegexOptions.IgnoreCase)) continue;
            var champ = (await DirsAsync(ev.Url, ct)).FirstOrDefault(d => Regex.IsMatch(d.Name, series.FolderPattern, RegexOptions.IgnoreCase));
            if (champ.Url == null) continue;
            if (isRoar) { roar = champ.Url; continue; }
            foreach (var s in await DirsAsync(champ.Url, ct))
            {
                var m = Regex.Match(s.Name, @"^(\d{12})_(Race.*)$", RegexOptions.IgnoreCase);
                if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) continue;
                races.Add(new ImsaRaceRef(series, year, Regex.Replace(ev.Name, @"^\d+_", ""), m.Groups[2].Value, start, s.Url, champ.Url)
                    { RoarSeriesUrl = Regex.IsMatch(ev.Name, "daytona", RegexOptions.IgnoreCase) ? roar : null });
            }
            await Task.Delay(100, ct);
        }
        return races.OrderBy(r => r.LocalStart).ToList();
    }

    // ---------------------------------------------------------------- one race

    /// <summary>The race with its classification and qualifying order; null when there are no results yet.</summary>
    public async Task<Roster?> ImportAsync(ImsaRaceRef race, CancellationToken ct = default)
    {
        var csv = await ResultsCsvAsync(race.Url, ct);
        if (csv == null) return null;
        var rows = ReadCsv(csv);
        if (rows.Count == 0) return null;

        var entries = rows.Select(ToEntry).Where(e => e != null).Cast<RosterEntry>().ToList();
        if (entries.Count == 0) return null;
        await AddGridAsync(race, entries, ct);

        var (circuit, key, lat, lon) = Circuit(race.Event);
        var winnerTime = Hours(rows[0].GetValueOrDefault("TOTAL_TIME"));
        var y = race.Year;
        var wt = race.Series == WeatherTech;
        // The classic races keep the ids (and names) of their Wikipedia copies, which these official results replace.
        (string Id, string Name)? classic = !wt ? null
            : key == "daytona road course" && winnerTime >= 20 ? ($"daytona-24-{y}", $"{y} 24 Hours of Daytona")
            : key == "sebring" && winnerTime >= 10 ? ($"sebring-{y}", $"{y} 12 Hours of Sebring")
            : key == "road atlanta" && winnerTime >= 9 ? ($"petit-le-mans-{y}", $"{y} Petit Le Mans")
            : key == "watkins glen" && winnerTime >= 5.5 ? (RosterIds.Slugify($"imsa-{y}-six-hours-of-the-glen-{race.LocalStart:MMdd}"), $"{y} 6 Hours of The Glen")
            : null;
        var sessionSuffix = Regex.IsMatch(race.Session, @"\d") ? " " + race.Session.Trim() : "";
        var label = wt ? "IMSA" : "Michelin Pilot Challenge";

        return new Roster
        {
            Id = classic?.Id ?? RosterIds.Slugify($"{race.Series.Key}-{y}-{key}-{race.LocalStart:MMdd}{sessionSuffix}"),
            Name = classic?.Name ?? $"{y} {label} {circuit.Replace(" road course", "")}{sessionSuffix}",
            Category = race.Series.Category(y),
            Series = race.Series.Name,
            Year = y,
            Date = race.LocalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // The archive's session folder gives the real local start time.
            UsualLocalTime = race.LocalStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            Track = new RosterTrack { Name = circuit, Key = key, Latitude = lat, Longitude = lon },
            Source = race.Url,
            Notes = entries.Any(e => e.GridPosition != null) ? "Grid: qualifying order within each class (IMSA grids line up by class)." : null,
            Entries = entries,
        };
    }

    public static (string Name, string Key, double? Lat, double? Lon) Circuit(string eventName)
    {
        foreach (var c in Circuits)
            if (Regex.IsMatch(eventName, c.Pattern, RegexOptions.IgnoreCase)) return (c.Name, c.Key, c.Lat, c.Lon);
        var name = Regex.Replace(eventName, @"\s*\(.*?\)", "").Trim();
        return (name, name.ToLowerInvariant(), null, null);
    }

    /// <summary>The official results file (else provisional / unofficial); for long races it is in the last "Hour" folder.</summary>
    private static async Task<string?> ResultsCsvAsync(string raceUrl, CancellationToken ct)
    {
        var files = (await CsvsAsync(raceUrl, ct)).Where(f => Regex.IsMatch(f.Name, ResultsFile, RegexOptions.IgnoreCase)).ToList();
        if (files.Count == 0)
        {
            var last = (await DirsAsync(raceUrl, ct)).Where(d => Regex.IsMatch(d.Name, @"^\d+_Hour", RegexOptions.IgnoreCase))
                .OrderBy(d => int.Parse(Regex.Match(d.Name, @"Hour\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } h ? h.Groups[1].Value : "0"))
                .LastOrDefault();
            if (last.Url != null)
                files = (await CsvsAsync(last.Url, ct)).Where(f => Regex.IsMatch(f.Name, ResultsFile, RegexOptions.IgnoreCase)).ToList();
        }
        var best = files.OrderBy(f => Preference(f.Name)).FirstOrDefault();
        return best.Url == null ? null : await ImportHttp.GetTextAsync(best.Url, ct);
    }

    /// <summary>The overall classification ("03_Results_Race_Official.CSV", "05_Results.CSV", "03_Results.CSV"), not by class.</summary>
    private const string ResultsFile = @"^\d\d_Results(?!.*\bby\b)[^/]*\.csv$";

    private static int Preference(string file)
        => Regex.IsMatch(file, "official", RegexOptions.IgnoreCase) ? 0
            : Regex.IsMatch(file, "provisional", RegexOptions.IgnoreCase) ? 2
            : Regex.IsMatch(file, "unofficial", RegexOptions.IgnoreCase) ? 3 : 1;

    /// <summary>
    /// Starting positions from qualifying: IMSA grids line up class by class (fastest class first), each class in its
    /// qualifying order; cars without a qualifying time start at the back of their class.
    /// </summary>
    private static async Task AddGridAsync(ImsaRaceRef race, List<RosterEntry> entries, CancellationToken ct)
    {
        async Task<List<(string Name, string Url)>> Qualifying(string seriesUrl)
            => (await DirsAsync(seriesUrl, ct))
                .Select(d => (d, M: Regex.Match(d.Name, @"^(\d{12})_Qualif", RegexOptions.IgnoreCase)))
                .Where(x => x.M.Success && string.CompareOrdinal(x.M.Groups[1].Value, race.LocalStart.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)) < 0)
                .Select(x => x.d).ToList();
        var sessions = await Qualifying(race.SeriesUrl);
        if (sessions.Count == 0 && race.RoarSeriesUrl != null) sessions = await Qualifying(race.RoarSeriesUrl);
        var order = new Dictionary<string, (int Rank, double Time)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            var file = (await CsvsAsync(s.Url, ct)).Where(f => Regex.IsMatch(f.Name, ResultsFile, RegexOptions.IgnoreCase))
                .OrderBy(f => Preference(f.Name)).FirstOrDefault();
            if (file.Url == null || await ImportHttp.GetTextAsync(file.Url, ct) is not { } text) continue;
            var rank = 0;
            foreach (var row in ReadCsv(text))
            {
                rank++;
                var no = row.GetValueOrDefault("NUMBER")?.Trim();
                var time = LapSeconds(row.GetValueOrDefault("FL_TIME") ?? row.GetValueOrDefault("TIME"));
                if (!string.IsNullOrEmpty(no) && time != null) order.TryAdd(no, (rank, time.Value));
            }
        }
        if (order.Count == 0) return;
        double ClassPace(IGrouping<string, RosterEntry> g)
            => g.Select(e => e.Number != null && order.TryGetValue(e.Number, out var q) ? q.Time : double.MaxValue).Min();
        var grid = 0;
        foreach (var cls in entries.GroupBy(e => (e.Class ?? "").Trim().ToUpperInvariant().Replace(" ", "")).OrderBy(ClassPace))
            foreach (var e in cls.OrderBy(e => e.Number != null && order.TryGetValue(e.Number, out var q) ? q.Rank : int.MaxValue)
                                 .ThenBy(e => e.Position ?? int.MaxValue))
                e.GridPosition = ++grid;
    }

    private static RosterEntry? ToEntry(Dictionary<string, string> row)
    {
        var drivers = Enumerable.Range(1, 8)
            // 2018 on: first and second name columns; 2016-2017: one "DRIVER_n" column.
            .Select(i => $"{row.GetValueOrDefault($"DRIVER{i}_FIRSTNAME")} {row.GetValueOrDefault($"DRIVER{i}_SECONDNAME")}".Trim() is { Length: > 0 } n
                ? n : row.GetValueOrDefault($"DRIVER_{i}")?.Trim() ?? "")
            .Where(n => n.Length > 0).ToList();
        if (drivers.Count == 0) return null;
        var status = row.GetValueOrDefault("STATUS")?.Trim() ?? "";
        var classified = status.Length == 0 || status.Equals("Classified", StringComparison.OrdinalIgnoreCase);
        var gap = row.GetValueOrDefault("GAP_FIRST")?.Trim();
        return new RosterEntry
        {
            Driver = string.Join(" / ", drivers),
            Team = NullIfEmpty(row.GetValueOrDefault("TEAM")),
            Number = NullIfEmpty(row.GetValueOrDefault("NUMBER")),
            Nation = NullIfEmpty(row.GetValueOrDefault("DRIVER1_COUNTRY")),
            Class = NullIfEmpty(row.GetValueOrDefault("CLASS")),
            Car = NullIfEmpty(row.GetValueOrDefault("VEHICLE")),
            Position = classified && int.TryParse(row.GetValueOrDefault("POSITION"), out var p) ? p : null,
            Laps = int.TryParse(row.GetValueOrDefault("LAPS"), out var l) ? l : null,
            Status = classified ? (gap is { Length: > 0 } && gap != "-" ? gap : NullIfEmpty(row.GetValueOrDefault("TOTAL_TIME"))) : status,
        };
    }

    /// <summary>Rows of a ";"-separated results file, by column name.</summary>
    public static List<Dictionary<string, string>> ReadCsv(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return new();
        var header = lines[0].Split(';').Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToList();
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(';');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < cells.Length; i++)
                if (header[i].Length > 0) row.TryAdd(header[i], cells[i].Trim().Trim('"'));
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>"21:59:13.350" (2016-2017: "1:40'37.481") -> hours; "1:34.598" -> minutes. Null when not a time.</summary>
    private static double? Hours(string? time)
    {
        var parts = (time ?? "").Trim().Replace('\'', ':').Split(':');
        if (parts.Length < 2 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return null;
        var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();
        return parts.Length == 3 ? v[0] + v[1] / 60 + v[2] / 3600 : (v[0] + v[1] / 60) / 60;
    }

    private static double? LapSeconds(string? time)
    {
        var parts = (time ?? "").Trim().Replace('\'', ':').Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total > 0 ? total : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
