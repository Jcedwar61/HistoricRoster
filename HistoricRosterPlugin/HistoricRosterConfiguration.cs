using AssettoServer.Server.Configuration;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace HistoricRosterPlugin;

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class HistoricRosterConfiguration : IValidateConfiguration<HistoricRosterConfigurationValidator>
{
    [YamlMember(Description = "Folder holding roster JSON files and content_catalog.json. Relative paths are resolved from the AssettoServer working directory.")]
    public string RosterFolder { get; init; } = "rosters";

    [YamlMember(Description = "Content catalog file name inside RosterFolder. Maps real cars/circuits to AC folders and download links.")]
    public string CatalogFile { get; init; } = "content_catalog.json";

    [YamlMember(Description = "Bulk roster database inside RosterFolder, built by RosterTool build-db")]
    public string DatabaseFile { get; init; } = RosterDatabase.DefaultFileName;

    [YamlMember(Description = "AC 'content' folder (holds cars/ and tracks/) used to find installed and missing content. Empty = the server's 'content' folder, or your Assetto Corsa install found through Steam.")]
    public string ContentFolder { get; init; } = "";

    /// <summary>ContentFolder, or "content" if it exists, or the Steam AC install's content folder.</summary>
    [YamlIgnore]
    public string EffectiveContentFolder => _effectiveContentFolder ??= ResolveContentFolder();
    private string? _effectiveContentFolder;

    private string ResolveContentFolder()
    {
        if (!string.IsNullOrWhiteSpace(ContentFolder)) return Path.GetFullPath(ContentFolder);
        if (Directory.Exists(Path.Combine("content", "cars"))) return Path.GetFullPath("content");
        // No drive scan: a server PC usually has no game install, and the scan would hold up startup for up to 30 s.
        return AcInstallLocator.FindContentFolder(scanDrives: false) ?? Path.GetFullPath("content");
    }

    [YamlMember(Description = "entry_list.ini to overwrite when a roster is loaded. Empty = the entry_list.ini of the running config/preset.")]
    public string? EntryListPath { get; init; }

    [YamlMember(Description = "server_cfg.ini whose TRACK / CONFIG_TRACK / MAX_CLIENTS are updated on load. Empty = the server_cfg.ini of the running config/preset.")]
    public string? ServerCfgPath { get; init; }

    [YamlMember(Description = "Also switch the server's track when a roster is loaded")]
    public bool LoadTrack { get; init; } = true;

    [YamlMember(Description = "Write cm_content/content.json so Content Manager shows download links for missing cars/tracks")]
    public bool WriteContentManagerLinks { get; init; } = true;

    [YamlMember(Description = "Back up entry_list.ini, server_cfg.ini and content.json before overwriting them")]
    public bool BackupFiles { get; init; } = true;

    [YamlMember(Description = "Default grid order when loading a roster: AsListed, StartingGrid or FinishingOrder")]
    public GridOrder DefaultGridOrder { get; init; } = GridOrder.StartingGrid;

    [YamlMember(Description = "Maximum number of cars to write. 0 = use the track's pit box count when known, otherwise no limit")]
    public int MaxSlots { get; init; } = 0;

    [YamlMember(Description = "Oldest season that can be imported/loaded (the F1 data goes back to 1950)")]
    public int MinYear { get; init; } = 1950;

    [YamlMember(Description = "Newest season that can be imported/loaded. 0 = current year")]
    public int MaxYear { get; init; } = 0;

    [YamlMember(Description = "Download search link used when no exact URL is known. {query} is replaced with the URL-encoded car or track name.")]
    public string DownloadSearchUrl { get; init; } = "https://www.overtake.gg/search/?q={query}&t=resource";

    [YamlMember(Description = "Link shown for official Kunos cars/tracks (base game or DLC). Default: Steam's list of every Assetto Corsa DLC.")]
    public string KunosContentUrl { get; init; } = ResolverOptions.DefaultKunosContentUrl;

    [YamlMember(Description = "More sites to search for missing mods, offered next to the main link: name -> URL with {query}")]
    public Dictionary<string, string> ExtraDownloadSearchUrls { get; init; } = new(ResolverOptions.DefaultExtraSearchUrls);

    [YamlMember(Description = "Skin used when an entry has no skin. Empty = let AC pick the car's first skin.")]
    public string FallbackSkin { get; init; } = "";

    [YamlMember(Description = "Allow /roster_f1 to import results from the Jolpica F1 API (api.jolpi.ca)")]
    public bool EnableF1Import { get; init; } = true;

    [YamlMember(Description = "Allow /roster_nascar to import results from NASCAR's public results feed (cf.nascar.com, 2017 onward, unofficial)")]
    public bool EnableNascarImport { get; init; } = true;

    [YamlMember(Description = "Save every finished race session on this server as a roster in <RosterFolder>/captured (category 'Captured', listed after the real races)")]
    public bool CaptureRaceResults { get; init; } = false;

    [YamlMember(Description = "Store players' Steam GUIDs in captured rosters. Loading such a roster then reserves each slot for that player.")]
    public bool CaptureGuids { get; init; } = false;

    [YamlMember(Description = "Tell each player which historical driver's seat they took when they spawn")]
    public bool AnnounceSeatOnJoin { get; init; } = true;

    [YamlMember(Description = "Set the real start time, date, weather and temperature of the race when a roster is loaded (Open-Meteo archive). Rain needs WeatherFX (EnableWeatherFx in extra_cfg.yml) and CSP.")]
    public bool RecreateConditions { get; init; } = true;

    [YamlMember(Description = "Race length as a percentage of the real distance (the winner's laps). 0 = leave the server's race length alone.")]
    public int RaceLengthPercent { get; init; } = 100;

    [YamlMember(Description = "When a roster was loaded with the real starting grid, every race starts from it, even after practice or qualifying (which would otherwise set the grid by best lap)")]
    public bool KeepRealGrid { get; init; } = true;

    [YamlMember(Description = "Real cars that aren't installed: Off = leave the entry out; SameClass = an installed car of the same class and period stands in, and the seat says so")]
    public StandInMode StandIns { get; init; } = StandInMode.Off;

    [YamlMember(Description = "Let players book a real driver's seat with /roster_seat (reserves the slot for their Steam GUID from the next restart)")]
    public bool AllowSeatBooking { get; init; } = true;

    [YamlMember(Description = "After each race, tell every player how they did against the real driver whose seat they took")]
    public bool CompareWithHistory { get; init; } = true;

    [YamlIgnore] public int EffectiveMaxYear => MaxYear > 0 ? MaxYear : DateTime.UtcNow.Year;
}
