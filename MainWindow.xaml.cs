using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Brush = Microsoft.UI.Xaml.Media.Brush;

namespace InstantReplay;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime, dwHighDateTime;
        public ulong Ticks => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    private static readonly uint s_taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private const int SwMinimize = 6;
    private const int GwlpWndProc = -4;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int WM_HOTKEY = 0x0312;
    private const uint WmSize = 0x0005;
    private const int SizeMinimized = 1;
    private const uint WmSysCommand = 0x0112;
    private const int ScKeyMenu = 0xF100;

    internal const int HotkeyId = 0x1000;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly TimeSpan SaveCooldown = TimeSpan.FromSeconds(4);
    private DateTime _lastSaveAcceptedUtc = DateTime.MinValue;

    private readonly Settings _settings;
    private readonly RecorderEngine _engine;
    private readonly string _tempDir;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dq;

    private TrayIcon? _trayIcon;
    private DispatcherTimer? _bufferHealthTimer;
    private DispatcherTimer? _micRefreshTimer;
    private DispatcherTimer? _micMeterTimer;
    private bool _micProbeInFlight;
    private DispatcherTimer? _deviceChangeDebounceTimer;
    private DispatcherTimer? _statsTimer;
    private ulong _lastIdleTicks, _lastKernelTicks, _lastUserTicks;
    private bool _statsBaselineTaken;
    private AudioDeviceWatcher? _audioWatcher;
    private Storyboard? _dotStoryboard;
    private Vector3 _navPillPosition;
    private bool _homeEntrancePlayed;
    private bool _inlineVideoRevealed;
    private bool _isClosingForReal;
    private bool _isHidden;
    private bool _isMinimized;

    private bool? _lastBufferHealthy;
    private readonly bool _startMinimized;

    private List<string> _knownMics = new();
    private List<string> _micChoices = new();
    private string _defaultMicName = "";
    private bool _micMissing;

    private const double DefaultWindowWidthDips = 960;
    private const double DefaultWindowHeightDips = 700;

    private ReplayLibrary _library = null!;
    private DispatcherTimer? _infoBarTimer;
    private ReplayItem? _viewerItem;
    private bool _galleryRefreshing;
    private bool _galleryRefreshAgain;

    private bool _isVideoFullscreen;
    private WebView2? _inlinePlayer;
    private Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? _webEnvTask;
    private bool _inlinePageReady;
    private bool _inlineWarming;
    private double _playerTime;
    private double _playerDuration;
    private bool _playerPlaying = true;
    private double _playerVolume = 1;
    private bool _playerMuted;
    private int _viewerGeneration;
    private double _playerAspect = 16.0 / 9.0;

    private enum TrimDragTarget { None, Start, End, Playhead }
    private bool _isEditingVideo;
    private bool _isExportingVideo;
    private CancellationTokenSource? _exportCts;
    private double _trimStartSec;
    private double _trimEndSec;
    private TrimDragTarget _trimDragTarget;
    private DateTime _trimSeekHoldUntilUtc;
    private DateTime _lastTrimPreviewUtc;
    private double? _pendingTrimSeekSec;
    private DispatcherTimer? _trimPreviewTimer;
    private string _cropAspectSpec = "free";
    private int _cropX;
    private int _cropY;
    private int _cropW;
    private int _cropH;
    private int _cropSrcW;
    private int _cropSrcH;

    private readonly HashSet<GridViewItem> _hoverWired = new();

    private bool _capturingHotkey;
    private uint _pendingHotkeyModifier;

    private bool _initializing = true;
    private bool _syncingStartupToggle;
    private bool _syncingMicCombo;
    private bool _syncingAudioToggles;
    private bool _syncingDurationCustom;
    private bool _durationCustomVisible;
    private Storyboard? _durationDisclosure;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, Storyboard> _propertyMotion = new();
    private Storyboard? _audioAppsDisclosure;
    private Storyboard? _audioSourceDisclosure;
    private bool _audioSourceShown = true;
    private bool _audioAppsShown;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private List<AudioAppCandidate> _audioApps = new();
    private bool _dirty;
    private bool _savingChanges;
    private PendingSettings _baseline;
    private bool _flyoutShown;

    private string? _lastSavedReplayPath;
    private FrameworkElement _currentPage;
    private bool _isSettingsActive;

    private TaskbarBadge? _taskbarBadge;
    private BadgeKind _badgeKind = BadgeKind.None;
    private IntPtr _taskbarBigIcon;
    private IntPtr _taskbarSmallIcon;
    private IntPtr _trayBadgeIcon;
    private bool _hasErrored;
    private bool _checkingUpdate;
    private bool _downloadingUpdate;
    private UpdateCheckResult? _availableUpdate;

    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _originalWndProc;

    public MainWindow(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        InitializeComponent();

        Title = "instant replay";
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _library = new ReplayLibrary(_dq);
        _currentPage = HomePage;

        ConfigureWindowChrome();
        SubclassWndProc();

        _settings = Settings.Load();
        Logger.Init(_settings.DebugLogging);
        ReplayLibrary.EnsureBaseFolders(_settings.GetEffectiveSaveDestination());
        UpdateWebView2Requirement();

        _tempDir = Path.Combine(Path.GetTempPath(), "replay_buffer");
        _engine = new RecorderEngine(_settings);

        RootGrid.Loaded += (_, _) =>
        {
            InitAudioToggles();
            UpdateNavPillVisual(animate: false);
            UpdateTitleBarDragRegions();
            if (!_homeEntrancePlayed)
            {
                _homeEntrancePlayed = true;
                FadeIn(PagesHost, from: 0.84f, offset: 4);
                UpdateHeroOrnament();
            }
        };

        RefreshMicrophonesInBackground();
        InitDurationSegments();
        InitFpsSegments();
        InitQualitySegments();
        InitEncoderSegments();
        InitCaptureSegments();
        InitCaptureTargets();
        UpdateHotkeyButtonDisplay();

        StartupToggle.IsOn = StartupManager.IsEnabled();
        DebugToggle.IsOn = _settings.DebugLogging;
        AutoUpdateToggle.IsOn = _settings.AutoCheckUpdates;
        ThemeToggle.IsOn = _settings.DarkMode;
        SaveLocationBox.Text = _settings.GetEffectiveSaveDestination();
        UpdateVersionText.Text = $"version {AppUpdater.CurrentVersionText}";

        _initializing = false;
        _baseline = CapturePending();
        UpdateHeroSummary();

        GalleryGrid.ItemsSource = _library.Items;
        GalleryGrid.ContainerContentChanging += GalleryGrid_ContainerContentChanging;
        _library.Items.CollectionChanged += (_, _) => UpdateGalleryChrome();
        _library.FolderChanged += () => _ = RefreshGalleryAsync();

        if (!RegisterHotkeyInternal(_settings.HotkeyModifiers, _settings.HotkeyVk))
            Logger.Warn($"could not register global hotkey {HotkeyDisplay.Format(_settings.HotkeyModifiers, _settings.HotkeyVk)}.");

        ApplyAppTheme(_settings.DarkMode);
        Chrome.ApplyBackdrop(this, _settings.DarkMode);
        SetupTrayIcon();
        RefreshAppBadge();

        if (_settings.AutoCheckUpdates)
        {
            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            updateTimer.Tick += (_, _) =>
            {
                updateTimer.Stop();
                _ = CheckForUpdatesAsync(userInitiated: false);
            };
            updateTimer.Start();
        }

        _bufferHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _bufferHealthTimer.Tick += (_, _) =>
        {
            if (_isHidden) return;
            _engine.TrackForegroundGame();
            CheckBufferHealth();
            UpdateBufferBadge();
        };
        if (!_startMinimized) _bufferHealthTimer.Start();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _statsTimer.Tick += (_, _) => { if (!_isHidden) UpdateSystemStats(); };
        if (!_startMinimized)
        {
            _statsTimer.Start();
            UpdateSystemStats();
            _ = Task.Delay(300).ContinueWith(_ => _dq.TryEnqueue(UpdateSystemStats));
        }

        _micRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _micRefreshTimer.Tick += (_, _) => { if (!_isHidden) RefreshMicrophonesInBackground(); };
        if (!_startMinimized) _micRefreshTimer.Start();

        _micMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _micMeterTimer.Tick += (_, _) => { if (!_isHidden && _isSettingsActive) UpdateMicLiveStatus(); };
        if (!_startMinimized) _micMeterTimer.Start();

        _deviceChangeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _deviceChangeDebounceTimer.Tick += (_, _) =>
        {
            _deviceChangeDebounceTimer!.Stop();
            RefreshMicrophonesInBackground();
        };
        _audioWatcher = new AudioDeviceWatcher();
        _audioWatcher.DevicesChanged += () => _dq.TryEnqueue(() =>
        {
            _deviceChangeDebounceTimer!.Stop();
            _deviceChangeDebounceTimer!.Start();
        });

        _ = RefreshGalleryAsync();
        _ = InitialStartAsync();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Activated += Window_Activated;
        Closed += Window_ClosedRequested;
        RootGrid.PreviewKeyDown += Window_PreviewKeyDown;
        RootGrid.PreviewKeyUp += Window_PreviewKeyUp;
        if (TrimTrackGrid != null)
        {
            TrimTrackGrid.PointerCanceled += (_, e) => EndTrimDrag(e.Pointer);
            TrimTrackGrid.PointerCaptureLost += (_, e) => EndTrimDrag(e.Pointer);
        }

        if (_startMinimized)
        {
            _dq.TryEnqueue(() =>
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
                ShowWindow(_hwnd, SwHide);
                _isHidden = true;
                UpdateHeroOrnament();
                NotifyRunningInBackground();
            });
        }
    }

    private void ConfigureWindowChrome()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        double scale = Math.Max(1.0, dpi / 96.0);

        RectInt32 work = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int w = (int)Math.Min(DefaultWindowWidthDips * scale, work.Width * 0.94);
        int h = (int)Math.Min(DefaultWindowHeightDips * scale, work.Height * 0.94);

        _appWindow.ResizeClient(new SizeInt32(w, h));
        SizeInt32 outer = _appWindow.Size;
        _appWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - outer.Width) / 2),
            work.Y + Math.Max(0, (work.Height - outer.Height) / 2)));

        string iconPath = ResolveIconPath();
        if (File.Exists(iconPath))
        {
            try { _appWindow.SetIcon(iconPath); }
            catch (Exception ex) { Logger.Warn($"could not set icon: {ex.Message}"); }
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        var tb = _appWindow.TitleBar;
        if (tb != null)
        {
            try
            {
                tb.ExtendsContentIntoTitleBar = true;
                tb.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Warn($"titlebar extend failed: {ex.Message}");
            }
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleBarDragRegions();
    }

    private void UpdateTitleBarDragRegions()
    {
        try
        {
            var tb = _appWindow.TitleBar;
            if (tb == null || RootGrid.ActualWidth <= 0) return;

            if (_isVideoFullscreen)
            {
                tb.SetDragRectangles(Array.Empty<RectInt32>());
                return;
            }

            double scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
            int headerHeightPx = (int)Math.Round(FloatingHeader.ActualHeight * scale);
            int topStripPx = (int)Math.Round(8 * scale);
            int winWidthPx = (int)Math.Round(RootGrid.ActualWidth * scale);

            var rects = new List<RectInt32>();
            if (topStripPx > 0 && winWidthPx > 0)
                rects.Add(new RectInt32(0, 0, winWidthPx, topStripPx));

            RectInt32 BoundsPx(FrameworkElement el)
            {
                var pt = el.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                return new RectInt32(
                    (int)Math.Floor(pt.X * scale),
                    (int)Math.Floor(pt.Y * scale),
                    (int)Math.Ceiling(el.ActualWidth * scale),
                    (int)Math.Ceiling(el.ActualHeight * scale));
            }

            if (NavSegmentedCapsule.ActualWidth > 0 && WindowButtonsCapsule.ActualWidth > 0)
            {
                var n = BoundsPx(NavSegmentedCapsule);
                var w = BoundsPx(WindowButtonsCapsule);

                if (n.X > 4)
                    rects.Add(new RectInt32(0, 0, n.X, headerHeightPx));

                int rightGapW = Math.Max(0, w.X - (n.X + n.Width));
                if (rightGapW > 4)
                    rects.Add(new RectInt32(n.X + n.Width, 0, rightGapW, headerHeightPx));
            }
            else if (winWidthPx > 0)
            {
                rects.Add(new RectInt32(0, 0, winWidthPx, headerHeightPx));
            }

            tb.SetDragRectangles(rects.ToArray());
        }
        catch { }
    }

    private void MinimizeTitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        else ShowWindow(_hwnd, SwMinimize);
    }

    private void CloseTitleButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWebView2Requirement()
    {
        bool installed;
        try
        {
            string version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            installed = !string.IsNullOrWhiteSpace(version);
        }
        catch { installed = false; }
        WebViewRequirementBar.IsOpen = !installed;
        WebViewRequirementBar.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void DownloadWebView2Button_Click(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://developer.microsoft.com/en-us/microsoft-edge/webview2/")); }
        catch { }
    }

    private void SubclassWndProc()
    {
        _wndProcDelegate = WndProc;
        IntPtr newProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, newProcPtr);
    }

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateWebViewEnvironmentAsync()
    {
        var opts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        };
        return Microsoft.Web.WebView2.Core.CoreWebView2Environment
            .CreateWithOptionsAsync(string.Empty, string.Empty, opts)
            .AsTask();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmSize)
        {
            bool minimized = wParam.ToInt64() == SizeMinimized;
            if (_isMinimized != minimized)
            {
                _isMinimized = minimized;
                _dq.TryEnqueue(UpdateHeroOrnament);
            }
        }
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            if (!_capturingHotkey) _ = SaveReplayAsync();
            return IntPtr.Zero;
        }
        if (msg == WmSysCommand && _capturingHotkey && ((int)(wParam.ToInt64() & 0xFFF0) == ScKeyMenu))
        {
            return IntPtr.Zero;
        }
        if (msg == s_taskbarButtonCreated && s_taskbarButtonCreated != 0)
        {
            _taskbarBadge?.Reset();
            _dq.TryEnqueue(RefreshAppBadge);
        }
        if (msg == TrayIcon.WM_TRAYICON)
        {
            _trayIcon?.HandleMessage(lParam);
            return IntPtr.Zero;
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void ApplyAppTheme(bool dark)
    {
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Chrome.UpdateBackdropTheme(this, dark);
        if (_appWindow.TitleBar != null)
            _appWindow.TitleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        TitleBarButtons.Apply(_appWindow.TitleBar, dark);
        if (!_isVideoFullscreen)
        {
            if (ViewerPage != null) ViewerPage.Background = GetThemedBrush("BgBrush");
            if (PlayerCard != null) PlayerCard.Background = GetThemedBrush("SurfaceElevatedBrush");
        }
        if (_inlinePageReady) SendAccentInline();
        UpdateNavPillVisual(animate: false);
        RebuildCategoryFilterPills();
        UpdateSystemStats();
        SetRunningVisual(_engine.IsRunning);
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)
            _dq.TryEnqueue(() =>
            {
                UpdateNavPillVisual(animate: false);
                UpdateHeroOrnament();
                if (!_uiSettings.AnimationsEnabled && _durationDisclosure != null)
                    SetDurationCustomVisible(_durationCustomVisible, animate: false);
                if (!_uiSettings.AnimationsEnabled) StopDotPulse();
            });
    }

    private Brush GetThemedBrush(string key)
    {
        string dictKey = _settings.DarkMode ? "Dark" : "Light";
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(dictKey, out var dictObj) &&
                dictObj is ResourceDictionary dict && dict.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        return (Brush)Application.Current.Resources[key];
    }

    private readonly record struct PendingSettings(
        int ReplayDurationSeconds,
        int FrameRate,
        string QualityPreset,
        string EncoderChoice,
        string CaptureMethod,
        string CaptureTarget,
        int CaptureMonitorIndex,
        bool CaptureClientArea,
        bool CaptureCursor,
        bool CaptureRoundedCorners,
        bool CaptureSecondaryWindows,
        string Microphone,
        string SaveDestination,
        bool RecordSystemAudio,
        string SystemAudioMode,
        string SystemAudioAppsKey,
        bool RecordMicrophone);

    private static string FormatAudioAppsKey(IEnumerable<string> apps) =>
        string.Join("|", apps
            .Select(a => a.Trim().ToLowerInvariant())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));

    private PendingSettings CapturePending() => new(
        _settings.ReplayDurationSeconds,
        _settings.FrameRate,
        _settings.QualityPreset,
        _settings.EncoderChoice,
        _settings.CaptureMethod,
        _settings.CaptureTarget,
        _settings.CaptureMonitorIndex,
        _settings.CaptureClientArea,
        _settings.CaptureCursor,
        _settings.CaptureRoundedCorners,
        _settings.CaptureSecondaryWindows,
        _settings.Microphone,
        _settings.SaveDestination,
        _settings.RecordSystemAudio,
        _settings.SystemAudioMode,
        FormatAudioAppsKey(_settings.SystemAudioApps),
        _settings.RecordMicrophone);

    private void NavHomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_currentPage, ViewerPage))
            CloseViewer();
        SwitchToPage(settingsPage: false);
    }

    private void NavSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_currentPage, ViewerPage))
            StopViewerPlayback();
        SwitchToPage(settingsPage: true);
    }

    private void SwitchToPage(bool settingsPage)
    {
        if (_isSettingsActive == settingsPage && !ReferenceEquals(_currentPage, ViewerPage))
            return;

        if (_dirty && !settingsPage)
            RevertPendingChanges();

        _isSettingsActive = settingsPage;
        UpdateNavPillVisual(animate: true);

        if (settingsPage)
        {
            RefreshStartupToggleState();
            UpdateMicLiveStatus();
            RefreshAudioAppsInBackground();
        }

        var target = settingsPage ? (FrameworkElement)SettingsPage : HomePage;
        NavigateTo(target);
        if (!settingsPage) _ = RefreshGalleryAsync();
    }

    private void UpdateNavPillVisual(bool animate)
    {
        Vector3 target = new(_isSettingsActive ? 96 : 0, 0, 0);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(NavActivePill, true);
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(NavActivePill);
        if (animate && _uiSettings.AnimationsEnabled && target != _navPillPosition)
        {
            var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
            slide.InsertExpressionKeyFrame(0, "this.StartingValue");
            slide.InsertKeyFrame(1, target, visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1), new Vector2(0.36f, 1)));
            slide.Duration = TimeSpan.FromMilliseconds(165);
            visual.StartAnimation("Translation", slide);
        }
        else if (!animate || target != _navPillPosition)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
        }
        _navPillPosition = target;
        var primary = GetThemedBrush("TextPrimaryBrush");
        var secondary = GetThemedBrush("TextSecondaryBrush");
        NavHomeButton.Foreground = NavHomeIcon.Stroke = _isSettingsActive ? secondary : primary;
        NavSettingsButton.Foreground = NavSettingsIcon.Stroke = _isSettingsActive ? primary : secondary;
    }

    private void FadeIn(FrameworkElement element, float from = 0.9f, float offset = 0)
    {
        if (!_uiSettings.AnimationsEnabled) return;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1), new Vector2(0.36f, 1));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, from);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = TimeSpan.FromMilliseconds(165);
        visual.StartAnimation("Opacity", fade);
        if (offset == 0)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(0, offset, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, easing);
        slide.Duration = fade.Duration;
        visual.StartAnimation("Translation", slide);
    }

    private void NavigateTo(FrameworkElement newPage, bool fadeOnly = false)
    {
        if (ReferenceEquals(newPage, _currentPage)) return;
        var oldPage = _currentPage;
        _currentPage = newPage;
        UpdateHeroOrnament();
        if (ReferenceEquals(oldPage, ViewerPage)) StopViewerPlayback();
        oldPage.Visibility = Visibility.Collapsed;
        newPage.Visibility = Visibility.Visible;
        if (!fadeOnly && newPage is ScrollViewer scroll) scroll.ChangeView(0, 0, null, disableAnimation: true);
        FadeIn(newPage, offset: fadeOnly ? 0 : 4);
    }

    private void Notify(string title, string message, int balloonIcon)
    {
        if (!ToastCenter.Show(title, message))
            _trayIcon?.ShowBalloonTip(title, message, balloonIcon);
    }

    private void NotifyRunningInBackground()
    {
        if (_settings.AppMinimizedNotificationShown) return;
        _settings.AppMinimizedNotificationShown = true;
        _settings.SaveFields(nameof(Settings.AppMinimizedNotificationShown));
        Notify("instant replay", "instant replay is running in the background.", TrayIcon.IconInfo);
    }

    private void CloseButton_Click()
    {
        if (_isVideoFullscreen) ExitVideoFullscreen();
        if (!_isExportingVideo && _viewerItem != null)
        {
            StopViewerPlayback(destroy: true);
            NavigateTo(HomePage, fadeOnly: true);
        }
        try { _appWindow.Hide(); }
        catch
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
            ShowWindow(_hwnd, SwHide);
        }
        _isHidden = true;
        UpdateHeroOrnament();
        _bufferHealthTimer?.Stop();
        _statsTimer?.Stop();
        _micMeterTimer?.Stop();
        _micRefreshTimer?.Stop();
        StopDotPulse();
        _taskbarBadge?.SetTaskbarVisible(false);
        NotifyRunningInBackground();
    }

    private void Window_ClosedRequested(object sender, WindowEventArgs args)
    {
        if (_isClosingForReal) return;
        args.Handled = true;
        if (_dirty) RevertPendingChanges();
        CloseButton_Click();
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_capturingHotkey) EndHotkeyCapture();
            _trimDragTarget = TrimDragTarget.None;
            return;
        }

        UpdateHeroOrnament();
        RefreshStartupToggleState();
        _taskbarBadge?.Reset();
        RefreshAppBadge();
        _ = RefreshGalleryAsync();
    }

    private void UpdateHeroSummary()
    {
        string target = _settings.CaptureTarget == "SpecificWindows" ? "game capture" : "whole desktop";
        string hk = HotkeyDisplay.Format(_settings.HotkeyModifiers, _settings.HotkeyVk);
        HeroSummaryText.Text = $"{_settings.FrameRate} fps · {target}";
        HeroHotkeyText.Text = hk;
        EmptyGalleryHintText.Text = $"press {hk} while recording to save your first clip.";
    }

    private void UpdateBufferBadge()
    {
        if (!_engine.IsRunning)
        {
            HeroBufferText.Text = $"stopped · {_settings.ReplayDurationSeconds} s target";
            return;
        }
        if (_engine.IsPaused)
        {
            HeroBufferText.Text = _engine.TargetState;
            return;
        }
        if (_engine.TryGetBufferStats(out long bytes, out long ms))
        {
            int secs = (int)Math.Clamp(Math.Round(ms / 1000.0), 0, _settings.ReplayDurationSeconds);
            double mb = bytes / (1024.0 * 1024.0);
            string game = _engine.ActiveGameName;
            string prefix = string.IsNullOrEmpty(game) ? "" : $"{game.ToLowerInvariant()} · ";
            HeroBufferText.Text = $"{prefix}{secs} s / {_settings.ReplayDurationSeconds} s · {mb:0.0} mb";
        }
    }

    private void UpdateHeroOrnament() => HeroOrnament.SetPlaying(
        !_isClosingForReal && !_isHidden && !_isMinimized && _uiSettings.AnimationsEnabled
        && ReferenceEquals(_currentPage, HomePage));

    private void PlayHeroSaveFlash()
    {
        if (!_uiSettings.AnimationsEnabled) return;
        AnimateDouble(HeroSaveFlash, UIElement.OpacityProperty, 0, 240, from: Math.Max(0.65, HeroSaveFlash.Opacity));
    }

    private static string ResolveIconPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "icon.ico");
        return File.Exists(path) ? path : "";
    }

    private void SetupTrayIcon()
    {
        string iconPath = ResolveIconPath();
        _taskbarBadge = new TaskbarBadge(_hwnd);
        _trayIcon = new TrayIcon(_hwnd, iconPath, "Instant replay");
        _trayIcon.MenuItemsProvider = () => new List<TrayMenuItem>
        {
            new() { Text = "start with windows", IsChecked = StartupManager.IsEnabled(), OnClick = () =>
            {
                bool next = !StartupManager.IsEnabled();
                StartupManager.SetEnabled(next);
                _dq.TryEnqueue(() => StartupToggle.IsOn = next);
            }},
            TrayMenuItem.Separator(),
            new() { Text = "save replay", OnClick = () => _ = SaveReplayAsync() },
            new() { Text = "open window", OnClick = ShowFromTray },
            TrayMenuItem.Separator(),
            new() { Text = "restart app", OnClick = RestartApplication },
            TrayMenuItem.Separator(),
            new() { Text = "exit", OnClick = ExitApplicationAsync },
        };
        _trayIcon.DoubleClicked += ShowFromTray;
        _trayIcon.BalloonClicked += () =>
        {
            if (_lastSavedReplayPath != null) OpenContainingFolder(_lastSavedReplayPath);
        };
    }

    private static void OpenContainingFolder(string filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true,
                });
                return;
            }
            if (!string.IsNullOrEmpty(filePath) && Directory.Exists(filePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex) { Logger.Warn($"could not open explorer: {ex.Message}"); }
    }

    public void BringToFront() => ShowFromTray();

    private void ShowFromTray()
    {
        _isHidden = false;
        UpdateHeroOrnament();
        _bufferHealthTimer?.Start();
        _statsTimer?.Start();
        _micRefreshTimer?.Start();
        _micMeterTimer?.Start();
        UpdateSystemStats();
        try { _appWindow.Show(); } catch { }
        ShowWindow(_hwnd, SwShow);
        if (_appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        _taskbarBadge?.SetTaskbarVisible(true);
        Activate();
        RefreshStartupToggleState();
        RefreshAppBadge();
        if (_engine.IsRunning && !_engine.IsPaused)
        {
            StartDotPulse();
        }
    }

    private void RefreshAppBadge() =>
        UpdateAppBadge(_hasErrored ? BadgeKind.Error
            : _engine.IsRunning && !_engine.IsPaused ? BadgeKind.Recording
            : BadgeKind.Paused);

    private void UpdateAppBadge(BadgeKind kind)
    {
        if (kind == BadgeKind.Error) _hasErrored = true;
        try
        {
            if (kind != _badgeKind)
            {
                _badgeKind = kind;
                IntPtr prevBig = _taskbarBigIcon;
                IntPtr prevSmall = _taskbarSmallIcon;
                IntPtr prevTray = _trayBadgeIcon;

                if (kind == BadgeKind.None)
                {
                    _taskbarBigIcon = BadgeGlyphs.LoadAppIcon(48);
                    _taskbarSmallIcon = BadgeGlyphs.LoadAppIcon(24);
                    _trayBadgeIcon = IntPtr.Zero;
                }
                else
                {
                    _taskbarBigIcon = BadgeGlyphs.CreateBadgeIcon(kind, 48);
                    _taskbarSmallIcon = BadgeGlyphs.CreateBadgeIcon(kind, 24);
                    _trayBadgeIcon = BadgeGlyphs.CreateBadgeIcon(kind, 32);
                }

                _trayIcon?.SetIcon(_trayBadgeIcon);
                _taskbarBadge?.Clear();
                ApplyTaskbarIcons();

                if (prevBig != IntPtr.Zero) DestroyIcon(prevBig);
                if (prevSmall != IntPtr.Zero) DestroyIcon(prevSmall);
                if (prevTray != IntPtr.Zero) DestroyIcon(prevTray);
            }
            else
            {
                _taskbarBadge?.Clear();
                ApplyTaskbarIcons();
            }
        }
        catch { }
    }

    private void ApplyTaskbarIcons()
    {
        const uint WmSetIcon = 0x0080;
        if (_taskbarBigIcon != IntPtr.Zero)
        {
            try { _appWindow.SetTaskbarIcon(Win32Interop.GetIconIdFromIcon(_taskbarBigIcon)); } catch { }
            SendMessageW(_hwnd, WmSetIcon, (IntPtr)1, _taskbarBigIcon);
        }
        if (_taskbarSmallIcon != IntPtr.Zero)
        {
            SendMessageW(_hwnd, WmSetIcon, (IntPtr)0, _taskbarSmallIcon);
        }
    }

    private void ReleaseBadgeIcons()
    {
        if (_taskbarBigIcon != IntPtr.Zero) { DestroyIcon(_taskbarBigIcon); _taskbarBigIcon = IntPtr.Zero; }
        if (_taskbarSmallIcon != IntPtr.Zero) { DestroyIcon(_taskbarSmallIcon); _taskbarSmallIcon = IntPtr.Zero; }
        if (_trayBadgeIcon != IntPtr.Zero) { DestroyIcon(_trayBadgeIcon); _trayBadgeIcon = IntPtr.Zero; }
    }

    private void RestartApplication()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        catch { }
        ExitApplicationAsync();
    }

    private bool StartCaptureCore()
    {
        try
        {
            Directory.CreateDirectory(_tempDir);
            return _engine.Start(_tempDir);
        }
        catch (Exception ex) { Logger.Error("startcapture failed", ex); return false; }
    }

    private async Task InitialStartAsync()
    {
        bool ok = await Task.Run(StartCaptureCore);
        SetRunningVisual(ok);
        if (!ok) ShowStartFailure();
    }

    private void ShowStartFailure()
    {
        ShowInfoBar(InfoBarSeverity.Error, "Recording could not start",
            "Check that the capture target is available or switch the video encoder to Software in Settings.");
        _infoBarTimer?.Stop();
    }

    private void SetRunningVisual(bool running)
    {
        StopDotPulse();
        _lastBufferHealthy = running ? true : null;

        if (running)
        {
            StatusHeadline.Text = _engine.TargetState;
            var recBrush = GetThemedBrush("RecBrush");
            RecDot.Fill = recBrush;
            RecDot.Opacity = 1;
            if (!_engine.IsPaused && !_isHidden)
            {
                StartDotPulse();
            }
            ApplyStartStopLook(true);
            _hasErrored = false;
            RefreshAppBadge();
        }
        else
        {
            var muted = GetThemedBrush("TextTertiaryBrush");
            RecDot.Fill = muted;
            StatusHeadline.Text = "stopped";
            ApplyStartStopLook(false);
            RefreshAppBadge();
        }
        UpdateBufferBadge();
    }

    private void ApplyStartStopLook(bool running)
    {
        SaveReplayButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartReplayText.Text = "start replay";
        StopReplayText.Text = "stop";
        StartReplayButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopReplayButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CheckBufferHealth()
    {
        if (!_engine.IsRunning)
        {
            if (_lastBufferHealthy != null) { _lastBufferHealthy = null; SetRunningVisual(false); }
            return;
        }
        if (_engine.IsPaused)
        {
            StopDotPulse();
            StatusHeadline.Text = _engine.TargetState;
            var warnBrush = GetThemedBrush("ErrorBrush");
            RecDot.Fill = warnBrush;
            ApplyStartStopLook(true);
            RefreshAppBadge();
            _lastBufferHealthy = null;
            return;
        }

        bool healthy = _engine.IsBufferHealthy();
        if (_lastBufferHealthy == healthy) return;
        _lastBufferHealthy = healthy;

        if (healthy) { SetRunningVisual(true); return; }

        StopDotPulse();
        var err = GetThemedBrush("ErrorBrush");
        RecDot.Fill = err;
        StatusHeadline.Text = "error";
        UpdateAppBadge(BadgeKind.Error);
    }

    private async Task SaveReplayAsync()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastSaveAcceptedUtc < SaveCooldown) return;
        _lastSaveAcceptedUtc = now;

        string destDir = _settings.GetEffectiveSaveDestination();
        SaveReplayResult result;
        try
        {
            result = await Task.Run(() =>
            {
                ReplayLibrary.EnsureBaseFolders(destDir);
                return _engine.SaveReplay(_tempDir, destDir);
            });
        }
        catch (Exception ex)
        {
            Logger.Error("failed to save replay", ex);
            result = new SaveReplayResult(SaveReplayOutcome.MergeFailed);
        }

        _lastSavedReplayPath = result.Success ? result.Path : null;
        int clippedSeconds = result.Success
            ? await Task.Run(() => ClipNotificationWindow.RecordedSeconds(result.Path, _settings.ReplayDurationSeconds))
            : 0;

        _dq.TryEnqueue(() =>
        {
            if (result.Success)
            {
                PlayHeroSaveFlash();
                if (!_isClosingForReal) ClipNotificationWindow.Show(clippedSeconds, result.Category);
                _ = RefreshGalleryAsync();
            }
            else
            {
                Logger.Warn($"failed to clip: {result.UserMessage()}");
                if (!_isClosingForReal) ClipNotificationWindow.ShowError(result.UserMessage());
            }
        });
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        StartReplayButton.IsEnabled = false;
        StopReplayButton.IsEnabled = false;
        if (_engine.IsRunning)
        {
            StopReplayText.Text = "stopping...";
            await Task.Run(() => _engine.Stop());
            SetRunningVisual(false);
        }
        else
        {
            StartReplayText.Text = "starting...";
            bool ok = await Task.Run(StartCaptureCore);
            SetRunningVisual(ok);
            if (!ok) ShowStartFailure();
            else ClearInfoBar();
        }
        StartReplayButton.IsEnabled = true;
        StopReplayButton.IsEnabled = true;
    }

    private void SaveReplayButton_Click(object sender, RoutedEventArgs e) => _ = SaveReplayAsync();

    private void OpenReplaysFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string dir = _settings.GetEffectiveSaveDestination();
        ReplayLibrary.EnsureBaseFolders(dir);
        OpenContainingFolder(dir);
    }

    // ==================== GALLERY & CATEGORY FILTERS ====================

    private void UpdateGalleryChrome()
    {
        int shown = _library.Items.Count;
        int total = _library.AllItems.Count;
        GalleryCountText.Text = shown == total
            ? (total == 1 ? "1 clip" : $"{total} clips")
            : $"{shown} of {total} clips";

        GalleryEmptyState.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryGrid.Visibility = shown == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (shown > 0) UpdateGalleryColumns();
        RebuildCategoryFilterPills();
    }

    private void RebuildCategoryFilterPills()
    {
        var filters = _library.GetCategoryFilters();
        string active = _library.ActiveFilter;
        var activeStyle = (Style)Application.Current.Resources["FilterActiveButtonStyle"];
        var baseStyle = (Style)Application.Current.Resources["BaseButtonStyle"];

        bool structureMatches = CategoryFilterBar.Children.Count == filters.Count;
        if (structureMatches)
        {
            for (int i = 0; i < filters.Count; i++)
            {
                if (CategoryFilterBar.Children[i] is not Button b ||
                    b.Tag is not string tag ||
                    !string.Equals(tag, filters[i].Name, StringComparison.OrdinalIgnoreCase) ||
                    b.Content is not TextBlock tb ||
                    !string.Equals(tb.Text, filters[i].Label, StringComparison.Ordinal))
                {
                    structureMatches = false;
                    break;
                }
            }
        }

        if (structureMatches)
        {
            for (int i = 0; i < filters.Count; i++)
            {
                var btn = (Button)CategoryFilterBar.Children[i];
                bool isSelected = string.Equals(filters[i].Name, active, StringComparison.OrdinalIgnoreCase);
                btn.Style = isSelected ? activeStyle : baseStyle;
                if (btn.Content is TextBlock tb)
                    tb.FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium;
            }
            return;
        }

        CategoryFilterBar.Children.Clear();
        foreach (var f in filters)
        {
            bool isSelected = string.Equals(f.Name, active, StringComparison.OrdinalIgnoreCase);
            var label = new TextBlock
            {
                Text = f.Label,
                FontSize = 12,
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            var btn = new Button
            {
                Style = isSelected ? activeStyle : baseStyle,
                Content = label,
                Tag = f.Name,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(4),
            };
            btn.Click += (_, _) => SwitchGalleryFilter(f.Name);
            CategoryFilterBar.Children.Add(btn);
        }
    }

    private void SwitchGalleryFilter(string filterName)
    {
        if (string.Equals(filterName, _library.ActiveFilter, StringComparison.OrdinalIgnoreCase)) return;
        _library.SetFilter(filterName);
        UpdateGalleryChrome();
        if (GalleryGrid.Visibility == Visibility.Visible) FadeIn(GalleryGrid);
    }

    private async Task RefreshGalleryAsync()
    {
        if (_galleryRefreshing) { _galleryRefreshAgain = true; return; }
        _galleryRefreshing = true;
        try { await _library.RefreshAsync(_settings.GetEffectiveSaveDestination()); }
        catch (Exception ex) { Logger.Warn($"gallery refresh failed: {ex.Message}"); }
        finally { _galleryRefreshing = false; }

        UpdateGalleryChrome();
        if (_galleryRefreshAgain) { _galleryRefreshAgain = false; _ = RefreshGalleryAsync(); }
    }

    private void GalleryGrid_Loaded(object sender, RoutedEventArgs e) => UpdateGalleryColumns();

    private void GalleryGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 0.5) UpdateGalleryColumns();
    }

    private void UpdateGalleryColumns()
    {
        double width = GalleryGrid.ActualWidth;
        if (width < 1 || GalleryGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        const int minimumSlotWidth = 260;
        const int slotPadding = 12;
        const int footerHeight = 60;
        int columns = Math.Clamp((int)(width / minimumSlotWidth), 1, 3);
        double slotWidth = Math.Floor(width / columns);
        panel.MaximumRowsOrColumns = columns;
        panel.ItemWidth = slotWidth;
        panel.ItemHeight = Math.Round((slotWidth - slotPadding) * 9 / 16) + footerHeight + slotPadding;
    }

    private void GalleryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReplayItem item) OpenViewer(item);
    }

    private static T? FindVisualChildByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            var nested = FindVisualChildByName<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private void GalleryGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is not GridViewItem card) return;

        if (!_hoverWired.Add(card)) return;
        card.PointerEntered += (_, _) => AnimateCardHover(card, true);
        card.PointerExited += (_, _) => AnimateCardHover(card, false);
        card.PointerCaptureLost += (_, _) => AnimateCardHover(card, false);
    }

    private void AnimateCardHover(GridViewItem card, bool hover)
    {
        if (!_uiSettings.AnimationsEnabled) return;
        try
        {
            if (FindVisualChildByName<Microsoft.UI.Xaml.Shapes.Rectangle>(card, "ThumbHoverScrim") is { } scrim)
                AnimateDouble(scrim, UIElement.OpacityProperty, hover ? 1.0 : 0.0, 150);

            if (FindVisualChildByName<Border>(card, "PlayOrb") is { } playOrb)
                AnimateDouble(playOrb, UIElement.OpacityProperty, hover ? 1.0 : 0.90, 150);

            if (FindVisualChildByName<Border>(card, "CardRoot") is { } rootBorder)
                rootBorder.BorderBrush = hover ? GetThemedBrush("ControlElevationBorderBrush") : GetThemedBrush("SurfaceBorderBrush");
        }
        catch { }
    }

    private void CardOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ReplayItem item })
            OpenContainingFolder(item.VideoPath);
    }

    private async void CardDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReplayItem item }) return;
        if (ReferenceEquals(_viewerItem, item)) StopViewerPlayback(destroy: true);
        bool deleted = await _library.DeleteAsync(item);
        UpdateGalleryChrome();
        if (deleted)
            ShowInfoBar(InfoBarSeverity.Success, "Replay deleted", "Moved to the recycle bin.");
        else
            ShowInfoBar(InfoBarSeverity.Error, "Couldn't delete replay", "The clip may be open in another program.");
    }

    // ==================== INLINE & FULLSCREEN VIDEO PLAYER ====================

    private async void OpenViewer(ReplayItem item)
    {
        StopViewerPlayback(destroy: false);
        _viewerItem = item;
        _playerTime = 0;
        _playerPlaying = true;
        _inlineVideoRevealed = false;

        AnimateDouble(InlineWebHost, UIElement.OpacityProperty, 0, 0);
        ViewerPoster.Source = null;
        ViewerPoster.Visibility = Visibility.Collapsed;
        ViewerCategoryText.Text = item.BadgeCategory;
        ViewerMetaText.Text = $"{item.MetaLineText} · {item.SizeText}";

        NavigateTo(ViewerPage);
        int generation = _viewerGeneration;
        if (!item.MetadataLoaded) await _library.EnsureAsync(item);
        if (generation != _viewerGeneration || !ReferenceEquals(_currentPage, ViewerPage)) return;
        _playerAspect = item.VideoWidth > 0 && item.VideoHeight > 0 ? (double)item.VideoWidth / item.VideoHeight : 16.0 / 9.0;
        InitializeEditorForItem(item);
        UpdateInlinePlayerBounds();
        await OpenInlinePlayerAsync(item, generation);
    }

    private void RevealInlineVideo(int generation)
    {
        if (generation != _viewerGeneration || _inlineVideoRevealed || !ReferenceEquals(_currentPage, ViewerPage)) return;
        _inlineVideoRevealed = true;
        AnimateDouble(InlineWebHost, UIElement.OpacityProperty, 1, 170);
    }

    private async void ScheduleVideoRevealFallback(int generation)
    {
        await Task.Delay(900);
        if (generation == _viewerGeneration)
            RevealInlineVideo(generation);
    }

    private async Task OpenInlinePlayerAsync(ReplayItem item, int generation)
    {
        try
        {
            if (!File.Exists(item.VideoPath)) throw new FileNotFoundException("Replay file not found.");

            // Reuse warm WebView2 instance when already loaded so switching clips never flashes blurry startup
            if (_inlinePlayer?.CoreWebView2 != null && _inlinePageReady)
            {
                string srcUri = new Uri(item.VideoPath).AbsoluteUri;
                string payload = JsonSerializer.Serialize(new
                {
                    c = "load",
                    src = srcUri,
                    t = 0.0,
                    autoplay = true,
                    v = _playerVolume,
                    m = _playerMuted,
                });
                SendAccentInline();
                ScheduleVideoRevealFallback(generation);
                SendScriptInline($"receiveCommand({payload})");
                _inlinePlayer.Focus(FocusState.Programmatic);
                return;
            }

            if (_inlineWarming)
            {
                while (_inlineWarming && generation == _viewerGeneration) await Task.Delay(25);
                if (generation != _viewerGeneration || !ReferenceEquals(_currentPage, ViewerPage)) return;
                if (_inlinePlayer?.CoreWebView2 != null && _inlinePageReady)
                {
                    await OpenInlinePlayerAsync(item, generation);
                    return;
                }
            }

            _inlineWarming = true;
            var web = _inlinePlayer ?? new WebView2 { DefaultBackgroundColor = Colors.Transparent };
            _inlinePlayer = web;
            if (!InlineWebHost.Children.Contains(web))
                InlineWebHost.Children.Add(web);

            await PlayerWebViewShared.WaitForLoadedAsync(web, TimeSpan.FromSeconds(6));
            try
            {
                _webEnvTask ??= CreateWebViewEnvironmentAsync();
                var env = await _webEnvTask;
                await web.EnsureCoreWebView2Async(env);
            }
            catch
            {
                await web.EnsureCoreWebView2Async();
            }

            if (generation != _viewerGeneration || !ReferenceEquals(web, _inlinePlayer)) return;
            var core = web.CoreWebView2!;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += (_, e) =>
            {
                if (ReferenceEquals(web, _inlinePlayer))
                    HandlePlayerMessage(PlayerWebViewShared.ToPlainJson(e.WebMessageAsJson ?? ""), _isVideoFullscreen);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!ReferenceEquals(web, _inlinePlayer)) return;
                _inlineWarming = false;
                if (e.IsSuccess)
                {
                    _inlinePageReady = true;
                    SendAccentInline();
                    if (_isEditingVideo) SyncEditorStateToPlayer();
                    ScheduleVideoRevealFallback(generation);
                }
                else
                {
                    _inlinePageReady = false;
                    RevealInlineVideo(generation);
                    ShowInfoBar(InfoBarSeverity.Error, "Couldn't play replay", "The media player could not load this file.");
                }
            };
            core.Navigate(BuildPlayerUrl(item.VideoPath, _playerTime, false));
            web.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            _inlineWarming = false;
            Logger.Error("inline player failed", ex);
            if (generation == _viewerGeneration)
                ShowInfoBar(InfoBarSeverity.Error, "Couldn't play replay", "Playback requires the WebView2 Runtime.");
        }
    }

    private void PlayerViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateInlinePlayerBounds();

    private void UpdateInlinePlayerBounds()
    {
        if (PlayerViewport == null || PlayerCard == null || InlinePlayerHost == null) return;
        double availableWidth = PlayerViewport.ActualWidth;
        double availableHeight = PlayerViewport.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0 || _playerAspect <= 0) return;

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(InlinePlayerHost);
        if (_isVideoFullscreen)
        {
            PlayerCard.Width = availableWidth;
            PlayerCard.Height = availableHeight;
            visual.Clip = null;
            return;
        }

        double width = Math.Min(availableWidth, availableHeight * _playerAspect);
        double height = width / _playerAspect;
        PlayerCard.Width = width;
        PlayerCard.Height = height;
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)width, (float)height);
        geometry.CornerRadius = new Vector2(8);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }

    private void SendAccentInline() => SendScriptInline($"receiveCommand({{\"c\":\"accent\",\"v\":\"{GetAccentHex()}\"}})");

    private string GetAccentHex() => _settings.DarkMode ? "#B8BECB" : "#505562";

    private void SendScriptInline(string script)
    {
        try { if (_inlinePlayer?.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync(script); }
        catch { }
    }

    private void ReadPlayerState(JsonElement state)
    {
        if (state.TryGetProperty("width", out var width) && state.TryGetProperty("height", out var height)
            && width.TryGetInt32(out int w) && height.TryGetInt32(out int h) && w > 0 && h > 0)
        {
            double aspect = (double)w / h;
            if (Math.Abs(aspect - _playerAspect) > 0.001) { _playerAspect = aspect; UpdateInlinePlayerBounds(); }
            if (_cropSrcW != w || _cropSrcH != h)
            {
                _cropSrcW = w;
                _cropSrcH = h;
                if (_cropW <= 0 || _cropH <= 0)
                {
                    _cropX = 0;
                    _cropY = 0;
                    _cropW = w & ~1;
                    _cropH = h & ~1;
                }
                UpdateCropInfoDisplay();
            }
        }
        if (state.TryGetProperty("d", out var d) && d.TryGetDouble(out double dur) && double.IsFinite(dur) && dur > 0)
        {
            if (Math.Abs(dur - _playerDuration) > 0.05)
            {
                bool wasAtEnd = _trimEndSec <= 0 || Math.Abs(_trimEndSec - _playerDuration) < 0.15;
                _playerDuration = dur;
                if (wasAtEnd || _trimEndSec > dur) _trimEndSec = dur;
                _trimStartSec = Math.Clamp(_trimStartSec, 0, Math.Max(0, _trimEndSec - 0.15));
                UpdateTrimTrackVisuals();
            }
        }
        bool playerSeeking = state.TryGetProperty("seeking", out var seekingEl) && seekingEl.ValueKind == JsonValueKind.True;
        if (state.TryGetProperty("t", out var t) && t.TryGetDouble(out double seconds) && double.IsFinite(seconds))
        {
            if (_trimDragTarget == TrimDragTarget.None && !playerSeeking && DateTime.UtcNow >= _trimSeekHoldUntilUtc)
            {
                _playerTime = Math.Max(seconds, 0);
                if (_isEditingVideo) UpdateTrimTrackVisuals();
            }
        }
        if (state.TryGetProperty("playing", out var playing))
        {
            _playerPlaying = playing.ValueKind == JsonValueKind.True;
            UpdateEditPlayPauseIcon();
        }
        if (state.TryGetProperty("v", out var volume) && volume.TryGetDouble(out double value) && double.IsFinite(value)) _playerVolume = Math.Clamp(value, 0, 1);
        if (state.TryGetProperty("m", out var mute)) _playerMuted = mute.ValueKind == JsonValueKind.True;
    }

    private void ViewerBackButton_Click(object sender, RoutedEventArgs e) => CloseViewer();

    private void ViewerEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExportingVideo || _viewerItem == null) return;
        SetEditMode(!_isEditingVideo);
    }

    private void ViewerOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerItem != null) OpenContainingFolder(_viewerItem.VideoPath);
    }

    private void CloseViewer()
    {
        try { _exportCts?.Cancel(); } catch { }
        StopViewerPlayback(destroy: true);
        NavigateTo(HomePage, fadeOnly: true);
    }

    private void StopViewerPlayback(bool destroy = false)
    {
        _viewerGeneration++;
        _inlineVideoRevealed = false;
        try { _exportCts?.Cancel(); } catch { }
        SetEditMode(false, sendToPlayer: false);
        ExitVideoFullscreen();
        AnimateDouble(InlineWebHost, UIElement.OpacityProperty, 0, 0);

        if (destroy)
        {
            _inlinePageReady = false;
            _inlineWarming = false;
            var web = _inlinePlayer;
            _inlinePlayer = null;
            if (web != null) { InlineWebHost.Children.Remove(web); try { web.Close(); } catch { } }
        }
        else if (_inlinePlayer?.CoreWebView2 != null && _inlinePageReady)
        {
            SendScriptInline("receiveCommand({\"c\":\"unload\"})");
        }
    }

    private async void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        ReplayItem? item = _viewerItem;
        if (item == null) return;
        StopViewerPlayback(destroy: true);
        await Task.Delay(220);

        bool deleted = await _library.DeleteAsync(item);
        UpdateGalleryChrome();
        if (deleted)
        {
            _viewerItem = null;
            NavigateTo(HomePage, fadeOnly: true);
            ShowInfoBar(InfoBarSeverity.Success, "Replay deleted", "Moved to the recycle bin.");
        }
        else
        {
            ShowInfoBar(InfoBarSeverity.Error, "Couldn't delete replay", "The clip may still be in use.");
        }
    }

    private void EnterVideoFullscreen()
    {
        if (_isVideoFullscreen || _viewerItem == null) return;
        try
        {
            _isVideoFullscreen = true;
            if (FloatingHeader != null) FloatingHeader.Visibility = Visibility.Collapsed;
            if (ViewerToolbar != null) ViewerToolbar.Visibility = Visibility.Collapsed;
            if (ViewerPage != null)
            {
                ViewerPage.Padding = new Thickness(0);
                ViewerPage.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            }
            if (PlayerCard != null)
            {
                PlayerCard.BorderThickness = new Thickness(0);
                PlayerCard.CornerRadius = new CornerRadius(0);
                PlayerCard.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            }

            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            UpdateTitleBarDragRegions();
            UpdateInlinePlayerBounds();
            if (_inlinePageReady) SendScriptInline("receiveCommand({\"c\":\"fsState\",\"fullscreen\":true})");
            _inlinePlayer?.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Logger.Warn($"fullscreen enter failed: {ex.Message}");
            ExitVideoFullscreen();
        }
    }

    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_capturingHotkey || !ReferenceEquals(_currentPage, ViewerPage)) return;
        if (ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), _inlinePlayer)) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_isVideoFullscreen) ExitVideoFullscreen();
            else if (_isEditingVideo) SetEditMode(false);
            else CloseViewer();
        }
        else if (e.Key is Windows.System.VirtualKey.F or Windows.System.VirtualKey.F11)
        {
            if (_isEditingVideo) return;
            e.Handled = true;
            if (_isVideoFullscreen) ExitVideoFullscreen();
            else EnterVideoFullscreen();
        }
    }

    private void ExitVideoFullscreen()
    {
        if (!_isVideoFullscreen) return;
        _isVideoFullscreen = false;
        try
        {
            if (_appWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped)
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        catch { }

        if (FloatingHeader != null) FloatingHeader.Visibility = Visibility.Visible;
        if (ViewerToolbar != null) ViewerToolbar.Visibility = Visibility.Visible;
        if (ViewerPage != null)
        {
            ViewerPage.Padding = new Thickness(24, 68, 24, 20);
            ViewerPage.Background = GetThemedBrush("BgBrush");
        }
        if (PlayerCard != null)
        {
            PlayerCard.BorderThickness = new Thickness(1);
            PlayerCard.CornerRadius = new CornerRadius(8);
            PlayerCard.Background = GetThemedBrush("SurfaceElevatedBrush");
        }

        UpdateInlinePlayerBounds();
        _dq.TryEnqueue(UpdateTitleBarDragRegions);
        if (_inlinePageReady) SendScriptInline("receiveCommand({\"c\":\"fsState\",\"fullscreen\":false})");
    }

    private string BuildPlayerUrl(string videoPath, double startSeconds, bool fullscreen)
    {
        string page = new Uri(Path.Combine(AppContext.BaseDirectory, "mediaplayer", "player.html")).AbsoluteUri;
        string src = new Uri(videoPath).AbsoluteUri;
        string t = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return $"{page}?src={Uri.EscapeDataString(src)}&t={t}&mode={(fullscreen ? "fullscreen" : "inline")}&autoplay={(_playerPlaying ? 1 : 0)}&volume={_playerVolume.ToString(CultureInfo.InvariantCulture)}&muted={(_playerMuted ? 1 : 0)}";
    }

    private void HandlePlayerMessage(string json, bool fullscreen)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "videoReady":
                    RevealInlineVideo(_viewerGeneration);
                    break;
                case "videoError":
                    int errCode = root.TryGetProperty("code", out var ec) && ec.TryGetInt32(out int cVal) ? cVal : 0;
                    string errMsg = root.TryGetProperty("message", out var em) ? em.GetString() ?? "" : "";
                    string errSrc = root.TryGetProperty("src", out var es) ? es.GetString() ?? "" : "";
                    Logger.Warn($"videoError code={errCode} msg='{errMsg}' src='{errSrc}'");
                    break;
                case "state": ReadPlayerState(root); break;
                case "fs":
                    string act = root.TryGetProperty("act", out var ae) ? ae.GetString() ?? "" : "";
                    if (act == "Exit") ExitVideoFullscreen();
                    else if (act == "enter" && _viewerItem != null && !_isEditingVideo) EnterVideoFullscreen();
                    break;
                case "crop":
                    if (root.TryGetProperty("x", out var cx) && cx.TryGetInt32(out int x)) _cropX = x;
                    if (root.TryGetProperty("y", out var cy) && cy.TryGetInt32(out int y)) _cropY = y;
                    if (root.TryGetProperty("w", out var cw) && cw.TryGetInt32(out int w)) _cropW = w;
                    if (root.TryGetProperty("h", out var ch) && ch.TryGetInt32(out int h)) _cropH = h;
                    if (root.TryGetProperty("srcW", out var sw) && sw.TryGetInt32(out int srcW)) _cropSrcW = srcW;
                    if (root.TryGetProperty("srcH", out var sh) && sh.TryGetInt32(out int srcH)) _cropSrcH = srcH;
                    UpdateCropInfoDisplay();
                    break;
                case "close":
                    if (fullscreen || _isVideoFullscreen) ExitVideoFullscreen();
                    else if (_isEditingVideo) SetEditMode(false);
                    else CloseViewer();
                    break;
            }
        }
        catch { }
    }

    // ==================== VIDEO TRIM & CROP EDITOR ====================

    private void InitializeEditorForItem(ReplayItem item)
    {
        _isEditingVideo = false;
        _trimDragTarget = TrimDragTarget.None;
        _playerDuration = Math.Max(0, item.Duration.TotalSeconds);
        _trimStartSec = 0;
        _trimEndSec = _playerDuration;
        _cropAspectSpec = "free";
        _cropSrcW = Math.Max(0, item.VideoWidth);
        _cropSrcH = Math.Max(0, item.VideoHeight);
        _cropX = 0;
        _cropY = 0;
        _cropW = _cropSrcW & ~1;
        _cropH = _cropSrcH & ~1;
        UpdateEditChrome();
        UpdateCropRatioButtonsVisual();
        UpdateCropInfoDisplay();
        UpdateTrimTrackVisuals();
    }

    private void SetEditMode(bool enabled, bool sendToPlayer = true)
    {
        if (enabled && _isVideoFullscreen) ExitVideoFullscreen();
        _isEditingVideo = enabled;
        _trimDragTarget = TrimDragTarget.None;
        if (enabled) _playerPlaying = false;
        UpdateEditChrome();

        if (sendToPlayer && _inlinePageReady)
        {
            if (enabled) SyncEditorStateToPlayer();
            else SendScriptInline("receiveCommand({\"c\":\"editMode\",\"on\":false})");
        }
        _dq.TryEnqueue(UpdateInlinePlayerBounds);
    }

    private void UpdateEditChrome()
    {
        if (EditorBar != null)
            EditorBar.Visibility = _isEditingVideo ? Visibility.Visible : Visibility.Collapsed;
        if (ViewerEditButton != null)
            ViewerEditButton.Style = (Style)Application.Current.Resources[_isEditingVideo ? "FilterActiveButtonStyle" : "BaseButtonStyle"];
        if (ViewerEditText != null)
            ViewerEditText.Text = _isEditingVideo ? "done" : "edit";
        UpdateEditPlayPauseIcon();
        UpdateTrimTrackVisuals();
    }

    private void SyncEditorStateToPlayer()
    {
        if (!_inlinePageReady) return;
        SendScriptInline("receiveCommand({\"c\":\"editMode\",\"on\":true})");
        SendTrimRangeToPlayer(seekSeconds: null);
        if (_cropAspectSpec != "free")
            SendScriptInline($"receiveCommand({{\"c\":\"cropAspect\",\"ratio\":\"{_cropAspectSpec}\"}})");
    }

    private void SendTrimRangeToPlayer(double? seekSeconds, bool isPreview = false)
    {
        if (!_inlinePageReady || _playerDuration <= 0) return;
        string s = _trimStartSec.ToString("0.###", CultureInfo.InvariantCulture);
        string e = _trimEndSec.ToString("0.###", CultureInfo.InvariantCulture);
        if (seekSeconds is { } sk)
        {
            string k = sk.ToString("0.###", CultureInfo.InvariantCulture);
            string prev = isPreview ? ",\"preview\":true" : "";
            SendScriptInline($"receiveCommand({{\"c\":\"trimRange\",\"start\":{s},\"end\":{e},\"seek\":{k}{prev}}})");
        }
        else
        {
            SendScriptInline($"receiveCommand({{\"c\":\"trimRange\",\"start\":{s},\"end\":{e}}})");
        }
    }

    private void QueueTrimPreviewSeek(double targetSec)
    {
        _pendingTrimSeekSec = targetSec;
        _trimSeekHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
        if ((DateTime.UtcNow - _lastTrimPreviewUtc).TotalMilliseconds >= 40)
        {
            FlushPendingTrimPreview();
            return;
        }
        if (_trimPreviewTimer == null)
        {
            _trimPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(36) };
            _trimPreviewTimer.Tick += (_, _) => FlushPendingTrimPreview();
        }
        if (!_trimPreviewTimer.IsEnabled) _trimPreviewTimer.Start();
    }

    private void FlushPendingTrimPreview()
    {
        _trimPreviewTimer?.Stop();
        if (_pendingTrimSeekSec is not { } sec) return;
        _pendingTrimSeekSec = null;
        _lastTrimPreviewUtc = DateTime.UtcNow;
        SendTrimRangeToPlayer(sec, isPreview: true);
    }

    private void UpdateEditPlayPauseIcon()
    {
        if (EditPlayIcon == null || EditPauseIcon == null) return;
        EditPlayIcon.Visibility = _playerPlaying ? Visibility.Collapsed : Visibility.Visible;
        EditPauseIcon.Visibility = _playerPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        SendScriptInline("receiveCommand({\"c\":\"togglePlay\"})");
    }

    private void TrimTrackGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrimTrackVisuals();

    private void TrimTrackGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isEditingVideo || _isExportingVideo || _playerDuration <= 0.1 || TrimTrackGrid == null) return;
        var pt = e.GetCurrentPoint(TrimTrackGrid);
        if (!pt.Properties.IsLeftButtonPressed) return;

        double trackW = TrimTrackGrid.ActualWidth;
        double usableW = Math.Max(1.0, trackW - 12.0);
        double x = pt.Position.X;

        double startCenter = (_trimStartSec / _playerDuration) * usableW + 6.0;
        double endCenter = (_trimEndSec / _playerDuration) * usableW + 6.0;
        double dStart = Math.Abs(x - startCenter);
        double dEnd = Math.Abs(x - endCenter);

        if (dStart <= 12 && dStart <= dEnd) _trimDragTarget = TrimDragTarget.Start;
        else if (dEnd <= 12) _trimDragTarget = TrimDragTarget.End;
        else _trimDragTarget = TrimDragTarget.Playhead;

        TrimTrackGrid.CapturePointer(e.Pointer);
        ApplyTrimDragAt(x);
        e.Handled = true;
    }

    private void TrimTrackGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_trimDragTarget == TrimDragTarget.None || TrimTrackGrid == null) return;
        var pt = e.GetCurrentPoint(TrimTrackGrid);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            EndTrimDrag(e.Pointer);
            return;
        }
        ApplyTrimDragAt(pt.Position.X);
        e.Handled = true;
    }

    private void TrimTrackGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_trimDragTarget == TrimDragTarget.None) return;
        EndTrimDrag(e.Pointer);
        e.Handled = true;
    }

    private void EndTrimDrag(Microsoft.UI.Xaml.Input.Pointer? pointer = null)
    {
        if (_trimDragTarget == TrimDragTarget.None) return;
        TrimDragTarget finished = _trimDragTarget;
        _trimDragTarget = TrimDragTarget.None;
        _trimPreviewTimer?.Stop();
        _pendingTrimSeekSec = null;
        _trimSeekHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(400);
        if (pointer != null)
        {
            try { TrimTrackGrid?.ReleasePointerCapture(pointer); } catch { }
        }
        else
        {
            try { TrimTrackGrid?.ReleasePointerCaptures(); } catch { }
        }
        double finalSeek = finished switch
        {
            TrimDragTarget.Start => _trimStartSec,
            TrimDragTarget.End => _trimEndSec,
            _ => _playerTime,
        };
        SendTrimRangeToPlayer(finalSeek, isPreview: false);
        UpdateTrimTrackVisuals();
    }

    private void ApplyTrimDragAt(double x)
    {
        if (_playerDuration <= 0.1 || TrimTrackGrid == null) return;
        double usableW = Math.Max(1.0, TrimTrackGrid.ActualWidth - 12.0);
        double frac = Math.Clamp((x - 6.0) / usableW, 0.0, 1.0);
        double t = frac * _playerDuration;
        double minSpan = Math.Min(0.25, _playerDuration * 0.2);

        switch (_trimDragTarget)
        {
            case TrimDragTarget.Start:
                _trimStartSec = Math.Clamp(t, 0.0, Math.Max(0.0, _trimEndSec - minSpan));
                _playerTime = _trimStartSec;
                QueueTrimPreviewSeek(_trimStartSec);
                break;
            case TrimDragTarget.End:
                _trimEndSec = Math.Clamp(t, Math.Min(_playerDuration, _trimStartSec + minSpan), _playerDuration);
                _playerTime = _trimEndSec;
                QueueTrimPreviewSeek(_trimEndSec);
                break;
            case TrimDragTarget.Playhead:
                double seekT = Math.Clamp(t, _trimStartSec, _trimEndSec);
                _playerTime = seekT;
                QueueTrimPreviewSeek(seekT);
                break;
        }
        UpdateTrimTrackVisuals();
    }

    private void UpdateTrimTrackVisuals()
    {
        if (TrimStartText == null || TrimEndText == null || TrimDurationText == null) return;
        double dur = _playerDuration;
        double start = dur > 0 ? Math.Clamp(_trimStartSec, 0, dur) : 0;
        double end = dur > 0 ? Math.Clamp(_trimEndSec, start, dur) : 0;
        double cur = dur > 0 ? Math.Clamp(_playerTime, 0, dur) : 0;

        TrimStartText.Text = FormatEditorTime(start);
        TrimEndText.Text = FormatEditorTime(end);
        TrimDurationText.Text = $"{Math.Max(0, end - start):0.0}s";

        if (TrimTrackGrid == null || TrimStartHandle == null || TrimEndHandle == null || TrimActiveFill == null || TrimPlayhead == null) return;
        double trackW = TrimTrackGrid.ActualWidth;
        if (trackW <= 12 || dur <= 0) return;

        double usableW = Math.Max(1.0, trackW - 12.0);
        double startPx = (start / dur) * usableW;
        double endPx = (end / dur) * usableW;
        double headPx = Math.Clamp((cur / dur) * usableW + 5.0, 0.0, Math.Max(0.0, trackW - 2.0));

        TrimStartHandle.Margin = new Thickness(startPx, 0, 0, 0);
        TrimEndHandle.Margin = new Thickness(endPx, 0, 0, 0);
        TrimActiveFill.Margin = new Thickness(startPx + 6.0, 0, 0, 0);
        TrimActiveFill.Width = Math.Max(0.0, endPx - startPx);
        TrimPlayhead.Margin = new Thickness(headPx, 0, 0, 0);
    }

    private static string FormatEditorTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        int totalTenths = (int)Math.Round(seconds * 10.0);
        int mins = totalTenths / 600;
        int secs = (totalTenths % 600) / 10;
        int tenths = totalTenths % 10;
        return $"{mins}:{secs:00}.{tenths}";
    }

    private void CropRatioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExportingVideo || sender is not Button btn || btn.Tag is not string ratio) return;
        _cropAspectSpec = ratio;
        UpdateCropRatioButtonsVisual();
        SendScriptInline($"receiveCommand({{\"c\":\"cropAspect\",\"ratio\":\"{ratio}\"}})");
    }

    private void UpdateCropRatioButtonsVisual()
    {
        Button?[] btns = [CropRatioFreeButton, CropRatio169Button, CropRatio43Button, CropRatio916Button, CropRatio11Button];
        var activeStyle = (Style)Application.Current.Resources["FilterActiveButtonStyle"];
        var baseStyle = (Style)Application.Current.Resources["BaseButtonStyle"];
        foreach (Button? b in btns)
        {
            if (b == null) continue;
            bool isActive = string.Equals(b.Tag as string, _cropAspectSpec, StringComparison.OrdinalIgnoreCase);
            b.Style = isActive ? activeStyle : baseStyle;
        }
    }

    private void UpdateCropInfoDisplay()
    {
        if (CropInfoText == null) return;
        if (_cropW > 0 && _cropH > 0)
            CropInfoText.Text = $"{_cropW} × {_cropH}";
        else
            CropInfoText.Text = "—";
    }

    private void EditResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExportingVideo) return;
        _trimStartSec = 0;
        _trimEndSec = _playerDuration;
        _cropAspectSpec = "free";
        _cropX = 0;
        _cropY = 0;
        _cropW = _cropSrcW & ~1;
        _cropH = _cropSrcH & ~1;
        UpdateCropRatioButtonsVisual();
        UpdateCropInfoDisplay();
        UpdateTrimTrackVisuals();
        SendTrimRangeToPlayer(0);
        SendScriptInline("receiveCommand({\"c\":\"cropReset\"})");
    }

    private void EditCancelExportButton_Click(object sender, RoutedEventArgs e)
    {
        try { _exportCts?.Cancel(); } catch { }
    }

    private async void EditSaveCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExportingVideo || _viewerItem is not { } item) return;
        if (!File.Exists(item.VideoPath))
        {
            ShowInfoBar(InfoBarSeverity.Error, "Couldn't export clip", "The original replay file is missing.");
            return;
        }

        _isExportingVideo = true;
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;

        SetExportingUiState(true);
        SendScriptInline("receiveCommand({\"c\":\"pause\"})");

        string outPath = VideoEditor.BuildOutputPath(item);
        VideoCropRect? crop = (_cropW > 0 && _cropH > 0 && _cropSrcW > 0 && _cropSrcH > 0
            && (_cropX > 0 || _cropY > 0 || _cropW < (_cropSrcW & ~1) || _cropH < (_cropSrcH & ~1)))
            ? new VideoCropRect(_cropX, _cropY, _cropW, _cropH)
            : null;

        double totalDur = _playerDuration > 0 ? _playerDuration : item.Duration.TotalSeconds;
        double start = Math.Clamp(_trimStartSec, 0, Math.Max(0, totalDur - 0.15));
        double end = totalDur > 0.15 ? Math.Clamp(_trimEndSec, start + 0.15, totalDur) : Math.Max(0.15, totalDur);

        var progress = new Progress<double>(pct =>
        {
            _dq.TryEnqueue(() =>
            {
                if (EditExportProgressText != null)
                    EditExportProgressText.Text = $"saving {Math.Clamp(pct, 0, 100):0}%";
            });
        });

        try
        {
            await VideoEditor.ExportAsync(new VideoExportRequest(item.VideoPath, outPath, start, end, crop), progress, ct);
            await RefreshGalleryAsync();
            ShowInfoBar(InfoBarSeverity.Success, "Saved new clip", $"{Path.GetFileName(outPath)} · {(crop?.Width ?? (_cropSrcW & ~1))}×{(crop?.Height ?? (_cropSrcH & ~1))} · {end - start:0.0}s");
        }
        catch (OperationCanceledException)
        {
            ShowInfoBar(InfoBarSeverity.Informational, "Export canceled", "No separate clip was created.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"video trim/crop export failed: {ex.Message}");
            ShowInfoBar(InfoBarSeverity.Error, "Export failed", ex.Message);
        }
        finally
        {
            _isExportingVideo = false;
            SetExportingUiState(false);
        }
    }

    private void SetExportingUiState(bool exporting)
    {
        if (EditSaveCopyButton != null) EditSaveCopyButton.IsEnabled = !exporting;
        if (EditResetButton != null) EditResetButton.IsEnabled = !exporting;
        if (ViewerEditButton != null) ViewerEditButton.IsEnabled = !exporting;
        if (ViewerDeleteButton != null) ViewerDeleteButton.IsEnabled = !exporting;
        if (EditExportProgressText != null)
        {
            EditExportProgressText.Text = "saving 0%";
            EditExportProgressText.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        }
        if (EditCancelExportButton != null)
            EditCancelExportButton.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== SETTINGS CONTROLS ====================

    private void RefreshMicrophonesInBackground()
    {
        _ = Task.Run(() =>
        {
            List<string> mics;
            string defaultMic;
            try
            {
                mics = _engine.EnumerateMicrophones();
                defaultMic = _engine.GetDefaultMicrophoneName();
            }
            catch { return; }

            _dq.TryEnqueue(() =>
            {
                string prevDefault = _defaultMicName;
                _knownMics = mics;
                _defaultMicName = defaultMic;
                _syncingMicCombo = true;
                try
                {
                    bool configuredMissing = !string.IsNullOrEmpty(_settings.Microphone) &&
                                             !mics.Contains(_settings.Microphone, StringComparer.OrdinalIgnoreCase);
                    _micMissing = configuredMissing;

                    var choices = new List<string>();
                    var displayItems = new List<string>();

                    string autoLabel = string.IsNullOrEmpty(defaultMic)
                        ? "auto · windows default"
                        : $"auto · {defaultMic.ToLowerInvariant()}";
                    choices.Add("");
                    displayItems.Add(autoLabel);

                    foreach (string m in mics)
                    {
                        choices.Add(m);
                        bool isDef = !string.IsNullOrEmpty(defaultMic) &&
                                     string.Equals(m, defaultMic, StringComparison.OrdinalIgnoreCase);
                        displayItems.Add(isDef ? $"{m.ToLowerInvariant()} · default" : m.ToLowerInvariant());
                    }

                    if (configuredMissing)
                    {
                        choices.Add(_settings.Microphone);
                        displayItems.Add($"{_settings.Microphone.ToLowerInvariant()} · disconnected");
                    }

                    _micChoices = choices;
                    MicCombo.ItemsSource = displayItems;
                    MicCombo.IsEnabled = mics.Count > 0 || configuredMissing;

                    if (string.IsNullOrEmpty(_settings.Microphone))
                    {
                        MicCombo.SelectedIndex = 0;
                    }
                    else
                    {
                        int idx = choices.FindIndex(1, c => string.Equals(c, _settings.Microphone, StringComparison.OrdinalIgnoreCase));
                        MicCombo.SelectedIndex = idx >= 0 ? idx : 0;
                    }
                }
                finally { _syncingMicCombo = false; }

                UpdateMicLiveStatus();

                // If auto mode is active and Windows default capture mic changed while recording, rebind cleanly
                if (string.IsNullOrEmpty(_settings.Microphone) &&
                    !string.IsNullOrEmpty(prevDefault) &&
                    !string.Equals(prevDefault, defaultMic, StringComparison.OrdinalIgnoreCase) &&
                    _engine.IsRunning && _settings.RecordMicrophone)
                {
                    _ = Task.Run(() => _engine.Restart(_tempDir, wipeSegments: false));
                }
            });
        });
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingMicCombo) return;
        int idx = MicCombo.SelectedIndex;
        if (idx < 0 || idx >= _micChoices.Count) return;
        string mic = _micChoices[idx];
        _settings.Microphone = mic;
        _micMissing = !string.IsNullOrEmpty(mic) && !_knownMics.Contains(mic, StringComparer.OrdinalIgnoreCase);
        CheckDirty();
        UpdateMicLiveStatus();
    }

    private void UpdateMicLiveStatus()
    {
        if (!_isSettingsActive || ReferenceEquals(_currentPage, ViewerPage) || _isHidden)
            return;

        if (_engine.IsRunning && _settings.RecordMicrophone && !_dirty &&
            _engine.TryGetMicStatus(out _, out _, out _, out long failHr, out _, out int micPeak, out string activeName))
        {
            if (failHr == 0 && !string.IsNullOrEmpty(activeName))
            {
                double target = Math.Clamp(micPeak, 0, 100) / 100.0;
                AnimateDouble(MicLevelBarScale, ScaleTransform.ScaleXProperty, target, 210);
                MicStatusLabel.Text = $"active · {activeName.ToLowerInvariant()}";
                MicLiveDot.Fill = GetThemedBrush(micPeak >= 8 ? "AccentIndicatorBrush" : "SuccessBrush");
                return;
            }
        }

        if (_micProbeInFlight) return;
        _micProbeInFlight = true;
        string wanted = _settings.Microphone;

        _ = Task.Run(() =>
        {
            bool ok = false;
            string active = "";
            int peak = 0;
            try { ok = _engine.ProbeMicState(string.IsNullOrEmpty(wanted) ? null : wanted, detectSignal: false, out active, out peak); }
            catch { }

            _dq.TryEnqueue(() =>
            {
                _micProbeInFlight = false;
                if (ok && !string.IsNullOrEmpty(active))
                {
                    double target = Math.Clamp(peak, 0, 100) / 100.0;
                    AnimateDouble(MicLevelBarScale, ScaleTransform.ScaleXProperty, target, 210);
                    MicStatusLabel.Text = $"active · {active.ToLowerInvariant()}";
                    MicLiveDot.Fill = GetThemedBrush(peak >= 8 ? "AccentIndicatorBrush" : "SuccessBrush");
                }
                else if (_knownMics.Count == 0)
                {
                    MicLevelBarScale.ScaleX = 0;
                    MicStatusLabel.Text = "no active microphone detected";
                    MicLiveDot.Fill = GetThemedBrush("ErrorBrush");
                }
                else
                {
                    MicLevelBarScale.ScaleX = 0;
                    MicStatusLabel.Text = "selected mic disconnected · click detect active";
                    MicLiveDot.Fill = GetThemedBrush("ErrorBrush");
                }
            });
        });
    }

    private async void DetectMicButton_Click(object sender, RoutedEventArgs e)
    {
        DetectMicButton.IsEnabled = false;
        MicStatusLabel.Text = "scanning active microphones...";
        try
        {
            var (ok, detected, peak, mics, defMic) = await Task.Run(() =>
            {
                bool found = _engine.ProbeMicState(null, detectSignal: true, out string name, out int pct);
                var list = _engine.EnumerateMicrophones();
                string def = _engine.GetDefaultMicrophoneName();
                return (found, name, pct, list, def);
            });

            _knownMics = mics;
            _defaultMicName = defMic;

            if (ok && !string.IsNullOrEmpty(detected))
            {
                _settings.Microphone = string.Equals(detected, defMic, StringComparison.OrdinalIgnoreCase) ? "" : detected;
                RefreshMicrophonesInBackground();
                CheckDirty();
                double target = Math.Clamp(peak, 0, 100) / 100.0;
                AnimateDouble(MicLevelBarScale, ScaleTransform.ScaleXProperty, target, 210);
                MicStatusLabel.Text = peak >= 4
                    ? $"signal detected · {detected.ToLowerInvariant()}"
                    : $"active default · {detected.ToLowerInvariant()}";
                MicLiveDot.Fill = GetThemedBrush("SuccessBrush");
            }
            else
            {
                MicLevelBarScale.ScaleX = 0;
                MicStatusLabel.Text = "no active microphone found";
                MicLiveDot.Fill = GetThemedBrush("ErrorBrush");
            }
        }
        finally
        {
            DetectMicButton.IsEnabled = true;
        }
    }

    private void InitDurationSegments()
    {
        DurationCombo.SelectedIndex = _settings.ReplayDurationSeconds switch
        {
            30 => 0,
            60 => 1,
            120 => 2,
            _ => 3
        };
        _syncingDurationCustom = true;
        DurationCustomBox.Text = _settings.ReplayDurationSeconds.ToString(CultureInfo.InvariantCulture);
        _syncingDurationCustom = false;
        SetDurationCustomVisible(DurationCombo.SelectedIndex == 3, animate: false);
    }

    private void DurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        bool isCustom = DurationCombo.SelectedIndex == 3;
        if (isCustom)
        {
            int secs = Math.Clamp(_settings.ReplayDurationSeconds, 5, 600);
            _settings.ReplayDurationSeconds = secs;
            _syncingDurationCustom = true;
            DurationCustomBox.Text = secs.ToString(CultureInfo.InvariantCulture);
            _syncingDurationCustom = false;
            SetDurationCustomVisible(true, animate: true);
        }
        else
        {
            _settings.ReplayDurationSeconds = DurationCombo.SelectedIndex switch
            {
                0 => 30,
                1 => 60,
                2 => 120,
                _ => 60
            };
            SetDurationCustomVisible(false, animate: true);
        }
        CheckDirty();
    }

    private void SetDurationCustomVisible(bool show, bool animate)
    {
        const double customSlot = 104;
        double from = DurationCustomWrap.Visibility == Visibility.Visible ? DurationCustomWrap.ActualWidth : 0;
        double fromOpacity = from > 0 ? DurationCustomContent.Opacity : 0;
        _durationDisclosure?.Stop();
        _durationDisclosure = null;
        _durationCustomVisible = show;
        DurationCustomWrap.IsHitTestVisible = show;
        DurationCustomBox.IsTabStop = show;
        double end = show ? customSlot : 0;

        if (!animate || !_uiSettings.AnimationsEnabled || (Math.Abs(from - end) < 0.5 && fromOpacity == (show ? 1 : 0)))
        {
            DurationCustomWrap.Width = end;
            DurationCustomContent.Opacity = show ? 1 : 0;
            DurationCustomWrap.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // Keep the complete input visible while fading out. Only then reclaim its width.
        DurationCustomWrap.Width = from;
        DurationCustomContent.Opacity = fromOpacity;
        DurationCustomWrap.Visibility = Visibility.Visible;
        bool resize = Math.Abs(end - from) >= 0.5;
        double fadeMs = show || fromOpacity > 0.01 ? 100 : 0;
        double resizeMs = resize ? 200 : 0;
        var transition = new Storyboard();
        if (resize) AddMotion(transition, DurationCustomWrap, "Width", from, end, show ? 0 : fadeMs, resizeMs, layout: true);
        else DurationCustomWrap.Width = end;
        if (fadeMs > 0) AddMotion(transition, DurationCustomContent, "Opacity", fromOpacity, show ? 1 : 0, show ? resizeMs : 0, fadeMs);
        else DurationCustomContent.Opacity = 0;
        transition.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_durationDisclosure, transition)) return;
            DurationCustomWrap.Width = end;
            DurationCustomContent.Opacity = show ? 1 : 0;
            if (!show) DurationCustomWrap.Visibility = Visibility.Collapsed;
            transition.Stop();
            _durationDisclosure = null;
        };
        _durationDisclosure = transition;
        transition.Begin();
    }

    private void DurationCustomBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _syncingDurationCustom || !_durationCustomVisible) return;
        string raw = DurationCustomBox.Text ?? "";
        string digits = new(raw.Where(char.IsDigit).ToArray());
        if (digits != raw)
        {
            _syncingDurationCustom = true;
            int caret = DurationCustomBox.SelectionStart;
            DurationCustomBox.Text = digits;
            DurationCustomBox.SelectionStart = Math.Min(caret, digits.Length);
            _syncingDurationCustom = false;
        }
        if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs) && secs is >= 5 and <= 600)
        {
            _settings.ReplayDurationSeconds = secs;
            CheckDirty();
        }
    }

    private void DurationCustomBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingDurationCustom || !_durationCustomVisible) return;
        int parsed = int.TryParse(DurationCustomBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, 5, 600)
            : Math.Clamp(_settings.ReplayDurationSeconds, 5, 600);
        _settings.ReplayDurationSeconds = parsed;
        _syncingDurationCustom = true;
        DurationCustomBox.Text = parsed.ToString(CultureInfo.InvariantCulture);
        _syncingDurationCustom = false;
        CheckDirty();
    }

    private void InitFpsSegments() =>
        FpsCombo.SelectedIndex = _settings.FrameRate switch { 60 => 1, 120 => 2, _ => 0 };

    private void FpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.FrameRate = FpsCombo.SelectedIndex switch { 1 => 60, 2 => 120, _ => 30 };
        CheckDirty();
    }

    private void InitQualitySegments() =>
        QualityCombo.SelectedIndex = _settings.QualityPreset switch
        {
            "Balanced" => 1,
            "Quality" or "High" or "Ultra" => 2,
            _ => 0
        };

    private void QualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.QualityPreset = QualityCombo.SelectedIndex switch
        {
            1 => "Balanced",
            2 => "High",
            _ => "Low"
        };
        CheckDirty();
    }

    private void InitAudioToggles()
    {
        _syncingAudioToggles = true;
        SysAudioToggle.IsOn = _settings.RecordSystemAudio;
        MicToggle.IsOn = _settings.RecordMicrophone;
        bool selectedMode = string.Equals(_settings.SystemAudioMode, "SelectedApps", StringComparison.OrdinalIgnoreCase);
        SysAudioModeCombo.SelectedIndex = selectedMode ? 1 : 0;
        UpdateSystemAudioVisibility();
        RebuildAudioAppsList();
        _syncingAudioToggles = false;
    }

    private void UpdateSystemAudioVisibility()
    {
        bool source = _settings.RecordSystemAudio;
        bool apps = string.Equals(_settings.SystemAudioMode, "SelectedApps", StringComparison.OrdinalIgnoreCase);
        bool motion = !_initializing && !_syncingAudioToggles && _uiSettings.AnimationsEnabled;
        bool sourceChanged = source != _audioSourceShown;
        bool appsChanged = apps != _audioAppsShown;
        bool sourceStillExpanding = source && !double.IsNaN(SystemAudioSourceSection.Height);
        if (!motion || (source && (sourceChanged || appsChanged)))
            SetAudioDisclosure(SelectedAudioAppsPanel, SelectedAudioAppsContent, ref _audioAppsDisclosure,
                apps, motion && !sourceChanged && !sourceStillExpanding);
        if (!motion || sourceChanged || (sourceStillExpanding && appsChanged))
            SetAudioDisclosure(SystemAudioSourceSection, SystemAudioSourceContent, ref _audioSourceDisclosure, source, motion);
        _audioSourceShown = source;
        if (source || !motion) _audioAppsShown = apps;
    }

    private void SetAudioDisclosure(FrameworkElement panel, FrameworkElement content, ref Storyboard? active, bool show, bool animate)
    {
        double from = panel.Visibility == Visibility.Visible ? panel.ActualHeight : 0;
        double fromOpacity = panel.Visibility == Visibility.Visible ? panel.Opacity : 0;
        active?.Stop();
        active = null;
        panel.Tag = null;
        panel.IsHitTestVisible = show;
        if (!animate || (!show && from == 0))
        {
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            panel.Height = double.NaN;
            panel.Opacity = 1;
            return;
        }

        panel.Visibility = Visibility.Visible;
        double width = panel.ActualWidth > 0 ? panel.ActualWidth : RecordAudioRow.ActualWidth;
        content.Measure(new Windows.Foundation.Size(Math.Max(1, width), double.PositiveInfinity));
        panel.Height = show ? Math.Ceiling(content.DesiredSize.Height) : 0;
        double duration = show ? 300 : 255;
        var storyboard = new Storyboard();
        var height = new DoubleAnimation
        {
            From = from, To = panel.Height, Duration = TimeSpan.FromMilliseconds(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, EnableDependentAnimation = true,
        };
        var opacity = new DoubleAnimation
        {
            From = fromOpacity, To = show ? 1 : 0, Duration = TimeSpan.FromMilliseconds(duration),
            EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        Storyboard.SetTarget(height, panel);
        Storyboard.SetTargetProperty(height, "Height");
        Storyboard.SetTarget(opacity, panel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(height);
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) =>
        {
            if (panel.IsHitTestVisible != show || !ReferenceEquals(panel.Tag, storyboard)) return;
            if (!show) panel.Visibility = Visibility.Collapsed;
            storyboard.Stop();
            panel.Height = double.NaN;
            panel.Opacity = 1;
            panel.Tag = null;
        };
        panel.Tag = storyboard;
        active = storyboard;
        storyboard.Begin();
    }

    private void ClipDisclosure_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement panel) return;
        var clip = panel.Clip as RectangleGeometry ?? new RectangleGeometry();
        clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        if (panel.Clip == null) panel.Clip = clip;
    }

    private void InitEncoderSegments() =>
        EncoderCombo.SelectedIndex = _settings.EncoderChoice switch { "Hardware" => 1, "Software" => 2, _ => 0 };

    private void EncoderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.EncoderChoice = EncoderCombo.SelectedIndex switch { 1 => "Hardware", 2 => "Software", _ => "Auto" };
        CheckDirty();
    }

    private void InitCaptureSegments()
    {
        bool dxgiOk = false;
        try { dxgiOk = ReplayEngine.DxgiDuplicationSupported(); }
        catch { }
        CaptureDxgiItem.IsEnabled = dxgiOk;
        CaptureMethodCombo.SelectedIndex = _settings.CaptureMethod switch
        {
            "Wgc" => 1,
            "Dxgi" when dxgiOk => 2,
            _ => 0
        };
        UpdateCaptureMethodControls();
    }

    private void UpdateCaptureMethodControls()
    {
        bool window = _settings.CaptureTarget == "SpecificWindows";
        CaptureMethodCombo.IsEnabled = !window;
    }

    private List<(int Index, string Name)> _captureMonitors = new();
    private bool _syncingCaptureTargets;

    private void InitCaptureTargets()
    {
        _syncingCaptureTargets = true;
        try
        {
            _settings.CaptureBorder = false;
            CaptureTargetCombo.SelectedIndex = _settings.CaptureTarget == "SpecificWindows" ? 1 : 0;
            _captureMonitors = ReplayEngine.EnumerateMonitors();
            CaptureMonitorCombo.ItemsSource = _captureMonitors.Select(m => m.Name).ToList();
            CaptureMonitorCombo.SelectedIndex = _settings.CaptureMonitorIndex < 0 ? 0 : _captureMonitors.FindIndex(m => m.Index == _settings.CaptureMonitorIndex);
            CaptureCursorToggle.IsOn = _settings.CaptureCursor;
            UpdateCaptureTargetControls();
        }
        finally { _syncingCaptureTargets = false; }
    }

    private void UpdateCaptureTargetControls()
    {
        bool window = _settings.CaptureTarget == "SpecificWindows";
        CaptureMonitorRow.Visibility = window ? Visibility.Collapsed : Visibility.Visible;
        MonitorDivider.Visibility = window ? Visibility.Collapsed : Visibility.Visible;
        CustomGamesRow.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
        GamesRuleDivider.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
        RecordAudioRow.Label = window ? "record app audio" : "record system audio";
        UpdateGamesSummary();
        UpdateCaptureMethodControls();
    }

    private void UpdateGamesSummary()
    {
        var detector = new GameDetector(_settings.SpecificWindows);
        int custom = _settings.SpecificWindows.Count;
        GamesSummaryText.Text = custom > 0
            ? $"{detector.CatalogCount:N0} built-in · {custom} custom"
            : $"{detector.CatalogCount:N0} built-in";
    }

    private async void CaptureTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingCaptureTargets) return;
        _settings.CaptureTarget = CaptureTargetCombo.SelectedIndex == 1 ? "SpecificWindows" : "Display";
        if (_settings.CaptureTarget == "SpecificWindows" && _settings.CaptureMethod == "Dxgi")
        {
            _settings.CaptureMethod = "Wgc";
            CaptureMethodCombo.SelectedIndex = 0;
            await new ContentDialog
            {
            FontFamily = new FontFamily("Segoe UI"),
                Title = "game capture uses windows graphics capture",
                Content = "dxgi captures an entire display. windows graphics capture can isolate a game window.",
                CloseButtonText = "got it",
                XamlRoot = RootGrid.XamlRoot
            }.ShowAsync();
        }
        UpdateCaptureTargetControls();
        CheckDirty();
    }

    private async void ManageGamesButton_Click(object sender, RoutedEventArgs e)
    {
        var container = new StackPanel { Spacing = 12, MinWidth = 420 };
        var listPanel = new StackPanel { Spacing = 6 };
        var emptyHint = new TextBlock
        {
            Text = "no custom rules. the built-in catalog recognizes supported games automatically.",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetThemedBrush("TextSecondaryBrush")
        };

        void RebuildList()
        {
            listPanel.Children.Clear();
            emptyHint.Visibility = _settings.SpecificWindows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var rule in _settings.SpecificWindows)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(new TextBlock { Text = rule.Name.ToLowerInvariant(), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = rule.Description, FontSize = 11.5, Foreground = GetThemedBrush("TextSecondaryBrush") });
                row.Children.Add(text);

                var removeBtn = new Button
                {
                    Content = "remove",
                    Height = 28,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 12
                };
                removeBtn.Click += (_, _) =>
                {
                    _settings.SpecificWindows = _settings.SpecificWindows.Where(r => r != rule).ToList();
                    _settings.SaveFields(nameof(Settings.SpecificWindows));
                    _engine.ReloadGameRules();
                    UpdateGamesSummary();
                    RebuildList();
                };
                Grid.SetColumn(removeBtn, 1);
                row.Children.Add(removeBtn);

                listPanel.Children.Add(new Border
                {
                    Background = GetThemedBrush("SurfaceElevatedBrush"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = row
                });
            }
        }

        RebuildList();

        var nameBox = new TextBox { FontFamily = new FontFamily("Segoe UI"), PlaceholderText = "game name (e.g. minecraft)" };
        var exeBox = new TextBox { PlaceholderText = "executable (e.g. javaw.exe)" };
        var kwBox = new TextBox { PlaceholderText = "title keyword (optional)" };
        var errText = new TextBlock { FontSize = 12, Foreground = GetThemedBrush("ErrorBrush"), TextWrapping = TextWrapping.Wrap };

        var browseBtn = new Button { Content = "browse .exe...", CornerRadius = new CornerRadius(6) };
        browseBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                InitializeWithWindow.Initialize(picker, _hwnd);
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    exeBox.Text = file.Name.ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                        nameBox.Text = Path.GetFileNameWithoutExtension(file.Name);
                }
            }
            catch (Exception ex) { errText.Text = ex.Message; }
        };

        var addBtn = new Button
        {
            Content = "add rule",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Height = 32,
            MinHeight = 32
        };
        addBtn.Click += (_, _) =>
        {
            string exe = exeBox.Text.Trim();
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                errText.Text = "enter a valid executable name ending in .exe";
                return;
            }
            string kw = kwBox.Text.Trim();
            string nm = string.IsNullOrWhiteSpace(nameBox.Text) ? Path.GetFileNameWithoutExtension(exe) : nameBox.Text.Trim();
            if (_settings.SpecificWindows.Any(r => r.Executable.Equals(exe, StringComparison.OrdinalIgnoreCase) && r.Keyword.Equals(kw, StringComparison.OrdinalIgnoreCase)))
            {
                errText.Text = "this executable and keyword rule already exists.";
                return;
            }
            _settings.SpecificWindows = [.. _settings.SpecificWindows, new GameRule(nm, exe, kw)];
            _settings.SaveFields(nameof(Settings.SpecificWindows));
            _engine.ReloadGameRules();
            UpdateGamesSummary();
            nameBox.Text = exeBox.Text = kwBox.Text = errText.Text = "";
            RebuildList();
        };

        var addGrid = new StackPanel { Spacing = 8 };
        addGrid.Children.Add(nameBox);
        addGrid.Children.Add(exeBox);
        addGrid.Children.Add(kwBox);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(browseBtn);
        btnRow.Children.Add(addBtn);
        addGrid.Children.Add(btnRow);
        addGrid.Children.Add(errText);

        container.Children.Add(emptyHint);
        container.Children.Add(new ScrollViewer { MaxHeight = 180, Content = listPanel });
        container.Children.Add(addGrid);

        await new ContentDialog
        {
            FontFamily = new FontFamily("Segoe UI"),
            Title = "custom games",
            Content = container,
            CloseButtonText = "done",
            XamlRoot = RootGrid.XamlRoot
        }.ShowAsync();
    }

    private void CaptureMonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingCaptureTargets) return;
        if (CaptureMonitorCombo.SelectedIndex >= 0 && CaptureMonitorCombo.SelectedIndex < _captureMonitors.Count)
            _settings.CaptureMonitorIndex = _captureMonitors[CaptureMonitorCombo.SelectedIndex].Index;
        CheckDirty();
    }

    private void WindowCaptureOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingCaptureTargets) return;
        _settings.CaptureCursor = CaptureCursorToggle.IsOn;
        _settings.CaptureBorder = false;
        CheckDirty();
    }

    private void CaptureMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.CaptureMethod = CaptureMethodCombo.SelectedIndex switch
        {
            1 => "Wgc",
            2 => "Dxgi",
            _ => "Auto"
        };
        UpdateCaptureMethodControls();
        CheckDirty();
    }

    private void SysAudioToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingAudioToggles) return;
        _settings.RecordSystemAudio = SysAudioToggle.IsOn;
        UpdateSystemAudioVisibility();
        if (_settings.RecordSystemAudio &&
            string.Equals(_settings.SystemAudioMode, "SelectedApps", StringComparison.OrdinalIgnoreCase))
        {
            RefreshAudioAppsInBackground();
        }
        CheckDirty();
    }

    private void SysAudioModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingAudioToggles) return;
        _settings.SystemAudioMode = SysAudioModeCombo.SelectedIndex == 1 ? "SelectedApps" : "All";
        UpdateSystemAudioVisibility();
        if (string.Equals(_settings.SystemAudioMode, "SelectedApps", StringComparison.OrdinalIgnoreCase))
        {
            RefreshAudioAppsInBackground();
            RebuildAudioAppsList();
        }
        CheckDirty();
    }

    private void RefreshAudioAppsButton_Click(object sender, RoutedEventArgs e) =>
        RefreshAudioAppsInBackground();

    private void RefreshAudioAppsInBackground()
    {
        RefreshAudioAppsButton.IsEnabled = false;
        _ = Task.Run(() =>
        {
            var found = GameDetector.EnumerateAudioApps();
            _dq.TryEnqueue(() =>
            {
                _audioApps = found;
                RebuildAudioAppsList();
                RefreshAudioAppsButton.IsEnabled = true;
            });
        });
    }

    private void RebuildAudioAppsList()
    {
        AudioAppsListPanel.Children.Clear();

        var selectedSet = new HashSet<string>(
            _settings.SystemAudioApps.Select(a => a.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var items = new Dictionary<string, AudioAppCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _audioApps)
            items[app.Executable] = app;
        foreach (var saved in selectedSet)
        {
            if (string.IsNullOrWhiteSpace(saved) || items.ContainsKey(saved)) continue;
            string display = Path.GetFileNameWithoutExtension(saved).ToLowerInvariant();
            items[saved] = new AudioAppCandidate(saved, display, IsAudioActive: false);
        }

        var ordered = items.Values
            .OrderByDescending(a => selectedSet.Contains(a.Executable))
            .ThenByDescending(a => a.IsAudioActive)
            .ThenBy(a => a.Executable, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            AudioAppsListPanel.Children.Add(new TextBlock
            {
                Text = "no running audio apps. add an executable below.",
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
            });
            return;
        }

        foreach (var app in ordered)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string label = string.Equals(app.DisplayName, Path.GetFileNameWithoutExtension(app.Executable), StringComparison.OrdinalIgnoreCase)
                ? app.Executable.ToLowerInvariant()
                : $"{app.DisplayName.ToLowerInvariant()} · {app.Executable.ToLowerInvariant()}";

            var cb = new CheckBox
            {
                FontFamily = new FontFamily("Segoe UI"),
                Content = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                IsChecked = selectedSet.Contains(app.Executable),
                MinHeight = 26,
                Padding = new Thickness(6, 2, 0, 2),
                Tag = app.Executable
            };
            cb.Checked += AudioAppCheckBox_Changed;
            cb.Unchecked += AudioAppCheckBox_Changed;
            Grid.SetColumn(cb, 0);
            row.Children.Add(cb);

            if (app.IsAudioActive)
            {
                var activeBadge = new TextBlock
                {
                    Text = "active",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["SuccessBrush"]
                };
                Grid.SetColumn(activeBadge, 1);
                row.Children.Add(activeBadge);
            }

            AudioAppsListPanel.Children.Add(row);
        }
    }

    private void AudioAppCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingAudioToggles) return;
        if (sender is not CheckBox cb || cb.Tag is not string exe) return;
        bool isChecked = cb.IsChecked == true;
        _settings.SystemAudioApps.RemoveAll(a => string.Equals(a, exe, StringComparison.OrdinalIgnoreCase));
        if (isChecked) _settings.SystemAudioApps.Add(exe.ToLowerInvariant());
        CheckDirty();
    }

    private void AddAudioAppButton_Click(object sender, RoutedEventArgs e) => TryAddCustomAudioApp();

    private void AddAudioAppBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        TryAddCustomAudioApp();
    }

    private void TryAddCustomAudioApp()
    {
        string raw = (AddAudioAppBox.Text ?? "").Trim().Trim('"');
        if (raw.Length == 0) return;
        string name = Path.GetFileName(raw).ToLowerInvariant();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!_settings.SystemAudioApps.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
            _settings.SystemAudioApps.Add(name);
        AddAudioAppBox.Text = "";
        RebuildAudioAppsList();
        CheckDirty();
    }

    private void MicToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingAudioToggles) return;
        _settings.RecordMicrophone = MicToggle.IsOn;
        CheckDirty();
    }

    private bool RegisterHotkeyInternal(uint modifiers, uint vk) =>
        RegisterHotKey(_hwnd, HotkeyId, modifiers | ModNoRepeat, vk) ||
        RegisterHotKey(_hwnd, HotkeyId, modifiers, vk);

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);

    private static bool IsVkDown(int vk) =>
        ((GetAsyncKeyState(vk) | GetKeyState(vk)) & 0x8000) != 0;

    private static uint GetLiveModifiers()
    {
        uint mods = 0;
        if (IsVkDown(0x11 /* VK_CONTROL */)) mods |= ModControl;
        if (IsVkDown(0x12 /* VK_MENU */)) mods |= ModAlt;
        if (IsVkDown(0x10 /* VK_SHIFT */)) mods |= ModShift;
        if (IsVkDown(0x5B /* VK_LWIN */) || IsVkDown(0x5C /* VK_RWIN */)) mods |= ModWin;
        return mods;
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_capturingHotkey) BeginHotkeyCapture();
    }

    private void BeginHotkeyCapture()
    {
        _capturingHotkey = true;
        _pendingHotkeyModifier = 0;
        UnregisterHotKey(_hwnd, HotkeyId);
        HotkeyCaptureButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        HotkeyCaptureButton.CornerRadius = new CornerRadius(4);
        HotkeyCaptureText.Text = "press a key...";
        HotkeyCaptureButton.Focus(FocusState.Programmatic);
    }

    private void EndHotkeyCapture(bool restoreHotkey = true)
    {
        _capturingHotkey = false;
        _pendingHotkeyModifier = 0;
        if (restoreHotkey)
            RegisterHotkeyInternal(_settings.HotkeyModifiers, _settings.HotkeyVk);
        HotkeyCaptureButton.Style = (Style)Application.Current.Resources["SettingsInputButtonStyle"];
        HotkeyCaptureButton.CornerRadius = new CornerRadius(4);
        UpdateHotkeyButtonDisplay();
    }

    private void UpdateHotkeyButtonDisplay() =>
        HotkeyCaptureText.Text = HotkeyDisplay.Format(_settings.HotkeyModifiers, _settings.HotkeyVk);

    private void Window_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        VirtualKey key = e.Key;
        if (key == VirtualKey.None || (int)key == 0xE5) key = e.OriginalKey;

        uint liveMods = GetLiveModifiers();
        if (key == VirtualKey.Escape && liveMods == 0 && _pendingHotkeyModifier == 0)
        {
            e.Handled = true;
            EndHotkeyCapture();
            return;
        }

        uint? mod = HotkeyDisplay.ModifierBitForKey(key);
        if (mod != null)
        {
            _pendingHotkeyModifier = liveMods | mod.Value;
            HotkeyCaptureText.Text = HotkeyDisplay.Format(_pendingHotkeyModifier, 0);
            e.Handled = true;
            return;
        }

        if (key == VirtualKey.None || (int)key is 0xE5 or 0xE7)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        uint finalMods = liveMods != 0 ? liveMods : _pendingHotkeyModifier;
        FinalizeHotkey(finalMods, (uint)key);
    }

    private void Window_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        VirtualKey key = e.Key;
        if (key == VirtualKey.None || (int)key == 0xE5) key = e.OriginalKey;
        uint? mod = HotkeyDisplay.ModifierBitForKey(key);
        if (mod != null)
        {
            _pendingHotkeyModifier = GetLiveModifiers() & ~mod.Value;
            HotkeyCaptureText.Text = _pendingHotkeyModifier == 0
                ? "press a key…"
                : HotkeyDisplay.Format(_pendingHotkeyModifier, 0);
            e.Handled = true;
        }
    }

    private void FinalizeHotkey(uint modifiers, uint vk)
    {
        uint oldMod = _settings.HotkeyModifiers;
        uint oldVk = _settings.HotkeyVk;
        UnregisterHotKey(_hwnd, HotkeyId);

        if (!RegisterHotkeyInternal(modifiers, vk))
        {
            RegisterHotkeyInternal(oldMod, oldVk);
            _capturingHotkey = false;
            _pendingHotkeyModifier = 0;
            HotkeyCaptureButton.Style = (Style)Application.Current.Resources["SettingsInputButtonStyle"];
            HotkeyCaptureButton.CornerRadius = new CornerRadius(4);
            HotkeyCaptureText.Text = "already in use";
            var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            revertTimer.Tick += (_, _) => { revertTimer.Stop(); UpdateHotkeyButtonDisplay(); };
            revertTimer.Start();
            return;
        }

        _settings.HotkeyModifiers = modifiers;
        _settings.HotkeyVk = vk;
        _settings.SaveFields(nameof(Settings.HotkeyModifiers), nameof(Settings.HotkeyVk));
        EndHotkeyCapture(restoreHotkey: false);
        UpdateHeroSummary();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _syncingStartupToggle) return;
        StartupManager.SetEnabled(StartupToggle.IsOn);
    }

    private void RefreshStartupToggleState()
    {
        bool actual = StartupManager.IsEnabled();
        if (StartupToggle.IsOn == actual) return;
        _syncingStartupToggle = true;
        StartupToggle.IsOn = actual;
        _syncingStartupToggle = false;
    }

    private void DebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.DebugLogging = DebugToggle.IsOn;
        Logger.SetDebugMode(_settings.DebugLogging);
        _settings.SaveFields(nameof(Settings.DebugLogging));
    }

    private void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        _settings.SaveFields(nameof(Settings.AutoCheckUpdates));
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate || _downloadingUpdate) return;
        if (_availableUpdate is { UpdateAvailable: true } update)
        {
            await DownloadAndInstallUpdateAsync(update);
            return;
        }
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_checkingUpdate || _downloadingUpdate) return;
        _checkingUpdate = true;
        if (userInitiated)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButtonText.Text = "checking...";
            UpdateStatusText.Text = "checking github releases…";
        }

        try
        {
            UpdateCheckResult res = await AppUpdater.CheckForUpdatesAsync();
            if (res.UpdateAvailable && res.LatestVersion != null)
            {
                _availableUpdate = res;
                string ver = $"v{res.LatestVersion.Major}.{res.LatestVersion.Minor}.{res.LatestVersion.Build}";
                UpdateStatusText.Text = $"update available ({ver})";
                CheckUpdateButtonText.Text = $"install {ver}";
                CheckUpdateButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                if (!userInitiated)
                    ShowInfoBar(InfoBarSeverity.Informational, "Update available", $"Instant Replay {ver} is ready in settings → updates.");
            }
            else
            {
                _availableUpdate = null;
                CheckUpdateButton.Style = (Style)Application.Current.Resources["BaseButtonStyle"];
                CheckUpdateButtonText.Text = "check for updates";
                UpdateStatusText.Text = res.NoReleasesYet
                    ? "no releases published yet"
                    : res.LatestVersion > res.CurrentVersion
                        ? "a newer release has no verifiable installer"
                        : "you are on the latest version";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"update check failed: {ex.Message}");
            if (userInitiated)
            {
                UpdateStatusText.Text = "couldn't check for updates";
                ShowInfoBar(InfoBarSeverity.Warning, "Update check failed", ex.Message);
            }
        }
        finally
        {
            _checkingUpdate = false;
            if (!_downloadingUpdate) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallUpdateAsync(UpdateCheckResult update)
    {
        _downloadingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButtonText.Text = "downloading 0%";
        UpdateStatusText.Text = "downloading update package...";

        var progress = new Progress<double>(pct =>
        {
            _dq.TryEnqueue(() =>
            {
                if (CheckUpdateButtonText != null) CheckUpdateButtonText.Text = $"downloading {pct:0}%";
            });
        });

        try
        {
            string installerPath = await AppUpdater.DownloadInstallerAsync(update, progress);
            UpdateStatusText.Text = "launching installer...";
            CheckUpdateButtonText.Text = "restarting...";
            AppUpdater.LaunchInstaller(installerPath);
            ExitApplicationAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"update download failed: {ex.Message}");
            UpdateStatusText.Text = "update download failed";
            CheckUpdateButtonText.Text = _availableUpdate?.LatestVersion is { } lv
                ? $"Install v{lv.Major}.{lv.Minor}.{lv.Build}"
                : "check for updates";
            ShowInfoBar(InfoBarSeverity.Error, "Update failed", ex.Message);
        }
        finally
        {
            _downloadingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.DarkMode = ThemeToggle.IsOn;
        _settings.SaveFields(nameof(Settings.DarkMode));
        _dq.TryEnqueue(() => ApplyAppTheme(_settings.DarkMode));
    }

    private void SaveLocationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.SaveDestination = SaveLocationBox.Text;
        CheckDirty();
    }

    private async void BrowseSaveLocationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary;
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) SaveLocationBox.Text = folder.Path;
        }
        catch (Exception ex) { Logger.Warn($"folder picker failed: {ex.Message}"); }
    }

    private void CheckDirty()
    {
        _dirty = CapturePending() != _baseline;
        UpdatePendingFlyout();
    }

    private void UpdatePendingFlyout()
    {
        if (_dirty == _flyoutShown) return;
        _flyoutShown = _dirty;
        PendingChangesFlyout.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        if (_dirty) FadeIn(PendingChangesFlyout, from: 0.86f);
    }

    private void RevertPendingChanges()
    {
        PendingSettings prev = _baseline;
        _settings.ReplayDurationSeconds = prev.ReplayDurationSeconds;
        _settings.FrameRate = prev.FrameRate;
        _settings.QualityPreset = prev.QualityPreset;
        _settings.EncoderChoice = prev.EncoderChoice;
        _settings.CaptureMethod = prev.CaptureMethod;
        _settings.CaptureSecondaryWindows = prev.CaptureSecondaryWindows;
        _settings.CaptureRoundedCorners = prev.CaptureRoundedCorners;
        _settings.CaptureBorder = false;
        _settings.CaptureCursor = prev.CaptureCursor;
        _settings.CaptureClientArea = prev.CaptureClientArea;
        _settings.CaptureMonitorIndex = prev.CaptureMonitorIndex;
        _settings.CaptureTarget = prev.CaptureTarget;
        _settings.Microphone = prev.Microphone;
        _settings.SaveDestination = prev.SaveDestination;
        _settings.RecordSystemAudio = prev.RecordSystemAudio;
        _settings.SystemAudioMode = prev.SystemAudioMode;
        _settings.SystemAudioApps = prev.SystemAudioAppsKey
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        _settings.RecordMicrophone = prev.RecordMicrophone;

        bool wasInit = _initializing;
        _initializing = true;
        try
        {
            if (_micChoices.Count > 0)
            {
                int idx = string.IsNullOrEmpty(_settings.Microphone)
                    ? 0
                    : _micChoices.FindIndex(1, m => string.Equals(m, _settings.Microphone, StringComparison.OrdinalIgnoreCase));
                MicCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            InitAudioToggles();
            InitDurationSegments();
            InitFpsSegments();
            InitQualitySegments();
            SaveLocationBox.Text = _settings.GetEffectiveSaveDestination();
            InitEncoderSegments();
            CaptureMethodCombo.SelectedIndex = _settings.CaptureMethod switch
            {
                "Wgc" => 1,
                "Dxgi" when CaptureDxgiItem.IsEnabled => 2,
                _ => 0
            };
            InitCaptureTargets();
        }
        finally { _initializing = wasInit; }
        CheckDirty();
    }

    private async void PendingSaveButton_Click(object sender, RoutedEventArgs e) => await SaveChangesAsync();

    private void PendingDiscardButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_savingChanges) { RevertPendingChanges(); ClearInfoBar(); }
    }

    private async Task SaveChangesAsync()
    {
        if (_savingChanges || !_dirty) return;
        _savingChanges = true;
        PendingSaveButton.IsEnabled = false;
        PendingDiscardButton.IsEnabled = false;

        ShowInfoBar(InfoBarSeverity.Informational, "Saving changes", "Applying settings and restarting the replay buffer…");

        bool ok = true;
        bool restarted = false;
        try
        {
            _settings.Save();
            ReplayLibrary.EnsureBaseFolders(_settings.GetEffectiveSaveDestination());

            if (_engine.IsRunning)
            {
                restarted = true;
                ok = await Task.Run(() => _engine.Restart(_tempDir, wipeSegments: true));
            }

            _baseline = CapturePending();
            _dirty = false;
            UpdatePendingFlyout();
            UpdateHeroSummary();
            _ = RefreshGalleryAsync();

            if (ok)
            {
                ShowInfoBar(InfoBarSeverity.Success, "Settings applied",
                    restarted ? "Settings saved and capture buffer restarted." : "Settings saved for next capture.");
            }
            else
            {
                ShowInfoBar(InfoBarSeverity.Error, "Engine failed to start",
                    "Settings saved, but capture could not start with them.");
            }
        }
        catch (Exception ex)
        {
            ShowInfoBar(InfoBarSeverity.Error, "Could not save changes", ex.Message);
        }
        finally
        {
            PendingSaveButton.IsEnabled = true;
            PendingDiscardButton.IsEnabled = true;
            _savingChanges = false;
            if (StatusInfoBar.IsOpen && StatusInfoBar.Severity != InfoBarSeverity.Error) _infoBarTimer?.Start();
            SetRunningVisual(_engine.IsRunning);
        }
    }

    private void EnsureInfoBarTimer()
    {
        if (_infoBarTimer != null) return;
        _infoBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _infoBarTimer.Tick += (_, _) => { _infoBarTimer!.Stop(); ClearInfoBar(); };
    }

    private void ShowInfoBar(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title.ToLowerInvariant();
        StatusInfoBar.Message = message.ToLowerInvariant();
        StatusInfoBar.Visibility = Visibility.Visible;
        StatusInfoBar.IsOpen = true;
        EnsureInfoBarTimer();
        _infoBarTimer!.Stop();
        if (!_savingChanges && severity != InfoBarSeverity.Error) _infoBarTimer.Start();
    }

    private void ClearInfoBar()
    {
        _infoBarTimer?.Stop();
        StatusInfoBar.IsOpen = false;
        StatusInfoBar.Visibility = Visibility.Collapsed;
    }

    private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _infoBarTimer?.Stop();
        StatusInfoBar.Visibility = Visibility.Collapsed;
    }

    private static void AddMotion(Storyboard storyboard, DependencyObject target, string path, double from, double to,
        double delayMs, double durationMs, bool layout = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = layout };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        if (delayMs > 0) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(delayMs), Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(delayMs + durationMs), Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new(0.22, 1), ControlPoint2 = new(0.36, 1) },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        storyboard.Children.Add(animation);
    }

    private void AnimateDouble(DependencyObject target, DependencyProperty property, double to, double milliseconds, double? from = null)
    {
        double start = from ?? (double)target.GetValue(property);
        if (_propertyMotion.TryGetValue(target, out var previous))
        {
            _propertyMotion.Remove(target);
            previous.Stop();
        }
        target.SetValue(property, to);
        if (!_uiSettings.AnimationsEnabled || milliseconds <= 0 || Math.Abs(start - to) < 0.001) return;

        var storyboard = new Storyboard();
        AddMotion(storyboard, target, property == UIElement.OpacityProperty ? "Opacity" : "ScaleX", start, to, 0, milliseconds);
        _propertyMotion.Add(target, storyboard);
        storyboard.Completed += (_, _) =>
        {
            if (!_propertyMotion.TryGetValue(target, out var current) || current != storyboard) return;
            _propertyMotion.Remove(target);
            storyboard.Stop();
        };
        storyboard.Begin();
    }

    private void StartDotPulse()
    {
        StopDotPulse();
        if (!_uiSettings.AnimationsEnabled) return;
        try
        {
            var anim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.72,
                Duration = TimeSpan.FromSeconds(1.4),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, RecDot);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            _dotStoryboard = sb;
            sb.Begin();
        }
        catch { }
    }

    private void StopDotPulse()
    {
        try { _dotStoryboard?.Stop(); } catch { }
        _dotStoryboard = null;
        if (RecDot != null) RecDot.Opacity = 1;
    }

    private void UpdateSystemStats()
    {
        double? cpuPct = null;
        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong i = idle.Ticks, k = kernel.Ticks, u = user.Ticks;
                if (_statsBaselineTaken && k >= _lastKernelTicks && u >= _lastUserTicks && i >= _lastIdleTicks)
                {
                    ulong dTotal = (k - _lastKernelTicks) + (u - _lastUserTicks);
                    ulong dIdle = i - _lastIdleTicks;
                    if (dTotal > 0 && dTotal >= dIdle) cpuPct = 100.0 * (dTotal - dIdle) / dTotal;
                }
                _lastIdleTicks = i; _lastKernelTicks = k; _lastUserTicks = u;
                _statsBaselineTaken = true;
            }
        }
        catch { }

        if (cpuPct is { } pct)
        {
            CpuValueText.Text = $"{pct:0}%";
            CpuHintText.Text = "overall processor load";
            double targetCpu = Math.Clamp(pct, 0, 100) / 100.0;
            AnimateDouble(CpuBarScale, ScaleTransform.ScaleXProperty, targetCpu, 210);
            ApplyMetricStyle(CpuCard, CpuBarFill, CpuValueText, CpuHintText, pct >= 85, pct >= 70);
        }
        else
        {
            CpuValueText.Text = "—";
        }

        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m))
            {
                double totalGb = m.ullTotalPhys / (1024.0 * 1024 * 1024);
                double usedGb = (m.ullTotalPhys - m.ullAvailPhys) / (1024.0 * 1024 * 1024);
                RamValueText.Text = $"{usedGb:0.0} / {totalGb:0} gb";
                RamHintText.Text = $"{m.dwMemoryLoad}% in use";
                double targetRam = Math.Clamp(m.dwMemoryLoad, 0, 100) / 100.0;
                AnimateDouble(RamBarScale, ScaleTransform.ScaleXProperty, targetRam, 210);
                ApplyMetricStyle(RamCard, RamBarFill, RamValueText, RamHintText, m.dwMemoryLoad >= 92, m.dwMemoryLoad >= 82);
            }
        }
        catch { }
    }

    private void ApplyMetricStyle(Border card, Border barFill, TextBlock value, TextBlock hint, bool critical, bool elevated)
    {
        card.BorderBrush = GetThemedBrush("SurfaceBorderBrush");
        barFill.Background = GetThemedBrush("AccentIndicatorBrush");
        value.Foreground = GetThemedBrush("TextPrimaryBrush");
        hint.Foreground = GetThemedBrush("TextTertiaryBrush");
    }

    private void ExitApplicationAsync()
    {
        if (_isClosingForReal) return;
        if (_dirty) RevertPendingChanges();
        _isClosingForReal = true;
        UpdateHeroOrnament();

        UnregisterHotKey(_hwnd, HotkeyId);
        _bufferHealthTimer?.Stop();
        _statsTimer?.Stop();
        _micRefreshTimer?.Stop();
        _micMeterTimer?.Stop();
        _deviceChangeDebounceTimer?.Stop();
        _audioWatcher?.Dispose();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        StopDotPulse();
        StopViewerPlayback(destroy: true);
        EnableWindow(_hwnd, true);
        _engine.Stop();
        _engine.Dispose();
        ClipNotificationWindow.Shutdown();
        _taskbarBadge?.Dispose();
        ReleaseBadgeIcons();
        _trayIcon?.Dispose();

        void FinishExit()
        {
            try { Close(); } catch { }
            Environment.Exit(0);
        }

        if (!_dq.TryEnqueue(FinishExit)) FinishExit();
    }
}
