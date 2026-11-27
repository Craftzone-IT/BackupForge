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
        h.Register(IpcOps.RestoreVersions, async (req, _, ct) =>
        {
            var job = Job(rt, req);
            var arr = new JsonArray();
            if (job.Type == JobTypes.Files)
            {
                foreach (var p in Engine.Files.FileRestoreEngine.Points(Core.Abstractions.PhysicalFileSystem.Instance, rt.ConfigStore.Current, job))
                    arr.Add(new JsonObject { ["point"] = p.Id, ["time"] = p.LocalTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ["path"] = p.Path });
            }
            else if (job.Type == JobTypes.Restic && rt.Restic is not null)
            {
                var repo = rt.ConfigStore.Current.FindRepo(job.Restic!.RepoId)!;
                foreach (var s in (await rt.Restic.Client.SnapshotsAsync(repo, rt.Secrets, "bf:" + job.Id, ct).ConfigureAwait(false)).OrderByDescending(s => s.Time))
                    arr.Add(new JsonObject { ["point"] = s.ShortId, ["time"] = s.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ["path"] = string.Join(", ", s.Paths) });
            }
            return arr;
        });
        h.Register(IpcOps.RestoreBrowse, async (req, _, ct) =>
        {
            var job = Job(rt, req);
            var point = req.Str("point") ?? "current";
            var path = req.Str("path") ?? "";
            var arr = new JsonArray();
            if (job.Type == JobTypes.Files)
            {
                var fs = Core.Abstractions.PhysicalFileSystem.Instance;
                string dir;
                try { dir = Engine.Files.FileRestoreEngine.Resolve(fs, rt.ConfigStore.Current, job, point, path); }
                catch (InvalidOperationException ex) { throw new IpcException(IpcErrorCodes.BadRequest, ex.Message); }
                if (!fs.DirectoryExists(dir)) throw new IpcException(IpcErrorCodes.NotFound, "folder not found in the restore point");
                foreach (var e in fs.Enumerate(dir).OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    arr.Add(new JsonObject { ["name"] = e.Name, ["dir"] = e.IsDirectory, ["size"] = e.Length, ["mtime"] = e.LastWriteUtc.ToString("o") });
            }
            else if (job.Type == JobTypes.Restic && rt.Restic is not null)
            {
                var repo = rt.ConfigStore.Current.FindRepo(job.Restic!.RepoId)!;
                path = Engine.Restic.ResticPaths.ToSnapshotPath(path) ?? ""; // C:\data → /C/data (CR-002 A4.)
                var nodes = await rt.Restic.Client.LsAsync(repo, rt.Secrets, point == "current" ? "latest" : point, path.Length == 0 ? null : path, ct).ConfigureAwait(false);
                var prefix = "/" + path.Trim('/');
                foreach (var n in nodes.Where(n => path.Length == 0 ? n.Path.Count(c => c == '/') <= 2 : n.Path.StartsWith(prefix + "/", StringComparison.Ordinal) && n.Path.Count(c => c == '/') == prefix.Count(c => c == '/') + 1))
                    arr.Add(new JsonObject { ["name"] = n.Name, ["dir"] = n.Type == "dir", ["size"] = n.Size, ["mtime"] = n.Mtime?.ToString("o"), ["path"] = n.Path });
            }
            return arr;
        });
        h.Register(IpcOps.RestoreRun, async (req, _, ct) =>
        {
            var job = Job(rt, req);
            var jobId = job.Id;
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

    private static JobConfig Job(ServiceRuntime rt, IpcRequest req)
    {
        var id = req.Required("job_id");
        return rt.ConfigStore.Current.FindJob(id) ?? throw new IpcException(IpcErrorCodes.NotFound, $"unknown job '{id}'");
    }
}
