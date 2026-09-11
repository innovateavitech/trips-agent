using TripsAgent.Application.Assets;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// The scanner registered when there is no real one. It scans nothing, and says so.
/// </summary>
/// <remarks>
/// Belt and braces behind <see cref="AssetPipelineStatus"/>. The jobs skip when the pipeline is
/// disabled, but if anything ever did reach a scan, the answer is "unavailable" — never "clean".
/// </remarks>
public sealed class UnavailableVirusScanner : IVirusScanner
{
    private readonly string _reason;

    public UnavailableVirusScanner(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _reason = reason;
    }

    public string Name => "none — asset pipeline disabled";

    public Task<VirusScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        Task.FromResult<VirusScanResult>(new VirusScanResult.Unavailable(_reason));
}
