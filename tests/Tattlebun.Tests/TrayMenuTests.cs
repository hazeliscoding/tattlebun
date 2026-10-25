using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Tattlebun.Desktop;

namespace Tattlebun.Tests;

public class TrayMenuTests
{
    // On Windows, Avalonia draws the tray menu with these controls. Without a theme they have no
    // template, so the menu opens empty and Quit can't be clicked.
    [Fact]
    public void The_tray_menu_controls_have_templates()
    {
        var app = AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting()
            .Instance!;

        Assert.True(app.TryGetResource(typeof(MenuFlyoutPresenter), ThemeVariant.Default, out var presenter));
        Assert.IsType<ControlTheme>(presenter);
        Assert.True(app.TryGetResource(typeof(MenuItem), ThemeVariant.Default, out var item));
        Assert.IsType<ControlTheme>(item);
    }
}
