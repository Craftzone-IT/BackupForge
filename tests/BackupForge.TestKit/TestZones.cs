namespace BackupForge.TestKit;

/// <summary>Deterministic time zones (identical on Linux and Windows, independent of the OS tz database).</summary>
public static class TestZones
{
    /// <summary>Central European Time with EU DST rules (last Sunday of March 02:00 → 03:00, last Sunday of October 03:00 → 02:00).</summary>
    public static TimeZoneInfo Cet { get; } = CreateCet();

    private static TimeZoneInfo CreateCet()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/CET", TimeSpan.FromHours(1), "Test CET", "Test CET", "Test CEST", [rule]);
    }

    /// <summary>Local CET wall time → UTC instant (for building test expectations).</summary>
    public static DateTimeOffset CetLocal(int y, int mo, int d, int h, int mi) =>
        new DateTimeOffset(new DateTime(y, mo, d, h, mi, 0), Cet.GetUtcOffset(new DateTime(y, mo, d, h, mi, 0))).ToUniversalTime();
}
