using Hr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hr.Infrastructure.Attendance;

/// <summary>One employee's month, as the report grid renders it.</summary>
public record MonthlySummary(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? Department,
    IReadOnlyDictionary<int, AttendanceDay> Days,
    int Present,
    int Late,
    int HalfDays,
    int Absent,
    int OnLeave,
    int Incomplete,
    int WorkedMinutes,
    int OvertimeMinutes,
    int LateMinutes)
{
    /// <summary>Days that count towards pay — what payroll pro-rates against.</summary>
    public decimal PayableDays => Present + Late + OnLeave + HalfDays * 0.5m;
}

public record ManualDayInput(
    int EmployeeId,
    DateOnly Date,
    TimeOnly? FirstIn,
    TimeOnly? LastOut,
    AttendanceStatus Status,
    string Reason,
    string? Notes);

public interface IAttendanceService
{
    Task<List<AttendanceDay>> GetDayRegisterAsync(DateOnly date, int? departmentId = null,
        CancellationToken ct = default);
    Task<List<AttendanceDay>> GetForEmployeeAsync(int employeeId, DateOnly from, DateOnly to,
        CancellationToken ct = default);
    Task<List<AttendancePunch>> GetPunchesAsync(int employeeId, DateOnly date,
        CancellationToken ct = default);
    Task<List<MonthlySummary>> GetMonthlyAsync(int year, int month, int? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records or corrects a day by hand — the path used when a terminal misses a
    /// read. The day is stamped as manual so the next sync won't overwrite it.
    /// </summary>
    Task<AttendanceDay> SaveManualAsync(ManualDayInput input, string actorId, string actorName,
        CancellationToken ct = default);

    /// <summary>Drops a manual override so the day reverts to what the device says.</summary>
    Task RevertToDeviceAsync(int attendanceDayId, CancellationToken ct = default);

    /// <summary>Punches the terminals recorded against a device id we can't match to anyone.</summary>
    Task<List<(string DeviceUserId, int Count, DateTime LastSeen)>> GetUnmatchedAsync(
        CancellationToken ct = default);
    /// <summary>Assigns orphaned punches to an employee and rebuilds their days.</summary>
    Task<int> AssignUnmatchedAsync(string deviceUserId, int employeeId, CancellationToken ct = default);
}

public class AttendanceService(HrDbContext db, IAttendanceSyncService sync) : IAttendanceService
{
    public Task<List<AttendanceDay>> GetDayRegisterAsync(
        DateOnly date, int? departmentId = null, CancellationToken ct = default) =>
        db.AttendanceDays
            .Include(d => d.Employee).ThenInclude(e => e.Department)
            .Include(d => d.LeaveRequest).ThenInclude(l => l!.LeaveType)
            .Where(d => d.Date == date
                        && (departmentId == null || d.Employee.DepartmentId == departmentId))
            .OrderBy(d => d.Employee.FullName)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<List<AttendanceDay>> GetForEmployeeAsync(
        int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        db.AttendanceDays
            .Include(d => d.LeaveRequest).ThenInclude(l => l!.LeaveType)
            .Where(d => d.EmployeeId == employeeId && d.Date >= from && d.Date <= to)
            .OrderBy(d => d.Date)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<List<AttendancePunch>> GetPunchesAsync(
        int employeeId, DateOnly date, CancellationToken ct = default)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = date.AddDays(1).ToDateTime(TimeOnly.MinValue);

        return db.AttendancePunches
            .Include(p => p.BiometricDevice)
            .Where(p => p.EmployeeId == employeeId && p.PunchedAt >= start && p.PunchedAt < end)
            .OrderBy(p => p.PunchedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<List<MonthlySummary>> GetMonthlyAsync(
        int year, int month, int? departmentId = null, CancellationToken ct = default)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);

        var employees = await db.Employees
            .Include(e => e.Department)
            .Where(e => (departmentId == null || e.DepartmentId == departmentId)
                        && e.JoinedOn <= to
                        && (e.LeftOn == null || e.LeftOn >= from))
            .OrderBy(e => e.FullName)
            .AsNoTracking()
            .ToListAsync(ct);

        var ids = employees.Select(e => e.Id).ToList();

        var days = await db.AttendanceDays
            .Where(d => ids.Contains(d.EmployeeId) && d.Date >= from && d.Date <= to)
            .AsNoTracking()
            .ToListAsync(ct);

        var byEmployee = days.GroupBy(d => d.EmployeeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return employees.Select(e =>
        {
            var mine = byEmployee.GetValueOrDefault(e.Id, []);
            int CountOf(AttendanceStatus s) => mine.Count(d => d.Status == s);

            return new MonthlySummary(
                e.Id, e.EmployeeCode, e.FullName, e.Department?.Name,
                mine.ToDictionary(d => d.Date.Day, d => d),
                CountOf(AttendanceStatus.Present),
                CountOf(AttendanceStatus.Late),
                CountOf(AttendanceStatus.HalfDay),
                CountOf(AttendanceStatus.Absent),
                CountOf(AttendanceStatus.OnLeave),
                CountOf(AttendanceStatus.Incomplete),
                mine.Sum(d => d.WorkedMinutes),
                mine.Sum(d => d.OvertimeMinutes),
                mine.Sum(d => d.LateMinutes));
        }).ToList();
    }

    public async Task<AttendanceDay> SaveManualAsync(
        ManualDayInput input, string actorId, string actorName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new InvalidOperationException(
                "Give a reason for the correction — it goes on the record.");

        if (input.FirstIn is { } inAt && input.LastOut is { } outAt && outAt < inAt)
            throw new InvalidOperationException("Check-out can't be before check-in.");

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == input.EmployeeId, ct)
                       ?? throw new InvalidOperationException("Employee not found.");

        if (input.Date < employee.JoinedOn)
            throw new InvalidOperationException(
                $"{employee.FullName} had not joined on {input.Date:yyyy-MM-dd}.");
        if (input.Date > DateOnly.FromDateTime(DateTime.Today))
            throw new InvalidOperationException("Attendance can't be recorded for a future date.");

        var day = await db.AttendanceDays
            .FirstOrDefaultAsync(d => d.EmployeeId == input.EmployeeId && d.Date == input.Date, ct);

        if (day is null)
        {
            day = new AttendanceDay { EmployeeId = input.EmployeeId, Date = input.Date };
            db.AttendanceDays.Add(day);
        }

        day.FirstIn = input.FirstIn;
        day.LastOut = input.LastOut;
        day.Status = input.Status;
        day.Notes = input.Notes;

        // Marking it manual is what protects the correction from the next sync.
        day.Source = AttendanceSource.Manual;
        day.OverriddenById = actorId;
        day.OverriddenByName = actorName;
        day.OverriddenAtUtc = DateTime.UtcNow;
        day.OverrideReason = input.Reason;

        var shift = await ShiftForAsync(employee, ct);
        Recompute(day, shift);

        await db.SaveChangesAsync(ct);
        return day;
    }

    /// <summary>Re-derives the minute figures from hand-entered times.</summary>
    private static void Recompute(AttendanceDay day, Shift shift)
    {
        if (day.FirstIn is not { } inAt || day.LastOut is not { } outAt)
        {
            day.WorkedMinutes = 0;
            day.LateMinutes = 0;
            day.EarlyLeaveMinutes = 0;
            day.OvertimeMinutes = 0;
            return;
        }

        day.WorkedMinutes = Math.Max(0, (int)(outAt - inAt).TotalMinutes - shift.BreakMinutes);

        var allowed = shift.StartsAt.AddMinutes(shift.GraceMinutes);
        day.LateMinutes = inAt <= allowed ? 0 : (int)(inAt - shift.StartsAt).TotalMinutes;
        day.EarlyLeaveMinutes = outAt >= shift.EndsAt ? 0 : (int)(shift.EndsAt - outAt).TotalMinutes;

        var overtimeStart = shift.EndsAt.AddMinutes(shift.OvertimeAfterMinutes);
        day.OvertimeMinutes = outAt > overtimeStart ? (int)(outAt - shift.EndsAt).TotalMinutes : 0;
    }

    private async Task<Shift> ShiftForAsync(Employee employee, CancellationToken ct) =>
        (employee.ShiftId is { } id
            ? await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            : null)
        ?? await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.IsDefault, ct)
        ?? new Shift { Name = "Default" };

    public async Task RevertToDeviceAsync(int attendanceDayId, CancellationToken ct = default)
    {
        var day = await db.AttendanceDays.FirstOrDefaultAsync(d => d.Id == attendanceDayId, ct);
        if (day is null) return;

        var employeeId = day.EmployeeId;
        var date = day.Date;

        day.Source = AttendanceSource.Device;
        day.OverriddenById = null;
        day.OverriddenByName = null;
        day.OverriddenAtUtc = null;
        day.OverrideReason = null;
        await db.SaveChangesAsync(ct);

        await sync.RebuildAsync(date, date, employeeId, ct);
    }

    public async Task<List<(string DeviceUserId, int Count, DateTime LastSeen)>> GetUnmatchedAsync(
        CancellationToken ct = default)
    {
        var rows = await db.AttendancePunches
            .Where(p => p.EmployeeId == null)
            .GroupBy(p => p.DeviceUserId)
            .Select(g => new
            {
                DeviceUserId = g.Key,
                Count = g.Count(),
                LastSeen = g.Max(p => p.PunchedAt)
            })
            .OrderByDescending(r => r.LastSeen)
            .ToListAsync(ct);

        return rows.Select(r => (r.DeviceUserId, r.Count, r.LastSeen)).ToList();
    }

    public async Task<int> AssignUnmatchedAsync(
        string deviceUserId, int employeeId, CancellationToken ct = default)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct)
                       ?? throw new InvalidOperationException("Employee not found.");

        var punches = await db.AttendancePunches
            .Where(p => p.EmployeeId == null && p.DeviceUserId == deviceUserId)
            .ToListAsync(ct);

        if (punches.Count == 0) return 0;

        foreach (var punch in punches) punch.EmployeeId = employeeId;

        // Remember the mapping so the next sync matches these automatically.
        if (!string.Equals(employee.EmployeeCode, deviceUserId, StringComparison.OrdinalIgnoreCase))
            employee.DeviceUserId = deviceUserId;

        await db.SaveChangesAsync(ct);

        var from = DateOnly.FromDateTime(punches.Min(p => p.PunchedAt));
        var to = DateOnly.FromDateTime(punches.Max(p => p.PunchedAt));
        await sync.RebuildAsync(from, to, employeeId, ct);

        return punches.Count;
    }
}
