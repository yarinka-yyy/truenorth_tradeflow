using System.Globalization;
using TrueNorth.TradeFlow.Account;

namespace TrueNorth.TradeFlow.Risk;

internal enum EntryEligibilityStatus
{
    ScenarioReady,
    Waiting,
    InsufficientData
}

internal enum EntrySetupState
{
    Complete,
    NoSetup,
    Incomplete,
    Inconclusive
}

internal enum RiskOrderRole
{
    Opening,
    ProtectiveOrClosing,
    Unknown
}

internal sealed record RiskFreshnessPolicy(
    TimeSpan AccountMaximumAge,
    TimeSpan MarketMaximumAge,
    TimeSpan RulesMaximumAge,
    TimeSpan SetupMaximumAge);

internal sealed record RiskEvidence<T>(T Value, string Unit, string Source, DateTimeOffset? ObservedAt, string? Market = null);

internal sealed record RiskEvidenceReference(string Subject, string? Source, DateTimeOffset? ObservedAt);

internal sealed record EntryEligibilitySetup(
    EntrySetupState State,
    string? Asset,
    string? Market,
    string? EntryType,
    decimal? LimitPrice,
    PositionDirection? Direction,
    decimal? TakeProfit,
    decimal? StopLoss,
    string? PriceUnit,
    string? Source,
    DateTimeOffset? ObservedAt);

internal sealed record RiskMarketIdentity(
    string? Venue,
    string? Contract,
    string? BaseAsset,
    string? QuoteCurrency,
    string? ContractQuantityUnit,
    string? BaseQuantityUnit,
    string? Source,
    DateTimeOffset? ObservedAt);

internal sealed record RiskFundingRules(
    decimal? RatePerInterval,
    string? RateUnit,
    decimal? IntervalHours,
    string? IntervalUnit,
    decimal? Price,
    string? PriceUnit,
    bool? PositiveRateMeansLongPays,
    string? Source,
    DateTimeOffset? ObservedAt);

internal sealed record RiskMaintenanceTier(
    decimal? UpperNotional,
    string? NotionalUnit,
    decimal? Rate,
    string? RateUnit,
    decimal? Deduction,
    string? DeductionUnit);

internal sealed record RiskInstrument(
    RiskMarketIdentity Identity,
    string? RulesSource,
    DateTimeOffset? RulesObservedAt,
    bool? IsLinear,
    bool? SupportsCross,
    decimal? ContractMultiplier,
    string? MultiplierUnit,
    decimal? PriceTick,
    string? PriceUnit,
    decimal? QuantityStep,
    decimal? MinimumQuantity,
    decimal? MaximumQuantity,
    string? QuantityUnit,
    decimal? MinimumNotional,
    decimal? MaximumNotional,
    string? NotionalUnit,
    decimal? MinimumLeverage,
    decimal? MaximumLeverage,
    decimal? LeverageStep,
    decimal? EntryFeeRate,
    decimal? ExitFeeRate,
    string? LeverageUnit,
    string? FeeRateUnit,
    RiskFundingRules? Funding,
    IReadOnlyList<RiskMaintenanceTier>? MaintenanceTiers);

internal sealed record RiskSetupMarketBinding(
    string? Venue,
    string? Contract,
    string? BaseAsset,
    string? SetupMarket,
    string? Source,
    DateTimeOffset? ObservedAt);

internal sealed record RiskPositionInput(
    OpenPosition Position,
    string PositionKey,
    RiskEvidence<decimal>? MarkPrice,
    RiskEvidence<decimal>? StopLoss,
    RiskEvidence<decimal>? StopCoveredBaseQuantity);

internal sealed record RiskOrderFillLink(string PositionKey, decimal QuantityInOrderUnits);

internal sealed record RiskOrderInput(
    OpenOrder Order,
    string OrderKey,
    RiskEvidence<RiskOrderRole>? Role,
    RiskEvidence<decimal>? ExpectedEntryPrice,
    RiskEvidence<decimal>? StopLoss,
    RiskEvidence<decimal>? StopCoveredBaseQuantity,
    RiskEvidence<decimal>? ReservedMarginAndFees,
    IReadOnlyList<RiskOrderFillLink> FilledPositionLinks);

internal sealed record RiskPoolInput(
    AccountSnapshot Account,
    IReadOnlyList<RiskInstrument> Instruments,
    IReadOnlyList<RiskPositionInput> Positions,
    IReadOnlyList<RiskOrderInput> Orders,
    RiskSetupMarketBinding? SetupBinding,
    RiskEvidence<decimal>? CurrentMarketPrice,
    RiskEvidence<decimal>? VerifiedLimitPrice,
    RiskEvidence<decimal>? CandidateStopCoveredBaseQuantity,
    RiskEvidence<bool>? AvailableMarginIncludesOrderReserves,
    RiskEvidence<decimal>? OtherUnreservedRequirements,
    RiskEvidence<decimal>? AdditionalMaintenanceHeadroom);

internal sealed record EntryEligibilityInput(
    SettingsProfile Settings,
    EntryEligibilitySetup Setup,
    DateTimeOffset EvaluatedAt,
    RiskFreshnessPolicy Freshness,
    IReadOnlyList<RiskPoolInput> Pools,
    RiskEvidence<bool>? AllConnectedExposureCovered);

internal sealed record EntryPoolResult(
    string Venue,
    string Account,
    string CollateralPool,
    string? Contract,
    string? QuoteCurrency,
    string? QuantityUnit,
    string? BaseQuantityUnit,
    string AccountSource,
    DateTimeOffset? AccountObservedAt,
    string? MarketBindingSource,
    DateTimeOffset? MarketBindingObservedAt,
    string? EntryPriceSource,
    DateTimeOffset? EntryPriceObservedAt,
    string? RulesSource,
    DateTimeOffset? RulesObservedAt,
    IReadOnlyList<RiskEvidenceReference> EvidenceTimes,
    EntryEligibilityStatus Status,
    IReadOnlyList<string> Reasons,
    decimal? FullBalance,
    decimal? SizingBalance,
    decimal? EntryPrice,
    decimal? Leverage,
    decimal? MarginBalancePercent,
    decimal? Quantity,
    decimal? BaseQuantity,
    decimal? Notional,
    decimal? AvailableMargin,
    decimal? InitialMargin,
    decimal? RequiredMargin,
    decimal? IncrementalCandidateEntryFee,
    decimal? TotalStressEntryFees,
    decimal? ExitFees,
    decimal? AdverseFunding,
    decimal? StressEquity,
    decimal? Maintenance,
    decimal? StressReserve,
    decimal? AdditionalHeadroom,
    decimal? ReserveAfterHeadroom,
    int StressExposureCount);

internal sealed record EntryEligibilityResult(
    EntryEligibilityStatus Status,
    string Summary,
    DateTimeOffset EvaluatedAt,
    string? SetupAsset,
    string? SetupMarket,
    string? SetupEntryType,
    PositionDirection? SetupDirection,
    string? SetupSource,
    DateTimeOffset? SetupObservedAt,
    EntryPoolResult? SelectedPool,
    IReadOnlyList<EntryPoolResult> Pools,
    IReadOnlyList<string> Reasons);

internal static class EntryEligibilityEvaluator
{
    private sealed class EvaluationState
    {
        private readonly List<string> _reasons = [];

        public bool HasMissingData { get; private set; }
        public bool HasKnownBlock { get; private set; }
        public int MissingCount { get; private set; }
        public int BlockCount { get; private set; }
        public IReadOnlyList<string> Reasons => _reasons;

        public void Missing(string reason)
        {
            HasMissingData = true;
            MissingCount++;
            Add(reason);
        }

        public void Block(string reason)
        {
            HasKnownBlock = true;
            BlockCount++;
            Add(reason);
        }

        public void Add(string reason)
        {
            if (!_reasons.Contains(reason, StringComparer.Ordinal))
                _reasons.Add(reason);
        }

        public EntryEligibilityStatus Status => HasMissingData
            ? EntryEligibilityStatus.InsufficientData
            : HasKnownBlock ? EntryEligibilityStatus.Waiting : EntryEligibilityStatus.ScenarioReady;
    }

    private sealed record StressExposure(
        PositionDirection Direction,
        decimal BaseQuantity,
        decimal ReferencePrice,
        decimal StopLoss,
        RiskInstrument Instrument,
        bool NeedsEntryFee);

    public static EntryEligibilityResult Evaluate(EntryEligibilityInput input)
    {
        var global = new EvaluationState();
        var settingsValidation = SettingsValidator.Validate(input.Settings);
        AppSettings? settings = null;
        if (!settingsValidation.IsValid)
            global.Missing("Сохранённые торговые параметры не прошли существующую проверку.");
        else if (settingsValidation.Profile.IsIncomplete())
            global.Missing("Не заполнены сохранённые активы, тип входа, доля баланса или плечо.");
        else
            settings = settingsValidation.Profile.Settings;

        ValidatePolicy(input, global);
        ValidateSetup(input, settings, global);
        ValidatePoolKeys(input, global);
        ValidateGlobalCoverage(input, global, input.Setup.Asset);

        var poolResults = input.Pools.Select(pool => EvaluatePoolSafely(input, pool, settings)).ToArray();

        if (global.HasMissingData || global.HasKnownBlock)
            return BuildResult(input, poolResults, null, global);

        if (poolResults.Length == 0)
        {
            global.Missing("Нет подключённых пулов обеспечения для проверки.");
            return BuildResult(input, poolResults, null, global);
        }

        if (poolResults.Any(pool => pool.FullBalance is null))
        {
            global.Missing("Полный баланс одного из подключённых пулов неизвестен, поэтому порядок площадок не подтверждён.");
            return BuildResult(input, poolResults, null, global);
        }

        var currencies = input.Pools
            .Select(pool => pool.Account.QuoteCurrency)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (currencies.Length != 1)
        {
            global.Missing("Балансы в разных валютах несопоставимы; конвертация между счетами не выполняется.");
            return BuildResult(input, poolResults, null, global);
        }

        if (input.Pools.GroupBy(pool => pool.Account.Venue, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            global.Missing("На одной площадке найдено несколько счетов или пулов; порядок выбора не подтверждён.");
            return BuildResult(input, poolResults, null, global);
        }

        var ordered = poolResults
            .OrderByDescending(pool => pool.FullBalance)
            .ThenBy(pool => pool.Venue, StringComparer.Ordinal)
            .ToArray();
        foreach (var balanceGroup in ordered.GroupBy(pool => pool.FullBalance))
        {
            var tied = balanceGroup.ToArray();
            var ready = tied.Where(pool => pool.Status == EntryEligibilityStatus.ScenarioReady).ToArray();
            if (ready.Length > 1)
            {
                global.Missing($"У подходящих площадок {string.Join(", ", ready.Select(pool => pool.Venue))} равный полный баланс; подтверждённого порядка нет.");
                return BuildResult(input, poolResults, null, global);
            }

            if (ready.Length == 1)
                return BuildResult(input, poolResults, ready[0], global);
        }

        foreach (var pool in ordered.Where(pool => pool.Status == EntryEligibilityStatus.InsufficientData))
            global.Missing($"{pool.Venue}: {string.Join(" ", pool.Reasons)}");
        foreach (var pool in ordered.Where(pool => pool.Status == EntryEligibilityStatus.Waiting))
            global.Block($"{pool.Venue}: {string.Join(" ", pool.Reasons)}");
        if (!global.HasMissingData && !global.HasKnownBlock)
            global.Block("Ни одна площадка не прошла условия тестового сценария.");

        return BuildResult(input, poolResults, null, global);
    }

    private static EntryEligibilityResult BuildResult(
        EntryEligibilityInput input,
        IReadOnlyList<EntryPoolResult> pools,
        EntryPoolResult? selected,
        EvaluationState state)
    {
        if (selected is not null && state.Status == EntryEligibilityStatus.ScenarioReady)
        {
            var summary = $"Выбран {selected.Venue} · {selected.Contract}.";
            return new EntryEligibilityResult(EntryEligibilityStatus.ScenarioReady, summary,
                input.EvaluatedAt, input.Setup.Asset, input.Setup.Market, input.Setup.EntryType,
                input.Setup.Direction, input.Setup.Source, input.Setup.ObservedAt, selected, pools, []);
        }

        var status = state.Status;
        var reasons = state.Reasons.ToList();
        if (reasons.Count == 0)
            reasons.Add(status == EntryEligibilityStatus.Waiting
                ? "Есть известное условие, которое пока не выполнено."
                : "Недостаточно подтверждённых данных для оценки.");
        var label = status == EntryEligibilityStatus.Waiting ? "Ожидание" : "Недостаточно данных";
        return new EntryEligibilityResult(status, $"{label}: {string.Join(" ", reasons)}",
            input.EvaluatedAt, input.Setup.Asset, input.Setup.Market, input.Setup.EntryType,
            input.Setup.Direction, input.Setup.Source, input.Setup.ObservedAt, null, pools, reasons);
    }

    private static void ValidatePolicy(EntryEligibilityInput input, EvaluationState state)
    {
        if (input.EvaluatedAt.Offset != TimeSpan.Zero)
            state.Missing("Время оценки должно быть задано в UTC.");
        if (input.Freshness.AccountMaximumAge <= TimeSpan.Zero
            || input.Freshness.MarketMaximumAge <= TimeSpan.Zero
            || input.Freshness.RulesMaximumAge <= TimeSpan.Zero
            || input.Freshness.SetupMaximumAge <= TimeSpan.Zero)
            state.Missing("Границы свежести входов должны быть положительными и заданы явно.");
    }

    private static void ValidateSetup(EntryEligibilityInput input, AppSettings? settings, EvaluationState state)
    {
        var setup = input.Setup;
        switch (setup.State)
        {
            case EntrySetupState.NoSetup:
                state.Block("Подтверждённого сетапа нет.");
                return;
            case EntrySetupState.Incomplete:
                state.Missing("Сетап заполнен не полностью.");
                return;
            case EntrySetupState.Inconclusive:
                state.Missing("Исход сетапа не подтверждён.");
                return;
            case EntrySetupState.Complete:
                break;
            default:
                state.Missing("Неизвестное состояние сетапа.");
                return;
        }

        if (string.IsNullOrWhiteSpace(setup.Asset) || string.IsNullOrWhiteSpace(setup.Market)
            || string.IsNullOrWhiteSpace(setup.Source) || string.IsNullOrWhiteSpace(setup.PriceUnit)
            || !Fresh(setup.ObservedAt, setup.Source, input.EvaluatedAt, input.Freshness.SetupMaximumAge))
            state.Missing("У сетапа нет точной идентичности, источника или свежего времени наблюдения.");
        if (setup.EntryType is not ("Market" or "Limit"))
            state.Missing("Тип входа сетапа неизвестен.");
        if (setup.EntryType == "Limit" && setup.LimitPrice is not > 0)
            state.Missing("Для Limit сетап должен содержать точную исходную цену входа.");
        if (setup.Direction is not (PositionDirection.Long or PositionDirection.Short))
            state.Missing("Направление сетапа неизвестно.");
        if (setup.TakeProfit is null or <= 0 || setup.StopLoss is null or <= 0)
            state.Missing("Для расчёта обязательны положительные take-profit и stop-loss.");
        if (settings is not null)
        {
            if (!settings.Assets.Contains(setup.Asset, StringComparer.OrdinalIgnoreCase))
                state.Block("Актив сетапа отсутствует в проверенном списке сохранённых активов.");
            if (!string.Equals(settings.EntryType, setup.EntryType, StringComparison.Ordinal))
                state.Block("Тип входа сетапа не совпадает с сохранённым типом входа.");
        }
    }

    private static void ValidatePoolKeys(EntryEligibilityInput input, EvaluationState state)
    {
        if (input.Pools.Count == 0)
            state.Missing("Нет данных по подключённым счетам и пулам обеспечения.");

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in input.Pools)
        {
            var account = pool.Account;
            if (string.IsNullOrWhiteSpace(account.Venue) || string.IsNullOrWhiteSpace(account.Account)
                || string.IsNullOrWhiteSpace(account.CollateralPool))
            {
                state.Missing("Площадка, счёт и пул обеспечения должны иметь точные идентификаторы.");
                continue;
            }

            var key = $"{account.Venue}\u001f{account.Account}\u001f{account.CollateralPool}";
            if (!scopes.Add(key))
                state.Missing($"Снимок {account.Venue}/{account.Account}/{account.CollateralPool} повторяется.");
        }
    }

    private static void ValidateGlobalCoverage(EntryEligibilityInput input, EvaluationState state, string? targetAsset)
    {
        if (input.AllConnectedExposureCovered is not { } coverage
            || !EvidenceFresh(coverage, "bool", input.EvaluatedAt, input.Freshness.AccountMaximumAge))
        {
            state.Missing("Полнота покрытия подключённых площадок не подтверждена.");
            return;
        }
        if (!coverage.Value)
            state.Missing("Покрытие подключённых площадок неполное; глобальную уникальность актива подтвердить нельзя.");

        foreach (var pool in input.Pools)
        {
            ValidateAccountEnvelope(pool.Account, input, state);
            var positions = ReadList(pool.Account.Positions, input, "позиций", state, input.Freshness.AccountMaximumAge);
            var orders = ReadList(pool.Account.Orders, input, "открывающих заявок", state, input.Freshness.AccountMaximumAge);
            if (positions is not null)
            {
                foreach (var position in positions)
                {
                    var instrument = FindInstrument(pool, position.Contract, input, requireRules: false, state);
                    if (instrument is null)
                        continue;
                    if (!string.Equals(position.Asset, instrument.Identity.BaseAsset, StringComparison.Ordinal))
                        state.Missing($"Идентичность базового актива для позиции {position.Contract} не подтверждена.");
                    else if (string.Equals(position.Asset, targetAsset, StringComparison.OrdinalIgnoreCase))
                        state.Block($"Актив {targetAsset} уже открыт на {pool.Account.Venue}; новый вход заблокирован.");
                }
            }

            if (orders is not null)
            {
                if (pool.Orders.Count != orders.Count)
                    state.Missing($"Не подтверждена классификация всех заявок на {pool.Account.Venue}.");
                for (var index = 0; index < Math.Min(orders.Count, pool.Orders.Count); index++)
                {
                    var riskOrder = pool.Orders[index];
                    var order = orders[index];
                    if (!Equals(riskOrder.Order, order)
                        || riskOrder.Role is not { } role
                        || !EvidenceFresh(role, "role", input.EvaluatedAt, input.Freshness.AccountMaximumAge)
                        || role.Value == RiskOrderRole.Unknown
                        || !Enum.IsDefined(role.Value))
                    {
                        state.Missing($"Покрытие или назначение заявки на {pool.Account.Venue} неизвестно.");
                        continue;
                    }
                    if (role.Value != RiskOrderRole.Opening)
                        continue;
                    if (order.RemainingQuantity is not > 0)
                    {
                        state.Missing($"Остаток открывающей заявки {riskOrder.OrderKey} не подтверждён.");
                        continue;
                    }

                    var instrument = FindInstrument(pool, order.Contract, input, requireRules: false, state);
                    if (instrument is null)
                        continue;
                    if (!string.Equals(order.Asset, instrument.Identity.BaseAsset, StringComparison.Ordinal))
                        state.Missing($"Идентичность базового актива для заявки {order.Contract} не подтверждена.");
                    else if (string.Equals(order.Asset, targetAsset, StringComparison.OrdinalIgnoreCase))
                        state.Block($"Открывающий остаток по активу {targetAsset} найден на {pool.Account.Venue}; второй вход запрещён.");
                }
            }
        }
    }

    private static EntryPoolResult EvaluatePoolSafely(EntryEligibilityInput input, RiskPoolInput pool, AppSettings? settings)
    {
        try
        {
            return EvaluatePool(input, pool, settings);
        }
        catch (OverflowException)
        {
            var state = new EvaluationState();
            state.Missing($"Числовые входы на {pool.Account.Venue} выходят за пределы decimal; расчёт по пулу остановлен.");
            var balance = ReadBalance(pool.Account, input, state, out _, out var availableMargin);
            ValidateAccountEnvelope(pool.Account, input, state);
            var instrument = pool.SetupBinding is { Contract: { } contract }
                ? pool.Instruments.FirstOrDefault(item => string.Equals(item.Identity.Contract, contract, StringComparison.Ordinal))
                : null;
            return CreatePoolResult(input, pool, pool.Account, instrument, state,
                fullBalance: balance,
                sizingBalance: null,
                entryPrice: null,
                leverage: null,
                quantity: null,
                notional: null,
                availableMargin: availableMargin?.Value,
                initialMargin: null,
                requiredMargin: null,
                incrementalCandidateEntryFee: null,
                totalStressEntryFees: null,
                exitFees: null,
                adverseFunding: null,
                stressEquity: null,
                maintenance: null,
                stressReserve: null,
                additionalHeadroom: pool.AdditionalMaintenanceHeadroom?.Value,
                reserveAfterHeadroom: null,
                stressCount: 0);
        }
    }

    private static EntryPoolResult EvaluatePool(EntryEligibilityInput input, RiskPoolInput pool, AppSettings? settings)
    {
        var state = new EvaluationState();
        var account = pool.Account;
        var fullBalance = ReadBalance(account, input, state, out var equity, out var availableMargin);
        ValidateAccountEnvelope(account, input, state);
        if (input.Setup.State == EntrySetupState.NoSetup)
        {
            state.Block("Подтверждённого сетапа нет.");
            return CreatePoolResult(input, pool, account, null, state,
                fullBalance: fullBalance,
                sizingBalance: null,
                entryPrice: null,
                leverage: null,
                quantity: null,
                notional: null,
                availableMargin: availableMargin?.Value,
                initialMargin: null,
                requiredMargin: null,
                incrementalCandidateEntryFee: null,
                totalStressEntryFees: null,
                exitFees: null,
                adverseFunding: null,
                stressEquity: null,
                maintenance: null,
                stressReserve: null,
                additionalHeadroom: null,
                reserveAfterHeadroom: null,
                stressCount: 0);
        }
        var headroomEvidence = pool.AdditionalMaintenanceHeadroom;
        var headroomValid = headroomEvidence is { } headroomValue
            && !string.IsNullOrWhiteSpace(account.QuoteCurrency)
            && ValidEvidence(headroomValue, account.QuoteCurrency, input, input.Freshness.AccountMaximumAge)
            && headroomValue.Value >= 0;
        if (!headroomValid)
            state.Missing("Дополнительный запас maintenance не задан свежим значением с подтверждённой валютой и источником.");
        decimal? additionalHeadroom = headroomValid ? headroomEvidence!.Value : null;
        var positions = ReadList(account.Positions, input, "позиций", state, input.Freshness.AccountMaximumAge);
        var orders = ReadList(account.Orders, input, "заявок", state, input.Freshness.AccountMaximumAge);
        var protections = ReadList(account.Protections, input, "защитных уровней", state, input.Freshness.AccountMaximumAge);

        var instrumentByContract = new Dictionary<string, RiskInstrument>(StringComparer.Ordinal);
        foreach (var instrument in pool.Instruments)
        {
            if (string.IsNullOrWhiteSpace(instrument.Identity.Contract)
                || !instrumentByContract.TryAdd(instrument.Identity.Contract, instrument))
                state.Missing($"Идентичность контракта на {account.Venue} пустая или повторяется.");
        }

        var positionsByKey = new Dictionary<string, RiskPositionInput>(StringComparer.Ordinal);
        if (positions is not null && pool.Positions.Count != positions.Count)
            state.Missing($"Данные риска не покрывают все позиции на {account.Venue}.");
        if (positions is not null)
        {
            for (var index = 0; index < Math.Min(positions.Count, pool.Positions.Count); index++)
            {
                var riskPosition = pool.Positions[index];
                var position = positions[index];
                if (!Equals(riskPosition.Position, position) || string.IsNullOrWhiteSpace(riskPosition.PositionKey)
                    || !positionsByKey.TryAdd(riskPosition.PositionKey, riskPosition))
                {
                    state.Missing($"Строка позиции или её ключ на {account.Venue} не сопоставлены точно.");
                    continue;
                }
            }
        }

        var orderKeys = new HashSet<string>(StringComparer.Ordinal);
        if (orders is not null && pool.Orders.Count != orders.Count)
            state.Missing($"Данные риска не покрывают все заявки на {account.Venue}.");

        var aggregateUnrealized = 0m;
        var pnlComplete = positions is not null && positions.Count == pool.Positions.Count;
        var exposures = new List<StressExposure>();
        var expectedExposureCount = positions is null || orders is null
            || positions.Count != pool.Positions.Count || orders.Count != pool.Orders.Count
                ? -1
                : positions.Count;
        var linkedFilledByPosition = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (positions is not null)
        {
            foreach (var position in positions)
            {
                if (position.UnrealizedPnl is not { } pnl
                    || !string.Equals(pnl.Currency, account.QuoteCurrency, StringComparison.Ordinal))
                {
                    state.Missing($"Совокупный нереализованный PnL на {account.Venue} неизвестен или в другой валюте.");
                    pnlComplete = false;
                }
                else
                {
                    aggregateUnrealized += pnl.Value;
                }
            }
        }

        if (fullBalance is { } balanceValue && equity is { } equityValue && pnlComplete
            && equityValue.Value != balanceValue + aggregateUnrealized)
            state.Missing($"Equity на {account.Venue} не совпадает с балансом и совокупным нереализованным PnL.");

        if (positions is not null)
        {
            for (var index = 0; index < Math.Min(positions.Count, pool.Positions.Count); index++)
            {
                var riskPosition = pool.Positions[index];
                var position = positions[index];
                if (!Equals(riskPosition.Position, position))
                    continue;
                if (string.IsNullOrWhiteSpace(riskPosition.PositionKey)
                    || !positionsByKey.ContainsKey(riskPosition.PositionKey))
                    continue;
                if (!TryInstrument(pool, position.Contract, input, state, out var instrument, requireRules: true))
                    continue;
                if (!ValidateQuantity(position.Asset, position.Quantity, position.QuantityUnit, account, instrument,
                        position.Direction, state, $"позиции {position.Contract}", out var baseQuantity))
                    continue;

                if (riskPosition.MarkPrice is not { } mark
                    || !ValidEvidence(mark, PriceUnit(instrument), input, input.Freshness.MarketMaximumAge))
                {
                    state.Missing($"Для позиции {position.Contract} нет свежей цены mark с проверенными единицами.");
                    continue;
                }
                if (mark.Value <= 0)
                {
                    state.Missing($"Цена mark для позиции {position.Contract} должна быть положительной.");
                    continue;
                }
                if (!TryStopCoverage(riskPosition.StopLoss, riskPosition.StopCoveredBaseQuantity,
                        instrument, baseQuantity, input, input.Freshness.AccountMaximumAge, state,
                        $"позиции {position.Contract}", out var stop))
                    continue;
                if (protections is null || protections.Count(protection =>
                        string.Equals(protection.Contract, position.Contract, StringComparison.Ordinal)
                        && protection.StopLoss is { } stopMoney
                        && stopMoney.Value == stop
                        && string.Equals(stopMoney.Currency, account.QuoteCurrency, StringComparison.Ordinal)) != 1)
                {
                    state.Missing($"Читаемая защита для позиции {position.Contract} не совпадает с покрытым стопом.");
                    continue;
                }
                exposures.Add(new StressExposure(position.Direction, baseQuantity, mark.Value, stop, instrument,
                    NeedsEntryFee: false));
            }
        }

        decimal? alreadyReservedRequirements = 0m;
        var includesReserves = pool.AvailableMarginIncludesOrderReserves;
        bool? orderReservesIncluded = null;
        if (includesReserves is { } includes
            && ValidEvidence(includes, "bool", input, input.Freshness.AccountMaximumAge))
            orderReservesIncluded = includes.Value;
        else
        {
            state.Missing($"Правило резервирования открытых заявок на {account.Venue} неизвестно.");
            alreadyReservedRequirements = null;
        }

        if (orders is not null)
        {
            if (orders.Count != pool.Orders.Count)
                alreadyReservedRequirements = null;
            for (var index = 0; index < Math.Min(orders.Count, pool.Orders.Count); index++)
            {
                var riskOrder = pool.Orders[index];
                var order = orders[index];
                if (!Equals(riskOrder.Order, order) || string.IsNullOrWhiteSpace(riskOrder.OrderKey)
                    || !orderKeys.Add(riskOrder.OrderKey))
                {
                    state.Missing($"Заявка или её ключ на {account.Venue} не сопоставлены точно.");
                    alreadyReservedRequirements = null;
                    continue;
                }
                if (riskOrder.Role is not { } orderRole
                    || !EvidenceFresh(orderRole, "role", input.EvaluatedAt, input.Freshness.AccountMaximumAge)
                    || orderRole.Value == RiskOrderRole.Unknown
                    || !Enum.IsDefined(orderRole.Value))
                {
                    state.Missing($"Назначение заявки на {account.Venue} неизвестно.");
                    expectedExposureCount = -1;
                    continue;
                }
                if (orderRole.Value == RiskOrderRole.ProtectiveOrClosing)
                    continue;
                if (expectedExposureCount >= 0)
                    expectedExposureCount++;

                if (order.TotalQuantity <= 0 || order.FilledQuantity < 0
                    || order.RemainingQuantity is not { } remaining || remaining <= 0
                    || order.QuantityUnit != account.QuantityUnit)
                {
                    state.Missing($"Остаток или единица открывающей заявки {riskOrder.OrderKey} не подтверждены.");
                    continue;
                }
                if (!TryInstrument(pool, order.Contract, input, state, out var instrument, requireRules: true))
                    continue;
                if (!ValidateQuantity(order.Asset, remaining, order.QuantityUnit, account, instrument,
                        order.Direction, state, $"остатка заявки {order.Contract}", out var baseQuantity))
                    continue;
                if (!ValidateFilledLinks(riskOrder, order, positionsByKey,
                        linkedFilledByPosition, state))
                    continue;
                if (riskOrder.ExpectedEntryPrice is not { } entry
                    || !ValidEvidence(entry, PriceUnit(instrument), input, input.Freshness.MarketMaximumAge)
                    || entry.Value <= 0 || !IsMultiple(entry.Value, instrument.PriceTick))
                {
                    state.Missing($"Для остатка заявки {riskOrder.OrderKey} нет свежей цены входа с нужным шагом.");
                    continue;
                }
                if (!TryStopCoverage(riskOrder.StopLoss, riskOrder.StopCoveredBaseQuantity,
                        instrument, baseQuantity, input, input.Freshness.AccountMaximumAge, state,
                        $"остатка заявки {riskOrder.OrderKey}", out var stop))
                    continue;
                exposures.Add(new StressExposure(order.Direction, baseQuantity, entry.Value, stop, instrument,
                    NeedsEntryFee: true));

                if (orderReservesIncluded == false)
                {
                    if (riskOrder.ReservedMarginAndFees is not { } reserve
                        || !ValidEvidence(reserve, account.QuoteCurrency, input, input.Freshness.AccountMaximumAge)
                        || reserve.Value < 0)
                    {
                        state.Missing($"Незарезервированное требование заявки {riskOrder.OrderKey} неизвестно.");
                        alreadyReservedRequirements = null;
                    }
                    else if (alreadyReservedRequirements is { } current)
                    {
                        alreadyReservedRequirements = current + reserve.Value;
                    }
                }
            }
        }

        foreach (var filled in linkedFilledByPosition)
        {
            if (!positionsByKey.TryGetValue(filled.Key, out var position)
                || position.Position.Quantity is not { } positionQuantity || filled.Value > positionQuantity)
                state.Missing($"Частичное исполнение превышает привязанную позицию {filled.Key}.");
        }

        decimal? sizingBalance = null;
        if (fullBalance is { } balance && pnlComplete)
            sizingBalance = balance + Math.Min(aggregateUnrealized, 0m);
        if (sizingBalance is <= 0)
            state.Block("Расчётный баланс после нереализованного PnL не положительный.");

        var binding = pool.SetupBinding;
        var candidateInstrument = binding is null
            ? null
            : pool.Instruments.FirstOrDefault(candidate =>
                string.Equals(candidate.Identity.Contract, binding.Contract, StringComparison.Ordinal));
        var candidateKnownBlock = false;
        var bindingValid = binding is not null && candidateInstrument is not null
            && input.Setup.State == EntrySetupState.Complete
            && Fresh(input.Setup.ObservedAt, input.Setup.Source, input.EvaluatedAt, input.Freshness.SetupMaximumAge);
        if (input.Setup.State == EntrySetupState.NoSetup)
            state.Block("Подтверждённого сетапа нет.");
        else if (input.Setup.State != EntrySetupState.Complete)
            state.Missing("Сетап заполнен не полностью или его исход не подтверждён.");
        if (binding is null || candidateInstrument is null)
            state.Missing($"На {account.Venue} нет проверенного соответствия сетапа точному рынку.");
        else
        {
            if (!Fresh(binding.ObservedAt, binding.Source, input.EvaluatedAt, input.Freshness.SetupMaximumAge))
            {
                state.Missing($"Подтверждение рынка для сетапа на {account.Venue} устарело или не имеет источника.");
                bindingValid = false;
            }
            if (!string.Equals(binding.Venue, account.Venue, StringComparison.Ordinal)
                || !string.Equals(binding.Contract, candidateInstrument.Identity.Contract, StringComparison.Ordinal)
                || !string.Equals(binding.BaseAsset, candidateInstrument.Identity.BaseAsset, StringComparison.Ordinal)
                || !string.Equals(binding.SetupMarket, input.Setup.Market, StringComparison.Ordinal)
                || !string.Equals(binding.BaseAsset, input.Setup.Asset, StringComparison.Ordinal))
            {
                state.Block($"Рынок или актив сетапа не подтверждён для точного контракта {candidateInstrument.Identity.Contract} на {account.Venue}.");
                bindingValid = false;
            }
            var ruleMissingBefore = state.MissingCount;
            var ruleBlocksBefore = state.BlockCount;
            if (!TryInstrument(pool, candidateInstrument.Identity.Contract, input, state, out var validatedInstrument,
                    requireRules: true))
            {
                candidateKnownBlock = state.BlockCount > ruleBlocksBefore
                    && state.MissingCount == ruleMissingBefore;
                bindingValid = false;
            }
            else
                candidateInstrument = validatedInstrument;
            if (!bindingValid)
                candidateInstrument = null;
        }

        var candidateSetupLevelsValid = false;
        var entryPrice = GetEntryPrice(input, pool, candidateInstrument, state, out var priceIntentBlocked);
        candidateKnownBlock |= priceIntentBlocked;
        if (candidateInstrument is not null && entryPrice is { } checkedEntry
            && input.Setup.TakeProfit is { } takeProfit && input.Setup.StopLoss is { } setupStop
            && input.Setup.Direction is { } setupDirection)
        {
            var setupPriceUnitValid = string.Equals(input.Setup.PriceUnit, PriceUnit(candidateInstrument), StringComparison.Ordinal);
            if (!setupPriceUnitValid)
                state.Missing("Единица цен сетапа не совпадает с точной валютой и активом рынка.");
            if (!IsMultiple(takeProfit, candidateInstrument.PriceTick)
                || !IsMultiple(setupStop, candidateInstrument.PriceTick))
                state.Block("Уровни take-profit или stop-loss не соответствуют шагу цены точного рынка.");
            var levelsOrdered = setupDirection switch
            {
                PositionDirection.Long => setupStop < checkedEntry && checkedEntry < takeProfit,
                PositionDirection.Short => takeProfit < checkedEntry && checkedEntry < setupStop,
                _ => false
            };
            if (!levelsOrdered)
                state.Block("Порядок цены входа, take-profit и stop-loss неверен для направления сетапа.");
            candidateSetupLevelsValid = bindingValid
                && setupPriceUnitValid
                && IsMultiple(takeProfit, candidateInstrument.PriceTick)
                && IsMultiple(setupStop, candidateInstrument.PriceTick)
                && levelsOrdered;
        }
        decimal? leverage = null;
        decimal? quantity = null;
        decimal? notional = null;
        decimal? initialMargin = null;
        decimal? incrementalCandidateEntryFee = null;
        decimal? totalStressEntryFees = null;
        decimal? requiredMargin = null;
        var availableValue = availableMargin?.Value;

        if (candidateInstrument is not null && entryPrice is { } price && settings is not null
            && sizingBalance is > 0 && settings.MarginBalancePercent is { } percent
            && settings.Leverage is { } configuredLeverage)
        {
            var rules = candidateInstrument;
            if (rules.MinimumLeverage is not > 0 || rules.MaximumLeverage is not > 0
                || rules.LeverageStep is not > 0 || rules.ContractMultiplier is not > 0
                || rules.QuantityStep is not > 0 || rules.PriceTick is not > 0
                || rules.EntryFeeRate is not { } entryFeeRate
                || rules.MinimumQuantity is not { } minimumQuantity
                || rules.MaximumQuantity is not { } maximumQuantity
                || rules.MinimumNotional is not { } minimumNotional
                || rules.MaximumNotional is not { } maximumNotional)
            {
                state.Missing($"Размер, шаг или максимальное плечо рынка {rules.Identity.Contract} неизвестны.");
            }
            else
            {
                leverage = Math.Min(configuredLeverage, rules.MaximumLeverage.Value);
                if (leverage.Value < rules.MinimumLeverage.Value
                    || (leverage.Value - rules.MinimumLeverage.Value) % rules.LeverageStep.Value != 0)
                    state.Block($"Плечо {leverage}× не входит в подтверждённый шаг и диапазон {rules.Identity.Contract}.");
                var targetMargin = sizingBalance.Value * percent / 100m;
                var rawQuantity = targetMargin * leverage.Value / price / rules.ContractMultiplier.Value;
                quantity = decimal.Floor(rawQuantity / rules.QuantityStep.Value) * rules.QuantityStep.Value;
                notional = quantity.Value * price * rules.ContractMultiplier.Value;
                incrementalCandidateEntryFee = notional.Value * entryFeeRate;
                initialMargin = notional.Value / leverage.Value;

                if (notional > targetMargin * leverage.Value)
                    state.Missing("Округлённый номинал превышает выделенный размер.");
                if (notional > 0 && initialMargin > 0 && notional / targetMargin > leverage.Value)
                    state.Missing("Фактическое плечо после округления превысило применённое плечо.");
                if (!IsMultiple(price, rules.PriceTick))
                    state.Block("Цена входа не соответствует шагу цены точного рынка.");
                if (quantity < minimumQuantity || notional < minimumNotional)
                    state.Block("После округления размер ниже минимума рынка; доля баланса не увеличена.");
                if (quantity > maximumQuantity || notional > maximumNotional)
                    state.Block("После округления размер превышает максимум рынка.");
                if (quantity <= 0)
                    state.Block("После округления по шагу рынка размер равен нулю.");

                if (settings.MarginBalancePercent is <= 0 or > 100 || configuredLeverage < 1)
                    state.Missing("Сохранённая доля баланса или плечо не прошли проверку.");

                var otherRequirements = pool.OtherUnreservedRequirements;
                if (otherRequirements is not { } other
                    || !ValidEvidence(other, account.QuoteCurrency, input, input.Freshness.AccountMaximumAge)
                    || other.Value < 0)
                {
                    state.Missing($"Дополнительные незарезервированные требования на {account.Venue} неизвестны.");
                }
                else if (alreadyReservedRequirements is { } existingReservations)
                {
                    requiredMargin = initialMargin.Value + incrementalCandidateEntryFee.Value + other.Value + existingReservations;
                    if (availableValue is { } available && available < requiredMargin.Value)
                        state.Block("Доступной маржи недостаточно для дополнительной начальной маржи, комиссий и незарезервированных требований.");
                }

                if (availableValue is null)
                    state.Missing($"Доступная маржа на {account.Venue} неизвестна.");
            }
        }
        else
        {
            if (entryPrice is null && !candidateKnownBlock)
                state.Missing($"Цена входа для {account.Venue} не подтверждена.");
            if (settings is null)
                state.Missing("Параметры входа не прошли проверку существующим путём.");
        }

        var exitFees = 0m;
        var adverseFunding = 0m;
        var additionalPnl = 0m;
        var maintenance = 0m;
        var stressCount = exposures.Count;
        var stressInputsComplete = expectedExposureCount >= 0 && exposures.Count == expectedExposureCount;
        if (candidateInstrument is not null && entryPrice is { } candidatePrice && quantity is > 0
            && input.Setup.Direction is { } candidateDirection && input.Setup.StopLoss is { } candidateStop
            && candidateSetupLevelsValid
            && pool.SetupBinding is not null
            && pool.SetupBinding.Contract == candidateInstrument.Identity.Contract)
        {
            var candidateBaseQuantity = quantity.Value * candidateInstrument.ContractMultiplier!.Value;
            var candidateStopEvidence = input.Setup.StopLoss is { } setupStopValue
                ? new RiskEvidence<decimal>(setupStopValue, input.Setup.PriceUnit ?? string.Empty,
                    input.Setup.Source ?? string.Empty, input.Setup.ObservedAt)
                : null;
            if (!TryStopCoverage(candidateStopEvidence, pool.CandidateStopCoveredBaseQuantity, candidateInstrument,
                    candidateBaseQuantity, input, input.Freshness.SetupMaximumAge, state,
                    "нового входа", out var stop))
                stressInputsComplete = false;
            else
            {
                exposures.Add(new StressExposure(candidateDirection, candidateBaseQuantity, candidatePrice,
                    stop, candidateInstrument, NeedsEntryFee: true));
                stressCount++;
            }
        }
        else
            stressInputsComplete = false;

        if (stressInputsComplete && pnlComplete && equity is { } currentEquity
            && headroomValid && headroomEvidence is { } headroom && exposures.Count > 0)
        {
            var stressEntryFees = 0m;
            foreach (var exposure in exposures)
            {
                if (!TryCalculateStress(exposure, input, state, out var pnl, out var entryFee,
                        out var closeFee, out var funding, out var requirement))
                {
                    stressInputsComplete = false;
                    break;
                }
                additionalPnl += pnl;
                if (exposure.NeedsEntryFee)
                    stressEntryFees += entryFee;
                exitFees += closeFee;
                adverseFunding += funding;
                maintenance += requirement;
            }

            if (!stressInputsComplete)
                return CreatePoolResult(input, pool, account, candidateInstrument, state, fullBalance, sizingBalance, entryPrice,
                    leverage, quantity, notional, availableValue, initialMargin, requiredMargin,
                    incrementalCandidateEntryFee, totalStressEntryFees,
                    exitFees, adverseFunding, null, null, null, headroom.Value, null, stressCount);

            totalStressEntryFees = stressEntryFees;
            var stressEquity = currentEquity.Value + additionalPnl - stressEntryFees - exitFees - adverseFunding;
            var stressReserve = stressEquity - maintenance;
            var reserveAfterHeadroom = stressReserve - headroom.Value;
            if (reserveAfterHeadroom < 0)
                state.Block("Резерв после дополнительного запаса тестового сценария ниже maintenance.");

            return CreatePoolResult(input, pool, account, candidateInstrument, state, fullBalance, sizingBalance, entryPrice,
                leverage, quantity, notional, availableValue, initialMargin, requiredMargin,
                incrementalCandidateEntryFee, totalStressEntryFees,
                exitFees, adverseFunding, stressEquity, maintenance, stressReserve, headroom.Value,
                reserveAfterHeadroom, stressCount);
        }

        if (exposures.Count == 0 && !candidateKnownBlock)
            state.Missing("Нет полного набора позиций, открывающих остатков и нового входа для стресс-расчёта.");
        return CreatePoolResult(input, pool, account, candidateInstrument, state, fullBalance, sizingBalance, entryPrice,
            leverage, quantity, notional, availableValue, initialMargin, requiredMargin,
            incrementalCandidateEntryFee, totalStressEntryFees,
            exitFees, adverseFunding, null, null, null, additionalHeadroom, null, stressCount);
    }

    private static EntryPoolResult CreatePoolResult(
        EntryEligibilityInput input,
        RiskPoolInput pool,
        AccountSnapshot account,
        RiskInstrument? instrument,
        EvaluationState state,
        decimal? fullBalance,
        decimal? sizingBalance,
        decimal? entryPrice,
        decimal? leverage,
        decimal? quantity,
        decimal? notional,
        decimal? availableMargin,
        decimal? initialMargin,
        decimal? requiredMargin,
        decimal? incrementalCandidateEntryFee,
        decimal? totalStressEntryFees,
        decimal? exitFees,
        decimal? adverseFunding,
        decimal? stressEquity,
        decimal? maintenance,
        decimal? stressReserve,
        decimal? additionalHeadroom,
        decimal? reserveAfterHeadroom,
        int stressCount) =>
        new(account.Venue, account.Account, account.CollateralPool, instrument?.Identity.Contract ?? pool.SetupBinding?.Contract,
            instrument?.Identity.QuoteCurrency ?? account.QuoteCurrency,
            instrument?.Identity.ContractQuantityUnit,
            instrument?.Identity.BaseQuantityUnit,
            account.Source, account.ObservedAt, pool.SetupBinding?.Source, pool.SetupBinding?.ObservedAt,
            (input.Setup.EntryType == "Market" ? pool.CurrentMarketPrice : pool.VerifiedLimitPrice)?.Source,
            (input.Setup.EntryType == "Market" ? pool.CurrentMarketPrice : pool.VerifiedLimitPrice)?.ObservedAt,
            instrument?.RulesSource, instrument?.RulesObservedAt, CollectEvidenceTimes(input, pool),
            state.Status, state.Reasons, fullBalance, sizingBalance, entryPrice,
            leverage, input.Settings.Settings?.MarginBalancePercent, quantity,
            quantity is { } contracts && instrument?.ContractMultiplier is { } multiplier
                ? contracts * multiplier
                : null,
            notional, availableMargin, initialMargin, requiredMargin,
            incrementalCandidateEntryFee, totalStressEntryFees, exitFees, adverseFunding,
            stressEquity, maintenance, stressReserve,
            additionalHeadroom, reserveAfterHeadroom, stressCount);

    private static IReadOnlyList<RiskEvidenceReference> CollectEvidenceTimes(
        EntryEligibilityInput input,
        RiskPoolInput pool)
    {
        var evidence = new List<RiskEvidenceReference>();
        var account = pool.Account;
        Add("снимок счёта", account.Source, account.ObservedAt);
        Add("последнее полное чтение счёта", account.Source, account.LastSuccessfulReadAt);
        AddSection("баланс", account.Balances);
        AddSection("позиции", account.Positions);
        AddSection("заявки", account.Orders);
        AddSection("защита", account.Protections);
        Add("сетап", input.Setup.Source, input.Setup.ObservedAt);
        if (input.Setup.EntryType == "Limit" && input.Setup.LimitPrice is not null)
            Add("исходная цена Limit", input.Setup.Source, input.Setup.ObservedAt);
        AddEvidence("покрытие подключённых экспозиций", input.AllConnectedExposureCovered);
        Add("подтверждение рынка сетапа", pool.SetupBinding?.Source, pool.SetupBinding?.ObservedAt);
        AddEvidence("цена Market", pool.CurrentMarketPrice);
        AddEvidence("цена Limit", pool.VerifiedLimitPrice);
        AddEvidence("покрытие стопа нового входа", pool.CandidateStopCoveredBaseQuantity);
        AddEvidence("состав доступной маржи", pool.AvailableMarginIncludesOrderReserves);
        AddEvidence("прочие незарезервированные требования", pool.OtherUnreservedRequirements);
        AddEvidence("запас maintenance сценария", pool.AdditionalMaintenanceHeadroom);

        foreach (var instrument in pool.Instruments)
        {
            var contract = instrument.Identity.Contract ?? "unknown";
            Add($"идентичность рынка {contract}", instrument.Identity.Source, instrument.Identity.ObservedAt);
            Add($"правила рынка {contract}", instrument.RulesSource, instrument.RulesObservedAt);
            if (instrument.Funding is { } funding)
                Add($"funding {contract}", funding.Source, funding.ObservedAt);
        }
        foreach (var position in pool.Positions)
        {
            AddEvidence($"mark позиции {position.Position.Contract}", position.MarkPrice);
            AddEvidence($"stop позиции {position.Position.Contract}", position.StopLoss);
            AddEvidence($"покрытие стопа позиции {position.Position.Contract}", position.StopCoveredBaseQuantity);
        }
        foreach (var order in pool.Orders)
        {
            AddEvidence($"назначение заявки {order.OrderKey}", order.Role);
            AddEvidence($"цена остатка {order.OrderKey}", order.ExpectedEntryPrice);
            AddEvidence($"stop остатка {order.OrderKey}", order.StopLoss);
            AddEvidence($"покрытие стопа остатка {order.OrderKey}", order.StopCoveredBaseQuantity);
            AddEvidence($"резерв заявки {order.OrderKey}", order.ReservedMarginAndFees);
        }
        return evidence;

        void Add(string subject, string? source, DateTimeOffset? observedAt)
        {
            if (!string.IsNullOrWhiteSpace(source) || observedAt is not null)
                evidence.Add(new RiskEvidenceReference(subject, source, observedAt));
        }

        void AddEvidence<T>(string subject, RiskEvidence<T>? item)
        {
            if (item is { } value)
                Add(subject, value.Source, value.ObservedAt);
        }

        void AddSection<T>(string subject, AccountSection<T> section)
        {
            Add($"наблюдение: {subject}", section.Source, section.ObservedAt);
            Add($"последнее полное чтение: {subject}", section.Source, section.LastSuccessfulAt);
        }
    }

    private static decimal? ReadBalance(
        AccountSnapshot account,
        EntryEligibilityInput input,
        EvaluationState state,
        out MoneyAmount? equity,
        out MoneyAmount? availableMargin)
    {
        equity = null;
        availableMargin = null;
        var section = account.Balances;
        if (section.State != AccountReadState.Available || section.Value is null
            || section.ObservedAt is null || string.IsNullOrWhiteSpace(section.Source)
            || !Fresh(section.ObservedAt, section.Source, input.EvaluatedAt, input.Freshness.AccountMaximumAge)
            || !Fresh(section.LastSuccessfulAt, section.Source, input.EvaluatedAt, input.Freshness.AccountMaximumAge))
        {
            state.Missing($"Баланс на {account.Venue} не прочитан полностью и свежо.");
            return null;
        }

        var balance = section.Value.Balance;
        equity = section.Value.Equity;
        availableMargin = section.Value.AvailableMargin;
        if (balance is null || equity is null || availableMargin is null
            || string.IsNullOrWhiteSpace(account.QuoteCurrency)
            || !string.Equals(balance.Currency, account.QuoteCurrency, StringComparison.Ordinal)
            || !string.Equals(equity.Currency, account.QuoteCurrency, StringComparison.Ordinal)
            || !string.Equals(availableMargin.Currency, account.QuoteCurrency, StringComparison.Ordinal)
            || balance.Value < 0)
        {
            state.Missing($"Баланс, equity или доступная маржа на {account.Venue} имеют неизвестную единицу или значение.");
            return null;
        }
        return balance.Value;
    }

    private static void ValidateAccountEnvelope(
        AccountSnapshot account,
        EntryEligibilityInput input,
        EvaluationState state)
    {
        if (account.Availability != AccountReadState.Available
            || string.IsNullOrWhiteSpace(account.Source)
            || string.IsNullOrWhiteSpace(account.QuoteCurrency)
            || string.IsNullOrWhiteSpace(account.QuantityUnit)
            || !Fresh(account.ObservedAt, account.Source, input.EvaluatedAt, input.Freshness.AccountMaximumAge)
            || !Fresh(account.LastSuccessfulReadAt, account.Source, input.EvaluatedAt, input.Freshness.AccountMaximumAge))
            state.Missing($"Снимок {account.Venue}/{account.Account}/{account.CollateralPool} неполный, прошлый или устарел.");

    }

    private static IReadOnlyList<T>? ReadList<T>(
        AccountSection<IReadOnlyList<T>> section,
        EntryEligibilityInput input,
        string name,
        EvaluationState state,
        TimeSpan maximumAge)
    {
        if (section.State is not (AccountReadState.Available or AccountReadState.ConfirmedEmpty)
            || section.Value is null
            || section.ObservedAt is null
            || section.LastSuccessfulAt is null
            || string.IsNullOrWhiteSpace(section.Source)
            || !Fresh(section.ObservedAt, section.Source, input.EvaluatedAt, maximumAge)
            || !Fresh(section.LastSuccessfulAt, section.Source, input.EvaluatedAt, maximumAge))
        {
            state.Missing($"Раздел {name} не прочитан полностью и свежо.");
            return null;
        }

        if (section.State == AccountReadState.ConfirmedEmpty && section.Value.Count != 0)
        {
            state.Missing($"Раздел {name} помечен пустым, но содержит строки.");
            return null;
        }
        return section.Value;
    }

    private static bool Fresh(DateTimeOffset? observedAt, string? source, DateTimeOffset now, TimeSpan maximumAge) =>
        observedAt is { } time
        && !string.IsNullOrWhiteSpace(source)
        && time <= now
        && now - time <= maximumAge;

    private static bool ValidEvidence<T>(
        RiskEvidence<T> evidence,
        string expectedUnit,
        EntryEligibilityInput input,
        TimeSpan maximumAge) =>
        string.Equals(evidence.Unit, expectedUnit, StringComparison.Ordinal)
        && Fresh(evidence.ObservedAt, evidence.Source, input.EvaluatedAt, maximumAge);

    private static bool EvidenceFresh<T>(
        RiskEvidence<T> evidence,
        string expectedUnit,
        DateTimeOffset now,
        TimeSpan maximumAge) =>
        string.Equals(evidence.Unit, expectedUnit, StringComparison.Ordinal)
        && Fresh(evidence.ObservedAt, evidence.Source, now, maximumAge);

    private static RiskInstrument? FindInstrument(
        RiskPoolInput pool,
        string? contract,
        EntryEligibilityInput input,
        bool requireRules,
        EvaluationState state)
    {
        var matches = pool.Instruments.Where(candidate =>
            string.Equals(candidate.Identity.Contract, contract, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            state.Missing($"Точный контракт {contract ?? "unknown"} не сопоставлен ровно одному набору правил.");
            return null;
        }

        var instrument = matches[0];
        var missingBefore = state.MissingCount;
        ValidateIdentity(instrument, pool.Account, input, state);
        if (state.MissingCount != missingBefore)
            return null;
        if (requireRules)
        {
            missingBefore = state.MissingCount;
            var blocksBefore = state.BlockCount;
            ValidateRules(instrument, pool.Account, input, state);
            if (state.MissingCount != missingBefore || state.BlockCount != blocksBefore)
                return null;
        }
        return instrument;
    }

    private static bool TryInstrument(
        RiskPoolInput pool,
        string? contract,
        EntryEligibilityInput input,
        EvaluationState state,
        out RiskInstrument instrument,
        bool requireRules)
    {
        var found = FindInstrument(pool, contract, input, requireRules, state);
        if (found is null)
        {
            instrument = null!;
            return false;
        }
        instrument = found;
        return true;
    }

    private static void ValidateIdentity(
        RiskInstrument instrument,
        AccountSnapshot account,
        EntryEligibilityInput input,
        EvaluationState state)
    {
        var identity = instrument.Identity;
        if (string.IsNullOrWhiteSpace(identity.Venue) || string.IsNullOrWhiteSpace(identity.Contract)
            || string.IsNullOrWhiteSpace(identity.BaseAsset) || string.IsNullOrWhiteSpace(identity.QuoteCurrency)
            || string.IsNullOrWhiteSpace(identity.ContractQuantityUnit)
            || !string.Equals(identity.BaseQuantityUnit, identity.BaseAsset, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.Source)
            || !Fresh(identity.ObservedAt, identity.Source, input.EvaluatedAt, input.Freshness.RulesMaximumAge))
        {
            state.Missing("Точная идентичность рынка, единицы, источник или время сопоставления неизвестны.");
            return;
        }
        if (!string.Equals(identity.Venue, account.Venue, StringComparison.Ordinal)
            || !string.Equals(identity.QuoteCurrency, account.QuoteCurrency, StringComparison.Ordinal))
            state.Missing($"Рынок {identity.Contract} не принадлежит точной площадке или валюте пула.");
    }

    private static void ValidateRules(
        RiskInstrument instrument,
        AccountSnapshot account,
        EntryEligibilityInput input,
        EvaluationState state)
    {
        if (string.IsNullOrWhiteSpace(instrument.RulesSource)
            || !Fresh(instrument.RulesObservedAt, instrument.RulesSource,
                input.EvaluatedAt, input.Freshness.RulesMaximumAge))
        {
            state.Missing($"Правила рынка {instrument.Identity.Contract} устарели или не имеют источника.");
            return;
        }

        var identity = instrument.Identity;
        var priceUnit = PriceUnit(instrument);
        if (instrument.IsLinear is null || instrument.SupportsCross is null
            || instrument.ContractMultiplier is not > 0
            || !string.Equals(instrument.MultiplierUnit,
                $"{identity.BaseQuantityUnit}/{identity.ContractQuantityUnit}", StringComparison.Ordinal)
            || instrument.PriceTick is not > 0
            || !string.Equals(instrument.PriceUnit, priceUnit, StringComparison.Ordinal)
            || instrument.QuantityStep is not > 0
            || instrument.MinimumQuantity is not > 0
            || instrument.MaximumQuantity is not > 0
            || instrument.MinimumQuantity > instrument.MaximumQuantity
            || !string.Equals(instrument.QuantityUnit, identity.ContractQuantityUnit, StringComparison.Ordinal)
            || instrument.MinimumNotional is not > 0
            || instrument.MaximumNotional is not > 0
            || instrument.MinimumNotional > instrument.MaximumNotional
            || !string.Equals(instrument.NotionalUnit, identity.QuoteCurrency, StringComparison.Ordinal)
            || instrument.MinimumLeverage is not >= 1
            || instrument.MaximumLeverage is not >= 1
            || instrument.MinimumLeverage > instrument.MaximumLeverage
            || instrument.LeverageStep is not > 0
            || instrument.EntryFeeRate is not >= 0 or > 1
            || instrument.ExitFeeRate is not >= 0 or > 1
            || !string.Equals(instrument.LeverageUnit, "x", StringComparison.Ordinal)
            || !string.Equals(instrument.FeeRateUnit, "fraction", StringComparison.Ordinal)
            || !ValidMaintenanceTiers(instrument.MaintenanceTiers, identity.QuoteCurrency!))
        {
            state.Missing($"Правила размера, комиссии или maintenance для {identity.Contract} неполны либо имеют неверные единицы.");
            return;
        }
        if (instrument.IsLinear != true)
            state.Block($"Контракт {identity.Contract} не подтверждён как линейный.");
        if (instrument.SupportsCross != true)
            state.Block($"Для {identity.Contract} cross-маржа недоступна; изолированная маржа не подставляется.");

        if (instrument.Funding is not { } funding
            || string.IsNullOrWhiteSpace(funding.Source)
            || !Fresh(funding.ObservedAt, funding.Source, input.EvaluatedAt, input.Freshness.MarketMaximumAge)
            || funding.RatePerInterval is null
            || !string.Equals(funding.RateUnit, "fraction/interval", StringComparison.Ordinal)
            || funding.IntervalHours is not > 0
            || !string.Equals(funding.IntervalUnit, "hours", StringComparison.Ordinal)
            || funding.Price is not > 0
            || !string.Equals(funding.PriceUnit, priceUnit, StringComparison.Ordinal)
            || funding.PositiveRateMeansLongPays is null)
            state.Missing($"Funding для {identity.Contract} не подтверждён по знаку, цене, ставке, интервалу или времени.");

        if (!string.Equals(account.QuantityUnit, identity.ContractQuantityUnit, StringComparison.Ordinal))
            state.Missing($"Единица снимка счёта не совпадает с единицей контрактов {identity.Contract}.");
    }

    private static bool ValidMaintenanceTiers(IReadOnlyList<RiskMaintenanceTier>? tiers, string quoteCurrency)
    {
        if (tiers is null || tiers.Count == 0)
            return false;
        decimal? prior = null;
        for (var index = 0; index < tiers.Count; index++)
        {
            var tier = tiers[index];
            if (tier.Rate is not >= 0 or > 1 || tier.Deduction is not >= 0
                || !string.Equals(tier.RateUnit, "fraction", StringComparison.Ordinal)
                || !string.Equals(tier.NotionalUnit, quoteCurrency, StringComparison.Ordinal)
                || !string.Equals(tier.DeductionUnit, quoteCurrency, StringComparison.Ordinal))
                return false;
            if (tier.UpperNotional is { } upper)
            {
                if (upper <= 0 || (prior is { } previous && upper <= previous) || index == tiers.Count - 1)
                    return false;
                prior = upper;
            }
            else if (index != tiers.Count - 1)
                return false;
        }
        return tiers[^1].UpperNotional is null;
    }

    private static bool ValidateQuantity(
        string? asset,
        decimal? quantity,
        string? quantityUnit,
        AccountSnapshot account,
        RiskInstrument instrument,
        PositionDirection direction,
        EvaluationState state,
        string subject,
        out decimal baseQuantity)
    {
        baseQuantity = 0;
        if (direction is not (PositionDirection.Long or PositionDirection.Short)
            || quantity is not > 0
            || !string.Equals(asset, instrument.Identity.BaseAsset, StringComparison.Ordinal)
            || !string.Equals(quantityUnit, instrument.Identity.ContractQuantityUnit, StringComparison.Ordinal)
            || !string.Equals(quantityUnit, account.QuantityUnit, StringComparison.Ordinal)
            || instrument.ContractMultiplier is not > 0)
        {
            state.Missing($"Размер, направление или идентичность {subject} не подтверждены в исходных единицах.");
            return false;
        }
        baseQuantity = quantity.Value * instrument.ContractMultiplier.Value;
        return baseQuantity > 0;
    }

    private static bool ValidateFilledLinks(
        RiskOrderInput riskOrder,
        OpenOrder order,
        IReadOnlyDictionary<string, RiskPositionInput> positionsByKey,
        IDictionary<string, decimal> linkedFilledByPosition,
        EvaluationState state)
    {
        var links = riskOrder.FilledPositionLinks;
        if (links.Any(link => string.IsNullOrWhiteSpace(link.PositionKey) || link.QuantityInOrderUnits <= 0)
            || links.Sum(link => link.QuantityInOrderUnits) != order.FilledQuantity)
        {
            state.Missing($"Исполненная и оставшаяся части заявки {riskOrder.OrderKey} не разделены точно.");
            return false;
        }

        foreach (var link in links)
        {
            if (!positionsByKey.TryGetValue(link.PositionKey, out var position)
                || !string.Equals(position.Position.Contract, order.Contract, StringComparison.Ordinal)
                || !string.Equals(position.Position.Asset, order.Asset, StringComparison.Ordinal)
                || position.Position.Direction != order.Direction)
            {
                state.Missing($"Исполненная часть заявки {riskOrder.OrderKey} не связана с точной позицией.");
                return false;
            }
            var alreadyLinked = linkedFilledByPosition.TryGetValue(link.PositionKey, out var linkedQuantity)
                ? linkedQuantity
                : 0m;
            linkedFilledByPosition[link.PositionKey] = alreadyLinked + link.QuantityInOrderUnits;
        }
        return true;
    }

    private static bool TryStopCoverage(
        RiskEvidence<decimal>? stopEvidence,
        RiskEvidence<decimal>? coverageEvidence,
        RiskInstrument instrument,
        decimal requiredBaseQuantity,
        EntryEligibilityInput input,
        TimeSpan stopMaximumAge,
        EvaluationState state,
        string subject,
        out decimal stop)
    {
        stop = 0;
        if (stopEvidence is not { } stopValue
            || !ValidEvidence(stopValue, PriceUnit(instrument), input, stopMaximumAge)
            || stopValue.Value <= 0 || !IsMultiple(stopValue.Value, instrument.PriceTick))
        {
            state.Missing($"Стоп {subject} не читается, устарел или не соответствует шагу цены.");
            return false;
        }
        if (coverageEvidence is not { } coverage
            || !ValidEvidence(coverage, instrument.Identity.BaseQuantityUnit ?? string.Empty,
                input, input.Freshness.AccountMaximumAge)
            || coverage.Value < requiredBaseQuantity)
        {
            state.Missing($"Стоп {subject} не подтверждён на весь объём.");
            return false;
        }
        stop = stopValue.Value;
        return true;
    }

    private static decimal? GetEntryPrice(
        EntryEligibilityInput input,
        RiskPoolInput pool,
        RiskInstrument? instrument,
        EvaluationState state,
        out bool knownIntentBlock)
    {
        knownIntentBlock = false;
        if (instrument is null)
            return null;

        var expectedUnit = PriceUnit(instrument);
        var selected = input.Setup.EntryType switch
        {
            "Market" => pool.CurrentMarketPrice,
            "Limit" => pool.VerifiedLimitPrice,
            _ => null
        };
        var expectedSource = input.Setup.EntryType == "Market"
            ? "свежего наблюдения цены Market"
            : "проверенной цены Limit";
        if (selected is not { } evidence
            || !ValidEvidence(evidence, expectedUnit, input, input.Freshness.MarketMaximumAge)
            || evidence.Value <= 0
            || !IsMultiple(evidence.Value, instrument.PriceTick))
        {
            state.Missing($"Для {instrument.Identity.Contract} нет {expectedSource} с нужной единицей, шагом и временем.");
            return null;
        }
        if (!string.Equals(evidence.Market, instrument.Identity.Contract, StringComparison.Ordinal))
        {
            state.Missing($"Цена входа не сопоставлена точному контракту {instrument.Identity.Contract}.");
            return null;
        }
        if (input.Setup.EntryType == "Limit")
        {
            if (input.Setup.LimitPrice is not { } intendedPrice || intendedPrice <= 0)
            {
                state.Missing("Исходная цена Limit в сетапе не подтверждена.");
                return null;
            }
            if (evidence.Value != intendedPrice)
            {
                state.Block("Проверенная цена Limit не совпадает с ценой входа в сетапе.");
                knownIntentBlock = true;
                return null;
            }
        }
        return evidence.Value;
    }

    private static bool TryCalculateStress(
        StressExposure exposure,
        EntryEligibilityInput input,
        EvaluationState state,
        out decimal additionalPnl,
        out decimal entryFee,
        out decimal exitFee,
        out decimal funding,
        out decimal maintenance)
    {
        additionalPnl = entryFee = exitFee = funding = maintenance = 0;
        var instrument = exposure.Instrument;
        var schedule = instrument.Funding;
        if (schedule is null || instrument.ContractMultiplier is not > 0
            || instrument.EntryFeeRate is null || instrument.ExitFeeRate is null
            || instrument.MaintenanceTiers is null
            || schedule.RatePerInterval is null || schedule.IntervalHours is not > 0
            || !string.Equals(schedule.RateUnit, "fraction/interval", StringComparison.Ordinal)
            || !string.Equals(schedule.IntervalUnit, "hours", StringComparison.Ordinal)
            || schedule.Price is not > 0 || schedule.PositiveRateMeansLongPays is null
            || !string.Equals(schedule.PriceUnit, PriceUnit(instrument), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(schedule.Source)
            || !Fresh(schedule.ObservedAt, schedule.Source, input.EvaluatedAt, input.Freshness.MarketMaximumAge))
        {
            state.Missing($"Для стресс-расчёта {instrument.Identity.Contract} не хватает комиссий, funding или maintenance.");
            return false;
        }

        var direction = DirectionSign(exposure.Direction);
        var stressedPrice = exposure.StopLoss * (1m - 0.01m * direction);
        var stressedNotional = exposure.BaseQuantity * stressedPrice;
        additionalPnl = direction * exposure.BaseQuantity * (stressedPrice - exposure.ReferencePrice);
        if (exposure.NeedsEntryFee)
            entryFee = exposure.BaseQuantity * exposure.ReferencePrice * instrument.EntryFeeRate.Value;
        exitFee = stressedNotional * instrument.ExitFeeRate.Value;
        var payerSign = schedule.PositiveRateMeansLongPays.Value ? 1m : -1m;
        var adverseRate = Math.Max(direction * schedule.RatePerInterval.Value * payerSign, 0m);
        funding = exposure.BaseQuantity * schedule.Price.Value * adverseRate * (24m / schedule.IntervalHours.Value);
        if (!TryMaintenance(stressedNotional, instrument.MaintenanceTiers, out maintenance))
        {
            state.Missing($"Стрессовый номинал {instrument.Identity.Contract} не попал в подтверждённый maintenance tier.");
            return false;
        }
        return true;
    }

    private static bool TryMaintenance(
        decimal notional,
        IReadOnlyList<RiskMaintenanceTier> tiers,
        out decimal maintenance)
    {
        foreach (var tier in tiers)
        {
            if (tier.UpperNotional is null || notional <= tier.UpperNotional.Value)
            {
                if (tier.Rate is null || tier.Deduction is null)
                    break;
                maintenance = Math.Max(notional * tier.Rate.Value - tier.Deduction.Value, 0m);
                return true;
            }
        }
        maintenance = 0;
        return false;
    }

    private static string PriceUnit(RiskInstrument instrument) =>
        $"{instrument.Identity.QuoteCurrency}/{instrument.Identity.BaseAsset}";

    private static decimal DirectionSign(PositionDirection direction) => direction switch
    {
        PositionDirection.Long => 1m,
        PositionDirection.Short => -1m,
        _ => 0m
    };

    private static bool IsMultiple(decimal value, decimal? step) =>
        step is > 0 && value % step.Value == 0;
}
