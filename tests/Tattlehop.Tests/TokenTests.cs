using System.Globalization;
using System.Xml.Linq;

namespace Tattlehop.Tests;

public class TokenTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Light", "Text1")]
    [InlineData("Light", "Text2")]
    [InlineData("Light", "AccentText")]
    [InlineData("Dark", "Text1")]
    [InlineData("Dark", "Text2")]
    [InlineData("Dark", "AccentText")]
    public void Text_passes_AA_on_every_surface(string theme, string text)
    {
        var colors = Colors(theme);
        foreach (var surface in new[] { "Surface1", "Surface2", "Surface3" })
        {
            Assert.True(Contrast(colors[text], colors[surface]) >= 4.5, $"{text} on {surface} in {theme}");
        }
    }

    // Positive control: the logo's light cornflower is too pale for body text on Surface3.
    [Fact]
    public void The_check_fails_the_light_accent_as_body_text() =>
        Assert.True(Contrast(Colors("Light")["Accent"], Colors("Light")["Surface3"]) < 4.5);

    private static Dictionary<string, string> Colors(string theme)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Tattlehop.slnx")))
        {
            dir = dir.Parent!;
        }

        var tokens = XDocument.Load(Path.Combine(dir.FullName, "src", "Tattlehop.Desktop", "Themes", "Tokens.axaml"));
        return tokens.Descendants()
            .Single(e => (string?)e.Attribute(Xaml + "Key") == theme)
            .Elements()
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => (string)e.Attribute("Color")!);
    }

    // WCAG 2 contrast ratio. Alpha is ignored, so it only fits opaque tokens.
    private static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var rgb = hex[^6..];
        double Channel(int i)
        {
            var v = int.Parse(rgb.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }
}
