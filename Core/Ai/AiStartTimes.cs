using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin.Ai;

/// <summary>
/// Real local start times for F1 races the results API has none for (it only has them from 2005 on). OpenAI looks
/// each season up on the web and gives a source for each time; DeepSeek answers from what it knows. A time is used
/// when it comes with a web source, or when both give the same time (within 15 minutes). Times are written as the
/// race's local start (<see cref="Roster.UsualLocalTime"/>) and kept in _ai_f1_starts.json, so a refresh of the F1
/// results puts them back without asking again.
/// </summary>
public static class AiStartTimes
{
    public const string CacheFile = "_ai_f1_starts.json";
    private const int Parallel = 4;

    /// <summary>Cache: year -> round -> "HH:mm" local.</summary>
    private class StartCache
    {
        public Dictionary<string, Dictionary<string, string>> Seasons { get; set; } = new();
    }

    private static int? Round(Roster r)
        => r.Source != null && Regex.Match(r.Source, @"ergast/f1/\d{4}/(\d+)/") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    private static bool Missing(Roster r) => r.Time == null && r.UsualLocalTime == null;

    /// <summary>
    /// Fills in local start times for F1 races without one. <paramref name="skip"/> holds the seasons already done
    /// ("ai:f1-start-1978"). Returns the number of races changed.
    /// </summary>
    public static async Task<int> FillF1StartTimesAsync(RosterDatabase db, string rostersFolder, AiSettings settings, HashSet<string> skip,
        IProgress<string> log, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(rostersFolder, CacheFile);
        var cache = AppData.Load<StartCache>(cachePath) ?? new StartCache();
        var changed = 0;

        var seasons = db.Rosters
            .Where(r => r.Category == "F1" && r.Year != null && Round(r) != null && Missing(r))
            .GroupBy(r => r.Year!.Value).OrderBy(g => g.Key).ToList();
        if (seasons.Count == 0) return 0;

        // Times found in earlier runs first.
        foreach (var season in seasons)
            if (cache.Seasons.TryGetValue(season.Key.ToString(), out var known))
                changed += Apply(season, known);

        var web = settings.OpenAi.Usable ? settings.OpenAi : null;
        var memory = settings.DeepSeek.Usable ? settings.DeepSeek : null;
        if (web == null && memory == null) return changed;
        var toAsk = seasons.Where(s => s.Any(Missing) && !skip.Contains("ai:f1-start-" + s.Key)).ToList();
        if (toAsk.Count == 0) return changed;

        log.Report($"AI: real F1 start times for {toAsk.Count} season(s)" +
                   (web != null ? $", looked up on the web by {web.Name} ({web.Model})" : "") +
                   (memory != null ? $", checked by {memory.Name} ({memory.Model})" : "") +
                   "; a time is used when it has a web source or both give it.");

        var total = 0;
        var failed = 0;
        using var gate = new SemaphoreSlim(Parallel);
        var lockObj = new object();
        await Task.WhenAll(toAsk.Select(async season =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var races = season.Where(Missing).OrderBy(r => Round(r)).ToList();
                var prompt = SeasonPrompt(season.Key, races);
                var webTask = web == null ? Task.FromResult<Dictionary<int, (TimeSpan, string?)>?>(null) : Ask(async () =>
                {
                    var (text, _) = await AiClient.WebSearchAsync(web, prompt + "\nLook the times up on the web now (race reports, " +
                        "contemporary newspapers, TV listings, motorsport archives) without asking whether to proceed: this runs unattended. " +
                        "Give the page you found each time on as \"source\". Reply with the JSON object only.", ct);
                    var a = text.IndexOf('{');
                    var b = text.LastIndexOf('}');
                    if (a < 0 || b <= a) throw new AiException($"{web.Name} web search: the answer wasn't JSON.");
                    // A time without its own source needs DeepSeek to give the same one.
                    return Parse(JsonNode.Parse(text[a..(b + 1)]) as JsonObject ?? new JsonObject());
                }, web.Name, season.Key, log, lockObj);
                var memTask = memory == null ? Task.FromResult<Dictionary<int, (TimeSpan, string?)>?>(null) : Ask(async () =>
                {
                    var o = await new AiClient(new[] { memory }).CompleteJsonAsync(new[]
                    {
                        AiMessage.System("You are an expert on Formula One history. Be accurate; leave out a time you don't know."),
                        AiMessage.User(prompt),
                    }, ct);
                    // From memory: never a source.
                    return Parse(o).ToDictionary(x => x.Key, x => (x.Value.Time, (string?)null));
                }, memory.Name, season.Key, log, lockObj);
                var answers = await Task.WhenAll(webTask, memTask);
                if (answers.All(a => a == null)) { Interlocked.Increment(ref failed); return; }
                var w = answers[0] ?? new();
                var m = answers[1] ?? new();

                var found = new Dictionary<string, string>();
                int sourced = 0, agreed = 0, open = 0;
                foreach (var r in races)
                {
                    var round = Round(r)!.Value;
                    var hasW = w.TryGetValue(round, out var wt);
                    var hasM = m.TryGetValue(round, out var mt);
                    TimeSpan? pick = null;
                    if (hasW && wt.Item2 != null) { pick = wt.Item1; sourced++; }
                    else if (hasW && hasM && Math.Abs((wt.Item1 - mt.Item1).TotalMinutes) <= 15) { pick = wt.Item1; agreed++; }
                    else if (web == null && hasM) { pick = mt.Item1; agreed++; } // DeepSeek only: its answer as it is
                    else if (hasW || hasM) open++;
                    if (pick is { } p) found[round.ToString(CultureInfo.InvariantCulture)] = p.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                }

                lock (lockObj)
                {
                    if (!cache.Seasons.TryGetValue(season.Key.ToString(), out var kept)) cache.Seasons[season.Key.ToString()] = kept = new();
                    foreach (var (k, v) in found) kept[k] = v;
                    var n = Apply(season, found);
                    changed += n;
                    total += n;
                    if (answers.All(a => a != null) && n > 0) skip.Add("ai:f1-start-" + season.Key);
                    log.Report($"  F1 {season.Key}: {n} of {races.Count} start times" +
                               (sourced + agreed > 0 ? $" ({sourced} from web sources, {agreed} given by both)" : "") +
                               (open > 0 ? $", {open} unsure" : ""));
                    try { AppData.Save(cachePath, cache); } catch { /* only saves asking again */ }
                }
            }
            finally { gate.Release(); }
        }));

        log.Report($"AI: {total} F1 races got their real start time" +
                   (failed > 0 ? $"; {failed} season(s) failed and will be asked again next update" : "") + ".");
        return changed;
    }

    private static async Task<Dictionary<int, (TimeSpan Time, string? Source)>?> Ask(Func<Task<Dictionary<int, (TimeSpan, string?)>>> ask,
        string name, int year, IProgress<string> log, object lockObj)
    {
        try { return await ask(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (lockObj) log.Report($"  F1 {year}: {name}: {ex.Message}");
            return null;
        }
    }

    private static int Apply(IEnumerable<Roster> season, IReadOnlyDictionary<string, string> times)
    {
        var n = 0;
        foreach (var r in season)
            if (Missing(r) && Round(r) is { } round && times.TryGetValue(round.ToString(CultureInfo.InvariantCulture), out var t))
            {
                r.UsualLocalTime = t;
                n++;
            }
        return n;
    }

    private static string SeasonPrompt(int year, List<Roster> races)
    {
        var s = new StringBuilder();
        s.AppendLine($"For the {year} FIA Formula One World Championship, give the start time of each of these races, in the local time at the circuit on race day:");
        foreach (var r in races)
            s.AppendLine($"  {Round(r)}: {r.Name}, {r.Track?.Name}, {r.Date}");
        s.AppendLine();
        s.AppendLine("The start time is when the race itself started (the start, not the warm-up or the formation lap's scheduled time if " +
                     "different), as \"HH:MM\" on a 24-hour clock. Leave out a race whose start time you can't find or don't know: a wrong " +
                     "time is worse than none.");
        s.AppendLine("Answer in JSON: {\"races\": [{\"round\": <round>, \"start\": \"HH:MM\", \"source\": \"<URL where you found it, or null>\"}]}");
        return s.ToString();
    }

    /// <summary>round -> (local start, source URL or null). Times outside 09:00-20:00 are dropped (no F1 race before
    /// 2005 started outside them).</summary>
    private static Dictionary<int, (TimeSpan Time, string? Source)> Parse(JsonObject o)
    {
        var map = new Dictionary<int, (TimeSpan, string?)>();
        foreach (var x in (o["races"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!int.TryParse(x["round"]?.ToString(), out var round)) continue;
            var m = Regex.Match(x["start"]?.ToString() ?? "", @"^\s*(\d{1,2})[:.h](\d{2})");
            if (!m.Success) continue;
            var t = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
            if (t.TotalHours < 9 || t.TotalHours > 20) continue;
            var src = x["source"]?.ToString();
            map[round] = (t, string.IsNullOrWhiteSpace(src) || src == "null" || !src.StartsWith("http") ? null : src.Trim());
        }
        return map;
    }
}
