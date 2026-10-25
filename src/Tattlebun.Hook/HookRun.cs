using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tattlebun.Hook;

public static class HookRun
{
    public static string DefaultLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tattlebun", "hook.log");

    // Exit code 2 from a PreToolUse hook blocks the tool call, and stdout can be read as a decision,
    // so every path returns 0 and writes nothing to stdout.
    public static int Run(Stream stdin, string logPath)
    {
        try
        {
            var input = JsonSerializer.Deserialize(stdin, HookJson.Default.HookInput);
            if (input is null)
            {
                return 0;
            }

            // The command stays out of the log: it can hold a token.
            var line = new HookLogLine(DateTimeOffset.UtcNow, input.HookEventName, input.SessionId, input.ToolName, input.ToolUseId);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            // shortcut: the log grows without bound; M1 replaces it with receipts in SQLite.
            using var log = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            log.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, HookJson.Default.HookLogLine) + "\n"));
        }
        catch (Exception)
        {
            // A lost log line is fine; a failed command is not.
        }

        return 0;
    }
}

internal sealed record HookInput(string? SessionId, string? HookEventName, string? ToolName, string? ToolUseId);

internal sealed record HookLogLine(DateTimeOffset Time, string? Event, string? Session, string? Tool, string? ToolUseId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HookInput))]
[JsonSerializable(typeof(HookLogLine))]
internal sealed partial class HookJson : JsonSerializerContext;
