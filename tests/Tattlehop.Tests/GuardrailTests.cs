using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Tattlehop.Desktop;
using Tattlehop.Hook;

namespace Tattlehop.Tests;

/// <summary>
/// Structural rules from AGENTS.md: the core has no UI and no Win32, Tattlehop's own code never
/// references a networking assembly, and only the tray app may bring in Velopack's update check.
/// </summary>
public class GuardrailTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static readonly string[] NetworkAssemblies =
    [
        "System.Net.Http",
        "System.Net.Http.Json",
        "System.Net.Mail",
        "System.Net.Requests",
        "System.Net.Sockets",
        "System.Net.WebClient",
        "System.Net.WebSockets",
        "System.Net.WebSockets.Client",
    ];

    private static readonly Assembly Core = Assembly.Load("Tattlehop.Core");
    private static readonly Assembly Windows = Assembly.Load("Tattlehop.Windows");
    private static readonly Assembly Hook = typeof(HookRun).Assembly;
    private static readonly Assembly Desktop = typeof(App).Assembly;

    [Fact]
    public void Core_does_not_reference_Avalonia() => Assert.Empty(References(Core, "Avalonia"));

    // Blame must test on made-up snapshots, so the Windows calls live outside the core.
    [Fact]
    public void Core_has_no_Win32_interop() => Assert.Empty(Interop(Core));

    [Fact]
    public void No_Tattlehop_assembly_references_a_networking_assembly()
    {
        foreach (var assembly in new[] { Core, Windows, Hook, Desktop })
        {
            Assert.Empty(NetworkReferences(assembly));
        }
    }

    // A snapshot must never touch the network, so the update check stays in the tray app.
    [Fact]
    public void Only_the_tray_app_may_reference_Velopack()
    {
        foreach (var assembly in new[] { Core, Windows, Hook })
        {
            Assert.Empty(References(assembly, "Velopack"));
        }
    }

    // Positive controls: the same checks find what they exist to catch.
    [Fact]
    public void The_check_finds_Avalonia_in_the_tray_app() => Assert.NotEmpty(References(Desktop, "Avalonia"));

    [Fact]
    public void The_check_finds_a_networking_reference() =>
        Assert.Contains("System.Net.Http", NetworkReferences(typeof(JsonContent).Assembly));

    [Fact]
    public void The_check_finds_Win32_interop()
    {
        var found = Interop(typeof(GuardrailTests).Assembly);
        Assert.Contains($"{typeof(InteropFixture).FullName}.{nameof(InteropFixture.GetSystemMetrics)}", found);
        Assert.Contains(typeof(IComFixture).FullName, found);
    }

    private static string[] References(Assembly assembly, string prefix) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))];

    private static string[] NetworkReferences(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(name => name.Name!).Where(NetworkAssemblies.Contains)];

    // P/Invoke methods (CsWin32 and LibraryImport both compile to these) and COM interfaces.
    private static string[] Interop(Assembly assembly) =>
        [.. assembly.GetTypes().Where(type => type.IsImport).Select(type => type.FullName!),
         .. assembly.GetTypes()
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")];

    private static class InteropFixture
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
    }

    [ComImport, Guid("00000000-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComFixture;
}
