using System.Buffers.Binary;
using System.Text;
using Hr.Infrastructure.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hr.Tests;

/// <summary>
/// Covers the wire-format decoding. This is the part that cannot be checked
/// against a real terminal from here, so the encoding is reproduced from the
/// device's own packing rules and decoded back.
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
}
