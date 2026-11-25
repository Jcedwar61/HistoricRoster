# Historic Roster → SimCast: race context (sketch)

Goal: when Historic Roster starts a race, SimCast's booth knows the *real* race behind it (event, date, real grid,
real result, real conditions) and can talk about it like a TV booth does ("Alonso won this from pole in 2006, today
they're fourth"). The two apps stay separate; the only contract is one JSON file.

Status: **both sides implemented.** Historic Roster: `Core/SimCastContext.cs`, hooked in `WriteRace()`. SimCast:
`simcast/history.py` (loaded per session in `race.py`), the REAL RACE brief in `ai.py`, instant lines in
`writer.py`, Settings > Commentary > Real races / Real result, the race Historic Roster has set up on the start
screen, tests in `tools/tests/test_history.py`. "Start SimCast with the race" is `App/SimCastLauncher.cs`: SimCast.exe
from its uninstall entry (`HKCU\...\Uninstall\SimCast`, `InstallLocation`, checked against SimCast 1.18:
`%LOCALAPPDATA%\Programs\SimCast`), else that default folder; not started again while any window whose title starts
with "SimCast" is open (its main window is "SimCast <version>"). Both are in the user guides.

Sections 2 and 3 below are the original design sketch; the shipped code is the files named above.

---

## 1. The contract

**File:** `%APPDATA%\SimCast\race_context.json`

- Historic Roster writes it only when SimCast is on the PC: `%APPDATA%\SimCast\` exists, or SimCast.exe is installed
  (then the folder is created, so it works before SimCast's first run). Non-SimCast users get nothing new on disk.
- Written atomically (`.tmp` then rename), UTF-8 without BOM, for every race Historic Roster sets up: the program's
  Write / Start race and Content Manager presets (with the SimCast setting on), and `RosterTool race` (unless
  `--no-simcast`). All go through `SimCastContext.Update`.
- A race set up without a context (setting off, `--no-simcast`, or the write failed) removes the last race's file, so
  SimCast never takes an older race on the same track for this one. Only a file whose `source` is "Historic Roster" is
  removed; SimCast itself never writes it.
- `written_utc` is always `yyyy-MM-ddTHH:mm:ssZ` in the invariant culture (SimCast parses exactly that).
- SimCast only reads it. It uses the file when all of these hold, and otherwise ignores it:
  - the sim is Assetto Corsa
  - `ac.track` matches the AC track folder (the static page's `track`, before SimCast adds ` (layout)`)
  - `written_utc` is less than 12 hours old
  - `schema` is a version SimCast knows

**Schema v1**

```jsonc
{
  "schema": 1,
  "source": "Historic Roster",
  "app_version": "x.y.z",
  "written_utc": "2026-11-25T19:02:11Z",

  "ac": {
    "track": "ks_monza66",          // race.ini [RACE] TRACK, matched against SimCast's frame
    "layout": "",                   // CONFIG_TRACK
    "race_laps": 26,                // laps in this sim race
    "sessions": ["race"],           // or ["qualify","race"], ["practice","qualify","race"]
    "watch": false                  // true = the player is watching, the in-game app drives
  },

  "event": {
    "name": "2006 Italian Grand Prix",
    "series": "Formula 1",
    "category": "F1",
    "year": 2006,
    "date": "2006-09-10",
    "circuit": "Autodromo Nazionale Monza",
    "locality": "Monza",
    "real_laps": 53,
    "multiclass": false,
    "classes": []                   // fastest first, when multiclass
  },

  "real_conditions": {              // the real race day; null when real conditions are off or the lookup failed
    "start_local": "14:00",
    "start_known": true,            // false = the event's usual start, or 14:00 assumed
    "ambient_c": 24,
    "kind": "clear",                // clear, some cloud, cloudy, overcast, drizzle, rain, heavy rain
    "night": false,
    "summary": "clear, 24 °C, start 14:00 local"
  },

  "player": {
    "car_index": 0,                 // race.ini CAR_0 is always the player
    "seat_driver": "Michael Schumacher"  // the real driver whose seat the player took
  },

  "grid": [                         // every car in race.ini except the Safety Car, by CAR_n index
    {
      "car_index": 1,               // = AC car id = CrewChiefEx slot: the primary match key
      "driver": "Fernando Alonso",
      "team": "Renault",
      "number": "1",
      "nation": "ESP",
      "car": "Renault R26",         // always the real car's name
      "ac_model": "rss_formula_hybrid_2006",  // the AC car folder actually driven
      "class": null,                // only in multiclass races
      "stand_in_for": null,         // real car name when an installed car stands in
      "real_grid": 10,
      "real_pos": null,             // null = didn't finish
      "real_status": "Engine",
      "real_laps": 43
    }
  ],

  "not_racing": [                   // real entries left out (car not installed)
    { "driver": "Christijan Albers", "team": "MF1", "real_grid": 19, "real_pos": 17 }
  ],

  "source_note": "Wikipedia / Jolpica"
}
```

Every key is always present (null when unknown); `app_version` is the program's version.

Notes:
- **Match cars by `car_index`, then by folded name.** The player's car carries the player's own name in race.ini,
  not the real driver's, so a name-only match would miss it.
- The schema has no free text an AI would read as instructions. The only text fields are names and the
  conditions summary. SimCast quotes them as data.
- Later (v1.1, optional): `season` with `{name, round, standings_before: [{driver, points}]}` from `Core/Season.cs`.

**Compatibility (SimCast is updated often and separately):**
- Historic Roster works with any SimCast version. It finds SimCast through the installer's uninstall entry, treats any
  window titled "SimCast…" as SimCast running, and writes the file even before SimCast's first run.
- SimCast versions from before this feature never read the file, so nothing changes for them.
- The format is a fixed contract. Adding fields is fine (SimCast ignores unknown ones, and tests this). Renaming,
  removing or changing the meaning of a field means raising `schema`, which older SimCast versions then ignore.
- SimCast's `tools/tests/fixtures/historic_roster_context_v1.json` is a file written by Historic Roster's own writer.
  SimCast's tests load it, so a future SimCast that can't read schema 1 any more fails its tests. Don't edit the
  fixture to make a test pass.

---

## 2. Historic Roster side (~half a day)

**New file `Core/SimCastContext.cs`**

```csharp
public static class SimCastContext
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimCast");

    /// <summary>Writes race_context.json for SimCast when it's installed. Never throws.</summary>
    public static string? TryWrite(RaceSetup s, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            // Same car order as RaceIniWriter.Write: player first, then grid order, Safety Car last.
            var cars = new List<RaceSlot> { s.Player };
            cars.AddRange(s.Grid.Where(c => !c.IsPlayer).OrderBy(c => c.IsSafetyCar ? 1 : 0).ThenBy(c => c.GridPosition));
            var doc = new
            {
                schema = 1, source = "Historic Roster", written_utc = DateTime.UtcNow,
                ac = new { track = s.Track.Folder, layout = s.Track.Layout ?? "", race_laps = s.Laps,
                           sessions = Sessions(s.Weekend), watch = s.Watch },
                @event = new { name = s.Roster.Name, series = s.Roster.Series, category = s.Roster.Category,
                               year = s.Roster.Year, date = s.Roster.Date, circuit = s.Roster.Track?.Name,
                               locality = s.Roster.Track?.Locality, real_laps = RaceDistance.RealLaps(s.Roster),
                               multiclass = s.IsMultiClass, classes = RaceClasses.Ordered(...) },
                conditions = ...,   // from the RealConditions result already applied to s
                player = new { car_index = 0, seat_driver = s.Player.Car.Entry.Driver },
                grid = cars.Select((c, i) => (c, i)).Where(x => !x.c.IsSafetyCar).Select(x => new {
                    car_index = x.i, driver = x.c.Car.Entry.Driver, team = x.c.Car.Entry.Team, ...,
                    stand_in_for = x.c.RealCar?.Entry.Car,
                    real_grid = x.c.Car.Entry.GridPosition, real_pos = x.c.Car.Entry.Position,
                    real_status = x.c.Car.Entry.Status, real_laps = x.c.Car.Entry.Laps }),
                not_racing = s.LeftOut.Select(...),
            };
            var path = Path.Combine(Folder, "race_context.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(doc, RosterJson.Options));
            File.Move(path + ".tmp", path, overwrite: true);
            return path;
        }
        catch (Exception ex) { log("SimCast race context not written: " + ex.Message); return null; }
    }
}
```

The car-order logic is duplicated from `RaceIniWriter.Write` (`Core/RaceSetup.cs:400`). Better to pull it out
into one `RaceIniWriter.CarOrder(setup)` that both use, so the indexes can't drift apart.

**Hook:** `App/MainWindow.xaml.cs` in `WriteRace()`, right after `RaceIniWriter.Write(...)`:

```csharp
if (_settings.SimCastContext && SimCastContext.TryWrite(_setup, Log) is { } ctx)
    Log($"SimCast race context written to {ctx}.");
```

**Settings:** a "Send race details to SimCast's commentary" checkbox, on by default, shown only when the
SimCast folder exists. Optionally later: "Start SimCast with the race" (built: see Status above for how SimCast is
found).

**Online (AssettoServer plugin):** not in scope. SimCast runs on the player's PC, not the server. A later
version could have the in-game app write the same file from the server's `ActiveRoster`.

---

## 3. SimCast side (~a day)

**New module `simcast/history.py`**

```python
PATH = os.path.join(APP_DIR, "race_context.json")
MAX_AGE = 12 * 3600

class RaceContext:
    def __init__(self, d): ...
    @staticmethod
    def load(frame):
        """The Historic Roster context for this session, or None."""
        if frame.game != "AC": return None
        # read, check schema == 1, written_utc fresh, ac.track == frame's track folder
    def entry(self, car):      # by car index, then by folded name
    def real_winner(self): ...
    def real_podium(self): ...
    def brief(self, tr, writer, spoilers): ...  # the REAL RACE block for the AI
```

Load it in `RaceTracker.reset()` (`race.py:124`, already called on every new session/track) and keep it as
`tr.history`.

**AI booth (`ai.py`)**
- `race_brief`: when `tr.history` is set, add a block:
  ```
  REAL RACE (history; may be quoted as history, never as what is happening now):
  2006 Italian Grand Prix, Monza, 10 Sep 2006, 53 laps. Real winner: Michael Schumacher (Ferrari) from P2.
  Viewer's driver is in Michael Schumacher's seat (real: started P2, won).
  ```
  Then per standing row, add to `notes`: `real: P2 -> won` / `real: P10 -> retired (Engine)`. And
  `stand-in for <car>` where that applies, so the booth doesn't call the wrong car.
- `SYSTEM` hard rules: replace "never invent ... history" with "History may only come from REAL RACE. Say it in
  the past tense, and never present it as what is happening in this race."
- `intro_task`: when there's history, "welcome viewers to the {event} at {circuit}, {date}", and the analyst sets
  up the real story.
- `wrap_task`: compare the result to the real one: who beat history, the viewer's driver vs their real result.

**Instant lines (`writer.py`, no AI key needed):** a small set, for example:
- intro: "Welcome to Monza, the Italian Grand Prix of 2006."
- the player's car passes its real finishing position late in the race: "Schumacher's seat is ahead of where
  history put it!"
- finish: "In 2006 this one went to Schumacher. Today it's Alonso."

**Spoilers setting:** `Settings → Commentary → Real results`:
- *after the finish* (default): real grid, conditions and retirements are fair game, but the real result only comes
  up in the wrap-up
- *any time*
- *never*

`brief()` and the instant lines respect this setting.

**GUI:** on the start screen, show a "Historic Roster: 2006 Italian Grand Prix" chip when a fresh context matches,
so the player can see the booth has it.

---

## 4. Test plan
- Historic Roster: a unit test that the `car_index` in the context equals the CAR_n index in the race.ini
  written by the same setup (watch and normal, multiclass, with Safety Car and stand-ins).
- SimCast: `--demo` with a fixture `race_context.json` and AC track id. Check the brief includes REAL RACE. Check a
  stale or mismatched file is ignored. Check spoiler modes.
- End to end: start a race from Historic Roster with SimCast running and CrewChiefEx on, and listen for the intro
  and the wrap-up.

## 5. Open questions
- ~~SimCast's install path~~: settled, `HKCU\...\Uninstall\SimCast` → `InstallLocation`.
- The Historic Roster unit test in the test plan (context `car_index` = race.ini CAR_n) isn't written yet: there's
  no test project for the program. `RaceIniWriter.CarOrder` being the only place that orders cars covers it for now.
- Should `real_status` be normalised (Finished / +n laps / DNF reason), or passed through as free text? Free text
  is fine for the AI; instant lines only need `real_pos == null`.
- Practice/qualifying sessions: probably give history only to the intro and race. Qualifying talk about the "real
  pole" works too.
