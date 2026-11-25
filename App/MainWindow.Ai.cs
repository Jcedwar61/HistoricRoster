using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using HistoricRosterPlugin;
using HistoricRosterPlugin.Ai;

namespace HistoricRosterApp;

/// <summary>The main window's AI features (OpenAI / DeepSeek).</summary>
public partial class MainWindow
{
    /// <summary>Year range from an AI search (the year box holds one year only).</summary>
    private int? _aiYearFrom, _aiYearTo;

    /// <summary>The AI assistant, or null (after offering the settings) when no key is set up.</summary>
    private AiAssistant? Ai()
    {
        if (_settings.Ai.Configured) return _settings.Ai.CreateAssistant();
        if (MessageBox.Show("This needs an OpenAI or DeepSeek API key. Open AI settings to add one?", "Historic Roster",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            AiSettings_Click(this, new RoutedEventArgs());
        return _settings.Ai.Configured ? _settings.Ai.CreateAssistant() : null;
    }

    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        if (new AiSettingsWindow(_settings.Ai) { Owner = this }.ShowDialog() != true) return;
        _settings.Save();
        Log(_settings.Ai.Configured
            ? "AI set up: " + string.Join(", then ", _settings.Ai.CreateClient().Endpoints.Select(x => $"{x.Name} ({x.Model})")) + "."
            : "AI: no API key set; the AI features are off.");
    }

    // ------------------------------------------------------------------ Ask AI

    /// <summary>What is on screen, for the Ask AI window.</summary>
    private string ChatContext(bool withLog)
    {
        var s = new StringBuilder();
        s.AppendLine(InstallText.Text);
        if (_current != null)
        {
            s.AppendLine("Race open in the program:");
            s.Append(AiAssistant.RaceFacts(_current.Roster, 45));
            if (_content?.Track is { } t)
                s.AppendLine($"AC track matched: {t.Folder ?? "none"}{(t.Layout != null ? "/" + t.Layout : "")}, installed: {t.Installed}");
            if (_content != null)
            {
                var missing = Workspace.MissingItems(_content);
                if (missing.Count > 0) s.AppendLine("Missing on this PC: " + string.Join("; ", missing.Select(m => $"{m.Kind} {m.Label}")));
            }
            s.AppendLine("Setup: " + ActionText.Text);
            if (CondText.Text.Length > 0) s.AppendLine("Conditions: " + CondText.Text);
        }
        if (withLog)
        {
            s.AppendLine("Activity log (latest lines):");
            foreach (var l in LogBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(80)) s.AppendLine(l.TrimEnd());
            try
            {
                if (File.Exists(App.LogPath))
                {
                    var err = File.ReadAllLines(App.LogPath).TakeLast(40);
                    s.AppendLine("error.log (latest lines):");
                    foreach (var l in err) s.AppendLine(l);
                }
            }
            catch { /* the log is optional context */ }
        }
        return s.ToString();
    }

    private static string? Guide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "USER_GUIDE.md");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    }

    private void OpenChat(bool withLog, string? firstQuestion)
    {
        if (Ai() is not { } ai) return;
        new AiChatWindow(ai, AiAssistant.HelpSystemPrompt(Guide(), ChatContext(withLog)), firstQuestion) { Owner = this }.Show();
    }

    private void AskAi_Click(object sender, RoutedEventArgs e) => OpenChat(false, null);

    private void AskAiLog_Click(object sender, RoutedEventArgs e)
        => OpenChat(true, "Explain what the activity log shows, especially any errors or warnings, and tell me what to do about them.");

    // ------------------------------------------------------------------ preview and report

    private void AiPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || Ai() is not { } ai) return;
        var roster = _current.Roster;
        var seat = _setup?.Watch == true ? null : _setup?.Player.Driver;
        new AiTextWindow("Preview: " + roster.Name, ct => ai.RacePreviewAsync(roster, seat, ct), ai.Client) { Owner = this }.Show();
    }

    private Action<Window>? AiReportAction(RaceComparison cmp, Roster roster) => _settings.Ai.Configured
        ? owner =>
        {
            var ai = _settings.Ai.CreateAssistant();
            new AiTextWindow("Race report: " + roster.Name, ct => ai.RaceReportAsync(roster, cmp, ct), ai.Client) { Owner = owner }.Show();
        }
        : null;

    // ------------------------------------------------------------------ search

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) AiSearch_Click(sender, e);
    }

    private async void AiSearch_Click(object sender, RoutedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            MessageBox.Show("Type what you're looking for in the search box first, in plain words: \"Senna's wins at Monaco\", " +
                            "\"night races\", \"Le Mans in the Audi years\" ...", "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Ai() is not { } ai) return;
        AiSearchButton.IsEnabled = false;
        SetStatus("Asking the AI what to search for ...");
        try
        {
            var categories = _races.Select(r => r.Category).Where(c => c != "").Distinct().Order().ToList();
            var r = await ai.SearchAsync(q, categories);
            _updatingUi = true;
            SearchBox.Text = string.Join(" ", r.Words);
            SeriesBox.SelectedItem = r.Category != null && categories.Contains(r.Category) ? r.Category : SeriesBox.Items[0];
            YearBox.SelectedIndex = 0;
            _aiYearFrom = r.YearFrom;
            _aiYearTo = r.YearTo;
            _updatingUi = false;
            var years = r.YearFrom == null && r.YearTo == null ? ""
                : r.YearFrom == r.YearTo ? $" {r.YearFrom}."
                : $" {r.YearFrom?.ToString() ?? "…"}-{r.YearTo?.ToString() ?? "…"}.";
            AiSearchText.Text = $"AI search \"{q}\":{years} {r.Explanation}".Trim();
            AiSearchPanel.Visibility = Visibility.Visible;
            _view?.Refresh();
            UpdateCount();
            SetStatus($"AI search done ({ai.Client.LastUsed}).");
            if (_view != null && !_view.Cast<object>().Any() && ReadyOnlyBox.IsChecked == true)
                AiSearchText.Text += " None of the matching races can run with your content: untick \"Only races I can run\" to see them.";
        }
        catch (Exception ex)
        {
            _updatingUi = false;
            SetStatus("AI search failed: " + ex.Message);
            Log("AI search failed: " + ex.Message);
        }
        finally { AiSearchButton.IsEnabled = true; }
    }

    private void AiSearchClear_Click(object sender, RoutedEventArgs e)
    {
        _aiYearFrom = _aiYearTo = null;
        AiSearchPanel.Visibility = Visibility.Collapsed;
        _updatingUi = true;
        SearchBox.Text = "";
        SeriesBox.SelectedIndex = 0;
        _updatingUi = false;
        _view?.Refresh();
        UpdateCount();
    }

    // ------------------------------------------------------------------ find installed content

    /// <summary>Asks the AI whether any installed car or track is one of the missing real ones, then lets you confirm.</summary>
    private async void AiMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _current == null || _content == null || _cts != null || Ai() is not { } ai) return;
        var ws = _ws;
        var roster = _current.Roster;
        var lib = ws.Library;
        var content = _content;
        var carCandidates = lib.Cars.Select(c => new AiCandidate(c.Folder,
            string.Join(" · ", new[] { c.Name, c.Brand, c.Year?.ToString(), c.Category }.Where(x => !string.IsNullOrWhiteSpace(x))))).ToList();
        var trackCandidates = lib.Tracks.Select(t => new AiCandidate(t.Folder + (t.Layout != null ? "/" + t.Layout : ""),
            string.Join(" · ", new[] { t.Name, t.City, t.Country }.Where(x => !string.IsNullOrWhiteSpace(x))))).ToList();
        // One question per distinct missing real car.
        var cars = content.Cars.Where(c => !c.Installed)
            .GroupBy(c => (c.Entry.Car ?? c.Entry.Team ?? c.Entry.Driver).ToLowerInvariant())
            .Select(g => g.First()).ToList();
        var proposals = new List<(string What, string Pick, string Reason, Action Apply)>();
        var unsure = new List<string>();

        await RunAsync("Finding your cars and tracks with AI", async (log, ct) =>
        {
            if (content.Track is { Installed: false } && roster.Track is { } track && trackCandidates.Count > 0)
            {
                log.Report($"Track: {track.Name} ...");
                var p = await ai.PickTrackAsync(track, roster, AiAssistant.Shortlist($"{track.Name} {track.Locality}", trackCandidates), ct);
                if (p.Id != null && p.Confidence != "low")
                {
                    var parts = p.Id.Split('/', 2);
                    proposals.Add(($"Track {track.Name}", p.Id, p.Reason, () => TrackChoices.Set(ws, track, parts[0], parts.Length > 1 ? parts[1] : null)));
                }
                else unsure.Add($"Track {track.Name}: {(p.Id != null ? $"maybe {p.Id}, not sure. " : "")}{p.Reason}");
            }
            foreach (var car in cars)
            {
                ct.ThrowIfCancellationRequested();
                if (carCandidates.Count == 0) break;
                var real = car.Entry.Car ?? car.Entry.Team ?? car.Entry.Driver;
                log.Report($"Car: {real} ...");
                var p = await ai.PickCarAsync(real, roster, car.Entry,
                    AiAssistant.Shortlist($"{real} {car.Entry.Team} {roster.Category}", carCandidates), ct);
                var entry = car.Entry;
                if (p.Id is { } id && p.Confidence != "low")
                    proposals.Add(($"Car {real}", id, p.Reason, () => CarChoices.Set(ws, roster, entry, id)));
                else unsure.Add($"Car {real}: {(p.Id != null ? $"maybe {p.Id}, not sure. " : "")}{p.Reason}");
            }
            log.Report($"AI ({ai.Client.LastUsed}): {proposals.Count} found among your installed content, {unsure.Count} not.");
        });

        foreach (var u in unsure) Log("  " + u);
        if (proposals.Count == 0)
        {
            MessageBox.Show("The AI didn't find any of the missing real cars or track among your installed content.\n\n" +
                            string.Join("\n", unsure.Take(12)), "Find mine with AI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var text = "The AI thinks you already have these. Use them?\n\n" +
                   string.Join("\n\n", proposals.Select(p => $"• {p.What}  →  {p.Pick}\n   {p.Reason}")) +
                   "\n\nCars are used for this series and season, a track for every race at that circuit. Undo a car by right-clicking it " +
                   "on the grid, a track with Use automatic match.";
        if (MessageBox.Show(text, "Find mine with AI", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var p in proposals)
        {
            p.Apply();
            Log($"{p.What}: using {p.Pick} (AI match).");
        }
        await ReloadCatalogAsync();
    }

    // ------------------------------------------------------------------ find content online

    /// <summary>Asks OpenAI (web search) and DeepSeek for the best mods for each missing car and track, links checked.</summary>
    private void AiFindOnline_Click(object sender, RoutedEventArgs e)
    {
        if (_ws == null || _current == null || _content == null || _resolver == null || Ai() == null) return;
        var roster = _current.Roster;
        var content = _content;
        var resolver = _resolver;
        var requests = new List<ModRequest>();
        if (content.Track is { Installed: false, Kunos: false } t && roster.Track is { } track)
            requests.Add(new ModRequest("track", track.Name, t.Folder == null ? null : t.Folder + (t.Layout != null ? "/" + t.Layout : ""),
                roster.Name, roster.Category, roster.Year, Locality: track.Locality));
        foreach (var car in content.Cars.Where(c => !c.Installed && !c.Kunos)
                     .GroupBy(c => (c.Entry.Car ?? c.Entry.Team ?? c.Entry.Driver).ToLowerInvariant()).Select(g => g.First()))
            requests.Add(new ModRequest("car", car.Entry.Car ?? car.Entry.Team ?? car.Entry.Driver, car.Model,
                roster.Name, roster.Category, roster.Year, Team: car.Entry.Team));
        if (requests.Count == 0)
        {
            MessageBox.Show("Nothing to search for: what's missing is official Kunos content (get it on Steam).", "Historic Roster");
            return;
        }
        var endpoints = new[] { _settings.Ai.OpenAi, _settings.Ai.DeepSeek }.Where(x => x.Usable).ToList();
        var services = string.Join(" and ", endpoints.Select(x => x.Provider == AiProvider.OpenAI ? $"OpenAI ({x.Model}, web search)" : $"DeepSeek ({x.Model})"));
        string Search(string name) => resolver.SearchSources(name)[0].Url;
        new ModOptionsWindow(requests, services,
            (r, ct) => ModScout.FindAsync(endpoints, r, Search, ct),
            (r, o) => _ = GetModAsync(r, o)) { Owner = this }.Show();
    }

    /// <summary>Gets one option from the AI search: direct archives are installed, pages open and Downloads is watched.</summary>
    private async Task GetModAsync(ModRequest r, ModOption o)
    {
        if (_ws == null) return;
        if (_cts != null)
        {
            MessageBox.Show("Another job is running; wait for it or stop it first.", "Historic Roster");
            return;
        }
        var ws = _ws;
        var parts = (r.Folder ?? o.Folder)?.Split('/', 2);
        var item = new WantedItem(r.Kind, parts?[0], parts?.Length > 1 ? parts[1] : null, r.RealName, o.Url);
        Log($"{r.RealName}: getting {o.Name} from {o.Url} (AI suggestion).");
        await RunAsync($"Getting {r.RealName}", (log, ct) => ContentGetter.GetAsync(new List<WantedItem> { item }, ws.ContentFolder, new GetOptions(), log, ct));
        await RescanIfChangedAsync();
        if (_current != null) ShowRace(_current);
    }
}
