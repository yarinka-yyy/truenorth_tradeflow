using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using TrueNorth.TradeFlow.Account;

namespace TrueNorth.TradeFlow.Verification;

internal static class AccountReadChecks
{
    public static void CheckCore(Action<bool, string> check)
    {
        var snapshots = AccountExample.Create();
        var north = snapshots.Single(snapshot => snapshot.Venue == "North Perp");
        var south = snapshots.Single(snapshot => snapshot.Venue == "South Perp");

        check(snapshots.Count == 2 && snapshots.All(snapshot => snapshot.Source == AccountExample.SourceName),
            "local example contains two explicitly local venue snapshots");
        check(north.Balances.State == AccountReadState.Available
              && north.Balances.Value?.AvailableMargin == new MoneyAmount(0m, "USDC"),
            "a confirmed zero margin remains a readable amount with its currency");
        check(north.Balances.Value?.Balance == new MoneyAmount(1250m, "USDC")
              && north.Balances.Value?.Equity == new MoneyAmount(1266.30m, "USDC"),
            "balance and equity remain separate and keep their units");
        check(north.Positions.State == AccountReadState.Incomplete
              && north.Positions.Value?.Count == 3
              && north.Positions.FailureReason is not null,
            "partial coverage retains every received position and its failure reason");
        check(north.Positions.Value?.Select(position => position.Ownership)
                  .SequenceEqual([PositionOwnership.SoftwareOwned, PositionOwnership.Manual, PositionOwnership.Unknown]) == true,
            "software, manual and mixed unknown exposure stay distinct");
        check(north.Orders.Value?.Single().FilledQuantity == 0.025m
              && north.Orders.Value?.Single().RemainingQuantity == 0.015m
              && north.Orders.Value?.Single().ProtectionStatus.Contains("Цены TP/SL получены", StringComparison.Ordinal) == true
              && north.Orders.Value?.Single().ProtectionStatus.Contains("покрытие открытого объёма неизвестно", StringComparison.Ordinal) == true
              && north.Protections.State == AccountReadState.Incomplete,
            "partial fill remainder and unknown TP/SL coverage remain separate from the observed prices");
        check(south.Availability == AccountReadState.AccessLost
              && south.Balances.State == AccountReadState.PreviousSnapshot
              && south.Balances.Value?.Balance == new MoneyAmount(420m, "USD")
              && south.Balances.LastSuccessfulAt == south.LastSuccessfulReadAt,
            "lost access preserves a previous balance and its last successful read");
        check(south.Orders.State == AccountReadState.PreviousSnapshot
              && south.Orders.Value?.Count == 0
              && south.Orders.FailureReason is not null,
            "a previous empty list remains distinct from an unread or inaccessible section");
        check(snapshots.Select(snapshot => snapshot.QuoteCurrency).Distinct(StringComparer.Ordinal).Count() == 2,
            "different venue currencies stay separate in the example");

        var observedAt = north.ObservedAt;
        var empty = AccountSection<IReadOnlyList<OpenOrder>>.ConfirmedEmpty([], observedAt, "fixture");
        var lost = AccountSection<IReadOnlyList<OpenOrder>>.AccessLost("fixture", "no access");
        var notRead = AccountSection<IReadOnlyList<OpenOrder>>.NotRead("fixture");
        check(empty.State == AccountReadState.ConfirmedEmpty && empty.Value?.Count == 0
              && lost.State == AccountReadState.AccessLost && lost.Value is null
              && notRead.State == AccountReadState.NotRead && notRead.Value is null,
            "confirmed empty, access loss and not-read states are distinct");
        check(south.History.Value?.Single().RealizedPnl == new MoneyAmount(4.22m, "USD")
              && south.History.State == AccountReadState.PreviousSnapshot,
            "previous history keeps its own venue currency and snapshot state");
        var incompleteHistoryStatus = AccountPresentation.CreateCard(north).HistoryStatus;
        check(incompleteHistoryStatus.Contains("Данные неполные", StringComparison.Ordinal)
              && incompleteHistoryStatus.Contains("строк: 2", StringComparison.Ordinal)
              && incompleteHistoryStatus.Contains("последнее успешное чтение", StringComparison.Ordinal)
              && !incompleteHistoryStatus.Contains("закрытых сделок нет", StringComparison.Ordinal),
            "partial history keeps its rows and last complete read without claiming there are no trades");
        var confirmedEmptyHistory = AccountSection<IReadOnlyList<ClosedTrade>>.ConfirmedEmpty(
            Array.Empty<ClosedTrade>(), observedAt, AccountExample.SourceName);
        var emptyHistoryStatus = AccountPresentation.CreateCard(north with { History = confirmedEmptyHistory }).HistoryStatus;
        check(emptyHistoryStatus.Contains("подтверждённо 0 сделок", StringComparison.Ordinal)
              && AccountPresentation.CreateHistoryNoRowsMessage([north with { History = confirmedEmptyHistory }])
                  .StartsWith("Подтверждённо пусто", StringComparison.Ordinal),
            "confirmed-empty history is presented as a verified zero");
        var inaccessibleHistory = AccountSection<IReadOnlyList<ClosedTrade>>.AccessLost(
            AccountExample.SourceName, "История недоступна в проверке.", observedAt.AddHours(-26));
        var inaccessibleHistoryStatus = AccountPresentation.CreateCard(north with { History = inaccessibleHistory }).HistoryStatus;
        var inaccessibleNoRows = AccountPresentation.CreateHistoryNoRowsMessage([north with { History = inaccessibleHistory }]);
        check(inaccessibleHistoryStatus.Contains("Доступ утрачен", StringComparison.Ordinal)
              && inaccessibleHistoryStatus.Contains("последнее успешное чтение", StringComparison.Ordinal)
              && inaccessibleHistoryStatus.Contains("История недоступна в проверке.", StringComparison.Ordinal)
              && inaccessibleNoRows.Contains("актуальность истории не подтверждена", StringComparison.Ordinal)
              && !inaccessibleNoRows.Contains("Подтверждённо пусто", StringComparison.Ordinal),
            "inaccessible history retains its last success and reason without claiming a verified zero");
        var missingHistoryValue = AccountSection<IReadOnlyList<ClosedTrade>>.Available(
            null!, observedAt, AccountExample.SourceName);
        var missingHistoryStatus = AccountPresentation.CreateCard(north with { History = missingHistoryValue }).HistoryStatus;
        check(missingHistoryStatus.Contains("строки истории не получены", StringComparison.Ordinal)
              && !AccountPresentation.CreateHistoryNoRowsMessage([north with { History = missingHistoryValue }])
                  .Contains("чтение истории завершено", StringComparison.Ordinal),
            "a missing history value is never treated as a full zero-row read");
        var incompleteWithoutLastSuccess = AccountSection<IReadOnlyList<ClosedTrade>>.Incomplete(
            [], observedAt, AccountExample.SourceName, "Неполное чтение без известного полного результата.");
        var incompleteWithoutLastSuccessCard = AccountPresentation.CreateCard(north with { History = incompleteWithoutLastSuccess });
        check(incompleteWithoutLastSuccess.LastSuccessfulAt is null
              && incompleteWithoutLastSuccessCard.HistoryStatus.Contains("наблюдение", StringComparison.Ordinal)
              && !incompleteWithoutLastSuccessCard.HistoryStatus
                  .Contains("последнее успешное чтение", StringComparison.Ordinal),
            "an incomplete observation is not substituted for a known last successful read");

        var operation = new TradeOperationIdentity("Venue", "Account", "BTC-PERP", ["order-1"], ["fill-1"]);
        var linked = new ExposureIdentity("Venue", "Account", "BTC-PERP", ["order-1"], ["fill-1"]);
        check(PositionOwnershipClassifier.Classify(linked, [operation]) == PositionOwnership.SoftwareOwned,
            "ownership is software only when venue, account, contract and IDs match");
        check(PositionOwnershipClassifier.Classify(
                  new ExposureIdentity("Venue", "Account", "BTC-PERP", [], []), [operation])
              == PositionOwnership.Unknown,
            "asset and contract without order or execution IDs do not establish ownership");
        check(PositionOwnershipClassifier.Classify(
                  new ExposureIdentity("Venue", "Account", "BTC-PERP", ["manual-order"], ["manual-fill"]),
                  [operation], explicitlyManual: true) == PositionOwnership.Manual,
            "manual ownership requires explicit evidence and no overlapping operation ID");
        check(PositionOwnershipClassifier.Classify(
                  new ExposureIdentity("Venue", "Account", "BTC-PERP", ["order-1", "other-order"], ["fill-1"]),
                  [operation]) == PositionOwnership.Unknown,
            "partially linked IDs remain unknown");
        check(PositionOwnershipClassifier.Classify(
                  linked with { HasMixedExecution = true }, [operation]) == PositionOwnership.Unknown,
            "mixed software and manual executions remain unknown");
        check(PositionOwnershipClassifier.Classify(
                  linked with { Account = "Other account" }, [operation]) == PositionOwnership.Unknown,
            "matching IDs from a different account do not establish ownership");
        check(PositionOwnershipClassifier.Classify(linked, [operation, operation]) == PositionOwnership.Unknown,
            "more than one matching operation leaves ownership unknown");
        check(PositionOwnershipClassifier.Classify(
                  new ExposureIdentity("Venue", "Account", "BTC-PERP", ["fill-1"], ["order-1"]), [operation])
              == PositionOwnership.Unknown,
            "an order ID cannot match an execution ID or vice versa");
        var partiallyOverlappingOperation = operation with { ExecutionIds = ["fill-other"] };
        check(PositionOwnershipClassifier.Classify(linked, [operation, partiallyOverlappingOperation])
              == PositionOwnership.Unknown,
            "a second same-scope operation with a partial typed-ID overlap makes ownership ambiguous");
        check(PositionOwnershipClassifier.Classify(
                  new ExposureIdentity("", "Account", "BTC-PERP", ["order-1"], ["fill-1"]),
                  [operation with { Venue = "" }]) == PositionOwnership.Unknown,
            "empty scope fields cannot establish software ownership");
        check(PositionOwnershipClassifier.Classify(linked, [operation, operation with { Account = "" }])
              == PositionOwnership.Unknown,
            "an unknown-scope operation with overlapping typed IDs cannot be ignored as proof of ownership");
    }

    public static void CheckNativeWpf(Action<bool, string> check, string fixtureRoot)
    {
        if (Application.Current is null)
        {
            var app = new App();
            app.InitializeComponent();
        }

        var settingsPath = Path.Combine(fixtureRoot, "account-wpf", "settings.json");
        var store = new SettingsStore(settingsPath);
        var profile = new SettingsProfile { SelectedMode = "TrueNorth AI setups" };
        var saved = store.Save(profile);
        check(saved.Succeeded, "account UI fixture saves an isolated starting mode");
        var savedBytes = File.ReadAllBytes(settingsPath);
        var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(store, store.Load(), controller,
            new TrueNorthSessionChecks.RecordingSessionService());

        try
        {
            window.Width = 760;
            window.Height = 560;
            window.Show();
            window.Activate();
            window.UpdateLayout();
            PumpDispatcher();

            var dashboard = Get<AccountDashboardView>(window, "AccountDashboardPage");
            var history = Get<AccountHistoryView>(window, "AccountHistoryPage");
            var navigation = Get<ListBox>(window, "NavigationList");
            var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
            var accountBadge = Get<Border>(window, "AccountTestDataBadge");

            check(dashboard.IsShowingExample == false && history.IsShowingExample == false
                  && accountBadge.Visibility == Visibility.Collapsed,
                "first overview and history start without account data or a test badge");
            check(dashboard.ExampleButton.Focusable && dashboard.ExampleButton.IsTabStop
                  && AutomationProperties.GetName(dashboard.ExampleButton).Contains("тестовые данные", StringComparison.OrdinalIgnoreCase),
                "sample CTA has a keyboard stop and an explicit accessible name");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-empty-top-760x560.png"));
            dashboard.ExampleButton.BringIntoView();
            PumpDispatcher();
            check(dashboard.ExampleButton.IsVisible,
                "the sample CTA can be brought into the minimum-width viewport by scrolling "
                + $"(window={window.IsLoaded}/{window.IsVisible}/{window.ActualWidth:0}x{window.ActualHeight:0}; "
                + $"dashboard={dashboard.Visibility}/{dashboard.IsVisible}/{dashboard.ActualWidth:0}x{dashboard.ActualHeight:0}; "
                + $"button={dashboard.ExampleButton.Visibility}/{dashboard.ExampleButton.IsVisible}/"
                + $"{dashboard.ExampleButton.ActualWidth:0}x{dashboard.ExampleButton.ActualHeight:0}; "
                + $"scroll={scroll.ScrollableHeight:0}/{scroll.VerticalOffset:0})");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-empty-760x560.png"));

            Focus(dashboard.ExampleButton);
            InvokeButton(dashboard.ExampleButton);
            PumpDispatcher();
            check(dashboard.IsShowingExample && history.IsShowingExample
                  && accountBadge.Visibility == Visibility.Visible,
                "WPF Button InvokePattern opens the local example in overview and history");
            check(dashboard.OpenHistoryButton.IsKeyboardFocused && dashboard.OpenHistoryButton.IsVisible,
                "entering the example moves focus from the hidden CTA to the visible history action");
            check(File.ReadAllBytes(settingsPath).SequenceEqual(savedBytes)
                  && store.Load().Profile.SelectedMode == "TrueNorth AI setups",
                "opening the local example does not change the saved operating mode");

            window.UpdateLayout();
            PumpDispatcher();
            check(scroll.ScrollableHeight > 0, "the example overview remains scrollable at 760 by 560");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-example-760x560.png"));
            scroll.ScrollToVerticalOffset(Math.Min(200, scroll.ScrollableHeight));
            window.UpdateLayout();
            PumpDispatcher();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-example-data-760x560.png"));
            check(accountBadge.Visibility == Visibility.Visible,
                "the fixed test-data badge stays visible while reading account sections");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            window.UpdateLayout();
            PumpDispatcher();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-example-scroll-760x560.png"));
            check(accountBadge.Visibility == Visibility.Visible,
                "the fixed test-data badge remains visible after scrolling the overview");

            scroll.ScrollToTop();
            window.UpdateLayout();
            Focus(dashboard.OpenHistoryButton);
            InvokeButton(dashboard.OpenHistoryButton);
            PumpDispatcher();
            check(Get<FrameworkElement>(window, "AccountHistoryPage").Visibility == Visibility.Visible
                  && accountBadge.Visibility == Visibility.Visible,
                "history opens as a separate view and retains the fixed test-data badge "
                + $"(selected={navigation.SelectedIndex}; page={Get<FrameworkElement>(window, "AccountHistoryPage").Visibility}; "
                + $"badge={accountBadge.Visibility}; sourceFocus={dashboard.OpenHistoryButton.IsKeyboardFocused})");
            check(history.BackButton.IsKeyboardFocused && history.BackButton.IsVisible,
                "entering history moves focus to its visible return action "
                + $"(focus={(Keyboard.FocusedElement as FrameworkElement)?.Name ?? Keyboard.FocusedElement?.GetType().Name ?? "none"}; "
                + $"target={history.BackButton.Visibility}/{history.BackButton.IsVisible}/{history.BackButton.IsKeyboardFocused})");
            check(history.VenueFilter.Focusable && history.VenueFilter.IsTabStop
                  && history.FromFilter.Focusable && history.FromFilter.IsTabStop
                  && history.ToFilter.Focusable && history.ToFilter.IsTabStop
                  && history.ResetButton.Focusable && history.ResetButton.IsTabStop,
                "venue and close-period filters remain on the native keyboard focus path");
            var initialHistoryRows = (IReadOnlyList<ClosedTradeRow>)history.Rows.ItemsSource!;
            check(history.TradeCount == 3
                  && initialHistoryRows.Any(row => row.SnapshotLabel == "Данные прошлого снимка")
                  && initialHistoryRows.Any(row => row.SnapshotLabel == "История неполная")
                  && initialHistoryRows.All(row => row.OpenedAt.StartsWith("Открыта:", StringComparison.Ordinal)
                      && row.ClosedAt.StartsWith("Закрыта:", StringComparison.Ordinal)
                      && row.RealizedPnl.StartsWith("Реализованный PnL:", StringComparison.Ordinal)),
                "history labels previous and incomplete rows and names opening, closing and realized PnL values");
            check(history.FilterSummary.Contains("Покрытие:", StringComparison.Ordinal)
                  && history.FilterSummary.Contains("North Perp — неполная; строк 2", StringComparison.Ordinal)
                  && history.FilterSummary.Contains("South Perp — прошлый снимок; строк 1", StringComparison.Ordinal),
                "history coverage identifies the selected venue snapshots and their read state");

            var northSnapshot = AccountExample.Create().Single(snapshot => snapshot.Venue == "North Perp");
            var confirmedEmpty = AccountSection<IReadOnlyList<ClosedTrade>>.ConfirmedEmpty(
                Array.Empty<ClosedTrade>(), northSnapshot.ObservedAt, AccountExample.SourceName);
            history.SetSnapshots([northSnapshot with { History = confirmedEmpty }]);
            PumpDispatcher();
            check(history.TradeCount == 0 && history.NoFilteredRowsVisible
                  && history.EmptyMessage.StartsWith("Подтверждённо пусто", StringComparison.Ordinal)
                  && history.FilterSummary.Contains("подтверждённо пусто", StringComparison.Ordinal),
                "history distinguishes a confirmed-empty result from a filter with no rows");

            var lostHistory = AccountSection<IReadOnlyList<ClosedTrade>>.AccessLost(
                AccountExample.SourceName, "Чтение истории недоступно в проверке.", northSnapshot.ObservedAt.AddHours(-1));
            history.SetSnapshots([northSnapshot with { History = lostHistory }]);
            PumpDispatcher();
            check(history.TradeCount == 0 && history.NoFilteredRowsVisible
                  && history.FilterSummary.Contains("доступ утрачен", StringComparison.Ordinal)
                  && history.EmptyMessage.Contains("актуальность истории не подтверждена", StringComparison.Ordinal)
                  && !history.EmptyMessage.Contains("Подтверждённо пусто", StringComparison.Ordinal),
                "history presents unavailable coverage with a neutral zero-row message");

            history.SetSnapshots(AccountExample.Create());
            PumpDispatcher();
            var tabMovedToVenue = history.BackButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            PumpDispatcher();
            check(tabMovedToVenue && ReferenceEquals(Keyboard.FocusedElement, history.VenueFilter),
                "Tab traversal from the return action reaches the venue filter through the WPF focus tree");
            var tabMovedToDate = history.VenueFilter.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            PumpDispatcher();
            check(tabMovedToDate && history.FromFilter.IsKeyboardFocusWithin
                  && history.FromFilter.FocusVisualStyle is not null,
                "Tab traversal reaches the native date field with its visible focus style");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "history-example-date-focused-760x560.png"));
            var dateTextBox = FindVisualChild<DatePickerTextBox>(history.FromFilter);
            check(dateTextBox is not null
                  && dateTextBox.Foreground == (System.Windows.Media.Brush)window.FindResource("TextBrush"),
                "native date text and watermark inherit the readable text color token");

            Focus(history.VenueFilter);
            history.VenueFilter.SelectedIndex = 1;
            PumpDispatcher();
            check(history.VenueFilter.SelectedIndex == 1,
                "the focused native WPF venue filter accepts a selection and updates history");
            history.FromFilter.SelectedDate = new DateTime(2026, 11, 2);
            history.ToFilter.SelectedDate = new DateTime(2026, 11, 9);
            PumpDispatcher();
            var filteredRows = (IReadOnlyList<ClosedTradeRow>)history.Rows.ItemsSource!;
            check(history.TradeCount == 1 && filteredRows[0].Venue == "North Perp"
                  && filteredRows[0].RealizedPnl.StartsWith("Реализованный PnL:", StringComparison.Ordinal)
                  && filteredRows[0].RealizedPnl.EndsWith("USDC", StringComparison.Ordinal),
                "venue and close-date filters return one labeled result with its own currency");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "history-example-filter-dates-760x560.png"));

            history.FromFilter.SelectedDate = new DateTime(2026, 11, 9);
            history.ToFilter.SelectedDate = new DateTime(2026, 11, 2);
            PumpDispatcher();
            check(history.TradeCount == 0 && history.FilterSummary.Contains("позже", StringComparison.Ordinal),
                "a reversed close-date interval is reported without results");
            Focus(history.ResetButton);
            InvokeButton(history.ResetButton);
            PumpDispatcher();
            check(history.TradeCount == 3 && history.VenueFilter.SelectedIndex == 0
                  && history.FromFilter.SelectedDate is null && history.ToFilter.SelectedDate is null,
                "WPF InvokePattern reset restores the full history filter set "
                + $"(count={history.TradeCount}; venue={history.VenueFilter.SelectedIndex}; "
                + $"from={history.FromFilter.SelectedDate}; to={history.ToFilter.SelectedDate}; "
                + $"focus={Keyboard.FocusedElement?.GetType().Name})");

            scroll.ScrollToTop();
            window.UpdateLayout();
            PumpDispatcher();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "history-example-760x560.png"));
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            window.UpdateLayout();
            PumpDispatcher();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "history-example-scroll-760x560.png"));
            check(accountBadge.Visibility == Visibility.Visible,
                "the fixed test-data badge remains visible after scrolling history");

            Focus(history.BackButton);
            InvokeButton(history.BackButton);
            PumpDispatcher();
            check(navigation.SelectedIndex == 0 && dashboard.IsShowingExample
                  && accountBadge.Visibility == Visibility.Visible
                  && dashboard.OpenHistoryButton.IsKeyboardFocused,
                "WPF InvokePattern return from history restores the example overview and focus");

            Focus(dashboard.ExitButton);
            InvokeButton(dashboard.ExitButton);
            PumpDispatcher();
            check(!dashboard.IsShowingExample && dashboard.ExampleButton.IsKeyboardFocused
                  && accountBadge.Visibility == Visibility.Collapsed,
                "exiting the overview example restores focus to its visible local-example action");
            InvokeButton(dashboard.ExampleButton);
            PumpDispatcher();
            check(dashboard.IsShowingExample && dashboard.OpenHistoryButton.IsKeyboardFocused,
                "re-entering the example restores focus to its visible history action");
            Focus(dashboard.OpenHistoryButton);
            InvokeButton(dashboard.OpenHistoryButton);
            PumpDispatcher();
            check(history.BackButton.IsKeyboardFocused,
                "opening history through WPF InvokePattern restores focus to its visible return action");
            Focus(history.ExitButton);
            InvokeButton(history.ExitButton);
            PumpDispatcher();
            check(!dashboard.IsShowingExample && !history.IsShowingExample
                  && history.ExampleButton.IsKeyboardFocused
                  && accountBadge.Visibility == Visibility.Collapsed,
                "exiting from history restores focus to its visible local-example action");
            InvokeButton(history.ExampleButton);
            PumpDispatcher();
            check(history.IsShowingExample && history.BackButton.IsKeyboardFocused,
                "opening the history example restores focus to its visible return action");
            Focus(history.ExitButton);
            InvokeButton(history.ExitButton);
            PumpDispatcher();
            check(!dashboard.IsShowingExample && !history.IsShowingExample
                  && history.ExampleButton.IsKeyboardFocused
                  && accountBadge.Visibility == Visibility.Collapsed,
                "WPF InvokePattern exit restores the empty account state and visible history CTA");
            check(File.ReadAllBytes(settingsPath).SequenceEqual(savedBytes)
                  && store.Load().Profile.SelectedMode == "TrueNorth AI setups",
                "example entry and exit preserve saved settings byte for byte");

            navigation.SelectedIndex = 2;
            PumpDispatcher();
            check(Get<FrameworkElement>(window, "SimulationContent").Visibility == Visibility.Visible
                  && Get<Border>(window, "SimulationStatusBadge").Visibility == Visibility.Visible
                  && Get<Border>(window, "AccountTestDataBadge").Visibility == Visibility.Collapsed,
                "simulation keeps its own status and never inherits the account example label");
            navigation.SelectedIndex = 3;
            PumpDispatcher();
            check(Get<FrameworkElement>(window, "AiTestPage").Visibility == Visibility.Visible
                  && Get<Border>(window, "SimulationStatusBadge").Visibility == Visibility.Collapsed
                  && store.Load().Profile.SelectedMode == "TrueNorth AI setups",
                "AI test remains separate and the saved mode stays unchanged");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
        }
    }

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private static void Focus(FrameworkElement element)
    {
        element.BringIntoView();
        PumpDispatcher();
        var focused = Keyboard.Focus(element);
        if (!ReferenceEquals(focused, element))
            throw new InvalidOperationException($"Could not focus WPF control: {element.Name} (visible={element.IsVisible}, enabled={element.IsEnabled})");
        PumpDispatcher();
    }

    private static void InvokeButton(Button button)
    {
        if (!button.IsVisible || !button.IsEnabled)
            throw new InvalidOperationException($"Cannot invoke hidden or disabled WPF button: {button.Name}");

        var peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new InvalidOperationException($"WPF automation peer not found: {button.Name}");
        var invokePattern = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException($"WPF InvokePattern not available: {button.Name}");
        invokePattern.Invoke();
    }

    private static void RenderWindow(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        checkRenderedImage(path);

        static void checkRenderedImage(string imagePath)
        {
            if (new FileInfo(imagePath).Length == 0)
                throw new InvalidOperationException($"WPF screenshot is empty: {imagePath}");
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string ArtifactsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "006-account-dashboard.md")))
                return Path.Combine(directory.FullName, "artifacts", "006-account-dashboard");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts", "006-account-dashboard");
    }
}
