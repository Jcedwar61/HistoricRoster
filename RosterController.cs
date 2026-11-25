using Microsoft.AspNetCore.Mvc;

namespace HistoricRosterPlugin;

/// <summary>
/// Read-only HTTP API on the server's HTTP port, used by the "Historic Roster" CSP app.
/// Changes (import, load, restore) go through the admin chat commands instead.
/// </summary>
[ApiController]
[Route("historicroster")]
public class RosterController : ControllerBase
{
    private readonly RosterStore _store;
    private readonly ContentResolver _resolver;
    private readonly HistoricRosterService _service;
    private readonly HistoricRosterConfiguration _configuration;

    public RosterController(RosterStore store, ContentResolver resolver, HistoricRosterService service, HistoricRosterConfiguration configuration)
    {
        _store = store;
        _resolver = resolver;
        _service = service;
        _configuration = configuration;
    }

    /// <summary>GET /historicroster/rosters?q=le+mans+2008&amp;category=LMP1&amp;offset=0&amp;limit=50</summary>
    [HttpGet("rosters")]
    public object Rosters(string? q = null, string? category = null, int offset = 0, int limit = 50)
    {
        IEnumerable<Roster> rosters = string.IsNullOrWhiteSpace(q) ? _store.List() : _store.Find(q);
        if (!string.IsNullOrWhiteSpace(category))
            rosters = rosters.Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase));

        var list = rosters
            // Real races first; races captured on this server after them.
            .OrderBy(r => RosterStore.IsCaptured(r)).ThenByDescending(r => r.Year ?? 0).ThenBy(r => r.Date ?? "").ThenBy(r => r.Name)
            .ToList();
        limit = Math.Clamp(limit, 1, 200);
        return new
        {
            total = list.Count,
            categories = _store.List().Select(r => r.Category).Where(c => c != null).Distinct().Order().ToList(),
            minYear = _configuration.MinYear,
            maxYear = _configuration.EffectiveMaxYear,
            rosters = list.Skip(Math.Max(0, offset)).Take(limit).Select(r => new
            {
                id = r.Id,
                name = r.Name,
                category = r.Category,
                year = r.Year,
                track = r.Track?.Name,
                cars = r.Entries.Count,
            }),
        };
    }

    /// <summary>GET /historicroster/roster/{id}: the grid with the AC car, skin and match type for each entry.</summary>
    [HttpGet("roster/{id}")]
    public ActionResult<object> Roster(string id)
    {
        var roster = _store.Get(id);
        if (roster == null) return NotFound();

        var content = _resolver.Resolve(roster);
        var ordered = RosterInstaller.Order(content.Cars, c => c.Entry, _configuration.DefaultGridOrder);
        var seats = _store.GetSeats().Where(b => b.RosterId == roster.Id).ToList();
        return new
        {
            id = roster.Id,
            name = roster.Name,
            category = roster.Category,
            series = roster.Series,
            year = roster.Year,
            date = roster.Date,
            source = roster.Source,
            notes = roster.Notes,
            realLaps = RaceDistance.Known(roster) ? RaceDistance.RealLaps(roster) : (int?)null,
            track = content.Track == null ? null : new
            {
                name = roster.Track?.Name,
                folder = content.Track.Folder,
                layout = content.Track.Layout,
                installed = content.Track.Installed,
                pitBoxes = content.Track.PitBoxes,
                url = content.Track.DownloadUrl,
            },
            entries = ordered.Select(c => new
            {
                driver = c.Entry.Driver,
                team = c.Entry.Team,
                number = c.Entry.Number,
                @class = c.Entry.Class,
                car = c.Entry.Car,
                grid = c.Entry.GridPosition,
                position = c.Entry.Position,
                status = c.Entry.Status,
                model = c.Model,
                skin = c.Skin,
                match = c.Match,
                installed = c.Installed,
                // Player who booked this seat (name only).
                bookedBy = seats.FirstOrDefault(b => b.IsFor(c.Entry.Driver, c.Entry.Number))?.PlayerName,
            }),
            // Missing on the server.
            missing = content.MissingSources().Select(m => new
            {
                label = m.Label,
                url = m.Sources.FirstOrDefault()?.Url,
                sources = m.Sources.Select(s => new { name = s.Name, url = s.Url }),
            }),
            // Every real car/track folder the roster uses, so a client can check its own install (CSP app).
            needs = NeedsList(content),
        };
    }

    private static object Sources(IReadOnlyList<DownloadSource> sources) => sources.Select(s => new { name = s.Name, url = s.Url }).ToList();

    private static List<object> NeedsList(RosterContent content)
    {
        var list = new List<object>();
        if (content.Track != null)
            list.Add(new { kind = "track", folder = content.Track.Folder, layout = content.Track.Layout, label = content.Track.Label, url = content.Track.DownloadUrl, sources = Sources(content.Track.Sources) });
        foreach (var car in content.Cars.GroupBy(c => c.Model ?? "?" + c.Label).Select(g => g.First()))
            list.Add(new { kind = "car", folder = car.Model, layout = (string?)null, label = car.Label, url = car.DownloadUrl, sources = Sources(car.Sources) });
        return list;
    }

    /// <summary>GET /historicroster/active: running roster and any roster waiting for a restart.</summary>
    [HttpGet("active")]
    public object Active()
    {
        var live = _service.LiveRoster;
        var written = _store.GetActive();
        return new
        {
            running = live == null ? null : new { id = live.RosterId, name = live.RosterName, cars = live.Slots.Count, laps = live.Laps, conditions = live.Conditions },
            pending = written == null || written.RosterId == live?.RosterId ? null : new { id = written.RosterId, name = written.RosterName },
            rosterCount = _store.List().Count,
        };
    }
}
