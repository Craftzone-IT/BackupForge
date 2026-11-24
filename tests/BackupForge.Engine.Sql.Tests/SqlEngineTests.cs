using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.TestKit;

namespace BackupForge.Engine.Sql.Tests;

public sealed class SqlEngineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeSqlServer _sql = new();
    private readonly MemoryKeyValueStore _state = new();
    private readonly BackupConfig _cfg;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 24, 12, 30, 0, TimeSpan.Zero));
    private JobConfig Job => _cfg.Jobs[0];
    private DataPaths Paths => new(_dir.Sub("data"));
    private string Staging => Path.Combine(Paths.StagingRoot, "sql-main");

    public SqlEngineTests()
    {
        _cfg = new BackupConfig
        {
            InstanceName = "SRV01", Language = "en",
            ResticRepos = [new ResticRepoConfig { Id = "r", Location = "x", PasswordRef = "p" }],
            Jobs =
            [
                new JobConfig { Id = "sql-main", Name = "SQL", Type = JobTypes.Sql, Sql = new SqlJobOptions { Instance = @".\SQLEXPRESS" }, OnSuccess = [] },
                new JobConfig { Id = "off", Name = "off", Type = JobTypes.Restic, Sources = [new SourceConfig { Path = "@staging:sql-main" }], Restic = new ResticJobOptions { RepoId = "r" } },
            ],
        };
    }

    public void Dispose() => _dir.Dispose();

    private async Task<RunOutcome> Run(string kind = RunKinds.Backup, IFileSystem? fs = null)
    {
        var engine = new SqlEngine(_sql, fs ?? PhysicalFileSystem.Instance, NoStagingPermissions.Instance);
        var ctx = new JobRunContext
        {
            Config = _cfg, Job = Job, Request = new WorkRequest { JobId = Job.Id, Kind = kind, Trigger = RunTriggers.Manual }, RunId = 1,
            Log = new MemoryRunLog(), Progress = new Progress<RunProgress>(), Clock = _clock, Secrets = new InMemorySecretStore(), Paths = Paths, State = _state,
        };
        var o = await engine.RunAsync(ctx, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return o;
    }

    [Fact]
    public async Task Backs_up_user_and_system_databases_and_verifies()
    {
        _sql.LogBackups["erp"] = _clock.UtcNow.UtcDateTime.AddHours(-1);
        var o = await Run();
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal(["master", "model", "msdb", "crm", "erp"], o.SqlResults.Select(r => r.Database));
        Assert.All(o.SqlResults, r => Assert.True(r.VerifyOk));
        Assert.True(File.Exists(Path.Combine(Staging, "erp_20261124_123000.bak")));
        Assert.StartsWith("5/5 databases ok", o.Summary, StringComparison.Ordinal);
        Assert.All(_sql.Backups, b => Assert.False(b.Compression)); // Express
    }

    [Fact]
    public async Task Full_recovery_without_log_backup_is_a_warning_and_can_be_silenced()
    {
        var o = await Run();
        Assert.Equal(RunResults.Warning, o.Result);
        Assert.Contains("erp: FULL recovery", o.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(o.Items, i => i.Path == "model"); // system dbs never warn
        Job.Sql!.IgnoreFullRecovery = ["erp"];
        Assert.Equal(RunResults.Ok, (await Run()).Result);
    }

    [Fact]
    public async Task User_db_failure_fails_the_run_system_only_failure_is_a_warning()
    {
        Job.Sql!.IgnoreFullRecovery = ["erp"];
        _sql.BackupFails["crm"] = new SqlCommandException("Msg 3202: Write on disk failed: There is not enough space on the disk.", 3202);
        var o = await Run();
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Equal(RunResults.Ok, o.SqlResults.Single(r => r.Database == "erp").Result); // the others continue
        Assert.Contains("crm", o.Summary, StringComparison.Ordinal);

        _sql.BackupFails.Clear();
        _sql.BackupFails["model"] = new SqlCommandException("Msg 3013: BACKUP DATABASE is terminating abnormally.", 3013);
        Assert.Equal(RunResults.Warning, (await Run()).Result);
    }

    [Fact]
    public async Task Verify_failure_fails_db_and_removes_the_file()
    {
        Job.Sql!.IgnoreFullRecovery = ["erp"];
        _sql.VerifyFails.Add("erp");
        var o = await Run();
        var erp = o.SqlResults.Single(r => r.Database == "erp");
        Assert.Equal(RunResults.Failed, erp.Result);
        Assert.False(erp.VerifyOk);
        Assert.False(File.Exists(erp.BackupFile));
        Assert.Equal(RunResults.Failed, o.Result);
    }

    [Fact]
    public async Task Permission_error_says_run_grant()
    {
        _sql.BackupFails["erp"] = new SqlCommandException("Msg 262: BACKUP DATABASE permission denied in database 'erp'.", 262);
        var o = await Run();
        Assert.Contains("bf sql grant sql-main", o.SqlResults.Single(r => r.Database == "erp").Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_mode_and_missing_database()
    {
        Job.Sql!.Databases = new SqlDatabaseSelection { Mode = SqlDbModes.Selected, Include = ["crm", "ghost"], IncludeSystem = false };
        var o = await Run();
        Assert.Equal(["crm"], _sql.Backups.Select(b => b.Db));
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains(o.SqlResults, r => r.Database == "ghost" && r.Result == RunResults.Failed);
    }

    [Fact]
    public async Task Not_enough_staging_space_fails_before_backup()
    {
        var fs = new SpaceFs(PhysicalFileSystem.Instance, free: 100_000_000);
        var o = await Run(fs: fs);
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("not enough space", o.Summary, StringComparison.Ordinal);
        Assert.Empty(_sql.Backups);
    }

    [Fact]
    public async Task Connect_failure()
    {
        _sql.ConnectError = new SqlCommandException("Login failed for user 'NT SERVICE\\BackupForge'.", 18456);
        var o = await Run();
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("cannot connect", o.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compression_auto_off_before_restic_on_for_standard_edition()
    {
        _sql.Info = new SqlServerInfo("Standard Edition", "16.0", 2, null);
        await Run();
        Assert.All(_sql.Backups, b => Assert.True(b.Compression));
        _sql.Backups.Clear();
        Job.OnSuccess = ["off"];
        await Run();
        Assert.All(_sql.Backups, b => Assert.False(b.Compression));
        Job.OnSuccess = [];
        Job.Sql!.Compression = SqlCompressionModes.Off;
        _sql.Backups.Clear();
        await Run();
        Assert.All(_sql.Backups, b => Assert.False(b.Compression));
    }

    [Fact]
    public async Task Keep_local_without_chain_keeps_newest_n()
    {
        Job.Sql!.KeepLocal = 2;
        Job.Sql.IgnoreFullRecovery = ["erp"];
        for (var i = 0; i < 4; i++) await Run();
        Assert.Equal(2, Directory.GetFiles(Staging, "erp_*.bak").Length);
        Assert.True(File.Exists(Path.Combine(Staging, "erp_20261124_123300.bak")));
    }

    [Fact]
    public async Task Checkdb_reports_errors()
    {
        _sql.CheckDbErrors["erp"] = ["Msg 8928: Object ID 5, index ID 1: Page (1:153) could not be processed."];
        var o = await Run(RunKinds.Checkdb);
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("erp", o.Summary, StringComparison.Ordinal);
        Assert.Contains("\"ok\":false", _state.GetValue(SqlState.CheckdbKey("sql-main", "erp")), StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", _state.GetValue(SqlState.CheckdbKey("sql-main", "crm")), StringComparison.Ordinal);
    }

    private sealed class SpaceFs(IFileSystem inner, long free) : IFileSystem
    {
        public char Separator => inner.Separator;
        public bool IsCaseSensitive => inner.IsCaseSensitive;
        public bool FileExists(string p) => inner.FileExists(p);
        public bool DirectoryExists(string p) => inner.DirectoryExists(p);
        public FsEntry? GetEntry(string p) => inner.GetEntry(p);
        public IEnumerable<FsEntry> Enumerate(string d) => inner.Enumerate(d);
        public void CreateDirectory(string p) => inner.CreateDirectory(p);
        public void DeleteFile(string p) => inner.DeleteFile(p);
        public void DeleteDirectory(string p, bool r) => inner.DeleteDirectory(p, r);
        public void MoveFile(string s, string d, bool o) => inner.MoveFile(s, d, o);
        public void MoveDirectory(string s, string d) => inner.MoveDirectory(s, d);
        public Stream OpenRead(string p) => inner.OpenRead(p);
        public Stream Create(string p) => inner.Create(p);
        public void Flush(Stream s) => inner.Flush(s);
        public void SetTimes(string p, DateTime c, DateTime w) => inner.SetTimes(p, c, w);
        public void SetAttributes(string p, FileAttributes a) => inner.SetAttributes(p, a);
        public (long Free, long Total)? GetSpace(string p) => (free, free * 2);
    }
}

public class SqlPlanningTests
{
    [Fact]
    public void Statements_quote_names()
    {
        Assert.Equal("BACKUP DATABASE [we]]ird] TO DISK = N'C:\\s\\o''k.bak' WITH CHECKSUM, INIT, FORMAT, COPY_ONLY, NO_COMPRESSION, STATS = 5",
            SqlPlanning.BackupStatement("we]ird", @"C:\s\o'k.bak", true, false));
        Assert.Equal("RESTORE VERIFYONLY FROM DISK = N'x.bak' WITH CHECKSUM", SqlPlanning.VerifyStatement("x.bak"));
        Assert.Equal("DBCC CHECKDB([erp]) WITH NO_INFOMSGS, ALL_ERRORMSGS", SqlPlanning.CheckDbStatement("erp"));
    }

    [Fact]
    public void File_names_and_service_accounts()
    {
        Assert.Equal("a_b_20261124_123000.bak", SqlPlanning.BackupFileName("a:b", new DateTime(2026, 11, 24, 12, 30, 0)));
        Assert.Equal(@"NT SERVICE\MSSQL$SQLEXPRESS", SqlPlanning.DefaultServiceAccount(@".\sqlexpress"));
        Assert.Equal(@"NT SERVICE\MSSQLSERVER", SqlPlanning.DefaultServiceAccount("."));
    }

    [Fact]
    public void Grant_script_is_minimal()
    {
        var s = SqlPlanning.GrantScript(["master", "erp"], checkdb: false);
        Assert.Contains(@"CREATE LOGIN [NT SERVICE\BackupForge] FROM WINDOWS", s, StringComparison.Ordinal);
        Assert.Contains("db_backupoperator", s, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON [dbo].[backupset]", s, StringComparison.Ordinal);
        Assert.Contains("CREATE ANY DATABASE", s, StringComparison.Ordinal);
        Assert.DoesNotContain("sysadmin", s, StringComparison.Ordinal);
        Assert.DoesNotContain("db_owner", s, StringComparison.Ordinal);
        Assert.Contains("ALTER ROLE [db_owner] ADD MEMBER", SqlPlanning.GrantScript(["erp"], checkdb: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Cleanup_only_touches_own_bak_files()
    {
        using var d = new TempDir();
        foreach (var n in new[] { "erp_20261101_000000.bak", "erp_20261102_000000.bak", "erp_20261103_000000.bak", "manual.bak", "erp_20261101_000000.trn", "notes.txt" })
            File.WriteAllText(d.Sub(n), "x");
        Directory.CreateDirectory(d.Sub("sub"));
        File.WriteAllText(d.Sub("sub", "erp_20260101_000000.bak"), "x");
        var deleted = SqlPlanning.CleanupStaging(d.Path, 1);
        Assert.Equal(2, deleted.Count);
        Assert.True(File.Exists(d.Sub("erp_20261103_000000.bak")));
        Assert.True(File.Exists(d.Sub("manual.bak")));
        Assert.True(File.Exists(d.Sub("erp_20261101_000000.trn")));
        Assert.True(File.Exists(d.Sub("sub", "erp_20260101_000000.bak")));
    }
}
