using FluentAssertions;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.UnitTests.Storefront;

/// <summary>
/// How a custom hostname is proven, when it is checked, and how its certificate is issued and renewed.
/// </summary>
public class SiteDomainRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Long enough to be accepted; plainly not a real verification value.</summary>
    private static readonly string Token = new('v', 32);

    [Fact]
    public void A_free_subdomain_serves_at_once()
    {
        var domain = SiteDomain.ForSubdomain(NewSite(), "lagos-travel.localhost", Now);

        domain.IsVerified.Should().BeTrue();
        domain.CanServeSecurely.Should().BeTrue();
        domain.IsCertificateDue(Now).Should().BeFalse("the platform's wildcard certificate covers it");
    }

    [Fact]
    public void A_custom_hostname_waits_for_its_records_and_is_first_checked_after_a_moment()
    {
        var domain = SiteDomain.ForCustom(NewSite(), "www.lekkihorizon.com", Token, Now);

        domain.VerificationStatus.Should().Be(DomainVerificationStatus.Pending);
        domain.CanServe.Should().BeFalse();
        domain.NextCheckAt.Should().Be(Now + DomainVerification.FirstCheckDelay);
        domain.TxtRecordName.Should().Be("_storefront-verify.www.lekkihorizon.com");
    }

    [Fact]
    public void A_guessable_token_is_refused()
    {
        var act = () => SiteDomain.ForCustom(NewSite(), "www.lekkihorizon.com", "short", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(120, 15)]
    [InlineData(600, 60)]
    public void Checks_back_off_from_every_five_minutes_to_hourly(int minutesSinceStart, int expectedGapMinutes)
    {
        var domain = SiteDomain.ForCustom(NewSite(), "www.lekkihorizon.com", Token, Now);
        var at = Now.AddMinutes(minutesSinceStart);

        domain.RecordVerificationAttempt(bothRecordsFound: false, at);

        domain.NextCheckAt.Should().Be(at.AddMinutes(expectedGapMinutes));
        domain.CheckCount.Should().Be(1);
    }

    [Fact]
    public void Seven_days_without_the_records_abandons_the_hostname_until_the_agent_starts_again()
    {
        var domain = SiteDomain.ForCustom(NewSite(), "www.lekkihorizon.com", Token, Now);

        domain.RecordVerificationAttempt(false, Now.AddDays(7));

        domain.VerificationStatus.Should().Be(DomainVerificationStatus.Abandoned);
        domain.NextCheckAt.Should().BeNull();

        domain.RestartVerification(Now.AddDays(8));
        domain.VerificationStatus.Should().Be(DomainVerificationStatus.Pending);
        domain.NextCheckAt.Should().Be(Now.AddDays(8));
    }

    [Fact]
    public void Both_records_verify_the_hostname_and_queue_its_certificate()
    {
        var domain = SiteDomain.ForCustom(NewSite(), "www.lekkihorizon.com", Token, Now);

        domain.RecordVerificationAttempt(true, Now.AddMinutes(5));

        domain.IsVerified.Should().BeTrue();
        domain.SslStatus.Should().Be(SslCertificateStatus.Pending);
        domain.IsCertificateDue(Now.AddMinutes(5)).Should().BeTrue();
        domain.CanServeSecurely.Should().BeFalse("no certificate yet");
    }

    [Fact]
    public void A_certificate_is_renewed_thirty_days_before_it_expires()
    {
        var domain = VerifiedCustom();
        var expires = Now.AddDays(90);

        domain.RecordCertificateIssued(expires, Now);

        domain.CanServeSecurely.Should().BeTrue();
        domain.IsCertificateDue(Now.AddDays(59)).Should().BeFalse();
        domain.IsCertificateDue(Now.AddDays(60)).Should().BeTrue();
    }

    [Fact]
    public void A_failing_new_certificate_backs_off_and_gives_up_after_six_attempts()
    {
        var domain = VerifiedCustom();
        var at = Now;

        domain.RecordCertificateFailure("rate limited", permanent: false, at);
        domain.SslNextAttemptAt.Should().Be(at.AddMinutes(15));

        for (var attempt = 2; attempt <= CertificateRenewal.MaxIssueAttempts; attempt++)
        {
            at = at.AddDays(1);
            domain.RecordCertificateFailure("rate limited", permanent: false, at);
        }

        domain.SslStatus.Should().Be(SslCertificateStatus.Failed);
        domain.SslNextAttemptAt.Should().BeNull();
    }

    [Fact]
    public void A_failed_renewal_keeps_the_certificate_that_still_works()
    {
        var domain = VerifiedCustom();
        domain.RecordCertificateIssued(Now.AddDays(20), Now);

        domain.RecordCertificateFailure("issuer unavailable", permanent: false, Now);

        domain.SslStatus.Should().Be(SslCertificateStatus.Issued);
        domain.SslNextAttemptAt.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public void A_hostname_set_aside_for_review_serves_nothing_until_it_is_cleared()
    {
        var domain = SiteDomain.ForSubdomain(NewSite(), "emirates-deals.localhost", Now);

        domain.FlagForReview();
        domain.CanServe.Should().BeFalse();

        domain.ClearReview(reviewerUserId: null, Now);
        domain.CanServe.Should().BeTrue();
        domain.ReviewedAt.Should().Be(Now);
    }

    [Fact]
    public void A_TXT_value_is_matched_after_unquoting_and_rejoining_its_chunks()
    {
        var chunked = $"\"{Token[..16]}\" \"{Token[16..]}\"";

        DomainVerification.TokenFound(Token, ["\"v=spf1 include:example.com ~all\"", chunked]).Should().BeTrue();
        DomainVerification.TokenFound(Token, ["\"something-else\""]).Should().BeFalse();
    }

    [Fact]
    public void A_CNAME_is_matched_ignoring_case_and_the_trailing_dot() =>
        DomainVerification.TargetFound("sites.example.net", ["Sites.Example.NET."]).Should().BeTrue();

    [Theory]
    [InlineData("#FFFFFF", "#000000", 21.0)]
    [InlineData("#767676", "#FFFFFF", 4.54)]
    public void Contrast_is_measured_the_WCAG_way(string first, string second, double expected) =>
        ColorContrast.Ratio(first, second).Should().BeApproximately(expected, 0.01);

    [Theory]
    [InlineData("#1F2933", true)]
    [InlineData("#325DEC", true)]
    [InlineData("#777777", false)]
    [InlineData("#FFFF00", false)]
    public void A_primary_colour_must_carry_white_text(string hex, bool passes) =>
        ColorContrast.PassesWithWhiteText(hex).Should().Be(passes);

    private static SiteDomain VerifiedCustom()
    {
        var domain = SiteDomain.ForCustom(NewSite(), "www.lekkihorizon.com", Token, Now);
        domain.RecordVerificationAttempt(true, Now);
        return domain;
    }

    private static Site NewSite() => Site.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "Lekki Horizon Travels");
}
