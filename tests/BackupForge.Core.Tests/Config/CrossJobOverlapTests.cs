using BackupForge.Core.Config;
using BackupForge.Core.Resources;

namespace BackupForge.Core.Tests.Config;

/// <summary>CR-001 1a.: a mirror must not swallow another job's folders.</summary>
public class CrossJobOverlapTests
{
    private const string DataRoot = @"C:\ProgramData\BackupForge";

    private static BackupConfig Nas()
    {
        var c = new BackupConfig
        {
            InstanceName = "SRV01",
            Targets = [new TargetConfig { Id = "nas1", Type = TargetTypes.Local, Path = @"\\nas\backup\srv01" }],
        };
        c.Targets[0].Type = TargetTypes.Local; // local type with UNC path keeps the test free of credentials
        c.Jobs.Add(Files("a", @"D:\Data", "", versionsFolder: @"E:\A_Versions"));
        c.Jobs.Add(Files("b", @"D:\Company", "Company"));
        return c;
    }

    private static JobConfig Files(string id, string source, string subdir, string? versionsFolder = null, string mode = FilesModes.Mirror) => new()
    {
        Id = id, Name = id, Type = JobTypes.Files,
        Schedule = new ScheduleConfig { Kind = ScheduleKinds.Manual },
        Sources = [new SourceConfig { Path = source }],
        Target = new JobTargetRef { TargetId = "nas1", Subdir = subdir },
        Files = new FilesOptions { Mode = mode, Versioning = new VersioningOptions { Folder = versionsFolder } },
    };

    private static ValidationResult V(BackupConfig c) => ConfigValidator.Validate(c, null, DataRoot);

    [Fact]
    public void Root_mirror_over_another_jobs_mirror_and_versions_is_an_error()
    {
        var r = V(Nas());
        var errs = r.Errors.Where(e => e.Code == "path.overlap_job" && e.Path == "jobs[0].target").ToList();
        Assert.Contains(errs, e => e.Args.Contains(@"\\nas\backup\srv01\Company"));
        Assert.Contains(errs, e => e.Args.Contains(@"\\nas\backup\srv01\Company_Versions"));
        // symmetric: b's mirror lies in a's mirror, so b could also delete what a writes there
        Assert.True(r.Has("path.overlap_job", "jobs[1].target"));
        var msg = errs[0].Message(Texts.Hungarian);
        Assert.Contains("átfedi", msg, StringComparison.Ordinal);
        Assert.Contains("tükörmappa", msg, StringComparison.Ordinal);
        Assert.Contains("mirror folder", errs[0].Message(Texts.English), StringComparison.Ordinal);
    }

    [Fact]
    public void Mirror_over_another_jobs_source()
    {
        var c = Nas();
        c.Jobs[1] = Files("b", @"\\nas\backup\srv01\Archiv", "", versionsFolder: @"F:\BV");
        c.Jobs[1].Target!.TargetId = "usb";
        c.Targets.Add(new TargetConfig { Id = "usb", Path = @"X:\B" });
        c.Jobs[1].Target!.Subdir = "Archiv";
        Assert.True(V(c).Has("path.overlap_job", "jobs[0].target"), V(c).ToString());
    }

    [Fact]
    public void Mirror_over_local_restic_repository_even_unused()
    {
        var c = Nas();
        c.Jobs.RemoveAt(1);
        c.ResticRepos.Add(new ResticRepoConfig { Id = "local", Location = @"\\nas\backup\srv01\restic", PasswordRef = "p" });
        Assert.True(V(c).Has("path.overlap_job", "jobs[0].target"), V(c).ToString());
        c.ResticRepos[0].Location = "sftp:backup@storage.example.com:/srv01"; // remote backends are not paths
        Assert.False(V(c).Has("path.overlap_job", "jobs[0].target"));
        c.ResticRepos[0].Location = @"local:\\nas\backup\srv01\restic";
        Assert.True(V(c).Has("path.overlap_job", "jobs[0].target"));
    }

    [Fact]
    public void Mirror_over_sql_staging_and_data_root()
    {
        var c = Nas();
        c.Jobs.RemoveAt(1);
        c.Jobs.Add(new JobConfig { Id = "sql", Name = "sql", Type = JobTypes.Sql, Sql = new SqlJobOptions { Instance = ".", StagingPath = @"\\nas\backup\srv01\staging" } });
        Assert.True(V(c).Has("path.overlap_job", "jobs[0].target"), V(c).ToString());

        var d = new BackupConfig { InstanceName = "x", Targets = [new TargetConfig { Id = "nas1", Path = @"C:\ProgramData" }] };
        d.Jobs.Add(Files("a", @"D:\Data", "", versionsFolder: @"E:\V"));
        var r = V(d);
        var e = Assert.Single(r.Errors, x => x.Code == "path.overlap_job");
        Assert.Contains("BackupForge", e.Message(Texts.English), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_versions_folders_overlap()
    {
        var c = Nas();
        c.Jobs[0] = Files("a", @"D:\Data", "Data", versionsFolder: @"E:\V");
        c.Jobs[1] = Files("b", @"D:\Company", "Company", versionsFolder: @"E:\V\b");
        var r = V(c);
        Assert.True(r.Has("path.overlap_job", "jobs[0].files.versioning.folder") || r.Has("path.overlap_job", "jobs[1].files.versioning.folder"), r.ToString());
    }

    [Fact]
    public void Update_mode_is_only_a_warning()
    {
        var c = Nas();
        c.Jobs[0].Files!.Mode = FilesModes.Update;
        var r = V(c);
        Assert.False(r.Has("path.overlap_job", "jobs[0].target"));
        Assert.True(r.HasWarning("path.overlap_job", "jobs[0].target"));
        c.Jobs[1].Files!.Mode = FilesModes.Update;
        r = V(c);
        Assert.True(r.IsValid, r.ToString()); // nothing deletes → only warnings
        Assert.True(r.HasWarning("path.overlap_job", "jobs[1].target"));
    }

    [Fact]
    public void Common_name_prefix_is_not_an_overlap()
    {
        var c = Nas();
        c.Jobs[0] = Files("a", @"D:\Data", "Comp");
        var r = V(c);
        Assert.True(r.IsValid, r.ToString());
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Spec_example_has_no_overlap() =>
        Assert.True(ConfigValidator.Validate(ConfigFixtures.Example(), ConfigFixtures.ExampleSecrets(), DataRoot).IsValid);
}
