using FluentAssertions;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// "A second aggregator needs no schema change" has a code half as well as a database half: the
/// new aggregator is one more adapter registration, and nothing else has to know.
/// </summary>
public class SupplierAdapterRegistryTests
{
    [Fact]
    public void Adapters_are_found_by_supplier_code_and_product()
    {
        var flights = new FakeAdapter("trips_africa", SupplierProductType.Flight);
        var buses = new FakeAdapter("trips_africa", SupplierProductType.Bus);

        var registry = new SupplierAdapterRegistry([flights, buses]);

        registry.Resolve("trips_africa", SupplierProductType.Flight).Should().BeSameAs(flights);
        registry.Resolve("trips_africa", SupplierProductType.Bus).Should().BeSameAs(buses);
    }

    [Fact]
    public void A_second_aggregator_sits_beside_the_first_without_either_knowing()
    {
        var tripsAfrica = new FakeAdapter("trips_africa", SupplierProductType.Flight);
        var second = new FakeAdapter("acme_air", SupplierProductType.Flight, SupplierProductType.Bus);

        var registry = new SupplierAdapterRegistry([tripsAfrica, second]);

        registry.Resolve("trips_africa", SupplierProductType.Flight).Should().BeSameAs(tripsAfrica);
        registry.Resolve("acme_air", SupplierProductType.Flight).Should().BeSameAs(second);
        registry.Resolve("acme_air", SupplierProductType.Bus).Should().BeSameAs(second);
        registry.All.Should().HaveCount(2);
    }

    [Fact]
    public void Two_adapters_for_the_same_supplier_and_product_fail_at_startup()
    {
        // Which adapter issues a real ticket must never depend on the order of registration lines.
        var build = () => new SupplierAdapterRegistry(
            [new FakeAdapter("trips_africa", SupplierProductType.Flight), new FakeAdapter("trips_africa", SupplierProductType.Flight)]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*Exactly one adapter*");
    }

    [Fact]
    public void An_unknown_supplier_or_product_is_a_clear_failure_not_a_null()
    {
        var registry = new SupplierAdapterRegistry([new FakeAdapter("trips_africa", SupplierProductType.Flight)]);

        registry.TryResolve("trips_africa", SupplierProductType.Bus, out var adapter).Should().BeFalse();
        adapter.Should().BeNull();

        var resolve = () => registry.Resolve("nobody", SupplierProductType.Flight);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*'nobody'*");
    }

    [Theory]
    [InlineData("Trips Africa")]
    [InlineData("")]
    public void An_adapter_with_a_code_no_suppliers_row_could_have_is_refused(string code)
    {
        var build = () => new SupplierAdapterRegistry([new FakeAdapter(code, SupplierProductType.Flight)]);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_adapter_that_sells_nothing_is_refused()
    {
        var build = () => new SupplierAdapterRegistry([new FakeAdapter("trips_africa")]);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_empty_registry_is_valid_until_something_is_asked_of_it()
    {
        // The hosts start with no adapter registered until #33 lands one; that must not stop them.
        var registry = new SupplierAdapterRegistry([]);

        registry.All.Should().BeEmpty();
    }

    private sealed class FakeAdapter(string code, params SupplierProductType[] products) : ISupplierAdapter
    {
        public string SupplierCode => code;

        public IReadOnlyCollection<SupplierProductType> Products => products;

        public Task<SupplierSearchResult> SearchAsync(SupplierCallContext context, SupplierSearchQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierPriceConfirmation> ConfirmPriceAsync(SupplierCallContext context, SupplierPriceConfirmationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierIssueResult> IssueAsync(SupplierCallContext context, SupplierIssueRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierStatusResult> GetStatusAsync(SupplierCallContext context, SupplierStatusQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierFareRules> GetRulesAsync(SupplierCallContext context, SupplierRulesQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierCancellationResult> CancelAsync(SupplierCallContext context, SupplierCancellationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
