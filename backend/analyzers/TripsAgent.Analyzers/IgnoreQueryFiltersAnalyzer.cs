using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TripsAgent.Analyzers;

/// <summary>
/// <c>TRIPS002</c> — fails the build when production code calls EF Core's
/// <c>IgnoreQueryFilters()</c>. See CLAUDE.md rule 3.
/// </summary>
/// <remarks>
/// <para>
/// <c>IgnoreQueryFilters()</c> switches off the tenant filter for one query, which then reads
/// every agency's rows at once. It compiles, passes a test written by someone acting as a single
/// agency, and reads naturally in a diff. It is also the one mistake in this codebase that leaks
/// one travel agency's customers and prices to another — so it is a compiler error rather than
/// something a reviewer has to spot.
/// </para>
/// <para>
/// The sanctioned way across tenants is <c>IPlatformScope.Enter(reason)</c>. It is logged with the
/// reason and the acting user, and the database's row-level security honours it. A bare
/// <c>IgnoreQueryFilters()</c> does neither.
/// </para>
/// <para>
/// Test assemblies — any whose name ends in <c>Tests</c> — are exempt. Proving isolation holds
/// means deliberately switching the filter off and showing what still stops a leak, and that is
/// test code's job.
/// </para>
/// <para>
/// The escape hatch is <c>backend/IgnoreQueryFiltersAllowlist.txt</c>, reviewed like any other
/// change. See <see cref="SymbolAllowlist"/>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IgnoreQueryFiltersAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID shown in the build output.</summary>
    public const string DiagnosticId = "TRIPS002";

    /// <summary>The allowlist file the analyser looks for among the project's additional files.</summary>
    public const string AllowlistFileName = "IgnoreQueryFiltersAllowlist.txt";

    private const string MethodName = "IgnoreQueryFilters";

    // Matched by full name rather than by symbol, so the analyser needs no reference to EF Core —
    // it runs inside the compiler, which must be able to load it without EF on the path.
    private const string DeclaringType = "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";

    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "IgnoreQueryFilters() bypasses the tenant filter",
        messageFormat: "'{0}' calls IgnoreQueryFilters(), which switches off the tenant filter and reads every "
            + "agency's rows at once. Cross tenants with IPlatformScope.Enter(reason) instead — it is logged, and "
            + "row-level security honours it. If this genuinely cannot use IPlatformScope, add '{0}' to "
            + "backend/IgnoreQueryFiltersAllowlist.txt with a reason, and have it reviewed.",
        category: "Tenancy",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A query that ignores the tenant filter returns every agency's data. In a white-label "
            + "product that is one travel agency seeing another's customers and prices. See CLAUDE.md rule 3.",
        helpLinkUri: "https://github.com/innovateavitech/trips-agent/blob/main/CLAUDE.md#3-never-bypass-the-tenant-filter");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (IsTestAssembly(start.Compilation.AssemblyName))
            {
                return;
            }

            var allowlist = SymbolAllowlist.Load(start.Options.AdditionalFiles, AllowlistFileName, start.CancellationToken);

            start.RegisterOperationAction(
                operation => Analyze(operation, ((IInvocationOperation)operation.Operation).TargetMethod, allowlist),
                OperationKind.Invocation);

            // A method group — `.Select(EntityFrameworkQueryableExtensions.IgnoreQueryFilters)` —
            // is the same call, just made later.
            start.RegisterOperationAction(
                operation => Analyze(operation, ((IMethodReferenceOperation)operation.Operation).Method, allowlist),
                OperationKind.MethodReference);
        });
    }

    /// <summary>
    /// True for <c>TripsAgent.IntegrationTests</c> and its siblings. Ordinal and case-sensitive,
    /// so an assembly that merely contains the word — <c>TripsAgent.TestUtilities</c>, or anything
    /// ending in "contests" — is still checked.
    /// </summary>
    internal static bool IsTestAssembly(string? assemblyName) =>
        assemblyName is not null && assemblyName.EndsWith("Tests", StringComparison.Ordinal);

    private static void Analyze(OperationAnalysisContext context, IMethodSymbol method, SymbolAllowlist allowlist)
    {
        // `rows.IgnoreQueryFilters()` binds to the reduced extension form; the static form has
        // the declaring type we match on.
        var target = method.ReducedFrom ?? method;

        if (!string.Equals(target.Name, MethodName, StringComparison.Ordinal)
            || !string.Equals(target.ContainingType?.ToDisplayString(TypeNameFormat), DeclaringType, StringComparison.Ordinal))
        {
            return;
        }

        var member = EnclosingMember(context.ContainingSymbol);

        if (allowlist.Allows(member))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            context.Operation.Syntax.GetLocation(),
            SymbolAllowlist.FullNameOf(member)));
    }

    /// <summary>
    /// The method, property or field an allowlist entry names. A call inside a lambda or a local
    /// function is attributed to the member that contains it — that is the unit anyone reviews.
    /// </summary>
    private static ISymbol EnclosingMember(ISymbol symbol)
    {
        var current = symbol;

        while (current is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction }
               && current.ContainingSymbol is not null)
        {
            current = current.ContainingSymbol;
        }

        return current;
    }
}
