using System.Text;
using Tattlebun.Hook;

namespace Tattlebun.Tests;

public sealed class HookTests : IDisposable
{
    private const string PreToolUse = """
        {"session_id":"session-1","transcript_path":"t.jsonl","cwd":"C:\\repo","hook_event_name":"PreToolUse",
         "tool_name":"Bash","tool_use_id":"toolu_1","tool_input":{"command":"set NPM_TOKEN=fake-token-for-tests"}}
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("tattlebun-hook-").FullName;

    private string LogPath => Path.Combine(_dir, "hook.log");

    [Fact]
    public void A_payload_appends_one_line_without_the_command()
    {
        Assert.Equal(0, Run(PreToolUse, LogPath));

        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.Contains("\"session\":\"session-1\"", line);
        Assert.Contains("\"tool_use_id\":\"toolu_1\"", line);
        Assert.DoesNotContain("fake-token-for-tests", line);
    }

    [Fact]
    public void Each_call_appends_a_line()
    {
        Run(PreToolUse, LogPath);
        Run(PreToolUse.Replace("PreToolUse", "PostToolUse"), LogPath);

        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Bad_input_still_exits_0(string stdin) => Assert.Equal(0, Run(stdin, LogPath));

    [Fact]
    public void A_log_it_cannot_write_still_exits_0()
    {
        var blocked = Path.Combine(_dir, "a-file");
        File.WriteAllText(blocked, "");

        Assert.Equal(0, Run(PreToolUse, Path.Combine(blocked, "hook.log")));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static int Run(string stdin, string logPath) =>
        HookRun.Run(new MemoryStream(Encoding.UTF8.GetBytes(stdin)), logPath);
}
