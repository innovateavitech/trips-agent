using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TripsAgent.Analyzers;

/// <summary>
/// Fails the build when a member that looks like money is declared as a floating-point type.
/// </summary>
/// <remarks>
/// <para>
/// <c>decimal</c>, <c>double</c> and <c>float</c> all lose fractions of a kobo. In a
/// double-entry ledger that means debits stop equalling credits, and there is no way to
/// reconcile it afterwards — you cannot tell which kobo went missing or from where.
/// See CLAUDE.md rule 2.
/// </para>
/// <para>
/// This complements two guards that already exist and does not duplicate either.
/// <c>MoneyConventions</c> constrains EF Core <em>columns</em>, and the <c>Money</c> type makes
/// the right thing easy in the domain. Neither sees a DTO, a supplier response or a view model —
/// and a <c>decimal TotalFare</c> on a Trips Africa response is precisely how a bad number gets
/// in before EF Core is ever involved.
/// </para>
/// <para>
/// Scope is deliberately narrow: <b>properties and fields only</b>. Method parameters and locals
/// are left alone because a percentage really is a <c>decimal</c> — <c>Money.Percentage(decimal
/// percent)</c> is correct code, and an analyser that shouts at correct code gets switched off.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MoneyMustNotBeFloatingPointAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id. Referenced by <c>.editorconfig</c> and by any suppression.</summary>
    public const string DiagnosticId = "TA0001";

    /// <summary>
    /// The MSBuild property holding the allowlist, as a comma-separated list of
    /// <c>MemberName</c> or <c>TypeName.MemberName</c> entries.
    /// </summary>
    /// <remarks>
    /// Set per project rather than in one repo-wide list, on purpose: an exemption then sits in
    /// the csproj of the project that needs it, where its reviewer is the person who owns that
    /// code. A single global list is a place things get appended to and never removed from.
    /// </remarks>
    public const string AllowedMembersOption = "TripsAgentMoneyAllowedMembers";

    /// <summary>
    /// Name endings that mean "this is an amount of money". Matched case-sensitively against
    /// PascalCase so <c>Coffee</c> does not match <c>Fee</c> and <c>Warfare</c> does not match
    /// <c>Fare</c> — a false positive here teaches people to suppress the rule, which is worse
    /// than not having it.
    /// </summary>
    public static readonly ImmutableArray<string> MoneySuffixes =
        ImmutableArray.Create("Amount", "Price", "Fare", "Balance", "Fee");

    private static readonly char[] AllowListSeparators = { ',', ';' };

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Money must not use a floating-point type",
        messageFormat:
            "'{0}' looks like money but is declared as '{1}'. Money is a whole number of minor "
            + "units (kobo): declare it as 'long' or 'Money' and name it '{0}Minor'.",
        category: "TripsAgent.Money",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "decimal, double and float all lose fractions of a kobo, and in a double-entry ledger "
            + "that imbalance cannot be reconciled afterwards. Store money as a long count of minor "
            + "units with a Minor suffix, or use the Money type. If this member genuinely is not "
            + "money, add it to the " + AllowedMembersOption + " property in its csproj.",
        helpLinkUri: "https://github.com/innovateavitech/trips-agent/blob/main/CLAUDE.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The allowlist is read once per compilation rather than once per symbol — parsing it
        // for every property in the solution would be measurable on a large build.
        context.RegisterCompilationStartAction(start =>
        {
            var allowed = ReadAllowList(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
            start.RegisterSymbolAction(symbol => Analyze(symbol, allowed), SymbolKind.Property, SymbolKind.Field);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, ImmutableHashSet<string> allowed)
    {
        var symbol = context.Symbol;

        // Property backing fields are implicitly declared and would double-report the property.
        if (symbol.IsImplicitlyDeclared)
        {
            return;
        }

        ITypeSymbol type;
        switch (symbol)
        {
            case IPropertySymbol property:
                type = property.Type;
                break;

            case IFieldSymbol field:
                // Enum members are fields of the enum type; they can never be floating point.
                if (field.ContainingType?.TypeKind == TypeKind.Enum)
                {
                    return;
                }

                type = field.Type;
                break;

            default:
                return;
        }

        if (!IsFloatingPoint(type) || !LooksLikeMoney(symbol.Name))
        {
            return;
        }

        if (IsAllowed(symbol, allowed))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None,
            symbol.Name,
            type.ToDisplayString()));
    }

    /// <summary>
    /// True for <c>decimal</c>, <c>double</c> and <c>float</c>, including their nullable forms —
    /// <c>decimal?</c> loses kobo exactly as reliably as <c>decimal</c>.
    /// </summary>
    public static bool IsFloatingPoint(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            type = named.TypeArguments[0];
        }

        return type.SpecialType == SpecialType.System_Decimal
            || type.SpecialType == SpecialType.System_Double
            || type.SpecialType == SpecialType.System_Single;
    }

    /// <summary>
    /// True when the name ends with one of <see cref="MoneySuffixes"/> on a PascalCase boundary.
    /// A leading underscore is stripped and the first letter capitalised first, so the private
    /// field <c>_totalFee</c> is treated the same as the property <c>TotalFee</c>.
    /// </summary>
    public static bool LooksLikeMoney(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var normalized = name!.TrimStart('_');
        if (normalized.Length == 0)
        {
            return false;
        }

        normalized = char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);

        // Ordinal, so the suffix must appear with its own capital letter — the PascalCase word
        // boundary. "Coffee" ends with "fee", not "Fee", and is therefore not money.
        return MoneySuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static bool IsAllowed(ISymbol symbol, ImmutableHashSet<string> allowed)
    {
        if (allowed.IsEmpty)
        {
            return false;
        }

        return allowed.Contains(symbol.Name)
            || (symbol.ContainingType is not null
                && allowed.Contains(symbol.ContainingType.Name + "." + symbol.Name));
    }

    private static ImmutableHashSet<string> ReadAllowList(AnalyzerConfigOptions options)
    {
        if (!options.TryGetValue("build_property." + AllowedMembersOption, out var raw)
            && !options.TryGetValue(AllowedMembersOption, out raw))
        {
            return ImmutableHashSet<string>.Empty;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ImmutableHashSet<string>.Empty;
        }

        var entries = raw.Split(AllowListSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0);

        return ImmutableHashSet.CreateRange(StringComparer.Ordinal, entries);
    }
}
