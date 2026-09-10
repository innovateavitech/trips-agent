using System.Reflection;
using NetArchTest.Rules;

namespace TripsAgent.ArchitectureTests;

/// <summary>
/// These tests enforce the layering rule from CLAUDE.md:
///
///     Api ──▶ Application ──▶ Domain ◀── Infrastructure
///
/// Arrows never point backwards. If one of these fails, the fix is almost never
/// "add a reference" — it is to move the type, or introduce an interface in the
/// inner layer that the outer layer implements.
/// </summary>
public class LayeringTests
{
    private static readonly Assembly Domain =
        typeof(TripsAgent.Domain.AssemblyMarker).Assembly;

    private static readonly Assembly Application =
        typeof(TripsAgent.Application.AssemblyMarker).Assembly;

    [Fact]
    public void Domain_should_not_depend_on_any_other_layer()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "TripsAgent.Application",
                "TripsAgent.Infrastructure",
                "TripsAgent.Api",
                "TripsAgent.Worker",
                "TripsAgent.Contracts")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
             Domain must not depend on any other layer — that is what makes business rules
             testable without a database or a web server.

             Offending types: {Format(result)}

             Fix: move the type into Domain, or define an interface in Domain that the outer
             layer implements. Do not add a project reference.
             """);
    }

    [Fact]
    public void Domain_should_not_depend_on_EntityFramework()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
             Domain must stay persistence-ignorant. EF Core belongs in Infrastructure.

             Offending types: {Format(result)}

             Fix: configure the entity with IEntityTypeConfiguration<T> in
             Infrastructure/Persistence/Configurations/ instead of annotating the domain type.
             """);
    }

    [Fact]
    public void Application_should_not_depend_on_Infrastructure()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOn("TripsAgent.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"""
             Application defines WHAT happens; Infrastructure decides HOW. The dependency
             points inward, never outward.

             Offending types: {Format(result)}

             Fix: declare an interface in Application and implement it in Infrastructure.
             """);
    }

    private static string Format(TestResult result) =>
        result.FailingTypeNames is null
            ? "(none reported)"
            : string.Join(", ", result.FailingTypeNames);
}
