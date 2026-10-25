using Avalonia;
using Avalonia.Controls;

namespace Tattlebun.Desktop;

internal static class Program
{
    // Tattlebun has no window yet, so only Quit in the tray menu ends it.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
