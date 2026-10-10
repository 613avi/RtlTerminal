using System.IO;
using System.Windows;
using System.Windows.Threading;



namespace RtlTerminal;



public partial class App : Application
{
    private const int MinimumWindowsBuild = 17763;

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RtlTerminal",
        "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => LogCrash("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LogCrash("UnobservedTask", args.Exception);

        base.OnStartup(e);
        Ui.Language = AppSettings.LoadUiLanguage();

        if (!OperatingSystem.IsWindowsVersionAtLeast(
                major: 10,
                minor: 0,
                build: MinimumWindowsBuild))
        {
            var version = Environment.OSVersion.Version;
            MessageBox.Show(
                "RtlTerminal requires Windows 10 version 1809 " +
                "(build 17763) or Windows Server 2019 or later." +
                Environment.NewLine +
                Environment.NewLine +
                $"Detected Windows version: {version}",
                "Unsupported Windows version",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        foreach (var theme in Enum.GetValues<AppThemeKind>())
            AppTheme.SetOverrides(theme, AppSettings.LoadCustomColors(theme));
        AppTheme.Apply(AppSettings.LoadTheme());
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(
                CrashLogPath,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {source} " +
                $"args=[{string.Join(" | ", Environment.GetCommandLineArgs())}] " +
                $"cwd={Environment.CurrentDirectory}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never turn into a second crash.
        }
    }
}
