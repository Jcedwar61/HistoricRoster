using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

public record SkinInfo(string Folder, string? Driver, string? Team, string? Number);

public record CarInfo(string Folder, string Name, string? Brand, int? Year, string? Category, IReadOnlyList<string> Tags,
    IReadOnlyList<SkinInfo> Skins)
{
    public bool Kunos => Folder.StartsWith("ks_", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Latitude">From ui_track.json "geotags" (degrees north), when the track gives a real position.</param>
public record TrackInfo(string Folder, string? Layout, string Name, string? City, string? Country, int? PitBoxes,
    double? Latitude = null, double? Longitude = null);

/// <summary>
/// Index of the cars and tracks installed in an AC content folder, read from their ui_car.json,
/// ui_skin.json and ui_track.json files. Used by RosterTool scan/find and to pick the right livery of a real car.
/// </summary>
public class ContentLibrary
{
    public string ContentFolder { get; }
    public IReadOnlyList<CarInfo> Cars { get; }
    public IReadOnlyList<TrackInfo> Tracks { get; }

    private ContentLibrary(string contentFolder, IReadOnlyList<CarInfo> cars, IReadOnlyList<TrackInfo> tracks)
    {
        ContentFolder = contentFolder;
        Cars = cars;
        Tracks = tracks;
    }

    // ------------------------------------------------------------------ loading

    private static readonly object CacheLock = new();
    private static (string Folder, DateTime Stamp, ContentLibrary Library)? _cache;

    /// <summary>Loads (and caches until cars/ or tracks/ change) the library for a content folder.</summary>
    public static ContentLibrary Load(string contentFolder)
    {
        contentFolder = Path.GetFullPath(contentFolder);
        var stamp = Stamp(contentFolder);
        lock (CacheLock)
        {
            if (_cache is { } c && c.Folder == contentFolder && c.Stamp == stamp) return c.Library;
            var library = new ContentLibrary(contentFolder, ScanCars(contentFolder), ScanTracks(contentFolder));
            _cache = (contentFolder, stamp, library);
            return library;
        }
    }

    private static DateTime Stamp(string contentFolder)
    {
        var cars = Path.Combine(contentFolder, "cars");
        var tracks = Path.Combine(contentFolder, "tracks");
        var a = Directory.Exists(cars) ? Directory.GetLastWriteTimeUtc(cars) : DateTime.MinValue;
        var b = Directory.Exists(tracks) ? Directory.GetLastWriteTimeUtc(tracks) : DateTime.MinValue;
        return a > b ? a : b;
    }

    private static List<CarInfo> ScanCars(string contentFolder)
    {
        var list = new List<CarInfo>();
        var carsDir = Path.Combine(contentFolder, "cars");
        if (!Directory.Exists(carsDir)) return list;

        foreach (var dir in Directory.EnumerateDirectories(carsDir))
        {
            var folder = Path.GetFileName(dir);
            var ui = ReadUi(Path.Combine(dir, "ui", "ui_car.json"));
            var name = Str(ui, "name") ?? folder;
            var brand = Str(ui, "brand");
            var tags = StrArray(ui, "tags");
            var year = Int(ui, "year") ?? YearFromText(name) ?? YearFromText(folder);
            var category = Classify(name, brand, tags, Str(ui, "class"), year, folder);

            var skins = new List<SkinInfo>();
            var skinsDir = Path.Combine(dir, "skins");
            if (Directory.Exists(skinsDir))
            {
                foreach (var skinDir in Directory.EnumerateDirectories(skinsDir))
                {
                    var s = ReadUi(Path.Combine(skinDir, "ui_skin.json"));
                    skins.Add(new SkinInfo(Path.GetFileName(skinDir), Str(s, "drivername"), Str(s, "team"), Str(s, "number")));
                }
            }
            list.Add(new CarInfo(folder, name, brand, year, category, tags, skins));
        }
        return list;
    }

    private static List<TrackInfo> ScanTracks(string contentFolder)
    {
        var list = new List<TrackInfo>();
        var tracksDir = Path.Combine(contentFolder, "tracks");
        if (!Directory.Exists(tracksDir)) return list;

        foreach (var dir in Directory.EnumerateDirectories(tracksDir))
        {
            var folder = Path.GetFileName(dir);
            var uiDir = Path.Combine(dir, "ui");
            var single = Path.Combine(uiDir, "ui_track.json");
            // A track can have a default layout (ui\ui_track.json) AND extra layouts (ui\<layout>\ui_track.json),
            // e.g. Monza with added period layouts: list all of them.
            if (File.Exists(single)) list.Add(TrackFrom(folder, null, ReadUi(single)));
            if (!Directory.Exists(uiDir)) continue;
            foreach (var layoutUi in Directory.EnumerateDirectories(uiDir))
            {
                var layout = Path.GetFileName(layoutUi);
                var json = Path.Combine(layoutUi, "ui_track.json");
                if (File.Exists(json) && Directory.Exists(Path.Combine(dir, layout)))
                    list.Add(TrackFrom(folder, layout, ReadUi(json)));
            }
        }
        return list;
    }

    private static TrackInfo TrackFrom(string folder, string? layout, JsonElement? ui)
    {
        var (lat, lon) = Geotags(ui);
        return new(folder, layout, Str(ui, "name") ?? folder, Str(ui, "city"), Str(ui, "country"), Int(ui, "pitboxes"), lat, lon);
    }

    /// <summary>
    /// ui_track.json "geotags": ["32° 40′ 41″ N", "117° 10′ 21″ W"], ["45.6156 N", "9.2811 E"], ["45.6156", "9.2811"]
    /// or numbers. Placeholders ("lat", "lon") and out-of-range values give nulls.
    /// </summary>
    public static (double? Lat, double? Lon) Geotags(JsonElement? ui)
    {
        if (ui is not { ValueKind: JsonValueKind.Object } o) return (null, null);
        foreach (var p in o.EnumerateObject())
        {
            if (!p.Name.Equals("geotags", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Array) continue;
            var parts = p.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetRawText() : x.GetString() ?? "").ToList();
            if (parts.Count < 2) return (null, null);
            var lat = Coordinate(parts[0], 'S');
            var lon = Coordinate(parts[1], 'W');
            if (lat is not (>= -90 and <= 90) || lon is not (>= -180 and <= 180) || (lat == 0 && lon == 0)) return (null, null);
            return (lat, lon);
        }
        return (null, null);
    }

    private static double? Coordinate(string text, char negative)
    {
        var nums = Regex.Matches(text, @"-?\d+(?:[.,]\d+)?").Select(m => double.Parse(m.Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) return null;
        var value = Math.Abs(nums[0]) + (nums.Count > 1 ? nums[1] / 60 : 0) + (nums.Count > 2 ? nums[2] / 3600 : 0);
        var neg = nums[0] < 0 || text.TrimEnd().EndsWith(negative.ToString(), StringComparison.OrdinalIgnoreCase)
                  || Regex.IsMatch(text, $@"\b{negative}\b", RegexOptions.IgnoreCase);
        return neg ? -value : value;
    }

    // ------------------------------------------------------------------ classification

    /// <summary>Works out the racing category of a car from its name, brand, tags and class.</summary>
    public static string? Classify(string name, string? brand, IReadOnlyList<string> tags, string? cls, int? year, string folder = "")
    {
        var text = $" {name} {brand} {cls} {string.Join(' ', tags)} {folder.Replace('_', ' ')} ".ToLowerInvariant().Replace("#", " ");
        var nameText = $" {name} {folder.Replace('_', ' ')} ".ToLowerInvariant();
        bool Has(string pattern) => Regex.IsMatch(text, pattern);
        bool NameHas(string pattern) => Regex.IsMatch(nameText, pattern);
        var street = string.Equals(cls, "street", StringComparison.OrdinalIgnoreCase);

        if (Has(@"\brally\b|\bwrc\b|\bdrift\b") && !Has(@"nascar")) return null;
        if (Has(@"nascar|stock ?car|cup series|winston cup|sprint cup|xfinity|\barca\b|\bgen ?[67]\b|next ?gen cup")) return "NASCAR";
        if (street) return null; // road cars, including road "hypercars"
        // Australian touring cars (V8 Supercars / ATCC / Supercars Championship).
        if (Has(@"\bv8 ?supercars?\b|\bsupercars\b|\batcc\b|\bvasc\b|#v8sc")) return "Supercars";
        // F1 car packs named after the season ("Grand Prix 1998 F300", "GrandPrix 1950-60s").
        if (Has(@"\bgrand ?prix ?(19[5-9]\d|20[0-3]\d)\b|\bgrandprix ?19[5-9]0")) return "F1";
        if (Has(@"\bgt1\b|lmgt1|\bf1 gtr\b|\bgt-?1\b|clk[- ]?gtr|clk[- ]?lm|\bmc12\b|911 gt1")) return "GT1";
        if (Has(@"\blmp2\b|\blmp3\b")) return "LMP2";
        if (Has(@"\blmh\b|\blmdh\b|\bgtp\b|le mans hypercar")) return "Hypercar";
        if (Has(@"\blmp1\b|lmp900|919 hybrid|\br18\b|\bts0[45]0\b|audi r10|peugeot 908|\blmp\b") && (year == null || year >= 1995)) return "LMP1";
        // Kunos tags both GT3 and GTE cars "#GTE-GT3": decide by the name first.
        if (NameHas(@"\bgt3\b") && !NameHas(@"gt3 ?rs\b|gt3 cup|\bcup\b")) return "GT3";
        if (NameHas(@"\bgte\b|lmgte|\bgt2\b|\bgtlm\b|\brsr\b|\bc7\.?r\b|\bc6\.?r\b")) return "GTE";
        if (Has(@"\bgt3\b") && !Has(@"gt3 ?rs\b|gt3 cup")) return "GT3";
        if (Has(@"\bgte\b|lmgte|\bgtlm\b")) return "GTE";
        if (Has(@"\bf1\b|formula ?1\b|formula one|\bf20\d\d\b|\bsf\d\d-?[th]\b|\bmp4[-/ ]?\d{1,2}\b|\bfw\d\d\b")
            && !Has(@"mclaren f1\b(?! ?\d)|\bf1 road\b")) return "F1";
        if (Has(@"open ?wheel|formula") && year is >= 1950) return "Formula";
        return null;
    }

    // ------------------------------------------------------------------ matching

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "of", "de", "la", "le", "di", "do", "del", "and", "team", "racing", "motorsport", "motorsports", "sport",
        "f1", "gt", "gt1", "gt3", "lmp1", "car", "edition", "evo", "spec", "version", "v2", "v3", "circuit", "international",
        "raceway", "speedway", "motor", "park", "grand", "prix", "gp", "autodromo", "autódromo", "ring", "track", "road",
        "course", "layout", "full", "national", "2", "1",
    };

    public static IEnumerable<string> Tokens(string? text)
        => Regex.Split((text ?? "").ToLowerInvariant().Replace('_', ' '), "[^\\p{L}\\p{N}]+")
            .Where(t => t.Length >= 2 && !NoiseWords.Contains(t));

    /// <summary>
    /// The livery of an installed car that belongs to this entry: same race number, then driver surname, then team.
    /// Only picks between liveries of the real car itself; null when none fits.
    /// </summary>
    public string? SkinFor(string model, RosterEntry entry)
    {
        var car = Cars.FirstOrDefault(c => c.Folder.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (car == null || car.Skins.Count == 0) return null;

        var surname = entry.Driver.Split('/')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var teamTokens = Tokens(entry.Team).ToHashSet();
        string? bestSkin = null;
        var bestScore = 0;
        foreach (var s in car.Skins)
        {
            var score = 0;
            if (entry.Number != null && s.Number != null && s.Number.Trim().TrimStart('#') == entry.Number.Trim()) score += 50;
            if (surname.Length >= 3 && s.Driver != null && s.Driver.Contains(surname, StringComparison.OrdinalIgnoreCase)) score += 40;
            if (surname.Length >= 3 && s.Folder.Contains(surname, StringComparison.OrdinalIgnoreCase)) score += 25;
            score += Tokens(s.Team).Count(teamTokens.Contains) * 10;
            // A real livery beats a generated one that matches just as well.
            if (score > 0 && !IsGenerated(s.Folder)) score += 1;
            if (score > bestScore) { bestScore = score; bestSkin = s.Folder; }
        }
        return bestSkin;
    }

    /// <summary>
    /// The livery that is this driver's own: same race number or the driver's surname in the livery. Null when the
    /// car only has other drivers' liveries (then <see cref="SkinFor"/> falls back to a teammate's or the first one).
    /// </summary>
    public string? OwnSkin(string model, RosterEntry entry)
    {
        var car = Cars.FirstOrDefault(c => c.Folder.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (car == null) return null;
        var surname = entry.Driver.Split('/')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        // Real liveries before generated ones (folders starting with "hr_").
        var skins = car.Skins.OrderBy(s => IsGenerated(s.Folder) ? 1 : 0).ToList();
        return skins.FirstOrDefault(s =>
                entry.Number != null && s.Number != null && s.Number.Trim().TrimStart('#') == entry.Number.Trim().TrimStart('#'))?.Folder
            ?? skins.FirstOrDefault(s => surname.Length >= 3 &&
                ((s.Driver != null && s.Driver.Contains(surname, StringComparison.OrdinalIgnoreCase))
                 || s.Folder.Contains(surname, StringComparison.OrdinalIgnoreCase)))?.Folder;
    }

    /// <summary>A livery made by Historic Roster's generator (its folders start with "hr_").</summary>
    public static bool IsGenerated(string skinFolder) => skinFolder.StartsWith("hr_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Forget the cached index (after liveries were added: a new skin folder doesn't change content\cars).</summary>
    public static void Invalidate()
    {
        lock (CacheLock) _cache = null;
    }

    // ------------------------------------------------------------------ lenient ui_*.json reading

    private static JsonElement? ReadUi(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // AC ui files often contain raw tabs/newlines inside strings, which strict JSON rejects.
            var text = File.ReadAllText(path).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ");
            using var doc = JsonDocument.Parse(text, RosterJson.Lenient);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return null;
        foreach (var p in o.EnumerateObject())
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var v = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        return null;
    }

    private static int? Int(JsonElement? e, string name)
        => int.TryParse(Str(e, name), out var v) ? v : null;

    private static IReadOnlyList<string> StrArray(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return Array.Empty<string>();
        foreach (var p in o.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                return p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
        return Array.Empty<string>();
    }

    private static int? YearFromText(string text)
    {
        // Also finds years glued to letters/underscores: "F2004", "f1_1950_ferrari_375".
        var m = Regex.Match(text, "(?<!\\d)(19[5-9]\\d|20[0-3]\\d)(?!\\d)");
        return m.Success ? int.Parse(m.Value) : null;
    }
}
