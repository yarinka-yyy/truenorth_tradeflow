using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Orders;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Verification;

internal static class OrderLifecycleChecks
{
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private const string Venue = "Scenario Venue B";
    private const string Account = "Scenario Account B";

    internal static void CheckCore(Action<bool, string> check, string root)
    {
        CheckMarketAndPartialFill(check, Path.Combine(root, "orders-market"));
        CheckStoppedLimitCancellation(check, Path.Combine(root, "orders-stop-cancel"));
        CheckUnknownEntryRestart(check, Path.Combine(root, "orders-entry-restart"));
        CheckReconciledDomainMismatchBlocks(check, Path.Combine(root, "orders-reconciled-domain-block"));
        CheckIncompleteProtectionConfirmationBlocks(check, Path.Combine(root, "orders-incomplete-protection"));
        CheckUnknownCancelAndProtectionRestart(check, Path.Combine(root, "orders-action-restart"));
        CheckRestartProtectionRejectionCancelsFirst(check, Path.Combine(root, "orders-repair-rejection-restart"));
        CheckCancelFillAndTakeProfitRace(check, Path.Combine(root, "orders-cancel-races"));
        CheckStaleAndRejectedEntry(check, Path.Combine(root, "orders-entry-blocks"));
        CheckEmergencyCloseSchedule(check, Path.Combine(root, "orders-close-retry"));
        CheckUnknownEmergencyCloseRestart(check, Path.Combine(root, "orders-close-restart"));
        CheckEmergencyCloseNoticeAfterRestart(check, Path.Combine(root, "orders-close-notice-restart"));
        CheckEmergencyRetryabilityAndSerialization(check, Path.Combine(root, "orders-retryability"));
        CheckMixedOwnershipBlocksChanges(check, Path.Combine(root, "orders-mixed-ownership"));
        CheckNormalLimitCompletion(check, Path.Combine(root, "orders-normal-limit-completion"));
        CheckPartialFillDeadlines(check, Path.Combine(root, "orders-partial-deadlines"));
        CheckOwnerDecisionsAndFurtherFills(check, Path.Combine(root, "orders-owner-decisions"));
        CheckTransferAndPendingOutcome(check, Path.Combine(root, "orders-transfer-pending"));
        CheckAmbiguousRemainderBlocksDueMutation(check, Path.Combine(root, "orders-ambiguous-due"));
    }

    internal static void CheckNativeWpf(Action<bool, string> check, string root)
    {
        if (Application.Current is null)
        {
            var app = new App();
            app.InitializeComponent();
        }

        var settingsPath = Path.Combine(root, "order-lifecycle-ui", "settings.json");
        var store = new SettingsStore(settingsPath);
        var saved = store.Save(new SettingsProfile { SelectedMode = "TrueNorth AI setups" });
        check(saved.Succeeded, "local owner-decision UI fixture saves an isolated settings profile");
        var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(store, store.Load(), controller,
            new TrueNorthSessionChecks.RecordingSessionService(),
            orderLifecycleExampleFactory: () => new LocalOrderLifecycleExampleSession(
                Path.Combine(root, "order-lifecycle-ui", "example-journals")));
        try
        {
            window.Width = 760;
            window.Height = 560;
            window.Show();
            window.Activate();
            window.UpdateLayout();
            PumpDispatcher();
            var dashboard = (AccountDashboardView)window.FindName("AccountDashboardPage")!;
            Focus(dashboard.ExampleButton);
            InvokeButton(dashboard.ExampleButton);
            PumpDispatcher();
            var firstSession = window.OrderLifecycleExampleSession
                ?? throw new InvalidOperationException("The isolated lifecycle example did not start.");
            var beforeContinue = firstSession.Recovery.State.ManagedOrders.Single();
            var beforeContinuePresentation = firstSession.CreatePresentation();
            check(dashboard.OrderLifecyclePanelElement.Visibility == Visibility.Visible
                  && dashboard.ContinueOwnerParametersButton.IsVisible
                  && dashboard.TransferToManualButton.IsVisible
                  && beforeContinue.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && beforeContinue.Status == ManagedOrderStatus.OwnerDecisionRequired
                  && Path.GetFullPath(firstSession.JournalPath).StartsWith(
                      Path.GetFullPath(Path.Combine(root, "order-lifecycle-ui", "example-journals")),
                      StringComparison.OrdinalIgnoreCase),
                "the overview reaches owner choice in a separate example journal and exposes both actual decisions");
            check(dashboard.OrderLifecycleOrderElement.Text == beforeContinuePresentation.Order,
                "the overview renders the order and cumulative fill from the current journal state");
            check(dashboard.OrderLifecycleQuantitiesElement.Text == beforeContinuePresentation.Quantities,
                "the overview renders current position and exact remainder from the current journal state");
            check(dashboard.OrderLifecycleDeadlineElement.Text == beforeContinuePresentation.FirstFillDeadline,
                "the overview renders first-fill and cancellation deadlines from the current journal state");
            check(dashboard.OrderLifecycleProtectionElement.Text == beforeContinuePresentation.Protection,
                "the overview renders owner protection and coverage from the current journal state");
            check(dashboard.OrderLifecycleOwnerChangeElement.Text == beforeContinuePresentation.OwnerChange
                  && dashboard.OrderLifecycleDecisionStatusElement.Text == beforeContinuePresentation.DecisionResult,
                "the overview renders owner choice and decision status from the current journal state");
            check(firstSession.OwnerChoiceNotices.Count == 1
                  && firstSession.OwnerChoiceNotices.Single().Id == beforeContinue.OwnerDecisionNotificationId
                  && firstSession.OwnerChoiceNotices.Single().Category == NotificationCategory.OrderChange,
                "a proven manual TP/SL change emits one stable owner-choice notification through the 008 dispatcher");
            check(dashboard.ContinueOwnerParametersButton.Focusable
                  && dashboard.ContinueOwnerParametersButton.IsTabStop
                  && dashboard.TransferToManualButton.Focusable
                  && dashboard.TransferToManualButton.IsTabStop
                  && AutomationProperties.GetName(dashboard.ContinueOwnerParametersButton)
                      .Contains("новыми параметрами", StringComparison.OrdinalIgnoreCase)
                  && AutomationProperties.GetName(dashboard.TransferToManualButton)
                      .Contains("владельцу", StringComparison.OrdinalIgnoreCase),
                "owner choices have keyboard stops and explicit accessible names");

            Focus(dashboard.ContinueOwnerParametersButton);
            var movedToTransfer = dashboard.ContinueOwnerParametersButton.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next));
            PumpDispatcher();
            check(movedToTransfer && dashboard.TransferToManualButton.IsKeyboardFocused,
                "Tab focus moves from continuing with parameters to manual transfer");
            ScrollLifecyclePanelIntoView(window, dashboard);
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "order-lifecycle-owner-choice-760x560.png"));
            InvokeButton(dashboard.ContinueOwnerParametersButton);
            PumpDispatcher();
            var continued = firstSession.Recovery.State.ManagedOrders.Single();
            var afterContinuePresentation = firstSession.CreatePresentation();
            check(dashboard.OrderLifecycleDecisionStatusElement.Text == afterContinuePresentation.DecisionResult
                  && continued.ManagementMode == ManagedOrderManagementMode.Application
                  && continued.OwnerParametersAccepted
                  && continued.TakeProfit == 72400m
                  && continued.StopLoss == 68800m
                  && dashboard.OrderLifecycleQuantitiesElement.Text == afterContinuePresentation.Quantities
                  && dashboard.OrderLifecycleProtectionElement.Text == afterContinuePresentation.Protection
                  && dashboard.OrderLifecycleOwnerChangeElement.Text == afterContinuePresentation.OwnerChange
                  && !dashboard.ContinueOwnerParametersButton.IsEnabled
                  && !dashboard.TransferToManualButton.IsEnabled,
                "WPF InvokePattern accepts verified owner levels in the example journal and redraws the persisted result");
            ScrollLifecyclePanelIntoView(window, dashboard);
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "order-lifecycle-after-continue-760x560.png"));

            Focus(dashboard.ExitButton);
            InvokeButton(dashboard.ExitButton);
            PumpDispatcher();
            Focus(dashboard.ExampleButton);
            InvokeButton(dashboard.ExampleButton);
            PumpDispatcher();
            var secondSession = window.OrderLifecycleExampleSession
                ?? throw new InvalidOperationException("The transfer example did not start.");
            var beforeTransfer = secondSession.Recovery.State.ManagedOrders.Single();
            check(secondSession.JournalPath != firstSession.JournalPath
                  && !File.Exists(firstSession.JournalPath)
                  && beforeTransfer.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && dashboard.OrderLifecycleDecisionStatusElement.Text
                      == secondSession.CreatePresentation().DecisionResult,
                "reopening the overview creates a fresh example journal and restores its persisted owner decision");
            Focus(dashboard.ContinueOwnerParametersButton);
            check(dashboard.ContinueOwnerParametersButton.MoveFocus(
                    new TraversalRequest(FocusNavigationDirection.Next))
                  && dashboard.TransferToManualButton.IsKeyboardFocused,
                "reopening the example restores keyboard traversal to the transfer decision");
            ScrollLifecyclePanelIntoView(window, dashboard);
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "order-lifecycle-transfer-focus-760x560.png"));
            InvokeButton(dashboard.TransferToManualButton);
            PumpDispatcher();
            var transferred = secondSession.Recovery.State.ManagedOrders.Single();
            var afterTransferPresentation = secondSession.CreatePresentation();
            check(dashboard.OrderLifecycleDecisionStatusElement.Text == afterTransferPresentation.DecisionResult
                  && transferred.ManagementMode == ManagedOrderManagementMode.Manual
                  && transferred.TransferredPositionQuantity == transferred.PositionQuantity
                  && transferred.TransferredRemainderQuantity == transferred.RemainingQuantity
                  && dashboard.OrderLifecycleQuantitiesElement.Text == afterTransferPresentation.Quantities
                  && dashboard.OrderLifecycleProtectionElement.Text == afterTransferPresentation.Protection
                  && dashboard.OrderLifecycleOwnerChangeElement.Text == afterTransferPresentation.OwnerChange
                  && !dashboard.ContinueOwnerParametersButton.IsEnabled
                  && !dashboard.TransferToManualButton.IsEnabled,
                "WPF InvokePattern transfers the position and remainder in the example journal and redraws its saved values");
            ScrollLifecyclePanelIntoView(window, dashboard);
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "order-lifecycle-after-transfer-760x560.png"));
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
        }
    }

    private static void CheckMarketAndPartialFill(Action<bool, string> check, string root)
    {
        var market = CreateIntent("Market", InitialTime);
        using (var harness = new Harness(Path.Combine(root, "market"), InitialTime))
        {
            var opened = harness.Orders.OpenAsync(market.Intent, market.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            if (harness.Orders.Orders.Count == 0)
                throw new InvalidOperationException($"Market open failed: {opened.Status}; recovery={harness.Recovery.StatusText}");
            var state = harness.Orders.Orders.Single();
            var position = harness.Adapter.Positions.Single();
            var protection = harness.Adapter.Protections.Single();
            check(opened.Succeeded && state.EntryOrderId is not null && state.PositionId == position.PositionId
                  && state.Status == ManagedOrderStatus.Protected,
                "Market entry is saved, submitted and reconciled to its exact owned position and identifiers");
            check(position.Quantity == market.Intent.Quantity && protection.TakeProfit?.Value == market.Intent.TakeProfit
                  && protection.StopLoss?.Value == market.Intent.StopLoss
                  && protection.TakeProfitCoveredQuantity == position.Quantity
                  && protection.StopLossCoveredQuantity == position.Quantity,
                "TP and SL are both confirmed at the requested levels and cover the actual full position size");
            check(harness.Adapter.ActionCalls.SequenceEqual(["entry", "protect"])
                  && harness.Adapter.DurableTransferSeenBeforeEveryCallback,
                "the journal reaches TransferStarted with the persisted intent before every controlled adapter callback");
            check(harness.Windows.Sent.Select(message => message.Id).Contains(state.IntentId + ":opened")
                  && harness.Windows.Sent.Select(message => message.Id).Contains(state.IntentId + ":protected"),
                "open and protection notifications use stable journaled event identifiers");

            harness.Adapter.ClearProtection();
            harness.Recovery.ReportFailureAsync(Venue, Account, "fixture entry pause", harness.Settings)
                .GetAwaiter().GetResult();
            var repaired = harness.Orders.RepairProtectionAsync(state.IntentId, savedOperatingIntent: false)
                .GetAwaiter().GetResult();
            check(repaired.Succeeded && harness.Adapter.ActionCalls.Count(action => action == "protect") == 2
                  && !harness.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
                "a fresh exact protection repair remains available after Stop and a venue entry pause");
        }

        var limit = CreateIntent("Limit", InitialTime);
        using var partial = new Harness(Path.Combine(root, "partial-limit"), InitialTime);
        partial.Adapter.EntryFilledQuantity = limit.Intent.Quantity / 2m;
        var partialResult = partial.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var partialState = partial.Orders.Orders.Single();
        var partialPosition = partial.Adapter.Positions.Single();
        var partialProtection = partial.Adapter.Protections.Single();
        check(partialResult.Succeeded && partialState.FilledQuantity == limit.Intent.Quantity / 2m
              && partialState.RemainingQuantity == limit.Intent.Quantity / 2m
              && partialState.PositionQuantity == partialPosition.Quantity
              && partialProtection.TakeProfitCoveredQuantity == partialPosition.Quantity
              && partialProtection.StopLossCoveredQuantity == partialPosition.Quantity,
            "a partial Limit fill protects the actual filled quantity while retaining the exact unfilled remainder");

        using var failedProtection = new Harness(Path.Combine(root, "partial-protection-rejected"), InitialTime);
        failedProtection.Adapter.EntryFilledQuantity = limit.Intent.Quantity / 2m;
        failedProtection.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
        var protectedAfterCancel = failedProtection.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        check(protectedAfterCancel.Succeeded
              && failedProtection.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel", "close"])
              && failedProtection.Adapter.Orders.Count == 0 && failedProtection.Adapter.Positions.Count == 0
              && failedProtection.Adapter.CloseRequests.Single().Quantity == limit.Intent.Quantity / 2m,
            "a rejected repair cancels an owned Limit remainder without repeating the unchanged repair, then closes the actual partial fill");
    }

    private static void CheckStoppedLimitCancellation(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.EntryFilledQuantity = 0m;
        var opened = harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        check(opened.Succeeded && state.Status == ManagedOrderStatus.EntryOpen
              && state.FilledQuantity == 0m && state.RemainingQuantity == fixture.Intent.Quantity
              && state.NextEmergencyCloseAt is null && state.FirstPartialFillAt is null
              && state.PartialFillCancelAt is null
              && state.NextManagementCheckAt == InitialTime.AddMinutes(harness.Settings.LimitOrderCheckMinutes),
            "an unfilled Limit gets its check interval without starting the partial-fill or emergency-close deadline");

        harness.Recovery.ReportFailureAsync(Venue, Account, "fixture entry pause", harness.Settings)
            .GetAwaiter().GetResult();
        harness.Adapter.CancelOutcome = RecoveryOperationOutcome.NotExecuted;
        var rejectedCancel = harness.Orders.CancelRemainderAsync(state.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        check(!rejectedCancel.Succeeded && harness.Orders.Orders.Single().RemainingQuantity == fixture.Intent.Quantity
              && harness.Adapter.Orders.Single().RemainingQuantity == fixture.Intent.Quantity,
            "a confirmed cancellation rejection preserves the exact open remainder and its ownership");
        harness.Adapter.CancelOutcome = RecoveryOperationOutcome.Confirmed;
        var cancelled = harness.Orders.CancelRemainderAsync(state.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        check(cancelled.Succeeded && harness.Adapter.ActionCalls.SequenceEqual(["entry", "cancel", "cancel"])
              && harness.Adapter.Orders.Count == 0 && harness.Orders.Orders.Single().RemainingQuantity == 0m,
            "Stop can cancel only the exact software-owned open remainder despite the entry pause");
        check(!harness.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
            "cancelling an owned remainder does not clear the venue entry pause");
    }

    private static void CheckUnknownEntryRestart(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.EntryOutcome = RecoveryOperationOutcome.Unknown;
        var opened = harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        var entryOperation = harness.Recovery.State.Operations.Single();
        check(!opened.Succeeded && state.PendingAction == ManagedOrderAction.Entry
              && entryOperation.Stage == RecoveryOperationStage.Unknown,
            "a lost entry response is durably marked unknown and blocks every follow-up mutation");

        harness.Clock.UtcNow = InitialTime.AddSeconds(harness.Settings.ReadRetryDelaySeconds);
        harness.Adapter.ResolveUnknownEntry(state, entryOperation.OperationId, fixture.Intent.Quantity);
        harness.Restart();
        var reconciled = harness.Orders.ReconcilePendingAsync(state.IntentId).GetAwaiter().GetResult();
        check(reconciled.Succeeded && harness.Adapter.ActionCalls.Count(action => action == "entry") == 1
              && harness.Orders.Orders.Single().Status == ManagedOrderStatus.Protected,
            "restart reconciliation confirms the original entry once, then checks and repairs protection");
        check(harness.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
            "the dependent repair follows confirmed read evidence without retransmitting the entry");
    }

    private static void CheckReconciledDomainMismatchBlocks(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.EntryOutcome = RecoveryOperationOutcome.Unknown;
        harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true).GetAwaiter().GetResult();
        var pending = harness.Orders.Orders.Single();
        var operation = harness.Recovery.State.Operations.Single(candidate =>
            candidate.ActionKind == RecoveryActionKind.Entry);
        harness.Adapter.ResolveUnknownEntry(pending, operation.OperationId, fixture.Intent.Quantity);
        harness.Adapter.CorruptObservationPositionQuantity(operation.OperationId, fixture.Intent.Quantity / 2m);
        harness.Clock.UtcNow = InitialTime.AddSeconds(harness.Settings.ReadRetryDelaySeconds);
        harness.Restart();
        var result = harness.Orders.ReconcilePendingAsync(pending.IntentId).GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        var calls = harness.Adapter.ActionCalls.Count;
        var followUp = harness.Orders.RepairProtectionAsync(pending.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        check(!result.Succeeded && !followUp.Succeeded
              && harness.Recovery.State.Operations.Single(candidate => candidate.OperationId == operation.OperationId)
                  .Stage == RecoveryOperationStage.Reconciled
              && state.PendingOperationId == operation.OperationId
              && !harness.Recovery.IsActionAllowed(Venue, Account, savedOperatingIntent: false,
                  actionKind: RecoveryActionKind.ProtectOwnedPosition, market: fixture.Intent.Contract)
              && harness.Adapter.ActionCalls.Count == calls && calls == 1,
            "a generically reconciled operation with invalid domain evidence retains its exact pending target and blocks follow-up mutations");
    }

    private static void CheckRestartProtectionRejectionCancelsFirst(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
        harness.Adapter.ProtectionOutcome = RecoveryOperationOutcome.Unknown;
        harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true).GetAwaiter().GetResult();
        var pending = harness.Orders.Orders.Single();
        var operation = harness.Recovery.State.Operations.Single(candidate =>
            candidate.ActionKind == RecoveryActionKind.ProtectOwnedPosition);
        harness.Adapter.ResolveUnknownProtectionRejection(pending, operation.OperationId);
        harness.Clock.UtcNow = InitialTime.AddSeconds(harness.Settings.ReadRetryDelaySeconds);
        harness.Restart();
        var result = harness.Orders.ReconcilePendingAsync(pending.IntentId).GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        check(result.Succeeded && state.EntryTerminalState == ManagedEntryTerminalState.Cancelled
              && state.RemainingQuantity == 0m && state.PositionQuantity == 0m
              && state.Status == ManagedOrderStatus.Closed
              && harness.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel", "close"])
              && harness.Adapter.ProtectRequests.Count == 1
              && harness.Adapter.CloseRequests.Single().Quantity == fixture.Intent.Quantity / 2m
              && harness.Adapter.Orders.Count == 0 && harness.Adapter.Positions.Count == 0
              && !harness.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
            "restart-confirmed repair rejection cancels the exact Limit remainder, skips a duplicate repair, and closes only the verified position");
    }

    private static void CheckIncompleteProtectionConfirmationBlocks(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.ProtectionCoverageOverride = fixture.Intent.Quantity / 2m;
        var result = harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        var protection = harness.Adapter.Protections.Single();
        var operation = harness.Recovery.State.Operations.Last(candidate =>
            candidate.ActionKind == RecoveryActionKind.ProtectOwnedPosition);
        check(!result.Succeeded && state.PendingAction == ManagedOrderAction.Protection
              && operation.Stage == RecoveryOperationStage.Unknown
              && protection.TakeProfitCoveredQuantity < state.PositionQuantity
              && protection.StopLossCoveredQuantity < state.PositionQuantity
              && harness.Adapter.ActionCalls.SequenceEqual(["entry", "protect"])
              && !harness.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
            "an incomplete post-repair TP/SL read is treated as unknown, pauses new entries and allows only reconciliation");
    }

    private static void CheckEmergencyCloseSchedule(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
        harness.Adapter.CloseOutcome = RecoveryOperationOutcome.NotExecuted;
        harness.Adapter.CloseRetryability = RecoveryRetryability.Retryable;
        harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true).GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        var firstDue = state.NextEmergencyCloseAt;
        check(state.Status == ManagedOrderStatus.EmergencyCloseWaiting
              && state.EmergencyCloseRetriesUsed == 1
              && firstDue == InitialTime.AddSeconds(10)
              && harness.Adapter.CloseRequests.Single().Quantity == state.PositionQuantity
              && harness.Adapter.CloseRequests.Single().Direction == state.Direction,
            "a confirmed protection rejection schedules an exact full-size reduce-only close and persists its first retry");

        for (var retry = 1; retry <= 3; retry++)
        {
            harness.Clock.UtcNow = state.NextEmergencyCloseAt!.Value;
            harness.Orders.RunDueEmergencyCloseAsync(state.IntentId, savedOperatingIntent: false)
                .GetAwaiter().GetResult();
            state = harness.Orders.Orders.Single();
        }
        check(harness.Adapter.CloseRequests.Count == 4 && state.EmergencyCloseRetriesUsed == 3
              && state.EmergencyCloseNoticeSent
              && state.NextEmergencyCloseAt == harness.Clock.UtcNow.AddSeconds(30),
            "three additional rejected closes each wait ten seconds, then persist a notice and a thirty-second interval");
        check(harness.Windows.Sent.Count(message => message.Id == state.IntentId + ":close-error") == 1
              && !harness.Windows.Sent.Single(message => message.Id == state.IntentId + ":close-error")
                  .SafeText.Contains(Account, StringComparison.Ordinal),
            "the exhausted close sequence emits one stable sanitized error notification");
        check(harness.Windows.Sent.Count(message => message.Id == state.IntentId + ":protection-error") == 1
              && !harness.Windows.Sent.Single(message => message.Id == state.IntentId + ":protection-error")
                  .SafeText.Contains(Account, StringComparison.Ordinal),
            "a protection failure uses a stable sanitized error ID through the notification dispatcher");

        var next = state.NextEmergencyCloseAt!.Value;
        harness.Orders.RunDueEmergencyCloseAsync(state.IntentId, savedOperatingIntent: false).GetAwaiter().GetResult();
        check(harness.Adapter.CloseRequests.Count == 4,
            "a scheduled emergency close does not retry before its persisted due time");
        harness.Clock.UtcNow = next;
        harness.Adapter.CloseOutcome = RecoveryOperationOutcome.Confirmed;
        var closed = harness.Orders.RunDueEmergencyCloseAsync(state.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        check(closed.Succeeded && harness.Orders.Orders.Single().Status == ManagedOrderStatus.Closed
              && harness.Adapter.CloseRequests.Count == 5
              && harness.Adapter.CloseRequests.Last().Quantity == fixture.Intent.Quantity,
            "the post-notice retry closes only the fresh remaining quantity and reconciles the flat position");
    }

    private static void CheckUnknownCancelAndProtectionRestart(Action<bool, string> check, string root)
    {
        var limit = CreateIntent("Limit", InitialTime);
        using (var harness = new Harness(Path.Combine(root, "cancel"), InitialTime))
        {
            harness.Adapter.EntryFilledQuantity = 0m;
            harness.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true).GetAwaiter().GetResult();
            var state = harness.Orders.Orders.Single();
            harness.Adapter.CancelOutcome = RecoveryOperationOutcome.Unknown;
            var canceled = harness.Orders.CancelRemainderAsync(state.IntentId, savedOperatingIntent: false)
                .GetAwaiter().GetResult();
            state = harness.Orders.Orders.Single();
            var operation = harness.Recovery.State.Operations.Single(candidate =>
                candidate.ActionKind == RecoveryActionKind.CancelOwnedOrder);
            check(!canceled.Succeeded && state.PendingAction == ManagedOrderAction.CancelRemainder
                  && harness.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                "an unknown cancellation remains pending with no second submission");
            harness.Clock.UtcNow = InitialTime.AddSeconds(harness.Settings.ReadRetryDelaySeconds);
            harness.Adapter.ResolveUnknownCancel(state, operation.OperationId);
            harness.Restart();
            var reconciled = harness.Orders.ReconcilePendingAsync(state.IntentId).GetAwaiter().GetResult();
            check(reconciled.Succeeded && harness.Orders.Orders.Single().Status == ManagedOrderStatus.Cancelled
                  && harness.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                "restart evidence confirms the exact cancellation without repeating it");
        }

        var market = CreateIntent("Market", InitialTime);
        using var protectionHarness = new Harness(Path.Combine(root, "protection"), InitialTime);
        protectionHarness.Orders.OpenAsync(market.Intent, market.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var protectedState = protectionHarness.Orders.Orders.Single();
        protectionHarness.Adapter.ClearProtection();
        protectionHarness.Adapter.ProtectionOutcome = RecoveryOperationOutcome.Unknown;
        var repair = protectionHarness.Orders.RepairProtectionAsync(protectedState.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        protectedState = protectionHarness.Orders.Orders.Single();
        var repairOperation = protectionHarness.Recovery.State.Operations.Last();
        check(!repair.Succeeded && protectedState.PendingAction == ManagedOrderAction.Protection
              && protectionHarness.Adapter.ActionCalls.Count(action => action == "protect") == 2,
            "an unknown protection repair blocks closing and retry until its read outcome is known");

        protectionHarness.Clock.UtcNow = InitialTime.AddSeconds(protectionHarness.Settings.ReadRetryDelaySeconds);
        protectionHarness.Adapter.ResolveUnknownProtection(protectedState, repairOperation.OperationId);
        protectionHarness.Restart();
        var repairReconciled = protectionHarness.Orders.ReconcilePendingAsync(protectedState.IntentId)
            .GetAwaiter().GetResult();
        check(repairReconciled.Succeeded && protectionHarness.Orders.Orders.Single().Status == ManagedOrderStatus.Protected
              && protectionHarness.Adapter.ActionCalls.Count(action => action == "protect") == 2,
            "restart evidence confirms the original TP/SL repair without sending a duplicate");
    }

    private static void CheckStaleAndRejectedEntry(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using (var moving = new Harness(Path.Combine(root, "moving-clock"), InitialTime))
        {
            moving.Clock.AdvancePerRead = TimeSpan.FromMilliseconds(1);
            var result = moving.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            check(result.Succeeded && moving.Orders.Orders.Single().Status == ManagedOrderStatus.Protected
                  && moving.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
                "a fresh snapshot made after the entry begins allows exactly one protected Market entry");
        }
        using (var stale = new Harness(Path.Combine(root, "stale"), InitialTime))
        {
            stale.Adapter.StaleSnapshot = true;
            var result = stale.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            check(!result.Succeeded && stale.Adapter.ActionCalls.Count == 0
                  && stale.Orders.Orders.Single().Status == ManagedOrderStatus.IntentSaved
                  && !stale.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
                "a stale exact-account snapshot blocks entry and preserves a venue read-recovery pause");
            var intentId = stale.Orders.Orders.Single().IntentId;
            stale.Restart();
            stale.Adapter.StaleSnapshot = false;
            stale.Orders.RunDueChecksAsync(intentId).GetAwaiter().GetResult();
            check(!ManagedOrderState.BlocksNewEntry(stale.Orders.Orders.Single())
                  && stale.Recovery.State.Operations.Count == 0 && stale.Adapter.ActionCalls.Count == 0,
                "restart retires a preflight-only intent without sending a financial action");
        }

        using var rejected = new Harness(Path.Combine(root, "rejected"), InitialTime);
        rejected.Adapter.EntryOutcome = RecoveryOperationOutcome.NotExecuted;
        var rejection = rejected.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        check(!rejection.Succeeded && rejected.Orders.Orders.Single().Status == ManagedOrderStatus.EntryRejected
              && rejected.Adapter.Positions.Count == 0 && rejected.Adapter.Orders.Count == 0,
            "a confirmed Market rejection is recorded without creating a position or entry order");

        using var conflict = new Harness(Path.Combine(root, "account-conflict"), InitialTime);
        conflict.Adapter.ConflictAccountEvidence = true;
        var conflicting = conflict.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        check(!conflicting.Succeeded && conflict.Adapter.ActionCalls.Count == 0
              && conflict.Orders.Orders.Single().Status == ManagedOrderStatus.IntentSaved,
            "entry eligibility is rejected when the selected account snapshot conflicts with the fresh adapter read");
    }

    private static void CheckUnknownEmergencyCloseRestart(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        var opened = harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        check(opened.Succeeded, "the close-restart fixture opens and protects a synthetic position");

        harness.Adapter.ClearProtection();
        harness.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
        harness.Adapter.CloseOutcome = RecoveryOperationOutcome.Unknown;
        var repair = harness.Orders.RepairProtectionAsync(state.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        state = harness.Orders.Orders.Single();
        var closeOperation = harness.Recovery.State.Operations.Single(operation =>
            operation.ActionKind == RecoveryActionKind.EmergencyReduceOnlyClose);
        check(!repair.Succeeded && state.PendingAction == ManagedOrderAction.EmergencyClose
              && closeOperation.Stage == RecoveryOperationStage.Unknown
              && state.PendingPriorStatus == ManagedOrderStatus.EmergencyCloseWaiting
              && state.NextEmergencyCloseAt == InitialTime
              && !harness.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true)
              && harness.Adapter.ActionCalls.Count(action => action == "close") == 1,
            "an unknown emergency-close response remains pending and is never repeated automatically");

        var pendingCalls = harness.Adapter.ActionCalls.ToArray();
        harness.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
        state = harness.Orders.Orders.Single();
        check(state.PendingAction == ManagedOrderAction.EmergencyClose
              && harness.Adapter.ActionCalls.SequenceEqual(pendingCalls),
            "ordinary lifecycle polling cannot pass an untrusted close outcome or reach another protection action");

        harness.Clock.UtcNow = InitialTime.AddSeconds(harness.Settings.ReadRetryDelaySeconds);
        harness.Adapter.ResolveUnknownClose(state, closeOperation.OperationId);
        harness.Restart();
        var reconciled = harness.Orders.ReconcilePendingAsync(state.IntentId).GetAwaiter().GetResult();
        check(reconciled.Succeeded && harness.Orders.Orders.Single().Status == ManagedOrderStatus.Closed
              && harness.Adapter.ActionCalls.Count(action => action == "close") == 1,
            "restart read evidence resolves the unknown close as flat without a duplicate close call");
    }

    private static void CheckMixedOwnershipBlocksChanges(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true).GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        harness.Adapter.SetPositions(harness.Adapter.Positions.Select(position => position with
        {
            Ownership = PositionOwnership.Unknown,
            HasMixedExecution = true
        }).ToArray());
        harness.Adapter.ClearProtection();

        var blocked = harness.Orders.RepairProtectionAsync(state.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        var current = harness.Orders.Orders.Single();
        check(!blocked.Succeeded && current.Status == ManagedOrderStatus.OwnershipAmbiguous
              && harness.Adapter.ActionCalls.Count(action => action == "protect") == 1
              && !harness.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
            "mixed ownership remains visible in the account snapshot and blocks any protection mutation");

        using var unlinked = new Harness(Path.Combine(root, "unlinked-order-id"), InitialTime);
        unlinked.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true).GetAwaiter().GetResult();
        var ownedState = unlinked.Orders.Orders.Single();
        unlinked.Adapter.SetPositions(unlinked.Adapter.Positions.Select(position => position with
        {
            OrderIds = [ownedState.EntryOrderId!, "unexpected-order"]
        }).ToArray());
        unlinked.Adapter.ClearProtection();
        var unlinkedRepair = unlinked.Orders.RepairProtectionAsync(ownedState.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        var ambiguousState = unlinked.Orders.Orders.Single();
        check(!unlinkedRepair.Succeeded && ambiguousState.Status == ManagedOrderStatus.OwnershipAmbiguous
              && ambiguousState.PositionQuantity == fixture.Intent.Quantity
              && unlinked.Adapter.Positions.Single().OrderIds?.Count == 2
              && unlinked.Adapter.ActionCalls.Count(action => action == "protect") == 1
              && !unlinked.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
            "an exposure linked to an extra order ID stays visible and blocks protection as ambiguous");
    }

    private static void CheckNormalLimitCompletion(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using (var fullFill = new Harness(Path.Combine(root, "confirmed-full-fill"), InitialTime))
        {
            fullFill.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            fullFill.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var before = fullFill.Orders.Orders.Single();
            fullFill.Adapter.CompleteLimitFillDuringMonitoring(before);
            fullFill.Clock.UtcNow = before.NextManagementCheckAt!.Value;
            var reconciled = fullFill.Orders.RunDueChecksAsync(before.IntentId).GetAwaiter().GetResult();
            var after = fullFill.Orders.Orders.Single();
            var position = fullFill.Adapter.Positions.Single(candidate => candidate.Contract == fixture.Intent.Contract);
            var protection = fullFill.Adapter.Protections.Single(candidate => candidate.PositionId == after.PositionId);
            check(reconciled.Succeeded && after.EntryTerminalState == ManagedEntryTerminalState.Filled
                  && after.FilledQuantity == fixture.Intent.Quantity && after.RemainingQuantity == 0m
                  && after.PositionQuantity == fixture.Intent.Quantity && position.Quantity == fixture.Intent.Quantity
                  && after.NextManagementCheckAt == fullFill.Clock.UtcNow.AddMinutes(fullFill.Settings.PositionCheckMinutes)
                  && protection.TakeProfit?.Value == fixture.Intent.TakeProfit
                  && protection.StopLoss?.Value == fixture.Intent.StopLoss
                  && protection.TakeProfitCoveredQuantity == fixture.Intent.Quantity
                  && protection.StopLossCoveredQuantity == fixture.Intent.Quantity
                  && fullFill.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "protect"]),
                "normal monitoring uses exact terminal fill evidence to persist a full Limit fill, switch to position checks, and cover the grown position without cancellation");
        }

        using (var unfilled = new Harness(Path.Combine(root, "unfilled-to-full"), InitialTime))
        {
            unfilled.Adapter.EntryFilledQuantity = 0m;
            unfilled.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var unfilledState = unfilled.Orders.Orders.Single();
            var premature = unfilled.Orders.RunDueChecksAsync(unfilledState.IntentId).GetAwaiter().GetResult();
            check(!premature.Succeeded && unfilled.Windows.Sent.All(message => message.Id != unfilledState.IntentId + ":opened"),
                "an unfilled Limit scan before its interval emits no opening notification");

            unfilled.Adapter.CompleteLimitFillDuringMonitoring(unfilledState);
            unfilled.Clock.UtcNow = unfilledState.NextManagementCheckAt!.Value;
            var reconciled = unfilled.Orders.RunDueChecksAsync(unfilledState.IntentId).GetAwaiter().GetResult();
            var full = unfilled.Orders.Orders.Single();
            var position = unfilled.Adapter.Positions.Single(candidate => candidate.Contract == fixture.Intent.Contract);
            var protection = unfilled.Adapter.Protections.Single(candidate => candidate.PositionId == full.PositionId);
            var openedNotice = unfilled.Windows.Sent.Single(message => message.Id == unfilledState.IntentId + ":opened");
            var calls = unfilled.Adapter.ActionCalls.ToArray();
            check(reconciled.Succeeded && full.PositionId is not null
                  && full.EntryTerminalState == ManagedEntryTerminalState.Filled
                  && full.FilledQuantity == fixture.Intent.Quantity && full.RemainingQuantity == 0m
                  && full.PositionQuantity == fixture.Intent.Quantity && position.Quantity == fixture.Intent.Quantity
                  && full.NextManagementCheckAt == unfilled.Clock.UtcNow.AddMinutes(unfilled.Settings.PositionCheckMinutes)
                  && protection.TakeProfitCoveredQuantity == fixture.Intent.Quantity
                  && protection.StopLossCoveredQuantity == fixture.Intent.Quantity
                  && openedNotice.Category == NotificationCategory.Opening
                  && unfilled.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
                "a first full fill from an unfilled Limit learns its proven position ID, protects full size, emits one opening notice, and never cancels");
            unfilled.Clock.UtcNow = full.NextManagementCheckAt!.Value;
            unfilled.Orders.RunDueChecksAsync(full.IntentId).GetAwaiter().GetResult();
            check(unfilled.Windows.Sent.Count(message => message.Id == unfilledState.IntentId + ":opened") == 1
                  && unfilled.Adapter.ActionCalls.SequenceEqual(calls),
                "routine position polling does not repeat the stable opening notification or mutate covered exposure");
        }

        using (var openingDisabled = new Harness(Path.Combine(root, "normal-open-notification-disabled"), InitialTime,
                   settings => settings.OpeningNotifications = false))
        {
            openingDisabled.Adapter.EntryFilledQuantity = 0m;
            openingDisabled.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var notificationState = openingDisabled.Orders.Orders.Single();
            openingDisabled.Adapter.CompleteLimitFillDuringMonitoring(notificationState);
            openingDisabled.Clock.UtcNow = notificationState.NextManagementCheckAt!.Value;
            var reconciled = openingDisabled.Orders.RunDueChecksAsync(notificationState.IntentId).GetAwaiter().GetResult();
            check(reconciled.Succeeded && openingDisabled.Orders.Orders.Single().EntryTerminalState
                      == ManagedEntryTerminalState.Filled
                  && openingDisabled.Windows.Sent.All(message => message.Id != notificationState.IntentId + ":opened"),
                "normal monitoring honors the disabled opening-notification setting while reconciling and protecting the fill");
        }

        using (var mismatched = new Harness(Path.Combine(root, "mismatched-full-fill-proof"), InitialTime))
        {
            mismatched.Adapter.EntryFilledQuantity = 0m;
            mismatched.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var mismatchedState = mismatched.Orders.Orders.Single();
            mismatched.Adapter.CompleteLimitFillDuringMonitoring(mismatchedState);
            mismatched.Adapter.CorruptOriginEntryPositionId(mismatchedState, "unlinked-position");
            mismatched.Clock.UtcNow = mismatchedState.NextManagementCheckAt!.Value;
            var blocked = mismatched.Orders.RunDueChecksAsync(mismatchedState.IntentId).GetAwaiter().GetResult();
            var mismatchedPaused = mismatched.Orders.Orders.Single();
            check(!blocked.Succeeded && mismatchedPaused.Status == ManagedOrderStatus.OwnershipAmbiguous
                  && mismatchedPaused.EntryTerminalState == ManagedEntryTerminalState.Open
                  && mismatchedPaused.PositionId is null && mismatched.Adapter.ActionCalls.SequenceEqual(["entry"])
                  && mismatched.Windows.Sent.All(message => message.Id != mismatchedState.IntentId + ":opened"),
                "a terminal full-fill observation linked to the wrong position remains paused without protection, cancellation, or opening notice");
        }

        using var absent = new Harness(Path.Combine(root, "bare-absence"), InitialTime);
        absent.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
        absent.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var open = absent.Orders.Orders.Single();
        absent.Adapter.RemoveEntryOrderWithoutTerminalEvidence();
        absent.Clock.UtcNow = open.NextManagementCheckAt!.Value;
        var ambiguous = absent.Orders.RunDueChecksAsync(open.IntentId).GetAwaiter().GetResult();
        var paused = absent.Orders.Orders.Single();
        check(!ambiguous.Succeeded && paused.EntryTerminalState == ManagedEntryTerminalState.Open
              && paused.Status == ManagedOrderStatus.OwnershipAmbiguous
              && paused.RemainingQuantity == fixture.Intent.Quantity / 2m
              && absent.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
            "a missing open Limit row without exact terminal evidence remains ambiguous and causes no cancellation or repair");

        foreach (var closeByTakeProfit in new[] { true, false })
        {
            using var closed = new Harness(Path.Combine(root,
                    closeByTakeProfit ? "normal-full-tp-closure" : "normal-full-sl-closure"), InitialTime);
            closed.Adapter.EntryFilledQuantity = fixture.Intent.Quantity;
            closed.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var active = closed.Orders.Orders.Single();
            if (closeByTakeProfit)
                closed.Adapter.CloseByTakeProfit(active);
            else
                closed.Adapter.CloseByStopLoss(active);
            closed.Clock.UtcNow = active.NextManagementCheckAt!.Value;
            var result = closed.Orders.RunDueChecksAsync(active.IntentId).GetAwaiter().GetResult();
            var state = closed.Orders.Orders.Single();
            var notice = closed.Windows.Sent.Single(message => message.Id == active.IntentId + ":closed");
            var calls = closed.Adapter.ActionCalls.ToArray();
            closed.Clock.UtcNow = state.NextManagementCheckAt ?? closed.Clock.UtcNow;
            closed.Orders.RunDueChecksAsync(active.IntentId).GetAwaiter().GetResult();
            check(result.Succeeded && state.Status == ManagedOrderStatus.Closed
                  && state.PositionQuantity == 0m && state.RemainingQuantity == 0m
                  && notice.Category == NotificationCategory.Closing
                  && closed.Windows.Sent.Count(message => message.Id == active.IntentId + ":closed") == 1
                  && closed.Adapter.ActionCalls.SequenceEqual(calls)
                  && calls.SequenceEqual(["entry", "protect"]),
                $"normal monitoring saves a verified full {(closeByTakeProfit ? "TP" : "SL")} closure once, notifies through 008, and sends no cancel or close");
        }

        using (var closingDisabled = new Harness(Path.Combine(root, "normal-close-notification-disabled"), InitialTime,
                   settings => settings.ClosingNotifications = false))
        {
            closingDisabled.Adapter.EntryFilledQuantity = fixture.Intent.Quantity;
            closingDisabled.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var active = closingDisabled.Orders.Orders.Single();
            closingDisabled.Adapter.CloseByTakeProfit(active);
            closingDisabled.Clock.UtcNow = active.NextManagementCheckAt!.Value;
            closingDisabled.Orders.RunDueChecksAsync(active.IntentId).GetAwaiter().GetResult();
            check(closingDisabled.Orders.Orders.Single().Status == ManagedOrderStatus.Closed
                  && closingDisabled.Windows.Sent.All(message => message.Id != active.IntentId + ":closed"),
                "normal monitoring honors the disabled closing-notification setting while persisting verified closure");
        }
    }

    private static void CheckPartialFillDeadlines(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using (var fullLimit = new Harness(Path.Combine(root, "full-limit"), InitialTime))
        {
            fullLimit.Adapter.EntryFilledQuantity = fixture.Intent.Quantity;
            fullLimit.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var full = fullLimit.Orders.Orders.Single();
            fullLimit.Clock.UtcNow = full.NextManagementCheckAt!.Value;
            var checkedPosition = fullLimit.Orders.RunDueChecksAsync(full.IntentId).GetAwaiter().GetResult();
            check(full.FirstPartialFillAt is null && full.PartialFillCancelAt is null
                  && full.NextManagementCheckAt == InitialTime.AddMinutes(fullLimit.Settings.PositionCheckMinutes)
                  && checkedPosition.Succeeded
                  && fullLimit.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
                "a fully filled Limit switches to position checks and never starts remainder cancellation");
        }

        using (var restart = new Harness(Path.Combine(root, "restart-deadline"), InitialTime))
        {
            restart.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            restart.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var initial = restart.Orders.Orders.Single();
            var deadline = InitialTime.AddMinutes(restart.Settings.PartialFillCancelMinutes);
            check(initial.FirstPartialFillAt == InitialTime && initial.PartialFillCancelAt == deadline
                  && initial.NextManagementCheckAt == InitialTime.AddMinutes(restart.Settings.LimitOrderCheckMinutes),
                "the first partial fill saves one cancellation deadline and uses the Limit check interval");

            restart.Restart();
            restart.Clock.UtcNow = deadline;
            var cancelled = restart.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var state = restart.Orders.Orders.Single();
            check(cancelled.Succeeded && state.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && state.FilledQuantity == fixture.Intent.Quantity / 2m && state.RemainingQuantity == 0m
                  && state.PositionQuantity == fixture.Intent.Quantity / 2m
                  && state.PartialFillCancelAt == deadline
                  && restart.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel"]),
                "restart keeps the original deadline and cancels only the exact open remainder once");
            check(restart.Adapter.ProtectRequests.Last().Quantity == fixture.Intent.Quantity / 2m
                  && restart.Adapter.ProtectRequests.Last().PositionId == state.PositionId,
                "cancelling a partial order leaves its actual position covered without expanding the target");
        }

        using (var history = new Harness(Path.Combine(root, "history-deadline"), InitialTime))
        {
            history.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            history.Adapter.OmitFirstFillTime = true;
            history.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            history.Adapter.EntryFillHistoryComplete = true;
            history.Adapter.EntryHistoryFirstFillAt = InitialTime;
            history.Restart();
            history.Clock.UtcNow = InitialTime.AddMinutes(history.Settings.LimitOrderCheckMinutes);
            var reconciled = history.Orders.RunDueChecksAsync(history.Orders.Orders.Single().IntentId)
                .GetAwaiter().GetResult();
            var state = history.Orders.Orders.Single();
            check(reconciled.Succeeded && state.FirstPartialFillAt == InitialTime
                  && state.PartialFillCancelAt == InitialTime.AddMinutes(history.Settings.PartialFillCancelMinutes)
                  && state.EntryTerminalState == ManagedEntryTerminalState.Open
                  && !history.Adapter.ActionCalls.Contains("cancel"),
                "fresh complete fill history restores the earliest first-fill time instead of restarting its timer");
        }

        using (var earlierHistory = new Harness(Path.Combine(root, "earlier-history"), InitialTime))
        {
            earlierHistory.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            earlierHistory.Adapter.OmitFirstFillTime = true;
            earlierHistory.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var initial = earlierHistory.Orders.Orders.Single();
            var laterSavedFill = InitialTime.AddMinutes(1);
            var savedLater = earlierHistory.Recovery.SaveManagedOrderAsync(initial with
                {
                    FirstPartialFillAt = laterSavedFill,
                    PartialFillCancelAt = laterSavedFill.AddMinutes(earlierHistory.Settings.PartialFillCancelMinutes)
                }).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("The controlled later deadline did not persist.");
            earlierHistory.Adapter.EntryFillHistoryComplete = true;
            earlierHistory.Adapter.EntryHistoryFirstFillAt = InitialTime;
            earlierHistory.Clock.UtcNow = savedLater.NextManagementCheckAt!.Value;
            earlierHistory.Orders.RunDueChecksAsync(savedLater.IntentId).GetAwaiter().GetResult();
            var earlier = earlierHistory.Orders.Orders.Single();
            var earliestDeadline = InitialTime.AddMinutes(earlierHistory.Settings.PartialFillCancelMinutes);
            earlierHistory.Adapter.EntryHistoryFirstFillAt = InitialTime.AddMinutes(2);
            earlierHistory.Clock.UtcNow = earlier.NextManagementCheckAt!.Value;
            earlierHistory.Orders.RunDueChecksAsync(earlier.IntentId).GetAwaiter().GetResult();
            var afterLaterHistory = earlierHistory.Orders.Orders.Single();
            check(earlier.FirstPartialFillAt == InitialTime && earlier.PartialFillCancelAt == earliestDeadline
                  && afterLaterHistory.FirstPartialFillAt == InitialTime
                  && afterLaterHistory.PartialFillCancelAt == earliestDeadline,
                "later exact fill history can move a saved first-fill deadline earlier, while a later history value never postpones it");
        }

        using (var unknownHistory = new Harness(Path.Combine(root, "unknown-history"), InitialTime))
        {
            unknownHistory.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            unknownHistory.Adapter.OmitFirstFillTime = true;
            unknownHistory.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var before = unknownHistory.Orders.Orders.Single();
            unknownHistory.Restart();
            unknownHistory.Clock.UtcNow = InitialTime.AddMinutes(unknownHistory.Settings.LimitOrderCheckMinutes);
            var cancelled = unknownHistory.Orders.RunDueChecksAsync(before.IntentId).GetAwaiter().GetResult();
            var after = unknownHistory.Orders.Orders.Single();
            check(cancelled.Succeeded && after.FirstPartialFillAt is null
                  && after.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && after.RemainingQuantity == 0m
                  && unknownHistory.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                "when history cannot establish the first fill after restart, a fresh exact remainder is cancelled once");
        }

        using (var rejected = new Harness(Path.Combine(root, "one-cancel-attempt"), InitialTime))
        {
            rejected.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            rejected.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = rejected.Orders.Orders.Single();
            rejected.Adapter.CancelOutcome = RecoveryOperationOutcome.NotExecuted;
            rejected.Clock.UtcNow = state.PartialFillCancelAt!.Value;
            rejected.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            var afterFailure = rejected.Orders.Orders.Single();
            rejected.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            check(afterFailure.PartialFillCancelAttempted
                  && rejected.Orders.Orders.Single().EntryTerminalState == ManagedEntryTerminalState.Open
                  && rejected.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                "a confirmed cancel rejection is recorded and the due scheduler does not send the same cancel again");
            foreach (var restart in new[] { false, true })
            {
                var beforeFill = rejected.Orders.Orders.Single();
                rejected.Adapter.AddPartialFill(beforeFill, fixture.Intent.Quantity / 8m);
                if (restart)
                    rejected.Restart();
                rejected.Clock.UtcNow = beforeFill.NextManagementCheckAt!.Value;
                rejected.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
                var position = rejected.Adapter.Positions.Single(p => p.PositionId == state.PositionId);
                var protection = rejected.Adapter.Protections.Single(p => p.PositionId == state.PositionId);
                check(position.Quantity == beforeFill.PositionQuantity + fixture.Intent.Quantity / 8m
                      && protection.TakeProfitCoveredQuantity == position.Quantity
                      && protection.StopLossCoveredQuantity == position.Quantity
                      && rejected.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                    $"further Limit fill after refused cancellation receives full protection (restart={restart})");
            }
        }

        foreach (var closeByTakeProfit in new[] { true, false })
        {
            using var closed = new Harness(Path.Combine(root, closeByTakeProfit ? "tp-closure" : "sl-closure"),
                InitialTime);
            closed.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            closed.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = closed.Orders.Orders.Single();
            if (closeByTakeProfit)
                closed.Adapter.CloseByTakeProfit(state);
            else
                closed.Adapter.CloseByStopLoss(state);
            closed.Clock.UtcNow = state.NextManagementCheckAt!.Value;
            var result = closed.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            var after = closed.Orders.Orders.Single();
            check(result.Succeeded && after.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && after.PositionQuantity == 0m && after.RemainingQuantity == 0m
                  && (closeByTakeProfit ? after.TakeProfitClosedQuantity : after.StopLossClosedQuantity)
                      == fixture.Intent.Quantity / 2m
                  && closed.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel"]),
                $"a verified {(closeByTakeProfit ? "TP" : "SL")} closure cancels the exact remaining Limit once");
        }

        using var stopped = new Harness(Path.Combine(root, "stop-preserves-position"), InitialTime);
        stopped.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
        stopped.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var stop = stopped.Orders.StopAsync().GetAwaiter().GetResult();
        var afterStop = stopped.Orders.Orders.Single();
        check(stop.Succeeded && afterStop.EntryTerminalState == ManagedEntryTerminalState.Cancelled
              && afterStop.PositionQuantity == fixture.Intent.Quantity / 2m
              && afterStop.Status == ManagedOrderStatus.Protected
              && stopped.Adapter.Positions.Single(position => position.Contract == fixture.Intent.Contract).Quantity
                  == fixture.Intent.Quantity / 2m
              && stopped.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel"]),
            "Stop cancels the owned remainder while keeping the exact filled position protected");
    }

    private static void CheckOwnerDecisionsAndFurtherFills(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using (var owner = new Harness(Path.Combine(root, "continue"), InitialTime))
        {
            owner.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            owner.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var before = owner.Orders.Orders.Single();
            owner.Adapter.SetManualProtection(before, fixture.Intent.TakeProfit + 1m, fixture.Intent.StopLoss - 1m);
            owner.Clock.UtcNow = before.NextManagementCheckAt!.Value;
            var detected = owner.Orders.RunDueChecksAsync(before.IntentId).GetAwaiter().GetResult();
            var pending = owner.Orders.Orders.Single();
            var deadline = before.PartialFillCancelAt;
            check(!detected.Succeeded && pending.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && pending.Status == ManagedOrderStatus.OwnerDecisionRequired
                  && pending.PendingOwnerTakeProfit == fixture.Intent.TakeProfit + 1m
                  && pending.PendingOwnerStopLoss == fixture.Intent.StopLoss - 1m
                  && pending.PartialFillCancelAt == deadline
                  && owner.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
                "a proven manual TP/SL change pauses ordinary management and keeps the original cancel schedule");

            owner.Restart();
            owner.Adapter.SetManualProtection(pending, fixture.Intent.TakeProfit + 2m, fixture.Intent.StopLoss - 2m);
            var staleChoice = owner.Orders.ContinueWithOwnerParametersAsync(before.IntentId).GetAwaiter().GetResult();
            pending = owner.Orders.Orders.Single();
            check(!staleChoice.Succeeded && pending.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && pending.TakeProfit == fixture.Intent.TakeProfit
                  && pending.PendingOwnerTakeProfit == fixture.Intent.TakeProfit + 2m
                  && owner.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
                "Continue re-reads the displayed TP/SL and requires a new choice when they changed again");

            owner.Adapter.AddPartialFill(pending, fixture.Intent.Quantity / 4m);
            owner.Clock.UtcNow = pending.NextManagementCheckAt!.Value;
            owner.Orders.RunDueChecksAsync(before.IntentId).GetAwaiter().GetResult();
            pending = owner.Orders.Orders.Single();
            var grownPosition = owner.Adapter.Positions.Single(candidate => candidate.PositionId == before.PositionId);
            var grownProtection = owner.Adapter.Protections.Single(candidate => candidate.PositionId == before.PositionId);
            check(pending.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && pending.FilledQuantity == fixture.Intent.Quantity * 3m / 4m
                  && grownPosition.Quantity == pending.PositionQuantity
                  && grownProtection.TakeProfit?.Value == fixture.Intent.TakeProfit + 2m
                  && grownProtection.StopLoss?.Value == fixture.Intent.StopLoss - 2m
                  && grownProtection.TakeProfitCoveredQuantity == grownPosition.Quantity
                  && grownProtection.StopLossCoveredQuantity == grownPosition.Quantity
                  && owner.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "protect"]),
                "a later fill during owner choice receives full protection at the re-read owner levels before either choice is accepted");

            var continued = owner.Orders.ContinueWithOwnerParametersAsync(before.IntentId).GetAwaiter().GetResult();
            var accepted = owner.Orders.Orders.Single();
            owner.Clock.UtcNow = accepted.NextManagementCheckAt!.Value;
            var afterAcceptanceCheck = owner.Orders.RunDueChecksAsync(before.IntentId).GetAwaiter().GetResult();
            var checkedAccepted = owner.Orders.Orders.Single();
            check(continued.Succeeded && accepted.ManagementMode == ManagedOrderManagementMode.Application
                  && accepted.OwnerParametersAccepted && accepted.TakeProfit == fixture.Intent.TakeProfit + 2m
                  && accepted.StopLoss == fixture.Intent.StopLoss - 2m
                  && accepted.PartialFillCancelAt == deadline
                  && accepted.Status == ManagedOrderStatus.Protected
                  && accepted.PositionQuantity == fixture.Intent.Quantity * 3m / 4m
                  && afterAcceptanceCheck.Succeeded
                  && checkedAccepted.ManagementMode == ManagedOrderManagementMode.Application
                  && checkedAccepted.OwnerParametersAccepted
                  && owner.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "protect"]),
                "a second fresh choice accepts the current owner levels without resetting the deadline or reopening the decision");
        }

        using (var timer = new Harness(Path.Combine(root, "owner-timer-keeps-running"), InitialTime))
        {
            timer.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            timer.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = timer.Orders.Orders.Single();
            timer.Adapter.SetManualProtection(state, fixture.Intent.TakeProfit + 1m,
                fixture.Intent.StopLoss - 1m);
            timer.Clock.UtcNow = state.NextManagementCheckAt!.Value;
            timer.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            timer.Clock.UtcNow = state.PartialFillCancelAt!.Value;
            var cancelled = timer.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            var after = timer.Orders.Orders.Single();
            check(cancelled.Succeeded && after.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && after.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && after.PositionQuantity == fixture.Intent.Quantity / 2m
                  && timer.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel"]),
                "the partial-fill deadline continues through an owner decision and cancels only its remainder");
        }

        using (var fill = new Harness(Path.Combine(root, "further-fill"), InitialTime))
        {
            fill.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            fill.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = fill.Orders.Orders.Single();
            fill.Adapter.SetManualProtection(state, fixture.Intent.TakeProfit + 1m, fixture.Intent.StopLoss - 1m);
            fill.Adapter.AddPartialFill(state, fixture.Intent.Quantity / 4m);
            fill.Clock.UtcNow = state.NextManagementCheckAt!.Value;
            fill.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            var updated = fill.Orders.Orders.Single();
            var position = fill.Adapter.Positions.Single(candidate => candidate.PositionId == state.PositionId);
            var protection = fill.Adapter.Protections.Single(candidate => candidate.PositionId == state.PositionId);
            check(updated.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && updated.FilledQuantity == fixture.Intent.Quantity * 3m / 4m
                  && updated.RemainingQuantity == fixture.Intent.Quantity / 4m
                  && updated.PartialFillCancelAt == state.PartialFillCancelAt
                  && updated.PositionQuantity == position.Quantity
                  && protection.TakeProfit?.Value == fixture.Intent.TakeProfit + 1m
                  && protection.StopLoss?.Value == fixture.Intent.StopLoss - 1m
                  && protection.TakeProfitCoveredQuantity == position.Quantity
                  && protection.StopLossCoveredQuantity == position.Quantity
                  && fill.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "protect"]),
                "a further fill receives full protection at the fresh owner levels while the management choice stays open");
            var protectedCalls = fill.Adapter.ActionCalls.ToArray();
            var transfer = fill.Orders.TransferToManualAsync(state.IntentId).GetAwaiter().GetResult();
            var transferredFill = fill.Orders.Orders.Single();
            check(transfer.Succeeded && transferredFill.ManagementMode == ManagedOrderManagementMode.Manual
                  && transferredFill.TransferredPositionQuantity == fixture.Intent.Quantity * 3m / 4m
                  && transferredFill.TransferredRemainderQuantity == fixture.Intent.Quantity / 4m
                  && fill.Adapter.ActionCalls.SequenceEqual(protectedCalls),
                "Transfer after a further fill saves the current position and remainder without another adapter action");
        }

        using (var rejectedCancel = new Harness(Path.Combine(root, "rejected-cancel-then-fill"), InitialTime))
        {
            rejectedCancel.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            rejectedCancel.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var initial = rejectedCancel.Orders.Orders.Single();
            rejectedCancel.Adapter.SetManualProtection(initial, fixture.Intent.TakeProfit + 1m,
                fixture.Intent.StopLoss - 1m);
            rejectedCancel.Clock.UtcNow = initial.NextManagementCheckAt!.Value;
            rejectedCancel.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var awaiting = rejectedCancel.Orders.Orders.Single();
            rejectedCancel.Adapter.CancelOutcome = RecoveryOperationOutcome.NotExecuted;
            rejectedCancel.Clock.UtcNow = awaiting.PartialFillCancelAt!.Value;
            rejectedCancel.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var rejected = rejectedCancel.Orders.Orders.Single();
            rejectedCancel.Adapter.AddPartialFill(rejected, fixture.Intent.Quantity / 4m);
            rejectedCancel.Clock.UtcNow = rejected.NextManagementCheckAt!.Value;
            rejectedCancel.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var afterFill = rejectedCancel.Orders.Orders.Single();
            var position = rejectedCancel.Adapter.Positions.Single(candidate => candidate.PositionId == initial.PositionId);
            var protection = rejectedCancel.Adapter.Protections.Single(candidate => candidate.PositionId == initial.PositionId);
            check(afterFill.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && afterFill.PartialFillCancelAttempted && afterFill.FilledQuantity == fixture.Intent.Quantity * 3m / 4m
                  && protection.TakeProfit?.Value == fixture.Intent.TakeProfit + 1m
                  && protection.StopLoss?.Value == fixture.Intent.StopLoss - 1m
                  && protection.TakeProfitCoveredQuantity == position.Quantity
                  && protection.StopLossCoveredQuantity == position.Quantity
                  && rejectedCancel.Adapter.ActionCalls.Count(action => action == "cancel") == 1
                  && rejectedCancel.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel", "protect"]),
                "a known rejected deadline cancel is not repeated, while a later owner-decision fill is protected at verified owner levels");
        }

        using (var directRace = new Harness(Path.Combine(root, "owner-full-fill-during-cancel"), InitialTime))
        {
            directRace.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            directRace.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var initial = directRace.Orders.Orders.Single();
            directRace.Adapter.SetManualProtection(initial, fixture.Intent.TakeProfit + 1m,
                fixture.Intent.StopLoss - 1m);
            directRace.Clock.UtcNow = initial.NextManagementCheckAt!.Value;
            directRace.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var awaiting = directRace.Orders.Orders.Single();
            directRace.Adapter.CancelRacesToFullFill = true;
            directRace.Adapter.CancelOutcome = RecoveryOperationOutcome.NotExecuted;
            directRace.Clock.UtcNow = awaiting.PartialFillCancelAt!.Value;
            directRace.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var filled = directRace.Orders.Orders.Single();
            var position = directRace.Adapter.Positions.Single(candidate => candidate.PositionId == initial.PositionId);
            var protection = directRace.Adapter.Protections.Single(candidate => candidate.PositionId == initial.PositionId);
            check(filled.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && filled.EntryTerminalState == ManagedEntryTerminalState.Filled
                  && filled.FilledQuantity == fixture.Intent.Quantity && filled.RemainingQuantity == 0m
                  && position.Quantity == fixture.Intent.Quantity
                  && protection.TakeProfit?.Value == fixture.Intent.TakeProfit + 1m
                  && protection.StopLoss?.Value == fixture.Intent.StopLoss - 1m
                  && protection.TakeProfitCoveredQuantity == fixture.Intent.Quantity
                  && protection.StopLossCoveredQuantity == fixture.Intent.Quantity
                  && directRace.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel", "protect"]),
                "a direct cancel/fill race in owner-choice mode repairs the fully grown position at the confirmed owner TP/SL");
        }

        using (var restartRace = new Harness(Path.Combine(root, "owner-full-fill-after-restart"), InitialTime))
        {
            restartRace.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            restartRace.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var initial = restartRace.Orders.Orders.Single();
            restartRace.Adapter.SetManualProtection(initial, fixture.Intent.TakeProfit + 1m,
                fixture.Intent.StopLoss - 1m);
            restartRace.Clock.UtcNow = initial.NextManagementCheckAt!.Value;
            restartRace.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var awaiting = restartRace.Orders.Orders.Single();
            restartRace.Adapter.CancelOutcome = RecoveryOperationOutcome.Unknown;
            restartRace.Clock.UtcNow = awaiting.PartialFillCancelAt!.Value;
            var uncertain = restartRace.Orders.RunDueChecksAsync(initial.IntentId).GetAwaiter().GetResult();
            var pending = restartRace.Orders.Orders.Single();
            var operation = restartRace.Recovery.State.Operations.Single(candidate =>
                candidate.ActionKind == RecoveryActionKind.CancelOwnedOrder);
            restartRace.Adapter.ResolveUnknownCancelToFullFill(pending, operation.OperationId);
            restartRace.Clock.UtcNow = restartRace.Clock.UtcNow.AddSeconds(restartRace.Settings.ReadRetryDelaySeconds);
            restartRace.Restart();
            var reconciled = restartRace.Orders.ReconcilePendingAsync(initial.IntentId).GetAwaiter().GetResult();
            var afterRestart = restartRace.Orders.Orders.Single();
            var position = restartRace.Adapter.Positions.Single(candidate => candidate.PositionId == initial.PositionId);
            var protection = restartRace.Adapter.Protections.Single(candidate => candidate.PositionId == initial.PositionId);
            check(!uncertain.Succeeded && !reconciled.Succeeded
                  && reconciled.Status.Contains("выбор владельца", StringComparison.OrdinalIgnoreCase)
                  && afterRestart.PendingOperationId is null
                  && restartRace.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                "restart resolves the unknown cancel/fill evidence without retransmitting cancellation and leaves the owner decision open");
            check(afterRestart.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && afterRestart.EntryTerminalState == ManagedEntryTerminalState.Filled
                  && afterRestart.PositionQuantity == fixture.Intent.Quantity
                  && position.Quantity == fixture.Intent.Quantity,
                "restart reconciliation saves the full filled position while preserving the owner choice");
            check(protection.TakeProfit?.Value == fixture.Intent.TakeProfit + 1m
                  && protection.StopLoss?.Value == fixture.Intent.StopLoss - 1m
                  && protection.TakeProfitCoveredQuantity == fixture.Intent.Quantity
                  && protection.StopLossCoveredQuantity == fixture.Intent.Quantity
                  && restartRace.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel", "protect"]),
                "restart reconciliation repairs full owner-level protection for the raced fill");
        }

        using (var failedRepair = new Harness(Path.Combine(root, "repair-rejected"), InitialTime))
        {
            failedRepair.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            failedRepair.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = failedRepair.Orders.Orders.Single();
            failedRepair.Adapter.SetManualProtection(state, fixture.Intent.TakeProfit + 1m,
                fixture.Intent.StopLoss - 1m);
            failedRepair.Adapter.AddPartialFill(state, fixture.Intent.Quantity / 4m);
            failedRepair.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
            failedRepair.Clock.UtcNow = state.NextManagementCheckAt!.Value;
            failedRepair.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            var after = failedRepair.Orders.Orders.Single();
            check(after.Status == ManagedOrderStatus.Closed && after.PositionQuantity == 0m
                  && failedRepair.Adapter.CloseRequests.Single().Quantity == fixture.Intent.Quantity * 3m / 4m
                  && failedRepair.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "protect", "cancel", "close"]),
                "only a confirmed failed repair invokes A's exact cancel and emergency-close path for the grown quantity");
        }

        using (var absentLevels = new Harness(Path.Combine(root, "ambiguous-owner-levels"), InitialTime))
        {
            absentLevels.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            absentLevels.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = absentLevels.Orders.Orders.Single();
            absentLevels.Adapter.SetIncompleteManualProtection(state, fixture.Intent.StopLoss - 1m);
            absentLevels.Clock.UtcNow = state.NextManagementCheckAt!.Value;
            var result = absentLevels.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            var paused = absentLevels.Orders.Orders.Single();
            check(!result.Succeeded && paused.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice
                  && paused.Status == ManagedOrderStatus.OwnershipAmbiguous
                  && paused.PendingOwnerTakeProfit is null && paused.LastFailure is not null
                  && absentLevels.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
                "missing owner TP levels require reconciliation and cannot trigger a protection or cancellation action");
        }
    }

    private static void CheckTransferAndPendingOutcome(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using (var transferred = new Harness(Path.Combine(root, "transfer"), InitialTime))
        {
            transferred.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
            transferred.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = transferred.Orders.Orders.Single();
            transferred.Adapter.SetManualProtection(state, fixture.Intent.TakeProfit + 1m,
                fixture.Intent.StopLoss - 1m);
            transferred.Clock.UtcNow = state.NextManagementCheckAt!.Value;
            transferred.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            state = transferred.Orders.Orders.Single();
            var revision = state.Revision;
            var choice = transferred.Orders.TransferToManualAsync(state.IntentId).GetAwaiter().GetResult();
            var manual = transferred.Orders.Orders.Single();
            var calls = transferred.Adapter.ActionCalls.ToArray();
            transferred.Orders.CancelRemainderAsync(state.IntentId, savedOperatingIntent: false).GetAwaiter().GetResult();
            transferred.Orders.RepairProtectionAsync(state.IntentId, savedOperatingIntent: false).GetAwaiter().GetResult();
            transferred.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
            transferred.Orders.StopAsync().GetAwaiter().GetResult();
            check(choice.Succeeded && manual.ManagementMode == ManagedOrderManagementMode.Manual
                  && manual.Revision == revision + 1
                  && manual.TransferredPositionQuantity == fixture.Intent.Quantity / 2m
                  && manual.TransferredRemainderQuantity == fixture.Intent.Quantity / 2m
                  && manual.RemainingQuantity == fixture.Intent.Quantity / 2m
                  && transferred.Orders.Orders.Single().ManagementMode == ManagedOrderManagementMode.Manual
                  && transferred.Adapter.ActionCalls.SequenceEqual(calls),
                "Transfer saves position and exact remainder once and blocks all later application mutations");
        }

        using var pendingTransfer = new Harness(Path.Combine(root, "pending-transfer"), InitialTime);
        pendingTransfer.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
        pendingTransfer.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var pendingState = pendingTransfer.Orders.Orders.Single();
        pendingTransfer.Adapter.CancelOutcome = RecoveryOperationOutcome.Unknown;
        var unknown = pendingTransfer.Orders.CancelRemainderAsync(pendingState.IntentId,
            savedOperatingIntent: false).GetAwaiter().GetResult();
        pendingState = pendingTransfer.Orders.Orders.Single();
        var operation = pendingTransfer.Recovery.State.Operations.Single(candidate =>
            candidate.ActionKind == RecoveryActionKind.CancelOwnedOrder);
        var transfer = pendingTransfer.Orders.TransferToManualAsync(pendingState.IntentId).GetAwaiter().GetResult();
        var afterTransfer = pendingTransfer.Orders.Orders.Single();
        pendingTransfer.Adapter.ResolveUnknownCancel(afterTransfer, operation.OperationId);
        pendingTransfer.Clock.UtcNow = InitialTime.AddSeconds(pendingTransfer.Settings.ReadRetryDelaySeconds);
        pendingTransfer.Restart();
        var reconciled = pendingTransfer.Orders.ReconcilePendingAsync(afterTransfer.IntentId).GetAwaiter().GetResult();
        var afterReconcile = pendingTransfer.Orders.Orders.Single();
        check(!unknown.Succeeded && transfer.Succeeded && reconciled.Succeeded
              && afterReconcile.ManagementMode == ManagedOrderManagementMode.Manual
              && afterReconcile.EntryTerminalState == ManagedEntryTerminalState.Cancelled
              && afterReconcile.TransferredPositionQuantity == fixture.Intent.Quantity / 2m
              && afterReconcile.TransferredRemainderQuantity == 0m
              && pendingTransfer.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel"]),
            "an already sent cancel is reconciled after transfer, updates the risk record, and sends no dependent action");
    }

    private static void CheckAmbiguousRemainderBlocksDueMutation(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Limit", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.EntryFilledQuantity = fixture.Intent.Quantity / 2m;
        harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        harness.Adapter.MakeRemainderOwnershipUnknown();
        harness.Clock.UtcNow = state.PartialFillCancelAt!.Value;
        var result = harness.Orders.RunDueChecksAsync(state.IntentId).GetAwaiter().GetResult();
        var paused = harness.Orders.Orders.Single();
        check(!result.Succeeded && paused.Status == ManagedOrderStatus.OwnershipAmbiguous
              && paused.LastFailure is not null && harness.Adapter.ActionCalls.SequenceEqual(["entry", "protect"]),
            "an ambiguous remainder owner pauses on fresh evidence and never sends cancellation or protection");
    }

    private static void CheckEmergencyCloseNoticeAfterRestart(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using var harness = new Harness(root, InitialTime);
        harness.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
        harness.Adapter.CloseOutcome = RecoveryOperationOutcome.NotExecuted;
        harness.Adapter.CloseRetryability = RecoveryRetryability.Retryable;
        harness.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var state = harness.Orders.Orders.Single();
        for (var retry = 0; retry < 2; retry++)
        {
            harness.Clock.UtcNow = state.NextEmergencyCloseAt!.Value;
            harness.Orders.RunDueEmergencyCloseAsync(state.IntentId, savedOperatingIntent: false)
                .GetAwaiter().GetResult();
            state = harness.Orders.Orders.Single();
        }
        check(state.EmergencyCloseRetriesUsed == 3 && harness.Adapter.CloseRequests.Count == 3,
            "the close fixture reaches the persisted retry limit before testing restart notification delivery");

        harness.Adapter.CloseOutcome = RecoveryOperationOutcome.Unknown;
        harness.Clock.UtcNow = state.NextEmergencyCloseAt!.Value;
        var unknown = harness.Orders.RunDueEmergencyCloseAsync(state.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        state = harness.Orders.Orders.Single();
        var operation = harness.Recovery.State.Operations.Last(candidate =>
            candidate.ActionKind == RecoveryActionKind.EmergencyReduceOnlyClose);
        harness.Adapter.ResolveUnknownCloseAsRetryableRejection(state, operation.OperationId);
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(harness.Settings.ReadRetryDelaySeconds);
        harness.Restart();
        var reconciled = harness.Orders.ReconcilePendingAsync(state.IntentId).GetAwaiter().GetResult();
        state = harness.Orders.Orders.Single();
        check(!unknown.Succeeded && !reconciled.Succeeded
              && state.Status == ManagedOrderStatus.EmergencyCloseWaiting
              && state.EmergencyCloseRetriesUsed == 3 && state.NextEmergencyCloseAt == harness.Clock.UtcNow.AddSeconds(30)
              && state.EmergencyCloseNoticeSent && !state.EmergencyCloseNoticePending
              && harness.Adapter.ActionCalls.Count(action => action == "close") == 4
              && harness.Windows.Sent.Count(message => message.Id == state.IntentId + ":close-error") == 1,
            "restart-confirmed retry exhaustion persists the thirty-second schedule and dispatches one stable close error");
    }

    private static void CheckCancelFillAndTakeProfitRace(Action<bool, string> check, string root)
    {
        var limit = CreateIntent("Limit", InitialTime);
        using (var tpHarness = new Harness(Path.Combine(root, "tp-before-cancel"), InitialTime))
        {
            tpHarness.Adapter.EntryFilledQuantity = limit.Intent.Quantity / 2m;
            tpHarness.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var before = tpHarness.Orders.Orders.Single();
            tpHarness.Adapter.CloseByTakeProfit(before);
            var cancelled = tpHarness.Orders.CancelRemainderAsync(before.IntentId, savedOperatingIntent: false)
                .GetAwaiter().GetResult();
            var after = tpHarness.Orders.Orders.Single();
            check(cancelled.Succeeded && after.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && after.FilledQuantity == limit.Intent.Quantity / 2m && after.RemainingQuantity == 0m
                  && after.PositionQuantity == 0m && after.TakeProfitClosedQuantity == limit.Intent.Quantity / 2m
                  && after.PositionId == before.PositionId && after.TakeProfitOrderId == before.TakeProfitOrderId
                  && tpHarness.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel"]),
                "a real remainder cancel reconciles cumulative fills separately from a TP-closed position and keeps linked IDs");
            check(tpHarness.Windows.Sent.Any(message => message.Id == after.IntentId + ":cancelled")
                  && !tpHarness.Windows.Sent.Any(message => message.Id == after.IntentId + ":filled"),
                "TP closure does not turn a genuine remainder cancellation into a full-fill event");
        }

        using (var restartTp = new Harness(Path.Combine(root, "tp-and-cancel-after-restart"), InitialTime))
        {
            restartTp.Adapter.EntryFilledQuantity = limit.Intent.Quantity / 2m;
            restartTp.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var state = restartTp.Orders.Orders.Single();
            restartTp.Adapter.CancelOutcome = RecoveryOperationOutcome.Unknown;
            var unknown = restartTp.Orders.CancelRemainderAsync(state.IntentId, savedOperatingIntent: false)
                .GetAwaiter().GetResult();
            state = restartTp.Orders.Orders.Single();
            var cancelOperation = restartTp.Recovery.State.Operations.Single(candidate =>
                candidate.ActionKind == RecoveryActionKind.CancelOwnedOrder);
            restartTp.Adapter.CloseByTakeProfit(state);
            restartTp.Adapter.ResolveUnknownCancel(state, cancelOperation.OperationId);
            restartTp.Clock.UtcNow = InitialTime.AddSeconds(restartTp.Settings.ReadRetryDelaySeconds);
            restartTp.Restart();
            var tpReconciled = restartTp.Orders.ReconcilePendingAsync(state.IntentId).GetAwaiter().GetResult();
            var closed = restartTp.Orders.Orders.Single();
            check(!unknown.Succeeded && tpReconciled.Succeeded
                  && closed.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && closed.Status == ManagedOrderStatus.Closed
                  && closed.FilledQuantity == limit.Intent.Quantity / 2m && closed.RemainingQuantity == 0m
                  && closed.PositionQuantity == 0m
                  && closed.TakeProfitClosedQuantity == limit.Intent.Quantity / 2m
                  && restartTp.Adapter.ActionCalls.Count(action => action == "cancel") == 1,
                "restart uses exact TP history to reconcile a real cancellation after the position already closed");
        }

        using var fillHarness = new Harness(Path.Combine(root, "full-fill-during-cancel"), InitialTime);
        fillHarness.Adapter.EntryFilledQuantity = limit.Intent.Quantity / 2m;
        fillHarness.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var partial = fillHarness.Orders.Orders.Single();
        fillHarness.Adapter.CancelOutcome = RecoveryOperationOutcome.NotExecuted;
        fillHarness.Adapter.CancelRacesToFullFill = true;
        var filled = fillHarness.Orders.CancelRemainderAsync(partial.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        var full = fillHarness.Orders.Orders.Single();
        check(filled.Succeeded && full.EntryTerminalState == ManagedEntryTerminalState.Filled
              && full.FilledQuantity == limit.Intent.Quantity && full.RemainingQuantity == 0m
              && full.PositionQuantity == limit.Intent.Quantity
              && fillHarness.Adapter.ProtectRequests.Last().Quantity == limit.Intent.Quantity
              && fillHarness.Adapter.ActionCalls.SequenceEqual(["entry", "protect", "cancel", "protect"]),
            "a NotExecuted cancel response reconciles a raced full fill and protects the exact final size");
        check(fillHarness.Windows.Sent.Any(message => message.Id == full.IntentId + ":filled")
              && !fillHarness.Windows.Sent.Any(message => message.Id == full.IntentId + ":cancelled"),
            "a full fill during cancellation gets a stable fill event rather than a false cancellation event");

        using var restartHarness = new Harness(Path.Combine(root, "full-fill-after-unknown"), InitialTime);
        restartHarness.Adapter.EntryFilledQuantity = limit.Intent.Quantity / 2m;
        restartHarness.Orders.OpenAsync(limit.Intent, limit.Input, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        var pending = restartHarness.Orders.Orders.Single();
        restartHarness.Adapter.CancelOutcome = RecoveryOperationOutcome.Unknown;
        var uncertain = restartHarness.Orders.CancelRemainderAsync(pending.IntentId, savedOperatingIntent: false)
            .GetAwaiter().GetResult();
        pending = restartHarness.Orders.Orders.Single();
        var operation = restartHarness.Recovery.State.Operations.Single(candidate =>
            candidate.ActionKind == RecoveryActionKind.CancelOwnedOrder);
        restartHarness.Adapter.CancelRacesToFullFill = true;
        restartHarness.Adapter.ResolveUnknownCancelToFullFill(pending, operation.OperationId);
        restartHarness.Clock.UtcNow = InitialTime.AddSeconds(restartHarness.Settings.ReadRetryDelaySeconds);
        restartHarness.Restart();
        var reconciled = restartHarness.Orders.ReconcilePendingAsync(pending.IntentId).GetAwaiter().GetResult();
        var restarted = restartHarness.Orders.Orders.Single();
        check(!uncertain.Succeeded && reconciled.Succeeded
              && restarted.EntryTerminalState == ManagedEntryTerminalState.Filled
              && restarted.FilledQuantity == limit.Intent.Quantity && restarted.RemainingQuantity == 0m
              && restarted.PositionQuantity == limit.Intent.Quantity
              && restartHarness.Adapter.ActionCalls.Count(action => action == "cancel") == 1
              && restartHarness.Adapter.ProtectRequests.Last().Quantity == limit.Intent.Quantity,
            "restart reconciliation applies the same exact full-fill evidence without retransmitting cancellation");
    }

    private static void CheckEmergencyRetryabilityAndSerialization(Action<bool, string> check, string root)
    {
        var fixture = CreateIntent("Market", InitialTime);
        using (var permanent = new Harness(Path.Combine(root, "permanent"), InitialTime))
        {
            permanent.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
            permanent.Adapter.CloseOutcome = RecoveryOperationOutcome.NotExecuted;
            permanent.Adapter.CloseRetryability = RecoveryRetryability.Permanent;
            permanent.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var paused = permanent.Orders.Orders.Single();
            var again = permanent.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            check(paused.Status == ManagedOrderStatus.EmergencyClosePaused
                  && paused.NextEmergencyCloseAt is null && paused.EmergencyCloseRetriesUsed == 0
                  && !again.Succeeded && permanent.Adapter.ActionCalls.Count(action => action == "entry") == 1
                  && !permanent.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
                "a permanent close rejection pauses safely, stays outside the retry timer and keeps the entry gate closed");

            var callsWhilePaused = permanent.Adapter.ActionCalls.ToArray();
            permanent.Restart();
            permanent.Clock.UtcNow = paused.NextManagementCheckAt!.Value;
            permanent.Adapter.StaleSnapshot = true;
            permanent.Orders.RunDueChecksAsync(paused.IntentId).GetAwaiter().GetResult();
            var afterStalePoll = permanent.Orders.Orders.Single();
            permanent.Adapter.StaleSnapshot = false;
            var stop = permanent.Orders.StopAsync().GetAwaiter().GetResult();
            var afterStop = permanent.Orders.Orders.Single();
            check(afterStalePoll.Status == ManagedOrderStatus.EmergencyClosePaused
                  && afterStalePoll.NextEmergencyCloseAt is null
                  && afterStalePoll.EmergencyCloseRetryability == RecoveryRetryability.Permanent
                  && afterStop.Status == ManagedOrderStatus.EmergencyClosePaused && !stop.Succeeded
                  && permanent.Adapter.ActionCalls.SequenceEqual(callsWhilePaused),
                "restart, a temporary read pause, and Stop preserve permanent emergency pause without retrying close or protection");

            var tpId = "verified-paused-tp";
            var slId = "verified-paused-sl";
            var withLevels = permanent.Recovery.SaveManagedOrderAsync(afterStop with
            {
                TakeProfitOrderId = tpId,
                StopLossOrderId = slId
            }).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Paused TP/SL fixture did not persist.");
            permanent.Adapter.SetSoftwareProtection(withLevels, tpId, slId);
            permanent.Adapter.CloseByTakeProfit(withLevels);
            permanent.Clock.UtcNow = withLevels.NextManagementCheckAt!.Value;
            var flat = permanent.Orders.RunDueChecksAsync(withLevels.IntentId).GetAwaiter().GetResult();
            var closed = permanent.Orders.Orders.Single();
            check(flat.Succeeded && closed.Status == ManagedOrderStatus.Closed
                  && closed.TakeProfitClosedQuantity == fixture.Intent.Quantity
                  && closed.EmergencyCloseRetriesUsed == 0
                  && permanent.Adapter.ActionCalls.SequenceEqual(callsWhilePaused),
                "a verified TP closure is processed from emergency pause without issuing a repair or reduce-only retry");
        }

        using (var authorization = new Harness(Path.Combine(root, "authorization"), InitialTime))
        {
            authorization.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
            authorization.Adapter.CloseOutcome = RecoveryOperationOutcome.NotExecuted;
            authorization.Adapter.CloseRetryability = RecoveryRetryability.AuthorizationRequired;
            authorization.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var paused = authorization.Orders.Orders.Single();
            check(paused.Status == ManagedOrderStatus.EmergencyClosePaused
                  && paused.NextEmergencyCloseAt is null && paused.EmergencyCloseRetriesUsed == 0,
                "authorization failures require manual recovery and never enter the automated close retry loop");
            var calls = authorization.Adapter.ActionCalls.ToArray();
            authorization.Clock.UtcNow = paused.NextManagementCheckAt!.Value;
            authorization.Orders.RunDueChecksAsync(paused.IntentId).GetAwaiter().GetResult();
            authorization.Restart();
            authorization.Orders.StopAsync().GetAwaiter().GetResult();
            var afterStop = authorization.Orders.Orders.Single();
            check(afterStop.Status == ManagedOrderStatus.EmergencyClosePaused
                  && afterStop.EmergencyCloseRetryability == RecoveryRetryability.AuthorizationRequired
                  && afterStop.NextEmergencyCloseAt is null
                  && authorization.Adapter.ActionCalls.SequenceEqual(calls),
                "normal polling, restart, and Stop preserve the authorization pause without another close or protection attempt");
        }

        using (var unknownReason = new Harness(Path.Combine(root, "unknown-reason"), InitialTime))
        {
            unknownReason.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
            unknownReason.Adapter.CloseOutcome = RecoveryOperationOutcome.NotExecuted;
            unknownReason.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var paused = unknownReason.Orders.Orders.Single();
            check(paused.Status == ManagedOrderStatus.EmergencyClosePaused
                  && paused.EmergencyCloseRetryability == RecoveryRetryability.Unknown
                  && paused.NextEmergencyCloseAt is null && paused.EmergencyCloseRetriesUsed == 0,
                "a close rejection without an explicit retryable cause uses the safe no-retry default");
        }

        using (var waiting = new Harness(Path.Combine(root, "waiting-poll"), InitialTime, settings =>
        {
            settings.PositionCheckMinutes = 1;
            settings.EmergencyCloseRetryDelaySeconds = 300;
        }))
        {
            waiting.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
            waiting.Adapter.CloseOutcome = RecoveryOperationOutcome.NotExecuted;
            waiting.Adapter.CloseRetryability = RecoveryRetryability.Retryable;
            waiting.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var scheduled = waiting.Orders.Orders.Single();
            var retryAt = scheduled.NextEmergencyCloseAt;
            var calls = waiting.Adapter.ActionCalls.ToArray();
            waiting.Clock.UtcNow = scheduled.NextManagementCheckAt!.Value;
            waiting.Orders.RunDueChecksAsync(scheduled.IntentId).GetAwaiter().GetResult();
            var polled = waiting.Orders.Orders.Single();
            waiting.Adapter.StaleSnapshot = true;
            waiting.Clock.UtcNow = polled.NextManagementCheckAt!.Value;
            waiting.Orders.RunDueChecksAsync(polled.IntentId).GetAwaiter().GetResult();
            var stale = waiting.Orders.Orders.Single();
            waiting.Adapter.StaleSnapshot = false;
            waiting.Restart();
            waiting.Clock.UtcNow = retryAt!.Value.AddMinutes(-1);
            waiting.Orders.RunDueChecksAsync(stale.IntentId).GetAwaiter().GetResult();
            var afterRestart = waiting.Orders.Orders.Single();
            check(polled.Status == ManagedOrderStatus.EmergencyCloseWaiting,
                "normal polling preserves the retryable emergency status");
            check(polled.NextEmergencyCloseAt == retryAt,
                "normal polling preserves the retryable emergency deadline");
            check(waiting.Adapter.ActionCalls.SequenceEqual(calls),
                "normal polling does not send another protection or close action");
            check(stale.Status == ManagedOrderStatus.EmergencyCloseWaiting
                  && stale.NextEmergencyCloseAt == retryAt
                  && waiting.Adapter.ActionCalls.SequenceEqual(calls),
                "a temporary read failure preserves the retryable emergency schedule without protection retries");
            check(afterRestart.Status == ManagedOrderStatus.EmergencyCloseWaiting
                  && afterRestart.NextEmergencyCloseAt == retryAt
                  && waiting.Adapter.ActionCalls.SequenceEqual(calls),
                "restart preserves the retryable emergency schedule without protection retries");
            waiting.Clock.UtcNow = retryAt!.Value;
            waiting.Adapter.CloseOutcome = RecoveryOperationOutcome.Confirmed;
            var due = waiting.Orders.RunDueChecksAsync(stale.IntentId).GetAwaiter().GetResult();
            var closed = waiting.Orders.Orders.Single();
            check(due.Succeeded && closed.Status == ManagedOrderStatus.Closed
                  && waiting.Adapter.ActionCalls.Count(action => action == "close") == 2
                  && waiting.Adapter.ActionCalls.Count(action => action == "protect") == 1
                  && waiting.Adapter.CloseRequests.Last().Quantity == fixture.Intent.Quantity,
                "the persisted retry deadline routes through the existing exact emergency-close gate");
        }

        using (var restart = new Harness(Path.Combine(root, "unknown-then-retryable"), InitialTime))
        {
            restart.Adapter.ProtectionOutcome = RecoveryOperationOutcome.NotExecuted;
            restart.Adapter.CloseOutcome = RecoveryOperationOutcome.Unknown;
            restart.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            var pending = restart.Orders.Orders.Single();
            var closeOperation = restart.Recovery.State.Operations.Single(operation =>
                operation.ActionKind == RecoveryActionKind.EmergencyReduceOnlyClose);
            restart.Adapter.ResolveUnknownCloseAsRetryableRejection(pending, closeOperation.OperationId);
            restart.Clock.UtcNow = InitialTime.AddSeconds(restart.Settings.ReadRetryDelaySeconds);
            restart.Restart();
            var reconciled = restart.Orders.ReconcilePendingAsync(pending.IntentId).GetAwaiter().GetResult();
            var waiting = restart.Orders.Orders.Single();
            restart.Orders.ReconcilePendingAsync(pending.IntentId).GetAwaiter().GetResult();
            check(!reconciled.Succeeded && waiting.Status == ManagedOrderStatus.EmergencyCloseWaiting
                  && waiting.EmergencyCloseRetriesUsed == 1
                  && waiting.NextEmergencyCloseAt == restart.Clock.UtcNow.AddSeconds(10)
                  && waiting.EmergencyCloseRetryability == RecoveryRetryability.Retryable
                  && restart.Adapter.ActionCalls.Count(action => action == "close") == 1,
                "unknown close then read-confirmed temporary rejection across restart counts exactly once and only schedules a retry");
        }

        using var overlapping = new Harness(Path.Combine(root, "overlap"), InitialTime);
        var opens = Task.WhenAll(
            overlapping.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true),
            overlapping.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true));
        opens.GetAwaiter().GetResult();
        check(overlapping.Adapter.ActionCalls.Count(action => action == "entry") == 1
              && overlapping.Orders.Orders.Count == 1,
            "overlapping identical Open requests are serialized and cannot create a duplicate managed order");
        var saved = overlapping.Orders.Orders.Single();
        var ownerUpdate = overlapping.Recovery.SaveManagedOrderAsync(saved with { LastFailure = "owner review" })
            .GetAwaiter().GetResult();
        var staleWrite = overlapping.Recovery.SaveManagedOrderAsync(saved with { LastFailure = "stale writer" })
            .GetAwaiter().GetResult();
        check(ownerUpdate is not null && staleWrite is null
              && overlapping.Orders.Orders.Single().LastFailure == "owner review",
            "managed journal revisions reject a stale owner or lifecycle update instead of erasing newer state");
    }

    private static (ManagedOrderIntent Intent, EntryEligibilityInput Input) CreateIntent(
        string entryType,
        DateTimeOffset now)
    {
        var sample = EntryEligibilityExample.Create();
        var setup = sample.Setup with
        {
            EntryType = entryType,
            LimitPrice = entryType == "Limit" ? 50m : null,
            ObservedAt = now.AddSeconds(-30)
        };
        var settings = sample.Settings.Settings.Copy();
        settings.EntryType = entryType;
        var profile = new SettingsProfile
        {
            Settings = settings,
            SelectedMode = sample.Settings.SelectedMode,
            SavedOperatingIntent = true
        };
        var pools = sample.Pools.Select(pool =>
        {
            if (pool.Account.Venue != Venue)
                return pool;
            var sourceAccount = pool.Account;
            var manualPosition = sourceAccount.Positions.Value!.Single() with
            {
                PositionId = "manual-btc-position",
                OrderIds = ["manual-btc-order"]
            };
            var manualProtection = sourceAccount.Protections.Value!.Single() with
            {
                PositionId = manualPosition.PositionId,
                Ownership = PositionOwnership.Manual,
                TakeProfitCoveredQuantity = manualPosition.Quantity,
                StopLossCoveredQuantity = manualPosition.Quantity,
                TakeProfitOrderId = "manual-btc-tp",
                StopLossOrderId = "manual-btc-sl"
            };
            return pool with
            {
                Account = sourceAccount with
                {
                    Positions = AccountSection<IReadOnlyList<OpenPosition>>.Available([manualPosition],
                        sourceAccount.ObservedAt, sourceAccount.Source),
                    Protections = AccountSection<IReadOnlyList<PositionProtection>>.Available([manualProtection],
                        sourceAccount.ObservedAt, sourceAccount.Source)
                },
                Positions = pool.Positions.Select(position => position with { Position = manualPosition }).ToArray(),
                CurrentMarketPrice = EntryEligibilityExample.Evidence(50m, "USDC/SOL", now.AddSeconds(-2), "SOL-PERP"),
                VerifiedLimitPrice = entryType == "Limit"
                    ? EntryEligibilityExample.Evidence(50m, "USDC/SOL", now.AddSeconds(-2), "SOL-PERP")
                    : null
            };
        }).ToArray();
        var input = sample with
        {
            Settings = profile,
            Setup = setup,
            EvaluatedAt = now,
            Pools = pools
        };
        var eligibility = EntryEligibilityEvaluator.Evaluate(input);
        if (eligibility.Status != EntryEligibilityStatus.ScenarioReady || eligibility.SelectedPool is null)
            throw new InvalidOperationException("The controlled 007 setup did not produce an eligible order fixture.");
        var intent = new ManagedOrderIntent(Venue, Account, setup.Asset!, eligibility.SelectedPool.Contract!,
            setup.Direction!.Value, entryType, eligibility.SelectedPool.Quantity!.Value, setup.LimitPrice,
            setup.TakeProfit!.Value, setup.StopLoss!.Value);
        return (intent, input);
    }

    private static AccountSnapshot Snapshot(
        DateTimeOffset now,
        IReadOnlyList<OpenPosition> positions,
        IReadOnlyList<OpenOrder> orders,
        IReadOnlyList<PositionProtection> protections,
        IReadOnlyList<ClosedTrade>? closedTrades = null) =>
        new(Venue, Account, "USDC cross B", now, "controlled order fixture", "USDC", "contract",
            AccountReadState.Available, null, now,
            AccountSection<AccountBalance>.Available(new AccountBalance(new MoneyAmount(1000m, "USDC"),
                new MoneyAmount(1000m, "USDC"), new MoneyAmount(150m, "USDC")), now, "controlled order fixture"),
            AccountSection<IReadOnlyList<OpenPosition>>.Available(positions, now, "controlled order fixture"),
            AccountSection<IReadOnlyList<OpenOrder>>.Available(orders, now, "controlled order fixture"),
            AccountSection<IReadOnlyList<PositionProtection>>.Available(protections, now, "controlled order fixture"),
            closedTrades is { Count: > 0 }
                ? AccountSection<IReadOnlyList<ClosedTrade>>.Available(closedTrades, now, "controlled order fixture")
                : AccountSection<IReadOnlyList<ClosedTrade>>.ConfirmedEmpty(Array.Empty<ClosedTrade>(), now,
                    "controlled order fixture"));

    private static void Focus(FrameworkElement element)
    {
        element.BringIntoView();
        PumpDispatcher();
        if (!ReferenceEquals(Keyboard.Focus(element), element))
            throw new InvalidOperationException($"Could not focus WPF control: {element.Name}");
        PumpDispatcher();
    }

    private static void ScrollLifecyclePanelIntoView(Window window, AccountDashboardView dashboard)
    {
        dashboard.OrderLifecyclePanelElement.BringIntoView();
        window.UpdateLayout();
        if (window.FindName("MainScrollViewer") is ScrollViewer scrollViewer)
            scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight,
                scrollViewer.VerticalOffset + 80));
        window.UpdateLayout();
        PumpDispatcher();
    }

    private static void InvokeButton(Button button)
    {
        if (!button.IsVisible || !button.IsEnabled)
            throw new InvalidOperationException($"Cannot invoke hidden or disabled WPF button: {button.Name}");
        var peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new InvalidOperationException($"WPF automation peer not found: {button.Name}");
        var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException($"WPF InvokePattern unavailable: {button.Name}");
        invoke.Invoke();
    }

    private static void RenderWindow(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"WPF screenshot is empty: {path}");
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
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "009-orders-and-protection.md")))
                return Path.Combine(directory.FullName, "artifacts", "009-orders-and-protection");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts", "009-orders-and-protection");
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _journalPath;
        private readonly RecoveryJournalStore _store;

        internal Harness(string root, DateTimeOffset now, Action<AppSettings>? configureSettings = null)
        {
            Directory.CreateDirectory(root);
            _journalPath = Path.Combine(root, "recovery-state.json");
            _store = new RecoveryJournalStore(_journalPath);
            Clock = new FakeClock(now);
            Settings = new AppSettings();
            configureSettings?.Invoke(Settings);
            Adapter = new ControlledAdapter(Clock);
            StartServices();
        }

        internal FakeClock Clock { get; }
        internal AppSettings Settings { get; }
        internal ControlledAdapter Adapter { get; }
        internal RecoveryCoordinator Recovery { get; private set; } = null!;
        internal NotificationDispatcher Dispatcher { get; private set; } = null!;
        internal RecordingChannel Windows { get; private set; } = null!;
        internal RecordingChannel Telegram { get; private set; } = null!;
        internal OrderLifecycleCoordinator Orders { get; private set; } = null!;

        internal void Restart()
        {
            Dispatcher.Dispose();
            StartServices();
        }

        private void StartServices()
        {
            Recovery = new RecoveryCoordinator(_store, Settings, Clock, Adapter);
            Adapter.Bind(Recovery, _store);
            Windows = new RecordingChannel(NotificationChannel.Windows);
            Telegram = new RecordingChannel(NotificationChannel.Telegram);
            Dispatcher = new NotificationDispatcher(Recovery, [Windows, Telegram], Settings);
            Orders = new OrderLifecycleCoordinator(Recovery, Adapter, Settings, Clock, Dispatcher);
        }

        public void Dispose() => Dispatcher.Dispose();
    }

    private sealed class FakeClock(DateTimeOffset now) : IRecoveryClock
    {
        private DateTimeOffset _now = now;
        internal TimeSpan AdvancePerRead { get; set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                var current = _now;
                _now += AdvancePerRead;
                return current;
            }
            set => _now = value;
        }
    }

    private sealed class ControlledAdapter(FakeClock clock) : IManagedOrderTestAdapter
    {
        private static readonly OpenPosition ManualBtcPosition = new("BTC", "BTC-PERP", PositionDirection.Long,
            1m, "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.Manual,
            "controlled manual baseline", "manual-btc-position", ["manual-btc-order"]);
        private static readonly PositionProtection ManualBtcProtection = new("BTC-PERP",
            new MoneyAmount(120m, "USDC"), new MoneyAmount(90m, "USDC"), "manual-btc-position",
            PositionOwnership.Manual, 1m, 1m, "manual-btc-tp", "manual-btc-sl");
        private readonly List<OpenPosition> _positions = [];
        private readonly List<OpenOrder> _orders = [];
        private readonly List<PositionProtection> _protections = [];
        private readonly List<ClosedTrade> _closedTrades = [];
        private readonly Dictionary<string, RecoveryOperationObservation> _observations = new(StringComparer.Ordinal);
        private RecoveryCoordinator? _recovery;
        private RecoveryJournalStore? _store;

        public TimeSpan MaximumSnapshotAge => TimeSpan.FromMinutes(5);
        public List<string> Actions { get; } = [];
        public List<string> ActionCalls { get; } = [];
        public List<(decimal Quantity, PositionDirection Direction)> CloseRequests { get; } = [];
        public List<(decimal Quantity, string PositionId)> ProtectRequests { get; } = [];
        public IReadOnlyList<OpenPosition> Positions => _positions.ToArray();
        public IReadOnlyList<OpenOrder> Orders => _orders.ToArray();
        public IReadOnlyList<PositionProtection> Protections => _protections.ToArray();
        public bool DurableTransferSeenBeforeEveryCallback { get; private set; } = true;
        public bool StaleSnapshot { get; set; }
        public bool ConflictAccountEvidence { get; set; }
        public bool CancelRacesToFullFill { get; set; }
        public decimal? EntryFilledQuantity { get; set; }
        public decimal? ProtectionCoverageOverride { get; set; }
        public RecoveryOperationOutcome EntryOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        public RecoveryOperationOutcome ProtectionOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        public RecoveryOperationOutcome CancelOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        public RecoveryOperationOutcome CloseOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        public RecoveryRetryability CloseRetryability { get; set; } = RecoveryRetryability.Unknown;
        public bool OmitFirstFillTime { get; set; }
        public DateTimeOffset? EntryHistoryFirstFillAt { get; set; }
        public bool EntryFillHistoryComplete { get; set; }
        private string? _entryOrderId;

        internal void Bind(RecoveryCoordinator recovery, RecoveryJournalStore store)
        {
            _recovery = recovery;
            _store = store;
        }

        public Task<RecoverySnapshotRead> ReadAsync(
            string venue,
            string account,
            CancellationToken cancellationToken)
        {
            Actions.Add("read");
            var observedAt = StaleSnapshot ? clock.UtcNow.AddHours(-1) : clock.UtcNow;
            var snapshot = Snapshot(observedAt, [ManualBtcPosition, .. _positions], _orders.ToArray(),
                [ManualBtcProtection, .. _protections], _closedTrades.ToArray());
            if (ConflictAccountEvidence)
                snapshot = snapshot with
                {
                    Balances = AccountSection<AccountBalance>.Available(new AccountBalance(
                        new MoneyAmount(1000m, "USDC"), new MoneyAmount(1000m, "USDC"),
                        new MoneyAmount(151m, "USDC")), observedAt, "controlled conflicting fixture")
                };
            var fillTimes = EntryHistoryFirstFillAt is { } firstFill && _entryOrderId is { } entryOrderId
                ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { [entryOrderId] = firstFill }
                : null;
            return Task.FromResult(new RecoverySnapshotRead(true,
                snapshot,
                OpeningOrderRemaindersComplete: true,
                ProtectionCoverageComplete: true,
                UnfinishedOperationsComplete: true,
                OperationOutcomes: new Dictionary<string, RecoveryOperationObservation>(_observations,
                    StringComparer.Ordinal),
                ClosedPositionHistoryComplete: true,
                EntryFirstFillTimes: fillTimes,
                EntryFillHistoryComplete: EntryFillHistoryComplete));
        }

        public Task<RecoveryOperationOutcome> SubmitEntryAsync(
            ManagedOrderState intent,
            string operationId,
            CancellationToken cancellationToken)
        {
            BeforeCallback("entry", operationId);
            var outcome = EntryOutcome;
            if (outcome != RecoveryOperationOutcome.Unknown)
                ObserveEntry(intent, operationId, outcome,
                    outcome == RecoveryOperationOutcome.Confirmed
                        ? EntryFilledQuantity ?? (intent.EntryType == "Market" ? intent.Quantity : 0m)
                        : 0m);
            return Task.FromResult(outcome);
        }

        public Task<RecoveryOperationOutcome> CancelRemainderAsync(
            ManagedOrderState intent,
            string orderId,
            string operationId,
            CancellationToken cancellationToken)
        {
            BeforeCallback("cancel", operationId);
            var order = _orders.SingleOrDefault(candidate => candidate.OrderId == orderId);
            if (order is null)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            var outcome = CancelOutcome;
            if (outcome != RecoveryOperationOutcome.Unknown)
            {
                if (CancelRacesToFullFill)
                {
                    RecordFullFillDuringCancel(intent, order, operationId, outcome);
                    return Task.FromResult(outcome);
                }
                var position = _positions.SingleOrDefault(candidate => candidate.Contract == intent.Contract);
                if (outcome == RecoveryOperationOutcome.Confirmed)
                    _orders.Remove(order);
                _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                    intent.Contract, "CancelOpeningRemainder", outcome, orderId,
                    position?.PositionId ?? intent.PositionId,
                    intent.Direction, order.FilledQuantity,
                    outcome == RecoveryOperationOutcome.Confirmed ? 0m : order.RemainingQuantity,
                    position?.Quantity ?? 0m, intent.TakeProfitOrderId, intent.StopLossOrderId,
                    EntryOrderState: outcome == RecoveryOperationOutcome.Confirmed
                        ? ManagedEntryTerminalState.Cancelled : ManagedEntryTerminalState.Open);
            }
            return Task.FromResult(outcome);
        }

        public Task<RecoveryOperationOutcome> SetProtectionAsync(
            ManagedOrderState intent,
            string positionId,
            decimal actualQuantity,
            string operationId,
            CancellationToken cancellationToken)
        {
            BeforeCallback("protect", operationId);
            ProtectRequests.Add((actualQuantity, positionId));
            var position = _positions.SingleOrDefault(candidate => candidate.PositionId == positionId);
            if (position is null || position.Quantity != actualQuantity)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            var outcome = ProtectionOutcome;
            if (outcome != RecoveryOperationOutcome.Unknown)
            {
                if (outcome == RecoveryOperationOutcome.Confirmed)
                {
                    var coveredQuantity = ProtectionCoverageOverride ?? actualQuantity;
                    _protections.RemoveAll(candidate => candidate.PositionId == positionId);
                    _protections.Add(new PositionProtection(intent.Contract,
                        new MoneyAmount(intent.TakeProfit, "USDC"), new MoneyAmount(intent.StopLoss, "USDC"),
                        positionId, PositionOwnership.SoftwareOwned, coveredQuantity, coveredQuantity,
                        "tp-" + positionId, "sl-" + positionId));
                }
                _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                    intent.Contract, "SetFullPositionProtection", outcome,
                    PositionId: positionId, Direction: intent.Direction, PositionQuantity: actualQuantity,
                    TakeProfitOrderId: _protections.SingleOrDefault(item => item.PositionId == positionId)
                        ?.TakeProfitOrderId,
                    StopLossOrderId: _protections.SingleOrDefault(item => item.PositionId == positionId)
                        ?.StopLossOrderId);
            }
            return Task.FromResult(outcome);
        }

        public Task<RecoveryOperationOutcome> CloseReduceOnlyAsync(
            ManagedOrderState intent,
            string positionId,
            PositionDirection positionDirection,
            decimal exactQuantity,
            string operationId,
            CancellationToken cancellationToken)
        {
            BeforeCallback("close", operationId);
            CloseRequests.Add((exactQuantity, positionDirection));
            var position = _positions.SingleOrDefault(candidate => candidate.PositionId == positionId);
            if (position is null || position.Quantity != exactQuantity || position.Direction != positionDirection)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            var outcome = CloseOutcome;
            if (outcome != RecoveryOperationOutcome.Unknown)
            {
                if (outcome == RecoveryOperationOutcome.Confirmed)
                {
                    _positions.Remove(position);
                    _protections.RemoveAll(candidate => candidate.PositionId == positionId);
                }
                _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                    intent.Contract, "ReduceOnlyClose", outcome,
                    PositionId: positionId, Direction: positionDirection,
                    PositionQuantity: outcome == RecoveryOperationOutcome.Confirmed ? 0m : exactQuantity,
                    Retryability: outcome == RecoveryOperationOutcome.NotExecuted
                        ? CloseRetryability : RecoveryRetryability.Unknown);
            }
            return Task.FromResult(outcome);
        }

        internal void ClearProtection() => _protections.Clear();

        internal void SetManualProtection(
            ManagedOrderState intent,
            decimal takeProfit,
            decimal stopLoss,
            decimal? takeProfitCoverage = null,
            decimal? stopLossCoverage = null)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            _protections.RemoveAll(candidate => candidate.PositionId == position.PositionId);
            _protections.Add(new PositionProtection(intent.Contract,
                new MoneyAmount(takeProfit, "USDC"), new MoneyAmount(stopLoss, "USDC"),
                position.PositionId, PositionOwnership.Manual,
                takeProfitCoverage ?? position.Quantity, stopLossCoverage ?? position.Quantity,
                "manual-tp-" + position.PositionId, "manual-sl-" + position.PositionId));
        }

        internal void SetSoftwareProtection(
            ManagedOrderState intent,
            string takeProfitOrderId,
            string stopLossOrderId)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            _protections.RemoveAll(candidate => candidate.PositionId == position.PositionId);
            _protections.Add(new PositionProtection(intent.Contract,
                new MoneyAmount(intent.TakeProfit, "USDC"), new MoneyAmount(intent.StopLoss, "USDC"),
                position.PositionId, PositionOwnership.SoftwareOwned, position.Quantity, position.Quantity,
                takeProfitOrderId, stopLossOrderId));
        }

        internal void SetIncompleteManualProtection(ManagedOrderState intent, decimal stopLoss)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            _protections.RemoveAll(candidate => candidate.PositionId == position.PositionId);
            _protections.Add(new PositionProtection(intent.Contract, null, new MoneyAmount(stopLoss, "USDC"),
                position.PositionId, PositionOwnership.Manual, position.Quantity, position.Quantity,
                "manual-tp-" + position.PositionId, "manual-sl-" + position.PositionId));
        }

        internal void AddPartialFill(ManagedOrderState intent, decimal quantity)
        {
            var orderIndex = _orders.FindIndex(candidate => candidate.OrderId == intent.EntryOrderId);
            var positionIndex = _positions.FindIndex(candidate => candidate.PositionId == intent.PositionId);
            var order = _orders[orderIndex];
            var position = _positions[positionIndex];
            if (quantity <= 0m || quantity >= order.RemainingQuantity)
                throw new ArgumentOutOfRangeException(nameof(quantity));
            _orders[orderIndex] = order with
            {
                FilledQuantity = order.FilledQuantity + quantity,
                Status = "Open"
            };
            _positions[positionIndex] = position with { Quantity = position.Quantity + quantity };
        }

        internal void CompleteLimitFillDuringMonitoring(ManagedOrderState intent)
        {
            if (intent.EntryOrderId is not { } orderId
                || intent.OriginEntryOperationId is not { } operationId)
                throw new InvalidOperationException("The controlled Limit fill requires a persisted entry identity.");
            var order = _orders.Single(candidate => candidate.OrderId == orderId);
            var positionIndex = _positions.FindIndex(candidate => candidate.Contract == intent.Contract);
            if (order.FilledQuantity >= intent.Quantity)
                throw new InvalidOperationException("The controlled Limit order cannot transition to full fill.");
            var positionId = intent.PositionId ?? "position-" + intent.IntentId[..8];
            _orders.Remove(order);
            if (positionIndex >= 0)
                _positions[positionIndex] = _positions[positionIndex] with { Quantity = intent.Quantity };
            else
                _positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, intent.Quantity,
                    "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned,
                    "controlled full Limit fill", positionId, [orderId]));
            var prior = _observations[operationId];
            _observations[operationId] = prior with
            {
                Outcome = RecoveryOperationOutcome.Confirmed,
                OrderId = orderId,
                PositionId = positionId,
                Direction = intent.Direction,
                FilledQuantity = intent.Quantity,
                RemainingQuantity = 0m,
                PositionQuantity = intent.Quantity,
                EntryOrderState = ManagedEntryTerminalState.Filled
            };
        }

        internal void CorruptOriginEntryPositionId(ManagedOrderState intent, string positionId)
        {
            if (intent.OriginEntryOperationId is not { } operationId)
                throw new InvalidOperationException("The controlled Limit fill has no persisted entry operation.");
            _observations[operationId] = _observations[operationId] with { PositionId = positionId };
        }

        internal void RemoveEntryOrderWithoutTerminalEvidence()
        {
            if (_orders.Count == 0)
                throw new InvalidOperationException("No controlled open order exists to remove.");
            _orders.RemoveAt(0);
        }

        internal void MakeRemainderOwnershipUnknown() =>
            _orders[0] = _orders[0] with { Ownership = PositionOwnership.Unknown };

        internal void SetPositions(IEnumerable<OpenPosition> positions)
        {
            _positions.Clear();
            _positions.AddRange(positions);
        }

        internal void ResolveUnknownEntry(
            ManagedOrderState intent,
            string operationId,
            decimal filledQuantity)
        {
            ObserveEntry(intent, operationId, RecoveryOperationOutcome.Confirmed, filledQuantity);
        }

        internal void CorruptObservationPositionQuantity(string operationId, decimal quantity) =>
            _observations[operationId] = _observations[operationId] with { PositionQuantity = quantity };

        internal void ResolveUnknownCancel(ManagedOrderState intent, string operationId)
        {
            var order = _orders.Single(candidate => candidate.OrderId == intent.EntryOrderId);
            _orders.Remove(order);
            var position = _positions.SingleOrDefault(candidate => candidate.PositionId == intent.PositionId);
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "CancelOpeningRemainder", RecoveryOperationOutcome.Confirmed,
                order.OrderId, position?.PositionId ?? intent.PositionId, intent.Direction,
                order.FilledQuantity, 0m, position?.Quantity ?? 0m,
                intent.TakeProfitOrderId, intent.StopLossOrderId,
                EntryOrderState: ManagedEntryTerminalState.Cancelled);
        }

        internal void ResolveUnknownCancelToFullFill(ManagedOrderState intent, string operationId)
        {
            var order = _orders.Single(candidate => candidate.OrderId == intent.EntryOrderId);
            RecordFullFillDuringCancel(intent, order, operationId, RecoveryOperationOutcome.NotExecuted);
        }

        internal void ResolveUnknownProtection(ManagedOrderState intent, string operationId)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            _protections.RemoveAll(candidate => candidate.PositionId == position.PositionId);
            _protections.Add(new PositionProtection(intent.Contract,
                new MoneyAmount(intent.TakeProfit, "USDC"), new MoneyAmount(intent.StopLoss, "USDC"),
                position.PositionId, PositionOwnership.SoftwareOwned, position.Quantity, position.Quantity,
                "tp-" + position.PositionId, "sl-" + position.PositionId));
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "SetFullPositionProtection", RecoveryOperationOutcome.Confirmed,
                PositionId: position.PositionId, Direction: position.Direction,
                PositionQuantity: position.Quantity);
        }

        internal void ResolveUnknownProtectionRejection(ManagedOrderState intent, string operationId)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "SetFullPositionProtection", RecoveryOperationOutcome.NotExecuted,
                PositionId: position.PositionId, Direction: position.Direction,
                PositionQuantity: position.Quantity, Retryability: RecoveryRetryability.Unknown);
        }

        internal void ResolveUnknownClose(ManagedOrderState intent, string operationId)
        {
            _positions.Clear();
            _orders.Clear();
            _protections.Clear();
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "ReduceOnlyClose", RecoveryOperationOutcome.Confirmed,
                PositionId: intent.PositionId, Direction: intent.Direction, PositionQuantity: 0m);
        }

        internal void ResolveUnknownCloseAsRetryableRejection(ManagedOrderState intent, string operationId)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "ReduceOnlyClose", RecoveryOperationOutcome.NotExecuted,
                PositionId: position.PositionId, Direction: position.Direction,
                PositionQuantity: position.Quantity, Retryability: RecoveryRetryability.Retryable);
        }

        internal void CloseByTakeProfit(ManagedOrderState intent)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            var protection = _protections.Single(candidate => candidate.PositionId == intent.PositionId);
            _positions.Remove(position);
            _protections.Remove(protection);
            _closedTrades.Add(new ClosedTrade(intent.Asset, intent.Contract, intent.Direction,
                intent.CreatedAt, clock.UtcNow, new MoneyAmount(0m, "USDC"), "Take profit",
                position.PositionId, [intent.EntryOrderId!], protection.TakeProfitOrderId,
                position.Quantity, PositionOwnership.SoftwareOwned, false, TradeCloseKind.TakeProfit));
        }

        internal void CloseByStopLoss(ManagedOrderState intent)
        {
            var position = _positions.Single(candidate => candidate.PositionId == intent.PositionId);
            var protection = _protections.Single(candidate => candidate.PositionId == intent.PositionId);
            _positions.Remove(position);
            _protections.Remove(protection);
            _closedTrades.Add(new ClosedTrade(intent.Asset, intent.Contract, intent.Direction,
                intent.CreatedAt, clock.UtcNow, new MoneyAmount(0m, "USDC"), "Stop loss",
                position.PositionId, [intent.EntryOrderId!], protection.StopLossOrderId,
                position.Quantity, PositionOwnership.SoftwareOwned, false, TradeCloseKind.StopLoss));
        }

        private void RecordFullFillDuringCancel(
            ManagedOrderState intent,
            OpenOrder order,
            string operationId,
            RecoveryOperationOutcome outcome)
        {
            var oldPosition = _positions.SingleOrDefault(candidate => candidate.Contract == intent.Contract);
            var positionId = oldPosition?.PositionId ?? intent.PositionId ?? "position-" + intent.IntentId[..8];
            _orders.Remove(order);
            _positions.RemoveAll(candidate => candidate.Contract == intent.Contract);
            _positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, intent.Quantity,
                "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned,
                "controlled fill-race fixture", positionId, [order.OrderId!]));
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "CancelOpeningRemainder", outcome, order.OrderId!, positionId,
                intent.Direction, intent.Quantity, 0m, intent.Quantity,
                intent.TakeProfitOrderId, intent.StopLossOrderId,
                EntryOrderState: ManagedEntryTerminalState.Filled);
        }

        private void ObserveEntry(
            ManagedOrderState intent,
            string operationId,
            RecoveryOperationOutcome outcome,
            decimal filledQuantity)
        {
            _positions.Clear();
            _orders.Clear();
            _protections.Clear();
            var orderId = "order-" + intent.IntentId[..8];
            _entryOrderId = orderId;
            var positionId = filledQuantity == 0m ? null : "position-" + intent.IntentId[..8];
            var remaining = outcome == RecoveryOperationOutcome.NotExecuted
                ? 0m : intent.Quantity - filledQuantity;
            if (filledQuantity > 0m)
                _positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, filledQuantity,
                    "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned,
                    "controlled fixture", positionId, [orderId]));
            if (remaining > 0m && outcome == RecoveryOperationOutcome.Confirmed)
                _orders.Add(new OpenOrder(intent.Asset, intent.Contract, intent.Direction, intent.Quantity,
                    filledQuantity, "contract", "Open", PositionOwnership.SoftwareOwned, "No TP/SL",
                    orderId, operationId));
            _observations[operationId] = new RecoveryOperationObservation(operationId, Venue, Account,
                intent.Contract, "PlaceOpeningOrder", outcome,
                outcome == RecoveryOperationOutcome.NotExecuted ? null : orderId,
                positionId, intent.Direction, filledQuantity, remaining,
                filledQuantity == 0m ? null : filledQuantity,
                EntryOrderState: outcome == RecoveryOperationOutcome.NotExecuted
                    ? ManagedEntryTerminalState.Rejected
                    : remaining > 0m ? ManagedEntryTerminalState.Open : ManagedEntryTerminalState.Filled,
                FirstFillAt: filledQuantity > 0m && !OmitFirstFillTime ? clock.UtcNow : null);
        }

        private void BeforeCallback(string action, string operationId)
        {
            Actions.Add(action);
            ActionCalls.Add(action);
            var saved = _store!.Load().State;
            DurableTransferSeenBeforeEveryCallback &= saved.Operations.SingleOrDefault(operation =>
                    operation.OperationId == operationId)?.Stage == RecoveryOperationStage.TransferStarted
                && saved.ManagedOrders.Any(order => order.PendingOperationId == operationId);
        }
    }

    private sealed class RecordingChannel(NotificationChannel channel) : INotificationChannel
    {
        public NotificationChannel Channel => channel;
        public bool IsEnabled => true;
        public bool IsConfigured => true;
        public List<NotificationMessage> Sent { get; } = [];

        public Task<NotificationChannelSendResult> SendAsync(
            NotificationMessage message,
            CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(new NotificationChannelSendResult(channel == NotificationChannel.Windows
                ? NotificationAttemptStatus.PassedToWindows : NotificationAttemptStatus.AcceptedByTelegram));
        }
    }
}
