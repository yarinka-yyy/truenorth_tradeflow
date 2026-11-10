using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TrueNorth.TradeFlow;

namespace TrueNorth.TradeFlow.Verification;

internal static class SimulationChecks
{
    internal static void CheckScenarios(Action<bool, string> check)
    {
        check(SimulationScenario.All.Select(scenario => scenario.Id).SequenceEqual(
                ["limit-cycle", "low-funds", "session-expired", "process-stopped"]),
            "four fixed simulation scenarios are available in the planned order");
        check(SimulationScenario.All.All(scenario => scenario.Steps.Length > 1),
            "each scenario has a scripted sequence of immutable snapshots");

        var allNotifications = new SimulationNotificationPreferences(true, true, true, true, true, true, true);
        var limit = new SimulationSession("limit-cycle");
        var idle = limit.Snapshot;
        check(!idle.Started && idle.CurrentStep is null && idle.Events.IsEmpty,
            "a new session is inert until Start is explicitly requested");
        var started = limit.Start(allNotifications);
        check(started.Started && started.StepNumber == 1 && started.CurrentStep?.Candidates.Length == 1
              && started.Notifications.IsEmpty,
            "Limit scenario starts with its candidate and no routine notification");
        var firstSnapshot = limit.Snapshot;
        limit.Next();
        check(firstSnapshot.StepNumber == 1 && firstSnapshot.CurrentStep?.Orders.IsEmpty == true
              && limit.Snapshot.CurrentStep?.Orders.Single().Status == "Ожидает исполнения",
            "published snapshots remain unchanged when the next step is applied");
        var partial = limit.Next();
        check(partial.CurrentStep?.Orders.Single().Filled == "4 из 10"
              && partial.CurrentStep.Orders.Single().Remaining == "6"
              && partial.CurrentStep.OpenPositions.Single().TakeProfit == "108,00"
              && partial.CurrentStep.OpenPositions.Single().StopLoss == "96,00"
              && partial.Notifications.Single().Category == "Открытие",
            "partial fill preserves the remainder and TP/SL and previews an opening notice");

        var cancelled = limit.Next();
        check(cancelled.CurrentStep?.Orders.Single().Remaining == "0"
              && cancelled.CurrentStep.OpenPositions.Length == 1
              && cancelled.Notifications.Last().Category == "Изменение заявки",
            "remainder cancellation keeps the position and uses the order-change category");
        var closed = limit.Next();
        check(closed.CurrentStep?.OpenPositions.IsEmpty == true
              && closed.CurrentStep.ClosedPositions.Single().CloseTime == "10:18"
              && closed.CurrentStep.ClosedPositions.Single().CloseReason == "Закрытие по TP"
              && closed.CurrentStep.ClosedPositions.Single().RealizedResult == "+32,00"
              && closed.CurrentStep.Venues[0].Balance == "2 532,00"
              && closed.CurrentStep.Venues[0].Available == "1 072,00"
              && closed.Notifications.Last().Category == "Закрытие",
            "TP close moves the position to realized results and updates venue funds");
        var complete = limit.Next();
        check(complete.Complete && complete.CurrentStep?.Candidates.Single().Market == "ETH-PERP"
              && complete.CurrentStep.ClosedPositions.Single().RealizedResult == "+32,00"
              && complete.Notifications.Select(notification => notification.Category)
                  .SequenceEqual(["Открытие", "Изменение заявки", "Закрытие"]),
            "final candidate keeps the closed trade, realized result and simulated notification history");
        var eventCount = complete.Events.Length;
        var repeated = limit.Next();
        check(repeated.Complete && repeated.Events.Length == eventCount
              && ReferenceEquals(repeated.CurrentStep, complete.CurrentStep),
            "Next after completion does not append duplicate events");
        var reset = limit.Reset();
        check(!reset.Started && reset.Events.IsEmpty && reset.Notifications.IsEmpty
              && reset.ScenarioId == "limit-cycle",
            "Reset clears session state and keeps the selected scenario");

        var lowFunds = new SimulationSession("low-funds");
        var low = lowFunds.Start(allNotifications);
        check(low.CurrentStep?.Status == "Ожидание средств" && low.CurrentStep.Venues[0].Available == "8,00",
            "insufficient funds are shown as a delayed entry state");
        low = lowFunds.Next();
        check(low.CurrentStep?.Venues[0].Available == "208,00" && low.CurrentStep.ClosedPositions.IsEmpty,
            "deposit resumes the check without a closed trade");
        low = lowFunds.Next();
        check(low.CurrentStep?.Title == "Подходящего сетапа нет" && low.Notifications.IsEmpty,
            "NO_SETUP remains a visible routine event without a notification");
        check(lowFunds.Next().CurrentStep?.Candidates.Single().Market == "BTC-PERP",
            "the next retry presents a candidate after NO_SETUP");

        CheckNotificationFilters(allNotifications, check);
        var settings = new AppSettings
        {
            NotificationsEnabled = true,
            OpeningNotifications = true,
            ClosingNotifications = true,
            OrderChangeNotifications = true,
            ErrorNotifications = false,
            RecoveryNotifications = true,
            FailureRemindersEnabled = true
        };
        var captured = SimulationNotificationPreferences.Capture(settings);
        settings.NotificationsEnabled = false;
        settings.ErrorNotifications = true;
        var stopped = new SimulationSession("process-stopped");
        var stoppedSnapshot = stopped.Start(captured);
        check(stoppedSnapshot.Notifications.IsEmpty,
            "notification switches are copied at start and do not follow later settings edits");
        stoppedSnapshot = stopped.Next();
        check(stoppedSnapshot.Events.Last().IsReminder && stoppedSnapshot.Notifications.IsEmpty,
            "failure reminders require both the error category and reminder switches");
        stoppedSnapshot = stopped.Next();
        check(stoppedSnapshot.Notifications.Single().Category == "Восстановление"
              && stoppedSnapshot.Events.Last().CategoryLabel == "Восстановление",
            "recovery preview remains distinct from a disabled error category");

        var session = new SimulationSession("session-expired");
        check(SimulationScenario.Find("session-expired").Steps[1].Time == "13:00",
            "the failure reminder follows the scripted one-hour interval");
        var expired = session.Start(allNotifications);
        check(expired.CurrentStep?.Venues[0].Status == "Данные устарели"
              && expired.CurrentStep.Venues[1].Status == "Данные недоступны"
              && expired.Notifications.Single().Category == "Ошибка",
            "expired session distinguishes stale and unavailable data and previews the initial error");
        expired = session.Next();
        check(expired.Events.Last().IsReminder && expired.Notifications.Last().Category == "Ошибка",
            "expired-session reminder is a separate opt-in error notice");
        expired = session.Next();
        check(expired.CurrentStep?.Venues[0].Status == "Данные устарели",
            "synthetic reauthentication does not mark account data fresh prematurely");
        expired = session.Next();
        check(expired.CurrentStep?.Venues.All(venue => venue.Status == "Данные доступны") == true
              && expired.Notifications.Last().Category == "Восстановление",
            "synthetic reload resumes the scenario with a recovery-category notice");

        var noNotices = new SimulationSession("process-stopped");
        var disabled = noNotices.Start(allNotifications with { Enabled = false });
        disabled = noNotices.Next();
        disabled = noNotices.Next();
        check(disabled.Events.Length == 3 && disabled.Notifications.IsEmpty
              && disabled.CurrentStep?.Status == "Ожидание продолжения",
            "disabling all notifications leaves the scripted state and event journal intact");
    }

    internal static void CheckMainWindow(Action<bool, string> check, Application application)
    {
        var artifacts = ArtifactsDirectory();
        Directory.CreateDirectory(artifacts);
        var fixtureRoot = Path.Combine(artifacts, ".fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var settingsPath = Path.Combine(fixtureRoot, "settings.json");
        var store = new SettingsStore(settingsPath);
        var initial = TestProfile(25);
        check(store.Save(initial).Succeeded, "isolated simulation profile is prepared");
        var saved = TestProfile(32);
        check(store.Save(saved).Succeeded && File.Exists(settingsPath + ".bak"),
            "isolated simulation profile has a primary file and backup");
        var settingsBytes = File.ReadAllBytes(settingsPath);
        var backupBytes = File.ReadAllBytes(settingsPath + ".bak");
        var powerRequests = new List<bool>();
        var controller = new WakeProtectionController(false, enabled => { powerRequests.Add(enabled); return true; });
        var window = new MainWindow(store, store.Load(), controller);

        try
        {
            window.Width = 1040;
            window.Height = 720;
            window.Show();
            window.Activate();
            window.UpdateLayout();
            PumpDispatcher(application);
            var navigation = Get<ListBox>(window, "NavigationList");
            navigation.SelectedIndex = 2;
            window.UpdateLayout();
            PumpDispatcher(application);
            var page = Get<SimulationPage>(window, "SimulationContent");
            var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
            var simulationBadge = Get<Border>(window, "SimulationStatusBadge");
            check(page.Visibility == Visibility.Visible && simulationBadge.Visibility == Visibility.Visible
                  && Get<Border>(window, "OperatingStatusBadge").Visibility == Visibility.Collapsed,
                "simulation navigation selects its separate page and fixed synthetic-data badge");
            var scenarioChoice = Get<ComboBox>(window, "ScenarioSelector");
            scenarioChoice.ApplyTemplate();
            window.UpdateLayout();
            var scenarioPopup = (Popup?)scenarioChoice.Template.FindName("Popup", scenarioChoice)
                ?? throw new InvalidOperationException("Scenario popup was not found.");
            scenarioChoice.IsDropDownOpen = true;
            PumpDispatcher(application);
            var scenarioPopupSurface = scenarioPopup.Child as Border
                ?? throw new InvalidOperationException("Scenario popup surface is unavailable.");
            var firstScenarioOption = scenarioChoice.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem
                ?? throw new InvalidOperationException("First scenario option was not generated.");
            check(scenarioPopup.IsOpen && scenarioPopupSurface.ActualWidth > 0
                  && (scenarioPopupSurface.Background as SolidColorBrush)?.Color == Color.FromRgb(0x18, 0x24, 0x2E)
                  && (firstScenarioOption.Foreground as SolidColorBrush)?.Color == Color.FromRgb(0xE8, 0xEE, 0xF2),
                "scenario picker popup keeps light option text on the existing dark panel");
            RenderElement(scenarioPopupSurface, Path.Combine(artifacts, "simulation-scenario-popup.png"));
            scenarioChoice.IsDropDownOpen = false;
            PumpDispatcher(application);
            check(Get<TextBlock>(window, "StepTitleText").Text == "Ожидание запуска"
                  && Get<Button>(window, "StartButton").IsEnabled
                  && !Get<Button>(window, "NextButton").IsEnabled
                  && !Get<Button>(window, "ResetButton").IsEnabled,
                "before Start the page shows an inert state and exposes only the primary action");
            var disabledAction = Get<Button>(window, "NextButton");
            check(disabledAction.Background is SolidColorBrush background
                  && background.Color == Color.FromRgb(0x1A, 0x27, 0x30)
                  && disabledAction.Foreground is SolidColorBrush foreground
                  && foreground.Color == Color.FromRgb(0x7E, 0x8F, 0x9B),
                "disabled actions retain readable colors on the dark simulation page");
            RenderWindow(window, Path.Combine(artifacts, "simulation-before-start.png"));

            Click(Get<Button>(window, "StartButton"));
            Click(Get<Button>(window, "NextButton"));
            Click(Get<Button>(window, "NextButton"));
            window.UpdateLayout();
            PumpDispatcher(application);
            check(Get<TextBlock>(window, "StepTitleText").Text == "Заявка исполнена частично"
                  && Get<ItemsControl>(window, "OpenPositionsItems").Items.Count == 1
                  && Get<ItemsControl>(window, "OrdersItems").Items.Count == 1
                  && Get<TextBlock>(window, "NotificationCategoryText").Text == "Открытие",
                "MainWindow Start and Next actions render the partial-fill state and in-app notice");
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            RenderWindow(window, Path.Combine(artifacts, "simulation-limit-partial.png"));
            Get<ItemsControl>(window, "OpenPositionsItems").BringIntoView();
            window.UpdateLayout();
            PumpDispatcher(application);
            RenderWindow(window, Path.Combine(artifacts, "simulation-limit-partial-details.png"));

            Click(Get<Button>(window, "NextButton"));
            check(Get<TextBlock>(window, "StepTitleText").Text == "Остаток заявки отменён"
                  && Get<ItemsControl>(window, "OpenPositionsItems").Items.Count == 1
                  && Get<TextBlock>(window, "NotificationCategoryText").Text == "Изменение заявки",
                "the UI keeps the partial position after cancellation and previews the order-change notice");
            Click(Get<Button>(window, "NextButton"));
            window.UpdateLayout();
            PumpDispatcher(application);
            check(Get<ItemsControl>(window, "OpenPositionsItems").Items.Count == 0
                  && Get<ItemsControl>(window, "ClosedPositionsItems").Items.Count == 1
                  && Get<TextBlock>(window, "NotificationCategoryText").Text == "Закрытие",
                "the UI separates the closed result from open positions and previews its closing notice");
            Get<ItemsControl>(window, "ClosedPositionsItems").BringIntoView();
            window.UpdateLayout();
            PumpDispatcher(application);
            RenderWindow(window, Path.Combine(artifacts, "simulation-limit-closed.png"));
            Click(Get<Button>(window, "NextButton"));
            var completedTitle = Get<TextBlock>(window, "StepTitleText").Text;
            check(completedTitle == "Найден следующий кандидат" && !Get<Button>(window, "NextButton").IsEnabled
                  && Get<ItemsControl>(window, "ClosedPositionsItems").Items.Count == 1
                  && Get<ItemsControl>(window, "NotificationsItems").Items.Count == 3
                  && Get<ItemsControl>(window, "EventsItems").Items.Count == 6,
                "the completed UI keeps the closed result, notification history and journal with the next candidate");

            var scenarioSelector = Get<ComboBox>(window, "ScenarioSelector");
            scenarioSelector.SelectedIndex = 2;
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            PumpDispatcher(application);
            Click(Get<Button>(window, "StartButton"));
            window.UpdateLayout();
            PumpDispatcher(application);
            check(Get<TextBlock>(window, "StepTitleText").Text == "Сессия истекла"
                  && Get<TextBlock>(window, "NotificationCategoryText").Text == "Ошибка"
                  && Get<ItemsControl>(window, "VenuesItems").Items.Count == 2,
                "session failure UI shows its initial error and separate venue availability");
            RenderWindow(window, Path.Combine(artifacts, "simulation-session-expired.png"));
            Click(Get<Button>(window, "NextButton"));
            var reminderTitle = Get<TextBlock>(window, "StepTitleText").Text;
            var reminderCount = Get<ItemsControl>(window, "EventsItems").Items.Count;
            navigation.SelectedIndex = 1;
            window.UpdateLayout();
            navigation.SelectedIndex = 2;
            window.UpdateLayout();
            PumpDispatcher(application);
            check(Get<TextBlock>(window, "StepTitleText").Text == reminderTitle
                  && Get<ItemsControl>(window, "EventsItems").Items.Count == reminderCount,
                "navigation to Settings and back preserves the active simulation step and journal");
            RenderWindow(window, Path.Combine(artifacts, "simulation-session-reminder.png"));
            scenarioSelector.SelectedIndex = 3;
            check(Get<TextBlock>(window, "StepTitleText").Text == "Ожидание запуска"
                  && Get<ItemsControl>(window, "EventsItems").Items.Count == 0,
                "changing scenarios resets the previous session and its event journal");
            Click(Get<Button>(window, "StartButton"));
            Click(Get<Button>(window, "NextButton"));
            Click(Get<Button>(window, "NextButton"));
            check(Get<TextBlock>(window, "StepTitleText").Text == "Процесс восстановлен"
                  && Get<TextBlock>(window, "NotificationCategoryText").Text == "Восстановление",
                "stopped-process scenario previews recovery separately from the initial error");
            Click(Get<Button>(window, "ResetButton"));
            check(Get<TextBlock>(window, "StepTitleText").Text == "Ожидание запуска"
                  && Get<ItemsControl>(window, "EventsItems").Items.Count == 0
                  && Get<Button>(window, "StartButton").IsEnabled
                  && !Get<Button>(window, "NextButton").IsEnabled,
                "the MainWindow reset action clears the run and restores the start state");

            var modeList = Get<ListBox>(window, "ModeList");
            check(modeList.Items.Count == 3
                  && ((ListBoxItem)modeList.Items[0]).Tag as string == "Test"
                  && ((ListBoxItem)modeList.Items[1]).Tag as string == "TrueNorth AI setups"
                  && !((ListBoxItem)modeList.Items[2]).IsEnabled,
                "Test and TrueNorth AI modes remain available and Arbitrage remains disabled");
            navigation.SelectedIndex = 0;
            check(((ListBoxItem)modeList.SelectedItem).Tag as string == "Test",
                "simulation leaves the saved work mode unchanged");
            navigation.SelectedIndex = 2;

            window.Width = 760;
            window.Height = 560;
            scenarioSelector.SelectedIndex = 3;
            window.UpdateLayout();
            PumpDispatcher(application);
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            check(window.ActualWidth <= 761 && window.ActualWidth >= 759
                  && Get<Button>(window, "StartButton").ActualWidth > 0
                  && Get<Button>(window, "ResetButton").ActualWidth > 0
                  && Get<Button>(window, "StartButton").IsTabStop
                  && Get<ComboBox>(window, "ScenarioSelector").IsTabStop
                  && IsInViewport(Get<Button>(window, "StartButton"), scroll)
                  && IsInViewport(Get<Button>(window, "NextButton"), scroll)
                  && IsInViewport(Get<Button>(window, "ResetButton"), scroll),
                "scenario choice and all actions are in the first minimum-width viewport and on the keyboard path");
            RenderWindow(window, Path.Combine(artifacts, "simulation-minimum-before-start.png"));

            var selector = Get<ComboBox>(window, "ScenarioSelector");
            var start = Get<Button>(window, "StartButton");
            CheckKeyboardFocus(selector, start, check, application);
            Click(start);
            Click(Get<Button>(window, "NextButton"));
            Keyboard.ClearFocus();
            window.Focus();
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            PumpDispatcher(application);
            var activeNext = Get<Button>(window, "NextButton");
            var activeTitle = Get<TextBlock>(window, "StepTitleText");
            check(activeTitle.Text == "Сбой продолжается" && activeNext.IsEnabled
                  && IsInViewport(activeTitle, scroll) && IsInViewport(activeNext, scroll),
                "at minimum width the active Next action and current step title share the first viewport");
            RenderWindow(window, Path.Combine(artifacts, "simulation-minimum-active-top.png"));
            scroll.ScrollToBottom();
            window.UpdateLayout();
            PumpDispatcher(application);
            check(scroll.ScrollableHeight > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 1
                  && simulationBadge.IsVisible
                  && Get<TextBlock>(window, "SimulationStatusText").Text.Contains("СИМУЛЯЦИЯ", StringComparison.Ordinal),
                $"minimum-width scroll and badge state (range={scroll.ScrollableHeight:0.0}, offset={scroll.VerticalOffset:0.0}, badge={simulationBadge.Visibility}/{simulationBadge.IsVisible})");
            RenderWindow(window, Path.Combine(artifacts, "simulation-minimum-process-reminder-bottom.png"));
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();

            var finalLoad = store.Load();
            check(File.ReadAllBytes(settingsPath).SequenceEqual(settingsBytes)
                  && File.ReadAllBytes(settingsPath + ".bak").SequenceEqual(backupBytes)
                  && finalLoad.Profile.SelectedMode == "Test"
                  && finalLoad.Profile.Settings.MarginBalancePercent == 32
                  && finalLoad.Profile.SavedOperatingIntent
                  && powerRequests.Count == 0 && !controller.IsEnabled,
                "navigation, simulation and reset leave settings, backup, saved mode and power controller unchanged");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
            try
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CheckNotificationFilters(
        SimulationNotificationPreferences enabled,
        Action<bool, string> check)
    {
        var entries = Enum.GetValues<SimulationNoticeCategory>();
        foreach (var category in entries)
        {
            var entry = new SimulationEvent("00:00", category.ToString(), "Синтетическое событие.", category);
            check(enabled.Allows(entry), $"enabled {category} category creates a notification preview");
            var disabled = category switch
            {
                SimulationNoticeCategory.Opening => enabled with { Opening = false },
                SimulationNoticeCategory.Closing => enabled with { Closing = false },
                SimulationNoticeCategory.OrderChange => enabled with { OrderChanges = false },
                SimulationNoticeCategory.Error => enabled with { Errors = false },
                SimulationNoticeCategory.Recovery => enabled with { Recovery = false },
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };
            check(!disabled.Allows(entry), $"disabled {category} category suppresses only its preview");
        }

        var reminder = new SimulationEvent("00:01", "Напоминание", "Сбой продолжается.", SimulationNoticeCategory.Error, true);
        check(enabled.Allows(reminder) && enabled.Allows(reminder with { IsReminder = false }),
            "a configured reminder is allowed by the matching error and reminder switches");
        check(!(enabled with { Reminders = false }).Allows(reminder),
            "disabling reminders suppresses a repeat while keeping the error category enabled");
        check((enabled with { Errors = false, Recovery = true }).Allows(
                  new SimulationEvent("00:02", "Восстановление", "Доступ вернулся.", SimulationNoticeCategory.Recovery))
              && !(enabled with { Errors = false }).Allows(
                  new SimulationEvent("00:03", "Ошибка", "Доступа нет.", SimulationNoticeCategory.Error)),
            "recovery and error switches are independent");
        check(!(enabled with { Enabled = false }).Allows(
                  new SimulationEvent("00:04", "Открытие", "Позиция открылась.", SimulationNoticeCategory.Opening)),
            "the global notification switch suppresses all categories");

        var routine = new SimulationSession("low-funds");
        var routineSnapshot = routine.Start(enabled);
        while (routineSnapshot.HasNext)
            routineSnapshot = routine.Next();
        check(routineSnapshot.Events.Length == 4 && routineSnapshot.Notifications.IsEmpty,
            "ordinary balance, NO_SETUP and candidate steps never create notifications");
    }

    private static void CheckKeyboardFocus(
        ComboBox selector,
        Button start,
        Action<bool, string> check,
        Application application)
    {
        var focus = Keyboard.Focus(selector);
        PumpDispatcher(application);
        check(focus == selector && Keyboard.FocusedElement == selector
              && selector.IsKeyboardFocusWithin,
            "scenario selector receives native WPF keyboard focus");
        selector.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        PumpDispatcher(application);
        check(Keyboard.FocusedElement is Button focused && focused.IsTabStop
              && focused.FocusVisualStyle is not null,
            "Tab traversal reaches an enabled action with the visible focus outline");
        Keyboard.Focus(start);
        PumpDispatcher(application);
        check(start.IsKeyboardFocused && start.IsEnabled && start.FocusVisualStyle is not null,
            "primary action retains keyboard focus and its visible outline at minimum width");
    }

    private static SettingsProfile TestProfile(decimal margin) => new()
    {
        Settings = new AppSettings
        {
            Assets = ["BTC", "ETH"],
            EntryType = "Limit",
            MarginBalancePercent = margin,
            Leverage = 2,
            NotificationsEnabled = true,
            OpeningNotifications = true,
            ClosingNotifications = true,
            OrderChangeNotifications = true,
            ErrorNotifications = true,
            RecoveryNotifications = true,
            FailureRemindersEnabled = true,
            PreventSleep = false
        },
        SelectedMode = "Test",
        SavedOperatingIntent = true
    };

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name)
            ?? (window.FindName("SimulationContent") as FrameworkElement)?.FindName(name)
            ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool IsInViewport(FrameworkElement element, ScrollViewer scroll)
    {
        var topLeft = element.TransformToAncestor(scroll).Transform(new Point(0, 0));
        var bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).Contains(bounds);
    }

    private static void RenderWindow(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"WPF render is empty: {path}");
    }

    private static void RenderElement(FrameworkElement element, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"WPF render is empty: {path}");
    }

    private static string ArtifactsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "003-simulation.md")))
                return Path.Combine(directory.FullName, "artifacts", "003-simulation");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts", "003-simulation");
    }

    private static void PumpDispatcher(Application application)
    {
        var frame = new DispatcherFrame();
        application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
