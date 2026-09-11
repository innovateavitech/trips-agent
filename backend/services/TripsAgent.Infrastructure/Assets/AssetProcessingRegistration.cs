using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TripsAgent.Application.Assets;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// Wires the asset pipeline — scanner, image processor and jobs — into the Worker.
/// </summary>
/// <remarks>
/// Worker only. The API enqueues through <see cref="IAssetPipelineDispatcher"/> and never loads a
/// scanner or an image decoder, which keeps both out of the process that faces the internet.
/// </remarks>
public static class AssetProcessingRegistration
{
    /// <summary>The configuration key naming the virus scanner to use.</summary>
    public const string VirusScannerSetting = "Assets:VirusScanner";

    /// <summary>The value selecting <see cref="EicarTestVirusScanner"/>. Development only.</summary>
    public const string EicarTestOnly = "EicarTestOnly";

    public static IServiceCollection AddAssetProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddVirusScanner(configuration, environment);
        services.AddHostedService<AssetPipelineStatusReporter>();

        // Stateless, so one instance serves every job.
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();

        services.AddScoped<ProcessAssetHandler>();
        services.AddScoped<IAssetProcessor>(sp => sp.GetRequiredService<ProcessAssetHandler>());
        services.AddScoped<AssetProcessingJob>();
        services.AddScoped<AssetSweepJob>();

        return services;
    }

    /// <summary>
    /// Registers the configured virus scanner — or, with no usable one, disables the asset pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #18 says nothing unscanned is served, and the easiest way to break that is not a bug in
    /// the pipeline — it is a deployment that quietly runs with no real scanner. So there is no
    /// default, and the development scanner is never used outside Development.
    /// </para>
    /// <para>
    /// What happens without one is deliberately narrow. This used to throw, which took the whole
    /// Worker down — and with it the payment-webhook drain, the ledger integrity audit and every
    /// consumer. Now the pipeline alone is disabled (see <see cref="AssetPipelineStatus"/>): uploads
    /// stay pending and unserved, and a critical log line says why at every start.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddVirusScanner(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var status = Resolve(configuration[VirusScannerSetting], environment);
        services.AddSingleton(status);

        if (status.IsEnabled)
        {
            services.AddSingleton<IVirusScanner, EicarTestVirusScanner>();
        }
        else
        {
            services.AddSingleton<IVirusScanner>(new UnavailableVirusScanner(status.DisabledReason!));
        }

        return services;
    }

    private static AssetPipelineStatus Resolve(string? configured, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return AssetPipelineStatus.Disabled(
                $"No virus scanner is configured at {VirusScannerSetting}. No production scanner has been "
                + "chosen yet — that is an open product decision. For local development set "
                + $"{VirusScannerSetting}={EicarTestOnly}; it is already set in the Worker's appsettings.Development.json.");
        }

        if (string.Equals(configured, EicarTestOnly, StringComparison.OrdinalIgnoreCase))
        {
            return environment.IsDevelopment()
                ? AssetPipelineStatus.Enabled
                : AssetPipelineStatus.Disabled(
                    $"{VirusScannerSetting} is {EicarTestOnly} in the {environment.EnvironmentName} environment. "
                    + "That scanner detects nothing but the EICAR test file, so outside Development it would "
                    + "label real uploads clean without scanning them.");
        }

        return AssetPipelineStatus.Disabled(
            $"{VirusScannerSetting} is '{configured}', which is not a scanner this build knows. "
            + $"The only one available today is {EicarTestOnly}, for Development.");
    }
}
