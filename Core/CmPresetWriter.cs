using System.Text.Json;
using System.Text.Json.Nodes;

namespace HistoricRosterPlugin;

/// <summary>
/// Saves a race as Content Manager presets, so it can be started from Content Manager's Quick Drive instead of
/// from this program: a Quick Drive preset (track, car, conditions, laps and the grid) and the grid on its own as
/// a Race Grids preset. The format follows Content Manager's own (QuickDrive.SaveableData,
/// QuickDrive_Race.SaveableData v2 and RaceGridViewModel.SaveableData in gro-ove/actools): plain JSON, with the
/// race mode's data and the grid nested inside as JSON strings.
/// </summary>
public static class CmPresetWriter
{
    public const string SubFolder = "Historic Roster";

    /// <summary>Content Manager's presets folder (%LOCALAPPDATA%\AcTools Content Manager\Presets), or null if CM isn't set up here.</summary>
    public static string? PresetsFolder()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcTools Content Manager");
        return Directory.Exists(root) ? Path.Combine(root, "Presets") : null;
    }

    public record Result(string QuickDrivePreset, string RaceGridPreset);

    /// <summary>Writes both presets under Presets\Quick Drive\Historic Roster and Presets\Race Grids\Historic Roster.</summary>
    public static Result Write(RaceSetup setup, string presetsFolder, string name)
    {
        var file = SafeFileName(name) + ".cmpreset";
        var grid = GridJson(setup);

        var gridPath = Path.Combine(presetsFolder, "Race Grids", SubFolder, file);
        var quickPath = Path.Combine(presetsFolder, "Quick Drive", SubFolder, file);
        Save(gridPath, grid);
        Save(quickPath, QuickDriveJson(setup, grid));
        return new Result(quickPath, gridPath);
    }

    /// <summary>Content Manager's "custom" grid: every AI car in real grid order, the player inserted at StartingPosition.</summary>
    public static string GridJson(RaceSetup setup)
    {
        var raceCars = setup.Grid.Where(s => !s.IsSafetyCar).OrderBy(s => s.GridPosition).ToList();
        var player = setup.Player;
        var ai = raceCars.Where(s => !s.IsPlayer).ToList();

        var o = new JsonObject
        {
            ["ModeId"] = "custom",
            ["FilterValue"] = null,
            ["CarIds"] = Array(ai.Select(s => s.Car.Model)),
            ["CandidatePriorities"] = null,
            ["AiLevels"] = ai.Any(s => s.AiLevel != null) ? Array(ai.Select(s => setup.LevelOf(s))) : null,
            ["AiAggressions"] = null,
            ["Ballasts"] = ai.Any(s => s.Car.Entry.Ballast != 0) ? Array(ai.Select(s => (double)s.Car.Entry.Ballast)) : null,
            ["Restrictors"] = ai.Any(s => s.Car.Entry.Restrictor != 0) ? Array(ai.Select(s => (double)s.Car.Entry.Restrictor)) : null,
        };
        if (player.Car.Entry.Ballast != 0) o["PlayerBallast"] = (double)player.Car.Entry.Ballast;
        if (player.Car.Entry.Restrictor != 0) o["PlayerRestrictor"] = (double)player.Car.Entry.Restrictor;
        o["Names"] = Array(ai.Select(s => s.Driver));
        o["Nationalities"] = ai.Any(s => !string.IsNullOrEmpty(s.Car.Entry.Nation)) ? Array(ai.Select(s => NullIfEmpty(s.Car.Entry.Nation))) : null;
        o["SkinIds"] = ai.Any(s => !string.IsNullOrEmpty(s.Car.Skin)) ? Array(ai.Select(s => NullIfEmpty(s.Car.Skin))) : null;
        o["AiLimitations"] = null;
        o["ShuffleCandidates"] = false;
        o["VarietyLimitation"] = null;
        o["OpponentsNumber"] = ai.Count;
        o["StartingPosition"] = Math.Max(1, raceCars.IndexOf(player) + 1);
        o["AiLevel"] = setup.AiLevel;
        o["AiLevelMin"] = ai.Count > 0 ? ai.Min(s => setup.LevelOf(s)) : setup.AiLevel;
        o["AiLevelArrangeRandom"] = 0d;
        o["AiLevelArrangeReverse"] = false;
        o["AiLevelArrangePowerRatio"] = false;
        o["AiAggression"] = setup.AiAggression;
        o["AiAggressionMin"] = setup.AiAggression;
        o["AiAggressionArrangeRandom"] = 0d;
        o["AiAggressionArrangeReverse"] = false;
        return o.ToJsonString();
    }

    public static string QuickDriveJson(RaceSetup setup, string gridJson)
    {
        var race = new JsonObject
        {
            ["Version"] = 2,
            ["Penalties"] = setup.Penalties,
            ["JumpStartPenalty"] = null,
            ["LapsNumber"] = setup.Laps,
            ["RaceGridSerialized"] = gridJson,
        };
        var track = setup.Track.Folder! + (string.IsNullOrEmpty(setup.Track.Layout) ? "" : "/" + setup.Track.Layout);

        var o = new JsonObject
        {
            ["Mode"] = "/Pages/Drive/QuickDrive_Race.xaml",
            ["ModeData"] = race.ToJsonString(),
            ["CarId"] = setup.Player.Car.Model,
            ["TrackId"] = track,
            ["WeatherId"] = setup.Weather,
            ["RealConditions"] = false,
            ["Temperature"] = (double)setup.AmbientTemperature,
            ["Time"] = (int)setup.TimeOfDay.TotalSeconds,
            ["TimeMultipler"] = 1, // sic: Content Manager's own key
            ["udt"] = false,
            ["dtv"] = null,
            ["tpc"] = false,
            ["TrackPropertiesPresetFilename"] = null,
            ["TrackPropertiesData"] = null,
            ["asc"] = false,
            ["AssistsPresetFilename"] = null,
            ["AssistsData"] = null,
            ["ico"] = false,
            ["wsf"] = 10d,
            ["wst"] = 10d,
            ["wd"] = 0d,
            ["rws"] = false,
            ["rwd"] = false,
            ["rte"] = false,
            ["rti"] = false,
            ["crt"] = true,
            ["crtv"] = (double)setup.RoadTemperature,
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray Array<T>(IEnumerable<T> items) => new(items.Select(x => x == null ? null : JsonValue.Create(x)).ToArray<JsonNode?>());

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static void Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Historic race" : clean;
    }
}
