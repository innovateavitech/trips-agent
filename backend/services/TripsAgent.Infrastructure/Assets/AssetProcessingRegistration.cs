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

        // Stateless, so one instance serves every job.
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();

        services.AddScoped<ProcessAssetHandler>();
        services.AddScoped<IAssetProcessor>(sp => sp.GetRequiredService<ProcessAssetHandler>());
        services.AddScoped<AssetProcessingJob>();
        services.AddScoped<AssetSweepJob>();

        return services;
    }

    /// <summary>
    /// Registers the configured virus scanner, or refuses to start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refusing is the point. Issue #18 says nothing unscanned is served, and the easiest way to
    /// break that is not a bug in the pipeline — it is a deployment that quietly runs with no real
    /// scanner. So there is no default: a Worker with no scanner named does not start, and the
    /// development scanner is refused everywhere except Development.
    /// </para>
    /// <para>
    /// Throwing here takes the host down before it runs a single job, which the orchestrator
    /// reports as a failed deploy — exactly the right amount of noise.
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

        var configured = configuration[VirusScannerSetting];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"""
                 No virus scanner is configured at {VirusScannerSetting}, so the Worker will not start.

                 Every uploaded file is scanned before it can be served (issue #18), and running
                 without a scanner would mark files clean that nobody checked. No production
                 scanner has been chosen yet — that is an open product decision.

                 For local development set {VirusScannerSetting}={EicarTestOnly} (it is already set
                 in the Worker's appsettings.Development.json). That scanner detects only the EICAR
                 test file and is refused outside Development.
                 """);
        }

        if (string.Equals(configured, EicarTestOnly, StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"""
                     {VirusScannerSetting} is {EicarTestOnly} in the {environment.EnvironmentName} environment.

                     That scanner detects nothing but the EICAR test file. It is allowed only in
                     Development, because anywhere else it would label real uploads "scanned clean"
                     without scanning them. Register a real scanner before deploying the Worker.
                     """);
            }

            services.AddSingleton<IVirusScanner, EicarTestVirusScanner>();
            return services;
        }

        throw new InvalidOperationException(
            $"{VirusScannerSetting} is '{configured}', which is not a scanner this build knows. "
            + $"The only one available today is {EicarTestOnly}, for Development.");
    }
}
