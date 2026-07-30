using Auto.Domain;
using Microsoft.EntityFrameworkCore;

namespace Auto.Infrastructure;

public interface IVehicleService
{
    Task<List<Vehicle>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<Vehicle?> GetAsync(int id, CancellationToken ct = default);
    Task<Vehicle> CreateAsync(Vehicle vehicle, CancellationToken ct = default);
    Task<Vehicle> UpdateAsync(Vehicle vehicle, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a vehicle and its maintenance history. The history goes with it
    /// because a maintenance record only means anything against a vehicle.
    /// </summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<MaintenanceRecord> LogMaintenanceAsync(int vehicleId, MaintenanceRecord record,
        string performedById, string performedByName, CancellationToken ct = default);

    Task<MaintenanceRecord?> GetMaintenanceAsync(int id, CancellationToken ct = default);
    Task<MaintenanceRecord> UpdateMaintenanceAsync(MaintenanceRecord record, CancellationToken ct = default);
    Task DeleteMaintenanceAsync(int id, CancellationToken ct = default);

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

        if (string.IsNullOrWhiteSpace(vehicle.RegistrationNumber))
            throw new InvalidOperationException("Registration number is required.");
        if (string.IsNullOrWhiteSpace(vehicle.Make) || string.IsNullOrWhiteSpace(vehicle.Model))
            throw new InvalidOperationException("Make and model are required.");

        // Registration is unique across the fleet, so a rename has to check the
        // rest of the fleet — excluding this vehicle, or saving it unchanged fails.
        if (await db.Vehicles.AnyAsync(
                v => v.RegistrationNumber == vehicle.RegistrationNumber && v.Id != vehicle.Id, ct))
            throw new InvalidOperationException(
                $"{vehicle.RegistrationNumber} is already on another vehicle.");

        existing.RegistrationNumber = vehicle.RegistrationNumber;
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

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var vehicle = await db.Vehicles.Include(v => v.MaintenanceRecords)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return;

        // Soft delete — ModuleDbContext turns Remove into a flag update.
        db.MaintenanceRecords.RemoveRange(vehicle.MaintenanceRecords);
        db.Vehicles.Remove(vehicle);
        await db.SaveChangesAsync(ct);
    }

    public Task<MaintenanceRecord?> GetMaintenanceAsync(int id, CancellationToken ct = default) =>
        db.MaintenanceRecords.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<MaintenanceRecord> UpdateMaintenanceAsync(
        MaintenanceRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.Description))
            throw new InvalidOperationException("Description is required.");

        var existing = await db.MaintenanceRecords.FirstOrDefaultAsync(m => m.Id == record.Id, ct)
            ?? throw new InvalidOperationException("Maintenance record not found.");

        existing.Date = record.Date;
        existing.Type = record.Type;
        existing.OdometerAtService = record.OdometerAtService;
        existing.Description = record.Description;
        existing.Cost = record.Cost;
        existing.VendorName = record.VendorName;
        existing.NextDueDate = record.NextDueDate;
        existing.NextDueOdometer = record.NextDueOdometer;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteMaintenanceAsync(int id, CancellationToken ct = default)
    {
        var record = await db.MaintenanceRecords.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (record is null) return;
        db.MaintenanceRecords.Remove(record);
        await db.SaveChangesAsync(ct);
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
