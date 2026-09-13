namespace TripsAgent.Application.Storefront;

/// <summary>
/// Takes the row lock on the calling agency's site, held until the surrounding transaction ends.
/// </summary>
/// <remarks>
/// Staging, publishing and rolling back each read the site's versions and then change them. Two at
/// once — a double click, two tabs — must queue rather than interleave, so exactly one of two racing
/// publishes wins. A port because <c>IAppDbContext</c> deliberately runs no raw SQL.
/// </remarks>
public interface ISiteLock
{
    /// <summary>Locks the agency's site and returns its id, or null when the agency has no site.</summary>
    public Task<Guid?> LockCurrentSiteAsync(CancellationToken cancellationToken = default);
}
