using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TripsAgent.Analyzers;

/// <summary>
/// <c>TRIPS001</c> — fails the build when something named like money is a <see cref="decimal"/>,
/// <see cref="double"/> or <see cref="float"/>. See CLAUDE.md rule 2.
/// </summary>
/// <remarks>
/// <para>
/// Why a compiler error rather than a code-review comment: <c>decimal Price</c> compiles, passes
/// its tests and looks perfectly reasonable in a diff. It only goes wrong in production, when a
/// fraction of a kobo goes missing and the ledger's debits stop equalling its credits. Catching it
/// here means it is caught every time, by the compiler, the moment it is typed.
/// </para>
/// <para>
/// How it decides: a field, property, parameter or local variable whose name ends in one of
/// <see cref="MoneyWords"/> — <c>NetPrice</c>, <c>_serviceFee</c>, <c>WalletBalance</c> — and whose
/// type is, or contains, a <c>decimal</c>, <c>double</c> or <c>float</c>. <c>decimal?</c>,
/// <c>double[]</c> and <c>List&lt;decimal&gt;</c> all count. It is a name heuristic on purpose: it
/// cannot know what a variable means, but a beginner reaching for <c>decimal</c> for a price
/// nearly always names it like one.
/// </para>
/// <para>
/// The escape hatch is <c>backend/MoneyTypeAllowlist.txt</c> — see <see cref="MoneyTypeAllowlist"/>.
/// Generated code (EF Core migrations, for instance) is skipped.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MoneyTypeAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID shown in the build output.</summary>
    public const string DiagnosticId = "TRIPS001";

    /// <summary>
    /// Name endings that mean "this is an amount of money". <c>Minor</c> is here too: something
    /// named <c>PriceMinor</c> promises a whole number of kobo, so a <c>decimal</c> one is a lie.
    /// Plurals (<c>Fees</c>, <c>Prices</c>) are matched automatically.
    /// </summary>
    internal static readonly ImmutableArray<string> MoneyWords =
        ImmutableArray.Create("Amount", "Price", "Fare", "Balance", "Fee", "Minor");

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Money must not be decimal or floating-point",
        messageFormat: "'{0}' looks like money but is typed '{1}'. Store money as a long of minor units "
            + "(kobo) named with a Minor suffix, e.g. 'long {2}', or use TripsAgent.Domain.Common.Money. "
            + "If this genuinely is not an amount, add it to backend/MoneyTypeAllowlist.txt with a reason.",
        category: "Money",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Floating-point and decimal money lose fractions of a kobo, and a double-entry "
            + "ledger that must balance exactly cannot recover them. See CLAUDE.md rule 2.",
        helpLinkUri: "https://github.com/innovateavitech/trips-agent/blob/main/CONTRIBUTING.md#money-is-never-a-decimal");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The allowlist is read once per project build, not once per symbol.
        context.RegisterCompilationStartAction(start =>
        {
            var allowlist = MoneyTypeAllowlist.Load(start.Options.AdditionalFiles, start.CancellationToken);

            start.RegisterSymbolAction(
                symbolContext => AnalyzeMember(symbolContext, allowlist),
                SymbolKind.Field,
                SymbolKind.Property,
                SymbolKind.Method);

            // Local variables are not symbols the compiler visits one by one, so they are found
            // through their declarations instead: `var price = 1500.00m;`.
            start.RegisterOperationAction(
                operationContext => AnalyzeLocal(operationContext, allowlist),
                OperationKind.VariableDeclarator);
        });
    }

    /// <summary>
    /// True when the last word of <paramref name="name"/> is a money word. <c>totalFee</c>,
    /// <c>_totalFee</c>, <c>SERVICE_FEE</c> and <c>Fees</c> all match; <c>Coffee</c> and
    /// <c>Welfare</c> do not, because the word has to start at a word boundary.
    /// </summary>
    internal static bool LooksLikeMoney(string name)
    {
        var lastWord = name.Substring(name.LastIndexOf('_') + 1);
        var singular = lastWord.Length > 1 && lastWord.EndsWith("s", StringComparison.Ordinal)
            ? lastWord.Substring(0, lastWord.Length - 1)
            : lastWord;

        foreach (var word in MoneyWords)
        {
            if (EndsWithWord(lastWord, word) || EndsWithWord(singular, word))
            {
                return true;
            }
        }

        return false;
    }

    private static void AnalyzeMember(SymbolAnalysisContext context, MoneyTypeAllowlist allowlist)
    {
        switch (context.Symbol)
        {
            case IFieldSymbol field:
                Check(field, field.Type, allowlist, context.ReportDiagnostic);
                break;

            case IPropertySymbol property:
                Check(property, property.Type, allowlist, context.ReportDiagnostic);
                break;

            // Parameters of methods and constructors. Property accessors are skipped: their
            // `value` parameter is the property itself, which is already checked above.
            case IMethodSymbol { AssociatedSymbol: null, IsImplicitlyDeclared: false } method:
                foreach (var parameter in method.Parameters)
                {
                    if (!IsRecordPositionalParameter(parameter))
                    {
                        Check(parameter, parameter.Type, allowlist, context.ReportDiagnostic);
                    }
                }

                break;
        }
    }

    private static void AnalyzeLocal(OperationAnalysisContext context, MoneyTypeAllowlist allowlist)
    {
        var local = ((IVariableDeclaratorOperation)context.Operation).Symbol;
        Check(local, local.Type, allowlist, context.ReportDiagnostic);
    }

    private static void Check(ISymbol symbol, ITypeSymbol type, MoneyTypeAllowlist allowlist, Action<Diagnostic> report)
    {
        // Cheapest test first: almost nothing is named like money, so most symbols stop here.
        if (symbol.IsImplicitlyDeclared
            || !LooksLikeMoney(symbol.Name)
            || !ContainsFloatingPoint(type)
            || allowlist.Allows(symbol))
        {
            return;
        }

        report(Diagnostic.Create(
            Rule,
            symbol.Locations.FirstOrDefault() ?? Location.None,
            symbol.Name,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            SuggestedName(symbol.Name)));
    }

    /// <summary>
    /// True for <c>decimal</c>, <c>double</c> and <c>float</c>, and for any type built from them —
    /// <c>decimal?</c> is <c>Nullable&lt;decimal&gt;</c>, so it is caught as a generic argument.
    /// </summary>
    private static bool ContainsFloatingPoint(ITypeSymbol type) => type switch
    {
        { SpecialType: SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single } => true,
        IArrayTypeSymbol array => ContainsFloatingPoint(array.ElementType),
        INamedTypeSymbol { IsGenericType: true } generic => generic.TypeArguments.Any(ContainsFloatingPoint),
        _ => false,
    };

    /// <summary>
    /// In <c>record Quote(decimal Price)</c> the parameter also becomes a property, and the
    /// property is what gets reported. Reporting the parameter too would show the same mistake
    /// twice on the same line.
    /// </summary>
    private static bool IsRecordPositionalParameter(IParameterSymbol parameter) =>
        parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
        && constructor.ContainingType.IsRecord
        && constructor.ContainingType.GetMembers(parameter.Name).OfType<IPropertySymbol>().Any();

    // "fee" and "Fee" are the whole word. "totalFee" ends in it at a capital letter; "coffee"
    // ends in the same three letters but not at a word boundary, so it does not count.
    private static bool EndsWithWord(string candidate, string word) =>
        candidate.Equals(word, StringComparison.OrdinalIgnoreCase)
        || candidate.EndsWith(word, StringComparison.Ordinal);

    private static string SuggestedName(string name) =>
        name.EndsWith("Minor", StringComparison.Ordinal) ? name : name + "Minor";
}
