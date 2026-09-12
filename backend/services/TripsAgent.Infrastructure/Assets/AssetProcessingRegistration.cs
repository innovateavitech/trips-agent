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

    /// <summary>
    /// The value selecting <see cref="ClamAvVirusScanner"/>: a real scanner, allowed in every
    /// environment. Needs <c>Assets:ClamAv:Host</c>.
    /// </summary>
    public const string ClamAv = "ClamAv";

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
    /// Configuring <see cref="ClamAv"/> with a clamd host is what switches the pipeline on anywhere
    /// else. The host is not contacted here: a clamd that is down when the Worker starts is the same
    /// as one that goes down later — each scan comes back unavailable, the asset waits, and the job
    /// is retried until clamd answers.
    /// </para>
    /// <para>
    /// What happens without a usable scanner is deliberately narrow. This used to throw, which took
    /// the whole Worker down — and with it the payment-webhook drain, the ledger integrity audit and
    /// every consumer. Now the pipeline alone is disabled (see <see cref="AssetPipelineStatus"/>):
    /// uploads stay pending and unserved, and a critical log line says why at every start.
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

        var (status, scanner) = Choose(configuration, environment);

        services.AddSingleton(status);

        // Every scanner here is stateless — ClamAV opens a connection per scan — so one instance
        // serves every job.
        services.AddSingleton(scanner);

        return services;
    }

    private static (AssetPipelineStatus Status, IVirusScanner Scanner) Choose(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration[VirusScannerSetting];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return Disabled(
                $"No virus scanner is configured at {VirusScannerSetting}. Set it to {ClamAv}, with "
                + $"{ClamAvOptions.SectionName}:Host pointing at a clamd daemon. For local development "
                + $"{EicarTestOnly} also works; it is already set in the Worker's appsettings.Development.json.");
        }

        if (string.Equals(configured, ClamAv, StringComparison.OrdinalIgnoreCase))
        {
            var options = configuration.GetSection(ClamAvOptions.SectionName).Get<ClamAvOptions>() ?? new ClamAvOptions();

            return options.Problem() is { } problem
                ? Disabled($"{VirusScannerSetting} is {ClamAv}, but {problem}")
                : (AssetPipelineStatus.Enabled, new ClamAvVirusScanner(options));
        }

        if (string.Equals(configured, EicarTestOnly, StringComparison.OrdinalIgnoreCase))
        {
            return environment.IsDevelopment()
                ? (AssetPipelineStatus.Enabled, new EicarTestVirusScanner())
                : Disabled(
                    $"{VirusScannerSetting} is {EicarTestOnly} in the {environment.EnvironmentName} environment. "
                    + "That scanner detects nothing but the EICAR test file, so outside Development it would "
                    + "label real uploads clean without scanning them.");
        }

        return Disabled(
            $"{VirusScannerSetting} is '{configured}', which is not a scanner this build knows. "
            + $"Use {ClamAv}, or {EicarTestOnly} in Development.");
    }

    /// <summary>The pipeline switched off, with a scanner behind it that can only ever say "unavailable".</summary>
    private static (AssetPipelineStatus Status, IVirusScanner Scanner) Disabled(string reason) =>
        (AssetPipelineStatus.Disabled(reason), new UnavailableVirusScanner(reason));
}
