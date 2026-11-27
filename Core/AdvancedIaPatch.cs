namespace HistoricRosterPlugin;

/// <summary>
/// A small patch to the AdvancedIA CSP app (MIT, by Vincent Chevalier) so it works with a watched race. AdvancedIA drives
/// the AI cars and applies the racing rules and AI penalties as usual; only in a watched Historic Roster race
/// ([HISTORIC_ROSTER] WATCH=1 in race.ini, for the race that's loaded) it:
/// - never retires or disqualifies a car: cars finish or retire as they did in real life, which the in-game app steers,
///   and never sends a stuck car to its pit box (the in-game app puts it back on track, out of shot);
/// - never penalises the user's car (CAR_0): the autopilot drives it in place of the real driver, can't serve a
///   penalty (a drive-through would need AC's pit menu) and would be disqualified for it;
/// - shows no on-screen panel, flags, messages or beeps for the user's car: a watched race has no driver;
/// - leaves each car's pace (AI level, grip, throttle) to the in-game app's real-result steering.
/// Each change is an exact text replacement, applied once (the originals are kept as *.hr-orig). When AdvancedIA's code
/// has changed (an update), nothing is patched and the result says so.
/// </summary>
public static class AdvancedIaPatch
{
    private const string Require = "local HR=require('src/historicroster') -- Historic Roster: watched races";

    private const string HelperLua = """
        -- Historic Roster: is this a watched race (race.ini [HISTORIC_ROSTER] WATCH=1, written for the race that's
        -- loaded: same track and car count)? Then cars finish or retire as they did in real life, so AdvancedIA never
        -- retires or disqualifies one, and never penalises the user's car (CAR_0), which the autopilot drives in place
        -- of the real driver. Added by the Historic Roster program; remove this file and restore the *.hr-orig files
        -- to undo.
        local M = {}
        local checked, watched = false, false
        function M.watched()
          if checked then return watched end
          checked = true
          pcall(function()
            local sim = ac.getSim()
            if sim.isOnlineRace then return end
            local cfg = ac.INIConfig.raceConfig()
            local function get(key) local v = cfg:get('HISTORIC_ROSTER', key, '') return type(v) == 'string' and v or '' end
            if tonumber(get('WATCH')) ~= 1 or tonumber(get('CARS')) ~= sim.carsCount then return end
            local track = get('TRACK'):lower()
            if track == '' or (ac.getTrackID and tostring(ac.getTrackID()):lower() ~= track) then return end
            watched = true
          end)
          return watched
        end
        return M

        """;

    private static readonly (string File, string Old, string New)[] Edits =
    {
        ("src/penalties.lua", "if not p.playerPenalties or C.solo(profile) then",
                          "if not p.playerPenalties or HR.watched() or C.solo(profile) then"),
        ("src/penalties.lua", "local ready=p.playerPenalties and not C.solo(profile)",
                          "local ready=p.playerPenalties and not HR.watched() and not C.solo(profile)"),
        ("src/penalties.lua", "if s.count[i]>p.penaltyDisqualify then s.dq[i]=true",
                          "if s.count[i]>p.penaltyDisqualify and not HR.watched() then s.dq[i]=true"),
        ("src/sanctions.lua", " if c.dq then return end\r\n c.dq=true;",
                          " if c.dq or HR.watched() then return end\r\n c.dq=true;"),
        ("src/sanctions.lua", "elseif a.player and a.valid and p.sanPlayerTrackLimits and not a.finished and not c.dq then",
                          "elseif a.player and a.valid and p.sanPlayerTrackLimits and not HR.watched() and not a.finished and not c.dq then"),
        ("src/engine.lua", "if K.recoverRetire and e.profile=='C' and cmd.recover",
                       "if K.recoverRetire and not HR.watched() and e.profile=='C' and cmd.recover"),
        // The driving belongs to the in-game app (Historic Roster's own race-craft and real-result steering): each car's
        // pace (AI level, grip, throttle), its line, caution and aggression, brake help and induced mistakes. AdvancedIA
        // keeps the rules: flags, speed limits under yellow and full-course yellow, penalties.
        ("src/applier.lua", "local function write(name,index,value,extra)\r\n if name=='turnSignals' then",
                            "local function write(name,index,value,extra)\r\n if HR.watched() and (name=='level' or name=='grip' or name=='throttle' or name=='offset' or name=='caution' or name=='aggression' or name=='brake' or name=='brakeMistake' or name=='tractionMistake') then return true end\r\n if name=='turnSignals' then"),
        // A stuck car isn't sent to its pit box (a lap lost): the in-game app puts it back on track, out of shot.
        ("src/engine.lua", "cmd.recover=not a.pit and (red and still>=K.redRecoverTime",
                           "cmd.recover=not HR.watched() and not a.pit and (red and still>=K.redRecoverTime"),
        ("src/engine.lua", "if wedged and not a.pit then cmd.recover=true end",
                           "if wedged and not a.pit and not HR.watched() then cmd.recover=true end"),
        // A watched race has no driver: no on-screen panel, flags or messages for the user's car.
        ("AdvancedIA.lua", "function script.HUMANAI__HUD()\r\n if released then return end",
                           "function script.HUMANAI__HUD()\r\n if released or HR.watched() then return end"),
        ("src/applier.lua", "function M.message(title,text) if callable(ac.setMessage)",
                            "function M.message(title,text) if HR.watched() then return false end if callable(ac.setMessage)"),
        ("src/applier.lua", "if name=='flag' then return pcall(f,value) end",
                            "if name=='flag' then if HR.watched() then return true end return pcall(f,value) end"),
        // ... and no DRS or delta beeps for it.
        ("src/applier.lua", " if frame.online or frame.replay or frame.paused or not ac.AudioEvent or not callable(ac.dirname) then return end",
                            " if HR.watched() or frame.online or frame.replay or frame.paused or not ac.AudioEvent or not callable(ac.dirname) then return end"),
        ("src/applier.lua", " if frame.online or frame.replay or frame.paused or a.deltaFailed or not ac.AudioEvent or not callable(ac.dirname) then return end",
                            " if HR.watched() or frame.online or frame.replay or frame.paused or a.deltaFailed or not ac.AudioEvent or not callable(ac.dirname) then return end"),
    };

    // Edits an earlier version of the program applied, and what replaces them now.
    private static readonly (string File, string Was, string Now)[] Upgrades =
    {
        ("src/applier.lua", " if HR.watched() and (name=='level' or name=='grip' or name=='throttle') then return true end",
                            " if HR.watched() and (name=='level' or name=='grip' or name=='throttle' or name=='offset' or name=='caution' or name=='aggression' or name=='brake' or name=='brakeMistake' or name=='tractionMistake') then return true end"),
    };

    public static string Folder(string acRoot) => Path.Combine(acRoot, "apps", "lua", "AdvancedIA");

    /// <summary>Patches AdvancedIA when it's installed. Returns what was done, or null when there was nothing to do.</summary>
    public static string? Apply(string acRoot)
    {
        var src = Folder(acRoot);
        if (!Directory.Exists(Path.Combine(src, "src"))) return null;

        // All or nothing: every edit must find its original text (or already be applied) before anything is written.
        var texts = new Dictionary<string, string>();
        var pending = new List<(string File, string Old, string New)>();
        foreach (var edit in Edits)
        {
            var path = Path.Combine(src, edit.File);
            if (!File.Exists(path)) return $"AdvancedIA not patched: {edit.File} is missing (a version this program doesn't know).";
            if (!texts.TryGetValue(edit.File, out var text))
            {
                text = File.ReadAllText(path);
                // A file an earlier version patched: bring its edits up to date first.
                foreach (var up in Upgrades.Where(u => u.File == edit.File))
                    if (Match(text, up.Was) is { } was && !text.Contains(up.Now)) text = text.Replace(was, up.Now);
                texts[edit.File] = text;
            }
            var old = Match(text, edit.Old);
            if (old == null && Match(text, edit.New) != null) continue; // already applied
            if (old == null || Count(text, old) != 1)
                return $"AdvancedIA not patched: {edit.File} isn't the version this program knows (an AdvancedIA update?).";
            pending.Add(edit);
        }
        var helper = Path.Combine(src, "src", "historicroster.lua");
        var helperCurrent = File.Exists(helper) && File.ReadAllText(helper) == HelperLua;
        var requireMissing = texts.Keys.Where(f => !texts[f].Contains("require('src/historicroster')")).ToList();
        var upgraded = texts.Any(kv => kv.Value != File.ReadAllText(Path.Combine(src, kv.Key)));
        if (pending.Count == 0 && helperCurrent && requireMissing.Count == 0 && !upgraded) return null;

        if (!helperCurrent) File.WriteAllText(helper, HelperLua);
        foreach (var file in texts.Keys)
        {
            var path = Path.Combine(src, file);
            var text = texts[file];
            var original = File.ReadAllText(path); // (as on disk: an upgrade alone is a change too)
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            if (!text.Contains("require('src/historicroster')")) text = Require + nl + text;
            foreach (var edit in pending.Where(e => e.File == file))
            {
                var old = Match(text, edit.Old)!;
                text = text.Replace(old, Adapt(edit.New, nl));
            }
            if (text == original) continue;
            if (!File.Exists(path + ".hr-orig")) File.Copy(path, path + ".hr-orig");
            File.WriteAllText(path, text);
        }
        return "AdvancedIA patched for watched races (no retirements, disqualifications, penalties, alerts or beeps for your car there).";
    }

    // The edits are written with CRLF where they span lines; a file with LF line endings gets LF.
    private static string? Match(string text, string s)
    {
        if (text.Contains(s)) return s;
        var lf = s.Replace("\r\n", "\n");
        return lf != s && text.Contains(lf) ? lf : null;
    }

    private static string Adapt(string s, string nl) => nl == "\n" ? s.Replace("\r\n", "\n") : s;

    private static int Count(string text, string s)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(s, at, StringComparison.Ordinal)) >= 0) { n++; at += s.Length; }
        return n;
    }
}
