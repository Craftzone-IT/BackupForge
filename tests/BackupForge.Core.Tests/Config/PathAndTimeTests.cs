using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Scheduling;

namespace BackupForge.Core.Tests.Config;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"D:\Data\", @"D:\Data")]
    [InlineData(@"D:/Data//Sub", @"D:\Data\Sub")]
    [InlineData(@"\\nas\share\\dir\", @"\\nas\share\dir")]
    [InlineData(@"D:", @"D:\")]
    [InlineData(@"D:\", @"D:\")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, PathUtil.Normalize(input));

    [Theory]
    [InlineData(@"D:\Data\Sub", @"D:\Data", true)]
    [InlineData(@"D:\Data", @"d:\data\", true)]
    [InlineData(@"D:\DataX", @"D:\Data", false)]
    [InlineData(@"D:\Data", @"D:\", true)]
    [InlineData(@"\\nas\share\a", @"\\nas\share", true)]
    [InlineData(@"\\nas\share2", @"\\nas\share", false)]
    public void IsSameOrUnder(string inner, string outer, bool expected) => Assert.Equal(expected, PathUtil.IsSameOrUnder(inner, outer));

    [Fact]
    public void Combine_keeps_separator_style()
    {
        Assert.Equal(@"\\nas\b\Company", PathUtil.Combine(@"\\nas\b", "Company"));
        Assert.Equal(@"X:\Mentes\a\b", PathUtil.Combine(@"X:\Mentes\", "a/b"));
        Assert.Equal("/tmp/x/y", PathUtil.Combine("/tmp/x", "y"));
    }

    [Fact]
    public void Versions_path_next_to_mirror()
    {
        var t = new TargetConfig { Path = @"\\192.168.1.10\backup\srv01" };
        var jt = new JobTargetRef { Subdir = "Company" };
        Assert.Equal(@"\\192.168.1.10\backup\srv01\Company", JobPaths.MirrorPath(t, jt));
        Assert.Equal(@"\\192.168.1.10\backup\srv01\Company_Versions", JobPaths.VersionsPath(t, jt, new VersioningOptions()));
        Assert.Equal(@"\\192.168.1.10\backup\srv01\V", JobPaths.VersionsPath(t, jt, new VersioningOptions { Folder = "V" }));
        Assert.Equal(@"E:\V", JobPaths.VersionsPath(t, jt, new VersioningOptions { Folder = @"E:\V" }));
        Assert.Null(JobPaths.VersionsPath(t, new JobTargetRef(), new VersioningOptions()));
    }
}

public class TimeSpecTests
{
    [Theory]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("6:15", false)]
    [InlineData("06:60", false)]
    [InlineData(null, false)]
    public void Hm(string? s, bool ok) => Assert.Equal(ok, TimeSpec.TryParseHm(s, out _));

    [Theory]
    [InlineData("weekly Sun 03:00", "weekly", "Sun", "03:00", null)]
    [InlineData("Sun 06:30", "weekly", "Sun", "06:30", null)]
    [InlineData("daily 02:00", "daily", null, null, null)]
    [InlineData("monthly last 04:00", "monthly", null, "04:00", "last")]
    [InlineData("monthly 1 04:00", "monthly", null, "04:00", "1")]
    public void Recurrence_parses(string s, string kind, string? day, string? time, string? dom)
    {
        Assert.True(TimeSpec.TryParseRecurrence(s, out var sc));
        Assert.Equal(kind, sc.Kind);
        Assert.Equal(day, sc.Day);
        if (kind != "daily") Assert.Equal(time, sc.Time);
        Assert.Equal(dom, sc.DayOfMonth);
    }

    [Theory]
    [InlineData("weekly Sunday 03:00")]
    [InlineData("monthly 31 01:00")]
    [InlineData("hourly")]
    [InlineData("")]
    public void Recurrence_rejects(string s) => Assert.False(TimeSpec.TryParseRecurrence(s, out _));

    [Theory]
    [InlineData("5%", 5.0)]
    [InlineData("0.5%", 0.5)]
    public void Percent(string s, double v)
    {
        Assert.True(TimeSpec.TryParsePercent(s, out var p));
        Assert.Equal(v, p);
    }
}
