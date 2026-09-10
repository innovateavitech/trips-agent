namespace TripsAgent.Application.Identity;

/// <summary>
/// The claim names our access tokens carry.
/// </summary>
/// <remarks>
/// Constants shared by the issuer and by the middleware that reads them back, so the two can
/// never drift. A mismatch here would not fail loudly: the middleware would simply find no
/// agency, leave the tenant unresolved, and every tenant-scoped query would quietly return
/// nothing.
/// </remarks>
public static class TripsClaimTypes
{
    /// <summary>The signed-in user. Standard JWT subject claim.</summary>
    public const string Subject = "sub";

    /// <summary>The agency the caller acts as. Absent for Trips platform staff.</summary>
    public const string AgencyId = "agency_id";

    /// <summary>The principal at the top of that agency's tree.</summary>
    public const string RootAgencyId = "root_agency_id";

    /// <summary>One claim per role name. Also the token's role claim type.</summary>
    public const string Role = "role";

    /// <summary>The user's email address, for display in the console.</summary>
    public const string Email = "email";
}
