using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;

namespace TripsAgent.Documents;

/// <summary>Wires the PDF renderer into a host. The Worker renders; the API only asks it to.</summary>
public static class DocumentRenderingRegistration
{
    /// <summary>Which QuestPDF licence the business holds: Community, Professional or Enterprise.</summary>
    public const string LicenseSetting = "Documents:QuestPdfLicense";

    public static IServiceCollection AddDocumentRendering(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var license = ReadLicense(configuration[LicenseSetting]);

        // Stateless once QuestPDF's settings are applied, and thread-safe: one instance serves every job.
        services.AddSingleton<IDocumentRenderer>(_ => new QuestPdfDocumentRenderer(license));

        // The pipeline that uses it — number, render, store, email. Registered with the renderer
        // rather than in AddApplication, so a host without one cannot resolve half a pipeline.
        services.AddScoped<OrderDocumentService>();

        return services;
    }

    /// <summary>
    /// The licence tier from configuration. Community when unset — QuestPDF's free tier, for a
    /// business under USD 1M a year in revenue.
    /// </summary>
    /// <exception cref="InvalidOperationException">The setting names no tier QuestPDF has.</exception>
    public static LicenseType ReadLicense(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return LicenseType.Community;
        }

        // Evaluation is QuestPDF's trial mode, and it stamps a notice on every page — never on a
        // document a traveller receives.
        if (Enum.TryParse<LicenseType>(configured.Trim(), ignoreCase: true, out var license)
            && license is LicenseType.Community or LicenseType.Professional or LicenseType.Enterprise)
        {
            return license;
        }

        throw new InvalidOperationException(
            $"{LicenseSetting} is '{configured}'. Use Community, Professional or Enterprise — whichever licence the "
            + "business holds. Community is free only below USD 1M annual revenue; see https://www.questpdf.com/license/.");
    }
}
