using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssettoServer.Server.Configuration;
using Serilog;

namespace HistoricRosterPlugin;

public record InstallResult(ActiveRoster Active, RosterContent Content, IReadOnlyList<string> Warnings, IReadOnlyList<string> Backups);

/// <summary>How a roster is written: grid order, race length, real conditions and stand-ins.</summary>
public record InstallOptions
{
    public GridOrder Order { get; init; } = GridOrder.StartingGrid;
    /// <summary>Race length in % of the real distance. 0 = leave the server's race length alone.</summary>
    public int LapsPercent { get; init; } = 100;
    /// <summary>Real race-day conditions to write, or null to leave time and weather alone.</summary>
    public Conditions? Conditions { get; init; }
    public StandInMode StandIns { get; init; } = StandInMode.Off;
}

/// <summary>
/// Writes a roster into the server config: entry_list.ini (cars, skins, names, teams, ballast, seat bookings),
/// server_cfg.ini (TRACK, CONFIG_TRACK, CARS, MAX_CLIENTS, race laps, sun angle, weather) and cm_content/content.json
/// (Content Manager download links). The AC server reads all of these at startup, so the roster takes effect on the
/// next restart.
/// </summary>
public class RosterInstaller
{
    private readonly HistoricRosterConfiguration _configuration;
    private readonly RosterStore _store;
    private readonly ContentResolver _resolver;
    private readonly bool _weatherFx;

    public string EntryListPath { get; }
    public string ServerCfgPath { get; }
    public string ContentJsonPath { get; }

    public RosterInstaller(HistoricRosterConfiguration configuration, ACServerConfiguration serverConfiguration,
        RosterStore store, ContentResolver resolver)
    {
        _configuration = configuration;
        _store = store;
        _resolver = resolver;
        _weatherFx = serverConfiguration.Extra.EnableWeatherFx;

        var baseFolder = serverConfiguration.BaseFolder;
        EntryListPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuration.EntryListPath)
            ? Path.Join(baseFolder, "entry_list.ini") : configuration.EntryListPath);
        ServerCfgPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuration.ServerCfgPath)
            ? Path.Join(baseFolder, "server_cfg.ini") : configuration.ServerCfgPath);
        // Same location AssettoServer reads it from (ConfigurationLocations.CMContentJsonPath).
        ContentJsonPath = Path.GetFullPath(Path.Join(Path.GetDirectoryName(ServerCfgPath)!, "cm_content", "content.json"));
    }

    /// <summary>Puts entries in grid order. Entries without the sort key keep their listed order, after the others.</summary>
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, RosterEntry> entry, GridOrder order)
    {
        var indexed = items.Select((x, i) => (Item: x, Index: i));
        return (order switch
        {
            GridOrder.StartingGrid => indexed.OrderBy(x => entry(x.Item).GridPosition is > 0 ? entry(x.Item).GridPosition : int.MaxValue).ThenBy(x => x.Index),
            GridOrder.FinishingOrder => indexed.OrderBy(x => entry(x.Item).Position ?? int.MaxValue).ThenBy(x => x.Index),
            _ => indexed.OrderBy(x => x.Index),
        }).Select(x => x.Item).ToList();
    }

    public InstallResult Install(Roster roster, InstallOptions options)
    {
        var warnings = new List<string>();
        var backups = new List<string>();
        var catalog = _resolver.LoadCatalog();
        var content = _resolver.Resolve(roster, catalog);

        // The real circuit must be installed: never race a historic grid somewhere else.
        // (Rosters captured on this server or built from installed cars may have no track: then the current track stays.)
        if (_configuration.LoadTrack && roster.Track != null && content.Track is { Installed: false } missingTrack)
            throw new InvalidOperationException(missingTrack.Folder == null
                ? $"The AC track for '{roster.Track.Name}' isn't known yet. Install it ({missingTrack.DownloadUrl}), then add it to the content catalog."
                : $"Track {missingTrack.Label} isn't installed on the server. Get it: {missingTrack.DownloadUrl}");

        var maxSlots = _configuration.MaxSlots > 0 ? _configuration.MaxSlots : content.Track?.PitBoxes ?? 0;

        var library = options.StandIns == StandInMode.SameClass ? _resolver.Library : null;
        if (options.StandIns == StandInMode.SameClass && library == null)
            warnings.Add("Stand-ins are on, but the server's content folder has no cars/ folder to pick them from.");
        var picker = library != null ? new StandInPicker(content, library) : null;
        var bookings = _store.GetSeats().Where(b => b.RosterId == roster.Id).ToList();

        var slots = new List<ActiveSlot>();
        var written = new List<ResolvedCar>();
        var ini = new StringBuilder();
        ini.AppendLine($"; Generated by HistoricRosterPlugin from roster '{Clean(roster.Id)}'");
        ini.AppendLine($"; {Clean(roster.Name)} - written {DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)}");
        ini.AppendLine($"; Grid order: {options.Order}");
        ini.AppendLine();

        var standIns = 0;
        foreach (var real in Order(content.Cars, c => c.Entry, options.Order))
        {
            var car = real;
            string? standInFor = null;
            if (!real.Installed)
            {
                // Only the real car, unless stand-ins are on and an installed car of the same class exists.
                if (picker?.Pick(real) is { } standIn)
                {
                    car = standIn;
                    standInFor = real.Entry.Car ?? real.Label;
                    standIns++;
                }
                else
                {
                    warnings.Add(real.Model == null
                        ? $"Left out {real.Entry.Driver}: no AC folder known for {real.Label} ({real.DownloadUrl})"
                        : $"Left out {real.Entry.Driver}: {real.Model} isn't installed ({real.DownloadUrl})");
                    continue;
                }
            }
            if (maxSlots > 0 && slots.Count >= maxSlots)
            {
                warnings.Add($"Grid limited to {maxSlots} cars (track pit boxes / MaxSlots); the rest were left out.");
                break;
            }

            var entry = car.Entry;
            var cls = StandInPicker.ClassOf(roster, entry);
            var ballast = entry.Ballast;
            var restrictor = entry.Restrictor;
            if (ballast == 0 && restrictor == 0 && catalog.BalanceFor(roster, entry, car.Model, cls) is { } balance)
            {
                ballast = balance.Ballast;
                restrictor = balance.Restrictor;
            }
            var booking = bookings.FirstOrDefault(b => b.IsFor(entry.Driver, entry.Number));

            var slot = slots.Count;
            ini.AppendLine($"[CAR_{slot}]");
            ini.AppendLine($"MODEL={Clean(car.Model!)}");
            ini.AppendLine($"SKIN={Clean(car.Skin ?? "")}");
            ini.AppendLine("SPECTATOR_MODE=0");
            // A booked seat carries the player's own name in place of the real driver's.
            ini.AppendLine($"DRIVERNAME={Clean(booking?.PlayerName ?? entry.Driver)}");
            ini.AppendLine($"TEAM={Clean(BuildTeam(entry))}");
            ini.AppendLine($"GUID={Clean(booking?.Guid ?? entry.Guid ?? "")}");
            ini.AppendLine($"BALLAST={ballast.ToString(CultureInfo.InvariantCulture)}");
            ini.AppendLine($"RESTRICTOR={restrictor.ToString(CultureInfo.InvariantCulture)}");
            ini.AppendLine();

            written.Add(car);
            slots.Add(new ActiveSlot
            {
                Slot = slot, Model = car.Model!, Skin = car.Skin, Driver = entry.Driver,
                Team = entry.Team, Number = entry.Number, Car = entry.Car, Class = entry.Class,
                RealGrid = entry.GridPosition, RealPosition = entry.Position, RealStatus = entry.Status,
                StandInFor = standInFor, RosterGuid = entry.Guid,
            });
        }

        if (slots.Count == 0)
            throw new InvalidOperationException("No cars could be written: none of the real cars is installed on the server. Install them (RosterTool install, or the links from /roster_content) and add missing ones to the content catalog."
                                                + (options.StandIns == StandInMode.Off ? " Or set StandIns: SameClass in the plugin config." : ""));
        if (standIns > 0)
            warnings.Insert(0, $"{standIns} car(s) are stand-ins of the same class; the real cars are listed by /roster_content.");

        // entry_list.ini
        Backup(EntryListPath, backups);
        WriteAtomic(EntryListPath, ini.ToString());

        // server_cfg.ini: CARS (allowed models) and MAX_CLIENTS must match the new entry list; the rest is optional.
        var track = content.Track;
        int? laps = null;
        string? conditions = null;
        if (!File.Exists(ServerCfgPath))
        {
            warnings.Add($"server_cfg.ini not found at {ServerCfgPath}; set CARS, MAX_CLIENTS and TRACK by hand.");
        }
        else
        {
            Backup(ServerCfgPath, backups);
            var cfg = File.ReadAllText(ServerCfgPath);
            cfg = SetIniValue(cfg, "SERVER", "CARS", string.Join(';', slots.Select(s => s.Model).Distinct(StringComparer.OrdinalIgnoreCase)));
            cfg = SetIniValue(cfg, "SERVER", "MAX_CLIENTS", slots.Count.ToString(CultureInfo.InvariantCulture));
            if (_configuration.LoadTrack)
            {
                if (track?.Folder == null)
                    warnings.Add(roster.Track == null
                        ? "Roster has no track; server track left unchanged."
                        : $"No AC track known for '{roster.Track.Name}'; server track left unchanged. Add it to the catalog.");
                else
                {
                    cfg = SetIniValue(cfg, "SERVER", "TRACK", track.Folder);
                    cfg = SetIniValue(cfg, "SERVER", "CONFIG_TRACK", track.Layout ?? "");
                }
            }

            // Race length: a share of the real distance.
            if (options.LapsPercent > 0)
            {
                if (!RaceDistance.Known(roster))
                    warnings.Add("The roster has no lap counts; race length left unchanged.");
                else
                {
                    laps = RaceDistance.LapsFor(roster, options.LapsPercent);
                    if (!HasSection(cfg, "RACE"))
                        cfg = AppendSection(cfg, "RACE", ("NAME", "Race"), ("LAPS", "1"), ("TIME", "0"), ("WAIT_TIME", "60"), ("IS_OPEN", "1"));
                    cfg = SetIniValue(cfg, "RACE", "LAPS", laps.Value.ToString(CultureInfo.InvariantCulture));
                    cfg = SetIniValue(cfg, "RACE", "TIME", "0");
                }
            }

            // Real start time, date and weather.
            if (options.Conditions is { } c)
            {
                cfg = WriteConditions(cfg, c, roster, warnings);
                conditions = c.Describe();
            }

            // Qualifying or practice before the race re-orders the grid by best lap, unless the plugin puts the real grid back.
            if (options.Order == GridOrder.StartingGrid && !_configuration.KeepRealGrid
                                                        && (HasSection(cfg, "QUALIFY") || HasSection(cfg, "PRACTICE")))
                warnings.Add("Practice/qualifying will set the race grid by best lap, not the real grid. Turn KeepRealGrid on, or remove [QUALIFY]/[PRACTICE] from server_cfg.ini.");

            WriteAtomic(ServerCfgPath, cfg);
        }

        // cm_content/content.json
        if (_configuration.WriteContentManagerLinks)
        {
            Backup(ContentJsonPath, backups);
            WriteAtomic(ContentJsonPath, BuildContentJson(written, track));
        }

        var active = new ActiveRoster
        {
            RosterId = roster.Id,
            RosterName = roster.Name,
            WrittenAtUtc = DateTime.UtcNow,
            EntryListPath = EntryListPath,
            TrackFolder = _configuration.LoadTrack && File.Exists(ServerCfgPath) ? track?.Folder : null,
            TrackLayout = _configuration.LoadTrack && File.Exists(ServerCfgPath) ? track?.Layout : null,
            Order = options.Order,
            Laps = laps,
            Conditions = conditions,
            Slots = slots,
        };
        _store.SetActive(active);

        Log.Information("HistoricRoster: installed roster {Roster}: {Count} cars, track {Track}. Restart the server to apply",
            roster.Id, slots.Count, track?.Label ?? "unchanged");
        foreach (var warning in warnings) Log.Warning("HistoricRoster: {Warning}", warning);
        foreach (var (label, url) in content.Missing()) Log.Warning("HistoricRoster: missing on server: {Item} -> {Url}", label, url);

        return new InstallResult(active, content, warnings, backups);
    }

    /// <summary>
    /// SUN_ANGLE plus a single [WEATHER_0]. With WeatherFX the weather carries the real local start time, the race
    /// date (for the sun's path) and rain; without it only Kunos dry weathers and 08:00-18:00 are possible.
    /// </summary>
    private string WriteConditions(string cfg, Conditions c, Roster roster, List<string> warnings)
    {
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var hours = c.LocalStart.TotalHours;
        cfg = SetIniValue(cfg, "SERVER", "SUN_ANGLE", F(Math.Clamp((hours - 13) * 16, -80, 80)));

        string graphics;
        if (_weatherFx)
        {
            graphics = $"3_clear_type={c.WeatherFxType}_time={(int)c.LocalStart.TotalSeconds}";
            if (DateOnly.TryParse(roster.Date, CultureInfo.InvariantCulture, out var date))
            {
                // The sun follows the time of year; dates before 1970 use the same day in 1970.
                if (date.Year < 1970) date = new DateOnly(1970, date.Month, Math.Min(date.Day, DateTime.DaysInMonth(1970, date.Month)));
                var unix = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
                graphics += $"_start={unix}";
            }
        }
        else
        {
            graphics = c.KunosWeather;
            if (c.Wet) warnings.Add($"It was {c.Kind} at the real race, but WeatherFX is off (EnableWeatherFx in extra_cfg.yml), so it stays dry.");
            if (hours < 8 || hours > 18) warnings.Add($"The real start was {c.LocalStart:hh\\:mm}; without WeatherFX the server can only do 08:00-18:00.");
        }

        cfg = RemoveSections(cfg, s => s.StartsWith("WEATHER_", StringComparison.OrdinalIgnoreCase));
        return AppendSection(cfg, "WEATHER_0",
            ("GRAPHICS", graphics),
            ("BASE_TEMPERATURE_AMBIENT", F(Math.Clamp(Math.Round(c.AirTemp), 0, 45))),
            ("BASE_TEMPERATURE_ROAD", F(c.RoadOffset)),
            ("VARIATION_AMBIENT", "0"),
            ("VARIATION_ROAD", "0"),
            ("WIND_BASE_SPEED_MIN", "3"),
            ("WIND_BASE_SPEED_MAX", "8"),
            ("WIND_BASE_DIRECTION", "0"),
            ("WIND_VARIATION_DIRECTION", "30"));
    }

    /// <summary>
    /// Re-writes the GUID of every slot of the written roster: a player's seat booking, else the roster's own
    /// reservation. Takes effect on the next restart.
    /// </summary>
    public void ApplyBookings(ActiveRoster active)
    {
        if (!File.Exists(EntryListPath)) throw new InvalidOperationException($"entry_list.ini not found at {EntryListPath}.");
        var bookings = _store.GetSeats().Where(b => b.RosterId == active.RosterId).ToList();
        var ini = File.ReadAllText(EntryListPath);
        foreach (var slot in active.Slots)
        {
            var booked = bookings.FirstOrDefault(b => b.IsFor(slot.Driver, slot.Number));
            ini = SetIniValue(ini, $"CAR_{slot.Slot}", "GUID", Clean(booked?.Guid ?? slot.RosterGuid ?? ""));
            ini = SetIniValue(ini, $"CAR_{slot.Slot}", "DRIVERNAME", Clean(booked?.PlayerName ?? slot.Driver));
        }
        WriteAtomic(EntryListPath, ini);
    }

    /// <summary>Restores the newest backups of entry_list.ini, server_cfg.ini and content.json.</summary>
    public IReadOnlyList<string> RestoreLatestBackups()
    {
        var restored = new List<string>();
        foreach (var path in new[] { EntryListPath, ServerCfgPath, ContentJsonPath })
        {
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) continue;
            var latest = Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".*.bak")
                // Newest by the yyyyMMdd-HHmmss in the name (File.Copy keeps the old write time).
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest == null) continue;
            File.Copy(latest, path, overwrite: true);
            restored.Add(latest);
            Log.Information("HistoricRoster: restored {Backup} to {Path}", latest, path);
        }
        return restored;
    }

    /// <summary>Content Manager's content.json: a download link per car folder written (stand-ins included) and for the track.</summary>
    private string BuildContentJson(List<ResolvedCar> written, ResolvedTrack? track)
    {
        var cars = new JsonObject();
        foreach (var car in written.GroupBy(c => c.Model!, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            // A stand-in's DownloadUrl is the real car's; link the stand-in car itself.
            var url = car.Match == "stand-in"
                ? car.Kunos ? _configuration.KunosContentUrl : _resolver.SearchUrl(car.Model!)
                : car.DownloadUrl;
            cars[car.Model!] = new JsonObject { ["url"] = url };
        }

        var root = new JsonObject { ["cars"] = cars };
        if (track?.Folder != null) root["track"] = new JsonObject { ["url"] = track.DownloadUrl };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Sets KEY=value inside [SECTION], adding the key (or the section) if missing. Keeps everything else.</summary>
    public static string SetIniValue(string ini, string section, string key, string value)
    {
        var newline = ini.Contains("\r\n") ? "\r\n" : "\n";
        var lines = ini.Replace("\r\n", "\n").Split('\n').ToList();
        var inSection = false;
        var sectionEnd = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inSection) { sectionEnd = i; break; }
                inSection = string.Equals(trimmed[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);
                if (inSection) sectionEnd = lines.Count;
                continue;
            }
            if (!inSection) continue;
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && string.Equals(trimmed[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join(newline, lines);
            }
        }

        if (sectionEnd < 0)
        {
            lines.Insert(0, $"[{section}]");
            lines.Insert(1, $"{key}={value}");
        }
        else
        {
            // Insert before trailing blank lines of the section.
            var insertAt = sectionEnd;
            while (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
            lines.Insert(insertAt, $"{key}={value}");
        }
        return string.Join(newline, lines);
    }

    public static bool HasSection(string ini, string section)
        => ini.Replace("\r\n", "\n").Split('\n').Any(l => IsHeader(l, out var name) && string.Equals(name, section, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes whole sections (header and keys) whose name matches.</summary>
    public static string RemoveSections(string ini, Func<string, bool> match)
    {
        var newline = ini.Contains("\r\n") ? "\r\n" : "\n";
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in ini.Replace("\r\n", "\n").Split('\n'))
        {
            if (IsHeader(line, out var name)) skipping = match(name);
            if (!skipping) kept.Add(line);
        }
        return string.Join(newline, kept);
    }

    /// <summary>Adds a section with its keys at the end of the file.</summary>
    public static string AppendSection(string ini, string section, params (string Key, string Value)[] keys)
    {
        var newline = ini.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder(ini.TrimEnd('\r', '\n'));
        sb.Append(newline).Append(newline).Append('[').Append(section).Append(']').Append(newline);
        foreach (var (k, v) in keys) sb.Append(k).Append('=').Append(v).Append(newline);
        return sb.ToString();
    }

    private static bool IsHeader(string line, out string name)
    {
        var t = line.Trim();
        name = t.Length > 2 && t[0] == '[' && t[^1] == ']' ? t[1..^1].Trim() : "";
        return name.Length > 0;
    }

    private void Backup(string path, List<string> backups)
    {
        if (!_configuration.BackupFiles || !File.Exists(path)) return;
        var backup = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(path, backup, overwrite: true);
        backups.Add(backup);
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string BuildTeam(RosterEntry entry)
    {
        var number = string.IsNullOrWhiteSpace(entry.Number) ? "" : $"#{entry.Number.Trim()} ";
        return (number + (entry.Team ?? "")).Trim();
    }

    /// <summary>Keeps values on one INI line.</summary>
    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
