using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Global page filter: while the signed-in user carries the must_change_password claim, every page except the
/// account pages (/Account/*: change password, logout, ...) redirects to /Account/ChangePassword.
/// The claim lives in the auth cookie, so this check costs no database query.
/// </summary>
public class MustChangePasswordFilter : IPageFilter
{
    public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }

    public void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (!context.HttpContext.User.HasClaim(c => c.Type == AppClaims.MustChangePassword)) return;
        var page = context.ActionDescriptor;
        if (string.IsNullOrEmpty(page.AreaName) && page.ViewEnginePath.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase)) return;
        context.Result = new RedirectToPageResult("/Account/ChangePassword", new { area = "" });
    }

    public void OnPageHandlerExecuted(PageHandlerExecutedContext context) { }
}
