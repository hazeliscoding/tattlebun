using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tattlebun.Core;

namespace Tattlebun.Windows;

/// <summary>A hook call as the store needs it.</summary>
public sealed record HookCall(string Event, string Session, string ToolUseId, string Tool);

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

    public ReceiptStore(string path)
    {
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
                start_snapshot INTEGER NOT NULL, end_snapshot INTEGER);
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
        var fingerprinter = new Fingerprinter(FingerprintKey());
        var (previous, previousNumber) = LastSnapshot();
        var now = read(fingerprinter, previous);
        var number = Scalar<long>("INSERT INTO snapshots (taken) VALUES ($taken) RETURNING number", ("$taken", now.Taken.ToString("o")));

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
                "INSERT OR IGNORE INTO commands (tool_use_id, session, tool, start_snapshot) VALUES ($tool_use_id, $session, $tool, $number)",
                ("$tool_use_id", call.ToolUseId), ("$session", call.Session), ("$tool", call.Tool), ("$number", number));
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
