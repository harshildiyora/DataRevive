using System;
using System.IO;
using System.Windows;
using DataRecovery.App.Services;

namespace DataRecovery.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize default Dark Theme
        ThemeService.ApplyTheme(true);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogException("AppDomain", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogException("Dispatcher", args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    private static void LogException(string source, Exception? ex)
    {
        string msg = $"Unhandled Exception [{source}]:\n{ex?.ToString()}";
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] {msg}\n\n");
            MessageBox.Show(msg, "DataRecovery Pro Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
