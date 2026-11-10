using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrueNorth.TradeFlow.Verification;

internal static class AiTestChecks
{
    internal static void CheckCore(Action<bool, string> check, string fixtureRoot)
    {
        CheckTemplatesAndInput(check);
        CheckResponses(check);
        CheckBatchOutcomes(check);
        CheckReports(check, fixtureRoot);
    }

    private static void CheckTemplatesAndInput(Action<bool, string> check)
    {
        foreach (var (entryType, wireType) in new[] { ("Market", "MARKET"), ("Limit", "LIMIT") })
        {
            check(AiAnalysisRequestFactory.TryCreateBatch("btc", entryType, out var requests, out _)
                  && requests.Count == 1 && requests[0].Asset == "BTC" && requests[0].Market == "BTC-USDC"
                  && requests[0].OrderType == wireType && requests[0].TemplateVersion == "ai-setup-v1",
                $"{entryType} template binds a validated ticker to exact BTC-USDC market and uppercase wire type");
            var prompt = requests[0].Prompt;
            check(prompt.Contains("current BTC-USDC perpetual market", StringComparison.Ordinal)
                  && prompt.Contains("analysis only", StringComparison.OrdinalIgnoreCase)
                  && prompt.Contains("Do not prepare, submit, change, or cancel orders", StringComparison.Ordinal)
                  && prompt.Contains("wallet, account, agent, signatures, or trading tools", StringComparison.Ordinal)
                  && prompt.Contains("reference_time_utc", StringComparison.Ordinal)
                  && (entryType != "Market" || prompt.Contains("entry_price to JSON null", StringComparison.Ordinal)),
                $"{entryType} prompt is analysis-only and uses the explicit entry contract");
        }

        check(AiAnalysisRequestFactory.TryCreateBatch("btc, eth\nsol", "Limit", out var batch, out _)
              && batch.Select(request => request.Market).SequenceEqual(["BTC-USDC", "ETH-USDC", "SOL-USDC"]),
            "asset batch order is preserved while exact markets are formed");
        check(!AiAnalysisRequestFactory.TryCreateBatch("BTC-USDC", "Limit", out var invalidMarket, out _)
              && invalidMarket.Count == 0,
            "market suffix and punctuation are rejected before a prompt is built");
        check(!AiAnalysisRequestFactory.TryCreateBatch("BTC", "Unknown", out _, out _)
              && !AiAnalysisRequestFactory.TryCreateBatch("BTC, btc", "Limit", out _, out _)
              && !AiAnalysisRequestFactory.TryCreateBatch("", "Limit", out _, out _),
            "unknown entry type, duplicate assets, and empty input are rejected");
    }

    private static void CheckResponses(Action<bool, string> check)
    {
        var limit = Request("SOL", "Limit");
        var historicalShape = Response(limit, side: "LONG", reference: 100m, entry: 99m, takeProfit: 110m, stopLoss: 95m,
            reason: "Synthetic historical-shape fixture.", invalidation: "Synthetic level breaks.");
        check(AiResponseAssessor.Assess(limit, historicalShape).Outcome == AiTestOutcome.CompleteSetup,
            "historical LIMIT response shape with nullable reference time parses as synthetic format/data complete");

        var market = Request("BTC", "Market");
        var marketLong = AiResponseAssessor.Assess(market,
            Response(market, side: "LONG", reference: 100m, entry: null, takeProfit: 110m, stopLoss: 95m));
        var marketShort = AiResponseAssessor.Assess(market,
            Response(market, side: "SHORT", reference: 100m, entry: null, takeProfit: 90m, stopLoss: 110m));
        check(marketLong.Outcome == AiTestOutcome.CompleteSetup && marketLong.Fields?.EntryPrice is null
              && marketShort.Outcome == AiTestOutcome.CompleteSetup,
            "Market LONG and SHORT use reference price as anchor and require a null actual entry");

        var limitShort = AiResponseAssessor.Assess(limit,
            Response(limit, side: "SHORT", reference: 100m, entry: 100m, takeProfit: 90m, stopLoss: 110m));
        check(limitShort.Outcome == AiTestOutcome.CompleteSetup,
            "Limit SHORT validates take-profit below entry below stop-loss");

        var noSetup = AiResponseAssessor.Assess(limit, NoSetup(limit));
        check(noSetup.Outcome == AiTestOutcome.ValidNoSetup && noSetup.Freshness == AiFreshnessState.Unknown,
            "well-formed NO_SETUP is a valid no-trade response with unknown freshness");

        var timestamped = AiResponseAssessor.Assess(limit,
            Response(limit, referenceTime: "2026-11-08T16:00:00Z"));
        check(timestamped.Outcome == AiTestOutcome.CompleteSetup
              && timestamped.Freshness == AiFreshnessState.TimestampPresentNotAssessed,
            "reference timestamp presence is reported separately without an age threshold");

        var missingReferenceTime = RemoveProperty(Response(limit).RawText, "reference_time_utc");
        check(AiResponseAssessor.Assess(limit,
                new AiSourceResponse(limit.Id, missingReferenceTime, true)).Outcome == AiTestOutcome.IncompleteOrInvalid,
            "missing reference_time_utc differs from explicit null");

        var missingMarketEntry = RemoveProperty(Response(market).RawText, "entry_price");
        check(AiResponseAssessor.Assess(market,
                new AiSourceResponse(market.Id, missingMarketEntry, true)).Outcome == AiTestOutcome.IncompleteOrInvalid,
            "missing Market entry_price differs from explicit null");

        var wrongMarket = SetProperty(Response(limit).RawText, "market", "ETH-USDC");
        var wrongType = SetProperty(Response(limit).RawText, "order_type", "MARKET");
        var wrongStatus = SetProperty(Response(limit).RawText, "status", "MAYBE");
        check(AiResponseAssessor.Assess(limit, new(limit.Id, wrongMarket, true)).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, new(limit.Id, wrongType, true)).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, new(limit.Id, wrongStatus, true)).Outcome == AiTestOutcome.IncompleteOrInvalid,
            "wrong exact market, entry type, and status are rejected");

        var badNumberType = SetProperty(Response(limit).RawText, "entry_price", "99");
        var badSide = SetProperty(Response(limit).RawText, "side", "BUY");
        var badTimestamp = SetProperty(Response(limit).RawText, "reference_time_utc", "2026-11-08 16:00");
        check(AiResponseAssessor.Assess(limit, new(limit.Id, badNumberType, true)).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, new(limit.Id, badSide, true)).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, new(limit.Id, badTimestamp, true)).Outcome == AiTestOutcome.IncompleteOrInvalid,
            "string prices, unknown side values, and non-UTC timestamps are rejected");

        var zeroPrice = Response(limit, entry: 0m);
        var negativePrice = Response(limit, stopLoss: -1m);
        var badLongOrder = Response(limit, side: "LONG", entry: 100m, takeProfit: 99m, stopLoss: 95m);
        var badShortOrder = Response(limit, side: "SHORT", entry: 100m, takeProfit: 101m, stopLoss: 99m);
        var badMarketEntry = Response(market, entry: 99m);
        check(AiResponseAssessor.Assess(limit, zeroPrice).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, negativePrice).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, badLongOrder).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, badShortOrder).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(market, badMarketEntry).Outcome == AiTestOutcome.IncompleteOrInvalid,
            "positive price ranges and distinct LONG/SHORT price ordering are enforced");

        var missingField = RemoveProperty(Response(limit).RawText, "reason");
        var unknownField = SetProperty(Response(limit).RawText, "extra", "unexpected");
        var twoObjects = Response(limit).RawText + " " + Response(limit).RawText;
        check(AiResponseAssessor.Assess(limit, new(limit.Id, missingField, true)).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, new(limit.Id, unknownField, true)).Outcome == AiTestOutcome.IncompleteOrInvalid
              && AiResponseAssessor.Assess(limit, new(limit.Id, twoObjects, true)).Outcome == AiTestOutcome.IncompleteOrInvalid,
            "missing fields, extra keys, and multiple JSON objects are rejected");

        var stale = RunOne(limit, (_, _) => Task.FromResult(new AiSourceResponse(Guid.NewGuid(), "old raw response", true)));
        var partial = RunOne(limit, (_, _) => Task.FromResult(new AiSourceResponse(limit.Id, "partial raw response", false)));
        check(stale.Outcome == AiTestOutcome.Inconclusive && stale.RawFinalAnswer is null
              && partial.Outcome == AiTestOutcome.Inconclusive && partial.RawFinalAnswer is null,
            "old response identity and incomplete streams are inconclusive and never recorded as final answers");
    }

    private static void CheckBatchOutcomes(Action<bool, string> check)
    {
        var requests = Requests("BTC, ETH, SOL", "Limit");
        var timeout = RunOne(requests[0], (_, _) => Task.FromException<AiSourceResponse>(new TimeoutException()));
        var error = RunOne(requests[1], (_, _) => Task.FromException<AiSourceResponse>(new IOException("fixture error")));
        check(timeout.Outcome == AiTestOutcome.Timeout && error.Outcome == AiTestOutcome.RequestError
              && error.Reasons.All(reason => !reason.Contains("fixture error", StringComparison.Ordinal)),
            "timeout and request error are separate outcomes without copying exception text into the report");

        var pending = new PendingSource();
        using var cancellation = new CancellationTokenSource();
        var run = AiTestBatchRunner.RunAsync(requests, pending, null, cancellation.Token);
        check(SpinWait.SpinUntil(() => pending.Calls == 1, TimeSpan.FromSeconds(2)),
            "sequential batch enters exactly one in-flight request before cancellation");
        cancellation.Cancel();
        var cancelled = run.GetAwaiter().GetResult();
        check(cancelled.Results.Select(result => result.Outcome).SequenceEqual([
                  AiTestOutcome.Cancelled, AiTestOutcome.NotStarted, AiTestOutcome.NotStarted])
              && cancelled.Results.Count == requests.Count
              && Math.Abs(cancelled.ProgressFraction - (1d / 3d)) < 0.0001,
            "mid-flight cancellation records the active request and accounts for all not-started requests");
        CheckAccounting(cancelled, check);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var notStarted = AiTestBatchRunner.RunAsync(requests, pending, null, alreadyCancelled.Token).GetAwaiter().GetResult();
        check(pending.Calls == 1 && notStarted.Count(AiTestOutcome.NotStarted) == requests.Count
              && notStarted.ProgressFraction == 0,
            "pre-cancelled batches record every request as not started and call no source");
        check(notStarted.WasCancelled,
            "a batch cancelled before its first request is still presented as cancelled");
        CheckAccounting(notStarted, check);
    }

    private static void CheckReports(Action<bool, string> check, string fixtureRoot)
    {
        var requests = Requests("BTC, ETH, SOL", "Limit");
        var source = new SyntheticAiAnalysisSource();
        var report = AiTestBatchRunner.RunAsync(requests, source, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        var repeated = AiTestBatchRunner.RunAsync(requests, source, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        var markdown = AiTestReportFormatter.Format(report);
        check(report.Count(AiTestOutcome.CompleteSetup) == 2 && report.Count(AiTestOutcome.ValidNoSetup) == 1
              && report.Results.Count == 3 && markdown.Contains("Всего запросов: **3**", StringComparison.Ordinal)
              && report.ProgressFraction == 1
              && markdown.Contains("reference_time_utc", StringComparison.Ordinal)
              && markdown.Contains("не торговое решение", StringComparison.Ordinal),
            "synthetic Markdown report counts SETUP and NO_SETUP separately with denominator and parsed fields");
        check(report.Results.Select(result => (result.Request.Asset, result.Outcome))
                .SequenceEqual(repeated.Results.Select(result => (result.Request.Asset, result.Outcome))),
            "repeating the same package produces the same synthetic outcome for each asset");

        var retryContents = new List<string>();
        var attempts = 0;
        var retryStore = new AiTestReportStore(Path.Combine(fixtureRoot, "ai-report-retry"), (path, content) =>
        {
            retryContents.Add(content);
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("synthetic first write failure");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return Task.CompletedTask;
        });
        var failed = false;
        try
        {
            retryStore.SaveAsync(report).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            failed = true;
        }
        var retriedPath = retryStore.SaveAsync(report).GetAwaiter().GetResult();
        check(failed && attempts == 2 && retryContents[0] == retryContents[1]
              && File.Exists(retriedPath) && File.ReadAllText(retriedPath).Contains("NO_SETUP", StringComparison.Ordinal),
            "failed report write can retry the same in-memory results into a unique local Markdown file");

        var atomicStore = new AiTestReportStore(Path.Combine(fixtureRoot, "ai-report-atomic"));
        var firstPath = atomicStore.SaveAsync(report).GetAwaiter().GetResult();
        var secondPath = atomicStore.SaveAsync(report).GetAwaiter().GetResult();
        check(Path.GetFileName(firstPath) != Path.GetFileName(secondPath)
              && Path.GetFileName(firstPath).StartsWith("ai-test-report-", StringComparison.Ordinal)
              && File.Exists(firstPath) && File.Exists(secondPath)
              && !Directory.EnumerateFiles(Path.GetDirectoryName(firstPath)!, "*.tmp").Any(),
            "report names are unique and the atomic writer leaves no temporary files");

        var malicious = "```markdown\n| cell | <script>\u0001\n````";
        var hostileReport = new AiTestBatchReport(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new AiTestResult(requests[0], AiTestOutcome.IncompleteOrInvalid, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                malicious, null, AiFreshnessState.Unknown, ["reason | <unsafe>"]) ]);
        var escaped = AiTestReportFormatter.Format(hostileReport);
        check(escaped.Contains("`````text", StringComparison.Ordinal)
              && escaped.Contains("\\u0001", StringComparison.Ordinal)
              && !escaped.Contains('\u0001') && escaped.Contains("&lt;unsafe&gt;", StringComparison.Ordinal),
            "Markdown escapes control text, table delimiters, HTML-like content, and variable code fences");

        check(AiTestReportStore.DefaultDirectory() == Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueNorth TradeFlow", "TestReports"),
            "production report destination is the user's local application data folder");
    }

    internal static void CheckMainWindow(Action<bool, string> check, Application app, string fixtureRoot)
    {
        var artifactDirectory = ArtifactsDirectory();
        Directory.CreateDirectory(artifactDirectory);
        CheckRenderedRun(check, app, fixtureRoot, artifactDirectory);
        CheckCancellationUi(check, app, fixtureRoot);
    }

    private static void CheckRenderedRun(Action<bool, string> check, Application app, string fixtureRoot, string artifactDirectory)
    {
        var settingsPath = Path.Combine(fixtureRoot, "ai-test-window", "settings.json");
        var store = new SettingsStore(settingsPath);
        var profile = store.Load().Profile;
        profile.SelectedMode = "TrueNorth AI setups";
        profile.Settings.Assets = ["BTC", "ETH", "SOL"];
        profile.Settings.EntryType = "Market";
        profile.Settings.PreventSleep = false;
        check(store.Save(profile).Succeeded, "AI test UI starts from an isolated saved profile");
        var settingsBytes = File.ReadAllBytes(settingsPath);
        var session = new NoOpSessionService();
        using var controller = new WakeProtectionController(false, _ => true);
        var source = new RecordingSource(new SyntheticAiAnalysisSource());
        var writeAttempts = 0;
        var retryWriteStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryWriteRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportStore = new AiTestReportStore(artifactDirectory, async (path, content) =>
        {
            if (Interlocked.Increment(ref writeAttempts) == 1)
                throw new IOException("synthetic first write failure");
            retryWriteStarted.TrySetResult(true);
            await retryWriteRelease.Task;
            File.WriteAllText(path, content, new UTF8Encoding(false));
        });
        var window = new MainWindow(store, store.Load(), controller, session, source, reportStore)
        {
            Width = 760,
            Height = 560
        };
        try
        {
            window.Show();
            Get<ListBox>(window, "NavigationList").SelectedIndex = 3;
            PumpDispatcher(app.Dispatcher);
            var page = Get<FrameworkElement>(window, "AiTestPage");
            var input = Get<TextBox>(window, "AiTestAssetsTextBox");
            var entry = Get<ComboBox>(window, "AiTestEntryTypeComboBox");
            var start = Get<Button>(window, "StartAiTestButton");
            var cancel = Get<Button>(window, "CancelAiTestButton");
            var retry = Get<Button>(window, "RetryAiReportButton");
            var open = Get<Button>(window, "OpenAiReportButton");
            var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
            check(page.Visibility == Visibility.Visible
                  && Get<TextBlock>(window, "AiTestRealRunUnavailableText").Text.Contains("Реальные запросы станут доступны", StringComparison.Ordinal)
                  && ((ListBoxItem)Get<ListBox>(window, "ModeList").SelectedItem).Tag as string == "TrueNorth AI setups",
                "AI test page identifies the unavailable real run and preserves the saved operating mode");
            check(start.IsTabStop && cancel.IsTabStop
                  && AutomationProperties.GetName(start).Length > 0 && AutomationProperties.GetName(cancel).Length > 0
                  && start.FocusVisualStyle is not null && cancel.FocusVisualStyle is not null,
                "AI test actions have keyboard stops, accessible names, and visible focus styles");
            var panelColor = ((SolidColorBrush)window.FindResource("PanelBrush")).Color;
            var quietColor = ((SolidColorBrush)window.FindResource("QuietBrush")).Color;
            check(cancel.Background is SolidColorBrush cancelBackground && cancelBackground.Color == panelColor
                  && cancel.Foreground is SolidColorBrush cancelForeground && cancelForeground.Color == quietColor
                  && open.Background is SolidColorBrush openBackground && openBackground.Color == panelColor,
                "disabled AI test actions use the dark application theme and remain visibly disabled");

            input.Text = "BTC-USDC";
            Click(start);
            check(source.Calls == 0 && Get<TextBlock>(window, "AiTestInputErrorText").Text.Length > 0,
                "invalid market input is rejected before source invocation or prompt processing");
            input.Text = "BTC, ETH, SOL";
            entry.SelectedIndex = 0;
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            PumpDispatcher(app.Dispatcher);
            RenderWindow(window, Path.Combine(artifactDirectory, "ai-test-window-760x560-top.png"));
            CheckInViewport(check, input, scroll, "asset input remains visible at 760x560");
            CheckInViewport(check, entry, scroll, "entry type remains visible at 760x560");
            CheckInViewport(check, start, scroll, "primary Start action is visible without scrolling at 760x560");
            CheckInViewport(check, cancel, scroll, "Cancel action is visible without scrolling at 760x560");

            Keyboard.Focus(input);
            PumpDispatcher(app.Dispatcher);
            input.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            PumpDispatcher(app.Dispatcher);
            check(Keyboard.FocusedElement == entry,
                "Tab moves from asset input to entry type in keyboard order");
            entry.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            PumpDispatcher(app.Dispatcher);
            check(Keyboard.FocusedElement == start,
                "Tab moves from entry type to the primary start action");

            var modeBefore = ((ListBoxItem)Get<ListBox>(window, "ModeList").SelectedItem).Tag as string;
            Click(start);
            PumpUntil(() => retry.Visibility == Visibility.Visible && !cancel.IsEnabled, app.Dispatcher);
            var summaryBeforeRetry = Get<TextBlock>(window, "AiTestSummaryText").Text;
            check(source.Calls == 3 && start.IsEnabled && !cancel.IsEnabled
                  && retry.IsEnabled && writeAttempts == 1
                  && Get<ProgressBar>(window, "AiTestProgressBar").Value == 1
                  && Get<TextBlock>(window, "AiTestPathText").Text.Contains("Не удалось сохранить отчёт", StringComparison.Ordinal)
                  && summaryBeforeRetry.Contains("SETUP полностью", StringComparison.Ordinal)
                  && summaryBeforeRetry.Contains("NO_SETUP: 1", StringComparison.Ordinal),
                $"completed synthetic run shows SETUP and NO_SETUP counts and keeps results after report-write failure (calls={source.Calls}, start={start.IsEnabled}, cancel={cancel.IsEnabled}, retry={retry.Visibility}/{retry.IsEnabled}, attempts={writeAttempts}, summary={summaryBeforeRetry})");
            check(Get<TextBlock>(window, "AiTestStatusText").Text == "Синтетический пакет завершён.",
                "queued progress callbacks cannot overwrite the completed batch status");

            Click(retry);
            PumpUntil(() => retryWriteStarted.Task.IsCompleted, app.Dispatcher);
            check(!start.IsEnabled && Get<TextBlock>(window, "AiTestStatusText").Text == "Синтетический пакет завершён.",
                "starting another batch is disabled while a report retry is pending");
            Click(start);
            PumpDispatcher(app.Dispatcher);
            check(source.Calls == 3,
                "a start event during a pending report retry cannot replace the batch being saved");
            retryWriteRelease.TrySetResult(true);
            PumpUntil(() => open.IsEnabled, app.Dispatcher);
            var savedPath = Get<TextBlock>(window, "AiTestPathText").Text["Отчёт сохранён: ".Length..];
            check(writeAttempts == 2 && Get<TextBlock>(window, "AiTestSummaryText").Text == summaryBeforeRetry
                  && File.Exists(savedPath) && modeBefore == ((ListBoxItem)Get<ListBox>(window, "ModeList").SelectedItem).Tag as string
                  && session.OpenCalls == 0 && session.CheckCalls == 0
                  && File.ReadAllBytes(settingsPath).SequenceEqual(settingsBytes),
                "retry saves the same report without persisting temporary input, changing mode, or using the TrueNorth session");
            var savedReport = File.ReadAllText(savedPath);
            check(savedReport.Contains("SETUP: полно по формату и данным", StringComparison.Ordinal)
                  && savedReport.Contains("NO_SETUP: валидный ответ", StringComparison.Ordinal)
                  && savedReport.Contains("данные не отправлялись", StringComparison.Ordinal)
                  && savedReport.Contains("reference_time_utc", StringComparison.Ordinal),
                "saved workspace synthetic report includes both outcomes and the complete parsed schema");

            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            window.UpdateLayout();
            PumpDispatcher(app.Dispatcher);
            CheckInViewport(check, open, scroll, "open report action remains reachable at 760x560");
            RenderWindow(window, Path.Combine(artifactDirectory, "ai-test-window-760x560-actions.png"));
        }
        finally
        {
            window.PrepareForApplicationClose();
            window.AllowApplicationClose();
            window.Close();
        }
    }

    private static void CheckCancellationUi(Action<bool, string> check, Application app, string fixtureRoot)
    {
        var store = new SettingsStore(Path.Combine(fixtureRoot, "ai-test-cancel-window", "settings.json"));
        var session = new NoOpSessionService();
        var source = new PendingSource();
        using var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(store, store.Load(), controller, session, source,
            new AiTestReportStore(Path.Combine(fixtureRoot, "ai-test-cancel-reports")));
        try
        {
            window.Show();
            Get<ListBox>(window, "NavigationList").SelectedIndex = 3;
            var start = Get<Button>(window, "StartAiTestButton");
            var cancel = Get<Button>(window, "CancelAiTestButton");
            var summary = Get<TextBlock>(window, "AiTestSummaryText");
            Get<TextBox>(window, "AiTestAssetsTextBox").Text = "BTC, ETH, SOL";
            Click(start);
            PumpUntil(() => source.Calls == 1, app.Dispatcher);
            Click(start);
            check(source.Calls == 1 && !start.IsEnabled && cancel.IsEnabled,
                "a second start is ignored while one batch is active");
            Click(cancel);
            PumpUntil(() => summary.Text.Contains("не запускались: 2", StringComparison.Ordinal), app.Dispatcher);
            check(summary.Text.Contains("отменены: 1", StringComparison.Ordinal)
                  && summary.Text.Contains("не запускались: 2", StringComparison.Ordinal)
                  && Math.Abs(Get<ProgressBar>(window, "AiTestProgressBar").Value - (1d / 3d)) < 0.0001,
                "cancel action records the active request and both later requests as not started");

            Click(start);
            PumpUntil(() => source.Calls == 2, app.Dispatcher);
            window.PrepareForApplicationClose();
            PumpUntil(() => Get<TextBlock>(window, "AiTestStatusText").Text.Contains("Пакет отменён", StringComparison.Ordinal), app.Dispatcher);
            check(summary.Text.Contains("отменены: 1", StringComparison.Ordinal)
                  && summary.Text.Contains("не запускались: 2", StringComparison.Ordinal)
                  && Math.Abs(Get<ProgressBar>(window, "AiTestProgressBar").Value - (1d / 3d)) < 0.0001
                  && session.OpenCalls == 0 && session.CheckCalls == 0,
                "application-close preparation cancels the active batch without touching the session service");
            source.CompletePending();
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
        }
    }

    private static void CheckAccounting(AiTestBatchReport report, Action<bool, string> check)
    {
        var classified = Enum.GetValues<AiTestOutcome>().Sum(report.Count);
        check(classified == report.Results.Count,
            "all batch outcomes are mutually exclusive and sum to the total request count");
    }

    private static AiTestResult RunOne(AiAnalysisRequest request,
        Func<AiAnalysisRequest, CancellationToken, Task<AiSourceResponse>> analyze)
    {
        var report = AiTestBatchRunner.RunAsync([request], new RecordingSource(analyze), null, CancellationToken.None)
            .GetAwaiter().GetResult();
        return report.Results[0];
    }

    private static AiAnalysisRequest Request(string asset, string entryType) => Requests(asset, entryType)[0];

    private static IReadOnlyList<AiAnalysisRequest> Requests(string assets, string entryType)
    {
        if (!AiAnalysisRequestFactory.TryCreateBatch(assets, entryType, out var requests, out var error))
            throw new InvalidOperationException(error);
        return requests;
    }

    private static AiSourceResponse Response(AiAnalysisRequest request, string side = "LONG", decimal? reference = 100m,
        decimal? entry = null, decimal? takeProfit = 110m, decimal? stopLoss = 95m,
        string? referenceTime = null, string reason = "Synthetic format fixture.", string? invalidation = "Synthetic level breaks.")
    {
        if (entry is null && request.OrderType == "LIMIT")
            entry = 99m;
        return new AiSourceResponse(request.Id, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "SETUP",
            ["market"] = request.Market,
            ["order_type"] = request.OrderType,
            ["side"] = side,
            ["reference_price"] = reference,
            ["reference_time_utc"] = referenceTime,
            ["entry_price"] = entry,
            ["take_profit"] = takeProfit,
            ["stop_loss"] = stopLoss,
            ["reason"] = reason,
            ["invalidation"] = invalidation
        }), true);
    }

    private static AiSourceResponse NoSetup(AiAnalysisRequest request) => new(request.Id,
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "NO_SETUP",
            ["market"] = request.Market,
            ["order_type"] = request.OrderType,
            ["side"] = null,
            ["reference_price"] = null,
            ["reference_time_utc"] = null,
            ["entry_price"] = null,
            ["take_profit"] = null,
            ["stop_loss"] = null,
            ["reason"] = "Synthetic no-setup fixture.",
            ["invalidation"] = null
        }), true);

    private static string SetProperty(string json, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[name] = value?.DeepClone();
        return node.ToJsonString();
    }

    private static string SetProperty(string json, string name, string value) => SetProperty(json, name, JsonValue.Create(value));
    private static string SetProperty(string json, string name, decimal value) => SetProperty(json, name, JsonValue.Create(value));

    private static string RemoveProperty(string json, string name)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(name);
        return node.ToJsonString();
    }

    private static string ArtifactsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "005-ai-test-mode.md")))
                return Path.Combine(directory.FullName, "artifacts", "005-ai-test-mode");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts", "005-ai-test-mode");
    }

    private static void CheckInViewport(Action<bool, string> check, FrameworkElement element, ScrollViewer scroll, string description)
    {
        var topLeft = element.TransformToAncestor(scroll).Transform(new Point(0, 0));
        var bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        check(new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).Contains(bounds), description);
    }

    private static void PumpUntil(Func<bool> condition, Dispatcher dispatcher)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            PumpDispatcher(dispatcher);
            Thread.Sleep(10);
        }
        PumpDispatcher(dispatcher);
        if (!condition())
            throw new InvalidOperationException("WPF AI-test check timed out waiting for the UI state.");
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class RecordingSource : IAiAnalysisSource
    {
        private readonly Func<AiAnalysisRequest, CancellationToken, Task<AiSourceResponse>> _analyze;
        private int _calls;

        internal RecordingSource(IAiAnalysisSource inner)
            : this(inner.AnalyzeAsync)
        {
        }

        internal RecordingSource(Func<AiAnalysisRequest, CancellationToken, Task<AiSourceResponse>> analyze) => _analyze = analyze;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _analyze(request, cancellationToken);
        }
    }

    private sealed class PendingSource : IAiAnalysisSource
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(AiAnalysisRequest Request, TaskCompletionSource<AiSourceResponse> Completion)> _pending = new();
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var completion = new TaskCompletionSource<AiSourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((request, completion));
            return completion.Task;
        }

        internal void CompletePending()
        {
            while (_pending.TryDequeue(out var pending))
                pending.Completion.TrySetResult(Response(pending.Request));
        }
    }

    private sealed class NoOpSessionService : ITrueNorthSessionService
    {
        internal int OpenCalls { get; private set; }
        internal int CheckCalls { get; private set; }

        public Task<TrueNorthSessionResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Task.FromResult(new TrueNorthSessionResult(OpenCalls, TrueNorthSessionState.Unknown, "Fixture only."));
        }

        public Task<TrueNorthSessionResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return Task.FromResult(new TrueNorthSessionResult(CheckCalls, TrueNorthSessionState.Unknown, "Fixture only."));
        }

        public void Close()
        {
        }
    }
}
