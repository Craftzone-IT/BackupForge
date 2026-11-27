using BackupForge.Core.Abstractions;
using BackupForge.Core.Logging;
using BackupForge.TestKit;
using Microsoft.Extensions.Logging;

namespace BackupForge.Core.Tests.Logging;

public class SecretMaskerTests
{
    [Fact]
    public void Registered_secrets_are_masked()
    {
        var m = new SecretMasker();
        m.Register("test-password-123");
        Assert.Equal("login failed for *** on nas", m.Apply("login failed for test-password-123 on nas"));
    }

    [Fact]
    public void Longer_secret_masked_whole()
    {
        var m = new SecretMasker();
        m.Register("abcd");
        m.Register("abcdefgh");
        Assert.Equal("x *** y", m.Apply("x abcdefgh y"));
    }

    [Fact]
    public void Very_short_values_are_ignored()
    {
        var m = new SecretMasker();
        m.Register("ab");
        Assert.Equal("about", m.Apply("about"));
    }

    [Theory]
    [InlineData("sftp://user:hunter2@host/x", "sftp://user:***@host/x")]
    [InlineData("password=hunter2;user=x", "password=***;user=x")]
    [InlineData("RESTIC_PASSWORD set, Password: \"a b c\" done", "RESTIC_PASSWORD set, Password: *** done")]
    [InlineData("token: abc123", "token: ***")]
    public void Patterns_masked(string input, string expected) => Assert.Equal(expected, new SecretMasker().Apply(input));

    [Fact]
    public void Plain_text_untouched() => Assert.Equal("312 másolva, 0 törölve", new SecretMasker().Apply("312 másolva, 0 törölve"));
}

public class FileLoggerTests
{
    [Fact]
    public void Writes_masked_lines_and_rotates_daily_with_retention()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var masker = new SecretMasker();
        masker.Register("test-password-123");
        // a stale file from long ago and an unrelated file
        File.WriteAllText(dir.Sub("service-20260901.log"), "old");
        File.WriteAllText(dir.Sub("service-keep.log"), "not ours");
        File.WriteAllText(dir.Sub("other-20260901.log"), "other prefix");

        using (var p = new FileLoggerProvider(dir.Path, "service", 30, clock, masker))
        {
            var log = p.CreateLogger("BackupForge.Service.Worker");
            log.LogInformation("connect with test-password-123");
            log.LogDebug("hidden");
            clock.Advance(TimeSpan.FromDays(1));
            log.LogWarning("second day");
        }

        var day1 = File.ReadAllText(dir.Sub("service-20261101.log"));
        Assert.Contains("INF Worker: connect with ***", day1, StringComparison.Ordinal);
        Assert.DoesNotContain("test-password-123", day1, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", day1, StringComparison.Ordinal);
        Assert.Contains("WRN Worker: second day", File.ReadAllText(dir.Sub("service-20261102.log")), StringComparison.Ordinal);
        Assert.False(File.Exists(dir.Sub("service-20260901.log")));
        Assert.True(File.Exists(dir.Sub("service-keep.log")));
        Assert.True(File.Exists(dir.Sub("other-20260901.log")));
    }

    [Fact]
    public void Log_file_starts_with_one_bom_also_after_a_restart()
    {
        // CR-002 A7.: PS 5.1 Get-Content reads the file correctly only with a BOM; appending must not add a second one
        using var dir = new TempDir();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        for (var i = 0; i < 2; i++)
            using (var p = new FileLoggerProvider(dir.Path, "service", 30, clock, new SecretMasker()))
                p.CreateLogger("BackupForge.Service.Worker").LogInformation("indulás — próba");
        var bytes = File.ReadAllBytes(dir.Sub("service-20261101.log"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(1, CountBoms(bytes));
        Assert.Equal(2, System.Text.Encoding.UTF8.GetString(bytes).Split("indulás — próba").Length - 1);
    }

    public static int CountBoms(byte[] b)
    {
        var n = 0;
        for (var i = 0; i + 2 < b.Length; i++) if (b[i] == 0xEF && b[i + 1] == 0xBB && b[i + 2] == 0xBF) n++;
        return n;
    }
}
