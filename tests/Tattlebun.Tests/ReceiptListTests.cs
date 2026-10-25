using Tattlebun.Core;
using Tattlebun.Hook;
using Tattlebun.Windows;

namespace Tattlebun.Tests;

public class ReceiptListTests
{
    [Fact]
    public void Receipts_list_each_commands_changes_then_one_of_these_then_the_untied()
    {
        Command[] commands =
        [
            new("session-aaaaaaaa", "toolu_npm00001", 1, 2),
            new("session-aaaaaaaa", "toolu_noop0002", 3, 4),
            new("session-bbbbbbbb", "toolu_bgsrv003", 5, null),
        ];
        var tools = new Dictionary<string, string> { ["toolu_npm00001"] = "Bash", ["toolu_noop0002"] = "Bash", ["toolu_bgsrv003"] = "PowerShell" };
        StoredVerdict[] verdicts =
        [
            new(2, new Change("npm", "is-number", ChangeKind.Added), ["toolu_npm00001"]),
            new(6, new Change("env-user", "Path", ChangeKind.Changed), ["toolu_npm00001", "toolu_bgsrv003"]),
            new(7, new Change(@"file|C:\Users\me\.gitconfig", @"C:\Users\me\.gitconfig", ChangeKind.Removed), []),
        ];

        var text = ReceiptList.Format(commands, tools, verdicts);

        Assert.Equal(
            """
            PowerShell bgsrv003, session session-
              still running
            Bash noop0002, session session-
              no changes
            Bash npm00001, session session-
              added   npm            is-number
            One of these: Bash npm00001, PowerShell bgsrv003
              changed env-user       Path
            Not tied to a session
              removed file           C:\Users\me\.gitconfig

            """.ReplaceLineEndings(),
            text);
    }

    [Fact]
    public void Timing_reports_the_95th_percentile_and_the_share_under_budget()
    {
        double[] times = [.. Enumerable.Range(1, 19).Select(i => (double)i * 5), 250];

        Assert.Equal("20 snapshots: 95% took 95 ms or less, 95% under the 100 ms budget" + Environment.NewLine, ReceiptList.Timing(times));
    }

    [Fact]
    public void Control_and_bidi_characters_in_names_are_shown_escaped()
    {
        StoredVerdict[] verdicts = [new(2, new Change("home", "\u001b[2Jfake‮txt.exe", ChangeKind.Added), [])];

        var text = ReceiptList.Format([], new Dictionary<string, string>(), verdicts);

        Assert.Contains("\\u001B[2Jfake\\u202Etxt.exe", text);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('‮', text);
    }

    [Fact]
    public void An_empty_store_says_so() => Assert.Equal("No receipts yet." + Environment.NewLine, ReceiptList.Format([], new Dictionary<string, string>(), []));
}
