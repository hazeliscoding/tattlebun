using System.Text;
using Tattlebun.Core;

namespace Tattlebun.Tests;

public class DiffTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Added_removed_and_changed_items_are_found()
    {
        var before = Snap(("env-user", "s1", [("Path", "a"), ("OLD", "x")]));
        var after = Snap(("env-user", "s2", [("Path", "b"), ("NEW", "y")]));

        var changes = Diff.Between(before, after);

        Assert.Equal(
            [new Change("env-user", "Path", ChangeKind.Changed), new Change("env-user", "NEW", ChangeKind.Added), new Change("env-user", "OLD", ChangeKind.Removed)],
            changes);
    }

    [Fact]
    public void Same_items_give_no_changes() =>
        Assert.Empty(Diff.Between(Snap(("home", "s1", [("a", "1")])), Snap(("home", "s2", [("a", "1")]))));

    // An unreadable part would otherwise look like everything in it was removed, or added.
    [Fact]
    public void A_part_missing_from_either_side_gives_no_changes()
    {
        var full = Snap(("services", "s1", [("svc", "1")]));
        var unread = Snap();

        Assert.Empty(Diff.Between(full, unread));
        Assert.Empty(Diff.Between(unread, full));
    }

    [Fact]
    public void Fingerprints_depend_on_the_key()
    {
        var value = Encoding.UTF8.GetBytes("token");

        Assert.Equal(new Fingerprinter([1]).Of(value), new Fingerprinter([1]).Of(value));
        Assert.NotEqual(new Fingerprinter([1]).Of(value), new Fingerprinter([2]).Of(value));
        Assert.DoesNotContain("token", new Fingerprinter([1]).Of(value), StringComparison.OrdinalIgnoreCase);
    }

    private static Snapshot Snap(params (string Name, string Stamp, (string Item, string Fingerprint)[] Items)[] parts) =>
        new(Time, parts.ToDictionary(p => p.Name, p => new Part(p.Stamp, p.Items.ToDictionary(i => i.Item, i => i.Fingerprint))));
}
