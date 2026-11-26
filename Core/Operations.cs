using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HistoricRosterPlugin.Ai;

namespace HistoricRosterPlugin;

public record DbBuildOptions
{
    /// <summary>F1 goes back to 1950; the other series start where their sources do (NASCAR feed 2017, Wikipedia events per event).</summary>
    public int From { get; init; } = 1950;
    public int To { get; init; } = DateTime.UtcNow.Year;
    /// <summary>f1, nascar, endurance, nascar-classic, imsa (IMSA timing archive), and the championships read from
    /// Wikipedia season articles: wec, imsa-classic, indycar, cart, dtm, supercars.</summary>
    public string[] Series { get; init; } = AllSeries;
    public static readonly string[] AllSeries =
        { "f1", "nascar", "endurance", "nascar-classic", "imsa", "wec", "imsa-classic", "indycar", "cart", "dtm", "supercars" };
    /// <summary>1 = Cup, 2 = Xfinity, 3 = Truck.</summary>
    public int[] NascarSeries { get; init; } = { 1 };
    public bool Refresh { get; init; }
    /// <summary>OpenAI / DeepSeek fill in what the sources lack (real F1 chassis names). Null = no AI.</summary>
    public AiSettings? Ai { get; init; }
}

/// <summary>Downloads real race results into historic_rosters_db.json (F1 API, NASCAR feed, IMSA timing archive, Wikipedia).</summary>
public static class DatabaseBuilder
{
    public static async Task<RosterDatabase> BuildAsync(string dbPath, DbBuildOptions o, IProgress<string> log, CancellationToken ct = default)
    {
        var db = RosterDatabase.Load(dbPath);
        var to = Math.Min(o.To, DateTime.UtcNow.Year);
        var before = db.Rosters.Count;
        var added = 0;
        var failures = 0;
        log.Report($"Database: {dbPath} ({before} races). Seasons {o.From}-{to}.");

        void Add(Roster r)
        {
            db.Upsert(r);
            if (++added % 25 == 0) db.Save(dbPath); // checkpoint
        }

        if (o.Series.Contains("f1"))
        {
            var f1 = new F1Importer();
            for (var y = o.From; y <= to; y++)
            {
                ct.ThrowIfCancellationRequested();
                if (!o.Refresh && db.Rosters.Count(r => r.Category == "F1" && r.Year == y) >= 15 && y < DateTime.UtcNow.Year)
                    continue;
                try
                {
                    var season = await f1.ImportSeasonAsync(y, TimeSpan.FromMilliseconds(400), ct);
                    foreach (var r in season) Add(r);
                    log.Report($"F1 {y}: {season.Count} races");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures++; log.Report($"F1 {y}: failed ({ex.Message})"); }
                await Task.Delay(400, ct);
            }
        }

        if (o.Series.Contains("nascar"))
        {
            var nascar = new NascarImporter();
            foreach (var s in o.NascarSeries)
                for (var y = Math.Max(o.From, NascarImporter.FirstYear); y <= to; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<EventSummary> races;
                    try { races = await nascar.ListSeasonAsync(y, s, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures++; log.Report($"NASCAR {y} series {s}: list failed ({ex.Message})"); continue; }

                    var got = 0;
                    foreach (var race in races.Where(r => DateTime.TryParse(r.Date, out var d) && d < DateTime.UtcNow.Date))
                    {
                        var id = RosterIds.Slugify($"nascar-{y}-s{s}-{race.Name}");
                        if (!o.Refresh && db.Contains(id)) continue;
                        try
                        {
                            var imported = await nascar.ImportAsync(y, s, int.Parse(race.Key), ct);
                            // The list's race name can differ from the result's: count only races really new.
                            if (!db.Contains(imported.Id)) got++;
                            Add(imported);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { failures++; log.Report($"  {race.Name}: {ex.Message}"); }
                        await Task.Delay(250, ct);
                    }
                    log.Report(got > 0 ? $"{NascarImporter.SeriesName(s)} {y}: {got} new races" : $"{NascarImporter.SeriesName(s)} {y}: up to date");
                }
        }

        // Wikipedia event-years that had no usable results: not asked again (each request counts towards
        // Wikipedia's rate limit, which otherwise makes every update wait minutes). --refresh asks again.
        var skipPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "_wiki_no_results.json");
        var skip = o.Refresh ? new HashSet<string>() : AppData.Load<HashSet<string>>(skipPath) ?? new HashSet<string>();

        // IMSA (WeatherTech and Michelin Pilot Challenge) from IMSA's timing archive: every round from 2016 on. These
        // official results replace the Wikipedia copies of Daytona, Sebring and Petit Le Mans (same ids).
        static bool FromImsa(Roster r) => r.Source?.StartsWith(ImsaImporter.BaseUrl, StringComparison.OrdinalIgnoreCase) == true;
        if (o.Series.Contains("imsa"))
        {
            var imsa = new ImsaImporter();
            var known = db.Rosters.Where(FromImsa).Select(r => r.Source!).ToHashSet();
            foreach (var series in ImsaImporter.AllSeries)
                for (var y = Math.Max(o.From, ImsaImporter.FirstYear); y <= to; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    // A past season already read completely isn't listed again (each listing is ~50 requests).
                    if (!o.Refresh && y < DateTime.Now.Year && skip.Contains($"imsa-season:{series.Key}-{y}")) continue;
                    List<ImsaRaceRef> races;
                    try { races = await imsa.ListSeasonAsync(y, series, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures++; log.Report($"{series.Name} {y}: list failed ({ex.Message})"); continue; }
                    var got = 0;
                    var complete = true;
                    foreach (var race in races.Where(r => r.LocalStart < DateTime.Now))
                    {
                        if (!o.Refresh && (known.Contains(race.Url) || skip.Contains("imsa:" + race.Url))) continue;
                        try
                        {
                            var r = await imsa.ImportAsync(race, ct);
                            if (r == null)
                            {
                                if (race.LocalStart < DateTime.Now.AddDays(-14)) skip.Add("imsa:" + race.Url);
                                else complete = false;
                                continue;
                            }
                            Add(r);
                            known.Add(race.Url);
                            got++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { failures++; complete = false; log.Report($"  {race.Event} {race.Session}: {ex.Message}"); }
                        await Task.Delay(150, ct);
                    }
                    if (complete && races.Count > 0 && y < DateTime.Now.Year) skip.Add($"imsa-season:{series.Key}-{y}");
                    log.Report(got > 0 ? $"{series.Name} {y}: {got} new races" : $"{series.Name} {y}: up to date");
                }
        }

        var wiki = new WikipediaImporter();
        var events = WikipediaImporter.Events.Where(e =>
            (o.Series.Contains("endurance") && e.Category(2000) != "NASCAR") ||
            (o.Series.Contains("nascar-classic") && e.Category(2000) == "NASCAR"));
        foreach (var ev in events)
        {
            var got = 0;
            for (var y = Math.Max(o.From, ev.FromYear); y <= Math.Min(to, ev.ToYear); y++)
            {
                ct.ThrowIfCancellationRequested();
                // NASCAR seasons the feed covers come from the feed (selected now, or already in the database).
                if (ev.Category(y) == "NASCAR" && y >= NascarImporter.FirstYear
                    && (o.Series.Contains("nascar") || db.Rosters.Any(r => r.Year == y && r.Id.StartsWith("nascar-")))) continue;
                var id = RosterIds.Slugify($"{ev.Key}-{y}");
                if (!o.Refresh && (db.Contains(id) || skip.Contains(id))) continue;
                if (db.Rosters.Any(r => r.Id == id && FromImsa(r))) continue; // IMSA's official results are better
                try
                {
                    var r = await wiki.ImportAsync(ev, y, ct);
                    if (r != null) { Add(r); got++; }
                    else if (y < DateTime.UtcNow.Year) skip.Add(id); // this year's may still appear
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures++; log.Report($"  {ev.Key} {y}: {ex.Message}"); }
                await Task.Delay(200, ct);
            }
            log.Report(got > 0 ? $"{ev.Series}: {got} new events" : $"{ev.Series}: up to date");
        }

        // Championships read through their Wikipedia season articles: every linked race article with a classification.
        static string? WikiTitle(string? source)
        {
            var i = source?.LastIndexOf("/wiki/", StringComparison.Ordinal) ?? -1;
            return i < 0 ? null : Uri.UnescapeDataString(source![(i + 6)..]).Replace('_', ' ');
        }
        var wikiSources = db.Rosters.Select(r => WikiTitle(r.Source)).Where(t => t != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seasons = new WikiSeasonImporter();
        foreach (var series in WikiSeasonImporter.Series.Where(s => o.Series.Contains(s.Key)))
        {
            var got = 0;
            for (var y = Math.Max(o.From, series.FromYear); y <= Math.Min(to, series.ToYear); y++)
            {
                ct.ThrowIfCancellationRequested();
                var seasonKey = $"season:{series.Key}-{y}";
                if (!o.Refresh && skip.Contains(seasonKey)) continue;
                (string Title, string Html)? season;
                try { season = await seasons.SeasonAsync(series, y, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures++; log.Report($"  {series.Key} {y}: {ex.Message}"); continue; }
                if (season is not { } s)
                {
                    if (y < DateTime.Now.Year) skip.Add(seasonKey);
                    continue;
                }
                var complete = true;
                foreach (var title in WikiSeasonImporter.RaceLinks(s.Html, y, s.Title.Contains('–')))
                {
                    ct.ThrowIfCancellationRequested();
                    // Races already in the database (Le Mans, Bathurst, the Indy 500 of another season article ...).
                    if (wikiSources.Contains(title) || (!o.Refresh && skip.Contains("wiki:" + title))) continue;
                    try
                    {
                        var raceDay = await seasons.ImportRaceAsync(series, title, ct);
                        if (raceDay.Count == 0)
                        {
                            if (!title.StartsWith(DateTime.Now.Year.ToString())) skip.Add("wiki:" + title);
                            else complete = false;
                        }
                        else if (!wikiSources.Contains(WikiTitle(raceDay[0].Source) ?? ""))
                        {
                            foreach (var r in raceDay) Add(r);
                            wikiSources.Add(WikiTitle(raceDay[0].Source)!);
                            got += raceDay.Count;
                        }
                        wikiSources.Add(title);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures++; complete = false; log.Report($"  {title}: {ex.Message}"); }
                    await Task.Delay(300, ct);
                }
                if (complete && y < DateTime.Now.Year) skip.Add(seasonKey);
                try { AppData.Save(skipPath, skip); } catch { /* only saves time next run */ }
            }
            log.Report(got > 0 ? $"{series.Key}: {got} new races" : $"{series.Key}: up to date");
        }

        // One copy per race: for NASCAR seasons the feed covers, drop the Wikipedia crown-jewel copies.
        var feedYears = db.Rosters.Where(r => r.Category == "NASCAR" && r.Id.StartsWith("nascar-")).Select(r => r.Year).ToHashSet();
        var wikiNascar = WikipediaImporter.Events.Where(e => e.Category(2000) == "NASCAR").Select(e => e.Key + "-").ToList();
        var dupes = db.Rosters.RemoveAll(r => r.Category == "NASCAR" && feedYears.Contains(r.Year) && wikiNascar.Any(k => r.Id.StartsWith(k)));
        if (dupes > 0) log.Report($"Removed {dupes} Wikipedia NASCAR races already covered by the NASCAR feed.");

        await BackfillTimesAsync(db, skip, log, ct);

        if (o.Ai is { Configured: true } ai)
        {
            db.Save(dbPath); // the downloads are kept even if the AI step is stopped
            try { await AiDatabase.FillF1ChassisAsync(db, Path.GetDirectoryName(Path.GetFullPath(dbPath))!, ai, skip, log, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Report($"AI: {ex.Message}"); }
            db.Save(dbPath);
            try { await AiStartTimes.FillF1StartTimesAsync(db, Path.GetDirectoryName(Path.GetFullPath(dbPath))!, ai, skip, log, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Report($"AI start times: {ex.Message}"); }
        }

        db.Save(dbPath);
        try { AppData.Save(skipPath, skip); } catch { /* only saves time next run */ }
        log.Report($"Done: {db.Rosters.Count} races ({db.Rosters.Count - before:+#;-#;0} new, {failures} failures).");
        foreach (var g in db.Rosters.GroupBy(r => r.Category ?? "?").OrderBy(g => g.Key))
            log.Report($"  {g.Key,-9} {g.Count(),4}  ({g.Min(r => r.Year)}-{g.Max(r => r.Year)})");
        return db;
    }

    /// <summary>
    /// Real start times, race days and circuit positions for races imported before these were stored: NASCAR feed
    /// races get the official green-flag time, Wikipedia races the day from the article and the event's usual start.
    /// Only races still missing them are looked up, so it's quick once done.
    /// </summary>
    public static async Task<int> BackfillTimesAsync(RosterDatabase db, HashSet<string> skip, IProgress<string> log, CancellationToken ct = default)
    {
        var fixedCount = 0;

        // NASCAR feed: ".../cacher/{year}/{series}/{raceId}/weekend-feed.json"
        var nascar = new NascarImporter();
        var feed = db.Rosters.Where(r => r.Time == null && r.Source != null)
            .Select(r => (Roster: r, M: System.Text.RegularExpressions.Regex.Match(r.Source!, @"cacher/(\d{4})/(\d)/(\d+)/weekend-feed")))
            .Where(x => x.M.Success).ToList();
        foreach (var season in feed.GroupBy(x => (Year: int.Parse(x.M.Groups[1].Value), Series: int.Parse(x.M.Groups[2].Value))))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var times = await nascar.StartTimesAsync(season.Key.Year, season.Key.Series, ct);
                foreach (var (r, m) in season)
                    if (times.TryGetValue(m.Groups[3].Value, out var t)) { r.Time = t; fixedCount++; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Report($"  NASCAR {season.Key.Year} start times: {ex.Message}"); }
            await Task.Delay(150, ct);
        }

        // F1 (Jolpica): the season schedule has each round's UTC start (from 2005 on).
        var f1 = db.Rosters.Where(r => r.Time == null && r.Source != null)
            .Select(r => (Roster: r, M: System.Text.RegularExpressions.Regex.Match(r.Source!, @"ergast/f1/(\d{4})/(\d+)/")))
            .Where(x => x.M.Success && !skip.Contains("time:f1-" + x.M.Groups[1].Value)).ToList();
        foreach (var season in f1.GroupBy(x => x.M.Groups[1].Value))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = await ImportHttp.GetJsonAsync($"https://api.jolpi.ca/ergast/f1/{season.Key}.json?limit=100", ct);
                var times = doc.RootElement.GetProperty("MRData").GetProperty("RaceTable").GetProperty("Races").EnumerateArray()
                    .Where(x => x.Str("time") != null).ToDictionary(x => x.Str("round")!, x => x.Str("time")!);
                foreach (var (r, m) in season)
                    if (times.TryGetValue(m.Groups[2].Value, out var t)) { r.Time = t; fixedCount++; }
                if (times.Count == 0) skip.Add("time:f1-" + season.Key); // no times that far back
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Report($"  F1 {season.Key} start times: {ex.Message}"); }
            await Task.Delay(400, ct);
        }

        // Wikipedia events: usual start, circuit position and the real day (read 50 articles per request).
        var wikiDays = new List<(Roster Roster, string Title, DayOfWeek? RaceDay)>();
        foreach (var ev in WikipediaImporter.Events)
            foreach (var r in db.Rosters.Where(r => r.Year is { } && r.Id == RosterIds.Slugify($"{ev.Key}-{r.Year}")))
            {
                if (r.UsualLocalTime == null && ev.UsualStart != null) { r.UsualLocalTime = ev.UsualStart(r.Year!.Value); fixedCount++; }
                if (r.Track != null && r.Track.Latitude == null && ev.Position is { } p) { r.Track.Latitude = p.Lat; r.Track.Longitude = p.Lon; }
                if ((r.Date?.Length ?? 0) >= 10 || skip.Contains("date:" + r.Id) || r.Source?.Contains("/wiki/") != true) continue;
                wikiDays.Add((r, Uri.UnescapeDataString(r.Source[(r.Source.LastIndexOf("/wiki/", StringComparison.Ordinal) + 6)..]).Replace('_', ' '), ev.RaceDay));
            }
        if (wikiDays.Count > 0)
        {
            try
            {
                var days = await WikipediaImporter.InfoboxDatesAsync(wikiDays.Select(x => (x.Title, x.Roster.Year!.Value, x.RaceDay)).ToList(), ct);
                foreach (var (r, title, _) in wikiDays)
                    if (days.TryGetValue(title, out var day)) { r.Date = day; fixedCount++; }
                    else skip.Add("date:" + r.Id);
                log.Report($"Wikipedia race days: {days.Count} of {wikiDays.Count} found.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Report($"  Wikipedia race days: {ex.Message}"); }
        }

        if (fixedCount > 0) log.Report($"Real start times / race days filled in: {fixedCount}.");
        return fixedCount;
    }
}

/// <summary>Matches installed cars to the real cars they are, and builds rosters from installed liveries.</summary>
public static class LibraryScanner
{
    public const string CatalogFile = "content_catalog.installed.json";

    /// <summary>Bumped when the matching logic changes, so the next start re-scans even if no folder changed.</summary>
    private const int ScanVersion = 5; // 3: Ram trucks matched as "RAM"; 4: trucks and Cup cars kept apart; 5: IMSA, IndyCar, DTM, GT4, TCR, Group C
    private const string StampFile = "_library_scan.json";

    private record ScanStamp(string ContentFolder, DateTime CarsUtc, DateTime TracksUtc, int Cars, int Tracks, int Version);

    private static ScanStamp CurrentStamp(Workspace ws)
    {
        var cars = Path.Combine(ws.ContentFolder, "cars");
        var tracks = Path.Combine(ws.ContentFolder, "tracks");
        int Count(string dir) => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Count() : 0;
        DateTime Time(string dir) => Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir) : DateTime.MinValue;
        return new ScanStamp(ws.ContentFolder, Time(cars), Time(tracks), Count(cars), Count(tracks), ScanVersion);
    }

    /// <summary>
    /// Re-matches your cars AND tracks when either folder changed since the last scan (a car or track added or
    /// removed), when the matching logic was updated, or when there is no scan yet. Returns true when it scanned.
    /// </summary>
    public static bool ScanIfChanged(Workspace ws, IProgress<string> log)
    {
        var path = Path.Combine(ws.RostersFolder, StampFile);
        var now = CurrentStamp(ws);
        ScanStamp? last = null;
        try { if (File.Exists(path)) last = JsonSerializer.Deserialize<ScanStamp>(File.ReadAllText(path), RosterJson.Options); } catch { }
        if (last == now && File.Exists(Path.Combine(ws.RostersFolder, CatalogFile))) return false;
        log.Report(last == null ? "Matching your installed cars and tracks to the real ones ..." : "Your cars or tracks changed: matching them again ...");
        Scan(ws, log);
        return true;
    }

    public static void Scan(Workspace ws, IProgress<string> log, int maxCars = 40)
    {
        var lib = ws.Library;
        log.Report($"Scanned {ws.ContentFolder}: {lib.Cars.Count} cars, {lib.Tracks.Count} track layouts.");

        // Rules that map real cars to the installed folders that ARE those cars:
        // F1 = same team + same season (team from its name or chassis code);
        // NASCAR = same make + same car generation, the nearest model year, and the track-type version
        //          (road course / superspeedway / short track) where the mod has them;
        // GT, prototypes, Supercars = the distinctive words of the model name (e.g. "499p", "m4 gt3", "ford ba")
        //          from its first season for 5 seasons.
        // Cars without a year or a recognisable model name get no rule (never a look-alike).
        var rules = new List<CarRule>();
        var stock = new List<(CarInfo Car, string Make, string Variant)>();
        var skipped = 0;
        foreach (var car in lib.Cars.Where(c => c.Category is "F1" or "NASCAR" or "GT1" or "GT3" or "GTE" or "LMP1" or "LMP2" or "Hypercar" or "Supercars"
                                                 or "Group C" or "GT4" or "TCR" or "DTM" or "IndyCar"))
        {
            if (car.Year == null) { skipped++; continue; }
            var note = $"Installed: {car.Name} (from scan)";
            if (car.Category == "F1")
            {
                var teams = F1Teams.Identify(car.Name, car.Folder, car.Brand, car.Year.Value);
                if (teams.Count == 0) { skipped++; continue; }
                rules.AddRange(teams.Select(team => new CarRule
                    { Category = "F1", YearFrom = car.Year, YearTo = car.Year, Team = team, Model = car.Folder, Kunos = car.Kunos, Note = note }));
                continue;
            }
            if (car.Category == "NASCAR")
            {
                if (StockCars.Make(car) is not { } make) { skipped++; continue; }
                stock.Add((car, make, StockCars.Variant(car)));
                continue;
            }
            var words = CarNames.ModelWords(car);
            if (words.Count == 0) { skipped++; continue; }
            rules.Add(new CarRule
            {
                // Classes differ between series (GT3 = GTD = SP9 ...), so the model words do the matching, not the category.
                Category = car.Category == "Supercars" ? "Supercars" : null,
                YearFrom = car.Year, YearTo = car.Year + 5,
                CarWords = words,
                // A car without "Evo" in its name isn't the later Evo version.
                Exclude = car.Name.Contains("evo", StringComparison.OrdinalIgnoreCase) ? null : new List<string> { "Evo" },
                Model = car.Folder, Kunos = car.Kunos, Note = note,
            });
        }
        rules.AddRange(StockCars.Rules(stock));
        // Exact-season rules first, and track-type versions before the general version of a car.
        rules = rules.OrderByDescending(r => r.YearFrom == r.YearTo ? 1 : 0).ThenByDescending(r => r.Tracks != null ? 1 : 0).ToList();
        if (skipped > 0) log.Report($"{skipped} race cars have no year or recognisable model in ui_car.json; add catalog rules for them by hand.");

        var trackRules = TrackMatcher.Rules(lib, ws.LoadRosters(), log);

        File.WriteAllText(Path.Combine(ws.RostersFolder, CatalogFile),
            "// Written by a library scan of " + ws.ContentFolder.Replace('\\', '/') + ". It's redone automatically when your cars or tracks change.\n" +
            JsonSerializer.Serialize(new ContentCatalog { Cars = rules, Tracks = trackRules }, RosterJson.Options), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(ws.RostersFolder, StampFile), JsonSerializer.Serialize(CurrentStamp(ws), RosterJson.Options));
        log.Report($"Matched {rules.Select(r => r.Model).Distinct().Count()} installed cars and {trackRules.Count} real circuits to your installed content.");

        // Rosters built from installed cars and their liveries (driver/number/team from ui_skin.json).
        foreach (var group in lib.Cars.Where(c => c.Category != null).GroupBy(c => c.Category!))
        {
            var entries = new List<RosterEntry>();
            foreach (var car in group.OrderByDescending(c => c.Year ?? 0))
            {
                var skins = car.Skins.Where(s => s.Driver != null).ToList();
                if (skins.Count == 0 && car.Skins.Count > 0) skins = car.Skins.Take(2).ToList();
                entries.AddRange(skins.Select(s => new RosterEntry
                {
                    Driver = s.Driver ?? $"{car.Name} ({s.Folder})",
                    Team = s.Team ?? car.Brand,
                    Number = s.Number,
                    Car = car.Name,
                    Model = car.Folder,
                    Skin = s.Folder,
                }));
            }
            if (entries.Count < 2) continue;
            var roster = new Roster
            {
                Id = RosterIds.Slugify("installed-" + group.Key),
                Name = $"My {group.Key} grid (installed cars and liveries)",
                Category = group.Key,
                Source = "Built from installed cars",
                Notes = "No real event: pick a track in Assetto Corsa or on the server.",
                Entries = entries.Take(maxCars).ToList(),
            };
            File.WriteAllText(Path.Combine(ws.RostersFolder, roster.Id + ".json"),
                JsonSerializer.Serialize(roster, RosterJson.Options), new UTF8Encoding(false));
        }
    }
}

public record GetOptions
{
    /// <summary>Extra content folders to install into as well (e.g. an AssettoServer content folder).</summary>
    public string[] ExtraTargets { get; init; } = Array.Empty<string>();
    public bool Overwrite { get; init; }
    public bool OpenBrowser { get; init; } = true;
    public int MaxOpen { get; init; } = 8;
    public int WatchMinutes { get; init; } = 30;
    public string DownloadsFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

/// <summary>
/// Gets real cars/tracks: direct archive links are downloaded and installed; other pages (overtake.gg needs your
/// login) open in the browser and archives you download are installed from the Downloads folder as they finish.
/// Official Kunos content (Steam) is only listed.
/// </summary>
public static class ContentGetter
{
    public static async Task<int> GetAsync(List<WantedItem> items, string content, GetOptions o, IProgress<string> log, CancellationToken ct = default)
    {
        var targets = new[] { content }.Concat(o.ExtraTargets).ToArray();
        List<WantedItem> Left() => items.Where(i => !i.IsKunos && !i.IsInstalled(content)).ToList();

        foreach (var k in items.Where(i => i.IsKunos)) log.Report($"Official Kunos content (base game / DLC on Steam): {k.Label}");
        var toGet = Left();
        if (toGet.Count == 0) { log.Report("Nothing to download."); return 0; }

        var opened = 0;
        foreach (var url in toGet.Select(i => i.Url).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var label = string.Join(", ", toGet.Where(i => i.Url == url).Select(i => i.Label));
            if (IsDirectArchive(url))
            {
                log.Report($"Downloading {label} ...");
                var file = await DownloadAsync(url, log, ct);
                if (file != null)
                {
                    Report(ArchiveInstaller.Install(file, targets, o.Overwrite), log);
                    File.Delete(file);
                    continue;
                }
            }
            if (!o.OpenBrowser || opened >= o.MaxOpen)
            {
                log.Report($"Get it here: {label}: {url}");
                continue;
            }
            log.Report($"Opening download page: {label}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            opened++;
        }

        if (Left().Count == 0) { log.Report("Everything is installed."); return 0; }
        if (opened == 0 || o.WatchMinutes <= 0) return Left().Count;

        log.Report($"Download the files in your browser. Watching {o.DownloadsFolder} for {o.WatchMinutes} min; archives are installed as they arrive.");
        var start = DateTime.Now.AddSeconds(-5);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, long>();
        var until = DateTime.Now.AddMinutes(o.WatchMinutes);
        while (DateTime.Now < until)
        {
            await Task.Delay(3000, ct);
            if (!Directory.Exists(o.DownloadsFolder)) continue;
            foreach (var file in Directory.EnumerateFiles(o.DownloadsFolder))
            {
                if (done.Contains(file) || !ArchiveInstaller.ArchiveExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                var info = new FileInfo(file);
                if (info.LastWriteTime < start) continue;
                // Wait until the size stops changing (download finished).
                if (!sizes.TryGetValue(file, out var last) || last != info.Length) { sizes[file] = info.Length; continue; }
                done.Add(file);
                log.Report($"Installing {info.Name} ...");
                Report(ArchiveInstaller.Install(file, targets, o.Overwrite), log);
                var left = Left();
                log.Report(left.Count == 0 ? "Everything is installed." : $"Still missing: {string.Join(", ", left.Select(i => i.Folder ?? i.Label))}");
                if (left.Count == 0) return 0;
            }
        }
        log.Report("Stopped watching the Downloads folder.");
        return Left().Count;
    }

    public static bool IsDirectArchive(string url)
    {
        var path = url.Split('?')[0].ToLowerInvariant();
        return ArchiveInstaller.ArchiveExtensions.Any(path.EndsWith);
    }

    private static async Task<string?> DownloadAsync(string url, IProgress<string> log, CancellationToken ct)
    {
        try
        {
            using var response = await ImportHttp.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType == "text/html")
            {
                log.Report($"Not a direct download (HTTP {(int)response.StatusCode}); opening it in the browser instead.");
                return null;
            }
            var file = Path.Combine(Path.GetTempPath(), "HistoricRoster-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(url.Split('?')[0]));
            await using (var fs = File.Create(file)) await response.Content.CopyToAsync(fs, ct);
            return file;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report($"Download failed: {ex.Message}");
            return null;
        }
    }

    public static void Report(ArchiveInstaller.Result r, IProgress<string> log)
    {
        foreach (var c in r.Cars) log.Report($"Installed car {c}");
        foreach (var t in r.Tracks) log.Report($"Installed track {t}");
        foreach (var s in r.Skins ?? new()) log.Report($"Installed livery {s}");
        foreach (var s in r.Skipped) log.Report($"Skipped {s}");
        if (r.Error != null) log.Report(r.Error);
    }
}

/// <summary>Runs the championship import and writes content_catalog.champs.json.</summary>
public static class ChampionshipCatalog
{
    public const string CatalogFile = "content_catalog.champs.json";

    public static List<ChampionshipImporter.Report> Import(Workspace ws, string path, IProgress<string> log)
    {
        var files = ChampionshipImporter.FindFiles(path).ToList();
        if (files.Count == 0) { log.Report($"No .champ files in {path}"); return new(); }
        var races = ws.LoadRosters();
        log.Report($"Reading {files.Count} championship(s) from {path}; {races.Count} real races to match against.");
        var (catalog, reports) = ChampionshipImporter.Import(files, ws.ContentFolder, races);
        foreach (var r in reports.Where(r => r.Year != null))
            log.Report($"{r.Name} [{r.Category ?? "?"} {r.Year}]: {r.SeatRules} seat rules, {r.DriversFound}/{r.Seats} drivers found, {r.TrackRules} track rules");
        File.WriteAllText(Path.Combine(ws.RostersFolder, CatalogFile),
            "// Written by the championship import: exact car + livery per driver and season, and the championship's\n" +
            "// track for each real race. Import again after adding championships.\n" +
            JsonSerializer.Serialize(catalog, RosterJson.Options), new UTF8Encoding(false));
        log.Report($"Wrote {catalog.Cars.Count} seat rules and {catalog.Tracks.Count} track rules.");
        return reports;
    }
}
