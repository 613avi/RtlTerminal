using Microsoft.Win32;
using System.Windows;

namespace RtlTerminal;

public readonly record struct TerminalFontSettings(
    string Family,
    double Size,
    bool Bold,
    bool Italic);

public static class AppSettings
{
    private const string SettingsKey = @"Software\RtlTerminal";
    private static readonly int[] SupportedHistorySizes = [2000, 5000, 10000];

    public static string? LoadTerminalProfile()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("TerminalProfile") as string;
    }

    public static void SaveTerminalProfile(string profile)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("TerminalProfile", profile, RegistryValueKind.String);
    }

    public static IReadOnlyList<string> LoadLastCmdDirectories()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var directories = key?.GetValue("LastCmdDirectories") switch
        {
            string[] values => values,
            string value => [value],
            _ => []
        };

        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    public static void RememberCmdDirectory(string directory)
    {
        var directories = LoadLastCmdDirectories()
            .Where(savedDirectory => !string.Equals(
                savedDirectory,
                directory,
                StringComparison.OrdinalIgnoreCase))
            .Prepend(directory)
            .Take(10)
            .ToArray();

        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(
            "LastCmdDirectories",
            directories,
            RegistryValueKind.MultiString);
    }

    public static int LoadHistorySize()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var savedSize = key?.GetValue("HistorySize") is int value
            ? value
            : 2000;
        return SupportedHistorySizes.Contains(savedSize)
            ? savedSize
            : 2000;
    }

    public static void SaveHistorySize(int historySize)
    {
        if (!SupportedHistorySizes.Contains(historySize))
            historySize = 2000;

        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(
            "HistorySize",
            historySize,
            RegistryValueKind.DWord);
    }

    public static UiLanguage LoadUiLanguage()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return Enum.TryParse(key?.GetValue("UiLanguage") as string, out UiLanguage language)
            ? language
            : UiLanguage.English;
    }

    public static void SaveUiLanguage(UiLanguage language)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("UiLanguage", language.ToString(), RegistryValueKind.String);
    }

    public static string? LoadSkippedUpdateVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("SkippedUpdateVersion") as string;
    }

    public static void SaveSkippedUpdateVersion(string? version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);

        if (string.IsNullOrWhiteSpace(version))
            key.DeleteValue("SkippedUpdateVersion", throwOnMissingValue: false);
        else
            key.SetValue(
                "SkippedUpdateVersion",
                version,
                RegistryValueKind.String);
    }

    public static AppThemeKind LoadTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return Enum.TryParse(key?.GetValue("Theme") as string, out AppThemeKind theme)
            ? theme
            : AppThemeKind.Dark;
    }

    public static void SaveTheme(AppThemeKind theme)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("Theme", theme.ToString(), RegistryValueKind.String);
    }

    public static TerminalFontSettings? LoadFont()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var family = key?.GetValue("FontFamily") as string;

        if (string.IsNullOrWhiteSpace(family))
            return null;

        var size = key?.GetValue("FontSize") is string sizeText &&
            double.TryParse(
                sizeText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedSize)
            ? parsedSize
            : 14;

        return new TerminalFontSettings(
            family,
            Math.Clamp(size, 8, 72),
            key?.GetValue("FontBold") is int bold && bold == 1,
            key?.GetValue("FontItalic") is int italic && italic == 1);
    }

    public static void SaveFont(TerminalFontSettings settings)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("FontFamily", settings.Family);
        key.SetValue(
            "FontSize",
            settings.Size.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        key.SetValue(
            "FontBold",
            settings.Bold ? 1 : 0,
            RegistryValueKind.DWord);
        key.SetValue(
            "FontItalic",
            settings.Italic ? 1 : 0,
            RegistryValueKind.DWord);
    }

    public static Dictionary<string, System.Windows.Media.Color> LoadCustomColors(AppThemeKind theme)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var colors = new Dictionary<string, System.Windows.Media.Color>();

        if (key?.GetValue($"CustomColors{theme}") is not string[] entries)
            return colors;

        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=');

            if (separator > 0 && TryParseColor(entry[(separator + 1)..], out var color))
                colors[entry[..separator]] = color;
        }

        return colors;
    }

    public static void SaveCustomColors(AppThemeKind theme, IReadOnlyDictionary<string, System.Windows.Media.Color> colors)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);

        if (colors.Count == 0)
        {
            key.DeleteValue($"CustomColors{theme}", throwOnMissingValue: false);
            return;
        }

        key.SetValue(
            $"CustomColors{theme}",
            colors.Select(entry => $"{entry.Key}={FormatColor(entry.Value)}").ToArray(),
            RegistryValueKind.MultiString);
    }

    public static string FormatColor(System.Windows.Media.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static bool TryParseColor(string? text, out System.Windows.Media.Color color)
    {
        color = default;
        text = text?.Trim().TrimStart('#');

        if (text is not { Length: 6 } ||
            !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return false;
        }

        color = System.Windows.Media.Color.FromRgb(
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);
        return true;
    }

    // Folder for new tabs when none is requested; null keeps the folder
    // RtlTerminal was started in.
    public static string? LoadStartupDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("StartupDirectory") as string is { Length: > 0 } directory
            ? directory
            : null;
    }

    public static void SaveStartupDirectory(string? directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);

        if (string.IsNullOrWhiteSpace(directory))
            key.DeleteValue("StartupDirectory", throwOnMissingValue: false);
        else
            key.SetValue("StartupDirectory", directory.Trim(), RegistryValueKind.String);
    }

    public static bool LoadShowStartupBanner() => LoadFlag("ShowStartupBanner", true);

    public static void SaveShowStartupBanner(bool show) => SaveFlag("ShowStartupBanner", show);

    public static bool LoadCheckUpdatesOnStartup() => LoadFlag("CheckUpdatesOnStartup", true);

    public static void SaveCheckUpdatesOnStartup(bool check) => SaveFlag("CheckUpdatesOnStartup", check);

    private static bool LoadFlag(string name, bool defaultValue)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(name) is int value ? value != 0 : defaultValue;
    }

    private static void SaveFlag(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}
