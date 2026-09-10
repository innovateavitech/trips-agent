namespace TripsAgent.Application.Tenancy;

/// <summary>
/// The scoped, per-request implementation of <see cref="ITenantContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as scoped, so one instance serves one request and nothing leaks between them.
/// Middleware calls <see cref="SetTenant"/> once, early; everything downstream only reads.
/// </para>
/// <para>
/// <see cref="SetTenant"/> refuses to run twice. Re-assigning the tenant mid-request would mean
/// entities already loaded under one agency are saved under another, which is the worst kind of
/// bug this class exists to prevent — so it fails loudly instead.
/// </para>
/// </remarks>
public sealed class TenantContext : ITenantContext
{
    public Guid? AgencyId { get; private set; }

    public Guid? RootAgencyId { get; private set; }

    public Guid? UserId { get; private set; }

    /// <summary>True when an agency has been resolved for this request.</summary>
    public bool HasTenant => AgencyId.HasValue;

    /// <summary>Populates the context for this request. May be called at most once.</summary>
    /// <param name="agencyId">The agency the caller belongs to.</param>
    /// <param name="rootAgencyId">
    /// The principal at the top of that agency's tree. Defaults to <paramref name="agencyId"/>,
    /// which is correct for a principal.
    /// </param>
    /// <param name="userId">The signed-in user, if there is one.</param>
    public void SetTenant(Guid agencyId, Guid? rootAgencyId = null, Guid? userId = null)
    {
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty agency id would match no rows and silently return empty results. "
                + "Leave the tenant unresolved instead.",
                nameof(agencyId));
        }

        if (AgencyId.HasValue)
        {
            throw new InvalidOperationException(
                $"""
                 The tenant for this request is already set to {AgencyId}.

                 Re-assigning it mid-request would let entities loaded under one agency be saved
                 under another. If you need to read across agencies, use IPlatformScope.
                 """);
        }

        AgencyId = agencyId;
        RootAgencyId = rootAgencyId ?? agencyId;
        UserId = userId;
    }

    /// <summary>Records the signed-in user for a request that has no agency — a platform admin.</summary>
    public void SetPlatformUser(Guid userId) => UserId = userId;
}
