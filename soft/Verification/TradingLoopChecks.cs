using System.Globalization;
using System.IO;
using System.Text.Json;
using TrueNorth.TradeFlow;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Orders;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;
using TrueNorth.TradeFlow.Trading;

namespace TrueNorth.TradeFlow.Verification;

internal static class TradingLoopChecks
{
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    internal static async Task CheckCoreAsync(Action<bool, string> check, string root)
    {
        await CheckNoSetupSurvivesRestart(check, Path.Combine(root, "trading-loop-no-setup"));
        await CheckFundsShortfallSkipsAi(check, Path.Combine(root, "trading-loop-no-funds"));
        await CheckConfirmedMinimumSkipsAi(check, Path.Combine(root, "trading-loop-confirmed-minimum"));
        await CheckReassessmentAgeBoundary(check, Path.Combine(root, "trading-loop-expired"));
        await CheckYoungCandidateRequiresFreshAi(check, Path.Combine(root, "trading-loop-young"));
        await CheckPendingErrorsKeepTheirRetry(check, Path.Combine(root, "trading-loop-pending-retry"));
        await CheckPendingRiskAndRefreshRetry(check, Path.Combine(root, "trading-loop-pending-risk"));
        await CheckFreshExposureAndSelectedAlternative(check, Path.Combine(root, "trading-loop-exposure-refresh"));
        await CheckVenueAdmissionIsScoped(check, Path.Combine(root, "trading-loop-venues"));
        await CheckStopBeforeAndDuringAi(check, Path.Combine(root, "trading-loop-stop"));
        await CheckProfileUpdateAndTestMode(check, Path.Combine(root, "trading-loop-profile-update"));
        await CheckProfileUpdatePreservesOpenLimit(check, Path.Combine(root, "trading-loop-profile-limit"));
        await CheckSanitizedFailureDetails(check, Path.Combine(root, "trading-loop-sanitized-errors"));
        CheckStartStopIntentSerialization(check);
        await CheckFailedWritesCloseAdmission(check, Path.Combine(root, "trading-loop-write-failures"));
        await CheckSuccessfulEntryAndProtection(check, Path.Combine(root, "trading-loop-entry"));
        await CheckUnknownEntryIsNotRepeated(check, Path.Combine(root, "trading-loop-unknown"));
        await CheckTransferFence(check, Path.Combine(root, "trading-loop-fence"));
    }

    private static async Task CheckNoSetupSurvivesRestart(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        check((await harness.Loop.StartAsync()).Succeeded, "a reconciled local loop can save Start");
        var first = await harness.Loop.RunOnceAsync();
        check(first.Succeeded && first.Status.Contains("NO_SETUP", StringComparison.Ordinal)
              && harness.Ai.Requests.Count == 1,
            $"valid NO_SETUP is distinct from an AI failure and queried once (status={first.Status}; requests={harness.Ai.Requests.Count}; stored={harness.Loop.State.LastStatus}; recovery={harness.Recovery.StatusText})");
        var retryAt = harness.Loop.State.NextSearchAt["SOL"];
        check(retryAt == InitialTime.AddMinutes(240), "NO_SETUP persists the default 240-minute retry time");

        await harness.RestartAsync();
        check(harness.Loop.State.NextSearchAt["SOL"] == retryAt,
            "the retry deadline survives process restart through the recovery journal");
        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Requests.Count == 1,
            "a balance refresh does not reset a persisted NO_SETUP deadline");

        harness.Clock.UtcNow = retryAt;
        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Requests.Count == 2, "the asset becomes searchable at its durable due time");
    }

    private static async Task CheckFundsShortfallSkipsAi(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"], availableMargin: 0m);
        await harness.Loop.StartAsync();
        var result = await harness.Loop.RunOnceAsync();
        check(result.Succeeded && result.Status.Contains("Недостаточно", StringComparison.Ordinal)
              && harness.Ai.Requests.Count == 0,
            "zero free margin is rechecked without an AI request");
    }

    private static async Task CheckConfirmedMinimumSkipsAi(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"], availableMargin: 100m);
        harness.Contexts.MinimumEntryAffordable = false;
        await harness.Loop.StartAsync();
        var result = await harness.Loop.RunOnceAsync();
        check(result.Status.Contains("подтверждённого минимума", StringComparison.Ordinal)
              && harness.Ai.Requests.Count == 0,
            "positive margin below the controlled confirmed minimum avoids AI without recalculating 007");
    }

    private static async Task CheckReassessmentAgeBoundary(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        await harness.SavePendingAsync(InitialTime.AddHours(-24));
        await harness.Loop.StartAsync();
        var result = await harness.Loop.RunOnceAsync();
        check(!result.Succeeded && result.Status.Contains("24 часа", StringComparison.Ordinal)
              && harness.Ai.Requests.Count == 0 && harness.Loop.State.PendingSetup is null,
            "age exactly 24 hours discards the saved response without AI reassessment");

        using var invalidTime = new Harness(Path.Combine(root, "future"), ["SOL"]);
        await invalidTime.SavePendingAsync(InitialTime.AddMinutes(1));
        await invalidTime.Loop.StartAsync();
        var future = await invalidTime.Loop.RunOnceAsync();
        check(!future.Succeeded && invalidTime.Ai.Requests.Count == 0
              && invalidTime.Loop.State.PendingSetup is null,
            "a future response timestamp closes entry and drops the candidate");

        using var missingTime = new Harness(Path.Combine(root, "missing"), ["SOL"]);
        await missingTime.SavePendingAsync(null);
        await missingTime.Loop.StartAsync();
        var missing = await missingTime.Loop.RunOnceAsync();
        check(!missing.Succeeded && missingTime.Ai.Requests.Count == 0
              && missingTime.Loop.State.PendingSetup is null,
            "a missing response timestamp closes entry and drops the candidate");
    }

    private static async Task CheckYoungCandidateRequiresFreshAi(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        await harness.SavePendingAsync(InitialTime.AddHours(-23));
        harness.Ai.Handler = request => Task.FromResult(NoSetup(request));
        var origin = harness.SavedPendingSetup!;
        await harness.Loop.StartAsync();
        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Requests.Count == 1 && harness.Adapter.SubmitCount == 0
              && harness.Ai.Reassessments.Count == 1 && harness.Loop.State.PendingSetup is null
              && harness.Ai.Reassessments[0].Request.Id == origin.Request.Id
              && harness.Ai.Reassessments[0].OriginResponse == origin.Response
              && harness.Ai.Reassessments[0].ReceivedAt == origin.ReceivedAt,
            "a young saved candidate is reassessed once with its original request, response and receipt before it can enter");
        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Requests.Count == 1,
            "a completed restart reassessment waits on its durable NO_SETUP deadline in later ticks");
    }

    private static async Task CheckPendingErrorsKeepTheirRetry(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        await harness.SavePendingAsync(InitialTime.AddHours(-1));
        harness.Ai.Handler = _ => throw new InvalidOperationException("private-token-value");
        await harness.Loop.StartAsync();
        var first = await harness.Loop.RunOnceAsync();
        var retryAt = harness.Loop.State.NextSearchAt["SOL"];
        check(!first.Succeeded && harness.Loop.State.PendingSetup is not null
              && retryAt == InitialTime.AddMinutes(240),
            "an unclear restart reassessment keeps the saved candidate and persists its accepted retry deadline");

        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Reassessments.Count == 1 && harness.Loop.State.PendingSetup is not null,
            "ordinary ticks do not repeat an incomplete reassessment before its persisted deadline");

        harness.Ai.Handler = request => Task.FromResult(NoSetup(request));
        harness.Clock.UtcNow = retryAt;
        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Reassessments.Count == 2 && harness.Loop.State.PendingSetup is null,
            "the saved candidate can be reassessed again at its due time");
    }

    private static async Task CheckFreshExposureAndSelectedAlternative(Action<bool, string> check, string root)
    {
        using var manual = new Harness(Path.Combine(root, "manual"), ["SOL"]);
        var alternate = manual.AddAlternativeRoute("SOL", Harness.VenueB, Harness.AccountB,
            "SOL-PERP", availableMargin: 1000m, balance: 2000m);
        manual.Ai.Handler = request =>
        {
            manual.Adapter.AddManualPosition(Harness.VenueB, Harness.AccountB,
                new OpenPosition("SOL", "SOL-PERP", PositionDirection.Long, 1m, "contract",
                    new MoneyAmount(0m, "USDC"), PositionOwnership.Manual, "local fixture", "manual-sol"));
            return Task.FromResult(Setup(request, manual.Clock.UtcNow));
        };
        await manual.Loop.StartAsync();
        await manual.Loop.RunOnceAsync();
        check(manual.Adapter.SubmitCount == 0 && manual.Loop.State.PendingSetup is not null
              && manual.Ai.Requests.Count == 1 && manual.Contexts.LastInput?.Pools
                  .Single(pool => pool.Account.Venue == Harness.VenueB).Account.Positions.Value!
                  .Any(position => position.PositionId == "manual-sol") == true,
            "an intervening manual duplicate on another venue is seen in post-AI account and exposure evidence");

        using var selected = new Harness(Path.Combine(root, "selected"), ["SOL"]);
        selected.Adapter.SetAvailableMargin(Harness.VenueA, Harness.AccountA, 1m);
        var selectedRoute = selected.AddAlternativeRoute("SOL", Harness.VenueB, Harness.AccountB,
            "SOL-PERP", availableMargin: 150m, balance: 1000m);
        selected.Ai.Handler = request => Task.FromResult(Setup(request, selected.Clock.UtcNow));
        await selected.Loop.StartAsync();
        var selectedResult = await selected.Loop.RunOnceAsync();
        var managed = selected.Orders.Orders.SingleOrDefault();
        check(selectedResult.Succeeded && managed?.Venue == selectedRoute.Venue
              && managed.Account == selectedRoute.Account && selected.Adapter.SubmitCount == 1,
            $"007 can select an eligible confirmed alternative route and the order uses that exact route (status={selectedResult.Status}; venue={managed?.Venue}; account={managed?.Account}; submits={selected.Adapter.SubmitCount})");

        using var unavailable = new Harness(Path.Combine(root, "no-fallback"), ["SOL"]);
        unavailable.Adapter.SetAvailableMargin(Harness.VenueA, Harness.AccountA, 1m);
        var unavailableRoute = unavailable.AddAlternativeRoute("SOL", Harness.VenueB, Harness.AccountB,
            "SOL-PERP", availableMargin: 150m, balance: 1000m);
        unavailable.Contexts.UnavailableRoutes.Add(unavailableRoute);
        unavailable.Ai.Handler = request => Task.FromResult(Setup(request, unavailable.Clock.UtcNow));
        await unavailable.Loop.StartAsync();
        var blocked = await unavailable.Loop.RunOnceAsync();
        check(!blocked.Succeeded && blocked.Status.Contains("007 выбрал маршрут", StringComparison.Ordinal)
              && unavailable.Adapter.SubmitCount == 0
              && unavailable.Loop.State.PendingSetup is not null,
            "a route chosen by 007 but marked unavailable is deferred without falling back to another venue");
    }

    private static async Task CheckPendingRiskAndRefreshRetry(Action<bool, string> check, string root)
    {
        using var riskWait = new Harness(Path.Combine(root, "risk-wait"), ["SOL"], recoveryCheckSeconds: 10);
        riskWait.Ai.Handler = request =>
        {
            riskWait.Adapter.SetAvailableMargin(Harness.VenueA, Harness.AccountA, 0m);
            return Task.FromResult(Setup(request, riskWait.Clock.UtcNow));
        };
        await riskWait.Loop.StartAsync();
        await riskWait.Loop.RunOnceAsync();
        var dueAt = riskWait.Loop.State.NextSearchAt["SOL"];
        await riskWait.Loop.RunOnceAsync();
        check(riskWait.Loop.State.PendingSetup is not null && riskWait.Ai.Requests.Count == 1
              && dueAt == InitialTime.AddSeconds(10),
            "a temporary 007 denial keeps the candidate and bounded persisted retry without repeating AI");
        riskWait.Adapter.SetAvailableMargin(Harness.VenueA, Harness.AccountA, 1000m);
        riskWait.Clock.UtcNow = dueAt;
        var freshRetry = await riskWait.Loop.RunOnceAsync();
        check(riskWait.Adapter.SubmitCount == 1 && riskWait.Ai.Requests.Count == 1,
            $"fresh account conditions retry the saved candidate without a second AI call (status={freshRetry.Status}; submits={riskWait.Adapter.SubmitCount}; orders={string.Join(',', riskWait.Orders.Orders.Select(order => order.Status))}; recovery={riskWait.Recovery.StatusText})");

        using var refreshFailure = new Harness(Path.Combine(root, "refresh-failure"), ["SOL"]);
        refreshFailure.Ai.Handler = request =>
        {
            refreshFailure.Adapter.ReadFailure = new IOException("private-read-detail");
            return Task.FromResult(Setup(request, refreshFailure.Clock.UtcNow));
        };
        await refreshFailure.Loop.StartAsync();
        await refreshFailure.Loop.RunOnceAsync();
        var next = await refreshFailure.Loop.RunOnceAsync();
        check(next.Status.Contains("снимка", StringComparison.Ordinal)
              && refreshFailure.Loop.State.PendingSetup is not null && refreshFailure.Ai.Requests.Count == 1,
            "a post-AI account refresh failure preserves the candidate and blocks repeated AI calls");
    }

    private static async Task CheckVenueAdmissionIsScoped(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL", "ETH", "BTC"]);
        await harness.Recovery.ReportFailureAsync(Harness.VenueA, Harness.AccountA,
            "Синтетическая пауза одной площадки.", harness.Profile.Settings);
        harness.Ai.Handler = request => Task.FromResult(NoSetup(request));
        await harness.Loop.StartAsync();
        await harness.Loop.RunOnceAsync();
        check(harness.Ai.Requests.Select(request => request.Asset).SequenceEqual(["BTC"]),
            "a same-venue pause blocks its assets while an independent venue remains eligible");

        using var unknownCoverage = new Harness(Path.Combine(root, "unknown-coverage"), ["SOL"]);
        unknownCoverage.Contexts.ExposureCovered = false;
        var blocked = await unknownCoverage.Loop.StartAsync();
        check(!blocked.Succeeded && !unknownCoverage.Loop.IsAdmitting && unknownCoverage.Ai.Requests.Count == 0,
            "unknown connected-exposure coverage blocks global entry admission before AI");
    }

    private static async Task CheckStopBeforeAndDuringAi(Action<bool, string> check, string root)
    {
        using var early = new Harness(Path.Combine(root, "early"), ["SOL"]);
        await early.Loop.StartAsync();
        await early.Loop.StopAsync();
        await early.Loop.RunOnceAsync();
        check(early.Ai.Requests.Count == 0 && !early.Loop.IsAdmitting,
            "Stop saved before a cycle prevents a later AI request");

        using var late = new Harness(Path.Combine(root, "late"), ["SOL"]);
        await late.Loop.StartAsync();
        late.Ai.BlockNextResponse();
        var cycle = late.Loop.RunOnceAsync();
        await late.Ai.RequestStarted.Task;
        var stop = late.Loop.StopAsync();
        check(!late.Loop.IsAdmitting, "Stop closes in-memory admission before waiting for the AI pipeline");
        late.Ai.ReleaseBlockedResponse();
        await cycle;
        var stopped = await stop;
        check(stopped.Succeeded && !late.Loop.IsRunning && late.Adapter.SubmitCount == 0,
            "a late AI response after Stop cannot start a transfer");
    }

    private static async Task CheckProfileUpdateAndTestMode(Action<bool, string> check, string root)
    {
        using var baseline = new Harness(Path.Combine(root, "baseline"), ["SOL"]);
        baseline.Ai.Handler = request => Task.FromResult(Setup(request, baseline.Clock.UtcNow));
        await baseline.Loop.StartAsync();
        await baseline.Loop.RunOnceAsync();
        var baselineQuantity = baseline.Orders.Orders.Single().PositionQuantity;

        using var updated = new Harness(Path.Combine(root, "updated"), ["SOL"]);
        updated.Ai.Handler = request => Task.FromResult(Setup(request, updated.Clock.UtcNow));
        await updated.Loop.StartAsync();
        var updatedProfile = updated.Profile.Copy();
        updatedProfile.Settings.MarginBalancePercent = 5m;
        var updatedSettings = await updated.Loop.UpdateProfileAsync(updatedProfile);
        check(updatedSettings && updated.Loop.IsRunning && updated.Loop.IsAdmitting
              && updated.SettingsStore.Load().Profile?.SavedOperatingIntent == true,
            "an ordinary settings update preserves a saved Start without closing admission");
        await updated.Loop.RunOnceAsync();
        var updatedQuantity = updated.Orders.Orders.Single().PositionQuantity;
        check(updated.Ai.Requests.Count == 1 && updatedQuantity < baselineQuantity,
            "the next accepted entry uses the updated sizing settings without another Start");

        using var staleDecision = new Harness(Path.Combine(root, "stale-decision"), ["SOL"]);
        await staleDecision.Loop.StartAsync();
        staleDecision.Ai.BlockNextResponse();
        var staleCycle = staleDecision.Loop.RunOnceAsync();
        await staleDecision.Ai.RequestStarted.Task;
        var staleProfile = staleDecision.Profile.Copy();
        staleProfile.Settings.MarginBalancePercent = 5m;
        var updateInProgress = staleDecision.Loop.UpdateProfileAsync(staleProfile);
        check(!staleDecision.Loop.IsAdmitting && staleDecision.Loop.IsRunning,
            "a profile update invalidates the in-flight entry decision while preserving Start");
        staleDecision.Ai.ReleaseBlockedResponse();
        var staleUpdateSaved = await updateInProgress;
        var staleResult = await staleCycle;
        check(staleUpdateSaved && !staleResult.Succeeded && staleDecision.Adapter.SubmitCount == 0
              && staleDecision.Loop.IsRunning && staleDecision.Loop.IsAdmitting
              && staleDecision.SettingsStore.Load().Profile?.SavedOperatingIntent == true,
            "the canceled AI response cannot submit and the updated Start remains available to a new cycle");

        using var testMode = new Harness(Path.Combine(root, "test-mode"), ["SOL"]);
        testMode.Ai.Handler = request => Task.FromResult(Setup(request, testMode.Clock.UtcNow));
        await testMode.Loop.StartAsync();
        await testMode.Loop.RunOnceAsync();
        var existing = testMode.Orders.Orders.Single();
        var testProfile = testMode.Profile.Copy();
        testProfile.SelectedMode = "Test";
        testProfile.SavedOperatingIntent = true;
        check(await testMode.Loop.UpdateProfileAsync(testProfile),
            "switching to Test persists Stop for the trading loop");
        var testRun = await testMode.Loop.RunOnceAsync();
        var after = testMode.Orders.Orders.Single();
        check(testRun.Succeeded && testMode.Ai.Requests.Count == 1
              && testMode.SettingsStore.Load().Profile?.SavedOperatingIntent == false
              && after.PositionQuantity == existing.PositionQuantity
              && after.TakeProfit == existing.TakeProfit && after.StopLoss == existing.StopLoss
              && after.Status == ManagedOrderStatus.Protected,
            "Test mode schedules no new AI entry and keeps the existing protected intent unchanged");
    }

    private static async Task CheckProfileUpdatePreservesOpenLimit(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"], entryType: "Limit");
        harness.Ai.Handler = request => Task.FromResult(Setup(request, harness.Clock.UtcNow));
        harness.Adapter.EntryFilledQuantity = 1m;
        await harness.Loop.StartAsync();
        var opened = await harness.Loop.RunOnceAsync();
        var before = harness.Orders.Orders.Single();
        var openOrder = harness.Adapter.GetOpenOrder(Harness.VenueA, Harness.AccountA, before.EntryOrderId!);
        check(opened.Succeeded && before.EntryTerminalState == ManagedEntryTerminalState.Open
              && before.RemainingQuantity > 0m && openOrder is not null,
            "the controlled Limit fixture has a protected partial fill and an owned open remainder");

        var updated = harness.Profile.Copy();
        updated.Settings.MarginBalancePercent = 5m;
        updated.Settings.PartialFillCancelMinutes = 30;
        var saved = await harness.Loop.UpdateProfileAsync(updated);
        var after = harness.Orders.Orders.Single();
        var retainedOrder = harness.Adapter.GetOpenOrder(Harness.VenueA, Harness.AccountA, before.EntryOrderId!);
        var retainedPosition = harness.Adapter.GetPosition(Harness.VenueA, Harness.AccountA, before.PositionId!);
        check(saved && harness.Loop.IsRunning && harness.Loop.IsAdmitting
              && harness.SettingsStore.Load().Profile?.SavedOperatingIntent == true
              && after == before && retainedOrder == openOrder && harness.Adapter.CancelCount == 0
              && retainedPosition?.Quantity == before.PositionQuantity,
            "changing settings preserves the existing Limit remainder, filled position and saved Start without cancellation");
    }

    private static async Task CheckSanitizedFailureDetails(Action<bool, string> check, string root)
    {
        const string privateDetail = "private-token-value-987";
        using var aiFailure = new Harness(Path.Combine(root, "ai"), ["SOL"]);
        aiFailure.Ai.Handler = _ => throw new InvalidOperationException(privateDetail);
        await aiFailure.Loop.StartAsync();
        var aiResult = await aiFailure.Loop.RunOnceAsync();
        var failureEvent = aiFailure.Recovery.State.Events.Single(item => item.Type == RecoveryEventType.Error);
        check(!aiResult.Status.Contains(privateDetail, StringComparison.Ordinal)
              && !failureEvent.Message.Contains(privateDetail, StringComparison.Ordinal)
              && failureEvent.InternalDetail?.Contains(privateDetail, StringComparison.Ordinal) != true
              && !failureEvent.SafeSummary.Contains(privateDetail, StringComparison.Ordinal),
            "AI exceptions expose only sanitized error details in status and durable notifications");

        using var readFailure = new Harness(Path.Combine(root, "read"), ["SOL"]);
        readFailure.Adapter.ReadFailure = new IOException(privateDetail);
        var read = await readFailure.Recovery.ReadCurrentSnapshotAsync(Harness.VenueA, Harness.AccountA);
        var recoveryEvents = readFailure.Recovery.State.Events.Where(item => item.Type == RecoveryEventType.Error)
            .ToArray();
        var sanitized = !read.SessionAvailable
            && read.FailureReason?.Contains(privateDetail, StringComparison.Ordinal) != true
            && !readFailure.Recovery.StatusText.Contains(privateDetail, StringComparison.Ordinal)
            && readFailure.Recovery.State.Venues.All(venue =>
                !venue.Reason.Contains(privateDetail, StringComparison.Ordinal))
            && readFailure.Recovery.State.Venues.All(venue =>
                venue.LastReadFailure?.Contains(privateDetail, StringComparison.Ordinal) != true)
            && recoveryEvents.All(item =>
                !item.Message.Contains(privateDetail, StringComparison.Ordinal)
                && item.InternalDetail?.Contains(privateDetail, StringComparison.Ordinal) != true
                && !item.SafeSummary.Contains(privateDetail, StringComparison.Ordinal));
        check(sanitized,
            $"recovery read exceptions are sanitized before status persistence (session={read.SessionAvailable}; reasonSafe={read.FailureReason?.Contains(privateDetail, StringComparison.Ordinal) != true}; statusSafe={!readFailure.Recovery.StatusText.Contains(privateDetail, StringComparison.Ordinal)}; venues={readFailure.Recovery.State.Venues.Count}; events={recoveryEvents.Length})");
    }

    private static void CheckStartStopIntentSerialization(Action<bool, string> check)
    {
        var admission = new TradingLoopAdmission();
        var generation = admission.Generation;
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedOperatingIntent = false;

        var start = Task.Run(() => admission.RunIfGeneration(generation, () =>
        {
            savedOperatingIntent = true;
            saveEntered.SetResult();
            releaseSave.Task.GetAwaiter().GetResult();
            return admission.Open(generation);
        }));
        saveEntered.Task.GetAwaiter().GetResult();
        var stop = Task.Run(() =>
        {
            stopEntered.SetResult();
            admission.Close();
            savedOperatingIntent = false;
        });
        stopEntered.Task.GetAwaiter().GetResult();
        var stopWaitedForStartCommit = !stop.Wait(20);
        releaseSave.SetResult();
        Task.WaitAll(start, stop);
        check(stopWaitedForStartCommit && !savedOperatingIntent && !admission.IsStarted,
            "a concurrent Stop serializes after the final Start save and leaves durable intent stopped");

        var staleGeneration = admission.Generation;
        admission.Close();
        var writeRan = false;
        var staleStart = admission.RunIfGeneration(staleGeneration, () =>
        {
            writeRan = true;
            return true;
        });
        check(!staleStart && !writeRan,
            "Stop before final Start commit rejects the stale generation without writing Start");
    }

    private static async Task CheckSuccessfulEntryAndProtection(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        harness.Ai.Handler = request => Task.FromResult(Setup(request, harness.Clock.UtcNow));
        await harness.Loop.StartAsync();
        var result = await harness.Loop.RunOnceAsync();
        var order = harness.Orders.Orders.SingleOrDefault();
        check(result.Succeeded && result.IntentId is not null && harness.Adapter.SubmitCount == 1
              && order?.Status == ManagedOrderStatus.Protected,
            $"the local cycle reaches one submitted entry and confirms its TP/SL protection through Orders 009 (status={result.Status}; id={result.IntentId}; submits={harness.Adapter.SubmitCount}; order={order?.Status}; recovery={harness.Recovery.StatusText})");
        check(harness.Adapter.DurableTransferSeenBeforeEveryCallback,
            "entry and protection callbacks run only after their operation and managed state are durable");
    }

    private static async Task CheckFailedWritesCloseAdmission(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        var badStartPath = Path.Combine(root, "blocked-start", "settings.json");
        Directory.CreateDirectory(badStartPath);
        var badStartStore = new SettingsStore(badStartPath);
        badStartStore.Load();
        var failedStartLoop = new TradingLoopCoordinator(badStartStore, harness.Profile,
            harness.Recovery, harness.Orders, harness.Ai, harness.Contexts, harness.Clock);
        var failedStart = await failedStartLoop.StartAsync();
        check(!failedStart.Succeeded && !failedStartLoop.IsAdmitting,
            "a failed Start settings write never opens entry admission");

        var activeProfile = harness.Profile.Copy();
        activeProfile.SavedOperatingIntent = true;
        var badStopPath = Path.Combine(root, "blocked-stop", "settings.json");
        Directory.CreateDirectory(badStopPath);
        var badStopStore = new SettingsStore(badStopPath);
        badStopStore.Load();
        var failedStopLoop = new TradingLoopCoordinator(badStopStore, activeProfile,
            harness.Recovery, harness.Orders, harness.Ai, harness.Contexts, harness.Clock);
        var failedStop = await failedStopLoop.StopAsync();
        check(!failedStop.Succeeded && !failedStopLoop.IsAdmitting
              && failedStop.Status.Contains("не записано", StringComparison.Ordinal),
            "a failed Stop settings write keeps the process stopped and reports that persistence failed");

        var rejectSettingsWrites = false;
        var updateStore = new SettingsStore(Path.Combine(root, "update-settings", "settings.json"),
            commitOverride: (temporary, target, _) =>
            {
                if (rejectSettingsWrites)
                    throw new IOException("synthetic settings write failure");
                File.Move(temporary, target, overwrite: true);
            });
        var seededProfile = updateStore.Save(harness.Profile).Profile!;
        var updateLoop = new TradingLoopCoordinator(updateStore, seededProfile,
            harness.Recovery, harness.Orders, harness.Ai, harness.Contexts, harness.Clock);
        var running = await updateLoop.StartAsync();
        rejectSettingsWrites = true;
        var rejectedProfile = seededProfile.Copy();
        rejectedProfile.Settings.MarginBalancePercent = 5m;
        var updateSaved = await updateLoop.UpdateProfileAsync(rejectedProfile);
        check(running.Succeeded && !updateSaved && updateLoop.IsRunning && updateLoop.IsAdmitting
              && updateStore.Load().Profile?.SavedOperatingIntent == true
              && updateStore.Load().Profile?.Settings.MarginBalancePercent == seededProfile.Settings.MarginBalancePercent,
            "a failed ordinary settings write preserves the prior running gate and saved profile");

        var failedRecovery = new RecoveryCoordinator(
            new RecoveryJournalStore(Path.Combine(root, "recovery-write-failure.json"),
                (_, _, _) => throw new IOException("synthetic journal failure")),
            harness.Profile.Settings, harness.Clock, harness.Adapter);
        var failedOrders = new OrderLifecycleCoordinator(failedRecovery, harness.Adapter,
            harness.Profile.Settings, harness.Clock);
        var failedLoop = new TradingLoopCoordinator(harness.SettingsStore, harness.Profile,
            failedRecovery, failedOrders, harness.Ai, harness.Contexts, harness.Clock);
        var failedRecoveryStart = await failedLoop.StartAsync();
        var result = await failedLoop.RunOnceAsync();
        var statusReportsUnpersistedDeadline = result.Status.Contains("не записан", StringComparison.Ordinal);
        check(!result.Succeeded && !failedRecovery.StorageReady && !failedLoop.IsAdmitting
              && statusReportsUnpersistedDeadline
              && !failedRecovery.StatusText.Contains("synthetic journal failure", StringComparison.Ordinal),
            $"a failed recovery-journal write reports the NO_SETUP deadline as unsaved and closes admission (succeeded={result.Succeeded}; statusMatch={statusReportsUnpersistedDeadline}; start={failedRecoveryStart.Status}; run={result.Status}; ready={failedRecovery.StorageReady}; admitting={failedLoop.IsAdmitting}; requests={harness.Ai.Requests.Count})");
    }

    private static async Task CheckUnknownEntryIsNotRepeated(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        harness.Adapter.EntryOutcome = RecoveryOperationOutcome.Unknown;
        harness.Ai.Handler = request => Task.FromResult(Setup(request, harness.Clock.UtcNow));
        await harness.Loop.StartAsync();
        await harness.Loop.RunOnceAsync();
        check(harness.Adapter.SubmitCount == 1
              && harness.Recovery.State.Operations.Any(operation => operation.Stage == RecoveryOperationStage.Unknown),
            "an unknown entry outcome remains a durable reconciliation item");

        await harness.RestartAsync();
        await harness.Loop.RunOnceAsync();
        check(harness.Adapter.SubmitCount == 1 && harness.Ai.Requests.Count == 1,
            "restart reconciles an unknown outcome instead of repeating its risky action");
    }

    private static async Task CheckTransferFence(Action<bool, string> check, string root)
    {
        using var harness = new Harness(root, ["SOL"]);
        var route = harness.Contexts.Routes.Single();
        var account = (await harness.Recovery.ReadCurrentSnapshotAsync(route.Venue, route.Account)).Account!;
        var profile = harness.Profile.Copy();
        profile.SavedOperatingIntent = true;
        var request = Request("SOL", "Market");
        var fields = AiResponseAssessor.Assess(request, Setup(request, harness.Clock.UtcNow)).Fields!;
        var setup = new EntryEligibilitySetup(EntrySetupState.Complete, "SOL", request.Market, "Market", null,
            PositionDirection.Long, 55m, 45m, "USDC/SOL", "local fixture", harness.Clock.UtcNow.AddSeconds(-10));
        var context = await harness.Contexts.ReadAsync(route, [account], setup, profile, harness.Clock.UtcNow,
            CancellationToken.None);
        var input = context.EligibilityInput!;
        var eligibility = EntryEligibilityEvaluator.Evaluate(input);
        var pool = eligibility.SelectedPool!;
        var intent = new ManagedOrderIntent(route.Venue, route.Account, route.Asset, route.Contract,
            PositionDirection.Long, "Market", pool.Quantity!.Value, null, 55m, 45m);

        var result = await harness.Orders.OpenAsync(intent, input, savedOperatingIntent: true,
            cancellationToken: CancellationToken.None, entryAdmissionFence: _ => false);
        check(!result.Succeeded && harness.Adapter.SubmitCount == 0
              && harness.Recovery.State.Operations.Any(operation => operation.Stage == RecoveryOperationStage.Abandoned),
            "the final entry fence can reject after asynchronous order reads without calling the adapter");
    }

    private static AiAnalysisRequest Request(string asset, string orderType)
    {
        if (!AiAnalysisRequestFactory.TryCreateBatch(asset, orderType, out var requests, out var error))
            throw new InvalidOperationException(error);
        return requests.Single();
    }

    private static AiSourceResponse Setup(AiAnalysisRequest request, DateTimeOffset now) =>
        new(request.Id, JsonSerializer.Serialize(new
        {
            status = "SETUP",
            market = request.Market,
            order_type = request.OrderType,
            side = "LONG",
            reference_price = 50m,
            reference_time_utc = now.AddSeconds(-10).ToString("O", CultureInfo.InvariantCulture),
            entry_price = request.OrderType == "MARKET" ? (decimal?)null : 50m,
            take_profit = 55m,
            stop_loss = 45m,
            reason = "синтетический тест",
            invalidation = "синтетическое условие"
        }), true);

    private static AiSourceResponse NoSetup(AiAnalysisRequest request) =>
        new(request.Id, JsonSerializer.Serialize(new
        {
            status = "NO_SETUP",
            market = request.Market,
            order_type = request.OrderType,
            side = (string?)null,
            reference_price = (decimal?)null,
            reference_time_utc = (string?)null,
            entry_price = (decimal?)null,
            take_profit = (decimal?)null,
            stop_loss = (decimal?)null,
            reason = "синтетически сетап не найден",
            invalidation = (string?)null
        }), true);

    private sealed class Harness : IDisposable
    {
        internal const string VenueA = "Scenario Venue A";
        internal const string AccountA = "Scenario Account A";
        internal const string VenueB = "Scenario Venue B";
        internal const string AccountB = "Scenario Account B";

        private readonly string _root;
        private readonly string _settingsPath;
        private readonly string _journalPath;

        internal Harness(
            string root,
            IReadOnlyList<string> assets,
            decimal availableMargin = 1000m,
            int recoveryCheckSeconds = 60,
            string entryType = "Market")
        {
            _root = Path.GetFullPath(root);
            Directory.CreateDirectory(_root);
            _settingsPath = Path.Combine(_root, "settings.json");
            _journalPath = Path.Combine(_root, "recovery-state.json");
            Clock = new FakeClock(InitialTime);
            Profile = new SettingsProfile
            {
                SelectedMode = "TrueNorth AI setups",
                Settings = new AppSettings
                {
                    Assets = assets.Cast<string?>().ToList(),
                    EntryType = entryType,
                    MarginBalancePercent = 10m,
                    Leverage = 1m,
                    RecoveryCheckSeconds = recoveryCheckSeconds
                }
            };
            SettingsStore = new SettingsStore(_settingsPath);
            var saved = SettingsStore.Save(Profile);
            Profile = saved.Profile ?? throw new InvalidOperationException(saved.Message);
            Adapter = new FixtureAdapter(Clock);
            var routes = assets.Select(asset => new TradingLoopRoute(asset,
                asset is "SOL" or "ETH" ? VenueA : VenueB,
                asset is "SOL" or "ETH" ? AccountA : AccountB,
                asset + "-PERP")).ToArray();
            foreach (var route in routes.GroupBy(route => (route.Venue, route.Account)).Select(group => group.First()))
                Adapter.AddAccount(route.Venue, route.Account, route.Asset, availableMargin);
            Adapter.Bind(new RecoveryJournalStore(_journalPath));
            Contexts = new FixtureContextSource(routes);
            Ai = new FixtureAi(Clock);
            StartServices();
        }

        internal FakeClock Clock { get; }
        internal SettingsProfile Profile { get; private set; }
        internal SettingsStore SettingsStore { get; private set; }
        internal FixtureAdapter Adapter { get; }
        internal FixtureContextSource Contexts { get; }
        internal FixtureAi Ai { get; }
        internal TradingLoopPendingSetup? SavedPendingSetup { get; private set; }
        internal RecoveryCoordinator Recovery { get; private set; } = null!;
        internal OrderLifecycleCoordinator Orders { get; private set; } = null!;
        internal TradingLoopCoordinator Loop { get; private set; } = null!;

        internal async Task SavePendingAsync(DateTimeOffset? receivedAt)
        {
            var request = Request("SOL", "Market");
            var response = Setup(request, Clock.UtcNow);
            SavedPendingSetup = new TradingLoopPendingSetup(request, response, receivedAt);
            var saved = await Recovery.SaveTradingLoopStateAsync(new TradingLoopState
            {
                PendingSetup = SavedPendingSetup
            });
            if (!saved)
                throw new InvalidOperationException("Could not seed the local pending-setup fixture.");
            Loop = CreateLoop();
        }

        internal TradingLoopRoute AddAlternativeRoute(
            string asset,
            string venue,
            string account,
            string contract,
            decimal availableMargin,
            decimal balance)
        {
            var route = new TradingLoopRoute(asset, venue, account, contract);
            Contexts.AddRoute(route);
            Adapter.AddAccount(venue, account, asset, availableMargin, balance);
            return route;
        }

        internal Task RestartAsync()
        {
            SettingsStore = new SettingsStore(_settingsPath);
            Profile = SettingsStore.Load().Profile ?? new SettingsProfile();
            StartServices();
            return Task.CompletedTask;
        }

        private void StartServices()
        {
            Recovery = new RecoveryCoordinator(new RecoveryJournalStore(_journalPath), Profile.Settings, Clock, Adapter);
            Orders = new OrderLifecycleCoordinator(Recovery, Adapter, Profile.Settings, Clock);
            Loop = CreateLoop();
        }

        private TradingLoopCoordinator CreateLoop() => new(SettingsStore, Profile, Recovery, Orders, Ai, Contexts, Clock);

        public void Dispose()
        {
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : IRecoveryClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FixtureAi(FakeClock clock) : IAiAnalysisSource, ITradingLoopReassessmentSource
    {
        private TaskCompletionSource<AiAnalysisRequest>? _started;
        private TaskCompletionSource<AiSourceResponse>? _release;
        internal readonly List<AiAnalysisRequest> Requests = [];
        internal readonly List<TradingLoopReassessmentContext> Reassessments = [];
        internal Func<AiAnalysisRequest, Task<AiSourceResponse>> Handler = request => Task.FromResult(NoSetup(request));
        internal TaskCompletionSource<AiAnalysisRequest> RequestStarted => _started
            ?? throw new InvalidOperationException("No blocked AI request was started.");

        internal void BlockNextResponse()
        {
            _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void ReleaseBlockedResponse()
        {
            var request = _started?.Task.Result ?? throw new InvalidOperationException("No AI request is blocked.");
            _release!.TrySetResult(Setup(request, clock.UtcNow));
        }

        public Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_release is not null)
            {
                _started!.TrySetResult(request);
                return _release.Task;
            }
            return Handler(request);
        }

        public Task<AiSourceResponse> ReassessAsync(
            TradingLoopReassessmentContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reassessments.Add(context);
            Requests.Add(context.Request);
            return Handler(context.Request);
        }
    }

    private sealed class FixtureContextSource(IReadOnlyList<TradingLoopRoute> routes) : ITradingLoopContextSource
    {
        private readonly List<TradingLoopRoute> _routes = routes.ToList();
        internal IReadOnlyList<TradingLoopRoute> Routes => _routes;
        internal bool ExposureCovered { get; set; } = true;
        internal bool? MinimumEntryAffordable { get; set; } = true;
        internal HashSet<TradingLoopRoute> UnavailableRoutes { get; } = [];
        internal EntryEligibilityInput? LastInput { get; private set; }

        internal void AddRoute(TradingLoopRoute route) => _routes.Add(route);

        public IReadOnlyList<TradingLoopRoute> GetRoutes(SettingsProfile profile) => _routes.ToArray();

        public Task<TradingLoopContext> ReadAsync(
            TradingLoopRoute route,
            IReadOnlyList<AccountSnapshot> accounts,
            EntryEligibilitySetup? setup,
            SettingsProfile profile,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = !UnavailableRoutes.Contains(route);
            if (setup is null)
                return Task.FromResult(new TradingLoopContext(available, ExposureCovered,
                    Reason: available ? null : "Синтетический маршрут недоступен.",
                    MinimumEntryAffordable: MinimumEntryAffordable));

            var example = EntryEligibilityExample.Create();
            var marketAt = now.AddSeconds(-2);
            var pools = new List<RiskPoolInput>();
            foreach (var account in accounts)
            {
                var template = example.Pools.SingleOrDefault(pool => pool.Account.Venue == account.Venue);
                if (template is null)
                    continue;
                var routeForAccount = _routes.FirstOrDefault(candidate => candidate.Asset == setup.Asset
                    && candidate.Venue == account.Venue && candidate.Account == account.Account);
                var contract = routeForAccount?.Contract
                    ?? template.Instruments.FirstOrDefault(instrument => instrument.Identity.BaseAsset == setup.Asset)
                        ?.Identity.Contract;
                if (string.IsNullOrWhiteSpace(contract))
                    continue;

                pools.Add(template with
                {
                    Account = account,
                    Positions = [],
                    Orders = [],
                    SetupBinding = new RiskSetupMarketBinding(account.Venue, contract, setup.Asset,
                        setup.Market, "local fixture", now.AddSeconds(-2)),
                    CurrentMarketPrice = setup.EntryType == "Market"
                        ? EntryEligibilityExample.Evidence(50m, "USDC/SOL", marketAt, contract) : null,
                    VerifiedLimitPrice = setup.EntryType == "Limit"
                        ? EntryEligibilityExample.Evidence(setup.LimitPrice!.Value, "USDC/SOL", marketAt, contract) : null
                });
            }

            var input = example with
            {
                Settings = profile.Copy(),
                Setup = setup,
                EvaluatedAt = now,
                Pools = pools,
                AllConnectedExposureCovered = EntryEligibilityExample.Evidence(ExposureCovered, "bool",
                    now.AddSeconds(-5))
            };
            LastInput = input;
            return Task.FromResult(new TradingLoopContext(available, ExposureCovered, input,
                available ? null : "Синтетический маршрут недоступен.", MinimumEntryAffordable));
        }
    }

    private sealed class FixtureAdapter(FakeClock clock) : IManagedOrderTestAdapter
    {
        private readonly Dictionary<(string Venue, string Account), AccountFixture> _accounts = [];
        private readonly Dictionary<string, RecoveryOperationObservation> _observations = new(StringComparer.Ordinal);
        private RecoveryJournalStore? _store;
        internal RecoveryOperationOutcome EntryOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        internal int SubmitCount { get; private set; }
        internal int CancelCount { get; private set; }
        internal bool DurableTransferSeenBeforeEveryCallback { get; private set; } = true;
        internal Exception? ReadFailure { get; set; }
        internal decimal? EntryFilledQuantity { get; set; }
        public TimeSpan MaximumSnapshotAge => TimeSpan.FromMinutes(5);

        internal void AddAccount(
            string venue,
            string account,
            string asset,
            decimal availableMargin,
            decimal balance = 1000m) =>
            _accounts.Add((venue, account), new AccountFixture(asset, availableMargin, balance));

        internal void SetAvailableMargin(string venue, string account, decimal value) =>
            _accounts[(venue, account)].AvailableMargin = value;

        internal void SetBalance(string venue, string account, decimal value) =>
            _accounts[(venue, account)].Balance = value;

        internal void AddManualPosition(string venue, string account, OpenPosition position) =>
            _accounts[(venue, account)].Positions.Add(position);

        internal OpenOrder? GetOpenOrder(string venue, string account, string orderId) =>
            _accounts[(venue, account)].Orders.SingleOrDefault(order => order.OrderId == orderId);

        internal OpenPosition? GetPosition(string venue, string account, string positionId) =>
            _accounts[(venue, account)].Positions.SingleOrDefault(position => position.PositionId == positionId);

        internal void Bind(RecoveryJournalStore store) => _store = store;

        public Task<RecoverySnapshotRead> ReadAsync(string venue, string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFailure is not null)
                throw ReadFailure;
            var fixture = _accounts[(venue, account)];
            var now = clock.UtcNow;
            var snapshot = new AccountSnapshot(venue, account, "USDC cross A", now, "local fixture", "USDC",
                "contract", AccountReadState.Available, null, now,
                AccountSection<AccountBalance>.Available(new AccountBalance(new MoneyAmount(fixture.Balance, "USDC"),
                    new MoneyAmount(fixture.Balance, "USDC"), new MoneyAmount(fixture.AvailableMargin, "USDC")), now,
                    "local fixture"),
                AccountSection<IReadOnlyList<OpenPosition>>.Available(fixture.Positions.ToArray(), now, "local fixture"),
                AccountSection<IReadOnlyList<OpenOrder>>.Available(fixture.Orders.ToArray(), now, "local fixture"),
                AccountSection<IReadOnlyList<PositionProtection>>.Available(fixture.Protections.ToArray(), now,
                    "local fixture"),
                AccountSection<IReadOnlyList<ClosedTrade>>.NotRead("local fixture"));
            return Task.FromResult(new RecoverySnapshotRead(true, snapshot, true, true, true,
                new Dictionary<string, RecoveryOperationObservation>(_observations, StringComparer.Ordinal),
                ClosedPositionHistoryComplete: true, EntryFillHistoryComplete: true));
        }

        public Task<RecoveryOperationOutcome> SubmitEntryAsync(
            ManagedOrderState intent,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurableBeforeCallback(operationId);
            SubmitCount++;
            if (EntryOutcome != RecoveryOperationOutcome.Confirmed)
                return Task.FromResult(EntryOutcome);

            var fixture = _accounts[(intent.Venue, intent.Account)];
            var orderId = "entry-" + intent.IntentId;
            var positionId = "position-" + intent.IntentId;
            fixture.Positions.Clear();
            fixture.Orders.Clear();
            fixture.Protections.Clear();
            var filled = EntryFilledQuantity ?? intent.Quantity;
            var remaining = intent.Quantity - filled;
            var confirmedPositionId = filled > 0m ? positionId : null;
            if (filled > 0m)
                fixture.Positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, filled,
                    "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned, "local fixture",
                    positionId, [orderId]));
            if (remaining > 0m)
                fixture.Orders.Add(new OpenOrder(intent.Asset, intent.Contract, intent.Direction, intent.Quantity,
                    filled, "contract", "Open", PositionOwnership.SoftwareOwned, "No TP/SL", orderId, operationId));
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "PlaceOpeningOrder", RecoveryOperationOutcome.Confirmed, orderId, confirmedPositionId,
                intent.Direction, filled, remaining, filled > 0m ? filled : null,
                EntryOrderState: remaining > 0m ? ManagedEntryTerminalState.Open : ManagedEntryTerminalState.Filled,
                FirstFillAt: filled > 0m ? clock.UtcNow : null);
            return Task.FromResult(RecoveryOperationOutcome.Confirmed);
        }

        public Task<RecoveryOperationOutcome> CancelRemainderAsync(
            ManagedOrderState intent,
            string orderId,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurableBeforeCallback(operationId);
            CancelCount++;
            var fixture = _accounts[(intent.Venue, intent.Account)];
            fixture.Orders.RemoveAll(order => order.OrderId == orderId);
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "CancelOpeningRemainder", RecoveryOperationOutcome.Confirmed, orderId,
                intent.PositionId, intent.Direction, intent.FilledQuantity, 0m, intent.PositionQuantity,
                EntryOrderState: ManagedEntryTerminalState.Cancelled);
            return Task.FromResult(RecoveryOperationOutcome.Confirmed);
        }

        public Task<RecoveryOperationOutcome> SetProtectionAsync(
            ManagedOrderState intent,
            string positionId,
            decimal actualQuantity,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurableBeforeCallback(operationId);
            var fixture = _accounts[(intent.Venue, intent.Account)];
            var position = fixture.Positions.SingleOrDefault(item => item.PositionId == positionId);
            if (position?.Quantity != actualQuantity)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            var tpId = "tp-" + positionId;
            var slId = "sl-" + positionId;
            fixture.Protections.Clear();
            fixture.Protections.Add(new PositionProtection(intent.Contract, new MoneyAmount(intent.TakeProfit, "USDC"),
                new MoneyAmount(intent.StopLoss, "USDC"), positionId, PositionOwnership.SoftwareOwned,
                actualQuantity, actualQuantity, tpId, slId));
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "SetFullPositionProtection", RecoveryOperationOutcome.Confirmed,
                PositionId: positionId, Direction: intent.Direction, PositionQuantity: actualQuantity,
                TakeProfitOrderId: tpId, StopLossOrderId: slId);
            return Task.FromResult(RecoveryOperationOutcome.Confirmed);
        }

        public Task<RecoveryOperationOutcome> CloseReduceOnlyAsync(
            ManagedOrderState intent,
            string positionId,
            PositionDirection positionDirection,
            decimal exactQuantity,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurableBeforeCallback(operationId);
            var fixture = _accounts[(intent.Venue, intent.Account)];
            fixture.Positions.RemoveAll(position => position.PositionId == positionId
                && position.Direction == positionDirection && position.Quantity == exactQuantity);
            fixture.Protections.RemoveAll(protection => protection.PositionId == positionId);
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "ReduceOnlyClose", RecoveryOperationOutcome.Confirmed,
                PositionId: positionId, Direction: positionDirection, PositionQuantity: 0m);
            return Task.FromResult(RecoveryOperationOutcome.Confirmed);
        }

        private void DurableBeforeCallback(string operationId)
        {
            var saved = _store!.Load().State;
            DurableTransferSeenBeforeEveryCallback &= saved.Operations.SingleOrDefault(operation =>
                    operation.OperationId == operationId)?.Stage == RecoveryOperationStage.TransferStarted
                && saved.ManagedOrders.Any(order => order.PendingOperationId == operationId);
        }

        private sealed class AccountFixture(string asset, decimal availableMargin, decimal balance)
        {
            internal string Asset { get; } = asset;
            internal decimal AvailableMargin { get; set; } = availableMargin;
            internal decimal Balance { get; set; } = balance;
            internal List<OpenPosition> Positions { get; } = [];
            internal List<OpenOrder> Orders { get; } = [];
            internal List<PositionProtection> Protections { get; } = [];
        }
    }
}
