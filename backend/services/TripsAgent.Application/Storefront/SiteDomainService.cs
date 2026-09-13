using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// The addresses a website answers on (issue 59): its free subdomain, and the agency's own hostnames —
/// connected, checked, made the main address, and removed.
/// </summary>
/// <remarks>
/// <para>
/// A custom hostname serves nothing until its DNS records are found (<see cref="DomainVerifier"/>), and cannot
/// be the main address until it also has a certificate (<see cref="CertificateSweep"/>). The background jobs
/// do the looking; <see cref="CheckNowAsync"/> lets an agent who has just added the records skip the wait.
/// </para>
/// <para>
/// Changing the main address or removing a hostname changes where travellers find the site, so the site row
/// is locked, the change is audited with a reason, and <see cref="SiteDomainsChanged"/> goes through the
/// outbox for the storefront's host cache.
/// </para>
/// </remarks>
public sealed class SiteDomainService
{
    /// <summary>How many recent lookups the console shows per hostname: the last three checks, two records each.</summary>
    public const int RecentCheckCount = 6;

    /// <summary>Hex characters in a verification token: 160 random bits, far past guessing.</summary>
    public const int TokenLength = 40;

    private const string NoSite = "You have not created a website yet.";
    private const string NoDomain = "There is no address with that id on your website.";

    private static readonly char[] AddressEnd = ['/', '?', '#'];

    private readonly IAppDbContext _db;
    private readonly ITransactionRunner _transactions;
    private readonly ISiteLock _siteLock;
    private readonly IPlatformScope _platformScope;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly IOutbox _outbox;
    private readonly IAuditContext _audit;
    private readonly DomainVerifier _verifier;
    private readonly StorefrontOptions _options;
    private readonly TimeProvider _clock;

    public SiteDomainService(
        IAppDbContext db,
        ITransactionRunner transactions,
        ISiteLock siteLock,
        IPlatformScope platformScope,
        IUniqueViolationDetector uniqueViolations,
        IOutbox outbox,
        IAuditContext audit,
        DomainVerifier verifier,
        StorefrontOptions options,
        TimeProvider clock)
    {
        _db = db;
        _transactions = transactions;
        _siteLock = siteLock;
        _platformScope = platformScope;
        _uniqueViolations = uniqueViolations;
        _outbox = outbox;
        _audit = audit;
        _verifier = verifier;
        _options = options;
        _clock = clock;
    }

    /// <summary>How soon after one look the agent may ask for another.</summary>
    public static TimeSpan CheckNowCooldown { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Every address the website answers on: the free one first, then the agency's own, oldest first.</summary>
    public async Task<StorefrontResult<IReadOnlyList<SiteDomainResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (site is null)
        {
            return new StorefrontResult<IReadOnlyList<SiteDomainResponse>>.NotFound(NoSite);
        }

        var domains = await _db.SiteDomains.AsNoTracking()
            .Where(domain => domain.SiteId == site.Id)
            .ToListAsync(cancellationToken);

        var responses = new List<SiteDomainResponse>(domains.Count);

        foreach (var domain in domains
                     .OrderBy(domain => domain.Type == SiteDomainType.Subdomain ? 0 : 1)
                     .ThenBy(domain => domain.CreatedAt))
        {
            responses.Add(await DescribeAsync(domain, site, cancellationToken));
        }

        return new StorefrontResult<IReadOnlyList<SiteDomainResponse>>.Ok(responses);
    }

    /// <summary>
    /// Connects one of the agency's own hostnames. It waits for its DNS records, and the response lists the
    /// records to create.
    /// </summary>
    public async Task<StorefrontResult<SiteDomainResponse>> AddAsync(AddSiteDomainRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (site is null)
        {
            return new StorefrontResult<SiteDomainResponse>.NotFound(NoSite);
        }

        var errors = new FieldErrors();
        var hostname = ReadHostname(request.Hostname, errors);

        if (hostname is null)
        {
            return new StorefrontResult<SiteDomainResponse>.Invalid(errors.ToDictionary());
        }

        var existing = await _db.SiteDomains.AsNoTracking()
            .Where(domain => domain.SiteId == site.Id)
            .Select(domain => new { domain.Hostname, domain.Type })
            .ToListAsync(cancellationToken);

        if (existing.Any(domain => string.Equals(domain.Hostname, hostname, StringComparison.OrdinalIgnoreCase)))
        {
            return new StorefrontResult<SiteDomainResponse>.Conflict(
                "That address is already connected to your website.",
                "It is in your list of addresses, with the records it needs.");
        }

        if (existing.Count(domain => domain.Type == SiteDomainType.Custom) >= SiteDomain.MaxCustomDomainsPerSite)
        {
            errors.Add("hostname", $"A website can have at most {SiteDomain.MaxCustomDomainsPerSite} addresses of its own. Remove one first.");
            return new StorefrontResult<SiteDomainResponse>.Invalid(errors.ToDictionary());
        }

        if (await VerifiedElsewhereAsync(hostname, cancellationToken))
        {
            return new StorefrontResult<SiteDomainResponse>.Conflict(
                "That address is connected to another website.",
                "If the domain is yours, remove it from the other website first, or contact support.");
        }

        // The M3 subscription plans (issue 64) will gate this on their custom-domain entitlement. Until plans
        // exist, every agency with a website may connect its own domain.
        var token = RandomNumberGenerator.GetHexString(TokenLength, lowercase: true);
        var domain = SiteDomain.ForCustom(site, hostname, token, _clock.GetUtcNow());

        if (await LooksLikeBrandAsync(hostname, cancellationToken))
        {
            // Open question 20: set aside for a person to look at, not refused.
            domain.FlagForReview();
            _db.AdminAlerts.Add(AdminAlert.ForHostnameReview(site.AgencyId, domain.Id, hostname));
        }

        _db.SiteDomains.Add(domain);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();

            return new StorefrontResult<SiteDomainResponse>.Conflict(
                "That address is already connected to your website.",
                "Someone added it at the same moment. Reload to see it.");
        }

        return new StorefrontResult<SiteDomainResponse>.Ok(await DescribeAsync(domain, site, cancellationToken));
    }

    /// <summary>
    /// Disconnects one of the agency's own hostnames. When it was the main address, the free address takes over.
    /// </summary>
    public Task<StorefrontResult<bool>> RemoveAsync(Guid domainId, CancellationToken cancellationToken = default) =>
        _transactions.RunAsync<StorefrontResult<bool>>(
            async token =>
            {
                var site = await LockedSiteAsync(token);

                if (site is null)
                {
                    return new StorefrontResult<bool>.NotFound(NoSite);
                }

                var domain = await _db.SiteDomains.FirstOrDefaultAsync(
                    candidate => candidate.Id == domainId && candidate.SiteId == site.Id,
                    token);

                if (domain is null)
                {
                    return new StorefrontResult<bool>.NotFound(NoDomain);
                }

                if (domain.Type == SiteDomainType.Subdomain)
                {
                    return new StorefrontResult<bool>.Conflict(
                        "Your free address cannot be removed.",
                        "Every website keeps its free address, so there is always one that works.");
                }

                var changed = new List<string> { domain.Hostname };

                if (site.PrimaryDomainId == domain.Id)
                {
                    var free = await _db.SiteDomains.FirstAsync(
                        candidate => candidate.SiteId == site.Id && candidate.Type == SiteDomainType.Subdomain,
                        token);

                    site.SetPrimaryDomain(free);
                    changed.Add(free.Hostname);
                    _audit.SetReason($"Removed {domain.Hostname}, the main address; {free.Hostname} is the main address again");
                }

                // Nothing is waiting for a review of an address that is gone.
                var alerts = await _db.AdminAlerts
                    .Where(alert => alert.Type == AdminAlertType.HostnameReview
                                    && alert.EntityId == domain.Id
                                    && alert.Status != AdminAlertStatus.Resolved)
                    .ToListAsync(token);

                foreach (var alert in alerts)
                {
                    alert.Resolve(_clock.GetUtcNow());
                }

                _db.SiteDomains.Remove(domain);
                _outbox.Enqueue(new SiteDomainsChanged(site.Id, site.AgencyId, changed), site.AgencyId);

                await _db.SaveChangesAsync(token);

                return new StorefrontResult<bool>.Ok(true);
            },
            cancellationToken);

    /// <summary>Makes a hostname the website's main address — the one its canonical links and sitemap use.</summary>
    public Task<StorefrontResult<SiteDomainResponse>> MakePrimaryAsync(Guid domainId, CancellationToken cancellationToken = default) =>
        _transactions.RunAsync<StorefrontResult<SiteDomainResponse>>(
            async token =>
            {
                var site = await LockedSiteAsync(token);

                if (site is null)
                {
                    return new StorefrontResult<SiteDomainResponse>.NotFound(NoSite);
                }

                var domain = await _db.SiteDomains.FirstOrDefaultAsync(
                    candidate => candidate.Id == domainId && candidate.SiteId == site.Id,
                    token);

                if (domain is null)
                {
                    return new StorefrontResult<SiteDomainResponse>.NotFound(NoDomain);
                }

                if (site.PrimaryDomainId == domain.Id)
                {
                    return new StorefrontResult<SiteDomainResponse>.Ok(await DescribeAsync(domain, site, token));
                }

                if (domain.Type == SiteDomainType.Custom && !domain.CanServeSecurely)
                {
                    return new StorefrontResult<SiteDomainResponse>.Conflict(
                        "That address is not ready to be your main address.",
                        NotReadyReason(domain));
                }

                var previous = await _db.SiteDomains.AsNoTracking()
                    .Where(candidate => candidate.Id == site.PrimaryDomainId)
                    .Select(candidate => candidate.Hostname)
                    .FirstOrDefaultAsync(token);

                site.SetPrimaryDomain(domain);

                _audit.SetReason(previous is null
                    ? $"Made {domain.Hostname} the main address"
                    : $"Made {domain.Hostname} the main address, replacing {previous}");

                _outbox.Enqueue(
                    new SiteDomainsChanged(site.Id, site.AgencyId, previous is null ? [domain.Hostname] : [domain.Hostname, previous]),
                    site.AgencyId);

                await _db.SaveChangesAsync(token);

                return new StorefrontResult<SiteDomainResponse>.Ok(await DescribeAsync(domain, site, token));
            },
            cancellationToken);

    /// <summary>
    /// Looks for a hostname's records now rather than at the next scheduled check. A hostname abandoned after
    /// seven days starts its seven days again; one whose certificate failed is tried again.
    /// </summary>
    public async Task<StorefrontResult<SiteDomainResponse>> CheckNowAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (site is null)
        {
            return new StorefrontResult<SiteDomainResponse>.NotFound(NoSite);
        }

        var domain = await _db.SiteDomains.FirstOrDefaultAsync(
            candidate => candidate.Id == domainId && candidate.SiteId == site.Id,
            cancellationToken);

        if (domain is null)
        {
            return new StorefrontResult<SiteDomainResponse>.NotFound(NoDomain);
        }

        if (domain.Type == SiteDomainType.Subdomain)
        {
            return new StorefrontResult<SiteDomainResponse>.Conflict("Your free address needs no checking.", "It works already.");
        }

        var now = _clock.GetUtcNow();

        if (domain.IsVerified)
        {
            if (domain.SslStatus != SslCertificateStatus.Failed)
            {
                return new StorefrontResult<SiteDomainResponse>.Conflict("This address is already verified.", "There is nothing more to check.");
            }

            // Whatever stopped the certificate may be fixed now: the next certificate run tries again.
            domain.RetryCertificate(now);
            await _db.SaveChangesAsync(cancellationToken);

            return new StorefrontResult<SiteDomainResponse>.Ok(await DescribeAsync(domain, site, cancellationToken));
        }

        if (domain.VerificationStatus == DomainVerificationStatus.Pending
            && domain.LastCheckedAt is { } lastChecked
            && now - lastChecked < CheckNowCooldown)
        {
            return new StorefrontResult<SiteDomainResponse>.Conflict(
                "This address was checked a moment ago.",
                "Wait a few seconds, then check again. New DNS records can take a while to reach everyone.");
        }

        if (domain.VerificationStatus == DomainVerificationStatus.Abandoned)
        {
            domain.RestartVerification(now);
        }

        if (await _verifier.CheckAsync(domain, now, cancellationToken))
        {
            _outbox.Enqueue(new SiteDomainsChanged(site.Id, site.AgencyId, [domain.Hostname]), site.AgencyId);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return new StorefrontResult<SiteDomainResponse>.Conflict("That address is connected to another website.", DomainVerifier.HeldElsewhere);
        }

        return new StorefrontResult<SiteDomainResponse>.Ok(await DescribeAsync(domain, site, cancellationToken));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<SiteDomainResponse> DescribeAsync(SiteDomain domain, Site site, CancellationToken cancellationToken)
    {
        var checks = domain.Type == SiteDomainType.Custom
            ? await _db.SiteDomainChecks.AsNoTracking()
                .Where(check => check.SiteDomainId == domain.Id)
                .OrderByDescending(check => check.CheckedAt)
                .ThenBy(check => check.Kind)
                .Take(RecentCheckCount)
                .ToListAsync(cancellationToken)
            : new List<SiteDomainCheck>();

        var isPrimary = site.PrimaryDomainId == domain.Id;

        return new SiteDomainResponse(
            domain.Id,
            domain.Hostname,
            domain.Type.ToString(),
            isPrimary,
            domain.NeedsReview,
            domain.VerificationStatus.ToString(),
            domain.SslStatus.ToString(),
            domain.VerifiedAt,
            domain.LastCheckedAt,
            domain.NextCheckAt,
            domain.SslExpiresAt,
            domain.SslLastError,
            RecordsFor(domain),
            checks
                .Select(check => new SiteDomainCheckResponse(
                    check.CheckedAt,
                    check.Kind.ToString().ToUpperInvariant(),
                    check.RecordName,
                    check.ExpectedValue,
                    check.ObservedValues,
                    check.Outcome.ToString(),
                    check.Resolver,
                    check.ErrorDetail))
                .ToList(),
            CanRemove: domain.Type == SiteDomainType.Custom,
            CanMakePrimary: !isPrimary && (domain.Type == SiteDomainType.Subdomain || domain.CanServeSecurely),
            CanCheckNow: domain.Type == SiteDomainType.Custom
                         && (!domain.IsVerified || domain.SslStatus == SslCertificateStatus.Failed));
    }

    /// <summary>The two records to create at the registrar, ready to copy.</summary>
    private IReadOnlyList<SiteDnsRecordResponse> RecordsFor(SiteDomain domain)
    {
        if (domain.Type != SiteDomainType.Custom || domain.VerificationToken is not { } token)
        {
            return [];
        }

        return
        [
            new SiteDnsRecordResponse(
                "TXT",
                domain.TxtRecordName,
                RegistrableDomains.HostLabel(domain.TxtRecordName, domain.Hostname),
                token,
                "Proves that you control this domain."),
            new SiteDnsRecordResponse(
                "CNAME",
                domain.Hostname,
                RegistrableDomains.HostLabel(domain.Hostname, domain.Hostname),
                _options.CustomDomainTarget,
                "Sends visitors on this address to your website. If your DNS provider offers a proxy, switch it off for this record."),
        ];
    }

    /// <summary>
    /// The hostname in what the agent typed, or null with the reason recorded. People paste whole addresses,
    /// so a scheme, a path and a port are dropped first.
    /// </summary>
    private string? ReadHostname(string? input, FieldErrors errors)
    {
        var raw = input?.Trim() ?? string.Empty;
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd >= 0)
        {
            raw = raw[(schemeEnd + 3)..];
        }

        var pathStart = raw.IndexOfAny(AddressEnd);

        if (pathStart >= 0)
        {
            raw = raw[..pathStart];
        }

        var portStart = raw.IndexOf(':', StringComparison.Ordinal);

        if (portStart >= 0)
        {
            raw = raw[..portStart];
        }

        if (!Hostnames.TryNormalise(raw, out var hostname, out var problem))
        {
            errors.Add("hostname", problem);
            return null;
        }

        if (Hostnames.IsWithin(hostname, _options.SubdomainBaseDomain) || Hostnames.IsWithin(hostname, _options.CustomDomainTarget))
        {
            errors.Add("hostname", "That is one of the free addresses we provide. Connect a domain you own, like www.yourbusiness.com.");
            return null;
        }

        if (RegistrableDomains.IsApex(hostname))
        {
            errors.Add(
                "hostname",
                $"Use an address with something in front, like www.{hostname}. A bare domain cannot point at your website with a CNAME record.");
            return null;
        }

        return hostname;
    }

    /// <summary>Whether any label the agency chose — not the suffix — looks like a well-known brand.</summary>
    private async Task<bool> LooksLikeBrandAsync(string hostname, CancellationToken cancellationToken)
    {
        var brands = await _db.ReservedHostnameLabels.AsNoTracking()
            .Where(label => label.Kind == ReservedHostnameKind.Brand)
            .Select(label => label.Label)
            .ToListAsync(cancellationToken);

        var labels = hostname.Split('.');
        var suffixLabels = RegistrableDomains.Of(hostname).Split('.').Length - 1;

        return labels.Take(labels.Length - suffixLabels).Any(label => ReservedHostnames.LooksLikeBrand(label, brands));
    }

    private async Task<bool> VerifiedElsewhereAsync(string hostname, CancellationToken cancellationToken)
    {
        // Yes or no, and nothing more crosses the boundary: the other website stays anonymous.
        using (_platformScope.Enter("custom domain — is this hostname already verified for another website?"))
        {
            return await _db.SiteDomains.AsNoTracking().AnyAsync(
                domain => domain.Hostname == hostname && domain.VerificationStatus == DomainVerificationStatus.Verified,
                cancellationToken);
        }
    }

    private static string NotReadyReason(SiteDomain domain)
    {
        if (domain.NeedsReview)
        {
            return "It is waiting for a review, because it looks like a well-known brand. It works once it has been cleared.";
        }

        if (!domain.IsVerified)
        {
            return "Its DNS records have not been found yet. Check that both records are in place, then check again.";
        }

        return "Its security certificate has not been issued yet. That usually takes a few minutes once the records are found.";
    }

    private async Task<Site?> LockedSiteAsync(CancellationToken cancellationToken)
    {
        var siteId = await _siteLock.LockCurrentSiteAsync(cancellationToken);

        return siteId is null ? null : await _db.Sites.FirstAsync(site => site.Id == siteId, cancellationToken);
    }
}
