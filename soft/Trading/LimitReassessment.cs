using TrueNorth.TradeFlow;
using TrueNorth.TradeFlow.Orders;

namespace TrueNorth.TradeFlow.Trading;

internal enum LimitReassessmentOutcome
{
    Keep,
    InvalidNoReplacement,
    Replacement,
    Inconclusive
}

internal enum LimitReassessmentPhase
{
    WaitingForEntry,
    Scheduled,
    CancelPending,
    ReplacementReady,
    ReplacementPendingEntry
}

internal sealed record LimitReassessmentContext(
    Guid AssessmentId,
    string IntentId,
    string EntryOrderId,
    AiAnalysisRequest Request,
    AiSourceResponse OriginResponse,
    AiParsedFields OriginSetup,
    DateTimeOffset ReceivedAt,
    ManagedOrderState Order);

internal sealed record LimitReassessmentResult(
    Guid AssessmentId,
    string IntentId,
    string EntryOrderId,
    Guid OriginRequestId,
    LimitReassessmentOutcome Outcome,
    AiSourceResponse? ReplacementResponse = null,
    string? Reason = null);

internal interface ITradingLoopLimitReassessmentSource
{
    Task<LimitReassessmentResult> ReassessLimitAsync(
        LimitReassessmentContext context,
        CancellationToken cancellationToken);
}

internal sealed record TradingLoopLimitReassessment(
    string IntentId,
    string? EntryOrderId,
    AiAnalysisRequest Request,
    AiSourceResponse OriginResponse,
    DateTimeOffset ReceivedAt,
    int IntervalMinutes,
    DateTimeOffset NextReassessmentAt,
    LimitReassessmentPhase Phase,
    Guid? AssessmentId = null,
    LimitReassessmentResult? Decision = null,
    DateTimeOffset? DecisionReceivedAt = null,
    DateTimeOffset? CancellationConfirmedAt = null,
    int? ReplacementIntervalMinutes = null,
    string? LastStatus = null,
    TradingLoopPreparedEntry? ReplacementIntent = null);
