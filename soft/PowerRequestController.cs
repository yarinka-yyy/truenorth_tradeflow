using System.Runtime.InteropServices;

namespace TrueNorth.TradeFlow;

internal sealed class WakeProtectionController : IDisposable
{
    private readonly Func<bool, bool> _applyRequest;
    private bool _enabled;
    private bool _active;
    private bool _requestHeld;
    private bool _disposed;

    internal WakeProtectionController(bool enabled, Func<bool, bool>? applyRequest = null)
    {
        _enabled = enabled;
        _applyRequest = applyRequest ?? ApplyWindowsRequest;
        Reconcile();
    }

    internal bool IsRequestHeld => _requestHeld;
    internal bool IsEnabled => _enabled;
    internal string? Error { get; private set; }
    internal event Action? StateChanged;

    internal void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enabled = enabled;
        Reconcile();
    }

    internal void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = active;
        Reconcile();
    }

    private void Reconcile()
    {
        var requested = _enabled && _active;
        if (requested == _requestHeld)
        {
            if (Error is not null)
            {
                Error = null;
                StateChanged?.Invoke();
            }
            return;
        }

        try
        {
            if (!_applyRequest(requested))
            {
                Error = requested
                    ? "Windows отклонила запрос защиты от сна."
                    : "Windows не подтвердила снятие запроса защиты от сна.";
                StateChanged?.Invoke();
                return;
            }

            _requestHeld = requested;
            Error = null;
            StateChanged?.Invoke();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            Error = requested
                ? $"Не удалось запросить защиту от сна: {exception.Message}"
                : $"Не удалось снять запрос защиты от сна: {exception.Message}";
            StateChanged?.Invoke();
        }
    }

    private static bool ApplyWindowsRequest(bool preventSleep)
    {
        // The request is thread-scoped; production callers must acquire and clear it on the WPF UI thread.
        const uint continuous = 0x80000000;
        const uint systemRequired = 0x00000001;
        var flags = preventSleep ? continuous | systemRequired : continuous;
        return SetThreadExecutionState(flags) != 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _active = false;
        Reconcile();
        _disposed = true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);
}
