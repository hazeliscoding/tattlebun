namespace Tattlebun.Core;

/// <summary>One watched tool call, placed by the snapshots around it.</summary>
/// <param name="Session">The Claude Code session ID.</param>
/// <param name="ToolUseId">The tool call's ID, which its PreToolUse and PostToolUse hooks share.</param>
/// <param name="StartSnapshot">The snapshot its PreToolUse hook took, before it ran.</param>
/// <param name="EndSnapshot">
/// The snapshot its PostToolUse hook took, or the first snapshot after its session died; null while it may still run.
/// </param>
public sealed record Command(string Session, string ToolUseId, long StartSnapshot, long? EndSnapshot);

public enum BlameKind
{
    NotTiedToASession,
    Tied,
    OneOfThese,
}

/// <summary>A change and the commands that were running while it happened.</summary>
public sealed record Verdict(Change Change, IReadOnlyList<Command> Suspects)
{
    public BlameKind Kind => Suspects.Count switch
    {
        0 => BlameKind.NotTiedToASession,
        1 => BlameKind.Tied,
        _ => BlameKind.OneOfThese,
    };
}

public static class Blame
{
    // Snapshots are numbered in the order they were taken. A change found between snapshots `from` and `to`
    // happened after the first and before the second, while any command that started before `to` and hadn't
    // ended by `from` was running. Receipts say "while", not "by".
    public static IReadOnlyList<Command> Suspects(IEnumerable<Command> commands, long from, long to) =>
        [.. commands.Where(c => c.StartSnapshot < to && (c.EndSnapshot is null || c.EndSnapshot > from))];

    public static IReadOnlyList<Verdict> Judge(IReadOnlyList<Change> changes, IEnumerable<Command> commands, long from, long to)
    {
        var suspects = Suspects(commands, from, to);
        return [.. changes.Select(change => new Verdict(change, suspects))];
    }
}
