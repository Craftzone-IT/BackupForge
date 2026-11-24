using System.Globalization;
using BackupForge.Core.Config;
using BackupForge.Core.Resources;

namespace BackupForge.Core.Scheduling;

/// <summary>Human-readable schedule ("4 óránként 06:15-től", "naponta 12:30, 22:30") in the config language.</summary>
public static class ScheduleText
{
    public static string Describe(BackupConfig config, JobConfig job, CultureInfo? culture = null)
    {
        culture ??= Texts.CultureFor(config.Language);
        if (job.Schedule.Kind == ScheduleKinds.Chained)
        {
            var parents = config.Jobs.Where(j => j.OnSuccess.Contains(job.Id, StringComparer.Ordinal)).Select(j => j.Id).ToList();
            return parents.Count == 0
                ? Texts.Get("sched.chained_none", culture)
                : Texts.Format(culture, "sched.chained", string.Join(", ", parents));
        }
        return Describe(job.Schedule, culture);
    }

    public static string Describe(ScheduleConfig s, CultureInfo culture)
    {
        var hu = culture.TwoLetterISOLanguageName == "hu";
        switch (s.Kind)
        {
            case ScheduleKinds.Interval:
            {
                var every = s.EveryMin ?? 0;
                var text = every % 60 == 0
                    ? Texts.Format(culture, "sched.every_h", every / 60)
                    : Texts.Format(culture, "sched.every_min", every);
                if (s.Start is not null && TimeSpec.TryParseHm(s.Start, out var st))
                    text += Texts.Format(culture, "sched.from", s.Start, hu ? HuFromSuffix(st) : "");
                if (s.Window is not null) text += Texts.Format(culture, "sched.window", s.Window.From, s.Window.To);
                return text;
            }
            case ScheduleKinds.Daily:
            {
                var times = string.Join(", ", s.Times);
                if (s.Days.Count is 0 or 7) return Texts.Format(culture, "sched.daily", times);
                var names = Texts.Get("day.short", culture).Split(',');
                var days = string.Join(", ", s.Days.Select(d => TimeSpec.TryParseDay(d, out var dw) ? names[(int)dw] : d));
                return Texts.Format(culture, "sched.days", days, times);
            }
            case ScheduleKinds.Weekly:
            {
                var names = Texts.Get("day.long", culture).Split(',');
                var day = TimeSpec.TryParseDay(s.Day, out var dw) ? names[(int)dw] : s.Day;
                return Texts.Format(culture, "sched.weekly", day, s.Time);
            }
            case ScheduleKinds.Monthly:
                return string.Equals(s.DayOfMonth, "last", StringComparison.OrdinalIgnoreCase)
                    ? Texts.Format(culture, "sched.monthly_last", s.Time)
                    : Texts.Format(culture, "sched.monthly", s.DayOfMonth, s.Time);
            case ScheduleKinds.Manual:
                return Texts.Get("sched.manual", culture);
            default:
                return s.Kind;
        }
    }

    /// <summary>
    /// Hungarian "-tól/-től" after a clock time: vowel harmony of the last spoken number word
    /// (06:15 → "hat óra tizenöt" → -től; 06:00 → "hat óra" → -tól; 06:30 → "harminc" → -tól).
    /// </summary>
    public static string HuFromSuffix(TimeOnly t)
    {
        var n = t.Minute != 0 ? t.Minute : t.Hour;
        return BackVowel(n) ? "tól" : "től";
    }

    private static bool BackVowel(int n)
    {
        if (n == 0) return true; // nulla
        var units = n % 10;
        if (units != 0) return units is 3 or 6 or 8; // három, hat, nyolc
        return (n / 10) switch
        {
            1 => false, // tíz
            2 => true,  // húsz
            3 => true,  // harminc
            4 => false, // negyven
            5 => false, // ötven
            _ => false,
        };
    }
}
