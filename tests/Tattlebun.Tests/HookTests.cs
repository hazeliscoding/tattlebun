using System.Text;
using Tattlebun.Core;
using Tattlebun.Hook;
using Tattlebun.Windows;

namespace Tattlebun.Tests;

public sealed class HookTests : IDisposable
{
    private const string Command = "set NPM_TOKEN=fake-token-for-tests";

    private readonly string _dir = Directory.CreateTempSubdirectory("tattlebun-hook-").FullName;

    private string StorePath => Path.Combine(_dir, "tattlebun.db");

    [Fact]
    public void A_change_between_PreToolUse_and_PostToolUse_lands_on_that_command()
    {
        var reads = new Queue<Snapshot>([Snap("a"), Snap("a", "b")]);

        Assert.Equal(0, Run(Payload("PreToolUse"), (_, _, _) => reads.Dequeue()));
        Assert.Equal(0, Run(Payload("PostToolUse"), (_, _, _) => reads.Dequeue()));

        using var store = new ReceiptStore(StorePath);
        var verdict = Assert.Single(store.Verdicts());
        Assert.Equal(new Change("home", "b", ChangeKind.Added), verdict.Change);
        Assert.Equal(["toolu_1"], verdict.SuspectToolUseIds);
        Assert.Equal([new Command("session-1", "toolu_1", 1, 2)], store.Commands());
    }

    [Fact]
    public void A_PostToolBatch_payload_closes_the_calls_it_lists()
    {
        Run(Payload("PreToolUse"), (_, _, _) => Snap());
        Run("""{"session_id":"session-1","hook_event_name":"PostToolBatch","tool_calls":[{"tool_name":"Bash","tool_use_id":"toolu_1"}]}""", (_, _, _) => Snap());

        using var store = new ReceiptStore(StorePath);
        Assert.Equal([new Command("session-1", "toolu_1", 1, 1)], store.Commands());
    }

    [Theory]
    [InlineData(@"C:\Users\me\.bashrc", true)]
    [InlineData(@"C:\repo2\notes.md", true)]
    [InlineData(@"C:\repo\src\app.cs", false)]
    [InlineData(@"C:\REPO\readme.md", false)]
    public void A_file_counts_as_outside_the_folder_only_when_it_is(string file, bool outside) =>
        Assert.Equal(outside, HookRun.IsOutside(file, @"C:\repo"));

    [Fact]
    public void A_Write_outside_the_folder_puts_its_file_on_the_watch_list()
    {
        var file = Path.Combine(_dir, "outside", ".bashrc");
        IReadOnlyList<string>? watched = null;
        var payload = $$$"""
            {"session_id":"session-1","cwd":"C:\\repo","hook_event_name":"PreToolUse","tool_name":"Write","tool_use_id":"toolu_w",
             "tool_input":{"file_path":{{{System.Text.Json.JsonSerializer.Serialize(file)}}},"content":"secret contents"}}
            """;

        Run(payload, (_, _, written) => { watched = written; return Snap(); });

        Assert.Equal([file], watched);
        var stored = string.Concat(Directory.GetFiles(_dir, "tattlebun.db*").Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
        Assert.DoesNotContain("secret contents", stored);
        Assert.Contains(".bashrc", stored);
    }

    // The trust contract: a command can hold a token, so it never reaches the store.
    [Fact]
    public void The_command_never_reaches_the_store()
    {
        Run(Payload("PreToolUse"), (_, _, _) => Snap("a"));
        Run(Payload("PostToolUse"), (_, _, _) => Snap("a", "b"));

        var stored = string.Concat(Directory.GetFiles(_dir, "tattlebun.db*").Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
        Assert.DoesNotContain("fake-token-for-tests", stored);
        // Positive control: the same scan finds what the store does keep.
        Assert.Contains("toolu_1", stored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Bad_input_still_exits_0(string stdin) => Assert.Equal(0, Run(stdin, (_, _, _) => Snap()));

    [Fact]
    public void A_failed_snapshot_still_exits_0() =>
        Assert.Equal(0, Run(Payload("PreToolUse"), (_, _, _) => throw new IOException("unreadable")));

    [Fact]
    public void A_store_it_cannot_open_still_exits_0()
    {
        var blocked = Path.Combine(_dir, "a-file");
        File.WriteAllText(blocked, "");

        Assert.Equal(0, HookRun.Run(Stdin(Payload("PreToolUse")), Path.Combine(blocked, "tattlebun.db"), (_, _, _) => Snap()));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Payload(string hookEvent) =>
        $$$"""
        {"session_id":"session-1","transcript_path":"t.jsonl","cwd":"C:\\repo","hook_event_name":"{{{hookEvent}}}",
         "tool_name":"Bash","tool_use_id":"toolu_1","tool_input":{"command":"{{{Command}}}"}}
        """;

    private static Snapshot Snap(params string[] homeItems) =>
        new(DateTimeOffset.UtcNow, new Dictionary<string, Part> { ["home"] = new("s", homeItems.ToDictionary(i => i, _ => "1")) });

    private static MemoryStream Stdin(string text) => new(Encoding.UTF8.GetBytes(text));

    private int Run(string stdin, Func<Fingerprinter, Snapshot?, IReadOnlyList<string>, Snapshot> read) => HookRun.Run(Stdin(stdin), StorePath, read);
}
