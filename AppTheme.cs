using System.Windows;
using System.Windows.Media;

namespace RtlTerminal;

public enum AppThemeKind
{
    Dark,
    Light
}

// One customizable color. Window colors are exposed to XAML as "{Key}Brush".
public sealed record ThemeColor(string Key, string Group, string Label, Color Dark, Color Light)
{
    public Color Default(AppThemeKind theme) => theme == AppThemeKind.Light ? Light : Dark;
}

public static class AppTheme
{
    // In the light theme applications still pick colors for a dark screen,
    // so explicit backgrounds default to light tints that keep text readable.
    public static readonly IReadOnlyList<ThemeColor> Catalog =
    [
        new("TerminalBackground", "Terminal", "Background", Rgb(12, 12, 12), Rgb(255, 255, 255)),
        new("TerminalForeground", "Terminal", "Text", Rgb(230, 230, 230), Rgb(36, 41, 47)),
        new("Cursor", "Terminal", "Cursor", Rgb(230, 230, 230), Rgb(36, 41, 47)),
        new("CursorBorder", "Terminal", "Cursor border", Rgb(211, 211, 211), Rgb(110, 119, 129)),
        new("Selection", "Terminal", "Selection", Rgb(70, 125, 200), Rgb(70, 125, 200)),
        new("AnsiText0", "Text colors", "Black", Rgb(12, 12, 12), Rgb(36, 41, 47)),
        new("AnsiText1", "Text colors", "Red", Rgb(197, 15, 31), Rgb(207, 34, 46)),
        new("AnsiText2", "Text colors", "Green", Rgb(19, 161, 14), Rgb(17, 99, 41)),
        new("AnsiText3", "Text colors", "Yellow", Rgb(193, 156, 0), Rgb(125, 78, 0)),
        new("AnsiText4", "Text colors", "Blue", Rgb(0, 55, 218), Rgb(5, 80, 174)),
        new("AnsiText5", "Text colors", "Magenta", Rgb(136, 23, 152), Rgb(130, 80, 223)),
        new("AnsiText6", "Text colors", "Cyan", Rgb(58, 150, 221), Rgb(27, 124, 131)),
        new("AnsiText7", "Text colors", "White", Rgb(204, 204, 204), Rgb(66, 74, 83)),
        new("AnsiText8", "Text colors", "Bright black", Rgb(118, 118, 118), Rgb(110, 119, 129)),
        new("AnsiText9", "Text colors", "Bright red", Rgb(231, 72, 86), Rgb(164, 14, 38)),
        new("AnsiText10", "Text colors", "Bright green", Rgb(22, 198, 12), Rgb(26, 127, 55)),
        new("AnsiText11", "Text colors", "Bright yellow", Rgb(249, 241, 165), Rgb(99, 60, 1)),
        new("AnsiText12", "Text colors", "Bright blue", Rgb(59, 120, 255), Rgb(9, 105, 218)),
        new("AnsiText13", "Text colors", "Bright magenta", Rgb(180, 0, 158), Rgb(130, 80, 223)),
        new("AnsiText14", "Text colors", "Bright cyan", Rgb(97, 214, 214), Rgb(49, 146, 170)),
        new("AnsiText15", "Text colors", "Bright white", Rgb(242, 242, 242), Rgb(13, 17, 23)),
        new("AnsiBackground0", "Background colors", "Black", Rgb(12, 12, 12), Rgb(246, 248, 250)),
        new("AnsiBackground1", "Background colors", "Red", Rgb(197, 15, 31), Rgb(255, 235, 233)),
        new("AnsiBackground2", "Background colors", "Green", Rgb(19, 161, 14), Rgb(218, 251, 225)),
        new("AnsiBackground3", "Background colors", "Yellow", Rgb(193, 156, 0), Rgb(255, 248, 197)),
        new("AnsiBackground4", "Background colors", "Blue", Rgb(0, 55, 218), Rgb(221, 244, 255)),
        new("AnsiBackground5", "Background colors", "Magenta", Rgb(136, 23, 152), Rgb(251, 239, 255)),
        new("AnsiBackground6", "Background colors", "Cyan", Rgb(58, 150, 221), Rgb(216, 243, 246)),
        new("AnsiBackground7", "Background colors", "White", Rgb(204, 204, 204), Rgb(208, 215, 222)),
        new("AnsiBackground8", "Background colors", "Bright black", Rgb(118, 118, 118), Rgb(225, 230, 235)),
        new("AnsiBackground9", "Background colors", "Bright red", Rgb(231, 72, 86), Rgb(255, 206, 203)),
        new("AnsiBackground10", "Background colors", "Bright green", Rgb(22, 198, 12), Rgb(172, 238, 187)),
        new("AnsiBackground11", "Background colors", "Bright yellow", Rgb(249, 241, 165), Rgb(255, 236, 153)),
        new("AnsiBackground12", "Background colors", "Bright blue", Rgb(59, 120, 255), Rgb(182, 227, 255)),
        new("AnsiBackground13", "Background colors", "Bright magenta", Rgb(180, 0, 158), Rgb(236, 215, 255)),
        new("AnsiBackground14", "Background colors", "Bright cyan", Rgb(97, 214, 214), Rgb(179, 233, 239)),
        new("AnsiBackground15", "Background colors", "Bright white", Rgb(242, 242, 242), Rgb(234, 238, 242)),
        new("TitleBar", "Window", "Title bar", Rgb(32, 33, 36), Rgb(222, 226, 231)),
        new("Toolbar", "Window", "Menu bar and active tab", Rgb(48, 49, 52), Rgb(246, 248, 250)),
        new("ToolbarBorder", "Window", "Menu bar border", Rgb(60, 64, 67), Rgb(208, 215, 222)),
        new("ChromeForeground", "Window", "Window text", Rgb(240, 240, 240), Rgb(31, 35, 40)),
        new("ChromeStrongForeground", "Window", "Strong window text", Rgb(255, 255, 255), Rgb(0, 0, 0)),
        new("MutedForeground", "Window", "Muted window text", Rgb(139, 148, 158), Rgb(87, 96, 106)),
        new("Accent", "Window", "Accent", Rgb(114, 214, 197), Rgb(15, 118, 110)),
        new("Focus", "Window", "Keyboard focus", Rgb(168, 199, 250), Rgb(11, 87, 208)),
        new("MenuBackground", "Window", "Menu background", Rgb(37, 37, 38), Rgb(255, 255, 255)),
        new("MenuBorder", "Window", "Menu border", Rgb(63, 63, 70), Rgb(208, 215, 222)),
        new("MenuHighlight", "Window", "Menu highlight", Rgb(58, 58, 58), Rgb(234, 238, 242)),
        new("MenuOpen", "Window", "Open menu", Rgb(65, 65, 65), Rgb(220, 225, 230)),
        new("Separator", "Window", "Separators", Rgb(69, 70, 74), Rgb(216, 222, 228)),
        new("CaptionHover", "Window", "Window button hover", Rgb(64, 64, 64), Rgb(205, 210, 216)),
        new("CaptionPressed", "Window", "Window button pressed", Rgb(80, 80, 80), Rgb(190, 196, 203)),
        new("ButtonPressed", "Window", "Button pressed", Rgb(70, 70, 70), Rgb(208, 215, 222)),
        new("RoundButtonHover", "Window", "Round button hover", Rgb(64, 65, 69), Rgb(205, 210, 216)),
        new("RoundButtonPressed", "Window", "Round button pressed", Rgb(81, 82, 86), Rgb(190, 196, 203)),
        new("RoundButtonFocus", "Window", "Round button focus", Rgb(69, 79, 96), Rgb(196, 214, 240)),
        new("TabAccent", "Window", "Active tab icon", Rgb(114, 214, 197), Rgb(15, 118, 110)),
        new("TabIcon", "Window", "Tab icon", Rgb(139, 148, 158), Rgb(101, 109, 118)),
        new("ActiveTabText", "Window", "Active tab text", Rgb(240, 244, 248), Rgb(31, 35, 40)),
        new("InactiveTabText", "Window", "Inactive tab text", Rgb(165, 174, 184), Rgb(87, 96, 106)),
        new("ActiveTab", "Window", "Active tab", Rgb(48, 49, 52), Rgb(246, 248, 250)),
        new("HoverTab", "Window", "Tab hover", Rgb(42, 43, 47), Rgb(215, 220, 226)),
        new("TabSeparator", "Window", "Tab separator", Rgb(78, 80, 84), Rgb(175, 184, 193)),
        new("ScrollTrack", "Window", "Scrollbar track", Rgb(17, 17, 17), Rgb(246, 248, 250)),
        new("ScrollThumb", "Window", "Scrollbar thumb", Rgb(85, 85, 85), Rgb(190, 196, 203)),
        new("ScrollThumbHover", "Window", "Scrollbar thumb hover", Rgb(136, 136, 136), Rgb(150, 158, 167)),
        new("ScrollThumbPressed", "Window", "Scrollbar thumb pressed", Rgb(170, 170, 170), Rgb(110, 119, 129)),
    ];

    private static readonly Dictionary<string, ThemeColor> ColorsByKey =
        Catalog.ToDictionary(color => color.Key);

    private static readonly Dictionary<AppThemeKind, Dictionary<string, Color>> Overrides = new()
    {
        [AppThemeKind.Dark] = [],
        [AppThemeKind.Light] = []
    };

    private static readonly Dictionary<TerminalColor, int> PaletteIndexes =
        TerminalBuffer.AnsiColors
            .Select((color, index) => (color, index))
            .GroupBy(entry => entry.color)
            .ToDictionary(group => group.Key, group => group.First().index);

    // Neutral grays of the 256-color palette: the 24-step ramp (232-255)
    // and the gray diagonal of the color cube. On a light screen their
    // lightness is inverted so every step stays distinct and readable.
    private static readonly HashSet<TerminalColor> PaletteGrays =
    [
        .. Enumerable.Range(232, 24).Select(TerminalBuffer.Get256Color),
        .. new[] { 16, 59, 102, 145, 188, 231 }.Select(TerminalBuffer.Get256Color)
    ];

    private static TerminalColor[] _foregrounds = TerminalBuffer.AnsiColors.ToArray();
    private static TerminalColor[] _backgrounds = TerminalBuffer.AnsiColors.ToArray();

    public static AppThemeKind Current { get; private set; } = AppThemeKind.Dark;

    public static bool IsLight => Current == AppThemeKind.Light;

    public static Color Get(string key) => Get(Current, key);

    public static Color Get(AppThemeKind theme, string key) =>
        Overrides[theme].TryGetValue(key, out var color) ? color : ColorsByKey[key].Default(theme);

    public static IReadOnlyDictionary<string, Color> GetOverrides(AppThemeKind theme) =>
        new Dictionary<string, Color>(Overrides[theme]);

    // Replaces the custom colors of a theme. Call Apply to show them.
    public static void SetOverrides(AppThemeKind theme, IReadOnlyDictionary<string, Color> colors)
    {
        Overrides[theme] = colors
            .Where(entry => ColorsByKey.TryGetValue(entry.Key, out var color) && color.Default(theme) != entry.Value)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    public static TerminalColor DefaultForeground => Terminal(Get("TerminalForeground"));

    public static TerminalColor DefaultBackground => Terminal(Get("TerminalBackground"));

    public static TerminalColor MapForeground(TerminalColor color) =>
        PaletteIndexes.TryGetValue(color, out var index) ? _foregrounds[index]
        : IsLight && PaletteGrays.Contains(color) ? InvertGray(color)
        : color;

    public static TerminalColor MapBackground(TerminalColor color) =>
        PaletteIndexes.TryGetValue(color, out var index) ? _backgrounds[index]
        : IsLight && PaletteGrays.Contains(color) ? InvertGray(color)
        : color;

    public static Color TabAccent => Get("TabAccent");
    public static Color TabIcon => Get("TabIcon");
    public static Color ActiveTabText => Get("ActiveTabText");
    public static Color InactiveTabText => Get("InactiveTabText");
    public static Color ActiveTab => Get("ActiveTab");
    public static Color HoverTab => Get("HoverTab");
    public static Color TabSeparator => Get("TabSeparator");
    public static Color CursorFill => WithAlpha(Get("Cursor"), 90);
    public static Color SelectionFill => WithAlpha(Get("Selection"), 100);
    public static Color ScrollTrack => Get("ScrollTrack");
    public static Color ScrollThumb => Get("ScrollThumb");
    public static Color ScrollThumbHover => Get("ScrollThumbHover");
    public static Color ScrollThumbPressed => Get("ScrollThumbPressed");
    public static Color CursorBorder => Get("CursorBorder");

    public static void Apply(AppThemeKind theme)
    {
        Current = theme;
        _foregrounds = Enumerable.Range(0, 16).Select(index => Terminal(Get($"AnsiText{index}"))).ToArray();
        _backgrounds = Enumerable.Range(0, 16).Select(index => Terminal(Get($"AnsiBackground{index}"))).ToArray();
        TerminalBuffer.DefaultForeground = DefaultForeground;
        TerminalBuffer.DefaultBackground = DefaultBackground;
        var resources = Application.Current.Resources;

        foreach (var color in Catalog.Where(color => color.Group is "Window" || color.Key.StartsWith("Terminal", StringComparison.Ordinal)))
            resources[color.Key + "Brush"] = Frozen(Get(color.Key));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TerminalColor Terminal(Color color) => new(color.R, color.G, color.B);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static TerminalColor InvertGray(TerminalColor color) =>
        new((byte)(255 - color.Red), (byte)(255 - color.Red), (byte)(255 - color.Red));

    private static Color Rgb(byte red, byte green, byte blue) =>
        Color.FromRgb(red, green, blue);
}
