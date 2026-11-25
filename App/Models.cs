using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistoricRosterPlugin;
using HistoricRosterPlugin.Ai;

namespace HistoricRosterApp;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A race in the list on the left.</summary>
public class RaceItem : Observable
{
    public required Roster Roster { get; init; }
    public string Name => Roster.Name;
    public string Category => Roster.Category ?? "";
    public int Year => Roster.Year ?? 0;
    public string Subtitle => $"{Roster.Category} · {Roster.Track?.Name}";

    private int _ready = -1, _total, _standIns;
    private bool _trackReady;

    /// <summary>Installed real cars; -1 until checked.</summary>
    public int Ready => _ready;
    public int Total => _total;
    /// <summary>Missing real cars that can get a stand-in of the same class.</summary>
    public int StandIns => _standIns;
    public bool TrackReady => _trackReady;
    public bool FullyReady => _ready > 0 && _ready == _total && _trackReady;
    public bool Runnable => (_ready > 0 || _standIns > 0) && _trackReady;

    public string ReadyText => _ready < 0 ? ""
        : $"{_ready}/{_total} cars{(_standIns > 0 && _ready < _total ? $" +{_standIns} stand-in{(_standIns == 1 ? "" : "s")}" : "")}{(_trackReady ? "" : " · no track")}";
    public Brush ReadyBrush => _ready < 0 ? Brushes.Transparent
        : FullyReady ? Ui.Ok : Runnable ? Ui.Warn : Ui.Muted;

    public void SetReadiness(int ready, int total, bool trackReady, int standIns = 0)
    {
        _ready = ready; _total = total; _trackReady = trackReady; _standIns = standIns;
        Changed(nameof(ReadyText)); Changed(nameof(ReadyBrush)); Changed(nameof(FullyReady)); Changed(nameof(Runnable));
    }
}

/// <summary>A row of the pre-race grid.</summary>
public class GridRow : Observable
{
    public required ResolvedCar Car { get; init; }
    public RaceSlot? Slot { get; init; }
    public RaceSetup? Setup { get; init; }
    public string Position { get; init; } = "";
    public string Number => Car.Entry.Number is { Length: > 0 } n ? "#" + n : "";
    /// <summary>Your name in your seat ("Jim (replaces Button)"), else the real driver.</summary>
    public string Driver => Slot != null && IsPlayer && Setup != null && !Setup.Watch && !string.IsNullOrWhiteSpace(Setup.PlayerName)
        ? $"{Setup.NameOf(Slot)}  (replaces {Slot.Driver})"
        : Slot?.Driver ?? Car.Entry.Driver;
    public void RefreshName() => Changed(nameof(Driver));
    public string Team => Car.Entry.Team ?? "";
    public bool IsStandIn => Slot?.IsStandIn == true;
    public string RealCar => (Car.Entry.Car ?? "") + (IsStandIn ? " (not installed)" : "");
    public string AcCar => IsStandIn ? $"stand-in: {Car.Model}" : Car.Model ?? "no AC car known";
    public string Livery => Car.Skin ?? "";
    public string RealResult => Car.Entry.Position is { } p
        ? $"P{p}" + (RealClassPosition is { } c ? $" · class P{c}" : "")
        : Car.Entry.Status ?? "";
    /// <summary>The car's class, in a multi-class race ("" otherwise).</summary>
    public string Class { get; init; } = "";
    /// <summary>Real finishing position within the class, in a multi-class race.</summary>
    public int? RealClassPosition { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public string ToolTipText => IsStandIn
        ? $"The real {Car.Entry.Car ?? "car"} isn't installed: {Car.Model} stands in for it. Get the real car from the missing list."
        : "";

    /// <summary>This AI driver's level ("" for you and the Safety Car).</summary>
    public string AiText => Slot == null || Slot.IsSafetyCar || Setup == null || (IsPlayer && !Setup.Watch) ? "" : $"{Setup.LevelOf(Slot):0.#}";
    public void RefreshAi() => Changed(nameof(AiText));
    /// <summary>After watch-only is switched: your seat's name, status and AI level change.</summary>
    public void RefreshWatch() { Changed(nameof(Driver)); Changed(nameof(Status)); Changed(nameof(AiText)); }

    private bool _isPlayer;
    public bool IsPlayer { get => _isPlayer; set { _isPlayer = value; Changed(); Changed(nameof(RowBrush)); Changed(nameof(Status)); Changed(nameof(AiText)); Changed(nameof(Driver)); } }

    public bool OnGrid => Slot != null;
    public string Status => Slot?.IsSafetyCar == true ? "Safety Car"
        : IsPlayer && Setup?.Watch == true ? (IsStandIn ? "AI · camera (stand-in)" : "AI · camera")
        : IsPlayer ? (IsStandIn ? "YOU (stand-in)" : "YOU")
        : IsStandIn ? "Stand-in"
        : OnGrid ? "AI"
        : Car.Model == null ? "No AC car known" : "Not installed";
    public Brush RowBrush => IsPlayer ? Ui.PlayerRow : !OnGrid ? Ui.LeftOutRow : Brushes.Transparent;
    public Brush StatusBrush => !OnGrid ? Ui.Bad : Slot!.IsSafetyCar || IsStandIn ? Ui.Warn : Ui.Ok;
    public double RowOpacity => OnGrid ? 1 : 0.6;
}

/// <summary>Something the race needs that isn't installed.</summary>
public class MissingRow
{
    public required WantedItem Item { get; init; }
    /// <summary>For a missing car: one of the race entries that need it (for "I have it").</summary>
    public ResolvedCar? Car { get; init; }
    public System.Windows.Visibility PickVisibility
        => Car != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string Kind => Item.Kind == "track" ? "Track" : "Car";
    public string Label => Item.Label;
    public System.Windows.Visibility OtherSitesVisibility
        => Item.OtherSources.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string Where => Item.IsKunos ? "Official Kunos content (Steam)"
        : ContentGetter.IsDirectArchive(Item.Url) ? "Direct download"
        : Item.Url.Contains("overtake.gg") ? (Item.Url.Contains("/search/") ? "overtake.gg search" : "overtake.gg page")
        : "Download page";
}

public static class Ui
{
    // The theme's colours (App.xaml), for code.
    public static readonly Brush Ok = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x4B)));
    public static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0x6A, 0x0C)));
    public static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xD7, 0x26, 0x1E)));
    public static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x68, 0x70, 0x82)));
    public static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x21)));
    public static readonly Brush PlayerRow = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xED, 0xEC)));
    public static readonly Brush LeftOutRow = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)));
    public static readonly Brush Bubble = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF6)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Your driver name (settings), for windows that show results and standings.</summary>
    public static string? PlayerName { get; set; }

    /// <summary>"Jim (as Button)" for your seat when you have a name, else the real driver.</summary>
    public static string AsYou(string realDriver, string? name = null)
    {
        var n = string.IsNullOrWhiteSpace(name) ? PlayerName : name;
        return string.IsNullOrWhiteSpace(n) ? $"{realDriver} (you)" : $"{n!.Trim()} (as {realDriver})";
    }

    /// <summary>Loads a preview image without locking the file; null when missing or unreadable.</summary>
    public static ImageSource? LoadImage(string? path, int decodeWidth)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.DecodePixelWidth = decodeWidth;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>preview.jpg of a car's livery, else the small livery.png.</summary>
    public static ImageSource? CarPreview(string content, string? model, string? skin, int width)
    {
        if (model == null) return null;
        var skins = Path.Combine(content, "cars", model, "skins");
        var dir = skin != null && Directory.Exists(Path.Combine(skins, skin)) ? Path.Combine(skins, skin)
            : Directory.Exists(skins) ? Directory.EnumerateDirectories(skins).FirstOrDefault() : null;
        if (dir == null) return null;
        return LoadImage(Path.Combine(dir, "preview.jpg"), width) ?? LoadImage(Path.Combine(dir, "livery.png"), width);
    }

    public static string TrackUiFolder(string content, string folder, string? layout)
        => layout == null ? Path.Combine(content, "tracks", folder, "ui") : Path.Combine(content, "tracks", folder, "ui", layout);
}

/// <summary>Settings saved in %APPDATA%\HistoricRoster\settings.json.</summary>
public class AppSettings
{
    public string? ContentFolder { get; set; }
    public string? RostersFolder { get; set; }
    public double AiLevel { get; set; } = 95;
    public double AiAggression { get; set; } = 50;
    /// <summary>Off by default: AI behaviour around the AdvancedAI Safety Car can be unreliable.</summary>
    public bool AddSafetyCar { get; set; }
    public bool StandIns { get; set; } = true;
    public bool RealSkill { get; set; } = true;
    public double SkillSpread { get; set; } = 6;
    public bool RealConditions { get; set; } = true;
    /// <summary>Race distance as a share of the real one: 25, 50 or 100.</summary>
    public int DistancePercent { get; set; } = 25;
    /// <summary>Seat picked when a race opens: Pole, Midfield, Team (favourite team) or Driver (favourite driver).</summary>
    public string SeatMode { get; set; } = "Pole";
    public string? FavoriteTeam { get; set; }
    public string? FavoriteDriver { get; set; }
    /// <summary>Your driver name: replaces the real driver of the seat you take. Null = read from AC's race.ini.</summary>
    public string? PlayerName { get; set; }
    /// <summary>Watch an AI-only race instead of driving.</summary>
    public bool Watch { get; set; }
    public bool ReadyOnly { get; set; } = true;
    /// <summary>Write the real race's details for SimCast's commentary (only when SimCast is installed).</summary>
    public bool SimCastContext { get; set; } = true;
    /// <summary>Start SimCast with each race (when it isn't open already).</summary>
    public bool StartSimCast { get; set; }
    /// <summary>OpenAI / DeepSeek: keys (encrypted), models and which features use them.</summary>
    public AiSettings Ai { get; set; } = new();
    /// <summary>0 in files written before versioning (the property is missing there), so they get migrated.</summary>
    public int SettingsVersion { get; set; }

    private const int CurrentVersion = 2;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HistoricRoster", "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        if (s.SettingsVersion < 2)
        {
            // Older settings files: new defaults (Safety Car off, only runnable races listed).
            s.AddSafetyCar = false;
            s.ReadyOnly = true;
            s.SettingsVersion = CurrentVersion;
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience */ }
    }
}
