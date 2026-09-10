using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using TripsAgent.Analyzers;

namespace TripsAgent.AnalyzerTests;

/// <summary>
/// Proves TRIPS001 fires on money held as decimal/double/float, stays quiet on everything else,
/// and respects the allowlist.
/// </summary>
/// <remarks>
/// Both halves matter. An analyser that never fires would pass every "stays quiet" test, and one
/// that fires on everything would pass every "fires" test — only together do they show it works.
/// </remarks>
public class MoneyTypeAnalyzerTests
{
    private const string AllowlistPath = "/repo/backend/MoneyTypeAllowlist.txt";

    // ------------------------------------------------------------------ it fires

    [Theory]
    [InlineData("decimal", "Amount")]
    [InlineData("double", "NetPrice")]
    [InlineData("float", "BaseFare")]
    [InlineData("decimal", "WalletBalance")]
    [InlineData("decimal", "ServiceFee")]
    [InlineData("decimal", "PriceMinor")]
    [InlineData("decimal", "Fees")]
    public async Task A_money_named_property_typed_floating_point_fails(string type, string name)
    {
        var diagnostics = await Analyze($"public class Quote {{ public {type} {name} {{ get; set; }} }}");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(MoneyTypeAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task A_money_named_field_fails()
    {
        var diagnostics = await Analyze("public class Wallet { private decimal _balance; public decimal Get() => _balance; }");

        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'_balance'");
    }

    [Fact]
    public async Task A_money_named_constant_in_screaming_case_fails()
    {
        var diagnostics = await Analyze("public static class Fees { public const decimal SERVICE_FEE = 100m; }");

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task A_money_named_parameter_fails()
    {
        var diagnostics = await Analyze("public class Checkout { public void Charge(decimal amount) { } }");

        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'amount'");
    }

    [Fact]
    public async Task A_money_named_local_fails()
    {
        // The exact example from CONTRIBUTING.md, which promises "the build will fail".
        const string source = """
            public class Checkout
            {
                public long Total()
                {
                    decimal price = 1500.00m;
                    return (long)price;
                }
            }
            """;

        var diagnostics = await Analyze(source);

        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'price'");
    }

    [Theory]
    [InlineData("decimal?")]
    [InlineData("double[]")]
    [InlineData("System.Collections.Generic.List<decimal>")]
    public async Task Floating_point_hidden_inside_another_type_still_fails(string type)
    {
        var diagnostics = await Analyze($"public class Quote {{ public {type} Price {{ get; set; }} = default!; }}");

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task A_positional_record_is_reported_once_not_twice()
    {
        // The parameter and the property it generates are the same mistake on the same line.
        var diagnostics = await Analyze("public record Quote(decimal Price);");

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task The_failure_is_a_build_error_not_a_warning()
    {
        var diagnostics = await Analyze("public class Quote { public decimal Price { get; set; } }");

        diagnostics.Should().ContainSingle().Which.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task The_message_says_what_to_do_instead()
    {
        var diagnostics = await Analyze("public class Quote { public decimal NetPrice { get; set; } }");

        var message = diagnostics.Should().ContainSingle().Which.GetMessage();
        message.Should().Contain("'decimal'");
        message.Should().Contain("long NetPriceMinor");
        message.Should().Contain("MoneyTypeAllowlist.txt");
    }

    // ------------------------------------------------------------ it stays quiet

    [Theory]
    [InlineData("long", "PriceMinor")]            // the right way
    [InlineData("long", "WalletBalanceMinor")]    // the right way
    [InlineData("decimal", "MarkupPercent")]      // a ratio, not an amount
    [InlineData("double", "ExchangeRate")]        // a ratio, not an amount
    [InlineData("int", "Amount")]                 // a whole number is fine
    [InlineData("decimal", "Coffee")]             // ends in "fee", but not as a word
    [InlineData("decimal", "Welfare")]            // ends in "fare", but not as a word
    [InlineData("double", "Latitude")]
    public async Task Everything_else_is_left_alone(string type, string name)
    {
        var diagnostics = await Analyze($"public class Thing {{ public {type} {name} {{ get; set; }} }}");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Generated_code_is_skipped()
    {
        // EF Core migrations and other tool output are marked like this, and nobody edits them.
        var diagnostics = await Analyze("""
            // <auto-generated />
            public class Snapshot { public decimal Price { get; set; } }
            """);

        diagnostics.Should().BeEmpty();
    }

    // ------------------------------------------------------------- the allowlist

    [Fact]
    public async Task An_allowlisted_type_may_hold_money_named_decimals()
    {
        const string source = """
            namespace Shop.Formatting
            {
                public static class AmountFormatter
                {
                    public static string Format(long amountMinor)
                    {
                        decimal amount = amountMinor / 100m;
                        return amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            """;

        var diagnostics = await Analyze(source, allowlist: "Shop.Formatting.AmountFormatter");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_allowlisted_member_allows_only_that_member()
    {
        const string source = """
            namespace Shop
            {
                public class Pricing
                {
                    public decimal DisplayPrice { get; set; }

                    public decimal NetPrice { get; set; }
                }
            }
            """;

        var diagnostics = await Analyze(source, allowlist: "Shop.Pricing.DisplayPrice");

        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'NetPrice'");
    }

    [Fact]
    public async Task A_nested_type_is_listed_with_dots()
    {
        const string source = """
            namespace Shop
            {
                public class Outer
                {
                    public class Probe { public decimal Price { get; set; } }
                }
            }
            """;

        var diagnostics = await Analyze(source, allowlist: "Shop.Outer.Probe");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Comments_and_blank_lines_in_the_allowlist_are_ignored()
    {
        const string allowlist = """
            # Every entry carries a reason.

            Shop.Legacy   # a trailing comment on the same line
            """;

        var diagnostics = await Analyze(
            "namespace Shop { public class Legacy { public decimal Price { get; set; } } }",
            allowlist);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_namespace_cannot_be_allowlisted()
    {
        // Allowing a whole namespace would be too broad an exception to a rule about money.
        var diagnostics = await Analyze(
            "namespace Shop { public class Legacy { public decimal Price { get; set; } } }",
            allowlist: "Shop");

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task A_file_with_a_different_name_is_not_treated_as_the_allowlist()
    {
        var diagnostics = await AnalyzerHarness.RunAsync(
            new MoneyTypeAnalyzer(),
            "namespace Shop { public class Legacy { public decimal Price { get; set; } } }",
            new Dictionary<string, string> { ["/repo/backend/SomethingElse.txt"] = "Shop.Legacy" });

        diagnostics.Should().ContainSingle();
    }

    // ------------------------------------------------------------------ helpers

    private static Task<ImmutableArray<Diagnostic>> Analyze(string source, string? allowlist = null) =>
        AnalyzerHarness.RunAsync(
            new MoneyTypeAnalyzer(),
            source,
            allowlist is null ? null : new Dictionary<string, string> { [AllowlistPath] = allowlist });
}
