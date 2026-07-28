using Hr.Domain;

namespace Hr.Infrastructure.Attendance;

/// <summary>What the calculator needs to know about a day before it can judge it.</summary>
public record DayContext(
    DateOnly Date,
    Shift Shift,
    bool IsHoliday,
    LeaveRequest? Leave);

/// <summary>
/// Turns a day's raw punches into a judged attendance record. Kept pure — no
/// database, no clock — because this is the arithmetic that decides someone's pay,
/// and it needs to be testable without a terminal on the desk.
/// </summary>
public static class AttendanceCalculator
{
    /// <summary>
    /// First punch of the day is the arrival, last is the departure. Everything in
    /// between is ignored: staff punch several times a day on these terminals (in,
    /// lunch, back, out) and there is no reliable in/out flag on the record, so
    /// bracketing the day is the only defensible reading.
    /// </summary>
    public static AttendanceDay Build(
        int employeeId, DayContext context, IReadOnlyList<DateTime> punches)
    {
        var day = new AttendanceDay
        {
            EmployeeId = employeeId,
            Date = context.Date,
            PunchCount = punches.Count
        };

        var ordered = punches.OrderBy(p => p).ToList();
        if (ordered.Count > 0)
        {
            day.FirstIn = TimeOnly.FromDateTime(ordered[0]);
            day.LastOut = TimeOnly.FromDateTime(ordered[^1]);
        }

        // Approved leave outranks whatever the terminal saw: someone who came in to
        // hand over a laptop on their leave day is still on leave.
        if (context.Leave is { Status: LeaveStatus.Approved })
        {
            day.Status = AttendanceStatus.OnLeave;
            day.Source = AttendanceSource.Leave;
            day.LeaveRequestId = context.Leave.Id;
            return day;
        }

        if (context.IsHoliday)
        {
            day.Status = AttendanceStatus.Holiday;
            day.Source = AttendanceSource.Holiday;
            return Overtime(day, context, ordered);
        }

        if (context.Shift.IsWeeklyOff(context.Date.DayOfWeek))
        {
            day.Status = AttendanceStatus.WeeklyOff;
            day.Source = AttendanceSource.WeeklyOff;
            return Overtime(day, context, ordered);
        }

        day.Source = AttendanceSource.Device;

        if (ordered.Count == 0)
        {
            day.Status = AttendanceStatus.Absent;
            return day;
        }

        // A single read means they came in and never punched out. That's a real and
        // common failure, and it needs a human rather than a guessed departure time.
        if (ordered.Count == 1)
        {
            day.Status = AttendanceStatus.Incomplete;
            day.LateMinutes = LateBy(day.FirstIn!.Value, context.Shift);
            return day;
        }

        var worked = (int)(ordered[^1] - ordered[0]).TotalMinutes - context.Shift.BreakMinutes;
        day.WorkedMinutes = Math.Max(0, worked);

        day.LateMinutes = LateBy(day.FirstIn!.Value, context.Shift);
        day.EarlyLeaveMinutes = EarlyBy(day.LastOut!.Value, context.Shift);

        var shiftEnd = context.Shift.EndsAt;
        var overtimeStart = shiftEnd.AddMinutes(context.Shift.OvertimeAfterMinutes);
        if (day.LastOut > overtimeStart)
            day.OvertimeMinutes = (int)(day.LastOut.Value - shiftEnd).TotalMinutes;

        day.Status = day.WorkedMinutes < context.Shift.MinimumMinutes
            ? AttendanceStatus.Absent
            : day.WorkedMinutes < context.Shift.HalfDayMinutes
                ? AttendanceStatus.HalfDay
                : day.LateMinutes > 0
                    ? AttendanceStatus.Late
                    : AttendanceStatus.Present;

        return day;
    }

    /// <summary>Time worked on a holiday or weekly off is all overtime.</summary>
    private static AttendanceDay Overtime(
        AttendanceDay day, DayContext context, List<DateTime> ordered)
    {
        if (ordered.Count < 2) return day;

        var worked = (int)(ordered[^1] - ordered[0]).TotalMinutes - context.Shift.BreakMinutes;
        day.WorkedMinutes = Math.Max(0, worked);
        day.OvertimeMinutes = day.WorkedMinutes;
        return day;
    }

    private static int LateBy(TimeOnly arrival, Shift shift)
    {
        var allowed = shift.StartsAt.AddMinutes(shift.GraceMinutes);
        return arrival <= allowed ? 0 : (int)(arrival - shift.StartsAt).TotalMinutes;
    }

    private static int EarlyBy(TimeOnly departure, Shift shift) =>
        departure >= shift.EndsAt ? 0 : (int)(shift.EndsAt - departure).TotalMinutes;

    /// <summary>
    /// Working days in a range, excluding weekly offs and holidays — how a leave
    /// request's day count is worked out.
    /// </summary>
    public static decimal WorkingDays(
        DateOnly from, DateOnly to, Shift shift, IReadOnlySet<DateOnly> holidays, bool halfDay)
    {
        if (to < from) return 0;

        var days = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (shift.IsWeeklyOff(d.DayOfWeek)) continue;
            if (holidays.Contains(d)) continue;
            days++;
        }

        if (days == 0) return 0;
        return halfDay ? days - 0.5m : days;
    }
}
