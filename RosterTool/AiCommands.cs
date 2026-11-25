using System.Diagnostics;
using HistoricRosterPlugin.Ai;

namespace HistoricRosterPlugin;

/// <summary>
/// RosterTool ai ...: the AI features (OpenAI / DeepSeek) on the command line. Uses the keys saved in the
/// Historic Roster program's AI settings, or the OPENAI_API_KEY / DEEPSEEK_API_KEY environment variables.
/// </summary>
internal static class AiCommands
{
    public const string HelpText = """
          ai test [--race <id>]                Check each AI service and try every AI feature on a race
          ai preview <roster id> [--as <driver>]  Pre-race briefing written by the AI
          ai search "<plain words>"            Find races from a description ("Senna at Monaco")
          ai match <roster id> [--apply]       Ask the AI which installed cars/track are the missing real ones
          ai ask "<question>"                  Ask about the program, a race or motorsport history
          ai update-db                         Fill in the database's real F1 chassis names (OpenAI and DeepSeek
                                               both asked; only names they agree on are written)
                    [--provider openai|deepseek]    use only that service (default: the order in AI settings)
        """;

    public static async Task<int> Run(Cli cli, Func<Workspace> ws, Func<Workspace, string, Roster> find)
    {
        var settings = AiSettings.LoadFromProgram();
        var sub = cli.Positional.FirstOrDefault()?.ToLowerInvariant();
        var rest = cli.Positional.Skip(1).ToList();
        var client = Client(settings, cli.Get("provider"));
        if (!client.Configured && sub != null)
        {
            Console.WriteLine(AiClient.NotConfiguredText + " (Or set OPENAI_API_KEY / DEEPSEEK_API_KEY.)");
            return 1;
        }
        var ai = new AiAssistant(client);
        switch (sub)
        {
            case "test": return await Test(settings, cli, ws, find);
            case "preview":
            {
                var w = ws();
                var roster = find(w, rest.FirstOrDefault() ?? throw new InvalidOperationException("Usage: RosterTool ai preview <roster id>"));
                await Print(ai.RacePreviewAsync(roster, cli.Get("as")), client);
                return 0;
            }
            case "search":
            {
                var w = ws();
                var rosters = w.LoadRosters();
                var r = await ai.SearchAsync(string.Join(' ', rest), rosters.Select(x => x.Category).OfType<string>().Distinct().Order().ToList());
                Console.WriteLine($"{r.Explanation}\nFilters: series {r.Category ?? "any"}, years {r.YearFrom?.ToString() ?? "…"}-{r.YearTo?.ToString() ?? "…"}, words [{string.Join(", ", r.Words)}]");
                foreach (var x in Filter(rosters, r).Take(40)) Console.WriteLine($"  {x.Id}   {x.Name}");
                return 0;
            }
            case "update-db": return await UpdateDb(settings, cli, ws);
            case "match": return await Match(ai, ws(), find, rest.FirstOrDefault(), cli.Has("apply"));
            case "ask":
            {
                var system = AiAssistant.HelpSystemPrompt(Guide(), null);
                await Print(ai.ChatAsync(system, new[] { AiMessage.User(string.Join(' ', rest)) }), client);
                return 0;
            }
            default:
                Console.WriteLine("RosterTool ai <command>\n" + HelpText);
                return sub == null ? 0 : 1;
        }
    }

    private static async Task<int> UpdateDb(AiSettings s, Cli cli, Func<Workspace> wsf)
    {
        var provider = cli.Get("provider")?.ToLowerInvariant();
        if (provider == "openai") s.DeepSeekKeyProtected = null;
        if (provider == "deepseek") s.OpenAiKeyProtected = null;
        var ws = wsf();
        var path = ws.DatabasePath;
        var db = RosterDatabase.Load(path);
        Console.WriteLine($"Database: {path} ({db.Rosters.Count} races).");
        var skipPath = Path.Combine(ws.RostersFolder, "_wiki_no_results.json");
        var skip = AppData.Load<HashSet<string>>(skipPath) ?? new HashSet<string>();
        if (cli.Has("refresh")) skip.RemoveWhere(k => k.StartsWith("ai:")); // ask every season again; keep the Wikipedia list
        var n = cli.Has("times-only") ? 0 : await AiDatabase.FillF1ChassisAsync(db, ws.RostersFolder, s, skip, new ConsoleLog(), default);
        n += await AiStartTimes.FillF1StartTimesAsync(db, ws.RostersFolder, s, skip, new ConsoleLog(), default);
        if (n > 0) db.Save(path);
        try { AppData.Save(skipPath, skip); } catch { }
        Console.WriteLine(n > 0 ? $"Saved: {n} entries updated." : "Nothing to update.");
        return 0;
    }

    private class ConsoleLog : IProgress<string> { public void Report(string value) => Console.WriteLine(value); }

    private static AiClient Client(AiSettings s, string? provider) => provider?.ToLowerInvariant() switch
    {
        "openai" => new AiClient(new[] { s.OpenAi }),
        "deepseek" => new AiClient(new[] { s.DeepSeek }),
        _ => s.CreateClient(),
    };

    private static string? Guide()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "USER_GUIDE.md"), Path.Combine(AppContext.BaseDirectory, "README.md") })
            if (File.Exists(p)) return File.ReadAllText(p);
        return null;
    }

    private static async Task<string> Print(IAsyncEnumerable<string> pieces, AiClient client, int? maxChars = null)
    {
        var all = new System.Text.StringBuilder();
        await foreach (var p in pieces)
        {
            all.Append(p);
            if (maxChars == null || all.Length <= maxChars) Console.Write(p);
        }
        if (maxChars != null && all.Length > maxChars) Console.Write(" [...]");
        Console.WriteLine();
        Console.WriteLine($"-- {client.LastUsed}");
        return all.ToString();
    }

    private static IEnumerable<Roster> Filter(IEnumerable<Roster> rosters, AiSearch r) => rosters
        .Where(x => r.Category == null || string.Equals(x.Category, r.Category, StringComparison.OrdinalIgnoreCase))
        .Where(x => r.YearFrom == null || x.Year >= r.YearFrom)
        .Where(x => r.YearTo == null || x.Year <= r.YearTo)
        .Where(x => r.Words.All(w => TextFold.Contains($"{x.Name} {x.Category} {x.Year} {x.Track?.Name} {x.Series}", w)))
        .OrderByDescending(x => x.Year).ThenBy(x => x.Name);

    private static async Task<int> Match(AiAssistant ai, Workspace ws, Func<Workspace, string, Roster> find, string? id, bool apply)
    {
        var roster = find(ws, id ?? throw new InvalidOperationException("Usage: RosterTool ai match <roster id>"));
        var resolver = ws.CreateResolver();
        var content = resolver.Resolve(roster, resolver.LoadCatalog());
        var lib = ws.Library;
        var cars = lib.Cars.Select(c => new AiCandidate(c.Folder, string.Join(" · ", new[] { c.Name, c.Brand, c.Year?.ToString(), c.Category }.Where(x => !string.IsNullOrWhiteSpace(x))))).ToList();
        var tracks = lib.Tracks.Select(t => new AiCandidate(t.Folder + (t.Layout != null ? "/" + t.Layout : ""), string.Join(" · ", new[] { t.Name, t.City, t.Country }.Where(x => !string.IsNullOrWhiteSpace(x))))).ToList();
        Console.WriteLine($"{roster.Name}: track {(content.Track?.Installed == true ? "installed" : "missing")}, {content.Cars.Count(c => !c.Installed)} of {content.Cars.Count} cars missing.");

        if (content.Track is { Installed: false } && roster.Track is { } track)
        {
            var p = await ai.PickTrackAsync(track, roster, AiAssistant.Shortlist($"{track.Name} {track.Locality}", tracks));
            Console.WriteLine($"Track {track.Name}: {p.Id ?? "none"} ({p.Confidence}) {p.Reason}");
            if (apply && p.Id != null && p.Confidence != "low")
            {
                var parts = p.Id.Split('/', 2);
                TrackChoices.Set(ws, track, parts[0], parts.Length > 1 ? parts[1] : null);
                Console.WriteLine("  applied");
            }
        }
        foreach (var car in content.Cars.Where(c => !c.Installed).GroupBy(c => (c.Entry.Car ?? c.Entry.Team ?? c.Entry.Driver).ToLowerInvariant()).Select(g => g.First()))
        {
            var real = car.Entry.Car ?? car.Entry.Team ?? car.Entry.Driver;
            var p = await ai.PickCarAsync(real, roster, car.Entry, AiAssistant.Shortlist($"{real} {car.Entry.Team} {roster.Category}", cars));
            Console.WriteLine($"Car {real}: {p.Id ?? "none"} ({p.Confidence}) {p.Reason}");
            if (apply && p.Id != null && p.Confidence != "low")
            {
                CarChoices.Set(ws, roster, car.Entry, p.Id);
                Console.WriteLine("  applied");
            }
        }
        Console.WriteLine($"-- {ai.Client.LastUsed}{(apply ? "" : "   (nothing changed; add --apply to use the confident matches)")}");
        return 0;
    }

    /// <summary>Each service on its own, then every feature once. Prints PASS/FAIL per check.</summary>
    private static async Task<int> Test(AiSettings s, Cli cli, Func<Workspace> wsf, Func<Workspace, string, Roster> find)
    {
        var failures = 0;
        async Task Check(string name, Func<Task<string>> run)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var detail = await run();
                Console.WriteLine($"PASS  {name}  ({sw.Elapsed.TotalSeconds:0.0}s)  {detail}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}  ({sw.Elapsed.TotalSeconds:0.0}s)  {ex.Message}");
            }
        }

        foreach (var ep in new[] { s.OpenAi, s.DeepSeek })
        {
            if (!ep.Usable) { Console.WriteLine($"SKIP  {ep.Name}: no key"); continue; }
            await Check($"{ep.Name} key + model {ep.Model}", async () => "replied \"" + (await AiClient.TestAsync(ep)).Trim() + "\"");
            await Check($"{ep.Name} models list", async () => $"{(await AiClient.ListModelsAsync(ep)).Count} models");
            await Check($"{ep.Name} streaming", async () =>
            {
                var c = new AiClient(new[] { ep });
                var n = 0; var text = "";
                await foreach (var p in c.StreamAsync(new[] { AiMessage.User("Count from 1 to 5, separated by spaces.") })) { n++; text += p; }
                return $"{n} pieces: \"{text.Trim()}\"";
            });
            await Check($"{ep.Name} JSON mode", async () =>
            {
                var o = await new AiClient(new[] { ep }).CompleteJsonAsync(new[] { AiMessage.User("Answer in JSON: {\"ok\": true, \"capital_of_france\": \"...\"}") });
                return o.ToJsonString();
            });
        }

        var client = Client(s, cli.Get("provider"));
        if (!client.Configured) return 1;
        var ai = new AiAssistant(client);
        var ws = wsf();
        var rosters = ws.LoadRosters();
        var race = cli.Get("race") is { } id ? find(ws, id)
            : rosters.Where(r => r.Category == "F1" && r.Year is >= 2000 and <= 2010 && r.Entries.Count >= 18 && r.Entries.Any(e => e.GridPosition != null))
                .OrderByDescending(r => r.Year).FirstOrDefault() ?? rosters.First(r => r.Entries.Count > 0);
        Console.WriteLine($"\nFeatures, on {race.Name} ({race.Id}), via {string.Join(" then ", client.Endpoints.Select(e => e.Name))}:");

        await Check("race preview", async () =>
        {
            var text = ""; await foreach (var p in ai.RacePreviewAsync(race, race.Entries.FirstOrDefault()?.Driver)) text += p;
            if (text.Length < 300) throw new Exception("too short: " + text);
            return $"{text.Length} chars, by {client.LastUsed}: \"{OneLine(text, 160)}\"";
        });
        await Check("race report", async () =>
        {
            var rows = race.Entries.Where(e => e.Position != null).OrderBy(e => e.Position).Select((e, i) =>
                new ResultRow(e.Driver, e.Team, e.Position, e.GridPosition, i == 2 ? 1 : i < 2 ? i + 2 : i + 1, i == 2)).ToList();
            var cmp = new RaceComparison(new LastRace { RosterId = race.Id, RosterName = race.Name, PlayerSeat = rows.ElementAtOrDefault(2)?.Driver ?? "" }, DateTime.UtcNow, rows);
            var text = ""; await foreach (var p in ai.RaceReportAsync(race, cmp)) text += p;
            if (text.Length < 200) throw new Exception("too short: " + text);
            return $"{text.Length} chars: \"{OneLine(text, 160)}\"";
        });
        await Check("search", async () =>
        {
            var r = await ai.SearchAsync("Michael Schumacher's wins at Monza in the Ferrari years", rosters.Select(x => x.Category).OfType<string>().Distinct().Order().ToList());
            var hits = Filter(rosters, r).ToList();
            if (hits.Count == 0) throw new Exception($"no races for {r.Category} {r.YearFrom}-{r.YearTo} [{string.Join(",", r.Words)}]");
            return $"{r.Category} {r.YearFrom}-{r.YearTo} [{string.Join(",", r.Words)}] -> {hits.Count} races, e.g. {hits[0].Name}";
        });
        await Check("car pick (real car present)", async () =>
        {
            var cands = new[]
            {
                new AiCandidate("rss_formula_hybrid_2021", "Formula Hybrid 2021 · RSS · 2021 · F1"),
                new AiCandidate("ks_ferrari_f2004", "Ferrari F2004 · Ferrari · 2004 · F1"),
                new AiCandidate("ks_ferrari_sf15t", "Ferrari SF15-T · Ferrari · 2015 · F1"),
                new AiCandidate("acf_mclaren_mp4_19", "McLaren MP4-19 · McLaren · 2004 · F1"),
            };
            var r = new Roster { Name = "2004 Italian Grand Prix", Category = "F1", Year = 2004 };
            var p = await ai.PickCarAsync("Ferrari F2004", r, new RosterEntry { Driver = "Michael Schumacher", Team = "Ferrari" }, cands);
            if (p.Id != "ks_ferrari_f2004") throw new Exception($"picked {p.Id ?? "none"}: {p.Reason}");
            return $"{p.Id} ({p.Confidence})";
        });
        await Check("car pick (real car absent -> none)", async () =>
        {
            var cands = new[] { new AiCandidate("ks_ferrari_sf15t", "Ferrari SF15-T · Ferrari · 2015 · F1"), new AiCandidate("ks_bmw_m3_e30", "BMW M3 E30 · BMW · 1987") };
            var r = new Roster { Name = "2004 Italian Grand Prix", Category = "F1", Year = 2004 };
            var p = await ai.PickCarAsync("Ferrari F2004", r, null, cands);
            if (p.Id != null && p.Confidence != "low") throw new Exception($"wrongly picked {p.Id}: {p.Reason}");
            return $"{p.Id ?? "none"} ({p.Confidence}): {OneLine(p.Reason, 100)}";
        });
        await Check("track pick", async () =>
        {
            var cands = new[]
            {
                new AiCandidate("ks_monza66/full", "Monza 1966 · Monza · Italy"),
                new AiCandidate("monza/", "Monza · Monza · Italy"),
                new AiCandidate("ks_vallelunga/extended_circuit", "Vallelunga · Campagnano · Italy"),
            }.Select(c => c with { Id = c.Id.TrimEnd('/') }).ToList();
            var r = new Roster { Name = "2004 Italian Grand Prix", Category = "F1", Year = 2004 };
            var p = await ai.PickTrackAsync(new RosterTrack { Name = "Autodromo Nazionale di Monza", Locality = "Monza" }, r, cands);
            if (p.Id != "monza") throw new Exception($"picked {p.Id ?? "none"}: {p.Reason}");
            return $"{p.Id} ({p.Confidence})";
        });
        await Check("team colours", async () =>
        {
            var entries = race.Entries.Take(10).ToList();
            var hues = await ai.TeamHuesAsync(race, entries);
            var known = hues.Count(h => h.Value != null);
            if (hues.Count == 0) throw new Exception("no answer");
            return $"{hues.Count} drivers, {known} with a hue: " + string.Join(", ", hues.Take(4).Select(h => $"{h.Key}={(h.Value is { } v ? v.ToString("0") : "neutral")}"));
        });
        await Check("help chat", async () =>
        {
            var text = ""; await foreach (var p in ai.ChatAsync(AiAssistant.HelpSystemPrompt(Guide(), AiAssistant.RaceFacts(race, 10)),
                               new[] { AiMessage.User("In one or two sentences: how do I watch an AI-only race in this program?") })) text += p;
            return $"\"{OneLine(text, 200)}\"";
        });

        Console.WriteLine(failures == 0 ? "\nAll AI checks passed." : $"\n{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static string OneLine(string s, int max)
    {
        var t = string.Join(' ', s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
