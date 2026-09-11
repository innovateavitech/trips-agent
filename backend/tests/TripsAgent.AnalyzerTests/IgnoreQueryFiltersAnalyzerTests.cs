using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using TripsAgent.Analyzers;

namespace TripsAgent.AnalyzerTests;

/// <summary>
/// Proves TRIPS002 fires on every way of calling <c>IgnoreQueryFilters()</c> from production code,
/// stays quiet in tests and on look-alikes, and respects the allowlist.
/// </summary>
/// <remarks>
/// As with TRIPS001, both halves matter: an analyser that never fires passes every "stays quiet"
/// test, and one that fires on everything passes every "fires" test.
/// </remarks>
public class IgnoreQueryFiltersAnalyzerTests
{
    private const string AllowlistPath = "/repo/backend/IgnoreQueryFiltersAllowlist.txt";

    /// <summary>
    /// A stand-in for EF Core's extension method. The analyser matches on the declaring type's
    /// full name, so the stub only has to live at the same name — the tests need no EF reference.
    /// </summary>
    private const string EfCore = """
        namespace Microsoft.EntityFrameworkCore
        {
            public static class EntityFrameworkQueryableExtensions
            {
                public static System.Linq.IQueryable<T> IgnoreQueryFilters<T>(this System.Linq.IQueryable<T> source) => source;
            }
        }
        """;

    // ------------------------------------------------------------------ it fires

    [Fact]
    public async Task A_call_in_production_code_fails()
    {
        var diagnostics = await Analyze("""
            namespace Shop
            {
                using System.Linq;
                using Microsoft.EntityFrameworkCore;

                public class Reports
                {
                    public int CountAll(IQueryable<string> rows) => rows.IgnoreQueryFilters().Count();
                }
            }
            """);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(IgnoreQueryFiltersAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task The_failure_is_a_build_error_not_a_warning()
    {
        var diagnostics = await Analyze(Reports("rows.IgnoreQueryFilters().Count()"));

        diagnostics.Should().ContainSingle().Which.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task The_message_names_the_member_and_the_way_out()
    {
        var diagnostics = await Analyze(Reports("rows.IgnoreQueryFilters().Count()"));

        var message = diagnostics.Should().ContainSingle().Which.GetMessage();

        // Exactly the string the allowlist expects, so nobody has to work out the format.
        message.Should().Contain("'Shop.Reports.CountAll'");
        message.Should().Contain("IPlatformScope.Enter(reason)");
        message.Should().Contain("IgnoreQueryFiltersAllowlist.txt");
    }

    [Fact]
    public async Task Calling_it_as_a_static_method_still_fails()
    {
        var diagnostics = await Analyze(Reports("EntityFrameworkQueryableExtensions.IgnoreQueryFilters(rows).Count()"));

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task Passing_it_as_a_method_group_still_fails()
    {
        var diagnostics = await Analyze("""
            namespace Shop
            {
                using System;
                using System.Linq;
                using Microsoft.EntityFrameworkCore;

                public class Reports
                {
                    public int CountAll(IQueryable<string> rows)
                    {
                        Func<IQueryable<string>, IQueryable<string>> unfiltered =
                            EntityFrameworkQueryableExtensions.IgnoreQueryFilters<string>;

                        return unfiltered(rows).Count();
                    }
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task A_call_inside_a_lambda_is_attributed_to_the_member_that_contains_it()
    {
        var diagnostics = await Analyze("""
            namespace Shop
            {
                using System;
                using System.Linq;
                using Microsoft.EntityFrameworkCore;

                public class Reports
                {
                    public Func<IQueryable<string>, int> Build() => rows => rows.IgnoreQueryFilters().Count();
                }
            }
            """);

        // Not "<lambda>" — an allowlist entry has to name something a reviewer can find.
        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'Shop.Reports.Build'");
    }

    [Fact]
    public async Task Every_call_is_reported()
    {
        var diagnostics = await Analyze(Reports("rows.IgnoreQueryFilters().Count() + rows.IgnoreQueryFilters().Count()"));

        diagnostics.Should().HaveCount(2);
    }

    // ------------------------------------------------------------ it stays quiet

    [Fact]
    public async Task Test_assemblies_are_exempt()
    {
        // Proving isolation means switching the filter off on purpose and showing what still
        // stops a leak. That is what the integration tests do.
        var diagnostics = await Analyze(
            Reports("rows.IgnoreQueryFilters().Count()"),
            assemblyName: "TripsAgent.IntegrationTests");

        diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("TripsAgent.TestUtilities")]   // contains "Test", does not end in "Tests"
    [InlineData("TripsAgent.Infrastructure")]
    [InlineData("TripsAgent.integrationtests")] // the exemption is case-sensitive
    public async Task An_assembly_that_is_not_a_test_assembly_is_checked(string assemblyName)
    {
        var diagnostics = await Analyze(Reports("rows.IgnoreQueryFilters().Count()"), assemblyName: assemblyName);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task A_method_of_the_same_name_on_another_type_is_left_alone()
    {
        var diagnostics = await Analyze("""
            namespace Shop
            {
                using System.Linq;

                public static class LocalExtensions
                {
                    public static IQueryable<T> IgnoreQueryFilters<T>(this IQueryable<T> source) => source;
                }

                public class Reports
                {
                    public int CountAll(IQueryable<string> rows) => rows.IgnoreQueryFilters().Count();
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Mentioning_it_in_a_comment_or_a_string_is_left_alone()
    {
        // The codebase explains why it does NOT use IgnoreQueryFilters in several doc comments.
        // A text search would flag every one of them; a semantic analyser does not.
        var diagnostics = await Analyze("""
            namespace Shop
            {
                /// <summary>Deliberately not <c>IgnoreQueryFilters()</c>.</summary>
                public class Reports
                {
                    // rows.IgnoreQueryFilters() would read every agency.
                    public string Why() => "never call IgnoreQueryFilters() here";
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Generated_code_is_skipped()
    {
        // Roslyn only honours the marker in a file's leading comments, so it goes above the EF
        // stub rather than through Analyze(), which would put the stub first.
        var diagnostics = await AnalyzerHarness.RunAsync(
            new IgnoreQueryFiltersAnalyzer(),
            "// <auto-generated />\n" + EfCore + "\n" + Reports("rows.IgnoreQueryFilters().Count()"));

        diagnostics.Should().BeEmpty();
    }

    // ------------------------------------------------------------- the allowlist

    [Fact]
    public async Task An_allowlisted_member_allows_only_that_member()
    {
        var diagnostics = await Analyze(
            """
            namespace Shop
            {
                using System.Linq;
                using Microsoft.EntityFrameworkCore;

                public class Reports
                {
                    public int PlatformTotals(IQueryable<string> rows) => rows.IgnoreQueryFilters().Count();

                    public int AgencyTotals(IQueryable<string> rows) => rows.IgnoreQueryFilters().Count();
                }
            }
            """,
            allowlist: "Shop.Reports.PlatformTotals   # platform-wide revenue report, reviewed in #999");

        diagnostics.Should().ContainSingle().Which.GetMessage().Should().Contain("'Shop.Reports.AgencyTotals'");
    }

    [Fact]
    public async Task An_allowlisted_type_allows_everything_inside_it()
    {
        var diagnostics = await Analyze(Reports("rows.IgnoreQueryFilters().Count()"), allowlist: "Shop.Reports");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_namespace_cannot_be_allowlisted()
    {
        var diagnostics = await Analyze(Reports("rows.IgnoreQueryFilters().Count()"), allowlist: "Shop");

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task The_money_allowlist_does_not_allow_tenant_bypasses()
    {
        // Each rule reads only its own file. An exception to the money rule must never quietly
        // become an exception to the tenancy rule.
        var diagnostics = await AnalyzerHarness.RunAsync(
            new IgnoreQueryFiltersAnalyzer(),
            EfCore + Reports("rows.IgnoreQueryFilters().Count()"),
            new Dictionary<string, string> { ["/repo/backend/MoneyTypeAllowlist.txt"] = "Shop.Reports" });

        diagnostics.Should().ContainSingle();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A class with one method whose body is <paramref name="expression"/>.</summary>
    private static string Reports(string expression) => $$"""
        namespace Shop
        {
            using System.Linq;
            using Microsoft.EntityFrameworkCore;

            public class Reports
            {
                public int CountAll(IQueryable<string> rows) => {{expression}};
            }
        }
        """;

    private static Task<ImmutableArray<Diagnostic>> Analyze(
        string source,
        string? allowlist = null,
        string assemblyName = "Probe") =>
        AnalyzerHarness.RunAsync(
            new IgnoreQueryFiltersAnalyzer(),
            EfCore + "\n" + source,
            allowlist is null ? null : new Dictionary<string, string> { [AllowlistPath] = allowlist },
            assemblyName);
}
