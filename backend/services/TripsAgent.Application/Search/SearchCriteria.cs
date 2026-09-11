using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TripsAgent.Application.Suppliers;

namespace TripsAgent.Application.Search;

/// <summary>
/// A search's criteria in one canonical form, and its SHA-256 — the search cache key (#40) and
/// <c>search_requests.criteria_hash</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two searches that mean the same thing must hash the same, or the cache never hits: "los" and
/// "LOS", or the same date typed two ways, are one search. So airports are upper-cased, dates are
/// ISO <c>yyyy-MM-dd</c>, the cabin is lower-cased with economy as the default, and the keys are
/// written in one fixed order — the declaration order below, which never varies between runs.
/// </para>
/// <para>
/// Legs are <b>not</b> sorted: Lagos → Abuja then Abuja → Lagos is a different trip from the other
/// way round. The agency is not in here either — it is part of the cache key instead, beside this
/// hash, so the hash stays comparable across agencies for the conversion report.
/// </para>
/// </remarks>
public sealed record SearchCriteria(string Json, string Hash)
{
    public static SearchCriteria Normalise(SupplierSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var canonical = new
        {
            product = query.ProductType.ToString(),
            shape = query.TripShape.ToString(),
            legs = query.Legs.Select(leg => new
            {
                origin = leg.Origin.Trim().ToUpperInvariant(),
                destination = leg.Destination.Trim().ToUpperInvariant(),
                date = leg.DepartureDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }),
            passengers = new
            {
                adults = query.Passengers.Adults,
                children = query.Passengers.Children,
                infants = query.Passengers.Infants,
            },
            cabin = string.IsNullOrWhiteSpace(query.Cabin) ? "economy" : query.Cabin.Trim().ToLowerInvariant(),
            page = Math.Max(query.Page, 1),
        };

        var json = JsonSerializer.Serialize(canonical);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        return new SearchCriteria(json, hash);
    }
}
