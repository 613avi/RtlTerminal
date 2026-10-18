using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RtlTerminal;

public partial class MainWindow : Window
{
    private readonly object _renderLock = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly List<TerminalTab> _tabs = [];
    private readonly List<string> _temporaryClipboardFiles = [];
    private TerminalTab? _activeTab;
    private bool _renderStartQueued;
    private bool _updatingContextMenuItem;
    private double _cellWidth = 8.5;
    private double _lineHeight = 18;
    private bool _restoringScrollPosition;
    private int _nextTabNumber = 1;
    private TerminalProfile _defaultProfile = TerminalProfile.CommandPrompt;
    private int _historySize = 2000;
    private bool _updateCheckInProgress;
    private bool _suppressRightMouseUp;
    private Key? _pendingContextMenuKey;
    private (int X, int Y, int Button)? _lastReportedMouseCell;

    public MainWindow()
    {
        InitializeComponent();
        WireTerminalView(TerminalTextBox);
        SmartRtlMenuItem.IsChecked = true;
        BuildSchemeMenus();
        ApplyLanguage();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += RenderTimer_Tick;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyViewportResize(); };
        _defaultProfile = LoadDefaultProfile();
        _historySize = AppSettings.LoadHistorySize();
ApplySavedFontSettings();
        UpdateThemeMenu();
        UpdateFontMetrics();
        AllowDrop = true;
        PreviewDragOver += Window_PreviewDragOver;
        PreviewDrop += Window_PreviewDrop;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ActiveSession is not null && IsSupportedDrop(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (FindAncestor<TerminalView>(e.OriginalSource as DependencyObject) is { Tag: TerminalPane pane })
            ActivatePane(pane);

        if (ActiveSession is null)
            return;

        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                WriteClipboardPaths(files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList());
            }
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
                e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                WriteClipboardPaths([SaveTemporaryImage(bitmap)]);
            }
            else if (e.Data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            {
                WritePastedText(text.Replace("\r\n", "\r").Replace("\n", "\r"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, exception.Message, Ui.T("Drop"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Activate();
        ActiveView.Focus();
    }

    private static bool IsSupportedDrop(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent(DataFormats.Bitmap) ||
        data.GetDataPresent(DataFormats.UnicodeText);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshContextMenuIntegrationState();
        PromptForContextMenuIntegration();
        ActiveView.Focus();
        ActiveView.UpdateLayout();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => CreateTerminalTab(_defaultProfile));
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void CreateTerminalTab(
        TerminalProfile profile,
        string? requestedStartupDirectory = null)
    {
        var pane = CreatePane(profile);
        var tab = new TerminalTab(pane);
        _tabs.Add(tab);
        ShowTab(tab);
        StartPane(pane, requestedStartupDirectory);
        ActiveView.Focus();
    }

    // Opens a new terminal to the right of the active pane, in the same tab.
    private void SplitActivePane()
    {
        if (_activeTab is null || ActivePane is not { } current)
        {
            CreateTerminalTab(_defaultProfile);
            return;
        }

        StorePaneWidths();
        var pane = CreatePane(current.Profile);
        pane.Width = current.Width /= 2;
        _activeTab.Panes.Insert(_activeTab.Panes.IndexOf(current) + 1, pane);
        _activeTab.ActivePane = pane;
        LayoutPanes();
        RebuildTabStrip();
        StartPane(pane, null);
        QueueViewportResize();
        ActiveView.Focus();
    }

    private TerminalPane CreatePane(TerminalProfile profile)
    {
        var pane = new TerminalPane(_nextTabNumber++, profile, GetProfileTitle(profile));
        var inUse = _tabs.SelectMany(tab => tab.Panes).Any(other => ReferenceEquals(other.View, TerminalTextBox));
        pane.View = inUse ? CreateTerminalView() : TerminalTextBox;
        pane.View.Clear();
        pane.View.Tag = pane;
        return pane;
    }

    private void StartPane(TerminalPane pane, string? requestedStartupDirectory)
    {
        var startupDirectory = ResolveStartupDirectory(
            requestedStartupDirectory);
        PaneHost.UpdateLayout();

        try
        {
            var columns = GetColumns(pane.View);
            var rows = GetRows(pane.View);
            pane.Buffer = new TerminalBuffer(
                columns,
                rows,
                _historySize);
            pane.GridSize = (columns, rows);
            pane.CancellationTokenSource = new CancellationTokenSource();
            pane.Session = new ConPtySession(columns, rows);
            pane.Session.Start(
                GetProfileCommand(pane.Profile),
                startupDirectory);

            if (pane.Profile == TerminalProfile.CommandPrompt &&
                startupDirectory is not null)
            {
                AppSettings.RememberCmdDirectory(startupDirectory);
            }

            _ = Task.Run(() => ReadOutputLoop(pane));
        }
        catch (Exception exception)
        {
            var errorBuffer = new TerminalBuffer(GetColumns(pane.View), GetRows(pane.View));
            Render(pane, errorBuffer.Process("خطا در اجرای ConPTY:\r\n" + exception.Message));
        }
    }

    // The XAML view is the template: new panes copy its font and get the same handlers.
    private TerminalView CreateTerminalView()
    {
        var view = new TerminalView
        {
            BorderThickness = TerminalTextBox.BorderThickness,
            Padding = TerminalTextBox.Padding,
            FontFamily = TerminalTextBox.FontFamily,
            FontSize = TerminalTextBox.FontSize,
            FontWeight = TerminalTextBox.FontWeight,
            FontStyle = TerminalTextBox.FontStyle,
            Cursor = Cursors.IBeam,
            FlowDirection = FlowDirection.LeftToRight
        };
        view.SetResourceReference(BackgroundProperty, "TerminalBackgroundBrush");
        view.SetResourceReference(ForegroundProperty, "TerminalForegroundBrush");
        TextOptions.SetTextFormattingMode(view, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(view, TextRenderingMode.ClearType);
        System.Windows.Documents.Typography.SetStandardLigatures(view, false);
        System.Windows.Documents.Typography.SetContextualLigatures(view, false);
        System.Windows.Documents.Typography.SetDiscretionaryLigatures(view, false);
        System.Windows.Documents.Typography.SetKerning(view, false);
        System.Windows.Documents.Typography.SetNumeralAlignment(view, FontNumeralAlignment.Tabular);
        WireTerminalView(view);
        return view;
    }

    private void WireTerminalView(TerminalView view)
    {
        view.LinkRequested += uri =>
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception exception)
            { MessageBox.Show(this, exception.Message, Ui.T("Open link"), MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        view.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(TerminalTextBox_ScrollChanged));
        view.PreviewKeyDown += TerminalTextBox_PreviewKeyDown;
        view.PreviewKeyUp += TerminalTextBox_PreviewKeyUp;
        view.PreviewMouseDown += TerminalTextBox_PreviewMouseDown;
        view.PreviewMouseUp += TerminalTextBox_PreviewMouseUp;
        view.PreviewMouseMove += TerminalTextBox_PreviewMouseMove;
        view.PreviewMouseWheel += TerminalTextBox_PreviewMouseWheel;
        view.PreviewMouseRightButtonDown += TerminalTextBox_PreviewMouseRightButtonDown;
        view.GotKeyboardFocus += TerminalTextBox_GotKeyboardFocus;
        view.LostKeyboardFocus += TerminalTextBox_LostKeyboardFocus;
        view.PreviewTextInput += TerminalTextBox_PreviewTextInput;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        foreach (var pane in _tabs.SelectMany(tab => tab.Panes))
            _ = pane.DisposeAsync();

        CleanupTemporaryClipboardFiles();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => WindowWorkArea.Attach(this);

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M2.5,0.5 L9.5,0.5 9.5,7.5 M0.5,2.5 L7.5,2.5 7.5,9.5 0.5,9.5 Z"
            : "M0.5,0.5 L9.5,0.5 9.5,9.5 0.5,9.5 Z");

        QueueViewportResize();
    }

    private void QueueViewportResize()
    {
        if (_resizeTimer is null) return;
        _resizeTimer.Stop();
        if (WindowState != WindowState.Minimized) _resizeTimer.Start();
    }

    private void ApplyViewportResize()
    {
        if (WindowState == WindowState.Minimized || _activeTab is null) return;

        foreach (var pane in _activeTab.Panes)
        {
            if (pane.Session is not { } session || pane.Buffer is not { } buffer ||
                pane.View.ViewportWidth <= 0 || pane.View.ViewportHeight <= 0) continue;
            var columns = GetColumns(pane.View);
            var rows = GetRows(pane.View);
            if (pane.GridSize == (columns, rows)) continue;
            // Use the settled viewport, not transient window sizes during minimize/restore.
            var snapshot = buffer.Resize(columns, rows);
            session.Resize(columns, rows);
            pane.GridSize = (columns, rows);
            QueueRender(pane, snapshot);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (controlPressed && shiftPressed && e.Key == Key.T)
        {
            CreateTerminalTab(_defaultProfile);
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.Tab)
        {
            SelectRelativeTab(shiftPressed ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (controlPressed && shiftPressed && e.Key == Key.D)
        {
            SplitActivePane();
            e.Handled = true;
            return;
        }

        if (controlPressed && GetEffectiveKey(e) is Key.Left or Key.Right &&
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            SelectRelativePane(GetEffectiveKey(e) == Key.Left ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.W)
        {
            ClosePane(ActivePane);
            e.Handled = true;
        }
    }

    private void SplitPaneButton_Click(object sender, RoutedEventArgs e) => SplitActivePane();

    private void SplitPaneMenuItem_Click(object sender, RoutedEventArgs e) => SplitActivePane();

    private void ClosePaneMenuItem_Click(object sender, RoutedEventArgs e) => ClosePane(ActivePane);

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        CreateTerminalTab(_defaultProfile);
    }

    private void ProfileMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenuStyle"),
            FlowDirection = MainMenu.FlowDirection
        };

        AddProfileMenuItem(menu, Ui.T("Command Prompt"), TerminalProfile.CommandPrompt);
        AddProfileMenuItem(menu, "PowerShell", TerminalProfile.PowerShell);

        if (IsWslAvailable())
            AddProfileMenuItem(menu, "WSL", TerminalProfile.Wsl);

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void AddProfileMenuItem(
        ItemsControl menu,
        string header,
        TerminalProfile profile)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => SelectTerminalProfile(profile);
        menu.Items.Add(item);
    }

    private void SelectTerminalProfile(TerminalProfile profile)
    {
        _defaultProfile = profile;
        AppSettings.SaveTerminalProfile(profile.ToString());
        CreateTerminalTab(profile);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExportSessionMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (ActiveBuffer is null)
        {
            MessageBox.Show(
                this,
                Ui.T("There is no active terminal session to export."),
                Ui.T("Export session"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Ui.T("Export terminal session"),
            Filter = Ui.T("Text files (*.txt)|*.txt|All files (*.*)|*.*"),
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"RtlTerminal-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var snapshot = ActiveBuffer.CaptureSnapshot();
            File.WriteAllText(
                dialog.FileName,
                CreateSessionText(snapshot),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            MessageBox.Show(
                this,
                Ui.T("The session could not be exported.") + Environment.NewLine + exception.Message,
                Ui.T("Export session"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string CreateSessionText(TerminalSnapshot snapshot)
    {
        var text = new StringBuilder();

        foreach (var line in snapshot.Lines)
        {
            foreach (var run in line.Runs)
                text.Append(run.Text);

            text.AppendLine();
        }

        return text.ToString();
    }

    private void LastDirectoriesMenuItem_SubmenuOpened(
        object sender,
        RoutedEventArgs e)
    {
        LastDirectoriesMenuItem.Items.Clear();

        foreach (var directory in AppSettings.LoadLastCmdDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            var item = new MenuItem
            {
                Header = new TextBlock
                {
                    Text = directory,
                    MaxWidth = 520,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FlowDirection = FlowDirection.LeftToRight
                },
                ToolTip = directory
            };
            item.Click += (_, _) => CreateTerminalTab(
                TerminalProfile.CommandPrompt,
                directory);
            LastDirectoriesMenuItem.Items.Add(item);
        }

        if (LastDirectoriesMenuItem.Items.Count == 0)
        {
            LastDirectoriesMenuItem.Items.Add(new MenuItem
            {
                Header = Ui.T("No recent directories"),
                IsEnabled = false
            });
        }
    }

    private void TerminalTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        if (ActiveSession is null || string.IsNullOrEmpty(e.Text))
            return;

        ActiveSession.Write(e.Text);
        e.Handled = true;
    }

    private void TerminalTextBox_ScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (sender is not TerminalView { Tag: TerminalPane pane } view)
            return;

        if ((e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) &&
            pane.Session is not null && pane.Buffer is not null)
        {
            QueueViewportResize();
        }

        if (_restoringScrollPosition || view.IsUpdatingScroll)
            return;

        if (Math.Abs(e.VerticalChange) < 0.01)
            return;

        pane.FollowOutput =
            e.ExtentHeight <= e.ViewportHeight ||
            e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
    }

    private void TerminalTextBox_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        ActivatePaneOf(sender);
        if (IsMouseTrackingEnabled() && !ActiveView.HasSelection)
            return;

        e.Handled = true;
        _suppressRightMouseUp = true;
        if (ActiveView.HasSelection) CopySelection();
        else PasteClipboard();
        ActiveView.Focus();
    }

    private ContextMenu CreateTerminalContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle"), PlacementTarget = ActiveView, FlowDirection = MainMenu.FlowDirection };
        var split = new MenuItem { Header = Ui.T("Split side by side"), InputGestureText = "Ctrl+Shift+D" };
        split.Click += SplitPaneMenuItem_Click;
        var closePane = new MenuItem { Header = Ui.T("Close pane"), InputGestureText = "Ctrl+W", IsEnabled = _activeTab?.Panes.Count > 1 };
        closePane.Click += ClosePaneMenuItem_Click;
        var movePane = new MenuItem { Header = Ui.T("Move pane to a new tab"), IsEnabled = _activeTab?.Panes.Count > 1 };
        movePane.Click += MovePaneMenuItem_Click;
        var copy = new MenuItem { Header = Ui.T("_Copy"), IsEnabled = ActiveView.HasSelection };
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuItem { Header = Ui.T("_Paste"), IsEnabled = ActiveSession is not null };
        paste.Click += (_, _) => PasteClipboard();
        var select = new MenuItem { Header = Ui.T("Select _all") };
        select.Click += (_, _) => ActiveView.SelectAll();
        menu.Items.Add(copy); menu.Items.Add(paste); menu.Items.Add(new Separator()); menu.Items.Add(select);
        menu.Items.Add(new Separator()); menu.Items.Add(split); menu.Items.Add(movePane); menu.Items.Add(closePane);
        return menu;
    }

    private void TerminalTextBox_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        ActivatePaneOf(sender);
        if (e.ChangedButton == MouseButton.Right && ActiveView.HasSelection) return;
        var button = GetMouseButtonCode(e.ChangedButton);

        if (button < 0 || !SendMouseEvent(e, button, released: false))
            return;

        _lastReportedMouseCell = null;
        Mouse.Capture(ActiveView);
        ActiveView.Focus();
        e.Handled = true;
    }

    private void TerminalTextBox_PreviewMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && _suppressRightMouseUp)
        {
            _suppressRightMouseUp = false;
            e.Handled = true;
            return;
        }
        if (!ReferenceEquals(sender, ActiveView))
            return;
        var button = GetMouseButtonCode(e.ChangedButton);

        if (button < 0 || !SendMouseEvent(e, button, released: true))
            return;

        _lastReportedMouseCell = null;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private void TerminalTextBox_PreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        var mode = ActiveSnapshot?.Modes.MouseTrackingMode ?? 0;

        if (!ReferenceEquals(sender, ActiveView) ||
            !IsMouseTrackingEnabled() ||
            mode == 1000 ||
            mode == 1002 && e.LeftButton != MouseButtonState.Pressed &&
                e.MiddleButton != MouseButtonState.Pressed &&
                e.RightButton != MouseButtonState.Pressed)
        {
            return;
        }

        var button = e.LeftButton == MouseButtonState.Pressed
            ? 0
            : e.MiddleButton == MouseButtonState.Pressed
                ? 1
                : e.RightButton == MouseButtonState.Pressed
                    ? 2
                    : 3;

        if (!TryGetMouseCell(e, out var x, out var y) ||
            _lastReportedMouseCell == (x, y, button))
        {
            return;
        }

        _lastReportedMouseCell = (x, y, button);
        SendMouseReport(button + 32, x, y, released: false);
        e.Handled = true;
    }

    private void TerminalTextBox_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        // Other panes scroll their own history; only the active one talks to its program.
        if (!ReferenceEquals(sender, ActiveView))
            return;

        if (ActiveSession is not null && !IsMouseTrackingEnabled() &&
            ActiveSnapshot?.Modes is { AlternateScreen: true } modes &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // The alternate screen has no scrollback; like other terminals,
            // turn the wheel into cursor keys so full-screen apps still scroll.
            var key = (e.Delta > 0 ? "A" : "B");
            var sequence = modes.ApplicationCursorKeys ? $"\x1bO{key}" : $"\x1b[{key}";
            ActiveSession.Write(string.Concat(Enumerable.Repeat(sequence, 3)));
            e.Handled = true;
            return;
        }

        if (!IsMouseTrackingEnabled() ||
            !TryGetMouseCell(e, out var x, out var y))
        {
            return;
        }

        SendMouseReport(e.Delta > 0 ? 64 : 65, x, y, released: false);
        e.Handled = true;
    }

    private void TerminalTextBox_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ActivatePaneOf(sender);
        if (sender is TerminalView { Tag: TerminalPane pane } &&
            pane.LastRenderedSnapshot?.Modes.FocusReporting == true)
            pane.Session?.Write("\x1b[I");
    }

    private void TerminalTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _pendingContextMenuKey = null;
        // The pane losing focus may no longer be the active one after a click.
        if (sender is TerminalView { Tag: TerminalPane pane } &&
            pane.LastRenderedSnapshot?.Modes.FocusReporting == true)
            pane.Session?.Write("\x1b[O");
    }

    private bool SendMouseEvent(
        MouseEventArgs e,
        int button,
        bool released)
    {
        if (button == 0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && ActiveView.IsLinkAt(e))
            return false;
        if (!IsMouseTrackingEnabled() ||
            !TryGetMouseCell(e, out var x, out var y))
        {
            return false;
        }

        SendMouseReport(button, x, y, released);
        return true;
    }

    private void SendMouseReport(int button, int x, int y, bool released)
    {
        if (ActiveSession is null)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            button += 4;

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            button += 8;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            button += 16;

        if (ActiveSnapshot?.Modes.SgrMouse == true)
        {
            ActiveSession.Write($"\x1b[<{button};{x};{y}{(released ? 'm' : 'M')}");
            return;
        }

        // Legacy X10 encoding: releases carry button 3 and values are offset
        // by 32. Output is UTF-8, so only values that stay single-byte are sent.
        if (x > 95 || y > 95)
            return;

        var code = released && button < 64 ? (button & ~3) | 3 : button;
        ActiveSession.Write($"\x1b[M{(char)(32 + code)}{(char)(32 + x)}{(char)(32 + y)}");
    }

    private bool TryGetMouseCell(MouseEventArgs e, out int x, out int y) =>
        ActiveView.TryGetGridCell(e, out x, out y);

    private bool IsMouseTrackingEnabled() =>
        !ActiveView.IsSelecting &&
        (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && ActiveSnapshot?.Modes is
        {
            MouseTrackingMode: > 0
        };

    private static int GetMouseButtonCode(MouseButton button) =>
        button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            _ => -1
        };

    private void SmartRtlMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        RenderVisiblePanes();

        ActiveView.Focus();
    }

    private void FontSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new FontSettingsWindow(
            TerminalTextBox.FontFamily.Source,
            TerminalTextBox.FontSize,
            TerminalTextBox.FontWeight,
            TerminalTextBox.FontStyle,
            _historySize)
        {
            Owner = this
        };

        if (settingsWindow.ShowDialog() != true)
            return;

        ApplyHistorySize(settingsWindow.SelectedHistorySize);
        ApplyFontSettings(settingsWindow.SelectedSettings);
        AppSettings.SaveFont(settingsWindow.SelectedSettings);
        AppSettings.SaveHistorySize(settingsWindow.SelectedHistorySize);
        ActiveView.Focus();
    }

    private void ApplyHistorySize(int historySize)
    {
        _historySize = historySize;

        foreach (var pane in _tabs.SelectMany(tab => tab.Panes))
        {
            if (pane.Buffer is not { } buffer)
                continue;

            QueueRender(
                pane,
                buffer.SetMaximumScrollbackRows(historySize));
        }
    }

    private void GuideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var guideWindow = new GuideWindow
        {
            Owner = this
        };
        guideWindow.Show();
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var theme = ReferenceEquals(sender, LightThemeMenuItem)
            ? AppThemeKind.Light
            : AppThemeKind.Dark;
        AppSettings.SaveTheme(theme);
        ApplyTheme(theme);
        ActiveView.Focus();
    }

    internal void ApplyTheme(AppThemeKind theme)
    {
        AppTheme.Apply(theme);
        UpdateThemeMenu();
        RebuildTabStrip();
        foreach (var view in AllViews())
            view.RefreshTheme();
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(this) { Owner = this };
        settingsWindow.ShowDialog();
        ActiveView.Focus();
    }

    private void CustomizeColorsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var colorsWindow = new SettingsWindow(this, SettingsPage.Colors) { Owner = this };
        colorsWindow.ShowDialog();
        ActiveView.Focus();
    }

    private void BuildSchemeMenus()
    {
        foreach (var (menu, theme) in new[] { (DarkSchemesMenuItem, AppThemeKind.Dark), (LightSchemesMenuItem, AppThemeKind.Light) })
        {
            var defaults = new MenuItem { Header = "Default colors", IsCheckable = true, Tag = "Default colors", DataContext = theme };
            defaults.Click += (_, _) => ApplySchemeFromMenu(theme, null);
            menu.Items.Add(defaults);
            menu.Items.Add(new Separator());

            foreach (var scheme in ColorScheme.All.Where(scheme => scheme.Theme == theme))
            {
                var item = new MenuItem { Header = scheme.Name, IsCheckable = true, Tag = scheme.Name, DataContext = scheme };
                item.Click += (_, _) => ApplySchemeFromMenu(theme, scheme);
                menu.Items.Add(item);
            }
        }
    }

    private void ApplySchemeFromMenu(AppThemeKind theme, ColorScheme? scheme)
    {
        var overrides = new Dictionary<string, System.Windows.Media.Color>(AppTheme.GetOverrides(theme));

        if (scheme is null)
            ColorScheme.ResetTerminalColors(overrides);
        else
            scheme.ApplyTo(overrides);

        AppTheme.SetOverrides(theme, overrides);
        AppSettings.SaveCustomColors(theme, overrides);
        AppSettings.SaveTheme(theme);
        ApplyTheme(theme);
        ActiveView.Focus();
    }

    private void UpdateThemeMenu()
    {
        DarkThemeMenuItem.IsChecked = !AppTheme.IsLight;
        LightThemeMenuItem.IsChecked = AppTheme.IsLight;

        foreach (var item in DarkSchemesMenuItem.Items.OfType<MenuItem>().Concat(LightSchemesMenuItem.Items.OfType<MenuItem>()))
        {
            item.IsChecked = item.DataContext switch
            {
                ColorScheme scheme => scheme.IsActive,
                AppThemeKind theme => ColorScheme.UsesDefaultTerminalColors(theme),
                _ => false
            };
        }
    }

    private async void CheckForUpdatesMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        // Let terminal startup finish before performing optional network I/O.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        if (!IsLoaded || !AppSettings.LoadCheckUpdatesOnStartup())
            return;

        await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress)
        {
            if (manual)
            {
                MessageBox.Show(
                    this,
                    Ui.T("An update check is already in progress."),
                    Ui.T("Check for updates"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        _updateCheckInProgress = true;
        CheckForUpdatesMenuItem.IsEnabled = false;
        var originalHeader = CheckForUpdatesMenuItem.Header;

        if (manual)
            CheckForUpdatesMenuItem.Header = Ui.T("Checking for updates...");

        try
        {
            var result = await UpdateService.CheckAsync();

            if (!result.IsUpdateAvailable)
            {
                if (manual)
                {
                    MessageBox.Show(
                        this,
                        Ui.T("Rtl Terminal {0} is up to date.", result.CurrentVersion.ToString(3)),
                        Ui.T("Check for updates"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            if (!manual && string.Equals(
                    AppSettings.LoadSkippedUpdateVersion(),
                    result.LatestTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ShowUpdateAvailable(result);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                TaskCanceledException or
                InvalidDataException or
                JsonException)
        {
            if (manual)
            {
                MessageBox.Show(
                    this,
                    Ui.T("Rtl Terminal could not check GitHub for updates.") + "\n\n" +
                    exception.Message,
                    Ui.T("Check for updates"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            CheckForUpdatesMenuItem.Header = originalHeader;
            CheckForUpdatesMenuItem.IsEnabled = true;
            _updateCheckInProgress = false;
        }
    }

    private void ShowUpdateAvailable(UpdateCheckResult result)
    {
        var updateWindow = new UpdateAvailableWindow(
            result.CurrentVersion,
            result.LatestVersion)
        {
            Owner = this
        };

        updateWindow.ShowDialog();

        if (updateWindow.DontShowAgain)
        {
            AppSettings.SaveSkippedUpdateVersion(result.LatestTag);
        }
        else if (string.Equals(
                     AppSettings.LoadSkippedUpdateVersion(),
                     result.LatestTag,
                     StringComparison.OrdinalIgnoreCase))
        {
            AppSettings.SaveSkippedUpdateVersion(null);
        }

        if (!updateWindow.OpenUpdateRequested)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.ReleasePage.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this,
                Ui.T("The update page could not be opened.") + "\n\n" + exception.Message,
                Ui.T("Rtl Terminal update"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            """
            Rtl Terminal
            by behnamapps

            Developer: behnam tajadini
            YouTube: aka_techno

            تقدیم به همه فارسی زبانان
            """,
            Ui.T("About Rtl Terminal"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Ui.Language = ReferenceEquals(sender, HebrewMenuItem)
            ? UiLanguage.Hebrew
            : UiLanguage.English;
        AppSettings.SaveUiLanguage(Ui.Language);
        ApplyLanguage();
        RebuildTabStrip();
        ActiveView.Focus();
    }

    private void ApplyLanguage()
    {
        EnglishMenuItem.IsChecked = !Ui.IsRightToLeft;
        HebrewMenuItem.IsChecked = Ui.IsRightToLeft;
        var direction = Ui.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        MainMenu.FlowDirection = direction;
        if (MainMenu.Parent is FrameworkElement toolbar)
            toolbar.FlowDirection = direction;

        // The English header from XAML is kept in Tag and used as the lookup key.
        foreach (var item in MenuItems(MainMenu.Items))
        {
            if (item.Tag is null && item.Header is string header)
                item.Tag = header;
            if (item.Tag is string text)
                item.Header = Ui.T(text);
        }

        SmartRtlMenuItem.ToolTip = Ui.T("Use right-to-left layout only for lines containing RTL letters");
        RowRtlMenuItem.ToolTip = Ui.T("Force right-to-left direction and right alignment for every row, including full-screen applications. This may move application borders.");
        SmartRtlIndicator.ToolTip = Ui.T("Smart RTL automatically handles Persian and mixed-direction text");

        foreach (var (button, toolTip, name) in new (Button, string, string)[]
        {
            (NewTabButton, "New terminal (Ctrl+Shift+T)", "New terminal"),
            (SplitPaneButton, "Split side by side (Ctrl+Shift+D)", "Split side by side"),
            (ProfileMenuButton, "Terminal profiles", "Terminal profiles"),
            (MinimizeButton, "Minimize", "Minimize"),
            (MaximizeButton, "Maximize / Restore", "Maximize or restore"),
            (CloseWindowButton, "Close window", "Close window")
        })
        {
            button.ToolTip = Ui.T(toolTip);
            System.Windows.Automation.AutomationProperties.SetName(button, Ui.T(name));
        }

        if (!_updatingContextMenuItem)
            RefreshContextMenuIntegrationState();
    }

    private static IEnumerable<MenuItem> MenuItems(ItemCollection items) =>
        items.OfType<MenuItem>().SelectMany(item => MenuItems(item.Items).Prepend(item));

    private void ContextMenuIntegrationMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingContextMenuItem)
            return;

        try
        {
            if (ContextMenuIntegrationMenuItem.IsChecked)
                ContextMenuIntegration.Install();
            else
                ContextMenuIntegration.Uninstall();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                Ui.T("تغییر منوی راست‌کلیک انجام نشد.") +
                Environment.NewLine +
                exception.Message,
                "RtlTerminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RefreshContextMenuIntegrationState();
            ActiveView.Focus();
        }
    }

    private void PromptForContextMenuIntegration()
    {
        if (ContextMenuIntegration.HasAnsweredInitialPrompt())
            return;

        var result = MessageBox.Show(
            this,
            Ui.T("آیا گزینه «Open in RtlTerminal» به منوی راست‌کلیک پوشه‌ها اضافه شود؟"),
            "RtlTerminal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        try
        {
            if (result == MessageBoxResult.Yes)
                ContextMenuIntegration.Install();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                Ui.T("افزودن منوی راست‌کلیک انجام نشد.") +
                Environment.NewLine +
                exception.Message,
                "RtlTerminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ContextMenuIntegration.MarkInitialPromptAnswered();
            RefreshContextMenuIntegrationState();
        }
    }

    private void RefreshContextMenuIntegrationState()
    {
        _updatingContextMenuItem = true;
        ContextMenuIntegrationMenuItem.IsChecked =
            ContextMenuIntegration.IsInstalled();
        ContextMenuIntegrationMenuItem.Header =
            ContextMenuIntegrationMenuItem.IsChecked
                ? Ui.T("Remove _Open in RtlTerminal")
                : Ui.T("Add _Open in RtlTerminal");
        _updatingContextMenuItem = false;
    }

    private static string? ResolveStartupDirectory(
        string? requestedDirectory = null)
    {
        if (TryResolveDirectory(requestedDirectory, out var directory))
            return directory;

        var arguments = Environment.GetCommandLineArgs();

        if (arguments.Length >= 2 &&
            TryResolveDirectory(arguments[1], out directory))
        {
            return directory;
        }

        if (AppSettings.LoadStartupDirectory() is { } savedDirectory &&
            TryResolveDirectory(Environment.ExpandEnvironmentVariables(savedDirectory), out directory))
        {
            return directory;
        }

        return TryResolveDirectory(Environment.CurrentDirectory, out directory)
            ? directory
            : null;
    }

    private static bool TryResolveDirectory(
        string? candidate,
        out string? directory)
    {
        directory = null;

        if (string.IsNullOrWhiteSpace(candidate) ||
            !Directory.Exists(candidate))
        {
            return false;
        }

        try
        {
            directory = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static string GetProfileTitle(TerminalProfile profile) =>
        profile switch
        {
            TerminalProfile.PowerShell => "PowerShell",
            TerminalProfile.Wsl => "WSL",
            _ => Ui.T("Command Prompt")
        };

    private static string GetProfileCommand(TerminalProfile profile) =>
        AppSettings.LoadShowStartupBanner()
            ? GetBannerProfileCommand(profile)
            : profile switch
            {
                TerminalProfile.PowerShell =>
                    @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo",
                TerminalProfile.Wsl =>
                    @"C:\Windows\System32\wsl.exe --exec sh -lc ""exec \""${SHELL:-/bin/bash}\"" -l""",
                _ => @"C:\Windows\System32\cmd.exe /D /Q /K ""chcp 65001>nul"""
            };

    private static string GetBannerProfileCommand(TerminalProfile profile) =>
        (profile switch
        {
            TerminalProfile.PowerShell =>
                """
                C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoExit -Command "$lines=@('+--------------------------------------------------------+','| RtlTerminal v{APP_VERSION}                                     |','|                                                        |','| Author : Behnam Tajadini                               |','| Source : github.com/mirbehnam/RtlTerminal              |','| YouTube: @aka_techno                                   |','+--------------------------------------------------------+','','  پشتیبانی کامل از زبان فارسی و راست‌به‌چپ',''); $lines | ForEach-Object { Write-Host $_ }"
                """,
            TerminalProfile.Wsl =>
                """
                C:\Windows\System32\wsl.exe --exec sh -lc "printf '%s\n' '+--------------------------------------------------------+' '| RtlTerminal v{APP_VERSION}                                     |' '|                                                        |' '| Author : Behnam Tajadini                               |' '| Source : github.com/mirbehnam/RtlTerminal              |' '| YouTube: @aka_techno                                   |' '+--------------------------------------------------------+' '' '  پشتیبانی کامل از زبان فارسی و راست‌به‌چپ' ''; exec \"${SHELL:-/bin/bash}\" -l"
                """,
            _ =>
                """
                C:\Windows\System32\cmd.exe /D /Q /K "chcp 65001>nul & echo +--------------------------------------------------------+& echo ^| RtlTerminal v{APP_VERSION}                                     ^|& echo ^|                                                        ^|& echo ^| Author : Behnam Tajadini                               ^|& echo ^| Source : github.com/mirbehnam/RtlTerminal              ^|& echo ^| YouTube: @aka_techno                                   ^|& echo +--------------------------------------------------------+& echo.& echo   پشتیبانی کامل از زبان فارسی و راست‌به‌چپ& echo."
                """
        }).Replace("{APP_VERSION}", AppVersion.Display);

    private static bool IsWslAvailable()
    {
        var windowsDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return File.Exists(Path.Combine(windowsDirectory, "System32", "wsl.exe"));
    }

    private static TerminalProfile LoadDefaultProfile()
    {
        var savedProfile = AppSettings.LoadTerminalProfile();

        if (!Enum.TryParse(savedProfile, out TerminalProfile profile) ||
            !Enum.IsDefined(profile) ||
            profile == TerminalProfile.Wsl && !IsWslAvailable())
        {
            return TerminalProfile.CommandPrompt;
        }

        return profile;
    }

    private TerminalPane? ActivePane => _activeTab?.ActivePane;
    private TerminalView ActiveView => ActivePane?.View ?? TerminalTextBox;
    private ConPtySession? ActiveSession => ActivePane?.Session;
    private TerminalBuffer? ActiveBuffer => ActivePane?.Buffer;
    private TerminalSnapshot? ActiveSnapshot => ActivePane?.LastRenderedSnapshot;

    private IEnumerable<TerminalView> AllViews() =>
        _tabs.SelectMany(tab => tab.Panes).Select(pane => pane.View)
            .Append(TerminalTextBox).Distinct();

    private void ShowTab(TerminalTab tab)
    {
        StorePaneWidths();
        _activeTab = tab;
        LayoutPanes();
        RebuildTabStrip();
        RenderVisiblePanes();
        QueueViewportResize();
    }

    // Keep widths the user dragged with a splitter when the panes are laid out again.
    private void StorePaneWidths()
    {
        if (_activeTab is null || PaneHost.ColumnDefinitions.Count != _activeTab.Panes.Count * 2 - 1)
            return;

        for (var index = 0; index < _activeTab.Panes.Count; index++)
        {
            var width = PaneHost.ColumnDefinitions[index * 2].ActualWidth;
            if (width > 0)
                _activeTab.Panes[index].Width = width;
        }
    }

    // Panes sit side by side in star columns, with a splitter between each pair.
    private void LayoutPanes()
    {
        foreach (var tab in _tabs)
        {
            foreach (var pane in tab.Panes)
            {
                if (pane.IsShown)
                    pane.VerticalOffset = pane.View.VerticalOffset;
                lock (_renderLock)
                    pane.IsShown = ReferenceEquals(tab, _activeTab);
            }
        }

        PaneHost.Children.Clear();
        PaneHost.ColumnDefinitions.Clear();
        if (_activeTab is null)
            return;

        var split = _activeTab.Panes.Count > 1;
        foreach (var pane in _activeTab.Panes)
        {
            if (PaneHost.ColumnDefinitions.Count > 0)
            {
                PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var splitter = new GridSplitter
                {
                    Width = 5,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    ResizeDirection = GridResizeDirection.Columns,
                    Focusable = false
                };
                splitter.SetResourceReference(BackgroundProperty, "ToolbarBorderBrush");
                splitter.DragCompleted += (_, _) => QueueViewportResize();
                Grid.SetColumn(splitter, PaneHost.ColumnDefinitions.Count - 1);
                PaneHost.Children.Add(splitter);
            }

            PaneHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, pane.Width), GridUnitType.Star),
                MinWidth = 120
            });
            pane.Frame.BorderThickness = new Thickness(split ? 1 : 0);
            DetachView(pane.View);
            pane.Frame.Child = pane.View;
            Grid.SetColumn(pane.Frame, PaneHost.ColumnDefinitions.Count - 1);
            PaneHost.Children.Add(pane.Frame);

            if (!pane.FollowOutput)
            {
                var view = pane.View;
                var offset = pane.VerticalOffset;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => view.ScrollToVerticalOffset(offset));
            }
        }

        UpdatePaneFrames();
    }

    // A view can still sit in the frame of a closed pane: the XAML view is handed to the next new pane.
    private static void DetachView(TerminalView view)
    {
        switch (view.Parent)
        {
            case Panel panel:
                panel.Children.Remove(view);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    // Stops a closed pane from drawing into its view and frees the view for reuse.
    private void ReleasePane(TerminalPane pane)
    {
        lock (_renderLock)
            pane.IsShown = false;
        pane.Frame.Child = null;
        _ = pane.DisposeAsync();
    }

    private void UpdatePaneFrames()
    {
        if (_activeTab is null)
            return;

        foreach (var pane in _activeTab.Panes)
        {
            pane.Frame.SetResourceReference(Border.BorderBrushProperty,
                ReferenceEquals(pane, _activeTab.ActivePane) ? "AccentBrush" : "TerminalBackgroundBrush");
        }
    }

    private void ActivatePaneOf(object sender)
    {
        if (sender is TerminalView { Tag: TerminalPane pane })
            ActivatePane(pane);
    }

    private void ActivatePane(TerminalPane pane)
    {
        if (_activeTab is null || ReferenceEquals(pane, _activeTab.ActivePane) ||
            !_activeTab.Panes.Contains(pane))
            return;

        _activeTab.ActivePane = pane;
        UpdatePaneFrames();
        RebuildTabStrip();
        if (!pane.View.IsKeyboardFocusWithin)
            pane.View.Focus();
    }

    private void SelectRelativePane(int direction)
    {
        if (_activeTab is not { Panes.Count: > 1 } tab)
            return;

        var index = tab.Panes.IndexOf(tab.ActivePane);
        ActivatePane(tab.Panes[(index + direction + tab.Panes.Count) % tab.Panes.Count]);
    }

    private void SelectTab(TerminalTab tab)
    {
        if (ReferenceEquals(tab, _activeTab))
            return;

        ShowTab(tab);
        ActiveView.Focus();
    }

    private void SelectRelativeTab(int direction)
    {
        if (_activeTab is null || _tabs.Count < 2)
            return;

        var currentIndex = _tabs.IndexOf(_activeTab);
        var nextIndex = (currentIndex + direction + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[nextIndex]);
    }

    private void ClosePane(TerminalPane? pane)
    {
        if (pane is null || _tabs.FirstOrDefault(tab => tab.Panes.Contains(pane)) is not { } tab)
            return;

        if (tab.Panes.Count == 1)
        {
            CloseTab(tab);
            return;
        }

        DetachPane(tab, pane);
        RebuildTabStrip();
        ReleasePane(pane);
    }

    private void DetachPane(TerminalTab tab, TerminalPane pane)
    {
        StorePaneWidths();
        var index = tab.Panes.IndexOf(pane);
        tab.Panes.RemoveAt(index);
        // The neighbour takes over the removed pane's width.
        tab.Panes[Math.Max(0, index - 1)].Width += pane.Width;
        if (ReferenceEquals(tab.ActivePane, pane))
            tab.ActivePane = tab.Panes[Math.Max(0, index - 1)];

        if (ReferenceEquals(tab, _activeTab))
        {
            LayoutPanes();
            QueueViewportResize();
            ActiveView.Focus();
        }
    }

    // Moves every pane of another tab to the right of the current tab's panes.
    private void JoinTab(TerminalTab? source)
    {
        if (source is null || _activeTab is not { } target || ReferenceEquals(source, target))
            return;

        StorePaneWidths();
        var width = target.Panes.Average(pane => pane.Width);
        foreach (var pane in source.Panes)
        {
            pane.Width = width;
            target.Panes.Add(pane);
        }

        _tabs.Remove(source);
        LayoutPanes();
        RebuildTabStrip();
        RenderVisiblePanes();
        QueueViewportResize();
        ActiveView.Focus();
    }

    private void MovePaneToNewTab(TerminalPane? pane)
    {
        if (pane is null || _activeTab is not { Panes.Count: > 1 } tab || !tab.Panes.Contains(pane))
            return;

        DetachPane(tab, pane);
        pane.Width = 1;
        var newTab = new TerminalTab(pane);
        _tabs.Insert(_tabs.IndexOf(tab) + 1, newTab);
        ShowTab(newTab);
        ActiveView.Focus();
    }

    private ContextMenu CreateTabContextMenu(TerminalTab tab)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle"), FlowDirection = MainMenu.FlowDirection };
        var join = new MenuItem
        {
            Header = Ui.T("Show side by side with the current tab"),
            IsEnabled = !ReferenceEquals(tab, _activeTab)
        };
        join.Click += (_, _) => JoinTab(tab);
        var close = new MenuItem { Header = Ui.T("Close tab") };
        close.Click += (_, _) => CloseTab(tab);
        menu.Items.Add(join); menu.Items.Add(new Separator()); menu.Items.Add(close);
        return menu;
    }

    private void JoinTabMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        JoinTabMenuItem.Items.Clear();

        foreach (var tab in _tabs.Where(tab => !ReferenceEquals(tab, _activeTab)))
        {
            var item = new MenuItem { Header = tab.Title };
            item.Click += (_, _) => JoinTab(tab);
            JoinTabMenuItem.Items.Add(item);
        }

        if (JoinTabMenuItem.Items.Count == 0)
            JoinTabMenuItem.Items.Add(new MenuItem { Header = Ui.T("No other tabs"), IsEnabled = false });
    }

    private void MovePaneMenuItem_Click(object sender, RoutedEventArgs e) => MovePaneToNewTab(ActivePane);

    private void CloseTab(TerminalTab? tab)
    {
        if (tab is null)
            return;

        var index = _tabs.IndexOf(tab);

        if (index < 0)
            return;

        var wasActive = ReferenceEquals(tab, _activeTab);
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            foreach (var pane in tab.Panes)
                ReleasePane(pane);
            Close();
            return;
        }

        if (wasActive)
        {
            _activeTab = null;
            ShowTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        else
            RebuildTabStrip();

        ActiveView.Focus();
        foreach (var pane in tab.Panes)
            ReleasePane(pane);
    }

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            var isActive = ReferenceEquals(tab, _activeTab);
            var accent = new SolidColorBrush(AppTheme.TabAccent);
            var panel = new DockPanel { Height = 34 };
            var closeButton = new Button
            {
                Width = 26, Height = 24, Margin = new Thickness(0, 0, 5, 0),
                Content = "×", FontSize = 15,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = Ui.T("Close {0}", tab.Title), Tag = tab,
                Style = (Style)FindResource("ChromeRoundButtonStyle")
            };
            System.Windows.Automation.AutomationProperties.SetName(closeButton, Ui.T("Close {0}", tab.Title));
            closeButton.Click += TabCloseButton_Click;
            DockPanel.SetDock(closeButton, Dock.Right);
            panel.Children.Add(closeButton);
            var label = new DockPanel();
            var icon = new TextBlock
            {
                Text = tab.Profile == TerminalProfile.PowerShell ? "›_" :
                    tab.Profile == TerminalProfile.Wsl ? "$_" : ">_",
                FontFamily = new FontFamily("Consolas"), FontSize = 13,
                Foreground = isActive ? accent : new SolidColorBrush(AppTheme.TabIcon),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            };
            DockPanel.SetDock(icon, Dock.Left);
            label.Children.Add(icon);
            label.Children.Add(new TextBlock
            {
                Text = tab.Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var selectButton = new Button
            {
                Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 0, 4, 0), ToolTip = tab.Title, Tag = tab,
                Foreground = new SolidColorBrush(isActive
                    ? AppTheme.ActiveTabText : AppTheme.InactiveTabText),
                Style = (Style)FindResource("ChromeTabButtonStyle")
            };
            System.Windows.Automation.AutomationProperties.SetName(selectButton, tab.Title);
            selectButton.Click += TabButton_Click;
            selectButton.ContextMenu = CreateTabContextMenu(tab);
            panel.Children.Add(selectButton);
            var grid = new Grid { Width = 220, Height = 38, Margin = new Thickness(0, 8, 0, 0) };
            var activeBrush = new SolidColorBrush(AppTheme.ActiveTab);
            var shape = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,38 Q8,38 8,30 L8,12 Q8,0 20,0 L200,0 Q212,0 212,12 L212,30 Q212,38 220,38 Z"),
                Fill = isActive ? activeBrush : Brushes.Transparent, IsHitTestVisible = false
            };
            grid.Children.Add(shape);
            if (!isActive) grid.Children.Add(new Border
            {
                Width = 1, Height = 18, Background = new SolidColorBrush(AppTheme.TabSeparator),
                HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false
            });
            panel.Margin = new Thickness(8, 0, 8, 0);
            grid.Children.Add(panel);
            grid.MouseEnter += (_, _) => shape.Fill = isActive ? activeBrush : new SolidColorBrush(AppTheme.HoverTab);
            grid.MouseLeave += (_, _) => shape.Fill = isActive ? activeBrush : Brushes.Transparent;
            TabStrip.Children.Add(grid);
            if (isActive)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => grid.BringIntoView());
        }
    }

    private void TitleTabsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TabScroller.MaxWidth = Math.Max(0, e.NewSize.Width - 98);
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TerminalTab tab })
            SelectTab(tab);
    }

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button { Tag: TerminalTab tab })
            CloseTab(tab);
    }

    private void TerminalTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (GetEffectiveKey(e) == Key.Apps ||
            GetEffectiveKey(e) == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Do not transfer focus into the popup while its opening key is held:
            // ContextMenu handles Apps key-up itself and would immediately close.
            _pendingContextMenuKey = GetEffectiveKey(e);
            e.Handled = true;
            return;
        }
        if (ActiveSession is null)
            return;

        var controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (controlPressed && shiftPressed && e.Key == Key.C)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (controlPressed && shiftPressed && e.Key == Key.V)
        {
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.C)
        {
            if (ActiveView.HasSelection)
                CopySelection();
            else
                ActiveSession.Write("\x03");

            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key == Key.V)
        {
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (controlPressed && GetEffectiveKey(e) == Key.Space)
        {
            ActiveSession.Write("\0");
            e.Handled = true;
            return;
        }

        if (controlPressed && shiftPressed && e.Key == Key.A)
        {
            ActiveView.SelectAll();
            e.Handled = true;
            return;
        }

        if (controlPressed && e.Key >= Key.A && e.Key <= Key.Z)
        {
            var controlCharacter = (char)(e.Key - Key.A + 1);
            ActiveSession.Write(controlCharacter.ToString());
            e.Handled = true;
            return;
        }

        if (!controlPressed &&
            shiftPressed &&
            GetEffectiveKey(e) == Key.OemQuestion &&
            InputLanguageManager.Current.CurrentInputLanguage
                .TwoLetterISOLanguageName == "fa")
        {
            ActiveSession.Write("\u061f");
            e.Handled = true;
            return;
        }

        var key = GetEffectiveKey(e);
        var altPressed = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var sequence = GetTerminalKeySequence(
            key,
            shiftPressed,
            altPressed,
            controlPressed,
            ActiveSnapshot?.Modes.ApplicationCursorKeys == true);

        if (sequence is null)
            return;

        ActiveSession.Write(sequence);
        e.Handled = true;
    }

    private static Key GetEffectiveKey(KeyEventArgs e) =>
        e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

    private void TerminalTextBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_pendingContextMenuKey != GetEffectiveKey(e)) return;
        _pendingContextMenuKey = null;
        e.Handled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ActiveView.IsKeyboardFocusWithin) return;
            var menu = CreateTerminalContextMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.Closed += (_, _) => ActiveView.Focus();
            menu.IsOpen = true;
        });
    }

    private static string? GetTerminalKeySequence(
        Key key,
        bool shift,
        bool alt,
        bool control,
        bool applicationCursorKeys)
    {
        if (key == Key.Tab)
            return shift ? "\x1b[Z" : "\t";

        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) +
            (control ? 4 : 0);
        var hasModifier = modifier > 1;
        var final = key switch
        {
            Key.Up => 'A',
            Key.Down => 'B',
            Key.Right => 'C',
            Key.Left => 'D',
            Key.Home => 'H',
            Key.End => 'F',
            _ => '\0'
        };

        if (final != '\0')
        {
            if (hasModifier)
                return $"\x1b[1;{modifier}{final}";

            return applicationCursorKeys
                ? $"\x1bO{final}"
                : $"\x1b[{final}";
        }

        var functionFinal = key switch
        {
            Key.F1 => 'P',
            Key.F2 => 'Q',
            Key.F3 => 'R',
            Key.F4 => 'S',
            _ => '\0'
        };

        if (functionFinal != '\0')
            return hasModifier
                ? $"\x1b[1;{modifier}{functionFinal}"
                : $"\x1bO{functionFinal}";

        var tildeCode = key switch
        {
            Key.Insert => 2,
            Key.Delete => 3,
            Key.PageUp => 5,
            Key.PageDown => 6,
            Key.F5 => 15,
            Key.F6 => 17,
            Key.F7 => 18,
            Key.F8 => 19,
            Key.F9 => 20,
            Key.F10 => 21,
            Key.F11 => 23,
            Key.F12 => 24,
            _ => 0
        };

        if (tildeCode != 0)
            return hasModifier
                ? $"\x1b[{tildeCode};{modifier}~"
                : $"\x1b[{tildeCode}~";

        return key switch
        {
            Key.Enter => alt ? "\x1b\r" : "\r",
            Key.Space => alt ? "\x1b " : " ",
            Key.Back => alt ? "\x1b\x7f" : "\x7f",
            Key.Escape => "\x1b",
            _ => null
        };
    }

    private void CopySelection()
    {
        if (!ActiveView.HasSelection)
            return;

        try { ActiveView.CopySelection(); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, Ui.T("The clipboard is busy. Please try copying again."), Ui.T("Clipboard"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void PasteClipboard()
    {
        try { PasteClipboardCore(); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, Ui.T("The clipboard is busy. Please try pasting again."), Ui.T("Clipboard"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void PasteClipboardCore()
    {
        if (ActiveSession is null)
            return;

        if (Clipboard.ContainsFileDropList())
        {
            var fileDropList = Clipboard.GetFileDropList();
            var paths = new List<string>(fileDropList.Count);

            for (var index = 0; index < fileDropList.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(fileDropList[index]))
                    paths.Add(fileDropList[index]!);
            }

            WriteClipboardPaths(paths);
            return;
        }

        if (Clipboard.ContainsImage())
        {
            var imagePath = SaveClipboardImage();

            if (imagePath is not null)
                WriteClipboardPaths([imagePath]);

            return;
        }

        if (!Clipboard.ContainsText())
            return;

        var text = Clipboard.GetText()
            .Replace("\r\n", "\r")
            .Replace("\n", "\r");

        WritePastedText(text);
    }

    private void WriteClipboardPaths(IReadOnlyList<string> paths)
    {
        if (ActiveSession is null || paths.Count == 0)
            return;

        var formattedPaths = new StringBuilder();

        foreach (var path in paths)
        {
            if (formattedPaths.Length > 0)
                formattedPaths.Append(' ');

            formattedPaths.Append(FormatClipboardPath(path));
        }

        WritePastedText(formattedPaths.ToString());
    }

    private void WritePastedText(string text)
    {
        if (ActiveSession is null || string.IsNullOrEmpty(text))
            return;

        if (ActiveSnapshot?.Modes.BracketedPaste == true)
            ActiveSession.Write($"\x1b[200~{text}\x1b[201~");
        else
            ActiveSession.Write(text);
    }

    private string FormatClipboardPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (_activeTab?.Profile == TerminalProfile.Wsl)
            return QuotePosixPath(ToWslPath(fullPath));

        return fullPath.Contains(' ') ? $"\"{fullPath}\"" : fullPath;
    }

    // C:\Users\x -> /mnt/c/Users/x; \\wsl.localhost\Distro\home\x -> /home/x
    internal static string ToWslPath(string windowsPath)
    {
        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (!windowsPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = windowsPath[prefix.Length..];
            var slash = rest.IndexOf('\\');
            return slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
        }

        if (windowsPath.Length >= 2 && windowsPath[1] == ':' && char.IsAsciiLetter(windowsPath[0]))
            return $"/mnt/{char.ToLowerInvariant(windowsPath[0])}{windowsPath[2..].Replace('\\', '/')}";

        return windowsPath.Replace('\\', '/');
    }

    internal static string QuotePosixPath(string path)
    {
        var builder = new StringBuilder(path.Length + 8);

        foreach (var c in path)
        {
            if (" '\"\\()&;|<>$`!*?[]{}#~".Contains(c))
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }

    private string? SaveClipboardImage()
    {
        var image = Clipboard.GetImage();

        return image is null ? null : SaveTemporaryImage(image);
    }

    private string SaveTemporaryImage(BitmapSource image)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "RtlTerminal",
            "Clipboard");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory,
            $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        _temporaryClipboardFiles.Add(path);
        return path;
    }

    private void CleanupTemporaryClipboardFiles()
    {
        foreach (var path in _temporaryClipboardFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _temporaryClipboardFiles.Clear();
    }

    private void ReadOutputLoop(TerminalPane pane)
    {
        var session = pane.Session;
        var buffer = pane.Buffer;
        var cancellationTokenSource = pane.CancellationTokenSource;

        if (session is null ||
            buffer is null ||
            cancellationTokenSource is null)
        {
            return;
        }

        var bytes = new byte[8192];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var cancellationToken = cancellationTokenSource.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            int byteCount;

            try
            {
                byteCount = session.Read(bytes);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                break;
            }

            if (byteCount <= 0)
                break;

            var characterCount = decoder.GetChars(
                bytes,
                0,
                byteCount,
                characters,
                0,
                flush: false);

            var output = new string(characters, 0, characterCount);

            var snapshot = buffer.Process(output);

            foreach (var response in snapshot.Responses)
                session.Write(response);

            if (snapshot.Modes.SynchronizedOutput)
                SuspendRendering(pane, snapshot.Revision);
            else
                QueueRender(pane, snapshot);
        }
    }

    private void SuspendRendering(TerminalPane pane, long revision)
    {
        lock (_renderLock)
        {
            pane.LatestQueuedRevision = Math.Max(
                pane.LatestQueuedRevision,
                revision);
            pane.PendingSnapshot = null;
        }
    }

    private void QueueRender(TerminalPane pane, TerminalSnapshot snapshot)
    {
        lock (_renderLock)
        {
            if (snapshot.Revision < pane.LatestQueuedRevision)
                return;

            pane.LatestQueuedRevision = snapshot.Revision;
            pane.PendingSnapshot = snapshot;

            // Panes of hidden tabs are drawn when their tab is shown again.
            if (!pane.IsShown)
                return;

            if (_renderTimer.IsEnabled || _renderStartQueued)
                return;

            _renderStartQueued = true;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            StartRenderTimer);
    }

    private void StartRenderTimer()
    {
        lock (_renderLock)
            _renderStartQueued = false;

        if (!_renderTimer.IsEnabled)
            _renderTimer.Start();
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        var panes = _activeTab?.Panes.ToArray() ?? [];

        foreach (var pane in panes)
        {
            TerminalSnapshot? snapshot;

            lock (_renderLock)
            {
                snapshot = pane.PendingSnapshot;
                pane.PendingSnapshot = null;
            }

            if (snapshot is not null)
                Render(pane, snapshot);
        }

        lock (_renderLock)
        {
            if (panes.All(pane => pane.PendingSnapshot is null))
                _renderTimer.Stop();
        }
    }

    // Draws pending output, or redraws the last frame so view settings take effect.
    private void RenderVisiblePanes()
    {
        if (_activeTab is null)
            return;

        foreach (var pane in _activeTab.Panes)
        {
            bool pending;
            lock (_renderLock)
                pending = pane.PendingSnapshot is not null;

            if (pending)
                StartRenderTimer();
            else if (pane.LastRenderedSnapshot is { } snapshot)
                Render(pane, snapshot);
        }
    }

    private void Render(TerminalPane pane, TerminalSnapshot snapshot)
    {
        pane.LastRenderedSnapshot = snapshot;
        _restoringScrollPosition = true;
        pane.View.Present(snapshot, SmartRtlMenuItem.IsChecked,
            _cellWidth, _lineHeight, pane.FollowOutput, RowRtlMenuItem.IsChecked);
        _restoringScrollPosition = false;
    }

    private void ApplySavedFontSettings()
    {
        var settings = AppSettings.LoadFont();

        if (settings is { } savedSettings)
            ApplyFontSettings(savedSettings);
    }

    private void ApplyFontSettings(TerminalFontSettings settings)
    {
        foreach (var view in AllViews())
        {
            view.FontFamily = new FontFamily(settings.Family);
            view.FontSize = settings.Size;
            view.FontWeight = settings.Bold
                ? FontWeights.Bold
                : FontWeights.Normal;
            view.FontStyle = settings.Italic
                ? FontStyles.Italic
                : FontStyles.Normal;
        }

        UpdateFontMetrics();
        // Hidden tabs get the new grid size when they are shown again.
        foreach (var pane in _tabs.SelectMany(tab => tab.Panes))
            pane.GridSize = default;

        QueueViewportResize();
        RenderVisiblePanes();
    }

    private void UpdateFontMetrics()
    {
        var typeface = new Typeface(
            TerminalTextBox.FontFamily,
            TerminalTextBox.FontStyle,
            TerminalTextBox.FontWeight,
            TerminalTextBox.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var measurement = new FormattedText(
            "M",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalTextBox.FontSize,
            Brushes.White,
            pixelsPerDip);

        _cellWidth = Math.Ceiling(Math.Max(4, measurement.WidthIncludingTrailingWhitespace) * pixelsPerDip) / pixelsPerDip;
        _lineHeight = Math.Ceiling(Math.Max(
            TerminalTextBox.FontSize + 3,
            measurement.Height + 2) * pixelsPerDip) / pixelsPerDip;
    }

    private short GetColumns(TerminalView view)
    {
        var width = view.ViewportWidth > 0
            ? view.ViewportWidth
            : view.ActualWidth - view.Padding.Left - view.Padding.Right - 18;
        return (short)Math.Clamp((int)Math.Floor(Math.Max(80, width) / _cellWidth), 10, 300);
    }

    private short GetRows(TerminalView view)
    {
        var height = view.ViewportHeight > 0
            ? view.ViewportHeight
            : view.ActualHeight - view.Padding.Top - view.Padding.Bottom;
        return (short)Math.Clamp((int)Math.Floor(Math.Max(120, height) / _lineHeight), 10, 100);
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    private enum TerminalProfile
    {
        CommandPrompt,
        PowerShell,
        Wsl
    }

    // A tab in the title bar: one or more panes shown side by side.
    private sealed class TerminalTab(TerminalPane pane)
    {
        public List<TerminalPane> Panes { get; } = [pane];
        public TerminalPane ActivePane { get; set; } = pane;
        public TerminalProfile Profile => ActivePane.Profile;
        public string Title => Panes.Count > 1 ? $"{ActivePane.Title} (+{Panes.Count - 1})" : ActivePane.Title;
    }

    private sealed class TerminalPane(
        int number,
        TerminalProfile profile,
        string profileTitle)
    {
        private int _disposeStarted;

        public int Number { get; } = number;
        public TerminalProfile Profile { get; } = profile;
        public string Title { get; } = $"{profileTitle} {number}";
        public TerminalView View { get; set; } = null!;
        public Border Frame { get; } = new();
        public double Width { get; set; } = 1;
        public bool IsShown { get; set; }
        public ConPtySession? Session { get; set; }
        public TerminalBuffer? Buffer { get; set; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        public TerminalSnapshot? PendingSnapshot { get; set; }
        public long LatestQueuedRevision { get; set; }
        public TerminalSnapshot? LastRenderedSnapshot { get; set; }
        public bool FollowOutput { get; set; } = true;
        public (short Columns, short Rows) GridSize { get; set; }
        public double VerticalOffset { get; set; }

        public Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return Task.CompletedTask;

            var session = Session;
            var cancellationTokenSource = CancellationTokenSource;
            CancellationTokenSource = null;
            Session = null;

            return Task.Run(() =>
            {
                try
                {
                    // Keep ReadOutputLoop draining until ClosePseudoConsole
                    // completes and closes the output channel.
                    session?.Dispose();
                }
                finally
                {
                    cancellationTokenSource?.Cancel();
                    cancellationTokenSource?.Dispose();
                }
            });
        }
    }
}
