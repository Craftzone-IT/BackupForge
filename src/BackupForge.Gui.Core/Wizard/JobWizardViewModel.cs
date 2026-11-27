using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Scheduling;
using BackupForge.Gui.Core.ViewModels;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.Wizard;

public enum WizardStep { Type, Source, Target, Mode, Repo, Instance, Databases, SqlOptions, Schedule, Name, Summary }

public sealed partial class StepItem(WizardStep step, string title) : ObservableObject
{
    public WizardStep Step { get; } = step;
    public string Title { get; } = title;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool HasError { get; set; }
}

public sealed partial class SourceRow : ObservableObject
{
    [ObservableProperty] public partial string Path { get; set; } = "";
    [ObservableProperty] public partial string Excludes { get; set; } = "";
    [ObservableProperty] public partial string? Warning { get; set; }
    /// <summary>The UNC path of a mapped drive letter (offered instead of Z:\…, which LocalSystem does not see).</summary>
    [ObservableProperty] public partial string? SuggestedUnc { get; set; }
}

public sealed partial class DbRow(string name, string recovery, string size, bool system, string? warning) : ObservableObject
{
    public string Name { get; } = name;
    public string Recovery { get; } = recovery;
    public string Size { get; } = size;
    public bool System { get; } = system;
    public string? Warning { get; } = warning;
    [ObservableProperty] public partial bool Selected { get; set; }
}

public sealed record Choice(string Id, string Text);

/// <summary>
/// "Új feladat → Tovább → … → Befejezés" (CR-002 B2., SPEC 11.2 2.). Works on a DRAFT (<see cref="Draft"/>, a copy of the
/// job held by <see cref="JobEditViewModel"/>, whose ToJob() is lossless): the configuration changes only on Finish, after
/// the service validated it. Every rule is here (Linux-testable); the WPF window only binds.
/// </summary>
public sealed partial class JobWizardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string? _originalId;
    private readonly string? _originalParent;
    private bool _nameTouched;
    private bool _syncing;

    /// <param name="existing">null = a new job; otherwise the job to edit (or to copy when <paramref name="copy"/>).</param>
    public JobWizardViewModel(MainViewModel main, JobConfig? existing = null, bool copy = false)
    {
        _main = main;
        IsNew = existing is null || copy;
        var job = existing is null ? NewJob(JobTypes.Files) : ConfigJson.Clone(new BackupConfig { InstanceName = "x", Jobs = [existing] }).Jobs[0];
        if (copy)
        {
            job.Name = Ui.F("copy_of", job.Name);
            job.Id = Slug.Unique(job.Id, main.EditConfig.Jobs.Select(j => j.Id));
            job.OnSuccess = [];
        }
        else if (existing is not null)
        {
            _originalId = existing.Id;
            _nameTouched = true;
        }
        _originalParent = existing is null || copy ? null : ParentOf(main.EditConfig, existing.Id);
        Draft = new JobEditViewModel(job, main, markDirty: false);
        ChainParentId = _originalParent;
        LoadSourceRows();
        Draft.PropertyChanged += (_, e) =>
        {
            if (_syncing) return;
            if (e.PropertyName is nameof(JobEditViewModel.Name)) _nameTouched = true;
            Refresh();
        };
        RebuildSteps();
        Current = copy ? WizardStep.Summary : Steps[0].Step; // a copy is complete: review it, change what differs
        Refresh();
    }

    public MainViewModel Main => _main;
    public bool IsNew { get; }
    public JobEditViewModel Draft { get; }
    public ObservableCollection<StepItem> Steps { get; } = [];
    public ObservableCollection<SourceRow> SourceRows { get; } = [];
    public ObservableCollection<DbRow> DbRows { get; } = [];
    public ObservableCollection<Choice> InstanceChoices { get; } = [];
    public ObservableCollection<string> NextRuns { get; } = [];
    public ObservableCollection<HumanError> Problems { get; } = [];
    /// <summary>Targets / repositories created in this wizard; saved together with the job on Finish.</summary>
    public List<TargetConfig> NewTargets { get; } = [];
    public List<ResticRepoConfig> NewRepos { get; } = [];

    [ObservableProperty] public partial WizardStep Current { get; set; }
    [ObservableProperty] public partial bool ShowAdvanced { get; set; }
    [ObservableProperty] public partial string DryRunOutput { get; set; } = "";
    [ObservableProperty] public partial bool Busy { get; set; }
    /// <summary>Set by Finish: the window closes.</summary>
    [ObservableProperty] public partial bool Completed { get; set; }

    // ------------------------------------------------------------------ steps

    private static JobConfig NewJob(string type) => new()
    {
        Id = "", Name = "", Type = type,
        Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["22:30"] },
        Files = type == JobTypes.Files ? new FilesOptions() : null,
        Target = type == JobTypes.Files ? new JobTargetRef() : null,
        Restic = type == JobTypes.Restic ? new ResticJobOptions() : null,
        Sql = type == JobTypes.Sql ? new SqlJobOptions() : null,
    };

    public static IReadOnlyList<WizardStep> StepsFor(string type, bool isNew)
    {
        var list = new List<WizardStep>();
        if (isNew) list.Add(WizardStep.Type);
        list.AddRange(type switch
        {
            JobTypes.Files => [WizardStep.Source, WizardStep.Target, WizardStep.Mode],
            JobTypes.Restic => [WizardStep.Source, WizardStep.Repo],
            _ => [WizardStep.Instance, WizardStep.Databases, WizardStep.SqlOptions],
        });
        list.AddRange([WizardStep.Schedule, WizardStep.Name, WizardStep.Summary]);
        return list;
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        foreach (var s in StepsFor(Draft.Type, IsNew)) Steps.Add(new StepItem(s, Ui.T("step_" + s.ToString().ToLowerInvariant())));
        MarkSteps();
    }

    private void MarkSteps()
    {
        var idx = Steps.ToList().FindIndex(s => s.Step == Current);
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == idx;
            Steps[i].IsDone = i < idx || (!IsNew && StepError(Steps[i].Step) is null);
            Steps[i].HasError = Problems.Any(p => p.Target?.Step == Steps[i].Step && !p.IsWarning);
        }
    }

    partial void OnCurrentChanged(WizardStep value)
    {
        MarkSteps();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(CurrentError));
        OnPropertyChanged(nameof(StepTitle));
        if (value == WizardStep.Summary || value == WizardStep.Target) _ = ValidateDraftAsync();
        if (value == WizardStep.Repo) _ = CheckRepoAsync();
        if (value is WizardStep.Instance or WizardStep.Databases && InstanceChoices.Count == 0) _ = DiscoverAsync();
    }

    public string StepTitle => Ui.T("step_" + Current.ToString().ToLowerInvariant());
    public bool CanGoBack => Steps.Count > 0 && Steps[0].Step != Current;
    public bool IsLastStep => Current == WizardStep.Summary;
    public bool CanGoNext => !IsLastStep && StepError(Current) is null;
    public string? CurrentError => StepError(Current);
    public bool CanFinish => !Busy && Steps.All(s => s.Step == WizardStep.Summary || StepError(s.Step) is null) && !Problems.Any(p => !p.IsWarning);

    /// <summary>Why "Tovább" is disabled on a step (null = ok). Only what can be told locally; the service validates on Finish.</summary>
    public string? StepError(WizardStep step)
    {
        switch (step)
        {
            case WizardStep.Source:
                if (Draft.Type == JobTypes.Restic && UseStagingSource) return string.IsNullOrEmpty(StagingJobId) ? Ui.T("err_pick_sql_job") : null;
                return SourceRows.Any(r => !string.IsNullOrWhiteSpace(r.Path)) ? null : Ui.T("err_no_source");
            case WizardStep.Target:
                return string.IsNullOrWhiteSpace(Draft.TargetId) ? Ui.T("err_no_target") : null;
            case WizardStep.Mode:
                if (Draft.Versioning && (!int.TryParse(Draft.KeepDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kd) || kd < 1)) return Ui.T("err_keep_days");
                if (!int.TryParse(Draft.GuardPct, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gp) || gp is < 1 or > 100) return Ui.T("err_guard_pct");
                return null;
            case WizardStep.Repo:
                return string.IsNullOrWhiteSpace(Draft.RepoId) ? Ui.T("err_no_repo") : null;
            case WizardStep.Instance:
                if (string.IsNullOrWhiteSpace(Draft.SqlInstance)) return Ui.T("err_no_instance");
                return Draft.SqlAuthSql && string.IsNullOrWhiteSpace(Draft.SqlUsername) ? Ui.T("err_no_sql_user") : null;
            case WizardStep.Databases:
                return Draft.SqlMode == SqlDbModes.Selected && string.IsNullOrWhiteSpace(Draft.SqlInclude) ? Ui.T("err_no_databases") : null;
            case WizardStep.SqlOptions:
                return AlsoToRepo && string.IsNullOrEmpty(AlsoRepoId) ? Ui.T("err_no_repo") : null;
            case WizardStep.Schedule:
                return ScheduleError();
            case WizardStep.Name:
                if (string.IsNullOrWhiteSpace(Draft.Name)) return Ui.T("err_no_name");
                if (!Slug.IsValid(Draft.Id)) return Ui.T("err_bad_id");
                return IdTaken ? Ui.T("err_id_taken") : null;
            default:
                return null;
        }
    }

    private bool IdTaken => _main.EditConfig.Jobs.Any(j => j.Id == Draft.Id && j.Id != _originalId) || (AlsoToRepo && _main.EditConfig.Jobs.Any(j => j.Id == AlsoJobId));

    [RelayCommand]
    public void Next()
    {
        if (!CanGoNext) return;
        var i = Steps.ToList().FindIndex(s => s.Step == Current);
        if (Current == WizardStep.Name && !_nameTouched) _nameTouched = true;
        Current = Steps[i + 1].Step;
    }

    [RelayCommand]
    public void Back()
    {
        var i = Steps.ToList().FindIndex(s => s.Step == Current);
        if (i > 0) Current = Steps[i - 1].Step;
    }

    /// <summary>Clicking a step on the left: back to any step, forward only over steps without errors.</summary>
    [RelayCommand]
    public void GoTo(WizardStep step)
    {
        var target = Steps.ToList().FindIndex(s => s.Step == step);
        if (target < 0) return;
        var cur = Steps.ToList().FindIndex(s => s.Step == Current);
        for (var i = cur; i < target; i++) if (StepError(Steps[i].Step) is not null) { Current = Steps[i].Step; return; }
        Current = step;
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    // ------------------------------------------------------------------ type

    [RelayCommand]
    public void SelectType(string type)
    {
        if (!IsNew) return; // the type of an existing job cannot change
        _syncing = true;
        Draft.Type = type;
        if (type == JobTypes.Restic && string.IsNullOrEmpty(Draft.RepoId)) Draft.RepoId = RepoChoices.FirstOrDefault()?.Id ?? "";
        if (type == JobTypes.Files && string.IsNullOrEmpty(Draft.TargetId)) Draft.TargetId = TargetChoices.FirstOrDefault()?.Id ?? "";
        if (type == JobTypes.Sql && string.IsNullOrEmpty(Draft.SqlInstance)) Draft.SqlInstance = @".\SQLEXPRESS";
        _syncing = false;
        RebuildSteps();
        Refresh();
        Next();
    }

    public bool IsFiles => Draft.Type == JobTypes.Files;
    public bool IsRestic => Draft.Type == JobTypes.Restic;
    public bool IsSql => Draft.Type == JobTypes.Sql;

    // ------------------------------------------------------------------ source

    private void LoadSourceRows()
    {
        SourceRows.Clear();
        var job = Draft.ToJob();
        if (job.Type == JobTypes.Restic && job.Sources is [{ } only] && only.Path.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal))
        {
            _useStaging = true;
            _stagingJobId = only.Path[ConfigValidator.StagingPrefix.Length..];
            return;
        }
        foreach (var s in job.Sources) SourceRows.Add(Row(s.Path, string.Join("; ", s.Excludes)));
    }

    private SourceRow Row(string path, string excludes)
    {
        var r = new SourceRow { Path = path, Excludes = excludes };
        r.PropertyChanged += (_, _) => { if (!_syncing) WriteSources(); };
        return r;
    }

    /// <summary>Back into the draft's "path | ex1; ex2" lines — only when the user changed the list (lossless otherwise).</summary>
    private void WriteSources()
    {
        _syncing = true;
        Draft.Sources = UseStagingSource && IsRestic
            ? ConfigValidator.StagingPrefix + StagingJobId
            : string.Join(Environment.NewLine, SourceRows.Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => string.IsNullOrWhiteSpace(r.Excludes) ? r.Path.Trim() : $"{r.Path.Trim()} | {r.Excludes.Trim()}"));
        _syncing = false;
        if (IsFiles && string.IsNullOrWhiteSpace(Draft.Subdir) && SourceRows.FirstOrDefault() is { Path.Length: > 0 } first) Draft.Subdir = Leaf(first.Path);
        Refresh();
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (_main.Dialogs.PickFolder(null) is not { } f) return;
        SourceRows.Add(Row(f, ""));
        WriteSources();
        await CheckSourcesAsync().ConfigureAwait(true);
    }

    public void AddSourcePath(string path)
    {
        SourceRows.Add(Row(path, ""));
        WriteSources();
    }

    [RelayCommand]
    private void RemoveSource(SourceRow? row)
    {
        if (row is null) return;
        SourceRows.Remove(row);
        WriteSources();
    }

    /// <summary>Every source as the SERVICE sees it (LocalSystem): a user's mapped drive letter (Z:) does not exist there.</summary>
    [RelayCommand]
    public async Task CheckSourcesAsync()
    {
        foreach (var r in SourceRows.Where(r => !string.IsNullOrWhiteSpace(r.Path)))
        {
            var res = await _main.TryCallQuietAsync(IpcOps.PathCheck, new JsonObject { ["path"] = r.Path.Trim() }).ConfigureAwait(true);
            if (res is null) { r.Warning = null; continue; }
            if (res["is_network_drive_letter"]?.GetValue<bool>() == true && res["exists"]?.GetValue<bool>() != true)
            {
                var unc = _main.Dialogs.NetworkPathFor(r.Path.Trim());
                r.Warning = unc is null ? Ui.T("warn_network_drive") : Ui.F("warn_network_drive_unc", unc);
                r.SuggestedUnc = unc;
            }
            else if (res["exists"]?.GetValue<bool>() != true) r.Warning = Ui.F("warn_source_missing", res["error"]);
            else if (res["listable"]?.GetValue<bool>() != true) r.Warning = Ui.F("warn_source_unreadable", res["error"]);
            else r.Warning = null;
        }
    }

    [RelayCommand]
    private void UseUnc(SourceRow? row)
    {
        if (row?.SuggestedUnc is { } unc) { row.Path = unc; row.Warning = null; row.SuggestedUnc = null; }
    }

    private bool _useStaging;
    public bool UseStagingSource
    {
        get => _useStaging;
        set
        {
            if (!SetProperty(ref _useStaging, value)) return;
            if (value && string.IsNullOrEmpty(StagingJobId)) _stagingJobId = SqlJobChoices.FirstOrDefault()?.Id;
            WriteSources();
            if (value && Draft.ScheduleKind != ScheduleKinds.Chained && IsNew && StagingJobId is { } sj) { ScheduleKindChoice = ScheduleKinds.Chained; ChainParentId = sj; }
            OnPropertyChanged(nameof(StagingJobId));
        }
    }

    private string? _stagingJobId;
    public string? StagingJobId
    {
        get => _stagingJobId;
        set
        {
            if (!SetProperty(ref _stagingJobId, value)) return;
            WriteSources();
            if (IsNew && value is not null && Draft.ScheduleKind == ScheduleKinds.Chained) ChainParentId = value;
        }
    }

    public IEnumerable<Choice> SqlJobChoices => _main.EditConfig.Jobs.Where(j => j.Type == JobTypes.Sql && j.Id != Draft.Id).Select(j => new Choice(j.Id, j.Name));

    // ------------------------------------------------------------------ target

    public IEnumerable<Choice> TargetChoices => _main.EditConfig.Targets.Concat(NewTargets).Select(t => new Choice(t.Id, TargetText(t)));

    private string TargetText(TargetConfig t)
    {
        var st = _main.Status?["targets"]?.AsArray().FirstOrDefault(x => x?["id"]?.ToString() == t.Id);
        var free = J.D(st?["free_gb"]) is { } g ? Ui.F("target_free", g.ToString("0.#", Ui.Culture)) : "";
        var online = st?["online"]?.GetValue<bool>() switch { true => "", false => " · " + Ui.T("target_offline"), _ => "" };
        return $"{t.Id.ToUpperInvariant()} ({t.Path}){free}{online}";
    }

    private TargetConfig? SelectedTarget => _main.EditConfig.Targets.Concat(NewTargets).FirstOrDefault(t => t.Id == Draft.TargetId);

    public string MirrorPreview => SelectedTarget is { } t ? JobPaths.MirrorPath(t, new JobTargetRef { TargetId = t.Id, Subdir = Draft.Subdir.Trim() }) : "";
    public string VersionsPreview => SelectedTarget is { } t && Draft.Versioning
        ? JobPaths.VersionsPath(t, new JobTargetRef { TargetId = t.Id, Subdir = Draft.Subdir.Trim() }, new VersioningOptions { Enabled = true, Folder = string.IsNullOrWhiteSpace(Draft.VersionsFolder) ? null : Draft.VersionsFolder.Trim() }) ?? ""
        : "";

    /// <summary>"Új cél…": a small dialog (local folder / network share + user + password, connection test in the service).</summary>
    [RelayCommand]
    private void NewTarget()
    {
        var vm = new NewTargetViewModel(_main, Slug.Unique("nas", _main.EditConfig.Targets.Concat(NewTargets).Select(t => t.Id)));
        if (!_main.Dialogs.ShowDialog(vm) || vm.Result is not { } t) return;
        NewTargets.Add(t);
        OnPropertyChanged(nameof(TargetChoices));
        Draft.TargetId = t.Id;
    }

    // ------------------------------------------------------------------ mode

    public bool IsMirror
    {
        get => Draft.Mode == FilesModes.Mirror;
        set { if (value && Draft.Mode != FilesModes.Mirror) Draft.Mode = FilesModes.Mirror; }
    }

    public bool IsUpdate
    {
        get => Draft.Mode == FilesModes.Update;
        set { if (value && Draft.Mode != FilesModes.Update) Draft.Mode = FilesModes.Update; }
    }

    public string GuardSentence => Ui.F("guard_sentence", Draft.GuardPct, Draft.GuardMin);
    public static IReadOnlyList<Choice> VssChoices { get; } =
        [new(VssModes.WhenAvailable, Ui.T("vss_when_available")), new(VssModes.Required, Ui.T("vss_required")), new(VssModes.Never, Ui.T("vss_never"))];

    // ------------------------------------------------------------------ repo

    public IEnumerable<Choice> RepoChoices => _main.EditConfig.ResticRepos.Concat(NewRepos).Select(r => new Choice(r.Id, $"{r.Id} ({r.Location})"));

    public string RetentionSummary
    {
        get
        {
            var r = _main.EditConfig.ResticRepos.Concat(NewRepos).FirstOrDefault(x => x.Id == Draft.RepoId)?.Retention;
            if (r is null) return "";
            var parts = new List<string>();
            if (r.KeepLast is { } l) parts.Add(Ui.F("keep_last_n", l));
            if (r.KeepDaily is { } d) parts.Add(Ui.F("keep_daily_n", d));
            if (r.KeepWeekly is { } w) parts.Add(Ui.F("keep_weekly_n", w));
            if (r.KeepMonthly is { } m) parts.Add(Ui.F("keep_monthly_n", m));
            if (r.KeepYearly is { } y) parts.Add(Ui.F("keep_yearly_n", y));
            return string.Join(", ", parts);
        }
    }

    [ObservableProperty] public partial bool RepoNotInitialized { get; set; }

    /// <summary>A saved repository that restic does not find: offer to create it (G4) instead of a CLI command.</summary>
    [RelayCommand]
    public async Task CheckRepoAsync()
    {
        RepoNotInitialized = false;
        if (string.IsNullOrEmpty(Draft.RepoId) || _main.EditConfig.FindRepo(Draft.RepoId) is null) return;
        try { await _main.Client.CallAsync(IpcOps.RepoSnapshots, new JsonObject { ["repo_id"] = Draft.RepoId }).ConfigureAwait(true); }
        catch (IpcException ex) when (TargetsViewModel.IsNotInitialized(ex.Message)) { RepoNotInitialized = true; }
        catch (Exception ex) when (ex is IpcException or IpcUnavailableException) { }
    }

    /// <summary>"Új tároló…" / "Tároló létrehozása most…": the existing repository wizard (generated password, recovery sheet, init only after confirmation).</summary>
    [RelayCommand]
    private async Task CreateRepoAsync()
    {
        var existing = RepoNotInitialized ? Draft.RepoId : null;
        var id = await _main.Targets.CreateRepoInteractiveAsync(existing).ConfigureAwait(true);
        if (id is null) return;
        OnPropertyChanged(nameof(RepoChoices));
        Draft.RepoId = id;
        RepoNotInitialized = false;
    }

    // ------------------------------------------------------------------ SQL

    [RelayCommand]
    public async Task DiscoverAsync()
    {
        if (await _main.TryCallQuietAsync(IpcOps.SqlDiscover).ConfigureAwait(true) is not JsonArray r) return;
        InstanceChoices.Clear();
        foreach (var inst in r)
        {
            var name = inst!["instance"]!.ToString();
            InstanceChoices.Add(new Choice(name, $"{name}  {inst["edition"]} {inst["version"]}".TrimEnd()));
            if (!string.Equals(name, Draft.SqlInstance, StringComparison.OrdinalIgnoreCase)) continue;
            DbRows.Clear();
            var include = Draft.SqlInclude.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in inst["databases"]?.AsArray() ?? [])
            {
                var rm = d!["recovery_model"]?.ToString() ?? "";
                var sys = d["system"]?.GetValue<bool>() == true;
                var size = J.L(d["size_bytes"]) is { } b ? BackupForge.Core.Resources.SizeText.Format(b, Ui.Culture) : "?";
                var row = new DbRow(d["name"]!.ToString(), rm, size, sys, !sys && rm is "FULL" or "BULK_LOGGED" ? Ui.T("full_recovery_hint") : null) { Selected = include.Contains(d["name"]!.ToString()) };
                row.PropertyChanged += (_, _) => WriteDatabases();
                DbRows.Add(row);
            }
            PermissionProblem = inst["error"]?.ToString();
        }
    }

    [ObservableProperty] public partial string? PermissionProblem { get; set; }

    public bool AllUserDatabases
    {
        get => Draft.SqlMode == SqlDbModes.AllUser;
        set { if (value) Draft.SqlMode = SqlDbModes.AllUser; }
    }

    public bool SelectedDatabases
    {
        get => Draft.SqlMode == SqlDbModes.Selected;
        set { if (value) { Draft.SqlMode = SqlDbModes.Selected; WriteDatabases(); } }
    }

    private void WriteDatabases()
    {
        if (Draft.SqlMode != SqlDbModes.Selected) return;
        Draft.SqlInclude = string.Join(", ", DbRows.Where(d => d.Selected).Select(d => d.Name));
    }

    [RelayCommand]
    private async Task GrantAsync() => await _main.Sql.GrantForAsync(Draft.ToJob()).ConfigureAwait(true);

    public bool CheckdbWeekly
    {
        get => !string.IsNullOrWhiteSpace(Draft.Checkdb);
        set => Draft.Checkdb = value ? (string.IsNullOrWhiteSpace(Draft.Checkdb) ? "weekly Sun 02:00" : Draft.Checkdb) : "";
    }

    [ObservableProperty] public partial bool AlsoToRepo { get; set; }
    [ObservableProperty] public partial string? AlsoRepoId { get; set; }
    public string AlsoJobId => Draft.Id + "-restic";

    partial void OnAlsoToRepoChanged(bool value)
    {
        if (value && AlsoRepoId is null) AlsoRepoId = RepoChoices.FirstOrDefault()?.Id;
        Refresh();
    }

    // ------------------------------------------------------------------ schedule

    public static IReadOnlyList<string> WeekDays { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public string ScheduleKindChoice
    {
        get => Draft.ScheduleKind;
        set
        {
            if (value == Draft.ScheduleKind) return;
            Draft.ScheduleKind = value;
            if (value == ScheduleKinds.Interval && string.IsNullOrWhiteSpace(Draft.EveryMin)) { Draft.EveryMin = "240"; Draft.Start = string.IsNullOrWhiteSpace(Draft.Start) ? "06:00" : Draft.Start; }
            if (value == ScheduleKinds.Daily && string.IsNullOrWhiteSpace(Draft.Times)) Draft.Times = "22:30";
            if (value == ScheduleKinds.Weekly && string.IsNullOrWhiteSpace(Draft.Day)) { Draft.Day = "Sun"; Draft.Time = string.IsNullOrWhiteSpace(Draft.Time) ? "03:00" : Draft.Time; }
            if (value == ScheduleKinds.Monthly && string.IsNullOrWhiteSpace(Draft.DayOfMonth)) { Draft.DayOfMonth = "1"; Draft.Time = string.IsNullOrWhiteSpace(Draft.Time) ? "03:00" : Draft.Time; }
            if (value == ScheduleKinds.Chained && ChainParentId is null) ChainParentId = ChainChoices.FirstOrDefault()?.Id;
            OnPropertyChanged();
        }
    }

    /// <summary>Interval in hours for the "N óránként" choice (every_min / 60).</summary>
    public string EveryHours
    {
        get => int.TryParse(Draft.EveryMin, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m % 60 == 0 ? (m / 60).ToString(CultureInfo.InvariantCulture) : Draft.EveryMin;
        set => Draft.EveryMin = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? (h * 60).ToString(CultureInfo.InvariantCulture) : value;
    }

    /// <summary>An empty day list means every day.</summary>
    public bool DayChecked(string day) => string.IsNullOrWhiteSpace(Draft.Days) ||
        Draft.Days.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(day, StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    public void ToggleDay(string day)
    {
        var days = WeekDays.Where(d => d == day ? !DayChecked(d) : DayChecked(d)).ToList();
        if (days.Count == 0) return; // at least one day stays
        Draft.Days = days.Count == 7 ? "" : string.Join(", ", days); // all seven = every day (no list)
        OnPropertyChanged(nameof(DayFlags));
    }

    public IReadOnlyList<bool> DayFlags => [.. WeekDays.Select(DayChecked)];

    private string? _chainParentId;
    /// <summary>"egy másik feladat után": the parent whose on_success will list this job (set on Finish).</summary>
    public string? ChainParentId
    {
        get => _chainParentId;
        set { if (SetProperty(ref _chainParentId, value)) Refresh(); }
    }

    /// <summary>Jobs this one may run after: not itself and none that already (transitively) runs after it — no cycle.</summary>
    public IEnumerable<Choice> ChainChoices
    {
        get
        {
            var cfg = _main.EditConfig;
            var after = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>([_originalId ?? Draft.Id]);
            while (queue.Count > 0)
                foreach (var next in cfg.FindJob(queue.Dequeue())?.OnSuccess ?? [])
                    if (after.Add(next)) queue.Enqueue(next);
            return cfg.Jobs.Where(j => j.Id != (_originalId ?? Draft.Id) && j.Id != Draft.Id && !after.Contains(j.Id)).Select(j => new Choice(j.Id, j.Name));
        }
    }

    private static string? ParentOf(BackupConfig cfg, string id) => cfg.Jobs.FirstOrDefault(j => j.OnSuccess.Contains(id))?.Id;

    private string? ScheduleError()
    {
        var job = Draft.ToJob();
        var s = job.Schedule;
        switch (s.Kind)
        {
            case ScheduleKinds.Interval:
                if (s.EveryMin is not > 0) return Ui.T("err_interval");
                if (s.Start is not null && !TimeSpec.TryParseHm(s.Start, out _)) return Ui.T("err_time");
                if (s.Window is { } w && (!TimeSpec.TryParseHm(w.From, out _) || !TimeSpec.TryParseHm(w.To, out _))) return Ui.T("err_time");
                return null;
            case ScheduleKinds.Daily:
                if (s.Times.Count == 0 || s.Times.Any(t => !TimeSpec.TryParseHm(t, out _))) return Ui.T("err_time");
                return s.Days.All(d => TimeSpec.TryParseDay(d, out _)) ? null : Ui.T("err_day");
            case ScheduleKinds.Weekly:
                return !TimeSpec.TryParseDay(s.Day, out _) ? Ui.T("err_day") : !TimeSpec.TryParseHm(s.Time, out _) ? Ui.T("err_time") : null;
            case ScheduleKinds.Monthly:
                return !TimeSpec.IsValidDayOfMonth(s.DayOfMonth) ? Ui.T("err_day_of_month") : !TimeSpec.TryParseHm(s.Time, out _) ? Ui.T("err_time") : null;
            case ScheduleKinds.Chained:
                return string.IsNullOrEmpty(ChainParentId) ? Ui.T("err_no_chain_parent") : null;
            default:
                return null;
        }
    }

    public string SchedulePreview
    {
        get
        {
            if (Draft.ScheduleKind == ScheduleKinds.Chained)
                return ChainParentId is { } p ? Ui.F("chain_preview", _main.EditConfig.FindJob(p)?.Name ?? p) : "";
            try { return ScheduleError() is null ? ScheduleText.Describe(Draft.ToJob().Schedule, Ui.Culture) : "?"; }
            catch (FormatException) { return "?"; }
        }
    }

    /// <summary>The next 5 runs (local time) — <see cref="ScheduleCalculator"/>, the same the service uses.</summary>
    public void UpdateNextRuns(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        NextRuns.Clear();
        if (ScheduleError() is not null) return;
        var s = Draft.ToJob().Schedule;
        if (!ScheduleCalculator.IsTimeBased(s)) return;
        var t = nowUtc;
        for (var i = 0; i < 5 && ScheduleCalculator.Next(s, t, zone) is { } next; i++)
        {
            NextRuns.Add(TimeZoneInfo.ConvertTime(next, zone).ToString("yyyy-MM-dd ddd HH:mm", Ui.Culture));
            t = next;
        }
    }

    public string ExpectedIntervalText
    {
        get
        {
            var cfg = ConfigWithDraft();
            var job = cfg.FindJob(Draft.Id);
            var h = job is null ? null : ExpectedInterval.For(cfg, job);
            return h is { } v ? Ui.F("expected_interval", v.ToString("0.#", Ui.Culture)) : "";
        }
    }

    // ------------------------------------------------------------------ name

    public string DefaultName()
    {
        var job = Draft.ToJob();
        var src = job.Sources.FirstOrDefault()?.Path;
        return job.Type switch
        {
            JobTypes.Files => $"{Leaf(src)} → {job.Target?.TargetId.ToUpperInvariant()}".Trim(' ', '→'),
            JobTypes.Restic => src is not null && src.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal)
                ? $"{_main.EditConfig.FindJob(src[ConfigValidator.StagingPrefix.Length..])?.Name ?? src} → {job.Restic?.RepoId.ToUpperInvariant()}"
                : $"{Leaf(src)} → {job.Restic?.RepoId.ToUpperInvariant()}".Trim(' ', '→'),
            _ => $"SQL {job.Sql?.Instance}".Trim(),
        };
    }

    private static string Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().TrimEnd('\\', '/');
        var i = p.LastIndexOfAny(['\\', '/']);
        var leaf = i < 0 ? p : p[(i + 1)..];
        return leaf.EndsWith(':') ? leaf.TrimEnd(':') : leaf;
    }

    public bool IdChangeWarning => !IsNew && _originalId is not null && Draft.Id != _originalId;

    private void Refresh()
    {
        if (!_syncing && IsNew && !_nameTouched && Draft.Type != "" && DefaultName() is { Length: > 0 } dn && dn != Draft.Name)
        {
            // the default name follows the source / target / instance until the user types one
            _syncing = true;
            Draft.Name = dn;
            _syncing = false;
        }
        if (!_syncing && IsNew && (string.IsNullOrEmpty(Draft.Id) || Draft.Id == _autoId))
        {
            var basis = string.IsNullOrWhiteSpace(Draft.Name) ? Draft.Type : Draft.Name;
            _autoId = Slug.Unique(Slug.From(basis), _main.EditConfig.Jobs.Select(j => j.Id));
            _syncing = true;
            Draft.Id = _autoId;
            _syncing = false;
        }
        foreach (var p in (string[])[nameof(CanGoNext), nameof(CurrentError), nameof(CanFinish), nameof(MirrorPreview), nameof(VersionsPreview), nameof(GuardSentence),
                     nameof(IsMirror), nameof(IsUpdate), nameof(RetentionSummary), nameof(SchedulePreview), nameof(ExpectedIntervalText), nameof(ScheduleKindChoice),
                     nameof(EveryHours), nameof(DayFlags), nameof(SummaryText), nameof(IsFiles), nameof(IsRestic), nameof(IsSql), nameof(ChainChoices),
                     nameof(AllUserDatabases), nameof(SelectedDatabases), nameof(CheckdbWeekly), nameof(IdChangeWarning), nameof(TargetChoices), nameof(RepoChoices)])
            OnPropertyChanged(p);
        UpdateNextRuns(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        if (Steps.Count > 0) MarkSteps();
    }

    private string? _autoId;

    // ------------------------------------------------------------------ summary, validation, finish

    public string SummaryText => Summary.Describe(this, ConfigWithDraft(), Draft.ToJob());

    /// <summary>The configuration as it would be saved: the main config (with the other pages' edits) + this draft.</summary>
    public BackupConfig ConfigWithDraft()
    {
        var cfg = _main.BuildConfig();
        foreach (var t in NewTargets) if (cfg.FindTarget(t.Id) is null) cfg.Targets.Add(t);
        foreach (var r in NewRepos) if (cfg.FindRepo(r.Id) is null) cfg.ResticRepos.Add(r);
        var job = Draft.ToJob();
        var idx = _originalId is null ? -1 : cfg.Jobs.FindIndex(j => j.Id == _originalId);
        if (idx >= 0) cfg.Jobs[idx] = job; else cfg.Jobs.Add(job);
        // a renamed id: chains pointing at the old id follow
        if (_originalId is not null && _originalId != job.Id)
            foreach (var j in cfg.Jobs) j.OnSuccess = [.. j.OnSuccess.Select(x => x == _originalId ? job.Id : x)];
        // "egy másik feladat után": the chain is the parent's on_success
        var newParent = job.Schedule.Kind == ScheduleKinds.Chained ? ChainParentId : null;
        if (newParent != _originalParent)
        {
            if (_originalParent is not null && cfg.FindJob(_originalParent) is { } oldP) oldP.OnSuccess.Remove(job.Id);
            if (newParent is not null && cfg.FindJob(newParent) is { } newP && !newP.OnSuccess.Contains(job.Id)) newP.OnSuccess.Add(job.Id);
        }
        if (job.Type == JobTypes.Sql && AlsoToRepo && AlsoRepoId is not null)
        {
            // SQL → restic chain in one go: <id>-restic backs up the staging folder after every successful SQL run
            var chained = new JobConfig
            {
                Id = AlsoJobId, Name = $"{job.Name} → {AlsoRepoId.ToUpperInvariant()}", Type = JobTypes.Restic,
                Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained },
                Sources = [new SourceConfig { Path = ConfigValidator.StagingPrefix + job.Id }],
                Restic = new ResticJobOptions { RepoId = AlsoRepoId },
            };
            if (cfg.FindJob(chained.Id) is null) cfg.Jobs.Add(chained);
            if (!job.OnSuccess.Contains(chained.Id)) cfg.FindJob(job.Id)!.OnSuccess.Add(chained.Id);
        }
        return cfg;
    }

    /// <summary>The service validates the configuration with the draft (overlaps, secrets …); errors go to their step.</summary>
    [RelayCommand]
    public async Task ValidateDraftAsync()
    {
        var cfg = ConfigWithDraft();
        JsonNode? r;
        try { r = await _main.Client.CallAsync(IpcOps.ConfigValidate, new JsonObject { ["config"] = ConfigJson.Serialize(cfg) }).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IpcException or IpcUnavailableException) { return; }
        SetProblems(cfg, r?["errors"]?.AsArray(), r?["warnings"]?.AsArray());
    }

    private void SetProblems(BackupConfig cfg, JsonArray? errors, JsonArray? warnings)
    {
        Problems.Clear();
        var mine = cfg.Jobs.FindIndex(j => j.Id == Draft.Id);
        foreach (var (list, warn) in new[] { (errors, false), (warnings, true) })
            foreach (var e in list ?? [])
            {
                var path = e?["path"]?.ToString() ?? "";
                var h = ErrorMap.Humanize(cfg, path, e?["message"]?.ToString() ?? "", warn);
                // the other jobs' problems belong to them — except an overlap with this draft, which the other job reports too
                if (h.JobIndex is { } ji && ji != mine && !(e?["code"]?.ToString() == "path.overlap_job" && (e["message"]?.ToString() ?? "").Contains(Draft.Id, StringComparison.Ordinal))) continue;
                Problems.Add(h);
            }
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(TargetProblems));
        MarkSteps();
    }

    /// <summary>The overlap errors (CR-001 path.overlap_job) right on the Target step.</summary>
    public IEnumerable<HumanError> TargetProblems => Problems.Where(p => p.Target?.Step == WizardStep.Target);

    /// <summary>A problem clicked in the summary: open its step (or the Advanced page).</summary>
    [RelayCommand]
    private void OpenProblem(HumanError? p)
    {
        if (p?.Target?.Step is { } s && Steps.Any(x => x.Step == s)) { Current = s; ShowAdvanced = false; }
        else ShowAdvanced = true;
    }

    /// <summary>Próbafuttatás of the DRAFT: planned by the service, nothing saved or written (B4).</summary>
    [RelayCommand]
    private async Task DryRunAsync()
    {
        DryRunOutput = Ui.T("dryrun_running");
        var cfg = ConfigWithDraft();
        var job = cfg.FindJob(Draft.Id)!;
        var args = new JsonObject { ["job"] = Node(job), ["targets"] = new JsonArray([.. NewTargets.Select(t => (JsonNode)Node(t))]), ["repos"] = new JsonArray([.. NewRepos.Select(r => (JsonNode)Node(r))]) };
        try
        {
            var r = await _main.Client.CallAsync(IpcOps.JobDryRun, args).ConfigureAwait(true);
            var lines = r?["log"]?.AsArray().Select(l => l?.ToString()).Where(l => l is not null && l.Contains("plan:", StringComparison.Ordinal)).Take(200) ?? [];
            var items = r?["items"]?.AsArray().Select(i => $"{i?["level"]}: {i?["path"]} {i?["message"]}") ?? [];
            DryRunOutput = $"{r?["result"]}: {r?["summary"]}{Environment.NewLine}{string.Join(Environment.NewLine, items.Concat(lines))}";
        }
        catch (IpcException ex) when (ex.Code == IpcErrorCodes.Invalid)
        {
            SetProblems(cfg, ex.Details?.AsArray(), null);
            DryRunOutput = Ui.T("dryrun_invalid");
        }
        catch (Exception ex) when (ex is IpcException or IpcUnavailableException)
        {
            DryRunOutput = ex.Message;
        }
    }

    internal static JsonNode Node(JobConfig j) => JsonNode.Parse(ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Jobs = [j] }))!["jobs"]![0]!.DeepClone();
    internal static JsonNode Node(TargetConfig t) => JsonNode.Parse(ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Targets = [t] }))!["targets"]![0]!.DeepClone();
    internal static JsonNode Node(ResticRepoConfig r) => JsonNode.Parse(ConfigJson.Serialize(new BackupConfig { InstanceName = "x", ResticRepos = [r] }))!["restic_repos"]![0]!.DeepClone();

    [RelayCommand]
    public Task<bool> FinishAsync() => FinishCoreAsync(runNow: false);

    [RelayCommand]
    public Task<bool> FinishAndRunAsync() => FinishCoreAsync(runNow: true);

    private async Task<bool> FinishCoreAsync(bool runNow)
    {
        if (Busy) return false;
        Busy = true;
        try
        {
            var cfg = ConfigWithDraft();
            var (ok, errors, _) = await _main.SaveConfigAsync(cfg).ConfigureAwait(true);
            if (!ok)
            {
                SetProblems(cfg, errors, null);
                Current = WizardStep.Summary;
                return false;
            }
            if (Draft.SqlAuthSql && !string.IsNullOrEmpty(SqlPassword)) await _main.SetSecretAsync($"sql:{Draft.Id}", SqlPassword).ConfigureAwait(true);
            if (runNow) await _main.TryCallAsync(IpcOps.JobRun, new JsonObject { ["job_id"] = Draft.Id }).ConfigureAwait(true);
            Completed = true;
            return true;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>SQL login password: stored as a secret (sql:&lt;id&gt;) on Finish, never kept in the configuration.</summary>
    public string? SqlPassword { get; set; }

    [RelayCommand]
    private void Cancel() => Completed = false;
}

/// <summary>Ids from names (CR-002 B2.): "Céges adatok → NAS1" → "ceges-adatok-nas1"; a taken id gets "-2", "-3" …</summary>
public static class Slug
{
    public static string From(string name)
    {
        var d = name.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? c : '-');
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-', '_');
        if (s.Length > 60) s = s[..60].TrimEnd('-', '_');
        return s.Length == 0 ? "job" : s;
    }

    public static string Unique(string id, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.Ordinal);
        if (!set.Contains(id)) return id;
        for (var n = 2; ; n++)
            if (!set.Contains($"{id}-{n}")) return $"{id}-{n}";
    }

    public static bool IsValid(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,63}$");
}
