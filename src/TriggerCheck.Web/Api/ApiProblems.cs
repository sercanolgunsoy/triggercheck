using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Web.Areas.App.Support;

namespace TriggerCheck.Web.Api;

/// <summary>RFC 7807 problem responses and request validation of the REST API (messages are English).</summary>
public static class ApiProblems
{
    public static Task Write(HttpContext http, int status, string title, string detail) =>
        Results.Problem(detail, statusCode: status, title: title).ExecuteAsync(http);

    public static IResult NotFound() => Results.Problem("The resource was not found.", statusCode: StatusCodes.Status404NotFound, title: "Not Found");

    public static IResult Conflict(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>Reads a JSON body; null (with <paramref name="error"/> set) for a missing / malformed body.</summary>
    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType()) return (null, UnsupportedMediaType());
        try
        {
            var value = await http.Request.ReadFromJsonAsync<T>(ct);
            return value is null ? (null, Invalid("", "A JSON object is required.")) : (value, null);
        }
        catch (JsonException ex)
        {
            // Path and position only: never echo the raw body.
            return (null, Invalid(ex.Path is { Length: > 1 } p ? p.TrimStart('$', '.') : "", "The JSON body is malformed or has a value of the wrong type."));
        }
        catch (InvalidOperationException)
        {
            // Unknown charset in the Content-Type (e.g. "application/json; charset=bogus").
            return (null, UnsupportedMediaType());
        }
    }

    private static IResult UnsupportedMediaType() =>
        Results.Problem("Send the body as JSON (Content-Type: application/json, UTF-8).",
            statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported Media Type");

    /// <summary>
    /// Same validation as the panel's monitor form: data annotations, the SSRF pre-check (IValidatableObject) and the
    /// plan / type rules of <see cref="MonitorInput.Validate(Plan, ModelStateDictionary, IStringLocalizer, string)"/>.
    /// Returns field (camelCase) → messages; empty when valid.
    /// </summary>
    public static Dictionary<string, string[]> ValidateMonitor(MonitorInput input, Plan plan, IServiceProvider services, IStringLocalizer L)
    {
        var ms = new ModelStateDictionary();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(input, new ValidationContext(input, services, null), results, validateAllProperties: true);
        foreach (var (member, message) in results.SelectMany(r => r.MemberNames.DefaultIfEmpty("").Select(m => (m, r.ErrorMessage))))
            ms.AddModelError(member, message ?? "Invalid value.");
        input.Validate(plan, ms, L, prefix: "");

        return ms.Where(e => e.Value is { Errors.Count: > 0 })
            .ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray());
    }
}
