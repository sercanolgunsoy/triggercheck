using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Maintenance;

namespace TriggerCheck.Tests.Maintenance;

/// <summary>FEAT-3: pure schedule logic, including DST transitions (Europe/Berlin) and a zone without DST (Europe/Istanbul).</summary>
public class MaintenanceScheduleTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static MaintenanceWindow Weekly(string tz, WeekDays days, int startMinutes, int durationMinutes) => new()
    {
        IsRecurring = true, TimeZoneId = tz, RecurrenceDays = days, RecurrenceStartMinutes = startMinutes, DurationMinutes = durationMinutes
    };

    [Fact]
    public void One_off_window_is_active_between_start_inclusive_and_end_exclusive()
    {
        var w = new MaintenanceWindow { StartsAtUtc = Utc(2026, 5, 1, 10), EndsAtUtc = Utc(2026, 5, 1, 12) };
        Assert.Null(MaintenanceSchedule.ActiveAt(w, Utc(2026, 5, 1, 9, 59)));
        Assert.NotNull(MaintenanceSchedule.ActiveAt(w, Utc(2026, 5, 1, 10)));
        Assert.NotNull(MaintenanceSchedule.ActiveAt(w, Utc(2026, 5, 1, 11, 59)));
        Assert.Null(MaintenanceSchedule.ActiveAt(w, Utc(2026, 5, 1, 12)));
        Assert.Equal(Utc(2026, 5, 1, 10), MaintenanceSchedule.CurrentOrNext(w, Utc(2026, 4, 30, 0))!.Value.StartUtc);
        Assert.Null(MaintenanceSchedule.CurrentOrNext(w, Utc(2026, 5, 1, 12)));
    }

    [Fact]
    public void Weekly_window_in_Berlin_follows_daylight_saving_time()
    {
        // Sundays 03:00 local for 2 hours. CET (+1) until 2026-03-29, CEST (+2) until 2026-10-25.
        var w = Weekly("Europe/Berlin", WeekDays.Sunday, 3 * 60, 120);
        var starts = MaintenanceSchedule.Occurrences(w, Utc(2026, 3, 20, 0), Utc(2026, 4, 6, 0)).Select(o => o.StartUtc).ToList();
        Assert.Equal([Utc(2026, 3, 22, 2), Utc(2026, 3, 29, 1), Utc(2026, 4, 5, 1)], starts);

        var autumn = MaintenanceSchedule.Occurrences(w, Utc(2026, 10, 17, 0), Utc(2026, 11, 2, 0)).Select(o => o.StartUtc).ToList();
        Assert.Equal([Utc(2026, 10, 18, 1), Utc(2026, 10, 25, 2), Utc(2026, 11, 1, 2)], autumn);

        // Active at local 04:30 on the DST day (02:30 UTC), not at 03:00 UTC (= 05:00 local, the end).
        Assert.NotNull(MaintenanceSchedule.ActiveAt(w, Utc(2026, 3, 29, 2, 30)));
        Assert.Null(MaintenanceSchedule.ActiveAt(w, Utc(2026, 3, 29, 3)));
    }

    [Fact]
    public void Start_time_inside_the_spring_forward_gap_is_shifted_forward_and_ambiguous_time_uses_the_first_occurrence()
    {
        // 02:30 does not exist on 2026-03-29 in Berlin (02:00 -> 03:00): it becomes 03:30 CEST = 01:30 UTC.
        var w = Weekly("Europe/Berlin", WeekDays.Sunday, 2 * 60 + 30, 30);
        var spring = MaintenanceSchedule.Occurrences(w, Utc(2026, 3, 29, 0), Utc(2026, 3, 30, 0)).Single();
        Assert.Equal(Utc(2026, 3, 29, 1, 30), spring.StartUtc);
        Assert.Equal(Utc(2026, 3, 29, 2), spring.EndUtc);

        // 02:30 happens twice on 2026-10-25: the first (CEST, +2) = 00:30 UTC.
        var autumn = MaintenanceSchedule.Occurrences(w, Utc(2026, 10, 25, 0), Utc(2026, 10, 26, 0)).Single();
        Assert.Equal(Utc(2026, 10, 25, 0, 30), autumn.StartUtc);
    }

    [Fact]
    public void Weekly_window_in_Istanbul_has_the_same_UTC_time_all_year()
    {
        // Mondays 01:00 Istanbul (+3, no DST) = Sunday 22:00 UTC, in winter and in summer.
        var w = Weekly("Europe/Istanbul", WeekDays.Monday, 60, 60);
        var winter = MaintenanceSchedule.Occurrences(w, Utc(2026, 1, 1, 0), Utc(2026, 1, 8, 0)).Single();
        var summer = MaintenanceSchedule.Occurrences(w, Utc(2026, 7, 1, 0), Utc(2026, 7, 8, 0)).Single();
        Assert.Equal(Utc(2026, 1, 4, 22), winter.StartUtc);
        Assert.Equal(Utc(2026, 7, 5, 22), summer.StartUtc);
        Assert.Equal(DayOfWeek.Sunday, winter.StartUtc.DayOfWeek);
    }

    [Fact]
    public void Occurrence_running_past_midnight_and_multiple_days_are_found()
    {
        // Tuesday and Friday 23:00 UTC for 3 hours.
        var w = Weekly("UTC", WeekDays.Tuesday | WeekDays.Friday, 23 * 60, 180);
        Assert.NotNull(MaintenanceSchedule.ActiveAt(w, Utc(2026, 6, 3, 1)));   // Wednesday 01:00 (from Tuesday)
        Assert.NotNull(MaintenanceSchedule.ActiveAt(w, Utc(2026, 6, 6, 1, 59))); // Saturday 01:59 (from Friday)
        Assert.Null(MaintenanceSchedule.ActiveAt(w, Utc(2026, 6, 6, 2)));
        Assert.Null(MaintenanceSchedule.ActiveAt(w, Utc(2026, 6, 4, 1)));       // Thursday
        Assert.Equal(Utc(2026, 6, 5, 23), MaintenanceSchedule.CurrentOrNext(w, Utc(2026, 6, 4, 0))!.Value.StartUtc);
    }

    [Fact]
    public void Invalid_or_empty_recurrence_never_matches()
    {
        Assert.Empty(MaintenanceSchedule.Occurrences(Weekly("Mars/Olympus", WeekDays.Monday, 0, 60), Utc(2026, 1, 1, 0), Utc(2026, 2, 1, 0)));
        Assert.Empty(MaintenanceSchedule.Occurrences(Weekly("UTC", WeekDays.None, 0, 60), Utc(2026, 1, 1, 0), Utc(2026, 2, 1, 0)));
        Assert.Empty(MaintenanceSchedule.Occurrences(new MaintenanceWindow(), Utc(2026, 1, 1, 0), Utc(2026, 2, 1, 0)));
    }
}
