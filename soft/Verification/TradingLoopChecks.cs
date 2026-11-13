using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        CheckCandidateProfitEstimates(check);
        await CheckCandidateRanking(check, Path.Combine(root, "trading-loop-ranking"));
        await CheckCandidateCollectionDeadline(check, Path.Combine(root, "trading-loop-ranking-deadline"));
        await CheckUncooperativeCandidateCancellation(check, Path.Combine(root, "trading-loop-ranking-uncooperative"));
        await CheckCandidateRankingCancellation(check, root);
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
        await CheckLimitReassessmentOutcomes(check, Path.Combine(root, "trading-loop-limit-outcomes"));
        await CheckLimitReassessmentRaces(check, Path.Combine(root, "trading-loop-limit-races"));
        await CheckLimitReassessmentRecovery(check, Path.Combine(root, "trading-loop-limit-recovery"));
        await CheckLocalScenarioJourney(check, Path.Combine(root, "trading-loop-local-journey"));
        await CheckLocalScenarioStopAndTest(check, Path.Combine(root, "trading-loop-local-stop"));
        await CheckLocalScenarioExit(check, Path.Combine(root, "trading-loop-local-exit"));
        await CheckLocalScenarioPlannedUpdate(check, Path.Combine(root, "trading-loop-local-update"));
        await CheckLocalScenarioDeadlineWake(check, Path.Combine(root, "trading-loop-local-deadline"));
        await CheckLocalScenarioFallbackDeadline(check, Path.Combine(root, "trading-loop-local-fallback-deadline"));
        await CheckLocalScenarioHeldDeadline(check, Path.Combine(root, "trading-loop-local-held-deadline"));
        await CheckLocalScenarioLimitDeadline(check, Path.Combine(root, "trading-loop-local-limit-deadline"));
    }

    private static void CheckCandidateProfitEstimates(Action<bool, string> check)
    {
        var fixture = EntryEligibilityExample.Create();
        var source = fixture.Pools.Single(pool => pool.Account.Venue == "Scenario Venue A");
        var instrument = source.Instruments.Single(item => item.Identity.Contract == "SOL-PERP") with
        {
            EntryFeeRate = 0.001m,
            ExitFeeRate = 0.001m,
            Funding = source.Instruments.Single(item => item.Identity.Contract == "SOL-PERP").Funding! with
            {
                RatePerInterval = 0.0005m,
                IntervalHours = 1m,
                Price = 100m,
                PositiveRateMeansLongPays = true
            }
        };
        var longSetup = new EntryEligibilitySetup(EntrySetupState.Complete, "SOL", "SOL-USDC", "Market",
            null, PositionDirection.Long, 111m, 90m, "USDC/SOL", "local fixture", InitialTime);
        var shortSetup = longSetup with { Direction = PositionDirection.Short, TakeProfit = 90m, StopLoss = 110m };

        var longValid = CandidateProfitEstimator.TryCalculate(longSetup, "USDC", 100m, 2m, 2m,
            instrument, out var longEstimate, out _);
        var shortValid = CandidateProfitEstimator.TryCalculate(shortSetup, "USDC", 100m, 2m, 2m,
            instrument, out var shortEstimate, out _);
        check(longValid && longEstimate is { GrossProfit: 22m, EntryFee: 0.2m, ExitFee: 0.222m,
                  AdverseFunding: 2.4m, NetProfit: 19.178m }
              && shortValid && shortEstimate is { GrossProfit: 20m, EntryFee: 0.2m, ExitFee: 0.18m,
                  AdverseFunding: 0m, NetProfit: 19.62m },
            $"TP calculation matches the agreed Long/Short funding example (Long={longEstimate}; Short={shortEstimate})");

        var rounded = CandidateProfitEstimator.TryCalculate(longSetup, "USDC", 100m, 20m, 2m,
            instrument with { ContractMultiplier = 0.1m }, out var roundedEstimate, out _);
        check(rounded && roundedEstimate == longEstimate,
            "candidate PnL uses rounded base quantity after the contract multiplier");

        var makerUnknown = CandidateProfitEstimator.TryCalculate(longSetup, "USDC", 100m, 2m, 2m,
            instrument with { EntryFeeRate = null }, out _, out _);
        var fundingUnknown = CandidateProfitEstimator.TryCalculate(longSetup, "USDC", 100m, 2m, 2m,
            instrument with { Funding = instrument.Funding! with { PositiveRateMeansLongPays = null } }, out _, out _);
        var badTakeProfitStep = CandidateProfitEstimator.TryCalculate(longSetup with { TakeProfit = 111.005m },
            "USDC", 100m, 2m, 2m, instrument, out _, out _);
        var quantityOverflow = CandidateProfitEstimator.TryCalculate(longSetup, "USDC", 100m,
            decimal.MaxValue, decimal.MaxValue, instrument with { ContractMultiplier = 2m }, out _, out _);
        check(!makerUnknown && !fundingUnknown && !badTakeProfitStep && !quantityOverflow,
            "unknown confirmed costs, overflow, and a TP outside the market price step exclude the candidate");

        var flipsFunding = CandidateProfitEstimator.TryCalculate(longSetup, "USDC", 100m, 2m, 2m,
            instrument with { Funding = instrument.Funding! with { PositiveRateMeansLongPays = false } },
            out var reversedFunding, out _);
        check(flipsFunding && reversedFunding?.AdverseFunding == 0m,
            "confirmed funding sign changes which side pays and funding credits remain zero");

        var winner = CandidateProfitEstimator.TrySelectWinner([longEstimate!, shortEstimate!],
            out var winnerIndex, out var currenciesDiffer);
        var tiedWinner = CandidateProfitEstimator.TrySelectWinner(
            [longEstimate! with { NetProfit = 5m }, shortEstimate! with { NetProfit = 5m }],
            out var tiedIndex, out _);
        var mixedWinner = CandidateProfitEstimator.TrySelectWinner(
            [longEstimate!, shortEstimate! with { Currency = "USD" }], out _, out var mixedCurrencies);
        var noPositive = CandidateProfitEstimator.TrySelectWinner(
            [longEstimate! with { NetProfit = 0m }], out var noPositiveIndex, out _);
        check(winner && winnerIndex == 1 && !currenciesDiffer
              && tiedWinner && tiedIndex == 0 && !mixedWinner && mixedCurrencies
              && !noPositive && noPositiveIndex == -1,
            "strict positive selection chooses the larger result, preserves asset order on ties, and stops on mixed currencies");
    }

    private static async Task CheckCandidateRanking(Action<bool, string> check, string root)
    {
        foreach (var entryType in new[] { "Market", "Limit" })
        {
            using var harness = new Harness(Path.Combine(root, entryType.ToLowerInvariant()), ["SOL", "ETH"],
                entryType: entryType, selectionMethod: "NetProfitAtTakeProfit");
            ConfigureRankingFixture(harness);
            var started = await harness.Loop.StartAsync();
            var result = await harness.Loop.RunOnceAsync();
            var order = harness.Orders.Orders.SingleOrDefault();
            var inputs = harness.Contexts.LastInputs.ToArray();
            var status = harness.Loop.State.LastStatus ?? string.Empty;
            var currentMarketRefreshed = entryType != "Market"
                || inputs.LastOrDefault(input => input.Setup.Asset == "SOL")?.Pools.Single()
                    .CurrentMarketPrice?.Value == 51m;
            var initialMarketWinnerChanges = entryType != "Market"
                || EstimateCandidate(inputs.First(input => input.Setup.Asset == "SOL"))
                    > EstimateCandidate(inputs.First(input => input.Setup.Asset == "ETH"));
            check(started.Succeeded && result.Succeeded && harness.Adapter.SubmitCount == 1
                  && order?.Asset == "ETH" && currentMarketRefreshed && initialMarketWinnerChanges
                  && inputs.Length == 4 && inputs.TakeLast(2).Select(input => input.EvaluatedAt).Distinct().Count() == 1
                  && status.Contains("Выбран ETH", StringComparison.Ordinal)
                  && status.Contains("Scenario Venue A/Scenario Account A/ETH-PERP", StringComparison.Ordinal)
                  && status.Contains("USDC", StringComparison.Ordinal)
                  && status.Contains("комиссия входа", StringComparison.Ordinal)
                  && status.Contains("funding за 24 ч", StringComparison.Ordinal)
                  && status.Contains("чистый", StringComparison.Ordinal),
                $"{entryType} refreshes the full candidate set before transfer, reranks a changed Market price, sends one highest-net candidate, and records its route and costs (status={result.Status})");
        }

        using (var marginChanged = new Harness(Path.Combine(root, "fresh-margin"), ["SOL", "ETH"],
                   selectionMethod: "NetProfitAtTakeProfit"))
        {
            ConfigureRankingFixture(marginChanged);
            marginChanged.Contexts.AfterInitialCandidateBatch = () =>
                marginChanged.Adapter.SetAvailableMargin(Harness.VenueA, Harness.AccountA, 1m);
            await marginChanged.Loop.StartAsync();
            var result = await marginChanged.Loop.RunOnceAsync();
            check(marginChanged.Adapter.SubmitCount == 0 && marginChanged.Loop.State.PendingSetup is null
                  && (marginChanged.Loop.State.LastStatus ?? string.Empty).Contains("обновления", StringComparison.Ordinal)
                  && marginChanged.Loop.State.NextSearchAt.ContainsKey("SOL"),
                $"a fresh account read that removes aggregate margin defers every ranked entry before transmission (status={result.Status})");
        }

        using (var eligibilityRecovers = new Harness(Path.Combine(root, "fresh-eligibility"), ["SOL", "ETH"],
                   selectionMethod: "NetProfitAtTakeProfit"))
        {
            ConfigureRankingFixture(eligibilityRecovers);
            eligibilityRecovers.Contexts.CandidateStopCoverageByAsset["ETH"] = 0m;
            eligibilityRecovers.Contexts.AfterInitialCandidateBatch = () =>
                eligibilityRecovers.Contexts.CandidateStopCoverageByAsset["ETH"] = 100m;
            await eligibilityRecovers.Loop.StartAsync();
            var result = await eligibilityRecovers.Loop.RunOnceAsync();
            var status = eligibilityRecovers.Loop.State.LastStatus ?? string.Empty;
            check(result.Succeeded && eligibilityRecovers.Orders.Orders.SingleOrDefault()?.Asset == "ETH"
                  && eligibilityRecovers.Contexts.LastInputs.Count == 4
                  && !eligibilityRecovers.Loop.State.NextSearchAt.ContainsKey("ETH")
                  && status.Contains("Выбран ETH", StringComparison.Ordinal)
                  && !status.Contains("ETH: допуск 007", StringComparison.Ordinal),
                $"a complete AI response that initially fails 007 can enter the refreshed comparison after updated evidence, clearing stale retry and skip data (status={result.Status})");
        }

        using (var interruptedRefresh = new Harness(Path.Combine(root, "interrupted-refresh"), ["SOL", "ETH"],
                   selectionMethod: "NetProfitAtTakeProfit"))
        {
            ConfigureRankingFixture(interruptedRefresh);
            interruptedRefresh.Contexts.AfterInitialCandidateBatch = () =>
                interruptedRefresh.Adapter.FailNextRead = true;
            await interruptedRefresh.Loop.StartAsync();
            var interrupted = await interruptedRefresh.Loop.RunOnceAsync();
            var orphanedRankedPending = interruptedRefresh.Loop.State.PendingSetup is
                { WasCandidateRanked: true, PreparedEntry: null };
            interruptedRefresh.Clock.UtcNow = InitialTime.AddSeconds(60);
            var freshCycle = await interruptedRefresh.Loop.RunOnceAsync();
            check(!interrupted.Succeeded && orphanedRankedPending
                  && freshCycle.Succeeded && interruptedRefresh.Adapter.SubmitCount == 1
                  && interruptedRefresh.Orders.Orders.SingleOrDefault()?.Asset == "ETH"
                  && interruptedRefresh.Ai.Requests.Count == 4
                  && interruptedRefresh.Contexts.LastInputs.Count == 6,
                $"an interrupted pre-transfer refresh discards its ranked pending and the next tick makes a fresh complete comparison (first={interrupted.Status}; next={freshCycle.Status}; requests={interruptedRefresh.Ai.Requests.Count}; inputs={interruptedRefresh.Contexts.LastInputs.Count})");
        }

        using (var stale = new Harness(Path.Combine(root, "stale-setup"), ["SOL", "ETH"],
                   selectionMethod: "NetProfitAtTakeProfit"))
        {
            stale.Ai.Handler = request => Task.FromResult(request.Asset == "SOL"
                ? Setup(request, stale.Clock.UtcNow, "LONG", 50m, 52m, 45m,
                    stale.Clock.UtcNow.AddMinutes(-20))
                : Setup(request, stale.Clock.UtcNow, "SHORT", 50m, 45m, 55m));
            await stale.Loop.StartAsync();
            var result = await stale.Loop.RunOnceAsync();
            var staleRetry = stale.Loop.State.NextSearchAt.TryGetValue("SOL", out var retryAt)
                && retryAt == InitialTime.AddSeconds(stale.Profile.Settings.RecoveryCheckSeconds);
            check(result.Succeeded && stale.Orders.Orders.Single().Asset == "ETH"
                  && staleRetry
                  && result.Status.Contains("свежего времени наблюдения", StringComparison.OrdinalIgnoreCase),
                $"a stale candidate is excluded and scheduled for the existing fresh-data retry while another candidate can win (succeeded={result.Succeeded}; order={stale.Orders.Orders.SingleOrDefault()?.Asset}; retry={stale.Loop.State.NextSearchAt.GetValueOrDefault("SOL")}; status={result.Status})");
        }

        using (var noSetup = new Harness(Path.Combine(root, "no-setup"), ["SOL", "ETH"],
                   selectionMethod: "NetProfitAtTakeProfit"))
        {
            noSetup.Ai.Handler = request => Task.FromResult(request.Asset == "SOL"
                ? NoSetup(request)
                : Setup(request, noSetup.Clock.UtcNow, "SHORT", 50m, 45m, 55m));
            await noSetup.Loop.StartAsync();
            var result = await noSetup.Loop.RunOnceAsync();
            check(result.Succeeded && noSetup.Adapter.SubmitCount == 1
                  && noSetup.Orders.Orders.Single().Asset == "ETH"
                  && noSetup.Loop.State.NextSearchAt["SOL"] == InitialTime.AddMinutes(noSetup.Profile.Settings.NewSetupRetryMinutes),
                "NO_SETUP keeps its existing retry deadline while the remaining candidate is ranked");
        }

        using var ordered = new Harness(Path.Combine(root, "asset-order"), ["SOL", "ETH"]);
        ConfigureRankingFixture(ordered);
        await ordered.Loop.StartAsync();
        var first = await ordered.Loop.RunOnceAsync();
        check(first.Succeeded && ordered.Adapter.SubmitCount == 1
              && ordered.Orders.Orders.Single().Asset == "SOL" && ordered.Ai.Requests.Count == 1,
            "profiles without the new selection field keep the first eligible asset behavior");
    }

    private static void ConfigureRankingFixture(Harness harness)
    {
        harness.Contexts.CandidateStopCoverage = 100m;
        harness.Contexts.SetMarketPrice("SOL", 45m);
        harness.Contexts.SetMarketPriceAfterInitialEvaluation("SOL", 51m);
        harness.Ai.Handler = request =>
        {
            if (request.Asset == "SOL")
                return Task.FromResult(Setup(request, harness.Clock.UtcNow, "LONG", 50m, 52m, 35m));
            return Task.FromResult(Setup(request, harness.Clock.UtcNow, "SHORT", 50m, 45m, 55m));
        };
    }

    private static decimal EstimateCandidate(EntryEligibilityInput input)
    {
        var eligibility = EntryEligibilityEvaluator.Evaluate(input);
        var pool = eligibility.SelectedPool ?? throw new InvalidOperationException(
            $"Candidate fixture did not pass 007: {eligibility.Status}; {string.Join(" ", eligibility.Reasons)}");
        var source = input.Pools.Single(item => item.Account.Venue == pool.Venue
            && item.Account.Account == pool.Account);
        var instrument = source.Instruments.Single(item => item.Identity.Contract == pool.Contract);
        if (!CandidateProfitEstimator.TryCalculate(input.Setup, pool.QuoteCurrency, pool.EntryPrice,
                pool.Quantity, pool.BaseQuantity, instrument, out var estimate, out _) || estimate is null)
            throw new InvalidOperationException("Candidate fixture did not have complete profit inputs.");
        return estimate.NetProfit;
    }

    private static async Task CheckCandidateCollectionDeadline(Action<bool, string> check, string root)
    {
        var delayStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            delayStarted.TrySetResult(duration);
            await releaseDeadline.Task.WaitAsync(cancellationToken);
        }

        using var harness = new Harness(root, ["SOL", "ETH"], selectionMethod: "NetProfitAtTakeProfit",
            delay: Delay);
        var requestCount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var cancelled = 0;
        harness.Ai.CancellationHandler = (request, cancellationToken) =>
        {
            var response = new TaskCompletionSource<AiSourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref cancelled);
                response.TrySetCanceled(cancellationToken);
            });
            if (Interlocked.Increment(ref started) == 2)
                requestCount.TrySetResult();
            return response.Task;
        };
        await harness.Loop.StartAsync();
        var cycle = harness.Loop.RunOnceAsync();
        var delayWindow = await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await requestCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Clock.UtcNow = InitialTime.AddMinutes(2);
        releaseDeadline.TrySetResult();
        var result = await cycle.WaitAsync(TimeSpan.FromSeconds(5));
        check(delayWindow == TimeSpan.FromMinutes(2) && harness.Adapter.SubmitCount == 0
              && cancelled == 2 && harness.Loop.State.PendingSetup is null
              && harness.Loop.State.NextSearchAt.Keys.OrderBy(value => value, StringComparer.Ordinal)
                  .SequenceEqual(["ETH", "SOL"])
              && !result.Status.Contains("Выбран", StringComparison.Ordinal),
            $"two-minute collection cancels both late synthetic AI calls and leaves no entry candidate (status={result.Status}; cancelled={cancelled})");
    }

    private static async Task CheckUncooperativeCandidateCancellation(Action<bool, string> check, string root)
    {
        var delayStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            delayStarted.TrySetResult(duration);
            await releaseDeadline.Task.WaitAsync(cancellationToken);
        }

        using var harness = new Harness(root, ["SOL", "ETH"], selectionMethod: "NetProfitAtTakeProfit",
            delay: Delay);
        ConfigureRankingFixture(harness);
        var hangingResponse = new TaskCompletionSource<AiSourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestStarted = new TaskCompletionSource<AiAnalysisRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = 0;
        harness.Ai.CancellationHandler = (request, cancellationToken) =>
        {
            if (request.Asset == "SOL")
                return Task.FromResult(Setup(request, harness.Clock.UtcNow, "LONG", 50m, 52m, 35m));
            cancellationToken.Register(() => Interlocked.Increment(ref cancellationObserved));
            secondRequestStarted.TrySetResult(request);
            return hangingResponse.Task;
        };

        await harness.Loop.StartAsync();
        var firstCycle = harness.Loop.RunOnceAsync();
        var deadline = await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var hangingRequest = await secondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseDeadline.TrySetResult();
        var first = await firstCycle.WaitAsync(TimeSpan.FromSeconds(5));
        var firstOrder = harness.Orders.Orders.SingleOrDefault();

        var assetOrderProfile = harness.Loop.Profile.Copy();
        assetOrderProfile.Settings.CandidateSelectionMethod = "AssetOrder";
        var switchedToAssetOrder = await harness.Loop.UpdateProfileAsync(assetOrderProfile);
        var rankedRetryDue = harness.Loop.State.NextSearchAt.GetValueOrDefault("ETH");
        harness.Clock.UtcNow = rankedRetryDue;
        var second = await harness.Loop.RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await harness.Loop.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var requestCount = harness.Ai.Requests.Count;
        hangingResponse.TrySetResult(Setup(hangingRequest, harness.Clock.UtcNow, "SHORT", 50m, 45m, 55m));

        check(deadline == TimeSpan.FromMinutes(2) && first.Succeeded && firstOrder?.Asset == "SOL"
              && cancellationObserved == 1 && harness.Adapter.SubmitCount == 1
              && switchedToAssetOrder && rankedRetryDue > InitialTime
              && requestCount == 2
              && second.Status.Contains("Предыдущий запрос AI после отмены", StringComparison.Ordinal)
              && stopped.Succeeded,
            $"a fast candidate remains usable at the deadline; an AI task ignoring cancellation cannot delay Stop or overlap a due AssetOrder search after a settings switch (second={second.Status}; canceled={cancellationObserved}; requests={requestCount})");
    }

    private static async Task CheckCandidateRankingCancellation(Action<bool, string> check, string root)
    {
        foreach (var action in new[] { "Stop", "Test", "exit" })
        {
            using var harness = new Harness(Path.Combine(root, "ranking-cancel-" + action), ["SOL", "ETH"],
                selectionMethod: "NetProfitAtTakeProfit");
            var requestCount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var cancelled = 0;
            harness.Ai.CancellationHandler = (request, cancellationToken) =>
            {
                var response = new TaskCompletionSource<AiSourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref cancelled);
                    response.TrySetCanceled(cancellationToken);
                });
                if (Interlocked.Increment(ref started) == 2)
                    requestCount.TrySetResult();
                return response.Task;
            };
            await harness.Loop.StartAsync();
            var cycle = harness.Loop.RunOnceAsync();
            await requestCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task control = action switch
            {
                "Stop" => harness.Loop.StopAsync(),
                "Test" => harness.Loop.UpdateProfileAsync(new SettingsProfile
                {
                    Settings = harness.Profile.Settings.Copy(),
                    SelectedMode = "Test",
                    SavedOperatingIntent = false
                }),
                _ => harness.Loop.HoldAdmissionForExitAsync()
            };
            var both = Task.WhenAll(cycle, control);
            await both.WaitAsync(TimeSpan.FromSeconds(5));
            check(cancelled == 2 && harness.Adapter.SubmitCount == 0
                  && harness.Loop.State.PendingSetup is null,
                $"{action} cancels the in-flight candidate collection before any entry is transmitted (cancelled={cancelled})");
        }
    }

    private static async Task CheckLocalScenarioJourney(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Market");
        var clock = new FakeClock(InitialTime);
        var scenario = new LocalTradingScenario(root, profile, clock, automaticScheduling: false);
        check(scenario.IsReady && !scenario.HasExternalConnections
              && scenario.SettingsPath.StartsWith(Path.Combine(root, "profile"), StringComparison.OrdinalIgnoreCase)
              && scenario.JournalPath.StartsWith(Path.Combine(root, "journal"), StringComparison.OrdinalIgnoreCase),
            "local scenario uses its own durable profile and journal with no external connections");
        var start = await scenario.StartAsync(profile);
        var first = await scenario.RunOnceAsync();
        check(start.Succeeded && first.Succeeded && scenario.SyntheticAiRequestCount == 1
              && scenario.SyntheticOpenOrders.Count == 0 && scenario.SyntheticPositions.Count == 1,
            $"synthetic search creates one filled opening request and position (start={start.Status}; cycle={first.Status}; requests={scenario.SyntheticAiRequestCount}; orders={scenario.SyntheticOpenOrders.Count}; positions={scenario.SyntheticPositions.Count})");

        var opened = scenario.ManagedOrders.FirstOrDefault(order => order.Status == ManagedOrderStatus.Protected);
        check(opened is not null && opened.EntryTerminalState == ManagedEntryTerminalState.Filled
              && opened.RemainingQuantity == 0m && opened.PositionQuantity > 0m,
            "local entry is linked to the A/B lifecycle with a fully filled, protected owned position");
        if (opened is null)
        {
            scenario.Dispose();
            return;
        }
        var afterOpen = await scenario.Recovery.ReadCurrentSnapshotAsync(opened.Venue, opened.Account);
        check(afterOpen.Account?.Protections.Value?.SingleOrDefault(item => item.PositionId == opened.PositionId) is
                { Ownership: PositionOwnership.SoftwareOwned, TakeProfitCoveredQuantity: var tpCovered,
                    StopLossCoveredQuantity: var slCovered }
              && tpCovered == opened.PositionQuantity && slCovered == opened.PositionQuantity,
            "synthetic TP and SL cover the exact owned position quantity");

        check(await scenario.CloseAtTakeProfitAsync(opened.IntentId),
            "the local fixture can deliver one durable synthetic take-profit close");
        clock.UtcNow = opened.NextManagementCheckAt
            ?? throw new InvalidOperationException("Synthetic position has no scheduled management check.");
        var next = await scenario.RunOnceAsync();
        var closed = scenario.ManagedOrders.SingleOrDefault(order => order.IntentId == opened.IntentId);
        check(next.Succeeded && closed?.Status == ManagedOrderStatus.Closed
              && scenario.SyntheticAiRequestCount == 2 && scenario.SyntheticPositions.Count == 1
              && scenario.ManagedOrders.Any(order => order.IntentId != opened.IntentId
                  && order.Status == ManagedOrderStatus.Protected),
            $"a verified TP close leads to the next synthetic search and protected entry (status={next.Status}; requests={scenario.SyntheticAiRequestCount}; positions={scenario.SyntheticPositions.Count})");

        var savedIntent = scenario.ManagedOrders.FirstOrDefault(order => order.IntentId != opened.IntentId)?.IntentId;
        scenario.Dispose();
        using var restarted = new LocalTradingScenario(root, profile, clock, automaticScheduling: false);
        var recovered = await restarted.ResumeAsync();
        check(recovered.Succeeded && restarted.HasSavedStart && restarted.SyntheticPositions.Count == 1
              && restarted.SyntheticOpenOrders.Count == 0
              && savedIntent is not null && restarted.ManagedOrders.Any(order => order.IntentId == savedIntent),
            "restart reconciles the durable local venue state before resuming without duplicating an entry");
    }

    private static async Task CheckLocalScenarioStopAndTest(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Limit");
        using var scenario = new LocalTradingScenario(root, profile,
            new FakeClock(InitialTime), automaticScheduling: false);
        check((await scenario.StartAsync(profile)).Succeeded, "local Stop fixture starts from a saved, complete profile");
        await scenario.RunOnceAsync();
        var testProfile = profile.Copy();
        testProfile.SelectedMode = "Test";
        testProfile.SavedOperatingIntent = true;
        var stopped = await scenario.UpdateProfileAsync(testProfile);
        var state = scenario.Snapshot();
        check(!scenario.Loop.IsAdmitting && scenario.Loop.Profile.SelectedMode == "Test"
              && !scenario.Loop.Profile.SavedOperatingIntent && scenario.SyntheticOpenOrders.Count == 0
              && scenario.SyntheticPositions.Count == 1 && state.HasOwnedMonitoring
              && state.OwnedPositionCount == 1 && state.OwnedOpenEntryCount == 0,
            $"Test saves mode and Stop together, cancels only the exact remainder, and keeps the protected position under monitoring (result={stopped.Status})");
    }

    private static async Task CheckLocalScenarioExit(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Limit");
        var clock = new FakeClock(InitialTime);
        using (var confirmed = new LocalTradingScenario(Path.Combine(root, "confirmed"), profile, clock,
                   automaticScheduling: false))
        {
            await confirmed.StartAsync(profile);
            await confirmed.RunOnceAsync();
            var before = await confirmed.HoldAndReconcileForExitAsync();
            check(before.IsKnown && before.OpenEntries.Count == 1 && confirmed.Loop.IsExitHeld
                  && !confirmed.Loop.IsAdmitting,
                "tray exit holds admission before counting exactly owned local opening entries");
            confirmed.FillDuringNextCancel(1m);
            var cancelled = await confirmed.CancelOpenEntriesForExitAsync(before);
            check(cancelled.Snapshot.IsKnown && cancelled.Snapshot.OpenEntries.Count == 0
                  && cancelled.Results.Count == 1
                  && cancelled.Results[0].Contains(before.OpenEntries[0].OrderId, StringComparison.Ordinal)
                  && confirmed.SyntheticOpenOrders.Count == 0
                  && confirmed.SyntheticPositions.Count == 1,
                "exit cancellation follows the exact owned ID, reconciles its fill race, and leaves the position open");
            var resumed = await confirmed.ResumeAfterExitAsync();
            check(resumed.Succeeded && !confirmed.Loop.IsExitHeld && confirmed.Loop.IsAdmitting
                  && confirmed.SyntheticPositions.Count == 1,
                "Return reconciles first and then restores the saved Start intent");
        }

        var cancelAndLeaveRoot = Path.Combine(root, "cancel-and-leave");
        using (var cancelAndLeave = new LocalTradingScenario(cancelAndLeaveRoot, profile, clock,
                   automaticScheduling: false))
        {
            await cancelAndLeave.StartAsync(profile);
            await cancelAndLeave.RunOnceAsync();
            var before = await cancelAndLeave.HoldAndReconcileForExitAsync();
            var cancelled = await cancelAndLeave.CancelOpenEntriesForExitAsync(before);
            check(cancelled.Snapshot.IsKnown && cancelled.Snapshot.OpenEntries.Count == 0
                  && cancelled.Results.Count == 1 && cancelAndLeave.SyntheticPositions.Count == 1,
                "confirmed Cancel then Leave records its exact cancellation and preserves the open position");
        }
        using (var restartedAfterCancel = new LocalTradingScenario(cancelAndLeaveRoot, profile, clock,
                   automaticScheduling: false))
        {
            await restartedAfterCancel.ResumeAsync();
            check(restartedAfterCancel.HasSavedStart && restartedAfterCancel.SyntheticOpenOrders.Count == 0
                  && restartedAfterCancel.SyntheticPositions.Count == 1,
                "restart after Cancel and Leave reconciles the position without recreating the cancelled remainder");
        }

        var unknownRoot = Path.Combine(root, "unknown");
        using (var unknown = new LocalTradingScenario(unknownRoot, profile, clock, automaticScheduling: false))
        {
            await unknown.StartAsync(profile);
            await unknown.RunOnceAsync();
            var before = await unknown.HoldAndReconcileForExitAsync();
            unknown.SetNextCancelOutcome(RecoveryOperationOutcome.Unknown);
            var result = await unknown.CancelOpenEntriesForExitAsync(before);
            check(!result.Snapshot.IsKnown && result.Results.Count == 1
                  && result.Snapshot.CancellationResults.Count == 1
                  && unknown.SyntheticOpenOrders.Count == 1 && unknown.SyntheticPositions.Count == 1
                  && unknown.Loop.Profile.SavedOperatingIntent,
                "unknown cancellation remains visible before Leave or Return and preserves the persisted order and intent");
        }
        using var restarted = new LocalTradingScenario(unknownRoot, profile, clock, automaticScheduling: false);
        var recovery = await restarted.ResumeAsync();
        check(restarted.SyntheticOpenOrders.Count == 1 && restarted.SyntheticPositions.Count == 1
              && restarted.Loop.Profile.SavedOperatingIntent && restarted.SyntheticAiRequestCount == 0
              && !recovery.Succeeded,
            "restart after an unknown exit cancellation reconciles the same local order and never resends entry");
    }

    private static async Task CheckLocalScenarioPlannedUpdate(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Limit");
        var clock = new FakeClock(InitialTime);
        var scenarioRoot = Path.Combine(root, "preserved-profile");
        string? expectedOrderId = null;
        string? expectedPositionId = null;
        using (var scenario = new LocalTradingScenario(scenarioRoot, profile, clock, automaticScheduling: false))
        {
            await scenario.StartAsync(profile);
            await scenario.RunOnceAsync();
            var order = scenario.SyntheticOpenOrders.Single();
            var position = scenario.SyntheticPositions.Single();
            expectedOrderId = order.OrderId;
            expectedPositionId = position.PositionId;
            var requestCount = scenario.SyntheticAiRequestCount;
            var savedOperations = new RecoveryJournalStore(scenario.JournalPath).Load().State.Operations
                .Select(operation => (operation.OperationId, operation.Stage, operation.ActionKind)).ToArray();

            var prepared = await scenario.PrepareForPlannedUpdateAsync();
            var savedProfile = new SettingsStore(scenario.SettingsPath).Load();
            var savedState = new RecoveryJournalStore(scenario.JournalPath).Load().State;
            check(prepared.Succeeded && scenario.Loop.IsExitHeld && !scenario.Loop.IsAdmitting
                  && scenario.HasSavedStart && savedProfile.Profile.SavedOperatingIntent
                  && scenario.SyntheticOpenOrders.Single().OrderId == order.OrderId
                  && scenario.SyntheticPositions.Single().PositionId == position.PositionId
                  && scenario.SyntheticAiRequestCount == requestCount
                  && savedState.Operations.Select(operation => (operation.OperationId, operation.Stage, operation.ActionKind))
                      .SequenceEqual(savedOperations),
                "planned update holds admission without changing Start, cancelling the open order, closing the position, or adding journal operations");

            var resumed = await scenario.ResumeAfterPlannedUpdateAsync();
            check(resumed.Succeeded && !scenario.Loop.IsExitHeld && scenario.Loop.IsAdmitting
                  && scenario.HasSavedStart && scenario.SyntheticOpenOrders.Single().OrderId == order.OrderId
                  && scenario.SyntheticPositions.Single().PositionId == position.PositionId,
                "cancelled planned update reconciles before restoring the saved Start intent and scheduler");

            await scenario.PrepareForPlannedUpdateAsync();
        }

        using var restarted = new LocalTradingScenario(scenarioRoot, profile, clock, automaticScheduling: false);
        var recovery = await restarted.ResumeAsync();
        check(restarted.HasSavedStart && restarted.SyntheticOpenOrders.Count == 1
              && restarted.SyntheticOpenOrders.Single().OrderId == expectedOrderId
              && restarted.SyntheticPositions.Count == 1
              && restarted.SyntheticPositions.Single().PositionId == expectedPositionId
              && restarted.SyntheticAiRequestCount == 0
              && recovery.Succeeded,
            "restart after planned update reads the same open order and position before admitting entries");
    }

    private static async Task CheckLocalScenarioDeadlineWake(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Market");
        profile.Settings.RecoveryCheckSeconds = 3600;
        var clock = new FakeClock(InitialTime);
        var delays = new ControlledDelayQueue();
        using var scenario = new LocalTradingScenario(root, profile, clock, delays.DelayAsync);
        scenario.ReturnNoSetupForNextSearch();
        await scenario.StartAsync(profile);
        await WaitUntilAsync(() => scenario.SyntheticAiRequestCount == 1, "first deadline fixture search");
        await WaitUntilAsync(() => scenario.Loop.State.NextSearchAt.ContainsKey("SOL"),
            "persisted NO_SETUP deadline");
        var retryAt = scenario.Loop.State.NextSearchAt["SOL"];
        await WaitUntilAsync(() => delays.PendingCount > 0, "local scheduler wait registration");
        scenario.ReturnNoSetupForNextSearch();
        clock.UtcNow = retryAt;
        delays.ReleaseNext();
        await WaitUntilAsync(() => scenario.SyntheticAiRequestCount == 2, "persisted search deadline wake");
        check(retryAt == InitialTime.AddMinutes(profile.Settings.NewSetupRetryMinutes)
              && scenario.Loop.State.NextSearchAt["SOL"] > retryAt,
            "local scheduler sleeps on the saved deadline and wakes after controlled-clock advancement");
    }

    private static async Task CheckLocalScenarioFallbackDeadline(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Market");
        profile.Settings.RecoveryCheckSeconds = 60;
        profile.SavedOperatingIntent = true;
        var settingsStore = new SettingsStore(Path.Combine(root, "profile", "settings.json"));
        var saved = settingsStore.Save(profile);
        check(saved.Succeeded, "fallback scheduler fixture begins from a durably saved Start intent");
        if (!saved.Succeeded)
            return;

        var clock = new FakeClock(InitialTime);
        var delays = new ControlledDelayQueue();
        using var scenario = new LocalTradingScenario(root, profile, clock, delays.DelayAsync);
        await WaitUntilAsync(() => delays.PendingCount > 0, "initial fallback deadline wait");
        var firstFallbackAt = InitialTime.AddSeconds(profile.Settings.RecoveryCheckSeconds);
        check(scenario.ScheduledRunCount == 0 && scenario.Loop.State.NextSearchAt.Count == 0
              && scenario.Loop.State.LimitReassessments.Count == 0
              && delays.Durations.First() == TimeSpan.FromSeconds(profile.Settings.RecoveryCheckSeconds),
            "saved Start with no persisted search or Limit deadline waits on one recovery fallback without an external wake");

        delays.ReleaseNext();
        await WaitUntilAsync(() => delays.PendingCount > 0, "fallback rearmed while the fake clock remains early");
        check(scenario.ScheduledRunCount == 0 && delays.Durations.Last()
              == TimeSpan.FromSeconds(profile.Settings.RecoveryCheckSeconds),
            "an elapsed test delay cannot run a fallback before the injected clock reaches its fixed deadline");

        clock.UtcNow = firstFallbackAt;
        delays.ReleaseNext();
        await WaitUntilAsync(() => scenario.ScheduledRunCount == 1 && delays.PendingCount > 0,
            "first fallback tick and next retry wait");
        check(scenario.SyntheticAiRequestCount == 1 && scenario.SyntheticPositions.Count == 1
              && delays.Durations.Last() == TimeSpan.FromSeconds(profile.Settings.RecoveryCheckSeconds),
            "the fallback runs once at its fixed deadline and advances the next recovery retry by one interval");

        clock.UtcNow = firstFallbackAt.AddSeconds(profile.Settings.RecoveryCheckSeconds);
        delays.ReleaseNext();
        await WaitUntilAsync(() => scenario.ScheduledRunCount == 2 && delays.PendingCount > 0,
            "second fallback tick without a manual signal");
        check(scenario.ScheduledRunCount == 2 && scenario.SyntheticPositions.Count == 1,
            "the next fallback is consumed on schedule without spinning or duplicating the synthetic position");
    }

    private static async Task CheckLocalScenarioHeldDeadline(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Market");
        profile.Settings.RecoveryCheckSeconds = 3600;
        var clock = new FakeClock(InitialTime);
        var delays = new ControlledDelayQueue();
        using var scenario = new LocalTradingScenario(root, profile, clock, delays.DelayAsync);
        scenario.ReturnNoSetupForNextSearch();
        await scenario.StartAsync(profile);
        await WaitUntilAsync(() => scenario.SyntheticAiRequestCount == 1, "held-deadline fixture first search");
        await WaitUntilAsync(() => scenario.Loop.State.NextSearchAt.ContainsKey("SOL"),
            "held-deadline fixture saved search deadline");
        var retryAt = scenario.Loop.State.NextSearchAt["SOL"];
        await WaitUntilAsync(() => delays.PendingCount > 0, "held-deadline initial scheduler wait");

        var held = await scenario.HoldAndReconcileForExitAsync();
        var runsWhileHeld = scenario.ScheduledRunCount;
        clock.UtcNow = retryAt;
        await Task.Delay(40).ConfigureAwait(false);
        check(held.IsKnown && scenario.Loop.IsExitHeld
              && scenario.SyntheticAiRequestCount == 1
              && scenario.ScheduledRunCount == runsWhileHeld,
            "a due search deadline is ignored during exit hold without a scheduler spin");

        var resumed = await scenario.ResumeAfterExitAsync();
        await WaitUntilAsync(() => scenario.SyntheticAiRequestCount == 2,
            "held search deadline wakes after Return");
        check(resumed.Succeeded && !scenario.Loop.IsExitHeld
              && scenario.ScheduledRunCount > runsWhileHeld
              && scenario.SyntheticAiRequestCount == 2,
            "Return wakes the same scheduler and consumes the due search after reconciliation");
    }

    private static async Task CheckLocalScenarioLimitDeadline(Action<bool, string> check, string root)
    {
        var profile = LocalScenarioProfile("Limit");
        profile.Settings.LimitReassessmentMinutes = 30;
        profile.Settings.LimitOrderCheckMinutes = 240;
        profile.Settings.PositionCheckMinutes = 240;
        profile.Settings.PartialFillCancelMinutes = 240;
        profile.Settings.RecoveryCheckSeconds = 7200;
        var clock = new FakeClock(InitialTime);
        var delays = new ControlledDelayQueue();
        using var scenario = new LocalTradingScenario(root, profile, clock, delays.DelayAsync);
        await scenario.StartAsync(profile);
        await WaitUntilAsync(() => scenario.SyntheticOpenOrders.Count == 1
            && scenario.Loop.State.LimitReassessments.Count == 1, "scheduled Limit fixture entry");
        var reassessment = scenario.Loop.State.LimitReassessments.Values.Single();
        await WaitUntilAsync(() => delays.PendingCount > 0, "Limit scheduler deadline wait");
        var firstDelay = delays.Durations.First();
        check(firstDelay == reassessment.NextReassessmentAt - clock.UtcNow,
            $"scheduler uses the persisted Limit reassessment deadline (delay={firstDelay}; due={reassessment.NextReassessmentAt:O})");

        clock.UtcNow = reassessment.NextReassessmentAt;
        delays.ReleaseNext();
        await WaitUntilAsync(() => scenario.Loop.State.LimitReassessments.Values.Single().NextReassessmentAt
            > reassessment.NextReassessmentAt, "due Limit reassessment is consumed");
        check(scenario.ScheduledRunCount >= 2,
            "the saved Limit deadline wakes the scheduler and is rescheduled after one bounded cycle");
    }

    private static SettingsProfile LocalScenarioProfile(string entryType) => new()
    {
        SelectedMode = "TrueNorth AI setups",
        Settings = new AppSettings
        {
            Assets = ["SOL"],
            EntryType = entryType,
            MarginBalancePercent = 10m,
            Leverage = 1m,
            PositionCheckMinutes = 5,
            LimitOrderCheckMinutes = 1,
            PartialFillCancelMinutes = 60,
            NewSetupRetryMinutes = 240,
            LimitReassessmentMinutes = 60,
            RecoveryCheckSeconds = 60,
            NotificationsEnabled = true,
            OpeningNotifications = true,
            ClosingNotifications = true,
            OrderChangeNotifications = true,
            ErrorNotifications = true,
            RecoveryNotifications = true,
            PreventSleep = true
        }
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(5).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Timed out waiting for " + description + ".");
    }

    private sealed class ControlledDelayQueue
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _waiting = new();
        private readonly ConcurrentQueue<TimeSpan> _durations = new();

        internal int PendingCount => _waiting.Count(item => !item.Task.IsCompleted);
        internal IReadOnlyList<TimeSpan> Durations => _durations.ToArray();

        internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _durations.Enqueue(delay);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _waiting.Enqueue(completion);
            return completion.Task;
        }

        internal void ReleaseNext()
        {
            while (_waiting.TryDequeue(out var completion))
                if (!completion.Task.IsCompleted)
                {
                    completion.TrySetResult();
                    return;
                }
            throw new InvalidOperationException("No controlled scheduler delay is pending.");
        }
    }

    internal static void CheckMainWindow(
        Action<bool, string> check,
        Application application,
        string fixtureRoot,
        string artifactDirectory)
    {
        CheckLocalScenarioWindow(check, application, fixtureRoot, artifactDirectory);
        CheckLocalScenarioModeSaveFailures(check, application, fixtureRoot);
        CheckExitConfirmationWindows(check, application);
    }

    private static void CheckLocalScenarioModeSaveFailures(
        Action<bool, string> check,
        Application application,
        string fixtureRoot)
    {
        var profile = LocalScenarioProfile("Limit");
        var ordinaryPath = Path.Combine(fixtureRoot, "local-mode-ordinary-failure", "settings.json");
        var ordinaryBase = new SettingsStore(ordinaryPath);
        check(ordinaryBase.Save(profile).Succeeded, "ordinary mode-write failure fixture is prepared");
        var ordinaryBytes = File.ReadAllBytes(ordinaryPath);
        var ordinaryStore = new SettingsStore(ordinaryPath,
            commitOverride: (_, _, _) => throw new IOException("fixed ordinary profile write failure"));
        using (var local = new LocalTradingScenario(Path.Combine(fixtureRoot, "local-mode-ordinary-failure", "local"),
                   profile, new FakeClock(InitialTime), automaticScheduling: false))
        {
            check(local.StartAsync(profile).GetAwaiter().GetResult().Succeeded,
                "local mode-write failure fixture starts from its saved local profile");
            using var wake = new WakeProtectionController(false, _ => true);
            var window = new MainWindow(ordinaryStore, ordinaryStore.Load(), wake,
                new TrueNorthSessionChecks.RecordingSessionService(), new SyntheticAiAnalysisSource(),
                new AiTestReportStore(Path.Combine(fixtureRoot, "local-mode-ordinary-failure", "reports")),
                localTradingScenario: local);
            try
            {
                window.Show();
                SelectMode(application, window, 0);
                PumpUntil(() => local.Loop.Profile.SelectedMode == "Test"
                    && Get<TextBlock>(window, "OverviewModeStatusText").Visibility == Visibility.Visible,
                    application, "ordinary profile mode-save failure");
                check(File.ReadAllBytes(ordinaryPath).SequenceEqual(ordinaryBytes)
                      && ordinaryStore.Load().Profile.SelectedMode == "TrueNorth AI setups"
                      && !local.Loop.Profile.SavedOperatingIntent && !local.Loop.IsAdmitting
                      && !Get<Button>(window, "LocalScenarioStartButton").IsEnabled
                      && local.Snapshot().HasOwnedMonitoring == false,
                    "ordinary mode-save failure leaves its bytes unchanged and the locally saved Test+Stop profile cannot admit entries");
            }
            finally
            {
                window.AllowApplicationClose();
                window.Close();
            }
        }

        var localFailureRoot = Path.Combine(fixtureRoot, "local-mode-local-failure");
        var localOrdinaryPath = Path.Combine(localFailureRoot, "app-settings.json");
        var localOrdinaryStore = new SettingsStore(localOrdinaryPath);
        check(localOrdinaryStore.Save(profile).Succeeded, "local mode-write failure app profile is prepared");
        var localOrdinaryBytes = File.ReadAllBytes(localOrdinaryPath);
        var allowLocalWrites = true;
        using (var local = new LocalTradingScenario(Path.Combine(localFailureRoot, "local"), profile,
                   new FakeClock(InitialTime), automaticScheduling: false,
                   profileCommitOverride: (temporary, target, _) =>
                   {
                       if (!allowLocalWrites)
                           throw new IOException("fixed local profile write failure");
                       File.Move(temporary, target, overwrite: true);
                   }))
        {
            check(local.StartAsync(profile).GetAwaiter().GetResult().Succeeded,
                "local store failure fixture starts from its saved local profile");
            allowLocalWrites = false;
            using var wake = new WakeProtectionController(false, _ => true);
            var window = new MainWindow(localOrdinaryStore, localOrdinaryStore.Load(), wake,
                new TrueNorthSessionChecks.RecordingSessionService(), new SyntheticAiAnalysisSource(),
                new AiTestReportStore(Path.Combine(localFailureRoot, "reports")), localTradingScenario: local);
            try
            {
                window.Show();
                SelectMode(application, window, 0);
                PumpUntil(() => local.Loop.Profile.SelectedMode == "Test"
                    && Get<TextBlock>(window, "OverviewModeStatusText").Visibility == Visibility.Visible,
                    application, "local profile mode-save failure");
                check(File.ReadAllBytes(localOrdinaryPath).SequenceEqual(localOrdinaryBytes)
                      && localOrdinaryStore.Load().Profile.SelectedMode == "TrueNorth AI setups"
                      && !local.Loop.Profile.SavedOperatingIntent && !local.Loop.IsAdmitting
                      && !Get<Button>(window, "LocalScenarioStartButton").IsEnabled,
                    "local mode-save failure prevents the ordinary profile update and keeps current admission closed");
                allowLocalWrites = true;
                local.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                window.AllowApplicationClose();
                window.Close();
            }
        }
    }

    private static void CheckLocalScenarioWindow(
        Action<bool, string> check,
        Application application,
        string fixtureRoot,
        string artifactDirectory)
    {
        var root = Path.Combine(fixtureRoot, "trading-loop-local-window");
        var profile = LocalScenarioProfile("Limit");
        profile.Settings.CandidateSelectionMethod = "NetProfitAtTakeProfit";
        profile.Settings.PreventSleep = true;
        var settingsStore = new SettingsStore(Path.Combine(root, "app-settings.json"));
        check(settingsStore.Save(profile).Succeeded, "WPF local-loop fixture saves a complete application profile");
        var ordinaryProfileBytes = File.ReadAllBytes(settingsStore.PathName);
        var restartRoot = Path.Combine(root, "local-restart-intent");
        using (var beforeRestart = new LocalTradingScenario(restartRoot, profile,
                   new FakeClock(InitialTime), automaticScheduling: false))
            check(beforeRestart.StartAsync(profile).GetAwaiter().GetResult().Succeeded,
                "WPF fixture writes Start intent to the dedicated local profile");
        using (var afterRestart = new LocalTradingScenario(restartRoot, profile,
                   new FakeClock(InitialTime), automaticScheduling: false))
            check(afterRestart.HasSavedStart
                  && afterRestart.SettingsPath.StartsWith(Path.Combine(restartRoot, "profile"), StringComparison.OrdinalIgnoreCase),
                "local restart restores saved intent from its dedicated profile");
        var clock = new FakeClock(InitialTime);
        var localScenario = new LocalTradingScenario(Path.Combine(root, "local-scenario"), profile,
            clock, automaticScheduling: false);
        var powerRequests = new List<bool>();
        using var wakeProtection = new WakeProtectionController(true, active =>
        {
            powerRequests.Add(active);
            return true;
        });
        var session = new TrueNorthSessionChecks.RecordingSessionService();
        var reportRoot = Path.Combine(root, "ai-test-reports");
        var window = new MainWindow(settingsStore, settingsStore.Load(), wakeProtection, session,
            new SyntheticAiAnalysisSource(), new AiTestReportStore(reportRoot),
            localTradingScenario: localScenario)
        {
            Width = 760,
            Height = 560
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher(application.Dispatcher);
            check(!wakeProtection.IsRequestHeld && powerRequests.Count == 0
                  && localScenario.LocalNotifications.Count == 0,
                "idle local scenario sends no wake request or notification");
            var realStart = Get<Button>(window, "RealStartButton");
            var localStart = Get<Button>(window, "LocalScenarioStartButton");
            var stop = Get<Button>(window, "LocalScenarioStopButton");
            var closePosition = Get<Button>(window, "LocalScenarioClosePositionButton");
            var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
            var selectionMethod = Get<ComboBox>(window, "CandidateSelectionMethodComboBox");
            var signIn = Get<Button>(window, "OpenTrueNorthButton");
            var checkSession = Get<Button>(window, "CheckTrueNorthSessionButton");
            check(Get<TextBlock>(window, "LocalScenarioPauseText").Text.Contains("намерение Stop", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioPauseText").Text.Contains("нет", StringComparison.OrdinalIgnoreCase)
                  && Get<TextBlock>(window, "LocalScenarioActivityText").Text.Contains("остановлен", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "OperatingStatusText").Text.Contains("ОСТАНОВЛЕН", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "CurrentModeText").Text == "TrueNorth AI setups"
                  && selectionMethod.SelectedIndex == 1
                  && localScenario.SettingsPath.StartsWith(Path.Combine(localScenario.RootPath, "profile"), StringComparison.OrdinalIgnoreCase),
                "overview separates the selected mode from the labelled, isolated local scenario, preserving ranking settings");
            var settingsNavigation = Get<ListBox>(window, "NavigationList");
            settingsNavigation.SelectedIndex = 1;
            window.UpdateLayout();
            PumpDispatcher(application.Dispatcher);
            selectionMethod.SelectedIndex = 0;
            InvokeAutomation(Get<Button>(window, "SaveSettingsButton"));
            PumpUntil(() => settingsStore.Load().Profile.Settings.CandidateSelectionMethod == "AssetOrder"
                && localScenario.Loop.Profile.Settings.CandidateSelectionMethod == "AssetOrder", application,
                "asset-order selection persisted to both profiles");
            check(selectionMethod.SelectedIndex == 0
                  && settingsStore.Load().Profile.Settings.CandidateSelectionMethod == "AssetOrder"
                  && localScenario.Loop.Profile.Settings.CandidateSelectionMethod == "AssetOrder",
                "settings save the legacy asset-order method to both ordinary and local profiles");
            selectionMethod.SelectedIndex = 1;
            InvokeAutomation(Get<Button>(window, "SaveSettingsButton"));
            PumpUntil(() => settingsStore.Load().Profile.Settings.CandidateSelectionMethod == "NetProfitAtTakeProfit"
                && localScenario.Loop.Profile.Settings.CandidateSelectionMethod == "NetProfitAtTakeProfit", application,
                "ranking selection persisted to both profiles");
            RenderWindow(window, Path.Combine(artifactDirectory, "candidate-selection-760x560.png"));
            check(selectionMethod.SelectedIndex == 1
                  && (selectionMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Чистая прибыль"
                  && Get<TextBlock>(window, "CandidateSelectionMethodHelpText").Text.Contains("TP", StringComparison.Ordinal)
                  && localScenario.Loop.Profile.Settings.CandidateSelectionMethod == "NetProfitAtTakeProfit",
                "the ranking choice survives saving and is visible in the settings screen");
            ordinaryProfileBytes = File.ReadAllBytes(settingsStore.PathName);
            settingsNavigation.SelectedIndex = 0;
            window.UpdateLayout();
            PumpDispatcher(application.Dispatcher);
            var review = Get<Expander>(window, "LocalScenarioProfileReview");
            check(AutomationProperties.GetName(review).Length > 0
                  && Get<TextBlock>(window, "LocalScenarioProfileReviewText").Text.Contains("SOL", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioProfileReviewText").Text.Contains("Limit", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioProfileReviewText").Text.Contains("10%", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioProfileReviewText").Text.Contains("×1", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioProfileReviewText").Text.Contains("чистый результат на TP", StringComparison.Ordinal),
                "accessible profile review shows saved selection method, mode, assets, entry type, size and leverage before local Start");
            check(realStart.IsTabStop && localStart.IsTabStop && stop.IsTabStop
                  && AutomationProperties.GetName(realStart).Length > 0
                  && AutomationProperties.GetName(localStart).Length > 0
                  && localStart.IsEnabled && !stop.IsEnabled,
                "local controls have keyboard stops and accessible names, with Start enabled from saved settings");
            Keyboard.Focus(realStart);
            realStart.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            PumpDispatcher(application.Dispatcher);
            check(Keyboard.FocusedElement == localStart,
                "keyboard focus moves from the unavailable real Start to the explicit local Start");
            var localStartBounds = localStart.TransformToAncestor(scroll)
                .TransformBounds(new Rect(0, 0, localStart.ActualWidth, localStart.ActualHeight));
            check(localStart.IsVisible && localStartBounds.Top >= 0 && localStartBounds.Bottom <= scroll.ViewportHeight,
                "local Start remains readable in the 760x560 overview without scrolling");
            var signInBounds = signIn.TransformToAncestor(scroll)
                .TransformBounds(new Rect(0, 0, signIn.ActualWidth, signIn.ActualHeight));
            var sessionCheckBounds = checkSession.TransformToAncestor(scroll)
                .TransformBounds(new Rect(0, 0, checkSession.ActualWidth, checkSession.ActualHeight));
            check(signIn.IsVisible && checkSession.IsVisible
                  && signInBounds.Top >= 0 && signInBounds.Bottom <= scroll.ViewportHeight
                  && sessionCheckBounds.Top >= 0 && sessionCheckBounds.Bottom <= scroll.ViewportHeight,
                "existing sign-in and session controls remain visible with local Start at 760x560");
            review.IsExpanded = true;
            window.UpdateLayout();
            PumpDispatcher(application.Dispatcher);
            localStartBounds = localStart.TransformToAncestor(scroll)
                .TransformBounds(new Rect(0, 0, localStart.ActualWidth, localStart.ActualHeight));
            check(localStart.IsVisible && localStartBounds.Top >= 0 && localStartBounds.Bottom <= scroll.ViewportHeight,
                "expanding the saved profile review keeps local Start readable at 760x560");
            RenderWindow(window, Path.Combine(artifactDirectory, "block-c-local-loop-review-760x560.png"));
            review.IsExpanded = false;
            window.UpdateLayout();
            RenderWindow(window, Path.Combine(artifactDirectory, "block-c-local-loop-760x560.png"));

            InvokeAutomation(realStart);
            PumpDispatcher(application.Dispatcher);
            check(Get<TextBlock>(window, "LocalScenarioActionStatusText").Text.Contains("недоступен", StringComparison.Ordinal)
                  && localScenario.SyntheticAiRequestCount == 0 && !localScenario.HasSavedStart,
                $"real Start explains that live sources are unavailable and never reaches an adapter (status={Get<TextBlock>(window, "LocalScenarioActionStatusText").Text}; ai={localScenario.SyntheticAiRequestCount}; saved={localScenario.HasSavedStart})");
            InvokeAutomation(localStart);
            PumpUntil(() => localScenario.HasSavedStart, application,
                "saved local Start intent");
            check(File.ReadAllBytes(settingsStore.PathName).SequenceEqual(ordinaryProfileBytes)
                  && !settingsStore.Load().Profile.SavedOperatingIntent,
                "local Start writes only the dedicated local profile and leaves the ordinary profile byte-for-byte unchanged");
            var first = localScenario.RunOnceAsync().GetAwaiter().GetResult();
            PumpDispatcher(application.Dispatcher);
            check(first.Succeeded && localScenario.SyntheticPositions.Count == 1
                  && localScenario.SyntheticOpenOrders.Count == 1 && wakeProtection.IsRequestHeld
                  && powerRequests.Contains(true),
                "local Start runs the same synthetic cycle and requests wake protection for its owned position");
            check(Get<TextBlock>(window, "LocalScenarioStatusText").Text.Contains("Способ: чистый результат на TP", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioStatusText").Text.Contains("маршрут", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioStatusText").Text.Contains("USDC", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioStatusText").Text.Contains("чистый", StringComparison.Ordinal),
                "local-cycle status exposes the ranking calculation, selected route, currency and net estimate");
            check(Get<Button>(window, "LocalScenarioClosePositionButton").Visibility == Visibility.Visible
                  && Get<TextBlock>(window, "OperatingStatusText").Text.Contains("СОПРОВОЖДЕНИЕ", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "LocalScenarioActivityText").Text.Contains("Сопровождение", StringComparison.Ordinal),
                "overview reports actual position monitoring and exposes only the synthetic close control");
            RenderWindow(window, Path.Combine(artifactDirectory, "block-c-local-loop-monitoring-760x560.png"));

            InvokeAutomation(stop);
            PumpUntil(() => !localScenario.HasSavedStart && !localScenario.Loop.IsAdmitting
                && localScenario.SyntheticOpenOrders.Count == 0, application,
                "saved local Stop and exact remainder cancellation");
            check(File.ReadAllBytes(settingsStore.PathName).SequenceEqual(ordinaryProfileBytes)
                  && !settingsStore.Load().Profile.SavedOperatingIntent,
                "local Stop writes only the dedicated local profile and leaves the ordinary profile byte-for-byte unchanged");
            check(!localScenario.Loop.IsAdmitting && localScenario.SyntheticOpenOrders.Count == 0
                  && localScenario.SyntheticPositions.Count == 1 && wakeProtection.IsRequestHeld,
                "Stop persists closed admission, cancels the exact remainder, and keeps wake protection for the position");

            SelectMode(application, window, 0);
            PumpUntil(() => settingsStore.Load().Profile.SelectedMode == "Test"
                && localScenario.Loop.Profile.SelectedMode == "Test"
                && Get<TextBlock>(window, "SettingsStatusText").Text.Contains("Режим сохранён", StringComparison.Ordinal), application,
                "coherent Test mode transition");
            check(!settingsStore.Load().Profile.SavedOperatingIntent && !localScenario.Loop.Profile.SavedOperatingIntent
                  && localScenario.SyntheticPositions.Count == 1 && localScenario.Snapshot().HasOwnedMonitoring,
                "Test persists mode and Stop while the already-open owned position remains monitored");

            Get<ListBox>(window, "NavigationList").SelectedIndex = 3;
            PumpDispatcher(application.Dispatcher);
            var aiTestStart = Get<Button>(window, "StartAiTestButton");
            check(aiTestStart.IsEnabled && session.OpenCalls == 0 && session.CheckCalls == 0,
                "analysis-only AI Test remains available without opening or checking a live session");
            InvokeAutomation(aiTestStart);
            PumpUntil(() => Get<Button>(window, "OpenAiReportButton").IsEnabled,
                application, "analysis-only test batch completion");
            check(Get<TextBlock>(window, "AiTestStatusText").Text == "Синтетический пакет завершён."
                  && localScenario.SyntheticPositions.Count == 1 && wakeProtection.IsRequestHeld
                  && session.OpenCalls == 0 && session.CheckCalls == 0,
                "AI Test completes while the stopped local loop continues protecting its synthetic position");

            Get<ListBox>(window, "NavigationList").SelectedIndex = 0;
            PumpDispatcher(application.Dispatcher);
            window.Close();
            PumpDispatcher(application.Dispatcher);
            check(!window.IsVisible && localScenario.Snapshot().HasOwnedMonitoring
                  && !localScenario.Loop.Profile.SavedOperatingIntent && wakeProtection.IsRequestHeld,
                "closing the window hides it to tray without stopping saved position monitoring");
            window.Show();
            PumpDispatcher(application.Dispatcher);
            InvokeAutomation(closePosition);
            var monitored = localScenario.ManagedOrders.Single(order => order.PositionQuantity > 0m);
            clock.UtcNow = monitored.NextManagementCheckAt
                ?? throw new InvalidOperationException("Synthetic position has no scheduled management check.");
            localScenario.RunOnceAsync().GetAwaiter().GetResult();
            PumpUntil(() => localScenario.SyntheticPositions.Count == 0 && !wakeProtection.IsRequestHeld,
                application, "final synthetic position close and wake release");
            check(powerRequests.Contains(true) && powerRequests[^1] == false
                  && localScenario.LocalNotifications.Count > 0,
                "wake request is released after final exposure closes; notifications only followed actual scenario events");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            localScenario.Dispose();
        }

        var blockedParent = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedParent, "fixed local startup fixture");
        var unavailable = LocalTradingScenario.TryCreate(Path.Combine(blockedParent, "scenario"), profile,
            out var unavailableReason, new FakeClock(InitialTime), automaticScheduling: false);
        check(unavailable is null && unavailableReason?.Contains("IOException", StringComparison.Ordinal) == true,
            "a local scenario storage failure is returned as an optional fail-closed result");
        var shellStore = new SettingsStore(Path.Combine(root, "unavailable-shell-settings.json"));
        check(shellStore.Save(profile).Succeeded, "fail-closed WPF shell fixture saves ordinary settings");
        using var shellWake = new WakeProtectionController(true, _ => true);
        var unavailableWindow = new MainWindow(shellStore, shellStore.Load(), shellWake,
            new TrueNorthSessionChecks.RecordingSessionService(), new SyntheticAiAnalysisSource(),
            new AiTestReportStore(Path.Combine(root, "unavailable-ai-reports")),
            localScenarioUnavailableReason: unavailableReason)
        {
            Width = 760,
            Height = 560
        };
        unavailableWindow.Show();
        PumpDispatcher(application.Dispatcher);
        check(unavailableWindow.IsVisible
              && !Get<Button>(unavailableWindow, "LocalScenarioStartButton").IsEnabled
              && Get<TextBlock>(unavailableWindow, "LocalScenarioPauseText").Text.Contains("IOException", StringComparison.Ordinal)
              && Get<TextBlock>(unavailableWindow, "OperatingStatusText").Text.Contains("ОТКЛЮЧЁН", StringComparison.Ordinal),
            "local storage failure leaves the normal WPF shell open, explains the cause, and disables local Start");
        unavailableWindow.AllowApplicationClose();
        unavailableWindow.Close();
        PumpDispatcher(application.Dispatcher);
    }

    private static void CheckExitConfirmationWindows(Action<bool, string> check, Application application)
    {
        var entries = new LocalScenarioExitSnapshot(true,
            [new("intent-1", "order-1", "SOL", "SOL-PERP", 1m),
             new("intent-2", "order-2", "BTC", "BTC-PERP", 2m)],
            "Подтверждено открытых программных заявок: 2.", []);
        var cancel = InvokeExitDialog(application, entries, "CancelEntriesButton");
        check(cancel.Action == ExitConfirmationAction.CancelEntries
              && cancel.FindName("ExitEntriesText") is TextBlock countText
              && countText.Text.Contains("2", StringComparison.Ordinal)
              && Get<Button>(cancel, "CancelEntriesButton").Visibility == Visibility.Visible,
            "exit dialog presents the exact confirmed count and invokes cancellation through its accessible button");

        var unknown = new LocalScenarioExitSnapshot(false, [],
            "Количество заявок не подтверждено.", ["SOL · order-1: исход неизвестен; требуется сверка."]);
        var leave = InvokeExitDialog(application, unknown, "LeaveButton");
        check(leave.Action == ExitConfirmationAction.Leave
              && Get<TextBlock>(leave, "ExitEntriesText").Text.Contains("неизвестно", StringComparison.OrdinalIgnoreCase)
              && Get<Button>(leave, "CancelEntriesButton").Visibility == Visibility.Collapsed
              && Get<TextBlock>(leave, "ExitResultsText").Text.Contains("исход неизвестен", StringComparison.Ordinal),
            "unknown cancellation is shown before explicit Leave and disables unsafe exact-ID cancellation");

        var returned = InvokeExitDialog(application,
            new LocalScenarioExitSnapshot(true, [], "Подтверждено открытых программных заявок: 0.", []),
            "ReturnButton");
        check(returned.Action == ExitConfirmationAction.Return
              && Get<Button>(returned, "CancelEntriesButton").Visibility == Visibility.Collapsed,
            "Return is a distinct accessible exit outcome when no opening requests remain");
    }

    private static ExitConfirmationWindow InvokeExitDialog(
        Application application,
        LocalScenarioExitSnapshot snapshot,
        string buttonName)
    {
        var dialog = new ExitConfirmationWindow(snapshot);
        application.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var button = Get<Button>(dialog, buttonName);
            if (!button.IsEnabled || button.Visibility != Visibility.Visible
                || AutomationProperties.GetName(button).Length == 0)
                throw new InvalidOperationException($"Exit action {buttonName} is not accessible.");
            InvokeAutomation(button);
        }));
        dialog.ShowDialog();
        return dialog;
    }

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static void SelectMode(Application application, Window window, int index)
    {
        application.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => Get<ListBox>(window, "ModeList").SelectedIndex = index));
        PumpDispatcher(application.Dispatcher);
    }

    private static void InvokeAutomation(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("Button does not expose the Invoke automation pattern.");
        invoke.Invoke();
    }

    private static void PumpUntil(Func<bool> condition, Application application, string description)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
                return;
            PumpDispatcher(application.Dispatcher);
            Thread.Sleep(2);
        }
        throw new InvalidOperationException("Timed out waiting for " + description + ".");
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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

        using var ranked = new Harness(Path.Combine(root, "candidate-ranking"), ["SOL"],
            selectionMethod: "NetProfitAtTakeProfit");
        ranked.Adapter.EntryOutcome = RecoveryOperationOutcome.Unknown;
        ranked.Ai.Handler = request => Task.FromResult(Setup(request, ranked.Clock.UtcNow));
        await ranked.Loop.StartAsync();
        await ranked.Loop.RunOnceAsync();
        await ranked.RestartAsync();
        await ranked.Loop.RunOnceAsync();
        check(ranked.Adapter.SubmitCount == 1 && ranked.Ai.Requests.Count == 1
              && ranked.Recovery.State.Operations.Any(operation => operation.Stage == RecoveryOperationStage.Unknown),
            "candidate ranking also reconciles the saved IntentId after restart without repeating an unknown entry");
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

        using var cancelHarness = new Harness(Path.Combine(root, "cancel-begin-fence"), ["SOL"], entryType: "Limit");
        var limit = await OpenLimitAsync(cancelHarness);
        var fenceCalls = 0;
        var cancel = await cancelHarness.Orders.CancelRemainderAsync(limit.IntentId, savedOperatingIntent: true,
            CancellationToken.None, transferAdmissionFence: _ =>
            {
                fenceCalls++;
                return false;
            });
        var cancelOperation = cancelHarness.Recovery.State.Operations.Last(operation =>
            operation.ActionKind == RecoveryActionKind.CancelOwnedOrder);
        var afterFence = cancelHarness.Orders.Orders.Single(item => item.IntentId == limit.IntentId);
        check(!cancel.Succeeded && fenceCalls == 1 && cancelHarness.Adapter.CancelCount == 0
              && afterFence.EntryTerminalState == ManagedEntryTerminalState.Open
              && afterFence.PendingOperationId is null
              && cancelOperation.Stage == RecoveryOperationStage.Abandoned,
            "a refused final transfer fence abandons the prepared automatic cancel before its adapter call");
    }

    private static async Task CheckLimitReassessmentOutcomes(Action<bool, string> check, string root)
    {
        using (var keep = new Harness(Path.Combine(root, "keep"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(keep);
            var before = keep.Loop.State.LimitReassessments[order.IntentId];
            keep.Clock.UtcNow = before.NextReassessmentAt;
            keep.Ai.LimitHandler = context => Task.FromResult(LimitResult(context, LimitReassessmentOutcome.Keep));
            var result = await keep.Loop.RunOnceAsync();
            var after = keep.Loop.State.LimitReassessments[order.IntentId];
            check(result.Succeeded && after.EntryOrderId == order.EntryOrderId
                  && after.NextReassessmentAt == keep.Clock.UtcNow.AddMinutes(before.IntervalMinutes)
                  && keep.Orders.Orders.Single(item => item.IntentId == order.IntentId).NextManagementCheckAt
                      == keep.Clock.UtcNow.AddMinutes(keep.Profile.Settings.LimitOrderCheckMinutes)
                  && keep.Adapter.CancelCount == 0 && keep.Adapter.SubmitCount == 1
                  && keep.Ai.LimitReassessments.Single().EntryOrderId == order.EntryOrderId
                  && keep.Ai.LimitReassessments.Single().Request.Id == before.Request.Id
                  && keep.Ai.LimitReassessments.Single().OriginResponse == before.OriginResponse,
                "Keep preserves the exact Limit and its independent 009 check while scheduling the next reassessment");
        }

        using (var invalid = new Harness(Path.Combine(root, "invalid"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(invalid);
            invalid.Clock.UtcNow = invalid.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            invalid.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.InvalidNoReplacement, reason: "Синтетический сетап утратил актуальность."));
            var result = await invalid.Loop.RunOnceAsync();
            var savedOrder = invalid.Orders.Orders.Single(item => item.IntentId == order.IntentId);
            var cancelIndex = invalid.Adapter.Trace.FindIndex(item => item == "cancel:" + order.EntryOrderId);
            check(result.Succeeded && invalid.Adapter.CancelCount == 1 && invalid.Adapter.SubmitCount == 1
                  && savedOrder.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && invalid.Loop.State.LimitReassessments.Count == 0
                  && cancelIndex > 0
                  && invalid.Adapter.Trace.Take(cancelIndex).Any(item => item.StartsWith("read:0:", StringComparison.Ordinal))
                  && invalid.Adapter.Trace.Skip(cancelIndex + 1)
                      .Any(item => item.StartsWith("read:1:", StringComparison.Ordinal)),
                "InvalidNoReplacement cancels only the linked order between fresh pre- and post-action reads");
        }

        using (var replacement = new Harness(Path.Combine(root, "replacement"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(replacement);
            replacement.Clock.UtcNow = replacement.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            replacement.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.Replacement,
                ReplacementSetup(context.Request, replacement.Clock.UtcNow)));
            var result = await replacement.Loop.RunOnceAsync();
            var newOrder = replacement.Orders.Orders.SingleOrDefault(item => item.IntentId != order.IntentId
                && ManagedOrderState.BlocksNewEntry(item));
            var cancelIndex = replacement.Adapter.Trace.FindIndex(item => item == "cancel:" + order.EntryOrderId);
            var entryIndex = replacement.Adapter.Trace.FindIndex(item => item.StartsWith("entry:", StringComparison.Ordinal)
                && item != "entry:" + order.IntentId);
            check(newOrder is not null && result.Succeeded && replacement.Adapter.CancelCount == 1
                  && replacement.Adapter.SubmitCount == 2
                  && newOrder.EntryType == "Limit" && newOrder.LimitPrice == 50m
                  && newOrder.TakeProfit == 56m && newOrder.StopLoss == 45m
                  && replacement.Orders.Orders.Single(item => item.IntentId == order.IntentId).EntryTerminalState
                      == ManagedEntryTerminalState.Cancelled
                  && replacement.Loop.State.LimitReassessments.ContainsKey(newOrder.IntentId)
                  && replacement.Adapter.PreparedEntrySavedBeforeEntryTransfer
                  && cancelIndex >= 0 && entryIndex > cancelIndex,
                $"a complete replacement is sent after confirmed cancellation (status={result.Status}; submits={replacement.Adapter.SubmitCount}; cancel={replacement.Adapter.CancelCount}; loop={replacement.Loop.State.LastStatus})");
        }

        using (var unclear = new Harness(Path.Combine(root, "inconclusive"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(unclear);
            unclear.Clock.UtcNow = unclear.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            unclear.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.Inconclusive, reason: "Синтетические данные не позволяют решить."));
            var first = await unclear.Loop.RunOnceAsync();
            var due = unclear.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            await unclear.Loop.RunOnceAsync();
            check(!first.Succeeded && due == unclear.Clock.UtcNow.AddMinutes(240)
                  && unclear.Loop.State.LimitReassessments[order.IntentId].EntryOrderId == order.EntryOrderId
                  && unclear.Adapter.CancelCount == 0 && unclear.Adapter.SubmitCount == 1
                  && unclear.Ai.LimitReassessments.Count == 1
                  && unclear.Recovery.State.Events.Any(item => item.Type == RecoveryEventType.Error
                      && item.NotificationSummary == "Переоценка Limit не завершена; исходная заявка сохранена."),
                "Inconclusive keeps the exact order, records an 008 notification and does not confuse search NO_SETUP");
        }

        using (var independent = new Harness(Path.Combine(root, "independent-009-deadline"),
                   ["SOL"], entryType: "Limit"))
        {
            await independent.Loop.StartAsync();
            independent.Adapter.EntryFilledQuantity = 0m;
            var routeA = independent.Contexts.Routes.Single();
            var routeB = independent.AddAlternativeRoute("SOL", Harness.VenueB, Harness.AccountB,
                "SOL-PERP", 1000m, 900m);
            var solRequest = Request("SOL", "Limit");
            var alternateRequest = Request("SOL", "Limit");
            var solResponse = Setup(solRequest, independent.Clock.UtcNow);
            var alternateResponse = Setup(alternateRequest, independent.Clock.UtcNow);
            var solPrepared = await PrepareEntryAsync(independent, solRequest, solResponse,
                selectedRoute: routeA);
            var alternatePrepared = await PrepareEntryAsync(independent, alternateRequest, alternateResponse,
                selectedRoute: routeB);
            var sol = await OpenLimitDirectlyAsync(independent, solRequest, solResponse, solPrepared.IntentId,
                selectedRoute: routeA);
            var alternate = await OpenLimitDirectlyAsync(independent, alternateRequest, alternateResponse,
                alternatePrepared.IntentId, selectedRoute: routeB);
            var interval = independent.Profile.Settings.LimitReassessmentMinutes;
            var states = new Dictionary<string, TradingLoopLimitReassessment>(StringComparer.Ordinal)
            {
                [sol.IntentId] = new TradingLoopLimitReassessment(sol.IntentId, sol.EntryOrderId,
                    solRequest, solResponse, independent.Clock.UtcNow, interval,
                    independent.Clock.UtcNow.AddMinutes(300), LimitReassessmentPhase.Scheduled),
                [alternate.IntentId] = new TradingLoopLimitReassessment(alternate.IntentId,
                    alternate.EntryOrderId, alternateRequest, alternateResponse, independent.Clock.UtcNow,
                    interval, independent.Clock.UtcNow.AddMinutes(240), LimitReassessmentPhase.Scheduled)
            };
            if (!await independent.Recovery.SaveTradingLoopStateAsync(independent.Loop.State with
                { LimitReassessments = states }))
                throw new InvalidOperationException("Could not seed independent synthetic Limit deadlines.");
            await independent.RestartAsync();
            independent.Clock.UtcNow = independent.Clock.UtcNow.AddMinutes(240);
            independent.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.Inconclusive));
            var independentResult = await independent.Loop.RunOnceAsync();
            var monitoredSol = independent.Orders.Orders.Single(item => item.IntentId == sol.IntentId);
            var monitoredAlternate = independent.Orders.Orders.Single(item => item.IntentId == alternate.IntentId);
            check(monitoredSol.NextManagementCheckAt == independent.Clock.UtcNow.AddMinutes(
                      independent.Profile.Settings.LimitOrderCheckMinutes)
                  && monitoredAlternate.NextManagementCheckAt == independent.Clock.UtcNow.AddMinutes(
                      independent.Profile.Settings.LimitOrderCheckMinutes)
                  && independent.Ai.LimitReassessments.Count == 1
                  && independent.Ai.LimitReassessments[0].IntentId == alternate.IntentId
                  && ManagedOrderState.BlocksNewEntry(monitoredSol)
                  && ManagedOrderState.BlocksNewEntry(monitoredAlternate),
                $"a future reassessment does not defer another Limit's due 009 and reassessment deadlines "
                + $"(sol={monitoredSol.NextManagementCheckAt:O}; alt={monitoredAlternate.NextManagementCheckAt:O}; "
                + $"expected={independent.Clock.UtcNow.AddMinutes(independent.Profile.Settings.LimitOrderCheckMinutes):O}; "
                + $"ai={independent.Ai.LimitReassessments.Count}; "
                + $"aiIntent={independent.Ai.LimitReassessments.FirstOrDefault()?.IntentId}; "
                + $"solId={sol.IntentId}; altId={alternate.IntentId}; status={independentResult.Status})");
        }
    }

    private static async Task CheckLimitReassessmentRaces(Action<bool, string> check, string root)
    {
        using (var stopped = new Harness(Path.Combine(root, "stop"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(stopped);
            stopped.Clock.UtcNow = stopped.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            stopped.Ai.BlockNextLimitResponse();
            var run = stopped.Loop.RunOnceAsync();
            var context = await stopped.Ai.LimitReassessmentStarted.Task;
            var stop = stopped.Loop.StopAsync();
            while (stopped.Loop.IsAdmitting)
                await Task.Yield();
            stopped.Ai.ReleaseBlockedLimitResponse(LimitResult(context, LimitReassessmentOutcome.Replacement,
                ReplacementSetup(context.Request, stopped.Clock.UtcNow)));
            var outcomes = await Task.WhenAll(run, stop);
            var stoppedOrder = stopped.Orders.Orders.Single(item => item.IntentId == order.IntentId);
            check(stopped.Adapter.SubmitCount == 1 && stopped.Adapter.CancelCount == 1
                  && stoppedOrder.EntryTerminalState == ManagedEntryTerminalState.Cancelled
                  && stopped.Loop.State.LimitReassessments.Values.All(item => item.Decision is null),
                $"Stop during AI invalidates the late replacement (run={outcomes[0].Status}; stop={outcomes[1].Status}; order={stoppedOrder.EntryTerminalState}; limitStates={stopped.Loop.State.LimitReassessments.Count})");
        }

        using (var transferred = new Harness(Path.Combine(root, "manual-transfer"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(transferred);
            transferred.Clock.UtcNow = transferred.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            transferred.Ai.BlockNextLimitResponse();
            var run = transferred.Loop.RunOnceAsync();
            var context = await transferred.Ai.LimitReassessmentStarted.Task;
            var transfer = await transferred.Orders.TransferToManualAsync(order.IntentId);
            transferred.Ai.ReleaseBlockedLimitResponse(LimitResult(context, LimitReassessmentOutcome.Replacement,
                ReplacementSetup(context.Request, transferred.Clock.UtcNow)));
            await run;
            check(transfer.Succeeded && transferred.Adapter.CancelCount == 0 && transferred.Adapter.SubmitCount == 1
                  && transferred.Orders.Orders.Single(item => item.IntentId == order.IntentId).ManagementMode
                      == ManagedOrderManagementMode.Manual
                  && transferred.Loop.State.LimitReassessments.Count == 0,
                "manual transfer during AI invalidates the late result and prevents automated order changes");
        }

        foreach (var fillQuantity in new decimal?[] { 0.5m, null })
        {
            using var filled = new Harness(Path.Combine(root, fillQuantity is null ? "full-fill" : "partial-fill"),
                ["SOL"], entryType: "Limit");
            var order = await OpenLimitAsync(filled);
            filled.Clock.UtcNow = filled.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            filled.Adapter.FillDuringCancel = fillQuantity ?? order.Quantity;
            filled.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.Replacement,
                ReplacementSetup(context.Request, filled.Clock.UtcNow)));
            await filled.Loop.RunOnceAsync();
            var after = filled.Orders.Orders.Single(item => item.IntentId == order.IntentId);
            var expectedFill = fillQuantity ?? order.Quantity;
            check(filled.Adapter.CancelCount == 1 && filled.Adapter.SubmitCount == 1
                  && after.FilledQuantity == expectedFill && after.PositionQuantity == expectedFill
                  && after.Status == ManagedOrderStatus.Protected
                  && after.TakeProfitCoveredQuantity == expectedFill && after.StopLossCoveredQuantity == expectedFill
                  && filled.Loop.State.LimitReassessments.Count == 0,
                $"{(fillQuantity is null ? "full" : "partial")} fill during cancel stays under TP/SL protection and blocks replacement");
        }
    }

    private static async Task CheckLimitReassessmentRecovery(Action<bool, string> check, string root)
    {
        using (var linked = new Harness(Path.Combine(root, "before-order-result"), ["SOL"], entryType: "Limit"))
        {
            await linked.Loop.StartAsync();
            var request = Request("SOL", "Limit");
            var response = Setup(request, linked.Clock.UtcNow);
            var prepared = await PrepareEntryAsync(linked, request, response);
            var pending = new TradingLoopPendingSetup(request, response, linked.Clock.UtcNow,
                linked.Profile.Settings.LimitReassessmentMinutes, prepared);
            if (!await linked.Recovery.SaveTradingLoopStateAsync(linked.Loop.State with { PendingSetup = pending }))
                throw new InvalidOperationException("Could not seed the original limit response.");
            linked.Adapter.EntryFilledQuantity = 0m;
            var order = await OpenLimitDirectlyAsync(linked, request, response, prepared.IntentId);
            await linked.RestartAsync();
            await linked.Loop.StartAsync();
            await linked.Loop.RunOnceAsync();
            check(linked.Adapter.SubmitCount == 1 && linked.Loop.State.PendingSetup is null
                  && order.IntentId == prepared.IntentId
                  && linked.Loop.State.LimitReassessments[order.IntentId].EntryOrderId == order.EntryOrderId
                  && linked.Loop.State.LimitReassessments[order.IntentId].Request.Id == request.Id
                  && linked.Ai.LimitReassessments.Count == 0,
                "restart links the persisted source response to the exact order when the loop missed its result");
        }

        using (var foreign = new Harness(Path.Combine(root, "no-adopt-foreign-order"), ["SOL"],
                   entryType: "Limit"))
        {
            await foreign.Loop.StartAsync();
            var request = Request("SOL", "Limit");
            var response = Setup(request, foreign.Clock.UtcNow);
            foreign.Adapter.EntryFilledQuantity = 0m;
            var actual = await PrepareEntryAsync(foreign, request, response);
            var order = await OpenLimitDirectlyAsync(foreign, request, response, actual.IntentId);
            var expected = actual with { IntentId = Guid.NewGuid().ToString("N") };
            var pending = new TradingLoopPendingSetup(request, response, foreign.Clock.UtcNow,
                foreign.Profile.Settings.LimitReassessmentMinutes, expected);
            if (!await foreign.Recovery.SaveTradingLoopStateAsync(foreign.Loop.State with { PendingSetup = pending }))
                throw new InvalidOperationException("Could not seed the unmatched exact-intent fixture.");
            await foreign.RestartAsync();
            await foreign.Loop.RunOnceAsync();
            var retained = foreign.Orders.Orders.Single(item => item.IntentId == order.IntentId);
            check(foreign.Adapter.SubmitCount == 1 && foreign.Adapter.CancelCount == 0
                  && retained.EntryTerminalState == ManagedEntryTerminalState.Open
                  && retained.PendingOperationId is null
                  && foreign.Loop.State.PendingSetup?.PreparedEntry?.IntentId == expected.IntentId
                  && !foreign.Loop.State.LimitReassessments.ContainsKey(order.IntentId),
                $"a matching legacy order with another IntentId is not adopted or mutated during restart "
                + $"(submits={foreign.Adapter.SubmitCount}; cancels={foreign.Adapter.CancelCount}; "
                + $"entry={retained.EntryTerminalState}; pending={retained.PendingOperationId}; "
                + $"saved={foreign.Loop.State.PendingSetup?.PreparedEntry?.IntentId}; expected={expected.IntentId}; "
                + $"reassessment={foreign.Loop.State.LimitReassessments.ContainsKey(order.IntentId)}; "
                + $"status={foreign.Loop.State.LastStatus})");
        }

        using (var beforeCancel = new Harness(Path.Combine(root, "before-cancel"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(beforeCancel);
            beforeCancel.Clock.UtcNow = beforeCancel.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            await SeedReplacementDecisionAsync(beforeCancel, order, LimitReassessmentPhase.CancelPending);
            await beforeCancel.RestartAsync();
            await beforeCancel.Loop.StartAsync();
            await beforeCancel.Loop.RunOnceAsync();
            check(beforeCancel.Adapter.CancelCount == 1 && beforeCancel.Adapter.SubmitCount == 2
                  && beforeCancel.Ai.LimitReassessments.Count == 0
                  && beforeCancel.Orders.Orders.Any(item => item.IntentId != order.IntentId
                      && item.EntryType == "Limit" && ManagedOrderState.BlocksNewEntry(item)),
                "restart before cancellation resumes the stored decision once, then sends the replacement through A");
        }

        using (var afterCancel = new Harness(Path.Combine(root, "after-cancel"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(afterCancel);
            afterCancel.Clock.UtcNow = afterCancel.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            var waiting = await SeedReplacementDecisionAsync(afterCancel, order,
                LimitReassessmentPhase.CancelPending);
            var cancelled = await afterCancel.Orders.CancelRemainderAsync(order.IntentId, true);
            if (!cancelled.Succeeded)
                throw new InvalidOperationException("Could not seed the confirmed cancellation boundary.");
            var prepared = await SaveReplacementPendingEntryAsync(afterCancel, waiting, order.IntentId);
            await afterCancel.RestartAsync();
            await afterCancel.Loop.StartAsync();
            await afterCancel.Loop.RunOnceAsync();
            check(afterCancel.Adapter.CancelCount == 1 && afterCancel.Adapter.SubmitCount == 2
                  && afterCancel.Loop.State.LimitReassessments.Count == 1
                  && afterCancel.Loop.State.LimitReassessments.ContainsKey(prepared.IntentId)
                  && afterCancel.Loop.State.LimitReassessments.Values.Single().IntentId != order.IntentId,
                "restart after confirmed cancellation resumes replacement without a second cancel");
        }

        using (var beforeReceived = new Harness(Path.Combine(root, "after-replacement-transfer"), ["SOL"],
                   entryType: "Limit"))
        {
            var order = await OpenLimitAsync(beforeReceived);
            beforeReceived.Clock.UtcNow = beforeReceived.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            var waiting = await SeedReplacementDecisionAsync(beforeReceived, order,
                LimitReassessmentPhase.CancelPending);
            var cancelled = await beforeReceived.Orders.CancelRemainderAsync(order.IntentId, true);
            if (!cancelled.Succeeded)
                throw new InvalidOperationException("Could not seed the confirmed cancellation boundary.");
            var prepared = await SaveReplacementPendingEntryAsync(beforeReceived, waiting, order.IntentId);
            var reassessment = beforeReceived.Loop.State.LimitReassessments[order.IntentId];
            var replacementResponse = reassessment.Decision!.ReplacementResponse!;
            await OpenLimitDirectlyAsync(beforeReceived, reassessment.Request, replacementResponse,
                prepared.IntentId);
            var submitted = beforeReceived.Adapter.SubmitCount;
            await beforeReceived.RestartAsync();
            await beforeReceived.Loop.StartAsync();
            await beforeReceived.Loop.RunOnceAsync();
            check(beforeReceived.Adapter.CancelCount == 1 && beforeReceived.Adapter.SubmitCount == submitted
                  && beforeReceived.Loop.State.LimitReassessments.Count == 1
                  && beforeReceived.Loop.State.LimitReassessments.ContainsKey(prepared.IntentId)
                  && beforeReceived.Loop.State.LimitReassessments.Values.Single().IntentId != order.IntentId,
                "restart after replacement transfer links the new order instead of repeating cancel or entry");
        }

        using (var rejected = new Harness(Path.Combine(root, "rejected-cancel"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(rejected);
            rejected.Clock.UtcNow = rejected.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            rejected.Adapter.CancelOutcome = RecoveryOperationOutcome.NotExecuted;
            rejected.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.Replacement,
                ReplacementSetup(context.Request, rejected.Clock.UtcNow)));
            await rejected.Loop.RunOnceAsync();
            var next = rejected.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            await rejected.RestartAsync();
            await rejected.Loop.StartAsync();
            await rejected.Loop.RunOnceAsync();
            check(rejected.Adapter.CancelCount == 1 && rejected.Adapter.SubmitCount == 1
                  && rejected.Ai.LimitReassessments.Count == 1 && next > rejected.Clock.UtcNow
                  && rejected.Orders.Orders.Single(item => item.IntentId == order.IntentId)
                      .EntryTerminalState == ManagedEntryTerminalState.Open,
                "a rejected cancel keeps the exact remainder and does not repeat cancellation after restart");
        }

        using (var unknown = new Harness(Path.Combine(root, "unknown-cancel"), ["SOL"], entryType: "Limit"))
        {
            var order = await OpenLimitAsync(unknown);
            unknown.Clock.UtcNow = unknown.Loop.State.LimitReassessments[order.IntentId].NextReassessmentAt;
            unknown.Adapter.CancelOutcome = RecoveryOperationOutcome.Unknown;
            unknown.Ai.LimitHandler = context => Task.FromResult(LimitResult(context,
                LimitReassessmentOutcome.InvalidNoReplacement));
            await unknown.Loop.RunOnceAsync();
            await unknown.RestartAsync();
            await unknown.Loop.StartAsync();
            await unknown.Loop.RunOnceAsync();
            check(unknown.Adapter.CancelCount == 1 && unknown.Adapter.SubmitCount == 1
                  && unknown.Orders.Orders.Single(item => item.IntentId == order.IntentId).PendingOperationId is not null
                  && unknown.Loop.State.LimitReassessments[order.IntentId].Phase
                      == LimitReassessmentPhase.CancelPending,
                "unknown cancellation remains pending reconciliation and is never sent twice after restart");
        }
    }

    private static async Task<ManagedOrderState> OpenLimitAsync(Harness harness)
    {
        harness.Adapter.EntryFilledQuantity = 0m;
        harness.Ai.Handler = request => Task.FromResult(Setup(request, harness.Clock.UtcNow));
        if (!(await harness.Loop.StartAsync()).Succeeded)
            throw new InvalidOperationException("Could not start the synthetic Limit scenario.");
        var result = await harness.Loop.RunOnceAsync();
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Status);
        return harness.Orders.Orders.Single(order => order.EntryType == "Limit");
    }

    private static LimitReassessmentResult LimitResult(
        LimitReassessmentContext context,
        LimitReassessmentOutcome outcome,
        AiSourceResponse? replacement = null,
        string? reason = null) => new(context.AssessmentId, context.IntentId, context.EntryOrderId,
        context.Request.Id, outcome, replacement, reason);

    private static AiSourceResponse ReplacementSetup(AiAnalysisRequest request, DateTimeOffset now) =>
        new(request.Id, JsonSerializer.Serialize(new
        {
            status = "SETUP",
            market = request.Market,
            order_type = request.OrderType,
            side = "LONG",
            reference_price = 50m,
            reference_time_utc = now.AddSeconds(-10).ToString("O", CultureInfo.InvariantCulture),
            entry_price = 50m,
            take_profit = 56m,
            stop_loss = 45m,
            reason = "синтетический новый сетап",
            invalidation = "синтетическое условие"
        }), true);

    private static async Task<TradingLoopPreparedEntry> PrepareEntryAsync(
        Harness harness,
        AiAnalysisRequest request,
        AiSourceResponse response,
        string? intentId = null,
        TradingLoopRoute? selectedRoute = null)
    {
        var assessed = AiResponseAssessor.Assess(request, response);
        var fields = assessed.Fields ?? throw new InvalidOperationException("Synthetic setup did not parse.");
        var observedAt = DateTimeOffset.Parse(fields.ReferenceTimeUtc!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var setup = new EntryEligibilitySetup(EntrySetupState.Complete, request.Asset, request.Market,
            request.EntryType,
            fields.EntryPrice, fields.Side == "LONG" ? PositionDirection.Long : PositionDirection.Short,
            fields.TakeProfit, fields.StopLoss, "USDC/" + request.Asset, "local fixture", observedAt);
        var routes = harness.Contexts.GetRoutes(harness.Profile);
        var route = selectedRoute ?? routes.Single(item => item.Asset == request.Asset);
        var account = (await harness.Recovery.ReadCurrentSnapshotAsync(route.Venue, route.Account)).Account!;
        var profile = harness.Profile.Copy();
        profile.SavedOperatingIntent = true;
        profile.Settings.EntryType = request.EntryType;
        var context = await harness.Contexts.ReadAsync(route, [account], setup, profile, harness.Clock.UtcNow,
            CancellationToken.None);
        var input = context.EligibilityInput! with { EvaluatedAt = harness.Clock.UtcNow };
        var eligibility = EntryEligibilityEvaluator.Evaluate(input);
            var pool = eligibility.SelectedPool ?? throw new InvalidOperationException(
                $"Synthetic entry was not eligible ({eligibility.Status}: {string.Join(' ', eligibility.Reasons)}).");
        return new TradingLoopPreparedEntry(intentId ?? Guid.NewGuid().ToString("N"), request.Id,
            route.Venue, route.Account, route.Contract, request.Asset, request.EntryType,
            setup.Direction!.Value, pool.Quantity!.Value, setup.LimitPrice,
            setup.TakeProfit!.Value, setup.StopLoss!.Value);
    }

    private static async Task<ManagedOrderState> OpenLimitDirectlyAsync(
        Harness harness,
        AiAnalysisRequest request,
        AiSourceResponse response,
        string? intentId = null,
        TradingLoopRoute? selectedRoute = null)
    {
        var prepared = await PrepareEntryAsync(harness, request, response, intentId, selectedRoute);
        var account = (await harness.Recovery.ReadCurrentSnapshotAsync(prepared.Venue, prepared.Account))
            .Account ?? throw new InvalidOperationException("Synthetic account was unavailable.");
        var profile = harness.Profile.Copy();
        profile.SavedOperatingIntent = true;
        profile.Settings.EntryType = request.EntryType;
        var assessed = AiResponseAssessor.Assess(request, response);
        var fields = assessed.Fields ?? throw new InvalidOperationException("Synthetic setup did not parse.");
        var observedAt = DateTimeOffset.Parse(fields.ReferenceTimeUtc!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var setup = new EntryEligibilitySetup(EntrySetupState.Complete, request.Asset, request.Market,
            request.EntryType, fields.EntryPrice, prepared.Direction, prepared.TakeProfit, prepared.StopLoss,
            "USDC/" + request.Asset, "local fixture", observedAt);
        var route = new TradingLoopRoute(prepared.Asset, prepared.Venue, prepared.Account, prepared.Contract);
        var context = await harness.Contexts.ReadAsync(route, [account], setup, profile, harness.Clock.UtcNow,
            CancellationToken.None);
        var input = context.EligibilityInput! with { EvaluatedAt = harness.Clock.UtcNow };
        var intent = new ManagedOrderIntent(prepared.Venue, prepared.Account, prepared.Asset, prepared.Contract,
            prepared.Direction, prepared.EntryType, prepared.Quantity, prepared.LimitPrice,
            prepared.TakeProfit, prepared.StopLoss, prepared.IntentId);
        var opened = await harness.Orders.OpenAsync(intent, input, savedOperatingIntent: true,
            cancellationToken: CancellationToken.None);
        if (!opened.Succeeded || opened.IntentId is null)
            throw new InvalidOperationException(opened.Status);
        return harness.Orders.Orders.Single(order => order.IntentId == opened.IntentId);
    }

    private static async Task<TradingLoopLimitReassessment> SeedReplacementDecisionAsync(
        Harness harness,
        ManagedOrderState order,
        LimitReassessmentPhase phase)
    {
        var current = harness.Loop.State.LimitReassessments[order.IntentId];
        var assessmentId = Guid.NewGuid();
        var result = new LimitReassessmentResult(assessmentId, order.IntentId, order.EntryOrderId!,
            current.Request.Id, LimitReassessmentOutcome.Replacement,
            ReplacementSetup(current.Request, harness.Clock.UtcNow));
        var decision = current with
        {
            Phase = phase,
            AssessmentId = assessmentId,
            Decision = result,
            DecisionReceivedAt = harness.Clock.UtcNow,
            CancellationConfirmedAt = phase == LimitReassessmentPhase.ReplacementPendingEntry
                ? harness.Clock.UtcNow : null,
            ReplacementIntervalMinutes = phase == LimitReassessmentPhase.ReplacementPendingEntry
                ? harness.Profile.Settings.LimitReassessmentMinutes : null
        };
        var states = new Dictionary<string, TradingLoopLimitReassessment>(harness.Loop.State.LimitReassessments,
            StringComparer.Ordinal)
        {
            [order.IntentId] = decision
        };
        if (!await harness.Recovery.SaveTradingLoopStateAsync(harness.Loop.State with
            { LimitReassessments = states }))
            throw new InvalidOperationException("Could not seed the saved replacement decision.");
        return decision;
    }

    private static async Task<TradingLoopPreparedEntry> SaveReplacementPendingEntryAsync(
        Harness harness,
        TradingLoopLimitReassessment decision,
        string intentId)
    {
        var response = decision.Decision?.ReplacementResponse
            ?? throw new InvalidOperationException("A replacement response is required for the fixture.");
        var prepared = await PrepareEntryAsync(harness, decision.Request, response);
        var states = new Dictionary<string, TradingLoopLimitReassessment>(harness.Loop.State.LimitReassessments,
            StringComparer.Ordinal)
        {
            [intentId] = decision with
            {
                Phase = LimitReassessmentPhase.ReplacementPendingEntry,
                CancellationConfirmedAt = harness.Clock.UtcNow,
                ReplacementIntervalMinutes = harness.Profile.Settings.LimitReassessmentMinutes,
                ReplacementIntent = prepared
            }
        };
        if (!await harness.Recovery.SaveTradingLoopStateAsync(harness.Loop.State with
            {
                LimitReassessments = states,
                PendingSetup = new TradingLoopPendingSetup(decision.Request, response,
                    decision.DecisionReceivedAt ?? harness.Clock.UtcNow,
                    harness.Profile.Settings.LimitReassessmentMinutes, prepared, intentId)
            }))
            throw new InvalidOperationException("Could not seed the confirmed replacement boundary.");
        return prepared;
    }

    private static AiAnalysisRequest Request(string asset, string orderType)
    {
        if (!AiAnalysisRequestFactory.TryCreateBatch(asset, orderType, out var requests, out var error))
            throw new InvalidOperationException(error);
        return requests.Single();
    }

    private static AiSourceResponse Setup(AiAnalysisRequest request, DateTimeOffset now) =>
        Setup(request, now, "LONG", 50m, 55m, 45m);

    private static AiSourceResponse Setup(
        AiAnalysisRequest request,
        DateTimeOffset now,
        string side,
        decimal referencePrice,
        decimal takeProfit,
        decimal stopLoss,
        DateTimeOffset? referenceAt = null) =>
        new(request.Id, JsonSerializer.Serialize(new
        {
            status = "SETUP",
            market = request.Market,
            order_type = request.OrderType,
            side,
            reference_price = referencePrice,
            reference_time_utc = (referenceAt ?? now.AddSeconds(-10)).ToString("O", CultureInfo.InvariantCulture),
            entry_price = request.OrderType == "MARKET" ? (decimal?)null : referencePrice,
            take_profit = takeProfit,
            stop_loss = stopLoss,
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
        private readonly Func<TimeSpan, CancellationToken, Task>? _delay;

        internal Harness(
            string root,
            IReadOnlyList<string> assets,
            decimal availableMargin = 1000m,
            int recoveryCheckSeconds = 60,
            string entryType = "Market",
            string selectionMethod = "AssetOrder",
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _root = Path.GetFullPath(root);
            Directory.CreateDirectory(_root);
            _settingsPath = Path.Combine(_root, "settings.json");
            _journalPath = Path.Combine(_root, "recovery-state.json");
            _delay = delay;
            Clock = new FakeClock(InitialTime);
            Profile = new SettingsProfile
            {
                SelectedMode = "TrueNorth AI setups",
                Settings = new AppSettings
                {
                    Assets = assets.Cast<string?>().ToList(),
                    EntryType = entryType,
                    CandidateSelectionMethod = selectionMethod,
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

        private TradingLoopCoordinator CreateLoop() => new(SettingsStore, Profile, Recovery, Orders, Ai, Contexts, Clock, _delay);

        public void Dispose()
        {
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : IRecoveryClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FixtureAi(FakeClock clock) : IAiAnalysisSource, ITradingLoopReassessmentSource,
        ITradingLoopLimitReassessmentSource
    {
        private readonly object _requestGate = new();
        private TaskCompletionSource<AiAnalysisRequest>? _started;
        private TaskCompletionSource<AiSourceResponse>? _release;
        private TaskCompletionSource<LimitReassessmentContext>? _limitStarted;
        private TaskCompletionSource<LimitReassessmentResult>? _limitRelease;
        internal readonly List<AiAnalysisRequest> Requests = [];
        internal readonly List<TradingLoopReassessmentContext> Reassessments = [];
        internal readonly List<LimitReassessmentContext> LimitReassessments = [];
        internal Func<AiAnalysisRequest, Task<AiSourceResponse>> Handler = request => Task.FromResult(NoSetup(request));
        internal Func<AiAnalysisRequest, CancellationToken, Task<AiSourceResponse>>? CancellationHandler { get; set; }
        internal Func<LimitReassessmentContext, Task<LimitReassessmentResult>> LimitHandler = context =>
            Task.FromResult(LimitResult(context, LimitReassessmentOutcome.Inconclusive));
        internal TaskCompletionSource<AiAnalysisRequest> RequestStarted => _started
            ?? throw new InvalidOperationException("No blocked AI request was started.");
        internal TaskCompletionSource<LimitReassessmentContext> LimitReassessmentStarted => _limitStarted
            ?? throw new InvalidOperationException("No limit reassessment is blocked.");

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

        internal void BlockNextLimitResponse()
        {
            _limitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _limitRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void ReleaseBlockedLimitResponse(LimitReassessmentResult result) =>
            _limitRelease?.TrySetResult(result);

        public Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
        {
            lock (_requestGate)
                Requests.Add(request);
            if (_release is not null)
            {
                _started!.TrySetResult(request);
                return _release.Task;
            }
            return CancellationHandler?.Invoke(request, cancellationToken) ?? Handler(request);
        }

        public Task<AiSourceResponse> ReassessAsync(
            TradingLoopReassessmentContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reassessments.Add(context);
            lock (_requestGate)
                Requests.Add(context.Request);
            return Handler(context.Request);
        }

        public Task<LimitReassessmentResult> ReassessLimitAsync(
            LimitReassessmentContext context,
            CancellationToken cancellationToken)
        {
            LimitReassessments.Add(context);
            if (_limitRelease is not null)
            {
                _limitStarted!.TrySetResult(context);
                return _limitRelease.Task;
            }
            return LimitHandler(context);
        }
    }

    private sealed class FixtureContextSource(IReadOnlyList<TradingLoopRoute> routes) : ITradingLoopContextSource
    {
        private readonly List<TradingLoopRoute> _routes = routes.ToList();
        private readonly HashSet<string> _initialCandidateAssets = new(StringComparer.Ordinal);
        private bool _initialCandidateBatchCompleted;
        internal IReadOnlyList<TradingLoopRoute> Routes => _routes;
        internal bool ExposureCovered { get; set; } = true;
        internal bool? MinimumEntryAffordable { get; set; } = true;
        internal HashSet<TradingLoopRoute> UnavailableRoutes { get; } = [];
        internal EntryEligibilityInput? LastInput { get; private set; }
        internal List<EntryEligibilityInput> LastInputs { get; } = [];
        internal Dictionary<string, decimal> MarketPrices { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, decimal> MarketPricesAfterInitialEvaluation { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, decimal> CandidateStopCoverageByAsset { get; } = new(StringComparer.Ordinal);
        internal Action? AfterInitialCandidateBatch { get; set; }
        internal decimal CandidateStopCoverage { get; set; } = 20m;

        internal void AddRoute(TradingLoopRoute route) => _routes.Add(route);
        internal void SetMarketPrice(string asset, decimal price) => MarketPrices[asset] = price;
        internal void SetMarketPriceAfterInitialEvaluation(string asset, decimal price) =>
            MarketPricesAfterInitialEvaluation[asset] = price;

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

            var candidateStopCoverage = CandidateStopCoverageByAsset.TryGetValue(setup.Asset!, out var coverage)
                ? coverage : CandidateStopCoverage;
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
                var instrumentExample = template.Instruments.FirstOrDefault(instrument => instrument.Identity.BaseAsset == setup.Asset);
                var contract = routeForAccount?.Contract ?? instrumentExample?.Identity.Contract;
                if (string.IsNullOrWhiteSpace(contract))
                    continue;

                pools.Add(template with
                {
                    Account = account,
                    Instruments = template.Instruments.Select(instrument => instrument with
                    {
                        Identity = instrument.Identity with { ObservedAt = now.AddSeconds(-5) },
                        RulesObservedAt = now.AddSeconds(-5),
                        Funding = instrument.Funding is { } funding
                            ? funding with { ObservedAt = now.AddSeconds(-5) } : null
                    }).ToArray(),
                    Positions = [],
                    Orders = [],
                    SetupBinding = new RiskSetupMarketBinding(account.Venue, contract, setup.Asset,
                        setup.Market, "local fixture", now.AddSeconds(-2)),
                    CurrentMarketPrice = setup.EntryType == "Market"
                        ? EntryEligibilityExample.Evidence(
                            MarketPricesAfterInitialEvaluation.TryGetValue(setup.Asset!, out var refreshedPrice)
                                && _initialCandidateBatchCompleted
                                ? refreshedPrice
                                : MarketPrices.TryGetValue(setup.Asset!, out var marketPrice) ? marketPrice : 50m,
                            "USDC/" + setup.Asset, marketAt, contract) : null,
                    VerifiedLimitPrice = setup.EntryType == "Limit"
                        ? EntryEligibilityExample.Evidence(setup.LimitPrice!.Value, "USDC/" + setup.Asset,
                            marketAt, contract) : null,
                    CandidateStopCoveredBaseQuantity = EntryEligibilityExample.Evidence(
                        candidateStopCoverage, setup.Asset!, now.AddSeconds(-2), contract),
                    AvailableMarginIncludesOrderReserves = Refresh(template.AvailableMarginIncludesOrderReserves, now),
                    OtherUnreservedRequirements = Refresh(template.OtherUnreservedRequirements, now),
                    AdditionalMaintenanceHeadroom = Refresh(template.AdditionalMaintenanceHeadroom, now)
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
            LastInputs.Add(input);
            if (setup is not null && _routes.LastOrDefault(candidate => candidate.Asset == setup.Asset) == route)
            {
                _initialCandidateAssets.Add(setup.Asset!);
                if (!_initialCandidateBatchCompleted && _initialCandidateAssets.Count
                        == _routes.Select(candidate => candidate.Asset).Distinct(StringComparer.Ordinal).Count())
                {
                    _initialCandidateBatchCompleted = true;
                    AfterInitialCandidateBatch?.Invoke();
                }
            }
            return Task.FromResult(new TradingLoopContext(available, ExposureCovered, input,
                available ? null : "Синтетический маршрут недоступен.", MinimumEntryAffordable));
        }

        private static RiskEvidence<T>? Refresh<T>(RiskEvidence<T>? evidence, DateTimeOffset now) =>
            evidence is null ? null : evidence with { ObservedAt = now };
    }

    private sealed class FixtureAdapter(FakeClock clock) : IManagedOrderTestAdapter
    {
        private readonly Dictionary<(string Venue, string Account), AccountFixture> _accounts = [];
        private readonly Dictionary<string, RecoveryOperationObservation> _observations = new(StringComparer.Ordinal);
        private RecoveryJournalStore? _store;
        internal RecoveryOperationOutcome EntryOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        internal RecoveryOperationOutcome CancelOutcome { get; set; } = RecoveryOperationOutcome.Confirmed;
        internal int SubmitCount { get; private set; }
        internal int CancelCount { get; private set; }
        internal bool DurableTransferSeenBeforeEveryCallback { get; private set; } = true;
        internal bool PreparedEntrySavedBeforeEntryTransfer { get; private set; } = true;
        internal Exception? ReadFailure { get; set; }
        internal bool FailNextRead { get; set; }
        internal decimal? EntryFilledQuantity { get; set; }
        internal decimal? FillDuringCancel { get; set; }
        internal List<string> Trace { get; } = [];
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
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new IOException("fixed transient account read failure");
            }
            if (ReadFailure is not null)
                throw ReadFailure;
            var fixture = _accounts[(venue, account)];
            Trace.Add($"read:{CancelCount}:{fixture.Orders.Count}:{fixture.Protections.Count}");
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
            Trace.Add("entry:" + intent.IntentId);
            if (EntryOutcome != RecoveryOperationOutcome.Confirmed)
                return Task.FromResult(EntryOutcome);

            var fixture = _accounts[(intent.Venue, intent.Account)];
            var orderId = "entry-" + intent.IntentId;
            var positionId = "position-" + intent.IntentId;
            fixture.Positions.RemoveAll(position => position.Contract == intent.Contract);
            fixture.Orders.RemoveAll(order => order.Contract == intent.Contract);
            fixture.Protections.RemoveAll(protection => protection.Contract == intent.Contract);
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
            Trace.Add("cancel:" + orderId);
            var current = fixture.Orders.Single(item => item.OrderId == orderId);
            var filled = current.FilledQuantity;
            if (FillDuringCancel is { } extraFill && extraFill > 0m)
            {
                filled = Math.Min(intent.Quantity, filled + extraFill);
                fixture.Orders.RemoveAll(item => item.OrderId == orderId);
                var remainder = intent.Quantity - filled;
                if (remainder > 0m)
                    fixture.Orders.Add(current with { FilledQuantity = filled });
                var positionId = "position-" + intent.IntentId;
                fixture.Positions.RemoveAll(item => item.PositionId == positionId);
                fixture.Positions.Add(new OpenPosition(intent.Asset, intent.Contract, intent.Direction, filled,
                    "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.SoftwareOwned, "local fixture",
                    positionId, [orderId]));
            }

            var remaining = intent.Quantity - filled;
            if (CancelOutcome == RecoveryOperationOutcome.Unknown)
                return Task.FromResult(RecoveryOperationOutcome.Unknown);
            if (CancelOutcome == RecoveryOperationOutcome.Confirmed)
                fixture.Orders.RemoveAll(item => item.OrderId == orderId);
            _observations[operationId] = new RecoveryOperationObservation(operationId, intent.Venue, intent.Account,
                intent.Contract, "CancelOpeningRemainder", CancelOutcome, orderId,
                filled > 0m ? "position-" + intent.IntentId : intent.PositionId,
                intent.Direction, filled,
                CancelOutcome == RecoveryOperationOutcome.Confirmed ? 0m : remaining,
                filled > 0m ? filled : intent.PositionQuantity,
                EntryOrderState: CancelOutcome == RecoveryOperationOutcome.Confirmed
                    ? remaining > 0m ? ManagedEntryTerminalState.Cancelled : ManagedEntryTerminalState.Filled
                    : ManagedEntryTerminalState.Open,
                FirstFillAt: filled > 0m ? clock.UtcNow : intent.FirstPartialFillAt);
            return Task.FromResult(CancelOutcome);
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
            Trace.Add("protect:" + positionId);
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
            var operation = saved.Operations.SingleOrDefault(candidate => candidate.OperationId == operationId);
            DurableTransferSeenBeforeEveryCallback &= operation?.Stage == RecoveryOperationStage.TransferStarted
                && saved.ManagedOrders.Any(order => order.PendingOperationId == operationId);
            if (operation?.ActionKind != RecoveryActionKind.Entry)
                return;

            var managed = saved.ManagedOrders.SingleOrDefault(order => operationId.StartsWith(
                order.IntentId + ":entry:", StringComparison.Ordinal));
            var prepared = saved.TradingLoop.PendingSetup?.PreparedEntry;
            PreparedEntrySavedBeforeEntryTransfer &= managed is not null && prepared is not null
                && prepared.IntentId == managed.IntentId && prepared.Venue == managed.Venue
                && prepared.Account == managed.Account && prepared.Contract == managed.Contract
                && prepared.Asset == managed.Asset && prepared.EntryType == managed.EntryType
                && (saved.TradingLoop.PendingSetup?.ReplacesIntentId is not { } replacedIntentId
                    || saved.TradingLoop.LimitReassessments.TryGetValue(replacedIntentId, out var replaced)
                        && replaced.ReplacementIntent == prepared);
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
