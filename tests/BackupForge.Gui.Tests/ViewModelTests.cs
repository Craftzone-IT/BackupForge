using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Gui.Core;
using BackupForge.Gui.Core.ViewModels;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Gui.Tests;

public sealed class FakeClient : IServiceClient
{
    public BackupConfig Config { get; set; } = ConfigJson.Parse(File.ReadAllText(TestPaths.Example("config.example.json")));
    public List<(string Op, JsonObject? Args)> Calls { get; } = [];
    public Func<string, JsonObject?, JsonNode?>? Handler { get; set; }
    public event Action<IpcEvent>? EventReceived;

    public void Raise(IpcEvent e) => EventReceived?.Invoke(e);

    public Task<JsonNode?> CallAsync(string op, JsonObject? args = null, CancellationToken ct = default)
    {
        Calls.Add((op, args));
        if (Handler?.Invoke(op, args) is { } custom) return Task.FromResult<JsonNode?>(custom);
        return Task.FromResult<JsonNode?>(op switch
        {
            IpcOps.ConfigGet => new JsonObject { ["config"] = JsonNode.Parse(ConfigJson.Serialize(Config)) },
            IpcOps.SecretsList => new JsonArray(new JsonObject { ["ref"] = "target:nas1", ["is_set"] = true }),
            IpcOps.StatusGet => JsonNode.Parse(File.ReadAllText(TestPaths.Example("status.example.json"))),
            IpcOps.ConfigSave => SaveConfig(args!),
            _ => new JsonObject(),
        });
    }

    private JsonNode SaveConfig(JsonObject args)
    {
        var cfg = ConfigJson.Parse(args["config"]!.GetValue<string>());
        var v = ConfigValidator.Validate(cfg);
        if (!v.IsValid)
        {
            var arr = new JsonArray();
            foreach (var e in v.Errors) arr.Add(new JsonObject { ["path"] = e.Path, ["message"] = e.Message(BackupForge.Core.Resources.Texts.English) });
            throw new IpcException(IpcErrorCodes.Invalid, "invalid configuration", arr);
        }
        Config = cfg;
        return new JsonObject { ["saved"] = true };
    }
}

public class ViewModelTests
{
    private static async Task<(MainViewModel Vm, FakeClient Client, NullDialogs Dialogs)> Create()
    {
        var c = new FakeClient();
        var d = new NullDialogs();
        var vm = new MainViewModel(c, d);
        await vm.RefreshAsync();
        return (vm, c, d);
    }

    [Fact]
    public async Task Refresh_loads_config_status_and_overview()
    {
        var (vm, _, _) = await Create();
        Assert.Null(vm.ConnectionError);
        Assert.Equal(3, vm.Overview.Cards.Count);
        var card = vm.Overview.Cards.Single(c => c.Id == "company-mirror");
        Assert.Equal("ok", card.Result);
        Assert.Contains("312 másolva", card.Summary, StringComparison.Ordinal);
        Assert.True(vm.Overview.Cards.Single(c => c.Id == "sql-to-offsite").IsRunning);
        Assert.Contains("target:nas1", vm.SetSecrets);
        Assert.Equal(3, vm.Jobs.Jobs.Count);
        Assert.Equal(2, vm.Targets.Targets.Count);
        Assert.True(vm.Notifications.Enabled);
    }

    [Fact]
    public async Task Job_editor_round_trip_is_lossless()
    {
        var (vm, c, _) = await Create();
        foreach (var j in vm.Jobs.Jobs)
        {
            var original = ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Jobs = [c.Config.FindJob(j.Id)!] });
            Assert.Equal(original, ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Jobs = [j.ToJob()] }));
        }
    }

    [Fact]
    public async Task Editing_and_saving_sends_the_change()
    {
        var (vm, c, d) = await Create();
        var job = vm.Jobs.Jobs.Single(j => j.Id == "company-mirror");
        job.Name = "Renamed";
        job.Enabled = false;
        Assert.True(vm.IsDirty);
        Assert.True(await vm.SaveAsync());
        Assert.Equal("Renamed", c.Config.FindJob("company-mirror")!.Name);
        Assert.False(c.Config.FindJob("company-mirror")!.Enabled);
        Assert.Contains(d.Messages, m => m.StartsWith("i ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validation_errors_are_shown_per_field()
    {
        var (vm, c, d) = await Create();
        var job = vm.Jobs.Jobs.Single(j => j.Id == "company-mirror");
        job.GuardPct = "0";
        Assert.False(await vm.SaveAsync());
        Assert.Contains(vm.ValidationErrors, e => e.Path == "jobs[0].files.delete_guard_pct");
        Assert.Contains("delete_guard_pct", job.Errors, StringComparison.Ordinal);
        Assert.Equal(20, c.Config.FindJob("company-mirror")!.Files!.DeleteGuardPct); // not saved
    }

    [Fact]
    public async Task Schedule_preview_and_new_job()
    {
        var (vm, _, _) = await Create();
        var job = vm.Jobs.Jobs.Single(j => j.Id == "company-mirror");
        Assert.Equal("4 óránként 06:15-től", job.SchedulePreview);
        job.ScheduleKind = ScheduleKinds.Daily;
        job.Times = "12:30, 22:30";
        Assert.Equal("naponta 12:30, 22:30", job.SchedulePreview);
        vm.Jobs.AddCommand.Execute(JobTypes.Restic);
        Assert.Equal("restic-1", vm.Jobs.Selected!.Id);
        Assert.True(vm.Jobs.Selected.IsRestic);
        Assert.Equal("offsite", vm.Jobs.Selected.RepoId);
    }

    [Fact]
    public async Task Repo_wizard_generates_password_shows_sheet_saves_then_inits()
    {
        var (vm, c, d) = await Create();
        vm.Targets.SelectedRepo = vm.Targets.Repos[0];
        await vm.Targets.CreateRepoCommand.ExecuteAsync(null);
        var ops = c.Calls.Select(x => x.Op).ToList();
        var set = ops.IndexOf(IpcOps.SecretsSet);
        var save = ops.LastIndexOf(IpcOps.ConfigSave);
        var init = ops.IndexOf(IpcOps.RepoInit);
        Assert.True(set >= 0 && save > set && init > save, string.Join(",", ops));
        var pw = c.Calls[set].Args!["value"]!.ToString();
        Assert.Equal(32, pw.Length);
        Assert.All(pw, ch => Assert.Contains(ch, TargetsViewModel.PasswordAlphabet));
        Assert.Contains(d.Messages, m => m.StartsWith("T ", StringComparison.Ordinal) && m.Contains(pw, StringComparison.Ordinal));
        Assert.True(c.Calls[init].Args!["confirm"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Repo_wizard_stops_without_confirmation()
    {
        var (vm, c, d) = await Create();
        d.ConfirmResult = false;
        vm.Targets.SelectedRepo = vm.Targets.Repos[0];
        await vm.Targets.CreateRepoCommand.ExecuteAsync(null);
        Assert.DoesNotContain(c.Calls, x => x.Op is IpcOps.SecretsSet or IpcOps.RepoInit);
    }

    [Fact]
    public async Task Restore_flow_calls_service_with_new_folder_target()
    {
        var (vm, c, d) = await Create();
        c.Handler = (op, _) => op switch
        {
            IpcOps.RestoreVersions => new JsonArray(new JsonObject { ["point"] = "current" }, new JsonObject { ["point"] = "2026-11-20 061500", ["time"] = "2026-11-20 06:15:00" }),
            IpcOps.RestoreBrowse => new JsonArray(new JsonObject { ["name"] = "Docs", ["dir"] = true }, new JsonObject { ["name"] = "a.txt", ["dir"] = false, ["size"] = 10 }),
            IpcOps.RestoreRun => new JsonObject { ["result"] = "ok", ["summary"] = "1 restored" },
            _ => null,
        };
        vm.Restore.JobId = "company-mirror";
        await Task.Delay(50);
        Assert.Equal(2, vm.Restore.Points.Count);
        vm.Restore.Point = vm.Restore.Points[1];
        await Task.Delay(50);
        await vm.Restore.OpenCommand.ExecuteAsync(vm.Restore.Entries[0]);
        Assert.Equal("Docs", vm.Restore.CurrentPath);
        vm.Restore.SelectedEntry = vm.Restore.Entries.Single(e => e.Name == "a.txt");
        vm.Restore.TargetFolder = @"D:\Restore";
        await vm.Restore.RestoreCommand.ExecuteAsync(null);
        var call = c.Calls.Last(x => x.Op == IpcOps.RestoreRun).Args!;
        Assert.Equal(@"Docs\a.txt", call["path"]!.ToString());
        Assert.Equal("2026-11-20 061500", call["point"]!.ToString());
        Assert.Equal(@"D:\Restore", call["to"]!.ToString());
        Assert.False(call["to_original"]!.GetValue<bool>());
        Assert.Equal("ok: 1 restored", vm.Restore.Result);
    }

    [Fact]
    public async Task Job_lists_of_restore_history_and_sql_follow_the_configuration()
    {
        // H1 / CR-002 B1.: the computed lists must notify after every load (also after a save)
        var c = new FakeClient();
        var vm = new MainViewModel(c, new NullDialogs());
        var changed = new List<string>();
        vm.Restore.PropertyChanged += (_, e) => changed.Add("restore." + e.PropertyName);
        vm.History.PropertyChanged += (_, e) => changed.Add("history." + e.PropertyName);
        vm.Sql.PropertyChanged += (_, e) => changed.Add("sql." + e.PropertyName);
        Assert.Empty(vm.Restore.JobIds);
        await vm.RefreshAsync();
        Assert.Contains("restore.JobIds", changed);
        Assert.Contains("history.JobIds", changed);
        Assert.Contains("sql.SqlJobIds", changed);
        Assert.Equal(["company-mirror", "sql-to-offsite"], vm.Restore.JobIds.Order(StringComparer.Ordinal));
        Assert.Equal(3, vm.History.JobIds.Count());
        Assert.Equal(["sql-main"], vm.Sql.SqlJobIds);
        changed.Clear();
        Assert.True(await vm.SaveAsync());
        Assert.Contains("restore.JobIds", changed);
    }

    [Fact]
    public async Task Missing_secrets_after_import()
    {
        var (_, c, _) = await Create();
        var missing = SettingsViewModel.MissingSecrets(c.Config, new HashSet<string> { "target:nas1" });
        Assert.Equal(["restic:offsite", "smtp"], missing);
    }

    [Fact]
    public async Task Service_down_shows_connection_error()
    {
        var c = new FakeClient { Handler = (_, _) => throw new IpcUnavailableException("the BackupForge service is not reachable") };
        var vm = new MainViewModel(c, new NullDialogs());
        await vm.RefreshAsync();
        Assert.Contains("not reachable", vm.ConnectionError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_events_update_the_card()
    {
        var (vm, c, _) = await Create();
        c.Raise(new IpcEvent { Event = "run.progress", Data = new JsonObject { ["job_id"] = "company-mirror", ["phase"] = "copy", ["percent"] = 42.0 } });
        var card = vm.Overview.Cards.Single(x => x.Id == "company-mirror");
        Assert.True(card.IsRunning);
        Assert.Equal(42.0, card.Percent);
    }

    [Fact]
    public void Ui_strings_exist_in_both_languages()
    {
        Ui.Culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var en = Ui.T("saved");
        Ui.Culture = System.Globalization.CultureInfo.GetCultureInfo("hu-HU");
        Assert.NotEqual(en, Ui.T("saved"));
        Assert.Contains("test-pw", TargetsViewModel.RecoverySheet("sftp:x", "test-pw", DateTime.Now, "SRV01"), StringComparison.Ordinal);
    }
}
