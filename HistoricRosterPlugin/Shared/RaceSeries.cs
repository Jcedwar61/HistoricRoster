using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>
/// The championship family a race belongs to, for picking races by series: multi-class races are filed under their
/// top class (LMP1, Hypercar ...), so IMSA and WEC races are also found under "IMSA" and "WEC".
/// </summary>
public static class RaceSeries
{
    public static string? Group(Roster r)
    {
        var s = r.Series ?? "";
        if (Regex.IsMatch(s, @"\bIMSA\b|American Le Mans|United SportsCar|^24 Hours of Daytona$|^12 Hours of Sebring$|^Petit Le Mans$", RegexOptions.IgnoreCase))
            return "IMSA";
        if (s.Contains("World Endurance", StringComparison.OrdinalIgnoreCase)) return "WEC";
        if (s == "DTM") return "DTM";
        return null;
    }

    /// <summary>The choices of a series filter: every category and every group.</summary>
    public static List<string> Choices(IEnumerable<Roster> rosters)
        => rosters.SelectMany(r => new[] { r.Category, Group(r) }).Where(c => !string.IsNullOrEmpty(c)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when the race is of that category or series group.</summary>
    public static bool Is(Roster r, string choice)
        => string.Equals(r.Category, choice, StringComparison.OrdinalIgnoreCase) || string.Equals(Group(r), choice, StringComparison.OrdinalIgnoreCase);
}
