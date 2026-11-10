using TrueNorth.TradeFlow.Account;

namespace TrueNorth.TradeFlow.Risk;

internal static class EntryEligibilityExample
{
    internal const string SourceName = "Тестовый сценарий 007";

    internal static EntryEligibilityInput Create()
    {
        var evaluatedAt = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var accountAt = evaluatedAt.AddSeconds(-20);
        var priceAt = evaluatedAt.AddSeconds(-2);
        var setupAt = evaluatedAt.AddSeconds(-30);
        var rulesAt = evaluatedAt.AddDays(-1);
        var policy = new RiskFreshnessPolicy(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromDays(30), TimeSpan.FromMinutes(10));

        var btc = Position("BTC", "BTC-PERP", PositionDirection.Long, 1m, 0m, PositionOwnership.Manual);
        var eth = Position("ETH", "ETH-PERP", PositionDirection.Long, 0.1m, 0m, PositionOwnership.Unknown);
        var setup = new EntryEligibilitySetup(
            EntrySetupState.Complete, "SOL", "SOL-USDC", "Limit", 50m, PositionDirection.Short,
            45m, 55m, "USDC/SOL", SourceName, setupAt);
        var explicitFixtureSettings = new SettingsProfile
        {
            SelectedMode = "Test",
            Settings = new AppSettings
            {
                Assets = ["SOL"],
                EntryType = "Limit",
                MarginBalancePercent = 10m,
                Leverage = 1m
            }
        };

        var preferred = Pool(
            venue: "Scenario Venue A",
            accountName: "Scenario Account A",
            poolName: "USDC cross A",
            balance: 1500m,
            availableMargin: 1000m,
            positions: [eth],
            positionInputs: [new RiskPositionInput(eth, "eth-open", Evidence(2000m, "USDC/ETH", priceAt), null, null)],
            protections: [],
            instruments:
            [
                Instrument("Scenario Venue A", "ETH-PERP", "ETH", rulesAt, evaluatedAt.AddSeconds(-5), fundingRate: 0m, fundingPrice: 2000m),
                Instrument("Scenario Venue A", "SOL-PERP", "SOL", rulesAt, evaluatedAt.AddSeconds(-5), fundingRate: -0.0001m, fundingPrice: 50m)
            ],
            evaluatedAt, accountAt, priceAt);

        var fallback = Pool(
            venue: "Scenario Venue B",
            accountName: "Scenario Account B",
            poolName: "USDC cross B",
            balance: 1000m,
            availableMargin: 150m,
            positions: [btc],
            positionInputs: [new RiskPositionInput(btc, "btc-open", Evidence(100m, "USDC/BTC", priceAt),
                Evidence(90m, "USDC/BTC", accountAt), Evidence(1m, "BTC", accountAt))],
            protections: [new PositionProtection("BTC-PERP", new MoneyAmount(120m, "USDC"), new MoneyAmount(90m, "USDC"))],
            instruments:
            [
                Instrument("Scenario Venue B", "BTC-PERP", "BTC", rulesAt, evaluatedAt.AddSeconds(-5), fundingRate: 0.0001m, fundingPrice: 100m),
                Instrument("Scenario Venue B", "SOL-PERP", "SOL", rulesAt, evaluatedAt.AddSeconds(-5), fundingRate: -0.0001m, fundingPrice: 50m)
            ],
            evaluatedAt, accountAt, priceAt);

        return new EntryEligibilityInput(
            explicitFixtureSettings,
            setup,
            evaluatedAt,
            policy,
            [preferred, fallback],
            Evidence(true, "bool", accountAt));
    }

    internal static EntryEligibilityResult Evaluate() => EntryEligibilityEvaluator.Evaluate(Create());

    private static RiskPoolInput Pool(
        string venue,
        string accountName,
        string poolName,
        decimal balance,
        decimal availableMargin,
        IReadOnlyList<OpenPosition> positions,
        IReadOnlyList<RiskPositionInput> positionInputs,
        IReadOnlyList<PositionProtection> protections,
        IReadOnlyList<RiskInstrument> instruments,
        DateTimeOffset evaluatedAt,
        DateTimeOffset accountAt,
        DateTimeOffset priceAt)
    {
        var snapshot = new AccountSnapshot(
            venue, accountName, poolName, accountAt, SourceName, "USDC", "contract",
            AccountReadState.Available, null, accountAt,
            AccountSection<AccountBalance>.Available(
                new AccountBalance(new MoneyAmount(balance, "USDC"), new MoneyAmount(balance, "USDC"),
                    new MoneyAmount(availableMargin, "USDC")), accountAt, SourceName),
            AccountSection<IReadOnlyList<OpenPosition>>.Available(positions, accountAt, SourceName),
            AccountSection<IReadOnlyList<OpenOrder>>.ConfirmedEmpty(Array.Empty<OpenOrder>(), accountAt, SourceName),
            AccountSection<IReadOnlyList<PositionProtection>>.Available(protections, accountAt, SourceName),
            AccountSection<IReadOnlyList<ClosedTrade>>.NotRead(SourceName));

        return new RiskPoolInput(
            snapshot,
            instruments,
            positionInputs,
            [],
            new RiskSetupMarketBinding(venue, "SOL-PERP", "SOL", "SOL-USDC", SourceName, evaluatedAt.AddSeconds(-5)),
            null,
            Evidence(50m, "USDC/SOL", priceAt, "SOL-PERP"),
            Evidence(2m, "SOL", accountAt),
            Evidence(true, "bool", accountAt),
            Evidence(0m, "USDC", accountAt),
            Evidence(10m, "USDC", evaluatedAt));
    }

    private static OpenPosition Position(
        string asset,
        string contract,
        PositionDirection direction,
        decimal quantity,
        decimal pnl,
        PositionOwnership ownership) =>
        new(asset, contract, direction, quantity, "contract", new MoneyAmount(pnl, "USDC"), ownership,
            "Синтетический источник примера.");

    private static RiskInstrument Instrument(
        string venue,
        string contract,
        string asset,
        DateTimeOffset rulesAt,
        DateTimeOffset fundingAt,
        decimal fundingRate,
        decimal fundingPrice)
    {
        var identity = new RiskMarketIdentity(
            venue, contract, asset, "USDC", "contract", asset, SourceName, rulesAt);
        var priceUnit = $"USDC/{asset}";
        return new RiskInstrument(
            identity, SourceName, rulesAt, true, true,
            1m, $"{asset}/contract", 0.01m, priceUnit,
            0.01m, 0.01m, 10000m, "contract",
            1m, 1000000m, "USDC", 1m, 5m, 1m, 0.0005m, 0.0005m,
            "x", "fraction",
            new RiskFundingRules(fundingRate, "fraction/interval", 1m, "hours", fundingPrice,
                priceUnit, true, SourceName, fundingAt),
            [new RiskMaintenanceTier(null, "USDC", 0.025m, "fraction", 0m, "USDC")]);
    }

    internal static RiskEvidence<T> Evidence<T>(T value, string unit, DateTimeOffset observedAt, string? market = null) =>
        new(value, unit, SourceName, observedAt, market);
}
