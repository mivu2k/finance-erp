using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hr.Infrastructure.Devices;

public interface IZkDeviceClient
{
    /// <summary>Connects, reads identity and counters, disconnects. Used by "Test connection".</summary>
    Task<ZkDeviceInfo> GetInfoAsync(string host, int port, int commKey, CancellationToken ct = default);

    /// <summary>Reads the whole on-board attendance log.</summary>
    Task<IReadOnlyList<ZkAttendanceRecord>> GetAttendanceAsync(
        string host, int port, int commKey, CancellationToken ct = default);

    /// <summary>Reads enrolled users, so HR can match device ids to people.</summary>
    Task<IReadOnlyList<ZkUser>> GetUsersAsync(
        string host, int port, int commKey, CancellationToken ct = default);

    /// <summary>Wipes the terminal's log. Irreversible — only after a successful pull.</summary>
    Task ClearAttendanceAsync(string host, int port, int commKey, CancellationToken ct = default);
}

/// <summary>
/// Talks to a ZKTeco terminal over TCP 4370, implementing the protocol directly so
/// it runs on Linux (the vendor SDK is Windows-only COM).
/// </summary>
/// <remarks>
/// The device is disabled for the duration of a read — that's the vendor's own
/// pattern, and it stops the log shifting underneath a paged download. It is always
/// re-enabled in a finally block: leaving a terminal disabled would lock staff out
/// at the door.
/// </remarks>
public class ZkDeviceClient(ILogger<ZkDeviceClient> logger) : IZkDeviceClient
{
    private const int DefaultTimeoutMs = 15_000;

    public async Task<ZkDeviceInfo> GetInfoAsync(
        string host, int port, int commKey, CancellationToken ct = default)
    {
        using var session = await ZkSession.OpenAsync(host, port, commKey, logger, ct);

        var serial = await session.ReadStringParamAsync("~SerialNumber", ct);
        var firmware = await session.ReadFirmwareAsync(ct);
        var name = await session.ReadStringParamAsync("~DeviceName", ct);
        var time = await session.ReadTimeAsync(ct);
        var users = await session.ReadIntParamAsync("UserCounts", ct);
        var records = await session.ReadIntParamAsync("AttLogCounts", ct);

        return new ZkDeviceInfo(serial, firmware, name, time, users ?? 0, records ?? 0);
    }

    public async Task<IReadOnlyList<ZkAttendanceRecord>> GetAttendanceAsync(
        string host, int port, int commKey, CancellationToken ct = default)
    {
        using var session = await ZkSession.OpenAsync(host, port, commKey, logger, ct);

        await session.DisableDeviceAsync(ct);
        try
        {
            var data = await session.ReadDataAsync(ZkCommand.AttLogRrq, ct);
            return ParseAttendance(data, logger);
        }
        finally
        {
            await session.EnableDeviceAsync(CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<ZkUser>> GetUsersAsync(
        string host, int port, int commKey, CancellationToken ct = default)
    {
        using var session = await ZkSession.OpenAsync(host, port, commKey, logger, ct);

        await session.DisableDeviceAsync(ct);
        try
        {
            var data = await session.ReadDataAsync(ZkCommand.UserTempRrq, ct, payload: [0x05]);
            return ParseUsers(data, logger);
        }
        finally
        {
            await session.EnableDeviceAsync(CancellationToken.None);
        }
    }

    public async Task ClearAttendanceAsync(
        string host, int port, int commKey, CancellationToken ct = default)
    {
        using var session = await ZkSession.OpenAsync(host, port, commKey, logger, ct);

        await session.DisableDeviceAsync(ct);
        try
        {
            await session.CommandAsync(ZkCommand.ClearAttLog, ct);
        }
        finally
        {
            await session.EnableDeviceAsync(CancellationToken.None);
        }
    }

    // --- record parsing ---

    /// <summary>
    /// Attendance records come back as a flat array of fixed-size structs. Firmware
    /// generations differ in width — 40 bytes on modern devices like the uFace 800,
    /// 16 on older ones — so the layout is chosen from the payload length.
    /// </summary>
    internal static List<ZkAttendanceRecord> ParseAttendance(byte[] data, ILogger logger)
    {
        var records = new List<ZkAttendanceRecord>();
        if (data.Length == 0) return records;

        var size = data.Length % 40 == 0 ? 40
                 : data.Length % 16 == 0 ? 16
                 : 0;

        if (size == 0)
        {
            logger.LogWarning(
                "Attendance payload of {Bytes} bytes matches no known record layout", data.Length);
            return records;
        }

        for (var offset = 0; offset + size <= data.Length; offset += size)
        {
            var span = data.AsSpan(offset, size);

            string userId;
            uint stamp;
            int verify, status;

            if (size == 40)
            {
                // uid(2) userid(24, NUL-padded) status(1) timestamp(4) punch(1) reserved(8)
                userId = ReadCString(span.Slice(2, 24));
                status = span[26];
                stamp = BitConverter.ToUInt32(span.Slice(27, 4));
                verify = span[31];
            }
            else
            {
                // uid(2) userid(4) status(1) timestamp(4) ... on older firmware
                userId = BitConverter.ToUInt16(span[..2]).ToString();
                status = span[4];
                stamp = BitConverter.ToUInt32(span.Slice(5, 4));
                verify = -1;
            }

            if (string.IsNullOrWhiteSpace(userId) || stamp == 0) continue;

            records.Add(new ZkAttendanceRecord(
                userId.Trim(), DecodeTime(stamp), verify, status));
        }

        return records;
    }

    internal static List<ZkUser> ParseUsers(byte[] data, ILogger logger)
    {
        var users = new List<ZkUser>();
        if (data.Length == 0) return users;

        // 72 bytes on modern firmware; 28 on the older compact layout.
        var size = data.Length % 72 == 0 ? 72
                 : data.Length % 28 == 0 ? 28
                 : 0;

        if (size == 0)
        {
            logger.LogWarning("User payload of {Bytes} bytes matches no known layout", data.Length);
            return users;
        }

        for (var offset = 0; offset + size <= data.Length; offset += size)
        {
            var span = data.AsSpan(offset, size);

            if (size == 72)
            {
                // uid(2) privilege(1) password(8) name(24) card(4) group(1) pad(1)
                // userid(9) ... trailing reserved
                var privilege = span[2];
                var name = ReadCString(span.Slice(11, 24));
                var userId = ReadCString(span.Slice(48, 9));
                if (string.IsNullOrWhiteSpace(userId)) continue;

                users.Add(new ZkUser(userId.Trim(), name.Trim(),
                    privilege & 0x07, (privilege & 0x80) == 0));
            }
            else
            {
                var uid = BitConverter.ToUInt16(span[..2]);
                var privilege = span[2];
                var name = ReadCString(span.Slice(11, 8));
                users.Add(new ZkUser(uid.ToString(), name.Trim(),
                    privilege & 0x07, (privilege & 0x80) == 0));
            }
        }

        return users;
    }

    /// <summary>
    /// The device packs a timestamp into one integer as a series of nested
    /// remainders, counting from the year 2000 in local device time.
    /// </summary>
    internal static DateTime DecodeTime(uint value)
    {
        var second = (int)(value % 60); value /= 60;
        var minute = (int)(value % 60); value /= 60;
        var hour = (int)(value % 24); value /= 24;
        var day = (int)(value % 31) + 1; value /= 31;
        var month = (int)(value % 12) + 1; value /= 12;
        var year = (int)value + 2000;

        // The year is carried as a two-digit offset from 2000, so anything past
        // 2099 is a corrupt word rather than a date. Without this check a garbage
        // record decodes to a perfectly constructible date far in the future and
        // gets stored as a real punch.
        if (year is < 2000 or > 2099) return default;

        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A corrupt record shouldn't take down a whole sync.
            return default;
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> span)
    {
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }
}
