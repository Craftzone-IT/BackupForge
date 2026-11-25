using System.Security.Cryptography;

namespace BackupForge.Engine.Restic;

/// <summary>
/// The bundled restic.exe (SPEC 7.1): pinned version + SHA-256 from third_party/restic/VERSION (embedded at build).
/// BACKUPFORGE_RESTIC overrides the path (development / tests); such a binary is reported as "unverified".
/// </summary>
public sealed class ResticBinary
{
    public const string OverrideEnvVar = "BACKUPFORGE_RESTIC";

    public ResticBinary(string path, string? expectedSha256)
    {
        Path = path;
        ExpectedSha256 = expectedSha256;
    }

    public string Path { get; }
    public string? ExpectedSha256 { get; }
    public static string PinnedVersion => Pins.GetValueOrDefault("version", "?");

    private static readonly Dictionary<string, string> Pins = LoadPins();

    private static Dictionary<string, string> LoadPins()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using var s = typeof(ResticBinary).Assembly.GetManifestResourceStream("restic.VERSION");
        if (s is null) return d;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var i = line.IndexOf('=');
            if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return d;
    }

    /// <summary>restic.exe next to the service, or the override.</summary>
    public static ResticBinary Default()
    {
        var env = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return new ResticBinary(env, null);
        var exe = OperatingSystem.IsWindows() ? "restic.exe" : "restic";
        return new ResticBinary(System.IO.Path.Combine(AppContext.BaseDirectory, exe), Pins.GetValueOrDefault("exe_sha256"));
    }

    private string? _verified;

    /// <summary>Null if fine, else a reason. The hash is computed once per file timestamp.</summary>
    public string? Verify()
    {
        if (!File.Exists(Path)) return $"not found: {Path}";
        if (ExpectedSha256 is null) return null; // override → unverified, the caller logs it
        var stamp = File.GetLastWriteTimeUtc(Path).Ticks + ":" + new FileInfo(Path).Length;
        if (_verified == stamp) return null;
        using var fs = File.OpenRead(Path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(fs));
        if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase)) return $"SHA-256 {hash[..12]}… ≠ pinned {ExpectedSha256[..12]}…";
        _verified = stamp;
        return null;
    }
}
