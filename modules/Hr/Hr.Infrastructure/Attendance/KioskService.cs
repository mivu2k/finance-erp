using Hr.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hr.Infrastructure.Attendance;

/// <summary>
/// Turns something scanned at a station into a punch.
/// </summary>
/// <remarks>
/// One entry point for both readers, because to the browser they are
/// indistinguishable: an NFC reader and a QR scanner both act as keyboards that
/// type what they read and press Enter. What arrived is decided by looking at it,
/// not by which device sent it.
/// </remarks>
public interface IKioskService
{
    /// <summary>
    /// Records a punch for whatever was scanned. Never throws for an unrecognised
    /// scan — a queue of people waiting to clock in is no place for an exception.
    /// </summary>
    Task<KioskResult> ScanAsync(string stationToken, string scanned, CancellationToken ct = default);

    /// <summary>The station behind a kiosk URL, or null if the token is not current.</summary>
    Task<AttendanceStation?> ResolveStationAsync(string stationToken, CancellationToken ct = default);
}

/// <param name="Accepted">Whether a punch was recorded.</param>
/// <param name="EmployeeName">Who it was, when we know.</param>
/// <param name="Direction">What the punch was read as — first of the day in, later ones out.</param>
/// <param name="At">Local time recorded.</param>
/// <param name="Message">What to show on the kiosk.</param>
public record KioskResult(
    bool Accepted, string? EmployeeName, PunchDirection Direction, DateTime? At, string Message);

public class KioskService(
    HrDbContext db,
    IAttendanceTokenService tokens,
    IAttendanceSyncService sync,
    ILogger<KioskService> logger) : IKioskService
{
    /// <summary>
    /// Ignore a repeat scan of the same person inside this window. Card readers
    /// fire twice on a slow swipe and people re-present when unsure, and a stray
    /// second punch changes a derived day.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(45);

    public Task<AttendanceStation?> ResolveStationAsync(
        string stationToken, CancellationToken ct = default) =>
        db.AttendanceStations
            .FirstOrDefaultAsync(s => s.AccessToken == stationToken && s.IsEnabled, ct);

    public async Task<KioskResult> ScanAsync(
        string stationToken, string scanned, CancellationToken ct = default)
    {
        var station = await ResolveStationAsync(stationToken, ct);
        if (station is null)
            return Rejected("This station is not recognised. Ask an administrator to re-issue its link.");

        scanned = scanned.Trim();
        if (scanned.Length == 0) return Rejected("Nothing scanned.");

        var (employee, method, evidence) = await IdentifyAsync(scanned, ct);
        if (employee is null)
            return Rejected(method == PunchMethod.QrCode
                ? "That code has expired. Let your screen refresh and try again."
                : "Card not recognised. Ask HR to register it against your record.");

        var now = DateTime.Now;

        var recent = await db.AttendancePunches
            .Where(p => p.EmployeeId == employee.Id && p.PunchedAt > now - Debounce)
            .OrderByDescending(p => p.PunchedAt)
            .FirstOrDefaultAsync(ct);

        if (recent is not null)
            return new KioskResult(false, employee.FullName, recent.Direction, recent.PunchedAt,
                $"Already recorded at {recent.PunchedAt:HH:mm}. You're set.");

        // First scan of the day reads as an arrival, anything later as a departure.
        // The authoritative reading is still done when the day is rebuilt — this is
        // only so the kiosk can tell the person something useful.
        var today = DateOnly.FromDateTime(now);
        var earlier = await db.AttendancePunches.CountAsync(
            p => p.EmployeeId == employee.Id
                 && p.PunchedAt >= today.ToDateTime(TimeOnly.MinValue)
                 && p.PunchedAt < today.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);

        var direction = earlier == 0 ? PunchDirection.In : PunchDirection.Out;

        db.AttendancePunches.Add(new AttendancePunch
        {
            AttendanceStationId = station.Id,
            EmployeeId = employee.Id,
            PunchedAt = now,
            Direction = direction,
            Method = method,
            Evidence = evidence
        });

        station.LastPunchAtUtc = DateTime.UtcNow;
        station.LastPunchDescription = $"{employee.FullName} — {direction} at {now:HH:mm}";

        await db.SaveChangesAsync(ct);
        await sync.RebuildAsync(today, today, employee.Id, ct);

        logger.LogInformation("Kiosk punch: {Employee} {Direction} at {Station} via {Method}",
            employee.FullName, direction, station.Name, method);

        return new KioskResult(true, employee.FullName, direction, now,
            direction == PunchDirection.In ? "Welcome in" : "Goodbye");
    }

    /// <summary>
    /// Works out who scanned. A rotating token announces itself by its prefix;
    /// anything else is treated as a card UID, which is what an NFC reader types.
    /// </summary>
    private async Task<(Employee? Employee, PunchMethod Method, string? Evidence)> IdentifyAsync(
        string scanned, CancellationToken ct)
    {
        if (tokens.Parse(scanned) is { } token)
        {
            var employee = await db.Employees
                .FirstOrDefaultAsync(e => e.Id == token.EmployeeId, ct);

            if (employee?.QrSecret is null || !tokens.Verify(token, employee.QrSecret))
                return (null, PunchMethod.QrCode, null);

            // The step, never the token: storing the token would store a credential.
            return (employee, PunchMethod.QrCode, $"qr step {token.Step}");
        }

        var byCard = await db.Employees
            .FirstOrDefaultAsync(e => e.CardNumber == scanned, ct);

        return (byCard, PunchMethod.Card, byCard is null ? null : $"card {scanned}");
    }

    private static KioskResult Rejected(string message) =>
        new(false, null, PunchDirection.Unspecified, null, message);
}
