using FluentAssertions;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// #35: a single tampered element aborts the whole checkout <b>and raises a security alert</b>.
/// </summary>
public class PriceConfirmationServiceTests
{
    private readonly RecordingAlerter _alerter = new();

    [Fact]
    public async Task A_tampered_element_blocks_the_booking_and_raises_one_p1_alert_naming_it()
    {
        var booking = NewBooking();
        var service = Service(Line("1|A", "hash-1", "hash-1"), Line("2|B", "hash-2", "hash-X"), Line("3|C", "hash-3", "hash-3"));

        await service.ConfirmAsync(booking, "trips_africa", Request());

        booking.Status.Should().Be(SupplierBookingStatus.PriceRejected);

        var alert = _alerter.Alerts.Should().ContainSingle().Subject;
        alert.Severity.Should().Be(AlertSeverity.P1);
        alert.AgencyId.Should().Be(booking.AgencyId);
        alert.Detail.Should().Contain(booking.Id.ToString())
            .And.Contain("element 2 (2|B)")
            .And.NotContain("element 1")
            .And.NotContain("hash-", "the alert names the elements, never what was hashed");
    }

    [Fact]
    public async Task An_honest_confirmation_leaves_the_booking_ready_and_raises_nothing()
    {
        var booking = NewBooking();
        var service = Service(Line("1|A", "hash-1", "hash-1"), Line("2|B", "hash-2", "HASH-2"));

        await service.ConfirmAsync(booking, "trips_africa", Request());

        booking.Status.Should().Be(SupplierBookingStatus.PriceConfirmed);
        _alerter.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Price_confirmation_cannot_reach_the_search_cache()
    {
        // #40: the cache is bypassed entirely for confirmation. Enforced by construction — the
        // service is never handed the cache — so no later edit inside it can read a cached price.
        typeof(PriceConfirmationService).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Should().NotContain(typeof(TripsAgent.Application.Search.ISearchResultCache));
    }

    private PriceConfirmationService Service(params PriceConfirmationLine[] lines) =>
        new(new SupplierAdapterRegistry([new ScriptedAdapter(lines)]), _alerter, TimeProvider.System);

    private static PriceConfirmationLine Line(string code, string expected, string received) =>
        new(code, new Money(2_167_800), new Money(2_167_800), TicketTimeLimit: null, expected, received);

    private static SupplierPriceConfirmationRequest Request() =>
        new(
            SupplierProductType.Bus,
            "sess-bus-0001",
            "169:103:1:0:0",
            new SupplierOfferReference("169", "103", "1", "0", 0),
            [new SupplierPassenger(PassengerType.Adult, "Ngozi", "Adeyemi")]);

    private static SupplierBooking NewBooking() =>
        SupplierBooking.Create(
            agencyId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            orderLineId: Guid.CreateVersion7(),
            supplierOfferId: null,
            productType: SupplierProductType.Bus,
            tripType: "Domestic",
            tripMode: "Road",
            supplierSessionId: "sess-bus-0001",
            currency: "NGN",
            idempotencyKey: "key-0001");

    private sealed class RecordingAlerter : IPlatformAlerter
    {
        public List<PlatformAlert> Alerts { get; } = [];

        public Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    /// <summary>A bus supplier whose confirmation answers with exactly the lines the test gives it.</summary>
    private sealed class ScriptedAdapter(IReadOnlyList<PriceConfirmationLine> lines) : ISupplierAdapter
    {
        public string SupplierCode => "trips_africa";

        public IReadOnlyCollection<SupplierProductType> Products { get; } = [SupplierProductType.Bus];

        public Task<SupplierPriceConfirmation> ConfirmPriceAsync(
            SupplierCallContext context,
            SupplierPriceConfirmationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupplierPriceConfirmation(request.SupplierSessionId, "Domestic", "Road", lines));

        public Task<SupplierSearchResult> SearchAsync(
            SupplierCallContext context, SupplierSearchQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierIssueResult> IssueAsync(
            SupplierCallContext context, SupplierIssueRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierStatusResult> GetStatusAsync(
            SupplierCallContext context, SupplierStatusQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierFareRules> GetRulesAsync(
            SupplierCallContext context, SupplierRulesQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SupplierCancellationResult> CancelAsync(
            SupplierCallContext context, SupplierCancellationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
