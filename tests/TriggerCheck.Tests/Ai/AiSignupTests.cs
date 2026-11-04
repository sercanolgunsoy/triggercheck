using Microsoft.EntityFrameworkCore;
using TriggerCheck.Tests.Billing;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Ai;

/// <summary>Decision K2: customers created by self-service sign-up start opted in to AI.</summary>
public class AiSignupTests(BillingTestFactory f) : IClassFixture<BillingTestFactory>
{
    [Fact]
    public async Task Self_service_sign_up_opts_the_new_customer_in_to_AI()
    {
        var email = $"ai-signup@{Guid.NewGuid():N}.example.test";
        var planId = await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Standard").Select(p => p.Id).FirstAsync());
        var r = await f.CreateBrowser().PostFormAsync("/Account/Register", new()
        {
            ["Input.CompanyName"] = "AI Signup Co", ["Input.FullName"] = "Ada Lovelace", ["Input.Email"] = email,
            ["Input.Password"] = "Signup123!", ["Input.PlanId"] = planId.ToString(), ["Input.AcceptTerms"] = "true"
        });
        AssertRedirect(r, "/Account/RegisterConfirmation");
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail == email).Select(c => c.AiEnabled).SingleAsync()));
    }
}
