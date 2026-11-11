using System.IO;
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
              && state.NextEmergencyCloseAt is null,
            "an unfilled Limit stays open without starting the partial-fill or emergency-close timer");

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
        using (var stale = new Harness(Path.Combine(root, "stale"), InitialTime))
        {
            stale.Adapter.StaleSnapshot = true;
            var result = stale.Orders.OpenAsync(fixture.Intent, fixture.Input, savedOperatingIntent: true)
                .GetAwaiter().GetResult();
            check(!result.Succeeded && stale.Adapter.ActionCalls.Count == 0
                  && stale.Orders.Orders.Single().Status == ManagedOrderStatus.IntentSaved
                  && !stale.Recovery.IsEntryAllowed(Venue, savedOperatingIntent: true),
                "a stale exact-account snapshot blocks entry and preserves a venue read-recovery pause");
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

    private sealed class Harness : IDisposable
    {
        private readonly string _journalPath;
        private readonly RecoveryJournalStore _store;

        internal Harness(string root, DateTimeOffset now)
        {
            Directory.CreateDirectory(root);
            _journalPath = Path.Combine(root, "recovery-state.json");
            _store = new RecoveryJournalStore(_journalPath);
            Clock = new FakeClock(now);
            Settings = new AppSettings();
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
        public DateTimeOffset UtcNow { get; set; } = now;
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
            return Task.FromResult(new RecoverySnapshotRead(true,
                snapshot,
                OpeningOrderRemaindersComplete: true,
                ProtectionCoverageComplete: true,
                UnfinishedOperationsComplete: true,
                OperationOutcomes: new Dictionary<string, RecoveryOperationObservation>(_observations,
                    StringComparer.Ordinal),
                ClosedPositionHistoryComplete: true));
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
                    : remaining > 0m ? ManagedEntryTerminalState.Open : ManagedEntryTerminalState.Filled);
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
