using System.Text.Json.Nodes;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Ipc;

namespace BackupForge.Service;

/// <summary>
/// CR-002 B4.: what the job wizard needs before anything is saved — <c>path.check</c>, <c>target.test</c> of an unsaved
/// target, and <c>job.dryrun</c> of a draft job (validated with the running configuration, planned only, never saved).
/// </summary>
public static class DraftIpc
{
    public static void Register(ServiceIpcHandler h)
    {
        h.Register(IpcOps.PathCheck, (req, _, _) =>
        {
            var path = req.Required("path");
            // a UNC path on a credentialed SMB target's server: only inside its lease (CR-003 2.)
            IDisposable lease;
            try { lease = h.Runtime.LeaseFor(path); }
            catch (TargetConnectException ex) { return Task.FromResult<JsonNode?>(new JsonObject { ["path"] = path, ["exists"] = false, ["listable"] = false, ["is_network_drive_letter"] = false, ["error"] = h.Runtime.Masker.Apply(ex.Message) }); }
            using (lease) return Task.FromResult<JsonNode?>(PathCheck(path));
        });
    }

    /// <summary>Seen with the service's eyes (LocalSystem): a user's mapped drive letter does not exist there.</summary>
    public static JsonObject PathCheck(string path)
    {
        var r = new JsonObject { ["path"] = path, ["exists"] = false, ["listable"] = false, ["is_network_drive_letter"] = false, ["error"] = null };
        var driveLetter = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
        if (driveLetter && OperatingSystem.IsWindows())
        {
            var root = path[..3];
            try { r["is_network_drive_letter"] = !Directory.Exists(root) || new DriveInfo(root).DriveType == DriveType.Network; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { r["is_network_drive_letter"] = true; }
        }
        try
        {
            r["exists"] = Directory.Exists(path) || File.Exists(path);
            if (Directory.Exists(path))
            {
                using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                e.MoveNext();
                r["listable"] = true;
            }
            else if (!File.Exists(path)) r["error"] = "not found";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            r["error"] = ex.Message;
        }
        return r;
    }

    /// <summary>The job / targets / repos of a draft request on top of the running configuration (same id → replaced).</summary>
    public static (BackupConfig Config, JobConfig Job) Merge(BackupConfig current, JsonObject args)
    {
        var wrapper = new JsonObject
        {
            ["schema"] = BackupConfig.CurrentSchema,
            ["instance_name"] = "draft",
            ["jobs"] = new JsonArray(args["job"]?.DeepClone() ?? throw new IpcException(IpcErrorCodes.BadRequest, "missing argument 'job'")),
            ["targets"] = args["targets"]?.DeepClone() ?? new JsonArray(),
            ["restic_repos"] = args["repos"]?.DeepClone() ?? new JsonArray(),
        };
        BackupConfig draft;
        try { draft = ConfigJson.Parse(wrapper.ToJsonString()); }
        catch (ConfigFormatException ex) { throw new IpcException(IpcErrorCodes.BadRequest, ex.Message); }
        var cfg = ConfigJson.Clone(current);
        var job = draft.Jobs[0];
        Upsert(cfg.Jobs, job, j => j.Id);
        foreach (var t in draft.Targets) Upsert(cfg.Targets, t, x => x.Id);
        foreach (var r in draft.ResticRepos) Upsert(cfg.ResticRepos, r, x => x.Id);
        return (cfg, cfg.FindJob(job.Id)!);
    }

    private static void Upsert<T>(List<T> list, T item, Func<T, string> id)
    {
        var i = list.FindIndex(x => id(x) == id(item));
        if (i >= 0) list[i] = item; else list.Add(item);
    }

    /// <summary>Plans the draft with the engine in dry-run mode: nothing is written, no history, no state.</summary>
    public static async Task<JsonNode?> DryRunAsync(ServiceIpcHandler h, IpcRequest req, CancellationToken ct)
    {
        var rt = h.Runtime;
        var (cfg, job) = Merge(rt.ConfigStore.Current, req.Args!);
        var v = ConfigValidator.Validate(cfg, rt.Secrets, rt.Paths.Root);
        if (!v.IsValid) throw new IpcException(IpcErrorCodes.Invalid, "invalid configuration", h.ValidationNode(v));
        var engine = rt.Engines.FirstOrDefault(e => e.CanRun(job.Type, RunKinds.Backup))
                     ?? throw new IpcException(IpcErrorCodes.Rejected, $"no engine for '{job.Type}'");
        var log = new MemoryRunLog();
        var ctx = new JobRunContext
        {
            Config = cfg, Job = job, RunId = 0, Log = log, Clock = rt.Clock, Secrets = rt.Secrets, Paths = rt.Paths,
            Request = new WorkRequest { JobId = job.Id, Kind = RunKinds.Backup, Trigger = RunTriggers.Manual, Options = new RunOptions { DryRun = true } },
            Progress = new Progress<RunProgress>(),
            State = new MemoryKeyValueStore(), // a draft must not trip / approve the delete guard or touch any stored state
            ProtectedPaths = ProtectedPaths.Collect(cfg, rt.Paths.Root),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        RunOutcome o;
        try { o = await engine.RunAsync(ctx, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { o = RunOutcome.Failed("dry-run cancelled or took longer than 10 minutes"); }
        var items = new JsonArray();
        foreach (var i in o.Items.Take(200)) items.Add(new JsonObject { ["level"] = i.Level, ["path"] = i.Path, ["message"] = rt.Masker.Apply(i.Message) });
        var lines = new JsonArray();
        foreach (var l in log.Lines.Take(2000)) lines.Add(rt.Masker.Apply(l));
        return new JsonObject { ["result"] = o.Result, ["summary"] = rt.Masker.Apply(o.Summary ?? ""), ["items"] = items, ["log"] = lines, ["draft"] = true };
    }
}
