using System.Globalization;
using System.Text.Json;

namespace HistoricRosterPlugin;

public record EventSummary(string Key, string Name, string Date, string Track);

/// <summary>Shared HTTP client for the importers.</summary>
internal static class ImportHttp
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AssettoServer-HistoricRosterPlugin/1.0");
        return client;
    }

    public static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await Client.GetAsync(url, ct);
            // Rate limited: wait as asked (or a few seconds) and retry.
            if ((int)response.StatusCode == 429 && attempt <= 5)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                if (wait > TimeSpan.FromMinutes(2)) wait = TimeSpan.FromMinutes(2);
                RosterLog.Info($"{new Uri(url).Host} asks to slow down: waiting {wait.TotalSeconds:0} s (try {attempt} of 5) ...");
                await Task.Delay(wait, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{url} returned HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    public static string? Str(this JsonElement e, string name)
        => e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    public static int? Int(this JsonElement e, string name)
        => int.TryParse(e.Str(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Formula One results 1950-today from the Jolpica API (the Ergast successor): https://api.jolpi.ca</summary>
public class F1Importer
{
    private const string BaseUrl = "https://api.jolpi.ca/ergast/f1";

    public async Task<IReadOnlyList<EventSummary>> ListSeasonAsync(int year, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{BaseUrl}/{year}.json?limit=100", ct);
        var races = doc.RootElement.GetProperty("MRData").GetProperty("RaceTable").GetProperty("Races");
        return races.EnumerateArray().Select(r => new EventSummary(
            r.Str("round") ?? "?",
            r.Str("raceName") ?? "",
            r.Str("date") ?? "",
            r.GetProperty("Circuit").Str("circuitName") ?? "")).ToList();
    }

    public async Task<Roster> ImportAsync(int year, int round, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{BaseUrl}/{year}/{round}/results.json?limit=100", ct);
        var races = doc.RootElement.GetProperty("MRData").GetProperty("RaceTable").GetProperty("Races");
        if (races.GetArrayLength() == 0)
            throw new InvalidOperationException($"No F1 results for {year} round {round}.");
        return FromRace(races[0], year);
    }

    /// <summary>Every race of a season, fetched page by page (100 results per request).</summary>
    public async Task<IReadOnlyList<Roster>> ImportSeasonAsync(int year, TimeSpan delay, CancellationToken ct = default)
    {
        // Results are paged by result row; one race can be split over two pages, so merge by round.
        var byRound = new SortedDictionary<int, (JsonElement Race, List<JsonElement> Results)>();
        var docs = new List<JsonDocument>();
        try
        {
            for (int offset = 0, total = 1; offset < total; offset += 100)
            {
                var doc = await ImportHttp.GetJsonAsync($"{BaseUrl}/{year}/results.json?limit=100&offset={offset}", ct);
                docs.Add(doc);
                var mr = doc.RootElement.GetProperty("MRData");
                total = int.Parse(mr.Str("total") ?? "0");
                foreach (var race in mr.GetProperty("RaceTable").GetProperty("Races").EnumerateArray())
                {
                    var round = race.Int("round") ?? 0;
                    if (!byRound.TryGetValue(round, out var entry)) byRound[round] = entry = (race, new List<JsonElement>());
                    entry.Results.AddRange(race.GetProperty("Results").EnumerateArray());
                }
                if (offset + 100 < total) await Task.Delay(delay, ct);
            }
            return byRound.Values.Select(v => FromRace(v.Race, year, v.Results)).ToList();
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    private static Roster FromRace(JsonElement race, int year, IEnumerable<JsonElement>? results = null)
    {
        var round = race.Int("round") ?? 0;
        var circuit = race.GetProperty("Circuit");
        var raceName = race.Str("raceName") ?? $"Round {round}";

        var roster = new Roster
        {
            Id = RosterIds.Slugify($"f1-{year}-{round:00}-{raceName}"),
            Name = $"{year} {raceName}",
            Category = "F1",
            Series = "FIA Formula One World Championship",
            Year = year,
            Date = race.Str("date"),
            Time = race.Str("time"),
            Track = new RosterTrack
            {
                Name = circuit.Str("circuitName") ?? "",
                Key = circuit.Str("circuitId"),
                Locality = circuit.TryGetProperty("Location", out var loc) ? loc.Str("locality") : null,
                Latitude = circuit.TryGetProperty("Location", out var l1) && double.TryParse(l1.Str("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ? lat : null,
                Longitude = circuit.TryGetProperty("Location", out var l2) && double.TryParse(l2.Str("long"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ? lon : null,
            },
            Source = $"Jolpica F1 API ({BaseUrl}/{year}/{round}/results.json)",
        };

        foreach (var r in results ?? race.GetProperty("Results").EnumerateArray())
        {
            var driver = r.GetProperty("Driver");
            var constructor = r.GetProperty("Constructor");
            var team = constructor.Str("name");
            var grid = r.Int("grid");
            roster.Entries.Add(new RosterEntry
            {
                Driver = $"{driver.Str("givenName")} {driver.Str("familyName")}".Trim(),
                Nation = driver.Str("nationality"),
                Team = team,
                Number = r.Str("number"),
                Car = $"{team} {year}",
                GridPosition = grid is > 0 ? grid : null, // 0 = pit lane start
                Position = int.TryParse(r.Str("positionText"), out var pos) ? pos : null, // R/D/W/N = not classified
                Status = r.Str("status"),
                Laps = r.Int("laps"),
            });
        }
        return roster;
    }
}

/// <summary>
/// NASCAR results from NASCAR's public results feed (cf.nascar.com). Unofficial; race results are available from 2017 on.
/// Series: 1 = Cup, 2 = Xfinity, 3 = Truck.
/// </summary>
public class NascarImporter
{
    private const string BaseUrl = "https://cf.nascar.com/cacher";
    public const int FirstYear = 2017; // the feed refuses race files for 2015-2016 (HTTP 403)

    public static string SeriesName(int series) => series switch
    {
        1 => "NASCAR Cup Series",
        2 => "NASCAR Xfinity Series",
        3 => "NASCAR Craftsman Truck Series",
        _ => $"NASCAR series {series}",
    };

    /// <summary>
    /// The feed's race_date ("2026-06-20T17:00:00") is the green-flag time in US Eastern time (Daytona 500: 14:30,
    /// Coca-Cola 600: 18:00). Returns it in UTC as "HH:mm:ssZ", or null without a time.
    /// </summary>
    public static string? EasternToUtc(string? raceDate)
    {
        if (raceDate == null || !raceDate.Contains('T')) return null;
        if (!DateTime.TryParse(raceDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (local.TimeOfDay == TimeSpan.Zero) return null; // date only
        var tz = Eastern();
        var utc = tz != null
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz)
            : local.AddHours(IsUsDst(local) ? 4 : 5); // no time zone data: US rules
        return utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
    }

    private static TimeZoneInfo? Eastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try the other id */ }
        return null;
    }

    /// <summary>US daylight saving (since 2007): second Sunday of March to first Sunday of November.</summary>
    private static bool IsUsDst(DateTime d)
    {
        static DateTime NthSunday(int y, int m, int n) { var x = new DateTime(y, m, 1); while (x.DayOfWeek != DayOfWeek.Sunday) x = x.AddDays(1); return x.AddDays(7 * (n - 1)); }
        return d >= NthSunday(d.Year, 3, 2).AddHours(2) && d < NthSunday(d.Year, 11, 1).AddHours(2);
    }

    /// <summary>Race id -> UTC start for every race of a season (to fill in start times of races already imported).</summary>
    public async Task<Dictionary<string, string>> StartTimesAsync(int year, int series, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{BaseUrl}/{year}/{series}/race_list_basic.json", ct);
        var map = new Dictionary<string, string>();
        foreach (var r in doc.RootElement.EnumerateArray())
            if (r.Str("race_id") is { } id && EasternToUtc(r.Str("race_date")) is { } t) map[id] = t;
        return map;
    }

    public async Task<IReadOnlyList<EventSummary>> ListSeasonAsync(int year, int series, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{BaseUrl}/{year}/{series}/race_list_basic.json", ct);
        return doc.RootElement.EnumerateArray()
            .Select(r => new EventSummary(
                r.Str("race_id") ?? "?",
                r.Str("race_name") ?? "",
                (r.Str("race_date") ?? "").Split('T')[0],
                r.Str("track_name") ?? ""))
            .OrderBy(e => e.Date, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Roster> ImportAsync(int year, int series, int raceId, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{BaseUrl}/{year}/{series}/{raceId}/weekend-feed.json", ct);
        // Cancelled or not yet run races have null instead of a list here and in "results".
        if (!doc.RootElement.TryGetProperty("weekend_race", out var races) || races.ValueKind != JsonValueKind.Array || races.GetArrayLength() == 0)
            throw new InvalidOperationException("skipped, no results (cancelled or not run yet)");

        var race = races[0];
        var raceName = race.Str("race_name") ?? $"Race {raceId}";
        var date = (race.Str("race_date") ?? "").Split('T')[0];
        var utcStart = EasternToUtc(race.Str("race_date"));

        var roster = new Roster
        {
            Id = RosterIds.Slugify($"nascar-{year}-s{series}-{raceName}"),
            Name = $"{year} {raceName}",
            Category = "NASCAR",
            Series = SeriesName(series),
            Year = year,
            Date = date,
            Time = utcStart,
            Track = new RosterTrack { Name = race.Str("track_name") ?? "", Key = "nascar-track-" + race.Str("track_id") },
            Source = $"NASCAR results feed ({BaseUrl}/{year}/{series}/{raceId}/weekend-feed.json)",
        };

        var results = race.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array
            ? res.EnumerateArray().ToList() : new List<JsonElement>();
        foreach (var r in results)
        {
            var make = r.Str("car_make");
            var finish = r.Int("finishing_position");
            var start = r.Int("starting_position");
            roster.Entries.Add(new RosterEntry
            {
                Driver = (r.Str("driver_fullname") ?? "").Trim(),
                Team = r.Str("team_name")?.Trim(),
                Number = r.Str("official_car_number") ?? r.Str("car_number"),
                Car = $"{year} {make} {r.Str("car_model")}".Trim(),
                GridPosition = start is > 0 ? start : null,
                Position = finish is > 0 ? finish : null,
                Status = string.IsNullOrWhiteSpace(r.Str("finishing_status")) ? null : r.Str("finishing_status"),
                Laps = r.Int("laps_completed"),
            });
        }

        if (roster.Entries.Count == 0)
            throw new InvalidOperationException("skipped, no results (cancelled or not run yet)");
        return roster;
    }
}
