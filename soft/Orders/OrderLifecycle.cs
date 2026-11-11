using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Orders;

internal enum ManagedOrderStatus
{
    IntentSaved,
    EntryRejected,
    EntryOpen,
    PositionUnprotected,
    Protected,
    ReconciliationRequired,
    EmergencyCloseWaiting,
    EmergencyClosePaused,
    Cancelled,
    Closed,
    OwnershipAmbiguous
}

internal enum ManagedEntryTerminalState
{
    Unknown,
    NotSubmitted,
    Open,
    Cancelled,
    Filled,
    Rejected
}

internal enum ManagedOrderAction
{
    Entry,
    CancelRemainder,
    Protection,
    EmergencyClose
}

internal sealed record ManagedOrderState(
    string IntentId,
    string Venue,
    string Account,
    string Asset,
    string Contract,
    PositionDirection Direction,
    string EntryType,
    decimal Quantity,
    decimal? LimitPrice,
    decimal TakeProfit,
    decimal StopLoss,
    EntryEligibilityStatus EligibilityStatus,
    DateTimeOffset EligibilityEvaluatedAt,
    DateTimeOffset CreatedAt,
    ManagedOrderStatus Status = ManagedOrderStatus.IntentSaved,
    string? EntryOrderId = null,
    string? PositionId = null,
    decimal FilledQuantity = 0m,
    decimal RemainingQuantity = 0m,
    decimal PositionQuantity = 0m,
    string? TakeProfitOrderId = null,
    string? StopLossOrderId = null,
    decimal TakeProfitCoveredQuantity = 0m,
    decimal StopLossCoveredQuantity = 0m,
    string? PendingOperationId = null,
    ManagedOrderAction? PendingAction = null,
    decimal? PendingQuantity = null,
    int EmergencyCloseRetriesUsed = 0,
    DateTimeOffset? NextEmergencyCloseAt = null,
    bool EmergencyCloseNoticeSent = false,
    bool EmergencyCloseNoticePending = false,
    string? LastFailure = null,
    decimal TakeProfitClosedQuantity = 0m,
    decimal StopLossClosedQuantity = 0m,
    decimal? LastFailedProtectionQuantity = null,
    ManagedEntryTerminalState EntryTerminalState = ManagedEntryTerminalState.NotSubmitted,
    RecoveryRetryability EmergencyCloseRetryability = RecoveryRetryability.Unknown,
    ManagedOrderStatus? PendingPriorStatus = null,
    string? LastAppliedOperationId = null,
    long Revision = 0)
{
    internal static RecoveryActionKind RecoveryKind(ManagedOrderAction action) => action switch
    {
        ManagedOrderAction.Entry => RecoveryActionKind.Entry,
        ManagedOrderAction.CancelRemainder => RecoveryActionKind.CancelOwnedOrder,
        ManagedOrderAction.Protection => RecoveryActionKind.ProtectOwnedPosition,
        ManagedOrderAction.EmergencyClose => RecoveryActionKind.EmergencyReduceOnlyClose,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    internal static bool IsValid(ManagedOrderState? state) => state is not null
        && !string.IsNullOrWhiteSpace(state.IntentId)
        && !string.IsNullOrWhiteSpace(state.Venue)
        && !string.IsNullOrWhiteSpace(state.Account)
        && !string.IsNullOrWhiteSpace(state.Asset)
        && !string.IsNullOrWhiteSpace(state.Contract)
        && Enum.IsDefined(state.Direction)
        && Enum.IsDefined(state.EntryTerminalState)
        && Enum.IsDefined(state.EmergencyCloseRetryability)
        && state.EntryType is "Market" or "Limit"
        && state.Quantity > 0m
        && (state.EntryType == "Market" ? state.LimitPrice is null : state.LimitPrice is > 0m)
        && state.TakeProfit > 0m && state.StopLoss > 0m
        && Enum.IsDefined(state.EligibilityStatus)
        && state.EligibilityStatus == EntryEligibilityStatus.ScenarioReady
        && Enum.IsDefined(state.Status)
        && (state.PendingAction is null || Enum.IsDefined(state.PendingAction.Value))
        && state.EmergencyCloseRetriesUsed >= 0
        && (!state.EmergencyCloseNoticePending || state.EmergencyCloseNoticeSent)
        && state.FilledQuantity >= 0m && state.FilledQuantity <= state.Quantity
        && state.RemainingQuantity >= 0m && state.RemainingQuantity <= state.Quantity
        && (state.EntryTerminalState switch
        {
            ManagedEntryTerminalState.NotSubmitted => state.EntryOrderId is null
                && state.FilledQuantity == 0m && state.RemainingQuantity == state.Quantity,
            ManagedEntryTerminalState.Open => state.EntryOrderId is not null
                && state.RemainingQuantity > 0m
                && state.FilledQuantity + state.RemainingQuantity == state.Quantity,
            ManagedEntryTerminalState.Cancelled => state.EntryOrderId is not null
                && state.RemainingQuantity == 0m && state.FilledQuantity < state.Quantity,
            ManagedEntryTerminalState.Filled => state.EntryOrderId is not null
                && state.RemainingQuantity == 0m && state.FilledQuantity == state.Quantity,
            ManagedEntryTerminalState.Rejected => state.EntryOrderId is null
                && state.FilledQuantity == 0m && state.RemainingQuantity == 0m,
            _ => false
        })
        && state.PositionQuantity >= 0m && state.PositionQuantity <= state.Quantity
        && state.TakeProfitCoveredQuantity >= 0m && state.StopLossCoveredQuantity >= 0m
        && state.TakeProfitClosedQuantity >= 0m && state.StopLossClosedQuantity >= 0m
        && (state.LastFailedProtectionQuantity is null || state.LastFailedProtectionQuantity >= 0m)
        && state.Revision >= 0
        && ((state.PendingOperationId is null) == (state.PendingAction is null))
        && (state.PendingOperationId is null || state.Status == ManagedOrderStatus.ReconciliationRequired)
        && ((state.PendingOperationId is null) == (state.PendingPriorStatus is null))
        && (state.PendingQuantity is null || state.PendingQuantity >= 0m)
        && (state.PendingOperationId is null || state.PendingQuantity is > 0m)
        && (state.PendingOperationId is not null || state.PendingQuantity is null)
        && (state.NextEmergencyCloseAt is null || state.Status == ManagedOrderStatus.EmergencyCloseWaiting
            || state.Status == ManagedOrderStatus.ReconciliationRequired
                && state.PendingAction == ManagedOrderAction.EmergencyClose);

    internal static bool BlocksNewEntry(ManagedOrderState state) => state.Status is not
        (ManagedOrderStatus.EntryRejected or ManagedOrderStatus.Cancelled or ManagedOrderStatus.Closed);

    internal static bool RequiresVenueEntryPause(ManagedOrderState state) => state.Status is
        ManagedOrderStatus.PositionUnprotected or ManagedOrderStatus.EmergencyCloseWaiting
        or ManagedOrderStatus.EmergencyClosePaused
        || state.PendingAction is ManagedOrderAction.Protection or ManagedOrderAction.EmergencyClose;
}

internal sealed record ManagedOrderIntent(
    string Venue,
    string Account,
    string Asset,
    string Contract,
    PositionDirection Direction,
    string EntryType,
    decimal Quantity,
    decimal? LimitPrice,
    decimal TakeProfit,
    decimal StopLoss);

internal interface IManagedOrderTestAdapter : IRecoverySnapshotSource
{
    Task<RecoveryOperationOutcome> SubmitEntryAsync(
        ManagedOrderState intent,
        string operationId,
        CancellationToken cancellationToken);

    Task<RecoveryOperationOutcome> CancelRemainderAsync(
        ManagedOrderState intent,
        string orderId,
        string operationId,
        CancellationToken cancellationToken);

    Task<RecoveryOperationOutcome> SetProtectionAsync(
        ManagedOrderState intent,
        string positionId,
        decimal actualQuantity,
        string operationId,
        CancellationToken cancellationToken);

    Task<RecoveryOperationOutcome> CloseReduceOnlyAsync(
        ManagedOrderState intent,
        string positionId,
        PositionDirection positionDirection,
        decimal exactQuantity,
        string operationId,
        CancellationToken cancellationToken);
}

internal sealed record OrderLifecycleResult(bool Succeeded, string Status, string? IntentId = null);

internal sealed class OrderLifecycleCoordinator
{
    private readonly RecoveryCoordinator _recovery;
    private readonly IManagedOrderTestAdapter _adapter;
    private readonly NotificationDispatcher? _notifications;
    private readonly IRecoveryClock _clock;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _workflowGate = new(1, 1);

    internal OrderLifecycleCoordinator(
        RecoveryCoordinator recovery,
        IManagedOrderTestAdapter adapter,
        AppSettings settings,
        IRecoveryClock clock,
        NotificationDispatcher? notifications = null)
    {
        _recovery = recovery;
        _adapter = adapter;
        _settings = settings.Copy();
        _clock = clock;
        _notifications = notifications;
    }

    internal IReadOnlyList<ManagedOrderState> Orders => _recovery.State.ManagedOrders;

    internal Task<OrderLifecycleResult> OpenAsync(
        ManagedOrderIntent intent,
        EntryEligibilityInput eligibilityInput,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default) =>
        ExecuteSeriallyAsync(cancellationToken,
            () => OpenCoreAsync(intent, eligibilityInput, savedOperatingIntent, cancellationToken));

    private async Task<OrderLifecycleResult> OpenCoreAsync(
        ManagedOrderIntent intent,
        EntryEligibilityInput eligibilityInput,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        if (!savedOperatingIntent || !eligibilityInput.Settings.SavedOperatingIntent
            || !_recovery.IsEntryAllowed(intent.Venue, savedOperatingIntent))
            return Blocked("Новые входы остановлены или ожидают сверки.");

        if (_recovery.State.ManagedOrders.Any(order => order.Venue == intent.Venue
                && order.Account == intent.Account && order.Contract == intent.Contract
                && ManagedOrderState.BlocksNewEntry(order)))
            return Blocked("Для этого контракта уже есть незавершённое намерение.");

        var eligibility = EntryEligibilityEvaluator.Evaluate(eligibilityInput);
        if (!MatchesEligibility(intent, eligibilityInput.Setup, eligibility))
            return Blocked("Результат допуска 007 не подтверждает точный рынок и условия входа.");

        var state = new ManagedOrderState(Guid.NewGuid().ToString("N"), intent.Venue, intent.Account,
            intent.Asset, intent.Contract, intent.Direction, intent.EntryType, intent.Quantity, intent.LimitPrice,
            intent.TakeProfit, intent.StopLoss, eligibility.Status, eligibility.EvaluatedAt, now,
            RemainingQuantity: intent.Quantity);
        var savedIntent = await _recovery.SaveManagedOrderAsync(state, cancellationToken).ConfigureAwait(false);
        if (savedIntent is null)
            return Blocked("Намерение не удалось сохранить в журнале восстановления.");
        state = savedIntent;

        var preflight = await ReadFreshAsync(intent.Venue, intent.Account, cancellationToken).ConfigureAwait(false);
        if (preflight?.Account is not { } preflightAccount)
        {
            await _recovery.ReportFailureAsync(intent.Venue, intent.Account,
                "Для проверки допуска нет свежего точного снимка счёта.", _settings, cancellationToken)
                .ConfigureAwait(false);
            return new(false, "Для входа нет свежего точного снимка счёта.", state.IntentId);
        }

        if (!TryEvaluateEligibility(intent, eligibilityInput, preflightAccount, now, out var freshEligibility)
            || freshEligibility is null)
        {
            await _recovery.ReportFailureAsync(intent.Venue, intent.Account,
                "Данные допуска не совпадают со свежим точным снимком.", _settings, cancellationToken)
                .ConfigureAwait(false);
            return new(false, "Результат допуска 007 не подтверждает точный рынок и условия входа.", state.IntentId);
        }
        eligibility = freshEligibility;

        var actionEligibilityMatches = false;
        var result = await RunActionAsync(state, ManagedOrderAction.Entry, RecoveryActionKind.Entry,
            "entry", "PlaceOpeningOrder", savedOperatingIntent,
            read => TryEvaluateEligibility(intent, eligibilityInput, read.Account!, _clock.UtcNow,
                    out var actionEligibility)
                && actionEligibility is not null
                && actionEligibility.Status == eligibility.Status
                && actionEligibility.SelectedPool?.Quantity == eligibility.SelectedPool?.Quantity
                && !read.Account!.Positions.Value!.Any(position => position.Contract == state.Contract)
                && !read.Account.Orders.Value!.Any(order => order.Contract == state.Contract),
            (_, operationId) => _adapter.SubmitEntryAsync(state, operationId, cancellationToken),
            cancellationToken,
            read =>
            {
                actionEligibilityMatches = TryEvaluateEligibility(intent, eligibilityInput, read.Account!,
                    _clock.UtcNow, out var actionEligibility)
                    && actionEligibility is not null
                    && actionEligibility.Status == eligibility.Status
                    && actionEligibility.SelectedPool?.Quantity == eligibility.SelectedPool?.Quantity;
                return state with
                {
                    EligibilityEvaluatedAt = actionEligibilityMatches
                        ? actionEligibility!.EvaluatedAt : state.EligibilityEvaluatedAt
                };
            }).ConfigureAwait(false);
        if (!result.Succeeded || result.State is null)
        {
            if (!actionEligibilityMatches)
                await _recovery.ReportFailureAsync(state.Venue, state.Account,
                    "Свежая проверка допуска изменилась до передачи входа.", _settings, cancellationToken)
                    .ConfigureAwait(false);
            return new(false, result.Status, state.IntentId);
        }
        if (result.Outcome == RecoveryOperationOutcome.NotExecuted)
            return new(false, "Вход подтверждённо отклонён.", state.IntentId);

        var position = FindOwnedPosition(result.Read!.Account!, result.State, out _);
        if (position is not null)
        {
            await NotifyAsync(result.State.IntentId + ":opened", NotificationCategory.Opening,
                "Открытие подтверждено; состояние позиции сверено.", cancellationToken).ConfigureAwait(false);
            if (!ProtectionComplete(result.Read.Account!, result.State, position))
                return await RepairProtectionCoreAsync(result.State, savedOperatingIntent: false, cancellationToken)
                    .ConfigureAwait(false);
        }
        return new(true, position is null ? "Заявка сверена." : "Вход и защита сверены.", state.IntentId);
    }

    internal Task<OrderLifecycleResult> CancelRemainderAsync(
        string intentId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default) =>
        ExecuteSeriallyAsync(cancellationToken,
            () => CancelRemainderCoreAsync(intentId, savedOperatingIntent, cancellationToken));

    private async Task<OrderLifecycleResult> CancelRemainderCoreAsync(
        string intentId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken,
        bool repairAfterCancel = true)
    {
        var state = Find(intentId);
        if (state is null || state.EntryOrderId is null)
            return Blocked("Точная заявка приложения не найдена.", intentId);
        var result = await RunActionAsync(state, ManagedOrderAction.CancelRemainder,
            RecoveryActionKind.CancelOwnedOrder, "cancel", "CancelOpeningRemainder", savedOperatingIntent,
            before => TryFindOwnedOrder(state, before.Account!, out var order) && order?.RemainingQuantity > 0m,
            (_, operationId) => _adapter.CancelRemainderAsync(state, state.EntryOrderId!, operationId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.State is null)
            return new(false, result.Status, intentId);

        var updated = result.State;
        if (updated.EntryTerminalState == ManagedEntryTerminalState.Open)
        {
            var currentPosition = result.Read is null ? null
                : FindOwnedPosition(result.Read.Account!, updated, out _);
            if (repairAfterCancel && currentPosition is not null
                && !ProtectionComplete(result.Read!.Account!, updated, currentPosition))
            {
                var repaired = await RepairProtectionCoreAsync(updated with
                    { Status = ManagedOrderStatus.PositionUnprotected }, savedOperatingIntent: false,
                    cancellationToken).ConfigureAwait(false);
                return new(false, repaired.Succeeded
                    ? "Отмена отклонена; фактическая позиция получила защиту."
                    : repaired.Status, intentId);
            }
            return new(false, "Отмена подтверждённо отклонена; точный остаток сохранён.", intentId);
        }
        if (updated.EntryTerminalState == ManagedEntryTerminalState.Filled)
        {
            var position = FindOwnedPosition(result.Read!.Account!, updated, out _);
            await NotifyAsync(updated.IntentId + ":filled", NotificationCategory.OrderChange,
                "Остаток заявки исполнился до отмены; полный объём сверён.", cancellationToken).ConfigureAwait(false);
            if (position is not null && !ProtectionComplete(result.Read.Account!, updated, position))
                return await RepairProtectionCoreAsync(updated with { Status = ManagedOrderStatus.PositionUnprotected },
                    savedOperatingIntent: false, cancellationToken).ConfigureAwait(false);
            return new(true, "Заявка полностью исполнилась во время отмены; объём позиции сверен.", intentId);
        }
        if (updated.EntryTerminalState != ManagedEntryTerminalState.Cancelled)
            return await BlockAndReconcileAsync(updated, "Снимок не различил отмену и полное исполнение заявки.",
                cancellationToken).ConfigureAwait(false);

        await NotifyAsync(updated.IntentId + ":cancelled", NotificationCategory.OrderChange,
            "Остаток заявки отменён и сверён.", cancellationToken).ConfigureAwait(false);

        var remainingPosition = FindOwnedPosition(result.Read!.Account!, updated, out _);
        if (repairAfterCancel && remainingPosition is not null
            && !ProtectionComplete(result.Read.Account!, updated, remainingPosition))
            return await RepairProtectionCoreAsync(updated with { Status = ManagedOrderStatus.PositionUnprotected },
                savedOperatingIntent: false, cancellationToken).ConfigureAwait(false);
        return new(true, remainingPosition is null
            ? "Остаток отменён; частично исполненная позиция по TP/SL закрыта и подтверждена."
            : "Остаток отменён и сверён.", intentId);
    }

    internal Task<OrderLifecycleResult> RepairProtectionAsync(
        string intentId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default) =>
        ExecuteSeriallyAsync(cancellationToken,
            () => RepairProtectionCoreByIdAsync(intentId, savedOperatingIntent, cancellationToken));

    private async Task<OrderLifecycleResult> RepairProtectionCoreByIdAsync(
        string intentId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken)
    {
        var state = Find(intentId);
        return state is null
            ? Blocked("Намерение не найдено.", intentId)
            : await RepairProtectionCoreAsync(state, savedOperatingIntent, cancellationToken).ConfigureAwait(false);
    }

    internal Task<OrderLifecycleResult> RunDueEmergencyCloseAsync(
        string intentId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default) =>
        ExecuteSeriallyAsync(cancellationToken,
            () => RunDueEmergencyCloseCoreAsync(intentId, savedOperatingIntent, cancellationToken));

    private async Task<OrderLifecycleResult> RunDueEmergencyCloseCoreAsync(
        string intentId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken)
    {
        var state = Find(intentId);
        if (state is { EmergencyCloseNoticePending: true })
            state = await EnsureEmergencyCloseNoticeAsync(state, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Status != ManagedOrderStatus.EmergencyCloseWaiting
            || state.NextEmergencyCloseAt is not { } due || due > _clock.UtcNow)
            return Blocked("Аварийное закрытие ещё не наступило или не запланировано.", intentId);

        var read = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
        if (read is null || !TryReadStateProgress(state, read, out var progress))
            return await BlockAndReconcileAsync(state, "Для аварийного закрытия нет свежих точных данных.",
                cancellationToken).ConfigureAwait(false);
        var refreshedState = await SaveAsync(ApplyFillProgress(state, progress), cancellationToken)
            .ConfigureAwait(false);
        if (refreshedState is null)
            return Blocked("Свежий объём не сохранился до аварийного действия.", intentId);
        state = refreshedState;
        if (progress.EntryRemainder > 0m || state.EntryTerminalState == ManagedEntryTerminalState.Open)
        {
            var cancelled = await CancelRemainderCoreAsync(state.IntentId, savedOperatingIntent: false,
                cancellationToken, repairAfterCancel: false).ConfigureAwait(false);
            state = Find(intentId)!;
            if (!cancelled.Succeeded || state.EntryTerminalState == ManagedEntryTerminalState.Open
                || state.RemainingQuantity > 0m)
                return await BlockAndReconcileAsync(state,
                    "Аварийное закрытие остановлено: открывающий остаток не подтверждённо отменён.", cancellationToken)
                    .ConfigureAwait(false);
            read = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
            if (read is null || !TryReadStateProgress(state, read, out progress))
                return await BlockAndReconcileAsync(state,
                    "После отмены остатка нет свежего подтверждения позиции.", cancellationToken).ConfigureAwait(false);
            refreshedState = await SaveAsync(ApplyFillProgress(state, progress), cancellationToken)
                .ConfigureAwait(false);
            if (refreshedState is null)
                return Blocked("Фактический объём после отмены не сохранился.", intentId);
            state = refreshedState;
        }

        if (progress.Position is null)
        {
            var closed = await SaveAsync(state with { Status = ManagedOrderStatus.Closed }, cancellationToken)
                .ConfigureAwait(false);
            return closed is null
                ? Blocked("Плоская позиция не сохранилась в журнале.", intentId)
                : new(true, "Позиция уже закрыта по проверенным данным.", intentId);
        }
        if (ProtectionComplete(read.Account!, state, progress.Position))
        {
            var protectedState = await SaveAsync(state with
            {
                Status = ManagedOrderStatus.Protected,
                NextEmergencyCloseAt = null,
                TakeProfitOrderId = progress.Protection!.TakeProfitOrderId,
                StopLossOrderId = progress.Protection.StopLossOrderId,
                TakeProfitCoveredQuantity = progress.Protection.TakeProfitCoveredQuantity!.Value,
                StopLossCoveredQuantity = progress.Protection.StopLossCoveredQuantity!.Value
            }, cancellationToken).ConfigureAwait(false);
            return protectedState is null
                ? Blocked("Подтверждённая защита не сохранилась.", intentId)
                : new(true, "Текущая позиция уже полностью защищена.", intentId);
        }
        if (state.EmergencyCloseRetriesUsed > 0
            && state.EmergencyCloseRetryability != RecoveryRetryability.Retryable)
            return await BlockAndReconcileAsync(state,
                "Для повтора аварийного закрытия нет подтверждённой временной причины.", cancellationToken)
                .ConfigureAwait(false);

        var result = await RunActionAsync(state, ManagedOrderAction.EmergencyClose,
            RecoveryActionKind.EmergencyReduceOnlyClose, "close", "ReduceOnlyClose", savedOperatingIntent,
            before => FindOwnedPosition(before.Account!, state, out _) is { } current
                && current.Quantity == progress.CurrentPosition
                && state.RemainingQuantity == 0m
                && state.EntryTerminalState != ManagedEntryTerminalState.Open
                && HasNoOtherContractOrders(before.Account!, state),
            (before, operationId) =>
            {
                var current = FindOwnedPosition(before.Account!, state, out _)!;
                return _adapter.CloseReduceOnlyAsync(state, current.PositionId!, current.Direction,
                    current.Quantity!.Value, operationId, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.State is null)
            return new(false, result.Status, intentId);

        if (result.State.PositionQuantity == 0m)
        {
            await NotifyAsync(result.State.IntentId + ":closed", NotificationCategory.Closing,
                "Закрытие подтверждено и сверено.", cancellationToken).ConfigureAwait(false);
            return new(true, "Позиция закрыта и сверена.", intentId);
        }
        if (result.Outcome == RecoveryOperationOutcome.NotExecuted)
            return await ApplyEmergencyCloseFailureCoreAsync(result.State,
                result.Observation?.Retryability ?? RecoveryRetryability.Unknown, cancellationToken)
                .ConfigureAwait(false);
        if (result.State.Status == ManagedOrderStatus.Protected)
            return new(true, "Остаток после закрытия сверён и полностью защищён.", intentId);
        return await RepairProtectionCoreAsync(result.State, savedOperatingIntent: false, cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<OrderLifecycleResult> ReconcilePendingAsync(
        string intentId,
        CancellationToken cancellationToken = default) =>
        ExecuteSeriallyAsync(cancellationToken,
            () => ReconcilePendingCoreAsync(intentId, cancellationToken));

    private async Task<OrderLifecycleResult> ReconcilePendingCoreAsync(
        string intentId,
        CancellationToken cancellationToken)
    {
        var state = Find(intentId);
        if (state is null || state.PendingOperationId is null || state.PendingAction is null)
            return Blocked("Нет сохранённого действия для сверки.", intentId);

        var operation = _recovery.State.Operations.SingleOrDefault(candidate =>
            candidate.OperationId == state.PendingOperationId);
        if (operation is null)
            return await BlockAndReconcileAsync(state, "Журнал не содержит связанного действия.", cancellationToken)
                .ConfigureAwait(false);
        if (operation.Stage == RecoveryOperationStage.Prepared)
        {
            if (!await _recovery.AbandonPreparedOperationAsync(operation.OperationId, cancellationToken)
                    .ConfigureAwait(false))
                return Blocked("Подготовленное действие не удалось безопасно отбросить.", intentId);
            operation = _recovery.State.Operations.Single(candidate => candidate.OperationId == operation.OperationId);
        }
        if (operation.Stage is RecoveryOperationStage.Unknown or RecoveryOperationStage.TransferStarted)
        {
            var reconciled = await _recovery.CheckDueAsync(state.Venue, cancellationToken).ConfigureAwait(false);
            if (!reconciled)
                return Blocked("Сверка ожидает срока чтения 008.", intentId);
            operation = _recovery.State.Operations.Single(candidate => candidate.OperationId == state.PendingOperationId);
            if (operation.Stage != RecoveryOperationStage.Reconciled)
                return Blocked("Исход действия пока не установлен.", intentId);
        }
        if (operation.Stage is RecoveryOperationStage.Prepared or RecoveryOperationStage.TransferStarted
            || operation.Stage == RecoveryOperationStage.Unknown)
            return Blocked("Сверка не разрешила новое действие.", intentId);
        if (operation.Stage == RecoveryOperationStage.Abandoned)
        {
            var abandoned = state with
            {
                PendingOperationId = null,
                PendingAction = null,
                PendingQuantity = null,
                Status = state.PendingPriorStatus ?? ManagedOrderStatus.IntentSaved,
                PendingPriorStatus = null,
                LastFailure = "Процесс перезапустился до участка передачи."
            };
            var savedAbandoned = await SaveAsync(abandoned, cancellationToken).ConfigureAwait(false);
            return savedAbandoned is not null
                ? new(false, "Непереданное действие отброшено; сохранённый срок восстановления сохранён.", intentId)
                : Blocked("Непереданное действие не удалось сохранить.", intentId);
        }

        var read = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
        if (read is null || !TryGetObservation(read, operation, out var observation))
            return Blocked("После чтения не хватает точного подтверждения операции.", intentId);
        if (observation!.Outcome.ToString() != operation.Outcome
            || observation.Retryability != operation.Retryability)
            return await BlockAndReconcileAsync(state,
                "Результат чтения или причина повтора не совпадает с журналом.", cancellationToken)
                .ConfigureAwait(false);

        var applied = await ApplyReconciledAsync(state, operation, read, observation!, cancellationToken)
            .ConfigureAwait(false);
        return applied;
    }

    private async Task<OrderLifecycleResult> RepairProtectionCoreAsync(
        ManagedOrderState state,
        bool savedOperatingIntent,
        CancellationToken cancellationToken)
    {
        if (state.Status is ManagedOrderStatus.EntryRejected or ManagedOrderStatus.Cancelled
            or ManagedOrderStatus.Closed)
            return Blocked("Для завершённого намерения нет открытой позиции для ремонта защиты.", state.IntentId);
        if (state.PendingOperationId is not null)
            return Blocked("Предыдущее действие ожидает сверки.", state.IntentId);
        if (state.Status == ManagedOrderStatus.EmergencyCloseWaiting)
            return Blocked("Защита уже отклонена; действует сохранённый срок безопасного закрытия.", state.IntentId);
        if (state.Status == ManagedOrderStatus.EmergencyClosePaused)
            return Blocked("Аварийное закрытие требует ручного восстановления.", state.IntentId);

        var read = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
        if (read is null || !TryReadStateProgress(state, read, out var progress))
        {
            var ambiguous = await SaveAndPauseAsync(state with
            {
                Status = ManagedOrderStatus.OwnershipAmbiguous,
                LastFailure = "Свежий снимок не подтвердил точный владелец или объём позиции."
            }, "Свежий снимок не подтвердил точного владельца позиции.", cancellationToken)
                .ConfigureAwait(false);
            return ambiguous is null
                ? Blocked("Не удалось сохранить неоднозначное состояние позиции.", state.IntentId)
                : Blocked("Владение позицией неоднозначно; изменение заблокировано.", state.IntentId);
        }

        state = ApplyFillProgress(state, progress);
        if (progress.Position is null)
        {
            if (progress.Filled > 0m && progress.EntryRemainder == 0m)
            {
                var closed = await SaveAsync(state with { Status = ManagedOrderStatus.Closed }, cancellationToken)
                    .ConfigureAwait(false);
                return closed is null
                    ? Blocked("Подтверждённое закрытие не сохранилось в журнале.", state.IntentId)
                    : new(true, "Позиция закрыта по проверенным TP/SL.", state.IntentId);
            }
            var savedNoPosition = await SaveAsync(state with { Status = ManagedOrderStatus.EntryOpen }, cancellationToken)
                .ConfigureAwait(false);
            return savedNoPosition is null
                ? Blocked("Состояние заявки без позиции не сохранилось.", state.IntentId)
                : Blocked("Открытой позиции для ремонта защиты нет.", state.IntentId);
        }

        var position = progress.Position;
        if (ProtectionComplete(read.Account!, state, position))
        {
            var protectedState = await SaveAsync(state with
            {
                Status = ManagedOrderStatus.Protected,
                PositionQuantity = position.Quantity!.Value,
                TakeProfitOrderId = progress.Protection!.TakeProfitOrderId,
                StopLossOrderId = progress.Protection.StopLossOrderId,
                TakeProfitCoveredQuantity = progress.Protection.TakeProfitCoveredQuantity!.Value,
                StopLossCoveredQuantity = progress.Protection.StopLossCoveredQuantity!.Value,
                LastFailedProtectionQuantity = null
            }, cancellationToken).ConfigureAwait(false);
            return protectedState is null
                ? Blocked("Подтверждённая защита не сохранилась в журнале.", state.IntentId)
                : new(true, "TP и SL уже покрывают весь фактический объём.", state.IntentId);
        }

        if (state.LastFailedProtectionQuantity == position.Quantity)
            return await HandleProtectionRejectedCoreAsync(state, position.Quantity!.Value, read, cancellationToken)
                .ConfigureAwait(false);

        var updated = state with
        {
            Status = ManagedOrderStatus.PositionUnprotected,
            PositionQuantity = position.Quantity!.Value
        };
        var result = await RunActionAsync(updated, ManagedOrderAction.Protection,
            RecoveryActionKind.ProtectOwnedPosition, "protect", "SetFullPositionProtection", savedOperatingIntent,
            before => FindOwnedPosition(before.Account!, updated, out _) is { } current
                && HasNoOtherContractOrders(before.Account!, updated)
                && !ProtectionComplete(before.Account!, updated, current),
            (before, operationId) =>
            {
                var current = FindOwnedPosition(before.Account!, updated, out _)!;
                return _adapter.SetProtectionAsync(updated, current.PositionId!, current.Quantity!.Value,
                    operationId, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.State is null)
            return new(false, result.Status, state.IntentId);

        if (result.State.Status == ManagedOrderStatus.Protected)
        {
            await NotifyAsync(state.IntentId + ":protected", NotificationCategory.Opening,
                "TP и SL подтверждены на весь объём позиции.", cancellationToken).ConfigureAwait(false);
            return new(true, "TP и SL подтверждены на весь объём.", state.IntentId);
        }

        if (result.Outcome == RecoveryOperationOutcome.NotExecuted)
            return await HandleProtectionRejectedCoreAsync(result.State,
                result.State.PendingQuantity ?? updated.PositionQuantity, result.Read!, cancellationToken)
                .ConfigureAwait(false);
        return Blocked("Защита позиции не подтверждена; новые действия остановлены.", state.IntentId);
    }

    private async Task<ActionResult> RunActionAsync(
        ManagedOrderState state,
        ManagedOrderAction action,
        RecoveryActionKind recoveryAction,
        string operationSuffix,
        string operationName,
        bool savedOperatingIntent,
        Func<RecoverySnapshotRead, bool> precondition,
        Func<RecoverySnapshotRead, string, Task<RecoveryOperationOutcome>> send,
        CancellationToken cancellationToken,
        Func<RecoverySnapshotRead, ManagedOrderState>? refreshState = null)
    {
        if (!_recovery.IsActionAllowed(state.Venue, state.Account, savedOperatingIntent,
                recoveryAction, state.Contract))
            return new(false, "Площадка или журнал ожидает сверки.", state, null, null);
        var latestState = Find(state.IntentId);
        if (state.PendingOperationId is not null || latestState is null
            || latestState.Revision != state.Revision || latestState.PendingOperationId is not null)
            return new(false, "Предыдущее действие ещё ожидает сверки.", state, null, null);

        var before = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            await BlockAndReconcileAsync(state, "Для изменения нет свежего полного снимка.", cancellationToken)
                .ConfigureAwait(false);
            return new(false, "Для изменения нет свежего полного снимка.", state, null, null);
        }
        if (!precondition(before))
        {
            var ambiguous = action switch
            {
                ManagedOrderAction.Entry => before.Account!.Positions.Value!.Any(position => position.Contract == state.Contract)
                    || before.Account.Orders.Value!.Any(order => order.Contract == state.Contract),
                ManagedOrderAction.CancelRemainder => state.EntryOrderId is not null,
                ManagedOrderAction.Protection or ManagedOrderAction.EmergencyClose =>
                    before.Account!.Positions.Value!.Any(position => position.Contract == state.Contract),
                _ => false
            };
            if (ambiguous)
                await SaveAndPauseAsync(state with
                    {
                        Status = ManagedOrderStatus.OwnershipAmbiguous,
                        LastFailure = "Снимок содержит цель с неясным или смешанным владельцем."
                    }, "Снимок содержит цель с неясным или смешанным владельцем.", cancellationToken)
                    .ConfigureAwait(false);
            return new(false, "Точное владение и состояние цели не подтверждены.", state, before, null);
        }

        if (refreshState is not null)
        {
            var refreshed = await SaveAsync(refreshState(before), cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
                return new(false, "Обновлённое намерение не сохранилось; адаптер не вызван.", state, before, null);
            state = refreshed;
        }

        var operationId = state.IntentId + ":" + operationSuffix + ":" + Guid.NewGuid().ToString("N");
        var request = new RecoveryOperationRequest(operationId, state.Venue, state.Account, state.Contract,
            operationName, ActionKind: recoveryAction);
        if (!await _recovery.PrepareOperationAsync(request, savedOperatingIntent, cancellationToken)
                .ConfigureAwait(false))
            return new(false, "Действие не прошло проверку допуска.", state, before, null);

        var pending = state with
        {
            Status = ManagedOrderStatus.ReconciliationRequired,
            PendingOperationId = operationId,
            PendingAction = action,
            PendingQuantity = action switch
            {
                ManagedOrderAction.Entry => state.Quantity,
                ManagedOrderAction.CancelRemainder => before.Account!.Orders.Value!
                    .SingleOrDefault(order => order.OrderId == state.EntryOrderId)?.RemainingQuantity,
                ManagedOrderAction.Protection => FindOwnedPosition(before.Account!, state, out _)?.Quantity,
                ManagedOrderAction.EmergencyClose => FindOwnedPosition(before.Account!, state, out _)?.Quantity,
                _ => null
            },
            PendingPriorStatus = state.Status
        };
        var savedPending = await SaveAsync(pending, cancellationToken).ConfigureAwait(false);
        if (savedPending is null)
        {
            await _recovery.AbandonPreparedOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            return new(false, "Участок передачи не удалось сохранить; адаптер не вызван.", state, before, null);
        }
        pending = savedPending;

        bool transferStarted;
        try
        {
            transferStarted = await _recovery.BeginTransferAsync(operationId, savedOperatingIntent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            transferStarted = false;
        }
        if (!transferStarted)
        {
            await _recovery.AbandonPreparedOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            await ClearPendingAsync(state.IntentId, operationId, CancellationToken.None).ConfigureAwait(false);
            return new(false, "Участок передачи не начался; состояние восстановлено без вызова адаптера.",
                state, before, null);
        }

        if (!IsFresh(before, state.Venue, state.Account, _clock.UtcNow, _adapter.MaximumSnapshotAge))
        {
            await _recovery.AbortTransferBeforeCallbackAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            await ClearPendingAsync(state.IntentId, operationId, CancellationToken.None).ConfigureAwait(false);
            await _recovery.ReportFailureAsync(state.Venue, state.Account,
                "Срок снимка истёк до вызова адаптера.", _settings, CancellationToken.None).ConfigureAwait(false);
            return new(false, "Срок снимка истёк; действие ожидает нового чтения.", state, before, null);
        }

        RecoveryOperationOutcome sent;
        try
        {
            sent = await send(before, operationId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await MarkUnknownAsync(pending, operationId, "Передача была прервана.", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await MarkUnknownAsync(pending, operationId, "Адаптер не вернул подтверждённый исход.",
                cancellationToken).ConfigureAwait(false);
        }
        if (sent == RecoveryOperationOutcome.Unknown)
            return await MarkUnknownAsync(pending, operationId, "Исход действия неизвестен.", cancellationToken)
                .ConfigureAwait(false);

        var read = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
        var operation = _recovery.State.Operations.Single(candidate => candidate.OperationId == operationId);
        if (read is null || !TryGetObservation(read, operation, out var observation)
            || observation!.Outcome != sent || !TryVerifyActionEvidence(pending, action, read, observation!))
            return await MarkUnknownAsync(pending, operationId,
                "Ответ нельзя подтвердить свежим точным снимком.", cancellationToken).ConfigureAwait(false);

        var outcome = sent == RecoveryOperationOutcome.Confirmed
            ? RecoveryTransferOutcome.Confirmed
            : RecoveryTransferOutcome.NotExecuted;
        if (!await _recovery.CompleteTransferAsync(operationId, outcome, _settings,
                cancellationToken: cancellationToken,
                retryability: sent == RecoveryOperationOutcome.NotExecuted
                    ? observation.Retryability : RecoveryRetryability.Unknown)
                .ConfigureAwait(false))
            return new(false, "Исход действия не сохранился в журнале.", pending, read, sent);

        var updated = ApplyObservation(pending, action, read, observation) with
        {
            PendingOperationId = null,
            PendingAction = null,
            PendingQuantity = null,
            PendingPriorStatus = null,
            LastAppliedOperationId = operationId
        };
        var savedUpdated = await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (savedUpdated is null)
            return new(false, "Исход записан, но состояние намерения не сохранилось.", pending, read, sent);
        return new(true, "Действие подтверждено свежим снимком.", savedUpdated, read, sent, observation);
    }

    private async Task<ActionResult> MarkUnknownAsync(
        ManagedOrderState pending,
        string operationId,
        string detail,
        CancellationToken cancellationToken)
    {
        var completed = await _recovery.CompleteTransferAsync(operationId, RecoveryTransferOutcome.Unknown,
            _settings, detail, CancellationToken.None).ConfigureAwait(false);
        if (!completed)
            return new(false, "Исход неизвестен, журнал недоступен; повторная передача заблокирована.", pending, null, null);
        var updated = pending with { LastFailure = detail };
        var saved = await SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
        return new(false, "Исход неизвестен; разрешена только сверка.", saved ?? pending, null, null);
    }

    private async Task<OrderLifecycleResult> ApplyReconciledAsync(
        ManagedOrderState state,
        RecoveryOperation operation,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation,
        CancellationToken cancellationToken)
    {
        var action = state.PendingAction!.Value;
        if (!TryVerifyActionEvidence(state, action, read, observation)
            || operation.Retryability != observation.Retryability)
            return await BlockAndReconcileAsync(state,
                "Снимок не подтверждает точное состояние, владельца или причину исхода действия.",
                cancellationToken).ConfigureAwait(false);
        var updated = ApplyObservation(state, action, read, observation) with
        {
            PendingOperationId = null,
            PendingAction = null,
            PendingQuantity = null,
            PendingPriorStatus = null,
            LastAppliedOperationId = operation.OperationId
        };
        var saved = await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (saved is null)
            return Blocked("Сверенный исход не сохранился.", state.IntentId);
        updated = saved;

        if (operation.Outcome == nameof(RecoveryOperationOutcome.Confirmed))
        {
            if (action == ManagedOrderAction.Entry && updated.FilledQuantity > 0m)
                await NotifyAsync(updated.IntentId + ":opened", NotificationCategory.Opening,
                    "Открытие подтверждено; состояние позиции сверено.", cancellationToken).ConfigureAwait(false);
            else if (action == ManagedOrderAction.CancelRemainder
                && updated.EntryTerminalState == ManagedEntryTerminalState.Cancelled)
                await NotifyAsync(updated.IntentId + ":cancelled", NotificationCategory.OrderChange,
                    "Остаток заявки отменён и сверён.", cancellationToken).ConfigureAwait(false);
            else if (action == ManagedOrderAction.Protection && updated.Status == ManagedOrderStatus.Protected)
                await NotifyAsync(updated.IntentId + ":protected", NotificationCategory.Opening,
                    "TP и SL подтверждены на весь объём позиции.", cancellationToken).ConfigureAwait(false);
            else if (action == ManagedOrderAction.EmergencyClose && updated.Status == ManagedOrderStatus.Closed)
                await NotifyAsync(updated.IntentId + ":closed", NotificationCategory.Closing,
                    "Закрытие подтверждено и сверено.", cancellationToken).ConfigureAwait(false);
        }

        if (action == ManagedOrderAction.CancelRemainder
            && updated.EntryTerminalState == ManagedEntryTerminalState.Filled)
            await NotifyAsync(updated.IntentId + ":filled", NotificationCategory.OrderChange,
                "Остаток заявки исполнился до отмены; полный объём сверён.", cancellationToken).ConfigureAwait(false);

        if (action == ManagedOrderAction.Protection
            && operation.Outcome == nameof(RecoveryOperationOutcome.NotExecuted))
            return await HandleProtectionRejectedCoreAsync(updated,
                state.PendingQuantity ?? state.PositionQuantity, read, cancellationToken).ConfigureAwait(false);

        if (action == ManagedOrderAction.EmergencyClose
            && operation.Outcome == nameof(RecoveryOperationOutcome.NotExecuted))
            return await ApplyEmergencyCloseFailureCoreAsync(updated, observation.Retryability, cancellationToken)
                .ConfigureAwait(false);

        if ((action == ManagedOrderAction.Entry || action == ManagedOrderAction.CancelRemainder
                || action == ManagedOrderAction.Protection)
            && updated.PositionQuantity > 0m && updated.Status != ManagedOrderStatus.Protected)
            return await RepairProtectionCoreAsync(updated, savedOperatingIntent: false, cancellationToken)
                .ConfigureAwait(false);
        return new(operation.Outcome == nameof(RecoveryOperationOutcome.Confirmed)
                || action == ManagedOrderAction.CancelRemainder
                    && updated.EntryTerminalState == ManagedEntryTerminalState.Filled,
            "Исход восстановлен по свежему чтению; повторная передача не выполнялась.", state.IntentId);
    }

    private ManagedOrderState ApplyObservation(
        ManagedOrderState state,
        ManagedOrderAction action,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation)
    {
        var account = read.Account!;
        return action switch
        {
            ManagedOrderAction.Entry => ApplyEntry(state, read, observation),
            ManagedOrderAction.CancelRemainder => ApplyCancel(state, read, observation),
            ManagedOrderAction.Protection => ApplyProtection(state, account, observation),
            ManagedOrderAction.EmergencyClose => ApplyClose(state, account, observation),
            _ => state
        };
    }

    private ManagedOrderState ApplyEntry(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation)
    {
        var account = read.Account!;
        if (observation.Outcome == RecoveryOperationOutcome.NotExecuted)
            return state with
            {
                Status = ManagedOrderStatus.EntryRejected,
                EntryTerminalState = ManagedEntryTerminalState.Rejected,
                FilledQuantity = 0m,
                RemainingQuantity = 0m,
                PositionQuantity = 0m,
                LastFailure = "Вход подтверждённо отклонён."
            };
        if (!TryReadFillProgress(state, read, observation, _adapter.MaximumSnapshotAge, out var progress))
            return state with { Status = ManagedOrderStatus.OwnershipAmbiguous };
        return state with
        {
            EntryOrderId = observation.OrderId,
            PositionId = observation.PositionId,
            FilledQuantity = progress.Filled,
            RemainingQuantity = progress.EntryRemainder,
            PositionQuantity = progress.CurrentPosition,
            TakeProfitOrderId = progress.Protection?.TakeProfitOrderId ?? observation.TakeProfitOrderId,
            StopLossOrderId = progress.Protection?.StopLossOrderId ?? observation.StopLossOrderId,
            TakeProfitCoveredQuantity = progress.Protection?.TakeProfitCoveredQuantity ?? 0m,
            StopLossCoveredQuantity = progress.Protection?.StopLossCoveredQuantity ?? 0m,
            TakeProfitClosedQuantity = progress.TakeProfitClosed,
            StopLossClosedQuantity = progress.StopLossClosed,
            EntryTerminalState = observation.EntryOrderState,
            Status = progress.Position is null
                ? progress.Filled > 0m && progress.EntryRemainder == 0m
                    ? ManagedOrderStatus.Closed : ManagedOrderStatus.EntryOpen
                : ProtectionComplete(account, state, progress.Position)
                    ? ManagedOrderStatus.Protected : ManagedOrderStatus.PositionUnprotected,
            LastFailure = null
        };
    }

    private ManagedOrderState ApplyCancel(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation)
    {
        var account = read.Account!;
        if (!TryReadFillProgress(state, read, observation, _adapter.MaximumSnapshotAge, out var progress))
            return state with { Status = ManagedOrderStatus.OwnershipAmbiguous };
        var cancelled = observation.EntryOrderState == ManagedEntryTerminalState.Cancelled;
        var filled = observation.EntryOrderState == ManagedEntryTerminalState.Filled;
        return state with
        {
            EntryOrderId = observation.OrderId,
            PositionId = observation.PositionId ?? state.PositionId,
            FilledQuantity = progress.Filled,
            RemainingQuantity = progress.EntryRemainder,
            PositionQuantity = progress.CurrentPosition,
            TakeProfitOrderId = progress.Protection?.TakeProfitOrderId ?? observation.TakeProfitOrderId
                ?? state.TakeProfitOrderId,
            StopLossOrderId = progress.Protection?.StopLossOrderId ?? observation.StopLossOrderId
                ?? state.StopLossOrderId,
            TakeProfitCoveredQuantity = progress.Protection?.TakeProfitCoveredQuantity ?? 0m,
            StopLossCoveredQuantity = progress.Protection?.StopLossCoveredQuantity ?? 0m,
            TakeProfitClosedQuantity = progress.TakeProfitClosed,
            StopLossClosedQuantity = progress.StopLossClosed,
            EntryTerminalState = observation.EntryOrderState,
            Status = progress.Position is null
                ? cancelled && progress.Filled == 0m ? ManagedOrderStatus.Cancelled
                    : cancelled || filled ? ManagedOrderStatus.Closed : ManagedOrderStatus.EntryOpen
                    : ProtectionComplete(account, state, progress.Position)
                        ? ManagedOrderStatus.Protected : ManagedOrderStatus.PositionUnprotected,
            LastFailure = observation.Outcome == RecoveryOperationOutcome.NotExecuted
                && !filled ? "Отмена не выполнена; остаток заявки остаётся открытым."
                : null
        };
    }

    private static ManagedOrderState ApplyProtection(
        ManagedOrderState state,
        AccountSnapshot account,
        RecoveryOperationObservation observation)
    {
        var protection = account.Protections.Value!.SingleOrDefault(candidate => candidate.PositionId == state.PositionId);
        if (protection is null)
            return state with
            {
                Status = ManagedOrderStatus.PositionUnprotected,
                PositionQuantity = observation.PositionQuantity ?? state.PositionQuantity,
                TakeProfitOrderId = observation.TakeProfitOrderId ?? state.TakeProfitOrderId,
                StopLossOrderId = observation.StopLossOrderId ?? state.StopLossOrderId,
                TakeProfitCoveredQuantity = 0m,
                StopLossCoveredQuantity = 0m,
                LastFailedProtectionQuantity = observation.Outcome == RecoveryOperationOutcome.NotExecuted
                    ? state.PendingQuantity : null
            };
        var protectedPosition = FindOwnedPosition(account, state, out _);
        return state with
        {
            TakeProfitOrderId = protection.TakeProfitOrderId,
            StopLossOrderId = protection.StopLossOrderId,
            TakeProfitCoveredQuantity = protection.TakeProfitCoveredQuantity ?? 0m,
            StopLossCoveredQuantity = protection.StopLossCoveredQuantity ?? 0m,
            PositionQuantity = protectedPosition?.Quantity ?? observation.PositionQuantity ?? state.PositionQuantity,
            Status = observation.Outcome == RecoveryOperationOutcome.Confirmed && protectedPosition is not null
                && ProtectionComplete(account, state, protectedPosition)
                    ? ManagedOrderStatus.Protected : ManagedOrderStatus.PositionUnprotected,
            LastFailure = observation.Outcome == RecoveryOperationOutcome.NotExecuted
                ? "Восстановление TP/SL подтверждённо отклонено." : null,
            LastFailedProtectionQuantity = observation.Outcome == RecoveryOperationOutcome.NotExecuted
                ? state.PendingQuantity : null
        };
    }

    private ManagedOrderState ApplyClose(
        ManagedOrderState state,
        AccountSnapshot account,
        RecoveryOperationObservation observation)
    {
        var position = state.PositionId is null ? null : account.Positions.Value!
            .SingleOrDefault(candidate => candidate.PositionId == state.PositionId);
        var remaining = position?.Quantity ?? 0m;
        var protection = position is null ? null : account.Protections.Value!
            .SingleOrDefault(candidate => candidate.PositionId == position.PositionId);
        var updated = state with
        {
            PositionQuantity = remaining,
            TakeProfitOrderId = protection?.TakeProfitOrderId ?? state.TakeProfitOrderId,
            StopLossOrderId = protection?.StopLossOrderId ?? state.StopLossOrderId,
            TakeProfitCoveredQuantity = protection?.TakeProfitCoveredQuantity ?? 0m,
            StopLossCoveredQuantity = protection?.StopLossCoveredQuantity ?? 0m,
            Status = remaining == 0m ? ManagedOrderStatus.Closed
                : protection is not null && ProtectionComplete(account, state, position!)
                    ? ManagedOrderStatus.Protected : ManagedOrderStatus.PositionUnprotected,
            NextEmergencyCloseAt = null,
            LastFailure = observation.Outcome == RecoveryOperationOutcome.NotExecuted
                ? "Аварийное закрытие не выполнено; результат ожидает решения по повтору."
                : null
        };
        if (observation.Outcome != RecoveryOperationOutcome.NotExecuted
            || updated.Status == ManagedOrderStatus.Protected)
            return updated;
        return PlanCloseRetry(updated, observation.Retryability);
    }

    private async Task<OrderLifecycleResult> HandleProtectionRejectedCoreAsync(
        ManagedOrderState state,
        decimal? failedQuantity,
        RecoverySnapshotRead failureRead,
        CancellationToken cancellationToken)
    {
        var savedFailure = await SaveAndPauseAsync(state with
        {
            Status = ManagedOrderStatus.PositionUnprotected,
            LastFailure = "Восстановление TP/SL подтверждённо отклонено.",
            LastFailedProtectionQuantity = failedQuantity
        }, "Защиту позиции не удалось восстановить; новые входы требуют сверки.", cancellationToken)
            .ConfigureAwait(false);
        if (savedFailure is null)
            return Blocked("Отказ защиты не сохранился в журнале.", state.IntentId);

        await NotifyAsync(state.IntentId + ":protection-error", NotificationCategory.Error,
            "Защиту позиции не удалось восстановить; состояние проверяется.", cancellationToken).ConfigureAwait(false);

        state = savedFailure;
        var progress = default(FillProgress);
        if (!TryReadStateProgress(state, failureRead, out progress))
            return await BlockAndReconcileAsync(state,
                "После отказа защиты снимок не подтвердил полный объём заявки и позиции.", cancellationToken)
                .ConfigureAwait(false);
        var refreshed = await SaveAsync(ApplyFillProgress(state, progress), cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
            return Blocked("Актуальный объём позиции не сохранился.", state.IntentId);
        state = refreshed;

        if (progress.EntryRemainder > 0m || state.EntryTerminalState == ManagedEntryTerminalState.Open)
        {
            var cancelled = await CancelRemainderCoreAsync(state.IntentId, savedOperatingIntent: false,
                cancellationToken, repairAfterCancel: false).ConfigureAwait(false);
            state = Find(state.IntentId)!;
            if (!cancelled.Succeeded || state.EntryTerminalState == ManagedEntryTerminalState.Open
                || state.RemainingQuantity > 0m)
                return await BlockAndReconcileAsync(state,
                    "Закрытие запрещено: точный остаток заявки ещё открыт или его исход неизвестен.",
                    cancellationToken).ConfigureAwait(false);
        }

        var afterCancel = await ReadFreshAsync(state, cancellationToken).ConfigureAwait(false);
        if (afterCancel is null || !TryReadStateProgress(state, afterCancel, out progress))
            return await BlockAndReconcileAsync(state,
                "После отмены остатка нельзя подтвердить текущую позицию и защиту.", cancellationToken)
                .ConfigureAwait(false);
        refreshed = await SaveAsync(ApplyFillProgress(state, progress), cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
            return Blocked("Фактическая позиция после отмены не сохранилась.", state.IntentId);
        state = refreshed;
        if (progress.EntryRemainder > 0m || state.EntryTerminalState == ManagedEntryTerminalState.Open)
            return await BlockAndReconcileAsync(state,
                "Закрытие запрещено, пока остаток заявки остаётся открытым.", cancellationToken)
                .ConfigureAwait(false);

        if (progress.Position is null)
        {
            if (progress.Filled > 0m && progress.CurrentPosition == 0m)
            {
                var closed = await SaveAsync(state with { Status = ManagedOrderStatus.Closed }, cancellationToken)
                    .ConfigureAwait(false);
                return closed is null
                    ? Blocked("Закрытие позиции не сохранилось.", state.IntentId)
                    : new(true, "Позиция закрылась по подтверждённым TP/SL до аварийного действия.", state.IntentId);
            }
            return await BlockAndReconcileAsync(state,
                "После отказа защиты не найдено подтверждённой позиции для безопасного выхода.", cancellationToken)
                .ConfigureAwait(false);
        }

        if (ProtectionComplete(afterCancel.Account!, state, progress.Position))
        {
            var protectedState = await SaveAsync(state with
            {
                Status = ManagedOrderStatus.Protected,
                NextEmergencyCloseAt = null,
                TakeProfitOrderId = progress.Protection!.TakeProfitOrderId,
                StopLossOrderId = progress.Protection.StopLossOrderId,
                TakeProfitCoveredQuantity = progress.Protection.TakeProfitCoveredQuantity!.Value,
                StopLossCoveredQuantity = progress.Protection.StopLossCoveredQuantity!.Value
            }, cancellationToken).ConfigureAwait(false);
            return protectedState is null
                ? Blocked("Повторно сверенная защита не сохранилась.", state.IntentId)
                : new(true, "После отмены остатка TP и SL покрывают полный актуальный объём.", state.IntentId);
        }

        if (failedQuantity is not null && progress.CurrentPosition != failedQuantity.Value)
            return await RepairProtectionCoreAsync(state, savedOperatingIntent: false, cancellationToken)
                .ConfigureAwait(false);

        var waiting = await SaveAsync(state with
        {
            Status = ManagedOrderStatus.EmergencyCloseWaiting,
            NextEmergencyCloseAt = _clock.UtcNow,
            EmergencyCloseRetryability = RecoveryRetryability.Unknown,
            LastFailure = "Защита отклонена; открыт только безопасный выход по фактическому объёму."
        }, cancellationToken).ConfigureAwait(false);
        if (waiting is null)
            return Blocked("План безопасного закрытия не сохранился.", state.IntentId);
        return await RunDueEmergencyCloseCoreAsync(state.IntentId, savedOperatingIntent: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ManagedOrderState?> SaveAndPauseAsync(
        ManagedOrderState state,
        string reason,
        CancellationToken cancellationToken)
    {
        var saved = await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        if (saved is null)
            return null;
        await _recovery.ReportFailureAsync(saved.Venue, saved.Account, reason, _settings, CancellationToken.None)
            .ConfigureAwait(false);
        return saved;
    }

    private async Task<ManagedOrderState> EnsureEmergencyCloseNoticeAsync(
        ManagedOrderState state,
        CancellationToken cancellationToken)
    {
        if (!state.EmergencyCloseNoticePending || _notifications is null)
            return state;
        await _notifications.DispatchAsync(new NotificationMessage(state.IntentId + ":close-error",
            NotificationCategory.Error,
            "Повторные аварийные закрытия исчерпаны; следующая сверка назначена позже."), cancellationToken)
            .ConfigureAwait(false);
        var saved = await SaveAsync(state with { EmergencyCloseNoticePending = false }, cancellationToken)
            .ConfigureAwait(false);
        return saved ?? state;
    }

    private async Task<OrderLifecycleResult> ApplyEmergencyCloseFailureCoreAsync(
        ManagedOrderState state,
        RecoveryRetryability retryability,
        CancellationToken cancellationToken)
    {
        if (state.Status == ManagedOrderStatus.EmergencyClosePaused)
            return await BlockAndReconcileAsync(state,
                "Аварийное закрытие требует временного устранения причины или ручного восстановления.",
                cancellationToken).ConfigureAwait(false);
        if (state.EmergencyCloseNoticePending)
            state = await EnsureEmergencyCloseNoticeAsync(state, cancellationToken).ConfigureAwait(false);
        if (state.Status == ManagedOrderStatus.EmergencyCloseWaiting)
            return new(false, retryability == RecoveryRetryability.Retryable
                ? "Сверенный временный отказ назначил безопасный повтор."
                : "Сверенный отказ не разрешает автоматический повтор.", state.IntentId);
        return new(true, "Закрытие не требуется после свежей проверки позиции.", state.IntentId);
    }

    private bool TryReadStateProgress(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        out FillProgress progress)
    {
        var currentPosition = read.Account?.Positions.Value?.Where(position => position.Contract == state.Contract)
            .ToArray() ?? [];
        var position = currentPosition.Length == 1 ? currentPosition[0] : null;
        var observation = new RecoveryOperationObservation("lifecycle-read", state.Venue, state.Account,
            state.Contract, "ReadManagedLifecycle", RecoveryOperationOutcome.Confirmed,
            state.EntryOrderId, position?.PositionId ?? state.PositionId, state.Direction,
            state.FilledQuantity, state.RemainingQuantity, position?.Quantity ?? 0m,
            state.TakeProfitOrderId, state.StopLossOrderId, EntryOrderState: state.EntryTerminalState);
        return TryReadFillProgress(state, read, observation, _adapter.MaximumSnapshotAge, out progress);
    }

    private static ManagedOrderState ApplyFillProgress(ManagedOrderState state, FillProgress progress)
    {
        var status = progress.Position is null
            ? progress.Filled > 0m && progress.EntryRemainder == 0m
                ? ManagedOrderStatus.Closed
                : state.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                    ? ManagedOrderStatus.Cancelled
                    : state.EntryTerminalState == ManagedEntryTerminalState.Rejected
                        ? ManagedOrderStatus.EntryRejected : ManagedOrderStatus.EntryOpen
            : state.Status == ManagedOrderStatus.EmergencyCloseWaiting
                ? ManagedOrderStatus.EmergencyCloseWaiting : ManagedOrderStatus.PositionUnprotected;
        return state with
        {
            EntryOrderId = progress.OrderId,
            PositionId = progress.PositionId ?? state.PositionId,
            FilledQuantity = progress.Filled,
            RemainingQuantity = progress.EntryRemainder,
            PositionQuantity = progress.CurrentPosition,
            TakeProfitOrderId = progress.Protection?.TakeProfitOrderId ?? state.TakeProfitOrderId,
            StopLossOrderId = progress.Protection?.StopLossOrderId ?? state.StopLossOrderId,
            TakeProfitCoveredQuantity = progress.Protection?.TakeProfitCoveredQuantity ?? 0m,
            StopLossCoveredQuantity = progress.Protection?.StopLossCoveredQuantity ?? 0m,
            TakeProfitClosedQuantity = progress.TakeProfitClosed,
            StopLossClosedQuantity = progress.StopLossClosed,
            Status = status,
            NextEmergencyCloseAt = status == ManagedOrderStatus.EmergencyCloseWaiting
                ? state.NextEmergencyCloseAt : null
        };
    }

    private ManagedOrderState PlanCloseRetry(ManagedOrderState state, RecoveryRetryability retryability)
    {
        if (retryability != RecoveryRetryability.Retryable)
            return state with
            {
                Status = ManagedOrderStatus.EmergencyClosePaused,
                NextEmergencyCloseAt = null,
                EmergencyCloseRetryability = retryability,
                LastFailure = retryability switch
                {
                    RecoveryRetryability.Permanent => "Аварийное закрытие отклонено без права повтора.",
                    RecoveryRetryability.AuthorizationRequired => "Для аварийного закрытия требуется восстановить авторизацию.",
                    _ => "Причина отказа аварийного закрытия не подтверждает безопасный повтор."
                }
            };
        var retries = state.EmergencyCloseRetriesUsed;
        if (retries < _settings.EmergencyCloseRetryCount)
            return state with
            {
                EmergencyCloseRetriesUsed = retries + 1,
                Status = ManagedOrderStatus.EmergencyCloseWaiting,
                NextEmergencyCloseAt = _clock.UtcNow.AddSeconds(_settings.EmergencyCloseRetryDelaySeconds),
                EmergencyCloseRetryability = retryability,
                LastFailure = "Безопасное закрытие подтверждённо отклонено."
            };
        return state with
        {
            Status = ManagedOrderStatus.EmergencyCloseWaiting,
            NextEmergencyCloseAt = _clock.UtcNow.AddSeconds(_settings.EmergencyCloseAfterNoticeSeconds),
            EmergencyCloseNoticeSent = true,
            EmergencyCloseNoticePending = true,
            EmergencyCloseRetryability = retryability,
            LastFailure = "Попытки аварийного закрытия исчерпаны; назначено уведомлённое ожидание."
        };
    }

    private bool TryVerifyActionEvidence(
        ManagedOrderState state,
        ManagedOrderAction action,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation) => action switch
        {
            ManagedOrderAction.Entry => TryReadEntry(state, read, observation),
            ManagedOrderAction.CancelRemainder => TryVerifyCancel(state, read, observation),
            ManagedOrderAction.Protection => FindOwnedPosition(read.Account!, state, out _) is { } position
                && HasNoOtherContractOrders(read.Account!, state)
                && ObservationMatchesPosition(state, position, observation)
                && (observation.Outcome == RecoveryOperationOutcome.Confirmed
                    ? ProtectionComplete(read.Account!, state, position)
                    : observation.Outcome == RecoveryOperationOutcome.NotExecuted
                        && !ProtectionComplete(read.Account!, state, position)),
            ManagedOrderAction.EmergencyClose => CloseEvidenceValid(state, read, observation,
                state.PendingQuantity ?? 0m)
                && state.EntryTerminalState != ManagedEntryTerminalState.Open
                && state.RemainingQuantity == 0m
                && HasNoOtherContractOrders(read.Account!, state)
                && !read.Account!.Orders.Value!.Any(order => order.OrderId == state.EntryOrderId
                    && order.RemainingQuantity is > 0m),
            _ => false
        };

    private bool TryReadEntry(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation)
    {
        if (observation.Outcome == RecoveryOperationOutcome.NotExecuted)
            return observation.EntryOrderState == ManagedEntryTerminalState.Rejected
                && observation.FilledQuantity == 0m && observation.RemainingQuantity == 0m
                && observation.Direction == state.Direction
                && observation.OrderId is null && observation.PositionId is null
                && !read.Account!.Orders.Value!.Any(order => order.Contract == state.Contract)
                && !read.Account.Positions.Value!.Any(position => position.Contract == state.Contract);
        if (observation.Outcome != RecoveryOperationOutcome.Confirmed
            || !TryReadFillProgress(state, read, observation, _adapter.MaximumSnapshotAge, out var progress))
            return false;
        return observation.EntryOrderState == (progress.EntryRemainder > 0m
                ? ManagedEntryTerminalState.Open : ManagedEntryTerminalState.Filled)
            && (progress.EntryRemainder > 0m || progress.Filled == state.Quantity);
    }

    private bool TryVerifyCancel(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation) =>
        observation.OrderId == state.EntryOrderId
        && observation.Direction == state.Direction
        && TryReadFillProgress(state, read, observation, _adapter.MaximumSnapshotAge, out var progress)
        && (observation.Outcome, observation.EntryOrderState) switch
        {
            (RecoveryOperationOutcome.Confirmed, ManagedEntryTerminalState.Cancelled) =>
                progress.EntryRemainder == 0m && progress.Filled < state.Quantity,
            (RecoveryOperationOutcome.Confirmed, ManagedEntryTerminalState.Filled) =>
                progress.EntryRemainder == 0m && progress.Filled == state.Quantity,
            (RecoveryOperationOutcome.NotExecuted, ManagedEntryTerminalState.Open) =>
                progress.EntryRemainder > 0m,
            (RecoveryOperationOutcome.NotExecuted, ManagedEntryTerminalState.Filled) =>
                progress.EntryRemainder == 0m && progress.Filled == state.Quantity,
            _ => false
        };

    private static bool TryReadFillProgress(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation,
        TimeSpan maximumAge,
        out FillProgress progress)
    {
        progress = default!;
        var account = read.Account!;
        var filled = observation.FilledQuantity;
        var entryRemainder = observation.RemainingQuantity;
        var orderId = observation.OrderId;
        if (filled is not { } filledQuantity || entryRemainder is not { } remainder
            || filledQuantity < 0m || filledQuantity > state.Quantity
            || remainder < 0m || remainder > state.Quantity
            || observation.Direction != state.Direction
            || string.IsNullOrWhiteSpace(orderId)
            || state.EntryOrderId is not null && state.EntryOrderId != orderId)
            return false;
        var validQuantityState = observation.EntryOrderState switch
        {
            ManagedEntryTerminalState.Open => remainder > 0m
                && filledQuantity + remainder == state.Quantity,
            ManagedEntryTerminalState.Cancelled => remainder == 0m && filledQuantity < state.Quantity,
            ManagedEntryTerminalState.Filled => remainder == 0m && filledQuantity == state.Quantity,
            _ => false
        };
        if (!validQuantityState)
            return false;

        var matchingOrders = account.Orders.Value!.Where(order => order.OrderId == orderId).ToArray();
        if (remainder > 0m)
        {
            if (matchingOrders.Length != 1 || !OwnedOrder(matchingOrders[0], state, orderId)
                || matchingOrders[0].TotalQuantity != state.Quantity
                || matchingOrders[0].RemainingQuantity != remainder
                || matchingOrders[0].FilledQuantity != filledQuantity)
                return false;
        }
        else if (matchingOrders.Any(order => order.RemainingQuantity is > 0m
                || !OwnedOrder(order, state, orderId) || order.TotalQuantity != state.Quantity
                || order.FilledQuantity != filledQuantity))
            return false;

        var contractPositions = account.Positions.Value!.Where(position => position.Contract == state.Contract).ToArray();
        if (account.Orders.Value!.Any(order => order.Contract == state.Contract && order.OrderId != orderId))
            return false;
        if (filledQuantity == 0m)
        {
            if (observation.EntryOrderState != ManagedEntryTerminalState.Cancelled
                    && observation.EntryOrderState != ManagedEntryTerminalState.Open
                || observation.PositionId is not null
                || observation.PositionQuantity is not null && observation.PositionQuantity != 0m
                || contractPositions.Length != 0)
                return false;
            progress = new(orderId, null, 0m, remainder, 0m, 0m, 0m, null, null);
            return true;
        }

        if (string.IsNullOrWhiteSpace(observation.PositionId)
            || state.PositionId is not null && state.PositionId != observation.PositionId
            || observation.PositionQuantity is not { } observedPositionQuantity
            || observedPositionQuantity < 0m || observedPositionQuantity > filledQuantity)
            return false;
        OpenPosition? activePosition = null;
        if (observedPositionQuantity > 0m)
        {
            if (contractPositions.Length != 1)
                return false;
            activePosition = contractPositions[0];
            if (activePosition.PositionId != observation.PositionId || activePosition.Direction != state.Direction
                || activePosition.Quantity != observedPositionQuantity || activePosition.Ownership != PositionOwnership.SoftwareOwned
                || activePosition.HasMixedExecution || !HasExactEntryLink(activePosition, orderId))
                return false;
        }
        else if (contractPositions.Length != 0)
            return false;

        var closedQuantity = filledQuantity - observedPositionQuantity;
        var tpClosed = 0m;
        var slClosed = 0m;
        if (closedQuantity > 0m)
        {
            if (!read.ClosedPositionHistoryComplete
                || !SectionFresh(account.History, account.ObservedAt, maximumAge, allowEmpty: true)
                || account.History.Value is not { } history)
                return false;
            var tpOrderId = observation.TakeProfitOrderId ?? state.TakeProfitOrderId;
            var slOrderId = observation.StopLossOrderId ?? state.StopLossOrderId;
            var linked = history.Where(trade => trade.PositionId == observation.PositionId
                && trade.EntryOrderIds is { Count: 1 }
                && trade.EntryOrderIds.Contains(orderId, StringComparer.Ordinal)).ToArray();
            if (linked.Length == 0 || linked.Any(trade => trade.Contract != state.Contract
                    || trade.Direction != state.Direction || trade.Ownership != PositionOwnership.SoftwareOwned
                    || trade.HasMixedExecution || trade.ClosedQuantity is not > 0m
                    || trade.ClosedAt < state.CreatedAt || trade.ClosedAt > account.ObservedAt
                    || trade.CloseOrderId is null
                    || trade.CloseKind == TradeCloseKind.TakeProfit && trade.CloseOrderId != tpOrderId
                    || trade.CloseKind == TradeCloseKind.StopLoss && trade.CloseOrderId != slOrderId
                    || trade.CloseKind is not (TradeCloseKind.TakeProfit or TradeCloseKind.StopLoss)))
                return false;
            tpClosed = linked.Where(trade => trade.CloseKind == TradeCloseKind.TakeProfit)
                .Sum(trade => trade.ClosedQuantity!.Value);
            slClosed = linked.Where(trade => trade.CloseKind == TradeCloseKind.StopLoss)
                .Sum(trade => trade.ClosedQuantity!.Value);
            if (tpClosed + slClosed != closedQuantity)
                return false;
        }
        var protection = activePosition is null ? null : account.Protections.Value!
            .SingleOrDefault(item => item.PositionId == activePosition.PositionId && item.Contract == state.Contract);
        progress = new(orderId, observation.PositionId, filledQuantity, remainder, observedPositionQuantity,
            tpClosed, slClosed, activePosition, protection);
        return true;
    }

    private static bool CloseEvidenceValid(
        ManagedOrderState state,
        RecoverySnapshotRead read,
        RecoveryOperationObservation observation,
        decimal priorQuantity)
    {
        if (observation.PositionId != state.PositionId || observation.Direction != state.Direction
            || observation.PositionQuantity is not { } positionQuantity || positionQuantity < 0m
            || observation.Outcome == RecoveryOperationOutcome.Unknown)
            return false;
        var position = state.PositionId is null ? null : read.Account!.Positions.Value!
            .SingleOrDefault(candidate => candidate.PositionId == state.PositionId);
        if (observation.Outcome == RecoveryOperationOutcome.NotExecuted)
            return position is not null && position.Quantity == priorQuantity
                && observation.PositionQuantity == position.Quantity
                && position.Ownership == PositionOwnership.SoftwareOwned && !position.HasMixedExecution
                && state.EntryOrderId is not null
                && HasExactEntryLink(position, state.EntryOrderId);
        if (observation.Outcome != RecoveryOperationOutcome.Confirmed || positionQuantity >= priorQuantity)
            return false;
        return positionQuantity == (position?.Quantity ?? 0m)
            && (position is null || position.Direction == state.Direction
                && position.Ownership == PositionOwnership.SoftwareOwned && !position.HasMixedExecution
                && HasExactEntryLink(position, state.EntryOrderId!));
    }

    private static bool ObservationMatchesPosition(
        ManagedOrderState state,
        OpenPosition position,
        RecoveryOperationObservation observation) =>
        observation.PositionId == position.PositionId && observation.Direction == state.Direction
        && observation.PositionQuantity == position.Quantity && position.Direction == state.Direction
        && position.Ownership == PositionOwnership.SoftwareOwned && !position.HasMixedExecution
        && state.EntryOrderId is not null
        && HasExactEntryLink(position, state.EntryOrderId);

    private static bool HasExactEntryLink(OpenPosition position, string entryOrderId) =>
        position.OrderIds is { Count: 1 } ids && ids.Contains(entryOrderId, StringComparer.Ordinal);

    private static bool HasNoOtherContractOrders(AccountSnapshot account, ManagedOrderState state) =>
        !account.Orders.Value!.Any(order => order.Contract == state.Contract
            && order.OrderId != state.EntryOrderId && order.RemainingQuantity is > 0m);

    private static bool ProtectionComplete(AccountSnapshot account, ManagedOrderState state, OpenPosition position)
    {
        if (position.PositionId is null || position.Quantity is not > 0m)
            return false;
        var protections = account.Protections.Value!.Where(item => item.Contract == state.Contract
            && item.PositionId == position.PositionId).ToArray();
        if (protections.Length != 1)
            return false;
        var protection = protections[0];
        return protection.TakeProfit is { } takeProfit && protection.StopLoss is { } stopLoss
            && takeProfit.Value == state.TakeProfit && stopLoss.Value == state.StopLoss
            && takeProfit.Currency == account.QuoteCurrency && stopLoss.Currency == account.QuoteCurrency
            && protection.Ownership == PositionOwnership.SoftwareOwned
            && protection.TakeProfitCoveredQuantity >= position.Quantity
            && protection.StopLossCoveredQuantity >= position.Quantity
            && !string.IsNullOrWhiteSpace(protection.TakeProfitOrderId)
            && !string.IsNullOrWhiteSpace(protection.StopLossOrderId);
    }

    private static OpenPosition? FindOwnedPosition(
        AccountSnapshot account,
        ManagedOrderState state,
        out bool ambiguous)
    {
        ambiguous = false;
        if (state.PositionId is null)
            return null;
        var matching = account.Positions.Value!.Where(position => position.Contract == state.Contract).ToArray();
        var found = matching.Where(position => position.PositionId == state.PositionId).ToArray();
        if (matching.Length != 1 || found.Length != 1 || found[0].Direction != state.Direction
            || found[0].Quantity is not > 0m || found[0].Ownership != PositionOwnership.SoftwareOwned
            || found[0].HasMixedExecution
            || state.EntryOrderId is null
            || !HasExactEntryLink(found[0], state.EntryOrderId))
        {
            ambiguous = matching.Length > 0;
            return null;
        }
        return found[0];
    }

    private static bool TryFindOwnedOrder(ManagedOrderState state, AccountSnapshot account, out OpenOrder? found)
    {
        found = null;
        if (state.EntryOrderId is null)
            return false;
        var matches = account.Orders.Value!.Where(order => order.OrderId == state.EntryOrderId).ToArray();
        if (matches.Length != 1 || account.Orders.Value!.Any(order => order.Contract == state.Contract
                && order.OrderId != state.EntryOrderId)
            || !OwnedOrder(matches[0], state, state.EntryOrderId))
            return false;
        found = matches[0];
        return true;
    }

    private static bool OwnedOrder(OpenOrder order, ManagedOrderState state, string orderId) =>
        order.OrderId == orderId && order.Contract == state.Contract && order.Direction == state.Direction
        && order.Ownership == PositionOwnership.SoftwareOwned
        && order.OperationId is not null && order.OperationId.StartsWith(state.IntentId + ":entry:", StringComparison.Ordinal);

    private async Task<RecoverySnapshotRead?> ReadFreshAsync(
        ManagedOrderState state,
        CancellationToken cancellationToken) =>
        await ReadFreshAsync(state.Venue, state.Account, cancellationToken).ConfigureAwait(false);

    private async Task<RecoverySnapshotRead?> ReadFreshAsync(
        string venue,
        string account,
        CancellationToken cancellationToken)
    {
        RecoverySnapshotRead read;
        try
        {
            read = await _adapter.ReadAsync(venue, account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        return IsFresh(read, venue, account, _clock.UtcNow, _adapter.MaximumSnapshotAge)
            ? read : null;
    }

    private static bool IsFresh(
        RecoverySnapshotRead? read,
        string venue,
        string account,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (read is not { SessionAvailable: true, Account: { } snapshot }
            || maximumAge <= TimeSpan.Zero || !read.OpeningOrderRemaindersComplete
            || !read.ProtectionCoverageComplete || !read.UnfinishedOperationsComplete
            || read.OperationOutcomes is null
            || snapshot.Venue != venue || snapshot.Account != account
            || snapshot.Availability != AccountReadState.Available
            || string.IsNullOrWhiteSpace(snapshot.Source)
            || !Fresh(snapshot.ObservedAt, now, maximumAge)
            || snapshot.LastSuccessfulReadAt is not { } lastRead
            || !Fresh(lastRead, now, maximumAge)
            || !SectionFresh(snapshot.Balances, now, maximumAge, allowEmpty: false)
            || !SectionFresh(snapshot.Positions, now, maximumAge, allowEmpty: true)
            || !SectionFresh(snapshot.Orders, now, maximumAge, allowEmpty: true)
            || !SectionFresh(snapshot.Protections, now, maximumAge, allowEmpty: true))
            return false;
        if (snapshot.Positions.Value!.Any(position => position is null
                || string.IsNullOrWhiteSpace(position.Contract) || string.IsNullOrWhiteSpace(position.PositionId)
                || string.IsNullOrWhiteSpace(position.QuantityUnit) || position.Quantity is not > 0m)
            || snapshot.Orders.Value!.Any(order => order is null || string.IsNullOrWhiteSpace(order.OrderId)
                || string.IsNullOrWhiteSpace(order.Contract) || string.IsNullOrWhiteSpace(order.QuantityUnit)
                || order.RemainingQuantity is null)
            || snapshot.Protections.Value!.Any(item => item is null
                || string.IsNullOrWhiteSpace(item.Contract)))
            return false;
        var positions = snapshot.Positions.Value!;
        var orders = snapshot.Orders.Value!;
        var protections = snapshot.Protections.Value!;
        if (positions.Select(position => position.PositionId)
                .Distinct(StringComparer.Ordinal).Count() != positions.Count
            || orders.Select(order => order.OrderId)
                .Distinct(StringComparer.Ordinal).Count() != orders.Count
            || protections.Where(protection => protection.PositionId is not null)
                .Select(protection => protection.PositionId).Distinct(StringComparer.Ordinal).Count()
                != protections.Count(protection => protection.PositionId is not null))
            return false;
        return true;
    }

    private static bool SectionFresh<T>(
        AccountSection<T> section,
        DateTimeOffset now,
        TimeSpan maximumAge,
        bool allowEmpty) =>
        section.Value is not null
        && (section.State == AccountReadState.Available
            || allowEmpty && section.State == AccountReadState.ConfirmedEmpty)
        && section.LastSuccessfulAt is { } successful && Fresh(successful, now, maximumAge)
        && section.ObservedAt is { } observed && Fresh(observed, now, maximumAge);

    private static bool Fresh(DateTimeOffset at, DateTimeOffset now, TimeSpan maximumAge) =>
        at <= now && now - at <= maximumAge;

    private static bool MatchesEligibility(
        ManagedOrderIntent intent,
        EntryEligibilitySetup setup,
        EntryEligibilityResult eligibility)
    {
        var pool = eligibility.SelectedPool;
        return eligibility.Status == EntryEligibilityStatus.ScenarioReady
            && pool?.Status == EntryEligibilityStatus.ScenarioReady
            && pool.Venue == intent.Venue && pool.Account == intent.Account && pool.Contract == intent.Contract
            && eligibility.SetupAsset == intent.Asset && eligibility.SetupMarket == setup.Market
            && eligibility.SetupDirection == intent.Direction && eligibility.SetupEntryType == intent.EntryType
            && setup.State == EntrySetupState.Complete && setup.Asset == intent.Asset
            && setup.Direction == intent.Direction && setup.EntryType == intent.EntryType
            && setup.LimitPrice == intent.LimitPrice
            && setup.TakeProfit == intent.TakeProfit && setup.StopLoss == intent.StopLoss
            && pool.Quantity == intent.Quantity;
    }

    private bool TryEvaluateEligibility(
        ManagedOrderIntent intent,
        EntryEligibilityInput input,
        AccountSnapshot actualAccount,
        DateTimeOffset now,
        out EntryEligibilityResult? eligibility)
    {
        eligibility = null;
        var matchingIndexes = input.Pools.Select((pool, index) => (pool, index))
            .Where(pair => pair.pool.Account.Venue == intent.Venue && pair.pool.Account.Account == intent.Account)
            .Select(pair => pair.index).ToArray();
        if (matchingIndexes.Length != 1 || actualAccount.Venue != intent.Venue
            || actualAccount.Account != intent.Account)
            return false;

        var index = matchingIndexes[0];
        var selected = input.Pools[index];
        if (!AccountEvidenceMatches(selected, actualAccount, now, input.Freshness.AccountMaximumAge))
            return false;

        var pools = input.Pools.ToArray();
        pools[index] = selected with
        {
            Account = actualAccount,
            Positions = selected.Positions.Select(position => position with
            {
                Position = actualAccount.Positions.Value!.Single(actual => SamePosition(position.Position, actual))
            }).ToArray(),
            Orders = selected.Orders.Select(order => order with
            {
                Order = actualAccount.Orders.Value!.Single(actual => SameOrder(order.Order, actual))
            }).ToArray()
        };
        var boundInput = input with { Pools = pools, EvaluatedAt = now };
        var evaluated = EntryEligibilityEvaluator.Evaluate(boundInput);
        if (!MatchesEligibility(intent, input.Setup, evaluated))
            return false;
        eligibility = evaluated;
        return true;
    }

    private static bool AccountEvidenceMatches(
        RiskPoolInput riskPool,
        AccountSnapshot actual,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        var supplied = riskPool.Account;
        if (supplied.Venue != actual.Venue || supplied.Account != actual.Account
            || supplied.CollateralPool != actual.CollateralPool
            || supplied.QuoteCurrency != actual.QuoteCurrency || supplied.QuantityUnit != actual.QuantityUnit
            || supplied.ObservedAt > actual.ObservedAt
            || !SectionFresh(supplied.Balances, now, maximumAge, allowEmpty: false)
            || !SectionFresh(supplied.Positions, now, maximumAge, allowEmpty: true)
            || !SectionFresh(supplied.Orders, now, maximumAge, allowEmpty: true)
            || !SectionFresh(supplied.Protections, now, maximumAge, allowEmpty: true)
            || supplied.Balances.Value != actual.Balances.Value
            || !SamePositions(supplied.Positions.Value!, actual.Positions.Value!)
            || !SameOrders(supplied.Orders.Value!, actual.Orders.Value!)
            || !SameProtections(supplied.Protections.Value!, actual.Protections.Value!))
            return false;

        if (riskPool.Positions.Count != actual.Positions.Value!.Count
            || riskPool.Positions.Any(inputPosition => !actual.Positions.Value!.Any(position =>
                SamePosition(inputPosition.Position, position))))
            return false;
        if (riskPool.Orders.Count != actual.Orders.Value!.Count
            || riskPool.Orders.Any(inputOrder => !actual.Orders.Value!.Any(order =>
                SameOrder(inputOrder.Order, order))))
            return false;
        return true;
    }

    private static bool SamePositions(
        IReadOnlyList<OpenPosition> left,
        IReadOnlyList<OpenPosition> right) => left.Count == right.Count
        && left.All(position => right.Any(other => SamePosition(position, other)));

    private static bool SamePosition(OpenPosition left, OpenPosition right) =>
        left.PositionId is not null && left.PositionId == right.PositionId
        && left.Asset == right.Asset && left.Contract == right.Contract && left.Direction == right.Direction
        && left.Quantity == right.Quantity && left.QuantityUnit == right.QuantityUnit
        && left.Ownership == right.Ownership && left.HasMixedExecution == right.HasMixedExecution
        && SameIds(left.OrderIds, right.OrderIds);

    private static bool SameOrders(IReadOnlyList<OpenOrder> left, IReadOnlyList<OpenOrder> right) => left.Count == right.Count
        && left.All(order => right.Any(other => SameOrder(order, other)));

    private static bool SameOrder(OpenOrder left, OpenOrder right) =>
        left.OrderId is not null && left.OrderId == right.OrderId
        && left.Asset == right.Asset && left.Contract == right.Contract && left.Direction == right.Direction
        && left.TotalQuantity == right.TotalQuantity && left.FilledQuantity == right.FilledQuantity
        && left.QuantityUnit == right.QuantityUnit && left.Ownership == right.Ownership
        && left.OperationId == right.OperationId;

    private static bool SameProtections(
        IReadOnlyList<PositionProtection> left,
        IReadOnlyList<PositionProtection> right) => left.Count == right.Count
        && left.All(item => item.PositionId is not null && right.Any(other =>
            other.PositionId == item.PositionId && other.Contract == item.Contract
            && other.TakeProfit == item.TakeProfit && other.StopLoss == item.StopLoss
            && other.Ownership == item.Ownership
            && other.TakeProfitCoveredQuantity == item.TakeProfitCoveredQuantity
            && other.StopLossCoveredQuantity == item.StopLossCoveredQuantity
            && other.TakeProfitOrderId == item.TakeProfitOrderId
            && other.StopLossOrderId == item.StopLossOrderId));

    private static bool SameIds(IReadOnlyCollection<string>? left, IReadOnlyCollection<string>? right) =>
        left is not null && right is not null && left.Count == right.Count
        && left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool TryGetObservation(
        RecoverySnapshotRead read,
        RecoveryOperation operation,
        out RecoveryOperationObservation? observation)
    {
        observation = null;
        return read.OperationOutcomes!.TryGetValue(operation.OperationId, out observation)
            && observation is not null && Enum.IsDefined(observation.Outcome)
            && Enum.IsDefined(observation.Retryability) && Enum.IsDefined(observation.EntryOrderState)
            && (observation.Outcome == RecoveryOperationOutcome.NotExecuted
                || observation.Retryability == RecoveryRetryability.Unknown)
            && observation.OperationId == operation.OperationId
            && observation.Venue == operation.Venue && observation.Account == operation.Account
            && observation.Market == operation.Market && observation.Operation == operation.Operation;
    }

    private ManagedOrderState? Find(string intentId) =>
        _recovery.State.ManagedOrders.SingleOrDefault(order => order.IntentId == intentId);

    private Task<ManagedOrderState?> SaveAsync(ManagedOrderState state, CancellationToken cancellationToken) =>
        _recovery.SaveManagedOrderAsync(state, cancellationToken);

    private async Task<ManagedOrderState?> ClearPendingAsync(
        string intentId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var current = Find(intentId);
        if (current?.PendingOperationId != operationId)
            return null;
        return await SaveAsync(current with
        {
            PendingOperationId = null,
            PendingAction = null,
            PendingQuantity = null,
            Status = current.PendingPriorStatus ?? ManagedOrderStatus.IntentSaved,
            PendingPriorStatus = null
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OrderLifecycleResult> BlockAndReconcileAsync(
        ManagedOrderState state,
        string reason,
        CancellationToken cancellationToken)
    {
        await _recovery.ReportFailureAsync(state.Venue, state.Account, reason, _settings, cancellationToken)
            .ConfigureAwait(false);
        return Blocked(reason, state.IntentId);
    }

    private async Task NotifyAsync(
        string id,
        NotificationCategory category,
        string text,
        CancellationToken cancellationToken)
    {
        if (_notifications is not null)
            await _notifications.DispatchAsync(new NotificationMessage(id, category, text),
                cancellationToken).ConfigureAwait(false);
    }

    private static OrderLifecycleResult Blocked(string message, string? intentId = null) =>
        new(false, message, intentId);

    private async Task<OrderLifecycleResult> ExecuteSeriallyAsync(
        CancellationToken cancellationToken,
        Func<Task<OrderLifecycleResult>> action)
    {
        await _workflowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _workflowGate.Release();
        }
    }

    private sealed record ActionResult(
        bool Succeeded,
        string Status,
        ManagedOrderState? State,
        RecoverySnapshotRead? Read,
        RecoveryOperationOutcome? Outcome,
        RecoveryOperationObservation? Observation = null);

    private sealed record FillProgress(
        string OrderId,
        string? PositionId,
        decimal Filled,
        decimal EntryRemainder,
        decimal CurrentPosition,
        decimal TakeProfitClosed,
        decimal StopLossClosed,
        OpenPosition? Position,
        PositionProtection? Protection);
}
