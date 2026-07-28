using ErpPlatform.Shared.Identity;
using ErpPlatform.Shared.Persistence;
using Hr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hr.Infrastructure;

public record EmployeeFilter(
    string? Search = null,
    int? DepartmentId = null,
    int? DesignationId = null,
    EmployeeStatus? Status = null,
    bool IncludeLeavers = false);

public interface IEmployeeService
{
    Task<List<Employee>> ListAsync(EmployeeFilter filter, CancellationToken ct = default);
    Task<Employee?> GetAsync(int id, CancellationToken ct = default);
    Task<Employee?> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task<Employee> SaveAsync(Employee employee, CancellationToken ct = default);
    Task<Employee> SeparateAsync(int id, DateOnly leftOn, EmployeeStatus status, string? reason, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<List<Department>> ListDepartmentsAsync(CancellationToken ct = default);
    Task<List<Designation>> ListDesignationsAsync(CancellationToken ct = default);
    Task SaveDepartmentAsync(Department department, CancellationToken ct = default);
    Task SaveDesignationAsync(Designation designation, CancellationToken ct = default);

    Task<List<EmployeeDocument>> ListDocumentsAsync(int employeeId, CancellationToken ct = default);
    Task AddDocumentAsync(EmployeeDocument document, CancellationToken ct = default);
    Task DeleteDocumentAsync(int documentId, CancellationToken ct = default);
    /// <summary>Documents lapsing within <paramref name="withinDays"/> — the HR dashboard's alert list.</summary>
    Task<List<EmployeeDocument>> ListExpiringDocumentsAsync(int withinDays = 60, CancellationToken ct = default);

    /// <summary>Platform users who can enter HR but have no employee record yet.</summary>
    Task<List<PlatformUser>> ListUnlinkedUsersAsync(CancellationToken ct = default);
}

public class EmployeeService(HrDbContext db, IPlatformUserDirectory directory) : IEmployeeService
{
    private const string EmployeeCodeSequence = "Employee";

    public async Task<List<Employee>> ListAsync(EmployeeFilter filter, CancellationToken ct = default)
    {
        var q = db.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .AsNoTracking()
            .AsQueryable();

        if (!filter.IncludeLeavers && filter.Status is null)
            q = q.Where(e => e.Status != EmployeeStatus.Resigned
                          && e.Status != EmployeeStatus.Terminated
                          && e.Status != EmployeeStatus.Retired);

        if (filter.Status is { } status) q = q.Where(e => e.Status == status);
        if (filter.DepartmentId is { } dept) q = q.Where(e => e.DepartmentId == dept);
        if (filter.DesignationId is { } desig) q = q.Where(e => e.DesignationId == desig);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(e => e.FullName.Contains(s)
                          || e.EmployeeCode.Contains(s)
                          || (e.Phone != null && e.Phone.Contains(s))
                          || (e.NationalId != null && e.NationalId.Contains(s)));
        }

        return await q.OrderBy(e => e.FullName).ToListAsync(ct);
    }

    public Task<Employee?> GetAsync(int id, CancellationToken ct = default) =>
        db.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .Include(e => e.Documents)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<Employee?> GetByUserIdAsync(string userId, CancellationToken ct = default) =>
        db.Employees.Include(e => e.Department).Include(e => e.Designation)
            .FirstOrDefaultAsync(e => e.UserId == userId, ct);

    public async Task<Employee> SaveAsync(Employee employee, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(employee.FullName))
            throw new InvalidOperationException("Full name is required.");
        if (employee.JoinedOn == default)
            throw new InvalidOperationException("Joining date is required.");
        if (employee.LeftOn is { } left && left < employee.JoinedOn)
            throw new InvalidOperationException("Leaving date cannot be before the joining date.");

        if (string.IsNullOrWhiteSpace(employee.EmployeeCode))
            employee.EmployeeCode = await new DocumentNumberService(db)
                .NextAsync(EmployeeCodeSequence, "EMP", ct);

        var codeTaken = await db.Employees
            .AnyAsync(e => e.EmployeeCode == employee.EmployeeCode && e.Id != employee.Id, ct);
        if (codeTaken)
            throw new InvalidOperationException($"Employee code {employee.EmployeeCode} is already in use.");

        // One employee record per login, so payroll and attendance can't double up.
        if (!string.IsNullOrWhiteSpace(employee.UserId))
        {
            var userTaken = await db.Employees
                .AnyAsync(e => e.UserId == employee.UserId && e.Id != employee.Id, ct);
            if (userTaken)
                throw new InvalidOperationException("That login is already linked to another employee.");
        }
        else
        {
            employee.UserId = null;
        }

        if (employee.Id == 0) db.Employees.Add(employee);
        await db.SaveChangesAsync(ct);
        return employee;
    }

    public async Task<Employee> SeparateAsync(
        int id, DateOnly leftOn, EmployeeStatus status, string? reason, CancellationToken ct = default)
    {
        if (status is not (EmployeeStatus.Resigned or EmployeeStatus.Terminated or EmployeeStatus.Retired))
            throw new InvalidOperationException("Separation must be a resignation, termination or retirement.");

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct)
                       ?? throw new InvalidOperationException("Employee not found.");
        if (leftOn < employee.JoinedOn)
            throw new InvalidOperationException("Leaving date cannot be before the joining date.");

        employee.Status = status;
        employee.LeftOn = leftOn;
        employee.LeavingReason = reason;
        await db.SaveChangesAsync(ct);
        return employee;
    }

    /// <summary>Soft delete — HR records are never removed outright.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return;
        db.Employees.Remove(employee);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<Department>> ListDepartmentsAsync(CancellationToken ct = default) =>
        db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);

    public Task<List<Designation>> ListDesignationsAsync(CancellationToken ct = default) =>
        db.Designations.AsNoTracking().OrderBy(d => d.Title).ToListAsync(ct);

    public async Task SaveDepartmentAsync(Department department, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(department.Name))
            throw new InvalidOperationException("Department name is required.");
        if (department.Id == 0) db.Departments.Add(department);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveDesignationAsync(Designation designation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(designation.Title))
            throw new InvalidOperationException("Designation title is required.");
        if (designation.Id == 0) db.Designations.Add(designation);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<EmployeeDocument>> ListDocumentsAsync(int employeeId, CancellationToken ct = default) =>
        db.EmployeeDocuments.AsNoTracking()
            .Where(d => d.EmployeeId == employeeId)
            .OrderByDescending(d => d.Id).ToListAsync(ct);

    public async Task AddDocumentAsync(EmployeeDocument document, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(document.Title))
            throw new InvalidOperationException("Document title is required.");
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteDocumentAsync(int documentId, CancellationToken ct = default)
    {
        var doc = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return;
        db.EmployeeDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<EmployeeDocument>> ListExpiringDocumentsAsync(
        int withinDays = 60, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(withinDays));
        return db.EmployeeDocuments.Include(d => d.Employee).AsNoTracking()
            .Where(d => d.ExpiresOn != null && d.ExpiresOn <= cutoff)
            .OrderBy(d => d.ExpiresOn).ToListAsync(ct);
    }

    public async Task<List<PlatformUser>> ListUnlinkedUsersAsync(CancellationToken ct = default)
    {
        var linked = await db.Employees.Where(e => e.UserId != null)
            .Select(e => e.UserId!).ToListAsync(ct);
        var users = await directory.ListForModuleAsync(AppModules.Hr, ct);
        return users.Where(u => !linked.Contains(u.UserId)).ToList();
    }
}
