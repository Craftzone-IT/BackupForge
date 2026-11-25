using BackupForge.Engine.Sql;

namespace BackupForge.Engine.Sql.Tests;

public sealed class FakeSqlServer : ISqlSessionFactory
{
    public SqlServerInfo Info { get; set; } = new("Express Edition (64-bit)", "16.0.1000.6", 4, null);
    public List<SqlDatabaseInfo> Databases { get; } =
    [
        new("master", 1, "ONLINE", "SIMPLE", false, 4_000_000),
        new("model", 3, "ONLINE", "FULL", false, 2_000_000),
        new("msdb", 4, "ONLINE", "SIMPLE", false, 10_000_000),
        new("tempdb", 2, "ONLINE", "SIMPLE", false, 8_000_000),
        new("erp", 5, "ONLINE", "FULL", false, 900_000_000),
        new("crm", 6, "ONLINE", "SIMPLE", false, 50_000_000),
        new("old", 7, "OFFLINE", "SIMPLE", false, 1_000),
    ];
    public Dictionary<string, SqlCommandException> BackupFails { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> VerifyFails { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> LogBackups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> CheckDbErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Db, string File, bool Compression, bool CopyOnly)> Backups { get; } = [];
    public SqlCommandException? ConnectError { get; set; }

    public Task<ISqlSession> OpenAsync(string instance, string? username, string? password, CancellationToken ct) =>
        ConnectError is not null ? Task.FromException<ISqlSession>(ConnectError) : Task.FromResult<ISqlSession>(new Session(this));

    private sealed class Session(FakeSqlServer s) : ISqlSession
    {
        public Task<SqlServerInfo> GetInfoAsync(CancellationToken ct) => Task.FromResult(s.Info);
        public Task<List<SqlDatabaseInfo>> ListDatabasesAsync(CancellationToken ct) => Task.FromResult(s.Databases.ToList());

        public Task BackupAsync(string database, string file, bool copyOnly, bool compression, Action<int>? percent, Action<string>? message, CancellationToken ct)
        {
            if (s.BackupFails.TryGetValue(database, out var ex)) throw ex;
            percent?.Invoke(50);
            File.WriteAllText(file, "BAK:" + database);
            lock (s.Backups) s.Backups.Add((database, file, compression, copyOnly));
            percent?.Invoke(100);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(string file, CancellationToken ct)
        {
            var db = File.ReadAllText(file)[4..];
            if (s.VerifyFails.Contains(db)) throw new SqlCommandException("Msg 3013: VERIFY DATABASE is terminating abnormally.", 3013);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, DateTime>> LastBackupsAsync(char type, CancellationToken ct) =>
            Task.FromResult(type == 'L' ? new Dictionary<string, DateTime>(s.LogBackups, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["erp"] = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });

        public Task<List<string>> CheckDbAsync(string database, CancellationToken ct) =>
            Task.FromResult(s.CheckDbErrors.TryGetValue(database, out var e) ? e : []);

        public Task ExecuteAsync(string sql, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
