using Avalonia;
using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SfwPlayer.Platform;
using SfwPlayer.Platform.MacOS;
using SfwPlayer.Services;

namespace SfwPlayer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var wkIdx = Array.IndexOf(args, "--wktest");
        if (wkIdx >= 0 && wkIdx + 1 < args.Length)
        {
            VlcSetup.ActivateApp();
            WkTestMode.Run(args[wkIdx + 1]);
            return;
        }

        if (Array.IndexOf(args, "--signin-test") >= 0)
        {
            VlcSetup.ActivateApp();
            WkTestMode.RunSignIn();
            return;
        }

        VlcSetup.Initialize();
        VlcSetup.ActivateApp();
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
        services.AddSingleton<CookieStore>();
        services.AddSingleton<PlaybackStateStore>();
        services.AddSingleton<InnerTubeService>();
        services.AddSingleton<YoutubeService>();
        var provider = services.BuildServiceProvider();

        // load any persisted cookies immediately
        provider.GetRequiredService<CookieStore>().TryLoad();
        return provider;
    }
}
