using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace TripsAgent.AnalyzerTests;

/// <summary>
/// Compiles a C# snippet in memory and runs one analyser over it — the same thing
/// <c>dotnet build</c> does, without a project file or a disk.
/// </summary>
internal static class AnalyzerHarness
{
    /// <summary>
    /// Every assembly the test process itself was started with — the whole .NET base library —
    /// so snippets can use <c>List&lt;T&gt;</c>, records and the rest without listing references.
    /// </summary>
    private static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();

    /// <summary>
    /// Returns the analyser's diagnostics for <paramref name="source"/>.
    /// </summary>
    /// <param name="analyzer">The analyser under test.</param>
    /// <param name="source">The code to compile. It must be valid C#.</param>
    /// <param name="additionalFiles">Files handed to the analyser as the build would, keyed by path.</param>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string source,
        IReadOnlyDictionary<string, string>? additionalFiles = null)
    {
        var compilation = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A snippet with a typo produces no diagnostics from the analyser either, which would look
        // exactly like a passing test. Refuse to run against code that does not compile.
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("the probe snippet itself must compile");

        var texts = (additionalFiles ?? new Dictionary<string, string>())
            .Select(file => (AdditionalText)new InMemoryText(file.Key, file.Value))
            .ToImmutableArray();

        return await compilation
            .WithAnalyzers([analyzer], new AnalyzerOptions(texts))
            .GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>An additional file that lives only in memory.</summary>
    private sealed class InMemoryText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(content);
    }
}
