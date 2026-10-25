using System.Text;
using Tattlebun.Core;
using Tattlebun.Windows;

namespace Tattlebun.Hook;

/// <summary>Recent receipts as plain text, for <c>tattlebun-hook --list</c>. It shows names only; no value is stored.</summary>
public static class ReceiptList
{
    public static string Format(IReadOnlyList<Command> commands, IReadOnlyDictionary<string, string> tools, IReadOnlyList<StoredVerdict> verdicts, int recent = 20)
    {
        var text = new StringBuilder();
        var shown = commands.OrderByDescending(c => c.StartSnapshot).Take(recent).ToList();
        var ids = shown.Select(c => c.ToolUseId).ToHashSet();
        string Name(string id) => $"{tools.GetValueOrDefault(id, "?")} {Short(id)}";

        foreach (var command in shown)
        {
            text.AppendLine($"{Name(command.ToolUseId)}, session {command.Session[..Math.Min(8, command.Session.Length)]}");
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
        return $"  {kind,-8}{part,-15}{change.Name}";
    }

    private static string Short(string toolUseId) => toolUseId.Length > 8 ? toolUseId[^8..] : toolUseId;
}
