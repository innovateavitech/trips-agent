using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TripsAgent.Analyzers;

/// <summary>
/// The explicit list of types and members allowed to hold a money-named <c>decimal</c>,
/// <c>double</c> or <c>float</c> — read from <c>backend/MoneyTypeAllowlist.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// There are a few legitimate cases — a display-formatting helper that turns kobo into
/// "1,500.00", percentage maths, a deliberately-wrong entity in a test. They go in one file so
/// every exception is visible in one place and every new one shows up in a pull request.
/// </para>
/// <para>
/// One entry per line, <c>#</c> starts a comment. An entry is either a type
/// (<c>TripsAgent.Documents.Formatting.AmountFormatter</c>, nested types joined with dots), which
/// allows everything inside it, or a single member (<c>TripsAgent.Domain.Common.Money.Percentage</c>),
/// which allows that member and its parameters and locals. All overloads of a method share one
/// name. Namespaces are deliberately <b>not</b> accepted: allowing a whole namespace is far too
/// broad an exception to a rule about money.
/// </para>
/// </remarks>
internal sealed class MoneyTypeAllowlist
{
    /// <summary>The file name the analyser looks for among the project's additional files.</summary>
    public const string FileName = "MoneyTypeAllowlist.txt";

    // Namespace.Outer.Inner — no "global::", no generic arguments.
    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    private readonly HashSet<string> _entries;

    private MoneyTypeAllowlist(HashSet<string> entries)
    {
        _entries = entries;
    }

    /// <summary>Reads every allowlist file handed to the compiler. Having none is fine.</summary>
    public static MoneyTypeAllowlist Load(ImmutableArray<AdditionalText> files, CancellationToken cancellationToken)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!string.Equals(Path.GetFileName(file.Path), FileName, StringComparison.OrdinalIgnoreCase))
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

        return new MoneyTypeAllowlist(entries);
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

    private static string FullNameOf(ISymbol symbol) =>
        symbol is INamedTypeSymbol type
            ? type.ToDisplayString(TypeNameFormat)
            : $"{FullNameOf(symbol.ContainingSymbol)}.{symbol.Name}";
}
