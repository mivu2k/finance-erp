namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// The one source of "now" and "today" for the whole platform.
/// </summary>
/// <remarks>
/// Two bugs this exists to kill.
/// <para>
/// <b>The wrong day.</b> Code was split between <c>DateTime.Today</c> (the server's
/// local date) and <c>DateOnly.FromDateTime(DateTime.UtcNow)</c> (the UTC date). In
/// Asia/Karachi those disagree from midnight until 05:00 every single day, so anything
/// date-bounded — an attendance day, a leave request, an overdue task, a guarantee
/// expiry, a file due back — could be off by one for five hours a night, depending
/// only on which line of code happened to compute it.
/// </para>
/// <para>
/// <b>Untestable time.</b> A test that reads the machine clock passes or fails by when
/// it runs. Injecting the clock means "overdue yesterday" is a fixture, not a race.
/// </para>
/// <para>
/// Timestamps stay UTC — <see cref="UtcNow"/> is what audit columns are stamped with.
/// It is only the <em>calendar date</em> that is a business concept and must be read
/// in the business's own timezone, which is what <see cref="Today"/> returns.
/// </para>
/// </remarks>
public interface IBusinessClock
{
    /// <summary>Instant, always UTC. What audit and event timestamps are stamped with.</summary>
    DateTime UtcNow { get; }

    /// <summary>The business's calendar date — the answer to "what day is it here?".</summary>
    DateOnly Today { get; }

    /// <summary>Wall-clock time in the business timezone, for shift and attendance arithmetic.</summary>
    DateTime LocalNow { get; }

    /// <summary>The timezone the business runs in.</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>Converts a stored UTC timestamp into business-local wall time for display.</summary>
    DateTime ToLocal(DateTime utc);
}

/// <summary>
/// Reads the machine clock and converts into the configured business timezone.
/// </summary>
/// <param name="timeZoneId">
/// An IANA id such as <c>Asia/Karachi</c>. Falls back to the machine's local zone when
/// the id is unknown — a container missing tzdata should degrade rather than refuse to
/// start, and the fallback is the same zone the code used before this type existed.
/// </param>
public class BusinessClock(string? timeZoneId = null) : IBusinessClock
{
    public TimeZoneInfo TimeZone { get; } = Resolve(timeZoneId);

    private static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone);

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            TimeZone);
}

/// <summary>
/// A clock frozen at a chosen instant, for tests. Lets "due yesterday" be a fixture
/// rather than something that depends on when the suite happens to run.
/// </summary>
public class FixedClock(DateTime utcNow, TimeZoneInfo? zone = null) : IBusinessClock
{
    public TimeZoneInfo TimeZone { get; } = zone ?? TimeZoneInfo.Utc;

    public DateTime UtcNow { get; private set; } =
        utcNow.Kind == DateTimeKind.Utc ? utcNow : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone);
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            TimeZone);

    /// <summary>Moves the clock, so a test can watch something become overdue.</summary>
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
