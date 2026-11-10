using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TrueNorth.TradeFlow.Notifications;

internal sealed class WindowsNotificationChannel : INotificationChannel
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Dispatcher _dispatcher;

    internal WindowsNotificationChannel(Forms.NotifyIcon notifyIcon, Dispatcher dispatcher)
    {
        _notifyIcon = notifyIcon;
        _dispatcher = dispatcher;
    }

    public NotificationChannel Channel => NotificationChannel.Windows;
    public bool IsEnabled => true;
    public bool IsConfigured => _notifyIcon.Visible;

    public async Task<NotificationChannelSendResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = message.SafeText[..Math.Min(message.SafeText.Length, 255)];
            _notifyIcon.ShowBalloonTip(5000, "TrueNorth TradeFlow", text, Forms.ToolTipIcon.Info);
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
        return new(NotificationAttemptStatus.PassedToWindows);
    }
}
