using System.Globalization;

namespace TrueNorth.TradeFlow.Account;

internal sealed record AccountVenueCard(
    string Venue,
    string AvailabilityLabel,
    string AvailabilityDetails,
    string AccountDetails,
    string BalanceStatus,
    string Balance,
    string Equity,
    string AvailableMargin,
    string PositionStatus,
    string PositionEmptyText,
    IReadOnlyList<AccountPositionRow> Positions,
    string OrderStatus,
    string OrderEmptyText,
    IReadOnlyList<AccountOrderRow> Orders,
    string ProtectionStatus,
    string ProtectionEmptyText,
    IReadOnlyList<AccountProtectionRow> Protections,
    string HistoryStatus);

internal sealed record AccountPositionRow(string Position, string UnrealizedPnl, string Ownership);
internal sealed record AccountOrderRow(string Order, string Ownership, string Protection);
internal sealed record AccountProtectionRow(string Contract, string TakeProfit, string StopLoss);
internal sealed record ClosedTradeRow(
    string Venue,
    string Asset,
    string Direction,
    string OpenedAt,
    string ClosedAt,
    DateTimeOffset ClosedAtValue,
    string RealizedPnl,
    string CloseReason,
    string SnapshotLabel);

internal static class AccountPresentation
{
    private static readonly CultureInfo CurrentCulture = CultureInfo.CurrentCulture;

    public static AccountVenueCard CreateCard(AccountSnapshot snapshot)
    {
        var balances = snapshot.Balances.Value;
        var positions = snapshot.Positions.Value ?? [];
        var orders = snapshot.Orders.Value ?? [];
        var protections = snapshot.Protections.Value ?? [];
        var history = snapshot.History.Value ?? [];

        return new AccountVenueCard(
            snapshot.Venue,
            StateLabel(snapshot.Availability),
            snapshot.Availability == AccountReadState.AccessLost
                ? Join(snapshot.AvailabilityFailure,
                    snapshot.LastSuccessfulReadAt is { } last ? $"Последнее успешное чтение: {FormatTime(last)}" : null)
                : snapshot.AvailabilityFailure ?? string.Empty,
            $"Счёт: {snapshot.Account} · пул обеспечения: {snapshot.CollateralPool}\n"
                + $"Источник: {snapshot.Source} · наблюдение: {FormatTime(snapshot.ObservedAt)}",
            SectionDetails(snapshot.Balances),
            Money(balances?.Balance),
            Money(balances?.Equity),
            Money(balances?.AvailableMargin),
            SectionDetails(snapshot.Positions),
            EmptyText(snapshot.Positions.State, positions.Count),
            positions.Select(position => new AccountPositionRow(
                $"{position.Asset} · {Direction(position.Direction)} · {Quantity(position.Quantity, position.QuantityUnit)}",
                $"Нереализованный PnL: {Money(position.UnrealizedPnl)}",
                $"Владелец: {Ownership(position.Ownership)} · {position.OwnershipNote}"))
                .ToArray(),
            SectionDetails(snapshot.Orders),
            EmptyText(snapshot.Orders.State, orders.Count),
            orders.Select(order => new AccountOrderRow(
                $"{order.Asset} · {Direction(order.Direction)} · {order.Status}; исполнено "
                    + $"{Quantity(order.FilledQuantity, order.QuantityUnit)} из {Quantity(order.TotalQuantity, order.QuantityUnit)}; "
                    + $"остаток {Quantity(order.RemainingQuantity, order.QuantityUnit)}",
                $"Владелец: {Ownership(order.Ownership)}",
                $"Защита: {order.ProtectionStatus}"))
                .ToArray(),
            SectionDetails(snapshot.Protections),
            EmptyText(snapshot.Protections.State, protections.Count),
            protections.Select(protection => new AccountProtectionRow(
                protection.Contract,
                Money(protection.TakeProfit),
                Money(protection.StopLoss)))
                .ToArray(),
            HistoryStatus(snapshot.History, history.Count));
    }

    public static IReadOnlyList<ClosedTradeRow> CreateHistoryRows(IReadOnlyList<AccountSnapshot> snapshots) =>
        snapshots.SelectMany(snapshot =>
        {
            var snapshotLabel = snapshot.History.State switch
            {
                AccountReadState.Incomplete => "История неполная",
                AccountReadState.PreviousSnapshot => "Данные прошлого снимка",
                AccountReadState.AccessLost => "Текущее чтение истории недоступно",
                AccountReadState.NotRead => "История не прочитана",
                _ => string.Empty
            };
            return (snapshot.History.Value ?? []).Select(trade => new ClosedTradeRow(
                snapshot.Venue,
                trade.Asset,
                Direction(trade.Direction),
                $"Открыта: {FormatTime(trade.OpenedAt)}",
                $"Закрыта: {FormatTime(trade.ClosedAt)}",
                trade.ClosedAt,
                $"Реализованный PnL: {Money(trade.RealizedPnl)}",
                trade.CloseReason ?? "Причина закрытия недоступна",
                snapshotLabel));
        }).ToArray();

    public static string CreateHistoryCoverage(IReadOnlyList<AccountSnapshot> snapshots) =>
        string.Join(" · ", snapshots.Select(snapshot =>
        {
            var section = snapshot.History;
            var count = section.Value?.Count ?? 0;
            var state = section.State switch
            {
                AccountReadState.Available when section.Value is null => "результат не получен",
                AccountReadState.ConfirmedEmpty when section.Value is null => "пустота не подтверждена",
                AccountReadState.Available => "прочитана",
                AccountReadState.ConfirmedEmpty => "подтверждённо пусто",
                AccountReadState.Incomplete => "неполная",
                AccountReadState.AccessLost => "доступ утрачен",
                AccountReadState.PreviousSnapshot => "прошлый снимок",
                _ => "не читалась"
            };
            return $"{snapshot.Venue} — {state}; строк {count}";
        }));

    public static string CreateHistoryNoRowsMessage(IReadOnlyList<AccountSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return "Для выбранных площадок нет снимков истории.";

        var fullyRead = snapshots.All(snapshot => snapshot.History.Value is not null
            && snapshot.History.State is AccountReadState.Available or AccountReadState.ConfirmedEmpty);
        if (!fullyRead)
            return "По выбранным фильтрам строки не получены; полнота или актуальность истории не подтверждена.";

        if (snapshots.All(snapshot => snapshot.History.State == AccountReadState.ConfirmedEmpty))
            return "Подтверждённо пусто: в выбранных снимках нет закрытых сделок.";

        return "По выбранным фильтрам сделок не найдено; чтение истории завершено.";
    }

    public static string StateLabel(AccountReadState state) => state switch
    {
        AccountReadState.Available => "Доступна",
        AccountReadState.ConfirmedEmpty => "Подтверждённо пусто",
        AccountReadState.Incomplete => "Данные неполные",
        AccountReadState.AccessLost => "Доступ утрачен",
        AccountReadState.PreviousSnapshot => "Показываем прошлый снимок",
        _ => "Чтение не выполнялось"
    };

    private static string SectionDetails<T>(AccountSection<T> section)
    {
        var state = section.State switch
        {
            AccountReadState.Available => "Прочитано",
            AccountReadState.ConfirmedEmpty => "Подтверждённый пустой список",
            AccountReadState.Incomplete => "Данные неполные",
            AccountReadState.AccessLost => "Доступ утрачен",
            AccountReadState.PreviousSnapshot => "Данные прошлого снимка",
            _ => "Чтение не выполнялось"
        };
        var timeDetails = section.State switch
        {
            AccountReadState.Available or AccountReadState.ConfirmedEmpty =>
                section.ObservedAt is { } observed ? $"наблюдение {FormatTime(observed)}" : null,
            AccountReadState.Incomplete => Join(
                section.ObservedAt is { } observed ? $"наблюдение {FormatTime(observed)}" : null,
                section.LastSuccessfulAt is { } last ? $"последнее успешное чтение {FormatTime(last)}" : null),
            AccountReadState.AccessLost or AccountReadState.PreviousSnapshot =>
                section.LastSuccessfulAt is { } last ? $"последнее успешное чтение {FormatTime(last)}" : null,
            _ => null
        };
        return Join(state, timeDetails, section.FailureReason);
    }

    private static string HistoryStatus(AccountSection<IReadOnlyList<ClosedTrade>> section, int count)
    {
        var details = SectionDetails(section);
        if (section.Value is null)
            return Join(details, "строки истории не получены");
        if (count > 0)
            return Join(details, $"строк: {count}");

        var countDetails = section.State switch
        {
            AccountReadState.Available => "прочитано сделок: 0",
            AccountReadState.ConfirmedEmpty => "подтверждённо 0 сделок",
            AccountReadState.PreviousSnapshot => "строк в прошлом снимке: 0",
            _ => "актуальные строки не получены"
        };
        return Join(details, countDetails);
    }

    private static string EmptyText(AccountReadState state, int count) => count > 0
        ? string.Empty
        : state switch
        {
            AccountReadState.ConfirmedEmpty => "Подтверждённый пустой список.",
            AccountReadState.AccessLost or AccountReadState.NotRead => "Данные недоступны.",
            AccountReadState.PreviousSnapshot => "В прошлом снимке список был пуст.",
            _ => "Строки не получены."
        };

    private static string Money(MoneyAmount? amount) => amount is null
        ? "Недоступно"
        : $"{amount.Value.ToString("N2", CurrentCulture)} {amount.Currency}";

    private static string Quantity(decimal? quantity, string unit) => quantity is null
        ? "Недоступно"
        : $"{quantity.Value.ToString("0.####", CurrentCulture)} {unit}";

    private static string Direction(PositionDirection direction) => direction switch
    {
        PositionDirection.Long => "Лонг",
        PositionDirection.Short => "Шорт",
        _ => "Направление неизвестно"
    };

    private static string Ownership(PositionOwnership ownership) => ownership switch
    {
        PositionOwnership.SoftwareOwned => "TradeFlow",
        PositionOwnership.Manual => "ручной",
        _ => "неизвестен"
    };

    private static string FormatTime(DateTimeOffset? value) => value is null
        ? "время недоступно"
        : FormatTime(value.Value);

    private static string FormatTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd MMM yyyy HH:mm", CurrentCulture);

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
