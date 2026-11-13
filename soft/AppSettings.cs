using System.Globalization;
using System.Text.Json;

namespace TrueNorth.TradeFlow;

internal sealed class AppSettings
{
    public List<string?> Assets { get; set; } = [];
    public string? EntryType { get; set; }
    public string CandidateSelectionMethod { get; set; } = "AssetOrder";
    public decimal? MarginBalancePercent { get; set; }
    public decimal? Leverage { get; set; }
    public int PositionCheckMinutes { get; set; } = 60;
    public int LimitOrderCheckMinutes { get; set; } = 2;
    public int PartialFillCancelMinutes { get; set; } = 60;
    public int NewSetupRetryMinutes { get; set; } = 240;
    public int LimitReassessmentMinutes { get; set; } = 240;
    public int ReadRetryCount { get; set; } = 3;
    public int ReadRetryDelaySeconds { get; set; } = 10;
    public int RecoveryCheckSeconds { get; set; } = 60;
    public int EmergencyCloseRetryCount { get; set; } = 3;
    public int EmergencyCloseRetryDelaySeconds { get; set; } = 10;
    public int EmergencyCloseAfterNoticeSeconds { get; set; } = 30;
    public bool NotificationsEnabled { get; set; } = true;
    public bool OpeningNotifications { get; set; } = true;
    public bool ClosingNotifications { get; set; } = true;
    public bool OrderChangeNotifications { get; set; } = true;
    public bool ErrorNotifications { get; set; } = true;
    public bool RecoveryNotifications { get; set; } = true;
    public bool FailureRemindersEnabled { get; set; } = true;
    public int FailureReminderMinutes { get; set; } = 60;
    public bool PreventSleep { get; set; } = true;

    internal AppSettings Copy() => new()
    {
        Assets = Assets?.ToList() ?? [],
        EntryType = EntryType,
        CandidateSelectionMethod = CandidateSelectionMethod,
        MarginBalancePercent = MarginBalancePercent,
        Leverage = Leverage,
        PositionCheckMinutes = PositionCheckMinutes,
        LimitOrderCheckMinutes = LimitOrderCheckMinutes,
        PartialFillCancelMinutes = PartialFillCancelMinutes,
        NewSetupRetryMinutes = NewSetupRetryMinutes,
        LimitReassessmentMinutes = LimitReassessmentMinutes,
        ReadRetryCount = ReadRetryCount,
        ReadRetryDelaySeconds = ReadRetryDelaySeconds,
        RecoveryCheckSeconds = RecoveryCheckSeconds,
        EmergencyCloseRetryCount = EmergencyCloseRetryCount,
        EmergencyCloseRetryDelaySeconds = EmergencyCloseRetryDelaySeconds,
        EmergencyCloseAfterNoticeSeconds = EmergencyCloseAfterNoticeSeconds,
        NotificationsEnabled = NotificationsEnabled,
        OpeningNotifications = OpeningNotifications,
        ClosingNotifications = ClosingNotifications,
        OrderChangeNotifications = OrderChangeNotifications,
        ErrorNotifications = ErrorNotifications,
        RecoveryNotifications = RecoveryNotifications,
        FailureRemindersEnabled = FailureRemindersEnabled,
        FailureReminderMinutes = FailureReminderMinutes,
        PreventSleep = PreventSleep
    };
}

internal sealed class SettingsProfile
{
    public AppSettings Settings { get; set; } = new();
    public string SelectedMode { get; set; } = "Test";
    public bool SavedOperatingIntent { get; set; }

    internal SettingsProfile Copy() => new()
    {
        Settings = Settings?.Copy() ?? new(),
        SelectedMode = SelectedMode,
        SavedOperatingIntent = SavedOperatingIntent
    };

    internal bool IsIncomplete() => Settings is null
        || Settings.Assets is null
        || !Settings.Assets.Any(asset => !string.IsNullOrWhiteSpace(asset))
        || string.IsNullOrWhiteSpace(Settings.EntryType)
        || Settings.MarginBalancePercent is null
        || Settings.Leverage is null;
}

internal sealed class SettingsEnvelope
{
    public int FormatVersion { get; set; } = SettingsStore.CurrentFormatVersion;
    public SettingsProfile Profile { get; set; } = new();
}

internal static class SettingsValidator
{
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal)
    {
        "Test",
        "TrueNorth AI setups"
    };

    internal static SettingsValidation Validate(SettingsProfile profile)
    {
        var normalized = profile.Copy();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = normalized.Settings;

        if (!Modes.Contains(normalized.SelectedMode))
            errors[nameof(SettingsProfile.SelectedMode)] = "Выберите доступный режим.";

        if (profile.Settings is null)
            errors["Settings"] = "Раздел настроек повреждён.";

        if (profile.Settings?.Assets is null)
        {
            errors[nameof(AppSettings.Assets)] = "Список активов повреждён.";
            settings.Assets = [];
        }
        else
        {
            var assets = new List<string?>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicate = false;
            foreach (var asset in profile.Settings.Assets)
            {
                var trimmed = asset?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;
                if (!seen.Add(trimmed))
                    duplicate = true;
                assets.Add(trimmed);
            }
            settings.Assets = assets;
            if (duplicate)
                errors[nameof(AppSettings.Assets)] = "Активы не должны повторяться без учёта регистра.";
        }

        settings.EntryType = string.IsNullOrWhiteSpace(settings.EntryType) ? null : settings.EntryType.Trim();
        if (settings.EntryType is not null && settings.EntryType is not ("Market" or "Limit"))
            errors[nameof(AppSettings.EntryType)] = "Выберите Market или Limit.";

        if (settings.CandidateSelectionMethod is not ("AssetOrder" or "NetProfitAtTakeProfit"))
            errors[nameof(AppSettings.CandidateSelectionMethod)] = "Выберите допустимый способ выбора сетапа.";

        if (settings.MarginBalancePercent is <= 0 or > 100)
            errors[nameof(AppSettings.MarginBalancePercent)] = "Укажите значение больше 0 и не больше 100.";
        if (settings.Leverage is < 1)
            errors[nameof(AppSettings.Leverage)] = "Плечо должно быть не меньше 1.";

        Positive(nameof(AppSettings.PositionCheckMinutes), settings.PositionCheckMinutes, "Интервал должен быть больше 0 минут.");
        Positive(nameof(AppSettings.LimitOrderCheckMinutes), settings.LimitOrderCheckMinutes, "Интервал должен быть больше 0 минут.");
        Positive(nameof(AppSettings.PartialFillCancelMinutes), settings.PartialFillCancelMinutes, "Срок должен быть больше 0 минут.");
        Positive(nameof(AppSettings.NewSetupRetryMinutes), settings.NewSetupRetryMinutes, "Интервал должен быть больше 0 минут.");
        Positive(nameof(AppSettings.LimitReassessmentMinutes), settings.LimitReassessmentMinutes, "Интервал должен быть больше 0 минут.");
        NonNegative(nameof(AppSettings.ReadRetryCount), settings.ReadRetryCount, "Число дополнительных чтений не может быть меньше 0.");
        Positive(nameof(AppSettings.ReadRetryDelaySeconds), settings.ReadRetryDelaySeconds, "Пауза должна быть больше 0 секунд.");
        Positive(nameof(AppSettings.RecoveryCheckSeconds), settings.RecoveryCheckSeconds, "Интервал должен быть больше 0 секунд.");
        NonNegative(nameof(AppSettings.EmergencyCloseRetryCount), settings.EmergencyCloseRetryCount, "Число дополнительных попыток не может быть меньше 0.");
        Positive(nameof(AppSettings.EmergencyCloseRetryDelaySeconds), settings.EmergencyCloseRetryDelaySeconds, "Пауза должна быть больше 0 секунд.");
        Positive(nameof(AppSettings.EmergencyCloseAfterNoticeSeconds), settings.EmergencyCloseAfterNoticeSeconds, "Интервал должен быть больше 0 секунд.");
        Positive(nameof(AppSettings.FailureReminderMinutes), settings.FailureReminderMinutes, "Интервал должен быть больше 0 минут.");

        return new SettingsValidation(normalized, errors);

        void Positive(string field, int value, string message)
        {
            if (value <= 0)
                errors[field] = message;
        }

        void NonNegative(string field, int value, string message)
        {
            if (value < 0)
                errors[field] = message;
        }
    }

    internal static bool TryParseOptionalDecimal(string? text, CultureInfo culture, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(text.Trim(), styles, culture, out var parsed))
            return false;

        value = parsed;
        return true;
    }
}

internal sealed record SettingsValidation(SettingsProfile Profile, IReadOnlyDictionary<string, string> Errors)
{
    internal bool IsValid => Errors.Count == 0;
}
