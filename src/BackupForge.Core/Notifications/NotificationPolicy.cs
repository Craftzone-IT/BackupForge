using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;

namespace BackupForge.Core.Notifications;

/// <summary>Which immediate mail a finished run deserves (SPEC 10.1), and duplicate suppression.</summary>
public static class NotificationPolicy
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan StaleRepeat = TimeSpan.FromHours(24);

    /// <summary>failure / warning / recovery, or null. <paramref name="previous"/> = the job's finished, non-skipped run before this one.</summary>
    public static string? KindFor(RunRecord run, RunRecord? previous, EmailSettings settings)
    {
        if (run.Kind is not (RunKinds.Backup or RunKinds.Check or RunKinds.ForgetPrune or RunKinds.Checkdb)) return null;
        switch (run.Result)
        {
            case RunResults.Failed:
                return settings.OnFailure ? NotificationKinds.Failure : null;
            case RunResults.Warning:
                return settings.OnWarning ? NotificationKinds.Warning : null;
            case RunResults.Ok when previous?.Result is RunResults.Failed or RunResults.Warning:
                return settings.OnRecovery ? NotificationKinds.Recovery : null;
            default:
                return null;
        }
    }

    /// <summary>Same job, same kind, same summary within 24 h → no second mail (the weekly report shows them).</summary>
    public static string Fingerprint(string jobId, string kind, string summary) => $"{jobId}|{kind}|{summary}";

    public static bool IsDuplicate(HistoryStore history, string jobId, string kind, string fingerprint, DateTimeOffset now)
    {
        if (kind is not (NotificationKinds.Failure or NotificationKinds.Warning)) return false;
        var last = history.LastNotification(jobId, kind, fingerprint);
        return last is not null && now - last.At < DuplicateWindow && last.Status is "sent" or "pending" or "retrying";
    }
}
