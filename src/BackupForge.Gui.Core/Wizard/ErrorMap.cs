using System.Text.RegularExpressions;
using BackupForge.Core.Config;

namespace BackupForge.Gui.Core.Wizard;

/// <summary>Where a validation error belongs in the GUI: a wizard step (for job paths) or a page, and the field's label key.</summary>
public sealed record ErrorTarget(WizardStep? Step, string Page, string LabelKey);

/// <summary>An error as the user sees it (CR-002 B5.): "Company → NAS1: Cél: nincs kiválasztva" — never "jobs[0].target.target_id".</summary>
public sealed record HumanError(string Text, string Path, int? JobIndex, ErrorTarget? Target, bool IsWarning);

/// <summary>
/// Validator path → GUI place. One table (pattern → step + field label), tested against every path template of
/// <c>ConfigValidator</c> so a new validator rule cannot slip through without a human label.
/// </summary>
public static partial class ErrorMap
{
    private static readonly (Regex Pattern, WizardStep? Step, string Page, string Label)[] Table =
    [
        (R(@"^jobs\[\d+\]\.id$"), WizardStep.Name, "page_jobs", "f_id"),
        (R(@"^jobs\[\d+\]\.name$"), WizardStep.Name, "page_jobs", "f_name"),
        (R(@"^jobs\[\d+\]\.type$"), WizardStep.Type, "page_jobs", "f_type"),
        (R(@"^jobs\[\d+\]$"), WizardStep.Summary, "page_jobs", "f_job"),
        (R(@"^jobs\[\d+\]\.(files|restic|sql)$"), WizardStep.Type, "page_jobs", "f_type"),
        (R(@"^jobs\[\d+\]\.schedule(\..+)?$"), WizardStep.Schedule, "page_jobs", "f_schedule"),
        (R(@"^jobs\[\d+\]\.expected_interval_h$"), WizardStep.Schedule, "page_jobs", "f_expected_interval"),
        (R(@"^jobs\[\d+\]\.timeout_min$"), WizardStep.Schedule, "page_jobs", "f_timeout"),
        (R(@"^jobs\[\d+\]\.on_success(\[\d+\])?$"), WizardStep.Schedule, "page_jobs", "f_chain"),
        (R(@"^jobs\[\d+\]\.sources(\[\d+\]\.path)?$"), WizardStep.Source, "page_jobs", "f_sources"),
        (R(@"^jobs\[\d+\]\.target(\.target_id)?$"), WizardStep.Target, "page_jobs", "f_target"),
        (R(@"^jobs\[\d+\]\.files\.versioning\.folder$"), WizardStep.Target, "page_jobs", "f_versions_folder"),
        (R(@"^jobs\[\d+\]\.files\.(mode|vss|delete_guard_pct|delete_guard_min_files|versioning\.keep_days)$"), WizardStep.Mode, "page_jobs", "f_mode"),
        (R(@"^jobs\[\d+\]\.files\.(compare|parallelism|throttle_mbps|cloud_files|versioning\.max_versions_per_file)$"), WizardStep.Mode, "page_jobs", "f_advanced"),
        (R(@"^jobs\[\d+\]\.restic\.repo_id$"), WizardStep.Repo, "page_jobs", "f_repo"),
        (R(@"^jobs\[\d+\]\.restic\.vss$"), WizardStep.Repo, "page_jobs", "f_vss"),
        (R(@"^jobs\[\d+\]\.sql\.(instance|auth|username|login_ref)$"), WizardStep.Instance, "page_jobs", "f_instance"),
        (R(@"^jobs\[\d+\]\.sql\.databases\.(mode|include)$"), WizardStep.Databases, "page_jobs", "f_databases"),
        (R(@"^jobs\[\d+\]\.sql\.(keep_local|checkdb|compression|staging_path)$"), WizardStep.SqlOptions, "page_jobs", "f_sql_options"),
        (R(@"^targets(\[\d+\](\..+)?)?$"), null, "page_targets", "f_targets"),
        (R(@"^restic_repos(\[\d+\](\..+)?)?$"), null, "page_targets", "f_repos"),
        (R(@"^jobs$"), null, "page_jobs", "f_jobs"),
        (R(@"^notifications\..+$"), null, "page_notifications", "f_notifications"),
        (R(@"^general\..+$"), null, "page_settings", "f_general"),
        (R(@"^(instance_name|language|schema(\.unsupported)?)$"), null, "page_settings", "f_general"),
    ];

    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^jobs\[(\d+)\]")]
    private static partial Regex JobIndexRx();

    /// <summary>The GUI place of a validator path, or null when the table has no row for it (a test makes sure there is none).</summary>
    public static ErrorTarget? Find(string path)
    {
        foreach (var (pattern, step, page, label) in Table)
            if (pattern.IsMatch(path)) return new ErrorTarget(step, page, label);
        return null;
    }

    public static int? JobIndex(string path) => JobIndexRx().Match(path) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>"&lt;job name&gt;: &lt;field&gt;: &lt;message&gt;" (or the page instead of the job name).</summary>
    public static HumanError Humanize(BackupConfig config, string path, string message, bool warning = false)
    {
        var target = Find(path);
        var index = JobIndex(path);
        var owner = index is { } i && i < config.Jobs.Count
            ? (string.IsNullOrWhiteSpace(config.Jobs[i].Name) ? config.Jobs[i].Id : config.Jobs[i].Name)
            : Ui.T(target?.Page ?? "page_settings");
        var field = target is null ? "" : Ui.T(target.LabelKey) + ": ";
        return new HumanError($"{owner}: {field}{message}", path, index, target, warning);
    }
}
