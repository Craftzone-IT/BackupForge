using System.Globalization;
using System.Text.Json.Nodes;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Ipc;

namespace BackupForge.Service;

/// <summary>
/// restore.run (SPEC 13.): runs as a "restore" kind through the queue (the job itself cannot run meanwhile), by default
/// into a new folder &lt;to&gt;\Restore_&lt;yyyy-MM-dd HHmmss&gt;.
/// </summary>
public static class RestoreIpc
{
    public static void Register(ServiceIpcHandler h)
    {
        var rt = h.Runtime;
        h.Register(IpcOps.RestoreRun, async (req, _, ct) =>
        {
            var jobId = req.Required("job_id");
            var job = rt.ConfigStore.Current.FindJob(jobId) ?? throw new IpcException(IpcErrorCodes.NotFound, $"unknown job '{jobId}'");
            var toOriginal = req.Bool("to_original");
            if (toOriginal && !req.Bool("confirm")) throw new IpcException(IpcErrorCodes.Rejected, "restoring to the original location overwrites files — confirmation required");
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["point"] = req.Str("point") ?? "latest",
                ["path"] = req.Str("path") ?? "",
            };
            if (job.Type == JobTypes.Restic) parameters["snapshot"] = parameters["point"] == "current" ? "latest" : parameters["point"];
            if (toOriginal) parameters["to_original"] = "1";
            else
            {
                var to = req.Str("to") ?? throw new IpcException(IpcErrorCodes.BadRequest, "missing argument 'to'");
                if (!Path.IsPathFullyQualified(to)) throw new IpcException(IpcErrorCodes.BadRequest, "'to' must be an absolute path");
                parameters["target"] = Path.Combine(to, "Restore_" + rt.Clock.LocalNow().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));
            }
            var r = rt.Coordinator.Enqueue(new WorkRequest { JobId = jobId, Kind = RunKinds.Restore, Trigger = RunTriggers.Manual, Parameters = parameters });
            if (!r.Accepted) throw new IpcException(IpcErrorCodes.Rejected, r.Reason ?? "rejected");
            if (!req.Bool("wait")) return new JsonObject { ["queued"] = jobId, ["target"] = parameters.GetValueOrDefault("target") };
            var rec = await r.Completion.WaitAsync(ct).ConfigureAwait(false);
            return new JsonObject { ["run_id"] = rec?.Id, ["result"] = rec?.Result, ["summary"] = rec?.Summary, ["target"] = parameters.GetValueOrDefault("target") };
        });
    }
}
