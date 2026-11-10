using System.Diagnostics;

namespace TrueNorth.TradeFlow.Recovery;

internal sealed class BackgroundProcessRecoverySupervisor : IAsyncDisposable
{
    private readonly RecoveryCoordinator _recovery;
    private readonly ProcessRestartBudgetStore _budget;
    private readonly Func<CancellationToken, Task<Process>> _startWorker;
    private readonly Action<Process> _stopWorker;
    private readonly Func<CancellationToken, Task<bool>> _reconcileBeforeResume;
    private readonly Func<bool> _savedOperatingIntent;
    private readonly string _venue;
    private readonly string _account;
    private readonly AppSettings _settings;
    private readonly Action<string?> _statusChanged;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _processGate = new();
    private Process? _currentProcess;
    private Task? _run;

    internal BackgroundProcessRecoverySupervisor(
        RecoveryCoordinator recovery,
        ProcessRestartBudgetStore budget,
        Func<CancellationToken, Task<Process>> startWorker,
        Action<Process> stopWorker,
        Func<CancellationToken, Task<bool>> reconcileBeforeResume,
        Func<bool> savedOperatingIntent,
        string venue,
        string account,
        AppSettings settings,
        Action<string?> statusChanged)
    {
        _recovery = recovery;
        _budget = budget;
        _startWorker = startWorker;
        _stopWorker = stopWorker;
        _reconcileBeforeResume = reconcileBeforeResume;
        _savedOperatingIntent = savedOperatingIntent;
        _venue = venue;
        _account = account;
        _settings = settings.Copy();
        _statusChanged = statusChanged;
    }

    internal int? CurrentProcessId
    {
        get
        {
            lock (_processGate)
                return _currentProcess is { HasExited: false } process ? process.Id : null;
        }
    }

    internal async Task StartAsync()
    {
        if (_run is not null)
            throw new InvalidOperationException("Background process recovery is already running.");

        _run = RunAsync();
        await _started.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Process? current;
        lock (_processGate)
            current = _currentProcess;
        if (current is { HasExited: false })
            StopOwnedWorker(current);
        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
        current?.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            var process = await _startWorker(_stop.Token).ConfigureAwait(false);
            SetCurrentProcess(process);
            _started.TrySetResult();
            _statusChanged(null);

            while (true)
            {
                await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
                if (_stop.IsCancellationRequested)
                    return;

                var episode = await _recovery.ReportFailureAsync(_venue, _account,
                    "Процесс фоновой работы неожиданно завершился.", _settings, _stop.Token).ConfigureAwait(false);
                if (episode is null)
                {
                    _statusChanged("Фоновая работа остановлена: журнал восстановления недоступен.");
                    return;
                }

                _statusChanged("Фоновая работа остановлена. Перед возобновлением выполняется полная сверка.");
                if (!await _reconcileBeforeResume(_stop.Token).ConfigureAwait(false))
                {
                    _statusChanged(_recovery.StatusText);
                    return;
                }

                var savedOperatingIntent = _savedOperatingIntent();
                if (!_recovery.IsEntryAllowed(_venue, savedOperatingIntent))
                {
                    _statusChanged(savedOperatingIntent
                        ? _recovery.StatusText
                        : "Сверка завершена. Фоновая работа оставлена остановленной по сохранённому Stop.");
                    return;
                }

                while (true)
                {
                    var decision = _budget.TryBeginAttempt(ProcessRecoveryKind.BackgroundWorker);
                    if (decision.Kind == ProcessRestartDecisionKind.Wait)
                    {
                        _statusChanged("Сверка завершена. Ожидается допустимый интервал перезапуска фоновой работы.");
                        await Task.Delay(decision.Delay, _stop.Token).ConfigureAwait(false);
                        if (!_recovery.IsEntryAllowed(_venue, _savedOperatingIntent()))
                        {
                            _statusChanged("Фоновая работа оставлена остановленной по сохранённому Stop.");
                            return;
                        }
                        continue;
                    }

                    if (decision.Kind is ProcessRestartDecisionKind.Exhausted or ProcessRestartDecisionKind.Unavailable)
                    {
                        var status = decision.Status ?? ProcessRestartBudgetStore.StorageFailureStatus;
                        _statusChanged(status);
                        var errorSettings = _settings.Copy();
                        errorSettings.ReadRetryCount = 0;
                        await _recovery.ReportFailureAsync(_venue, _account, status, errorSettings, _stop.Token)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (decision.Kind != ProcessRestartDecisionKind.Started)
                        return;

                    try
                    {
                        process = await _startWorker(_stop.Token).ConfigureAwait(false);
                        SetCurrentProcess(process);
                        _budget.ClearStatus(ProcessRecoveryKind.BackgroundWorker);
                        _statusChanged("Фоновая работа восстановлена после полной сверки.");
                        break;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        _statusChanged("Не удалось запустить фоновую работу; ограниченная попытка записана.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _statusChanged("Фоновая работа остановлена из-за ошибки процесса восстановления.");
        }
        finally
        {
            _started.TrySetResult();
        }
    }

    private void SetCurrentProcess(Process process)
    {
        lock (_processGate)
        {
            _currentProcess?.Dispose();
            _currentProcess = process;
        }
    }

    private void StopOwnedWorker(Process process)
    {
        try
        {
            _stopWorker(process);
            if (!process.WaitForExit(5000) && !process.HasExited)
                process.Kill(entireProcessTree: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
