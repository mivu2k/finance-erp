using Hr.Infrastructure.Attendance;
using Xunit;

namespace Hr.Tests;

/// <summary>
/// ADMS uploads arrive as tab-separated text from firmware that varies in how many
/// trailing columns it bothers to send. A row we misread is a punch silently lost,
/// so the parser is deliberately forgiving about extras and strict about the two
/// fields that matter.
/// </summary>
public class AdmsParsingTests
{
    [Fact]
    public void Reads_the_standard_seven_column_row()
    {
        var body = "1\t2026-07-29 08:31:04\t0\t1\t0\t0\t0\n";

        var r = Assert.Single(AdmsService.ParseAttendance(body));

        Assert.Equal("1", r.DeviceUserId);
        Assert.Equal(new DateTime(2026, 7, 29, 8, 31, 4), r.Timestamp);
        Assert.Equal(0, r.Status);
        Assert.Equal(1, r.VerifyMode);
    }

    [Fact]
    public void Reads_a_row_that_stops_after_the_timestamp()
    {
        // Older firmware sends only what it must.
        var r = Assert.Single(AdmsService.ParseAttendance("42\t2026-07-29 17:02:00"));

        Assert.Equal("42", r.DeviceUserId);
        Assert.Equal(new DateTime(2026, 7, 29, 17, 2, 0), r.Timestamp);
    }

    [Fact]
    public void Reads_a_whole_batch_and_keeps_every_row()
    {
        var body = string.Join('\n',
            "1\t2026-07-29 08:31:04\t0\t1\t0\t0\t0",
            "2\t2026-07-29 08:33:11\t0\t1\t0\t0\t0",
            "1\t2026-07-29 17:05:22\t1\t1\t0\t0\t0");

        var records = AdmsService.ParseAttendance(body).ToList();

        Assert.Equal(3, records.Count);
        Assert.Equal(["1", "2", "1"], records.Select(r => r.DeviceUserId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("garbage")]                            // no tab at all
    [InlineData("1\tnot-a-date\t0\t1")]                // unparseable timestamp
    [InlineData("\t2026-07-29 08:31:04\t0\t1")]        // no user id
    public void Drops_rows_it_cannot_trust(string body)
    {
        Assert.Empty(AdmsService.ParseAttendance(body));
    }

    [Fact]
    public void A_bad_row_does_not_take_the_good_ones_with_it()
    {
        // One corrupt line in a batch of 500 must not cost the other 499.
        var body = string.Join('\n',
            "1\t2026-07-29 08:31:04\t0\t1",
            "2\tnonsense\t0\t1",
            "3\t2026-07-29 08:35:00\t0\t1");

        var records = AdmsService.ParseAttendance(body).ToList();

        Assert.Equal(["1", "3"], records.Select(r => r.DeviceUserId));
    }

    [Fact]
    public void Tolerates_carriage_returns_and_padding()
    {
        var body = "  7 \t 2026-07-29 09:00:00 \t0\t1\r\n";

        var r = Assert.Single(AdmsService.ParseAttendance(body));

        Assert.Equal("7", r.DeviceUserId);
        Assert.Equal(new DateTime(2026, 7, 29, 9, 0, 0), r.Timestamp);
    }
}
