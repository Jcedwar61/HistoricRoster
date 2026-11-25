using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HistoricRosterPlugin;
using HistoricRosterPlugin.Ai;

namespace HistoricRosterApp;

/// <summary>Enter the API keys, pick the models, test the connection.</summary>
public class AiSettingsWindow : Window
{
    private readonly AiSettings _s;
    private readonly PasswordBox _openAiKey = new() { Padding = new Thickness(4, 3, 4, 3) };
    private readonly ComboBox _openAiModel = new() { IsEditable = true };
    private readonly TextBox _openAiUrl = new() { Padding = new Thickness(4, 3, 4, 3) };
    private readonly PasswordBox _deepSeekKey = new() { Padding = new Thickness(4, 3, 4, 3) };
    private readonly ComboBox _deepSeekModel = new() { IsEditable = true };
    private readonly TextBox _deepSeekUrl = new() { Padding = new Thickness(4, 3, 4, 3) };
    private readonly ComboBox _preferred = new() { ItemsSource = new[] { "OpenAI", "DeepSeek" } };
    private readonly CheckBox _fallback = new() { Content = "If it fails or has no key, use the other service" };
    private readonly CheckBox _colours = new() { Content = "Generated liveries use each team's real colour (asks the AI)" };
    private readonly TextBlock _status = Dlg.Text("", 12);

    public AiSettingsWindow(AiSettings settings)
    {
        _s = settings;
        Title = "AI settings (OpenAI and DeepSeek)"; Width = 640; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Bg");

        _openAiKey.Password = settings.OpenAiKey;
        _openAiModel.ItemsSource = new[] { "gpt-5-mini", "gpt-5", "gpt-5-nano", "gpt-4.1-mini", "gpt-4o-mini" };
        _openAiModel.Text = settings.OpenAiModel;
        _openAiUrl.Text = settings.OpenAiUrl ?? "";
        _deepSeekKey.Password = settings.DeepSeekKey;
        _deepSeekModel.ItemsSource = new[] { "deepseek-chat", "deepseek-reasoner" };
        _deepSeekModel.Text = settings.DeepSeekModel;
        _deepSeekUrl.Text = settings.DeepSeekUrl ?? "";
        _preferred.SelectedItem = settings.Preferred == "DeepSeek" ? "DeepSeek" : "OpenAI";
        _fallback.IsChecked = settings.Fallback;
        _colours.IsChecked = settings.TeamColours;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Dlg.Text(
            "With an API key, Historic Roster can use AI to: write race previews and race reports, find which of your installed cars and " +
            "tracks are the real ones, search races in plain words, give generated liveries the real team colours, and answer questions " +
            "about the program in the Ask AI window. Only the data of the feature you use is sent (race, grid, your installed car/track names " +
            "or your question), and only when you use it. Keys are stored encrypted for your Windows user.", 12, color: Ui.Muted));

        root.Children.Add(Section("OpenAI", "platform.openai.com/api-keys", _openAiKey, _openAiModel, _openAiUrl, () => Endpoint(AiProvider.OpenAI)));
        root.Children.Add(Section("DeepSeek", "platform.deepseek.com/api_keys", _deepSeekKey, _deepSeekModel, _deepSeekUrl, () => Endpoint(AiProvider.DeepSeek)));

        var pref = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        pref.Children.Add(new TextBlock { Text = "Use first", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Ui.Muted });
        _preferred.Width = 120;
        pref.Children.Add(_preferred);
        root.Children.Add(pref);
        root.Children.Add(_fallback);
        _colours.Margin = new Thickness(0, 6, 0, 8);
        root.Children.Add(_colours);
        root.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Dlg.Button("Cancel", (_, _) => DialogResult = false, isCancel: true));
        buttons.Children.Add(Dlg.Button("Save", (_, _) => { Save(); DialogResult = true; }, primary: true));
        root.Children.Add(buttons);
        Content = root;
    }

    private AiEndpoint Endpoint(AiProvider p) => p == AiProvider.OpenAI
        ? new AiEndpoint(p, _openAiKey.Password, _openAiModel.Text, NullIfEmpty(_openAiUrl.Text))
        : new AiEndpoint(p, _deepSeekKey.Password, _deepSeekModel.Text, NullIfEmpty(_deepSeekUrl.Text));

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private UIElement Section(string name, string keyPage, PasswordBox key, ComboBox model, TextBox url, Func<AiEndpoint> endpoint)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        void Row(int r, string label, FrameworkElement e)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
            Grid.SetRow(t, r); g.Children.Add(t);
            e.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(e, r); Grid.SetColumn(e, 1); g.Children.Add(e);
        }
        Row(0, "API key", key);
        Row(1, "Model", model);
        url.ToolTip = "Leave empty for the official address. Any OpenAI-compatible address works (e.g. a proxy or Azure OpenAI).";
        Row(2, "Address", url);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var test = new Button { Content = "Test", Margin = new Thickness(0, 0, 6, 0) };
        test.SetResourceReference(StyleProperty, "SmallButton");
        test.Click += async (_, _) =>
        {
            var ep = endpoint();
            if (!ep.Usable) { _status.Text = $"{name}: enter a key and a model first."; _status.Foreground = Ui.Bad; return; }
            _status.Text = $"Testing {name} ..."; _status.Foreground = Ui.Muted;
            try
            {
                var reply = await AiClient.TestAsync(ep);
                _status.Text = $"{name} works ({ep.Model} replied: \"{reply.Trim()}\")."; _status.Foreground = Ui.Ok;
            }
            catch (Exception ex) { _status.Text = $"{name} test failed: {ex.Message}"; _status.Foreground = Ui.Bad; }
        };
        var models = new Button { Content = "List models", Margin = new Thickness(0, 0, 6, 0) };
        models.SetResourceReference(StyleProperty, "SmallButton");
        models.Click += async (_, _) =>
        {
            var ep = endpoint();
            if (string.IsNullOrWhiteSpace(ep.ApiKey)) { _status.Text = $"{name}: enter the key first."; _status.Foreground = Ui.Bad; return; }
            try
            {
                var text = model.Text;
                var list = await AiClient.ListModelsAsync(ep);
                model.ItemsSource = list;
                model.Text = text;
                _status.Text = $"{name}: {list.Count} models listed; pick one in the Model box."; _status.Foreground = Ui.Ok;
            }
            catch (Exception ex) { _status.Text = $"{name}: {ex.Message}"; _status.Foreground = Ui.Bad; }
        };
        var getKey = new Button { Content = "Get a key" };
        getKey.SetResourceReference(StyleProperty, "SmallButton");
        getKey.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://" + keyPage) { UseShellExecute = true });
        actions.Children.Add(test);
        actions.Children.Add(models);
        actions.Children.Add(getKey);
        Row(3, "", actions);

        var box = new StackPanel();
        box.Children.Add(Dlg.Text(name.ToUpperInvariant(), 11, bold: true, color: Ui.Muted));
        box.Children.Add(g);
        return Dlg.Card(box);
    }

    private void Save()
    {
        _s.OpenAiKey = _openAiKey.Password;
        _s.OpenAiModel = string.IsNullOrWhiteSpace(_openAiModel.Text) ? AiEndpoint.OpenAiDefaultModel : _openAiModel.Text.Trim();
        _s.OpenAiUrl = NullIfEmpty(_openAiUrl.Text);
        _s.DeepSeekKey = _deepSeekKey.Password;
        _s.DeepSeekModel = string.IsNullOrWhiteSpace(_deepSeekModel.Text) ? AiEndpoint.DeepSeekDefaultModel : _deepSeekModel.Text.Trim();
        _s.DeepSeekUrl = NullIfEmpty(_deepSeekUrl.Text);
        _s.Preferred = _preferred.SelectedItem as string ?? "OpenAI";
        _s.Fallback = _fallback.IsChecked == true;
        _s.TeamColours = _colours.IsChecked == true;
    }
}

/// <summary>A text the AI writes (race preview, race report), shown as it is written.</summary>
public class AiTextWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0), FontSize = 14, Padding = new Thickness(4), Background = Brushes.Transparent,
    };
    private readonly TextBlock _status = Dlg.Text("Writing ...", 11, color: Ui.Muted);

    public AiTextWindow(string title, Func<CancellationToken, IAsyncEnumerable<string>> write, AiClient client)
    {
        Title = title; Width = 760; Height = 680; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Dlg.Button("Copy", (_, _) => { try { Clipboard.SetText(_text.Text); } catch { } }));
        buttons.Children.Add(Dlg.Button("Close", (_, _) => Close(), primary: true, isCancel: true));

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = Dlg.Text(title, 18, bold: true);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(Dlg.Card(_text));
        Content = root;

        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) =>
        {
            try
            {
                await foreach (var piece in write(_cts.Token)) { _text.AppendText(piece); _text.ScrollToEnd(); }
                _status.Text = $"Written by {client.LastUsed}. AI can get facts wrong; check anything important.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _status.Text = "The AI couldn't answer: " + ex.Message;
                _status.Foreground = Ui.Bad;
            }
        };
    }
}

/// <summary>Ask AI: a chat about the program, the race on screen, errors and motorsport history.</summary>
public class AiChatWindow : Window
{
    private readonly AiAssistant _ai;
    private readonly string _system;
    private readonly List<AiMessage> _history = new();
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 160, Padding = new Thickness(6, 5, 6, 5),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly Button _send;
    private readonly TextBlock _status = Dlg.Text("", 11, color: Ui.Muted);
    private CancellationTokenSource? _cts;

    public AiChatWindow(AiAssistant ai, string systemPrompt, string? firstQuestion = null)
    {
        _ai = ai;
        _system = systemPrompt;
        Title = "Ask AI"; Width = 760; Height = 760; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");

        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _send = Dlg.Button("Send", (_, _) => _ = SendAsync(), primary: true);
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; _ = SendAsync(); }
        };

        var inputRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        right.Children.Add(_send);
        right.Children.Add(Dlg.Button("Stop", (_, _) => _cts?.Cancel()));
        ((Button)right.Children[1]).Margin = new Thickness(8, 6, 0, 0);
        DockPanel.SetDock(right, Dock.Right);
        inputRow.Children.Add(right);
        inputRow.Children.Add(_input);

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = Dlg.Text("Ask about this race, setting things up, an error in the activity log, a mod, or motorsport history. " +
                            "Enter sends, Shift+Enter starts a new line. AI can get facts wrong.", 12, color: Ui.Muted);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(_status);
        root.Children.Add(inputRow);
        root.Children.Add(Dlg.Card(_scroll));
        Content = root;

        Closed += (_, _) => _cts?.Cancel();
        Loaded += (_, _) =>
        {
            _input.Focus();
            if (firstQuestion != null) { _input.Text = firstQuestion; _ = SendAsync(); }
        };
    }

    private TextBox Bubble(string text, bool user)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontSize = 13,
        };
        var border = new Border
        {
            Child = box, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(user ? 60 : 0, 4, user ? 0 : 60, 4),
            Background = user ? Ui.PlayerRow : Ui.Bubble,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        _messages.Children.Add(border);
        _scroll.ScrollToEnd();
        return box;
    }

    private async Task SendAsync()
    {
        var q = _input.Text.Trim();
        if (q.Length == 0 || _cts != null) return;
        _input.Clear();
        Bubble(q, user: true);
        _history.Add(AiMessage.User(q));
        var answer = Bubble("", user: false);
        _cts = new CancellationTokenSource();
        _send.IsEnabled = false;
        _status.Text = "Thinking ...";
        _status.Foreground = Ui.Muted;
        var sb = new StringBuilder();
        try
        {
            await foreach (var piece in _ai.ChatAsync(_system, _history, _cts.Token))
            {
                sb.Append(piece);
                answer.Text = sb.ToString();
                _scroll.ScrollToEnd();
            }
            _status.Text = $"Answered by {_ai.Client.LastUsed}.";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex)
        {
            if (sb.Length == 0) answer.Text = "(no answer)";
            _status.Text = "The AI couldn't answer: " + ex.Message;
            _status.Foreground = Ui.Bad;
        }
        finally
        {
            if (sb.Length > 0) _history.Add(AiMessage.Assistant(sb.ToString()));
            else _history.RemoveAt(_history.Count - 1);
            _cts = null;
            _send.IsEnabled = true;
            _input.Focus();
        }
    }
}

/// <summary>
/// "Find online with AI": the best download options for each missing car and track, found by OpenAI (web search)
/// and DeepSeek, with every link checked. Results appear as each item is done.
/// </summary>
public class ModOptionsWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = Dlg.Text("Searching ...", 11, color: Ui.Muted);

    /// <param name="find">Finds the options for one request.</param>
    /// <param name="get">Gets one option (download and install, or open the page and watch Downloads).</param>
    public ModOptionsWindow(IReadOnlyList<ModRequest> requests, string services,
        Func<ModRequest, CancellationToken, Task<ModOptions>> find, Action<ModRequest, ModOption> get)
    {
        Title = "Find missing content online with AI"; Width = 860; Height = 700; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Dlg.Button("Close", (_, _) => Close(), primary: true, isCancel: true));

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new StackPanel();
        head.Children.Add(Dlg.Text("Best places to get the missing content", 18, bold: true));
        head.Children.Add(Dlg.Text($"Asked {services}. Links are checked; where a mod has no working link you get a search for its name. " +
                                   "Get downloads direct links and installs them; for pages it opens the page and installs the archive you download " +
                                   "from your Downloads folder. AI can be wrong: check the page before you pay for anything.", 12, color: Ui.Muted));
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(Dlg.Card(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        Content = root;

        var slots = requests.Select(r =>
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            box.Children.Add(Dlg.Text($"{(r.Kind == "track" ? "Track" : "Car")}: {r.RealName}", 15, bold: true));
            box.Children.Add(Dlg.Text("Searching ...", 12, color: Ui.Muted));
            _list.Children.Add(box);
            return (r, box);
        }).ToList();

        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) =>
        {
            var done = 0;
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(slots.Select(async s =>
            {
                await gate.WaitAsync();
                try
                {
                    var result = await find(s.r, _cts.Token);
                    Show(s.box, result, get);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    s.box.Children.RemoveAt(1);
                    s.box.Children.Add(Dlg.Text("Couldn't search: " + ex.Message, 12, color: Ui.Bad));
                }
                finally { gate.Release(); }
                _status.Text = $"{++done} of {slots.Count} searched.";
            }));
            if (!_cts.IsCancellationRequested) _status.Text = $"Done: {slots.Count} searched. Best options first.";
        };
    }

    private static void Show(StackPanel box, ModOptions result, Action<ModRequest, ModOption> get)
    {
        box.Children.RemoveAt(1);
        if (result.Options.Count == 0)
            box.Children.Add(Dlg.Text("No mod found for it. Try Other sites in the missing list.", 12, color: Ui.Warn));
        foreach (var o in result.Options)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var open = Dlg.Button("Open", (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(o.Url) { UseShellExecute = true }));
            open.ToolTip = o.Url;
            actions.Children.Add(open);
            if (o.LinkState != "search")
            {
                var g = Dlg.Button(o.Direct ? "Download & install" : "Get", (_, _) => get(result.Request, o));
                g.ToolTip = o.Direct ? "Downloads the archive and installs it" : "Opens the page; the archive you download there is installed automatically";
                actions.Children.Add(g);
            }
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);

            var info = new StackPanel();
            var title = $"{o.Name}{(o.Author != null ? " — " + o.Author : "")}";
            info.Children.Add(Dlg.Text(title, 13, bold: true));
            var tags = new List<string> { new string('★', o.Quality) + new string('☆', 5 - o.Quality) };
            tags.Add(o.Paid ? "paid" : "free");
            tags.Add(o.LinkState switch { "ok" => o.Direct ? "direct download, checked" : "link checked", "unchecked" => "site blocks link checks", _ => "no known link: search" });
            tags.Add("found by " + string.Join(" + ", o.FoundBy) + (o.Confirmed ? "" : " (from memory, unconfirmed)"));
            if (o.Folder != null) tags.Add("folder " + o.Folder);
            var t = Dlg.Text(string.Join(" · ", tags), 11, color: o.FoundBy.Count > 1 ? Ui.Ok : o.Confirmed ? Ui.Muted : Ui.Warn);
            t.Margin = new Thickness(0, 0, 0, 2);
            info.Children.Add(t);
            if (o.Notes.Length > 0) { var n = Dlg.Text(o.Notes, 12); n.Margin = new Thickness(0); info.Children.Add(n); }
            row.Children.Add(info);
            box.Children.Add(row);
        }
        foreach (var n in result.Notes) box.Children.Add(Dlg.Text(n, 11, color: Ui.Warn));
    }
}
