using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>Registers Trips Africa as the flight and bus supplier.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddTripsAfrica(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TripsAfricaSearchCircuits>();

        var baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        // Through AddSupplierHttpClient and nothing else: audited by construction, and no retry can
        // be put underneath either client (ADR-0003). The two differ only in how long they wait.
        services.AddSupplierHttpClient<TripsAfricaSearchHttp, TripsAfricaSearchHttp>(
            client => client.BaseAddress = baseAddress, options.SearchTimeout);
        services.AddSupplierHttpClient<TripsAfricaBookingHttp, TripsAfricaBookingHttp>(
            client => client.BaseAddress = baseAddress, options.BookingTimeout);

        // The ticket-issue call gets a client of its own — see docs/adr/0003-never-retry-ticket-issuance.md.
        // Its timeout is set apart from every other call's, and like every supplier client it is built
        // through AddSupplierHttpClient, which refuses any handler but the audit handler: retries are not
        // switched off here, they cannot be switched on. A timeout is an unknown outcome, handed to the
        // status poller, and never a reason to send the call again.
        services.AddSupplierHttpClient<TripsAfricaIssueHttp, TripsAfricaIssueHttp>(
            client => client.BaseAddress = baseAddress, options.IssueTimeout);

        services.AddScoped<TripsAfricaCredentials>();
        services.AddScoped<TripsAfricaSupplier>();
        services.AddScoped<TripsAfricaSearchRunner>();
        services.AddScoped<TripsAfricaTicketing>();

        // One adapter per product, as ISupplierAdapter asks: the two use different endpoints and
        // different authentication. SupplierAdapterRegistry refuses a second claim on either.
        services.AddScoped<ISupplierAdapter, TripsAfricaFlightAdapter>();
        services.AddScoped<ISupplierAdapter, TripsAfricaBusAdapter>();

        return services;
    }

    /// <summary>
    /// Reads <c>TripsAfrica:*</c>. A malformed number stops startup rather than falling back to a
    /// default: a typo in a timeout should be loud, not a surprise at 3am.
    /// </summary>
    public static TripsAfricaOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(TripsAfricaOptions.SectionName);
        var defaults = new TripsAfricaOptions();

        var options = new TripsAfricaOptions
        {
            BaseUrl = section["BaseUrl"] is { Length: > 0 } baseUrl ? baseUrl : defaults.BaseUrl,
            Environment = section["Environment"] is { Length: > 0 } environment
                ? Enum.Parse<SupplierEnvironment>(environment, ignoreCase: true)
                : defaults.Environment,
            BookingTimeout = Seconds(section, "TimeoutSeconds") ?? defaults.BookingTimeout,
            IssueTimeout = Seconds(section, "IssueTimeoutSeconds") ?? defaults.IssueTimeout,
            SearchTimeout = Seconds(section, "SearchTimeoutSeconds") ?? defaults.SearchTimeout,
            MerchantCode = section["MerchantCode"],
            MerchantKey = section["MerchantKey"],
            BearerToken = section["BearerToken"],
        };

        // The poller takes over an unanswered issue call after IssueRecoveryDelay. An issue call allowed
        // to wait longer than that could still be waiting when the poller steps in.
        if (options.IssueTimeout >= SupplierPollSchedule.IssueRecoveryDelay)
        {
            throw new InvalidOperationException(
                $"{TripsAfricaOptions.SectionName}:IssueTimeoutSeconds must be below "
                + $"{SupplierPollSchedule.IssueRecoveryDelay.TotalSeconds:0} seconds, after which the status poller takes "
                + $"over an unanswered issue call. It was {options.IssueTimeout.TotalSeconds:0}.");
        }

        return options;
    }

    private static TimeSpan? Seconds(IConfigurationSection section, string name)
    {
        var value = section[name];

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 1)
        {
            throw new InvalidOperationException(
                $"{TripsAfricaOptions.SectionName}:{name} must be a whole number of seconds, at least 1. It was '{value}'.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
