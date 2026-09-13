using FluentAssertions;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.SubAgents;

namespace TripsAgent.UnitTests.Tenancy.SubAgents;

/// <summary>The depth cap and the scope's own rules. Feature F10, issue 63.</summary>
public class SubAgentScopeTests
{
    [Fact]
    public void A_scope_belongs_to_the_principal_that_granted_it()
    {
        var principal = Guid.CreateVersion7();
        var subAgent = Guid.CreateVersion7();

        var scope = SubAgentScope.Grant(principal, subAgent, SellableProductType.Flight);

        // agency_id is the principal, which is what keeps every write inside the plain tenant rule.
        scope.AgencyId.Should().Be(principal);
        scope.SubAgencyId.Should().Be(subAgent);
        scope.SupplierId.Should().BeNull("no supplier named means every supplier of that type");
    }

    [Fact]
    public void An_agency_cannot_scope_itself()
    {
        var agency = Guid.CreateVersion7();

        var itself = () => SubAgentScope.Grant(agency, agency, SellableProductType.Flight);

        itself.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_supplier_vocabulary_maps_onto_the_scope_vocabulary()
    {
        SubAgentScopes.For(SupplierProductType.Flight).Should().Be(SellableProductType.Flight);
        SubAgentScopes.For(SupplierProductType.Bus).Should().Be(SellableProductType.Bus);
    }

    [Fact]
    public void A_sub_agent_cannot_have_sub_agents_of_its_own()
    {
        var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Branch Limited", "ikeja-branch");

        // Build-plan decision 7: two levels. Refused here, and again by a CHECK constraint.
        var thirdLevel = () => Agency.RegisterSubAgent(subAgent, "Third Level Limited", "third-level");

        thirdLevel.Should().Throw<InvalidOperationException>()
            .WithMessage("*Only a principal can have sub-agents*");
    }

    [Fact]
    public void A_sub_agent_inherits_its_principals_market_but_keeps_its_own_copy()
    {
        var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Branch Limited", "ikeja-branch");

        subAgent.BaseCurrency.Should().Be(principal.BaseCurrency);
        subAgent.VatRateBasisPoints.Should().Be(principal.VatRateBasisPoints);

        // Its own copy: changing the principal's rate later must not rewrite the child's tax.
        principal.SetVatRate(0);
        subAgent.VatRateBasisPoints.Should().Be(Agency.DefaultVatRateBasisPoints);
    }

    [Fact]
    public void A_new_sub_agent_cannot_transact_until_it_is_verified()
    {
        var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Branch Limited", "ikeja-branch");

        subAgent.Status.Should().Be(AgencyStatus.PendingVerification);
        subAgent.CanTransact.Should().BeFalse();
    }

    [Fact]
    public void Freezing_a_sub_agent_stops_it_selling_and_leaves_it_able_to_sign_in()
    {
        var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Branch Limited", "ikeja-branch");
        subAgent.MarkVerified(DateTimeOffset.UtcNow);

        // Freeze is the agency lifecycle reused rather than a second concept, so the storefront,
        // the checkout and the sign-in path need no new rule and cannot disagree about one.
        subAgent.Suspend("Unpaid balance.", DateTimeOffset.UtcNow);

        AgencyAccess.CanTakeNewBookings(subAgent.Status).Should().BeFalse();
        AgencyAccess.CanServeStorefront(subAgent.Status).Should().BeFalse();
        AgencyAccess.CanSignIn(subAgent.Status).Should().BeTrue();
        AgencyAccess.CanServeExistingTravellers(subAgent.Status).Should().BeTrue();
    }

    [Fact]
    public void Revoking_a_sub_agent_ends_it_for_good()
    {
        var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Branch Limited", "ikeja-branch");
        subAgent.MarkVerified(DateTimeOffset.UtcNow);

        subAgent.Terminate("The relationship has ended.", DateTimeOffset.UtcNow);

        subAgent.Status.Should().Be(AgencyStatus.Terminated);
        AgencyAccess.CanSignIn(subAgent.Status).Should().BeFalse();

        // The row stays: orders, invoices and ledger entries still point at it.
        subAgent.StatusReason.Should().Be("The relationship has ended.");
    }
}
