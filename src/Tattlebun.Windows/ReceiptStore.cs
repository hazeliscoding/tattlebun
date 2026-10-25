using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tattlebun.Core;

namespace Tattlebun.Windows;

/// <summary>A hook call as the store needs it.</summary>
/// <param name="Event">The hook event's name.</param>
/// <param name="Session">The Claude Code session ID.</param>
/// <param name="ToolUseId">The tool call's ID, empty for events that aren't about one call.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="BatchToolUseIds">For PostToolBatch, every call in the batch.</param>
/// <param name="Claude">The Claude Code process that ran the hook.</param>
public sealed record HookCall(string Event, string Session, string ToolUseId, string Tool, IReadOnlyList<string>? BatchToolUseIds = null, ClaudeProcess? Claude = null);

/// <summary>A process, told apart from a later one that reuses its ID by its start time.</summary>
public sealed record ClaudeProcess(int Pid, long StartedTicks)
{
    // Claude Code sets CLAUDE_PID for its hooks to its own process ID.
    public static ClaudeProcess? FromEnvironment()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_PID"), out var pid))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return new ClaudeProcess(pid, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    // A process this user can't open has taken over the ID, so this user's Claude Code is gone too.
    public static bool IsAlive(ClaudeProcess claude)
    {
        try
        {
            using var process = Process.GetProcessById(claude.Pid);
            return process.StartTime.ToUniversalTime().Ticks == claude.StartedTicks;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>One change found between two snapshots, and the commands that were running while it happened.</summary>
public sealed record StoredVerdict(long Snapshot, Change Change, IReadOnlyList<string> SuspectToolUseIds);

/// <summary>
/// Receipts, commands and the last snapshot, in one SQLite file in WAL mode. Each hook call holds the write lock
/// while it reads its snapshot, so parallel hooks take turns and snapshot numbers follow the order they were read.
/// It keeps names and keyed fingerprints, never a value.
/// </summary>
public sealed class ReceiptStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Func<ClaudeProcess, bool> _isAlive;

    public ReceiptStore(string path, Func<ClaudeProcess, bool>? isAlive = null)
    {
        _isAlive = isAlive ?? ClaudeProcess.IsAlive;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Microsoft.Data.Sqlite finds the bundled SQLite by reflection, which NativeAOT trims away.
        SQLitePCL.Batteries_V2.Init();
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS snapshots (number INTEGER PRIMARY KEY AUTOINCREMENT, taken TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commands (
                tool_use_id TEXT PRIMARY KEY, session TEXT NOT NULL, tool TEXT NOT NULL,
                start_snapshot INTEGER NOT NULL, end_snapshot INTEGER, claude_pid INTEGER, claude_started INTEGER);
            CREATE TABLE IF NOT EXISTS changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, snapshot INTEGER NOT NULL, part TEXT NOT NULL, name TEXT NOT NULL, kind INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS suspects (change_id INTEGER NOT NULL, tool_use_id TEXT NOT NULL);
            """);
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tattlebun", "tattlebun.db");

    /// <summary>Takes a snapshot with <paramref name="read"/>, records what changed since the last one, and opens or closes the call's command.</summary>
    public void Record(HookCall call, Func<Fingerprinter, Snapshot?, Snapshot> read)
    {
        // Deferred is false, so this is BEGIN IMMEDIATE: the write lock is taken before the snapshot is read.
        using var transaction = _db.BeginTransaction(deferred: false);
        if (call.Event is "PostToolBatch" or "UserPromptSubmit")
        {
            CloseUnfinished(call);
            transaction.Commit();
            return;
        }

        var fingerprinter = new Fingerprinter(FingerprintKey());
        var (previous, previousNumber) = LastSnapshot();
        var now = read(fingerprinter, previous);
        var number = Scalar<long>("INSERT INTO snapshots (taken) VALUES ($taken) RETURNING number", ("$taken", now.Taken.ToString("o")));

        // The commands of a session that died end at this snapshot, the first to notice.
        foreach (var claude in OpenClaudeProcesses().Where(c => !_isAlive(c)))
        {
            Execute(
                "UPDATE commands SET end_snapshot = $number WHERE claude_pid = $pid AND claude_started = $started AND end_snapshot IS NULL",
                ("$number", number), ("$pid", claude.Pid), ("$started", claude.StartedTicks));
        }

        if (previous is not null)
        {
            var commands = Commands("WHERE end_snapshot IS NULL OR end_snapshot > $from", ("$from", previousNumber));
            foreach (var verdict in Blame.Judge(Diff.Between(previous, now), commands, previousNumber, number))
            {
                var id = Scalar<long>(
                    "INSERT INTO changes (snapshot, part, name, kind) VALUES ($snapshot, $part, $name, $kind) RETURNING id",
                    ("$snapshot", number), ("$part", verdict.Change.Part), ("$name", verdict.Change.Name), ("$kind", (int)verdict.Change.Kind));
                foreach (var suspect in verdict.Suspects)
                {
                    Execute("INSERT INTO suspects (change_id, tool_use_id) VALUES ($id, $tool_use_id)", ("$id", id), ("$tool_use_id", suspect.ToolUseId));
                }
            }
        }

        if (call.Event == "PreToolUse")
        {
            Execute(
                """
                INSERT OR IGNORE INTO commands (tool_use_id, session, tool, start_snapshot, claude_pid, claude_started)
                VALUES ($tool_use_id, $session, $tool, $number, $pid, $started)
                """,
                ("$tool_use_id", call.ToolUseId), ("$session", call.Session), ("$tool", call.Tool), ("$number", number),
                ("$pid", (object?)call.Claude?.Pid ?? DBNull.Value), ("$started", (object?)call.Claude?.StartedTicks ?? DBNull.Value));
        }
        else if (call.Event is "PostToolUse" or "PostToolUseFailure")
        {
            Execute("UPDATE commands SET end_snapshot = $number WHERE tool_use_id = $tool_use_id AND end_snapshot IS NULL", ("$number", number), ("$tool_use_id", call.ToolUseId));
        }

        Execute(
            "INSERT INTO meta (key, value) VALUES ('last', $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$value", JsonSerializer.SerializeToUtf8Bytes(new LastSnapshot(number, now), StoreJson.Default.LastSnapshot)));
        transaction.Commit();
    }

    public IReadOnlyList<Command> Commands() => Commands("");

    // A denied call fires PreToolUse and nothing else, and an interrupted turn can skip PostToolBatch too. These end
    // at the latest snapshot without taking a new one: a change such a call made after it goes unblamed, which is
    // better than blaming the wrong command, and batches and prompts cost no snapshot.
    private void CloseUnfinished(HookCall call)
    {
        var latest = Scalar<long>("SELECT coalesce(max(number), 0) FROM snapshots");
        if (call.Event == "UserPromptSubmit")
        {
            Execute("UPDATE commands SET end_snapshot = $latest WHERE session = $session AND end_snapshot IS NULL", ("$latest", latest), ("$session", call.Session));
            return;
        }

        foreach (var id in call.BatchToolUseIds ?? [])
        {
            Execute("UPDATE commands SET end_snapshot = $latest WHERE tool_use_id = $id AND end_snapshot IS NULL", ("$latest", latest), ("$id", id));
        }
    }

    private List<ClaudeProcess> OpenClaudeProcesses()
    {
        var processes = new List<ClaudeProcess>();
        Each(
            "SELECT DISTINCT claude_pid, claude_started FROM commands WHERE end_snapshot IS NULL AND claude_pid IS NOT NULL",
            row => processes.Add(new ClaudeProcess(row.GetInt32(0), row.GetInt64(1))));
        return processes;
    }

    public IReadOnlyList<StoredVerdict> Verdicts()
    {
        var suspects = new Dictionary<long, List<string>>();
        Each("SELECT change_id, tool_use_id FROM suspects ORDER BY rowid", row =>
            (suspects.TryGetValue(row.GetInt64(0), out var list) ? list : suspects[row.GetInt64(0)] = []).Add(row.GetString(1)));

        var verdicts = new List<StoredVerdict>();
        Each("SELECT id, snapshot, part, name, kind FROM changes ORDER BY id", row => verdicts.Add(new StoredVerdict(
            row.GetInt64(1),
            new Change(row.GetString(2), row.GetString(3), (ChangeKind)row.GetInt32(4)),
            suspects.TryGetValue(row.GetInt64(0), out var list) ? list : [])));
        return verdicts;
    }

    public void Dispose() => _db.Dispose();

    // A random key, kept encrypted with DPAPI for the current user, so fingerprints mean nothing off this account.
    private byte[] FingerprintKey()
    {
        Execute(
            "INSERT OR IGNORE INTO meta (key, value) VALUES ('fingerprint_key', $value)",
            ("$value", ProtectedData.Protect(RandomNumberGenerator.GetBytes(32), null, DataProtectionScope.CurrentUser)));
        return ProtectedData.Unprotect(Scalar<byte[]>("SELECT value FROM meta WHERE key = 'fingerprint_key'"), null, DataProtectionScope.CurrentUser);
    }

    private (Snapshot? Snapshot, long Number) LastSnapshot()
    {
        LastSnapshot? last = null;
        Each("SELECT value FROM meta WHERE key = 'last'", row => last = JsonSerializer.Deserialize(row.GetFieldValue<byte[]>(0), StoreJson.Default.LastSnapshot));
        return last is null ? (null, 0) : (last.Snapshot, last.Number);
    }

    private List<Command> Commands(string where, params (string Name, object Value)[] parameters)
    {
        var commands = new List<Command>();
        Each(
            $"SELECT session, tool_use_id, start_snapshot, end_snapshot FROM commands {where} ORDER BY start_snapshot",
            row => commands.Add(new Command(row.GetString(0), row.GetString(1), row.GetInt64(2), row.IsDBNull(3) ? null : row.GetInt64(3))),
            parameters);
        return commands;
    }

    private SqliteCommand Prepare(string sql, (string Name, object Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        return (T)command.ExecuteScalar()!;
    }

    private void Each(string sql, Action<SqliteDataReader> row, params (string Name, object Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            row(reader);
        }
    }
}

internal sealed record LastSnapshot(long Number, Snapshot Snapshot);

[JsonSerializable(typeof(LastSnapshot))]
internal sealed partial class StoreJson : JsonSerializerContext;
