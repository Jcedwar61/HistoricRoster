using System.IO;
using System.Windows;

namespace HistoricRosterApp;

public partial class App : Application
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HistoricRoster", "error.log");

    public static void LogError(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Never fail silently: log every unhandled error and show it.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("UI", args.Exception);
            MessageBox.Show(args.Exception.Message + $"\n\nDetails: {LogPath}", "Historic Roster", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError("AppDomain", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { LogError("Task", args.Exception); args.SetObserved(); };
        base.OnStartup(e);

        // The theme's Window style (background, font, text colour) for every window, the dialogs built in code
        // included: an implicit style only reaches the Window class itself, not the classes derived from it.
        if (TryFindResource(typeof(Window)) is Style windowStyle)
            FrameworkElement.StyleProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(windowStyle));

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogError("Startup", ex);
            MessageBox.Show("Historic Roster could not start:\n\n" + ex.Message + $"\n\nDetails: {LogPath}", "Historic Roster",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
