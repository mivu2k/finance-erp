namespace Auto.Domain;

public enum VehicleStatus { Active = 0, Sold = 1, Scrapped = 2 }

public enum MaintenanceType { Service = 0, Repair = 1, Inspection = 2, Insurance = 3, Other = 4 }

/// <summary>A company-owned vehicle. Not a customer's car — those belong to Repair.</summary>
public class Vehicle : AuditableEntity
{
    public string Make { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string RegistrationNumber { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Vin { get; set; }
    public string? Color { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public VehicleStatus Status { get; set; } = VehicleStatus.Active;
    public decimal CurrentOdometer { get; set; }
    public string? Notes { get; set; }

    public List<MaintenanceRecord> MaintenanceRecords { get; set; } = [];
}

/// <summary>
/// One service/repair/inspection/insurance event against a vehicle. The optional
/// next-due fields are what the dashboard reads to flag upcoming maintenance —
/// there's no separate reminder entity.
/// </summary>
public class MaintenanceRecord : AuditableEntity
{
    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;

    public DateOnly Date { get; set; }
    public MaintenanceType Type { get; set; }
    public decimal? OdometerAtService { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public string? VendorName { get; set; }

    public DateOnly? NextDueDate { get; set; }
    public decimal? NextDueOdometer { get; set; }

    public string PerformedById { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
}
