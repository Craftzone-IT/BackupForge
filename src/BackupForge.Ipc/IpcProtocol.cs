using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BackupForge.Ipc;

/// <summary>Named pipe protocol shared by the service, the GUI and bf.exe (SPEC 11.1).</summary>
public static class IpcProtocol
{
    /// <summary>Bump on incompatible changes; both sides send it and the service rejects a mismatch.</summary>
    public const int Version = 1;
    public const string DefaultPipeName = "BackupForge";
    public const string PipeEnvVar = "BACKUPFORGE_PIPE";
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    public static string PipeName => Environment.GetEnvironmentVariable(PipeEnvVar) is { Length: > 0 } p ? p : DefaultPipeName;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };
}

public static class IpcOps
{
    public const string Hello = "hello";
    public const string StatusGet = "status.get";
    public const string ConfigGet = "config.get";
    public const string ConfigValidate = "config.validate";
    public const string ConfigSave = "config.save";
    public const string ConfigReload = "config.reload";
    public const string SecretsSet = "secrets.set";
    public const string SecretsList = "secrets.list";
    public const string JobRun = "job.run";
    public const string JobCancel = "job.cancel";
    public const string JobDryRun = "job.dryrun";
    public const string JobApproveDelete = "job.approve_mass_delete";
    public const string HistoryList = "history.list";
    public const string HistoryGet = "history.get";
    public const string LogTail = "log.tail";
    public const string EventsSubscribe = "events.subscribe";
    public const string NotifyTest = "notify.test";
    public const string TargetTest = "target.test";
    public const string RepoInit = "repo.init";
    public const string RepoSnapshots = "repo.snapshots";
    public const string RepoLs = "repo.ls";
    public const string RepoCheck = "repo.check";
    public const string RestoreRun = "restore.run";
    public const string RestoreVersions = "restore.versions";
    public const string RestoreBrowse = "restore.browse";
    public const string SqlDiscover = "sql.discover";
    public const string SqlGrant = "sql.grant";
    public const string SqlGrantScript = "sql.grant_script";
    public const string ReportPreview = "report.preview";
}

public static class IpcErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnknownOp = "unknown_op";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string NotFound = "not_found";
    public const string Rejected = "rejected";
    public const string Invalid = "invalid";
    public const string Internal = "internal";
}

public sealed class IpcRequest
{
    public int V { get; set; } = IpcProtocol.Version;
    public long Id { get; set; }
    public string Op { get; set; } = "";
    public JsonObject? Args { get; set; }

    public string? Str(string name) => Args?[name]?.GetValueKind() == JsonValueKind.String ? Args[name]!.GetValue<string>() : null;
    public bool Bool(string name) => Args?[name]?.GetValueKind() is JsonValueKind.True;
    public long? Long(string name) => Args?[name]?.GetValueKind() == JsonValueKind.Number ? Args[name]!.GetValue<long>() : null;

    public string Required(string name) =>
        Str(name) is { Length: > 0 } s ? s : throw new IpcException(IpcErrorCodes.BadRequest, $"missing argument '{name}'");
}

public sealed class IpcError
{
    public string Code { get; set; } = IpcErrorCodes.Internal;
    public string Message { get; set; } = "";
    /// <summary>Optional structured details (e.g. validation errors with field paths).</summary>
    public JsonNode? Details { get; set; }
}

public sealed class IpcResponse
{
    public long Id { get; set; }
    public bool Ok { get; set; }
    public JsonNode? Result { get; set; }
    public IpcError? Error { get; set; }
}

/// <summary>Server push (progress, state changes). Distinguished from responses by the "event" property.</summary>
public sealed class IpcEvent
{
    public string Event { get; set; } = "";
    public JsonNode? Data { get; set; }
}

public sealed class IpcException(string code, string message, JsonNode? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public JsonNode? Details { get; } = details;
}

/// <summary>The service is not reachable (not running, or no permission: the pipe is for SYSTEM + Administrators only).</summary>
public sealed class IpcUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
