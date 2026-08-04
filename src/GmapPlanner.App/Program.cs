using Avalonia;
using System;
using GmapPlanner.Core;

namespace GmapPlanner.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // A windowed app has nowhere to print, so without this a crash just vanishes.
        // Mirrors the Python app's data_dir/last_error.log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            LogCrash(e);
            throw;
        }
    }

    private static void LogCrash(Exception? e)
    {
        if (e is null) return;
        try
        {
            System.IO.File.WriteAllText(
                AppDataPaths.DataPath("last_error.log"),
                $"{DateTime.Now:O}\n{e}\n");
        }
        catch
        {
            // Logging the crash must never cause another one.
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
