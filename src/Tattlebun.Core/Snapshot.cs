using System.Security.Cryptography;

namespace Tattlebun.Core;

/// <summary>One watched place, such as the user's environment variables or the top of the home folder.</summary>
/// <param name="Stamp">Last-write times of what the part reads; an unchanged stamp lets a reader reuse the part.</param>
/// <param name="Items">Each item's name and its fingerprint.</param>
public sealed record Part(string Stamp, IReadOnlyDictionary<string, string> Items);

/// <summary>The watch list at one moment. A part that couldn't be read is missing.</summary>
public sealed record Snapshot(DateTimeOffset Taken, IReadOnlyDictionary<string, Part> Parts);

public enum ChangeKind
{
    Added,
    Removed,
    Changed,
}

public sealed record Change(string Part, string Name, ChangeKind Kind);

public static class Diff
{
    // A part missing from either snapshot couldn't be read then, so it says nothing about what changed.
    public static IReadOnlyList<Change> Between(Snapshot before, Snapshot after)
    {
        var changes = new List<Change>();
        foreach (var (name, now) in after.Parts)
        {
            if (!before.Parts.TryGetValue(name, out var then) || ReferenceEquals(then, now))
            {
                continue;
            }

            foreach (var (item, fingerprint) in now.Items)
            {
                if (!then.Items.TryGetValue(item, out var old))
                {
                    changes.Add(new Change(name, item, ChangeKind.Added));
                }
                else if (old != fingerprint)
                {
                    changes.Add(new Change(name, item, ChangeKind.Changed));
                }
            }

            changes.AddRange(then.Items.Keys.Where(item => !now.Items.ContainsKey(item)).Select(item => new Change(name, item, ChangeKind.Removed)));
        }

        return changes;
    }
}

/// <summary>
/// Fingerprints values without storing them. The hash is keyed, so a stored fingerprint can't be checked
/// against guessed values, such as a short token, without the key.
/// </summary>
public sealed class Fingerprinter(byte[] key)
{
    public string Of(ReadOnlySpan<byte> value) => Convert.ToHexString(HMACSHA256.HashData(key, value), 0, 16);
}
