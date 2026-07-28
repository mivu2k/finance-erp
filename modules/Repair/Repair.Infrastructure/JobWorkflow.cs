using Repair.Domain;

namespace Repair.Infrastructure;

/// <summary>
/// The repair pipeline as a state machine, in one place. The Laravel app enforced
/// this only by convention in the controller; making it explicit is what stops a
/// delivered job being dragged back into diagnosis.
/// </summary>
public static class JobWorkflow
{
    private static readonly Dictionary<JobStatus, JobStatus[]> Allowed = new()
    {
        [JobStatus.Received] = [JobStatus.Diagnosing, JobStatus.Cancelled],
        [JobStatus.Diagnosing] = [JobStatus.WaitingApproval, JobStatus.InProgress, JobStatus.Cancelled],
        [JobStatus.WaitingApproval] = [JobStatus.InProgress, JobStatus.Diagnosing, JobStatus.Cancelled],
        [JobStatus.InProgress] = [JobStatus.Completed, JobStatus.Diagnosing, JobStatus.Cancelled],
        [JobStatus.Completed] = [JobStatus.Delivered, JobStatus.InProgress],
        // Terminal: a delivered or cancelled job is history.
        [JobStatus.Delivered] = [],
        [JobStatus.Cancelled] = []
    };

    public static IReadOnlyList<JobStatus> NextFrom(JobStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    public static bool CanMove(JobStatus from, JobStatus to) => NextFrom(from).Contains(to);

    public static void EnsureCanMove(JobStatus from, JobStatus to)
    {
        if (from == to)
            throw new InvalidOperationException($"The job is already {Describe(to)}.");
        if (!CanMove(from, to))
            throw new InvalidOperationException(
                $"A job that is {Describe(from)} can't move to {Describe(to)}.");
    }

    public static string Describe(JobStatus status) => status switch
    {
        JobStatus.Received => "received",
        JobStatus.Diagnosing => "in diagnosis",
        JobStatus.WaitingApproval => "awaiting approval",
        JobStatus.InProgress => "in progress",
        JobStatus.Completed => "completed",
        JobStatus.Delivered => "delivered",
        JobStatus.Cancelled => "cancelled",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Statuses that still count as work in the workshop. A List rather than an
    /// array on purpose: inside an EF predicate, <c>array.Contains</c> binds to the
    /// span overload, which the query translator can't evaluate.
    /// </summary>
    public static readonly List<JobStatus> Open =
    [
        JobStatus.Received, JobStatus.Diagnosing, JobStatus.WaitingApproval, JobStatus.InProgress
    ];
}
