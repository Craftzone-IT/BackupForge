using BackupForge.Core.Config;
using BackupForge.Core.Scheduling;
using BackupForge.TestKit;
using static BackupForge.TestKit.TestZones;

namespace BackupForge.Core.Tests.Scheduling;

public class ScheduleCalculatorTests
{
    private static DateTimeOffset? Next(ScheduleConfig s, DateTimeOffset after) => ScheduleCalculator.Next(s, after, Cet);

    private static ScheduleConfig Daily(params string[] times) => new() { Kind = ScheduleKinds.Daily, Times = [.. times] };

    [Fact]
    public void Daily_picks_next_time_today_then_tomorrow()
    {
        var s = Daily("12:30", "22:30");
        Assert.Equal(CetLocal(2026, 11, 24, 12, 30), Next(s, CetLocal(2026, 11, 24, 8, 0)));
        Assert.Equal(CetLocal(2026, 11, 24, 22, 30), Next(s, CetLocal(2026, 11, 24, 12, 30))); // strictly after
        Assert.Equal(CetLocal(2026, 11, 25, 12, 30), Next(s, CetLocal(2026, 11, 24, 23, 0)));
    }

    [Fact]
    public void Daily_with_days_filter()
    {
        var s = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["20:00"], Days = ["Mon", "Fri"] };
        // 2026-11-24 is a Tuesday → next is Friday 27th
        Assert.Equal(CetLocal(2026, 11, 27, 20, 0), Next(s, CetLocal(2026, 11, 24, 8, 0)));
        Assert.Equal(CetLocal(2026, 11, 30, 20, 0), Next(s, CetLocal(2026, 11, 27, 20, 0)));
    }

    [Fact]
    public void Weekly_and_monthly()
    {
        var w = new ScheduleConfig { Kind = ScheduleKinds.Weekly, Day = "Sun", Time = "03:00" };
        Assert.Equal(CetLocal(2026, 11, 29, 3, 0), Next(w, CetLocal(2026, 11, 24, 8, 0)));
        var m = new ScheduleConfig { Kind = ScheduleKinds.Monthly, DayOfMonth = "1", Time = "04:00" };
        Assert.Equal(CetLocal(2026, 12, 1, 4, 0), Next(m, CetLocal(2026, 11, 24, 8, 0)));
        var last = new ScheduleConfig { Kind = ScheduleKinds.Monthly, DayOfMonth = "last", Time = "23:00" };
        Assert.Equal(CetLocal(2026, 11, 30, 23, 0), Next(last, CetLocal(2026, 11, 24, 8, 0)));
        Assert.Equal(CetLocal(2027, 2, 28, 23, 0), Next(last, CetLocal(2027, 1, 31, 23, 0)));
    }

    [Fact]
    public void Interval_from_start_time()
    {
        var s = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 240, Start = "06:15" };
        Assert.Equal(CetLocal(2026, 11, 24, 10, 15), Next(s, CetLocal(2026, 11, 24, 8, 0)));
        Assert.Equal(CetLocal(2026, 11, 25, 2, 15), Next(s, CetLocal(2026, 11, 24, 22, 15))); // continuous series
    }

    [Fact]
    public void Interval_window_respected_including_overnight()
    {
        var s = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 60, Start = "00:00", Window = new TimeWindow { From = "06:00", To = "22:00" } };
        Assert.Equal(CetLocal(2026, 11, 25, 6, 0), Next(s, CetLocal(2026, 11, 24, 21, 0)));
        var night = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 120, Start = "00:00", Window = new TimeWindow { From = "22:00", To = "04:00" } };
        Assert.Equal(CetLocal(2026, 11, 24, 22, 0), Next(night, CetLocal(2026, 11, 24, 8, 0)));
        Assert.Equal(CetLocal(2026, 11, 25, 0, 0), Next(night, CetLocal(2026, 11, 24, 22, 0)));
        Assert.Equal(CetLocal(2026, 11, 25, 22, 0), Next(night, CetLocal(2026, 11, 25, 2, 0)));
    }

    [Fact]
    public void Chained_and_manual_have_no_next()
    {
        Assert.Null(Next(new ScheduleConfig { Kind = ScheduleKinds.Chained }, CetLocal(2026, 1, 1, 0, 0)));
        Assert.Null(Next(new ScheduleConfig { Kind = ScheduleKinds.Manual }, CetLocal(2026, 1, 1, 0, 0)));
    }

    // --- DST: 2026-03-29 02:00 CET → 03:00 CEST (02:xx does not exist)
    [Fact]
    public void Spring_forward_missing_time_runs_at_first_minute_after_change()
    {
        var s = Daily("02:30");
        var before = CetLocal(2026, 3, 29, 1, 0);
        var next = Next(s, before);
        // 03:00 CEST = 01:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), next);
        // and the day after it is back to 02:30 CEST
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), Next(s, next!.Value));
    }

    [Fact]
    public void Spring_forward_two_missing_times_run_once()
    {
        var s = Daily("02:15", "02:45");
        var first = Next(s, CetLocal(2026, 3, 29, 0, 0))!.Value;
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), first);
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 15, 0, TimeSpan.Zero), Next(s, first));
    }

    // --- DST: 2026-10-25 03:00 CEST → 02:00 CET (02:xx happens twice)
    [Fact]
    public void Fall_back_repeated_hour_runs_only_once()
    {
        var s = Daily("02:30");
        var first = Next(s, new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero))!.Value;
        // first occurrence: 02:30 CEST = 00:30 UTC
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), first);
        // not again at 02:30 CET (01:30 UTC), but the next day
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 1, 30, 0, TimeSpan.Zero), Next(s, first));
    }

    [Fact]
    public void Interval_across_fall_back_does_not_duplicate()
    {
        var s = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 60, Start = "00:00" };
        var t = new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero); // 00:30 CEST
        var seen = new List<DateTimeOffset>();
        for (var i = 0; i < 6; i++) { t = Next(s, t)!.Value; seen.Add(t); }
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second > p.First));
    }

    [Fact]
    public void Interval_across_spring_forward_keeps_running()
    {
        var s = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 30, Start = "00:00" };
        var t = CetLocal(2026, 3, 29, 1, 0);
        var list = new List<DateTimeOffset>();
        for (var i = 0; i < 4; i++) { t = Next(s, t)!.Value; list.Add(t); }
        // 01:30 CET, (02:00, 02:30 missing → 03:00 CEST once), 03:30 CEST, 04:00 CEST
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), list[0]);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), list[1]);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), list[2]);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero), list[3]);
    }

    [Fact]
    public void Previous_finds_last_due_for_catch_up()
    {
        var s = Daily("12:30", "22:30");
        Assert.Equal(CetLocal(2026, 11, 23, 22, 30), ScheduleCalculator.Previous(s, CetLocal(2026, 11, 24, 8, 0), Cet));
        Assert.Equal(CetLocal(2026, 11, 24, 12, 30), ScheduleCalculator.Previous(s, CetLocal(2026, 11, 24, 12, 30), Cet));
        var w = new ScheduleConfig { Kind = ScheduleKinds.Weekly, Day = "Sun", Time = "03:00" };
        Assert.Equal(CetLocal(2026, 11, 22, 3, 0), ScheduleCalculator.Previous(w, CetLocal(2026, 11, 24, 8, 0), Cet));
    }
}

public class ExpectedIntervalTests
{
    [Fact]
    public void From_schedule()
    {
        Assert.Equal(4, ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 240 }));
        Assert.Equal(14, ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["12:30", "22:30"] }));
        Assert.Equal(24, ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["22:00"] }));
        Assert.Equal(72, ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["22:00"], Days = ["Mon", "Tue", "Wed", "Thu", "Fri"] }));
        Assert.Equal(168, ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Weekly, Day = "Sun", Time = "01:00" }));
        Assert.Equal(744, ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Monthly, DayOfMonth = "1", Time = "01:00" }));
        Assert.Null(ExpectedInterval.FromSchedule(new ScheduleConfig { Kind = ScheduleKinds.Manual }));
    }

    [Fact]
    public void Chained_inherits_largest_parent_and_explicit_wins()
    {
        var c = Config.ConfigFixtures.Example();
        Assert.Equal(14, ExpectedInterval.For(c, c.Jobs[2]));
        c.Jobs[0].OnSuccess = ["sql-to-offsite"]; // company-mirror: explicit 4
        Assert.Equal(14, ExpectedInterval.For(c, c.Jobs[2]));
        c.Jobs[2].ExpectedIntervalH = 30;
        Assert.Equal(30, ExpectedInterval.For(c, c.Jobs[2]));
    }

    [Fact]
    public void Manual_job_has_none_and_cycles_do_not_hang()
    {
        var c = Config.ConfigFixtures.Minimal();
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained };
        c.Jobs[0].OnSuccess = ["docs"];
        Assert.Null(ExpectedInterval.For(c, c.Jobs[0]));
    }
}
