using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>A race that is imported from its English Wikipedia article.</summary>
/// <param name="TitlePattern">Article title with {year}, e.g. "{year} 24 Hours of Le Mans".</param>
/// <param name="Aliases">Words one of which a found article title must contain (titles change with sponsors);
/// the first is also the search text. Null = the title pattern without {year}.</param>
public record WikiEvent(string Key, string TitlePattern, int FromYear, int ToYear, Func<int, string> Category,
    string Series, string TrackName, string TrackKey, string[]? Aliases = null)
{
    public string[] SearchWords => Aliases ?? new[] { TitlePattern.Replace("{year}", "").Trim() };
    /// <summary>The circuit's position (for the race-day weather and the sun).</summary>
    public (double Lat, double Lon)? Position { get; init; }
    /// <summary>The event's usual local start time by year ("HH:mm"), shown as the usual time, not the exact one.</summary>
    public Func<int, string>? UsualStart { get; init; }
    /// <summary>The weekday the race starts on, to pick it out of a date span that includes practice days.</summary>
    public DayOfWeek? RaceDay { get; init; }
}

/// <summary>
/// Reads race classification tables from Wikipedia race articles (CC BY-SA; the article URL is stored as the source).
/// Used for series without a results API: endurance (LMP1 / GT1 / GT3 classes) and NASCAR before 2017.
/// </summary>
public class WikipediaImporter
{
    private const string Api = "https://en.wikipedia.org/w/api.php";

    /// <summary>Races covered by "RosterTool build-db". Missing articles for a year are skipped.</summary>
    public static readonly IReadOnlyList<WikiEvent> Events = new WikiEvent[]
    {
        new("le-mans", "{year} 24 Hours of Le Mans", 1923, 2026, y => SportsCars(y, 2021),
            "24 Hours of Le Mans", "Circuit de la Sarthe", "le mans") { Position = (47.950, 0.224), UsualStart = _ => "16:00", RaceDay = DayOfWeek.Saturday },
        new("sebring", "{year} 12 Hours of Sebring", 1952, 2026, y => SportsCars(y, 2021),
            "12 Hours of Sebring", "Sebring International Raceway", "sebring") { Position = (27.454, -81.348), UsualStart = _ => "10:30", RaceDay = DayOfWeek.Saturday },
        new("petit-le-mans", "{year} Petit Le Mans", 1998, 2026, y => SportsCars(y, 2021),
            "Petit Le Mans", "Michelin Raceway Road Atlanta", "road atlanta") { Position = (34.146, -83.815), UsualStart = _ => "12:00", RaceDay = DayOfWeek.Saturday },
        new("daytona-24", "{year} 24 Hours of Daytona", 1966, 2026, y => SportsCars(y, 2023),
            "24 Hours of Daytona", "Daytona International Speedway road course", "daytona road course") { Position = (29.185, -81.071), UsualStart = _ => "14:30", RaceDay = DayOfWeek.Saturday },
        new("spa-24", "{year} Spa 24 Hours", 1996, 2026, y => y <= 2010 ? "GT1" : "GT3",
            "Spa 24 Hours", "Circuit de Spa-Francorchamps", "spa-francorchamps", new[] { "24 Hours of Spa", "Spa 24 Hours" }) { Position = (50.437, 5.971), UsualStart = _ => "16:30", RaceDay = DayOfWeek.Saturday },
        new("bathurst-12", "{year} Bathurst 12 Hour", 2011, 2026, _ => "GT3",
            "Bathurst 12 Hour", "Mount Panorama Circuit", "bathurst", new[] { "Bathurst 12 Hour" }) { Position = (-33.448, 149.555), UsualStart = _ => "05:45", RaceDay = DayOfWeek.Sunday },
        new("nurburgring-24", "{year} 24 Hours Nürburgring", 2008, 2026, _ => "GT3",
            "24 Hours Nürburgring", "Nürburgring Nordschleife", "nordschleife",
            new[] { "24 Hours Nürburgring", "24 Hours of Nürburgring", "Nürburgring 24 Hours" }) { Position = (50.346, 6.966), UsualStart = _ => "15:30", RaceDay = DayOfWeek.Saturday },
        new("bathurst-1000", "{year} Bathurst 1000", 1996, 2026, _ => "Supercars",
            "Bathurst 1000", "Mount Panorama Circuit", "bathurst",
            new[] { "Bathurst 1000", "FAI 1000", "T-Marts 1000", "Supercheap Auto 1000", "Supercheap Auto Bathurst", "AMP Bathurst" }) { Position = (-33.448, 149.555), UsualStart = _ => "11:00", RaceDay = DayOfWeek.Sunday },
        new("daytona-500", "{year} Daytona 500", 1996, 2026, _ => "NASCAR",
            "NASCAR Cup Series", "Daytona International Speedway", "daytona international speedway") { Position = (29.185, -81.071), UsualStart = _ => "13:00" },
        new("coca-cola-600", "{year} Coca-Cola 600", 1996, 2026, _ => "NASCAR",
            "NASCAR Cup Series", "Charlotte Motor Speedway", "charlotte") { Position = (35.352, -80.683), UsualStart = _ => "18:00" },
        new("brickyard-400", "{year} Brickyard 400", 1996, 2020, _ => "NASCAR",
            "NASCAR Cup Series", "Indianapolis Motor Speedway", "indianapolis", new[] { "Brickyard 400", "at the Brickyard" }) { Position = (39.795, -86.235), UsualStart = _ => "13:30" },
        new("southern-500", "{year} Southern 500", 1996, 2026, _ => "NASCAR",
            "NASCAR Cup Series", "Darlington Raceway", "darlington", new[] { "Southern 500" })
            // An afternoon race until 2004, a Saturday night race from 2005.
            { Position = (34.296, -79.905), UsualStart = y => y <= 2004 ? "13:00" : y <= 2014 ? "19:30" : "18:00" },
        new("talladega-fall", "{year} YellaWood 500", 2015, 2026, _ => "NASCAR",
            "NASCAR Cup Series", "Talladega Superspeedway", "talladega") { Position = (33.567, -86.066), UsualStart = _ => "14:00" },
    };

    /// <summary>
    /// The category of a sports-car race by era: Hypercar (from <paramref name="hypercarFrom"/>), LMP1 (prototypes from
    /// 1994: WSC, LMP900, LMP1, DPi), Group C (1982-1993, with IMSA GTP), else Sportscar (earlier sports cars).
    /// </summary>
    public static string SportsCars(int year, int hypercarFrom)
        => year >= hypercarFrom ? "Hypercar" : year >= 1994 ? "LMP1" : year >= 1982 ? "Group C" : "Sportscar";

    public async Task<Roster?> ImportAsync(WikiEvent ev, int year, CancellationToken ct = default)
    {
        var nascarYear = ev.Category(year) == "NASCAR" ? year : (int?)null;

        // 1. The usual title. 2. Wikipedia search, for titles that carry a sponsor name that year.
        var found = await TryArticleAsync(ev.TitlePattern.Replace("{year}", year.ToString()), year, nascarYear, ev.RaceDay, ct);
        if (found == null)
        {
            foreach (var candidate in await SearchTitlesAsync(ev, year, ct))
            {
                found = await TryArticleAsync(candidate, year, nascarYear, ev.RaceDay, ct);
                if (found != null) break;
            }
        }
        if (found == null) return null;
        var (pageTitle, entries, raceDate) = found.Value;

        return new Roster
        {
            Id = RosterIds.Slugify($"{ev.Key}-{year}"),
            Name = pageTitle,
            Category = ev.Category(year),
            Series = ev.Series,
            Year = year,
            // The race day from the article's info box (needed for the race-day weather and the sun), else the year.
            Date = raceDate ?? year.ToString(),
            UsualLocalTime = ev.UsualStart?.Invoke(year),
            Track = new RosterTrack { Name = ev.TrackName, Key = ev.TrackKey, Latitude = ev.Position?.Lat, Longitude = ev.Position?.Lon },
            Source = "Wikipedia: https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_')),
            Notes = entries.Any(e => e.Position != null) ? null : "Entry list only (no classification table found)",
            Entries = entries,
        };
    }

    /// <summary>The article's title and entries, or null if it doesn't exist, is another year, or has no table.</summary>
    private async Task<(string Title, List<RosterEntry> Entries, string? Date)?> TryArticleAsync(string title, int year, int? nascarYear, DayOfWeek? raceDay, CancellationToken ct)
    {
        var url = $"{Api}?action=parse&page={Uri.EscapeDataString(title)}&prop=text&format=json&formatversion=2&redirects=1";
        using var doc = await ImportHttp.GetJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("parse", out var parse)) return null;
        var pageTitle = parse.Str("title") ?? title;
        // A redirect to a different year/race (e.g. a series article) is not this race.
        if (!pageTitle.Contains(year.ToString())) return null;
        var html = parse.GetProperty("text").GetString() ?? "";
        var entries = ParseResults(html, nascarYear);
        return entries.Count == 0 ? null : (pageTitle, entries, InfoboxDate(html, year, raceDay));
    }

    private static readonly string[] Months =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    /// <summary>
    /// The race day ("yyyy-MM-dd") from the article's info box "Date" row, else its first paragraph. Spans that
    /// include practice ("10 to 14 June 2015") give the day of the span that is <paramref name="raceDay"/>.
    /// </summary>
    public static string? InfoboxDate(string html, int year, DayOfWeek? raceDay = null)
    {
        var box = Regex.Match(html, "<table[^>]*class=\"[^\"]*infobox[^\"]*\".*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (box.Success)
        {
            var row = Regex.Match(box.Value, @"<th[^>]*>\s*(?:<[^>]+>\s*)*Date\s*(?:<[^>]+>\s*)*</th>\s*<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (row.Success && DateFromText(Regex.Replace(row.Groups[1].Value, "<[^>]+>", " "), year, raceDay) is { } d) return d;
        }
        var lead = Regex.Match(html, @"<p>(?:(?!</p>).)*?<b>.*?</p>", RegexOptions.Singleline);
        return lead.Success ? DateFromText(Regex.Replace(lead.Value, "<[^>]+>", " "), year, raceDay) : null;
    }

    /// <summary>
    /// Race days of many articles at once (50 per request, from the wikitext's info box date or first paragraph),
    /// so a whole database is filled in with a handful of requests instead of one per article. Title -> "yyyy-MM-dd".
    /// </summary>
    public static async Task<Dictionary<string, string>> InfoboxDatesAsync(IReadOnlyCollection<(string Title, int Year, DayOfWeek? RaceDay)> pages, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in pages.Chunk(50))
        {
            var titles = string.Join("|", chunk.Select(p => p.Title));
            using var doc = await ImportHttp.GetJsonAsync($"{Api}?action=query&prop=revisions&rvprop=content&rvslots=main&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(titles)}", ct);
            if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pagesEl)) continue;
            // Titles as stored (normalized / redirected) -> titles as asked.
            var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "normalized", "redirects" })
                if (q.TryGetProperty(key, out var arr))
                    foreach (var x in arr.EnumerateArray()) alias[x.Str("to") ?? ""] = alias.TryGetValue(x.Str("from") ?? "", out var orig) ? orig : x.Str("from") ?? "";
            foreach (var page in pagesEl.EnumerateArray())
            {
                var title = page.Str("title") ?? "";
                var asked = alias.TryGetValue(title, out var a) ? a : title;
                var wanted = chunk.FirstOrDefault(p => p.Title.Equals(asked, StringComparison.OrdinalIgnoreCase));
                if (wanted.Title == null || !page.TryGetProperty("revisions", out var revs)) continue;
                var wikitext = revs[0].GetProperty("slots").GetProperty("main").Str("content") ?? "";
                if (WikitextDate(wikitext, wanted.Year, wanted.RaceDay) is { } day) result[wanted.Title] = day;
            }
            await Task.Delay(500, ct);
        }
        return result;
    }

    /// <summary>The race day from wikitext: the info box's date field, else the first paragraph.</summary>
    public static string? WikitextDate(string wikitext, int year, DayOfWeek? raceDay)
    {
        static string Plain(string v) => Regex.Replace(Regex.Replace(Regex.Replace(v, @"<ref[^>]*/>|<ref.*?</ref>", " ", RegexOptions.Singleline),
            @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1"), @"\{\{[^{}]*\}\}|'''|''", " ");
        foreach (Match m in Regex.Matches(wikitext, @"^\s*\|\s*(?:full)?dates?\s*=\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var v = m.Groups[1].Value;
            var t = Regex.Match(v, @"\{\{\s*(?:start[ -]date|dts)[^|}]*\|\s*(\d{4})\s*\|\s*(\d{1,2})\s*\|\s*(\d{1,2})", RegexOptions.IgnoreCase);
            if (t.Success && int.TryParse(t.Groups[2].Value, out var mo) && int.TryParse(t.Groups[3].Value, out var d) && mo is >= 1 and <= 12)
            {
                var y = int.Parse(t.Groups[1].Value);
                if (d >= 1 && d <= DateTime.DaysInMonth(y, mo))
                    return new DateOnly(y, mo, d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (DateFromText(Plain(v), year, raceDay) is { } day) return day;
        }
        var lead = Regex.Match(wikitext, @"^[^\n]*'''[^\n]*$", RegexOptions.Multiline);
        return lead.Success ? DateFromText(Plain(lead.Value), year, raceDay) : null;
    }

    private const string MonthNames = "january|february|march|april|may|june|july|august|september|october|november|december";

    /// <summary>
    /// The first date or date span in the text: "June 13–14, 2015", "13–14 June 2015", "from 10 to 14 June 2015",
    /// "February 15, 2004". A span gives its last day that is <paramref name="raceDay"/> (the race, not practice),
    /// else its first day. "yyyy-MM-dd", or null.
    /// </summary>
    public static string? DateFromText(string text, int year, DayOfWeek? raceDay = null)
    {
        text = WebUtility.HtmlDecode(text).ToLowerInvariant().Replace(' ', ' ');
        const string sep = @"\s*(?:–|—|-|to|and|&)\s*";
        var monthFirst = Regex.Match(text, $@"\b({MonthNames})\s+(\d{{1,2}})(?:{sep}(\d{{1,2}}))?\b(?:,?\s*((?:19|20)\d\d))?");
        var dayFirst = Regex.Match(text, $@"\b(\d{{1,2}})(?:{sep}(\d{{1,2}}))?\s+({MonthNames})\b(?:,?\s*((?:19|20)\d\d))?");
        Match m;
        bool mf;
        if (monthFirst.Success && (!dayFirst.Success || monthFirst.Index <= dayFirst.Index)) { m = monthFirst; mf = true; }
        else if (dayFirst.Success) { m = dayFirst; mf = false; }
        else return null;
        var month = Array.IndexOf(Months, mf ? m.Groups[1].Value : m.Groups[3].Value) + 1;
        var first = int.Parse(mf ? m.Groups[2].Value : m.Groups[1].Value);
        var last = int.TryParse(mf ? m.Groups[3].Value : m.Groups[2].Value, out var l) ? l : first;
        var y = int.TryParse(m.Groups[4].Value, out var yy) ? yy : year;
        if (y != year || month < 1 || first < 1 || last < first || last > DateTime.DaysInMonth(y, month)) return null;
        var pick = new DateOnly(y, month, first);
        if (raceDay != null)
            for (var d = last; d >= first; d--)
                if (new DateOnly(y, month, d).DayOfWeek == raceDay) { pick = new DateOnly(y, month, d); break; }
        // A span across months ("29 July – 1 August 2021", "January 31 – February 2"): its last race day.
        if (raceDay != null && pick.DayOfWeek != raceDay)
        {
            var rest = text[(m.Index + m.Length)..];
            var end = Regex.Match(rest, $@"^\s*(?:–|—|-|to|and|&)\s*(?:(\d{{1,2}})\s+({MonthNames})|({MonthNames})\s+(\d{{1,2}}))");
            if (end.Success)
            {
                var em = Array.IndexOf(Months, end.Groups[2].Success ? end.Groups[2].Value : end.Groups[3].Value) + 1;
                var ed = int.Parse(end.Groups[1].Success ? end.Groups[1].Value : end.Groups[4].Value);
                var ey = em < month ? y + 1 : y;
                if (em >= 1 && ed >= 1 && ed <= DateTime.DaysInMonth(ey, em))
                {
                    var stop = new DateOnly(ey, em, ed);
                    for (var d = stop; d >= pick && d >= stop.AddDays(-7); d = d.AddDays(-1))
                        if (d.DayOfWeek == raceDay) { pick = d; break; }
                }
            }
        }
        // A single day that isn't the race's weekday is the finish (a 24-hour race ends on Sunday) or a practice
        // day: the nearest race weekday is the start.
        if (raceDay != null && pick.DayOfWeek != raceDay)
        {
            var back = ((int)pick.DayOfWeek - (int)raceDay.Value + 7) % 7;
            pick = back <= 3 ? pick.AddDays(-back) : pick.AddDays(7 - back);
        }
        return pick.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Article titles that start with the year and contain one of the event's alias words.</summary>
    private async Task<List<string>> SearchTitlesAsync(WikiEvent ev, int year, CancellationToken ct)
    {
        var results = new List<string>();
        var query = Uri.EscapeDataString($"{year} {ev.SearchWords[0]}");
        using var doc = await ImportHttp.GetJsonAsync($"{Api}?action=query&list=search&srsearch={query}&srlimit=8&format=json&formatversion=2", ct);
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var hits)) return results;
        foreach (var hit in hits.EnumerateArray())
        {
            var t = hit.Str("title") ?? "";
            if (!t.StartsWith(year.ToString())) continue;
            if (ev.SearchWords.Any(a => TextFold.Contains(t, a))) results.Add(t);
        }
        return results.Take(3).ToList();
    }

    // ---------------------------------------------------------------- table parsing

    private record Table(List<string> Headers, List<List<string>> Rows);

    /// <summary>Finds the classification table(s) in an article and turns them into roster entries.</summary>
    public static List<RosterEntry> ParseResults(string html, int? nascarYear)
        => ParseRaces(html, nascarYear).SelectMany(r => r).ToList();

    /// <summary>
    /// The classification table(s) per race: an article about a weekend with two races (DTM, Supercars, IndyCar
    /// double-headers) has a table per race, each with its own winner. Tables without a winner (retirements) belong to
    /// the race before them.
    /// </summary>
    public static List<List<RosterEntry>> ParseRaces(string html, int? nascarYear)
    {
        html = Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline);
        html = Regex.Replace(html, "<sup[^>]*class=\"reference\"[^>]*>.*?</sup>", "", RegexOptions.Singleline);

        var tables = ExtractTables(html).Select(ParseTable).Where(t => t != null).Cast<Table>().ToList();

        // Results: Pos + Driver(s) + Laps. Several tables can hold one race (finishers, retirements).
        var results = tables.Where(t => Col(t, "pos", "fin", "position") >= 0 && Col(t, "driver") >= 0 && Col(t, "laps") >= 0).ToList();
        var chosen = results.Count > 0
            ? results
            // Fallback: the biggest entry list (No. + Driver + Team/Manufacturer).
            : tables.Where(t => Col(t, "driver") >= 0 && Col(t, "no", "#", "car no") >= 0)
                .OrderByDescending(t => t.Rows.Count).Take(1).ToList();

        var races = new List<List<RosterEntry>>();
        var seen = new HashSet<string>();
        foreach (var t in chosen)
        {
            int pos = Col(t, "pos", "fin", "position"), grid = Col(t, "grid", "st", "start"), cls = Col(t, "class"),
                no = Col(t, "no", "#", "car no"), team = Col(t, "team", "entrant", "owner"), drivers = Col(t, "driver"),
                car = Col(t, "chassis", "car", "vehicle", "make", "manufacturer"), laps = Col(t, "laps"),
                status = Col(t, "time/retired", "reason", "status", "time", "retired");

            // A table with its own winner while the race so far has one is the next race.
            var hasWinner = pos >= 0 && t.Rows.Any(r => pos < r.Count && r[pos] == "1");
            if (races.Count == 0 || (hasWinner && races[^1].Any(e => e.Position == 1)))
            {
                races.Add(new List<RosterEntry>());
                seen.Clear();
            }
            var entries = races[^1];
            foreach (var row in t.Rows)
            {
                string Get(int i) => i >= 0 && i < row.Count ? row[i] : "";
                var driver = Get(drivers);
                if (driver.Length == 0) continue;

                var key = $"{Get(pos)}|{Get(no)}|{driver}";
                if (!seen.Add(key)) continue; // rowspan sub-rows (e.g. the engine row) repeat the entry

                var carName = Get(car);
                if (nascarYear != null && carName.Length > 0) carName = $"{nascarYear} {carName}";
                var posText = Get(pos);
                entries.Add(new RosterEntry
                {
                    Driver = driver,
                    Team = NullIfEmpty(Get(team)),
                    Number = NullIfEmpty(Get(no).TrimStart('#')),
                    Class = NullIfEmpty(Get(cls)),
                    Car = NullIfEmpty(carName),
                    Position = int.TryParse(posText, out var p) ? p : null,
                    GridPosition = int.TryParse(Get(grid), out var g) && g > 0 ? g : null,
                    Laps = int.TryParse(Get(laps), out var l) ? l : null,
                    Status = NullIfEmpty(Get(status)) ?? (posText.Length > 0 && !int.TryParse(posText, out _) ? posText : null),
                });
            }
        }
        return races.Where(r => r.Count > 0).ToList();
    }

    /// <summary>Diagnostics: header and row count of each wikitable (RosterTool wiki "title").</summary>
    public static IEnumerable<string> DescribeTables(string html)
    {
        html = Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline);
        foreach (var t in ExtractTables(html).Select(ParseTable))
            yield return t == null ? "(no header row)" : $"rows={t.Rows.Count} [{string.Join(" | ", t.Headers)}]";
    }

    /// <summary>The article's title (after redirects) and HTML; null when it doesn't exist.</summary>
    public static async Task<(string Title, string Html)?> FetchPageAsync(string title, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{Api}?action=parse&page={Uri.EscapeDataString(title)}&prop=text&format=json&formatversion=2&redirects=1", ct);
        if (!doc.RootElement.TryGetProperty("parse", out var p)) return null;
        return (p.Str("title") ?? title, p.GetProperty("text").GetString() ?? "");
    }

    public static async Task<string?> FetchHtmlAsync(string title, CancellationToken ct = default)
    {
        using var doc = await ImportHttp.GetJsonAsync($"{Api}?action=parse&page={Uri.EscapeDataString(title)}&prop=text&format=json&formatversion=2&redirects=1", ct);
        return doc.RootElement.TryGetProperty("parse", out var p) ? p.GetProperty("text").GetString() : null;
    }

    private static int Col(Table t, params string[] names)
    {
        // Exact match first, then "starts with" (e.g. "Drivers", "Team / Entrant").
        for (var i = 0; i < t.Headers.Count; i++)
            if (names.Contains(t.Headers[i])) return i;
        for (var i = 0; i < t.Headers.Count; i++)
            if (names.Any(n => n.Length > 2 && t.Headers[i].StartsWith(n))) return i;
        return -1;
    }

    /// <summary>Top-level "wikitable" tables, nested tables included in their parent's text.</summary>
    private static IEnumerable<string> ExtractTables(string html)
    {
        var open = new Regex("<table\\b[^>]*>", RegexOptions.IgnoreCase);
        var tag = new Regex("<(/?)table\\b[^>]*>", RegexOptions.IgnoreCase);
        var pos = 0;
        while (true)
        {
            var m = open.Match(html, pos);
            if (!m.Success) yield break;
            // Find the matching </table>.
            var depth = 0;
            var end = -1;
            for (var t = tag.Match(html, m.Index); t.Success; t = t.NextMatch())
            {
                depth += t.Groups[1].Value == "/" ? -1 : 1;
                if (depth == 0) { end = t.Index + t.Length; break; }
            }
            if (end < 0) yield break;
            if (m.Value.Contains("wikitable"))
            {
                yield return html.Substring(m.Index, end - m.Index);
                pos = end;
            }
            else
            {
                // Layout/collapsible wrapper: look for wikitables inside it.
                pos = m.Index + m.Length;
            }
        }
    }

    /// <summary>Expands rowspan/colspan into a plain grid. The first all-header row gives the column names.</summary>
    private static Table? ParseTable(string tableHtml)
    {
        // Drop nested tables (flag boxes etc.) and the outer table tag.
        var inner = tableHtml[(tableHtml.IndexOf('>') + 1)..^"</table>".Length];
        inner = Regex.Replace(inner, "<table\\b.*?</table>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var rows = Regex.Matches(inner, "<tr\\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var cellRx = new Regex("<(th|td)\\b([^>]*)>(.*?)</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        List<string>? headers = null;
        var data = new List<List<string>>();
        var pending = new Dictionary<int, (int Left, string Text)>(); // rowspans carried down

        foreach (Match row in rows)
        {
            var cells = cellRx.Matches(row.Groups[1].Value);
            if (cells.Count == 0) continue;
            var isHeader = cells.All(c => c.Groups[1].Value.Equals("th", StringComparison.OrdinalIgnoreCase));

            var line = new List<string>();
            var col = 0;
            void FillPending()
            {
                while (pending.TryGetValue(col, out var p))
                {
                    line.Add(p.Text);
                    if (p.Left <= 1) pending.Remove(col); else pending[col] = (p.Left - 1, p.Text);
                    col++;
                }
            }

            foreach (Match c in cells)
            {
                FillPending();
                var attrs = c.Groups[2].Value;
                var text = CellText(c.Groups[3].Value);
                var colspan = Attr(attrs, "colspan");
                var rowspan = Attr(attrs, "rowspan");
                for (var k = 0; k < colspan; k++)
                {
                    line.Add(text);
                    if (rowspan > 1 && !isHeader) pending[col] = (rowspan - 1, text);
                    col++;
                }
            }
            FillPending();

            if (isHeader)
            {
                headers ??= line.Select(h => h.ToLowerInvariant().TrimEnd('.').Trim()).ToList();
                continue;
            }
            if (headers == null) continue;
            // Section rows ("Did not finish") have one value spread over the whole row.
            if (line.Where(v => v.Length > 0).Distinct().Count() <= 1) continue;
            data.Add(line);
        }
        return headers == null ? null : new Table(headers, data);
    }

    private static int Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, name + "\\s*=\\s*\"?(\\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) && v > 0 ? Math.Min(v, 50) : 1;
    }

    private static string CellText(string html)
    {
        var text = Regex.Replace(html, "<br\\s*/?>", " / ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\[\\d+\\]|\\[[a-z]\\]", "");
        text = Regex.Replace(text, "\\s+", " ").Trim();
        text = Regex.Replace(text, "(\\s*/\\s*)+", " / ").Trim(' ', '/');
        return text;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
