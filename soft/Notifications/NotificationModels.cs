using TrueNorth.TradeFlow.Recovery;

namespace TrueNorth.TradeFlow.Notifications;

internal enum NotificationCategory
{
    Opening,
    Closing,
    OrderChange,
    Error,
    Recovery
}

internal enum NotificationChannel
{
    Windows,
    Telegram
}

internal enum NotificationAttemptStatus
{
    InProgress,
    PassedToWindows,
    AcceptedByTelegram,
    Rejected,
    Unknown
}

internal enum NotificationAttemptStartStatus
{
    Started,
    AlreadyAttempted,
    RateLimited,
    ObsoleteReminder,
    StorageUnavailable
}

internal sealed record NotificationAttemptStartResult(
    NotificationAttemptStartStatus Status,
    NotificationDeliveryAttempt? Existing = null,
    DateTimeOffset? RetryAfterAt = null);

internal sealed record NotificationDeliveryAttempt(
    string MessageId,
    NotificationChannel Channel,
    NotificationAttemptStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null);

internal sealed record NotificationMessage(
    string Id,
    NotificationCategory Category,
    string SafeText,
    bool IsReminder = false,
    bool IsTest = false,
    string? EpisodeId = null);

internal sealed record NotificationChannelSendResult(
    NotificationAttemptStatus Status,
    int? RetryAfterSeconds = null);

internal sealed record NotificationChannelDeliveryResult(
    NotificationChannel Channel,
    NotificationAttemptStatus? Status,
    string SafeStatus);

internal sealed record NotificationDispatchReport(
    IReadOnlyList<NotificationChannelDeliveryResult> Channels);

internal interface INotificationChannel
{
    NotificationChannel Channel { get; }
    bool IsEnabled { get; }
    bool IsConfigured { get; }
    Task<NotificationChannelSendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}

internal sealed class TelegramCredentials
{
    internal TelegramCredentials(string token, string chatId)
    {
        Token = token;
        ChatId = chatId;
    }

    internal string Token { get; }
    internal string ChatId { get; }

    public override string ToString() => "[Telegram credentials]";
}

internal static class NotificationMessageFactory
{
    private static readonly IReadOnlyDictionary<string, string> VerifiedVenues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hyperliquid"] = "Hyperliquid",
            ["Ondo Perps"] = "Ondo Perps",
            ["Lighter"] = "Lighter",
            ["Lighter Robinhood"] = "Lighter Robinhood"
        };

    internal static NotificationMessage FromRecoveryEvent(RecoveryEvent recoveryEvent)
    {
        var venue = VerifiedVenues.TryGetValue(recoveryEvent.Venue, out var verifiedName)
            ? verifiedName + ": "
            : string.Empty;
        return new(recoveryEvent.EventId,
            recoveryEvent.Type == RecoveryEventType.Error ? NotificationCategory.Error : NotificationCategory.Recovery,
            venue + recoveryEvent.SafeSummary,
            recoveryEvent.IsReminder,
            EpisodeId: recoveryEvent.EpisodeId);
    }
}
