using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Resources;
using BackupForge.Ipc;

namespace BackupForge.Cli;

/// <summary>bf.exe (SPEC 11.3). Exit codes: 0 ok, 1 error, 2 usage, 3 service not reachable; run --wait: 0 ok, 10 warning, 11 failed.</summary>
public static class BfCli
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;
    public const int ExitUnavailable = 3;
    public const int ExitWarning = 10;
    public const int ExitFailed = 11;

    public const string Usage = """
        BackupForge command line

          bf status [--json]                  service and job status
          bf jobs                             list jobs
          bf run <jobId> [--dry-run] [--allow-mass-delete] [--wait]
          bf cancel <jobId>
          bf approve-delete <jobId>           allow one mass delete (delete guard)
          bf history [<jobId>] [--last N]
          bf log <runId> [--lines N]
          bf config validate [<file>]
          bf config reload
          bf config export [<file>]           without secrets
          bf config import <file>
          bf secret set <ref>                 value from stdin (or a hidden prompt)
          bf notify test
          bf target test <targetId>
          bf repo init|snapshots|check <repoId> [--yes]
          bf restore <jobId> ...              see 'bf restore --help'
          bf sql discover [<instance>] | grant <jobId> [--login <sqllogin>] [--yes]
          bf report preview [<file.html>]
          bf diag vss [<path>] [--no-writers]  VSS self-test (elevated, without the service)

        Exit codes: 0 ok, 1 error, 2 usage, 3 service not reachable; with --wait: 0 ok, 10 warning, 11 failed.
        """;

    public static Func<Task<IpcClient>> Connect { get; set; } = () => IpcClient.ConnectAsync();

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }
        var a = new Args(args);
        try
        {
            return a.Command switch
            {
                "status" => await Status(a, stdout).ConfigureAwait(false),
                "jobs" => await Jobs(stdout).ConfigureAwait(false),
                "run" => await Run(a, stdout).ConfigureAwait(false),
                "cancel" => await Simple(IpcOps.JobCancel, new JsonObject { ["job_id"] = a.Positional(1, "jobId") }, stdout, $"cancel requested: {a.Positional(1, "jobId")}").ConfigureAwait(false),
                "approve-delete" => await Simple(IpcOps.JobApproveDelete, new JsonObject { ["job_id"] = a.Positional(1, "jobId") }, stdout, "approved for the next run").ConfigureAwait(false),
                "history" => await History(a, stdout).ConfigureAwait(false),
                "log" => await Log(a, stdout).ConfigureAwait(false),
                "config" => await Config(a, stdout, stderr).ConfigureAwait(false),
                "secret" => await Secret(a, stdout, stdin).ConfigureAwait(false),
                "notify" when a.Positional(1, "test") == "test" => await Simple(IpcOps.NotifyTest, null, stdout, "test message sent").ConfigureAwait(false),
                "target" when a.Positional(1, "test") == "test" => await Generic(IpcOps.TargetTest, new JsonObject { ["target_id"] = a.Positional(2, "targetId") }, stdout).ConfigureAwait(false),
                "repo" => await Repo(a, stdout).ConfigureAwait(false),
                "restore" => await Restore(a, stdout).ConfigureAwait(false),
                "sql" => await Sql(a, stdout).ConfigureAwait(false),
                "report" => await Report(a, stdout).ConfigureAwait(false),
                "diag" when a.Positional(1, "vss") == "vss" => DiagVss(a, stdout),
                _ => throw new UsageException($"unknown command '{a.Command}'"),
            };
        }
        catch (UsageException ex)
        {
            stderr.WriteLine("error: " + ex.Message);
            stderr.WriteLine("run 'bf --help' for usage");
            return ExitUsage;
        }
        catch (IpcUnavailableException ex)
        {
            stderr.WriteLine("error: " + ex.Message);
            return ExitUnavailable;
        }
        catch (Engine.Sql.SqlCommandException ex)
        {
            stderr.WriteLine("SQL error: " + ex.Message);
            return ExitError;
        }
        catch (IpcException ex)
        {
            stderr.WriteLine("error: " + ex.Message);
            if (ex.Details is JsonArray details)
                foreach (var d in details) stderr.WriteLine($"  {d?["path"]}: {d?["message"]}");
            return ExitError;
        }
    }

    private static async Task<JsonNode?> Call(string op, JsonObject? args = null)
    {
        await using var c = await Connect().ConfigureAwait(false);
        return await c.CallAsync(op, args).ConfigureAwait(false);
    }

    private static async Task<int> Simple(string op, JsonObject? args, TextWriter stdout, string message)
    {
        await Call(op, args).ConfigureAwait(false);
        stdout.WriteLine(message);
        return ExitOk;
    }

    private static async Task<int> Generic(string op, JsonObject? args, TextWriter stdout)
    {
        var r = await Call(op, args).ConfigureAwait(false);
        stdout.WriteLine(r?.ToJsonString(IpcProtocol.JsonIndented) ?? "ok");
        return ExitOk;
    }

    private static async Task<int> Status(Args a, TextWriter o)
    {
        var s = await Call(IpcOps.StatusGet).ConfigureAwait(false);
        if (a.Has("--json"))
        {
            o.WriteLine(s!.ToJsonString(IpcProtocol.JsonIndented));
            return ExitOk;
        }
        var svc = s!["service"]!;
        o.WriteLine($"{s["app"]} {s["app_version"]} — {s["instance_name"]} ({s["hostname"]})");
        o.WriteLine($"service started {svc["started"]}, heartbeat {svc["heartbeat"]}");
        o.WriteLine(svc["config_ok"]!.GetValue<bool>() ? "config: ok" : $"CONFIG ERROR: {svc["config_error"]}");
        o.WriteLine();
        var rows = new List<string[]> { new[] { "JOB", "TYPE", "STATE", "LAST RUN", "RESULT", "NEXT RUN", "SUMMARY" } };
        foreach (var j in s["jobs"]!.AsArray())
        {
            var state = !j!["enabled"]!.GetValue<bool>() ? "DISABLED"
                : j["running"] is JsonObject r ? $"running {r["phase"]} {Pct(r["percent"])}".TrimEnd()
                : j["stale"]!.GetValue<bool>() ? "STALE" : "idle";
            var last = j["last_run"];
            rows.Add([
                j["id"]!.ToString(), j["type"]!.ToString(), state,
                Short(last?["start"]), last?["result"]?.ToString() ?? "-", Short(j["next_run"]), last?["summary"]?.ToString() ?? "",
            ]);
        }
        Table(o, rows);
        foreach (var t in s["targets"]!.AsArray())
            o.WriteLine($"target {t!["id"]}: {(t["online"]!.GetValue<bool>() ? "online" : "OFFLINE")} {Gb(t["free_gb"])} free {(t["error"] is null ? "" : "— " + t["error"])}");
        return ExitOk;
    }

    private static async Task<int> Jobs(TextWriter o)
    {
        var s = await Call(IpcOps.StatusGet).ConfigureAwait(false);
        var rows = new List<string[]> { new[] { "JOB", "NAME", "TYPE", "ENABLED", "SCHEDULE" } };
        foreach (var j in s!["jobs"]!.AsArray())
            rows.Add([j!["id"]!.ToString(), j["name"]!.ToString(), j["type"]!.ToString(), j["enabled"]!.GetValue<bool>() ? "yes" : "NO", j["schedule_text"]!.ToString()]);
        Table(o, rows);
        return ExitOk;
    }

    private static async Task<int> Run(Args a, TextWriter o)
    {
        var job = a.Positional(1, "jobId");
        var dry = a.Has("--dry-run");
        var wait = a.Has("--wait") || dry;
        await using var c = await Connect().ConfigureAwait(false);
        if (wait)
        {
            c.EventReceived += e =>
            {
                if (e.Data?["job_id"]?.ToString() != job) return;
                if (e.Event == "run.progress") o.WriteLine($"  {e.Data["phase"]} {Pct(e.Data["percent"])} {e.Data["message"]}".TrimEnd());
            };
            await c.CallAsync(IpcOps.EventsSubscribe).ConfigureAwait(false);
        }
        var r = await c.CallAsync(IpcOps.JobRun, new JsonObject
        {
            ["job_id"] = job,
            ["dry_run"] = dry,
            ["allow_mass_delete"] = a.Has("--allow-mass-delete"),
            ["wait"] = wait,
        }).ConfigureAwait(false);
        if (!wait)
        {
            o.WriteLine($"queued: {job}");
            return ExitOk;
        }
        if (r?["log"] is JsonArray log)
            foreach (var l in log) o.WriteLine(l?.ToString());
        var result = r?["result"]?.ToString();
        o.WriteLine($"{job}: run {r?["run_id"]} {result ?? "not run"} — {r?["summary"]}");
        return result switch
        {
            "ok" => ExitOk,
            "warning" => ExitWarning,
            _ => ExitFailed,
        };
    }

    private static async Task<int> History(Args a, TextWriter o)
    {
        var args = new JsonObject { ["last"] = a.Int("--last", 20) };
        if (a.PositionalOrNull(1) is { } job) args["job_id"] = job;
        var list = await Call(IpcOps.HistoryList, args).ConfigureAwait(false);
        var rows = new List<string[]> { new[] { "RUN", "JOB", "KIND", "TRIGGER", "START", "END", "RESULT", "SUMMARY" } };
        foreach (var r in list!.AsArray())
            rows.Add([r!["run_id"]!.ToString(), r["job_id"]!.ToString(), r["kind"]!.ToString(), r["trigger"]!.ToString(), Short(r["start"]), Short(r["end"]), r["result"]?.ToString() ?? "running", r["summary"]!.ToString()]);
        Table(o, rows);
        return ExitOk;
    }

    private static async Task<int> Log(Args a, TextWriter o)
    {
        if (!long.TryParse(a.Positional(1, "runId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new UsageException("runId must be a number");
        var r = await Call(IpcOps.LogTail, new JsonObject { ["run_id"] = id, ["lines"] = a.Int("--lines", 500) }).ConfigureAwait(false);
        foreach (var l in r!["lines"]!.AsArray()) o.WriteLine(l?.ToString());
        return ExitOk;
    }

    private static async Task<int> Config(Args a, TextWriter o, TextWriter e)
    {
        switch (a.Positional(1, "validate|reload|export|import"))
        {
            case "validate":
            {
                var file = a.PositionalOrNull(2);
                if (file is null)
                {
                    var current = await Call(IpcOps.ConfigGet).ConfigureAwait(false);
                    if (current?["file_error"] is { } fe) { e.WriteLine("config.json: " + fe); return ExitError; }
                    o.WriteLine("config.json: valid (in use)");
                    return ExitOk;
                }
                var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                JsonNode? res;
                try
                {
                    res = await Call(IpcOps.ConfigValidate, new JsonObject { ["config"] = text }).ConfigureAwait(false);
                }
                catch (IpcUnavailableException)
                {
                    // offline: structural + semantic validation without the secret check
                    BackupConfig cfg;
                    try { cfg = ConfigJson.Parse(text); }
                    catch (ConfigFormatException ex) { e.WriteLine(ex.Message); return ExitError; }
                    var v = ConfigValidator.Validate(cfg);
                    foreach (var err in v.Errors) e.WriteLine($"{err.Path}: {err.Message(Texts.CultureFor(cfg.Language))}");
                    o.WriteLine(v.IsValid ? $"{file}: valid (secrets not checked: service not reachable)" : $"{file}: {v.Errors.Count} error(s)");
                    return v.IsValid ? ExitOk : ExitError;
                }
                foreach (var err in res!["errors"]!.AsArray()) e.WriteLine($"{err!["path"]}: {err["message"]}");
                var valid = res["valid"]!.GetValue<bool>();
                o.WriteLine(valid ? $"{file}: valid" : $"{file}: {res["errors"]!.AsArray().Count} error(s)");
                return valid ? ExitOk : ExitError;
            }
            case "reload":
            {
                var r = await Call(IpcOps.ConfigReload).ConfigureAwait(false);
                o.WriteLine(r?["changed"]?.GetValue<bool>() == true ? "configuration reloaded (changed)" : "configuration reloaded (no change)");
                return ExitOk;
            }
            case "export":
            {
                var r = await Call(IpcOps.ConfigGet).ConfigureAwait(false);
                var json = r!["config"]!.ToJsonString(IpcProtocol.JsonIndented);
                if (a.PositionalOrNull(2) is { } path) { await File.WriteAllTextAsync(path, json).ConfigureAwait(false); o.WriteLine($"exported to {path} (secrets are never exported, only their references)"); }
                else o.WriteLine(json);
                return ExitOk;
            }
            case "import":
            {
                var path = a.Positional(2, "file");
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                await Call(IpcOps.ConfigSave, new JsonObject { ["config"] = text, ["by"] = "cli" }).ConfigureAwait(false);
                o.WriteLine("configuration imported; set missing secrets with 'bf secret set <ref>'");
                var v = await Call(IpcOps.SecretsList).ConfigureAwait(false);
                _ = v;
                return ExitOk;
            }
            default:
                throw new UsageException("bf config validate|reload|export|import");
        }
    }

    private static async Task<int> Secret(Args a, TextWriter o, TextReader stdin)
    {
        if (a.Positional(1, "set") != "set") throw new UsageException("bf secret set <ref>");
        var reference = a.Positional(2, "ref");
        string? value;
        if (!Console.IsInputRedirected && ReferenceEquals(stdin, Console.In))
        {
            o.Write($"value for '{reference}': ");
            value = ReadHidden();
            o.WriteLine();
        }
        else value = stdin.ReadLine();
        if (value is null) throw new UsageException("no value given");
        await Call(IpcOps.SecretsSet, new JsonObject { ["ref"] = reference, ["value"] = value }).ConfigureAwait(false);
        o.WriteLine(value.Length == 0 ? $"secret '{reference}' removed" : $"secret '{reference}' stored");
        return ExitOk;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            sb.Append(k.KeyChar);
        }
        return sb.ToString();
    }

    private static async Task<int> Repo(Args a, TextWriter o)
    {
        var sub = a.Positional(1, "init|snapshots|check");
        var repo = a.Positional(2, "repoId");
        switch (sub)
        {
            case "init":
                if (!a.Has("--yes")) throw new UsageException($"'bf repo init {repo}' creates a NEW, empty repository at the configured location — repeat with --yes to confirm");
                return await Generic(IpcOps.RepoInit, new JsonObject { ["repo_id"] = repo, ["confirm"] = true }, o).ConfigureAwait(false);
            case "snapshots":
            {
                var r = await Call(IpcOps.RepoSnapshots, new JsonObject { ["repo_id"] = repo }).ConfigureAwait(false);
                var rows = new List<string[]> { new[] { "ID", "TIME", "HOST", "TAGS", "PATHS" } };
                foreach (var s in r!.AsArray())
                    rows.Add([s!["short_id"]?.ToString() ?? "", Short(s["time"]), s["hostname"]?.ToString() ?? "", string.Join(",", s["tags"]?.AsArray().Select(x => x?.ToString()) ?? []), string.Join(",", s["paths"]?.AsArray().Select(x => x?.ToString()) ?? [])]);
                Table(o, rows);
                return ExitOk;
            }
            case "check":
                return await Generic(IpcOps.RepoCheck, new JsonObject { ["repo_id"] = repo, ["wait"] = true }, o).ConfigureAwait(false);
            default:
                throw new UsageException("bf repo init|snapshots|check <repoId>");
        }
    }

    private const string RestoreUsage = """
        bf restore <jobId> --list                                   restore points (Versions folders / snapshots)
        bf restore <jobId> --browse [--from <point>] [--path <p>]   list a folder of a restore point
        bf restore <jobId> --from <point|current|snapshotId> --path <file-or-folder> --to <folder> [--wait]
        bf restore <jobId> ... --to-original --yes                  overwrite at the original place (backup of the overwritten files is kept)
        """;

    private static async Task<int> Restore(Args a, TextWriter o)
    {
        if (a.Has("--help")) { o.WriteLine(RestoreUsage); return ExitOk; }
        var job = a.Positional(1, "jobId");
        if (a.Has("--list"))
            return await Generic(IpcOps.RestoreVersions, new JsonObject { ["job_id"] = job }, o).ConfigureAwait(false);
        if (a.Has("--browse"))
            return await Generic(IpcOps.RestoreBrowse, new JsonObject { ["job_id"] = job, ["point"] = a.Value("--from") ?? "current", ["path"] = a.Value("--path") ?? "" }, o).ConfigureAwait(false);
        var args = new JsonObject
        {
            ["job_id"] = job,
            ["point"] = a.Value("--from") ?? throw new UsageException("--from is required"),
            ["path"] = a.Value("--path") ?? throw new UsageException("--path is required"),
            ["to"] = a.Value("--to"),
            ["to_original"] = a.Has("--to-original"),
            ["confirm"] = a.Has("--yes"),
            ["wait"] = a.Has("--wait"),
        };
        if (args["to"] is null && !a.Has("--to-original")) throw new UsageException("--to <folder> (or --to-original --yes) is required");
        var r = await Call(IpcOps.RestoreRun, args).ConfigureAwait(false);
        o.WriteLine(r?.ToJsonString(IpcProtocol.JsonIndented));
        return r?["result"]?.ToString() switch { null or "ok" => ExitOk, "warning" => ExitWarning, _ => ExitFailed };
    }

    private static async Task<int> Sql(Args a, TextWriter o)
    {
        switch (a.Positional(1, "discover|grant"))
        {
            case "discover":
            {
                var args = new JsonObject();
                if (a.PositionalOrNull(2) is { } inst) args["instance"] = inst;
                return await Generic(IpcOps.SqlDiscover, args, o).ConfigureAwait(false);
            }
            case "grant":
                return await SqlGrant(a, o).ConfigureAwait(false);
            default:
                throw new UsageException("bf sql discover|grant");
        }
    }

    /// <summary>
    /// SPEC 8.6: runs in bf.exe with the administrator's own Windows login (or --login sa + password on stdin) — the credential
    /// is never stored and never sent to the service. Shows the T-SQL; executes it only with --yes.
    /// </summary>
    private static async Task<int> SqlGrant(Args a, TextWriter o)
    {
        var jobId = a.Positional(2, "jobId");
        var cfgNode = await Call(IpcOps.ConfigGet).ConfigureAwait(false);
        var cfg = ConfigJson.Parse(cfgNode!["config"]!.ToJsonString());
        var job = cfg.FindJob(jobId) ?? throw new UsageException($"unknown job '{jobId}'");
        if (job.Sql is null) throw new UsageException($"'{jobId}' is not an SQL job");
        var login = a.Value("--login");
        var password = login is null ? null : Console.In.ReadLine();
        await using var session = await new Engine.Sql.SqlClientSessionFactory().OpenAsync(job.Sql.Instance, login, password, CancellationToken.None).ConfigureAwait(false);
        var dbs = Engine.Sql.SqlPlanning.Select(job.Sql, await session.ListDatabasesAsync(CancellationToken.None).ConfigureAwait(false)).Select(d => d.Name).ToList();
        var script = Engine.Sql.SqlPlanning.GrantScript(dbs, !string.IsNullOrEmpty(job.Sql.Checkdb));
        o.WriteLine(script);
        if (a.Has("--script")) return ExitOk;
        if (!a.Has("--yes")) { o.WriteLine("review the script above, then repeat with --yes to run it"); return ExitOk; }
        await session.ExecuteAsync(script, CancellationToken.None).ConfigureAwait(false);
        o.WriteLine($"permissions set for {Engine.Sql.SqlPlanning.ServiceLogin} on {dbs.Count} database(s)");
        return ExitOk;
    }

    /// <summary>CR-002 A1.: one VSS snapshot with a full step trace, in this process (needs an elevated prompt).</summary>
    private static int DiagVss(Args a, TextWriter o)
    {
        if (!OperatingSystem.IsWindows())
        {
            o.WriteLine("bf diag vss: Windows only");
            return ExitError;
        }
        var path = a.PositionalOrNull(2) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc");
        Windows.VssSnapshotProvider.InitializeComSecurity(); // first COM use in the process, exactly as the service does
        return new Windows.VssSnapshotProvider().Diagnose(path, withWriters: !a.Has("--no-writers"), o);
    }

    private static async Task<int> Report(Args a, TextWriter o)
    {
        if (a.Positional(1, "preview") != "preview") throw new UsageException("bf report preview [<file.html>]");
        var r = await Call(IpcOps.ReportPreview).ConfigureAwait(false);
        var html = r?["html"]?.ToString() ?? "";
        if (a.PositionalOrNull(2) is { } path) { await File.WriteAllTextAsync(path, html).ConfigureAwait(false); o.WriteLine($"written {path}"); }
        else o.WriteLine(html);
        return ExitOk;
    }

    // ------------------------------------------------------------ formatting

    private static string Short(JsonNode? iso)
    {
        if (iso is null) return "-";
        var s = iso.ToString();
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : s;
    }

    private static string Pct(JsonNode? p) => p is null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:0}%", p.GetValue<double>());

    private static string Gb(JsonNode? g) => g is null ? "?" : string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", g.GetValue<double>());

    private static void Table(TextWriter o, List<string[]> rows)
    {
        var widths = new int[rows[0].Length];
        foreach (var r in rows)
            for (var i = 0; i < r.Length - 1; i++) widths[i] = Math.Max(widths[i], r[i].Length);
        foreach (var r in rows)
            o.WriteLine(string.Join("  ", r.Select((c, i) => i < r.Length - 1 ? c.PadRight(widths[i]) : c)).TrimEnd());
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Args(string[] args)
    {
        private static readonly HashSet<string> ValueOptions = ["--last", "--lines", "--from", "--path", "--to", "--login"];

        public string Command => args[0];

        public bool Has(string flag) => args.Contains(flag, StringComparer.Ordinal);

        public string? Value(string option)
        {
            var i = Array.IndexOf(args, option);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public int Int(string option, int @default) =>
            Value(option) is { } v ? int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new UsageException($"{option} needs a number") : @default;

        private List<string> PositionalList()
        {
            var list = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    if (ValueOptions.Contains(args[i])) i++;
                    continue;
                }
                list.Add(args[i]);
            }
            return list;
        }

        public string? PositionalOrNull(int index)
        {
            var p = PositionalList();
            return index < p.Count ? p[index] : null;
        }

        public string Positional(int index, string name) => PositionalOrNull(index) ?? throw new UsageException($"missing <{name}>");
    }
}
