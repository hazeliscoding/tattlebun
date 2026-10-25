using System.Text;
using Tattlebun.Core;
using Tattlebun.Windows;

namespace Tattlebun.Tests;

public sealed class ReceiptStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tattlebun-store-").FullName;

    private string StorePath => Path.Combine(_dir, "tattlebun.db");

    [Fact]
    public void The_first_snapshot_is_a_baseline_with_no_changes()
    {
        using var store = new ReceiptStore(StorePath);
        store.Record(Call("PreToolUse", "s1", "a"), (_, _, _) => Snap("x", "y"));

        Assert.Empty(store.Verdicts());
    }

    [Fact]
    public void Overlapping_commands_from_two_sessions_both_get_one_of_these()
    {
        using var store = new ReceiptStore(StorePath);
        store.Record(Call("PreToolUse", "s1", "a"), (_, _, _) => Snap());
        store.Record(Call("PreToolUse", "s2", "b"), (_, _, _) => Snap());
        store.Record(Call("PostToolUse", "s2", "b"), (_, _, _) => Snap("npm-package"));
        store.Record(Call("PostToolUse", "s1", "a"), (_, _, _) => Snap("npm-package"));

        var verdict = Assert.Single(store.Verdicts());
        Assert.Equal(["a", "b"], verdict.SuspectToolUseIds);
    }

    [Fact]
    public void A_change_after_every_command_ended_is_not_tied_to_a_session()
    {
        using var store = new ReceiptStore(StorePath);
        store.Record(Call("PreToolUse", "s1", "a"), (_, _, _) => Snap());
        store.Record(Call("PostToolUse", "s1", "a"), (_, _, _) => Snap());
        store.Record(Call("PreToolUse", "s1", "b"), (_, _, _) => Snap("startup-entry"));

        Assert.Empty(Assert.Single(store.Verdicts()).SuspectToolUseIds);
    }

    // Parallel tool calls run several hooks at once, each with its own connection.
    [Fact]
    public void Parallel_hooks_take_turns_and_number_their_snapshots_in_order()
    {
        Parallel.For(0, 12, i =>
        {
            using var store = new ReceiptStore(StorePath);
            store.Record(Call("PreToolUse", $"s{i}", $"t{i}"), (_, _, _) => Snap($"item{i}"));
        });

        using var check = new ReceiptStore(StorePath);
        Assert.Equal(Enumerable.Range(1, 12).Select(n => (long)n), check.Commands().Select(c => c.StartSnapshot).Order());
    }

    [Fact]
    public void The_fingerprint_key_outlives_the_connection()
    {
        var value = Encoding.UTF8.GetBytes("value");
        string? first = null, second = null;
        using (var store = new ReceiptStore(StorePath))
        {
            store.Record(Call("PreToolUse", "s1", "a"), (f, _, _) => { first = f.Of(value); return Snap(); });
        }

        using (var store = new ReceiptStore(StorePath))
        {
            store.Record(Call("PreToolUse", "s1", "b"), (f, _, _) => { second = f.Of(value); return Snap(); });
        }

        Assert.Equal(first, second);
    }

    // A denied call fires PreToolUse and nothing else; its batch's PostToolBatch closes it.
    [Fact]
    public void PostToolBatch_closes_a_call_that_never_got_PostToolUse()
    {
        using var store = new ReceiptStore(StorePath);
        store.Record(Call("PreToolUse", "s1", "denied"), (_, _, _) => Snap());
        store.Record(new HookCall("PostToolBatch", "s1", "", "", ["denied"]), (_, _, _) => throw new InvalidOperationException("takes no snapshot"));
        store.Record(Call("PreToolUse", "s1", "next"), (_, _, _) => Snap("made-by-someone-else"));

        Assert.Equal(new Command("s1", "denied", 1, 1), store.Commands()[0]);
        Assert.Empty(Assert.Single(store.Verdicts()).SuspectToolUseIds);
    }

    [Fact]
    public void UserPromptSubmit_closes_only_that_sessions_open_commands()
    {
        using var store = new ReceiptStore(StorePath);
        store.Record(Call("PreToolUse", "s1", "interrupted"), (_, _, _) => Snap());
        store.Record(Call("PreToolUse", "s2", "running"), (_, _, _) => Snap());
        store.Record(new HookCall("UserPromptSubmit", "s1", "", ""), (_, _, _) => throw new InvalidOperationException("takes no snapshot"));

        Assert.Equal([new Command("s1", "interrupted", 1, 2), new Command("s2", "running", 2, null)], store.Commands());
    }

    // The session behind "install" died after snapshot 1; snapshot 2, from another session, notices and closes it.
    [Fact]
    public void A_dead_sessions_commands_end_at_the_first_snapshot_that_notices()
    {
        var dead = new TrackedProcess(111, 5);
        using var store = new ReceiptStore(StorePath, isAlive: claude => claude != dead);
        store.Record(Call("PreToolUse", "s1", "install") with { Claude = dead }, (_, _, _) => Snap());
        store.Record(Call("PreToolUse", "s2", "test") with { Claude = new TrackedProcess(222, 6) }, (_, _, _) => Snap("during-install"));
        store.Record(Call("PostToolUse", "s2", "test"), (_, _, _) => Snap("during-install", "after-it-died"));

        Assert.Equal(new Command("s1", "install", 1, 2), store.Commands()[0]);
        Assert.Equal([["install"], ["test"]], store.Verdicts().Select(v => v.SuspectToolUseIds));
    }

    [Fact]
    public void A_written_file_lands_on_its_calls_receipt_and_stays_watched()
    {
        const string file = @"C:\Users\me\.bashrc";
        var fingerprint = "v1";
        Snapshot Read(IReadOnlyList<string> written) =>
            new(DateTimeOffset.UtcNow, written.ToDictionary(f => $"file|{f}", f => new Part(fingerprint, new Dictionary<string, string> { [f] = fingerprint })));
        using var store = new ReceiptStore(StorePath);

        store.Record(Call("PreToolUse", "s1", "write") with { WrittenFile = file }, (_, _, written) => Read(written));
        fingerprint = "v2";
        store.Record(Call("PostToolUse", "s1", "write"), (_, _, written) => Read(written));
        fingerprint = "v3";
        store.Record(Call("PreToolUse", "s1", "later"), (_, _, written) => Read(written));

        Assert.Equal(
            [(new Change($"file|{file}", file, ChangeKind.Changed), new[] { "write" }), (new Change($"file|{file}", file, ChangeKind.Changed), Array.Empty<string>())],
            store.Verdicts().Select(v => (v.Change, v.SuspectToolUseIds.ToArray())));
    }

    [Fact]
    public void A_background_command_stays_open_until_its_shell_exits()
    {
        var running = new HashSet<TrackedProcess>();
        using var store = new ReceiptStore(StorePath, isAlive: running.Contains);
        store.Record(Call("PreToolUse", "s1", "dev-server"), (_, _, _) => Snap());
        var shell = new TrackedProcess(333, DateTime.UtcNow.Ticks);
        running.Add(shell);
        store.Record(Call("PostToolUse", "s1", "dev-server") with { BackgroundShells = [shell] }, (_, _, _) => Snap());
        store.Record(new HookCall("PostToolBatch", "s1", "", "", ["dev-server"]), (_, _, _) => Snap());
        store.Record(new HookCall("UserPromptSubmit", "s1", "", ""), (_, _, _) => Snap());
        store.Record(Call("PreToolUse", "s1", "test"), (_, _, _) => Snap("while-the-server-ran"));

        Assert.Null(store.Commands().Single(c => c.ToolUseId == "dev-server").EndSnapshot);

        running.Remove(shell);
        store.Record(Call("PostToolUse", "s1", "test"), (_, _, _) => Snap("while-the-server-ran"));

        Assert.Equal(4, store.Commands().Single(c => c.ToolUseId == "dev-server").EndSnapshot);
        Assert.Equal(["dev-server"], store.Verdicts().Single().SuspectToolUseIds);
    }

    // A process Claude Code started before the command, such as an MCP server, isn't its shell.
    [Fact]
    public void An_older_process_is_not_taken_for_a_background_commands_shell()
    {
        var mcpServer = new TrackedProcess(444, DateTime.UtcNow.AddMinutes(-5).Ticks);
        using var store = new ReceiptStore(StorePath, isAlive: _ => true);
        store.Record(Call("PreToolUse", "s1", "bg"), (_, _, _) => Snap());
        store.Record(Call("PostToolUse", "s1", "bg") with { BackgroundShells = [mcpServer] }, (_, _, _) => Snap());

        Assert.Equal(2, store.Commands().Single().EndSnapshot);
    }

    [Fact]
    public void A_child_process_is_found_with_its_start_time()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var parent = new TrackedProcess(me.Id, me.StartTime.ToUniversalTime().Ticks);
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.Contains(TrackedProcess.ChildrenOf(parent), p => p.Pid == child.Id && TrackedProcess.IsAlive(p));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void A_live_process_is_alive_and_a_reused_id_is_not()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var started = me.StartTime.ToUniversalTime().Ticks;

        Assert.True(TrackedProcess.IsAlive(new TrackedProcess(me.Id, started)));
        Assert.False(TrackedProcess.IsAlive(new TrackedProcess(me.Id, started - 1)));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HookCall Call(string hookEvent, string session, string toolUseId) => new(hookEvent, session, toolUseId, "Bash");

    private static Snapshot Snap(params string[] homeItems) =>
        new(DateTimeOffset.UtcNow, new Dictionary<string, Part> { ["home"] = new("s", homeItems.ToDictionary(i => i, _ => "1")) });
}
