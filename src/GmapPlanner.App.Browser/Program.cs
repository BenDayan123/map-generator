using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using GmapPlanner.App.Browser;

[assembly: SupportedOSPlatform("browser")]

internal sealed partial class Program
{
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
        .With(new FontManagerOptions
        {
            // A browser has no system fonts: Inter is the default face, and Inter has no Hebrew,
            // so Hebrew trip names/notes fall back to the bundled Noto Sans Hebrew.
            DefaultFamilyName = "fonts:Inter#Inter",
            FontFallbacks =
            [
                new FontFallback { FontFamily = new FontFamily("avares://GmapPlanner.App.Browser/Assets/Fonts#Noto Sans Hebrew") },
            ],
        })
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
}
