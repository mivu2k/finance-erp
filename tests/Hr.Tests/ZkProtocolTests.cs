using System.Buffers.Binary;
using System.Text;
using Hr.Infrastructure.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hr.Tests;

/// <summary>
/// Covers the wire format in both directions. Decoding is reproduced from the
/// device's own packing rules and decoded back; the outbound checksum is pinned
/// against bytes captured from a real uFace terminal, because that is the half
/// that had no coverage and was wrong.
/// </summary>
public class ZkProtocolTests
{
    /// <summary>
    /// How the terminal packs a timestamp: nested remainders counting from 2000,
    /// with month and day stored zero-based.
    /// </summary>
    private static uint Encode(DateTime t) =>
        (uint)((((t.Year % 100) * 12 * 31 + (t.Month - 1) * 31 + t.Day - 1) * 24 * 60 * 60)
               + t.Hour * 3600 + t.Minute * 60 + t.Second);

    [Theory]
    [InlineData(2024, 1, 15, 9, 30, 0)]
    [InlineData(2026, 7, 28, 18, 5, 42)]
    [InlineData(2000, 1, 1, 0, 0, 0)]
    [InlineData(2031, 12, 31, 23, 59, 59)]
    public void DecodeTime_round_trips_the_device_encoding(
        int year, int month, int day, int hour, int minute, int second)
    {
        var expected = new DateTime(year, month, day, hour, minute, second);

        Assert.Equal(expected, ZkDeviceClient.DecodeTime(Encode(expected)));
    }

    [Fact]
    public void DecodeTime_returns_default_for_a_corrupt_value()
    {
        // A garbage word must not take down a whole sync.
        Assert.Equal(default, ZkDeviceClient.DecodeTime(uint.MaxValue));
    }

    [Fact]
    public void ParseAttendance_reads_the_40_byte_layout()
    {
        var punchedAt = new DateTime(2026, 7, 28, 8, 55, 0);
        var data = BuildRecord40("1042", punchedAt, verify: 1, status: 0);

        var records = ZkDeviceClient.ParseAttendance(data, NullLogger.Instance);

        var record = Assert.Single(records);
        Assert.Equal("1042", record.DeviceUserId);
        Assert.Equal(punchedAt, record.Timestamp);
        Assert.Equal(1, record.VerifyMode);
    }

    [Fact]
    public void ParseAttendance_reads_a_whole_batch()
    {
        var first = new DateTime(2026, 7, 28, 8, 55, 0);
        var second = new DateTime(2026, 7, 28, 17, 32, 0);

        var data = BuildRecord40("7", first, 1, 0)
            .Concat(BuildRecord40("1042", second, 15, 1))
            .ToArray();

        var records = ZkDeviceClient.ParseAttendance(data, NullLogger.Instance);

        Assert.Equal(2, records.Count);
        Assert.Equal("7", records[0].DeviceUserId);
        Assert.Equal("1042", records[1].DeviceUserId);
        Assert.Equal(second, records[1].Timestamp);
    }

    [Fact]
    public void ParseAttendance_skips_records_with_no_user_or_timestamp()
    {
        var good = BuildRecord40("5", new DateTime(2026, 7, 28, 9, 0, 0), 1, 0);
        var blank = new byte[40];

        var records = ZkDeviceClient.ParseAttendance(
            good.Concat(blank).ToArray(), NullLogger.Instance);

        Assert.Single(records);
    }

    [Fact]
    public void ParseAttendance_ignores_a_payload_of_unknown_width()
    {
        // 37 divides by neither 40 nor 16 — better to report nothing than garbage.
        Assert.Empty(ZkDeviceClient.ParseAttendance(new byte[37], NullLogger.Instance));
    }

    [Fact]
    public void ParseAttendance_handles_an_empty_log()
    {
        Assert.Empty(ZkDeviceClient.ParseAttendance([], NullLogger.Instance));
    }

    [Fact]
    public void ParseUsers_reads_the_72_byte_layout()
    {
        var data = new byte[72];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 3);
        data[2] = 0x00; // ordinary user, enabled
        Encoding.UTF8.GetBytes("Kamran Ali").CopyTo(data, 11);
        Encoding.UTF8.GetBytes("1042").CopyTo(data, 48);

        var users = ZkDeviceClient.ParseUsers(data, NullLogger.Instance);

        var user = Assert.Single(users);
        Assert.Equal("1042", user.DeviceUserId);
        Assert.Equal("Kamran Ali", user.Name);
        Assert.True(user.Enabled);
    }

    /// <summary>uid(2) userid(24) status(1) timestamp(4) punch(1) reserved(8)</summary>
    private static byte[] BuildRecord40(string userId, DateTime at, int verify, int status)
    {
        var record = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(record, 1);
        Encoding.UTF8.GetBytes(userId).CopyTo(record, 2);
        record[26] = (byte)status;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(27), Encode(at));
        record[31] = (byte)verify;
        return record;
    }

    // --- outbound framing -------------------------------------------------

    /// <summary>
    /// Captured from a real terminal at 192.168.19.231: this exact CMD_CONNECT
    /// frame was answered, and a checksum one less than this was silently dropped.
    /// A wrong checksum costs nothing at compile time and hangs at run time, so the
    /// value is pinned rather than recomputed by the same logic under test.
    /// </summary>
    [Theory]
    // command, sessionId, replyId, expected checksum
    [InlineData(1000, 0, 0, 0xFC17)]
    [InlineData(1000, 0, 1, 0xFC16)]
    public void Checksum_matches_what_the_terminal_accepts(
        ushort command, ushort session, ushort reply, int expected)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0), command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), session);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), reply);

        Assert.Equal((ushort)expected, ZkFraming.Checksum(packet));
    }

    [Fact]
    public void Checksum_folds_carries_rather_than_truncating_them()
    {
        // Words that overflow 16 bits on their own: the carry must come back in,
        // which is precisely where the original implementation lost a bit.
        var packet = new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };

        // 0xFFFF * 3 = 0x2FFFD -> fold -> 0xFFFD + 0x2 = 0xFFFF -> ~ = 0x0000.
        // Truncating instead of folding gives 0xFFFD here, and one less than the
        // right answer on ordinary packets.
        Assert.Equal(0x0000, ZkFraming.Checksum(packet));
    }

    [Fact]
    public void Checksum_ignores_whatever_is_already_in_the_checksum_field()
    {
        var clean = new byte[] { 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var dirty = new byte[] { 0xE8, 0x03, 0xAB, 0xCD, 0x00, 0x00, 0x00, 0x00 };

        Assert.Equal(ZkFraming.Checksum(clean), ZkFraming.Checksum(dirty));
    }
}
