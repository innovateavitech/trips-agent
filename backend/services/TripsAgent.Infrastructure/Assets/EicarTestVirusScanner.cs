using System.Text;
using TripsAgent.Application.Assets;

namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// NOT A VIRUS SCANNER. Recognises the EICAR test file and nothing else. Development only.
/// </summary>
/// <remarks>
/// <para>
/// No scanner has been chosen yet (ClamAV, a cloud provider's malware scanning, a commercial
/// API — it is a product and budget decision). This exists so the pipeline can be run and tested
/// end to end on a laptop: every file comes back clean except the EICAR string, which every real
/// antivirus engine also detects and which exists precisely so scanners can be tested without
/// handling real malware.
/// </para>
/// <para>
/// <see cref="AssetProcessingRegistration"/> refuses to start the Worker with this outside the
/// Development environment. A scanner that passes everything is worse than none: it writes
/// "scanned clean" on files nobody checked.
/// </para>
/// </remarks>
public sealed class EicarTestVirusScanner : IVirusScanner
{
    /// <summary>What a detection is called, matching the name real engines use for the test file.</summary>
    public const string Signature = "EICAR-Test-File (development scanner)";

    /// <summary>
    /// The EICAR test string, assembled at run time.
    /// </summary>
    /// <remarks>
    /// Never written out as one literal. A real antivirus on a developer's machine would, quite
    /// correctly, flag a source file or an assembly carrying it — so the halves are joined when the
    /// type loads, and neither half is the test string on its own.
    /// </remarks>
    public static string TestString { get; } =
        string.Concat(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private static readonly byte[] Marker = Encoding.ASCII.GetBytes(TestString);

    public string Name => "eicar-test-only";

    public Task<VirusScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        // Anywhere in the file, not only at the start as the EICAR specification says. Stricter
        // than a real engine here costs nothing and makes the test file easy to embed.
        VirusScanResult result = content.Span.IndexOf(Marker) >= 0
            ? new VirusScanResult.Infected(Signature)
            : new VirusScanResult.Clean();

        return Task.FromResult(result);
    }
}
