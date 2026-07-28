using Microsoft.EntityFrameworkCore;
using Repair.Domain;

namespace Repair.Infrastructure;

public interface ICustomerService
{
    Task<List<Customer>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<Customer?> GetAsync(int id, CancellationToken ct = default);
    Task<Customer> SaveAsync(Customer customer, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    /// <summary>Everything this customer has ever brought in, newest first.</summary>
    Task<List<RepairJob>> HistoryAsync(int customerId, CancellationToken ct = default);
}

public class CustomerService(RepairDbContext db) : ICustomerService
{
    public async Task<List<Customer>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        var q = db.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => c.Name.Contains(s)
                          || c.Phone.Contains(s)
                          || (c.Organization != null && c.Organization.Contains(s))
                          || (c.Email != null && c.Email.Contains(s)));
        }

        return await q.OrderBy(c => c.Name).Take(500).ToListAsync(ct);
    }

    public Task<Customer?> GetAsync(int id, CancellationToken ct = default) =>
        db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<Customer> SaveAsync(Customer customer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customer.Name))
            throw new InvalidOperationException("Customer name is required.");
        if (string.IsNullOrWhiteSpace(customer.Phone))
            throw new InvalidOperationException("Phone number is required.");

        if (customer.Id == 0) db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return;

        // Refusing rather than cascading: the jobs are the customer's history.
        if (await db.Intakes.AnyAsync(i => i.CustomerId == id, ct))
            throw new InvalidOperationException(
                "This customer has intakes on file and can't be removed.");

        db.Customers.Remove(customer);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<RepairJob>> HistoryAsync(int customerId, CancellationToken ct = default) =>
        db.RepairJobs.AsNoTracking()
            .Where(j => j.CustomerId == customerId)
            .OrderByDescending(j => j.Id)
            .ToListAsync(ct);
}
