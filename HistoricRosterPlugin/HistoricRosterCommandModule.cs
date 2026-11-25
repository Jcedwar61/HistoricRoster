using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using Qmmands;
using Serilog;

namespace HistoricRosterPlugin;

/// <summary>
/// Chat / admin commands:
///   /roster_list [category]          saved rosters
///   /roster_find &lt;words&gt;            search rosters
///   /roster_info &lt;id&gt;               details and grid
///   /roster_content &lt;id&gt;            cars/track the server is missing, with download links
///   /roster_active                   roster currently running / pending restart
///   /roster_seat [driver | #number]  book a real driver's seat in the loaded roster (or show yours)
///   /roster_unseat                   free your booked seat
///   /roster_seats                    booked seats of the loaded roster
///   /roster_f1 &lt;year&gt; [round]       (admin) list a season, or import one race
///   /roster_nascar &lt;year&gt; &lt;series&gt; [n]  (admin) list a season (1=Cup 2=Xfinity 3=Truck), or import race n
///   /roster_load &lt;id&gt; [options]     (admin) write cars + track + race length + real conditions; restart to apply
///   /roster_restore                  (admin) put back the config files from before the last load
/// </summary>
public class HistoricRosterCommandModule : ACModuleBase
{
    private const int MaxLines = 20;
    // AssettoServer sends one RCON reply per command and it cannot exceed 1000 ASCII bytes.
    private const int RconBudget = 900;
    private int _rconChars;
    private bool _rconTruncated;

    private readonly HistoricRosterConfiguration _configuration;
    private readonly RosterStore _store;
    private readonly RosterInstaller _installer;
    private readonly ContentResolver _resolver;
    private readonly HistoricRosterService _service;
    private readonly F1Importer _f1;
    private readonly NascarImporter _nascar;

    public HistoricRosterCommandModule(HistoricRosterConfiguration configuration, RosterStore store, RosterInstaller installer,
        ContentResolver resolver, HistoricRosterService service, F1Importer f1, NascarImporter nascar)
    {
        _configuration = configuration;
        _store = store;
        _installer = installer;
        _resolver = resolver;
        _service = service;
        _f1 = f1;
        _nascar = nascar;
    }

    [Command("roster_list")]
    public void List(string? category = null)
    {
        var rosters = _store.List()
            .Where(r => category == null || string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rosters.Count == 0)
        {
            Say(category == null ? "No rosters saved. Import one with /roster_f1 or /roster_nascar." : $"No {category} rosters saved.");
            return;
        }
        Say($"{rosters.Count} roster(s):");
        foreach (var r in rosters.Take(MaxLines))
            Say($"{r.Id} - {r.Name} ({r.Entries.Count} cars{(r.Category != null ? ", " + r.Category : "")})");
        if (rosters.Count > MaxLines) Say($"...and {rosters.Count - MaxLines} more. Filter with /roster_list <category> or search with /roster_find <words>.");
    }

    [Command("roster_find")]
    public void Find([Remainder] string query)
    {
        var found = _store.Find(query);
        if (found.Count == 0) { Say($"No roster matches '{query}'."); return; }
        Say($"{found.Count} match(es) for '{query}':");
        foreach (var r in found.Take(MaxLines))
            Say($"{r.Id} - {r.Name} ({r.Entries.Count} cars{(r.Category != null ? ", " + r.Category : "")})");
        if (found.Count > MaxLines) Say($"...and {found.Count - MaxLines} more. Add words to narrow it down.");
    }

    [Command("roster_info")]
    public void Info(string id)
    {
        var roster = _store.Get(id);
        if (roster == null) { Say($"No roster '{id}'. See /roster_list."); return; }

        Say($"{roster.Name}{(roster.Track != null ? " @ " + roster.Track.Name : "")}");
        if (roster.Source != null) Say($"Source: {roster.Source}");
        if (roster.Notes != null) Say($"Notes: {roster.Notes}");
        if (RaceDistance.Known(roster))
            Say($"Real distance: {RaceDistance.RealLaps(roster)} laps" +
                (_configuration.RaceLengthPercent is > 0 and not 100 ? $" ({_configuration.RaceLengthPercent}% = {RaceDistance.LapsFor(roster, _configuration.RaceLengthPercent)} laps on this server)" : ""));
        var ordered = RosterInstaller.Order(roster.Entries, e => e, _configuration.DefaultGridOrder);
        foreach (var e in ordered.Take(MaxLines))
        {
            var pos = e.Position?.ToString() ?? (e.Status ?? "-");
            Say($"P{e.GridPosition?.ToString() ?? "?"} -> {pos}: {e.Driver}{(e.Number != null ? " #" + e.Number : "")} {e.Team} {e.Car}");
        }
        if (ordered.Count > MaxLines) Say($"...and {ordered.Count - MaxLines} more.");
    }

    [Command("roster_content")]
    public void Content(string id)
    {
        var roster = _store.Get(id);
        if (roster == null) { Say($"No roster '{id}'. See /roster_list."); return; }

        var content = _resolver.Resolve(roster);
        ReplyContent(content);
    }

    [Command("roster_active")]
    public void Active()
    {
        var live = _service.LiveRoster;
        var written = _store.GetActive();
        if (live != null)
        {
            Say($"Running: {live.RosterName} ({live.Slots.Count} cars{(live.Laps != null ? $", {live.Laps} laps" : "")})");
            if (live.Conditions != null) Say($"Real conditions: {live.Conditions}");
        }
        else Say("No historic roster is running.");
        if (written != null && written.RosterId != live?.RosterId)
            Say($"Pending restart: {written.RosterName} (written {written.WrittenAtUtc:yyyy-MM-dd HH:mm} UTC)");
    }

    [Command("roster_seat")]
    public void Seat([Remainder] string? query = null)
    {
        if (!_configuration.AllowSeatBooking) { Say("Seat booking is turned off on this server."); return; }
        if (Client == null || Client.Guid == 0) { Say("Book a seat from in game: it is held for your Steam account."); return; }
        var active = _store.GetActive();
        if (active == null) { Say("No roster is loaded. An admin loads one with /roster_load."); return; }
        var guid = Client.Guid.ToString();
        var seats = _store.GetSeats().Where(b => b.RosterId == active.RosterId).ToList();

        if (string.IsNullOrWhiteSpace(query))
        {
            var mine = seats.FirstOrDefault(b => b.Guid == guid);
            Say(mine != null
                ? $"Your seat in {active.RosterName}: {mine.Driver}{(mine.Number != null ? " #" + mine.Number : "")}. /roster_unseat frees it."
                : $"No seat booked in {active.RosterName}. Book one with /roster_seat <driver name or #number>.");
            return;
        }

        var matches = FindSlots(active, query);
        if (matches.Count == 0) { Say($"No driver in {active.RosterName} matches '{query}'. /roster_info {active.RosterId} shows the grid."); return; }
        if (matches.Count > 1)
        {
            Say($"{matches.Count} drivers match '{query}' - be more specific:");
            foreach (var m in matches.Take(8)) Say($"#{m.Number ?? "?"} {m.Driver} ({m.Team})");
            return;
        }

        var slot = matches[0];
        var taken = seats.FirstOrDefault(b => b.IsFor(slot.Driver, slot.Number) && b.Guid != guid);
        if (taken != null) { Say($"{slot.Driver}'s seat is already booked by {taken.PlayerName}."); return; }
        if (!string.IsNullOrWhiteSpace(slot.RosterGuid) && slot.RosterGuid != guid)
        {
            Say($"{slot.Driver}'s seat is reserved by the roster itself.");
            return;
        }

        _store.UpdateSeats(all =>
        {
            all.RemoveAll(b => b.RosterId == active.RosterId && b.Guid == guid);
            all.Add(new SeatBooking
            {
                RosterId = active.RosterId, Driver = slot.Driver, Number = slot.Number, Guid = guid,
                PlayerName = Client.Name ?? "?", BookedAtUtc = DateTime.UtcNow,
            });
            return 0;
        });
        try
        {
            _installer.ApplyBookings(active);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HistoricRoster: writing seat booking failed");
            Say($"Booking saved, but entry_list.ini could not be updated: {ex.Message}");
            return;
        }
        Log.Information("HistoricRoster: {Player} booked {Driver} in {Roster}", Client.Name, slot.Driver, active.RosterId);
        Say($"Booked: {slot.Driver}{(slot.Number != null ? " #" + slot.Number : "")} {slot.Car ?? slot.Model} in {active.RosterName}.");
        Say("The seat is held for you from the next server restart. /roster_unseat frees it.");
    }

    [Command("roster_unseat")]
    public void Unseat()
    {
        if (Client == null || Client.Guid == 0) { Say("Use this from in game."); return; }
        var active = _store.GetActive();
        if (active == null) { Say("No roster is loaded."); return; }
        var guid = Client.Guid.ToString();
        var removed = _store.UpdateSeats(all => all.RemoveAll(b => b.RosterId == active.RosterId && b.Guid == guid));
        if (removed == 0) { Say("You have no seat booked."); return; }
        _installer.ApplyBookings(active);
        Say("Seat freed (from the next restart).");
    }

    [Command("roster_seats")]
    public void Seats()
    {
        var active = _store.GetActive();
        if (active == null) { Say("No roster is loaded."); return; }
        var seats = _store.GetSeats().Where(b => b.RosterId == active.RosterId).ToList();
        if (seats.Count == 0)
        {
            Say($"No seats booked in {active.RosterName}." + (_configuration.AllowSeatBooking ? " Book one with /roster_seat <driver or #number>." : ""));
            return;
        }
        Say($"{seats.Count} of {active.Slots.Count} seats booked in {active.RosterName} (a booked seat stays empty if its player doesn't join):");
        foreach (var b in seats.Take(MaxLines)) Say($"{b.Driver}{(b.Number != null ? " #" + b.Number : "")} - {b.PlayerName}");
    }

    [Command("roster_f1"), RequireAdmin]
    public async Task F1Async(int year, int? round = null)
    {
        if (!_configuration.EnableF1Import) { Say("F1 import is disabled in the plugin config."); return; }
        if (!CheckYear(year)) return;

        try
        {
            if (round == null)
            {
                var races = await _f1.ListSeasonAsync(year);
                Say($"{year} F1 season - {races.Count} races. Import with /roster_f1 {year} <round>:");
                foreach (var r in races) Say($"{r.Key}. {r.Name} - {r.Track} ({r.Date})");
                return;
            }

            var roster = await _f1.ImportAsync(year, round.Value);
            SaveImported(roster);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HistoricRoster: F1 import failed");
            Say($"F1 import failed: {ex.Message}");
        }
    }

    [Command("roster_nascar"), RequireAdmin]
    public async Task NascarAsync(int year, int series = 1, int? race = null)
    {
        if (!_configuration.EnableNascarImport) { Say("NASCAR import is disabled in the plugin config."); return; }
        if (!CheckYear(year)) return;
        if (year < NascarImporter.FirstYear)
        {
            Say($"NASCAR's results feed starts in {NascarImporter.FirstYear}. For earlier races, add a roster JSON file (see README).");
            return;
        }
        if (series is < 1 or > 3) { Say("Series must be 1 (Cup), 2 (Xfinity) or 3 (Truck)."); return; }

        try
        {
            var races = await _nascar.ListSeasonAsync(year, series);
            if (race == null)
            {
                Say($"{year} {NascarImporter.SeriesName(series)} - {races.Count} events. Import with /roster_nascar {year} {series} <n>:");
                for (var i = 0; i < races.Count; i++)
                    Say($"{i + 1}. {races[i].Name} - {races[i].Track} ({races[i].Date})");
                return;
            }

            if (race < 1 || race > races.Count) { Say($"Pick a number between 1 and {races.Count}."); return; }
            var roster = await _nascar.ImportAsync(year, series, int.Parse(races[race.Value - 1].Key));
            SaveImported(roster);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HistoricRoster: NASCAR import failed");
            Say($"NASCAR import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// /roster_load &lt;id&gt; [options], options in any order:
    ///   AsListed | StartingGrid | FinishingOrder   grid order
    ///   25%  (or 25)                               race length as a share of the real distance; 0% = leave it alone
    ///   noweather | weather                        skip / use the real start time and weather
    ///   standins | nostandins                      fill missing real cars with same-class cars, or leave them out
    /// </summary>
    [Command("roster_load"), RequireAdmin]
    public async Task LoadAsync(string id, [Remainder] string? options = null)
    {
        var roster = _store.Get(id);
        if (roster == null) { Say($"No roster '{id}'. See /roster_list."); return; }
        if (roster.Year != null && !RosterStore.IsCaptured(roster) && !CheckYear(roster.Year.Value)) return;

        var gridOrder = _configuration.DefaultGridOrder;
        var percent = _configuration.RaceLengthPercent;
        var weather = _configuration.RecreateConditions;
        var standIns = _configuration.StandIns;
        foreach (var token in (options ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim().ToLowerInvariant();
            if (Enum.TryParse<GridOrder>(t, ignoreCase: true, out var o) && !int.TryParse(t, out _)) gridOrder = o;
            else if (int.TryParse(t.TrimEnd('%'), out var p) && p is >= 0 and <= 1000) percent = p;
            else if (t is "noweather" or "nowx") weather = false;
            else if (t is "weather" or "wx") weather = true;
            else if (t is "standins" or "standin") standIns = StandInMode.SameClass;
            else if (t is "nostandins" or "nostandin") standIns = StandInMode.Off;
            else
            {
                Say($"Unknown option '{token}'. Options: AsListed|StartingGrid|FinishingOrder, <percent>%, weather|noweather, standins|nostandins.");
                return;
            }
        }

        Conditions? conditions = null;
        if (weather && !RosterStore.IsCaptured(roster))
        {
            try
            {
                conditions = await RealConditions.GetAsync(roster);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "HistoricRoster: weather lookup failed");
            }
            if (conditions == null)
                Say(DateOnly.TryParse(roster.Date, System.Globalization.CultureInfo.InvariantCulture, out _)
                    ? "Real weather not found (no archive data or no connection); time and weather left unchanged."
                    : "The roster has no exact race date, so time and weather are left unchanged.");
        }

        try
        {
            var result = _installer.Install(roster, new InstallOptions
            {
                Order = gridOrder, LapsPercent = percent, Conditions = conditions, StandIns = standIns,
            });
            var a = result.Active;
            Say($"Loaded {roster.Name}: {a.Slots.Count} cars" +
                (a.TrackFolder != null ? $", track {a.TrackFolder}{(a.TrackLayout != null ? "/" + a.TrackLayout : "")}" : "") +
                (a.Laps != null ? $", {a.Laps} laps{(percent != 100 ? $" ({percent}% of the real distance)" : "")}" : "") + ".");
            if (a.Conditions != null) Say($"Real conditions: {a.Conditions}.");
            if (gridOrder == GridOrder.StartingGrid && _configuration.KeepRealGrid)
                Say("Races start from the real grid, even after practice or qualifying.");
            Say("Restart the server to apply. /roster_restore undoes this.");
            foreach (var w in result.Warnings.Take(6)) Say("Warning: " + w);
            ReplyContent(result.Content);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HistoricRoster: loading roster {Roster} failed", id);
            Say($"Load failed: {ex.Message}");
        }
    }

    [Command("roster_restore"), RequireAdmin]
    public void Restore()
    {
        var restored = _installer.RestoreLatestBackups();
        Say(restored.Count == 0
            ? "No backups found."
            : $"Restored {restored.Count} file(s): {string.Join(", ", restored.Select(Path.GetFileName))}. Restart the server to apply.");
    }

    /// <summary>Slots matching "#23", "23", or every word of a driver name (accents ignored).</summary>
    private static List<ActiveSlot> FindSlots(ActiveRoster active, string query)
    {
        var q = query.Trim();
        var number = q.TrimStart('#');
        if (q.StartsWith('#') || number.All(char.IsDigit))
        {
            var byNumber = active.Slots.Where(s => string.Equals(s.Number?.Trim(), number, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byNumber.Count > 0 || q.StartsWith('#')) return byNumber;
        }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = active.Slots.Where(s => words.All(w => TextFold.Contains(s.Driver, w))).ToList();
        // An exact full name wins over partial matches ("Ralf Schumacher" vs "Schumacher").
        var exact = found.Where(s => string.Equals(TextFold.Fold(s.Driver), TextFold.Fold(q), StringComparison.Ordinal)).ToList();
        return exact.Count == 1 ? exact : found;
    }

    private void SaveImported(Roster roster)
    {
        var saved = _store.SaveNew(roster);
        Say($"Saved {saved.Name} as '{saved.Id}' ({saved.Entries.Count} cars).");
        var missing = _resolver.Resolve(saved).Missing();
        Say(missing.Count == 0
            ? $"All content is on the server. Load it with /roster_load {saved.Id}"
            : $"{missing.Count} item(s) not on the server - see /roster_content {saved.Id}. Load anyway with /roster_load {saved.Id}");
    }

    private void ReplyContent(RosterContent content)
    {
        var missing = content.Missing();
        if (missing.Count == 0)
            Say("All cars and the track are installed on the server.");
        else
        {
            Say($"Missing on the server ({missing.Count}) - download, then install with Content Manager (drag the archive onto CM):");
            foreach (var (label, url) in missing.Take(MaxLines)) Say($"{label}: {url}");
            if (missing.Count > MaxLines) Say($"...and {missing.Count - MaxLines} more (see server log).");
        }

    }

    /// <summary>
    /// Reply to the player, or for RCON/console (no client) keep the combined reply under the RCON size limit.
    /// Everything that doesn't fit goes to the server log.
    /// </summary>
    private void Say(string message)
    {
        if (Client != null)
        {
            Reply(message);
            return;
        }

        // Non-ASCII characters would not survive RCON's ASCII encoding anyway.
        var ascii = new string(message.Select(c => c < 128 ? c : '?').ToArray());
        if (_rconTruncated || _rconChars + ascii.Length + 1 > RconBudget)
        {
            if (!_rconTruncated)
            {
                _rconTruncated = true;
                Reply("... (more in the server log)");
            }
            Log.Information("HistoricRoster: {Line}", message);
            return;
        }
        _rconChars += ascii.Length + 1;
        Reply(ascii);
    }

    private bool CheckYear(int year)
    {
        if (year >= _configuration.MinYear && year <= _configuration.EffectiveMaxYear) return true;
        Say($"Only seasons {_configuration.MinYear}-{_configuration.EffectiveMaxYear} are allowed.");
        return false;
    }
}
