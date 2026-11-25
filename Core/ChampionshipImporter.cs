using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>
/// Reads Content Manager championships (*.champ, as shipped in season packs and saved in
/// Documents\Assetto Corsa\champs) and turns them into catalog rules:
///   - one rule per seat: this driver, in this season, drives this exact car folder with this livery;
///   - one rule per round that matches a real race of that season: the pack author's track and layout.
/// Rounds whose layout is from a later era than the season (e.g. a 2023 layout in a 2006 season) are
/// skipped, so nothing "close enough" gets in.
/// </summary>
public static class ChampionshipImporter
{
    public record ChampSeat(string Name, string Car, string? Skin, string? Nationality);
    public record ChampRound(string Track);
    public record Champ(string File, string Name, int? Year, List<ChampSeat> Seats, List<string> Rounds, List<string> SlashRounds);

    public record Report(string File, string Name, int? Year, string? Category, int SeatRules, int DriversFound, int Seats,
        int TrackRules, List<string> Notes);

    public static IEnumerable<string> FindFiles(string path)
    {
        if (File.Exists(path)) return new[] { path };
        if (!Directory.Exists(path)) return Array.Empty<string>();
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".champ", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    public static Champ? Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file), RosterJson.Lenient);
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : Path.GetFileNameWithoutExtension(file);

        var seats = new List<ChampSeat>();
        if (root.TryGetProperty("opponents", out var opp) && opp.ValueKind == JsonValueKind.Array)
            foreach (var o in opp.EnumerateArray())
            {
                var driver = o.Str("name") ?? "";
                var car = o.Str("car");
                if (car == null) continue;
                seats.Add(new ChampSeat(driver, car, o.Str("skin"), o.Str("nationality")));
            }

        var rounds = new List<string>();
        if (root.TryGetProperty("rounds", out var rr) && rr.ValueKind == JsonValueKind.Array)
            rounds.AddRange(rr.EnumerateArray().Select(r => r.Str("track")).Where(t => t != null).Cast<string>());

        // CM's companion .champ.json writes tracks as "folder/layout", which removes the dash ambiguity.
        var slash = new List<string>();
        var companion = file + ".json";
        if (File.Exists(companion))
        {
            try
            {
                using var cdoc = JsonDocument.Parse(File.ReadAllText(companion), RosterJson.Lenient);
                if (cdoc.RootElement.TryGetProperty("rounds", out var cr) && cr.ValueKind == JsonValueKind.Array)
                    slash.AddRange(cr.EnumerateArray().Select(r => r.Str("track")).Where(t => t != null).Cast<string>());
            }
            catch { /* companion is optional */ }
        }

        var year = YearIn(name) ?? YearIn(Path.GetFileNameWithoutExtension(file)) ?? YearIn(Path.GetDirectoryName(file));
        return new Champ(file, name, year, seats, rounds, slash);
    }

    /// <summary>
    /// "FAlonso" -> "Alonso", "Michael Schumacher" -> "Michael Schumacher", "PLAYER" -> null.
    /// Full names are kept so that Michael and Ralf Schumacher stay apart.
    /// </summary>
    public static string? DriverKey(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Equals("player", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains(' ')) return Regex.Replace(name, "\\s+", " ");
        // Split glued initials/camel case: "FAlonso" -> "F Alonso", "JButton" -> "J Button".
        var parts = Regex.Matches(name, "[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+").Select(m => m.Value).ToList();
        var surname = parts.LastOrDefault(p => p.Length >= 3);
        return surname;
    }

    /// <summary>Folder/layout for a round. CM's .champ joins them with "-", which is ambiguous with dashed folder names,
    /// so the installed tracks decide; the .champ.json "folder/layout" form is used when available.</summary>
    public static (string Folder, string? Layout)? SplitTrack(string round, string? slashForm, string content)
    {
        if (slashForm != null)
        {
            var i = slashForm.IndexOf('/');
            return i < 0 ? (slashForm, null) : (slashForm[..i], slashForm[(i + 1)..]);
        }
        var tracks = Path.Combine(content, "tracks");
        if (Directory.Exists(Path.Combine(tracks, round)) && !round.Contains('-')) return (round, null);
        for (var i = round.IndexOf('-'); i > 0; i = round.IndexOf('-', i + 1))
        {
            var folder = round[..i];
            var layout = round[(i + 1)..];
            if (Directory.Exists(Path.Combine(tracks, folder, layout))) return (folder, layout);
        }
        if (Directory.Exists(Path.Combine(tracks, round))) return (round, null);
        return null;
    }

    public static int? YearIn(string? text)
    {
        if (text == null) return null;
        var m = Regex.Match(text, "(?<!\\d)(19[5-9]\\d|20[0-3]\\d)(?!\\d)");
        return m.Success ? int.Parse(m.Value) : null;
    }

    /// <summary>Builds the catalog from all championships. Races are the real rosters, used to match rounds and check drivers.</summary>
    public static (ContentCatalog Catalog, List<Report> Reports) Import(IEnumerable<string> files, string content, IReadOnlyList<Roster> races)
    {
        var lib = Directory.Exists(Path.Combine(content, "cars")) ? ContentLibrary.Load(content) : null;
        var catalog = new ContentCatalog();
        var reports = new List<Report>();
        var seenCars = new HashSet<string>();
        var seenTracks = new HashSet<string>();

        foreach (var file in files)
        {
            var notes = new List<string>();
            Champ? champ;
            try { champ = Read(file); }
            catch (Exception ex) { reports.Add(new Report(file, Path.GetFileName(file), null, null, 0, 0, 0, 0, new() { "Unreadable: " + ex.Message })); continue; }
            if (champ == null) continue;

            // Category: what most of its cars are.
            var category = champ.Seats
                .Select(s => lib?.Cars.FirstOrDefault(c => c.Folder.Equals(s.Car, StringComparison.OrdinalIgnoreCase)) is { } info
                    ? info.Category
                    : ContentLibrary.Classify(s.Car, null, Array.Empty<string>(), null, champ.Year, s.Car))
                .Where(c => c != null)
                .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

            // Season: from the championship name, else the cars' year.
            var year = champ.Year ?? champ.Seats
                .Select(s => lib?.Cars.FirstOrDefault(c => c.Folder.Equals(s.Car, StringComparison.OrdinalIgnoreCase))?.Year)
                .Where(y => y != null).GroupBy(y => y).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

            if (year == null)
            {
                reports.Add(new Report(file, champ.Name, null, category, 0, 0, champ.Seats.Count, 0, new() { "No season year in its name or cars; skipped." }));
                continue;
            }

            var seasonRaces = races.Where(r => r.Year == year && (category == null || r.Category == category)).ToList();
            var seasonDrivers = seasonRaces.SelectMany(r => r.Entries).Select(e => e.Driver).Distinct().ToList();

            // Seats.
            int seatRules = 0, found = 0, seats = 0;
            foreach (var s in champ.Seats)
            {
                var key = DriverKey(s.Name);
                if (key == null) continue; // the player's own seat
                seats++;
                if (seasonDrivers.Any(d => TextFold.Contains(d, key))) found++;
                else if (seasonRaces.Count > 0) notes.Add($"Driver '{s.Name}' isn't in the {year} results (rule kept; check the name).");
                if (!seenCars.Add($"{year}|{TextFold.Fold(key)}|{s.Car.ToLowerInvariant()}")) continue;
                catalog.Cars.Add(new CarRule
                {
                    Category = category,
                    YearFrom = year,
                    YearTo = year,
                    Driver = key,
                    Model = s.Car,
                    Skin = string.IsNullOrWhiteSpace(s.Skin) ? null : s.Skin,
                    Kunos = s.Car.StartsWith("ks_", StringComparison.OrdinalIgnoreCase),
                    Note = $"From championship '{champ.Name}'",
                });
                seatRules++;
            }

            // Rounds -> real races of that season.
            var trackRules = 0;
            var usedRaces = new HashSet<string>();
            for (var i = 0; i < champ.Rounds.Count; i++)
            {
                var round = champ.Rounds[i];
                var slash = i < champ.SlashRounds.Count ? champ.SlashRounds[i] : null;
                var split = SplitTrack(round, slash, content);
                if (split == null) { notes.Add($"Round '{round}': track not installed and folder/layout ambiguous; skipped."); continue; }
                var (folder, layout) = split.Value;

                // Era check: a layout dated after the season is a later version of the circuit.
                var dated = YearIn($"{folder} {layout}");
                if (dated != null && dated > year + 2)
                {
                    notes.Add($"Round '{folder}{(layout != null ? "/" + layout : "")}': {dated} layout for a {year} season; skipped (not period-correct).");
                    continue;
                }

                var info = lib?.Tracks.FirstOrDefault(t => t.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)
                                                          && string.Equals(t.Layout, layout, StringComparison.OrdinalIgnoreCase))
                           ?? lib?.Tracks.FirstOrDefault(t => t.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
                var words = ContentLibrary.Tokens($"{folder} {layout} {info?.Name} {info?.City}").Where(w => !IsYearOrNoise(w)).ToHashSet();

                var race = seasonRaces
                    .Where(r => r.Track != null && !usedRaces.Contains(r.Track.Key ?? r.Track.Name))
                    .Select(r => (Race: r, Score: ContentLibrary.Tokens($"{r.Track!.Name} {r.Track.Key} {r.Track.Locality}").Distinct().Count(w => SameWord(w, words))))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Race)
                    .FirstOrDefault();
                if (race == null) { notes.Add($"Round '{folder}{(layout != null ? "/" + layout : "")}': no {year} race at this circuit found; skipped."); continue; }

                var circuitKey = race.Track!.Key ?? race.Track.Name;
                usedRaces.Add(circuitKey);
                if (!seenTracks.Add($"{year}|{circuitKey}")) continue;
                catalog.Tracks.Add(new TrackRule
                {
                    Match = new List<string> { circuitKey },
                    YearFrom = year,
                    YearTo = year,
                    Category = category,
                    Folder = folder,
                    Layout = layout,
                    Kunos = folder.StartsWith("ks_", StringComparison.OrdinalIgnoreCase),
                    Note = $"From championship '{champ.Name}' ({race.Track.Name})",
                });
                trackRules++;
            }

            reports.Add(new Report(file, champ.Name, year, category, seatRules, found, seats, trackRules, notes));
        }
        return (catalog, reports);
    }

    private static bool IsYearOrNoise(string w) => YearIn(w) != null || w is "layout" or "v2" or "v3" or "f1";

    /// <summary>Same word, or one is the other plus a suffix for longer names ("hockenheim" / "hockenheimring").</summary>
    private static bool SameWord(string w, HashSet<string> words)
    {
        w = TextFold.Fold(w);
        foreach (var raw in words)
        {
            var c = TextFold.Fold(raw);
            if (c == w) return true;
            if (Math.Min(c.Length, w.Length) >= 5 && (c.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(c, StringComparison.Ordinal))) return true;
        }
        return false;
    }
}
