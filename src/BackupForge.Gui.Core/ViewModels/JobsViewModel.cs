using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Scheduling;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.ViewModels;

/// <summary>One row of the Feladatok table.</summary>
public sealed record JobRow(string Id, string Name, string TypeText, string Schedule, string Where, string LastResult, bool Enabled)
{
    public string EnabledText => Enabled ? "" : Ui.T("disabled");
}

/// <summary>
/// Feladatok (CR-002 B2.): a table with New / Edit / Copy / Enable-Disable / Delete. Creating and editing go through the
/// <see cref="Wizard.JobWizardViewModel"/>; the table commands save at once (the old field editor is gone).
/// </summary>
public sealed partial class JobsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public JobsViewModel(MainViewModel main)
    {
        _main = main;
        main.ConfigReloaded += Load;
    }

    public ObservableCollection<JobRow> Rows { get; } = [];
    [ObservableProperty] public partial JobRow? Selected { get; set; }

    public void Load()
    {
        var sel = Selected?.Id;
        Rows.Clear();
        var cfg = _main.EditConfig;
        foreach (var j in cfg.Jobs)
        {
            string schedule;
            try { schedule = ScheduleText.Describe(cfg, j, Ui.Culture); }
            catch (FormatException) { schedule = "?"; }
            var where = j.Type switch
            {
                JobTypes.Files => j.Target is { } t ? $"{t.TargetId.ToUpperInvariant()}{(t.Subdir.Length > 0 ? "\\" + t.Subdir : "")}" : "",
                JobTypes.Restic => j.Restic?.RepoId.ToUpperInvariant() ?? "",
                _ => j.Sql?.Instance ?? "",
            };
            var status = _main.Status?["jobs"]?.AsArray().FirstOrDefault(x => x?["id"]?.ToString() == j.Id);
            Rows.Add(new JobRow(j.Id, j.Name, Ui.T("type_" + j.Type), schedule, where, status?["last_result"]?.ToString() ?? "", j.Enabled));
        }
        Selected = Rows.FirstOrDefault(r => r.Id == sel) ?? Rows.FirstOrDefault();
    }

    /// <summary>Jobs are saved by the wizard and the table commands; the page has nothing pending.</summary>
    public void ApplyTo(BackupConfig cfg) { }

    public Wizard.JobWizardViewModel CreateWizard(string? jobId = null, bool copy = false) =>
        new(_main, jobId is null ? null : _main.EditConfig.FindJob(jobId), copy);

    [RelayCommand]
    private void New() => _main.Dialogs.ShowDialog(CreateWizard());

    [RelayCommand]
    private void Edit()
    {
        if (Selected is not null) _main.Dialogs.ShowDialog(CreateWizard(Selected.Id));
    }

    [RelayCommand]
    private void Copy()
    {
        if (Selected is not null) _main.Dialogs.ShowDialog(CreateWizard(Selected.Id, copy: true));
    }

    [RelayCommand]
    public async Task ToggleEnabledAsync()
    {
        if (Selected is null) return;
        var cfg = _main.BuildConfig();
        if (cfg.FindJob(Selected.Id) is not { } job) return;
        job.Enabled = !job.Enabled;
        await SaveOrShowAsync(cfg).ConfigureAwait(true);
    }

    /// <summary>Deletes the job from the configuration — the history stays and nothing on the target is touched (the confirmation says so).</summary>
    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null || !_main.Dialogs.Confirm(Ui.F("confirm_delete_job", Selected.Name))) return;
        var cfg = _main.BuildConfig();
        cfg.Jobs.RemoveAll(j => j.Id == Selected.Id);
        foreach (var j in cfg.Jobs) j.OnSuccess.Remove(Selected.Id); // nothing runs "after" a deleted job
        await SaveOrShowAsync(cfg).ConfigureAwait(true);
    }

    private async Task SaveOrShowAsync(BackupConfig cfg)
    {
        var (ok, errors, _) = await _main.SaveConfigAsync(cfg).ConfigureAwait(true);
        if (!ok)
            _main.Dialogs.Error(Ui.T("save_failed") + Environment.NewLine + string.Join(Environment.NewLine,
                (errors ?? []).Select(e => Wizard.ErrorMap.Humanize(cfg, e?["path"]?.ToString() ?? "", e?["message"]?.ToString() ?? "").Text)));
    }
}

/// <summary>Editable form of one job (strings for the text boxes; ToJob() builds the config object).</summary>
public sealed partial class JobEditViewModel : ObservableObject
{
    private readonly JobConfig _original;
    private readonly MainViewModel _main;

    public JobEditViewModel(JobConfig job, MainViewModel main, bool markDirty = true)
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
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SchedulePreview) or nameof(Errors)) return;
            if (markDirty) main.MarkDirty();
            OnPropertyChanged(nameof(SchedulePreview));
        };
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

    public static string[] ScheduleKindsList => ScheduleKinds.All;
    public static string[] Modes => FilesModes.All;
    public static string[] VssList => VssModes.All;
    public static string[] SqlModes => SqlDbModes.All;
    public static string[] CompressionList => SqlCompressionModes.All;

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
