using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// UI-3: links server-side validation messages to their fields for screen readers. Every
/// <c>&lt;span asp-validation-for&gt;</c> gets a stable id, and a field whose model state is invalid gets
/// <c>aria-invalid="true"</c> plus <c>aria-describedby</c> pointing at that message. (Client-side errors are linked
/// by jQuery Validate itself, with its own "-error" ids, hence the different suffix here.)
/// </summary>
public static class AccessibleValidation
{
    public static string FullName(ViewContext context, ModelExpression @for) =>
        context.ViewData.TemplateInfo.GetFullHtmlFieldName(@for.Name);

    public static string MessageId(string fullName) => TagBuilder.CreateSanitizedId(fullName, "_") + "-validation";

    public static void AppendIdRef(TagHelperOutput output, string attribute, string id)
    {
        var current = output.Attributes[attribute]?.Value?.ToString();
        if (current?.Split(' ').Contains(id) == true) return;
        output.Attributes.SetAttribute(attribute, string.IsNullOrWhiteSpace(current) ? id : current + " " + id);
    }
}

[HtmlTargetElement("span", Attributes = "asp-validation-for")]
[HtmlTargetElement("div", Attributes = "asp-validation-for")]
public class ValidationMessageIdTagHelper : TagHelper
{
    [HtmlAttributeName("asp-validation-for")] public ModelExpression For { get; set; } = default!;
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("id"))
            output.Attributes.SetAttribute("id", AccessibleValidation.MessageId(AccessibleValidation.FullName(ViewContext, For)));
    }
}

[HtmlTargetElement("input", Attributes = "asp-for")]
[HtmlTargetElement("select", Attributes = "asp-for")]
[HtmlTargetElement("textarea", Attributes = "asp-for")]
public class InvalidFieldTagHelper : TagHelper
{
    [HtmlAttributeName("asp-for")] public ModelExpression For { get; set; } = default!;
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = AccessibleValidation.FullName(ViewContext, For);
        if (ViewContext.ViewData.ModelState.TryGetValue(name, out var entry) && entry.Errors.Count > 0)
        {
            output.Attributes.SetAttribute("aria-invalid", "true");
            AccessibleValidation.AppendIdRef(output, "aria-describedby", AccessibleValidation.MessageId(name));
        }
    }
}
