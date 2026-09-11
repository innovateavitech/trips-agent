using FluentAssertions;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.UnitTests.Documents;

/// <summary>
/// What a document number looks like, and which counter it draws from. The database decides the
/// counter value; these rules decide everything else.
/// </summary>
public class DocumentNumberFormatTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();

    private static DocumentNumberFormat Format(
        string prefix = "INV-LAGOS",
        int padding = 6,
        bool includeYear = true,
        bool resetsYearly = true) =>
        DocumentNumberFormat.Configure(AgencyId, DocumentType.Invoice, prefix, padding, includeYear, resetsYearly);

    // ------------------------------------------------------------------------------ rendering

    [Fact]
    public void A_number_is_prefix_year_and_padded_counter()
    {
        Format().Render(42, 2026).Should().Be("INV-LAGOS-2026-000042");
    }

    [Fact]
    public void Without_the_year_a_number_is_prefix_and_padded_counter()
    {
        Format(includeYear: false, resetsYearly: false).Render(42, 2026).Should().Be("INV-LAGOS-000042");
    }

    [Theory]
    [InlineData(1, "INV-LAGOS-2026-7")]
    [InlineData(4, "INV-LAGOS-2026-0007")]
    [InlineData(10, "INV-LAGOS-2026-0000000007")]
    public void Padding_sets_the_counter_width(int padding, string expected)
    {
        Format(padding: padding).Render(7, 2026).Should().Be(expected);
    }

    [Fact]
    public void A_counter_wider_than_the_padding_is_written_in_full_never_truncated()
    {
        // Cutting 1,000,000 down to six digits would print 000000 — or, cutting from the left,
        // 000000 again for 2,000,000. Either is a duplicate of a number already issued.
        Format(padding: 6).Render(1_000_000, 2026).Should().Be("INV-LAGOS-2026-1000000");
    }

    [Fact]
    public void Consecutive_counters_render_to_different_numbers()
    {
        var format = Format(padding: 2);

        Enumerable.Range(1, 500)
            .Select(n => format.Render(n, 2026))
            .Should().OnlyHaveUniqueItems("every counter value must print as its own number");
    }

    [Fact]
    public void A_counter_below_one_is_refused()
    {
        var act = () => Format().Render(0, 2026);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --------------------------------------------------------------------------------- rules

    [Fact]
    public void The_prefix_is_upper_cased_and_trimmed()
    {
        Format(prefix: "  acme/inv ").Prefix.Should().Be("ACME/INV");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INV LAGOS")]
    [InlineData("INV_LAGOS")]
    [InlineData("-INV")]
    [InlineData("INV-")]
    [InlineData("ÀBC")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public void An_unusable_prefix_is_refused(string prefix)
    {
        DocumentNumberFormat.FindProblem(prefix, 6, includeYear: true, resetsYearly: true)
            .Should().NotBeNull();

        var act = () => Format(prefix: prefix);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-3)]
    public void Padding_outside_one_to_ten_is_refused(int padding)
    {
        DocumentNumberFormat.FindProblem("INV", padding, includeYear: true, resetsYearly: true)
            .Should().Contain("digits");
    }

    [Fact]
    public void Restarting_every_year_without_the_year_in_the_number_is_refused()
    {
        // INV-000001 in January 2027 would be the same text as INV-000001 in January 2026.
        DocumentNumberFormat.FindProblem("INV", 6, includeYear: false, resetsYearly: true)
            .Should().Contain("year");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void The_other_year_combinations_are_allowed(bool includeYear, bool resetsYearly)
    {
        DocumentNumberFormat.FindProblem("INV-LAGOS", 6, includeYear, resetsYearly).Should().BeNull();
    }

    [Fact]
    public void Changing_to_an_invalid_format_leaves_the_old_one_in_place()
    {
        var format = Format(prefix: "INV", padding: 4);

        var act = () => format.Change("INV", 0, includeYear: true, resetsYearly: true);

        act.Should().Throw<ArgumentException>();
        format.Padding.Should().Be(4);
    }

    [Fact]
    public void Turning_yearly_reset_on_or_off_is_refused_once_documents_are_issued()
    {
        // Either direction switches counters, and the new one would reprint numbers already issued.
        DocumentNumberFormat.FindResetChangeProblem(currentlyResetsYearly: true, resetsYearly: false, hasIssuedDocuments: true)
            .Should().Contain("restarts every year");
        DocumentNumberFormat.FindResetChangeProblem(currentlyResetsYearly: false, resetsYearly: true, hasIssuedDocuments: true)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void Yearly_reset_is_free_before_the_first_document_or_when_left_alone(
        bool currentlyResetsYearly,
        bool resetsYearly,
        bool hasIssuedDocuments)
    {
        DocumentNumberFormat.FindResetChangeProblem(currentlyResetsYearly, resetsYearly, hasIssuedDocuments)
            .Should().BeNull();
    }

    [Fact]
    public void The_default_resets_yearly()
    {
        var settings = AgencySettings.CreateDefault(
            Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos"));

        DocumentNumberFormat.DefaultFor(settings, DocumentType.Invoice).ResetsYearly
            .Should().Be(DocumentNumberFormat.DefaultResetsYearly);
    }

    // --------------------------------------------------------------------------------- years

    [Fact]
    public void A_yearly_reset_draws_from_that_years_counter()
    {
        Format(resetsYearly: true).SequenceYearFor(2027).Should().Be(2027);
    }

    [Fact]
    public void Numbering_that_never_resets_draws_from_the_one_continuous_counter()
    {
        Format(includeYear: true, resetsYearly: false).SequenceYearFor(2027)
            .Should().Be(DocumentNumberFormat.ContinuousSequenceYear);
    }

    [Fact]
    public void The_year_is_the_agencys_local_year_not_utcs()
    {
        // 23:30 UTC on New Year's Eve is 00:30 on New Year's Day in Lagos (UTC+1). The invoice
        // belongs to the new year's sequence, as the agent's accountant will expect.
        var issuedAt = new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero);

        DocumentNumberFormat.LocalYear(issuedAt, "Africa/Lagos").Should().Be(2027);
        DocumentNumberFormat.LocalYear(issuedAt, "UTC").Should().Be(2026);
    }

    [Fact]
    public void Just_before_local_midnight_is_still_the_old_year()
    {
        var issuedAt = new DateTimeOffset(2026, 12, 31, 22, 59, 0, TimeSpan.Zero);

        DocumentNumberFormat.LocalYear(issuedAt, "Africa/Lagos").Should().Be(2026);
    }

    // ------------------------------------------------------------------------------ defaults

    private static AgencySettings SettingsFor(string slug) =>
        AgencySettings.CreateDefault(
            Agency.RegisterPrincipal("Lagos Travel Limited", slug, "NG", "NGN", "Africa/Lagos"));

    [Fact]
    public void An_invoice_defaults_to_the_agencys_invoice_prefix_with_the_year_and_a_yearly_reset()
    {
        var settings = SettingsFor("lagos-travel");

        var format = DocumentNumberFormat.DefaultFor(settings, DocumentType.Invoice);

        format.AgencyId.Should().Be(settings.AgencyId);
        format.Render(1, 2026).Should().Be("INV-LAGOST-2026-000001");
        format.ResetsYearly.Should().BeTrue();
    }

    [Theory]
    [InlineData(DocumentType.Voucher, "VCH-LAGOST-2026-000001")]
    [InlineData(DocumentType.Itinerary, "ITN-LAGOST-2026-000001")]
    [InlineData(DocumentType.Quote, "QUO-LAGOST-2026-000001")]
    public void Other_types_default_to_a_type_code_and_the_booking_prefix(DocumentType type, string expected)
    {
        DocumentNumberFormat.DefaultFor(SettingsFor("lagos-travel"), type).Render(1, 2026).Should().Be(expected);
    }

    [Fact]
    public void Every_document_type_has_a_valid_default()
    {
        // A new DocumentType without a default would fail the first document of that type an
        // agency ever issues — in production, at checkout.
        var settings = SettingsFor("lagos-travel");

        foreach (var type in Enum.GetValues<DocumentType>())
        {
            var act = () => DocumentNumberFormat.DefaultFor(settings, type);

            act.Should().NotThrow($"{type} needs a default number format");
        }
    }

    [Fact]
    public void The_defaults_never_name_trips()
    {
        // CLAUDE.md rule 4: an invoice number is printed on a traveller-facing document, so it
        // carries the agency's identity, never ours.
        var settings = SettingsFor("lagos-travel");

        foreach (var type in Enum.GetValues<DocumentType>())
        {
            DocumentNumberFormat.DefaultFor(settings, type).Prefix
                .Should().NotContainEquivalentOf("TRIPS");
        }
    }
}
