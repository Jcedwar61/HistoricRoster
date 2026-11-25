using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HistoricRosterPlugin.Ai;

/// <summary>The AI services the program can use. Both speak the OpenAI chat-completions API.</summary>
public enum AiProvider { OpenAI, DeepSeek }

/// <summary>One provider's connection: key, model and address.</summary>
public record AiEndpoint(AiProvider Provider, string ApiKey, string Model, string? BaseUrl = null)
{
    public const string OpenAiDefaultUrl = "https://api.openai.com/v1";
    public const string DeepSeekDefaultUrl = "https://api.deepseek.com/v1";
    public const string OpenAiDefaultModel = "gpt-5-mini";
    public const string DeepSeekDefaultModel = "deepseek-chat";

    public string Url => (string.IsNullOrWhiteSpace(BaseUrl)
        ? Provider == AiProvider.OpenAI ? OpenAiDefaultUrl : DeepSeekDefaultUrl
        : BaseUrl!).TrimEnd('/');

    public string Name => Provider == AiProvider.OpenAI ? "OpenAI" : "DeepSeek";
    public bool Usable => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);
}

public record AiMessage(string Role, string Content)
{
    public static AiMessage System(string text) => new("system", text);
    public static AiMessage User(string text) => new("user", text);
    public static AiMessage Assistant(string text) => new("assistant", text);
}

public class AiException : Exception
{
    public AiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Talks to OpenAI and DeepSeek. Requests go to the preferred provider first and, when it isn't set up or
/// fails, to the other one. Nothing is sent unless a feature that needs it is used.
/// </summary>
public class AiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>The providers to try, in order.</summary>
    public IReadOnlyList<AiEndpoint> Endpoints { get; }

    public AiClient(IEnumerable<AiEndpoint> endpoints) => Endpoints = endpoints.Where(e => e.Usable).ToList();

    public bool Configured => Endpoints.Count > 0;

    /// <summary>Which provider answered the last request ("OpenAI (gpt-5-mini)").</summary>
    public string? LastUsed { get; private set; }

    // ------------------------------------------------------------------ requests

    /// <summary>A complete answer as text.</summary>
    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct = default, bool json = false)
    {
        return await WithFallback(async ep =>
        {
            using var req = Request(ep, messages, stream: false, json);
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new AiException($"{ep.Name}: {ErrorText(resp, body)}");
            var node = JsonNode.Parse(body);
            return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new AiException($"{ep.Name} returned no answer.");
        }, ct);
    }

    /// <summary>A JSON object answer (the prompt must ask for JSON), parsed.</summary>
    public async Task<JsonObject> CompleteJsonAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct = default)
    {
        var text = await CompleteAsync(messages, ct, json: true);
        // Some models wrap JSON in a code fence despite JSON mode.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new AiException("The AI's answer wasn't JSON.");
        try { return JsonNode.Parse(text[start..(end + 1)]) as JsonObject ?? throw new AiException("The AI's answer wasn't a JSON object."); }
        catch (JsonException ex) { throw new AiException("The AI's answer wasn't valid JSON.", ex); }
    }

    /// <summary>The answer piece by piece as it is written (for text shown live).</summary>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<AiMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Configured) throw new AiException(NotConfiguredText);
        Exception? last = null;
        foreach (var ep in Endpoints)
        {
            HttpResponseMessage resp;
            try
            {
                var req = Request(ep, messages, stream: true, json: false);
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    resp.Dispose();
                    throw new AiException($"{ep.Name}: {ErrorText(resp, body)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                continue; // next provider
            }

            LastUsed = $"{ep.Name} ({ep.Model})";
            using (resp)
            {
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(s, Encoding.UTF8);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (!line.StartsWith("data:")) continue;
                    var data = line[5..].Trim();
                    if (data == "[DONE]") yield break;
                    string? piece = null;
                    try { piece = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>(); }
                    catch (JsonException) { /* keep-alive or partial line */ }
                    if (!string.IsNullOrEmpty(piece)) yield return piece;
                }
            }
            yield break;
        }
        throw last as AiException ?? new AiException(last?.Message ?? NotConfiguredText, last);
    }

    /// <summary>
    /// An answer researched on the web (OpenAI's Responses API with its web_search tool), with the pages it cited.
    /// OpenAI only; DeepSeek has no web search.
    /// </summary>
    public static async Task<(string Text, IReadOnlyList<(string Url, string Title)> Citations)> WebSearchAsync(
        AiEndpoint ep, string prompt, CancellationToken ct = default)
    {
        if (ep.Provider != AiProvider.OpenAI) throw new AiException($"{ep.Name} can't search the web.");
        var body = new JsonObject
        {
            ["model"] = ep.Model.Trim(),
            ["input"] = prompt,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" }),
        };
        // Reasoning models think long by default; a search answer doesn't need it.
        var m = ep.Model.Trim().ToLowerInvariant();
        if (m.StartsWith("gpt-5") || m.StartsWith("o3") || m.StartsWith("o4")) body["reasoning"] = new JsonObject { ["effort"] = "low" };
        using var req = new HttpRequestMessage(HttpMethod.Post, ep.Url + "/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey.Trim());
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException($"{ep.Name} web search: {ErrorText(resp, text)}");

        var answer = new StringBuilder();
        var cites = new List<(string, string)>();
        foreach (var item in (JsonNode.Parse(text)?["output"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (item["type"]?.ToString() != "message") continue;
            foreach (var part in (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (part["type"]?.ToString() != "output_text") continue;
                answer.Append(part["text"]?.ToString());
                foreach (var a in (part["annotations"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (a["type"]?.ToString() == "url_citation" && a["url"]?.ToString() is { Length: > 0 } u)
                        cites.Add((u, a["title"]?.ToString() ?? ""));
            }
        }
        if (answer.Length == 0) throw new AiException($"{ep.Name} web search returned no answer.");
        return (answer.ToString(), cites);
    }

    /// <summary>The models the provider offers (for the settings window).</summary>
    public static async Task<List<string>> ListModelsAsync(AiEndpoint ep, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ep.Url + "/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey.Trim());
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException($"{ep.Name}: {ErrorText(resp, body)}");
        return (JsonNode.Parse(body)?["data"] as JsonArray ?? new JsonArray())
            .Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Order().ToList();
    }

    /// <summary>Checks a key and model with a tiny request. Returns the model's reply.</summary>
    public static Task<string> TestAsync(AiEndpoint ep, CancellationToken ct = default)
        => new AiClient(new[] { ep }).CompleteAsync(new[] { AiMessage.User("Reply with the single word: ready") }, ct);

    // ------------------------------------------------------------------ helpers

    public const string NotConfiguredText = "No AI service is set up. Add an OpenAI or DeepSeek API key in AI settings.";

    private async Task<T> WithFallback<T>(Func<AiEndpoint, Task<T>> call, CancellationToken ct)
    {
        if (!Configured) throw new AiException(NotConfiguredText);
        var errors = new List<string>();
        foreach (var ep in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await call(ep);
                LastUsed = $"{ep.Name} ({ep.Model})";
                return r;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add(ex is AiException ? ex.Message : $"{ep.Name}: {ex.Message}");
            }
        }
        throw new AiException(string.Join(" / ", errors));
    }

    private static HttpRequestMessage Request(AiEndpoint ep, IReadOnlyList<AiMessage> messages, bool stream, bool json)
    {
        var body = new JsonObject
        {
            ["model"] = ep.Model.Trim(),
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["stream"] = stream,
        };
        // DeepSeek's reasoner model doesn't take JSON mode; the prompt still asks for JSON.
        if (json && !ep.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        var req = new HttpRequestMessage(HttpMethod.Post, ep.Url + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey.Trim());
        return req;
    }

    private static string ErrorText(HttpResponseMessage resp, string body)
    {
        string? msg = null;
        try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch { }
        var code = (int)resp.StatusCode;
        var hint = code switch
        {
            401 => " (check the API key)",
            402 => " (the account has no credit left)",
            404 => " (check the model name)",
            429 => " (rate limit or quota reached)",
            _ => "",
        };
        return $"HTTP {code}{hint}{(msg != null ? ": " + msg : "")}";
    }
}
