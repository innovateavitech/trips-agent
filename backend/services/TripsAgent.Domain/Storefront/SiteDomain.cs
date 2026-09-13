using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// One hostname a site answers on: its free subdomain, or an address the agency owns.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Hostname"/> is unique across the whole platform, not per agency — two agencies claiming
/// the same address is the one thing this table exists to prevent. It is stored normalised (see
/// <see cref="Hostnames"/>), so the unique index cannot be dodged with capitals or a homograph.
/// </para>
/// <para>
/// A custom hostname serves nothing until it is <see cref="DomainVerificationStatus.Verified"/>:
/// the agent adds a TXT record proving they control the zone and a CNAME sending its traffic to us,
/// and a background job checks both. Ownership without routing would be a verified address serving
/// nothing; routing without ownership would let anyone point a hostname at us and claim it.
/// </para>
/// </remarks>
public sealed class SiteDomain : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>How many of its own hostnames one site may connect, so the table is not free storage.</summary>
    public const int MaxCustomDomainsPerSite = 5;

    private SiteDomain()
    {
        Hostname = string.Empty;
    }

    /// <summary>
    /// The site's free address, <c>label.&lt;base domain&gt;</c>. Verified from birth — we own the
    /// zone — and covered by the platform's wildcard certificate, so it serves at once.
    /// </summary>
    public static SiteDomain ForSubdomain(Site site, string hostname, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(site);

        return new SiteDomain
        {
            AgencyId = site.AgencyId,
            SiteId = site.Id,
            Hostname = RequireNormalised(hostname),
            Type = SiteDomainType.Subdomain,
            VerificationStatus = DomainVerificationStatus.Verified,
            VerifiedAt = now,
            SslStatus = SslCertificateStatus.Issued,
            SslIssuedAt = now,
        };
    }

    /// <summary>A hostname the agency owns, waiting for its DNS records.</summary>
    /// <param name="site">The site it will serve.</param>
    /// <param name="hostname">Already normalised and checked.</param>
    /// <param name="verificationToken">
    /// The value the agent puts in the TXT record. Random, at least 128 bits, and never derived from
    /// anything guessable — a predictable token would let anyone who can guess it claim the host.
    /// </param>
    /// <param name="now">When the agent added it; the seven-day window starts here.</param>
    public static SiteDomain ForCustom(Site site, string hostname, string verificationToken, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationToken);

        if (verificationToken.Length < DomainVerification.MinimumTokenLength)
        {
            throw new ArgumentException("A verification token must be long enough not to be guessed.", nameof(verificationToken));
        }

        return new SiteDomain
        {
            AgencyId = site.AgencyId,
            SiteId = site.Id,
            Hostname = RequireNormalised(hostname),
            Type = SiteDomainType.Custom,
            VerificationStatus = DomainVerificationStatus.Pending,
            VerificationToken = verificationToken,
            VerificationStartedAt = now,
            NextCheckAt = now + DomainVerification.FirstCheckDelay,
            SslStatus = SslCertificateStatus.None,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SiteId { get; private set; }

    /// <summary>Lower-case, ASCII (punycode for an international name), no trailing dot.</summary>
    public string Hostname { get; private set; }

    public SiteDomainType Type { get; private set; }

    public DomainVerificationStatus VerificationStatus { get; private set; }

    /// <summary>What the TXT record must hold. Null for a subdomain, which needs no proof.</summary>
    public string? VerificationToken { get; private set; }

    /// <summary>When checking began, or last restarted. The seven-day window counts from here.</summary>
    public DateTimeOffset? VerificationStartedAt { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>When the verification job looks next. Null once verified or abandoned.</summary>
    public DateTimeOffset? NextCheckAt { get; private set; }

    /// <summary>How many times the records have been looked for.</summary>
    public int CheckCount { get; private set; }

    public SslCertificateStatus SslStatus { get; private set; }

    public DateTimeOffset? SslIssuedAt { get; private set; }

    /// <summary>
    /// When the certificate lapses. Null for a subdomain: the platform's wildcard certificate covers
    /// it, and that one is renewed as infrastructure, not per site.
    /// </summary>
    public DateTimeOffset? SslExpiresAt { get; private set; }

    /// <summary>When the certificate job tries next — to issue, retry, or renew.</summary>
    public DateTimeOffset? SslNextAttemptAt { get; private set; }

    /// <summary>Failed attempts since the last success. Drives the back-off and the ceiling.</summary>
    public int SslAttemptCount { get; private set; }

    public string? SslLastError { get; private set; }

    /// <summary>
    /// Set when the hostname looks like a well-known brand (open question 20). It serves nothing until
    /// a platform admin clears it, and an admin alert says it is waiting.
    /// </summary>
    public bool NeedsReview { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsVerified => VerificationStatus == DomainVerificationStatus.Verified;

    /// <summary>True when travellers may be shown the site on this host: verified, and not set aside.</summary>
    public bool CanServe => IsVerified && !NeedsReview;

    /// <summary>True when the host can serve the site over HTTPS today.</summary>
    public bool CanServeSecurely => CanServe && (Type == SiteDomainType.Subdomain || SslStatus == SslCertificateStatus.Issued);

    /// <summary>Sets the hostname aside for a person to look at before it serves anything.</summary>
    public void FlagForReview()
    {
        NeedsReview = true;
        ReviewedAt = null;
        ReviewedByUserId = null;
    }

    /// <summary>A platform admin looked, and the hostname may serve.</summary>
    public void ClearReview(Guid? reviewerUserId, DateTimeOffset now)
    {
        if (!NeedsReview)
        {
            return;
        }

        NeedsReview = false;
        ReviewedAt = now;
        ReviewedByUserId = reviewerUserId;
    }

    /// <summary>Where the TXT record goes: <c>_storefront-verify.&lt;hostname&gt;</c>.</summary>
    public string TxtRecordName => $"{DomainVerification.TxtRecordPrefix}.{Hostname}";

    /// <summary>
    /// Records one check. Both records present verifies the host and queues its certificate; anything
    /// else schedules the next look, until seven days have passed.
    /// </summary>
    public void RecordVerificationAttempt(bool bothRecordsFound, DateTimeOffset now)
    {
        if (Type != SiteDomainType.Custom || VerificationStatus != DomainVerificationStatus.Pending)
        {
            throw new InvalidOperationException("Only a custom hostname waiting for its records is checked.");
        }

        CheckCount++;
        LastCheckedAt = now;

        if (bothRecordsFound)
        {
            VerificationStatus = DomainVerificationStatus.Verified;
            VerifiedAt = now;
            NextCheckAt = null;

            // HTTP validation of a certificate needs traffic reaching us, which the CNAME just proved.
            SslStatus = SslCertificateStatus.Pending;
            SslAttemptCount = 0;
            SslNextAttemptAt = now;
            return;
        }

        var started = VerificationStartedAt ?? now;

        if (now - started >= DomainVerification.GiveUpAfter)
        {
            VerificationStatus = DomainVerificationStatus.Abandoned;
            NextCheckAt = null;
            return;
        }

        NextCheckAt = DomainVerification.NextCheckAfter(started, now);
    }

    /// <summary>Starts the seven days again, for an abandoned host whose records the agent has fixed.</summary>
    public void RestartVerification(DateTimeOffset now)
    {
        if (Type != SiteDomainType.Custom || VerificationStatus == DomainVerificationStatus.Verified)
        {
            throw new InvalidOperationException("Only an unverified custom hostname can be checked again from the start.");
        }

        VerificationStatus = DomainVerificationStatus.Pending;
        VerificationStartedAt = now;
        CheckCount = 0;
        NextCheckAt = now;
    }

    /// <summary>True when the certificate job should act on this host now.</summary>
    public bool IsCertificateDue(DateTimeOffset now)
    {
        if (Type != SiteDomainType.Custom || !IsVerified || SslNextAttemptAt is not { } next || next > now)
        {
            return false;
        }

        return SslStatus switch
        {
            SslCertificateStatus.Pending or SslCertificateStatus.Expired => true,

            // A renewal: the job only looks once the certificate is inside its renewal window.
            SslCertificateStatus.Issued => SslExpiresAt is { } expires && expires - now <= CertificateRenewal.RenewBefore,

            _ => false,
        };
    }

    /// <summary>A certificate was issued or renewed. The next look is at the start of its renewal window.</summary>
    public void RecordCertificateIssued(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A certificate that has already expired was not issued.");
        }

        SslStatus = SslCertificateStatus.Issued;
        SslIssuedAt = now;
        SslExpiresAt = expiresAt;
        SslAttemptCount = 0;
        SslLastError = null;
        SslNextAttemptAt = expiresAt - CertificateRenewal.RenewBefore;
    }

    /// <summary>The issuer accepted the order but has not finished. Look again at <paramref name="retryAt"/>.</summary>
    public void RecordCertificatePending(DateTimeOffset retryAt)
    {
        if (SslStatus != SslCertificateStatus.Issued)
        {
            SslStatus = SslCertificateStatus.Pending;
        }

        SslNextAttemptAt = retryAt;
    }

    /// <summary>
    /// An attempt failed. Backs off rather than retrying at once, and gives up after
    /// <see cref="CertificateRenewal.MaxIssueAttempts"/>.
    /// </summary>
    /// <remarks>
    /// Never a tight retry loop. Certificate authorities rate-limit per registered domain, so hammering
    /// one misconfigured hostname can exhaust the limit for other agents too. A renewal that fails
    /// keeps the current certificate — it is still valid — and keeps trying, once a day at most.
    /// </remarks>
    /// <param name="error">What went wrong, for the console and the runbook.</param>
    /// <param name="permanent">True when retrying cannot help, e.g. the hostname no longer points at us.</param>
    /// <param name="now">When.</param>
    public void RecordCertificateFailure(string error, bool permanent, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        SslAttemptCount++;
        SslLastError = error.Length <= 500 ? error : error[..500];

        var stillValid = SslStatus == SslCertificateStatus.Issued && SslExpiresAt > now;

        if (stillValid)
        {
            SslNextAttemptAt = now + CertificateRenewal.RetryDelay(SslAttemptCount);
            return;
        }

        if (permanent || SslAttemptCount >= CertificateRenewal.MaxIssueAttempts)
        {
            SslStatus = SslCertificateStatus.Failed;
            SslNextAttemptAt = null;
            return;
        }

        SslNextAttemptAt = now + CertificateRenewal.RetryDelay(SslAttemptCount);
    }

    /// <summary>The certificate lapsed with no renewal. Keeps trying, on the back-off.</summary>
    public void RecordCertificateExpired(DateTimeOffset now)
    {
        if (SslStatus != SslCertificateStatus.Issued || SslExpiresAt > now)
        {
            return;
        }

        SslStatus = SslCertificateStatus.Expired;
        SslNextAttemptAt = now;
    }

    /// <summary>Starts issuance again for a host whose certificate failed, once the cause is fixed.</summary>
    public void RetryCertificate(DateTimeOffset now)
    {
        if (Type != SiteDomainType.Custom || !IsVerified)
        {
            throw new InvalidOperationException("Only a verified custom hostname needs a certificate.");
        }

        if (SslStatus == SslCertificateStatus.Issued)
        {
            return;
        }

        SslStatus = SslCertificateStatus.Pending;
        SslAttemptCount = 0;
        SslNextAttemptAt = now;
    }

    private static string RequireNormalised(string hostname)
    {
        if (!Hostnames.TryNormalise(hostname, out var normalised, out var problem))
        {
            throw new ArgumentException(problem, nameof(hostname));
        }

        if (!string.Equals(normalised, hostname, StringComparison.Ordinal))
        {
            throw new ArgumentException("Store the normalised form of the hostname.", nameof(hostname));
        }

        return normalised;
    }
}

/// <summary>
/// One look for one DNS record: what we asked for, what came back, and from which resolver.
/// </summary>
/// <remarks>
/// Append-only — the application role may insert and read, never change or delete. It is the answer
/// to "why isn't my domain working?", and it is only useful if it records the misses as well as the
/// match.
/// </remarks>
public sealed class SiteDomainCheck : Entity, ITenantScoped
{
    public const int MaxValueLength = 300;

    private SiteDomainCheck()
    {
        RecordName = string.Empty;
        ExpectedValue = string.Empty;
        ObservedValues = [];
        Resolver = string.Empty;
    }

    public static SiteDomainCheck Record(
        SiteDomain domain,
        DnsRecordKind kind,
        string recordName,
        string expectedValue,
        IReadOnlyList<string> observedValues,
        DnsCheckOutcome outcome,
        string resolver,
        string? errorDetail,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(observedValues);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolver);

        return new SiteDomainCheck
        {
            AgencyId = domain.AgencyId,
            SiteDomainId = domain.Id,
            Kind = kind,
            RecordName = Clip(recordName),
            ExpectedValue = Clip(expectedValue ?? string.Empty),
            ObservedValues = observedValues.Take(10).Select(Clip).ToArray(),
            Outcome = outcome,
            Resolver = Clip(resolver),
            ErrorDetail = errorDetail is null ? null : Clip(errorDetail),
            CheckedAt = checkedAt,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SiteDomainId { get; private set; }

    public DateTimeOffset CheckedAt { get; private set; }

    public DnsRecordKind Kind { get; private set; }

    /// <summary>The name looked up, e.g. <c>_storefront-verify.www.example.com</c>.</summary>
    public string RecordName { get; private set; }

    public string ExpectedValue { get; private set; }

    /// <summary>Every value the resolver returned, matching or not.</summary>
    public string[] ObservedValues { get; private set; }

    public DnsCheckOutcome Outcome { get; private set; }

    /// <summary>Which resolver answered, so a disagreement between resolvers is visible.</summary>
    public string Resolver { get; private set; }

    public string? ErrorDetail { get; private set; }

    private static string Clip(string value) => value.Length <= MaxValueLength ? value : value[..MaxValueLength];
}
