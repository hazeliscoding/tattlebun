using System.Text.Json;
using System.Text.Json.Serialization;
using Tattlebun.Core;
using Tattlebun.Windows;

namespace Tattlebun.Hook;

public static class HookRun
{
    // Exit code 2 from a PreToolUse hook blocks the tool call, and stdout can be read as a decision,
    // so every path returns 0 and writes nothing to stdout.
    public static int Run(Stream stdin, string storePath, Func<Fingerprinter, Snapshot?, Snapshot> read)
    {
        try
        {
            var input = JsonSerializer.Deserialize(stdin, HookJson.Default.HookInput);
            if (input?.HookEventName is null || input.SessionId is null)
            {
                return 0;
            }

            // Only these fields reach the store; the command and its output never do.
            using var store = new ReceiptStore(storePath);
            store.Record(new HookCall(input.HookEventName, input.SessionId, input.ToolUseId ?? "", input.ToolName ?? ""), read);
        }
        catch (Exception)
        {
            // A lost snapshot only widens the next interval; a failed command is worse.
        }

        return 0;
    }
}

internal sealed record HookInput(string? SessionId, string? HookEventName, string? ToolName, string? ToolUseId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HookInput))]
internal sealed partial class HookJson : JsonSerializerContext;
