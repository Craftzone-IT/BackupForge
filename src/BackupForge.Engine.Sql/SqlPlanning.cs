using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;

namespace BackupForge.Engine.Sql;

/// <summary>Platform-independent decisions of the SQL engine (SPEC 8.), unit-tested on Linux.</summary>
public static partial class SqlPlanning
{
    /// <summary>Databases to back up: all_user (new databases included automatically) or the include list; system DBs if include_system.</summary>
    public static List<SqlDatabaseInfo> Select(SqlJobOptions o, IEnumerable<SqlDatabaseInfo> all)
    {
        var list = new List<SqlDatabaseInfo>();
        foreach (var db in all.Where(d => d.IsBackupable))
        {
            if (o.Databases.Exclude.Contains(db.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (db.IsSystem) { if (o.Databases.IncludeSystem) list.Add(db); continue; }
            if (o.Databases.Mode == SqlDbModes.AllUser || o.Databases.Include.Contains(db.Name, StringComparer.OrdinalIgnoreCase)) list.Add(db);
        }
        // system databases first (small, fast), then users in name order
        return [.. list.OrderBy(d => d.IsSystem ? 0 : 1).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Selected databases that do not exist / are not online (mode 'selected').</summary>
    public static List<string> Missing(SqlJobOptions o, IEnumerable<SqlDatabaseInfo> all) =>
        o.Databases.Mode != SqlDbModes.Selected ? []
        : [.. o.Databases.Include.Where(n => !all.Any(d => d.IsBackupable && d.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>compression auto: uncompressed if the next chain link is restic (dedup), else compressed if the edition supports it.</summary>
    public static bool UseCompression(BackupConfig cfg, JobConfig job, SqlServerInfo info, out string? note)
    {
        note = null;
        var o = job.Sql!;
        switch (o.Compression)
        {
            case SqlCompressionModes.Off:
                return false;
            case SqlCompressionModes.On:
                if (info.SupportsCompression) return true;
                note = "express";
                return false;
            default:
                if (job.OnSuccess.Select(cfg.FindJob).Any(j => j?.Type == JobTypes.Restic)) return false;
                if (info.SupportsCompression) return true;
                note = "express";
                return false;
        }
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")]
    private static partial Regex InvalidFileChars();

    public static string SafeName(string db) => InvalidFileChars().Replace(db, "_").Trim().TrimEnd('.');

    public static string BackupFileName(string db, DateTime local) =>
        $"{SafeName(db)}_{local.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.bak";

    [GeneratedRegex(@"^(?<db>.+)_(?<ts>\d{8}_\d{6})\.bak$", RegexOptions.IgnoreCase)]
    private static partial Regex BakName();

    /// <summary>
    /// keep_local (SPEC 8.3): per database keep the newest N .bak files named by us; only files directly in the job's staging folder.
    /// Returns the files deleted.
    /// </summary>
    public static List<string> CleanupStaging(string stagingDir, int keepLocal)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(stagingDir)) return deleted;
        var groups = Directory.EnumerateFiles(stagingDir, "*.bak", SearchOption.TopDirectoryOnly)
            .Select(f => (File: f, M: BakName().Match(Path.GetFileName(f))))
            .Where(x => x.M.Success)
            .GroupBy(x => x.M.Groups["db"].Value, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            foreach (var old in g.OrderByDescending(x => x.M.Groups["ts"].Value, StringComparer.Ordinal).Skip(Math.Max(0, keepLocal)))
            {
                try { File.Delete(old.File); deleted.Add(old.File); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return deleted;
    }

    /// <summary>Run result (SPEC 8.2): any user db failed → failed; only system dbs failed → warning; recovery model warnings → warning.</summary>
    public static string Aggregate(IEnumerable<SqlDbResult> results, int warnings)
    {
        var list = results.ToList();
        if (list.Any(r => !r.System && r.Result == RunResults.Failed)) return RunResults.Failed;
        if (list.Count > 0 && list.All(r => r.Result == RunResults.Failed)) return RunResults.Failed;
        if (list.Any(r => r.Result == RunResults.Failed) || warnings > 0) return RunResults.Warning;
        return RunResults.Ok;
    }

    /// <summary>FULL / BULK_LOGGED without a log backup in the last 24 h (SPEC 8.4).</summary>
    public static bool NeedsRecoveryWarning(SqlDatabaseInfo db, DateTime? lastLogBackupUtc, DateTime nowUtc, IEnumerable<string> ignored) =>
        !db.IsSystem
        && db.RecoveryModel is "FULL" or "BULK_LOGGED"
        && !ignored.Contains(db.Name, StringComparer.OrdinalIgnoreCase)
        && (lastLogBackupUtc is null || nowUtc - lastLogBackupUtc.Value > TimeSpan.FromHours(24));

    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Literal(string s) => "N'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string BackupStatement(string db, string file, bool copyOnly, bool compression) =>
        $"BACKUP DATABASE {Quote(db)} TO DISK = {Literal(file)} WITH CHECKSUM, INIT, FORMAT{(copyOnly ? ", COPY_ONLY" : "")}, {(compression ? "COMPRESSION" : "NO_COMPRESSION")}, STATS = 5";

    public static string VerifyStatement(string file) => $"RESTORE VERIFYONLY FROM DISK = {Literal(file)} WITH CHECKSUM";

    public static string CheckDbStatement(string db) => $"DBCC CHECKDB({Quote(db)}) WITH NO_INFOMSGS, ALL_ERRORMSGS";

    /// <summary>The service's SQL login (service SID of the BackupForge service, SPEC 8.6).</summary>
    public const string ServiceLogin = @"NT SERVICE\BackupForge";

    /// <summary>
    /// Minimal permissions (docs/SQL-PERMISSIONS.md): login, db_backupoperator in every backed up database (system ones too),
    /// CREATE ANY DATABASE (RESTORE VERIFYONLY needs CREATE DATABASE), SELECT on msdb.dbo.backupset, db_owner only for CHECKDB.
    /// </summary>
    public static string GrantScript(IEnumerable<string> databases, bool checkdb, string login = ServiceLogin)
    {
        var sb = new StringBuilder();
        var l = Quote(login);
        sb.AppendLine("-- BackupForge: minimal permissions for the backup service (review before running)");
        sb.AppendLine("USE [master];");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = {Literal(login)}) CREATE LOGIN {l} FROM WINDOWS;");
        sb.AppendLine($"GRANT CREATE ANY DATABASE TO {l};   -- RESTORE VERIFYONLY needs CREATE DATABASE");
        sb.AppendLine($"GRANT VIEW ANY DATABASE TO {l};");
        var all = databases.Select(d => d).ToList();
        if (!all.Contains("msdb", StringComparer.OrdinalIgnoreCase)) all.Add("msdb");
        foreach (var db in all.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"USE {Quote(db)};");
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {Literal(login)}) CREATE USER {l} FOR LOGIN {l};");
            sb.AppendLine($"ALTER ROLE [db_backupoperator] ADD MEMBER {l};");
            if (db.Equals("msdb", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"GRANT SELECT ON [dbo].[backupset] TO {l};   -- last backups by anyone (status / recovery model check)");
            if (checkdb && !SqlDatabaseInfo.SystemNames.Contains(db, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"ALTER ROLE [db_owner] ADD MEMBER {l};   -- DBCC CHECKDB requires db_owner (only because checkdb is enabled)");
        }
        sb.AppendLine("USE [master];");
        return sb.ToString();
    }

    /// <summary>The default (virtual) service account of an instance, for the staging folder ACL.</summary>
    public static string DefaultServiceAccount(string instance)
    {
        var i = instance.IndexOf('\\');
        var name = i < 0 ? "MSSQLSERVER" : instance[(i + 1)..];
        return name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? @"NT SERVICE\MSSQLSERVER" : $@"NT SERVICE\MSSQL${name.ToUpperInvariant()}";
    }

    public static long? Median(IEnumerable<long> values)
    {
        var l = values.Order().ToList();
        if (l.Count == 0) return null;
        return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }
}
