using BackupForge.Core.Config;

namespace BackupForge.Core.Scheduling;

/// <summary>
/// Computes due instants of a schedule (SPEC 4. "Ütemezés"). All times are local wall-clock times of
/// <paramref name="zone"/>. DST: a local time that does not exist (spring forward) runs at the first minute
/// after the change; an ambiguous local time (fall back) runs only once, at its first occurrence.
/// </summary>
public static class ScheduleCalculator
{
    private const int MaxDaysAhead = 800;
    private static readonly DateTime IntervalAnchor = new(2000, 1, 3, 0, 0, 0, DateTimeKind.Unspecified); // a Monday

    public static bool IsTimeBased(ScheduleConfig s) =>
        s.Kind is ScheduleKinds.Interval or ScheduleKinds.Daily or ScheduleKinds.Weekly or ScheduleKinds.Monthly;

    /// <summary>First due instant strictly after <paramref name="afterUtc"/>; null for chained / manual / invalid schedules.</summary>
    public static DateTimeOffset? Next(ScheduleConfig s, DateTimeOffset afterUtc, TimeZoneInfo zone)
    {
        if (!IsTimeBased(s)) return null;
        var startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime).AddDays(-1);
        for (var d = 0; d < MaxDaysAhead; d++)
        {
            DateTimeOffset? best = null;
            foreach (var c in CandidatesForDay(s, startDay.AddDays(d), zone))
                if (c > afterUtc && (best is null || c < best)) best = c;
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>Latest due instant at or before <paramref name="atOrBeforeUtc"/> (used by catch-up); null if none within ~2 years.</summary>
    public static DateTimeOffset? Previous(ScheduleConfig s, DateTimeOffset atOrBeforeUtc, TimeZoneInfo zone)
    {
        if (!IsTimeBased(s)) return null;
        var startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(atOrBeforeUtc, zone).DateTime).AddDays(1);
        for (var d = 0; d < MaxDaysAhead; d++)
        {
            DateTimeOffset? best = null;
            foreach (var c in CandidatesForDay(s, startDay.AddDays(-d), zone))
                if (c <= atOrBeforeUtc && (best is null || c > best)) best = c;
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>All due instants of one local day (unsorted, may contain duplicates after DST gaps).</summary>
    internal static IEnumerable<DateTimeOffset> CandidatesForDay(ScheduleConfig s, DateOnly day, TimeZoneInfo zone)
    {
        switch (s.Kind)
        {
            case ScheduleKinds.Daily:
                if (s.Days.Count > 0 && !s.Days.Any(x => TimeSpec.TryParseDay(x, out var dw) && dw == day.DayOfWeek)) yield break;
                foreach (var t in s.Times)
                    if (TimeSpec.TryParseHm(t, out var tod)) yield return LocalToUtc(day.ToDateTime(tod), zone);
                break;

            case ScheduleKinds.Weekly:
                if (TimeSpec.TryParseDay(s.Day, out var wd) && wd == day.DayOfWeek && TimeSpec.TryParseHm(s.Time, out var wt))
                    yield return LocalToUtc(day.ToDateTime(wt), zone);
                break;

            case ScheduleKinds.Monthly:
                if (!TimeSpec.TryParseHm(s.Time, out var mt) || s.DayOfMonth is null) yield break;
                var dom = string.Equals(s.DayOfMonth, "last", StringComparison.OrdinalIgnoreCase)
                    ? DateTime.DaysInMonth(day.Year, day.Month)
                    : int.TryParse(s.DayOfMonth, out var n) ? n : -1;
                if (day.Day == dom) yield return LocalToUtc(day.ToDateTime(mt), zone);
                break;

            case ScheduleKinds.Interval:
                if (s.EveryMin is not { } every || every < 1) yield break;
                var startMin = 0;
                if (s.Start is not null && TimeSpec.TryParseHm(s.Start, out var st)) startMin = st.Hour * 60 + st.Minute;
                // Wall-clock minute index since the anchor; occurrences where (index - start) % every == 0.
                var dayIndex = (long)(day.ToDateTime(TimeOnly.MinValue) - IntervalAnchor).TotalMinutes;
                var offset = Mod(startMin - dayIndex, every);
                for (var m = offset; m < 1440; m += every)
                {
                    var tod = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m));
                    if (s.Window is not null && !InWindow(tod, s.Window)) continue;
                    yield return LocalToUtc(day.ToDateTime(tod), zone);
                }
                break;
        }
    }

    private static long Mod(long a, long m) => ((a % m) + m) % m;

    /// <summary>Window [from, to); when from &gt; to the window spans midnight.</summary>
    public static bool InWindow(TimeOnly t, TimeWindow w)
    {
        if (!TimeSpec.TryParseHm(w.From, out var from) || !TimeSpec.TryParseHm(w.To, out var to)) return true;
        if (from == to) return true;
        return from < to ? t >= from && t < to : t >= from || t < to;
    }

    /// <summary>Local wall-clock time → instant, with the DST rules described on the class.</summary>
    public static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var t = local;
            while (zone.IsInvalidTime(t)) t = t.AddMinutes(1);
            // first valid minute after the gap, truncated to the minute
            t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);
            local = t;
        }
        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            offset = offsets.Max(); // the larger offset = the earlier instant (still on summer time)
        }
        else offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>expected_interval_h (SPEC 4.): how often a successful run is expected.</summary>
public static class ExpectedInterval
{
    public static double? FromSchedule(ScheduleConfig s)
    {
        switch (s.Kind)
        {
            case ScheduleKinds.Interval:
                return s.EveryMin is { } e ? e / 60.0 : null;
            case ScheduleKinds.Daily:
            {
                var mins = s.Times.Select(t => TimeSpec.TryParseHm(t, out var x) ? x.Hour * 60 + x.Minute : -1)
                    .Where(m => m >= 0).Distinct().Order().ToList();
                if (mins.Count == 0) return null;
                var dayGap = 24.0;
                if (s.Days.Count is > 0 and < 7)
                {
                    // the largest gap between run days (e.g. Mon–Fri → Fri→Mon = 3 days)
                    var days = s.Days.Select(d => TimeSpec.TryParseDay(d, out var dw) ? (int)dw : -1).Where(x => x >= 0).Distinct().Order().ToList();
                    if (days.Count == 0) return null;
                    var maxDays = 0;
                    for (var i = 0; i < days.Count; i++)
                    {
                        var next = i + 1 < days.Count ? days[i + 1] : days[0] + 7;
                        maxDays = Math.Max(maxDays, next - days[i]);
                    }
                    dayGap = 24.0 * maxDays;
                }
                var maxGap = 0;
                for (var i = 0; i + 1 < mins.Count; i++) maxGap = Math.Max(maxGap, mins[i + 1] - mins[i]);
                var wrap = dayGap * 60 - mins[^1] + mins[0];
                return Math.Max(maxGap, wrap) / 60.0;
            }
            case ScheduleKinds.Weekly:
                return 168;
            case ScheduleKinds.Monthly:
                return 744;
            default:
                return null;
        }
    }

    /// <summary>
    /// Effective value for a job: explicit expected_interval_h, else from the schedule; chained jobs inherit the
    /// largest value of the jobs that trigger them (recursively); manual jobs have none.
    /// </summary>
    public static double? For(BackupConfig config, JobConfig job) => For(config, job, []);

    private static double? For(BackupConfig config, JobConfig job, HashSet<string> visiting)
    {
        if (job.ExpectedIntervalH is { } explicitValue) return explicitValue;
        if (job.Schedule.Kind != ScheduleKinds.Chained) return FromSchedule(job.Schedule);
        if (!visiting.Add(job.Id)) return null;
        double? best = null;
        foreach (var parent in config.Jobs.Where(j => j.OnSuccess.Contains(job.Id, StringComparer.Ordinal)))
        {
            var v = For(config, parent, visiting);
            if (v is not null && (best is null || v > best)) best = v;
        }
        visiting.Remove(job.Id);
        return best;
    }
}
