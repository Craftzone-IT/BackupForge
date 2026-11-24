namespace BackupForge.TestKit;

public static class TestPaths
{
    public static string Schema(string name) => Path.Combine(AppContext.BaseDirectory, "schema", name);
    public static string Example(string name) => Path.Combine(AppContext.BaseDirectory, "schema", "examples", name);
}

/// <summary>A temp directory deleted on dispose (tests only write inside their own temp folder).</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bf-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
