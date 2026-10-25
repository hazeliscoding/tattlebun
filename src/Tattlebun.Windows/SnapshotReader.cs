using System.Security;
using System.Text;
using Microsoft.Win32;
using Tattlebun.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Tattlebun.Windows;

/// <summary>
/// Reads the watch list straight from the registry and disk. A part whose stamp matches the previous
/// snapshot's is reused without reading it, and a part that fails to read is left out of the snapshot.
/// </summary>
public sealed class SnapshotReader(Fingerprinter fingerprinter, string home, string appData, string documents)
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const long LargeFile = 1 << 20;

    private static readonly string[] Dotfiles =
    [
        ".gitconfig", ".gitignore_global", ".npmrc", ".yarnrc", ".yarnrc.yml", ".condarc", ".pypirc", @".ssh\config",
        ".bashrc", ".bash_profile", ".profile", ".wslconfig",
    ];

    public SnapshotReader(Fingerprinter fingerprinter)
        : this(
            fingerprinter,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
    {
    }

    public Snapshot Read(Snapshot? previous)
    {
        var parts = new Dictionary<string, Part>();

        // The stamp is read before the items, so a change made while the items are read shows up next time.
        // A part with no stamp is read every time.
        void Add(string name, Func<string>? stamp, Func<Dictionary<string, string>> items)
        {
            try
            {
                var now = stamp?.Invoke();
                parts[name] = now is not null && previous is not null && previous.Parts.TryGetValue(name, out var old) && old.Stamp == now
                    ? old
                    : new Part(now ?? "", items());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
            }
        }

        void Values(string name, RegistryKey hive, string path) =>
            Add(name, () => KeyStamp(hive, path), () => RegistryValues(hive, path));

        Values("env-user", Registry.CurrentUser, "Environment");
        Values("env-machine", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        Values("run-user", Registry.CurrentUser, Run);
        Values("runonce-user", Registry.CurrentUser, RunOnce);
        Values("run-machine", Registry.LocalMachine, Run);
        Values("runonce-machine", Registry.LocalMachine, RunOnce);
        Values("run-machine32", Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");

        // A service added or removed changes the Services key's own last-write time, so its name list can be reused.
        Add("services", () => KeyStamp(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services"), () => SubkeyNames(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services"));

        // An upgrade rewrites an app's own key without touching the Uninstall key, so every app key's time is read.
        Add("apps", null, () => new[] { ("user", Registry.CurrentUser, Uninstall), ("machine", Registry.LocalMachine, Uninstall), ("machine32", Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") }
            .SelectMany(root => SubkeyTimes(root.Item2, root.Item3).Select(app => ($"{root.Item1}|{app.Key}", app.Value)))
            .ToDictionary(app => app.Item1, app => app.Value));

        // nvm-windows keeps global packages beside the node.exe it links, not in %APPDATA%\npm.
        string[] npmRoots = [Path.Combine(appData, "npm", "node_modules"), .. Environment.GetEnvironmentVariable("NVM_SYMLINK") is { } nvm ? [Path.Combine(nvm, "node_modules")] : Array.Empty<string>()];
        Add("npm", () => DirStamp([.. npmRoots, .. npmRoots.SelectMany(Scopes)]), () => NpmPackages(npmRoots));

        var python = Path.Combine(appData, "Python");
        Add("pip", () => DirStamp([python, .. SitePackages(python)]), () => SitePackages(python)
            .SelectMany(site => Directory.EnumerateDirectories(site, "*.dist-info").Select(d => $"{Path.GetFileName(Path.GetDirectoryName(site))}|{Path.GetFileName(d)}"))
            .ToDictionary(n => n, _ => "1"));

        DirEntries("dotnet-tools", Path.Combine(home, ".dotnet", "tools"));
        DirEntries("startup-user", Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        DirEntries("startup-common", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        Add("home", () => DirStamp([home]), () => Directory.EnumerateFileSystemEntries(home).Select(Path.GetFileName).Where(n => !IsClaudeCodeOwn(n!)).ToDictionary(n => n!, _ => "1"));

        foreach (var file in Dotfiles.Select(f => Path.Combine(home, f))
            .Concat([Path.Combine(appData, "pip", "pip.ini")])
            .Concat(new[] { "PowerShell", "WindowsPowerShell" }.SelectMany(shell => new[] { "Microsoft.PowerShell_profile.ps1", "profile.ps1" }.Select(p => Path.Combine(documents, shell, p)))))
        {
            Add($"file|{file}", () => FileStamp(file), () => FileFingerprint(file));
        }

        return new Snapshot(DateTimeOffset.UtcNow, parts);

        void DirEntries(string name, string dir) =>
            Add(name, () => DirStamp([dir]), () => Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFileSystemInfos().ToDictionary(e => e.Name, e => e.LastWriteTimeUtc.Ticks.ToString())
                : []);
    }

    // Claude Code writes these at the top of the home folder while commands run; a receipt would blame the agent.
    internal static bool IsClaudeCodeOwn(string name) =>
        name.Equals(".claude.json.lock", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".claude.json.tmp.", StringComparison.OrdinalIgnoreCase);

    internal static unsafe long LastWrite(RegistryKey key)
    {
        FILETIME time;
        var error = PInvoke.RegQueryInfoKey(new HKEY(key.Handle.DangerousGetHandle()), default, null, null, null, null, null, null, null, null, null, &time);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new IOException($"RegQueryInfoKey failed: {error}");
        }

        return ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    }

    private static string KeyStamp(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key is null ? "absent" : LastWrite(key).ToString();
    }

    private Dictionary<string, string> RegistryValues(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null)
        {
            return [];
        }

        return key.GetValueNames().ToDictionary(n => n, n => fingerprinter.Of(ValueBytes(key.GetValueKind(n), key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames))), StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] ValueBytes(RegistryValueKind kind, object? value) =>
        [(byte)kind, .. value switch
        {
            byte[] bytes => bytes,
            string[] lines => Encoding.UTF8.GetBytes(string.Join('\0', lines)),
            int number => BitConverter.GetBytes(number),
            long number => BitConverter.GetBytes(number),
            _ => Encoding.UTF8.GetBytes(value?.ToString() ?? ""),
        }];

    private static Dictionary<string, string> SubkeyNames(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key is null ? [] : key.GetSubKeyNames().ToDictionary(n => n, _ => "1", StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> SubkeyTimes(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        var times = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key?.GetSubKeyNames() ?? [])
        {
            using var sub = key!.OpenSubKey(name);
            if (sub is not null)
            {
                times[name] = LastWrite(sub).ToString();
            }
        }

        return times;
    }

    private static string DirStamp(IEnumerable<string> dirs) =>
        string.Join('|', dirs.Select(d => Directory.Exists(d) ? Directory.GetLastWriteTimeUtc(d).Ticks.ToString() : "absent"));

    private static IEnumerable<string> Scopes(string root) =>
        Directory.Exists(root) ? Directory.EnumerateDirectories(root, "@*") : [];

    private static Dictionary<string, string> NpmPackages(IEnumerable<string> roots)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (dir.Name.StartsWith('@'))
                {
                    foreach (var scoped in dir.EnumerateDirectories())
                    {
                        packages[$"{dir.Name}/{scoped.Name}"] = scoped.LastWriteTimeUtc.Ticks.ToString();
                    }
                }
                else if (!dir.Name.StartsWith('.'))
                {
                    packages[dir.Name] = dir.LastWriteTimeUtc.Ticks.ToString();
                }
            }
        }

        return packages;
    }

    private static IEnumerable<string> SitePackages(string python) =>
        Directory.Exists(python)
            ? Directory.EnumerateDirectories(python).Select(v => Path.Combine(v, "site-packages")).Where(Directory.Exists)
            : [];

    private static string FileStamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "absent";
    }

    // Only the fingerprint is kept, never the contents. A large file is fingerprinted by size and time instead.
    private Dictionary<string, string> FileFingerprint(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return [];
        }

        return new() { [file] = info.Length > LargeFile ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : fingerprinter.Of(File.ReadAllBytes(file)) };
    }
}
