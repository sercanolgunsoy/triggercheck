using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Applied to the customer panel (Areas/App). Signs the user out immediately if the user or its customer
/// was deactivated by a SuperAdmin while the session was still alive.
/// </summary>
public class ActiveAccountFilter(AppDbContext db, SignInManager<ApplicationUser> signIn, ICurrentCustomer current) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var userId = signIn.UserManager.GetUserId(context.HttpContext.User);
        var ok = current.CustomerId is int cid && await db.Users.AnyAsync(u =>
            u.Id == userId && u.IsActive && u.CustomerId == cid && u.Customer!.IsActive);
        if (!ok)
        {
            await signIn.SignOutAsync();
            context.Result = new Microsoft.AspNetCore.Mvc.RedirectToPageResult("/Account/Login", new { area = "", reason = "inactive" });
            return;
        }
        await next();
    }
}
