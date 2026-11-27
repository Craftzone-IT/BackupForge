using System.Text.Json;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Status;
using BackupForge.Engine.Sql;
using BackupForge.Ipc;

namespace BackupForge.Service.Components;

/// <summary>
/// keep_local cleanup after the successful end of the chain (SPEC 8.3), the sql block of status.json and sql.discover.
/// </summary>
public sealed class SqlSupport(ISqlSessionFactory sessions) : IServiceComponent
{
    private ServiceRuntime _rt = null!;

    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        runtime.Coordinator.Changed += OnEvent;
        return Task.CompletedTask;
    }

    private void OnEvent(CoordinatorEvent e)
    {
        if (e.Type != CoordinatorEventType.Finished || e.Kind != RunKinds.Backup || !RunResults.IsSuccess(e.Record?.Result)) return;
        var cfg = _rt.ConfigStore.Current;
        var job = cfg.FindJob(e.JobId);
        if (job is null) return;
        // a job that consumed @staging:<sql job> finished successfully → the chain end for that staging
        foreach (var src in job.Sources.Where(s => s.Path.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal)))
        {
            if (job.OnSuccess.Any(n => cfg.FindJob(n)?.Sources.Any(x => x.Path == src.Path) == true)) continue; // not the last consumer
            var sqlJob = cfg.FindJob(src.Path[ConfigValidator.StagingPrefix.Length..]);
            if (sqlJob?.Sql is null) continue;
            SqlPlanning.CleanupStaging(StagingPaths.For(cfg, _rt.Paths, sqlJob.Id), sqlJob.Sql.KeepLocal);
        }
    }

    /// <summary>The sql block of status.json from history (per-db results) and kv (edition, last full by anyone, CHECKDB).</summary>
    public SqlStatus Status(JobConfig job)
    {
        var h = _rt.History;
        var st = new SqlStatus { Instance = job.Sql?.Instance ?? "" };
        if (h.GetValue(SqlState.InfoKey(job.Id)) is { } infoJson && JsonSerializer.Deserialize<string[]>(infoJson) is { Length: 2 } info)
        {
            st.Edition = info[0];
            st.Version = info[1];
        }
        var fullAny = h.GetValue(SqlState.LastFullAnyKey(job.Id)) is { } fa ? JsonSerializer.Deserialize<Dictionary<string, string>>(fa) ?? [] : [];
        var recovery = h.GetValue(SqlState.RecoveryKey(job.Id)) is { } rj ? JsonSerializer.Deserialize<Dictionary<string, string>>(rj) ?? [] : [];
        var rows = h.SqlResults(job.Id);
        foreach (var g in rows.GroupBy(r => r.Result.Database, StringComparer.OrdinalIgnoreCase))
        {
            var last = g.First();
            var oks = g.Where(r => r.Result.Result == RunResults.Ok).ToList();
            var db = new SqlDbStatus
            {
                Name = g.Key,
                System = last.Result.System,
                RecoveryModel = recovery.GetValueOrDefault(g.Key) ?? last.Result.RecoveryModel,
                LastResult = last.Result.Result,
                LastOk = oks.FirstOrDefault()?.StartedAt,
                LastSize = last.Result.SizeBytes,
                MedianOkSize = SqlPlanning.Median(oks.Take(20).Select(r => r.Result.SizeBytes ?? 0).Where(x => x > 0)),
                VerifyOk = last.Result.VerifyOk,
                LastFullAny = fullAny.TryGetValue(g.Key, out var t) && DateTimeOffset.TryParse(t, out var tt) ? tt : null,
                Error = last.Result.Error,
            };
            if (h.GetValue(SqlState.CheckdbKey(job.Id, g.Key)) is { } cj)
            {
                using var d = JsonDocument.Parse(cj);
                if (DateTimeOffset.TryParse(d.RootElement.GetProperty("at").GetString(), out var at)) db.LastCheckdb = at;
                db.LastCheckdbOk = d.RootElement.GetProperty("ok").GetBoolean();
            }
            st.Dbs.Add(db);
        }
        var staging = StagingPaths.For(_rt.ConfigStore.Current, _rt.Paths, job.Id);
        st.Staging = new StagingStatus { Path = staging };
        // a staging folder on a credentialed SMB server is not measured by the minutely status (no lease there, CR-003 2.)
        if (Core.Abstractions.LeaseGuardFileSystem.ServerOf(staging) is not { } server || !_rt.NeedsLease(server))
            if (Directory.Exists(staging) && Core.Abstractions.PhysicalFileSystem.Instance.GetSpace(staging) is var (free, _)) st.Staging.FreeGb = Math.Round(free / 1073741824.0, 1);
        return st;
    }

    public void RegisterIpc(ServiceIpcHandler h)
    {
        h.Register(IpcOps.SqlDiscover, async (req, _, ct) =>
        {
            var instances = req.Str("instance") is { } one ? [one] : SqlDiscovery.LocalInstances();
            var arr = new JsonArray();
            foreach (var inst in instances)
            {
                var node = new JsonObject { ["instance"] = inst };
                try
                {
                    await using var s = await sessions.OpenAsync(inst, null, null, ct).ConfigureAwait(false);
                    var info = await s.GetInfoAsync(ct).ConfigureAwait(false);
                    node["edition"] = info.Edition;
                    node["version"] = info.Version;
                    var dbs = new JsonArray();
                    Dictionary<string, DateTime> full = [];
                    try { full = await s.LastBackupsAsync('D', ct).ConfigureAwait(false); } catch (SqlCommandException) { }
                    foreach (var d in await s.ListDatabasesAsync(ct).ConfigureAwait(false))
                        dbs.Add(new JsonObject
                        {
                            ["name"] = d.Name, ["state"] = d.State, ["recovery_model"] = d.RecoveryModel, ["system"] = d.IsSystem,
                            ["size_bytes"] = d.EstimatedBackupBytes, ["last_full_any"] = full.TryGetValue(d.Name, out var f) ? f.ToString("o") : null,
                        });
                    node["databases"] = dbs;
                }
                catch (SqlCommandException ex)
                {
                    node["error"] = ex.IsPermission || ex.Number == 18456
                        ? $"{ex.Message} — the service login is missing: run 'bf sql grant <jobId>' as an administrator"
                        : ex.Message;
                }
                arr.Add(node);
            }
            return arr;
        });
    }
}

/// <summary>Local SQL Server instances from the services (MSSQLSERVER, MSSQL$NAME) — SPEC 8.1.</summary>
public static class SqlDiscovery
{
    public static List<string> LocalInstances()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        foreach (var s in System.ServiceProcess.ServiceController.GetServices())
        {
            if (s.ServiceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)) list.Add(".");
            else if (s.ServiceName.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase)) list.Add(@".\" + s.ServiceName[6..]);
            s.Dispose();
        }
        return list;
    }
}
