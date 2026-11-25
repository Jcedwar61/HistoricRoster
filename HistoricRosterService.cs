using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace HistoricRosterPlugin;

/// <summary>
/// Keeps the real starting grid, tells players whose historical seat they are in and how they did against the real
/// driver, and saves finished races as rosters.
/// </summary>
public class HistoricRosterService : IHostedService
{
    private readonly HistoricRosterConfiguration _configuration;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly RosterStore _store;

    /// <summary>The installed roster, if it matches the cars this server started with.</summary>
    public ActiveRoster? LiveRoster { get; private set; }

    public HistoricRosterService(HistoricRosterConfiguration configuration, ACServerConfiguration serverConfiguration,
        SessionManager sessionManager, EntryCarManager entryCarManager, RosterStore store)
    {
        _configuration = configuration;
        _serverConfiguration = serverConfiguration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LiveRoster = MatchActiveRoster();
        if (LiveRoster != null)
            Log.Information("HistoricRoster: running roster {Roster} ({Name})", LiveRoster.RosterId, LiveRoster.RosterName);

        _sessionManager.SessionChanged += OnSessionChanged;
        _entryCarManager.ClientConnected += OnClientConnected;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.SessionChanged -= OnSessionChanged;
        _entryCarManager.ClientConnected -= OnClientConnected;
        return Task.CompletedTask;
    }

    /// <summary>The roster is "live" only if the server's cars are exactly the ones written for it.</summary>
    private ActiveRoster? MatchActiveRoster()
    {
        var active = _store.GetActive();
        if (active == null) return null;

        var cars = _entryCarManager.EntryCars;
        var matches = cars.Length == active.Slots.Count
                      && active.Slots.All(s => s.Slot < cars.Length
                                               && string.Equals(cars[s.Slot].Model, s.Model, StringComparison.OrdinalIgnoreCase));
        if (!matches)
        {
            Log.Information("HistoricRoster: roster {Roster} was written but the server is running a different entry list (restart pending?)", active.RosterId);
            return null;
        }
        return active;
    }

    public ActiveSlot? SlotFor(EntryCar car) => LiveRoster?.Slots.FirstOrDefault(s => s.Slot == car.SessionId);

    private void OnClientConnected(ACTcpClient client, EventArgs args)
    {
        if (!_configuration.AnnounceSeatOnJoin || LiveRoster == null) return;
        client.FirstUpdateSent += OnFirstUpdateSent;
    }

    private void OnFirstUpdateSent(ACTcpClient client, EventArgs args)
    {
        client.FirstUpdateSent -= OnFirstUpdateSent;
        var slot = SlotFor(client.EntryCar);
        if (slot == null || LiveRoster == null) return;

        var number = string.IsNullOrWhiteSpace(slot.Number) ? "" : $"#{slot.Number} ";
        var car = slot.StandInFor != null ? $"{slot.StandInFor} (stand-in: {slot.Model})" : slot.Car ?? slot.Model;
        client.SendChatMessage($"{LiveRoster.RosterName}: you are driving {slot.Driver}'s {number}{car}" +
                               (string.IsNullOrWhiteSpace(slot.Team) ? "" : $" ({slot.Team})"));
        var real = slot.RealPosition != null ? $"P{slot.RealPosition}" : slot.RealStatus ?? "not classified";
        client.SendChatMessage($"Real race: started P{slot.RealGrid?.ToString() ?? "?"}, finished {real}." +
                               (LiveRoster.Conditions != null ? $" Conditions: {LiveRoster.Conditions}." : "") +
                               (_configuration.CompareWithHistory ? " Beat it!" : ""));
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        KeepRealGrid(args);

        var finished = args.PreviousSession;
        if (finished?.Configuration.Type != SessionType.Race || finished.Results == null) return;

        try
        {
            var results = OrderedResults(finished);
            if (results.Count == 0) return;
            if (_configuration.CompareWithHistory && LiveRoster != null) CompareWithHistory(results);
            if (_configuration.CaptureRaceResults) CaptureRace(results);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HistoricRoster: could not handle race results");
        }
    }

    /// <summary>
    /// AssettoServer sets a race's grid from the previous session's best laps (practice, qualifying, or the last race
    /// when sessions loop). This runs before the grid is sent, so the real starting grid (the entry list order) is put back.
    /// </summary>
    private void KeepRealGrid(SessionChangedEventArgs args)
    {
        if (!_configuration.KeepRealGrid || LiveRoster is not { Order: GridOrder.StartingGrid }) return;
        if (args.NextSession.Configuration.Type != SessionType.Race || args.InvertedGridSlots > 0) return;
        args.NextSession.Grid = _entryCarManager.EntryCars;
        Log.Information("HistoricRoster: race starts from the real grid of {Roster}", LiveRoster.RosterId);
    }

    /// <summary>Classified finishers first (by RacePos), then everyone else by laps completed.</summary>
    private List<(byte SessionId, EntryCarResult Result)> OrderedResults(SessionState session)
    {
        var cars = _entryCarManager.EntryCars;
        return session.Results!
            .Where(r => !string.IsNullOrWhiteSpace(r.Value.Name) && r.Key < cars.Length)
            .OrderBy(r => r.Value.RacePos > 0 ? r.Value.RacePos : uint.MaxValue)
            .ThenByDescending(r => r.Value.NumLaps)
            .Select(r => (r.Key, r.Value))
            .ToList();
    }

    /// <summary>Tells each player their result against the real driver's, and everyone who beat history by most.</summary>
    private void CompareWithHistory(List<(byte SessionId, EntryCarResult Result)> results)
    {
        var cars = _entryCarManager.EntryCars;
        var finish = 0;
        (string Name, string Driver, int Gain)? best = null;
        foreach (var (sessionId, result) in results)
        {
            int? yours = result.RacePos > 0 ? ++finish : null;
            var car = cars[sessionId];
            var slot = SlotFor(car);
            if (slot == null) continue;

            var real = slot.RealPosition != null ? $"P{slot.RealPosition}" : slot.RealStatus ?? "not classified";
            string verdict;
            if (yours == null) verdict = $"You weren't classified. {slot.Driver}'s real result: {real}.";
            else if (slot.RealPosition == null) verdict = $"You finished P{yours} as {slot.Driver}, who really retired ({real}). Better than history!";
            else
            {
                var gain = slot.RealPosition.Value - yours.Value;
                verdict = $"You finished P{yours} as {slot.Driver} - real result {real}, " +
                          (gain > 0 ? $"+{gain}: you beat history!" : gain < 0 ? $"{gain}." : "exactly as it happened.");
                if (gain > 0 && (best == null || gain > best.Value.Gain)) best = (result.Name, slot.Driver, gain);
            }
            car.Client?.SendChatMessage(verdict);
        }
        if (best is { } b)
            _entryCarManager.BroadcastChat($"Biggest gain on history: {b.Name} as {b.Driver}, +{b.Gain} places.");
    }

    private void CaptureRace(List<(byte SessionId, EntryCarResult Result)> results)
    {
        var cars = _entryCarManager.EntryCars;
        var track = _serverConfiguration.Server.Track;
        var layout = _serverConfiguration.Server.TrackConfig;
        var now = DateTime.Now;
        var historic = LiveRoster != null ? _store.Get(LiveRoster.RosterId) : null;
        var roster = new Roster
        {
            Id = RosterIds.Slugify($"server-{now:yyyy-MM-dd-HHmm}-{Path.GetFileName(track)}"),
            Name = $"Server race {now:yyyy-MM-dd HH:mm} - {(historic != null ? historic.Name : Path.GetFileName(track))}",
            // Kept apart from the real races; the real race's category is kept as the series.
            Category = RosterStore.CapturedCategory,
            Series = historic?.Category,
            Year = now.Year,
            Date = now.ToString("yyyy-MM-dd"),
            Track = new RosterTrack
            {
                Name = Path.GetFileName(track),
                Folder = track,
                Layout = string.IsNullOrWhiteSpace(layout) ? null : layout,
            },
            Source = "Captured on this server",
            Notes = LiveRoster != null ? $"Run with historic roster '{LiveRoster.RosterId}'" : null,
        };

        var grid = 1;
        var finish = 1;
        foreach (var (sessionId, result) in results)
        {
            var car = cars[sessionId];
            var slot = SlotFor(car);
            roster.Entries.Add(new RosterEntry
            {
                Driver = result.Name,
                Team = string.IsNullOrWhiteSpace(result.Team) ? slot?.Team : result.Team,
                Nation = string.IsNullOrWhiteSpace(result.NationCode) ? null : result.NationCode,
                Number = slot?.Number,
                Class = slot?.Class,
                Car = slot?.Car,
                Model = car.Model,
                Skin = car.Skin,
                GridPosition = grid++,
                Position = result.RacePos > 0 ? finish++ : null,
                Status = result.HasCompletedLastLap ? "Finished" : "Not classified",
                Laps = (int)result.NumLaps,
                Guid = _configuration.CaptureGuids && result.Guid != 0 ? result.Guid.ToString() : null,
                Ballast = car.Ballast,
                Restrictor = car.Restrictor,
            });
        }

        var saved = _store.SaveNew(roster, captured: true);
        Log.Information("HistoricRoster: saved race results as roster {Roster} ({Count} drivers)", saved.Id, saved.Entries.Count);
    }
}
