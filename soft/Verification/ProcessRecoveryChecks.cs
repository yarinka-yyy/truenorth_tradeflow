using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Recovery;

namespace TrueNorth.TradeFlow.Verification;

internal static class ProcessRecoveryChecks
{
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private const string Venue = "Recovery Fixture Venue";
    private const string Account = "Recovery Fixture Account";

    internal static void CheckCore(Action<bool, string> check, string root)
    {
        CheckBudgets(check, root);
        CheckTestProfileValidation(check, root);
        CheckRegisteredProductIdentity(check, root);
        CheckReconciliationAndStop(check, root);
        CheckFailureEvent(check, root);
    }

    internal static void CheckRuntime(Action<bool, string> check, string root)
    {
        CheckBackgroundWorkerRuntime(check, root);
    }

    internal static void CheckApplicationRuntime(Action<bool, string> check, string root)
    {
        var configuredPath = Environment.GetEnvironmentVariable("TRUENORTH_TRADEFLOW_PROCESS_RECOVERY_EXE");
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Set TRUENORTH_TRADEFLOW_PROCESS_RECOVERY_EXE to the verified self-contained Release publish before --process-recovery-runtime.");
        var appHost = Path.GetFullPath(configuredPath);
        var configuredBaseline = Environment.GetEnvironmentVariable("TRUENORTH_TRADEFLOW_PROCESS_RECOVERY_BASELINE_EXE");
        if (string.IsNullOrWhiteSpace(configuredBaseline))
            throw new InvalidOperationException("Set TRUENORTH_TRADEFLOW_PROCESS_RECOVERY_BASELINE_EXE to the verified 0.1.0 self-contained publish before --process-recovery-runtime.");
        var baselineExe = Path.GetFullPath(configuredBaseline);
        CheckRejectedTestHost(check, appHost, root);
        CheckApplicationObserverRuntime(check, root);
        CheckPlannedUpdateRuntime(check, appHost, root);
        CheckLegacyManualShutdownRuntime(check, baselineExe, root);
        CheckManualPrimaryExitRace(check, appHost, root);
        CheckReplacementReadinessTimeout(check, appHost, root);
    }

    private static void CheckPlannedUpdateRuntime(Action<bool, string> check, string appHost, string root)
    {
        CheckCommittedUpdateRuntime(check, appHost, Path.Combine(root, "planned-update-commit"));
        CheckRefusedUpdateRuntime(check, appHost, Path.Combine(root, "planned-update-refused"));
        CheckTimedOutUpdateRuntime(check, appHost, Path.Combine(root, "planned-update-timeout"));
        CheckCancelledUpdateRuntime(check, appHost, Path.Combine(root, "planned-update-cancelled"));
    }

    private static void CheckCommittedUpdateRuntime(Action<bool, string> check, string appHost, string profileRoot)
    {
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? primary = null;
        Process? observer = null;
        try
        {
            primary = StartApplicationHost(appHost, profileRoot, startup: true);
            owned.Add(primary);
            CheckProcessReady(primary, scope, TimeSpan.FromSeconds(15));
            check(WaitForNamedEvent(scope.ObserverReadyEventName, TimeSpan.FromSeconds(15)),
                "the current app publishes its isolated recovery observer before update preparation");
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start", TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);

            using var request = EventWaitHandle.OpenExisting(scope.UpdateRequestEventName);
            using var ready = EventWaitHandle.OpenExisting(scope.UpdateReadyEventName);
            using var refused = EventWaitHandle.OpenExisting(scope.UpdateRefusedEventName);
            using var commit = EventWaitHandle.OpenExisting(scope.UpdateCommitEventName);
            using var disable = EventWaitHandle.OpenExisting(scope.DisableEventName);
            ready.Reset();
            refused.Reset();
            commit.Reset();
            request.Set();
            var readySignaled = ready.WaitOne(TimeSpan.FromSeconds(10));
            var refusedSignaled = refused.WaitOne(0);
            check(readySignaled && !refusedSignaled && !primary.HasExited,
                "the current app confirms preparation and stays alive until the installer commits");
            commit.Set();

            var appExited = primary.WaitForExit(15000);
            var observerExited = observer.WaitForExit(15000);
            var committed = ReadLogEvents(scope.RuntimeLogPath!, "update-committed")
                .Any(fields => fields.GetValueOrDefault("pid") == primary.Id.ToString(CultureInfo.InvariantCulture));
            check(appExited && observerExited && disable.WaitOne(0)
                  && committed && owned.All(process => process.WaitForExit(0)),
                "committed update disables recovery, exits the owned app and observer, and leaves no restart");
            Console.WriteLine($"PROCESS-UPDATE committed app={primary.Id} observer={observer.Id} exited={appExited}/{observerExited}");
        }
        finally
        {
            scope.DisableRecovery();
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckRefusedUpdateRuntime(Action<bool, string> check, string appHost, string profileRoot)
    {
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? primary = null;
        Process? observer = null;
        try
        {
            primary = StartApplicationHost(appHost, profileRoot, startup: true, simulateConfirmedExit: true,
                extraArguments: ["--recovery-test-update-refuse"]);
            owned.Add(primary);
            CheckProcessReady(primary, scope, TimeSpan.FromSeconds(15));
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start", TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);

            using var request = EventWaitHandle.OpenExisting(scope.UpdateRequestEventName);
            using var refused = EventWaitHandle.OpenExisting(scope.UpdateRefusedEventName);
            using var disable = EventWaitHandle.OpenExisting(scope.DisableEventName);
            request.Set();
            var refusedSignaled = refused.WaitOne(TimeSpan.FromSeconds(5));
            check(refusedSignaled && !primary.HasExited && !observer.HasExited && !disable.WaitOne(0),
                "update refusal keeps the app and observer running without disabling recovery");
            using (var confirmedExit = EventWaitHandle.OpenExisting(scope.TestConfirmedExitEventName))
                confirmedExit.Set();
            var appExited = primary.WaitForExit(10000);
            var observerExited = observer.WaitForExit(10000);
            check(appExited && observerExited,
                "the refused update can be followed by the normal confirmed exit path");
        }
        finally
        {
            scope.DisableRecovery();
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckTimedOutUpdateRuntime(Action<bool, string> check, string appHost, string profileRoot)
    {
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? primary = null;
        Process? observer = null;
        try
        {
            primary = StartApplicationHost(appHost, profileRoot, startup: true, simulateConfirmedExit: true,
                extraArguments: ["--recovery-test-update-ignore"]);
            owned.Add(primary);
            CheckProcessReady(primary, scope, TimeSpan.FromSeconds(15));
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start", TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);

            using var request = EventWaitHandle.OpenExisting(scope.UpdateRequestEventName);
            using var ready = EventWaitHandle.OpenExisting(scope.UpdateReadyEventName);
            using var refused = EventWaitHandle.OpenExisting(scope.UpdateRefusedEventName);
            using var cancel = EventWaitHandle.OpenExisting(scope.UpdateCancelEventName);
            request.Set();
            var responseReceived = ready.WaitOne(500) || refused.WaitOne(0);
            cancel.Set();
            cancel.Reset();
            check(!responseReceived && !primary.HasExited && !observer.HasExited
                  && !ReadLogEvents(scope.RuntimeLogPath!, "update-committed").Any(),
                "a preparation timeout cancels the request without closing the app late or stopping its observer");
            using (var confirmedExit = EventWaitHandle.OpenExisting(scope.TestConfirmedExitEventName))
                confirmedExit.Set();
            check(primary.WaitForExit(10000) && observer.WaitForExit(10000),
                "a timed-out update leaves the app available for a later normal exit");
        }
        finally
        {
            scope.DisableRecovery();
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckCancelledUpdateRuntime(Action<bool, string> check, string appHost, string profileRoot)
    {
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? primary = null;
        Process? observer = null;
        try
        {
            primary = StartApplicationHost(appHost, profileRoot, startup: true, simulateConfirmedExit: true,
                extraArguments: ["--recovery-test-update-delay"]);
            owned.Add(primary);
            CheckProcessReady(primary, scope, TimeSpan.FromSeconds(15));
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start", TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);

            using var request = EventWaitHandle.OpenExisting(scope.UpdateRequestEventName);
            using var cancel = EventWaitHandle.OpenExisting(scope.UpdateCancelEventName);
            using var disable = EventWaitHandle.OpenExisting(scope.DisableEventName);
            request.Set();
            WaitForLogEvent(scope.RuntimeLogPath!, "update-preparing", TimeSpan.FromSeconds(5));
            cancel.Set();
            WaitForLogEvent(scope.RuntimeLogPath!, "update-cancelled", TimeSpan.FromSeconds(5));
            Thread.Sleep(6000);
            check(!primary.HasExited && !observer.HasExited && !disable.WaitOne(0)
                  && !ReadLogEvents(scope.RuntimeLogPath!, "update-committed").Any(),
                "cancelling before readiness prevents a delayed handler from shutting down after the setup timed out");
            using (var confirmedExit = EventWaitHandle.OpenExisting(scope.TestConfirmedExitEventName))
                confirmedExit.Set();
            check(primary.WaitForExit(10000) && observer.WaitForExit(10000),
                "a cancelled delayed update leaves both owned processes available for ordinary shutdown");
        }
        finally
        {
            scope.DisableRecovery();
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckLegacyManualShutdownRuntime(Action<bool, string> check, string baselineExe, string root)
    {
        var appHost = Path.GetFullPath(baselineExe);
        var runtimeConfigPath = Path.Combine(Path.GetDirectoryName(appHost)!, "TrueNorth.TradeFlow.runtimeconfig.json");
        if (!File.Exists(appHost) || !File.Exists(runtimeConfigPath) || !IsSelfContainedRuntimeConfig(runtimeConfigPath))
            throw new InvalidOperationException($"Legacy process check requires the verified 0.1.0 self-contained executable: {appHost}");
        if (FileVersionInfo.GetVersionInfo(appHost).FileVersion?.StartsWith("0.1.0.", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("The legacy process check executable is not file version 0.1.0.");

        var profileRoot = Path.Combine(root, "legacy-manual-shutdown");
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? primary = null;
        Process? observer = null;
        try
        {
            primary = StartApplicationHost(appHost, profileRoot, startup: true, simulateConfirmedExit: true);
            owned.Add(primary);
            CheckProcessReady(primary, scope, TimeSpan.FromSeconds(15));
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start", TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);
            using var disable = EventWaitHandle.OpenExisting(scope.DisableEventName);
            using (var confirmedExit = EventWaitHandle.OpenExisting(scope.TestConfirmedExitEventName))
                confirmedExit.Set();

            var appExited = primary.WaitForExit(10000);
            var observerExited = observer.WaitForExit(10000);
            check(appExited && observerExited && disable.WaitOne(0)
                  && ReadLogEvents(scope.RuntimeLogPath!, "planned-stop")
                      .Any(fields => fields.GetValueOrDefault("pid") == primary.Id.ToString(CultureInfo.InvariantCulture)),
                "baseline 0.1.0 normal exit disables its observer and leaves no automatic restart");

            using var appMutex = new Mutex(false, scope.ApplicationMutexName);
            using var observerMutex = new Mutex(false, scope.ObserverMutexName);
            var appLocked = appMutex.WaitOne(0);
            var observerLocked = observerMutex.WaitOne(0);
            check(appLocked && observerLocked,
                "the installer can hold both legacy process mutexes after owner-mediated shutdown");
            if (appLocked && observerLocked)
            {
                using var duplicate = StartApplicationHost(appHost, profileRoot, startup: true);
                check(duplicate.WaitForExit(10000) && duplicate.ExitCode == 0
                      && ReadLogEvents(scope.RuntimeLogPath!, "host-ready").Count == 1,
                    "holding the legacy app mutex prevents a late 0.1.0 startup during replacement");
                observerMutex.ReleaseMutex();
                appMutex.ReleaseMutex();
            }
            Console.WriteLine($"PROCESS-UPDATE legacy app={primary.Id} observer={observer.Id} exited={appExited}/{observerExited}");
        }
        finally
        {
            scope.DisableRecovery();
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    internal static void CheckNativeWpf(Action<bool, string> check, string root)
    {
        var settingsPath = Path.Combine(root, "process-recovery-ui", "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var controller = new WakeProtectionController(false, _ => true);
        var statusMessage = "Автоматическое восстановление приложения остановлено: исчерпаны 3 попытки за 5 минут.";
        var window = new MainWindow(settingsStore, settingsStore.Load(), controller,
            processRecoveryStatus: statusMessage);
        try
        {
            window.Show();
            window.UpdateLayout();
            var status = (TextBlock)window.FindName("ProcessRecoveryStatusText")!;
            check(status.Visibility == Visibility.Visible && status.Text == statusMessage,
                "the overview shows the exact process-recovery status when a live window is available");
            window.UpdateProcessRecoveryStatus(null);
            check(status.Visibility == Visibility.Collapsed,
                "the process-recovery status can be cleared after a later successful recovery");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
        }
    }

    internal static int RunWorker(string[] args)
    {
        if (!TryOption(args, "--process-recovery-worker-ready", out var readyName)
            || !TryOption(args, "--process-recovery-worker-stop", out var stopName))
            return 2;

        try
        {
            using var stop = EventWaitHandle.OpenExisting(stopName);
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            ready.Set();
            stop.WaitOne();
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 3;
        }
    }

    private static void CheckBudgets(Action<bool, string> check, string root)
    {
        var clock = new FakeClock(InitialTime);
        var budget = new ProcessRestartBudgetStore(Path.Combine(root, "process-budget", "state.json"), clock);
        var first = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
        var early = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
        var independent = budget.TryBeginAttempt(ProcessRecoveryKind.BackgroundWorker);
        check(first.Kind == ProcessRestartDecisionKind.Started
              && early.Kind == ProcessRestartDecisionKind.Wait
              && early.Delay == TimeSpan.FromSeconds(10)
              && independent.Kind == ProcessRestartDecisionKind.Started,
            "application and background-process budgets are independent and enforce a ten-second minimum gap");

        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        var second = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        var third = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
        var exhausted = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
        check(second.Kind == ProcessRestartDecisionKind.Started
              && third.Kind == ProcessRestartDecisionKind.Started
              && exhausted.Kind == ProcessRestartDecisionKind.Exhausted
              && exhausted.Status == ProcessRestartBudgetStore.ExhaustedStatus(ProcessRecoveryKind.Application)
              && budget.ReadStatus(ProcessRecoveryKind.Application).Message == exhausted.Status,
            "the fourth application restart is blocked and its exact status is persisted after three attempts in five minutes");

        clock.UtcNow = clock.UtcNow.Add(ProcessRestartBudgetStore.Window);
        var afterWindow = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
        check(afterWindow.Kind == ProcessRestartDecisionKind.Started,
            "attempts older than the five-minute window no longer consume the restart budget");

        var corruptPath = Path.Combine(root, "process-budget-corrupt", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllText(corruptPath, "{ broken");
        var unavailable = new ProcessRestartBudgetStore(corruptPath, clock)
            .TryBeginAttempt(ProcessRecoveryKind.Application);
        check(unavailable.Kind == ProcessRestartDecisionKind.Unavailable,
            "a damaged restart journal blocks process recovery instead of resetting its limits");

        var production = ProcessRecoveryScope.Create(null);
        var isolatedA = ProcessRecoveryScope.Create(Path.Combine(root, "profile-a"));
        var isolatedB = ProcessRecoveryScope.Create(Path.Combine(root, "profile-b"));
        check(production.ApplicationMutexName == @"Local\TrueNorth.TradeFlow"
              && isolatedA.ApplicationMutexName != isolatedB.ApplicationMutexName
              && isolatedA.TestSessionEndingEventName != isolatedB.TestSessionEndingEventName
              && isolatedA.TestConfirmedExitEventName != isolatedB.TestConfirmedExitEventName
              && isolatedA.ReadyEventName(50, 100) != isolatedA.ReadyEventName(51, 100)
              && isolatedA.ReadyEventName(50, 100) != isolatedA.ReadyEventName(50, 101),
            "production keeps the existing instance mutex while test profiles and readiness handshakes stay isolated by process identity");
    }

    private static void CheckTestProfileValidation(Action<bool, string> check, string root)
    {
        var safeProfile = Path.Combine(root, "validated-isolated-profile");
        Directory.CreateDirectory(safeProfile);
        var productionProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrueNorth TradeFlow");
        var productionAlias = Path.Combine(Path.GetDirectoryName(productionProfile)!, ".", "TrueNorth TradeFlow", "..",
            "TrueNorth TradeFlow");
        var environmentAlias = Path.Combine("%LOCALAPPDATA%", "TrueNorth TradeFlow");
        var extendedAlias = "\\\\?\\" + productionProfile;

        var missingProfileAccepted = ProcessRecoveryStartupProfile.TryResolve(["--recovery-test-host"],
            out _, out _, out _);
        var signalWithoutHostAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-session-ending", "--recovery-test-profile=" + safeProfile],
            out _, out _, out _);
        var updateRefuseAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-update-refuse", "--recovery-test-profile=" + safeProfile],
            out var updateRefuseHost, out _, out _);
        var updateIgnoreAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-update-ignore", "--recovery-test-profile=" + safeProfile],
            out var updateIgnoreHost, out _, out _);
        var updateDelayAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-update-delay", "--recovery-test-profile=" + safeProfile],
            out var updateDelayHost, out _, out _);
        var updateSwitchWithoutHostAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-update-refuse", "--recovery-test-profile=" + safeProfile],
            out _, out _, out _);
        var arbitraryProfileRead = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-profile=" + safeProfile], out var ordinaryTestHost, out var ordinaryProfile, out _);
        check(!missingProfileAccepted && !signalWithoutHostAccepted
              && updateRefuseAccepted && updateRefuseHost
              && updateIgnoreAccepted && updateIgnoreHost
              && updateDelayAccepted && updateDelayHost && !updateSwitchWithoutHostAccepted
              && arbitraryProfileRead && !ordinaryTestHost && ordinaryProfile is null,
            "update test switches require the explicit isolated host and a random profile argument is ignored by normal startup");

        var productionAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-profile=" + productionProfile], out _, out _, out _);
        var aliasAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-profile=" + productionAlias], out _, out _, out _);
        var environmentAliasAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-profile=" + environmentAlias], out _, out _, out _);
        var extendedAliasAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-profile=" + extendedAlias], out _, out _, out _);
        var isolatedAccepted = ProcessRecoveryStartupProfile.TryResolve(
            ["--recovery-test-host", "--recovery-test-profile=" + safeProfile],
            out var isolatedHost, out var isolatedPath, out _);
        check(!productionAccepted && !aliasAccepted && !environmentAliasAccepted && !extendedAliasAccepted
              && isolatedAccepted && isolatedHost
              && string.Equals(Path.GetFullPath(safeProfile), isolatedPath, StringComparison.OrdinalIgnoreCase),
            "test profile validation rejects production LocalAppData, environment, extended-path, and dot-segment aliases before creating a process scope");
    }

    private static void CheckRegisteredProductIdentity(Action<bool, string> check, string root)
    {
        var installLocation = Path.Combine(root, "registered-product");
        var installedExecutable = Path.Combine(installLocation, ProcessRecoveryProductIdentity.ExecutableName);
        var portableExecutable = Path.Combine(root, "portable-copy", ProcessRecoveryProductIdentity.ExecutableName);
        check(ProcessRecoveryProductIdentity.Matches(installedExecutable, ProcessRecoveryProductIdentity.DisplayName,
                  installLocation)
              && !ProcessRecoveryProductIdentity.Matches(portableExecutable,
                  ProcessRecoveryProductIdentity.DisplayName, installLocation)
              && !ProcessRecoveryProductIdentity.Matches(installedExecutable, "Other Product", installLocation)
              && !ProcessRecoveryProductIdentity.Matches(installedExecutable,
                  ProcessRecoveryProductIdentity.DisplayName, "relative-install-path"),
            "only the exact executable registered for this AppId and product name can accept an update request");
    }

    private static void CheckReconciliationAndStop(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "process-reconcile", "recovery-state.json");
        var settingsStore = new SettingsStore(Path.Combine(root, "process-reconcile", "settings.json"));
        var profile = settingsStore.Load().Profile;
        profile.Settings.ReadRetryCount = 1;
        profile.Settings.ReadRetryDelaySeconds = 10;
        profile.Settings.RecoveryCheckSeconds = 60;
        profile.SavedOperatingIntent = false;
        check(settingsStore.Save(profile).Succeeded, "the Stop-preservation profile fixture is prepared");

        var clock = new FakeClock(InitialTime);
        var source = new CompleteSnapshotSource(clock);
        var recovery = new RecoveryCoordinator(new RecoveryJournalStore(path), profile.Settings, clock, source);
        var episode = recovery.ReportFailureAsync(Venue, Account,
            "The background process ended unexpectedly.", profile.Settings).GetAwaiter().GetResult();
        var paused = recovery.HasPendingRecovery && !recovery.IsEntryAllowed(Venue, profile.SavedOperatingIntent);
        clock.UtcNow = recovery.State.Venues.Single().NextCheckAt!.Value;
        source.ObservedAt = clock.UtcNow;
        var reconciled = recovery.CheckDueAsync(Venue).GetAwaiter().GetResult();
        var reloadedProfile = settingsStore.Load().Profile;
        check(episode is not null && paused && reconciled && !recovery.HasPendingRecovery
              && !recovery.IsEntryAllowed(Venue, reloadedProfile.SavedOperatingIntent)
              && !reloadedProfile.SavedOperatingIntent
              && recovery.State.Events.Count(item => item.Type == RecoveryEventType.Recovered) == 1,
            "a background-process restart remains paused until A completes a full reconciliation and never changes saved Stop");
    }

    private static void CheckFailureEvent(Action<bool, string> check, string root)
    {
        var settings = new AppSettings { ReadRetryCount = 3, ReadRetryDelaySeconds = 10, RecoveryCheckSeconds = 60 };
        var recovery = new RecoveryCoordinator(
            new RecoveryJournalStore(Path.Combine(root, "process-budget-error", "recovery-state.json")), settings,
            new FakeClock(InitialTime));
        var status = ProcessRestartBudgetStore.ExhaustedStatus(ProcessRecoveryKind.BackgroundWorker);
        var errorSettings = settings.Copy();
        errorSettings.ReadRetryCount = 0;
        var episode = recovery.ReportFailureAsync(Venue, Account, status, errorSettings).GetAwaiter().GetResult();
        var recoveryEvent = recovery.State.Events.Single();
        var message = NotificationMessageFactory.FromRecoveryEvent(recoveryEvent);
        check(episode == recoveryEvent.EpisodeId
              && recoveryEvent.Type == RecoveryEventType.Error
              && recoveryEvent.Venue == Venue && recoveryEvent.Account == Account
              && message.Category == NotificationCategory.Error
              && recoveryEvent.InternalDetail == status
              && recovery.HasPendingRecovery
              && !recovery.IsEntryAllowed(Venue, savedOperatingIntent: true)
              && recovery.IsEntryAllowed("Independent Fixture Venue", savedOperatingIntent: true),
            "an exhausted worker budget creates an exact venue/account error event without blocking another venue");

        var workerStatus = new ProcessRecoveryStatus(true, status);
        var noApplicationStatus = new ProcessRecoveryStatus(true, null);
        var missingEventStatus = ProcessRecoveryStatusRouting.UnknownScopeFailure(
            noApplicationStatus, workerStatus, new RecoveryJournalState());
        var exactEventStatus = ProcessRecoveryStatusRouting.UnknownScopeFailure(
            noApplicationStatus, workerStatus, recovery.State);
        var applicationFailureStatus = ProcessRecoveryStatusRouting.UnknownScopeFailure(
            new ProcessRecoveryStatus(true, "Application restart budget exhausted."), workerStatus, recovery.State);
        check(exactEventStatus == string.Empty && missingEventStatus == status
              && applicationFailureStatus?.Contains("Application restart budget exhausted.", StringComparison.Ordinal) == true
              && recovery.IsEntryAllowed("Independent Fixture Venue", savedOperatingIntent: true),
            "an existing exact worker error stays scoped on the next launch, while missing scope and app failure fail closed");
    }

    private static void CheckApplicationObserverRuntime(Action<bool, string> check, string root)
    {
        var configuredPath = Environment.GetEnvironmentVariable("TRUENORTH_TRADEFLOW_PROCESS_RECOVERY_EXE");
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Set TRUENORTH_TRADEFLOW_PROCESS_RECOVERY_EXE to the verified self-contained Release publish before --process-recovery-runtime.");
        var appHost = Path.GetFullPath(configuredPath);
        var runtimeConfigPath = Path.Combine(Path.GetDirectoryName(appHost)!, "TrueNorth.TradeFlow.runtimeconfig.json");
        if (!File.Exists(appHost) || !File.Exists(runtimeConfigPath) || !IsSelfContainedRuntimeConfig(runtimeConfigPath))
            throw new InvalidOperationException($"Process recovery runtime requires the win-x64 self-contained 10.0.14 publish; path validation failed: {appHost}");
        check(true, $"the isolated process test uses the verified self-contained Release executable: {appHost}");

        var profileRoot = Path.Combine(root, "process-observer-runtime");
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? primary = null;
        Process? duplicate = null;
        Process? observer = null;
        Process? restarted = null;
        Process? prepareUninstall = null;
        Process? sessionEnding = null;
        Process? sessionEndingObserver = null;
        try
        {
            primary = StartApplicationHost(appHost, profileRoot, startup: true);
            owned.Add(primary);
            CheckProcessReady(primary, scope, TimeSpan.FromSeconds(15));
            check(WaitForNamedEvent(scope.ObserverReadyEventName, TimeSpan.FromSeconds(15)),
                $"same-EXE observer completed its startup handshake for app PID {primary.Id}");

            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start", TimeSpan.FromSeconds(5));
            var observerPid = int.Parse(observerFields["pid"], CultureInfo.InvariantCulture);
            observer = Process.GetProcessById(observerPid);
            owned.Add(observer);
            check(observer.Id != primary.Id && observerFields["target-pid"] == primary.Id.ToString(CultureInfo.InvariantCulture)
                  && observerFields["target-start"] == ProcessRecoveryScope.StartMarker(primary).ToString(CultureInfo.InvariantCulture),
                $"observer PID {observer.Id} tracks the exact ready app PID/start identity {primary.Id}/{observerFields["target-start"]}");

            using var openSignal = EventWaitHandle.OpenExisting(scope.TestOpenSignalEventName);
            duplicate = StartApplicationHost(appHost, profileRoot, startup: false);
            owned.Add(duplicate);
            var signalReceived = openSignal.WaitOne(TimeSpan.FromSeconds(5));
            var activation = WaitForLogEvent(scope.RuntimeLogPath!, "test-open-signal",
                fields => fields.TryGetValue("pid", out var value) && value == primary.Id.ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(5));
            check(signalReceived && duplicate.WaitForExit(10000) && duplicate.ExitCode == 0 && !primary.HasExited
                  && activation["pid"] == primary.Id.ToString(CultureInfo.InvariantCulture),
                $"manual second launch PID {duplicate.Id} exited {duplicate.ExitCode}, delivered the open signal to primary PID {primary.Id}, and created no second UI host");

            var originalPid = primary.Id;
            KillOwnedProcess(primary, appHost);
            check(primary.WaitForExit(10000), $"owned app PID {originalPid} exited after the test-only crash");

            var restartFields = WaitForLogEvent(scope.RuntimeLogPath!, "ready",
                fields => fields.TryGetValue("pid", out var value)
                    && int.TryParse(value, out var pid) && pid != originalPid,
                TimeSpan.FromSeconds(15));
            var restartPid = int.Parse(restartFields["pid"], CultureInfo.InvariantCulture);
            restarted = Process.GetProcessById(restartPid);
            owned.Add(restarted);
            CheckProcessReady(restarted, scope, TimeSpan.FromSeconds(5));
            var launchFields = ReadLogEvents(scope.RuntimeLogPath!, "restart-start")
                .Last(fields => fields.TryGetValue("pid", out var value) && value == restartPid.ToString(CultureInfo.InvariantCulture));
            var hostReadyFields = ReadLogEvents(scope.RuntimeLogPath!, "host-ready")
                .Last(fields => fields.TryGetValue("pid", out var value) && value == restartPid.ToString(CultureInfo.InvariantCulture));
            var attemptIdIsValid = Guid.TryParse(launchFields.GetValueOrDefault("attempt"), out var attemptId)
                && attemptId != Guid.Empty;
            check(restartPid != originalPid && restartPid != observer.Id
                  && launchFields["start"] == restartFields["start"]
                  && attemptIdIsValid
                  && hostReadyFields["startup"] == "true",
                $"observer relaunched the same EXE as PID {restartPid}, verified its ready handshake/start marker, and preserved --startup");

            prepareUninstall = StartPrepareUninstall(appHost, profileRoot);
            owned.Add(prepareUninstall);
            var restartedExitedOnUninstall = restarted.WaitForExit(10000);
            var observerExitedOnUninstall = observer.WaitForExit(10000);
            check(prepareUninstall.WaitForExit(20000) && prepareUninstall.ExitCode == 0
                  && restartedExitedOnUninstall && observerExitedOnUninstall,
                $"--prepare-uninstall exited {prepareUninstall.ExitCode} after owned app PID {restartPid} and observer PID {observer.Id} stopped");
            check(ReadLogEvents(scope.RuntimeLogPath!, "planned-stop").Any(fields => fields["pid"] == restartPid.ToString(CultureInfo.InvariantCulture))
                  && owned.All(process => process.WaitForExit(0)),
                "planned uninstall stopped recovery before shutdown and left no owned application or observer process");

            sessionEnding = StartApplicationHost(appHost, profileRoot, startup: false, simulateSessionEnding: true);
            owned.Add(sessionEnding);
            var sessionHostReady = WaitForLogEvent(scope.RuntimeLogPath!, "host-ready",
                fields => fields.TryGetValue("pid", out var value) && value == sessionEnding.Id.ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(10));
            var sessionObserverFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start",
                fields => fields.TryGetValue("target-pid", out var value) && value == sessionEnding.Id.ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(10));
            sessionEndingObserver = Process.GetProcessById(int.Parse(sessionObserverFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(sessionEndingObserver);
            using (var sessionEndingSignal = EventWaitHandle.OpenExisting(scope.TestSessionEndingEventName))
                sessionEndingSignal.Set();
            var sessionObserverExited = sessionEndingObserver.WaitForExit(10000);
            check(sessionEnding.WaitForExit(10000) && sessionEnding.ExitCode == 0
                  && sessionHostReady["start"] == sessionObserverFields["target-start"]
                  && ReadLogEvents(scope.RuntimeLogPath!, "session-ending-disable")
                      .Any(fields => fields["pid"] == sessionEnding.Id.ToString(CultureInfo.InvariantCulture))
                  && ReadLogEvents(scope.RuntimeLogPath!, "planned-stop")
                      .Any(fields => fields["pid"] == sessionEnding.Id.ToString(CultureInfo.InvariantCulture))
                  && sessionObserverExited
                  && owned.All(process => process.WaitForExit(0)),
                $"the dev session-ending handler disabled recovery before shutdown for PID {sessionEnding.Id}, then its observer exited without a restart");

            Console.WriteLine($"PROCESS-RECOVERY app initial={originalPid} observer={observer.Id} duplicate={duplicate.Id} "
                + $"restarted={restartPid} session-ending={sessionEnding.Id}/{sessionEndingObserver.Id} "
                + $"observer-wait-exited={observerExitedOnUninstall}/{sessionObserverExited} "
                + $"restart-wait-exited={restartedExitedOnUninstall} "
                + $"direct-child-exit-codes={primary.ExitCode}/{duplicate.ExitCode}/{prepareUninstall.ExitCode}/{sessionEnding.ExitCode}");
        }
        finally
        {
            scope.DisableRecovery();
            if (observer is not null)
                observer.WaitForExit(5000);
            if (sessionEndingObserver is not null)
                sessionEndingObserver.WaitForExit(5000);
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckRejectedTestHost(Action<bool, string> check, string appHost, string root)
    {
        var ignoredEnvironmentProfile = Path.Combine(root, "ignored-test-profile-environment");
        Directory.CreateDirectory(ignoredEnvironmentProfile);
        var ignoredEnvironmentLog = Path.Combine(ignoredEnvironmentProfile, "process-recovery-runtime.log");
        var productionProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrueNorth TradeFlow");
        var productionAlias = Path.Combine(Path.GetDirectoryName(productionProfile)!, ".", "TrueNorth TradeFlow", "..",
            "TrueNorth TradeFlow");
        var safeProfile = Path.Combine(root, "rejected-session-ending-profile");
        Directory.CreateDirectory(safeProfile);
        var safeLog = Path.Combine(safeProfile, "process-recovery-runtime.log");

        var missingArgumentExit = RunRejectedTestHost(appHost, ["--recovery-test-host"], ignoredEnvironmentProfile);
        var productionPathExit = RunRejectedTestHost(appHost,
            ["--recovery-test-host", "--recovery-test-profile=" + productionProfile], null);
        var productionAliasExit = RunRejectedTestHost(appHost,
            ["--recovery-test-host", "--recovery-test-profile=" + productionAlias], null);
        var sessionWithoutHostExit = RunRejectedTestHost(appHost,
            ["--recovery-test-session-ending", "--recovery-test-profile=" + safeProfile], null);
        check(missingArgumentExit == 2 && productionPathExit == 2 && productionAliasExit == 2
              && sessionWithoutHostExit == 2
              && !File.Exists(ignoredEnvironmentLog) && !File.Exists(safeLog),
            "invalid test switches exit before observer, mutex, or isolated runtime-log creation; environment-only, production, and dot-segment profiles are rejected");
        Console.WriteLine($"PROCESS-RECOVERY invalid-test-host exits={missingArgumentExit}/{productionPathExit}/{productionAliasExit}/{sessionWithoutHostExit} isolated-logs-absent=true");
    }

    private static int RunRejectedTestHost(string executable, IReadOnlyList<string> arguments, string? environmentProfile)
    {
        using var process = StartRejectedTestHost(executable, arguments, environmentProfile);
        if (process.WaitForExit(10000))
            return process.ExitCode;
        KillOwnedProcess(process, executable);
        process.WaitForExit(5000);
        return int.MinValue;
    }

    private static Process StartRejectedTestHost(string executable, IReadOnlyList<string> arguments,
        string? environmentProfile)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environmentProfile is not null)
            start.Environment["TRUENORTH_PROCESS_RECOVERY_TEST_PROFILE"] = environmentProfile;
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        return Process.Start(start)
            ?? throw new InvalidOperationException("The rejected test-host command did not start.");
    }

    private static (bool Exists, long Length, long LastWriteTicks) ReadFileStamp(string path)
    {
        if (!File.Exists(path))
            return (false, 0, 0);
        var file = new FileInfo(path);
        return (true, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static void CheckManualPrimaryExitRace(Action<bool, string> check, string appHost, string root)
    {
        var profileRoot = Path.Combine(root, "process-observer-manual-exit-race");
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var budget = new ProcessRestartBudgetStore(scope.ProcessRestartBudgetPath);
        var owned = new List<Process>();
        Process? initial = null;
        Process? observer = null;
        Process? manualPrimary = null;
        try
        {
            initial = StartApplicationHost(appHost, profileRoot, startup: true);
            owned.Add(initial);
            CheckProcessReady(initial, scope, TimeSpan.FromSeconds(15));
            check(WaitForNamedEvent(scope.ObserverReadyEventName, TimeSpan.FromSeconds(15)),
                $"manual-exit race observer became ready for initial PID {initial.Id}");
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start",
                fields => fields.TryGetValue("target-pid", out var value)
                    && value == initial.Id.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);

            var priorAttempt = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
            KillOwnedProcess(initial, appHost);
            check(priorAttempt.Kind == ProcessRestartDecisionKind.Started && initial.WaitForExit(10000),
                $"initial app PID {initial.Id} exited while one production-budget attempt kept the observer in its real ten-second wait");
            var waiting = WaitForLogEvent(scope.RuntimeLogPath!, "restart-wait", TimeSpan.FromSeconds(5));
            check(double.TryParse(waiting.GetValueOrDefault("seconds"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds <= 10,
                "observer entered its real bounded retry interval before a manual primary was started");

            manualPrimary = StartApplicationHost(appHost, profileRoot, startup: false, simulateConfirmedExit: true);
            owned.Add(manualPrimary);
            CheckProcessReady(manualPrimary, scope, TimeSpan.FromSeconds(10));
            var attached = WaitForLogEvent(scope.RuntimeLogPath!, "attached-existing",
                fields => fields.TryGetValue("pid", out var value)
                    && value == manualPrimary.Id.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(15));
            var initialReady = ReadLogEvents(scope.RuntimeLogPath!, "host-ready")
                .Single(fields => fields["pid"] == initial.Id.ToString(CultureInfo.InvariantCulture));
            var manualReady = ReadLogEvents(scope.RuntimeLogPath!, "host-ready")
                .Single(fields => fields["pid"] == manualPrimary.Id.ToString(CultureInfo.InvariantCulture));
            check(attached["start"] == ProcessRecoveryScope.StartMarker(manualPrimary).ToString(CultureInfo.InvariantCulture)
                  && initialReady["stop-id"] != manualReady["stop-id"],
                $"the waiting observer attached to manual primary PID {manualPrimary.Id} with its distinct stop identity");

            using (var confirmedExit = EventWaitHandle.OpenExisting(scope.TestConfirmedExitEventName))
                confirmedExit.Set();
            var manualExited = manualPrimary.WaitForExit(10000);
            var observerExited = observer.WaitForExit(10000);
            check(manualExited && manualPrimary.ExitCode == 0 && observerExited
                  && ReadLogEvents(scope.RuntimeLogPath!, "planned-stop")
                      .Any(fields => fields["pid"] == manualPrimary.Id.ToString(CultureInfo.InvariantCulture))
                  && ReadLogEvents(scope.RuntimeLogPath!, "test-confirmed-exit")
                      .Any(fields => fields["pid"] == manualPrimary.Id.ToString(CultureInfo.InvariantCulture))
                  && ReadLogEvents(scope.RuntimeLogPath!, "restart-start").Count == 0
                  && ReadLogEvents(scope.RuntimeLogPath!, "host-ready").Count == 2,
                $"confirmed Exit from adopted manual PID {manualPrimary.Id} stopped its observer without creating a third main PID");
            Console.WriteLine($"PROCESS-RECOVERY manual-exit-race initial={initial.Id} observer={observer.Id} "
                + $"manual={manualPrimary.Id} wait-seconds={seconds:0.###} exits={manualExited}/{observerExited}/{manualPrimary.ExitCode}");
        }
        finally
        {
            scope.DisableRecovery();
            if (observer is not null)
                observer.WaitForExit(5000);
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckReplacementReadinessTimeout(Action<bool, string> check, string appHost, string root)
    {
        var profileRoot = Path.Combine(root, "process-observer-readiness-timeout");
        Directory.CreateDirectory(profileRoot);
        var scope = ProcessRecoveryScope.Create(profileRoot);
        var owned = new List<Process>();
        Process? initial = null;
        Process? observer = null;
        Process? replacement = null;
        try
        {
            initial = StartApplicationHost(appHost, profileRoot, startup: true, neverReadyOnRestart: true);
            owned.Add(initial);
            CheckProcessReady(initial, scope, TimeSpan.FromSeconds(15));
            check(WaitForNamedEvent(scope.ObserverReadyEventName, TimeSpan.FromSeconds(15)),
                $"readiness-timeout observer became ready for initial PID {initial.Id}");
            var observerFields = WaitForLogEvent(scope.RuntimeLogPath!, "observer-start",
                fields => fields.TryGetValue("target-pid", out var value)
                    && value == initial.Id.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(5));
            observer = Process.GetProcessById(int.Parse(observerFields["pid"], CultureInfo.InvariantCulture));
            owned.Add(observer);

            KillOwnedProcess(initial, appHost);
            check(initial.WaitForExit(10000), $"initial app PID {initial.Id} exited before the bounded replacement test");
            var launch = WaitForLogEvent(scope.RuntimeLogPath!, "restart-start", TimeSpan.FromSeconds(10));
            var replacementPid = int.Parse(launch["pid"], CultureInfo.InvariantCulture);
            replacement = Process.GetProcessById(replacementPid);
            owned.Add(replacement);
            var unready = WaitForLogEvent(scope.RuntimeLogPath!, "host-unready",
                fields => fields.TryGetValue("pid", out var value) && value == replacementPid.ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(10));
            var timedOut = WaitForLogEvent(scope.RuntimeLogPath!, "ready-timeout",
                fields => fields.TryGetValue("pid", out var value) && value == replacementPid.ToString(CultureInfo.InvariantCulture),
                ProcessRecoveryObserver.ReplacementReadinessTimeout + TimeSpan.FromSeconds(10));
            var replacementExited = replacement.WaitForExit(10000);
            scope.DisableRecovery();
            var observerExited = observer.WaitForExit(10000);
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            var launches = ReadLogEvents(scope.RuntimeLogPath!, "restart-start");
            var exitedLaunches = ReadLogEvents(scope.RuntimeLogPath!, "ready-timeout")
                .Concat(ReadLogEvents(scope.RuntimeLogPath!, "restart-stopped"))
                .Select(fields => fields.GetValueOrDefault("pid"))
                .ToHashSet(StringComparer.Ordinal);
            var exitWasRecorded = int.TryParse(timedOut.GetValueOrDefault("exit"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out _);
            check(unready["start"] == launch["start"] && timedOut["start"] == launch["start"]
                  && replacementExited && observerExited && exitWasRecorded
                  && launches.Count is >= 1 and <= 2
                  && launches.All(fields => exitedLaunches.Contains(fields["pid"]))
                  && owned.All(process => process.WaitForExit(0))
                  && ReadLogEvents(scope.RuntimeLogPath!, "ready")
                      .All(fields => launches.All(launchFields => fields["pid"] != launchFields["pid"]))
                  && ReadLogEvents(scope.RuntimeLogPath!, "host-ready")
                      .All(fields => launches.All(launchFields => fields["pid"] != launchFields["pid"])),
                $"owned replacement PID {replacementPid} was stopped after the {ProcessRecoveryObserver.ReplacementReadinessTimeout.TotalSeconds:0}-second deadline and every bounded attempt exited");
            Console.WriteLine($"PROCESS-RECOVERY readiness-timeout initial={initial.Id} observer={observer.Id} "
                + $"replacements={string.Join(',', launches.Select(fields => fields["pid"]))} "
                + $"exited={replacementExited}/{observerExited} timeout-exit={timedOut["exit"]}");
        }
        finally
        {
            scope.DisableRecovery();
            if (observer is not null)
                observer.WaitForExit(5000);
            AddLoggedOwnedProcesses(owned, scope.RuntimeLogPath!, appHost);
            CleanupOwnedProcesses(owned, appHost);
        }
    }

    private static void CheckBackgroundWorkerRuntime(Action<bool, string> check, string root)
    {
        CheckWorkerReconcile(check, Path.Combine(root, "process-worker-recovered"), savedOperatingIntent: true,
            exhaustBudget: false);
        CheckWorkerReconcile(check, Path.Combine(root, "process-worker-stop"), savedOperatingIntent: false,
            exhaustBudget: false);
        CheckWorkerReconcile(check, Path.Combine(root, "process-worker-exhausted"), savedOperatingIntent: true,
            exhaustBudget: true);
    }

    private static void CheckWorkerReconcile(
        Action<bool, string> check,
        string root,
        bool savedOperatingIntent,
        bool exhaustBudget)
    {
        Directory.CreateDirectory(root);
        var clock = new FakeClock(InitialTime);
        var settings = new AppSettings { ReadRetryCount = 1, ReadRetryDelaySeconds = 10, RecoveryCheckSeconds = 60 };
        var source = new CompleteSnapshotSource(clock);
        var recovery = new RecoveryCoordinator(new RecoveryJournalStore(Path.Combine(root, "recovery-state.json")),
            settings, clock, source);
        if (savedOperatingIntent && !exhaustBudget)
            recovery.ReportFailureAsync("Unrelated Fixture Venue", "Unrelated Fixture Account",
                "An unrelated venue needs reconciliation.", settings).GetAwaiter().GetResult();
        var budget = new ProcessRestartBudgetStore(Path.Combine(root, "process-recovery-state.json"), clock);
        if (exhaustBudget)
        {
            for (var attempt = 0; attempt < ProcessRestartBudgetStore.MaximumAttempts; attempt++)
            {
                var result = budget.TryBeginAttempt(ProcessRecoveryKind.BackgroundWorker);
                if (attempt < ProcessRestartBudgetStore.MaximumAttempts - 1)
                    clock.UtcNow = clock.UtcNow.Add(ProcessRestartBudgetStore.MinimumInterval);
                check(result.Kind == ProcessRestartDecisionKind.Started,
                    "test-clock fixture prepares one consumed worker restart attempt");
            }
        }

        var workerPath = FindDotNetHost();
        var verificationAssembly = Path.Combine(AppContext.BaseDirectory, "TrueNorth.TradeFlow.Verification.dll");
        if (!File.Exists(verificationAssembly))
            throw new FileNotFoundException("Verification assembly for the test worker was not found.", verificationAssembly);
        var launches = new ConcurrentDictionary<int, WorkerLaunch>();
        var statuses = new ConcurrentQueue<string?>();
        var savedIntent = savedOperatingIntent;
        var reconcileCalls = 0;
        BackgroundProcessRecoverySupervisor? supervisor = null;
        try
        {
            supervisor = new BackgroundProcessRecoverySupervisor(recovery, budget,
                cancellationToken => StartWorker(workerPath, verificationAssembly, launches, cancellationToken),
                process =>
                {
                    if (launches.TryGetValue(process.Id, out var launch))
                        launch.Stop.Set();
                },
                async cancellationToken =>
                {
                    Interlocked.Increment(ref reconcileCalls);
                    var nextCheck = recovery.State.Venues.Single(state => state.Venue == Venue).NextCheckAt;
                    if (nextCheck is { } due)
                    {
                        clock.UtcNow = due;
                        source.ObservedAt = due;
                    }
                    return await recovery.CheckDueAsync(Venue, cancellationToken);
                },
                () => Volatile.Read(ref savedIntent), Venue, Account, settings,
                message => statuses.Enqueue(message));
            supervisor.StartAsync().GetAwaiter().GetResult();
            var initialPid = supervisor.CurrentProcessId
                ?? throw new InvalidOperationException("Initial background worker did not become ready.");
            var initial = Process.GetProcessById(initialPid);
            var initialStart = ProcessRecoveryScope.StartMarker(initial);
            KillOwnedProcess(initial, workerPath);
            check(initial.WaitForExit(10000), $"owned background worker PID {initialPid} exited with its actual process handle");

            if (!savedOperatingIntent)
                Volatile.Write(ref savedIntent, false);

            if (exhaustBudget)
            {
                var exhaustedStatus = ProcessRestartBudgetStore.ExhaustedStatus(ProcessRecoveryKind.BackgroundWorker);
                WaitForCondition(() => statuses.Contains(exhaustedStatus), TimeSpan.FromSeconds(10));
                WaitForCondition(() =>
                {
                    var exactErrorRecorded = recovery.State.Events.Any(item => item.Type == RecoveryEventType.Error
                        && item.Venue == Venue && item.Account == Account && item.InternalDetail == exhaustedStatus);
                    var storageReady = recovery.StorageReady;
                    var storageStatus = recovery.StorageStatus;
                    return exactErrorRecorded && storageReady && storageStatus == RecoveryJournalStatus.Loaded;
                }, TimeSpan.FromSeconds(10));
                var currentPid = supervisor.CurrentProcessId;
                var recoveryState = recovery.State;
                var errorEvent = recoveryState.Events.FirstOrDefault(item => item.Type == RecoveryEventType.Error
                    && item.Venue == Venue && item.Account == Account && item.InternalDetail == exhaustedStatus);
                var storageReady = recovery.StorageReady;
                var storageStatus = recovery.StorageStatus;
                var independentVenueAllowed = recovery.IsEntryAllowed("Independent Fixture Venue", savedOperatingIntent: true);
                var exhaustedStatusReported = statuses.Contains(exhaustedStatus);
                check(reconcileCalls == 1,
                    $"exhausted worker budget runs one full reconciliation (calls={reconcileCalls}, initial={initialPid}/{initialStart})");
                check(currentPid is null && launches.Count == 1,
                    $"exhausted worker budget leaves no current PID and does not restart the worker "
                    + $"(current={currentPid?.ToString() ?? "none"}, launches={launches.Count}, initial={initialPid}/{initialStart})");
                check(storageReady && storageStatus == RecoveryJournalStatus.Loaded && errorEvent is not null,
                    $"exhausted worker budget durably records its exact venue/account error "
                    + $"(ready={storageReady}, storage={storageStatus}, event={(errorEvent is null ? "missing" : $"{errorEvent.Type}/{errorEvent.Venue}/{errorEvent.Account}/{errorEvent.InternalDetail}")})");
                check(independentVenueAllowed,
                    $"exhausted worker budget leaves another venue entry-eligible "
                    + $"(allowed={independentVenueAllowed}, ready={storageReady}, storage={storageStatus}, "
                    + $"venues={string.Join(';', recoveryState.Venues.Select(state => $"{state.Venue}/{state.Account}:{state.Phase}"))})");
                check(exhaustedStatusReported,
                    $"exhausted worker budget publishes its exact status (reported={exhaustedStatusReported})");
            }
            else if (!savedOperatingIntent)
            {
                WaitForCondition(() => supervisor.CurrentProcessId is null && statuses.Any(message => message?.Contains("сохранённому Stop", StringComparison.Ordinal) == true),
                    TimeSpan.FromSeconds(10));
                check(reconcileCalls == 1 && supervisor.CurrentProcessId is null && launches.Count == 1
                      && !recovery.HasPendingRecovery && !recovery.IsEntryAllowed(Venue, savedIntent),
                    $"worker PID {initialPid} stayed stopped after reconciliation because the current saved intent is Stop");
            }
            else
            {
                WaitForCondition(() => supervisor.CurrentProcessId is { } pid && pid != initialPid,
                    TimeSpan.FromSeconds(10));
                var restartedPid = supervisor.CurrentProcessId!.Value;
                var restarted = Process.GetProcessById(restartedPid);
                check(reconcileCalls == 1 && restartedPid != initialPid && !restarted.HasExited
                      && ProcessRecoveryScope.StartMarker(restarted) > initialStart
                      && recovery.State.Events.Count(item => item.Type == RecoveryEventType.Recovered) == 1
                      && recovery.IsEntryAllowed(Venue, savedOperatingIntent: true)
                      && !recovery.IsEntryAllowed("Unrelated Fixture Venue", savedOperatingIntent: true),
                    $"worker PID {restartedPid} restarted after its own full reconciliation while another venue retained its independent pause");
                Volatile.Write(ref savedIntent, false);
                check(!recovery.IsEntryAllowed(Venue, savedIntent),
                    "the recovered worker still observes the existing saved Stop entry gate");
                restarted.Dispose();
            }

            Console.WriteLine($"PROCESS-RECOVERY worker scenario={(exhaustBudget ? "exhausted" : savedOperatingIntent ? "reconciled" : "stop")} "
                + $"initial={initialPid} attempts={budget.ReadStatus(ProcessRecoveryKind.BackgroundWorker).Message ?? "within-limit"} "
                + $"reconcile-calls={reconcileCalls} started={launches.Count}");
            initial.Dispose();
        }
        finally
        {
            if (supervisor is not null)
                supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (var launch in launches.Values)
            {
                try
                {
                    if (IsSameProcess(launch.Pid, launch.StartMarker, workerPath, out var process))
                    {
                        using (process)
                        {
                            if (!process.HasExited)
                                process.Kill(entireProcessTree: false);
                            process.WaitForExit(5000);
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                launch.Ready.Dispose();
                launch.Stop.Dispose();
            }
        }
    }

    private static async Task<Process> StartWorker(
        string executable,
        string verificationAssembly,
        ConcurrentDictionary<int, WorkerLaunch> launches,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var readyName = $@"Local\TrueNorth.TradeFlow.ProcessRecovery.Worker.Ready.{id}";
        var stopName = $@"Local\TrueNorth.TradeFlow.ProcessRecovery.Worker.Stop.{id}";
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(verificationAssembly);
        start.ArgumentList.Add("--process-recovery-test-worker");
        start.ArgumentList.Add("--process-recovery-worker-ready=" + readyName);
        start.ArgumentList.Add("--process-recovery-worker-stop=" + stopName);
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("The test background worker did not start.");
        var marker = ProcessRecoveryScope.StartMarker(process);
        var launch = new WorkerLaunch(process.Id, marker, stop, ready);
        launches[process.Id] = launch;
        try
        {
            if (!WaitForNamedEvent(readyName, TimeSpan.FromSeconds(10), process, cancellationToken))
                throw new InvalidOperationException("The test background worker did not complete its readiness handshake.");
            return process;
        }
        catch
        {
            stop.Set();
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            throw;
        }
    }

    private static Process StartApplicationHost(string executable, string profileRoot, bool startup,
        bool simulateSessionEnding = false, bool simulateConfirmedExit = false,
        bool neverReadyOnRestart = false, string[]? extraArguments = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--recovery-test-host");
        start.ArgumentList.Add("--recovery-test-profile");
        start.ArgumentList.Add(profileRoot);
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        if (startup)
            start.ArgumentList.Add("--startup");
        if (simulateSessionEnding)
            start.ArgumentList.Add("--recovery-test-session-ending");
        if (simulateConfirmedExit)
            start.ArgumentList.Add("--recovery-test-confirmed-exit");
        if (neverReadyOnRestart)
            start.ArgumentList.Add("--recovery-test-never-ready-on-restart");
        if (extraArguments is not null)
            foreach (var argument in extraArguments)
                start.ArgumentList.Add(argument);
        return Process.Start(start)
            ?? throw new InvalidOperationException("The isolated application host did not start.");
    }

    private static Process StartPrepareUninstall(string executable, string profileRoot)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--recovery-test-host");
        start.ArgumentList.Add("--prepare-uninstall");
        start.ArgumentList.Add("--recovery-test-profile");
        start.ArgumentList.Add(profileRoot);
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        return Process.Start(start)
            ?? throw new InvalidOperationException("The isolated prepare-uninstall request did not start.");
    }

    private static string FindDotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "dotnet.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("The installed dotnet host used by the verification runner was not found.");
    }

    private static bool IsSelfContainedRuntimeConfig(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var frameworks = json.RootElement.GetProperty("runtimeOptions")
                .GetProperty("includedFrameworks").EnumerateArray()
                .Select(framework => (Name: framework.GetProperty("name").GetString(),
                    Version: framework.GetProperty("version").GetString()))
                .ToArray();
            return frameworks.Any(framework => framework.Name == "Microsoft.NETCore.App" && framework.Version == "10.0.14")
                && frameworks.Any(framework => framework.Name == "Microsoft.WindowsDesktop.App" && framework.Version == "10.0.14");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            return false;
        }
    }

    private static void CheckProcessReady(Process process, ProcessRecoveryScope scope, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.WaitForExit(0))
                throw new InvalidOperationException($"Owned process PID {process.Id} exited before readiness.");
            if (scope.IsProcessReady(process))
                return;
            Thread.Sleep(25);
        }
        var trace = scope.RuntimeLogPath is { } path && File.Exists(path)
            ? File.ReadAllText(path)
            : "<no isolated runtime log>";
        throw new TimeoutException($"Owned process {process.Id} did not signal its readiness handshake. Runtime trace: {trace}");
    }

    private static bool WaitForNamedEvent(string name, TimeSpan timeout, Process? process = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process?.WaitForExit(0) == true)
                return false;
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                if (handle.WaitOne(25))
                    return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(25);
            }
        }
        return false;
    }

    private static Dictionary<string, string> WaitForLogEvent(string path, string eventName, TimeSpan timeout) =>
        WaitForLogEvent(path, eventName, _ => true, timeout);

    private static Dictionary<string, string> WaitForLogEvent(
        string path,
        string eventName,
        Func<Dictionary<string, string>, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = ReadLogEvents(path, eventName).FirstOrDefault(predicate);
            if (found is not null)
                return found;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"Process-recovery log event '{eventName}' was not observed in {path}.");
    }

    private static IReadOnlyList<Dictionary<string, string>> ReadLogEvents(string path, string eventName)
    {
        if (!File.Exists(path))
            return [];

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var contents = reader.ReadToEnd();
                var lines = contents.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                if (!contents.EndsWith(Environment.NewLine, StringComparison.Ordinal) && lines.Length > 0)
                    Array.Resize(ref lines, lines.Length - 1);
                return lines
                    .Select(ParseLogLine)
                    .Where(fields => fields is not null && fields.TryGetValue("event", out var current) && current == eventName)
                    .Cast<Dictionary<string, string>>()
                    .ToArray();
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(10);
            }
        }

        return [];
    }

    private static Dictionary<string, string>? ParseLogLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["event"] = parts[1] };
        foreach (var part in parts.Skip(2))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
                fields[part[..separator]] = part[(separator + 1)..];
        }
        return fields;
    }

    private static void KillOwnedProcess(Process process, string expectedExecutable)
    {
        if (process.HasExited)
            return;
        if (!SameExecutable(process, expectedExecutable))
            throw new InvalidOperationException($"Refusing to terminate process {process.Id} with a different executable path.");
        process.Kill(entireProcessTree: false);
    }

    private static bool SameExecutable(Process process, string expected) =>
        string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
            Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameProcess(int pid, long startMarker, string executable, out Process process)
    {
        try
        {
            process = Process.GetProcessById(pid);
            if (ProcessRecoveryScope.StartMarker(process) == startMarker && SameExecutable(process, executable))
                return true;
            process.Dispose();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
        }
        process = null!;
        return false;
    }

    private static void AddLoggedOwnedProcesses(List<Process> owned, string logPath, string executable)
    {
        var identities = ReadLogEvents(logPath, "app-startup")
            .Select(fields => (Pid: fields.GetValueOrDefault("pid"), Start: fields.GetValueOrDefault("start")))
            .Concat(ReadLogEvents(logPath, "observer-start")
                .Select(fields => (Pid: fields.GetValueOrDefault("pid"), Start: fields.GetValueOrDefault("self-start"))))
            .Concat(ReadLogEvents(logPath, "restart-start")
                .Select(fields => (Pid: fields.GetValueOrDefault("pid"), Start: fields.GetValueOrDefault("start"))));
        foreach (var identity in identities)
        {
            if (!int.TryParse(identity.Pid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(identity.Start, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                || !IsSameProcess(pid, start, executable, out var process))
                continue;

            if (owned.Any(existing => existing.Id == process.Id))
                process.Dispose();
            else
                owned.Add(process);
        }
    }

    private static void CleanupOwnedProcesses(List<Process> owned, string executable)
    {
        foreach (var process in owned)
        {
            try
            {
                if (!process.WaitForExit(0))
                {
                    if (SameExecutable(process, executable))
                        process.Kill(entireProcessTree: false);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        }
    }

    private static void WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(25);
        if (!condition())
            throw new TimeoutException("Expected background-process recovery state was not reached.");
    }

    private static RecoverySnapshotRead CompleteRead(DateTimeOffset observedAt) => new(true,
        new AccountSnapshot(Venue, Account, "USDC cross", observedAt, "process recovery fixture", "USDC", "BTC",
            AccountReadState.Available, null, observedAt,
            AccountSection<AccountBalance>.Available(
                new AccountBalance(new MoneyAmount(1000m, "USDC"), new MoneyAmount(1000m, "USDC"),
                    new MoneyAmount(900m, "USDC")), observedAt, "process recovery fixture"),
            AccountSection<IReadOnlyList<OpenPosition>>.Available(
                [new OpenPosition("BTC", "BTC-PERP", PositionDirection.Long, 1m, "BTC", null,
                    PositionOwnership.Unknown, "fixture")], observedAt, "process recovery fixture"),
            AccountSection<IReadOnlyList<OpenOrder>>.Available(
                [new OpenOrder("ETH", "ETH-PERP", PositionDirection.Long, 2m, 0.5m, "ETH",
                    "Partially filled", PositionOwnership.Unknown, "opening remainder")], observedAt, "process recovery fixture"),
            AccountSection<IReadOnlyList<PositionProtection>>.Available(
                [new PositionProtection("BTC-PERP", new MoneyAmount(120m, "USDC"),
                    new MoneyAmount(90m, "USDC"))], observedAt, "process recovery fixture"),
            AccountSection<IReadOnlyList<ClosedTrade>>.NotRead("history not needed for reconciliation")),
        OpeningOrderRemaindersComplete: true,
        ProtectionCoverageComplete: true,
        UnfinishedOperationsComplete: true,
        OperationOutcomes: new Dictionary<string, RecoveryOperationObservation>());

    private static bool TryOption(string[] args, string name, out string value)
    {
        var argument = args.FirstOrDefault(item => item.StartsWith(name + "=", StringComparison.Ordinal));
        value = argument is null ? string.Empty : argument[(name.Length + 1)..];
        return argument is not null;
    }

    private static T Get<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing WPF control '{name}'.");

    private sealed class FakeClock(DateTimeOffset now) : IRecoveryClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class CompleteSnapshotSource(FakeClock clock) : IRecoverySnapshotSource
    {
        public TimeSpan MaximumSnapshotAge { get; } = TimeSpan.FromMinutes(5);
        public DateTimeOffset ObservedAt { get; set; } = clock.UtcNow;

        public Task<RecoverySnapshotRead> ReadAsync(string venue, string account, CancellationToken cancellationToken) =>
            Task.FromResult(CompleteRead(ObservedAt));
    }

    private sealed record WorkerLaunch(int Pid, long StartMarker, EventWaitHandle Stop, EventWaitHandle Ready);
}
