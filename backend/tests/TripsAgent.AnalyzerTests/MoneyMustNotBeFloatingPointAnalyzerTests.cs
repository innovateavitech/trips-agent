using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TripsAgent.Analyzers;

namespace TripsAgent.AnalyzerTests;

/// <summary>
/// Proves TA0001 both fires and stays quiet.
/// </summary>
/// <remarks>
/// <para>
/// A rule that never fires is decoration; a rule that fires on correct code gets suppressed and
/// then ignored. Both halves are tested here, and the quiet cases outnumber the loud ones on
/// purpose — false positives are what kill an analyser.
/// </para>
/// <para>
/// These tests compile C# in memory and run the analyser over it, rather than using the Roslyn
/// testing harness. One fewer package to keep in version lockstep with the compiler, and the
/// mechanics stay readable to someone who has not met that harness before.
/// </para>
/// </remarks>
public class MoneyMustNotBeFloatingPointAnalyzerTests
{
    // ------------------------------------------------------------------ it fires

    [Theory]
    [InlineData("decimal")]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("decimal?")]
    [InlineData("double?")]
    public async Task A_floating_point_money_property_fails_the_build(string type)
    {
        var diagnostics = await AnalyzeAsync($$"""
            public class Order
            {
                public {{type}} TotalPrice { get; set; }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(MoneyMustNotBeFloatingPointAnalyzer.DiagnosticId, diagnostic.Id);

        // The severity is the whole point: TreatWarningsAsErrors could be turned off in a
        // csproj, but an Error cannot be downgraded by accident.
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("TotalAmount")]
    [InlineData("TicketPrice")]
    [InlineData("BaseFare")]
    [InlineData("WalletBalance")]
    [InlineData("ServiceFee")]
    public async Task Every_money_suffix_is_caught(string name)
    {
        var diagnostics = await AnalyzeAsync($$"""
            public class Line
            {
                public decimal {{name}} { get; set; }
            }
            """);

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task A_money_field_fails_the_build_including_a_private_camelCase_one()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Wallet
            {
                private decimal _currentBalance;
                public decimal LedgerAmount;
            }
            """);

        Assert.Equal(2, diagnostics.Length);
    }

    [Fact]
    public async Task The_message_names_the_member_and_the_fix()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Order
            {
                public decimal TotalPrice { get; set; }
            }
            """);

        var message = Assert.Single(diagnostics).GetMessage();

        // Acceptance criterion: the error must say what to do instead, not merely that the
        // developer is wrong. Someone hitting this for the first time should not need to
        // find a document.
        Assert.Contains("TotalPrice", message, StringComparison.Ordinal);
        Assert.Contains("long", message, StringComparison.Ordinal);
        Assert.Contains("TotalPriceMinor", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- it stays quiet

    [Theory]
    [InlineData("long TotalPriceMinor")]
    [InlineData("long WalletBalanceMinor")]
    [InlineData("int SeatCount")]
    [InlineData("string CustomerName")]
    public async Task Correctly_typed_money_is_accepted(string declaration)
    {
        var diagnostics = await AnalyzeAsync($$"""
            public class Order
            {
                public {{declaration}} { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Coffee")]      // ends with "fee", not "Fee"
    [InlineData("Warfare")]     // ends with "fare", not "Fare"
    [InlineData("Toffee")]
    public async Task A_word_that_merely_ends_in_a_suffix_is_not_money(string name)
    {
        var diagnostics = await AnalyzeAsync($$"""
            public class Menu
            {
                public decimal {{name}} { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_decimal_that_is_a_ratio_rather_than_an_amount_is_left_alone()
    {
        // Money.Percentage(decimal) is correct code. Rates, percentages and FX multipliers are
        // ratios, and a ratio in decimal is exactly right — see Money.cs.
        var diagnostics = await AnalyzeAsync("""
            public class MarkupRule
            {
                public decimal Percent { get; set; }
                public decimal TaxRate { get; set; }
                public decimal FxRate { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Method_parameters_and_locals_are_out_of_scope()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Calculator
            {
                public long Apply(long amountMinor, decimal percent)
                {
                    decimal rawPrice = amountMinor * percent;
                    return (long)rawPrice;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task An_enum_member_is_not_a_money_field()
    {
        var diagnostics = await AnalyzeAsync("""
            public enum Outcome
            {
                Balance,
                Price,
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_property_reports_once_not_twice_for_its_backing_field()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Order
            {
                public decimal TotalPrice { get; set; }
            }
            """);

        Assert.Single(diagnostics);
    }

    // ----------------------------------------------------------------- allowlist

    [Fact]
    public async Task An_allowlisted_member_is_permitted()
    {
        const string source = """
            public class Receipt
            {
                public decimal DisplayPrice { get; set; }
            }
            """;

        Assert.Single(await AnalyzeAsync(source));
        Assert.Empty(await AnalyzeAsync(source, allowedMembers: "DisplayPrice"));
    }

    [Fact]
    public async Task The_allowlist_can_be_scoped_to_one_type()
    {
        const string source = """
            public class Receipt
            {
                public decimal DisplayPrice { get; set; }
            }

            public class Order
            {
                public decimal DisplayPrice { get; set; }
            }
            """;

        // Unqualified, the entry allows the member name everywhere.
        Assert.Empty(await AnalyzeAsync(source, allowedMembers: "DisplayPrice"));

        // Qualified, it allows exactly one — so a blanket exemption is a deliberate choice
        // rather than something you get by accident.
        var scoped = await AnalyzeAsync(source, allowedMembers: "Receipt.DisplayPrice");
        var remaining = Assert.Single(scoped);
        Assert.Contains("DisplayPrice", remaining.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("Order", remaining.Location.SourceTree is null ? null : FindContainingClass(source, remaining));
    }

    [Fact]
    public async Task Allowlist_entries_tolerate_whitespace_and_both_separators()
    {
        const string source = """
            public class Receipt
            {
                public decimal DisplayPrice { get; set; }
                public decimal RoundedFee { get; set; }
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, allowedMembers: " DisplayPrice , RoundedFee "));
        Assert.Empty(await AnalyzeAsync(source, allowedMembers: "DisplayPrice;RoundedFee"));
    }

    [Fact]
    public async Task An_empty_allowlist_allows_nothing()
    {
        const string source = """
            public class Receipt
            {
                public decimal DisplayPrice { get; set; }
            }
            """;

        Assert.Single(await AnalyzeAsync(source, allowedMembers: ""));
        Assert.Single(await AnalyzeAsync(source, allowedMembers: "   "));
    }

    // ------------------------------------------------------------------ plumbing

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        string? allowedMembers = null)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerProbe",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: ReferenceAssemblies,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Any error in the probe source itself would make the assertions meaningless — a typo in
        // a test snippet would otherwise read as "the analyser found nothing".
        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(compileErrors.Length == 0, $"probe source failed to compile: {string.Join("; ", compileErrors.Select(e => e.GetMessage()))}");

        var options = new ProbeAnalyzerOptions(allowedMembers);

        var withAnalyzer = compilation.WithAnalyzers(
            [new MoneyMustNotBeFloatingPointAnalyzer()],
            options);

        return await withAnalyzer.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }

    private static string? FindContainingClass(string source, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.SourceSpan;
        var before = source[..span.Start];
        var index = before.LastIndexOf("public class ", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var rest = before[(index + "public class ".Length)..];
        var end = rest.IndexOfAny([' ', '\r', '\n', '{']);
        return end < 0 ? rest : rest[..end];
    }

    private static readonly ImmutableArray<MetadataReference> ReferenceAssemblies = LoadReferences();

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        // The reference set the test host itself was compiled against. Simpler and more honest
        // than pinning a reference-assembly package that then drifts from the target framework.
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

        return [.. trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    /// <summary>
    /// Supplies the one <c>.editorconfig</c> value the analyser reads, without needing a file
    /// on disk.
    /// </summary>
    private sealed class ProbeAnalyzerOptions(string? allowedMembers)
        : AnalyzerOptions([], new ProbeOptionsProvider(allowedMembers));

    private sealed class ProbeOptionsProvider(string? allowedMembers) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ProbeOptions(allowedMembers);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class ProbeOptions(string? allowedMembers) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (allowedMembers is not null
                && key == MoneyMustNotBeFloatingPointAnalyzer.AllowedMembersOption)
            {
                value = allowedMembers;
                return true;
            }

            value = null!;
            return false;
        }
    }
}
