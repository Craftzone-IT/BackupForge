using System.Globalization;
using System.Text.Json;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Resources;

namespace BackupForge.Engine.Sql;

/// <summary>Gives the SQL Server service account write access to the staging folder (Windows ACL); no-op elsewhere.</summary>
public interface IStagingPermissions
{
    void AllowWrite(string folder, string account);
}

public sealed class NoStagingPermissions : IStagingPermissions
{
    public static readonly NoStagingPermissions Instance = new();
    public void AllowWrite(string folder, string account) { }
}

/// <summary>Per-database state for status.json kept in history kv (edition, version, last CHECKDB, last full by anyone).</summary>
public static class SqlState
{
    public static string InfoKey(string jobId) => "sql:info:" + jobId;
    public static string LastFullAnyKey(string jobId) => "sql:last_full_any:" + jobId;
    public static string RecoveryKey(string jobId) => "sql:recovery:" + jobId;
    public static string CheckdbKey(string jobId, string db) => $"sql:checkdb:{jobId}:{db.ToLowerInvariant()}";
}

/// <summary>SQL Server backups (SPEC 8.): BACKUP … WITH CHECKSUM → RESTORE VERIFYONLY, into the local staging folder.</summary>
public sealed class SqlEngine(ISqlSessionFactory sessions, IFileSystem fs, IStagingPermissions permissions) : IJobEngine
{
    public bool CanRun(string jobType, string kind) => jobType == JobTypes.Sql && kind is RunKinds.Backup or RunKinds.Checkdb;

    public async Task<RunOutcome> RunAsync(JobRunContext ctx, CancellationToken ct)
    {
        var job = ctx.Job!;
        var o = job.Sql!;
        var culture = Texts.CultureFor(ctx.Config.Language);
        string? password = null;
        if (o.Auth == SqlAuthModes.Sql)
        {
            password = o.LoginRef is null ? null : ctx.Secrets.Get(o.LoginRef);
            if (password is null) return RunOutcome.Failed($"secret '{o.LoginRef}' is not set");
        }
        ISqlSession session;
        try
        {
            session = await sessions.OpenAsync(o.Instance, o.Auth == SqlAuthModes.Sql ? o.Username : null, password, ct).ConfigureAwait(false);
        }
        catch (SqlCommandException ex)
        {
            return RunOutcome.Failed(Texts.Format(culture, "sql.connect_failed", o.Instance, ex.Message));
        }
        await using (session.ConfigureAwait(false))
        {
            return ctx.Request.Kind == RunKinds.Checkdb
                ? await CheckDbAsync(ctx, session, culture, ct).ConfigureAwait(false)
                : await BackupAsync(ctx, session, culture, ct).ConfigureAwait(false);
        }
    }

    private async Task<RunOutcome> BackupAsync(JobRunContext ctx, ISqlSession session, CultureInfo culture, CancellationToken ct)
    {
        var job = ctx.Job!;
        var o = job.Sql!;
        var log = ctx.Log;
        var outcome = new RunOutcome();
        var info = await session.GetInfoAsync(ct).ConfigureAwait(false);
        log.Info($"{o.Instance}: {info.Edition} {info.Version}");
        ctx.State.SetValue(SqlState.InfoKey(job.Id), JsonSerializer.Serialize(new[] { info.Edition, info.Version }));

        var all = await session.ListDatabasesAsync(ct).ConfigureAwait(false);
        var dbs = SqlPlanning.Select(o, all);
        foreach (var missing in SqlPlanning.Missing(o, all))
        {
            outcome.SqlResults.Add(new SqlDbResult { Instance = o.Instance, Database = missing, Result = RunResults.Failed, Error = "database not found or not online" });
            outcome.AddError(missing, "database not found or not online");
        }
        if (dbs.Count == 0 && outcome.SqlResults.Count == 0) return RunOutcome.Failed(Texts.Format(culture, "sql.no_databases", o.Instance));

        // staging (SPEC 8.3)
        var staging = StagingPaths.For(ctx.Config, ctx.Paths, job.Id);
        fs.CreateDirectory(staging);
        var account = info.ServiceAccount ?? SqlPlanning.DefaultServiceAccount(o.Instance);
        try { permissions.AllowWrite(staging, account); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.Warning($"staging ACL for {account}: {ex.Message}"); }

        // free space check before any BACKUP
        var needed = (long)(dbs.Sum(d => d.EstimatedBackupBytes ?? 0) * 1.1);
        if (fs.GetSpace(staging) is var (free, _) && needed > free)
            return RunOutcome.Failed(Texts.Format(culture, "sql.no_space", SizeText.Format(needed, culture), SizeText.Format(free, culture), staging));

        var compression = SqlPlanning.UseCompression(ctx.Config, job, info, out var note);
        if (note == "express") log.Info(Texts.Get("sql.express_no_compression", culture));

        Dictionary<string, DateTime> logBackups = [];
        try { logBackups = await session.LastBackupsAsync('L', ct).ConfigureAwait(false); }
        catch (SqlCommandException ex) { log.Warning("msdb log backups not readable: " + ex.Message); }

        var stamp = ctx.Clock.LocalNow().DateTime;
        var warnings = new List<string>();
        for (var i = 0; i < dbs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var db = dbs[i];
            var file = Path.Combine(staging, SqlPlanning.BackupFileName(db.Name, stamp));
            var r = new SqlDbResult { Instance = o.Instance, Database = db.Name, System = db.IsSystem, RecoveryModel = db.RecoveryModel, BackupFile = file };
            var started = DateTime.UtcNow;
            try
            {
                log.Info($"BACKUP {db.Name} → {file} ({(compression ? "compressed" : "uncompressed")}{(o.CopyOnly ? ", copy-only" : "")})");
                await session.BackupAsync(db.Name, file, o.CopyOnly, compression,
                    p => ctx.Progress.Report(new RunProgress(RunPhases.Backup, Math.Round((i + p / 100.0) * 100 / dbs.Count, 1), db.Name)),
                    m => log.Info($"  {db.Name}: {m}"), ct).ConfigureAwait(false);
                if (o.Verify)
                {
                    ctx.Progress.Report(new RunProgress(RunPhases.Verify, Math.Round((i + 1.0) * 100 / dbs.Count, 1), db.Name));
                    await session.VerifyAsync(file, ct).ConfigureAwait(false);
                    r.VerifyOk = true;
                }
                r.SizeBytes = fs.GetEntry(file)?.Length;
                r.Result = RunResults.Ok;
            }
            catch (SqlCommandException ex)
            {
                r.Result = RunResults.Failed;
                r.Error = ex.IsPermission ? Texts.Format(culture, "sql.no_permission", db.Name, job.Id) : ex.Message;
                if (o.Verify && r.VerifyOk is null && fs.FileExists(file)) r.VerifyOk = false;
                log.Error($"{db.Name}: {ex.Message}");
                outcome.AddError(db.Name, r.Error);
                TryDelete(file); // a failed / unverified .bak must never look like a backup
            }
            r.DurationS = (DateTime.UtcNow - started).TotalSeconds;
            outcome.SqlResults.Add(r);
            outcome.BytesTransferred += r.SizeBytes ?? 0;

            if (r.Result == RunResults.Ok && SqlPlanning.NeedsRecoveryWarning(db, logBackups.TryGetValue(db.Name, out var lb) ? lb : null, ctx.Clock.UtcNow.UtcDateTime, o.IgnoreFullRecovery))
            {
                var w = Texts.Format(culture, "sql.full_recovery", db.Name);
                warnings.Add(w);
                outcome.AddWarning(db.Name, w);
                log.Warning(w);
            }
        }

        // msdb: last full backup by anyone (SPEC 8.5), recovery models for the status
        try
        {
            var full = await session.LastBackupsAsync('D', ct).ConfigureAwait(false);
            ctx.State.SetValue(SqlState.LastFullAnyKey(job.Id), JsonSerializer.Serialize(full.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("o"))));
        }
        catch (SqlCommandException ex) { log.Warning("msdb full backups not readable: " + ex.Message); }
        ctx.State.SetValue(SqlState.RecoveryKey(job.Id), JsonSerializer.Serialize(all.ToDictionary(d => d.Name, d => d.RecoveryModel)));

        outcome.Result = SqlPlanning.Aggregate(outcome.SqlResults, warnings.Count);
        var okCount = outcome.SqlResults.Count(x => x.Result == RunResults.Ok);
        outcome.FilesCopied = okCount;
        outcome.FilesProcessed = outcome.SqlResults.Count;
        outcome.Summary = Texts.Format(culture, "sql.summary", okCount, outcome.SqlResults.Count, SizeText.Format(outcome.BytesTransferred, culture));
        var failedNames = outcome.SqlResults.Where(x => x.Result == RunResults.Failed).Select(x => x.Database).ToList();
        if (failedNames.Count > 0) outcome.Summary += " · " + string.Join(", ", failedNames) + ": " + (outcome.SqlResults.First(x => x.Result == RunResults.Failed).Error ?? "");
        if (warnings.Count > 0) outcome.Summary += Texts.Format(culture, "sql.summary_warn", warnings.Count, string.Join("; ", warnings.Select(w => w.Split(':')[0] + ": FULL recovery")));
        if (outcome.Summary.Length > 400) outcome.Summary = outcome.Summary[..399] + "…";

        // keep_local cleanup happens after the successful end of the chain (SqlStagingCleanup); without a chain right now
        if (RunResults.IsSuccess(outcome.Result) && job.OnSuccess.Count == 0 && !ctx.Options.DryRun)
            foreach (var f in SqlPlanning.CleanupStaging(staging, o.KeepLocal)) log.Info($"staging cleanup: {f}");
        return outcome;
    }

    private async Task<RunOutcome> CheckDbAsync(JobRunContext ctx, ISqlSession session, CultureInfo culture, CancellationToken ct)
    {
        var job = ctx.Job!;
        var o = job.Sql!;
        var dbs = SqlPlanning.Select(o, await session.ListDatabasesAsync(ct).ConfigureAwait(false)).Where(d => !d.IsSystem).ToList();
        var outcome = new RunOutcome();
        var bad = new List<string>();
        for (var i = 0; i < dbs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var db = dbs[i];
            ctx.Progress.Report(new RunProgress(RunPhases.Checkdb, Math.Round(i * 100.0 / Math.Max(1, dbs.Count), 1), db.Name));
            List<string> errors;
            try { errors = await session.CheckDbAsync(db.Name, ct).ConfigureAwait(false); }
            catch (SqlCommandException ex) { errors = [ex.Message]; }
            foreach (var e in errors) { ctx.Log.Error($"{db.Name}: {e}"); outcome.AddError(db.Name, e); }
            if (errors.Count > 0) bad.Add(db.Name);
            ctx.State.SetValue(SqlState.CheckdbKey(job.Id, db.Name), JsonSerializer.Serialize(new { at = ctx.Clock.UtcNow.ToString("o"), ok = errors.Count == 0 }));
        }
        outcome.FilesProcessed = dbs.Count;
        if (bad.Count > 0)
        {
            outcome.Result = RunResults.Failed;
            outcome.Summary = Texts.Format(culture, "sql.checkdb_failed", string.Join(", ", bad));
        }
        else outcome.Summary = Texts.Format(culture, "sql.checkdb_ok", dbs.Count);
        return outcome;
    }

    private void TryDelete(string file)
    {
        try { if (fs.FileExists(file)) fs.DeleteFile(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
