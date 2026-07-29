using System.Globalization;
using Hr.Domain;
using Hr.Infrastructure.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hr.Infrastructure.Attendance;

/// <summary>
/// The ADMS ("push SDK") side of ZKTeco attendance: the terminal dials out to us
/// over HTTP and posts its records, instead of us reading them over TCP 4370.
/// </summary>
/// <remarks>
/// Needed because some firmware refuses SDK access outright — it answers
/// <c>ACK_UNAUTH</c> to every authentication attempt no matter what comm key is
/// set — and because a device behind NAT or on a branch network cannot be reached
/// inbound at all.
///
/// The device drives the conversation and is unforgiving about replies: it wants
/// bare text, and a malformed answer makes it drop the batch and retry forever.
/// Everything here answers in the exact shapes the firmware expects.
/// </remarks>
public interface IAdmsService
{
    /// <summary>
    /// First contact in each cycle. The terminal asks what to do; the reply is its
    /// configuration, including how often to call back and what to send.
    /// </summary>
    Task<string> HandshakeAsync(string serialNumber, string? remoteAddress,
        CancellationToken ct = default);

    /// <summary>
    /// A batch of records. <paramref name="table"/> is what the device is sending —
    /// only ATTLOG carries attendance; the rest is acknowledged and discarded.
    /// </summary>
    Task<AdmsUploadResult> UploadAsync(string serialNumber, string? table, string body,
        string? remoteAddress, CancellationToken ct = default);

    /// <summary>
    /// The terminal polling for work. We never push commands back, so this is a
    /// heartbeat — but it must be answered or the device treats us as down.
    /// </summary>
    Task<string> PollAsync(string serialNumber, string? remoteAddress,
        CancellationToken ct = default);
}

public record AdmsUploadResult(int Received, int Stored, string Reply);

public class AdmsService(
    HrDbContext db,
    IAttendanceSyncService sync,
    ILogger<AdmsService> logger) : IAdmsService
{
    /// <summary>Seconds between the device's polls when it has nothing to say.</summary>
    private const int PollDelaySeconds = 10;

    public async Task<string> HandshakeAsync(
        string serialNumber, string? remoteAddress, CancellationToken ct = default)
    {
        var device = await ResolveAsync(serialNumber, remoteAddress, ct);
        logger.LogInformation("ADMS handshake from {Serial} ({Device})", serialNumber, device.Name);

        // Stamps of 0 tell the terminal to send everything it holds; it keeps its
        // own log, and IngestAsync dedupes, so a full replay costs nothing and is
        // the only way to recover after a restore.
        return string.Join('\n',
        [
            $"GET OPTION FROM: {serialNumber}",
            "ATTLOGStamp=0",
            "OPERLOGStamp=0",
            "ATTPHOTOStamp=None",
            $"ErrorDelay={PollDelaySeconds * 3}",
            $"Delay={PollDelaySeconds}",
            "TransTimes=00:00;12:00",
            "TransInterval=1",
            "TransFlag=TransData AttLog",
            "Realtime=1",
            "Encrypt=0"
        ]) + "\n";
    }

    public async Task<AdmsUploadResult> UploadAsync(
        string serialNumber, string? table, string body, string? remoteAddress,
        CancellationToken ct = default)
    {
        var device = await ResolveAsync(serialNumber, remoteAddress, ct);

        // OPERLOG and friends are device housekeeping. Acknowledging without
        // storing keeps the terminal moving; refusing makes it retry forever.
        if (!string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase))
            return new AdmsUploadResult(0, 0, "OK");

        var records = ParseAttendance(body).ToList();
        if (records.Count == 0) return new AdmsUploadResult(0, 0, "OK");

        var result = await sync.IngestAsync(device, records, ct);

        device.LastSyncAtUtc = DateTime.UtcNow;
        device.LastSyncResult = result.Message;
        device.LastSyncPunchCount = result.PunchesNew;
        device.LastPunchAtUtc = records.Max(r => r.Timestamp);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("ADMS upload from {Serial}: {Read} read, {New} stored",
            serialNumber, result.PunchesRead, result.PunchesNew);

        // The device counts what it sent; answering with a different number makes
        // it resend the batch.
        return new AdmsUploadResult(records.Count, result.PunchesNew, $"OK: {records.Count}");
    }

    public async Task<string> PollAsync(
        string serialNumber, string? remoteAddress, CancellationToken ct = default)
    {
        await ResolveAsync(serialNumber, remoteAddress, ct);
        return "OK";
    }

    /// <summary>
    /// ATTLOG rows are tab-separated: user id, timestamp, status, verify mode, then
    /// fields we don't use. Firmware varies in how many trailing columns it sends,
    /// so anything past the fourth is ignored rather than required.
    /// </summary>
    internal static IEnumerable<ZkAttendanceRecord> ParseAttendance(string body)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 2) continue;

            var user = parts[0].Trim();
            if (string.IsNullOrEmpty(user)) continue;

            if (!DateTime.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var when))
                continue;

            var status = parts.Length > 2 && int.TryParse(parts[2], out var s) ? s : 0;
            var verify = parts.Length > 3 && int.TryParse(parts[3], out var v) ? v : 0;

            yield return new ZkAttendanceRecord(user, when, verify, status);
        }
    }

    /// <summary>
    /// Finds the terminal by serial, or records a new one as pending. An unknown
    /// device's punches are still stored: losing attendance while someone gets
    /// round to approving it would be worse than holding records from a terminal
    /// that turns out to be unwanted.
    /// </summary>
    private async Task<BiometricDevice> ResolveAsync(
        string serialNumber, string? remoteAddress, CancellationToken ct)
    {
        var serial = serialNumber.Trim();
        var device = await db.BiometricDevices
            .FirstOrDefaultAsync(d => d.SerialNumber == serial, ct);

        if (device is null)
        {
            device = new BiometricDevice
            {
                Name = $"Terminal {serial}",
                SerialNumber = serial,
                Host = remoteAddress ?? string.Empty,
                Mode = DeviceMode.Push,
                IsPendingApproval = true,
                IsEnabled = true
            };
            db.BiometricDevices.Add(device);
            logger.LogWarning(
                "Unknown terminal {Serial} announced itself from {Address} — " +
                "recorded as pending approval on /hr/devices", serial, remoteAddress);
        }

        device.Mode = DeviceMode.Push;
        device.LastContactAtUtc = DateTime.UtcNow;
        device.LastContactAddress = remoteAddress;

        await db.SaveChangesAsync(ct);
        return device;
    }
}
