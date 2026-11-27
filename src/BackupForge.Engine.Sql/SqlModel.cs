namespace BackupForge.Engine.Sql;

public sealed record SqlServerInfo(string Edition, string Version, int EngineEdition, string? ServiceAccount)
{
    /// <summary>Express (4) does not support backup compression.</summary>
    public bool SupportsCompression => EngineEdition != 4;
}

public sealed record SqlDatabaseInfo(string Name, int DatabaseId, string State, string RecoveryModel, bool IsSnapshot, long? EstimatedBackupBytes)
{
    public static readonly string[] SystemNames = ["master", "model", "msdb"];
    public bool IsSystem => SystemNames.Contains(Name, StringComparer.OrdinalIgnoreCase) || Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase);
    public bool IsBackupable => State == "ONLINE" && !IsSnapshot && !Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase);
}

public sealed class SqlCommandException(string message, int number) : Exception(message)
{
    /// <summary>SQL error number (262 / 916 / 3013 …); 0 if not an SQL error.</summary>
    public int Number { get; } = number;
    public bool IsPermission => Number is 262 or 916 or 15247 or 3110 or 229 or 300;
}

/// <summary>One connection to an instance. The real one uses Microsoft.Data.SqlClient; tests use a fake.</summary>
public interface ISqlSession : IAsyncDisposable
{
    Task<SqlServerInfo> GetInfoAsync(CancellationToken ct);
    Task<List<SqlDatabaseInfo>> ListDatabasesAsync(CancellationToken ct);
    Task BackupAsync(string database, string file, bool copyOnly, bool compression, Action<int>? percent, Action<string>? message, CancellationToken ct);
    Task VerifyAsync(string file, CancellationToken ct);
    /// <summary>Last finished backup per database from msdb.dbo.backupset (type 'D' = full, 'L' = log), by anyone.</summary>
    Task<Dictionary<string, DateTime>> LastBackupsAsync(char type, CancellationToken ct);
    /// <summary>DBCC CHECKDB; returns the error messages (empty = ok).</summary>
    Task<List<string>> CheckDbAsync(string database, CancellationToken ct);
    Task ExecuteAsync(string sql, CancellationToken ct);
    /// <summary>The databases the current login may NOT back up (HAS_PERMS_BY_NAME … 'BACKUP DATABASE'); for the dry-run (CR-002 B4.).</summary>
    Task<List<string>> NoBackupPermissionAsync(IEnumerable<string> databases, CancellationToken ct) => Task.FromResult(new List<string>());
}

public interface ISqlSessionFactory
{
    Task<ISqlSession> OpenAsync(string instance, string? username, string? password, CancellationToken ct);
}
