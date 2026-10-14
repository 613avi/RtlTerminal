using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RtlTerminal;

public enum SettingsPage
{
    General,
    Colors
}

// General options or the color editor. Color edits are previewed live and
// rolled back when the window is cancelled.
public sealed class SettingsWindow : Window
{
    private static readonly int[] CustomColors = new int[16];

    private readonly MainWindow _owner;
    private readonly AppThemeKind _originalTheme = AppTheme.Current;
    private readonly Dictionary<AppThemeKind, IReadOnlyDictionary<string, Color>> _originalColors;
    private readonly Dictionary<AppThemeKind, Dictionary<string, Color>> _colors;
    private readonly List<(ThemeColor Color, Border Swatch, TextBox Hex, Button Reset)> _rows = [];
    private readonly TextBox _startupDirectory = new() { Padding = new Thickness(6, 4, 6, 4), FlowDirection = FlowDirection.LeftToRight };
    private readonly CheckBox _showBanner = new() { Content = Ui.T("Show the developer's welcome banner when a tab opens"), Margin = new Thickness(0, 18, 0, 0) };
    private readonly CheckBox _checkUpdates = new() { Content = Ui.T("Check for updates on startup"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly ComboBox _theme = new() { Width = 140 };
    private readonly ComboBox _scheme = new() { Width = 170 };
    private readonly SettingsPage _page;
    private bool _loading;
    private bool _saved;

    public SettingsWindow(MainWindow owner, SettingsPage page = SettingsPage.General)
    {
        _owner = owner;
        _originalColors = Enum.GetValues<AppThemeKind>().ToDictionary(theme => theme, AppTheme.GetOverrides);
        _colors = _originalColors.ToDictionary(entry => entry.Key, entry => new Dictionary<string, Color>(entry.Value));
        Title = Ui.T(page == SettingsPage.Colors ? "Colors" : "Settings");
        Icon = owner.Icon;
        Width = 720;
        Height = 660;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(244, 245, 247));
        FlowDirection = Ui.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        _page = page;
        var content = page == SettingsPage.Colors ? CreateColorsPage() : CreateGeneralPage();
        if (page == SettingsPage.General)
            Height = 340;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(CreateButton("Cancel", (_, _) => Close(), isCancel: true));
        buttons.Children.Add(CreateButton("Apply", (_, _) => Save()));
        buttons.Children.Add(CreateButton("OK", (_, _) => { Save(); Close(); }, isDefault: true));

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(content);
        Content = root;
        Closed += (_, _) => RestoreIfCancelled();
    }

    private UIElement CreateGeneralPage()
    {
        _startupDirectory.Text = AppSettings.LoadStartupDirectory() ?? "";
        _showBanner.IsChecked = AppSettings.LoadShowStartupBanner();
        _checkUpdates.IsChecked = AppSettings.LoadCheckUpdatesOnStartup();

        var browse = CreateButton("Browse...", (_, _) => BrowseStartupDirectory());
        browse.Margin = new Thickness(8, 0, 0, 0);
        var folder = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folder.Children.Add(browse);
        folder.Children.Add(_startupDirectory);

        var page = new StackPanel { Margin = new Thickness(14) };
        page.Children.Add(new TextBlock { Text = Ui.T("Startup folder for new tabs"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        page.Children.Add(folder);
        page.Children.Add(Hint("Leave empty to start in the folder RtlTerminal was opened in. Environment variables such as %USERPROFILE% are supported."));
        page.Children.Add(_showBanner);
        page.Children.Add(_checkUpdates);
        page.Children.Add(Hint("Changes to the banner and the folder apply to new tabs."));
        return page;
    }

    private UIElement CreateColorsPage()
    {
        _theme.Items.Add(new ComboBoxItem { Content = Ui.T("Dark"), Tag = AppThemeKind.Dark });
        _theme.Items.Add(new ComboBoxItem { Content = Ui.T("Light"), Tag = AppThemeKind.Light });
        _theme.SelectedIndex = AppTheme.IsLight ? 1 : 0;
        _theme.SelectionChanged += (_, _) => { Preview(); RefreshRows(); };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var reset = CreateButton("Reset all colors", (_, _) => { _colors[SelectedTheme].Clear(); Preview(); RefreshRows(); });
        DockPanel.SetDock(reset, Dock.Right);
        header.Children.Add(reset);
        var themePicker = new StackPanel { Orientation = Orientation.Horizontal };
        themePicker.Children.Add(new TextBlock { Text = Ui.T("Theme"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        themePicker.Children.Add(_theme);
        themePicker.Children.Add(new TextBlock { Text = Ui.T("Color scheme"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        themePicker.Children.Add(_scheme);
        header.Children.Add(themePicker);

        _scheme.Items.Add(new ComboBoxItem { Content = Ui.T("Choose a scheme..."), IsEnabled = false });
        _scheme.Items.Add(new ComboBoxItem { Content = Ui.T("Default colors"), Tag = "default" });
        foreach (var scheme in ColorScheme.All)
            _scheme.Items.Add(new ComboBoxItem { Content = scheme.Name, Tag = scheme });
        _scheme.SelectedIndex = 0;
        _scheme.SelectionChanged += (_, _) => ApplyScheme();

        var list = new StackPanel();
        foreach (var group in AppTheme.Catalog.GroupBy(color => color.Group))
        {
            list.Children.Add(new TextBlock
            {
                Text = Ui.T(group.Key),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 6)
            });
            foreach (var color in group)
                list.Children.Add(CreateColorRow(color));
        }

        var page = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);
        var hint = Hint("Programs that pick exact RGB colors are not affected by the scheme. In Claude Code, choose a theme ending in \"(ANSI colors only)\" with /theme so it follows these colors.");
        hint.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(hint, Dock.Top);
        page.Children.Add(hint);
        page.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        RefreshRows();
        return page;
    }

    private UIElement CreateColorRow(ThemeColor color)
    {
        var swatch = new Border
        {
            Width = 40,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(140, 146, 156)),
            Cursor = Cursors.Hand,
            ToolTip = Ui.T("Pick a color")
        };
        var hex = new TextBox { Width = 84, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(4, 2, 4, 2), FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Consolas") };
        var reset = new Button { Content = Ui.T("Reset"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            if (PickColor(AppTheme.Get(SelectedTheme, color.Key)) is { } picked)
                SetColor(color, picked);
        };
        hex.LostFocus += (_, _) => CommitHex(color, hex);
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(color, hex); };
        reset.Click += (_, _) => { _colors[SelectedTheme].Remove(color.Key); Preview(); RefreshRows(); };

        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var element in new FrameworkElement[] { swatch, hex })
        {
            DockPanel.SetDock(element, Dock.Left);
            row.Children.Add(element);
        }
        DockPanel.SetDock(reset, Dock.Right);
        row.Children.Add(reset);
        row.Children.Add(new TextBlock { Text = Ui.T(color.Label), VerticalAlignment = VerticalAlignment.Center });
        _rows.Add((color, swatch, hex, reset));
        return row;
    }

    private void ApplyScheme()
    {
        if ((_scheme.SelectedItem as ComboBoxItem)?.Tag is not { } tag)
            return;

        if (tag is ColorScheme scheme)
        {
            _theme.SelectedIndex = scheme.Theme == AppThemeKind.Light ? 1 : 0;
            scheme.ApplyTo(_colors[SelectedTheme]);
        }
        else
        {
            ColorScheme.ResetTerminalColors(_colors[SelectedTheme]);
        }

        Preview();
        RefreshRows();
        Dispatcher.BeginInvoke(() => _scheme.SelectedIndex = 0);
    }

    private AppThemeKind SelectedTheme =>
        (_theme.SelectedItem as ComboBoxItem)?.Tag is AppThemeKind theme ? theme : AppTheme.Current;

    private void CommitHex(ThemeColor color, TextBox hex)
    {
        if (_loading)
            return;

        if (AppSettings.TryParseColor(hex.Text, out var parsed))
            SetColor(color, parsed);
        else
            RefreshRows();
    }

    private void SetColor(ThemeColor color, Color value)
    {
        if (value == color.Default(SelectedTheme))
            _colors[SelectedTheme].Remove(color.Key);
        else
            _colors[SelectedTheme][color.Key] = value;

        Preview();
        RefreshRows();
    }

    private void RefreshRows()
    {
        _loading = true;
        foreach (var (color, swatch, hex, reset) in _rows)
        {
            var value = _colors[SelectedTheme].TryGetValue(color.Key, out var custom) ? custom : color.Default(SelectedTheme);
            swatch.Background = new SolidColorBrush(value);
            hex.Text = AppSettings.FormatColor(value);
            reset.Visibility = _colors[SelectedTheme].ContainsKey(color.Key) ? Visibility.Visible : Visibility.Hidden;
        }
        _loading = false;
    }

    private void Preview()
    {
        foreach (var (theme, colors) in _colors)
            AppTheme.SetOverrides(theme, colors);
        _owner.ApplyTheme(SelectedTheme);
    }

    private void Save()
    {
        CommitFocusedHex();

        if (_page == SettingsPage.Colors)
        {
            foreach (var (theme, colors) in _colors)
                AppSettings.SaveCustomColors(theme, colors);
            AppSettings.SaveTheme(SelectedTheme);
            Preview();
            _saved = true;
            return;
        }

        var directory = Environment.ExpandEnvironmentVariables(_startupDirectory.Text.Trim());

        if (directory.Length > 0 && !Directory.Exists(directory))
        {
            MessageBox.Show(this, Ui.T("The folder does not exist."), Ui.T("Settings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppSettings.SaveStartupDirectory(_startupDirectory.Text);
        AppSettings.SaveShowStartupBanner(_showBanner.IsChecked == true);
        AppSettings.SaveCheckUpdatesOnStartup(_checkUpdates.IsChecked == true);
        _saved = true;
    }

    private void CommitFocusedHex()
    {
        foreach (var (color, _, hex, _) in _rows.Where(row => row.Hex.IsKeyboardFocused))
            CommitHex(color, hex);
    }

    private void RestoreIfCancelled()
    {
        if (_saved || _page != SettingsPage.Colors)
            return;

        foreach (var (theme, colors) in _originalColors)
            AppTheme.SetOverrides(theme, colors);
        _owner.ApplyTheme(_originalTheme);
    }

    private void BrowseStartupDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Ui.T("Choose the startup folder") };
        var current = Environment.ExpandEnvironmentVariables(_startupDirectory.Text.Trim());

        if (Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true)
            _startupDirectory.Text = dialog.FolderName;
    }

    private Color? PickColor(Color initial)
    {
        var custom = Marshal.AllocHGlobal(sizeof(int) * CustomColors.Length);
        try
        {
            Marshal.Copy(CustomColors, 0, custom, CustomColors.Length);
            var options = new ChooseColorOptions
            {
                StructSize = Marshal.SizeOf<ChooseColorOptions>(),
                Owner = new WindowInteropHelper(this).Handle,
                Result = initial.R | initial.G << 8 | initial.B << 16,
                CustomColors = custom,
                Flags = ColorDialogRgbInit | ColorDialogFullOpen
            };

            if (!ChooseColor(ref options))
                return null;

            Marshal.Copy(custom, CustomColors, 0, CustomColors.Length);
            return Color.FromRgb((byte)options.Result, (byte)(options.Result >> 8), (byte)(options.Result >> 16));
        }
        finally
        {
            Marshal.FreeHGlobal(custom);
        }
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = Ui.T(text),
        Foreground = new SolidColorBrush(Color.FromRgb(105, 115, 134)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private static Button CreateButton(string text, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = Ui.T(text),
            MinWidth = 90,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        button.Click += click;
        return button;
    }

    private const int ColorDialogRgbInit = 0x1;
    private const int ColorDialogFullOpen = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorOptions
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public int Result;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref ChooseColorOptions options);
}
