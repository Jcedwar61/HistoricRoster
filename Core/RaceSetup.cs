using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HistoricRosterPlugin;

public enum RaceWeekend
{
    /// <summary>Race only, from the real starting grid.</summary>
    RaceOnly,
    /// <summary>Qualifying, then the race (the grid comes from your qualifying).</summary>
    QualifyingAndRace,
    /// <summary>Practice, qualifying, then the race.</summary>
    PracticeQualifyingAndRace,
}

/// <summary>One car on the offline grid: an installed real car (or a stand-in for it) with its driver.</summary>
public class RaceSlot
{
    /// <summary>The car that is actually driven. For a stand-in, an installed car of the same class.</summary>
    public required ResolvedCar Car { get; init; }
    /// <summary>The real car, when it isn't installed and <see cref="Car"/> stands in for it.</summary>
    public ResolvedCar? RealCar { get; init; }
    public bool IsStandIn => RealCar != null;
    /// <summary>1-based position on the starting grid.</summary>
    public int GridPosition { get; set; }
    public bool IsPlayer { get; set; }
    public bool IsSafetyCar { get; init; }
    /// <summary>This AI driver's own level (real results); null = the race's level.</summary>
    public double? AiLevel { get; set; }
    public string Driver => IsSafetyCar ? "Safety Car" : Car.Entry.Driver;
}

/// <summary>Everything that goes into race.ini for an offline race against AI.</summary>
public class RaceSetup
{
    public required Roster Roster { get; init; }
    public required ResolvedTrack Track { get; init; }
    /// <summary>Installed real cars in grid order (player included), then the Safety Car if added.</summary>
    public required List<RaceSlot> Grid { get; init; }
    /// <summary>Real entries left out because their car isn't installed.</summary>
    public required List<ResolvedCar> LeftOut { get; init; }

    public RaceWeekend Weekend { get; set; } = RaceWeekend.RaceOnly;
    public int Laps { get; set; } = 10;
    public int PracticeMinutes { get; set; } = 20;
    public int QualifyingMinutes { get; set; } = 15;
    /// <summary>AI level of the fastest driver (or of everyone, without <see cref="RealSkill"/>).</summary>
    public double AiLevel { get; set; } = 95;
    public double AiAggression { get; set; } = 50;
    /// <summary>Spread AI levels by the drivers' real results: the best real driver gets AiLevel, the worst AiLevel - SkillSpread.</summary>
    public bool RealSkill { get; set; } = true;
    public double SkillSpread { get; set; } = 6;
    /// <summary>Local start time at the circuit, any time of day or night (night needs Custom Shaders Patch).</summary>
    public TimeSpan TimeOfDay { get; set; } = new(14, 0, 0);
    public string Weather { get; set; } = "3_clear";

    /// <summary>Real race day: sets the season's sun path (CSP __CM_DATE).</summary>
    public DateOnly? RaceDate { get; set; }
    /// <summary>The circuit's real position and time zone; the sun is placed from these (CSP).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>The circuit's UTC offset on race day (with daylight saving), seconds.</summary>
    public int? UtcOffsetSeconds { get; set; }
    /// <summary>IANA time zone, e.g. "America/Los_Angeles", for the base offset and daylight saving.</summary>
    public string? TimeZone { get; set; }
    /// <summary>
    /// CSP WeatherFX weather type (ac.WeatherType: 15 clear, 7 rain ...) from the real race-day weather, shown by the
    /// weather controller (Pure, Sol, CSP's base). Null = the weather folder in <see cref="Weather"/> as picked.
    /// </summary>
    public int? WeatherFxType { get; set; }
    /// <summary>Weather controller for <see cref="WeatherFxType"/> ("pureCtrl", "base", ...); null = keep race.ini's, else "base".</summary>
    public string? WeatherController { get; set; }
    public int AmbientTemperature { get; set; } = 22;
    public int RoadTemperature { get; set; } = 30;
    public bool Penalties { get; set; } = true;

    public RaceSlot Player => Grid.First(s => s.IsPlayer);

    /// <summary>Your driver name. It replaces the real driver of the seat you take; null = keep the name in race.ini.</summary>
    public string? PlayerName { get; set; }

    /// <summary>
    /// Watch only: the AI drives every car, your seat included (the Historic Roster in-game app turns on the CSP
    /// autopilot at the start). Your seat keeps its real driver's name and is the car the camera starts on.
    /// </summary>
    public bool Watch { get; set; }

    /// <summary>The name shown for a slot: yours for your seat, else the real driver's.</summary>
    public string NameOf(RaceSlot s) => s.IsPlayer && !Watch && !string.IsNullOrWhiteSpace(PlayerName) ? PlayerName!.Trim() : s.Driver;

    /// <summary>
    /// The real grid from a resolved roster, in real grid order, pole sitter as player. Real cars that aren't installed
    /// get a stand-in (an installed car of the same class) when <paramref name="standIns"/> is on and a library is given;
    /// otherwise they are left out. The missing real cars stay in <see cref="RosterContent.Missing"/> either way.
    /// </summary>
    public static RaceSetup Create(RosterContent content, int maxCars, ContentLibrary? library = null, bool standIns = false)
    {
        if (content.Track is not { Installed: true, Folder: not null } track)
            throw new InvalidOperationException("The real track isn't installed.");

        var ordered = RosterOrder.Order(content.Cars, c => c.Entry, GridOrder.StartingGrid);
        var filler = standIns && library != null ? new StandInPicker(content, library) : null;

        var grid = new List<RaceSlot>();
        var leftOut = new List<ResolvedCar>();
        foreach (var car in ordered)
        {
            if (grid.Count >= Math.Max(1, maxCars)) { leftOut.Add(car); continue; }
            if (car.Installed) grid.Add(new RaceSlot { Car = car, GridPosition = grid.Count + 1 });
            else if (filler?.Pick(car) is { } standIn) grid.Add(new RaceSlot { Car = standIn, RealCar = car, GridPosition = grid.Count + 1 });
            else leftOut.Add(car);
        }
        if (grid.Count == 0) throw new InvalidOperationException("None of the real cars is installed.");

        grid[0].IsPlayer = true;
        var setup = new RaceSetup
        {
            Roster = content.Roster,
            Track = track,
            Grid = grid,
            LeftOut = leftOut,
            Laps = RealLaps(content.Roster),
        };
        return setup;
    }

    /// <summary>The real race distance in laps (the winner's laps).</summary>
    public static int RealLaps(Roster r) => RaceDistance.RealLaps(r);

    /// <summary>Laps for a share of the real distance (25, 50, 100 %), at least 1.</summary>
    public static int LapsFor(Roster r, int percent) => RaceDistance.LapsFor(r, percent);

    /// <summary>
    /// Sets each AI driver's level from the real race: drivers are ranked by finishing position (60 %) and starting
    /// position (40 %); the best gets AiLevel and the worst AiLevel - SkillSpread. In a multi-class race drivers are
    /// ranked within their class, so every class has its own fastest driver (a GT class winner who finished 20th
    /// overall is still a top GT driver). Without RealSkill everyone gets AiLevel.
    /// </summary>
    public void ApplySkill()
    {
        foreach (var s in Grid) s.AiLevel = null;
        if (!RealSkill) return;
        var drivers = Grid.Where(s => !s.IsSafetyCar).ToList();
        var groups = IsMultiClass ? drivers.GroupBy(s => RaceClasses.Key(s.Car.Entry)).Select(g => g.ToList()).ToList()
                                  : new List<List<RaceSlot>> { drivers };
        foreach (var group in groups) RankSkill(group);
    }

    private void RankSkill(List<RaceSlot> drivers)
    {
        var n = drivers.Count;
        if (n < 2) return;
        double Rank(int? v, IEnumerable<int?> all)
        {
            var known = all.Where(x => x is > 0).Select(x => x!.Value).Order().ToList();
            return v is > 0 ? known.IndexOf(v.Value) : known.Count + 0.5;
        }
        var finishes = drivers.Select(s => s.Car.Entry.Position).ToList();
        var grids = drivers.Select(s => s.Car.Entry.GridPosition).ToList();
        var ranked = drivers
            .Select(s => (Slot: s, Score: 0.6 * Rank(s.Car.Entry.Position, finishes) + 0.4 * Rank(s.Car.Entry.GridPosition, grids)))
            .OrderBy(x => x.Score).ToList();
        for (var i = 0; i < n; i++)
            ranked[i].Slot.AiLevel = Math.Clamp(Math.Round(AiLevel - SkillSpread * i / (n - 1), 1), 50, 100);
    }

    /// <summary>Two or more classes on the grid (LMP and GT ...).</summary>
    public bool IsMultiClass => RaceClasses.IsMultiClass(Grid.Where(s => !s.IsSafetyCar).Select(s => s.Car.Entry));

    /// <summary>The AI level written for a slot.</summary>
    public double LevelOf(RaceSlot s) => s.AiLevel ?? AiLevel;

    public void SetPlayer(RaceSlot slot)
    {
        foreach (var s in Grid) s.IsPlayer = ReferenceEquals(s, slot);
    }

    /// <summary>Sun angle for the start time: 16 degrees per hour from 13:00 (AC / Content Manager), not clamped.</summary>
    public double UnclampedSunAngle => (TimeOfDay.TotalHours - 13) * 16;

    /// <summary>SUN_ANGLE for vanilla AC, which only knows 08:00-18:00. CSP reads the unclamped one for other times.</summary>
    public double SunAngle => Math.Clamp(UnclampedSunAngle, -80, 80);

    /// <summary>Outside vanilla AC's 08:00-18:00: only Custom Shaders Patch can show it.</summary>
    public bool NeedsCspForTime => TimeOfDay.TotalHours < 8 || TimeOfDay.TotalHours > 18;
}

/// <summary>The weather controller to drive with the real race-day weather: the one already set in race.ini by Content
/// Manager (unless it's CSP's plain "base"), else Pure, else Sol, else CSP's base controller.</summary>
public static class WeatherControllers
{
    public static string Preferred(string acRoot, string? current)
    {
        var dir = Path.Combine(acRoot, "extension", "weather-controllers");
        if (!string.IsNullOrWhiteSpace(current) && !current.Equals("base", StringComparison.OrdinalIgnoreCase))
            return Static(dir, current);
        foreach (var id in new[] { "pureCtrl", "sol", "sol_controller" })
            if (Directory.Exists(Path.Combine(dir, id))) return Static(dir, id);
        return "base";
    }

    /// <summary>Pure's dynamic controller starts the Pure Planner app's last-used plan, and a Live plan moves the
    /// clock to the real current time at the track (a night race shown in daylight). "pureCtrl static" is Pure's
    /// controller for fixed weather: it keeps race.ini's start time and weather.</summary>
    static string Static(string dir, string id)
        => id.Equals("pureCtrl", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(dir, "pureCtrl static"))
            ? "pureCtrl static" : id;

    /// <summary>The controller race.ini has now (Content Manager writes it), or null.</summary>
    public static string? Current(string racePath)
        => File.Exists(racePath) ? IniDocument.Load(racePath).Get("LIGHTING", "__CM_WEATHER_CONTROLLER") : null;
}

/// <summary>
/// The real race day, place and conditions for a race setup, shared by the desktop program and RosterTool, so a
/// watched race and a raced one get the same real start time (night included), date, sun position and weather.
/// </summary>
public static class RealRaceTime
{
    /// <summary>The circuit's position: the roster's own, else the installed track's geotags.</summary>
    public static (double Lat, double Lon)? Position(Roster r, ResolvedTrack? t, ContentLibrary? lib)
    {
        if (r.Track?.Latitude is { } la && r.Track.Longitude is { } lo) return (la, lo);
        if (t?.Folder == null || lib == null) return null;
        var info = lib.Tracks.FirstOrDefault(x => x.Folder.Equals(t.Folder, StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(x.Layout ?? "", t.Layout ?? "", StringComparison.OrdinalIgnoreCase))
                   ?? lib.Tracks.FirstOrDefault(x => x.Folder.Equals(t.Folder, StringComparison.OrdinalIgnoreCase) && x.Latitude != null);
        return info?.Latitude is { } a && info.Longitude is { } b ? (a, b) : null;
    }

    /// <summary>The roster's full race day, when it has one (not just a year).</summary>
    public static DateOnly? Day(Roster r)
        => r.Date != null && DateOnly.TryParse(r.Date.Length >= 10 ? r.Date[..10] : r.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>The event's usual local start ("16:00" for Le Mans), when the roster has one.</summary>
    public static TimeSpan? UsualStart(Roster r)
        => r.UsualLocalTime != null && TimeSpan.TryParse(r.UsualLocalTime, CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>Real day and place only (the start time and weather stay as picked).</summary>
    public static void ApplyPlace(RaceSetup s, ContentLibrary? lib)
    {
        s.RaceDate = Day(s.Roster);
        var pos = Position(s.Roster, s.Track, lib);
        s.Latitude = pos?.Lat;
        s.Longitude = pos?.Lon;
        s.UtcOffsetSeconds = null;
        s.TimeZone = null;
        s.WeatherFxType = null;
    }

    /// <summary>Everything real: start time (any hour, night races too), day, place, time zone, weather and temperatures.</summary>
    public static void Apply(RaceSetup s, Conditions c, ContentLibrary? lib, string acRoot, string? raceIniPath)
    {
        ApplyPlace(s, lib);
        s.TimeOfDay = TimeSpan.FromMinutes(Math.Round(c.LocalStart.TotalMinutes));
        if (c.Date != null) s.RaceDate = c.Date;
        if (c.Latitude != null && c.Longitude != null) { s.Latitude = c.Latitude; s.Longitude = c.Longitude; }
        s.UtcOffsetSeconds = c.UtcOffsetSeconds;
        s.TimeZone = c.TimeZone;
        s.WeatherFxType = c.WeatherFxType;
        s.WeatherController = WeatherControllers.Preferred(acRoot, raceIniPath != null ? WeatherControllers.Current(raceIniPath) : null);
        s.AmbientTemperature = (int)Math.Round(c.AirTemp);
        s.RoadTemperature = s.AmbientTemperature + (c.IsNight ? 2 : c.RainMmPerHour > 0.2 ? 3 : 8);
    }
}

/// <summary>Grid ordering shared by the plugin, RosterTool and the desktop program.</summary>
public static class RosterOrder
{
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
}

/// <summary>
/// Writes an offline race into Documents\Assetto Corsa\cfg\race.ini the way Content Manager does: the existing
/// file is kept (backed up first) and only the race, session, car, light, weather and temperature sections are set.
/// The race session carries the player's real grid slot (STARTING_POSITION); the in-game app lays out the grid.
/// </summary>
public static class RaceIniWriter
{
    /// <summary>The driver name Assetto Corsa / Content Manager last used (race.ini [CAR_0] DRIVER_NAME), or null.</summary>
    public static string? ReadPlayerName(string racePath)
    {
        try
        {
            if (!File.Exists(racePath)) return null;
            var (name, _) = OwnName(IniDocument.Load(racePath));
            return string.IsNullOrWhiteSpace(name) || name == "Player" ? null : name.Trim();
        }
        catch (IOException) { return null; }
    }

    /// <summary>Section with the watch-only flag, read by the Historic Roster in-game app.</summary>
    public const string WatchSection = "HISTORIC_ROSTER";

    /// <summary>
    /// The player's own name and nationality in race.ini. After a watched race, CAR_0 carries the real driver's,
    /// and the player's own are kept in [HISTORIC_ROSTER].
    /// </summary>
    private static (string? Name, string? Nation) OwnName(IniDocument ini)
        => ini.Get(WatchSection, "WATCH") == "1"
            ? (ini.Get(WatchSection, "PLAYER_NAME"), ini.Get(WatchSection, "PLAYER_NATIONALITY"))
            : (ini.Get("CAR_0", "DRIVER_NAME"), ini.Get("CAR_0", "NATIONALITY"));

    /// <summary>
    /// Start time, date, place and weather, the way Content Manager writes them for Custom Shaders Patch:
    /// - SUN_ANGLE (vanilla AC, 08:00-18:00) and __CM_UNCLAMPED_SUN_ANGLE (CSP, any time: night races);
    /// - __CM_DATE: the real race day (season, sun path);
    /// - __TRACK_GEOTAG_LAT/LONG and __TRACK_TIMEZONE_*: the circuit's position and time zone, which CSP places the
    ///   sun with. Values left by an earlier race (another circuit) are always removed: when this race's aren't
    ///   known, CSP works them out from the track itself;
    /// - __CM_WEATHER_TYPE / __CM_WEATHER_CONTROLLER: the real race-day weather (rain included) for WeatherFX.
    /// </summary>
    private static void WriteTimeAndPlace(IniDocument ini, RaceSetup s)
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        ini.Set("LIGHTING", "SUN_ANGLE", F(s.SunAngle));
        ini.Set("LIGHTING", "__CM_UNCLAMPED_SUN_ANGLE", F(s.UnclampedSunAngle));
        ini.Set("LIGHTING", "TIME_MULT", "1");

        // Time zone on race day.
        TimeZoneInfo? tz = null;
        if (s.TimeZone != null) try { tz = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone); } catch { /* unknown id */ }
        var localStart = s.RaceDate?.ToDateTime(TimeOnly.FromTimeSpan(s.TimeOfDay));
        int? offset = s.UtcOffsetSeconds ?? (tz != null && localStart != null ? (int)tz.GetUtcOffset(localStart.Value).TotalSeconds : null);
        int? baseOffset = tz != null ? (int)tz.BaseUtcOffset.TotalSeconds : offset;

        // The real day and start: the local wall-clock start written as if it were UTC. CSP's WeatherFX takes the start
        // time of day from this value as track-local time (its log: "start time=46800 (… 13:00:00)"), so a true UTC
        // time here would start the race hours off (Spa 15:00 shown as 13:00).
        foreach (var k in new[] { "__CM_DATE", "__CM_DATE_USE_TIME" }) ini.Remove("LIGHTING", k);
        if (localStart != null)
        {
            var wallClock = new DateTimeOffset(DateTime.SpecifyKind(localStart.Value, DateTimeKind.Unspecified), TimeSpan.Zero);
            ini.Set("LIGHTING", "__CM_DATE", wallClock.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        // Position and time zone: this circuit's, or none (never an earlier race's).
        foreach (var k in new[] { "__TRACK_GEOTAG_LAT", "__TRACK_GEOTAG_LONG", "__TRACK_TIMEZONE_OFFSET", "__TRACK_TIMEZONE_BASE_OFFSET", "__TRACK_TIMEZONE_DTS" })
            ini.Remove("LIGHTING", k);
        if (s.Latitude != null && s.Longitude != null)
        {
            ini.Set("LIGHTING", "__TRACK_GEOTAG_LAT", s.Latitude.Value.ToString("0.######", CultureInfo.InvariantCulture));
            ini.Set("LIGHTING", "__TRACK_GEOTAG_LONG", s.Longitude.Value.ToString("0.######", CultureInfo.InvariantCulture));
        }
        if (offset != null)
        {
            ini.Set("LIGHTING", "__TRACK_TIMEZONE_OFFSET", offset.Value.ToString(CultureInfo.InvariantCulture));
            ini.Set("LIGHTING", "__TRACK_TIMEZONE_BASE_OFFSET", (baseOffset ?? offset.Value).ToString(CultureInfo.InvariantCulture));
            ini.Set("LIGHTING", "__TRACK_TIMEZONE_DTS", offset.Value != (baseOffset ?? offset.Value) ? "1" : "0");
        }

        // Weather: the real race-day weather type, or the weather folder as picked.
        if (s.WeatherFxType is { } type)
        {
            ini.Set("LIGHTING", "__CM_WEATHER_CONTROLLER", s.WeatherController ?? ini.Get("LIGHTING", "__CM_WEATHER_CONTROLLER") ?? "base");
            ini.Set("LIGHTING", "__CM_WEATHER_TYPE", type.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            // Content Manager's "this weather folder" mode.
            ini.Set("LIGHTING", "__CM_WEATHER_CONTROLLER", "base");
            ini.Set("LIGHTING", "__CM_WEATHER_TYPE", "-1");
        }
    }

    /// <summary>
    /// The cars in race.ini order: index i is [CAR_i] (and the AC car id). CAR_0 is the user's car (<see cref="UserCar"/>),
    /// then the grid in order, the Safety Car last. SimCastContext uses the same indexes, so keep this the only place
    /// that decides them.
    /// </summary>
    public static List<RaceSlot> CarOrder(RaceSetup setup)
    {
        var first = UserCar(setup);
        var cars = new List<RaceSlot> { first };
        cars.AddRange(setup.Grid.Where(s => !ReferenceEquals(s, first)).OrderBy(s => s.IsSafetyCar ? 1 : 0).ThenBy(s => s.GridPosition));
        return cars;
    }

    /// <summary>
    /// The car AC gives the user (CAR_0). Racing: your seat. Watching: the best real finisher, whatever seat the camera
    /// starts on ([HISTORIC_ROSTER] CAMERA_CAR). AC treats CAR_0 as the player's car even with the autopilot driving it:
    /// a pit stop there opens AC's pit menu and waits for you. So CAR_0 is a car that never retires, and the in-game app
    /// keeps it out of the pits; every other car (your seat's driver too) pits and retires on its own like any AI car.
    /// </summary>
    public static RaceSlot UserCar(RaceSetup setup)
        => setup.Watch
            ? setup.Grid.Where(s => !s.IsSafetyCar && s.Car.Entry.Position is > 0).OrderBy(s => s.Car.Entry.Position).FirstOrDefault() ?? setup.Player
            : setup.Player;

    public static string Write(RaceSetup setup, string racePath)
    {
        var ini = File.Exists(racePath) ? IniDocument.Load(racePath) : new IniDocument();

        // Keep the player's own name and nationality.
        var (myName, myNation) = OwnName(ini);

        if (File.Exists(racePath))
            File.Copy(racePath, $"{racePath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);

        ini.RemoveSections(s => s.StartsWith("CAR_", StringComparison.OrdinalIgnoreCase)
                                || s.StartsWith("SESSION_", StringComparison.OrdinalIgnoreCase)
                                || s.Equals(WatchSection, StringComparison.OrdinalIgnoreCase));
        var ownName = !string.IsNullOrWhiteSpace(setup.PlayerName) ? setup.PlayerName!.Trim() : myName ?? "Player";
        if (setup.Watch)
        {
            // AC ignores unknown sections; the in-game app turns on the autopilot when it sees WATCH=1.
            ini.Set(WatchSection, "WATCH", "1");
            ini.Set(WatchSection, "PLAYER_NAME", ownName);
            ini.Set(WatchSection, "PLAYER_NATIONALITY", myNation ?? "");
        }

        var cars = CarOrder(setup);
        var player = cars[0]; // the user's car (in a watched race, not necessarily the seat picked)
        var raceCars = setup.Grid.Where(s => !s.IsSafetyCar).ToList();
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // Watching: the car the camera starts on (the seat picked), by race.ini CAR_n index. And CAR_0's real driver:
        // Content Manager, starting the race, replaces CAR_0's name and nationality with its own player's, so the
        // in-game app puts the real ones back (CSP splits INI values at commas: none here).
        if (setup.Watch)
        {
            ini.Set(WatchSection, "CAMERA_CAR", cars.IndexOf(setup.Player).ToString(CultureInfo.InvariantCulture));
            ini.Set(WatchSection, "CAR0_NAME", player.Driver.Replace(",", " ").Trim());
            if (NationCodes.Of(player.Car.Entry.Nation) is { } code) ini.Set(WatchSection, "CAR0_NATION", code);
        }

        // Which race this section belongs to. Content Manager keeps sections it doesn't know when it writes race.ini for
        // another race, so the in-game app only uses this section while the loaded track and car count match these.
        ini.Set(WatchSection, "TRACK", setup.Track.Folder!);
        ini.Set(WatchSection, "CARS", cars.Count.ToString(CultureInfo.InvariantCulture));

        // The Safety Car: the in-game app keeps it in its pit box until it sends it out (AC would race it like any
        // other AI car) and never films it.
        var safetyIndex = cars.FindIndex(s => s.IsSafetyCar);
        if (safetyIndex >= 0) ini.Set(WatchSection, "SAFETY_CAR", safetyIndex.ToString(CultureInfo.InvariantCulture));

        if (setup.Watch)
        {
            // The real result, so the in-game app can steer the AI to it: per car (by race.ini CAR_n index) its real
            // finishing position (0 = didn't finish) and real laps completed, plus the real and the race distance.
            ini.Set(WatchSection, "REAL_RESULT", "1");
            ini.Set(WatchSection, "REAL_LAPS", RaceDistance.RealLaps(setup.Roster).ToString(CultureInfo.InvariantCulture));
            ini.Set(WatchSection, "RACE_LAPS", setup.Laps.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < cars.Count; i++)
            {
                if (cars[i].IsSafetyCar) continue;
                var e = cars[i].Car.Entry;
                ini.Set(WatchSection, $"CAR_{i}_POS", (e.Position ?? 0).ToString(CultureInfo.InvariantCulture));
                ini.Set(WatchSection, $"CAR_{i}_LAPS", (e.Laps ?? 0).ToString(CultureInfo.InvariantCulture));
            }
        }

        if (setup.IsMultiClass)
        {
            // Each car's class (by race.ini CAR_n index), for the in-game app's class standings, and the classes
            // as CLASS_1..n, fastest first (by their best real finisher). CSP splits INI values at commas: none here.
            static string Safe(string c) => c.Replace(",", "/");
            var classes = RaceClasses.Ordered(raceCars.Select(c => c.Car.Entry));
            for (var k = 0; k < classes.Count; k++) ini.Set(WatchSection, $"CLASS_{k + 1}", Safe(classes[k]));
            for (var i = 0; i < cars.Count; i++)
                if (!cars[i].IsSafetyCar && RaceClasses.Of(cars[i].Car.Entry) is { } c)
                    ini.Set(WatchSection, $"CAR_{i}_CLASS", Safe(c));
        }

        ini.Set("HEADER", "VERSION", "2");
        ini.Set("RACE", "TRACK", setup.Track.Folder!);
        ini.Set("RACE", "CONFIG_TRACK", setup.Track.Layout ?? "");
        ini.Set("RACE", "MODEL", player.Car.Model!);
        ini.Set("RACE", "MODEL_CONFIG", "");
        ini.Set("RACE", "SKIN", player.Car.Skin ?? "");
        ini.Set("RACE", "CARS", cars.Count.ToString(CultureInfo.InvariantCulture));
        ini.Set("RACE", "AI_LEVEL", F(setup.AiLevel));
        ini.Set("RACE", "FIXED_SETUP", "0");
        ini.Set("RACE", "PENALTIES", setup.Penalties ? "1" : "0");
        ini.Set("RACE", "JUMP_START_PENALTY", setup.Penalties ? "1" : "0");
        ini.Set("RACE", "RACE_LAPS", setup.Laps.ToString(CultureInfo.InvariantCulture));
        foreach (var section in new[] { "REMOTE", "REPLAY", "BENCHMARK", "RESTART" }) ini.Set(section, "ACTIVE", "0");
        WriteTimeAndPlace(ini, setup);
        ini.Set("WEATHER", "NAME", setup.Weather);
        ini.Set("TEMPERATURE", "AMBIENT", setup.AmbientTemperature.ToString(CultureInfo.InvariantCulture));
        ini.Set("TEMPERATURE", "ROAD", setup.RoadTemperature.ToString(CultureInfo.InvariantCulture));

        // Sessions. The race keeps the player's real grid slot (STARTING_POSITION).
        var index = 0;
        void Session(string name, int type, Action<string> fill)
        {
            var s = $"SESSION_{index++}";
            ini.Set(s, "NAME", name);
            ini.Set(s, "TYPE", type.ToString(CultureInfo.InvariantCulture));
            fill(s);
        }
        if (setup.Weekend == RaceWeekend.PracticeQualifyingAndRace)
            Session("Practice", 1, s => { ini.Set(s, "DURATION_MINUTES", setup.PracticeMinutes.ToString(CultureInfo.InvariantCulture)); ini.Set(s, "SPAWN_SET", "PIT"); });
        if (setup.Weekend != RaceWeekend.RaceOnly)
            Session("Qualifying", 2, s => { ini.Set(s, "DURATION_MINUTES", setup.QualifyingMinutes.ToString(CultureInfo.InvariantCulture)); ini.Set(s, "SPAWN_SET", "PIT"); });
        Session("Race", 3, s =>
        {
            ini.Set(s, "LAPS", setup.Laps.ToString(CultureInfo.InvariantCulture));
            ini.Set(s, "DURATION_MINUTES", "0");
            ini.Set(s, "SPAWN_SET", "START");
            ini.Set(s, "STARTING_POSITION", Math.Max(1, raceCars.IndexOf(player) + 1).ToString(CultureInfo.InvariantCulture));
        });

        for (var i = 0; i < cars.Count; i++)
        {
            var slot = cars[i];
            var s = $"CAR_{i}";
            ini.Set(s, "MODEL", i == 0 ? "-" : slot.Car.Model!); // CM convention: CAR_0 uses [RACE] MODEL
            ini.Set(s, "MODEL_CONFIG", "");
            ini.Set(s, "SKIN", slot.Car.Skin ?? "");
            ini.Set(s, "SETUP", "");
            ini.Set(s, "BALLAST", F(slot.Car.Entry.Ballast));
            ini.Set(s, "RESTRICTOR", slot.Car.Entry.Restrictor.ToString(CultureInfo.InvariantCulture));
            if (i == 0 && setup.Watch)
            {
                // Watched race: the AI drives this seat too, under the real driver's name.
                ini.Set(s, "DRIVER_NAME", slot.Driver);
                ini.Set(s, "NATIONALITY", slot.Car.Entry.Nation ?? "");
                ini.Set(s, "AI_LEVEL", F(setup.LevelOf(slot)));
                ini.Set(s, "AI_AGGRESSION", F(setup.AiAggression));
            }
            else if (i == 0)
            {
                // Your name replaces the real driver of the seat you took.
                ini.Set(s, "DRIVER_NAME", ownName);
                ini.Set(s, "NATIONALITY", myNation ?? "");
            }
            else
            {
                ini.Set(s, "DRIVER_NAME", slot.Driver);
                ini.Set(s, "NATIONALITY", slot.IsSafetyCar ? "" : slot.Car.Entry.Nation ?? "");
                ini.Set(s, "AI_LEVEL", F(setup.LevelOf(slot)));
                ini.Set(s, "AI_AGGRESSION", F(setup.AiAggression));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(racePath)!);
        ini.Save(racePath);
        return racePath;
    }
}

/// <summary>Minimal ordered INI document that keeps unknown sections and keys as they are.</summary>
public class IniDocument
{
    private readonly List<(string Name, List<(string Key, string Value)> Keys)> _sections = new();

    public static IniDocument Load(string path)
    {
        var doc = new IniDocument();
        List<(string, string)>? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new List<(string, string)>();
                doc._sections.Add((line[1..^1].Trim(), current));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || current == null) continue;
            current.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return doc;
    }

    public string? Get(string section, string key)
    {
        var s = _sections.FirstOrDefault(x => x.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (s.Keys == null) return null;
        var k = s.Keys.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return k.Key == null ? null : k.Value;
    }

    public void Set(string section, string key, string value)
    {
        var s = _sections.FirstOrDefault(x => x.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (s.Keys == null)
        {
            s = (section, new List<(string, string)>());
            _sections.Add(s);
        }
        var i = s.Keys.FindIndex(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) s.Keys[i] = (s.Keys[i].Key, value);
        else s.Keys.Add((key, value));
    }

    public void RemoveSections(Func<string, bool> match) => _sections.RemoveAll(s => match(s.Name));

    public void Remove(string section, string key)
    {
        var s = _sections.FirstOrDefault(x => x.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        s.Keys?.RemoveAll(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        foreach (var (name, keys) in _sections)
        {
            sb.Append('[').Append(name).Append("]\r\n");
            foreach (var (k, v) in keys) sb.Append(k).Append('=').Append(v).Append("\r\n");
            sb.Append("\r\n");
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Installs or updates the Historic Roster in-game (CSP) app in Assetto Corsa from a bundled copy.</summary>
public static class InGameApp
{
    public static string InstalledFolder(string acRoot) => Path.Combine(acRoot, "apps", "lua", "HistoricRoster");

    /// <summary>
    /// Copies the bundled app when it is missing or its script differs from the bundled one (line endings aside: two
    /// checkouts of the same code mustn't take turns replacing it). Never while Assetto Corsa is running: CSP restarts a
    /// changed app at once, and a race in progress loses everything the app was doing for it. Returns what was done
    /// ("installed", "updated", "left as it is ...") or null when it was already current. Throws when there's no
    /// bundled copy.
    /// </summary>
    public static string? EnsureCurrent(string acRoot, string bundledFolder)
    {
        var bundledLua = Path.Combine(bundledFolder, "HistoricRoster.lua");
        if (!File.Exists(bundledLua)) throw new FileNotFoundException("The in-game app isn't bundled with this program.", bundledLua);
        var target = InstalledFolder(acRoot);
        var targetLua = Path.Combine(target, "HistoricRoster.lua");
        var existed = File.Exists(targetLua);
        static string Code(string path) => File.ReadAllText(path).Replace("\r\n", "\n");
        if (existed && Code(targetLua) == Code(bundledLua)) return null;
        if (existed && GameRunning())
            return "left as it is while Assetto Corsa is running (replacing it would restart it mid-race; it's updated next time)";

        // Copies the app's own files only; the RosterTool copy (tool\) and anything else in the folder is kept.
        foreach (var file in Directory.EnumerateFiles(bundledFolder, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(bundledFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
        return existed ? "updated" : "installed";
    }

    static bool GameRunning()
    {
        try { return Process.GetProcessesByName("acs").Length > 0 || Process.GetProcessesByName("acs_x86").Length > 0; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>What's installed that the program works together with: CSP, the in-game app and the Safety Car.</summary>
public record CompanionApps(string SafetyCarModel, bool SafetyCarInstalled, string? SafetyCarSkin)
{
    public const string DefaultSafetyCar = "ferrari_458_s3";

    /// <summary>Custom Shaders Patch is installed (needed for the autopilot that drives your car in a watched race).</summary>
    public bool Csp { get; init; }
    /// <summary>The Historic Roster in-game app is installed (it turns the autopilot on in a watched race).</summary>
    public bool HistoricRosterApp { get; init; }
    /// <summary>A watched race can run: CSP and the in-game app are both installed.</summary>
    public bool CanWatch => Csp && HistoricRosterApp;

    public static CompanionApps Detect(string acRoot)
    {
        var apps = Path.Combine(acRoot, "apps", "lua");
        var model = DefaultSafetyCar;
        var carDir = Path.Combine(acRoot, "content", "cars", model);
        var installed = Directory.Exists(carDir);
        string? skin = null;
        if (installed && Directory.Exists(Path.Combine(carDir, "skins")))
        {
            var skins = Directory.GetDirectories(Path.Combine(carDir, "skins")).Select(Path.GetFileName).ToList();
            // A livery with "safety" in its name, if the car has one.
            skin = skins.FirstOrDefault(s => s!.Contains("safety", StringComparison.OrdinalIgnoreCase)) ?? skins.FirstOrDefault();
        }
        return new CompanionApps(model, installed, skin)
        {
            Csp = File.Exists(Path.Combine(acRoot, "dwrite.dll")) && Directory.Exists(Path.Combine(acRoot, "extension")),
            HistoricRosterApp = File.Exists(Path.Combine(apps, "HistoricRoster", "manifest.ini")),
        };
    }

    /// <summary>The Safety Car as a grid slot (always last; not part of the real grid).</summary>
    public RaceSlot SafetyCarSlot(int gridPosition) => new()
    {
        Car = new ResolvedCar(new RosterEntry { Driver = "Safety Car", Car = "Safety Car" }, SafetyCarModel, SafetyCarSkin,
            SafetyCarInstalled, false, "", "Safety Car", "safety"),
        GridPosition = gridPosition,
        IsSafetyCar = true,
    };

    /// <summary>
    /// Adds the Safety Car ON TOP of the finished grid; it never takes a real car's place, whether you race or watch.
    /// Returns null when added, else why it was left out (the in-game app runs it, and AC can't start more cars than
    /// the track's pit boxes; the real grid comes first).
    /// </summary>
    public string? TryAddSafetyCar(RaceSetup setup, int? pitBoxes)
    {
        // The in-game app (it needs CSP) runs the Safety Car; without it AC would race it like any other AI car.
        if (!Csp) return "Custom Shaders Patch isn't installed, so no Safety Car was added (it would race like any other car).";
        if (!SafetyCarInstalled) return $"Safety Car {SafetyCarModel} isn't installed.";
        if (setup.Grid.Any(s => s.IsSafetyCar)) return null;
        if (pitBoxes is { } boxes && setup.Grid.Count >= boxes)
            return $"All {boxes} pit boxes are taken by the real grid, so no Safety Car was added.";
        setup.Grid.Add(SafetyCarSlot(setup.Grid.Count + 1));
        return null;
    }
}

/// <summary>Starts Assetto Corsa the way Content Manager does (acs.exe reads cfg\race.ini). Steam must be running.</summary>
public static class AcLauncher
{
    public static bool SteamRunning => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>True while Assetto Corsa is open (it only reads race.ini when it starts).</summary>
    public static bool AcRunning => Process.GetProcessesByName("acs").Length > 0 || Process.GetProcessesByName("acs_x86").Length > 0;

    public static Process? Launch(string acRoot)
    {
        var exe = Path.Combine(acRoot, "acs.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("acs.exe not found in " + acRoot);
        // Without steam_appid.txt, acs.exe asks Steam to restart it through the normal launcher, which ignores race.ini.
        var appId = Path.Combine(acRoot, "steam_appid.txt");
        if (!File.Exists(appId)) File.WriteAllText(appId, AcInstallLocator.SteamAppId);
        return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = acRoot, UseShellExecute = true });
    }

    /// <summary>Content Manager's program, from its acmanager:// link handler; null when CM isn't installed.</summary>
    public static string? ContentManagerExe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.ClassesRoot })
        {
            using var key = root.OpenSubKey(root == Microsoft.Win32.Registry.CurrentUser
                ? @"Software\Classes\acmanager\shell\open\command" : @"acmanager\shell\open\command");
            if (key?.GetValue(null) is not string command) continue;
            // "F:\Content Manager.exe" "%1"
            var exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>
    /// Starts the race in <paramref name="raceIniPath"/> through Content Manager (acmanager://race/config?configFile=...).
    /// CM writes that file to race.ini exactly as given (names, sessions and the watch flag stay as written) and starts
    /// Assetto Corsa its own way. Returns false when Content Manager isn't installed.
    /// </summary>
    public static bool LaunchViaContentManager(string raceIniPath)
    {
        var cm = ContentManagerExe();
        if (cm == null) return false;
        // A copy: CM reads it while writing race.ini, and race.ini itself may be the file it writes to.
        var copy = Path.Combine(Path.GetTempPath(), "HistoricRoster_race.ini");
        File.Copy(raceIniPath, copy, overwrite: true);
        var url = "acmanager://race/config?configFile=" + Uri.EscapeDataString(copy);
        Process.Start(new ProcessStartInfo(cm, $"\"{url}\"") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cm)! });
        return true;
    }

    /// <summary>Starts the race through Content Manager when it's installed, else acs.exe directly. Returns how it started.</summary>
    public static string Start(string acRoot, string raceIniPath)
    {
        if (LaunchViaContentManager(raceIniPath)) return "through Content Manager";
        Launch(acRoot);
        return "directly (Content Manager isn't installed)";
    }
}

/// <summary>AC's three-letter nation codes (ac.NationCode) for the nationalities in the race data.</summary>
public static class NationCodes
{
    private static readonly Dictionary<string, string> ByNationality = new(StringComparer.OrdinalIgnoreCase)
    {
        ["British"] = "GBR", ["English"] = "GBR", ["Scottish"] = "GBR", ["Welsh"] = "GBR", ["Italian"] = "ITA",
        ["French"] = "FRA", ["German"] = "DEU", ["East German"] = "DEU", ["Brazilian"] = "BRA", ["American"] = "USA",
        ["Finnish"] = "FIN", ["Spanish"] = "ESP", ["Australian"] = "AUS", ["Japanese"] = "JPN", ["Austrian"] = "AUT",
        ["Dutch"] = "NLD", ["Canadian"] = "CAN", ["Belgian"] = "BEL", ["Mexican"] = "MEX", ["Swedish"] = "SWE",
        ["Swiss"] = "CHE", ["New Zealander"] = "NZL", ["Argentine"] = "ARG", ["Argentinian"] = "ARG", ["Monegasque"] = "MCO",
        ["Danish"] = "DNK", ["Russian"] = "RUS", ["South African"] = "ZAF", ["Thai"] = "THA", ["Colombian"] = "COL",
        ["Venezuelan"] = "VEN", ["Polish"] = "POL", ["Portuguese"] = "PRT", ["Chinese"] = "CHN", ["Irish"] = "IRL",
        ["Indian"] = "IND", ["Chilean"] = "CHL", ["Hungarian"] = "HUN", ["Rhodesian"] = "ZWE", ["Malaysian"] = "MYS",
        ["Indonesian"] = "IDN", ["Liechtensteiner"] = "LIE", ["Uruguayan"] = "URY", ["Czech"] = "CZE",
    };

    /// <summary>The code for a nationality ("French" → "FRA"); a three-letter code is kept as it is; null when unknown.</summary>
    public static string? Of(string? nationality)
    {
        if (string.IsNullOrWhiteSpace(nationality)) return null;
        var n = nationality.Trim();
        if (ByNationality.TryGetValue(n, out var code)) return code;
        return n.Length == 3 && n.All(char.IsLetter) ? n.ToUpperInvariant() : null;
    }
}
