using Microsoft.Win32;
using Tattlebun.Core;
using Tattlebun.Windows;

namespace Tattlebun.Tests;

public sealed class SnapshotReaderTests : IDisposable
{
    private static readonly Fingerprinter Fingerprints = new([7, 7, 7]);

    private readonly string _root = Directory.CreateTempSubdirectory("tattlebun-reader-").FullName;

    private string Home => Path.Combine(_root, "home");

    private string AppData => Path.Combine(_root, "appdata");

    private SnapshotReader TempReader()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(AppData);
        return new SnapshotReader(Fingerprints, Home, AppData, Path.Combine(_root, "documents"));
    }

    [Fact]
    public void The_real_watch_list_is_read()
    {
        var snapshot = new SnapshotReader(Fingerprints).Read(null);

        Assert.Contains("Path", snapshot.Parts["env-machine"].Items.Keys);
        Assert.Contains("Winmgmt", snapshot.Parts["services"].Items.Keys);
        Assert.NotEmpty(snapshot.Parts["apps"].Items);
    }

    [Fact]
    public void Reading_again_reuses_parts_whose_stamp_is_unchanged()
    {
        var reader = new SnapshotReader(Fingerprints);
        var first = reader.Read(null);
        var second = reader.Read(first);

        Assert.Same(first.Parts["services"], second.Parts["services"]);
        Assert.Same(first.Parts["env-machine"], second.Parts["env-machine"]);
        Assert.NotSame(first.Parts["apps"], second.Parts["apps"]);
    }

    // Positive control for reuse: a part whose stamp differs is read again.
    [Fact]
    public void A_part_with_a_stale_stamp_is_read_again()
    {
        var stale = new Snapshot(DateTimeOffset.UtcNow, new Dictionary<string, Part>
        {
            ["env-machine"] = new("stale", new Dictionary<string, string> { ["bogus"] = "x" }),
        });

        var snapshot = new SnapshotReader(Fingerprints).Read(stale);

        Assert.DoesNotContain("bogus", snapshot.Parts["env-machine"].Items.Keys);
        Assert.Contains("Path", snapshot.Parts["env-machine"].Items.Keys);
    }

    [Fact]
    public void A_new_item_at_the_top_of_home_is_added_but_Claude_Codes_own_files_are_not()
    {
        var reader = TempReader();
        var before = reader.Read(null);
        foreach (var name in new[] { "new.txt", ".claude.json.lock", ".claude.json.tmp.4242.abc123" })
        {
            File.WriteAllText(Path.Combine(Home, name), "");
        }

        var changes = Diff.Between(before, reader.Read(before)).Where(c => c.Part == "home");

        Assert.Equal([new Change("home", "new.txt", ChangeKind.Added)], changes);
    }

    [Fact]
    public void A_dotfile_changes_when_its_contents_do_not_when_it_is_touched()
    {
        var reader = TempReader();
        var file = Path.Combine(Home, ".gitconfig");
        File.WriteAllText(file, "a=1");
        var first = reader.Read(null);

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        var touched = reader.Read(first);
        File.WriteAllText(file, "a=2");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(2));
        var edited = reader.Read(touched);

        Assert.Empty(Diff.Between(first, touched));
        Assert.Equal([new Change($"file|{file}", file, ChangeKind.Changed)], Diff.Between(touched, edited));
        Assert.DoesNotContain("a=2", edited.Parts[$"file|{file}"].Items[file]);
    }

    [Fact]
    public void A_written_file_is_read_like_a_dotfile()
    {
        var reader = TempReader();
        var file = Path.Combine(_root, "elsewhere", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var before = reader.Read(null, [file]);
        File.WriteAllText(file, "{}");

        Assert.Equal([new Change($"file|{file}", file, ChangeKind.Added)], Diff.Between(before, reader.Read(before, [file])));
    }

    // A junction on the way could lead to a network share, so the file behind it is left out, not followed.
    [Fact]
    public void A_written_file_behind_a_folder_link_is_left_out()
    {
        var reader = TempReader();
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        var link = Path.Combine(_root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using (var mklink = System.Diagnostics.Process.Start(start)!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
        }

        try
        {
            var behindLink = Path.Combine(link, "settings.json");
            var direct = Path.Combine(target, "settings.json");
            var snapshot = reader.Read(null, [behindLink, direct]);

            Assert.True(File.Exists(behindLink));
            Assert.DoesNotContain($"file|{behindLink}", snapshot.Parts.Keys);
            // Positive control: the same file, reached without the link, is read.
            Assert.Contains($"file|{direct}", snapshot.Parts.Keys);
        }
        finally
        {
            // Deleting the junction itself leaves its target alone; a recursive delete refuses it.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Global_npm_packages_are_read_with_their_scopes()
    {
        var reader = TempReader();
        var modules = Path.Combine(AppData, "npm", "node_modules");
        Directory.CreateDirectory(Path.Combine(modules, "left-pad"));
        Directory.CreateDirectory(Path.Combine(modules, "@scope", "pkg"));
        var before = reader.Read(null);

        Directory.CreateDirectory(Path.Combine(modules, "@scope", "other"));
        var after = reader.Read(before);

        Assert.Contains("left-pad", after.Parts["npm"].Items.Keys);
        Assert.Contains("@scope/pkg", after.Parts["npm"].Items.Keys);
        Assert.Equal([new Change("npm", "@scope/other", ChangeKind.Added)], Diff.Between(before, after));
    }

    [Fact(Explicit = true)]
    public void TattlebunTest_a_user_variable_is_added_then_removed()
    {
        const string name = "TattlebunTest_Reader";
        var reader = new SnapshotReader(Fingerprints);
        using var env = Registry.CurrentUser.OpenSubKey("Environment", writable: true)!;
        try
        {
            var before = reader.Read(null);
            env.SetValue(name, "1");
            var added = reader.Read(before);
            env.DeleteValue(name);
            var removed = reader.Read(added);

            Assert.Contains(new Change("env-user", name, ChangeKind.Added), Diff.Between(before, added));
            Assert.Contains(new Change("env-user", name, ChangeKind.Removed), Diff.Between(added, removed));
        }
        finally
        {
            env.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    // Services are reused while the Services key's own time is unchanged, which is only safe if a new
    // subkey changes its parent's last-write time.
    [Fact(Explicit = true)]
    public void TattlebunTest_a_new_subkey_changes_its_parents_last_write_time()
    {
        var path = $@"Software\TattlebunTest_Stamp_{Guid.NewGuid():N}";
        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(path);
            var before = SnapshotReader.LastWrite(parent);
            Thread.Sleep(50);
            parent.CreateSubKey("child").Dispose();

            Assert.NotEqual(before, SnapshotReader.LastWrite(parent));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
