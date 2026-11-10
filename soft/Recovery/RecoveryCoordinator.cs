using System.Collections.Concurrent;
using TrueNorth.TradeFlow.Account;

namespace TrueNorth.TradeFlow.Recovery;

internal interface IRecoveryClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemRecoveryClock : IRecoveryClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal enum RecoveryOperationOutcome
{
    Confirmed,
    NotExecuted,
    Unknown
}

internal sealed record RecoverySnapshotRead(
    bool SessionAvailable,
    AccountSnapshot? Account,
    bool OpeningOrderRemaindersComplete,
    bool ProtectionCoverageComplete,
    bool UnfinishedOperationsComplete,
    IReadOnlyDictionary<string, RecoveryOperationObservation>? OperationOutcomes = null,
    string? FailureReason = null);

internal sealed record RecoveryOperationObservation(
    string OperationId,
    string Venue,
    string Account,
    string Market,
    string Operation,
    RecoveryOperationOutcome Outcome);

internal interface IRecoverySnapshotSource
{
    TimeSpan MaximumSnapshotAge { get; }
    Task<RecoverySnapshotRead> ReadAsync(string venue, string account, CancellationToken cancellationToken);
}

internal sealed record RecoveryOperationRequest(
    string OperationId,
    string Venue,
    string Account,
    string Market,
    string Operation,
    DateTimeOffset? DeadlineAt = null);

internal sealed record ConfirmedRecoveryScope(string Venue, string Account);

internal enum RecoveryTransferOutcome
{
    Confirmed,
    NotExecuted,
    Unknown
}

internal sealed class RecoveryCoordinator
{
    private const string UnknownVenue = "*";
    private readonly RecoveryJournalStore _store;
    private readonly IRecoverySnapshotSource? _source;
    private readonly IRecoveryClock _clock;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _venueChecks = new(StringComparer.Ordinal);
    private volatile RecoveryJournalState _state = new();
    private volatile RecoveryJournalStatus _storageStatus;
    private volatile bool _storageReady;
    private volatile string? _storageMessage;

    internal RecoveryCoordinator(
        RecoveryJournalStore store,
        AppSettings settings,
        IRecoveryClock? clock = null,
        IRecoverySnapshotSource? source = null)
    {
        _store = store;
        _settings = settings.Copy();
        _clock = clock ?? new SystemRecoveryClock();
        _source = source;

        var loaded = _store.Load();
        _state = loaded.State;
        _storageStatus = loaded.Status;
        _storageReady = loaded.Status is RecoveryJournalStatus.Missing or RecoveryJournalStatus.Loaded;
        _storageMessage = loaded.Message;
        if (_storageReady)
            RestoreInterruptedWork();
    }

    internal event Action? StatusChanged;

    internal RecoveryJournalStatus StorageStatus => _storageStatus;
    internal bool StorageReady => _storageReady;
    internal string? StorageMessage => _storageMessage;
    internal RecoveryJournalState State => new()
    {
        Venues = [.. _state.Venues],
        Operations = [.. _state.Operations],
        Events = [.. _state.Events]
    };

    internal bool HasPendingRecovery => !_storageReady
        || _state.Venues.Any(venue => venue.Phase != RecoveryReadPhase.Resolved)
        || _state.Operations.Any(operation => operation.Stage is RecoveryOperationStage.TransferStarted
            or RecoveryOperationStage.Unknown);

    internal string StatusText
    {
        get
        {
            if (!_storageReady)
                return "Журнал восстановления недоступен. Зависимые действия заблокированы. "
                    + (_storageMessage ?? _storageStatus.ToString());

            var paused = _state.Venues.Where(venue => venue.Phase != RecoveryReadPhase.Resolved).ToArray();
            var unresolved = _state.Operations.Where(operation => operation.Stage is RecoveryOperationStage.TransferStarted
                or RecoveryOperationStage.Unknown).ToArray();
            if (paused.Length == 0 && unresolved.Length == 0)
                return "Активных пауз восстановления нет.";

            if (paused.Any(venue => venue.Venue == UnknownVenue))
                return "Область сбоя неизвестна. Новые входы на всех площадках заблокированы до уточнения.";
            if (paused.Any(venue => venue.Phase == RecoveryReadPhase.UnknownScope))
                return $"Счёт площадки {paused.First(venue => venue.Phase == RecoveryReadPhase.UnknownScope).Venue} не определён. "
                    + "Новые входы заблокированы до уточнения счёта.";

            if (paused.Length == 0)
                return $"Операция {unresolved[0].OperationId} находится в участке передачи на "
                    + $"{unresolved[0].Venue} · {unresolved[0].Account}; новые входы площадки заблокированы до исхода.";

            var scope = string.Join(", ", paused.Select(venue =>
                string.IsNullOrWhiteSpace(venue.Account) ? venue.Venue : $"{venue.Venue} · {venue.Account}"));
            var nextCheck = paused.Where(venue => venue.NextCheckAt is not null)
                .Select(venue => venue.NextCheckAt!.Value.ToLocalTime().ToString("HH:mm:ss"))
                .FirstOrDefault();
            var status = $"Пауза восстановления: {scope}. Новые входы заблокированы до полной сверки.";
            var activeTransfers = unresolved.Where(operation => operation.Stage == RecoveryOperationStage.TransferStarted)
                .Select(operation => operation.OperationId).ToArray();
            if (activeTransfers.Length > 0)
                status += $" Участок передачи выполняется: {string.Join(", ", activeTransfers)}.";
            var unknownOutcomes = unresolved.Where(operation => operation.Stage == RecoveryOperationStage.Unknown)
                .Select(operation => operation.OperationId).ToArray();
            if (unknownOutcomes.Length > 0)
                status += $" Неизвестный исход операции: {string.Join(", ", unknownOutcomes)}.";
            if (_source is null)
                status += " Источник снимков не подключён; автоматическое чтение не выполняется.";
            else if (nextCheck is not null)
                status += $" Срок проверки по расписанию: {nextCheck}.";
            return status;
        }
    }

    internal bool IsEntryAllowed(string venue, bool savedOperatingIntent)
    {
        if (!savedOperatingIntent || !_storageReady || string.IsNullOrWhiteSpace(venue))
            return false;
        if (_state.Venues.Any(state => state.Venue == UnknownVenue
                || (state.Venue == venue && state.Phase != RecoveryReadPhase.Resolved)))
            return false;
        return !_state.Operations.Any(operation => operation.Venue == venue
            && operation.Stage is (RecoveryOperationStage.TransferStarted or RecoveryOperationStage.Unknown));
    }

    internal async Task<string?> ReportFailureAsync(
        string? venue,
        string? account,
        string reason,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!ValidRetrySettings(settings) || string.IsNullOrWhiteSpace(reason))
            return null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_storageReady)
                return null;

            var scopeVenue = string.IsNullOrWhiteSpace(venue) ? UnknownVenue : venue;
            var scopeAccount = string.IsNullOrWhiteSpace(account) ? null : account;
            var coveringUnknownScope = _state.Venues.FirstOrDefault(candidate =>
                candidate.Phase == RecoveryReadPhase.UnknownScope
                && (candidate.Venue == UnknownVenue || candidate.Venue == scopeVenue));
            if (coveringUnknownScope is not null)
                return coveringUnknownScope.EpisodeId;

            var existing = FindVenueState(_state, scopeVenue, scopeAccount);
            if (existing is { Phase: not RecoveryReadPhase.Resolved })
                return existing.EpisodeId;

            var state = CreateFailureState(_state, scopeVenue, scopeAccount, reason, settings.Copy(), _clock.UtcNow,
                unknownScope: scopeVenue == UnknownVenue || scopeAccount is null);
            var episodeId = FindVenueState(state, scopeVenue, scopeAccount)!.EpisodeId;
            return SaveState(state) ? episodeId : null;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    internal async Task<bool> RefineUnknownScopeAsync(
        string episodeId,
        ConfirmedRecoveryScope confirmedScope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(episodeId)
            || confirmedScope is null
            || string.IsNullOrWhiteSpace(confirmedScope.Venue)
            || confirmedScope.Venue == UnknownVenue
            || string.IsNullOrWhiteSpace(confirmedScope.Account))
            return false;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_storageReady)
                return false;

            var unknown = _state.Venues.SingleOrDefault(candidate =>
                candidate.EpisodeId == episodeId && candidate.Phase == RecoveryReadPhase.UnknownScope);
            if (unknown is null || (unknown.Venue != UnknownVenue && unknown.Venue != confirmedScope.Venue))
                return false;

            var existing = FindVenueState(_state, confirmedScope.Venue, confirmedScope.Account);
            if (existing is { Phase: not RecoveryReadPhase.Resolved })
                return false;

            var now = _clock.UtcNow;
            var phase = unknown.AdditionalReadsRemaining == 0
                ? RecoveryReadPhase.Periodic
                : RecoveryReadPhase.AdditionalReads;
            var refined = unknown with
            {
                Venue = confirmedScope.Venue,
                Account = confirmedScope.Account,
                Phase = phase,
                NextCheckAt = now.AddSeconds(phase == RecoveryReadPhase.AdditionalReads
                    ? unknown.RetryDelaySeconds
                    : unknown.RecoveryCheckSeconds),
                ReadInProgress = false,
                ReadStartedAt = null,
                LastReadFailure = null
            };
            var events = _state.Events.Select(recoveryEvent =>
                recoveryEvent.EventId == episodeId + ":error"
                    ? recoveryEvent with { Venue = confirmedScope.Venue, Account = confirmedScope.Account }
                    : recoveryEvent).ToList();
            var venues = _state.Venues.Where(candidate => candidate.EpisodeId != episodeId).ToList();
            return SaveState(ReplaceVenue(_state with { Venues = venues, Events = events }, refined));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    internal async Task<bool> PrepareOperationAsync(
        RecoveryOperationRequest request,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default)
    {
        if (!ValidRequest(request))
            return false;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            if ((request.DeadlineAt is { } deadlineAt && deadlineAt <= now)
                || !IsEntryAllowed(request.Venue, savedOperatingIntent)
                || _state.Operations.Any(operation => operation.OperationId == request.OperationId))
                return false;

            var operation = new RecoveryOperation(request.OperationId, request.Venue, request.Account,
                request.Market, request.Operation, null, now, request.DeadlineAt,
                null, null, null, RecoveryOperationStage.Prepared, null);
            return SaveState(_state with { Operations = [.. _state.Operations, operation] });
        }
        finally
        {
            _stateGate.Release();
        }
    }

    internal async Task<bool> BeginTransferAsync(
        string operationId,
        bool savedOperatingIntent,
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_storageReady)
                return false;
            var operation = _state.Operations.SingleOrDefault(candidate => candidate.OperationId == operationId);
            if (operation is not { Stage: RecoveryOperationStage.Prepared }
                || !IsEntryAllowed(operation.Venue, savedOperatingIntent))
                return false;

            var now = _clock.UtcNow;
            if (operation.DeadlineAt is { } deadlineAt && deadlineAt <= now)
            {
                var abandoned = operation with
                {
                    Stage = RecoveryOperationStage.Abandoned,
                    CompletedAt = now,
                    Outcome = "Срок подготовки истёк до участка передачи."
                };
                SaveState(ReplaceOperation(_state, abandoned));
                return false;
            }

            var updated = operation with
            {
                Stage = RecoveryOperationStage.TransferStarted,
                TransferStartedAt = now
            };
            return SaveState(ReplaceOperation(_state, updated));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    internal async Task<bool> CompleteTransferAsync(
        string operationId,
        RecoveryTransferOutcome outcome,
        AppSettings settings,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Enum.IsDefined(outcome))
                return false;
            if (!_storageReady)
                return false;
            var operation = _state.Operations.SingleOrDefault(candidate => candidate.OperationId == operationId);
            if (operation is not { Stage: RecoveryOperationStage.TransferStarted })
                return false;

            var now = _clock.UtcNow;
            if (outcome != RecoveryTransferOutcome.Unknown)
            {
                var completed = operation with
                {
                    Stage = outcome == RecoveryTransferOutcome.Confirmed
                        ? RecoveryOperationStage.Confirmed
                        : RecoveryOperationStage.NotExecuted,
                    CompletedAt = now,
                    Outcome = detail ?? outcome.ToString()
                };
                return SaveState(ReplaceOperation(_state, completed));
            }

            if (!ValidRetrySettings(settings))
                return false;
            var reason = string.IsNullOrWhiteSpace(detail) ? "Исход передачи неизвестен." : detail;
            var state = CreateFailureState(_state, operation.Venue, operation.Account, reason, settings.Copy(), now,
                unknownScope: false);
            var venueState = FindVenueState(state, operation.Venue, operation.Account)!;
            var unknown = operation with
            {
                Stage = RecoveryOperationStage.Unknown,
                EpisodeId = venueState.EpisodeId,
                CompletedAt = now,
                Outcome = reason
            };
            return SaveState(ReplaceOperation(state, unknown));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    internal async Task<bool> CheckDueAsync(string venue, CancellationToken cancellationToken = default)
    {
        if (_source is null || string.IsNullOrWhiteSpace(venue) || venue == UnknownVenue)
            return false;

        var venueGate = _venueChecks.GetOrAdd(venue, _ => new SemaphoreSlim(1, 1));
        await venueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecoveryVenueState scheduled;
            RecoveryOperation[] scheduledOperations;
            bool isAdditionalRead;
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_storageReady)
                    return false;
                var now = _clock.UtcNow;
                var due = _state.Venues.Where(candidate => candidate.Venue == venue
                        && candidate.Account is not null
                        && candidate.Phase is (RecoveryReadPhase.AdditionalReads or RecoveryReadPhase.Periodic)
                        && !candidate.ReadInProgress
                        && candidate.NextCheckAt <= now)
                    .OrderBy(candidate => candidate.NextCheckAt)
                    .FirstOrDefault();
                if (due is null || _state.Operations.Any(operation => operation.Venue == venue
                        && operation.Account == due.Account
                        && operation.Stage == RecoveryOperationStage.TransferStarted))
                    return false;

                isAdditionalRead = due.Phase == RecoveryReadPhase.AdditionalReads
                    && due.AdditionalReadsRemaining > 0;
                if (due.Phase == RecoveryReadPhase.AdditionalReads && !isAdditionalRead)
                {
                    var exhausted = due with
                    {
                        Phase = RecoveryReadPhase.Periodic,
                        NextCheckAt = now.AddSeconds(due.RecoveryCheckSeconds),
                        ReadInProgress = false,
                        LastReadFailure = due.LastReadFailure ?? "Повторы чтения исчерпаны."
                    };
                    SaveState(EmitError(_state, exhausted, now));
                    return false;
                }

                var started = due with
                {
                    AdditionalReadsRemaining = isAdditionalRead ? due.AdditionalReadsRemaining - 1 : 0,
                    AdditionalReadsStarted = due.AdditionalReadsStarted + (isAdditionalRead ? 1 : 0),
                    ReadInProgress = true,
                    ReadStartedAt = now,
                    NextCheckAt = now.AddSeconds(isAdditionalRead ? due.RetryDelaySeconds : due.RecoveryCheckSeconds)
                };
                if (!SaveState(ReplaceVenue(_state, started)))
                    return false;
                scheduled = started;
                scheduledOperations = _state.Operations.Where(operation => operation.Venue == venue
                        && operation.Account == due.Account
                        && operation.Stage is not (RecoveryOperationStage.Prepared or RecoveryOperationStage.Abandoned))
                    .ToArray();
            }
            finally
            {
                _stateGate.Release();
            }

            RecoverySnapshotRead? read;
            string? failure;
            try
            {
                read = await _source.ReadAsync(scheduled.Venue, scheduled.Account!, cancellationToken)
                    .ConfigureAwait(false);
                failure = ValidateSnapshot(read, scheduled, scheduledOperations,
                    _source.MaximumSnapshotAge, _clock.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                read = null;
                failure = "Чтение восстановления было прервано.";
            }
            catch (Exception exception)
            {
                read = null;
                failure = $"Не удалось прочитать состояние: {exception.Message}";
            }

            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_storageReady)
                    return true;
                var current = FindVenueState(_state, scheduled.Venue, scheduled.Account);
                if (current is null || current.EpisodeId != scheduled.EpisodeId
                    || current.Phase == RecoveryReadPhase.Resolved)
                    return true;

                var now = _clock.UtcNow;
                if (failure is null && read is not null)
                {
                    var reconciled = Reconcile(_state, current, read, now);
                    SaveState(reconciled);
                    return true;
                }

                var failed = current with
                {
                    ReadInProgress = false,
                    ReadStartedAt = null,
                    LastReadFailure = failure
                };
                if (isAdditionalRead && failed.AdditionalReadsRemaining == 0)
                {
                    failed = failed with
                    {
                        Phase = RecoveryReadPhase.Periodic,
                        NextCheckAt = now.AddSeconds(failed.RecoveryCheckSeconds)
                    };
                    SaveState(EmitError(_state, failed, now));
                    return true;
                }
                else if (failed.Phase == RecoveryReadPhase.Periodic)
                {
                    failed = failed with { NextCheckAt = now.AddSeconds(failed.RecoveryCheckSeconds) };
                }
                SaveState(ReplaceVenue(_state, failed));
                return true;
            }
            finally
            {
                _stateGate.Release();
            }
        }
        finally
        {
            venueGate.Release();
        }
    }

    private void RestoreInterruptedWork()
    {
        var now = _clock.UtcNow;
        var state = _state;
        var operationsChanged = false;
        var operations = state.Operations.Select(operation =>
        {
            if (operation.Stage == RecoveryOperationStage.Prepared)
            {
                operationsChanged = true;
                return operation with
                {
                    Stage = RecoveryOperationStage.Abandoned,
                    CompletedAt = now,
                    Outcome = "Процесс перезапущен до участка передачи; подготовленное намерение не переносится."
                };
            }
            if (operation.Stage != RecoveryOperationStage.TransferStarted)
                return operation;
            operationsChanged = true;
            return operation with
            {
                Stage = RecoveryOperationStage.Unknown,
                CompletedAt = now,
                Outcome = "Процесс завершился в участке передачи; исход неизвестен."
            };
        }).ToList();
        if (operationsChanged)
            state = state with { Operations = operations };

        foreach (var operation in state.Operations.Where(operation => operation.Stage == RecoveryOperationStage.Unknown))
        {
            var current = FindVenueState(state, operation.Venue, operation.Account);
            if (current is { Phase: not RecoveryReadPhase.Resolved })
            {
                state = ReplaceOperation(state, operation with { EpisodeId = current.EpisodeId });
                continue;
            }

            state = CreateFailureState(state, operation.Venue, operation.Account,
                "Процесс завершился в участке передачи; исход операции неизвестен.", _settings, now,
                unknownScope: false);
            var episode = FindVenueState(state, operation.Venue, operation.Account)!;
            state = ReplaceOperation(state, operation with { EpisodeId = episode.EpisodeId });
        }

        foreach (var venue in state.Venues.Where(venue => venue.ReadInProgress).ToArray())
        {
            var recoveredRead = venue with { ReadInProgress = false, ReadStartedAt = null };
            if (venue.Phase == RecoveryReadPhase.AdditionalReads && venue.AdditionalReadsRemaining == 0)
            {
                recoveredRead = recoveredRead with
                {
                    Phase = RecoveryReadPhase.Periodic,
                    NextCheckAt = now.AddSeconds(venue.RecoveryCheckSeconds),
                    LastReadFailure = "Процесс завершился во время последнего дополнительного чтения."
                };
                state = EmitError(state, recoveredRead, now);
            }
            else
            {
                state = ReplaceVenue(state, recoveredRead);
            }
        }

        if (!ReferenceEquals(state, _state))
            SaveState(state);
    }

    private bool SaveState(RecoveryJournalState state)
    {
        _storageReady = false;
        _storageMessage = "Изменение журнала восстановления записывается.";
        _state = state;
        var result = _store.Save(state);
        if (!result.Saved)
        {
            _storageReady = false;
            _storageStatus = result.Status;
            _storageMessage = result.Message ?? "Не удалось зафиксировать изменение журнала восстановления.";
        }
        else
        {
            _storageStatus = RecoveryJournalStatus.Loaded;
            _storageMessage = null;
            _storageReady = true;
        }
        StatusChanged?.Invoke();
        return result.Saved;
    }

    private static RecoveryJournalState CreateFailureState(
        RecoveryJournalState state,
        string venue,
        string? account,
        string reason,
        AppSettings settings,
        DateTimeOffset now,
        bool unknownScope)
    {
        var current = FindVenueState(state, venue, account);
        if (current is { Phase: not RecoveryReadPhase.Resolved })
            return state;

        var episodeId = Guid.NewGuid().ToString("N");
        var phase = unknownScope
            ? RecoveryReadPhase.UnknownScope
            : settings.ReadRetryCount == 0 ? RecoveryReadPhase.Periodic : RecoveryReadPhase.AdditionalReads;
        var failure = new RecoveryVenueState(venue, account, episodeId, now, reason,
            phase,
            settings.ReadRetryCount, 0, settings.ReadRetryDelaySeconds, settings.RecoveryCheckSeconds,
            unknownScope || settings.ReadRetryCount == 0
                ? unknownScope ? null : now.AddSeconds(settings.RecoveryCheckSeconds)
                : now.AddSeconds(settings.ReadRetryDelaySeconds),
            false, false, false, null, null, null);
        state = ReplaceVenue(state, failure);
        return unknownScope || settings.ReadRetryCount == 0
            ? EmitError(state, failure, now)
            : state;
    }

    private static RecoveryJournalState EmitError(
        RecoveryJournalState state,
        RecoveryVenueState venue,
        DateTimeOffset now)
    {
        if (venue.ErrorEventEmitted)
            return ReplaceVenue(state, venue);

        var updated = venue with { ErrorEventEmitted = true };
        const string safeSummary = "Новые входы приостановлены; состояние требует полной сверки.";
        var recoveryEvent = new RecoveryEvent(venue.EpisodeId + ":error", RecoveryEventType.Error,
            venue.EpisodeId, venue.Venue, venue.Account, null, null, now,
            safeSummary, venue.LastReadFailure ?? venue.Reason);
        state = ReplaceVenue(state, updated);
        if (!state.Events.Any(existing => existing.EventId == recoveryEvent.EventId))
            state = state with { Events = [.. state.Events, recoveryEvent] };
        return state;
    }

    private static RecoveryJournalState Reconcile(
        RecoveryJournalState state,
        RecoveryVenueState venue,
        RecoverySnapshotRead read,
        DateTimeOffset now)
    {
        var operations = state.Operations.Select(operation =>
        {
            if (operation.Venue != venue.Venue || operation.Account != venue.Account)
                return operation;
            if (operation.Stage == RecoveryOperationStage.Prepared)
                return operation with
                {
                    Stage = RecoveryOperationStage.Abandoned,
                    CompletedAt = now,
                    ReconciledAt = now,
                    Outcome = "Подготовлено, но участок передачи не начинался."
                };
            if (operation.Stage is not (RecoveryOperationStage.TransferStarted or RecoveryOperationStage.Unknown))
                return operation;

            var outcome = read.OperationOutcomes![operation.OperationId].Outcome;
            return operation with
            {
                Stage = RecoveryOperationStage.Reconciled,
                ReconciledAt = now,
                Outcome = outcome.ToString()
            };
        }).ToList();

        var recovered = venue with
        {
            Phase = RecoveryReadPhase.Resolved,
            ReadInProgress = false,
            ReadStartedAt = null,
            NextCheckAt = null,
            RecoveryEventEmitted = true,
            ReconciledAt = now,
            LastReadFailure = null
        };
        state = ReplaceVenue(state with { Operations = operations }, recovered);
        if (!state.Events.Any(recoveryEvent => recoveryEvent.EventId == venue.EpisodeId + ":recovered"))
        {
            var recoveryEvent = new RecoveryEvent(venue.EpisodeId + ":recovered", RecoveryEventType.Recovered,
                venue.EpisodeId, venue.Venue, venue.Account, null, null, now,
                "Полная сверка завершена; незавершённые действия нельзя повторять автоматически.", null);
            state = state with { Events = [.. state.Events, recoveryEvent] };
        }
        return state;
    }

    private static string? ValidateSnapshot(
        RecoverySnapshotRead? read,
        RecoveryVenueState venue,
        IReadOnlyList<RecoveryOperation> operations,
        TimeSpan maximumAge,
        DateTimeOffset now)
    {
        if (venue.Venue == UnknownVenue || string.IsNullOrWhiteSpace(venue.Account))
            return "Площадка или счёт не определены.";
        if (read is null)
            return "Источник не вернул снимок.";
        if (!read.SessionAvailable)
            return read.FailureReason ?? "Сессия площадки недоступна.";
        if (read.Account is not { } account)
            return read.FailureReason ?? "Источник не вернул снимок счёта.";
        if (!string.Equals(account.Venue, venue.Venue, StringComparison.Ordinal)
            || !string.Equals(account.Account, venue.Account, StringComparison.Ordinal))
            return "Площадка или счёт изменились; пауза сохранена.";
        if (maximumAge <= TimeSpan.Zero)
            return "Для источника не задан срок свежести снимка.";
        var requiredAfter = venue.ReadStartedAt ?? venue.FailedAt;
        foreach (var operation in operations)
        {
            if (operation.TransferStartedAt is { } startedAt && startedAt > requiredAfter)
                requiredAfter = startedAt;
            if (operation.CompletedAt is { } completedAt && completedAt > requiredAfter)
                requiredAfter = completedAt;
        }
        if (account.Availability != AccountReadState.Available
            || !Fresh(account.ObservedAt, requiredAfter, now, maximumAge)
            || !Fresh(account.LastSuccessfulReadAt, requiredAfter, now, maximumAge)
            || string.IsNullOrWhiteSpace(account.Source))
            return account.AvailabilityFailure ?? "Снимок счёта неполный или устарел.";

        if (!CompleteSection(account.Balances, allowEmpty: false)
            || !CompleteSection(account.Positions, allowEmpty: true)
            || !CompleteSection(account.Orders, allowEmpty: true)
            || !CompleteSection(account.Protections, allowEmpty: true))
            return "Баланс, позиции, заявки или защита прочитаны не полностью.";

        if (!Fresh(account.Balances.ObservedAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Balances.LastSuccessfulAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Positions.ObservedAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Positions.LastSuccessfulAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Orders.ObservedAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Orders.LastSuccessfulAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Protections.ObservedAt, requiredAfter, now, maximumAge)
            || !Fresh(account.Protections.LastSuccessfulAt, requiredAfter, now, maximumAge))
            return "Один из обязательных разделов снимка устарел.";
        if (account.Positions.Value!.Any(position => position is null
                || string.IsNullOrWhiteSpace(position.Asset)
                || string.IsNullOrWhiteSpace(position.Contract)
                || string.IsNullOrWhiteSpace(position.QuantityUnit)
                || position.Quantity is not > 0))
            return "У позиции отсутствует точный рынок или размер.";
        if (account.Orders.Value!.Any(order => order is null
                || string.IsNullOrWhiteSpace(order.Asset)
                || string.IsNullOrWhiteSpace(order.Contract)
                || string.IsNullOrWhiteSpace(order.QuantityUnit)
                || order.RemainingQuantity is null))
            return "У открытой заявки неизвестен точный рынок или остаток.";
        if (!read.OpeningOrderRemaindersComplete)
            return "Полнота остатков открывающих заявок не подтверждена.";
        if (!read.ProtectionCoverageComplete)
            return "Покрытие позиций защитой не подтверждено.";
        if (!read.UnfinishedOperationsComplete)
            return "Список незавершённых операций неполон.";

        var unresolved = read.OperationOutcomes;
        if (unresolved is null)
            return "Результаты незавершённых операций недоступны.";
        foreach (var operation in operations)
        {
            if (operation.Stage is not (RecoveryOperationStage.TransferStarted or RecoveryOperationStage.Unknown))
                continue;
            if (!unresolved.TryGetValue(operation.OperationId, out var observation)
                || observation is null
                || !Enum.IsDefined(observation.Outcome)
                || observation.Outcome == RecoveryOperationOutcome.Unknown
                || observation.OperationId != operation.OperationId
                || observation.Venue != operation.Venue
                || observation.Account != operation.Account
                || observation.Market != operation.Market
                || observation.Operation != operation.Operation)
                return $"Исход операции {operation.OperationId} не установлен.";
        }

        return null;

        bool CompleteSection<T>(AccountSection<T>? section, bool allowEmpty) => section is not null
            && (section.State == AccountReadState.Available || (allowEmpty && section.State == AccountReadState.ConfirmedEmpty))
            && section.Value is not null
            && !string.IsNullOrWhiteSpace(section.Source)
            && section.ObservedAt is not null
            && section.LastSuccessfulAt is not null;
    }

    private static bool Fresh(
        DateTimeOffset? observedAt,
        DateTimeOffset requiredAfter,
        DateTimeOffset now,
        TimeSpan maximumAge) => observedAt is { } value
        && value >= requiredAfter
        && value <= now
        && now - value <= maximumAge;

    private static RecoveryVenueState? FindVenueState(
        RecoveryJournalState state,
        string venue,
        string? account) => state.Venues.SingleOrDefault(candidate => candidate.Venue == venue
            && candidate.Account == account);

    private static RecoveryJournalState ReplaceVenue(RecoveryJournalState state, RecoveryVenueState replacement)
    {
        var found = false;
        var venues = state.Venues.Select(venue =>
        {
            if (venue.Venue != replacement.Venue || venue.Account != replacement.Account)
                return venue;
            found = true;
            return replacement;
        }).ToList();
        if (!found)
            venues.Add(replacement);
        return state with { Venues = venues };
    }

    private static RecoveryJournalState ReplaceOperation(RecoveryJournalState state, RecoveryOperation replacement) =>
        state with
        {
            Operations = state.Operations.Select(operation => operation.OperationId == replacement.OperationId
                ? replacement : operation).ToList()
        };

    private static bool ValidRequest(RecoveryOperationRequest? request) => request is not null
        && !string.IsNullOrWhiteSpace(request.OperationId)
        && !string.IsNullOrWhiteSpace(request.Venue)
        && request.Venue != UnknownVenue
        && !string.IsNullOrWhiteSpace(request.Account)
        && !string.IsNullOrWhiteSpace(request.Market)
        && !string.IsNullOrWhiteSpace(request.Operation);

    private static bool ValidRetrySettings(AppSettings settings) => settings.ReadRetryCount >= 0
        && settings.ReadRetryDelaySeconds > 0
        && settings.RecoveryCheckSeconds > 0;

}
