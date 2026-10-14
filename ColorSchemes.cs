using System.Windows.Media;

namespace RtlTerminal;

// Ready-made terminal palettes (values from Windows Terminal). Choosing one
// writes its colors into the custom colors of the matching theme.
public sealed record ColorScheme(string Name, AppThemeKind Theme, string Background, string Foreground, string[] Ansi)
{
    public static readonly IReadOnlyList<ColorScheme> All =
    [
        new("Campbell", AppThemeKind.Dark, "0C0C0C", "CCCCCC",
            ["0C0C0C", "C50F1F", "13A10E", "C19C00", "0037DA", "881798", "3A96DD", "CCCCCC",
             "767676", "E74856", "16C60C", "F9F1A5", "3B78FF", "B4009E", "61D6D6", "F2F2F2"]),
        new("One Half Dark", AppThemeKind.Dark, "282C34", "DCDFE4",
            ["282C34", "E06C75", "98C379", "E5C07B", "61AFEF", "C678DD", "56B6C2", "DCDFE4",
             "5A6374", "E06C75", "98C379", "E5C07B", "61AFEF", "C678DD", "56B6C2", "DCDFE4"]),
        new("Solarized Dark", AppThemeKind.Dark, "002B36", "839496",
            ["002B36", "DC322F", "859900", "B58900", "268BD2", "D33682", "2AA198", "EEE8D5",
             "073642", "CB4B16", "586E75", "657B83", "839496", "6C71C4", "93A1A1", "FDF6E3"]),
        new("Tango Dark", AppThemeKind.Dark, "000000", "D3D7CF",
            ["000000", "CC0000", "4E9A06", "C4A000", "3465A4", "75507B", "06989A", "D3D7CF",
             "555753", "EF2929", "8AE234", "FCE94F", "729FCF", "AD7FA8", "34E2E2", "EEEEEC"]),
        new("Dracula", AppThemeKind.Dark, "282A36", "F8F8F2",
            ["21222C", "FF5555", "50FA7B", "F1FA8C", "BD93F9", "FF79C6", "8BE9FD", "F8F8F2",
             "6272A4", "FF6E6E", "69FF94", "FFFFA5", "D6ACFF", "FF92DF", "A4FFFF", "FFFFFF"]),
        new("One Half Light", AppThemeKind.Light, "FAFAFA", "383A42",
            ["383A42", "E45649", "50A14F", "C18301", "0184BC", "A626A4", "0997B3", "A0A1A7",
             "4F525D", "DF6C75", "98C379", "E4C07A", "61AFEF", "C577DD", "56B5C1", "202227"]),
        new("Solarized Light", AppThemeKind.Light, "FDF6E3", "657B83",
            ["073642", "DC322F", "859900", "B58900", "268BD2", "D33682", "2AA198", "93A1A1",
             "002B36", "CB4B16", "586E75", "657B83", "839496", "6C71C4", "93A1A1", "002B36"]),
        new("Tango Light", AppThemeKind.Light, "FFFFFF", "555753",
            ["000000", "CC0000", "4E9A06", "C4A000", "3465A4", "75507B", "06989A", "888A85",
             "555753", "EF2929", "4E9A06", "C4A000", "729FCF", "AD7FA8", "06989A", "000000"]),
    ];

    public static IEnumerable<string> Keys =>
        new[] { "TerminalBackground", "TerminalForeground", "Cursor", "CursorBorder" }
            .Concat(Enumerable.Range(0, 16).SelectMany(index => new[] { $"AnsiText{index}", $"AnsiBackground{index}" }));

    public Dictionary<string, Color> Colors()
    {
        var background = Parse(Background);
        var foreground = Parse(Foreground);
        var colors = new Dictionary<string, Color>
        {
            ["TerminalBackground"] = background,
            ["TerminalForeground"] = foreground,
            ["Cursor"] = foreground,
            ["CursorBorder"] = foreground
        };

        for (var index = 0; index < 16; index++)
        {
            var color = Parse(Ansi[index]);
            colors[$"AnsiText{index}"] = color;
            // On a light screen, explicit backgrounds become tints so the
            // text drawn over them (picked for a dark screen) stays readable.
            colors[$"AnsiBackground{index}"] = Theme == AppThemeKind.Light ? Blend(color, background, .2) : color;
        }

        return colors;
    }

    private static Color Parse(string hex) =>
        Color.FromRgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));

    private static Color Blend(Color color, Color background, double amount) =>
        Color.FromRgb(
            (byte)(color.R * amount + background.R * (1 - amount)),
            (byte)(color.G * amount + background.G * (1 - amount)),
            (byte)(color.B * amount + background.B * (1 - amount)));
}
