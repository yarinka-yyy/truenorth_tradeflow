using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TrueNorth.TradeFlow;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\TrueNorth.TradeFlow";
    private const string OpenEventName = @"Local\TrueNorth.TradeFlow.Open";
    private const string UninstallEventName = @"Local\TrueNorth.TradeFlow.Uninstall";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _openEvent;
    private EventWaitHandle? _uninstallEvent;
    private RegisteredWaitHandle? _openWait;
    private RegisteredWaitHandle? _uninstallWait;
    private Forms.NotifyIcon? _trayIcon;
    private WakeProtectionController? _wakeProtection;
    private TrueNorthSessionService? _sessionService;
    private MainWindow? _mainWindow;
    private bool _ownsMutex;
    private bool _isClosing;
    private bool _exitConfirmationOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--prepare-uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(RequestExistingInstanceClose());
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                SignalExistingInstance();
            Shutdown();
            return;
        }

        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        _uninstallEvent = new EventWaitHandle(false, EventResetMode.AutoReset, UninstallEventName);
        _openWait = ThreadPool.RegisterWaitForSingleObject(_openEvent, (_, timedOut) =>
        {
            if (!timedOut)
                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        _uninstallWait = ThreadPool.RegisterWaitForSingleObject(_uninstallEvent, (_, timedOut) =>
        {
            if (!timedOut)
                Dispatcher.BeginInvoke(new Action(ShutdownCleanly));
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        var settingsStore = new SettingsStore(UserPreferences.SettingsPath, UserPreferences.ReadLegacyPreventSleep);
        var settings = settingsStore.Load();
        _wakeProtection = new WakeProtectionController(settings.Profile.Settings.PreventSleep);
        _sessionService = new TrueNorthSessionService();
        _mainWindow = new MainWindow(settingsStore, settings, _wakeProtection, _sessionService);
        MainWindow = _mainWindow;
        _mainWindow.ShowInTaskbar = false;
        InitializeTrayIcon();
        SessionEnding += HandleSessionEnding;

        if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Открыть TrueNorth TradeFlow");
        open.Click += (_, _) => ShowMainWindow();
        var exit = new Forms.ToolStripMenuItem("Выйти");
        exit.Click += (_, _) => ConfirmExit();
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "TrueNorth TradeFlow",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ShowMainWindow();
        };
    }

    private void ShowMainWindow()
    {
        if (_isClosing || _mainWindow is null)
            return;

        _mainWindow.ShowInTaskbar = true;
        if (!_mainWindow.IsVisible)
            _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ConfirmExit()
    {
        if (_mainWindow is null || _isClosing || _exitConfirmationOpen)
            return;

        _exitConfirmationOpen = true;
        try
        {
            ShowMainWindow();
            var confirmation = new ExitConfirmationWindow { Owner = _mainWindow };
            if (confirmation.ShowDialog() == true)
                ShutdownCleanly();
        }
        finally
        {
            _exitConfirmationOpen = false;
        }
    }

    private void HandleSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        e.Cancel = false;
        ShutdownCleanly();
    }

    private void ShutdownCleanly()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _mainWindow?.PrepareForApplicationClose();
        _sessionService?.Close();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _wakeProtection?.Dispose();
        _mainWindow?.AllowApplicationClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openWait?.Unregister(null);
        _uninstallWait?.Unregister(null);
        _openEvent?.Dispose();
        _uninstallEvent?.Dispose();
        _trayIcon?.Dispose();
        _wakeProtection?.Dispose();
        _sessionService?.Dispose();

        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static int RequestExistingInstanceClose()
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        if (createdNew)
            return 0;

        EventWaitHandle? shutdown = null;
        for (var attempt = 0; attempt < 10 && shutdown is null; attempt++)
        {
            try
            {
                shutdown = EventWaitHandle.OpenExisting(UninstallEventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
        }
        if (shutdown is null)
            return 2;

        using (shutdown)
            shutdown.Set();

        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(15)))
                return 3;
        }
        catch (AbandonedMutexException)
        {
            // The existing process released its mutex while terminating.
        }

        mutex.ReleaseMutex();
        return 0;
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var open = EventWaitHandle.OpenExisting(OpenEventName);
                foreach (var process in Process.GetProcessesByName("TrueNorth.TradeFlow"))
                {
                    using (process)
                    {
                        if (process.Id != Environment.ProcessId)
                            AllowSetForegroundWindow(process.Id);
                    }
                }
                open.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
