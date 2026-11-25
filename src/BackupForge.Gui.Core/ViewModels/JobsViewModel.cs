using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Scheduling;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.ViewModels;

public sealed partial class JobsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public JobsViewModel(MainViewModel main)
    {
        _main = main;
        main.ConfigReloaded += Load;
    }

    public ObservableCollection<JobEditViewModel> Jobs { get; } = [];
    [ObservableProperty] public partial JobEditViewModel? Selected { get; set; }
    [ObservableProperty] public partial string DryRunOutput { get; set; } = "";
    public static string[] Types => JobTypes.All;
    public static string[] ScheduleKindsList => ScheduleKinds.All;
    public static string[] Modes => FilesModes.All;
    public static string[] VssList => VssModes.All;
    public static string[] SqlModes => SqlDbModes.All;
    public static string[] CompressionList => SqlCompressionModes.All;

    public void Load()
    {
        var sel = Selected?.Id;
        Jobs.Clear();
        foreach (var j in _main.EditConfig.Jobs) Jobs.Add(new JobEditViewModel(ConfigJson.Clone(new BackupConfig { InstanceName = "x", Jobs = [j] }).Jobs[0], _main));
        Selected = Jobs.FirstOrDefault(j => j.Id == sel) ?? Jobs.FirstOrDefault();
    }

    public void ApplyTo(BackupConfig cfg) => cfg.Jobs = [.. Jobs.Select(j => j.ToJob())];

    [RelayCommand]
    private void Add(string? type)
    {
        type ??= JobTypes.Files;
        var n = 1;
        while (Jobs.Any(j => j.Id == $"{type}-{n}")) n++;
        var job = new JobConfig
        {
            Id = $"{type}-{n}", Name = $"{type} {n}", Type = type,
            Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["22:00"] },
            Files = type == JobTypes.Files ? new FilesOptions() : null,
            Target = type == JobTypes.Files ? new JobTargetRef { TargetId = _main.EditConfig.Targets.FirstOrDefault()?.Id ?? "", Subdir = $"{type}-{n}" } : null,
            Restic = type == JobTypes.Restic ? new ResticJobOptions { RepoId = _main.EditConfig.ResticRepos.FirstOrDefault()?.Id ?? "" } : null,
            Sql = type == JobTypes.Sql ? new SqlJobOptions { Instance = @".\SQLEXPRESS" } : null,
        };
        var vm = new JobEditViewModel(job, _main);
        Jobs.Add(vm);
        Selected = vm;
        _main.MarkDirty();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null || !_main.Dialogs.Confirm(Ui.F("confirm_remove_job", Selected.Name))) return;
        Jobs.Remove(Selected);
        Selected = Jobs.FirstOrDefault();
        _main.MarkDirty();
    }

    /// <summary>"Próbafuttatás": the saved configuration's plan, nothing is changed.</summary>
    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (Selected is null) return;
        if (_main.IsDirty && !_main.Dialogs.Confirm(Ui.T("dryrun_uses_saved"))) return;
        DryRunOutput = Ui.T("dryrun_running");
        var r = await _main.TryCallAsync(IpcOps.JobDryRun, new JsonObject { ["job_id"] = Selected.Id }).ConfigureAwait(true);
        if (r is null) { DryRunOutput = ""; return; }
        var lines = r["log"]?.AsArray().Select(l => l?.ToString()).Where(l => l is not null && l.Contains("plan:", StringComparison.Ordinal)).Take(2000) ?? [];
        DryRunOutput = $"{r["result"]}: {r["summary"]}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }
}

/// <summary>Editable form of one job (strings for the text boxes; ToJob() builds the config object).</summary>
public sealed partial class JobEditViewModel : ObservableObject
{
    private readonly JobConfig _original;
    private readonly MainViewModel _main;

    public JobEditViewModel(JobConfig job, MainViewModel main)
    {
        _original = job;
        _main = main;
        Id = job.Id;
        Name = job.Name;
        Type = job.Type;
        Enabled = job.Enabled;
        var s = job.Schedule;
        ScheduleKind = s.Kind;
        EveryMin = s.EveryMin?.ToString(CultureInfo.InvariantCulture) ?? "";
        Start = s.Start ?? "";
        WindowFrom = s.Window?.From ?? "";
        WindowTo = s.Window?.To ?? "";
        Times = string.Join(", ", s.Times);
        Days = string.Join(", ", s.Days);
        Day = s.Day ?? "";
        Time = s.Time ?? "";
        DayOfMonth = s.DayOfMonth ?? "";
        Sources = string.Join(Environment.NewLine, job.Sources.Select(x => x.Excludes.Count == 0 ? x.Path : $"{x.Path} | {string.Join("; ", x.Excludes)}"));
        OnSuccess = string.Join(", ", job.OnSuccess);
        ExpectedIntervalH = job.ExpectedIntervalH?.ToString(CultureInfo.InvariantCulture) ?? "";
        CatchUp = job.CatchUp;
        TargetId = job.Target?.TargetId ?? "";
        Subdir = job.Target?.Subdir ?? "";
        var f = job.Files ?? new FilesOptions();
        Mode = f.Mode;
        Versioning = f.Versioning.Enabled;
        VersionsFolder = f.Versioning.Folder ?? "";
        KeepDays = f.Versioning.KeepDays.ToString(CultureInfo.InvariantCulture);
        FilesVss = f.Vss;
        GuardPct = f.DeleteGuardPct.ToString(CultureInfo.InvariantCulture);
        GuardMin = f.DeleteGuardMinFiles.ToString(CultureInfo.InvariantCulture);
        ContentCompare = f.Compare == CompareModes.Content;
        RepoId = job.Restic?.RepoId ?? "";
        ResticVss = job.Restic?.Vss ?? VssModes.WhenAvailable;
        Tags = string.Join(", ", job.Restic?.Tags ?? []);
        var q = job.Sql ?? new SqlJobOptions();
        SqlInstance = q.Instance;
        SqlAuthSql = q.Auth == SqlAuthModes.Sql;
        SqlUsername = q.Username ?? "";
        SqlMode = q.Databases.Mode;
        SqlInclude = string.Join(", ", q.Databases.Include);
        SqlExclude = string.Join(", ", q.Databases.Exclude);
        IncludeSystem = q.Databases.IncludeSystem;
        Verify = q.Verify;
        CopyOnly = q.CopyOnly;
        Compression = q.Compression;
        KeepLocal = q.KeepLocal.ToString(CultureInfo.InvariantCulture);
        Checkdb = q.Checkdb ?? "";
        PropertyChanged += (_, e) => { if (e.PropertyName is not nameof(SchedulePreview) and not nameof(Errors)) { main.MarkDirty(); OnPropertyChanged(nameof(SchedulePreview)); } };
    }

    [ObservableProperty] public partial string Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string Type { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string ScheduleKind { get; set; }
    [ObservableProperty] public partial string EveryMin { get; set; }
    [ObservableProperty] public partial string Start { get; set; }
    [ObservableProperty] public partial string WindowFrom { get; set; }
    [ObservableProperty] public partial string WindowTo { get; set; }
    [ObservableProperty] public partial string Times { get; set; }
    [ObservableProperty] public partial string Days { get; set; }
    [ObservableProperty] public partial string Day { get; set; }
    [ObservableProperty] public partial string Time { get; set; }
    [ObservableProperty] public partial string DayOfMonth { get; set; }
    [ObservableProperty] public partial string Sources { get; set; }
    [ObservableProperty] public partial string OnSuccess { get; set; }
    [ObservableProperty] public partial string ExpectedIntervalH { get; set; }
    [ObservableProperty] public partial bool CatchUp { get; set; }
    [ObservableProperty] public partial string TargetId { get; set; }
    [ObservableProperty] public partial string Subdir { get; set; }
    [ObservableProperty] public partial string Mode { get; set; }
    [ObservableProperty] public partial bool Versioning { get; set; }
    [ObservableProperty] public partial string VersionsFolder { get; set; }
    [ObservableProperty] public partial string KeepDays { get; set; }
    [ObservableProperty] public partial string FilesVss { get; set; }
    [ObservableProperty] public partial string GuardPct { get; set; }
    [ObservableProperty] public partial string GuardMin { get; set; }
    [ObservableProperty] public partial bool ContentCompare { get; set; }
    [ObservableProperty] public partial string RepoId { get; set; }
    [ObservableProperty] public partial string ResticVss { get; set; }
    [ObservableProperty] public partial string Tags { get; set; }
    [ObservableProperty] public partial string SqlInstance { get; set; }
    [ObservableProperty] public partial bool SqlAuthSql { get; set; }
    [ObservableProperty] public partial string SqlUsername { get; set; }
    [ObservableProperty] public partial string SqlMode { get; set; }
    [ObservableProperty] public partial string SqlInclude { get; set; }
    [ObservableProperty] public partial string SqlExclude { get; set; }
    [ObservableProperty] public partial bool IncludeSystem { get; set; }
    [ObservableProperty] public partial bool Verify { get; set; }
    [ObservableProperty] public partial bool CopyOnly { get; set; }
    [ObservableProperty] public partial string Compression { get; set; }
    [ObservableProperty] public partial string KeepLocal { get; set; }
    [ObservableProperty] public partial string Checkdb { get; set; }

    public bool IsFiles => Type == JobTypes.Files;
    public bool IsRestic => Type == JobTypes.Restic;
    public bool IsSql => Type == JobTypes.Sql;

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsFiles));
        OnPropertyChanged(nameof(IsRestic));
        OnPropertyChanged(nameof(IsSql));
    }

    /// <summary>The schedule in words ("4 óránként 06:15-től") while typing.</summary>
    public string SchedulePreview
    {
        get
        {
            try { return ScheduleText.Describe(_main.EditConfig, ToJob(), Ui.Culture); }
            catch (FormatException) { return "?"; }
        }
    }

    public string Errors => _main.ErrorsFor($"jobs[{_main.EditConfig.Jobs.FindIndex(j => j.Id == Id)}]");

    private static List<string> SplitList(string s) => [.. s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    private static int? Int(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string? Opt(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public JobConfig ToJob()
    {
        var j = ConfigJson.Clone(new BackupConfig { InstanceName = "x", Jobs = [_original] }).Jobs[0];
        j.Id = Id.Trim();
        j.Name = Name.Trim();
        j.Type = Type;
        j.Enabled = Enabled;
        j.Schedule = new ScheduleConfig
        {
            Kind = ScheduleKind,
            EveryMin = ScheduleKind == ScheduleKinds.Interval ? Int(EveryMin) : null,
            Start = ScheduleKind == ScheduleKinds.Interval ? Opt(Start) : null,
            Window = ScheduleKind == ScheduleKinds.Interval && Opt(WindowFrom) is { } wf && Opt(WindowTo) is { } wt ? new TimeWindow { From = wf, To = wt } : null,
            Times = ScheduleKind == ScheduleKinds.Daily ? SplitList(Times) : [],
            Days = ScheduleKind == ScheduleKinds.Daily ? SplitList(Days) : [],
            Day = ScheduleKind == ScheduleKinds.Weekly ? Opt(Day) : null,
            Time = ScheduleKind is ScheduleKinds.Weekly or ScheduleKinds.Monthly ? Opt(Time) : null,
            DayOfMonth = ScheduleKind == ScheduleKinds.Monthly ? Opt(DayOfMonth) : null,
        };
        j.OnSuccess = SplitList(OnSuccess);
        j.ExpectedIntervalH = double.TryParse(ExpectedIntervalH, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : null;
        j.CatchUp = CatchUp;
        j.Sources = [.. Sources.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            return new SourceConfig { Path = parts[0], Excludes = parts.Length > 1 ? SplitList(parts[1]) : [] };
        })];
        if (Type == JobTypes.Files)
        {
            j.Target = new JobTargetRef { TargetId = TargetId.Trim(), Subdir = Subdir.Trim() };
            j.Files ??= new FilesOptions();
            j.Files.Mode = Mode;
            j.Files.Versioning.Enabled = Versioning;
            j.Files.Versioning.Folder = Opt(VersionsFolder);
            j.Files.Versioning.KeepDays = Int(KeepDays) ?? -1;
            j.Files.Vss = FilesVss;
            j.Files.DeleteGuardPct = Int(GuardPct) ?? -1;
            j.Files.DeleteGuardMinFiles = Int(GuardMin) ?? -1;
            j.Files.Compare = ContentCompare ? CompareModes.Content : CompareModes.SizeMtime;
            j.Restic = null;
            j.Sql = null;
        }
        else if (Type == JobTypes.Restic)
        {
            j.Restic = new ResticJobOptions { RepoId = RepoId.Trim(), Vss = ResticVss, Tags = SplitList(Tags) };
            j.Files = null;
            j.Target = null;
            j.Sql = null;
        }
        else
        {
            j.Sql ??= new SqlJobOptions();
            j.Sql.Instance = SqlInstance.Trim();
            j.Sql.Auth = SqlAuthSql ? SqlAuthModes.Sql : SqlAuthModes.Windows;
            j.Sql.Username = SqlAuthSql ? Opt(SqlUsername) : null;
            j.Sql.LoginRef = SqlAuthSql ? $"sql:{j.Id}" : null;
            j.Sql.Databases = new SqlDatabaseSelection { Mode = SqlMode, Include = SplitList(SqlInclude), Exclude = SplitList(SqlExclude), IncludeSystem = IncludeSystem };
            j.Sql.Verify = Verify;
            j.Sql.CopyOnly = CopyOnly;
            j.Sql.Compression = Compression;
            j.Sql.KeepLocal = Int(KeepLocal) ?? -1;
            j.Sql.Checkdb = Opt(Checkdb);
            j.Sources = [];
            j.Files = null;
            j.Target = null;
            j.Restic = null;
        }
        return j;
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (_main.Dialogs.PickFolder(null) is { } f) Sources = string.IsNullOrWhiteSpace(Sources) ? f : Sources + Environment.NewLine + f;
    }

    [RelayCommand]
    private async Task SetSqlPasswordAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) return;
        if (await _main.SetSecretAsync($"sql:{Id}", password).ConfigureAwait(true)) _main.Dialogs.Info(Ui.T("password_saved"));
    }
}
