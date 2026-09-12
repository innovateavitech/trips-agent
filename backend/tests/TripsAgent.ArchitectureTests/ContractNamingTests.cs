using System.Reflection;

namespace TripsAgent.ArchitectureTests;

/// <summary>
/// Every public contract type has to have a name no other contract type shares.
/// </summary>
/// <remarks>
/// <para>
/// The OpenAPI document keys its schemas by a type's <b>short name alone</b> — namespaces are not
/// part of the key. Two records called <c>BookingDocumentResponse</c> in different namespaces are
/// therefore one schema in the document, and one type in the generated TypeScript: whichever the
/// generator reached last wins, and every screen typed against the other silently describes the
/// wrong shape.
/// </para>
/// <para>
/// That happened once, between the agency's document list and the traveller's manage-my-booking
/// page, and the only sign was an unrelated app failing to compile. C# is perfectly happy with the
/// collision; this test is what makes the build unhappy instead.
/// </para>
/// </remarks>
public class ContractNamingTests
{
    private static readonly Assembly Contracts =
        typeof(TripsAgent.Contracts.Commerce.ManageBookingResponse).Assembly;

    [Fact]
    public void No_two_contract_types_share_a_name()
    {
        var clashes = Contracts.GetExportedTypes()
            .Where(type => !type.IsNested)
            .GroupBy(type => type.Name)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(" and ", group.Select(type => type.FullName))}")
            .ToList();

        Assert.True(
            clashes.Count == 0,
            $"""
             Two contract types share a name, so they share one OpenAPI schema and one generated
             TypeScript type. The frontend will be typed against whichever of them the generator
             happened to reach last.

             Clashing: {string.Join("; ", clashes)}

             Fix: rename one of them after the thing it is for — the traveller's view of a document
             is not the agency's — and run `pnpm generate:api`.
             """);
    }
}
