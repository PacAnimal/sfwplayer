using Avalonia;
using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Platform;
using SfwPlayer.Services;

namespace SfwPlayer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VlcSetup.Initialize();
        VlcSetup.ActivateApp();
        VlcSetup.FinishLaunching(); // main-thread-only: makes CGMainDisplayID() valid before Avalonia init
        App.Services = BuildServices();

        var extraVlcArgs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) App.OverrideUrl = args[++i];
            else if (args[i] == "--exit-on-done") App.ExitOnDone = true;
            else if (args[i] == "--vlc-arg" && i + 1 < args.Length) extraVlcArgs.Add(args[++i]);
        }
        App.ExtraVlcArgs = [.. extraVlcArgs];

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSereneConsoleLogging(configure: c =>
        {
            c.PrintColor = true;
            c.FilterMicrosoftSpam = true;
            c.MinLogLevel = LogLevel.Information;
            c.DotNetLogLevelNames = false;
            c.PrintCategory = true;
            c.PrintLogLevel = true;
            c.TimestampFormat = null;
            c.TimestampUtc = false;
        });
        services.AddSingleton<YoutubeService>();
        return services.BuildServiceProvider();
    }
}
