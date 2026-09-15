using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GmapPlanner.App.Platform;
using GmapPlanner.App.ViewModels;
using GmapPlanner.App.Views;

namespace GmapPlanner.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(new DesktopPlatformServices()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}