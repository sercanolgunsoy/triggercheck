using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Escalation;

namespace TriggerCheck.Tests.Escalation;

/// <summary>FEAT-5: acknowledge-link tokens protected with ASP.NET Core Data Protection (purpose IncidentAck.v1).</summary>
public class AckLinkTests
{
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();

    private IncidentAckLinks Links(string? baseUrl = "https://monitor.example.test/", IDataProtectionProvider? keys = null) =>
        new(keys ?? _keys, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [IncidentAckLinks.BaseUrlSetting] = baseUrl }).Build(),
            Options.Create(new EscalationOptions()), NullLogger<IncidentAckLinks>.Instance);

    [Fact]
    public void Valid_token_round_trips_incident_and_customer()
    {
        var links = Links();
        var token = links.CreateToken(42, 7);
        Assert.True(links.TryValidate(token, out var t));
        Assert.Equal(new AckToken(42, 7), t);
        // Every protection is randomized: check the URL shape and that its token validates too.
        var url = links.CreateUrl(42, 7)!;
        const string prefix = "https://monitor.example.test/App/Incidents/Ack?token=";
        Assert.StartsWith(prefix, url);
        Assert.True(links.TryValidate(Uri.UnescapeDataString(url[prefix.Length..]), out var fromUrl));
        Assert.Equal(t, fromUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.2")]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_tokens_are_invalid(string? token) => Assert.False(Links().TryValidate(token, out _));

    [Fact]
    public void Tampered_tokens_and_other_key_rings_are_rejected()
    {
        var token = Links().CreateToken(42, 7);
        var middle = token.Length / 2;
        var flipped = token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..];
        Assert.False(Links().TryValidate(flipped, out var t));
        Assert.Null(t);
        Assert.False(Links(keys: new EphemeralDataProtectionProvider()).TryValidate(token, out _));
        // Same key ring but another purpose (e.g. a cookie or another feature's payload) does not validate either.
        var other = _keys.CreateProtector("Something.Else").ToTimeLimitedDataProtector().Protect(IncidentAckLinks.Payload(42, 7), DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(Links().TryValidate(other, out _));
    }

    [Fact]
    public void Expired_token_is_invalid()
    {
        var expired = _keys.CreateProtector(IncidentAckLinks.Purpose).ToTimeLimitedDataProtector()
            .Protect(IncidentAckLinks.Payload(42, 7), DateTimeOffset.UtcNow.AddSeconds(-1));
        var fresh = _keys.CreateProtector(IncidentAckLinks.Purpose).ToTimeLimitedDataProtector()
            .Protect(IncidentAckLinks.Payload(42, 7), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.False(Links().TryValidate(expired, out _));
        Assert.True(Links().TryValidate(fresh, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://monitor.example.test")]
    public void Without_a_usable_base_url_no_link_is_created(string? baseUrl) => Assert.Null(Links(baseUrl).CreateUrl(1, 1));
}
