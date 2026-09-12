using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
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

    public DbSet<Permission> Permissions { get; }

    public DbSet<RolePermission> RolePermissions { get; }

    public DbSet<OtpCode> OtpCodes { get; }

    public DbSet<RefreshToken> RefreshTokens { get; }

    public DbSet<PasswordResetToken> PasswordResetTokens { get; }

    /// <summary>The audit trail behind the lockout rule.</summary>
    public DbSet<LoginAttempt> LoginAttempts { get; }

    public DbSet<KybSubmission> KybSubmissions { get; }

    public DbSet<KybDocument> KybDocuments { get; }

    /// <summary>
    /// Not tenant-scoped: an alert records which agency it is <i>about</i>, and Trips staff read
    /// the queue across all of them.
    /// </summary>
    public DbSet<AdminAlert> AdminAlerts { get; }

    public DbSet<LedgerAccount> LedgerAccounts { get; }

    public DbSet<LedgerEntry> LedgerEntries { get; }

    public DbSet<Wallet> Wallets { get; }

    public DbSet<WalletHold> WalletHolds { get; }

    public DbSet<WalletTransaction> WalletTransactions { get; }

    /// <summary>Every attempt to take money, successful or not.</summary>
    public DbSet<PaymentTransaction> PaymentTransactions { get; }

    /// <summary>Gateway deliveries, recorded so each is processed exactly once.</summary>
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents { get; }

    /// <summary>
    /// Discrepancies the nightly integrity audit found.
    /// </summary>
    /// <remarks>
    /// Not tenant-scoped, and unusually so for a payments table. An unbalanced transaction group
    /// spans accounts that may belong to different agencies and to the platform, so there is no
    /// one agency it belongs to; and it is read by platform admins investigating the platform's
    /// own books, which an agency must never see. Permission controls access, not a filter.
    /// </remarks>
    public DbSet<ReconciliationException> ReconciliationExceptions { get; }

    /// <summary>Uploaded files, from the moment a slot is reserved to the moment they are servable.</summary>
    public DbSet<Asset> Assets { get; }

    /// <summary>The WebP renditions of image assets.</summary>
    public DbSet<AssetVariant> AssetVariants { get; }

    /// <summary>
    /// Each agency's own tours, packages and visas. Load one with its children to change it: a save
    /// replaces the whole product, and the rows it is made of are reached through it.
    /// </summary>
    public DbSet<Product> Products { get; }

    /// <summary>Each agency's own categories and themes.</summary>
    public DbSet<ProductCategory> ProductCategories { get; }

    /// <summary>
    /// Dated runs of a tour or package, sold by the seat. Load one with its tiers and its
    /// installment plan to change it: a save replaces the whole departure.
    /// </summary>
    public DbSet<Departure> Departures { get; }

    /// <summary>Seats held for a cart during checkout. Released by job 6 when they time out.</summary>
    public DbSet<DepartureHold> DepartureHolds { get; }

    /// <summary>Who is waiting for a seat on a full departure, and in what order.</summary>
    public DbSet<DepartureWaitlistEntry> DepartureWaitlist { get; }

    /// <summary>Who is on a departure, and which room they are in.</summary>
    public DbSet<PaxManifestEntry> PaxManifests { get; }

    /// <summary>Every message sent to anyone, and what happened to it. Tenant-scoped.</summary>
    public DbSet<Notification> Notifications { get; }

    /// <summary>The wording, per channel, locale and version. Platform-wide; seeded from the catalog.</summary>
    public DbSet<NotificationTemplate> NotificationTemplates { get; }

    /// <summary>Addresses that bounced permanently. Platform-wide.</summary>
    public DbSet<SuppressedEmailAddress> SuppressedEmailAddresses { get; }

    /// <summary>
    /// Each agency's markup rules. Never edited in place — see <see cref="MarkupRule"/> — so the rule
    /// id stored on a quote always explains the markup on it.
    /// </summary>
    public DbSet<MarkupRule> MarkupRules { get; }

    /// <summary>Prices as they were worked out, each naming the rule that decided its markup.</summary>
    public DbSet<PriceQuote> PriceQuotes { get; }

    /// <summary>What each agency sold, with the totals as they were at the moment of sale.</summary>
    public DbSet<Order> Orders { get; }

    /// <summary>The things bought, each carrying the price it was bought at.</summary>
    public DbSet<OrderLine> OrderLines { get; }

    /// <summary>Who travels on each line. Passport numbers are ciphertext.</summary>
    public DbSet<OrderTraveller> OrderTravellers { get; }

    /// <summary>Every status an order has had. Append-only — the grants withhold UPDATE.</summary>
    public DbSet<OrderStatusHistory> OrderStatusHistory { get; }

    /// <summary>Open carts. The prices in them are indicative, not frozen.</summary>
    public DbSet<Cart> Carts { get; }

    /// <summary>What is in those carts.</summary>
    public DbSet<CartItem> CartItems { get; }

    /// <summary>The aggregators we buy from. Platform reference data: not tenant-scoped.</summary>
    public DbSet<Supplier> Suppliers { get; }

    /// <summary>Every search an agency ran. The criteria hash is the search cache key (#40).</summary>
    public DbSet<SearchRequest> SearchRequests { get; }

    /// <summary>The supplier's session for a search, which confirmation must quote back.</summary>
    public DbSet<SearchSession> SearchSessions { get; }

    /// <summary>What a search found, at the net rate. Markup is applied when read, never stored here.</summary>
    public DbSet<SupplierOffer> SupplierOffers { get; }

    public DbSet<FlightSegment> FlightSegments { get; }

    public DbSet<BusSegment> BusSegments { get; }

    /// <summary>
    /// What this context is about to write.
    /// </summary>
    /// <remarks>
    /// Exposed for one job: throwing away a save that failed. When a concurrency check or a unique
    /// index refuses a save, the rejected changes stay tracked, and the next
    /// <see cref="SaveChangesAsync"/> on the same context sends them again — and fails again.
    /// <c>ChangeTracker.Clear()</c> discards them so the caller can re-read and decide afresh.
    /// </remarks>
    public ChangeTracker ChangeTracker { get; }

    /// <summary>How each document type's numbers are written, where the agency has chosen.</summary>
    public DbSet<DocumentNumberFormat> DocumentNumberFormats { get; }

    /// <summary>Issued documents, and the gapless numbers they own.</summary>
    public DbSet<GeneratedDocument> GeneratedDocuments { get; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
