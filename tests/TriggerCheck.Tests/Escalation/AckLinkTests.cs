using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Monitoring.Escalation;

namespace TriggerCheck.Tests.Escalation;

/// <summary>FEAT-5: HMAC-signed acknowledge-link tokens.</summary>
public class AckLinkTests
{
    private const string Secret = "unit-test-secret-0123456789abcdef0123";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private IncidentAckLinks Links(string secret = Secret, string baseUrl = "https://monitor.example.test/", int hours = 72) =>
        new(Options.Create(new EscalationOptions { AckLinkSecret = secret, PublicBaseUrl = baseUrl, AckLinkValidHours = hours }), _time, NullLogger<IncidentAckLinks>.Instance);

    [Fact]
    public void Valid_token_round_trips_incident_and_customer()
    {
        var links = Links();
        var token = links.CreateToken(42, 7);
        Assert.Equal(AckTokenStatus.Valid, links.Validate(token, out var t));
        Assert.Equal(42, t!.IncidentId);
        Assert.Equal(7, t.CustomerId);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddHours(72), t.ExpiresAtUtc);
        Assert.Equal("https://monitor.example.test/App/Incidents/Ack?token=" + Uri.EscapeDataString(token), links.CreateUrl(42, 7));
    }

    [Theory]
    [InlineData(0)] // incident id
    [InlineData(1)] // customer id
    [InlineData(2)] // expiry
    public void Changing_any_signed_part_invalidates_the_token(int part)
    {
        var links = Links();
        var parts = links.CreateToken(42, 7).Split('.');
        parts[part] = (long.Parse(parts[part]) + 1).ToString();
        Assert.Equal(AckTokenStatus.Invalid, links.Validate(string.Join('.', parts), out var t));
        Assert.Null(t);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("-1.2.99999999999.AAAA")]
    [InlineData("1.2.3.!!notbase64!!")]
    public void Malformed_tokens_are_invalid(string? token) =>
        Assert.Equal(AckTokenStatus.Invalid, Links().Validate(token, out _));

    [Fact]
    public void Tampered_signature_and_other_keys_are_rejected()
    {
        var token = Links().CreateToken(42, 7);
        var flipped = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];
        Assert.Equal(AckTokenStatus.Invalid, Links().Validate(flipped, out _));
        Assert.Equal(AckTokenStatus.Invalid, Links("another-secret-0123456789abcdef012345").Validate(token, out _));
    }

    [Fact]
    public void Token_expires()
    {
        var links = Links(hours: 2);
        var token = links.CreateToken(42, 7);
        _time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(AckTokenStatus.Valid, links.Validate(token, out _));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AckTokenStatus.Expired, links.Validate(token, out var t));
        Assert.Null(t);
    }

    [Fact]
    public void Without_a_base_url_no_link_is_created_and_a_short_secret_falls_back_to_a_random_key()
    {
        Assert.Null(Links(baseUrl: "").CreateUrl(1, 1));
        Assert.Null(Links(baseUrl: "not a url").CreateUrl(1, 1));
        // Short secrets are ignored: two instances then have different random keys.
        var token = Links(secret: "short").CreateToken(1, 1);
        Assert.Equal(AckTokenStatus.Invalid, Links(secret: "short").Validate(token, out _));
    }
}
