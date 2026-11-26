using System.Net;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>
/// A championship whose races are imported through its Wikipedia season articles: every race article the season
/// article links to that has a classification table.
/// </summary>
/// <param name="Key">Build option key and id prefix ("indycar", "wec", ...).</param>
/// <param name="SeasonTitles">Season article titles to try for a year ("{year}", "{yy}" = the next year's last two digits).</param>
/// <param name="SeriesWords">A race article's info box (or opening paragraph) must name the championship with one of
/// these (keeps out e.g. the F1 Grand Prix an IndyCar season article mentions).</param>
public record WikiSeries(string Key, string[] SeasonTitles, int FromYear, int ToYear, Func<int, string> Category,
    Func<int, string> SeriesName, string[] SeriesWords)
{
    /// <summary>The car for entries whose table names none (spec-chassis series list only the engine).</summary>
    public Func<int, string?>? DefaultCar { get; init; }
}

/// <summary>Imports the races of the championships in <see cref="Series"/> from their Wikipedia season articles.</summary>
public class WikiSeasonImporter
{
    /// <summary>Championships covered by "RosterTool build-db" through their season articles.</summary>
    public static readonly IReadOnlyList<WikiSeries> Series = new WikiSeries[]
    {
        new("wec", new[] { "{year} FIA World Endurance Championship", "{year}–{yy} FIA World Endurance Championship" }, 2012, 2026,
            y => WikipediaImporter.SportsCars(y, 2021), _ => "FIA World Endurance Championship", new[] { "World Endurance", "FIA WEC" }),
        new("imsa-classic", new[] { "{year} IMSA GT Championship", "{year} American Le Mans Series", "{year} United SportsCar Championship" }, 1971, 2015,
            y => WikipediaImporter.SportsCars(y, 2099),
            y => y <= 1998 ? "IMSA GT Championship" : y <= 2013 ? "American Le Mans Series" : "United SportsCar Championship",
            new[] { "IMSA", "American Le Mans", "Camel GT", "SportsCar Championship", "ALMS" }),
        new("indycar", new[] { "{year} IndyCar Series", "{year} IndyCar Series season", "{year} Indy Racing League", "{year} IRL IndyCar Series", "{year}–{yy} Indy Racing League" }, 1996, 2026,
            _ => "IndyCar", _ => "IndyCar Series", new[] { "IndyCar", "Indy Racing League", "IRL" })
            { DefaultCar = y => y >= 2018 ? "Dallara IR-18" : y >= 2012 ? "Dallara DW12" : y >= 2008 ? "Dallara IR-05" : null },
        new("cart", new[] { "{year} CART season", "{year} Champ Car season", "{year} CART PPG World Series", "{year} CART FedEx Championship Series", "{year} Champ Car World Series", "{year} IndyCar season" }, 1979, 2007,
            _ => "IndyCar", y => y >= 2004 ? "Champ Car World Series" : "CART",
            new[] { "CART", "Champ Car", "Indy Car", "IndyCar", "PPG" })
            { DefaultCar = y => y is >= 2007 ? "Panoz DP01" : null },
        new("dtm", new[] { "{year} Deutsche Tourenwagen Masters", "{year} DTM season", "{year} Deutsche Tourenwagen Meisterschaft" }, 1984, 2026,
            // GT3 cars since 2021.
            y => y >= 2021 ? "GT3" : "DTM", _ => "DTM", new[] { "DTM", "Deutsche Tourenwagen" }),
        new("supercars", new[] { "{year} Supercars Championship", "{year} V8 Supercars Championship", "{year} V8 Supercar Championship Series",
                "{year} V8 Supercars Championship Series", "{year} Shell Championship Series", "{year} Australian Touring Car Championship" }, 1997, 2026,
            _ => "Supercars", _ => "Supercars Championship", new[] { "Supercar", "V8 Supercar", "Touring Car Championship", "Shell Championship" }),
    };

    /// <summary>
    /// The season article for the year (null when none exists). One query finds which of the possible titles exists
    /// (Wikipedia limits how fast articles can be read, so every request counts).
    /// </summary>
    public async Task<(string Title, string Html)?> SeasonAsync(WikiSeries series, int year, CancellationToken ct = default)
    {
        var candidates = series.SeasonTitles
            .Select(p => p.Replace("{year}", year.ToString()).Replace("{yy}", ((year + 1) % 100).ToString("00"))).ToList();
        var url = "https://en.wikipedia.org/w/api.php?action=query&redirects=1&format=json&formatversion=2&titles="
                  + Uri.EscapeDataString(string.Join("|", candidates));
        using var doc = await ImportHttp.GetJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("query", out var q)) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "normalized", "redirects" })
            if (q.TryGetProperty(key, out var arr))
                foreach (var x in arr.EnumerateArray())
                    if (x.Str("from") is { } from && x.Str("to") is { } to) map[from] = to;
        var existing = q.TryGetProperty("pages", out var pages)
            ? pages.EnumerateArray().Where(pg => !pg.TryGetProperty("missing", out _) && !pg.TryGetProperty("invalid", out _))
                .Select(pg => pg.Str("title") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
        foreach (var c in candidates)
        {
            var title = c;
            for (var i = 0; i < 3 && map.TryGetValue(title, out var next); i++) title = next;
            // A redirect to the series article (no year) or another season is not this season.
            if (!existing.Contains(title) || !title.StartsWith(year.ToString())) continue;
            await Task.Delay(300, ct);
            return await WikipediaImporter.FetchPageAsync(title, ct);
        }
        return null;
    }

    /// <summary>A season or championship article (of any series), not a race.</summary>
    private static bool IsSeasonTitle(string title)
        => Regex.IsMatch(title, @"\b(season|series|championship|championnat|cup|trophy|lights|formula|calendar|standings|in motorsport|in sports?)\b", RegexOptions.IgnoreCase)
           && !Regex.IsMatch(title, @"\b(500|400|300|200|1000|grand prix|hours?|round)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Titles of the existing articles the season article links to that look like races of that season (and of the next
    /// year for a season that spans two, like "2019–20").
    /// </summary>
    public static List<string> RaceLinks(string seasonHtml, int year, bool spansTwoYears = false)
    {
        // The races are linked from the season's tables (calendar, results); links in the text and the navigation
        // boxes are mostly other series and years, and each one would cost a request.
        var tables = string.Concat(Regex.Matches(seasonHtml, "<table[^>]*class=\"[^\"]*wikitable[^\"]*\"[^>]*>.*?</table>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase).Select(m => m.Value));
        var fromTables = Links(tables, year, spansTwoYears);
        return fromTables.Count > 0 ? fromTables : Links(seasonHtml, year, spansTwoYears);
    }

    private static List<string> Links(string html, int year, bool spansTwoYears)
    {
        var titles = new List<string>();
        foreach (Match m in Regex.Matches(html, "<a\\s[^>]*href=\"/wiki/([^\"#?]+)\"[^>]*>", RegexOptions.IgnoreCase))
        {
            if (m.Value.Contains("class=\"new\"") || m.Value.Contains("redlink")) continue;
            var title = WebUtility.HtmlDecode(Uri.UnescapeDataString(m.Groups[1].Value)).Replace('_', ' ');
            if (title.Contains(':')) continue; // File:, Category:, Template: ...
            var y = Regex.Match(title, @"^(\d{4})\b(?![–-])");
            if (!y.Success || (int.Parse(y.Value) != year && !(spansTwoYears && int.Parse(y.Value) == year + 1))) continue;
            if (IsSeasonTitle(title)) continue;
            if (!titles.Contains(title)) titles.Add(title);
        }
        return titles;
    }

    /// <summary>
    /// The race(s) of an article (a weekend can have two), or none when it isn't one of this championship's races (the
    /// article doesn't name the championship) or has no classification table.
    /// </summary>
    public async Task<List<Roster>> ImportRaceAsync(WikiSeries series, string title, CancellationToken ct = default)
    {
        if (await WikipediaImporter.FetchPageAsync(title, ct) is not { } page) return new();
        return FromArticle(series, page.Title, page.Html);
    }

    public static List<Roster> FromArticle(WikiSeries series, string pageTitle, string html)
    {
        var none = new List<Roster>();
        var y = Regex.Match(pageTitle, @"^(\d{4})\b");
        if (!y.Success || IsSeasonTitle(pageTitle)) return none; // e.g. a race title that redirects to its season
        var year = int.Parse(y.Value);
        html = Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline);
        // The info box, else (articles without one) the opening paragraphs.
        var box = Infobox(html);
        var lead = Lead(html);
        var about = Plain(box ?? lead);
        if (!series.SeriesWords.Any(w => Regex.IsMatch(about, $@"\b{Regex.Escape(w)}s?\b", RegexOptions.IgnoreCase))) return none;

        var races = WikipediaImporter.ParseRaces(html, null).Where(r => r.Count(e => e.Position != null) >= 3).ToList();
        if (races.Count == 0) return none;
        var track = box != null ? Track(box) : null;
        if (track == null && CircuitLink(lead) is { } leadCircuit) track = new RosterTrack { Name = leadCircuit, Key = leadCircuit.ToLowerInvariant() };
        if (track == null) return none;
        // The road course inside the Indianapolis oval (IndyCar's spring Grand Prix).
        if (Regex.IsMatch(track.Name, "^Indianapolis Motor Speedway$", RegexOptions.IgnoreCase) && !Regex.IsMatch(pageTitle, @"\b500\b|brickyard"))
            track.Name += " road course";

        var defaultCar = series.DefaultCar?.Invoke(year);
        foreach (var e in races.SelectMany(r => r))
        {
            // Rookie / past-winner marks after the name ("Colton Herta R").
            e.Driver = Regex.Replace(e.Driver, @"(\s+(R|W|RY|\(R\)|\(W\))|\s*[*†‡])+$", "").Trim();
            if (e.Car == null && defaultCar != null) e.Car = defaultCar;
        }

        var date = WikipediaImporter.InfoboxDate(html, year) ?? year.ToString();
        return races.Select((entries, i) => new Roster
        {
            Id = RosterIds.Slugify($"{series.Key}-{pageTitle}{(races.Count > 1 ? $"-race-{i + 1}" : "")}"),
            Name = races.Count > 1 ? $"{pageTitle} (Race {i + 1})" : pageTitle,
            Category = series.Category(year),
            Series = series.SeriesName(year),
            Year = year,
            Date = date,
            Track = new RosterTrack { Name = track.Name, Key = track.Key, Locality = track.Locality },
            Source = "Wikipedia: https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_')),
            Notes = races.Count > 1 ? $"Race {i + 1} of {races.Count} of the weekend." : null,
            Entries = entries,
        }).ToList();
    }

    /// <summary>The article's info box (up to its own end, nested tables included).</summary>
    private static string? Infobox(string html)
    {
        var start = Regex.Match(html, "<table[^>]*class=\"[^\"]*infobox[^\"]*\"[^>]*>", RegexOptions.IgnoreCase);
        if (!start.Success) return null;
        var depth = 0;
        for (var t = Regex.Match(html[start.Index..], "<(/?)table\\b[^>]*>", RegexOptions.IgnoreCase); t.Success; t = t.NextMatch())
        {
            depth += t.Groups[1].Value == "/" ? -1 : 1;
            if (depth == 0) return html.Substring(start.Index, t.Index + t.Length);
        }
        return html.Substring(start.Index, Math.Min(20000, html.Length - start.Index));
    }

    /// <summary>The first two paragraphs of the article text.</summary>
    private static string Lead(string html)
        => string.Join(" ", Regex.Matches(html, "<p>(?:(?!</p>).)+</p>", RegexOptions.Singleline).Take(2).Select(m => m.Value));

    private static readonly Regex CircuitWords = new(
        @"circuit|raceway|speedway|motorsports? park|motor park|motorpark|\bring\b|ring$|autodrom|autódromo|race ?course|street circuit|streets of|\bpark\b|race ?track|" +
        @"glen|mosport|sebring|laguna|road america|road atlanta|mid-ohio|lime rock|mount panorama|sandown|norisring|hockenheim|nürburgring|zolder|" +
        @"assen|lausitz|oschersleben|brno|zandvoort|misano|imola|monza|spa-francorchamps|silverstone|fuji|shanghai|sakhir|losail|portim|interlagos|" +
        @"sepang|kyalami|surfers paradise|homestead|pukekohe|hidden valley|barbagallo|symmons|wanneroo|oran park|eastern creek|queensland|calder|winton|" +
        @"the bend|pocono|milwaukee|nazareth|gateway|elkhart|sonoma|portland|toronto|vancouver|edmonton|mont-tremblant|brands hatch|donington|catalunya",
        RegexOptions.IgnoreCase);
    private static readonly Regex EventWords = new(@"grand prix|\b\d{4}\b|\bhours?\b|\b(500|400|600|300|250|200|1000)\b|series|championship|season|super ?sprint|\brace\b|\bracing\b|challenge|showcase|festival|classic\b|presented|weekend|\(motorsport\)|\bdtm\b$|association|gmbh|\bteam\b|company|corporation|federation|\bclub\b|list of",
        RegexOptions.IgnoreCase);

    /// <summary>The first linked article in the HTML that is a circuit (not the event, a city or a series).</summary>
    private static string? CircuitLink(string html)
    {
        foreach (Match m in Regex.Matches(html, "<a\\s[^>]*title=\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var t = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (t.Contains(':') || EventWords.IsMatch(t) || !CircuitWords.IsMatch(t)) continue;
            return Regex.Replace(t, @"\s*\((?:racing circuit|race track|circuit|racetrack|track|motorsport)\)", "", RegexOptions.IgnoreCase).Trim();
        }
        return null;
    }

    /// <summary>
    /// The circuit from the info box: its Venue / Circuit / Location row (a linked circuit, else the row's text), else
    /// any circuit the info box links to. A road course inside a speedway is named as such.
    /// </summary>
    public static RosterTrack? Track(string box)
    {
        string? Row(string name)
        {
            var m = Regex.Match(box, $@"<th[^>]*>\s*(?:<[^>]+>\s*)*{name}\s*(?:<[^>]+>\s*)*</th>\s*<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        var cells = new[] { "Venue", "Circuit", "Location" }.Select(Row).Where(c => c != null).Cast<string>().ToList();
        var name = cells.Select(CircuitLink).FirstOrDefault(n => n != null)
                   ?? cells.Select(c => Plain(c).Split(',')[0].Trim()).FirstOrDefault(n => n.Length > 0)
                   ?? CircuitLink(box);
        if (string.IsNullOrWhiteSpace(name)) return null;
        // A town for its circuit.
        foreach (var (town, circuit) in Towns)
            if (Regex.IsMatch(name, town, RegexOptions.IgnoreCase)) { name = circuit; break; }
        if (Regex.IsMatch(name, @"^(temporary )?street circuit$", RegexOptions.IgnoreCase)) return null;
        var locality = cells.Count > 0 ? Plain(cells[^1]) : null;
        var courseText = Plain(Row("Course") ?? "") + " " + string.Join(" ", cells.Select(Plain));
        if (Regex.IsMatch(name, @"speedway", RegexOptions.IgnoreCase) && !Regex.IsMatch(name, "road course", RegexOptions.IgnoreCase)
            && Regex.IsMatch(courseText, @"road course|road circuit", RegexOptions.IgnoreCase))
            name += " road course";
        return new RosterTrack { Name = name, Key = name.ToLowerInvariant(), Locality = locality is { Length: > 0 } && locality != name ? locality : null };
    }

    private static readonly (string Town, string Circuit)[] Towns =
    {
        (@"^Elkhart Lake", "Road America"), (@"^Lexington, Ohio", "Mid-Ohio Sports Car Course"), (@"^Braselton", "Road Atlanta"),
        (@"^Bowmanville", "Canadian Tire Motorsport Park"), (@"^Lakeville, Connecticut", "Lime Rock Park"), (@"^Alton, Virginia", "Virginia International Raceway"),
        (@"^Watkins Glen, New York", "Watkins Glen International"), (@"^Monterey, California", "Laguna Seca"), (@"^Sebring, Florida", "Sebring International Raceway"),
        (@"^Bathurst, New South Wales", "Mount Panorama Circuit"), (@"^Spielberg", "Red Bull Ring"), (@"^Heusden-Zolder", "Circuit Zolder"),
    };

    private static string Plain(string html)
        => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<sup.*?</sup>", "", RegexOptions.Singleline), "<[^>]+>", " ")), @"\s+", " ").Trim();
}
