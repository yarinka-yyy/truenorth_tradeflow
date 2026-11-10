using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Recovery;
using Forms = System.Windows.Forms;

namespace TrueNorth.TradeFlow.Verification;

internal static class NotificationChecks
{
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private const string FakeToken = "123456:abcdefghijklmnopqrstuvwxyz012345";
    private const string FakeChatId = "-1001234567890";

    internal static void CheckCore(Action<bool, string> check, string root)
    {
        CheckCredentialStore(check, root).GetAwaiter().GetResult();
        CheckCategoryFiltersAndOneShot(check, root).GetAwaiter().GetResult();
        CheckFailureRecoveryAndReminders(check, root).GetAwaiter().GetResult();
        CheckIndependentChannelsAndUnknownAttempt(check, root).GetAwaiter().GetResult();
        CheckTelegramTransportAndRateLimit(check, root).GetAwaiter().GetResult();
    }

    internal static void CheckNativeWpf(Action<bool, string> check, string root, bool exerciseWindowsBalloon = false)
    {
        var profileDirectory = Path.Combine(root, "notification-ui");
        var settingsPath = Path.Combine(profileDirectory, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var profile = new SettingsProfile { Settings = Settings() };
        check(settingsStore.Save(profile).Succeeded, "notification UI uses an isolated temporary settings profile");
        var loaded = settingsStore.Load();
        var credentialStore = new TelegramCredentialStore(Path.Combine(profileDirectory, "telegram-credentials.dat"));
        check(credentialStore.SavePair(FakeToken, FakeChatId, enabled: true),
            "notification UI test credentials are protected in its temporary profile");

        var recovery = new RecoveryCoordinator(new RecoveryJournalStore(Path.Combine(profileDirectory, "recovery-state.json")),
            loaded.Profile.Settings, new FakeClock(InitialTime));
        using var icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true };
        INotificationChannel windows = exerciseWindowsBalloon
            ? new WindowsNotificationChannel(icon, Dispatcher.CurrentDispatcher)
            : new FakeNotificationChannel(NotificationChannel.Windows);
        var telegram = new FakeNotificationChannel(NotificationChannel.Telegram)
        {
            IsConfigured = credentialStore.HasCredentials
        };
        using var dispatcher = new NotificationDispatcher(recovery, [windows, telegram], loaded.Profile.Settings);
        var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(settingsStore, loaded, controller,
            recoveryCoordinator: recovery,
            notificationDispatcher: dispatcher,
            telegramCredentialStore: credentialStore)
        {
            Width = 760,
            Height = 560
        };

        try
        {
            window.Show();
            Get<ListBox>(window, "NavigationList").SelectedIndex = 1;
            window.UpdateLayout();
            PumpDispatcher();
            var scrollViewer = Get<ScrollViewer>(window, "MainScrollViewer");
            scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight);
            window.UpdateLayout();
            PumpDispatcher();

            var tokenBox = Get<PasswordBox>(window, "TelegramTokenPasswordBox");
            var chatBox = Get<TextBox>(window, "TelegramChatIdTextBox");
            var telegramEnabled = Get<CheckBox>(window, "TelegramEnabledCheckBox");
            var sendButton = Get<Button>(window, "SendNotificationTestButton");
            var testStatus = Get<TextBlock>(window, "NotificationTestStatusText");
            check(window.Width == 760 && window.Height == 560
                  && sendButton.IsVisible && sendButton.ActualWidth > 0 && sendButton.ActualHeight > 0
                  && tokenBox.IsVisible && tokenBox.ActualWidth > 0 && chatBox.ActualWidth > 0,
                "Telegram settings and test action are reachable at 760 by 560 after scrolling");
            check(tokenBox.Password.Length == 0 && chatBox.Text.Length == 0 && telegramEnabled.IsChecked == true,
                "saved token and chat are never restored into the editable controls");
            check(tokenBox.Focusable && tokenBox.IsTabStop && tokenBox.FocusVisualStyle is not null
                  && chatBox.Focusable && chatBox.IsTabStop && chatBox.FocusVisualStyle is not null
                  && sendButton.Focusable && sendButton.IsTabStop && sendButton.FocusVisualStyle is not null
                  && sendButton.IsHitTestVisible,
                "Telegram fields and test action support keyboard focus, visible focus styling, and mouse hit testing");

            Keyboard.Focus(tokenBox);
            check(tokenBox.IsKeyboardFocusWithin, "the masked token field receives keyboard focus");
            Get<CheckBox>(window, "NotificationsEnabledCheckBox").IsChecked = false;
            Keyboard.Focus(sendButton);
            check(sendButton.IsKeyboardFocused, "the test action receives keyboard focus independently of pointer input");
            sendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => testStatus.Text.Contains("Windows:", StringComparison.Ordinal)
                          && testStatus.Text.Contains("Telegram:", StringComparison.Ordinal));
            check(testStatus.Text.Contains("Windows: передано Windows", StringComparison.Ordinal)
                  && testStatus.Text.Contains("Telegram: принято Telegram", StringComparison.Ordinal),
                "the test button shows separate saved-configuration results for both channels");
            if (exerciseWindowsBalloon)
                PumpFor(TimeSpan.FromSeconds(7));
            check(telegram.Sent.Count == 1
                  && telegram.Sent.Single().SafeText
                      == "Это тестовое уведомление TrueNorth TradeFlow. Данные счёта не передаются.",
                "the explicit test uses a fixed message without account data and ignores unsaved drafts");
            check(!testStatus.Text.Contains("тестовое уведомление", StringComparison.Ordinal),
                "channel status reports outcomes without copying message content");

            telegramEnabled.IsChecked = false;
            Get<Button>(window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!credentialStore.IsEnabled && credentialStore.HasCredentials
                  && telegram.Sent.Count == 1
                  && !settingsStore.Load().Profile.Settings.NotificationsEnabled,
                "saving an explicit Telegram disable preserves the protected pair and never sends a message");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
        }
    }

    private static async Task CheckCredentialStore(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "notification-credentials", "telegram-credentials.dat");
        var store = new TelegramCredentialStore(path);
        check(store.SavePair(FakeToken, FakeChatId, enabled: true) && store.TryLoad(out var loaded)
              && loaded?.Token == FakeToken && loaded.ChatId == FakeChatId,
            "DPAPI CurrentUser stores and reloads the token and chat as one pair");
        var protectedBytes = File.ReadAllBytes(path);
        var protectedText = Encoding.UTF8.GetString(protectedBytes);
        check(!protectedText.Contains(FakeToken, StringComparison.Ordinal)
              && !protectedText.Contains(FakeChatId, StringComparison.Ordinal),
            "the protected credential file contains neither plaintext token nor chat ID");

        var settingsPath = Path.Combine(root, "notification-credentials", "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var profile = new SettingsProfile { Settings = Settings() };
        check(settingsStore.Save(profile).Succeeded, "ordinary settings can be saved separately from Telegram credentials");
        var settingsText = File.ReadAllText(settingsPath);
        check(!settingsText.Contains(FakeToken, StringComparison.Ordinal)
              && !settingsText.Contains(FakeChatId, StringComparison.Ordinal)
              && !settingsText.Contains("api.telegram.org", StringComparison.Ordinal)
              && !settingsText.Contains("telegramEnabled", StringComparison.Ordinal),
            "ordinary settings JSON contains no Telegram token, chat ID, endpoint, or enable state");

        var oldBytes = File.ReadAllBytes(path);
        var failedUpdate = new TelegramCredentialStore(path, (_, _) => throw new IOException("synthetic write failure"));
        check(!failedUpdate.SavePair("654321:zyxwvutsrqponmlkjihgfedcba987654", "987654321", enabled: true)
              && File.ReadAllBytes(path).SequenceEqual(oldBytes)
              && store.TryLoad(out var stillSaved) && stillSaved?.Token == FakeToken && stillSaved.ChatId == FakeChatId,
            "a failed protected-pair update leaves the previously saved pair unchanged");

        var absentPath = Path.Combine(root, "notification-credentials", "explicitly-disabled.dat");
        var absentStore = new TelegramCredentialStore(absentPath);
        check(absentStore.SetEnabled(false) && File.Exists(absentPath) && !absentStore.IsEnabled
              && !new TelegramCredentialStore(absentPath).IsEnabled,
            "explicit Telegram disable with no prior file persists an encrypted disabled state");

        var lockedPath = Path.Combine(root, "notification-credentials", "locked-credentials.dat");
        var lockedStore = new TelegramCredentialStore(lockedPath);
        check(lockedStore.SavePair(FakeToken, FakeChatId, enabled: true),
            "locked-file fixture starts with an enabled protected pair");
        var lockedBytes = File.ReadAllBytes(lockedPath);
        var failedClosed = false;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            failedClosed = !lockedStore.SetEnabled(false) && !lockedStore.IsEnabled;
        check(failedClosed && File.ReadAllBytes(lockedPath).SequenceEqual(lockedBytes)
              && new TelegramCredentialStore(lockedPath).IsEnabled,
            "a locked Telegram file blocks sends in memory, returns failure, and preserves the encrypted pair");
        check(lockedStore.SetEnabled(false) && !lockedStore.IsEnabled && lockedStore.HasCredentials
              && !new TelegramCredentialStore(lockedPath).IsEnabled,
            "Telegram can be disabled persistently after the credential-file lock is released");

        check(store.SetEnabled(false) && !store.IsEnabled && store.HasCredentials,
            "Telegram disable updates the protected state while preserving its credential pair");
        var disabledHandler = new QueuedHttpHandler(Response(HttpStatusCode.OK, "{\"ok\":true}"));
        using (var telegramChannel = new TelegramNotificationChannel(store, disabledHandler, TimeSpan.FromSeconds(1)))
        using (var disabledDispatcher = new NotificationDispatcher(
                   NewCoordinator(root, "credential-disable", Settings(), new FakeClock(InitialTime)),
                   [new FakeNotificationChannel(NotificationChannel.Windows), telegramChannel], Settings()))
        {
            var disabledReport = await disabledDispatcher.DispatchForVerificationAsync(
                new NotificationMessage("credential:disable", NotificationCategory.Error, "safe message"))
                .ConfigureAwait(false);
            check(disabledHandler.Calls == 0
                  && disabledReport.Channels.Single(item => item.Channel == NotificationChannel.Telegram).SafeStatus
                      .Contains("отключён", StringComparison.Ordinal),
                "the protected disabled state blocks Telegram before the transport is called");
        }
        check(store.SetEnabled(true) && store.IsEnabled
              && store.TryLoad(out var reenabled) && reenabled?.Token == FakeToken && reenabled.ChatId == FakeChatId,
            "Telegram can be explicitly re-enabled with the same protected credential pair");
        check(!store.SavePair("token without separator", FakeChatId, enabled: true),
            "malformed Telegram credentials are rejected before storage");
    }

    private static async Task CheckCategoryFiltersAndOneShot(Action<bool, string> check, string root)
    {
        var settings = Settings();
        var recovery = NewCoordinator(root, "categories", settings, new FakeClock(InitialTime));
        var windows = new FakeNotificationChannel(NotificationChannel.Windows);
        var telegram = new FakeNotificationChannel(NotificationChannel.Telegram);
        using var dispatcher = new NotificationDispatcher(recovery, [windows, telegram], settings);
        var categories = new[]
        {
            NotificationCategory.Opening, NotificationCategory.Closing, NotificationCategory.OrderChange,
            NotificationCategory.Error, NotificationCategory.Recovery
        };
        foreach (var category in categories)
        {
            var message = new NotificationMessage($"category:{category}", category, "synthetic notification");
            var first = await dispatcher.DispatchForVerificationAsync(message).ConfigureAwait(false);
            var repeated = await dispatcher.DispatchForVerificationAsync(message).ConfigureAwait(false);
            string Summary(NotificationDispatchReport report) => string.Join("; ", report.Channels.Select(result =>
                $"{result.Channel}={result.Status}/{result.SafeStatus}"));
            check(first.Channels.Count(result => result.Status is not null) == 2
                  && repeated.Channels.Count(result => result.Status is not null) == 2
                  && windows.Sent.Count(item => item.Id == message.Id) == 1
                  && telegram.Sent.Count(item => item.Id == message.Id) == 1,
                $"{category} sends once to both enabled channels and never repeats the same message "
                + $"(first={Summary(first)}, repeated={Summary(repeated)}, "
                + $"Windows={windows.Sent.Count(item => item.Id == message.Id)}, "
                + $"Telegram={telegram.Sent.Count(item => item.Id == message.Id)}, storage={recovery.StorageStatus})");
        }

        var before = windows.Sent.Count + telegram.Sent.Count;
        var disabledSettings = settings.Copy();
        disabledSettings.NotificationsEnabled = false;
        dispatcher.UpdateSettings(disabledSettings);
        var disabled = await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("category:global-off", NotificationCategory.Opening, "synthetic notification"))
            .ConfigureAwait(false);
        check(windows.Sent.Count + telegram.Sent.Count == before
              && disabled.Channels.All(result => result.SafeStatus.Contains("отключены", StringComparison.Ordinal)),
            "the global permission blocks both channels independently of category selection");

        disabledSettings.NotificationsEnabled = true;
        disabledSettings.OpeningNotifications = false;
        dispatcher.UpdateSettings(disabledSettings);
        var categoryDisabled = await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("category:opening-off", NotificationCategory.Opening, "synthetic notification"))
            .ConfigureAwait(false);
        check(windows.Sent.Count + telegram.Sent.Count == before
              && categoryDisabled.Channels.All(result => result.SafeStatus.Contains("категория отключена", StringComparison.Ordinal)),
            "each category setting filters the event before either channel is called");

        disabledSettings.OpeningNotifications = true;
        dispatcher.UpdateSettings(disabledSettings);
        telegram.IsEnabled = false;
        var telegramOff = await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("category:telegram-off", NotificationCategory.Opening, "synthetic notification"))
            .ConfigureAwait(false);
        check(windows.Sent.Any(item => item.Id == "category:telegram-off")
              && !telegram.Sent.Any(item => item.Id == "category:telegram-off")
              && telegramOff.Channels.Single(result => result.Channel == NotificationChannel.Telegram)
                  .SafeStatus.Contains("отключён", StringComparison.Ordinal),
            "explicitly disabling Telegram does not prevent an allowed Windows notification");

        var readSettings = Settings(retryCount: 3);
        var readRecovery = NewCoordinator(root, "normal-read-silence", readSettings, new FakeClock(InitialTime));
        var readWindows = new FakeNotificationChannel(NotificationChannel.Windows);
        using var readDispatcher = new NotificationDispatcher(readRecovery, [readWindows,
            new FakeNotificationChannel(NotificationChannel.Telegram)], readSettings);
        readDispatcher.Start(TimeSpan.FromHours(1));
        await readRecovery.ReportFailureAsync("Hyperliquid", "account-internal", "read retry", readSettings)
            .ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        check(readRecovery.State.Events.Count == 0 && readWindows.Sent.Count == 0,
            "ordinary recovery reads remain silent until A emits a notification event");
    }

    private static async Task CheckFailureRecoveryAndReminders(Action<bool, string> check, string root)
    {
        var settings = Settings(retryCount: 0);
        var clock = new FakeClock(InitialTime);
        var path = Path.Combine(root, "failure-reminders", "recovery-state.json");
        var coordinator = new RecoveryCoordinator(new RecoveryJournalStore(path), settings, clock);
        var episode = await coordinator.ReportFailureAsync("Hyperliquid", "ACCOUNT_SECRET_7731",
            "REASON_SECRET_9912", settings).ConfigureAwait(false);
        var original = coordinator.State.Events.Single();
        var safeMessage = NotificationMessageFactory.FromRecoveryEvent(original);
        check(episode == original.EpisodeId && original.EventId == episode + ":error"
              && safeMessage.Id == original.EventId && safeMessage.SafeText.Contains("Hyperliquid", StringComparison.Ordinal)
              && safeMessage.SafeText.EndsWith(original.SafeSummary, StringComparison.Ordinal)
              && !safeMessage.SafeText.Contains("ACCOUNT_SECRET_7731", StringComparison.Ordinal)
              && !safeMessage.SafeText.Contains("REASON_SECRET_9912", StringComparison.Ordinal),
            "recovery messages use stable event IDs, SafeSummary, and only a whitelisted venue name");

        var windows = new FakeNotificationChannel(NotificationChannel.Windows);
        var telegram = new FakeNotificationChannel(NotificationChannel.Telegram);
        using var dispatcher = new NotificationDispatcher(coordinator, [windows, telegram], settings);
        await dispatcher.DispatchForVerificationAsync(safeMessage).ConfigureAwait(false);
        check(telegram.Sent.Single().SafeText.Contains(original.SafeSummary, StringComparison.Ordinal)
              && !telegram.Sent.Single().SafeText.Contains("ACCOUNT_SECRET_7731", StringComparison.Ordinal)
              && !telegram.Sent.Single().SafeText.Contains("REASON_SECRET_9912", StringComparison.Ordinal)
              && !telegram.Sent.Single().SafeText.Contains("InternalDetail", StringComparison.Ordinal),
            "Telegram output excludes account, message, reason, outcome, detail, and exception fields");

        var journalText = File.ReadAllText(path);
        check(!journalText.Contains(FakeToken, StringComparison.Ordinal)
              && !journalText.Contains(FakeChatId, StringComparison.Ordinal),
            "recovery delivery attempts persist no Telegram credentials or chat ID");

        clock.UtcNow = InitialTime.AddMinutes(80);
        var afterRestart = new RecoveryCoordinator(new RecoveryJournalStore(path), settings, clock);
        await afterRestart.ProcessDueFailureRemindersAsync().ConfigureAwait(false);
        var reminders = afterRestart.State.Events.Where(item => item.IsReminder).ToArray();
        check(reminders.Length == 1 && reminders[0].EventId == $"{episode}:reminder:1"
              && afterRestart.State.Venues.Single().NextReminderAt == clock.UtcNow.AddMinutes(settings.FailureReminderMinutes),
            "restart emits one overdue reminder and stores the next due time from the current clock");
        var afterReminderRestart = new RecoveryCoordinator(new RecoveryJournalStore(path), settings, clock);
        await afterReminderRestart.ProcessDueFailureRemindersAsync().ConfigureAwait(false);
        check(afterReminderRestart.State.Events.Count(item => item.IsReminder) == 1,
            "a restart does not replay the same overdue reminder or catch up missed intervals");

        var disabledSettings = settings.Copy();
        disabledSettings.FailureRemindersEnabled = false;
        afterReminderRestart.UpdateSettings(disabledSettings);
        clock.UtcNow = afterReminderRestart.State.Venues.Single().NextReminderAt!.Value.AddMinutes(5);
        await afterReminderRestart.ProcessDueFailureRemindersAsync().ConfigureAwait(false);
        check(afterReminderRestart.State.Events.Count(item => item.IsReminder) == 1,
            "disabling failure reminders suppresses the pending reminder");

        CheckResolvedReminderCancellation(check, root);
        await CheckPendingReminderDispatchSuppression(check, root).ConfigureAwait(false);
    }

    private static void CheckResolvedReminderCancellation(Action<bool, string> check, string root)
    {
        var settings = Settings(retryCount: 0, recoveryCheckSeconds: 1);
        var clock = new FakeClock(InitialTime);
        var source = new FakeSnapshotSource();
        var coordinator = new RecoveryCoordinator(
            new RecoveryJournalStore(Path.Combine(root, "reminder-cancel", "recovery-state.json")),
            settings, clock, source);
        coordinator.ReportFailureAsync("Hyperliquid", "account-internal", "failure", settings)
            .GetAwaiter().GetResult();
        clock.UtcNow = coordinator.State.Venues.Single().NextCheckAt!.Value;
        source.NextRead = CompleteRead(clock.UtcNow, "Hyperliquid", "account-internal");
        coordinator.CheckDueAsync("Hyperliquid").GetAwaiter().GetResult();
        var resolved = coordinator.State.Venues.Single();
        clock.UtcNow = InitialTime.AddHours(2);
        coordinator.ProcessDueFailureRemindersAsync().GetAwaiter().GetResult();
        check(resolved.Phase == RecoveryReadPhase.Resolved && resolved.NextReminderAt is null
              && coordinator.State.Events.Any(item => item.Type == RecoveryEventType.Recovered)
              && coordinator.State.Events.All(item => !item.IsReminder),
            "full reconciliation clears the saved reminder deadline and emits a separate recovery event");
    }

    private static async Task CheckPendingReminderDispatchSuppression(Action<bool, string> check, string root)
    {
        var settings = Settings(retryCount: 0, recoveryCheckSeconds: 1);
        var clock = new FakeClock(InitialTime);
        var source = new FakeSnapshotSource();
        var path = Path.Combine(root, "pending-reminder-dispatch", "recovery-state.json");
        var recovery = new RecoveryCoordinator(new RecoveryJournalStore(path), settings, clock, source);
        var episode = await recovery.ReportFailureAsync("Hyperliquid", "reminder-account", "synthetic failure", settings)
            .ConfigureAwait(false);
        var windows = new FakeNotificationChannel(NotificationChannel.Windows);
        var telegram = new FakeNotificationChannel(NotificationChannel.Telegram);
        using var dispatcher = new NotificationDispatcher(recovery, [windows, telegram], settings);

        await dispatcher.DispatchPendingForVerificationAsync().ConfigureAwait(false);
        check(telegram.Sent.Any(item => item.Id == episode + ":error"),
            "initial failure event is delivered before reminder delivery becomes unavailable");

        telegram.IsEnabled = false;
        clock.UtcNow = recovery.State.Venues.Single().NextReminderAt!.Value;
        await recovery.ProcessDueFailureRemindersAsync().ConfigureAwait(false);
        clock.UtcNow = recovery.State.Venues.Single().NextReminderAt!.Value.AddMinutes(5);
        await recovery.ProcessDueFailureRemindersAsync().ConfigureAwait(false);
        var missedReminders = recovery.State.Events.Where(item => item.EpisodeId == episode && item.IsReminder).ToArray();
        await dispatcher.DispatchPendingForVerificationAsync().ConfigureAwait(false);
        check(missedReminders.Length == 2
              && !telegram.Sent.Any(item => item.IsReminder)
              && windows.Sent.Count(item => item.Id == missedReminders[^1].EventId) == 1
              && windows.Sent.All(item => item.Id != missedReminders[0].EventId),
            "reminders missed while Telegram is disabled do not create attempts for that channel "
            + $"(events={missedReminders.Length}, Windows={string.Join(',', windows.Sent.Select(item => item.Id))}, "
            + $"Telegram={string.Join(',', telegram.Sent.Select(item => item.Id))})");

        telegram.IsEnabled = true;
        await dispatcher.DispatchPendingForVerificationAsync().ConfigureAwait(false);
        var deliveredReminderIds = telegram.Sent.Where(item => item.IsReminder).Select(item => item.Id).ToArray();
        check(deliveredReminderIds.SequenceEqual([missedReminders[^1].EventId])
              && recovery.State.NotificationAttempts.All(attempt => attempt.Channel != NotificationChannel.Telegram
                  || attempt.MessageId != missedReminders[0].EventId),
            "re-enabling Telegram sends only the latest reminder from an active episode");

        telegram.IsEnabled = false;
        clock.UtcNow = recovery.State.Venues.Single().NextReminderAt!.Value;
        await recovery.ProcessDueFailureRemindersAsync().ConfigureAwait(false);
        var pendingReminder = recovery.State.Events.Last(item => item.EpisodeId == episode && item.IsReminder);
        await dispatcher.DispatchPendingForVerificationAsync().ConfigureAwait(false);
        source.NextRead = CompleteRead(clock.UtcNow, "Hyperliquid", "reminder-account");
        await recovery.CheckDueAsync("Hyperliquid").ConfigureAwait(false);
        check(recovery.State.Venues.Single().Phase == RecoveryReadPhase.Resolved
              && recovery.State.Venues.Single().NextReminderAt is null,
            "the reminder fixture is fully reconciled before Telegram becomes available again");

        telegram.IsEnabled = true;
        await dispatcher.DispatchPendingForVerificationAsync().ConfigureAwait(false);
        var restarted = new RecoveryCoordinator(new RecoveryJournalStore(path), settings, clock);
        var restartedTelegram = new FakeNotificationChannel(NotificationChannel.Telegram);
        using var restartedDispatcher = new NotificationDispatcher(restarted,
            [new FakeNotificationChannel(NotificationChannel.Windows), restartedTelegram], settings);
        await restartedDispatcher.DispatchPendingForVerificationAsync().ConfigureAwait(false);
        check(!telegram.Sent.Any(item => item.Id == pendingReminder.EventId)
              && !restartedTelegram.Sent.Any(item => item.IsReminder)
              && restarted.State.NotificationAttempts.All(attempt => attempt.Channel != NotificationChannel.Telegram
                  || attempt.MessageId != pendingReminder.EventId),
            "a pending reminder is neither sent after reconciliation nor replayed after restart and re-enable");
    }

    private static async Task CheckIndependentChannelsAndUnknownAttempt(Action<bool, string> check, string root)
    {
        var settings = Settings();
        var clock = new FakeClock(InitialTime);
        var recovery = NewCoordinator(root, "independent-channels", settings, clock);
        var windowsAttemptWasDurable = false;
        var windows = new FakeNotificationChannel(NotificationChannel.Windows,
            async (message, cancellationToken) =>
            {
                windowsAttemptWasDurable = recovery.State.NotificationAttempts.Any(item =>
                    item.MessageId == message.Id && item.Channel == NotificationChannel.Windows
                    && item.Status == NotificationAttemptStatus.InProgress);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return new(NotificationAttemptStatus.PassedToWindows);
            });
        var telegramAttemptWasDurable = false;
        var telegram = new FakeNotificationChannel(NotificationChannel.Telegram,
            async (message, cancellationToken) =>
            {
                telegramAttemptWasDurable = recovery.State.NotificationAttempts.Any(item =>
                    item.MessageId == message.Id && item.Channel == NotificationChannel.Telegram
                    && item.Status == NotificationAttemptStatus.InProgress);
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                return new(NotificationAttemptStatus.AcceptedByTelegram);
            });
        using var dispatcher = new NotificationDispatcher(recovery, [windows, telegram], settings,
            channelTimeout: TimeSpan.FromMilliseconds(60));
        var message = new NotificationMessage("parallel:timeout", NotificationCategory.Error, "safe synthetic message");
        var dispatchTask = dispatcher.DispatchForVerificationAsync(message);
        var recoveryTask = recovery.ReportFailureAsync("Hyperliquid", "account-internal", "another read failure", settings);
        await recoveryTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var report = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        check(windows.Sent.Count == 1 && telegram.Sent.Count == 1
              && windowsAttemptWasDurable && telegramAttemptWasDurable
              && report.Channels.Single(item => item.Channel == NotificationChannel.Windows).Status == NotificationAttemptStatus.Unknown
              && report.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status == NotificationAttemptStatus.AcceptedByTelegram,
            "a hanging Windows channel times out as unknown while Telegram succeeds and reconciliation continues");
        check(recovery.State.NotificationAttempts.Any(item => item.MessageId == message.Id
                  && item.Channel == NotificationChannel.Windows && item.Status == NotificationAttemptStatus.Unknown)
              && recovery.State.NotificationAttempts.Any(item => item.MessageId == message.Id
                  && item.Channel == NotificationChannel.Telegram && item.Status == NotificationAttemptStatus.AcceptedByTelegram),
            "each channel has its own durable result after the shared message attempt");

        var unknownMessage = new NotificationMessage("parallel:unknown", NotificationCategory.Error, "safe synthetic message");
        await dispatcher.DispatchForVerificationAsync(unknownMessage).ConfigureAwait(false);
        var sendsBeforeRepeat = windows.Sent.Count + telegram.Sent.Count;
        var repeat = await dispatcher.DispatchForVerificationAsync(unknownMessage).ConfigureAwait(false);
        check(windows.Sent.Count + telegram.Sent.Count == sendsBeforeRepeat
              && repeat.Channels.Count == 2
              && repeat.Channels.Any(item => item.Status == NotificationAttemptStatus.Unknown),
            "a durable unknown outcome is never sent again automatically on either channel");

        var backupPath = Path.Combine(root, "notification-backup-barrier", "recovery-state.json");
        var backupStore = new RecoveryJournalStore(backupPath);
        var backupCoordinator = new RecoveryCoordinator(backupStore, settings, clock);
        var attempt = await backupCoordinator.BeginNotificationAttemptAsync("backup:uncertain", NotificationChannel.Telegram)
            .ConfigureAwait(false);
        check(attempt.Status == NotificationAttemptStartStatus.Started,
            "notification attempt is persisted before channel transfer begins");
        var afterCrash = new RecoveryCoordinator(new RecoveryJournalStore(backupPath), settings, clock);
        var crashRetry = await afterCrash.BeginNotificationAttemptAsync("backup:uncertain", NotificationChannel.Telegram)
            .ConfigureAwait(false);
        check(crashRetry.Status == NotificationAttemptStartStatus.AlreadyAttempted
              && crashRetry.Existing?.Status == NotificationAttemptStatus.InProgress,
            "restart treats a durable in-progress transfer as unknown and does not begin it again");
        await backupCoordinator.CompleteNotificationAttemptAsync("backup:uncertain", NotificationChannel.Telegram,
            NotificationAttemptStatus.Unknown).ConfigureAwait(false);
        File.Delete(backupPath);
        var missingPrimary = new RecoveryCoordinator(new RecoveryJournalStore(backupPath), settings, clock);
        var blocked = await missingPrimary.BeginNotificationAttemptAsync("backup:uncertain", NotificationChannel.Telegram)
            .ConfigureAwait(false);
        check(missingPrimary.StorageStatus == RecoveryJournalStatus.MissingWithPreviousVersion
              && blocked.Status == NotificationAttemptStartStatus.StorageUnavailable,
            "a missing primary journal is fail-closed and does not reconstruct delivery attempts from its backup");
    }

    private static async Task CheckTelegramTransportAndRateLimit(Action<bool, string> check, string root)
    {
        var settings = Settings();
        var clock = new FakeClock(InitialTime);
        var recovery = NewCoordinator(root, "telegram-rate-limit", settings, clock);
        var credentialStore = new TelegramCredentialStore(Path.Combine(root, "telegram-rate-limit", "telegram-credentials.dat"));
        check(credentialStore.SavePair(FakeToken, FakeChatId, enabled: true),
            "fake Telegram transport uses an isolated encrypted credential pair");
        var handler = new QueuedHttpHandler(
            Response(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"description\":\"HTTP_SECRET_4422\",\"parameters\":{\"retry_after\":30}}"),
            Response(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":1}}"));
        using var telegram = new TelegramNotificationChannel(credentialStore, handler, TimeSpan.FromSeconds(1));
        var windows = new FakeNotificationChannel(NotificationChannel.Windows);
        using var dispatcher = new NotificationDispatcher(recovery, [windows, telegram], settings,
            channelTimeout: TimeSpan.FromSeconds(1));

        var first = await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("telegram:rate-limited", NotificationCategory.Error, "safe error summary"))
            .ConfigureAwait(false);
        check(first.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status == NotificationAttemptStatus.Rejected
              && first.Channels.Single(item => item.Channel == NotificationChannel.Windows).Status == NotificationAttemptStatus.PassedToWindows
              && recovery.State.TelegramRetryAfterAt == InitialTime.AddSeconds(30),
            "HTTP 429 is a known rejection, persists retry_after, and does not suppress Windows delivery");
        var request = handler.Requests.First();
        var decodedBody = Uri.UnescapeDataString(request.Body.Replace("+", " ", StringComparison.Ordinal));
        var parsedUri = new Uri(request.Uri);
        var fixedEndpoint = parsedUri.Scheme == Uri.UriSchemeHttps
            && parsedUri.Host == "api.telegram.org"
            && parsedUri.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal)
            && parsedUri.AbsolutePath.Contains(FakeToken, StringComparison.Ordinal);
        var expectedBody = decodedBody.Contains(FakeChatId, StringComparison.Ordinal)
            && decodedBody.Contains("safe error summary", StringComparison.Ordinal);
        check(fixedEndpoint && expectedBody,
            $"the fake transport receives only the fixed HTTPS Telegram host and sendMessage route (endpoint={fixedEndpoint}, body={expectedBody})");
        check(first.Channels.All(item => !item.SafeStatus.Contains(FakeToken, StringComparison.Ordinal)
                  && !item.SafeStatus.Contains(FakeChatId, StringComparison.Ordinal)
                  && !item.SafeStatus.Contains("HTTP_SECRET_4422", StringComparison.Ordinal))
              && !journalContains(root, "telegram-rate-limit", FakeToken)
              && !journalContains(root, "telegram-rate-limit", FakeChatId),
            "Telegram credentials and chat IDs stay out of status text and the recovery journal");

        var blocked = await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("telegram:next-during-limit", NotificationCategory.Error, "safe next summary"))
            .ConfigureAwait(false);
        check(handler.Requests.Count == 1
              && blocked.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status is null
              && blocked.Channels.Single(item => item.Channel == NotificationChannel.Telegram).SafeStatus.Contains("ограничил частоту", StringComparison.Ordinal),
            "new Telegram transfers wait through the saved rate-limit deadline");

        clock.UtcNow = InitialTime.AddSeconds(31);
        var next = await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("telegram:after-limit", NotificationCategory.Error, "safe next summary"))
            .ConfigureAwait(false);
        check(handler.Requests.Count == 2
              && next.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status == NotificationAttemptStatus.AcceptedByTelegram,
            "Telegram can send a new message after retry_after has elapsed");
        await dispatcher.DispatchForVerificationAsync(
            new NotificationMessage("telegram:rate-limited", NotificationCategory.Error, "safe error summary"))
            .ConfigureAwait(false);
        check(handler.Requests.Count == 2,
            "the known rejected message is never automatically resent after the rate limit expires");

        var fallbackRecovery = NewCoordinator(root, "telegram-rate-limit-fallback", settings, new FakeClock(InitialTime));
        var fallbackHandler = new QueuedHttpHandler(
            Response(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"description\":\"HTTP_SECRET_4422\"}"));
        using (var fallbackTelegram = new TelegramNotificationChannel(credentialStore, fallbackHandler, TimeSpan.FromSeconds(1)))
        using (var fallbackDispatcher = new NotificationDispatcher(fallbackRecovery,
                   [new FakeNotificationChannel(NotificationChannel.Windows), fallbackTelegram], settings,
                   channelTimeout: TimeSpan.FromSeconds(1)))
        {
            var fallback = await fallbackDispatcher.DispatchForVerificationAsync(
                new NotificationMessage("telegram:rate-limit-fallback", NotificationCategory.Error, "safe summary"))
                .ConfigureAwait(false);
            check(fallback.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status
                      == NotificationAttemptStatus.Rejected
                  && fallbackRecovery.State.TelegramRetryAfterAt == InitialTime.AddSeconds(60)
                  && fallback.Channels.All(item => !item.SafeStatus.Contains("HTTP_SECRET_4422", StringComparison.Ordinal)),
                "a 429 without retry_after applies a safe cooldown and omits API response details");
        }

        var malformedRecovery = NewCoordinator(root, "telegram-rate-limit-malformed", settings, new FakeClock(InitialTime));
        var malformedHandler = new QueuedHttpHandler(Response(HttpStatusCode.TooManyRequests, "[]"));
        using (var malformedTelegram = new TelegramNotificationChannel(credentialStore, malformedHandler,
                   TimeSpan.FromSeconds(1)))
        using (var malformedDispatcher = new NotificationDispatcher(malformedRecovery,
                   [new FakeNotificationChannel(NotificationChannel.Windows), malformedTelegram], settings,
                   channelTimeout: TimeSpan.FromSeconds(1)))
        {
            var malformed = await malformedDispatcher.DispatchForVerificationAsync(
                new NotificationMessage("telegram:rate-limit-malformed", NotificationCategory.Error, "safe summary"))
                .ConfigureAwait(false);
            check(malformed.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status
                      == NotificationAttemptStatus.Rejected
                  && malformedRecovery.State.TelegramRetryAfterAt == InitialTime.AddSeconds(60),
                "a malformed JSON response shape on HTTP 429 uses the safe 60-second cooldown");
        }

        var timeoutHandler = new QueuedHttpHandler(blockUntilCanceled: true);
        using var timeoutTelegram = new TelegramNotificationChannel(credentialStore, timeoutHandler,
            TimeSpan.FromMilliseconds(80));
        var timeoutRecovery = NewCoordinator(root, "telegram-timeout", settings, new FakeClock(InitialTime));
        using var timeoutDispatcher = new NotificationDispatcher(timeoutRecovery,
            [new FakeNotificationChannel(NotificationChannel.Windows), timeoutTelegram], settings,
            channelTimeout: TimeSpan.FromMilliseconds(150));
        var timeoutMessage = new NotificationMessage("telegram:unknown-timeout", NotificationCategory.Error, "safe timeout summary");
        var timedOut = await timeoutDispatcher.DispatchForVerificationAsync(timeoutMessage).ConfigureAwait(false);
        var timedOutAgain = await timeoutDispatcher.DispatchForVerificationAsync(timeoutMessage).ConfigureAwait(false);
        check(timedOut.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status == NotificationAttemptStatus.Unknown
              && timeoutHandler.Calls == 1
              && timedOutAgain.Channels.Single(item => item.Channel == NotificationChannel.Telegram).Status == NotificationAttemptStatus.Unknown,
            "a Telegram timeout is unknown and its durable attempt prevents a second request");
    }

    private static RecoveryCoordinator NewCoordinator(string root, string name, AppSettings settings, FakeClock clock) =>
        new(new RecoveryJournalStore(Path.Combine(root, name, "recovery-state.json")), settings, clock);

    private static AppSettings Settings(int retryCount = 0, int recoveryCheckSeconds = 60) => new()
    {
        ReadRetryCount = retryCount,
        ReadRetryDelaySeconds = 10,
        RecoveryCheckSeconds = recoveryCheckSeconds,
        NotificationsEnabled = true,
        OpeningNotifications = true,
        ClosingNotifications = true,
        OrderChangeNotifications = true,
        ErrorNotifications = true,
        RecoveryNotifications = true,
        FailureRemindersEnabled = true,
        FailureReminderMinutes = 60,
    };

    private static RecoverySnapshotRead CompleteRead(DateTimeOffset observedAt, string venue, string account) =>
        new(true,
            new AccountSnapshot(venue, account, "USDC cross", observedAt, "notification fixture", "USDC", "BTC",
                AccountReadState.Available, null, observedAt,
                AccountSection<AccountBalance>.Available(
                    new AccountBalance(new MoneyAmount(1000m, "USDC"), new MoneyAmount(1000m, "USDC"),
                        new MoneyAmount(900m, "USDC")), observedAt, "notification fixture"),
                AccountSection<IReadOnlyList<OpenPosition>>.Available([], observedAt, "notification fixture"),
                AccountSection<IReadOnlyList<OpenOrder>>.Available([], observedAt, "notification fixture"),
                AccountSection<IReadOnlyList<PositionProtection>>.Available([], observedAt, "notification fixture"),
                AccountSection<IReadOnlyList<ClosedTrade>>.NotRead("not needed")),
            OpeningOrderRemaindersComplete: true,
            ProtectionCoverageComplete: true,
            UnfinishedOperationsComplete: true,
            OperationOutcomes: new Dictionary<string, RecoveryOperationObservation>());

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static bool journalContains(string root, string directory, string value)
    {
        var path = Path.Combine(root, directory, "recovery-state.json");
        return File.Exists(path) && File.ReadAllText(path).Contains(value, StringComparison.Ordinal);
    }

    private static T Get<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing notification control '{name}'.");

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            PumpDispatcher();
            Thread.Sleep(20);
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            PumpDispatcher();
            Thread.Sleep(5);
        }
        if (!condition())
            throw new TimeoutException("The notification UI did not finish the test action.");
    }

    private sealed class FakeClock(DateTimeOffset now) : IRecoveryClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeNotificationChannel(
        NotificationChannel channel,
        Func<NotificationMessage, CancellationToken, Task<NotificationChannelSendResult>>? send = null)
        : INotificationChannel
    {
        private readonly ConcurrentQueue<NotificationMessage> _sent = new();
        public NotificationChannel Channel => channel;
        public bool IsEnabled { get; set; } = true;
        public bool IsConfigured { get; set; } = true;
        public IReadOnlyCollection<NotificationMessage> Sent => _sent.ToArray();

        public Task<NotificationChannelSendResult> SendAsync(
            NotificationMessage message,
            CancellationToken cancellationToken)
        {
            _sent.Enqueue(message);
            return send is null
                ? Task.FromResult(new NotificationChannelSendResult(channel == NotificationChannel.Windows
                    ? NotificationAttemptStatus.PassedToWindows
                    : NotificationAttemptStatus.AcceptedByTelegram))
                : send(message, cancellationToken);
        }
    }

    private sealed class FakeSnapshotSource : IRecoverySnapshotSource
    {
        internal RecoverySnapshotRead? NextRead { get; set; }
        public TimeSpan MaximumSnapshotAge => TimeSpan.FromMinutes(5);

        public Task<RecoverySnapshotRead> ReadAsync(string venue, string account, CancellationToken cancellationToken) =>
            Task.FromResult(NextRead ?? new RecoverySnapshotRead(false, null, false, false, false,
                FailureReason: "fixture response missing"));
    }

    private sealed class QueuedHttpHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses = new();
        private readonly bool _blockUntilCanceled;
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        private int _calls;

        internal QueuedHttpHandler(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses)
                _responses.Enqueue(response);
        }

        internal QueuedHttpHandler(bool blockUntilCanceled)
        {
            _blockUntilCanceled = blockUntilCanceled;
        }

        internal int Calls => Volatile.Read(ref _calls);
        internal IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _requests.Enqueue(new(request.RequestUri?.AbsoluteUri ?? string.Empty, body));
            if (_blockUntilCanceled)
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return _responses.TryDequeue(out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"ok\":false}", Encoding.UTF8, "application/json")
                };
        }
    }

    private sealed record CapturedRequest(string Uri, string Body);
}
