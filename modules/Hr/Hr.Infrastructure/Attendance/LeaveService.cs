using ErpPlatform.Shared.Persistence;
using Hr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hr.Infrastructure.Attendance;

public record LeaveFilter(
    int? EmployeeId = null,
    LeaveStatus? Status = null,
    int? LeaveTypeId = null,
    DateOnly? From = null,
    DateOnly? To = null);

public interface ILeaveService
{
    Task<List<LeaveType>> ListTypesAsync(bool activeOnly = true, CancellationToken ct = default);
    Task SaveTypeAsync(LeaveType type, CancellationToken ct = default);

    Task<List<LeaveRequest>> ListAsync(LeaveFilter filter, CancellationToken ct = default);
    Task<LeaveRequest?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Raises a request, working out the day count and holding it against the balance.</summary>
    Task<LeaveRequest> ApplyAsync(LeaveRequest request, string? requestedById,
        CancellationToken ct = default);
    Task<LeaveRequest> ApproveAsync(int id, string actorId, string actorName, string? note,
        CancellationToken ct = default);
    Task<LeaveRequest> RejectAsync(int id, string actorId, string actorName, string? note,
        CancellationToken ct = default);
    Task<LeaveRequest> CancelAsync(int id, CancellationToken ct = default);

    Task<List<LeaveBalance>> GetBalancesAsync(int employeeId, int year, CancellationToken ct = default);
    /// <summary>Creates missing balance rows for the year from each type's quota.</summary>
    Task<int> OpenYearAsync(int year, CancellationToken ct = default);
}

public class LeaveService(HrDbContext db, IAttendanceSyncService sync) : ILeaveService
{
    public Task<List<LeaveType>> ListTypesAsync(bool activeOnly = true, CancellationToken ct = default) =>
        db.LeaveTypes.Where(t => !activeOnly || t.IsActive)
            .OrderBy(t => t.Name).AsNoTracking().ToListAsync(ct);

    public async Task SaveTypeAsync(LeaveType type, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(type.Name))
            throw new InvalidOperationException("Leave type name is required.");
        if (type.AnnualQuota < 0)
            throw new InvalidOperationException("Annual quota can't be negative.");

        if (type.Id == 0) db.LeaveTypes.Add(type);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<LeaveRequest>> ListAsync(LeaveFilter filter, CancellationToken ct = default)
    {
        var q = db.LeaveRequests
            .Include(l => l.Employee)
            .Include(l => l.LeaveType)
            .AsNoTracking().AsQueryable();

        if (filter.EmployeeId is { } employeeId) q = q.Where(l => l.EmployeeId == employeeId);
        if (filter.Status is { } status) q = q.Where(l => l.Status == status);
        if (filter.LeaveTypeId is { } typeId) q = q.Where(l => l.LeaveTypeId == typeId);
        if (filter.From is { } from) q = q.Where(l => l.ToDate >= from);
        if (filter.To is { } to) q = q.Where(l => l.FromDate <= to);

        return q.OrderByDescending(l => l.Id).Take(500).ToListAsync(ct);
    }

    public Task<LeaveRequest?> GetAsync(int id, CancellationToken ct = default) =>
        db.LeaveRequests
            .Include(l => l.Employee)
            .Include(l => l.LeaveType)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<LeaveRequest> ApplyAsync(
        LeaveRequest request, string? requestedById, CancellationToken ct = default)
    {
        if (request.ToDate < request.FromDate)
            throw new InvalidOperationException("The end date is before the start date.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("A reason is required.");

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == request.EmployeeId, ct)
                       ?? throw new InvalidOperationException("Employee not found.");
        var type = await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId, ct)
                   ?? throw new InvalidOperationException("Leave type not found.");

        // Two open requests over the same dates would double-count the balance.
        var clashes = await db.LeaveRequests.AnyAsync(l =>
            l.EmployeeId == request.EmployeeId
            && l.Id != request.Id
            && (l.Status == LeaveStatus.Pending || l.Status == LeaveStatus.Approved)
            && l.FromDate <= request.ToDate && l.ToDate >= request.FromDate, ct);

        if (clashes)
            throw new InvalidOperationException(
                "This overlaps leave already requested or approved for these dates.");

        var shift = await ShiftForAsync(employee, ct);
        var holidays = (await db.Holidays
            .Where(h => h.Date >= request.FromDate && h.Date <= request.ToDate)
            .Select(h => h.Date).ToListAsync(ct)).ToHashSet();

        request.Days = AttendanceCalculator.WorkingDays(
            request.FromDate, request.ToDate, shift, holidays, request.IsHalfDay);

        if (request.Days <= 0)
            throw new InvalidOperationException(
                "That range is entirely weekly offs and holidays — no leave needed.");

        if (type is { AnnualQuota: > 0 })
        {
            var balance = await EnsureBalanceAsync(
                request.EmployeeId, type.Id, request.FromDate.Year, ct);

            if (request.Days > balance.Available)
                throw new InvalidOperationException(
                    $"Only {balance.Available:0.##} day(s) of {type.Name} remain; " +
                    $"this request is for {request.Days:0.##}.");

            // Hold the days while the request is open, so a second request can't
            // spend the same balance before this one is decided.
            balance.Pending += request.Days;
        }

        request.RequestNumber = await new DocumentNumberService(db).NextAsync("LeaveRequest", "LV", ct);
        request.Status = LeaveStatus.Pending;
        request.RequestedById = requestedById;

        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<LeaveRequest> ApproveAsync(
        int id, string actorId, string actorName, string? note, CancellationToken ct = default)
    {
        var request = await Require(id, ct);
        if (request.Status != LeaveStatus.Pending)
            throw new InvalidOperationException(
                $"This request is already {request.Status.ToString().ToLowerInvariant()}.");

        request.Status = LeaveStatus.Approved;
        request.DecidedById = actorId;
        request.DecidedByName = actorName;
        request.DecidedAtUtc = DateTime.UtcNow;
        request.DecisionNote = note;

        // The held days become spent days.
        if (await FindBalanceAsync(request, ct) is { } balance)
        {
            balance.Pending = Math.Max(0, balance.Pending - request.Days);
            balance.Taken += request.Days;
        }

        await db.SaveChangesAsync(ct);

        // Approved leave marks the attendance days, so the register shows why
        // someone wasn't at the terminal.
        await sync.RebuildAsync(request.FromDate, request.ToDate, request.EmployeeId, ct);
        return request;
    }

    public async Task<LeaveRequest> RejectAsync(
        int id, string actorId, string actorName, string? note, CancellationToken ct = default)
    {
        var request = await Require(id, ct);
        if (request.Status != LeaveStatus.Pending)
            throw new InvalidOperationException(
                $"This request is already {request.Status.ToString().ToLowerInvariant()}.");

        request.Status = LeaveStatus.Rejected;
        request.DecidedById = actorId;
        request.DecidedByName = actorName;
        request.DecidedAtUtc = DateTime.UtcNow;
        request.DecisionNote = note;

        if (await FindBalanceAsync(request, ct) is { } balance)
            balance.Pending = Math.Max(0, balance.Pending - request.Days);

        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<LeaveRequest> CancelAsync(int id, CancellationToken ct = default)
    {
        var request = await Require(id, ct);
        if (request.Status == LeaveStatus.Cancelled) return request;
        if (request.Status == LeaveStatus.Rejected)
            throw new InvalidOperationException("A rejected request can't be cancelled.");

        var wasApproved = request.Status == LeaveStatus.Approved;
        request.Status = LeaveStatus.Cancelled;

        if (await FindBalanceAsync(request, ct) is { } balance)
        {
            if (wasApproved) balance.Taken = Math.Max(0, balance.Taken - request.Days);
            else balance.Pending = Math.Max(0, balance.Pending - request.Days);
        }

        await db.SaveChangesAsync(ct);

        if (wasApproved)
            await sync.RebuildAsync(request.FromDate, request.ToDate, request.EmployeeId, ct);

        return request;
    }

    public Task<List<LeaveBalance>> GetBalancesAsync(
        int employeeId, int year, CancellationToken ct = default) =>
        db.LeaveBalances
            .Include(b => b.LeaveType)
            .Where(b => b.EmployeeId == employeeId && b.Year == year)
            .OrderBy(b => b.LeaveType.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>
    /// Opens the year: every active employee gets a balance row per metered leave
    /// type, entitled to that type's quota, with unused days carried forward from
    /// last year where the type allows it.
    /// </summary>
    public async Task<int> OpenYearAsync(int year, CancellationToken ct = default)
    {
        var types = await db.LeaveTypes.Where(t => t.IsActive && t.AnnualQuota > 0).ToListAsync(ct);
        var employees = await db.Employees
            .Where(e => e.Status == EmployeeStatus.Active || e.Status == EmployeeStatus.OnLeave)
            .Select(e => e.Id).ToListAsync(ct);

        var existing = await db.LeaveBalances
            .Where(b => b.Year == year)
            .Select(b => new { b.EmployeeId, b.LeaveTypeId })
            .ToListAsync(ct);

        var have = existing.Select(e => (e.EmployeeId, e.LeaveTypeId)).ToHashSet();

        var previous = await db.LeaveBalances
            .Where(b => b.Year == year - 1)
            .ToListAsync(ct);

        var created = 0;
        foreach (var employeeId in employees)
            foreach (var type in types)
            {
                if (have.Contains((employeeId, type.Id))) continue;

                var carry = 0m;
                if (type.AllowCarryForward)
                {
                    var last = previous.FirstOrDefault(
                        b => b.EmployeeId == employeeId && b.LeaveTypeId == type.Id);
                    if (last is not null)
                        carry = Math.Min(Math.Max(0, last.Available), type.MaxCarryForward);
                }

                db.LeaveBalances.Add(new LeaveBalance
                {
                    EmployeeId = employeeId,
                    LeaveTypeId = type.Id,
                    Year = year,
                    Entitled = type.AnnualQuota,
                    CarriedForward = carry
                });
                created++;
            }

        await db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<LeaveBalance> EnsureBalanceAsync(
        int employeeId, int leaveTypeId, int year, CancellationToken ct)
    {
        var balance = await db.LeaveBalances.FirstOrDefaultAsync(
            b => b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId && b.Year == year, ct);

        if (balance is not null) return balance;

        var type = await db.LeaveTypes.FirstAsync(t => t.Id == leaveTypeId, ct);
        balance = new LeaveBalance
        {
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            Year = year,
            Entitled = type.AnnualQuota
        };
        db.LeaveBalances.Add(balance);
        return balance;
    }

    private Task<LeaveBalance?> FindBalanceAsync(LeaveRequest request, CancellationToken ct) =>
        db.LeaveBalances.FirstOrDefaultAsync(
            b => b.EmployeeId == request.EmployeeId
                 && b.LeaveTypeId == request.LeaveTypeId
                 && b.Year == request.FromDate.Year, ct);

    private async Task<Shift> ShiftForAsync(Employee employee, CancellationToken ct) =>
        (employee.ShiftId is { } id
            ? await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            : null)
        ?? await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.IsDefault, ct)
        ?? new Shift { Name = "Default" };

    private async Task<LeaveRequest> Require(int id, CancellationToken ct) =>
        await db.LeaveRequests.Include(l => l.LeaveType)
            .FirstOrDefaultAsync(l => l.Id == id, ct)
        ?? throw new InvalidOperationException("Leave request not found.");
}
