using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// Plan §3, job 13: looks for the records of every custom hostname that is due a check, across agencies.
/// </summary>
/// <remarks>
/// Due means <see cref="SiteDomain.NextCheckAt"/> has passed — every five minutes for the first hour, then
/// less often, for seven days (see <see cref="DomainVerification"/>). Each hostname is saved as soon as it is
/// checked, so a failure part-way through keeps what was already learned.
/// </remarks>
public sealed partial class DomainVerificationSweep
{
    /// <summary>The most hostnames one run checks. The rest wait for the next run, a minute later.</summary>
    public const int BatchSize = 50;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly DomainVerifier _verifier;
    private readonly IOutbox _outbox;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<DomainVerificationSweep> _logger;

    public DomainVerificationSweep(
        IAppDbContext db,
        IPlatformScope platformScope,
        DomainVerifier verifier,
        IOutbox outbox,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<DomainVerificationSweep> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _verifier = verifier;
        _outbox = outbox;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>How many hostnames were checked.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var checkedCount = 0;

        using (_platformScope.Enter("domain verification — checks the DNS records of every custom hostname that is due, across agencies"))
        {
            var now = _clock.GetUtcNow();

            var due = await _db.SiteDomains
                .Where(domain => domain.Type == SiteDomainType.Custom
                                 && domain.VerificationStatus == DomainVerificationStatus.Pending
                                 && domain.NextCheckAt <= now)
                .OrderBy(domain => domain.NextCheckAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var domain in due)
            {
                var verified = await _verifier.CheckAsync(domain, _clock.GetUtcNow(), cancellationToken);

                if (verified)
                {
                    // The hostname serves its site from now on, so whatever remembered it as unknown must forget.
                    _outbox.Enqueue(new SiteDomainsChanged(domain.SiteId, domain.AgencyId, [domain.Hostname]), domain.AgencyId);
                }

                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
                {
                    // Another website verified the same hostname a moment ago. The next run records why this one did not.
                    LogLostRace(_logger, domain.Hostname);
                    break;
                }

                checkedCount++;

                if (verified)
                {
                    LogVerified(_logger, domain.Hostname, domain.AgencyId);
                }
            }
        }

        return checkedCount;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Verified {Hostname} for agency {AgencyId}.")]
    private static partial void LogVerified(ILogger logger, string hostname, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Hostname} was verified for another website while it was being checked; it is looked at again next run.")]
    private static partial void LogLostRace(ILogger logger, string hostname);
}

/// <summary>
/// Plan §3, job 14: issues a certificate for each newly verified custom hostname, and renews each one 30 days
/// before it expires, across agencies.
/// </summary>
/// <remarks>
/// <para>
/// A failure backs off — 15 minutes, then 1, 4, 12 and 24 hours — rather than retrying at once, because
/// certificate authorities rate-limit per registered domain (see <see cref="SiteDomain.RecordCertificateFailure"/>).
/// </para>
/// <para>
/// Two kinds of alert. A new certificate that will not come is a P2: the address cannot become its site's
/// main address, but nothing that worked has stopped. A certificate within a week of expiry that will not
/// renew, or one that has lapsed, is a P1: visitors on that address are about to see a browser warning.
/// </para>
/// </remarks>
public sealed partial class CertificateSweep
{
    /// <summary>The most hostnames one run handles.</summary>
    public const int BatchSize = 20;

    /// <summary>Who raised the alert; the back office files it under certificate problems.</summary>
    public const string AlertSource = "StorefrontCertificates";

    private const string IssuerUnreachable = "The certificate service could not be reached. It is tried again automatically.";

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly ICertificateIssuer _issuer;
    private readonly IPlatformAlerter _alerter;
    private readonly TimeProvider _clock;
    private readonly ILogger<CertificateSweep> _logger;

    public CertificateSweep(
        IAppDbContext db,
        IPlatformScope platformScope,
        ICertificateIssuer issuer,
        IPlatformAlerter alerter,
        TimeProvider clock,
        ILogger<CertificateSweep> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _issuer = issuer;
        _alerter = alerter;
        _clock = clock;
        _logger = logger;
    }

    /// <returns>How many hostnames the issuer was asked about.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var asked = 0;

        using (_platformScope.Enter("certificates — issues and renews certificates for verified custom hostnames, across agencies"))
        {
            var now = _clock.GetUtcNow();

            await MarkLapsedAsync(now, cancellationToken);

            var candidates = await _db.SiteDomains
                .Where(domain => domain.Type == SiteDomainType.Custom
                                 && domain.VerificationStatus == DomainVerificationStatus.Verified
                                 && domain.SslNextAttemptAt <= now
                                 && (domain.SslStatus == SslCertificateStatus.Pending
                                     || domain.SslStatus == SslCertificateStatus.Expired
                                     || domain.SslStatus == SslCertificateStatus.Issued))
                .OrderBy(domain => domain.SslNextAttemptAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var domain in candidates.Where(candidate => candidate.IsCertificateDue(now)))
            {
                var result = await RequestAsync(domain.Hostname, cancellationToken);
                var at = _clock.GetUtcNow();

                Apply(domain, result, at);
                await _db.SaveChangesAsync(cancellationToken);
                await AlertIfNeededAsync(domain, result, at, cancellationToken);

                asked++;
            }
        }

        return asked;
    }

    private static void Apply(SiteDomain domain, CertificateResult result, DateTimeOffset now)
    {
        switch (result)
        {
            case CertificateResult.Issued issued when issued.ExpiresAt > now:
                domain.RecordCertificateIssued(issued.ExpiresAt, now);
                break;

            case CertificateResult.Issued:
                domain.RecordCertificateFailure("The certificate service returned a certificate that had already expired.", permanent: false, now);
                break;

            case CertificateResult.Pending pending:
                domain.RecordCertificatePending(now + pending.RetryAfter);
                break;

            case CertificateResult.Failed failed:
                domain.RecordCertificateFailure(failed.Error, failed.Permanent, now);
                break;

            default:
                throw new InvalidOperationException($"Unhandled certificate result {result.GetType().Name}.");
        }
    }

    /// <summary>A certificate that expired with no renewal: say so loudly, and keep trying on the back-off.</summary>
    private async Task MarkLapsedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lapsed = await _db.SiteDomains
            .Where(domain => domain.Type == SiteDomainType.Custom
                             && domain.SslStatus == SslCertificateStatus.Issued
                             && domain.SslExpiresAt <= now)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var domain in lapsed)
        {
            domain.RecordCertificateExpired(now);
            await _db.SaveChangesAsync(cancellationToken);

            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"The certificate for {domain.Hostname} has expired",
                    $"The certificate for {domain.Hostname} lapsed without being renewed, so visitors on that address "
                    + $"now see a browser warning. Last error: {domain.SslLastError ?? "none recorded"}. "
                    + "It is being requested again now.",
                    AlertSource,
                    domain.AgencyId),
                cancellationToken);
        }
    }

    private async Task AlertIfNeededAsync(SiteDomain domain, CertificateResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (result is not CertificateResult.Failed failed)
        {
            return;
        }

        if (domain.SslStatus == SslCertificateStatus.Failed)
        {
            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P2,
                    $"No certificate could be issued for {domain.Hostname}",
                    $"{domain.Hostname} is verified, but {domain.SslAttemptCount} attempts to issue its certificate failed, "
                    + $"so it cannot become its website's main address. Last error: {failed.Error} "
                    + "Once the cause is fixed, the agent can try again from the console.",
                    AlertSource,
                    domain.AgencyId),
                cancellationToken);
        }
        else if (domain.SslStatus == SslCertificateStatus.Issued && domain.SslExpiresAt - now <= CertificateRenewal.UrgentWithin)
        {
            var expires = domain.SslExpiresAt?.ToString("u", CultureInfo.InvariantCulture);
            var next = domain.SslNextAttemptAt?.ToString("u", CultureInfo.InvariantCulture);

            await _alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    $"The certificate for {domain.Hostname} expires soon and did not renew",
                    $"It expires at {expires}. The renewal failed: {failed.Error} It is tried again at {next}; "
                    + "if it lapses, visitors on that address see a browser warning.",
                    AlertSource,
                    domain.AgencyId),
                cancellationToken);
        }
    }

    private async Task<CertificateResult> RequestAsync(string hostname, CancellationToken cancellationToken)
    {
        try
        {
            return await _issuer.RequestAsync(hostname, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The agent sees the recorded error in the console, so the exception itself stays in the log.
            LogIssuerFailed(_logger, ex, _issuer.Name, hostname);
            return new CertificateResult.Failed(IssuerUnreachable, Permanent: false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Issuer} certificate issuer failed for {Hostname}.")]
    private static partial void LogIssuerFailed(ILogger logger, Exception exception, string issuer, string hostname);
}
