using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InstantReplay;

public sealed record GameRule(string Name, string Executable, string Keyword = "")
{
    public string LowerName => Name.ToLowerInvariant();
    public string Description => string.IsNullOrWhiteSpace(Keyword)
        ? Executable.ToLowerInvariant()
        : $"{Executable.ToLowerInvariant()} · title contains {Keyword.ToLowerInvariant()}";
}

internal readonly record struct CaptureSurface(int Width, int Height, long Style, IntPtr Monitor, int DisplayWidth, int DisplayHeight);
internal sealed record CaptureWindow(IntPtr Handle, uint ProcessId, string Executable, string Title, string Game)
{
    public CaptureSurface Surface { get; init; }
}

public sealed record AudioAppCandidate(string Executable, string DisplayName, bool IsAudioActive);

internal sealed class GameDetector
{
    private sealed record Catalog(FrozenDictionary<string, GameRule[]> Rules, int Count);
    private static readonly Lazy<Catalog> SharedCatalog = new(LoadCatalog);
    private readonly FrozenDictionary<string, GameRule[]> _custom;
    private readonly Dictionary<uint, (string Executable, long Expiry)> _processCache = [];
    public int CatalogCount => SharedCatalog.Value.Count;

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dwm.exe", "sihost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "TextInputHost.exe", "ApplicationFrameHost.exe",
        "InstantReplay.exe", "replay.exe",
        "steam.exe", "EpicGamesLauncher.exe", "Battle.net.exe", "RiotClientServices.exe", "upc.exe",
        "msedgewebview2.exe"
    };

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "java.exe", "javaw.exe", "game.exe", "launcher.exe", "client.exe", "main.exe", "application.exe", "python.exe", "pythonw.exe"
    };

    private static readonly HashSet<string> MinecraftGameClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LWJGL", "GLFW30", "SDL_app"
    };

    public GameDetector(IEnumerable<GameRule> custom)
    {
        _custom = custom.GroupBy(r => Path.GetFileName(r.Executable), StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        _ = SharedCatalog.Value;
    }

    private static Catalog LoadCatalog()
    {
        try
        {
            string catalogPath = Path.Combine(AppContext.BaseDirectory, "assets", "games.json");
            if (!File.Exists(catalogPath))
                return new Catalog(new Dictionary<string, GameRule[]>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), 0);

            var rules = JsonSerializer.Deserialize<GameRule[]>(File.ReadAllText(catalogPath)) ?? [];
            var index = rules.GroupBy(r => Path.GetFileName(r.Executable), StringComparer.OrdinalIgnoreCase)
                .ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            return new Catalog(index, rules.Select(r => r.Name).Distinct().Count());
        }
        catch (Exception ex)
        {
            Logger.Warn($"game catalog could not be loaded: {ex.Message}");
        }
        return new Catalog(new Dictionary<string, GameRule[]>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), 0);
    }

    public static string SanitizeCategoryFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "desktop";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        bool prevSpace = false;
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (Array.IndexOf(invalid, c) >= 0 || char.IsControl(c))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        string clean = sb.ToString().Trim(' ', '.');
        if (clean.Length == 0 || clean is "." or "..") return "desktop";
        return clean;
    }

    public static bool IsSelfForeground()
    {
        IntPtr window = GetAncestor(GetForegroundWindow(), 2);
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid == Environment.ProcessId;
    }

    public static bool IsAlive(CaptureWindow target)
    {
        if (!IsWindow(target.Handle)) return false;
        GetWindowThreadProcessId(target.Handle, out uint pid);
        return pid == target.ProcessId;
    }

    public static bool IsMinimized(CaptureWindow target) => IsIconic(target.Handle);

    public static bool IsForeground(CaptureWindow target) =>
        GetAncestor(GetForegroundWindow(), 2) == target.Handle && IsAlive(target);

    public static List<AudioAppCandidate> EnumerateAudioApps()
    {
        var byExe = new Dictionary<string, AudioAppCandidate>(StringComparer.OrdinalIgnoreCase);

        static bool IsNoiseExe(string exe) =>
            string.IsNullOrWhiteSpace(exe)
            || Excluded.Contains(exe)
            || exe.Equals("audiodg.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("System", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("RuntimeBroker.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("backgroundTaskHost.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("taskhostw.exe", StringComparison.OrdinalIgnoreCase);

        // 1. Query active/recent WASAPI render audio sessions so audio-producing apps surface first.
        try
        {
            foreach (uint pid in AudioSessionInspector.GetRenderSessionPids())
            {
                if (pid == 0 || pid == Environment.ProcessId) continue;
                string? exe = QueryExeForPid(pid);
                if (exe == null || IsNoiseExe(exe)) continue;
                string key = exe.ToLowerInvariant();
                if (!byExe.ContainsKey(key))
                {
                    string display = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    byExe[key] = new AudioAppCandidate(key, display, true);
                }
            }
        }
        catch { }

        // 2. Also include visible user applications (e.g. games or Discord idle before voice/sound).
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == Environment.ProcessId || proc.MainWindowHandle == IntPtr.Zero) continue;
                    string? exe = QueryExeForPid((uint)proc.Id) ?? (proc.ProcessName + ".exe");
                    if (string.IsNullOrWhiteSpace(exe) || IsNoiseExe(exe)) continue;
                    string key = Path.GetFileName(exe).ToLowerInvariant();
                    if (!key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) key += ".exe";
                    if (!byExe.ContainsKey(key))
                    {
                        string display = Path.GetFileNameWithoutExtension(key).ToLowerInvariant();
                        byExe[key] = new AudioAppCandidate(key, display, false);
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

        return byExe.Values
            .OrderByDescending(a => a.IsAudioActive)
            .ThenBy(a => a.Executable, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? QueryExeForPid(uint pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length)) return null;
            return Path.GetFileName(path.ToString());
        }
        finally { CloseHandle(process); }
    }

    private string? Executable(uint pid)
    {
        long now = Environment.TickCount64;
        if (_processCache.TryGetValue(pid, out var cached) && cached.Expiry > now) return cached.Executable;
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length)) return null;
            string executable = Path.GetFileName(path.ToString());
            if (_processCache.Count >= 64) _processCache.Clear();
            _processCache[pid] = (executable, now + 5000);
            return executable;
        }
        finally { CloseHandle(process); }
    }

    internal static bool Matches(GameRule rule, string executable, string title, bool builtIn)
    {
        if (!Path.GetFileName(rule.Executable).Equals(executable, StringComparison.OrdinalIgnoreCase)) return false;
        string keyword = rule.Keyword.Trim();
        if (builtIn && Generic.Contains(executable) && keyword.Length == 0) keyword = rule.Name;
        return keyword.Length == 0 || title.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesGameSurface(GameRule rule, string executable, string windowClass) =>
        !((executable.Equals("java.exe", StringComparison.OrdinalIgnoreCase) || executable.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            && rule.Name.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase)) || MinecraftGameClasses.Contains(windowClass);

    internal GameRule? ListedRule(string executable, string title, string? windowClass = null)
    {
        var rule = _custom.TryGetValue(executable, out var custom) ? custom.FirstOrDefault(r => Matches(r, executable, title, false)) : null;
        if (rule == null && SharedCatalog.Value.Rules.TryGetValue(executable, out var candidates))
            rule = candidates.FirstOrDefault(r => Matches(r, executable, title, true)
                && (windowClass is null || MatchesGameSurface(r, executable, windowClass)));
        return rule;
    }

    public CaptureWindow? Foreground()
    {
        IntPtr previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            IntPtr window = GetAncestor(GetForegroundWindow(), 2);
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return null;
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            long style = GetWindowLongPtrW(window, -20).ToInt64();
            if ((style & 0x80) != 0) return null;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
            var text = new StringBuilder(512);
            GetWindowTextW(window, text, text.Capacity);
            string title = text.ToString().Trim();
            if (title.Length == 0 || title == "Program Manager" || title == "Windows Input Experience") return null;
            if (!GetClientRect(window, out var rect) || rect.Right - rect.Left < 2 || rect.Bottom - rect.Top < 2) return null;
            string? executable = Executable(pid);
            if (executable == null) return null;
            if (Excluded.Contains(executable)) return null;
            var className = new StringBuilder(256);
            GetClassNameW(window, className, className.Capacity);
            var rule = ListedRule(executable, title, className.ToString());
            if (rule == null) return null;
            IntPtr monitor = MonitorFromWindow(window, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info)) return null;
            var target = new CaptureWindow(window, pid, executable, title, rule.Name)
            {
                Surface = new CaptureSurface(rect.Right - rect.Left, rect.Bottom - rect.Top,
                    GetWindowLongPtrW(window, -16).ToInt64() & 0x80C40000L, monitor,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top)
            };
            return IsForeground(target) ? target : null;
        }
        catch (Exception ex) { Logger.Debug($"foreground detection: {ex.Message}"); return null; }
        finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private static class AudioSessionInspector
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);
            int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int sessionCount);
            int GetSession(int sessionIndex, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out int state);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            int GetState(out int state);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingId);
            int SetGroupingParam(ref Guid @override, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
            int GetProcessId(out uint retVal);
        }

        public static List<uint> GetRenderSessionPids()
        {
            var pids = new List<uint>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device == null)
                return pids;

            Guid mgrIid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref mgrIid, 23, IntPtr.Zero, out object mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr)
                return pids;

            if (mgr.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum == null)
                return pids;

            if (sessionEnum.GetCount(out int count) != 0) return pids;
            for (int i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out var ctl) != 0 || ctl is not IAudioSessionControl2 ctl2)
                    continue;
                if (ctl2.GetState(out int state) == 0 && state != 2 && ctl2.GetProcessId(out uint pid) == 0 && pid != 0)
                    pids.Add(pid);
            }
            return pids;
        }
    }
}
