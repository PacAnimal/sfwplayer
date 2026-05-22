using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SfwPlayer.Platform;

namespace SfwPlayer;

// ReSharper disable once PartialTypeWithSinglePart
public partial class App : Application
{
    public static IServiceProvider Services { get; set; } = null!;
    public static string? OverrideUrl { get; set; }
    public static bool ExitOnDone { get; set; }
    public static string[] ExtraVlcArgs { get; set; } = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        VlcSetup.PatchAvnWindow();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Services);

        base.OnFrameworkInitializationCompleted();
    }
}
