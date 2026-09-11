using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>Finds the adapter for a supplier and product.</summary>
public interface ISupplierAdapterRegistry
{
    /// <summary>Every registered adapter.</summary>
    public IReadOnlyCollection<ISupplierAdapter> All { get; }

    /// <summary>The adapter for <paramref name="supplierCode"/> and <paramref name="productType"/>.</summary>
    /// <exception cref="InvalidOperationException">No adapter is registered for that pair.</exception>
    public ISupplierAdapter Resolve(string supplierCode, SupplierProductType productType);

    /// <summary>As <see cref="Resolve"/>, without throwing when there is none.</summary>
    public bool TryResolve(string supplierCode, SupplierProductType productType, out ISupplierAdapter? adapter);
}

/// <summary>
/// Looks adapters up by <c>suppliers.code</c> and product, from whatever the container registered.
/// </summary>
/// <remarks>
/// <para>
/// This is where "a second aggregator needs no schema change" becomes true in code as well as in the
/// database: the new aggregator is a new <see cref="ISupplierAdapter"/> registration plus a new
/// <c>suppliers</c> row. Nothing here names Trips Africa.
/// </para>
/// <para>
/// Two adapters claiming the same supplier and product fail at construction rather than letting
/// whichever registered last win. Which adapter issues a real ticket must never depend on the
/// order of lines in a startup file.
/// </para>
/// </remarks>
public sealed class SupplierAdapterRegistry : ISupplierAdapterRegistry
{
    private readonly Dictionary<(string Code, SupplierProductType Product), ISupplierAdapter> _adapters = [];

    public SupplierAdapterRegistry(IEnumerable<ISupplierAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var all = adapters.ToList();

        foreach (var adapter in all)
        {
            if (!Supplier.IsValidCode(adapter.SupplierCode))
            {
                throw new InvalidOperationException(
                    $"{adapter.GetType().Name} declares supplier code '{adapter.SupplierCode}', which is not a valid "
                    + "suppliers.code — lower-case letters, digits and underscores, starting with a letter.");
            }

            if (adapter.Products.Count == 0)
            {
                throw new InvalidOperationException($"{adapter.GetType().Name} declares no products, so nothing could ever reach it.");
            }

            foreach (var product in adapter.Products)
            {
                if (_adapters.TryGetValue((adapter.SupplierCode, product), out var existing))
                {
                    throw new InvalidOperationException(
                        $"Both {existing.GetType().Name} and {adapter.GetType().Name} are registered for "
                        + $"'{adapter.SupplierCode}' {product}. Exactly one adapter may serve a supplier and product.");
                }

                _adapters[(adapter.SupplierCode, product)] = adapter;
            }
        }

        All = all;
    }

    public IReadOnlyCollection<ISupplierAdapter> All { get; }

    public ISupplierAdapter Resolve(string supplierCode, SupplierProductType productType) =>
        TryResolve(supplierCode, productType, out var adapter)
            ? adapter!
            : throw new InvalidOperationException(
                $"No supplier adapter is registered for '{supplierCode}' {productType}. "
                + "Register one in the host's startup, beside the other supplier integrations.");

    public bool TryResolve(string supplierCode, SupplierProductType productType, out ISupplierAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(supplierCode);

        return _adapters.TryGetValue((supplierCode, productType), out adapter);
    }
}
