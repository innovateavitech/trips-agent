namespace TripsAgent.Application.Storefront;

/// <summary>How one DNS lookup ended.</summary>
public enum DnsLookupStatus
{
    /// <summary>The name has records of the type asked for.</summary>
    Answered = 1,

    /// <summary>The name does not exist, or has no records of that type.</summary>
    NotFound = 2,

    /// <summary>No answer in time.</summary>
    Timeout = 3,

    /// <summary>The resolver could not get a proper answer from the domain's own DNS servers (SERVFAIL).</summary>
    ServerFailure = 4,

    /// <summary>Anything else: the resolver could not be reached, or its answer could not be read.</summary>
    Error = 5,
}

/// <summary>What one DNS lookup found.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Values">
/// Every value at the name for the type asked: TXT data as it arrived, quotes and all, and CNAME targets as
/// written. Empty unless <see cref="DnsLookupStatus.Answered"/>.
/// </param>
/// <param name="Resolver">Which resolver answered, for the check log.</param>
/// <param name="Detail">What went wrong, in words an agent can act on, when something did.</param>
public sealed record DnsLookupResult(DnsLookupStatus Status, IReadOnlyList<string> Values, string Resolver, string? Detail = null)
{
    /// <summary>The values found, or "not found" when there are none.</summary>
    public static DnsLookupResult Found(IReadOnlyList<string> values, string resolver)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values.Count == 0 ? Nothing(resolver) : new DnsLookupResult(DnsLookupStatus.Answered, values, resolver);
    }

    /// <summary>The name does not exist, or holds nothing of the type asked.</summary>
    public static DnsLookupResult Nothing(string resolver) => new(DnsLookupStatus.NotFound, [], resolver);

    /// <summary>The lookup did not get an answer.</summary>
    public static DnsLookupResult Failed(DnsLookupStatus status, string resolver, string detail) => new(status, [], resolver, detail);
}

/// <summary>
/// Looks DNS records up, for proving an agency's own hostname. A port: the cloud is not chosen, so nothing
/// above this names a provider (CLAUDE.md).
/// </summary>
/// <remarks>
/// Implementations never throw for an unanswered lookup. A timeout or an unreachable resolver is an outcome
/// to record, and the next check simply tries again.
/// </remarks>
public interface IDnsResolver
{
    /// <summary>The TXT records at <paramref name="name"/>.</summary>
    public Task<DnsLookupResult> LookUpTxtAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The CNAME record at <paramref name="name"/> itself — not wherever it finally leads.</summary>
    public Task<DnsLookupResult> LookUpCnameAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>What asking for a certificate produced.</summary>
public abstract record CertificateResult
{
    private CertificateResult()
    {
    }

    /// <summary>A certificate is in place.</summary>
    /// <param name="ExpiresAt">When it lapses. Renewal starts 30 days before.</param>
    public sealed record Issued(DateTimeOffset ExpiresAt) : CertificateResult;

    /// <summary>The order was accepted and has not finished.</summary>
    /// <param name="RetryAfter">How long to wait before asking again.</param>
    public sealed record Pending(TimeSpan RetryAfter) : CertificateResult;

    /// <summary>It did not work.</summary>
    /// <param name="Error">Shown to the agent in the console, so plain words and nothing internal.</param>
    /// <param name="Permanent">True when trying again cannot help.</param>
    public sealed record Failed(string Error, bool Permanent) : CertificateResult;
}

/// <summary>
/// Obtains and renews TLS certificates for agencies' own hostnames. A port with a development adapter; an
/// ACME (Let's Encrypt) adapter comes later, and needs no cloud SDK.
/// </summary>
/// <remarks>
/// Free subdomains never come here: the platform's wildcard certificate covers them. Implementations return
/// <see cref="CertificateResult.Failed"/> for an expected failure rather than throwing, and are never retried
/// in a tight loop — certificate authorities rate-limit per registered domain.
/// </remarks>
public interface ICertificateIssuer
{
    /// <summary>A short name, for logs.</summary>
    public string Name { get; }

    /// <summary>Asks for a certificate for <paramref name="hostname"/>, new or renewed.</summary>
    public Task<CertificateResult> RequestAsync(string hostname, CancellationToken cancellationToken = default);
}
