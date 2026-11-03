using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TriggerCheck.Web.Areas.SuperAdmin.Services;

/// <summary>
/// Binds decimals independent of the server culture: accepts "9.90", "9,90", "1.234,56" and "1,234.56".
/// (HTML number inputs always post '.', while Turkish users type ','.)
/// </summary>
public class FlexibleDecimalModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext ctx)
    {
        var vpr = ctx.ValueProvider.GetValue(ctx.ModelName);
        if (vpr == ValueProviderResult.None) return Task.CompletedTask;
        ctx.ModelState.SetModelValue(ctx.ModelName, vpr);

        var raw = vpr.FirstValue?.Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(raw)) return Task.CompletedTask; // [Required] reports the error

        if (TryParse(raw, out var value)) ctx.Result = ModelBindingResult.Success(value);
        else ctx.ModelState.TryAddModelError(ctx.ModelName, "Geçerli bir sayı girin.");
        return Task.CompletedTask;
    }

    public static bool TryParse(string raw, out decimal value)
    {
        int lastDot = raw.LastIndexOf('.'), lastComma = raw.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
            // The separator that appears last is the decimal separator; the other one groups thousands.
            normalized = lastComma > lastDot ? raw.Replace(".", "").Replace(',', '.') : raw.Replace(",", "");
        else
            normalized = raw.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
