using System.Globalization;
using BackupForge.Core.Config;

namespace BackupForge.Core.Scheduling;

/// <summary>Parsers for the small time languages used in config.json.</summary>
public static class TimeSpec
{
    public static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static bool TryParseHm(string? s, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(s) || s.Length != 5 || s[2] != ':') return false;
        return TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    public static bool TryParseDay(string? s, out DayOfWeek day)
    {
        day = default;
        if (s is null) return false;
        var i = Array.FindIndex(DayNames, d => string.Equals(d, s, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        day = (DayOfWeek)i;
        return true;
    }

    /// <summary>
    /// Parses a recurrence string used for maintenance / reports:
    /// "daily HH:mm", "weekly Sun HH:mm", "Sun HH:mm" (= weekly), "monthly 1 HH:mm" / "monthly last HH:mm".
    /// </summary>
    public static bool TryParseRecurrence(string? s, out ScheduleConfig schedule)
    {
        schedule = new ScheduleConfig();
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryParseDay(parts[0], out _)) parts = ["weekly", .. parts];
        switch (parts[0].ToLowerInvariant())
        {
            case "daily" when parts.Length == 2 && TryParseHm(parts[1], out _):
                schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = [parts[1]] };
                return true;
            case "weekly" when parts.Length == 3 && TryParseDay(parts[1], out var d) && TryParseHm(parts[2], out _):
                schedule = new ScheduleConfig { Kind = ScheduleKinds.Weekly, Day = DayNames[(int)d], Time = parts[2] };
                return true;
            case "monthly" when parts.Length == 3 && IsValidDayOfMonth(parts[1]) && TryParseHm(parts[2], out _):
                schedule = new ScheduleConfig { Kind = ScheduleKinds.Monthly, DayOfMonth = parts[1].ToLowerInvariant(), Time = parts[2] };
                return true;
            default:
                return false;
        }
    }

    public static bool IsValidDayOfMonth(string? s) =>
        string.Equals(s, "last", StringComparison.OrdinalIgnoreCase) ||
        (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 28);

    /// <summary>"5%" → 5. Accepts 0.1..100.</summary>
    public static bool TryParsePercent(string? s, out double pct)
    {
        pct = 0;
        if (s is null || !s.EndsWith('%')) return false;
        return double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out pct) && pct is > 0 and <= 100;
    }
}
