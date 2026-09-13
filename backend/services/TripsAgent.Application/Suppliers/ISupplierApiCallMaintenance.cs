namespace TripsAgent.Application.Suppliers;

/// <summary>What one maintenance run of <c>supplier.supplier_api_calls</c> did.</summary>
/// <param name="PartitionsEnsured">The monthly partitions that now exist, from the current month onwards.</param>
/// <param name="PartitionsExpired">The months past retention: dropped in a live run, only reported in a dry run.</param>
/// <param name="PartitionsDropped">How many whole months past retention were dropped. Always 0 in a dry run.</param>
/// <param name="RetentionMonths">The window the run applied.</param>
/// <param name="DryRun">True when expired months were reported and kept (issue 105).</param>
public sealed record SupplierApiCallMaintenanceResult(
    IReadOnlyList<string> PartitionsEnsured,
    IReadOnlyList<string> PartitionsExpired,
    int PartitionsDropped,
    int RetentionMonths,
    bool DryRun);

/// <summary>
/// Keeps the supplier call log's monthly partitions ahead of the calendar and drops expired months.
/// </summary>
/// <remarks>
/// Without it the table runs out of partitions and every supplier call fails to record — which, for
/// the adapters that record before they return, means every search fails. It is scheduled daily in
/// the Worker.
/// </remarks>
public interface ISupplierApiCallMaintenance
{
    public Task<SupplierApiCallMaintenanceResult> RunAsync(CancellationToken cancellationToken = default);
}
