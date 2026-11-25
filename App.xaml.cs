using Microsoft.UI.Xaml;

namespace InstantReplay;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\ReplayApp_SingleInstance_Mutex";
    private const string ShowWindowEventName = "Global\\ReplayApp_ShowWindow_Event";

    private Window? _window;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Logger.Error($"unhandled exception: {e.Exception}", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        bool acquired;
        try { acquired = _singleInstanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }

        if (!acquired)
        {
            Logger.Info("another replay instance is already running; signaling it to surface.");
            try
            {
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        using var existingShowEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                        existingShowEvent.Set();
                        break;
                    }
                    catch (WaitHandleCannotBeOpenedException) when (attempt < 39)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"could not signal existing instance: {ex.Message}");
            }

            Environment.Exit(0);
            return;
        }

        _showWindowEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowWindowEventName);

        bool startMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow(startMinimized);
        _window.Activate();

        StartShowWindowListener();
    }

    private void StartShowWindowListener()
    {
        var listenerThread = new Thread(() =>
        {
            while (true)
            {
                _showWindowEvent!.WaitOne();
                var dq = (_window as MainWindow)?.DispatcherQueue;
                dq?.TryEnqueue(() => (_window as MainWindow)?.BringToFront());
            }
        })
        {
            IsBackground = true,
            Name = "Replay-ShowWindowListener",
        };
        listenerThread.Start();
    }
}
