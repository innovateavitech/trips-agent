using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Storefront;
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

    /// <summary>
    /// Money given back for an order line — one per line, each with its reason and, for a supplier
    /// reversal, the status poll behind it. Append-only.
    /// </summary>
    public DbSet<Refund> Refunds { get; }

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

    /// <summary>
    /// What one booking on a departure pays, and when: the departure's terms snapshotted on the day
    /// it was booked. One per order line.
    /// </summary>
    public DbSet<BookingPaymentSchedule> BookingPaymentSchedules { get; }

    /// <summary>The payments a schedule is split into. Read by job 11 to send reminders.</summary>
    public DbSet<BookingInstallment> BookingInstallments { get; }

    /// <summary>Every message sent to anyone, and what happened to it. Tenant-scoped.</summary>
    public DbSet<Notification> Notifications { get; }

    /// <summary>The wording, per channel, locale and version. Platform-wide; seeded from the catalog.</summary>
    public DbSet<NotificationTemplate> NotificationTemplates { get; }

    /// <summary>Addresses that bounced permanently. Platform-wide.</summary>
    public DbSet<SuppressedEmailAddress> SuppressedEmailAddresses { get; }

    /// <summary>Starter websites. Platform reference data: every agency reads them, none writes them.</summary>
    public DbSet<SiteTemplate> SiteTemplates { get; }

    /// <summary>Hostname labels refused, or set aside for review (open question 20). Platform reference data.</summary>
    public DbSet<ReservedHostnameLabel> ReservedHostnameLabels { get; }

    /// <summary>Each agency's website. One per agency.</summary>
    public DbSet<Site> Sites { get; }

    /// <summary>The draft and every frozen version of each site.</summary>
    public DbSet<SiteVersion> SiteVersions { get; }

    /// <summary>The draft's pages.</summary>
    public DbSet<SitePage> SitePages { get; }

    /// <summary>The blocks on those pages.</summary>
    public DbSet<SiteBlock> SiteBlocks { get; }

    /// <summary>Each site's typography. The logo and colours are the agency's branding.</summary>
    public DbSet<SiteTheme> SiteThemes { get; }

    /// <summary>
    /// Every hostname a site answers on. Tenant-scoped like everything else; the one read across
    /// agencies — Host header to agency — is <c>HostResolver</c>'s, inside an audited platform scope.
    /// </summary>
    public DbSet<SiteDomain> SiteDomains { get; }

    /// <summary>Every DNS lookup made for a hostname. Append-only.</summary>
    public DbSet<SiteDomainCheck> SiteDomainChecks { get; }

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

    /// <summary>The traveller's "manage my booking" links (build plan F5, decision 21).</summary>
    public DbSet<BookingAccessToken> BookingAccessTokens { get; }

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

    /// <summary>Our record of each booking with a supplier. One per order line, enforced by the database.</summary>
    public DbSet<SupplierBooking> SupplierBookings { get; }

    /// <summary>The travellers on a supplier booking. The lead one's surname identifies it to the supplier.</summary>
    public DbSet<SupplierBookingPassenger> SupplierBookingPassengers { get; }

    /// <summary>
    /// Every status poll and what was done about it: the evidence a payment reversal rests on.
    /// Append-only — a trigger refuses UPDATE and DELETE, even to the owner.
    /// </summary>
    public DbSet<SupplierStatusPoll> SupplierStatusPolls { get; }

    /// <summary>
    /// Each agency's own customers. Personal data: the name, email and phone live here and nowhere
    /// else in the CRM, so erasing a person is one row anonymised in place.
    /// </summary>
    public DbSet<Customer> Customers { get; }

    /// <summary>Inquiries, and where each has got to in the pipeline.</summary>
    public DbSet<Lead> Leads { get; }

    /// <summary>Every move of every lead. Append-only — the grants withhold UPDATE and DELETE.</summary>
    public DbSet<LeadStageChange> LeadStageHistory { get; }

    /// <summary>Quotes. Load one with its items and days to change it: a save replaces them all.</summary>
    public DbSet<Quote> Quotes { get; }

    /// <summary>Follow-up tasks about leads, quotes and customers.</summary>
    public DbSet<FollowUpTask> FollowUpTasks { get; }

    /// <summary>Each customer's timeline of messages and notes. Append-only for the application role.</summary>
    public DbSet<Communication> Communications { get; }

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
