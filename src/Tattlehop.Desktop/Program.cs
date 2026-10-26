using Avalonia;
using Avalonia.Controls;

namespace Tattlehop.Desktop;

internal static class Program
{
    // Tattlehop has no window yet, so only Quit in the tray menu ends it.
    [STAThread]
    public static int Main(string[] args)
    {
        TaskWatch.Start();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
