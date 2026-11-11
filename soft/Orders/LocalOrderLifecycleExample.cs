using System.IO;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Orders;

internal sealed class LocalOrderLifecycleExampleSession : IDisposable
{
    private static readonly string ExampleRoot = Path.Combine(Path.GetTempPath(),
        "TrueNorth.TradeFlow", "local-order-examples");
    private readonly string _allowedExampleRoot;
    private readonly string _journalRoot;
    private readonly LocalExampleClock _clock;
    private readonly AppSettings _settings;
    private readonly LocalExampleAdapter _adapter;
    private readonly RecoveryJournalStore _store;
    private readonly RecoveryCoordinator _recovery;
    private readonly RecordingExampleChannel _windows;
    private readonly RecordingExampleChannel _telegram;
    private readonly NotificationDispatcher _notifications;
    private readonly OrderLifecycleCoordinator _orders;
    private OrderLifecycleResult? _lastResult;
    private string? _intentId;
    private bool _disposed;

    internal LocalOrderLifecycleExampleSession(string? exampleRoot = null)
    {
        _allowedExampleRoot = Path.GetFullPath(exampleRoot ?? ExampleRoot);
        _journalRoot = Path.Combine(_allowedExampleRoot, Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        _clock = new LocalExampleClock(now);
        _settings = new AppSettings
        {
            LimitOrderCheckMinutes = 2,
            PositionCheckMinutes = 60,
            PartialFillCancelMinutes = 60,
            NotificationsEnabled = true,
            OrderChangeNotifications = true,
            OpeningNotifications = false,
            ClosingNotifications = false,
            ErrorNotifications = false,
            RecoveryNotifications = false
        };
        _adapter = new LocalExampleAdapter(_clock);
        _store = new RecoveryJournalStore(Path.Combine(_journalRoot, "recovery-state.json"));
        _recovery = new RecoveryCoordinator(_store, _settings, _clock, _adapter);
        _windows = new RecordingExampleChannel(NotificationChannel.Windows);
        _telegram = new RecordingExampleChannel(NotificationChannel.Telegram);
        _notifications = new NotificationDispatcher(_recovery, [_windows, _telegram], _settings);
        _orders = new OrderLifecycleCoordinator(_recovery, _adapter, _settings, _clock, _notifications);
    }

    internal RecoveryCoordinator Recovery => _recovery;
    internal OrderLifecycleCoordinator Orders => _orders;
    internal string JournalPath => _store.PathName;
    internal IReadOnlyList<NotificationMessage> OwnerChoiceNotices => _windows.Sent;

    internal async Task<OrderLifecycleResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_intentId is not null)
            return _lastResult ?? new(true, "Локальный пример уже сверён.", _intentId);

        var now = _clock.UtcNow;
        var id = "local-example-" + Guid.NewGuid().ToString("N");
        var orderId = "example-order-" + Guid.NewGuid().ToString("N");
        var positionId = "example-position-" + Guid.NewGuid().ToString("N");
        var operationId = id + ":entry:example";
        var firstFillAt = now.AddMinutes(-5);
        var quantity = 0.040m;
        var filled = 0.025m;
        var remainder = quantity - filled;
        var state = new ManagedOrderState(id, "Local example", "Synthetic account", "BTC", "BTC-PERP",
            PositionDirection.Long, "Limit", quantity, 70000m, 72500m, 68700m,
            EntryEligibilityStatus.ScenarioReady, now.AddMinutes(-10), now.AddMinutes(-10),
            Status: ManagedOrderStatus.Protected,
            EntryOrderId: orderId,
            PositionId: positionId,
            FilledQuantity: filled,
            RemainingQuantity: remainder,
            PositionQuantity: filled,
            TakeProfitOrderId: "example-app-tp",
            StopLossOrderId: "example-app-sl",
            TakeProfitCoveredQuantity: filled,
            StopLossCoveredQuantity: filled,
            EntryTerminalState: ManagedEntryTerminalState.Open,
            LastAppliedOperationId: operationId,
            FirstPartialFillAt: firstFillAt,
            PartialFillCancelAt: firstFillAt.AddMinutes(_settings.PartialFillCancelMinutes),
            NextManagementCheckAt: now,
            OriginEntryOperationId: operationId);

        var operation = new RecoveryOperationRequest(operationId, state.Venue, state.Account, state.Contract,
            "PlaceOpeningOrder", ActionKind: RecoveryActionKind.Entry);
        var prepared = await _recovery.PrepareOperationAsync(operation, savedOperatingIntent: true, cancellationToken)
            .ConfigureAwait(false);
        var began = prepared && await _recovery.BeginTransferAsync(operationId, savedOperatingIntent: true,
            cancellationToken).ConfigureAwait(false);
        var completed = began && await _recovery.CompleteTransferAsync(operationId,
            RecoveryTransferOutcome.Confirmed, _settings, "Синтетический локальный пример.", cancellationToken)
            .ConfigureAwait(false);
        if (!completed)
            throw new InvalidOperationException($"Изолированное действие локального примера не завершилось: prepared={prepared}, began={began}, completed={completed}; {_recovery.StorageMessage ?? _recovery.StorageStatus.ToString()}.");

        if (await _recovery.SaveManagedOrderAsync(state, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("Не удалось сохранить намерение локального примера.");

        _adapter.Seed(state, operationId, firstFillAt,
            ownerTakeProfit: 72400m, ownerStopLoss: 68800m);
        _intentId = id;
        var result = await _orders.RunDueChecksAsync(id, savedOperatingIntent: false, cancellationToken)
            .ConfigureAwait(false);
        _lastResult = result;
        return result;
    }

    internal async Task<OrderLifecycleResult> ContinueWithOwnerParametersAsync(
        CancellationToken cancellationToken = default)
    {
        if (_intentId is null)
            return new(false, "Локальный пример ещё не подготовлен.");
        var result = await _orders.ContinueWithOwnerParametersAsync(_intentId, savedOperatingIntent: false,
            cancellationToken).ConfigureAwait(false);
        _lastResult = result;
        return result;
    }

    internal async Task<OrderLifecycleResult> TransferToManualAsync(
        CancellationToken cancellationToken = default)
    {
        if (_intentId is null)
            return new(false, "Локальный пример ещё не подготовлен.");
        var result = await _orders.TransferToManualAsync(_intentId, cancellationToken).ConfigureAwait(false);
        _lastResult = result;
        return result;
    }

    internal AccountOrderLifecycleExample CreatePresentation(OrderLifecycleResult? result = null)
    {
        result ??= _lastResult;
        var state = _intentId is null
            ? null : _recovery.State.ManagedOrders.SingleOrDefault(order => order.IntentId == _intentId);
        if (state is null)
            return new("Локальное намерение ещё не сохранено.", string.Empty, string.Empty, string.Empty,
                string.Empty, result?.Status ?? string.Empty, false);

        var ownerTakeProfit = state.PendingOwnerTakeProfit ?? state.TakeProfit;
        var ownerStopLoss = state.PendingOwnerStopLoss ?? state.StopLoss;
        var order = $"{state.Contract} · {state.EntryType} · исполнено {Format(state.FilledQuantity)} из {Format(state.Quantity)}"
            + (state.LimitPrice is { } limit ? $" · лимит {Format(limit)}" : string.Empty);
        var quantities = $"Позиция сейчас {Format(state.PositionQuantity)} · остаток заявки {Format(state.RemainingQuantity)}"
            + (state.ManagementMode == ManagedOrderManagementMode.Manual
                ? $" · передано: позиция {Format(state.TransferredPositionQuantity)} / остаток {Format(state.TransferredRemainderQuantity)}"
                : string.Empty);
        var firstFill = state.FirstPartialFillAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "не установлено";
        var deadline = state.PartialFillCancelAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "не назначен";
        var check = state.NextManagementCheckAt?.ToLocalTime().ToString("HH:mm:ss zzz") ?? "не назначена";
        var protection = $"TP {Format(ownerTakeProfit)} / SL {Format(ownerStopLoss)} · покрытие TP {Format(state.TakeProfitCoveredQuantity)} / SL {Format(state.StopLossCoveredQuantity)}"
            + $" при позиции {Format(state.PositionQuantity)}";
        var mode = state.ManagementMode switch
        {
            ManagedOrderManagementMode.AwaitingOwnerChoice => "ожидается выбор владельца",
            ManagedOrderManagementMode.Manual => "передано владельцу; изменения приложением отключены",
            _ => "сопровождение приложения"
        };
        var ownerStatus = $"Владелец: {mode} · состояние журнала: {StatusText(state.Status)}"
            + (string.IsNullOrWhiteSpace(state.LastFailure) ? string.Empty : $" · {state.LastFailure}");
        var decision = result?.Status ?? state.LastFailure ?? "Состояние загружено из локального журнала.";
        var canChoose = state.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
            && state.PendingOperationId is null && state.PendingOwnerTakeProfit is not null
            && state.PendingOwnerStopLoss is not null;
        return new(order, quantities,
            $"Первое частичное исполнение: {firstFill} · отмена остатка: {deadline} · следующая проверка: {check}",
            protection, ownerStatus, decision, canChoose);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notifications.Dispose();
        var fullTarget = Path.GetFullPath(_journalRoot);
        var allowedParent = _allowedExampleRoot;
        if (Directory.Exists(fullTarget)
            && string.Equals(Path.GetDirectoryName(fullTarget), allowedParent, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(Path.GetFileName(fullTarget), "N", out _))
        {
            try
            {
                Directory.Delete(fullTarget, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Format(decimal? value) =>
        value?.ToString("0.############################", System.Globalization.CultureInfo.CurrentCulture) ?? "неизвестно";

    private static string StatusText(ManagedOrderStatus status) => status switch
    {
        ManagedOrderStatus.OwnerDecisionRequired => "ожидает решения владельца",
        ManagedOrderStatus.Protected => "TP/SL сверены",
        ManagedOrderStatus.PositionUnprotected => "позиция без полной защиты",
        ManagedOrderStatus.ReconciliationRequired or ManagedOrderStatus.OwnershipAmbiguous => "нужна сверка",
        ManagedOrderStatus.Closed => "позиция закрыта",
        _ => status.ToString()
    };

    private sealed class LocalExampleClock(DateTimeOffset utcNow) : IRecoveryClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingExampleChannel(NotificationChannel channel) : INotificationChannel
    {
        public NotificationChannel Channel => channel;
        public bool IsEnabled => true;
        public bool IsConfigured => true;
        internal List<NotificationMessage> Sent { get; } = [];

        public Task<NotificationChannelSendResult> SendAsync(
            NotificationMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(message);
            return Task.FromResult(new NotificationChannelSendResult(channel == NotificationChannel.Windows
                ? NotificationAttemptStatus.PassedToWindows : NotificationAttemptStatus.AcceptedByTelegram));
        }
    }

    private sealed class LocalExampleAdapter(LocalExampleClock clock) : IManagedOrderTestAdapter
    {
        private ManagedOrderState? _state;
        private OpenPosition? _position;
        private OpenOrder? _entryOrder;
        private PositionProtection? _protection;
        private string? _entryOperationId;
        private DateTimeOffset? _firstFillAt;
        private readonly Dictionary<string, RecoveryOperationObservation> _observations = new(StringComparer.Ordinal);

        public TimeSpan MaximumSnapshotAge => TimeSpan.FromMinutes(5);
        internal List<string> ActionCalls { get; } = [];

        internal void Seed(ManagedOrderState state, string operationId, DateTimeOffset firstFillAt,
            decimal ownerTakeProfit, decimal ownerStopLoss)
        {
            _state = state;
            _entryOperationId = operationId;
            _firstFillAt = firstFillAt;
            _position = new OpenPosition(state.Asset, state.Contract, state.Direction, state.FilledQuantity,
                "BTC", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned,
                "Изолированный локальный пример.", state.PositionId, [state.EntryOrderId!]);
            _entryOrder = new OpenOrder(state.Asset, state.Contract, state.Direction, state.Quantity,
                state.FilledQuantity, "BTC", "Open", PositionOwnership.SoftwareOwned,
                "Частично исполнена.", state.EntryOrderId, operationId);
            _protection = new PositionProtection(state.Contract,
                new MoneyAmount(ownerTakeProfit, "USDC"), new MoneyAmount(ownerStopLoss, "USDC"),
                state.PositionId, PositionOwnership.Manual, state.FilledQuantity, state.FilledQuantity,
                "example-owner-tp", "example-owner-sl");
            _observations[operationId] = new RecoveryOperationObservation(operationId, state.Venue, state.Account,
                state.Contract, "PlaceOpeningOrder", RecoveryOperationOutcome.Confirmed,
                state.EntryOrderId, state.PositionId, state.Direction, state.FilledQuantity,
                state.RemainingQuantity, state.PositionQuantity, state.TakeProfitOrderId, state.StopLossOrderId,
                EntryOrderState: ManagedEntryTerminalState.Open, FirstFillAt: firstFillAt);
        }

        public Task<RecoverySnapshotRead> ReadAsync(string venue, string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = _state ?? throw new InvalidOperationException("Локальный пример не инициализирован.");
            var observedAt = clock.UtcNow;
            var snapshot = new AccountSnapshot(state.Venue, state.Account, "Синтетический USDC",
                observedAt, "Локальный пример", "USDC", "BTC", AccountReadState.Available, null, observedAt,
                AccountSection<AccountBalance>.Available(new AccountBalance(
                    new MoneyAmount(1000m, "USDC"), new MoneyAmount(1000m, "USDC"),
                    new MoneyAmount(500m, "USDC")), observedAt, "Локальный пример"),
                AccountSection<IReadOnlyList<OpenPosition>>.Available(_position is null ? [] : [_position],
                    observedAt, "Локальный пример"),
                AccountSection<IReadOnlyList<OpenOrder>>.Available(_entryOrder is null ? [] : [_entryOrder],
                    observedAt, "Локальный пример"),
                AccountSection<IReadOnlyList<PositionProtection>>.Available(_protection is null ? [] : [_protection],
                    observedAt, "Локальный пример"),
                AccountSection<IReadOnlyList<ClosedTrade>>.ConfirmedEmpty(Array.Empty<ClosedTrade>(), observedAt,
                    "Локальный пример"));
            var fillTimes = _entryOperationId is { } operationId && _state.EntryOrderId is { } orderId
                && _firstFillAt is { } firstFill
                    ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { [orderId] = firstFill }
                    : null;
            return Task.FromResult(new RecoverySnapshotRead(true, snapshot,
                OpeningOrderRemaindersComplete: true,
                ProtectionCoverageComplete: true,
                UnfinishedOperationsComplete: true,
                OperationOutcomes: new Dictionary<string, RecoveryOperationObservation>(_observations,
                    StringComparer.Ordinal),
                ClosedPositionHistoryComplete: true,
                EntryFirstFillTimes: fillTimes,
                EntryFillHistoryComplete: true));
        }

        public Task<RecoveryOperationOutcome> SubmitEntryAsync(
            ManagedOrderState intent, string operationId, CancellationToken cancellationToken)
        {
            ActionCalls.Add("entry");
            return Task.FromResult(RecoveryOperationOutcome.Unknown);
        }

        public Task<RecoveryOperationOutcome> CancelRemainderAsync(
            ManagedOrderState intent, string orderId, string operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActionCalls.Add("cancel");
            if (_entryOrder?.OrderId != orderId || _entryOrder.RemainingQuantity is not { } remainder
                || remainder <= 0m)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            _entryOrder = null;
            var position = _position;
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "CancelOpeningRemainder", RecoveryOperationOutcome.Confirmed,
                orderId, position?.PositionId ?? intent.PositionId, intent.Direction,
                intent.FilledQuantity, 0m, position?.Quantity ?? 0m,
                intent.TakeProfitOrderId, intent.StopLossOrderId,
                EntryOrderState: ManagedEntryTerminalState.Cancelled);
            return Task.FromResult(RecoveryOperationOutcome.Confirmed);
        }

        public Task<RecoveryOperationOutcome> SetProtectionAsync(
            ManagedOrderState intent, string positionId, decimal actualQuantity, string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActionCalls.Add("protect");
            if (_position?.PositionId != positionId || _position.Quantity != actualQuantity)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            _protection = new PositionProtection(intent.Contract,
                new MoneyAmount(intent.TakeProfit, "USDC"), new MoneyAmount(intent.StopLoss, "USDC"),
                positionId, PositionOwnership.SoftwareOwned, actualQuantity, actualQuantity,
                "example-software-tp", "example-software-sl");
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "SetFullPositionProtection", RecoveryOperationOutcome.Confirmed,
                PositionId: positionId, Direction: intent.Direction, PositionQuantity: actualQuantity,
                TakeProfitOrderId: _protection.TakeProfitOrderId, StopLossOrderId: _protection.StopLossOrderId);
            return Task.FromResult(RecoveryOperationOutcome.Confirmed);
        }

        public Task<RecoveryOperationOutcome> CloseReduceOnlyAsync(
            ManagedOrderState intent, string positionId, PositionDirection positionDirection, decimal exactQuantity,
            string operationId, CancellationToken cancellationToken)
        {
            ActionCalls.Add("close");
            return Task.FromResult(RecoveryOperationOutcome.Unknown);
        }
    }
}
