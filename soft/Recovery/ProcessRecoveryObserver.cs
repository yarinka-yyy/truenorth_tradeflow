using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace TrueNorth.TradeFlow.Recovery;

internal static class ProcessRecoveryObserver
{
    internal const string ObserverArgument = "--process-recovery-observer";
    internal const string SupervisedArgument = "--process-recovery-supervised";
    internal const string StopArgument = "--process-recovery-stop-id";
    internal const int DuplicateInstanceExitCode = 23;
    internal static readonly TimeSpan ReplacementReadinessTimeout = TimeSpan.FromSeconds(20);

    internal static Process? Start(
        ProcessRecoveryScope scope,
        int targetProcessId,
        long targetStartMarker,
        string stopId,
        IEnumerable<string> applicationArguments,
        out string? error)
    {
        error = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                error = "Не удалось определить исполняемый файл для наблюдателя восстановления.";
                return null;
            }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            start.ArgumentList.Add(ObserverArgument);
            start.ArgumentList.Add($"--process-recovery-target={targetProcessId}");
            start.ArgumentList.Add($"--process-recovery-start={targetStartMarker}");
            start.ArgumentList.Add($"{StopArgument}={stopId}");
            foreach (var argument in applicationArguments)
                start.ArgumentList.Add(argument);

            return Process.Start(start);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = "Не удалось запустить наблюдатель восстановления приложения.";
            return null;
        }
    }

    internal static int Run(string[] args, ProcessRecoveryScope scope)
    {
        var budget = new ProcessRestartBudgetStore(scope.ProcessRestartBudgetPath);
        try
        {
            if (!TryOption(args, "--process-recovery-target", out var pidText)
                || !int.TryParse(pidText, out var targetPid)
                || !TryOption(args, "--process-recovery-start", out var startText)
                || !long.TryParse(startText, out var targetStart)
                || !TryOption(args, StopArgument, out var stopId)
                || !Guid.TryParseExact(stopId, "N", out _))
            {
                budget.SetStatus(ProcessRecoveryKind.Application,
                    "Наблюдатель восстановления получил неверную идентичность процесса.");
                return 2;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return 3;

            using var observerMutex = new Mutex(initiallyOwned: true, scope.ObserverMutexName, out var createdNew);
            if (!createdNew)
                return 0;

            using var disable = scope.OpenDisableEvent();
            using var stop = EventWaitHandle.OpenExisting(scope.StopEventName(stopId));
            using var observerReady = new EventWaitHandle(false, EventResetMode.ManualReset, scope.ObserverReadyEventName);

            using var target = OpenExactProcess(targetPid, targetStart, executable, Environment.ProcessId);
            if (target is null)
            {
                budget.SetStatus(ProcessRecoveryKind.Application,
                    "Наблюдатель восстановления не смог проверить исходный экземпляр приложения.");
                return 4;
            }

            var launchArguments = MakeApplicationArguments(args, scope);
            var sessionId = target.SessionId;
            using var observerProcess = Process.GetCurrentProcess();
            scope.LogRuntime($"observer-start pid={observerProcess.Id} self-start={ProcessRecoveryScope.StartMarker(observerProcess)} "
                + $"target-pid={target.Id} target-start={targetStart}");
            observerReady.Set();

            var initial = WaitForReady(target, scope, stop, disable);
            if (initial == ReadyWaitResult.Stopped)
                return 0;
            if (initial == ReadyWaitResult.Exited)
                scope.LogRuntime($"initial-before-ready pid={target.Id} exit={ReadExitCode(target)}");

            var current = target;
            var ownedTarget = true;
            var marker = targetStart;
            while (true)
            {
                if (initial == ReadyWaitResult.Ready)
                    WaitForExitOrStop(current, stop, disable);
                if (stop.WaitOne(0) || disable.WaitOne(0))
                {
                    scope.LogRuntime($"planned-stop pid={current.Id}");
                    return 0;
                }

                var exitCode = ReadExitCode(current);
                scope.LogRuntime($"unexpected-exit pid={current.Id} start={marker} exit={exitCode}");
                if (ownedTarget)
                    current.Dispose();
                ownedTarget = false;

                var replacement = Recover(scope, budget, executable, launchArguments, sessionId, stopId,
                    marker, stop, disable, out var nextMarker, out var wasStopped);
                if (wasStopped || replacement is null)
                    return 0;

                current = replacement;
                marker = nextMarker;
                ownedTarget = true;
                initial = ReadyWaitResult.Ready;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            budget.SetStatus(ProcessRecoveryKind.Application,
                "Наблюдатель восстановления не смог продолжить безопасный контроль процесса.");
            scope.LogRuntime($"observer-error type={exception.GetType().Name}");
            return 5;
        }
    }

    private static Process? Recover(
        ProcessRecoveryScope scope,
        ProcessRestartBudgetStore budget,
        string executable,
        IReadOnlyList<string> applicationArguments,
        int sessionId,
        string stopId,
        long previousStart,
        EventWaitHandle stop,
        EventWaitHandle disable,
        out long currentStart,
        out bool wasStopped)
    {
        currentStart = previousStart;
        wasStopped = false;
        while (true)
        {
            var existing = WaitForExistingPrimary(scope, executable, sessionId, currentStart,
                stop, disable, out var existingStopped);
            if (existing is not null)
            {
                currentStart = ProcessRecoveryScope.StartMarker(existing);
                scope.LogRuntime($"attached-existing pid={existing.Id} start={currentStart}");
                return existing;
            }
            if (existingStopped || stop.WaitOne(0) || disable.WaitOne(0))
            {
                wasStopped = true;
                return null;
            }

            var decision = budget.TryBeginAttempt(ProcessRecoveryKind.Application);
            if (decision.Kind == ProcessRestartDecisionKind.Wait)
            {
                scope.LogRuntime($"restart-wait seconds={decision.Delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
                if (WaitForControl(stop, disable, decision.Delay))
                {
                    wasStopped = true;
                    return null;
                }
                continue;
            }
            if (decision.Kind == ProcessRestartDecisionKind.Exhausted)
            {
                var status = decision.Status ?? ProcessRestartBudgetStore.ExhaustedStatus(ProcessRecoveryKind.Application);
                budget.SetStatus(ProcessRecoveryKind.Application, status);
                scope.LogRuntime("restart-exhausted attempts=3 window-seconds=300");
                return null;
            }
            if (decision.Kind == ProcessRestartDecisionKind.Unavailable)
            {
                budget.SetStatus(ProcessRecoveryKind.Application, decision.Status ?? ProcessRestartBudgetStore.StorageFailureStatus);
                scope.LogRuntime("restart-blocked budget-unavailable");
                return null;
            }

            using var child = StartSupervised(executable, applicationArguments, stopId);
            if (child is null)
            {
                scope.LogRuntime("restart-launch-failed");
                continue;
            }

            var childStart = ProcessRecoveryScope.StartMarker(child);
            scope.LogRuntime($"restart-start pid={child.Id} start={childStart} attempt={decision.AttemptId}");
            var childResult = WaitForReady(child, scope, stop, disable, ReplacementReadinessTimeout);
            if (childResult == ReadyWaitResult.Ready)
            {
                currentStart = childStart;
                budget.ClearStatus(ProcessRecoveryKind.Application);
                scope.LogRuntime($"ready pid={child.Id} start={childStart}");
                return Process.GetProcessById(child.Id);
            }

            if (childResult == ReadyWaitResult.Stopped)
            {
                StopOwnedChild(child);
                scope.LogRuntime($"restart-stopped pid={child.Id} start={childStart} exit={ReadExitCode(child)}");
                wasStopped = true;
                return null;
            }

            if (childResult == ReadyWaitResult.TimedOut)
            {
                StopOwnedChild(child);
                scope.LogRuntime($"ready-timeout pid={child.Id} start={childStart} exit={ReadExitCode(child)}");
                currentStart = childStart;
                continue;
            }

            var childExitCode = ReadExitCode(child);
            scope.LogRuntime($"before-ready-exit pid={child.Id} start={childStart} exit={childExitCode}");
            if (childExitCode == ProcessRecoveryObserver.DuplicateInstanceExitCode && decision.AttemptId is { } attemptId)
                budget.UndoAttempt(ProcessRecoveryKind.Application, attemptId);
            else
                currentStart = childStart;
        }
    }

    private static Process? StartSupervised(
        string executable,
        IReadOnlyList<string> applicationArguments,
        string stopId)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            foreach (var argument in applicationArguments)
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add(SupervisedArgument);
            start.ArgumentList.Add($"{StopArgument}={stopId}");
            return Process.Start(start);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> MakeApplicationArguments(string[] args, ProcessRecoveryScope scope)
    {
        var result = new List<string>();
        if (args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            result.Add("--startup");
        if (args.Contains("--recovery-test-host", StringComparer.OrdinalIgnoreCase))
            result.Add("--recovery-test-host");
        if (args.Contains("--recovery-test-never-ready-on-restart", StringComparer.OrdinalIgnoreCase))
            result.Add("--recovery-test-never-ready");
        if (scope.Isolated)
            result.Add("--recovery-test-profile=" + scope.ProfileRoot);
        return result;
    }

    private static Process? FindRunningPrimary(
        ProcessRecoveryScope scope,
        string executable,
        int sessionId,
        long afterStart)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.Id != Environment.ProcessId
                    && process.SessionId == sessionId
                    && ProcessRecoveryScope.StartMarker(process) > afterStart
                    && scope.HasReadinessEvent(process)
                    && SameExecutable(process, executable))
                    return process;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException)
            {
            }

            process.Dispose();
        }
        return null;
    }

    private static Process? OpenExactProcess(int processId, long startMarker, string executable, int observerPid)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (process.Id != observerPid
                && ProcessRecoveryScope.StartMarker(process) == startMarker
                && process.SessionId == Process.GetCurrentProcess().SessionId
                && SameExecutable(process, executable))
                return process;
            process.Dispose();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool SameExecutable(Process process, string expected)
    {
        try
        {
            return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static ReadyWaitResult WaitForReady(
        Process process,
        ProcessRecoveryScope scope,
        EventWaitHandle stop,
        EventWaitHandle disable,
        TimeSpan? timeout = null)
    {
        using var exited = ProcessWaitHandle(process);
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            if (stop.WaitOne(0) || disable.WaitOne(0))
                return ReadyWaitResult.Stopped;
            if (exited.WaitOne(0))
                return ReadyWaitResult.Exited;

            EventWaitHandle? ready = null;
            try
            {
                try
                {
                    ready = EventWaitHandle.OpenExisting(scope.ReadyEventName(process.Id, ProcessRecoveryScope.StartMarker(process)));
                    if (ready.WaitOne(0))
                        return ReadyWaitResult.Ready;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }
                catch (InvalidOperationException)
                {
                    return ReadyWaitResult.Exited;
                }

                var waitDuration = TimeSpan.FromMilliseconds(250);
                if (timeout is { } limit)
                {
                    var remaining = limit - Stopwatch.GetElapsedTime(startedAt);
                    if (remaining <= TimeSpan.Zero)
                        return ReadyWaitResult.TimedOut;
                    if (remaining < waitDuration)
                        waitDuration = remaining;
                }

                var handles = ready is null
                    ? new WaitHandle[] { stop, disable, exited }
                    : [stop, disable, exited, ready];
                var result = WaitHandle.WaitAny(handles, waitDuration);
                if (result == 0 || result == 1)
                    return ReadyWaitResult.Stopped;
                if (result == 2)
                    return ReadyWaitResult.Exited;
                if (result == 3)
                    return ReadyWaitResult.Ready;
            }
            finally
            {
                ready?.Dispose();
            }
        }
    }

    private static void WaitForExitOrStop(Process process, EventWaitHandle stop, EventWaitHandle disable)
    {
        using var exited = ProcessWaitHandle(process);
        WaitHandle.WaitAny([stop, disable, exited]);
    }

    private static EventWaitHandle ProcessWaitHandle(Process process)
    {
        var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        waitHandle.SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false);
        return waitHandle;
    }

    private static bool WaitForControl(EventWaitHandle stop, EventWaitHandle disable, TimeSpan delay) =>
        WaitHandle.WaitAny([stop, disable], delay) != WaitHandle.WaitTimeout;

    private static bool PrimaryMutexIsOwned(ProcessRecoveryScope scope)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(scope.ApplicationMutexName);
            try
            {
                if (!mutex.WaitOne(0))
                    return true;
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                return false;
            }
            mutex.ReleaseMutex();
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private static Process? WaitForExistingPrimary(
        ProcessRecoveryScope scope,
        string executable,
        int sessionId,
        long afterStart,
        EventWaitHandle stop,
        EventWaitHandle disable,
        out bool wasStopped)
    {
        wasStopped = false;
        while (true)
        {
            if (stop.WaitOne(0) || disable.WaitOne(0))
            {
                wasStopped = true;
                return null;
            }

            var candidate = FindRunningPrimary(scope, executable, sessionId, afterStart);
            if (candidate is not null)
            {
                afterStart = ProcessRecoveryScope.StartMarker(candidate);
                var ready = WaitForReady(candidate, scope, stop, disable);
                if (ready == ReadyWaitResult.Ready)
                    return candidate;
                candidate.Dispose();
                if (ready == ReadyWaitResult.Stopped)
                {
                    wasStopped = true;
                    return null;
                }
                continue;
            }

            if (!PrimaryMutexIsOwned(scope))
                return null;
            if (WaitHandle.WaitAny([stop, disable], TimeSpan.FromMilliseconds(250)) != WaitHandle.WaitTimeout)
            {
                wasStopped = true;
                return null;
            }
        }
    }

    private static int ReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return int.MinValue;
        }
    }

    private static void StopOwnedChild(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool TryOption(string[] args, string name, out string value)
    {
        var argument = args.FirstOrDefault(item => item.StartsWith(name + "=", StringComparison.Ordinal));
        value = argument is null ? string.Empty : argument[(name.Length + 1)..];
        return argument is not null;
    }

    private enum ReadyWaitResult
    {
        Ready,
        Exited,
        Stopped,
        TimedOut
    }
}
