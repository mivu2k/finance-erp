namespace Hr.Domain;

/// <summary>
/// The HR master record for a person — the full detail Identity deliberately
/// doesn't carry. Linked to a platform login by <see cref="UserId"/> when the
/// person has one; employees without a login (workshop staff, drivers) are kept
/// here too, identified by <see cref="EmployeeCode"/>.
/// </summary>
public class Employee : AuditableEntity
{
    public string EmployeeCode { get; set; } = string.Empty;
    /// <summary>Identity user id, when this person can sign in. Null for non-users.</summary>
    public string? UserId { get; set; }

    // --- personal ---
    public string FullName { get; set; } = string.Empty;
    public string? FatherName { get; set; }
    public string? NationalId { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public Gender Gender { get; set; } = Gender.Unspecified;
    public MaritalStatus MaritalStatus { get; set; } = MaritalStatus.Unspecified;
    public string? BloodGroup { get; set; }
    public string? PhotoPath { get; set; }

    // --- contact ---
    public string? Phone { get; set; }
    public string? AlternatePhone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }

    // --- employment ---
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int? DesignationId { get; set; }
    public Designation? Designation { get; set; }
    public int? ShiftId { get; set; }
    public Shift? Shift { get; set; }

    /// <summary>
    /// The user id this person is enrolled under on the biometric terminals.
    /// Defaults to <see cref="EmployeeCode"/> but kept separate because the device
    /// often carries a shorter numeric id that predates the HR record.
    /// </summary>
    /// <summary>
    /// UID of the NFC card or fob issued to this person, exactly as the reader
    /// types it. Unique: two people on one card would merge their attendance.
    /// </summary>
    public string? CardNumber { get; set; }

    /// <summary>
    /// Per-employee secret behind their rotating attendance QR. Random, generated
    /// on first use. Re-issuing it invalidates every code already on their screen,
    /// which is what you want when a phone is lost.
    /// </summary>
    public string? QrSecret { get; set; }
    public string? ReportsToEmployeeCode { get; set; }
    public EmploymentType EmploymentType { get; set; } = EmploymentType.Permanent;
    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;
    public DateOnly JoinedOn { get; set; }
    public DateOnly? ConfirmedOn { get; set; }
    public DateOnly? LeftOn { get; set; }
    public string? LeavingReason { get; set; }
    public string? WorkLocation { get; set; }

    // --- payroll-adjacent reference data ---
    // The authoritative salary structure lives in Finance; these are the banking
    // and statutory details HR owns and payroll reads when paying someone.
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountTitle { get; set; }
    public string? TaxNumber { get; set; }
    public string? SocialSecurityNumber { get; set; }

    public string? Notes { get; set; }

    public List<EmployeeDocument> Documents { get; set; } = [];

}

/// <summary>A scanned or uploaded file attached to an employee (contract, CV, ID, certificate).</summary>
public class EmployeeDocument : AuditableEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; } = DocumentKind.Other;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>For documents that lapse — visas, licences, contracts.</summary>
    public DateOnly? ExpiresOn { get; set; }
    public string? Notes { get; set; }
}

public class Department : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? HeadEmployeeCode { get; set; }
}

public class Designation : AuditableEntity
{
    public string Title { get; set; } = string.Empty;
    public string? Grade { get; set; }
}

public enum Gender { Unspecified = 0, Male = 1, Female = 2, Other = 3 }
public enum MaritalStatus { Unspecified = 0, Single = 1, Married = 2, Other = 3 }
public enum EmploymentType { Permanent = 0, Contract = 1, Probation = 2, Intern = 3, PartTime = 4, Daily = 5 }
public enum EmployeeStatus { Active = 0, OnLeave = 1, Suspended = 2, Resigned = 3, Terminated = 4, Retired = 5 }
public enum DocumentKind { Other = 0, Contract = 1, NationalId = 2, Cv = 3, Certificate = 4, Licence = 5, Photo = 6, Appraisal = 7 }
