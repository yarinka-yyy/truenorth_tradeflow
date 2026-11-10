namespace TrueNorth.TradeFlow.Account;

public static class AccountExample
{
    public const string SourceName = "Локальный пример";

    public static IReadOnlyList<AccountSnapshot> Create()
    {
        var observedAt = new DateTimeOffset(2026, 11, 9, 10, 30, 0, TimeSpan.FromHours(3));
        var previousAt = observedAt.AddHours(-26);

        var softwareOperation = new TradeOperationIdentity(
            "North Perp", "Основной пример", "BTC-PERP", ["north-order-201"], ["north-fill-201"]);
        var mixedOperation = new TradeOperationIdentity(
            "North Perp", "Основной пример", "SOL-PERP", ["north-order-404"], ["north-fill-404"]);

        OpenPosition[] northPositions =
        [
            new(
                "BTC", "BTC-PERP", PositionDirection.Long, 0.025m, "BTC",
                new MoneyAmount(18.40m, "USDC"),
                PositionOwnershipClassifier.Classify(
                    new ExposureIdentity("North Perp", "Основной пример", "BTC-PERP",
                        ["north-order-201"], ["north-fill-201"]),
                    [softwareOperation]),
                "Однозначная связь с операцией по площадке, счёту, контракту и ID."),
            new(
                "ETH", "ETH-PERP", PositionDirection.Short, 0.6m, "ETH",
                new MoneyAmount(-4.20m, "USDC"),
                PositionOwnershipClassifier.Classify(
                    new ExposureIdentity("North Perp", "Основной пример", "ETH-PERP",
                        ["manual-order-303"], ["manual-fill-304"]),
                    [softwareOperation], explicitlyManual: true),
                "Пример явно помечен как ручная позиция."),
            new(
                "SOL", "SOL-PERP", PositionDirection.Long, 12m, "SOL",
                new MoneyAmount(2.10m, "USDC"),
                PositionOwnershipClassifier.Classify(
                    new ExposureIdentity("North Perp", "Основной пример", "SOL-PERP",
                        ["north-order-404"], ["north-fill-404", "manual-fill-405"], HasMixedExecution: true),
                    [mixedOperation]),
                "Исполнения смешаны; владелец неизвестен.")
        ];

        OpenOrder[] northOrders =
        [
            new("BTC", "BTC-PERP", PositionDirection.Long, 0.040m, 0.025m, "BTC",
                "Лимитная заявка", PositionOwnership.SoftwareOwned,
                "Цены TP/SL получены; покрытие открытого объёма неизвестно.")
        ];

        ClosedTrade[] northHistory =
        [
            new("BTC", "BTC-PERP", PositionDirection.Long,
                new DateTimeOffset(2026, 11, 4, 11, 30, 0, TimeSpan.FromHours(3)),
                new DateTimeOffset(2026, 11, 8, 12, 5, 0, TimeSpan.FromHours(3)),
                new MoneyAmount(23.40m, "USDC"), "TP"),
            new("ETH", "ETH-PERP", PositionDirection.Short,
                new DateTimeOffset(2026, 10, 20, 14, 20, 0, TimeSpan.FromHours(3)),
                new DateTimeOffset(2026, 10, 31, 9, 15, 0, TimeSpan.FromHours(3)),
                new MoneyAmount(-8.10m, "USDC"), null)
        ];

        OpenPosition[] southPositions =
        [
            new("XRP", "XRP-PERP", PositionDirection.Short, 750m, "XRP", null,
                PositionOwnership.Unknown, "В прошлом снимке нет однозначного набора ID.")
        ];

        ClosedTrade[] southHistory =
        [
            new("SOL", "SOL-PERP", PositionDirection.Long,
                new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(3)),
                new DateTimeOffset(2026, 11, 1, 13, 40, 0, TimeSpan.FromHours(3)),
                new MoneyAmount(4.22m, "USD"), null)
        ];

        return
        [
            new AccountSnapshot(
                "North Perp", "Основной пример", "USDC cross", observedAt, SourceName, "USDC", "контракты",
                AccountReadState.Available, null, observedAt,
                AccountSection<AccountBalance>.Available(
                    new AccountBalance(
                        new MoneyAmount(1250m, "USDC"),
                        new MoneyAmount(1266.30m, "USDC"),
                        new MoneyAmount(0m, "USDC")),
                    observedAt, SourceName),
                AccountSection<IReadOnlyList<OpenPosition>>.Incomplete(
                    northPositions, observedAt, SourceName,
                    "Не удалось прочитать ещё одну страницу позиций; показаны полученные строки."),
                AccountSection<IReadOnlyList<OpenOrder>>.Available(northOrders, observedAt, SourceName),
                AccountSection<IReadOnlyList<PositionProtection>>.Incomplete(
                    [
                        new PositionProtection("BTC-PERP", new MoneyAmount(72250m, "USDC"), new MoneyAmount(68900m, "USDC")),
                        new PositionProtection("ETH-PERP", null, new MoneyAmount(3500m, "USDC"))
                    ], observedAt, SourceName,
                    "Для SOL-PERP значения TP/SL в примере недоступны."),
                AccountSection<IReadOnlyList<ClosedTrade>>.Incomplete(
                    northHistory, observedAt, SourceName,
                    "Следующая страница истории не получена.", previousAt)),
            new AccountSnapshot(
                "South Perp", "Резервный пример", "USD cross", observedAt, SourceName, "USD", "контракты",
                AccountReadState.AccessLost, "Доступ к площадке потерян в сценарии примера.", previousAt,
                AccountSection<AccountBalance>.PreviousSnapshot(
                    new AccountBalance(
                        new MoneyAmount(420m, "USD"),
                        new MoneyAmount(416.80m, "USD"),
                        new MoneyAmount(87.50m, "USD")),
                    previousAt, SourceName, previousAt, "Текущее чтение баланса не удалось."),
                AccountSection<IReadOnlyList<OpenPosition>>.PreviousSnapshot(
                    southPositions, previousAt, SourceName, previousAt, "Текущее чтение позиций не удалось."),
                AccountSection<IReadOnlyList<OpenOrder>>.PreviousSnapshot(
                    Array.Empty<OpenOrder>(), previousAt, SourceName, previousAt,
                    "В прошлом снимке заявок не было; текущее чтение недоступно."),
                AccountSection<IReadOnlyList<PositionProtection>>.AccessLost(
                    SourceName, "Доступ потерян; актуальное состояние TP/SL неизвестно.", previousAt),
                AccountSection<IReadOnlyList<ClosedTrade>>.PreviousSnapshot(
                    southHistory, previousAt, SourceName, previousAt, "История сохранена из прошлого снимка."))
        ];
    }
}
