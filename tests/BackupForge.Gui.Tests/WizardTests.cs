using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BackupForge.Core.Config;
using BackupForge.Core.Resources;
using BackupForge.Gui.Core;
using BackupForge.Gui.Core.Wizard;
using BackupForge.Ipc;

namespace BackupForge.Gui.Tests;

/// <summary>CR-002 B2./B5.: the job wizard, fully driven through its ViewModel with a fake service.</summary>
public class WizardTests
{
    private static string JobJson(JobConfig j) => ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Jobs = [j] });

    private static async Task<JobWizardViewModel> Walk(JobWizardViewModel w)
    {
        for (var i = 0; i < 12 && !w.IsLastStep; i++)
        {
            Assert.True(w.CanGoNext, $"{w.Current}: {w.CurrentError}");
            w.Next();
        }
        Assert.Equal(WizardStep.Summary, w.Current);
        await w.ValidateDraftAsync();
        return w;
    }

    [Fact]
    public async Task New_files_job_from_type_to_finish()
    {
        var (vm, c, _) = await ViewModelTests.Create();
        var w = vm.Jobs.CreateWizard();
        Assert.Equal(WizardStep.Type, w.Current);
        w.SelectType(JobTypes.Files);
        Assert.Equal(WizardStep.Source, w.Current);
        Assert.False(w.CanGoNext);                          // no source yet
        Assert.Equal(Ui.T("err_no_source"), w.CurrentError);
        w.AddSourcePath(@"D:\Projektek");
        w.Next();
        Assert.Equal(WizardStep.Target, w.Current);
        Assert.Equal("nas1", w.Draft.TargetId);             // the first target, from a drop-down
        Assert.Equal("Projektek", w.Draft.Subdir);          // the folder name of the source
        Assert.Equal(@"\\192.168.1.10\backup\srv01\Projektek", w.MirrorPreview);
        Assert.Equal(@"\\192.168.1.10\backup\srv01\Projektek_Versions", w.VersionsPreview);
        w.Next();
        w.IsUpdate = true;
        w.Draft.KeepDays = "30";
        w.Next();
        w.ScheduleKindChoice = ScheduleKinds.Weekly;
        w.Draft.Day = "Sat";
        w.Draft.Time = "23:15";
        Assert.Equal("hetente, szombat 23:15", w.SchedulePreview);
        Assert.Equal(5, w.NextRuns.Count);
        w.Next();
        Assert.Equal("Projektek → NAS1", w.Draft.Name);     // the default name
        Assert.Equal("projektek-nas1", w.Draft.Id);         // the slug of the name
        await Walk(w);
        Assert.Contains("Projektek", w.SummaryText, StringComparison.Ordinal);
        Assert.Contains("30 napig", w.SummaryText, StringComparison.Ordinal);
        Assert.Empty(w.Problems);
        Assert.True(await w.FinishAsync());
        var job = c.Config.FindJob("projektek-nas1")!;
        Assert.Equal(FilesModes.Update, job.Files!.Mode);
        Assert.Equal(30, job.Files.Versioning.KeepDays);
        Assert.Equal([@"D:\Projektek"], job.Sources.Select(s => s.Path));
        Assert.Equal(new JobTargetRef { TargetId = "nas1", Subdir = "Projektek" }.Subdir, job.Target!.Subdir);
        Assert.Equal(ScheduleKinds.Weekly, job.Schedule.Kind);
        Assert.True(w.Completed);
        Assert.Equal(4, vm.Jobs.Rows.Count);               // the table reloaded
    }

    [Fact]
    public async Task New_restic_job_from_the_sql_staging_runs_after_the_sql_job()
    {
        var (vm, c, _) = await ViewModelTests.Create();
        var w = vm.Jobs.CreateWizard();
        w.SelectType(JobTypes.Restic);
        w.UseStagingSource = true;
        Assert.Equal("sql-main", w.StagingJobId);
        Assert.Equal(ScheduleKinds.Chained, w.ScheduleKindChoice); // the default after an SQL job
        Assert.Equal("sql-main", w.ChainParentId);
        await Walk(w);
        Assert.True(await w.FinishAsync());
        var job = c.Config.FindJob(w.Draft.Id)!;
        Assert.Equal("@staging:sql-main", job.Sources.Single().Path);
        Assert.Equal("offsite", job.Restic!.RepoId);
        Assert.Contains(job.Id, c.Config.FindJob("sql-main")!.OnSuccess);
    }

    [Fact]
    public async Task New_sql_job_with_repository_creates_the_chained_pair()
    {
        var (vm, c, _) = await ViewModelTests.Create();
        c.Handler = (op, _) => op == IpcOps.SqlDiscover
            ? new JsonArray(new JsonObject { ["instance"] = @".\ERP", ["edition"] = "Express", ["databases"] = new JsonArray(
                new JsonObject { ["name"] = "erp", ["recovery_model"] = "FULL", ["size_bytes"] = 1000, ["system"] = false },
                new JsonObject { ["name"] = "hr", ["recovery_model"] = "SIMPLE", ["size_bytes"] = 1000, ["system"] = false }) })
            : null;
        var w = vm.Jobs.CreateWizard();
        w.SelectType(JobTypes.Sql);
        w.Draft.SqlInstance = @".\ERP";
        w.Next();
        await w.DiscoverAsync();
        w.SelectedDatabases = true;
        Assert.False(w.CanGoNext);                          // nothing picked yet
        w.DbRows.Single(d => d.Name == "erp").Selected = true;
        Assert.NotNull(w.DbRows.Single(d => d.Name == "erp").Warning); // FULL recovery
        w.Next();
        w.AlsoToRepo = true;
        Assert.Equal("offsite", w.AlsoRepoId);
        w.Draft.KeepLocal = "3";
        await Walk(w);
        Assert.Contains("offsite", w.SummaryText, StringComparison.Ordinal);
        Assert.True(await w.FinishAsync());
        var sql = c.Config.FindJob(w.Draft.Id)!;
        Assert.Equal(["erp"], sql.Sql!.Databases.Include);
        Assert.Equal(SqlDbModes.Selected, sql.Sql.Databases.Mode);
        Assert.Equal(3, sql.Sql.KeepLocal);
        var chained = c.Config.FindJob(w.Draft.Id + "-restic")!;
        Assert.Equal(JobTypes.Restic, chained.Type);
        Assert.Equal("@staging:" + sql.Id, chained.Sources.Single().Path);
        Assert.Equal(ScheduleKinds.Chained, chained.Schedule.Kind);
        Assert.Equal([chained.Id], sql.OnSuccess);
    }

    [Fact]
    public async Task Editing_without_changes_is_byte_identical_for_every_job()
    {
        var (vm, c, _) = await ViewModelTests.Create();
        // plus a files job with every field set, to cover the whole editor
        var rich = ConfigJson.Clone(c.Config);
        rich.Jobs.Add(new JobConfig
        {
            Id = "full", Name = "Full", Type = JobTypes.Files, Enabled = false, CatchUp = false, ExpectedIntervalH = 30, TimeoutMin = 90,
            Schedule = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 120, Start = "07:10", Window = new TimeWindow { From = "07:00", To = "19:00" } },
            Sources = [new SourceConfig { Path = @"D:\A", Excludes = ["*.bak"] }, new SourceConfig { Path = @"D:\B" }],
            Target = new JobTargetRef { TargetId = "usb", Subdir = "Full" },
            Files = new FilesOptions { Mode = FilesModes.Update, Vss = VssModes.Required, DeleteGuardPct = 35, DeleteGuardMinFiles = 70, Compare = CompareModes.Content,
                Parallelism = 8, ThrottleMbps = 50, CloudFiles = CloudFilesModes.Fail, UseDefaultExcludes = false,
                Versioning = new VersioningOptions { KeepDays = 45, MaxVersionsPerFile = 5, Folder = @"X:\Regi" } },
        });
        c.Config = rich;
        await vm.RefreshAsync();
        foreach (var job in c.Config.Jobs.ToList())
        {
            var before = JobJson(job);
            var all = ConfigJson.Serialize(c.Config);
            var w = vm.Jobs.CreateWizard(job.Id);
            Assert.False(w.IsNew);
            Assert.DoesNotContain(w.Steps, s => s.Step == WizardStep.Type); // the type of an existing job stays
            await Walk(w);
            Assert.True(await w.FinishAsync(), string.Join("; ", w.Problems.Select(p => p.Text)));
            Assert.Equal(before, JobJson(c.Config.FindJob(job.Id)!));
            Assert.Equal(all, ConfigJson.Serialize(c.Config));
        }
    }

    [Fact]
    public async Task Edit_rename_disable_and_human_errors()
    {
        var (vm, c, d) = await ViewModelTests.Create();
        var w = vm.Jobs.CreateWizard("company-mirror");
        w.Draft.Name = "Céges adatok";
        w.Draft.Enabled = false;
        w.Draft.GuardPct = "0";
        w.GoTo(WizardStep.Mode);
        Assert.Equal(Ui.T("err_guard_pct"), w.CurrentError);
        w.Draft.GuardPct = "25";
        Assert.True(await w.FinishAsync());
        var job = c.Config.FindJob("company-mirror")!;
        Assert.Equal("Céges adatok", job.Name);
        Assert.False(job.Enabled);
        Assert.Equal(25, job.Files!.DeleteGuardPct);

        // a service-side error: human words, the step to open — never "jobs[0].target.target_id"
        var w2 = vm.Jobs.CreateWizard("company-mirror");
        w2.Draft.TargetId = "nope";
        Assert.False(await w2.FinishAsync());
        var p = Assert.Single(w2.Problems, x => !x.IsWarning);
        Assert.StartsWith("Céges adatok: Cél: ", p.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("jobs[", p.Text, StringComparison.Ordinal);
        Assert.Equal(WizardStep.Target, p.Target!.Step);
        w2.OpenProblemCommand.Execute(p);
        Assert.Equal(WizardStep.Target, w2.Current);
        Assert.Equal("nas1", c.Config.FindJob("company-mirror")!.Target!.TargetId); // not saved
    }

    [Fact]
    public async Task Overlap_with_another_job_shows_on_the_target_step()
    {
        var (vm, _, _) = await ViewModelTests.Create();
        var w = vm.Jobs.CreateWizard();
        w.SelectType(JobTypes.Files);
        w.AddSourcePath(@"D:\Minden");
        w.Next();
        w.Draft.Subdir = "";                                  // the share root: over company-mirror's mirror
        w.Draft.VersionsFolder = @"X:\Regi";
        await w.ValidateDraftAsync();
        Assert.Contains(w.TargetProblems, e => e.Text.Contains("átfedi", StringComparison.Ordinal));
        Assert.False(w.CanFinish);
    }

    [Fact]
    public async Task Step_checks_block_next()
    {
        var (vm, _, _) = await ViewModelTests.Create();
        var w = vm.Jobs.CreateWizard();
        w.SelectType(JobTypes.Files);
        w.AddSourcePath(@"D:\X");
        w.Next();
        w.Draft.TargetId = "";
        Assert.False(w.CanGoNext);
        Assert.Equal(Ui.T("err_no_target"), w.CurrentError);
        w.Draft.TargetId = "usb";
        w.Next();
        w.Next();
        w.ScheduleKindChoice = ScheduleKinds.Daily;
        w.Draft.Times = "25:99";
        Assert.False(w.CanGoNext);
        Assert.Equal(Ui.T("err_time"), w.CurrentError);
        w.Draft.Times = "06:00, 18:00";
        w.ToggleDay("Sun");                                    // every day minus Sunday
        Assert.Equal("Mon, Tue, Wed, Thu, Fri, Sat", w.Draft.Days);
        Assert.True(w.CanGoNext);
        // clicking a later step jumps no further than the first step with an error
        w.GoTo(WizardStep.Source);
        w.SourceRows.Clear();
        w.GoTo(WizardStep.Summary);
        Assert.Equal(WizardStep.Source, w.Current);
    }

    [Fact]
    public async Task Chain_choices_exclude_itself_and_jobs_that_would_make_a_cycle()
    {
        var (vm, _, _) = await ViewModelTests.Create();
        var w = vm.Jobs.CreateWizard("sql-main");
        var ids = w.ChainChoices.Select(x => x.Id).ToList();
        Assert.DoesNotContain("sql-main", ids);
        Assert.DoesNotContain("sql-to-offsite", ids);            // already runs after sql-main
        Assert.Contains("company-mirror", ids);
        Assert.Equal("sql-main", vm.Jobs.CreateWizard("sql-to-offsite").ChainParentId);
    }

    [Theory]
    [InlineData("Céges adatok → NAS1", "ceges-adatok-nas1")]
    [InlineData("  Árvíztűrő   tükörfúrógép  ", "arvizturo-tukorfurogep")]
    [InlineData("SQL .\\SQLEXPRESS", "sql-sqlexpress")]
    [InlineData("→→", "job")]
    public void Slug_from_names(string name, string id) => Assert.Equal(id, Slug.From(name));

    [Fact]
    public void Slug_collision_gets_a_number()
    {
        Assert.Equal("docs-2", Slug.Unique("docs", ["docs"]));
        Assert.Equal("docs-3", Slug.Unique("docs", ["docs", "docs-2"]));
        Assert.True(Slug.IsValid("docs-3"));
        Assert.False(Slug.IsValid("Docs"));
    }

    [Fact]
    public async Task Cancel_and_copy_leave_the_configuration_alone_until_finish()
    {
        var (vm, c, _) = await ViewModelTests.Create();
        var before = ConfigJson.Serialize(c.Config);
        var w = vm.Jobs.CreateWizard("company-mirror");
        w.Draft.Name = "changed";
        w.CancelCommand.Execute(null);                         // Mégse
        Assert.False(w.Completed);
        Assert.Equal(before, ConfigJson.Serialize(c.Config));
        Assert.DoesNotContain(c.Calls, x => x.Op == IpcOps.ConfigSave);

        var copy = vm.Jobs.CreateWizard("company-mirror", copy: true);
        Assert.Equal("company-mirror-2", copy.Draft.Id);
        Assert.Equal(WizardStep.Summary, copy.Current);
        Assert.Equal(before, ConfigJson.Serialize(c.Config));
    }

    [Fact]
    public async Task New_target_from_the_wizard_is_tested_and_saved_with_the_job()
    {
        var (vm, c, d) = await ViewModelTests.Create();
        c.Handler = (op, a) => op == IpcOps.TargetTest ? new JsonObject { ["online"] = true, ["writable"] = true, ["path"] = a!["target"]!["path"]!.ToString(), ["free_gb"] = 100, ["account"] = "SYSTEM" } : null;
        d.OnDialog = o =>
        {
            var t = (NewTargetViewModel)o;
            t.Path = @"\\nas02\backup\srv01";
            t.Username = @"NAS02\bf";
            t.TestCommand.Execute(null);
            t.Ok();
            return t.Done;
        };
        var w = vm.Jobs.CreateWizard();
        w.SelectType(JobTypes.Files);
        w.AddSourcePath(@"D:\Uj");
        w.Next();
        w.NewTargetCommand.Execute(null);
        Assert.Equal("nas", w.Draft.TargetId);
        var test = c.Calls.Last(x => x.Op == IpcOps.TargetTest).Args!;
        Assert.Equal("smb", test["target"]!["type"]!.ToString());
        await Walk(w);
        Assert.True(await w.FinishAsync());
        var t = c.Config.FindTarget("nas")!;
        Assert.Equal(@"\\nas02\backup\srv01", t.Path);
        Assert.Equal("target:nas", t.PasswordRef);
        Assert.Equal("nas", c.Config.FindJob(w.Draft.Id)!.Target!.TargetId);
    }

    [Fact]
    public async Task Mapped_drive_letter_source_offers_the_unc_path()
    {
        var (vm, c, d) = await ViewModelTests.Create();
        c.Handler = (op, _) => op == IpcOps.PathCheck ? new JsonObject { ["exists"] = false, ["listable"] = false, ["is_network_drive_letter"] = true } : null;
        d.NetworkPaths["Z:"] = @"\\fs01\data";
        var w = vm.Jobs.CreateWizard();
        w.SelectType(JobTypes.Files);
        w.AddSourcePath(@"Z:\Projektek");
        await w.CheckSourcesAsync();
        var row = w.SourceRows.Single();
        Assert.Equal(@"\\fs01\data\Projektek", row.SuggestedUnc);
        w.UseUncCommand.Execute(row);
        Assert.Contains(@"\\fs01\data\Projektek", w.Draft.Sources, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_dry_run_sends_the_job_not_the_saved_one()
    {
        var (vm, c, _) = await ViewModelTests.Create();
        c.Handler = (op, _) => op == IpcOps.JobDryRun ? new JsonObject { ["result"] = "ok", ["summary"] = "3 to copy", ["log"] = new JsonArray("INF plan: copy a.txt") } : null;
        var w = vm.Jobs.CreateWizard("company-mirror");
        w.Draft.Subdir = "Company2";
        await w.DryRunCommand.ExecuteAsync(null);
        var args = c.Calls.Last(x => x.Op == IpcOps.JobDryRun).Args!;
        Assert.Equal("Company2", args["job"]!["target"]!["subdir"]!.ToString());
        Assert.Null(args["job_id"]);
        Assert.Contains("plan: copy a.txt", w.DryRunOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(c.Calls, x => x.Op == IpcOps.ConfigSave);
    }

    [Fact]
    public async Task Jobs_table_toggle_and_delete_save_at_once()
    {
        var (vm, c, d) = await ViewModelTests.Create();
        vm.Jobs.Selected = vm.Jobs.Rows.Single(r => r.Id == "sql-to-offsite");
        await vm.Jobs.ToggleEnabledAsync();
        Assert.False(c.Config.FindJob("sql-to-offsite")!.Enabled);
        vm.Jobs.Selected = vm.Jobs.Rows.Single(r => r.Id == "sql-to-offsite");
        await vm.Jobs.DeleteAsync();
        Assert.Contains(d.Messages, m => m.StartsWith("? ", StringComparison.Ordinal) && m.Contains("a célon semmi nem törlődik", StringComparison.Ordinal));
        Assert.Null(c.Config.FindJob("sql-to-offsite"));
        Assert.Empty(c.Config.FindJob("sql-main")!.OnSuccess); // no dangling chain
    }

    // ------------------------------------------------------------------ B5: every validator path has a human place

    private static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(d, "BackupForge.slnx"))) d = Path.GetDirectoryName(d)!;
        return d;
    }

    [Fact]
    public void Every_validator_path_and_code_has_a_human_label()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "BackupForge.Core", "Config", "ConfigValidator.cs"));
        var templates = Regex.Matches(src, "\\$?\"((?:\\{p\\}|jobs|targets|restic_repos|notifications|general|instance_name|language|schema)[^\"]*)\"")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(templates.Count > 60, $"only {templates.Count} path templates found — has the validator changed shape?");
        var missing = new List<string>();
        foreach (var t in templates)
        {
            var concrete = Regex.Replace(t, "\\{(i|k|idx)\\}", "0").Replace("{env}", "AWS_SECRET_ACCESS_KEY", StringComparison.Ordinal).Replace("{name}", "keep_last", StringComparison.Ordinal);
            // {p} is a job, a target or a repository (and jobs[i].schedule inside ValidateSchedule)
            var candidates = concrete.StartsWith("{p}", StringComparison.Ordinal)
                ? new[] { "jobs[0]", "jobs[0].schedule", "targets[0]", "restic_repos[0]" }.Select(p => p + concrete[3..])
                : [concrete];
            if (!candidates.Any(c => ErrorMap.Find(c) is { } e && Ui.T(e.LabelKey) != e.LabelKey)) missing.Add(t);
        }
        foreach (var list in new[] { "jobs", "targets", "restic_repos" }) if (ErrorMap.Find(list + "[0].id") is null) missing.Add(list + "[0].id");
        Assert.True(missing.Count == 0, "no GUI label for: " + string.Join(", ", missing));

        var codes = Regex.Matches(src, "(?:Err|Warn)\\([^,]+, \"([a-z_.]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(codes);
        foreach (var code in codes.Concat(["range", "required", "ref.secret", "path.overlap_job"]))
        {
            Assert.NotEqual(code, Texts.Get(code, Texts.Hungarian));
            Assert.NotEqual(code, Texts.Get(code, Texts.English));
        }
    }

    [Fact]
    public async Task Main_save_errors_are_in_words()
    {
        var (vm, _, d) = await ViewModelTests.Create();
        vm.Settings.MaxParallel = "0";
        Assert.False(await vm.SaveAsync());
        var e = Assert.Single(vm.ValidationErrors);
        Assert.Equal("general.max_parallel_jobs", e.Path);
        Assert.StartsWith("Beállítások: Általános: ", e.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(d.Messages, m => m.Contains("general.max_parallel_jobs", StringComparison.Ordinal));
    }
}
