using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HistoricRosterPlugin;
using HistoricRosterPlugin.Ai;

namespace HistoricRosterApp;

/// <summary>Small dialogs built in code: shared helpers.</summary>
internal static class Dlg
{
    public static TextBlock Text(string text, double size = 13, bool bold = false, Brush? color = null) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = color ?? Ui.Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
    };

    public static Button Button(string text, RoutedEventHandler? click, bool primary = false, bool isCancel = false)
    {
        var b = new Button { Content = text, IsCancel = isCancel, Margin = new Thickness(8, 0, 0, 0) };
        if (primary) b.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        if (click != null) b.Click += click;
        return b;
    }

    public static Border Card(UIElement child)
    {
        var b = new Border { Child = child };
        b.SetResourceReference(FrameworkElement.StyleProperty, "CardBorder");
        return b;
    }

    /// <summary>
    /// "Suggest with AI": asks the AI which list item is the real thing, selects it and says why.
    /// </summary>
    public static Button AiSuggestButton<T>(ListBox list, TextBox search, IReadOnlyList<T> all, Func<T, AiCandidate> toCandidate,
        string description, Func<IReadOnlyList<AiCandidate>, CancellationToken, Task<AiPick>> suggest, TextBlock note)
    {
        var b = Button("Suggest with AI", null!);
        b.Margin = new Thickness(0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.ToolTip = "Ask the AI (OpenAI / DeepSeek) which of your installed items is the real one";
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            note.Text = "Asking the AI ...";
            note.Foreground = Ui.Muted;
            try
            {
                var byId = all.ToDictionary(x => toCandidate(x).Id, StringComparer.OrdinalIgnoreCase);
                var shortlist = AiAssistant.Shortlist(description, byId.Values.Select(toCandidate));
                var pick = await suggest(shortlist, default);
                if (pick.Id != null && byId.TryGetValue(pick.Id, out var item))
                {
                    search.Text = "";
                    list.SelectedItem = item;
                    list.ScrollIntoView(item);
                    note.Text = $"AI suggests {pick.Id}{(pick.Confidence != null ? $" ({pick.Confidence} confidence)" : "")}: {pick.Reason} Check it, then confirm.";
                    note.Foreground = pick.Confidence == "low" ? Ui.Warn : Ui.Ok;
                }
                else
                {
                    note.Text = "The AI found none of your installed items to be the real one. " + pick.Reason;
                    note.Foreground = Ui.Warn;
                }
            }
            catch (Exception ex) { note.Text = "The AI couldn't answer: " + ex.Message; note.Foreground = Ui.Bad; }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static GridViewColumn Col(string header, string path, double width)
        => new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };
}

/// <summary>Pick an installed track (and layout) for a real circuit.</summary>
public class TrackPickerWindow : Window
{
    public record Choice(string Folder, string? Layout, string Text);
    public Choice? Chosen { get; private set; }

    public TrackPickerWindow(ContentLibrary library, string circuit, string? locality = null,
        Func<IReadOnlyList<AiCandidate>, CancellationToken, Task<AiPick>>? aiSuggest = null)
    {
        Title = $"Choose the track for {circuit}"; Width = 620; Height = 600; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");

        var all = library.Tracks
            .Select(t => new Choice(t.Folder, t.Layout, $"{t.Name}   ·   {t.Folder}{(t.Layout != null ? " / " + t.Layout : "")}{(t.City != null ? "   ·   " + t.City : "")}"))
            .OrderBy(c => c.Text, StringComparer.CurrentCultureIgnoreCase).ToList();

        var search = new TextBox { Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListBox { DisplayMemberPath = nameof(Choice.Text) };
        void Filter()
        {
            var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.ItemsSource = all.Where(c => words.All(x => TextFold.Contains(c.Text, x))).ToList();
        }
        // Start with the first word of the circuit or its town that finds an installed track.
        search.Text = ContentLibrary.Tokens($"{circuit} {locality}").Append("")
            .First(w => w.Length == 0 || all.Any(c => TextFold.Contains(c.Text, w)));
        Filter();
        search.TextChanged += (_, _) => Filter();

        void Ok()
        {
            if (list.SelectedItem is not Choice c) return;
            Chosen = c;
            DialogResult = true;
        }
        list.MouseDoubleClick += (_, _) => Ok();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Dlg.Button("Cancel", (_, _) => DialogResult = false, isCancel: true));
        buttons.Children.Add(Dlg.Button("Use this track", (_, _) => Ok(), primary: true));

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new StackPanel();
        head.Children.Add(Dlg.Text($"The track you pick is used for every race at {circuit}, in every series. You can switch back to the automatic match later.", 12));
        if (aiSuggest != null)
        {
            var note = Dlg.Text("", 12);
            head.Children.Add(Dlg.AiSuggestButton(list, search, all, c => new AiCandidate(c.Folder + (c.Layout != null ? "/" + c.Layout : ""), c.Text),
                $"{circuit} {locality}", aiSuggest, note));
            note.Margin = new Thickness(0, 6, 0, 6);
            head.Children.Add(note);
        }
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(search);
        root.Children.Add(buttons);
        root.Children.Add(list);
        Content = root;
        Loaded += (_, _) => search.Focus();
    }
}

/// <summary>
/// "I have this car": pick an installed car (any folder in content\cars) for a real car the automatic matching
/// didn't find. Lists every installed car, so a mod with missing or odd info in its ui_car.json can still be chosen.
/// </summary>
public class CarPickerWindow : Window
{
    public record Choice(string Folder, string Text);
    public Choice? Chosen { get; private set; }
    public bool ClearChoice { get; private set; }

    public CarPickerWindow(ContentLibrary library, string contentFolder, string realCar, string scope, string? current,
        Func<IReadOnlyList<AiCandidate>, CancellationToken, Task<AiPick>>? aiSuggest = null, string? aiDescription = null)
    {
        Title = $"Choose your installed car for the {realCar}"; Width = 700; Height = 640; MinWidth = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");

        // Every folder in content\cars, also ones the scan couldn't read (no or broken ui_car.json).
        var known = library.Cars.ToDictionary(c => c.Folder, StringComparer.OrdinalIgnoreCase);
        var carsDir = System.IO.Path.Combine(contentFolder, "cars");
        var folders = System.IO.Directory.Exists(carsDir)
            ? System.IO.Directory.GetDirectories(carsDir).Select(System.IO.Path.GetFileName).Cast<string>()
            : known.Keys;
        var all = folders.Select(f => known.TryGetValue(f, out var c)
                ? new Choice(f, $"{c.Name}   ·   {f}{(c.Year != null ? "   ·   " + c.Year : "")}{(c.Category != null ? "   ·   " + c.Category : "")}")
                : new Choice(f, $"{f}   ·   (no car info)"))
            .OrderBy(c => c.Text, StringComparer.CurrentCultureIgnoreCase).ToList();

        var search = new TextBox { Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 8) };
        var list = new ListBox { DisplayMemberPath = nameof(Choice.Text) };
        void Filter()
        {
            var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.ItemsSource = all.Where(c => words.All(x => TextFold.Contains(c.Text, x))).ToList();
        }
        // Start with the first word of the real car's name that finds an installed car (skipping the year).
        search.Text = ContentLibrary.Tokens(realCar).Where(w => !(w.Length == 4 && w.All(char.IsDigit))).Append("")
            .First(w => w.Length == 0 || all.Any(c => TextFold.Contains(c.Text, w)));
        Filter();
        search.TextChanged += (_, _) => Filter();
        if (current != null && all.FirstOrDefault(c => c.Folder.Equals(current, StringComparison.OrdinalIgnoreCase)) is { } cur)
        {
            search.Text = "";
            list.SelectedItem = cur;
            list.ScrollIntoView(cur);
        }

        void Ok()
        {
            if (list.SelectedItem is not Choice c) return;
            Chosen = c;
            DialogResult = true;
        }
        list.MouseDoubleClick += (_, _) => Ok();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (current != null)
            buttons.Children.Add(Dlg.Button("Back to automatic match", (_, _) => { ClearChoice = true; DialogResult = true; }));
        buttons.Children.Add(Dlg.Button("Cancel", (_, _) => DialogResult = false, isCancel: true));
        buttons.Children.Add(Dlg.Button("Use this car", (_, _) => Ok(), primary: true));

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new StackPanel();
        head.Children.Add(Dlg.Text($"Pick the car you have installed for the {realCar}. It is used for this car {scope}, " +
                                   "with the livery that best matches each driver.", 12));
        if (current != null) head.Children.Add(Dlg.Text($"Your current choice: {current}", 12, bold: true));
        if (aiSuggest != null)
        {
            var note = Dlg.Text("", 12);
            head.Children.Add(Dlg.AiSuggestButton(list, search, all, c => new AiCandidate(c.Folder, c.Text), aiDescription ?? realCar, aiSuggest, note));
            note.Margin = new Thickness(0, 6, 0, 6);
            head.Children.Add(note);
        }
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(search);
        root.Children.Add(buttons);
        root.Children.Add(list);
        Content = root;
        Loaded += (_, _) => search.Focus();
    }
}

/// <summary>Your race next to the real one.</summary>
public class ResultWindow : Window
{
    private record Row(string Pos, string Driver, string Team, string Real, string Grid, string Delta, bool Me, string Class = "", string ClassPos = "", string RealClass = "");

    public ResultWindow(RaceComparison cmp, string? seasonNote, Action<Window>? aiReport = null)
    {
        Title = "Race result: " + cmp.Race.RosterName; Width = 720; Height = 640; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");

        var rows = cmp.Rows.Select(r => new Row(
            r.YourRacePosition is { } p ? $"P{p}" : "-",
            r.IsPlayer ? Ui.AsYou(r.Driver, cmp.Race.PlayerName) : r.Driver,
            r.Team ?? "",
            r.RealPosition is { } rp ? $"P{rp}" : "DNF",
            r.RealGrid is { } g ? $"P{g}" : "",
            r.Delta switch { null => "", > 0 => $"+{r.Delta}", 0 => "=", _ => r.Delta.ToString()! },
            r.IsPlayer,
            r.Class ?? "",
            r.YourClassPosition is { } cp ? $"P{cp}" : r.Class != null ? "-" : "",
            r.RealClassPosition is { } rcp ? $"P{rcp}" : r.Class != null ? "DNF" : "")).ToList();

        var view = new GridView();
        view.Columns.Add(Dlg.Col("Your race", nameof(Row.Pos), 70));
        if (cmp.IsMultiClass)
        {
            // Multi-class race: class positions, and +/- counts within the class.
            Width = 900;
            view.Columns.Add(Dlg.Col("Class", nameof(Row.Class), 64));
            view.Columns.Add(Dlg.Col("In class", nameof(Row.ClassPos), 64));
        }
        view.Columns.Add(Dlg.Col("Driver", nameof(Row.Driver), 200));
        view.Columns.Add(Dlg.Col("Team", nameof(Row.Team), 170));
        view.Columns.Add(Dlg.Col("Real result", nameof(Row.Real), 80));
        if (cmp.IsMultiClass) view.Columns.Add(Dlg.Col("Real in class", nameof(Row.RealClass), 84));
        view.Columns.Add(Dlg.Col("Real grid", nameof(Row.Grid), 70));
        view.Columns.Add(Dlg.Col("+/-", nameof(Row.Delta), 50));
        var list = new ListView { View = view, ItemsSource = rows };
        var style = new Style(typeof(ListViewItem));
        var trigger = new DataTrigger { Binding = new Binding(nameof(Row.Me)), Value = true };
        trigger.Setters.Add(new Setter(BackgroundProperty, Ui.PlayerRow));
        trigger.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(trigger);
        list.ItemContainerStyle = style;

        var head = new StackPanel();
        head.Children.Add(Dlg.Text(cmp.Race.RosterName, 20, bold: true));
        var verdictColor = cmp.Player?.Delta is > 0 ? Ui.Ok : cmp.Player?.Delta is < 0 ? Ui.Bad : null;
        head.Children.Add(Dlg.Text(cmp.Headline, 15, bold: true, color: verdictColor));
        if (seasonNote != null) head.Children.Add(Dlg.Text(seasonNote, 13, color: Ui.Warn));
        head.Children.Add(Dlg.Text("+/- is places gained or lost against the driver's real result.", 11, color: Ui.Muted));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (aiReport != null)
            buttons.Children.Add(Dlg.Button("AI race report", (_, _) => aiReport(this)));
        buttons.Children.Add(Dlg.Button("Close", (_, _) => Close(), primary: true, isCancel: true));

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(list);
        Content = root;
    }
}

/// <summary>Drive a real season: pick it, pick your seat and the round you join, follow the standings.</summary>
public class SeasonWindow : Window
{
    private record SeasonItem(string Key, string Name, int Rounds) { public override string ToString() => $"{Name}  ({Rounds} rounds)"; }
    private record RoundRow(string No, string Race, string Status, string Winner, string You, string RosterId);
    private record StandRow(string Pos, string Driver, string Team, string Points, string Wins, bool Me);

    private readonly List<Roster> _all;
    private readonly ComboBox _seasonBox = new() { MinWidth = 320 };
    private readonly ComboBox _driverBox = new() { MinWidth = 220, IsEditable = false };
    private readonly ComboBox _joinBox = new() { MinWidth = 260 };
    private readonly ListView _rounds = new();
    private readonly ListView _standings = new();
    private readonly TextBlock _status = Dlg.Text("", 13, bold: true);
    private readonly Button _startButton;
    private readonly Button _nextButton;
    private readonly Button _quitButton;
    private SeasonState? _state;
    private List<Roster> _seasonRounds = new();

    public string? RaceToSetUp { get; private set; }
    public string? DriverToSetUp { get; private set; }

    public SeasonWindow(List<Roster> all, Roster? current, string? currentDriver)
    {
        _all = all;
        Title = "Season"; Width = 1100; Height = 760; MinWidth = 800; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");

        _startButton = Dlg.Button("Start this season", (_, _) => Start(), primary: true);
        _nextButton = Dlg.Button("Set up next round", (_, _) => SetUpNext(), primary: true);
        _quitButton = Dlg.Button("Abandon season", (_, _) => Quit());

        var seasons = Seasons.List(all).Select(s => new SeasonItem(s.Key, s.Name, s.Rounds)).ToList();
        _seasonBox.ItemsSource = seasons;
        var startKey = SeasonState.ActiveKey ?? (current != null ? Seasons.KeyOf(current) : null);
        _seasonBox.SelectedItem = seasons.FirstOrDefault(s => s.Key == startKey) ?? seasons.FirstOrDefault();
        _seasonBox.SelectionChanged += (_, _) => LoadSeason(null, null);

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Add(string label, FrameworkElement e)
        {
            top.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Ui.Muted });
            e.Margin = new Thickness(0, 0, 16, 6);
            top.Children.Add(e);
        }
        Add("Season", _seasonBox);
        Add("Your seat", _driverBox);
        Add("Join at", _joinBox);

        var rv = new GridView();
        rv.Columns.Add(Dlg.Col("Rd", nameof(RoundRow.No), 36));
        rv.Columns.Add(Dlg.Col("Race", nameof(RoundRow.Race), 250));
        rv.Columns.Add(Dlg.Col("", nameof(RoundRow.Status), 90));
        rv.Columns.Add(Dlg.Col("Winner", nameof(RoundRow.Winner), 140));
        rv.Columns.Add(Dlg.Col("You", nameof(RoundRow.You), 50));
        _rounds.View = rv;

        var sv = new GridView();
        sv.Columns.Add(Dlg.Col("Pos", nameof(StandRow.Pos), 40));
        sv.Columns.Add(Dlg.Col("Driver", nameof(StandRow.Driver), 170));
        sv.Columns.Add(Dlg.Col("Team", nameof(StandRow.Team), 130));
        sv.Columns.Add(Dlg.Col("Pts", nameof(StandRow.Points), 50));
        sv.Columns.Add(Dlg.Col("Wins", nameof(StandRow.Wins), 44));
        _standings.View = sv;
        var style = new Style(typeof(ListViewItem));
        var trigger = new DataTrigger { Binding = new Binding(nameof(StandRow.Me)), Value = true };
        trigger.Setters.Add(new Setter(BackgroundProperty, Ui.PlayerRow));
        trigger.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(trigger);
        _standings.ItemContainerStyle = style;

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var lt = Dlg.Text("ROUNDS", 11, bold: true, color: Ui.Muted); DockPanel.SetDock(lt, Dock.Top);
        left.Children.Add(lt); left.Children.Add(_rounds);
        var right = new DockPanel();
        var rt = Dlg.Text("STANDINGS", 11, bold: true, color: Ui.Muted); DockPanel.SetDock(rt, Dock.Top);
        right.Children.Add(rt); right.Children.Add(_standings);
        Grid.SetColumn(right, 2);
        body.Children.Add(left); body.Children.Add(right);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_quitButton);
        buttons.Children.Add(Dlg.Button("Close", (_, _) => DialogResult = false, isCancel: true));
        buttons.Children.Add(_startButton);
        buttons.Children.Add(_nextButton);

        var head = new StackPanel();
        head.Children.Add(top);
        head.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        _driverBox.SelectionChanged += (_, _) => { if (_state == null) Preview(); };
        _joinBox.SelectionChanged += (_, _) => { if (_state == null) Preview(); };
        LoadSeason(currentDriver, current?.Id);
    }

    private string? Key => (_seasonBox.SelectedItem as SeasonItem)?.Key;

    private void LoadSeason(string? preferDriver, string? preferRound)
    {
        if (Key is not { } key) return;
        _seasonRounds = Seasons.Rounds(_all, key);
        _state = SeasonState.Load(key);

        var drivers = _seasonRounds.SelectMany(r => r.Entries)
            .GroupBy(e => e.Driver, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Driver: g.Key, Team: g.Last().Team, Races: g.Count()))
            .OrderByDescending(d => d.Races).ThenBy(d => d.Driver).ToList();
        _driverBox.ItemsSource = drivers.Select(d => $"{d.Driver}{(d.Team != null ? "  -  " + d.Team : "")}").ToList();
        var want = _state?.Driver ?? preferDriver;
        var idx = want == null ? 0 : drivers.FindIndex(d => d.Driver.Equals(want, StringComparison.OrdinalIgnoreCase));
        _driverBox.SelectedIndex = Math.Max(0, idx);
        _driverBox.Tag = drivers.Select(d => d.Driver).ToList();

        _joinBox.ItemsSource = _seasonRounds.Select((r, i) => $"Round {i + 1}: {r.Name}").ToList();
        var join = _state != null ? _seasonRounds.FindIndex(r => r.Id == _state.JoinedRosterId)
            : preferRound != null ? _seasonRounds.FindIndex(r => r.Id == preferRound) : 0;
        _joinBox.SelectedIndex = Math.Max(0, join);

        _driverBox.IsEnabled = _joinBox.IsEnabled = _state == null;
        Refresh();
    }

    private string? SelectedDriver => (_driverBox.Tag as List<string>) is { } d && _driverBox.SelectedIndex >= 0 && _driverBox.SelectedIndex < d.Count
        ? d[_driverBox.SelectedIndex] : null;

    /// <summary>Standings for the season as it would start with the chosen seat and round (not saved).</summary>
    private void Preview() => Refresh();

    private void Refresh()
    {
        var s = _state ?? (SelectedDriver is { } d && _joinBox.SelectedIndex >= 0 && _seasonRounds.Count > 0
            ? new SeasonState { Key = Key!, Driver = d, JoinedRosterId = _seasonRounds[_joinBox.SelectedIndex].Id }
            : null);
        if (s == null) return;

        var states = Seasons.RoundStates(_seasonRounds, s);
        _rounds.ItemsSource = states.Select(r => new RoundRow(r.Number.ToString(), r.Roster.Name,
            r.State switch { RoundState.Real => "real result", RoundState.Raced => "raced", RoundState.Next => "NEXT", _ => "" },
            r.Winner ?? "", r.YourPosition is { } p ? $"P{p}" : "", r.Roster.Id)).ToList();
        _standings.ItemsSource = Seasons.Standings(_seasonRounds, s)
            .Select(x => new StandRow(x.Position.ToString(), x.IsPlayer ? Ui.AsYou(x.Driver) : x.Driver, x.Team ?? "", x.Points.ToString("0.#"), x.Wins > 0 ? x.Wins.ToString() : "", x.IsPlayer)).ToList();

        var next = states.FirstOrDefault(r => r.State == RoundState.Next);
        var pts = Seasons.PointsLabel(_seasonRounds.FirstOrDefault()?.Category, _seasonRounds.FirstOrDefault()?.Year);
        _status.Text = _state == null
            ? $"Not started. Choose your seat and the round to join; earlier rounds count with their real results. Points: {pts}."
            : next == null
                ? $"Season finished in {s.Driver}'s seat. Points: {pts}."
                : $"Your season in {s.Driver}'s seat. Next: round {next.Number}, {next.Roster.Name}. Points: {pts}.";
        _startButton.Visibility = _state == null ? Visibility.Visible : Visibility.Collapsed;
        _nextButton.Visibility = _state != null && next != null ? Visibility.Visible : Visibility.Collapsed;
        _quitButton.Visibility = _state != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Start()
    {
        if (Key is not { } key || SelectedDriver is not { } driver || _joinBox.SelectedIndex < 0) return;
        var join = _seasonRounds[_joinBox.SelectedIndex];
        _state = new SeasonState
        {
            Key = key,
            Name = (_seasonBox.SelectedItem as SeasonItem)?.Name ?? key,
            Driver = driver,
            Team = join.Entries.FirstOrDefault(e => e.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase))?.Team,
            JoinedRosterId = join.Id,
        };
        _state.Save();
        SeasonState.ActiveKey = key;
        _driverBox.IsEnabled = _joinBox.IsEnabled = false;
        Refresh();
    }

    private void SetUpNext()
    {
        if (_state == null) return;
        var next = Seasons.RoundStates(_seasonRounds, _state).FirstOrDefault(r => r.State == RoundState.Next);
        if (next == null) return;
        SeasonState.ActiveKey = _state.Key;
        RaceToSetUp = next.Roster.Id;
        DriverToSetUp = _state.Driver;
        DialogResult = true;
    }

    private void Quit()
    {
        if (_state == null) return;
        if (MessageBox.Show($"Abandon your {_state.Name} season? Your results in it are deleted.", "Season",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _state.Delete();
        if (SeasonState.ActiveKey == _state.Key) SeasonState.ActiveKey = null;
        LoadSeason(null, null);
    }
}
