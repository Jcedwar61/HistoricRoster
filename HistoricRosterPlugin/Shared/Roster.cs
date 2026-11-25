namespace HistoricRosterPlugin;

/// <summary>A real or captured race line-up. Stored as one JSON file per roster.</summary>
public class Roster
{
    /// <summary>File-safe id: lowercase letters, digits, '-' and '_'. Also the file name without .json.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>F1, GT1, LMP1, GT3, NASCAR, ... Used to pick catalog rules and fallback models.</summary>
    public string? Category { get; set; }
    public string? Series { get; set; }
    public int? Year { get; set; }
    public string? Date { get; set; }
    /// <summary>Race start time in UTC, e.g. "13:00:00Z", when the source has it.</summary>
    public string? Time { get; set; }
    /// <summary>
    /// The event's usual local start, e.g. "16:00" for Le Mans, when the source has no exact time. Used only when
    /// <see cref="Time"/> is missing, and shown as the usual time, not the exact one.
    /// </summary>
    public string? UsualLocalTime { get; set; }
    public RosterTrack? Track { get; set; }
    /// <summary>Where the data came from (URL, book, "captured on server"). Shown with /roster_info.</summary>
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public List<RosterEntry> Entries { get; set; } = new();
}

public class RosterTrack
{
    /// <summary>Real circuit name, e.g. "Circuit de Barcelona-Catalunya".</summary>
    public string Name { get; set; } = "";
    /// <summary>Source id, e.g. Jolpica circuitId "catalunya". Also used for catalog matching.</summary>
    public string? Key { get; set; }
    /// <summary>Town/city of the circuit, e.g. "Melbourne". Also used for catalog matching.</summary>
    public string? Locality { get; set; }
    /// <summary>Circuit position, when the source has it (used for the real weather).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>AC track folder. Wins over the catalog.</summary>
    public string? Folder { get; set; }
    /// <summary>AC layout folder (CONFIG_TRACK). Empty for single-layout tracks.</summary>
    public string? Layout { get; set; }
    /// <summary>Exact download page for this track. Wins over the catalog and the overtake.gg search link.</summary>
    public string? DownloadUrl { get; set; }
}

public class RosterEntry
{
    public string Driver { get; set; } = "";
    public string? Team { get; set; }
    public string? Number { get; set; }
    public string? Nation { get; set; }
    /// <summary>Class in multi-class races, e.g. "LMP1", "GT1", "GTE Pro".</summary>
    public string? Class { get; set; }

    /// <summary>The real car name, e.g. "Ferrari F2004". Used for catalog matching and download searches.</summary>
    public string? Car { get; set; }
    /// <summary>Assetto Corsa car folder name, e.g. "ks_ferrari_f2004". Wins over the catalog.</summary>
    public string? Model { get; set; }
    /// <summary>Skin folder inside the car's skins folder.</summary>
    public string? Skin { get; set; }
    /// <summary>Exact download page for this car. Wins over the catalog and the overtake.gg search link.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Real starting position (1 = pole).</summary>
    public int? GridPosition { get; set; }
    /// <summary>Real finishing position. Null for non-classified.</summary>
    public int? Position { get; set; }
    /// <summary>"Finished", "Accident", "+1 Lap", ... free text.</summary>
    public string? Status { get; set; }
    public int? Laps { get; set; }

    /// <summary>Steam GUID. When set, the slot is reserved for that player only.</summary>
    public string? Guid { get; set; }
    public float Ballast { get; set; }
    public int Restrictor { get; set; }
}

/// <summary>What was last written to entry_list.ini, so seats can be announced after the restart.</summary>
public class ActiveRoster
{
    public string RosterId { get; set; } = "";
    public string RosterName { get; set; } = "";
    public DateTime WrittenAtUtc { get; set; }
    public string EntryListPath { get; set; } = "";
    public string? TrackFolder { get; set; }
    public string? TrackLayout { get; set; }
    /// <summary>Grid order the entry list was written in. StartingGrid = the real grid.</summary>
    public GridOrder Order { get; set; } = GridOrder.StartingGrid;
    /// <summary>Race laps written to server_cfg.ini, when the race length was set.</summary>
    public int? Laps { get; set; }
    /// <summary>Real race-day conditions written to server_cfg.ini, e.g. "rain, 14 °C at 14:00".</summary>
    public string? Conditions { get; set; }
    public List<ActiveSlot> Slots { get; set; } = new();
}

public class ActiveSlot
{
    public int Slot { get; set; }
    public string Model { get; set; } = "";
    public string? Skin { get; set; }
    public string Driver { get; set; } = "";
    public string? Team { get; set; }
    public string? Number { get; set; }
    public string? Car { get; set; }
    public string? Class { get; set; }
    /// <summary>The real starting and finishing positions, for the "beat history" comparison.</summary>
    public int? RealGrid { get; set; }
    public int? RealPosition { get; set; }
    public string? RealStatus { get; set; }
    /// <summary>Set when the real car isn't installed and <see cref="Model"/> stands in for it: the real car's name.</summary>
    public string? StandInFor { get; set; }
    /// <summary>GUID the roster itself reserves this slot for (captured rosters); seat bookings are kept separately.</summary>
    public string? RosterGuid { get; set; }
}

/// <summary>A player's booking of a real driver's seat in one roster (rosters/_seats.json).</summary>
public class SeatBooking
{
    public string RosterId { get; set; } = "";
    /// <summary>The real driver whose seat is booked (matched by name, and number when there is one).</summary>
    public string Driver { get; set; } = "";
    public string? Number { get; set; }
    public string Guid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public DateTime BookedAtUtc { get; set; }

    public bool IsFor(string driver, string? number)
        => string.Equals(TextFold.Fold(Driver), TextFold.Fold(driver), StringComparison.Ordinal)
           && string.Equals(Number?.Trim() ?? "", number?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Who may take a missing real car's seat.</summary>
public enum StandInMode
{
    /// <summary>Only the real car: entries whose car isn't installed are left out.</summary>
    Off,
    /// <summary>An installed car of the same class and period stands in, and the seat says so.</summary>
    SameClass,
}

public enum GridOrder
{
    /// <summary>Keep the order the entries are listed in the roster file.</summary>
    AsListed,
    /// <summary>Order by the real starting grid (GridPosition), falling back to listing order.</summary>
    StartingGrid,
    /// <summary>Order by the real finishing result (Position), falling back to listing order.</summary>
    FinishingOrder,
}
