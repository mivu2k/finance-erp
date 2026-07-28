using Hr.Domain;
using Hr.Infrastructure.Attendance;
using Xunit;

namespace Hr.Tests;

/// <summary>
/// The arithmetic that decides whether someone was present, late or absent — and
/// therefore what they get paid. Worth pinning down.
/// </summary>
public class AttendanceCalculatorTests
{

    private static Shift Standard() => new()
    {
        Id = 1,
        Name = "General",
        StartsAt = new TimeOnly(9, 0),
        EndsAt = new TimeOnly(17, 0),
        GraceMinutes = 15,
        HalfDayMinutes = 240,
        MinimumMinutes = 60,
        OvertimeAfterMinutes = 30,
        WeeklyOffMask = 1 << (int)DayOfWeek.Sunday
    };

    // Monday.
    private static readonly DateOnly Workday = new(2026, 7, 27);

    private static DayContext Context(
        Shift? shift = null, bool holiday = false, LeaveRequest? leave = null,
        DateOnly? date = null) =>
        new(date ?? Workday, shift ?? Standard(), holiday, leave);

    private static DateTime At(int hour, int minute, DateOnly? date = null) =>
        (date ?? Workday).ToDateTime(new TimeOnly(hour, minute));

    [Fact]
    public void First_punch_is_the_arrival_and_last_is_the_departure()
    {
        // Four reads in a day — in, lunch out, lunch back, home — is the normal
        // pattern on these terminals.
        var day = AttendanceCalculator.Build(1, Context(),
            [At(9, 0), At(13, 2), At(13, 45), At(17, 30)]);

        Assert.Equal(new TimeOnly(9, 0), day.FirstIn);
        Assert.Equal(new TimeOnly(17, 30), day.LastOut);
        Assert.Equal(4, day.PunchCount);
    }

    [Fact]
    public void On_time_arrival_is_present()
    {
        var day = AttendanceCalculator.Build(1, Context(), [At(8, 55), At(17, 5)]);

        Assert.Equal(AttendanceStatus.Present, day.Status);
        Assert.Equal(0, day.LateMinutes);
        Assert.Equal(490, day.WorkedMinutes);
    }

    [Fact]
    public void Arrival_within_grace_is_still_present()
    {
        var day = AttendanceCalculator.Build(1, Context(), [At(9, 14), At(17, 30)]);

        Assert.Equal(AttendanceStatus.Present, day.Status);
        Assert.Equal(0, day.LateMinutes);
    }

    [Fact]
    public void Late_minutes_run_from_the_shift_start_not_the_end_of_grace()
    {
        // Arriving 09:25 with a 15-minute grace is 25 minutes late, not 10.
        var day = AttendanceCalculator.Build(1, Context(), [At(9, 25), At(17, 30)]);

        Assert.Equal(AttendanceStatus.Late, day.Status);
        Assert.Equal(25, day.LateMinutes);
    }

    [Fact]
    public void Short_day_is_a_half_day()
    {
        var day = AttendanceCalculator.Build(1, Context(), [At(9, 0), At(12, 0)]);

        Assert.Equal(AttendanceStatus.HalfDay, day.Status);
        Assert.Equal(180, day.WorkedMinutes);
    }

    [Fact]
    public void Very_short_day_counts_as_absent()
    {
        // Below the minimum: someone who badged in and left again.
        var day = AttendanceCalculator.Build(1, Context(), [At(9, 0), At(9, 30)]);

        Assert.Equal(AttendanceStatus.Absent, day.Status);
    }

    [Fact]
    public void No_punches_is_absent()
    {
        var day = AttendanceCalculator.Build(1, Context(), []);

        Assert.Equal(AttendanceStatus.Absent, day.Status);
        Assert.Null(day.FirstIn);
        Assert.Equal(0, day.WorkedMinutes);
    }

    [Fact]
    public void A_single_punch_is_incomplete_and_needs_a_human()
    {
        // The departure is genuinely unknown; guessing it would be inventing pay.
        var day = AttendanceCalculator.Build(1, Context(), [At(9, 2)]);

        Assert.Equal(AttendanceStatus.Incomplete, day.Status);
        Assert.Equal(new TimeOnly(9, 2), day.FirstIn);
        Assert.Equal(0, day.WorkedMinutes);
    }

    [Fact]
    public void Break_minutes_come_off_the_worked_total()
    {
        var shift = Standard();
        shift.BreakMinutes = 60;

        var day = AttendanceCalculator.Build(1, Context(shift), [At(9, 0), At(17, 0)]);

        Assert.Equal(420, day.WorkedMinutes);
    }

    [Fact]
    public void Overtime_accrues_only_past_the_threshold()
    {
        var justUnder = AttendanceCalculator.Build(1, Context(), [At(9, 0), At(17, 25)]);
        Assert.Equal(0, justUnder.OvertimeMinutes);

        var over = AttendanceCalculator.Build(1, Context(), [At(9, 0), At(18, 0)]);
        Assert.Equal(60, over.OvertimeMinutes);
    }

    [Fact]
    public void Leaving_early_is_recorded()
    {
        var day = AttendanceCalculator.Build(1, Context(), [At(9, 0), At(16, 30)]);

        Assert.Equal(30, day.EarlyLeaveMinutes);
    }

    [Fact]
    public void Sunday_is_a_weekly_off()
    {
        var sunday = new DateOnly(2026, 7, 26);

        var day = AttendanceCalculator.Build(1, Context(date: sunday), []);

        Assert.Equal(AttendanceStatus.WeeklyOff, day.Status);
        Assert.Equal(AttendanceSource.WeeklyOff, day.Source);
    }

    [Fact]
    public void Work_on_a_weekly_off_is_all_overtime()
    {
        var sunday = new DateOnly(2026, 7, 26);

        var day = AttendanceCalculator.Build(1, Context(date: sunday),
            [At(10, 0, sunday), At(14, 0, sunday)]);

        Assert.Equal(AttendanceStatus.WeeklyOff, day.Status);
        Assert.Equal(240, day.OvertimeMinutes);
    }

    [Fact]
    public void Holiday_outranks_a_normal_working_day()
    {
        var day = AttendanceCalculator.Build(1, Context(holiday: true), []);

        Assert.Equal(AttendanceStatus.Holiday, day.Status);
    }

    [Fact]
    public void Approved_leave_wins_even_when_the_terminal_saw_them()
    {
        // Coming in to hand over a laptop doesn't cancel the leave.
        var leave = new LeaveRequest { Id = 9, Status = LeaveStatus.Approved };

        var day = AttendanceCalculator.Build(1, Context(leave: leave), [At(11, 0), At(11, 20)]);

        Assert.Equal(AttendanceStatus.OnLeave, day.Status);
        Assert.Equal(AttendanceSource.Leave, day.Source);
        Assert.Equal(9, day.LeaveRequestId);
    }

    [Fact]
    public void Pending_leave_does_not_excuse_an_absence()
    {
        var leave = new LeaveRequest { Id = 9, Status = LeaveStatus.Pending };

        var day = AttendanceCalculator.Build(1, Context(leave: leave), []);

        Assert.Equal(AttendanceStatus.Absent, day.Status);
    }

    [Fact]
    public void Working_days_skip_weekly_offs_and_holidays()
    {
        // Mon 27 Jul to Sun 2 Aug: 6 weekdays, one of which is a holiday.
        var holidays = new HashSet<DateOnly> { new(2026, 7, 29) };

        var days = AttendanceCalculator.WorkingDays(
            new DateOnly(2026, 7, 27), new DateOnly(2026, 8, 2),
            Standard(), holidays, halfDay: false);

        Assert.Equal(5m, days);
    }

    [Fact]
    public void A_half_day_request_costs_half_a_day_less()
    {
        var days = AttendanceCalculator.WorkingDays(
            new DateOnly(2026, 7, 27), new DateOnly(2026, 7, 27),
            Standard(), new HashSet<DateOnly>(), halfDay: true);

        Assert.Equal(0.5m, days);
    }

    [Fact]
    public void A_range_of_only_offs_consumes_nothing()
    {
        var sunday = new DateOnly(2026, 7, 26);

        var days = AttendanceCalculator.WorkingDays(
            sunday, sunday, Standard(), new HashSet<DateOnly>(), halfDay: false);

        Assert.Equal(0m, days);
    }
}
