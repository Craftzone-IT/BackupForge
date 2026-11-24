using System.Text.Json;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Resources;
using BackupForge.Core.Status;
using BackupForge.Ipc;

namespace BackupForge.Service;

/// <summary>Maps IPC operations to the runtime. Every request is untrusted input (SPEC 14.).</summary>
public sealed class ServiceIpcHandler
{
    private readonly ServiceRuntime _rt;
    private readonly Dictionary<string, Func<IpcRequest, IpcConnection, CancellationToken, Task<JsonNode?>>> _ops = new(StringComparer.Ordinal);

    public ServiceIpcHandler(ServiceRuntime runtime)
    {
        _rt = runtime;
        Register(IpcOps.Hello, (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["protocol"] = IpcProtocol.Version,
            ["app_version"] = ServiceRuntime.AppVersion,
            ["instance_name"] = _rt.ConfigStore.Current.InstanceName,
        }));
        Register(IpcOps.StatusGet, (_, _, _) => Task.FromResult(StatusNode()));
        Register(IpcOps.ConfigGet, (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["config"] = JsonNode.Parse(ConfigJson.Serialize(_rt.ConfigStore.Current)),
            ["file_error"] = _rt.ConfigStore.Error,
        }));
        Register(IpcOps.ConfigValidate, (req, _, _) => Task.FromResult(Validate(req)));
        Register(IpcOps.ConfigSave, (req, _, _) => Task.FromResult(Save(req)));
        Register(IpcOps.ConfigReload, (_, _, _) =>
        {
            var r = _rt.ReloadConfig("cli");
            if (!r.Ok) throw new IpcException(IpcErrorCodes.Invalid, r.Error ?? "invalid configuration", ValidationNode(r.Validation));
            return Task.FromResult<JsonNode?>(new JsonObject { ["changed"] = r.Changed });
        });
        Register(IpcOps.SecretsSet, (req, _, _) =>
        {
            var reference = req.Required("ref");
            var value = req.Str("value") ?? throw new IpcException(IpcErrorCodes.BadRequest, "missing argument 'value'");
            if (reference.Length > 200) throw new IpcException(IpcErrorCodes.BadRequest, "reference too long");
            if (value.Length == 0) _rt.Secrets.Remove(reference);
            else
            {
                _rt.Secrets.Set(reference, value);
                _rt.Masker.Register(value);
            }
            _rt.History.AddConfigChange(_rt.Clock.UtcNow, "gui-user", $"secret '{reference}' {(value.Length == 0 ? "removed" : "set")}", null);
            return Task.FromResult<JsonNode?>(new JsonObject { ["ref"] = reference, ["is_set"] = value.Length > 0 });
        });
        Register(IpcOps.SecretsList, (_, _, _) =>
        {
            var arr = new JsonArray();
            foreach (var r in _rt.Secrets.References.Order(StringComparer.Ordinal)) arr.Add(new JsonObject { ["ref"] = r, ["is_set"] = true });
            return Task.FromResult<JsonNode?>(arr);
        });
        Register(IpcOps.JobRun, RunAsync);
        Register(IpcOps.JobDryRun, (req, c, ct) =>
        {
            req.Args ??= [];
            req.Args["dry_run"] = true;
            req.Args["wait"] = true;
            return RunAsync(req, c, ct);
        });
        Register(IpcOps.JobCancel, (req, _, _) =>
        {
            var id = req.Required("job_id");
            if (!_rt.Coordinator.Cancel(id)) throw new IpcException(IpcErrorCodes.NotFound, $"'{id}' is not running or queued");
            return Task.FromResult<JsonNode?>(new JsonObject { ["cancelled"] = id });
        });
        Register(IpcOps.JobApproveDelete, (req, _, _) =>
        {
            var id = req.Required("job_id");
            if (_rt.ConfigStore.Current.FindJob(id) is null) throw new IpcException(IpcErrorCodes.NotFound, $"unknown job '{id}'");
            _rt.History.SetValue(DeleteGuardState.ApprovedKey(id), _rt.Clock.UtcNow.ToString("o"));
            _rt.History.AddConfigChange(_rt.Clock.UtcNow, "gui-user", $"job '{id}': mass delete approved once", null);
            return Task.FromResult<JsonNode?>(new JsonObject { ["approved"] = id });
        });
        Register(IpcOps.HistoryList, (req, _, _) =>
        {
            var job = req.Str("job_id");
            var last = (int)Math.Clamp(req.Long("last") ?? 20, 1, 1000);
            var kind = req.Str("kind");
            var arr = new JsonArray();
            foreach (var r in _rt.History.ListRuns(job, last, kind)) arr.Add(RunNode(r));
            return Task.FromResult<JsonNode?>(arr);
        });
        Register(IpcOps.HistoryGet, (req, _, _) =>
        {
            var id = req.Long("run_id") ?? throw new IpcException(IpcErrorCodes.BadRequest, "missing argument 'run_id'");
            var r = _rt.History.GetRun(id) ?? throw new IpcException(IpcErrorCodes.NotFound, $"run {id} not found");
            var node = RunNode(r);
            var items = new JsonArray();
            foreach (var i in _rt.History.GetItems(id)) items.Add(new JsonObject { ["level"] = i.Level, ["path"] = i.Path, ["message"] = i.Message });
            node["items"] = items;
            node["log_path"] = _rt.Paths.RunLogFile(r.JobId, r.Id);
            return Task.FromResult<JsonNode?>(node);
        });
        Register(IpcOps.LogTail, (req, _, _) =>
        {
            var id = req.Long("run_id") ?? throw new IpcException(IpcErrorCodes.BadRequest, "missing argument 'run_id'");
            var r = _rt.History.GetRun(id) ?? throw new IpcException(IpcErrorCodes.NotFound, $"run {id} not found");
            var lines = (int)Math.Clamp(req.Long("lines") ?? 200, 1, 100_000);
            var path = _rt.Paths.RunLogFile(r.JobId, r.Id);
            var text = File.Exists(path) ? Tail(path, lines) : [];
            var arr = new JsonArray();
            foreach (var l in text) arr.Add(l);
            return Task.FromResult<JsonNode?>(new JsonObject { ["path"] = path, ["lines"] = arr });
        });
        Register(IpcOps.NotifyTest, async (_, _, ct) =>
        {
            if (_rt.Notifier is null) throw new IpcException(IpcErrorCodes.Rejected, "notifications are not available");
            try { await _rt.Notifier.SendTestAsync(ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { throw new IpcException(IpcErrorCodes.Rejected, ex.Message); }
            return new JsonObject { ["sent"] = true };
        });
        Register(IpcOps.TargetTest, (req, _, _) =>
        {
            var id = req.Required("target_id");
            var t = _rt.ConfigStore.Current.FindTarget(id) ?? throw new IpcException(IpcErrorCodes.NotFound, $"unknown target '{id}'");
            if (_rt.TargetProbe is null) throw new IpcException(IpcErrorCodes.Rejected, "not available");
            var st = _rt.TargetProbe.Probe(t);
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["target_id"] = id, ["path"] = st.Path, ["online"] = st.Online, ["free_gb"] = st.FreeGb, ["size_gb"] = st.SizeGb, ["error"] = st.Error,
                ["account"] = Environment.UserName,
            });
        });
        Register(IpcOps.EventsSubscribe, (_, conn, _) =>
        {
            conn.Subscribed = true;
            return Task.FromResult<JsonNode?>(new JsonObject { ["subscribed"] = true });
        });
    }

    /// <summary>Extension point for later phases (restore, repo, sql, notify …).</summary>
    public void Register(string op, Func<IpcRequest, IpcConnection, CancellationToken, Task<JsonNode?>> handler) => _ops[op] = handler;

    public Task<JsonNode?> HandleAsync(IpcRequest request, IpcConnection connection, CancellationToken ct) =>
        _ops.TryGetValue(request.Op, out var h)
            ? h(request, connection, ct)
            : throw new IpcException(IpcErrorCodes.UnknownOp, $"unknown operation '{request.Op}'");

    public Task BroadcastAsync(CoordinatorEvent e)
    {
        if (_rt.Ipc is null) return Task.CompletedTask;
        var data = new JsonObject
        {
            ["job_id"] = e.JobId,
            ["kind"] = e.Kind,
            ["run_id"] = e.RunId,
            ["phase"] = e.Progress?.Phase,
            ["percent"] = e.Progress?.Percent,
            ["message"] = e.Progress?.Message,
            ["result"] = e.Record?.Result,
            ["summary"] = e.Record?.Summary,
        };
        return _rt.Ipc.BroadcastAsync("run." + e.Type.ToString().ToLowerInvariant(), data);
    }

    private async Task<JsonNode?> RunAsync(IpcRequest req, IpcConnection conn, CancellationToken ct)
    {
        var id = req.Required("job_id");
        if (_rt.ConfigStore.Current.FindJob(id) is null) throw new IpcException(IpcErrorCodes.NotFound, $"unknown job '{id}'");
        var options = new RunOptions { DryRun = req.Bool("dry_run"), AllowMassDelete = req.Bool("allow_mass_delete") };
        var r = _rt.Coordinator.Enqueue(new WorkRequest { JobId = id, Trigger = RunTriggers.Manual, Options = options });
        if (!r.Accepted) throw new IpcException(IpcErrorCodes.Rejected, r.Reason ?? "rejected");
        if (!req.Bool("wait")) return new JsonObject { ["queued"] = id };
        var record = await r.Completion.WaitAsync(ct).ConfigureAwait(false);
        if (record is null) return new JsonObject { ["queued"] = id, ["result"] = null, ["summary"] = "cancelled before start" };
        var node = RunNode(record);
        if (options.DryRun)
        {
            var path = _rt.Paths.RunLogFile(record.JobId, record.Id);
            var lines = new JsonArray();
            if (File.Exists(path)) foreach (var l in Tail(path, 2000)) lines.Add(l);
            node["log"] = lines;
        }
        return node;
    }

    private JsonNode? StatusNode()
    {
        var doc = _rt.BuildStatus();
        var node = JsonNode.Parse(StatusJson.Serialize(doc))!.AsObject();
        // extra live info for GUI / CLI (not part of status.json)
        var queued = new JsonArray();
        foreach (var q in _rt.Coordinator.Queued) queued.Add(new JsonObject { ["job_id"] = q.JobId, ["kind"] = q.Kind, ["trigger"] = q.Trigger });
        node["queued"] = queued;
        var running = new JsonArray();
        foreach (var a in _rt.Coordinator.Running)
            running.Add(new JsonObject { ["job_id"] = a.JobId, ["kind"] = a.Kind, ["run_id"] = a.RunId, ["phase"] = a.Phase, ["percent"] = a.Percent });
        node["running_all"] = running;
        return node;
    }

    private JsonNode? Validate(IpcRequest req)
    {
        var cfg = ParseConfigArg(req);
        var v = ConfigValidator.Validate(cfg, _rt.Secrets);
        return new JsonObject { ["valid"] = v.IsValid, ["errors"] = ValidationNode(v) };
    }

    private JsonNode? Save(IpcRequest req)
    {
        var cfg = ParseConfigArg(req);
        var by = req.Str("by") is "cli" ? "cli" : "gui-user";
        var v = _rt.SaveConfig(cfg, by);
        if (!v.IsValid) throw new IpcException(IpcErrorCodes.Invalid, "invalid configuration", ValidationNode(v));
        return new JsonObject { ["saved"] = true };
    }

    private static BackupConfig ParseConfigArg(IpcRequest req)
    {
        var node = req.Args?["config"] ?? throw new IpcException(IpcErrorCodes.BadRequest, "missing argument 'config'");
        try
        {
            return node.GetValueKind() == JsonValueKind.String ? ConfigJson.Parse(node.GetValue<string>()) : ConfigJson.Parse(node.ToJsonString());
        }
        catch (ConfigFormatException ex)
        {
            throw new IpcException(IpcErrorCodes.Invalid, ex.Message, new JsonArray(new JsonObject { ["path"] = ex.JsonPath, ["code"] = "format.invalid", ["message"] = ex.Message }));
        }
    }

    private JsonArray ValidationNode(ValidationResult? v)
    {
        var arr = new JsonArray();
        if (v is null) return arr;
        var culture = Texts.CultureFor(_rt.ConfigStore.Current.Language);
        foreach (var e in v.Errors) arr.Add(new JsonObject { ["path"] = e.Path, ["code"] = e.Code, ["message"] = e.Message(culture) });
        return arr;
    }

    private JsonObject RunNode(RunRecord r) => new()
    {
        ["run_id"] = r.Id,
        ["job_id"] = r.JobId,
        ["chain_run_id"] = r.ChainRunId,
        ["kind"] = r.Kind,
        ["trigger"] = r.Trigger,
        ["start"] = Iso(r.StartedAt),
        ["end"] = r.EndedAt is null ? null : Iso(r.EndedAt.Value),
        ["result"] = r.Result,
        ["summary"] = r.Summary,
        ["files_processed"] = r.FilesProcessed,
        ["files_copied"] = r.FilesCopied,
        ["files_versioned"] = r.FilesVersioned,
        ["files_deleted"] = r.FilesDeleted,
        ["files_skipped"] = r.FilesSkipped,
        ["bytes_processed"] = r.BytesProcessed,
        ["bytes_transferred"] = r.BytesTransferred,
        ["error_count"] = r.ErrorCount,
        ["warning_count"] = r.WarningCount,
    };

    private string Iso(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, _rt.Clock.LocalZone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    internal static List<string> Tail(string path, int lines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var q = new Queue<string>();
        while (sr.ReadLine() is { } l)
        {
            q.Enqueue(l);
            if (q.Count > lines) q.Dequeue();
        }
        return [.. q];
    }
}
