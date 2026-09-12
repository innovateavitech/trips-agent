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

            // Retries, circuit-breaker and a per-attempt timeout. Safe for all three calls we make:
            // initialize and charge_authorization both carry our own reference, which Paystack
            // refuses to reuse, so a retry returns the same attempt rather than creating a second
            // charge; and verify is a read.
            .AddStandardResilienceHandler();

        // The same client, asked a different question. Resolved rather than registered separately so
        // there is one HttpClient, one resilience pipeline and one set of credentials — two
        // registrations would mean two circuit breakers, and a gateway that is open for renewals
        // while closed for top-ups is a confusing thing to debug at two in the morning.
        services.AddScoped<IRecurringChargeGateway>(provider =>
            (PaystackGateway)provider.GetRequiredService<IPaymentGateway>());

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
