using FluentAssertions;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.UnitTests.Platform;

/// <summary>
/// What suspend, reinstate and terminate actually do, and what an agency in each state may still
/// do — build-plan decision 14, which is the one piece of the admin console with a traveller on
/// the other end of it.
/// </summary>
public class AgencyLifecycleTests
{
    private static Agency Verified()
    {
        var agency = Agency.RegisterPrincipal(
            "Lagos Travel Services Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");

        agency.MarkVerified(DateTimeOffset.UtcNow);
        return agency;
    }

    [Fact]
    public void Suspending_records_the_reason_and_when()
    {
        var at = new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);
        var agency = Verified();

        agency.Suspend("Chargeback rate above 3% for two months.", at);

        agency.Status.Should().Be(AgencyStatus.Suspended);
        agency.StatusReason.Should().Be("Chargeback rate above 3% for two months.");
        agency.StatusChangedAt.Should().Be(at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Suspending_without_a_reason_is_refused(string reason)
    {
        var suspend = () => Verified().Suspend(reason, DateTimeOffset.UtcNow);

        // The reason is the whole point: it goes on the profile, into the audit log, and into the
        // conversation the agency will have with support about it.
        suspend.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reinstating_a_verified_agency_puts_it_back_to_verified()
    {
        var agency = Verified();
        agency.Suspend("Investigating a dispute.", DateTimeOffset.UtcNow);

        agency.Reinstate("Dispute closed in the agency's favour.", DateTimeOffset.UtcNow);

        agency.Status.Should().Be(AgencyStatus.Verified);
        agency.CanTransact.Should().BeTrue();
    }

    [Fact]
    public void Reinstating_an_agency_that_was_never_verified_puts_it_back_to_waiting()
    {
        // Suspended before KYB was ever approved. Lifting the suspension must not smuggle it past
        // a decision nobody made.
        var agency = Agency.RegisterPrincipal("New Travel Limited", "new-travel", "NG", "NGN", "Africa/Lagos");
        agency.Suspend("Suspected duplicate signup.", DateTimeOffset.UtcNow);

        agency.Reinstate("Confirmed a different business.", DateTimeOffset.UtcNow);

        agency.Status.Should().Be(AgencyStatus.PendingVerification);
        agency.CanTransact.Should().BeFalse();
    }

    [Fact]
    public void Only_a_suspended_agency_can_be_reinstated()
    {
        var reinstate = () => Verified().Reinstate("No reason to.", DateTimeOffset.UtcNow);

        reinstate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Editing_the_profile_leaves_the_slug_alone()
    {
        var agency = Verified();

        agency.UpdateProfile(
            legalName: "Lagos Travel Services PLC",
            tradingName: "Lagos Travel",
            taxId: "  12345678-0001  ",
            timezone: "Africa/Lagos",
            vatRateBasisPoints: 750);

        agency.LegalName.Should().Be("Lagos Travel Services PLC");
        agency.TaxId.Should().Be("12345678-0001");

        // The slug is in storefront URLs and in links travellers already hold.
        agency.Slug.Should().Be("lagos-travel");
    }

    [Theory]
    [InlineData(AgencyStatus.Verified, true)]
    [InlineData(AgencyStatus.PendingVerification, false)]
    [InlineData(AgencyStatus.Rejected, false)]
    [InlineData(AgencyStatus.Suspended, false)]
    [InlineData(AgencyStatus.Terminated, false)]
    public void Only_a_verified_agency_sells(AgencyStatus status, bool allowed)
    {
        AgencyAccess.CanTakeNewBookings(status).Should().Be(allowed);
        AgencyAccess.CanServeStorefront(status).Should().Be(allowed);
    }

    [Theory]
    [InlineData(AgencyStatus.Verified)]
    [InlineData(AgencyStatus.Suspended)]
    public void A_suspension_does_not_take_a_travellers_documents_away(AgencyStatus status)
    {
        // Decision 14: existing bookings stand and travellers keep their documents through their
        // magic link. Somebody flying next Tuesday is not part of the argument with the agency.
        AgencyAccess.CanServeExistingTravellers(status).Should().BeTrue();
        AgencyAccess.CanSignIn(status).Should().BeTrue();
    }

    [Fact]
    public void A_terminated_agency_serves_nobody()
    {
        AgencyAccess.CanServeExistingTravellers(AgencyStatus.Terminated).Should().BeFalse();
        AgencyAccess.CanSignIn(AgencyStatus.Terminated).Should().BeFalse();
    }

    [Fact]
    public void Every_refusal_is_explained_except_the_one_that_is_not_a_refusal()
    {
        AgencyAccess.WhyNewBookingsAreRefused(AgencyStatus.Verified).Should().BeNull();

        foreach (var status in Enum.GetValues<AgencyStatus>().Where(s => s != AgencyStatus.Verified))
        {
            AgencyAccess.WhyNewBookingsAreRefused(status).Should().NotBeNullOrWhiteSpace();
        }
    }
}
