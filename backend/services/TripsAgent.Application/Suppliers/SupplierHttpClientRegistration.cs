using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// The only way to register an <see cref="HttpClient"/> that talks to a supplier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audited by construction.</b> Every client registered here carries
/// <see cref="SupplierAuditHandler"/>. An adapter cannot opt out, and cannot forget to opt in.
/// </para>
/// <para>
/// <b>Never retried, and checked rather than hoped.</b> This returns
/// <see cref="IServiceCollection"/>, not <see cref="IHttpClientBuilder"/>, so there is no builder to
/// chain <c>.AddStandardResilienceHandler()</c> onto. That alone would not stop a resilience handler
/// arriving another way — <c>ConfigureHttpClientDefaults</c> applies one to every client in the
/// process, and a second <c>AddHttpClient</c> call can reach this one by name. So the pipeline is
/// also inspected when it is built, after every other configuration has run, and any handler besides
/// the audit handler stops the client being created at all. A retry on a supplier client would
/// re-send the ticket-issue call, which is not idempotent: a second real ticket. ADR-0003.
/// </para>
/// <para>
/// That check is an allow list — one handler — rather than a list of known retry types, because a
/// hand-written retry loop in a <see cref="DelegatingHandler"/> is exactly as dangerous as Polly's.
/// A genuinely new need (a signing handler, say) is a change here, made on purpose and reviewed.
/// </para>
/// </remarks>
public static class SupplierHttpClientRegistration
{
    /// <summary>HttpClient's own default, so moving the timeout into the audit handler changes nothing.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Registers a typed supplier client with the audit handler attached, no retry possible, and the
    /// timeout owned by the audit handler.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configureClient">Base address, default headers — anything but the timeout.</param>
    /// <param name="timeout">
    /// How long one call may take before its outcome is recorded as unknown. Choose per supplier:
    /// too short and a slow but successful issue call becomes an unknown outcome to poll for.
    /// </param>
    public static IServiceCollection AddSupplierHttpClient<TClient, TImplementation>(
        this IServiceCollection services,
        Action<HttpClient>? configureClient = null,
        TimeSpan? timeout = null)
        where TClient : class
        where TImplementation : class, TClient
    {
        ArgumentNullException.ThrowIfNull(services);

        var callTimeout = timeout ?? DefaultTimeout;

        var builder = services.AddHttpClient<TClient, TImplementation>(client => configureClient?.Invoke(client));

        builder.AddHttpMessageHandler(provider => new SupplierAuditHandler(
            provider.GetRequiredService<ISupplierCallRecorder>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            callTimeout));

        // PostConfigure runs after every Configure, including ConfigureHttpClientDefaults and any
        // AddHttpClient call made after this one, so these see the pipeline as it will really be.
        services.PostConfigure<HttpClientFactoryOptions>(builder.Name, options =>
        {
            options.HttpClientActions.Add(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
            options.HttpMessageHandlerBuilderActions.Add(EnsureOnlyTheAuditHandler);
        });

        return services;
    }

    private static void EnsureOnlyTheAuditHandler(HttpMessageHandlerBuilder builder)
    {
        var handlers = builder.AdditionalHandlers;

        if (handlers.Count == 1 && handlers[0] is SupplierAuditHandler)
        {
            return;
        }

        var found = handlers.Count == 0
            ? "none"
            : string.Join(", ", handlers.Select(handler => handler.GetType().FullName));

        throw new InvalidOperationException(
            $"""
             The supplier HTTP client '{builder.Name}' must have exactly one handler, SupplierAuditHandler.
             Found: {found}.

             Supplier clients are never retried. The ticket-issue call is not idempotent, so a retry —
             from AddStandardResilienceHandler, a Polly policy, ConfigureHttpClientDefaults or a
             hand-written handler — issues a second real ticket. A timeout is an unknown outcome,
             resolved by polling the booking's status (docs/adr/0003-never-retry-ticket-issuance.md).

             Register supplier clients only through AddSupplierHttpClient, and do not add handlers to them.
             """);
    }
}
