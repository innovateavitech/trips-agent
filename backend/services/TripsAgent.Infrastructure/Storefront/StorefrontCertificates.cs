using TripsAgent.Application.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>Which certificate issuer is used: <c>Storefront:Certificates:Mode</c>.</summary>
public sealed class StorefrontCertificateSettings
{
    public const string SectionName = "Storefront:Certificates";

    /// <summary>Pretend certificates, issued at once. Development only.</summary>
    public const string DevelopmentMode = "Development";

    /// <summary>No issuer yet: every request fails, backs off, and alerts. The default outside Development.</summary>
    public const string NoneMode = "None";

    /// <summary>The configured mode, or null to choose by environment.</summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Issues a pretend 90-day certificate at once, so the whole custom-domain flow runs on a laptop. Nothing is
/// actually secured.
/// </summary>
public sealed class DevelopmentCertificateIssuer : ICertificateIssuer
{
    private readonly TimeProvider _clock;

    public DevelopmentCertificateIssuer(TimeProvider clock) => _clock = clock;

    /// <summary>How long a pretend certificate lasts: as long as a real Let's Encrypt one.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(90);

    public string Name => "development";

    public Task<CertificateResult> RequestAsync(string hostname, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        return Task.FromResult<CertificateResult>(new CertificateResult.Issued(_clock.GetUtcNow() + Lifetime));
    }
}

/// <summary>
/// Stands in where no issuer is configured yet. Every request fails, so a verified hostname backs off, ends up
/// failed and raises an alert, rather than waiting silently for a certificate that will never come.
/// </summary>
/// <remarks>A real ACME adapter replaces this; it needs no cloud SDK, only HTTP and a key.</remarks>
public sealed class UnavailableCertificateIssuer : ICertificateIssuer
{
    /// <summary>What the agent sees in the console.</summary>
    public const string Reason = "Certificates for your own domains cannot be issued yet. This is tried again automatically.";

    public string Name => "none";

    public Task<CertificateResult> RequestAsync(string hostname, CancellationToken cancellationToken = default) =>
        Task.FromResult<CertificateResult>(new CertificateResult.Failed(Reason, Permanent: false));
}
