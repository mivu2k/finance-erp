namespace Hr.Domain;

/// <summary>
/// A biometric terminal on the network. Targets ZKTeco devices speaking the
/// standard protocol on TCP 4370 (uFace 800, K40, F18 and similar).
/// </summary>
public class BiometricDevice : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 4370;
    /// <summary>Comm key set on the device (0 when left at default).</summary>
    public int CommKey { get; set; }
    public string? SerialNumber { get; set; }
    public string? Location { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Clear the device's on-board log after a successful pull.</summary>
    /// <remarks>
    /// Off by default. These terminals hold tens of thousands of records, and
    /// keeping them is the only way to re-sync if this database is ever restored
    /// from an older backup.
    /// </remarks>
    public bool ClearLogAfterSync { get; set; }

    public DateTime? LastSyncAtUtc { get; set; }
    public string? LastSyncResult { get; set; }
    public int LastSyncPunchCount { get; set; }
    /// <summary>Newest punch seen from this device — the watermark for the next pull.</summary>
    public DateTime? LastPunchAtUtc { get; set; }
}

/// <summary>
/// One raw read from a terminal, stored exactly as the device reported it. Never
/// edited: corrections happen on <see cref="AttendanceDay"/>, so the biometric
/// record stays the untouched evidence.
/// </summary>
public class AttendancePunch : BaseEntity
{
    public int? BiometricDeviceId { get; set; }
    public BiometricDevice? BiometricDevice { get; set; }

    /// <summary>The user id as enrolled on the terminal — matched to Employee.DeviceUserId.</summary>
    public string DeviceUserId { get; set; } = string.Empty;
    /// <summary>Null when the terminal has an enrolment we can't match to anyone.</summary>
    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Local time as recorded by the device.</summary>
    public DateTime PunchedAt { get; set; }
    public PunchDirection Direction { get; set; } = PunchDirection.Unspecified;
    /// <summary>How the person identified: fingerprint, face, card, password.</summary>
    public VerifyMode VerifyMode { get; set; } = VerifyMode.Unknown;
}

/// <summary>
/// One employee's worked day: the derived summary the reports and payroll read.
/// Rebuilt from punches, but can be overridden by hand when a device misses a read.
/// </summary>
public class AttendanceDay : AuditableEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateOnly Date { get; set; }

    /// <summary>First punch of the day. Nothing else is treated as an arrival.</summary>
    public TimeOnly? FirstIn { get; set; }
    /// <summary>Last punch of the day. Equal to FirstIn when there was only one read.</summary>
    public TimeOnly? LastOut { get; set; }
    public int PunchCount { get; set; }

    public AttendanceStatus Status { get; set; } = AttendanceStatus.Absent;
    public AttendanceSource Source { get; set; } = AttendanceSource.Device;

    public int WorkedMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyLeaveMinutes { get; set; }
    public int OvertimeMinutes { get; set; }

    /// <summary>Set when the day was created or corrected by a person, not the device.</summary>
    public string? OverriddenById { get; set; }
    public string? OverriddenByName { get; set; }
    public DateTime? OverriddenAtUtc { get; set; }
    public string? OverrideReason { get; set; }

    public int? LeaveRequestId { get; set; }
    public LeaveRequest? LeaveRequest { get; set; }

    public string? Notes { get; set; }

    /// <summary>Days that count as attendance for pro-rating pay.</summary>
    public bool IsPayable => Status is AttendanceStatus.Present or AttendanceStatus.Late
        or AttendanceStatus.HalfDay or AttendanceStatus.OnLeave
        or AttendanceStatus.Holiday or AttendanceStatus.WeeklyOff;
}

/// <summary>Working hours a person is measured against.</summary>
public class Shift : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public TimeOnly StartsAt { get; set; } = new(9, 0);
    public TimeOnly EndsAt { get; set; } = new(17, 0);

    /// <summary>Minutes after the start time before a late mark is recorded.</summary>
    public int GraceMinutes { get; set; } = 15;
    /// <summary>Worked minutes below this make it a half day.</summary>
    public int HalfDayMinutes { get; set; } = 240;
    /// <summary>Worked minutes below this count as absent even with punches.</summary>
    public int MinimumMinutes { get; set; } = 60;
    /// <summary>Minutes past the shift end before overtime accrues.</summary>
    public int OvertimeAfterMinutes { get; set; } = 30;
    /// <summary>Unpaid break deducted from worked time.</summary>
    public int BreakMinutes { get; set; }

    /// <summary>
    /// Days off, as a bitmask of <see cref="DayOfWeek"/>. Defaults to Sunday only.
    /// </summary>
    public int WeeklyOffMask { get; set; } = 1 << (int)DayOfWeek.Sunday;

    public bool IsDefault { get; set; }

    public bool IsWeeklyOff(DayOfWeek day) => (WeeklyOffMask & (1 << (int)day)) != 0;
}

/// <summary>A non-working day for everyone — public or company holiday.</summary>
public class Holiday : AuditableEntity
{
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPaid { get; set; } = true;
}

// --- leave ---

public class LeaveType : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    /// <summary>Days granted per year. Zero means unlimited/unmetered (e.g. unpaid).</summary>
    public decimal AnnualQuota { get; set; }
    public bool IsPaid { get; set; } = true;
    /// <summary>Unused days roll into next year, capped by <see cref="MaxCarryForward"/>.</summary>
    public bool AllowCarryForward { get; set; }
    public decimal MaxCarryForward { get; set; }
    /// <summary>Requires a document (medical certificate) beyond this many days.</summary>
    public int? DocumentRequiredAfterDays { get; set; }
    public string? Colour { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LeaveRequest : AuditableEntity
{
    public string RequestNumber { get; set; } = string.Empty;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int LeaveTypeId { get; set; }
    public LeaveType LeaveType { get; set; } = null!;

    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    /// <summary>Half days are supported by marking one end of the range as half.</summary>
    public bool IsHalfDay { get; set; }
    /// <summary>Working days consumed, excluding weekly offs and holidays.</summary>
    public decimal Days { get; set; }

    public string Reason { get; set; } = string.Empty;
    public string? ContactDuringLeave { get; set; }
    public string? AttachmentPath { get; set; }

    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;
    public string? RequestedById { get; set; }
    public string? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>An employee's entitlement and usage for one leave type in one year.</summary>
public class LeaveBalance : AuditableEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int LeaveTypeId { get; set; }
    public LeaveType LeaveType { get; set; } = null!;
    public int Year { get; set; }

    public decimal Entitled { get; set; }
    public decimal CarriedForward { get; set; }
    public decimal Taken { get; set; }
    /// <summary>Approved-but-future plus pending days, held against the balance.</summary>
    public decimal Pending { get; set; }

    public decimal Available => Entitled + CarriedForward - Taken - Pending;
}

public enum PunchDirection { Unspecified = 0, In = 1, Out = 2, BreakOut = 3, BreakIn = 4, OvertimeIn = 5, OvertimeOut = 6 }
public enum VerifyMode { Unknown = -1, Password = 0, Fingerprint = 1, Card = 2, Face = 15 }

public enum AttendanceStatus
{
    Absent = 0,
    Present = 1,
    Late = 2,
    HalfDay = 3,
    OnLeave = 4,
    Holiday = 5,
    WeeklyOff = 6,
    /// <summary>Punched in but never out — needs a human to close it.</summary>
    Incomplete = 7
}

/// <summary>Where a day's record came from — the audit question when a figure is queried.</summary>
public enum AttendanceSource { Device = 0, Manual = 1, Leave = 2, Holiday = 3, WeeklyOff = 4 }

public enum LeaveStatus { Pending = 0, Approved = 1, Rejected = 2, Cancelled = 3 }
