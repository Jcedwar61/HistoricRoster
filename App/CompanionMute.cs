using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using HistoricRosterPlugin;

namespace HistoricRosterApp;

/// <summary>
/// While a watched race runs, the sim companion programs that talk to or beep at a driver (CrewChief, SimHub, Simulator
/// Controller's voices ...) are muted in Windows' volume mixer (their volume set to 0): a watched race has no driver. SimCast and SimCoach keep
/// their sound (commentary and the weather link belong to watching). Assetto Corsa's own sound and anything else (music,
/// voice chat) are never touched. Everything muted is unmuted when Assetto Corsa closes; a list of what's muted is kept
/// on disk, so a crash of this program is put right the next time it starts.
/// </summary>
public static class CompanionMute
{
    // Executable names (without .exe), lower case.
    private static readonly string[] Mute =
    {
        "crewchiefv4", "simhubwpf", "simhub", "racelab", "simulator controller", "voice server", "race spotter",
        "race engineer", "race strategist", "secondmonitor", "z1dashboard",
    };
    private static readonly string[] Keep = { "simcast", "simcoach", "acs", "acs_x86" };

    private static string StatePath => AppData.PathOf("muted_companions.json");
    private static readonly object Gate = new();
    private static CancellationTokenSource? _watch;

    /// <summary>Unmutes what a previous run left muted (it crashed or was closed during a watched race).</summary>
    public static void RestoreLeftovers()
    {
        try
        {
            var left = AppData.Load<Dictionary<string, float[]>>(StatePath);
            if (left is { Count: > 0 }) Restore(new Dictionary<string, float[]>(left, StringComparer.OrdinalIgnoreCase));
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch { /* best effort */ }
    }

    /// <summary>Starts muting companions until Assetto Corsa (started now or within a few minutes) closes.</summary>
    public static void DuringWatchedRace(Action<string> log)
    {
        lock (Gate)
        {
            _watch?.Cancel();
            _watch = new CancellationTokenSource();
        }
        var token = _watch.Token;
        _ = Task.Run(async () =>
        {
            var muted = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Wait for the game (Content Manager takes a while to start it).
                var waited = TimeSpan.Zero;
                while (!AcLauncher.AcRunning && waited < TimeSpan.FromMinutes(4) && !token.IsCancellationRequested)
                {
                    await Task.Delay(2000, token);
                    waited += TimeSpan.FromSeconds(2);
                }
                // While it runs, mute companions (also ones started later).
                while (AcLauncher.AcRunning && !token.IsCancellationRequested)
                {
                    if (MuteCompanions(muted))
                    {
                        AppData.Save(StatePath, muted);
                        log("Watched race: muted " + string.Join(", ", muted.Keys) + " until Assetto Corsa closes (SimCast and SimCoach keep their sound).");
                    }
                    await Task.Delay(5000, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log("Couldn't mute the companion apps: " + ex.Message); }
            finally
            {
                if (muted.Count > 0)
                {
                    try { Restore(muted); File.Delete(StatePath); } catch { /* RestoreLeftovers does it next time */ }
                    log("Unmuted " + string.Join(", ", muted.Keys) + ".");
                }
            }
        }, token);
    }

    /// <summary>Stops muting now and unmutes (the program is closing).</summary>
    public static void Stop() { lock (Gate) _watch?.Cancel(); }

    /// <summary>
    /// Mutes every companion's audio sessions on every active playback device (channel volumes to 0; Windows 11 doesn't
    /// give other processes' sessions the simple mute control). New ones go into <paramref name="muted"/> with the levels
    /// they had. Returns true when something new was muted.
    /// </summary>
    private static bool MuteCompanions(Dictionary<string, float[]> muted)
    {
        var any = false;
        foreach (var (name, volume) in Sessions())
        {
            if (!Mute.Contains(name) || Keep.Any(k => name.StartsWith(k))) continue;
            var levels = Levels(volume);
            if (levels.All(l => l < 0.001f)) continue; // already silent (by us, or by the user)
            if (!muted.ContainsKey(name)) { muted[name] = levels; any = true; }
            var ctx = Guid.Empty;
            volume.SetAllVolumes((uint)levels.Length, new float[levels.Length], ref ctx);
        }
        return any;
    }

    /// <summary>Puts the levels back on those processes' sessions (full volume when the saved ones don't fit).</summary>
    private static void Restore(Dictionary<string, float[]> muted)
    {
        foreach (var (name, volume) in Sessions())
        {
            if (!muted.TryGetValue(name, out var saved)) continue;
            var count = Levels(volume).Length;
            var levels = saved.Length == count ? saved : Enumerable.Repeat(1f, count).ToArray();
            var ctx = Guid.Empty;
            volume.SetAllVolumes((uint)count, levels, ref ctx);
        }
    }

    private static float[] Levels(IChannelAudioVolume volume)
    {
        volume.GetChannelCount(out var count);
        var levels = new float[count];
        if (count > 0) volume.GetAllVolumes(count, levels);
        return levels;
    }

    /// <summary>Every audio session of another process on every active playback device: (process name, volume).</summary>
    private static IEnumerable<(string Name, IChannelAudioVolume Volume)> Sessions()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out var devices));
        devices.GetCount(out var count);
        for (uint d = 0; d < count; d++)
        {
            devices.Item(d, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var obj) != 0) continue;
            if (((IAudioSessionManager2)obj).GetSessionEnumerator(out var sessions) != 0) continue;
            sessions.GetCount(out var n);
            for (var i = 0; i < n; i++)
            {
                sessions.GetSession(i, out var session);
                session.GetProcessId(out var pid);
                if (pid == 0 || pid == Environment.ProcessId || session is not IChannelAudioVolume volume) continue;
                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
                catch { continue; }
                yield return (name, volume);
            }
        }
    }

    // ---------------------------------------------------------------- Windows Core Audio (just what's used here)

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl (not used here, but they come first in the vtable).
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2.
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolume(uint index, out float level);
        [PreserveSig] int SetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray)] float[] levels, ref Guid ctx);
        [PreserveSig] int GetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray), Out] float[] levels);
    }
}
