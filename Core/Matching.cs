using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>The distinctive words of an installed car's name, used to match it to the real car.</summary>
public static class CarNames
{
    /// <summary>Pack, class and series words that say nothing about which car it is.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "acf", "hy", "gt", "gt1", "gt2", "gt3", "gt4", "gte", "gtd", "lmp", "lmp1", "lmp2", "lmp3", "lmh", "lmdh", "hypercar",
        "supercars", "supercar", "v8", "atcc", "vasc", "imsa", "wec", "elms", "race", "racing", "version", "spec", "edition",
        "mod", "by", "the", "team", "series", "car", "cars", "pack", "and", "de", "of", "championship", "season",
    };

    /// <summary>
    /// "ACF HY - Ferrari 499P 2023" -> [499p]; "ACF GT3 - BMW M4 GT3 2021" -> [m4, gt3];
    /// "ATCC V8 Supercars 2003 - Ford BA" -> [ford, ba]; "Porsche 963" -> [963]. Empty when nothing distinctive is left.
    /// </summary>
    public static List<string> ModelWords(CarInfo car)
    {
        var name = car.Name;
        // Packs put their own name first: "ACF HY - Ferrari 499P 2023".
        var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash < name.Length - 3) name = name[(dash + 3)..];
        name = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", " ");   // "(AJ4 White Cement Spec)"
        name = Regex.Replace(name, @"'\d{2}\b", " ");               // "'26"

        var brand = CarRule.Words(car.Brand);
        static bool IsYear(string w) => w.Length == 4 && int.TryParse(w, out var y) && y is >= 1950 and <= 2039;
        var words = CarRule.Words(name).Where(w => !brand.Contains(w) && !Noise.Contains(w) && !IsYear(w)).ToList();
        // Model codes (with digits) are the most telling; then the rest, in name order.
        var pick = words.Where(w => w.Any(char.IsDigit)).Concat(words.Where(w => !w.Any(char.IsDigit))).Take(3).ToList();
        if (!pick.Any(w => w.Length >= 2)) return new List<string>();
        // GT3 and GT4 versions of the same model ("BMW M4 GT3" / "BMW M4 GT4") are different cars.
        foreach (var cls in new[] { "gt3", "gt4" })
            if (CarRule.Words(car.Name).Contains(cls)) pick.Add(cls);
        return pick;
    }
}

/// <summary>Stock cars: the make from the model name, the car generation, and the track-type versions some mods ship.</summary>
public static class StockCars
{
    private static readonly (string Pattern, string Make)[] Makes =
    {
        // Ram trucks ("Dodge RAM 1500"): NASCAR's results call the make "RAM" (2026 Truck Series), so this comes before Dodge.
        (@"\bram\b", "RAM"),
        (@"\bchevrolet\b|\bchevy\b|camaro|impala|monte ?carlo|lumina", "Chevrolet"),
        (@"\bford\b|fusion|mustang|taurus|thunderbird", "Ford"),
        (@"toyota|camry|\bsupra\b", "Toyota"),
        (@"\bdodge\b|charger|intrepid|avenger|challenger", "Dodge"),
        (@"pontiac|grand ?prix", "Pontiac"),
    };

    /// <summary>The make of a Cup/Xfinity/Truck stock car, or null (ARCA and made-up cars aren't these series).</summary>
    public static string? Make(CarInfo car)
    {
        var text = TextFold.Fold($"{car.Name} {car.Brand} {car.Folder}").Replace('_', ' ');
        if (Regex.IsMatch(text, @"\barca\b|\bhss\b")) return null;
        foreach (var (pattern, make) in Makes)
            if (Regex.IsMatch(text, pattern)) return make;
        return null;
    }

    /// <summary>A pickup-truck body (Truck Series) rather than a Cup/Xfinity car.</summary>
    public static bool IsTruck(CarInfo car, string make)
        => make == "RAM" || Regex.IsMatch(TextFold.Fold($"{car.Name} {car.Folder}").Replace('_', ' '),
               @"\b(silverado|f ?-?150|tundra|sierra|truck|pickup)\b");

    /// <summary>How NASCAR's results name each make's truck ("2026 Chevrolet Silverado", "2026 RAM 1500").</summary>
    private static string TruckModel(string make) => make switch
    {
        "Chevrolet" => "Silverado", "Ford" => "F-150", "Toyota" => "Tundra", "RAM" => "RAM", "Dodge" => "Ram", _ => make,
    };

    /// <summary>Words in a real entry's car that say it's a truck: Cup/Xfinity car mods never stand for these.</summary>
    private static readonly List<string> TruckWords = new() { "silverado", "f-150", "f150", "tundra", "ram ", "sierra" };

    /// <summary>"rc" (road course), "ss" (superspeedway), "st" (short track) or "" (intermediate / the general version).</summary>
    public static string Variant(CarInfo car)
    {
        var m = Regex.Match(car.Folder.ToLowerInvariant(), @"_(rc|ss|st|it)$");
        if (!m.Success) m = Regex.Match(car.Name.ToLowerInvariant(), @"\s(rc|ss|st|it)$");
        return m.Success && m.Groups[1].Value != "it" ? m.Groups[1].Value : "";
    }

    /// <summary>The car generation a model year belongs to (a 2025 car is the 2022-on Next Gen car).</summary>
    private static (int From, int To) Generation(int year) => year switch
    {
        >= 2022 => (2022, 2030),
        >= 2013 => (2013, 2021),
        >= 2007 => (2007, 2012),
        >= 1992 => (1992, 2006),
        >= 1981 => (1981, 1991),
        _ => (year - 2, year + 2),
    };

    private static List<string>? TracksFor(string variant, int generationFrom) => variant switch
    {
        "rc" => new() { "road course", "roval", "sonoma", "infineon", "watkins glen", "road america", "circuit of the americas",
                        "mid-ohio", "chicago street", "hermanos rodriguez", "mexico city", "portland", "gilles villeneuve" },
        "st" => new() { "martinsville", "bristol", "richmond", "north wilkesboro", "phoenix", "new hampshire", "loudon",
                        "iowa", "dover", "bowman gray", "nashville fairgrounds" },
        "ss" => generationFrom >= 2022 ? new() { "daytona", "talladega", "atlanta" } : new() { "daytona", "talladega" },
        _ => null,
    };

    /// <summary>
    /// One rule per make, track-type version and season of each generation you have a car for: the installed car with
    /// the nearest model year. Road-course versions come before superspeedway ones (Daytona's road course is "daytona" too).
    /// </summary>
    public static IEnumerable<CarRule> Rules(List<(CarInfo Car, string Make, string Variant)> stock)
    {
        var order = new[] { "rc", "st", "ss", "" };
        var rules = new List<CarRule>();
        // Trucks and cars are different vehicles: a truck mod is only used for that make's truck, a Cup/Xfinity car mod
        // never for a truck (a 2026 Chevrolet Camaro mod isn't a 2026 Chevrolet Silverado).
        foreach (var group in stock.GroupBy(s => (s.Make, s.Variant, Truck: IsTruck(s.Car, s.Make), Gen: Generation(s.Car.Year!.Value)))
                     .OrderBy(g => Array.IndexOf(order, g.Key.Variant)))
        {
            var (from, to) = group.Key.Gen;
            to = Math.Min(to, DateTime.UtcNow.Year + 1);
            for (var year = from; year <= to; year++)
            {
                var best = group.OrderBy(s => Math.Abs(s.Car.Year!.Value - year)).ThenByDescending(s => s.Car.Year).First();
                rules.Add(new CarRule
                {
                    Category = "NASCAR", YearFrom = year, YearTo = year,
                    Car = group.Key.Truck ? TruckModel(group.Key.Make) : group.Key.Make,
                    Exclude = group.Key.Truck ? null : TruckWords,
                    Tracks = TracksFor(group.Key.Variant, from),
                    Model = best.Car.Folder, Kunos = false,
                    Note = $"Installed: {best.Car.Name} (from scan{(group.Key.Variant.Length > 0 ? ", " + group.Key.Variant.ToUpperInvariant() + " version" : "")})",
                });
            }
        }
        return rules;
    }
}

/// <summary>
/// Matches each real circuit in the race database to an installed track and layout, by the words of their names,
/// towns and folders. Each rule names the real circuit exactly, so it can't catch another circuit.
/// </summary>
public static class TrackMatcher
{
    /// <summary>Words that say what kind of place or which series it is, not which circuit.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "nascar", "track", "formula", "oval", "layout", "version", "wec", "imsa", "gtwc", "day", "night", "raceway",
        "circuit", "circuito", "autodrom", "autodromo", "speedway", "superspeedway", "motor", "motorsport", "motorsports",
        "international", "internacional", "park", "street", "course", "road", "grand", "prix", "the", "del", "des", "and",
        "ring", "race", "racing", "sports", "car", "cup", "series", "full", "new",
    };

    private static HashSet<string> Words(string? text)
        => ContentLibrary.Tokens(TextFold.Fold(text).Replace('_', ' ').Replace('-', ' '))
            .Where(w => w.Length >= 3 && !Noise.Contains(w) && !w.All(char.IsDigit)).ToHashSet();

    /// <summary>A mod folder's words without its author prefix ("aa_bristol" -> bristol, "acu_interlagos" -> interlagos).</summary>
    private static HashSet<string> InstalledWords(TrackInfo t)
    {
        var folder = Regex.Replace(t.Folder, @"^[A-Za-z0-9]{1,4}_", "");
        return Words($"{t.Name} {t.City} {folder}");
    }

    /// <summary>The real circuit's words; NASCAR's numeric ids ("nascar-track-14") say nothing about the circuit.</summary>
    private static HashSet<string> KeyWords(RosterTrack track)
        => track.Key == null || track.Key.StartsWith("nascar-track", StringComparison.OrdinalIgnoreCase) ? new() : Words(track.Key);

    private static bool IsOval(string text) => Regex.IsMatch(text, @"speedway|\boval\b");
    private static bool IsStreet(string text) => Regex.IsMatch(text, @"\bstreet\b|\bstrip\b|\bstadt\b");

    /// <summary>
    /// How well an installed track's words match a real circuit's (higher is better), 0 when it isn't that circuit:
    /// most of the real name's words, or the results API's own circuit id ("interlagos"), or nearly all of the mod's
    /// words plus half of the real ones. Ovals never match road or street circuits, and the other way round.
    /// </summary>
    private static double Score(HashSet<string> real, HashSet<string> key, string realText, bool stockCars, TrackInfo t)
    {
        var installedText = TextFold.Fold($"{t.Name} {t.City} {t.Folder} {t.Layout}");
        if (IsOval(realText) && IsStreet(installedText)) return 0;
        if (IsStreet(realText) && IsOval(installedText) && !IsStreet(installedText)) return 0;
        // A sports-car or F1 circuit isn't an oval mod (Road Atlanta isn't Atlanta Motor Speedway).
        var nameText = TextFold.Fold($"{t.Name} {t.Folder}");
        if (!stockCars && !IsOval(realText) && IsOval(nameText)) return 0;
        // ...and an oval isn't a road circuit mod (Atlanta Motor Speedway isn't Road Atlanta).
        if (IsOval(realText) && !Regex.IsMatch(realText, @"road course|roval")
            && Regex.IsMatch(nameText, @"\broad\b|grand ?prix|\bgp\b") && !IsOval(nameText)) return 0;

        var mine = InstalledWords(t);
        var common = real.Where(mine.Contains).ToList();
        if (!common.Any(w => w.Length >= 4)) return 0;
        // The match has to come from the track's own name or folder, not just its town field.
        var named = Words($"{t.Name} {Regex.Replace(t.Folder, @"^[A-Za-z0-9]{1,4}_", "")}");
        if (!common.Any(w => w.Length >= 4 && named.Contains(w))) return 0;
        double Weight(string w) => w.Length >= 5 ? 3 : w.Length == 4 ? 2 : 1;
        var realCover = common.Sum(Weight) / real.Sum(Weight);
        var mineCover = common.Sum(Weight) / Math.Max(1, mine.Sum(Weight));
        var keyHit = key.Any(w => w.Length >= 4 && mine.Contains(w));
        var ok = realCover >= 0.6 || keyHit || (mineCover >= 0.75 && realCover >= 0.5);
        return ok ? realCover + mineCover + (keyHit ? 1 : 0) : 0;
    }

    /// <summary>The layout that suits the races held there: oval / road course for stock cars, GP for F1, endurance for sports cars.</summary>
    private static double LayoutBonus(TrackInfo t, string realText, IReadOnlyCollection<string?> categories)
    {
        var l = TextFold.Fold($"{t.Layout} {t.Name}");
        double b = 0;
        var roadCourse = Regex.IsMatch(realText, @"road course|roval");
        if (roadCourse) b += Regex.IsMatch(l, @"road|roval|\brc\b|sports ?car|24 ?h|rolex|\bgp\b|\bf1\b") ? 5 : -3;
        else if (categories.Contains("NASCAR"))
            b += Regex.IsMatch(l, @"road|roval|course|\bgp\b|\bf1\b") ? -3 : Regex.IsMatch(l, @"oval|nascar|speedway|brickyard") ? 3 : 0;
        if (categories.Contains("F1")) b += Regex.IsMatch(l, @"\bgp\b|grand ?prix|\bf1\b|\bfull\b") ? 3 : 0;
        if (categories.Any(c => c is "LMP1" or "Hypercar" or "GT1" or "GT3" or "GTE" or "Group C" or "GT4" or "Sportscar"))
            b += Regex.IsMatch(l, @"endurance|24 ?h|\bwec\b|\bfull\b|\bgp\b") ? 3 : 0;
        if (Regex.IsMatch(l, @"drift|drag|rally|kart|reverse|\bpit\b|parking|touge|national|club|short|sprint|demo")) b -= 4;
        if (Regex.IsMatch(l, @"(19|20)\d\d")) b -= 1; // season-specific variants: the plain layout first
        return b;
    }

    public static List<TrackRule> Rules(ContentLibrary lib, IEnumerable<Roster> rosters, IProgress<string> log)
    {
        // One rule per real circuit AND series: Indianapolis is an oval for NASCAR but the road course for F1.
        var real = rosters.Where(r => r.Track is { Name.Length: > 0 })
            .GroupBy(r => (r.Track!.Name, r.Category))
            .Select(g => (Track: g.First().Track!, Category: g.Key.Category))
            .ToList();
        var rules = new List<TrackRule>();
        foreach (var (track, category) in real)
        {
            var realText = TextFold.Fold($"{track.Name} {track.Key}");
            var words = Words($"{track.Name} {track.Locality}");
            var key = KeyWords(track);
            words.UnionWith(key);
            if (words.Count == 0) continue;
            var categories = new[] { category };

            // Best folder by name match, then the layout that suits the races held there.
            var scored = lib.Tracks.Select(t => (Info: t, Score: Score(words, key, realText, category == "NASCAR", t)))
                .Where(x => x.Score > 0).ToList();
            if (scored.Count == 0) continue;
            // A folder with a layout suited to these races beats an equally named one without (an oval for stock cars).
            var folder = scored.GroupBy(x => x.Info.Folder, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Max(x => x.Score + 0.3 * LayoutBonus(x.Info, realText, categories))).First();
            var best = folder.OrderByDescending(x => LayoutBonus(x.Info, realText, categories)).ThenByDescending(x => x.Score).First().Info;
            rules.Add(new TrackRule
            {
                Match = new List<string> { track.Name },
                Category = category,
                Folder = best.Folder,
                Layout = best.Layout,
                Note = $"Installed: {best.Name} (from scan)",
            });
        }
        // Rules match by name substring: "Daytona International Speedway road course" must come before the oval's rule.
        return rules.OrderByDescending(r => r.Match[0].Length).ToList();
    }
}
