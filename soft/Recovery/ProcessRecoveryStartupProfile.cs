using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace TrueNorth.TradeFlow.Recovery;

internal static class ProcessRecoveryStartupProfile
{
    private const string TestProfileOption = "--recovery-test-profile";
    private static readonly string[] TestSwitches =
    [
        "--recovery-test-host",
        "--recovery-test-session-ending",
        "--recovery-test-confirmed-exit",
        "--recovery-test-never-ready",
        "--recovery-test-never-ready-on-restart"
    ];

    internal static bool TryResolve(string[] arguments, out bool testHost, out string? profileRoot,
        out string? error)
    {
        testHost = arguments.Contains("--recovery-test-host", StringComparer.OrdinalIgnoreCase);
        profileRoot = null;
        error = null;

        var hasTestSwitch = arguments.Any(argument => TestSwitches.Contains(argument,
            StringComparer.OrdinalIgnoreCase));
        var hasUnknownTestSwitch = arguments.Any(argument => argument.StartsWith("--recovery-test-",
                StringComparison.OrdinalIgnoreCase)
            && !argument.StartsWith(TestProfileOption + "=", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(argument, TestProfileOption, StringComparison.OrdinalIgnoreCase)
            && !TestSwitches.Contains(argument, StringComparer.OrdinalIgnoreCase));
        if (hasUnknownTestSwitch)
        {
            error = "Неизвестный параметр тестового восстановления.";
            return false;
        }

        if (!hasTestSwitch)
            return true;
        if (!testHost)
        {
            error = "Параметры тестового восстановления требуют изолированный тестовый хост.";
            return false;
        }

        if (!TryReadUniqueProfile(arguments, out var suppliedProfile))
        {
            error = "Тестовому хосту восстановления требуется один явный изолированный профиль.";
            return false;
        }

        if (!TryNormalizeDirectory(suppliedProfile, requireExisting: false, out var suppliedPath))
        {
            error = "Путь тестового профиля недопустим.";
            return false;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var productionPathText = Path.Combine(localAppData, "TrueNorth TradeFlow");
        if (!TryNormalizeDirectory(productionPathText, requireExisting: false, out var productionPath))
        {
            error = "Не удалось проверить путь производственного профиля.";
            return false;
        }

        if (PathsOverlap(suppliedPath, productionPath))
        {
            error = "Тестовому хосту запрещён производственный профиль приложения.";
            return false;
        }

        if (!Directory.Exists(suppliedPath)
            || !TryNormalizeDirectory(suppliedPath, requireExisting: true, out suppliedPath)
            || PathsOverlap(suppliedPath, productionPath))
        {
            error = "Тестовый профиль должен быть существующим изолированным каталогом.";
            return false;
        }

        profileRoot = suppliedPath;
        return true;
    }

    private static bool TryReadUniqueProfile(string[] arguments, out string profile)
    {
        const string option = TestProfileOption;
        var values = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                values.Add(arguments[index][(option.Length + 1)..]);
            else if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Length)
                values.Add(arguments[++index]);
        }

        profile = values.Count == 1 ? values[0] : string.Empty;
        return values.Count == 1 && !string.IsNullOrWhiteSpace(profile);
    }

    private static bool TryNormalizeDirectory(string path, bool requireExisting, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (expanded.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)
                || expanded.StartsWith("\\\\?\\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                return false;

            if (expanded.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                expanded = "\\\\" + expanded[8..];
            else if (expanded.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                expanded = expanded[4..];

            var fullPath = ExpandShortPathNames(Path.GetFullPath(expanded));
            if (requireExisting && !Directory.Exists(fullPath))
                return false;

            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var current = root;
            foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                    continue;

                var directory = new DirectoryInfo(current);
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    continue;
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }

            normalized = Path.GetFullPath(current);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrBelow(first, second) || IsSameOrBelow(second, first);

    private static string ExpandShortPathNames(string path)
    {
        var required = GetLongPathName(path, null, 0);
        if (required == 0 || required > 32768)
            return path;

        var buffer = new StringBuilder((int)required);
        var length = GetLongPathName(path, buffer, required);
        return length == 0 || length >= required ? path : buffer.ToString();
    }

    private static bool IsSameOrBelow(string candidate, string parent)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder? longPath, uint bufferLength);
}
