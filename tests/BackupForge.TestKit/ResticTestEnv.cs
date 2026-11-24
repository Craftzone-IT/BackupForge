namespace BackupForge.TestKit;

/// <summary>
/// Finds a real restic binary for integration tests: BACKUPFORGE_RESTIC, else third_party/restic/restic(.exe)
/// fetched by tools/fetch_restic.py. With BF_REQUIRE_RESTIC=1 (CI) a missing binary fails instead of skipping.
/// </summary>
public static class ResticTestEnv
{
    public static string? Binary { get; } = Find();

    private static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("BACKUPFORGE_RESTIC");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "BackupForge.slnx"))) d = d.Parent;
        if (d is null) return null;
        var p = Path.Combine(d.FullName, "third_party", "restic", OperatingSystem.IsWindows() ? "restic.exe" : "restic");
        return File.Exists(p) ? p : null;
    }
}

public sealed class ResticFactAttribute : FactAttribute
{
    public ResticFactAttribute()
    {
        if (ResticTestEnv.Binary is null && Environment.GetEnvironmentVariable("BF_REQUIRE_RESTIC") != "1")
            Skip = "no restic binary (set BACKUPFORGE_RESTIC or run tools/fetch_restic.py)";
    }
}
