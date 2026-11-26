namespace HistoricRosterPlugin;

/// <summary>
/// Picks an installed car to stand in for a real car that isn't installed, so the grid stays full.
/// Only cars of the same class are used (an F1 car for an F1 car, a GT1 car for a GT1 car), preferring:
/// the car a team-mate drives in this race, then the closest year (so the field runs at one pace), the same make or
/// team (within about a decade), then a car that has this driver's own livery. The real car is still listed as missing, with its download link.
/// </summary>
public class StandInPicker
{
    private readonly RosterContent _content;
    private readonly ContentLibrary _library;
    private readonly Dictionary<string, int> _usedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _usedSkins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _teamCars = new(StringComparer.OrdinalIgnoreCase);

    public StandInPicker(RosterContent content, ContentLibrary library)
    {
        _content = content;
        _library = library;
        // Real cars installed in this race, by team: a team-mate's car is the best stand-in.
        foreach (var c in content.Cars.Where(c => c.Installed && c.Model != null))
        {
            if (!string.IsNullOrWhiteSpace(c.Entry.Team)) _teamCars.TryAdd(c.Entry.Team!.Trim(), c.Model!);
            Use(c.Model!, c.Skin);
        }
    }

    /// <summary>The class a stand-in must have: the entry's own class in multi-class races, else the race's series.</summary>
    public static string? ClassOf(Roster roster, RosterEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Class))
        {
            var fromClass = ContentLibrary.Classify(entry.Class!, null, Array.Empty<string>(), null, roster.Year);
            if (fromClass != null) return fromClass;
        }
        return roster.Category;
    }

    /// <summary>True when at least one installed car of the entry's class exists.</summary>
    public static bool CanStandIn(Roster roster, RosterEntry entry, ContentLibrary library)
        => ClassOf(roster, entry) is { } cls && library.Cars.Any(c => string.Equals(c.Category, cls, StringComparison.OrdinalIgnoreCase));

    public ResolvedCar? Pick(ResolvedCar missing)
    {
        var entry = missing.Entry;
        var cls = ClassOf(_content.Roster, entry);
        if (cls == null) return null;
        var pool = _library.Cars.Where(c => string.Equals(c.Category, cls, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) return null;

        var year = _content.Roster.Year;
        var wanted = ContentLibrary.Tokens($"{entry.Car} {entry.Team}").ToHashSet();
        var teamCar = entry.Team != null && _teamCars.TryGetValue(entry.Team.Trim(), out var tc) ? tc : null;

        CarInfo? best = null;
        var bestScore = double.MinValue;
        string? bestSkin = null;
        foreach (var c in pool)
        {
            double score = 0;
            if (teamCar != null && c.Folder.Equals(teamCar, StringComparison.OrdinalIgnoreCase)) score += 200;
            // The era matters most: a 2017 Ferrari is no stand-in for a 1982 one (it laps far faster, and the AI crashes
            // into the slower cars around it). Same make or team counts fully within 12 years, little beyond; a car whose
            // year isn't known counts as far off.
            int? gap = year != null && c.Year != null ? Math.Abs(c.Year.Value - year.Value) : null;
            var makeWeight = gap is null or <= 12 ? 30 : 8;
            score += ContentLibrary.Tokens($"{c.Name} {c.Brand} {c.Folder}").Distinct().Count(wanted.Contains) * makeWeight;
            score -= gap is { } g ? 4 * g : year != null ? 60 : 15;
            var ownLivery = _library.SkinFor(c.Folder, entry);
            if (ownLivery != null) score += 25;
            score -= 8 * _usedModels.GetValueOrDefault(c.Folder);
            if (score > bestScore) { bestScore = score; best = c; bestSkin = ownLivery; }
        }
        if (best == null) return null;

        // The driver's own livery on that car if it has one, else its least-used livery, so the grid isn't all one colour.
        var skin = bestSkin ?? best.Skins.Select(s => s.Folder).OrderBy(s => _usedSkins.GetValueOrDefault(best.Folder + "/" + s)).FirstOrDefault();
        Use(best.Folder, skin);
        // Team-mates get the same stand-in car.
        if (!string.IsNullOrWhiteSpace(entry.Team)) _teamCars.TryAdd(entry.Team!.Trim(), best.Folder);
        return new ResolvedCar(entry, best.Folder, skin, true, best.Kunos, missing.DownloadUrl, missing.Label, "stand-in");
    }

    private void Use(string model, string? skin)
    {
        _usedModels[model] = _usedModels.GetValueOrDefault(model) + 1;
        if (skin != null) _usedSkins[model + "/" + skin] = _usedSkins.GetValueOrDefault(model + "/" + skin) + 1;
    }
}
