using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Pages.Status;

[AllowAnonymous]
[EnableRateLimiting(StatusPageSetup.ViewPolicy)]
public class UnsubscribeModel(AppDbContext db, StatusPageSubscriptions subscriptions) : SubscriptionLinkModel(db, subscriptions)
{
    protected override bool IsConfirm => false;
}
