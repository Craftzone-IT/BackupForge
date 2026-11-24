using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Engine.Restic;
using BackupForge.Engine.Sql;
using BackupForge.TestKit;

namespace BackupForge.Integration.Tests;

/// <summary>Runs only when (localdb)\MSSQLLocalDB answers (the CI step starts it); otherwise see docs/MANUAL-TESTS.md.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public const string Instance = @"(localdb)\MSSQLLocalDB";
    private static readonly Lazy<string?> Problem = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return "Windows only";
        try
        {
            var s = new SqlClientSessionFactory().OpenAsync(Instance, null, null, CancellationToken.None).GetAwaiter().GetResult();
            s.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) { return "LocalDB not available: " + ex.Message; }
    });

    public LocalDbFactAttribute()
    {
        if (Problem.Value is { } p) Skip = p;
    }
}

public class SqlLocalDbTests
{
    [LocalDbFact]
    public async Task Sql_backup_verify_checkdb_then_restic_chain()
    {
        using var d = new TempDir();
        var db = "bfit_" + Guid.NewGuid().ToString("N")[..8];
        var factory = new SqlClientSessionFactory();
        await using (var s = await factory.OpenAsync(LocalDbFactAttribute.Instance, null, null, CancellationToken.None))
        {
            await s.ExecuteAsync($"CREATE DATABASE [{db}]; ALTER DATABASE [{db}] SET RECOVERY FULL;", CancellationToken.None);
            await s.ExecuteAsync($"USE [{db}]; CREATE TABLE t (id int, txt nvarchar(100)); INSERT INTO t VALUES (1, N'árvíztűrő');", CancellationToken.None);
        }
        try
        {
            var paths = new DataPaths(d.Sub("data"));
            var cfg = new BackupConfig
            {
                InstanceName = "IT", Language = "en",
                ResticRepos = [new ResticRepoConfig { Id = "r", Location = d.Sub("repo"), PasswordRef = "restic:r" }],
                Jobs =
                [
                    new JobConfig { Id = "sql", Name = "sql", Type = JobTypes.Sql, OnSuccess = ["off"],
                        Sql = new SqlJobOptions { Instance = LocalDbFactAttribute.Instance, Databases = new SqlDatabaseSelection { Mode = SqlDbModes.Selected, Include = [db], IncludeSystem = false }, Checkdb = "weekly Sun 01:00" } },
                    new JobConfig { Id = "off", Name = "off", Type = JobTypes.Restic, Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained },
                        Sources = [new SourceConfig { Path = "@staging:sql" }], Restic = new ResticJobOptions { RepoId = "r", Vss = VssModes.Never } },
                ],
            };
            var secrets = new InMemorySecretStore([new("restic:r", "test-restic-pw-456")]);
            var masker = new Core.Logging.SecretMasker();
            var bin = new ResticBinary(ResticTestEnv.Binary ?? throw new InvalidOperationException("restic needed"), null);
            await new ResticClient(bin, new SystemProcessRunner(), paths.ResticCache, masker).InitAsync(cfg.ResticRepos[0], secrets, CancellationToken.None);
            var history = new Core.History.HistoryStore(paths.HistoryDb);
            await using var coord = new JobCoordinator(cfg, SystemClock.Instance, history,
                [new SqlEngine(factory, PhysicalFileSystem.Instance, NoStagingPermissions.Instance), new ResticEngine(bin, new SystemProcessRunner(), masker)],
                secrets, paths, masker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            var sqlRun = await coord.Enqueue(new WorkRequest { JobId = "sql", Trigger = RunTriggers.Manual }).Completion;
            Assert.True(sqlRun!.Result == RunResults.Warning, sqlRun.Summary); // FULL recovery without log backups → warning, chain continues
            var bak = Directory.GetFiles(StagingPaths.For(cfg, paths, "sql"), "*.bak").Single();
            Assert.True(new FileInfo(bak).Length > 0);
            var rows = history.SqlResults("sql");
            Assert.True(rows.Single().Result.VerifyOk);

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (history.LastRun("off") is null && DateTime.UtcNow < deadline) await Task.Delay(500);
            var chained = history.LastRun("off")!;
            Assert.True(chained.Result == RunResults.Ok, chained.Summary);
            Assert.Equal(sqlRun.ChainRunId, chained.ChainRunId);

            var check = await coord.Enqueue(new WorkRequest { JobId = "sql", Kind = RunKinds.Checkdb, Trigger = RunTriggers.Manual }).Completion;
            Assert.True(check!.Result == RunResults.Ok, check.Summary);
        }
        finally
        {
            await using var s = await factory.OpenAsync(LocalDbFactAttribute.Instance, null, null, CancellationToken.None);
            await s.ExecuteAsync($"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];", CancellationToken.None);
        }
    }
}
