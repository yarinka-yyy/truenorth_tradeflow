using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;

namespace TrueNorth.TradeFlow.Recovery;

internal enum ProcessRecoveryKind
{
    BackgroundWorker,
    Application
}

internal enum ProcessRestartDecisionKind
{
    Started,
    Wait,
    Exhausted,
    Unavailable
}

internal sealed record ProcessRestartDecision(
    ProcessRestartDecisionKind Kind,
    Guid? AttemptId = null,
    TimeSpan Delay = default,
    string? Status = null);

internal sealed record ProcessRecoveryStatus(bool Available, string? Message);

internal static class ProcessRecoveryStatusRouting
{
    internal static string? UnknownScopeFailure(
        ProcessRecoveryStatus application,
        ProcessRecoveryStatus backgroundWorker,
        RecoveryJournalState recovery)
    {
        if (!application.Available || !backgroundWorker.Available)
            return ProcessRestartBudgetStore.StorageFailureStatus;

        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(application.Message))
            messages.Add(application.Message);
        if (!string.IsNullOrWhiteSpace(backgroundWorker.Message)
            && !HasExactErrorEvent(backgroundWorker.Message, recovery.Events))
            messages.Add(backgroundWorker.Message);
        return string.Join(Environment.NewLine, messages);
    }

    internal static bool HasExactErrorEvent(string detail, IEnumerable<RecoveryEvent> events) =>
        events.Any(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error
            && recoveryEvent.Venue != "*"
            && !string.IsNullOrWhiteSpace(recoveryEvent.Account)
            && string.Equals(recoveryEvent.InternalDetail, detail, StringComparison.Ordinal));
}

internal sealed class ProcessRecoveryScope
{
    private ProcessRecoveryScope(string profileRoot, bool isolated)
    {
        ProfileRoot = Path.GetFullPath(profileRoot);
        var suffix = isolated
            ? "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProfileRoot)))[..16]
            : string.Empty;
        ApplicationMutexName = @"Local\TrueNorth.TradeFlow" + suffix;
        OpenEventName = @"Local\TrueNorth.TradeFlow.Open" + suffix;
        UninstallEventName = @"Local\TrueNorth.TradeFlow.Uninstall" + suffix;
        ObserverMutexName = @"Local\TrueNorth.TradeFlow.RecoveryObserver" + suffix;
        DisableEventName = @"Local\TrueNorth.TradeFlow.RecoveryDisable" + suffix;
        ObserverReadyEventName = @"Local\TrueNorth.TradeFlow.RecoveryObserverReady" + suffix;
        RuntimeLogMutexName = @"Local\TrueNorth.TradeFlow.RecoveryLog" + suffix;
        TestOpenSignalEventName = @"Local\TrueNorth.TradeFlow.TestOpenSignal" + suffix;
        TestSessionEndingEventName = @"Local\TrueNorth.TradeFlow.TestSessionEnding" + suffix;
        TestConfirmedExitEventName = @"Local\TrueNorth.TradeFlow.TestConfirmedExit" + suffix;
        ProcessRestartBudgetPath = Path.Combine(ProfileRoot, "process-recovery-state.json");
        RuntimeLogPath = isolated ? Path.Combine(ProfileRoot, "process-recovery-runtime.log") : null;
        Isolated = isolated;
    }

    internal string ProfileRoot { get; }
    internal string ApplicationMutexName { get; }
    internal string OpenEventName { get; }
    internal string UninstallEventName { get; }
    internal string ObserverMutexName { get; }
    internal string DisableEventName { get; }
    internal string ObserverReadyEventName { get; }
    internal string RuntimeLogMutexName { get; }
    internal string TestOpenSignalEventName { get; }
    internal string TestSessionEndingEventName { get; }
    internal string TestConfirmedExitEventName { get; }
    internal string ProcessRestartBudgetPath { get; }
    internal string? RuntimeLogPath { get; }
    internal bool Isolated { get; }

    internal static ProcessRecoveryScope Create(string? isolatedProfileRoot) => isolatedProfileRoot is null
        ? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueNorth TradeFlow"), false)
        : new(isolatedProfileRoot, true);

    internal string ReadyEventName(int processId, long startMarker) =>
        $@"Local\TrueNorth.TradeFlow.Ready.{processId}.{startMarker}" + ScopeSuffix;

    internal string StopEventName(string stopId) =>
        $@"Local\TrueNorth.TradeFlow.RecoveryStop.{stopId}" + ScopeSuffix;

    internal EventWaitHandle OpenDisableEvent() =>
        new(false, EventResetMode.ManualReset, DisableEventName);

    internal void DisableRecovery()
    {
        using var disable = OpenDisableEvent();
        disable.Set();
    }

    internal bool IsProcessReady(Process process)
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEventName(process.Id, StartMarker(process)));
            return ready.WaitOne(0);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal bool HasReadinessEvent(Process process)
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEventName(process.Id, StartMarker(process)));
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static long StartMarker(Process process) => process.StartTime.ToUniversalTime().ToFileTimeUtc();

    internal void LogRuntime(string message)
    {
        if (RuntimeLogPath is null)
            return;

        using var mutex = new Mutex(false, RuntimeLogMutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
                throw new TimeoutException("Timed out waiting to write the isolated process-recovery log.");

            Directory.CreateDirectory(ProfileRoot);
            File.AppendAllText(RuntimeLogPath,
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        finally
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
        }
    }

    private string ScopeSuffix => Isolated
        ? "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProfileRoot)))[..16]
        : string.Empty;
}

internal sealed class ProcessRestartBudgetStore
{
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly string _mutexName;
    private readonly IRecoveryClock _clock;

    internal ProcessRestartBudgetStore(string path, IRecoveryClock? clock = null)
    {
        _path = Path.GetFullPath(path);
        _clock = clock ?? new SystemRecoveryClock();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path)))[..16];
        _mutexName = $@"Local\TrueNorth.TradeFlow.ProcessRecoveryBudget.{key}";
    }

    internal ProcessRestartDecision TryBeginAttempt(ProcessRecoveryKind kind)
    {
        return WithState<ProcessRestartDecision>(state =>
        {
            var now = _clock.UtcNow;
            var attempts = GetAttempts(state, kind);
            attempts.RemoveAll(attempt => attempt.StartedAt < now - Window);

            if (attempts.Count >= MaximumAttempts)
            {
                var status = ExhaustedStatus(kind);
                SetStatus(state, kind, status);
                return new(ProcessRestartDecisionKind.Exhausted, Status: status);
            }

            if (attempts.LastOrDefault() is { } previous
                && now < previous.StartedAt + MinimumInterval)
            {
                return new(ProcessRestartDecisionKind.Wait,
                    Delay: previous.StartedAt + MinimumInterval - now);
            }

            var attemptId = Guid.NewGuid();
            attempts.Add(new(attemptId, now));
            SetStatus(state, kind, null);
            return new(ProcessRestartDecisionKind.Started, attemptId);
        });
    }

    internal bool UndoAttempt(ProcessRecoveryKind kind, Guid attemptId) =>
        WithState(state => GetAttempts(state, kind).RemoveAll(attempt => attempt.Id == attemptId) == 1);

    internal ProcessRecoveryStatus ReadStatus(ProcessRecoveryKind kind)
    {
        try
        {
            using var mutex = OpenMutex();
            if (!TakeMutex(mutex))
                return new(false, "Журнал восстановления процессов занят; повторный запуск заблокирован.");
            try
            {
                var result = Load();
                return result.Error is null
                    ? new(true, GetStatus(result.State, kind))
                    : new(false, result.Error);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new(false, StorageFailureStatus);
        }
    }

    internal bool ClearStatus(ProcessRecoveryKind kind) => WithState(state =>
    {
        SetStatus(state, kind, null);
        return true;
    });

    internal bool SetStatus(ProcessRecoveryKind kind, string status) => WithState(state =>
    {
        SetStatus(state, kind, status);
        return true;
    });

    private T WithState<T>(Func<ProcessRestartBudgetState, T> update)
    {
        try
        {
            using var mutex = OpenMutex();
            if (!TakeMutex(mutex))
                return Failure<T>();
            try
            {
                var loaded = Load();
                if (loaded.Error is not null)
                    return Failure<T>();

                var result = update(loaded.State);
                return Save(loaded.State) ? result : Failure<T>();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Failure<T>();
        }
    }

    private static T Failure<T>()
    {
        if (typeof(T) == typeof(ProcessRestartDecision))
            return (T)(object)new ProcessRestartDecision(ProcessRestartDecisionKind.Unavailable,
                Status: StorageFailureStatus);
        return default!;
    }

    private Mutex OpenMutex() => new(false, _mutexName);

    private static bool TakeMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private (ProcessRestartBudgetState State, string? Error) Load()
    {
        if (!File.Exists(_path))
            return (new(), null);

        try
        {
            using var stream = File.OpenRead(_path);
            var state = JsonSerializer.Deserialize<ProcessRestartBudgetState>(stream);
            return state is { FormatVersion: 1, ApplicationAttempts: not null, BackgroundWorkerAttempts: not null }
                && state.ApplicationAttempts.All(IsValidAttempt)
                && state.BackgroundWorkerAttempts.All(IsValidAttempt)
                && (state.ApplicationStatus is null || state.ApplicationStatus.Length <= 2048)
                && (state.BackgroundWorkerStatus is null || state.BackgroundWorkerStatus.Length <= 2048)
                ? (state, null)
                : (new(), StorageFailureStatus);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new(), StorageFailureStatus);
        }
    }

    private bool Save(ProcessRestartBudgetState state)
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or WaitHandleCannotBeOpenedException;

    private static List<ProcessRestartAttempt> GetAttempts(ProcessRestartBudgetState state, ProcessRecoveryKind kind) =>
        kind == ProcessRecoveryKind.Application ? state.ApplicationAttempts : state.BackgroundWorkerAttempts;

    private static string? GetStatus(ProcessRestartBudgetState state, ProcessRecoveryKind kind) =>
        kind == ProcessRecoveryKind.Application ? state.ApplicationStatus : state.BackgroundWorkerStatus;

    private static void SetStatus(ProcessRestartBudgetState state, ProcessRecoveryKind kind, string? status)
    {
        if (kind == ProcessRecoveryKind.Application)
            state.ApplicationStatus = status;
        else
            state.BackgroundWorkerStatus = status;
    }

    internal static string ExhaustedStatus(ProcessRecoveryKind kind) => kind == ProcessRecoveryKind.Application
        ? "Автоматическое восстановление приложения остановлено: исчерпаны 3 попытки за 5 минут. Проверьте причину и запустите приложение вручную."
        : "Автоматическое восстановление фоновой работы остановлено: исчерпаны 3 попытки за 5 минут. Проверьте причину и запустите её вручную.";

    internal const string StorageFailureStatus =
        "Журнал восстановления процессов недоступен. Автоматический перезапуск заблокирован.";

    private sealed class ProcessRestartBudgetState
    {
        public ProcessRestartBudgetState() { }

        public int FormatVersion { get; set; } = 1;
        public List<ProcessRestartAttempt> ApplicationAttempts { get; set; } = [];
        public List<ProcessRestartAttempt> BackgroundWorkerAttempts { get; set; } = [];
        public string? ApplicationStatus { get; set; }
        public string? BackgroundWorkerStatus { get; set; }
    }

    private sealed record ProcessRestartAttempt(Guid Id, DateTimeOffset StartedAt);

    private static bool IsValidAttempt(ProcessRestartAttempt? attempt) =>
        attempt is not null && attempt.Id != Guid.Empty && attempt.StartedAt != default;
}
