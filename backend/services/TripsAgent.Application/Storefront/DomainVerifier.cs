using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// Looks for a custom hostname's two records and writes down what it found: the TXT record proving the agency
/// controls the domain, and the CNAME sending the hostname's visitors to the platform.
/// </summary>
/// <remarks>
/// <para>
/// Both are needed. Ownership without routing would verify an address that serves nothing; routing without
/// ownership would let anyone point a hostname at us and claim it.
/// </para>
/// <para>
/// Every lookup is recorded, misses included — that log is the answer to "why isn't my domain working?".
/// The caller saves.
/// </para>
/// </remarks>
public sealed class DomainVerifier
{
    /// <summary>Recorded when both records are right but another website already holds the hostname.</summary>
    public const string HeldElsewhere =
        "Both records are in place, but this address is already connected to another website. "
        + "Remove it there first, or contact support if the domain is yours.";

    private readonly IDnsResolver _dns;
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly StorefrontOptions _options;

    public DomainVerifier(IDnsResolver dns, IAppDbContext db, IPlatformScope platformScope, StorefrontOptions options)
    {
        _dns = dns;
        _db = db;
        _platformScope = platformScope;
        _options = options;
    }

    /// <summary>
    /// Checks <paramref name="domain"/> once and moves it on: verified, looked at again later, or abandoned
    /// once seven days have passed.
    /// </summary>
    /// <returns>True when this check verified it.</returns>
    public async Task<bool> CheckAsync(SiteDomain domain, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var token = domain.VerificationToken
                    ?? throw new InvalidOperationException("Only a custom hostname has records to check.");

        var txt = await _dns.LookUpTxtAsync(domain.TxtRecordName, cancellationToken);
        var cname = await _dns.LookUpCnameAsync(domain.Hostname, cancellationToken);

        var txtOutcome = Outcome(txt, values => DomainVerification.TokenFound(token, values));
        var cnameOutcome = Outcome(cname, values => DomainVerification.TargetFound(_options.CustomDomainTarget, values));
        var bothFound = txtOutcome == DnsCheckOutcome.Match && cnameOutcome == DnsCheckOutcome.Match;

        // Proof is not enough when another website already holds the address: one hostname, one website.
        var heldElsewhere = bothFound && await VerifiedElsewhereAsync(domain, cancellationToken);

        _db.SiteDomainChecks.Add(SiteDomainCheck.Record(
            domain,
            DnsRecordKind.Txt,
            domain.TxtRecordName,
            token,
            txt.Values,
            txtOutcome,
            txt.Resolver,
            txt.Detail,
            now));

        _db.SiteDomainChecks.Add(SiteDomainCheck.Record(
            domain,
            DnsRecordKind.Cname,
            domain.Hostname,
            _options.CustomDomainTarget,
            cname.Values,
            cnameOutcome,
            cname.Resolver,
            heldElsewhere ? HeldElsewhere : cname.Detail,
            now));

        var verified = bothFound && !heldElsewhere;
        domain.RecordVerificationAttempt(verified, now);

        return verified;
    }

    /// <summary>What one lookup means for one record: found as expected, found but different, or unanswered.</summary>
    public static DnsCheckOutcome Outcome(DnsLookupResult lookup, Func<IReadOnlyList<string>, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(matches);

        return lookup.Status switch
        {
            DnsLookupStatus.Answered => matches(lookup.Values) ? DnsCheckOutcome.Match : DnsCheckOutcome.Mismatch,
            DnsLookupStatus.NotFound => DnsCheckOutcome.NotFound,
            DnsLookupStatus.Timeout => DnsCheckOutcome.Timeout,
            DnsLookupStatus.ServerFailure => DnsCheckOutcome.ServerFailure,
            _ => DnsCheckOutcome.Error,
        };
    }

    private async Task<bool> VerifiedElsewhereAsync(SiteDomain domain, CancellationToken cancellationToken)
    {
        var hostname = domain.Hostname;
        var id = domain.Id;

        // Yes or no, and nothing more crosses the boundary: the other website stays anonymous.
        using (_platformScope.Enter("domain verification — is this hostname already verified for another website?"))
        {
            return await _db.SiteDomains.AsNoTracking().AnyAsync(
                other => other.Hostname == hostname
                         && other.Id != id
                         && other.VerificationStatus == DomainVerificationStatus.Verified,
                cancellationToken);
        }
    }
}
