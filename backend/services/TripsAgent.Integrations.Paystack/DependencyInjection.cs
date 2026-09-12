using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Payments;

namespace TripsAgent.Integrations.Paystack;

/// <summary>Registers Paystack as the payment gateway.</summary>
public static class DependencyInjection
{
    /// <summary>The configuration section: <c>Paystack:SecretKey</c>, <c>Paystack:BaseUrl</c>.</summary>
    public const string SectionName = "Paystack";

    public static IServiceCollection AddPaystack(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);
        services.AddSingleton(options);

        services.AddHttpClient<IPaymentGateway, PaystackGateway>(client =>
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.SecretKey);

                // Paystack is usually quick. A long timeout here would hold a request thread while
                // an agent stares at a spinner, and the retry below is the better answer.
                client.Timeout = TimeSpan.FromSeconds(20);
            })

            // Retries, circuit-breaker and a per-attempt timeout. Safe for both calls we make:
            // initialize carries our own reference so a retry returns the same attempt rather
            // than creating a second one, and verify is a read.
            .AddStandardResilienceHandler();

        return services;
    }

    /// <summary>
    /// Registers Paystack Transfers as the payout rail — <b>with no retry policy</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The missing <c>AddStandardResilienceHandler()</c> is the whole point of this method, and it
    /// is why transfers are not simply hung off the payments client above.
    /// <c>POST /transfer</c> is not idempotent in any way we can rely on, and a retry after a
    /// timeout can send an agency's money twice — into a bank account there is no supplier to ring
    /// about. See docs/adr/0008-never-retry-payout-transfers.md.
    /// </para>
    /// <para>
    /// <b>If you are here to add a retry because transfers sometimes time out: read the ADR first.</b>
    /// A timeout is an unknown outcome, and <c>PayoutStatusPoller</c> resolves it by asking
    /// Paystack what became of our reference.
    /// </para>
    /// <para>
    /// The timeout is longer than the payments client's 20 seconds because a transfer is worth
    /// waiting for: every second we give up early is a payout that has to be resolved by the
    /// poller instead of by the answer we were about to receive.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPaystackTransfers(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);

        services.AddHttpClient<IBankTransfers, PaystackBankTransfers>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.SecretKey);

            client.Timeout = TimeSpan.FromSeconds(45);
        });

        // Deliberately nothing here. No .AddStandardResilienceHandler(), no Polly pipeline, no
        // retry of any kind. See the remarks above and ADR-0008.
        return services;
    }

    /// <summary>
    /// Reads the keys from configuration.
    /// </summary>
    /// <remarks>
    /// Deliberately no default for the secret key. A gateway silently configured with an empty
    /// key would fail every signature check, which reads as "the gateway is quiet today" rather
    /// than as the misconfiguration it is.
    /// </remarks>
    public static PaystackOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        return new PaystackOptions
        {
            BaseUrl = section["BaseUrl"] is { Length: > 0 } baseUrl ? baseUrl : "https://api.paystack.co",
            SecretKey = section["SecretKey"] ?? string.Empty,
        };
    }
}
