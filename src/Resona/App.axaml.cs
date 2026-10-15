using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Resona.Services;
using MainWindow = Resona.Views.MainWindow;

namespace Resona;

public partial class App : Application
{
    internal static SingleInstanceCoordinator? SingleInstance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            SingleInstance?.SetActivationHandler(() =>
                Dispatcher.UIThread.Post(mainWindow.ActivateFromSecondaryLaunch));
            _ = AppUpdateService.Current.CheckForUpdatesAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
