namespace Tender.Domain;

/// <summary>
/// Where a project has got to. <see cref="OnHold"/> is deliberately separate from
/// <see cref="Cancelled"/> — work that stopped and may resume is not work that was
/// abandoned, and the two report very differently.
/// </summary>
public enum ProjectStatus
{
    Planned = 0,
    Active = 1,
    OnHold = 2,
    Completed = 3,
    Cancelled = 4
}

public enum ProjectPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>
/// A task's position in its own small workflow. <see cref="Blocked"/> exists because
/// a task waiting on someone else is not the same as one nobody has started, and the
/// board needs to show the difference.
/// </summary>
public enum ProjectTaskStatus
{
    NotStarted = 0,
    InProgress = 1,
    Blocked = 2,
    Completed = 3,
    Cancelled = 4
}

public enum MilestoneStatus
{
    Pending = 0,
    Achieved = 1,
    Missed = 2,
    Cancelled = 3
}

/// <summary>
/// A piece of work the organisation is running, tracked through its tasks and
/// milestones. Projects here are **standalone** — they are deliberately not linked to
/// <see cref="TenderRecord"/>, because plenty of work never went to tender and a
/// project's own schedule has nothing to do with a bid's. The two registers share
/// this module and nothing else.
/// </summary>
public class Project : AuditableEntity
{
    public string ProjectCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Client { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }

    /// <summary>Identity user id of the manager, plus a display-name snapshot — no cross-database FK.</summary>
    public string? ManagerUserId { get; set; }
    public string? ManagerName { get; set; }

    public decimal? ContractValue { get; set; }
    public decimal? Budget { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? TargetEndDate { get; set; }
    public DateOnly? ActualEndDate { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Planned;
    public ProjectPriority Priority { get; set; } = ProjectPriority.Normal;

    public string? ContactPerson { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public string? Notes { get; set; }

    public List<WorkTask> Tasks { get; set; } = [];
    public List<ProjectMilestone> Milestones { get; set; } = [];

    /// <summary>
    /// Completion measured from the tasks, not typed in — a stored percentage and a
    /// task list disagree the moment either moves. Cancelled tasks are excluded
    /// rather than counted as done: dropping work should not flatter the figure.
    /// </summary>
    public int ProgressPercent
    {
        get
        {
            var counted = Tasks.Where(t => t.Status != ProjectTaskStatus.Cancelled).ToList();
            return counted.Count == 0 ? 0 : (int)Math.Round(counted.Average(t => (double)t.ProgressPercent));
        }
    }

    public bool IsOpen => Status is ProjectStatus.Planned or ProjectStatus.Active or ProjectStatus.OnHold;
}

/// <summary>
/// A dated checkpoint on a project — handover, an inspection, a payment stage.
/// Separate from a task because a milestone is a date that is met or missed, not
/// work that is carried out, and it is what progress is reported against.
/// </summary>
public class ProjectMilestone : AuditableEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateOnly DueDate { get; set; }
    public DateOnly? AchievedDate { get; set; }

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Pending;

    /// <summary>Set when the milestone releases a payment stage.</summary>
    public decimal? PaymentAmount { get; set; }

    public int SortOrder { get; set; }
    public string? Notes { get; set; }

    /// <summary>Takes the date rather than reading the clock — see <see cref="WorkTask.IsOverdueOn"/>.</summary>
    public bool IsOverdueOn(DateOnly today) =>
        Status == MilestoneStatus.Pending && DueDate < today;
}
