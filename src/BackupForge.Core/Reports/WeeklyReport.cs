using System.Globalization;
using System.Net;
using System.Text;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Notifications;
using BackupForge.Core.Resources;
using BackupForge.Core.Status;

namespace BackupForge.Core.Reports;

/// <summary>Weekly HTML report (SPEC 10.3) built from the history and the current status document.</summary>
public static class WeeklyReport
{
    public static MailMessage Build(BackupConfig cfg, HistoryStore history, StatusDocument status, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var c = Texts.CultureFor(cfg.Language);
        var from = nowUtc.AddDays(-7);
        string T(DateTimeOffset? t) => t is null ? Texts.Get("report.never", c) : TimeZoneInfo.ConvertTime(t.Value, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        string H(string s) => Html.Encode(s);

        var runs = history.RunsSince(from).Where(r => r.Kind == RunKinds.Backup).ToList();
        var worst = 0; // 0 ok, 1 warning, 2 error
        void Raise(int level) => worst = Math.Max(worst, level);

        var html = new StringBuilder();
        var text = new StringBuilder();
        var rowsHtml = new StringBuilder();
        foreach (var job in cfg.Jobs)
        {
            var jr = runs.Where(r => r.JobId == job.Id).ToList();
            var ok = jr.Count(r => r.Result == RunResults.Ok);
            var warn = jr.Count(r => r.Result == RunResults.Warning);
            var failed = jr.Count(r => RunResults.IsNotOk(r.Result));
            var skipped = jr.Count(r => r.Result == RunResults.Skipped);
            var bytes = jr.Sum(r => job.Type == JobTypes.Files ? r.BytesTransferred : Math.Max(r.BytesTransferred, r.BytesProcessed));
            var js = status.Jobs.FirstOrDefault(j => j.Id == job.Id);
            var lastOk = history.LastRunWithResult(job.Id, RunKinds.Backup, RunResults.Ok, RunResults.Warning)?.StartedAt;
            if (failed > 0 || js?.Stale == true || js?.DeleteGuard?.Tripped == true) Raise(2);
            else if (warn > 0 || skipped > 0) Raise(1);
            var color = failed > 0 || js?.Stale == true ? "#fde2e1" : warn > 0 ? "#fff4d6" : !job.Enabled ? "#eeeeee" : "#e6f4ea";
            rowsHtml.Append(CultureInfo.InvariantCulture,
                $"<tr style=\"background:{color}\"><td>{H(job.Name)}<br><small>{H(job.Id)} · {H(job.Type)}</small></td><td align=\"right\">{jr.Count - skipped}</td><td align=\"right\">{ok}</td><td align=\"right\">{warn}</td><td align=\"right\">{failed}</td><td align=\"right\">{skipped}</td><td>{H(T(lastOk))}</td><td align=\"right\">{H(SizeText.Format(bytes, c))}</td></tr>");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {job.Name}: {jr.Count - skipped} / ok {ok} / warning {warn} / failed {failed} / skipped {skipped} · {T(lastOk)} · {SizeText.Format(bytes, c)}");
        }

        var alerts = new List<string>();
        if (!status.Service.ConfigOk) { Raise(2); alerts.Add(Texts.Format(c, "report.config_error", status.Service.ConfigError)); }
        foreach (var j in status.Jobs.Where(j => j.Stale)) alerts.Add(Texts.Format(c, "report.stale", j.Name));
        foreach (var j in status.Jobs.Where(j => j.DeleteGuard?.Tripped == true)) alerts.Add(Texts.Format(c, "report.guard", j.Name));

        var disabled = status.Jobs.Where(j => !j.Enabled).ToList();
        if (disabled.Count > 0) Raise(1);

        var targetLines = new List<(string Line, bool Bad)>();
        foreach (var t in status.Targets)
        {
            if (!t.Online && t.Checked is null) { targetLines.Add(($"{t.Id}: ?", false)); continue; }
            if (!t.Online) { Raise(2); targetLines.Add((Texts.Format(c, "report.target_offline", t.Id, t.Error), true)); continue; }
            var line = Texts.Format(c, "report.target_row", t.Id, t.FreeGb is null ? "?" : $"{t.FreeGb:0.#} GB", t.SizeGb is null ? "?" : $"{t.SizeGb:0.#} GB");
            var low = t.MinFreeGb is { } min && t.FreeGb is { } free && free < min;
            if (low) { Raise(1); line += " — " + Texts.Format(c, "report.target_low", t.MinFreeGb); }
            targetLines.Add((line, low));
        }

        var overallKey = worst switch { 2 => "report.overall_error", 1 => "report.overall_warning", _ => "report.overall_ok" };
        var overall = Texts.Get(overallKey, c);
        var overallColor = worst switch { 2 => "#b00020", 1 => "#b36b00", _ => "#1b7f3b" };
        var title = Texts.Get("report.title", c);
        var period = Texts.Format(c, "report.period", T(from), T(nowUtc));

        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(H(title)).Append("</title></head>");
        html.Append("<body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222;max-width:900px\">");
        html.Append(CultureInfo.InvariantCulture, $"<h2 style=\"margin:0\">{H(title)} — {H(cfg.InstanceName)}</h2><p style=\"color:#666;margin:4px 0 12px\">{H(period)} · {H(status.Hostname)} · BackupForge {H(status.AppVersion)}</p>");
        html.Append(CultureInfo.InvariantCulture, $"<p style=\"font-size:20px;font-weight:bold;color:{overallColor}\">{H(overall)}</p>");
        if (alerts.Count > 0)
        {
            html.Append("<ul style=\"color:#b00020\">");
            foreach (var a in alerts) html.Append(CultureInfo.InvariantCulture, $"<li>{H(a)}</li>");
            html.Append("</ul>");
        }
        html.Append(CultureInfo.InvariantCulture, $"<h3>{H(Texts.Get("report.jobs", c))}</h3><table cellpadding=\"5\" style=\"border-collapse:collapse;width:100%\"><tr style=\"background:#33475b;color:#fff\">");
        foreach (var k in new[] { "report.col_job", "report.col_runs", "report.col_ok", "report.col_warning", "report.col_failed", "report.col_skipped", "report.col_last_ok", "report.col_data" })
            html.Append(CultureInfo.InvariantCulture, $"<th align=\"left\">{H(Texts.Get(k, c))}</th>");
        html.Append("</tr>").Append(rowsHtml).Append("</table>");

        void Section(string key, IEnumerable<(string Line, bool Bad)> lines)
        {
            var list = lines.ToList();
            html.Append(CultureInfo.InvariantCulture, $"<h3>{H(Texts.Get(key, c))}</h3>");
            text.AppendLine().AppendLine(Texts.Get(key, c) + ":");
            if (list.Count == 0) { html.Append(CultureInfo.InvariantCulture, $"<p style=\"color:#888\">{H(Texts.Get("report.none", c))}</p>"); text.AppendLine("  " + Texts.Get("report.none", c)); return; }
            html.Append("<ul>");
            foreach (var (l, bad) in list)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li{(bad ? " style=\"color:#b00020\"" : "")}>{H(l)}</li>");
                text.AppendLine("  - " + l);
            }
            html.Append("</ul>");
        }

        Section("report.disabled", disabled.Select(j => (Texts.Format(c, "report.disabled_since", j.Name, j.DisabledSince is null ? "?" : j.DisabledSince.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), true)));
        Section("report.targets", targetLines);
        Section("report.versions", status.Jobs.Where(j => j.Versions is not null).Select(j =>
            (Texts.Format(c, "report.versions_row", j.Name, j.Versions!.Folders ?? 0, j.Versions.Oldest is null ? Texts.Get("report.none", c) : j.Versions.Oldest.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), false)));
        Section("report.repos", status.Repos.Select(r =>
        {
            var bad = r.LastCheckResult is RunResults.Failed;
            if (bad) Raise(2);
            var snaps = status.Jobs.Where(j => j.Restic?.RepoId == r.Id).Select(j => j.Restic!.Snapshots).FirstOrDefault(s => s is not null);
            var line = Texts.Format(c, "report.repo_row", r.Id, T(r.LastCheck), r.LastCheckResult ?? "-", r.AppendOnlyServer ? "append-only" : T(r.LastForgetPrune), r.LastForgetPruneResult ?? "-");
            if (snaps is not null) line += $" · {snaps} snapshots";
            return (line, bad);
        }));
        Section("report.sql", status.Jobs.Where(j => j.Sql is not null).SelectMany(j => j.Sql!.Dbs.Select(d =>
        {
            var extra = "";
            var bad = d.LastResult is RunResults.Failed || d.LastCheckdbOk == false;
            if (d.RecoveryModel is "FULL" or "BULK_LOGGED") extra += Texts.Get("report.sql_full", c);
            if (d.LastCheckdb is not null) extra += Texts.Format(c, "report.sql_checkdb", T(d.LastCheckdb), d.LastCheckdbOk == true ? "ok" : "HIBA");
            return ($"{j.Sql.Instance} / " + Texts.Format(c, "report.sql_row", d.Name, T(d.LastOk), extra), bad);
        })));
        Section("report.changes", history.ConfigChangesSince(from).Select(ch => ($"{T(ch.At)} · {ch.By} · {ch.Summary}", ch.Summary.Contains("DISABLED", StringComparison.Ordinal))));
        html.Append("<p style=\"color:#888;font-size:11px\">BackupForge</p></body></html>");

        var subject = $"[BackupForge][{cfg.InstanceName}] {title}: {overall}";
        var fullText = new StringBuilder().AppendLine($"{title} — {cfg.InstanceName}").AppendLine(period).AppendLine(overall);
        foreach (var a in alerts) fullText.AppendLine("! " + a);
        fullText.AppendLine().AppendLine(Texts.Get("report.jobs", c) + ":").Append(text);
        return new MailMessage { Subject = subject, Html = html.ToString(), Text = fullText.ToString() };
    }
}
