namespace Tender.Domain;

/// <summary>Which register a physical file belongs to.</summary>
public enum FileOwnerType
{
    Tender = 0,
    Project = 1
}

/// <summary>
/// Where the physical folder is. <see cref="Issued"/> means someone is holding it;
/// <see cref="Archived"/> means it is closed and back in permanent storage, which is
/// deliberately not the same as sitting in the active registry.
/// </summary>
public enum FileStatus
{
    InRegistry = 0,
    Issued = 1,
    Archived = 2,
    Lost = 3
}

/// <summary>
/// What happened to the file. Every one of these writes a <see cref="FileMovement"/>,
/// because "who had this last" is only answerable from a complete chain — a status
/// field alone loses the history the moment it changes.
/// </summary>
public enum FileMovementAction
{
    Opened = 0,
    Issued = 1,
    Returned = 2,
    /// <summary>Handed straight from one holder to another without coming back first.</summary>
    Transferred = 3,
    Archived = 4,
    Reopened = 5,
    MarkedLost = 6,
    Found = 7
}

/// <summary>
/// The physical folder behind a tender or a project — the thing with a sticker on the
/// spine that people carry around and lose.
/// </summary>
/// <remarks>
/// Kept as its own record rather than a few columns on the tender/project because a
/// file has a life of its own: it is issued, returned, transferred and eventually
/// archived, and each of those is a dated event with a person against it. One file
/// per owner record, created automatically when that record is.
/// <para>
/// <see cref="OwnerReference"/> and <see cref="OwnerTitle"/> are snapshots so the
/// registry list and the printed sticker stay readable without joining back to two
/// different tables on every row.
/// </para>
/// </remarks>
public class PhysicalFile : AuditableEntity, IConcurrencyChecked
{
    /// <summary>Optimistic lock: two clerks must not both issue the same folder.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    /// <summary>Allocated from the shared document sequence — <c>FILE-26-0001</c>.</summary>
    public string FileNumber { get; set; } = string.Empty;

    public FileOwnerType OwnerType { get; set; }
    public int OwnerId { get; set; }

    /// <summary>The tender number or project code, snapshotted.</summary>
    public string OwnerReference { get; set; } = string.Empty;
    public string OwnerTitle { get; set; } = string.Empty;

    public FileStatus Status { get; set; } = FileStatus.InRegistry;

    /// <summary>Who is holding it right now. Null whenever it is not <see cref="FileStatus.Issued"/>.</summary>
    public string? HolderUserId { get; set; }
    public string? HolderName { get; set; }

    /// <summary>Where it physically sits when nobody has it — "Cabinet 3, Shelf B".</summary>
    public string? Location { get; set; }

    /// <summary>Racks, boxes or volumes, when a file outgrows one folder.</summary>
    public string? VolumeNumber { get; set; }

    public DateOnly OpenedOn { get; set; }
    public DateOnly? ClosedOn { get; set; }

    public string? Remarks { get; set; }

    public List<FileMovement> Movements { get; set; } = [];

    /// <summary>Out of the registry and not yet back.</summary>
    public bool IsOut => Status == FileStatus.Issued;

    /// <summary>
    /// How long the current holder has had it, for the overdue list. Null when the
    /// file is not out.
    /// </summary>
    public int? DaysOutOn(DateOnly today) =>
        Status != FileStatus.Issued
            ? null
            : Movements
                .Where(m => m.Action is FileMovementAction.Issued or FileMovementAction.Transferred)
                .OrderByDescending(m => m.MovedOn).ThenByDescending(m => m.Id)
                .Select(m => today.DayNumber - m.MovedOn.DayNumber)
                .FirstOrDefault();
}

/// <summary>
/// One movement of a physical file. Append-only: correcting a mistake means
/// recording the opposite movement, never editing history, which is the whole point
/// of a tracking register.
/// </summary>
public class FileMovement : AuditableEntity
{
    public int PhysicalFileId { get; set; }
    public PhysicalFile PhysicalFile { get; set; } = null!;

    public FileMovementAction Action { get; set; }
    public DateOnly MovedOn { get; set; }

    /// <summary>Who held it before this movement — snapshotted so the chain reads on its own.</summary>
    public string? FromHolderName { get; set; }
    public string? FromLocation { get; set; }

    public string? ToHolderUserId { get; set; }
    public string? ToHolderName { get; set; }
    public string? ToLocation { get; set; }

    /// <summary>Why it left the registry — "site visit", "audit", "court submission".</summary>
    public string? Purpose { get; set; }

    /// <summary>When it is expected back. What the overdue list is measured against.</summary>
    public DateOnly? DueBack { get; set; }

    public string? Remarks { get; set; }

    public string RecordedById { get; set; } = string.Empty;
    public string RecordedByName { get; set; } = string.Empty;

    /// <summary>Still out past the date it was promised back.</summary>
    public bool IsOverdueOn(DateOnly today) =>
        Action is FileMovementAction.Issued or FileMovementAction.Transferred
        && DueBack is { } due && due < today;
}
