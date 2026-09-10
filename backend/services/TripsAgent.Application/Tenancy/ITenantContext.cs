namespace TripsAgent.Application.Tenancy;

/// <summary>
/// Who is asking, and which agency's data they are entitled to see. Scoped to one request.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for tenant scoping. EF Core's global query filters read
/// it on every query and the save interceptor reads it on every insert, so no handler ever
/// writes <c>WHERE agency_id = …</c> by hand — and cannot forget to.
/// </para>
/// <para>
/// It is populated by middleware from the caller's token. When nothing populates it,
/// <see cref="HasTenant"/> is false and tenant-scoped queries return <b>nothing</b>. Returning
/// nothing is the only safe default: the alternative, returning everything, is precisely the
/// cross-agency leak the filter exists to prevent.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>The agency whose data this request may touch. Null when unresolved.</summary>
    public Guid? AgencyId { get; }

    /// <summary>
    /// The principal at the top of this agency's tree — the same as <see cref="AgencyId"/> for a
    /// principal, and the managing principal for a sub-agent. Used for network-wide reporting.
    /// </summary>
    public Guid? RootAgencyId { get; }

    /// <summary>The signed-in user. Null for anonymous storefront traffic.</summary>
    public Guid? UserId { get; }

    /// <summary>True when an agency has been resolved for this request.</summary>
    public bool HasTenant => AgencyId.HasValue;
}
