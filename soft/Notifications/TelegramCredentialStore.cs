using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueNorth.TradeFlow.Notifications;

internal sealed class TelegramCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly Action<string, string>? _commitOverride;
    private int _forceDisabled;

    internal TelegramCredentialStore(string path, Action<string, string>? commitOverride = null)
    {
        _path = Path.GetFullPath(path);
        _commitOverride = commitOverride;
    }

    internal bool IsEnabled => Volatile.Read(ref _forceDisabled) == 0
        && TryLoad(out _, out var enabled) && enabled;

    internal bool HasCredentials => TryLoad(out var credentials, out _) && credentials is not null;

    internal bool TryLoad(out TelegramCredentials? credentials) => TryLoad(out credentials, out _);

    internal bool TryLoad(out TelegramCredentials? credentials, out bool enabled) =>
        TryLoad(out credentials, out enabled, out _);

    private bool TryLoad(out TelegramCredentials? credentials, out bool enabled, out bool missing)
    {
        credentials = null;
        enabled = false;
        missing = false;
        try
        {
            byte[] protectedBytes;
            try
            {
                protectedBytes = File.ReadAllBytes(_path);
            }
            catch (FileNotFoundException)
            {
                missing = true;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                missing = true;
                return false;
            }
            var clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            SavedCredentials? saved;
            try { saved = JsonSerializer.Deserialize<SavedCredentials>(clearBytes, JsonOptions); }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
            if (saved is null)
                return false;
            var hasToken = !string.IsNullOrWhiteSpace(saved.Token);
            var hasChatId = !string.IsNullOrWhiteSpace(saved.ChatId);
            if (hasToken != hasChatId || (saved.Enabled && !hasToken)
                || (hasToken && !IsValidPair(saved.Token, saved.ChatId)))
                return false;
            enabled = saved.Enabled;
            if (hasToken)
                credentials = new(saved.Token!, saved.ChatId!);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    internal bool SavePair(string token, string chatId, bool enabled)
    {
        if (!enabled)
            Volatile.Write(ref _forceDisabled, 1);
        token = token.Trim();
        chatId = chatId.Trim();
        if (!IsValidPair(token, chatId))
            return false;

        var saved = SaveConfiguration(enabled, token, chatId);
        if (saved)
            Volatile.Write(ref _forceDisabled, enabled ? 0 : 1);
        return saved;
    }

    internal bool SetEnabled(bool enabled)
    {
        if (!enabled)
            Volatile.Write(ref _forceDisabled, 1);
        if (!TryLoad(out var credentials, out var currentlyEnabled, out var missing))
        {
            if (enabled || !missing)
                return false;
            return SaveConfiguration(false, null, null);
        }
        if (enabled && credentials is null)
            return false;
        if (enabled == currentlyEnabled)
        {
            if (enabled)
                Volatile.Write(ref _forceDisabled, 0);
            return true;
        }
        var saved = SaveConfiguration(enabled, credentials?.Token, credentials?.ChatId);
        if (saved && enabled)
            Volatile.Write(ref _forceDisabled, 0);
        return saved;
    }

    private bool SaveConfiguration(bool enabled, string? token, string? chatId)
    {

        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new IOException("Credential directory is unavailable.");
            Directory.CreateDirectory(directory);
            var clearBytes = JsonSerializer.SerializeToUtf8Bytes(new SavedCredentials(enabled, token, chatId), JsonOptions);
            byte[] protectedBytes;
            try { protectedBytes = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
            temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_path)}.tmp.{Guid.NewGuid():N}");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            if (_commitOverride is null)
                File.Move(temporaryPath, _path, overwrite: File.Exists(_path));
            else
                _commitOverride(temporaryPath, _path);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or CryptographicException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsValidPair(string? token, string? chatId)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256 || token.Any(char.IsWhiteSpace)
            || string.IsNullOrWhiteSpace(chatId)
            || !long.TryParse(chatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var numericChatId)
            || numericChatId == 0)
            return false;

        var separator = token.IndexOf(':');
        return separator is > 0 and < 21
            && token.IndexOf(':', separator + 1) < 0
            && token[..separator].All(char.IsAsciiDigit)
            && token[(separator + 1)..].Length >= 16
            && token[(separator + 1)..].All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private sealed record SavedCredentials(bool Enabled, string? Token, string? ChatId);
}
