using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TripsAgent.Analyzers;

/// <summary>
/// An explicit list of types and members exempt from one analyser's rule, read from a text file
/// handed to the compiler: <c>backend/MoneyTypeAllowlist.txt</c> for <c>TRIPS001</c>,
/// <c>backend/IgnoreQueryFiltersAllowlist.txt</c> for <c>TRIPS002</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every rule enforced by the compiler has a few legitimate exceptions. They go in one file per
/// rule so each exception is visible in one place, and every new one shows up in a pull request
/// where someone has to agree with it.
/// </para>
/// <para>
/// One entry per line, <c>#</c> starts a comment. An entry is either a type
/// (<c>TripsAgent.Documents.Formatting.AmountFormatter</c>, nested types joined with dots), which
/// allows everything inside it, or a single member (<c>TripsAgent.Domain.Common.Money.Percentage</c>),
/// which allows that member and its parameters and locals. All overloads of a method share one
/// name. Namespaces are deliberately <b>not</b> accepted: allowing a whole namespace is far too
/// broad an exception to either rule.
/// </para>
/// </remarks>
internal sealed class SymbolAllowlist
{
    // Namespace.Outer.Inner — no "global::", no generic arguments.
    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    private readonly HashSet<string> _entries;

    private SymbolAllowlist(HashSet<string> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Reads every additional file named <paramref name="fileName"/>. Having none is fine — it
    /// means no exceptions, which is the strictest reading.
    /// </summary>
    public static SymbolAllowlist Load(
        ImmutableArray<AdditionalText> files,
        string fileName,
        CancellationToken cancellationToken)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!string.Equals(Path.GetFileName(file.Path), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = file.GetText(cancellationToken);
            if (text is null)
            {
                continue;
            }

            foreach (var line in text.Lines)
            {
                var entry = line.ToString();

                var comment = entry.IndexOf('#');
                if (comment >= 0)
                {
                    entry = entry.Substring(0, comment);
                }

                entry = entry.Trim();
                if (entry.Length > 0)
                {
                    entries.Add(entry);
                }
            }
        }

        return new SymbolAllowlist(entries);
    }

    /// <summary>
    /// True when the symbol, or any member or type it sits inside, is on the list.
    /// </summary>
    public bool Allows(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (_entries.Contains(FullNameOf(current)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The name an allowlist entry uses for <paramref name="symbol"/> — what a diagnostic tells a
    /// developer to add, so it is exactly the string the file expects.
    /// </summary>
    public static string FullNameOf(ISymbol symbol) =>
        symbol is INamedTypeSymbol type
            ? type.ToDisplayString(TypeNameFormat)
            : $"{FullNameOf(symbol.ContainingSymbol)}.{symbol.Name}";
}
