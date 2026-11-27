using System.Globalization;
using System.Text;
using System.Text.Json;
using HistoricRosterPlugin;

// RosterTool - command-line companion for HistoricRosterPlugin. The desktop program "Historic Roster" does the same
// with a window; both use the logic in Core.

Console.OutputEncoding = Encoding.UTF8;
var cli = Cli.Parse(args);
var toolDir = AppContext.BaseDirectory;
// Writes progress immediately and in order (Progress<T> would post to the thread pool in a console app).
IProgress<string> Log = new SyncLog();

try
{
    return cli.Command switch
    {
        "find" => Find(),
        "build-db" => await BuildDb(),
        "scan" => Scan(),
        "install" => await Install(),
        "get" => await Get(),
        "install-archive" => InstallArchive(),
        "race" => Race(),
        "result" => Result(),
        "champ" => Champ(),
        "install-app" => InstallApp(),
        "wiki" => await Wiki(),
        "wiki-season" => await WikiSeason(),
        "imsa" => await Imsa(),
        "ai" => await AiCommands.Run(cli, Ws, FindRoster),
        _ => Help(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

// ------------------------------------------------------------------ helpers

// Roster folder: --rosters, else <server>\rosters for --server, else the tool's own rosters folder.
Workspace Ws()
{
    var rosters = cli.Get("rosters")
                  ?? (cli.Get("server") is { } server ? Path.Combine(server, "rosters") : Path.Combine(toolDir, "rosters"));
    var ws = Workspace.Open(cli.Get("content"), rosters, Path.Combine(toolDir, "rosters"));
    if (ws.LinkedTracks.Count > 0)
        Console.WriteLine($"Linked {ws.LinkedTracks.Count} track(s) from another Assetto Corsa install (nothing copied): {string.Join(", ", ws.LinkedTracks)}.");
    if (ws.LinkedCars.Count > 0)
        Console.WriteLine($"Linked {ws.LinkedCars.Count} car(s) from another Assetto Corsa install (nothing copied): {string.Join(", ", ws.LinkedCars)}.");
    return ws;
}

Roster FindRoster(Workspace ws, string idOrFile)
    => File.Exists(idOrFile)
        ? JsonSerializer.Deserialize<Roster>(File.ReadAllText(idOrFile), RosterJson.Options)!
        : ws.LoadRosters().FirstOrDefault(r => r.Id == idOrFile.ToLowerInvariant())
          ?? throw new InvalidOperationException($"Roster '{idOrFile}' not found in {ws.RostersFolder} or its database.");

GetOptions GetOpts() => new()
{
    ExtraTargets = cli.Get("server-content") is { } sc ? new[] { Path.GetFullPath(sc) } : Array.Empty<string>(),
    Overwrite = cli.Has("overwrite"),
    OpenBrowser = !cli.Has("no-browser"),
    MaxOpen = cli.GetInt("max-open") ?? 8,
    WatchMinutes = cli.GetInt("watch") ?? 30,
    DownloadsFolder = cli.Get("downloads") ?? new GetOptions().DownloadsFolder,
};

int Help()
{
    Console.WriteLine("""
        RosterTool - companion for HistoricRosterPlugin (the "Historic Roster" program does the same with a window)

          find                                 Show the Assetto Corsa install and installed content
          build-db  [--from 1950] [--to 2026]  Build historic_rosters_db.json with real grids:
                    [--series f1,nascar,endurance,nascar-classic,imsa,wec,imsa-classic,indycar,cart,dtm,supercars]
                                                    (default: all)
                    [--nascar-series 1,2,3] [--refresh] [--server <AssettoServer folder> | --rosters <folder>]
                    [--no-ai]                       skip the AI step (real F1 chassis names, OpenAI + DeepSeek)
          scan                                 Match installed cars to real cars; rosters from your liveries
          champ [file or folder]               Import Content Manager championships (.champ): exact car,
                                               livery and track per driver/season (default: Documents\Assetto Corsa\champs)
          install   <roster id or .json file>  Get the real cars/track a race is missing:
                    [--watch 30] [--no-browser]     direct links are downloaded and installed; other pages open in
                    [--server-content <folder>]     your browser and archives you download are installed
          get <items.json>                     Same as install, for a list sent by the in-game app
          race <roster id> [--as <driver> | --grid <position>]  Offline race vs AI: writes Documents\Assetto Corsa\cfg\race.ini
                    [--name "Your Name"]            your name replaces that driver (default: the name in race.ini)
                    [--watch]                       AI drives every car (needs CSP + install-app); --as/--grid = camera start
                    [--laps n] [--ai-level 95] [--safety-car] [--weekend] [--launch]
                    [--cm]                          also save it as a Content Manager Quick Drive preset
                    [--distance 25|50|100]          share of the real race distance
                    [--no-standins] [--flat-ai]     leave missing cars out / same AI level for everyone
                    [--time 20:30] [--no-real-conditions]  real start time, date and weather are used unless set
                    [--no-simcast]                  don't give SimCast's commentary the real race (it does when
                                                    SimCast is installed: %APPDATA%\SimCast\race_context.json)
          result                               Your last race next to the real result (reads AC's race_out.json)
          install-archive <file.zip|.7z|.rar>  Install car/track folders from an archive into Assetto Corsa
          install-app                          Install the "Historic Roster" CSP app (apps sidebar in game)
          wiki <article title>                 Check how a Wikipedia race article is read
          wiki-season <series> <year> [--import n]  Race articles of a season article (series: wec, imsa-classic,
                                               indycar, cart, dtm, supercars); --import reads the first n
          imsa <year> [--mpc] [--import n]     IMSA races in IMSA's timing archive (--mpc: Michelin Pilot Challenge)
        """ + "\n" + AiCommands.HelpText + """

        Common: --content "<assettocorsa>\content" (found through Steam when omitted)
        """);
    return cli.Command is null or "help" or "--help" ? 0 : 1;
}

// ------------------------------------------------------------------ commands

int Find()
{
    Console.WriteLine("Steam libraries: " + string.Join(", ", AcInstallLocator.SteamLibraries()));
    foreach (var c in AcInstallLocator.FindAll()) Console.WriteLine($"Found Assetto Corsa: {c.Root} ({c.Source})");
    var ws = Ws();
    var lib = ws.Library;
    Console.WriteLine($"Assetto Corsa: {ws.AcRoot}");
    Console.WriteLine($"Content folder: {ws.ContentFolder}");
    Console.WriteLine($"Cars: {lib.Cars.Count} ({lib.Cars.Count(x => !x.Kunos)} mods)   Track layouts: {lib.Tracks.Count}");
    Console.WriteLine($"CSP: {(Directory.Exists(Path.Combine(ws.AcRoot, "extension")) ? "installed" : "not found")}");
    var apps = CompanionApps.Detect(ws.AcRoot);
    Console.WriteLine($"Historic Roster in-game app: {(apps.HistoricRosterApp ? "installed" : "no")}   " +
                      $"Safety Car {apps.SafetyCarModel}: {(apps.SafetyCarInstalled ? "installed" : "not installed")}");
    foreach (var g in lib.Cars.Where(x => x.Category != null).GroupBy(x => x.Category!).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {g.Key,-9} {g.Count(),3} cars: {string.Join(", ", g.Take(6).Select(x => x.Folder))}{(g.Count() > 6 ? ", ..." : "")}");
        if (cli.Has("all"))
            foreach (var c in g.OrderBy(c => c.Year ?? 0))
                Console.WriteLine($"      {c.Year?.ToString() ?? "----"}  {c.Folder}  ({c.Name}, {c.Skins.Count} liveries)");
    }
    return 0;
}

async Task<int> BuildDb()
{
    var rosters = cli.Get("rosters") ?? (cli.Get("server") is { } server ? Path.Combine(server, "rosters") : Path.Combine(toolDir, "rosters"));
    Directory.CreateDirectory(rosters);
    await DatabaseBuilder.BuildAsync(Path.Combine(rosters, RosterDatabase.DefaultFileName), new DbBuildOptions
    {
        From = cli.GetInt("from") ?? 1950,
        To = cli.GetInt("to") ?? DateTime.UtcNow.Year,
        Series = cli.Get("series")?.Split(',', StringSplitOptions.TrimEntries) ?? DbBuildOptions.AllSeries,
        NascarSeries = (cli.Get("nascar-series") ?? "1").Split(',').Select(int.Parse).ToArray(),
        Refresh = cli.Has("refresh"),
        Ai = cli.Has("no-ai") ? null : HistoricRosterPlugin.Ai.AiSettings.LoadFromProgram(),
    }, Log);
    return 0;
}

int Scan()
{
    var ws = Ws();
    LibraryScanner.Scan(ws, Log, cli.GetInt("max-cars") ?? 40);

    // Which real events can be recreated best with what is installed.
    var resolver = ws.CreateResolver();
    var catalog = resolver.LoadCatalog();
    var ranked = ws.LoadRosters()
        .Where(r => !r.Id.StartsWith("installed-") && r.Track != null)
        .Select(r => { var c = resolver.Resolve(r, catalog); return (Roster: r, Exact: c.Cars.Count(x => x.Installed), Total: c.Cars.Count, Track: c.Track?.Installed == true); })
        .Where(x => x.Total > 0)
        .OrderByDescending(x => (x.Track ? 0.25 : 0) + (double)x.Exact / x.Total).ThenByDescending(x => x.Exact)
        .Take(cli.GetInt("top") ?? 15).ToList();
    var report = new StringBuilder("Events you can recreate best with your installed content (real cars installed / grid, track):\n");
    foreach (var x in ranked)
        report.AppendLine($"  {x.Roster.Id,-48} {x.Exact,3}/{x.Total,-3} cars  track {(x.Track ? "installed" : "missing")}   {x.Roster.Name}");
    Console.Write(report);
    File.WriteAllText(Path.Combine(ws.RostersFolder, "_library_report.txt"), report.ToString());
    return 0;
}

int Champ()
{
    var ws = Ws();
    ChampionshipCatalog.Import(ws, cli.Positional.FirstOrDefault() ?? ws.ChampsFolder, Log);
    return 0;
}

async Task<int> Install()
{
    var ws = Ws();
    var roster = FindRoster(ws, cli.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Usage: RosterTool install <roster id or file>"));
    var items = Workspace.MissingItems(ws.CreateResolver().Resolve(roster));
    Console.WriteLine($"{roster.Name}: {items.Count} real car/track item(s) not installed.");
    await ContentGetter.GetAsync(items, ws.ContentFolder, GetOpts(), Log);
    return 0;
}

async Task<int> Get()
{
    var file = cli.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Usage: RosterTool get <items.json>");
    var items = JsonSerializer.Deserialize<List<WantedItem>>(File.ReadAllText(file), RosterJson.Options) ?? new();
    var content = Ws().ContentFolder;
    items = items.Where(i => !i.IsInstalled(content)).ToList();
    Console.WriteLine($"{items.Count} real car/track item(s) not installed on this PC.");
    await ContentGetter.GetAsync(items, content, GetOpts(), Log);
    return 0;
}

int InstallArchive()
{
    var file = cli.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Usage: RosterTool install-archive <file>");
    var content = Ws().ContentFolder;
    var targets = new[] { content }.Concat(GetOpts().ExtraTargets).ToArray();
    var r = ArchiveInstaller.Install(Path.GetFullPath(file), targets, cli.Has("overwrite"));
    ContentGetter.Report(r, Log);
    return r.Error == null ? 0 : 1;
}

int Race()
{
    var ws = Ws();
    var roster = FindRoster(ws, cli.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Usage: RosterTool race <roster id>"));
    var content = ws.CreateResolver().Resolve(roster);
    if (content.Track is not { Installed: true })
    {
        Console.WriteLine($"The real track isn't installed: {content.Track?.Label ?? "unknown"} -> {content.Track?.DownloadUrl}");
        return 1;
    }
    var apps = CompanionApps.Detect(ws.AcRoot);
    var safety = cli.Has("safety-car") && apps.SafetyCarInstalled;
    // The real grid fills the pit boxes first; the Safety Car never takes a real car's place.
    var max = cli.GetInt("max-cars") ?? content.Track.PitBoxes ?? 40;
    var setup = RaceSetup.Create(content, max, ws.Library, standIns: !cli.Has("no-standins"));
    setup.RealSkill = !cli.Has("flat-ai");

    if (cli.Get("as") is { } who)
        setup.SetPlayer(setup.Grid.FirstOrDefault(s => TextFold.Contains(s.Driver, who))
                        ?? throw new InvalidOperationException($"No driver matching '{who}' among the installed cars."));
    if (cli.GetInt("grid") is { } gridPos)
        setup.SetPlayer(setup.Grid.FirstOrDefault(s => !s.IsSafetyCar && s.GridPosition == gridPos)
                        ?? throw new InvalidOperationException($"No car starts from P{gridPos} (the grid has {setup.Grid.Count} installed cars)."));
    if (cli.Get("name") is { } myName) setup.PlayerName = myName;
    if (cli.Has("watch"))
    {
        if (!apps.CanWatch)
            throw new InvalidOperationException("Watching an AI race needs Custom Shaders Patch and the Historic Roster in-game app (RosterTool install-app).");
        setup.Watch = true;
    }
    if (cli.GetInt("laps") is { } laps) setup.Laps = laps;
    if (cli.GetInt("ai-level") is { } ai) setup.AiLevel = ai;
    if (cli.GetInt("distance") is { } pct) setup.Laps = RaceSetup.LapsFor(roster, pct);
    setup.ApplySkill();
    if (cli.Has("weekend")) setup.Weekend = RaceWeekend.QualifyingAndRace;
    if (safety && apps.TryAddSafetyCar(setup, content.Track.PitBoxes) is { } scNote) Console.WriteLine(scNote);

    // The real race day: start time (night races too), date, sun, time zone and weather. Same in a watched race.
    var raceIni = cli.Get("race-ini") ?? ws.RaceIniPath;
    RealRaceTime.ApplyPlace(setup, ws.Library);
    Conditions? cond = null;
    if (!cli.Has("no-real-conditions"))
    {
        try { cond = RealConditions.GetAsync(roster, default, RealRaceTime.Position(roster, content.Track, ws.Library)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("Real conditions: " + ex.Message); }
        if (cond != null)
        {
            RealRaceTime.Apply(setup, cond, ws.Library, ws.AcRoot, raceIni);
            var weatherDir = Path.Combine(ws.ContentFolder, "weather");
            var folders = Directory.Exists(weatherDir) ? Directory.GetDirectories(weatherDir).Select(Path.GetFileName).Cast<string>().ToList() : new List<string>();
            var (weather, note) = RealConditions.PickWeather(cond, folders);
            setup.Weather = weather;
            Console.WriteLine($"Real race: {cond.Describe()}. Weather {weather} (real weather type {setup.WeatherFxType} via {setup.WeatherController}).{(note != null ? " " + note : "")}");
        }
        else
        {
            if (RealRaceTime.UsualStart(roster) is { } usual) setup.TimeOfDay = usual;
            Console.WriteLine($"WARNING: couldn't get the real race-day weather; start {setup.TimeOfDay:hh\\:mm} local, weather {setup.Weather}.");
        }
    }
    if (cli.Get("time") is { } hhmm)
        setup.TimeOfDay = TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t
            : throw new InvalidOperationException("--time takes a local start time like 20:30.");
    if (setup.NeedsCspForTime && !apps.Csp)
        Console.WriteLine($"WARNING: a {setup.TimeOfDay:hh\\:mm} start needs Custom Shaders Patch; plain AC only shows 08:00-18:00.");

    var path = RaceIniWriter.Write(setup, raceIni);
    // SimCast's race context: written when SimCast is installed, else (--no-simcast) the last race's removed.
    if (SimCastContext.Update(!cli.Has("no-simcast"), setup, cond,
            typeof(Program).Assembly.GetName().Version?.ToString(3), Console.WriteLine) is { } ctx)
        Console.WriteLine($"SimCast race context written to {ctx}.");
    Console.WriteLine($"Wrote {path}:{roster.Name} at {setup.Track.Folder}{(setup.Track.Layout != null ? "/" + setup.Track.Layout : "")}, " +
                      $"{setup.Grid.Count} cars, {setup.Laps} laps, start {setup.TimeOfDay:hh\\:mm} local{(setup.RaceDate != null ? $" on {setup.RaceDate:d MMM yyyy}" : "")}.");
    if (setup.Watch)
        Console.WriteLine($"AI race to watch: the AI drives every car. The camera starts on {setup.Player.Driver} (P{setup.Player.GridPosition}); " +
                          "use the Historic Roster app window in game to follow other cars.");
    else
        Console.WriteLine($"{(string.IsNullOrWhiteSpace(setup.PlayerName) ? "You" : setup.PlayerName)} replace{(string.IsNullOrWhiteSpace(setup.PlayerName) ? "" : "s")} " +
                          $"{setup.Player.Driver} in the {setup.Player.Car.Entry.Car} ({setup.Player.Car.Model}) from P{setup.Player.GridPosition}.");
    foreach (var s in setup.Grid.Where(s => s.IsStandIn))
        Console.WriteLine($"  stand-in: {s.Car.Model} for {s.Driver}'s {s.RealCar!.Entry.Car} (get it: {s.RealCar.DownloadUrl})");
    if (setup.LeftOut.Count > 0) Console.WriteLine($"{setup.LeftOut.Count} real car(s) left out (RosterTool install {roster.Id}).");
    LastRace.Record(setup, null).Save();
    if (cli.Has("safety-car") && !apps.SafetyCarInstalled) Console.WriteLine($"Safety Car {apps.SafetyCarModel} isn't installed; not added.");
    if (setup.Grid.Any(s => s.IsSafetyCar) && !apps.HistoricRosterApp)
        Console.WriteLine("The Safety Car needs the Historic Roster in-game app to keep it in the pits: run RosterTool install-app.");
    if (cli.Has("cm") || cli.Get("cm-presets") != null)
    {
        var presets = cli.Get("cm-presets") ?? CmPresetWriter.PresetsFolder()
                      ?? throw new InvalidOperationException("Content Manager's Presets folder wasn't found; pass --cm-presets <folder>.");
        var r = CmPresetWriter.Write(setup, presets, roster.Name);
        Console.WriteLine($"Content Manager: Quick Drive preset {r.QuickDrivePreset}");
        Console.WriteLine($"                 Race Grids preset  {r.RaceGridPreset}");
    }
    if (cli.Has("launch")) Console.WriteLine("Starting Assetto Corsa " + AcLauncher.Start(ws.AcRoot, path) + ".");
    return 0;
}

int Result()
{
    var last = LastRace.Load() ?? throw new InvalidOperationException("No race has been written yet (RosterTool race or the Historic Roster program).");
    var roster = FindRoster(Ws(), last.RosterId);
    var cmp = RaceOutReader.Compare(last, roster, includeHandled: true);
    if (cmp == null)
    {
        Console.WriteLine($"No result for {last.RosterName} yet ({RaceOutReader.FilePath}): AC writes it when the race session ends.");
        return 1;
    }
    Console.WriteLine(cmp.Headline);
    // Multi-class race: class, place in class (yours and real), and +/- within the class.
    var mc = cmp.IsMultiClass;
    Console.WriteLine(mc ? "You   Class        In cl Real  Real cl Grid  +/-  Driver" : "You   Real  Grid  +/-  Driver");
    foreach (var r in cmp.Rows)
        Console.WriteLine($"{(r.YourRacePosition is { } p ? "P" + p : "-"),-5} " +
                          (mc ? $"{r.Class ?? "",-12} {(r.YourClassPosition is { } yc ? "P" + yc : "-"),-5} " : "") +
                          $"{(r.RealPosition is { } rp ? "P" + rp : "DNF"),-5} " +
                          (mc ? $"{(r.RealClassPosition is { } rc ? "P" + rc : "DNF"),-7} " : "") +
                          $"{(r.RealGrid is { } g ? "P" + g : ""),-5} {(r.Delta is { } d ? (d > 0 ? "+" : "") + d : ""),-4} {r.Driver}{(r.IsPlayer ? "  <- you" : "")}");
    return 0;
}

int InstallApp()
{
    var root = cli.Get("ac") ?? Ws().AcRoot;
    var source = Path.Combine(toolDir, "CspApp", "HistoricRoster");
    var target = Path.Combine(root, "apps", "lua", "HistoricRoster");
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var dest = Path.Combine(target, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
    }
    // A copy of this tool, for the app's "Download and install all missing" button.
    var toolTarget = Path.Combine(target, "tool");
    foreach (var file in Directory.EnumerateFiles(toolDir, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(toolDir, file);
        if (rel.StartsWith("CspApp") || (rel.StartsWith("rosters") && !rel.EndsWith("content_catalog.json"))) continue;
        var dest = Path.Combine(toolTarget, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
    }
    Console.WriteLine($"Installed the Historic Roster app (with RosterTool) to {target}.");
    return 0;
}

async Task<int> Wiki()
{
    var html = await WikipediaImporter.FetchHtmlAsync(string.Join(' ', cli.Positional));
    if (html == null) { Console.WriteLine("Article not found."); return 1; }
    foreach (var line in WikipediaImporter.DescribeTables(html)) Console.WriteLine(line);
    var entries = WikipediaImporter.ParseResults(html, null);
    Console.WriteLine($"Parsed {entries.Count} entries");
    foreach (var e in entries.Take(5)) Console.WriteLine($"  {e.Position} [{e.Class}] #{e.Number} {e.Driver} | {e.Team} | {e.Car} | {e.Laps}");
    return 0;
}

async Task<int> WikiSeason()
{
    var series = WikiSeasonImporter.Series.FirstOrDefault(s => s.Key == cli.Positional.FirstOrDefault())
                 ?? throw new InvalidOperationException("Usage: RosterTool wiki-season <" + string.Join("|", WikiSeasonImporter.Series.Select(s => s.Key)) + "> <year>");
    var year = int.Parse(cli.Positional.ElementAtOrDefault(1) ?? throw new InvalidOperationException("Give a year."));
    var importer = new WikiSeasonImporter();
    if (await importer.SeasonAsync(series, year) is not { } season) { Console.WriteLine("No season article found."); return 1; }
    var links = WikiSeasonImporter.RaceLinks(season.Html, year, season.Title.Contains('\u2013'));
    Console.WriteLine($"{season.Title}: {links.Count} linked articles that may be races");
    foreach (var title in links.Take(cli.GetInt("import") ?? 0))
    {
        var day = await importer.ImportRaceAsync(series, title);
        if (day.Count == 0) Console.WriteLine($"  -  {title}: not a race of this championship / no results");
        foreach (var r in day)
        {
        Console.WriteLine(r == null ? ""
            : $"  OK {r.Name}  [{r.Category}, {r.Series}]  {r.Date}  track \"{r.Track?.Name}\" ({r.Track?.Locality})  {r.Entries.Count} cars, " +
              $"{r.Entries.Count(e => e.GridPosition != null)} grid, classes {string.Join(",", r.Entries.Select(e => e.Class).Distinct())}");
        foreach (var e in r.Entries.OrderBy(e => e.Position ?? 999).Take(2)) Console.WriteLine($"       P{e.Position} G{e.GridPosition} [{e.Class}] #{e.Number} {e.Driver} | {e.Team} | {e.Car} | {e.Laps}");
        }
        await Task.Delay(300);
    }
    foreach (var title in links.Skip(cli.GetInt("import") ?? 0)) Console.WriteLine("  " + title);
    return 0;
}

async Task<int> Imsa()
{
    var year = int.Parse(cli.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Usage: RosterTool imsa <year>"));
    var importer = new ImsaImporter();
    var races = await importer.ListSeasonAsync(year, cli.Has("mpc") ? ImsaImporter.PilotChallenge : ImsaImporter.WeatherTech);
    Console.WriteLine($"{races.Count} races");
    var n = cli.GetInt("import") ?? 0;
    foreach (var race in races)
    {
        Console.WriteLine($"  {race.LocalStart:yyyy-MM-dd HH:mm}  {race.Event} / {race.Session}");
        if (n-- <= 0) continue;
        var r = await importer.ImportAsync(race);
        if (r == null) { Console.WriteLine("     (no results)"); continue; }
        Console.WriteLine($"     {r.Id}: {r.Name} [{r.Category}] track \"{r.Track?.Name}\" {r.Entries.Count} cars, {r.Entries.Count(e => e.GridPosition != null)} grid, " +
                          $"classes {string.Join(",", RaceClasses.Ordered(r.Entries))}");
        foreach (var e in r.Entries.OrderBy(e => e.GridPosition ?? 999).Take(3).Concat(r.Entries.Where(e => e.Position == null).Take(2)))
            Console.WriteLine($"       P{e.Position} G{e.GridPosition} [{e.Class}] #{e.Number} {e.Driver} | {e.Team} | {e.Car} | {e.Laps} | {e.Status}");
    }
    return 0;
}

class SyncLog : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}

record Cli(string? Command, List<string> Positional, Dictionary<string, string?> Options)
{
    public static Cli Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var key = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                options[key] = value;
            }
            else positional.Add(args[i]);
        }
        return new Cli(args.Length > 0 ? args[0].ToLowerInvariant() : null, positional, options);
    }

    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => Options.ContainsKey(key);
    public int? GetInt(string key) => int.TryParse(Get(key), out var v) ? v : null;
}
