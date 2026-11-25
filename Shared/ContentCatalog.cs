namespace HistoricRosterPlugin;

/// <summary>
/// content_catalog.json: maps real cars and circuits to Assetto Corsa folders and download pages.
/// Rules are checked top to bottom; the first match wins, so put specific rules first.
/// </summary>
public class ContentCatalog
{
    public List<CarRule> Cars { get; set; } = new();
    public List<TrackRule> Tracks { get; set; } = new();
    /// <summary>Balance of performance for multi-class grids: ballast/restrictor by class, car or AC model.</summary>
    public List<BalanceRule> Balance { get; set; } = new();

    /// <summary>The first balance rule for this entry driving <paramref name="model"/>, or null.</summary>
    public BalanceRule? BalanceFor(Roster roster, RosterEntry entry, string? model, string? cls)
        => Balance.FirstOrDefault(b => b.Matches(roster, entry, model, cls));
}

/// <summary>
/// Ballast (kg) and restrictor (%) for a class or car, e.g. { "class": "GTE", "ballast": 20, "restrictor": 5 }.
/// Applied only to entries that don't set their own ballast or restrictor. First match wins.
/// </summary>
public class BalanceRule
{
    /// <summary>Roster category (F1, LMP1, ...). Null = any.</summary>
    public string? Category { get; set; }
    /// <summary>Case-insensitive substring of the entry's class (or the stand-in class). Null = any class.</summary>
    public string? Class { get; set; }
    /// <summary>Case-insensitive substring of the real car name. Null = any car.</summary>
    public string? Car { get; set; }
    /// <summary>AC car folder, exact. Null = any model.</summary>
    public string? Model { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public float Ballast { get; set; }
    public int Restrictor { get; set; }
    public string? Note { get; set; }

    public bool Matches(Roster roster, RosterEntry entry, string? model, string? cls)
    {
        if (Category != null && !string.Equals(Category, roster.Category, StringComparison.OrdinalIgnoreCase)) return false;
        if (YearFrom != null && (roster.Year == null || roster.Year < YearFrom)) return false;
        if (YearTo != null && (roster.Year == null || roster.Year > YearTo)) return false;
        if (Class != null && !(Has(entry.Class, Class) || Has(cls, Class))) return false;
        if (Car != null && !Has(entry.Car, Car)) return false;
        if (Model != null && !string.Equals(Model, model, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool Has(string? haystack, string needle)
        => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

public class CarRule
{
    /// <summary>F1, GT1, LMP1, GT3, NASCAR, ... Null = any category.</summary>
    public string? Category { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    /// <summary>Case-insensitive substring of the entry's Team. Null = any team.</summary>
    public string? Team { get; set; }
    /// <summary>Case-insensitive substring of the entry's Car. Null = any car.</summary>
    public string? Car { get; set; }
    /// <summary>Case-insensitive substring of the entry's Class. Null = any class.</summary>
    public string? Class { get; set; }
    /// <summary>Driver name or surname contained in the entry's Driver (accents ignored). Null = any driver.
    /// Used for per-seat rules imported from championships: this driver's exact car and livery.</summary>
    public string? Driver { get; set; }
    /// <summary>The rule doesn't apply when the entry's Car contains any of these (accents ignored), e.g. a "911 GT3 R"
    /// rule for the 2016 car with exclude ["992", "991.II"] so the later cars aren't mapped to it.</summary>
    public List<string>? Exclude { get; set; }
    /// <summary>Whole words that must ALL be in the entry's Car (or Team), ignoring case and accents, e.g. ["499p"] or
    /// ["ford", "ba"]. Used by the automatic scan: the distinctive words of the installed car's name.</summary>
    public List<string>? CarWords { get; set; }
    /// <summary>Only for races at these circuits (substrings of the real circuit name, accents ignored), e.g. the
    /// road-course version of a stock car for ["sonoma", "watkins glen"]. Null = any circuit.</summary>
    public List<string>? Tracks { get; set; }

    /// <summary>AC car folder. Null = no AC car known yet (only the download search is offered).</summary>
    public string? Model { get; set; }
    public string? Skin { get; set; }
    /// <summary>Official Kunos content (base game / DLC): the link goes to Steam instead of overtake.gg.</summary>
    public bool Kunos { get; set; }
    /// <summary>Exact download page for the mod.</summary>
    public string? Url { get; set; }
    /// <summary>Search text for the download link when Url is empty. Placeholders: {year} {team} {car} {category}.</summary>
    public string? Search { get; set; }
    public string? Note { get; set; }

    /// <summary>
    /// Category matches the roster's category, or the entry's class in multi-class races
    /// (a "GT1" rule matches an "LMGT1" entry at Le Mans). Team and Car match in either direction,
    /// so "Red Bull Racing" and "Red Bull" match each other.
    /// </summary>
    public bool Matches(string? category, int? year, RosterEntry entry)
    {
        if (Category != null && !string.Equals(Category, category, StringComparison.OrdinalIgnoreCase)
            && !Contains(entry.Class, Category)) return false;
        if (YearFrom != null && (year == null || year < YearFrom)) return false;
        if (YearTo != null && (year == null || year > YearTo)) return false;
        if (Team != null && !EitherContains(entry.Team, Team)) return false;
        if (Car != null && !EitherContains(entry.Car, Car)) return false;
        if (Class != null && !Contains(entry.Class, Class)) return false;
        if (Driver != null && !TextFold.Contains(entry.Driver, Driver)) return false;
        if (Exclude != null && Exclude.Any(x => TextFold.Contains(entry.Car, x))) return false;
        if (CarWords is { Count: > 0 })
        {
            var words = Words($"{entry.Car} {entry.Team}");
            if (!CarWords.All(w => words.Contains(TextFold.Fold(w)))) return false;
        }
        return true;
    }

    /// <summary>The rule's circuits include this race's circuit (always true without <see cref="Tracks"/>).</summary>
    public bool MatchesTrack(RosterTrack? track)
        => Tracks is not { Count: > 0 }
           || track != null && Tracks.Any(t => TextFold.Contains(track.Name, t) || TextFold.Contains(track.Key, t));

    /// <summary>Lower-case, accent-free words: "Ford Falcon (BA)" -> {ford, falcon, ba}.</summary>
    public static HashSet<string> Words(string? text)
        => System.Text.RegularExpressions.Regex.Split(TextFold.Fold(text), "[^a-z0-9]+").Where(w => w.Length > 0).ToHashSet();

    private static bool Contains(string? haystack, string needle)
        => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Either string contains the other, ignoring case and accents ("Huracán" = "Huracan").</summary>
    private static bool EitherContains(string? value, string ruleValue)
        => value != null && (TextFold.Contains(value, ruleValue)
                             || (value.Trim().Length >= 3 && TextFold.Contains(ruleValue, value.Trim())));
}

public class TrackRule
{
    /// <summary>Case-insensitive substrings matched against the roster track's Key and Name, e.g. ["catalunya", "barcelona"].</summary>
    public List<string> Match { get; set; } = new();
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    /// <summary>Only for races of this series (F1, NASCAR, ...). Null = any series. Keeps e.g. an F1 "miami"
    /// rule from matching NASCAR's Homestead-Miami Speedway.</summary>
    public string? Category { get; set; }

    /// <summary>AC track folder. Null = no AC track known yet (only the download search is offered).</summary>
    public string? Folder { get; set; }
    public string? Layout { get; set; }
    public bool Kunos { get; set; }
    public string? Url { get; set; }
    public string? Search { get; set; }
    public string? Note { get; set; }

    public bool Matches(RosterTrack track, int? year, string? category = null)
    {
        if (Category != null && !string.Equals(Category, category, StringComparison.OrdinalIgnoreCase)) return false;
        if (YearFrom != null && (year == null || year < YearFrom)) return false;
        if (YearTo != null && (year == null || year > YearTo)) return false;
        // Accents ignored: "nurburgring" matches "Nürburgring".
        return Match.Any(m => TextFold.Contains(track.Key, m) || TextFold.Contains(track.Name, m) || TextFold.Contains(track.Locality, m));
    }
}
