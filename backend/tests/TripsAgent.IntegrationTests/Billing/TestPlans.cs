using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Billing;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Billing;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// Puts an agency on a plan, for tests that run through the real API host.
/// </summary>
/// <remarks>
/// What an agency may do — how many sub-agents, how many live listings, whether it may use its own
/// domain — is its plan's entitlements, and an agency with no plan gets the most restrictive answer.
/// A test about something a plan has to allow therefore needs a plan first, exactly as production
/// does. This goes through <see cref="TierAdminService"/> rather than inserting rows, so a test cannot
/// pass by building a tier the service would refuse.
/// </remarks>
public static class TestPlans
{
    private const string Reason = "Set up by an integration test.";

    public static async Task SubscribeAsync(IServiceProvider services, Guid agencyId, params EntitlementGrant[] grants)
    {
        await using var scope = services.CreateAsyncScope();
        var tiers = scope.ServiceProvider.GetRequiredService<TierAdminService>();

        var code = $"test-{Guid.NewGuid():N}"[..20];
        var created = await tiers.CreateAsync(new TierDraft(code, "Test plan", null, 0, 0, false), Reason);
        var tierId = ((TierChangeOutcome.Saved)created).Tier.Id;

        await tiers.SetEntitlementsAsync(tierId, grants, Reason);
        await tiers.PublishAsync(tierId, Reason);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var _ = scope.ServiceProvider.GetRequiredService<IPlatformScope>().Enter(
            "test setup — subscribes an agency to a plan");

        var tier = await db.SubscriptionTiers
            .Include(candidate => candidate.Prices)
            .SingleAsync(candidate => candidate.Id == tierId);

        var now = DateTimeOffset.UtcNow;

        db.Subscriptions.Add(Subscription.Start(
            agencyId, tier, tier.PriceAt("NGN", BillingInterval.Monthly, now), "NGN", now));

        await db.SaveChangesAsync();
    }
}
