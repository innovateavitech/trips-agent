using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Retention;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Suppliers;

namespace TripsAgent.Infrastructure.Retention;

/// <summary>Wires the retention purge and its settings.</summary>
public static class DataRetentionRegistration
{
    public static IServiceCollection AddDataRetention(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(DataRetentionOptions.SectionName);

        // Checked when the host starts, not when the job first runs at 03:10: a bad window should stop a
        // deploy, not fail quietly in the middle of the night.
        services.AddOptions<DataRetentionOptions>()
            .Configure(options => section.Bind(options))
            .Validate(
                options => options.Windows().All(days => days >= 1),
                "Every DataRetention window must be at least 1 day. Zero would delete rows the moment they "
                + "fall due — including a login attempt or a message still being written.")
            .Validate(
                options => options.ProcessedMessageDays >= DataRetentionOptions.MinimumProcessedMessageDays,
                $"DataRetention:ProcessedMessageDays must be at least {DataRetentionOptions.MinimumProcessedMessageDays}. "
                + "The inbox is what stops a redelivered message being processed twice.")
            .ValidateOnStart();

        // A factory rather than letting the container choose a constructor, so the optional test-only
        // rules parameter can never be filled by accident.
        services.AddScoped<IDataRetentionPurge>(provider => new DataRetentionPurge(
            provider.GetRequiredService<AppDbContext>(),
            provider.GetRequiredService<IPlatformScope>(),
            provider.GetRequiredService<ISupplierApiCallMaintenance>(),
            provider.GetRequiredService<IOptions<DataRetentionOptions>>().Value,
            provider.GetRequiredService<IOptions<SupplierApiCallOptions>>().Value,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<DataRetentionPurge>>()));

        return services;
    }
}
