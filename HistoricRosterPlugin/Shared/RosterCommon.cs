using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

// Code in Shared/ is compiled into both the server plugin and the standalone RosterTool,
// so it must not depend on AssettoServer or Serilog.

public static class RosterJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Lenient options for AC's own ui_*.json files, which are often slightly invalid.</summary>
    public static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
}

public static class RosterIds
{
    private static readonly Regex ValidId = new("^[a-z0-9_-]{1,96}$", RegexOptions.Compiled);

    public static bool IsValid(string id) => ValidId.IsMatch(id);

    /// <summary>Turns free text like "1976 Japanese GP @ Fuji" into "1976-japanese-gp-fuji".</summary>
    public static string Slugify(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 80) slug = slug[..80].Trim('-');
        return slug.Length == 0 ? "roster" : slug;
    }
}

public static class TextFold
{
    /// <summary>Lowercase without accents, so "Räikkönen" and "Raikkonen" compare equal.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool Contains(string? haystack, string? needle)
        => !string.IsNullOrEmpty(needle) && Fold(haystack).Contains(Fold(needle), StringComparison.Ordinal);
}

/// <summary>The real race distance, used for the offline race.ini and the server's [RACE] session.</summary>
public static class RaceDistance
{
    /// <summary>The winner's laps (else the most laps anyone did; 10 when the roster has no lap counts).</summary>
    public static int RealLaps(Roster r)
        => Math.Max(1, r.Entries.Where(e => e.Position == 1).Select(e => e.Laps).FirstOrDefault()
                       ?? r.Entries.Max(e => e.Laps) ?? 10);

    /// <summary>True when the roster has lap counts at all.</summary>
    public static bool Known(Roster r) => r.Entries.Any(e => e.Laps is > 0);

    /// <summary>Laps for a share of the real distance (25, 50, 100 %), at least 1.</summary>
    public static int LapsFor(Roster r, int percent) => Math.Max(1, (int)Math.Ceiling(RealLaps(r) * percent / 100.0));
}

/// <summary>Logging hooks: the plugin points these at Serilog, the tool at the console.</summary>
public static class RosterLog
{
    public static Action<string> Info { get; set; } = Console.WriteLine;
    public static Action<string> Warn { get; set; } = m => Console.WriteLine("WARNING: " + m);
}
