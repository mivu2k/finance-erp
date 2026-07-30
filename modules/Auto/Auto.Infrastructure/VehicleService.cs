using Auto.Domain;
using Microsoft.EntityFrameworkCore;

namespace Auto.Infrastructure;

public interface IVehicleService
{
    Task<List<Vehicle>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<Vehicle?> GetAsync(int id, CancellationToken ct = default);
    Task<Vehicle> CreateAsync(Vehicle vehicle, CancellationToken ct = default);
    Task<Vehicle> UpdateAsync(Vehicle vehicle, CancellationToken ct = default);

    Task<MaintenanceRecord> LogMaintenanceAsync(int vehicleId, MaintenanceRecord record,
        string performedById, string performedByName, CancellationToken ct = default);

    Task<List<MaintenanceRecord>> ListUpcomingMaintenanceAsync(int withinDays = 30, CancellationToken ct = default);
}

public class VehicleService(AutoDbContext db) : IVehicleService
{
    public async Task<List<Vehicle>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        var q = db.Vehicles.Include(v => v.MaintenanceRecords).AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(v => v.Make.Contains(s) || v.Model.Contains(s)
                          || v.RegistrationNumber.Contains(s)
                          || (v.Vin != null && v.Vin.Contains(s)));
        }

        return await q.OrderBy(v => v.RegistrationNumber).ToListAsync(ct);
    }

    public Task<Vehicle?> GetAsync(int id, CancellationToken ct = default) =>
        db.Vehicles.Include(v => v.MaintenanceRecords.OrderByDescending(m => m.Date))
            .FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<Vehicle> CreateAsync(Vehicle vehicle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vehicle.RegistrationNumber))
            throw new InvalidOperationException("Registration number is required.");
        if (string.IsNullOrWhiteSpace(vehicle.Make) || string.IsNullOrWhiteSpace(vehicle.Model))
            throw new InvalidOperationException("Make and model are required.");

        if (await db.Vehicles.AnyAsync(v => v.RegistrationNumber == vehicle.RegistrationNumber, ct))
            throw new InvalidOperationException("A vehicle with this registration number already exists.");

        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(ct);
        return vehicle;
    }

    public async Task<Vehicle> UpdateAsync(Vehicle vehicle, CancellationToken ct = default)
    {
        var existing = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicle.Id, ct)
            ?? throw new InvalidOperationException("Vehicle not found.");

        existing.Make = vehicle.Make;
        existing.Model = vehicle.Model;
        existing.Year = vehicle.Year;
        existing.Vin = vehicle.Vin;
        existing.Color = vehicle.Color;
        existing.PurchaseDate = vehicle.PurchaseDate;
        existing.Status = vehicle.Status;
        existing.CurrentOdometer = vehicle.CurrentOdometer;
        existing.Notes = vehicle.Notes;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<MaintenanceRecord> LogMaintenanceAsync(int vehicleId, MaintenanceRecord record,
        string performedById, string performedByName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.Description))
            throw new InvalidOperationException("Description is required.");

        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId, ct)
            ?? throw new InvalidOperationException("Vehicle not found.");

        record.VehicleId = vehicle.Id;
        record.PerformedById = performedById;
        record.PerformedByName = performedByName;

        if (record.OdometerAtService is { } odo && odo > vehicle.CurrentOdometer)
            vehicle.CurrentOdometer = odo;

        db.MaintenanceRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<List<MaintenanceRecord>> ListUpcomingMaintenanceAsync(int withinDays = 30, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(withinDays);
        return await db.MaintenanceRecords.Include(m => m.Vehicle).AsNoTracking()
            .Where(m => m.NextDueDate != null && m.NextDueDate <= cutoff)
            .OrderBy(m => m.NextDueDate)
            .ToListAsync(ct);
    }
}
