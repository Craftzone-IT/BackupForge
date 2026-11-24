using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.ViewModels;

public sealed partial class OverviewViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<JobCardViewModel> Cards { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> TargetLines { get; } = [];
    [ObservableProperty] public partial string Header { get; set; } = "";

    public void Update(JsonNode? s)
    {
        if (s is null) return;
        Header = $"{s["instance_name"]} — BackupForge {s["app_version"]}";
        var jobs = s["jobs"]?.AsArray() ?? [];
        var ids = jobs.Select(j => j!["id"]!.ToString()).ToList();
        for (var i = Cards.Count - 1; i >= 0; i--) if (!ids.Contains(Cards[i].Id)) Cards.RemoveAt(i);
        foreach (var j in jobs)
        {
            var id = j!["id"]!.ToString();
            var card = Cards.FirstOrDefault(c => c.Id == id);
            if (card is null) { card = new JobCardViewModel(main, id); Cards.Add(card); }
            card.Update(j);
        }
        Warnings.Clear();
        if (s["service"]?["config_ok"]?.GetValue<bool>() == false) Warnings.Add(Ui.F("warn_config", s["service"]?["config_error"]));
        foreach (var j in jobs)
        {
            if (j!["enabled"]?.GetValue<bool>() == false) Warnings.Add(Ui.F("warn_disabled", j["name"], Day(j["disabled_since"])));
            if (j["stale"]?.GetValue<bool>() == true) Warnings.Add(Ui.F("warn_stale", j["name"]));
            if (j["delete_guard"]?["tripped"]?.GetValue<bool>() == true) Warnings.Add(Ui.F("warn_guard", j["name"]));
        }
        TargetLines.Clear();
        foreach (var t in s["targets"]?.AsArray() ?? [])
        {
            var free = J.D(t!["free_gb"]);
            var min = J.D(t["min_free_gb"]);
            var online = t["online"]?.GetValue<bool>() == true;
            var line = online ? Ui.F("target_line", t["id"], free?.ToString("0.#", Ui.Culture) ?? "?", J.D(t["size_gb"])?.ToString("0.#", Ui.Culture) ?? "?") : Ui.F("target_offline", t["id"], t["error"]);
            if (online && free < min) { line += " — " + Ui.T("below_min"); Warnings.Add(line); }
            TargetLines.Add(line);
        }
    }

    public void ApplyProgress(JsonNode? d)
    {
        var card = Cards.FirstOrDefault(c => c.Id == d?["job_id"]?.ToString());
        if (card is null) return;
        card.IsRunning = true;
        card.Percent = J.D(d?["percent"]) ?? card.Percent;
        card.State = Ui.F("running_phase", d?["phase"], card.Percent is { } p ? p.ToString("0", Ui.Culture) + " %" : "");
    }

    internal static string Day(JsonNode? iso) => iso is null ? "?" : DateTimeOffset.TryParse(iso.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : iso.ToString();
    internal static string Time(JsonNode? iso) => iso is null ? "–" : DateTimeOffset.TryParse(iso.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : iso.ToString();
}

public sealed partial class JobCardViewModel(MainViewModel main, string id) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Type { get; set; } = "";
    [ObservableProperty] public partial string State { get; set; } = "";
    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial string LastRun { get; set; } = "";
    [ObservableProperty] public partial string NextRun { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Schedule { get; set; } = "";
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial double? Percent { get; set; }
    [ObservableProperty] public partial bool GuardTripped { get; set; }

    public void Update(JsonNode j)
    {
        Name = j["name"]!.ToString();
        Type = j["type"]!.ToString();
        Schedule = j["schedule_text"]?.ToString() ?? "";
        IsRunning = j["running"] is JsonObject;
        Percent = J.D(j["running"]?["percent"]);
        var enabled = j["enabled"]?.GetValue<bool>() == true;
        State = !enabled ? Ui.T("state_disabled") : IsRunning ? Ui.F("running_phase", j["running"]?["phase"], Percent is { } p ? p.ToString("0", Ui.Culture) + " %" : "")
            : j["stale"]?.GetValue<bool>() == true ? Ui.T("state_stale") : Ui.T("state_idle");
        Result = j["last_run"]?["result"]?.ToString() ?? "–";
        LastRun = OverviewViewModel.Time(j["last_run"]?["start"]);
        NextRun = OverviewViewModel.Time(j["next_run"]);
        Summary = j["last_run"]?["summary"]?.ToString() ?? "";
        GuardTripped = j["delete_guard"]?["tripped"]?.GetValue<bool>() == true;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (await main.TryCallAsync(IpcOps.JobRun, new JsonObject { ["job_id"] = Id }).ConfigureAwait(true) is not null)
        {
            IsRunning = true;
            State = Ui.T("queued");
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (main.Dialogs.Confirm(Ui.F("confirm_cancel", Name))) await main.TryCallAsync(IpcOps.JobCancel, new JsonObject { ["job_id"] = Id }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ApproveDeleteAsync()
    {
        if (main.Dialogs.Confirm(Ui.F("confirm_mass_delete", Name)))
            await main.TryCallAsync(IpcOps.JobApproveDelete, new JsonObject { ["job_id"] = Id }).ConfigureAwait(true);
    }
}
