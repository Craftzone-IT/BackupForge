using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Resources;

namespace BackupForge.Service;

/// <summary>
/// Called by the installer (SPEC 12.), elevated:
///   BackupForge.Service.exe --setup-install [--config &lt;file&gt;] [--secrets &lt;file&gt;] [--log &lt;file&gt;]
///   BackupForge.Service.exe --setup-uninstall [--purge]
/// Exit code 0 = ok, 1 = error (message on stdout / in the log).
/// </summary>
public static class SetupHelper
{
    /// <summary>
    /// CR-001 4b.: the installer always works on %ProgramData%\BackupForge — a stray BACKUPFORGE_DATA (developer override)
    /// must not point --setup-uninstall --purge at some other folder.
    /// </summary>
    public static DataPaths InstallPaths() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupForge"));

    /// <summary>A folder is only purged when it is recognisably a BackupForge data folder.</summary>
    public static bool LooksLikeDataFolder(DataPaths paths) => File.Exists(paths.ConfigFile) || File.Exists(paths.HistoryDb);

    public static int Run(string[] args, DataPaths paths, TextWriter output)
    {
        string? Arg(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null; }
        var logFile = Arg("--log");
        var log = new StringBuilder();
        void Log(string m) { log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}"); output.WriteLine(m); }
        try
        {
            return args[0] == "--setup-uninstall" ? Uninstall(paths, args.Contains("--purge"), Log) : Install(paths, Arg("--config"), Arg("--secrets"), Log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("ERROR: " + ex.Message);
            return 1;
        }
        finally
        {
            if (logFile is not null) try { File.AppendAllText(logFile, log.ToString()); } catch (IOException) { }
        }
    }

    private static int Install(DataPaths paths, string? configFile, string? secretsFile, Action<string> log)
    {
        paths.EnsureCreated();
        if (OperatingSystem.IsWindows()) RestrictProgramData(paths.Root, log);

        ISecretStore secrets = OperatingSystem.IsWindows() ? new Windows.DpapiSecretStore(paths.SecretsFile) : new InMemorySecretStore();
        if (secretsFile is not null)
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(secretsFile)) ?? [];
            foreach (var (k, v) in map) secrets.Set(k, v);
            log($"{map.Count} secret(s) imported into secrets.dat");
            log($"WARNING: delete {secretsFile} now — it contains passwords in plain text (the installer does not delete it)");
        }

        if (configFile is not null)
        {
            BackupConfig cfg;
            try { cfg = ConfigJson.Load(configFile); }
            catch (ConfigFormatException ex) { log("config: " + ex.Message); return 1; }
            var v = ConfigValidator.Validate(cfg, secrets, paths.Root);
            if (!v.IsValid)
            {
                foreach (var e in v.Errors) log($"config: {e.Path}: {e.Message(Texts.CultureFor(cfg.Language))}");
                return 1;
            }
            if (File.Exists(paths.ConfigFile)) File.Copy(paths.ConfigFile, paths.ConfigFile + ".bak.preinstall", overwrite: true);
            AtomicFile.Write(paths.ConfigFile, ConfigJson.ToUtf8(cfg));
            log($"configuration installed ({cfg.Jobs.Count} jobs)");
        }
        else if (File.Exists(paths.ConfigFile))
        {
            MigrateIfNeeded(paths, log);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (!EventLog.SourceExists(Windows.EventLogSink.Source)) EventLog.CreateEventSource(Windows.EventLogSink.Source, Windows.EventLogSink.LogName);
                log("event log source registered");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or ArgumentException) { log("event log source: " + ex.Message); }
        }
        return 0;
    }

    /// <summary>Upgrade path (SPEC 12.): a config of an older schema is migrated and config.json.bak.premigrate kept. Schema 1 is current.</summary>
    private static void MigrateIfNeeded(DataPaths paths, Action<string> log)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConfigFile));
        var schema = doc.RootElement.TryGetProperty("schema", out var s) && s.TryGetInt32(out var n) ? n : 0;
        if (schema == BackupConfig.CurrentSchema) { log("existing configuration kept (schema 1)"); return; }
        File.Copy(paths.ConfigFile, paths.ConfigFile + ".bak.premigrate", overwrite: true);
        log($"configuration schema {schema} is not supported by this version — kept as config.json.bak.premigrate, please check the configuration");
    }

    private static int Uninstall(DataPaths paths, bool purge, Action<string> log)
    {
        if (OperatingSystem.IsWindows())
        {
            // orphaned VSS snapshots of the service (SPEC 12.)
            if (File.Exists(paths.HistoryDb))
            {
                try
                {
                    var h = new Core.History.HistoryStore(paths.HistoryDb);
                    var ids = h.UndeletedVssSnapshots();
                    var deleted = new Windows.VssSnapshotProvider().DeleteOrphans(ids);
                    log($"orphaned VSS snapshots removed: {deleted.Count}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { log("VSS cleanup: " + ex.Message); }
            }
            try
            {
                if (EventLog.SourceExists(Windows.EventLogSink.Source)) EventLog.DeleteEventSource(Windows.EventLogSink.Source);
                log("event log source removed");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or ArgumentException) { log("event log source: " + ex.Message); }
        }
        if (purge && Directory.Exists(paths.Root) && !LooksLikeDataFolder(paths))
            log($"{paths.Root} kept: no config.json / history.db in it, not a BackupForge data folder");
        else if (purge && Directory.Exists(paths.Root))
        {
            // only our own data folder — never backup targets, Versions folders or restic repositories
            Directory.Delete(paths.Root, recursive: true);
            log($"{paths.Root} removed (/PURGE)");
        }
        else log($"{paths.Root} kept (settings and history)");
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictProgramData(string root, Action<string> log)
    {
        var di = new DirectoryInfo(root);
        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        di.SetAccessControl(sec);
        log($"{root}: ACL = SYSTEM + Administrators (no inheritance)");
    }
}
