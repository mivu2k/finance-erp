namespace Hr.Infrastructure.Devices;

/// <summary>
/// Wire-level constants for the ZKTeco terminal protocol (uFace 800, K40, F18 and
/// the rest of the standalone range).
/// </summary>
/// <remarks>
/// The vendor's own SDK — <c>zkemkeeper.dll</c> — is a 32-bit Windows COM library
/// and cannot be used from a Linux-hosted service, so the protocol is implemented
/// directly here. It is a stable, long-documented format: an 8-byte header
/// (command, checksum, session, reply) with an optional payload, wrapped over TCP
/// in a 8-byte framing header carrying a magic number and length.
/// </remarks>
internal static class ZkCommand
{
    public const ushort Connect = 1000;
    public const ushort Exit = 1001;
    public const ushort EnableDevice = 1002;
    public const ushort DisableDevice = 1003;
    public const ushort Restart = 1004;
    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckData = 2002;
    public const ushort AckUnauth = 2005;

    public const ushort PrepareData = 1500;
    public const ushort Data = 1501;
    public const ushort FreeData = 1502;
    public const ushort DataWrrq = 1503;
    public const ushort DataRdy = 1504;

    public const ushort Auth = 1102;
    public const ushort DeviceInfo = 11;
    public const ushort GetTime = 201;
    public const ushort AttLogRrq = 13;
    public const ushort ClearAttLog = 15;
    public const ushort UserTempRrq = 9;
}

/// <summary>The 8-byte TCP framing header that precedes every packet.</summary>
internal static class ZkFraming
{
    /// <summary>Magic prefix: 0x50 0x50 0x82 0x7d.</summary>
    public static readonly byte[] Magic = [0x50, 0x50, 0x82, 0x7d];
    public const int HeaderSize = 8;
}

/// <summary>A single attendance record as the terminal reports it.</summary>
public record ZkAttendanceRecord(
    string DeviceUserId,
    DateTime Timestamp,
    int VerifyMode,
    int Status);

/// <summary>A user enrolled on the terminal.</summary>
public record ZkUser(string DeviceUserId, string Name, int Privilege, bool Enabled);

/// <summary>What a connection test found.</summary>
public record ZkDeviceInfo(string? SerialNumber, string? FirmwareVersion,
    string? DeviceName, DateTime? DeviceTime, int UserCount, int RecordCount);
