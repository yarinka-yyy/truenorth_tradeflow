using TrueNorth.TradeFlow.Recovery;

namespace TrueNorth.TradeFlow.Notifications;

internal sealed class NotificationDispatcher : IDisposable
{
    private static readonly TimeSpan DefaultChannelTimeout = TimeSpan.FromSeconds(15);
    private readonly RecoveryCoordinator _recovery;
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationChannel> _channels;
    private readonly Dictionary<NotificationChannel, SemaphoreSlim> _channelGates = new()
    {
        [NotificationChannel.Windows] = new(1, 1),
        [NotificationChannel.Telegram] = new(1, 1)
    };
    private readonly TimeSpan _channelTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _queueLock = new();
    private AppSettings _settings;
    private System.Threading.Timer? _timer;
    private bool _started;
    private bool _scanRunning;
    private bool _scanAgain;
    private bool _disposed;

    internal NotificationDispatcher(
        RecoveryCoordinator recovery,
        IEnumerable<INotificationChannel> channels,
        AppSettings settings,
        TimeSpan? channelTimeout = null)
    {
        _recovery = recovery;
        _channels = channels.ToDictionary(channel => channel.Channel);
        _settings = settings.Copy();
        _channelTimeout = channelTimeout ?? DefaultChannelTimeout;
    }

    internal void UpdateSettings(AppSettings settings) =>
        Volatile.Write(ref _settings, settings.Copy());

    internal void Start(TimeSpan? reminderPollInterval = null)
    {
        lock (_queueLock)
        {
            if (_started || _disposed)
                return;
            _started = true;
            _recovery.StatusChanged += RecoveryStatusChanged;
            _timer = new System.Threading.Timer(_ => QueueScan(), null, TimeSpan.Zero,
                reminderPollInterval ?? TimeSpan.FromMinutes(1));
        }
    }

    internal Task<NotificationDispatchReport> SendTestAsync(CancellationToken cancellationToken = default) =>
        DispatchAsync(new NotificationMessage($"test:{Guid.NewGuid():N}", NotificationCategory.Error,
            "Это тестовое уведомление TrueNorth TradeFlow. Данные счёта не передаются.", IsTest: true),
            cancellationToken);

    internal Task<NotificationDispatchReport> DispatchForVerificationAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default) => DispatchAsync(message, cancellationToken);

    internal Task DispatchPendingForVerificationAsync() => RunPendingAsync();

    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_started)
                _recovery.StatusChanged -= RecoveryStatusChanged;
            _timer?.Dispose();
        }
        _shutdown.Cancel();
        foreach (var channel in _channels.Values.OfType<IDisposable>())
            channel.Dispose();
    }

    private async Task<NotificationDispatchReport> DispatchAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var settings = Volatile.Read(ref _settings);
        var categoryAllowed = IsCategoryAllowed(settings, message);
        var dispatches = new List<Task<NotificationChannelDeliveryResult>>(2);
        foreach (var channelKind in new[] { NotificationChannel.Windows, NotificationChannel.Telegram })
        {
            var allowed = settings.NotificationsEnabled && categoryAllowed;
            if (!allowed)
            {
                dispatches.Add(Task.FromResult(new NotificationChannelDeliveryResult(channelKind, null, !settings.NotificationsEnabled
                    ? "уведомления отключены в настройках"
                    : !categoryAllowed ? "категория отключена в настройках"
                    : "Telegram отключён в настройках")));
                continue;
            }

            if (!_channels.TryGetValue(channelKind, out var channel))
            {
                dispatches.Add(Task.FromResult(new NotificationChannelDeliveryResult(channelKind, null,
                    channelKind == NotificationChannel.Telegram ? "пара Telegram не настроена" : "канал Windows недоступен")));
                continue;
            }
            if (!IsChannelEnabled(channel))
            {
                dispatches.Add(Task.FromResult(new NotificationChannelDeliveryResult(channelKind, null,
                    channelKind == NotificationChannel.Telegram ? "Telegram отключён в настройках" : "канал Windows отключён")));
                continue;
            }
            if (!IsChannelConfigured(channel))
            {
                dispatches.Add(Task.FromResult(new NotificationChannelDeliveryResult(channelKind, null,
                    channelKind == NotificationChannel.Telegram ? "пара Telegram не настроена" : "канал Windows недоступен")));
                continue;
            }

            dispatches.Add(SendOneAsync(channel, message, cancellationToken));
        }
        return new(await Task.WhenAll(dispatches).ConfigureAwait(false));
    }

    private async Task<NotificationChannelDeliveryResult> SendOneAsync(
        INotificationChannel channel,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var gate = _channelGates[channel.Channel];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsChannelEnabled(channel))
                return new(channel.Channel, null, "канал отключён в настройках");
            if (!IsChannelConfigured(channel))
                return new(channel.Channel, null, "канал недоступен или не настроен");

            var start = message.IsReminder
                ? message.EpisodeId is { Length: > 0 } episodeId
                    ? await _recovery.BeginReminderNotificationAttemptAsync(message.Id, episodeId, channel.Channel,
                        cancellationToken).ConfigureAwait(false)
                    : new NotificationAttemptStartResult(NotificationAttemptStartStatus.ObsoleteReminder)
                : await _recovery.BeginNotificationAttemptAsync(message.Id, channel.Channel, cancellationToken)
                    .ConfigureAwait(false);
            if (start.Status == NotificationAttemptStartStatus.AlreadyAttempted)
                return DescribeExistingAttempt(channel.Channel, start.Existing);
            if (start.Status == NotificationAttemptStartStatus.RateLimited)
                return new(channel.Channel, null, $"Telegram ограничил частоту до {start.RetryAfterAt:HH:mm:ss} UTC");
            if (start.Status == NotificationAttemptStartStatus.ObsoleteReminder)
                return new(channel.Channel, null, "напоминание уже не актуально");
            if (start.Status != NotificationAttemptStartStatus.Started)
                return new(channel.Channel, NotificationAttemptStatus.Unknown, "журнал доставки недоступен; отправка остановлена");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            Task<NotificationChannelSendResult>? sendTask = null;
            NotificationChannelSendResult sendResult;
            try
            {
                timeout.CancelAfter(_channelTimeout);
                sendTask = channel.SendAsync(message, timeout.Token);
                sendResult = await sendTask.WaitAsync(_channelTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                ObserveFault(sendTask);
                sendResult = new(NotificationAttemptStatus.Unknown);
            }
            catch (OperationCanceledException)
            {
                timeout.Cancel();
                ObserveFault(sendTask);
                sendResult = new(NotificationAttemptStatus.Unknown);
            }
            catch (Exception)
            {
                sendResult = new(NotificationAttemptStatus.Unknown);
            }

            var saved = await _recovery.CompleteNotificationAttemptAsync(message.Id, channel.Channel,
                    sendResult.Status, sendResult.RetryAfterSeconds, cancellationToken)
                .ConfigureAwait(false);
            if (!saved)
                return new(channel.Channel, NotificationAttemptStatus.Unknown,
                    "исход неизвестен; результат не записан в журнал доставки");
            return DescribeResult(channel.Channel, sendResult);
        }
        catch (OperationCanceledException)
        {
            return new(channel.Channel, NotificationAttemptStatus.Unknown, "исход неизвестен; автоматический повтор запрещён");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunPendingAsync()
    {
        try
        {
            await _recovery.ProcessDueFailureRemindersAsync(_shutdown.Token).ConfigureAwait(false);
            var events = _recovery.State.Events;
            foreach (var recoveryEvent in events)
            {
                if (_shutdown.IsCancellationRequested)
                    return;
                await DispatchAsync(NotificationMessageFactory.FromRecoveryEvent(recoveryEvent), _shutdown.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Notification failures must not flow back into recovery or create more notifications.
        }
    }

    private void QueueScan()
    {
        lock (_queueLock)
        {
            if (_disposed || !_started)
                return;
            if (_scanRunning)
            {
                _scanAgain = true;
                return;
            }
            _scanRunning = true;
        }
        _ = Task.Run(ProcessQueuedScansAsync);
    }

    private async Task ProcessQueuedScansAsync()
    {
        while (true)
        {
            await RunPendingAsync().ConfigureAwait(false);
            lock (_queueLock)
            {
                if (_disposed || !_scanAgain)
                {
                    _scanAgain = false;
                    _scanRunning = false;
                    return;
                }
                _scanAgain = false;
            }
        }
    }

    private void RecoveryStatusChanged() => QueueScan();

    private static bool IsCategoryAllowed(AppSettings settings, NotificationMessage message)
    {
        if (message.IsTest)
            return true;
        if (message.IsReminder && !settings.FailureRemindersEnabled)
            return false;
        return message.Category switch
        {
            NotificationCategory.Opening => settings.OpeningNotifications,
            NotificationCategory.Closing => settings.ClosingNotifications,
            NotificationCategory.OrderChange => settings.OrderChangeNotifications,
            NotificationCategory.Error => settings.ErrorNotifications,
            NotificationCategory.Recovery => settings.RecoveryNotifications,
            _ => false
        };
    }

    private static bool IsChannelConfigured(INotificationChannel channel)
    {
        try { return channel.IsConfigured; }
        catch (Exception) { return false; }
    }

    private static bool IsChannelEnabled(INotificationChannel channel)
    {
        try { return channel.IsEnabled; }
        catch (Exception) { return false; }
    }

    private static NotificationChannelDeliveryResult DescribeExistingAttempt(
        NotificationChannel channel,
        NotificationDeliveryAttempt? attempt) => attempt?.Status switch
    {
        NotificationAttemptStatus.PassedToWindows => new(channel, attempt.Status, "передано Windows"),
        NotificationAttemptStatus.AcceptedByTelegram => new(channel, attempt.Status, "принято Telegram"),
        NotificationAttemptStatus.Rejected => new(channel, attempt.Status, "известный отказ; автоматический повтор запрещён"),
        _ => new(channel, NotificationAttemptStatus.Unknown, "исход неизвестен; автоматический повтор запрещён")
    };

    private static NotificationChannelDeliveryResult DescribeResult(
        NotificationChannel channel,
        NotificationChannelSendResult result) => result.Status switch
    {
        NotificationAttemptStatus.PassedToWindows => new(channel, result.Status, "передано Windows"),
        NotificationAttemptStatus.AcceptedByTelegram => new(channel, result.Status, "принято Telegram"),
        NotificationAttemptStatus.Rejected when channel == NotificationChannel.Telegram
            && result.RetryAfterSeconds is int seconds
            => new(channel, result.Status, $"Telegram отклонил; ограничение частоты на {seconds} сек."),
        NotificationAttemptStatus.Rejected => new(channel, result.Status, "известный отказ Telegram"),
        _ => new(channel, NotificationAttemptStatus.Unknown, "исход неизвестен; автоматический повтор запрещён")
    };

    private static void ObserveFault(Task<NotificationChannelSendResult>? sendTask)
    {
        if (sendTask is null)
            return;
        _ = sendTask.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
