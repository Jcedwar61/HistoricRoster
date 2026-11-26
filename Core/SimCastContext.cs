using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoricRosterPlugin;

/// <summary>
/// Writes %APPDATA%\SimCast\race_context.json when a race is written, so SimCast's commentary booth knows the real
/// race behind it: the event, the real grid and result, the real race-day conditions. Only when SimCast is installed
/// (its settings folder exists). The format is described in HistoricRosterPlugin/SIMCAST_INTEGRATION.md (schema 1).
/// </summary>
public static class SimCastContext
{
    public const int Schema = 1;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimCast");

    public static string FilePath => Path.Combine(Folder, "race_context.json");

    public static bool SimCastInstalled => Directory.Exists(Folder);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the context for <paramref name="setup"/>. Returns the path, or null when SimCast isn't installed or the
    /// file couldn't be written (logged; never throws, a race must never fail over this).
    /// </summary>
    /// <param name="realConditions">The real race-day conditions, when they were looked up.</param>
    /// <param name="simCastInstalled">
    /// SimCast.exe was found: write even before SimCast's first run has made its settings folder. Without it, the folder
    /// alone decides.
    /// </param>
    /// <remarks>
    /// Compatibility: SimCast is updated on its own schedule, so this file is a fixed contract. Add fields freely
    /// (SimCast ignores what it doesn't know); never rename, remove or change the meaning of one without raising
    /// <see cref="Schema"/>, which older SimCast versions then ignore. SimCast versions without this feature never
    /// read the file at all.
    /// </remarks>
    public static string? TryWrite(RaceSetup setup, Conditions? realConditions, string? appVersion, Action<string> log,
        bool simCastInstalled = false)
    {
        try
        {
            if (!simCastInstalled && !SimCastInstalled) return null;
            Directory.CreateDirectory(Folder);
            var json = Serialize(Build(setup, realConditions, appVersion, DateTime.UtcNow));
            var path = FilePath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            log("SimCast race context not written: " + ex.Message);
            // The last race's file must not stand in for this one (same track within 12 hours).
            Clear(log);
            return null;
        }
    }

    /// <summary>
    /// Removes the last race's context, for a race written without one (the setting off, or the write failed): else
    /// SimCast would take it for this race when the track is the same and it's under 12 hours old. Only a file
    /// Historic Roster wrote is removed (SimCast never writes it). Returns true when one was removed; never throws.
    /// </summary>
    public static bool Clear(Action<string> log)
    {
        try
        {
            var path = FilePath;
            try { File.Delete(path + ".tmp"); } catch { /* a stuck .tmp is never read; the file itself matters */ }
            if (!File.Exists(path)) return false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                if (!doc.RootElement.TryGetProperty("source", out var src) || src.GetString() != "Historic Roster") return false;
            }
            File.Delete(path);
            log("Removed the last race's SimCast race context.");
            return true;
        }
        catch (Exception ex)
        {
            log("Couldn't remove the last race's SimCast race context: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// For each race written: the context when <paramref name="send"/> is on (and SimCast is on the PC), else the last
    /// race's context removed. Returns the path written, or null.
    /// </summary>
    public static string? Update(bool send, RaceSetup setup, Conditions? realConditions, string? appVersion,
        Action<string> log, bool simCastInstalled = false)
    {
        if (send && TryWrite(setup, realConditions, appVersion, log, simCastInstalled) is { } path) return path;
        if (!send) Clear(log);
        return null;
    }

    /// <summary>The file's text, exactly as written.</summary>
    public static string Serialize(ContextDoc doc) => JsonSerializer.Serialize(doc, Json);

    public static ContextDoc Build(RaceSetup s, Conditions? c, string? appVersion, DateTime utcNow)
    {
        var r = s.Roster;
        var cars = RaceIniWriter.CarOrder(s);
        var raceCars = s.Grid.Where(x => !x.IsSafetyCar).Select(x => x.Car.Entry).ToList();
        var multi = s.IsMultiClass;
        return new ContextDoc
        {
            Schema = Schema,
            Source = "Historic Roster",
            AppVersion = appVersion,
            // Invariant: under e.g. fi-FI the ':' becomes '.', under th-TH the year is Buddhist, and SimCast (which
            // parses exactly "%Y-%m-%dT%H:%M:%SZ") would then ignore the file.
            WrittenUtc = utcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Ac = new AcPart
            {
                Track = s.Track.Folder ?? "",
                Layout = s.Track.Layout ?? "",
                RaceLaps = s.Laps,
                Sessions = s.Weekend switch
                {
                    RaceWeekend.QualifyingAndRace => new() { "qualify", "race" },
                    RaceWeekend.PracticeQualifyingAndRace => new() { "practice", "qualify", "race" },
                    _ => new() { "race" },
                },
                Watch = s.Watch,
            },
            Event = new EventPart
            {
                Name = r.Name,
                Series = r.Series,
                Category = r.Category,
                Year = r.Year,
                Date = r.Date,
                Circuit = r.Track?.Name,
                Locality = r.Track?.Locality,
                RealLaps = RaceDistance.RealLaps(r),
                Multiclass = multi,
                Classes = multi ? RaceClasses.Ordered(raceCars) : new(),
            },
            RealConditions = c == null ? null : new ConditionsPart
            {
                StartLocal = c.LocalStart.ToString(@"hh\:mm"),
                StartKnown = c.StartKnown,
                AmbientC = (int)Math.Round(c.AirTemp),
                Kind = c.Kind,
                Night = c.IsNight,
                Summary = c.Describe(),
            },
            Player = new PlayerPart { CarIndex = 0, SeatDriver = cars[0].Car.Entry.Driver },
            Grid = cars.Select((slot, i) => (slot, i)).Where(x => !x.slot.IsSafetyCar).Select(x =>
            {
                var e = x.slot.Car.Entry;
                return new CarPart
                {
                    CarIndex = x.i,
                    Driver = e.Driver,
                    Team = e.Team,
                    Number = e.Number,
                    Nation = e.Nation,
                    // A stand-in keeps the real entry, so Car is always the real car's name; AcModel is what's driven.
                    Car = e.Car,
                    AcModel = x.slot.Car.Model,
                    Class = multi ? RaceClasses.Of(e) : null,
                    StandInFor = x.slot.IsStandIn ? e.Car : null,
                    RealGrid = e.GridPosition,
                    RealPos = e.Position,
                    RealStatus = e.Status,
                    RealLaps = e.Laps,
                };
            }).ToList(),
            NotRacing = s.LeftOut.Select(m => new LeftOutPart
            {
                Driver = m.Entry.Driver,
                Team = m.Entry.Team,
                RealGrid = m.Entry.GridPosition,
                RealPos = m.Entry.Position,
            }).ToList(),
            SourceNote = r.Source,
        };
    }

    public class ContextDoc
    {
        public int Schema { get; set; }
        public string Source { get; set; } = "";
        public string? AppVersion { get; set; }
        public string WrittenUtc { get; set; } = "";
        public AcPart Ac { get; set; } = new();
        public EventPart Event { get; set; } = new();
        public ConditionsPart? RealConditions { get; set; }
        public PlayerPart Player { get; set; } = new();
        public List<CarPart> Grid { get; set; } = new();
        public List<LeftOutPart> NotRacing { get; set; } = new();
        public string? SourceNote { get; set; }
    }

    public class AcPart
    {
        public string Track { get; set; } = "";
        public string Layout { get; set; } = "";
        public int RaceLaps { get; set; }
        public List<string> Sessions { get; set; } = new();
        public bool Watch { get; set; }
    }

    public class EventPart
    {
        public string Name { get; set; } = "";
        public string? Series { get; set; }
        public string? Category { get; set; }
        public int? Year { get; set; }
        public string? Date { get; set; }
        public string? Circuit { get; set; }
        public string? Locality { get; set; }
        public int RealLaps { get; set; }
        public bool Multiclass { get; set; }
        public List<string> Classes { get; set; } = new();
    }

    public class ConditionsPart
    {
        public string StartLocal { get; set; } = "";
        public bool StartKnown { get; set; }
        public int AmbientC { get; set; }
        public string Kind { get; set; } = "";
        public bool Night { get; set; }
        public string Summary { get; set; } = "";
    }

    public class PlayerPart
    {
        public int CarIndex { get; set; }
        public string SeatDriver { get; set; } = "";
    }

    public class CarPart
    {
        public int CarIndex { get; set; }
        public string Driver { get; set; } = "";
        public string? Team { get; set; }
        public string? Number { get; set; }
        public string? Nation { get; set; }
        public string? Car { get; set; }
        public string? AcModel { get; set; }
        public string? Class { get; set; }
        public string? StandInFor { get; set; }
        public int? RealGrid { get; set; }
        public int? RealPos { get; set; }
        public string? RealStatus { get; set; }
        public int? RealLaps { get; set; }
    }

    public class LeftOutPart
    {
        public string Driver { get; set; } = "";
        public string? Team { get; set; }
        public int? RealGrid { get; set; }
        public int? RealPos { get; set; }
    }
}
