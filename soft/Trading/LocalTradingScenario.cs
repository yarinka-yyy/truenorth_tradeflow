using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using TrueNorth.TradeFlow;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Orders;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Trading;

internal sealed record LocalTradingScenarioStatus(
    string SelectedMode,
    bool SavedOperatingIntent,
    bool IsSearching,
    bool IsAdmitting,
    bool HasOwnedMonitoring,
    int OwnedPositionCount,
    int OwnedOpenEntryCount,
    string Activity,
    string PauseReason,
    string LastStatus,
    string ProfileReview);

internal sealed record LocalScenarioOpenEntry(
    string IntentId,
    string OrderId,
    string Asset,
    string Contract,
    decimal RemainingQuantity);

internal sealed record LocalScenarioExitSnapshot(
    bool IsKnown,
    IReadOnlyList<LocalScenarioOpenEntry> OpenEntries,
    string Status,
    IReadOnlyList<string> CancellationResults);

internal sealed record LocalScenarioExitResult(
    IReadOnlyList<string> Results,
    LocalScenarioExitSnapshot Snapshot);

internal sealed class LocalTradingScenario : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly TimeSpan DueDeadlineRetryDelay = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settingsStore;
    private readonly RecoveryJournalStore _journalStore;
    private readonly IRecoveryClock _clock;
    private readonly RecoveryCoordinator _recovery;
    private readonly OrderLifecycleCoordinator _orders;
    private readonly TradingLoopCoordinator _loop;
    private readonly LocalScenarioAdapter _adapter;
    private readonly NotificationDispatcher _notifications;
    private readonly LocalScenarioNotificationChannel _notificationsChannel;
    private readonly LocalScenarioAiSource _aiSource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _automaticScheduling;
    private CancellationTokenSource _shutdown = new();
    private readonly object _wakeGate = new();
    private TaskCompletionSource _wake = NewWakeSource();
    private Task? _worker;
    private int _runImmediately;
    private string? _initializationFailure;
    private string? _runtimeFailure;
    private int _searching;
    private int _scheduledRunCount;
    private DateTimeOffset? _dueDeadlineRetryAt;
    private DateTimeOffset? _fallbackRecoveryDeadlineAt;
    private bool _disposed;
    private IReadOnlyList<string> _exitCancellationResults = [];

    internal LocalTradingScenario(
        string rootPath,
        SettingsProfile seedProfile,
        IRecoveryClock? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool automaticScheduling = true,
        Action<string, string, string?>? profileCommitOverride = null)
    {
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
        _clock = clock ?? new SystemRecoveryClock();
        _delay = delay ?? Task.Delay;
        _automaticScheduling = automaticScheduling;
        _settingsStore = new SettingsStore(Path.Combine(RootPath, "profile", "settings.json"),
            commitOverride: profileCommitOverride);
        var loaded = _settingsStore.Load();
        if (loaded.Status == SettingsFileStatus.Missing)
        {
            var firstProfile = seedProfile.Copy();
            firstProfile.SavedOperatingIntent = false;
            var seeded = _settingsStore.Save(firstProfile);
            if (seeded.Succeeded && seeded.Profile is not null)
                loaded = new SettingsLoadResult(seeded.Profile, SettingsFileStatus.Loaded);
            else
                _initializationFailure = seeded.Message ?? "Не удалось создать профиль локального сценария.";
        }
        else if (loaded.Status is not SettingsFileStatus.Loaded)
        {
            _initializationFailure = loaded.Message ?? "Профиль локального сценария недоступен.";
        }

        _journalStore = new RecoveryJournalStore(Path.Combine(RootPath, "journal", "recovery-state.json"));
        _adapter = new LocalScenarioAdapter(Path.Combine(RootPath, "synthetic-market.json"), _clock);
        _initializationFailure ??= _adapter.InitializationFailure;
        var profile = loaded.Profile;
        _recovery = new RecoveryCoordinator(_journalStore, profile.Settings, _clock, _adapter);
        var localChannel = new LocalScenarioNotificationChannel();
        _notificationsChannel = localChannel;
        _notifications = new NotificationDispatcher(_recovery, [localChannel], profile.Settings);
        _notifications.Start(TimeSpan.FromMinutes(1));
        _orders = new OrderLifecycleCoordinator(_recovery, _adapter, profile.Settings, _clock, _notifications);
        var contexts = new LocalScenarioContextSource(_recovery,
            (asset, venue, account, contract) => new TradingLoopRoute(asset, venue, account, contract));
        _aiSource = new LocalScenarioAiSource(_clock);
        _loop = new TradingLoopCoordinator(_settingsStore, profile, _recovery, _orders,
            _aiSource, contexts, _clock);
        _recovery.StatusChanged += OnRecoveryStatusChanged;
        if (_automaticScheduling)
            _worker = RunSchedulerAsync(_shutdown.Token);
    }

    internal static string DefaultDirectory(string profileRoot) =>
        Path.Combine(profileRoot, "local-trading-scenario");

    internal static LocalTradingScenario? TryCreate(
        string rootPath,
        SettingsProfile seedProfile,
        out string? failure,
        IRecoveryClock? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool automaticScheduling = true,
        Action<string, string, string?>? profileCommitOverride = null)
    {
        try
        {
            failure = null;
            return new LocalTradingScenario(rootPath, seedProfile, clock, delay, automaticScheduling,
                profileCommitOverride);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            failure = $"Локальный сценарий недоступен: не удалось подготовить отдельное хранилище ({exception.GetType().Name}). Окно приложения остаётся доступным.";
            return null;
        }
    }

    internal string RootPath { get; }
    internal string SettingsPath => _settingsStore.PathName;
    internal string JournalPath => _journalStore.PathName;
    internal bool HasExternalConnections => false;
    internal bool IsReady => _initializationFailure is null;
    internal TradingLoopCoordinator Loop => _loop;
    internal RecoveryCoordinator Recovery => _recovery;
    internal IReadOnlyList<ManagedOrderState> ManagedOrders => _orders.Orders;
    internal IReadOnlyList<OpenPosition> SyntheticPositions => _adapter.Positions;
    internal IReadOnlyList<OpenOrder> SyntheticOpenOrders => _adapter.OpenOrders;
    internal int SyntheticAiRequestCount => _aiSource.RequestCount;
    internal int ScheduledRunCount => Volatile.Read(ref _scheduledRunCount);
    internal bool HasSavedStart => _loop.Profile.SavedOperatingIntent;
    internal bool HasOwnedMonitoring => _orders.Orders.Any(IsMonitoredOrder);
    internal int OwnedPositionCount => _orders.Orders.Count(order => IsOwnedPosition(order));
    internal int OwnedOpenEntryCount => _orders.Orders.Count(order => IsOwnedOpenEntry(order));
    internal string Status => _runtimeFailure ?? _initializationFailure ?? _loop.Status;
    internal IReadOnlyList<NotificationMessage> LocalNotifications =>
        _notificationsChannel.Messages;

    internal event Action? StateChanged;

    internal void ReturnNoSetupForNextSearch() => _aiSource.ReturnNoSetupNext();
    internal void SetNextCancelOutcome(RecoveryOperationOutcome outcome) => _adapter.SetNextCancelOutcome(outcome);
    internal void FillDuringNextCancel(decimal quantity) => _adapter.FillDuringNextCancel(quantity);
    internal void FailNextSyntheticRead(Exception exception) => _adapter.FailNextRead(exception);

    internal LocalTradingScenarioStatus Snapshot()
    {
        var profile = _loop.Profile;
        var monitored = HasOwnedMonitoring;
        var searching = Volatile.Read(ref _searching) != 0;
        var activity = searching
            ? "Идёт локальный поиск и сверка."
            : monitored
                ? $"Сопровождение локальных позиций и заявок: {OwnedPositionCount} поз., {OwnedOpenEntryCount} ост."
                : profile.SavedOperatingIntent
                    ? "Ожидание следующего срока локального поиска."
                    : "Локальный сценарий остановлен.";
        var pause = _initializationFailure ?? _runtimeFailure
            ?? (_loop.IsExitHeld ? "Входы приостановлены на время подтверждения выхода."
                : _recovery.HasPendingRecovery ? _recovery.StatusText
                : profile.SelectedMode != "TrueNorth AI setups" ? "Выбран режим Test; новые локальные входы остановлены."
                : !profile.SavedOperatingIntent ? HasOwnedMonitoring
                    ? "Сохранено намерение Stop; сопровождение открытых позиций продолжается."
                    : "Сохранено намерение Stop; нет открытых позиций для сопровождения."
                : profile.IsIncomplete() ? "Локальный Start заблокирован: сохранённые настройки неполны."
                : !_loop.IsAdmitting ? _loop.Status
                : "Паузы нет; локальный цикл использует синтетические данные.");
        return new(profile.SelectedMode, profile.SavedOperatingIntent, searching, _loop.IsAdmitting,
            monitored, OwnedPositionCount, OwnedOpenEntryCount, activity, pause, Status,
            FormatProfileReview(profile));
    }

    internal async Task<TradingLoopOutcome> ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_initializationFailure is not null)
            return Publish(new(false, _initializationFailure));
        return await RunOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TradingLoopOutcome> StartAsync(
        SettingsProfile savedProfile,
        CancellationToken cancellationToken = default)
    {
        if (_initializationFailure is not null)
            return Publish(new(false, _initializationFailure));
        var localProfile = savedProfile.Copy();
        localProfile.SelectedMode = "TrueNorth AI setups";
        localProfile.SavedOperatingIntent = _loop.Profile.SavedOperatingIntent;
        if (!await _loop.UpdateProfileAsync(localProfile, cancellationToken).ConfigureAwait(false))
            return Publish(new(false, _loop.Status));
        var result = await _loop.StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
            SignalWorker(runImmediately: true);
        return Publish(result);
    }

    internal async Task<TradingLoopOutcome> StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await _loop.StopAsync(cancellationToken).ConfigureAwait(false);
        SignalWorker();
        return Publish(result);
    }

    internal async Task<TradingLoopOutcome> UpdateProfileAsync(
        SettingsProfile profile,
        CancellationToken cancellationToken = default)
    {
        var result = await _loop.UpdateProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!result)
            return Publish(new(false, _loop.Status));
        if (profile.SelectedMode != "TrueNorth AI setups")
            result = (await _loop.StopAsync(cancellationToken).ConfigureAwait(false)).Succeeded;
        SignalWorker();
        return Publish(new(result, _loop.Status));
    }

    internal async Task<TradingLoopOutcome> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return new(false, "Локальный сценарий уже завершён.");
        if (_initializationFailure is not null)
            return Publish(new(false, _initializationFailure));
        Interlocked.Exchange(ref _searching, _loop.IsRunning ? 1 : 0);
        Publish(new(true, _loop.Status));
        try
        {
            var outcome = await _loop.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            _runtimeFailure = outcome.Succeeded ? null : outcome.Status;
            return Publish(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(new(false, "Локальный цикл остановлен; следующий запуск сверит состояние."));
        }
        catch (Exception)
        {
            _runtimeFailure = "Локальная сверка не завершена; новые входы заблокированы.";
            return Publish(new(false, _runtimeFailure));
        }
        finally
        {
            Interlocked.Exchange(ref _searching, 0);
            Publish(new(true, _loop.Status));
            SignalWorker();
        }
    }

    internal Task<bool> CloseAtTakeProfitAsync(string intentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _orders.Orders.SingleOrDefault(order => order.IntentId == intentId);
        if (state is null || !_adapter.CloseAtTakeProfit(state))
            return Task.FromResult(false);
        SignalWorker();
        Publish(new(true, "Синтетическое исполнение TP записано; следующая сверка подтвердит закрытие."));
        return Task.FromResult(true);
    }

    internal Task<bool> CloseNextPositionAtTakeProfitAsync(CancellationToken cancellationToken = default)
    {
        var order = _orders.Orders.FirstOrDefault(IsOwnedPosition);
        return order is null
            ? Task.FromResult(false)
            : CloseAtTakeProfitAsync(order.IntentId, cancellationToken);
    }

    internal async Task<LocalScenarioExitSnapshot> HoldAndReconcileForExitAsync(
        CancellationToken cancellationToken = default)
    {
        await _loop.HoldAdmissionForExitAsync().ConfigureAwait(false);
        var reconciliation = await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadExitSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var status = reconciliation.Succeeded ? snapshot.Status
            : reconciliation.Status + " " + snapshot.Status;
        return snapshot with { Status = status, CancellationResults = _exitCancellationResults };
    }

    internal async Task<TradingLoopOutcome> PrepareForPlannedUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed || _initializationFailure is not null)
            return new(false, _initializationFailure ?? "Локальный сценарий уже завершён.");

        try
        {
            await _loop.HoldAdmissionForExitAsync(cancellationToken).ConfigureAwait(false);
            var profile = _settingsStore.Load();
            var journal = _journalStore.Load();
            if (!_recovery.StorageReady || profile.Status != SettingsFileStatus.Loaded
                || profile.Profile.SavedOperatingIntent != _loop.Profile.SavedOperatingIntent
                || journal.Status is not (RecoveryJournalStatus.Loaded or RecoveryJournalStatus.Missing))
                return new(false, "Состояние профиля или журнал восстановления нельзя подтвердить; обновление остановлено.");

            _shutdown.Cancel();
            SignalWorker();
            if (_worker is not null)
                await _worker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new(true, "Новые входы приостановлены; журнал и намерение Start/Stop сохранены.");
        }
        catch (TimeoutException)
        {
            return new(false, "Локальный цикл не завершился за отведённое время; обновление остановлено.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, "Подготовка обновления отменена; обновление остановлено.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
        {
            return new(false, "Не удалось подтвердить сохранение состояния; обновление остановлено.");
        }
    }

    internal async Task<TradingLoopOutcome> ResumeAfterPlannedUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return new(false, "Локальный сценарий уже завершён.");

        var result = await _loop.ResumeAfterExitAsync(cancellationToken).ConfigureAwait(false);
        if (_shutdown.IsCancellationRequested && _worker is { IsCompleted: false })
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new(false, "Локальный планировщик не завершился после отмены обновления.");
            }
        }

        if (_automaticScheduling && _shutdown.IsCancellationRequested)
        {
            _shutdown.Dispose();
            _shutdown = new CancellationTokenSource();
            _worker = RunSchedulerAsync(_shutdown.Token);
            SignalWorker(runImmediately: result.Succeeded);
        }
        return result;
    }

    internal async Task<LocalScenarioExitResult> CancelOpenEntriesForExitAsync(
        LocalScenarioExitSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.IsKnown)
            return new([], snapshot with
            {
                Status = "Отмена недоступна: число или точное владение заявками не подтверждено."
            });

        var results = new List<string>();
        foreach (var entry in snapshot.OpenEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = _orders.Orders.SingleOrDefault(order => order.IntentId == entry.IntentId
                && order.EntryOrderId == entry.OrderId && order.ManagementMode == ManagedOrderManagementMode.Application);
            if (state is null)
            {
                results.Add($"{entry.Asset} · {entry.OrderId}: отмена не отправлена, точное намерение изменилось.");
                continue;
            }

            try
            {
                var result = await _orders.CancelRemainderAsync(entry.IntentId, savedOperatingIntent: false,
                    cancellationToken).ConfigureAwait(false);
                results.Add($"{entry.Asset} · {entry.OrderId}: {result.Status}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add($"{entry.Asset} · {entry.OrderId}: исход неизвестен; требуется сверка.");
                break;
            }
            catch (Exception)
            {
                results.Add($"{entry.Asset} · {entry.OrderId}: ошибка с неизвестным исходом; требуется сверка.");
            }
        }

        _exitCancellationResults = results.ToArray();
        var after = await ReadExitSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new(results, after with { CancellationResults = _exitCancellationResults });
    }

    internal async Task<LocalScenarioExitSnapshot> ReadExitSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var orders = _orders.Orders;
        var routes = LocalScenarioContextSource.BuildRoutes(_loop.Profile);
        var reads = new Dictionary<(string Venue, string Account), RecoverySnapshotRead>();
        var known = true;
        foreach (var scope in routes.Select(route => (route.Venue, route.Account)).Distinct())
        {
            var read = await _recovery.ReadCurrentSnapshotAsync(scope.Venue, scope.Account, cancellationToken)
                .ConfigureAwait(false);
            reads[scope] = read;
            if (!read.SessionAvailable || read.Account is null || !read.OpeningOrderRemaindersComplete
                || !read.UnfinishedOperationsComplete || !read.ProtectionCoverageComplete
                || read.Account.Positions.State is not (AccountReadState.Available or AccountReadState.ConfirmedEmpty)
                || read.Account.Positions.Value is null
                || read.Account.Orders.State is not (AccountReadState.Available or AccountReadState.ConfirmedEmpty)
                || read.Account.Orders.Value is null)
                known = false;
        }

        var entries = new List<LocalScenarioOpenEntry>();
        foreach (var state in orders.Where(order => order.ManagementMode == ManagedOrderManagementMode.Application))
        {
            var isOpen = state.EntryTerminalState == ManagedEntryTerminalState.Open;
            if (!isOpen)
            {
                if (state.PendingAction is ManagedOrderAction.Entry or ManagedOrderAction.CancelRemainder
                    || state.EntryTerminalState == ManagedEntryTerminalState.Unknown)
                    known = false;
                continue;
            }

            if (state.EntryOrderId is null || state.RemainingQuantity <= 0m
                || state.PendingOperationId is not null
                || !reads.TryGetValue((state.Venue, state.Account), out var read)
                || read.Account?.Orders.Value is not { } openOrders)
            {
                known = false;
                continue;
            }
            var matches = openOrders.Where(order => order.OrderId == state.EntryOrderId).ToArray();
            if (matches.Length != 1 || matches[0].Ownership != PositionOwnership.SoftwareOwned
                || matches[0].Contract != state.Contract || matches[0].Asset != state.Asset
                || matches[0].Direction != state.Direction || matches[0].RemainingQuantity != state.RemainingQuantity)
            {
                known = false;
                continue;
            }
            entries.Add(new(state.IntentId, state.EntryOrderId, state.Asset, state.Contract,
                state.RemainingQuantity));
        }

        foreach (var read in reads.Values)
        {
            if (read.Account?.Orders.Value is not { } openOrders)
                continue;
            foreach (var openOrder in openOrders.Where(order => order.Ownership == PositionOwnership.SoftwareOwned))
                if (!orders.Any(order => order.ManagementMode == ManagedOrderManagementMode.Application
                    && order.EntryOrderId == openOrder.OrderId && order.Contract == openOrder.Contract))
                    known = false;
        }

        var unknownActions = _recovery.State.Operations.Any(operation =>
            (operation.Stage is RecoveryOperationStage.TransferStarted or RecoveryOperationStage.Unknown)
            && (operation.ActionKind is RecoveryActionKind.Entry or RecoveryActionKind.CancelOwnedOrder));
        if (unknownActions)
            known = false;
        var statusText = known
            ? entries.Count == 0 ? "Открытых программных заявок нет." : $"Подтверждено открытых программных заявок: {entries.Count}."
            : "Количество открытых программных заявок неизвестно; перед выбором выхода нужна новая сверка.";
        return new(known, entries, statusText, _exitCancellationResults);
    }

    internal async Task<TradingLoopOutcome> ResumeAfterExitAsync(CancellationToken cancellationToken = default)
    {
        var result = await _loop.ResumeAfterExitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _exitCancellationResults = [];
            _runtimeFailure = null;
            SignalWorker(runImmediately: true);
        }
        return Publish(result);
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var signal = CaptureWakeSignal();
                if (Interlocked.Exchange(ref _runImmediately, 0) != 0)
                {
                    Interlocked.Increment(ref _scheduledRunCount);
                    await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                    UpdateDeadlineStateAfterRun();
                    continue;
                }
                var deadline = NextWakeAt();
                if (deadline is null)
                {
                    await WaitForWakeAsync(signal, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var delay = deadline.Value - _clock.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    var elapsed = await WaitForWakeAsync(signal, delay, cancellationToken).ConfigureAwait(false);
                    if (!elapsed || _clock.UtcNow < deadline.Value)
                        continue;
                    Interlocked.Increment(ref _scheduledRunCount);
                    await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                    UpdateDeadlineStateAfterRun();
                    continue;
                }
                Interlocked.Increment(ref _scheduledRunCount);
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                UpdateDeadlineStateAfterRun();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _runtimeFailure = "Расписание локального сценария остановлено; новые входы заблокированы.";
            Publish(new(false, _runtimeFailure));
        }
    }

    private DateTimeOffset? NextWakeAt()
    {
        var deadlines = CollectDeadlines();
        if (deadlines.Count == 0)
            return null;

        var now = _clock.UtcNow;
        var due = deadlines.Any(deadline => deadline <= now);
        var nextFuture = deadlines.Where(deadline => deadline > now)
            .DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        if (!due)
            return nextFuture == DateTimeOffset.MaxValue ? null : nextFuture;

        if (_dueDeadlineRetryAt is { } retryAt && retryAt > now)
            return nextFuture < retryAt ? nextFuture : retryAt;
        return now;
    }

    private List<DateTimeOffset> CollectDeadlines()
    {
        var profile = _loop.Profile;
        var now = _clock.UtcNow;
        var orders = _orders.Orders.Where(IsMonitoredOrder).ToArray();
        var recoveryVenues = _recovery.State.Venues
            .Where(venue => venue.Phase != RecoveryReadPhase.Resolved).ToArray();
        var shouldPollStart = profile.SelectedMode == "TrueNorth AI setups" && profile.SavedOperatingIntent
            && !_loop.IsExitHeld && !profile.IsIncomplete();
        var runSearches = shouldPollStart && _loop.IsAdmitting
            && _loop.State.PendingSetup is null;
        var needsFallbackRecoveryCheck = shouldPollStart
            || orders.Any(order => order.PendingOperationId is not null)
            || recoveryVenues.Any(venue => venue.NextCheckAt is null && venue.NextReminderAt is null);
        if (!shouldPollStart && orders.Length == 0 && !_recovery.HasPendingRecovery)
        {
            _fallbackRecoveryDeadlineAt = null;
            return [];
        }

        var deadlines = new List<DateTimeOffset>();
        if (runSearches)
        {
            var routes = LocalScenarioContextSource.BuildRoutes(profile);
            var blockedAssets = orders
                .SelectMany(order => routes.Where(route => route.Venue == order.Venue
                        && route.Account == order.Account && route.Contract == order.Contract)
                    .Select(route => route.Asset))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            deadlines.AddRange(_loop.State.NextSearchAt
                .Where(pair => !blockedAssets.Contains(pair.Key))
                .Select(pair => pair.Value));
            deadlines.AddRange(_loop.State.LimitReassessments.Values
                .Select(reassessment => reassessment.NextReassessmentAt));
        }
        foreach (var order in orders)
        {
            if (order.NextManagementCheckAt is { } checkAt)
                deadlines.Add(checkAt);
            if (!order.PartialFillCancelAttempted && order.PartialFillCancelAt is { } cancelAt)
                deadlines.Add(cancelAt);
            if (order.NextEmergencyCloseAt is { } emergencyAt)
                deadlines.Add(emergencyAt);
        }
        foreach (var venue in recoveryVenues)
        {
            if (venue.NextCheckAt is { } nextCheck)
                deadlines.Add(nextCheck);
            if (venue.NextReminderAt is { } reminder)
                deadlines.Add(reminder);
        }

        if (needsFallbackRecoveryCheck)
            deadlines.Add(GetFallbackRecoveryDeadline(now, profile.Settings.RecoveryCheckSeconds));
        else
            _fallbackRecoveryDeadlineAt = null;
        return deadlines;
    }

    private DateTimeOffset GetFallbackRecoveryDeadline(DateTimeOffset now, int recoveryCheckSeconds)
    {
        if (_fallbackRecoveryDeadlineAt is not { } deadline)
        {
            deadline = now.AddSeconds(recoveryCheckSeconds);
            _fallbackRecoveryDeadlineAt = deadline;
        }
        return deadline;
    }

    private void UpdateDeadlineStateAfterRun()
    {
        _fallbackRecoveryDeadlineAt = null;
        UpdateDueDeadlineBackoff();
    }

    private void UpdateDueDeadlineBackoff()
    {
        var now = _clock.UtcNow;
        _dueDeadlineRetryAt = CollectDeadlines().Any(deadline => deadline <= now)
            ? now.Add(DueDeadlineRetryDelay)
            : null;
    }

    private Task CaptureWakeSignal()
    {
        lock (_wakeGate)
            return _wake.Task;
    }

    private async Task<bool> WaitForWakeAsync(Task signal, TimeSpan? duration, CancellationToken cancellationToken)
    {
        if (duration is null)
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var wait = duration.Value <= TimeSpan.Zero ? TimeSpan.Zero
            : duration.Value > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : duration.Value;
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = _delay(wait, delayCancellation.Token);
        var completed = await Task.WhenAny(signal, delay).WaitAsync(cancellationToken).ConfigureAwait(false);
        delayCancellation.Cancel();
        if (completed != delay)
            return false;
        await delay.ConfigureAwait(false);
        return true;
    }

    private bool IsMonitoredOrder(ManagedOrderState order) =>
        order.ManagementMode == ManagedOrderManagementMode.Application
        && ManagedOrderState.BlocksNewEntry(order)
        && (order.PositionQuantity > 0m || order.EntryTerminalState == ManagedEntryTerminalState.Open
            || order.PendingOperationId is not null);

    private static bool IsOwnedPosition(ManagedOrderState order) =>
        order.ManagementMode == ManagedOrderManagementMode.Application
        && order.PositionQuantity > 0m && order.Status != ManagedOrderStatus.Closed;

    private static bool IsOwnedOpenEntry(ManagedOrderState order) =>
        order.ManagementMode == ManagedOrderManagementMode.Application
        && order.EntryTerminalState == ManagedEntryTerminalState.Open
        && order.EntryOrderId is not null && order.RemainingQuantity > 0m;

    private static string FormatProfileReview(SettingsProfile profile)
    {
        var settings = profile.Settings;
        var assets = string.Join(", ", settings.Assets.Where(asset => !string.IsNullOrWhiteSpace(asset))
            .Select(asset => asset!.Trim()));
        if (assets.Length == 0)
            assets = "не заданы";
        var entry = settings.EntryType ?? "не задан";
        var selection = settings.CandidateSelectionMethod == "NetProfitAtTakeProfit"
            ? "чистый результат на TP" : "порядок активов";
        var margin = settings.MarginBalancePercent?.ToString("0.##", CultureInfo.CurrentCulture) ?? "не задан";
        var leverage = settings.Leverage?.ToString("0.##", CultureInfo.CurrentCulture) ?? "не задано";
        return $"Режим: {profile.SelectedMode}; активы: {assets}; вход: {entry}; выбор: {selection}; маржа: {margin}%; плечо: ×{leverage}.";
    }

    private void OnRecoveryStatusChanged() => Publish(new(true, _loop.Status));

    private TradingLoopOutcome Publish(TradingLoopOutcome result)
    {
        StateChanged?.Invoke();
        return result;
    }

    private void SignalWorker(bool runImmediately = false)
    {
        if (!_automaticScheduling || _disposed)
            return;
        if (runImmediately)
            Interlocked.Exchange(ref _runImmediately, 1);
        TaskCompletionSource wake;
        lock (_wakeGate)
        {
            wake = _wake;
            _wake = NewWakeSource();
        }
        wake.TrySetResult();
    }

    private static TaskCompletionSource NewWakeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _recovery.StatusChanged -= OnRecoveryStatusChanged;
        _loop.CloseAdmissionForShutdown();
        _shutdown.Cancel();
        SignalWorker();
        _notifications.Dispose();
    }

    private sealed class LocalScenarioNotificationChannel : INotificationChannel
    {
        private readonly ConcurrentQueue<NotificationMessage> _messages = new();
        internal IReadOnlyList<NotificationMessage> Messages => _messages.ToArray();
        public NotificationChannel Channel => NotificationChannel.Windows;
        public bool IsEnabled => true;
        public bool IsConfigured => true;

        public Task<NotificationChannelSendResult> SendAsync(
            NotificationMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.Enqueue(message);
            return Task.FromResult(new NotificationChannelSendResult(NotificationAttemptStatus.PassedToWindows));
        }
    }

    private sealed record SyntheticVenueState(
        decimal Balance,
        decimal AvailableMargin,
        IReadOnlyList<OpenPosition> Positions,
        IReadOnlyList<OpenOrder> Orders,
        IReadOnlyList<PositionProtection> Protections,
        IReadOnlyList<ClosedTrade> ClosedTrades,
        IReadOnlyDictionary<string, RecoveryOperationObservation> Outcomes)
    {
        internal static SyntheticVenueState Empty => new(10000m, 10000m, [], [], [], [],
            new Dictionary<string, RecoveryOperationObservation>(StringComparer.Ordinal));
    }

    private sealed class LocalScenarioAdapter : IManagedOrderTestAdapter
    {
        private const string Venue = "Local Synthetic Venue";
        private const string Account = "Local Synthetic Account";
        private const string Source = "Изолированный локальный сценарий";
        private readonly object _gate = new();
        private readonly string _statePath;
        private readonly IRecoveryClock _clock;
        private SyntheticVenueState _state;
        private Exception? _readFailure;
        private string? _initializationFailure;
        private RecoveryOperationOutcome _nextCancelOutcome = RecoveryOperationOutcome.Confirmed;
        private decimal? _fillDuringCancel;

        internal LocalScenarioAdapter(string statePath, IRecoveryClock clock)
        {
            _statePath = Path.GetFullPath(statePath);
            _clock = clock;
            try
            {
                _state = Load();
            }
            catch (InvalidDataException)
            {
                _state = SyntheticVenueState.Empty;
                _initializationFailure = "Синтетическое состояние локального рынка повреждено или недоступно; файл сохранён без изменений.";
            }
        }

        public TimeSpan MaximumSnapshotAge => TimeSpan.FromMinutes(5);
        internal string? InitializationFailure => _initializationFailure;
        internal IReadOnlyList<OpenPosition> Positions { get { lock (_gate) return _state.Positions.ToArray(); } }
        internal IReadOnlyList<OpenOrder> OpenOrders { get { lock (_gate) return _state.Orders.ToArray(); } }
        internal void FailNextRead(Exception exception) { lock (_gate) _readFailure = exception; }
        internal void SetNextCancelOutcome(RecoveryOperationOutcome outcome) { lock (_gate) _nextCancelOutcome = outcome; }
        internal void FillDuringNextCancel(decimal quantity) { lock (_gate) _fillDuringCancel = quantity; }

        public Task<RecoverySnapshotRead> ReadAsync(string venue, string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (venue != Venue || account != Account)
                    throw new InvalidOperationException("Локальный адаптер отказал в неизвестной области счёта.");
                if (_initializationFailure is not null)
                    throw new InvalidDataException(_initializationFailure);
                if (_readFailure is { } readFailure)
                {
                    _readFailure = null;
                    throw readFailure;
                }
                var now = _clock.UtcNow;
                var snapshot = new AccountSnapshot(Venue, Account, "Синтетический USDC pool", now, Source, "USDC",
                    "contract", AccountReadState.Available, null, now,
                    AccountSection<AccountBalance>.Available(new AccountBalance(new MoneyAmount(_state.Balance, "USDC"),
                        new MoneyAmount(_state.Balance, "USDC"), new MoneyAmount(_state.AvailableMargin, "USDC")), now, Source),
                    AccountSection<IReadOnlyList<OpenPosition>>.Available(_state.Positions.ToArray(), now, Source),
                    AccountSection<IReadOnlyList<OpenOrder>>.Available(_state.Orders.ToArray(), now, Source),
                    AccountSection<IReadOnlyList<PositionProtection>>.Available(_state.Protections.ToArray(), now, Source),
                    AccountSection<IReadOnlyList<ClosedTrade>>.Available(_state.ClosedTrades.ToArray(), now, Source));
                return Task.FromResult(new RecoverySnapshotRead(true, snapshot, true, true, true,
                    new Dictionary<string, RecoveryOperationObservation>(_state.Outcomes, StringComparer.Ordinal),
                    ClosedPositionHistoryComplete: true, EntryFillHistoryComplete: true));
            }
        }

        public Task<RecoveryOperationOutcome> SubmitEntryAsync(
            ManagedOrderState intent,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var now = _clock.UtcNow;
                var orderId = "local-entry-" + intent.IntentId;
                var positionId = "local-position-" + intent.IntentId;
                var filled = intent.EntryType == "Limit" ? decimal.Round(intent.Quantity / 2m, 8) : intent.Quantity;
                var remaining = intent.Quantity - filled;
                var positions = _state.Positions.ToList();
                if (filled > 0m)
                    positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, filled,
                        intent.EntryType == "Limit" ? "contract" : "contract", new MoneyAmount(0m, "USDC"),
                        PositionOwnership.SoftwareOwned, Source, positionId, [orderId]));
                var orders = _state.Orders.ToList();
                if (remaining > 0m)
                    orders.Add(new OpenOrder(intent.Asset, intent.Contract, intent.Direction, intent.Quantity,
                        filled, "contract", "Open", PositionOwnership.SoftwareOwned, "Синтетическая заявка.",
                        orderId, operationId));
                var observation = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                    intent.Contract, "PlaceOpeningOrder", RecoveryOperationOutcome.Confirmed, orderId,
                    filled > 0m ? positionId : null, intent.Direction, filled, remaining,
                    filled > 0m ? filled : null,
                    EntryOrderState: remaining > 0m ? ManagedEntryTerminalState.Open : ManagedEntryTerminalState.Filled,
                    FirstFillAt: filled > 0m ? now : null);
                var updated = _state with
                {
                    Positions = positions,
                    Orders = orders,
                    Outcomes = WithOutcome(_state, observation)
                };
                if (!Save(updated))
                    return Task.FromResult(RecoveryOperationOutcome.Unknown);
                _state = updated;
                return Task.FromResult(RecoveryOperationOutcome.Confirmed);
            }
        }

        public Task<RecoveryOperationOutcome> CancelRemainderAsync(
            ManagedOrderState intent,
            string orderId,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var current = _state.Orders.SingleOrDefault(order => order.OrderId == orderId
                    && order.Contract == intent.Contract && order.Ownership == PositionOwnership.SoftwareOwned);
                var outcome = _nextCancelOutcome;
                _nextCancelOutcome = RecoveryOperationOutcome.Confirmed;
                var extraFill = _fillDuringCancel;
                _fillDuringCancel = null;
                if (current is null)
                    outcome = RecoveryOperationOutcome.Unknown;

                var filled = current?.FilledQuantity ?? intent.FilledQuantity;
                var positions = _state.Positions.ToList();
                if (current is not null && extraFill is > 0m)
                {
                    filled = Math.Min(intent.Quantity, filled + extraFill.Value);
                    positions.RemoveAll(position => position.PositionId == intent.PositionId);
                    if (filled > 0m)
                        positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, filled,
                            "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned, Source,
                            intent.PositionId, [orderId]));
                }
                var orders = _state.Orders.ToList();
                if (outcome == RecoveryOperationOutcome.Confirmed)
                    orders.RemoveAll(order => order.OrderId == orderId);
                else if (current is not null && extraFill is > 0m)
                    orders[orders.FindIndex(order => order.OrderId == orderId)] = current with { FilledQuantity = filled };

                var remaining = Math.Max(0m, intent.Quantity - filled);
                var observation = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                    intent.Contract, "CancelOpeningRemainder", outcome, orderId,
                    filled > 0m ? intent.PositionId : null, intent.Direction, filled,
                    outcome == RecoveryOperationOutcome.Confirmed ? 0m : remaining,
                    filled > 0m ? filled : intent.PositionQuantity,
                    EntryOrderState: outcome == RecoveryOperationOutcome.Confirmed
                        ? remaining > 0m ? ManagedEntryTerminalState.Cancelled : ManagedEntryTerminalState.Filled
                        : ManagedEntryTerminalState.Open,
                    FirstFillAt: filled > 0m ? _clock.UtcNow : intent.FirstPartialFillAt);
                var updated = _state with
                {
                    Positions = positions,
                    Orders = orders,
                    Outcomes = WithOutcome(_state, observation)
                };
                if (!Save(updated))
                    return Task.FromResult(RecoveryOperationOutcome.Unknown);
                _state = updated;
                return Task.FromResult(outcome);
            }
        }

        public Task<RecoveryOperationOutcome> SetProtectionAsync(
            ManagedOrderState intent,
            string positionId,
            decimal actualQuantity,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var position = _state.Positions.SingleOrDefault(item => item.PositionId == positionId
                    && item.Contract == intent.Contract && item.Quantity == actualQuantity);
                if (position is null)
                    return Task.FromResult(RecoveryOperationOutcome.Unknown);
                var takeProfitId = "local-tp-" + positionId;
                var stopLossId = "local-sl-" + positionId;
                var protection = new PositionProtection(intent.Contract, new MoneyAmount(intent.TakeProfit, "USDC"),
                    new MoneyAmount(intent.StopLoss, "USDC"), positionId, PositionOwnership.SoftwareOwned,
                    actualQuantity, actualQuantity, takeProfitId, stopLossId);
                var updated = _state with
                {
                    Protections = _state.Protections.Where(item => item.PositionId != positionId).Append(protection).ToArray(),
                    Outcomes = WithOutcome(_state, new RecoveryOperationObservation(operationId, intent.Venue,
                        intent.Account, intent.Contract, "SetFullPositionProtection", RecoveryOperationOutcome.Confirmed,
                        PositionId: positionId, Direction: intent.Direction, PositionQuantity: actualQuantity,
                        TakeProfitOrderId: takeProfitId, StopLossOrderId: stopLossId))
                };
                if (!Save(updated))
                    return Task.FromResult(RecoveryOperationOutcome.Unknown);
                _state = updated;
                return Task.FromResult(RecoveryOperationOutcome.Confirmed);
            }
        }

        public Task<RecoveryOperationOutcome> CloseReduceOnlyAsync(
            ManagedOrderState intent,
            string positionId,
            PositionDirection positionDirection,
            decimal exactQuantity,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var position = _state.Positions.SingleOrDefault(item => item.PositionId == positionId
                    && item.Direction == positionDirection && item.Quantity == exactQuantity);
                if (position is null)
                    return Task.FromResult(RecoveryOperationOutcome.Unknown);
                var updated = _state with
                {
                    Positions = _state.Positions.Where(item => item.PositionId != positionId).ToArray(),
                    Protections = _state.Protections.Where(item => item.PositionId != positionId).ToArray(),
                    Outcomes = WithOutcome(_state, new RecoveryOperationObservation(operationId, intent.Venue,
                        intent.Account, intent.Contract, "ReduceOnlyClose", RecoveryOperationOutcome.Confirmed,
                        PositionId: positionId, Direction: positionDirection, PositionQuantity: 0m))
                };
                if (!Save(updated))
                    return Task.FromResult(RecoveryOperationOutcome.Unknown);
                _state = updated;
                return Task.FromResult(RecoveryOperationOutcome.Confirmed);
            }
        }

        internal bool CloseAtTakeProfit(ManagedOrderState intent)
        {
            lock (_gate)
            {
                var position = _state.Positions.SingleOrDefault(item => item.PositionId == intent.PositionId
                    && item.Contract == intent.Contract && item.Ownership == PositionOwnership.SoftwareOwned);
                var protection = _state.Protections.SingleOrDefault(item => item.PositionId == intent.PositionId
                    && item.Contract == intent.Contract && item.Ownership == PositionOwnership.SoftwareOwned);
                if (position?.Quantity is not { } quantity || protection?.TakeProfitOrderId is null
                    || protection.TakeProfitCoveredQuantity != quantity)
                    return false;
                var now = _clock.UtcNow;
                var closed = new ClosedTrade(intent.Asset, intent.Contract, intent.Direction,
                    intent.CreatedAt, now, new MoneyAmount(0m, "USDC"), "Синтетическое исполнение TP.",
                    intent.PositionId, intent.EntryOrderId is null ? [] : [intent.EntryOrderId],
                    protection.TakeProfitOrderId, quantity, PositionOwnership.SoftwareOwned,
                    CloseKind: TradeCloseKind.TakeProfit);
                var updated = _state with
                {
                    Positions = _state.Positions.Where(item => item.PositionId != intent.PositionId).ToArray(),
                    Protections = _state.Protections.Where(item => item.PositionId != intent.PositionId).ToArray(),
                    ClosedTrades = _state.ClosedTrades.Append(closed).ToArray()
                };
                if (!Save(updated))
                    return false;
                _state = updated;
                return true;
            }
        }

        private SyntheticVenueState Load()
        {
            if (!File.Exists(_statePath))
                return SyntheticVenueState.Empty;
            try
            {
                var state = JsonSerializer.Deserialize<SyntheticVenueState>(File.ReadAllText(_statePath), JsonOptions);
                if (state is not null && state.Balance > 0m && state.AvailableMargin >= 0m
                    && state.Positions is not null && state.Orders is not null && state.Protections is not null
                    && state.ClosedTrades is not null && state.Outcomes is not null)
                    return state;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or NotSupportedException)
            {
                throw new InvalidDataException("Состояние синтетического рынка недоступно.", exception);
            }
            throw new InvalidDataException("Состояние синтетического рынка повреждено.");
        }

        private bool Save(SyntheticVenueState state)
        {
            if (_initializationFailure is not null)
                return false;
            var directory = Path.GetDirectoryName(_statePath)!;
            Directory.CreateDirectory(directory);
            var temporary = _statePath + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporary, _statePath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or NotSupportedException)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return false;
            }
        }

        private static IReadOnlyDictionary<string, RecoveryOperationObservation> WithOutcome(
            SyntheticVenueState state,
            RecoveryOperationObservation observation)
        {
            var outcomes = new Dictionary<string, RecoveryOperationObservation>(state.Outcomes, StringComparer.Ordinal)
            {
                [observation.OperationId] = observation
            };
            return outcomes;
        }
    }

    private sealed class LocalScenarioAiSource(IRecoveryClock clock) : IAiAnalysisSource
    {
        private const string Source = "Изолированный локальный сценарий";
        private int _requestCount;
        private int _noSetupNext;

        internal int RequestCount => Volatile.Read(ref _requestCount);
        internal void ReturnNoSetupNext() => Interlocked.Exchange(ref _noSetupNext, 1);

        public Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            if (Interlocked.Exchange(ref _noSetupNext, 0) != 0)
            {
                var noSetup = new AiSourceResponse(request.Id, JsonSerializer.Serialize(new
                {
                    status = "NO_SETUP",
                    market = request.Market,
                    order_type = request.OrderType,
                    side = (string?)null,
                    reference_price = (decimal?)null,
                    reference_time_utc = (string?)null,
                    entry_price = (decimal?)null,
                    take_profit = (decimal?)null,
                    stop_loss = (decimal?)null,
                    reason = Source + "; синтетический ответ без сетапа.",
                    invalidation = (string?)null
                }), true);
                return Task.FromResult(noSetup);
            }

            var response = new AiSourceResponse(request.Id, JsonSerializer.Serialize(new
            {
                status = "SETUP",
                market = request.Market,
                order_type = request.OrderType,
                side = "LONG",
                reference_price = 50m,
                reference_time_utc = clock.UtcNow.AddSeconds(-5).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                entry_price = request.OrderType == "MARKET" ? (decimal?)null : 50m,
                take_profit = 55m,
                stop_loss = 45m,
                reason = Source + "; рыночные данные не запрашивались.",
                invalidation = "Синтетический уровень теста достигнут."
            }), true);
            return Task.FromResult(response);
        }
    }

    private sealed class LocalScenarioContextSource(
        RecoveryCoordinator recovery,
        Func<string, string, string, string, TradingLoopRoute> routeFactory) : ITradingLoopContextSource
    {
        private const string Venue = "Local Synthetic Venue";
        private const string Account = "Local Synthetic Account";
        private const string Source = "Изолированный локальный сценарий";
        private static readonly RiskFreshnessPolicy Freshness = new(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromDays(30), TimeSpan.FromMinutes(10));

        internal static IReadOnlyList<TradingLoopRoute> BuildRoutes(SettingsProfile profile) =>
            profile.Settings.Assets.Where(asset => !string.IsNullOrWhiteSpace(asset))
                .Select(asset => new TradingLoopRoute(asset!.Trim().ToUpperInvariant(), Venue, Account,
                    asset.Trim().ToUpperInvariant() + "-PERP"))
                .ToArray();

        public IReadOnlyList<TradingLoopRoute> GetRoutes(SettingsProfile profile) =>
            BuildRoutes(profile).Select(route => routeFactory(route.Asset, route.Venue, route.Account, route.Contract))
                .ToArray();

        public async Task<TradingLoopContext> ReadAsync(
            TradingLoopRoute route,
            IReadOnlyList<AccountSnapshot> accounts,
            EntryEligibilitySetup? setup,
            SettingsProfile profile,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (route.Venue != Venue || route.Account != Account)
                return new(false, false, Reason: "Неизвестная область локального синтетического счёта.");
            if (setup is null)
                return new(true, true, MinimumEntryAffordable: true);
            var account = accounts.SingleOrDefault(item => item.Venue == Venue && item.Account == Account);
            if (account is null)
                return new(false, false, Reason: "Синтетический снимок счёта отсутствует.");

            var example = EntryEligibilityExample.Create();
            var template = example.Pools[0];
            var instrumentTemplate = template.Instruments.Single(item => item.Identity.BaseAsset == "SOL");
            var instruments = GetRoutes(profile).Select(candidate =>
            {
                var observed = now.AddSeconds(-5);
                var asset = candidate.Asset;
                var contract = candidate.Contract;
                return instrumentTemplate with
                {
                    Identity = instrumentTemplate.Identity with
                    {
                        Venue = Venue,
                        Contract = contract,
                        BaseAsset = asset,
                        Source = Source,
                        ObservedAt = observed
                    },
                    RulesSource = Source,
                    RulesObservedAt = observed,
                    Funding = instrumentTemplate.Funding is { } funding
                        ? funding with { Price = 50m, PriceUnit = "USDC/" + asset, Source = Source, ObservedAt = observed }
                        : null
                };
            }).ToArray();
            var positions = account.Positions.Value!.Select(position =>
            {
                var protection = account.Protections.Value!.SingleOrDefault(item => item.PositionId == position.PositionId);
                return new RiskPositionInput(position, position.PositionId ?? position.Contract,
                    EntryEligibilityExample.Evidence(50m, "USDC/" + position.Asset, now.AddSeconds(-2), position.Contract),
                    protection?.StopLoss is { } stopLoss
                        ? EntryEligibilityExample.Evidence(stopLoss.Value, stopLoss.Currency, now.AddSeconds(-2), position.Contract)
                        : null,
                    protection?.StopLossCoveredQuantity is { } covered
                        ? EntryEligibilityExample.Evidence(covered, position.QuantityUnit, now.AddSeconds(-2), position.Contract)
                        : null);
            }).ToArray();
            var managedOrders = recovery.State.ManagedOrders;
            var riskOrders = account.Orders.Value!.Select(order =>
            {
                var managed = managedOrders.SingleOrDefault(item => item.EntryOrderId == order.OrderId);
                return new RiskOrderInput(order, order.OrderId ?? order.Contract,
                    EntryEligibilityExample.Evidence(RiskOrderRole.Opening, "role", now.AddSeconds(-2), order.Contract),
                    EntryEligibilityExample.Evidence(50m, "USDC/" + order.Asset, now.AddSeconds(-2), order.Contract),
                    managed is null ? null : EntryEligibilityExample.Evidence(managed.StopLoss, "USDC", now.AddSeconds(-2), order.Contract),
                    managed is null ? null : EntryEligibilityExample.Evidence(managed.FilledQuantity, order.QuantityUnit, now.AddSeconds(-2), order.Contract),
                    EntryEligibilityExample.Evidence(0m, "USDC", now.AddSeconds(-2), order.Contract), []);
            }).ToArray();
            var priceAt = now.AddSeconds(-2);
            var pool = template with
            {
                Account = account,
                Instruments = instruments,
                Positions = positions,
                Orders = riskOrders,
                SetupBinding = new RiskSetupMarketBinding(Venue, route.Contract, route.Asset,
                    setup.Market, Source, now.AddSeconds(-2)),
                CurrentMarketPrice = setup.EntryType == "Market"
                    ? EntryEligibilityExample.Evidence(50m, "USDC/" + route.Asset, priceAt, route.Contract) : null,
                VerifiedLimitPrice = setup.EntryType == "Limit" && setup.LimitPrice is { } limit
                    ? EntryEligibilityExample.Evidence(limit, "USDC/" + route.Asset, priceAt, route.Contract) : null,
                CandidateStopCoveredBaseQuantity = EntryEligibilityExample.Evidence(20m, route.Asset, now.AddSeconds(-2), route.Contract),
                AvailableMarginIncludesOrderReserves = EntryEligibilityExample.Evidence(true, "bool", now.AddSeconds(-2)),
                OtherUnreservedRequirements = EntryEligibilityExample.Evidence(0m, "USDC", now.AddSeconds(-2)),
                AdditionalMaintenanceHeadroom = EntryEligibilityExample.Evidence(100m, "USDC", now.AddSeconds(-2))
            };
            var input = example with
            {
                Settings = profile.Copy(),
                Setup = setup,
                EvaluatedAt = now,
                Pools = [pool],
                AllConnectedExposureCovered = EntryEligibilityExample.Evidence(true, "bool", now.AddSeconds(-2))
            };
            return await Task.FromResult(new TradingLoopContext(true, true, input,
                MinimumEntryAffordable: true)).ConfigureAwait(false);
        }
    }
}
