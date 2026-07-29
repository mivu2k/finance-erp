using Hr.Infrastructure.Attendance;
using Xunit;

namespace Hr.Tests;

/// <summary>
/// The rotating attendance code is the only thing standing between a photograph of
/// someone's screen and a punch in their name, so the properties that make it worth
/// having — it expires, it is bound to one person, and it cannot be forged without
/// the secret — are asserted rather than assumed.
/// </summary>
public class AttendanceTokenTests
{
    private static readonly DateTime Noon = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly AttendanceTokenService _tokens = new();
    private readonly string _secret;

    public AttendanceTokenTests() => _secret = _tokens.NewSecret();

    [Fact]
    public void A_freshly_issued_code_verifies()
    {
        var token = _tokens.Parse(_tokens.Issue(42, _secret, Noon));

        Assert.NotNull(token);
        Assert.Equal(42, token!.EmployeeId);
        Assert.True(_tokens.Verify(token, _secret, Noon));
    }

    [Theory]
    [InlineData(29)]    // still inside the same half-minute
    [InlineData(-29)]
    [InlineData(45)]    // one step late: the walk to the scanner
    [InlineData(-30)]   // one step early: the phone's clock running behind
    public void Stays_valid_across_the_tolerated_window(int offsetSeconds)
    {
        var token = _tokens.Parse(_tokens.Issue(42, _secret, Noon))!;

        Assert.True(_tokens.Verify(token, _secret, Noon.AddSeconds(offsetSeconds)));
    }

    [Theory]
    [InlineData(120)]     // two minutes later
    [InlineData(-120)]
    [InlineData(86_400)]  // a screenshot from yesterday
    public void Expires_outside_it(int offsetSeconds)
    {
        var token = _tokens.Parse(_tokens.Issue(42, _secret, Noon))!;

        Assert.False(_tokens.Verify(token, _secret, Noon.AddSeconds(offsetSeconds)));
    }

    [Fact]
    public void Will_not_verify_against_another_employees_secret()
    {
        var token = _tokens.Parse(_tokens.Issue(42, _secret, Noon))!;

        Assert.False(_tokens.Verify(token, _tokens.NewSecret(), Noon));
    }

    [Fact]
    public void Reissuing_the_secret_invalidates_codes_already_on_screen()
    {
        var onScreen = _tokens.Parse(_tokens.Issue(42, _secret, Noon))!;

        // What happens when someone reports a lost phone.
        var replacement = _tokens.NewSecret();

        Assert.False(_tokens.Verify(onScreen, replacement, Noon));
    }

    [Fact]
    public void One_employees_code_cannot_be_replayed_as_another()
    {
        // Same secret, different claimed identity: the signature covers both.
        var forged = _tokens.Parse(_tokens.Issue(42, _secret, Noon))! with { EmployeeId = 99 };

        Assert.False(_tokens.Verify(forged, _secret, Noon));
    }

    [Fact]
    public void A_tampered_signature_is_rejected()
    {
        var token = _tokens.Parse(_tokens.Issue(42, _secret, Noon))!;
        var tampered = token with { Mac = new string('0', token.Mac.Length) };

        Assert.False(_tokens.Verify(tampered, _secret, Noon));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1042")]                       // an NFC card, not a code
    [InlineData("MEIATT1:42:1")]               // truncated
    [InlineData("OTHER:42:1:abcdef01")]        // someone else's QR entirely
    [InlineData("MEIATT1:notanumber:1:abcd")]
    public void Reads_nothing_out_of_a_payload_that_is_not_ours(string payload)
    {
        Assert.Null(_tokens.Parse(payload));
    }

    [Fact]
    public void Two_employees_at_the_same_instant_get_different_codes()
    {
        Assert.NotEqual(_tokens.Issue(1, _secret, Noon), _tokens.Issue(2, _secret, Noon));
    }

    [Fact]
    public void The_code_changes_when_the_window_does()
    {
        Assert.NotEqual(
            _tokens.Issue(42, _secret, Noon),
            _tokens.Issue(42, _secret, Noon.AddSeconds(30)));
    }

    [Fact]
    public void The_countdown_matches_the_rotation()
    {
        Assert.Equal(30, _tokens.SecondsRemaining(Noon));
        Assert.Equal(20, _tokens.SecondsRemaining(Noon.AddSeconds(10)));
        Assert.Equal(1, _tokens.SecondsRemaining(Noon.AddSeconds(29)));
    }
}
