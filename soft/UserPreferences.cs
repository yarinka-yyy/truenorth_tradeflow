using System.IO;
using Microsoft.Win32;

namespace TrueNorth.TradeFlow;

internal static class UserPreferences
{
    private const string KeyPath = @"Software\TrueNorth TradeFlow";
    private const string PreventSleepName = "PreventSleep";

    internal static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrueNorth TradeFlow",
        "settings.json");

    internal static string RecoveryStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrueNorth TradeFlow",
        "recovery-state.json");

    internal static string TelegramCredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrueNorth TradeFlow",
        "telegram-credentials.dat");

    internal static bool? ReadLegacyPreventSleep()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(PreventSleepName) is int value ? value != 0 : null;
    }
}
