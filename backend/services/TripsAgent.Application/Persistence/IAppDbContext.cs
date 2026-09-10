using Microsoft.EntityFrameworkCore;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Application.Persistence;

/// <summary>
/// The database, as use cases see it.
/// </summary>
/// <remarks>
/// <para>
/// Application must not reference Infrastructure — an architecture test fails the build if it
/// does — so handlers depend on this interface and <c>AppDbContext</c> implements it. Everything
/// behind it still runs through the same context, which means the same tenant filters, the same
/// write guard and the same timestamp stamping.
/// </para>
/// <para>
/// Only the sets a use case actually needs are listed. Adding one here is a deliberate act, which
/// is a small nudge towards noticing when a feature starts reaching into tables it should not.
/// </para>
/// </remarks>
public interface IAppDbContext
{
    public DbSet<Agency> Agencies { get; }

    public DbSet<AgencySettings> AgencySettings { get; }

    public DbSet<AgencyBranding> AgencyBranding { get; }

    public DbSet<User> Users { get; }

    public DbSet<Role> Roles { get; }

    public DbSet<UserRole> UserRoles { get; }

    public DbSet<OtpCode> OtpCodes { get; }

    public DbSet<RefreshToken> RefreshTokens { get; }

    /// <summary>The audit trail behind the lockout rule.</summary>
    public DbSet<LoginAttempt> LoginAttempts { get; }

    public DbSet<KybSubmission> KybSubmissions { get; }

    public DbSet<KybDocument> KybDocuments { get; }

    /// <summary>
    /// Not tenant-scoped: an alert records which agency it is <i>about</i>, and Trips staff read
    /// the queue across all of them.
    /// </summary>
    public DbSet<AdminAlert> AdminAlerts { get; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
