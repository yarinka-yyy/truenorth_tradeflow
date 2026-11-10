using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using TrueNorth.TradeFlow;

namespace TrueNorth.TradeFlow.Verification;

internal static class TrueNorthSessionChecks
{
    internal static void CheckMarkerClassifier(Action<bool, string> check)
    {
        check(TrueNorthSessionMarkers.Classify(false, false) == TrueNorthSessionState.Unknown,
            "absence of login markers never proves either session state");
        check(TrueNorthSessionMarkers.Classify(true, false) == TrueNorthSessionState.SignedOut,
            "an explicit sign-in action marks the local fixture signed out");
        check(TrueNorthSessionMarkers.Classify(false, true) == TrueNorthSessionState.Authenticated,
            "an explicit sign-out action marks the local fixture authenticated");
        check(TrueNorthSessionMarkers.Classify(true, true) == TrueNorthSessionState.Unknown,
            "conflicting session markers remain unknown");
        checkThrowsForeignOrigins(check);
    }

    private static void checkThrowsForeignOrigins(Action<bool, string> check)
    {
        var rejected = false;
        try
        {
            _ = new TrueNorthSessionService(Path.Combine(Path.GetTempPath(), "unused"), new Uri("https://accounts.google.com/"));
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        check(rejected, "session service rejects any configured origin outside TrueNorth or loopback fixtures");
    }

    internal static async Task CheckTransportAsync(Action<bool, string> check, string artifacts)
    {
        var fixtureRoot = Path.Combine(artifacts, ".fixtures", "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var server = new LoopbackPageServer();
        TrueNorthSessionService? original = null;
        TrueNorthSessionService? reopened = null;
        TrueNorthSessionService? isolated = null;
        TrueNorthSessionService? unknown = null;
        TrueNorthSessionService? moved = null;
        LoopbackPageServer? redirectTarget = null;
        try
        {
            var profile = Path.Combine(fixtureRoot, "edge-profile-one");
            server.Mode = "authenticated";
            original = new TrueNorthSessionService(profile, server.Origin);
            var repeatedOpens = await Task.WhenAll(original.OpenAsync(), original.OpenAsync())
                .WaitAsync(TimeSpan.FromSeconds(45));
            check(repeatedOpens.All(result => result.State == TrueNorthSessionState.Unknown),
                $"repeated Open requests reuse one visible Edge context and do not claim a session ({string.Join("; ", repeatedOpens.Select(Describe))})");
            var firstRead = await original.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            check(firstRead.State == TrueNorthSessionState.Authenticated,
                $"the installed Edge transport reads a positive marker from the page it opened ({Describe(firstRead)})");
            original.Close();

            server.Mode = "retain";
            reopened = new TrueNorthSessionService(profile, server.Origin);
            await reopened.OpenAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var retainedRead = await reopened.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            check(retainedRead.State == TrueNorthSessionState.Authenticated,
                $"closing and reopening the owned Edge context retains the same local profile state ({Describe(retainedRead)})");
            reopened.Close();

            server.Mode = "signed-out";
            isolated = new TrueNorthSessionService(Path.Combine(fixtureRoot, "edge-profile-two"), server.Origin);
            await isolated.OpenAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var isolatedRead = await isolated.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            check(isolatedRead.State == TrueNorthSessionState.SignedOut,
                $"a second Edge profile reads its own local marker without inheriting the first profile ({Describe(isolatedRead)})");
            isolated.Close();

            server.Mode = "none";
            unknown = new TrueNorthSessionService(Path.Combine(fixtureRoot, "edge-profile-unknown"), server.Origin);
            await unknown.OpenAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var noMarker = await unknown.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            check(noMarker.State == TrueNorthSessionState.Unknown,
                $"a fresh page without an explicit marker stays unknown ({Describe(noMarker)})");

            redirectTarget = new LoopbackPageServer();
            moved = new TrueNorthSessionService(Path.Combine(fixtureRoot, "edge-profile-moved"), server.Origin);
            await moved.OpenAsync().WaitAsync(TimeSpan.FromSeconds(30));
            server.RedirectTo = redirectTarget.Origin.AbsoluteUri;
            var movedResult = await moved.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            check(movedResult.State == TrueNorthSessionState.Unknown,
                $"a page redirected away from the expected origin cannot confirm an explicit sign-out marker ({Describe(movedResult)})");
            moved.Close();
            server.RedirectTo = null;

            var delayedRequest = server.DelayNextResponse();
            var delayedCheck = unknown.CheckAsync();
            await delayedRequest.WaitAsync(TimeSpan.FromSeconds(5));
            var closeTimer = System.Diagnostics.Stopwatch.StartNew();
            unknown.Close();
            var cancelled = await delayedCheck.WaitAsync(TimeSpan.FromSeconds(5));
            check(cancelled.State == TrueNorthSessionState.Unknown
                  && closeTimer.Elapsed < TimeSpan.FromSeconds(4),
                $"shutdown cancels the pending check and closes the owned Edge context within its bound ({Describe(cancelled)}; {closeTimer.Elapsed.TotalMilliseconds:0} ms)");
        }
        finally
        {
            original?.Dispose();
            reopened?.Dispose();
            isolated?.Dispose();
            unknown?.Dispose();
            moved?.Dispose();
            redirectTarget?.Dispose();
            server.Dispose();
            var fullFixtureRoot = Path.GetFullPath(fixtureRoot);
            var allowedRoot = Path.GetFullPath(Path.Combine(artifacts, ".fixtures")) + Path.DirectorySeparatorChar;
            if (fullFixtureRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullFixtureRoot).StartsWith("session-", StringComparison.Ordinal))
                Directory.Delete(fullFixtureRoot, recursive: true);
        }
        check(!Directory.Exists(fixtureRoot), "isolated browser profiles are removed after transport verification");
    }

    internal static void CheckOverview(
        MainWindow window,
        RecordingSessionService session,
        Action<bool, string> check,
        Action pumpDispatcher)
    {
        var artifacts = ArtifactsDirectory();
        Directory.CreateDirectory(artifacts);
        var state = Get<TextBlock>(window, "TrueNorthSessionStateText");
        var details = Get<TextBlock>(window, "TrueNorthSessionDetailsText");
        var card = Get<Border>(window, "TrueNorthSessionCard");
        var indicator = Get<Ellipse>(window, "TrueNorthSessionIndicator");
        var open = Get<Button>(window, "OpenTrueNorthButton");
        var verify = Get<Button>(window, "CheckTrueNorthSessionButton");
        var quiet = (SolidColorBrush)window.FindResource("QuietBrush");

        check(state.Text == "Статус входа неизвестен" && details.Text.Contains("отдельном окне Microsoft Edge", StringComparison.Ordinal)
              && indicator.Fill is SolidColorBrush unknownBrush && unknownBrush.Color == quiet.Color
              && open.Visibility == Visibility.Visible && open.Content as string == "Войти"
              && open.Style == window.FindResource("PrimaryAction")
              && verify.Style == window.FindResource("SecondaryAction"),
            "overview starts neutral and gives manual Edge sign-in the primary action");
        check(open.IsTabStop && verify.IsTabStop && open.FocusVisualStyle is not null && verify.FocusVisualStyle is not null
              && AutomationProperties.GetName(open).Contains("отдельном окне Microsoft Edge", StringComparison.Ordinal),
            "session actions have accessible names, keyboard stops and visible focus styles");
        RenderWindow(window, Path.Combine(artifacts, "session-overview-unknown.png"));

        var pendingOpen = new TaskCompletionSource<TrueNorthSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NextOpenTask = pendingOpen.Task;
        Click(open);
        Click(open);
        Click(verify);
        check(session.OpenCalls == 1 && session.CheckCalls == 0 && !open.IsEnabled && !verify.IsEnabled
              && state.Text == "Открываем Edge…",
            "rapid session clicks are coalesced while Edge opens and the UI reports neutral progress");
        pendingOpen.SetResult(new(1, TrueNorthSessionState.Unknown,
            "Окно Microsoft Edge открыто. После ручного входа нажмите «Проверить сессию»."));
        PumpUntil(() => open.IsEnabled, pumpDispatcher);
        check(state.Text == "Статус входа неизвестен", "opening Edge alone never changes the authentication status");

        session.NextCheckResult = new(2, TrueNorthSessionState.SignedOut,
            "На странице TrueNorth отображается явное действие входа.");
        Click(verify);
        var red = (SolidColorBrush)window.FindResource("ErrorBrush");
        check(state.Text == "Требуется войти в TrueNorth" && indicator.Fill is SolidColorBrush signedOutBrush
              && signedOutBrush.Color == red.Color && open.Content as string == "Войти снова"
              && AutomationProperties.GetName(open).Contains("Войти снова", StringComparison.Ordinal),
            "confirmed local signed-out state is red and offers re-login");
        RenderWindow(window, Path.Combine(artifacts, "session-overview-signed-out.png"));

        session.NextCheckResult = new(4, TrueNorthSessionState.Authenticated,
            "На странице TrueNorth отображается явное действие выхода.");
        Click(verify);
        var green = (SolidColorBrush)window.FindResource("AccentBrush");
        check(state.Text == "Вход в TrueNorth подтверждён" && indicator.Fill is SolidColorBrush authenticatedBrush
              && authenticatedBrush.Color == green.Color && open.Visibility == Visibility.Collapsed
              && verify.Style == window.FindResource("PrimaryAction"),
            "confirmed local authenticated state is green and makes recheck primary");
        RenderWindow(window, Path.Combine(artifacts, "session-overview-authenticated.png"));

        window.ApplyTrueNorthSessionResult(new(3, TrueNorthSessionState.SignedOut, "Устаревший результат."));
        check(state.Text == "Вход в TrueNorth подтверждён",
            "an older response cannot replace a newer session state");

        window.ApplyTrueNorthSessionResult(new(5, TrueNorthSessionState.Unknown,
            "На странице нет однозначного признака входа или выхода. Состояние оставлено непроверенным."));
        window.Width = 760;
        window.Height = 560;
        window.UpdateLayout();
        pumpDispatcher();
        var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
        scroll.ScrollToVerticalOffset(0);
        window.UpdateLayout();
        check(window.ActualWidth is >= 759 and <= 761 && card.ActualWidth > 0
              && open.IsVisible && verify.IsVisible && open.ActualWidth > 0 && verify.ActualWidth > 0
              && IsInViewport(open, scroll) && IsInViewport(verify, scroll),
            "unknown-state session controls remain visible and usable at the 760-pixel minimum width");
        Keyboard.Focus(open);
        pumpDispatcher();
        open.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        pumpDispatcher();
        check(Keyboard.FocusedElement == verify && verify.IsKeyboardFocused,
            "keyboard focus moves from sign-in to session verification in action order");
        RenderWindow(window, Path.Combine(artifacts, "session-overview-minimum-width.png"));

        var lateCheck = new TaskCompletionSource<TrueNorthSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NextCheckTask = lateCheck.Task;
        Click(verify);
        window.PrepareForApplicationClose();
        lateCheck.SetResult(new(6, TrueNorthSessionState.Authenticated, "Поздний ответ."));
        for (var attempt = 0; attempt < 4; attempt++)
            pumpDispatcher();
        check(!state.Text.Contains("подтверждён", StringComparison.OrdinalIgnoreCase)
              && !verify.IsEnabled,
            "a late response after shutdown begins cannot mark the session authenticated");
        window.Width = 1040;
        window.Height = 720;
        window.UpdateLayout();
        pumpDispatcher();
    }

    private static void PumpUntil(Func<bool> condition, Action pumpDispatcher)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            pumpDispatcher();
            Thread.Sleep(10);
        }
        pumpDispatcher();
    }

    private static string Describe(TrueNorthSessionResult result) =>
        $"state={result.State}, message={result.Message}";

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool IsInViewport(FrameworkElement element, ScrollViewer scroll)
    {
        var topLeft = element.TransformToAncestor(scroll).Transform(new Point(0, 0));
        var bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).Contains(bounds);
    }

    private static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static string ArtifactsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "004-truenorth-session.md")))
                return Path.Combine(directory.FullName, "artifacts", "004-truenorth-session");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts");
    }

    internal sealed class RecordingSessionService : ITrueNorthSessionService
    {
        internal int OpenCalls { get; private set; }
        internal int CheckCalls { get; private set; }
        internal Task<TrueNorthSessionResult>? NextOpenTask { get; set; }
        internal Task<TrueNorthSessionResult>? NextCheckTask { get; set; }
        internal TrueNorthSessionResult NextOpenResult { get; set; } = new(1, TrueNorthSessionState.Unknown, "Edge открыт.");
        internal TrueNorthSessionResult NextCheckResult { get; set; } = new(2, TrueNorthSessionState.Unknown, "Состояние неизвестно.");

        public Task<TrueNorthSessionResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            var pending = NextOpenTask;
            NextOpenTask = null;
            return pending ?? Task.FromResult(NextOpenResult);
        }

        public Task<TrueNorthSessionResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            var pending = NextCheckTask;
            NextCheckTask = null;
            return pending ?? Task.FromResult(NextCheckResult);
        }

        public void Close()
        {
        }
    }

    private sealed class LoopbackPageServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private string _mode = "authenticated";
        private string? _redirectTo;
        private int _delayNextResponse;
        private TaskCompletionSource<bool>? _delayedRequestStarted;

        internal LoopbackPageServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Origin = new Uri($"http://127.0.0.1:{port}/");
            _server = ServeAsync(_stop.Token);
        }

        internal Uri Origin { get; }
        internal string Mode
        {
            get => Volatile.Read(ref _mode);
            set => Volatile.Write(ref _mode, value);
        }
        internal string? RedirectTo
        {
            get => Volatile.Read(ref _redirectTo);
            set => Volatile.Write(ref _redirectTo, value);
        }

        internal Task DelayNextResponse()
        {
            _delayedRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _delayNextResponse, 1);
            return _delayedRequestStarted.Task;
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    try
                    {
                        var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        _ = await reader.ReadLineAsync(cancellationToken);
                        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }

                        if (Interlocked.Exchange(ref _delayNextResponse, 0) == 1)
                        {
                            Volatile.Read(ref _delayedRequestStarted)?.TrySetResult(true);
                            await Task.Delay(Timeout.Infinite, cancellationToken);
                        }

                        if (Volatile.Read(ref _redirectTo) is { } redirectTo)
                        {
                            var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {redirectTo}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(redirect, cancellationToken);
                            await stream.FlushAsync(cancellationToken);
                            continue;
                        }

                        var mode = Volatile.Read(ref _mode);
                        var body = Encoding.UTF8.GetBytes($"""
                            <!doctype html><meta charset="utf-8"><title>Loopback session fixture</title>
                            <main><h1>Local session test</h1><div id="actions"></div></main>
                            <script>
                              if ('{mode}' !== 'retain') localStorage.setItem('fixture-session', '{mode}');
                              const state = localStorage.getItem('fixture-session');
                              const actions = document.querySelector('#actions');
                              if (state === 'authenticated') actions.innerHTML = '<button>Sign out</button>';
                              else if (state === 'signed-out') actions.innerHTML = '<button>Continue with Google</button>';
                            </script>
                            """);
                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, cancellationToken);
                        await stream.WriteAsync(body, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }
                    catch (Exception exception) when (exception is SocketException or IOException)
                    {
                    }
                }
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested
                                                && exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _server.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
