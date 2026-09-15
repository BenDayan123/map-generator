using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GmapPlanner.App.Platform;
using GmapPlanner.App.ViewModels;
using GmapPlanner.App.Views;

namespace GmapPlanner.App.Browser;

public partial class App : Application
{
    // Inter (the default) has no Hebrew and the browser has no system fonts, so text inherits
    // an Inter → Noto Sans Hebrew fallback chain. This comma-separated FontFamily is the
    // per-control fallback path; FontManagerOptions.FontFallbacks crashes on Avalonia.Browser.
    private static readonly FontFamily UiFont = new(
        "fonts:Inter#Inter, avares://GmapPlanner.App.Browser/Assets/Fonts/NotoSansHebrew-Regular.ttf#Noto Sans Hebrew");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                FontFamily = UiFont,
                DataContext = new MainViewModel(new BrowserPlatformServices()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
