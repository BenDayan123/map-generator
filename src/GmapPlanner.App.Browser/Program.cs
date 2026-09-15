using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using GmapPlanner.App.Browser;

[assembly: SupportedOSPlatform("browser")]

internal sealed partial class Program
{
    // WithInterFont sets Inter as the default face. Inter has no Hebrew and a browser has no
    // system fonts, so the Hebrew fallback is applied as a comma-separated FontFamily on the
    // root view (App.axaml.cs) — FontManagerOptions.FontFallbacks is broken on Avalonia.Browser
    // (it collapses the default typeface to "$Default" and crashes at startup).
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
}
