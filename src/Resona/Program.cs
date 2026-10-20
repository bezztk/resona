using Avalonia;
using System;

using Velopack;
using Resona.Services;

namespace Resona;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (Array.Exists(args, argument => string.Equals(
                argument, "--migrate-track-filenames", StringComparison.OrdinalIgnoreCase)))
        {
            var result = TrackFileNameMigration.Run(Values.DbPath, Values.TracksDirectory);
            Console.WriteLine(
                $"Renamed={result.Renamed}; Recovered={result.Recovered}; AlreadyCanonical={result.AlreadyCanonical}; " +
                $"MissingFiles={result.MissingFiles}; Backup={result.BackupPath ?? "none"}");
            return;
        }

        VelopackApp.Build().Run();

        using var singleInstance = SingleInstanceCoordinator.Start();
        if (!singleInstance.IsPrimary)
        {
            singleInstance.NotifyPrimaryAsync().GetAwaiter().GetResult();
            return;
        }

        App.SingleInstance = singleInstance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
