using Hr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hr.Infrastructure.Attendance;

/// <summary>
/// Derives <see cref="AttendanceDay"/> records from raw punches.
/// </summary>
/// <remarks>
/// Punches are evidence and are never edited; the day is the judgement and is
/// rebuilt from them. Kept separate from whatever recorded the punch so the rules
/// — first punch in, last punch out, approved leave outranking both — live in one
/// place regardless of how someone clocked in.
/// </remarks>
public interface IAttendanceSyncService
{
    /// <summary>
    /// Recomputes the derived day records for a date range from stored punches.
    /// Manually corrected days are left alone.
    /// </summary>
    Task<int> RebuildAsync(DateOnly from, DateOnly to, int? employeeId = null,
        CancellationToken ct = default);
}

public class AttendanceSyncService(HrDbContext db) : IAttendanceSyncService
{
    public async Task<int> RebuildAsync(
        DateOnly from, DateOnly to, int? employeeId = null, CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);

        var defaultShift = await DefaultShiftAsync(ct);
        var shifts = await db.Shifts.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s, ct);

        var employees = await db.Employees
            .Where(e => employeeId == null || e.Id == employeeId)
            .Select(e => new { e.Id, e.ShiftId, e.JoinedOn, e.LeftOn })
            .ToListAsync(ct);

        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var punches = await db.AttendancePunches
            .Where(p => p.PunchedAt >= start && p.PunchedAt < end
                        && (employeeId == null || p.EmployeeId == employeeId))
            .Select(p => new { p.EmployeeId, p.PunchedAt })
            .ToListAsync(ct);

        var punchesByDay = punches
            .GroupBy(p => (p.EmployeeId, Date: DateOnly.FromDateTime(p.PunchedAt)))
            .ToDictionary(g => g.Key, g => g.Select(p => p.PunchedAt).ToList());

        var holidays = (await db.Holidays
            .Where(h => h.Date >= from && h.Date <= to)
            .Select(h => h.Date).ToListAsync(ct)).ToHashSet();

        var leave = await db.LeaveRequests
            .Where(l => l.Status == LeaveStatus.Approved && l.FromDate <= to && l.ToDate >= from
                        && (employeeId == null || l.EmployeeId == employeeId))
            .ToListAsync(ct);

        var existing = await db.AttendanceDays
            .Where(d => d.Date >= from && d.Date <= to
                        && (employeeId == null || d.EmployeeId == employeeId))
            .ToListAsync(ct);

        var existingByKey = existing.ToDictionary(d => (d.EmployeeId, d.Date));
        var rebuilt = 0;

        foreach (var employee in employees)
        {
            var shift = employee.ShiftId is { } id && shifts.TryGetValue(id, out var s)
                ? s : defaultShift;

            for (var date = from; date <= to; date = date.AddDays(1))
            {
                // Don't invent days outside someone's employment.
                if (date < employee.JoinedOn) continue;
                if (employee.LeftOn is { } left && date > left) continue;

                existingByKey.TryGetValue((employee.Id, date), out var current);

                // A day a person corrected by hand is theirs; the device doesn't
                // get to overwrite it on the next sync.
                if (current is { Source: AttendanceSource.Manual }) continue;

                var dayPunches = punchesByDay.GetValueOrDefault((employee.Id, date), []);
                var onLeave = leave.FirstOrDefault(l =>
                    l.EmployeeId == employee.Id && l.FromDate <= date && l.ToDate >= date);

                var computed = AttendanceCalculator.Build(
                    employee.Id,
                    new DayContext(date, shift, holidays.Contains(date), onLeave),
                    dayPunches);

                if (current is null)
                {
                    db.AttendanceDays.Add(computed);
                }
                else
                {
                    current.FirstIn = computed.FirstIn;
                    current.LastOut = computed.LastOut;
                    current.PunchCount = computed.PunchCount;
                    current.Status = computed.Status;
                    current.Source = computed.Source;
                    current.WorkedMinutes = computed.WorkedMinutes;
                    current.LateMinutes = computed.LateMinutes;
                    current.EarlyLeaveMinutes = computed.EarlyLeaveMinutes;
                    current.OvertimeMinutes = computed.OvertimeMinutes;
                    current.LeaveRequestId = computed.LeaveRequestId;
                }

                rebuilt++;
            }
        }

        await db.SaveChangesAsync(ct);
        return rebuilt;
    }

    private async Task<Shift> DefaultShiftAsync(CancellationToken ct) =>
        await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.IsDefault, ct)
        ?? await db.Shifts.AsNoTracking().FirstOrDefaultAsync(ct)
        ?? new Shift { Name = "Default" };

}
