using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Playwright;

namespace TrueNorth.TradeFlow;

internal enum TrueNorthSessionState
{
    Unknown,
    SignedOut,
    Authenticated
}

internal sealed record TrueNorthSessionResult(long Revision, TrueNorthSessionState State, string Message);

internal interface ITrueNorthSessionService
{
    Task<TrueNorthSessionResult> OpenAsync(CancellationToken cancellationToken = default);
    Task<TrueNorthSessionResult> CheckAsync(CancellationToken cancellationToken = default);
    void Close();
}

internal static class TrueNorthSessionMarkers
{
    internal static TrueNorthSessionState Classify(bool hasSignIn, bool hasSignOut) =>
        (hasSignIn, hasSignOut) switch
        {
            (true, false) => TrueNorthSessionState.SignedOut,
            (false, true) => TrueNorthSessionState.Authenticated,
            _ => TrueNorthSessionState.Unknown
        };
}

internal sealed class TrueNorthSessionService : ITrueNorthSessionService, IDisposable
{
    private static readonly Uri AppUri = new("https://app.truenorth.xyz/");
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(20);
    private const string ReadMarkersScript = """
        expected => {
            if (window.location.origin !== expected.origin || window.location.href !== expected.url)
                return { pageMatches: false, hasSignIn: false, hasSignOut: false };
            const visible = element => {
                const style = window.getComputedStyle(element);
                const box = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden'
                    && Number(style.opacity) !== 0 && box.width > 0 && box.height > 0;
            };
            const labels = Array.from(document.querySelectorAll('button,a,[role="button"],input[type="submit"]'))
                .filter(visible)
                .map(element => (element.getAttribute('aria-label') || element.innerText
                    || (element instanceof HTMLInputElement ? element.value : '') || '')
                    .trim().replace(/\s+/g, ' ').toLowerCase());
            return {
                pageMatches: true,
                hasSignIn: labels.some(label => ['sign in', 'log in', 'continue with google', 'войти', 'войти через google'].includes(label)),
                hasSignOut: labels.some(label => ['sign out', 'log out', 'выйти'].includes(label))
            };
        }
        """;

    private readonly string _profilePath;
    private readonly Uri _siteUri;
    private readonly string _siteUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private int _contextClosed;
    private int _disposed;
    private long _revision;

    internal TrueNorthSessionService()
        : this(DefaultProfilePath, AppUri)
    {
    }

    internal TrueNorthSessionService(string profilePath, Uri siteUri)
    {
        if (!IsSupportedSite(siteUri) && !(siteUri.Scheme == Uri.UriSchemeHttp && siteUri.IsLoopback))
            throw new ArgumentException("Only app.truenorth.xyz or a loopback test page is supported.", nameof(siteUri));

        _profilePath = Path.GetFullPath(profilePath);
        _siteUri = new UriBuilder(siteUri) { Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri;
        _siteUrl = _siteUri.AbsoluteUri;
    }

    internal static string DefaultProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrueNorth TradeFlow", "Browser", "Edge");

    public Task<TrueNorthSessionResult> OpenAsync(CancellationToken cancellationToken = default) =>
        RunAsync(checkSession: false, cancellationToken);

    public Task<TrueNorthSessionResult> CheckAsync(CancellationToken cancellationToken = default) =>
        RunAsync(checkSession: true, cancellationToken);

    private async Task<TrueNorthSessionResult> RunAsync(bool checkSession, CancellationToken cancellationToken)
    {
        var revision = Interlocked.Increment(ref _revision);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        var entered = false;
        var launching = _context is null || Volatile.Read(ref _contextClosed) != 0;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _disposed) != 0)
                return Unknown(revision, "Приложение закрывает окно Edge.");

            var context = await EnsureContextAsync(linked.Token).ConfigureAwait(false);
            launching = false;
            var page = await GetPageAsync(context).ConfigureAwait(false);

            if (!checkSession)
            {
                if (IsBlank(page.Url))
                    await NavigateAsync(page, linked.Token).ConfigureAwait(false);
                await page.BringToFrontAsync().WaitAsync(linked.Token).ConfigureAwait(false);
                return Unknown(revision, IsExpectedOrigin(page.Url)
                    ? "Окно Microsoft Edge открыто. После ручного входа нажмите «Проверить сессию»."
                    : "Завершите ручной вход в Edge и вернитесь на страницу TrueNorth.");
            }

            if (!IsExpectedOrigin(page.Url) && !IsBlank(page.Url))
            {
                await page.BringToFrontAsync().WaitAsync(linked.Token).ConfigureAwait(false);
                return Unknown(revision, "Проверка выполняется только на TrueNorth. Завершите вход в Edge и вернитесь на app.truenorth.xyz.");
            }

            await NavigateAsync(page, linked.Token).ConfigureAwait(false);
            if (page.IsClosed || !IsExpectedOrigin(page.Url))
                return Unknown(revision, "После загрузки открылась другая страница. Её содержимое не проверялось.");

            // shortcut: live markers are unverified; classify them only in loopback fixtures until the owner validates a site marker.
            if (!_siteUri.IsLoopback)
                return Unknown(revision, "Маркеры входа на сайте пока не подтверждены. Состояние оставлено непроверенным.");

            var pageUrlAtRead = page.Url;
            var expectedLocation = new Dictionary<string, string>
            {
                ["origin"] = _siteUri.GetLeftPart(UriPartial.Authority),
                ["url"] = pageUrlAtRead
            };
            var markers = await page.EvaluateAsync<JsonElement>(ReadMarkersScript, expectedLocation)
                .WaitAsync(linked.Token).ConfigureAwait(false);
            if (page.IsClosed || !IsExpectedOrigin(page.Url)
                || !string.Equals(page.Url, pageUrlAtRead, StringComparison.Ordinal)
                || !markers.GetProperty("pageMatches").GetBoolean())
                return Unknown(revision, "Страница изменилась во время проверки. Состояние оставлено непроверенным.");
            var state = TrueNorthSessionMarkers.Classify(
                markers.GetProperty("hasSignIn").GetBoolean(),
                markers.GetProperty("hasSignOut").GetBoolean());
            var message = state switch
            {
                TrueNorthSessionState.SignedOut => "На странице TrueNorth отображается явное действие входа. Войти можно в отдельном окне Edge.",
                TrueNorthSessionState.Authenticated => "На странице TrueNorth отображается явное действие выхода.",
                _ => "На странице нет однозначного признака входа или выхода. Состояние оставлено непроверенным."
            };
            return new(revision, state, message);
        }
        catch (OperationCanceledException)
        {
            return Unknown(revision, "Проверку отменили при завершении приложения.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return Unknown(revision, "Профиль Edge недоступен. Проверьте права на папку профиля и повторите попытку.");
        }
        catch (TimeoutException)
        {
            return Unknown(revision, "Истекло время ожидания загрузки TrueNorth. Проверьте сеть и повторите попытку.");
        }
        catch (PlaywrightException)
        {
            return Unknown(revision, launching
                ? "Профиль Edge занят или недоступен. Закройте другое окно TradeFlow с этой сессией и повторите попытку."
                : "Не удалось загрузить или проверить TrueNorth. Проверьте сеть и повторите попытку.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Unknown(revision, "TrueNorth вернул неоднозначный ответ страницы. Состояние оставлено непроверенным.");
        }
        finally
        {
            if (entered)
                _gate.Release();
        }
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken cancellationToken)
    {
        if (_context is not null && Volatile.Read(ref _contextClosed) == 0)
            return _context;

        _context = null;
        _page = null;
        _playwright?.Dispose();
        _playwright = null;
        Interlocked.Exchange(ref _contextClosed, 0);

        var directory = Path.GetDirectoryName(_profilePath)
            ?? throw new IOException("Browser profile path has no parent directory.");
        Directory.CreateDirectory(directory);
        var playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await playwright.Chromium.LaunchPersistentContextAsync(_profilePath, new()
            {
                Channel = "msedge",
                Headless = false,
                Timeout = (float)BrowserTimeout.TotalMilliseconds
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            _playwright = playwright;
            _context = context;
            context.Close += (_, _) =>
            {
                if (ReferenceEquals(Volatile.Read(ref _context), context))
                    Interlocked.Exchange(ref _contextClosed, 1);
            };
            return context;
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    private async Task<IPage> GetPageAsync(IBrowserContext context)
    {
        if (_page is { IsClosed: false })
            return _page;

        _page = context.Pages.LastOrDefault(page => !page.IsClosed) ?? await context.NewPageAsync().ConfigureAwait(false);
        _page.SetDefaultTimeout((float)BrowserTimeout.TotalMilliseconds);
        _page.SetDefaultNavigationTimeout((float)BrowserTimeout.TotalMilliseconds);
        return _page;
    }

    private async Task NavigateAsync(IPage page, CancellationToken cancellationToken)
    {
        await page.GotoAsync(_siteUrl, new()
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = (float)BrowserTimeout.TotalMilliseconds
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static TrueNorthSessionResult Unknown(long revision, string message) =>
        new(revision, TrueNorthSessionState.Unknown, message);

    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Interlocked.Increment(ref _revision);
        _shutdown.Cancel();
        var closeTask = CloseOwnedContextAsync();
        try
        {
            if (!closeTask.Wait(TimeSpan.FromSeconds(3)))
                _playwright?.Dispose();
        }
        catch (AggregateException)
        {
            _playwright?.Dispose();
        }
    }

    private async Task CloseOwnedContextAsync()
    {
        var entered = false;
        try
        {
            entered = await _gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!entered)
                return;

            var context = _context;
            _context = null;
            _page = null;
            if (context is not null && Volatile.Read(ref _contextClosed) == 0)
                await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or ObjectDisposedException)
        {
        }
        finally
        {
            _playwright?.Dispose();
            _playwright = null;
            if (entered)
                _gate.Release();
        }
    }

    public void Dispose()
    {
        Close();
        _shutdown.Dispose();
    }

    private bool IsExpectedOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(_siteUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(_siteUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _siteUri.Port
        && uri.UserInfo.Length == 0;

    private static bool IsSupportedSite(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals(AppUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == AppUri.Port;

    private static bool IsBlank(string value) => value.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
}
