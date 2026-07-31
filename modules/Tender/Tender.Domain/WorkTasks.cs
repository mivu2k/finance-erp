namespace Tender.Domain;

/// <summary>
/// One unit of work, hanging off either a tender or a project.
/// </summary>
/// <remarks>
/// Deliberately shared rather than duplicated per register: preparing a bid is as much
/// a checklist with owners and deadlines as running a project is — chase the bank
/// guarantee, collect the tax certificate, get the technical bid signed — and a second
/// near-identical entity would mean two boards, two services and two sets of rules
/// that drift apart.
/// <para>
/// Exactly one of <see cref="TenderRecordId"/> and <see cref="ProjectId"/> is set.
/// Two real foreign keys rather than a type/id pair, so cascade delete and the query
/// filters still work; <c>WorkTaskService</c> enforces the "exactly one" part, which
/// the database cannot express.
/// </para>
/// </remarks>
public class WorkTask : AuditableEntity
{
    public int? TenderRecordId { get; set; }
    public TenderRecord? TenderRecord { get; set; }

    public int? ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Identity user id plus a name snapshot, so the row reads after a rename.</summary>
    public string? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateOnly? CompletedDate { get; set; }

    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.NotStarted;
    public ProjectPriority Priority { get; set; } = ProjectPriority.Normal;

    /// <summary>0–100. Kept per task; a project's own figure averages these.</summary>
    public int ProgressPercent { get; set; }

    public decimal? EstimatedHours { get; set; }
    public decimal? ActualHours { get; set; }

    /// <summary>Manual ordering within its owner, so a work programme reads in sequence.</summary>
    public int SortOrder { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Past its due date and not finished. Takes the date rather than reading the clock:
    /// an entity that reads the machine clock is both untestable and, on a UTC server in
    /// a UTC+5 business, wrong for the first five hours of every day.
    /// </summary>
    public bool IsOverdueOn(DateOnly today) =>
        DueDate is { } due
        && Status is not (ProjectTaskStatus.Completed or ProjectTaskStatus.Cancelled)
        && due < today;

    public bool IsOpen =>
        Status is ProjectTaskStatus.NotStarted or ProjectTaskStatus.InProgress or ProjectTaskStatus.Blocked;
}

/// <summary>Which register a task hangs off. Mirrors <see cref="FileOwnerType"/>.</summary>
public enum WorkOwnerType
{
    Tender = 0,
    Project = 1
}
