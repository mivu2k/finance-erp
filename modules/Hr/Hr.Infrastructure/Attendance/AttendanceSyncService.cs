using Hr.Domain;
using Hr.Infrastructure.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hr.Infrastructure.Attendance;

public record SyncResult(
    string DeviceName,
    bool Succeeded,
    int PunchesRead,
    int PunchesNew,
    int Unmatched,
    int DaysRebuilt,
    string Message);

public interface IAttendanceSyncService
{
    /// <summary>Pulls every enabled device and rebuilds the days it touched.</summary>
    Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default);
    Task<SyncResult> SyncDeviceAsync(int deviceId, CancellationToken ct = default);
    Task<ZkDeviceInfo> TestAsync(int deviceId, CancellationToken ct = default);
    /// <summary>Users enrolled on a terminal, for matching device ids to employees.</summary>
    Task<IReadOnlyList<ZkUser>> ReadDeviceUsersAsync(int deviceId, CancellationToken ct = default);

    /// <summary>
    /// Recomputes the derived day records for a date range from stored punches.
    /// Manually corrected days are left alone.
    /// </summary>
    Task<int> RebuildAsync(DateOnly from, DateOnly to, int? employeeId = null,
        CancellationToken ct = default);
}

public class AttendanceSyncService(
    HrDbContext db,
    IZkDeviceClient client,
    ILogger<AttendanceSyncService> logger) : IAttendanceSyncService
{
    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        var devices = await db.BiometricDevices
            .Where(d => d.IsEnabled).Select(d => d.Id).ToListAsync(ct);

        var results = new List<SyncResult>();
        foreach (var id in devices)
            results.Add(await SyncDeviceAsync(id, ct));

        return results;
    }

    public async Task<SyncResult> SyncDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        var device = await db.BiometricDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                     ?? throw new InvalidOperationException("Device not found.");

        try
        {
            var records = await client.GetAttendanceAsync(device.Host, device.Port, device.CommKey, ct);
            var result = await IngestAsync(device, records, ct);

            device.LastSyncAtUtc = DateTime.UtcNow;
            device.LastSyncResult = result.Message;
            device.LastSyncPunchCount = result.PunchesNew;
            if (records.Count > 0)
                device.LastPunchAtUtc = records.Max(r => r.Timestamp);

            if (device.ClearLogAfterSync && result.Succeeded && result.PunchesRead > 0)
            {
                await client.ClearAttendanceAsync(device.Host, device.Port, device.CommKey, ct);
                logger.LogInformation("Cleared the on-board log of {Device}", device.Name);
            }

            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Attendance sync failed for {Device}", device.Name);

            device.LastSyncAtUtc = DateTime.UtcNow;
            device.LastSyncResult = Summarise(ex);
            await db.SaveChangesAsync(ct);

            return new SyncResult(device.Name, false, 0, 0, 0, 0, Summarise(ex));
        }
    }

    /// <summary>
    /// Stores new punches and rebuilds the days they fall on. Re-reading the same
    /// log is the normal case — the device keeps everything — so this is written to
    /// be safely repeatable rather than to assume a clean watermark.
    /// </summary>
    private async Task<SyncResult> IngestAsync(
        BiometricDevice device, IReadOnlyList<ZkAttendanceRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
            return new SyncResult(device.Name, true, 0, 0, 0, 0, "Nothing new on the terminal.");

        var valid = records.Where(r => r.Timestamp != default).ToList();

        // Match device enrolment ids to people. An employee's device id defaults to
        // their employee code, so most sites need no mapping at all.
        var employees = await db.Employees
            .Select(e => new { e.Id, e.EmployeeCode, e.DeviceUserId })
            .ToListAsync(ct);

        var byDeviceId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in employees)
        {
            var key = string.IsNullOrWhiteSpace(e.DeviceUserId) ? e.EmployeeCode : e.DeviceUserId;
            if (!string.IsNullOrWhiteSpace(key)) byDeviceId[key.Trim()] = e.Id;
        }

        // Load what we already hold for the window the device reported, so the
        // dedupe is one query rather than one per record.
        var from = valid.Min(r => r.Timestamp).Date;
        var to = valid.Max(r => r.Timestamp).Date.AddDays(1);

        var existing = await db.AttendancePunches
            .Where(p => p.BiometricDeviceId == device.Id && p.PunchedAt >= from && p.PunchedAt < to)
            .Select(p => new { p.DeviceUserId, p.PunchedAt })
            .ToListAsync(ct);

        var seen = existing
            .Select(p => (p.DeviceUserId, p.PunchedAt))
            .ToHashSet();

        var added = 0;
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<(int EmployeeId, DateOnly Date)>();

        foreach (var record in valid)
        {
            var key = record.DeviceUserId.Trim();
            if (!seen.Add((key, record.Timestamp))) continue;

            var matched = byDeviceId.TryGetValue(key, out var employeeId);
            if (!matched) unmatched.Add(key);

            db.AttendancePunches.Add(new AttendancePunch
            {
                BiometricDeviceId = device.Id,
                DeviceUserId = key,
                EmployeeId = matched ? employeeId : null,
                PunchedAt = record.Timestamp,
                VerifyMode = Enum.IsDefined(typeof(VerifyMode), record.VerifyMode)
                    ? (VerifyMode)record.VerifyMode
                    : VerifyMode.Unknown,
                Direction = PunchDirection.Unspecified
            });

            added++;
            if (matched) touched.Add((employeeId, DateOnly.FromDateTime(record.Timestamp)));
        }

        await db.SaveChangesAsync(ct);

        var rebuilt = 0;
        foreach (var group in touched.GroupBy(t => t.EmployeeId))
        {
            var dates = group.Select(g => g.Date).ToList();
            rebuilt += await RebuildAsync(dates.Min(), dates.Max(), group.Key, ct);
        }

        var message = added == 0
            ? $"Read {valid.Count} record(s); all already held."
            : $"Read {valid.Count} record(s), stored {added} new, rebuilt {rebuilt} day(s).";

        if (unmatched.Count > 0)
            message += $" {unmatched.Count} device id(s) match no employee: " +
                       string.Join(", ", unmatched.Take(5)) +
                       (unmatched.Count > 5 ? "…" : "") + ".";

        return new SyncResult(device.Name, true, valid.Count, added, unmatched.Count, rebuilt, message);
    }

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
            .Where(p => p.EmployeeId != null && p.PunchedAt >= start && p.PunchedAt < end
                        && (employeeId == null || p.EmployeeId == employeeId))
            .Select(p => new { EmployeeId = p.EmployeeId!.Value, p.PunchedAt })
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

    public async Task<ZkDeviceInfo> TestAsync(int deviceId, CancellationToken ct = default)
    {
        var device = await db.BiometricDevices.AsNoTracking()
                         .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                     ?? throw new InvalidOperationException("Device not found.");

        return await client.GetInfoAsync(device.Host, device.Port, device.CommKey, ct);
    }

    public async Task<IReadOnlyList<ZkUser>> ReadDeviceUsersAsync(
        int deviceId, CancellationToken ct = default)
    {
        var device = await db.BiometricDevices.AsNoTracking()
                         .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                     ?? throw new InvalidOperationException("Device not found.");

        return await client.GetUsersAsync(device.Host, device.Port, device.CommKey, ct);
    }

    private static string Summarise(Exception ex) =>
        ex is ZkDeviceException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
}
