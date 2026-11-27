using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace BackupForge.Engine.Sql;

/// <summary>Microsoft.Data.SqlClient implementation. Windows auth = the process identity (the service SID for the service).</summary>
public sealed partial class SqlClientSessionFactory : ISqlSessionFactory
{
    public async Task<ISqlSession> OpenAsync(string instance, string? username, string? password, CancellationToken ct)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = instance,
            InitialCatalog = "master",
            TrustServerCertificate = true,     // local instance with its self-signed certificate
            Encrypt = SqlConnectionEncryptOption.Optional,
            ConnectTimeout = 30,
            ApplicationName = "BackupForge",
            Pooling = false,
        };
        if (username is null) b.IntegratedSecurity = true;
        else
        {
            b.UserID = username;
            b.Password = password ?? "";
        }
        var c = new SqlConnection(b.ConnectionString);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw new SqlCommandException(ex.Message, ex.Number);
        }
        return new SqlClientSession(c);
    }
}

internal sealed partial class SqlClientSession(SqlConnection connection) : ISqlSession
{
    [GeneratedRegex(@"(\d+) percent processed")]
    private static partial Regex Percent();

    private SqlCommand Cmd(string sql, int timeoutSeconds = 0)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds; // 0 = no limit (the job timeout cancels)
        return cmd;
    }

    private static async Task<T> Wrap<T>(Func<Task<T>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch (SqlException ex) { throw new SqlCommandException($"Msg {ex.Number}: {ex.Message}", ex.Number); }
    }

    public Task<List<string>> NoBackupPermissionAsync(IEnumerable<string> databases, CancellationToken ct) => Wrap(async () =>
    {
        var missing = new List<string>();
        foreach (var db in databases)
        {
            await using var cmd = Cmd("SELECT HAS_PERMS_BY_NAME(@db, 'DATABASE', 'BACKUP DATABASE')", 30);
            cmd.Parameters.AddWithValue("@db", db);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not int ok || ok != 1) missing.Add(db);
        }
        return missing;
    });

    public Task<SqlServerInfo> GetInfoAsync(CancellationToken ct) => Wrap(async () =>
    {
        await using var cmd = Cmd("SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(200)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), CAST(SERVERPROPERTY('EngineEdition') AS int)", 60);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        var info = new SqlServerInfo(r.GetString(0), r.GetString(1), r.GetInt32(2), null);
        await r.CloseAsync().ConfigureAwait(false);
        try
        {
            await using var svc = Cmd("SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'", 30);
            if (await svc.ExecuteScalarAsync(ct).ConfigureAwait(false) is string acc) info = info with { ServiceAccount = acc };
        }
        catch (SqlException) { /* needs VIEW SERVER STATE — the default virtual account is used then */ }
        return info;
    });

    public Task<List<SqlDatabaseInfo>> ListDatabasesAsync(CancellationToken ct) => Wrap(async () =>
    {
        // size estimate: the last full backup (by anyone) or the allocated data file size
        const string sql = """
            SELECT d.name, d.database_id, d.state_desc, d.recovery_model_desc, CAST(CASE WHEN d.source_database_id IS NULL THEN 0 ELSE 1 END AS bit),
                   COALESCE(
                     (SELECT TOP 1 CAST(b.backup_size AS bigint) FROM msdb.dbo.backupset b WHERE b.database_name = d.name AND b.type = 'D' ORDER BY b.backup_finish_date DESC),
                     (SELECT SUM(CAST(f.size AS bigint)) * 8192 FROM sys.master_files f WHERE f.database_id = d.database_id AND f.type = 0))
            FROM sys.databases d ORDER BY d.name
            """;
        var list = new List<SqlDatabaseInfo>();
        try
        {
            await using var cmd = Cmd(sql, 120);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new SqlDatabaseInfo(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetInt64(5)));
        }
        catch (SqlException)
        {
            // no access to msdb / master_files: list without estimate
            list.Clear();
            await using var cmd = Cmd("SELECT name, database_id, state_desc, recovery_model_desc, CAST(CASE WHEN source_database_id IS NULL THEN 0 ELSE 1 END AS bit) FROM sys.databases ORDER BY name", 120);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new SqlDatabaseInfo(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), null));
        }
        return list;
    });

    public async Task BackupAsync(string database, string file, bool copyOnly, bool compression, Action<int>? percent, Action<string>? message, CancellationToken ct)
    {
        void Handler(object s, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
            {
                message?.Invoke(err.Message);
                var m = Percent().Match(err.Message);
                if (m.Success) percent?.Invoke(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        connection.InfoMessage += Handler;
        connection.FireInfoMessageEventOnUserErrors = false;
        try
        {
            await Wrap(async () =>
            {
                await using var cmd = Cmd(SqlPlanning.BackupStatement(database, file, copyOnly, compression));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return 0;
            }).ConfigureAwait(false);
        }
        finally
        {
            connection.InfoMessage -= Handler;
        }
    }

    public Task VerifyAsync(string file, CancellationToken ct) => Wrap(async () =>
    {
        await using var cmd = Cmd(SqlPlanning.VerifyStatement(file));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return 0;
    });

    public Task<Dictionary<string, DateTime>> LastBackupsAsync(char type, CancellationToken ct) => Wrap(async () =>
    {
        var d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = Cmd("SELECT database_name, MAX(backup_finish_date) FROM msdb.dbo.backupset WHERE type = @t GROUP BY database_name", 120);
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.Char, 1) { Value = type.ToString() });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            if (!r.IsDBNull(1)) d[r.GetString(0)] = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Local).ToUniversalTime();
        return d;
    });

    public async Task<List<string>> CheckDbAsync(string database, CancellationToken ct)
    {
        var messages = new List<string>();
        void Handler(object s, SqlInfoMessageEventArgs e) { foreach (SqlError err in e.Errors) messages.Add($"Msg {err.Number}: {err.Message}"); }
        connection.InfoMessage += Handler;
        try
        {
            await using var cmd = Cmd(SqlPlanning.CheckDbStatement(database));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            foreach (SqlError err in ex.Errors) messages.Add($"Msg {err.Number}: {err.Message}");
            if (ex.Errors.Cast<SqlError>().All(er => er.Class < 11)) messages.Clear();
            if (messages.Count == 0) messages.Add($"Msg {ex.Number}: {ex.Message}");
        }
        finally
        {
            connection.InfoMessage -= Handler;
        }
        return messages;
    }

    public Task ExecuteAsync(string sql, CancellationToken ct) => Wrap(async () =>
    {
        await using var cmd = Cmd(sql, 300);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return 0;
    });

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
