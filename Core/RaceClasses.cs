namespace HistoricRosterPlugin;

/// <summary>
/// Multi-class races (LMP, GT3, prototypes ... on one grid): each entry's class, and positions within a class.
/// A race is multi-class when its entries have two or more different classes.
/// </summary>
public static class RaceClasses
{
    /// <summary>The entry's class, trimmed; null when it has none.</summary>
    public static string? Of(RosterEntry e) => string.IsNullOrWhiteSpace(e.Class) ? null : e.Class.Trim();

    /// <summary>Key to group by: classes differing only in case or spacing are the same class.</summary>
    public static string Key(RosterEntry e) => (Of(e) ?? "").ToUpperInvariant().Replace(" ", "");

    public static bool IsMultiClass(IEnumerable<RosterEntry> entries)
        => entries.Select(Key).Where(k => k.Length > 0).Distinct().Skip(1).Any();

    /// <summary>The classes in the order of their best real finisher (fastest class first).</summary>
    public static List<string> Ordered(IEnumerable<RosterEntry> entries)
        => entries.Where(e => Of(e) != null)
            .GroupBy(Key)
            .OrderBy(g => g.Min(e => e.Position ?? int.MaxValue))
            .ThenBy(g => g.Min(e => e.GridPosition ?? int.MaxValue))
            .Select(g => Of(g.First())!)
            .ToList();

    /// <summary>
    /// Position within the class for each entry, from an overall position (1 = first). Entries without that position
    /// get none.
    /// </summary>
    public static Dictionary<RosterEntry, int> Positions(IEnumerable<RosterEntry> entries, Func<RosterEntry, int?> overall)
    {
        var result = new Dictionary<RosterEntry, int>(ReferenceEqualityComparer.Instance);
        foreach (var g in entries.GroupBy(Key))
        {
            var n = 0;
            foreach (var e in g.Where(e => overall(e) is > 0).OrderBy(e => overall(e)))
                result[e] = ++n;
        }
        return result;
    }

    /// <summary>"GT1 P2" style text for a class position, or "" when there is none.</summary>
    public static string Text(RosterEntry e, int? classPosition)
        => Of(e) is { } c && classPosition is { } p ? $"{c} P{p}" : "";
}
