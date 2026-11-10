namespace TrueNorth.TradeFlow.Account;

public enum AccountReadState
{
    NotRead,
    Available,
    ConfirmedEmpty,
    Incomplete,
    AccessLost,
    PreviousSnapshot
}

public enum PositionOwnership
{
    SoftwareOwned,
    Manual,
    Unknown
}

public enum PositionDirection
{
    Long,
    Short
}

public sealed record MoneyAmount(decimal Value, string Currency);

public sealed record AccountBalance(
    MoneyAmount? Balance,
    MoneyAmount? Equity,
    MoneyAmount? AvailableMargin);

public sealed record OpenPosition(
    string Asset,
    string Contract,
    PositionDirection Direction,
    decimal? Quantity,
    string QuantityUnit,
    MoneyAmount? UnrealizedPnl,
    PositionOwnership Ownership,
    string OwnershipNote);

public sealed record OpenOrder(
    string Asset,
    string Contract,
    PositionDirection Direction,
    decimal TotalQuantity,
    decimal FilledQuantity,
    string QuantityUnit,
    string Status,
    PositionOwnership Ownership,
    string ProtectionStatus)
{
    public decimal? RemainingQuantity => TotalQuantity >= FilledQuantity
        ? TotalQuantity - FilledQuantity
        : null;
}

public sealed record PositionProtection(
    string Contract,
    MoneyAmount? TakeProfit,
    MoneyAmount? StopLoss);

public sealed record ClosedTrade(
    string Asset,
    string Contract,
    PositionDirection Direction,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    MoneyAmount? RealizedPnl,
    string? CloseReason);

public sealed record ExposureIdentity(
    string Venue,
    string Account,
    string Contract,
    IReadOnlyCollection<string> OrderIds,
    IReadOnlyCollection<string> ExecutionIds,
    bool HasMixedExecution = false);

public sealed record TradeOperationIdentity(
    string Venue,
    string Account,
    string Contract,
    IReadOnlyCollection<string> OrderIds,
    IReadOnlyCollection<string> ExecutionIds);

public static class PositionOwnershipClassifier
{
    public static PositionOwnership Classify(
        ExposureIdentity exposure,
        IEnumerable<TradeOperationIdentity> operations,
        bool explicitlyManual = false)
    {
        if (exposure.HasMixedExecution || !HasScope(exposure.Venue, exposure.Account, exposure.Contract))
            return PositionOwnership.Unknown;

        var exposureOrderIds = KnownIds(exposure.OrderIds);
        var exposureExecutionIds = KnownIds(exposure.ExecutionIds);
        var allOperations = operations.ToArray();
        if (allOperations.Any(operation =>
                !HasScope(operation.Venue, operation.Account, operation.Contract)
                && SharesTypedIds(exposureOrderIds, exposureExecutionIds, operation)))
            return PositionOwnership.Unknown;

        var sameScope = allOperations.Where(operation =>
                HasScope(operation.Venue, operation.Account, operation.Contract)
                && StringComparer.Ordinal.Equals(operation.Venue, exposure.Venue)
                && StringComparer.Ordinal.Equals(operation.Account, exposure.Account)
                && StringComparer.Ordinal.Equals(operation.Contract, exposure.Contract))
            .ToArray();

        var overlapping = sameScope
            .Where(operation => SharesTypedIds(exposureOrderIds, exposureExecutionIds, operation))
            .ToArray();

        if (overlapping.Length > 1)
            return PositionOwnership.Unknown;

        if (overlapping.Length == 1)
        {
            var operation = overlapping[0];
            var operationOrderIds = KnownIds(operation.OrderIds);
            var operationExecutionIds = KnownIds(operation.ExecutionIds);
            var hasExposureIds = exposureOrderIds.Count + exposureExecutionIds.Count > 0;
            var fullyLinked = hasExposureIds
                && exposureOrderIds.IsSubsetOf(operationOrderIds)
                && exposureExecutionIds.IsSubsetOf(operationExecutionIds);
            return fullyLinked && !explicitlyManual
                ? PositionOwnership.SoftwareOwned
                : PositionOwnership.Unknown;
        }

        return explicitlyManual
            ? PositionOwnership.Manual
            : PositionOwnership.Unknown;
    }

    private static HashSet<string> KnownIds(IEnumerable<string>? ids) =>
        ids?.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal) ?? [];

    private static bool SharesTypedIds(
        HashSet<string> exposureOrderIds,
        HashSet<string> exposureExecutionIds,
        TradeOperationIdentity operation) =>
        exposureOrderIds.Overlaps(KnownIds(operation.OrderIds))
        || exposureExecutionIds.Overlaps(KnownIds(operation.ExecutionIds));

    private static bool HasScope(string? venue, string? account, string? contract) =>
        !string.IsNullOrWhiteSpace(venue)
        && !string.IsNullOrWhiteSpace(account)
        && !string.IsNullOrWhiteSpace(contract);
}

public sealed record AccountSection<T>
{
    private AccountSection(
        AccountReadState state,
        T? value,
        DateTimeOffset? observedAt,
        string source,
        DateTimeOffset? lastSuccessfulAt,
        string? failureReason)
    {
        State = state;
        Value = value;
        ObservedAt = observedAt;
        Source = source;
        LastSuccessfulAt = lastSuccessfulAt;
        FailureReason = failureReason;
    }

    public AccountReadState State { get; }
    public T? Value { get; }
    public DateTimeOffset? ObservedAt { get; }
    public string Source { get; }
    public DateTimeOffset? LastSuccessfulAt { get; }
    public string? FailureReason { get; }

    public static AccountSection<T> Available(T value, DateTimeOffset observedAt, string source) =>
        new(AccountReadState.Available, value, observedAt, source, observedAt, null);

    public static AccountSection<T> ConfirmedEmpty(T value, DateTimeOffset observedAt, string source) =>
        new(AccountReadState.ConfirmedEmpty, value, observedAt, source, observedAt, null);

    public static AccountSection<T> Incomplete(
        T value,
        DateTimeOffset observedAt,
        string source,
        string failureReason,
        DateTimeOffset? lastSuccessfulAt = null) =>
        new(AccountReadState.Incomplete, value, observedAt, source, lastSuccessfulAt, failureReason);

    public static AccountSection<T> AccessLost(
        string source,
        string failureReason,
        DateTimeOffset? lastSuccessfulAt = null) =>
        new(AccountReadState.AccessLost, default, null, source, lastSuccessfulAt, failureReason);

    public static AccountSection<T> PreviousSnapshot(
        T value,
        DateTimeOffset observedAt,
        string source,
        DateTimeOffset lastSuccessfulAt,
        string failureReason) =>
        new(AccountReadState.PreviousSnapshot, value, observedAt, source, lastSuccessfulAt, failureReason);

    public static AccountSection<T> NotRead(string source) =>
        new(AccountReadState.NotRead, default, null, source, null, null);
}

public sealed record AccountSnapshot(
    string Venue,
    string Account,
    string CollateralPool,
    DateTimeOffset ObservedAt,
    string Source,
    string QuoteCurrency,
    string QuantityUnit,
    AccountReadState Availability,
    string? AvailabilityFailure,
    DateTimeOffset? LastSuccessfulReadAt,
    AccountSection<AccountBalance> Balances,
    AccountSection<IReadOnlyList<OpenPosition>> Positions,
    AccountSection<IReadOnlyList<OpenOrder>> Orders,
    AccountSection<IReadOnlyList<PositionProtection>> Protections,
    AccountSection<IReadOnlyList<ClosedTrade>> History);
