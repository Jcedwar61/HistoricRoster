using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HistoricRosterPlugin;
using HistoricRosterPlugin.Ai;
using Microsoft.Win32;

namespace HistoricRosterApp;

public partial class MainWindow : Window
{
    private record SeatItem(RaceSlot Slot, string Text);

    private readonly AppSettings _settings = AppSettings.Load();
    private Workspace? _ws;
    private ContentResolver? _resolver;
    private ContentCatalog? _catalog;
    private CompanionApps? _apps;
    private List<RaceItem> _races = new();
    private ICollectionView? _view;

    private RaceItem? _current;
    private RosterContent? _content;
    private RaceSetup? _setup;
    /// <summary>The real race-day conditions of the race shown, and the weather folder picked for them.</summary>
    private Conditions? _cond;
    private string? _condWeather;
    private bool _condLoading;
    private string? _preferredDriver;
    // True while the window is being built or filled by code: control events are ignored then
    // (sliders fire ValueChanged during InitializeComponent, before later controls exist).
    private bool _updatingUi = true;
    private CancellationTokenSource? _cts;
    private int _readinessRun;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { await OpenWorkspaceAsync(); }
            catch (Exception ex) { App.LogError("Load", ex); Log("Error: " + ex.Message); SetStatus("Could not load: " + ex.Message); }
        };
        Closing += (_, _) => { SaveSettings(); CompanionMute.Stop(); };
        // Companion apps a crash of this program left muted during a watched race get their sound back.
        CompanionMute.RestoreLeftovers();
        // Messages from the shared importers (e.g. rate-limit waits) go to the Activity log.
        RosterLog.Info = m => Dispatcher.BeginInvoke(() => { Log(m); SetStatus(m); });
        RosterLog.Warn = m => Dispatcher.BeginInvoke(() => Log("Warning: " + m));
        // Back from Assetto Corsa: show how the race went against the real result.
        Activated += (_, _) => CheckForResult();

        _updatingUi = true;
        AiLevelSlider.Value = _settings.AiLevel;
        AiAggressionSlider.Value = _settings.AiAggression;
        RealSkillBox.IsChecked = _settings.RealSkill;
        SpreadSlider.Value = _settings.SkillSpread;
        TimeSlider.Value = 14;
        TempSlider.Value = 22;
        SafetyCarBox.IsChecked = _settings.AddSafetyCar;
        StandInBox.IsChecked = _settings.StandIns;
        RealCondBox.IsChecked = _settings.RealConditions;
        SimCastBox.IsChecked = _settings.SimCastContext;
        SimCastBox.Visibility = SimCastLauncher.Installed ? Visibility.Visible : Visibility.Collapsed;
        StartSimCastBox.IsChecked = _settings.StartSimCast;
        StartSimCastBox.Visibility = SimCastLauncher.Exe() != null ? Visibility.Visible : Visibility.Collapsed;
        ReadyOnlyBox.IsChecked = _settings.ReadyOnly;
        SeatModeBox.SelectedItem = SeatModeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.SeatMode) ?? SeatModeBox.Items[0];
        // First run: start from the name Assetto Corsa / Content Manager last used.
        _settings.PlayerName ??= RaceIniWriter.ReadPlayerName(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assetto Corsa", "cfg", "race.ini"));
        PlayerNameBox.Text = _settings.PlayerName ?? "";
        Ui.PlayerName = _settings.PlayerName;
        WatchBox.IsChecked = _settings.Watch;
        _updatingUi = false;
    }

    private void Watch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.Watch = WatchBox.IsChecked == true;
        ApplyWatch();
    }

    /// <summary>Watch-only on or off: your seat becomes the AI car the camera starts on.</summary>
    /// <summary>The in-game app shipped with this program (installed into AC for watched races).</summary>
    private static string BundledInGameApp => Path.Combine(AppContext.BaseDirectory, "CspApp", "HistoricRoster");

    private void ApplyWatch()
    {
        // Needs CSP; the in-game app is installed from the bundled copy when it isn't there yet.
        var canWatch = _apps?.Csp == true && (_apps.HistoricRosterApp || File.Exists(Path.Combine(BundledInGameApp, "HistoricRoster.lua")));
        WatchBox.IsEnabled = canWatch;
        WatchBox.ToolTip = canWatch
            ? "The AI drives every car, the seat you picked included, and every driver keeps their real name. The camera starts on that seat; the Historic Roster app window in game lets you follow other cars."
            : "Needs Custom Shaders Patch (CSP), which provides the autopilot that drives the car in your seat.";
        var watch = canWatch && WatchBox.IsChecked == true;
        PlayerNameBox.IsEnabled = !watch;
        StartButton.Content = watch ? "Watch race" : "Start race";
        if (_setup == null) return;
        _setup.Watch = watch;
        CmButton.IsEnabled = !watch;
        CmButton.ToolTip = watch
            ? "Content Manager writes its own race file without the watch flag: use Watch race or Write to Assetto Corsa."
            : "Save this race as a Content Manager Quick Drive preset (and its grid as a Race Grids preset), under Historic Roster";
        if (GridList.ItemsSource is IEnumerable<GridRow> rows) foreach (var row in rows) row.RefreshWatch();
        ActionText.Text = Summary();
    }

    private void PlayerName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingUi) return;
        var name = PlayerNameBox.Text.Trim();
        _settings.PlayerName = name.Length == 0 ? null : name;
        Ui.PlayerName = _settings.PlayerName;
        if (_setup == null) return;
        _setup.PlayerName = _settings.PlayerName;
        if (GridList.ItemsSource is IEnumerable<GridRow> rows) foreach (var row in rows) row.RefreshName();
        ActionText.Text = Summary();
    }

    private void SaveSettings()
    {
        _settings.AiLevel = AiLevelSlider.Value;
        _settings.AiAggression = AiAggressionSlider.Value;
        _settings.RealSkill = RealSkillBox.IsChecked == true;
        _settings.SkillSpread = SpreadSlider.Value;
        _settings.AddSafetyCar = SafetyCarBox.IsChecked == true;
        _settings.StandIns = StandInBox.IsChecked == true;
        _settings.RealConditions = RealCondBox.IsChecked == true;
        _settings.SimCastContext = SimCastBox.IsChecked == true;
        _settings.StartSimCast = StartSimCastBox.IsChecked == true;
        _settings.ReadyOnly = ReadyOnlyBox.IsChecked == true;
        _settings.SeatMode = (SeatModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Pole";
        _settings.Save();
    }

    // ================================================================== loading

    private string RostersFolder => _settings.RostersFolder ?? Path.Combine(AppContext.BaseDirectory, "rosters");

    private async Task OpenWorkspaceAsync()
    {
        // A folder picked earlier that is gone (moved, drive changed): forget it and detect AC again.
        if (_settings.ContentFolder != null && !Directory.Exists(Path.Combine(_settings.ContentFolder, "cars")))
        {
            Log($"Saved Assetto Corsa folder {_settings.ContentFolder} no longer exists; detecting it again.");
            _settings.ContentFolder = null;
            _settings.Save();
        }
        try
        {
            if (_settings.ContentFolder == null)
            {
                InstallText.Text = "Looking for Assetto Corsa ...";
                var found = await Task.Run(() => AcInstallLocator.FindAll());
                foreach (var c in found) Log($"Found Assetto Corsa: {c.Root} ({c.Source})");
            }
            _ws = Workspace.Open(_settings.ContentFolder, RostersFolder, Path.Combine(AppContext.BaseDirectory, "rosters"));
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            if (!PickAcFolder()) { InstallText.Text = "Assetto Corsa not found - use Settings to select it."; return; }
            _ws = Workspace.Open(_settings.ContentFolder, RostersFolder, Path.Combine(AppContext.BaseDirectory, "rosters"));
        }

        if (_ws.LinkedTracks.Count > 0)
            Log($"Linked {_ws.LinkedTracks.Count} track(s) from another Assetto Corsa install into {_ws.ContentFolder}\\tracks " +
                $"(nothing copied): {string.Join(", ", _ws.LinkedTracks)}.");
        if (_ws.LinkedCars.Count > 0)
            Log($"Linked {_ws.LinkedCars.Count} car(s) from another Assetto Corsa install into {_ws.ContentFolder}\\cars " +
                $"(nothing copied): {string.Join(", ", _ws.LinkedCars)}.");
        _resolver = _ws.CreateResolver();
        _apps = CompanionApps.Detect(_ws.AcRoot);
        var lib = await Task.Run(() => _ws.Library);
        var parts = new List<string> { $"Assetto Corsa: {_ws.AcRoot}", $"{lib.Cars.Count} cars", $"{lib.Tracks.Count} track layouts" };
        InstallText.Text = string.Join("  ·  ", parts);
        FillWeather();
        UpdateCompanionText();
        // Cars and tracks are always matched: re-done automatically whenever either folder changed.
        var ws = _ws;
        var scanLog = new Progress<string>(m => { Log(m); SetStatus(m); });
        try { await Task.Run(() => LibraryScanner.ScanIfChanged(ws, scanLog)); }
        catch (Exception ex) { Log("Couldn't match your cars and tracks: " + ex.Message); }
        await LoadRacesAsync();
        CheckForResult();
        Activated += async (_, _) => await RescanIfChangedAsync();
    }

    private bool _rescanning;

    /// <summary>
    /// Cars or tracks added while the program is open (downloaded, installed with Content Manager): when you come
    /// back to the window, match them again and refresh the races, so a new mod is found without restarting.
    /// </summary>
    private async Task RescanIfChangedAsync()
    {
        if (_ws == null || _rescanning || _cts != null) return;
        _rescanning = true;
        try
        {
            var ws = _ws;
            var scanLog = new Progress<string>(m => { Log(m); SetStatus(m); });
            if (await Task.Run(() => LibraryScanner.ScanIfChanged(ws, scanLog))) await LoadRacesAsync();
        }
        catch (Exception ex) { Log("Couldn't match your cars and tracks: " + ex.Message); }
        finally { _rescanning = false; }
    }

    private async Task LoadRacesAsync()
    {
        if (_ws == null || _resolver == null) return;
        SetStatus("Loading races ...");
        var rosters = await Task.Run(() => _ws.LoadRosters());
        _catalog = await Task.Run(() => _resolver.LoadCatalog());

        var selectedId = _current?.Roster.Id;
        _races = rosters
            .Where(r => r.Track != null && r.Entries.Count > 0)
            .OrderByDescending(r => r.Year ?? 0).ThenByDescending(r => r.Date ?? "").ThenBy(r => r.Name)
            .Select(r => new RaceItem { Roster = r })
            .ToList();

        _updatingUi = true;
        SeriesBox.ItemsSource = new[] { "All series" }.Concat(_races.Select(r => r.Category).Where(c => c != "").Distinct().Order()).ToList();
        SeriesBox.SelectedIndex = 0;
        YearBox.ItemsSource = new[] { "All years" }.Concat(_races.Select(r => r.Year).Where(y => y > 0).Distinct().OrderDescending().Select(y => y.ToString())).ToList();
        YearBox.SelectedIndex = 0;
        _updatingUi = false;

        _view = CollectionViewSource.GetDefaultView(_races);
        _view.Filter = o => Matches((RaceItem)o);
        RaceList.ItemsSource = _view;
        UpdateCount();
        SetStatus($"{_races.Count} races loaded. Checking which ones your installed content can run ...");

        if (selectedId != null && _races.FirstOrDefault(r => r.Roster.Id == selectedId) is { } again) RaceList.SelectedItem = again;
        await ComputeReadinessAsync();
    }

    /// <summary>Checks every race against the installed cars and tracks (in the background).</summary>
    private async Task ComputeReadinessAsync()
    {
        if (_resolver == null || _catalog == null) return;
        var run = ++_readinessRun;
        var items = _races.ToList();
        var resolver = _resolver;
        var catalog = _catalog;
        var library = StandInBox.IsChecked == true ? _ws?.Library : null;
        await Task.Run(() =>
        {
            const int batch = 150;
            for (var i = 0; i < items.Count && run == _readinessRun; i += batch)
            {
                var results = items.Skip(i).Take(batch).Select(item =>
                {
                    try
                    {
                        var c = resolver.Resolve(item.Roster, catalog);
                        var standIns = library == null ? 0 : c.Cars.Count(x => !x.Installed && StandInPicker.CanStandIn(item.Roster, x.Entry, library));
                        return (item, c.Cars.Count(x => x.Installed), c.Cars.Count, c.Track?.Installed == true, standIns);
                    }
                    catch { return (item, 0, item.Roster.Entries.Count, false, 0); }
                }).ToList();
                Dispatcher.Invoke(() => { foreach (var (item, r, t, tr, s) in results) item.SetReadiness(r, t, tr, s); });
            }
        });
        if (run != _readinessRun) return;
        var runnable = _races.Count(r => r.Runnable);
        var full = _races.Count(r => r.FullyReady);
        SetStatus($"{_races.Count} races. {runnable} can run with your installed content ({full} with every real car).");
        if (ReadyOnlyBox.IsChecked == true) _view?.Refresh();
        UpdateCount();
    }

    private void FillWeather()
    {
        if (_ws == null) return;
        var dir = Path.Combine(_ws.ContentFolder, "weather");
        var list = Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).Cast<string>().Order().ToList() : new List<string>();
        if (list.Count == 0) list.Add("3_clear");
        _updatingUi = true;
        WeatherBox.ItemsSource = list;
        WeatherBox.SelectedItem = list.Contains("3_clear") ? "3_clear" : list[0];
        _updatingUi = false;
    }

    /// <summary>Re-checks what's installed (CSP, the in-game app, the Safety Car), which can change while this
    /// program is open. True when something changed.</summary>
    private bool RefreshApps()
    {
        if (_ws == null || _apps == null) return false;
        var now = CompanionApps.Detect(_ws.AcRoot);
        if (now == _apps) return false;
        _apps = now;
        UpdateCompanionText();
        return true;
    }

    private void UpdateCompanionText()
    {
        if (_apps == null) return;
        // A TextBlock, not a string: WPF would treat "_" in "ferrari_458_s3" as a keyboard-shortcut marker.
        SafetyCarBox.Content = new TextBlock { Text = $"Add a Safety Car ({_apps.SafetyCarModel}) at the back of the grid" };
        SafetyCarBox.ToolTip = "Optional. The Historic Roster in-game app keeps the Safety Car in its pit box and sends it out\n" +
                               "when a car is stopped on the track; it leads the field for two laps once the track is clear.";
        var canSafety = _apps.SafetyCarInstalled && _apps.Csp;
        SafetyCarBox.IsEnabled = canSafety;
        if (!canSafety) SafetyCarBox.IsChecked = false;
        SafetyCarBox.Visibility = Visibility.Visible;

        var notes = new List<string>();
        if (!_apps.Csp) notes.Add("Custom Shaders Patch isn't installed: no Safety Car, grid layout or watched races.");
        else if (!_apps.SafetyCarInstalled) notes.Add($"Safety Car {_apps.SafetyCarModel} isn't installed, so a race has no Safety Car.");
        CompanionText.Text = string.Join(" ", notes);
    }

    // ================================================================== filtering

    private bool Matches(RaceItem r)
    {
        if (SeriesBox.SelectedIndex > 0 && r.Category != (string)SeriesBox.SelectedItem) return false;
        if (YearBox.SelectedIndex > 0 && r.Year.ToString() != (string)YearBox.SelectedItem) return false;
        if (_aiYearFrom is { } from && r.Year < from) return false;
        if (_aiYearTo is { } to && r.Year > to) return false;
        if (ReadyOnlyBox.IsChecked == true && !r.Runnable) return false;
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        var text = $"{r.Name} {r.Category} {r.Year} {r.Roster.Track?.Name} {r.Roster.Series}";
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => TextFold.Contains(text, w));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _view == null) return;
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_view == null) return;
        ListCountText.Text = $"{_view.Cast<object>().Count()} of {_races.Count} races";
    }

    // ================================================================== race setup

    private void RaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RaceList.SelectedItem is RaceItem item) ShowRace(item);
    }

    private void ShowRace(RaceItem item)
    {
        if (_ws == null || _resolver == null) return;
        _current = item;
        _preferredDriver = null;
        _content = _resolver.Resolve(item.Roster, _catalog);
        var c = _content;
        var r = item.Roster;
        var lib = StandInBox.IsChecked == true ? _ws.Library : null;
        item.SetReadiness(c.Cars.Count(x => x.Installed), c.Cars.Count, c.Track?.Installed == true,
            lib == null ? 0 : c.Cars.Count(x => !x.Installed && StandInPicker.CanStandIn(r, x.Entry, lib)));
        ResetTrackButton.Visibility = r.Track != null && TrackChoices.Has(_ws, r.Track) ? Visibility.Visible : Visibility.Collapsed;

        EmptyText.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        ActionBar.Visibility = Visibility.Visible;

        RaceTitle.Text = r.Name;
        RaceSubtitle.Text = string.Join("  ·  ", new[] { r.Series, r.Category, r.Date }.Where(s => !string.IsNullOrWhiteSpace(s)));
        RaceSource.Text = r.Source != null ? "Source: " + r.Source : "";
        RaceNotes.Text = r.Notes ?? "";

        // Track
        var t = c.Track;
        TrackName.Text = r.Track?.Name ?? "";
        TrackFolder.Text = t?.Folder != null ? $"AC track: {t.Folder}{(t.Layout != null ? " / " + t.Layout : "")}{(t.PitBoxes != null ? $"  ·  {t.PitBoxes} pit boxes" : "")}" : "No AC track known for this circuit";
        if (t?.Installed == true)
        {
            var ui = Ui.TrackUiFolder(_ws.ContentFolder, t.Folder!, t.Layout);
            TrackPreview.Source = Ui.LoadImage(Path.Combine(ui, "preview.png"), 0); // native res: the box can be ~900px wide
            TrackOutline.Source = Ui.LoadImage(Path.Combine(ui, "outline.png"), 240);
            TrackStatus.Text = "Installed";
            TrackStatus.Foreground = Ui.Ok;
        }
        else
        {
            TrackPreview.Source = null;
            TrackOutline.Source = null;
            TrackStatus.Text = t?.Folder == null ? "Not installed: the real track is needed (see below)" : "Not installed: get it below";
            TrackStatus.Foreground = Ui.Bad;
        }
        TrackNoImage.Visibility = TrackPreview.Source == null ? Visibility.Visible : Visibility.Collapsed;
        TrackOutlineChip.Visibility = TrackOutline.Source == null ? Visibility.Collapsed : Visibility.Visible;

        // Missing content
        var missing = Workspace.MissingItems(c);
        MissingCard.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MissingTitle.Text = $"MISSING ON THIS PC ({missing.Count}) - ONLY THE REAL CARS AND TRACK ARE USED";
        MissingList.ItemsSource = missing.Select(m => new MissingRow
        {
            Item = m,
            Car = m.Kind == "car" ? c.Cars.FirstOrDefault(x => !x.Installed && x.Label == m.Label) : null,
        }).ToList();

        // Laps default: the chosen share of the real race distance
        _updatingUi = true;
        LapsBox.Text = RaceSetup.LapsFor(r, _settings.DistancePercent).ToString();
        DistanceText.Text = $"real: {RaceSetup.RealLaps(r)} laps";
        _updatingUi = false;

        BuildSetup();
        _ = LoadConditionsAsync(item);
    }

    // ================================================================== real conditions

    private async Task LoadConditionsAsync(RaceItem item)
    {
        _cond = null;
        _condWeather = null;
        if (RealCondBox.IsChecked != true) { _condLoading = false; CondText.Text = ""; ApplySettings(); return; }
        _condLoading = true;
        CondText.Text = "Looking up the real race-day start time and weather ...";
        Conditions? c;
        var lib = _ws?.Library;
        try { c = await RealConditions.GetAsync(item.Roster, default, RealRaceTime.Position(item.Roster, _content?.Track, lib)); }
        catch (Exception ex) { c = null; Log("Real conditions: " + ex.Message); }
        if (_current != item || RealCondBox.IsChecked != true) return;
        _condLoading = false;
        if (c == null)
        {
            // No weather record: still the real start time when the roster has the event's usual one.
            var usual = RealRaceTime.UsualStart(item.Roster);
            if (usual != null)
            {
                _updatingUi = true;
                TimeSlider.Value = Math.Round(usual.Value.TotalHours * 4) / 4;
                _updatingUi = false;
            }
            CondText.Text = "COULDN'T GET THE REAL RACE-DAY WEATHER (needs the internet, races from 1940 on): check the time and weather above." +
                            (usual != null ? $" Start set to the event's usual {usual:hh\\:mm} local." : "");
            CondText.Foreground = Ui.Bad;
            ApplySettings();
            return;
        }
        CondText.ClearValue(TextBlock.ForegroundProperty);
        var (weather, note) = RealConditions.PickWeather(c, (WeatherBox.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>());
        _cond = c;
        _condWeather = weather;
        _updatingUi = true;
        TimeSlider.Value = Math.Round(c.LocalStart.TotalHours * 4) / 4;
        TempSlider.Value = Math.Clamp(Math.Round(c.AirTemp), TempSlider.Minimum, TempSlider.Maximum);
        WeatherBox.SelectedItem = weather;
        _updatingUi = false;
        var night = c.IsNight ? " Night race: the track's lights and the night sky need Custom Shaders Patch." : "";
        CondText.Text = $"Real race{(c.Date != null ? $" ({c.Date:d MMM yyyy})" : "")}: {c.Describe()}. Using {weather} with the real sky and sun.{night}{(note != null ? " " + note : "")}";
        ApplySettings();
    }

    private void RealCond_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        if (_current != null) _ = LoadConditionsAsync(_current);
    }

    /// <summary>(Re)builds the offline grid from the resolved race and the current settings.</summary>
    private void BuildSetup()
    {
        if (_content == null || _ws == null || _apps == null) return;
        RefreshApps();
        var c = _content;
        // The in-game app (it needs CSP) holds the Safety Car in the pits; without it the car would race like any AI car.
        var safety = SafetyCarBox.IsChecked == true && _apps.Csp && _apps.SafetyCarInstalled;
        _setup = null;
        _safetyCarNote = null;
        string? reason = null;
        if (c.Track is not { Installed: true }) reason = "The real track isn't installed.";
        else
        {
            // The real grid fills the pit boxes first; the Safety Car never takes a real car's place.
            var max = c.Track.PitBoxes ?? 40;
            try
            {
                _setup = RaceSetup.Create(c, Math.Max(1, max), _ws.Library, StandInBox.IsChecked == true);
                _safetyCarNote = safety ? _apps.TryAddSafetyCar(_setup, c.Track.PitBoxes) : null;
                if (PickSeat(_setup) is { } seat) _setup.SetPlayer(seat);
                _setup.PlayerName = _settings.PlayerName;
            }
            catch (InvalidOperationException ex)
            {
                reason = ex.Message + (StandInBox.IsChecked == true ? " No installed car of the same class can stand in." : "");
            }
        }

        // Seat choice
        _updatingUi = true;
        if (_setup != null)
        {
            var seats = _setup.Grid.Where(s => !s.IsSafetyCar)
                .Select(s => new SeatItem(s, $"P{s.GridPosition}  {s.Driver}  -  {s.Car.Entry.Car ?? s.Car.Model}{(s.IsStandIn ? $"  (stand-in: {s.Car.Model})" : "")}")).ToList();
            SeatBox.ItemsSource = seats;
            SeatBox.SelectedItem = seats.First(s => s.Slot.IsPlayer);
        }
        else SeatBox.ItemsSource = null;
        SettingsPanel.IsEnabled = _setup != null;
        _updatingUi = false;

        ApplySettings();
        FillGrid();
        WriteButton.IsEnabled = StartButton.IsEnabled = CmButton.IsEnabled = _setup != null;
        ApplyWatch();
        ActionText.Text = _setup == null ? reason + " Get it from the list above." : Summary();
        ActionText.Foreground = _setup == null ? Ui.Bad : (System.Windows.Media.Brush)FindResource("Ink");
        if (_setup != null && _safetyCarNote != null) Log(_safetyCarNote);
    }

    /// <summary>Why the Safety Car wasn't added to the current grid (null when added or not wanted).</summary>
    private string? _safetyCarNote;

    /// <summary>
    /// The seat a race opens with: the one picked by hand for this race, else your season's driver,
    /// else the default seat setting (pole, random midfield, favourite team or driver). Null = pole.
    /// </summary>
    private RaceSlot? PickSeat(RaceSetup setup)
    {
        var drivers = setup.Grid.Where(s => !s.IsSafetyCar).ToList();
        RaceSlot? ByDriver(string? name) => string.IsNullOrWhiteSpace(name) ? null
            : drivers.FirstOrDefault(s => s.Driver.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? drivers.FirstOrDefault(s => TextFold.Contains(s.Driver, name));
        RaceSlot? ByTeam(string? team) => string.IsNullOrWhiteSpace(team) ? null
            : drivers.Where(s => TextFold.Contains(s.Car.Entry.Team, team) || TextFold.Contains(team, s.Car.Entry.Team ?? "\u0001"))
                .OrderBy(s => s.GridPosition).FirstOrDefault();

        if (ByDriver(_preferredDriver) is { } chosen) return chosen;
        if (SeasonState.ActiveKey is { } key && key == Seasons.KeyOf(setup.Roster) && SeasonState.Load(key) is { } season
            && ByDriver(season.Driver) is { } seasonSeat) return seasonSeat;

        switch ((SeatModeBox.SelectedItem as ComboBoxItem)?.Tag as string)
        {
            case "Midfield":
                var third = drivers.Count / 3;
                var mid = drivers.Skip(third).Take(Math.Max(1, drivers.Count - 2 * third)).ToList();
                return mid[Random.Shared.Next(mid.Count)];
            case "Team":
                return ByTeam(_settings.FavoriteTeam) ?? ByDriver(_settings.FavoriteDriver);
            case "Driver":
                return ByDriver(_settings.FavoriteDriver) ?? ByTeam(_settings.FavoriteTeam);
            default:
                return null;
        }
    }

    private void SeatMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.SeatMode = (SeatModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Pole";
        _preferredDriver = null;
        BuildSetup();
    }

    private void Favourite_Click(object sender, RoutedEventArgs e)
    {
        if (_setup == null) return;
        var p = _setup.Player;
        _settings.FavoriteDriver = p.Driver;
        _settings.FavoriteTeam = p.Car.Entry.Team;
        _updatingUi = true;
        if ((SeatModeBox.SelectedItem as ComboBoxItem)?.Tag is "Pole" or "Midfield")
            SeatModeBox.SelectedItem = SeatModeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == "Team");
        _updatingUi = false;
        SaveSettings();
        SetStatus($"Favourites: {p.Driver}{(p.Car.Entry.Team != null ? ", " + p.Car.Entry.Team : "")}. New races open in this seat.");
    }

    private void StandIn_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.StandIns = StandInBox.IsChecked == true;
        if (_current != null) ShowRace(_current);
        _ = ComputeReadinessAsync();
    }

    private void FillGrid()
    {
        if (_content == null || _ws == null) return;
        var rows = new List<GridRow>();
        // Multi-class race (LMP, GT ... on one grid): a Class column and real results within the class.
        var multiClass = RaceClasses.IsMultiClass(_content.Roster.Entries);
        var classPos = multiClass ? RaceClasses.Positions(_content.Roster.Entries, e => e.Position) : new();
        string ClassOf(ResolvedCar c) => multiClass ? RaceClasses.Of(c.Entry) ?? "" : "";
        int? ClassPos(ResolvedCar c) => classPos.TryGetValue(c.Entry, out var p) ? p : null;
        if (_setup != null)
        {
            rows.AddRange(_setup.Grid.Select(s => new GridRow
            {
                Class = s.IsSafetyCar ? "" : ClassOf(s.Car),
                RealClassPosition = s.IsSafetyCar ? null : ClassPos(s.Car),
                Car = s.Car,
                Slot = s,
                Setup = _setup,
                Position = s.IsSafetyCar ? "SC" : s.GridPosition.ToString(),
                Thumbnail = Ui.CarPreview(_ws.ContentFolder, s.Car.Model, s.Car.Skin, 184),
                IsPlayer = s.IsPlayer,
            }));
            rows.AddRange(_setup.LeftOut.Select(car => new GridRow
            {
                Class = ClassOf(car),
                RealClassPosition = ClassPos(car),
                Car = car,
                Position = "-",
                Thumbnail = car.Installed ? Ui.CarPreview(_ws.ContentFolder, car.Model, car.Skin, 184) : null,
            }));
        }
        else
        {
            rows.AddRange(RosterOrder.Order(_content.Cars, x => x.Entry, GridOrder.StartingGrid).Select(car => new GridRow
            {
                Class = ClassOf(car),
                RealClassPosition = ClassPos(car),
                Car = car,
                Position = car.Entry.GridPosition?.ToString() ?? "-",
                Thumbnail = car.Installed ? Ui.CarPreview(_ws.ContentFolder, car.Model, car.Skin, 184) : null,
            }));
        }
        GridList.ItemsSource = rows;
        ClassColumn.Width = multiClass ? 64 : 0;
        RealResultColumn.Width = multiClass ? 118 : 84;
        var real = _setup?.Grid.Count(s => !s.IsSafetyCar && !s.IsStandIn) ?? 0;
        var stand = _setup?.Grid.Count(s => s.IsStandIn) ?? 0;
        GridTitle.Text = _setup == null
            ? $"REAL ENTRY LIST ({rows.Count})"
            : $"STARTING GRID - {real} REAL CARS{(stand > 0 ? $" + {stand} STAND-INS" : "")}{(_setup.Grid.Any(s => s.IsSafetyCar) ? " + SAFETY CAR" : "")}" +
              (_setup.LeftOut.Count > 0 ? $"  ·  {_setup.LeftOut.Count} LEFT OUT" : "");
        if (multiClass) GridTitle.Text += $"  ·  {RaceClasses.Ordered(_content.Roster.Entries).Count} CLASSES";
        UpdateLiveryBar();
    }

    // ================================================================== liveries

    /// <summary>Cars on the grid whose drivers have no livery of their own (they'd all show the same one).</summary>
    private List<LiveryNeed> LiveryNeeds()
    {
        if (_setup == null || _ws == null || _current == null) return new();
        // Real cars only: a stand-in isn't the driver's car, so it doesn't get a livery made for them.
        var cars = _setup.Grid.Where(s => !s.IsSafetyCar && !s.IsStandIn && s.Car.Model != null).Select(s => (s.Car, s.Car.Model!));
        return Liveries.Needs(cars, _ws.Library, _current.Roster.Year);
    }

    private void UpdateLiveryBar()
    {
        var needs = LiveryNeeds();
        var generated = _setup?.Grid.Any(s => s.Car.Skin?.StartsWith(Liveries.GeneratedPrefix, StringComparison.OrdinalIgnoreCase) == true) == true;
        LiveryBar.Visibility = needs.Count > 0 || generated ? Visibility.Visible : Visibility.Collapsed;
        var without = needs.Sum(n => n.Without.Count);
        LiveryText.Text = without == 0
            ? "Every driver has a livery of their own (some are generated)."
            : $"LIVERIES: {without} driver{(without == 1 ? "" : "s")} {(without == 1 ? "has" : "have")} no livery of their own and would show someone else's " +
              $"({string.Join(", ", needs.Select(n => $"{n.CarName}: {n.Without.Count} of {n.Drivers}"))}).";
        FindLiveriesButton.IsEnabled = GenerateLiveriesButton.IsEnabled = without > 0 && _cts == null;
        RemoveGeneratedButton.Visibility = generated ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FindLiveries_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _resolver == null || _current == null) return;
        var needs = LiveryNeeds();
        if (needs.Count == 0) return;
        var ws = _ws; var resolver = _resolver; var roster = _current.Roster;
        var downloads = new GetOptions().DownloadsFolder;
        int StillWithout()
        {
            ContentLibrary.Invalidate();
            return needs.Sum(n => n.Without.Count(d => ws.Library.OwnSkin(n.Model, d) == null));
        }
        await RunAsync("Finding liveries", (log, ct) => Liveries.FindAsync(needs, roster, resolver, ws.ContentFolder, downloads, 30, log, ct, StillWithout));
        ContentLibrary.Invalidate();
        if (_current != null) ShowRace(_current);
    }

    private async void GenerateLiveries_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _current == null || _setup == null) return;
        var needs = LiveryNeeds();
        if (needs.Count == 0) return;
        var ws = _ws;
        var lib = ws.Library;
        // Base: a real (not generated) livery of the car, the one these drivers show now if possible.
        var jobs = needs.Select(n =>
        {
            var car = lib.Cars.First(c => c.Folder.Equals(n.Model, StringComparison.OrdinalIgnoreCase));
            var shown = _setup.Grid.Where(s => s.Car.Model == n.Model).Select(s => s.Car.Skin).FirstOrDefault(s => s != null);
            var baseSkin = car.Skins.Select(s => s.Folder)
                .Where(f => !f.StartsWith(Liveries.GeneratedPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f == shown).FirstOrDefault();
            return (Need: n, BaseSkin: baseSkin);
        }).ToList();
        var noBase = jobs.Where(j => j.BaseSkin == null).ToList();
        foreach (var j in noBase) Log($"{j.Need.CarName}: no livery installed to start from, skipped.");
        jobs = jobs.Where(j => j.BaseSkin != null).ToList();
        if (jobs.Count == 0) return;

        var count = jobs.Sum(j => j.Need.Without.Count);
        if (MessageBox.Show(
                $"Generate {count} liveries?\n\n" + string.Join("\n", jobs.Select(j => $"• {j.Need.CarName}: {j.Need.Without.Count} driver(s), starting from \"{j.BaseSkin}\"")) +
                "\n\nEach driver gets a copy of that livery recoloured in a colour of their own, with their name, number and team. " +
                "The race number from the race information is painted on the car's race-number textures (where the car has them). New folders starting with \"hr_\" are added; your existing liveries are not changed.",
                "Generate liveries", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var roster = _current.Roster;
        var ai = _settings.Ai.Configured && _settings.Ai.TeamColours ? _settings.Ai.CreateAssistant() : null;
        await RunAsync("Generating liveries", (log, ct) => Task.Run(async () =>
        {
            Dictionary<string, double?>? hues = null;
            if (ai != null)
            {
                try
                {
                    log.Report("Asking the AI for each team's real livery colour ...");
                    hues = await ai.TeamHuesAsync(roster, jobs.SelectMany(j => j.Need.Without).ToList(), ct);
                    log.Report($"Real team colours from {ai.Client.LastUsed} for {hues.Count(h => h.Value != null)} drivers; the others get a colour of their own.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Report("No real team colours (AI: " + ex.Message + "); using distinct colours.");
                }
            }
            foreach (var (need, baseSkin) in jobs)
            {
                var r = LiveryGenerator.Generate(ws.ContentFolder, need.Model, baseSkin!, need.Without, log, ct, hues);
                foreach (var n in r.Notes) log.Report(n);
                log.Report($"{need.CarName}: {r.Created.Count} liveries made ({string.Join(", ", r.Created)}).");
            }
        }, ct));
        ContentLibrary.Invalidate();
        if (_current != null) ShowRace(_current);
    }

    private void RemoveGenerated_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _setup == null) return;
        var models = _setup.Grid.Where(s => s.Car.Model != null).Select(s => s.Car.Model!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (MessageBox.Show($"Delete the generated liveries (folders starting with \"{Liveries.GeneratedPrefix}\") of {string.Join(", ", models)}?",
                "Remove generated liveries", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var n = models.Sum(m => Liveries.RemoveGenerated(_ws.ContentFolder, m));
        Log($"Removed {n} generated liveries.");
        if (_current != null) ShowRace(_current);
    }

    private string Summary()
    {
        var s = _setup!;
        var p = s.Player;
        var track = $"{s.Track.Folder}{(s.Track.Layout != null ? "/" + s.Track.Layout : "")}";
        var sessions = s.Weekend switch
        {
            RaceWeekend.QualifyingAndRace => "qualifying + race",
            RaceWeekend.PracticeQualifyingAndRace => "practice + qualifying + race",
            _ => $"race from P{p.GridPosition}",
        };
        var car = p.IsStandIn ? $"{p.Car.Model} standing in for the {p.Car.Entry.Car}" : p.Car.Entry.Car ?? p.Car.Model;
        var you = s.Watch ? $"AI race to watch, camera on {p.Driver}"
            : string.IsNullOrWhiteSpace(s.PlayerName) ? $"You: {p.Driver}" : $"You: {s.NameOf(p)} in {p.Driver}'s seat";
        var racers = s.Grid.Count(x => !x.IsSafetyCar);
        var sc = s.Grid.Any(x => x.IsSafetyCar) ? " + Safety Car" : "";
        return (_safetyCarNote != null ? _safetyCarNote + "  " : "") +
               $"{you}, {car} ({p.Car.Skin ?? "default livery"})  ·  {sessions}  ·  {s.Laps} laps  ·  " +
               $"{racers} cars{sc}  ·  {track}  ·  {s.Weather}";
    }

    private void ApplySettings()
    {
        TimeText.Text = TimeSpan.FromHours(TimeSlider.Value).ToString(@"hh\:mm");
        if (_setup == null) return;
        _setup.Weekend = (RaceWeekend)Math.Max(0, WeekendBox.SelectedIndex);
        if (int.TryParse(LapsBox.Text, out var laps) && laps > 0) _setup.Laps = laps;
        _setup.AiLevel = AiLevelSlider.Value;
        _setup.AiAggression = AiAggressionSlider.Value;
        _setup.RealSkill = RealSkillBox.IsChecked == true;
        _setup.SkillSpread = SpreadSlider.Value;
        _setup.ApplySkill();
        SpreadSlider.IsEnabled = _setup.RealSkill;
        if (GridList.ItemsSource is IEnumerable<GridRow> rows) foreach (var row in rows) row.RefreshAi();
        // Real day, place and conditions first; then what's set above (the real values unless changed by hand).
        if (_cond != null && RealCondBox.IsChecked == true && _ws != null)
            RealRaceTime.Apply(_setup, _cond, _ws.Library, _ws.AcRoot, _ws.RaceIniPath);
        else
            RealRaceTime.ApplyPlace(_setup, _ws?.Library);
        var road = _setup.RoadTemperature - _setup.AmbientTemperature;
        _setup.TimeOfDay = TimeSpan.FromHours(TimeSlider.Value);
        _setup.Weather = WeatherBox.SelectedItem as string ?? "3_clear";
        // A weather folder picked by hand wins over the real weather type.
        if (_setup.Weather != _condWeather) _setup.WeatherFxType = null;
        _setup.AmbientTemperature = (int)TempSlider.Value;
        _setup.RoadTemperature = (int)TempSlider.Value + (_cond != null ? road : 8);
        TimeText.Text = TimeSpan.FromHours(TimeSlider.Value).ToString(@"hh\:mm") + (_setup.TimeOfDay.TotalHours is < 6.5 or >= 19.5 ? " night" : "");
        ActionText.Text = Summary() + (_setup.NeedsCspForTime && _apps is { Csp: false }
            ? "  ·  WARNING: this start time needs Custom Shaders Patch (plain AC only shows 08:00-18:00)" : "");
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        ApplySettings();
    }

    private void Seat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _setup == null || SeatBox.SelectedItem is not SeatItem seat) return;
        _setup.SetPlayer(seat.Slot);
        _preferredDriver = seat.Slot.Driver;
        foreach (var row in (IEnumerable<GridRow>)GridList.ItemsSource) row.IsPlayer = row.Slot?.IsPlayer == true;
        ActionText.Text = Summary();
    }

    private void SafetyCar_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        SaveSettings(); // (kept straight away: a Safety Car is a choice made once, not per race)
        if (_content == null) return;
        BuildSetup();
    }

    private void Distance_Click(object sender, RoutedEventArgs e)
    {
        var pct = int.Parse((string)((Button)sender).Tag);
        _settings.DistancePercent = pct; // the next races open with this share too
        if (_current != null) LapsBox.Text = RaceSetup.LapsFor(_current.Roster, pct).ToString();
    }

    private void GridList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Let the page scroll when the mouse is over the grid.
        e.Handled = true;
        SetupPanel.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    // ================================================================== write / start

    private bool WriteRace()
    {
        if (_setup == null || _ws == null) return false;
        if (_condLoading)
        {
            MessageBox.Show("Still looking up the real race-day start time and weather. Try again in a moment.", "Historic Roster",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        ApplySettings();
        if (_setup.Laps > 120 &&
            MessageBox.Show($"The real race distance is {_setup.Laps} laps. Write it like that?", "Historic Roster",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        if (_setup.Watch || _apps?.Csp == true)
        {
            // The in-game app runs the race: the grid, the Safety Car, and in a watched race the AI and the camera.
            try
            {
                if (InGameApp.EnsureCurrent(_ws.AcRoot, BundledInGameApp) is { } done)
                    Log($"Historic Roster in-game app {done} in {InGameApp.InstalledFolder(_ws.AcRoot)} (it runs the grid, the Safety Car and watched races).");
            }
            catch (Exception ex)
            {
                if (_setup.Watch)
                {
                    MessageBox.Show("Couldn't install the Historic Roster in-game app, which a watched race needs:\n" + ex.Message,
                        "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
                Log("Couldn't install the Historic Roster in-game app (no grid layout or Safety Car in this race): " + ex.Message);
            }
        }
        var path = RaceIniWriter.Write(_setup, _ws.RaceIniPath);
        UpdateSimCastContext();
        RecordRace();
        Log($"Wrote {path}: {_current?.Name}, {_setup.Grid.Count} cars, {_setup.Laps} laps, start {_setup.TimeOfDay:hh\\:mm} local" +
            $"{(_setup.RaceDate != null ? $" on {_setup.RaceDate:d MMM yyyy}" : "")}, {_setup.Weather}" +
            $"{(_setup.WeatherFxType != null ? $" (real weather type {_setup.WeatherFxType} via {_setup.WeatherController})" : "")}, {_setup.AmbientTemperature} °C (previous race.ini backed up).");
        SetStatus("Race written to Assetto Corsa.");
        return true;
    }

    /// <summary>
    /// SimCast's race context for the race just written (race.ini or Content Manager presets): written with the setting
    /// on, else the last race's removed, so SimCast never takes an older race on the same track for this one.
    /// </summary>
    private void UpdateSimCastContext()
    {
        if (_setup == null) return;
        var send = SimCastBox.IsChecked == true && SimCastBox.Visibility == Visibility.Visible;
        if (SimCastContext.Update(send, _setup, RealCondBox.IsChecked == true ? _cond : null,
                typeof(MainWindow).Assembly.GetName().Version?.ToString(3), Log, SimCastLauncher.Exe() != null) is { } ctx)
            Log($"SimCast race context written to {ctx}.");
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        if (WriteRace()) ActionText.Text = "Written to race.ini. " + Summary();
    }

    private void Cm_Click(object sender, RoutedEventArgs e)
    {
        if (_setup == null) return;
        var presets = CmPresetWriter.PresetsFolder();
        if (presets == null)
        {
            MessageBox.Show("Content Manager doesn't seem to be set up on this PC (no %LOCALAPPDATA%\\AcTools Content Manager folder).",
                "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            ApplySettings();
            var name = _current?.Name ?? _setup.Roster.Name;
            var r = CmPresetWriter.Write(_setup, presets, name);
            // (The preset's grid is in the same order with the real names, so SimCast matches by index or by name.)
            UpdateSimCastContext();
            RecordRace();
            Log($"Saved Content Manager presets: {r.QuickDrivePreset} and {r.RaceGridPreset}");
            var weekend = _setup.Weekend == RaceWeekend.RaceOnly ? "" : " (race only: Content Manager's Quick Drive race has no practice or qualifying)";
            SetStatus("Saved to Content Manager.");
            ActionText.Text = $"In Content Manager: Drive > Quick Drive > presets > {CmPresetWriter.SubFolder} > {CmPresetWriter.SafeFileName(name)}{weekend}. Restart Content Manager if it was open.";
        }
        catch (Exception ex)
        {
            Log("Couldn't save Content Manager presets: " + ex.Message);
            MessageBox.Show("Couldn't save the Content Manager presets:\n" + ex.Message, "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null) return;
        if (AcLauncher.AcRunning)
        {
            MessageBox.Show("Assetto Corsa is already running. Close it first; it only reads the race when it starts.",
                "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AcLauncher.SteamRunning && AcLauncher.ContentManagerExe() == null &&
            MessageBox.Show("Steam doesn't seem to be running; Assetto Corsa needs it. Start anyway?", "Historic Roster",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            // Something was installed or removed since the race was set up: rebuild, so the Safety Car follows it.
            if (RefreshApps()) BuildSetup();
            if (!WriteRace()) return;
            // Every race (driven or watched) starts through Content Manager when it's installed.
            var how = AcLauncher.Start(_ws.AcRoot, _ws.RaceIniPath);
            Log($"Starting Assetto Corsa {how} ...");
            SetStatus($"Assetto Corsa is starting {how}.");
            // A watched race has no driver: CrewChief, SimHub and the like are muted until the game closes.
            if (_setup?.Watch == true) CompanionMute.DuringWatchedRace(m => Dispatcher.BeginInvoke(() => Log(m)));
            if (StartSimCastBox.IsChecked == true && StartSimCastBox.Visibility == Visibility.Visible)
            {
                // Never let the commentary stop a race from starting.
                try { Log(SimCastLauncher.StartIfNeeded()); }
                catch (Exception ex) { Log("Couldn't start SimCast: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Log("Couldn't start Assetto Corsa: " + ex.Message);
            SetStatus("Couldn't start Assetto Corsa.");
            MessageBox.Show("Couldn't start Assetto Corsa:\n" + ex.Message, "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================================================================== results and seasons

    /// <summary>Remembers the race just sent to AC (or Content Manager), so its result can be compared afterwards.</summary>
    private void RecordRace()
    {
        if (_setup == null) return;
        string? seasonKey = null;
        if (SeasonState.ActiveKey is { } key && key == Seasons.KeyOf(_setup.Roster) && SeasonState.Load(key) is { } s
            && s.Driver.Equals(_setup.Player.Driver, StringComparison.OrdinalIgnoreCase))
            seasonKey = key;
        LastRace.Record(_setup, seasonKey).Save();
    }

    private bool _checkingResult;

    /// <summary>Shows a new race result (once), and adds it to the season when the race was a season round.</summary>
    private void CheckForResult()
    {
        if (_checkingResult || _races.Count == 0 || AcLauncher.AcRunning) return;
        var last = LastRace.Load();
        if (last == null || _races.FirstOrDefault(r => r.Roster.Id == last.RosterId) is not { } race) return;
        var cmp = RaceOutReader.Compare(last, race.Roster);
        if (cmp == null) return;
        _checkingResult = true;
        try
        {
            last.HandledResultUtc = cmp.ResultUtc;
            last.Save();
            Log($"Race result: {cmp.Headline}");
            var seasonNote = RecordSeasonResult(cmp);
            new ResultWindow(cmp, seasonNote, AiReportAction(cmp, race.Roster)) { Owner = this }.ShowDialog();
        }
        finally { _checkingResult = false; }
    }

    private string? RecordSeasonResult(RaceComparison cmp)
    {
        if (cmp.Race.SeasonKey is not { } key || SeasonState.Load(key) is not { } season) return null;
        season.Results[cmp.Race.RosterId] = cmp.Order;
        season.Save();
        var rounds = Seasons.Rounds(_races.Select(r => r.Roster), key);
        var me = Seasons.Standings(rounds, season).FirstOrDefault(s => s.IsPlayer);
        return me == null ? $"Added to your {season.Name} season." : $"Added to your {season.Name} season: P{me.Position} in the championship with {me.Points:0.#} points.";
    }

    private void LastResult_Click(object sender, RoutedEventArgs e)
    {
        var last = LastRace.Load();
        var race = last == null ? null : _races.FirstOrDefault(r => r.Roster.Id == last.RosterId);
        var cmp = last != null && race != null ? RaceOutReader.Compare(last, race.Roster, includeHandled: true) : null;
        if (cmp == null)
        {
            MessageBox.Show(last == null
                    ? "No race has been started from here yet."
                    : $"No result for {last.RosterName} yet. Assetto Corsa writes it when the race session ends.",
                "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new ResultWindow(cmp, null, AiReportAction(cmp, race!.Roster)) { Owner = this }.ShowDialog();
    }

    private void Season_Click(object sender, RoutedEventArgs e)
    {
        if (_races.Count == 0) return;
        var w = new SeasonWindow(_races.Select(r => r.Roster).ToList(), _current?.Roster, _setup?.Player.Driver) { Owner = this };
        if (w.ShowDialog() == true && w.RaceToSetUp is { } id) SelectRace(id, w.DriverToSetUp);
    }

    /// <summary>Opens a race in a given seat (from the season window).</summary>
    private void SelectRace(string rosterId, string? driver)
    {
        if (_races.FirstOrDefault(r => r.Roster.Id == rosterId) is not { } item) return;
        if (_view != null && !_view.Cast<RaceItem>().Contains(item))
        {
            _updatingUi = true;
            SearchBox.Text = "";
            SeriesBox.SelectedIndex = 0;
            YearBox.SelectedIndex = 0;
            ReadyOnlyBox.IsChecked = false;
            _updatingUi = false;
            _view.Refresh();
            UpdateCount();
        }
        RaceList.SelectedItem = item;
        RaceList.ScrollIntoView(item);
        if (driver != null && _current == item)
        {
            _preferredDriver = driver;
            BuildSetup();
        }
    }

    // ================================================================== track choice

    private void ChooseTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _current?.Roster.Track is not { } track) return;
        var roster = _current.Roster;
        var ai = _settings.Ai.Configured ? _settings.Ai.CreateAssistant() : null;
        var w = new TrackPickerWindow(_ws.Library, track.Name, track.Locality,
            ai == null ? null : (candidates, ct) => ai.PickTrackAsync(track, roster, candidates, ct)) { Owner = this };
        if (w.ShowDialog() != true || w.Chosen is not { } t) return;
        TrackChoices.Set(_ws, track, t.Folder, t.Layout);
        Log($"{track.Name}: using {t.Folder}{(t.Layout != null ? "/" + t.Layout : "")} for every race at this circuit.");
        _ = ReloadCatalogAsync();
    }

    private void ResetTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _current?.Roster.Track is not { } track) return;
        if (TrackChoices.Clear(_ws, track)) Log($"{track.Name}: back to the automatic track match.");
        _ = ReloadCatalogAsync();
    }

    private async Task ReloadCatalogAsync()
    {
        if (_resolver == null) return;
        _catalog = await Task.Run(() => _resolver.LoadCatalog());
        if (_current != null) ShowRace(_current);
        await ComputeReadinessAsync();
    }

    // ================================================================== missing content

    private void OpenMissing_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is MissingRow row) Process.Start(new ProcessStartInfo(row.Item.Url) { UseShellExecute = true });
    }

    /// <summary>"I have it": map a missing real car to a car folder you have installed.</summary>
    private void PickCar_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is MissingRow { Car: { } car }) PickCar(car);
    }

    /// <summary>Right-click on the grid: change (or undo) which installed car is used for that real car.</summary>
    private void GridList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (GridList.SelectedItem is not GridRow { Slot.IsSafetyCar: not true } row) { e.Handled = true; return; }
        var real = row.Car.Entry.Car ?? row.Car.Entry.Team ?? row.Car.Entry.Driver;
        var item = new MenuItem { Header = $"Choose my installed car for the {real}…" };
        item.Click += (_, _) => PickCar(row.Car);
        GridList.ContextMenu = new ContextMenu { Items = { item } };
    }

    private void PickCar(ResolvedCar car)
    {
        if (_ws == null || _current == null) return;
        var roster = _current.Roster;
        var entry = car.Entry;
        var realCar = entry.Car ?? entry.Team ?? entry.Driver;
        var scope = $"in every {roster.Year} {roster.Category} race".Replace("  ", " ").Replace("every  ", "every ");
        // A stand-in row carries the real car it stands in for.
        if (_setup?.Grid.FirstOrDefault(s => ReferenceEquals(s.Car, car))?.RealCar is { } realOf) { car = realOf; entry = car.Entry; realCar = entry.Car ?? entry.Team ?? entry.Driver; }
        var current = CarChoices.Get(_ws, roster, entry);
        var ai = _settings.Ai.Configured ? _settings.Ai.CreateAssistant() : null;
        var aiEntry = entry;
        var aiReal = realCar;
        var w = new CarPickerWindow(_ws.Library, _ws.ContentFolder, realCar, scope, current,
            ai == null ? null : (candidates, ct) => ai.PickCarAsync(aiReal, roster, aiEntry, candidates, ct),
            $"{realCar} {entry.Team} {roster.Category}") { Owner = this };
        if (w.ShowDialog() != true) return;
        if (w.ClearChoice)
        {
            if (CarChoices.Clear(_ws, roster, entry)) Log($"{realCar}: back to the automatic match.");
        }
        else if (w.Chosen is { } chosen)
        {
            CarChoices.Set(_ws, roster, entry, chosen.Folder);
            Log($"{realCar}: using your installed {chosen.Folder} {scope}.");
        }
        _ = ReloadCatalogAsync();
    }

    /// <summary>A menu of the other sites to search for a missing car or track.</summary>
    private void OtherSources_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MissingRow row } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var source in row.Item.OtherSources)
        {
            var item = new MenuItem { Header = source.Name, ToolTip = source.Url };
            item.Click += (_, _) => Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void GetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_content == null || _ws == null) return;
        var items = Workspace.MissingItems(_content);
        var ws = _ws;
        await RunAsync("Getting missing content", (log, ct) => ContentGetter.GetAsync(items, ws.ContentFolder, new GetOptions(), log, ct));
        await RescanIfChangedAsync();
        if (_current != null) ShowRace(_current);
    }

    // ================================================================== toolbar jobs

    private async void UpdateDb_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null) return;
        var ws = _ws;
        var ai = _settings.Ai.Configured ? _settings.Ai : null;
        await RunAsync("Updating the race database", (log, ct) =>
            DatabaseBuilder.BuildAsync(ws.DatabasePath, new DbBuildOptions { NascarSeries = new[] { 1, 2, 3 }, Ai = ai }, log, ct));
        await LoadRacesAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null) return;
        var ws = _ws;
        await RunAsync("Matching your cars and tracks", (log, _) => Task.Run(() => LibraryScanner.Scan(ws, log)));
        await LoadRacesAsync();
    }

    private async void Champ_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null) return;
        var dialog = new OpenFolderDialog { Title = "Folder with Content Manager championships (.champ)", InitialDirectory = _ws.ChampsFolder };
        if (dialog.ShowDialog(this) != true) return;
        var ws = _ws;
        var folder = dialog.FolderName;
        await RunAsync("Importing championships", (log, _) => Task.Run(() => ChampionshipCatalog.Import(ws, folder, log)));
        await LoadRacesAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (PickAcFolder()) await OpenWorkspaceAsync();
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        var guide = Path.Combine(AppContext.BaseDirectory, "USER_GUIDE.md");
        if (File.Exists(guide)) Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
    }

    private bool PickAcFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select your assettocorsa folder" };
        if (dialog.ShowDialog(this) != true) return false;
        var folder = dialog.FolderName;
        var content = Directory.Exists(Path.Combine(folder, "content", "cars")) ? Path.Combine(folder, "content")
            : Directory.Exists(Path.Combine(folder, "cars")) ? folder : null;
        if (content == null)
        {
            MessageBox.Show("That folder doesn't contain Assetto Corsa's content\\cars.", "Historic Roster");
            return false;
        }
        _settings.ContentFolder = content;
        _settings.Save();
        return true;
    }

    // ================================================================== activity

    private async Task RunAsync(string title, Func<IProgress<string>, CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        UpdateDbButton.IsEnabled = ScanButton.IsEnabled = ChampButton.IsEnabled = GetAllButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        LogToggle.IsChecked = true;
        Log($"--- {title}");
        SetStatus(title + " ...");
        var progress = new Progress<string>(m => { Log(m); SetStatus(m); });
        try
        {
            await work(progress, _cts.Token);
            SetStatus(title + ": done.");
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
            SetStatus(title + ": cancelled.");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            SetStatus(title + ": failed - " + ex.Message);
        }
        finally
        {
            UpdateDbButton.IsEnabled = ScanButton.IsEnabled = ChampButton.IsEnabled = GetAllButton.IsEnabled = true;
            Busy.Visibility = Visibility.Hidden;
            CancelButton.Visibility = Visibility.Collapsed;
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void LogToggle_Changed(object sender, RoutedEventArgs e)
        => LogBox.Visibility = LogToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Log(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
