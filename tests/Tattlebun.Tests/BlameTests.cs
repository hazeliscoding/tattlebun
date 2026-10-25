using Tattlebun.Core;

namespace Tattlebun.Tests;

// Snapshots are numbered 1, 2, 3, ... in the order any session took them.
public class BlameTests
{
    private static readonly Change PathAdded = new("env-user", "Path", ChangeKind.Changed);

    [Fact]
    public void One_command_gets_the_changes_found_while_it_ran()
    {
        var npm = new Command("s1", "npm", 1, 2);

        Assert.Equal([npm], Blame.Suspects([npm], 1, 2));
        Assert.Equal(BlameKind.Tied, Blame.Judge([PathAdded], [npm], 1, 2).Single().Kind);
    }

    [Fact]
    public void A_command_gets_nothing_from_before_it_started_or_after_it_ended()
    {
        var npm = new Command("s1", "npm", 2, 3);

        Assert.Empty(Blame.Suspects([npm], 1, 2));
        Assert.Empty(Blame.Suspects([npm], 3, 4));
    }

    [Fact]
    public void Overlapping_commands_from_two_sessions_both_get_one_of_these()
    {
        var a = new Command("s1", "a", 1, 4);
        var b = new Command("s2", "b", 2, 3);

        Assert.Equal(BlameKind.OneOfThese, Blame.Judge([PathAdded], [a, b], 2, 3).Single().Kind);
        Assert.Equal([a, b], Blame.Suspects([a, b], 2, 3));
        // Before b started and after it ended, only a was running.
        Assert.Equal([a], Blame.Suspects([a, b], 1, 2));
        Assert.Equal([a], Blame.Suspects([a, b], 3, 4));
    }

    [Fact]
    public void A_change_while_no_command_ran_is_not_tied_to_a_session()
    {
        var done = new Command("s1", "done", 1, 2);

        Assert.Equal(BlameKind.NotTiedToASession, Blame.Judge([PathAdded], [done], 2, 3).Single().Kind);
        Assert.Equal(BlameKind.NotTiedToASession, Blame.Judge([PathAdded], [], 7, 8).Single().Kind);
    }

    [Fact]
    public void A_command_still_running_gets_later_changes()
    {
        var running = new Command("s1", "build", 1, null);

        Assert.Equal([running], Blame.Suspects([running], 5, 6));
    }

    // Its session died after snapshot 4; snapshot 5, from another session, noticed and closed it.
    [Fact]
    public void A_command_whose_session_died_gets_changes_only_up_to_the_snapshot_that_closed_it()
    {
        var died = new Command("s1", "install", 1, 5);
        var other = new Command("s2", "test", 5, 6);

        Assert.Equal([died], Blame.Suspects([died, other], 4, 5));
        Assert.Equal([other], Blame.Suspects([died, other], 5, 6));
    }
}
