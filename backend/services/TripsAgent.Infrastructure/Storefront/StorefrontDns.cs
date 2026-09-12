using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>How custom hostnames' DNS records are looked up: <c>Storefront:Dns:*</c>.</summary>
public sealed class StorefrontDnsSettings
{
    public const string SectionName = "Storefront:Dns";

    /// <summary>Answers <c>.test</c> hostnames from their own claim. Development only.</summary>
    public const string DevelopmentMode = "Development";

    /// <summary>Asks a public resolver over HTTPS, with its JSON answers: no DNS library, no cloud SDK.</summary>
    public const string DnsOverHttpsMode = "DnsOverHttps";

    /// <summary>The configured mode, or null to choose by environment: Development locally, DNS over HTTPS elsewhere.</summary>
    public string? Mode { get; init; }

    /// <summary>The resolver asked in <see cref="DnsOverHttpsMode"/>.</summary>
    public Uri DohEndpoint { get; init; } = new("https://cloudflare-dns.com/dns-query");

    /// <summary>How long one lookup may take before it counts as a timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// <see cref="IDnsResolver"/> over HTTPS, reading the JSON answers public resolvers serve as
/// <c>application/dns-json</c>.
/// </summary>
/// <remarks>
/// A public resolver rather than the machine's own, so what we see is what the rest of the internet sees and
/// not a stale answer cached on a private network. Nothing here throws for an unanswered lookup: each ending
/// is returned as a status, recorded, and tried again at the next check.
/// </remarks>
public sealed partial class DnsOverHttpsResolver : IDnsResolver
{
    /// <summary>The named HttpClient this resolver uses.</summary>
    public const string HttpClientName = "storefront-dns";

    /// <summary>The DNS type code of a TXT record.</summary>
    public const int TxtType = 16;

    /// <summary>The DNS type code of a CNAME record.</summary>
    public const int CnameType = 5;

    private const int NoError = 0;
    private const int ServFail = 2;
    private const int NxDomain = 3;
    private const string Unreadable = "The resolver's answer could not be read.";

    private readonly IHttpClientFactory _clients;
    private readonly StorefrontDnsSettings _settings;
    private readonly ILogger<DnsOverHttpsResolver> _logger;

    public DnsOverHttpsResolver(IHttpClientFactory clients, StorefrontDnsSettings settings, ILogger<DnsOverHttpsResolver> logger)
    {
        _clients = clients;
        _settings = settings;
        _logger = logger;
    }

    public Task<DnsLookupResult> LookUpTxtAsync(string name, CancellationToken cancellationToken = default) =>
        LookUpAsync(name, "TXT", TxtType, cancellationToken);

    public Task<DnsLookupResult> LookUpCnameAsync(string name, CancellationToken cancellationToken = default) =>
        LookUpAsync(name, "CNAME", CnameType, cancellationToken);

    /// <summary>Reads one JSON answer: the values of the <paramref name="typeCode"/> records at <paramref name="name"/>.</summary>
    /// <remarks>
    /// Only records at the name asked count. A CNAME answer also lists where the chain leads, and those
    /// records belong to someone else's zone.
    /// </remarks>
    public static DnsLookupResult Parse(string json, string name, int typeCode, string resolver)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !TryGetInt(root, "Status", out var status))
            {
                return DnsLookupResult.Failed(DnsLookupStatus.Error, resolver, Unreadable);
            }

            switch (status)
            {
                case NoError:
                    break;

                case NxDomain:
                    return DnsLookupResult.Nothing(resolver);

                case ServFail:
                    return DnsLookupResult.Failed(
                        DnsLookupStatus.ServerFailure,
                        resolver,
                        "The domain's DNS servers did not give a proper answer (SERVFAIL). This is usually a problem at the DNS provider.");

                default:
                    return DnsLookupResult.Failed(DnsLookupStatus.Error, resolver, $"The resolver answered with DNS error code {status}.");
            }

            var values = new List<string>();

            if (root.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array)
            {
                foreach (var answer in answers.EnumerateArray())
                {
                    if (answer.ValueKind == JsonValueKind.Object
                        && TryGetInt(answer, "type", out var type)
                        && type == typeCode
                        && TryGetString(answer, "name", out var answerName)
                        && SameName(answerName, name)
                        && TryGetString(answer, "data", out var data))
                    {
                        values.Add(data);
                    }
                }
            }

            return DnsLookupResult.Found(values, resolver);
        }
        catch (JsonException)
        {
            return DnsLookupResult.Failed(DnsLookupStatus.Error, resolver, Unreadable);
        }
    }

    private async Task<DnsLookupResult> LookUpAsync(string name, string type, int typeCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resolver = _settings.DohEndpoint.Host;
        var address = new UriBuilder(_settings.DohEndpoint) { Query = $"name={Uri.EscapeDataString(name)}&type={type}" }.Uri;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

            using var response = await _clients.CreateClient(HttpClientName).SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return DnsLookupResult.Failed(DnsLookupStatus.Error, resolver, $"The resolver answered HTTP {(int)response.StatusCode}.");
            }

            return Parse(await response.Content.ReadAsStringAsync(timeout.Token), name, typeCode, resolver);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DnsLookupResult.Failed(DnsLookupStatus.Timeout, resolver, "The resolver did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(_logger, ex, resolver);
            return DnsLookupResult.Failed(DnsLookupStatus.Error, resolver, "The resolver could not be reached.");
        }
    }

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;

        return element.TryGetProperty(property, out var found)
               && found.ValueKind == JsonValueKind.Number
               && found.TryGetInt32(out value);
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString() ?? string.Empty;
        return true;
    }

    private static bool SameName(string answerName, string name) =>
        string.Equals(answerName.TrimEnd('.'), name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The DNS resolver {Resolver} could not be reached.")]
    private static partial void LogUnreachable(ILogger logger, Exception exception, string resolver);
}

/// <summary>
/// Plays DNS for local work: a hostname under <c>.test</c> answers as if its owner had created both records
/// correctly, and every other name is not found — as a real resolver says of a name nobody set up.
/// </summary>
/// <remarks>
/// Development only; <see cref="StorefrontRegistration"/> refuses it anywhere else. <c>.test</c> is reserved
/// for testing (RFC 2606), so no real domain could ever be verified by it.
/// </remarks>
public sealed class DevelopmentDnsResolver : IDnsResolver
{
    /// <summary>The resolver's name in the check log.</summary>
    public const string ResolverName = "development";

    /// <summary>The top-level domain that answers.</summary>
    public const string TestSuffix = ".test";

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly StorefrontOptions _options;

    public DevelopmentDnsResolver(IAppDbContext db, IPlatformScope platformScope, StorefrontOptions options)
    {
        _db = db;
        _platformScope = platformScope;
        _options = options;
    }

    public async Task<DnsLookupResult> LookUpTxtAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var prefix = DomainVerification.TxtRecordPrefix + ".";

        if (!IsTestName(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return DnsLookupResult.Nothing(ResolverName);
        }

        var hostname = name[prefix.Length..].TrimEnd('.').ToLowerInvariant();
        List<string> tokens;

        // Played back from the claims themselves — every claim on the hostname, as a real TXT set would hold.
        using (_platformScope.Enter("development DNS — plays back the TXT record of a .test hostname from its own claims"))
        {
            tokens = await _db.SiteDomains.AsNoTracking()
                .Where(domain => domain.Hostname == hostname && domain.VerificationToken != null)
                .Select(domain => domain.VerificationToken!)
                .ToListAsync(cancellationToken);
        }

        return DnsLookupResult.Found(tokens.Select(token => $"\"{token}\"").ToList(), ResolverName);
    }

    public Task<DnsLookupResult> LookUpCnameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Task.FromResult(IsTestName(name)
            ? DnsLookupResult.Found([_options.CustomDomainTarget + "."], ResolverName)
            : DnsLookupResult.Nothing(ResolverName));
    }

    private static bool IsTestName(string name) => name.TrimEnd('.').EndsWith(TestSuffix, StringComparison.OrdinalIgnoreCase);
}
