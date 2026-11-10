using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrueNorth.TradeFlow.Notifications;

internal sealed class TelegramNotificationChannel : INotificationChannel, IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.telegram.org/");
    private readonly TelegramCredentialStore _credentials;
    private readonly HttpClient _client;

    internal TelegramNotificationChannel(
        TelegramCredentialStore credentials,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        _credentials = credentials;
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
    }

    public NotificationChannel Channel => NotificationChannel.Telegram;
    public bool IsEnabled => _credentials.IsEnabled;
    public bool IsConfigured => _credentials.HasCredentials;

    public async Task<NotificationChannelSendResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        if (!_credentials.TryLoad(out var credentials, out var enabled)
            || credentials is null || !enabled || !_credentials.IsEnabled)
            return new(NotificationAttemptStatus.Rejected);

        var endpoint = new UriBuilder(ApiRoot.Scheme, ApiRoot.Host)
        {
            Path = $"bot{credentials.Token}/sendMessage"
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = credentials.ChatId,
                ["text"] = message.SafeText[..Math.Min(message.SafeText.Length, 4096)]
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(NotificationAttemptStatus.Unknown);
        }

        using (response)
        {
            var body = await ReadBoundedResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var apiResult = ParseResponse(body);
            if (apiResult.Ok == false)
                return new(NotificationAttemptStatus.Rejected,
                    response.StatusCode == HttpStatusCode.TooManyRequests ? apiResult.RetryAfterSeconds ?? 60 : null);

            if (response.IsSuccessStatusCode && apiResult.Ok == true)
                return new(NotificationAttemptStatus.AcceptedByTelegram);

            if ((int)response.StatusCode is >= 400 and < 500)
                return new(NotificationAttemptStatus.Rejected,
                    response.StatusCode == HttpStatusCode.TooManyRequests ? apiResult.RetryAfterSeconds ?? 60 : null);

            return new(NotificationAttemptStatus.Unknown);
        }
    }

    public void Dispose() => _client.Dispose();

    private static async Task<byte[]?> ReadBoundedResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var result = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return result.ToArray();
                if (result.Length + read > 65536)
                    return null;
                result.Write(buffer, 0, read);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static ApiResponse ParseResponse(byte[]? body)
    {
        if (body is null)
            return new(null);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(null);
            bool? ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? okValue.GetBoolean()
                : null;
            int? retryAfter = null;
            if (root.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("retry_after", out var retryValue)
                && retryValue.TryGetInt32(out var seconds)
                && seconds > 0)
                retryAfter = seconds;
            return new(ok, retryAfter);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(null);
        }
    }

    private sealed record ApiResponse(bool? Ok, int? RetryAfterSeconds = null);
}
