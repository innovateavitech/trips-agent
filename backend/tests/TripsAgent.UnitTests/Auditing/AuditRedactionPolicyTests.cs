using FluentAssertions;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.UnitTests.Auditing;

/// <summary>
/// The audit log keeps a copy of every change for years, which makes it the last place a secret
/// should end up. These tests pin down what never reaches it.
/// </summary>
public class AuditRedactionPolicyTests
{
    private readonly AuditRedactionPolicy policy = new();

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("password_hash")]
    [InlineData("NewPassword")]
    [InlineData("Passphrase")]
    [InlineData("RefreshToken")]
    [InlineData("access_token")]
    [InlineData("ApiKey")]
    [InlineData("ClientSecret")]
    [InlineData("MerchantKey")]
    [InlineData("PasswordSalt")]
    [InlineData("OtpCode")]
    [InlineData("CardCvv")]
    [InlineData("TransactionPin")]
    public void Credentials_should_never_be_recorded(string propertyName)
    {
        policy.TreatmentFor(propertyName).Should().Be(AuditFieldTreatment.Redact);
        policy.Apply(propertyName, "correct horse battery staple")
            .Should().Be(AuditRedactionPolicy.RedactedPlaceholder);
    }

    [Theory]
    [InlineData("PassportNumber")]
    [InlineData("passport_number")]
    [InlineData("Bvn")]
    [InlineData("NationalIdNumber")]
    [InlineData("BankAccountNumber")]
    [InlineData("CardNumber")]
    [InlineData("Iban")]
    [InlineData("TaxId")]
    public void Identity_and_financial_numbers_should_be_masked_not_dropped(string propertyName)
    {
        // Masked rather than redacted because an auditor has to be able to tell which document a
        // change referred to. The last few characters answer that; the whole number is not needed.
        policy.TreatmentFor(propertyName).Should().Be(AuditFieldTreatment.Mask);
    }

    [Fact]
    public void A_masked_value_should_keep_only_its_last_four_characters()
    {
        var masked = policy.Apply("PassportNumber", "A01234567");

        masked.Should().Be("*****4567");
    }

    [Fact]
    public void A_masked_value_should_keep_its_original_length()
    {
        // The shape of the value stays recognisable, so a nine-character passport is still
        // distinguishable from an eleven-digit BVN when reading a trail.
        var masked = policy.Apply("Bvn", "22123456789") as string;

        masked.Should().HaveLength("22123456789".Length);
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("123")]
    [InlineData("")]
    public void A_value_too_short_to_mask_should_be_dropped_entirely(string value)
    {
        // Showing four of five characters is not masking, it is a hint. Better to record nothing.
        policy.Apply("PassportNumber", value).Should().Be(AuditRedactionPolicy.RedactedPlaceholder);
    }

    [Fact]
    public void A_null_value_should_not_become_the_string_null()
    {
        policy.Apply("PassportNumber", null).Should().Be(AuditRedactionPolicy.RedactedPlaceholder);
    }

    [Theory]
    [InlineData("LegalName")]
    [InlineData("Email")]
    [InlineData("Status")]
    [InlineData("MarkupPercentage")]
    [InlineData("BalanceMinor")]
    public void Ordinary_business_data_should_pass_through_untouched(string propertyName)
    {
        policy.TreatmentFor(propertyName).Should().Be(AuditFieldTreatment.Keep);
        policy.Apply(propertyName, "Kano Travels Ltd").Should().Be("Kano Travels Ltd");
    }

    [Fact]
    public void A_secret_should_win_over_a_mask_when_a_name_matches_both()
    {
        // "passport_token" is a token that happens to mention a passport. Tokens are never kept.
        policy.TreatmentFor("PassportToken").Should().Be(AuditFieldTreatment.Redact);
    }

    [Fact]
    public void The_policy_should_be_extensible_for_names_nobody_predicted()
    {
        var extended = new AuditRedactionPolicy(additionalSecrets: ["mothersmaidenname"]);

        extended.TreatmentFor("MothersMaidenName").Should().Be(AuditFieldTreatment.Redact);
        policy.TreatmentFor("MothersMaidenName").Should().Be(AuditFieldTreatment.Keep,
            "the default policy only knows the names it was told about — that is the trade-off");
    }
}
