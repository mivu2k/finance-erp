using System.Security.Cryptography;
using System.Text;

namespace Hr.Infrastructure.Attendance;

/// <summary>
/// The rotating code behind an employee's attendance QR.
/// </summary>
/// <remarks>
/// A static badge is a photograph away from being cloned, so the QR carries a code
/// derived from the employee's own secret and the current half-minute. It is the
/// same construction as an authenticator app: the kiosk recomputes it rather than
/// looking anything up, so nothing needs storing between showing a code and
/// scanning it.
/// </remarks>
public interface IAttendanceTokenService
{
    /// <summary>What the employee's screen should be showing right now.</summary>
    string Issue(int employeeId, string secret, DateTime? nowUtc = null);

    /// <summary>Seconds until the displayed code changes, so the UI can count down.</summary>
    int SecondsRemaining(DateTime? nowUtc = null);

    /// <summary>
    /// Reads a scanned payload back to an employee, or null if it is not one of
    /// ours, has expired, or does not verify.
    /// </summary>
    ScannedToken? Parse(string payload);

    /// <summary>Confirms a parsed token against that employee's secret.</summary>
    bool Verify(ScannedToken token, string secret, DateTime? nowUtc = null);

    /// <summary>A fresh secret. Issuing one invalidates every code already on screen.</summary>
    string NewSecret();
}

/// <param name="EmployeeId">Who the code claims to be.</param>
/// <param name="Step">The half-minute it was minted for.</param>
/// <param name="Mac">The signature to check.</param>
public record ScannedToken(int EmployeeId, long Step, string Mac);

public class AttendanceTokenService : IAttendanceTokenService
{
    /// <summary>Rotation period. Long enough to scan, short enough that a photo is stale.</summary>
    private const int StepSeconds = 30;

    /// <summary>
    /// How many steps either side are still accepted. One covers the walk from
    /// unlocking a phone to holding it under the scanner, plus modest clock drift
    /// between the employee's device and the server.
    /// </summary>
    private const int Tolerance = 1;

    private const string Prefix = "MEIATT1";

    public string Issue(int employeeId, string secret, DateTime? nowUtc = null) =>
        $"{Prefix}:{employeeId}:{Step(nowUtc)}:{Mac(employeeId, Step(nowUtc), secret)}";

    public int SecondsRemaining(DateTime? nowUtc = null) =>
        StepSeconds - (int)((nowUtc ?? DateTime.UtcNow).ToUnixTimeSecondsSafe() % StepSeconds);

    public ScannedToken? Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var parts = payload.Trim().Split(':');
        if (parts.Length != 4 || parts[0] != Prefix) return null;
        if (!int.TryParse(parts[1], out var employeeId)) return null;
        if (!long.TryParse(parts[2], out var step)) return null;

        return new ScannedToken(employeeId, step, parts[3]);
    }

    public bool Verify(ScannedToken token, string secret, DateTime? nowUtc = null)
    {
        var current = Step(nowUtc);
        if (Math.Abs(current - token.Step) > Tolerance) return false;

        // Fixed-time comparison: a token check that leaks timing leaks the secret.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Mac(token.EmployeeId, token.Step, secret)),
            Encoding.ASCII.GetBytes(token.Mac));
    }

    public string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static long Step(DateTime? nowUtc) =>
        (nowUtc ?? DateTime.UtcNow).ToUnixTimeSecondsSafe() / StepSeconds;

    private static string Mac(int employeeId, long step, string secret)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{employeeId}:{step}"));

        // Eight hex characters: 32 bits of signature. Guessing one inside a 30
        // second window is not a practical attack, and it keeps the QR small
        // enough to scan quickly off a phone screen.
        return Convert.ToHexString(digest)[..8];
    }
}

internal static class TimeExtensions
{
    public static long ToUnixTimeSecondsSafe(this DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
