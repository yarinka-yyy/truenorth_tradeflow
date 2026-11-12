using System.Globalization;
using System.IO;
using TrueNorth.TradeFlow;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Orders;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Trading;

internal sealed record TradingLoopRoute(string Asset, string Venue, string Account, string Contract);

internal sealed record TradingLoopContext(
    bool RouteAvailable,
    bool? AllConnectedExposureCovered,
    EntryEligibilityInput? EligibilityInput = null,
    string? Reason = null,
    bool? MinimumEntryAffordable = null);

internal sealed record TradingLoopReassessmentContext(
    AiAnalysisRequest Request,
    AiSourceResponse OriginResponse,
    AiParsedFields OriginSetup,
    DateTimeOffset ReceivedAt);

internal interface ITradingLoopReassessmentSource
{
    Task<AiSourceResponse> ReassessAsync(
        TradingLoopReassessmentContext context,
        CancellationToken cancellationToken);
}

internal interface ITradingLoopContextSource
{
    IReadOnlyList<TradingLoopRoute> GetRoutes(SettingsProfile profile);

    Task<TradingLoopContext> ReadAsync(
        TradingLoopRoute route,
        IReadOnlyList<AccountSnapshot> accounts,
        EntryEligibilitySetup? setup,
        SettingsProfile profile,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed record TradingLoopOutcome(bool Succeeded, string Status, string? Asset = null, string? IntentId = null);

internal sealed class TradingLoopCoordinator
{
    private static readonly TimeSpan PendingSetupLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan PendingEligibilityRetryMinimum = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingEligibilityRetryMaximum = TimeSpan.FromMinutes(5);
    private readonly SettingsStore _settingsStore;
    private readonly RecoveryCoordinator _recovery;
    private readonly OrderLifecycleCoordinator _orders;
    private readonly IAiAnalysisSource _analysis;
    private readonly ITradingLoopContextSource _contexts;
    private readonly IRecoveryClock _clock;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _activeCycleGate = new();
    private readonly TradingLoopAdmission _admission = new();
    private SettingsProfile _profile;
    private TradingLoopState _state;
    private CancellationTokenSource? _activeCycle;
    private int _profileUpdateInProgress;
    private int _stopInProgress;
    private int _exitHold;
    private bool _pendingSetupLoadedAtStartup;
    private string? _lastStatus;

    internal TradingLoopCoordinator(
        SettingsStore settingsStore,
        SettingsProfile profile,
        RecoveryCoordinator recovery,
        OrderLifecycleCoordinator orders,
        IAiAnalysisSource analysis,
        ITradingLoopContextSource contexts,
        IRecoveryClock clock)
    {
        _settingsStore = settingsStore;
        _profile = SettingsValidator.Validate(profile).Profile;
        _recovery = recovery;
        _orders = orders;
        _analysis = analysis;
        _contexts = contexts;
        _clock = clock;
        _state = recovery.TradingLoopState;
        _pendingSetupLoadedAtStartup = _state.PendingSetup is not null;
        _lastStatus = _state.LastStatus;
        _recovery.UpdateSettings(_profile.Settings);
    }

    internal bool IsRunning => _admission.IsStarted;
    internal bool IsAdmitting => Volatile.Read(ref _profileUpdateInProgress) == 0
        && Volatile.Read(ref _stopInProgress) == 0 && _admission.IsAdmitting;
    internal bool IsExitHeld => Volatile.Read(ref _exitHold) != 0;
    internal SettingsProfile Profile => Volatile.Read(ref _profile).Copy();
    internal string Status => _lastStatus ?? _recovery.StatusText;
    internal TradingLoopState State => _recovery.TradingLoopState;

    internal void CloseAdmissionForShutdown()
    {
        Interlocked.Exchange(ref _exitHold, 1);
        _admission.Close();
        CancelActiveCycle();
    }

    internal async Task HoldAdmissionForExitAsync()
    {
        CloseAdmissionForShutdown();
        await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _cycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _cycleGate.Release();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<TradingLoopOutcome> ResumeAfterExitAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsExitHeld)
                return SetStatus(false, "Проверка выхода уже завершена.");
            await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var profile = Volatile.Read(ref _profile).Copy();
                if (!await ReconcileAsync(profile, cancellationToken).ConfigureAwait(false))
                    return SetStatus(false, _lastStatus ?? "После выхода не удалось завершить сверку.");

                if (profile.SavedOperatingIntent && profile.SelectedMode == "TrueNorth AI setups"
                    && !profile.IsIncomplete())
                {
                    var generation = _admission.Generation;
                    if (!_admission.Open(generation))
                        return SetStatus(false, "Намерение Start не восстановлено после сверки выхода.");
                }
                Interlocked.Exchange(ref _exitHold, 0);
                return SetStatus(true, profile.SavedOperatingIntent
                    ? "Сверка выхода завершена; сохранённое намерение Start восстановлено."
                    : "Сверка выхода завершена; сохранённое намерение Stop сохранено.");
            }
            finally
            {
                _cycleGate.Release();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<bool> UpdateProfileAsync(
        SettingsProfile profile,
        CancellationToken cancellationToken = default)
    {
        var validation = SettingsValidator.Validate(profile);
        if (validation.Errors.Count != 0)
            return false;

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsExitHeld)
            {
                SetStatus(false, "Настройки нельзя менять во время подтверждения выхода.");
                return false;
            }
            if (Volatile.Read(ref _stopInProgress) != 0)
            {
                SetStatus(false, "Изменение настроек ожидает завершения Stop.");
                return false;
            }

            Interlocked.Exchange(ref _profileUpdateInProgress, 1);
            var generation = _admission.Invalidate();
            CancelActiveCycle();
            await _cycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _stopInProgress) != 0)
                {
                    SetStatus(false, "Изменение настроек отменено командой Stop.");
                    return false;
                }

                var current = Volatile.Read(ref _profile).Copy();
                var updated = validation.Profile.Copy();
                updated.SavedOperatingIntent = updated.SelectedMode == "TrueNorth AI setups"
                    && current.SelectedMode == "TrueNorth AI setups" && current.SavedOperatingIntent;
                if (updated.SelectedMode != "TrueNorth AI setups")
                    generation = _admission.Close();

                SettingsOperationResult? saved = null;
                Exception? saveException = null;
                var committed = _admission.RunIfGeneration(generation, () =>
                {
                    if (Volatile.Read(ref _stopInProgress) != 0)
                        return false;
                    try
                    {
                        saved = _settingsStore.Save(updated);
                    }
                    catch (Exception exception)
                    {
                        saveException = exception;
                        return false;
                    }
                    return saved.Succeeded && saved.Profile is not null;
                });

                if (!committed)
                {
                    if (saved is null && saveException is null)
                    {
                        SetStatus(false, "Изменение настроек отменено более поздней командой Stop.");
                        return false;
                    }

                    if (updated.SelectedMode != "TrueNorth AI setups")
                    {
                        Volatile.Write(ref _profile, updated);
                        _recovery.UpdateSettings(updated.Settings);
                    }
                    var detail = saveException is not null
                        ? SafeFailureKind(saveException)
                        : saved!.Message ?? saved.Status.ToString();
                    SetStatus(false, updated.SelectedMode == "TrueNorth AI setups"
                        ? "Параметры не сохранены; прежние настройки и сохранённое намерение остаются в силе. " + detail
                        : "Режим Test включён в текущем процессе, но Stop не сохранён; допуск входов закрыт. " + detail);
                    return false;
                }

                Volatile.Write(ref _profile, saved!.Profile!);
                _recovery.UpdateSettings(saved.Profile!.Settings);
                if (current.SavedOperatingIntent && !saved.Profile.SavedOperatingIntent
                    && !await InvalidateLimitDecisionsOnStopAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    SetStatus(false, "Режим сохранён, но поздние решения Limit не удалось сбросить.");
                    return false;
                }
                SetStatus(true, updated.SelectedMode == "Test"
                    ? "Режим Test сохранён; новые входы остановлены, существующие заявки сохранены."
                    : updated.SavedOperatingIntent
                        ? "Параметры сохранены для следующих входов; намерение Start и текущие заявки сохранены."
                        : "Параметры сохранены; намерение Stop сохранено.");
                return true;
            }
            finally
            {
                _cycleGate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _profileUpdateInProgress, 0);
            _commandGate.Release();
        }
    }

    internal async Task<TradingLoopOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsExitHeld)
                return SetStatus(false, "Start заблокирован до завершения подтверждения выхода.");
            if (Volatile.Read(ref _stopInProgress) != 0)
                return SetStatus(false, "Start ожидает завершения Stop.");
            var generation = _admission.Generation;
            await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _stopInProgress) != 0)
                    return SetStatus(false, "Start отменён командой Stop.");
                var profile = Volatile.Read(ref _profile).Copy();
                if (profile.SelectedMode != "TrueNorth AI setups" || profile.IsIncomplete())
                    return SetStatus(false, "Для Start нужен режим TrueNorth AI setups и полные торговые настройки.");

                if (!await ReconcileAsync(profile, cancellationToken).ConfigureAwait(false))
                    return SetStatus(false, "Start ожидает свежую сверку и подтверждение области экспозиции.");
                if (!_recovery.StorageReady)
                {
                    _admission.SetGlobalBlock(true);
                    return SetStatus(false, _recovery.StatusText);
                }
                if (_admission.Generation != generation)
                    return SetStatus(false, "Start отменён более поздней командой Stop или изменением настроек.");

                if (!profile.SavedOperatingIntent
                    && !await InvalidateLimitDecisionsOnStopAsync(cancellationToken).ConfigureAwait(false))
                    return SetStatus(false, "Start заблокирован: результат переоценки после Stop не удалось сбросить.");

                var startProfile = profile.Copy();
                startProfile.SavedOperatingIntent = true;
                SettingsOperationResult? saved = null;
                var started = _admission.RunIfGeneration(generation, () =>
                {
                    if (Volatile.Read(ref _stopInProgress) != 0)
                        return false;
                    saved = _settingsStore.Save(startProfile);
                    if (!saved.Succeeded || saved.Profile is null)
                        return false;
                    Volatile.Write(ref _profile, saved.Profile);
                    _recovery.UpdateSettings(saved.Profile.Settings);
                    return _admission.Open(generation);
                });
                if (!started)
                {
                    if (saved is null)
                        return SetStatus(false, "Start отменён более поздней командой Stop или изменением настроек.");
                    return SetStatus(false, saved.Succeeded
                        ? "Start отменён более поздней командой Stop."
                        : "Start не сохранён; допуск входов остаётся закрытым. "
                            + (saved.Message ?? saved.Status.ToString()));
                }
                return SetStatus(true, "Start сохранён; новые входы разрешены после сверки каждого точного счёта.");
            }
            finally
            {
                _cycleGate.Release();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<TradingLoopOutcome> StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stopInProgress);
        _admission.Close();
        CancelActiveCycle();

        await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _cycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var stopped = Volatile.Read(ref _profile).Copy();
                stopped.SavedOperatingIntent = false;
                var saved = _settingsStore.Save(stopped);
                var intentSaved = saved.Succeeded && saved.Profile is not null;
                Volatile.Write(ref _profile, intentSaved ? saved.Profile! : stopped);
                _recovery.UpdateSettings(stopped.Settings);

                var lifecycle = await _orders.StopAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var order in _orders.Orders.Where(ManagedOrderState.BlocksNewEntry).ToArray())
                    await _orders.RunDueChecksAsync(order.IntentId, savedOperatingIntent: false, CancellationToken.None)
                        .ConfigureAwait(false);

                if (!await InvalidateLimitDecisionsOnStopAsync(CancellationToken.None).ConfigureAwait(false))
                    lifecycle = new(false, "Stop записан, но ожидающие решения по Limit не удалось сбросить.");

                var status = intentSaved
                    ? lifecycle.Status
                    : "Новые входы остановлены в текущем процессе, но намерение Stop не записано. "
                        + (saved.Message ?? saved.Status.ToString());
                return new(intentSaved && lifecycle.Succeeded, status);
            }
            finally
            {
                _cycleGate.Release();
            }
        }
        finally
        {
            _commandGate.Release();
            Interlocked.Decrement(ref _stopInProgress);
        }
    }

    internal async Task<TradingLoopOutcome> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stopInProgress) != 0 || Volatile.Read(ref _profileUpdateInProgress) != 0)
            return SetStatus(false, "Цикл отложен до завершения команды управления.");
        var runGeneration = _admission.Generation;
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _stopInProgress) != 0 || Volatile.Read(ref _profileUpdateInProgress) != 0)
        {
            _cycleGate.Release();
            return SetStatus(false, "Цикл отложен до завершения команды управления.");
        }
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeCycleGate)
            Volatile.Write(ref _activeCycle, cycle);
        try
        {
            var profile = Volatile.Read(ref _profile).Copy();
            if (!await ReconcileAsync(profile, cycle.Token).ConfigureAwait(false))
                return SetStatus(false, _lastStatus ?? "Цикл ожидает общей сверки.");

            var linkedOrder = await LinkPendingEntryAsync(cycle.Token).ConfigureAwait(false);
            if (linkedOrder is { } linkResult)
                return linkResult;

            var expiredCandidate = await DropExpiredCandidateAsync(cycle.Token).ConfigureAwait(false);
            if (expiredCandidate is { } expired)
                return expired;

            profile = Volatile.Read(ref _profile).Copy();
            if (!profile.SavedOperatingIntent || profile.SelectedMode != "TrueNorth AI setups")
            {
                _admission.Close();
                return SetStatus(true, "Новые входы остановлены; сверка и сопровождение продолжаются.");
            }
            if (IsExitHeld)
                return SetStatus(true, "Новые входы приостановлены для подтверждения выхода; сверка и сопровождение продолжаются.");
            if (profile.IsIncomplete())
                return SetStatus(false, "Новые входы заблокированы: торговые настройки неполны.");

            if (!_admission.IsStarted && !_admission.Open(runGeneration))
                return SetStatus(false, "Сохранённое намерение Start не прошло текущую сверку.");
            if (!_admission.IsAdmitting)
                return SetStatus(false, "Допуск новых входов закрыт общей блокировкой экспозиции.");

            var routes = _contexts.GetRoutes(profile).ToArray();
            var routeError = ValidateRoutes(profile, routes);
            if (routeError is not null)
                return BlockGlobally(routeError);

            var accounts = await ReadAccountsAsync(routes, cycle.Token).ConfigureAwait(false);
            var accountList = accounts.Values.Where(read => read is not null).Cast<AccountSnapshot>().ToArray();
            var contextReads = new Dictionary<TradingLoopRoute, TradingLoopContext>();
            foreach (var route in routes)
            {
                if (!accounts.TryGetValue((route.Venue, route.Account), out var account) || account is null)
                    continue;
                var context = await _contexts.ReadAsync(route, accountList, null, profile, _clock.UtcNow, cycle.Token)
                    .ConfigureAwait(false);
                contextReads[route] = context;
                if (context.AllConnectedExposureCovered != true)
                    return BlockGlobally(context.Reason ?? "Связь всей экспозиции не подтверждена.");
            }

            if (!_recovery.StorageReady)
                return BlockGlobally(_recovery.StatusText);

            _admission.SetGlobalBlock(_recovery.State.Venues.Any(venue => venue.Venue == "*"));
            if (!_admission.IsAdmitting)
                return SetStatus(false, "Область сбоя восстановления неизвестна; глобальный допуск закрыт.");

            var limitOutcome = await ProcessLimitReassessmentsAsync(routes, profile, cycle.Token)
                .ConfigureAwait(false);
            if (limitOutcome is not null)
                return limitOutcome;

            if (_state.PendingSetup is not null)
                return await ProcessPendingSetupAsync(routes, accounts, contextReads, profile, cycle.Token)
                    .ConfigureAwait(false);

            var insufficientFunds = false;
            var confirmedMinimumShortfall = false;
            var accountUnavailable = false;
            var recoveryPaused = false;
            var routeUnavailable = false;
            string? lastSearchStatus = null;
            foreach (var asset in profile.Settings.Assets.Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(value => value!.Trim().ToUpperInvariant()))
            {
                if (_state.NextSearchAt.TryGetValue(asset, out var dueAt) && dueAt > _clock.UtcNow)
                    continue;

                var assetRoutes = routes.Where(route => route.Asset == asset).ToArray();
                if (_orders.Orders.Any(order => assetRoutes.Any(route => route.Venue == order.Venue
                        && route.Account == order.Account && route.Contract == order.Contract)
                    && ManagedOrderState.BlocksNewEntry(order)))
                    continue;
                TradingLoopRoute? queryRoute = null;
                foreach (var route in assetRoutes)
                {
                    if (!accounts.TryGetValue((route.Venue, route.Account), out var account) || account is null)
                    {
                        accountUnavailable = true;
                        continue;
                    }
                    if (!_recovery.IsEntryAllowed(route.Venue, profile.SavedOperatingIntent))
                    {
                        recoveryPaused = true;
                        continue;
                    }
                    if (!contextReads.TryGetValue(route, out var context) || !context.RouteAvailable)
                    {
                        routeUnavailable = true;
                        continue;
                    }
                    if (context.MinimumEntryAffordable == false)
                    {
                        confirmedMinimumShortfall = true;
                        continue;
                    }
                    if (!HasAvailableFunds(account))
                    {
                        insufficientFunds = true;
                        continue;
                    }
                    queryRoute = route;
                    break;
                }

                if (queryRoute is null)
                    continue;

                var result = await SearchAssetAsync(queryRoute, routes, profile, cycle.Token)
                    .ConfigureAwait(false);
                if (result.IntentId is not null || !result.Succeeded)
                    return result;
                if (result.Status.Contains("NO_SETUP", StringComparison.Ordinal))
                {
                    lastSearchStatus = result.Status;
                    continue;
                }
                if (_state.PendingSetup is not null)
                    return result;
            }

            if (lastSearchStatus is not null)
                return SetStatus(true, lastSearchStatus);
            if (confirmedMinimumShortfall)
                return SetStatus(true, "Недостаточно средств для подтверждённого минимума; AI не запрашивался.");
            if (insufficientFunds)
                return SetStatus(true, "Недостаточно свободной маржи; AI не запрашивался.");
            if (accountUnavailable)
                return SetStatus(false, "Свежий снимок счёта недоступен; чтение передано восстановлению 008.");
            if (recoveryPaused)
                return SetStatus(false, "Площадка ожидает полной сверки 008; новые входы на ней заблокированы.");
            if (routeUnavailable)
                return SetStatus(false, "Проверенный локальный маршрут недоступен; AI не запрашивался.");
            return SetStatus(true, "По всем допустимым активам свежий сетап не найден или срок поиска ещё не наступил.");
        }
        catch (OperationCanceledException) when (cycle.IsCancellationRequested)
        {
            return SetStatus(false, "Цикл остановлен; участок передачи дополнительно сверяется журналом 008/009.");
        }
        catch (Exception exception)
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Цикл остановлен при неизвестной области сбоя ("
                + SafeFailureKind(exception) + "); новые входы закрыты.");
        }
        finally
        {
            lock (_activeCycleGate)
            {
                if (ReferenceEquals(_activeCycle, cycle))
                    Volatile.Write(ref _activeCycle, null);
            }
            _cycleGate.Release();
        }
    }

    private async Task<bool> ReconcileAsync(SettingsProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            if (profile.SelectedMode != "TrueNorth AI setups")
                _admission.Close();

            if (!_recovery.StorageReady)
            {
                _admission.SetGlobalBlock(true);
                _lastStatus = _recovery.StatusText;
                return false;
            }

            var routes = _contexts.GetRoutes(profile).ToArray();
            var routeError = ValidateRoutes(profile, routes);
            if (routeError is not null)
            {
                BlockGlobally(routeError);
                return false;
            }

            foreach (var venue in _recovery.State.Venues.Where(item => item.Phase != RecoveryReadPhase.Resolved)
                         .Select(item => item.Venue).Distinct(StringComparer.Ordinal))
                await _recovery.CheckDueAsync(venue, cancellationToken).ConfigureAwait(false);

            var accounts = await ReadAccountsAsync(routes, cancellationToken).ConfigureAwait(false);
            foreach (var order in _orders.Orders.Where(item => item.PendingOperationId is not null).ToArray())
                await _orders.ReconcilePendingAsync(order.IntentId, cancellationToken).ConfigureAwait(false);

            var stopIntentSaveFailed = false;
            if (profile.SelectedMode != "TrueNorth AI setups" && profile.SavedOperatingIntent)
            {
                var stopped = profile.Copy();
                stopped.SavedOperatingIntent = false;
                var saved = _settingsStore.Save(stopped);
                if (saved.Succeeded && saved.Profile is not null)
                    stopped = saved.Profile;
                else
                {
                    stopIntentSaveFailed = true;
                    _lastStatus = "Режим Test выключил новые входы, но намерение Stop не записано. "
                        + (saved.Message ?? saved.Status.ToString());
                }
                Volatile.Write(ref _profile, stopped);
                profile = stopped;
                _recovery.UpdateSettings(stopped.Settings);
            }

            if (!profile.SavedOperatingIntent || profile.SelectedMode != "TrueNorth AI setups")
                await _orders.StopAsync(cancellationToken).ConfigureAwait(false);
            foreach (var order in _orders.Orders.Where(ManagedOrderState.BlocksNewEntry).ToArray())
                await _orders.RunDueChecksAsync(order.IntentId, savedOperatingIntent: false, cancellationToken)
                    .ConfigureAwait(false);

            var snapshots = accounts.Values.Where(read => read is not null).Cast<AccountSnapshot>().ToArray();
            foreach (var route in routes)
            {
                if (!accounts.TryGetValue((route.Venue, route.Account), out var account) || account is null)
                    continue;
                var context = await _contexts.ReadAsync(route, snapshots, null, profile, _clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                if (context.AllConnectedExposureCovered != true)
                {
                    _admission.SetGlobalBlock(true);
                    _lastStatus = context.Reason ?? "Связь всей экспозиции не подтверждена.";
                    return false;
                }
            }

            if (!_recovery.StorageReady)
            {
                _admission.SetGlobalBlock(true);
                _lastStatus = _recovery.StatusText;
                return false;
            }

            _admission.SetGlobalBlock(_recovery.State.Venues.Any(venue => venue.Venue == "*"));
            if (stopIntentSaveFailed)
                return false;
            if (_admission.IsAdmitting)
                return true;
            if (_recovery.State.Venues.Any(venue => venue.Venue == "*"))
            {
                _lastStatus = "Область сбоя восстановления неизвестна; входы заблокированы на всех площадках.";
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _admission.SetGlobalBlock(true);
            _lastStatus = "Сверка не завершена; глобальный допуск закрыт ("
                + SafeFailureKind(exception) + ").";
            return false;
        }
    }

    private async Task<Dictionary<(string Venue, string Account), AccountSnapshot?>> ReadAccountsAsync(
        IReadOnlyList<TradingLoopRoute> routes,
        CancellationToken cancellationToken)
    {
        var accounts = new Dictionary<(string Venue, string Account), AccountSnapshot?>();
        foreach (var route in routes)
        {
            var key = (route.Venue, route.Account);
            if (accounts.ContainsKey(key))
                continue;
            var read = await _recovery.ReadCurrentSnapshotAsync(route.Venue, route.Account, cancellationToken)
                .ConfigureAwait(false);
            accounts[key] = read.Account;
        }
        return accounts;
    }

    private async Task<TradingLoopOutcome> ProcessPendingSetupAsync(
        IReadOnlyList<TradingLoopRoute> routes,
        IReadOnlyDictionary<(string Venue, string Account), AccountSnapshot?> accounts,
        IReadOnlyDictionary<TradingLoopRoute, TradingLoopContext> contexts,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var pending = _state.PendingSetup;
        if (pending is null)
            return SetStatus(false, "Сохранённый сетап исчез до проверки.");

        var assetRoutes = routes.Where(route => route.Asset == pending.Request.Asset).ToArray();
        if (assetRoutes.Length == 0)
        {
            const string removed = "Сохранённый сетап исключён: актив больше не выбран владельцем.";
            if (!await SaveLoopStateAsync(_state with { PendingSetup = null, LastStatus = removed }, cancellationToken)
                    .ConfigureAwait(false))
                return SetStatus(false, "Сетап не удалось закрыть в журнале.", pending.Request.Asset);
            _pendingSetupLoadedAtStartup = false;
            return SetStatus(false, removed, pending.Request.Asset);
        }

        if (_state.NextSearchAt.TryGetValue(pending.Request.Asset, out var dueAt) && dueAt > _clock.UtcNow)
            return SetStatus(true, $"Сохранённый сетап ожидает срока повтора {dueAt:O}.", pending.Request.Asset);

        if (assetRoutes.Any(route => _orders.Orders.Any(order => order.Venue == route.Venue
                && order.Account == route.Account && order.Contract == route.Contract
                && ManagedOrderState.BlocksNewEntry(order))))
        {
            return SetStatus(false, "Сохранённый сетап ожидает сверки уже записанного действия.",
                pending.Request.Asset);
        }

        var analysisRoute = FindEligibleSearchRoute(assetRoutes, accounts, contexts, profile,
            out var waitingReason);
        if (analysisRoute is null)
            return SetStatus(true, waitingReason ?? "Сохранённый сетап ждёт доступного точного маршрута.",
                pending.Request.Asset);

        if (_pendingSetupLoadedAtStartup)
            return await ReassessPersistedSetupAsync(pending, analysisRoute, routes, profile,
                cancellationToken).ConfigureAwait(false);

        return await EvaluatePendingSetupAsync(pending, analysisRoute, routes, profile,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingLoopOutcome?> LinkPendingEntryAsync(CancellationToken cancellationToken)
    {
        var pending = _state.PendingSetup;
        if (pending is null)
            return null;

        var candidates = _orders.Orders.Where(order => order.Asset == pending.Request.Asset
            && order.EntryType == pending.Request.EntryType && ManagedOrderState.BlocksNewEntry(order)).ToArray();
        var prepared = pending.PreparedEntry;
        if (prepared is null)
            return candidates.Length == 0 ? null
                : BlockGlobally("Старый сетап нельзя связать с точным IntentId; возможная заявка оставлена без изменений.");

        if (!PreparedEntryMatchesSetup(prepared, pending.Request, pending.Response))
            return BlockGlobally("Сохранённый IntentId не совпадает с исходным запросом и сетапом.");

        var matches = _orders.Orders.Where(order => order.IntentId == prepared.IntentId).ToArray();
        if (matches.Length > 1)
            return BlockGlobally("Сохранённый IntentId связан с несколькими заявками; требуется сверка.");
        if (matches.Length == 0)
        {
            if (candidates.Length > 0)
                return BlockGlobally("Сетап и заявка не имеют точного общего IntentId; возможная заявка не усыновлена.");
            return null;
        }

        var order = matches[0];
        if (!PreparedEntryMatchesOrder(prepared, order))
            return BlockGlobally("Сохранённый IntentId указывает на другой маршрут или параметры; заявка не изменена.");

        if (pending.ReplacesIntentId is { } replacedIntentId)
        {
            if (replacedIntentId == prepared.IntentId
                || !_state.LimitReassessments.TryGetValue(replacedIntentId, out var replaced)
                || replaced.Phase != LimitReassessmentPhase.ReplacementPendingEntry
                || replaced.ReplacementIntent != prepared
                || replaced.CancellationConfirmedAt is null
                || replaced.Decision?.Outcome != LimitReassessmentOutcome.Replacement
                || replaced.Request.Id != pending.Request.Id
                || replaced.Decision?.ReplacementResponse != pending.Response
                || replaced.DecisionReceivedAt != pending.ReceivedAt)
                return BlockGlobally("Сохранённая замена не связана с точным исходным Limit.");

            var oldOrder = _orders.Orders.SingleOrDefault(item => item.IntentId == replacedIntentId);
            if (oldOrder is null || oldOrder.ManagementMode == ManagedOrderManagementMode.Manual
                || oldOrder.EntryTerminalState != ManagedEntryTerminalState.Cancelled
                || oldOrder.RemainingQuantity != 0m || oldOrder.PositionQuantity != 0m
                || oldOrder.FilledQuantity != 0m)
                return BlockGlobally("Исходный Limit не подтверждён как отменённый без экспозиции; замена не связана.");
        }

        var limitReassessments = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal);
        if (pending.ReplacesIntentId is { } oldIntentId)
            limitReassessments.Remove(oldIntentId);
        if (order.EntryType == "Limit" && order.ManagementMode == ManagedOrderManagementMode.Application
            && ManagedOrderState.BlocksNewEntry(order))
        {
            var receivedAt = pending.ReceivedAt ?? _clock.UtcNow;
            var interval = pending.LimitReassessmentMinutes
                ?? Volatile.Read(ref _profile).Settings.LimitReassessmentMinutes;
            limitReassessments[order.IntentId] = CreateLimitReassessment(order, pending.Request,
                pending.Response, receivedAt, interval);
        }
        if (!await SaveLoopStateAsync(_state with
            {
                PendingSetup = null,
                LimitReassessments = limitReassessments,
                LastStatus = order.ManagementMode == ManagedOrderManagementMode.Application
                    ? "Заявка после перезапуска связана по точному IntentId и маршруту."
                    : "Заявка передана владельцу; автоматическая переоценка отключена."
            }, cancellationToken).ConfigureAwait(false))
            return SetStatus(false, "Связь исходного ответа и заявки не удалось сохранить.", pending.Request.Asset,
                order.IntentId);
        if (!await _recovery.ClearTradingLoopFailureAsync(_state, pending.Request.Asset, cancellationToken)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Связь заявки сохранена, но сбой не закрыт в журнале.",
                pending.Request.Asset, order.IntentId);
        }
        _state = _recovery.TradingLoopState;
        return null;
    }

    private static bool PreparedEntryMatchesSetup(
        TradingLoopPreparedEntry prepared,
        AiAnalysisRequest request,
        AiSourceResponse response)
    {
        if (!Guid.TryParseExact(prepared.IntentId, "N", out _)
            || prepared.RequestId != request.Id || response.ResponseRequestId != request.Id
            || !response.IsFinalStream || prepared.Asset != request.Asset
            || prepared.EntryType != request.EntryType || string.IsNullOrWhiteSpace(prepared.Venue)
            || string.IsNullOrWhiteSpace(prepared.Account) || string.IsNullOrWhiteSpace(prepared.Contract)
            || prepared.Quantity <= 0m || prepared.TakeProfit <= 0m || prepared.StopLoss <= 0m)
            return false;

        var assessed = AiResponseAssessor.Assess(request, response);
        if (assessed.Outcome != AiTestOutcome.CompleteSetup || assessed.Fields is not { } fields
            || ToEligibilitySetup(request, fields) is null)
            return false;

        var direction = fields.Side == "LONG" ? PositionDirection.Long : PositionDirection.Short;
        var expectedLimit = request.OrderType == "LIMIT" ? fields.EntryPrice : null;
        return prepared.Direction == direction && prepared.LimitPrice == expectedLimit
            && prepared.TakeProfit == fields.TakeProfit && prepared.StopLoss == fields.StopLoss;
    }

    private static bool PreparedEntryMatchesOrder(TradingLoopPreparedEntry prepared, ManagedOrderState order) =>
        prepared.IntentId == order.IntentId && prepared.Venue == order.Venue && prepared.Account == order.Account
        && prepared.Contract == order.Contract && prepared.Asset == order.Asset
        && prepared.EntryType == order.EntryType && prepared.Direction == order.Direction
        && prepared.Quantity == order.Quantity && prepared.LimitPrice == order.LimitPrice
        && prepared.TakeProfit == order.TakeProfit && prepared.StopLoss == order.StopLoss;

    private async Task<TradingLoopOutcome?> ProcessLimitReassessmentsAsync(
        IReadOnlyList<TradingLoopRoute> routes,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        foreach (var savedReassessment in _state.LimitReassessments.Values
                     .OrderBy(item => item.NextReassessmentAt).ToArray())
        {
            var reassessment = savedReassessment;
            if (reassessment.Phase == LimitReassessmentPhase.ReplacementPendingEntry)
            {
                var pending = _state.PendingSetup;
                if (pending?.ReplacesIntentId != reassessment.IntentId || pending.PreparedEntry is null
                    || reassessment.ReplacementIntent != pending.PreparedEntry)
                    return BlockGlobally("Старое состояние замены не содержит точный IntentId; требуется ручная сверка без повторной отправки.");
                return await ContinueLimitReplacementAsync(reassessment, routes, profile, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (reassessment.Phase == LimitReassessmentPhase.ReplacementReady)
                return await ContinueLimitReplacementAsync(reassessment, routes, profile, cancellationToken)
                    .ConfigureAwait(false);

            var order = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId);
            if (order is null)
                return BlockGlobally("Связанный Limit отсутствует в журнале; переоценка и входы остановлены.");

            if (order.ManagementMode == ManagedOrderManagementMode.Manual)
            {
                await RemoveLimitReassessmentAsync(reassessment,
                    "Limit передан владельцу; автоматическая переоценка остановлена.", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (order.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice)
            {
                if (reassessment.Decision is not null)
                {
                    var reset = reassessment with
                    {
                        EntryOrderId = order.EntryOrderId,
                        Phase = order.EntryOrderId is null
                            ? LimitReassessmentPhase.WaitingForEntry : LimitReassessmentPhase.Scheduled,
                        AssessmentId = null,
                        Decision = null,
                        DecisionReceivedAt = null,
                        CancellationConfirmedAt = null,
                        NextReassessmentAt = _clock.UtcNow.AddMinutes(reassessment.IntervalMinutes),
                        LastStatus = "Переоценка приостановлена до решения владельца."
                    };
                    await SaveLimitReassessmentAsync(reset, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (order.PendingOperationId is not null)
                return SetStatus(false, "Переоценка Limit ожидает сверки уже отправленного действия.",
                    order.Asset, order.IntentId);

            if (reassessment.Phase == LimitReassessmentPhase.CancelPending)
                return await ProcessLimitCancellationAsync(reassessment, profile, cancellationToken)
                    .ConfigureAwait(false);

            if (reassessment.Phase == LimitReassessmentPhase.WaitingForEntry)
            {
                if (order.EntryTerminalState == ManagedEntryTerminalState.Open && order.EntryOrderId is not null)
                {
                    await SaveLimitReassessmentAsync(reassessment with
                    {
                        EntryOrderId = order.EntryOrderId,
                        Phase = LimitReassessmentPhase.Scheduled,
                        LastStatus = "Точный ID Limit подтверждён; срок переоценки сохранён."
                    }, cancellationToken).ConfigureAwait(false);
                    reassessment = _state.LimitReassessments[reassessment.IntentId];
                }
                else if (order.EntryTerminalState is ManagedEntryTerminalState.Cancelled
                         or ManagedEntryTerminalState.Filled or ManagedEntryTerminalState.Rejected)
                {
                    await RemoveLimitReassessmentAsync(reassessment,
                        "Входящий Limit завершён; защитой позиции продолжает управлять 009.", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                else
                {
                    continue;
                }
            }

            if (order.EntryTerminalState != ManagedEntryTerminalState.Open)
            {
                await RemoveLimitReassessmentAsync(reassessment,
                    "Входящий Limit завершён; защитой позиции продолжает управлять 009.", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (order.EntryOrderId is null || reassessment.EntryOrderId != order.EntryOrderId)
                return BlockGlobally("Сохранённый ID Limit не совпадает с текущим; автоматическое действие заблокировано.");
            if (reassessment.NextReassessmentAt > _clock.UtcNow)
                continue;

            return await ReassessLimitAsync(reassessment, order, profile, cancellationToken)
                .ConfigureAwait(false);
        }
        return null;
    }

    private async Task<TradingLoopOutcome> ReassessLimitAsync(
        TradingLoopLimitReassessment reassessment,
        ManagedOrderState order,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var generation = _admission.Generation;
        if (!LimitCycleAllowsAction(profile, generation))
            return SetStatus(false, "Переоценка Limit отложена командой управления.", order.Asset, order.IntentId);

        await _orders.RunDueChecksAsync(order.IntentId, savedOperatingIntent: false, cancellationToken, force: true)
            .ConfigureAwait(false);
        order = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId) ?? order;
        if (cancellationToken.IsCancellationRequested || !LimitCycleAllowsAction(profile, generation))
            return SetStatus(false, "Переоценка Limit остановлена командой управления.", order.Asset, order.IntentId);
        if (order.ManagementMode == ManagedOrderManagementMode.Manual)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Limit передан владельцу; поздняя переоценка отброшена.", CancellationToken.None)
                .ConfigureAwait(false);
            return SetStatus(false, "Ручная передача отключила переоценку Limit.", order.Asset, order.IntentId);
        }
        if (order.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice)
            return SetStatus(false, "Переоценка приостановлена до решения владельца.", order.Asset, order.IntentId);
        if (order.PendingOperationId is not null)
            return SetStatus(false, "Переоценка ожидает сверки действия 009.", order.Asset, order.IntentId);
        if (order.EntryTerminalState != ManagedEntryTerminalState.Open || order.EntryOrderId is null)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Limit исполнен или завершён; защитой позиции продолжает управлять 009.",
                CancellationToken.None).ConfigureAwait(false);
            return SetStatus(true, "Limit завершён до переоценки; состояние позиции сверено.", order.Asset,
                order.IntentId);
        }
        if (order.EntryOrderId != reassessment.EntryOrderId)
            return BlockGlobally("Точный ID Limit изменился до переоценки; автоматическое действие заблокировано.");

        var origin = AiResponseAssessor.Assess(reassessment.Request, reassessment.OriginResponse);
        if (origin.Outcome != AiTestOutcome.CompleteSetup || origin.Fields is null
            || _analysis is not ITradingLoopLimitReassessmentSource source)
            return await RecordLimitInconclusiveAsync(reassessment,
                "Тестовый источник не подтвердил связанный исходный сетап для переоценки.",
                CancellationToken.None).ConfigureAwait(false);

        var assessmentId = Guid.NewGuid();
        LimitReassessmentResult result;
        try
        {
            result = await source.ReassessLimitAsync(new LimitReassessmentContext(assessmentId,
                reassessment.IntentId, order.EntryOrderId, reassessment.Request, reassessment.OriginResponse,
                origin.Fields, reassessment.ReceivedAt, order), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SetStatus(false, "Переоценка отменена командой управления; заявка и срок сохранены.",
                order.Asset, order.IntentId);
        }
        catch (Exception exception)
        {
            if (!LimitCycleAllowsAction(profile, generation))
                return SetStatus(false, "Поздняя переоценка после Stop или изменения настроек отброшена.",
                    order.Asset, order.IntentId);
            return await RecordLimitInconclusiveAsync(reassessment,
                "Переоценка Limit завершилась ошибкой (" + SafeFailureKind(exception) + ").",
                CancellationToken.None).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || !LimitCycleAllowsAction(profile, generation))
            return SetStatus(false, "Поздний ответ Limit после Stop или изменения настроек отброшен.",
                order.Asset, order.IntentId);

        await _orders.RunDueChecksAsync(order.IntentId, savedOperatingIntent: false, cancellationToken, force: true)
            .ConfigureAwait(false);
        var latest = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId);
        if (cancellationToken.IsCancellationRequested || !LimitCycleAllowsAction(profile, generation))
            return SetStatus(false, "Поздний ответ Limit после Stop или изменения настроек отброшен.",
                order.Asset, order.IntentId);
        if (latest is null)
            return BlockGlobally("Связанный Limit исчез после AI; автоматический результат отброшен.");
        if (latest.ManagementMode == ManagedOrderManagementMode.Manual)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Ручная передача обнулила поздний ответ переоценки.", CancellationToken.None).ConfigureAwait(false);
            return SetStatus(false, "Поздний ответ отброшен после передачи Limit владельцу.", latest.Asset,
                latest.IntentId);
        }
        if (latest.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice)
            return SetStatus(false, "Переоценка остановлена до решения владельца.", latest.Asset, latest.IntentId);
        if (latest.PendingOperationId is not null)
            return SetStatus(false, "Поздний ответ ожидает сверки действия 009.", latest.Asset, latest.IntentId);
        if (latest.EntryTerminalState != ManagedEntryTerminalState.Open || latest.EntryOrderId is null)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Limit изменился во время переоценки; позиция остаётся на сопровождении 009.",
                CancellationToken.None).ConfigureAwait(false);
            return SetStatus(true, "Limit исполнился или завершился во время переоценки.", latest.Asset,
                latest.IntentId);
        }
        if (latest.EntryOrderId != order.EntryOrderId
            || latest.TakeProfit != order.TakeProfit || latest.StopLoss != order.StopLoss)
            return await RecordLimitInconclusiveAsync(reassessment,
                "Состояние заявки или защиты изменилось во время переоценки; ответ отброшен.",
                CancellationToken.None).ConfigureAwait(false);

        var reason = string.IsNullOrWhiteSpace(result?.Reason)
            ? "Переоценка Limit не дала однозначного результата."
            : result.Reason.Trim();
        if (result is null || result.AssessmentId != assessmentId || result.IntentId != reassessment.IntentId
            || result.EntryOrderId != order.EntryOrderId || result.OriginRequestId != reassessment.Request.Id
            || !Enum.IsDefined(result.Outcome))
            return await RecordLimitInconclusiveAsync(reassessment,
                "Ответ переоценки не связан с исходным запросом и точным Limit.", CancellationToken.None)
                .ConfigureAwait(false);

        if (result.Outcome == LimitReassessmentOutcome.Keep)
        {
            if (result.ReplacementResponse is not null)
                return await RecordLimitInconclusiveAsync(reassessment,
                    "Ответ подтверждения актуальности содержит лишний сетап на замену.",
                    CancellationToken.None).ConfigureAwait(false);
            return await ScheduleLimitReassessmentAsync(reassessment,
                "Переоценка подтвердила актуальность Limit.", CancellationToken.None).ConfigureAwait(false);
        }
        if (result.Outcome == LimitReassessmentOutcome.Inconclusive)
            return await RecordLimitInconclusiveAsync(reassessment, reason, CancellationToken.None)
                .ConfigureAwait(false);
        if (result.Outcome == LimitReassessmentOutcome.InvalidNoReplacement
            && result.ReplacementResponse is null)
            return await SaveLimitDecisionAndCancelAsync(reassessment, result, profile, cancellationToken)
                .ConfigureAwait(false);
        if (result.Outcome != LimitReassessmentOutcome.Replacement || result.ReplacementResponse is null)
            return await RecordLimitInconclusiveAsync(reassessment,
                "Результат переоценки не содержит корректный исход для Limit.", CancellationToken.None)
                .ConfigureAwait(false);

        var replacement = AiResponseAssessor.Assess(reassessment.Request, result.ReplacementResponse);
        var replacementSetup = replacement.Outcome == AiTestOutcome.CompleteSetup && replacement.Fields is not null
            ? ToEligibilitySetup(reassessment.Request, replacement.Fields) : null;
        if (replacementSetup is null || replacement.Fields is null)
            return await RecordLimitInconclusiveAsync(reassessment,
                "Сетап на замену неполон или не связан с исходным рынком.", CancellationToken.None)
                .ConfigureAwait(false);
        if (replacementSetup.EntryType != "Limit" || replacementSetup.Direction == order.Direction
                && replacementSetup.LimitPrice == order.LimitPrice
                && replacementSetup.TakeProfit == order.TakeProfit
                && replacementSetup.StopLoss == order.StopLoss)
            return await RecordLimitInconclusiveAsync(reassessment,
                "Сетап на замену совпадает с действующим Limit и не создаёт нового сигнала.",
                CancellationToken.None).ConfigureAwait(false);

        return await SaveLimitDecisionAndCancelAsync(reassessment, result, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TradingLoopOutcome> SaveLimitDecisionAndCancelAsync(
        TradingLoopLimitReassessment reassessment,
        LimitReassessmentResult result,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return SetStatus(false, "Результат Limit отменён Stop; заявка остаётся под сопровождением 009.",
                reassessment.Request.Asset, reassessment.IntentId);
        var updated = reassessment with
        {
            Phase = LimitReassessmentPhase.CancelPending,
            AssessmentId = result.AssessmentId,
            Decision = result,
            DecisionReceivedAt = _clock.UtcNow,
            LastStatus = result.Outcome == LimitReassessmentOutcome.Replacement
                ? "Сетап на замену сохранён; ожидается точная отмена старого остатка."
                : "Утрата актуальности сохранена; ожидается точная отмена остатка без замены."
        };
        if (!await SaveLimitReassessmentAsync(updated, CancellationToken.None).ConfigureAwait(false))
            return SetStatus(false, "Результат переоценки не сохранился; отмена не запущена.",
                reassessment.Request.Asset, reassessment.IntentId);
        return await ProcessLimitCancellationAsync(updated, profile, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingLoopOutcome> ProcessLimitCancellationAsync(
        TradingLoopLimitReassessment reassessment,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        if (reassessment.Decision is not { } decision)
            return BlockGlobally("Отмена Limit не имеет сохранённого решения переоценки.");
        var order = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId);
        if (order is null || reassessment.EntryOrderId is null || order.EntryOrderId != reassessment.EntryOrderId)
            return BlockGlobally("Отмена не связана с точным исходным ID Limit.");
        if (order.ManagementMode == ManagedOrderManagementMode.Manual)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Ручная передача обнулила сохранённое решение Limit.", CancellationToken.None).ConfigureAwait(false);
            return SetStatus(false, "Limit передан владельцу; отмена и замена приложением отключены.",
                order.Asset, order.IntentId);
        }
        if (order.ManagementMode == ManagedOrderManagementMode.AwaitingOwnerChoice)
            return await ScheduleLimitReassessmentAsync(reassessment,
                "Отмена приостановлена до решения владельца.", CancellationToken.None).ConfigureAwait(false);
        if (order.PendingOperationId is not null)
            return SetStatus(false, "Точная отмена уже отправлена; ожидается сверка 008/009.",
                order.Asset, order.IntentId);

        var generation = _admission.Generation;
        if (!LimitCycleAllowsAction(profile, generation))
            return SetStatus(false, "Отмена Limit остановлена командой управления.", order.Asset, order.IntentId);
        await _orders.RunDueChecksAsync(order.IntentId, savedOperatingIntent: false, cancellationToken, force: true)
            .ConfigureAwait(false);
        order = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId) ?? order;
        if (order.ManagementMode == ManagedOrderManagementMode.Manual)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Ручная передача обнулила сохранённое решение Limit.", CancellationToken.None).ConfigureAwait(false);
            return SetStatus(false, "Limit передан владельцу; отмена и замена приложением отключены.",
                order.Asset, order.IntentId);
        }
        if (order.PendingOperationId is not null)
            return SetStatus(false, "Точная отмена ожидает сверки 008/009.", order.Asset, order.IntentId);
        if (order.EntryTerminalState != ManagedEntryTerminalState.Open)
            return await CompleteLimitCancellationAsync(reassessment, order, profile, cancellationToken)
                .ConfigureAwait(false);
        if (order.EntryOrderId != reassessment.EntryOrderId)
            return BlockGlobally("Точный Limit изменился перед отменой; автоматическое действие заблокировано.");

        var expectedOrderId = order.EntryOrderId;
        bool Fence(Func<bool> beginTransfer) => _admission.RunIfAdmitting(() =>
            LimitCycleAllowsAction(profile, generation)
            && _orders.Orders.Any(current => current.IntentId == reassessment.IntentId
                && current.EntryOrderId == expectedOrderId
                && current.ManagementMode == ManagedOrderManagementMode.Application
                && current.EntryTerminalState == ManagedEntryTerminalState.Open
                && (current.PendingOperationId is null
                    || current.PendingAction == ManagedOrderAction.CancelRemainder))
            && beginTransfer());
        await _orders.CancelRemainderAsync(order.IntentId, savedOperatingIntent: true,
            cancellationToken, transferAdmissionFence: Fence).ConfigureAwait(false);
        order = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId) ?? order;
        if (order.PendingOperationId is not null)
            return SetStatus(false, "Исход отмены неизвестен; точный Limit остаётся на сверке 008/009.",
                order.Asset, order.IntentId);
        return await CompleteLimitCancellationAsync(reassessment, order, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TradingLoopOutcome> CompleteLimitCancellationAsync(
        TradingLoopLimitReassessment reassessment,
        ManagedOrderState order,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        if (order.ManagementMode == ManagedOrderManagementMode.Manual)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Ручная передача обнулила сохранённое решение Limit.", CancellationToken.None).ConfigureAwait(false);
            return SetStatus(false, "Limit передан владельцу; замена заблокирована.", order.Asset, order.IntentId);
        }
        if (order.PendingOperationId is not null)
            return SetStatus(false, "Исход отмены неизвестен; требуется сверка 008/009.", order.Asset,
                order.IntentId);
        if (order.FilledQuantity > 0m || order.PositionQuantity > 0m
            || order.EntryTerminalState == ManagedEntryTerminalState.Filled)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Исполнение во время отмены оставлено под защитным сопровождением 009; замена заблокирована.",
                CancellationToken.None).ConfigureAwait(false);
            return SetStatus(true, "Исполнение сверено; защита позиции сохраняется, новая заявка не отправлена.",
                order.Asset, order.IntentId);
        }
        if (order.EntryTerminalState == ManagedEntryTerminalState.Cancelled
            && order.RemainingQuantity == 0m && order.PositionQuantity == 0m)
        {
            if (reassessment.Decision?.Outcome == LimitReassessmentOutcome.InvalidNoReplacement)
            {
                await RemoveLimitReassessmentAsync(reassessment,
                    "Неактуальный Limit отменён; сетапа на замену нет.", CancellationToken.None)
                    .ConfigureAwait(false);
                return SetStatus(true, "Неактуальный Limit отменён без замены.", order.Asset, order.IntentId);
            }
            if (reassessment.Decision?.Outcome == LimitReassessmentOutcome.Replacement)
            {
                var awaiting = reassessment with
                {
                    Phase = LimitReassessmentPhase.ReplacementReady,
                    CancellationConfirmedAt = _clock.UtcNow,
                    ReplacementIntervalMinutes = profile.Settings.LimitReassessmentMinutes,
                    ReplacementIntent = null,
                    LastStatus = "Старый остаток отменён без экспозиции; ожидается новый допуск 007."
                };
                if (!await SaveLimitReassessmentAsync(awaiting, CancellationToken.None).ConfigureAwait(false))
                    return SetStatus(false, "Подтверждённая отмена сохранена, но продолжение замены не записано.",
                        order.Asset, order.IntentId);
                return await ContinueLimitReplacementAsync(awaiting,
                    _contexts.GetRoutes(profile).ToArray(), profile, cancellationToken).ConfigureAwait(false);
            }
        }

        if (order.EntryTerminalState == ManagedEntryTerminalState.Open
            && order.LastAppliedOperationId?.StartsWith(order.IntentId + ":cancel:", StringComparison.Ordinal) == true)
            return await RecordLimitInconclusiveAsync(reassessment,
                "Точная отмена отклонена; остаток сохранён, повтор отмены заблокирован до новой переоценки.",
                CancellationToken.None).ConfigureAwait(false);

        return SetStatus(false, "Отмена не подтверждена; замена не отправлена, требуется сверка 009.",
            order.Asset, order.IntentId);
    }

    private async Task<TradingLoopOutcome> ContinueLimitReplacementAsync(
        TradingLoopLimitReassessment reassessment,
        IReadOnlyList<TradingLoopRoute> routes,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var oldOrder = _orders.Orders.SingleOrDefault(item => item.IntentId == reassessment.IntentId);
        if (oldOrder is null || reassessment.Decision?.ReplacementResponse is not { } response
            || reassessment.CancellationConfirmedAt is null)
            return BlockGlobally("Сохранённая замена не имеет полной связи с отменённым Limit.");
        if (reassessment.Phase is not (LimitReassessmentPhase.ReplacementReady
            or LimitReassessmentPhase.ReplacementPendingEntry))
            return BlockGlobally("Сохранённая замена находится вне ожидаемого этапа A.");
        if (reassessment.Phase == LimitReassessmentPhase.ReplacementPendingEntry
            && (_state.PendingSetup?.ReplacesIntentId != reassessment.IntentId
                || _state.PendingSetup.PreparedEntry is null
                || reassessment.ReplacementIntent != _state.PendingSetup.PreparedEntry
                || _state.PendingSetup.Request.Id != reassessment.Request.Id
                || _state.PendingSetup.Response != reassessment.Decision?.ReplacementResponse
                || _state.PendingSetup.ReceivedAt != reassessment.DecisionReceivedAt))
            return BlockGlobally("Восстановление старой замены без точного IntentId запрещено.");
        if (oldOrder.ManagementMode == ManagedOrderManagementMode.Manual
            || oldOrder.FilledQuantity > 0m || oldOrder.PositionQuantity > 0m
            || oldOrder.EntryTerminalState == ManagedEntryTerminalState.Filled)
        {
            await RemoveLimitReassessmentAsync(reassessment,
                "Состояние владельца или исполнение после отмены заблокировало замену.",
                CancellationToken.None).ConfigureAwait(false);
            return SetStatus(false, "Замена не отправлена; состояние позиции остаётся под контролем 009.",
                oldOrder.Asset, oldOrder.IntentId);
        }
        if (oldOrder.PendingOperationId is not null)
            return SetStatus(false, "Замена ожидает подтверждения отмены по журналу 008/009.",
                oldOrder.Asset, oldOrder.IntentId);
        if (oldOrder.EntryTerminalState != ManagedEntryTerminalState.Cancelled
            || oldOrder.RemainingQuantity != 0m || oldOrder.PositionQuantity != 0m)
            return BlockGlobally("Замену остановлено: старый Limit не подтверждён как отменённый без экспозиции.");

        if (reassessment.DecisionReceivedAt is not { } decisionAt || decisionAt > _clock.UtcNow
            || _clock.UtcNow - decisionAt >= PendingSetupLifetime)
            return await RemoveLimitReassessmentAsync(reassessment,
                "Сохранённый сетап замены просрочен; новый вход не отправлен.", cancellationToken)
                .ConfigureAwait(false);

        var parsed = AiResponseAssessor.Assess(reassessment.Request, response);
        var fields = parsed.Outcome == AiTestOutcome.CompleteSetup ? parsed.Fields : null;
        var setup = fields is null ? null : ToEligibilitySetup(reassessment.Request, fields);
        if (setup is null || fields is null)
            return await RemoveLimitReassessmentAsync(reassessment,
                "Сохранённый сетап замены потерял полноту после подтверждённой отмены.", cancellationToken)
                .ConfigureAwait(false);
        if (profile.SelectedMode != "TrueNorth AI setups" || !profile.SavedOperatingIntent
            || profile.Settings.EntryType != "Limit"
            || !profile.Settings.Assets.Any(asset => string.Equals(asset?.Trim(), setup.Asset,
                StringComparison.OrdinalIgnoreCase)))
            return await RemoveLimitReassessmentAsync(reassessment,
                "Настройки изменились после отмены; сохранённый сетап замены устарел.", cancellationToken)
                .ConfigureAwait(false);

        var assetRoutes = routes.Where(route => route.Asset == setup.Asset).ToArray();
        if (assetRoutes.Length == 0 || !AiAnalysisRequestFactory.TryCreateBatch(setup.Asset,
                profile.Settings.EntryType, out _, out _))
            return await RemoveLimitReassessmentAsync(reassessment,
                "Маршрут или текущий формат входа не подтверждает сохранённый сетап замены.", cancellationToken)
                .ConfigureAwait(false);

        var accounts = await ReadAccountsAsync(routes, cancellationToken).ConfigureAwait(false);
        if (accounts.Values.Any(account => account is null))
            return BlockGlobally("После отмены не удалось подтвердить все счета для допуска новой заявки.");
        var snapshots = accounts.Values.Cast<AccountSnapshot>().ToArray();
        var contexts = new Dictionary<TradingLoopRoute, TradingLoopContext>();
        foreach (var route in assetRoutes)
        {
            var context = await _contexts.ReadAsync(route, snapshots, setup, profile, _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            contexts[route] = context;
            if (context.AllConnectedExposureCovered != true)
                return BlockGlobally(context.Reason ?? "Экспозиция после отмены не подтверждена.");
        }
        var analysisRoute = FindEligibleSearchRoute(assetRoutes, accounts, contexts, profile, out var waitingReason);
        if (analysisRoute is null)
            return SetStatus(false, waitingReason ?? "Сетап замены ждёт свежего допуска 007.",
                setup.Asset, reassessment.IntentId);

        var intervalMinutes = profile.Settings.LimitReassessmentMinutes;
        var pending = new TradingLoopPendingSetup(reassessment.Request, response,
            decisionAt, intervalMinutes,
            reassessment.Phase == LimitReassessmentPhase.ReplacementPendingEntry
                ? reassessment.ReplacementIntent : null,
            reassessment.IntentId);
        var opened = await EvaluatePendingSetupAsync(pending, analysisRoute, routes, profile, cancellationToken)
            .ConfigureAwait(false);
        if (opened.IntentId is null)
            return opened;

        var newOrder = _orders.Orders.SingleOrDefault(item => item.IntentId == opened.IntentId);
        if (newOrder is null)
            return BlockGlobally("Новая заявка не связалась с восстановленным намерением после отмены.");
        var statesAfterEntry = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal);
        statesAfterEntry.Remove(reassessment.IntentId);
        if (newOrder.EntryType == "Limit" && ManagedOrderState.BlocksNewEntry(newOrder))
            statesAfterEntry[newOrder.IntentId] = CreateLimitReassessment(newOrder, reassessment.Request, response,
                reassessment.DecisionReceivedAt ?? _clock.UtcNow, intervalMinutes);
        if (!await SaveLoopStateAsync(_state with
            {
                PendingSetup = null,
                LimitReassessments = statesAfterEntry,
                LastStatus = opened.Status
            }, CancellationToken.None).ConfigureAwait(false))
            return SetStatus(false, "Новая заявка отправлена, но состояние замены не удалось сохранить.",
                newOrder.Asset, newOrder.IntentId);
        if (!await _recovery.ClearTradingLoopFailureAsync(_state, newOrder.Asset, CancellationToken.None)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Ошибка замены не закрыта в журнале; новые входы заблокированы.",
                newOrder.Asset, newOrder.IntentId);
        }
        _state = _recovery.TradingLoopState;
        return opened;
    }

    private async Task<TradingLoopOutcome> ScheduleLimitReassessmentAsync(
        TradingLoopLimitReassessment reassessment,
        string status,
        CancellationToken cancellationToken)
    {
        var scheduled = reassessment with
        {
            Phase = reassessment.EntryOrderId is null
                ? LimitReassessmentPhase.WaitingForEntry : LimitReassessmentPhase.Scheduled,
            AssessmentId = null,
            Decision = null,
            DecisionReceivedAt = null,
            CancellationConfirmedAt = null,
            NextReassessmentAt = _clock.UtcNow.AddMinutes(reassessment.IntervalMinutes),
            LastStatus = status
        };
        if (!await SaveLimitReassessmentAsync(scheduled, cancellationToken).ConfigureAwait(false))
            return SetStatus(false, "Следующий срок переоценки Limit не удалось сохранить.",
                reassessment.Request.Asset, reassessment.IntentId);
        if (!await _recovery.ClearTradingLoopFailureAsync(_state, reassessment.Request.Asset, cancellationToken)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Состояние ошибки переоценки не удалось закрыть; входы заблокированы.",
                reassessment.Request.Asset, reassessment.IntentId);
        }
        _state = _recovery.TradingLoopState;
        return SetStatus(true, status, reassessment.Request.Asset, reassessment.IntentId);
    }

    private async Task<TradingLoopOutcome> RecordLimitInconclusiveAsync(
        TradingLoopLimitReassessment reassessment,
        string reason,
        CancellationToken cancellationToken)
    {
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "Переоценка Limit не дала однозначного результата."
            : reason.Trim();
        var scheduled = reassessment with
        {
            Phase = reassessment.EntryOrderId is null
                ? LimitReassessmentPhase.WaitingForEntry : LimitReassessmentPhase.Scheduled,
            AssessmentId = null,
            Decision = null,
            DecisionReceivedAt = null,
            CancellationConfirmedAt = null,
            NextReassessmentAt = _clock.UtcNow.AddMinutes(reassessment.IntervalMinutes),
            LastStatus = detail
        };
        var states = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal)
        {
            [scheduled.IntentId] = scheduled
        };
        var updated = _state with
        {
            LimitReassessments = states,
            LastStatus = detail
        };
        if (!await _recovery.SaveTradingLoopFailureAsync(updated, reassessment.Request.Asset,
                "Переоценка Limit не завершена; исходная заявка сохранена.", detail, cancellationToken)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Причину переоценки не удалось сохранить; новые входы закрыты.",
                reassessment.Request.Asset, reassessment.IntentId);
        }
        _state = _recovery.TradingLoopState;
        _lastStatus = _state.LastStatus;
        return SetStatus(false, detail + $" Повтор после {scheduled.NextReassessmentAt:O}.",
            reassessment.Request.Asset, reassessment.IntentId);
    }

    private async Task<TradingLoopOutcome> RemoveLimitReassessmentAsync(
        TradingLoopLimitReassessment reassessment,
        string status,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal);
        states.Remove(reassessment.IntentId);
        if (!await SaveLoopStateAsync(_state with
            {
                LimitReassessments = states,
                LastStatus = status
            }, cancellationToken).ConfigureAwait(false))
            return SetStatus(false, "Состояние переоценки Limit не удалось завершить в журнале.",
                reassessment.Request.Asset, reassessment.IntentId);
        if (!await _recovery.ClearTradingLoopFailureAsync(_state, reassessment.Request.Asset, cancellationToken)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Ошибка переоценки не закрыта в журнале; новые входы заблокированы.",
                reassessment.Request.Asset, reassessment.IntentId);
        }
        _state = _recovery.TradingLoopState;
        return SetStatus(true, status, reassessment.Request.Asset, reassessment.IntentId);
    }

    private async Task<bool> SaveLimitReassessmentAsync(
        TradingLoopLimitReassessment reassessment,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal)
        {
            [reassessment.IntentId] = reassessment
        };
        return await SaveLoopStateAsync(_state with
        {
            LimitReassessments = states,
            LastStatus = reassessment.LastStatus
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool LimitCycleAllowsAction(SettingsProfile expectedProfile, long generation)
    {
        var current = Volatile.Read(ref _profile);
        return _admission.IsAdmitting && _admission.Generation == generation
            && Volatile.Read(ref _profileUpdateInProgress) == 0 && Volatile.Read(ref _stopInProgress) == 0
            && current.SavedOperatingIntent && current.SelectedMode == "TrueNorth AI setups"
            && expectedProfile.SavedOperatingIntent && expectedProfile.SelectedMode == "TrueNorth AI setups";
    }

    private static TradingLoopLimitReassessment CreateLimitReassessment(
        ManagedOrderState order,
        AiAnalysisRequest request,
        AiSourceResponse response,
        DateTimeOffset receivedAt,
        int intervalMinutes) => new(
        order.IntentId,
        order.EntryOrderId,
        request,
        response,
        receivedAt,
        intervalMinutes,
        order.CreatedAt.AddMinutes(intervalMinutes),
        order.EntryOrderId is not null && order.EntryTerminalState == ManagedEntryTerminalState.Open
            ? LimitReassessmentPhase.Scheduled : LimitReassessmentPhase.WaitingForEntry);

    private async Task<TradingLoopOutcome?> DropExpiredCandidateAsync(CancellationToken cancellationToken)
    {
        var pending = _state.PendingSetup;
        if (pending is null || pending.ReceivedAt is { } at
            && at <= _clock.UtcNow && _clock.UtcNow - at < PendingSetupLifetime)
            return null;

        var asset = pending.Request.Asset;
        var status = pending.ReceivedAt is null || pending.ReceivedAt > _clock.UtcNow
            ? "Сохранённый сетап отброшен: время ответа отсутствует или находится в будущем."
            : "Сохранённый сетап отброшен по границе возраста 24 часа без повторной оценки.";
        if (!await SaveLoopStateAsync(_state with { PendingSetup = null, LastStatus = status }, cancellationToken)
                .ConfigureAwait(false))
            return SetStatus(false, "Просроченный сетап не удалось безопасно удалить из журнала.", asset);
        _pendingSetupLoadedAtStartup = false;
        return SetStatus(false, status, asset);
    }

    private async Task<TradingLoopOutcome> SearchAssetAsync(
        TradingLoopRoute route,
        IReadOnlyList<TradingLoopRoute> routes,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        if (!_admission.IsAdmitting || !profile.SavedOperatingIntent
            || profile.SelectedMode != "TrueNorth AI setups"
            || !_recovery.IsEntryAllowed(route.Venue, profile.SavedOperatingIntent))
            return SetStatus(false, "Новые входы закрыты перед запросом AI.", route.Asset);

        if (!AiAnalysisRequestFactory.TryCreateBatch(route.Asset, profile.Settings.EntryType,
                out var requests, out var requestError) || requests.Count != 1)
            return SetStatus(false, requestError, route.Asset);
        var request = requests[0];

        AiSourceResponse response;
        try
        {
            response = await _analysis.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SetStatus(false, "Запрос AI отменён; ответ не может создать вход.", route.Asset);
        }
        catch (Exception exception)
        {
            return await FailAssetAsync(route.Asset, "Анализ AI завершился ошибкой ("
                + SafeFailureKind(exception) + ").",
                "Ошибка AI; повторный поиск назначен по расписанию.", cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || !_admission.IsAdmitting)
            return SetStatus(false, "Ответ AI устарел после Stop и не может создать вход.", route.Asset);

        if (!response.IsFinalStream || response.ResponseRequestId != request.Id)
            return await FailAssetAsync(route.Asset,
                response.ResponseRequestId != request.Id
                    ? "Ответ AI не связан с текущим запросом."
                    : "Поток AI не завершён.",
                "Ответ AI неполный; повторный поиск назначен по расписанию.", cancellationToken).ConfigureAwait(false);

        var assessed = AiResponseAssessor.Assess(request, response);
        if (assessed.Outcome == AiTestOutcome.ValidNoSetup)
            return await RecordNoSetupAsync(route.Asset, profile, cancellationToken).ConfigureAwait(false);
        if (assessed.Outcome != AiTestOutcome.CompleteSetup || assessed.Fields is null)
            return await FailAssetAsync(route.Asset,
                "Ответ AI неполный: " + string.Join(" ", assessed.Reasons),
                "Ответ AI неполный; повторный поиск назначен по расписанию.", cancellationToken).ConfigureAwait(false);

        var pending = new TradingLoopPendingSetup(request, response, _clock.UtcNow,
            profile.Settings.LimitReassessmentMinutes);
        var saved = await SaveAcceptedPendingSetupAsync(pending, profile, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded)
            return saved;
        _pendingSetupLoadedAtStartup = false;
        return await EvaluatePendingSetupAsync(pending, route, routes, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TradingLoopOutcome> ReassessPersistedSetupAsync(
        TradingLoopPendingSetup pending,
        TradingLoopRoute analysisRoute,
        IReadOnlyList<TradingLoopRoute> routes,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var origin = AiResponseAssessor.Assess(pending.Request, pending.Response);
        if (origin.Outcome != AiTestOutcome.CompleteSetup || origin.Fields is null
            || pending.ReceivedAt is not { } receivedAt
            || ToEligibilitySetup(pending.Request, origin.Fields) is not { } originSetup)
            return await FailPendingReassessmentAsync(pending,
                "Сохранённый сетап не имеет проверяемой исходной связи с ответом.",
                "Сохранённый сетап ожидает новой ясной переоценки.", profile, cancellationToken).ConfigureAwait(false);

        if (_analysis is not ITradingLoopReassessmentSource reassessmentSource)
            return await FailPendingReassessmentAsync(pending,
                "Тестовый источник не поддерживает явную переоценку сохранённого сетапа.",
                "Сохранённый сетап ожидает новой ясной переоценки.", profile, cancellationToken).ConfigureAwait(false);

        AiSourceResponse response;
        try
        {
            response = await reassessmentSource.ReassessAsync(
                new TradingLoopReassessmentContext(pending.Request, pending.Response, origin.Fields, receivedAt),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SetStatus(false, "Переоценка отменена; сохранённый сетап и срок повтора сохранены.",
                pending.Request.Asset);
        }
        catch (Exception exception)
        {
            return await FailPendingReassessmentAsync(pending,
                "Переоценка сохранённого сетапа завершилась ошибкой (" + SafeFailureKind(exception) + ").",
                "Сохранённый сетап ожидает новой ясной переоценки.", profile, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || !_admission.IsAdmitting)
            return SetStatus(false, "Поздняя переоценка после Stop не может создать вход.", pending.Request.Asset);

        if (response.ResponseRequestId != pending.Request.Id || !response.IsFinalStream)
            return await FailPendingReassessmentAsync(pending,
                response.ResponseRequestId != pending.Request.Id
                    ? "Переоценка не связана с исходным запросом."
                    : "Переоценка не завершена.",
                "Сохранённый сетап ожидает новой ясной переоценки.", profile, cancellationToken).ConfigureAwait(false);

        var assessed = AiResponseAssessor.Assess(pending.Request, response);
        if (assessed.Outcome == AiTestOutcome.ValidNoSetup)
            return await RecordNoSetupAsync(pending.Request.Asset, profile, cancellationToken).ConfigureAwait(false);
        if (assessed.Outcome != AiTestOutcome.CompleteSetup || assessed.Fields is null
            || ToEligibilitySetup(pending.Request, assessed.Fields) is null)
            return await FailPendingReassessmentAsync(pending,
                "Переоценка не подтвердила полный и свежий сетап.",
                "Сохранённый сетап ожидает новой ясной переоценки.", profile, cancellationToken).ConfigureAwait(false);

        var accepted = new TradingLoopPendingSetup(pending.Request, response, _clock.UtcNow,
            profile.Settings.LimitReassessmentMinutes);
        var saved = await SaveAcceptedPendingSetupAsync(accepted, profile, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded)
            return saved;
        _pendingSetupLoadedAtStartup = false;
        return await EvaluatePendingSetupAsync(accepted, analysisRoute, routes, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TradingLoopOutcome> EvaluatePendingSetupAsync(
        TradingLoopPendingSetup pending,
        TradingLoopRoute analysisRoute,
        IReadOnlyList<TradingLoopRoute> routes,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var asset = pending.Request.Asset;
        var assessed = AiResponseAssessor.Assess(pending.Request, pending.Response);
        if (assessed.Outcome != AiTestOutcome.CompleteSetup || assessed.Fields is null)
            return await DeferPendingSetupAsync(pending,
                "Сохранённый сетап не прошёл повторную проверку ответа.", profile, cancellationToken)
                .ConfigureAwait(false);

        var setup = ToEligibilitySetup(pending.Request, assessed.Fields);
        if (setup is null)
            return await DeferPendingSetupAsync(pending,
                "Время рыночной опоры сетапа неизвестно; вход отложен.", profile, cancellationToken)
                .ConfigureAwait(false);

        var accounts = await ReadAccountsAsync(routes, cancellationToken).ConfigureAwait(false);
        if (accounts.Values.Any(account => account is null))
            return BlockGlobally("После ответа AI не удалось подтвердить каждый счёт и связанную экспозицию.");
        var snapshots = accounts.Values.Cast<AccountSnapshot>().ToArray();

        var contexts = new Dictionary<TradingLoopRoute, TradingLoopContext>();
        foreach (var route in routes.Where(route => route.Asset == asset))
        {
            var context = await _contexts.ReadAsync(route, snapshots, setup, profile, _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            contexts[route] = context;
            if (context.AllConnectedExposureCovered != true)
                return BlockGlobally(context.Reason ?? "Связь всей экспозиции после ответа AI неизвестна.");
        }

        if (!contexts.TryGetValue(analysisRoute, out var candidateContext)
            || candidateContext.AllConnectedExposureCovered != true
            || candidateContext.EligibilityInput is not { } input
            || !ExactInputMatches(pending.Request, setup, profile, input, snapshots))
            return await DeferPendingSetupAsync(pending,
                "Текущие данные допуска не связаны с точными свежими снимками счетов.", profile,
                cancellationToken).ConfigureAwait(false);

        input = input with { EvaluatedAt = _clock.UtcNow };
        var eligibility = EntryEligibilityEvaluator.Evaluate(input);
        var pool = eligibility.SelectedPool;
        if (eligibility.Status != EntryEligibilityStatus.ScenarioReady || pool is null || pool.Quantity is not > 0)
            return await DeferPendingSetupAsync(pending,
                "Допуск 007 ожидает свежих условий: " + string.Join(" ", eligibility.Reasons),
                profile, cancellationToken).ConfigureAwait(false);

        var assetRoutes = routes.Where(route => route.Asset == asset).ToArray();
        var selectedRoute = assetRoutes.SingleOrDefault(route => route.Venue == pool.Venue
            && route.Account == pool.Account && route.Contract == pool.Contract);
        if (selectedRoute is null || !contexts.TryGetValue(selectedRoute, out var selectedContext)
            || !selectedContext.RouteAvailable || !_recovery.IsEntryAllowed(pool.Venue, profile.SavedOperatingIntent))
            return await DeferPendingSetupAsync(pending,
                "007 выбрал маршрут, который не подтверждён текущей доступностью и сверкой.", profile,
                cancellationToken).ConfigureAwait(false);

        if (setup.Direction is null || assessed.Fields.EntryPrice is <= 0)
            return await DeferPendingSetupAsync(pending,
                "Полный сетап не содержит точных уровней для входа.", profile, cancellationToken)
                .ConfigureAwait(false);

        var preparedCandidate = new TradingLoopPreparedEntry(Guid.NewGuid().ToString("N"),
            pending.Request.Id, selectedRoute.Venue, selectedRoute.Account, selectedRoute.Contract,
            asset, pending.Request.EntryType, setup.Direction.Value, pool.Quantity.Value,
            pending.Request.OrderType == "LIMIT" ? assessed.Fields.EntryPrice : null,
            assessed.Fields.TakeProfit!.Value, assessed.Fields.StopLoss!.Value);
        var preparedEntry = pending.PreparedEntry is { } priorPrepared
            && priorPrepared with { IntentId = preparedCandidate.IntentId } == preparedCandidate
                ? priorPrepared
                : preparedCandidate;
        pending = pending with { PreparedEntry = preparedEntry };

        var statesBeforeEntry = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal);
        if (pending.ReplacesIntentId is { } replacedIntentId)
        {
            if (!statesBeforeEntry.TryGetValue(replacedIntentId, out var replaced)
                || replaced.Phase is not (LimitReassessmentPhase.ReplacementReady
                    or LimitReassessmentPhase.ReplacementPendingEntry)
                || replaced.Decision?.Outcome != LimitReassessmentOutcome.Replacement
                || replaced.Decision?.ReplacementResponse != pending.Response
                || replaced.Request.Id != pending.Request.Id
                || replaced.DecisionReceivedAt != pending.ReceivedAt
                || replaced.CancellationConfirmedAt is null || replaced.IntentId == preparedEntry.IntentId)
                return BlockGlobally("Новый IntentId замены не связан с отменённым Limit.");
            statesBeforeEntry[replacedIntentId] = replaced with
            {
                Phase = LimitReassessmentPhase.ReplacementPendingEntry,
                ReplacementIntent = preparedEntry,
                ReplacementIntervalMinutes = pending.LimitReassessmentMinutes
                    ?? profile.Settings.LimitReassessmentMinutes,
                LastStatus = "IntentId и точный маршрут замены сохранены до границы передачи."
            };
        }
        if (!await SaveLoopStateAsync(_state with
            {
                PendingSetup = pending,
                LimitReassessments = statesBeforeEntry,
                LastStatus = "Точный IntentId и маршрут входа сохранены до границы передачи."
            }, cancellationToken).ConfigureAwait(false))
            return SetStatus(false, "Точный IntentId не записан; вход не начат.", asset);

        var intent = new ManagedOrderIntent(selectedRoute.Venue, selectedRoute.Account, asset,
            selectedRoute.Contract, setup.Direction.Value, pending.Request.EntryType, pool.Quantity.Value,
            assessed.Fields.EntryPrice, assessed.Fields.TakeProfit!.Value, assessed.Fields.StopLoss!.Value,
            preparedEntry.IntentId);
        var generation = _admission.Generation;
        bool Fence(Func<bool> beginTransfer) => _admission.RunIfAdmitting(() =>
            Volatile.Read(ref _profileUpdateInProgress) == 0 && Volatile.Read(ref _stopInProgress) == 0
            && _admission.Generation == generation
            && CurrentProfileAllowsEntry(intent, pending.Request, setup, profile, input, snapshots)
            && beginTransfer());

        var opened = await _orders.OpenAsync(intent, input, savedOperatingIntent: true,
            cancellationToken: cancellationToken, entryAdmissionFence: Fence).ConfigureAwait(false);
        if (opened.IntentId is not null)
        {
            if (opened.IntentId != preparedEntry.IntentId)
                return BlockGlobally("Order Lifecycle вернул другой IntentId; связь входа не доказана.");
            var managed = _orders.Orders.SingleOrDefault(order => order.IntentId == opened.IntentId);
            if (managed is null)
            {
                _admission.SetGlobalBlock(true);
                return SetStatus(false, "Результат входа не связан с сохранённым намерением; сверка обязательна.",
                    asset, opened.IntentId);
            }

            var limitReassessments = new Dictionary<string, TradingLoopLimitReassessment>(
                _state.LimitReassessments, StringComparer.Ordinal);
            if (pending.ReplacesIntentId is { } oldIntentId)
                limitReassessments.Remove(oldIntentId);
            if (managed.EntryType == "Limit" && ManagedOrderState.BlocksNewEntry(managed))
            {
                var interval = pending.LimitReassessmentMinutes ?? profile.Settings.LimitReassessmentMinutes;
                limitReassessments[managed.IntentId] = new TradingLoopLimitReassessment(
                    managed.IntentId,
                    managed.EntryOrderId,
                    pending.Request,
                    pending.Response,
                    pending.ReceivedAt ?? _clock.UtcNow,
                    interval,
                    managed.CreatedAt.AddMinutes(interval),
                    managed.EntryTerminalState == ManagedEntryTerminalState.Open && managed.EntryOrderId is not null
                        ? LimitReassessmentPhase.Scheduled : LimitReassessmentPhase.WaitingForEntry);
            }

            if (!await SaveLoopStateAsync(_state with
                {
                    PendingSetup = null,
                    LimitReassessments = limitReassessments,
                    LastStatus = opened.Status
                }, CancellationToken.None).ConfigureAwait(false))
                return SetStatus(false, "Действие записано в журнале, но состояние сетапа не удалось очистить.",
                    asset, opened.IntentId);
            _pendingSetupLoadedAtStartup = false;
        }
        else if (!opened.Succeeded)
        {
            return await DeferPendingSetupAsync(pending, opened.Status, profile, cancellationToken)
                .ConfigureAwait(false);
        }
        return SetStatus(opened.Succeeded, opened.Status, asset, opened.IntentId);
    }

    private async Task<TradingLoopOutcome> SaveAcceptedPendingSetupAsync(
        TradingLoopPendingSetup pending,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, DateTimeOffset>(_state.NextSearchAt, StringComparer.Ordinal);
        next.Remove(pending.Request.Asset);
        var state = _state with
        {
            NextSearchAt = next,
            PendingSetup = pending,
            LastStatus = $"Новый ответ для {pending.Request.Asset} сохранён и ожидает свежей проверки риска."
        };
        if (!await SaveLoopStateAsync(state, cancellationToken).ConfigureAwait(false))
            return SetStatus(false, "Ответ AI получен, но не сохранён; вход не начат.", pending.Request.Asset);
        if (!await _recovery.ClearTradingLoopFailureAsync(_state, pending.Request.Asset, cancellationToken)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Сетап сохранён, но состояние прежнего сбоя не записано.", pending.Request.Asset);
        }
        _state = _recovery.TradingLoopState;
        _pendingSetupLoadedAtStartup = false;
        return SetStatus(true, _state.LastStatus ?? "Сетап сохранён.", pending.Request.Asset);
    }

    private async Task<TradingLoopOutcome> RecordNoSetupAsync(
        string asset,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, DateTimeOffset>(_state.NextSearchAt, StringComparer.Ordinal)
        {
            [asset] = _clock.UtcNow.AddMinutes(profile.Settings.NewSetupRetryMinutes)
        };
        var updated = _state with
        {
            NextSearchAt = next,
            PendingSetup = null,
            LastStatus = $"NO_SETUP для {asset}; повтор через {profile.Settings.NewSetupRetryMinutes} минут."
        };
        if (!await _recovery.ClearTradingLoopFailureAsync(updated, asset, cancellationToken).ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Срок NO_SETUP не записан; новые входы закрыты.", asset);
        }
        _state = _recovery.TradingLoopState;
        _pendingSetupLoadedAtStartup = false;
        _lastStatus = _state.LastStatus;
        return SetStatus(true, _lastStatus!, asset);
    }

    private async Task<TradingLoopOutcome> FailPendingReassessmentAsync(
        TradingLoopPendingSetup pending,
        string detail,
        string safeSummary,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var due = _clock.UtcNow.AddMinutes(profile.Settings.NewSetupRetryMinutes);
        var next = new Dictionary<string, DateTimeOffset>(_state.NextSearchAt, StringComparer.Ordinal)
        {
            [pending.Request.Asset] = due
        };
        var updated = _state with { NextSearchAt = next, PendingSetup = pending, LastStatus = detail };
        if (!await _recovery.SaveTradingLoopFailureAsync(updated, pending.Request.Asset, safeSummary, detail,
                cancellationToken).ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Ошибка переоценки не удалось записать; входы закрыты.", pending.Request.Asset);
        }
        _state = _recovery.TradingLoopState;
        _lastStatus = _state.LastStatus;
        return SetStatus(false, detail + $" Повтор после {due:O}.", pending.Request.Asset);
    }

    private async Task<TradingLoopOutcome> DeferPendingSetupAsync(
        TradingLoopPendingSetup pending,
        string reason,
        SettingsProfile profile,
        CancellationToken cancellationToken)
    {
        var retrySeconds = Math.Clamp(profile.Settings.RecoveryCheckSeconds,
            (int)PendingEligibilityRetryMinimum.TotalSeconds,
            (int)PendingEligibilityRetryMaximum.TotalSeconds);
        var due = _clock.UtcNow.AddSeconds(retrySeconds);
        var next = new Dictionary<string, DateTimeOffset>(_state.NextSearchAt, StringComparer.Ordinal)
        {
            [pending.Request.Asset] = due
        };
        if (!await SaveLoopStateAsync(_state with
            {
                NextSearchAt = next,
                PendingSetup = pending,
                LastStatus = reason
            }, cancellationToken).ConfigureAwait(false))
            return SetStatus(false, "Временное ожидание сетапа не удалось сохранить; входы закрыты.",
                pending.Request.Asset);
        return SetStatus(false, reason + $" Повтор проверки после {due:O}.", pending.Request.Asset);
    }

    private TradingLoopRoute? FindEligibleSearchRoute(
        IReadOnlyList<TradingLoopRoute> assetRoutes,
        IReadOnlyDictionary<(string Venue, string Account), AccountSnapshot?> accounts,
        IReadOnlyDictionary<TradingLoopRoute, TradingLoopContext> contexts,
        SettingsProfile profile,
        out string? waitingReason)
    {
        var sawAccount = false;
        var sawAllowed = false;
        var sawRoute = false;
        waitingReason = null;
        foreach (var route in assetRoutes)
        {
            if (!accounts.TryGetValue((route.Venue, route.Account), out var account) || account is null)
                continue;
            sawAccount = true;
            if (!_recovery.IsEntryAllowed(route.Venue, profile.SavedOperatingIntent))
                continue;
            sawAllowed = true;
            if (!contexts.TryGetValue(route, out var context) || !context.RouteAvailable)
                continue;
            sawRoute = true;
            if (context.MinimumEntryAffordable == false || !HasAvailableFunds(account))
                continue;
            return route;
        }

        waitingReason = !sawAccount
            ? "Нет свежего снимка доступного точного счёта; поиск отложен."
            : !sawAllowed
                ? "Все точные маршруты ожидают сверки 008."
                : !sawRoute
                    ? "Проверенный локальный маршрут недоступен; AI не запрашивался."
                    : "Недостаточно средств для подтверждённого минимума; AI не запрашивался.";
        return null;
    }

    private async Task<TradingLoopOutcome> FailAssetAsync(
        string asset,
        string detail,
        string safeSummary,
        CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, DateTimeOffset>(_state.NextSearchAt, StringComparer.Ordinal)
        {
            [asset] = _clock.UtcNow.AddMinutes(Volatile.Read(ref _profile).Settings.NewSetupRetryMinutes)
        };
        var updated = _state with { NextSearchAt = next, LastStatus = detail };
        if (!await _recovery.SaveTradingLoopFailureAsync(updated, asset, safeSummary, detail, cancellationToken)
                .ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return SetStatus(false, "Ошибка цикла не удалось записать; новые входы закрыты.", asset);
        }
        _state = _recovery.TradingLoopState;
        _lastStatus = _state.LastStatus;
        return SetStatus(false, detail, asset);
    }

    private async Task<bool> InvalidateLimitDecisionsOnStopAsync(CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, TradingLoopLimitReassessment>(
            _state.LimitReassessments, StringComparer.Ordinal);
        var replacementIds = _state.LimitReassessments.Values
            .Where(item => item.Phase is LimitReassessmentPhase.ReplacementReady
                or LimitReassessmentPhase.ReplacementPendingEntry)
            .Select(item => item.IntentId).ToHashSet(StringComparer.Ordinal);
        var replacementRequestIds = _state.LimitReassessments.Values
            .Where(item => replacementIds.Contains(item.IntentId))
            .Select(item => item.Request.Id).ToHashSet();
        var changed = false;
        foreach (var item in _state.LimitReassessments.Values.ToArray())
        {
            if (replacementIds.Contains(item.IntentId))
            {
                states.Remove(item.IntentId);
                changed = true;
                continue;
            }
            if (item.Decision is null)
                continue;

            var order = _orders.Orders.SingleOrDefault(candidate => candidate.IntentId == item.IntentId);
            if (order is null || order.ManagementMode == ManagedOrderManagementMode.Manual
                || order.EntryTerminalState is ManagedEntryTerminalState.Cancelled or ManagedEntryTerminalState.Filled
                    or ManagedEntryTerminalState.Rejected)
            {
                states.Remove(item.IntentId);
                changed = true;
                continue;
            }

            states[item.IntentId] = item with
            {
                EntryOrderId = order.EntryOrderId,
                Phase = order.EntryOrderId is null
                    ? LimitReassessmentPhase.WaitingForEntry : LimitReassessmentPhase.Scheduled,
                AssessmentId = null,
                Decision = null,
                DecisionReceivedAt = null,
                CancellationConfirmedAt = null,
                NextReassessmentAt = _clock.UtcNow.AddMinutes(item.IntervalMinutes),
                LastStatus = "Stop сбросил позднее решение; следующий ответ требует новой переоценки."
            };
            changed = true;
        }

        var pendingSetup = _state.PendingSetup;
        if (pendingSetup is not null && (replacementRequestIds.Contains(pendingSetup.Request.Id)
                                         || pendingSetup.ReplacesIntentId is { } replacesId
                                         && replacementIds.Contains(replacesId)))
        {
            pendingSetup = null;
            changed = true;
        }
        if (!changed)
            return true;

        if (!await _recovery.SaveTradingLoopStateAsync(_state with
            {
                LimitReassessments = states,
                PendingSetup = pendingSetup,
                LastStatus = "Stop сбросил ожидающие решения переоценки Limit."
            }, cancellationToken).ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return false;
        }
        _state = _recovery.TradingLoopState;
        _lastStatus = _state.LastStatus;
        return true;
    }

    private async Task<bool> SaveLoopStateAsync(TradingLoopState state, CancellationToken cancellationToken)
    {
        if (!await _recovery.SaveTradingLoopStateAsync(state, cancellationToken).ConfigureAwait(false))
        {
            _admission.SetGlobalBlock(true);
            return false;
        }
        _state = _recovery.TradingLoopState;
        _lastStatus = _state.LastStatus;
        if (state.PendingSetup is null)
            _pendingSetupLoadedAtStartup = false;
        return true;
    }

    private bool CurrentProfileAllowsEntry(
        ManagedOrderIntent intent,
        AiAnalysisRequest request,
        EntryEligibilitySetup setup,
        SettingsProfile expectedProfile,
        EntryEligibilityInput input,
        IReadOnlyList<AccountSnapshot> currentAccounts)
    {
        var current = Volatile.Read(ref _profile);
        return current.SavedOperatingIntent && current.SelectedMode == "TrueNorth AI setups"
            && _admission.IsStarted
            && current.Settings.NewSetupRetryMinutes == expectedProfile.Settings.NewSetupRetryMinutes
            && current.Settings.EntryType == expectedProfile.Settings.EntryType
            && current.Settings.MarginBalancePercent == expectedProfile.Settings.MarginBalancePercent
            && current.Settings.Leverage == expectedProfile.Settings.Leverage
            && ExactInputMatches(request, setup, expectedProfile, input, currentAccounts)
            && EntryEligibilityEvaluator.Evaluate(input with { EvaluatedAt = _clock.UtcNow }) is
                { Status: EntryEligibilityStatus.ScenarioReady,
                    SelectedPool: { } selected }
            && selected.Venue == intent.Venue && selected.Account == intent.Account
            && selected.Contract == intent.Contract && selected.Quantity == intent.Quantity;
    }

    private static bool ExactInputMatches(
        AiAnalysisRequest request,
        EntryEligibilitySetup setup,
        SettingsProfile profile,
        EntryEligibilityInput input,
        IReadOnlyList<AccountSnapshot> currentAccounts)
    {
        if (input.Settings.SelectedMode != profile.SelectedMode || !input.Settings.SavedOperatingIntent
            || input.Settings.Settings.EntryType != profile.Settings.EntryType
            || input.Settings.Settings.MarginBalancePercent != profile.Settings.MarginBalancePercent
            || input.Settings.Settings.Leverage != profile.Settings.Leverage
            || !input.Settings.Settings.Assets.SequenceEqual(profile.Settings.Assets, StringComparer.OrdinalIgnoreCase)
            || input.Setup != setup || input.Setup.State != EntrySetupState.Complete
            || input.Setup.Asset != request.Asset || input.Setup.Market != request.Market
            || input.Setup.EntryType != request.EntryType
            || input.AllConnectedExposureCovered is not { Value: true }
            || input.Pools.Count != currentAccounts.Count)
            return false;

        foreach (var account in currentAccounts)
        {
            var matchingPools = input.Pools.Where(pool => pool.Account.Venue == account.Venue
                && pool.Account.Account == account.Account).ToArray();
            if (matchingPools.Length != 1 || !ReferenceEquals(matchingPools[0].Account, account))
                return false;
        }
        return true;
    }

    private static EntryEligibilitySetup? ToEligibilitySetup(AiAnalysisRequest request, AiParsedFields fields)
    {
        if (fields.Side is not ("LONG" or "SHORT") || fields.TakeProfit is not > 0 || fields.StopLoss is not > 0
            || !DateTimeOffset.TryParse(fields.ReferenceTimeUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var observedAt))
            return null;

        return new(EntrySetupState.Complete, request.Asset, request.Market, request.EntryType,
            fields.EntryPrice, fields.Side == "LONG" ? PositionDirection.Long : PositionDirection.Short,
            fields.TakeProfit, fields.StopLoss, "USDC/" + request.Asset,
            "TrueNorth AI setups response", observedAt.ToUniversalTime());
    }

    private static bool HasAvailableFunds(AccountSnapshot account) =>
        account.Balances.Value?.AvailableMargin?.Value is > 0m;

    private string? ValidateRoutes(SettingsProfile profile, IReadOnlyList<TradingLoopRoute> routes)
    {
        var assets = profile.Settings.Assets.Where(asset => !string.IsNullOrWhiteSpace(asset))
            .Select(asset => asset!.Trim().ToUpperInvariant()).ToArray();
        if (assets.Length == 0 || routes.Count == 0
            || !routes.Select(route => route.Asset).Distinct(StringComparer.Ordinal)
                .SequenceEqual(assets, StringComparer.Ordinal)
            || routes.Distinct().Count() != routes.Count
            || routes.Any(route => string.IsNullOrWhiteSpace(route.Venue) || string.IsNullOrWhiteSpace(route.Account)
                || string.IsNullOrWhiteSpace(route.Contract)))
            return "Область торговых маршрутов не совпадает с активами владельца; глобальный допуск закрыт.";
        return null;
    }

    private TradingLoopOutcome BlockGlobally(string reason)
    {
        _admission.SetGlobalBlock(true);
        _lastStatus = reason;
        return new(false, reason);
    }

    private TradingLoopOutcome SetStatus(bool succeeded, string status, string? asset = null, string? intentId = null)
    {
        _lastStatus = status;
        return new(succeeded, status, asset, intentId);
    }

    private void CancelActiveCycle()
    {
        lock (_activeCycleGate)
        {
            try
            {
                _activeCycle?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException)
            {
            }
        }
    }

    private static string SafeFailureKind(Exception exception) => exception switch
    {
        TimeoutException => "тайм-аут",
        UnauthorizedAccessException => "нет доступа",
        IOException => "ошибка ввода-вывода",
        OperationCanceledException => "отмена",
        _ => "неизвестная ошибка"
    };

}
