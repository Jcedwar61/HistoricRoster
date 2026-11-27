using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace HistoricRosterPlugin.Ai;

/// <summary>An installed car or track the AI may pick from.</summary>
public record AiCandidate(string Id, string Description);

/// <summary>The AI's pick among candidates, with why. Id null = none of them fits.</summary>
public record AiPick(string? Id, string Reason, string? Confidence);

/// <summary>What a free-text race search means, as list filters.</summary>
public record AiSearch(string? Category, int? YearFrom, int? YearTo, IReadOnlyList<string> Words, string? Explanation);

/// <summary>
/// The program's AI features: race previews and reports, matching mods to real cars and tracks, search,
/// team colours and a help assistant. Each one builds its prompt from the race data and asks <see cref="AiClient"/>.
/// </summary>
public class AiAssistant
{
    public AiClient Client { get; }
    public AiAssistant(AiClient client) => Client = client;

    private const string Persona =
        "You are the assistant built into Historic Roster, a Windows program that recreates real motor races " +
        "(F1, NASCAR, GT1, LMP1, GT3, Supercars, 1950 to today) in the sim Assetto Corsa with the real grid, cars, " +
        "track, date and weather. Be accurate about motorsport history; if you are not sure of a fact, say so " +
        "rather than inventing it. Use plain, friendly English. Write plain text only: the program can't show Markdown, so " +
        "no #, ** or other Markdown symbols; put a heading on its own line and use '- ' for list items.";

    // ------------------------------------------------------------------ race preview / report

    /// <summary>A short pre-race briefing on the real race, from the seat you take.</summary>
    public IAsyncEnumerable<string> RacePreviewAsync(Roster race, string? seatDriver, CancellationToken ct = default)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Write a pre-race briefing (about 250-350 words) for the real {race.Name}.");
        if (seatDriver != null) prompt.AppendLine($"The user will drive in {seatDriver}'s seat; tell them what that driver faced and achieved that day and what to aim for.");
        prompt.AppendLine("Cover: the context of the season going into this race, the circuit and what makes it hard, the key storylines and favourites, " +
                          "and (marked clearly as a spoiler at the end, under 'What really happened') how the race actually went.");
        prompt.AppendLine("Use short headed sections. No preamble.");
        prompt.AppendLine();
        prompt.Append(RaceFacts(race, maxEntries: 40));
        return Client.StreamAsync(new[] { AiMessage.System(Persona), AiMessage.User(prompt.ToString()) }, ct);
    }

    /// <summary>A race report of the user's race against the real one.</summary>
    public IAsyncEnumerable<string> RaceReportAsync(Roster race, RaceComparison cmp, CancellationToken ct = default)
    {
        var p = new StringBuilder();
        p.AppendLine($"Write a lively race report (about 200-300 words, like a magazine report) of the user's recreation of the {race.Name}, " +
                     "compared with what really happened. Highlight the user's drive, the biggest movers against history and anything remarkable. No preamble.");
        p.AppendLine();
        p.AppendLine($"Headline: {cmp.Headline}");
        if (cmp.Race.Watched) p.AppendLine("This was an AI-only race the user watched; no driver was the user.");
        else if (cmp.Player is { } me) p.AppendLine($"The user drove in {me.Driver}'s seat{(string.IsNullOrWhiteSpace(cmp.Race.PlayerName) ? "" : $" under the name {cmp.Race.PlayerName}")}.");
        p.AppendLine("Result (user's race position | driver | team | real grid | real finish | places vs history):");
        foreach (var r in cmp.Rows.OrderBy(r => r.YourRacePosition ?? 999).Take(45))
            p.AppendLine($"{(r.YourRacePosition is { } y ? "P" + y : "-")} | {r.Driver}{(r.IsPlayer ? " (USER)" : "")} | {r.Team} | " +
                         $"{(r.RealGrid is { } g ? "P" + g : "?")} | {(r.RealPosition is { } rp ? "P" + rp : "DNF")} | {r.Delta?.ToString("+0;-0;0") ?? "?"}");
        return Client.StreamAsync(new[] { AiMessage.System(Persona), AiMessage.User(p.ToString()) }, ct);
    }

    // ------------------------------------------------------------------ matching

    /// <summary>Which installed car is the real car (or the closest same-class car when told so).</summary>
    public Task<AiPick> PickCarAsync(string realCar, Roster race, RosterEntry? entry, IReadOnlyList<AiCandidate> candidates, CancellationToken ct = default)
    {
        var q = new StringBuilder();
        q.AppendLine($"Real car: {realCar}");
        q.AppendLine($"Race: {race.Name} ({race.Category}, {race.Year})");
        if (entry != null) q.AppendLine($"Team: {entry.Team}; driver: {entry.Driver}; class: {entry.Class}");
        return PickAsync(
            "Pick the installed Assetto Corsa car mod that IS this real car (same make, model and period/evolution; a season pack containing it counts). " +
            "If none is that car, answer id null. Do not pick a merely similar car.",
            q.ToString(), candidates, ct);
    }

    /// <summary>Which installed track (and layout) is the real circuit.</summary>
    public Task<AiPick> PickTrackAsync(RosterTrack track, Roster race, IReadOnlyList<AiCandidate> candidates, CancellationToken ct = default)
    {
        var q = $"Real circuit: {track.Name}{(track.Locality != null ? $" ({track.Locality})" : "")}\nRace: {race.Name} ({race.Category}, {race.Year})";
        return PickAsync(
            "Pick the installed Assetto Corsa track and layout that is this real circuit, with the layout this kind of race used in that year " +
            "(oval for NASCAR ovals, GP layout for F1, endurance layout for sports cars; prefer a period-correct version). If none is that circuit, answer id null.",
            q, candidates, ct);
    }

    private async Task<AiPick> PickAsync(string task, string question, IReadOnlyList<AiCandidate> candidates, CancellationToken ct)
    {
        // Keep the prompt small: the most likely candidates first (callers pre-sort), capped.
        var list = new StringBuilder();
        foreach (var c in candidates.Take(400)) list.AppendLine($"{c.Id} :: {c.Description}");
        var user = $"{task}\n\n{question}\n\nInstalled candidates (id :: description):\n{list}\n" +
                   "Answer in JSON: {\"id\": \"<exact id from the list or null>\", \"confidence\": \"high|medium|low\", \"reason\": \"<one sentence>\"}";
        var o = await Client.CompleteJsonAsync(new[] { AiMessage.System(Persona), AiMessage.User(user) }, ct);
        var id = Str(o, "id");
        if (id != null && !candidates.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) id = null; // never trust an id not offered
        else if (id != null) id = candidates.First(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Id;
        return new AiPick(id, Str(o, "reason") ?? "", Str(o, "confidence"));
    }

    // ------------------------------------------------------------------ search

    /// <summary>Turns a question ("Schumacher's wet wins in the 90s") into race-list filters.</summary>
    public async Task<AiSearch> SearchAsync(string query, IReadOnlyList<string> categories, CancellationToken ct = default)
    {
        var user =
            $"The user searches a list of real races with: \"{query}\"\n" +
            $"Series in the list: {string.Join(", ", categories)}. Each race has a name (e.g. \"2009 British Grand Prix\", \"2004 24 Hours of Le Mans\"), " +
            "a series, a year and a track name. Searching matches races whose name, series, year or track contains ALL the words.\n" +
            "Use your motorsport knowledge to turn the request into filters. If it refers to specific events (e.g. 'races Senna won at Monaco'), " +
            "put the circuit/event words in words and narrow the years. Keep words few (0-3) and literal (circuit or event names, not drivers).\n" +
            "Answer in JSON: {\"category\": \"<one of the series or null>\", \"yearFrom\": <int or null>, \"yearTo\": <int or null>, " +
            "\"words\": [\"...\"], \"explanation\": \"<one sentence: what you searched for and, if useful, which specific races match>\"}";
        var o = await Client.CompleteJsonAsync(new[] { AiMessage.System(Persona), AiMessage.User(user) }, ct);
        var cat = Str(o, "category");
        if (cat != null) cat = categories.FirstOrDefault(c => c.Equals(cat, StringComparison.OrdinalIgnoreCase));
        var words = (o["words"] as JsonArray)?.Select(w => w?.ToString()).OfType<string>().Where(w => w.Length > 0).ToList() ?? new List<string>();
        return new AiSearch(cat, Int(o, "yearFrom"), Int(o, "yearTo"), words, Str(o, "explanation"));
    }

    // ------------------------------------------------------------------ team colours

    /// <summary>
    /// The real main livery colour of each entry's car, as a hue (0-360) or null for black/white/grey/unknown.
    /// Keyed by driver name.
    /// </summary>
    public async Task<Dictionary<string, double?>> TeamHuesAsync(Roster race, IReadOnlyList<RosterEntry> entries, CancellationToken ct = default)
    {
        var list = new StringBuilder();
        foreach (var e in entries) list.AppendLine($"{e.Driver} | #{e.Number} | {e.Team} | {e.Car}");
        var user =
            $"For the {race.Name} ({race.Year}), give the dominant colour of each car's real livery that day.\n" +
            "Return the hue on the HSV colour wheel in degrees (red 0, orange 30, yellow 55, green 120, cyan 180, blue 220, purple 280, pink 330), " +
            "or null if the livery was mainly black, white or silver/grey, or you don't know.\n" +
            $"Entries (driver | number | team | car):\n{list}\n" +
            "Answer in JSON: {\"cars\": [{\"driver\": \"<exact driver from the list>\", \"hue\": <number or null>, \"colour\": \"<name>\"}]}";
        var o = await Client.CompleteJsonAsync(new[] { AiMessage.System(Persona), AiMessage.User(user) }, ct);
        var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in (o["cars"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (Str(c, "driver") is not { } d) continue;
            result[d] = Dbl(c, "hue") is { } h ? ((h % 360) + 360) % 360 : null;
        }
        return result;
    }

    // ------------------------------------------------------------------ help chat

    /// <summary>The help assistant's instructions: the user guide plus what is on screen.</summary>
    public static string HelpSystemPrompt(string? guide, string? context)
    {
        var s = new StringBuilder(Persona);
        s.AppendLine();
        s.AppendLine("Help the user with the program (setting up races, matching mods, liveries, seasons, the Safety Car, Content Manager, CSP, " +
                     "errors in the activity log) and with motorsport history and Assetto Corsa in general. Be concise and practical: name the exact " +
                     "buttons and settings from the guide. If the guide doesn't cover something, say so.");
        if (!string.IsNullOrWhiteSpace(context)) s.AppendLine().AppendLine("=== What the user has open now ===").AppendLine(context.Trim());
        if (!string.IsNullOrWhiteSpace(guide))
        {
            // The guide is long; its first part (the program) matters most.
            var g = guide.Length > 30000 ? guide[..30000] + "\n[guide shortened]" : guide;
            s.AppendLine().AppendLine("=== The program's user guide ===").AppendLine(g);
        }
        return s.ToString();
    }

    public IAsyncEnumerable<string> ChatAsync(string systemPrompt, IReadOnlyList<AiMessage> history, CancellationToken ct = default)
        => Client.StreamAsync(new[] { AiMessage.System(systemPrompt) }.Concat(history).ToList(), ct);

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// The candidates most likely to match a description (shared words first), so a big mod folder still fits in a prompt.
    /// </summary>
    public static List<AiCandidate> Shortlist(string description, IEnumerable<AiCandidate> candidates, int max = 150)
    {
        var words = ContentLibrary.Tokens(description).Where(w => w.Length > 1).Select(w => w.ToLowerInvariant()).ToHashSet();
        return candidates
            .Select(c => (c, score: ContentLibrary.Tokens(c.Id + " " + c.Description).Count(t => words.Contains(t.ToLowerInvariant()))))
            .OrderByDescending(x => x.score).ThenBy(x => x.c.Id, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(x => x.c).ToList();
    }

    /// <summary>The race as plain facts for a prompt.</summary>
    public static string RaceFacts(Roster race, int maxEntries = 60)
    {
        var s = new StringBuilder();
        s.AppendLine($"Race: {race.Name}");
        s.AppendLine($"Series: {race.Series} ({race.Category}), year {race.Year}{(race.Date != null ? ", date " + race.Date : "")}");
        if (race.Track != null) s.AppendLine($"Circuit: {race.Track.Name}{(race.Track.Locality != null ? ", " + race.Track.Locality : "")}");
        if (race.Notes != null) s.AppendLine($"Notes: {race.Notes}");
        s.AppendLine("Entries (grid | finish/status | #number | driver | team | car):");
        foreach (var e in RosterOrder.Order(race.Entries, x => x, GridOrder.StartingGrid).Take(maxEntries))
            s.AppendLine($"{(e.GridPosition is { } g ? "P" + g : "-")} | {(e.Position is { } p ? "P" + p : e.Status ?? "?")} | #{e.Number} | {e.Driver} | {e.Team} | {e.Car}{(e.Class != null ? " [" + e.Class + "]" : "")}");
        return s.ToString();
    }

    private static string? Str(JsonObject o, string key)
    {
        var n = o[key];
        if (n == null) return null;
        var s = n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToString();
        return string.IsNullOrWhiteSpace(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();
    }

    private static int? Int(JsonObject o, string key) => Dbl(o, key) is { } d ? (int)Math.Round(d) : null;

    private static double? Dbl(JsonObject o, string key)
        => Str(o, key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
