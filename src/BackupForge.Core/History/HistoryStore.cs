using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using Microsoft.Data.Sqlite;

namespace BackupForge.Core.History;

public sealed class RunRecord
{
    public long Id { get; init; }
    public string JobId { get; init; } = "";
    public string? ChainRunId { get; init; }
    public string Kind { get; init; } = RunKinds.Backup;
    public string Trigger { get; init; } = RunTriggers.Manual;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Result { get; init; }
    public string Summary { get; init; } = "";
    public long FilesProcessed { get; init; }
    public long FilesCopied { get; init; }
    public long FilesVersioned { get; init; }
    public long FilesDeleted { get; init; }
    public long FilesSkipped { get; init; }
    public long BytesProcessed { get; init; }
    public long BytesTransferred { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public string? DetailsJson { get; init; }
    public bool IsFinished => EndedAt is not null;
}

public sealed record ConfigChange(long Id, DateTimeOffset At, string By, string Summary, string? DiffJson);

public sealed record NotificationRecord(long Id, DateTimeOffset At, string Kind, string? JobId, string? Result, string Status, string? Error);

/// <summary>history.db (SQLite, SPEC 9.1). Thread-safe; every call uses its own pooled connection.</summary>
public sealed class HistoryStore : IKeyValueStore
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public HistoryStore(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Migrate();
    }

    public string Path { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode = WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                chain_run_id TEXT,
                kind TEXT NOT NULL,
                trigger TEXT NOT NULL,
                started_at INTEGER NOT NULL,
                ended_at INTEGER,
                result TEXT,
                summary TEXT NOT NULL DEFAULT '',
                files_processed INTEGER NOT NULL DEFAULT 0,
                files_copied INTEGER NOT NULL DEFAULT 0,
                files_versioned INTEGER NOT NULL DEFAULT 0,
                files_deleted INTEGER NOT NULL DEFAULT 0,
                files_skipped INTEGER NOT NULL DEFAULT 0,
                bytes_processed INTEGER NOT NULL DEFAULT 0,
                bytes_transferred INTEGER NOT NULL DEFAULT 0,
                error_count INTEGER NOT NULL DEFAULT 0,
                warning_count INTEGER NOT NULL DEFAULT 0,
                details_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_runs_job ON runs (job_id, kind, started_at);
            CREATE INDEX IF NOT EXISTS ix_runs_started ON runs (started_at);
            CREATE TABLE IF NOT EXISTS run_items (
                run_id INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                level TEXT NOT NULL,
                path TEXT,
                message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_items_run ON run_items (run_id);
            CREATE TABLE IF NOT EXISTS sql_db_results (
                run_id INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                instance TEXT NOT NULL,
                db TEXT NOT NULL,
                system INTEGER NOT NULL,
                result TEXT NOT NULL,
                backup_file TEXT,
                size_bytes INTEGER,
                duration_s REAL,
                verify_ok INTEGER,
                error TEXT,
                recovery_model TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_sqldb ON sql_db_results (db, run_id);
            CREATE TABLE IF NOT EXISTS config_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at INTEGER NOT NULL,
                by TEXT NOT NULL,
                summary TEXT NOT NULL,
                diff_json TEXT
            );
            CREATE TABLE IF NOT EXISTS vss_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at INTEGER NOT NULL,
                run_id INTEGER,
                snapshot_id TEXT NOT NULL,
                set_id TEXT,
                deleted_at INTEGER
            );
            CREATE TABLE IF NOT EXISTS notifications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at INTEGER NOT NULL,
                kind TEXT NOT NULL,
                job_id TEXT,
                result TEXT,
                status TEXT NOT NULL,
                error TEXT,
                fingerprint TEXT
            );
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        Exec(c, $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema', '{SchemaVersion}');");
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    // ---------------- runs

    public long StartRun(string jobId, string kind, string trigger, string? chainRunId, DateTimeOffset startedUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO runs (job_id, chain_run_id, kind, trigger, started_at) VALUES ($j, $c, $k, $t, $s); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$j", jobId);
        cmd.Parameters.AddWithValue("$c", (object?)chainRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", trigger);
        cmd.Parameters.AddWithValue("$s", Ms(startedUtc));
        return (long)cmd.ExecuteScalar()!;
    }

    public void FinishRun(long runId, DateTimeOffset endedUtc, RunOutcome o)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE runs SET ended_at=$e, result=$r, summary=$s, files_processed=$fp, files_copied=$fc, files_versioned=$fv,
                  files_deleted=$fd, files_skipped=$fs, bytes_processed=$bp, bytes_transferred=$bt, error_count=$ec,
                  warning_count=$wc, details_json=$dj WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$e", Ms(endedUtc));
            cmd.Parameters.AddWithValue("$r", o.Result);
            cmd.Parameters.AddWithValue("$s", o.Summary);
            cmd.Parameters.AddWithValue("$fp", o.FilesProcessed);
            cmd.Parameters.AddWithValue("$fc", o.FilesCopied);
            cmd.Parameters.AddWithValue("$fv", o.FilesVersioned);
            cmd.Parameters.AddWithValue("$fd", o.FilesDeleted);
            cmd.Parameters.AddWithValue("$fs", o.FilesSkipped);
            cmd.Parameters.AddWithValue("$bp", o.BytesProcessed);
            cmd.Parameters.AddWithValue("$bt", o.BytesTransferred);
            cmd.Parameters.AddWithValue("$ec", o.ErrorCount);
            cmd.Parameters.AddWithValue("$wc", o.WarningCount);
            cmd.Parameters.AddWithValue("$dj", (object?)o.DetailsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", runId);
            cmd.ExecuteNonQuery();
        }
        var seq = 0;
        foreach (var item in o.Items.Take(RunOutcome.MaxStoredItems))
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO run_items (run_id, seq, level, path, message) VALUES ($r, $q, $l, $p, $m)";
            cmd.Parameters.AddWithValue("$r", runId);
            cmd.Parameters.AddWithValue("$q", seq++);
            cmd.Parameters.AddWithValue("$l", item.Level);
            cmd.Parameters.AddWithValue("$p", (object?)item.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", item.Message);
            cmd.ExecuteNonQuery();
        }
        foreach (var d in o.SqlResults)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sql_db_results (run_id, instance, db, system, result, backup_file, size_bytes, duration_s, verify_ok, error, recovery_model)
                VALUES ($r, $i, $d, $sy, $re, $f, $sz, $du, $v, $er, $rm)
                """;
            cmd.Parameters.AddWithValue("$r", runId);
            cmd.Parameters.AddWithValue("$i", d.Instance);
            cmd.Parameters.AddWithValue("$d", d.Database);
            cmd.Parameters.AddWithValue("$sy", d.System ? 1 : 0);
            cmd.Parameters.AddWithValue("$re", d.Result);
            cmd.Parameters.AddWithValue("$f", (object?)d.BackupFile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sz", (object?)d.SizeBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$du", (object?)d.DurationS ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$v", d.VerifyOk is null ? DBNull.Value : d.VerifyOk.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$er", (object?)d.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rm", (object?)d.RecoveryModel ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>A run that did not execute (still running / chain broken / disabled): start = end.</summary>
    public long RecordSkipped(string jobId, string kind, string trigger, string? chainRunId, DateTimeOffset atUtc, string summary)
    {
        var id = StartRun(jobId, kind, trigger, chainRunId, atUtc);
        FinishRun(id, atUtc, new RunOutcome { Result = RunResults.Skipped, Summary = summary });
        return id;
    }

    /// <summary>Runs without an end (service crashed / machine rebooted) become interrupted. Returns their ids.</summary>
    public List<long> MarkUnfinishedInterrupted(DateTimeOffset atUtc, string summary)
    {
        using var c = Open();
        var ids = new List<long>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id FROM runs WHERE ended_at IS NULL";
            using var r = q.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE runs SET ended_at=$e, result='interrupted', summary=$s WHERE ended_at IS NULL";
        cmd.Parameters.AddWithValue("$e", Ms(atUtc));
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.ExecuteNonQuery();
        return ids;
    }

    private const string RunColumns = "id, job_id, chain_run_id, kind, trigger, started_at, ended_at, result, summary, files_processed, files_copied, files_versioned, files_deleted, files_skipped, bytes_processed, bytes_transferred, error_count, warning_count, details_json";

    private static RunRecord ReadRun(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        JobId = r.GetString(1),
        ChainRunId = r.IsDBNull(2) ? null : r.GetString(2),
        Kind = r.GetString(3),
        Trigger = r.GetString(4),
        StartedAt = FromMs(r.GetInt64(5)),
        EndedAt = r.IsDBNull(6) ? null : FromMs(r.GetInt64(6)),
        Result = r.IsDBNull(7) ? null : r.GetString(7),
        Summary = r.GetString(8),
        FilesProcessed = r.GetInt64(9),
        FilesCopied = r.GetInt64(10),
        FilesVersioned = r.GetInt64(11),
        FilesDeleted = r.GetInt64(12),
        FilesSkipped = r.GetInt64(13),
        BytesProcessed = r.GetInt64(14),
        BytesTransferred = r.GetInt64(15),
        ErrorCount = r.GetInt32(16),
        WarningCount = r.GetInt32(17),
        DetailsJson = r.IsDBNull(18) ? null : r.GetString(18),
    };

    private List<RunRecord> QueryRuns(string where, Action<SqliteCommand> bind, string order = "started_at DESC, id DESC", int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {RunColumns} FROM runs {(where.Length > 0 ? "WHERE " + where : "")} ORDER BY {order} LIMIT {limit}";
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var list = new List<RunRecord>();
        while (r.Read()) list.Add(ReadRun(r));
        return list;
    }

    public RunRecord? GetRun(long id) => QueryRuns("id=$id", c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    public List<RunRecord> ListRuns(string? jobId = null, int limit = 50, string? kind = null)
    {
        var where = new List<string>();
        if (jobId is not null) where.Add("job_id=$j");
        if (kind is not null) where.Add("kind=$k");
        return QueryRuns(string.Join(" AND ", where), c =>
        {
            if (jobId is not null) c.Parameters.AddWithValue("$j", jobId);
            if (kind is not null) c.Parameters.AddWithValue("$k", kind);
        }, limit: Math.Clamp(limit, 1, 10000));
    }

    /// <summary>The last finished, non-skipped run of a job.</summary>
    public RunRecord? LastRun(string jobId, string kind = RunKinds.Backup) =>
        QueryRuns("job_id=$j AND kind=$k AND ended_at IS NOT NULL AND result <> 'skipped'", c =>
        {
            c.Parameters.AddWithValue("$j", jobId);
            c.Parameters.AddWithValue("$k", kind);
        }, limit: 1).FirstOrDefault();

    public RunRecord? LastRunWithResult(string jobId, string kind, params string[] results)
    {
        var inList = string.Join(",", results.Select((_, i) => "$r" + i));
        return QueryRuns($"job_id=$j AND kind=$k AND result IN ({inList})", c =>
        {
            c.Parameters.AddWithValue("$j", jobId);
            c.Parameters.AddWithValue("$k", kind);
            for (var i = 0; i < results.Length; i++) c.Parameters.AddWithValue("$r" + i, results[i]);
        }, limit: 1).FirstOrDefault();
    }

    /// <summary>The start of the last run that actually started (any result except skipped), finished or not.</summary>
    public DateTimeOffset? LastStart(string jobId, string kind = RunKinds.Backup) =>
        QueryRuns("job_id=$j AND kind=$k AND (result IS NULL OR result <> 'skipped')", c =>
        {
            c.Parameters.AddWithValue("$j", jobId);
            c.Parameters.AddWithValue("$k", kind);
        }, limit: 1).FirstOrDefault()?.StartedAt;

    public List<RunRecord> RunsSince(DateTimeOffset sinceUtc, string? jobId = null)
    {
        var where = "started_at >= $s" + (jobId is null ? "" : " AND job_id=$j");
        return QueryRuns(where, c =>
        {
            c.Parameters.AddWithValue("$s", Ms(sinceUtc));
            if (jobId is not null) c.Parameters.AddWithValue("$j", jobId);
        }, order: "started_at ASC, id ASC", limit: 100000);
    }

    public List<RunItem> GetItems(long runId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT level, path, message FROM run_items WHERE run_id=$r ORDER BY seq";
        cmd.Parameters.AddWithValue("$r", runId);
        using var r = cmd.ExecuteReader();
        var list = new List<RunItem>();
        while (r.Read()) list.Add(new RunItem(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        return list;
    }

    public sealed record SqlDbRow(long RunId, DateTimeOffset StartedAt, SqlDbResult Result);

    /// <summary>Per-database results of a job, newest first.</summary>
    public List<SqlDbRow> SqlResults(string jobId, int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.run_id, r.started_at, d.instance, d.db, d.system, d.result, d.backup_file, d.size_bytes, d.duration_s, d.verify_ok, d.error, d.recovery_model
            FROM sql_db_results d JOIN runs r ON r.id = d.run_id
            WHERE r.job_id = $j ORDER BY r.started_at DESC, d.run_id DESC LIMIT {limit}
            """;
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        var list = new List<SqlDbRow>();
        while (r.Read())
            list.Add(new SqlDbRow(r.GetInt64(0), FromMs(r.GetInt64(1)), new SqlDbResult
            {
                Instance = r.GetString(2),
                Database = r.GetString(3),
                System = r.GetInt64(4) != 0,
                Result = r.GetString(5),
                BackupFile = r.IsDBNull(6) ? null : r.GetString(6),
                SizeBytes = r.IsDBNull(7) ? null : r.GetInt64(7),
                DurationS = r.IsDBNull(8) ? null : r.GetDouble(8),
                VerifyOk = r.IsDBNull(9) ? null : r.GetInt64(9) != 0,
                Error = r.IsDBNull(10) ? null : r.GetString(10),
                RecoveryModel = r.IsDBNull(11) ? null : r.GetString(11),
            }));
        return list;
    }

    // ---------------- config changes / notifications / vss

    public long AddConfigChange(DateTimeOffset atUtc, string by, string summary, string? diffJson)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO config_changes (at, by, summary, diff_json) VALUES ($a, $b, $s, $d); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$a", Ms(atUtc));
        cmd.Parameters.AddWithValue("$b", by);
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.Parameters.AddWithValue("$d", (object?)diffJson ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ConfigChange> ConfigChangesSince(DateTimeOffset sinceUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, at, by, summary, diff_json FROM config_changes WHERE at >= $s ORDER BY at, id";
        cmd.Parameters.AddWithValue("$s", Ms(sinceUtc));
        using var r = cmd.ExecuteReader();
        var list = new List<ConfigChange>();
        while (r.Read()) list.Add(new ConfigChange(r.GetInt64(0), FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    public ConfigChange? LastConfigChange()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, at, by, summary, diff_json FROM config_changes ORDER BY at DESC, id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ConfigChange(r.GetInt64(0), FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)) : null;
    }

    public long AddNotification(DateTimeOffset atUtc, string kind, string? jobId, string? result, string status, string? error, string? fingerprint)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO notifications (at, kind, job_id, result, status, error, fingerprint) VALUES ($a,$k,$j,$r,$s,$e,$f); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$a", Ms(atUtc));
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$j", (object?)jobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)fingerprint ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void UpdateNotification(long id, string status, string? error)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE notifications SET status=$s, error=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Last notification with the given fingerprint (sent or failed), for duplicate suppression.</summary>
    public NotificationRecord? LastNotification(string? jobId, string kind, string? fingerprint = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, at, kind, job_id, result, status, error FROM notifications WHERE kind=$k AND (job_id IS $j) "
                          + (fingerprint is null ? "" : "AND fingerprint=$f ") + "ORDER BY at DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$j", (object?)jobId ?? DBNull.Value);
        if (fingerprint is not null) cmd.Parameters.AddWithValue("$f", fingerprint);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new NotificationRecord(r.GetInt64(0), FromMs(r.GetInt64(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6))
            : null;
    }

    public NotificationRecord? LastNotificationAny()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, at, kind, job_id, result, status, error FROM notifications ORDER BY at DESC, id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new NotificationRecord(r.GetInt64(0), FromMs(r.GetInt64(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6))
            : null;
    }

    public long AddVssSnapshot(DateTimeOffset atUtc, long? runId, string snapshotId, string? setId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO vss_snapshots (created_at, run_id, snapshot_id, set_id) VALUES ($a,$r,$s,$set); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$a", Ms(atUtc));
        cmd.Parameters.AddWithValue("$r", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$set", (object?)setId ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void MarkVssDeleted(string snapshotId, DateTimeOffset atUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vss_snapshots SET deleted_at=$a WHERE snapshot_id=$s AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$a", Ms(atUtc));
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.ExecuteNonQuery();
    }

    public List<string> UndeletedVssSnapshots()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id FROM vss_snapshots WHERE deleted_at IS NULL";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ---------------- key/value state (delete-guard approvals, first-seen times, report times …)

    public string? GetValue(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetValue(string key, string? value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM kv WHERE key=$k";
        }
        else
        {
            cmd.CommandText = "INSERT INTO kv (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$v", value);
        }
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    public DateTimeOffset? GetTime(string key) =>
        long.TryParse(GetValue(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? FromMs(ms) : null;

    public void SetTime(string key, DateTimeOffset? utc) =>
        SetValue(key, utc is null ? null : Ms(utc.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ---------------- retention

    /// <summary>Deletes runs (with items / SQL rows), notifications and config changes older than the cutoff.</summary>
    public int Purge(DateTimeOffset olderThanUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM runs WHERE started_at < $c AND ended_at IS NOT NULL;
            DELETE FROM notifications WHERE at < $c;
            DELETE FROM config_changes WHERE at < $c;
            DELETE FROM vss_snapshots WHERE deleted_at IS NOT NULL AND deleted_at < $c;
            """;
        cmd.Parameters.AddWithValue("$c", Ms(olderThanUtc));
        return cmd.ExecuteNonQuery();
    }
}
