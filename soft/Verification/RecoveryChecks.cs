using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Recovery;

namespace TrueNorth.TradeFlow.Verification;

internal static class RecoveryChecks
{
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private const string VenueA = "Scenario Venue A";
    private const string AccountA = "Scenario Account A";
    private const string VenueB = "Scenario Venue B";
    private const string AccountB = "Scenario Account B";

    internal static void CheckCore(Action<bool, string> check, string root)
    {
        CheckJournalPublication(check, root);
        CheckFailedTransferBarrier(check, root);
        CheckOperationDeadlinesAndRestart(check, root);
        CheckRetryDeadlines(check, root);
        CheckReconciliationAndIsolation(check, root);
        CheckFreshnessAfterOperationTransitions(check, root);
        CheckTransferRecovery(check, root);
        CheckConcurrentReads(check, root);
        CheckFailClosedJournal(check, root);
        CheckUnknownScope(check, root);
    }

    internal static void CheckNativeWpf(Action<bool, string> check, string root)
    {
        var settingsPath = Path.Combine(root, "recovery-ui", "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var loaded = settingsStore.Load();
        var clock = new FakeClock(InitialTime);
        var coordinator = NewCoordinator(Path.Combine(root, "recovery-ui", "recovery-state.json"),
            loaded.Profile.Settings, clock);
        coordinator.ReportFailureAsync(VenueA, AccountA, "Синтетическая недоступность.", loaded.Profile.Settings)
            .GetAwaiter().GetResult();

        var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(settingsStore, loaded, controller, recoveryCoordinator: coordinator);
        try
        {
            window.Show();
            window.UpdateLayout();
            var status = (TextBlock)window.FindName("RecoveryStatusText")!;
            check(status.Visibility == Visibility.Visible
                  && status.Text.Contains(VenueA, StringComparison.Ordinal)
                  && status.Text.Contains("заблокированы", StringComparison.Ordinal),
                "the overview shows the real venue pause and entry block while recovery is pending");
            check(Get<TextBlock>(window, "OperatingIntentText").Text.Contains("Сохранённое намерение", StringComparison.Ordinal)
                  && Get<TextBlock>(window, "OperatingIntentText").Text.Contains("не запущен", StringComparison.Ordinal),
                "the recovery status leaves the saved Start or Stop intent and actual monitoring status intact");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
        }
    }

    private static void CheckJournalPublication(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "journal-atomic", "recovery-state.json");
        var settings = Settings(retryCount: 3);
        var clock = new FakeClock(InitialTime);
        var store = new RecoveryJournalStore(path);
        var coordinator = new RecoveryCoordinator(store, settings, clock);
        var episodeId = coordinator.ReportFailureAsync(VenueA, AccountA, "Синтетическая ошибка чтения.", settings)
            .GetAwaiter().GetResult();
        var originalBytes = File.ReadAllBytes(path);
        using (var document = JsonDocument.Parse(originalBytes))
        {
            var rootElement = document.RootElement;
            var savedVenue = rootElement.GetProperty("state").GetProperty("venues")[0];
            check(store.PathName == path && rootElement.GetProperty("formatVersion").GetInt32() == 1
                  && savedVenue.GetProperty("venue").GetString() == VenueA
                  && savedVenue.GetProperty("account").GetString() == AccountA
                  && savedVenue.GetProperty("episodeId").GetString() == episodeId
                  && savedVenue.GetProperty("nextCheckAt").GetDateTimeOffset() == InitialTime.AddSeconds(10),
                "the injected journal path persists the version, exact venue and account keys, episode and next deadline");
        }

        var previousState = store.Load().State;
        var changedState = previousState with
        {
            Venues = [previousState.Venues[0] with { LastReadFailure = "test update" }]
        };
        var faulted = new RecoveryJournalStore(path,
            (_, _, _) => throw new IOException("injected abort before publication"));
        var failed = faulted.Save(changedState);
        check(!failed.Saved && File.ReadAllBytes(path).SequenceEqual(originalBytes)
              && File.ReadAllBytes(faulted.PreviousVersionPath).SequenceEqual(originalBytes)
              && faulted.Load().State.Venues[0].EpisodeId == episodeId,
            "an interrupted write keeps the prior primary state readable and retains its previous version");
        check(!Directory.GetFiles(Path.GetDirectoryName(path)!, "recovery-state.json.tmp.*").Any(),
            "an interrupted publication removes its uncommitted temporary file");

        var updated = store.Save(changedState);
        check(updated.Saved && File.ReadAllBytes(store.PreviousVersionPath).SequenceEqual(originalBytes)
              && store.Load().State.Venues[0].LastReadFailure == "test update",
            "a successful atomic replacement leaves the complete old primary in the backup");
    }

    private static void CheckRetryDeadlines(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "retry-restart", "recovery-state.json");
        var settings = Settings(retryCount: 3);
        var clock = new FakeClock(InitialTime);
        var source = new FakeSource(TimeSpan.FromMinutes(5));
        var coordinator = NewCoordinator(path, settings, clock, source);
        var initialEpisode = coordinator.ReportFailureAsync(VenueA, AccountA, "Синтетическая ошибка чтения.", settings)
            .GetAwaiter().GetResult();
        var initial = coordinator.State.Venues.Single();
        check(initialEpisode is not null && coordinator.StorageReady
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && initial.AdditionalReadsRemaining == 3
              && initial.NextCheckAt == InitialTime.AddSeconds(10),
            $"the first failure immediately pauses its venue and schedules three additional reads ten seconds apart "
            + $"(episode={initialEpisode}, storage={coordinator.StorageStatus}:{coordinator.StorageMessage}, "
            + $"venue={initial.Venue}, account={initial.Account}, phase={initial.Phase}, retries={initial.AdditionalReadsRemaining}, "
            + $"due={initial.NextCheckAt}, expected={InitialTime.AddSeconds(10)}, allowed={coordinator.IsEntryAllowed(VenueA, true)}, "
            + $"ready={coordinator.StorageReady}, episodeOK={initialEpisode is not null})");

        source.Enqueue(VenueA, FailedRead("temporary read failure"));
        clock.UtcNow = initial.NextCheckAt!.Value;
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        var afterFirst = coordinator.State.Venues.Single();
        coordinator = NewCoordinator(path, settings, clock, source);
        var afterRestart = coordinator.State.Venues.Single();
        check(afterFirst.AdditionalReadsRemaining == 2 && afterFirst.AdditionalReadsStarted == 1
              && afterRestart.AdditionalReadsRemaining == 2
              && afterRestart.NextCheckAt == afterFirst.NextCheckAt
              && afterRestart.EpisodeId == initial.EpisodeId,
            $"the attempt counter, episode and next deadline survive a restart (first={afterFirst.AdditionalReadsRemaining}/"
            + $"{afterFirst.AdditionalReadsStarted}/{afterFirst.NextCheckAt}, restart={afterRestart.AdditionalReadsRemaining}/"
            + $"{afterRestart.AdditionalReadsStarted}/{afterRestart.NextCheckAt}, storage={coordinator.StorageStatus}:"
            + $"{coordinator.StorageMessage})");

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            clock.UtcNow = coordinator.State.Venues.Single().NextCheckAt!.Value;
            source.Enqueue(VenueA, FailedRead("temporary read failure"));
            coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        }

        var exhausted = coordinator.State.Venues.Single();
        check(source.CallsFor(VenueA) == 3 && exhausted.AdditionalReadsStarted == 3
              && exhausted.AdditionalReadsRemaining == 0 && exhausted.Phase == RecoveryReadPhase.Periodic
              && exhausted.ErrorEventEmitted && exhausted.NextCheckAt == clock.UtcNow.AddSeconds(60)
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1,
            "three failed additional reads emit one error event and schedule the first 60-second check");

        clock.UtcNow = clock.UtcNow.AddHours(1);
        source.Enqueue(VenueA, FailedRead("still unavailable"));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        var afterOverdue = coordinator.State.Venues.Single();
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(source.CallsFor(VenueA) == 4 && afterOverdue.NextCheckAt == clock.UtcNow.AddSeconds(60)
              && source.CallsFor(VenueA) == 4,
            "an overdue periodic deadline produces one read and schedules from the current time");

        var zeroPath = Path.Combine(root, "retry-zero", "recovery-state.json");
        var zeroSettings = Settings(retryCount: 0);
        var zeroClock = new FakeClock(InitialTime);
        var zeroSource = new FakeSource(TimeSpan.FromMinutes(5));
        var zero = NewCoordinator(zeroPath, zeroSettings, zeroClock, zeroSource);
        zero.ReportFailureAsync(VenueA, AccountA, "Синтетическая ошибка чтения.", zeroSettings).GetAwaiter().GetResult();
        var zeroState = zero.State.Venues.Single();
        check(zeroState.Phase == RecoveryReadPhase.Periodic && zeroState.ErrorEventEmitted
              && zeroState.NextCheckAt == InitialTime.AddSeconds(60)
              && zero.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1,
            "zero additional reads immediately emits one error event and starts the periodic interval");

        zeroClock.UtcNow = zeroState.NextCheckAt!.Value;
        zeroSource.Enqueue(VenueA, CompleteRead(zeroClock.UtcNow, VenueA, AccountA));
        zero.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(zeroSource.CallsFor(VenueA) == 1
              && zero.State.Venues.Single().Phase == RecoveryReadPhase.Resolved
              && zero.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1
              && zero.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 1,
            "the first zero-retry periodic read can reconcile and emits one recovery event");
    }

    private static void CheckFailedTransferBarrier(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "transfer-barrier-write-failure", "recovery-state.json");
        var commits = 0;
        var store = new RecoveryJournalStore(path, (temporary, destination, _) =>
        {
            if (Interlocked.Increment(ref commits) == 1)
                File.Move(temporary, destination);
            else
                throw new IOException("injected durable-boundary failure");
        });
        var settings = Settings();
        var coordinator = new RecoveryCoordinator(store, settings, new FakeClock(InitialTime));
        var request = new RecoveryOperationRequest("barrier-operation", VenueA, AccountA,
            "BTC-PERP", "PlaceOpeningOrder");
        var prepared = coordinator.PrepareOperationAsync(request, savedOperatingIntent: true).GetAwaiter().GetResult();
        var transmissionCalls = 0;
        var began = coordinator.BeginTransferAsync(request.OperationId, savedOperatingIntent: true).GetAwaiter().GetResult();
        if (began)
            transmissionCalls++;

        check(prepared && !began && transmissionCalls == 0 && !coordinator.StorageReady
              && new RecoveryJournalStore(path).Load().State.Operations.Single().Stage == RecoveryOperationStage.Prepared,
            "a failed durable transfer boundary returns before any external transmission and preserves the prepared record");
    }

    private static void CheckOperationDeadlinesAndRestart(Action<bool, string> check, string root)
    {
        var settings = Settings();
        var expiredClock = new FakeClock(InitialTime);
        var expiredPath = Path.Combine(root, "operation-deadlines", "expired", "recovery-state.json");
        var expired = NewCoordinator(expiredPath, settings, expiredClock);
        var alreadyExpired = new RecoveryOperationRequest("expired-before-prepare", VenueA, AccountA,
            "BTC-PERP", "PlaceOpeningOrder", InitialTime);
        check(!expired.PrepareOperationAsync(alreadyExpired, savedOperatingIntent: true).GetAwaiter().GetResult()
              && expired.State.Operations.Count == 0,
            "an operation whose deadline has already passed is rejected before it is journaled");

        var request = new RecoveryOperationRequest("expired-before-transfer", VenueA, AccountA,
            "BTC-PERP", "PlaceOpeningOrder", InitialTime.AddSeconds(5));
        var prepared = expired.PrepareOperationAsync(request, savedOperatingIntent: true).GetAwaiter().GetResult();
        expiredClock.UtcNow = request.DeadlineAt!.Value;
        var began = expired.BeginTransferAsync(request.OperationId, savedOperatingIntent: true).GetAwaiter().GetResult();
        var abandoned = expired.State.Operations.Single();
        check(prepared && !began && abandoned.Stage == RecoveryOperationStage.Abandoned
              && abandoned.TransferStartedAt is null && abandoned.CompletedAt == expiredClock.UtcNow
              && !expired.BeginTransferAsync(request.OperationId, savedOperatingIntent: true).GetAwaiter().GetResult(),
            "an expired prepared operation is durably abandoned before the transfer boundary and cannot begin later");

        var restartPath = Path.Combine(root, "operation-deadlines", "restart", "recovery-state.json");
        var restartClock = new FakeClock(InitialTime);
        var beforeRestart = NewCoordinator(restartPath, settings, restartClock);
        var unsent = new RecoveryOperationRequest("prepared-before-restart", VenueB, AccountB,
            "ETH-PERP", "PlaceOpeningOrder", InitialTime.AddMinutes(5));
        var preparedBeforeRestart = beforeRestart.PrepareOperationAsync(unsent, savedOperatingIntent: true)
            .GetAwaiter().GetResult();
        restartClock.UtcNow = InitialTime.AddSeconds(1);
        var afterRestart = NewCoordinator(restartPath, settings, restartClock);
        var restored = afterRestart.State.Operations.Single();
        check(preparedBeforeRestart && restored.Stage == RecoveryOperationStage.Abandoned
              && restored.TransferStartedAt is null && restored.CompletedAt == restartClock.UtcNow
              && !afterRestart.BeginTransferAsync(unsent.OperationId, savedOperatingIntent: true).GetAwaiter().GetResult(),
            "restart abandons every unsent prepared intent instead of carrying it into a transfer");

        var stoppedRequest = new RecoveryOperationRequest("stopped-intent", VenueA, AccountA,
            "BTC-PERP", "PlaceOpeningOrder");
        check(!afterRestart.PrepareOperationAsync(stoppedRequest, savedOperatingIntent: false).GetAwaiter().GetResult()
              && !afterRestart.BeginTransferAsync(unsent.OperationId, savedOperatingIntent: false).GetAwaiter().GetResult(),
            "saved Stop still blocks preparation and transfer after recovery restart");
    }

    private static void CheckReconciliationAndIsolation(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "reconcile-validation", "recovery-state.json");
        var settings = Settings(retryCount: 0);
        var clock = new FakeClock(InitialTime);
        var source = new FakeSource(TimeSpan.FromMinutes(5));
        var coordinator = NewCoordinator(path, settings, clock, source);
        coordinator.ReportFailureAsync(VenueA, AccountA, "Синтетическая ошибка чтения.", settings).GetAwaiter().GetResult();

        var stale = CompleteRead(clock.UtcNow.AddMinutes(-6), VenueA, AccountA);
        var full = CompleteRead(clock.UtcNow, VenueA, AccountA);
        var partial = full with
        {
            Account = full.Account! with
            {
                Positions = AccountSection<IReadOnlyList<OpenPosition>>.Incomplete(
                    [.. full.Account.Positions.Value!], clock.UtcNow, "recovery fixture", "строка не прочитана.")
            }
        };
        var changedAccount = CompleteRead(clock.UtcNow, VenueA, "Changed Account");
        var lostSession = full with { SessionAvailable = false, FailureReason = "session lost" };
        var unknownProtection = full with { ProtectionCoverageComplete = false };
        var unknownOperations = full with { UnfinishedOperationsComplete = false };
        var invalidReads = new[] { partial, changedAccount, lostSession, unknownProtection, unknownOperations, stale };
        foreach (var invalid in invalidReads)
        {
            var dueAt = coordinator.State.Venues.Single().NextCheckAt!.Value;
            clock.UtcNow = dueAt;
            source.Enqueue(VenueA, invalid);
            coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
            check(!coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
                  && coordinator.State.Venues.Single().Phase == RecoveryReadPhase.Periodic
                  && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 0,
                "partial, changed-account, lost-session, incomplete-protection, incomplete-operation, and stale reads keep the venue paused");
        }

        clock.UtcNow = coordinator.State.Venues.Single().NextCheckAt!.Value;
        source.Enqueue(VenueA, CompleteRead(clock.UtcNow, VenueA, AccountA));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        var recovered = coordinator.State.Venues.Single();
        check(recovered.Phase == RecoveryReadPhase.Resolved
              && coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 1,
            "only a fresh full snapshot for the original account clears the pause and records one recovery event");

        var isolationPath = Path.Combine(root, "venue-isolation", "recovery-state.json");
        var isolationClock = new FakeClock(InitialTime);
        var isolationSource = new FakeSource(TimeSpan.FromMinutes(5));
        var isolation = NewCoordinator(isolationPath, settings, isolationClock, isolationSource);
        isolation.ReportFailureAsync(VenueA, AccountA, "A unavailable", settings).GetAwaiter().GetResult();
        isolation.ReportFailureAsync(VenueB, AccountB, "B unavailable", settings).GetAwaiter().GetResult();
        isolationClock.UtcNow = InitialTime.AddSeconds(60);
        isolationSource.Enqueue(VenueA, FailedRead("A incomplete"));
        isolationSource.Enqueue(VenueB, CompleteRead(isolationClock.UtcNow, VenueB, AccountB));
        isolation.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        isolation.CheckDueAsync(VenueB).GetAwaiter().GetResult();
        check(!isolation.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && isolation.IsEntryAllowed(VenueB, savedOperatingIntent: true)
              && isolation.State.Venues.Single(venue => venue.Venue == VenueA).Phase == RecoveryReadPhase.Periodic
              && isolation.State.Venues.Single(venue => venue.Venue == VenueB).Phase == RecoveryReadPhase.Resolved,
            "a venue with a failed read stays paused while a different venue reconciles independently");
    }

    private static void CheckTransferRecovery(Action<bool, string> check, string root)
    {
        var immediateSettings = Settings(retryCount: 0);
        var immediateClock = new FakeClock(InitialTime);
        var immediate = NewCoordinator(Path.Combine(root, "transfer-outcomes", "recovery-state.json"),
            immediateSettings, immediateClock);
        var uncertainRequest = new RecoveryOperationRequest("uncertain-outcome", VenueA, AccountA,
            "BTC-PERP", "PlaceOpeningOrder");
        var confirmedRequest = new RecoveryOperationRequest("confirmed-outcome", VenueB, AccountB,
            "ETH-PERP", "CancelOpeningOrder");
        var uncertainPrepared = immediate.PrepareOperationAsync(uncertainRequest, true).GetAwaiter().GetResult();
        var uncertainStarted = immediate.BeginTransferAsync(uncertainRequest.OperationId, true).GetAwaiter().GetResult();
        var uncertainRecorded = immediate.CompleteTransferAsync(uncertainRequest.OperationId,
            RecoveryTransferOutcome.Unknown, immediateSettings, "synthetic timeout").GetAwaiter().GetResult();
        var confirmedPrepared = immediate.PrepareOperationAsync(confirmedRequest, true).GetAwaiter().GetResult();
        var confirmedStarted = immediate.BeginTransferAsync(confirmedRequest.OperationId, true).GetAwaiter().GetResult();
        var confirmedRecorded = immediate.CompleteTransferAsync(confirmedRequest.OperationId,
            RecoveryTransferOutcome.Confirmed, immediateSettings).GetAwaiter().GetResult();
        check(uncertainPrepared && uncertainStarted && uncertainRecorded
              && immediate.State.Operations.Single(operation => operation.OperationId == uncertainRequest.OperationId)
                  .Stage == RecoveryOperationStage.Unknown
              && !immediate.IsEntryAllowed(VenueA, true)
              && immediate.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1
              && confirmedPrepared && confirmedStarted && confirmedRecorded
              && immediate.State.Operations.Single(operation => operation.OperationId == confirmedRequest.OperationId)
                  .Stage == RecoveryOperationStage.Confirmed
              && immediate.IsEntryAllowed(VenueB, true),
            "confirmed transfer outcomes remain terminal while an unknown outcome immediately pauses only its venue");

        var operationId = "operation-008-1";
        var settingsPath = Path.Combine(root, "transfer", "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        var profile = new SettingsProfile
        {
            SelectedMode = "TrueNorth AI setups",
            SavedOperatingIntent = true,
            Settings = Settings(retryCount: 0)
        };
        check(settingsStore.Save(profile).Succeeded, "saved operating mode and intent fixture is prepared");

        var path = Path.Combine(root, "transfer", "recovery-state.json");
        var clock = new FakeClock(InitialTime);
        var source = new FakeSource(TimeSpan.FromMinutes(5));
        var coordinator = NewCoordinator(path, settingsStore.Load().Profile.Settings, clock, source);
        var request = new RecoveryOperationRequest(operationId, VenueA, AccountA, "BTC-PERP", "PlaceOpeningOrder",
            InitialTime.AddMinutes(2));
        var prepared = coordinator.PrepareOperationAsync(request, savedOperatingIntent: true).GetAwaiter().GetResult();
        var started = coordinator.BeginTransferAsync(operationId, savedOperatingIntent: true).GetAwaiter().GetResult();
        var durable = new RecoveryJournalStore(path).Load().State.Operations.Single();
        coordinator = NewCoordinator(path, settingsStore.Load().Profile.Settings, clock, source);
        var recovered = coordinator.State.Operations.Single();
        var stillStoppedByGate = !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: false);
        var stoppedProfile = settingsStore.Load().Profile;
        stoppedProfile.SavedOperatingIntent = false;
        var stopSaved = settingsStore.Save(stoppedProfile).Succeeded;

        check(prepared && started && durable.Venue == VenueA && durable.Account == AccountA
              && durable.Market == "BTC-PERP" && durable.Operation == "PlaceOpeningOrder"
              && durable.DeadlineAt == InitialTime.AddMinutes(2)
              && durable.Stage == RecoveryOperationStage.TransferStarted
              && recovered.Stage == RecoveryOperationStage.Unknown
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && stillStoppedByGate && stopSaved
              && !settingsStore.Load().Profile.SavedOperatingIntent
              && settingsStore.Load().Profile.SelectedMode == "TrueNorth AI setups",
            "a durable transfer boundary restores as unknown, pauses the exact account, and leaves saved Stop and mode intact");

        check(!coordinator.BeginTransferAsync(operationId, savedOperatingIntent: true).GetAwaiter().GetResult(),
            "an unknown in-flight operation cannot be submitted again after restart");

        var dueAt = coordinator.State.Venues.Single().NextCheckAt!.Value;
        clock.UtcNow = dueAt;
        source.Enqueue(VenueA, CompleteRead(clock.UtcNow, VenueA, AccountA,
            new Dictionary<string, RecoveryOperationObservation>
            {
                [operationId] = new(operationId, VenueA, AccountA, "OTHER-PERP", "PlaceOpeningOrder",
                    RecoveryOperationOutcome.Confirmed)
            }));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(coordinator.State.Operations.Single().Stage == RecoveryOperationStage.Unknown
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1,
            "an operation result with a mismatched market does not clear the pause");

        dueAt = coordinator.State.Venues.Single().NextCheckAt!.Value;
        clock.UtcNow = dueAt;
        source.Enqueue(VenueA, CompleteRead(clock.UtcNow, VenueA, AccountA,
            new Dictionary<string, RecoveryOperationObservation>
            {
                [operationId] = new(operationId, VenueA, AccountA, "BTC-PERP", "PlaceOpeningOrder",
                    RecoveryOperationOutcome.Unknown)
            }));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(coordinator.State.Operations.Single().Stage == RecoveryOperationStage.Unknown
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true),
            "a full account read with an unknown operation outcome does not clear the pause");

        dueAt = coordinator.State.Venues.Single().NextCheckAt!.Value;
        clock.UtcNow = dueAt;
        source.Enqueue(VenueA, CompleteRead(clock.UtcNow, VenueA, AccountA,
            new Dictionary<string, RecoveryOperationObservation>
            {
                [operationId] = new(operationId, VenueA, AccountA, "BTC-PERP", "PlaceOpeningOrder",
                    RecoveryOperationOutcome.Confirmed)
            }));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(coordinator.State.Operations.Single().Stage == RecoveryOperationStage.Reconciled
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: false)
              && !coordinator.BeginTransferAsync(operationId, savedOperatingIntent: true).GetAwaiter().GetResult()
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 1,
            "confirmed reconciliation ends the pause once, preserves Stop, and never reopens the same operation id");
    }

    private static void CheckFreshnessAfterOperationTransitions(Action<bool, string> check, string root)
    {
        var settings = Settings(retryCount: 0);
        var clock = new FakeClock(InitialTime);
        var source = new FakeSource(TimeSpan.FromMinutes(5));
        var coordinator = NewCoordinator(Path.Combine(root, "freshness-after-transfer", "recovery-state.json"),
            settings, clock, source);
        var request = new RecoveryOperationRequest("freshness-operation", VenueA, AccountA,
            "BTC-PERP", "PlaceOpeningOrder");
        var prepared = coordinator.PrepareOperationAsync(request, savedOperatingIntent: true).GetAwaiter().GetResult();
        var began = coordinator.BeginTransferAsync(request.OperationId, savedOperatingIntent: true).GetAwaiter().GetResult();

        clock.UtcNow = InitialTime.AddSeconds(1);
        var episodeId = coordinator.ReportFailureAsync(VenueA, AccountA, "synthetic failure", settings)
            .GetAwaiter().GetResult();
        clock.UtcNow = InitialTime.AddSeconds(2);
        var completed = coordinator.CompleteTransferAsync(request.OperationId, RecoveryTransferOutcome.Unknown,
            settings, "synthetic completion detail").GetAwaiter().GetResult();
        var observation = new Dictionary<string, RecoveryOperationObservation>(StringComparer.Ordinal)
        {
            [request.OperationId] = new(request.OperationId, VenueA, AccountA, request.Market,
                request.Operation, RecoveryOperationOutcome.Confirmed)
        };

        clock.UtcNow = InitialTime.AddSeconds(61);
        source.Enqueue(VenueA, CompleteRead(InitialTime.AddSeconds(1.5), VenueA, AccountA, observation));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        var afterCachedRead = coordinator.State.Venues.Single();
        check(prepared && began && episodeId is not null && completed
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && afterCachedRead.Phase == RecoveryReadPhase.Periodic
              && afterCachedRead.LastReadFailure is not null,
            "a post-failure cache from before transfer completion cannot clear the venue pause");

        clock.UtcNow = afterCachedRead.NextCheckAt!.Value;
        source.Enqueue(VenueA, CompleteRead(clock.UtcNow, VenueA, AccountA, observation));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(coordinator.State.Venues.Single().Phase == RecoveryReadPhase.Resolved
              && coordinator.State.Operations.Single().Stage == RecoveryOperationStage.Reconciled
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 1
              && !coordinator.BeginTransferAsync(request.OperationId, savedOperatingIntent: true).GetAwaiter().GetResult(),
            "a later full read after operation completion reconciles once and does not authorize repeating the operation");
    }

    private static void CheckConcurrentReads(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "read-serialization", "recovery-state.json");
        var settings = Settings(retryCount: 0);
        var clock = new FakeClock(InitialTime);
        var source = new FakeSource(TimeSpan.FromMinutes(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetHandler(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return FailedRead("temporary read failure");
        });
        var coordinator = NewCoordinator(path, settings, clock, source);
        coordinator.ReportFailureAsync(VenueA, AccountA, "A unavailable", settings).GetAwaiter().GetResult();
        clock.UtcNow = InitialTime.AddSeconds(60);
        var first = coordinator.CheckDueAsync(VenueA);
        started.Task.GetAwaiter().GetResult();
        var second = coordinator.CheckDueAsync(VenueA);
        release.TrySetResult();
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        check(source.CallsFor(VenueA) == 1 && source.MaximumConcurrent == 1
              && coordinator.State.Venues.Single().NextCheckAt == clock.UtcNow.AddSeconds(60),
            "one venue runs at most one read at a time and an overdue call is not replayed in a burst");
    }

    private static void CheckFailClosedJournal(Action<bool, string> check, string root)
    {
        var settings = Settings();
        foreach (var fixture in new[]
                 {
                     (Name: "corrupt", Bytes: "{ broken"u8.ToArray(), Expected: RecoveryJournalStatus.Corrupt),
                     (Name: "unknown-version", Bytes: "{\"formatVersion\":99,\"state\":{}}"u8.ToArray(), Expected: RecoveryJournalStatus.UnsupportedVersion)
                 })
        {
            var path = Path.Combine(root, "journal-status", fixture.Name, "recovery-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, fixture.Bytes);
            var before = File.ReadAllBytes(path);
            var coordinator = NewCoordinator(path, settings, new FakeClock(InitialTime));
            check(!coordinator.StorageReady && coordinator.StorageStatus == fixture.Expected
                  && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
                  && coordinator.StatusText.Contains("заблокированы", StringComparison.Ordinal)
                  && File.ReadAllBytes(path).SequenceEqual(before),
                $"{fixture.Name} recovery data blocks dependent actions and preserves the source file");
        }

        var backupOnlyPath = Path.Combine(root, "journal-status", "backup-only", "recovery-state.json");
        var backupOnlyStore = new RecoveryJournalStore(backupOnlyPath);
        var backupOnlyDirectory = Path.GetDirectoryName(backupOnlyPath)!;
        Directory.CreateDirectory(backupOnlyDirectory);
        File.WriteAllText(backupOnlyStore.PreviousVersionPath,
            "{\"formatVersion\":1,\"state\":{\"venues\":[],\"operations\":[],\"events\":[]}}");
        var backupBytes = File.ReadAllBytes(backupOnlyStore.PreviousVersionPath);
        var backupOnly = NewCoordinator(backupOnlyPath, settings, new FakeClock(InitialTime));
        check(backupOnly.StorageStatus == RecoveryJournalStatus.MissingWithPreviousVersion
              && !backupOnly.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && File.ReadAllBytes(backupOnlyStore.PreviousVersionPath).SequenceEqual(backupBytes),
            "a previous version cannot silently replace a missing primary or clear reconciliation obligations");
    }

    private static void CheckUnknownScope(Action<bool, string> check, string root)
    {
        var source = new FakeSource(TimeSpan.FromMinutes(5));
        var settings = Settings(retryCount: 3);
        var clock = new FakeClock(InitialTime);
        var unknownPath = Path.Combine(root, "unknown-scope", "recovery-state.json");
        var coordinator = NewCoordinator(unknownPath, settings, clock, source);
        var episodeId = coordinator.ReportFailureAsync(null, null, "private internal failure detail", settings)
            .GetAwaiter().GetResult();
        var plainReport = coordinator.ReportFailureAsync(VenueA, AccountA, "second private detail", settings)
            .GetAwaiter().GetResult();
        var initialEvent = new RecoveryJournalStore(unknownPath).Load().State.Events.Single();
        check(!coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && !coordinator.IsEntryAllowed(VenueB, savedOperatingIntent: true)
              && coordinator.State.Venues.Single().Phase == RecoveryReadPhase.UnknownScope
              && plainReport == episodeId && coordinator.State.Venues.Count == 1
              && initialEvent.Type == RecoveryEventType.Error
              && initialEvent.SafeSummary != initialEvent.InternalDetail
              && !initialEvent.SafeSummary.Contains("private", StringComparison.Ordinal)
              && !initialEvent.Message.Contains("private", StringComparison.Ordinal)
              && initialEvent.InternalDetail == "private internal failure detail"
              && !coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult()
              && source.Calls == 0,
            "unknown scope blocks every venue, keeps one error with separated safe text, and ordinary reports do not refine it");

        check(!coordinator.RefineUnknownScopeAsync("wrong-episode", new(VenueA, AccountA)).GetAwaiter().GetResult()
              && coordinator.State.Venues.Single().Venue == "*"
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true),
            "a wrong episode cannot refine the wildcard pause");

        check(coordinator.RefineUnknownScopeAsync(episodeId!, new(VenueA, AccountA)).GetAwaiter().GetResult()
              && coordinator.State.Venues.Single().EpisodeId == episodeId
              && coordinator.State.Venues.Single().Venue == VenueA
              && coordinator.State.Venues.Single().Account == AccountA
              && !coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && coordinator.IsEntryAllowed(VenueB, savedOperatingIntent: true)
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1,
            "explicit trusted refinement preserves the episode and error, pauses the exact target, and releases unrelated venues");

        clock.UtcNow = coordinator.State.Venues.Single().NextCheckAt!.Value;
        source.Enqueue(VenueA, CompleteRead(clock.UtcNow, VenueA, AccountA));
        coordinator.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(coordinator.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && coordinator.State.Venues.Single().Phase == RecoveryReadPhase.Resolved
              && coordinator.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 1,
            "the refined venue resumes only after a fresh complete account read");

        var venueClock = new FakeClock(InitialTime);
        var venueSource = new FakeSource(TimeSpan.FromMinutes(5));
        var venueScope = NewCoordinator(Path.Combine(root, "unknown-account", "recovery-state.json"),
            settings, venueClock, venueSource);
        var venueEpisode = venueScope.ReportFailureAsync(VenueA, null, "account unavailable", settings)
            .GetAwaiter().GetResult();
        check(!venueScope.RefineUnknownScopeAsync(venueEpisode!, new(VenueB, AccountB)).GetAwaiter().GetResult()
              && venueScope.State.Venues.Single().Venue == VenueA
              && venueScope.State.Venues.Single().Phase == RecoveryReadPhase.UnknownScope
              && !venueScope.IsEntryAllowed(VenueA, savedOperatingIntent: true),
            "an unknown account cannot be refined to a different venue or clear the original venue pause");

        check(venueScope.RefineUnknownScopeAsync(venueEpisode!, new(VenueA, AccountA)).GetAwaiter().GetResult()
              && venueScope.State.Venues.Single().Venue == VenueA
              && venueScope.State.Venues.Single().Account == AccountA
              && !venueScope.IsEntryAllowed(VenueA, savedOperatingIntent: true),
            "refining an unknown account replaces its scope while keeping the affected venue paused");
        venueClock.UtcNow = venueScope.State.Venues.Single().NextCheckAt!.Value;
        venueSource.Enqueue(VenueA, CompleteRead(venueClock.UtcNow, VenueA, AccountA));
        venueScope.CheckDueAsync(VenueA).GetAwaiter().GetResult();
        check(venueScope.IsEntryAllowed(VenueA, savedOperatingIntent: true)
              && venueScope.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Error) == 1
              && venueScope.State.Events.Count(recoveryEvent => recoveryEvent.Type == RecoveryEventType.Recovered) == 1,
            "a venue with a refined unknown account resumes only after its full account read");
    }

    private static RecoveryCoordinator NewCoordinator(
        string path,
        AppSettings settings,
        FakeClock clock,
        FakeSource? source = null) => new(new RecoveryJournalStore(path), settings, clock, source);

    private static AppSettings Settings(int retryCount = 3, int retryDelaySeconds = 10, int recoveryCheckSeconds = 60) => new()
    {
        ReadRetryCount = retryCount,
        ReadRetryDelaySeconds = retryDelaySeconds,
        RecoveryCheckSeconds = recoveryCheckSeconds
    };

    private static RecoverySnapshotRead FailedRead(string reason) =>
        new(false, null, false, false, false, FailureReason: reason);

    private static RecoverySnapshotRead CompleteRead(
        DateTimeOffset observedAt,
        string venue,
        string account,
        IReadOnlyDictionary<string, RecoveryOperationObservation>? operations = null) =>
        new(true,
            new AccountSnapshot(venue, account, "USDC cross", observedAt, "recovery fixture", "USDC", "BTC",
                AccountReadState.Available, null, observedAt,
                AccountSection<AccountBalance>.Available(
                    new AccountBalance(new MoneyAmount(1000m, "USDC"), new MoneyAmount(1000m, "USDC"),
                        new MoneyAmount(900m, "USDC")), observedAt, "recovery fixture"),
                AccountSection<IReadOnlyList<OpenPosition>>.Available(
                    [new OpenPosition("BTC", "BTC-PERP", PositionDirection.Long, 1m, "BTC", null,
                        PositionOwnership.Unknown, "fixture")], observedAt, "recovery fixture"),
                AccountSection<IReadOnlyList<OpenOrder>>.Available(
                    [new OpenOrder("ETH", "ETH-PERP", PositionDirection.Long, 2m, 0.5m, "ETH",
                        "Partially filled", PositionOwnership.Unknown, "opening remainder")], observedAt, "recovery fixture"),
                AccountSection<IReadOnlyList<PositionProtection>>.Available(
                    [new PositionProtection("BTC-PERP", new MoneyAmount(120m, "USDC"),
                        new MoneyAmount(90m, "USDC"))], observedAt, "recovery fixture"),
                AccountSection<IReadOnlyList<ClosedTrade>>.NotRead("history not needed for reconciliation")),
            OpeningOrderRemaindersComplete: true,
            ProtectionCoverageComplete: true,
            UnfinishedOperationsComplete: true,
            OperationOutcomes: operations ?? new Dictionary<string, RecoveryOperationObservation>());

    private static T Get<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing WPF control '{name}'.");

    private sealed class FakeClock(DateTimeOffset now) : IRecoveryClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeSource(TimeSpan maximumSnapshotAge) : IRecoverySnapshotSource
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<RecoverySnapshotRead>> _reads = new(StringComparer.Ordinal);
        private Func<string, string, Task<RecoverySnapshotRead>>? _handler;
        private int _calls;
        private int _active;
        private int _maximumConcurrent;

        public TimeSpan MaximumSnapshotAge { get; } = maximumSnapshotAge;
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);

        public int CallsFor(string venue) => _counts.TryGetValue(venue, out var count) ? count : 0;
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void Enqueue(string venue, RecoverySnapshotRead read) =>
            _reads.GetOrAdd(venue, _ => new ConcurrentQueue<RecoverySnapshotRead>()).Enqueue(read);

        public void SetHandler(Func<string, string, Task<RecoverySnapshotRead>> handler) => _handler = handler;

        public async Task<RecoverySnapshotRead> ReadAsync(
            string venue,
            string account,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _counts.AddOrUpdate(venue, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrent);
                if (observed >= active)
                    break;
            } while (Interlocked.CompareExchange(ref _maximumConcurrent, active, observed) != observed);

            try
            {
                if (_handler is not null)
                    return await _handler(venue, account).ConfigureAwait(false);
                return _reads.TryGetValue(venue, out var queue) && queue.TryDequeue(out var read)
                    ? read
                    : FailedRead("fixture response missing");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
