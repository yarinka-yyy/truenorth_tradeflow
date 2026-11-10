using System.IO;
using System.Security;
using System.Text.Json;

namespace TrueNorth.TradeFlow;

internal enum SettingsFileStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedVersion,
    Unavailable
}

internal enum SettingsOperationStatus
{
    Saved,
    Recovered,
    Invalid,
    Corrupt,
    UnsupportedVersion,
    Unavailable,
    Changed,
    Failed
}

internal sealed record SettingsLoadResult(
    SettingsProfile Profile,
    SettingsFileStatus Status,
    string? Message = null,
    bool CanRecover = false);

internal sealed record SettingsOperationResult(
    SettingsProfile? Profile,
    SettingsOperationStatus Status,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Errors = null)
{
    internal bool Succeeded => Status is SettingsOperationStatus.Saved or SettingsOperationStatus.Recovered;
}

internal sealed class SettingsStore
{
    internal const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Func<bool?>? _legacyPreventSleepReader;
    private readonly Action<string, string, string?>? _commitOverride;
    private SettingsFileStatus? _blockedLoadStatus;

    internal SettingsStore(
        string path,
        Func<bool?>? legacyPreventSleepReader = null,
        Action<string, string, string?>? commitOverride = null)
    {
        _path = Path.GetFullPath(path);
        _legacyPreventSleepReader = legacyPreventSleepReader;
        _commitOverride = commitOverride;
    }

    internal string PathName => _path;

    internal SettingsLoadResult Load()
    {
        var read = Read(_path);
        _blockedLoadStatus = read.Status is SettingsFileStatus.UnsupportedVersion or SettingsFileStatus.Unavailable
            ? read.Status
            : null;
        switch (read.Status)
        {
            case SettingsFileStatus.Loaded:
                return new(read.Profile!, read.Status);
            case SettingsFileStatus.Missing:
            {
                var defaults = new SettingsProfile();
                try
                {
                    if (_legacyPreventSleepReader?.Invoke() is bool legacyValue)
                        defaults.Settings.PreventSleep = legacyValue;
                    return new(defaults, read.Status, "Настройки ещё не сохранены.");
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
                {
                    return new(defaults, read.Status,
                        $"Настройки ещё не сохранены. Старое значение защиты от сна прочитать не удалось: {exception.Message}");
                }
            }
            case SettingsFileStatus.Corrupt:
            {
                var backupStatus = Read(BackupPath).Status;
                var canRecover = backupStatus == SettingsFileStatus.Loaded;
                return new(new SettingsProfile(), read.Status,
                    read.Message + (canRecover
                        ? " Доступна последняя исправная копия."
                        : " Исправная резервная копия не найдена."),
                    canRecover);
            }
            default:
                return new(new SettingsProfile(), read.Status, read.Message);
        }
    }

    internal SettingsOperationResult Save(SettingsProfile profile, bool replaceCorruptFile = false)
    {
        if (_blockedLoadStatus is SettingsFileStatus.UnsupportedVersion or SettingsFileStatus.Unavailable)
            return new(null,
                _blockedLoadStatus == SettingsFileStatus.UnsupportedVersion
                    ? SettingsOperationStatus.UnsupportedVersion
                    : SettingsOperationStatus.Unavailable,
                "При запуске файл настроек был недоступен или создан более новой версией. Перезапустите приложение, чтобы загрузить его заново.");

        var validation = SettingsValidator.Validate(profile);
        if (!validation.IsValid)
            return new(null, SettingsOperationStatus.Invalid, "Исправьте отмеченные поля.", validation.Errors);

        var current = Read(_path);
        if (current.Status == SettingsFileStatus.UnsupportedVersion)
        {
            _blockedLoadStatus = SettingsFileStatus.UnsupportedVersion;
            return new(null, SettingsOperationStatus.UnsupportedVersion,
                "Файл создан другой версией приложения. Текущая версия не будет его перезаписывать.");
        }
        if (current.Status == SettingsFileStatus.Unavailable)
        {
            _blockedLoadStatus = SettingsFileStatus.Unavailable;
            return new(null, SettingsOperationStatus.Unavailable,
                current.Message ?? "Файл настроек недоступен для чтения.");
        }
        if (current.Status == SettingsFileStatus.Corrupt && !replaceCorruptFile)
            return new(null, SettingsOperationStatus.Corrupt,
                "Файл настроек повреждён. Сначала восстановите исправную копию или сохраните форму явно.");

        FileRead? backupGuard = null;
        if (current.Status == SettingsFileStatus.Loaded)
        {
            backupGuard = Read(BackupPath);
            if (backupGuard.Status == SettingsFileStatus.UnsupportedVersion)
            {
                _blockedLoadStatus = SettingsFileStatus.UnsupportedVersion;
                return new(null, SettingsOperationStatus.UnsupportedVersion,
                    "Резервная копия создана более новой версией приложения. Сохранение остановлено, чтобы её не заменить.");
            }
            if (backupGuard.Status == SettingsFileStatus.Unavailable)
            {
                _blockedLoadStatus = SettingsFileStatus.Unavailable;
                return new(null, SettingsOperationStatus.Unavailable,
                    backupGuard.Message ?? "Резервная копия недоступна для чтения.");
            }
        }

        return Write(validation.Profile, current, current.Status == SettingsFileStatus.Corrupt,
            backupGuard: backupGuard);
    }

    internal SettingsOperationResult Recover()
    {
        var current = Read(_path);
        if (current.Status != SettingsFileStatus.Corrupt)
            return new(null, SettingsOperationStatus.Corrupt, "Основной файл не распознан как повреждённый.");

        var backup = Read(BackupPath);
        if (backup.Status != SettingsFileStatus.Loaded)
            return new(null, backup.Status == SettingsFileStatus.UnsupportedVersion
                ? SettingsOperationStatus.UnsupportedVersion
                : SettingsOperationStatus.Corrupt,
                "Проверенная резервная копия недоступна.");

        return Write(backup.Profile!, current, archiveCorruptFile: true,
            backupGuard: backup, recovered: true);
    }

    private SettingsOperationResult Write(
        SettingsProfile profile,
        FileRead current,
        bool archiveCorruptFile,
        FileRead? backupGuard = null,
        bool recovered = false)
    {
        string? temporaryPath = null;
        string? backupTemporaryPath = null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new IOException("Не удалось определить каталог настроек.");
            Directory.CreateDirectory(directory);

            var committedProfile = profile.Copy();
            var envelope = new SettingsEnvelope { Profile = committedProfile };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (Parse(bytes).Status != SettingsFileStatus.Loaded)
                return new(null, SettingsOperationStatus.Failed, "Подготовленный файл настроек не прошёл проверку.");

            temporaryPath = System.IO.Path.Combine(directory,
                $"{System.IO.Path.GetFileName(_path)}.tmp.{Guid.NewGuid():N}");
            WriteFlushed(temporaryPath, bytes);

            var publishedBackupPath = current.Status switch
            {
                SettingsFileStatus.Loaded => BackupPath,
                SettingsFileStatus.Corrupt when archiveCorruptFile => CorruptArchivePath(),
                _ => null
            };
            if (publishedBackupPath is not null)
            {
                if (current.Bytes is null)
                    return new(null, SettingsOperationStatus.Failed, "Исходный файл нельзя проверить перед заменой.");
                backupTemporaryPath = System.IO.Path.Combine(directory,
                    $"{System.IO.Path.GetFileName(_path)}.backup.tmp.{Guid.NewGuid():N}");
                WriteFlushed(backupTemporaryPath, current.Bytes);
            }

            var currentAgain = Read(_path);
            if (!SameFile(current, currentAgain))
                return new(null, currentAgain.Status == SettingsFileStatus.UnsupportedVersion
                    ? SettingsOperationStatus.UnsupportedVersion
                    : SettingsOperationStatus.Changed,
                    "Файл настроек изменился во время сохранения. Повторите действие после проверки файла.");

            if (backupGuard is not null && !SameFile(backupGuard, Read(BackupPath)))
                return new(null, SettingsOperationStatus.Changed,
                    "Резервная копия изменилась во время восстановления. Повторите проверку.");

            if (publishedBackupPath is not null)
            {
                var replaceExistingBackup = publishedBackupPath == BackupPath
                    && backupGuard?.Status != SettingsFileStatus.Missing;
                File.Move(backupTemporaryPath!, publishedBackupPath, overwrite: replaceExistingBackup);
                backupTemporaryPath = null;

                if (!SameFile(current, Read(_path)))
                    return new(null, SettingsOperationStatus.Changed,
                        "Основной файл изменился до завершения сохранения; его значения не заменены.");
            }

            if (_commitOverride is null)
            {
                if (current.Status == SettingsFileStatus.Missing)
                    File.Move(temporaryPath, _path);
                else
                    File.Move(temporaryPath, _path, overwrite: true);
            }
            else
            {
                _commitOverride(temporaryPath, _path, publishedBackupPath);
            }

            return new(committedProfile, recovered ? SettingsOperationStatus.Recovered : SettingsOperationStatus.Saved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or SecurityException or ArgumentException or NotSupportedException)
        {
            return new(null, SettingsOperationStatus.Failed,
                $"Не удалось сохранить настройки. Основной файл не заменён: {exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            if (backupTemporaryPath is not null)
            {
                try
                {
                    File.Delete(backupTemporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void WriteFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private string BackupPath => _path + ".bak";

    private string CorruptArchivePath() => _path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")
        + "." + Guid.NewGuid().ToString("N");

    private static bool SameFile(FileRead left, FileRead right) => left.Status == right.Status
        && (left.Bytes is null ? right.Bytes is null : right.Bytes is not null && left.Bytes.AsSpan().SequenceEqual(right.Bytes));

    private static FileRead Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return new(SettingsFileStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(SettingsFileStatus.Missing);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new(SettingsFileStatus.Unavailable, Message:
                $"Файл настроек нельзя прочитать или заменить: {exception.Message}");
        }

        return Parse(bytes);
    }

    private static FileRead Parse(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("formatVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number)
                return new(SettingsFileStatus.Corrupt, bytes, Message: "Файл настроек повреждён или не содержит версию формата.");

            if (!versionElement.TryGetInt32(out var version) || version != CurrentFormatVersion)
                return new(SettingsFileStatus.UnsupportedVersion, bytes,
                    Message: $"Числовая версия файла настроек {versionElement.GetRawText()} не поддерживается этим приложением.");

            if (!json.RootElement.TryGetProperty("profile", out var profileElement)
                || profileElement.ValueKind != JsonValueKind.Object
                || !profileElement.TryGetProperty("settings", out var settingsElement)
                || settingsElement.ValueKind != JsonValueKind.Object
                || !profileElement.TryGetProperty("selectedMode", out var modeElement)
                || modeElement.ValueKind != JsonValueKind.String
                || !profileElement.TryGetProperty("savedOperatingIntent", out var intentElement)
                || intentElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(SettingsFileStatus.Corrupt, bytes, Message: "Файл настроек не содержит всех обязательных разделов.");

            string[] requiredSettings =
            [
                "assets", "entryType", "marginBalancePercent", "leverage", "positionCheckMinutes",
                "limitOrderCheckMinutes", "partialFillCancelMinutes", "newSetupRetryMinutes",
                "limitReassessmentMinutes", "readRetryCount", "readRetryDelaySeconds", "recoveryCheckSeconds",
                "emergencyCloseRetryCount", "emergencyCloseRetryDelaySeconds", "emergencyCloseAfterNoticeSeconds",
                "notificationsEnabled", "openingNotifications", "closingNotifications", "orderChangeNotifications",
                "errorNotifications", "recoveryNotifications", "failureRemindersEnabled", "failureReminderMinutes",
                "preventSleep"
            ];
            if (requiredSettings.Any(field => !settingsElement.TryGetProperty(field, out _)))
                return new(SettingsFileStatus.Corrupt, bytes, Message: "В файле настроек отсутствуют обязательные поля.");

            var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(bytes, JsonOptions);
            if (envelope?.Profile is null)
                return new(SettingsFileStatus.Corrupt, bytes, Message: "Файл настроек не содержит профиля.");

            var validation = SettingsValidator.Validate(envelope.Profile);
            if (!validation.IsValid)
                return new(SettingsFileStatus.Corrupt, bytes, Message:
                    "Файл настроек содержит недопустимые значения: " + string.Join(" ", validation.Errors.Values));

            return new(SettingsFileStatus.Loaded, bytes, validation.Profile);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return new(SettingsFileStatus.Corrupt, bytes, Message: $"Файл настроек повреждён: {exception.Message}");
        }
    }

    private sealed record FileRead(
        SettingsFileStatus Status,
        byte[]? Bytes = null,
        SettingsProfile? Profile = null,
        string? Message = null);
}
