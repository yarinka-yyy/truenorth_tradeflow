using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Trading;
using Forms = System.Windows.Forms;

namespace TrueNorth.TradeFlow;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _openEvent;
    private EventWaitHandle? _uninstallEvent;
    private EventWaitHandle? _recoveryReadyEvent;
    private EventWaitHandle? _recoveryStopEvent;
    private EventWaitHandle? _recoveryDisableEvent;
    private RegisteredWaitHandle? _openWait;
    private RegisteredWaitHandle? _uninstallWait;
    private RegisteredWaitHandle? _testStopWait;
    private RegisteredWaitHandle? _testDisableWait;
    private RegisteredWaitHandle? _testSessionEndingWait;
    private RegisteredWaitHandle? _testConfirmedExitWait;
    private Process? _recoveryObserverProcess;
    private EventWaitHandle? _testOpenSignalEvent;
    private EventWaitHandle? _testSessionEndingEvent;
    private EventWaitHandle? _testConfirmedExitEvent;
    private ProcessRecoveryScope? _processRecoveryScope;
    private ProcessRestartBudgetStore? _processRestartBudget;
    private Forms.NotifyIcon? _trayIcon;
    private WakeProtectionController? _wakeProtection;
    private TrueNorthSessionService? _sessionService;
    private RecoveryCoordinator? _recoveryCoordinator;
    private NotificationDispatcher? _notificationDispatcher;
    private TelegramCredentialStore? _telegramCredentialStore;
    private MainWindow? _mainWindow;
    private LocalTradingScenario? _localTradingScenario;
    private string? _localScenarioUnavailableReason;
    private bool _ownsMutex;
    private bool _isClosing;
    private bool _exitConfirmationOpen;
    private bool _recoveryTestHost;
    private string? _processRecoveryStatus;

    protected override void OnStartup(StartupEventArgs e)
    {
        var arguments = e.Args.Length == 0
            ? Environment.GetCommandLineArgs().Skip(1).ToArray()
            : e.Args;
        if (!ProcessRecoveryStartupProfile.TryResolve(arguments, out var testHost, out var testProfile, out _))
        {
            Shutdown(2);
            return;
        }

        var recoveryScope = ProcessRecoveryScope.Create(testProfile);
        _processRecoveryScope = recoveryScope;
        _recoveryTestHost = testHost;
        using (var startupProcess = Process.GetCurrentProcess())
            recoveryScope.LogRuntime($"app-startup pid={startupProcess.Id} start={ProcessRecoveryScope.StartMarker(startupProcess)}");
        base.OnStartup(e);

        if (arguments.Contains(ProcessRecoveryObserver.ObserverArgument, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ProcessRecoveryObserver.Run(arguments, recoveryScope));
            return;
        }

        if (arguments.Contains("--prepare-uninstall", StringComparer.OrdinalIgnoreCase))
        {
            recoveryScope.DisableRecovery();
            Shutdown(RequestExistingInstanceClose(recoveryScope));
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, recoveryScope.ApplicationMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                SignalExistingInstance(recoveryScope);
            Shutdown(arguments.Contains(ProcessRecoveryObserver.SupervisedArgument, StringComparer.OrdinalIgnoreCase)
                ? ProcessRecoveryObserver.DuplicateInstanceExitCode
                : 0);
            return;
        }

        _recoveryDisableEvent = recoveryScope.OpenDisableEvent();
        var isSupervised = arguments.Contains(ProcessRecoveryObserver.SupervisedArgument, StringComparer.OrdinalIgnoreCase);
        if (!isSupervised)
            _recoveryDisableEvent.Reset();
        var stopId = ReadArgumentValue(arguments, ProcessRecoveryObserver.StopArgument)
            ?? Guid.NewGuid().ToString("N");
        _recoveryStopEvent = new EventWaitHandle(false, EventResetMode.ManualReset,
            recoveryScope.StopEventName(stopId));
        var currentProcess = Process.GetCurrentProcess();
        var startMarker = ProcessRecoveryScope.StartMarker(currentProcess);
        _recoveryReadyEvent = new EventWaitHandle(false, EventResetMode.ManualReset,
            recoveryScope.ReadyEventName(currentProcess.Id, startMarker));
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, recoveryScope.OpenEventName);
        if (_recoveryTestHost)
            _testOpenSignalEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                recoveryScope.TestOpenSignalEventName);
        _uninstallEvent = new EventWaitHandle(false, EventResetMode.AutoReset, recoveryScope.UninstallEventName);
        _openWait = ThreadPool.RegisterWaitForSingleObject(_openEvent, (_, timedOut) =>
        {
            if (!timedOut)
            {
                if (_recoveryTestHost)
                {
                    recoveryScope.LogRuntime($"test-open-signal pid={Environment.ProcessId}");
                    _testOpenSignalEvent?.Set();
                }
                else
                    Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        _uninstallWait = ThreadPool.RegisterWaitForSingleObject(_uninstallEvent, (_, timedOut) =>
        {
            if (!timedOut)
                Dispatcher.BeginInvoke(new Action(ShutdownCleanly));
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        _processRestartBudget = new ProcessRestartBudgetStore(recoveryScope.ProcessRestartBudgetPath);
        var applicationProcessStatus = _processRestartBudget.ReadStatus(ProcessRecoveryKind.Application);
        var backgroundWorkerProcessStatus = _processRestartBudget.ReadStatus(ProcessRecoveryKind.BackgroundWorker);
        _processRecoveryStatus = ReadProcessRecoveryStatus(applicationProcessStatus, backgroundWorkerProcessStatus);
        if (!isSupervised)
        {
            var applicationArguments = new List<string>();
            if (arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                applicationArguments.Add("--startup");
            if (_recoveryTestHost)
                applicationArguments.Add("--recovery-test-host");
            if (arguments.Contains("--recovery-test-never-ready-on-restart", StringComparer.OrdinalIgnoreCase))
                applicationArguments.Add("--recovery-test-never-ready-on-restart");
            if (recoveryScope.Isolated)
                applicationArguments.Add("--recovery-test-profile=" + recoveryScope.ProfileRoot);
            _recoveryObserverProcess = ProcessRecoveryObserver.Start(recoveryScope, currentProcess.Id,
                startMarker, stopId, applicationArguments, out var observerError);
            if (_recoveryObserverProcess is null)
            {
                _processRecoveryStatus = observerError;
                _processRestartBudget.SetStatus(ProcessRecoveryKind.Application,
                    observerError ?? "Наблюдатель восстановления приложения недоступен.");
            }
        }

        if (_recoveryTestHost)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _testStopWait = ThreadPool.RegisterWaitForSingleObject(_recoveryStopEvent, (_, timedOut) =>
            {
                if (!timedOut)
                    Dispatcher.BeginInvoke(new Action(ShutdownCleanly));
            }, null, Timeout.Infinite, executeOnlyOnce: true);
            _testDisableWait = ThreadPool.RegisterWaitForSingleObject(_recoveryDisableEvent, (_, timedOut) =>
            {
                if (!timedOut)
                    Dispatcher.BeginInvoke(new Action(ShutdownCleanly));
            }, null, Timeout.Infinite, executeOnlyOnce: true);
            if (arguments.Contains("--recovery-test-session-ending", StringComparer.OrdinalIgnoreCase))
            {
                _testSessionEndingEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                    recoveryScope.TestSessionEndingEventName);
                _testSessionEndingWait = ThreadPool.RegisterWaitForSingleObject(_testSessionEndingEvent, (_, timedOut) =>
                {
                    if (!timedOut)
                        Dispatcher.BeginInvoke(new Action(StopForPlannedSessionEnding));
                }, null, Timeout.Infinite, executeOnlyOnce: true);
            }
            if (arguments.Contains("--recovery-test-confirmed-exit", StringComparer.OrdinalIgnoreCase))
            {
                _testConfirmedExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                    recoveryScope.TestConfirmedExitEventName);
                _testConfirmedExitWait = ThreadPool.RegisterWaitForSingleObject(_testConfirmedExitEvent, (_, timedOut) =>
                {
                    if (!timedOut)
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            recoveryScope.LogRuntime($"test-confirmed-exit pid={Environment.ProcessId}");
                            ShutdownCleanly();
                        }));
                }, null, Timeout.Infinite, executeOnlyOnce: true);
            }
            recoveryScope.LogRuntime($"host-started pid={currentProcess.Id} start={startMarker} "
                + $"startup={arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase).ToString().ToLowerInvariant()} "
                + $"stop-id={stopId}");
            if (arguments.Contains("--recovery-test-never-ready", StringComparer.OrdinalIgnoreCase))
                recoveryScope.LogRuntime($"host-unready pid={currentProcess.Id} start={startMarker}");
            if (!arguments.Contains("--recovery-test-never-ready", StringComparer.OrdinalIgnoreCase))
            {
                recoveryScope.LogRuntime($"host-ready pid={currentProcess.Id} start={startMarker} "
                    + $"startup={arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase).ToString().ToLowerInvariant()} "
                    + $"stop-id={stopId}");
                MarkProcessReady();
            }
            return;
        }

        var settingsPath = Path.Combine(recoveryScope.ProfileRoot, "settings.json");
        var settingsStore = new SettingsStore(settingsPath,
            recoveryScope.Isolated ? () => null : UserPreferences.ReadLegacyPreventSleep);
        var settings = settingsStore.Load();
        var recoveryCoordinator = new RecoveryCoordinator(
            new RecoveryJournalStore(Path.Combine(recoveryScope.ProfileRoot, "recovery-state.json")), settings.Profile.Settings);
        _recoveryCoordinator = recoveryCoordinator;
        var unknownProcessFailure = ProcessRecoveryStatusRouting.UnknownScopeFailure(
            applicationProcessStatus, backgroundWorkerProcessStatus, recoveryCoordinator.State);
        if (!string.IsNullOrWhiteSpace(unknownProcessFailure))
        {
            var episodeId = recoveryCoordinator.ReportFailureAsync(null, null, unknownProcessFailure,
                settings.Profile.Settings).GetAwaiter().GetResult();
            if (episodeId is not null)
            {
                _processRestartBudget.ClearStatus(ProcessRecoveryKind.Application);
                _processRestartBudget.ClearStatus(ProcessRecoveryKind.BackgroundWorker);
            }
        }
        if (!string.IsNullOrWhiteSpace(backgroundWorkerProcessStatus.Message)
            && ProcessRecoveryStatusRouting.HasExactErrorEvent(backgroundWorkerProcessStatus.Message,
                recoveryCoordinator.State.Events))
            _processRestartBudget.ClearStatus(ProcessRecoveryKind.BackgroundWorker);
        _wakeProtection = new WakeProtectionController(settings.Profile.Settings.PreventSleep);
        _sessionService = new TrueNorthSessionService();
        InitializeTrayIcon();
        var notificationSettings = settings.Profile.Settings.Copy();
        if (settings.Status is not (SettingsFileStatus.Missing or SettingsFileStatus.Loaded))
        {
            notificationSettings.NotificationsEnabled = false;
        }
        recoveryCoordinator.UpdateSettings(notificationSettings);
        _telegramCredentialStore = new TelegramCredentialStore(Path.Combine(recoveryScope.ProfileRoot, "telegram-credentials.dat"));
        _notificationDispatcher = new NotificationDispatcher(recoveryCoordinator,
            [new WindowsNotificationChannel(_trayIcon!, Dispatcher),
             new TelegramNotificationChannel(_telegramCredentialStore)], notificationSettings);
        _localTradingScenario = LocalTradingScenario.TryCreate(
            LocalTradingScenario.DefaultDirectory(recoveryScope.ProfileRoot), settings.Profile,
            out _localScenarioUnavailableReason);
        _mainWindow = new MainWindow(settingsStore, settings, _wakeProtection, _sessionService,
            recoveryCoordinator: _recoveryCoordinator,
            notificationDispatcher: _notificationDispatcher,
            telegramCredentialStore: _telegramCredentialStore,
            processRecoveryStatus: _processRecoveryStatus,
            localTradingScenario: _localTradingScenario,
            localScenarioUnavailableReason: _localScenarioUnavailableReason);
        MainWindow = _mainWindow;
        _mainWindow.ShowInTaskbar = false;
        _notificationDispatcher.Start();
        SessionEnding += HandleSessionEnding;
        if (_localTradingScenario is not null)
            _ = _localTradingScenario.ResumeAsync();

        if (!arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
        MarkProcessReady();
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

    private async void ConfirmExit()
    {
        if (_mainWindow is null || _isClosing || _exitConfirmationOpen)
            return;

        _exitConfirmationOpen = true;
        try
        {
            var preparingExit = _localTradingScenario?.HoldAndReconcileForExitAsync();
            ShowMainWindow();
            var snapshot = preparingExit is null
                ? new LocalScenarioExitSnapshot(false, [], "Локальная сверка выхода недоступна.", [])
                : await preparingExit;
            while (!_isClosing)
            {
                var confirmation = new ExitConfirmationWindow(snapshot) { Owner = _mainWindow };
                var accepted = confirmation.ShowDialog() == true;
                var action = accepted ? confirmation.Action : ExitConfirmationAction.Return;
                if (action == ExitConfirmationAction.Return)
                {
                    if (_localTradingScenario is null)
                        return;
                    var resumed = await _localTradingScenario.ResumeAfterExitAsync();
                    if (resumed.Succeeded)
                        return;
                    snapshot = await _localTradingScenario.HoldAndReconcileForExitAsync();
                    snapshot = snapshot with { IsKnown = false, Status = resumed.Status };
                    continue;
                }
                if (action == ExitConfirmationAction.CancelEntries)
                {
                    if (_localTradingScenario is null)
                    {
                        snapshot = snapshot with { IsKnown = false, Status = "Точные идентификаторы заявок недоступны." };
                        continue;
                    }
                    var cancellation = await _localTradingScenario.CancelOpenEntriesForExitAsync(snapshot);
                    snapshot = cancellation.Snapshot;
                    if (snapshot.IsKnown && snapshot.OpenEntries.Count == 0)
                    {
                        ShutdownCleanly();
                        return;
                    }
                    continue;
                }
                if (action == ExitConfirmationAction.Leave)
                {
                    ShutdownCleanly();
                    return;
                }
            }
        }
        catch (Exception)
        {
            if (!_isClosing)
            {
                ShowMainWindow();
                _ = _localTradingScenario?.ResumeAfterExitAsync();
            }
        }
        finally
        {
            _exitConfirmationOpen = false;
        }
    }

    private void HandleSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        e.Cancel = false;
        StopForPlannedSessionEnding();
    }

    internal void PrepareForPlannedUpdate()
    {
        _processRecoveryScope?.DisableRecovery();
        ShutdownCleanly();
    }

    private void StopForPlannedSessionEnding()
    {
        _processRecoveryScope?.DisableRecovery();
        _processRecoveryScope?.LogRuntime($"session-ending-disable pid={Environment.ProcessId}");
        ShutdownCleanly();
    }

    private void ShutdownCleanly()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _processRecoveryScope?.DisableRecovery();
        _recoveryStopEvent?.Set();
        _mainWindow?.PrepareForApplicationClose();
        _localTradingScenario?.Dispose();
        _localTradingScenario = null;
        _notificationDispatcher?.Dispose();
        _notificationDispatcher = null;
        _sessionService?.Close();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _wakeProtection?.Dispose();
        _mainWindow?.AllowApplicationClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _testStopWait?.Unregister(null);
        _testDisableWait?.Unregister(null);
        _testSessionEndingWait?.Unregister(null);
        _testConfirmedExitWait?.Unregister(null);
        _openWait?.Unregister(null);
        _uninstallWait?.Unregister(null);
        _openEvent?.Dispose();
        _uninstallEvent?.Dispose();
        _recoveryReadyEvent?.Dispose();
        _recoveryStopEvent?.Dispose();
        _recoveryDisableEvent?.Dispose();
        _testOpenSignalEvent?.Dispose();
        _testSessionEndingEvent?.Dispose();
        _testConfirmedExitEvent?.Dispose();
        _recoveryObserverProcess?.Dispose();
        _notificationDispatcher?.Dispose();
        _trayIcon?.Dispose();
        _wakeProtection?.Dispose();
        _sessionService?.Dispose();

        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static int RequestExistingInstanceClose(ProcessRecoveryScope scope)
    {
        using var mutex = new Mutex(initiallyOwned: false, scope.ApplicationMutexName, out var createdNew);
        if (!createdNew)
        {
            EventWaitHandle? shutdown = null;
            for (var attempt = 0; attempt < 10 && shutdown is null; attempt++)
            {
                try
                {
                    shutdown = EventWaitHandle.OpenExisting(scope.UninstallEventName);
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
        }

        try
        {
            using var observer = Mutex.OpenExisting(scope.ObserverMutexName);
            var acquired = false;
            try
            {
                acquired = observer.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
                return 4;
            observer.ReleaseMutex();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        return 0;
    }

    private static void SignalExistingInstance(ProcessRecoveryScope scope)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var open = EventWaitHandle.OpenExisting(scope.OpenEventName);
                foreach (var process in Process.GetProcessesByName("TrueNorth.TradeFlow"))
                {
                    using (process)
                    {
                        if (process.Id != Environment.ProcessId && scope.IsProcessReady(process))
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

    private void MarkProcessReady() => _recoveryReadyEvent?.Set();

    private static string? ReadProcessRecoveryStatus(
        ProcessRecoveryStatus application,
        ProcessRecoveryStatus worker)
    {
        if (!application.Available || !worker.Available)
            return ProcessRestartBudgetStore.StorageFailureStatus;

        return string.Join(Environment.NewLine,
            new[] { application.Message, worker.Message }.Where(message => !string.IsNullOrWhiteSpace(message)));
    }

    private static string? ReadArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(name.Length + 1)..];
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                return args[index + 1];
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
