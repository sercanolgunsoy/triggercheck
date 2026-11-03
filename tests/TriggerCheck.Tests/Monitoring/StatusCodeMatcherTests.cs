using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Tests.Monitoring;

public class StatusCodeMatcherTests
{
    [Theory]
    [InlineData("200-299,301", 200, true)]
    [InlineData("200-299,301", 299, true)]
    [InlineData("200-299,301", 301, true)]
    [InlineData("200-299,301", 302, false)]
    [InlineData("200-299,301", 404, false)]
    [InlineData(" 200 , 204 ", 204, true)]
    [InlineData("200,204", 201, false)]
    [InlineData("2xx", 250, true)]
    [InlineData("2XX,4xx", 404, true)]
    [InlineData("2xx", 300, false)]
    [InlineData("500", 500, true)]
    [InlineData("200 - 202", 201, true)]
    [InlineData("200,,301,", 301, true)]
    public void Matches_codes_ranges_and_classes(string spec, int code, bool expected) =>
        Assert.Equal(expected, StatusCodeMatcher.Parse(spec).IsMatch(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_spec_means_2xx(string? spec)
    {
        var m = StatusCodeMatcher.Parse(spec);
        Assert.True(m.IsMatch(200));
        Assert.True(m.IsMatch(299));
        Assert.False(m.IsMatch(301));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("299-200")]
    [InlineData("99")]
    [InlineData("600")]
    [InlineData("200-")]
    [InlineData("-200")]
    [InlineData("200-299-300")]
    [InlineData("+200")]
    [InlineData("7xx")]
    [InlineData(",")]
    public void Invalid_specs_are_rejected(string spec)
    {
        Assert.False(StatusCodeMatcher.TryParse(spec, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => StatusCodeMatcher.Parse(spec));
    }

    [Theory]
    [InlineData("200-299", false)]
    [InlineData("200-299,301", true)]
    [InlineData("3xx", true)]
    [InlineData("200-400", true)]
    [InlineData("400-599", false)]
    public void Detects_when_redirect_codes_are_expected(string spec, bool expected) =>
        Assert.Equal(expected, StatusCodeMatcher.Parse(spec).AcceptsRedirectStatus);

    [Fact]
    public void ToString_normalizes() => Assert.Equal("200-299,301", StatusCodeMatcher.Parse(" 200-299 , 301 ").ToString());
}
