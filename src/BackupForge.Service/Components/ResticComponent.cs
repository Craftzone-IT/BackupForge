using System.Text.Json.Nodes;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Notifications;
using BackupForge.Engine.Restic;
using BackupForge.Ipc;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>restic binary check at start (SPEC 7.1), version for the status, and the repo.* / restic restore IPC operations.</summary>
public sealed class ResticComponent(ResticBinary binary, IProcessRunner processes, IEventSink events) : IServiceComponent
{
    private ServiceRuntime _rt = null!;

    public ResticClient Client => new(binary, processes, _rt.Paths.ResticCache, _rt.Masker);

    public async Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        var log = runtime.Loggers.CreateLogger("BackupForge.Restic");
        if (!runtime.ConfigStore.Current.Jobs.Any(j => j.Type == JobTypes.Restic) && !File.Exists(binary.Path)) return;
        if (binary.Verify() is { } bad)
        {
            log.LogError("restic binary problem: {Problem}", bad);
            events.Write(EventLevel.Error, EventIds.UnexpectedError, "restic.exe: " + bad);
            return;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var v = await Client.VersionAsync(cts.Token).ConfigureAwait(false);
            if (v is not null) runtime.History.SetValue(ResticState.VersionKey, v);
            log.LogInformation("restic {Version} ({Path}{Verified})", v, binary.Path, binary.ExpectedSha256 is null ? ", unverified override" : ", hash ok");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning("restic version check failed: {Error}", ex.Message);
        }
    }

    public void RegisterIpc(ServiceIpcHandler h)
    {
        h.Register(IpcOps.RepoInit, async (req, _, ct) =>
        {
            var repo = Repo(req);
            if (!req.Bool("confirm")) throw new IpcException(IpcErrorCodes.Rejected, "creating a repository needs explicit confirmation (confirm = true)");
            var r = await Wrap(() => Client.InitAsync(repo, _rt.Secrets, ct)).ConfigureAwait(false);
            _rt.History.AddConfigChange(_rt.Clock.UtcNow, "gui-user", $"restic repository '{repo.Id}' initialised", null);
            return new JsonObject { ["repo_id"] = repo.Id, ["output"] = r };
        });
        h.Register(IpcOps.RepoSnapshots, async (req, _, ct) =>
        {
            var repo = Repo(req);
            var list = await Wrap(() => Client.SnapshotsAsync(repo, _rt.Secrets, req.Str("tag"), ct)).ConfigureAwait(false);
            var arr = new JsonArray();
            foreach (var s in list.OrderByDescending(s => s.Time))
            {
                var tags = new JsonArray();
                foreach (var t in s.Tags ?? []) tags.Add(t);
                var paths = new JsonArray();
                foreach (var p in s.Paths) paths.Add(p);
                arr.Add(new JsonObject { ["id"] = s.Id, ["short_id"] = s.ShortId, ["time"] = s.Time.ToString("o"), ["hostname"] = s.Hostname, ["tags"] = tags, ["paths"] = paths });
            }
            return arr;
        });
        h.Register(IpcOps.RepoLs, async (req, _, ct) =>
        {
            var repo = Repo(req);
            var nodes = await Wrap(() => Client.LsAsync(repo, _rt.Secrets, req.Required("snapshot"), req.Str("path"), ct)).ConfigureAwait(false);
            var arr = new JsonArray();
            foreach (var n in nodes) arr.Add(new JsonObject { ["name"] = n.Name, ["type"] = n.Type, ["path"] = n.Path, ["size"] = n.Size, ["mtime"] = n.Mtime?.ToString("o") });
            return arr;
        });
        h.Register(IpcOps.RepoCheck, async (req, _, ct) =>
        {
            var repo = Repo(req);
            var r = _rt.Coordinator.Enqueue(new WorkRequest { JobId = MaintenanceIds.Repo(repo.Id), Kind = RunKinds.Check, Trigger = RunTriggers.Manual });
            if (!r.Accepted) throw new IpcException(IpcErrorCodes.Rejected, r.Reason ?? "rejected");
            if (!req.Bool("wait")) return new JsonObject { ["queued"] = repo.Id };
            var rec = await r.Completion.WaitAsync(ct).ConfigureAwait(false);
            return new JsonObject { ["run_id"] = rec?.Id, ["result"] = rec?.Result, ["summary"] = rec?.Summary };
        });
    }

    private ResticRepoConfig Repo(IpcRequest req)
    {
        var id = req.Required("repo_id");
        return _rt.ConfigStore.Current.FindRepo(id) ?? throw new IpcException(IpcErrorCodes.NotFound, $"unknown repository '{id}'");
    }

    private async Task<T> Wrap<T>(Func<Task<T>> f)
    {
        if (binary.Verify() is { } bad) throw new IpcException(IpcErrorCodes.Rejected, "restic.exe: " + bad);
        try { return await f().ConfigureAwait(false); }
        catch (ResticOperationException ex) { throw new IpcException(IpcErrorCodes.Rejected, _rt.Masker.Apply(ex.Message)); }
        catch (ResticConfigException ex) { throw new IpcException(IpcErrorCodes.Rejected, ex.Message); }
    }
}
