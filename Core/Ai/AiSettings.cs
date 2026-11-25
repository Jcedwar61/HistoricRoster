using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HistoricRosterPlugin.Ai;

/// <summary>
/// OpenAI and DeepSeek settings, saved in settings.json. The API keys are encrypted for your Windows user (DPAPI),
/// so the file is useless on another account or PC. OPENAI_API_KEY / DEEPSEEK_API_KEY environment variables work too.
/// </summary>
public class AiSettings
{
    public string Preferred { get; set; } = nameof(AiProvider.OpenAI);
    /// <summary>When the preferred service fails or has no key, use the other one.</summary>
    public bool Fallback { get; set; } = true;

    public string? OpenAiKeyProtected { get; set; }
    public string OpenAiModel { get; set; } = AiEndpoint.OpenAiDefaultModel;
    public string? OpenAiUrl { get; set; }

    public string? DeepSeekKeyProtected { get; set; }
    public string DeepSeekModel { get; set; } = AiEndpoint.DeepSeekDefaultModel;
    public string? DeepSeekUrl { get; set; }

    /// <summary>Generated liveries take each team's real colour (asked from the AI).</summary>
    public bool TeamColours { get; set; } = true;

    [JsonIgnore]
    public string OpenAiKey
    {
        get => Unprotect(OpenAiKeyProtected) ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        set => OpenAiKeyProtected = Protect(value);
    }

    [JsonIgnore]
    public string DeepSeekKey
    {
        get => Unprotect(DeepSeekKeyProtected) ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        set => DeepSeekKeyProtected = Protect(value);
    }

    // Never serialized: these carry the decrypted keys.
    [JsonIgnore] public AiEndpoint OpenAi => new(AiProvider.OpenAI, OpenAiKey, OpenAiModel, OpenAiUrl);
    [JsonIgnore] public AiEndpoint DeepSeek => new(AiProvider.DeepSeek, DeepSeekKey, DeepSeekModel, DeepSeekUrl);

    [JsonIgnore] public bool Configured => OpenAi.Usable || DeepSeek.Usable;

    public AiClient CreateClient()
    {
        var first = Preferred == nameof(AiProvider.DeepSeek) ? DeepSeek : OpenAi;
        var second = first.Provider == AiProvider.OpenAI ? DeepSeek : OpenAi;
        return new AiClient(Fallback || !first.Usable ? new[] { first, second } : new[] { first });
    }

    public AiAssistant CreateAssistant() => new(CreateClient());

    /// <summary>The "Ai" part of the Historic Roster program's settings.json (for RosterTool); defaults when missing.</summary>
    public static AiSettings LoadFromProgram()
    {
        var path = AppData.PathOf("settings.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["Ai"] is JsonObject ai)
                return ai.Deserialize<AiSettings>() ?? new AiSettings();
        }
        catch { /* unreadable: defaults (environment variables still work) */ }
        return new AiSettings();
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HistoricRoster.AI");

    private static string? Protect(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !OperatingSystem.IsWindows()) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; } // another user or PC: the key has to be entered again
    }
}
