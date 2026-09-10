using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy;

/// <summary>
/// A travel business: our paying customer, and the tenant every other business row belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy is a self-reference plus a materialised <c>path</c>, so a principal can read its
/// whole subtree with one indexed query rather than a recursive walk. The path is maintained by a
/// database trigger, not by this class — see the remarks on <see cref="Path"/>.
/// </para>
/// <para>
/// An agency is never deleted. Ending a relationship sets <see cref="AgencyStatus.Terminated"/>,
/// because orders, invoices and ledger entries still point here and a tax authority may ask about
/// them years later.
/// </para>
/// </remarks>
public sealed partial class Agency : Entity, IAuditableEntity
{
    /// <summary>Nigerian VAT, in basis points. 7.5% at the time of writing.</summary>
    public const int DefaultVatRateBasisPoints = 750;

    /// <summary>The deepest the tree may go for MVP: a principal and its direct sub-agents.</summary>
    public const int MaxDepth = 2;

    private Agency()
    {
        // EF Core materialisation. Everything below is set by the factory methods.
        LegalName = string.Empty;
        Slug = string.Empty;
        CountryCode = string.Empty;
        BaseCurrency = string.Empty;
        Timezone = string.Empty;
        Path = string.Empty;
    }

    /// <summary>
    /// Registers a travel business that signed up with us directly.
    /// </summary>
    /// <remarks>
    /// It starts <see cref="AgencyStatus.PendingVerification"/>: it exists and can sign in, but
    /// cannot fund a wallet or sell until KYB is approved.
    /// </remarks>
    public static Agency RegisterPrincipal(
        string legalName,
        string slug,
        string countryCode,
        string baseCurrency,
        string timezone,
        string? tradingName = null,
        int vatRateBasisPoints = DefaultVatRateBasisPoints)
    {
        var agency = new Agency
        {
            Type = AgencyType.Principal,
            ParentAgencyId = null,
            Status = AgencyStatus.PendingVerification,
            LegalName = Require(legalName, nameof(legalName)),
            TradingName = string.IsNullOrWhiteSpace(tradingName) ? null : tradingName.Trim(),
            Slug = NormaliseSlug(slug),
            CountryCode = NormaliseCountryCode(countryCode),
            BaseCurrency = NormaliseCurrency(baseCurrency),
            Timezone = Require(timezone, nameof(timezone)),
            VatRateBasisPoints = ValidateVatRate(vatRateBasisPoints),
        };

        return agency;
    }

    /// <summary>
    /// Registers an agency managed by <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to the parent's country, currency, timezone and VAT rate, because in practice a
    /// principal's sub-agents operate in the same market — but each is stored on the child, so
    /// changing the parent later does not silently rewrite the child's tax rate.
    /// </remarks>
    public static Agency RegisterSubAgent(
        Agency parent,
        string legalName,
        string slug,
        string? tradingName = null)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (parent.Type != AgencyType.Principal)
        {
            throw new InvalidOperationException(
                $"""
                 Only a principal can have sub-agents, and '{parent.Slug}' is a {parent.Type}.

                 The hierarchy is capped at {MaxDepth} levels for MVP — a sub-agent of a sub-agent
                 would be rejected by a CHECK constraint on the agencies table anyway.
                 """);
        }

        return new Agency
        {
            Type = AgencyType.SubAgent,
            ParentAgencyId = parent.Id,
            Status = AgencyStatus.PendingVerification,
            LegalName = Require(legalName, nameof(legalName)),
            TradingName = string.IsNullOrWhiteSpace(tradingName) ? null : tradingName.Trim(),
            Slug = NormaliseSlug(slug),
            CountryCode = parent.CountryCode,
            BaseCurrency = parent.BaseCurrency,
            Timezone = parent.Timezone,
            VatRateBasisPoints = parent.VatRateBasisPoints,
        };
    }

    /// <summary>Null for a principal; the managing principal for a sub-agent.</summary>
    public Guid? ParentAgencyId { get; private set; }

    /// <summary>
    /// The materialised hierarchy path — a PostgreSQL <c>ltree</c>, GIST-indexed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by a database trigger on insert and on reparent, never by application code. Two
    /// reasons: a path maintained in C# is wrong the moment anyone runs an UPDATE in psql, and
    /// reparenting has to rewrite every descendant in the same statement to stay consistent.
    /// </para>
    /// <para>
    /// Labels are the row's UUID with hyphens replaced by underscores, because an ltree label may
    /// only contain letters, digits and underscores. A principal's path is its own label; a
    /// sub-agent's is <c>parent.child</c>. Read a whole subtree with <c>path &lt;@ 'root_label'</c>.
    /// </para>
    /// </remarks>
    public string Path { get; private set; }

    public AgencyType Type { get; private set; }

    public AgencyStatus Status { get; private set; }

    /// <summary>The name on the incorporation documents. What KYB verifies.</summary>
    public string LegalName { get; private set; }

    /// <summary>What the business actually trades as, if it differs. Shown to travellers.</summary>
    public string? TradingName { get; private set; }

    /// <summary>URL-safe identifier, unique across the platform. Used in storefront URLs.</summary>
    public string Slug { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, upper case. <c>NG</c>.</summary>
    public string CountryCode { get; private set; }

    /// <summary>ISO 4217, upper case. <c>NGN</c>. The currency this agency's books are kept in.</summary>
    public string BaseCurrency { get; private set; }

    /// <summary>IANA zone. <c>Africa/Lagos</c>. Used for departure times and report boundaries.</summary>
    public string Timezone { get; private set; }

    /// <summary>Set when KYB is approved; null until then.</summary>
    public DateTimeOffset? VerifiedAt { get; private set; }

    /// <summary>The Trips staff member looking after this account. Null if unassigned.</summary>
    public Guid? AccountManagerUserId { get; private set; }

    /// <summary>Tax identification number, as issued locally. Collected during onboarding.</summary>
    public string? TaxId { get; private set; }

    /// <summary>
    /// VAT rate in basis points — 750 is 7.5%.
    /// </summary>
    /// <remarks>
    /// An integer rather than a decimal, for the same reason money is a bigint of minor units:
    /// there is exactly one representation and no rounding to argue about. It also keeps the
    /// column out of <c>numeric</c>, which the model rules forbid outright.
    /// </remarks>
    public int VatRateBasisPoints { get; private set; }

    /// <summary>Set when the onboarding wizard is finished; null while it is in progress.</summary>
    public DateTimeOffset? OnboardingCompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this agency may transact — fund a wallet, book, issue.</summary>
    public bool CanTransact => Status == AgencyStatus.Verified;

    /// <summary>Approves KYB. Idempotent: approving an already-verified agency changes nothing.</summary>
    public void MarkVerified(DateTimeOffset at)
    {
        if (Status == AgencyStatus.Verified)
        {
            return;
        }

        Status = AgencyStatus.Verified;
        VerifiedAt = at;
    }

    /// <summary>Rejects KYB. The reason lives on the submission, not here.</summary>
    public void MarkRejected()
    {
        Status = AgencyStatus.Rejected;
        VerifiedAt = null;
    }

    /// <summary>Puts the account back into review — used when a rejected agency resubmits.</summary>
    public void MarkPendingVerification() => Status = AgencyStatus.PendingVerification;

    /// <summary>Suspends the account. This also takes the storefront offline (FRD §2.15 RS-3).</summary>
    public void Suspend() => Status = AgencyStatus.Suspended;

    /// <summary>Ends the relationship for good. The row stays for the audit trail.</summary>
    public void Terminate() => Status = AgencyStatus.Terminated;

    /// <summary>Records that the onboarding wizard was completed.</summary>
    public void CompleteOnboarding(DateTimeOffset at) => OnboardingCompletedAt ??= at;

    /// <summary>Assigns, or clears, the Trips staff member looking after the account.</summary>
    public void AssignAccountManager(Guid? userId) => AccountManagerUserId = userId;

    /// <summary>Records the tax identification number collected during onboarding.</summary>
    public void SetTaxId(string? taxId) =>
        TaxId = string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim();

    /// <summary>Changes the VAT rate applied to this agency's sales, in basis points.</summary>
    public void SetVatRate(int basisPoints) => VatRateBasisPoints = ValidateVatRate(basisPoints);

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value.Trim();
    }

    /// <summary>
    /// Lower-cases and validates a slug. The database enforces the same shape with a CHECK
    /// constraint, so a row inserted by hand cannot bypass this.
    /// </summary>
    private static string NormaliseSlug(string slug)
    {
        var normalised = Require(slug, nameof(slug)).ToLowerInvariant();

        if (!SlugPattern().IsMatch(normalised))
        {
            throw new ArgumentException(
                $"""
                 '{slug}' is not a usable slug.

                 A slug appears in storefront URLs, so it must be lower-case letters, digits and
                 single hyphens between them — 'lagos-travel', not 'Lagos Travel' or '-lagos--'.
                 """,
                nameof(slug));
        }

        return normalised;
    }

    private static string NormaliseCountryCode(string countryCode)
    {
        var normalised = Require(countryCode, nameof(countryCode)).ToUpperInvariant();

        return normalised.Length == 2
            ? normalised
            : throw new ArgumentException(
                $"'{countryCode}' is not an ISO 3166-1 alpha-2 country code, e.g. 'NG'.",
                nameof(countryCode));
    }

    private static string NormaliseCurrency(string currency)
    {
        var normalised = Require(currency, nameof(currency)).ToUpperInvariant();

        return normalised.Length == 3
            ? normalised
            : throw new ArgumentException(
                $"'{currency}' is not an ISO 4217 currency code, e.g. 'NGN'.",
                nameof(currency));
    }

    private static int ValidateVatRate(int basisPoints)
    {
        // 0%-100%. A rate outside that is a units mistake — 7.5 entered where 750 was meant.
        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(basisPoints, 10_000);

        return basisPoints;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex SlugPattern();
}
