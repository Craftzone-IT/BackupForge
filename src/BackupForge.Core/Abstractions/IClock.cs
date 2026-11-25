namespace BackupForge.Core.Abstractions;

/// <summary>Time source. All scheduling works on UTC instants plus the configured local time zone.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeZoneInfo LocalZone { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo LocalZone => TimeZoneInfo.Local;
}

/// <summary>Manually advanced clock for tests.</summary>
public sealed class ManualClock(DateTimeOffset utcNow, TimeZoneInfo? zone = null) : IClock
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = utcNow.ToUniversalTime();

    public DateTimeOffset UtcNow { get { lock (_lock) return _now; } }
    public TimeZoneInfo LocalZone { get; } = zone ?? TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) { lock (_lock) _now += by; }
    public void Set(DateTimeOffset utc) { lock (_lock) _now = utc.ToUniversalTime(); }
}

public static class ClockExtensions
{
    /// <summary>The instant expressed in the clock's local zone (with offset), e.g. for status.json.</summary>
    public static DateTimeOffset ToLocal(this IClock clock, DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, clock.LocalZone);

    public static DateTimeOffset LocalNow(this IClock clock) => clock.ToLocal(clock.UtcNow);
}
