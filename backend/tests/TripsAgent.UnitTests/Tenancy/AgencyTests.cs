using FluentAssertions;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.UnitTests.Tenancy;

public class AgencyTests
{
    private static Agency NewPrincipal(string slug = "lagos-travel") =>
        Agency.RegisterPrincipal(
            legalName: "Lagos Travel Services Limited",
            slug: slug,
            countryCode: "NG",
            baseCurrency: "NGN",
            timezone: "Africa/Lagos",
            tradingName: "Lagos Travel");

    // ------------------------------------------------------------------------ registration

    [Fact]
    public void A_new_principal_starts_pending_verification_and_cannot_transact()
    {
        var agency = NewPrincipal();

        agency.Type.Should().Be(AgencyType.Principal);
        agency.ParentAgencyId.Should().BeNull();
        agency.Status.Should().Be(AgencyStatus.PendingVerification);
        agency.VerifiedAt.Should().BeNull();

        // The FRD is explicit: no wallet funding until KYB is approved.
        agency.CanTransact.Should().BeFalse();
    }

    [Fact]
    public void A_principal_defaults_to_the_Nigerian_VAT_rate() =>
        NewPrincipal().VatRateBasisPoints.Should().Be(750, "7.5% expressed in basis points");

    [Theory]
    [InlineData("ng", "NG")]
    [InlineData("Ng", "NG")]
    public void The_country_code_is_stored_upper_case(string given, string expected) =>
        Agency.RegisterPrincipal("X Limited", "x-limited", given, "NGN", "Africa/Lagos")
            .CountryCode.Should().Be(expected);

    [Theory]
    [InlineData("ngn", "NGN")]
    [InlineData("Usd", "USD")]
    public void The_currency_is_stored_upper_case(string given, string expected) =>
        Agency.RegisterPrincipal("X Limited", "x-limited", "NG", given, "Africa/Lagos")
            .BaseCurrency.Should().Be(expected);

    [Theory]
    [InlineData("NGA")]   // three letters
    [InlineData("N")]     // one
    public void An_invalid_country_code_is_rejected(string countryCode)
    {
        var act = () => Agency.RegisterPrincipal("X Limited", "x-limited", countryCode, "NGN", "Africa/Lagos");

        act.Should().Throw<ArgumentException>().WithMessage("*ISO 3166-1*");
    }

    [Theory]
    [InlineData("NG")]     // two letters
    [InlineData("NGNN")]   // four
    public void An_invalid_currency_is_rejected(string currency)
    {
        var act = () => Agency.RegisterPrincipal("X Limited", "x-limited", "NG", currency, "Africa/Lagos");

        act.Should().Throw<ArgumentException>().WithMessage("*ISO 4217*");
    }

    [Fact]
    public void A_blank_trading_name_is_stored_as_null() =>
        Agency.RegisterPrincipal("X Limited", "x-limited", "NG", "NGN", "Africa/Lagos", tradingName: "   ")
            .TradingName.Should().BeNull();

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void A_VAT_rate_outside_zero_to_one_hundred_percent_is_rejected(int basisPoints)
    {
        // 10000 basis points is 100%. Anything outside that is a units mistake — someone typing
        // 7.5 where 750 was meant, or a percentage where a fraction was.
        var act = () => Agency.RegisterPrincipal(
            "X Limited", "x-limited", "NG", "NGN", "Africa/Lagos", vatRateBasisPoints: basisPoints);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------- sub-agents

    [Fact]
    public void A_sub_agent_inherits_its_parents_market_settings()
    {
        var parent = Agency.RegisterPrincipal(
            "Parent Limited", "parent", "GH", "GHS", "Africa/Accra", vatRateBasisPoints: 1250);

        var child = Agency.RegisterSubAgent(parent, "Child Limited", "child");

        child.CountryCode.Should().Be("GH");
        child.BaseCurrency.Should().Be("GHS");
        child.Timezone.Should().Be("Africa/Accra");
        child.VatRateBasisPoints.Should().Be(1250);
        child.ParentAgencyId.Should().Be(parent.Id);
        child.Type.Should().Be(AgencyType.SubAgent);
    }

    [Fact]
    public void Changing_a_parent_later_does_not_rewrite_a_childs_tax_rate()
    {
        var parent = NewPrincipal();
        var child = Agency.RegisterSubAgent(parent, "Child Limited", "child");

        parent.SetVatRate(1000);

        // The values were copied at registration, not referenced. A principal correcting its own
        // rate must not silently restate a sub-agent's past or future invoices.
        child.VatRateBasisPoints.Should().Be(750);
    }

    [Fact]
    public void A_sub_agent_cannot_have_sub_agents()
    {
        var parent = NewPrincipal();
        var child = Agency.RegisterSubAgent(parent, "Child Limited", "child");

        var act = () => Agency.RegisterSubAgent(child, "Grandchild Limited", "grandchild");

        act.Should().Throw<InvalidOperationException>().WithMessage("*capped at 2 levels*");
    }

    // ---------------------------------------------------------------------------- lifecycle

    [Fact]
    public void Verifying_records_when_it_happened_and_unlocks_transacting()
    {
        var agency = NewPrincipal();
        var at = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        agency.MarkVerified(at);

        agency.Status.Should().Be(AgencyStatus.Verified);
        agency.VerifiedAt.Should().Be(at);
        agency.CanTransact.Should().BeTrue();
    }

    [Fact]
    public void Verifying_twice_keeps_the_original_timestamp()
    {
        var agency = NewPrincipal();
        var first = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        agency.MarkVerified(first);
        agency.MarkVerified(first.AddDays(30));

        // A duplicate approval — a double-clicked button, a retried job — must not restate when
        // the agency became verified.
        agency.VerifiedAt.Should().Be(first);
    }

    [Fact]
    public void Rejecting_clears_the_verification_timestamp()
    {
        var agency = NewPrincipal();
        agency.MarkVerified(DateTimeOffset.UtcNow);

        agency.MarkRejected();

        agency.Status.Should().Be(AgencyStatus.Rejected);
        agency.VerifiedAt.Should().BeNull();
        agency.CanTransact.Should().BeFalse();
    }

    [Fact]
    public void A_rejected_agency_can_be_put_back_into_review()
    {
        var agency = NewPrincipal();
        agency.MarkRejected();

        agency.MarkPendingVerification();

        agency.Status.Should().Be(AgencyStatus.PendingVerification);
    }

    [Theory]
    [InlineData(AgencyStatus.PendingVerification)]
    [InlineData(AgencyStatus.Rejected)]
    [InlineData(AgencyStatus.Suspended)]
    [InlineData(AgencyStatus.Terminated)]
    public void Only_a_verified_agency_may_transact(AgencyStatus status)
    {
        var agency = NewPrincipal();

        switch (status)
        {
            case AgencyStatus.Rejected: agency.MarkRejected(); break;
            case AgencyStatus.Suspended: agency.Suspend("Test.", DateTimeOffset.UtcNow); break;
            case AgencyStatus.Terminated: agency.Terminate("Test.", DateTimeOffset.UtcNow); break;
            default: break;
        }

        agency.CanTransact.Should().BeFalse();
    }

    [Fact]
    public void Suspending_a_verified_agency_stops_it_transacting()
    {
        var agency = NewPrincipal();
        agency.MarkVerified(DateTimeOffset.UtcNow);

        agency.Suspend("Chargebacks under investigation.", DateTimeOffset.UtcNow);

        agency.Status.Should().Be(AgencyStatus.Suspended);
        agency.CanTransact.Should().BeFalse();
    }

    [Fact]
    public void Completing_onboarding_is_recorded_once()
    {
        var agency = NewPrincipal();
        var first = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        agency.CompleteOnboarding(first);
        agency.CompleteOnboarding(first.AddDays(1));

        agency.OnboardingCompletedAt.Should().Be(first);
    }
}
