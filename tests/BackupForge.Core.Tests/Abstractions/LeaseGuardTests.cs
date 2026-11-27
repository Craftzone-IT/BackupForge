using BackupForge.Core.Abstractions;
using BackupForge.TestKit;

namespace BackupForge.Core.Tests.Abstractions;

/// <summary>CR-003 2.: no UNC access to a credentialed SMB server outside a lease.</summary>
public class LeaseGuardTests
{
    private sealed class NullFs : IFileSystem
    {
        public int Calls;
        public char Separator => '\\';
        public bool IsCaseSensitive => false;
        public bool FileExists(string path) { Calls++; return false; }
        public bool DirectoryExists(string path) { Calls++; return true; }
        public FsEntry? GetEntry(string path) { Calls++; return null; }
        public IEnumerable<FsEntry> Enumerate(string directory) { Calls++; return []; }
        public void CreateDirectory(string path) => Calls++;
        public void DeleteFile(string path) => Calls++;
        public void DeleteDirectory(string path, bool recursive) => Calls++;
        public void MoveFile(string source, string destination, bool overwrite) => Calls++;
        public void MoveDirectory(string source, string destination) => Calls++;
        public Stream OpenRead(string path) { Calls++; return Stream.Null; }
        public Stream Create(string path) { Calls++; return Stream.Null; }
        public void Flush(Stream stream) { }
        public void SetTimes(string path, DateTime creationUtc, DateTime lastWriteUtc) => Calls++;
        public void SetAttributes(string path, FileAttributes attributes) => Calls++;
        public (long Free, long Total)? GetSpace(string path) { Calls++; return null; }
    }

    private static (LeaseGuardFileSystem Guard, NullFs Inner, HashSet<string> Leased) Make(bool @throw)
    {
        var inner = new NullFs();
        var leased = new HashSet<string>();
        var g = new LeaseGuardFileSystem(inner) { NeedsLease = s => s == "nas01", HasLease = leased.Contains, ThrowOnViolation = @throw };
        return (g, inner, leased);
    }

    [Theory]
    [InlineData(@"\\NAS01\backup\x", "nas01")]
    [InlineData(@"\\?\UNC\nas01\backup\x", "nas01")]
    [InlineData("//nas01/backup", "nas01")]
    [InlineData(@"\\?\C:\data", null)]
    [InlineData(@"C:\data", null)]
    [InlineData("/home/u", null)]
    public void Server_of_a_path(string path, string? server) => Assert.Equal(server, LeaseGuardFileSystem.ServerOf(path));

    [Fact]
    public void Unc_without_lease_throws_in_tests_and_passes_inside_a_lease()
    {
        var (g, inner, leased) = Make(@throw: true);
        var ex = Assert.Throws<InvalidOperationException>(() => g.DirectoryExists(@"\\nas01\backup\Company_Versions"));
        Assert.Contains("without a connection lease", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);                       // the access did not happen
        leased.Add("nas01");
        Assert.True(g.DirectoryExists(@"\\nas01\backup\Company_Versions"));
        g.Enumerate(@"\\NAS01\backup");
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Local_paths_and_servers_without_credentials_are_never_checked()
    {
        var (g, inner, _) = Make(@throw: true);
        g.DirectoryExists(@"D:\Backup");
        g.DirectoryExists("/tmp/x");
        g.DirectoryExists(@"\\fs02\public\x");              // no own user name: the service account's access is intended
        g.MoveFile(@"D:\a", @"D:\b", true);
        Assert.Equal(4, inner.Calls);
        Assert.Equal(0, g.Violations);
    }

    [Fact]
    public void Production_warns_once_per_server_per_hour()
    {
        var (g, inner, _) = Make(@throw: false);
        var warnings = new List<string>();
        var now = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        g.Warn = warnings.Add;
        g.UtcNow = () => now;
        g.DirectoryExists(@"\\nas01\backup\a");
        g.GetSpace(@"\\nas01\backup");
        now = now.AddMinutes(59);
        g.Enumerate(@"\\nas01\backup");
        Assert.Single(warnings);
        now = now.AddMinutes(2);
        g.Enumerate(@"\\nas01\backup");
        Assert.Equal(2, warnings.Count);
        Assert.Equal(4, g.Violations);
        Assert.Equal(4, inner.Calls);                       // production does not break the access, it reports it
    }
}
