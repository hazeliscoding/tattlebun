using System.Text;
using Tattlehop.Core;
using Tattlehop.Windows;

namespace Tattlehop.Hook;

/// <summary>Recent receipts as plain text, for <c>tattlehop-hook --list</c>. It shows names only; no value is stored.</summary>
public static class ReceiptList
{
    // M1's target: 95% of snapshots under 100 ms, each timed from the hook's process start.
    public static string Timing(IReadOnlyList<double> hookTimes)
    {
        if (hookTimes.Count == 0)
        {
            return "";
        }

        var sorted = hookTimes.Order().ToList();
        var p95 = sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
        return $"{sorted.Count} snapshots: 95% took {p95:F0} ms or less, {sorted.Count(t => t < 100) * 100.0 / sorted.Count:F0}% under the 100 ms budget{Environment.NewLine}";
    }

    public static string Format(IReadOnlyList<Command> commands, IReadOnlyDictionary<string, string> tools, IReadOnlyList<StoredVerdict> verdicts, int recent = 20)
    {
        var text = new StringBuilder();
        var shown = commands.OrderByDescending(c => c.StartSnapshot).Take(recent).ToList();
        var ids = shown.Select(c => c.ToolUseId).ToHashSet();
        string Name(string id) => Printable($"{tools.GetValueOrDefault(id, "?")} {Short(id)}");

        foreach (var command in shown)
        {
            text.AppendLine($"{Name(command.ToolUseId)}, session {Printable(command.Session[..Math.Min(8, command.Session.Length)])}");
            var tied = verdicts.Where(v => v.SuspectToolUseIds is [var only] && only == command.ToolUseId).ToList();
            if (tied.Count == 0)
            {
                text.AppendLine(command.EndSnapshot is null ? "  still running" : "  no changes");
            }

            tied.ForEach(v => text.AppendLine(Line(v.Change)));
        }

        foreach (var group in verdicts.Where(v => v.SuspectToolUseIds.Count > 1 && v.SuspectToolUseIds.Any(ids.Contains)).GroupBy(v => string.Join(", ", v.SuspectToolUseIds.Select(Name))))
        {
            text.AppendLine($"One of these: {group.Key}");
            foreach (var verdict in group)
            {
                text.AppendLine(Line(verdict.Change));
            }
        }

        var untied = verdicts.Where(v => v.SuspectToolUseIds.Count == 0).TakeLast(recent).ToList();
        if (untied.Count > 0)
        {
            text.AppendLine("Not tied to a session");
            untied.ForEach(v => text.AppendLine(Line(v.Change)));
        }

        return text.Length == 0 ? "No receipts yet." + Environment.NewLine : text.ToString();
    }

    private static string Line(Change change)
    {
        var kind = change.Kind switch
        {
            ChangeKind.Added => "added",
            ChangeKind.Removed => "removed",
            _ => "changed",
        };
        // A file part is named after its path, which the item's name already shows.
        var part = change.Part.StartsWith("file|", StringComparison.Ordinal) ? "file" : change.Part;
        return $"  {kind,-8}{Printable(part),-15}{Printable(change.Name)}";
    }

    // Names come from the PC, so an agent could name a file with terminal escape codes or a bidi override and
    // make the list say something else. Those characters are shown as \u escapes instead.
    internal static string Printable(string text)
    {
        if (!text.Any(IsHidden))
        {
            return text;
        }

        var escaped = new StringBuilder();
        foreach (var c in text)
        {
            escaped.Append(IsHidden(c) ? $"\\u{(int)c:X4}" : c);
        }

        return escaped.ToString();
    }

    private static bool IsHidden(char c) =>
        char.IsControl(c) || c is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    private static string Short(string toolUseId) => toolUseId.Length > 8 ? toolUseId[^8..] : toolUseId;
}
