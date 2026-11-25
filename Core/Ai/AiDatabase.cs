using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin.Ai;

/// <summary>
/// The AI's part in updating the race database: the real F1 chassis of every entry ("Ferrari 312T3" instead of the
/// API's "Ferrari 1978"). OpenAI and DeepSeek are asked separately and a chassis is only written where both name the
/// same car, so one model's mistake never gets in. With only one service set up its answers are used as they are.
/// Answers are kept in _ai_f1_chassis.json, so a refresh of the F1 results puts them back without asking again.
/// </summary>
public static class AiDatabase
{
    public const string CacheFile = "_ai_f1_chassis.json";
    private const int Parallel = 4;

    /// <summary>Cache: year -> "round|team|driver" (folded) -> chassis.</summary>
    private class ChassisCache
    {
        public Dictionary<string, Dictionary<string, string>> Seasons { get; set; } = new();
    }

    /// <summary>True when the entry still has the F1 importer's "Team Year" stand-in name.</summary>
    public static bool IsPlaceholder(Roster r, RosterEntry e)
        => r.Year != null && (e.Car == null || string.Equals(e.Car, $"{e.Team} {r.Year}", StringComparison.OrdinalIgnoreCase));

    private static int? Round(Roster r)
        => r.Source != null && Regex.Match(r.Source, @"ergast/f1/\d{4}/(\d+)/") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    private static string Key(int round, string? team, string driver) => $"{round}|{TextFold.Fold(team)}|{TextFold.Fold(driver)}";

    /// <summary>
    /// Fills in real chassis names for F1 entries that still have the stand-in name. <paramref name="skip"/> holds the
    /// seasons already asked ("ai:f1-chassis-1978"); finished seasons are added so they aren't asked again.
    /// Returns the number of entries changed.
    /// </summary>
    public static async Task<int> FillF1ChassisAsync(RosterDatabase db, string rostersFolder, AiSettings settings, HashSet<string> skip,
        IProgress<string> log, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(rostersFolder, CacheFile);
        var cache = AppData.Load<ChassisCache>(cachePath) ?? new ChassisCache();
        var changed = 0;

        var seasons = db.Rosters
            .Where(r => r.Category == "F1" && r.Year != null && Round(r) != null && r.Entries.Any(e => IsPlaceholder(r, e)))
            .GroupBy(r => r.Year!.Value).OrderBy(g => g.Key).ToList();
        if (seasons.Count == 0) return 0;

        // Answers kept from earlier runs first (after a --refresh the importer wrote the stand-in names again).
        foreach (var season in seasons)
            if (cache.Seasons.TryGetValue(season.Key.ToString(), out var known))
                changed += Apply(season, known);

        var endpoints = new[] { settings.OpenAi, settings.DeepSeek }.Where(e => e.Usable).ToList();
        if (endpoints.Count == 0) return changed;
        var thisYear = DateTime.UtcNow.Year;
        var toAsk = seasons
            .Where(s => s.Any(r => r.Entries.Any(e => IsPlaceholder(r, e))))
            .Where(s => s.Key == thisYear || !skip.Contains("ai:f1-chassis-" + s.Key))
            .ToList();
        if (toAsk.Count == 0) return changed;

        log.Report($"AI: real F1 chassis for {toAsk.Count} season(s), asking {string.Join(" and ", endpoints.Select(e => $"{e.Name} ({e.Model})"))}" +
                   (endpoints.Count > 1 ? "; a name is used when both give it or one confirms the other's." : "."));

        var agreedTotal = 0;
        var disagreed = 0;
        var failed = 0;
        using var gate = new SemaphoreSlim(Parallel);
        var lockObj = new object();
        await Task.WhenAll(toAsk.Select(async season =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var prompt = SeasonPrompt(season.Key, season.ToList());
                var answers = await Task.WhenAll(endpoints.Select(async ep =>
                {
                    try
                    {
                        try { return Parse(await new AiClient(new[] { ep }).CompleteJsonAsync(prompt, ct)); }
                        catch (AiException) when (ep.Provider == AiProvider.OpenAI)
                        {
                            // From memory OpenAI sometimes won't answer and asks to look things up: let it.
                            lock (lockObj) log.Report($"  F1 {season.Key}: {ep.Name} looks it up on the web ...");
                            return Parse(await WebJsonAsync(ep, prompt, ct));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lock (lockObj) log.Report($"  F1 {season.Key}: {ex.Message}");
                        return null;
                    }
                }));
                // All failed: asked again next update. One failed: the other's answer still goes to it for review.
                if (answers.All(a => a == null)) { Interlocked.Increment(ref failed); return; }
                var partial = answers.Any(a => a == null);
                for (var i = 0; i < answers.Length; i++) answers[i] ??= new Dictionary<(string, string, int), string>();

                // Same name from both: used. Otherwise each AI reviews the other's claims, and a claim the
                // reviewer confirms is used (unless both confirm different cars).
                var agreed = new Dictionary<string, string>();
                var open = new List<(Roster Race, int Round, RosterEntry Entry, string?[] Picks)>();
                foreach (var r in season)
                foreach (var e in r.Entries.Where(e => IsPlaceholder(r, e)))
                {
                    var round = Round(r)!.Value;
                    var picks = answers.Select(a => Lookup(a!, round, e)).ToArray();
                    if (picks.All(p => p != null) && picks.Skip(1).All(p => SameChassis(p!, picks[0]!, e.Team)))
                        agreed[Key(round, e.Team, e.Driver)] = Clean(picks[0]!);
                    else if (picks.Any(p => p != null)) open.Add((r, round, e, picks));
                }
                var firstPass = agreed.Count;
                var bad = 0;
                var examples = new SortedSet<string>();
                if (endpoints.Count > 1 && open.Count > 0)
                {
                    // confirmed[i]: AI i's (team, chassis) claims the other AI confirmed, with the rounds confirmed.
                    var confirmed = await Task.WhenAll(endpoints.Select(async (_, i) =>
                    {
                        var j = 1 - i;
                        var claims = open.Where(x => x.Picks[i] != null)
                            .Select(x => (Team: x.Entry.Team ?? "", Chassis: Clean(x.Picks[i]!), x.Round, x.Entry.Driver)).ToList();
                        if (claims.Count == 0) return new Dictionary<(string, string, string), HashSet<int>>();
                        try { return await ReviewAsync(endpoints[j], season.Key, claims, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            lock (lockObj) log.Report($"  F1 {season.Key}: {endpoints[j].Name} review: {ex.Message}");
                            return new Dictionary<(string, string, string), HashSet<int>>();
                        }
                    }));
                    foreach (var (_, round, e, picks) in open)
                    {
                        var ok = picks.Select((p, i) => p != null
                                && confirmed[i].TryGetValue((TextFold.Fold(e.Team), TextFold.Fold(e.Driver), TextFold.Fold(Clean(p))), out var rounds)
                                && rounds.Contains(round) ? Clean(p) : null)
                            .OfType<string>().ToList();
                        if (ok.Count > 0 && ok.All(c => SameChassis(c, ok[0], e.Team))) agreed[Key(round, e.Team, e.Driver)] = ok[0];
                        else if (picks.All(p => p != null))
                        {
                            bad++;
                            examples.Add($"{e.Team}: {string.Join(" / ", picks.Select((p, i) => $"{endpoints[i].Name} \"{p}\""))}");
                        }
                    }
                }
                else if (endpoints.Count == 1)
                    foreach (var (_, round, e, picks) in open) agreed[Key(round, e.Team, e.Driver)] = Clean(picks[0]!);
                var missing = season.Sum(r => r.Entries.Count(e => IsPlaceholder(r, e))) - agreed.Count - bad;
                lock (lockObj)
                {
                    if (!cache.Seasons.TryGetValue(season.Key.ToString(), out var kept)) cache.Seasons[season.Key.ToString()] = kept = new();
                    foreach (var (k, v) in agreed) kept[k] = v;
                    var n = Apply(season, agreed);
                    changed += n;
                    agreedTotal += n;
                    disagreed += bad;
                    // Done only when both answered and something came of it; otherwise asked again next update.
                    if (season.Key < thisYear && !partial && n > 0) skip.Add("ai:f1-chassis-" + season.Key);
                    log.Report($"  F1 {season.Key}: {n} entries named" +
                               (endpoints.Count > 1 && n > firstPass ? $" ({firstPass} same from both, {n - firstPass} confirmed on review)" : "") +
                               (bad > 0 ? $", {bad} left (the AIs disagree)" : "") + (missing > 0 ? $", {missing} not known" : ""));
                    foreach (var x in examples.Take(4)) log.Report("      " + x);
                    try { AppData.Save(cachePath, cache); } catch { /* only saves asking again */ }
                }
            }
            finally { gate.Release(); }
        }));

        log.Report($"AI: {agreedTotal} F1 entries got their real chassis name" +
                   (disagreed > 0 ? $"; {disagreed} kept the team name because the AIs named different cars" : "") +
                   (failed > 0 ? $"; {failed} season(s) failed and will be asked again next update" : "") + ".");
        return changed;
    }

    private static int Apply(IEnumerable<Roster> season, IReadOnlyDictionary<string, string> chassis)
    {
        var n = 0;
        foreach (var r in season)
        {
            if (Round(r) is not { } round) continue;
            foreach (var e in r.Entries.Where(e => IsPlaceholder(r, e)))
                if (chassis.TryGetValue(Key(round, e.Team, e.Driver), out var c)) { e.Car = c; n++; }
        }
        return n;
    }

    // ------------------------------------------------------------------ prompt and answer

    /// <summary>The prompt answered with OpenAI's web search, as a JSON object.</summary>
    private static async Task<JsonObject> WebJsonAsync(AiEndpoint ep, IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var prompt = string.Join("\n\n", messages.Select(m => m.Content)) +
                     "\nYou have permission to look up sources (season entry lists, chassis histories): do it now, without asking " +
                     "whether to proceed. This runs unattended and nobody can answer a question. A best-effort answer is fine; " +
                     "another expert checks it. Reply with the JSON object only.";
        var (text, _) = await AiClient.WebSearchAsync(ep, prompt, ct);
        var a = text.IndexOf('{');
        var b = text.LastIndexOf('}');
        if (a < 0 || b <= a)
            throw new AiException($"{ep.Name} web search: the answer wasn't JSON: {(text.Length > 300 ? text[..300] + "…" : text).ReplaceLineEndings(" ")}");
        try { return JsonNode.Parse(text[a..(b + 1)]) as JsonObject ?? throw new AiException($"{ep.Name} web search: the answer wasn't a JSON object."); }
        catch (System.Text.Json.JsonException ex) { throw new AiException($"{ep.Name} web search: the answer wasn't valid JSON.", ex); }
    }

    private static IReadOnlyList<AiMessage> SeasonPrompt(int year, List<Roster> races)
    {
        var s = new StringBuilder();
        s.AppendLine($"For the {year} FIA Formula One World Championship, name the chassis each driver raced in each round.");
        s.AppendLine("Rounds:");
        foreach (var r in races.OrderBy(r => Round(r)))
            s.AppendLine($"  {Round(r)}: {r.Name} ({r.Date})");
        s.AppendLine("Entries (team: driver [rounds]):");
        foreach (var team in races.SelectMany(r => r.Entries.Where(e => IsPlaceholder(r, e)).Select(e => (Round: Round(r)!.Value, e.Team, e.Driver)))
                     .GroupBy(x => x.Team ?? "").OrderBy(g => g.Key))
        {
            s.AppendLine($"  {team.Key}:");
            foreach (var d in team.GroupBy(x => x.Driver))
                s.AppendLine($"    {d.Key} [{string.Join(",", d.Select(x => x.Round).Distinct().Order())}]");
        }
        s.AppendLine();
        s.AppendLine("Give the chassis as it is usually written, with the make: e.g. \"Ferrari 312T3\", \"Lotus 79\", \"McLaren MP4/4\", " +
                     "\"Cooper T51\", \"Williams FW14B\". Where a team used different chassis in different rounds or for different drivers, " +
                     "list each with its rounds and drivers. For the Indianapolis 500 rounds give the chassis builder (e.g. \"Kurtis Kraft 500A\"). " +
                     "Cover every team listed; leave out only a chassis you have no idea of. Give your best answer now without asking questions: " +
                     "another expert checks it afterwards.");
        s.AppendLine("Answer in JSON: {\"teams\": [{\"team\": \"<team exactly as listed>\", \"chassis\": [{\"name\": \"<chassis>\", " +
                     "\"rounds\": [<round numbers>], \"drivers\": [\"<drivers exactly as listed>\"]}]}]}");
        return new[]
        {
            AiMessage.System("You are an expert on Formula One history and its cars. Be accurate; never invent a chassis name."),
            AiMessage.User(s.ToString()),
        };
    }

    /// <summary>
    /// Asks <paramref name="reviewer"/> whether the other AI's chassis claims are right. Returns the confirmed
    /// (team, driver, chassis) claims, folded, with the rounds they are right for.
    /// </summary>
    private static async Task<Dictionary<(string, string, string), HashSet<int>>> ReviewAsync(AiEndpoint reviewer, int year,
        List<(string Team, string Chassis, int Round, string Driver)> claims, CancellationToken ct)
    {
        var items = claims.GroupBy(c => (TextFold.Fold(c.Team), TextFold.Fold(c.Driver), TextFold.Fold(c.Chassis))).ToList();
        var s = new StringBuilder();
        s.AppendLine($"Another motorsport expert listed which chassis Formula One teams raced in the {year} World Championship. Check each claim.");
        for (var n = 0; n < items.Count; n++)
        {
            var g = items[n];
            s.AppendLine($"{n + 1}. {g.First().Driver} ({g.First().Team}): {g.First().Chassis} | rounds: {string.Join(",", g.Select(c => c.Round).Distinct().Order())}");
        }
        s.AppendLine();
        s.AppendLine("For each claim answer \"correct\" (that driver raced that chassis in all those rounds), \"partly\" (only in some rounds: " +
                     "list them in \"rounds\"), \"wrong\" or \"unsure\". Judge the chassis model, not its spelling. Answer \"unsure\" rather than guessing.");
        s.AppendLine("Answer in JSON: {\"claims\": [{\"n\": <claim number>, \"verdict\": \"correct|partly|wrong|unsure\", \"rounds\": [<rounds, for partly>], \"note\": \"<short>\"}]}");
        const string system = "You are an expert on Formula One history and its cars. Be accurate and strict.";
        JsonObject? o = null;
        // A review checked on the web may say "only in some rounds"; from memory alone that is where models invent
        // mid-season changes, so then only a claim confirmed as a whole counts.
        var checkedOnWeb = false;
        if (reviewer.Provider == AiProvider.OpenAI)
        {
            // OpenAI can check the claims against real sources (entry lists, chassis histories) on the web.
            try
            {
                var (text, _) = await AiClient.WebSearchAsync(reviewer,
                    system + " Look up reliable sources (race entry lists, chassis histories) to check the claims.\n\n" + s, ct);
                var a = text.IndexOf('{');
                var b = text.LastIndexOf('}');
                if (a >= 0 && b > a) o = JsonNode.Parse(text[a..(b + 1)]) as JsonObject;
                checkedOnWeb = o != null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* no web search here: plain answer below */ }
        }
        o ??= await new AiClient(new[] { reviewer }).CompleteJsonAsync(new[] { AiMessage.System(system), AiMessage.User(s.ToString()) }, ct);

        var result = new Dictionary<(string, string, string), HashSet<int>>();
        foreach (var c in (o["claims"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!int.TryParse(c["n"]?.ToString(), out var n) || n < 1 || n > items.Count) continue;
            var item = items[n - 1];
            var claimed = item.Select(x => x.Round).ToHashSet();
            var rounds = c["verdict"]?.ToString().Trim().ToLowerInvariant() switch
            {
                "correct" => claimed,
                "partly" when checkedOnWeb => (c["rounds"] as JsonArray ?? new JsonArray())
                    .Select(x => int.TryParse(x?.ToString(), out var r) ? r : 0).Where(claimed.Contains).ToHashSet(),
                _ => null,
            };
            if (rounds is { Count: > 0 }) result[item.Key] = rounds;
        }
        return result;
    }

    /// <summary>(team folded, driver folded, round) -> chassis.</summary>
    private static Dictionary<(string, string, int), string> Parse(JsonObject o)
    {
        if (o["teams"] is not JsonArray)
            throw new AiException("answered without a team list" + (o["error"] is { } err ? ": " + err : ""));
        var map = new Dictionary<(string, string, int), string>();
        foreach (var t in (o["teams"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var team = TextFold.Fold(t["team"]?.ToString());
            foreach (var c in (t["chassis"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var name = c["name"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name) || name.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
                var rounds = (c["rounds"] as JsonArray ?? new JsonArray()).Select(x => int.TryParse(x?.ToString(), out var n) ? n : 0).Where(n => n > 0).ToList();
                var drivers = (c["drivers"] as JsonArray ?? new JsonArray()).Select(x => TextFold.Fold(x?.ToString())).Where(x => x.Length > 0).ToList();
                if (drivers.Count == 0) drivers.Add("*");
                if (rounds.Count == 0) rounds.Add(0); // whole season
                foreach (var d in drivers)
                foreach (var r in rounds)
                    map.TryAdd((team, d, r), name);
            }
        }
        return map;
    }

    private static string? Lookup(Dictionary<(string, string, int), string> a, int round, RosterEntry e)
    {
        var team = TextFold.Fold(e.Team);
        var driver = TextFold.Fold(e.Driver);
        foreach (var key in new[] { (team, driver, round), (team, "*", round), (team, driver, 0), (team, "*", 0) })
            if (a.TryGetValue(key, out var c)) return c;
        return null;
    }

    /// <summary>"Ferrari 312 T3" = "312T3" = "Scuderia Ferrari 312T3": the model part, spaces and punctuation ignored.</summary>
    public static bool SameChassis(string a, string b, string? team)
    {
        // Make and team words are dropped ("Euro Brun" = "EuroBrun"), leaving the model code.
        var teamWords = Regex.Split(TextFold.Fold(team), "[^a-z0-9]+").Where(w => w.Length > 0).ToList();
        var drop = new[] { string.Concat(teamWords) }.Concat(teamWords).Where(w => w.Length > 0).OrderByDescending(w => w.Length).ToList();
        string Code(string s)
        {
            var words = Regex.Split(TextFold.Fold(s), "[^a-z0-9]+").Where(w => w.Length > 0).ToList();
            // The first word is the make ("Lotus 79", "EuroBrun ER188"); keep it only when it is all there is.
            var code = string.Concat(words.Count > 1 && !words[0].Any(char.IsDigit) ? words.Skip(1) : words);
            foreach (var d in drop) code = code.Replace(d, "");
            return code;
        }
        var ca = Code(a);
        var cb = Code(b);
        return ca.Length > 0 && ca == cb;
    }

    private static string Clean(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
}
