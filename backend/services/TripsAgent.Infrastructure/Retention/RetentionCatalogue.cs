using System.Text.RegularExpressions;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Orders;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.Infrastructure.Retention;

/// <summary>How the retention schedule treats a table.</summary>
public enum RetentionTreatment
{
    /// <summary>
    /// Financial records, the audit log, and what supports them. Kept at least seven years. The purge
    /// job refuses to start if any of its rules names one of these.
    /// </summary>
    Protected,

    /// <summary>Rows the purge job deletes once they are past their window.</summary>
    Purged,

    /// <summary>Rows kept, with their personal columns cleared by the purge job once past their window.</summary>
    Anonymised,

    /// <summary>Whole monthly partitions dropped by a maintenance job once past their window.</summary>
    PartitionDropped,

    /// <summary>
    /// Kept for as long as what they describe exists: configuration, reference data, accounts — or a
    /// small table whose retention has been considered and deliberately left alone for now.
    /// </summary>
    Kept,

    /// <summary>A period is proposed, but nothing enforces it yet. The reason says what it waits for.</summary>
    NotYetEnforced,
}

/// <summary>One table's line in the retention schedule. <c>docs/DATA_RETENTION.md</c> mirrors these.</summary>
/// <param name="Table">Schema-qualified: <c>identity.login_attempts</c>.</param>
/// <param name="PurgedWith">For a table emptied by a cascade rather than a rule of its own: the table whose rule does it.</param>
public sealed record TableRetention(
    string Table,
    RetentionTreatment Treatment,
    string Period,
    string Reason,
    string? PurgedWith = null);

/// <summary>What a rule does to a row in scope.</summary>
public enum RetentionAction
{
    Delete,
    Anonymise,
}

/// <summary>
/// One purge rule: which rows of <see cref="Table"/> are past <see cref="Window"/>.
/// </summary>
/// <param name="Predicate">
/// A SQL condition on the table, aliased <c>t</c>, using the parameter <c>@cutoff</c>. The dry run
/// counts with exactly this condition and the live run deletes with it, so the dry run is a true
/// rehearsal rather than a second query that could disagree with the first.
/// </param>
/// <param name="Assignments">For <see cref="RetentionAction.Anonymise"/>: the <c>SET</c> list.</param>
public sealed record RetentionRule(
    string Table,
    RetentionAction Action,
    TimeSpan Window,
    string Predicate,
    string? Assignments = null)
{
    public string WindowDescription => $"{(int)Window.TotalDays} days";
}

/// <summary>
/// The retention schedule as code: how every table is treated, which tables are protected, and the
/// rules the purge job runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every table is listed.</b> An integration test compares <see cref="Tables"/> with the migrated
/// database and fails when a table is missing, so a table added later cannot quietly go unclassified —
/// whoever adds it has to decide how long it is kept. Another test checks that
/// <c>docs/DATA_RETENTION.md</c> names every table here.
/// </para>
/// <para>
/// <b>Protection is an allowlist, not just a denylist.</b> A rule may only target a table classified
/// <see cref="RetentionTreatment.Purged"/> or <see cref="RetentionTreatment.Anonymised"/>. So a new
/// financial table that nobody remembered to mark protected is still out of reach until someone
/// classifies it on purpose.
/// </para>
/// </remarks>
public static partial class RetentionCatalogue
{
    private const string SevenYears = "At least 7 years. Nothing deletes it today";

    /// <summary>Every table in the database, with its treatment. Order follows docs/DATA_RETENTION.md.</summary>
    public static IReadOnlyList<TableRetention> Tables { get; } =
    [
        // ---------------------------------------------------------------- money
        new("payments.ledger_accounts", RetentionTreatment.Protected, SevenYears,
            "The double-entry ledger's accounts. Every balance is rebuilt from these and their entries."),
        new("payments.ledger_entries", RetentionTreatment.Protected, SevenYears,
            "The ledger itself — the source of truth for every naira. Append-only in the database as well."),
        new("payments.wallets", RetentionTreatment.Protected, SevenYears,
            "Wallet balances, which the nightly audit reconciles against the ledger."),
        new("payments.wallet_holds", RetentionTreatment.Protected, SevenYears,
            "Funds reserved for a booking; each explains a movement on a wallet statement."),
        new("payments.wallet_transactions", RetentionTreatment.Protected, SevenYears,
            "The agent's wallet statement."),
        new("payments.payment_transactions", RetentionTreatment.Protected, SevenYears,
            "Every gateway payment — the evidence behind a top-up or an order payment."),
        new("payments.payment_webhook_events", RetentionTreatment.Protected, SevenYears,
            "What the gateway told us, with its signature check — the evidence in a payment dispute."),
        new("payments.reconciliation_exceptions", RetentionTreatment.Protected, SevenYears,
            "Discrepancies the ledger audit found, and how each was resolved."),
        new("payments.refunds", RetentionTreatment.Protected, SevenYears,
            "Every refund, with the status poll or the agent behind it — the evidence that money went back for a reason. Append-only in the database as well."),

        // ---------------------------------------------------------------- sales
        new("orders.orders", RetentionTreatment.Protected, SevenYears,
            "The sale. A financial and tax record."),
        new("orders.order_lines", RetentionTreatment.Protected, SevenYears,
            "Net, markup and tax frozen at purchase. Every revenue report is built from these."),
        new("orders.order_status_history", RetentionTreatment.Protected, SevenYears,
            "How each order moved and who moved it. Append-only in the database as well."),
        new("orders.order_travellers", RetentionTreatment.Anonymised,
            "Passport number and expiry cleared 90 days after the trip (DataRetention__TravelDocumentDays). The row stays 7 years",
            "Who travelled belongs to the sale; their passport number does not, once the trip is over."),
        new("orders.carts", RetentionTreatment.Purged,
            "30 days after expiry, if never converted to an order (DataRetention__ExpiredCartDays)",
            "A cart that became nothing is not a record of anything. A converted cart is kept with its order."),
        new("orders.cart_items", RetentionTreatment.Purged,
            "With their cart", "Deleted by the cart's ON DELETE CASCADE.",
            PurgedWith: "orders.carts"),

        // ---------------------------------------------------------------- documents and pricing
        new("documents.generated_documents", RetentionTreatment.Protected, SevenYears,
            "Issued invoices and vouchers. Tax law requires them kept."),
        new("documents.document_number_sequences", RetentionTreatment.Protected, SevenYears,
            "Gapless document numbering. A gap is a question from the tax authority."),
        new("documents.document_number_formats", RetentionTreatment.Protected, SevenYears,
            "How each issued number was formatted at the time."),
        new("pricing.price_quotes", RetentionTreatment.Protected, SevenYears,
            "The priced snapshot each order line was placed from."),
        new("pricing.markup_rules", RetentionTreatment.Protected, SevenYears,
            "Explains the markup on every historic order line."),

        // ---------------------------------------------------------------- supplier bookings
        new("supplier.supplier_bookings", RetentionTreatment.Protected, SevenYears,
            "The booking with the airline or operator — the cost side of the order line."),
        new("supplier.supplier_booking_confirmations", RetentionTreatment.Protected, SevenYears,
            "Price confirmations and their hash checks."),
        new("supplier.supplier_booking_passengers", RetentionTreatment.Protected, SevenYears,
            "Who was ticketed. Ticket numbers tie the supplier's invoice to ours. Erasing a person is #106."),
        new("supplier.supplier_status_polls", RetentionTreatment.Protected, SevenYears,
            "The evidence trail behind any payment reversal."),
        new("supplier.passenger_documents", RetentionTreatment.Purged,
            "90 days after the trip ends (DataRetention__TravelDocumentDays). Kept while the trip date is unknown",
            "Passport and visa details are needed to ticket and to fly, not to account for the sale afterwards."),
        new("supplier.supplier_api_calls", RetentionTreatment.PartitionDropped,
            "3 whole months, never less than 90 days (SupplierApiCalls__RetentionMonths)",
            "Full supplier requests and responses: large, and they lose their value fast. Dropped by supplier-api-call-maintenance; reported daily by data-retention."),

        // ---------------------------------------------------------------- supplier search
        new("supplier.search_requests", RetentionTreatment.NotYetEnforced, "Proposed: 30 days",
            "Search history, for the conversion report. Enforcement waits for the search work (#33, #40), which owns these tables and their volume."),
        new("supplier.search_sessions", RetentionTreatment.NotYetEnforced, "Proposed: 30 days",
            "A supplier session is dead within the hour. Waits for the search work."),
        new("supplier.supplier_offers", RetentionTreatment.NotYetEnforced,
            "Proposed: 30 days unless booked; a booked offer is kept with its booking",
            "A booked offer tells us when the trip ends, which the travel-document rule depends on. Waits for the search work."),
        new("supplier.flight_segments", RetentionTreatment.NotYetEnforced, "With their offer",
            "The trip dates. Waits for the search work."),
        new("supplier.bus_segments", RetentionTreatment.NotYetEnforced, "With their offer",
            "The trip dates. Waits for the search work."),
        new("supplier.supplier_fare_rules", RetentionTreatment.NotYetEnforced, "With their offer or booking",
            "The fare rules the traveller was shown. Waits for the search work."),
        new("supplier.suppliers", RetentionTreatment.Kept, "While the supplier is used", "Configuration."),
        new("supplier.supplier_credentials", RetentionTreatment.Kept, "While in use; rotated, not accumulated",
            "Encrypted merchant keys. Configuration."),

        // ---------------------------------------------------------------- identity
        new("identity.users", RetentionTreatment.Kept, "While the account exists",
            "Account holders. Erasing a person is #106, which is blocked on open question 26."),
        new("identity.roles", RetentionTreatment.Kept, "While in use", "Configuration."),
        new("identity.permissions", RetentionTreatment.Kept, "While in use", "Reference data."),
        new("identity.role_permissions", RetentionTreatment.Kept, "While in use", "Configuration."),
        new("identity.user_roles", RetentionTreatment.Kept, "While the account exists", "Who may do what."),
        new("identity.login_attempts", RetentionTreatment.Purged, "90 days (DataRetention__LoginAttemptDays)",
            "Feeds lockout and security investigations. An email and IP address per attempt is personal data with no use after a quarter."),
        new("identity.refresh_tokens", RetentionTreatment.Purged, "30 days after expiry (DataRetention__ExpiredCredentialDays)",
            "Hashes only, but a creation IP each. Kept past expiry long enough to investigate reuse."),
        new("identity.password_reset_tokens", RetentionTreatment.Purged, "30 days after expiry (DataRetention__ExpiredCredentialDays)",
            "A dead link has no use once any question about it is answered."),
        new("identity.otp_codes", RetentionTreatment.Purged, "30 days after expiry (DataRetention__ExpiredCredentialDays)",
            "Each holds the email or phone number it was sent to."),
        new("identity.user_invitations", RetentionTreatment.Purged,
            "90 days after expiry, if never accepted (DataRetention__ExpiredInvitationDays)",
            "An unanswered invitation holds the email of someone who never joined. Accepted ones are kept."),

        // ---------------------------------------------------------------- agencies
        new("tenancy.agencies", RetentionTreatment.Protected, SevenYears,
            "The customer. Every financial record hangs off it."),
        new("tenancy.agency_settings", RetentionTreatment.Kept, "While the agency exists", "Configuration."),
        new("tenancy.agency_branding", RetentionTreatment.Kept, "While the agency exists", "Configuration."),
        new("tenancy.kyb_submissions", RetentionTreatment.Protected, SevenYears,
            "Proof the agency was verified before it was allowed to transact."),
        new("tenancy.kyb_documents", RetentionTreatment.Protected, SevenYears,
            "The documents that verification rested on."),

        // ---------------------------------------------------------------- platform
        new("platform.audit_logs", RetentionTreatment.Protected,
            "84 months (AuditLog__RetentionMonths), then whole partitions dropped by audit-log-maintenance",
            "Who did what, when. This job never touches it; its own maintenance job ages it out."),
        new("platform.admin_alerts", RetentionTreatment.Kept, "Indefinitely, for now",
            "Small, and a resolved alert is the record of how an incident was handled. Revisit if it grows."),
        new("platform.outbox_messages", RetentionTreatment.Purged,
            "30 days after dispatch (DataRetention__ProcessedMessageDays). Failed messages are kept",
            "Delivered events. A failed one waits for a person."),
        new("platform.inbox_messages", RetentionTreatment.Purged,
            "30 days after processing, never under 7 (DataRetention__ProcessedMessageDays)",
            "Deduplication records. Only useful while a broker might still redeliver the message."),
        new("platform.assets", RetentionTreatment.Kept, "While referenced",
            "Uploaded files — logos, KYB documents. Uploads that never arrived are expired by asset-pipeline-sweep."),
        new("platform.asset_variants", RetentionTreatment.Kept, "With their asset", "Resized copies."),

        // ---------------------------------------------------------------- notifications
        new("notifications.notifications", RetentionTreatment.Purged,
            "365 days, once no longer queued or sending (DataRetention__NotificationDays)",
            "Each holds a recipient's address and the rendered content. A year answers \"did they get it?\"."),
        new("notifications.notification_templates", RetentionTreatment.Kept, "While in use", "Configuration."),
        new("notifications.suppressed_email_addresses", RetentionTreatment.Kept, "Indefinitely",
            "An address that bounced or complained has to stay suppressed, or we mail it again."),
    ];

    /// <summary>The tables no retention rule may ever target.</summary>
    public static IReadOnlySet<string> ProtectedTables { get; } = Tables
        .Where(table => table.Treatment == RetentionTreatment.Protected)
        .Select(table => table.Table)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// "The trip ended before the cutoff", for a supplier offer. The last flight arrival, or the last bus
    /// arrival (its departure when the operator gives no arrival).
    /// </summary>
    /// <remarks>
    /// <c>greatest</c> ignores nulls, and is null only when every argument is. So an offer with no segments
    /// — a trip whose dates we do not know — is never "before the cutoff", and its documents are kept.
    /// Unknown means keep, never purge.
    /// </remarks>
    private static string TripEndedBeforeCutoff(string offerId) =>
        $"""
         greatest(
             (SELECT max(fs.arrival_at) FROM supplier.flight_segments fs WHERE fs.supplier_offer_id = {offerId}),
             (SELECT max(coalesce(bs.arrival_at, bs.departure_at)) FROM supplier.bus_segments bs WHERE bs.supplier_offer_id = {offerId})
         ) < @cutoff
         """;

    /// <summary>A passenger document whose booking's trip has ended.</summary>
    private static readonly string PassengerTripEnded =
        $"""
         EXISTS (
             SELECT 1
               FROM supplier.supplier_booking_passengers p
               JOIN supplier.supplier_bookings b ON b.id = p.supplier_booking_id
              WHERE p.id = t.passenger_id
                AND {TripEndedBeforeCutoff("b.supplier_offer_id")})
         """;

    /// <summary>
    /// A traveller still holding document details whose order line's trip has ended. The first clause is
    /// what makes a second run find nothing: a cleared row no longer matches.
    /// </summary>
    private static readonly string TravellerDocumentsPastTrip =
        $"""
         (t.passport_number_encrypted IS NOT NULL OR t.passport_expiry IS NOT NULL)
         AND EXISTS (
             SELECT 1
               FROM orders.order_lines l
               LEFT JOIN supplier.supplier_bookings b ON b.id = l.supplier_booking_id
              WHERE l.id = t.order_line_id
                AND {TripEndedBeforeCutoff("coalesce(b.supplier_offer_id, l.supplier_offer_id)")})
         """;

    /// <summary>The rules the purge job runs, with the windows from <paramref name="options"/>.</summary>
    /// <remarks>
    /// Refresh tokens are safe to delete in one statement despite the RESTRICT foreign key from a used
    /// token to its replacement: a replacement is issued later with the same fixed lifetime, so it
    /// always expires after the token it replaced. Whenever a replacement is past the cutoff, so is
    /// everything that points at it — and the whole chain goes in the same statement.
    /// </remarks>
    public static IReadOnlyList<RetentionRule> Rules(DataRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            new("identity.login_attempts", RetentionAction.Delete, Days(options.LoginAttemptDays),
                "t.attempted_at < @cutoff"),
            new("identity.refresh_tokens", RetentionAction.Delete, Days(options.ExpiredCredentialDays),
                "t.expires_at < @cutoff"),
            new("identity.password_reset_tokens", RetentionAction.Delete, Days(options.ExpiredCredentialDays),
                "t.expires_at < @cutoff"),
            new("identity.otp_codes", RetentionAction.Delete, Days(options.ExpiredCredentialDays),
                "t.expires_at < @cutoff"),
            new("identity.user_invitations", RetentionAction.Delete, Days(options.ExpiredInvitationDays),
                "t.accepted_at IS NULL AND t.expires_at < @cutoff"),
            new("platform.outbox_messages", RetentionAction.Delete, Days(options.ProcessedMessageDays),
                $"t.status = '{OutboxMessageStatus.Dispatched}' AND t.dispatched_at < @cutoff"),
            new("platform.inbox_messages", RetentionAction.Delete, Days(options.ProcessedMessageDays),
                "t.processed_at < @cutoff"),
            new("notifications.notifications", RetentionAction.Delete, Days(options.NotificationDays),
                $"t.status NOT IN ('{NotificationStatus.Queued}', '{NotificationStatus.Sending}') AND t.created_at < @cutoff"),
            new("orders.carts", RetentionAction.Delete, Days(options.ExpiredCartDays),
                $"t.converted_order_id IS NULL AND t.status <> '{nameof(CartStatus.Converted)}' AND t.expires_at < @cutoff"),
            new("supplier.passenger_documents", RetentionAction.Delete, Days(options.TravelDocumentDays),
                PassengerTripEnded),
            new("orders.order_travellers", RetentionAction.Anonymise, Days(options.TravelDocumentDays),
                TravellerDocumentsPastTrip,
                Assignments: "passport_number_encrypted = NULL, passport_expiry = NULL, updated_at = now()"),
        ];
    }

    /// <summary>
    /// Throws unless every rule targets a table the catalogue says may be purged or anonymised — and
    /// none targets a protected one.
    /// </summary>
    public static void EnsureAllowed(IEnumerable<RetentionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            if (rule is null || !QualifiedTableName().IsMatch(rule.Table))
            {
                throw new InvalidOperationException(
                    $"Refusing to run: '{rule?.Table}' is not a lower-case, schema-qualified table name, so it "
                    + "cannot be checked against the protected list.");
            }

            if (ProtectedTables.Contains(rule.Table))
            {
                throw new InvalidOperationException(
                    $"Refusing to run: a retention rule targets {rule.Table}, which holds financial or audit "
                    + "records, or records that support them. Those are kept for at least seven years and this "
                    + "job never deletes or changes them. See docs/DATA_RETENTION.md.");
            }

            var expected = rule.Action == RetentionAction.Delete ? RetentionTreatment.Purged : RetentionTreatment.Anonymised;
            var entry = Tables.FirstOrDefault(table => table.Table == rule.Table);

            if (entry is null || entry.Treatment != expected)
            {
                throw new InvalidOperationException(
                    $"Refusing to run: a retention rule would {rule.Action.ToString().ToLowerInvariant()} rows of "
                    + $"{rule.Table}, which the retention catalogue does not classify as {expected}. Classify it in "
                    + "RetentionCatalogue.Tables and docs/DATA_RETENTION.md first, deliberately.");
            }

            if (rule.Action == RetentionAction.Anonymise && string.IsNullOrWhiteSpace(rule.Assignments))
            {
                throw new InvalidOperationException(
                    $"Refusing to run: the anonymise rule for {rule.Table} does not say which columns to clear.");
            }
        }
    }

    private static TimeSpan Days(int days) => TimeSpan.FromDays(days);

    [GeneratedRegex("^[a-z_]+\\.[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedTableName();
}
