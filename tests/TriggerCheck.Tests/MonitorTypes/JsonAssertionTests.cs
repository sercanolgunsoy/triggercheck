using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>The JSONPath subset parser and the assertion operators of JsonApi monitors.</summary>
public class JsonAssertionTests
{
    [Theory]
    [InlineData("$", "")]
    [InlineData("$.a", ".a")]
    [InlineData("$.a.b[0].c", ".a.b[0].c")]
    [InlineData("  $.items[12]  ", ".items[12]")]
    [InlineData("$['odd key'].x", ".odd key.x")]
    [InlineData("$[\"dq\"]", ".dq")]
    [InlineData("$[0][1]", "[0][1]")]
    [InlineData("$.a-b._c1", ".a-b._c1")]
    public void Parses_supported_paths(string path, string expected)
    {
        Assert.True(JsonPath.TryParse(path, out var segments, out var error), error);
        Assert.Equal(expected, string.Concat(segments.Select(s => s.ToString())));
    }

    [Theory]
    [InlineData(@"$['it\'s']", "it's")]
    [InlineData(@"$[""say \""hi\""""]", "say \"hi\"")]
    [InlineData(@"$['back\\slash']", @"back\slash")]
    [InlineData(@"$['dot.inside']", "dot.inside")]
    [InlineData(@"$['br[a]cket']", "br[a]cket")]
    [InlineData(@"$['été']", "été")]
    [InlineData(@"$['tab\there']", "tab\there")]
    public void Quoted_names_support_escapes(string path, string name)
    {
        Assert.True(JsonPath.TryParse(path, out var segments, out var error), error);
        Assert.Equal(name, Assert.Single(segments).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]               // must start with $
    [InlineData("$..a")]              // recursive descent
    [InlineData("$.*")]               // wildcard
    [InlineData("$[*]")]
    [InlineData("$.a[?(@.b > 1)]")]   // filter
    [InlineData("$.a[(@.length-1)]")] // script
    [InlineData("$.a[-1]")]           // negative index
    [InlineData("$.a[0:2]")]          // slice
    [InlineData("$.a[1,2]")]          // union
    [InlineData("$.a[")]
    [InlineData("$.a[0")]
    [InlineData("$['open")]
    [InlineData(@"$['bad\x']")]       // unknown escape
    [InlineData("$.")]
    [InlineData("$a")]
    [InlineData("$.a b")]
    [InlineData("$[99999999999]")]
    public void Rejects_unsupported_or_malformed_paths(string? path) =>
        Assert.False(JsonPath.TryParse(path, out _, out _));

    [Fact]
    public void Rejects_overly_long_paths()
    {
        Assert.False(JsonPath.TryParse("$" + string.Concat(Enumerable.Repeat(".a", 40)), out _, out var error));
        Assert.Contains("segments", error);
        Assert.False(JsonPath.TryParse("$." + new string('a', 600), out _, out _));
    }

    private const string Body = """
        {
          "status": "ok",
          "version": "1.2.3",
          "count": 42,
          "ratio": 0.1,
          "big": 12345678901234567890,
          "healthy": true,
          "missing": null,
          "items": [ { "name": "db", "latencyMs": 12.5 }, { "name": "cache", "latencyMs": 250 } ],
          "tags": ["eu", "prod"],
          "nested": { "a": { "b": [1, [2, 3]] } },
          "odd key": { "it's": "yes" },
          "unicode": "Ünïcödé"
        }
        """;

    private static string? Eval(string path, AssertionOperator op, string? expected = null) => JsonAssertion.Evaluate(Body, path, op, expected);

    [Theory]
    [InlineData("$.status", "ok")]
    [InlineData("$.count", "42")]
    [InlineData("$.count", "42.0")]
    [InlineData("$.ratio", "0.1")]
    [InlineData("$.ratio", "1e-1")]
    [InlineData("$.healthy", "true")]
    [InlineData("$.healthy", "TRUE")]
    [InlineData("$.missing", "null")]
    [InlineData("$.items[1].name", "cache")]
    [InlineData("$.items[0].latencyMs", "12.5")]
    [InlineData("$.nested.a.b[1][0]", "2")]
    [InlineData("$.tags", "[\"eu\", \"prod\"]")]
    [InlineData("$.nested.a", "{\"b\":[1,[2,3]]}")]
    [InlineData("$['odd key']['it\\'s']", "yes")]
    [InlineData("$.unicode", "Ünïcödé")]
    [InlineData("$.big", "12345678901234567890")]
    public void Equal_passes(string path, string expected) => Assert.Null(Eval(path, AssertionOperator.Equal, expected));

    [Theory]
    [InlineData("$.status", "OK")]          // strings are exact
    [InlineData("$.status", "ok ")]
    [InlineData("$.count", "41")]
    [InlineData("$.count", "forty-two")]
    [InlineData("$.healthy", "false")]
    [InlineData("$.missing", "")]
    [InlineData("$.tags", "[\"prod\", \"eu\"]")]
    [InlineData("$.nested.a", "not json")]
    public void Equal_fails_with_the_actual_value(string path, string expected)
    {
        var error = Eval(path, AssertionOperator.Equal, expected);
        Assert.NotNull(error);
        Assert.Contains("JSON value at " + path, error);
    }

    [Fact]
    public void Contains_on_strings_arrays_and_others()
    {
        Assert.Null(Eval("$.version", AssertionOperator.Contains, "1.2"));
        Assert.Null(Eval("$.status", AssertionOperator.Contains, "O")); // case-insensitive
        Assert.Null(Eval("$.tags", AssertionOperator.Contains, "prod"));
        Assert.NotNull(Eval("$.tags", AssertionOperator.Contains, "pro")); // array: element equality
        Assert.Null(Eval("$.nested.a.b", AssertionOperator.Contains, "1"));
        Assert.Null(Eval("$.items[0]", AssertionOperator.Contains, "\"db\""));
        Assert.NotNull(Eval("$.version", AssertionOperator.Contains, "2.0"));
    }

    [Fact]
    public void Exists_including_null_values()
    {
        Assert.Null(Eval("$.missing", AssertionOperator.Exists));
        Assert.Null(Eval("$", AssertionOperator.Exists));
        Assert.Null(Eval("$.items[1]", AssertionOperator.Exists));
        Assert.Equal("JSON path $.items[2] not found in response", Eval("$.items[2]", AssertionOperator.Exists));
        Assert.Equal("JSON path $.nope.deeper not found in response", Eval("$.nope.deeper", AssertionOperator.Exists));
    }

    [Theory]
    [InlineData("$.status[0]")]       // index into a string
    [InlineData("$.items.name")]      // member of an array
    [InlineData("$.count.value")]     // member of a number
    [InlineData("$.Status")]          // names are case-sensitive
    public void Wrong_shape_is_not_found(string path) =>
        Assert.Equal($"JSON path {path} not found in response", Eval(path, AssertionOperator.Equal, "x"));

    [Fact]
    public void Numeric_comparisons()
    {
        Assert.Null(Eval("$.items[1].latencyMs", AssertionOperator.LessThan, "300"));
        Assert.Null(Eval("$.items[1].latencyMs", AssertionOperator.GreaterThan, "249.99"));
        Assert.Null(Eval("$.ratio", AssertionOperator.LessThan, "1E0"));
        Assert.Equal("JSON value at $.items[1].latencyMs is 250 (expected < 250)", Eval("$.items[1].latencyMs", AssertionOperator.LessThan, "250"));
        Assert.Equal("JSON value at $.count is 42 (expected > 100)", Eval("$.count", AssertionOperator.GreaterThan, "100"));
        Assert.Equal("JSON value at $.status is \"ok\", not a number", Eval("$.status", AssertionOperator.GreaterThan, "1"));
        Assert.Equal("Expected value must be a number for < and >", Eval("$.count", AssertionOperator.LessThan, "abc"));
        Assert.Equal("Expected value must be a number for < and >", Eval("$.count", AssertionOperator.LessThan, "NaN"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>not json</html>")]
    [InlineData("{\"a\": 1")]
    [InlineData("{\"a\": 1} trailing")]
    public void Invalid_json_fails(string body) =>
        Assert.Equal("Response is not valid JSON", JsonAssertion.Evaluate(body, "$.a", AssertionOperator.Exists, null));

    [Fact]
    public void Deeply_nested_json_is_rejected_not_crashing()
    {
        var body = new string('[', 1000) + new string(']', 1000);
        Assert.Equal("Response is not valid JSON", JsonAssertion.Evaluate(body, "$[0]", AssertionOperator.Exists, null));
    }

    [Fact]
    public void Long_values_are_cut_in_messages()
    {
        var body = "{\"s\":\"" + new string('x', 5000) + "\"}";
        var error = JsonAssertion.Evaluate(body, "$.s", AssertionOperator.Equal, "y");
        Assert.NotNull(error);
        Assert.True(error!.Length < 300, error);
    }

    [Theory]
    [InlineData("$.a", AssertionOperator.Exists, null, true)]
    [InlineData("$.a", AssertionOperator.Equal, "", true)]
    [InlineData("$.a", AssertionOperator.Equal, null, false)]
    [InlineData("$..a", AssertionOperator.Exists, null, false)]
    [InlineData("$.a", AssertionOperator.GreaterThan, "1.5", true)]
    [InlineData("$.a", AssertionOperator.GreaterThan, "x", false)]
    public void Validates_configuration(string path, AssertionOperator op, string? expected, bool valid) =>
        Assert.Equal(valid, JsonAssertion.ValidateConfig(path, op, expected) is null);
}
