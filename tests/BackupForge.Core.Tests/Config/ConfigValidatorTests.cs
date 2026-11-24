using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Resources;

namespace BackupForge.Core.Tests.Config;

public class ConfigValidatorTests
{
    private static ValidationResult V(BackupConfig c, ISecretStore? s = null) => ConfigValidator.Validate(c, s);

    private static void AssertError(BackupConfig c, string code, string path, ISecretStore? s = null)
    {
        var r = V(c, s);
        Assert.True(r.Has(code, path), $"expected {code} at {path}, got:\n{r}");
    }

    [Fact]
    public void Spec_example_is_valid_with_its_secrets()
    {
        var r = V(ConfigFixtures.Example(), ConfigFixtures.ExampleSecrets());
        Assert.True(r.IsValid, r.ToString());
    }

    [Fact]
    public void Minimal_is_valid() => Assert.True(V(ConfigFixtures.Minimal()).IsValid, V(ConfigFixtures.Minimal()).ToString());

    [Fact]
    public void Schema_version_checked()
    {
        var c = ConfigFixtures.Minimal();
        c.Schema = 2;
        AssertError(c, "schema.unsupported", "schema");
    }

    [Fact]
    public void Instance_name_required()
    {
        var c = ConfigFixtures.Minimal();
        c.InstanceName = " ";
        AssertError(c, "required", "instance_name");
    }

    [Fact]
    public void Language_must_be_hu_or_en()
    {
        var c = ConfigFixtures.Minimal();
        c.Language = "de";
        AssertError(c, "value.invalid", "language");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Max_parallel_jobs_range(int v)
    {
        var c = ConfigFixtures.Minimal();
        c.General.MaxParallelJobs = v;
        AssertError(c, "range", "general.max_parallel_jobs");
    }

    [Fact]
    public void Retention_ranges()
    {
        var c = ConfigFixtures.Minimal();
        c.General.HistoryRetentionDays = 1;
        c.General.LogRetentionDays = 0;
        var r = V(c);
        Assert.True(r.Has("range", "general.history_retention_days"));
        Assert.True(r.Has("range", "general.log_retention_days"));
    }

    [Fact]
    public void Duplicate_job_ids()
    {
        var c = ConfigFixtures.Minimal();
        var copy = ConfigJson.Clone(c).Jobs[0];
        copy.Target!.Subdir = "Other";
        c.Jobs.Add(copy);
        AssertError(c, "id.duplicate", "jobs[1].id");
    }

    [Fact]
    public void Duplicate_target_and_repo_ids()
    {
        var c = ConfigFixtures.Minimal();
        c.Targets.Add(new TargetConfig { Id = "usb", Path = @"Y:\" });
        c.ResticRepos.Add(new ResticRepoConfig { Id = "r1", Location = "x", PasswordRef = "p" });
        c.ResticRepos.Add(new ResticRepoConfig { Id = "r1", Location = "y", PasswordRef = "p" });
        var r = V(c);
        Assert.True(r.Has("id.duplicate", "targets[1].id"), r.ToString());
        Assert.True(r.Has("id.duplicate", "restic_repos[1].id"), r.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData("-lead")]
    [InlineData("a/b")]
    public void Invalid_ids(string id)
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Id = id;
        AssertError(c, id.Length == 0 ? "required" : "id.invalid", "jobs[0].id");
    }

    [Fact]
    public void Id_max_length_64()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Id = new string('a', 65);
        AssertError(c, "id.invalid", "jobs[0].id");
        c.Jobs[0].Id = new string('a', 64);
        Assert.True(V(c).IsValid);
    }

    [Fact]
    public void Unknown_target_reference()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Target!.TargetId = "nope";
        AssertError(c, "ref.target", "jobs[0].target.target_id");
    }

    [Fact]
    public void Unknown_repo_reference()
    {
        var c = ConfigFixtures.Example();
        c.Jobs[2].Restic!.RepoId = "missing";
        AssertError(c, "ref.repo", "jobs[2].restic.repo_id", ConfigFixtures.ExampleSecrets());
    }

    [Fact]
    public void Unknown_on_success_reference()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].OnSuccess = ["ghost"];
        AssertError(c, "ref.job", "jobs[0].on_success[0]");
    }

    [Fact]
    public void Chain_cycle_detected()
    {
        var c = ConfigFixtures.Example();
        c.Jobs[2].OnSuccess = ["sql-main"];
        var r = V(c, ConfigFixtures.ExampleSecrets());
        var e = Assert.Single(r.Errors, x => x.Code == "chain.cycle");
        Assert.Contains("sql-main → sql-to-offsite → sql-main", e.Message(Texts.English), StringComparison.Ordinal);
    }

    [Fact]
    public void Self_chain_is_a_cycle()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].OnSuccess = ["docs"];
        AssertError(c, "chain.cycle", "jobs[0].on_success");
    }

    [Fact]
    public void Three_job_cycle_reported_once()
    {
        var c = ConfigFixtures.Minimal();
        foreach (var id in new[] { "b", "c" })
        {
            var j = ConfigJson.Clone(c).Jobs[0];
            j.Id = id;
            j.Target!.Subdir = id;
            c.Jobs.Add(j);
        }
        c.Jobs[0].OnSuccess = ["b"];
        c.Jobs[1].OnSuccess = ["c"];
        c.Jobs[2].OnSuccess = ["docs"];
        Assert.Single(V(c).Errors, x => x.Code == "chain.cycle");
    }

    [Fact]
    public void Diamond_chain_is_not_a_cycle()
    {
        var c = ConfigFixtures.Minimal();
        foreach (var id in new[] { "b", "c", "d" })
        {
            var j = ConfigJson.Clone(c).Jobs[0];
            j.Id = id;
            j.Target!.Subdir = id;
            c.Jobs.Add(j);
        }
        c.Jobs[0].OnSuccess = ["b", "c"];
        c.Jobs[1].OnSuccess = ["d"];
        c.Jobs[2].OnSuccess = ["d"];
        Assert.True(V(c).IsValid, V(c).ToString());
    }

    [Fact]
    public void Staging_source_must_point_to_sql_job()
    {
        var c = ConfigFixtures.Example();
        c.Jobs[2].Sources[0].Path = "@staging:company-mirror";
        AssertError(c, "staging.not_sql", "jobs[2].sources[0].path", ConfigFixtures.ExampleSecrets());
        c.Jobs[2].Sources[0].Path = "@staging:nothing";
        AssertError(c, "ref.job", "jobs[2].sources[0].path", ConfigFixtures.ExampleSecrets());
    }

    [Fact]
    public void Two_files_jobs_same_target_folder()
    {
        var c = ConfigFixtures.Minimal();
        var j = ConfigJson.Clone(c).Jobs[0];
        j.Id = "docs2";
        j.Sources = [new SourceConfig { Path = @"E:\Other" }];
        j.Target!.Subdir = "docs"; // differs only in case → same folder on Windows
        c.Jobs.Add(j);
        AssertError(c, "target.shared", "jobs[1].target");
    }

    [Fact]
    public void Same_target_different_subdir_is_fine()
    {
        var c = ConfigFixtures.Minimal();
        var j = ConfigJson.Clone(c).Jobs[0];
        j.Id = "docs2";
        j.Target!.Subdir = "Docs2";
        c.Jobs.Add(j);
        Assert.True(V(c).IsValid, V(c).ToString());
    }

    [Theory]
    [InlineData(@"X:\Backup\Docs", @"X:\Backup", "Docs")]           // source == mirror
    [InlineData(@"X:\Backup", @"X:\Backup", "Docs")]                // mirror inside source
    [InlineData(@"x:\backup\docs\sub", @"X:\Backup", "Docs")]       // source inside mirror (case-insensitive)
    [InlineData(@"D:\Data", @"D:\Data", "")]                        // target root == source
    public void Source_and_target_must_not_nest(string source, string targetPath, string subdir)
    {
        var c = ConfigFixtures.Minimal();
        c.Targets[0].Path = targetPath;
        c.Jobs[0].Target!.Subdir = subdir;
        c.Jobs[0].Files!.Versioning.Folder = subdir.Length == 0 ? @"Z:\V" : null;
        c.Jobs[0].Sources[0].Path = source;
        AssertError(c, "path.nested", "jobs[0].sources[0].path");
    }

    [Fact]
    public void Sibling_folder_with_common_prefix_is_not_nested()
    {
        var c = ConfigFixtures.Minimal();
        c.Targets[0].Path = @"D:\";
        c.Jobs[0].Target!.Subdir = "DataBackup";
        c.Jobs[0].Sources[0].Path = @"D:\Data";
        Assert.True(V(c).IsValid, V(c).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Delete_guard_pct_range(int pct)
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Files!.DeleteGuardPct = pct;
        AssertError(c, "range", "jobs[0].files.delete_guard_pct");
    }

    [Fact]
    public void Missing_secret_reported()
    {
        var c = ConfigFixtures.Example();
        var secrets = ConfigFixtures.ExampleSecrets();
        secrets.Remove("restic:offsite");
        secrets.Remove("target:nas1");
        var r = V(c, secrets);
        Assert.True(r.Has("ref.secret", "restic_repos[0].password_ref"), r.ToString());
        Assert.True(r.Has("ref.secret", "targets[0].password_ref"), r.ToString());
    }

    [Fact]
    public void Secrets_not_checked_without_store()
    {
        Assert.True(V(ConfigFixtures.Example()).IsValid);
    }

    [Fact]
    public void Smb_with_username_needs_password_ref()
    {
        var c = ConfigFixtures.Example();
        c.Targets[0].PasswordRef = null;
        AssertError(c, "required", "targets[0].password_ref", ConfigFixtures.ExampleSecrets());
    }

    [Fact]
    public void Smb_requires_unc_path()
    {
        var c = ConfigFixtures.Minimal();
        c.Targets[0].Type = TargetTypes.Smb;
        AssertError(c, "path.smb_unc", "targets[0].path");
    }

    [Fact]
    public void Target_path_absolute_and_type()
    {
        var c = ConfigFixtures.Minimal();
        c.Targets[0].Path = @"relative\dir";
        c.Targets.Add(new TargetConfig { Id = "x", Type = "ftp", Path = @"C:\x" });
        var r = V(c);
        Assert.True(r.Has("path.not_absolute", "targets[0].path"), r.ToString());
        Assert.True(r.Has("value.invalid", "targets[1].type"), r.ToString());
    }

    [Fact]
    public void Source_path_must_be_absolute_and_present()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Sources = [new SourceConfig { Path = "Docs" }, new SourceConfig { Path = "" }];
        var r = V(c);
        Assert.True(r.Has("path.not_absolute", "jobs[0].sources[0].path"), r.ToString());
        Assert.True(r.Has("required", "jobs[0].sources[1].path"), r.ToString());
        c.Jobs[0].Sources.Clear();
        AssertError(c, "sources.empty", "jobs[0].sources");
    }

    [Fact]
    public void Versioning_folder_required_for_root_target()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Target!.Subdir = "";
        AssertError(c, "versioning.folder_required", "jobs[0].files.versioning.folder");
        // relative to the target root would be inside the mirrored root → rejected (mirror would remove it)
        c.Jobs[0].Files!.Versioning.Folder = "Docs_Versions";
        AssertError(c, "path.versions_inside", "jobs[0].files.versioning.folder");
        c.Jobs[0].Files!.Versioning.Folder = @"Y:\Docs_Versions";
        Assert.True(V(c).IsValid, V(c).ToString());
        c.Jobs[0].Files!.Versioning.Enabled = false;
        c.Jobs[0].Files!.Versioning.Folder = null;
        Assert.True(V(c).IsValid, V(c).ToString());
    }

    [Fact]
    public void Versions_folder_inside_mirror_rejected()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Files!.Versioning.Folder = @"Docs\Versions";
        AssertError(c, "path.versions_inside", "jobs[0].files.versioning.folder");
    }

    [Fact]
    public void Type_specific_blocks_required()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Files = null;
        c.Jobs[0].Target = null;
        c.Jobs.Add(new JobConfig { Id = "r", Name = "r", Type = JobTypes.Restic, Sources = [new SourceConfig { Path = @"C:\a" }] });
        c.Jobs.Add(new JobConfig { Id = "s", Name = "s", Type = JobTypes.Sql });
        var r = V(c);
        Assert.True(r.Has("block.required", "jobs[0].files"), r.ToString());
        Assert.True(r.Has("block.required", "jobs[0].target"), r.ToString());
        Assert.True(r.Has("block.required", "jobs[1].restic"), r.ToString());
        Assert.True(r.Has("block.required", "jobs[2].sql"), r.ToString());
    }

    [Fact]
    public void Job_type_and_mode_values()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Files!.Mode = "sync";
        c.Jobs[0].Files!.Vss = "always";
        c.Jobs[0].Files!.Compare = "hash";
        c.Jobs[0].Files!.CloudFiles = "download";
        c.Jobs[0].Files!.Parallelism = 0;
        var r = V(c);
        foreach (var f in new[] { "mode", "vss", "compare", "cloud_files" })
            Assert.True(r.Has("value.invalid", $"jobs[0].files.{f}"), f + "\n" + r);
        Assert.True(r.Has("range", "jobs[0].files.parallelism"));
        c.Jobs[0].Type = "tape";
        AssertError(c, "value.invalid", "jobs[0].type");
    }

    [Fact]
    public void Name_required()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Name = "";
        AssertError(c, "required", "jobs[0].name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    public void Interval_every_min_required_and_at_least_5(int? every)
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = every };
        AssertError(c, every is null ? "required" : "range", "jobs[0].schedule.every_min");
    }

    [Fact]
    public void Interval_start_and_window_times()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 60, Start = "6:15", Window = new TimeWindow { From = "06:00", To = "24:00" } };
        var r = V(c);
        Assert.True(r.Has("time.invalid", "jobs[0].schedule.start"), r.ToString());
        Assert.True(r.Has("time.invalid", "jobs[0].schedule.window.to"), r.ToString());
    }

    [Fact]
    public void Daily_requires_valid_times_and_days()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = [] };
        AssertError(c, "required", "jobs[0].schedule.times");
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["12:60"], Days = ["Mon", "Funday"] };
        var r = V(c);
        Assert.True(r.Has("time.invalid", "jobs[0].schedule.times[0]"), r.ToString());
        Assert.True(r.Has("day.invalid", "jobs[0].schedule.days[1]"), r.ToString());
    }

    [Fact]
    public void Weekly_and_monthly_fields()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Weekly, Day = "Sunday", Time = null };
        var r = V(c);
        Assert.True(r.Has("day.invalid", "jobs[0].schedule.day"), r.ToString());
        Assert.True(r.Has("required", "jobs[0].schedule.time"), r.ToString());
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Monthly, DayOfMonth = "31", Time = "01:00" };
        AssertError(c, "value.invalid", "jobs[0].schedule.day_of_month");
        c.Jobs[0].Schedule.DayOfMonth = "last";
        Assert.True(V(c).IsValid);
    }

    [Fact]
    public void Unknown_schedule_kind()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = "hourly" };
        AssertError(c, "value.invalid", "jobs[0].schedule.kind");
    }

    [Fact]
    public void Repo_maintenance_strings()
    {
        var c = ConfigFixtures.Example();
        c.ResticRepos[0].Maintenance.ForgetPrune = "every sunday";
        c.ResticRepos[0].Maintenance.CheckReadDataSubset = "5";
        var r = V(c, ConfigFixtures.ExampleSecrets());
        Assert.True(r.Has("recurrence.invalid", "restic_repos[0].maintenance.forget_prune"), r.ToString());
        Assert.True(r.Has("percent.invalid", "restic_repos[0].maintenance.check_read_data_subset"), r.ToString());
    }

    [Fact]
    public void Repo_location_and_password_required()
    {
        var c = ConfigFixtures.Minimal();
        c.ResticRepos.Add(new ResticRepoConfig { Id = "r" });
        var r = V(c);
        Assert.True(r.Has("required", "restic_repos[0].location"), r.ToString());
        Assert.True(r.Has("required", "restic_repos[0].password_ref"), r.ToString());
    }

    [Fact]
    public void Sql_block_rules()
    {
        var c = ConfigFixtures.Example();
        var sql = c.Jobs[1].Sql!;
        sql.Instance = "";
        sql.Auth = SqlAuthModes.Sql;
        sql.Databases.Mode = SqlDbModes.Selected;
        sql.Compression = "max";
        sql.KeepLocal = -1;
        sql.Checkdb = "now";
        var r = V(c, ConfigFixtures.ExampleSecrets());
        Assert.True(r.Has("required", "jobs[1].sql.instance"), r.ToString());
        Assert.True(r.Has("required", "jobs[1].sql.username"), r.ToString());
        Assert.True(r.Has("required", "jobs[1].sql.login_ref"), r.ToString());
        Assert.True(r.Has("sql.selected_empty", "jobs[1].sql.databases.include"), r.ToString());
        Assert.True(r.Has("value.invalid", "jobs[1].sql.compression"), r.ToString());
        Assert.True(r.Has("range", "jobs[1].sql.keep_local"), r.ToString());
        Assert.True(r.Has("recurrence.invalid", "jobs[1].sql.checkdb"), r.ToString());
    }

    [Fact]
    public void Email_rules()
    {
        var c = ConfigFixtures.Example();
        var m = c.Notifications.Email;
        m.SmtpHost = "";
        m.SmtpPort = 0;
        m.From = "not-an-address";
        m.To = ["it@example.com", "broken"];
        m.Security = "tls13";
        m.WeeklyReport = "Sunday";
        var r = V(c, ConfigFixtures.ExampleSecrets());
        Assert.True(r.Has("required", "notifications.email.smtp_host"), r.ToString());
        Assert.True(r.Has("range", "notifications.email.smtp_port"), r.ToString());
        Assert.True(r.Has("email.address_invalid", "notifications.email.from"), r.ToString());
        Assert.True(r.Has("email.address_invalid", "notifications.email.to[1]"), r.ToString());
        Assert.True(r.Has("value.invalid", "notifications.email.security"), r.ToString());
        Assert.True(r.Has("recurrence.invalid", "notifications.email.weekly_report"), r.ToString());
        m.To.Clear();
        Assert.True(V(c, ConfigFixtures.ExampleSecrets()).Has("required", "notifications.email.to"));
    }

    [Fact]
    public void Disabled_email_is_not_validated()
    {
        var c = ConfigFixtures.Minimal();
        c.Notifications.Email = new EmailSettings { Enabled = false, SmtpHost = "" };
        Assert.True(V(c).IsValid);
    }

    [Theory]
    [InlineData("ftp://hc.example.com/x", false)]
    [InlineData("not a url", false)]
    [InlineData("https://hc-ping.com/abc", true)]
    public void Heartbeat_url(string url, bool ok)
    {
        var c = ConfigFixtures.Minimal();
        c.Notifications.HeartbeatUrl = url;
        Assert.Equal(ok, V(c).IsValid);
    }

    [Fact]
    public void Expected_interval_and_timeout_ranges()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].ExpectedIntervalH = 0;
        c.Jobs[0].TimeoutMin = 0;
        var r = V(c);
        Assert.True(r.Has("range", "jobs[0].expected_interval_h"));
        Assert.True(r.Has("range", "jobs[0].timeout_min"));
    }

    [Fact]
    public void Messages_are_localized()
    {
        var c = ConfigFixtures.Minimal();
        c.Jobs[0].Target!.TargetId = "nope";
        var e = Assert.Single(V(c).Errors);
        Assert.Equal("Unknown target 'nope'", e.Message(Texts.English));
        Assert.Equal("Ismeretlen cél: 'nope'", e.Message(Texts.Hungarian));
    }

    [Fact]
    public void Every_error_code_has_both_translations()
    {
        var codes = typeof(ConfigValidator).Assembly.GetManifestResourceNames();
        Assert.Contains("BackupForge.Core.Resources.Strings.resources", codes);
        foreach (var code in new[] { "required", "range", "ref.secret", "chain.cycle", "path.nested", "target.shared", "staging.not_sql" })
        {
            Assert.NotEqual(code, Texts.Get(code, Texts.English));
            Assert.NotEqual(code, Texts.Get(code, Texts.Hungarian));
            Assert.NotEqual(Texts.Get(code, Texts.English), Texts.Get(code, Texts.Hungarian));
        }
    }
}
