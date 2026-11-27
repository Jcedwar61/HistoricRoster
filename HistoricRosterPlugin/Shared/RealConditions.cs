using System.Globalization;
using System.Text.Json;

namespace HistoricRosterPlugin;

/// <summary>The weather at the real race: start time (local), air temperature, rain and cloud.</summary>
/// <param name="StartKnown">True: the exact start from the results; false: the event's usual start (or 14:00).</param>
/// <param name="UtcOffsetSeconds">The circuit's offset from UTC on race day (with daylight saving).</param>
/// <param name="TimeZone">The circuit's time zone (IANA id, e.g. "America/Los_Angeles"), when known.</param>
public record Conditions(TimeSpan LocalStart, bool StartKnown, double AirTemp, double RainMmPerHour, double CloudPercent,
    DateOnly? Date = null, double? Latitude = null, double? Longitude = null, int? UtcOffsetSeconds = null, string? TimeZone = null,
    bool UsualStart = false)
{
    public bool IsNight => LocalStart.TotalHours < 6.5 || LocalStart.TotalHours >= 19.5;

    public string Kind => RainMmPerHour >= 2.5 ? "heavy rain" : RainMmPerHour >= 0.5 ? "rain" : RainMmPerHour >= 0.1 ? "drizzle"
        : CloudPercent >= 85 ? "overcast" : CloudPercent >= 60 ? "cloudy" : CloudPercent >= 30 ? "some cloud" : "clear";
    public bool Wet => RainMmPerHour >= 0.1;

    public string Describe() => $"{Kind}, {AirTemp:0} °C, start {LocalStart:hh\\:mm} local{(StartKnown ? "" : UsualStart ? " (the event's usual start time)" : " (start time unknown, 14:00 assumed)")}{(IsNight ? ", at night" : "")}";

    /// <summary>
    /// AssettoServer / CSP WeatherFX type id (AssettoServer.Shared.Weather.WeatherFxType), which can show rain.
    /// </summary>
    public int WeatherFxType => Kind switch
    {
        "heavy rain" => 8,  // HeavyRain
        "rain" => 7,        // Rain
        "drizzle" => 4,     // Drizzle
        "overcast" => 19,   // OvercastClouds
        "cloudy" => 18,     // BrokenClouds
        "some cloud" => 17, // ScatteredClouds
        _ => 15,            // Clear
    };

    /// <summary>Kunos weather folder for servers without WeatherFX (no rain in vanilla AC).</summary>
    public string KunosWeather => Kind switch
    {
        "heavy rain" or "rain" or "overcast" => "7_heavy_clouds",
        "drizzle" or "cloudy" => "6_mid_clouds",
        "some cloud" => "5_light_clouds",
        _ => "3_clear",
    };

    /// <summary>Road temperature above air: sun heats the track, cloud less, rain and night hardly at all.</summary>
    public double RoadOffset
    {
        get
        {
            var night = LocalStart.TotalHours < 7 || LocalStart.TotalHours >= 20;
            if (Wet || night) return 1;
            return CloudPercent >= 85 ? 3 : CloudPercent >= 60 ? 5 : CloudPercent >= 30 ? 8 : 11;
        }
    }
}

/// <summary>
/// Looks up the real race-day weather from Open-Meteo's historical archive (free, no key; 1940 onward). The circuit is
/// found by its coordinates when the roster has them, else by its town through Open-Meteo's geocoding. Results are
/// cached in <see cref="CachePath"/> (the desktop program uses %APPDATA%\HistoricRoster\conditions.json, the server
/// plugin its roster folder); null = memory only.
/// </summary>
public static class RealConditions
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static Dictionary<string, Conditions?>? _cache;
    public static string? CachePath { get; set; }

    private static Dictionary<string, Conditions?> LoadCache()
    {
        try
        {
            if (CachePath != null && File.Exists(CachePath))
                return JsonSerializer.Deserialize<Dictionary<string, Conditions?>>(File.ReadAllText(CachePath), RosterJson.Options) ?? new();
        }
        catch (Exception ex) { RosterLog.Info($"conditions cache {CachePath} unreadable: {ex.Message}"); }
        return new();
    }

    private static void SaveCache()
    {
        if (CachePath == null || _cache == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CachePath))!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache, RosterJson.Options));
        }
        catch (Exception ex) { RosterLog.Info($"could not save conditions cache {CachePath}: {ex.Message}"); }
    }

    /// <param name="position">The circuit's position from the installed AC track (ui_track.json geotags), which wins.</param>
    public static async Task<Conditions?> GetAsync(Roster r, CancellationToken ct = default, (double Lat, double Lon)? position = null)
    {
        if (r.Track == null || !DateOnly.TryParse(r.Date, CultureInfo.InvariantCulture, out var date)) return null;
        if (date.Year < 1940 || date > DateOnly.FromDateTime(DateTime.UtcNow)) return null;
        var pos0 = position ?? (r.Track.Latitude != null && r.Track.Longitude != null ? (r.Track.Latitude.Value, r.Track.Longitude.Value) : null);
        // v4: earlier lookups (no start times or positions; rain from the wettest hour) are not reused.
        var key = $"v4|{r.Track.Name}|{r.Date}|{r.Time}|{r.UsualLocalTime}|{(pos0 is { } p0 ? $"{p0.Item1:0.00},{p0.Item2:0.00}" : r.Track.Locality)}";

        await Lock.WaitAsync(ct);
        try
        {
            _cache ??= LoadCache();
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }
        finally { Lock.Release(); }

        Conditions? result = null;
        try
        {
            var pos = pos0 ?? await GeocodeAsync(r.Track, ct);
            if (pos != null) result = await ArchiveAsync(pos.Value.Item1, pos.Value.Item2, date, r.Time, r.UsualLocalTime, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RosterLog.Info($"real conditions for {r.Name}: {ex.Message}");
            return null; // not cached: try again next time
        }

        await Lock.WaitAsync(ct);
        try
        {
            _cache![key] = result;
            SaveCache();
        }
        finally { Lock.Release(); }
        return result;
    }

    private static async Task<(double, double)?> GeocodeAsync(RosterTrack t, CancellationToken ct)
    {
        var place = System.Text.RegularExpressions.Regex.Replace(t.Name,
            @"(?i)\b(international|motor|super)?\s*(speedway|raceway|circuit|street course|road course|course|motorsports? park|park|grand prix|autodromo|autódromo|ring|superspeedway)\b", " ").Trim(' ', '-', ',');
        foreach (var name in new[] { t.Locality, place, t.Name }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
        {
            using var doc = await ImportHttp.GetJsonAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={Uri.EscapeDataString(name!)}", ct);
            if (doc.RootElement.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
                return (res[0].GetProperty("latitude").GetDouble(), res[0].GetProperty("longitude").GetDouble());
        }
        return null;
    }

    private static async Task<Conditions?> ArchiveAsync(double lat, double lon, DateOnly date, string? utcTime, string? usualLocal, CancellationToken ct)
    {
        // Race day and the day after (races starting late run past midnight). Recent days are not in the archive yet:
        // those come from the forecast service, which keeps the past three months.
        var d1 = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var d2 = date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var recent = date > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var host = recent ? "https://api.open-meteo.com/v1/forecast?" : "https://archive-api.open-meteo.com/v1/archive?";
        var url = host +
                  $"latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  $"&start_date={d1}&end_date={d2}&hourly=temperature_2m,precipitation,cloud_cover&timezone=auto";
        using var doc = await ImportHttp.GetJsonAsync(url, ct);
        var root = doc.RootElement;
        var offset = root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0;
        var apiOffset = offset; // the hourly series' times are local at this fixed offset
        var tz = root.TryGetProperty("timezone", out var z) ? z.GetString() : null;
        var hourly = root.GetProperty("hourly");
        double[] Series(string name) => hourly.TryGetProperty(name, out var arr)
            ? arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray()
            : Array.Empty<double>();
        var temp = Series("temperature_2m");
        var rain = Series("precipitation");
        var cloud = Series("cloud_cover");
        if (temp.Length < 24) return null;

        // Local start: the exact UTC start shifted to the circuit's time zone; else the event's usual local start;
        // else 14:00.
        var known = TimeSpan.TryParse((utcTime ?? "").TrimEnd('Z'), CultureInfo.InvariantCulture, out var utc);
        var usual = !known && TimeSpan.TryParse(usualLocal ?? "", CultureInfo.InvariantCulture, out var u) ? u : (TimeSpan?)null;

        // The service reports today's offset (standard time in winter); the race day's comes from the time zone
        // itself, daylight saving included.
        TimeZoneInfo? zone = null;
        if (tz != null) try { zone = TimeZoneInfo.FindSystemTimeZoneById(tz); } catch { /* unknown id: keep the service's */ }
        int OffsetAt(DateTime utcMoment) => zone != null ? (int)zone.GetUtcOffset(DateTime.SpecifyKind(utcMoment, DateTimeKind.Utc)).TotalSeconds : offset;

        TimeSpan start;
        DateTime startUtc;
        if (known)
        {
            // The roster's date is the local race day: find the UTC moment whose local day it is (a 22:00 Las Vegas
            // start is 06:00 UTC the next day).
            var pick = date.ToDateTime(TimeOnly.MinValue) + utc;
            foreach (var k in new[] { 0, 1, -1 })
            {
                var moment = date.AddDays(k).ToDateTime(TimeOnly.MinValue) + utc;
                if (DateOnly.FromDateTime(moment.AddSeconds(OffsetAt(moment))) == date) { pick = moment; break; }
            }
            offset = OffsetAt(pick);
            start = pick.AddSeconds(offset).TimeOfDay;
            startUtc = pick;
        }
        else
        {
            start = usual ?? new TimeSpan(14, 0, 0);
            offset = OffsetAt(date.ToDateTime(TimeOnly.FromTimeSpan(start)).AddSeconds(-offset));
            startUtc = date.ToDateTime(TimeOnly.FromTimeSpan(start)).AddSeconds(-offset);
        }
        start = TimeSpan.FromMinutes(((int)start.TotalMinutes % 1440 + 1440) % 1440);
        // Hour index into the series (race day and the next, at the service's offset).
        var h = Math.Clamp((int)Math.Floor((startUtc.AddSeconds(apiOffset) - date.ToDateTime(TimeOnly.MinValue)).TotalHours), 0, temp.Length - 1);

        double Avg(double[] a, int from, int n) => a.Length == 0 ? 0 : a.Skip(from).Take(n).Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Average();
        // The middle of the window's hours: one passing shower in the archive's grid cell (it's ~30 km across) doesn't
        // make a wet race; rain in two hours of three does.
        double Median(double[] a, int from, int n)
        {
            var v = a.Skip(from).Take(n).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            return v.Length == 0 ? 0 : v[v.Length / 2];
        }
        // The race window: the start hour and the two after it.
        return new Conditions(start, known, Math.Round(double.IsNaN(temp[h]) ? Avg(temp, h, 3) : temp[h], 1), Median(rain, h, 3), Avg(cloud, h, 3),
            date, lat, lon, offset, tz, usual != null);
    }
    /// <summary>
    /// The installed weather that fits best (Kunos, Sol or Pure folder names), and a note when rain is needed but
    /// no rain weather is installed.
    /// </summary>
    public static (string Weather, string? Note) PickWeather(Conditions c, IReadOnlyList<string> installed)
    {
        string? Find(params string[] words) => installed.FirstOrDefault(w => words.Any(k => w.Contains(k, StringComparison.OrdinalIgnoreCase)));
        string? DryPick() => c.CloudPercent >= 85 ? Find("overcast", "heavy_clouds", "7_heavy") ?? Find("mid_clouds")
            : c.CloudPercent >= 60 ? Find("mid_clouds", "6_mid") ?? Find("clouds")
            : c.CloudPercent >= 30 ? Find("light_clouds", "few_clouds", "5_light", "mid_clear", "4_mid")
            : Find("3_clear", "_clear", "clear");

        string? wet = c.Kind switch
        {
            "heavy rain" => Find("heavy_rain", "storm", "thunder") ?? Find("rain"),
            "rain" => installed.FirstOrDefault(w => w.Contains("rain", StringComparison.OrdinalIgnoreCase)
                                                   && !w.Contains("heavy", StringComparison.OrdinalIgnoreCase) && !w.Contains("light", StringComparison.OrdinalIgnoreCase))
                      ?? Find("rain"),
            "drizzle" => Find("drizzle", "light_rain") ?? Find("rain"),
            _ => null,
        };
        if (c.Wet && wet == null)
            return (DryPick() ?? Find("clouds") ?? installed.FirstOrDefault() ?? "3_clear",
                "It rained at the real race, but no rain weather is installed (Custom Shaders Patch with Sol or Pure adds it).");
        return (wet ?? DryPick() ?? installed.FirstOrDefault() ?? "3_clear", null);
    }
}
