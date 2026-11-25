using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InstantReplay;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

public sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
{
    public event Action? DevicesChanged;
    private IMMDeviceEnumerator? _enumerator;
    private bool _registered;

    public AudioDeviceWatcher()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"audio device watcher: falling back to polling ({ex.Message})");
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, int newState) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceAdded(string deviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDefaultDeviceChanged(int flow, int role, string deviceId)
    {
        if (flow == 1) DevicesChanged?.Invoke();
    }
    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        try
        {
            if (_registered && _enumerator != null)
                _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Logger.Warn($"audio device watcher dispose failed: {ex.Message}");
        }
        if (_enumerator != null && Marshal.IsComObject(_enumerator))
            Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }
}

public static class WindowsTheme
{
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int lightMode)
                return lightMode == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"could not read system theme, defaulting to dark: {ex.Message}");
        }
        return true;
    }
}

public sealed class Settings
{
    public string Microphone { get; set; } = "";
    public int ReplayDurationSeconds { get; set; } = 60;
    public int FrameRate { get; set; } = 60;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; } = true;
    public bool DebugLogging { get; set; } = false;
    public bool DarkMode { get; set; } = true;

    // Default hotkey: shift + s
    public uint HotkeyModifiers { get; set; } = 0x0004;
    public uint HotkeyVk { get; set; } = 0x53;

    public string QualityPreset { get; set; } = "Balanced";
    public string EncoderChoice { get; set; } = "Auto";
    public string CaptureMethod { get; set; } = "Wgc";
    public string CaptureTarget { get; set; } = "Display";
    public List<GameRule> SpecificWindows { get; set; } = [];
    public int CaptureMonitorIndex { get; set; } = -1;
    public bool CaptureClientArea { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
    public bool CaptureBorder { get; set; } = false;
    public bool CaptureRoundedCorners { get; set; } = true;
    public bool CaptureSecondaryWindows { get; set; } = false;

    public string SaveDestination { get; set; } = "";
    public bool AppMinimizedNotificationShown { get; set; } = false;
    public string BackgroundNotice { get; set; } = "Once";
    public bool AutoCheckUpdates { get; set; } = true;

    public string GetEffectiveSaveDestination() =>
        string.IsNullOrWhiteSpace(SaveDestination)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "replays")
            : SaveDestination;

    private static string PathOnDisk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ReplayBuffer", "settings.json");

    public static Settings Load()
    {
        try
        {
            string path = PathOnDisk;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (loaded != null)
                {
                    if (loaded.CaptureTarget is "Window" or "ActiveWindow") loaded.CaptureTarget = "SpecificWindows";
                    loaded.SpecificWindows ??= [];
                    if (loaded.FrameRate is not (30 or 60 or 120)) loaded.FrameRate = 60;
                    if (loaded.ReplayDurationSeconds is not (30 or 60 or 120 or 180 or 240 or 300)) loaded.ReplayDurationSeconds = 60;
                    if (loaded.QualityPreset is not ("Low" or "Balanced" or "High" or "Quality" or "Ultra")) loaded.QualityPreset = "Balanced";
                    loaded.CaptureBorder = false;
                    return loaded;
                }
            }
        }
        catch { }
        return new Settings { DarkMode = WindowsTheme.IsSystemDarkMode() };
    }

    public void Save()
    {
        try
        {
            string path = PathOnDisk;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Error("settings.save failed", ex);
        }
    }

    public void SaveFields(params string[] names)
    {
        if (names.Length == 0) return;
        try
        {
            string path = PathOnDisk;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            Dictionary<string, JsonElement>? onDisk = null;
            if (File.Exists(path))
            {
                try { onDisk = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)); }
                catch (Exception ex) { Logger.Warn($"settings.savefields rewrite ({ex.Message})"); }
            }

            if (onDisk is null)
            {
                Save();
                return;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var doc = new Dictionary<string, JsonElement>(onDisk, StringComparer.Ordinal);
            foreach (string name in names)
            {
                PropertyInfo? property = typeof(Settings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is null || !property.CanRead) continue;
                doc[name] = JsonSerializer.SerializeToElement(property.GetValue(this), options);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(doc, options));
        }
        catch (Exception ex)
        {
            Logger.Error("settings.savefields failed", ex);
        }
    }
}

public static class Logger
{
    private static readonly object s_lock = new();
    private static StreamWriter? s_writer;
    private static bool s_debugMode;

    public static string LogPath { get; private set; } = "";

    public static void Init(bool debugMode)
    {
        lock (s_lock)
        {
            s_debugMode = debugMode;
            string fileName = $"replay_debug_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            LogPath = Path.Combine(Path.GetTempPath(), fileName);
            s_writer = new StreamWriter(LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        Info("application started");
        Info($"version: {typeof(Logger).Assembly.GetName().Version?.ToString(3)}");
    }

    public static void SetDebugMode(bool enabled) => s_debugMode = enabled;
    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("error", ex is null ? message : $"{message}\n{ex}");
    public static void Debug(string message) { if (s_debugMode) Write("debug", message); }

    private static void Write(string level, string message)
    {
        lock (s_lock)
        {
            s_writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
        }
    }

    public static void Shutdown()
    {
        lock (s_lock)
        {
            s_writer?.Flush();
            s_writer?.Dispose();
            s_writer = null;
        }
    }
}

public enum SaveReplayOutcome
{
    Success,
    NotRecording,
    NoSegments,
    WriteBlocked,
    MergeFailed,
    LowMemory,
}

public readonly struct SaveReplayResult
{
    public SaveReplayOutcome Outcome { get; }
    public string? Path { get; }
    public string Category { get; }
    public bool Success => Outcome == SaveReplayOutcome.Success;

    public SaveReplayResult(SaveReplayOutcome outcome, string? path = null, string category = "desktop")
    {
        Outcome = outcome;
        Path = path;
        Category = category;
    }

    public string UserMessage() => Outcome switch
    {
        SaveReplayOutcome.Success => $"replay saved to {Category}",
        SaveReplayOutcome.NotRecording => "recorder isn't running — start replay first.",
        SaveReplayOutcome.NoSegments => "still buffering — try again in a few seconds.",
        SaveReplayOutcome.WriteBlocked => "save blocked by windows controlled folder access.",
        SaveReplayOutcome.MergeFailed => "failed to save replay (see debug log for details).",
        SaveReplayOutcome.LowMemory => "replay save skipped: windows is low on committed memory.",
        _ => "failed to save replay (see debug log for details).",
    };
}

public readonly record struct VideoQualityProfile(string Name, int BitrateKbps, int MaxWidth, int MaxHeight)
{
    public static VideoQualityProfile FromPreset(string preset, int fps)
    {
        var (name, at60Fps) = preset switch
        {
            "Ultra" => ("ultra", 18000),
            "Quality" or "High" => ("high", 9000),
            "Balanced" => ("balanced", 6000),
            _ => ("low", 4000)
        };
        int rate = Math.Clamp(fps, 15, 240);
        return new VideoQualityProfile(name, at60Fps * rate / 60, 3840, 2160);
    }
    public string Label => Name;
}

public sealed class RecorderEngine : IDisposable
{
    private volatile bool _armed;
    private bool _specific;
    private bool _paused;
    private GameDetector? _detector;
    private GameDetector _desktopDetector;
    private System.Threading.Timer? _targetTimer;
    private CaptureWindow? _target;
    private IntPtr _candidate;
    private DateTime _candidateSince;
    private CaptureSurface? _pendingSurface;
    private DateTime _surfaceSince;
    private DateTime _startedAt;
    private DateTime _retryAt;
    private string _targetState = "stopped";
    private (string Game, DateTime SeenUtc)? _lastDesktopGame;

    public bool IsPaused { get { lock (_opLock) return _armed && _specific && _paused; } }
    public string TargetState { get { lock (_opLock) return _targetState; } }
    public string ActiveGameName { get { lock (_opLock) return _specific ? (_target?.Game.ToLowerInvariant() ?? "") : ""; } }

    private readonly object _lock = new();
    private readonly object _opLock = new();
    private readonly Settings _settings;
    private Settings _captureSettings = new();
    private readonly ReplayEngine _engine = new();

    private long _lastHealthTicks = -1;
    private int _noProgressPolls;

    public RecorderEngine(Settings settings)
    {
        _settings = settings;
        _desktopDetector = new GameDetector(_settings.SpecificWindows);
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                _engine.GetStatus(out bool buffering, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                return buffering || _armed;
            }
        }
    }

    public void ReloadGameRules()
    {
        lock (_opLock)
        {
            _desktopDetector = new GameDetector(_settings.SpecificWindows);
            if (_specific) _detector = new GameDetector(_settings.SpecificWindows);
        }
    }

    public void TrackForegroundGame()
    {
        if (!Monitor.TryEnter(_opLock)) return;
        try
        {
            if (_specific) return;
            var fg = _desktopDetector.Foreground();
            if (fg != null)
                _lastDesktopGame = (fg.Game, DateTime.UtcNow);
            else if (!GameDetector.IsSelfForeground())
                _lastDesktopGame = null;
        }
        finally { Monitor.Exit(_opLock); }
    }

    public bool TryGetBufferStats(out long bufferBytes, out long bufferedMs) =>
        _engine.TryGetBufferStats(out bufferBytes, out bufferedMs);

    public bool IsBufferHealthy()
    {
        lock (_lock)
        {
            _engine.GetStatus(out bool buffering, out _, out _, out _, out _, out bool hasError,
                out _, out _, out _, out long healthTicks);
            if (!buffering)
            {
                _lastHealthTicks = -1;
                _noProgressPolls = 0;
                return false;
            }
            if (hasError) return false;

            if (_lastHealthTicks < 0)
            {
                _lastHealthTicks = healthTicks;
                return true;
            }

            if (healthTicks != _lastHealthTicks)
            {
                _lastHealthTicks = healthTicks;
                _noProgressPolls = 0;
                return true;
            }

            if (++_noProgressPolls >= 8)
            {
                _lastHealthTicks = -1;
                return false;
            }

            return true;
        }
    }

    public List<string> EnumerateMicrophones() => ReplayEngine.EnumerateMicrophones();
    public string GetDefaultMicrophoneName() => ReplayEngine.GetDefaultMicrophoneName();
    public bool ProbeMicState(string? wanted, bool detectSignal, out string activeName, out int peakPct) =>
        ReplayEngine.ProbeMicState(wanted, detectSignal, out activeName, out peakPct);

    public bool TryGetMicStatus(out long micFrames, out long micEvents, out long micGotFrames,
        out long micFailHr, out int sysPeak, out int micPeak, out string activeName) =>
        _engine.TryGetMicStatus(out micFrames, out micEvents, out micGotFrames,
            out micFailHr, out sysPeak, out micPeak, out activeName);

    private static EncoderPreference ResolveEncoder(string choice) => choice switch
    {
        "Hardware" => EncoderPreference.Hardware,
        "Software" => EncoderPreference.Software,
        _ => EncoderPreference.Auto,
    };

    private static CaptureMethod ResolveCaptureMethod(string choice) => choice switch
    {
        "Dxgi" => CaptureMethod.Dxgi,
        _ => CaptureMethod.Wgc,
    };

    public bool Start(string tempDir, bool wipeSegments = true)
    {
        lock (_opLock)
        {
            lock (_lock)
            {
                if (IsRunning)
                {
                    Logger.Warn("start() called while already running; ignoring.");
                    return true;
                }
            }

            _captureSettings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_settings))!;
            _specific = _captureSettings.CaptureTarget == "SpecificWindows";
            _desktopDetector = new GameDetector(_settings.SpecificWindows);
            _armed = true;
            if (_specific)
            {
                _detector = new GameDetector(_settings.SpecificWindows);
                _paused = true;
                _targetState = "waiting for game";
                _targetTimer = new System.Threading.Timer(_ => PollTarget(), null, 0, 200);
                return true;
            }
            bool ok = StartNative(IntPtr.Zero);
            _armed = ok;
            _targetState = ok ? "recording" : "error";
            return ok;
        }
    }

    private bool StartNative(IntPtr window)
    {
        var quality = VideoQualityProfile.FromPreset(_captureSettings.QualityPreset, _captureSettings.FrameRate);
        bool ok = _engine.StartBuffering(
            monitorIndex: _captureSettings.CaptureMonitorIndex,
            ringSeconds: Math.Max(_captureSettings.ReplayDurationSeconds, 1),
            fps: _captureSettings.FrameRate,
            bitrateKbps: quality.BitrateKbps,
            captureAudio: _captureSettings.RecordSystemAudio,
            preference: ResolveEncoder(_captureSettings.EncoderChoice),
            method: _specific ? CaptureMethod.Wgc : ResolveCaptureMethod(_captureSettings.CaptureMethod),
            captureMic: _captureSettings.RecordMicrophone,
            micDevice: _captureSettings.Microphone,
            targetWindow: window,
            captureOptions: (uint)((_captureSettings.CaptureClientArea ? 1 : 0) | (_captureSettings.CaptureCursor ? 2 : 0) | (!_specific && _captureSettings.CaptureRoundedCorners ? 8 : 0) | (_captureSettings.CaptureSecondaryWindows ? 16 : 0) | (_specific ? 64 : 0)),
            outputWidthCap: quality.MaxWidth, outputHeightCap: quality.MaxHeight);

        if (!ok)
        {
            Logger.Error("start() failed: native engine could not start buffering.");
            return false;
        }

        lock (_lock)
        {
            _lastHealthTicks = -1;
            _noProgressPolls = 0;
        }

        Logger.Info($"capture started - fps: {_captureSettings.FrameRate}, ring: {_captureSettings.ReplayDurationSeconds}s");
        return true;
    }

    private void PollTarget()
    {
        if (!Monitor.TryEnter(_opLock)) return;
        try
        {
            if (!_armed || !_specific || _detector == null) return;
            if (_target != null && !GameDetector.IsAlive(_target))
            {
                _engine.Shutdown();
                _target = null;
                _candidate = IntPtr.Zero;
                _pendingSurface = null;
                _paused = true;
                _retryAt = default;
                _targetState = "waiting for game";
                Logger.Info("captured window closed; waiting for eligible foreground game.");
            }
            var foreground = _detector.Foreground();
            if (foreground == null)
            {
                _candidate = IntPtr.Zero;
                _pendingSurface = null;
                if (!_paused) _engine.SetPaused(true);
                _paused = true;
                _targetState = _target == null ? "waiting for game" : "paused";
                return;
            }
            bool changedTarget = _target?.Handle != foreground.Handle || _target?.ProcessId != foreground.ProcessId;
            bool changedSurface = !changedTarget && _target!.Surface != foreground.Surface;
            if (changedSurface)
            {
                if (!_paused) _engine.SetPaused(true);
                _paused = true;
                _targetState = "restarting capture";
                if (_pendingSurface != foreground.Surface)
                {
                    _pendingSurface = foreground.Surface;
                    _surfaceSince = DateTime.UtcNow;
                    return;
                }
                if (DateTime.UtcNow - _surfaceSince < TimeSpan.FromMilliseconds(600)) return;
            }
            else _pendingSurface = null;
            if (changedTarget || changedSurface)
            {
                if (!_paused) _engine.SetPaused(true);
                _paused = true;
                if (changedTarget && _candidate != foreground.Handle)
                {
                    _candidate = foreground.Handle;
                    _candidateSince = DateTime.UtcNow;
                    return;
                }
                if ((changedTarget && DateTime.UtcNow - _candidateSince < TimeSpan.FromMilliseconds(250)) || DateTime.UtcNow < _retryAt) return;
                var verified = _detector.Foreground();
                if (verified?.Handle != foreground.Handle || verified.ProcessId != foreground.ProcessId || verified.Surface != foreground.Surface) return;
                _engine.StopBuffering();
                _target = foreground;
                if (!StartNative(foreground.Handle))
                {
                    _target = null;
                    _targetState = "capture unavailable";
                    _retryAt = DateTime.UtcNow.AddSeconds(5);
                    return;
                }
                _startedAt = DateTime.UtcNow;
                _pendingSurface = null;
                Logger.Info($"replay target changed to {foreground.Game.ToLowerInvariant()} ({foreground.Executable.ToLowerInvariant()})");
            }
            _engine.GetStatus(out bool running, out _, out _, out long frames, out _, out bool error, out _, out _, out _, out _);
            if (!running || error || (frames == 0 && DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(8)))
            {
                _engine.StopBuffering();
                _target = null;
                _paused = true;
                _targetState = "capture unavailable";
                _retryAt = DateTime.UtcNow.AddSeconds(5);
                Logger.Warn("window capture unavailable; waiting before retrying.");
                return;
            }
            var current = _detector.Foreground();
            if (current?.Handle != foreground.Handle || current.ProcessId != foreground.ProcessId || current.Surface != foreground.Surface)
            {
                _engine.SetPaused(true);
                _paused = true;
                _candidate = IntPtr.Zero;
                _pendingSurface = null;
                _targetState = "paused";
                return;
            }
            if (_paused) _engine.SetPaused(false);
            _paused = false;
            _targetState = "recording";
        }
        catch (Exception ex)
        {
            _engine.SetPaused(true);
            _paused = true;
            _targetState = "capture unavailable";
            Logger.Error("target detection failed", ex);
        }
        finally { Monitor.Exit(_opLock); }
    }

    public void Stop()
    {
        lock (_opLock)
        {
            _armed = false;
            _targetTimer?.Dispose();
            _targetTimer = null;
            _target = null;
            _candidate = IntPtr.Zero;
            _pendingSurface = null;
            _paused = false;
            _retryAt = default;
            _targetState = "stopped";
            _engine.StopBuffering();
            lock (_lock)
            {
                _lastHealthTicks = -1;
                _noProgressPolls = 0;
            }
            Logger.Info("recording stopped.");
        }
    }

    public bool Restart(string tempDir, bool wipeSegments = false)
    {
        lock (_opLock)
        {
            Stop();
            return Start(tempDir, wipeSegments);
        }
    }

    public SaveReplayResult SaveReplay(string tempDir, string destDir)
    {
        lock (_opLock)
        {
            return SaveReplayCore(destDir);
        }
    }

    private string ResolveSaveCategory()
    {
        if (_specific && _target != null)
            return GameDetector.SanitizeCategoryFolder(_target.Game);

        var fg = _desktopDetector.Foreground();
        if (fg != null)
        {
            _lastDesktopGame = (fg.Game, DateTime.UtcNow);
            return GameDetector.SanitizeCategoryFolder(fg.Game);
        }

        if (GameDetector.IsSelfForeground() && _lastDesktopGame is { } recent &&
            DateTime.UtcNow - recent.SeenUtc <= TimeSpan.FromSeconds(20))
        {
            return GameDetector.SanitizeCategoryFolder(recent.Game);
        }

        return "desktop";
    }

    private SaveReplayResult SaveReplayCore(string destDir)
    {
        lock (_lock)
        {
            if (!IsRunning)
            {
                Logger.Warn("savereplay requested while recorder is stopped.");
                return new SaveReplayResult(SaveReplayOutcome.NotRecording);
            }
        }

        string category = ResolveSaveCategory();
        string categoryFolder = Path.Combine(destDir, category);
        try
        {
            Directory.CreateDirectory(categoryFolder);
        }
        catch (Exception ex)
        {
            Logger.Error("savereplay: failed creating category folder", ex);
            return ProbeWriteBlocked(destDir, ex, category);
        }

        string baseName = $"replay_{DateTime.Now:yyyyMMdd_HHmmss}";
        string destMp4 = Path.Combine(categoryFolder, baseName + ".mp4");
        int suffix = 1;
        while (File.Exists(destMp4))
            destMp4 = Path.Combine(categoryFolder, $"{baseName}_{suffix++}.mp4");

        int rc;
        try
        {
            rc = _engine.SaveReplay(destMp4, Math.Max(_settings.ReplayDurationSeconds, 1));
        }
        catch (Exception ex)
        {
            Logger.Error("savereplay: exception in native engine", ex);
            CleanupPartialFile(destMp4);
            return ProbeWriteBlocked(destDir, ex, category);
        }

        if (rc == 0 && File.Exists(destMp4))
        {
            Logger.Info($"replay saved [{category}] - path: {destMp4}");
            return new SaveReplayResult(SaveReplayOutcome.Success, destMp4, category);
        }

        Logger.Info($"savereplay: native engine returned {rc}.");
        CleanupPartialFile(destMp4);

        if (rc == -31)
            return new SaveReplayResult(SaveReplayOutcome.LowMemory, category: category);

        if (rc == -1 || rc == -30)
            return new SaveReplayResult(SaveReplayOutcome.NoSegments, category: category);

        return ProbeWriteBlocked(destDir, null, category);
    }

    private static void CleanupPartialFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static SaveReplayResult ProbeWriteBlocked(string dir, Exception? originalError, string category)
    {
        try
        {
            string probePath = Path.Combine(dir, ".ir_write_test");
            File.WriteAllText(probePath, "replay write test");
            File.Delete(probePath);
        }
        catch (UnauthorizedAccessException)
        {
            Logger.Warn($"savereplay: write to '{dir}' blocked by controlled folder access.");
            return new SaveReplayResult(SaveReplayOutcome.WriteBlocked, category: category);
        }
        catch { }

        if (originalError != null)
            Logger.Error("savereplay failed", originalError);

        return new SaveReplayResult(SaveReplayOutcome.MergeFailed, category: category);
    }

    public void Dispose()
    {
        Stop();
        try { _engine.Shutdown(); }
        catch (Exception ex) { Logger.Warn($"engine shutdown failed: {ex.Message}"); }
    }
}

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReplayBuffer";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string existing &&
                   existing.Equals(BuildCommand(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn($"startup check failed: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled) key.SetValue(ValueName, BuildCommand());
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info($"start with windows {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            Logger.Error("startupmanager.setenabled failed", ex);
        }
    }

    private static string BuildCommand()
    {
        string exe = Environment.ProcessPath ?? "replay.exe";
        return $"\"{exe}\" --tray";
    }
}

internal static class IrNative
{
    internal const int OK = 0;
    internal const int Fail = -1;
    internal const int NotEnoughData = -30;

    private const string Dll = "engine";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_Create(out IntPtr engine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_Destroy(IntPtr engine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_Start(IntPtr engine, int monitorIndex, int ringSeconds,
        int fps, int bitrateKbps, int capW, int capH, int captureAudio, int captureMic,
        [MarshalAs(UnmanagedType.LPWStr)] string micDevice,
        int encoderPreference, int captureMethod, out long startQpc100ns);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_Stop(IntPtr engine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_SetCaptureTarget(IntPtr engine, IntPtr window, uint options);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_SetPaused(IntPtr engine, int paused);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_Save(IntPtr engine,
        [MarshalAs(UnmanagedType.LPWStr)] string outPath, int secondsBack);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_GetStatus(IntPtr engine, out int isBuffering,
        out int ringSeconds, out long framesEncoded, out long framesDropped,
        out int hasError, out long audioFrames, out long audioEvents,
        out long audioGotFrames, out long healthHeartbeat);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_GetBufferStats(IntPtr engine, out long bufferBytes, out long bufferedMs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_GetMonitorCount();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_GetMonitorInfo(int index,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int nameCap,
        out int width, out int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_IsDxgiSupported();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_DxgiDuplicationSupported();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_ActiveCaptureMethod(IntPtr engine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_ActiveEncoder(IntPtr engine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_HwInitHr(IntPtr engine);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_GetMicDeviceCount();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_GetMicDeviceName(int index,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int nameCap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_GetDefaultMicName(
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int nameCap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IR_ProbeMicState(
        [MarshalAs(UnmanagedType.LPWStr)] string? wantedName, int autoDetectSignal,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder activeName, int activeCap,
        out int peakPct);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void IR_MicStatus(IntPtr engine, out long micFrames, out long micEvents,
        out long micGotFrames, out long micFailHr, out int sysPeak, out int micPeak,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder activeName, int nameCap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr IR_Version();

    internal static string Version()
    {
        try { return Marshal.PtrToStringAnsi(IR_Version()) ?? ""; }
        catch { return ""; }
    }
}

public enum EncoderPreference
{
    Auto = 0,
    Hardware = 1,
    Software = 2,
}

public enum CaptureMethod
{
    Wgc = 0,
    Dxgi = 1,
}

public sealed class ReplayEngine : IDisposable
{
    public static bool DxgiDuplicationSupported()
    {
        try { return IrNative.IR_DxgiDuplicationSupported() == 1; }
        catch { return false; }
    }

    public const int SaveOk = IrNative.OK;
    public const int SaveFailed = IrNative.Fail;
    public const int SaveNotEnoughData = IrNative.NotEnoughData;

    private readonly object _opLock = new();
    private IntPtr _engine;
    private volatile bool _hasError;
    private string _lastVersion = "";

    public ReplayEngine()
    {
        try { _lastVersion = IrNative.Version(); }
        catch (Exception ex)
        {
            Logger.Warn($"could not load engine.dll: {ex.Message}");
            _lastVersion = "";
        }
    }

    public bool Initialize()
    {
        lock (_opLock)
        {
            if (_engine != IntPtr.Zero) return true;
            int rc = IrNative.IR_Create(out IntPtr handle);
            if (rc != IrNative.OK || handle == IntPtr.Zero)
            {
                _hasError = true;
                Logger.Error("ir_create failed; native engine unavailable.");
                return false;
            }
            _engine = handle;
            Logger.Info($"native engine created ({_lastVersion}).");
            return true;
        }
    }

    public bool StartBuffering(int monitorIndex, int ringSeconds, int fps, int bitrateKbps,
        bool captureAudio, EncoderPreference preference, CaptureMethod method = CaptureMethod.Wgc,
        bool captureMic = false, string? micDevice = null, IntPtr targetWindow = default, uint captureOptions = 11,
        int outputWidthCap = 3840, int outputHeightCap = 2160)
    {
        lock (_opLock)
        {
            GetStatus(out bool buffering, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            if (buffering) return true;
            if (!Initialize()) return false;

            int mon = monitorIndex;
            int secs = Math.Max(ringSeconds, 1);
            int rate = Math.Clamp(fps, 15, 240);
            int bps = Math.Max(bitrateKbps, 1);
            int capW = Math.Max(0, outputWidthCap), capH = Math.Max(0, outputHeightCap);

            if (targetWindow != IntPtr.Zero || (captureOptions & 32) != 0) method = CaptureMethod.Wgc;
            if (IrNative.IR_SetCaptureTarget(_engine, targetWindow, captureOptions) != IrNative.OK)
            {
                _hasError = true;
                Logger.Error("capture target unavailable.");
                return false;
            }
            if (method == CaptureMethod.Dxgi && IrNative.IR_DxgiDuplicationSupported() == 0)
            {
                _hasError = true;
                Logger.Error("dxgi capture unavailable on this monitor.");
                return false;
            }

            string wantedMic = (micDevice ?? "").Trim();
            int rc = IrNative.IR_Start(_engine, mon, secs, rate, bps, capW, capH,
                captureAudio ? 1 : 0, captureMic ? 1 : 0,
                captureMic ? wantedMic : string.Empty,
                (int)preference, (int)method, out _);
            if (rc != IrNative.OK)
            {
                _hasError = true;
                Logger.Error($"ir_start failed (rc={rc}).");
                return false;
            }

            _hasError = false;
            return true;
        }
    }

    public void StopBuffering()
    {
        lock (_opLock)
        {
            if (_engine != IntPtr.Zero)
            {
                try { IrNative.IR_Stop(_engine); } catch { }
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_opLock)
        {
            if (_engine != IntPtr.Zero) IrNative.IR_SetPaused(_engine, paused ? 1 : 0);
        }
    }

    public int SaveReplay(string outPath, int secondsBack)
    {
        lock (_opLock)
        {
            if (_engine == IntPtr.Zero) return SaveFailed;
            if (string.IsNullOrWhiteSpace(outPath)) return SaveFailed;
            return IrNative.IR_Save(_engine, outPath, Math.Max(secondsBack, 1));
        }
    }

    public bool TryGetBufferStats(out long bufferBytes, out long bufferedMs)
    {
        bufferBytes = 0;
        bufferedMs = 0;
        if (_engine == IntPtr.Zero) return false;
        try
        {
            IrNative.IR_GetBufferStats(_engine, out bufferBytes, out bufferedMs);
            return true;
        }
        catch { return false; }
    }

    public void GetStatus(out bool isBuffering, out int segmentCount, out int ringSeconds,
        out long framesEncoded, out long framesDropped, out bool hasError,
        out long audioFrames, out long audioEvents, out long audioGotFrames, out long healthHeartbeat)
    {
        if (_engine == IntPtr.Zero)
        {
            isBuffering = false;
            segmentCount = 0;
            ringSeconds = 0;
            framesEncoded = 0;
            framesDropped = 0;
            hasError = _hasError;
            audioFrames = 0;
            audioEvents = 0;
            audioGotFrames = 0;
            healthHeartbeat = 0;
            return;
        }

        IrNative.IR_GetStatus(_engine, out int ib, out int rs, out long fe, out long fd, out int he,
            out long af, out long ae, out long agf, out long hb);
        isBuffering = ib != 0;
        segmentCount = 0;
        ringSeconds = rs;
        framesEncoded = fe;
        framesDropped = fd;
        hasError = he != 0 || _hasError;
        audioFrames = af;
        audioEvents = ae;
        audioGotFrames = agf;
        healthHeartbeat = hb;
    }

    public static List<(int Index, string Name)> EnumerateMonitors()
    {
        var result = new List<(int, string)>();
        try
        {
            int count = IrNative.IR_GetMonitorCount();
            for (int i = 0; i < count; ++i)
            {
                var sb = new StringBuilder(256);
                IrNative.IR_GetMonitorInfo(i, sb, sb.Capacity, out _, out _);
                result.Add((i, sb.ToString().ToLowerInvariant()));
            }
        }
        catch (Exception ex) { Logger.Warn($"enumeratemonitors failed: {ex.Message}"); }
        return result;
    }

    public static List<string> EnumerateMicrophones()
    {
        var result = new List<string>();
        try
        {
            int count = IrNative.IR_GetMicDeviceCount();
            for (int i = 0; i < count; ++i)
            {
                var sb = new StringBuilder(512);
                IrNative.IR_GetMicDeviceName(i, sb, sb.Capacity);
                string name = sb.ToString().Trim();
                if (name.Length > 0 && !result.Contains(name)) result.Add(name);
            }
        }
        catch (Exception ex) { Logger.Warn($"enumeratemicrophones failed: {ex.Message}"); }
        return result;
    }

    public static string GetDefaultMicrophoneName()
    {
        try
        {
            var sb = new StringBuilder(512);
            return IrNative.IR_GetDefaultMicName(sb, sb.Capacity) != 0 ? sb.ToString().Trim() : "";
        }
        catch { return ""; }
    }

    public static bool ProbeMicState(string? wanted, bool detectSignal, out string activeName, out int peakPct)
    {
        activeName = "";
        peakPct = 0;
        try
        {
            var sb = new StringBuilder(512);
            int ok = IrNative.IR_ProbeMicState(wanted, detectSignal ? 1 : 0, sb, sb.Capacity, out peakPct);
            activeName = sb.ToString().Trim();
            return ok != 0;
        }
        catch { return false; }
    }

    public bool TryGetMicStatus(out long micFrames, out long micEvents, out long micGotFrames,
        out long micFailHr, out int sysPeak, out int micPeak, out string activeName)
    {
        micFrames = 0; micEvents = 0; micGotFrames = 0; micFailHr = 0;
        sysPeak = 0; micPeak = 0; activeName = "";
        if (_engine == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(512);
            IrNative.IR_MicStatus(_engine, out micFrames, out micEvents, out micGotFrames,
                out micFailHr, out sysPeak, out micPeak, sb, sb.Capacity);
            activeName = sb.ToString().Trim();
            return true;
        }
        catch { return false; }
    }

    public void Shutdown()
    {
        lock (_opLock)
        {
            StopBuffering();
            if (_engine != IntPtr.Zero)
            {
                try { IrNative.IR_Destroy(_engine); } catch { }
                _engine = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Shutdown();
}
