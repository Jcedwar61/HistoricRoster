using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace HistoricRosterPlugin.Ai;

/// <summary>A real car or track to find a mod for.</summary>
/// <param name="Kind">"car" or "track".</param>
/// <param name="RealName">The real car ("Lotus 79") or circuit name.</param>
/// <param name="Folder">The AC folder the catalog expects, when known.</param>
public record ModRequest(string Kind, string RealName, string? Folder, string Race, string? Category, int? Year, string? Team = null, string? Locality = null);

/// <summary>One download option the AIs found.</summary>
/// <param name="Url">Where to get it: the checked link, or a search for the mod's name when there is no working link.</param>
/// <param name="LinkState">"ok" (checked), "unchecked" (the site blocks checks), "search" (no working link: a search page).</param>
public record ModOption(string Name, string? Author, string Url, string LinkState, bool Direct, bool Paid, int Quality,
    string Notes, string? Folder, IReadOnlyList<string> FoundBy)
{
    /// <summary>For sorting: quality, more when several AIs agree, when the link works and when it is free.</summary>
    public int Score => Quality * 10 + (FoundBy.Count - 1) * 15 + (LinkState == "ok" ? 8 : LinkState == "unchecked" ? 4 : 0) + (Paid ? 0 : 5)
                        + (Confirmed ? 12 : 0);

    /// <summary>Found on the web (OpenAI's search) or its link works; otherwise it is only what an AI remembers.</summary>
    public bool Confirmed => LinkState == "ok" || FoundBy.Any(b => b.Contains("web"));
}

public record ModOptions(ModRequest Request, IReadOnlyList<ModOption> Options, IReadOnlyList<string> Notes);

/// <summary>
/// Finds the best Assetto Corsa mods for a missing real car or track online. OpenAI searches the web (Responses API,
/// web_search tool); DeepSeek answers from what it knows. The answers are merged, every link is checked, and an option
/// whose link is dead or unknown points to a search for its name instead, so no made-up link is shown.
/// </summary>
public static class ModScout
{
    private static readonly HttpClient Check = CreateCheckClient();

    private static HttpClient CreateCheckClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) HistoricRoster/1.0");
        return c;
    }

    /// <param name="endpoints">Usable OpenAI / DeepSeek connections; each is asked (at the same time).</param>
    /// <param name="searchUrl">A mod-site search page for a name (used when an option has no working link).</param>
    public static async Task<ModOptions> FindAsync(IReadOnlyList<AiEndpoint> endpoints, ModRequest r, Func<string, string> searchUrl,
        CancellationToken ct = default)
    {
        var usable = endpoints.Where(e => e.Usable).ToList();
        if (usable.Count == 0) throw new AiException(AiClient.NotConfiguredText);
        var notes = new List<string>();
        var answers = await Task.WhenAll(usable.Select(async ep =>
        {
            try { return await AskAsync(ep, r, notes, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lock (notes) notes.Add($"{ep.Name}: {ex.Message}");
                return new List<Raw>();
            }
        }));

        // Merge the same mod named by several AIs.
        var merged = new List<(Raw First, List<string> By, List<string> Urls)>();
        foreach (var raw in answers.SelectMany(a => a))
        {
            var key = Key(raw.Name);
            var same = merged.FirstOrDefault(m => Key(m.First.Name) == key
                || (raw.Url != null && m.Urls.Any(u => SameUrl(u, raw.Url))));
            if (same.First == null) merged.Add((raw, new List<string> { raw.By }, raw.Url != null ? new List<string> { raw.Url } : new List<string>()));
            else
            {
                if (!same.By.Contains(raw.By)) same.By.Add(raw.By);
                if (raw.Url != null && !same.Urls.Any(u => SameUrl(u, raw.Url))) same.Urls.Add(raw.Url);
            }
        }

        var options = await Task.WhenAll(merged.Select(async m =>
        {
            // The first link that works; web-search links come first.
            foreach (var url in m.Urls)
            {
                var (state, direct) = await CheckAsync(url, ct);
                if (state != "dead")
                    return Option(m.First, url, state, direct, m.By);
            }
            return Option(m.First, searchUrl(m.First.Name), "search", false, m.By);
        }));
        return new ModOptions(r, options.OrderByDescending(o => o.Score).Take(8).ToList(), notes);
    }

    private static ModOption Option(Raw raw, string url, string state, bool direct, List<string> by)
        => new(raw.Name, raw.Author, url, state, direct, raw.Paid, raw.Quality, raw.Notes, raw.Folder, by);

    // ------------------------------------------------------------------ asking

    private record Raw(string Name, string? Author, string? Url, bool Paid, int Quality, string Notes, string? Folder, string By);

    private static async Task<List<Raw>> AskAsync(AiEndpoint ep, ModRequest r, List<string> notes, CancellationToken ct)
    {
        if (ep.Provider == AiProvider.OpenAI)
        {
            try
            {
                var (text, cites) = await AiClient.WebSearchAsync(ep, Prompt(r, web: true), ct);
                var list = Parse(text, $"{ep.Name} web search");
                if (list.Count == 0) lock (notes) notes.Add($"{ep.Name} searched the web and found no mod for it.");
                // A link the model gives without having cited it may be invented: keep it only if it is a cited page.
                return list.Select(x => x.Url == null || cites.Any(c => SameUrl(c.Url, x.Url)) || IsWellKnownSite(x.Url) ? x : x with { Url = null }).ToList();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lock (notes) notes.Add($"{ep.Name} web search unavailable ({ex.Message}); asked from its knowledge instead.");
            }
        }
        var client = new AiClient(new[] { ep });
        var o = await client.CompleteJsonAsync(new[] { AiMessage.User(Prompt(r, web: false)) }, ct);
        return Parse(o.ToJsonString(), ep.Name);
    }

    private static string Prompt(ModRequest r, bool web)
    {
        var what = r.Kind == "track"
            ? $"the real circuit \"{r.RealName}\"{(r.Locality != null ? $" ({r.Locality})" : "")} in the layout used for the {Period(r)} race"
            : $"the real car \"{r.RealName}\"{(r.Team != null ? $" as raced by {r.Team}" : "")} in the {Period(r)} season";
        var s = new StringBuilder();
        s.AppendLine($"Find the best Assetto Corsa (the 2014 PC sim, not Competizione) {(r.Kind == "track" ? "track" : "car")} mods for {what}.");
        s.AppendLine($"It is for recreating the {r.Race}.");
        if (r.Folder != null) s.AppendLine($"A mod in the folder \"{r.Folder}\" is known to fit; include it if you know where to get it.");
        s.AppendLine("Best means: the right " + (r.Kind == "track" ? "layout and period" : "car and period/evolution") +
                     ", good quality (accurate physics/model, working AI, CSP-ready), free before paid, still downloadable.");
        s.AppendLine("Typical places: overtake.gg (formerly RaceDepartment), assettocorsa.club, the modders' own sites, Patreon, Gumroad, Payhip, Steam (official Kunos DLC).");
        s.AppendLine(web
            ? "Search the web. Give the mod's own download or info page as url, only pages you actually found; otherwise null."
            : "Answer from what you know. Give a url only if you are certain it is the mod's real page; otherwise null (a search is made from the name).");
        s.AppendLine("Only the " + (r.Kind == "track" ? "track" : "car") + " mods themselves: not skin/livery packs, sound mods, physics patches, renaming utilities or other add-ons.");
        s.AppendLine("Up to 5 options, best first; only mods that really exist. If you know of none, return an empty list.");
        s.AppendLine("Answer only with JSON: {\"options\": [{\"name\": \"<mod name>\", \"author\": \"<modder or team or null>\", \"url\": \"<url or null>\", " +
                     "\"paid\": <true|false>, \"quality\": <1-5>, \"folder\": \"<AC folder name if known, else null>\", \"notes\": \"<one short sentence: why / caveats>\"}]}");
        return s.ToString();
    }

    private static string Period(ModRequest r) => $"{r.Year} {r.Category}".Trim() is { Length: > 0 } p ? p : "period-correct";

    private static List<Raw> Parse(string text, string by)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new AiException("the answer wasn't JSON.");
        JsonObject? o;
        try { o = JsonNode.Parse(text[start..(end + 1)]) as JsonObject; }
        catch (Exception ex) { throw new AiException("the answer wasn't valid JSON.", ex); }
        var list = new List<Raw>();
        foreach (var x in (o?["options"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (Str(x, "name") is not { } name) continue;
            var url = Str(x, "url");
            if (url != null && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))) url = null;
            var q = int.TryParse(Str(x, "quality"), out var qq) ? Math.Clamp(qq, 1, 5) : 3;
            var paid = Str(x, "paid")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            list.Add(new Raw(name, Str(x, "author"), url, paid, q, Str(x, "notes") ?? "", Str(x, "folder"), by));
        }
        return list;
    }

    // ------------------------------------------------------------------ checking links

    /// <summary>"ok", "unchecked" (blocked or rate-limited: probably fine) or "dead"; and whether it is a file download.</summary>
    private static async Task<(string State, bool Direct)> CheckAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Check.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                var attachment = resp.Content.Headers.ContentDisposition?.DispositionType == "attachment";
                var direct = ContentGetter.IsDirectArchive(resp.RequestMessage?.RequestUri?.ToString() ?? url) && !type.Contains("html")
                             || attachment && ArchiveName(resp);
                return ("ok", direct);
            }
            // Cloudflare and friends answer bots with 403/429/503; the page probably works in a browser.
            return code is 401 or 403 or 429 or 503 or 520 or 521 or 522 or 523 ? ("unchecked", false) : ("dead", false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return ("unchecked", false); } // timeout: slow, not necessarily gone
        catch (HttpRequestException ex) when (ex.StatusCode == null && ex.InnerException is not System.Net.Sockets.SocketException) { return ("unchecked", false); }
        catch { return ("dead", false); } // DNS failure, refused, bad URL
    }

    private static bool ArchiveName(HttpResponseMessage resp)
    {
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName ?? "";
        return ContentGetter.IsDirectArchive(name.Trim('"'));
    }

    private static bool IsWellKnownSite(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host.EndsWith("overtake.gg") || u.Host.EndsWith("racedepartment.com")
            || u.Host.EndsWith("assettocorsa.club") || u.Host.EndsWith("store.steampowered.com"));

    private static bool SameUrl(string a, string b)
        => Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string url)
    {
        var s = url.Split('#')[0];
        // Citations carry ?utm_source=openai.
        var q = s.IndexOf('?');
        if (q >= 0 && s[q..].Contains("utm_", StringComparison.OrdinalIgnoreCase)) s = s[..q];
        s = s.Replace("https://", "").Replace("http://", "").Replace("www.", "");
        return s.TrimEnd('/');
    }

    private static string Key(string name)
        => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? Str(JsonObject o, string key)
    {
        var n = o[key];
        if (n == null) return null;
        var s = n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToString();
        return string.IsNullOrWhiteSpace(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();
    }
}
