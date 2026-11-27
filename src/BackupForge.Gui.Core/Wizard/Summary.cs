using BackupForge.Core.Config;
using BackupForge.Core.Scheduling;

namespace BackupForge.Gui.Core.Wizard;

/// <summary>The job in one or two sentences on the summary step (CR-002 B2.).</summary>
public static class Summary
{
    public static string Describe(JobWizardViewModel w, BackupConfig cfg, JobConfig job)
    {
        string schedule;
        try { schedule = job.Schedule.Kind == ScheduleKinds.Chained ? w.SchedulePreview : ScheduleText.Describe(cfg, job, Ui.Culture); }
        catch (FormatException) { schedule = "?"; }
        var lines = new List<string> { Cap(schedule) + "." };
        var sources = string.Join(", ", job.Sources.Select(s => s.Path));
        switch (job.Type)
        {
            case JobTypes.Files when job.Target is not null && cfg.FindTarget(job.Target.TargetId) is { } t:
                lines.Add(Ui.F(job.Files?.Mode == FilesModes.Update ? "sum_files_update" : "sum_files_mirror", sources, JobPaths.MirrorPath(t, job.Target), t.Id.ToUpperInvariant()));
                if (job.Files is { Versioning.Enabled: true } f && JobPaths.VersionsPath(t, job.Target, f.Versioning) is { } v)
                    lines.Add(Ui.F("sum_versions", f.Versioning.KeepDays, v));
                break;
            case JobTypes.Restic when job.Restic is not null:
                var src = job.Sources.FirstOrDefault()?.Path ?? "";
                lines.Add(src.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal)
                    ? Ui.F("sum_restic_staging", cfg.FindJob(src[ConfigValidator.StagingPrefix.Length..])?.Name ?? src, job.Restic.RepoId)
                    : Ui.F("sum_restic", sources, job.Restic.RepoId));
                if (w.RetentionSummary.Length > 0) lines.Add(Ui.F("sum_retention", w.RetentionSummary));
                break;
            case JobTypes.Sql when job.Sql is not null:
                var dbs = job.Sql.Databases.Mode == SqlDbModes.AllUser ? Ui.T("sum_all_user_dbs") : string.Join(", ", job.Sql.Databases.Include);
                lines.Add(Ui.F("sum_sql", job.Sql.Instance, dbs) + (job.Sql.Databases.IncludeSystem ? " " + Ui.T("sum_sql_system") : "") +
                          (job.Sql.Verify ? " " + Ui.T("sum_sql_verify") : ""));
                lines.Add(Ui.F("sum_sql_keep", job.Sql.KeepLocal));
                if (w.AlsoToRepo && w.AlsoRepoId is not null) lines.Add(Ui.F("sum_sql_chain", w.AlsoRepoId));
                break;
        }
        if (!job.Enabled) lines.Add(Ui.T("sum_disabled"));
        return string.Join(" ", lines);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Ui.Culture) + s[1..];
}
