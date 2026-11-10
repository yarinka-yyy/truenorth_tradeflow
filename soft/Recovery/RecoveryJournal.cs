using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueNorth.TradeFlow.Notifications;

namespace TrueNorth.TradeFlow.Recovery;

internal enum RecoveryJournalStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedVersion,
    Unavailable,
    MissingWithPreviousVersion,
    Failed
}

internal enum RecoveryReadPhase
{
    AdditionalReads,
    Periodic,
    UnknownScope,
    Resolved
}

internal enum RecoveryOperationStage
{
    Prepared,
    Abandoned,
    TransferStarted,
    Confirmed,
    NotExecuted,
    Unknown,
    Reconciled
}

internal enum RecoveryEventType
{
    Error,
    Recovered
}

internal sealed record RecoveryVenueState(
    [property: JsonRequired] string Venue,
    [property: JsonRequired] string? Account,
    [property: JsonRequired] string EpisodeId,
    [property: JsonRequired] DateTimeOffset FailedAt,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] RecoveryReadPhase Phase,
    [property: JsonRequired] int AdditionalReadsRemaining,
    [property: JsonRequired] int AdditionalReadsStarted,
    [property: JsonRequired] int RetryDelaySeconds,
    [property: JsonRequired] int RecoveryCheckSeconds,
    [property: JsonRequired] DateTimeOffset? NextCheckAt,
    [property: JsonRequired] bool ErrorEventEmitted,
    [property: JsonRequired] bool RecoveryEventEmitted,
    [property: JsonRequired] bool ReadInProgress,
    [property: JsonRequired] DateTimeOffset? ReconciledAt,
    [property: JsonRequired] string? LastReadFailure,
    DateTimeOffset? ReadStartedAt = null,
    DateTimeOffset? NextReminderAt = null,
    int ReminderSequence = 0);

internal sealed record RecoveryOperation(
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string Venue,
    [property: JsonRequired] string Account,
    [property: JsonRequired] string Market,
    [property: JsonRequired] string Operation,
    [property: JsonRequired] string? EpisodeId,
    [property: JsonRequired] DateTimeOffset PreparedAt,
    [property: JsonRequired] DateTimeOffset? DeadlineAt,
    [property: JsonRequired] DateTimeOffset? TransferStartedAt,
    [property: JsonRequired] DateTimeOffset? CompletedAt,
    [property: JsonRequired] DateTimeOffset? ReconciledAt,
    [property: JsonRequired] RecoveryOperationStage Stage,
    [property: JsonRequired] string? Outcome);

internal sealed record RecoveryEvent(
    [property: JsonRequired] string EventId,
    [property: JsonRequired] RecoveryEventType Type,
    [property: JsonRequired] string EpisodeId,
    [property: JsonRequired] string Venue,
    [property: JsonRequired] string? Account,
    [property: JsonRequired] string? Market,
    [property: JsonRequired] string? OperationId,
    [property: JsonRequired] DateTimeOffset At,
    [property: JsonRequired] string Message,
    string? InternalDetail = null,
    bool IsReminder = false)
{
    [JsonIgnore]
    internal string SafeSummary => Type switch
    {
        RecoveryEventType.Error => "Новые входы приостановлены; состояние требует полной сверки.",
        RecoveryEventType.Recovered => "Полная сверка завершена; незавершённые действия нельзя повторять автоматически.",
        _ => "Состояние восстановления требует проверки."
    };
}

internal sealed record RecoveryJournalState
{
    public List<RecoveryVenueState> Venues { get; init; } = [];
    public List<RecoveryOperation> Operations { get; init; } = [];
    public List<RecoveryEvent> Events { get; init; } = [];
    public List<NotificationDeliveryAttempt> NotificationAttempts { get; init; } = [];
    public DateTimeOffset? TelegramRetryAfterAt { get; init; }
}

internal sealed record RecoveryJournalEnvelope(int FormatVersion, RecoveryJournalState State);

internal sealed record RecoveryJournalLoadResult(
    RecoveryJournalStatus Status,
    RecoveryJournalState State,
    string? Message = null);

internal sealed record RecoveryJournalSaveResult(
    bool Saved,
    RecoveryJournalStatus Status,
    string? Message = null);

internal sealed class RecoveryJournalStore
{
    internal const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly Action<string, string, string?>? _commitOverride;

    internal RecoveryJournalStore(string path, Action<string, string, string?>? commitOverride = null)
    {
        _path = Path.GetFullPath(path);
        _commitOverride = commitOverride;
    }

    internal string PathName => _path;
    internal string PreviousVersionPath => _path + ".bak";

    internal RecoveryJournalLoadResult Load()
    {
        var primary = Read(_path);
        if (primary.Status == RecoveryJournalStatus.Missing)
        {
            var backup = Read(PreviousVersionPath);
            return backup.Status == RecoveryJournalStatus.Missing
                ? new(RecoveryJournalStatus.Missing, new())
                : new(RecoveryJournalStatus.MissingWithPreviousVersion, new(),
                    "Основной журнал отсутствует, но найдена предыдущая версия. Состояние не сброшено автоматически.");
        }

        return new(primary.Status, primary.State ?? new(), primary.Message);
    }

    internal RecoveryJournalSaveResult Save(RecoveryJournalState state)
    {
        if (!Validate(state, out var validationError))
            return new(false, RecoveryJournalStatus.Failed, validationError);

        var current = Read(_path);
        if (current.Status is RecoveryJournalStatus.Corrupt or RecoveryJournalStatus.UnsupportedVersion
            or RecoveryJournalStatus.Unavailable)
            return new(false, current.Status, current.Message);

        var backup = Read(PreviousVersionPath);
        if (backup.Status is RecoveryJournalStatus.Corrupt or RecoveryJournalStatus.UnsupportedVersion
            or RecoveryJournalStatus.Unavailable)
            return new(false, backup.Status,
                backup.Message ?? "Предыдущая версия журнала не может быть безопасно заменена.");
        if (current.Status == RecoveryJournalStatus.Missing && backup.Status != RecoveryJournalStatus.Missing)
            return new(false, RecoveryJournalStatus.MissingWithPreviousVersion,
                "Основной журнал отсутствует, а предыдущая версия сохранена. Запись остановлена.");

        string? temporaryPath = null;
        string? backupTemporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new IOException("Не удалось определить каталог журнала восстановления.");
            Directory.CreateDirectory(directory);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new RecoveryJournalEnvelope(CurrentFormatVersion, state), JsonOptions);
            if (Parse(bytes).Status != RecoveryJournalStatus.Loaded)
                return new(false, RecoveryJournalStatus.Failed, "Подготовленный журнал не прошёл проверку.");

            temporaryPath = Path.Combine(directory,
                $"{Path.GetFileName(_path)}.tmp.{Guid.NewGuid():N}");
            WriteFlushed(temporaryPath, bytes);

            if (current.Status == RecoveryJournalStatus.Loaded)
            {
                if (current.Bytes is null)
                    return new(false, RecoveryJournalStatus.Failed, "Исходный журнал нельзя проверить перед заменой.");

                backupTemporaryPath = Path.Combine(directory,
                    $"{Path.GetFileName(_path)}.backup.tmp.{Guid.NewGuid():N}");
                WriteFlushed(backupTemporaryPath, current.Bytes);
            }

            if (!SameFile(current, Read(_path)) || !SameFile(backup, Read(PreviousVersionPath)))
                return new(false, RecoveryJournalStatus.Failed,
                    "Журнал или предыдущая версия изменились во время сохранения.");

            if (current.Status == RecoveryJournalStatus.Loaded)
            {
                File.Move(backupTemporaryPath!, PreviousVersionPath,
                    overwrite: backup.Status == RecoveryJournalStatus.Loaded);
                backupTemporaryPath = null;
                if (!SameFile(current, Read(_path)))
                    return new(false, RecoveryJournalStatus.Failed,
                        "Основной журнал изменился перед публикацией новой версии.");
            }

            if (_commitOverride is null)
            {
                if (current.Status == RecoveryJournalStatus.Missing)
                    File.Move(temporaryPath, _path);
                else
                    File.Move(temporaryPath, _path, overwrite: true);
            }
            else
            {
                _commitOverride(temporaryPath, _path,
                    current.Status == RecoveryJournalStatus.Loaded ? PreviousVersionPath : null);
            }

            return new(true, RecoveryJournalStatus.Loaded);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or SecurityException or ArgumentException or NotSupportedException)
        {
            return new(false, RecoveryJournalStatus.Failed,
                $"Не удалось сохранить журнал восстановления. Основной файл не заменён: {exception.Message}");
        }
        finally
        {
            DeleteTemporary(temporaryPath);
            DeleteTemporary(backupTemporaryPath);
        }
    }

    private static void WriteFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteTemporary(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool SameFile(FileRead left, FileRead right) => left.Status == right.Status
        && (left.Bytes is null ? right.Bytes is null : right.Bytes is not null
            && left.Bytes.AsSpan().SequenceEqual(right.Bytes));

    private static FileRead Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return new(RecoveryJournalStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(RecoveryJournalStatus.Missing);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new(RecoveryJournalStatus.Unavailable, Message:
                $"Журнал восстановления нельзя прочитать: {exception.Message}");
        }

        return Parse(bytes);
    }

    private static FileRead Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("formatVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version))
                return new(RecoveryJournalStatus.Corrupt, bytes,
                    Message: "Журнал восстановления повреждён или не содержит версию формата.");
            if (version != CurrentFormatVersion)
                return new(RecoveryJournalStatus.UnsupportedVersion, bytes,
                    Message: $"Версия журнала восстановления {version} не поддерживается.");
            if (!root.TryGetProperty("state", out var stateElement)
                || stateElement.ValueKind != JsonValueKind.Object
                || !stateElement.TryGetProperty("venues", out var venuesElement)
                || venuesElement.ValueKind != JsonValueKind.Array
                || !stateElement.TryGetProperty("operations", out var operationsElement)
                || operationsElement.ValueKind != JsonValueKind.Array
                || !stateElement.TryGetProperty("events", out var eventsElement)
                || eventsElement.ValueKind != JsonValueKind.Array)
                return new(RecoveryJournalStatus.Corrupt, bytes,
                    Message: "Журнал восстановления не содержит всех обязательных разделов.");

            var envelope = JsonSerializer.Deserialize<RecoveryJournalEnvelope>(bytes, JsonOptions);
            string? error = null;
            if (envelope?.State is null || !Validate(envelope.State, out error))
                return new(RecoveryJournalStatus.Corrupt, bytes,
                    Message: error ?? "Журнал восстановления не содержит состояния.");

            return new(RecoveryJournalStatus.Loaded, bytes, envelope.State);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                           or FormatException or OverflowException or ArgumentException)
        {
            return new(RecoveryJournalStatus.Corrupt, bytes,
                Message: $"Журнал восстановления повреждён: {exception.Message}");
        }
    }

    private static bool Validate(RecoveryJournalState? state, out string? error)
    {
        error = null;
        if (state?.Venues is null || state.Operations is null || state.Events is null
            || state.NotificationAttempts is null)
        {
            error = "Журнал восстановления содержит отсутствующий список.";
            return false;
        }

        if (state.Venues.Any(venue => venue is null
                || string.IsNullOrWhiteSpace(venue.Venue)
                || string.IsNullOrWhiteSpace(venue.EpisodeId)
                || string.IsNullOrWhiteSpace(venue.Reason)
                || !Enum.IsDefined(venue.Phase)
                || venue.AdditionalReadsRemaining < 0
                || venue.AdditionalReadsStarted < 0
                || venue.RetryDelaySeconds <= 0
                || venue.RecoveryCheckSeconds <= 0
                || (venue.Phase is (RecoveryReadPhase.AdditionalReads or RecoveryReadPhase.Periodic)
                    && venue.NextCheckAt is null)
                || (venue.Phase == RecoveryReadPhase.Resolved
                    && (venue.NextCheckAt is not null || venue.ReconciledAt is null || !venue.RecoveryEventEmitted))
                || (venue.Phase == RecoveryReadPhase.UnknownScope && venue.NextCheckAt is not null)
                || (venue.Phase == RecoveryReadPhase.Periodic && !venue.ErrorEventEmitted)
                || (venue.Phase == RecoveryReadPhase.AdditionalReads
                    && venue.AdditionalReadsRemaining == 0 && !venue.ReadInProgress)
                || (!venue.ReadInProgress && venue.ReadStartedAt is not null)
                || venue.ReminderSequence < 0
                || (venue.NextReminderAt is not null
                    && (venue.Phase == RecoveryReadPhase.Resolved || !venue.ErrorEventEmitted))))
        {
            error = "Журнал восстановления содержит недопустимую паузу или срок.";
            return false;
        }

        if (state.Venues.GroupBy(venue => (venue.Venue, venue.Account)).Any(group => group.Count() > 1)
            || state.Operations.Any(operation => operation is null
                || string.IsNullOrWhiteSpace(operation.OperationId)
                || string.IsNullOrWhiteSpace(operation.Venue)
                || string.IsNullOrWhiteSpace(operation.Account)
                || string.IsNullOrWhiteSpace(operation.Market)
                || string.IsNullOrWhiteSpace(operation.Operation)
                || !Enum.IsDefined(operation.Stage))
            || state.Operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).Count() != state.Operations.Count
            || state.Operations.Any(operation => operation.Stage switch
            {
                RecoveryOperationStage.Prepared => operation.EpisodeId is not null
                    || operation.TransferStartedAt is not null || operation.CompletedAt is not null
                    || operation.ReconciledAt is not null,
                RecoveryOperationStage.Abandoned => operation.TransferStartedAt is not null
                    || operation.CompletedAt is null || operation.EpisodeId is not null,
                RecoveryOperationStage.TransferStarted => operation.EpisodeId is not null
                    || operation.TransferStartedAt is null
                    || operation.CompletedAt is not null || operation.ReconciledAt is not null,
                RecoveryOperationStage.Confirmed or RecoveryOperationStage.NotExecuted
                    => operation.TransferStartedAt is null
                    || operation.CompletedAt is null || operation.ReconciledAt is not null,
                RecoveryOperationStage.Unknown => string.IsNullOrWhiteSpace(operation.EpisodeId)
                    || operation.TransferStartedAt is null || operation.CompletedAt is null
                    || operation.ReconciledAt is not null,
                RecoveryOperationStage.Reconciled => string.IsNullOrWhiteSpace(operation.EpisodeId)
                    || operation.TransferStartedAt is null
                    || operation.CompletedAt is null || operation.ReconciledAt is null,
                _ => true
            }))
        {
            error = "Журнал восстановления содержит недопустимый или повторяющийся ключ операции.";
            return false;
        }

        if (state.Events.Any(recoveryEvent => recoveryEvent is null
                || string.IsNullOrWhiteSpace(recoveryEvent.EventId)
                || string.IsNullOrWhiteSpace(recoveryEvent.EpisodeId)
                || string.IsNullOrWhiteSpace(recoveryEvent.Venue)
                || string.IsNullOrWhiteSpace(recoveryEvent.Message)
                || !Enum.IsDefined(recoveryEvent.Type))
            || state.Events.Select(recoveryEvent => recoveryEvent.EventId).Distinct(StringComparer.Ordinal).Count() != state.Events.Count)
        {
            error = "Журнал восстановления содержит недопустимое или повторяющееся событие.";
            return false;
        }

        if (state.Events.Any(recoveryEvent => recoveryEvent.IsReminder && recoveryEvent.Type != RecoveryEventType.Error)
            || state.NotificationAttempts.Any(attempt => attempt is null
                || string.IsNullOrWhiteSpace(attempt.MessageId)
                || attempt.MessageId.Length > 256
                || attempt.MessageId.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not (':' or '-' or '_' or '.'))
                || !Enum.IsDefined(attempt.Channel)
                || !Enum.IsDefined(attempt.Status)
                || (attempt.Status == NotificationAttemptStatus.InProgress && attempt.CompletedAt is not null)
                || (attempt.Status != NotificationAttemptStatus.InProgress && attempt.CompletedAt is null))
            || state.NotificationAttempts.Select(attempt => (attempt.MessageId, attempt.Channel))
                .Distinct().Count() != state.NotificationAttempts.Count)
        {
            error = "Журнал восстановления содержит недопустимую попытку уведомления.";
            return false;
        }

        if (state.Venues.Any(venue => venue.ErrorEventEmitted
                != state.Events.Any(recoveryEvent => recoveryEvent.EpisodeId == venue.EpisodeId
                    && recoveryEvent.Type == RecoveryEventType.Error)
            || venue.RecoveryEventEmitted
                != state.Events.Any(recoveryEvent => recoveryEvent.EpisodeId == venue.EpisodeId
                    && recoveryEvent.Type == RecoveryEventType.Recovered)))
        {
            error = "События журнала восстановления не совпадают с состояниями эпизодов.";
            return false;
        }

        return true;
    }

    private sealed record FileRead(
        RecoveryJournalStatus Status,
        byte[]? Bytes = null,
        RecoveryJournalState? State = null,
        string? Message = null);
}
