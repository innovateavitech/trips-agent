namespace TripsAgent.Domain.Tenancy.SubAgents;

/// <summary>
/// A kind of thing an agency can sell, as a sub-agent's scope names it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately its own enum rather than a reuse of <c>SupplierProductType</c>. That one covers
/// only what the supplier sells — flights and bus seats — and a scope has to be able to say
/// "tours but not visas" about the catalog products an agency writes itself.
/// </para>
/// <para>
/// <see cref="Flight"/> and <see cref="Bus"/> are the two the MVP can actually enforce, because
/// they are the two that have a search and a checkout today. The catalog types are here so the
/// column does not need a migration when the catalog lands; nothing reads them yet.
/// </para>
/// </remarks>
public enum SellableProductType
{
    Flight = 1,
    Bus = 2,
    Tour = 3,
    Visa = 4,
    Package = 5,
}
