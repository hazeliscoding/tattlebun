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
/// <param name="BackgroundShells">
/// For a background command's PostToolUse, Claude Code's other children: its shell is among those that started after the command.
/// </param>
/// <param name="WrittenFile">For a Write or Edit call's PreToolUse, its file when that's outside the session's working folder.</param>
public sealed record HookCall(
    string Event, string Session, string ToolUseId, string Tool, IReadOnlyList<string>? BatchToolUseIds = null,
    TrackedProcess? Claude = null, IReadOnlyList<TrackedProcess>? BackgroundShells = null, string? WrittenFile = null);

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
    private readonly Func<TrackedProcess, bool> _isAlive;

    public ReceiptStore(string path, Func<TrackedProcess, bool>? isAlive = null)
    {
        _isAlive = isAlive ?? TrackedProcess.IsAlive;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Microsoft.Data.Sqlite finds the bundled SQLite by reflection, which NativeAOT trims away.
        SQLitePCL.Batteries_V2.Init();
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS snapshots (number INTEGER PRIMARY KEY AUTOINCREMENT, taken_utc_ticks INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS commands (
                tool_use_id TEXT PRIMARY KEY, session TEXT NOT NULL, tool TEXT NOT NULL,
                start_snapshot INTEGER NOT NULL, end_snapshot INTEGER, claude_pid INTEGER, claude_started INTEGER);
            CREATE TABLE IF NOT EXISTS changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, snapshot INTEGER NOT NULL, part TEXT NOT NULL, name TEXT NOT NULL, kind INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS suspects (change_id INTEGER NOT NULL, tool_use_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS background_shells (tool_use_id TEXT NOT NULL, pid INTEGER NOT NULL, started INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS written_files (path TEXT PRIMARY KEY COLLATE NOCASE);
            """);
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tattlebun", "tattlebun.db");

    /// <summary>Takes a snapshot with <paramref name="read"/>, records what changed since the last one, and opens or closes the call's command.</summary>
    public void Record(HookCall call, Func<Fingerprinter, Snapshot?, IReadOnlyList<string>, Snapshot> read)
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

        // A file an agent writes outside its folder joins the watch list before this snapshot, which becomes its
        // baseline, so the call's PostToolUse finds what it did to it. It stays watched after that.
        if (call.WrittenFile is not null)
        {
            Execute("INSERT OR IGNORE INTO written_files (path) VALUES ($path)", ("$path", call.WrittenFile));
        }

        var written = new List<string>();
        Each("SELECT path FROM written_files ORDER BY path", row => written.Add(row.GetString(0)));
        var now = read(fingerprinter, previous, written);
        var number = Scalar<long>("INSERT INTO snapshots (taken_utc_ticks) VALUES ($taken) RETURNING number", ("$taken", now.Taken.UtcTicks));

        // The commands of a session that died end at this snapshot, the first to notice.
        foreach (var claude in OpenClaudeCodes().Where(c => !_isAlive(c)))
        {
            Execute(
                "UPDATE commands SET end_snapshot = $number WHERE claude_pid = $pid AND claude_started = $started AND end_snapshot IS NULL",
                ("$number", number), ("$pid", claude.Pid), ("$started", claude.StartedTicks));
        }

        // So do background commands whose shells have all exited.
        foreach (var (id, shells) in OpenBackgroundShells().Where(b => !b.Shells.Any(_isAlive)))
        {
            Execute("UPDATE commands SET end_snapshot = $number WHERE tool_use_id = $id AND end_snapshot IS NULL", ("$number", number), ("$id", id));
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
        else if (call.Event is "PostToolUse" or "PostToolUseFailure" && !KeepRunningInBackground(call))
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
        const string StillOpen = "end_snapshot IS NULL AND tool_use_id NOT IN (SELECT tool_use_id FROM background_shells)";
        if (call.Event == "UserPromptSubmit")
        {
            Execute($"UPDATE commands SET end_snapshot = $latest WHERE session = $session AND {StillOpen}", ("$latest", latest), ("$session", call.Session));
            return;
        }

        foreach (var id in call.BatchToolUseIds ?? [])
        {
            Execute($"UPDATE commands SET end_snapshot = $latest WHERE tool_use_id = $id AND {StillOpen}", ("$latest", latest), ("$id", id));
        }
    }

    // A background command's PostToolUse comes as soon as it starts. Its shell is one of Claude Code's children that
    // started after the command's own snapshot, and the command stays open until those have all exited. Another
    // call's process can slip in, but it exits within seconds.
    private bool KeepRunningInBackground(HookCall call)
    {
        if (call.BackgroundShells is not { Count: > 0 })
        {
            return false;
        }

        var startedAt = Scalar<long?>(
            "SELECT s.taken_utc_ticks FROM commands c JOIN snapshots s ON s.number = c.start_snapshot WHERE c.tool_use_id = $id", ("$id", call.ToolUseId));
        var shells = (call.BackgroundShells ?? []).Where(p => startedAt is not null && p.StartedTicks >= startedAt).ToList();
        foreach (var shell in shells)
        {
            Execute(
                "INSERT INTO background_shells (tool_use_id, pid, started) VALUES ($id, $pid, $started)",
                ("$id", call.ToolUseId), ("$pid", shell.Pid), ("$started", shell.StartedTicks));
        }

        return shells.Count > 0;
    }

    private List<(string Id, List<TrackedProcess> Shells)> OpenBackgroundShells()
    {
        var shells = new Dictionary<string, List<TrackedProcess>>();
        Each(
            "SELECT b.tool_use_id, b.pid, b.started FROM background_shells b JOIN commands c ON c.tool_use_id = b.tool_use_id WHERE c.end_snapshot IS NULL",
            row => (shells.TryGetValue(row.GetString(0), out var list) ? list : shells[row.GetString(0)] = []).Add(new TrackedProcess(row.GetInt32(1), row.GetInt64(2))));
        return [.. shells.Select(p => (p.Key, p.Value))];
    }

    private List<TrackedProcess> OpenClaudeCodes()
    {
        var processes = new List<TrackedProcess>();
        Each(
            "SELECT DISTINCT claude_pid, claude_started FROM commands WHERE end_snapshot IS NULL AND claude_pid IS NOT NULL",
            row => processes.Add(new TrackedProcess(row.GetInt32(0), row.GetInt64(1))));
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
