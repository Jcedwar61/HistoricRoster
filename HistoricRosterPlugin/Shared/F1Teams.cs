using System.Text.RegularExpressions;

namespace HistoricRosterPlugin;

/// <summary>
/// Identifies which F1 constructor an installed car belongs to, from its name/folder (team names first,
/// then chassis codes checked against the season). F1 mods often put the pack name ("Grand Prix 2009")
/// in "brand", so the brand can't be trusted. Returns the team names used by the results API (Jolpica),
/// so a catalog rule for the car matches the real entries of that team in that season.
/// </summary>
public static class F1Teams
{
    private record Team(string[] ResultNames, string NamePattern, string? ChassisPattern, int From, int To);

    private static readonly Team[] Teams =
    {
        // Names that contain another team's name come first (BMW Sauber before Sauber, Toro Rosso before Red Bull...).
        new(new[] { "BMW Sauber" }, @"bmw ?sauber", @"\bf1[ ._-]?0[5-9]\b|\bf10[5-9]\b", 2006, 2010),
        new(new[] { "Toro Rosso" }, @"toro ?rosso", @"\bstr ?\d{1,2}\b", 2006, 2019),
        new(new[] { "AlphaTauri" }, @"alpha ?tauri", @"\bat0[1-4]\b", 2020, 2023),
        new(new[] { "RB F1 Team", "Racing Bulls" }, @"vcarb|racing ?bulls?|visa ?cash", @"\bvcarb ?\d{2}\b", 2024, 2030),
        new(new[] { "Red Bull" }, @"red ?bull", @"\brb ?\d{1,2}[a-z]?\b", 2005, 2030),
        new(new[] { "Super Aguri" }, @"super ?aguri", @"\bsa0[5-8]\b", 2006, 2008),
        new(new[] { "Force India" }, @"force ?india", @"\bvjm ?\d{2}\b", 2008, 2018),
        new(new[] { "Racing Point" }, @"racing ?point", @"\brp ?(19|20)\b", 2019, 2020),
        new(new[] { "Aston Martin" }, @"aston ?martin", @"\bamr ?\d{2}\b", 2021, 2030),
        new(new[] { "Alfa Romeo" }, @"alfa ?romeo", @"\bc3[89]\b|\bc4[0-3]\b", 2019, 2023),
        new(new[] { "Sauber" }, @"sauber|\bstake\b|\bkick\b", @"\bc\d{2}\b", 1993, 2025),
        new(new[] { "Audi" }, @"\baudi\b", @"\br26\b", 2026, 2030),
        new(new[] { "Cadillac" }, @"cadillac", @"\bcad ?\d{2}\b|\bmac ?\d{2}\b", 2026, 2030),
        new(new[] { "Williams" }, @"williams", @"\bfw ?\d{2}\b", 1977, 2030),
        new(new[] { "McLaren" }, @"mclaren", @"\bmp4[ ._-]?\d{1,2}\b|\bmcl ?\d{2}\b", 1966, 2030),
        new(new[] { "Ferrari" }, @"ferrari|scuderia",
            @"\bf300\b|\bf399\b|\bf1[ -]?2000\b|\bf200[0-8]\b|\b248\b|\bf60\b|\bf10\b|\b150 ?italia\b|\bf2012\b|\bf138\b|\bf14 ?t\b|\bsf ?[-_]?\d{2}[a-z]?\b|\bsf\d{2}", 1950, 2030),
        new(new[] { "Renault" }, @"renault", @"\br2[0-9]\b|\br3[01]\b", 2002, 2011),
        new(new[] { "Renault" }, @"renault", @"\br\.? ?s\.? ?(1[6-9]|20)\b", 2016, 2020),
        new(new[] { "Lotus F1" }, @"lotus", @"\be2[0-3]\b", 2012, 2015),
        new(new[] { "Lotus" }, @"lotus", @"\bt12[78]\b", 2010, 2011),
        new(new[] { "Mercedes" }, @"mercedes", @"\bw\d{2}\b", 2010, 2030),
        new(new[] { "Toyota" }, @"toyota", @"\btf1\d{2}\b", 2002, 2009),
        new(new[] { "Honda" }, @"honda", @"\bra10[6-8]\b", 2006, 2008),
        new(new[] { "Brawn" }, @"brawn", @"\bbgp ?\d*\b", 2009, 2009),
        new(new[] { "MF1" }, @"midland", @"\bm16\b", 2006, 2006),
        new(new[] { "Spyker" }, @"spyker", @"\bf8[ -]?vii\b", 2006, 2007),
        new(new[] { "Caterham" }, @"caterham", @"\bct0[1-5]\b", 2012, 2014),
        new(new[] { "Marussia", "Manor Marussia" }, @"marussia|\bmanor\b", @"\bmr0[1-5]\b", 2012, 2016),
        new(new[] { "Virgin" }, @"virgin", null, 2010, 2011),
        new(new[] { "HRT" }, @"\bhrt\b|hispania", null, 2010, 2012),
        new(new[] { "Alpine F1 Team" }, @"alpine", @"\ba5\d{2}\b", 2021, 2030),
        new(new[] { "Haas F1 Team" }, @"\bhaas\b", @"\bvf ?[-_]?\d{2}\b", 2016, 2030),
        new(new[] { "Jordan" }, @"jordan", @"\bej ?\d{2}\b", 1991, 2005),
        new(new[] { "Jaguar" }, @"jaguar", @"\br[1-5]\b", 2000, 2004),
        new(new[] { "Minardi" }, @"minardi", @"\bps0[1-5]\b|\bm19[0-8]\b|\bm0[12]\b", 1985, 2005),
        new(new[] { "BAR" }, @"\bbar\b|british american", null, 1999, 2005),
        new(new[] { "Arrows" }, @"arrows", @"\ba(1[0-9]|2[0-3])\b", 1978, 2002),
        new(new[] { "Prost" }, @"\bprost\b", @"\bap0[1-4]\b", 1997, 2001),
        new(new[] { "Benetton" }, @"benetton", @"\bb19\d\b|\bb200\b", 1986, 2001),
        new(new[] { "Stewart" }, @"\bstewart\b", @"\bsf ?[1-3]\b", 1997, 1999),
        new(new[] { "Ligier" }, @"ligier", @"\bjs ?[1-4]\d\b", 1976, 1996),
        new(new[] { "Tyrrell" }, @"tyrrell", @"\b0(0\d|1\d|2[0-6])\b", 1970, 1998),
        new(new[] { "Forti" }, @"\bforti\b", null, 1995, 1996),

        // Classic constructors (result names are matched as part of e.g. "Cooper-Climax", "Lotus-Ford", "Brabham-BMW").
        new(new[] { "Alfa Romeo" }, @"alfa ?romeo|alfetta", null, 1950, 1985),
        new(new[] { "Maserati" }, @"maserati", @"\b250 ?f\b", 1950, 1960),
        new(new[] { "Mercedes" }, @"mercedes", @"\bw196\b", 1954, 1955),
        new(new[] { "Lancia" }, @"\blancia\b", @"\bd50\b", 1954, 1955),
        new(new[] { "Gordini" }, @"gordini", null, 1950, 1956),
        new(new[] { "Connaught" }, @"connaught", null, 1952, 1959),
        new(new[] { "Vanwall" }, @"vanwall", @"\bvw ?\d\b", 1954, 1960),
        new(new[] { "Aston Martin" }, @"aston ?martin", @"\bdbr ?[45]\b", 1959, 1960),
        new(new[] { "Cooper" }, @"\bcooper\b", null, 1950, 1969),
        new(new[] { "BRM" }, @"\bbrm\b", null, 1951, 1977),
        new(new[] { "Scarab" }, @"scarab", null, 1960, 1960),
        new(new[] { "Lotus" }, @"lotus", null, 1958, 1994),
        new(new[] { "Lola" }, @"\blola\b", null, 1962, 1997),
        new(new[] { "Porsche" }, @"porsche", @"\b718\b|\b787\b|\b804\b", 1957, 1964),
        new(new[] { "Brabham" }, @"brabham", @"\bbt ?\d{1,2}[a-z]?\b", 1962, 1992),
        new(new[] { "Honda" }, @"honda", @"\bra ?\d{3}\b", 1964, 1968),
        new(new[] { "Eagle" }, @"\beagle\b", null, 1966, 1968),
        new(new[] { "Matra" }, @"matra", @"\bms ?\d{2}\b", 1967, 1972),
        new(new[] { "March" }, @"\bmarch\b", null, 1970, 1992),
        new(new[] { "Surtees" }, @"surtees", @"\bts ?\d{1,2}\b", 1970, 1978),
        new(new[] { "Shadow" }, @"\bshadow\b", @"\bdn ?\d{1,2}\b", 1973, 1980),
        new(new[] { "Hesketh" }, @"hesketh", null, 1974, 1978),
        new(new[] { "Wolf" }, @"\bwolf\b", @"\bwr ?\d\b", 1977, 1979),
        new(new[] { "Fittipaldi" }, @"fittipaldi|copersucar", null, 1975, 1982),
        new(new[] { "Ensign" }, @"\bensign\b", null, 1973, 1982),
        new(new[] { "Renault" }, @"renault", @"\bre ?\d{2}\b|\brs ?0?\d{1,2}\b", 1977, 1985),
        new(new[] { "Toleman" }, @"toleman", @"\btg ?\d{3}\b", 1981, 1985),
        new(new[] { "Zakspeed" }, @"zakspeed", null, 1985, 1989),
        new(new[] { "Dallara" }, @"dallara|scuderia italia", null, 1988, 1992),
        new(new[] { "Leyton House" }, @"leyton ?house", null, 1990, 1991),
        new(new[] { "Footwork" }, @"footwork", @"\bfa ?1\d\b", 1991, 1996),
        new(new[] { "Larrousse" }, @"larrousse|venturi", null, 1987, 1994),
        new(new[] { "Talbot-Lago" }, @"talbot", null, 1950, 1951),
        new(new[] { "Iso Marlboro" }, @"\biso\b", null, 1973, 1974),
        new(new[] { "Embassy Hill" }, @"embassy|graham ?hill", null, 1975, 1975),
        new(new[] { "Parnelli" }, @"parnelli", null, 1974, 1976),
        new(new[] { "Penske" }, @"penske", @"\bpc ?\d\b", 1974, 1977),
        new(new[] { "ATS" }, @"\bats\b", @"\bd[1-7]\b|\bhs ?1\b", 1977, 1984),
        new(new[] { "Theodore" }, @"theodore", @"\bty ?0[12]\b", 1977, 1983),
        new(new[] { "Osella" }, @"osella", @"\bfa ?1[a-m]\b", 1980, 1990),
        new(new[] { "Spirit" }, @"\bspirit\b", null, 1983, 1985),
        new(new[] { "RAM" }, @"\bram\b", null, 1983, 1985),
        new(new[] { "AGS" }, @"\bags\b", @"\bjh ?2\d[a-z]?\b", 1986, 1991),
        new(new[] { "Coloni" }, @"coloni", null, 1987, 1991),
        new(new[] { "Rial" }, @"\brial\b", null, 1988, 1989),
        new(new[] { "Euro Brun" }, @"euro ?brun", @"\ber ?18\d[a-z]?\b", 1988, 1990),
        new(new[] { "Onyx" }, @"\bonyx\b", null, 1989, 1990),
        new(new[] { "Fondmetal" }, @"fondmetal|fomet", null, 1991, 1992),
        new(new[] { "Lambo" }, @"lambo|modena", null, 1991, 1991),
        new(new[] { "Andrea Moda" }, @"andrea ?moda", null, 1992, 1992),
        new(new[] { "Simtek" }, @"simtek", null, 1994, 1995),
        new(new[] { "Pacific" }, @"\bpacific\b", null, 1994, 1995),
    };

    /// <summary>Result-API team names for this car, or empty when it can't be identified with confidence.</summary>
    public static IReadOnlyList<string> Identify(string name, string folder, string? brand, int year)
    {
        var text = " " + $"{name} {brand} {folder}".ToLowerInvariant().Replace('_', ' ').Replace('-', ' ') + " ";

        // 1. A team name in the name/brand/folder.
        foreach (var t in Teams)
            if (year >= t.From && year <= t.To && Regex.IsMatch(text, t.NamePattern))
                return t.ResultNames;

        // 2. A chassis code, only if exactly one team's code matches for this season.
        var byChassis = Teams.Where(t => t.ChassisPattern != null && year >= t.From && year <= t.To
                                          && Regex.IsMatch(text, t.ChassisPattern)).ToList();
        return byChassis.Count == 1 ? byChassis[0].ResultNames : Array.Empty<string>();
    }
}
