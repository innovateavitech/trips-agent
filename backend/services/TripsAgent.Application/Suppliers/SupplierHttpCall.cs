using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// What the audit handler needs to know about one outgoing supplier request, attached to the
/// request itself.
/// </summary>
/// <remarks>
/// <para>
/// An adapter tags every <see cref="HttpRequestMessage"/> it sends with
/// <see cref="HttpRequestMessageSupplierExtensions.ForSupplierCall"/>. It travels in
/// <see cref="HttpRequestMessage.Options"/> rather than in a scoped service because
/// <c>IHttpClientFactory</c> builds handlers in a scope of its own: a scoped service resolved
/// there is never the request's.
/// </para>
/// <para>
/// A supplier request without one is refused before it is sent — see <c>SupplierAuditHandler</c>.
/// </para>
/// </remarks>
/// <param name="SupplierId">The <c>suppliers</c> row the call goes to.</param>
/// <param name="Operation">Which adapter operation this is — search, issue, and so on.</param>
/// <param name="Context">Who the call is for: agency, booking and correlation id.</param>
public sealed record SupplierHttpCall(Guid SupplierId, SupplierOperation Operation, SupplierCallContext Context)
{
    /// <summary>Where the call's details are kept on the request.</summary>
    public static readonly HttpRequestOptionsKey<SupplierHttpCall> OptionKey = new("TripsAgent.SupplierHttpCall");

    /// <summary>
    /// Where the audit handler leaves the id of the <c>supplier_api_calls</c> row it is writing, so
    /// an adapter can point a <c>SupplierStatusPoll</c> at the call behind it. The row is written
    /// moments later, off the request path.
    /// </summary>
    public static readonly HttpRequestOptionsKey<Guid> AuditedCallIdKey = new("TripsAgent.SupplierApiCallId");
}

/// <summary>How an adapter tags a request for the audit handler.</summary>
public static class HttpRequestMessageSupplierExtensions
{
    /// <summary>Attaches the call's details. Returns the request, for chaining.</summary>
    public static HttpRequestMessage ForSupplierCall(
        this HttpRequestMessage request,
        Guid supplierId,
        SupplierOperation operation,
        SupplierCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierId, Guid.Empty);

        request.Options.Set(SupplierHttpCall.OptionKey, new SupplierHttpCall(supplierId, operation, context));
        return request;
    }

    /// <summary>The id of the audited call, once the request has been sent.</summary>
    public static Guid? AuditedCallId(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(SupplierHttpCall.AuditedCallIdKey, out var id) ? id : null;
    }
}
