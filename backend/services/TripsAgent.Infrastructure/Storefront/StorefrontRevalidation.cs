using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>Where the storefront is, and the secret it accepts rebuild requests with.</summary>
public sealed class StorefrontRevalidationSettings
{
    public const string SectionName = "Storefront:Revalidation";

    /// <summary>The storefront's own address, e.g. <c>https://sites.example.com</c>. Empty to log only.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The shared secret the storefront checks. Never committed: it lives in the environment, and
    /// <c>.env.example</c> carries only its name.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>True when there is somewhere to send a rebuild request.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Asks the storefront to rebuild a site's pages, over HTTP.
/// </summary>
/// <remarks>
/// Deliberately forgiving: a storefront that is down, slow or mid-deploy must not turn a successful
/// publish into a failed background message that retries forever. A failure is logged, and the pages
/// refresh on their own timer instead.
/// </remarks>
public sealed partial class HttpStorefrontRevalidator : IStorefrontRevalidator
{
    /// <summary>The named client, so its timeout is set in one place.</summary>
    public const string HttpClientName = "storefront-revalidation";

    /// <summary>The header carrying the shared secret.</summary>
    public const string TokenHeader = "X-Revalidate-Token";

    private readonly IHttpClientFactory _clients;
    private readonly StorefrontRevalidationSettings _settings;
    private readonly ILogger<HttpStorefrontRevalidator> _logger;

    public HttpStorefrontRevalidator(
        IHttpClientFactory clients,
        StorefrontRevalidationSettings settings,
        ILogger<HttpStorefrontRevalidator> logger)
    {
        _clients = clients;
        _settings = settings;
        _logger = logger;
    }

    public async Task RevalidateAsync(
        Guid siteId,
        IReadOnlyList<string> hostnames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostnames);

        var endpoint = new Uri(new Uri(_settings.Endpoint!.TrimEnd('/') + "/"), "api/revalidate");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new { siteId, hostnames }),
            };

            request.Headers.Add(TokenHeader, _settings.Token);

            using var response = await _clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                LogRefused(_logger, siteId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogUnreachable(_logger, siteId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The storefront refused a rebuild for site {SiteId} with status {StatusCode}. Its pages will "
                  + "refresh on their own timer instead.")]
    private static partial void LogRefused(ILogger logger, Guid siteId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The storefront could not be reached to rebuild site {SiteId}. Its pages will refresh on their "
                  + "own timer instead.")]
    private static partial void LogUnreachable(ILogger logger, Guid siteId, Exception exception);
}

/// <summary>
/// What runs where no storefront address is configured: the request is logged and nothing is sent.
/// </summary>
/// <remarks>
/// The whole publish flow works on a laptop with no storefront running, and the log line says what
/// would have been asked for.
/// </remarks>
public sealed partial class LoggingStorefrontRevalidator : IStorefrontRevalidator
{
    private readonly ILogger<LoggingStorefrontRevalidator> _logger;

    public LoggingStorefrontRevalidator(ILogger<LoggingStorefrontRevalidator> logger) => _logger = logger;

    public Task RevalidateAsync(
        Guid siteId,
        IReadOnlyList<string> hostnames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostnames);

        LogWouldRevalidate(_logger, siteId, string.Join(", ", hostnames));

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No storefront is configured, so site {SiteId} ({Hostnames}) was not asked to rebuild its pages.")]
    private static partial void LogWouldRevalidate(ILogger logger, Guid siteId, string hostnames);
}
