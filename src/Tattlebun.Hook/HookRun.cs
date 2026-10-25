using System.Text.Json;
using System.Text.Json.Serialization;
using Tattlebun.Core;
using Tattlebun.Windows;

namespace Tattlebun.Hook;

public static class HookRun
{
    // Exit code 2 from a PreToolUse hook blocks the tool call, and stdout can be read as a decision,
    // so every path returns 0 and writes nothing to stdout.
    public static int Run(Stream stdin, string storePath, Func<Fingerprinter, Snapshot?, IReadOnlyList<string>, Snapshot> read)
    {
        try
        {
            var input = JsonSerializer.Deserialize(stdin, HookJson.Default.HookInput);
            if (input?.HookEventName is null || input.SessionId is null)
            {
                return 0;
            }

            var claude = input.HookEventName is "PreToolUse" or "PostToolUse" ? TrackedProcess.ClaudeCode() : null;
            var background = input.HookEventName == "PostToolUse"
                && input.ToolResponse is { ValueKind: JsonValueKind.Object } response && response.TryGetProperty("backgroundTaskId", out _);

            // Only these fields reach the store; the command and its output never do.
            using var store = new ReceiptStore(storePath);
            store.Record(
                new HookCall(
                    input.HookEventName, input.SessionId, input.ToolUseId ?? "", input.ToolName ?? "",
                    [.. input.ToolCalls?.Select(c => c.ToolUseId).OfType<string>() ?? []],
                    claude,
                    background && claude is not null ? [.. TrackedProcess.ChildrenOf(claude).Where(p => p.Pid != Environment.ProcessId)] : null,
                    input is { HookEventName: "PreToolUse", ToolName: "Write" or "Edit", Cwd: { } cwd, ToolInput: { ValueKind: JsonValueKind.Object } toolInput }
                        && toolInput.TryGetProperty("file_path", out var file) && file.GetString() is { } path
                        && Path.IsPathFullyQualified(path) && Path.GetFullPath(path) is var full && IsLocal(full) && IsOutside(full, cwd)
                        ? full
                        : null),
                read);
        }
        catch (Exception)
        {
            // A lost snapshot only widens the next interval; a failed command is worse.
        }

        return 0;
    }

    // A written file is read at every snapshot after, so only a plain path on a local fixed drive is kept. A UNC path,
    // a \\?\ or \\.\ device path (CON resolves to \\.\CON) or a mapped network drive would make each hook reach the
    // network, which could leak the user's NTLM hash and stall the agent.
    public static bool IsLocal(string fullPath) =>
        fullPath.Length > 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\'
        && !DeviceNames.Contains(Path.GetFileNameWithoutExtension(fullPath).TrimEnd(' ', '.'))
        && new DriveInfo(fullPath[..3]).DriveType == DriveType.Fixed;

    // Windows 10 still opens these as devices in any folder, and reading CON would wait on the console.
    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" }), "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    // The trailing separator keeps C:\repo2 from counting as inside C:\repo.
    public static bool IsOutside(string file, string folder) =>
        !Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

internal sealed record HookInput(
    string? SessionId, string? HookEventName, string? ToolName, string? ToolUseId, string? Cwd,
    IReadOnlyList<BatchCall>? ToolCalls, JsonElement? ToolInput, JsonElement? ToolResponse);

internal sealed record BatchCall(string? ToolUseId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HookInput))]
internal sealed partial class HookJson : JsonSerializerContext;
