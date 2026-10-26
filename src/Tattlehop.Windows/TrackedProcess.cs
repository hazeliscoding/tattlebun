using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace Tattlehop.Windows;

/// <summary>A process, told apart from a later one that reuses its ID by its start time.</summary>
public sealed record TrackedProcess(int Pid, long StartedTicks)
{
    // Claude Code sets CLAUDE_PID for its hooks to its own process ID.
    public static TrackedProcess? ClaudeCode() =>
        int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_PID"), out var pid) ? Of(pid) : null;

    // A process this user can't open has taken over the ID, so the one it tracked is gone.
    public static bool IsAlive(TrackedProcess tracked) => Of(tracked.Pid) == tracked;

    /// <summary>The processes <paramref name="parent"/> started that still run, from a Toolhelp snapshot.</summary>
    public static unsafe IReadOnlyList<TrackedProcess> ChildrenOf(TrackedProcess parent)
    {
        var snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if ((nint)snapshot.Value == -1)
        {
            return [];
        }

        var children = new List<TrackedProcess>();
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            for (var more = PInvoke.Process32FirstW(snapshot, &entry); more; more = PInvoke.Process32NextW(snapshot, &entry))
            {
                // A child started before its parent belongs to an earlier process that had the parent's ID.
                if (entry.th32ParentProcessID == parent.Pid && Of((int)entry.th32ProcessID) is { } child && child.StartedTicks >= parent.StartedTicks)
                {
                    children.Add(child);
                }
            }
        }
        finally
        {
            PInvoke.CloseHandle(snapshot);
        }

        return children;
    }

    private static TrackedProcess? Of(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new TrackedProcess(pid, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
