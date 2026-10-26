using System.Diagnostics;
using Tattlehop.Core;
using Tattlehop.Windows;

namespace Tattlehop.Tests;

public sealed class ScheduledTasksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tattlehop-tasks-").FullName;

    [Fact]
    public void The_tasks_this_user_can_see_are_listed_by_path()
    {
        var tasks = ScheduledTasks.List();

        Assert.NotEmpty(tasks);
        Assert.All(tasks.Keys, path => Assert.StartsWith(@"\", path));
    }

    // Snapshot 2 lists tasks; the command runs from 1 to 3; the next listing, at 4, finds a new task.
    [Fact]
    public void A_task_found_by_the_next_listing_is_judged_since_the_listing_before_began()
    {
        var listings = new Queue<Dictionary<string, string>>([new() { [@"\Existing"] = "1" }, new() { [@"\Existing"] = "1", [@"\Updater"] = "1" }]);
        using var store = new ReceiptStore(Path.Combine(_dir, "tattlehop.db"));
        Snapshot Empty(Fingerprinter f, Snapshot? p, IReadOnlyList<string> w) => new(DateTimeOffset.UtcNow, new Dictionary<string, Part>());

        store.Record(new HookCall("PreToolUse", "s1", "schtasks", "PowerShell"), Empty);
        store.RecordScheduledTasks(listings.Dequeue);
        store.Record(new HookCall("PostToolUse", "s1", "schtasks", "PowerShell"), Empty);
        store.RecordScheduledTasks(listings.Dequeue);

        var verdict = Assert.Single(store.Verdicts());
        Assert.Equal(new Change("tasks", @"\Updater", ChangeKind.Added), verdict.Change);
        Assert.Equal(["schtasks"], verdict.SuspectToolUseIds);
    }

    [Fact(Explicit = true)]
    public void TattlehopTest_a_task_this_user_creates_is_listed()
    {
        const string name = "TattlehopTest_Tasks";
        try
        {
            Schtasks($"/create /tn {name} /tr \"cmd /c exit\" /sc once /st 23:59 /f");

            Assert.Contains($@"\{name}", ScheduledTasks.List().Keys);
        }
        finally
        {
            Schtasks($"/delete /tn {name} /f");
        }

        Assert.DoesNotContain($@"\{name}", ScheduledTasks.List().Keys);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Schtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }
}
