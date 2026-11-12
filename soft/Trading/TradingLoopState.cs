using TrueNorth.TradeFlow;

namespace TrueNorth.TradeFlow.Trading;

internal sealed record TradingLoopPendingSetup(
    AiAnalysisRequest Request,
    AiSourceResponse Response,
    DateTimeOffset? ReceivedAt);

internal sealed record TradingLoopState
{
    public Dictionary<string, DateTimeOffset> NextSearchAt { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ActiveFailureIds { get; init; } = new(StringComparer.Ordinal);
    public TradingLoopPendingSetup? PendingSetup { get; init; }
    public string? LastStatus { get; init; }

    internal TradingLoopState Copy() => this with
    {
        NextSearchAt = new(NextSearchAt, StringComparer.Ordinal),
        ActiveFailureIds = new(ActiveFailureIds, StringComparer.Ordinal)
    };
}

internal sealed class TradingLoopAdmission
{
    private readonly object _gate = new();
    private bool _started;
    private bool _globallyBlocked;
    private long _generation;

    internal long Generation
    {
        get { lock (_gate) return _generation; }
    }

    internal bool IsStarted
    {
        get { lock (_gate) return _started; }
    }

    internal bool IsAdmitting
    {
        get { lock (_gate) return _started && !_globallyBlocked; }
    }

    internal long Close()
    {
        lock (_gate)
        {
            _started = false;
            _generation++;
            return _generation;
        }
    }

    internal long Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            return _generation;
        }
    }

    internal bool Open(long expectedGeneration)
    {
        lock (_gate)
        {
            if (_generation != expectedGeneration)
                return false;
            _started = true;
            return true;
        }
    }

    internal bool RunIfGeneration(long expectedGeneration, Func<bool> action)
    {
        lock (_gate)
            return _generation == expectedGeneration && action();
    }

    internal void SetGlobalBlock(bool blocked)
    {
        lock (_gate)
            _globallyBlocked = blocked;
    }

    internal bool RunIfAdmitting(Func<bool> action)
    {
        lock (_gate)
            return _started && !_globallyBlocked && action();
    }

}
